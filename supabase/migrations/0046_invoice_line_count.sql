-- ---------------------------------------------------------------------------
-- 0046 · How many lines an invoice has, on the invoice itself.
--
-- "I confirmed two rows and the Invoices page shows one" has now been reported
-- five times. The lines were never lost -- the payload sends every row, the
-- insert is one bulk call, and this view's own amount_total has summed BOTH of
-- them every time (1,000 + 2,000 reading 3.00 K on the list). What the page
-- could not do was SAY so. The count lived only in a detail drawer that opens
-- on selection and stands down entirely on a narrow window, so the list itself
-- showed an invoice of two exactly as it showed an invoice of one.
--
-- count(*) over sales_line, not over v_sales_line: the view adds three joins to
-- answer a question about rows on one table.
--
-- FREE. The `c` lateral below already computes lines_total for cost_coverage --
-- it has since 0019 -- and this only selects what was being thrown away. No new
-- scan, no new join, no index to add.
--
-- Everything else is the definition as it stood, byte for byte. create or
-- replace view cannot rename, reorder or retype a column, so the existing ones
-- have to survive untouched and the new one has to go LAST.
-- ---------------------------------------------------------------------------


CREATE OR REPLACE VIEW "public"."v_invoice" WITH ("security_invoker"='on') AS
 SELECT "i"."invoice_id",
    "i"."invoice_no",
    "i"."invoice_date",
    "i"."buyer_id",
    "b"."name" AS "buyer_name",
    "b"."credit_limit",
    "i"."broker_id",
    "br"."name" AS "broker_name",
    "i"."broker_pct",
    "i"."terms_days",
    "i"."doc_type",
    "i"."status",
    "i"."created_by",
    "p"."full_name" AS "salesperson",
    COALESCE("t"."amount_total", (0)::numeric) AS "amount_total",
    COALESCE("t"."carats_sold", (0)::numeric) AS "carats_sold",
    COALESCE("r"."received", (0)::numeric) AS "received",
        CASE
            WHEN (("i"."status")::"text" = 'CANCELLED'::"text") THEN (0)::numeric
            ELSE "round"((COALESCE("t"."amount_total", (0)::numeric) - COALESCE("r"."received", (0)::numeric)), 2)
        END AS "outstanding",
        CASE
            WHEN (COALESCE("t"."carats_sold", (0)::numeric) > (0)::numeric) THEN (COALESCE("t"."amount_total", (0)::numeric) / "t"."carats_sold")
            ELSE (0)::numeric
        END AS "blended_rate",
    "round"(((COALESCE("t"."amount_pre_broker", (0)::numeric) * "i"."broker_pct") / (100)::numeric), 2) AS "broker_payable",
    ("i"."invoice_date" + "i"."terms_days") AS "due_date",
    ((CURRENT_DATE > ("i"."invoice_date" + "i"."terms_days")) AND ((COALESCE("t"."amount_total", (0)::numeric) - COALESCE("r"."received", (0)::numeric)) > 0.01) AND (("i"."status")::"text" = 'POSTED'::"text")) AS "is_overdue",
    GREATEST(0, (CURRENT_DATE - ("i"."invoice_date" + "i"."terms_days"))) AS "days_overdue",
    "i"."created_at",
    "i"."updated_at",
        CASE
            WHEN (("c"."lines_total" > 0) AND ("c"."lines_costed" = "c"."lines_total")) THEN "round"("c"."cost_total", 2)
            ELSE NULL::numeric
        END AS "cost_total",
        CASE
            WHEN (("i"."status")::"text" = 'CANCELLED'::"text") THEN NULL::numeric
            WHEN (("c"."lines_total" > 0) AND ("c"."lines_costed" = "c"."lines_total")) THEN "round"((COALESCE("t"."amount_total", (0)::numeric) - "c"."cost_total"), 2)
            ELSE NULL::numeric
        END AS "margin",
        CASE
            WHEN ("c"."lines_total" > 0) THEN "round"((("c"."lines_costed")::numeric / ("c"."lines_total")::numeric), 4)
            ELSE (0)::numeric
        END AS "cost_coverage",
    COALESCE("c"."lines_total", (0)::bigint) AS "line_count"
   FROM (((((("public"."sales_invoice" "i"
     JOIN "public"."buyer" "b" ON (("b"."buyer_id" = "i"."buyer_id")))
     LEFT JOIN "public"."broker" "br" ON (("br"."broker_id" = "i"."broker_id")))
     LEFT JOIN "public"."profiles" "p" ON (("p"."id" = "i"."created_by")))
     LEFT JOIN LATERAL ( SELECT "sum"("vl"."amount") AS "amount_total",
            "sum"("vl"."amount_pre_broker") AS "amount_pre_broker",
            "sum"("vl"."selection_ct") AS "carats_sold"
           FROM "public"."v_sales_line" "vl"
          WHERE ("vl"."invoice_id" = "i"."invoice_id")) "t" ON (true))
     LEFT JOIN LATERAL ( SELECT "sum"("rc"."amount") AS "received"
           FROM "public"."receipt" "rc"
          WHERE ("rc"."invoice_id" = "i"."invoice_id")) "r" ON (true))
     LEFT JOIN LATERAL ( SELECT "count"(*) AS "lines_total",
            "count"("sl"."cost_per_ct") AS "lines_costed",
            "sum"(("sl"."cost_per_ct" * "sl"."gross_weight_ct")) AS "cost_total"
           FROM "public"."sales_line" "sl"
          WHERE ("sl"."invoice_id" = "i"."invoice_id")) "c" ON (true));

    