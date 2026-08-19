# 10 · Changes made, and what is still to do

Two products share one Supabase database: the **WPF desktop** (salespeople, read/write) and the
**Android owner app** (`demojasmin/diamond-sales-android`, read-only). This document records what
was changed on the desktop in this round, what is still open on each side, and what has to happen
before either can be handed to a client.

Nothing in this round was committed. `HEAD` is still `e2b04c8`.

---

## Part 1 · WPF — what changed (done)

No API, query, database logic or business rule was modified. Every change is presentation.

### New files

| File | Purpose |
|---|---|
| `DiamondDesktop/Styles/Dashboard.xaml` | Dashboard card/KPI/chart styles |
| `DiamondDesktop/Styles/Audit.xaml` | Audit badges, timeline, detail styles |
| `DiamondDesktop/SplashWindow.xaml(.cs)` | Startup splash |
| `DiamondDesktop/BrandLoader.xaml(.cs)` | The one loading mark, shared by splash and pages |
| `Styles/Components.xaml` (additions) | `DiamondSilhouette` + `DiamondFacets` geometries |

### Modified

`App.xaml` · `App.xaml.cs` · `MainWindow.xaml` · `MainWindow.xaml.cs` · `Styles/Components.xaml` ·
`UiHelpers.cs` · `DiamondCalc.Tests/Program.cs` · `DiamondCalc.Tests/DialogProbe.cs`

### 1.1 Dashboard

* Redesigned in six stages: header, filter toolbar, KPI tiles, charts, drill-down table, polish.
* **Gold/navy palette removed entirely.** `GoldColor`, `GoldDeepColor`, `NavyColor`,
  `GoldBrush`, `GoldSoftBrush`, `GoldEdgeBrush`, `GoldBarBrush` were deleted, not re-pointed.
  Everything now reads app tokens through `DynamicResource`, so the page follows the light/dark
  swap. Verified: 115 keys referenced, none unresolved in either theme.
* **Filter toolbar is one row** — a 7-column grid. Fixed widths for Range/From/To, star widths for
  Buyer/Grade/Search, Auto for the buttons. Measured as a single row at 1920/1600/1440/1366/1280.
  "Reset" renamed to "Clear"; it now also clears the dates and the search box.
* `SEARCH DRILL-DOWN` label shortened to `SEARCH` — it was the widest thing in its column and was
  holding the column open.

### 1.2 Fixes with a root cause worth remembering

| Symptom | Root cause | Fix |
|---|---|---|
| Grids said "No invoices yet" for ~1.8s on every load | `EmptyToVisibilityConverter` treated `null` (not loaded) the same as empty (loaded, zero rows) | `Loaded` parameter on the 7 grid bindings; cell/value bindings unchanged |
| Breakdown + drill-down section invisible | Last child of a `DockPanel`, so it got the leftover height — which was zero — and the page had no scroll | Page wrapped in a `ScrollViewer`; section given a fixed height. 337px of content had been unreachable |
| Trend line drawn as a wedge | `Viewbox Stretch="Fill"` scaled the stroke ~11× across against ~2.6× down | Canvas plots in real pixels; redraws on `SizeChanged` |
| Bars never filled their track | `BarWidth` hardcoded to 420px inside a `*`-width track | Star-weight columns (`BarStar`/`RestStar`); the proportion `\|value\|/max` is unchanged |
| Invoice value clipped ("2,00,20,75,46,66,95,7…") | Fixed 228px tiles | Tiles stretch and reflow 4/2/1 by width; values sit in a shrink-to-fit `Viewbox` |
| Errors vanished after 6s | `Say` auto-cleared everything | Only success messages fade; failures persist |
| "Nothing in this period" stuck under a full screen | Advice never withdrawn once it stopped being true | Cleared on reload and withdrawn when a breakdown returns rows |
| Audit detail showed headers but no fields | `AppRow` pins `DataGridRow` to `Height="42"`; a row contains its cells **and** its details | Details moved out of the row into a drawer |
| App started with no window | `SplashWindow` animated names that had moved into `BrandLoader`; threw on `Loaded`, the global handler opened a blocking MessageBox | Orphaned targets removed |

### 1.3 Audit page

