# Solitaire Desk — beginner's testing guide

Written for someone who has never used this app. Work through it page by page.

**Before you start**

- Sign in as an **owner** account. Some pages are hidden from other roles.
- Every page has a **Refresh** button. If a screen looks empty, press it before assuming a bug.
- The red line along the bottom of the window is where the app talks to you. Read it after every action.
- "ct" means carats. Money has no currency symbol — the base currency is set in Settings (INR).
- Large money is shortened: `1.25 K`, `1.25 M`, `1.25 B`. Carats are always shown in full.

**How to read a test case**

| Column | Meaning |
|---|---|
| Do this | The exact steps |
| Expect | What a correct app does |
| If you see something else | It is a bug — note the page, the steps and the message |

---

## 1 · Sales entry

**Purpose.** Type a new sales invoice: who is buying, at what terms, and which parcels of
diamonds at what price. Save it as a draft, then post it. Posting is what deducts stock and
assigns the invoice number.

**Fields**

| Field | What it is | Rules the app enforces |
|---|---|---|
| Date | Invoice date | Defaults to today; any date allowed |
| Buyer | Who is buying | **Required.** Must be picked from the list, not typed freely |
| Broker | Optional agent | Optional |
| Broker % | Their commission | Comes from the broker, editable per invoice |
| Terms | Days until payment is due | **Cannot be negative.** Due date is calculated from it |
| Type | BILL or other document type | Defaults to BILL |
| Grade (line) | Diamond grade | **Required** |
| Size (line) | Sieve size | **Required**, and must be a size that grade actually uses |
| Weight ct | Gross weight of the parcel | **Must be greater than 0** |
| Selection ct | Weight actually selected | Rejection = Weight − Selection, calculated for you |
| Price/ct | Rate per carat | **Cannot be negative**. Zero is allowed |
| Ex Rate, Less 1, Less 2 | Exchange rate and two discounts | Must be in range or the line shows an error |

**Sample data**

```
Date        today
Buyer       ABC Company
Broker      (leave empty)
Terms       45
Line 1      Grade NO 1   Size -6.5   Weight 12.5   Selection 10.0   Price/ct 35000
```

**Positive tests**

| Do this | Expect |
|---|---|
| Fill the sample above, press **Save draft** | "Draft saved · 1 line(s)" |
| Press **Save draft** again without changing anything | Still one invoice, not two |
| Press **Post** | A popup "Posted as MIG-xxxx", the form clears for a new invoice |
| Go to **Invoices**, press Refresh | The new invoice is in the list with status POSTED |
| Go to **Stock**, press Refresh | NO 1 × -6.5 balance has dropped by 10 carats |

**Negative tests**

| Do this | Expect |
|---|---|
| Leave Buyer empty, press Save draft | "Buyer is required", cursor jumps to the Buyer box |
| Type Terms = −5 | "Terms cannot be negative" |
| Delete every line, press Save draft | "An invoice needs at least one line" |
| Set Weight = 0 | Line shows "Weight must be greater than 0" |
| Pick grade NO 1 and a size NO 1 does not use | "NO 1 does not use size …" |
| Type Price/ct = −100 | "Price cannot be negative" |
| Type Weight = 50000 | Asks "That is 50,000 carats. Is that right?" — you can say No |
| Post an invoice for more stock than exists | Warns "Negative stock", lists the shortfall, asks "Post anyway?" |
| Double-click Post quickly | Only one invoice is created |

**Behind the scenes.** `save_draft_invoice` and `post_invoice` (database functions).
Tables: `invoice`, `sales_line`. The invoice number is assigned by the database, never by the app.

---

## 2 · Invoices

**Purpose.** Find any invoice, look at its detail, record money received against it, cancel it,
print it, or export the list.

**Filters.** Status, Buyer, and a search box that matches **invoice no, buyer or amount**.

**Sample data.** Use the invoice you just posted.

**Positive tests**

| Do this | Expect |
|---|---|
| Press Refresh | The list fills, the chip says how many |
| Type an invoice number in Search | Only that invoice remains |
| Press Escape in the search box | The search clears |
| Click a row | A detail panel opens on the right |
| Type 5000 in Receipt amount, press **Record receipt** | "Receipt recorded · 5,000.00 CASH", Outstanding drops |
| Press **Export CSV** | A .csv file is written with full, unshortened numbers |
| Select an invoice, press **Cancel invoice**, confirm, type a reason | "Cancelled … · stock returned" |

