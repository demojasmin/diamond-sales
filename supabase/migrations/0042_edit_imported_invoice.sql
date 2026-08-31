-- ---------------------------------------------------------------------------
-- 0042 . An imported sale can be corrected, by moving only what changed.
--
-- WHY IT WAS REFUSED, AND WHY THAT WAS RIGHT
--
-- An imported MIG- invoice carries NO stock movements. replace_imported_sales (0018/0022) writes
-- none on purpose: the imported opening stock balance is already net of every sale on the sheet.
-- The carats those invoices sold left the bucket before this system existed.
--
-- 0040 corrects an invoice by deleting the movements it wrote and writing them again from the
-- corrected lines. Run against an imported invoice that becomes:
--
--   step 1  delete its movements          -> finds none, because there never were any
--   step 6  write them from the new lines -> takes the FULL new amount out of stock
--
-- so every carat on the invoice is deducted a second time, on top of an opening balance that had
-- already accounted for it. And v_reconciliation (0026) leaves MIG- lines out of
-- sold_on_invoices_ct while counting every SALE in moved_out_ct, so those movements would put the
-- bucket out of balance permanently, by an amount no amount of correct trading could clear.
--
-- The refusal was not a missing feature. It was the only thing standing between an edit and a
-- silent double deduction.
--
-- THE FIX
--
-- Move the DIFFERENCE, not the total. The old figures are already out of the opening balance, so
-- correcting 10 ct to 25 ct should take 15 ct more out, not 25.
--
-- Recorded as a signed ADJUST (0008) tagged ref_type = 'import_edit', never as a SALE:
--
--   * v_stock_position signs ADJUST, so the balance follows the correction exactly.
--   * v_reconciliation counts SALE only, so the bucket stays balanced: both sides continue to
--     ignore this invoice, which is what 0026 decided and what remains true.
--   * the ledger keeps the correction visible, with its reason on it, rather than silently
--     restating history.
--
-- The negative-stock guard asks the matching question. For an ordinary invoice the stock is back
-- at its pre-invoice level, so the bucket must cover the whole of the new figure. For an imported
-- one nothing was given back, so it need only cover the EXTRA.
--
-- Cost is deliberately still not stamped on imported lines. They carry no movements, so there is
-- no basis to stamp, 0024's margin view excludes them, and inventing a margin for a historical
-- sale would be worse than leaving it blank.
--
-- AND THE RE-IMPORT
--
-- replace_imported_sales deletes every MIG- invoice and re-inserts it from the sheet. It has never
-- touched stock_movement, because imported invoices had none. They can now. Left alone, a
-- re-import would restore the original lines and leave the adjustment applied, so stock would hold
-- a correction for an edit that no longer exists. It is cleared instead.
--
-- ONE TRANSACTION, as 0040 is: any raise rolls back the lot.
-- ---------------------------------------------------------------------------

begin;

-- ---------------------------------------------------------------------------
-- What changed, per bucket, between the invoice as it now stands and what it said before.
--
-- A function rather than the same CTE written twice: the guard and the movements must agree
-- exactly on what moved, and two copies of this arithmetic would be two chances to disagree.
--
-- FULL join, both ways: a bucket removed from the invoice (old, no new) must give its carats
-- back, and one newly added (new, no old) must take them out.
-- ---------------------------------------------------------------------------
create or replace function public.imported_edit_delta(p_invoice_id bigint, p_before jsonb)
returns table (grade_id bigint, size_id bigint, delta numeric)
language sql
stable
security definer
set search_path = public
as $fn$
    with old_ct as (
        select (r->>'g')::bigint   as grade_id,
               (r->>'s')::bigint   as size_id,
               (r->>'ct')::numeric as ct
          from jsonb_array_elements(coalesce(p_before, '[]'::jsonb)) r
    ),
    new_ct as (
        select l.grade_id, l.size_id, sum(l.gross_weight_ct) as ct
          from public.sales_line l
         where l.invoice_id = p_invoice_id
         group by l.grade_id, l.size_id
    )
    select coalesce(o.grade_id, n.grade_id),
           coalesce(o.size_id,  n.size_id),
           round(coalesce(n.ct, 0) - coalesce(o.ct, 0), 4)
      from old_ct o
      full join new_ct n on n.grade_id = o.grade_id and n.size_id = o.size_id;
