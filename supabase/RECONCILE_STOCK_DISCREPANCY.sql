-- ---------------------------------------------------------------------------
-- Reconcile the Stock page against the stock import, carat for carat.
--
-- READ ONLY. Three SELECTs. Nothing is written, released or deleted.
-- Run each one on its own (select it, then Run) on PRIYA, and send the results.
--
-- The Stock page's TOTAL STOCK is AVAILABLE = shelf (ledger) - reserved. So it
-- falls when a sale is confirmed AND when a Sales entry line is merely typed.
-- These queries split the two apart.
-- ---------------------------------------------------------------------------


-- A . Per grade, from the stock import to the figure on screen.
--     Read across:  at_import + in_since - sold - rejected - converted_out + adjusted
--                   = shelf_ct ;  shelf_ct - reserved_ct = available_ct (what the screen shows)
--     The last row (grade = TOTAL) is the whole stock.
with mv as (
    select g.code as grade,
           sum(case when m.movement_type = 'INTAKE' and m.ref_type = 'stock_import' then m.weight_ct else 0 end) as at_import,
           sum(case when (m.movement_type = 'INTAKE' and coalesce(m.ref_type, '') <> 'stock_import')
                      or m.movement_type = 'CONVERT_IN' then m.weight_ct else 0 end)                         as in_since,
           sum(case when m.movement_type = 'SALE'        then m.weight_ct else 0 end)                         as sold,
           sum(case when m.movement_type = 'REJECTION'   then m.weight_ct else 0 end)                         as rejected,
           sum(case when m.movement_type = 'CONVERT_OUT' then m.weight_ct else 0 end)                         as converted_out,
           sum(case when m.movement_type = 'ADJUST'      then m.weight_ct else 0 end)                         as adjusted
      from public.stock_movement m
      join public.grade g on g.grade_id = m.grade_id
     group by g.code
),
pos as (
    select grade_code as grade, sum(ledger_ct) as shelf_ct, sum(reserved_ct) as reserved_ct,
           sum(balance_ct) as available_ct
      from public.v_stock_position
     group by grade_code
)
select coalesce(p.grade, 'TOTAL')                   as grade,
       round(sum(coalesce(m.at_import, 0)), 4)      as at_import,
       round(sum(coalesce(m.in_since, 0)), 4)       as in_since,
       round(sum(coalesce(m.sold, 0)), 4)           as sold,
       round(sum(coalesce(m.rejected, 0)), 4)       as rejected,
       round(sum(coalesce(m.converted_out, 0)), 4)  as converted_out,
       round(sum(coalesce(m.adjusted, 0)), 4)       as adjusted,
       round(sum(p.shelf_ct), 4)                    as shelf_ct,
       round(sum(p.reserved_ct), 4)                 as reserved_ct,
       round(sum(p.available_ct), 4)                as available_ct
  from pos p
  left join mv m on m.grade = p.grade
 group by rollup (p.grade)
having sum(p.shelf_ct) <> 0 or sum(p.reserved_ct) <> 0 or sum(coalesce(m.at_import, 0)) <> 0
 order by (p.grade is null), p.grade;


-- B . Every reserved carat: which Sales entry holds it, and whether any app still
--     has that entry. An app re-sends its holds each time it starts, which the audit
--     trail records as an UPDATE -- so last_resent shows the last time any copy of
--     the app still had the entry. A hold never re-sent since it was typed is held by
--     an entry no app loads any more.
select r.reservation_id,
       r.client_ref,
       g.code                                 as grade,
       s.code                                 as size,
       r.weight_ct,
       r.created_at                           as typed_at,
       max(a.changed_at) filter (where a.action = 'UPDATE') as last_resent,
       count(*)          filter (where a.action = 'UPDATE') as times_resent,
       p.full_name                            as typed_by
  from public.stock_reservation r
  join public.grade g       on g.grade_id = r.grade_id
  join public.size_bucket s on s.size_id  = r.size_id
  left join public.profiles p on p.id = r.created_by
  left join public.audit_log a on a.table_name = 'stock_reservation' and a.record_id = r.reservation_id
 group by r.reservation_id, r.client_ref, g.code, s.code, r.weight_ct, r.created_at, p.full_name
 order by r.created_at;


-- C . Every movement after the stock import, one row each, newest last. These are
--     the only things that moved the SHELF figure since the sheet was loaded.
select m.movement_id, m.movement_date, m.created_at, m.movement_type, m.ref_type,
       g.code as grade, s.code as size, m.weight_ct,
       i.invoice_no, i.invoice_date, i.status, m.reason
  from public.stock_movement m
  join public.grade g       on g.grade_id = m.grade_id
  join public.size_bucket s on s.size_id  = m.size_id
  left join public.sales_line l    on m.ref_type = 'sales_line' and l.line_id = m.ref_id
  left join public.sales_invoice i on i.invoice_id = l.invoice_id
 where coalesce(m.ref_type, '') <> 'stock_import'
   and not (m.ref_type = 'sales_line' and i.invoice_no like 'MIG-%')
 order by m.movement_id;
