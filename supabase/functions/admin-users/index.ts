// ---------------------------------------------------------------------------
// admin-users · the only place the service_role key is allowed to exist.
//
// Creating a login, resetting a password and changing a role are all admin-API
// operations. They need the service_role key, which bypasses RLS entirely --
// anyone holding it can read and rewrite the whole database. It therefore
// cannot ship inside the desktop app: an APK or an .exe is not a secret, and a
// key in a config file is a key in everyone's hands.
//
// So the desktop sends the SIGNED-IN USER'S token to this function, and the
// service_role key never leaves Supabase's own environment. Deno.env holds it;
// nothing returns it; no response echoes it.
//
// Authorisation is checked HERE, not in the caller. The desktop disables the
// buttons for non-owners, which stops an honest mistake -- it is not a security
// boundary, because anyone can call this URL directly with any token.
// ---------------------------------------------------------------------------
// An exact version, not "@2": a floating tag would pull whatever supabase-js released last on the
// next deploy, into the one function that holds the service_role key.
import { createClient } from "https://esm.sh/@supabase/supabase-js@2.47.10";

const SUPABASE_URL = Deno.env.get("SUPABASE_URL")!;
const ANON_KEY = Deno.env.get("SUPABASE_ANON_KEY")!;
const SERVICE_ROLE_KEY = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;

// Matches the check constraint on public.profiles. Kept as a literal rather than
// read from the database so an unexpected value is refused here, before it can
// reach a table that would only refuse it with a constraint error.
const ROLES = ["sales", "manager", "owner"] as const;
type Role = typeof ROLES[number];

const cors = {
    "Access-Control-Allow-Origin": "*",
    "Access-Control-Allow-Headers": "authorization, content-type",
    "Access-Control-Allow-Methods": "POST, OPTIONS",
};

const json = (body: unknown, status = 200) =>
    new Response(JSON.stringify(body), {
        status,
        headers: { ...cors, "Content-Type": "application/json" },
    });

const fail = (status: number, code: string, message: string) =>
    json({ ok: false, code, message }, status);

// A database error is logged here, in Supabase's function logs, and the caller gets a plain
// sentence: the raw text names tables, columns and constraints, which no screen needs to show.
const dbFail = (code: string, message: string, detail: unknown) => {
    console.error(`admin-users ${code}:`, detail);
    return fail(400, code, message);
};

