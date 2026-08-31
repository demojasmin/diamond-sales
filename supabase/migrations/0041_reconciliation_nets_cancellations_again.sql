-- ---------------------------------------------------------------------------
-- 0041 · A cancelled invoice must leave the reconciliation balanced.
--
-- THE FAULT
--
-- cancel_invoice (0010) does NOT delete the SALE movements. It leaves them and writes a signed
-- ADJUST beside each one, tagged ref_type = 'cancel', so both halves stay on the ledger and the
-- stock comes back. That is right, and v_stock_position handles it: 0008 signs ADJUST, so the
-- balance is correct the moment the cancellation lands.
--
-- v_reconciliation is where it goes wrong. It compares
--
--     moved_out_ct        every SALE movement, whatever its invoice now says
--     sold_on_invoices_ct the selection on POSTED lines only
--
-- so the instant an invoice is cancelled its carats leave the right-hand side and stay on the
-- left. The bucket reports a difference for ever, and no amount of correct trading clears it.
--
-- WHY IT CAME BACK
--
-- 0011 had this right:
--
--     where m.movement_type in ('SALE', 'REJECTION')
--        or (m.movement_type = 'ADJUST' and m.ref_type = 'cancel')
--
-- 0026 rewrote the view to keep migrated MIG- invoices out of it -- a real fix for a real
-- problem -- and in restating the whole thing dropped the netting. Nothing failed at the time
-- because neither database had a cancelled invoice yet.
--
-- THE FIX
--
-- Symmetry. sold_on_invoices_ct already ignores a cancelled invoice's lines; moved_out_ct now
-- ignores its movements. Both sides answer the same question -- "what does this business say it
-- sold" -- so a cancellation removes the sale from both at once and the bucket stays balanced.
--
-- Netting the ADJUST instead, as 0011 did, gives the same arithmetic. Excluding is clearer: a
-- cancelled sale is not a sale that was reversed, it is a sale that did not happen, and the ledger
-- keeps both movements either way.
--
-- NOT NETTED, DELIBERATELY: an ADJUST somebody recorded by hand. That is a real correction to a
-- real position and it belongs in the difference, which is the whole point of the report.
--
-- A VIEW ONLY. No data is touched, no movement is written or removed, and every balance on every
-- screen is exactly what it was a moment before this ran.
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
      -- 0026. A migrated invoice never moved stock and never should.
      and coalesce(i.invoice_no, '') not like 'MIG-%'
) sl on true;

comment on view public.v_reconciliation is
    'CALC-8. Carats leaving stock as SALE movements against carats sold on invoices that were expected to move stock, per grade x size. Migrated MIG- invoices are excluded (0026): the imported opening balance is already net of them. Cancelled invoices are excluded from BOTH sides (0041): cancel_invoice leaves the SALE movement in place and reverses it with an ADJUST, so counting the movement while the line had already dropped out reported a difference that could never be cleared.';

commit;


-- ---------------------------------------------------------------------------
-- Verification · read only. Every row must read 'ok'.
-- ---------------------------------------------------------------------------
select 'the view exists' as check,
       case when exists (select 1 from pg_views where schemaname = 'public' and viewname = 'v_reconciliation')
            then 'ok' else 'MISSING' end as status
union all
select 'it excludes cancelled invoices',
       case when pg_get_viewdef('public.v_reconciliation'::regclass) like '%CANCELLED%'
            then 'ok' else 'MISSING' end
union all
select 'it still excludes migrated MIG- invoices',
       case when pg_get_viewdef('public.v_reconciliation'::regclass) like '%MIG-%%'
            then 'ok' else 'MISSING' end
union all
select 'buckets that do not reconcile',
       (select count(*)::text from public.v_reconciliation where not reconciles)
union all
select 'which ones',
       coalesce((select string_agg(grade_code || ' x ' || size_code || ' (' || trim_scale(diff_ct) || ' ct)', ', ')
                   from public.v_reconciliation where not reconciles), 'none')
union all
select 'buckets negative right now',
       (select count(*)::text from public.v_stock_position where balance_ct < 0)
 order by 1;
