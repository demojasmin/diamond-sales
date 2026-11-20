-- ---------------------------------------------------------------------------
-- Save what II held at the 0057 merge, split back into FL, # (NO II) and Color, so the Stock
-- report can show it on hover. Run once per database, AFTER 0057 (Demo first, then Priya).
--
-- WHY A SNAPSHOT. Since 0057 FL, # and Color are one grade: every sale and adjustment lands on II
-- and nothing records which of the three it came from, so a live split cannot exist. What 0057
-- DID record (public.merge_0057_moved, RLS-locked, readable only here) is which ledger rows were FL
-- or Color before it ran. Their carats at the moment of the merge are therefore exact -- and they
-- stay exactly that: a figure "at merge, <date>", not today's stock.
--
-- WRITES ONE ROW: app_config 'ii_merge_breakdown' =
--     "<merge date>;<grade>,<size>,<carats>;<grade>,<size>,<carats>;..."   grade = FL | NO II | COL
-- Upserted, so running it twice changes nothing. Touches no stock, sale or price row.
-- ---------------------------------------------------------------------------
begin;

do $$
begin
    if to_regclass('public.merge_0057_moved') is null then
        raise exception 'No 0057 merge record on this database. Run 0057 first. Nothing was changed.';
    end if;
end;
$$;

with ii as (select grade_id, updated_at as merged_at from public.grade where code = 'NO II'),
mv as (
    select s.code as size_code,
           coalesce(og.code, 'NO II') as orig,
           case when m.movement_type in ('INTAKE', 'CONVERT_IN', 'ADJUST') then m.weight_ct
                when m.movement_type = 'REJECTION' then 0
                else -m.weight_ct end as signed
      from public.stock_movement m
      join public.size_bucket s using (size_id)
      left join public.merge_0057_moved o on o.tbl = 'stock_movement' and o.col = 'grade_id' and o.row_id = m.movement_id
      left join public.merge_0057_grade og on og.grade_id = o.old_grade_id
     where m.grade_id = (select grade_id from ii)
       and m.created_at <= (select merged_at from ii)),
cells as (select orig, size_code, sum(signed) ct from mv group by 1, 2 having sum(signed) <> 0)
insert into public.app_config (key, value, description, updated_at)
select 'ii_merge_breakdown',
       to_char((select merged_at from ii) at time zone 'Asia/Kolkata', 'YYYY-MM-DD') || ';'
       || string_agg(orig || ',' || size_code || ',' || ct, ';' order by size_code, orig),
       'II at the 0057 merge, split into FL / # / Color. A snapshot, shown on the Stock report hover.',
       now()
  from cells
on conflict (key) do update set value = excluded.value, description = excluded.description, updated_at = now();

commit;

-- Check: the three totals, which must add up to II at the merge.
select split_part(e, ',', 1) as grade, sum(split_part(e, ',', 3)::numeric) as carats_at_merge
  from public.app_config, unnest(string_to_array(value, ';')) e
 where key = 'ii_merge_breakdown' and e like '%,%'
 group by 1 order by 1;
