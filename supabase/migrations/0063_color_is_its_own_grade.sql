-- ---------------------------------------------------------------------------
-- 0063. Color is its own grade again. "II Color" / "II Col" are II.
--
-- WHAT THE OFFICE ASKED FOR (20 Nov 2026)
--     II Color, II Col                    -> II   (grade NO II)
--     Color, COLOR, COLOUR, COL, C        -> Color (grade COL), never mixed with II
--
-- WHAT IT CHANGES -- the catalogue only:
--   1. Grade COL ("Color") comes back from 0057's own backup (merge_0057_grade), with its
--      original grade_id, sort order and sieve pairings, so anything that remembered id 14 still
--      means Color. If there is no backup, it is created fresh with II's sieves.
--   2. Its spellings (C, COL, color, COLOR, COLOUR) move from II's alias list back to COL.
--   3. II gains "II Color", "II Colour", "II Col" (and their spaceless forms).
--   4. Grade II COLOR, if migration 0062 was ever run, is removed -- but ONLY while nothing has
--      been recorded against it. If it holds data the migration refuses and changes nothing.
--
-- WHAT IT DOES NOT CHANGE
--   * No stock movement, sale, intake, price or receipt is moved or edited. Carats that 0057
--     moved into II stay in II -- after 13 Oct they cannot be told apart from II's own, and
--     moving a guess would change balances. On Priya the ledger is empty (reset 20 Nov), so
--     there is nothing to move. From now on a sheet or sale saying "Color" lands on Color.
--   * FL and # stay part of II, exactly as 0057 left them.
--
-- One transaction. Every check at the end must read 'ok'.
-- UNDO: ROLLBACK not needed for data (none moved); to merge again, re-run 0057.
-- ---------------------------------------------------------------------------
begin;

do $$
declare
    v_ii   bigint := (select grade_id from public.grade where code = 'NO II');
    v_iic  bigint := (select grade_id from public.grade where code = 'II COLOR');
    v_col  bigint := (select grade_id from public.grade where code = 'COL');
    v_used boolean;
    v_backup boolean := to_regclass('public.merge_0057_grade') is not null;