Deno.serve(async (req) => {
    if (req.method === "OPTIONS") return new Response("ok", { headers: cors });
    if (req.method !== "POST") return fail(405, "METHOD_NOT_ALLOWED", "Use POST.");

    // ── who is asking ──────────────────────────────────────────────────────
    const authHeader = req.headers.get("Authorization") ?? "";
    if (!authHeader.startsWith("Bearer ")) {
        return fail(401, "NO_TOKEN", "Sign in first.");
    }

    // Anon client + the caller's token: this resolves the token the same way any
    // other request would, so a forged or expired one simply yields no user.
    const asCaller = createClient(SUPABASE_URL, ANON_KEY, {
        global: { headers: { Authorization: authHeader } },
    });

    const { data: auth, error: authError } = await asCaller.auth.getUser();
    if (authError || !auth?.user) return fail(401, "BAD_TOKEN", "Sign in again.");
    const callerId = auth.user.id;

    // service_role from here on. Never returned, never logged.
    const admin = createClient(SUPABASE_URL, SERVICE_ROLE_KEY, {
        auth: { autoRefreshToken: false, persistSession: false },
    });

    // The caller's role is read with service_role, not through their own client:
    // an RLS policy that hid a row would otherwise read as "not an owner", and a
    // permission check that fails open on a policy change is worth avoiding.
    const { data: caller } = await admin
        .from("profiles").select("role, active, full_name").eq("id", callerId).single();

    if (!caller?.active) return fail(403, "INACTIVE", "This account is deactivated.");
    if (caller.role !== "owner") {
        return fail(403, "NOT_OWNER", "Only an owner may manage user accounts.");
    }

    let body: Record<string, unknown>;
    try { body = await req.json(); }
    catch { return fail(400, "BAD_JSON", "Malformed request."); }

    const action = String(body.action ?? "");
    const targetId = body.user_id ? String(body.user_id) : null;

    // ── audit · written for every action that changes anything ─────────────
    // record_uuid, because profiles.id is a uuid and audit_log.record_id is a
    // bigint (0021 adds the column). The specific operation travels in
    // new_values so the existing INSERT/UPDATE/DELETE check constraint stands.
    //
    // The change has already happened when this runs, so a failed audit write cannot undo it -- but
    // it must not pass silently either. It is logged, and the response says so (audit: "failed"),
    // so an account change with no audit row is visible rather than merely missing.
    const writeAudit = async (
        dbAction: "INSERT" | "UPDATE",
        detail: Record<string, unknown>,
        before: Record<string, unknown> | null = null,
        recordId: string | null = targetId,
    ): Promise<"ok" | "failed"> => {
        const { error } = await admin.from("audit_log").insert({
            table_name: "profiles",
            record_uuid: recordId,
            action: dbAction,
            changed_by: callerId,
            old_values: before,
            new_values: { admin_action: action, ...detail },
        });
        if (error) {
            console.error("admin-users AUDIT_FAILED:", action, recordId, error);
            return "failed";
        }
        return "ok";
    };

    // Ends a user's sessions through 0060's admin_end_sessions (service_role only). This replaced
    // auth.admin.signOut(<user id>), which takes the user's own JWT, not an id -- it failed every
    // time and the failure was ignored, so no session was ever ended. A failure here is logged and
    // reported in the response (sessions: "failed"); the change itself has already been made.
    const endSessions = async (userId: string): Promise<"ended" | "failed"> => {
        const { error } = await admin.rpc("admin_end_sessions", { p_user: userId });
        if (error) {
            console.error("admin-users END_SESSIONS_FAILED:", userId, error);
            return "failed";
        }
        return "ended";
    };

    // A Supabase Auth sign-in ban, applied on deactivation and lifted on reactivation. "876000h" is
    // a hundred years -- Auth has no "forever", and the ban is lifted explicitly, not by expiry. A
    // failure is logged and reported (ban: "failed"); the profile change has already been made.
    const setBan = async (userId: string, banned: boolean): Promise<"applied" | "lifted" | "failed"> => {
        const { error } = await admin.auth.admin.updateUserById(userId, { ban_duration: banned ? "876000h" : "none" });
        if (error) {
            console.error("admin-users BAN_FAILED:", userId, banned, error);
            return "failed";
        }
        return banned ? "applied" : "lifted";
    };

    // A password must have ten characters that are not spaces at either end: ten spaces is not one.
    const weakPassword = (password: string) => password.trim().length < 10;

    // Guards shared by every action that targets an existing account.
    const loadTarget = async () => {
        if (!targetId) return { error: fail(400, "NO_USER", "No account named.") };
        const { data } = await admin
            .from("profiles").select("id, role, active, full_name").eq("id", targetId).single();
        if (!data) return { error: fail(404, "NO_SUCH_USER", "No such account.") };
        return { target: data };
    };

    // An owner locking themselves out, or the last owner being removed, leaves
    // nobody who can undo it -- the one failure this endpoint cannot recover from.
    //
    // Only an ACTIVE owner counts. Demoting or deactivating an owner who is already deactivated
    // removes no one who can sign in, so it must not be refused as "the only active owner" -- which
    // it was, whenever exactly one other owner was active (Priya: one active, one inactive).
    const lastOwnerCheck = async (target: { id: string; role: string; active: boolean }) => {
        if (target.role !== "owner" || !target.active) return null;
        const { count } = await admin
            .from("profiles").select("id", { count: "exact", head: true })
            .eq("role", "owner").eq("active", true);
        if ((count ?? 0) <= 1) {
            return fail(409, "LAST_OWNER", "This is the only active owner. Appoint another first.");
        }
        return null;
    };

    switch (action) {
        // ── create ─────────────────────────────────────────────────────────
        case "create": {
            const email = String(body.email ?? "").trim().toLowerCase();
            const fullName = String(body.full_name ?? "").trim();
            const role = String(body.role ?? "sales") as Role;
            const password = String(body.password ?? "");

            if (!/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(email)) {
                return fail(400, "BAD_EMAIL", "That is not a valid email address.");
            }
            if (fullName.length < 2 || fullName.length > 120) {
                return fail(400, "BAD_NAME", "Full name must be between 2 and 120 characters.");
            }
            if (!ROLES.includes(role)) {
                return fail(400, "BAD_ROLE", `Role must be one of ${ROLES.join(", ")}.`);
            }
            // Supabase's own floor is 6; 10 is this app's, and it is cheaper to
            // refuse here than to explain a rejected signup.
            if (weakPassword(password)) {
                return fail(400, "WEAK_PASSWORD", "Password must be at least 10 characters, not counting spaces at either end.");
            }

            const { data: created, error: createError } = await admin.auth.admin.createUser({
                email,
                password,
                email_confirm: true,
                user_metadata: { full_name: fullName },
            });
            if (createError || !created?.user) {
                return fail(400, "CREATE_FAILED", createError?.message ?? "Could not create the login.");
            }

            // A profile row may already exist if a trigger creates one on signup;
            // upsert so this works either way rather than assuming.
            const { error: profileError } = await admin.from("profiles").upsert({
                id: created.user.id, full_name: fullName, role, active: true,
            });
            if (profileError) {
                // The login exists but has no usable profile -- remove it rather
                // than leave an account nobody can sign in with or find.
                await admin.auth.admin.deleteUser(created.user.id);
                return dbFail("PROFILE_FAILED", "The login could not be set up. Nothing was created.", profileError);
            }

            const audit = await writeAudit("INSERT", { email, full_name: fullName, role }, null, created.user.id);
            return json({ ok: true, user_id: created.user.id, email, role, audit });
        }

        // ── activate / deactivate ──────────────────────────────────────────
        case "activate":
        case "deactivate": {
            const { target, error } = await loadTarget();
            if (error) return error;

            const active = action === "activate";
            if (!active) {
                if (target!.id === callerId) {
                    return fail(409, "SELF", "You cannot deactivate your own account.");
                }
                const blocked = await lastOwnerCheck(target!);
                if (blocked) return blocked;
            }
            if (target!.active === active) {
                // Already in that state. Deactivating again still (re)applies the sign-in ban and
                // ends sessions, so an account switched off before the ban existed can be closed
                // properly without first reactivating it. Reactivating again changes nothing.
                if (active) return json({ ok: true, unchanged: true, active });
                const ban = await setBan(target!.id, true);
                const sessions = await endSessions(target!.id);
                return json({ ok: true, unchanged: true, active, ban, sessions });
            }

            const { error: updateError } = await admin
                .from("profiles").update({ active }).eq("id", target!.id);
            if (updateError) return dbFail("UPDATE_FAILED", "The account could not be changed.", updateError);

            // Deactivating closes the account at the door as well as at the data: a Supabase sign-in
            // ban (so no new session can be started, from the app or by calling Supabase directly)
            // and the sessions it already holds ended. Data access already stopped -- every RLS
            // check requires an active profile. Reactivating lifts the ban.
            const ban = await setBan(target!.id, !active);
            const sessions = active ? undefined : await endSessions(target!.id);

            const audit = await writeAudit("UPDATE", { active }, { active: target!.active });
            return json({ ok: true, active, audit, ban, ...(sessions ? { sessions } : {}) });
        }

        // ── change role ────────────────────────────────────────────────────
        case "change_role": {
            const role = String(body.role ?? "") as Role;
            if (!ROLES.includes(role)) {
                return fail(400, "BAD_ROLE", `Role must be one of ${ROLES.join(", ")}.`);
            }
            const { target, error } = await loadTarget();
            if (error) return error;

            if (target!.id === callerId && role !== "owner") {
                return fail(409, "SELF", "You cannot remove your own owner role.");
            }
            if (role !== "owner") {
                const blocked = await lastOwnerCheck(target!);
                if (blocked) return blocked;
            }
            if (target!.role === role) return json({ ok: true, unchanged: true, role });

            const { error: updateError } = await admin
                .from("profiles").update({ role }).eq("id", target!.id);
            if (updateError) return dbFail("UPDATE_FAILED", "The role could not be changed.", updateError);

            const audit = await writeAudit("UPDATE", { role }, { role: target!.role });
            return json({ ok: true, role, audit });
        }

        // ── reset password ─────────────────────────────────────────────────
        case "reset_password": {
            const password = String(body.password ?? "");
            // A blank (or short) password changes nothing: refused before anything is touched.
            if (weakPassword(password)) {
                return fail(400, "WEAK_PASSWORD", "Password must be at least 10 characters, not counting spaces at either end.");
            }
            const { target, error } = await loadTarget();
            if (error) return error;

            const { error: resetError } = await admin.auth.admin
                .updateUserById(target!.id, { password });
            if (resetError) return fail(400, "RESET_FAILED", resetError.message);

            // Every existing session dies with the old password, so a leaked token cannot outlive
            // the reset that was meant to stop it. Supabase Auth already ends them all when a password
            // changes (tested on Demo, 15 Oct 2026: refresh_token_not_found) -- the caller's own too,
            // so an owner resetting their own password signs in again everywhere. This is the second,
            // explicit guarantee, independent of that behaviour.
            const sessions = await endSessions(target!.id);

            // The password itself is never written to the audit, only the fact.
            const audit = await writeAudit("UPDATE", { password_reset: true });
            return json({ ok: true, audit, sessions });
        }

        default:
            return fail(400, "BAD_ACTION",
                "Action must be one of create, activate, deactivate, change_role, reset_password.");
    }
});
