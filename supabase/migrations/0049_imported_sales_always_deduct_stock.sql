-- ---------------------------------------------------------------------------
-- 0049. EVERY imported sale takes its carats out of stock.
--
-- SUPERSEDES 0048 RATHER THAN EDITING IT, the same way 0047 supersedes 0038: an
-- applied migration is a record of what a database was told, and rewriting one
-- in place makes two databases with identical migration lists behave
-- differently. 0048 stays on disk and stays applied; this replaces the function
-- it installed.
--
-- WHAT 0048 DID, and why it is going:
--
--   sale dated <= the last stock count   no movement
--   sale dated >  the last stock count   SALE + REJECTION
--
-- That read the stock sheet as a COUNT taken after those sales, so their carats
-- were already off the shelf and deducting again would remove them twice. The
-- desk has decided otherwise: an imported sale is a sale, and it moves stock
-- exactly as one typed into the app does. The date is no longer consulted.
--
-- WHAT THIS DOES:
--
--   every imported MIG- line   SALE for what was sold, REJECTION for what was
--                              rejected -- the same pair post_invoice writes
--                              for a live sale
--
-- THE CONSEQUENCE, stated once and plainly. If the stock sheet is a count taken
-- AFTER the sales imported alongside it, this takes the same carats out a second
-- time and the position falls below what is physically in the drawer. Negative
-- stock is allowed (0047), so nothing refuses and nothing warns. If the sheet is
-- an OPENING balance, this is right and 0048 was the bug. Which of the two it is
-- is the office's call, not this function's.
--
-- NO DOUBLE DEDUCTION ON RE-IMPORT. The function removes the movements the
-- previous import wrote before writing its own, so importing the same sheet ten
-- times deducts once. These rows are written here and nowhere else, which is
-- what makes that guard sufficient.
--
-- NOTHING IS APPLIED TO DATA ALREADY IMPORTED. Movements are written during an
-- import and at no other time, so an existing position does not move when this
-- migration is applied. The next import is what brings it into line.
-- ---------------------------------------------------------------------------

begin;

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
    v_stock_lines integer := 0;  -- 0049. lines whose carats this import took out
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

        -- 0049. And the SALE/REJECTION movements the previous import wrote. This is the whole of
        -- what stops a re-import deducting twice. BEFORE the lines go: they are found through sales_line.line_id, and once the
        -- lines are deleted there is nothing left to find them by -- they would sit in the ledger
        -- deducting carats for an invoice that no longer exists.
        delete from public.stock_movement
         where ref_type = 'sales_line'
           and ref_id in (select line_id from public.sales_line
                           where invoice_id = any(v_old_ids));

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

    -- 0049 - EVERY imported sale comes out of stock.
    --
    -- 0048 deducted only the sales dated after the last stock count, reading the sheet as a COUNT
    -- taken with the earlier ones already gone. The desk has decided otherwise: an imported sale is
    -- a sale, and it moves stock exactly as one typed into the app does. The date is not consulted.
    --
    -- WORTH KNOWING, because it is why 0048 read the other way: if the stock sheet IS a count taken
    -- after these sales, this takes the same carats out a second time and the position falls below
    -- what is in the drawer. Negative stock is allowed (0047), so nothing refuses and nothing warns.
    --
    -- SALE and REJECTION, the same pair post_invoice writes, tagged 'sales_line' for the same
    -- reason: cancel_invoice reverses movements by that tag, so a cancelled import gives its carats
    -- back without a line of new code. edit_posted_invoice is unaffected -- its MIG- branch adds a
    -- signed delta rather than rewriting, so old + delta is still the new figure.
    --
    -- Dated the INVOICE date, not today, so the Stock page counts them where they belong in time.
    insert into public.stock_movement
        (movement_date, grade_id, size_id, movement_type, weight_ct,
         price_per_ct, ref_type, ref_id, created_by)
    select i.invoice_date, l.grade_id, l.size_id, 'SALE', l.selection_ct,
           l.price_per_ct, 'sales_line', l.line_id, auth.uid()
      from public.sales_line l
      join public.sales_invoice i on i.invoice_id = l.invoice_id
     where i.invoice_no like 'MIG-%'
       and l.selection_ct > 0;

    get diagnostics v_stock_lines = row_count;

    insert into public.stock_movement
        (movement_date, grade_id, size_id, movement_type, weight_ct,
         price_per_ct, ref_type, ref_id, created_by)
    select i.invoice_date, l.grade_id, l.size_id, 'REJECTION', l.rejection_ct,
           l.price_per_ct, 'sales_line', l.line_id, auth.uid()
      from public.sales_line l
      join public.sales_invoice i on i.invoice_id = l.invoice_id
     where i.invoice_no like 'MIG-%'
       and l.rejection_ct > 0;

    return jsonb_build_object('ok', true, 'deleted', v_deleted,
                              'invoices', v_invoices, 'lines', v_lines,
                              'receipts', v_receipts,
                              'stock_lines', v_stock_lines);
end;
$$;

comment on function public.replace_imported_sales is
    '0049. Replaces the imported MIG- sales history in one transaction and takes stock out for EVERY imported line - SALE for what was sold, REJECTION for what was rejected - exactly as a sale entered in the app does. Supersedes 0048, which deducted only the sales dated after the last stock count. A re-import removes the movements the previous import wrote before writing its own, so importing the same sheet twice deducts once.';

commit;
