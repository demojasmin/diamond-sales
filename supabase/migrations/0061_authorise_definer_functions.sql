-- ---------------------------------------------------------------------------
-- 0061. Every SECURITY DEFINER function the app can call now checks WHO is calling.
--
-- WHY. Found 15 Oct 2026: these functions bypass row-level security (they run as their owner) and
-- were executable by any signed-in account with no role or active check -- so an INACTIVE account,
-- or a stranger who used the (then open) public sign-up, could e.g. call delete_imported_stock()
-- and wipe every imported holding, or replace the stock or the sales outright. The table policies
-- were right; these functions simply went around them.
--
-- HOW, without touching any function's logic:
--   * a function the app calls is renamed <name>__unchecked (and the app loses EXECUTE on it), and
--     a short function with the ORIGINAL name and the SAME arguments and defaults checks the caller,
--     then hands the call through unchanged. Allowed callers see exactly the behaviour they had.
--   * a function the app never calls loses EXECUTE for app users altogether. Other functions still
--     call it (they run as the owner), and the SQL editor still can.
-- Who may call what follows the table policies and the app: imports owner; grades/sizes manager or
-- owner; correcting a posted invoice manager/owner or the active user who created it; holds any
-- active staff; clearing a sign-in lockout only for your own address.
--
-- Also: a new profile starts INACTIVE (it was active by default -- a stranger signing up became an
-- active sales user). admin-users sets active explicitly when an owner creates a user.
--
-- Rollback: supabase/ROLLBACK_0061.sql. Safe to run twice: a function already wrapped is skipped.
-- ---------------------------------------------------------------------------
begin;

-- ── the check ────────────────────────────────────────────────────────────────
-- current_user_role() is null for an inactive profile or no profile, so both are refused.
create or replace function public.require_role(variadic p_roles text[])
returns void
language plpgsql
stable
set search_path = public, pg_temp
as $function$
begin
    if coalesce(public.current_user_role(), '') <> all (p_roles) then
        raise exception 'Not allowed: this needs an active % account.', array_to_string(p_roles, ' or ')
            using errcode = 'insufficient_privilege';
    end if;
end;
$function$;
revoke all on function public.require_role(text[]) from public, anon;
grant execute on function public.require_role(text[]) to authenticated, service_role;

-- ── rename the app-facing functions out of the way (skipped if already done) ─────────────────
do $$
declare
    f text;
begin
    foreach f in array array[
        'import_stock(date, jsonb, boolean, uuid, text)',
        'replace_imported_sales(jsonb)',
        'add_grade(text, text)',
        'add_size(text, text)',
        'edit_posted_invoice(bigint, date, bigint, bigint, numeric, integer, text, bigint, jsonb, text)',
        'reserve_line(uuid, text, bigint, bigint, numeric)',
        'release_line(uuid, text)',
        'release_entry(uuid)',
        'clear_login_failures(text)']
    loop
        if to_regprocedure('public.' || split_part(f, '(', 1) || '__unchecked(' || split_part(f, '(', 2)) is null then
            execute format('alter function public.%s rename to %I', f, split_part(f, '(', 1) || '__unchecked');
        end if;
        execute format('revoke all on function public.%s from public, anon, authenticated',
                       split_part(f, '(', 1) || '__unchecked(' || split_part(f, '(', 2));
    end loop;
end;
$$;

-- ── the guarded functions, original names and signatures ─────────────────────────────────────
create or replace function public.import_stock(p_as_at date, p_rows jsonb, p_replace boolean default true,
                                               p_batch uuid default null::uuid, p_source text default 'excel'::text)
returns jsonb language plpgsql security definer set search_path = public, pg_temp as $function$
begin
    perform public.require_role('owner');
    return public.import_stock__unchecked(p_as_at, p_rows, p_replace, p_batch, p_source);
end;
$function$;

create or replace function public.replace_imported_sales(p_payload jsonb)
returns jsonb language plpgsql security definer set search_path = public, pg_temp as $function$
begin
    perform public.require_role('owner');
    return public.replace_imported_sales__unchecked(p_payload);
