-- ===========================================================================
-- Removes one corrupt intake that dominates the company valuation.
--
--     stock_movement 12   INTAKE  500,500.0000 ct @ 4,000,037,500.00  2026-07-27
--
-- It is not a business event. It came from an automated UI run where the entry
-- fields did not clear between rows, so typed values concatenated: the weight
-- and the price each ran two entries together. The next highest price anywhere
-- in the ledger is 55,000.00, so the row is unambiguous.
--
-- What it does to the numbers:
--
--     NO II x +6.5   balance   560,500.0000 ct   should be 60,000.0000
--                    avg cost  3,571,848,115.52  should be     35,000.00
--                    value     2,002,020,868,750,000
--
--     company total  21,20,400.0000 ct and 2,00,20,75,46,66,95,721.35
--                    -- that one bucket is 99.9973% of the whole valuation
--
--
-- Why DELETE and not a compensating ADJUST
--
-- The ledger is append-only and that is right: a reversal preserves the audit
-- trail where a delete destroys it. But avg_cost in v_stock_position is a
-- weighted average over rows that ADD stock. A negative ADJUST removes the
-- 500,500 ct and leaves 4,000,037,500 in the average, so the bucket would read
-- 60,000 ct at ~3.57 billion -- still 10^14 out. Reversal can correct a wrong
-- QUANTITY; it cannot correct a wrong PRICE.
--
-- The append-only rule protects real history. This row records something that
-- never happened, so there is no history to protect.
--
-- Its rough_intake parent (intake_id 2, same weight and price) is already gone,
-- which is why movement 12's ref_id no longer resolves.
--
--
-- Run this in the Supabase SQL editor. The desktop app cannot: the authenticated
-- role has no DELETE on stock_movement, which is the ledger working as designed.
-- ===========================================================================

do $$
declare
    v_deleted int;
begin
    -- Pinned to the exact row on every field, so this can never widen. If the
    -- row is already gone, the block reports it and changes nothing.
    delete from public.stock_movement
    where movement_id   = 12
      and movement_type = 'INTAKE'
      and weight_ct     = 500500.0000
      and price_per_ct  = 4000037500.00;

    get diagnostics v_deleted = row_count;

    if v_deleted = 1 then
        raise notice 'Removed corrupt intake: stock_movement 12.';
    else
        raise notice 'Nothing removed - stock_movement 12 is absent or does not match.';
    end if;
end $$;

-- Expected afterwards:
--   select balance_ct, avg_cost, stock_value from public.v_stock_position
--    where grade_code = 'NO II' and size_code = '+6.5';
--   -->  60000.0000 | 35000.00 | 2100000000.00
--
--   select sum(balance_ct), sum(stock_value) from public.v_stock_position;
--   -->  1619900.0000 | approximately 5.67e10
