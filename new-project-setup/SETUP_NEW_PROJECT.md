# Setting up a new client's Supabase project

How to build a brand-new database for Solitaire Desk, matching the code as of **21 Nov 2026**
(migrations up to **0063**).

Everything is done in the Supabase dashboard (SQL Editor, Authentication, Edge Functions) and in
`DiamondDesktop/appsettings.json`. No terminal, about 20 minutes.

> **Only on a NEW, EMPTY project.** Every SQL file below refuses to run on a database that already
> has the later migrations, and says so. To bring an **existing** database up to date, run
> `supabase/WHICH_MIGRATIONS_ARE_APPLIED.sql` there and apply only the missing files from
> `supabase/migrations/`, in number order.

---

## The files, in the order they run

| # | File (in `new-project-setup/`) | What it does |
|---|---|---|
| 1 | `schema.sql` | Structure snapshot of 19 Aug 2026: tables, views, functions, triggers, security policies. No data |
| 2 | `0034_add_size.sql`, `0035_retire_sizes.sql` | Two migrations that came after the snapshot |
| 3 | `new_project_seed.sql` | Grades, sieve sizes, pairings, currencies, settings, **and the client's name** |
| 4 | `0036_to_0053_since_schema.sql` | Migrations 0036–0053: stock reservation, importers, reconciliation, one grade per spelling |
| 5 | **`0054_to_0063_since_0053.sql`** | Migrations 0054–0063, plus the client's grade order (see Step 5) |
| 6 | `verify_new_project.sql` | Checks the whole thing |
| 7 | `create_owner.sql` | Makes the first login an owner |

`build_since_0053.py` regenerates file 5 from `supabase/migrations/`. Run it again whenever one of
those migrations changes; never edit the bundle by hand.

---

## Step 0 · Create the project

1. <https://supabase.com/dashboard> → **New project**. Pick the client's organisation, a strong
   database password (keep it safe), and the region nearest the client (Mumbai for India).
2. When it is ready, write down, from **Project Settings → API**:
   - the **Project URL** (`https://<ref>.supabase.co`)
   - the **anon / public key**
