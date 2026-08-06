-- ---------------------------------------------------------------------------
-- 0015 · Remove the corrupt intake and the 2024-07-01 seed stock, keeping the
--        62 parcels imported from stk BKC-JAN.xlsx.
--
-- MUST BE RUN IN THE SUPABASE SQL EDITOR. The application cannot do this: the
-- ledger is append-only and Postgres enforces it -- `authenticated` has INSERT
-- on stock_movement but no DELETE, so the client fails with 42501.
--
-- Precedent: 0012_remove_corrupt_test_intake did the same for an earlier row.
--
-- Run part 1 and 2 together. Part 3 is a decision, and is left commented out.
-- ---------------------------------------------------------------------------

begin;

-- ---------------------------------------------------------------------------
-- 1 · The corrupt parcel
--
--     NO II x +6.5, 5,00,500 ct at 4,00,00,37,500.00 per carat.
--     That one row is 2.002e15 of "value" -- 99.997% of everything the app
--     reports, which is why Inventory value reads 2,002,073 B and the bucket's
--     average cost reads 3.57 billion per carat.
--
--     No real rate approaches a million, so the threshold identifies it without
--     naming an id that could drift.
-- ---------------------------------------------------------------------------
delete from public.stock_movement
 where movement_type = 'INTAKE'
   and coalesce(price_per_ct, 0) > 1000000;

delete from public.rough_intake
 where coalesce(price_per_ct, 0) > 1000000;

-- ---------------------------------------------------------------------------
-- 2 · The seed stock, except the one bucket that cannot go yet
--
--     27 rows dated 2024-07-01, every one exactly 60,000 ct at 35,000 --
--     16,20,000 ct of demonstration data.
--
--     NO 1 x +6.5 is HELD BACK. It carries a 50,000 ct REJECTION from posted
--     invoice INV-2026-00004, so removing its 60,000 ct intake would leave the
--     bucket at -49,739.64 ct. Part 3 deals with that.
--
--     The seed movements carry ref_id NULL, and matching rough_intake rows exist
--     but are orphaned -- invisible to v_stock_position, which reads only
--     stock_movement, yet still clutter the table. Both go.
-- ---------------------------------------------------------------------------
delete from public.stock_movement m
 where m.movement_type = 'INTAKE'
   and m.movement_date = date '2024-07-01'
   and m.ref_type is distinct from 'stock_import'
   and not (m.grade_id = (select grade_id from public.grade where code = 'NO 1')
        and m.size_id  = (select size_id  from public.size_bucket where code = '+6.5'));

delete from public.rough_intake i
 where i.intake_date = date '2024-07-01'
   and not (i.grade_id = (select grade_id from public.grade where code = 'NO 1')
        and i.size_id  = (select size_id  from public.size_bucket where code = '+6.5'));

commit;

-- ---------------------------------------------------------------------------
-- 3 · DECISION REQUIRED — the last seed row and the invoice holding it
--
--     INV-2026-00004 (03 Aug 2026, KIRAN EXPORTS, amount 0.00) has a single
--     line: 50,000 ct gross, 0 selected, so the whole parcel was rejected. It
--     is the zero-value test invoice already flagged during testing.
--
--     Option A — delete the invoice as test data, then the last seed row.
--     Leaves NO 1 x +6.5 at 260.36 ct, purely from the workbook. Uncomment:
--
-- begin;
-- -- The receipt goes FIRST. INV-2026-00004 carries receipt 4177 for 120.00 CASH, which is why
-- -- its Outstanding reads -120.00. receipt.invoice_id references sales_invoice, so deleting the
-- -- invoice while that row stands aborts on a foreign key. Children before parents throughout.
-- delete from public.receipt where invoice_id = 5677;
-- delete from public.stock_movement
--  where ref_type = 'sales_line'
--    and ref_id in (select line_id from public.sales_line where invoice_id = 5677);
-- delete from public.sales_line where invoice_id = 5677;
-- delete from public.sales_invoice where invoice_id = 5677;
-- delete from public.stock_movement
--  where movement_type = 'INTAKE'
--    and movement_date = date '2024-07-01'
--    and ref_type is distinct from 'stock_import';
-- delete from public.rough_intake where intake_date = date '2024-07-01';
-- commit;
--
--     Option B — keep the invoice as a real trade. Cancel it in the app instead
--     (Invoices -> select -> Cancel), which writes a compensating ADJUST and is
--     the append-only way. Note this dilutes the bucket's average cost, because
--     the ADJUST returns 50,000 ct at the line's 5,000/ct rate.
--
--     Option C — do nothing. NO 1 x +6.5 keeps 60,000 ct of seed stock.
-- ---------------------------------------------------------------------------

-- ---------------------------------------------------------------------------
-- Verification — run after part 1 and 2.
-- Expect: 62 imported movements, 1 seed row left, 0 corrupt, no negatives.
-- ---------------------------------------------------------------------------
-- select count(*) filter (where ref_type = 'stock_import')            as imported,
--        count(*) filter (where movement_date = date '2024-07-01')    as seed_left,
--        count(*) filter (where coalesce(price_per_ct,0) > 1000000)   as corrupt_left
--   from public.stock_movement where movement_type = 'INTAKE';
--
-- select round(sum(balance_ct), 4)  as carats,
--        round(sum(stock_value), 2) as inventory_value,
--        count(*) filter (where balance_ct < 0) as negative_buckets
--   from public.v_stock_position where balance_ct <> 0;
