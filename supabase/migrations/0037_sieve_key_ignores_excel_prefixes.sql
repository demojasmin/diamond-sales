-- ---------------------------------------------------------------------------
-- 0037 · sieve_key ignores the punctuation Excel puts in front of a size.
--
-- THE FAULT
--
-- The stock workbooks write "'+18", not "+18". The apostrophe is Excel's text-forcing
-- prefix -- it stops the cell being read as a formula -- and it is not part of the size.
-- Some sheets do the same with a leading comma, ",-2 MB".
--
-- StockFileImport.SizeKey in the app has always stripped both. public.sieve_key did not.
-- So the two disagreed on exactly the labels a real workbook prints:
--
--     sieve_key('+18')  -> '+18'      SizeKey("+18")  -> "+18"
--     sieve_key(''+18') -> ''+18'     SizeKey("'+18") -> "+18"      <-- disagree
--
-- WHY IT MATTERS
--
-- add_size is the only thing standing between an import and a duplicate sieve, and it
-- decides using sieve_key. Feed it the label a workbook actually prints and it would have
-- created a size whose CODE is "'+18" and whose key is "'+18" -- which matches nothing.
-- The next sheet writing "+18" would then have made a SECOND row for the same physical
-- sieve, and the position would have split across the two with nothing to flag it.
--
-- The app could have cleaned the label before sending it, and it now does. But the
-- database has to hold this rule too: it is the last check before a row is written, and
-- the Android app and any direct PostgREST call never pass through the desktop at all.
--
-- SAFE
--
-- No index or constraint uses sieve_key, and no existing size code begins with either
-- character, so every key in both projects is unchanged by this:
--
--     -2->-2  -6.5->-6.5  +6.5->+6.5  +11->+11  14+->+14
--     0.2->+0.2  0.25->+0.25  1/6->+0.1667  1/3->+0.3333
--
-- Touches no stock, no movement, no history. Safe to run repeatedly.
-- ---------------------------------------------------------------------------

begin;

create or replace function public.sieve_key(p_code text)
returns text
language sql
immutable
as $$
    -- Leading apostrophes, commas and spaces are stripped first: they are how a spreadsheet
    -- protects a cell, not anything about the sieve. Mirrors StockFileImport.SizeKey, which
    -- does the same with TrimStart('\'', ',', ' ').
    with t as (
        select btrim(regexp_replace(btrim(coalesce(p_code, '')), '^['',[:space:]]+', '')) as c
    )
    select case
        when t.c = '' then null

        -- "6.5+", "+6.5", "6.5" -> one bucket. Sign either end, number in the middle.
        when btrim(t.c, '+-') ~ '^[0-9]+(\.[0-9]+)?$' then
            case when t.c like '-%' or t.c like '%-' then '-' else '+' end
            || trim_scale(round(btrim(t.c, '+-')::numeric, 4))::text

        -- "1/5" is 0.2, and the catalogue calls that bucket "0.2". Division, not a lookup
        -- table: it holds for any fraction a sheet prints, including ones nobody has thought
        -- of, which is the whole point of not hardcoding sizes.
        --
        -- Rounded to four places because 1/6 is 0.1666... and a code has to be a finite
        -- string. Four is the carat precision this system already uses, so two sieves that
        -- differ later than that are not two sieves.
        when t.c ~ '^[0-9]+\s*/\s*[0-9]*[1-9][0-9]*$' then
            '+' || trim_scale(round(
                split_part(t.c, '/', 1)::numeric / split_part(t.c, '/', 2)::numeric, 4))::text

        else lower(t.c)
    end
    from t;
$$;

comment on function public.sieve_key(text) is
    'Normalises sieve notation so "6.5+", "+6.5" and Excel''s "''+6.5" are one bucket. Mirrors StockFileImport.SizeKey.';

commit;


-- ---------------------------------------------------------------------------
-- Verification · read only. Every row must read 'ok'.
-- ---------------------------------------------------------------------------
select 'the spreadsheet prefix is ignored' as check,
       case when public.sieve_key('''+18') = public.sieve_key('+18')
             and public.sieve_key(',-2')   = public.sieve_key('-2')
            then 'ok' else 'STILL DISAGREES' end as status
union all
select 'notation still resolves',
       case when public.sieve_key('6.5+') = public.sieve_key('+6.5')
             and public.sieve_key('11+')  = public.sieve_key('+11')
             and public.sieve_key('2-')   = public.sieve_key('-2')
            then 'ok' else 'BROKEN' end
union all
select 'fractions still resolve',
       case when public.sieve_key('1/5') = public.sieve_key('0.2')
             and public.sieve_key('1/4') = public.sieve_key('0.25')
             and public.sieve_key('1/6') <> public.sieve_key('1/3')
            then 'ok' else 'BROKEN' end
union all
select 'a word is still not a size',
       case when public.sieve_key('') is null and public.sieve_key('   ') is null
            then 'ok' else 'BROKEN' end
union all
select 'no existing size key moved',
       case when (select count(*) from public.size_bucket
                   where public.sieve_key(code) is distinct from
                         public.sieve_key(regexp_replace(code, '^['',[:space:]]+', ''))) = 0
            then 'ok' else 'A KEY CHANGED' end
union all
select 'still one bucket per sieve',
       case when (select count(*) from (select public.sieve_key(code) k from public.size_bucket
                                         group by 1 having count(*) > 1) d) = 0
            then 'ok' else 'DUPLICATE SIEVE KEYS' end
 order by 1;
