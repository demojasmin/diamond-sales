-- ===========================================================================
-- Everything that landed after schema.sql was dumped: migrations 0036 to 0045.
--
-- schema.sql is a snapshot of 19 Aug 2026, and 0034 and 0035 ship beside it
-- because they came after. Ten more have landed since. Without them a new
-- project has no stock-reservation feature at all, and the desktop app calls
-- reserve_line on every line typed and gets "function does not exist" back.
--
-- GENERATED, not written. Each section is one migration copied verbatim from
-- supabase/migrations/ in number order. If a section needs changing, change the
-- migration and regenerate this -- otherwise the project you build stops
-- matching the ones already running.
--
-- WHEN:  schema.sql -> 0034 -> 0035 -> seed -> THIS FILE -> verify
--
-- After the seed, not before: 0039 adds the "Unknown Grade" catalogue entry, so
-- running it first makes the seed's own summary read 28 grades where the
-- instructions say 27 and look like a failure. The other nine create only
-- functions, views, a table and triggers, so they do not care about the order.
--
-- Each section commits itself, so a failure stops the rest and leaves what came
-- before it in place. The check at the end names anything that did not land.
-- ===========================================================================

do $guard$
begin
    if to_regproc('public.sieve_key') is null then
        raise exception 'Stop: 0034_add_size.sql has not been run. Do Step 1b first.';
    end if;
    if not exists (select 1 from information_schema.columns
                    where table_schema = 'public' and table_name = 'size_bucket'
                      and column_name = 'active') then
        raise exception 'Stop: 0035_retire_sizes.sql has not been run. Do Step 1b first.';
    end if;
    if not exists (select 1 from public.grade) then
        raise exception 'Stop: the catalogue is empty, so Step 2 (new_project_seed.sql) has not been run. Do that first.';
    end if;

    -- THE OTHER DIRECTION. This file is for a project that has NONE of 0036 to 0045. Run it on a
    -- database already carrying them and Section 8 rewinds v_stock_position from 0044's twelve
    -- columns to 0043's ten -- and "create or replace view" may only append, so Postgres refuses
    -- with 42P16 "cannot drop columns from view" eleven hundred lines in, having already re-applied
    -- seven sections. They are identical definitions and nothing is harmed, but the error names a
    -- view rather than the mistake, which is a poor way to learn you are in the wrong project.
    if to_regclass('public.stock_reservation') is not null then
        raise exception 'Stop: this database already has 0043 (stock_reservation exists), so it is not a new project. This file only runs against one that has none of 0036 to 0045. To see what an existing database is missing, run supabase/WHICH_MIGRATIONS_ARE_APPLIED.sql and apply just those, in number order, from supabase/migrations/.';
    end if;
end $guard$;




-- ###########################################################################
-- SECTION 1 of 17 : 0036_add_size_restores_retired.sql
-- ###########################################################################