$fn$;

comment on function public.imported_edit_delta is
    'CALC-9 (0042). Per grade x size, how much MORE (positive) or LESS (negative) gross weight an invoice now sells than the figures passed in. Used only for imported MIG- invoices, whose old carats are already out of the imported opening balance and so must not move a second time.';

revoke all on function public.imported_edit_delta(bigint, jsonb) from public;
revoke all on function public.imported_edit_delta(bigint, jsonb) from anon;
grant execute on function public.imported_edit_delta(bigint, jsonb) to authenticated;


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
    -- 0042. An imported invoice is corrected by DIFFERENCE, so what it said before is needed
    -- after its lines are gone.
    v_imported boolean;
    v_before   jsonb;
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

    -- 0042. Imported sales are corrected by DIFFERENCE rather than refused. See the header.
    v_imported := coalesce(v_no, '') like 'MIG-%';

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

    -- ── 1b · what an imported invoice said, before it is gone ───────────────
    -- These carats are ALREADY out of the opening balance. Only the difference between them and
    -- the corrected figures may move, which is the whole of why an imported invoice can now be
    -- edited at all.
    if v_imported then
        select jsonb_agg(jsonb_build_object('g', grade_id, 's', size_id, 'ct', out_ct))
          into v_before
          from (select grade_id, size_id, sum(gross_weight_ct) as out_ct
                  from public.sales_line
                 where invoice_id = p_invoice_id
                 group by grade_id, size_id) x;
    end if;

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
        -- An ordinary invoice: its stock is back at the pre-invoice level by now, so the question
        -- is whether the bucket covers the whole of what the invoice NOW says left it.
        --
        -- An imported one: nothing was given back, because nothing had been taken. The bucket
        -- already reflects the old figures, so the question is only whether it covers the EXTRA.
        select grade_id, size_id, sum(gross_weight_ct) as out_ct
          from public.sales_line
         where invoice_id = p_invoice_id and not v_imported
         group by grade_id, size_id
        union all
        select grade_id, size_id, delta
          from public.imported_edit_delta(p_invoice_id, v_before)
         where v_imported and delta > 0
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
    -- Imported lines are deliberately left uncostable, exactly as the import leaves them: they
    -- carry no movements, so there is no basis to stamp, and 0024's margin view excludes them.
    -- Stamping one here would invent a margin for a historical sale.
    update public.sales_line l
       set cost_per_ct = nullif(sp.avg_cost, 0),
           cost_basis  = case when nullif(sp.avg_cost, 0) is null
                              then null else 'moving_average' end
      from public.v_stock_position sp
     where l.invoice_id = p_invoice_id
       and not v_imported
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
     where l.invoice_id = p_invoice_id and l.selection_ct > 0 and not v_imported;

    insert into public.stock_movement
        (movement_date, grade_id, size_id, movement_type, weight_ct,
         price_per_ct, ref_type, ref_id, created_by, reason)
    select p_invoice_date, l.grade_id, l.size_id, 'REJECTION', l.rejection_ct,
           l.price_per_ct, 'sales_line', l.line_id, auth.uid(), v_note
      from public.sales_line l
     where l.invoice_id = p_invoice_id and l.rejection_ct > 0 and not v_imported;

    -- ── 6b · an imported invoice moves the DIFFERENCE, and nothing else ───────
    -- ADJUST, signed (0008), never SALE. Two reasons, and both matter:
    --
    --   the arithmetic — the old carats are already out of the opening balance, so writing the
    --   new figures in full would take them out twice. Only new minus old may move.
    --
    --   the report — v_reconciliation (0026) leaves MIG- lines out of sold_on_invoices_ct while
    --   counting every SALE in moved_out_ct. A SALE here would put the bucket out of balance for
    --   ever, by an amount no correct trading could clear. ADJUST is counted by neither side.
    --
    -- The sign is old minus new: selling MORE means more carats leave, which is a NEGATIVE
    -- adjustment to the balance.
    if v_imported then
        insert into public.stock_movement
            (movement_date, grade_id, size_id, movement_type, weight_ct,
             price_per_ct, ref_type, ref_id, created_by, reason)
        select p_invoice_date, d.grade_id, d.size_id, 'ADJUST', -d.delta,
               null, 'import_edit', p_invoice_id, auth.uid(), v_note
          from public.imported_edit_delta(p_invoice_id, v_before) d
         where d.delta <> 0;
    end if;

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
    'Corrects a POSTED invoice in one transaction: replaces its lines, re-checks the negative-stock policy against the corrected figures, and rewrites the stock. An ordinary invoice has its SALE/REJECTION movements deleted and written again in full. An IMPORTED MIG- invoice has no movements of its own (its carats are already out of the imported opening balance) so it moves only the difference, as a signed ADJUST tagged import_edit (0042). Keeps invoice_no and POSTED status. Refuses drafts and cancelled invoices. audit_log carries the removed rows.';


