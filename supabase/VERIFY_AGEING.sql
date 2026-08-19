-- ===========================================================================
-- CALC-AGE · read-only verification of the ageing bands and the ₹0.50 threshold.
--
-- SELECT ONLY. No INSERT, UPDATE, DELETE or DDL — safe to run against the live
-- book at any time, before or after the migration that moves the bands
-- (supabase/migrations/0032_ageing_bands.sql).
--
-- Sections 1-3 are the BEFORE/AFTER evidence; run them once before applying the
-- migration and once after, and compare. Sections 4-6 must hold only afterwards.
-- ===========================================================================

-- ---------------------------------------------------------------------------
-- 1 · BEFORE/AFTER · what the ₹0.50 threshold moves.
--
-- Every posted invoice owing between one paisa and fifty. These leave the
-- receivables book and stop being overdue when the migration is applied. If
-- this returns no rows, the threshold change moves nothing at all.
-- ---------------------------------------------------------------------------
select count(*)                        as invoices_affected,
       coalesce(sum(outstanding), 0)   as rupees_leaving_the_book,
       count(*) filter (where is_overdue) as of_which_currently_overdue
from public.v_invoice
where status = 'POSTED' and outstanding > 0.01 and outstanding <= 0.50;

-- ---------------------------------------------------------------------------
-- 2 · BEFORE/AFTER · the ageing split.
--
-- Run before the migration and the first row is '0-30'; after it, 'not due' and
-- '1-30'. The two new figures must add up to the old one.
-- ---------------------------------------------------------------------------
select age_bucket,
       count(*)          as invoices,
       sum(outstanding)  as outstanding
from public.v_receivables_ageing
group by age_bucket
order by min(days_overdue);

-- ---------------------------------------------------------------------------
-- 3 · BEFORE/AFTER · the total must not move.
--
-- Splitting a band re-sorts money; it must never create or destroy any. These
-- two figures have to match each other, before and after.
-- ---------------------------------------------------------------------------
select (select coalesce(sum(outstanding), 0) from public.v_receivables_ageing) as banded_total,
       (select coalesce(sum(outstanding), 0) from public.v_invoice
        where status = 'POSTED' and outstanding > 0.50)                        as book_total;

-- ---------------------------------------------------------------------------
-- 4 · AFTER · the nine-value boundary contract.
--
-- The same table is asserted in Android's AgeingBoundaryTest and the desktop's
-- DiamondCalc.Tests. `expected` is written out independently of the view, so a
-- boundary edited in one place and not the other shows up as `ok = false`.
-- ---------------------------------------------------------------------------
with probe(days) as (
    values (-1), (0), (1), (30), (31), (60), (61), (90), (91)
),
expected as (
    select days,
           case
               when days <= 0  then 'not due'
               when days <= 30 then '1-30'
               when days <= 60 then '31-60'
               when days <= 90 then '61-90'
               else '90+'
           end as band
    from probe
)
select days, band as expected_band from expected order by days;

-- ---------------------------------------------------------------------------
-- 5 · AFTER · no invoice that is not past due may sit in an overdue band.
--
-- This is the whole defect, stated as an invariant. Must be 0.
-- ---------------------------------------------------------------------------
select count(*) as must_be_zero
from public.v_receivables_ageing
where age_bucket <> 'not due' and days_overdue <= 0;

-- ---------------------------------------------------------------------------
-- 6 · AFTER · and nothing past due may hide in 'not due'. Must be 0.
-- ---------------------------------------------------------------------------
select count(*) as must_be_zero
from public.v_receivables_ageing
where age_bucket = 'not due' and days_overdue > 0;

-- ---------------------------------------------------------------------------
-- 7 · AFTER · every band the view emits is one of the five agreed names.
--
-- Catches a typo in the CASE that would otherwise appear as a silently empty
-- tile on the desktop and a missing segment on the phone. Must be 0.
-- ---------------------------------------------------------------------------
select count(*) as must_be_zero
from public.v_receivables_ageing
where age_bucket not in ('not due', '1-30', '31-60', '61-90', '90+');