Header with summary, filter toolbar (Action / Entity / Search), four KPI cards, a timeline of the
12 newest entries, the change-log table, and a **detail drawer** replacing the old
`Before`/`After` columns of flattened `k=v` text.

* **Search covers Entity, Action and Record only.** It previously matched column *names*, so
  "price" returned 192 `sales_line` rows via `price_per_ct`. It also now indexes the record both
  as `1` and `#1`, because the column displays `#1`.
* The detail panel adapts: an insert shows one value per field and says
  *"no previous values"* once; an update shows labelled BEFORE/AFTER with changed fields
  highlighted; a delete shows the values as they stood.
* `IMPORT` is templated but **no database trigger emits it** — imported rows log as `INSERT`.

### 1.4 Loading

* `BrandLoader` — diamond, two counter-rotating arcs, sparkles, "Loading" + three dots. No bar, no
  percentage.
* Splash uses it with the wordmark; pages use it without (`ShowWordmark="False"`).
* The page loader is a veil over the workspace, raised and lowered by `Read<T>` — the single funnel
  all 23 reads pass through. In-flight work is **counted**, not flagged, because `Busy` scopes nest.
* **The veil also covers the nav sidebar** (the sidebar is drawn by the `TabControl` template), so
  there is no navigating away mid-load. Change the `NavTabs` template if that is not wanted.

### 1.5 The diamond mark

The old mark was `M 12,2 L 22,9 L 12,22 L 2,9 Z` — a four-point rhombus, a placeholder pretending
to be a gem. It is now a **brilliant cut seen from the front**, defined once in `Components.xaml`
as two geometries and used by both the loader and the dashboard header:

| Resource | Content |
|---|---|
| `DiamondSilhouette` | `M 7,3 L 17,3 L 22,9 L 12,21 L 2,9 Z` — table, crown, girdle, culet |
| `DiamondFacets` | girdle + two crown bezels + two pavilion facets |

**Both paths must share one coordinate space.** They live in a fixed 24×24 `Canvas` inside a single
`Viewbox`. Giving each `Path` its own `Stretch` fits each to its *own* bounds — which differ — and
the facet lines slide off the stone. There is a check asserting the facet bounds stay inside the
silhouette.

Animation is a **shimmer**, not a spin: the facet lines breathe 0.3 → 0.85 opacity on a 1.4s cycle,
so light appears to catch the cut while the geometry stays still. A rotating gem reads as a spinner
in costume.

### 1.6 Splash

Shown in `App.OnStartup` around the existing sign-in sequence, which is unchanged. It covers the
session restore in `LoginWindow.Loaded`. Dismissed on `ContentRendered` **and** in a `finally`,
because a restored session skips the form entirely.

---

## Part 2 · WPF — still open

1. **Supabase URL and anon key are compiled constants**, duplicated in `Data/Db.cs:16-17` and
   `Data/Outbox.cs:14-15`. Pointing at a client needs a code edit and a rebuild. Move to a config
   file beside the exe. *The Android app already does this correctly — copy it.*
2. **Buyer and Grade filters do not reach the KPI tiles.** `dashboard_summary(p_from, p_to)` takes
   dates only, so W1/W2/W3/W9/W11 and the invoice count describe all buyers while the charts and
   table describe the filtered set. This is what produced "13 invoices" beside "1 invoice".
   W14 Broker cost *is* client-side, so the Sales row currently mixes two scopes.
3. **Grade filter affects only W11/W12.** `v_invoice` carries no grade — grade lives on
   `sales_line` — so it does nothing for sales, the trend or the drill-down.
4. **W7 Margin is permanently "—"**: no cost basis exists in the database.
5. **W11 Inventory value is wrong** (₹2.00e15) until the corrupt intake is removed. See Part 4.
6. **Five posted invoices are dated in the future** (to 31-07-2026) and fall outside every range;
   1436 POSTED exist, 1431 appear in "All time".
7. Pages not reviewed: Intake & movements, Users, Settings, Login.
8. `DiamondApi` is a separate SQLite stack that the desktop app does not call. Decide whether it
   ships or is deleted.

---

## Part 3 · Android — still open

Reviewed from source at commit `1dd5c7e`. Not built or run (no Android SDK available here).