-- ---------------------------------------------------------------------------
-- A re-import must take the correction with it.
--
-- replace_imported_sales has never touched stock_movement, and until 0042 it never needed to: an
-- imported invoice had none. It can now carry an import_edit adjustment, and restoring the
-- sheet's original lines while leaving that adjustment applied would hold stock at a correction
-- whose invoice no longer exists.
--
-- Scoped to import_edit alone. Nothing else in stock_movement is the importer's to remove.
-- ---------------------------------------------------------------------------
create or replace function public.clear_import_edits(p_invoice_ids bigint[])
returns integer
language sql
security definer
set search_path = public
as $fn$
    with gone as (
        delete from public.stock_movement
         where ref_type = 'import_edit'
           and ref_id = any(p_invoice_ids)
        returning 1
    )
    select coalesce(count(*), 0)::integer from gone;
$fn$;

comment on function public.clear_import_edits is
    '0042. Removes the import_edit stock adjustments belonging to the given invoices. Called by replace_imported_sales before it deletes them, so a re-import does not leave stock holding a correction for an edit that has been replaced.';

revoke all on function public.clear_import_edits(bigint[]) from public;
revoke all on function public.clear_import_edits(bigint[]) from anon;
grant execute on function public.clear_import_edits(bigint[]) to authenticated;


-- The importer, taught to take the correction with it. Identical to 0022 in every other respect.
create or replace function public.replace_imported_sales(p_payload jsonb)
returns jsonb
language plpgsql
security definer
set search_path = public
as $$
declare
    v_old_ids     bigint[];
    v_deleted     integer := 0;
    v_invoices    integer := 0;
    v_lines       integer := 0;
    v_receipts    integer := 0;
    v_currency    bigint;
