-- ===========================================================================
-- 0033 · CALC-AGE — the third leg of the fifty-paise threshold.
--
-- 0032 moved v_invoice.is_overdue and v_receivables_ageing to > 0.50 and left
-- this function at > 0.01 on purpose: a view can be replaced from its own
-- pg_get_viewdef output, and this function's deployed body had not been read.
-- It feeds the desktop dashboard tiles (DiamondDesktop MainWindow.xaml.cs,
-- Repo.DashboardAsync), so an outstanding_total computed at 0.01 sat beside a
-- receivables page computed at 0.50 and the two disagreed by whatever lay
-- between them.
--
-- ---------------------------------------------------------------------------
-- PRE-FLIGHT — DO THIS BEFORE RUNNING THE FILE.
--
-- No migration in this repository creates dashboard_summary; it predates 0007
-- and its body lives only in the database. The text below is the Android repo's
-- bootstrap version (0002_calc_views.sql) with the one threshold changed. If the
-- deployed body has drifted from it — as v_invoice had, by three columns — this
-- would silently revert that drift.
--
-- Capture the live body and diff it against the body below. Only the `where`
-- line inside `book` may differ. If anything else differs, edit THIS FILE to
-- match the live text and change only that line:
--
--     select pg_get_functiondef('public.dashboard_summary(date,date)'::regprocedure);
--
-- `create or replace function` cannot change the return type, so a live body
-- returning a different column set will fail loudly (42P13) rather than
-- silently — that is the backstop, not the plan.
-- ---------------------------------------------------------------------------
--
-- SCOPE. One function body. No INSERT, UPDATE, DELETE, DROP, ALTER TABLE or
-- TRUNCATE — it cannot change a business record.
--
-- ROLLBACK. Re-run this file with 0.50 -> 0.01, or restore the captured body.
--
-- Order matters only in that this reads v_invoice.is_overdue, which 0032 sets to
-- the same threshold. Run 0032 first; running this one alone leaves the overdue
-- tiles at 0.01 and only the outstanding total at 0.50.
-- ===========================================================================

set local search_path = public, pg_temp;

create or replace function public.dashboard_summary(
    p_from date,
    p_to   date
)
returns table (
    sales_amount      numeric,
    carats_sold       numeric,
    blended_rate      numeric,
    invoice_count     bigint,
    outstanding_total numeric,
    overdue_total     numeric,
    overdue_count     bigint,
    stock_value       numeric,
    stock_carats      numeric
)
language sql
stable
as $$
    with period as (
        select coalesce(sum(amount_total), 0) as amt,
               coalesce(sum(carats_sold), 0)  as ct,
               count(*)                       as n
        from public.v_invoice
        where status = 'POSTED' and invoice_date between p_from and p_to
    ),
    book as (
        select coalesce(sum(outstanding), 0)                                as out_total,
               coalesce(sum(outstanding) filter (where is_overdue), 0)      as od_total,
               count(*) filter (where is_overdue)                           as od_count
        from public.v_invoice
        -- The one changed line. 0.50 is DiamondCalc.Calc.SettledBelow and
        -- domain/Calc.kt::SETTLED_BELOW; owed means strictly more than fifty
        -- paise, so exactly 0.50 is settled here, on the desktop and on Android.
        where status = 'POSTED' and outstanding > 0.50
    ),
    stock as (
        select coalesce(sum(stock_value), 0)                  as val,
               coalesce(sum(greatest(balance_ct, 0)), 0)      as ct
        from public.v_stock_position
    )
    select period.amt,
           period.ct,
           case when period.ct > 0 then period.amt / period.ct else 0 end,
           period.n,
           book.out_total,
           book.od_total,
           book.od_count,
           stock.val,
           stock.ct
    from period, book, stock;
$$;

-- Security posture, re-applied. `create or replace function` keeps the existing
-- ACL, so 0031_revoke_anon_write_rpcs.sql's revokes survive this file on their
-- own. These three lines say so out loud rather than relying on it: a future
-- drop-and-recreate would silently hand EXECUTE back to PUBLIC, which is the
-- default this database deliberately does not use.
grant  execute on function public.dashboard_summary(date, date) to authenticated;
revoke execute on function public.dashboard_summary(date, date) from public;
revoke execute on function public.dashboard_summary(date, date) from anon;
