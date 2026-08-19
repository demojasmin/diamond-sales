-- ---------------------------------------------------------------------------
-- 0030 · A grade the catalogue does not have can be added from the app.
--
-- WHY THIS EXISTS
--
-- 0029 added FL, 1MB and -2 MB because JADU 11-5-26.pdf printed them. Then
-- 17-8-26.pdf printed "MIX" and the same wall came back. Writing one migration
-- per grade name does not converge: the office's sheet is not frozen, and every
-- new name costs a round trip through a developer while a real stock count sits
-- unimportable.
--
-- The refusal itself is right and stays. An import REPLACES, so a line naming a
-- grade the catalogue lacks is not skipped -- it is deleted. What was missing is
-- a way to answer the refusal without a migration.
--
-- WHY NOT LET THE IMPORTER CREATE GRADES SILENTLY
--
-- Because a typo on the sheet would become a permanent grade holding real
-- carats, and nobody would notice until the stock report grew a row. So this is
-- a function the APP CALLS AFTER THE USER HAS BEEN SHOWN THE NAMES and agreed
-- to them. The database offers the capability; it never uses it on its own.
--
-- SAFE TO RUN REPEATEDLY. Creates no grade by itself.
-- ---------------------------------------------------------------------------

create or replace function public.add_grade(p_code text, p_display_name text default null)
returns jsonb
language plpgsql
security definer
set search_path = public
as $$
declare
    v_code text := btrim(coalesce(p_code, ''));
    v_id   bigint;
    v_new  boolean := false;
begin
    if v_code = '' then
        raise exception 'add_grade needs a grade code';
    end if;

    -- A guard, not a validation rule. Grade codes are trade shorthand -- "GH",
    -- "TOP-COL", "-2 MB" -- so almost anything short is legitimate. What this
    -- catches is a whole table row arriving as a name, which is exactly what a
    -- misread PDF produces ("1BB 6.01 11.80 3.05 1 BB").
    if length(v_code) > 20 then
        raise exception 'add_grade: "%" is too long to be a grade code', v_code;
    end if;

    -- Existing code OR existing alias. Without the alias arm, adding "DX" would
    -- create a second grade beside NO DX, and the stock would split across two
    -- rows that are the same goods. The ';' sentinels are 0013's idiom: bare
    -- '%DX%' would also match a hypothetical "DX1".
    select grade_id into v_id
      from public.grade
     where code = v_code
        or (';' || coalesce(aliases, '') || ';') like ('%;' || v_code || ';%')
     limit 1;

    if v_id is null then
        insert into public.grade (code, display_name, sort_order, active)
        values (v_code,
                coalesce(nullif(btrim(p_display_name), ''), v_code),
                (select coalesce(max(sort_order), 0) + 1 from public.grade),
                true)
        returning grade_id into v_id;
        v_new := true;
    end if;

    -- The size pairings, or the grade imports and can never be SOLD. 0018 puts a
    -- trigger on sales_line against grade_size; a grade added without pairings
    -- passes the stock import -- that trigger is on sales_line, not
    -- stock_movement -- and then fails at the first invoice with "Grade MIX does
    -- not use size 14+", which reads as a bug rather than as a missing row.
    --
    -- Same rule as 0018 and 0029: every size except '-2', which is NO 1 and
    -- NO 1 BB only.
    insert into public.grade_size (grade_id, size_id)
    select v_id, s.size_id
      from public.size_bucket s
     where s.code <> '-2'
    on conflict do nothing;

    return jsonb_build_object('ok', true, 'grade_id', v_id,
                              'created', v_new, 'code', v_code);
end;
$$;

revoke all     on function public.add_grade(text, text) from public;
grant  execute on function public.add_grade(text, text) to authenticated;

comment on function public.add_grade(text, text) is
    'Adds a grade named on a stock sheet that the catalogue does not have, with the size pairings 0018''s rule gives it. Idempotent, and returns the existing grade when the code is already a code or an alias.';


-- ---------------------------------------------------------------------------
-- Verification · both rows must read 'ok'. Creates nothing: NO 1 already
-- exists, and 'DX' is an alias of NO DX, so both take the existing-grade path.
-- ---------------------------------------------------------------------------
select 'existing code'  as case,
       case when (public.add_grade('NO 1') ->> 'created') = 'false' then 'ok' else 'CREATED A DUPLICATE' end as status
union all
select 'existing alias',
       case when (public.add_grade('DX')   ->> 'created') = 'false' then 'ok' else 'CREATED A DUPLICATE' end;
