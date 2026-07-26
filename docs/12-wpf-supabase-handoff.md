# 12 · WPF ↔ Supabase — where this project actually stands

Handoff for whoever picks this up next. Written at the end of the session that moved the desktop app
off its own backend and onto Supabase. **Read §4 before trusting anything** — it separates what is
verified from what is merely written.

---

## 1 · What this is

Replacing a two-workbook Excel process with:

| Piece | State |
|---|---|
| **Supabase Postgres** | live, `nzcvjaixgqoliyrotstz`, schema + RLS + calc views deployed |
| **WPF desktop** (`DiamondDesktop`) | the write side — sales entry, stock, masters. **Now on Supabase.** |
| **Android owner app** | read-only dashboards. Built by another team, already connected. |
| `DiamondApi` (ASP.NET + SQLite) | **retired.** Superseded by Supabase. Still in the repo, no longer referenced. |
| `DiamondCalc` | kept — but see §3, its role changed. |

### The golden rule

> **Postgres decides what a number means. Clients only group and sum.**

Line amount, rejection, outstanding, blended rate, due date, stock balance, weighted-average cost and
broker payable are all computed in SQL views. Neither client redefines them. `DiamondCalc` is allowed
to compute figures **for live feedback while the user types**, and those values are **never persisted** —
the server recomputes from raw inputs on save. If the desktop and the phone ever disagree on an invoice
total, something has broken this rule.

---

## 2 · Connection

```
Url:  https://nzcvjaixgqoliyrotstz.supabase.co
Key:  sb_publishable_bkIjJlfcQZDrXD6-l7i1uQ_v6OLf9Un   (publishable — ships in both binaries by design)
```

The anon key is **not a secret**; RLS is the security boundary. Never put `service_role` in the
desktop app, never use a direct `Npgsql` connection string.

Credentials are **not** in source. The login screen reads `SOLITAIRE_EMAIL` / `SOLITAIRE_PASSWORD`
from the environment for dev convenience, otherwise you type them. A successful session is persisted
DPAPI-encrypted at `%LOCALAPPDATA%/SolitaireDesk/session.dat` and auto-refreshes.

---

## 3 · What changed this session

### 3a · Database — four new migrations, all applied

In `supabase/migrations/`, run in order after `0001`–`0006`:

| File | Why |
|---|---|
| `0007_client_ref.sql` | SYNC-001 idempotency keys on `sales_invoice`, `receipt`, `stock_movement`. Partial unique indexes, so existing rows are untouched. |
| `0008_adjust_is_signed.sql` | **Unblocks `cancel_invoice`.** See below. |
| `0009_align_with_spec.sql` | `invoice_no` nullable; `stock_movement.reason` added. |
| `0010_write_rpcs.sql` | `next_invoice_no`, `post_invoice`, `cancel_invoice`, `convert_stock`. |

**Why 0008 exists.** `0001` had `check (weight_ct >= 0)` and `0002` put `ADJUST` in the negative branch
of `v_stock_position`. So an ADJUST row could only ever *reduce* stock — but cancelling a posted invoice
has to give carats back, and the schema's own comment says to "correct a mistake with an offsetting
ADJUST row". An offsetting row cannot offset in one direction only. FR-INV-2 specifies the ledger with
**"weights signed"**, and INV-6 requires a cancelled invoice's movements to *sum to zero*; neither is
possible otherwise. ADJUST is now signed-as-stored; every other type keeps its positive-magnitude meaning.

**Why 0009 exists.** `0001` had `invoice_no NOT NULL UNIQUE`, but docs/03 §2.3 lists the opposite as a
decision worth defending: the number is assigned **at post**, not at create, so two offline clients
cannot both mint `INV-0042`. NOT NULL also burns a number on every abandoned draft. Now nullable, with
`CHECK (status <> 'POSTED' OR invoice_no IS NOT NULL)`.

**Why the RPCs exist.** PostgREST has no client-side transaction. Posting must set `POSTED` *and* insert
the SALE/REJECTION movements atomically, or a dropped connection leaves an invoice marked posted with no
stock deducted — invisible until month-end. All four functions are **SECURITY INVOKER on purpose**: a
DEFINER function would run as its owner and let any signed-in user post anyone's invoice.