end;
$function$;

create or replace function public.add_grade(p_code text, p_display_name text default null::text)
returns jsonb language plpgsql security definer set search_path = public, pg_temp as $function$
begin
    perform public.require_role('manager', 'owner');
    return public.add_grade__unchecked(p_code, p_display_name);
end;
$function$;

create or replace function public.add_size(p_code text, p_display_name text default null::text)
returns jsonb language plpgsql security definer set search_path = public, pg_temp as $function$
begin
    perform public.require_role('manager', 'owner');
    return public.add_size__unchecked(p_code, p_display_name);
end;
$function$;

create or replace function public.edit_posted_invoice(p_invoice_id bigint, p_invoice_date date, p_buyer_id bigint,
                                                      p_broker_id bigint, p_broker_pct numeric, p_terms_days integer,
                                                      p_doc_type text, p_currency_id bigint, p_lines jsonb, p_reason text)
returns jsonb language plpgsql security definer set search_path = public, pg_temp as $function$
declare
    v_role text := public.current_user_role();   -- null: inactive, no profile, or logged out
begin
    -- A manager or owner may correct any invoice; an active sales user only one they created
    -- (invoice_update lets the creator write a POSTED row too). Unknown invoices fall through to
    -- the function's own "not found".
    --
    -- Written on the role itself, not as "not is_manager_or_owner() and not ...": for an inactive
    -- account those return NULL, NOT NULL is NULL, and an IF on NULL does not raise -- which let an
    -- inactive account through (caught on Demo, 15 Oct 2026).
    if v_role is null
       or v_role not in ('sales', 'manager', 'owner')
       or (v_role = 'sales'
           and not exists (select 1 from public.sales_invoice
                            where invoice_id = p_invoice_id and created_by = auth.uid())) then
        raise exception 'Not allowed: only a manager, an owner or the person who created this invoice may correct it.'
            using errcode = 'insufficient_privilege';
    end if;
    return public.edit_posted_invoice__unchecked(p_invoice_id, p_invoice_date, p_buyer_id, p_broker_id,
                                                 p_broker_pct, p_terms_days, p_doc_type, p_currency_id,
                                                 p_lines, p_reason);
end;
$function$;

create or replace function public.reserve_line(p_client_ref uuid, p_line_key text, p_grade_id bigint,
                                               p_size_id bigint, p_weight_ct numeric)
returns jsonb language plpgsql security definer set search_path = public, pg_temp as $function$
begin
    perform public.require_role('sales', 'manager', 'owner');
    return public.reserve_line__unchecked(p_client_ref, p_line_key, p_grade_id, p_size_id, p_weight_ct);
end;
$function$;

create or replace function public.release_line(p_client_ref uuid, p_line_key text)
returns jsonb language plpgsql security definer set search_path = public, pg_temp as $function$
begin
    perform public.require_role('sales', 'manager', 'owner');
    return public.release_line__unchecked(p_client_ref, p_line_key);
end;
$function$;

create or replace function public.release_entry(p_client_ref uuid)
returns jsonb language plpgsql security definer set search_path = public, pg_temp as $function$
begin
    perform public.require_role('sales', 'manager', 'owner');
    return public.release_entry__unchecked(p_client_ref);
end;
$function$;

-- Clearing a lockout: only your own, once signed in. Anyone could reset anyone's counter before,
-- which made the lockout pointless. The app calls it straight after a successful sign-in.
create or replace function public.clear_login_failures(p_email text)
returns void language plpgsql security definer set search_path = public, pg_temp as $function$
begin
    if auth.uid() is null or lower(coalesce(p_email, '')) <> lower(coalesce(auth.jwt() ->> 'email', '')) then
        raise exception 'Not allowed: a sign-in lockout can only be cleared by that account, once signed in.'
            using errcode = 'insufficient_privilege';
    end if;
    perform public.clear_login_failures__unchecked(p_email);
end;
$function$;

