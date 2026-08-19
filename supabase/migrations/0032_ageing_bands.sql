-- ===========================================================================
-- 0032 · CALC-AGE — a band for money that is not due yet, and one settled
--        threshold of fifty paise.
--
-- This file was written as 0007_ageing_bands.sql in the Android repository and
-- moved here unchanged below the header. It belongs in this series: these
-- migrations are the incremental history of the live database, while the
-- Android repo's 0001-0006 build a database from scratch. Two files numbered
-- 0007 for one database is how the wrong one gets applied.
--
-- Numbered 0032 because 0027-0030 are stock import batches, the cost-adjust
-- cleanup, the FL/1MB/-2MB grades and add_grade(), and 0031 is the anon-write
-- revoke sitting one directory up. None of the five touches either view here.
--
-- REBUILT FROM THE DEPLOYED DEFINITIONS, not from any migration in either repo.
--
-- The first attempt was written from repository text and failed with
-- `42P16 cannot drop columns from view`. The repository no longer describes this
-- database: live v_invoice carries 28 columns to the Android bootstrap's 25
-- (cost_total, margin, cost_coverage, added by 0020_margin_views.sql and fed by
-- a sixth lateral join over sales_line.cost_per_ct) and computes `outstanding`
-- as 0 for a CANCELLED invoice (0017). Replacing it with the older text would
-- have dropped three columns and silently reverted that rule.
--
-- So both views below are the output of `pg_get_viewdef(..., true)` taken from
-- this database on 12/08/2026, with only these lines changed:
--
--   v_invoice             is_overdue threshold   > 0.01  ->  > 0.50
--   v_receivables_ageing  new first band         days_overdue <= 0 -> 'not due'
--   v_receivables_ageing  renamed band           '0-30'  ->  '1-30'
--   v_receivables_ageing  open-book threshold    > 0.01  ->  > 0.50
--
-- Every column of both views keeps its name, type and ordinal position, because
-- the column lists are not retyped here at all.
--
-- STILL CURRENT AS OF 0031. The capture predates 0026-0031, and none of them
-- redefines either view: 0026 rebuilds v_reconciliation, 0027 adds
-- v_stock_import_batch and import_stock(), 0028 adds adjust_stock_at_cost(),
-- 0029/0030 are grade rows and add_grade(), 0031 revokes anon writes. 0020 is
-- still the last migration to redefine v_invoice, and its text was diffed
-- against this one: every difference is pg_get_viewdef's parenthesisation, not
-- a change of meaning.
--
-- WHAT THIS FIXES
--
-- 1. `days_overdue` floors at zero, so an invoice due next month and one thirty
--    days late both arrive as 0 and are filed together under '0-30'. On this
--    book that is 53 invoices worth 21,07,06,865.02 sitting beside 27 invoices
--    worth 9,56,90,062.23 that are genuinely late — 69% of the band — on a page
--    headed "Ageing and collections".
--
-- 2. Three different thresholds were live for "still owed": these views used
--    > 0.01, DiamondCalc.Calc.IsOverdue used > 0, and Android used 0.50. The two
--    clients now both say 0.50; this file is the third. Measured on 12/08/2026,
--    no posted invoice owes between 0.01 and 0.50, so the substitution moves no
--    money today — it stops the three drifting tomorrow.
--
-- DO NOT APPLY THIS ALONE. It was applied on 13/08/2026 and rolled back the same
-- day: the desktop build in the field (SolitaireDesk-Setup-1.0.1.0.exe, built
-- 07/08/2026) reads the band names off this view and matched none of them, so
-- the whole receivables book fell out of the tiles. The five-tile Receivables
-- page exists in DiamondDesktop (MainWindow.xaml.cs, commit 0529371) but is in
-- no released installer. Ship that installer first, or in the same window.
--
-- SCOPE. Two `create or replace view` statements and their grants. No INSERT,
-- UPDATE, DELETE, DROP, ALTER TABLE or TRUNCATE — it cannot change a business
-- record. dashboard_summary() is deliberately NOT included; it follows in 0033.
--
-- ROLLBACK. Re-run this file with 0.50 -> 0.01 and the 'not due' branch removed,
-- or restore from the pg_get_viewdef output captured before applying.
--
-- The band names are a three-way contract, asserted against the same nine
-- boundary inputs in the Android repo's supabase/VERIFY_AGEING.sql,
-- DiamondCalc/Calc.cs::AgeBucket and domain/Calc.kt::ageingBucket.
-- ===========================================================================
-- Deterministic resolution of the unqualified table names below, which are how
-- pg_get_viewdef renders them.
set local search_path = public, pg_temp;

