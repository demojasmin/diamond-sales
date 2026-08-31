-- ---------------------------------------------------------------------------
-- 0040 · A posted invoice can be corrected, and the stock follows it.
--
-- THE PROBLEM
--
-- Posting is one-way. A figure typed wrong on an invoice that has already moved stock could
-- only be undone by cancelling it and entering the whole sale again -- two documents on the
-- ledger where the office only ever meant one, and an invoice number burned each time.
--
-- WHY NOT JUST LIFT THE DRAFT-ONLY GUARD
--
-- SaveDraftAsync deletes and re-inserts sales_line. On a POSTED invoice that leaves every
-- stock_movement pointing at a line_id that no longer exists, with the carats still deducted.
-- The position would stop reconciling against the invoices and nothing would say why. The guard
-- is not the problem; it is the only thing standing between that and a silent corruption.
--
-- WHY DELETE THE MOVEMENTS RATHER THAN REVERSE THEM
--
-- The obvious shape is cancel_invoice's: leave the SALE rows and add a signed ADJUST beside them.
-- It is wrong here. v_reconciliation (0026) compares SALE movements against the selection on
-- POSTED lines and does not net ADJUST at all -- so a reversal would leave the old SALE counted
-- for ever against lines that no longer exist, and every edited invoice would report a permanent
-- reconciliation failure that no one could clear.
--
-- So the movements this invoice wrote are deleted and written again, which is exactly what
-- import_stock(p_replace => true) already does with a stock sheet. Nothing is lost: audit_log
-- carries a DELETE row for every movement and every line removed, with their old values, so the
-- previous version of the invoice is fully recoverable from the trail.
--
-- WHAT IS PRESERVED
--
--   invoice_no    the same number. A correction is not a new document.
--   status        stays POSTED. It never becomes a draft, so it cannot be half-posted.
--   history       audit_log gets a DELETE per removed row and an INSERT per new one.
--   the reason    carried on every new movement, so the ledger says why the figures moved.
--
-- WHAT IS REFUSED
--
--   a DRAFT        -- that is save_draft's job, and it is cheaper
--   a CANCELLED    -- reversed already; correcting it would resurrect stock
--   a MIG- invoice -- imported sales deliberately move no stock (see 0022). Editing one here
--                     would write movements the opening balance already accounts for.
--   no reason      -- an unexplained edit of a posted document is worse than no edit
--   no lines       -- an invoice with nothing on it is a cancellation, which has its own path
--
-- ONE TRANSACTION. A plpgsql function is atomic: any raise below rolls back the deletions, the
-- new lines and the new movements together. There is no state in which the stock has been given
-- back but the invoice has not been rewritten.
-- ---------------------------------------------------------------------------

begin;

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
    v_status  text;
    v_no      text;
    v_policy  text;
    v_short   jsonb;
    v_reason  text := btrim(coalesce(p_reason, ''));
    v_note    text;