begin
    if v_ii is null then raise exception '0063: grade NO II not found. Nothing was changed.'; end if;

    -- 4. II COLOR (0062), only if it is empty.
    if v_iic is not null then
        select exists (select 1 from public.stock_movement where grade_id = v_iic or counterparty_grade_id = v_iic)
            or exists (select 1 from public.sales_line where grade_id = v_iic)
            or exists (select 1 from public.rough_intake where grade_id = v_iic)
            or exists (select 1 from public.stock_reservation where grade_id = v_iic)
            or exists (select 1 from public.rejection_disposition where to_grade_id = v_iic)
            or exists (select 1 from public.price_list where grade_id = v_iic)
          into v_used;
        if v_used then
            raise exception '0063: grade II COLOR already has stock, sales or prices recorded against it. Nothing was changed -- ask before moving them to II.';
        end if;
        delete from public.grade where grade_id = v_iic;          -- grade_size cascades
    end if;

    -- 2 (first half). Free the Color spellings from II, so no spelling ever names two grades.
    update public.grade
       set aliases = nullif(array_to_string(array(
               select a from unnest(string_to_array(coalesce(aliases, ''), ';')) a
                where a <> '' and coalesce(public.grade_key(a), '') not in ('C', 'COL', 'COLOR', 'COLOUR')), ';'), '')
     where grade_id = v_ii;

    -- 1. COL back.
    if v_col is null then
        if v_backup and exists (select 1 from public.merge_0057_grade where code = 'COL')
           and not exists (select 1 from public.grade g join public.merge_0057_grade b on b.grade_id = g.grade_id
                            where b.code = 'COL') then
            -- OVERRIDING SYSTEM VALUE: grade_id is an identity column (GENERATED ALWAYS), and this
            -- puts back the id Color had before 0057. The id sequence is already past it, so later
            -- grades are unaffected.
            insert into public.grade (grade_id, code, display_name, aliases, sort_order, active)
            overriding system value
            select grade_id, 'COL', 'Color', 'COLOUR;COLOR;C;color;Colour', sort_order, true
              from public.merge_0057_grade where code = 'COL';
        else
            insert into public.grade (code, display_name, aliases, sort_order, active)
            values ('COL', 'Color', 'COLOUR;COLOR;C;color;Colour',
                    (select coalesce(max(sort_order), 0) + 1 from public.grade), true);
        end if;
        v_col := (select grade_id from public.grade where code = 'COL');

        -- Its sieves: the ones it had before 0057, else the same as II's.
        if v_backup and exists (select 1 from public.merge_0057_grade_size s
                                  join public.merge_0057_grade b on b.grade_id = s.grade_id where b.code = 'COL') then
            insert into public.grade_size (grade_id, size_id)
            select v_col, s.size_id
              from public.merge_0057_grade_size s
              join public.merge_0057_grade b on b.grade_id = s.grade_id
              join public.size_bucket z on z.size_id = s.size_id
             where b.code = 'COL'
            on conflict do nothing;
        else
            insert into public.grade_size (grade_id, size_id)
            select v_col, size_id from public.grade_size where grade_id = v_ii
            on conflict do nothing;
        end if;
    else
        -- COL already there (0057 rolled back, say): just make sure it owns its spellings.
        update public.grade set aliases = 'COLOUR;COLOR;C;color;Colour', display_name = 'Color', active = true
         where grade_id = v_col;
    end if;

    -- 3. "II Color" / "II Col" are II.
    update public.grade
       set aliases = concat_ws(';', nullif(aliases, ''), 'II Color;II Colour;II Col;IICOLOR;IICOLOUR;IICOL')
     where grade_id = v_ii
       and not exists (select 1 from unnest(string_to_array(coalesce(aliases, ''), ';')) a
                        where public.grade_key(a) = 'IICOLOR');

    raise notice '0063: Color is grade % with % sieve(s); II aliases are now: %',
        v_col, (select count(*) from public.grade_size where grade_id = v_col),
        (select aliases from public.grade where grade_id = v_ii);
end $$;

commit;


-- ---------------------------------------------------------------------------
-- Verification (read-only). Every row must read 'ok'.
-- ---------------------------------------------------------------------------
with spelling(s, expect) as (values
        ('Color', 'COL'), ('COLOR', 'COL'), ('COLOUR', 'COL'), ('COL', 'COL'), ('C', 'COL'), ('color', 'COL'),
        ('II Color', 'NO II'), ('II Col', 'NO II'), ('II', 'NO II'), ('FL', 'NO II'), ('#', 'NO II')),
     owner as (
        select s.s, s.expect,
               (select string_agg(distinct g.code, ',')
                  from public.grade g
                 where g.code = s.s
                    or s.s = any(string_to_array(coalesce(g.aliases, ''), ';'))
                    or (public.grade_key(s.s) is not null
                        and (public.grade_key(g.code) = public.grade_key(s.s)
                             or exists (select 1 from unnest(string_to_array(coalesce(g.aliases, ''), ';')) a
                                         where public.grade_key(a) = public.grade_key(s.s))))) as got
          from spelling s)
select '"' || s || '" -> ' || expect as check, case when got = expect then 'ok' else 'GOT ' || coalesce(got, 'nothing') end as status
  from owner
union all
select 'Color has sieves', case when (select count(*) from public.grade_size gs join public.grade g using (grade_id) where g.code = 'COL') > 0 then 'ok' else 'NONE' end
union all
select 'no II COLOR grade left', case when not exists (select 1 from public.grade where code = 'II COLOR') then 'ok' else 'STILL THERE' end
union all
select 'no spelling belongs to two grades',
       case when not exists (
                select k from (
                    select public.grade_key(code) k, grade_id from public.grade
                    union
                    select public.grade_key(a), grade_id
                      from public.grade, unnest(string_to_array(coalesce(aliases, ''), ';')) a
                ) t where k is not null group by k having count(distinct grade_id) > 1)
            then 'ok' else 'CLASH' end;
