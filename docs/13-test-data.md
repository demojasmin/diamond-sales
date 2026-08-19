# 13 · Test data — every page

Manual test pack. Every figure is taken from the **real workbooks**
(`Sale File Sample.xlsx` and `stk BKC-JAN-dummy-FIXED.xlsx`, verified in
[04-workbook-verification.md](04-workbook-verification.md)), so the expected amounts are known to
the paisa — if the app disagrees, the app is wrong.

Sections 1–5 are the hand-entered pack (three invoices you type yourself). Sections 6–12 test the
pages that only make sense against the **imported** dataset, and every expected number there is
computed from the workbook, not from the app.

---

## 0 · Two datasets, and which one each test needs

**Dataset A — hand-entered.** Four intakes and three invoices you type. Sections 1–5.

**Dataset B — imported.** Settings → *Import sales from Excel* and *Import stock from Excel*.
Sections 6–12. Both imports **replace** what they imported before, so B is repeatable; A is not
(`stock_movement` is append-only).

### What dataset B contains, to the paisa

`Sale File Sample.xlsx` — 1447 rows → **1438 invoices, 1447 lines, 1057 with a receipt**,
01-08-2024 to 31-07-2026:

| | |
|---|---|
| Amount | `5,973,512,864.94` (5.97 B) |
| Received | `3,681,459,214.10` |
| Outstanding | `2,292,053,650.84` |
| Selection carats | `126,210.57` |
| Buyers created | 12 · Brokers created | 8 |
| Doc types | `BILL` 1437 · `EXPORT` 7 · `W BILL` 2 · `$ BILL` 1 |