`post_invoice` returns `jsonb` rather than raising on a soft failure, because CFG-003's `warn` policy is
a question for the user:

```
{ ok:true, invoice_no:"INV-2026-00001" }
{ ok:true, already_posted:true }                          -- replay of a lost response
{ ok:false, needs_override:true, shortfalls:[...] }       -- nothing written; ask, then retry with p_override
```

### 3b · Desktop — new data layer

`DiamondDesktop/Data/` (~1,100 lines):

| File | Contains |
|---|---|
| `Models.cs` | Supabase models for every table and view, plus `Movement`/`InvoiceStatus`/`PriceContext` constants |
| `Db.cs` | client, auth, DPAPI session persistence, `CurrentUser`/`IsOwner`/`IsOnline` |
| `Repo.cs` | every read and write. Reads come from views; writes return `null` or a human message |
| `Outbox.cs` | SYNC-001 offline queue (local SQLite), replays on reconnect, treats `23505` as success |

`Api.cs` deleted. `Catalogue`'s hardcoded 22 underscore-coded grades deleted — grades and sizes now
load from the `grade` / `size_bucket` tables.

### 3c · Earlier in the same session — UI and accessibility

Before the Supabase work, ten defects were found and fixed by measuring the running app (UIA bounds +
pixel sampling), not by eye:

- DatePicker was 34px in a row of 32px inputs (it had borrowed the *button* height)
- "Post" missed the card's right edge by 8px (trailing margin on the last button)
- Intake's four stacked forms didn't share a field grid — now WPF `SharedSizeGroup`
- Disposition dropdown rendered 20px vs the 32px standard
- Master data and Settings two-column layouts didn't top-align
- Master data overflowed and clipped "Set price"
- Dark-theme primary button was **3.2:1** white-on-accent — below WCAG AA. New `AccentStrongBrush`
  token; now 4.88:1. `AccentBrush` is lightened so accent *text* reads on dark, and that same lightness
  is what failed under white text — the two jobs need different colours.
- Status bar leaked a C# parameter name (`grossWeightCt is out of range`) instead of "Grade is required"
- The whole app was **missing from the UI Automation tree** — the custom nav-rail template's
  ContentPresenter wasn't named `PART_SelectedContentHost`, which is how `TabItemAutomationPeer` finds
  a tab's content. Screen readers saw the nav rail and nothing else.
- Theme toggle and every header control had no accessible name (announced as bare "button"/"combo box")

---

## 4 · Verified vs not — read this

### Verified against the live database

- **20/20** end-to-end checks on the RPCs (`scratchpad/verify.mjs` pattern): draft saves with NULL
  `invoice_no`, post assigns `INV-2026-00001`, re-post is idempotent, CALC-1 = 5078.70 (discounts
  compound, not add), CALC-2/3/4/11 all match, `v_reconciliation` clean, cancel demands a reason,
  cancelled movements net to zero (INV-6), negative ADJUST accepted.
- **The desktop app signs into Supabase and reads live data.** Top bar shows
  `Connected · 23 grades · 1 buyers`; Master data renders the real codes including `NO 2 BB`, the 23rd
  grade the old hardcoded seed never had.
- Build is green, 0 compiler warnings.

### NOT verified

- **The write path through the UI.** An invoice typed in the app reaching Supabase with a matching
  total is §9's real acceptance criterion and it has **not been demonstrated**. `Repo.SaveDraftAsync` /
  `PostAsync` are written and the underlying RPCs are proven by REST, but nobody has clicked
  Save → Post in the app and confirmed the row.
- **The 33 UI tests are broken.** They target the retired backend's login flow and buyer names.
- `SupabaseRoundTripTests.cs` exists and is the right test, but its **fixture cannot sign in**.
  The app signs in fine when driven by hand — the failure is in the fixture, not the product.
  Attempts so far: env vars via `Environment.SetEnvironmentVariable` (not inherited), typing the
  credentials with `Keyboard.Type`, and `ProcessStartInfo.Environment` with `UseShellExecute=false`.
  Next step is to print the login screen's `Status` text on failure — the app is almost certainly
  showing the answer on screen.

---

## 5 · Scope deliberately dropped