-- ---------------------------------------------------------------------------
-- v_invoice · deployed text, one line changed (is_overdue threshold).
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
    COALESCE(t.amount_total, (0)::numeric) AS amount_total,
    COALESCE(t.carats_sold, (0)::numeric) AS carats_sold,
    COALESCE(r.received, (0)::numeric) AS received,
        CASE
            WHEN ((i.status)::text = 'CANCELLED'::text) THEN (0)::numeric
            ELSE round((COALESCE(t.amount_total, (0)::numeric) - COALESCE(r.received, (0)::numeric)), 2)
        END AS outstanding,
        CASE
            WHEN (COALESCE(t.carats_sold, (0)::numeric) > (0)::numeric) THEN (COALESCE(t.amount_total, (0)::numeric) / t.carats_sold)
            ELSE (0)::numeric
        END AS blended_rate,
    round(((COALESCE(t.amount_pre_broker, (0)::numeric) * i.broker_pct) / (100)::numeric), 2) AS broker_payable,
    (i.invoice_date + i.terms_days) AS due_date,
    ((CURRENT_DATE > (i.invoice_date + i.terms_days)) AND ((COALESCE(t.amount_total, (0)::numeric) - COALESCE(r.received, (0)::numeric)) > 0.50) AND ((i.status)::text = 'POSTED'::text)) AS is_overdue,
    GREATEST(0, (CURRENT_DATE - (i.invoice_date + i.terms_days))) AS days_overdue,
    i.created_at,
    i.updated_at,
        CASE
            WHEN ((c.lines_total > 0) AND (c.lines_costed = c.lines_total)) THEN round(c.cost_total, 2)
            ELSE NULL::numeric
        END AS cost_total,
        CASE
            WHEN ((i.status)::text = 'CANCELLED'::text) THEN NULL::numeric
            WHEN ((c.lines_total > 0) AND (c.lines_costed = c.lines_total)) THEN round((COALESCE(t.amount_total, (0)::numeric) - c.cost_total), 2)
            ELSE NULL::numeric
        END AS margin,
        CASE
            WHEN (c.lines_total > 0) THEN round(((c.lines_costed)::numeric / (c.lines_total)::numeric), 4)
            ELSE (0)::numeric
        END AS cost_coverage
   FROM ((((((sales_invoice i
     JOIN buyer b ON ((b.buyer_id = i.buyer_id)))
     LEFT JOIN broker br ON ((br.broker_id = i.broker_id)))
     LEFT JOIN profiles p ON ((p.id = i.created_by)))
     LEFT JOIN LATERAL ( SELECT sum(vl.amount) AS amount_total,
            sum(vl.amount_pre_broker) AS amount_pre_broker,
            sum(vl.selection_ct) AS carats_sold
           FROM v_sales_line vl
          WHERE (vl.invoice_id = i.invoice_id)) t ON (true))
     LEFT JOIN LATERAL ( SELECT sum(rc.amount) AS received
           FROM receipt rc
          WHERE (rc.invoice_id = i.invoice_id)) r ON (true))
     LEFT JOIN LATERAL ( SELECT count(*) AS lines_total,
            count(sl.cost_per_ct) AS lines_costed,
            sum((sl.cost_per_ct * sl.gross_weight_ct)) AS cost_total
           FROM sales_line sl
          WHERE (sl.invoice_id = i.invoice_id)) c ON (true));

-- ---------------------------------------------------------------------------
-- v_receivables_ageing · deployed text, the CASE gains a first band and the
-- open-book threshold moves to fifty paise.
-- ---------------------------------------------------------------------------
create or replace view public.v_receivables_ageing as
 SELECT invoice_id,
    invoice_no,
    buyer_id,
    buyer_name,
    outstanding,
    due_date,
    days_overdue,
    is_overdue,
        CASE
            WHEN days_overdue <= 0 THEN 'not due'::text
            WHEN days_overdue <= 30 THEN '1-30'::text
            WHEN days_overdue <= 60 THEN '31-60'::text
            WHEN days_overdue <= 90 THEN '61-90'::text
            ELSE '90+'::text
        END AS age_bucket
   FROM v_invoice v
  WHERE status::text = 'POSTED'::text AND outstanding > 0.50;

-- ---------------------------------------------------------------------------
-- Security posture, re-applied.
--
-- `create or replace view` preserves ownership, ACLs and reloptions, so these
-- are belt-and-braces rather than strictly required — and cheap insurance
-- against a view silently losing security_invoker, which would run it as its
-- owner and hand every signed-in user the whole book regardless of role.
-- ---------------------------------------------------------------------------
alter view public.v_invoice             set (security_invoker = on);
alter view public.v_receivables_ageing  set (security_invoker = on);

grant select on public.v_invoice            to authenticated;
grant select on public.v_receivables_ageing to authenticated;
revoke all on public.v_invoice            from anon;
revoke all on public.v_receivables_ageing from anon;
