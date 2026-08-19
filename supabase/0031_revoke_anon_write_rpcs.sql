-- ---------------------------------------------------------------------------
-- 0031 · The write functions must not be callable with the public key.
--
-- WHAT IS WRONG
--
-- Every RPC in this database is granted to the "anon" role. The anon key is
-- published by design -- it ships inside the desktop app, it is in
-- appsettings.json, it is in docs/12 -- so "granted to anon" means "callable by
-- anybody who has ever seen the app".
--
-- For most functions that is harmless: they run as the CALLER, so RLS still
-- refuses an unauthenticated request. Five do not. These are SECURITY DEFINER,
-- which means they run as the owner and RLS does not apply to them at all:
--
--     delete_imported_stock()      wipes the imported stock position
--     import_stock(...)            replaces the whole stock position
--     replace_imported_stock(...)  the same, by its older name
--     replace_imported_sales(...)  deletes and rewrites the migrated invoices
--     add_grade(...)               writes rows into the catalogue
--
-- Confirmed against the live project, not assumed: an unauthenticated caller
-- holding only the publishable key successfully executed a SECURITY DEFINER
-- function (login_locked_for) and got an answer.
--
-- WHAT STAYS
--
-- Three functions are called by the SIGN-IN SCREEN, before anybody is
-- authenticated, and must keep their anon grant or the lockout protection
-- breaks:
--
--     login_locked_for(...)     asked before the password is sent
--     note_login_failure(...)   recorded when it is wrong
--     clear_login_failures(...) cleared when it is right
--
-- note_login_failure is itself worth a second look one day: anyone can call it
-- to lock a known email out for lockout_minutes. That is a real nuisance, but
-- closing it needs a different design -- a captcha, or rate limiting at the
-- edge -- and removing the grant would disable the lockout entirely, which is
-- worse. Left alone deliberately, and written down rather than forgotten.
--
-- Trigger functions (audit_change, handle_new_user, touch_updated_at,
-- sales_line_grade_size_guard) are also left alone: they are never called
-- directly, and revoking EXECUTE on a trigger function risks breaking the
-- trigger itself.
--
-- APPLIES TO BOTH PROJECTS. This came out of the dump, so Priya gems has it
-- too. Run it there as well.
--
-- Safe to run repeatedly. Grants nothing; only takes away.
-- ---------------------------------------------------------------------------

-- ── FIRST: take it away from PUBLIC ────────────────────────────────────────
--
-- Revoking from anon alone does nothing for most of these. Postgres grants
-- EXECUTE on every new function to PUBLIC, and anon is a member of PUBLIC, so
-- the privilege arrives by inheritance and a revoke aimed at anon removes a
-- grant that was never what let it in.
--
-- Proved on the live project: the first run of this migration revoked from
-- anon and left NINE functions still callable. The ones that came out clean
-- were exactly the ones the original schema had already revoked from PUBLIC.
--
-- Safe because "authenticated" holds its own explicit grant on every one of
-- these -- the verification at the foot checks that, so a mistake here shows
-- up as a failing row rather than as an app that stops working.
revoke execute on function public.delete_imported_stock()                       from public;
revoke execute on function public.import_stock(date, jsonb, boolean, uuid, text) from public;
revoke execute on function public.replace_imported_stock(date, jsonb)           from public;
revoke execute on function public.replace_imported_sales(jsonb)                 from public;
revoke execute on function public.add_grade(text, text)                         from public;
revoke execute on function public.adjust_stock(bigint, bigint, numeric, text, date, uuid)                   from public;
revoke execute on function public.adjust_stock_at_cost(bigint, bigint, numeric, numeric, text, date, uuid)  from public;
revoke execute on function public.post_invoice(bigint, boolean)                                             from public;
revoke execute on function public.cancel_invoice(bigint, text)                                              from public;
revoke execute on function public.convert_stock(bigint, bigint, bigint, bigint, numeric, numeric, date, uuid) from public;
revoke execute on function public.record_rejection(bigint, bigint, numeric, numeric, date, uuid, jsonb)     from public;
revoke execute on function public.dashboard_summary(date, date)                 from public;
revoke execute on function public.margin_summary(date, date)                    from public;
revoke execute on function public.next_invoice_no(date)                         from public;
revoke execute on function public.assert_stock(bigint, bigint, numeric)         from public;


-- ── the five that bypass RLS ───────────────────────────────────────────────
revoke execute on function public.delete_imported_stock()                       from anon;
revoke execute on function public.import_stock(date, jsonb, boolean, uuid, text) from anon;
revoke execute on function public.replace_imported_stock(date, jsonb)           from anon;
revoke execute on function public.replace_imported_sales(jsonb)                 from anon;
revoke execute on function public.add_grade(text, text)                         from anon;

-- ── the rest of the write path ─────────────────────────────────────────────
-- These run as the caller, so RLS already refuses anon. Revoked anyway: a
-- policy that is edited later should not be the only thing standing between a
-- public key and the stock ledger.
revoke execute on function public.adjust_stock(bigint, bigint, numeric, text, date, uuid)                   from anon;
revoke execute on function public.adjust_stock_at_cost(bigint, bigint, numeric, numeric, text, date, uuid)  from anon;
revoke execute on function public.post_invoice(bigint, boolean)                                             from anon;
revoke execute on function public.cancel_invoice(bigint, text)                                              from anon;
revoke execute on function public.convert_stock(bigint, bigint, bigint, bigint, numeric, numeric, date, uuid) from anon;
revoke execute on function public.record_rejection(bigint, bigint, numeric, numeric, date, uuid, jsonb)     from anon;

-- ── reporting functions ────────────────────────────────────────────────────
-- Read-only, but they read figures no stranger should see.
revoke execute on function public.dashboard_summary(date, date) from anon;
revoke execute on function public.margin_summary(date, date)    from anon;
revoke execute on function public.next_invoice_no(date)         from anon;
revoke execute on function public.assert_stock(bigint, bigint, numeric) from anon;


-- ---------------------------------------------------------------------------
-- Verification · every row must read 'ok'.
--
-- The three sign-in functions must still be callable by anon; everything else
-- above must not be.
-- ---------------------------------------------------------------------------
with expected(fn, anon_should_have) as (values
    ('delete_imported_stock', false), ('import_stock', false),
    ('replace_imported_stock', false), ('replace_imported_sales', false),
    ('add_grade', false), ('adjust_stock', false), ('adjust_stock_at_cost', false),
    ('post_invoice', false), ('cancel_invoice', false), ('convert_stock', false),
    ('record_rejection', false), ('dashboard_summary', false),
    ('margin_summary', false), ('next_invoice_no', false), ('assert_stock', false),
    ('login_locked_for', true), ('note_login_failure', true),
    ('clear_login_failures', true)
)
select e.fn,
       case when e.anon_should_have then 'anon may call' else 'anon must not call' end as rule,
       case when bool_or(has_function_privilege('anon', p.oid, 'execute')) = e.anon_should_have
            then 'ok' else 'WRONG' end as anon,
       -- The app runs as "authenticated". If a revoke above took the privilege
       -- from the signed-in user too, the app breaks -- so it is checked here
       -- rather than discovered on somebody's desk.
       case when bool_and(has_function_privilege('authenticated', p.oid, 'execute'))
            then 'ok' else 'APP WILL BREAK' end as app_can_still_call
  from expected e
  join pg_proc p on p.proname = e.fn
                and p.pronamespace = 'public'::regnamespace
 group by e.fn, e.anon_should_have
 order by 3 desc, 4 desc, 1;