-- ---------------------------------------------------------------------------
-- 0036 · A retired sieve can be brought back through the same popup that adds a new one.
--
-- WHY THIS SUPERSEDES 0035 RATHER THAN EDITING IT
--
-- 0035 is already applied to both projects. A migration that has run is a record of what
-- happened; editing it in place would leave the file disagreeing with every database that
-- has it, and the next person to read the folder would have no way to tell which version
-- their project actually holds. So this replaces the function instead.
--
-- WHAT CHANGES
--
-- 0035 made add_size REFUSE a retired sieve. That was right for the case it was written
-- for -- an import walking silently into a switched-off size -- but it is wrong as a
-- permanent rule: it made "retired" mean "gone forever", which nobody decided. The office
-- retires a sieve because it has stopped using it, and a sheet turning up next quarter with
-- that column on it is a normal event, not an error.
--
-- So the refusal moves from add_size to the place it belongs: the trigger. Stock cannot
-- LAND on a retired size (0035's trg_reject_retired_size, unchanged), but a person who is
-- looking at the popup and has read the heading off the paper can bring the sieve back.
-- The confirmation is the authority; the flag is not a lock.
--
-- WHAT DOES NOT CHANGE
--
-- Nothing here is named. There is no list of 14+, +18 or +23, and no size is special. A
-- retired sieve is any row with active = false, and the same rule restores any of them.
--
-- The twin rule is untouched and still the point of the whole file: the lookup is on
-- sieve_key, so answering yes to "6.5+" when "+6.5" exists RESTORES that row rather than
-- creating a second one. Restoring cannot create a duplicate, because it never inserts.
--
-- SAFE TO RUN REPEATEDLY. Touches no stock, no movement and no history.
-- ---------------------------------------------------------------------------

begin;

create or replace function public.add_size(p_code text, p_display_name text default null)
returns jsonb
language plpgsql
security definer
set search_path = public
as $$
declare
    v_code    text := btrim(coalesce(p_code, ''));
    v_id      bigint;
    v_active  boolean;
    v_new     boolean := false;
    v_restored boolean := false;
begin
    if v_code = '' then
        raise exception 'add_size needs a size code';
    end if;

    -- size_bucket.code is varchar(8). A longer "size" is a misread cell, not a sieve.
    if length(v_code) > 8 then
        raise exception 'add_size: "%" is too long to be a sieve size', v_code;
    end if;

    -- Existing row, matched on the normalised key rather than on the text, so a sheet
    -- writing "11+" finds the "+11" already there. This is what makes the whole thing
    -- duplicate-proof, and it applies to retired rows exactly as it does to live ones.
    select size_id, active into v_id, v_active
      from public.size_bucket
     where public.sieve_key(code) = public.sieve_key(v_code)
     limit 1;

    if v_id is null then
        insert into public.size_bucket (code, sort_order)
        values (v_code, (select coalesce(max(sort_order), 0) + 1 from public.size_bucket))
        returning size_id into v_id;
        v_new := true;

    elsif not v_active then
        -- Brought back, not recreated. The row keeps its size_id, so every movement,
        -- parcel and audit entry that ever pointed at this sieve still points at it and
        -- the history reads as one continuous thing rather than two sieves with a gap.
        update public.size_bucket
           set active = true, updated_at = now()
         where size_id = v_id;
        v_restored := true;
    end if;

    -- The pairings, or the size imports and can never be SOLD: 0018 guards sales_line with
    -- a trigger against grade_size. Same rule as 0018 and 0030 -- every grade except the
    -- '-2' exception, which is NO 1 and NO 1 BB only.
    --
    -- Written for a size this call CREATED, and also for one it RESTORED: 0035 deletes the
    -- pairings of a sieve it removes outright, and a restored row whose pairings went with
    -- it would come back unsellable. An already-live size is still left alone -- its
    -- pairings may have been changed on purpose, and add_size is not the repair tool.
    if v_new or v_restored then
        insert into public.grade_size (grade_id, size_id)
        select g.grade_id, v_id
          from public.grade g
         where public.sieve_key(v_code) <> public.sieve_key('-2')
            or g.code in ('NO 1', 'NO 1 BB')
        on conflict do nothing;
    end if;

    return jsonb_build_object('ok', true, 'size_id', v_id, 'created', v_new,
                              'restored', v_restored, 'code', v_code);
end;
$$;

revoke all     on function public.add_size(text, text) from public;
revoke all     on function public.add_size(text, text) from anon;
grant  execute on function public.add_size(text, text) to authenticated;

comment on function public.add_size(text, text) is
    'Adds a sieve size named on a stock or sale file, or restores one that was retired, with the grade pairings 0018''s rule gives it. Returns the EXISTING row when the code is the same bucket in another notation, so "6.5+" never becomes a twin of "+6.5".';

commit;


-- ---------------------------------------------------------------------------
-- Verification · read only. Every row must read 'ok'.
-- ---------------------------------------------------------------------------
select 'add_size exists' as check,
       case when exists (select 1 from pg_proc p join pg_namespace n on n.oid = p.pronamespace
                          where n.nspname = 'public' and p.proname = 'add_size')
            then 'ok' else 'MISSING' end as status
union all
select 'the retired-size write barrier is still in place',
       case when (select count(*) from pg_trigger
                   where tgname = 'trg_reject_retired_size' and not tgisinternal) = 4
            then 'ok - 4 tables'
            else 'UNEXPECTED - ' || (select count(*)::text from pg_trigger
                                      where tgname = 'trg_reject_retired_size' and not tgisinternal) end
union all
select 'still one bucket per sieve',
       case when (select count(*) from (select public.sieve_key(code) k from public.size_bucket
                                         group by 1 having count(*) > 1) d) = 0
            then 'ok' else 'DUPLICATE SIEVE KEYS' end
union all
select 'sizes, live and retired',
       (select count(*) filter (where active)::text || ' live, '
             || count(*) filter (where not active)::text || ' retired'
          from public.size_bucket)
 order by 1;


-- ###########################################################################
-- SECTION 2 of 17 : 0037_sieve_key_ignores_excel_prefixes.sql
-- ###########################################################################

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


-- ###########################################################################
-- SECTION 3 of 17 : 0038_import_stock_refuses_negative.sql
-- ###########################################################################

-- ---------------------------------------------------------------------------
-- 0038 · A replacing stock import may not leave a bucket below zero.
--
-- THE FAULT
--
-- import_stock(p_replace => true) deletes every stock_import movement and writes the new
-- sheet in its place. Sales, rejections and conversions are left alone -- correctly, they
-- are what happened -- but they were measured against the OLD baseline.
--
-- Replace a 300 ct sheet with a 107.76 ct one after 202.10 ct has gone out, and the
-- position lands at -94.34 ct. Which is exactly what Demo showed: three negative buckets
-- and a headline reading "Stock left that never arrived".
--
-- WHY negative_stock = block DID NOT CATCH IT
--
-- assert_stock runs when a movement is POSTED. It reads the position at that instant and
-- refuses to take a bucket below zero, and it did its job: every one of those rejections
-- was legal when it was recorded.
--
-- The import moved the floor afterwards. No movement was posted, so nothing called
-- assert_stock, and the balance went negative retroactively at a moment the policy had no
-- opportunity to see. A check that only fires on the way out cannot protect against the
-- baseline being lowered underneath it.
--
-- WHAT THIS DOES
--
-- Before deleting anything, it works out what every bucket WOULD hold once the old import
-- is gone and the new sheet is in: the new sheet's figure, plus every movement that is not
-- a stock import, signed exactly as v_stock_position signs it. Any bucket landing below
-- zero names itself and the import is refused whole -- nothing deleted, nothing written.
--
-- It refuses only under negative_stock = 'block', the same setting assert_stock obeys, so
-- one policy governs both doors rather than this one inventing a rule of its own.
--
-- WHAT IT DOES NOT DO
--
-- It writes no compensating adjustment and hides no balance. A shortfall means the sheet
-- and the ledger disagree, and that is a question for whoever holds the paper -- inventing
-- carats to make the arithmetic close would put a number in the client's stock that nobody
-- counted.
-- ---------------------------------------------------------------------------

begin;

create or replace function public.import_stock(
    p_as_at   date,
    p_rows    jsonb,
    p_replace boolean default true,
    p_batch   uuid    default null,
    p_source  text    default 'excel'
)
returns jsonb
language plpgsql
security definer
set search_path = public
as $$
declare
    v_old_ids bigint[];
    v_deleted integer := 0;
    v_written integer := 0;
    v_batch   uuid    := coalesce(p_batch, gen_random_uuid());
    v_short   text;
    v_count   integer;
begin
    if p_rows is null or jsonb_typeof(p_rows) <> 'array' then
        raise exception 'import_stock expects an array of rows';
    end if;

    -- Refuse to replace a real dataset with nothing. An empty array is what a
    -- parser returns when it silently failed, and "delete everything" is too
    -- destructive to be reachable by accident. Clearing the import on purpose
    -- is what delete_imported_stock() is for.
    if jsonb_array_length(p_rows) = 0 then
        raise exception 'import_stock was given no rows; use delete_imported_stock() to clear the import';
    end if;

    if p_source is not null and p_source not in ('excel', 'pdf') then
        raise exception 'import_stock does not know the source %', p_source;
    end if;

    if p_replace and public.negative_stock_policy() = 'block' then
        -- Everything that is NOT a stock import, signed the way v_stock_position signs it.
        -- Using the same rule matters more than it looks: weight_ct is stored positive for
        -- every movement type and the direction lives in movement_type, so a guard that
        -- summed the raw column would read a rejection as an arrival.
        with keeping as (
            select m.grade_id, m.size_id,
                   sum(case when m.movement_type in ('INTAKE', 'CONVERT_IN') then m.weight_ct
                            when m.movement_type = 'ADJUST'                  then m.weight_ct
                            else -m.weight_ct end) as other_ct
              from public.stock_movement m
             where m.ref_type is distinct from 'stock_import'
             group by m.grade_id, m.size_id
        ),
        arriving as (
            select (r->>'grade_id')::bigint as grade_id,
                   (r->>'size_id')::bigint  as size_id,
                   sum((r->>'weight_ct')::numeric) as new_ct
              from jsonb_array_elements(p_rows) as r
             group by 1, 2
        ),
        -- FULL join on purpose. A bucket the new sheet does not mention at all still holds
        -- whatever went out of it, and dropping to zero import while sales remain is the
        -- most likely way to go negative -- so the row must survive the join to be seen.
        resulting as (
            select coalesce(a.grade_id, k.grade_id)          as grade_id,
                   coalesce(a.size_id,  k.size_id)           as size_id,
                   coalesce(a.new_ct,   0)                   as new_ct,
                   coalesce(k.other_ct, 0)                   as other_ct,
                   coalesce(a.new_ct, 0) + coalesce(k.other_ct, 0) as balance
              from arriving a
              full join keeping k
                on k.grade_id = a.grade_id and k.size_id = a.size_id
        )
        select count(*),
               string_agg(
                   format('%s x %s: the sheet brings %s ct but %s ct has gone out against it, leaving %s ct',
                          coalesce(g.code, '?'), coalesce(s.code, '?'),
                          trim_scale(round(res.new_ct, 4)),
                          trim_scale(round(-res.other_ct, 4)),
                          trim_scale(round(res.balance, 4))),
                   E'\n  - ' order by g.code, s.code)
          into v_count, v_short
          from resulting res
          left join public.grade       g on g.grade_id = res.grade_id
          left join public.size_bucket s on s.size_id  = res.size_id
         where res.balance < -0.00005;

        if v_count > 0 then
            raise exception E'This sheet cannot replace the current stock: % bucket(s) would be left below zero.\n\n  - %\n\nNothing has been imported. Either this is not the sheet that matches these sales and rejections, or those movements were recorded against stock this sheet does not carry.',
                            v_count, v_short
                using errcode = 'check_violation';
        end if;
    end if;

    if p_replace then
        -- The parcels behind the movements, captured before the movements go.
        select coalesce(array_agg(distinct ref_id), '{}')
          into v_old_ids
          from public.stock_movement
         where ref_type = 'stock_import'
           and ref_id is not null;

        delete from public.stock_movement where ref_type = 'stock_import';
        get diagnostics v_deleted = row_count;

        if array_length(v_old_ids, 1) is not null then
            delete from public.rough_intake where intake_id = any(v_old_ids);
        end if;
    end if;

    -- Parcels and their movements in one statement each, both reading the same
    -- CTE, so a movement without its intake is not expressible here.
    with parcel as (
        insert into public.rough_intake
            (intake_date, grade_id, size_id, weight_ct, price_per_ct, created_by,
             import_batch, import_source)
        select p_as_at,
               (r->>'grade_id')::bigint,
               (r->>'size_id')::bigint,
               (r->>'weight_ct')::numeric,
               (r->>'price_per_ct')::numeric,
               auth.uid(),
               v_batch,
               p_source
          from jsonb_array_elements(p_rows) as r
        returning intake_id, grade_id, size_id, weight_ct, price_per_ct
    )
    insert into public.stock_movement
        (movement_date, grade_id, size_id, movement_type, weight_ct,
         price_per_ct, ref_type, ref_id, created_by)
    select p_as_at, p.grade_id, p.size_id, 'INTAKE', p.weight_ct,
           p.price_per_ct, 'stock_import', p.intake_id, auth.uid()
      from parcel p;

    get diagnostics v_written = row_count;

    return jsonb_build_object('ok', true, 'deleted', v_deleted,
                              'written', v_written, 'batch', v_batch);
end;
$$;

revoke all on function public.import_stock(date, jsonb, boolean, uuid, text) from public;
grant execute on function public.import_stock(date, jsonb, boolean, uuid, text) to authenticated;

commit;


-- ---------------------------------------------------------------------------
-- Verification · read only. Every row must read 'ok'.
-- ---------------------------------------------------------------------------
select 'import_stock still exists' as check,
       case when exists (select 1 from pg_proc p join pg_namespace n on n.oid = p.pronamespace
                          where n.nspname = 'public' and p.proname = 'import_stock')
            then 'ok' else 'MISSING' end as status
union all
select 'the guard is in it',
       case when (select prosrc from pg_proc p join pg_namespace n on n.oid = p.pronamespace
                   where n.nspname = 'public' and p.proname = 'import_stock') like '%would be left below zero%'
            then 'ok' else 'MISSING' end
union all
select 'it signs movements the way v_stock_position does',
       case when (select prosrc from pg_proc p join pg_namespace n on n.oid = p.pronamespace
                   where n.nspname = 'public' and p.proname = 'import_stock') like '%CONVERT_IN%'
            then 'ok' else 'NOT SIGNED' end
union all
select 'negative_stock policy',
       coalesce(public.negative_stock_policy(), '(unset)')
union all
select 'buckets negative right now',
       (select count(*)::text from public.v_stock_position where balance_ct < 0)
 order by 1;


-- ###########################################################################
-- SECTION 4 of 17 : 0039_unknown_grade.sql
-- ###########################################################################

-- ---------------------------------------------------------------------------
-- 0039 · "Unknown Grade", so a row the sheet printed no grade against is
--         imported rather than refused.
--
-- WHY THIS EXISTS
--
-- The PDF reader used to stop the whole file when a printed row carried carats
-- but no grade name, on the reasoning that placing them under the row above or
-- the row below would be a guess. The reasoning is sound; the conclusion was
-- not. An import that refuses loses exactly as many carats as one that drops a
-- line -- and it loses the other twenty rows with them. A stock count sat
-- unimportable over one blank cell in one column.
--
-- So the reader now places those carats under this grade. Nothing is guessed:
-- the row keeps its own size, weight and rate, and it appears on the Stock page
-- under a name that says precisely what is known about it. Somebody who can
-- read the paper moves it to the real grade with an ADJUST, which is the same
-- correction path every other mistaken bucket takes (BR-INV-1).
--
-- Nothing is created by hand here. add_grade (0030) already does the whole job
-- -- the code, the display name, the sort order and the grade_size pairings
-- 0018's rule gives it -- and it is idempotent, so this file is safe to run
-- repeatedly and safe to run against a database that already has the grade.
-- ---------------------------------------------------------------------------

begin;

select public.add_grade('Unknown Grade', 'Unknown Grade');

commit;

-- ---------------------------------------------------------------------------
-- Verification · every row must read 'ok'.
-- ---------------------------------------------------------------------------
select 'the grade exists and is active' as case,
       case when exists (select 1 from public.grade
                          where code = 'Unknown Grade' and active)
            then 'ok' else 'MISSING' end as status
union all
select 'it is paired with every size the import can use',
       case when (select count(*) from public.grade_size gs
                    join public.grade g using (grade_id)
                   where g.code = 'Unknown Grade')
                 = (select count(*) from public.size_bucket where code <> '-2')
            then 'ok' else 'PAIRINGS MISSING' end
union all
select 'running it twice creates nothing',
       case when (public.add_grade('Unknown Grade') ->> 'created') = 'false'
            then 'ok' else 'CREATED A DUPLICATE' end;


-- ###########################################################################
-- SECTION 5 of 17 : 0040_edit_posted_invoice.sql
-- ###########################################################################

-- ---------------------------------------------------------------------------
-- 0040 · A posted invoice can be corrected, and the stock follows it.
--
-- THE PROBLEM
--
-- Posting is one-way. A figure typed wrong on an invoice that has already moved stock could
-- only be undone by cancelling it and entering the whole sale again -- two documents on the
-- ledger where the office only ever meant one, and an invoice number burned each time.
--
-- WHY NOT JUST LIFT THE DRAFT-ONLY GUARD
--
-- SaveDraftAsync deletes and re-inserts sales_line. On a POSTED invoice that leaves every
-- stock_movement pointing at a line_id that no longer exists, with the carats still deducted.
-- The position would stop reconciling against the invoices and nothing would say why. The guard
-- is not the problem; it is the only thing standing between that and a silent corruption.
--
-- WHY DELETE THE MOVEMENTS RATHER THAN REVERSE THEM
--
-- The obvious shape is cancel_invoice's: leave the SALE rows and add a signed ADJUST beside them.
-- It is wrong here. v_reconciliation (0026) compares SALE movements against the selection on
-- POSTED lines and does not net ADJUST at all -- so a reversal would leave the old SALE counted
-- for ever against lines that no longer exist, and every edited invoice would report a permanent
-- reconciliation failure that no one could clear.
--
-- So the movements this invoice wrote are deleted and written again, which is exactly what
-- import_stock(p_replace => true) already does with a stock sheet. Nothing is lost: audit_log
-- carries a DELETE row for every movement and every line removed, with their old values, so the
-- previous version of the invoice is fully recoverable from the trail.
--
-- WHAT IS PRESERVED
--
--   invoice_no    the same number. A correction is not a new document.
--   status        stays POSTED. It never becomes a draft, so it cannot be half-posted.
--   history       audit_log gets a DELETE per removed row and an INSERT per new one.
--   the reason    carried on every new movement, so the ledger says why the figures moved.
--
-- WHAT IS REFUSED
--
--   a DRAFT        -- that is save_draft's job, and it is cheaper
--   a CANCELLED    -- reversed already; correcting it would resurrect stock
--   a MIG- invoice -- imported sales deliberately move no stock (see 0022). Editing one here
--                     would write movements the opening balance already accounts for.
--   no reason      -- an unexplained edit of a posted document is worse than no edit
--   no lines       -- an invoice with nothing on it is a cancellation, which has its own path
--
-- ONE TRANSACTION. A plpgsql function is atomic: any raise below rolls back the deletions, the
-- new lines and the new movements together. There is no state in which the stock has been given
-- back but the invoice has not been rewritten.
-- ---------------------------------------------------------------------------

begin;

create or replace function public.edit_posted_invoice(
    p_invoice_id   bigint,
    p_invoice_date date,
    p_buyer_id     bigint,
    p_broker_id    bigint,
    p_broker_pct   numeric,
    p_terms_days   integer,
    p_doc_type     text,
    p_currency_id  bigint,
    p_lines        jsonb,
    p_reason       text
)
returns jsonb
language plpgsql
security definer
set search_path = public
as $$
declare
    v_status  text;
    v_no      text;
    v_policy  text;
    v_short   jsonb;
    v_reason  text := btrim(coalesce(p_reason, ''));
    v_note    text;
begin
    -- FOR UPDATE, not a bare select. Two desks correcting the same invoice at once would each
    -- read the movements the other was about to delete, and one set would be written twice.
    select status, invoice_no
      into v_status, v_no
      from public.sales_invoice
     where invoice_id = p_invoice_id
       for update;

    if not found then
        raise exception 'Invoice % not found', p_invoice_id using errcode = 'no_data_found';
    end if;

    if v_status = 'DRAFT' then
        raise exception 'Invoice % is a draft; save it the ordinary way', p_invoice_id;
    end if;

    if v_status = 'CANCELLED' then
        raise exception 'Invoice % is cancelled. Its stock has already been returned, so there is nothing to correct.', p_invoice_id;
    end if;

    if v_status <> 'POSTED' then
        raise exception 'Invoice % is %, which cannot be edited', p_invoice_id, v_status;
    end if;

    -- Imported sales never moved stock (0022). Rewriting one here would deduct carats the
    -- imported opening balance has already taken out.
    if coalesce(v_no, '') like 'MIG-%' then
        raise exception 'Invoice % was imported. Imported sales carry no stock movements, so editing one here would deduct its carats a second time.', v_no;
    end if;

    if v_reason = '' then
        raise exception 'Correcting a posted invoice requires a reason';
    end if;

    if p_lines is null or jsonb_typeof(p_lines) <> 'array' or jsonb_array_length(p_lines) = 0 then
        raise exception 'An invoice must keep at least one line. To remove it entirely, cancel it.';
    end if;

    v_note := 'Edit of invoice ' || coalesce(v_no, p_invoice_id::text) || ': ' || v_reason;

    -- ── 1 · give the stock back ────────────────────────────────────────────
    -- Deleted, not reversed. See the header: v_reconciliation counts SALE against posted lines,
    -- so a lingering SALE for a line that is about to go would never balance again. audit_log
    -- keeps a DELETE row with the old values for each one.
    delete from public.stock_movement m
     using public.sales_line l
     where m.ref_type = 'sales_line'
       and m.ref_id = l.line_id
       and l.invoice_id = p_invoice_id
       and m.movement_type in ('SALE', 'REJECTION');

    -- ── 2 · the old lines go with them ─────────────────────────────────────
    delete from public.sales_line where invoice_id = p_invoice_id;

    -- ── 3 · the corrected lines ────────────────────────────────────────────
    -- rejection_ct is GENERATED from gross - selection, so it is never inserted.
    insert into public.sales_line
        (invoice_id, grade_id, size_id, gross_weight_ct, selection_ct,
         price_per_ct, ex_rate, less1_pct, less2_pct, remark)
    select p_invoice_id,
           (r->>'grade_id')::bigint,
           (r->>'size_id')::bigint,
           (r->>'gross_weight_ct')::numeric,
           (r->>'selection_ct')::numeric,
           (r->>'price_per_ct')::numeric,
           coalesce(nullif((r->>'ex_rate')::numeric, 0), 1),
           coalesce((r->>'less1_pct')::numeric, 0),
           coalesce((r->>'less2_pct')::numeric, 0),
           nullif(btrim(coalesce(r->>'remark', '')), '')
      from jsonb_array_elements(p_lines) as r;

    -- ── 4 · the same guard posting uses, against the corrected figures ─────
    -- The stock is back at its pre-invoice level by now, so this asks the question the office
    -- would: does the bucket cover what this invoice NOW says left it.
    v_policy := public.negative_stock_policy();

    with need as (
        select grade_id, size_id, sum(gross_weight_ct) as out_ct
          from public.sales_line
         where invoice_id = p_invoice_id
         group by grade_id, size_id
    )
    select jsonb_agg(jsonb_build_object(
               'grade_code', sp.grade_code,
               'size_code',  sp.size_code,
               'balance_ct', sp.balance_ct,
               'needed_ct',  n.out_ct))
      into v_short
      from need n
      join public.v_stock_position sp
        on sp.grade_id = n.grade_id and sp.size_id = n.size_id
     where sp.balance_ct < n.out_ct;

    -- No needs_override branch. Posting offers one under 'warn' because the user is at the
    -- screen deciding; a correction that cannot be honoured should simply not be made, and the
    -- rollback puts the original invoice and its stock back untouched.
    if v_short is not null and v_policy = 'block' then
        raise exception 'This correction would take stock negative: %', v_short::text
            using errcode = 'check_violation';
    end if;

    -- ── 5 · the cost stamp, as posting does it (0019) ──────────────────────
    update public.sales_line l
       set cost_per_ct = nullif(sp.avg_cost, 0),
           cost_basis  = case when nullif(sp.avg_cost, 0) is null
                              then null else 'moving_average' end
      from public.v_stock_position sp
     where l.invoice_id = p_invoice_id
       and sp.grade_id  = l.grade_id
       and sp.size_id   = l.size_id;

    -- ── 6 · and the stock leaves again, on the corrected figures ───────────
    -- reason is carried so the ledger says why these rows differ from the ones audit_log shows
    -- being deleted a moment earlier.
    insert into public.stock_movement
        (movement_date, grade_id, size_id, movement_type, weight_ct,
         price_per_ct, ref_type, ref_id, created_by, reason)
    select p_invoice_date, l.grade_id, l.size_id, 'SALE', l.selection_ct,
           l.price_per_ct, 'sales_line', l.line_id, auth.uid(), v_note
      from public.sales_line l
     where l.invoice_id = p_invoice_id and l.selection_ct > 0;

    insert into public.stock_movement
        (movement_date, grade_id, size_id, movement_type, weight_ct,
         price_per_ct, ref_type, ref_id, created_by, reason)
    select p_invoice_date, l.grade_id, l.size_id, 'REJECTION', l.rejection_ct,
           l.price_per_ct, 'sales_line', l.line_id, auth.uid(), v_note
      from public.sales_line l
     where l.invoice_id = p_invoice_id and l.rejection_ct > 0;

    -- ── 7 · the header, keeping the number and the status ──────────────────
    update public.sales_invoice
       set invoice_date = p_invoice_date,
           buyer_id     = p_buyer_id,
           broker_id    = p_broker_id,
           broker_pct   = p_broker_pct,
           terms_days   = p_terms_days,
           doc_type     = p_doc_type,
           currency_id  = p_currency_id,
           updated_by   = auth.uid()
     where invoice_id = p_invoice_id;

    return jsonb_build_object('ok', true, 'invoice_no', v_no, 'reason', v_reason);
end;
$$;

revoke all on function public.edit_posted_invoice(bigint, date, bigint, bigint, numeric, integer, text, bigint, jsonb, text) from public;
revoke all on function public.edit_posted_invoice(bigint, date, bigint, bigint, numeric, integer, text, bigint, jsonb, text) from anon;
grant execute on function public.edit_posted_invoice(bigint, date, bigint, bigint, numeric, integer, text, bigint, jsonb, text) to authenticated;

comment on function public.edit_posted_invoice is
    'Corrects a POSTED invoice in one transaction: deletes the stock movements it wrote, replaces its lines, re-checks the negative-stock policy against the corrected figures, and writes the movements again. Keeps invoice_no and POSTED status. Refuses drafts, cancelled invoices and imported MIG- invoices. audit_log carries the removed rows.';

commit;


-- ---------------------------------------------------------------------------
-- Verification · read only. Every row must read 'ok'.
-- ---------------------------------------------------------------------------
select 'edit_posted_invoice exists' as check,
       case when exists (select 1 from pg_proc p join pg_namespace n on n.oid = p.pronamespace
                          where n.nspname = 'public' and p.proname = 'edit_posted_invoice')
            then 'ok' else 'MISSING' end as status
union all
select 'it refuses an imported MIG- invoice',
       case when (select prosrc from pg_proc p join pg_namespace n on n.oid = p.pronamespace
                   where n.nspname = 'public' and p.proname = 'edit_posted_invoice') like '%MIG-%%'
            then 'ok' else 'MISSING' end
union all
select 'it deletes rather than reverses, so reconciliation holds',
       case when (select prosrc from pg_proc p join pg_namespace n on n.oid = p.pronamespace
                   where n.nspname = 'public' and p.proname = 'edit_posted_invoice')
                 like '%delete from public.stock_movement%'
            then 'ok' else 'MISSING' end
union all
select 'it re-checks the negative-stock policy',
       case when (select prosrc from pg_proc p join pg_namespace n on n.oid = p.pronamespace
                   where n.nspname = 'public' and p.proname = 'edit_posted_invoice')
                 like '%negative_stock_policy%'
            then 'ok' else 'MISSING' end
union all
select 'anon cannot execute it',
       case when has_function_privilege('anon',
                'public.edit_posted_invoice(bigint, date, bigint, bigint, numeric, integer, text, bigint, jsonb, text)',
                'execute')
            then 'ANON CAN EXECUTE' else 'ok' end
union all
select 'buckets negative right now',
       (select count(*)::text from public.v_stock_position where balance_ct < 0)
 order by 1;


-- ###########################################################################
-- SECTION 6 of 17 : 0041_reconciliation_nets_cancellations_again.sql
-- ###########################################################################

-- ---------------------------------------------------------------------------
-- 0041 · A cancelled invoice must leave the reconciliation balanced.
--
-- THE FAULT
--
-- cancel_invoice (0010) does NOT delete the SALE movements. It leaves them and writes a signed
-- ADJUST beside each one, tagged ref_type = 'cancel', so both halves stay on the ledger and the
-- stock comes back. That is right, and v_stock_position handles it: 0008 signs ADJUST, so the
-- balance is correct the moment the cancellation lands.
--
-- v_reconciliation is where it goes wrong. It compares
--
--     moved_out_ct        every SALE movement, whatever its invoice now says
--     sold_on_invoices_ct the selection on POSTED lines only
--
-- so the instant an invoice is cancelled its carats leave the right-hand side and stay on the
-- left. The bucket reports a difference for ever, and no amount of correct trading clears it.
--
-- WHY IT CAME BACK
--
-- 0011 had this right:
--
--     where m.movement_type in ('SALE', 'REJECTION')
--        or (m.movement_type = 'ADJUST' and m.ref_type = 'cancel')
--
-- 0026 rewrote the view to keep migrated MIG- invoices out of it -- a real fix for a real
-- problem -- and in restating the whole thing dropped the netting. Nothing failed at the time
-- because neither database had a cancelled invoice yet.
--
-- THE FIX
--
-- Symmetry. sold_on_invoices_ct already ignores a cancelled invoice's lines; moved_out_ct now
-- ignores its movements. Both sides answer the same question -- "what does this business say it
-- sold" -- so a cancellation removes the sale from both at once and the bucket stays balanced.
--
-- Netting the ADJUST instead, as 0011 did, gives the same arithmetic. Excluding is clearer: a
-- cancelled sale is not a sale that was reversed, it is a sale that did not happen, and the ledger
-- keeps both movements either way.
--
-- NOT NETTED, DELIBERATELY: an ADJUST somebody recorded by hand. That is a real correction to a
-- real position and it belongs in the difference, which is the whole point of the report.
--
-- A VIEW ONLY. No data is touched, no movement is written or removed, and every balance on every
-- screen is exactly what it was a moment before this ran.
-- ---------------------------------------------------------------------------

begin;

create or replace view public.v_reconciliation as
select
    g.code                                  as grade_code,
    s.code                                  as size_code,
    coalesce(mv.sale_ct, 0)                 as moved_out_ct,
    coalesce(sl.sold_ct, 0)                 as sold_on_invoices_ct,
    round(coalesce(mv.sale_ct, 0) - coalesce(sl.sold_ct, 0), 4) as diff_ct,
    abs(coalesce(mv.sale_ct, 0) - coalesce(sl.sold_ct, 0)) < 0.0001 as reconciles
from public.grade g
cross join public.size_bucket s
left join lateral (
    select sum(m.weight_ct) as sale_ct
    from public.stock_movement m
    -- LEFT joined, and coalesced below: a SALE movement that points at no line is not something
    -- this view should hide. It has no invoice to excuse it, so it stays in the difference where
    -- somebody will see it.
    left join public.sales_line    l on l.line_id    = m.ref_id and m.ref_type = 'sales_line'
    left join public.sales_invoice i on i.invoice_id = l.invoice_id
    where m.grade_id = g.grade_id and m.size_id = s.size_id
      and m.movement_type = 'SALE'
      -- 0041. The cancelled invoice's lines already left sold_ct below; its movements leave here.
      and coalesce(i.status, 'POSTED') <> 'CANCELLED'
) mv on true
left join lateral (
    select sum(l.selection_ct) as sold_ct
    from public.sales_line l
    join public.sales_invoice i using (invoice_id)
    where l.grade_id = g.grade_id and l.size_id = s.size_id
      and i.status = 'POSTED'
      -- 0026. A migrated invoice never moved stock and never should.
      and coalesce(i.invoice_no, '') not like 'MIG-%'
) sl on true;

comment on view public.v_reconciliation is
    'CALC-8. Carats leaving stock as SALE movements against carats sold on invoices that were expected to move stock, per grade x size. Migrated MIG- invoices are excluded (0026): the imported opening balance is already net of them. Cancelled invoices are excluded from BOTH sides (0041): cancel_invoice leaves the SALE movement in place and reverses it with an ADJUST, so counting the movement while the line had already dropped out reported a difference that could never be cleared.';

commit;


-- ---------------------------------------------------------------------------
-- Verification · read only. Every row must read 'ok'.
-- ---------------------------------------------------------------------------
select 'the view exists' as check,
       case when exists (select 1 from pg_views where schemaname = 'public' and viewname = 'v_reconciliation')
            then 'ok' else 'MISSING' end as status
union all
select 'it excludes cancelled invoices',
       case when pg_get_viewdef('public.v_reconciliation'::regclass) like '%CANCELLED%'
            then 'ok' else 'MISSING' end
union all
select 'it still excludes migrated MIG- invoices',
       case when pg_get_viewdef('public.v_reconciliation'::regclass) like '%MIG-%%'
            then 'ok' else 'MISSING' end
union all
select 'buckets that do not reconcile',
       (select count(*)::text from public.v_reconciliation where not reconciles)
union all
select 'which ones',
       coalesce((select string_agg(grade_code || ' x ' || size_code || ' (' || trim_scale(diff_ct) || ' ct)', ', ')
                   from public.v_reconciliation where not reconciles), 'none')
union all
select 'buckets negative right now',
       (select count(*)::text from public.v_stock_position where balance_ct < 0)
 order by 1;


-- ###########################################################################
-- SECTION 7 of 17 : 0042_edit_imported_invoice.sql
-- ###########################################################################

-- ---------------------------------------------------------------------------
-- 0042 . An imported sale can be corrected, by moving only what changed.
--
-- WHY IT WAS REFUSED, AND WHY THAT WAS RIGHT
--
-- An imported MIG- invoice carries NO stock movements. replace_imported_sales (0018/0022) writes
-- none on purpose: the imported opening stock balance is already net of every sale on the sheet.
-- The carats those invoices sold left the bucket before this system existed.
--
-- 0040 corrects an invoice by deleting the movements it wrote and writing them again from the
-- corrected lines. Run against an imported invoice that becomes:
--
--   step 1  delete its movements          -> finds none, because there never were any
--   step 6  write them from the new lines -> takes the FULL new amount out of stock
--
-- so every carat on the invoice is deducted a second time, on top of an opening balance that had
-- already accounted for it. And v_reconciliation (0026) leaves MIG- lines out of
-- sold_on_invoices_ct while counting every SALE in moved_out_ct, so those movements would put the
-- bucket out of balance permanently, by an amount no amount of correct trading could clear.
--
-- The refusal was not a missing feature. It was the only thing standing between an edit and a
-- silent double deduction.
--
-- THE FIX
--
-- Move the DIFFERENCE, not the total. The old figures are already out of the opening balance, so
-- correcting 10 ct to 25 ct should take 15 ct more out, not 25.
--
-- Recorded as a signed ADJUST (0008) tagged ref_type = 'import_edit', never as a SALE:
--
--   * v_stock_position signs ADJUST, so the balance follows the correction exactly.
--   * v_reconciliation counts SALE only, so the bucket stays balanced: both sides continue to
--     ignore this invoice, which is what 0026 decided and what remains true.
--   * the ledger keeps the correction visible, with its reason on it, rather than silently
--     restating history.
--
-- The negative-stock guard asks the matching question. For an ordinary invoice the stock is back
-- at its pre-invoice level, so the bucket must cover the whole of the new figure. For an imported
-- one nothing was given back, so it need only cover the EXTRA.
--
-- Cost is deliberately still not stamped on imported lines. They carry no movements, so there is
-- no basis to stamp, 0024's margin view excludes them, and inventing a margin for a historical
-- sale would be worse than leaving it blank.
--
-- AND THE RE-IMPORT
--
-- replace_imported_sales deletes every MIG- invoice and re-inserts it from the sheet. It has never
-- touched stock_movement, because imported invoices had none. They can now. Left alone, a
-- re-import would restore the original lines and leave the adjustment applied, so stock would hold
-- a correction for an edit that no longer exists. It is cleared instead.
--
-- ONE TRANSACTION, as 0040 is: any raise rolls back the lot.
-- ---------------------------------------------------------------------------

begin;

-- ---------------------------------------------------------------------------
-- What changed, per bucket, between the invoice as it now stands and what it said before.
--
-- A function rather than the same CTE written twice: the guard and the movements must agree
-- exactly on what moved, and two copies of this arithmetic would be two chances to disagree.
--
-- FULL join, both ways: a bucket removed from the invoice (old, no new) must give its carats
-- back, and one newly added (new, no old) must take them out.
-- ---------------------------------------------------------------------------
create or replace function public.imported_edit_delta(p_invoice_id bigint, p_before jsonb)
returns table (grade_id bigint, size_id bigint, delta numeric)
language sql
stable
security definer
set search_path = public
as $fn$
    with old_ct as (
        select (r->>'g')::bigint   as grade_id,
               (r->>'s')::bigint   as size_id,
               (r->>'ct')::numeric as ct
          from jsonb_array_elements(coalesce(p_before, '[]'::jsonb)) r
    ),
    new_ct as (
        select l.grade_id, l.size_id, sum(l.gross_weight_ct) as ct
          from public.sales_line l
         where l.invoice_id = p_invoice_id
         group by l.grade_id, l.size_id
    )
    select coalesce(o.grade_id, n.grade_id),
           coalesce(o.size_id,  n.size_id),
           round(coalesce(n.ct, 0) - coalesce(o.ct, 0), 4)
      from old_ct o
      full join new_ct n on n.grade_id = o.grade_id and n.size_id = o.size_id;
$fn$;

comment on function public.imported_edit_delta is
    'CALC-9 (0042). Per grade x size, how much MORE (positive) or LESS (negative) gross weight an invoice now sells than the figures passed in. Used only for imported MIG- invoices, whose old carats are already out of the imported opening balance and so must not move a second time.';

revoke all on function public.imported_edit_delta(bigint, jsonb) from public;
revoke all on function public.imported_edit_delta(bigint, jsonb) from anon;
grant execute on function public.imported_edit_delta(bigint, jsonb) to authenticated;


create or replace function public.edit_posted_invoice(
    p_invoice_id   bigint,
    p_invoice_date date,
    p_buyer_id     bigint,
    p_broker_id    bigint,
    p_broker_pct   numeric,
    p_terms_days   integer,
    p_doc_type     text,
    p_currency_id  bigint,
    p_lines        jsonb,
    p_reason       text
)
returns jsonb
language plpgsql
security definer
set search_path = public
as $$
declare
    v_status  text;
    v_no      text;
    v_policy  text;
    v_short   jsonb;
    v_reason  text := btrim(coalesce(p_reason, ''));
    v_note    text;
    -- 0042. An imported invoice is corrected by DIFFERENCE, so what it said before is needed
    -- after its lines are gone.
    v_imported boolean;
    v_before   jsonb;
begin
    -- FOR UPDATE, not a bare select. Two desks correcting the same invoice at once would each
    -- read the movements the other was about to delete, and one set would be written twice.
    select status, invoice_no
      into v_status, v_no
      from public.sales_invoice
     where invoice_id = p_invoice_id
       for update;

    if not found then
        raise exception 'Invoice % not found', p_invoice_id using errcode = 'no_data_found';
    end if;

    if v_status = 'DRAFT' then
        raise exception 'Invoice % is a draft; save it the ordinary way', p_invoice_id;
    end if;

    if v_status = 'CANCELLED' then
        raise exception 'Invoice % is cancelled. Its stock has already been returned, so there is nothing to correct.', p_invoice_id;
    end if;

    if v_status <> 'POSTED' then
        raise exception 'Invoice % is %, which cannot be edited', p_invoice_id, v_status;
    end if;

    -- 0042. Imported sales are corrected by DIFFERENCE rather than refused. See the header.
    v_imported := coalesce(v_no, '') like 'MIG-%';

    if v_reason = '' then
        raise exception 'Correcting a posted invoice requires a reason';
    end if;

    if p_lines is null or jsonb_typeof(p_lines) <> 'array' or jsonb_array_length(p_lines) = 0 then
        raise exception 'An invoice must keep at least one line. To remove it entirely, cancel it.';
    end if;

    v_note := 'Edit of invoice ' || coalesce(v_no, p_invoice_id::text) || ': ' || v_reason;

    -- ── 1 · give the stock back ────────────────────────────────────────────
    -- Deleted, not reversed. See the header: v_reconciliation counts SALE against posted lines,
    -- so a lingering SALE for a line that is about to go would never balance again. audit_log
    -- keeps a DELETE row with the old values for each one.
    delete from public.stock_movement m
     using public.sales_line l
     where m.ref_type = 'sales_line'
       and m.ref_id = l.line_id
       and l.invoice_id = p_invoice_id
       and m.movement_type in ('SALE', 'REJECTION');

    -- ── 1b · what an imported invoice said, before it is gone ───────────────
    -- These carats are ALREADY out of the opening balance. Only the difference between them and
    -- the corrected figures may move, which is the whole of why an imported invoice can now be
    -- edited at all.
    if v_imported then
        select jsonb_agg(jsonb_build_object('g', grade_id, 's', size_id, 'ct', out_ct))
          into v_before
          from (select grade_id, size_id, sum(gross_weight_ct) as out_ct
                  from public.sales_line
                 where invoice_id = p_invoice_id
                 group by grade_id, size_id) x;
    end if;

    -- ── 2 · the old lines go with them ─────────────────────────────────────
    delete from public.sales_line where invoice_id = p_invoice_id;

    -- ── 3 · the corrected lines ────────────────────────────────────────────
    -- rejection_ct is GENERATED from gross - selection, so it is never inserted.
    insert into public.sales_line
        (invoice_id, grade_id, size_id, gross_weight_ct, selection_ct,
         price_per_ct, ex_rate, less1_pct, less2_pct, remark)
    select p_invoice_id,
           (r->>'grade_id')::bigint,
           (r->>'size_id')::bigint,
           (r->>'gross_weight_ct')::numeric,
           (r->>'selection_ct')::numeric,
           (r->>'price_per_ct')::numeric,
           coalesce(nullif((r->>'ex_rate')::numeric, 0), 1),
           coalesce((r->>'less1_pct')::numeric, 0),
           coalesce((r->>'less2_pct')::numeric, 0),
           nullif(btrim(coalesce(r->>'remark', '')), '')
      from jsonb_array_elements(p_lines) as r;

    -- ── 4 · the same guard posting uses, against the corrected figures ─────
    -- The stock is back at its pre-invoice level by now, so this asks the question the office
    -- would: does the bucket cover what this invoice NOW says left it.
    v_policy := public.negative_stock_policy();

    with need as (
        -- An ordinary invoice: its stock is back at the pre-invoice level by now, so the question
        -- is whether the bucket covers the whole of what the invoice NOW says left it.
        --
        -- An imported one: nothing was given back, because nothing had been taken. The bucket
        -- already reflects the old figures, so the question is only whether it covers the EXTRA.
        select grade_id, size_id, sum(gross_weight_ct) as out_ct
          from public.sales_line
         where invoice_id = p_invoice_id and not v_imported
         group by grade_id, size_id
        union all
        select grade_id, size_id, delta
          from public.imported_edit_delta(p_invoice_id, v_before)
         where v_imported and delta > 0
    )
    select jsonb_agg(jsonb_build_object(
               'grade_code', sp.grade_code,
               'size_code',  sp.size_code,
               'balance_ct', sp.balance_ct,
               'needed_ct',  n.out_ct))
      into v_short
      from need n
      join public.v_stock_position sp
        on sp.grade_id = n.grade_id and sp.size_id = n.size_id
     where sp.balance_ct < n.out_ct;

    -- No needs_override branch. Posting offers one under 'warn' because the user is at the
    -- screen deciding; a correction that cannot be honoured should simply not be made, and the
    -- rollback puts the original invoice and its stock back untouched.
    if v_short is not null and v_policy = 'block' then
        raise exception 'This correction would take stock negative: %', v_short::text
            using errcode = 'check_violation';
    end if;

    -- ── 5 · the cost stamp, as posting does it (0019) ──────────────────────
    -- Imported lines are deliberately left uncostable, exactly as the import leaves them: they
    -- carry no movements, so there is no basis to stamp, and 0024's margin view excludes them.
    -- Stamping one here would invent a margin for a historical sale.
    update public.sales_line l
       set cost_per_ct = nullif(sp.avg_cost, 0),
           cost_basis  = case when nullif(sp.avg_cost, 0) is null
                              then null else 'moving_average' end
      from public.v_stock_position sp
     where l.invoice_id = p_invoice_id
       and not v_imported
       and sp.grade_id  = l.grade_id
       and sp.size_id   = l.size_id;

    -- ── 6 · and the stock leaves again, on the corrected figures ───────────
    -- reason is carried so the ledger says why these rows differ from the ones audit_log shows
    -- being deleted a moment earlier.
    insert into public.stock_movement
        (movement_date, grade_id, size_id, movement_type, weight_ct,
         price_per_ct, ref_type, ref_id, created_by, reason)
    select p_invoice_date, l.grade_id, l.size_id, 'SALE', l.selection_ct,
           l.price_per_ct, 'sales_line', l.line_id, auth.uid(), v_note
      from public.sales_line l
     where l.invoice_id = p_invoice_id and l.selection_ct > 0 and not v_imported;

    insert into public.stock_movement
        (movement_date, grade_id, size_id, movement_type, weight_ct,
         price_per_ct, ref_type, ref_id, created_by, reason)
    select p_invoice_date, l.grade_id, l.size_id, 'REJECTION', l.rejection_ct,
           l.price_per_ct, 'sales_line', l.line_id, auth.uid(), v_note
      from public.sales_line l
     where l.invoice_id = p_invoice_id and l.rejection_ct > 0 and not v_imported;

    -- ── 6b · an imported invoice moves the DIFFERENCE, and nothing else ───────
    -- ADJUST, signed (0008), never SALE. Two reasons, and both matter:
    --
    --   the arithmetic — the old carats are already out of the opening balance, so writing the
    --   new figures in full would take them out twice. Only new minus old may move.
    --
    --   the report — v_reconciliation (0026) leaves MIG- lines out of sold_on_invoices_ct while
    --   counting every SALE in moved_out_ct. A SALE here would put the bucket out of balance for
    --   ever, by an amount no correct trading could clear. ADJUST is counted by neither side.
    --
    -- The sign is old minus new: selling MORE means more carats leave, which is a NEGATIVE
    -- adjustment to the balance.
    if v_imported then
        insert into public.stock_movement
            (movement_date, grade_id, size_id, movement_type, weight_ct,
             price_per_ct, ref_type, ref_id, created_by, reason)
        select p_invoice_date, d.grade_id, d.size_id, 'ADJUST', -d.delta,
               null, 'import_edit', p_invoice_id, auth.uid(), v_note
          from public.imported_edit_delta(p_invoice_id, v_before) d
         where d.delta <> 0;
    end if;

    -- ── 7 · the header, keeping the number and the status ──────────────────
    update public.sales_invoice
       set invoice_date = p_invoice_date,
           buyer_id     = p_buyer_id,
           broker_id    = p_broker_id,
           broker_pct   = p_broker_pct,
           terms_days   = p_terms_days,
           doc_type     = p_doc_type,
           currency_id  = p_currency_id,
           updated_by   = auth.uid()
     where invoice_id = p_invoice_id;

    return jsonb_build_object('ok', true, 'invoice_no', v_no, 'reason', v_reason);
end;
$$;


revoke all on function public.edit_posted_invoice(bigint, date, bigint, bigint, numeric, integer, text, bigint, jsonb, text) from public;
revoke all on function public.edit_posted_invoice(bigint, date, bigint, bigint, numeric, integer, text, bigint, jsonb, text) from anon;
grant execute on function public.edit_posted_invoice(bigint, date, bigint, bigint, numeric, integer, text, bigint, jsonb, text) to authenticated;

comment on function public.edit_posted_invoice is
    'Corrects a POSTED invoice in one transaction: replaces its lines, re-checks the negative-stock policy against the corrected figures, and rewrites the stock. An ordinary invoice has its SALE/REJECTION movements deleted and written again in full. An IMPORTED MIG- invoice has no movements of its own (its carats are already out of the imported opening balance) so it moves only the difference, as a signed ADJUST tagged import_edit (0042). Keeps invoice_no and POSTED status. Refuses drafts and cancelled invoices. audit_log carries the removed rows.';


-- ---------------------------------------------------------------------------
-- A re-import must take the correction with it.
--
-- replace_imported_sales has never touched stock_movement, and until 0042 it never needed to: an
-- imported invoice had none. It can now carry an import_edit adjustment, and restoring the
-- sheet's original lines while leaving that adjustment applied would hold stock at a correction
-- whose invoice no longer exists.
--
-- Scoped to import_edit alone. Nothing else in stock_movement is the importer's to remove.
-- ---------------------------------------------------------------------------
create or replace function public.clear_import_edits(p_invoice_ids bigint[])
returns integer
language sql
security definer
set search_path = public
as $fn$
    with gone as (
        delete from public.stock_movement
         where ref_type = 'import_edit'
           and ref_id = any(p_invoice_ids)
        returning 1
    )
    select coalesce(count(*), 0)::integer from gone;
$fn$;

comment on function public.clear_import_edits is
    '0042. Removes the import_edit stock adjustments belonging to the given invoices. Called by replace_imported_sales before it deletes them, so a re-import does not leave stock holding a correction for an edit that has been replaced.';

revoke all on function public.clear_import_edits(bigint[]) from public;
revoke all on function public.clear_import_edits(bigint[]) from anon;
grant execute on function public.clear_import_edits(bigint[]) to authenticated;


-- The importer, taught to take the correction with it. Identical to 0022 in every other respect.
create or replace function public.replace_imported_sales(p_payload jsonb)
returns jsonb
language plpgsql
security definer
set search_path = public
as $$
declare
    v_old_ids     bigint[];
    v_deleted     integer := 0;
    v_invoices    integer := 0;
    v_lines       integer := 0;
    v_receipts    integer := 0;
    v_currency    bigint;
begin
    if p_payload is null or jsonb_typeof(p_payload->'invoices') <> 'array' then
        raise exception 'replace_imported_sales expects {"invoices": [...]}';
    end if;

    if jsonb_array_length(p_payload->'invoices') = 0 then
        raise exception 'replace_imported_sales was given no invoices';
    end if;

    v_currency := (p_payload->>'currency_id')::bigint;
    if v_currency is null then
        raise exception 'replace_imported_sales needs a currency_id';
    end if;

    -- Only ever the previous import. A live invoice carries INV-yyyy-nnnnn and
    -- is not matched by this; 08 §4 is why migrated numbers are prefixed at all.
    select coalesce(array_agg(invoice_id), '{}')
      into v_old_ids
      from public.sales_invoice
     where invoice_no like 'MIG-%';

    if array_length(v_old_ids, 1) is not null then
        -- 0042. An imported invoice can now carry an import_edit stock adjustment, from having
        -- been corrected here. Restoring the sheet's original lines while leaving that adjustment
        -- applied would hold stock at a correction whose invoice is being replaced. Cleared first,
        -- inside the same transaction, so the sheet and the stock cannot disagree.
        perform public.clear_import_edits(v_old_ids);

        delete from public.receipt     where invoice_id = any(v_old_ids);
        delete from public.sales_line  where invoice_id = any(v_old_ids);
        delete from public.sales_invoice where invoice_id = any(v_old_ids);
        get diagnostics v_deleted = row_count;
    end if;

    -- Invoices first, keeping invoice_no as the handle to hang lines off: the
    -- ids are assigned by the sequence and the payload cannot know them.
    with incoming as (
        select inv from jsonb_array_elements(p_payload->'invoices') as inv
    ),
    written as (
        insert into public.sales_invoice
            (invoice_no, invoice_date, buyer_id, broker_id, broker_pct,
             terms_days, doc_type, currency_id, status,
             created_by, updated_by)
        select inv->>'invoice_no',
               (inv->>'invoice_date')::date,
               (inv->>'buyer_id')::bigint,
               nullif(inv->>'broker_id', '')::bigint,
               coalesce((inv->>'broker_pct')::numeric, 0),
               coalesce((inv->>'terms_days')::integer, 0),
               coalesce(inv->>'doc_type', 'BILL'),
               v_currency,
               'POSTED',
               auth.uid(),
               auth.uid()
          from incoming
        returning invoice_id, invoice_no
    )
    select count(*) into v_invoices from written;

    -- Lines, matched back by invoice_no.
    with incoming as (
        select inv->>'invoice_no' as no,
               jsonb_array_elements(coalesce(inv->'lines', '[]'::jsonb)) as ln
          from jsonb_array_elements(p_payload->'invoices') as inv
    )
    insert into public.sales_line
        (invoice_id, grade_id, size_id, gross_weight_ct, selection_ct,
         price_per_ct, ex_rate, less1_pct, less2_pct, remark)
    select i.invoice_id,
           (c.ln->>'grade_id')::bigint,
           (c.ln->>'size_id')::bigint,
           (c.ln->>'gross_weight_ct')::numeric,
           (c.ln->>'selection_ct')::numeric,
           (c.ln->>'price_per_ct')::numeric,
           coalesce((c.ln->>'ex_rate')::numeric, 1),
           coalesce((c.ln->>'less1_pct')::numeric, 0),
           coalesce((c.ln->>'less2_pct')::numeric, 0),
           nullif(c.ln->>'remark', '')
      from incoming c
      join public.sales_invoice i on i.invoice_no = c.no;

    get diagnostics v_lines = row_count;

    -- One receipt per invoice that carried money, exactly as the workbook's
    -- single overwritten "Rec. Amt" cell states it (DQ-11: there is no payment
    -- history to migrate, only a running total). Dated the invoice date, since
    -- the sheet records no payment date -- docs/08 §5 says declare it, not bury
    -- it. Method 'IMPORTED', unchanged from the client-side importer.
    --
    -- `received` arrives ALREADY CAPPED at the invoice total. The cap stays on
    -- the client because it needs the line amounts CALC-1 produces, and those
    -- are the calculation engine's to compute, not this function's.
    with incoming as (
        select inv->>'invoice_no' as no,
               (inv->>'received')::numeric as received,
               (inv->>'invoice_date')::date as on_date
          from jsonb_array_elements(p_payload->'invoices') as inv
    )
    insert into public.receipt (invoice_id, receipt_date, amount, method, created_by)
    select i.invoice_id, c.on_date, c.received, 'IMPORTED', auth.uid()
      from incoming c
      join public.sales_invoice i on i.invoice_no = c.no
     where c.received is not null and c.received > 0;

    get diagnostics v_receipts = row_count;

    return jsonb_build_object('ok', true, 'deleted', v_deleted,
                              'invoices', v_invoices, 'lines', v_lines,
                              'receipts', v_receipts);
end;
$$;

commit;


-- ###########################################################################
-- SECTION 8 of 17 : 0043_reserve_stock_at_entry.sql
-- ###########################################################################

-- ---------------------------------------------------------------------------
-- 0043 · Stock falls as a line is entered, not when the sale is confirmed.
--
-- WHAT WAS ASKED FOR
--
--   Sales Entry   -> stock decreases, grade x size, as each line is completed
--   Confirm Sale  -> confirms the invoice, and must NOT deduct again
--   Move to Stock -> returns what that entry took, and never more
--   No double deduction, no duplicate movement
--
-- WHY THIS IS NOT A stock_movement
--
-- The obvious implementation is to write a SALE movement the moment a line has a grade, a size and
-- a weight. It cannot be, for three reasons, and none of them is a matter of taste:
--
--   v_reconciliation compares SALE movements against the lines of POSTED invoices. A SALE written
--   for a line nobody has confirmed puts its bucket out of balance the instant it is typed, and it
--   stays out until the sale is confirmed -- or for ever, if it never is.
--
--   A weight is TYPED, and typed things change. 10.00 corrected to 1.00 would leave two movements
--   and a third to net them, so the ledger fills with the history of somebody's keystrokes for a
--   parcel that may never be sold at all.
--
--   An entry abandoned -- the window closed, the app killed, the machine off -- would leave carats
--   deducted with no invoice anywhere to explain where they went.
--
-- A RESERVATION says the true thing instead: these carats are spoken for. It comes off the balance
-- every screen reads, so the stock page shows the reduced figure immediately, which is what was
-- asked for. It is not a movement, so the ledger and the reconciliation report are untouched. And
-- it can be changed, released or converted, because nothing has happened yet.
--
-- ONE ROW PER LINE, which is what makes duplication structural rather than something to remember:
-- unique (client_ref, line_key). Re-reserving the same line UPDATES its row. Type 10.00, correct it
-- to 1.00, correct it again -- one row throughout, holding whatever the line currently says.
--
-- AND THE HANDOVER TO post_invoice
--
-- Confirming converts: post_invoice writes the SALE and REJECTION movements as it always has, and
-- releases that entry's reservations in the SAME transaction. The carats are held by exactly one
-- mechanism at every instant -- reserved before, moved after, never both, never neither.
--
-- Move to Stock is release_entry: it deletes the rows that entry holds and the balance returns by
-- exactly what they held. It cannot return more than was taken, because there is nothing else it
-- can delete.
-- ---------------------------------------------------------------------------

begin;

-- ---------------------------------------------------------------------------
-- What is spoken for, and by which line of which entry.
--
-- client_ref is the entry on screen, which HAS one before it is saved (0007) -- so a line can be
-- reserved against an invoice that does not exist yet, which is the whole requirement.
--
-- line_key is the app's own handle for the row. It is text and not a line_id because sales_line
-- rows do not exist while somebody is typing.
-- ---------------------------------------------------------------------------
create table if not exists public.stock_reservation (
    reservation_id bigserial primary key,
    client_ref     uuid          not null,
    line_key       text          not null,
    grade_id       bigint        not null references public.grade(grade_id),
    size_id        bigint        not null references public.size_bucket(size_id),
    weight_ct      numeric(14,4) not null check (weight_ct >= 0),
    created_by     uuid,
    created_at     timestamptz   not null default now(),
    -- THE anti-duplication rule, in the schema rather than in anybody's memory.
    constraint stock_reservation_line_uq unique (client_ref, line_key)
);

create index if not exists stock_reservation_bucket_ix
    on public.stock_reservation (grade_id, size_id);

comment on table public.stock_reservation is
    '0043. Carats spoken for by a sales entry that has not been confirmed. Netted out of v_stock_position so every screen shows the reduced figure, but NOT a stock_movement: the ledger and v_reconciliation only ever see confirmed sales. One row per (client_ref, line_key), so editing a line updates its reservation instead of adding another.';

alter table public.stock_reservation enable row level security;

drop policy if exists stock_reservation_rw on public.stock_reservation;
create policy stock_reservation_rw on public.stock_reservation
    for all to authenticated using (true) with check (true);


-- ---------------------------------------------------------------------------
-- The position, less what is spoken for.
--
-- Identical to 0008 in every other respect: the movement arithmetic, the cost basis, the ageing.
-- Only balance_ct changes, and only by subtracting the reservations held against that bucket.
--
-- stock_value keeps using greatest(..., 0) on the RESERVED balance too, so a bucket reserved down
-- to nothing is worth nothing rather than a negative figure.
-- ---------------------------------------------------------------------------
create or replace view public.v_stock_position as
with movement as (
    select
        m.grade_id,
        m.size_id,
        case when m.movement_type in ('INTAKE', 'CONVERT_IN') then m.weight_ct
             when m.movement_type = 'ADJUST'                  then m.weight_ct
             else -m.weight_ct end                            as signed_ct,
        case when m.movement_type in ('INTAKE', 'CONVERT_IN') then m.weight_ct
             when m.movement_type = 'ADJUST' and m.weight_ct > 0 then m.weight_ct
             else 0 end                                       as inward_ct,
        case when m.movement_type in ('INTAKE', 'CONVERT_IN')
             then m.weight_ct * coalesce(m.price_per_ct, 0)
             when m.movement_type = 'ADJUST' and m.weight_ct > 0
             then m.weight_ct * coalesce(m.price_per_ct, 0)
             else 0 end                                       as inward_value,
        case when m.movement_type in ('INTAKE', 'CONVERT_IN')
             then m.movement_date end                         as inward_date
    from public.stock_movement m
),
held as (
    select r.grade_id, r.size_id, sum(r.weight_ct) as reserved_ct
      from public.stock_reservation r
     group by r.grade_id, r.size_id
)
select
    g.grade_id,
    g.code                          as grade_code,
    g.display_name                  as grade_name,
    s.size_id,
    s.code                          as size_code,
    round(coalesce(sum(mv.signed_ct), 0) - coalesce(min(h.reserved_ct), 0), 4) as balance_ct,
    case when coalesce(sum(mv.inward_ct), 0) > 0
         then sum(mv.inward_value) / sum(mv.inward_ct)
         else 0 end                           as avg_cost,
    round(
        greatest(coalesce(sum(mv.signed_ct), 0) - coalesce(min(h.reserved_ct), 0), 0)
        * case when coalesce(sum(mv.inward_ct), 0) > 0
               then sum(mv.inward_value) / sum(mv.inward_ct)
               else 0 end
    , 2)                                      as stock_value,
    min(mv.inward_date)                       as oldest_intake,
    current_date - min(mv.inward_date)        as age_days
from public.grade g
cross join public.size_bucket s
left join movement mv on mv.grade_id = g.grade_id and mv.size_id = s.size_id
left join held h      on h.grade_id  = g.grade_id and h.size_id  = s.size_id
group by g.grade_id, g.code, g.display_name, s.size_id, s.code;

comment on view public.v_stock_position is
    'CALC-6/7. Balance, average cost and value per grade x size. Since 0043 the balance is net of stock_reservation: carats a sales entry has spoken for are already out of it, so the figure on screen is what is actually available to sell.';


-- ---------------------------------------------------------------------------
-- Hold what a line says. Called again whenever that line changes.
--
-- UPSERT on (client_ref, line_key), so a weight corrected from 10 to 1 leaves one reservation
-- holding 1 -- not two holding 11. That is the no-duplicate rule, and it is the schema's to keep.
--
-- Refuses to take a bucket negative, against the same policy posting uses. The check is made
-- against the balance WITH this line's existing reservation excluded, or correcting 10.00 down to
-- 9.00 would be measured against a balance the old 10.00 was still being subtracted from.
-- ---------------------------------------------------------------------------
create or replace function public.reserve_line(
    p_client_ref uuid,
    p_line_key   text,
    p_grade_id   bigint,
    p_size_id    bigint,
    p_weight_ct  numeric
)
returns jsonb
language plpgsql
security definer
set search_path = public
as $$
declare
    v_available numeric;
    v_policy    text;
begin
    if p_client_ref is null or coalesce(btrim(p_line_key), '') = '' then
        raise exception 'reserve_line needs an entry and a line';
    end if;

    if p_weight_ct is null or p_weight_ct <= 0 then
        -- Nothing to hold. A line emptied back out releases instead of reserving zero, so no row
        -- is left behind claiming nothing.
        delete from public.stock_reservation
         where client_ref = p_client_ref and line_key = p_line_key;
        return jsonb_build_object('ok', true, 'reserved', 0);
    end if;

    select coalesce(sp.balance_ct, 0)
           + coalesce((select r.weight_ct from public.stock_reservation r
                        where r.client_ref = p_client_ref and r.line_key = p_line_key), 0)
      into v_available
      from public.v_stock_position sp
     where sp.grade_id = p_grade_id and sp.size_id = p_size_id;

    v_policy := public.negative_stock_policy();

    if v_policy = 'block' and coalesce(v_available, 0) < p_weight_ct then
        raise exception 'Only % ct available in that bucket', coalesce(v_available, 0)
            using errcode = 'check_violation';
    end if;

    insert into public.stock_reservation
        (client_ref, line_key, grade_id, size_id, weight_ct, created_by)
    values (p_client_ref, p_line_key, p_grade_id, p_size_id, p_weight_ct, auth.uid())
    on conflict (client_ref, line_key) do update
        set grade_id  = excluded.grade_id,
            size_id   = excluded.size_id,
            weight_ct = excluded.weight_ct;

    return jsonb_build_object('ok', true, 'reserved', p_weight_ct);
end;
$$;


-- ---------------------------------------------------------------------------
-- Give one line's carats back. Removing a line, or emptying it.
-- ---------------------------------------------------------------------------
create or replace function public.release_line(p_client_ref uuid, p_line_key text)
returns jsonb
language plpgsql
security definer
set search_path = public
as $$
declare v_ct numeric := 0;
begin
    delete from public.stock_reservation
     where client_ref = p_client_ref and line_key = p_line_key
    returning weight_ct into v_ct;

    return jsonb_build_object('ok', true, 'returned', coalesce(v_ct, 0));
end;
$$;


-- ---------------------------------------------------------------------------
-- MOVE TO STOCK. Gives back everything this entry is holding, and nothing else.
--
-- It cannot return more than was taken: the only rows it can delete are the ones this entry holds,
-- and each holds exactly what its line last said. There is no figure passed in to get wrong.
--
-- Idempotent by the same fact. Pressing it twice returns the carats once and then reports zero,
-- because the second call finds nothing left to delete.
-- ---------------------------------------------------------------------------
create or replace function public.release_entry(p_client_ref uuid)
returns jsonb
language plpgsql
security definer
set search_path = public
as $$
declare v_ct numeric; v_n integer;
begin
    with gone as (
        delete from public.stock_reservation
         where client_ref = p_client_ref
        returning weight_ct
    )
    select coalesce(sum(weight_ct), 0), count(*) into v_ct, v_n from gone;

    return jsonb_build_object('ok', true, 'returned', v_ct, 'lines', v_n);
end;
$$;


-- ---------------------------------------------------------------------------
-- Confirming converts a hold into a movement. It must not do both.
--
-- post_invoice already writes the SALE and REJECTION movements; this releases the same entry's
-- reservations in that transaction, so at no instant are the carats subtracted twice. Either the
-- reservation holds them, or the movements do.
--
-- A trigger, not an edit to post_invoice: that function is 0019's and is rewritten by later
-- migrations, and a hook that survives being rewritten is worth more than one that has to be
-- re-applied every time somebody touches it.
-- ---------------------------------------------------------------------------
create or replace function public.release_on_post() returns trigger
language plpgsql
security definer
set search_path = public
as $$
begin
    if new.status = 'POSTED' and coalesce(old.status, '') <> 'POSTED'
       and new.client_ref is not null then
        delete from public.stock_reservation where client_ref = new.client_ref;
    end if;
    return new;
end;
$$;

drop trigger if exists sales_invoice_release_reservations on public.sales_invoice;
create trigger sales_invoice_release_reservations
    after update on public.sales_invoice
    for each row execute function public.release_on_post();

comment on function public.release_on_post is
    '0043. When an invoice becomes POSTED its entry stops holding reservations: the SALE and REJECTION movements post_invoice writes are now what subtract those carats. Without this the same parcel would be out of stock twice.';


revoke all on function public.reserve_line(uuid, text, bigint, bigint, numeric) from public, anon;
revoke all on function public.release_line(uuid, text) from public, anon;
revoke all on function public.release_entry(uuid) from public, anon;
grant execute on function public.reserve_line(uuid, text, bigint, bigint, numeric) to authenticated;
grant execute on function public.release_line(uuid, text) to authenticated;
grant execute on function public.release_entry(uuid) to authenticated;
grant select, insert, update, delete on public.stock_reservation to authenticated;
grant usage, select on sequence public.stock_reservation_reservation_id_seq to authenticated;

commit;


-- ###########################################################################
-- SECTION 9 of 17 : 0044_stock_position_ledger_column.sql
-- ###########################################################################

begin;

-- ---------------------------------------------------------------------------
-- 0044  The ledger balance, beside the available one
--
-- WHY. 0043 made balance_ct net of stock_reservation, which is right for the Stock page: what it
-- shows is what is left to sell. But the Stock REPORT is the office's sheet, and it prints
--     TOTAL STOCK  -  SALES  =  ON HAND
-- with TOTAL derived as on-hand plus sales. Reserved carats are in neither term, so the moment a
-- sales entry held 5 ct the report's TOTAL fell by 5 -- carats that physically exist, are not sold,
-- and appeared in no figure on the page. Holds now survive a restart, so a forgotten entry would
-- have understated TOTAL indefinitely with nothing on screen to explain it.
--
-- The desk's decision: the report is a LEDGER document and must not move when a parcel is spoken
-- for. So the view carries both figures and each screen reads the one it means.
--
--   balance_ct   what is AVAILABLE  -- movements less reservations.  Stock page.
--   ledger_ct    what is HELD       -- movements only.               Stock report.
--   reserved_ct  the difference, named, so the gap between them is never a mystery.
--
-- Nothing about the arithmetic changes. balance_ct is the same expression 0043 left; the two new
-- columns are the halves it was already computing. Adding columns to a view is safe for existing
-- readers -- PostgREST selects by name, and nothing selects *.
--
-- THE NEW COLUMNS GO LAST, and they have to: "create or replace view" may only APPEND to the
-- select list. Putting them beside balance_ct where they read best made Postgres see avg_cost
-- being renamed to ledger_ct and refuse the whole statement (42P16). Dropping the view instead
-- would have taken every dependent view with it, which is a far worse trade for tidier source.
-- ---------------------------------------------------------------------------

create or replace view public.v_stock_position as
with movement as (
    select
        m.grade_id,
        m.size_id,
        case when m.movement_type in ('INTAKE', 'CONVERT_IN') then m.weight_ct
             when m.movement_type = 'ADJUST'                  then m.weight_ct
             else -m.weight_ct end                            as signed_ct,
        case when m.movement_type in ('INTAKE', 'CONVERT_IN') then m.weight_ct
             when m.movement_type = 'ADJUST' and m.weight_ct > 0 then m.weight_ct
             else 0 end                                       as inward_ct,
        case when m.movement_type in ('INTAKE', 'CONVERT_IN')
             then m.weight_ct * coalesce(m.price_per_ct, 0)
             when m.movement_type = 'ADJUST' and m.weight_ct > 0
             then m.weight_ct * coalesce(m.price_per_ct, 0)
             else 0 end                                       as inward_value,
        case when m.movement_type in ('INTAKE', 'CONVERT_IN')
             then m.movement_date end                         as inward_date
    from public.stock_movement m
),
held as (
    select r.grade_id, r.size_id, sum(r.weight_ct) as reserved_ct
      from public.stock_reservation r
     group by r.grade_id, r.size_id
)
select
    g.grade_id,
    g.code                          as grade_code,
    g.display_name                  as grade_name,
    s.size_id,
    s.code                          as size_code,

    -- AVAILABLE. Unchanged from 0043.
    round(coalesce(sum(mv.signed_ct), 0) - coalesce(min(h.reserved_ct), 0), 4) as balance_ct,

    case when coalesce(sum(mv.inward_ct), 0) > 0
         then sum(mv.inward_value) / sum(mv.inward_ct)
         else 0 end                           as avg_cost,
    round(
        greatest(coalesce(sum(mv.signed_ct), 0) - coalesce(min(h.reserved_ct), 0), 0)
        * case when coalesce(sum(mv.inward_ct), 0) > 0
               then sum(mv.inward_value) / sum(mv.inward_ct)
               else 0 end
    , 2)                                      as stock_value,
    min(mv.inward_date)                       as oldest_intake,
    current_date - min(mv.inward_date)        as age_days,

    -- LAST, per the note above -- not because they belong here.
    --
    -- HELD. The movement ledger alone: what a stock take would count, whatever is spoken for.
    round(coalesce(sum(mv.signed_ct), 0), 4)                                   as ledger_ct,

    -- The difference, named. min() because the left join repeats the bucket's single held row
    -- across every movement row -- the same reason balance_ct uses it.
    round(coalesce(min(h.reserved_ct), 0), 4)                                  as reserved_ct
from public.grade g
cross join public.size_bucket s
left join movement mv on mv.grade_id = g.grade_id and mv.size_id = s.size_id
left join held h      on h.grade_id  = g.grade_id and h.size_id  = s.size_id
group by g.grade_id, g.code, g.display_name, s.size_id, s.code;

comment on view public.v_stock_position is
    'CALC-6/7. Balance, average cost and value per grade x size. balance_ct is what is AVAILABLE: movements less the carats a sales entry has spoken for (0043). ledger_ct is what is HELD: the movement ledger alone, which is what the Stock report prints so that a reservation never moves a ledger document (0044). reserved_ct is the difference between them.';

commit;


-- ###########################################################################
-- SECTION 10 of 17 : 0045_audit_stock_reservation.sql
-- ###########################################################################

begin;

-- ---------------------------------------------------------------------------
-- 0045  Reserving and releasing stock goes on the audit trail
--
-- WHY. 0043 gave a sales entry the power to take carats out of the available position the moment
-- a line is typed, and 0044 gave the office a way to see how much is spoken for. Neither left a
-- trace of WHO spoke for it or WHEN. Every other act that moves a figure on a screen -- an intake,
-- a sale, a receipt, a price change -- is on audit_log; a reservation was the one that was not,
-- and it is the one that can make a bucket read short with nothing on the ledger to explain it.
--
-- A DEDICATED TRIGGER, not the one the other tables share. That function predates these migrations
-- and its source is not in this repository, so attaching it to a new table would be guessing at a
-- contract nobody here can read. This writes the same five columns the shared one does, against a
-- table whose shape is known because 0043 created it.
--
-- record_id is the reservation's own key. It is short-lived by design -- a hold exists only until
-- the sale is confirmed or the carats go back -- so unlike an invoice the id will usually name a
-- row that is gone. That is the point: old_values on the DELETE says what was released, and the
-- INSERT before it says what was taken and by whom.
-- ---------------------------------------------------------------------------

create or replace function public.audit_stock_reservation()
returns trigger
language plpgsql
security definer
set search_path = public
as $$
begin
    insert into public.audit_log (table_name, record_id, action, changed_by, old_values, new_values)
    values (
        'stock_reservation',
        coalesce(new.reservation_id, old.reservation_id),
        tg_op,
        -- auth.uid() is null when the write came from a SECURITY DEFINER path with nobody signed
        -- in -- release_on_post firing inside post_invoice, a migration, the SQL editor. The page
        -- already renders that as "System" rather than as an unknown person.
        auth.uid(),
        case when tg_op in ('UPDATE', 'DELETE') then to_jsonb(old) end,
        case when tg_op in ('INSERT', 'UPDATE') then to_jsonb(new) end
    );

    -- AFTER trigger: the return value is discarded, but returning null from a row-level trigger
    -- is a habit worth not forming here.
    return coalesce(new, old);
end;
$$;

comment on function public.audit_stock_reservation() is
    'Writes reserve, correct and release to audit_log (0045). Carats held by an unconfirmed sales entry are the one movement of a figure on screen that the trail did not carry.';

drop trigger if exists trg_audit_stock_reservation on public.stock_reservation;

create trigger trg_audit_stock_reservation
    after insert or update or delete on public.stock_reservation
    for each row execute function public.audit_stock_reservation();

commit;


-- ###########################################################################
-- SECTION 11 of 17 : 0046_invoice_line_count.sql
-- ###########################################################################

begin;

-- ---------------------------------------------------------------------------
-- 0046 · How many lines an invoice has, on the invoice itself.
--
-- "I confirmed two rows and the Invoices page shows one" has now been reported
-- five times. The lines were never lost -- the payload sends every row, the
-- insert is one bulk call, and this view's own amount_total has summed BOTH of
-- them every time (1,000 + 2,000 reading 3.00 K on the list). What the page
-- could not do was SAY so. The count lived only in a detail drawer that opens
-- on selection and stands down entirely on a narrow window, so the list itself
-- showed an invoice of two exactly as it showed an invoice of one.
--
-- count(*) over sales_line, not over v_sales_line: the view adds three joins to
-- answer a question about rows on one table.
--
-- FREE. The `c` lateral below already computes lines_total for cost_coverage --
-- it has since 0019 -- and this only selects what was being thrown away. No new
-- scan, no new join, no index to add.
--
-- Everything else is the definition as it stood, byte for byte. create or
-- replace view cannot rename, reorder or retype a column, so the existing ones
-- have to survive untouched and the new one has to go LAST.
-- ---------------------------------------------------------------------------


CREATE OR REPLACE VIEW "public"."v_invoice" WITH ("security_invoker"='on') AS
 SELECT "i"."invoice_id",
    "i"."invoice_no",
    "i"."invoice_date",
    "i"."buyer_id",
    "b"."name" AS "buyer_name",
    "b"."credit_limit",
    "i"."broker_id",
    "br"."name" AS "broker_name",
    "i"."broker_pct",
    "i"."terms_days",
    "i"."doc_type",
    "i"."status",
    "i"."created_by",
    "p"."full_name" AS "salesperson",
    COALESCE("t"."amount_total", (0)::numeric) AS "amount_total",
    COALESCE("t"."carats_sold", (0)::numeric) AS "carats_sold",
    COALESCE("r"."received", (0)::numeric) AS "received",
        CASE
            WHEN (("i"."status")::"text" = 'CANCELLED'::"text") THEN (0)::numeric
            ELSE "round"((COALESCE("t"."amount_total", (0)::numeric) - COALESCE("r"."received", (0)::numeric)), 2)
        END AS "outstanding",
        CASE
            WHEN (COALESCE("t"."carats_sold", (0)::numeric) > (0)::numeric) THEN (COALESCE("t"."amount_total", (0)::numeric) / "t"."carats_sold")
            ELSE (0)::numeric
        END AS "blended_rate",
    "round"(((COALESCE("t"."amount_pre_broker", (0)::numeric) * "i"."broker_pct") / (100)::numeric), 2) AS "broker_payable",
    ("i"."invoice_date" + "i"."terms_days") AS "due_date",
    ((CURRENT_DATE > ("i"."invoice_date" + "i"."terms_days")) AND ((COALESCE("t"."amount_total", (0)::numeric) - COALESCE("r"."received", (0)::numeric)) > 0.01) AND (("i"."status")::"text" = 'POSTED'::"text")) AS "is_overdue",
    GREATEST(0, (CURRENT_DATE - ("i"."invoice_date" + "i"."terms_days"))) AS "days_overdue",
    "i"."created_at",
    "i"."updated_at",
        CASE
            WHEN (("c"."lines_total" > 0) AND ("c"."lines_costed" = "c"."lines_total")) THEN "round"("c"."cost_total", 2)
            ELSE NULL::numeric
        END AS "cost_total",
        CASE
            WHEN (("i"."status")::"text" = 'CANCELLED'::"text") THEN NULL::numeric
            WHEN (("c"."lines_total" > 0) AND ("c"."lines_costed" = "c"."lines_total")) THEN "round"((COALESCE("t"."amount_total", (0)::numeric) - "c"."cost_total"), 2)
            ELSE NULL::numeric
        END AS "margin",
        CASE
            WHEN ("c"."lines_total" > 0) THEN "round"((("c"."lines_costed")::numeric / ("c"."lines_total")::numeric), 4)
            ELSE (0)::numeric
        END AS "cost_coverage",
    COALESCE("c"."lines_total", (0)::bigint) AS "line_count"
   FROM (((((("public"."sales_invoice" "i"
     JOIN "public"."buyer" "b" ON (("b"."buyer_id" = "i"."buyer_id")))
     LEFT JOIN "public"."broker" "br" ON (("br"."broker_id" = "i"."broker_id")))
     LEFT JOIN "public"."profiles" "p" ON (("p"."id" = "i"."created_by")))
     LEFT JOIN LATERAL ( SELECT "sum"("vl"."amount") AS "amount_total",
            "sum"("vl"."amount_pre_broker") AS "amount_pre_broker",
            "sum"("vl"."selection_ct") AS "carats_sold"
           FROM "public"."v_sales_line" "vl"
          WHERE ("vl"."invoice_id" = "i"."invoice_id")) "t" ON (true))
     LEFT JOIN LATERAL ( SELECT "sum"("rc"."amount") AS "received"
           FROM "public"."receipt" "rc"
          WHERE ("rc"."invoice_id" = "i"."invoice_id")) "r" ON (true))
     LEFT JOIN LATERAL ( SELECT "count"(*) AS "lines_total",
            "count"("sl"."cost_per_ct") AS "lines_costed",
            "sum"(("sl"."cost_per_ct" * "sl"."gross_weight_ct")) AS "cost_total"
           FROM "public"."sales_line" "sl"
          WHERE ("sl"."invoice_id" = "i"."invoice_id")) "c" ON (true));

commit;


-- ###########################################################################
-- SECTION 12 of 17 : 0047_import_stock_allows_negative.sql
-- ###########################################################################

-- ---------------------------------------------------------------------------
-- 0047  A replacing stock import may leave a bucket below zero.
--
-- 0038 refused an import whose projected position went negative, on the reasoning that a sheet
-- disagreeing with the ledger is a question for whoever holds the paper. The desk has decided
-- otherwise: negative stock is allowed, and an import must not be blocked by it.
--
-- SUPERSEDES 0038 RATHER THAN EDITING IT, for the reason 0036 gives about 0035: a migration that
-- has run is a record of what happened. Editing it would leave the file disagreeing with every
-- database that already has it, and the next person to read the folder could not tell which
-- version their project holds.
--
-- THE ONLY CHANGE IS THE REFUSAL. This function is 0038's, copied line for line, with the
-- negative-stock block removed and the two variables that block alone used (v_short, v_count)
-- taken out with it. Everything else is untouched and deliberately so:
--
--   * the empty-array refusal, which is what a silently failed parser returns
--   * the source check, and delete_imported_stock as the way to clear an import on purpose
--   * the parcels-and-movements pair written from one CTE, so a movement without its intake is
--     not expressible
--   * the replace path: same deletes, same batch, same return shape
--
-- assert_stock is NOT touched. Posting still obeys negative_stock_policy() as it always has; this
-- changes what an IMPORT refuses, and nothing about what a sale refuses.
-- ---------------------------------------------------------------------------

begin;

create or replace function public.import_stock(
    p_as_at   date,
    p_rows    jsonb,
    p_replace boolean default true,
    p_batch   uuid    default null,
    p_source  text    default 'excel'
)
returns jsonb
language plpgsql
security definer
set search_path = public
as $$
declare
    v_old_ids bigint[];
    v_deleted integer := 0;
    v_written integer := 0;
    v_batch   uuid    := coalesce(p_batch, gen_random_uuid());
begin
    if p_rows is null or jsonb_typeof(p_rows) <> 'array' then
        raise exception 'import_stock expects an array of rows';
    end if;

    -- Refuse to replace a real dataset with nothing. An empty array is what a
    -- parser returns when it silently failed, and "delete everything" is too
    -- destructive to be reachable by accident. Clearing the import on purpose
    -- is what delete_imported_stock() is for.
    if jsonb_array_length(p_rows) = 0 then
        raise exception 'import_stock was given no rows; use delete_imported_stock() to clear the import';
    end if;

    if p_source is not null and p_source not in ('excel', 'pdf') then
        raise exception 'import_stock does not know the source %', p_source;
    end if;

    if p_replace then
        -- The parcels behind the movements, captured before the movements go.
        select coalesce(array_agg(distinct ref_id), '{}')
          into v_old_ids
          from public.stock_movement
         where ref_type = 'stock_import'
           and ref_id is not null;

        delete from public.stock_movement where ref_type = 'stock_import';
        get diagnostics v_deleted = row_count;

        if array_length(v_old_ids, 1) is not null then
            delete from public.rough_intake where intake_id = any(v_old_ids);
        end if;
    end if;

    -- Parcels and their movements in one statement each, both reading the same
    -- CTE, so a movement without its intake is not expressible here.
    with parcel as (
        insert into public.rough_intake
            (intake_date, grade_id, size_id, weight_ct, price_per_ct, created_by,
             import_batch, import_source)
        select p_as_at,
               (r->>'grade_id')::bigint,
               (r->>'size_id')::bigint,
               (r->>'weight_ct')::numeric,
               (r->>'price_per_ct')::numeric,
               auth.uid(),
               v_batch,
               p_source
          from jsonb_array_elements(p_rows) as r
        returning intake_id, grade_id, size_id, weight_ct, price_per_ct
    )
    insert into public.stock_movement
        (movement_date, grade_id, size_id, movement_type, weight_ct,
         price_per_ct, ref_type, ref_id, created_by)
    select p_as_at, p.grade_id, p.size_id, 'INTAKE', p.weight_ct,
           p.price_per_ct, 'stock_import', p.intake_id, auth.uid()
      from parcel p;

    get diagnostics v_written = row_count;

    return jsonb_build_object('ok', true, 'deleted', v_deleted,
                              'written', v_written, 'batch', v_batch);
end;
$$;

revoke all on function public.import_stock(date, jsonb, boolean, uuid, text) from public;
grant execute on function public.import_stock(date, jsonb, boolean, uuid, text) to authenticated;

commit;


-- ###########################################################################
-- SECTION 13 of 17 : 0048_imported_sales_after_count_deduct_stock.sql
-- ###########################################################################

-- ---------------------------------------------------------------------------
-- 0048. An imported sale made AFTER the stock count takes its carats out.
--
-- THE RULE, in one line: a sale dated on or before the count is already in it;
-- a sale dated after it is not.
--
-- The stock importer lands a COUNTED POSITION, not an opening balance. When the
-- shelf was counted on 1 September, every parcel sold before that date was
-- already gone from it -- which is why replace_imported_sales has never written
-- a stock movement, and why it must not start writing one for those sales.
-- Doing so would take the same carats out twice.
--
-- What was missing is the other half. A sheet of sales that runs PAST the count
-- date carries invoices whose carats are still on the shelf as far as the
-- ledger knows, and nothing ever took them off. The position read high by
-- exactly those lines, silently, with no row to point at.
--
-- WHAT THIS CHANGES, precisely:
--
--   imported sale dated <= the count   nothing, exactly as before
--   imported sale dated >  the count   SALE + REJECTION movements, as a posted
--                                      sale writes them
--   no stock count in the database     nothing, exactly as before
--
-- NOTHING IS APPLIED TO DATA ALREADY IMPORTED. This writes movements during an
-- import and at no other time, so an existing position does not move when the
-- migration is applied. The next import of the same sheet is what brings it
-- into line -- and that import first removes the movements the previous one
-- wrote, so importing twice deducts once.
--
-- Tagged ref_type = 'sales_line' deliberately, so the two functions that
-- already understand that tag keep working untouched:
--
--   cancel_invoice        reverses SALE/REJECTION rows by this tag, so
--                         cancelling an imported invoice returns its carats
--   edit_posted_invoice   adds a signed delta for a MIG- invoice rather than
--                         rewriting its movements, so old + delta is still right
-- ---------------------------------------------------------------------------

begin;

create or replace function public.replace_imported_sales(p_payload jsonb)
returns jsonb
language plpgsql
security definer
set search_path = public
as $$
declare
    v_old_ids     bigint[];
    v_deleted     integer := 0;
    v_invoices    integer := 0;
    v_lines       integer := 0;
    v_receipts    integer := 0;
    v_currency    bigint;
    v_counted     date;          -- 0048. the day the stock was last counted
    v_stock_lines integer := 0;  -- 0048. lines whose carats this import took out
begin
    if p_payload is null or jsonb_typeof(p_payload->'invoices') <> 'array' then
        raise exception 'replace_imported_sales expects {"invoices": [...]}';
    end if;

    if jsonb_array_length(p_payload->'invoices') = 0 then
        raise exception 'replace_imported_sales was given no invoices';
    end if;

    v_currency := (p_payload->>'currency_id')::bigint;
    if v_currency is null then
        raise exception 'replace_imported_sales needs a currency_id';
    end if;

    -- Only ever the previous import. A live invoice carries INV-yyyy-nnnnn and
    -- is not matched by this; 08 §4 is why migrated numbers are prefixed at all.
    select coalesce(array_agg(invoice_id), '{}')
      into v_old_ids
      from public.sales_invoice
     where invoice_no like 'MIG-%';

    if array_length(v_old_ids, 1) is not null then
        -- 0042. An imported invoice can now carry an import_edit stock adjustment, from having
        -- been corrected here. Restoring the sheet's original lines while leaving that adjustment
        -- applied would hold stock at a correction whose invoice is being replaced. Cleared first,
        -- inside the same transaction, so the sheet and the stock cannot disagree.
        perform public.clear_import_edits(v_old_ids);

        -- 0048. And the SALE/REJECTION movements this importer wrote for invoices dated after the
        -- stock count. BEFORE the lines go: they are found through sales_line.line_id, and once the
        -- lines are deleted there is nothing left to find them by -- they would sit in the ledger
        -- deducting carats for an invoice that no longer exists.
        delete from public.stock_movement
         where ref_type = 'sales_line'
           and ref_id in (select line_id from public.sales_line
                           where invoice_id = any(v_old_ids));

        delete from public.receipt     where invoice_id = any(v_old_ids);
        delete from public.sales_line  where invoice_id = any(v_old_ids);
        delete from public.sales_invoice where invoice_id = any(v_old_ids);
        get diagnostics v_deleted = row_count;
    end if;

    -- Invoices first, keeping invoice_no as the handle to hang lines off: the
    -- ids are assigned by the sequence and the payload cannot know them.
    with incoming as (
        select inv from jsonb_array_elements(p_payload->'invoices') as inv
    ),
    written as (
        insert into public.sales_invoice
            (invoice_no, invoice_date, buyer_id, broker_id, broker_pct,
             terms_days, doc_type, currency_id, status,
             created_by, updated_by)
        select inv->>'invoice_no',
               (inv->>'invoice_date')::date,
               (inv->>'buyer_id')::bigint,
               nullif(inv->>'broker_id', '')::bigint,
               coalesce((inv->>'broker_pct')::numeric, 0),
               coalesce((inv->>'terms_days')::integer, 0),
               coalesce(inv->>'doc_type', 'BILL'),
               v_currency,
               'POSTED',
               auth.uid(),
               auth.uid()
          from incoming
        returning invoice_id, invoice_no
    )
    select count(*) into v_invoices from written;

    -- Lines, matched back by invoice_no.
    with incoming as (
        select inv->>'invoice_no' as no,
               jsonb_array_elements(coalesce(inv->'lines', '[]'::jsonb)) as ln
          from jsonb_array_elements(p_payload->'invoices') as inv
    )
    insert into public.sales_line
        (invoice_id, grade_id, size_id, gross_weight_ct, selection_ct,
         price_per_ct, ex_rate, less1_pct, less2_pct, remark)
    select i.invoice_id,
           (c.ln->>'grade_id')::bigint,
           (c.ln->>'size_id')::bigint,
           (c.ln->>'gross_weight_ct')::numeric,
           (c.ln->>'selection_ct')::numeric,
           (c.ln->>'price_per_ct')::numeric,
           coalesce((c.ln->>'ex_rate')::numeric, 1),
           coalesce((c.ln->>'less1_pct')::numeric, 0),
           coalesce((c.ln->>'less2_pct')::numeric, 0),
           nullif(c.ln->>'remark', '')
      from incoming c
      join public.sales_invoice i on i.invoice_no = c.no;

    get diagnostics v_lines = row_count;

    -- One receipt per invoice that carried money, exactly as the workbook's
    -- single overwritten "Rec. Amt" cell states it (DQ-11: there is no payment
    -- history to migrate, only a running total). Dated the invoice date, since
    -- the sheet records no payment date -- docs/08 §5 says declare it, not bury
    -- it. Method 'IMPORTED', unchanged from the client-side importer.
    --
    -- `received` arrives ALREADY CAPPED at the invoice total. The cap stays on
    -- the client because it needs the line amounts CALC-1 produces, and those
    -- are the calculation engine's to compute, not this function's.
    with incoming as (
        select inv->>'invoice_no' as no,
               (inv->>'received')::numeric as received,
               (inv->>'invoice_date')::date as on_date
          from jsonb_array_elements(p_payload->'invoices') as inv
    )
    insert into public.receipt (invoice_id, receipt_date, amount, method, created_by)
    select i.invoice_id, c.on_date, c.received, 'IMPORTED', auth.uid()
      from incoming c
      join public.sales_invoice i on i.invoice_no = c.no
     where c.received is not null and c.received > 0;

    get diagnostics v_receipts = row_count;

    -- 0048 - sales made AFTER the count come out of stock.
    --
    -- An imported sale dated on or before the stock count is already accounted for: the count was
    -- taken with those carats gone, so deducting again would take the same parcel out twice. A sale
    -- dated AFTER it is not in the count, and until now nothing took it out at all -- the position
    -- read high by exactly those carats.
    select max(movement_date) into v_counted
      from public.stock_movement
     where ref_type = 'stock_import';

    -- NO COUNT, NO DEDUCTION. A database that has never imported a stock sheet has no date to
    -- compare against, and guessing one either way would be inventing a position. Behaves exactly
    -- as it did before 0048.
    if v_counted is not null then
        -- SALE and REJECTION, the same pair post_invoice writes, tagged 'sales_line' for the same
        -- reason: cancel_invoice reverses movements by that tag, so a cancelled import gives its
        -- carats back without a line of new code. edit_posted_invoice is unaffected -- its MIG-
        -- branch adds a signed delta rather than rewriting, so old + delta is still the new figure.
        --
        -- Dated the INVOICE date, not today, so the Stock page counts them where they belong.
        insert into public.stock_movement
            (movement_date, grade_id, size_id, movement_type, weight_ct,
             price_per_ct, ref_type, ref_id, created_by)
        select i.invoice_date, l.grade_id, l.size_id, 'SALE', l.selection_ct,
               l.price_per_ct, 'sales_line', l.line_id, auth.uid()
          from public.sales_line l
          join public.sales_invoice i on i.invoice_id = l.invoice_id
         where i.invoice_no like 'MIG-%'
           and i.invoice_date > v_counted
           and l.selection_ct > 0;

        get diagnostics v_stock_lines = row_count;

        insert into public.stock_movement
            (movement_date, grade_id, size_id, movement_type, weight_ct,
             price_per_ct, ref_type, ref_id, created_by)
        select i.invoice_date, l.grade_id, l.size_id, 'REJECTION', l.rejection_ct,
               l.price_per_ct, 'sales_line', l.line_id, auth.uid()
          from public.sales_line l
          join public.sales_invoice i on i.invoice_id = l.invoice_id
         where i.invoice_no like 'MIG-%'
           and i.invoice_date > v_counted
           and l.rejection_ct > 0;
    end if;

    return jsonb_build_object('ok', true, 'deleted', v_deleted,
                              'invoices', v_invoices, 'lines', v_lines,
                              'receipts', v_receipts,
                              'counted_as_at', v_counted,
                              'stock_lines', v_stock_lines);
end;
$$;

comment on function public.replace_imported_sales is
    '0048. Replaces the imported MIG- sales history in one transaction, and takes stock out for the imported sales dated AFTER the newest stock_import movement - those are the ones the counted position does not already account for. Sales on or before the count write no movement, because the count was taken with those carats already gone. A database with no stock import writes none at all. A re-import removes the movements the previous import wrote before writing its own.';

commit;


-- ###########################################################################
-- SECTION 14 of 17 : 0049_imported_sales_always_deduct_stock.sql
-- ###########################################################################

-- ---------------------------------------------------------------------------
-- 0049. EVERY imported sale takes its carats out of stock.
--
-- SUPERSEDES 0048 RATHER THAN EDITING IT, the same way 0047 supersedes 0038: an
-- applied migration is a record of what a database was told, and rewriting one
-- in place makes two databases with identical migration lists behave
-- differently. 0048 stays on disk and stays applied; this replaces the function
-- it installed.
--
-- WHAT 0048 DID, and why it is going:
--
--   sale dated <= the last stock count   no movement
--   sale dated >  the last stock count   SALE + REJECTION
--
-- That read the stock sheet as a COUNT taken after those sales, so their carats
-- were already off the shelf and deducting again would remove them twice. The
-- desk has decided otherwise: an imported sale is a sale, and it moves stock
-- exactly as one typed into the app does. The date is no longer consulted.
--
-- WHAT THIS DOES:
--
--   every imported MIG- line   SALE for what was sold, REJECTION for what was
--                              rejected -- the same pair post_invoice writes
--                              for a live sale
--
-- THE CONSEQUENCE, stated once and plainly. If the stock sheet is a count taken
-- AFTER the sales imported alongside it, this takes the same carats out a second
-- time and the position falls below what is physically in the drawer. Negative
-- stock is allowed (0047), so nothing refuses and nothing warns. If the sheet is
-- an OPENING balance, this is right and 0048 was the bug. Which of the two it is
-- is the office's call, not this function's.
--
-- NO DOUBLE DEDUCTION ON RE-IMPORT. The function removes the movements the
-- previous import wrote before writing its own, so importing the same sheet ten
-- times deducts once. These rows are written here and nowhere else, which is
-- what makes that guard sufficient.
--
-- NOTHING IS APPLIED TO DATA ALREADY IMPORTED. Movements are written during an
-- import and at no other time, so an existing position does not move when this
-- migration is applied. The next import is what brings it into line.
-- ---------------------------------------------------------------------------

begin;

create or replace function public.replace_imported_sales(p_payload jsonb)
returns jsonb
language plpgsql
security definer
set search_path = public
as $$
declare
    v_old_ids     bigint[];
    v_deleted     integer := 0;
    v_invoices    integer := 0;
    v_lines       integer := 0;
    v_receipts    integer := 0;
    v_currency    bigint;
    v_stock_lines integer := 0;  -- 0049. lines whose carats this import took out
begin
    if p_payload is null or jsonb_typeof(p_payload->'invoices') <> 'array' then
        raise exception 'replace_imported_sales expects {"invoices": [...]}';
    end if;

    if jsonb_array_length(p_payload->'invoices') = 0 then
        raise exception 'replace_imported_sales was given no invoices';
    end if;

    v_currency := (p_payload->>'currency_id')::bigint;
    if v_currency is null then
        raise exception 'replace_imported_sales needs a currency_id';
    end if;

    -- Only ever the previous import. A live invoice carries INV-yyyy-nnnnn and
    -- is not matched by this; 08 §4 is why migrated numbers are prefixed at all.
    select coalesce(array_agg(invoice_id), '{}')
      into v_old_ids
      from public.sales_invoice
     where invoice_no like 'MIG-%';

    if array_length(v_old_ids, 1) is not null then
        -- 0042. An imported invoice can now carry an import_edit stock adjustment, from having
        -- been corrected here. Restoring the sheet's original lines while leaving that adjustment
        -- applied would hold stock at a correction whose invoice is being replaced. Cleared first,
        -- inside the same transaction, so the sheet and the stock cannot disagree.
        perform public.clear_import_edits(v_old_ids);

        -- 0049. And the SALE/REJECTION movements the previous import wrote. This is the whole of
        -- what stops a re-import deducting twice. BEFORE the lines go: they are found through sales_line.line_id, and once the
        -- lines are deleted there is nothing left to find them by -- they would sit in the ledger
        -- deducting carats for an invoice that no longer exists.
        delete from public.stock_movement
         where ref_type = 'sales_line'
           and ref_id in (select line_id from public.sales_line
                           where invoice_id = any(v_old_ids));

        delete from public.receipt     where invoice_id = any(v_old_ids);
        delete from public.sales_line  where invoice_id = any(v_old_ids);
        delete from public.sales_invoice where invoice_id = any(v_old_ids);
        get diagnostics v_deleted = row_count;
    end if;

    -- Invoices first, keeping invoice_no as the handle to hang lines off: the
    -- ids are assigned by the sequence and the payload cannot know them.
    with incoming as (
        select inv from jsonb_array_elements(p_payload->'invoices') as inv
    ),
    written as (
        insert into public.sales_invoice
            (invoice_no, invoice_date, buyer_id, broker_id, broker_pct,
             terms_days, doc_type, currency_id, status,
             created_by, updated_by)
        select inv->>'invoice_no',
               (inv->>'invoice_date')::date,
               (inv->>'buyer_id')::bigint,
               nullif(inv->>'broker_id', '')::bigint,
               coalesce((inv->>'broker_pct')::numeric, 0),
               coalesce((inv->>'terms_days')::integer, 0),
               coalesce(inv->>'doc_type', 'BILL'),
               v_currency,
               'POSTED',
               auth.uid(),
               auth.uid()
          from incoming
        returning invoice_id, invoice_no
    )
    select count(*) into v_invoices from written;

    -- Lines, matched back by invoice_no.
    with incoming as (
        select inv->>'invoice_no' as no,
               jsonb_array_elements(coalesce(inv->'lines', '[]'::jsonb)) as ln
          from jsonb_array_elements(p_payload->'invoices') as inv
    )
    insert into public.sales_line
        (invoice_id, grade_id, size_id, gross_weight_ct, selection_ct,
         price_per_ct, ex_rate, less1_pct, less2_pct, remark)
    select i.invoice_id,
           (c.ln->>'grade_id')::bigint,
           (c.ln->>'size_id')::bigint,
           (c.ln->>'gross_weight_ct')::numeric,
           (c.ln->>'selection_ct')::numeric,
           (c.ln->>'price_per_ct')::numeric,
           coalesce((c.ln->>'ex_rate')::numeric, 1),
           coalesce((c.ln->>'less1_pct')::numeric, 0),
           coalesce((c.ln->>'less2_pct')::numeric, 0),
           nullif(c.ln->>'remark', '')
      from incoming c
      join public.sales_invoice i on i.invoice_no = c.no;

    get diagnostics v_lines = row_count;

    -- One receipt per invoice that carried money, exactly as the workbook's
    -- single overwritten "Rec. Amt" cell states it (DQ-11: there is no payment
    -- history to migrate, only a running total). Dated the invoice date, since
    -- the sheet records no payment date -- docs/08 §5 says declare it, not bury
    -- it. Method 'IMPORTED', unchanged from the client-side importer.
    --
    -- `received` arrives ALREADY CAPPED at the invoice total. The cap stays on
    -- the client because it needs the line amounts CALC-1 produces, and those
    -- are the calculation engine's to compute, not this function's.
    with incoming as (
        select inv->>'invoice_no' as no,
               (inv->>'received')::numeric as received,
               (inv->>'invoice_date')::date as on_date
          from jsonb_array_elements(p_payload->'invoices') as inv
    )
    insert into public.receipt (invoice_id, receipt_date, amount, method, created_by)
    select i.invoice_id, c.on_date, c.received, 'IMPORTED', auth.uid()
      from incoming c
      join public.sales_invoice i on i.invoice_no = c.no
     where c.received is not null and c.received > 0;

    get diagnostics v_receipts = row_count;

    -- 0049 - EVERY imported sale comes out of stock.
    --
    -- 0048 deducted only the sales dated after the last stock count, reading the sheet as a COUNT
    -- taken with the earlier ones already gone. The desk has decided otherwise: an imported sale is
    -- a sale, and it moves stock exactly as one typed into the app does. The date is not consulted.
    --
    -- WORTH KNOWING, because it is why 0048 read the other way: if the stock sheet IS a count taken
    -- after these sales, this takes the same carats out a second time and the position falls below
    -- what is in the drawer. Negative stock is allowed (0047), so nothing refuses and nothing warns.
    --
    -- SALE and REJECTION, the same pair post_invoice writes, tagged 'sales_line' for the same
    -- reason: cancel_invoice reverses movements by that tag, so a cancelled import gives its carats
    -- back without a line of new code. edit_posted_invoice is unaffected -- its MIG- branch adds a
    -- signed delta rather than rewriting, so old + delta is still the new figure.
    --
    -- Dated the INVOICE date, not today, so the Stock page counts them where they belong in time.
    insert into public.stock_movement
        (movement_date, grade_id, size_id, movement_type, weight_ct,
         price_per_ct, ref_type, ref_id, created_by)
    select i.invoice_date, l.grade_id, l.size_id, 'SALE', l.selection_ct,
           l.price_per_ct, 'sales_line', l.line_id, auth.uid()
      from public.sales_line l
      join public.sales_invoice i on i.invoice_id = l.invoice_id
     where i.invoice_no like 'MIG-%'
       and l.selection_ct > 0;

    get diagnostics v_stock_lines = row_count;

    insert into public.stock_movement
        (movement_date, grade_id, size_id, movement_type, weight_ct,
         price_per_ct, ref_type, ref_id, created_by)
    select i.invoice_date, l.grade_id, l.size_id, 'REJECTION', l.rejection_ct,
           l.price_per_ct, 'sales_line', l.line_id, auth.uid()
      from public.sales_line l
      join public.sales_invoice i on i.invoice_id = l.invoice_id
     where i.invoice_no like 'MIG-%'
       and l.rejection_ct > 0;

    return jsonb_build_object('ok', true, 'deleted', v_deleted,
                              'invoices', v_invoices, 'lines', v_lines,
                              'receipts', v_receipts,
                              'stock_lines', v_stock_lines);
end;
$$;

comment on function public.replace_imported_sales is
    '0049. Replaces the imported MIG- sales history in one transaction and takes stock out for EVERY imported line - SALE for what was sold, REJECTION for what was rejected - exactly as a sale entered in the app does. Supersedes 0048, which deducted only the sales dated after the last stock count. A re-import removes the movements the previous import wrote before writing its own, so importing the same sheet twice deducts once.';

commit;


-- ###########################################################################
-- SECTION 15 of 17 : 0050_import_reports_carats_deducted.sql
-- ###########################################################################

-- ---------------------------------------------------------------------------
-- 0050. The import says how many CARATS it took out, not just how many lines.
--
-- SUPERSEDES 0049 RATHER THAN EDITING IT, as 0049 supersedes 0048: an applied
-- migration is a record of what a database was told. 0049 stays on disk and
-- stays applied; this replaces the function it installed.
--
-- NOTHING ABOUT THE DEDUCTION CHANGES. The same lines write the same SALE and
-- REJECTION movements for the same weights on the same dates. This adds one
-- number to what the function REPORTS, so the completion dialog can say
-- "134,416.9100 ct" beside "1,446 line(s)" instead of leaving the weight to be
-- looked up in SQL afterwards.
--
-- WHY IT IS SUMMED OFF THE MOVEMENTS, not off the lines. The movements are what
-- actually left stock. A line that sold nothing writes no SALE row, so it
-- contributes only whatever its REJECTION took -- which is the honest figure for
-- "carats deducted". Summing the lines instead would report weight that no
-- movement ever moved.
--
-- So a sheet of 1,447 lines where one sold nothing reports 1,446 lines and the
-- weight of every movement written, the zero-sold line included at its rejection
-- and excluded when it rejected nothing either.
-- ---------------------------------------------------------------------------

begin;

create or replace function public.replace_imported_sales(p_payload jsonb)
returns jsonb
language plpgsql
security definer
set search_path = public
as $$
declare
    v_old_ids     bigint[];
    v_deleted     integer := 0;
    v_invoices    integer := 0;
    v_lines       integer := 0;
    v_receipts    integer := 0;
    v_currency    bigint;
    v_stock_lines integer := 0;  -- 0049. lines whose carats this import took out
    v_stock_ct    numeric := 0;  -- 0050. and how many carats that came to
begin
    if p_payload is null or jsonb_typeof(p_payload->'invoices') <> 'array' then
        raise exception 'replace_imported_sales expects {"invoices": [...]}';
    end if;

    if jsonb_array_length(p_payload->'invoices') = 0 then
        raise exception 'replace_imported_sales was given no invoices';
    end if;

    v_currency := (p_payload->>'currency_id')::bigint;
    if v_currency is null then
        raise exception 'replace_imported_sales needs a currency_id';
    end if;

    -- Only ever the previous import. A live invoice carries INV-yyyy-nnnnn and
    -- is not matched by this; 08 §4 is why migrated numbers are prefixed at all.
    select coalesce(array_agg(invoice_id), '{}')
      into v_old_ids
      from public.sales_invoice
     where invoice_no like 'MIG-%';

    if array_length(v_old_ids, 1) is not null then
        -- 0042. An imported invoice can now carry an import_edit stock adjustment, from having
        -- been corrected here. Restoring the sheet's original lines while leaving that adjustment
        -- applied would hold stock at a correction whose invoice is being replaced. Cleared first,
        -- inside the same transaction, so the sheet and the stock cannot disagree.
        perform public.clear_import_edits(v_old_ids);

        -- 0049. And the SALE/REJECTION movements the previous import wrote. This is the whole of
        -- what stops a re-import deducting twice. BEFORE the lines go: they are found through sales_line.line_id, and once the
        -- lines are deleted there is nothing left to find them by -- they would sit in the ledger
        -- deducting carats for an invoice that no longer exists.
        delete from public.stock_movement
         where ref_type = 'sales_line'
           and ref_id in (select line_id from public.sales_line
                           where invoice_id = any(v_old_ids));

        delete from public.receipt     where invoice_id = any(v_old_ids);
        delete from public.sales_line  where invoice_id = any(v_old_ids);
        delete from public.sales_invoice where invoice_id = any(v_old_ids);
        get diagnostics v_deleted = row_count;
    end if;

    -- Invoices first, keeping invoice_no as the handle to hang lines off: the
    -- ids are assigned by the sequence and the payload cannot know them.
    with incoming as (
        select inv from jsonb_array_elements(p_payload->'invoices') as inv
    ),
    written as (
        insert into public.sales_invoice
            (invoice_no, invoice_date, buyer_id, broker_id, broker_pct,
             terms_days, doc_type, currency_id, status,
             created_by, updated_by)
        select inv->>'invoice_no',
               (inv->>'invoice_date')::date,
               (inv->>'buyer_id')::bigint,
               nullif(inv->>'broker_id', '')::bigint,
               coalesce((inv->>'broker_pct')::numeric, 0),
               coalesce((inv->>'terms_days')::integer, 0),
               coalesce(inv->>'doc_type', 'BILL'),
               v_currency,
               'POSTED',
               auth.uid(),
               auth.uid()
          from incoming
        returning invoice_id, invoice_no
    )
    select count(*) into v_invoices from written;

    -- Lines, matched back by invoice_no.
    with incoming as (
        select inv->>'invoice_no' as no,
               jsonb_array_elements(coalesce(inv->'lines', '[]'::jsonb)) as ln
          from jsonb_array_elements(p_payload->'invoices') as inv
    )
    insert into public.sales_line
        (invoice_id, grade_id, size_id, gross_weight_ct, selection_ct,
         price_per_ct, ex_rate, less1_pct, less2_pct, remark)
    select i.invoice_id,
           (c.ln->>'grade_id')::bigint,
           (c.ln->>'size_id')::bigint,
           (c.ln->>'gross_weight_ct')::numeric,
           (c.ln->>'selection_ct')::numeric,
           (c.ln->>'price_per_ct')::numeric,
           coalesce((c.ln->>'ex_rate')::numeric, 1),
           coalesce((c.ln->>'less1_pct')::numeric, 0),
           coalesce((c.ln->>'less2_pct')::numeric, 0),
           nullif(c.ln->>'remark', '')
      from incoming c
      join public.sales_invoice i on i.invoice_no = c.no;

    get diagnostics v_lines = row_count;

    -- One receipt per invoice that carried money, exactly as the workbook's
    -- single overwritten "Rec. Amt" cell states it (DQ-11: there is no payment
    -- history to migrate, only a running total). Dated the invoice date, since
    -- the sheet records no payment date -- docs/08 §5 says declare it, not bury
    -- it. Method 'IMPORTED', unchanged from the client-side importer.
    --
    -- `received` arrives ALREADY CAPPED at the invoice total. The cap stays on
    -- the client because it needs the line amounts CALC-1 produces, and those
    -- are the calculation engine's to compute, not this function's.
    with incoming as (
        select inv->>'invoice_no' as no,
               (inv->>'received')::numeric as received,
               (inv->>'invoice_date')::date as on_date
          from jsonb_array_elements(p_payload->'invoices') as inv
    )
    insert into public.receipt (invoice_id, receipt_date, amount, method, created_by)
    select i.invoice_id, c.on_date, c.received, 'IMPORTED', auth.uid()
      from incoming c
      join public.sales_invoice i on i.invoice_no = c.no
     where c.received is not null and c.received > 0;

    get diagnostics v_receipts = row_count;

    -- 0049 - EVERY imported sale comes out of stock.
    --
    -- 0048 deducted only the sales dated after the last stock count, reading the sheet as a COUNT
    -- taken with the earlier ones already gone. The desk has decided otherwise: an imported sale is
    -- a sale, and it moves stock exactly as one typed into the app does. The date is not consulted.
    --
    -- WORTH KNOWING, because it is why 0048 read the other way: if the stock sheet IS a count taken
    -- after these sales, this takes the same carats out a second time and the position falls below
    -- what is in the drawer. Negative stock is allowed (0047), so nothing refuses and nothing warns.
    --
    -- SALE and REJECTION, the same pair post_invoice writes, tagged 'sales_line' for the same
    -- reason: cancel_invoice reverses movements by that tag, so a cancelled import gives its carats
    -- back without a line of new code. edit_posted_invoice is unaffected -- its MIG- branch adds a
    -- signed delta rather than rewriting, so old + delta is still the new figure.
    --
    -- Dated the INVOICE date, not today, so the Stock page counts them where they belong in time.
    insert into public.stock_movement
        (movement_date, grade_id, size_id, movement_type, weight_ct,
         price_per_ct, ref_type, ref_id, created_by)
    select i.invoice_date, l.grade_id, l.size_id, 'SALE', l.selection_ct,
           l.price_per_ct, 'sales_line', l.line_id, auth.uid()
      from public.sales_line l
      join public.sales_invoice i on i.invoice_id = l.invoice_id
     where i.invoice_no like 'MIG-%'
       and l.selection_ct > 0;

    get diagnostics v_stock_lines = row_count;

    insert into public.stock_movement
        (movement_date, grade_id, size_id, movement_type, weight_ct,
         price_per_ct, ref_type, ref_id, created_by)
    select i.invoice_date, l.grade_id, l.size_id, 'REJECTION', l.rejection_ct,
           l.price_per_ct, 'sales_line', l.line_id, auth.uid()
      from public.sales_line l
      join public.sales_invoice i on i.invoice_id = l.invoice_id
     where i.invoice_no like 'MIG-%'
       and l.rejection_ct > 0;

    -- 0050 - and the WEIGHT those movements came to.
    --
    -- Summed off the movements themselves rather than off the lines, because the movements are
    -- the thing that actually left stock: a line that sold nothing wrote no SALE row and is
    -- counted here at whatever its REJECTION took, which is the honest figure. Reading the lines
    -- instead would report carats that no movement ever moved.
    --
    -- Safe to sum the whole MIG- set: the delete at the top of this function cleared the previous
    -- import's rows, so every one of these was written a few statements ago.
    select coalesce(sum(m.weight_ct), 0) into v_stock_ct
      from public.stock_movement m
      join public.sales_line l on l.line_id = m.ref_id and m.ref_type = 'sales_line'
      join public.sales_invoice i on i.invoice_id = l.invoice_id
     where i.invoice_no like 'MIG-%';

    return jsonb_build_object('ok', true, 'deleted', v_deleted,
                              'invoices', v_invoices, 'lines', v_lines,
                              'receipts', v_receipts,
                              'stock_lines', v_stock_lines,
                              'stock_ct', v_stock_ct);
end;
$$;

comment on function public.replace_imported_sales is
    '0050. Replaces the imported MIG- sales history in one transaction and takes stock out for every imported line - SALE for what was sold, REJECTION for what was rejected - exactly as a sale entered in the app does. Reports stock_lines and stock_ct, the number of lines and the weight the movements came to. Supersedes 0049, which reported the line count alone. A re-import removes the movements the previous import wrote before writing its own, so importing the same sheet twice deducts once.';

commit;


-- ###########################################################################
-- SECTION 16 of 17 : 0051_import_never_drives_stock_negative.sql
-- ###########################################################################

-- ---------------------------------------------------------------------------
-- 0051. The import takes what the shelf has and no more. It never goes negative.
--
-- SUPERSEDES 0050 RATHER THAN EDITING IT, as 0050 supersedes 0049 and 0049
-- supersedes 0048: an applied migration is a record of what a database was told.
-- 0050 stays on disk and stays applied; this replaces the function it installed.
--
-- WHAT CHANGES. 0049 deducted every imported line in full, and on a sheet whose
-- sales predate the stock count that drove the position far below zero -- the
-- desk saw -131,503 ct. The rule is now: an import may empty a bucket but may
-- not overdraw it. What the shelf could not cover is reported instead, as
-- short_ct and short_buckets, so the import dialog can warn about it.
--
-- WHAT DOES NOT CHANGE. Which lines write movements, their type, date, price and
-- tag; the delete-then-rewrite that stops a re-import deducting twice; every
-- number 0050 already reported. Only the WEIGHT on a capped line differs, and
-- only where there were not carats enough to take.
-- ---------------------------------------------------------------------------

begin;

create or replace function public.replace_imported_sales(p_payload jsonb)
returns jsonb
language plpgsql
security definer
set search_path = public
as $$
declare
    v_old_ids     bigint[];
    v_deleted     integer := 0;
    v_invoices    integer := 0;
    v_lines       integer := 0;
    v_receipts    integer := 0;
    v_currency    bigint;
    v_stock_lines integer := 0;  -- 0049. lines whose carats this import took out
    v_stock_ct    numeric := 0;  -- 0050. and how many carats that came to
    v_short_ct    numeric := 0;  -- 0051. carats the sheet wanted and the shelf did not have
    v_short_bkts  integer := 0;  -- 0051. how many grade x size buckets ran out
begin
    if p_payload is null or jsonb_typeof(p_payload->'invoices') <> 'array' then
        raise exception 'replace_imported_sales expects {"invoices": [...]}';
    end if;

    if jsonb_array_length(p_payload->'invoices') = 0 then
        raise exception 'replace_imported_sales was given no invoices';
    end if;

    v_currency := (p_payload->>'currency_id')::bigint;
    if v_currency is null then
        raise exception 'replace_imported_sales needs a currency_id';
    end if;

    -- Only ever the previous import. A live invoice carries INV-yyyy-nnnnn and
    -- is not matched by this; 08 §4 is why migrated numbers are prefixed at all.
    select coalesce(array_agg(invoice_id), '{}')
      into v_old_ids
      from public.sales_invoice
     where invoice_no like 'MIG-%';

    if array_length(v_old_ids, 1) is not null then
        -- 0042. An imported invoice can now carry an import_edit stock adjustment, from having
        -- been corrected here. Restoring the sheet's original lines while leaving that adjustment
        -- applied would hold stock at a correction whose invoice is being replaced. Cleared first,
        -- inside the same transaction, so the sheet and the stock cannot disagree.
        perform public.clear_import_edits(v_old_ids);

        -- 0049. And the SALE/REJECTION movements the previous import wrote. This is the whole of
        -- what stops a re-import deducting twice. BEFORE the lines go: they are found through sales_line.line_id, and once the
        -- lines are deleted there is nothing left to find them by -- they would sit in the ledger
        -- deducting carats for an invoice that no longer exists.
        delete from public.stock_movement
         where ref_type = 'sales_line'
           and ref_id in (select line_id from public.sales_line
                           where invoice_id = any(v_old_ids));

        delete from public.receipt     where invoice_id = any(v_old_ids);
        delete from public.sales_line  where invoice_id = any(v_old_ids);
        delete from public.sales_invoice where invoice_id = any(v_old_ids);
        get diagnostics v_deleted = row_count;
    end if;

    -- Invoices first, keeping invoice_no as the handle to hang lines off: the
    -- ids are assigned by the sequence and the payload cannot know them.
    with incoming as (
        select inv from jsonb_array_elements(p_payload->'invoices') as inv
    ),
    written as (
        insert into public.sales_invoice
            (invoice_no, invoice_date, buyer_id, broker_id, broker_pct,
             terms_days, doc_type, currency_id, status,
             created_by, updated_by)
        select inv->>'invoice_no',
               (inv->>'invoice_date')::date,
               (inv->>'buyer_id')::bigint,
               nullif(inv->>'broker_id', '')::bigint,
               coalesce((inv->>'broker_pct')::numeric, 0),
               coalesce((inv->>'terms_days')::integer, 0),
               coalesce(inv->>'doc_type', 'BILL'),
               v_currency,
               'POSTED',
               auth.uid(),
               auth.uid()
          from incoming
        returning invoice_id, invoice_no
    )
    select count(*) into v_invoices from written;

    -- Lines, matched back by invoice_no.
    with incoming as (
        select inv->>'invoice_no' as no,
               jsonb_array_elements(coalesce(inv->'lines', '[]'::jsonb)) as ln
          from jsonb_array_elements(p_payload->'invoices') as inv
    )
    insert into public.sales_line
        (invoice_id, grade_id, size_id, gross_weight_ct, selection_ct,
         price_per_ct, ex_rate, less1_pct, less2_pct, remark)
    select i.invoice_id,
           (c.ln->>'grade_id')::bigint,
           (c.ln->>'size_id')::bigint,
           (c.ln->>'gross_weight_ct')::numeric,
           (c.ln->>'selection_ct')::numeric,
           (c.ln->>'price_per_ct')::numeric,
           coalesce((c.ln->>'ex_rate')::numeric, 1),
           coalesce((c.ln->>'less1_pct')::numeric, 0),
           coalesce((c.ln->>'less2_pct')::numeric, 0),
           nullif(c.ln->>'remark', '')
      from incoming c
      join public.sales_invoice i on i.invoice_no = c.no;

    get diagnostics v_lines = row_count;

    -- One receipt per invoice that carried money, exactly as the workbook's
    -- single overwritten "Rec. Amt" cell states it (DQ-11: there is no payment
    -- history to migrate, only a running total). Dated the invoice date, since
    -- the sheet records no payment date -- docs/08 §5 says declare it, not bury
    -- it. Method 'IMPORTED', unchanged from the client-side importer.
    --
    -- `received` arrives ALREADY CAPPED at the invoice total. The cap stays on
    -- the client because it needs the line amounts CALC-1 produces, and those
    -- are the calculation engine's to compute, not this function's.
    with incoming as (
        select inv->>'invoice_no' as no,
               (inv->>'received')::numeric as received,
               (inv->>'invoice_date')::date as on_date
          from jsonb_array_elements(p_payload->'invoices') as inv
    )
    insert into public.receipt (invoice_id, receipt_date, amount, method, created_by)
    select i.invoice_id, c.on_date, c.received, 'IMPORTED', auth.uid()
      from incoming c
      join public.sales_invoice i on i.invoice_no = c.no
     where c.received is not null and c.received > 0;

    get diagnostics v_receipts = row_count;

    -- 0051 - EVERY imported sale comes out of stock, BUT NEVER BELOW ZERO.
    --
    -- 0049 took every line in full and let the position go negative. It is allowed to (0047), but
    -- on a sheet of historic sales it reads as thousands of carats owed that nobody owes. The desk
    -- has settled it: take what is on the shelf, leave what is not there, and say what was left.
    --
    -- HOW THE SHELF IS SHARED OUT. Lines are walked per grade x size bucket in invoice-date order,
    -- oldest first, with a running total of what earlier lines already took. Each line gets what is
    -- left of the bucket and no more, so the earliest sales come out whole and the shortfall lands
    -- on the latest ones. Spreading it pro-rata instead would leave every line slightly wrong
    -- rather than a named few plainly short, and "which sales could not be taken out" is a question
    -- the office can act on.
    --
    -- SALE BEFORE REJECTION inside a line, for the same reason: what was sold is the part that
    -- matters, so a line with only some room left records the sale and drops the rejection.
    --
    -- WHAT THIS COSTS, said plainly. A capped line's movements weigh LESS than the line does, so
    -- for that line the ledger and the invoice disagree -- by exactly the carats the shelf did not
    -- have. That disagreement is the point, and it is reported as short_ct / short_buckets rather
    -- than left for somebody to find.
    --
    -- The room is read from v_stock_position.balance_ct, which is net of reservations (0043), so an
    -- import cannot take carats a half-typed sales entry is holding. Floored at zero: a bucket
    -- already negative from earlier trading has nothing to give, and an import must not be the
    -- thing that digs it deeper. And it is the position BEFORE this import writes anything -- the
    -- movements below are inserted in the same statement that reads it.
    --
    -- Type, date, price and tag are 0049's unchanged: SALE and REJECTION, the pair post_invoice
    -- writes, tagged 'sales_line' so cancel_invoice reverses them, dated the INVOICE date so the
    -- Stock page counts them where they belong in time.
    with wanted as (
        select l.line_id, l.grade_id, l.size_id, l.price_per_ct,
               i.invoice_date,
               coalesce(l.selection_ct, 0) as selection_ct,
               coalesce(l.rejection_ct, 0) as rejection_ct,
               coalesce(l.selection_ct, 0) + coalesce(l.rejection_ct, 0) as gross_ct
          from public.sales_line l
          join public.sales_invoice i on i.invoice_id = l.invoice_id
         where i.invoice_no like 'MIG-%'
    ),
    running as (
        select w.*,
               greatest(coalesce(p.balance_ct, 0), 0) as room,
               coalesce(sum(w.gross_ct) over (partition by w.grade_id, w.size_id
                                              order by w.invoice_date, w.line_id
                                              rows between unbounded preceding and 1 preceding), 0)
                   as taken_before
          from wanted w
          left join public.v_stock_position p
                 on p.grade_id = w.grade_id and p.size_id = w.size_id
    ),
    capped as (
        select r.*,
               greatest(0, least(r.gross_ct, r.room - r.taken_before)) as allow_ct
          from running r
    ),
    split as (
        select c.*,
               least(c.selection_ct, c.allow_ct) as sale_out,
               least(c.rejection_ct, c.allow_ct - least(c.selection_ct, c.allow_ct)) as rej_out
          from capped c
    ),
    wrote as (
        insert into public.stock_movement
            (movement_date, grade_id, size_id, movement_type, weight_ct,
             price_per_ct, ref_type, ref_id, created_by)
        select s.invoice_date, s.grade_id, s.size_id, 'SALE', s.sale_out,
               s.price_per_ct, 'sales_line', s.line_id, auth.uid()
          from split s
         where s.sale_out > 0
        union all
        select s.invoice_date, s.grade_id, s.size_id, 'REJECTION', s.rej_out,
               s.price_per_ct, 'sales_line', s.line_id, auth.uid()
          from split s
         where s.rej_out > 0
        returning weight_ct
    ),
    -- The WEIGHT off the movements themselves, as 0050 summed it: they are what actually left.
    took as (
        select coalesce(sum(weight_ct), 0) as ct_out from wrote
    ),
    -- The COUNT off the lines, and only those that wrote a SALE -- which is what 0050's
    -- `get diagnostics` after its first insert counted, and what "Stock taken out: N line(s)"
    -- has always meant. Counting the movement rows instead double-counts every line that
    -- rejected as well as sold.
    --
    -- A line capped to nothing is not counted, and should not be: it took nothing out. A line
    -- that sold nothing was already excluded under 0050 for the same reason.
    counted as (
        select count(*) filter (where s.sale_out > 0) as lines_out from split s
    ),
    fell_short as (
        -- Rounded at 0.00005 so that a carat lost to numeric rounding is not reported as a
        -- shortage; the sheet is kept to four places everywhere else.
        select coalesce(round(sum(c.gross_ct - c.allow_ct), 4), 0) as short_ct,
               count(distinct (c.grade_id, c.size_id))             as short_buckets
          from capped c
         where c.gross_ct - c.allow_ct > 0.00005
    )
    select counted.lines_out, round(took.ct_out, 4),
           fell_short.short_ct, fell_short.short_buckets
      into v_stock_lines, v_stock_ct, v_short_ct, v_short_bkts
      from took, counted, fell_short;

    return jsonb_build_object('ok', true, 'deleted', v_deleted,
                              'invoices', v_invoices, 'lines', v_lines,
                              'receipts', v_receipts,
                              'stock_lines', v_stock_lines,
                              'stock_ct', v_stock_ct,
                              'short_ct', v_short_ct,
                              'short_buckets', v_short_bkts);
end;
$$;

comment on function public.replace_imported_sales is
    '0051. Replaces the imported MIG- sales history in one transaction and takes stock out for every imported line - SALE for what was sold, REJECTION for what was rejected - up to what the grade x size bucket actually holds, never below zero. Reports stock_lines and stock_ct for what left, and short_ct and short_buckets for what the shelf could not cover. Supersedes 0050, which deducted every line in full and allowed the position to go negative. A re-import removes the movements the previous import wrote before writing its own, so importing the same sheet twice deducts once.';

commit;


-- ###########################################################################
-- SECTION 17 of 17 : 0052_reconciliation_counts_imported_sales.sql
-- ###########################################################################

-- ---------------------------------------------------------------------------
-- 0052. The reconciliation counts imported sales, because they now move stock.
--
-- THE FAULT
--
-- v_reconciliation compares two sides of the same question, per grade x size:
--
--     moved_out_ct        every SALE movement
--     sold_on_invoices_ct the selection on POSTED lines -- but NOT on MIG- ones
--
-- That asymmetry was correct when 0026 wrote it. An imported invoice moved no
-- stock at all: the sheet was read as an opening balance already net of its own
-- history, so a MIG- line had no movement to answer for and counting it would
-- have reported a difference against nothing.
--
-- 0049 changed that and nothing here followed. Imported sales write SALE
-- movements now, exactly as a sale typed into the app does. Those movements land
-- on the LEFT, their lines are still excluded from the RIGHT, and the view
-- reports a difference equal to the whole imported history -- 314 ct on the
-- desk's current sheet, and 28,529 ct on the full one. It cannot be cleared by
-- any amount of correct trading, which is the worst kind of red figure: one that
-- teaches the office to ignore the report.
--
-- THE FIX
--
-- One line removed. Both sides now count an imported sale, so it cancels out of
-- the difference the way an app-entered sale always has.
--
-- WHAT THE DIFFERENCE MEANS AFTERWARDS, and it is not always zero. 0051 caps an
-- imported line at what its bucket holds, so a line the shelf could not cover
-- writes a SALE SMALLER than its selection_ct. The remainder shows up here as a
-- NEGATIVE diff_ct -- the invoice says more was sold than the ledger took out,
-- which is exactly true and exactly what the office needs to see.
--
-- IT IS PART OF short_ct, NOT ALL OF IT, and the difference matters to anyone
-- reconciling the two. This view compares SALE movements against selection_ct;
-- rejections appear on NEITHER side. The import dialog's short_ct counts the
-- whole parcel, sold and rejected alike. So diff_ct is the SALE half:
--
--     short_ct  =  (selection short, which is -diff_ct)  +  (rejection short)
--
-- Measured on the desk's sheet: 334.1235 short in total, of which 203.2735 was
-- selection and shows here, and 130.8500 was rejection and does not. Reading a
-- mismatch between the two figures as a fault would be reading it wrongly.
--
-- So: a clean import reconciles to zero. A short one reports its shortfall, in
-- the right buckets, permanently, until somebody puts the stock in or corrects
-- the sale. That is the report doing its job rather than carrying a constant.
--
-- NOTHING ELSE CHANGES. Cancelled invoices are still excluded from both sides
-- (0041). The columns, their names and their order are 0041's, untouched --
-- "create or replace view" may only append, and this appends nothing.
--
-- A VIEW ONLY. No data is touched, no movement is written or removed, and every
-- balance on every screen is exactly what it was a moment before this ran.
-- ---------------------------------------------------------------------------

begin;

create or replace view public.v_reconciliation as
select
    g.code                                  as grade_code,
    s.code                                  as size_code,
    coalesce(mv.sale_ct, 0)                 as moved_out_ct,
    coalesce(sl.sold_ct, 0)                 as sold_on_invoices_ct,
    round(coalesce(mv.sale_ct, 0) - coalesce(sl.sold_ct, 0), 4) as diff_ct,
    abs(coalesce(mv.sale_ct, 0) - coalesce(sl.sold_ct, 0)) < 0.0001 as reconciles
from public.grade g
cross join public.size_bucket s
left join lateral (
    select sum(m.weight_ct) as sale_ct
    from public.stock_movement m
    -- LEFT joined, and coalesced below: a SALE movement that points at no line is not something
    -- this view should hide. It has no invoice to excuse it, so it stays in the difference where
    -- somebody will see it.
    left join public.sales_line    l on l.line_id    = m.ref_id and m.ref_type = 'sales_line'
    left join public.sales_invoice i on i.invoice_id = l.invoice_id
    where m.grade_id = g.grade_id and m.size_id = s.size_id
      and m.movement_type = 'SALE'
      -- 0041. The cancelled invoice's lines already left sold_ct below; its movements leave here.
      and coalesce(i.status, 'POSTED') <> 'CANCELLED'
) mv on true
left join lateral (
    select sum(l.selection_ct) as sold_ct
    from public.sales_line l
    join public.sales_invoice i using (invoice_id)
    where l.grade_id = g.grade_id and l.size_id = s.size_id
      and i.status = 'POSTED'
      -- 0052. 0026 excluded MIG- invoices here because an imported sale moved no stock. Since 0049
      -- it does, and its SALE movement is counted above -- so excluding the line it came from left
      -- the whole imported history sitting in the difference with nothing to answer it.
) sl on true;

comment on view public.v_reconciliation is
    'CALC-8. Carats leaving stock as SALE movements against carats sold on invoices, per grade x size. Imported MIG- invoices are counted on BOTH sides (0052): they move stock since 0049, so excluding their lines left their movements unanswered. Where 0051 capped an imported line at what its bucket held, diff_ct is negative by the SELECTION the shelf could not cover - part of the import dialog''s short_ct, not all of it, because that figure counts rejections too and this view counts them on neither side. Cancelled invoices are excluded from BOTH sides (0041): cancel_invoice leaves the SALE movement in place and reverses it with an ADJUST, so counting the movement while the line had already dropped out reported a difference that could never be cleared.';

commit;


-- ===========================================================================
-- Did it land? Every row must read 'ok'. verify_new_project.sql cannot check
-- these -- it pins the schema as it stood before them and passes either way.
-- ===========================================================================
select 'stock_reservation table (0043)' as object,
       case when to_regclass('public.stock_reservation') is not null then 'ok' else 'MISSING' end as status
union all select 'reserve_line (0043)',
       case when to_regproc('public.reserve_line') is not null then 'ok' else 'MISSING' end
union all select 'release_entry (0043)',
       case when to_regproc('public.release_entry') is not null then 'ok' else 'MISSING' end
union all select 'holds released when a sale posts (0043)',
       case when exists (select 1 from pg_trigger where tgname = 'sales_invoice_release_reservations'
                           and not tgisinternal) then 'ok' else 'MISSING' end
-- The COLUMN, not the view: 0043 creates v_stock_position too, so the view being
-- there proves nothing about 0044 having run over the top of it.
union all select 'ledger_ct on v_stock_position (0044)',
       case when exists (select 1 from information_schema.columns where table_schema = 'public'
                           and table_name = 'v_stock_position' and column_name = 'ledger_ct')
            then 'ok' else 'MISSING' end
union all select 'reserve and release are audited (0045)',
       case when exists (select 1 from pg_trigger where tgname = 'trg_audit_stock_reservation'
                           and not tgisinternal) then 'ok' else 'MISSING' end
union all select 'edit a posted invoice (0040/0042)',
       case when to_regproc('public.edit_posted_invoice') is not null then 'ok' else 'MISSING' end
union all select 'edit an imported invoice (0042)',
       case when to_regproc('public.imported_edit_delta') is not null then 'ok' else 'MISSING' end
-- 0041 rewrites a view rather than adding one, so its definition has to be read.
union all select 'reconciliation nets cancellations (0041)',
       case when coalesce(pg_get_viewdef('public.v_reconciliation'::regclass) ilike '%CANCELLED%', false)
            then 'ok' else 'MISSING' end
-- 0052 REMOVES 0026's MIG- exclusion from that same view, so this one reads the other way round:
-- the exclusion still being there means Section 17 did not run and the reconciliation will report
-- the whole imported history as an unclearable difference.
union all select 'reconciliation counts imported sales (0052)',
       case when coalesce(pg_get_viewdef('public.v_reconciliation'::regclass) like '%MIG-%%', false)
            then 'MISSING' else 'ok' end
union all select 'Unknown Grade exists (0039)',
       case when exists (select 1 from public.grade where code = 'Unknown Grade')
            then 'ok' else 'MISSING' end
union all select 'add a sieve size from the app (0036)',
       case when to_regproc('public.add_size') is not null then 'ok' else 'MISSING' end
-- 0046 adds a COLUMN to a view that already existed, so the view being there proves
-- nothing -- the column is the only evidence the section ran.
union all select 'line_count on v_invoice (0046)',
       case when exists (select 1 from information_schema.columns where table_schema = 'public'
                           and table_name = 'v_invoice' and column_name = 'line_count')
            then 'ok' else 'MISSING' end
-- 0048 rewrites a function rather than adding one, so its existence proves nothing -- the comment
-- it carries is the only evidence this section, and not an older one, is what is installed.
-- 0049 supersedes 0048, so the comment must read 0049 -- a database still saying 0048 has the
-- date-conditional version installed and will not deduct the older sales.
-- 0050 supersedes 0049, so the comment must read 0050 -- a database still saying 0049 deducts
-- correctly but reports no carat total, and the import dialog shows a blank weight.
-- 0051 supersedes 0050, so the comment must read 0051 -- a database still saying 0050 deducts every
-- line in full and lets the stock position go negative.
union all select 'import never drives stock negative (0051)',
       case when coalesce(obj_description('public.replace_imported_sales'::regproc) like '0051.%', false)
            then 'ok' else 'MISSING' end;

-- Step 2 reported 27 grades. This is why it now reads 28.
select 'grades' as thing, count(*) as count from public.grade
union all select 'reservations held (must be 0 on a new project)', count(*) from public.stock_reservation;