### 3.1 Critical · the 1000-row cap

`data/SupabaseRepository.kt` fetches with **no `limit`, `range` or paging**. PostgREST returns at
most 1000 rows and says nothing about the rest. The file's own comment reads:

> *"The whole book is fetched in one pass … the domain is small-data (a few thousand rows a year)"*

There are already **1438 invoices**. The owner's phone therefore reads 1000 and treats that as the
whole business — wrong sales, wrong receivables, no error. The same comment claims this is
*"what keeps the phone and the WPF desktop from ever disagreeing"*; it is the reason they will.

The desktop hit the identical bug and fixes it in `Repo.AllPagesAsync` — keep requesting the next
1000 until a short page comes back. The same shape in Kotlin (supabase-kt **3.7.0**):

```kotlin
/** PostgREST caps a response at 1000 rows and gives no sign that more exist. */
private suspend inline fun <reified T : Any> PostgrestQueryBuilder.selectPaged(
    pageSize: Long = 1000L,
): List<T> {
    val all = mutableListOf<T>()
    var from = 0L
    while (true) {
        val page = select { range(from, from + pageSize - 1) }.decodeList<T>()
        all += page
        if (page.size < pageSize) return all   // short page means the end
        from += pageSize
    }
}
```

Apply it in `fetchAll()` to the transactional reads — `v_invoice`, `v_sales_line`, `receipt`,
`v_stock_movement`:

```kotlin
val invoicesJob = async { db.from("v_invoice").selectPaged<InvoiceDto>() }
```

The reference reads (`grade`, `size_bucket`, `buyer`, `broker`, `app_config`, `v_stock_position`)
are well under 1000 and can stay as they are.

**Order matters when paging.** Each request is a separate query, so without a stable sort the
database may return rows in a different order between pages and a row can be duplicated or skipped.
The desktop orders by `invoice_date desc, invoice_id desc` for exactly this reason — add an
equivalent `order(...)` inside `selectPaged`, or the paging swaps one silent wrongness for another.

*Snippet not compiled here — no Android SDK on this machine. Treat the shape as correct and the
exact DSL as needing a build.*

**Quick confirmation:** compare the owner's all-time total sales against the desktop's.

### 3.2 Critical · demo mode is invisible

No `local.properties` ⇒ `Supabase.isConfigured` is false ⇒ the app silently runs `DemoRepository`:
a seeded year of realistic fake data, **no login**, normal company name. `isDemoMode` exists on the
ViewModel but is used only to skip the unlock screen — it is never shown.

An owner given a misconfigured build sees invented revenue and cannot tell. Either show a permanent
DEMO banner or refuse to start unconfigured.

### 3.3 The two dashboards compute differently

The Android app makes **no RPC calls at all** — no `dashboard_summary`. The desktop reads that
server function; the phone derives the same figures locally. Two implementations of one number
drift. Decide which is authoritative.

### 3.4 Reads base tables as well as views

Reads: `v_invoice`, `v_sales_line`, `v_stock_position`, `v_stock_movement` **and** `app_config`,
`broker`, `buyer`, `grade`, `profiles`, `receipt`, `size_bucket`.

`receipt` is the one to move behind a view — it is transactional, not reference data. It reads
neither `v_receivables_ageing` nor `v_reconciliation`, so ageing is recomputed on the phone too.

### 3.5 Confirmed good — do not "fix"

* **No writes anywhere.** Searched the whole source for insert/update/delete/upsert: none.
* **Credentials via `local.properties` → `BuildConfig`**, gitignored.
* **The palette matches the desktop byte for byte** (`#2F6BE0`, `#1E9E6A`, `#B87A16`, `#CF4632`
  and the dark variants). Both products are blue. Any navy/gold mockup matches neither.

---

## Part 4 · Database and shared — the real blockers

1. **Migrations 0001–0006 do not exist in either repo.** Only 0007–0013 are in
   `supabase/migrations/`; the Android repo contains no SQL at all, though its code cites
   `0002_calc_views.sql` and `0003_rls.sql`. **A client database cannot be built today.**
   Recover with `supabase db pull` or `pg_dump --schema-only`, then prove it by resetting a scratch
   project and running both apps against it.
