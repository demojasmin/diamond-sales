-- ---------------------------------------------------------------------------
-- Preview of the historical rejection correction · READ ONLY.
--
-- One SELECT. Nothing is written. Works before and after 0055 (it reads the REJECTION and SALE
-- rows, which 0055 keeps, and the 0055 holds when they exist).
--
-- Set sheet_date below to the date the client confirms the stock PDFs describe. A sale dated ON
-- OR BEFORE it was already off the shelf when the sheet was printed; one dated after it was not.
--
-- Per grade x size, it shows what each correction option would add back. It APPLIES NOTHING.
-- See supabase/REJECTION_CORRECTION_PLAN.md for what each column means and which needs whose
-- confirmation.
-- ---------------------------------------------------------------------------
with params as (
    select date '2026-08-18' as sheet_date          -- <-- confirm with the client
),
lines as (
    select l.grade_id, l.size_id, i.invoice_date, i.invoice_no,
           coalesce(sum(m.weight_ct) filter (where m.movement_type = 'SALE'), 0)      as sale_out,
           coalesce(sum(m.weight_ct) filter (where m.movement_type = 'REJECTION'), 0) as rej_out
      from public.sales_line l
      join public.sales_invoice i  on i.invoice_id = l.invoice_id and i.status = 'POSTED'
      left join public.stock_movement m on m.ref_type = 'sales_line' and m.ref_id = l.line_id
     group by l.line_id, l.grade_id, l.size_id, i.invoice_date, i.invoice_no
),
holds as (
    select grade_id, size_id, -sum(weight_ct) as held
      from public.stock_movement
     where ref_type in ('rejection_hold', 'rej_hold_mvmt')
     group by 1, 2
)
select g.code as grade, s.code as size,
       round(sp.ledger_ct, 4)                                                            as shelf_now,
       round(sum(x.rej_out), 4)                                                          as rejected_total,
       round(coalesce(min(h.held), 0), 4)                                                as held_by_0055,
       -- Option A: rejected stones came back to this bucket -> every rejection is added back.
       round(sum(x.rej_out), 4)                                                          as a_add_back_all_rejections,
       -- Option B: rejected stones came back, and the sheet is a count printed on sheet_date ->
       -- before it, BOTH the sale and the rejection were already reflected in the count.
       round(sum(x.rej_out) + coalesce(sum(x.sale_out) filter (where x.invoice_date <= p.sheet_date), 0), 4)
                                                                                         as b_add_back_rej_and_presheet_sales,
       -- Option C: rejected stones did NOT come back; only the pre-sheet sales were deducted twice.
       round(coalesce(sum(x.sale_out + x.rej_out) filter (where x.invoice_date <= p.sheet_date), 0), 4)
                                                                                         as c_add_back_presheet_sales_only
  from lines x
  cross join params p
  join public.grade g       on g.grade_id = x.grade_id
  join public.size_bucket s on s.size_id  = x.size_id
  join public.v_stock_position sp on sp.grade_id = x.grade_id and sp.size_id = x.size_id
  left join holds h on h.grade_id = x.grade_id and h.size_id = x.size_id
 group by g.code, s.code, sp.ledger_ct, g.sort_order, s.sort_order
having sum(x.rej_out) > 0 or sum(x.sale_out) filter (where x.invoice_date <= (select sheet_date from params)) > 0
 order by g.sort_order, s.sort_order;