**Negative tests**

| Do this | Expect |
|---|---|
| Press Record receipt with nothing selected | "Select an invoice first" |
| Record a receipt with the amount box empty | "Enter a receipt amount" |
| Record a receipt of 0 or −100 | "Enter a receipt amount" |
| Record a receipt against a **cancelled** invoice | "That invoice is cancelled — nothing can be received against it" |
| Cancel an already-cancelled invoice | "That invoice is already cancelled" |
| Cancel and leave the reason blank | "A cancellation reason is required" |

**Behind the scenes.** View `v_invoice`; `cancel_invoice` function; receipts are inserted
straight into the `receipt` table.

---

## 3 · Receivables

**Purpose.** Who owes you money and how overdue it is, grouped into ageing buckets.

**Positive tests**

| Do this | Expect |
|---|---|
| Press Refresh | Buckets fill (Current, 1-30 days, and so on) |
| Click a bucket card | The list narrows to that bucket |
| Search a buyer name | Only their unpaid invoices remain |
| Click a row | A panel shows that buyer's unpaid invoices and total |
| Export CSV | Full numbers, not shortened |

**Negative tests**

| Do this | Expect |
|---|---|
| Search something that matches nothing | An empty state explains why, not a blank table |
| Record a full receipt on Invoices, come back and Refresh | That invoice has left Receivables |

**Behind the scenes.** View `v_receivables_ageing`. Read-only page — nothing here writes.

---

## 4 · Stock

**Purpose.** How many carats you hold of each grade × sieve size, what they are worth, and the
movement history of any one bucket.

**Fields.** Grade filter, Size filter, Search (grade code, grade name or size),
**Hide empty buckets** (on by default).

**Positive tests**

| Do this | Expect |
|---|---|
| Press Refresh | Buckets load; the four cards at the top fill |
| Read "Carats held" | It equals the sum of the Balance ct column below |
| Untick **Hide empty buckets** | Many more rows appear, most with 0.0000 |
| Tick it again | Only buckets holding a balance remain |
| Click a row | A Movements drawer opens on the right |
| Press **Show movements** | That bucket's intakes, sales and adjustments appear |
| Click a different row | The drawer clears — it never shows the old bucket's history |
| Press **Run invariants** | A reconciliation check reports pass or fail |
| Press Export CSV | Full numbers |

**Negative tests**

| Do this | Expect |
|---|---|
| Press Show movements with nothing selected | The button is greyed out |
| Search "zzzz" | "No bucket matches these filters. Press Clear to see them all." |
| Pick a grade with no stock | An empty state, not a blank table |

**Behind the scenes.** Views `v_stock_position` and `v_stock_movement`. Read-only page.

---

## 5 · Intake & movements

**Purpose.** Everything that puts carats **into** stock or moves them between buckets. Four
separate operations on one page.

### 5a · Rough intake

| Field | Rules |
|---|---|
| Grade, Size | **Both required** |
| Weight ct | **Must be greater than 0** |
| Price/ct | **Required** — it sets the cost basis. Zero is allowed but must be typed |

| Do this | Expect |
|---|---|
| Grade NO 1, Size -6.5, Weight 100, Price 35000, **Add intake** | "Intake recorded · 100.0000 ct"; weight and price boxes clear |
| Check Stock | That bucket is 100 ct heavier |
| Leave Price empty and press Add intake | "Enter the price per carat — it sets this parcel's cost basis" |
| Weight = 0 | "Weight must be positive" |
| No grade chosen | "Pick a grade and size" |
| Weight = 20000 | Asks you to confirm |

### 5b · Grade-to-grade conversion ("avelu")

Moves carats from one bucket to another. **Total carats held must not change.**

| Do this | Expect |
|---|---|
| From NO 1 -6.5 → To NO 2 -6.5, Weight 10, **Convert** | "Converted 10.0000 ct · total carats unchanged" |
| Check Stock before and after | One bucket down 10, the other up 10, grand total identical |
| Leave one side unpicked | "Pick both sides" |
| Weight = 0 | "Weight must be positive" |
| Price/ct = "abc" | "Price/ct must be a number, or left blank" |

