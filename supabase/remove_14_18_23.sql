-- ---------------------------------------------------------------------------
-- Remove the grades "+14", "'+18", "+23" and the sieve sizes "14+", "+18", "+23".
--
-- WHY. They hold nothing: no stock movement, no intake parcel, no sales line, no hold, no price,
-- no rejection destination (checked on Priya 2026-10-12). The three GRADES are sieve headings a
-- stock sheet was read as grade names; the three SIZES were never stocked. Both clutter the Stock
-- report and every picker.
--
-- SAFE BY CONSTRUCTION. One transaction. It first proves every one of the six is still unused
-- everywhere, and refuses with a message -- changing nothing -- if any of them has picked up a row
-- since. Only then does it remove their grade x size pairings and the six catalogue rows.
-- By code, not id, so it means the same thing on Priya and on Demo. Safe to run twice: the second
-- run finds nothing to remove.
-- ---------------------------------------------------------------------------
begin;

create temporary table rm_grades on commit drop as
select grade_id, code from public.grade where code in ('+14', '''+18', '+23');

create temporary table rm_sizes on commit drop as
select size_id, code from public.size_bucket where code in ('14+', '+18', '+23');

do $$
declare
    v_used text;
begin
    select string_agg(what, ', ') into v_used from (
        select 'stock_movement' what from public.stock_movement
         where grade_id in (select grade_id from rm_grades) or size_id in (select size_id from rm_sizes)
            or counterparty_grade_id in (select grade_id from rm_grades)
        union select 'rough_intake' from public.rough_intake
         where grade_id in (select grade_id from rm_grades) or size_id in (select size_id from rm_sizes)
        union select 'sales_line' from public.sales_line
         where grade_id in (select grade_id from rm_grades) or size_id in (select size_id from rm_sizes)
        union select 'stock_reservation' from public.stock_reservation
         where grade_id in (select grade_id from rm_grades) or size_id in (select size_id from rm_sizes)
        union select 'price_list' from public.price_list
         where grade_id in (select grade_id from rm_grades) or size_id in (select size_id from rm_sizes)
        union select 'rejection_disposition' from public.rejection_disposition
         where to_grade_id in (select grade_id from rm_grades)
    ) used;

    if v_used is not null then
        raise exception 'Not removed: these grades or sizes are now used in %. Nothing was changed.', v_used;
    end if;
end;
$$;

delete from public.grade_size
 where grade_id in (select grade_id from rm_grades) or size_id in (select size_id from rm_sizes);
delete from public.grade       where grade_id in (select grade_id from rm_grades);
delete from public.size_bucket where size_id  in (select size_id  from rm_sizes);

select (select count(*) from rm_grades) as grades_removed,
       (select count(*) from rm_sizes)  as sizes_removed;

commit;

-- Verification: both must be 0.
select (select count(*) from public.grade where code in ('+14', '''+18', '+23'))      as grades_left,
       (select count(*) from public.size_bucket where code in ('14+', '+18', '+23'))  as sizes_left;
