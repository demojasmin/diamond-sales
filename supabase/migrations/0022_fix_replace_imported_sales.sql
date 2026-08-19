-- ---------------------------------------------------------------------------
-- 0022 · replace_imported_sales, without the column that does not exist.
--
-- 0018 created this function with `posted_at` in the insert column list.
-- sales_invoice has no such column. PL/pgSQL bodies are not validated when the
-- function is created -- only the outer syntax is -- so 0018 applied cleanly and
-- the fault stayed invisible until the first real import ran and returned
--
--     42703: column "posted_at" of relation "sales_invoice" does not exist
--
-- and imported nothing. The whole call is one transaction, so nothing landed
-- half-done; the feature was simply unusable.
--
-- This migration exists because editing 0018 changes only what a FRESH database
-- would build. The broken body is already stored in every database 0018 has
-- been applied to, and nothing but a `create or replace` overwrites it.
--
-- Identical to 0018's function in every other respect: same signature, same
-- security definer and pinned search_path, same MIG-% delete scope, same status
-- of POSTED, same return shape. Two lines removed, nothing else.
-- ---------------------------------------------------------------------------
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