2. **No seed script.** Grades, sieve sizes, currencies and `app_config` are data, not schema. A
   fresh database has none, so the Excel import fails on every row.
3. **Per-migration notes for a new client:**
   * `0011` — *run it.* Fixes `v_reconciliation` reporting every cancellation as a permanent
     discrepancy. Written but deliberately never applied to the demo project, so it is untested
     against live data.
   * `0012` — *do not run.* Deletes one corrupt row (`stock_movement 12`) that exists only in the
     demo database.
   * `0013` — *must run.* Grade aliases for the hyphenated workbook spellings. Without it the
     import skips **750 of 1437 rows**.
4. **Confirm the owner account is read-only in the database.** The Android app cannot write, but
   its key ships in the APK; only RLS stops a direct REST call. Check the `INSERT`/`UPDATE`/`DELETE`
   policies on `sales_invoice`, `sales_line`, `receipt`.
5. **Decide where migrations live.** Two apps consume one schema, so schema changes are no longer
   private to the desktop. Either the WPF repo is the source of truth and Android pins a version,
   or a third `diamond-db` repo holds them.
6. **Add a schema-version check to both apps** (`app_config.schema_version`), so an app opened
   against an unprovisioned database says so instead of showing empty screens.

---

## Part 5 · Client handover runbook

1. Create the client's Supabase project.
2. Run **0001–0013** except `0012`. Verify object counts.
3. Run the seed script (grades, sizes, currencies, `app_config`, price list).
4. Create the owner in **Authentication → Users**, then insert a matching `profile` row with the
   same UUID, `role`, `active = true`. *An auth user without a profile cannot sign in — the app
   says "This login has no profile."*
5. Put the project URL + anon key into the desktop config and Android `local.properties`.
6. Smoke test: sign in on desktop → import the workbook → check totals → open the Android app and
   confirm the same totals.

**Credentials — what lives where**

| Item | Secret? | Where |
|---|---|---|
| Project URL + anon key | No (RLS is the boundary) | App config / `local.properties`, per client |
| DB password, `service_role` key | **Yes — full admin** | Password manager only. Never in an app or a repo |
| User email + password | Yes | Supabase Auth, hashed. Never stored by us |
| Session after login | Sensitive | Desktop: `%LOCALAPPDATA%\SolitaireDesk\session.dat`, DPAPI-sealed |

No user password is stored by either application.

---

## Part 6 · Verification, and three lessons

Suites: `dotnet run --project DiamondCalc.Tests -c Release`, plus offscreen probes that build the
real window, drive it and render it to PNG.

Three failure modes these caught, each of which had passed a weaker check first:

1. **Constructing a window is not loading it.** Storyboards only run on `Loaded`, so every probe
   was green while the app opened with no window at all. There is now a static scan (every
   `Storyboard.TargetName` must exist in the same file) and a probe that raises `Loaded` on the
   splash, loader, login and main window.
2. **A `Collapsed` element is still in the visual tree.** Asserting on text found in the tree
   "passed" while the panel was invisible, and "failed" while it was correctly hidden. Assert on
   `Visibility` and `ActualHeight`.
3. **Property checks do not prove pixels.** The audit detail had the right elements at the right
   size and was still clipped to a sliver on screen. Rendering to an image and looking at it is the
   only check that caught it.

---

## Priority

| # | Item | Side | Before handover? |
|---|---|---|---|
| 1 | Recover migrations 0001–0006, prove a blank DB builds | Shared | **Yes** |
| 2 | Paging fix (1000-row cap) | Android | **Yes** |
| 3 | Make demo mode visible, or fail closed | Android | **Yes** |
| 4 | Seed script | Shared | **Yes** |
| 5 | Confirm owner read-only in RLS | Shared | **Yes** |
| 6 | URL/key into config | WPF | **Yes** |
| 7 | Run the corrupt-intake cleanup | Shared | Yes |
| 8 | Decide filter scope on KPI tiles | WPF | Recommended |
| 9 | Align the two dashboards on one calculation | Both | Recommended |
| 10 | Schema-version check | Both | Recommended |
| 11 | Review remaining pages | WPF | Later |
| 12 | Delete or keep `DiamondApi` | WPF | Later |