### 5c · Rejection with dispositions

| Do this | Expect |
|---|---|
| Grade, Size, Weight 5, **Record rejection** | "Rejection recorded · 5.0000 ct" |
| Add disposition rows then record | Warns that dispositions were **NOT saved** — there is no table for them yet |
| Weight = 0 | "Weight must be positive" |

### 5d · Stock adjustment

| Do this | Expect |
|---|---|
| Weight −5 with a reason, **Adjust** | "Adjustment recorded — it stays visible in the ledger forever" |
| Leave Reason blank | "An adjustment needs a reason" |
| Weight = 0 | "Weight must be positive" |

### 5e · Bucket ledger (right-hand panel)

| Do this | Expect |
|---|---|
| Pick grade and size, press **Load** | Movements appear as entries with coloured type badges |
| Record an intake into the bucket you are viewing | The panel refreshes by itself |
| Pick a bucket that has never traded | "No movements for … Nothing has been taken in, sold or adjusted here." |

**Behind the scenes.** Functions `record_intake`, `convert_stock`, `record_rejection`,
`adjust_stock`. Views `v_stock_movement`, `v_sales_line`.

---

## 6 · Master data

**Purpose.** The reference lists everything else depends on: grades and their alternative
spellings, sieve sizes, buyers, brokers and prices.

**Positive tests**

| Do this | Expect |
|---|---|
| Search "NO 1" | The grade list narrows |
| Press Ctrl+F | The cursor jumps to the search box |
| Double-click an **Aliases** cell, add `;TEST1`, press Enter | Saved; the Audit page records the change |
| **Add buyer**: name "Test Buyer", terms 45 | "Buyer 'Test Buyer' added" and they appear in Sales entry |
| **Add broker**: name "Test Broker", 1.5 | Added and available in Sales entry |
| Set a price: grade, size, SALE, 40000 | "… = 40,000.00 from today" |

**Negative tests**

| Do this | Expect |
|---|---|
| Add a buyer with a name already in the list | "… is already in the list." |
| Add a buyer with a one-letter name | "That buyer name is too short." |
| Add a buyer with terms 400 | "Terms must be between 0 and 365 days." |
| Add a buyer with terms "abc" | "Terms must be a whole number of days." |
| Add a broker with 150% | "Broker % must be between 0 and 100." |
| Set a price with no grade chosen | "Pick a grade and size" |
| Set a negative price | "Enter a price" |

**Note.** Code, name and order are deliberately read-only — imported invoices refer to them.
Prices are never edited in place; setting a new one closes the old one, so a valuation as of a
past date still finds the price that applied then.

**Behind the scenes.** Tables `grade`, `size_bucket`, `buyer`, `broker`, `price_list`.

---

## 7 · Dashboard

**Purpose.** The owner's overview: what sold, what is owed, what is held.

**Filters.** Range (or a custom From/To), Buyer, Grade, and a search box.

**Positive tests**

| Do this | Expect |
|---|---|
| Choose **All time**, press Apply | All eight cards fill; the trend chart draws |
| Pick a **Buyer**, press Apply | Every number changes — cards, chart, breakdown and table |
| Pick a **Grade**, press Apply | Every sales number becomes that grade's share; a note explains this |
| Hover a point on the chart | A card shows that period's date, amount and share |
| Switch the breakdown dropdown | Bars change to salesperson, buyer, broker, period, ageing or inventory |
| Add up the AMOUNT column in the table | It equals the "Total sales" card above |
| Press **Clear** | All filters reset and the totals return to the unfiltered figures |

**Negative tests**

| Do this | Expect |
|---|---|
| Choose Custom and leave both dates empty | "Custom range: pick a From and a To date, or choose a named range." |
| Choose Custom with To before From | "Custom range: 'To' is before 'From'." |
| Pick a range with no sales | Explains *why* — e.g. "The most recent sale was 31 Jul 2026 — try All time" |
| Pick a buyer who bought nothing in the range | "No sales for … in …" |

