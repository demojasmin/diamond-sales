-- ---------------------------------------------------------------------------
-- SEC-001 · make max_login_attempts real.
--
-- The Settings page has always written app_config.max_login_attempts and nothing
-- has ever read it. The only lockout code in the repo is DiamondApi/Auth.cs,
-- which (a) belongs to an ASP.NET service the desktop never calls, and (b) reads
-- a DIFFERENT key -- 'lockout_attempts' -- so even there the configured number
-- could not reach the code enforcing it.
--
-- Counting in the client is not an option: it resets when the app restarts, so
-- three failures, close, three more failures is unlimited attempts. The count
-- belongs where it cannot be reset by the person being counted.
--
-- Scope, stated plainly: this locks out the APP. Supabase's own auth endpoint is
-- still reachable by anything holding the anon key, so this is a policy control
-- for a desk, not a defence against an attacker with the key. Real rate limiting
-- is the Auth provider's job and is configured there. What this DOES fix is the
-- setting lying about being in force.
-- ---------------------------------------------------------------------------

create table if not exists public.login_attempt (
    email         text primary key,
    fails         int         not null default 0,
    last_fail_at  timestamptz,
    locked_until  timestamptz
);

alter table public.login_attempt enable row level security;

-- No policies, and none by design: every path below is SECURITY DEFINER, so the
-- table is reachable only through the two functions. A signed-out client must be
-- able to ask "am I locked?" without being able to read or clear anyone's count.
revoke all on public.login_attempt from anon, authenticated;

-- How long a locked account stays locked. A permanent lock needs an admin to
-- clear it and turns a fat-fingered password into a support call.
create or replace function public.lockout_minutes()
returns int
language sql
stable
set search_path = public
as $$
    select coalesce(
        (select nullif(value, '')::int from public.app_config where key = 'lockout_minutes'),
        15);
$$;

create or replace function public.max_login_attempts()
returns int
language sql
stable
set search_path = public
as $$
    select greatest(1, least(100, coalesce(
        (select nullif(value, '')::int from public.app_config where key = 'max_login_attempts'),
        -- The pre-rename key, so a database seeded by DiamondApi/Seed.cs still works.
        (select nullif(value, '')::int from public.app_config where key = 'lockout_attempts'),
        5)));
$$;

comment on function public.max_login_attempts() is
    'Failed sign-ins allowed before lockout. Reads max_login_attempts, falling back to the older lockout_attempts key, then 5.';

-- Asked BEFORE the password is sent. Returns the seconds remaining, or 0.
create or replace function public.login_locked_for(p_email text)
returns int
language sql
stable
security definer
set search_path = public
as $$
    select coalesce(
        (select greatest(0, ceil(extract(epoch from (locked_until - now())))::int)
           from public.login_attempt
          where email = lower(trim(p_email))
            and locked_until is not null
            and locked_until > now()),
        0);
$$;

-- Called after a refused sign-in. Returns the seconds the account is now locked
-- for, so the client can say the same thing whether this failure was the last
-- allowed one or not.
create or replace function public.note_login_failure(p_email text)
returns int
language plpgsql
security definer
set search_path = public
as $$
declare
    v_email text := lower(trim(p_email));
    v_max   int  := public.max_login_attempts();
    v_fails int;
begin
    insert into public.login_attempt (email, fails, last_fail_at)
         values (v_email, 1, now())
    on conflict (email) do update
            -- A lapsed lock starts the count again rather than leaving the
            -- account one failure from a re-lock for ever.
            set fails = case when public.login_attempt.locked_until is not null
                              and public.login_attempt.locked_until <= now()
                             then 1
                             else public.login_attempt.fails + 1 end,
                last_fail_at = now(),
                locked_until = null
      returning fails into v_fails;

    if v_fails >= v_max then
        update public.login_attempt
           set locked_until = now() + make_interval(mins => public.lockout_minutes())
         where email = v_email;
    end if;

    return public.login_locked_for(v_email);
end;
$$;

-- Called after a successful sign-in. The slate is clean again.
create or replace function public.clear_login_failures(p_email text)
returns void
language sql
security definer
set search_path = public
as $$
    delete from public.login_attempt where email = lower(trim(p_email));
$$;

-- anon as well as authenticated: all three are called while signed OUT, which is
-- the only time they matter.
grant execute on function public.login_locked_for(text)     to anon, authenticated;
grant execute on function public.note_login_failure(text)   to anon, authenticated;
grant execute on function public.clear_login_failures(text) to anon, authenticated;
grant execute on function public.max_login_attempts()       to anon, authenticated;
grant execute on function public.lockout_minutes()          to anon, authenticated;

-- How long a lock lasts, alongside the count it belongs with, so the Settings
-- page shows both halves of the policy instead of one.
insert into public.app_config (key, value)
     values ('lockout_minutes', '15')
on conflict (key) do nothing;
