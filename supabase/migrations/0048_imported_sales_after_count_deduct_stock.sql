-- ---------------------------------------------------------------------------
-- 0048. An imported sale made AFTER the stock count takes its carats out.
--
-- THE RULE, in one line: a sale dated on or before the count is already in it;
-- a sale dated after it is not.
--
-- The stock importer lands a COUNTED POSITION, not an opening balance. When the
-- shelf was counted on 1 September, every parcel sold before that date was
-- already gone from it -- which is why replace_imported_sales has never written
-- a stock movement, and why it must not start writing one for those sales.
-- Doing so would take the same carats out twice.
--
-- What was missing is the other half. A sheet of sales that runs PAST the count
-- date carries invoices whose carats are still on the shelf as far as the
-- ledger knows, and nothing ever took them off. The position read high by
-- exactly those lines, silently, with no row to point at.
--
-- WHAT THIS CHANGES, precisely:
--
--   imported sale dated <= the count   nothing, exactly as before
--   imported sale dated >  the count   SALE + REJECTION movements, as a posted
--                                      sale writes them
--   no stock count in the database     nothing, exactly as before
--
-- NOTHING IS APPLIED TO DATA ALREADY IMPORTED. This writes movements during an
-- import and at no other time, so an existing position does not move when the
-- migration is applied. The next import of the same sheet is what brings it
-- into line -- and that import first removes the movements the previous one
-- wrote, so importing twice deducts once.
--
-- Tagged ref_type = 'sales_line' deliberately, so the two functions that
-- already understand that tag keep working untouched:
--
--   cancel_invoice        reverses SALE/REJECTION rows by this tag, so
--                         cancelling an imported invoice returns its carats
--   edit_posted_invoice   adds a signed delta for a MIG- invoice rather than
--                         rewriting its movements, so old + delta is still right
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
    v_counted     date;          -- 0048. the day the stock was last counted
    v_stock_lines integer := 0;  -- 0048. lines whose carats this import took out
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

        -- 0048. And the SALE/REJECTION movements this importer wrote for invoices dated after the
        -- stock count. BEFORE the lines go: they are found through sales_line.line_id, and once the
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

    -- 0048 - sales made AFTER the count come out of stock.
    --
    -- An imported sale dated on or before the stock count is already accounted for: the count was
    -- taken with those carats gone, so deducting again would take the same parcel out twice. A sale
    -- dated AFTER it is not in the count, and until now nothing took it out at all -- the position
    -- read high by exactly those carats.
    select max(movement_date) into v_counted
      from public.stock_movement
     where ref_type = 'stock_import';

    -- NO COUNT, NO DEDUCTION. A database that has never imported a stock sheet has no date to
    -- compare against, and guessing one either way would be inventing a position. Behaves exactly
    -- as it did before 0048.
    if v_counted is not null then
        -- SALE and REJECTION, the same pair post_invoice writes, tagged 'sales_line' for the same
        -- reason: cancel_invoice reverses movements by that tag, so a cancelled import gives its
        -- carats back without a line of new code. edit_posted_invoice is unaffected -- its MIG-
        -- branch adds a signed delta rather than rewriting, so old + delta is still the new figure.
        --
        -- Dated the INVOICE date, not today, so the Stock page counts them where they belong.
        insert into public.stock_movement
            (movement_date, grade_id, size_id, movement_type, weight_ct,
             price_per_ct, ref_type, ref_id, created_by)
        select i.invoice_date, l.grade_id, l.size_id, 'SALE', l.selection_ct,
               l.price_per_ct, 'sales_line', l.line_id, auth.uid()
          from public.sales_line l
          join public.sales_invoice i on i.invoice_id = l.invoice_id
         where i.invoice_no like 'MIG-%'
           and i.invoice_date > v_counted
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
           and i.invoice_date > v_counted
           and l.rejection_ct > 0;
    end if;

    return jsonb_build_object('ok', true, 'deleted', v_deleted,
                              'invoices', v_invoices, 'lines', v_lines,
                              'receipts', v_receipts,
                              'counted_as_at', v_counted,
                              'stock_lines', v_stock_lines);
end;
$$;

comment on function public.replace_imported_sales is
    '0048. Replaces the imported MIG- sales history in one transaction, and takes stock out for the imported sales dated AFTER the newest stock_import movement - those are the ones the counted position does not already account for. Sales on or before the count write no movement, because the count was taken with those carats already gone. A database with no stock import writes none at all. A re-import removes the movements the previous import wrote before writing its own.';

commit;