**Behind the scenes.** Views `v_invoice`, `v_sales_line`, `v_stock_position`,
`v_receivables_ageing`. Read-only page.

---

## 8 · Audit

**Purpose.** Who changed what, and when. Nothing here can be edited — that is the point.

| Do this | Expect |
|---|---|
| Press Refresh | Recent changes listed newest first |
| Make a change elsewhere (add a buyer), come back, Refresh | Your change is at the top with your name |
| Search "buyer" | Only buyer rows |
| Click a row | Before and after values for every changed field |

**Negative tests**

| Do this | Expect |
|---|---|
| Search "price" | Only rows whose entity/action/record match — **not** every row that happens to have a price column |
| Try to edit a cell | Nothing happens; the page is read-only |

**Behind the scenes.** Table `audit_log`.

---

## 9 · Users

**Purpose.** Who can sign in and what role they hold. **Owner-only** — the tab is hidden from
everyone else.

| Do this | Expect |
|---|---|
| Sign in as owner | The Users tab is visible |
| Sign in as a salesperson | The Users tab is **not** visible |
| Press Refresh | The list of accounts with role and status |
| Search a name | The list narrows |
| Filter by role | Only that role remains |

**Behind the scenes.** Table `profile`. **This page is read-only** — see the gaps list below.

---

## 10 · Settings

**Purpose.** Policies and thresholds the rest of the app reads: overdue days, decimal places,
company name, low-stock threshold.

| Do this | Expect |
|---|---|
| Press Refresh | Settings grouped by category |
| Change "Overdue after (days)" to 20 | "1 unsaved change" appears |
| Press **Save (Owner)** | "Saved 1 setting" |
| Change something, press **Discard changes** | The old value comes back |
| Press Save with nothing changed | "Nothing has changed" |
| Sign in as a non-owner and try to save | The database refuses; the message says so |
| Search "overdue" | Only matching settings |
| Press **Clear** | The search clears (it does **not** discard your edits) |

**Behind the scenes.** Table `app_config`, one write per changed key.

---

# Gaps found — validation and functionality

These are things I checked for and did **not** find. Ordered by how much they matter.

## Likely to cause wrong data

**1 · Receipts are not checked against what is owed.** *Fixed.*
The Invoices page now refuses any receipt above the outstanding balance, naming the figure:
*"That is more than is owed. INV-… has 9,805,373.16 outstanding"*. It also refuses a receipt
against a DRAFT or CANCELLED invoice. A buyer's balance can no longer be driven negative from
the app. *Test I10 in docs/13 was rewritten to match — it used to expect the over-payment to
land.*

**2 · A conversion can be made to the same bucket.** *Fixed.*
Convert refuses both sides identical: *"From and To are the same bucket — a conversion has to
move carats somewhere else"*.

**3 · Settings values have no validation at all.** *Fixed.*
Every documented key is now range-checked before anything is written, and the message names the
setting: `carat_precision` takes 0-4, `money_precision` 0-2, `alert_overdue_days` 0-3650,
`negative_stock` one of BLOCK / WARN / ALLOW, and so on. An unrecognised key is still shown
under "Other" and still passes through unchecked — the database stays the authority on those.
*Test:* set **Carat decimal places** to `abc`, or to `99`, and press Save. Expect a refusal
naming the field, with nothing written.

**4 · Nothing checks the invoice date is sensible.**
Sales entry accepts a date years in the future or the past. Your data already contains invoices
dated 2026. There is no warning.

## Missing functionality

**5 · The Users page cannot do anything.**
It lists accounts and filters them, and that is all. There is no way to change someone's role,
deactivate a leaver, or invite a new user — even though the page is owner-only, which implies it
was meant to manage them. Today that has to be done directly in Supabase.

**6 · Buyers and brokers can be added but never edited or removed.** *Fixed.*
Every buyer and broker row now carries an **Edit** button: rename, change the default terms or
commission, and set Active to yes/no. The row is edited in place — `buyer_id` never changes, so
every invoice ever raised still points at the same record and a rename rewrites no history.
Deactivating takes the party out of the Sales entry pickers without deleting anything. Duplicate
names are refused, excluding the party's own current name.
*Test:* rename a buyer with an invoice against it, then open Invoices — the invoice shows the new
name and its figures are untouched.