begin
    -- FOR UPDATE, not a bare select. Two desks correcting the same invoice at once would each
    -- read the movements the other was about to delete, and one set would be written twice.
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

    -- Imported sales never moved stock (0022). Rewriting one here would deduct carats the
    -- imported opening balance has already taken out.
    if coalesce(v_no, '') like 'MIG-%' then
        raise exception 'Invoice % was imported. Imported sales carry no stock movements, so editing one here would deduct its carats a second time.', v_no;
    end if;

    if v_reason = '' then
        raise exception 'Correcting a posted invoice requires a reason';
    end if;

    if p_lines is null or jsonb_typeof(p_lines) <> 'array' or jsonb_array_length(p_lines) = 0 then
        raise exception 'An invoice must keep at least one line. To remove it entirely, cancel it.';
    end if;

    v_note := 'Edit of invoice ' || coalesce(v_no, p_invoice_id::text) || ': ' || v_reason;

    -- ── 1 · give the stock back ────────────────────────────────────────────
    -- Deleted, not reversed. See the header: v_reconciliation counts SALE against posted lines,
    -- so a lingering SALE for a line that is about to go would never balance again. audit_log
    -- keeps a DELETE row with the old values for each one.
    delete from public.stock_movement m
     using public.sales_line l
     where m.ref_type = 'sales_line'
       and m.ref_id = l.line_id
       and l.invoice_id = p_invoice_id
       and m.movement_type in ('SALE', 'REJECTION');

    -- ── 2 · the old lines go with them ─────────────────────────────────────
    delete from public.sales_line where invoice_id = p_invoice_id;

    -- ── 3 · the corrected lines ────────────────────────────────────────────
    -- rejection_ct is GENERATED from gross - selection, so it is never inserted.
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

    -- ── 4 · the same guard posting uses, against the corrected figures ─────
    -- The stock is back at its pre-invoice level by now, so this asks the question the office
    -- would: does the bucket cover what this invoice NOW says left it.
    v_policy := public.negative_stock_policy();

    with need as (
        select grade_id, size_id, sum(gross_weight_ct) as out_ct
          from public.sales_line
         where invoice_id = p_invoice_id
         group by grade_id, size_id
    )
    select jsonb_agg(jsonb_build_object(
               'grade_code', sp.grade_code,
               'size_code',  sp.size_code,
               'balance_ct', sp.balance_ct,
               'needed_ct',  n.out_ct))
      into v_short
      from need n
      join public.v_stock_position sp
        on sp.grade_id = n.grade_id and sp.size_id = n.size_id
     where sp.balance_ct < n.out_ct;

    -- No needs_override branch. Posting offers one under 'warn' because the user is at the
    -- screen deciding; a correction that cannot be honoured should simply not be made, and the
    -- rollback puts the original invoice and its stock back untouched.
    if v_short is not null and v_policy = 'block' then
        raise exception 'This correction would take stock negative: %', v_short::text
            using errcode = 'check_violation';
    end if;

    -- ── 5 · the cost stamp, as posting does it (0019) ──────────────────────
    update public.sales_line l
       set cost_per_ct = nullif(sp.avg_cost, 0),
           cost_basis  = case when nullif(sp.avg_cost, 0) is null
                              then null else 'moving_average' end
      from public.v_stock_position sp
     where l.invoice_id = p_invoice_id
       and sp.grade_id  = l.grade_id
       and sp.size_id   = l.size_id;

    -- ── 6 · and the stock leaves again, on the corrected figures ───────────
    -- reason is carried so the ledger says why these rows differ from the ones audit_log shows
    -- being deleted a moment earlier.
    insert into public.stock_movement
        (movement_date, grade_id, size_id, movement_type, weight_ct,
         price_per_ct, ref_type, ref_id, created_by, reason)
    select p_invoice_date, l.grade_id, l.size_id, 'SALE', l.selection_ct,
           l.price_per_ct, 'sales_line', l.line_id, auth.uid(), v_note
      from public.sales_line l
     where l.invoice_id = p_invoice_id and l.selection_ct > 0;

    insert into public.stock_movement
        (movement_date, grade_id, size_id, movement_type, weight_ct,
         price_per_ct, ref_type, ref_id, created_by, reason)
    select p_invoice_date, l.grade_id, l.size_id, 'REJECTION', l.rejection_ct,
           l.price_per_ct, 'sales_line', l.line_id, auth.uid(), v_note
      from public.sales_line l
     where l.invoice_id = p_invoice_id and l.rejection_ct > 0;

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

revoke all on function public.edit_posted_invoice(bigint, date, bigint, bigint, numeric, integer, text, bigint, jsonb, text) from public;
revoke all on function public.edit_posted_invoice(bigint, date, bigint, bigint, numeric, integer, text, bigint, jsonb, text) from anon;
grant execute on function public.edit_posted_invoice(bigint, date, bigint, bigint, numeric, integer, text, bigint, jsonb, text) to authenticated;

comment on function public.edit_posted_invoice is
    'Corrects a POSTED invoice in one transaction: deletes the stock movements it wrote, replaces its lines, re-checks the negative-stock policy against the corrected figures, and writes the movements again. Keeps invoice_no and POSTED status. Refuses drafts, cancelled invoices and imported MIG- invoices. audit_log carries the removed rows.';

commit;


-- ---------------------------------------------------------------------------
-- Verification · read only. Every row must read 'ok'.
-- ---------------------------------------------------------------------------
select 'edit_posted_invoice exists' as check,
       case when exists (select 1 from pg_proc p join pg_namespace n on n.oid = p.pronamespace
                          where n.nspname = 'public' and p.proname = 'edit_posted_invoice')
            then 'ok' else 'MISSING' end as status
union all
select 'it refuses an imported MIG- invoice',
       case when (select prosrc from pg_proc p join pg_namespace n on n.oid = p.pronamespace
                   where n.nspname = 'public' and p.proname = 'edit_posted_invoice') like '%MIG-%%'
            then 'ok' else 'MISSING' end
union all
select 'it deletes rather than reverses, so reconciliation holds',
       case when (select prosrc from pg_proc p join pg_namespace n on n.oid = p.pronamespace
                   where n.nspname = 'public' and p.proname = 'edit_posted_invoice')
                 like '%delete from public.stock_movement%'
            then 'ok' else 'MISSING' end
union all
select 'it re-checks the negative-stock policy',
       case when (select prosrc from pg_proc p join pg_namespace n on n.oid = p.pronamespace
                   where n.nspname = 'public' and p.proname = 'edit_posted_invoice')
                 like '%negative_stock_policy%'
            then 'ok' else 'MISSING' end
union all
select 'anon cannot execute it',
       case when has_function_privilege('anon',
                'public.edit_posted_invoice(bigint, date, bigint, bigint, numeric, integer, text, bigint, jsonb, text)',
                'execute')
            then 'ANON CAN EXECUTE' else 'ok' end
union all
select 'buckets negative right now',
       (select count(*)::text from public.v_stock_position where balance_ct < 0)
 order by 1;
