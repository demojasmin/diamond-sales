-- ---------------------------------------------------------------------------
-- 0024 · Count the margin against invoices that COULD carry a cost.
--
-- 0020 reported coverage as "costed of all POSTED", which reads 3 of 1,438 and
-- looks like a broken feature. It is not: migrated MIG- invoices deliberately
-- write no stock movements -- the opening balance is already net of those sales
-- -- so they have no cost basis and never can. Counting them in the denominator
-- measures the migration, not the margin.
--
-- invoices_total now counts only invoices that could be costed at all: those
-- posted through the app, which is what MIG- excludes. The same figure read
-- honestly becomes "3 of 3".
--
-- invoices_uncostable is added rather than dropping the old number entirely --
-- a caller that wants to say "and 1,435 migrated invoices carry no cost" still
-- can, and nothing has to guess at it by subtracting.
--
-- The money columns do not change. They only ever summed costed invoices.
-- ---------------------------------------------------------------------------
drop function if exists public.margin_summary(date, date);

create or replace function public.margin_summary(
    p_from date default null,
    p_to   date default null
)
returns table (
    revenue_total       numeric,
    cost_total          numeric,
    margin_total        numeric,
    margin_pct          numeric,
    invoices_costed     bigint,
    invoices_total      bigint,
    invoices_uncostable bigint
)
language sql
stable
as $$
    with scoped as (
        select *
          from public.v_invoice
         where status = 'POSTED'
           and (p_from is null or invoice_date >= p_from)
           and (p_to   is null or invoice_date <= p_to)
    ),
    -- A migrated invoice writes no stock movement, so nothing can ever stamp a
    -- cost onto its lines. Judging the feature by them is judging it by rows it
    -- was never able to reach.
    costable as (
        select * from scoped where invoice_no not like 'MIG-%'
    ),
    costed as (
        select * from costable where margin is not null
    )
    select
        round(coalesce(sum(c.amount_total), 0), 2),
        round(coalesce(sum(c.cost_total),   0), 2),
        round(coalesce(sum(c.margin),       0), 2),
        case when coalesce(sum(c.amount_total), 0) > 0
             then round(100 * sum(c.margin) / sum(c.amount_total), 2)
             else 0 end,
        (select count(*) from costed),
        (select count(*) from costable),
        (select count(*) from scoped) - (select count(*) from costable)
      from costed c;
$$;

comment on function public.margin_summary(date, date) is 'Revenue, cost and margin over POSTED invoices that could carry a cost basis, with the count costed against the count costable. Migrated MIG- invoices write no stock movements and can never be costed; they are reported separately as invoices_uncostable rather than dragging the coverage figure down.';

revoke all on function public.margin_summary(date, date) from public;
grant execute on function public.margin_summary(date, date) to authenticated;
