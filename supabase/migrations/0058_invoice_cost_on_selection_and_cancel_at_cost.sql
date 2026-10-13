-- ---------------------------------------------------------------------------
-- 0058. Two invoice calculations that 0055 left behind.
--
-- 1 · INVOICE COST IS CHARGED ON THE CARATS SOLD, NOT THE WHOLE PARCEL.
--     v_invoice.cost_total was cost_per_ct x gross_weight_ct (0019/0020): "the whole parcel left
--     stock". Since 0055 the rejected carats never leave -- they stay in stock and in its value --
--     so charging them to the sale counted them twice and turned a profitable invoice into a loss
--     (Priya INV-2026-00002: 189.70 ct charged, 78.24 ct sold, margin -37,03,183.69 instead of
--     +54,463.95). Now cost_per_ct x selection_ct, the same carats revenue is charged on.
--     Only the view changes; no row is touched. margin and margin_summary follow from it.
--
-- 2 · A CANCELLED INVOICE RETURNS ITS STONES AT WHAT THEY COST, NOT WHAT THEY SOLD FOR.
--     cancel_invoice put the sold carats back as a positive ADJUST priced at the SALE movement's
--     price -- the selling price, in the invoice's currency. Every positive ADJUST counts as a
--     purchase in avg_cost, so a cancellation bought the stones back at the selling price: a
--     rupee sale pushed the bucket's average cost up by the margin, a dollar sale ($285 x 95.66)
--     came back at Rs 285 a carat and collapsed it. Now each line comes back at the cost stamped
--     on it when it was posted (sales_line.cost_per_ct); a line with no stamp (imported sales)
--     comes back at the bucket's current average cost. The pre-0055 rejection-hold reversal had
--     the same flaw with no price at all (free stones, average pulled down) and now also comes
--     back at the bucket's average cost.
--     Existing cancellation rows are NOT rewritten.
--
-- Nothing else in either object changes. Columns of v_invoice are unchanged in name and order.
-- ---------------------------------------------------------------------------
begin;

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
    ((CURRENT_DATE > (i.invoice_date + i.terms_days)) AND ((COALESCE(t.amount_total, (0)::numeric) - COALESCE(r.received, (0)::numeric)) > 0.01) AND ((i.status)::text = 'POSTED'::text)) AS is_overdue,
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
        END AS cost_coverage,
    COALESCE(c.lines_total, (0)::bigint) AS line_count
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
            sum((sl.cost_per_ct * sl.selection_ct)) AS cost_total       -- 0058: was gross_weight_ct
           FROM sales_line sl
          WHERE (sl.invoice_id = i.invoice_id)) c ON (true));

create or replace function public.cancel_invoice(p_invoice_id bigint, p_reason text default null::text)
 returns jsonb
 language plpgsql
as $function$
declare
    v_status text;
begin
    select status into v_status
      from public.sales_invoice where invoice_id = p_invoice_id;

    if not found then
        raise exception 'Invoice % not found', p_invoice_id using errcode = 'no_data_found';
    end if;

    if v_status = 'CANCELLED' then
        return jsonb_build_object('ok', true, 'already_cancelled', true);
    end if;

    if p_reason is null or btrim(p_reason) = '' then
        raise exception 'Cancelling an invoice requires a reason';
    end if;

    if v_status = 'POSTED' then
        insert into public.stock_movement
            (movement_date, grade_id, size_id, movement_type, weight_ct,
             price_per_ct, ref_type, ref_id, created_by, reason)
        -- 0058: back at what the stones COST -- the stamp from posting, else the bucket's average.
        -- Never the selling price, which is a sale figure and may be in another currency.
        select current_date, m.grade_id, m.size_id, 'ADJUST', m.weight_ct,
               coalesce(l.cost_per_ct, nullif(sp.avg_cost, 0)), 'cancel', p_invoice_id, auth.uid(),
               'Reversal of invoice ' || p_invoice_id || ': ' || p_reason
          from public.stock_movement m
          join public.sales_line l on l.line_id = m.ref_id
          left join public.v_stock_position sp on sp.grade_id = m.grade_id and sp.size_id = m.size_id
         where m.ref_type = 'sales_line'
           and l.invoice_id = p_invoice_id
           and m.movement_type = 'SALE'                       -- 0055: the rejection never left
        union all
        select current_date, h.grade_id, h.size_id, 'ADJUST', -h.weight_ct,
               case when -h.weight_ct > 0 then nullif(sp.avg_cost, 0) end,   -- 0058: was null (free stones)
               'cancel', p_invoice_id, auth.uid(),
               'Reversal of invoice ' || p_invoice_id || ' (its pre-0055 rejection hold): ' || p_reason
          from public.stock_movement h
          left join public.v_stock_position sp on sp.grade_id = h.grade_id and sp.size_id = h.size_id
         where h.ref_type = 'rejection_hold'
           and h.ref_id = p_invoice_id;
    end if;

    update public.sales_invoice
       set status = 'CANCELLED', updated_by = auth.uid()
     where invoice_id = p_invoice_id;

    return jsonb_build_object('ok', true, 'reason', p_reason);
end;
$function$;

commit;