-- Who may call the guarded functions: signed-in users (the check above decides the rest).
revoke all on function public.import_stock(date, jsonb, boolean, uuid, text) from public, anon;
revoke all on function public.replace_imported_sales(jsonb) from public, anon;
revoke all on function public.add_grade(text, text) from public, anon;
revoke all on function public.add_size(text, text) from public, anon;
revoke all on function public.edit_posted_invoice(bigint, date, bigint, bigint, numeric, integer, text, bigint, jsonb, text) from public, anon;
revoke all on function public.reserve_line(uuid, text, bigint, bigint, numeric) from public, anon;
revoke all on function public.release_line(uuid, text) from public, anon;
revoke all on function public.release_entry(uuid) from public, anon;
revoke all on function public.clear_login_failures(text) from public, anon;
grant execute on function public.import_stock(date, jsonb, boolean, uuid, text) to authenticated, service_role;
grant execute on function public.replace_imported_sales(jsonb) to authenticated, service_role;
grant execute on function public.add_grade(text, text) to authenticated, service_role;
grant execute on function public.add_size(text, text) to authenticated, service_role;
grant execute on function public.edit_posted_invoice(bigint, date, bigint, bigint, numeric, integer, text, bigint, jsonb, text) to authenticated, service_role;
grant execute on function public.reserve_line(uuid, text, bigint, bigint, numeric) to authenticated, service_role;
grant execute on function public.release_line(uuid, text) to authenticated, service_role;
grant execute on function public.release_entry(uuid) to authenticated, service_role;
grant execute on function public.clear_login_failures(text) to authenticated, service_role;

-- ── functions the app never calls: app users lose EXECUTE ─────────────────────────────────────
-- delete_imported_stock wipes every imported holding; clear_import_edits is called only by
-- edit_posted_invoice and replace_imported_sales (as the owner, so unaffected); imported_edit_delta
-- is called by nothing. The SQL editor keeps them.
revoke all on function public.delete_imported_stock() from public, anon, authenticated;
revoke all on function public.clear_import_edits(bigint[]) from public, anon, authenticated;
revoke all on function public.imported_edit_delta(bigint, jsonb) from public, anon, authenticated;

-- ── a new profile starts inactive ─────────────────────────────────────────────────────────────
alter table public.profiles alter column active set default false;

commit;

-- PostgREST re-reads the functions now, not at its next restart.
notify pgrst, 'reload schema';

-- Check: what an app user can call now. Every row must read as expected.
select f as function,
       has_function_privilege('anon', ('public.' || f)::regprocedure, 'execute')          as anon,
       has_function_privilege('authenticated', ('public.' || f)::regprocedure, 'execute') as signed_in
  from unnest(array[
       -- guarded: anon false, signed_in true
       'import_stock(date,jsonb,boolean,uuid,text)', 'replace_imported_sales(jsonb)', 'add_grade(text,text)',
       'add_size(text,text)', 'edit_posted_invoice(bigint,date,bigint,bigint,numeric,integer,text,bigint,jsonb,text)',
       'reserve_line(uuid,text,bigint,bigint,numeric)', 'release_line(uuid,text)', 'release_entry(uuid)',
       'clear_login_failures(text)', 'require_role(text[])',
       -- originals and app-never-calls: anon false, signed_in false
       'import_stock__unchecked(date,jsonb,boolean,uuid,text)', 'replace_imported_sales__unchecked(jsonb)',
       'add_grade__unchecked(text,text)', 'add_size__unchecked(text,text)',
       'edit_posted_invoice__unchecked(bigint,date,bigint,bigint,numeric,integer,text,bigint,jsonb,text)',
       'reserve_line__unchecked(uuid,text,bigint,bigint,numeric)', 'release_line__unchecked(uuid,text)',
       'release_entry__unchecked(uuid)', 'clear_login_failures__unchecked(text)',
       'delete_imported_stock()', 'clear_import_edits(bigint[])', 'imported_edit_delta(bigint,jsonb)']) f;
