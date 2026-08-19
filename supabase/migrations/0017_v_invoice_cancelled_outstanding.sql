-- ---------------------------------------------------------------------------
-- 0017 · A cancelled invoice owes nothing.
--
-- `outstanding` was amount_total - received with no regard for status, so a
-- CANCELLED invoice kept whatever it owed on the day it was cancelled. Three
-- screens then disagreed about the same invoice: dashboard_summary floors each
-- one at zero, v_receivables_ageing counts POSTED only, and the Invoices page
-- summed the raw figure -- which is where the 120.27 mismatch came from.
--
-- Fixed here rather than in the client, because Desktop, Android and anything
-- reading the view over PostgREST all have to agree. The desktop app carried an
-- InvoiceMath.Due workaround for this; it becomes dead once this is applied.
--
-- Everything below is the definition as it stood, byte for byte, except the
-- `outstanding` expression. create or replace view cannot rename, reorder or
-- retype a column, so the rest has to survive untouched -- and round(numeric,2)
-- and 0::numeric are both numeric, which keeps the replace legal.
--
-- v_receivables_ageing reads FROM v_invoice and already filters to POSTED, so
-- it inherits this and needs no migration of its own.
--
-- is_overdue (below) already carried its own status = 'POSTED' test, which is
-- why cancelled invoices never showed as overdue even while they showed a debt.
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
    i.updated_at
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
          WHERE rc.invoice_id = i.invoice_id) r ON true;
