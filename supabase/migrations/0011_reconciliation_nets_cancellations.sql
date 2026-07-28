-- ===========================================================================
-- v_reconciliation reports every cancellation as a permanent discrepancy.
--
-- The invariant is "carats that left a bucket must equal carats sold from it".
-- The ledger is append-only, so cancelling an invoice does not delete its SALE
-- and REJECTION rows; cancel_invoice writes compensating ADJUST rows instead
-- and the net movement returns to zero. That is correct and deliberate.
--
-- But the old view counted the SALE row in `moved_out_ct` while the cancelled
-- invoice contributed nothing to `sold_on_invoices_ct`, so the two sides
-- disagreed by exactly the cancelled quantity -- forever, for every
-- cancellation ever made:
--
--     NO 1    x -6.5  -- moved   6.0000 ct, invoiced 0.0000 ct, off by   6.0000
--     NO 1 BB x -2    -- moved 120.0000 ct, invoiced 0.0000 ct, off by 120.0000
--
-- Both buckets net to zero in the ledger. Neither is a stock problem. The cost
-- is not cosmetic: a report that always shows two false alarms is a report
-- nobody reads, and the first real drift hides among the noise.
--
--
-- Why net the reversals rather than skip cancelled invoices
--
-- The obvious fix -- ignore SALE rows whose invoice is CANCELLED -- needs the
-- movement to resolve back to its invoice. It does not always: the reversal
-- rows carry `ref_type = 'cancel'` with `ref_id` pointing at the INVOICE, not
-- the line, and one live pair points at an invoice whose rows were since
-- deleted. Joining through sales_line would silently drop those, which trades
-- a false positive for a false negative -- a SALE nobody reversed would stop
-- being reported at all. That is the worse failure.
--
-- Netting the ledger against itself needs no join and has no blind spot: a
-- cancellation zeroes itself out, and an unreversed movement still shows.
--
--
-- Why both sides are now GROSS carats
--
-- Posting writes two movements per line: SALE for the selection and REJECTION
-- for the remainder. A cancellation reverses BOTH, in ADJUST rows that do not
-- say which part they undo. So the reversal can only be netted against the sum
-- of the two, which is the line's gross weight -- and gross is the honest
-- measure anyway, because gross is what physically leaves the bucket.
--
-- `sold_on_invoices_ct` therefore reads sales_line.gross_weight_ct instead of
-- selection_ct. On an uncancelled invoice both definitions agree on whether a
-- bucket reconciles; only the displayed magnitude changes.
--
-- No table, RPC or policy is touched. This is a read-only reporting view.
-- ===========================================================================

create or replace view public.v_reconciliation as
with bucket as (
    -- Every grade x size, so a bucket that has never traded still reports 0/0
    -- rather than vanishing -- the old view did this and the Stock screen
    -- counts on it.
    select g.grade_id, g.code as grade_code, s.size_id, s.code as size_code
    from public.grade g
    cross join public.size_bucket s
),
moved as (
    -- What left the bucket, net of anything a cancellation put back.
    -- ADJUST is signed (0008) but a cancel reversal is always the positive
    -- return of stock, so it subtracts from "moved out".
    select m.grade_id,
           m.size_id,
           sum(case when m.movement_type in ('SALE', 'REJECTION') then m.weight_ct
                    else -m.weight_ct
               end) as ct
    from public.stock_movement m
    where m.movement_type in ('SALE', 'REJECTION')
       or (m.movement_type = 'ADJUST' and m.ref_type = 'cancel')
    group by m.grade_id, m.size_id
),
invoiced as (
    -- What the documents say was sold. A DRAFT is not a document and a
    -- CANCELLED one is no longer one, so only POSTED counts.
    select l.grade_id,
           l.size_id,
           sum(l.gross_weight_ct) as ct
    from public.sales_line l
    join public.sales_invoice i on i.invoice_id = l.invoice_id
    where i.status = 'POSTED'
    group by l.grade_id, l.size_id
)
select b.grade_code,
       b.size_code,
       coalesce(m.ct, 0)                     as moved_out_ct,
       coalesce(v.ct, 0)                     as sold_on_invoices_ct,
       coalesce(m.ct, 0) - coalesce(v.ct, 0) as diff_ct,
       -- weight_ct is numeric(14,4); half a ulp is the right tolerance, and
       -- an exact = 0 would make a rounding artefact look like missing stock.
       abs(coalesce(m.ct, 0) - coalesce(v.ct, 0)) < 0.00005 as reconciles
from bucket b
left join moved    m on m.grade_id = b.grade_id and m.size_id = b.size_id
left join invoiced v on v.grade_id = b.grade_id and v.size_id = b.size_id
order by b.grade_code, b.size_code;

comment on view public.v_reconciliation is
    'Stock ledger vs posted invoices, per grade x size, in gross carats. '
    'Cancellation reversals are netted off, so a cancelled invoice does not '
    'report as a discrepancy; an unreversed movement still does.';
