-- ---------------------------------------------------------------------------
-- Is THIS project ready to import the printed stock sheet?
--
-- Run it in the project's SQL editor. Every row reads 'ok', 'not yet' or the
-- name of what is missing. Writes nothing, changes nothing, safe to run as
-- often as you like.
--
-- WHY A SECOND VERIFIER
--
-- verify_new_project.sql answers "can the app run here at all", and it predates
-- everything the PDF stock sheet needed: 0034 through 0039. This one answers
-- the question that keeps coming up instead -- "Demo does X and this project
-- does not, what is pending" -- and the honest answer has two halves.
--
-- The CODE is shared. Both projects run the same build, so nothing about the
-- reader, the report or the pickers can be present in one and absent in the
-- other.
--
-- The DATA is not. A catalogue row, a function and a config key each live in
-- one database. Applying a migration to Demo does nothing for Priya, and an
-- import into Demo writes Demo's config keys only. That is the whole of the
-- difference, and this lists it row by row.
-- ---------------------------------------------------------------------------

with results(sort_key, check_name, detail, status) as (

    -- ── the functions the import path calls ───────────────────────────────
    -- Each is called by name from the desktop app. A missing one does not fail
    -- at startup; it fails on the screen that needs it, which is a much worse
    -- way to find out. add_grade answers the new-grade prompt, add_size the new
    -- sieve prompt, import_stock does the import itself, and sieve_key is what
    -- decides whether "1/5" and "0.2" are the same bucket.
    select 1, 'function ' || e.name, 'migration ' || e.came_from, 'MISSING'
      from (values ('add_grade', '0030'), ('add_size', '0034'),
                   ('sieve_key', '0034 / 0037'), ('import_stock', '0027'),
                   ('reject_retired_size', '0035'),
                   ('delete_imported_stock', '0027')) as e(name, came_from)
     where not exists (
        select 1 from pg_proc p join pg_namespace n on n.oid = p.pronamespace
         where n.nspname = 'public' and p.proname = e.name)

    union all
    select 1, 'every function the import path calls', '6 of 6', 'ok'
     where (select count(*) from pg_proc p join pg_namespace n on n.oid = p.pronamespace
             where n.nspname = 'public'
               and p.proname in ('add_grade', 'add_size', 'sieve_key', 'import_stock',
                                 'reject_retired_size', 'delete_imported_stock')) = 6

    -- ── the batch view · 0027 ─────────────────────────────────────────────
    -- The app refuses to import at all without this, and says so by name.
    union all
    select 2, 'v_stock_import_batch (0027)',
           'the import list the replace/add dialog is built on',
           case when exists (select 1 from pg_views
                              where schemaname = 'public' and viewname = 'v_stock_import_batch')
                then 'ok' else 'MISSING' end

    -- ── retirable sieves · 0035 ───────────────────────────────────────────
    -- SizeBucket.Active is read on every catalogue load. Without the column the
    -- model cannot map and every size list comes back empty.
    union all
    select 3, 'size_bucket.active (0035)', 'retired sieves stay readable',
           case when exists (select 1 from information_schema.columns
                              where table_schema = 'public' and table_name = 'size_bucket'
                                and column_name = 'active')
                then 'ok' else 'MISSING' end

    -- ── negative stock refused · 0038 ─────────────────────────────────────
    -- Recognised by the guard 0038 added, not by a version number: import_stock
    -- consults negative_stock_policy() before it replaces, so that call being
    -- inside the function IS the fix being present.
    union all
    select 4, 'import_stock refuses to leave a bucket negative (0038)',
           'checked under negative_stock = block, the same setting assert_stock obeys',
           case when exists (
                    select 1 from pg_proc p join pg_namespace n on n.oid = p.pronamespace
                     where n.nspname = 'public' and p.proname = 'import_stock'
                       and pg_get_functiondef(p.oid) ilike '%negative_stock_policy%')
                then 'ok' else 'APPLY 0038' end

    union all
    select 4, 'no bucket is negative right now',
           (select count(*)::text || ' bucket(s) below zero'
              from public.v_stock_position where balance_ct < 0),
           case when (select count(*) from public.v_stock_position where balance_ct < 0) = 0
                then 'ok' else 'RECONCILE THESE FIRST' end

    -- ── Unknown Grade · 0039 ──────────────────────────────────────────────
    union all
    select 5, 'grade "Unknown Grade" (0039)',
           'a printed row with no grade name imports instead of refusing',
           case when exists (select 1 from public.grade
                              where code = 'Unknown Grade' and active)
                then 'ok' else 'APPLY 0039' end

    union all
    select 5, 'Unknown Grade takes every sieve the import can use',
           (select count(*)::text from public.grade_size gs
              join public.grade g using (grade_id) where g.code = 'Unknown Grade')
           || ' of '
           || (select count(*)::text from public.size_bucket where code <> '-2'),
           case when (select count(*) from public.grade_size gs
                        join public.grade g using (grade_id)
                       where g.code = 'Unknown Grade')
                     = (select count(*) from public.size_bucket where code <> '-2')
                then 'ok' else 'PAIRINGS MISSING' end

    -- ── the sieves the client's sheet prints ──────────────────────────────
    -- Matched through sieve_key, not on the literal text, so this finds the row
    -- whether the project spells a fifth of a carat "1/5" or "0.2".
    union all
    select 6, 'sieve ' || e.printed || ' is in the catalogue',
           coalesce((select 'stored as "' || s.code || '"' from public.size_bucket s
                      where public.sieve_key(s.code) = public.sieve_key(e.printed) limit 1),
                    'the sheet prints this column'),
           case when exists (select 1 from public.size_bucket s
                              where public.sieve_key(s.code) = public.sieve_key(e.printed))
                then 'ok' else 'ADD IT AT THE NEXT IMPORT' end
      from (values ('6.5-'), ('6.5+'), ('11+'), ('1/6'), ('1/5'), ('1/4')) as e(printed)

    -- ── what the last import recorded HERE ────────────────────────────────
    -- Written by the app, not by a migration, and per project. Importing into
    -- Demo writes Demo's keys; this project gets them from an import into this
    -- project. Until then the Stock report falls back to whatever holds stock,
    -- so a column the sheet prints at 0.00 all the way down is not shown and
    -- the rates beside it have nowhere to come from.
    union all
    select 7, 'app_config."stock_sheet_sizes"',
           coalesce((select value from public.app_config where key = 'stock_sheet_sizes'),
                    'import a sheet in THIS project to record it'),
           case when exists (select 1 from public.app_config
                              where key = 'stock_sheet_sizes' and coalesce(value, '') <> '')
                then 'ok' else 'not yet' end

    union all
    select 7, 'app_config."stock_sheet_rates"',
           coalesce((select (length(value) - length(replace(value, ';', '')) + 1)::text
                            || ' rates recorded'
                       from public.app_config
                      where key = 'stock_sheet_rates' and coalesce(value, '') <> ''),
                    'import a sheet in THIS project to record them'),
           case when exists (select 1 from public.app_config
                              where key = 'stock_sheet_rates' and coalesce(value, '') <> '')
                then 'ok' else 'not yet' end

    -- ── the published key must not be able to write · 0031 ────────────────
    -- The anon key ships inside the app and is in appsettings.json, so "granted
    -- to anon" means "callable by anybody who has ever seen the installer".
    -- These five are SECURITY DEFINER, which means they run as the owner and RLS
    -- does not apply to them at all -- import_stock replaces the whole stock
    -- position and delete_imported_stock wipes it.
    --
    -- Checked here because 0031 lived OUTSIDE supabase/migrations for a while,
    -- so a project set up by applying that folder skipped it and nothing said so.
    -- Re-creating a function later does not fix it either: 0038 revokes from
    -- PUBLIC, and a grant made directly to anon survives that untouched.
    union all
    select 9, 'anon cannot call ' || e.name || ' (0031)',
           'SECURITY DEFINER, so RLS does not stand in front of it',
           case when bool_or(has_function_privilege('anon', p.oid, 'execute'))
                then 'APPLY 0031' else 'ok' end
      from (values ('delete_imported_stock'), ('import_stock'),
                   ('replace_imported_stock'), ('replace_imported_sales'),
                   ('add_grade')) as e(name)
      join pg_proc p on p.proname = e.name
                    and p.pronamespace = 'public'::regnamespace
     group by e.name

    -- The other half of that revoke: it must not have taken the privilege from
    -- the signed-in user as well, or the app breaks on somebody's desk instead.
    union all
    select 9, 'and the signed-in user still can',
           'authenticated keeps execute on all five',
           case when bool_and(has_function_privilege('authenticated', p.oid, 'execute'))
                then 'ok' else 'APP WILL BREAK' end
      from (values ('delete_imported_stock'), ('import_stock'),
                   ('replace_imported_stock'), ('replace_imported_sales'),
                   ('add_grade')) as e(name)
      join pg_proc p on p.proname = e.name
                    and p.pronamespace = 'public'::regnamespace

    -- ── and whether the app is allowed to write them ──────────────────────
    -- The config write is deliberately swallowed on failure: a completed import
    -- must never be reported as failed over a display preference. So a project
    -- where this is refused looks exactly like one where nobody has imported
    -- yet, and only this row tells them apart.
    union all
    select 8, 'the app may write app_config',
           'the two keys above are written after an import',
           case when has_table_privilege('authenticated', 'public.app_config', 'INSERT')
                 and has_table_privilege('authenticated', 'public.app_config', 'UPDATE')
                then 'ok' else 'GRANT NEEDED' end
)

-- The first row is the whole answer, so two projects can be compared at a glance
-- instead of read line against line. Anything not 'ok' counts as pending,
-- including 'not yet' -- a config key nobody has written is as much a reason
-- for this project to behave differently from the other one as a missing
-- function is.
select check_name, detail, status
  from (
        select 0 as sort_key,
               'ALL CHECKS' as check_name,
               count(*) filter (where status = 'ok')::text || ' ok, '
                 || count(*) filter (where status <> 'ok')::text || ' pending' as detail,
               case when count(*) filter (where status <> 'ok') = 0
                    then 'READY' else 'NOT READY' end as status
          from results
        union all
        select sort_key, check_name, detail, status from results
       ) all_rows
 order by sort_key, check_name;
