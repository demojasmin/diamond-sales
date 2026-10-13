-- ---------------------------------------------------------------------------
-- Where did the carats go?  3900 ct after the sales import -> 3758 ct after
-- more sales were added (142 ct), and LB2 x +6.5 reading lower than expected.
--
-- READ ONLY. Every statement is a SELECT. Nothing is written, no function that
-- writes is called. Safe on the client's live database.
--
-- The SQL editor shows only the LAST result, so run each query on its own:
-- select it, then Run. Paste all six results back.
-- ---------------------------------------------------------------------------


-- 1 . The total three ways. "Available" is what the dashboard shows; it is the
--     ledger LESS what unconfirmed Sales-entry lines are holding.
select round(sum(ledger_ct), 4)   as ledger_ct,
       round(sum(reserved_ct), 4) as held_by_sales_entry_ct,
       round(sum(balance_ct), 4)  as available_ct
  from public.v_stock_position;


-- 2 . Every movement written since the sales import, by kind. These rows plus
--     query 1's held figure must add up to the 142 ct. "Since" = after the last
--     movement the sales import wrote.
select m.movement_type, m.ref_type,
       count(*)                      as rows,
       round(sum(m.weight_ct), 4)    as weight_ct
  from public.stock_movement m
 where m.movement_id > (select coalesce(max(mm.movement_id), 0)
                          from public.stock_movement mm
                          join public.sales_line l on l.line_id = mm.ref_id
                          join public.sales_invoice i on i.invoice_id = l.invoice_id
                         where mm.ref_type = 'sales_line' and i.invoice_no like 'MIG-%')
 group by 1, 2
 order by 1, 2;


-- 3 . What unconfirmed Sales-entry lines are holding, and since when. A row here
--     whose entry is not open on any desk is carats stranded out of stock.
select r.client_ref, g.code as grade, s.code as size, r.weight_ct, r.created_at
  from public.stock_reservation r
  join public.grade g       on g.grade_id = r.grade_id
  join public.size_bucket s on s.size_id  = r.size_id
 order by r.created_at;


-- 4 . Is LB2 one grade or several? Two rows here means the stock sits under one
--     and the sale was entered against the other.
select g.grade_id, g.code, g.display_name, g.aliases, g.active
  from public.grade g
 where upper(regexp_replace(g.code, '[^a-zA-Z0-9]', '', 'g')) like 'LB%'
    or upper(coalesce(g.aliases, '')) like '%LB%'
 order by g.code;


-- 5 . Every LB2 bucket: ledger, held, available. Shows whether the sale went to
--     +6.5 or to a neighbouring sieve.
select sp.grade_code, sp.size_code, sp.ledger_ct, sp.reserved_ct, sp.balance_ct
  from public.v_stock_position sp
 where upper(regexp_replace(sp.grade_code, '[^a-zA-Z0-9]', '', 'g')) = 'LB2'
   and (sp.ledger_ct <> 0 or sp.reserved_ct <> 0)
 order by sp.size_code;


-- 6 . The full LB2 x +6.5 ledger, oldest first, with the invoice behind each
--     movement and a running balance. The last running_ct is what the screen shows
--     before holds.
select m.movement_id, m.movement_date, m.movement_type, m.ref_type,
       m.weight_ct, i.invoice_no, i.status, l.gross_weight_ct, l.selection_ct,
       l.rejection_ct, m.reason,
       round(sum(case when m.movement_type in ('INTAKE', 'CONVERT_IN', 'ADJUST')
                      then m.weight_ct else -m.weight_ct end)
             over (order by m.movement_id), 4) as running_ct
  from public.stock_movement m
  join public.grade g       on g.grade_id = m.grade_id
  join public.size_bucket s on s.size_id  = m.size_id
  left join public.sales_line l    on m.ref_type = 'sales_line' and l.line_id = m.ref_id
  left join public.sales_invoice i on i.invoice_id = coalesce(l.invoice_id,
                                       case when m.ref_type in ('import_edit', 'cancel') then m.ref_id end)
 where upper(regexp_replace(g.code, '[^a-zA-Z0-9]', '', 'g')) = 'LB2'
   and s.code in ('+6.5', '6.5+', '6.5')
 order by m.movement_id;
