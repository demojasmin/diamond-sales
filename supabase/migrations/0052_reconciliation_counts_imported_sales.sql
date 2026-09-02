-- ---------------------------------------------------------------------------
-- 0052. The reconciliation counts imported sales, because they now move stock.
--
-- THE FAULT
--
-- v_reconciliation compares two sides of the same question, per grade x size:
--
--     moved_out_ct        every SALE movement
--     sold_on_invoices_ct the selection on POSTED lines -- but NOT on MIG- ones
--
-- That asymmetry was correct when 0026 wrote it. An imported invoice moved no
-- stock at all: the sheet was read as an opening balance already net of its own
-- history, so a MIG- line had no movement to answer for and counting it would
-- have reported a difference against nothing.
--
-- 0049 changed that and nothing here followed. Imported sales write SALE
-- movements now, exactly as a sale typed into the app does. Those movements land
-- on the LEFT, their lines are still excluded from the RIGHT, and the view
-- reports a difference equal to the whole imported history -- 314 ct on the
-- desk's current sheet, and 28,529 ct on the full one. It cannot be cleared by
-- any amount of correct trading, which is the worst kind of red figure: one that
-- teaches the office to ignore the report.
--
-- THE FIX
--
-- One line removed. Both sides now count an imported sale, so it cancels out of
-- the difference the way an app-entered sale always has.
--
-- WHAT THE DIFFERENCE MEANS AFTERWARDS, and it is not always zero. 0051 caps an
-- imported line at what its bucket holds, so a line the shelf could not cover
-- writes a SALE SMALLER than its selection_ct. The remainder shows up here as a
-- NEGATIVE diff_ct -- the invoice says more was sold than the ledger took out,
-- which is exactly true and exactly what the office needs to see.
--
-- IT IS PART OF short_ct, NOT ALL OF IT, and the difference matters to anyone
-- reconciling the two. This view compares SALE movements against selection_ct;
-- rejections appear on NEITHER side. The import dialog's short_ct counts the
-- whole parcel, sold and rejected alike. So diff_ct is the SALE half:
--
--     short_ct  =  (selection short, which is -diff_ct)  +  (rejection short)
--
-- Measured on the desk's sheet: 334.1235 short in total, of which 203.2735 was
-- selection and shows here, and 130.8500 was rejection and does not. Reading a
-- mismatch between the two figures as a fault would be reading it wrongly.
--
-- So: a clean import reconciles to zero. A short one reports its shortfall, in
-- the right buckets, permanently, until somebody puts the stock in or corrects
-- the sale. That is the report doing its job rather than carrying a constant.
--
-- NOTHING ELSE CHANGES. Cancelled invoices are still excluded from both sides
-- (0041). The columns, their names and their order are 0041's, untouched --
-- "create or replace view" may only append, and this appends nothing.
--
-- A VIEW ONLY. No data is touched, no movement is written or removed, and every
-- balance on every screen is exactly what it was a moment before this ran.
-- ---------------------------------------------------------------------------

begin;

create or replace view public.v_reconciliation as
select
    g.code                                  as grade_code,
    s.code                                  as size_code,
    coalesce(mv.sale_ct, 0)                 as moved_out_ct,
    coalesce(sl.sold_ct, 0)                 as sold_on_invoices_ct,
    round(coalesce(mv.sale_ct, 0) - coalesce(sl.sold_ct, 0), 4) as diff_ct,
    abs(coalesce(mv.sale_ct, 0) - coalesce(sl.sold_ct, 0)) < 0.0001 as reconciles
from public.grade g
cross join public.size_bucket s
left join lateral (
    select sum(m.weight_ct) as sale_ct
    from public.stock_movement m
    -- LEFT joined, and coalesced below: a SALE movement that points at no line is not something
    -- this view should hide. It has no invoice to excuse it, so it stays in the difference where
    -- somebody will see it.
    left join public.sales_line    l on l.line_id    = m.ref_id and m.ref_type = 'sales_line'
    left join public.sales_invoice i on i.invoice_id = l.invoice_id
    where m.grade_id = g.grade_id and m.size_id = s.size_id
      and m.movement_type = 'SALE'
      -- 0041. The cancelled invoice's lines already left sold_ct below; its movements leave here.
      and coalesce(i.status, 'POSTED') <> 'CANCELLED'
) mv on true
left join lateral (
    select sum(l.selection_ct) as sold_ct
    from public.sales_line l
    join public.sales_invoice i using (invoice_id)
    where l.grade_id = g.grade_id and l.size_id = s.size_id
      and i.status = 'POSTED'
      -- 0052. 0026 excluded MIG- invoices here because an imported sale moved no stock. Since 0049
      -- it does, and its SALE movement is counted above -- so excluding the line it came from left
      -- the whole imported history sitting in the difference with nothing to answer it.
) sl on true;

comment on view public.v_reconciliation is
    'CALC-8. Carats leaving stock as SALE movements against carats sold on invoices, per grade x size. Imported MIG- invoices are counted on BOTH sides (0052): they move stock since 0049, so excluding their lines left their movements unanswered. Where 0051 capped an imported line at what its bucket held, diff_ct is negative by the SELECTION the shelf could not cover - part of the import dialog''s short_ct, not all of it, because that figure counts rejections too and this view counts them on neither side. Cancelled invoices are excluded from BOTH sides (0041): cancel_invoice leaves the SALE movement in place and reverses it with an ADJUST, so counting the movement while the line had already dropped out reported a difference that could never be cleared.';

commit;


-- ---------------------------------------------------------------------------
-- Verification · read only. The first three must read 'ok'.
-- ---------------------------------------------------------------------------
select 'the view exists' as check, 1 as ord,
       case when exists (select 1 from pg_views where schemaname = 'public' and viewname = 'v_reconciliation')
            then 'ok' else 'MISSING' end as status
union all
select 'it still excludes cancelled invoices', 2,
       case when pg_get_viewdef('public.v_reconciliation'::regclass) like '%CANCELLED%'
            then 'ok' else 'MISSING' end
union all
-- The point of this migration, stated as its own check: the exclusion must be GONE.
select 'it no longer excludes migrated MIG- invoices', 3,
       case when pg_get_viewdef('public.v_reconciliation'::regclass) like '%MIG-%%'
            then 'STILL THERE - 0052 did not take' else 'ok' end
union all
select 'buckets that do not reconcile', 4,
       (select count(*)::text from public.v_reconciliation where not reconciles)
union all
-- The SELECTION half of the last import's short_ct, negative. Zero if it reported none.
select 'carats they differ by, in total', 5,
       (select coalesce(trim_scale(round(sum(diff_ct), 4))::text, '0')
          from public.v_reconciliation where not reconciles)
union all
select 'which ones', 6,
       coalesce((select string_agg(grade_code || ' x ' || size_code || ' (' || trim_scale(diff_ct) || ' ct)', ', ')
                   from public.v_reconciliation where not reconciles), 'none')
 order by ord;
