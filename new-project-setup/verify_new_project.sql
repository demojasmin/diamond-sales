-- ---------------------------------------------------------------------------
-- Verify a NEW, EMPTY project is ready to receive the app.
--
-- Run this in the new project's SQL editor AFTER the schema has been copied
-- across. Every row must read 'ok'. Anything else names what is missing.
--
-- Writes nothing. Safe to run as often as you like.
--
-- The lists below are not a guess: they are every table, view and function the
-- desktop app actually calls, taken from Models.cs and from every Rpc(...) call
-- site. If one is missing the app does not fail at startup -- it fails on the
-- screen that needs it, which is a much worse way to find out.
-- ---------------------------------------------------------------------------

with
-- ── 1 · the tables the app reads and writes ───────────────────────────────
expected_tables(name) as (values
    ('app_config'), ('audit_log'), ('broker'), ('buyer'), ('currency'),
    ('grade'), ('grade_size'), ('login_attempt'), ('price_list'), ('profiles'),
    ('receipt'), ('rejection_disposition'), ('rough_intake'), ('sales_invoice'),
    ('sales_line'), ('size_bucket'), ('stock_movement')
),
-- ── 2 · the views every screen is built on ────────────────────────────────
expected_views(name) as (values
    ('v_invoice'), ('v_receivables_ageing'), ('v_reconciliation'),
    ('v_sales_line'), ('v_stock_import_batch'), ('v_stock_movement'),
    ('v_stock_position')
),
-- ── 3 · the functions · all 13 the app calls, plus the ones they call ─────
expected_functions(name) as (values
    ('add_grade'), ('adjust_stock'), ('adjust_stock_at_cost'), ('assert_stock'),
    ('cancel_invoice'), ('clear_login_failures'), ('convert_stock'),
    ('delete_imported_stock'), ('import_stock'), ('lockout_minutes'),
    ('login_locked_for'), ('max_login_attempts'), ('negative_stock_policy'),
    ('next_invoice_no'), ('note_login_failure'), ('post_invoice'),
    ('record_rejection'), ('replace_imported_sales'), ('replace_imported_stock')
),
-- ── 4 · settings the app reads by name; a missing key is a silent default ──
expected_config(key) as (values
    ('alert_low_stock_ct'), ('negative_stock'), ('lockout_minutes'),
    ('max_login_attempts'), ('session_timeout_min')
),

results(sort_key, check_name, detail, status) as (

    -- structure ------------------------------------------------------------
    select 1, 'tables', e.name, 'MISSING'
      from expected_tables e
     where not exists (select 1 from information_schema.tables
                        where table_schema = 'public' and table_name = e.name)

    union all
    select 2, 'views', e.name, 'MISSING'
      from expected_views e
     where not exists (select 1 from information_schema.views
                        where table_schema = 'public' and table_name = e.name)

    union all
    select 3, 'functions', e.name, 'MISSING'
      from expected_functions e
     where not exists (select 1 from information_schema.routines
                        where routine_schema = 'public' and routine_name = e.name)

    -- security -------------------------------------------------------------
    -- A table with RLS switched off is readable by anyone holding the anon
    -- key, which is published in the app. This is the check that matters most.
    union all
    select 4, 'RLS disabled', c.relname, 'OPEN TO EVERYONE'
      from pg_class c
     where c.relnamespace = 'public'::regnamespace
       and c.relkind = 'r'
       and not c.relrowsecurity

    -- RLS on with no policy is the opposite failure: the table is invisible
    -- to every signed-in user and the screen renders blank with no error.
    union all
    select 5, 'RLS with no policy', c.relname, 'NOBODY CAN READ IT'
      from pg_class c
     where c.relnamespace = 'public'::regnamespace
       and c.relkind = 'r'
       and c.relrowsecurity
       and not exists (select 1 from pg_policies p
                        where p.schemaname = 'public' and p.tablename = c.relname)

    -- catalogue ------------------------------------------------------------
    union all
    select 6, 'grades', 'none seeded', 'EMPTY'
     where not exists (select 1 from public.grade)

    union all
    select 6, 'sizes', 'none seeded', 'EMPTY'
     where not exists (select 1 from public.size_bucket)

    -- Every grade needs its size pairings or it imports and can never be sold
    -- (0018 puts a trigger on sales_line). Caught here rather than at the
    -- client's first invoice.
    union all
    select 7, 'grade with no sizes', g.code, 'CANNOT BE SOLD'
      from public.grade g
     where g.active
       and not exists (select 1 from public.grade_size gs where gs.grade_id = g.grade_id)

    union all
    select 8, 'setting', e.key, 'MISSING'
      from expected_config e
     where not exists (select 1 from public.app_config c where c.key = e.key)

    -- access ---------------------------------------------------------------
    -- Without an active owner nobody can reach Settings, Users or imports, and
    -- admin-users refuses to create one because the caller must already be an
    -- owner. There is no way in from the app: it must be inserted by hand.
    union all
    select 9, 'owner login', 'no active owner', 'NOBODY CAN ADMINISTER'
     where not exists (select 1 from public.profiles
                        where role = 'owner' and active)

    -- A profile whose id is not a real login can never sign in.
    union all
    select 10, 'profile without a login', p.id::text, 'ORPHANED'
      from public.profiles p
     where not exists (select 1 from auth.users u where u.id = p.id)

    -- Role is compared against these three literals by the edge function and
    -- by the CHECK constraint. 'Owner' or 'OWNER ' silently grants nothing.
    union all
    select 11, 'bad role value', p.role, 'NOT sales/manager/owner'
      from public.profiles p
     where p.role not in ('sales', 'manager', 'owner')

    -- a new client starts clean --------------------------------------------
    -- If any of these carry rows, data came across from another client's
    -- database. That is a far worse problem than a missing table.
    union all
    select 12, 'DATA FROM ANOTHER CLIENT', 'sales_invoice: ' || count(*)::text, 'MUST BE EMPTY'
      from public.sales_invoice having count(*) > 0

    union all
    select 12, 'DATA FROM ANOTHER CLIENT', 'stock_movement: ' || count(*)::text, 'MUST BE EMPTY'
      from public.stock_movement having count(*) > 0

    union all
    select 12, 'DATA FROM ANOTHER CLIENT', 'buyer: ' || count(*)::text, 'MUST BE EMPTY'
      from public.buyer having count(*) > 0
)

select check_name, detail, status
  from results
 union all
select 'ALL CHECKS PASSED', 'the project is ready for the app', 'ok'
 where not exists (select 1 from results)
 order by 1;


-- ---------------------------------------------------------------------------
-- Counts, for the record. Read these against the old project's own numbers
-- when deciding whether the catalogue came across whole.
-- ---------------------------------------------------------------------------
select 'tables'    as object, count(*) from information_schema.tables   where table_schema  = 'public'
union all select 'views',     count(*) from information_schema.views    where table_schema  = 'public'
union all select 'functions', count(*) from information_schema.routines where routine_schema = 'public'
union all select 'policies',  count(*) from pg_policies                 where schemaname    = 'public'
union all select 'triggers',  count(*) from information_schema.triggers where trigger_schema = 'public'
union all select 'grades',    count(*) from public.grade
union all select 'sizes',     count(*) from public.size_bucket
union all select 'pairings',  count(*) from public.grade_size
-- Should be 1: 14+ ships retired, present so history reads, closed to new entries.
-- A 0 here means 0035 did not run, and the app will still offer the retired sieve.
union all select 'retired sizes', count(*) from public.size_bucket where not active
union all select 'profiles',  count(*) from public.profiles;
