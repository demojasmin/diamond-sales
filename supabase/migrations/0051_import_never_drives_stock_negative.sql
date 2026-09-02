-- ---------------------------------------------------------------------------
-- 0051. The import takes what the shelf has and no more. It never goes negative.
--
-- SUPERSEDES 0050 RATHER THAN EDITING IT, as 0050 supersedes 0049 and 0049
-- supersedes 0048: an applied migration is a record of what a database was told.
-- 0050 stays on disk and stays applied; this replaces the function it installed.
--
-- WHAT CHANGES. 0049 deducted every imported line in full, and on a sheet whose
-- sales predate the stock count that drove the position far below zero -- the
-- desk saw -131,503 ct. The rule is now: an import may empty a bucket but may
-- not overdraw it. What the shelf could not cover is reported instead, as
-- short_ct and short_buckets, so the import dialog can warn about it.
--
-- WHAT DOES NOT CHANGE. Which lines write movements, their type, date, price and
-- tag; the delete-then-rewrite that stops a re-import deducting twice; every
-- number 0050 already reported. Only the WEIGHT on a capped line differs, and
-- only where there were not carats enough to take.
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
    v_stock_ct    numeric := 0;  -- 0050. and how many carats that came to
    v_short_ct    numeric := 0;  -- 0051. carats the sheet wanted and the shelf did not have
    v_short_bkts  integer := 0;  -- 0051. how many grade x size buckets ran out
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

    -- 0051 - EVERY imported sale comes out of stock, BUT NEVER BELOW ZERO.
    --
    -- 0049 took every line in full and let the position go negative. It is allowed to (0047), but
    -- on a sheet of historic sales it reads as thousands of carats owed that nobody owes. The desk
    -- has settled it: take what is on the shelf, leave what is not there, and say what was left.
    --
    -- HOW THE SHELF IS SHARED OUT. Lines are walked per grade x size bucket in invoice-date order,
    -- oldest first, with a running total of what earlier lines already took. Each line gets what is
    -- left of the bucket and no more, so the earliest sales come out whole and the shortfall lands
    -- on the latest ones. Spreading it pro-rata instead would leave every line slightly wrong
    -- rather than a named few plainly short, and "which sales could not be taken out" is a question
    -- the office can act on.
    --
    -- SALE BEFORE REJECTION inside a line, for the same reason: what was sold is the part that
    -- matters, so a line with only some room left records the sale and drops the rejection.
    --
    -- WHAT THIS COSTS, said plainly. A capped line's movements weigh LESS than the line does, so
    -- for that line the ledger and the invoice disagree -- by exactly the carats the shelf did not
    -- have. That disagreement is the point, and it is reported as short_ct / short_buckets rather
    -- than left for somebody to find.
    --
    -- The room is read from v_stock_position.balance_ct, which is net of reservations (0043), so an
    -- import cannot take carats a half-typed sales entry is holding. Floored at zero: a bucket
    -- already negative from earlier trading has nothing to give, and an import must not be the
    -- thing that digs it deeper. And it is the position BEFORE this import writes anything -- the
    -- movements below are inserted in the same statement that reads it.
    --
    -- Type, date, price and tag are 0049's unchanged: SALE and REJECTION, the pair post_invoice
    -- writes, tagged 'sales_line' so cancel_invoice reverses them, dated the INVOICE date so the
    -- Stock page counts them where they belong in time.
    with wanted as (
        select l.line_id, l.grade_id, l.size_id, l.price_per_ct,
               i.invoice_date,
               coalesce(l.selection_ct, 0) as selection_ct,
               coalesce(l.rejection_ct, 0) as rejection_ct,
               coalesce(l.selection_ct, 0) + coalesce(l.rejection_ct, 0) as gross_ct
          from public.sales_line l
          join public.sales_invoice i on i.invoice_id = l.invoice_id
         where i.invoice_no like 'MIG-%'
    ),
    running as (
        select w.*,
               greatest(coalesce(p.balance_ct, 0), 0) as room,
               coalesce(sum(w.gross_ct) over (partition by w.grade_id, w.size_id
                                              order by w.invoice_date, w.line_id
                                              rows between unbounded preceding and 1 preceding), 0)
                   as taken_before
          from wanted w
          left join public.v_stock_position p
                 on p.grade_id = w.grade_id and p.size_id = w.size_id
    ),
    capped as (
        select r.*,
               greatest(0, least(r.gross_ct, r.room - r.taken_before)) as allow_ct
          from running r
    ),
    split as (
        select c.*,
               least(c.selection_ct, c.allow_ct) as sale_out,
               least(c.rejection_ct, c.allow_ct - least(c.selection_ct, c.allow_ct)) as rej_out
          from capped c
    ),
    wrote as (
        insert into public.stock_movement
            (movement_date, grade_id, size_id, movement_type, weight_ct,
             price_per_ct, ref_type, ref_id, created_by)
        select s.invoice_date, s.grade_id, s.size_id, 'SALE', s.sale_out,
               s.price_per_ct, 'sales_line', s.line_id, auth.uid()
          from split s
         where s.sale_out > 0
        union all
        select s.invoice_date, s.grade_id, s.size_id, 'REJECTION', s.rej_out,
               s.price_per_ct, 'sales_line', s.line_id, auth.uid()
          from split s
         where s.rej_out > 0
        returning weight_ct
    ),
    -- The WEIGHT off the movements themselves, as 0050 summed it: they are what actually left.
    took as (
        select coalesce(sum(weight_ct), 0) as ct_out from wrote
    ),
    -- The COUNT off the lines, and only those that wrote a SALE -- which is what 0050's
    -- `get diagnostics` after its first insert counted, and what "Stock taken out: N line(s)"
    -- has always meant. Counting the movement rows instead double-counts every line that
    -- rejected as well as sold.
    --
    -- A line capped to nothing is not counted, and should not be: it took nothing out. A line
    -- that sold nothing was already excluded under 0050 for the same reason.
    counted as (
        select count(*) filter (where s.sale_out > 0) as lines_out from split s
    ),
    fell_short as (
        -- Rounded at 0.00005 so that a carat lost to numeric rounding is not reported as a
        -- shortage; the sheet is kept to four places everywhere else.
        select coalesce(round(sum(c.gross_ct - c.allow_ct), 4), 0) as short_ct,
               count(distinct (c.grade_id, c.size_id))             as short_buckets
          from capped c
         where c.gross_ct - c.allow_ct > 0.00005
    )
    select counted.lines_out, round(took.ct_out, 4),
           fell_short.short_ct, fell_short.short_buckets
      into v_stock_lines, v_stock_ct, v_short_ct, v_short_bkts
      from took, counted, fell_short;

    return jsonb_build_object('ok', true, 'deleted', v_deleted,
                              'invoices', v_invoices, 'lines', v_lines,
                              'receipts', v_receipts,
                              'stock_lines', v_stock_lines,
                              'stock_ct', v_stock_ct,
                              'short_ct', v_short_ct,
                              'short_buckets', v_short_bkts);
end;
$$;

comment on function public.replace_imported_sales is
    '0051. Replaces the imported MIG- sales history in one transaction and takes stock out for every imported line - SALE for what was sold, REJECTION for what was rejected - up to what the grade x size bucket actually holds, never below zero. Reports stock_lines and stock_ct for what left, and short_ct and short_buckets for what the shelf could not cover. Supersedes 0050, which deducted every line in full and allowed the position to go negative. A re-import removes the movements the previous import wrote before writing its own, so importing the same sheet twice deducts once.';

commit;
