# 13 · Test data — Sales entry & Invoices

Manual test pack for the two pages reviewed so far. Every figure is taken from the **real
workbook** (`Sale File Sample.xlsx`, verified in [04-workbook-verification.md](04-workbook-verification.md)),
so the expected amounts are known to the paisa — if the app disagrees, the app is wrong.

---

## 0 · What is in the database right now

Checked live against `nzcvjaixgqoliyrotstz`:

| | |
|---|---|
| Buyers | `ABC Company` (terms 45) · `QUEST DIAMOND` (0) · `Z K ENTERPRISE` (0) · `VERIFY BUYER` (45) |
| Brokers | `JITESH SHAH` (1 %) · `PARESH MEHTA` (1 %) |
| Grades | 23, including `NO 2 BB` |
| Sizes | `-2` · `-6.5` · `+6.5` · `+11` |
| **Stock** | **none — 0 of 92 buckets** |
| **Invoices** | **none** |

**Consequence:** post anything today and you get the negative-stock warning every time. Seed stock
first (step 1) unless that warning is what you are testing.

---

## 1 · Seed the stock (Intake & movements tab)

Four intakes. Round numbers, comfortably above what the invoices consume:

| Grade | Size | Weight ct | Price/ct |
|---|---|---|---|
| `NO II` | `-6.5` | 500 | 40000 |
| `NO II` | `+6.5` | 500 | 37500 |
| `NO II` | `+11` | 500 | 47000 |
| `NO 1 BB` | `+11` | 500 | 53000 |

Check on the **Stock** tab that four buckets now show 500.0000 ct.

---

## 2 · Page 1 · Sales entry — three invoices

### INV-1 · single line, tests the discount chain

Header — Buyer `ABC Company`, Broker `JITESH SHAH`, Type `BILL`.
Terms should **auto-fill to 45**, Broker % **to 1**. Due date = invoice date + 45.

| Size | Grade | Weight | Selection | Price/ct | Less 1 | Less 2 |
|---|---|---|---|---|---|---|
| `-6.5` | `NO II` | 2.30 | 2.30 | 63000 | 2.5 | 0 |

| Expect | |
|---|---|
| Rejection | `0.00` |
| **Amount** | **`139,864.73`** — workbook `Sale!Q3` |
| Carats / Amount footer | `2.30` / `139,864.73` |

Discounts **compound**: 2.5 % then 1 % broker. If you see `139,872.75` the app is *adding* 3.5 %
instead — that is the bug CALC-1 exists to prevent.

### INV-2 · two lines, one fully rejected

Header — Buyer `Z K ENTERPRISE` (terms auto-fills **0** → Due = invoice date), Broker `PARESH MEHTA`.

| Size | Grade | Weight | Selection | Price/ct |
|---|---|---|---|---|
| `+11` | `NO 1 BB` | 137.29 | 112.89 | 53001 |
| `+11` | `NO II` | 15.39 | **0** | 53001 |

| Expect | |
|---|---|
| Line 1 rejection | `24.40` — the SALES-001 acceptance criterion |
| Line 1 amount | `5,923,450.06` — workbook `Sale!Q4` |
| Line 2 amount | `0.00`, rejection `15.39`, **no error, does not block the save** |
| Invoice total | `5,923,450.06` · carats `112.89` |

Line 2 is workbook row 5 — a real parcel the buyer rejected outright. A validation that refuses a
zero-selection line would reject real business.

### INV-3 · three lines, tests the blended rate

Header — Buyer `QUEST DIAMOND` (terms 0), Broker `JITESH SHAH`.

| Size | Grade | Weight | Selection | Price/ct | Less 1 |
|---|---|---|---|---|---|
| `+11` | `NO II` | 232.86 | 149.74 | 47251 | 4 |
| `+6.5` | `NO II` | 93.81 | 77.99 | 37501 | 4 |
| `-6.5` | `NO II` | 14.18 | 12.65 | 37501 | 4 |

| Expect | |
|---|---|
| Amounts | `6,724,426.65` · `2,779,637.72` · `450,858.02` (`Sale!Q6/Q7/Q8`) |
| Total | `9,954,922.39` |
| Carats | `240.38` |
| **Blended rate/ct** | **`41,413.27`** = total ÷ carats |