**7 · Dispositions on the rejection form are typed and thrown away.** *Fixed.*
`rejection_disposition` exists (migration 0018) and `record_rejection` writes the dispositions in
the **same transaction** as the movement they describe, so a disposition without its rejection is
not expressible. The confirmation now reads *"Rejection recorded · 12.0000 ct — 3 disposition(s)
saved"*.
*Test:* type dispositions totalling more than the rejected weight and press Add rejection. Expect
*"The dispositions come to 15.0000 ct but only 12.0000 ct was rejected"*, with nothing written.

**8 · There is no delete or edit for a posted invoice.**
Cancel is the only reversal, which is correct accounting — worth knowing, not a bug.

## A note on zero-value invoices and the dashboard

`INV-2026-00004` (03 Aug 2026, KIRAN EXPORTS) was posted during testing with an amount of 0.00 and
0.00 carats — a line whose parcel was rejected in full. It is valid data, and it makes the Dashboard
look inconsistent for the month it falls in:

- The **drill-down table** lists it, because it is a posted invoice in range.
- The **trend chart** ignores it, because the chart plots money and it is worth none — so the chart
  says *"No sales in 01 Aug 2026 to 03 Aug 2026"* directly above a table showing one row.

*Resolved in the wording, not the behaviour.* The chart still plots only invoices worth something —
that is correct — but when every invoice in range is worth 0.00 it now says so explicitly:
*"No sales worth anything in 01 Aug 2026 to 03 Aug 2026. 1 invoice posted in this period, every one
of them 0.00 — fully rejected parcels. They are listed below."* The two panels no longer contradict
each other. Posting a zero-value invoice is still allowed, deliberately (item C below).

It also broke two test suites, which is worth recording because the cause was not obvious:

| Suite | What failed | Why |
|---|---|---|
| `dashfix` | 4 checks about the empty-state message | The suite picked "This month" and assumed it was empty. Once a posted invoice existed today, the "most recent sale was …" branch could not fire — that branch only runs when the whole range sits *after* the last sale. |
| `gradefilter` | "every listed invoice contributes something" | The invoice has a NO 1 line worth 0, so one of the 85 rows contributes nothing. The app is right: an invoice appears under a grade filter because it *has* a line of that grade. |

Both were faults in the tests, not the app. `dashfix` now computes its range from the data
(`last posted sale + 1 day`) instead of assuming the current month is empty, and `gradefilter` now
asserts that no row contributes a *negative* amount, with the per-invoice comparison against
`v_sales_line` doing the real work. Evidence for the diagnosis:

```
posted invoices in 01 Aug 2026 .. 03 Aug 2026 : 1
    INV-2026-00004  03 Aug 2026  KIRAN EXPORTS  amount 0.00  carats 0.00
of those worth 0.00 : 1        of those worth > 0 : 0
NO 1 lines : 85 across 85 invoices, 1 of them worth 0
```

## Decisions taken, and why

These three came out of testing Sales entry. All were reviewed and **deliberately left as they
are** — none is a defect, and each would change how the screen is used. They are recorded here and
in a comment at the code they describe, so the next person does not "fix" one by accident.

**A · The line grid reads Size, then Grade.** Every other grid in the app reads Grade then Size,
and this page's own empty-state hint says "Pick a grade and size". Swapping the columns changes the
tab order on a keyboard-first entry screen, where people type without looking. That is a workflow
decision for whoever uses it daily, not a bug. *Comment at the Size column in MainWindow.xaml.*

**B · The line grid's headers are Title Case.** "Size", "Weight", "Price/ct" — every other grid uses
UPPERCASE. Purely visual, one line to change, and equally a design decision. *Same comment.*

**C · An invoice can be posted with a total of zero.** Weight must be above zero, but Selection is
not checked, so a line where everything was rejected amounts to nothing — and if every line is like
that, so does the invoice. This is correct: a parcel can be rejected in full, and that is a real
trade worth recording. Whether the app should *warn* before posting a zero-value invoice is a
business decision. It is how INV-2026-00004 came to show 0.00. *Comment at SaleLine.Validate.*