> Received is **capped per invoice**, which is why it ends `…214.10` and not the workbook's
> `…215.18`. 248 rows carry a negative outstanding (₹-1.08 in total) from DQ-12 settlement
> rounding; the importer floors each at zero ([SaleImporter.cs:72](../DiamondDesktop/Data/SaleImporter.cs#L72)).
> **Outstanding is therefore ₹1.08 higher in the app than in the workbook. That is correct.**

`stk BKC-JAN-dummy-FIXED.xlsx` — **62 parcels, 21 grades, 2,324.3369 ct, value 94.50 M**.

### Catalogue

| | |
|---|---|
| Grades | 23 active. `+14` displays as **Plus Fourteen** |
| Sizes | `-2` · `-6.5` · `+6.5` · `+11` · `+14` · `+18` · `+23` — plus `0.2` and `0.25` |
| `0.2` / `0.25` | **Not sieve sizes.** Corrupt cells the sales workbook uses on 46 lines ([04 §4.1](04-workbook-verification.md)). They stay in `size_bucket` so the import resolves; they must **not** appear in the Sales-entry size picker |
| `grade_size` | Plus Fourteen → `+14`/`+18`/`+23` only. NO 1 and NO 1 BB → four. Everyone else → three |

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

Header — Buyer `QUEST DIAMOND` (terms 0), Broker `JITESH SHAH`, **Broker % `0`**.

> **Set Broker % to 0.** The expected amounts below are the workbook's `Sale!Q` figures, which do
> not carry a broker deduction. FR-CALC-1 puts broker % *inside* the line amount, so entering the
> broker's own default of 1% makes every figure here 1% lower — `66,57,182.38` instead of
> `67,24,426.65`, and a total of `98,55,373.16` instead of `99,54,922.39`. Both are correct; only
> one matches this table.

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
| I10 | **Over-payment is refused** — on INV-2, try to pay `5923451` | *"That is more than is owed. INV-… has … outstanding"*; nothing is written. Receipts are now capped at the outstanding balance, so the buyer's balance can no longer go negative. (Before this check existed the test expected `-0.94` to be accepted — DQ-12's residue. PAY-003 write-off is still not built; it is simply no longer reachable by over-paying.) |
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

## 6 · Page 3 · Receivables — ageing

Dataset B. Ageing computed **as at 06-08-2026**, due date = invoice date + terms:

| Bucket | Invoices | Outstanding |
|---|---:|---:|
| Not due | 55 | `200,101,900.94` |
| 1–30 | 24 | `81,345,257.94` |
| 31–60 | 21 | `68,189,194.80` |
| 61–90 | 32 | `95,230,285.54` |
| **90+** | **608** | **`1,847,187,011.19`** |
| **Total** | **740** | **`2,292,053,650.41`** |

Outstanding by buyer — the whole list, largest first:

| Buyer | Outstanding |
|---|---:|
| SHREE GEMS | `320,690,409.28` |
| KIRAN EXPORTS | `311,165,438.97` |
| ABC Company | `293,041,742.00` |
| Z K ENTERPRISE | `290,308,679.20` |
| QUEST DIAMOND | `283,013,174.71` |
| RATNA IMPEX | `279,203,160.17` |
| PARAS TRADING | `263,932,930.82` |
| M M DIAMOND | `246,915,060.71` |
| RAJ IMPEX | `2,127,229.38` |
| M K DIAMOND | `1,111,760.10` |
| FEMINA DIAM | `526,139.46` |
| PRISHA GEMS | `17,926.04` |

| # | Do this | Expect |
|---|---|---|
| R1 | Open the tab | Total matches `2,292,053,650` ± the ₹1.08 rounding note in §0 |
| R2 | Read the bucket totals | The five figures above. **82 % of the book is 90+ days** — that is the dataset, not a bug |
| R3 | Sort by Outstanding | SHREE GEMS first, PRISHA GEMS last, in the order above |
| R4 | Compare against Invoices page total | Identical. Two pages, one number — they used to disagree on the floored-negative residue |
| R5 | Double-click any cell | Nothing becomes editable |
| R6 | The four small buyers (RAJ IMPEX and below) | Present. An ageing view that drops sub-1 % buyers is hiding money |

---

## 7 · Page 4 · Stock — against dataset B

62 parcels, 2,324.3369 ct. Per grade, so you can check any row without opening Excel:

| Grade | Buckets | Carats | | Grade | Buckets | Carats |
|---|---:|---:|---|---|---:|---:|
| NO 1 | 3 | `368.3700` | | OW | 3 | `11.9688` |
| NO 1 BB | 4 | `256.3000` | | LC 1 | 3 | `16.0597` |
| NO II | 3 | `719.8310` | | LC 2 | 3 | `55.2498` |
| EX 1 | 3 | `28.7579` | | GH | 3 | `92.3916` |
| NO 2 | 3 | `18.9291` | | LB 1 | 3 | `20.0684` |
| NO DX | 3 | `12.9391` | | LB 2 | 3 | `13.7288` |
| NO 3 | 3 | `9.1591` | | **Plus Fourteen** | **3** | **`271.3885`** |
| NO 4 | 3 | `6.7092` | | EXTRA | 1 | `113.8400` |
| NO 5 | 3 | `5.9793` | | | | |
| NO 6 | 3 | `3.4895` | | | | |
| NO 7 | 3 | `28.3694` | | | | |
| TOP COL | 3 | `195.9293` | | | | |
| COLOR | 3 | `74.8790` | | **TOTAL** | **62** | **`2,324.3369`** |

| # | Do this | Expect |
|---|---|---|
| S13 | Total carats on the summary | `2,324.3369`, value `94.50 M` |
| S14 | Find **Plus Fourteen** | Exactly three buckets: `+14` `113.7895`, `+18` `124.8195`, `+23` `32.7795` |
| S15 | Count NO 1's buckets | **3, not 4.** Its `+11` holding is `-2.4e-05` ct — the workbook's own placeholder, below the 0.001 sentinel ([StockImport.cs:60](../DiamondDesktop/StockImport.cs#L60)) |
| S16 | Count NO 1 BB's | **4** — the only grade here carrying `-2` with real weight |
| S17 | EXTRA | **1 bucket**, `113.8400`. Its other two are `1e-11` and `1e-08` placeholders |
| S18 | Re-run the same import | Report says *"Previous parcels replaced 62"*, totals unchanged. No doubling |
| S19 | Any bucket sized `0.2` or `0.25` | **None.** The stock workbook never uses them |

---

## 8 · Page 5 · Intake & movements

| # | Do this | Expect |
|---|---|---|
| M1 | Intake `NO 5` `+6.5` `100` ct @ `21000` | Stock for that bucket rises by exactly 100.0000 |
| M2 | Intake with weight `0` | Refused — an intake of nothing is not a movement |
| M3 | Intake a negative weight | `-` refused at the keystroke |
| M4 | Grade **Plus Fourteen** → open Size | `+14`, `+18`, `+23` only |
| M5 | Intake Plus Fourteen `+18` `10` ct | Accepted; Stock shows `134.8195` |
| M6 | Open the movement list for that bucket | Newest first, your INTAKE at the top with today's date |
| M7 | Import stock again after M5 | **Your 10 ct is gone.** Import replaces the imported position; a hand intake on top of an imported bucket does not survive. Test this once so it is not a surprise later |

---

## 9 · Page 6 · Master data

| # | Do this | Expect |
|---|---|---|
| D1 | Grades list | 23 active. `+14` shows as **Plus Fourteen** |
| D2 | Sizes list | 9 rows — the 7 real sieves plus `0.2` and `0.25` |
| D3 | Check `0.2` / `0.25` | Visible **here** (they exist) but offered on **no** grade in Sales entry. Master data shows the table; the picker shows what is sellable |
| D4 | Buyers | 12 after the sales import, each with the commonest Terms from the file ([SaleImporter.cs:39](../DiamondDesktop/Data/SaleImporter.cs#L39)) |
| D5 | Brokers | 8. `JITESH SHAH` 372 lines, `RAJU PATEL` 370, `PARESH MEHTA` 366, `NILESH SONI` 329, then four with 1–7 lines |
| D6 | `NO BROKER` in the broker list | Present — it is a name in the file, not a null. Worth knowing before someone "cleans" it |
| D7 | Deactivate a grade, reopen Sales entry | Gone from the picker; existing invoices that use it still read correctly |

---

## 10 · Page 7 · Dashboard

Dataset B. Every tile has a known answer:

| Tile | Expect |
|---|---|
| Revenue (all time) | `5,973,512,864.94` |
| Outstanding | `2,292,053,650.84` |
| Carats sold | `126,210.57` |
| Stock on hand | `2,324.3369 ct`, `94.50 M` |
| Date range | 01-08-2024 → 31-07-2026 |

| # | Do this | Expect |
|---|---|---|
| B1 | Compare Revenue to the Invoices page total | Identical |
| B2 | Compare Outstanding to Receivables | Identical |
| B3 | Filter to a single grade | Charts narrow. Revenue is recomputed from `v_sales_line`, not scaled from the total |
| B4 | Set a range with no sales (e.g. 2020) | Empty state, not zeros-that-look-like-data |
| B5 | Margin tiles | Read `invoices_costed` **against `invoices_costable`**. All 1438 imported invoices are `MIG-`, write no stock movement, and can never be costed — [0024](../supabase/migrations/0024_margin_excludes_uncostable.sql) reports them as `invoices_uncostable` rather than as 0 % coverage |
| B6 | Post one hand-entered invoice, refresh | *That* one is costable. Coverage becomes 1 of 1, not 1 of 1439 |

---

## 11 · Page 8 · Audit

| # | Do this | Expect |
|---|---|---|
| A1 | Post an invoice, open Audit | A row for it, with your user and a timestamp |
| A2 | Cancel it | A second row. The post is **not** overwritten — the trail is append-only |
| A3 | Run a stock import | One row for the replace, naming the file and the parcel count |
| A4 | Change a setting | A row showing old → new value |
| A5 | Try to edit or delete a row | Refused. If audit can be edited it is not audit |
| A6 | Filter by date | Only that day's rows |

---

## 12 · Page 9 · Users, and Page 10 · Settings

| # | Do this | Expect |
|---|---|---|
| U1 | Open Users as `demojasmin89` (owner) | Full list, roles editable |
| U2 | Sign in as a non-owner, open Settings | **Save (Owner)** disabled; the fields read but do not write ([0021](../supabase/migrations/0021_user_admin_audit.sql)) |
| U3 | Deactivate your own account | Refused — locking yourself out of the only owner account is not a supported test |
| U4 | Settings → change *Low stock threshold* to `40`, Save | Stock page flags every bucket under 40 ct. On dataset B that is most of them |
| U5 | Settings → *Negative stock policy* = `block`, then post beyond a balance | Post refused outright, no override offered |
| U6 | Set it back to `warn`, post the same invoice | Shortfall dialog with an override |
| U7 | *Session timeout* `60` | Value survives a restart |
| U8 | Discard changes after editing three fields | All three revert; nothing was written |

---

## 5 · Two things to know before you start

**These writes are permanent.** `stock_movement` is append-only, so test intakes and sales stay in
the ledger forever — cancelling reverses a sale with a compensating row, it does not erase it. Each
post also consumes a real invoice number. Fine on a dev project; do not run this pack against live
trading data.

**Negative-stock policy is `warn`.** Skip step 1 and every Post raises the shortfall dialog. That is
correct behaviour (Q10) and worth testing once deliberately — post `NO 5 / +6.5` with no stock and
confirm the warning lists the shortfall and lets you override.