Removed rather than left broken, because Supabase has no equivalent:

| Gone | Why |
|---|---|
| Backup / restore | Supabase owns backups |
| Change requests | no table for them in this schema |
| **Add / deactivate user** | needs `service_role`, which must never ship in a trading-floor binary. Users tab is now **read-only**; accounts are managed in the Supabase dashboard. This is **AUTH-002 losing its write half** — needs an Edge Function if you want it back. |
| Rejection *dispositions* | `DispositionGrid` has no Supabase table. The rejection is recorded; the status bar says the dispositions were not saved. Flagged in code, not swallowed. |
| Dashboard W7 / W8 / W13 | no view exposes margin, avg-rate-by-grade or top-movers |

---

## 6 · Known gaps

1. **`grade_size` does not exist in Supabase.** MDM-004's per-grade sieve restriction ("only NO 1 and
   NO 1 BB carry `-2`") is enforced **client-side only** in `Catalogue.SizesFor`. The Android app cannot
   enforce it and neither can the database. It belongs on the server.
2. **`Repo.IntakeAsync` is not atomic** — it writes `rough_intake` and a `stock_movement` in two calls.
   A failure between them shows up in `v_reconciliation`. Move to an RPC if it ever fires.
3. **`ConvertAsync` mints a fresh `client_ref` per call**, so a retry after a timeout converts twice.
   Thread the caller's ref through when the UI grows retry.
4. **Excel migration is unowned.** `Sale File Sample.xlsx` and the master file still need loading, with
   legacy grade spellings resolved through `grade.aliases`. My view: it is a one-off script, and it should
   run *after* the RPCs so intake goes through the same path as everything else.
5. **Test data in the live DB.** Verification left 6 `stock_movement` rows netting to 0 ct (ledger is
   append-only, so they stay), a `VERIFY BUYER`, and seeded buyers/brokers. `INV-2026-00001` is consumed.

---

## 7 · One change the Android team must make

> `stock_movement.ADJUST` is now **signed**. It used to always reduce stock; a positive `weight_ct` now
> adds and a negative removes. `v_stock_position.balance_ct` already accounts for this, so anything
> reading the view is correct with no change. Only code reading raw `stock_movement` rows that *assumes
> ADJUST subtracts* needs fixing — most likely a movement-history list showing a `−`.
>
> Two additive columns, safe to ignore: `stock_movement.reason` and `client_ref` on three tables.
> No column renamed or removed.

If the Android app computes any balance itself instead of reading `v_stock_position`, that is a
golden-rule violation independent of this change and will drift.

---

## 8 · Running it

```
dotnet run --project DiamondDesktop          # sign in with your Supabase account
dotnet run --project DiamondCalc.Tests       # 129 checks, ~2s, still green
dotnet test DiamondDesktop.UiTests           # currently broken — see §4
```

`DiamondApi` is no longer needed for the desktop app.

---

## 9 · Hard-won gotchas

**supabase-csharp deserializes with Newtonsoft, not System.Text.Json.** `[JsonPropertyName]` is silently
ignored — every property comes back as `default` with no error. Use `[JsonProperty]`.

**Never map a generated column.** `sales_line.rejection_ct` is `GENERATED`; mapping it makes every
insert fail.

**Identity PKs need `[PrimaryKey("x", false)]`** — the second arg is `shouldInsert`.

**`invoice_no` and `status` must be `ignoreOnUpdate`.** `SaveDraftAsync` is read-modify-write; without
this, a concurrent post landing between the read and the PATCH gets **un-posted** while its stock
movements stay in an append-only ledger.

**`sum()` over zero rows is NULL.** `dashboard_summary` on an empty range throws against non-nullable
properties unless you set `NullValueHandling.Ignore`.

**wpfpilot-mcp** is good for *reading* the UI tree and invoking named buttons. It cannot type into a
DataGrid cell, and its `index` / `nearName` / `nearAutomationId` / `parentName` selectors are **silently
ignored** — every selector falls back to the first match of that control type. That once resolved a
"theme toggle" click to the **Minimize** button, which looked like success. Use `verb=invoke`, never
`verb=click`, and assert independently: its success response is not evidence anything happened.
For real UI testing use FlaUI directly.