3. **Plan.** Free projects **pause after 7 days of low activity** (the app then shows "No connection
   to the server") and have **no downloadable backups**. For a client trading on it, use **Pro**.

From here on, check the **project ref in the browser address bar** before every step. Two projects
can share a company name; the ref is what tells them apart.

---

## Step 1 · Build the structure

1. **SQL Editor → New query**, paste all of `schema.sql`, **Run**.
2. If the editor warns *"creates a table without enabling Row Level Security"*, choose **Run without
   RLS**. The script turns RLS on for every table itself.

Expect `Success. No rows returned.`

## Step 2 · The two migrations after the snapshot

1. **New query** → paste `0034_add_size.sql` → **Run**
2. **New query** → paste `0035_retire_sizes.sql` → **Run**

In that order: 0035 uses a function 0034 creates.

## Step 3 · Grades, sizes and settings

1. Open `new_project_seed.sql`. Just under `begin;` is one marked line:
   ```sql
   select set_config('app.client_name', 'REPLACE_WITH_CLIENT_NAME', true);
   ```
   Put the client's company name between the quotes. It appears on their documents and in the app.
2. Paste the whole file into a **New query** → **Run**.

It refuses to run while the placeholder is still there, and changes nothing when it refuses.
Expect a summary: **27 grades**, sizes, pairings, **2 currencies**, **10 settings**.

## Step 4 · Migrations 0036 to 0053

**New query** → paste `0036_to_0053_since_schema.sql` → **Run**.

After Step 3, not before. It ends with its own check: **every row `ok`**, then **28 grades** (it
adds "Unknown Grade") and **0 reservations held**.

## Step 5 · Migrations 0054 to 0063, and the grade order  *(new)*

**New query** → paste `0054_to_0063_since_0053.sql` → **Run**.

What it adds:

| | |
|---|---|
| 0054 | A sale can never take more than the bucket holds |
| 0055 | Only the **selection** leaves stock; a rejection is recorded, not deducted |
| 0056 | 1MB can be sold in −2 |
| 0057 | FL and # become part of **II** |
| 0058 | Invoice cost on selection; cancelling puts stones back at cost |
| 0059 | An adjustment cannot add stones for free |
| 0060 | Lets the `admin-users` function sign a user out (Step 8) |
| 0061 | **Security:** every privileged function checks the caller's role |
| 0063 | **Color** is its own grade again; "II Color" / "II Col" count as **II** |
| Grade order | The client's paper order: PREMIUM … MIX are 1–22, everything else 100+. The Stock report shows only 1–22 |

The last table it prints has **15 rows; every one must read `ok`**. After this the catalogue holds
**27 grades**: 28, less FL (merged into II), with Color kept.

## Step 6 · Check everything

**New query** → paste `verify_new_project.sql` → **Run**. Every row must read `ok`. It confirms the
tables, views and functions exist, RLS is on everywhere, every grade can be sold in some sieve, and
**no data from another client** came across.

---

## Step 7 · Sign-in security (Authentication settings)

1. **Authentication → Sign In / Providers → Email**: turn **"Allow new users to sign up" OFF**.
   Accounts are created only by an owner (Step 8) or the dashboard. With sign-up on, anyone holding
   the app's public key could create an account.
2. **Authentication → Passwords** (Pro plan): turn on **leaked-password protection**.
3. Leave **"Confirm email"** as it is. Owners create users already confirmed.

## Step 8 · The first owner

The app can only create logins for an existing owner, so the first one is made by hand.

1. **Authentication → Users → Add user → Create new user.** Client's email, a password of **at least
   10 characters**, tick **Auto Confirm User**.
2. Open `create_owner.sql`, put that email in the **two marked places in Option A**, run it.

Expect one row: the email, role **owner**, active **true**, confirmed **true**.

## Step 9 · The `admin-users` Edge Function (creating and managing logins in the app)

Without it the app works, except **Users** cannot create logins, reset passwords or deactivate
anyone.

1. **Edge Functions → Deploy a new function → Via editor**, name it exactly **`admin-users`**.
2. Paste the contents of `supabase/functions/admin-users/index.ts`, then **Deploy**.
3. Leave **Verify JWT** on. Nothing else to set: Supabase provides the URL, anon key and service key
   to the function itself. The service key never leaves Supabase.
4. Check: sign in to the app as the owner → **Users** → create a test user, then deactivate it.

---

## Step 10 · Point the app at the project

1. In `DiamondDesktop/appsettings.json`, add one entry to `Workspaces`, using the values from Step 0:
   ```json
   { "Name": "<Client name>", "Url": "https://<ref>.supabase.co", "AnonKey": "<anon public key>" }
   ```
   The anon key is meant to be public: row-level security and the role checks (0061) protect the
   data, not the key. **Never** put the service-role key or the database password in this file.
2. Rebuild the installer and install it on the client's PC.
3. Sign in with the owner from Step 8. The header should show the client's workspace name.

## Step 11 · The client's own data (in the app, not SQL)

These come from the client's files through the app, so nothing from another client is copied:

- **Buyers and brokers:** Master data, or created by the sales importer.
- **Opening stock:** Intake & movements → import the stock workbook or printed sheet (as at the count date).
- **Past sales** (optional): the sales importer.
- **Stock-sheet rates and sizes** for the Stock report valuation: written automatically when the
  client's **printed stock sheet (PDF)** is imported; they are not edited by hand. Color gets rates
  only if the sheet prints a Color row.

---

## Send back when done

- The final tables printed by **Step 4, Step 5 and Step 6** (every row `ok`)
- The **row from Step 8**
- The **project ref** and that sign-up is **off** (Step 7)
- Whether `admin-users` deployed (Step 9)

## If something fails

| Message | Meaning |
|---|---|
| `Stop: … has not been run. Do Step N first.` | A step was skipped. Do that step, then rerun this one |
| `Stop: this database already has …` | Not a new project. You are in the wrong one: check the ref |
| `REPLACE_WITH_CLIENT_NAME` refusal | Set the client's name in the seed (Step 3) |
| App says "No connection to the server" | Free project paused (Step 0), or the URL in `appsettings.json` is wrong |
| App says "Wrong email or password" for the owner | Step 8 not run, or the user wasn't auto-confirmed |
