-- ---------------------------------------------------------------------------
-- 0034 · A sieve size named on a stock sheet can be added from the app, and the
--        two fractions the office already trades.
--
-- WHY THIS EXISTS
--
-- 0030 did this for grades. Sizes had the same gap and it cost an afternoon: a
-- sheet headed "1/6" instead of "14+" read 226.72 of its own printed 508.71 ct,
-- and the only remedy was a developer. The office's sheets are not frozen, so
-- one migration per sieve size does not converge.
--
-- WHAT IT DOES NOT DO
--
-- It does not create a size that already exists under another notation, and that
-- is the point of the whole file. These sheets write the same bucket four ways:
--
--     -6.5   6.5-      +6.5   6.5+      +11   11+      -2   2-
--
-- and the app already resolves all of them. Creating "6.5+" beside "+6.5" would
-- put the same physical sieve in two rows, split the position between them, and
-- every total afterwards would be wrong with nothing to flag it. So the lookup
-- below normalises the sign the same way DiamondCalc and StockFileImport.SizeKey
-- do -- sign either side, number in the middle -- and hands back the existing
-- row when it finds one.
--
-- THE TWO NEW ONES
--
-- 1/6 and 1/3 are genuinely absent. They are stored as the fraction, not as a
-- decimal, because 1/6 is 0.1666... and a rounded code would be a different
-- number from the one printed on the sheet. The existing 0.2 and 0.25 keep their
-- decimal codes and their 1/5 and 1/4 labels; nothing about them changes.
--
-- SAFE TO RUN REPEATEDLY. Adds nothing that exists, changes no stock.
-- ---------------------------------------------------------------------------

-- ── the sieve key, in the database ─────────────────────────────────────────
-- The same rule as StockFileImport.SizeKey: strip the sign from either end,
-- keep the number, put the sign in front. "6.5+", "+6.5" and "6.5" all key to
-- "+6.5". A fraction like "1/6" has no number to parse, so it keys to itself
-- and can only match another "1/6".
create or replace function public.sieve_key(p_code text)
returns text
language sql
immutable
as $$
    with t as (select btrim(coalesce(p_code, '')) as c)
    select case
        when t.c = '' then null

        -- "6.5+", "+6.5", "6.5" -> one bucket. Sign either end, number in the middle.
        when btrim(t.c, '+-') ~ '^[0-9]+(\.[0-9]+)?$' then
            case when t.c like '-%' or t.c like '%-' then '-' else '+' end
            || trim_scale(round(btrim(t.c, '+-')::numeric, 4))::text

        -- "1/5" is 0.2, and the catalogue calls that bucket "0.2". Division, not a
        -- lookup table: it holds for any fraction a sheet prints, including ones
        -- nobody has thought of, which is the whole point of not hardcoding sizes.
        --
        -- Rounded to four places because 1/6 is 0.1666... and a code has to be a
        -- finite string. Four is the carat precision this system already uses
        -- (app_config.carat_precision), so two sieves that differ later than that
        -- are not two sieves.
        when t.c ~ '^[0-9]+\s*/\s*[0-9]*[1-9][0-9]*$' then
            '+' || trim_scale(round(
                split_part(t.c, '/', 1)::numeric / split_part(t.c, '/', 2)::numeric, 4))::text

        else lower(t.c)
    end
    from t;
$$;

comment on function public.sieve_key(text) is
    'Normalises sieve notation so "6.5+" and "+6.5" are one bucket. Mirrors StockFileImport.SizeKey.';


create or replace function public.add_size(p_code text, p_display_name text default null)
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
        raise exception 'add_size needs a size code';
    end if;

    -- size_bucket.code is varchar(8). A longer "size" is a misread cell, not a sieve.
    if length(v_code) > 8 then
        raise exception 'add_size: "%" is too long to be a sieve size', v_code;
    end if;

    -- Existing row, matched on the normalised key rather than on the text, so a
    -- sheet writing "11+" finds the "+11" already there.
    select size_id into v_id
      from public.size_bucket
     where public.sieve_key(code) = public.sieve_key(v_code)
     limit 1;

    if v_id is null then
        insert into public.size_bucket (code, sort_order)
        values (v_code, (select coalesce(max(sort_order), 0) + 1 from public.size_bucket))
        returning size_id into v_id;
        v_new := true;
    end if;

    -- The pairings, or the size imports and can never be SOLD: 0018 guards
    -- sales_line with a trigger against grade_size. Same rule as 0018 and 0030 --
    -- every grade except the '-2' exception, which is NO 1 and NO 1 BB only.
    --
    -- ONLY FOR A SIZE THIS CALL CREATED. Asking for a size that already exists must
    -- write nothing at all: an existing sieve may have had a pairing removed on
    -- purpose, and "add_size" quietly restoring it would be this function editing a
    -- catalogue it was only asked to read. Repairing pairings is a different job
    -- with a different name.
    if v_new then
        insert into public.grade_size (grade_id, size_id)
        select g.grade_id, v_id
          from public.grade g
         where public.sieve_key(v_code) <> public.sieve_key('-2')
            or g.code in ('NO 1', 'NO 1 BB')
        on conflict do nothing;
    end if;

    return jsonb_build_object('ok', true, 'size_id', v_id,
                              'created', v_new, 'code', v_code);
end;
$$;

revoke all     on function public.add_size(text, text) from public;
revoke all     on function public.add_size(text, text) from anon;
grant  execute on function public.add_size(text, text) to authenticated;

comment on function public.add_size(text, text) is
    'Adds a sieve size named on a stock sheet, with the grade pairings 0018''s rule gives it. Returns the EXISTING size when the code is the same bucket in another notation, so "6.5+" never becomes a twin of "+6.5".';


-- ── the two the office trades and the catalogue lacked ─────────────────────
select public.add_size('1/6');
select public.add_size('1/3');


-- ---------------------------------------------------------------------------
-- Verification · every row must read 'ok'.
--
-- The first two prove the new sizes exist. The rest prove the important half:
-- that the notations already in use resolve to the rows already there, and
-- create nothing.
-- ---------------------------------------------------------------------------
select '1/6 exists' as check,
       case when exists (select 1 from public.size_bucket where code = '1/6') then 'ok' else 'MISSING' end as status
union all
select '1/3 exists',
       case when exists (select 1 from public.size_bucket where code = '1/3') then 'ok' else 'MISSING' end
union all
select 'sizes total',
       case when (select count(*) from public.size_bucket) = 11 then 'ok - 9 + 2' else
            'UNEXPECTED - ' || (select count(*) from public.size_bucket)::text end
union all
-- Read only, and that is the point. This asked the question by CALLING add_size,
-- so a wrong sieve_key would have created the very duplicate it was checking for
-- and then reported it. A verification that can cause the fault it looks for is
-- worse than no verification: it passes, and the damage is its own doing.
select '"' || n || '" resolves to an existing row',
       case when exists (select 1 from public.size_bucket b
                          where public.sieve_key(b.code) = public.sieve_key(n))
            then 'ok - ' || (select b.code from public.size_bucket b
                              where public.sieve_key(b.code) = public.sieve_key(n) limit 1)
            else 'WOULD CREATE A TWIN' end
  from (values ('2-'), ('6.5-'), ('6.5+'), ('11+'), ('-2'), ('+6.5'), ('1/5'), ('1/4')) as t(n)
union all
select 'still one bucket per sieve',
       case when (select count(*) from (select public.sieve_key(code) k from public.size_bucket
                                         group by 1 having count(*) > 1) d) = 0
            then 'ok' else 'DUPLICATE SIEVE KEYS' end
 order by 1;