begin
    if p_payload is null or jsonb_typeof(p_payload->'invoices') <> 'array' then
        raise exception 'replace_imported_sales expects {"invoices": [...]}';
    end if;

    if jsonb_array_length(p_payload->'invoices') = 0 then
        raise exception 'replace_imported_sales was given no invoices';
    end if;

    v_currency := (p_payload->>'currency_id')::bigint;
    if v_currency is null then
        raise exception 'replace_imported_sales needs a currency_id';
    end if;

    -- Only ever the previous import. A live invoice carries INV-yyyy-nnnnn and
    -- is not matched by this; 08 §4 is why migrated numbers are prefixed at all.
    select coalesce(array_agg(invoice_id), '{}')
      into v_old_ids
      from public.sales_invoice
     where invoice_no like 'MIG-%';

    if array_length(v_old_ids, 1) is not null then
        -- 0042. An imported invoice can now carry an import_edit stock adjustment, from having
        -- been corrected here. Restoring the sheet's original lines while leaving that adjustment
        -- applied would hold stock at a correction whose invoice is being replaced. Cleared first,
        -- inside the same transaction, so the sheet and the stock cannot disagree.
        perform public.clear_import_edits(v_old_ids);

        delete from public.receipt     where invoice_id = any(v_old_ids);
        delete from public.sales_line  where invoice_id = any(v_old_ids);
        delete from public.sales_invoice where invoice_id = any(v_old_ids);
        get diagnostics v_deleted = row_count;
    end if;

    -- Invoices first, keeping invoice_no as the handle to hang lines off: the
    -- ids are assigned by the sequence and the payload cannot know them.
    with incoming as (
        select inv from jsonb_array_elements(p_payload->'invoices') as inv
    ),
    written as (
        insert into public.sales_invoice
            (invoice_no, invoice_date, buyer_id, broker_id, broker_pct,
             terms_days, doc_type, currency_id, status,
             created_by, updated_by)
        select inv->>'invoice_no',
               (inv->>'invoice_date')::date,
               (inv->>'buyer_id')::bigint,
               nullif(inv->>'broker_id', '')::bigint,
               coalesce((inv->>'broker_pct')::numeric, 0),
               coalesce((inv->>'terms_days')::integer, 0),
               coalesce(inv->>'doc_type', 'BILL'),
               v_currency,
               'POSTED',
               auth.uid(),
               auth.uid()
          from incoming
        returning invoice_id, invoice_no
    )
    select count(*) into v_invoices from written;

    -- Lines, matched back by invoice_no.
    with incoming as (
        select inv->>'invoice_no' as no,
               jsonb_array_elements(coalesce(inv->'lines', '[]'::jsonb)) as ln
          from jsonb_array_elements(p_payload->'invoices') as inv
    )
    insert into public.sales_line
        (invoice_id, grade_id, size_id, gross_weight_ct, selection_ct,
         price_per_ct, ex_rate, less1_pct, less2_pct, remark)
    select i.invoice_id,
           (c.ln->>'grade_id')::bigint,
           (c.ln->>'size_id')::bigint,
           (c.ln->>'gross_weight_ct')::numeric,
           (c.ln->>'selection_ct')::numeric,
           (c.ln->>'price_per_ct')::numeric,
           coalesce((c.ln->>'ex_rate')::numeric, 1),
           coalesce((c.ln->>'less1_pct')::numeric, 0),
           coalesce((c.ln->>'less2_pct')::numeric, 0),
           nullif(c.ln->>'remark', '')
      from incoming c
      join public.sales_invoice i on i.invoice_no = c.no;

    get diagnostics v_lines = row_count;

    -- One receipt per invoice that carried money, exactly as the workbook's
    -- single overwritten "Rec. Amt" cell states it (DQ-11: there is no payment
    -- history to migrate, only a running total). Dated the invoice date, since
    -- the sheet records no payment date -- docs/08 §5 says declare it, not bury
    -- it. Method 'IMPORTED', unchanged from the client-side importer.
    --
    -- `received` arrives ALREADY CAPPED at the invoice total. The cap stays on
    -- the client because it needs the line amounts CALC-1 produces, and those
    -- are the calculation engine's to compute, not this function's.
    with incoming as (
        select inv->>'invoice_no' as no,
               (inv->>'received')::numeric as received,
               (inv->>'invoice_date')::date as on_date
          from jsonb_array_elements(p_payload->'invoices') as inv
    )
    insert into public.receipt (invoice_id, receipt_date, amount, method, created_by)
    select i.invoice_id, c.on_date, c.received, 'IMPORTED', auth.uid()
      from incoming c
      join public.sales_invoice i on i.invoice_no = c.no
     where c.received is not null and c.received > 0;

    get diagnostics v_receipts = row_count;

    return jsonb_build_object('ok', true, 'deleted', v_deleted,
                              'invoices', v_invoices, 'lines', v_lines,
                              'receipts', v_receipts);
end;
$$;

commit;
