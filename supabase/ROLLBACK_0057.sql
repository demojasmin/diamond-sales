-- ---------------------------------------------------------------------------
-- Undo 0057: FL and COL come back as their own grades, with the same ids, and every row 0057
-- moved onto II goes back to the grade it came from. Reads public.merge_0057_*, which 0057 wrote.
-- Rows created AFTER 0057 (new sales, imports) stay on II -- there is no way to know which of the
-- three they meant.
-- One transaction; refuses if 0057's record is missing.
-- ---------------------------------------------------------------------------
begin;

do $$
begin
    if not exists (select 1 from public.merge_0057_grade) then
        raise exception 'ROLLBACK_0057: no record of 0057 to undo. Nothing was changed.';
    end if;
end;
$$;

-- The two grades, as they were (same ids, so moved rows can point at them again).
insert into public.grade overriding system value
select * from public.merge_0057_grade where code in ('FL', 'COL')
on conflict (grade_id) do nothing;

-- NO II's own name, order and aliases, as they were.
update public.grade g
   set display_name = o.display_name, sort_order = o.sort_order, aliases = o.aliases
  from public.merge_0057_grade o
 where o.grade_id = g.grade_id and o.code = 'NO II';

-- Pairings, as they were.
insert into public.grade_size select * from public.merge_0057_grade_size on conflict do nothing;

update public.stock_movement t set grade_id = m.old_grade_id from public.merge_0057_moved m
 where m.tbl = 'stock_movement' and m.col = 'grade_id' and t.movement_id = m.row_id;
update public.stock_movement t set counterparty_grade_id = m.old_grade_id from public.merge_0057_moved m
 where m.tbl = 'stock_movement' and m.col = 'counterparty_grade_id' and t.movement_id = m.row_id;
update public.sales_line t set grade_id = m.old_grade_id from public.merge_0057_moved m
 where m.tbl = 'sales_line' and t.line_id = m.row_id;
update public.rough_intake t set grade_id = m.old_grade_id from public.merge_0057_moved m
 where m.tbl = 'rough_intake' and t.intake_id = m.row_id;
update public.stock_reservation t set grade_id = m.old_grade_id from public.merge_0057_moved m
 where m.tbl = 'stock_reservation' and t.reservation_id = m.row_id;
update public.rejection_disposition t set to_grade_id = m.old_grade_id from public.merge_0057_moved m
 where m.tbl = 'rejection_disposition' and t.disposition_id = m.row_id;
update public.price_list t set grade_id = m.old_grade_id from public.merge_0057_moved m
 where m.tbl = 'price_list' and t.price_id = m.row_id;

-- II's pairings 0057 added, removed again where nothing uses them.
delete from public.grade_size gs
 using public.grade g
 where g.grade_id = gs.grade_id and g.code = 'NO II'
   and not exists (select 1 from public.merge_0057_grade_size o
                    where o.grade_id = gs.grade_id and o.size_id = gs.size_id)
   and not exists (select 1 from public.stock_movement m where m.grade_id = gs.grade_id and m.size_id = gs.size_id)
   and not exists (select 1 from public.sales_line l where l.grade_id = gs.grade_id and l.size_id = gs.size_id);

drop table public.merge_0057_moved, public.merge_0057_grade_size, public.merge_0057_grade;

commit;

select code, display_name, sort_order, aliases from public.grade where code in ('NO II', 'FL', 'COL');
