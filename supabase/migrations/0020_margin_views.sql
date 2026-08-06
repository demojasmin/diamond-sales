-- ---------------------------------------------------------------------------
-- 0020 · Margin, exposed.
--
-- Deliberately additive. v_sales_line and dashboard_summary are NOT touched:
-- their definitions live in 0002, which is version-controlled in the Android
-- repository and not here, so a create-or-replace written from a guessed body
-- would silently revert whatever they actually say. margin_summary() is a new
-- function instead, and the per-line cost is reached through a lateral on
-- sales_line rather than by widening v_sales_line.
--
-- v_invoice is the one existing object that changes, and only because its exact
-- current text is known (0017). Everything below it is byte-identical to that
-- migration except the three new columns at the end -- create or replace view
-- cannot reorder or retype, so appending is the only safe shape.
--
-- Cost is charged on gross_weight_ct to match the stamp in 0019.
--
-- Null never coalesces to zero. An invoice with no cost basis reports margin
-- null, not margin = revenue. Reporting a 100% margin on an unpriced parcel is
-- the one way this feature could actively mislead, so the null is load-bearing:
-- cost_coverage exists so a caller can tell "no margin" from "no data".
-- ---------------------------------------------------------------------------
create or replace view public.v_invoice as
 SELECT i.invoice_id,
    i.invoice_no,
    i.invoice_date,
    i.buyer_id,
    b.name AS buyer_name,
    b.credit_limit,
    i.broker_id,
    br.name AS broker_name,
    i.broker_pct,
    i.terms_days,
    i.doc_type,
    i.status,
    i.created_by,
    p.full_name AS salesperson,
    COALESCE(t.amount_total, 0::numeric) AS amount_total,
    COALESCE(t.carats_sold, 0::numeric) AS carats_sold,
    COALESCE(r.received, 0::numeric) AS received,
        CASE
            WHEN i.status::text = 'CANCELLED'::text THEN 0::numeric
            ELSE round(COALESCE(t.amount_total, 0::numeric) - COALESCE(r.received, 0::numeric), 2)
        END AS outstanding,
        CASE
            WHEN COALESCE(t.carats_sold, 0::numeric) > 0::numeric THEN COALESCE(t.amount_total, 0::numeric) / t.carats_sold
            ELSE 0::numeric
        END AS blended_rate,
    round(COALESCE(t.amount_pre_broker, 0::numeric) * i.broker_pct / 100::numeric, 2) AS broker_payable,
    i.invoice_date + i.terms_days AS due_date,
    CURRENT_DATE > (i.invoice_date + i.terms_days) AND (COALESCE(t.amount_total, 0::numeric) - COALESCE(r.received, 0::numeric)) > 0.01 AND i.status::text = 'POSTED'::text AS is_overdue,
    GREATEST(0, CURRENT_DATE - (i.invoice_date + i.terms_days)) AS days_overdue,
    i.created_at,
    i.updated_at,
    -- ── margin · 0020 ─────────────────────────────────────────────────────
    -- Null unless EVERY line carries a cost. A partial cost would understate
    -- COGS and overstate margin, which is worse than declining to answer.
        CASE
            WHEN c.lines_total > 0 AND c.lines_costed = c.lines_total
            THEN round(c.cost_total, 2)
            ELSE NULL::numeric
        END AS cost_total,
        CASE
            WHEN i.status::text = 'CANCELLED'::text THEN NULL::numeric
            WHEN c.lines_total > 0 AND c.lines_costed = c.lines_total
            THEN round(COALESCE(t.amount_total, 0::numeric) - c.cost_total, 2)
            ELSE NULL::numeric
        END AS margin,
        CASE
            WHEN c.lines_total > 0 THEN round(c.lines_costed::numeric / c.lines_total, 4)
            ELSE 0::numeric
        END AS cost_coverage
   FROM sales_invoice i
     JOIN buyer b ON b.buyer_id = i.buyer_id
     LEFT JOIN broker br ON br.broker_id = i.broker_id
     LEFT JOIN profiles p ON p.id = i.created_by
     LEFT JOIN LATERAL ( SELECT sum(vl.amount) AS amount_total,
            sum(vl.amount_pre_broker) AS amount_pre_broker,
            sum(vl.selection_ct) AS carats_sold
           FROM v_sales_line vl
          WHERE vl.invoice_id = i.invoice_id) t ON true
     LEFT JOIN LATERAL ( SELECT sum(rc.amount) AS received
           FROM receipt rc
          WHERE rc.invoice_id = i.invoice_id) r ON true
     LEFT JOIN LATERAL ( SELECT count(*) AS lines_total,
            count(sl.cost_per_ct) AS lines_costed,
            sum(sl.cost_per_ct * sl.gross_weight_ct) AS cost_total
           FROM sales_line sl
          WHERE sl.invoice_id = i.invoice_id) c ON true;

-- ---------------------------------------------------------------------------
-- The dashboard tile.
--
-- Separate from dashboard_summary rather than folded into it: that function's
-- body is not in this repository, and one KPI is not worth the risk of
-- rewriting it from a guess. The desktop calls both.
--
-- POSTED only, matching every other money figure on the dashboard. Cancelled
-- invoices contribute nothing, consistent with 0017.
--
-- Both counts are returned so the caller can render honestly. A margin computed
-- over 3 of 1,438 invoices is not wrong, but shown without its denominator it
-- reads as the whole book.
-- ---------------------------------------------------------------------------
create or replace function public.margin_summary(
    p_from date default null,
    p_to   date default null
)
returns table (
    revenue_total    numeric,
    cost_total       numeric,
    margin_total     numeric,
    margin_pct       numeric,
    invoices_costed  bigint,
    invoices_total   bigint
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
    costed as (
        select * from scoped where margin is not null
    )
    select
        round(coalesce(sum(c.amount_total), 0), 2),
        round(coalesce(sum(c.cost_total),   0), 2),
        round(coalesce(sum(c.margin),       0), 2),
        -- Margin over the revenue it was actually computed from, never over the
        -- whole book: dividing a partial margin by total revenue understates it.
        case when coalesce(sum(c.amount_total), 0) > 0
             then round(100 * sum(c.margin) / sum(c.amount_total), 2)
             else 0 end,
        (select count(*) from costed),
        (select count(*) from scoped)
      from costed c;
$$;

comment on function public.margin_summary(date, date) is 'Revenue, cost and margin over POSTED invoices in a date range, with the count of invoices carrying a cost basis alongside the total so a caller can show coverage. Invoices without cost (migrated MIG- rows, which never wrote stock movements) are excluded rather than counted as pure margin.';

revoke all on function public.margin_summary(date, date) from public;
grant execute on function public.margin_summary(date, date) to authenticated;
