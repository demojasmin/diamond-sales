-- ---------------------------------------------------------------------------
-- 0060. Let the admin-users Edge Function end a user's sessions.
--
-- WHY. After a password reset or a deactivation, admin-users called
-- auth.admin.signOut(<user id>, 'global'). That supabase-js call takes the user's own JWT, not their
-- id (auth-js GoTrueAdminApi.signOut(jwt, scope): "jwt: A valid, logged-in JWT"), so the request
-- failed, the error was ignored, and the function itself never ended a session -- so a DEACTIVATED
-- user stayed signed in. GoTrue's admin API has no "sign out user by id", so this does it directly:
-- deleting a user's rows in auth.sessions ends them -- their refresh tokens go with them (FK cascade)
-- and their access tokens die at expiry, as with any sign-out.
--
-- WHO MAY CALL IT. Only service_role, i.e. only server code holding the service key (admin-users).
-- Revoked from public, anon and authenticated: no signed-in user can call it from the app.
--
-- Ending sessions matters for DEACTIVATION: tested on Demo (15 Oct 2026), a deactivated user's session
-- stayed alive. A password reset already ends every session in Supabase Auth itself; admin-users calls
-- this then too, as an explicit guarantee. p_keep_session (a session to spare) is optional and unused
-- by admin-users today -- Supabase ends the caller's own session on a password change regardless.
--
-- Returns how many sessions were ended. Touches nothing else.
-- ---------------------------------------------------------------------------
begin;

create or replace function public.admin_end_sessions(p_user uuid, p_keep_session uuid default null)
returns integer
language plpgsql
security definer
set search_path = auth, pg_temp
as $function$
declare
    v_ended integer;
begin
    if p_user is null then
        raise exception 'admin_end_sessions: no user given';
    end if;
    delete from auth.sessions
     where user_id = p_user
       and (p_keep_session is null or id <> p_keep_session);
    get diagnostics v_ended = row_count;
    return v_ended;
end;
$function$;

revoke all on function public.admin_end_sessions(uuid, uuid) from public, anon, authenticated;
grant execute on function public.admin_end_sessions(uuid, uuid) to service_role;

comment on function public.admin_end_sessions(uuid, uuid) is
    '0060. Ends a user''s sessions (all, or all but p_keep_session). service_role only: called by the admin-users Edge Function after a password reset or a deactivation.';

commit;

-- Check: only service_role (and the owner) may execute it.
select has_function_privilege('anon', 'public.admin_end_sessions(uuid, uuid)', 'execute')          as anon_can_call,
       has_function_privilege('authenticated', 'public.admin_end_sessions(uuid, uuid)', 'execute') as signed_in_can_call,
       has_function_privilege('service_role', 'public.admin_end_sessions(uuid, uuid)', 'execute')  as service_role_can_call;