## Smaller things

**8a · The Remark column was squeezed to nothing.** *Fixed.* It was the only flexible column beside
ten fixed ones, so below about a 1400px window it took the shortfall — 20px at 1010px, showing
"Rema" and no remark text at all. It now has a 140px floor and the grid scrolls instead. Keyboard
entry is unaffected: Tab still walks the cells and the grid scrolls the focused one into view.

**9 · Terms are validated when adding a buyer (0–365) but not on the invoice itself.** *Fixed.*
`InvoiceEntry.Validate()` applies the same 0–365 rule, and the caret jumps to the Terms box.
*Test:* Terms `9999` → *"Terms must be between 0 and 365 days"*.

**10 · Broker % is validated when adding a broker (0–100) but the per-invoice override is only
range-checked by the calculation engine.** *Fixed.*
The header field is validated in its own right, in the same words the Add broker dialog uses, and
focus goes to the Broker % box. Previously the engine threw once per line, so an out-of-range
header value reddened every row with *"Broker % is out of range"* and sent people hunting through
the grid for a fault that was in the header.
*Test:* Broker % `150` → *"Broker % must be between 0 and 100"*, caret in the Broker % box, rows
not reddened.

**11 · No confirmation before leaving an unsaved invoice.** *Fixed.*
**New** asks first when there are unsaved lines: *"This invoice has 3 line(s) that have not been
saved. Start a new one and lose them?"* A saved draft has an id and is not at risk, so it is not
asked about.

**12 · The 1,000-row cap.** Fixed on the desktop — every list pages to the end. **Still open on
the Android app**, where the owner would see only the first 1,000 invoices and no warning.

## Known data problem, not a code bug

**13 · One corrupt intake row.** There is an intake of 5,00,500 ct at 4,00,00,37,500 per carat in
the database. It distorts Stock value and the Dashboard inventory card, and makes the Stock table
wide. It needs a cleanup SQL statement, not a UI change.

---

# Regression round · 05 Aug 2026

Everything below was found by a full code-level audit and fixed in the same pass. Migration
**0018** carries the database half.

## Critical

**14 · Both imports could destroy data and put nothing back.** *Fixed.*
Sale import and stock import are "replace, not merge", and both did it as a delete loop followed
by an insert loop over separate HTTP calls, with no transaction around them. Anything failing
between the two left the database holding neither dataset.

This is not theoretical — it happened during this session. 133 `stock_movement` rows were deleted
at 14:26:05, the replacement never landed, and the stock position collapsed from 62 buckets /
2,344.34 ct to 5 buckets / **−220.85 ct with three negative balances**. The SALE rows survived
because the ledger refuses DELETE to `authenticated`; only the intakes they subtract from vanished.

Both replacements now happen inside one SECURITY DEFINER function —
`replace_imported_stock(date, jsonb)` and `replace_imported_sales(jsonb)` — so they are one
transaction. Any failure rolls back and the previous import is still there. Each can only delete
rows it can prove are its own (`ref_type = 'stock_import'`, `invoice_no LIKE 'MIG-%'`), so a
hand-entered intake and a live invoice remain unreachable.
*Test:* re-run an import and pull the network cable mid-write. Expect the previous dataset intact
and an error, never an empty Stock page.

## High

**15 · CSV export silently dropped every templated column.** *Fixed.*
`ExportGrid` filtered on `DataGridBoundColumn`, which a `DataGridTemplateColumn` is not — so
**STATUS** was missing from the Invoices export and a CANCELLED invoice exported identical to a
POSTED one, same amount, no marker. Same defect on Receivables (BUCKET) and Stock (STATUS). Export
now reads `ClipboardContentBinding`, WPF's own "what is this column's value off-screen", and the
three template columns declare it.
*Test:* cancel an invoice, Export CSV, open the file. Expect a STATUS column reading `CANCELLED`.

**16 · CSV formula injection.** *Fixed.*
Buyer, broker and remark are free text the app accepts, and a value opening with `=` `+` `-` `@`
is executed by Excel on open. Such values are now prefixed with an apostrophe. Numbers are
deliberately exempt — `-1500.00` opens with `-` and must stay a number.
*Test:* add a buyer named `=1+1`, post an invoice for them, export, open in Excel. Expect the
literal text, not `2`.

