-- ---------------------------------------------------------------------------
-- 0026 · CALC-8 must compare like with like.
--
-- v_reconciliation checks that the carats leaving stock as SALE movements equal
-- the carats sold on POSTED invoices, per grade x size. That is the right test,
-- and it currently fails on every cell -- not because the ledger is wrong, but
-- because the two sides count different things.
--
-- Migrated invoices (MIG-) are POSTED and carry sales lines, but they write NO
-- stock movements, on purpose. SaleImporter says why:
--
--     the migrated opening balance is already net of these sales, so posting
--     them through post_invoice() would deduct the same carats twice
--
-- The opening stock imported from the workbook is what was left AFTER those
-- 1,441 invoices. Deducting them again would take the book to roughly -184,000
-- ct. So the absence of those movements is the design working, and a view that
-- counts their lines on one side and finds nothing on the other reports a fault
-- that does not exist.
--
-- Worse, it reports it on EVERY cell, which makes the one check meant to catch a
-- genuinely missing movement useless -- there is no signal left to see it in.
--
-- Excluding migrated invoices restores the test: it now compares the ledger
-- against the invoices that were expected to move stock, and a real omission
-- shows up again. Same rule 0024 applies to margin coverage.
--
-- Nothing else changes: same columns, same names, same order, same types.
-- ---------------------------------------------------------------------------
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
    where m.grade_id = g.grade_id and m.size_id = s.size_id
      and m.movement_type = 'SALE'
) mv on true
left join lateral (
    select sum(l.selection_ct) as sold_ct
    from public.sales_line l
    join public.sales_invoice i using (invoice_id)
    where l.grade_id = g.grade_id and l.size_id = s.size_id
      and i.status = 'POSTED'
      -- The one change. A migrated invoice never moved stock and never should.
      and coalesce(i.invoice_no, '') not like 'MIG-%'
) sl on true;

comment on view public.v_reconciliation is 'CALC-8. Carats leaving stock as SALE movements against carats sold on invoices that were expected to move stock, per grade x size. Migrated MIG- invoices are excluded: the imported opening balance is already net of them, so they deliberately write no movements and counting their lines here reported a permanent, meaningless failure on every cell.';
