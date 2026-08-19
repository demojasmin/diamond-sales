-- ---------------------------------------------------------------------------
-- STEP 4 · The first login.
--
-- Run this LAST, after the structure, the catalogue and the verification.
--
-- WHY IT HAS TO BE DONE BY HAND
--
-- The app creates staff accounts through the admin-users function, and that
-- function refuses anyone who is not already an owner. On a new project nobody
-- is. So the first account cannot come from the app -- there is no way in until
-- it exists.
--
-- TWO WAYS. Read both before choosing.
-- ---------------------------------------------------------------------------


-- ═══════════════════════════════════════════════════════════════════════════
--  ►►►  THE THREE LINES TO EDIT  ◄◄◄
-- ═══════════════════════════════════════════════════════════════════════════
--
--      email     the client signs in with this
--      password  at least 10 characters -- the app refuses shorter ones
--      name      shown in the app and against everything they record
--
-- ═══════════════════════════════════════════════════════════════════════════


-- ---------------------------------------------------------------------------
-- OPTION A · RECOMMENDED. Two clicks, then this query.
--
-- First, in the dashboard:
--
--     Authentication -> Users -> Add user -> Create new user
--     - the email and password
--     - TICK "Auto Confirm User"        <- without this they cannot sign in
--
-- Then edit the two values below and run this. That is all Option A needs.
-- ---------------------------------------------------------------------------

update public.profiles p
   set role      = 'owner',
       active    = true,
       full_name = 'FULL NAME HERE'                    -- <= edit
  from auth.users u
 where u.id = p.id
   and u.email = lower('EMAIL@EXAMPLE.COM');           -- <= edit

-- The profile row already exists: this project has a trigger that creates one
-- with every new login. That is why this is an UPDATE and not an INSERT.


-- ---------------------------------------------------------------------------
-- OPTION B · Everything in SQL, no Auth page.
--
-- Use this only if the Auth page is not an option. It writes directly into
-- Supabase's own auth tables, whose shape has changed between versions -- so it
-- can succeed and still leave an account that CANNOT SIGN IN, and every SQL
-- check below will still look perfect.
--
-- If you use it, the sign-in must be tested before the client is given the app.
-- If it fails: delete the user in the dashboard and use Option A instead.
--
-- Edit the three values at the top of the block, then run the whole block.
-- ---------------------------------------------------------------------------

/*  -- remove this line and the one at the foot to enable Option B

do $$
declare
    v_email    text := 'EMAIL@EXAMPLE.COM';        -- <= edit
    v_password text := 'CHANGE-THIS-PASSWORD';     -- <= edit, 10+ characters
    v_name     text := 'FULL NAME HERE';           -- <= edit
    v_id       uuid;
begin
    select id into v_id from auth.users where email = lower(v_email);

    if v_id is null then
        v_id := gen_random_uuid();

        insert into auth.users (
            instance_id, id, aud, role, email, encrypted_password,
            email_confirmed_at, created_at, updated_at,
            raw_app_meta_data, raw_user_meta_data,
            confirmation_token, recovery_token, email_change_token_new, email_change
        ) values (
            '00000000-0000-0000-0000-000000000000', v_id,
            'authenticated', 'authenticated', lower(v_email),
            extensions.crypt(v_password, extensions.gen_salt('bf')),
            now(), now(), now(),
            '{"provider":"email","providers":["email"]}'::jsonb,
            jsonb_build_object('full_name', v_name),
            '', '', '', ''
        );

        -- Without this row the account exists and cannot sign in. It is the
        -- part that most often differs between Supabase versions.
        insert into auth.identities (
            id, provider_id, user_id, identity_data, provider,
            last_sign_in_at, created_at, updated_at
        ) values (
            gen_random_uuid(), v_id::text, v_id,
            jsonb_build_object('sub', v_id::text, 'email', lower(v_email),
                               'email_verified', true, 'phone_verified', false),
            'email', now(), now(), now()
        );
    end if;

    -- The trigger may already have made the profile. Cover both cases.
    update public.profiles
       set role = 'owner', active = true, full_name = v_name
     where id = v_id;

    if not found then
        insert into public.profiles (id, full_name, role, active)
        values (v_id, v_name, 'owner', true);
    end if;
end $$;

*/  -- remove this line too


-- ---------------------------------------------------------------------------
-- CHECK · run after either option.
--
-- Expect ONE row: the email, role 'owner', active true, identities 1.
--
-- This proves the rows are right. It does NOT prove the password works -- only
-- an actual sign-in does that, which is the next thing to do.
-- ---------------------------------------------------------------------------

select u.email,
       p.role,
       p.active,
       p.full_name,
       (select count(*) from auth.identities i where i.user_id = u.id) as identities,
       (u.email_confirmed_at is not null)                              as confirmed
  from public.profiles p
  join auth.users u on u.id = p.id
 order by u.email;