Post all three. Each should return `INV-2026-0000n` and deduct stock — check the Stock tab.

---

## 3 · Page 1 · Validation and UI checks

| # | Do this | Expect |
|---|---|---|
| V1 | Type `12x` into Weight | `x` is rejected outright — the box never holds it |
| V2 | Paste `abc` into Price | Paste refused |
| V3 | Type `-5` into Terms | `-` refused (negatives are not allowed there) |
| V4 | Pick a grade, type nothing else | Row stays **white**. It is unfinished, not wrong |
| V5 | Selection `200`, Weight `100` | Row turns **red**, Amount `0.00`, tooltip explains |
| V6 | Save with no buyer | *"Buyer is required"* and the caret jumps to the Buyer picker |
| V7 | Open the Grade list on a fresh row | Placeholder reads `Grade` before you pick |
| V8 | Fresh invoice, look at the grid | Hint: *"Pick a grade and size, then type the weight…"* |
| V9 | Select a typed line, press `Delete` | Confirm names it: *"Remove NO II · 2.30 ct?"* |
| V10 | Press `Delete` on the blank row | Nothing happens, no prompt |
| V11 | `Enter` on the last row | New line, header retained |
| V12 | `Ctrl+S` | Button reads `Saving…`, whole row disabled until it returns |
| V13 | Click Post | Reads `Posting…`; on success an invoice number appears |
| V14 | Grade `NO II` → open Size | **3** sizes, no `-2`. `NO 1` offers **4** |
| V15 | Open the date picker | Day names `Sun…Sat` visible; only this month's dates shown |
| V16 | Page back a month, close, reopen | Returns to the invoice's month, not the one you browsed to |
| V17 | Narrow the window | Header fields wrap in pairs — a label never separates from its box |

---

## 4 · Page 2 · Invoices

Run after posting the three invoices above.

| # | Do this | Expect |
|---|---|---|
| I1 | Open the tab before posting anything | *"No invoices yet. Post one from Sales entry, or press Refresh."* |
| I2 | Press Refresh | Reads `Loading…` while it fetches |
| I3 | **Double-click any cell** | **Nothing becomes editable** — this is the read-only fix |
| I4 | Check Amount / Outstanding columns | Right-aligned, headers right-aligned over them |
| I5 | Narrow the window | Toolbar wraps; receipt amount, method and button stay together |
| I6 | Record receipt with nothing selected | *"Select an invoice first"* |
| I7 | Select INV-1, leave the amount blank | *"Enter a receipt amount"* |
| I8 | Select INV-1, receipt `50000` `RTGS` | *"Receipt recorded · 50,000.00 RTGS"*; Outstanding → `89,864.73` |
| I9 | Receipt the remaining `89864.73` | Outstanding → `0.00` exactly |
| I10 | **Settlement test** — on INV-2, pay `5923451` | Outstanding `-0.94`. A real over-payment, not float noise — this is DQ-12, and **PAY-003 is not built yet**, so the residue stays |
| I11 | Cancel invoice with nothing selected | *"Select an invoice first"* |
| I12 | Cancel INV-3 | Confirm names it: *"Cancel INV-2026-00003 for QUEST DIAMOND? 9,954,922.39 will be reversed…"* |
| I13 | Answer No | Nothing happens |
| I14 | Cancel again, then leave the reason blank | *"A cancellation reason is required"* |
| I15 | Cancel again with a reason | *"Cancelled INV-2026-00003 · stock returned"*; status chip → CANCELLED; Stock tab back to 500 |
| I16 | Try to receipt the cancelled invoice | *"That invoice is cancelled — nothing can be received against it"* |
| I17 | Try to cancel it again | *"That invoice is already cancelled"* |

---

## 4a · Page 4 · Stock — step by step

Stock is a **read-only page**. Nothing is entered here; every figure is derived from movements
written by Intake (page 5) and by posting invoices (page 1). So the test is: does it read the
ledger correctly and present it clearly.

### Prerequisite

