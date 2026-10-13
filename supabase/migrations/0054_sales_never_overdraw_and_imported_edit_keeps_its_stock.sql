    -- ---------------------------------------------------------------------------
    -- 0054. A sale never overdraws its bucket, and correcting an imported sale no
    -- longer hands its carats back.
    --
    --
    -- 1 . POSTING STILL LET A SALE TAKE A BUCKET NEGATIVE
    --
    -- 0053 stopped the sales-entry HOLD overdrawing a bucket, but left post_invoice
    -- on negative_stock_policy(): under 'allow' it posted whatever was typed, and
    -- under 'warn' a caller passing p_override did the same. The desk's rule is
    -- that stock never goes negative from a sale, so it now refuses a shortfall
    -- whatever the policy says.
    --
    -- Same check, same place, same text: the shortfall is still raised as
    -- "Posting would take stock negative: [...]", which is what Repo.RaisedShortfalls
    -- already lifts into the "Not enough stock" box, naming each bucket with what it
    -- needs and what it has. PER BUCKET as it always was -- (grade_id, size_id) --
    -- so stock in another grade or sieve can never cover a shortfall.
    --
    --
    -- 2 . "UPDATE SALE" ON AN IMPORTED (MIG-) INVOICE GAVE ITS STOCK BACK
    --
    -- 0042 was written when an imported invoice carried no movements: it moved the
    -- DIFFERENCE between old and new lines as an import_edit ADJUST. 0049 changed
    -- the premise -- every imported line now writes SALE/REJECTION tagged
    -- 'sales_line' -- and edit_posted_invoice did not follow. Its step 1 deletes
    -- those movements for EVERY invoice, imported ones included, and then for an
    -- imported invoice writes only the difference. So one update:
    --
    --   * returned every carat the invoice had taken, in every bucket on it, and
    --   * then applied the difference on top -- a removed line's carats came back
    --     twice, and an unchanged line's came back once.
    --
    -- The balance read high by the whole invoice, and v_reconciliation (0052,
    -- which counts imported SALE movements) reported the invoice as unsold.
    --
    -- Now an imported invoice is rewritten exactly as the import writes it: its
    -- movements deleted, then written again from the corrected lines, CAPPED at
    -- what each bucket holds -- 0051's rule, so an imported sale still never drives
    -- stock negative and a capped line stays capped. Any import_edit adjustment an
    -- earlier update left on the invoice is removed in the same transaction, or it
    -- would count a second time on top of the rewritten movements.
    --
    -- An ordinary invoice is unchanged except for the guard: a correction the
    -- bucket cannot cover is refused whatever the policy, naming both figures.
    --
    -- Existing data: an imported invoice already updated under 0042 holds an
    -- import_edit ADJUST and no SALE movements. The next update of it, or the next
    -- sales re-import, rewrites it correctly. DIAGNOSE_STOCK_DRIFT.sql lists them.
    --
    -- SUPERSEDES 0019's post_invoice and 0042's edit_posted_invoice. Grants are kept
    -- by create or replace. Writes no movement when applied. Safe to run repeatedly.
    -- ---------------------------------------------------------------------------

    begin;

    create or replace function public.post_invoice(
        p_invoice_id bigint,
        p_override   boolean default false
    )
    returns jsonb
    language plpgsql
    as $$
    declare
        v_status text;
        v_date   date;
        v_short  jsonb;
        v_no     varchar(20);
    begin
        select status, invoice_date into v_status, v_date
        from public.sales_invoice
        where invoice_id = p_invoice_id;

        if not found then
            raise exception 'Invoice % not found', p_invoice_id using errcode = 'no_data_found';
        end if;

        if v_status = 'POSTED' then
            return jsonb_build_object('ok', true, 'already_posted', true);
        end if;

        if v_status = 'CANCELLED' then
            raise exception 'Invoice % is cancelled and cannot be posted', p_invoice_id;
        end if;

        if not exists (select 1 from public.sales_line where invoice_id = p_invoice_id) then
            raise exception 'Invoice % has no lines', p_invoice_id;
        end if;

        with need as (
            select grade_id, size_id, sum(gross_weight_ct) as out_ct
            from public.sales_line
            where invoice_id = p_invoice_id
            group by grade_id, size_id
        )
        select jsonb_agg(jsonb_build_object(
                'grade_code', sp.grade_code,
                'size_code',  sp.size_code,
                'balance_ct', greatest(sp.balance_ct, 0),
                'needed_ct',  n.out_ct))
        into v_short
        from need n
        join public.v_stock_position sp
            on sp.grade_id = n.grade_id and sp.size_id = n.size_id
        where sp.balance_ct < n.out_ct;

        -- 0054. Whatever the policy, and whatever p_override says. See the header.
        if v_short is not null then
            raise exception 'Posting would take stock negative: %', v_short::text
                using errcode = 'check_violation';
        end if;

        update public.sales_line l
        set cost_per_ct = nullif(sp.avg_cost, 0),
            cost_basis  = case when nullif(sp.avg_cost, 0) is null
                                then null else 'moving_average' end
        from public.v_stock_position sp
        where l.invoice_id = p_invoice_id
        and sp.grade_id  = l.grade_id
        and sp.size_id   = l.size_id;

        insert into public.stock_movement
            (movement_date, grade_id, size_id, movement_type, weight_ct,
            price_per_ct, ref_type, ref_id, created_by)
        select v_date, l.grade_id, l.size_id, 'SALE', l.selection_ct,
            l.price_per_ct, 'sales_line', l.line_id, auth.uid()
        from public.sales_line l
        where l.invoice_id = p_invoice_id and l.selection_ct > 0;

        insert into public.stock_movement
            (movement_date, grade_id, size_id, movement_type, weight_ct,
            price_per_ct, ref_type, ref_id, created_by)
        select v_date, l.grade_id, l.size_id, 'REJECTION', l.rejection_ct,
            l.price_per_ct, 'sales_line', l.line_id, auth.uid()
        from public.sales_line l
        where l.invoice_id = p_invoice_id and l.rejection_ct > 0;

        update public.sales_invoice
        set status     = 'POSTED',
            invoice_no = coalesce(invoice_no, public.next_invoice_no(v_date)),
            updated_by = auth.uid()
        where invoice_id = p_invoice_id;

        select invoice_no into v_no from public.sales_invoice where invoice_id = p_invoice_id;
        return jsonb_build_object('ok', true, 'invoice_no', v_no);
    end;
    $$;

    comment on function public.post_invoice(bigint, boolean) is
        '0054. Posts a draft: per grade x size bucket, refuses when the bucket cannot cover the invoice (whatever negative_stock_policy says, and p_override is ignored), then stamps cost, writes SALE/REJECTION and assigns the number. Supersedes 0019.';


    create or replace function public.edit_posted_invoice(
        p_invoice_id   bigint,
        p_invoice_date date,
        p_buyer_id     bigint,
        p_broker_id    bigint,
        p_broker_pct   numeric,
        p_terms_days   integer,
        p_doc_type     text,
        p_currency_id  bigint,
        p_lines        jsonb,
        p_reason       text
    )
    returns jsonb
    language plpgsql
    security definer
    set search_path = public
    as $$
    declare
        v_status   text;
        v_no       text;
        v_short    text;
        v_reason   text := btrim(coalesce(p_reason, ''));
        v_note     text;
        v_imported boolean;
    begin
        select status, invoice_no
        into v_status, v_no
        from public.sales_invoice
        where invoice_id = p_invoice_id
        for update;

        if not found then
            raise exception 'Invoice % not found', p_invoice_id using errcode = 'no_data_found';
        end if;

        if v_status = 'DRAFT' then
            raise exception 'Invoice % is a draft; save it the ordinary way', p_invoice_id;
        end if;

        if v_status = 'CANCELLED' then
            raise exception 'Invoice % is cancelled. Its stock has already been returned, so there is nothing to correct.', p_invoice_id;
        end if;

        if v_status <> 'POSTED' then
            raise exception 'Invoice % is %, which cannot be edited', p_invoice_id, v_status;
        end if;

        v_imported := coalesce(v_no, '') like 'MIG-%';

        if v_reason = '' then
            raise exception 'Correcting a posted invoice requires a reason';
        end if;

        if p_lines is null or jsonb_typeof(p_lines) <> 'array' or jsonb_array_length(p_lines) = 0 then
            raise exception 'An invoice must keep at least one line. To remove it entirely, cancel it.';
        end if;

        v_note := 'Edit of invoice ' || coalesce(v_no, p_invoice_id::text) || ': ' || v_reason;

        -- ── 1 · give the stock back ────────────────────────────────────────────
        -- Every invoice, imported or not: since 0049 both carry SALE/REJECTION tagged sales_line.
        delete from public.stock_movement m
        using public.sales_line l
        where m.ref_type = 'sales_line'
        and m.ref_id = l.line_id
        and l.invoice_id = p_invoice_id
        and m.movement_type in ('SALE', 'REJECTION');

        -- 0054. And whatever an update under 0042 left behind, or it counts again on top of step 6.
        perform public.clear_import_edits(array[p_invoice_id]);

        -- ── 2 · the old lines go with them ─────────────────────────────────────
        delete from public.sales_line where invoice_id = p_invoice_id;

        -- ── 3 · the corrected lines ────────────────────────────────────────────
        insert into public.sales_line
            (invoice_id, grade_id, size_id, gross_weight_ct, selection_ct,
            price_per_ct, ex_rate, less1_pct, less2_pct, remark)
        select p_invoice_id,
            (r->>'grade_id')::bigint,
            (r->>'size_id')::bigint,
            (r->>'gross_weight_ct')::numeric,
            (r->>'selection_ct')::numeric,
            (r->>'price_per_ct')::numeric,
            coalesce(nullif((r->>'ex_rate')::numeric, 0), 1),
            coalesce((r->>'less1_pct')::numeric, 0),
            coalesce((r->>'less2_pct')::numeric, 0),
            nullif(btrim(coalesce(r->>'remark', '')), '')
        from jsonb_array_elements(p_lines) as r;

        -- ── 4 · an ordinary invoice: the bucket covers it, or nothing changes ──
        -- Refused whatever the policy (0054), per bucket, both figures named. The stock is back at
        -- its pre-invoice level by now, so this is the whole of what the invoice now says left it.
        -- An imported invoice is not refused here: it is capped in step 6, as the import caps it.
        if not v_imported then
            select string_agg(format('Insufficient stock for %s x %s: required %s ct, available %s ct.',
                                    sp.grade_code, sp.size_code,
                                    trim_scale(round(n.out_ct, 4)),
                                    trim_scale(round(greatest(sp.balance_ct, 0), 4))), ' ')
            into v_short
            from (select grade_id, size_id, sum(gross_weight_ct) as out_ct
                    from public.sales_line
                    where invoice_id = p_invoice_id
                    group by grade_id, size_id) n
            join public.v_stock_position sp
                on sp.grade_id = n.grade_id and sp.size_id = n.size_id
            where sp.balance_ct < n.out_ct;

            if v_short is not null then
                raise exception '%', v_short using errcode = 'check_violation';
            end if;
        end if;

        -- ── 5 · the cost stamp, as posting does it (0019); imported lines stay uncosted ──
        update public.sales_line l
        set cost_per_ct = nullif(sp.avg_cost, 0),
            cost_basis  = case when nullif(sp.avg_cost, 0) is null
                                then null else 'moving_average' end
        from public.v_stock_position sp
        where l.invoice_id = p_invoice_id
        and not v_imported
        and sp.grade_id  = l.grade_id
        and sp.size_id   = l.size_id;

        -- ── 6 · and the stock leaves again ─────────────────────────────────────
        -- An ordinary invoice in full (step 4 proved it fits). An imported one up to what each bucket
        -- holds, lines in order, sale before rejection -- 0051's rule, scoped to this invoice.
        with wanted as (
            select l.line_id, l.grade_id, l.size_id, l.price_per_ct,
                coalesce(l.selection_ct, 0) as selection_ct,
                coalesce(l.rejection_ct, 0) as rejection_ct,
                coalesce(l.selection_ct, 0) + coalesce(l.rejection_ct, 0) as gross_ct
            from public.sales_line l
            where l.invoice_id = p_invoice_id
        ),
        capped as (
            select w.*,
                case when not v_imported then w.gross_ct
                        else greatest(0, least(w.gross_ct,
                                greatest(coalesce(p.balance_ct, 0), 0)
                                - coalesce(sum(w.gross_ct) over (partition by w.grade_id, w.size_id
                                                                order by w.line_id
                                                                rows between unbounded preceding and 1 preceding), 0)))
                end as allow_ct
            from wanted w
            left join public.v_stock_position p
                    on p.grade_id = w.grade_id and p.size_id = w.size_id
        ),
        split as (
            select c.*,
                least(c.selection_ct, c.allow_ct) as sale_out,
                least(c.rejection_ct, c.allow_ct - least(c.selection_ct, c.allow_ct)) as rej_out
            from capped c
        )
        insert into public.stock_movement
            (movement_date, grade_id, size_id, movement_type, weight_ct,
            price_per_ct, ref_type, ref_id, created_by, reason)
        select p_invoice_date, s.grade_id, s.size_id, 'SALE', s.sale_out,
            s.price_per_ct, 'sales_line', s.line_id, auth.uid(), v_note
        from split s where s.sale_out > 0
        union all
        select p_invoice_date, s.grade_id, s.size_id, 'REJECTION', s.rej_out,
            s.price_per_ct, 'sales_line', s.line_id, auth.uid(), v_note
        from split s where s.rej_out > 0;

        -- ── 7 · the header, keeping the number and the status ──────────────────
        update public.sales_invoice
        set invoice_date = p_invoice_date,
            buyer_id     = p_buyer_id,
            broker_id    = p_broker_id,
            broker_pct   = p_broker_pct,
            terms_days   = p_terms_days,
            doc_type     = p_doc_type,
            currency_id  = p_currency_id,
            updated_by   = auth.uid()
        where invoice_id = p_invoice_id;

        return jsonb_build_object('ok', true, 'invoice_no', v_no, 'reason', v_reason);
    end;
    $$;

    comment on function public.edit_posted_invoice is
        '0054. Corrects a POSTED invoice in one transaction: deletes its SALE/REJECTION movements (and any 0042 import_edit adjustment), replaces its lines and writes the movements again. An ordinary invoice is refused, per bucket and whatever the policy, when the bucket cannot cover it. An imported MIG- invoice is capped at what each bucket holds, as replace_imported_sales (0051) caps it. Supersedes 0042, which returned an imported invoice''s carats on every update once 0049 gave those invoices movements.';

    commit;


    -- ---------------------------------------------------------------------------
    -- Verification. Every row must read 'ok'.
    -- ---------------------------------------------------------------------------
    select 'posting refuses a shortfall whatever the policy' as check,
        case when pg_get_functiondef('public.post_invoice'::regproc) not like '%negative_stock_policy%'
                and pg_get_functiondef('public.post_invoice'::regproc) like '%Posting would take stock negative%'
                then 'ok' else 'NOT APPLIED' end as status
    union all
    select 'an update no longer moves an imported invoice by difference',
        case when pg_get_functiondef('public.edit_posted_invoice'::regproc) not like '%imported_edit_delta%'
                and pg_get_functiondef('public.edit_posted_invoice'::regproc) like '%clear_import_edits%'
                then 'ok' else 'NOT APPLIED' end
    union all
    select 'the import cap is still 0051''s',
        case when pg_get_functiondef('public.replace_imported_sales'::regproc) like '%short_buckets%'
                then 'ok' else '0051 IS MISSING' end;