**17 · The offline outbox was dead code.** *Removed.*
`Outbox.cs` implemented a SQLite queue and replay — 181 lines, and **not one call site anywhere in
the repository**. Writes during a network drop simply failed, while `ClientRef` claimed to be
"offline-safe". The file and its `Microsoft.Data.Sqlite` dependency are gone.
**Offline support does not exist.** See *Still open* below.

## Medium

**18 · Remark was write-only.** *Fixed.* Typed per line and stored on `sales_line`, but read back
by no screen or document. It is now a column on the printed bill, which is the one artefact that
claims to be the complete record of the line.

**19 · No receipt history anywhere.** *Fixed.* The `receipt` table was written to and never read,
so two receipts of 25,000 looked exactly like one of 50,000. The invoice detail drawer now lists
every receipt with its date, method and amount, under the summary.

**20 · Three dialogs could open behind the main window.** *Fixed.* The negative-stock prompt, the
posted-as confirmation, the cancel confirmation and the remove-line prompt were unowned
`MessageBox.Show` calls — the exact hazard `ConfirmLarge` documents and guards against. All now
pass `this`. The crash handler in `App.xaml.cs` stays unowned deliberately: by then the window may
be gone.

**21 · Role matching was case-sensitive.** *Fixed.* `profile.role` has no CHECK constraint behind
it, so a row seeded `Owner` matched nothing and silently stripped a real owner of every permission
— imports greyed out with no explanation. Compared case- and whitespace-insensitively now, on the
Users page counts as well.

**22 · Grade × size was enforced only in the WPF client.** *Fixed.* `grade_size` now exists with a
`BEFORE INSERT OR UPDATE` trigger on `sales_line`, seeded from the rule in docs/04 §3.4 plus every
pairing that already carries stock or history — so no existing row is invalidated. The API, the
Android app and psql are all held to it now.

**23 · Three sale rows were being skipped on import.** *Fixed.* `Sale File Sample-1.xlsx` (merged
04 Aug 2026) introduced the spellings `Ex1` and `T COLOR`, which resolved to no grade, so the
importer dropped those rows — real sales, lost quietly. Aliases added in 0018 and mirrored in the
regression suite. The real workbook now plans **1,438 invoices / 1,447 lines / 0 skipped**, up from
1,436 / 1,444 / 3 skipped.

## Low

**24 · Stock KPI cards silently described a filtered subset.** *Fixed.* The cards total what is on
screen while the header totals all 207 buckets. Both were unlabelled, so a grade filter made them
look like a contradiction. The captions now read "across 3 of 207 buckets · filtered".

**25 · Audit's 500-row cap was invisible.** *Fixed.* A full page looked like a complete history, so
"it is not in the audit trail" was being read as "it never happened". The subtitle now says
"newest 500 only, older entries not loaded" when the window is full.

**26 · The API minted a different invoice series from the database.** *Fixed.* `NextInvoiceNo`
returned `INV-00001` — no year — and derived it from a COUNT, so one deleted invoice made the next
collide with a number already issued. It now matches `next_invoice_no()` exactly: max+1 within the
invoice's own year, formatted `INV-{yyyy}-{00000}`.

## Still open

**27 · Dashboard Margin is "—".** Not fixable at this level: margin needs a cost basis per sold
parcel and no view exposes one. `v_stock_position` carries `avg_cost` per bucket, but nothing
records what the carats on a given sales line cost when they came in. It needs a schema change —
cost captured onto the line at post — not a UI change.

**28 · The Users page is still read-only.** Creating, deactivating or re-roling an account needs
the Supabase `service_role` key, which must never ship inside a desktop binary. This one cannot be
fixed in the WPF app at all; it needs a server endpoint that holds the key, or it stays in the
Supabase dashboard.

**29 · Offline support does not exist.** The dead outbox was removed rather than wired up: doing it
properly needs connectivity detection, replay-on-reconnect, conflict handling and a pending-count
UI, which is a feature, not a bug fix. Until it is built, a write during a network drop fails and
the work is lost. `docs/06` lists the outbox in the architecture — that claim is now aspirational.