At least one intake, and ideally one posted invoice, so a bucket has both an IN and an OUT.
`NO II / -6.5` currently has exactly that.

### The checks

| # | Do this | Expect |
|---|---|---|
| S1 | Open the tab | Grid loads by itself; the summary shows total carats and total value |
| S2 | Look at the right-hand grid | *"Pick a grade and size on the left, then press Show movements."* |
| S3 | Look at **Show movements** with nothing selected | Disabled |
| S4 | Select any row | Show movements becomes enabled |
| S5 | Press **Show movements** | Right grid fills; status bar says *"Movements for NO II × -6.5"* |
| S6 | Check the arithmetic on that bucket | `balance = Σ INTAKE + CONVERT_IN − CONVERT_OUT − REJECTION − SALE` |
| S7 | Select a bucket with no activity (most are 0.0000) | Right grid shows no rows |
| S8 | Press **Refresh** | Button reads `Loading…`, whole toolbar disabled until it returns |
| S9 | Press **Export CSV** | File written; status bar names the path |
| S10 | Press **Run invariants** | Dialog reports pass, or lists each bucket that does not reconcile |
| S11 | Narrow the window to ~900px | Toolbar wraps; neither grid collapses below 360px |
| S12 | Double-click any cell | Nothing becomes editable |

### Worked example — `NO II / -6.5`

Its ledger currently holds:

```
INTAKE      500.0000
SALE         90.0000      (INV-2026-00002)
REJECTION    10.0000
SALE        450.0000      (INV-2026-00003)
REJECTION    50.0000
```

so `500 − 90 − 10 − 450 − 50 = −100.0000 ct`. **Negative, and correctly so** — both invoices were
posted through the negative-stock warning with override. This is the bucket to watch when testing
CFG-003: it proves an override really does drive a balance below zero rather than silently
refusing.

### To make the invariant check pass

It cannot pass today — see §5a. Every cancelled invoice leaves a permanent discrepancy.

---

## 5a · What "Run invariants" is reporting

The check compares **carats moved out of stock** against **carats sold on invoices**, per
grade × size. Two buckets currently fail:

```
NO 1    × -6.5  — moved   6.0000 ct, invoiced 0.0000 ct, off by   6.0000
NO 1 BB × -2    — moved 120.0000 ct, invoiced 0.0000 ct, off by 120.0000
```

Their ledgers explain it:

```
NO 1 BB / -2    SALE      120.0000   ref=sales_line
                REJECTION   3.8700   ref=sales_line
                ADJUST    120.0000   ref=cancel      <- the reversal
                ADJUST      3.8700   ref=cancel
```

Both came from **invoices that were later cancelled**. The ledger is append-only, so the original
`SALE` row stays forever and a compensating `ADJUST` returns the carats — exactly as designed, and
the net movement is zero.

But the view counts the `SALE` row in *moved out* while the cancelled invoice contributes nothing
to *sold on invoices*. **So every cancellation is reported as a discrepancy for the rest of time.**

That is a false positive in `v_reconciliation`, not a stock problem. Until the view nets off the
`ref_type = 'cancel'` adjustments — or excludes SALE rows belonging to cancelled invoices — this
dialog will keep crying wolf, and a real discrepancy will be harder to spot among the noise.

**Status: fix written, deliberately not applied.**
`supabase/migrations/0011_reconciliation_nets_cancellations.sql` nets the reversals off and was
verified against live data (0 mismatches under the new rule, 2 under the old). Reconciliation is
not in use yet, so it stays unapplied by decision — run it when the feature is needed. Until then
**"Run invariants" will keep reporting these two buckets**, which is expected, not a regression.

---

## 5 · Two things to know before you start

**These writes are permanent.** `stock_movement` is append-only, so test intakes and sales stay in
the ledger forever — cancelling reverses a sale with a compensating row, it does not erase it. Each
post also consumes a real invoice number. Fine on a dev project; do not run this pack against live
trading data.

**Negative-stock policy is `warn`.** Skip step 1 and every Post raises the shortfall dialog. That is
correct behaviour (Q10) and worth testing once deliberately — post `NO 5 / +6.5` with no stock and
confirm the warning lists the shortfall and lets you override.
