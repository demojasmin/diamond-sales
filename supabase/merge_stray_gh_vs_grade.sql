-- ---------------------------------------------------------------------------
-- One grade, not two: fold the stray "GH VS" row back into "GH".
--
-- WHAT HAPPENED. The app shows the mark, the database stores the code:
-- GradeNames.Marks turns code "GH" into "GH VS" on every screen. The IMPORT does
-- not use that map -- it resolves a sheet's grade names through the database's
-- own aliases column -- so a sheet writing "GH VS" did not match code "GH", the
-- importer offered to create the missing grade, and somebody said yes. Demo now
-- carries both, and every grade picker shows "GH VS" twice with no way to tell
-- them apart.
--
-- WHY IT IS NOT COSMETIC. ExcelImport.AliasMap assigns map[code] = code for every
-- grade and only TryAdds the aliases, so A CODE ALWAYS BEATS AN ALIAS. While the
-- empty "GH VS" row exists, the next sheet naming "GH VS" resolves to IT rather
-- than to the grade holding the trade -- and the carats land on an empty bucket
-- that looks right on screen.
--
-- SAFE BECAUSE IT IS EMPTY. Measured on Demo before this was written: "GH VS"
-- (id 32) carries 0 sales lines and 0 stock movements; "GH" (id 19) carries 64
-- lines. The check below re-measures rather than trusting that -- if anything has
-- been written against it since, the script refuses and the rows have to be
-- MERGED instead, which is a different and much more careful job.
--
-- AND THE ALIAS, so it cannot happen again. "GH VS" is added to GH's aliases if
-- it is not already there, which is what makes the next import resolve the name
-- instead of offering to create it a second time.
--
-- EITHER DATABASE. Set the guard below. Priya may not have the stray row at all,
-- in which case this reports that and changes nothing.
-- ---------------------------------------------------------------------------

begin;

-- ── the guard · SET THIS FIRST ─────────────────────────────────────────────
--     Priya (handover) : Priya Sales
--     Demo             : Priya Gems
do $$
declare
    expected constant text := 'Priya Gems';   -- <<<< EDIT ME
    v text;
begin
    select value into v from public.app_config where key = 'company_name';
    if v is distinct from expected then
        raise exception
            'Refusing: this run is set for "%" and this database says "%". Nothing changed.',
            expected, coalesce(v, '(none)');
    end if;
    raise notice 'Database confirmed: %.', expected;
end $$;


do $$
declare
    v_keep   bigint;
    v_stray  bigint;
    v_lines  integer;
    v_moves  integer;
    v_intake integer;
    v_price  integer;
    v_holds  integer;
    v_alias  text;
begin
    select grade_id into v_keep  from public.grade where code = 'GH';
    select grade_id into v_stray from public.grade where code = 'GH VS';

    if v_keep is null then
        raise exception 'There is no grade with code "GH" here. Nothing changed.';
    end if;

    if v_stray is null then
        raise notice 'No stray "GH VS" row on this database. Only the alias is checked below.';
    else
        -- Re-measured, not assumed. A row that has traded is not a stray.
        select count(*) into v_lines  from public.sales_line        where grade_id = v_stray;
        select count(*) into v_moves  from public.stock_movement
                                      where grade_id = v_stray or counterparty_grade_id = v_stray;
        select count(*) into v_intake from public.rough_intake      where grade_id = v_stray;
        select count(*) into v_price  from public.price_list        where grade_id = v_stray;
        select count(*) into v_holds  from public.stock_reservation where grade_id = v_stray;

        if v_lines + v_moves + v_intake + v_price + v_holds > 0 then
            raise exception
                'Grade "GH VS" (id %) carries trade: % sales line(s), % movement(s), % intake(s), % price row(s), % reservation(s). Nothing changed -- these rows need MERGING onto GH (id %), not deleting.',
                v_stray, v_lines, v_moves, v_intake, v_price, v_holds, v_keep;
        end if;

        delete from public.grade_size where grade_id = v_stray;
        delete from public.grade      where grade_id = v_stray;
        raise notice 'Removed the empty "GH VS" row (id %).', v_stray;
    end if;

    -- The alias is the half that stops it recurring. Matched case-insensitively
    -- against the separated list, not with a LIKE over the whole string: "GH VS"
    -- is a substring of nothing else here today, but "GH V" would be.
    select aliases into v_alias from public.grade where grade_id = v_keep;
    raise notice 'GH aliases before: %', coalesce(v_alias, '(none)');

    if not exists (
        select 1
          from unnest(string_to_array(coalesce(v_alias, ''), ';')) as a
         where upper(btrim(a)) = 'GH VS')
    then
        update public.grade
           set aliases = case when coalesce(btrim(v_alias), '') = '' then 'GH VS'
                              else btrim(v_alias) || ';GH VS' end
         where grade_id = v_keep;
        raise notice 'Added "GH VS" to GH''s aliases.';
    else
        raise notice '"GH VS" was already an alias of GH. Left alone.';
    end if;
end $$;

commit;


-- ---------------------------------------------------------------------------
-- Verification. One row, and its aliases must contain GH VS.
-- ---------------------------------------------------------------------------
select code, display_name, sort_order, aliases, active
  from public.grade
 where code in ('GH', 'GH VS')
 order by code;

-- No grade may be left with nowhere to trade.
select g.code as grade_with_no_sizes
  from public.grade g
 where g.active
   and not exists (select 1 from public.grade_size s where s.grade_id = g.grade_id)
 order by 1;
