# Setting up the new client's Supabase project

For whoever has dashboard access to project **`obgclifsluxrgpluaqxs`**.

Everything below is done in the browser — the SQL Editor and the Authentication
page. No terminal, no passwords to share, about ten minutes.

The project is currently **empty**: no tables, no data. Nothing here can affect
any other project.

---

## Before you start

Open <https://supabase.com/dashboard/project/obgclifsluxrgpluaqxs> and check the
project name at the top is the right one. Every step below happens in **this**
project and no other.

---

## Step 1 · Build the structure

Creates 17 tables, 7 views, 29 functions, 20 triggers and 34 security policies.
No data.

1. Left sidebar → **SQL Editor** → **New query**
2. Open `schema.sql` from the project folder, select all, copy
3. Paste into the editor
4. Click **Run**

Takes 10–20 seconds. Expect `Success. No rows returned.`

> If a warning appears saying *"This query creates a table without enabling Row
> Level Security"*, choose **Run without RLS**. The script enables RLS itself on
> all 17 tables — the warning is the editor reading ahead, not a real problem.

---

## Step 1b · Apply the two later migrations

`schema.sql` was dumped before these landed, and Step 2 will fail without them:
the seed writes a column that 0035 creates.

1. **New query** → paste `0034_add_size.sql` → **Run**
2. **New query** → paste `0035_retire_sizes.sql` → **Run**

In that order — 0035 uses the `sieve_key` function 0034 defines. Both are safe on
an empty database: their clean-up sections find nothing and do nothing.

0034 lets the office add a sieve size from the import screen instead of waiting for
a developer. 0035 adds the `active` flag, and refuses new stock, sales, parcels and
prices against a retired size at the database — not just in the desktop app.

---

## Step 2 · Add the grades and settings

Adds 27 grades, 7 sieve sizes (one of them retired), 164 grade/size pairings, 2 currencies and 10
settings. Still no customer data — no buyers, no prices, no invoices.

1. Open `supabase/new_project_seed.sql`
2. **Near the top, just under `begin;`, there is a marked block with one line:**

   ```sql
   select set_config('app.client_name', 'REPLACE_WITH_CLIENT_NAME', true);
   ```

   Put the client's company name between those quotes. It is the only edit the
   file needs, and it appears on their documents and in the app header.
3. Select all, copy, paste into a **New query**, click **Run**

The script **refuses to finish** while that placeholder is still there, and
because it runs as one transaction, a refusal leaves the database untouched.
If it complains, set the name and run it again.

Expect a small table at the end reading:

| thing | count |
|-----------|-------|
| sizes | 9 |
| grades | 27 |
| pairings | 218 |
| currency | 2 |
| settings | 10 |

---

## Step 2b · Apply everything that landed after the snapshot

`schema.sql` was dumped on 19 Aug 2026. Ten migrations have landed since, and
without them the app is missing whole features — most visibly stock reservation,
where the desktop app calls `reserve_line` on every line typed and gets
*"function does not exist"* back on each keystroke.

1. **New query** → paste `0036_to_0052_since_schema.sql` → **Run**

One paste, ten sections, each committing itself. Takes 10–20 seconds.

**After Step 2 and not before.** Section 4 adds the "Unknown Grade" catalogue
entry, so running this first would make Step 2's own summary read 28 grades
where it says 27 — and look like a failure when it is not. If you run it too
early it refuses and names the step you have missed, rather than half-applying.

It ends with its own check. **Every row must read `ok`**, followed by a small
table showing **28 grades** and **0 reservations held**. `verify_new_project.sql`
in Step 3 cannot check any of this — it pins the schema as it stood before these
ten and passes whether or not they are here — so this is the only place they are
confirmed. Send that back with the Step 3 result.

---

## Step 3 · Check it

1. Open `supabase/verify_new_project.sql`
2. Paste into a **New query**, click **Run**

**Every row must read `ok`.** The script checks that all 17 tables, 7 views and
19 functions exist, that RLS is switched on everywhere, that the catalogue is
complete, and that no data from another client came across.

**Send the result back.** If anything reads other than `ok`, send that too and
stop — it names exactly what is missing.

---

## Step 4 · Create the first login

The app cannot create this one itself: it only lets an existing owner create
accounts, and there is no owner yet. So the first must be made by hand.

1. Left sidebar → **Authentication** → **Users** → **Add user** → *Create new user*
2. Enter the client's email and a password of **at least 10 characters**
3. **Tick "Auto Confirm User"** — without it the account cannot sign in
4. Click Create

Then open `supabase/create_owner.sql`, edit the two marked values in **Option A**,
and run it in the SQL Editor. That file also carries an all-SQL alternative if
the Auth page is not an option, with a warning about why it is second choice.

Expect one row: the email, role `owner`, active true, identities 1, confirmed true.

---

## Step 5 · Send back

- What **Step 3** printed
- What the **check query in Step 4** printed
- The **email and password** you set, so the app can be tested against it

That is everything. The rest — pointing the app at this project, building the
installer, testing — happens outside the dashboard.

---

## Optional, and can wait

**The `admin-users` function.** Without it the app works fully, except an owner
cannot create staff logins from inside the app — new users have to be added
through the dashboard as in Step 4. Worth deploying before handover, but it
blocks nothing today.

**The plan.** This project is on Free. Supabase pauses Free projects after seven
days of low activity, and Free projects have no downloadable backups. For a
client running their business on this, Pro is worth the cost before handover.
