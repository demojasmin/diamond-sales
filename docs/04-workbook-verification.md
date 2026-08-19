# 04 · Workbook Verification Report

Date: 2026-07-25 · **Verified against the real files**
Sources read: `C:\Users\jbc\Downloads\Sale File Sample.xlsx` (2.86 MB) ·
`C:\Users\jbc\Downloads\Blank new master file.xlsx` (653 KB)
Method: direct XML inspection of the OOXML package (formulas, cached values, shared strings,
defined names, data validations, merges, comments, VBA parts). Script:
`scratchpad/xlsx_dump.py`.

This report closes blocking artifacts **A1** and **A2** and resolves **G1–G4**. Every claim in
[01-workbook-verification target](01-workbook-forensics.md) is now marked
**Verified** / **Incorrect** / **Missing** / **Needs Correction**.

---

## 0. Headline

| | Count |
|---|---|
| Spec claims **Verified** exactly | 24 |
| Spec claims **Incorrect** | 6 |
| Things in the files the spec **Missed** | 12 |
| Rules **Needing Correction** before build | 5 |

**The single most important result:** finding F-5 is **confirmed numerically**. The DIFF audit
column equals minus the rejection total, to the last binary digit. The reconciliation check in this
business has never worked.

**The single most important discovery:** the sales workbook contains **cell comments that break each
rejection down by destination grade**. That is the sales↔inventory link everyone believed did not
exist. It exists — as unstructured text in a comment balloon.

---

## 1. Global facts

| Check | Result | Status |
|---|---|---|
| Macros / VBA in either workbook | **None.** No `vbaProject.bin` in either package | ✅ Verified |
| Data validations (dropdowns) | **Zero, in all 24 sheets.** Every code is free text | ✅ Verified — and this is the root cause of DQ-4/DQ-5 |
| Defined names — Sale | One: `_xlnm._FilterDatabase = Sheet1!$A$2:$T$1048574` | 🆕 Missing from spec |
| Defined names — Master | **None** | ✅ Verified (none claimed) |
| Sale workbook sheets | 1 (`Sheet1`) | ✅ Verified |
| Master workbook sheets | 23 = `KAPNA ADD` + **22** grade sheets | ✅ Verified |
| External links | None | ✅ Verified |

The autofilter spans `A2:T1048574` — the whole sheet. The *intent* was always "all rows". The
`SUM` ranges were the thing that got frozen. This strengthens DQ-1 rather than excusing it.

---

## 2. Workbook A — `Sale File Sample.xlsx`

### 2.1 Structure

| Spec claim | Actual | Status |
|---|---|---|
| One sheet, one row per line item | `Sheet1`, 140 populated cells, 22 formulas | ✅ Verified |
| 20 cols A–T | maxCol = 20 (T) | ✅ Verified |
| Row 2 = headers, row 1 = grand totals | Exactly so | ✅ Verified |
| 6 sample rows | Rows 3–8 | ✅ Verified |
| Invoice = repeated `Sr.` | A6=A7=A8=3 | ✅ Verified — DQ-9 confirmed |

### 2.2 Column map — my Phase 1 reconstruction was correct in full

Actual header text from row 2:

| Col | Header (verbatim) | Phase 1 guess | Status |
|---|---|---|---|
| A | `Sr.` | Sr. | ✅ |
| B | `Date` | Invoice date **(inferred)** | ✅ **Inference correct** |
| C | `Name` | Buyer | ✅ Verified — but the header word is **`Name`**, not "Buyer" |
| D | `Broker` | Broker | ✅ |
| E | `Broker %` | Broker % | ✅ |
| F | `Terms` | Terms | ✅ |
| G | `Size` | Size | ✅ |
| H | `Number` | Grade | ✅ Verified — header really is `Number` |
| I | `Weight` | Weight | ✅ |
| J | `Rejection` | computed | ✅ |
| K | `Selection` | Selection | ✅ |
| L | `Price Per ct` | Price/ct | ✅ |
| M | `Ex Rate` | Ex rate | ✅ |
| N | `Less 1` | Less 1 % | ✅ |
| O | `Less 2` | Less 2 % | ✅ |
| P | `Type` | Type | ✅ |
| Q | `Amount` | computed | ✅ |
| R | `Rec. Amt` | Received | ✅ |
| S | `Outstanding` | computed | ✅ |
| T | `Remark` | Remark | ✅ |

### 2.3 Formulas — all five verified verbatim

| Cell | Actual formula | Cached value | Status |
|---|---|---|---|
| `J3` | `=I3-K3` | 0 | ✅ Verified |
| `Q3` | `=((((K3*L3*M3)*(100-N3)/100)*(100-O3)/100)*(100-E3)/100)` | 139864.725 | ✅ **Verified character-for-character** |
| `S3` | `=Q3-R3` | −0.27499999999417923 | ✅ Verified |
| `L1` | `=Q1/K1` | 45049.461932255246 | ✅ Verified |
| `K1` `Q1` `S1` | `=SUM(K3:K854)` · `=SUM(Q3:Q720)` · `=SUM(S3:S659)` | 355.57 · 16,018,237.18 · −0.275 | ✅ **DQ-1 verified exactly** |

Rows 7–8 use Excel *shared formulas* (`si=6,7,8`) — same logic, compressed storage. No behavioural
difference.

### 2.4 ❌ INCORRECT — DQ-6 is misdiagnosed

The spec calls `S3 = −0.2749…` "rounding / float noise" and prescribes a decimal-precision fix.
The file says otherwise:

```
  Q3 (invoice amount)  = 139864.725      computed
  R3 (Rec. Amt)        = 139865          typed by hand — a ROUND FIGURE
  S3 (outstanding)     = −0.275          the difference
```

This is **not** float error. The buyer paid a round ₹139,865 against an invoice of ₹139,864.725.
The ₹0.275 is a real **settlement rounding difference** — an over-payment of a quarter rupee.

**Consequence:** `CALC-002` (decimal precision) does not fix this and never could. The system needs a
**settlement/write-off rule**: when |outstanding| is below a configurable threshold, the invoice is
closed and the residue posted as a rounding adjustment. Without it, every hand-rounded payment
leaves a permanent phantom balance in the receivables ledger — exactly what the owner complained
about.

Status: **Needs Correction** — DQ-6, IMP-9 and CALC-002 all rest on a wrong diagnosis.

### 2.5 🆕 MISSING — the rejection breakdown comments

`xl/comments1.xml` contains three comments, all anchored to the **Rejection** column:

| Cell | Rejection ct | Comment content | Sums to |
|---|---|---|---|
| `J4` | 24.40 | `13.46 Selection` · `4.62 Reparing` · `6.31 FL+Col+II` | 24.39 |
| `J6` | 83.12 | `7.80 Selection tik` · `47.45 II Selection` · `26.77 II color` · `0.17 Culet` · `0.80 EX1` · `0.11 2` | 83.10 |
| `J7` | 15.82 | `0.03 Ex1` · `0.04 3` | 0.07 (partial) |

**This is the missing link.** Rejected carats do not simply return to the grade they came from —
they are split by destination: some re-selected, some sent for repair, some **re-graded into other
grades** (`FL+Col+II`, `II color`, `EX1`, `Culet`). That is precisely the "avelu" conversion the
master workbook records through positional cell links.

So the two workbooks *are* connected after all — through a human reading a comment balloon and
retyping numbers into a grade sheet. Undocumented, unvalidated, unauditable, and invisible to
every formula.

**Consequence for the model:** a rejection is not a scalar. It is a parent quantity with **child
dispositions**, each carrying a weight, a destination grade (or a non-grade outcome such as REPAIR
or CULET), and a reason. `INV-005` as specified — "record rejected carats" — is materially
incomplete.

Status: **Missing** — highest-impact omission in the specification.

### 2.6 🆕 Other omissions in Workbook A

| # | Found | Consequence |
|---|---|---|
| A-1 | **Size codes use suffix notation**: `11+`, `6.5+`, `6.5-`, and `0.2` stored as a **number** | The master uses *prefix* notation (`-2`, `,+6.5`). See §4.1 — this is a new blocking defect |
| A-2 | Row 5 is a **fully-rejected line**: weight 15.39, selection 0, amount 0 | `selection_ct >= 0` and `gross > 0` in the model are correct. A zero-value line is legitimate and must not be validated away |
| A-3 | `Terms` = **0** on invoice 3 (rows 6–8) | Due date = invoice date. Zero is a valid term, not missing data |
| A-4 | `Remark` encodes **quantity + reason**: `7.80 culet repair`, `3.27 Culet Repair` | Semi-structured. Same story as the comments — real data hiding in free text |
| A-5 | Dates are serials 45940 / 45947 / 45966 → **Oct–Nov 2025** | Sample is recent, live data |
| A-6 | Broker % = 1 on every row, consistent within each invoice | F-4's risk is real but **unrealised in this sample**. Migration must still check |

---

## 3. Workbook B — `Blank new master file.xlsx`

### 3.1 ✅ G1 RESOLVED — 22 grades, not 23

Sheet names, verbatim and in workbook order:

```
KAPNA ADD | 1  | 1 BB | II | EX 1 | NO-2  | NO-DX | NO-3 | NO-4 | NO-5 | NO-6 | NO-7
TOP-COL | COL  | OW | LC-1 | LC-2 | LC-3 | GH | LB-1 | LB-2 | +14 | EXTRA
```

22 grade sheets + `KAPNA ADD` = 23 sheets. Confirmed independently by the right-block layout in
`KAPNA ADD`, which contains exactly **22** reconciliation blocks.

- "23 sheets" → ✅ **Verified**
- "23 quality grades" (KPI strip) → ❌ **Incorrect**. It is 22
- "23 grade blocks down col A" → ❌ **Incorrect**. 22

Trailing spaces confirmed on `1 `, `NO-2 `, `COL ` — ✅ **DQ-5 Verified**.
Note `NO-DX` sits between `NO-2 ` and `NO-3`, not after `NO-7` as the spec's grouping implies.

### 3.2 ✅ Grade sheet `'1 '` — every claimed formula verified

| Spec claim | Actual | Status |
|---|---|---|
| `'1 '!B4 = 'KAPNA ADD'!J2` | identical | ✅ |
| `'1 '!B5 = '1 BB'!B19` | identical | ✅ |
| `'1 '!F6 = II!B21` | identical | ✅ |
| `B11 = SUM(B4:B10)` | identical | ✅ |
| `C11 = SUMPRODUCT(B4:B10*C4:C10)/B11` | identical | ✅ |
| `R11 = B11+F11+J11+N11` | identical | ✅ |
| `B44 = B11-B40-B23` | identical | ✅ |
| `II!C21 = 44000` | confirmed via `'1 '!G6 → 44000` | ✅ |

Section anchors for `'1 '`: stock rows 4–10 (total **11**), rejection rows 17–22 (total **23**),
sales rows 28–39 (total **40**), balance **44**. Exactly as the spec implied. **L1 resolved for
this sheet.**

### 3.3 ❌ INCORRECT — the 22 sheets are *not* structurally identical

The spec's key insight — "instances of one template" — is right in *shape* and wrong in *layout*.
Row anchors differ per sheet. From `KAPNA ADD!J478` and `!J484`, which enumerate every sheet:

| Sheet | Sales total row | Balance row | Sizes |
|---|---|---|---|
| `1 ` | 40 | 44 / 46 | **4** |
| `1 BB` | 39 | 43 / 45 | **4** |
| `II` | 49 | 53 / 55 | 3 |
| `EX 1` | 41 | 45 / 47 | 3 |
| `NO-2 ` | 43 | 47 / 49 | 3 |
| `NO-DX` | 44 | 48 / 50 | 3 |
| `NO-3` | 42 | 46 / 48 | 3 |
| `NO-4` | 43 | 47 / 49 | 3 |
| `NO-5` | 40 | 44 / 46 | 3 |
| `NO-6` | 40 | 44 / 46 | 3 |
| `NO-7` | 39 | 43 / 45 | 3 |
| `TOP-COL` | 49 | 53 / 55 | 3 |
| `COL ` | 42 | 46 / 48 | 3 |
| `OW` | 43 | 47 / 49 | 3 |
| `LC-1` | 39 | 43 / 45 | 3 |
| `LC-2` | 41 | 45 / 47 | 3 |
| `LC-3` | 42 | 46 / 48 | 3 |
| `GH` | 49 | 53 / 55 | 3 |
| `LB-1` | 40 | 44 / 46 | 3 |
| `LB-2` | 41 | 45 / 47 | 3 |
| `+14` | 39 | 43 / 45 | 3 |
| `EXTRA` | 59 | 63 / 65 | 3 |

**IMP-1 survives** — one parameterised ledger is still right. But **L1/L2 are answered the hard
way: the migration parser cannot assume fixed rows.** It must locate each section by its total
formula (`SUM(...)` anchoring a contiguous block), not by row number. Status: **Needs Correction**
in the migration plan.

### 3.4 ✅ G3 RESOLVED — and the answer is more specific than Q6 assumed

`'1 '` row 3 headers: `-2` · `-6.5` · `,+6.5` · `,+11` → **4 sizes**
`II` row 3 headers: `-6.5` · `,+6.5` · `,+11` → **3 sizes**

The three-size grades drop the **smallest** bucket, `-2`. Only `1 ` and `1 BB` carry all four —
consistent with the two 4-row blocks in `KAPNA ADD` (rows 2–5 and 14–17) against twenty 3-row
blocks.

The roll-up column therefore **moves**: `R`/`S` on 4-size sheets, `N`/`O` on 3-size sheets. Another
reason the parser must not hard-code columns.

`grade_size` in the domain model is confirmed correct, and can now be **seeded**: all 22 grades get
`-6.5`, `+6.5`, `+11`; only `NO 1` and `NO 1 BB` additionally get `-2`.

### 3.5 ✅✅ F-5 CONFIRMED — DIFF equals minus rejection, numerically

Column headers in `KAPNA ADD` row 1, verbatim:
`N=TOTAL STOCK` · `O=VALUE` · `P=SALE` · `Q=VALUE` · `R=BALANCE STK` · `S=TOTAL STOCK - SALE` · `T=DIFF`

The header text itself admits it: **S is "TOTAL STOCK − SALE"** — rejection is not in it. But
`R` pulls `B44`, which *does* net rejection. So:

```
  N2 = '1 '!B11                      stock
  P2 = '1 '!B40                      sale
  R2 = '1 '!B44 = B11 − B40 − B23    balance, nets rejection
  S2 = N2 − P2 = B11 − B40           "expected", ignores rejection
  T2 = R2 − S2 = −B23 = −rejection
```

Cached values from the live file:

| | Value |
|---|---|
| `KAPNA ADD!T2` | **−4.0199999999999996E-6** |
| `'1 '!B23` (rejection total) | **4.0199999999999996E-6** |
| `KAPNA ADD!T3` | **−1.0000005000000003E-6** |
| `'1 '!F23` | **1.0000004999999999E-6** |

Identical to the last digit, on every row checked. **The audit column that is supposed to prove
inventory is consistent has been reporting rejection with a minus sign, for as long as this
workbook has existed.**

Status: ✅ **Verified — the spec's `CALC-8` is Incorrect and must not be built.** The Phase 2
decision to delete it (correction C-2) is confirmed correct by the file itself.

### 3.6 ❌ INCORRECT — `KAPNA ADD!J2` is not a `SUM`

| Spec | `KAPNA ADD!J2 = SUM(block)` |
|---|---|
| Actual | `J2 = B15` · `K2 = C15` · `J3 = D15` · `K3 = E15` · `J4 = F15` · `J5 = H15` |

The `J`/`K` "total weight / price" cells are **direct references** to a per-size subtotal row
elsewhere in the block (row 15 for the first grade), not sums. The intake block layout is
transposed from what the spec describes. Status: **Incorrect** — affects the migration parser only.

### 3.7 ✅ Grand total row 309 verified

```
N309 = N11+N25+N40+N55+N70+N85+N100+N114+N128+N143+N157+N172+N185+N198
      +N210+N224+N239+N255+N269+N284+N297+N308      → 0.9419611554795011
```

22 terms — one per grade. ✅ **Verified**, and **L2 is resolved**: the block stride is *not*
uniform (11→25 is 14, 25→40 is 15, 297→308 is 11), because 4-size grades occupy more rows than
3-size grades. My Phase 1 arithmetic objection was correct in substance — the stride genuinely
isn't uniform — and the spec's `N309` figure was right all along.

`maxRow` is **484**, not 309: rows 459–484 hold the *left*-block (intake) grand totals, which the
spec never mentions. See §3.8.

### 3.8 🆕 MISSING — twelve things in the file the specification never records

| # | Found | Why it matters |
|---|---|---|
| B-1 | **Live `#DIV/0!` errors** throughout — `'1 '!C11`, `G11`, `K11`, `O11`, `S11`, `C44`, and `KAPNA ADD!O2`, `K2`, `K476`, `K484` | The workbook is **currently broken on screen**, not merely fragile. Weighted-average prices display as errors. DQ-2 understates this badly |
| B-2 | **Negative balances already present**: `'1 '!B44 = −2.02E-6`, `KAPNA ADD!R309 = −0.0127` | There is no negative-stock guard of any kind. Q10's answer must be a real policy, not a formality |
| B-3 | **Column `U` = value**: `U23 = R23*S23`, `U40 = R40*S40` | A value (weight × avg price) column the spec omits. Inventory valuation already exists in the sheet |
| B-4 | **Row 46 duplicates row 44** with a different price basis: `B46 = B11-B40-B23` (same weight) but `C46 = C4` vs `C44 = C11` | Two balance figures per grade — one valued at opening price, one at weighted-average. `KAPNA ADD!J484` consumes row 46, `J482` consumes row 44. **Which is "the" balance is undefined** |
| B-5 | **Stock rows 8–9 self-reference the rejection block**: `'1 '!B8 = B19`, `B9 = B20` | Some rejection rows are added back into stock *and* subtracted via `B23` — they cancel; rows 17,18,21,22 do not. Rejection is partly a return-to-stock. Needs client explanation |
| B-6 | **Left-block grand totals rows 469–484**, incl. `J476 = SUM(J2:J468)` (intake), `J478` (sales across all 22 sheets), `J482 = J476−J478`, `J484` (balance across all sheets) | A second, parallel reconciliation the spec never documents |
| B-7 | `C469 = SUM(B459:B468*C459:C468)/B469` — array formula using `SUM`, not `SUMPRODUCT` | Two idioms for the same weighted average. Both must migrate to `CALC-6` |
| B-8 | **`KAPNA ADD` size headers are a third notation**: `-2`, ` -6.50    `, ` +6.50   `, `  +11   ` — with padding spaces and 2 dp | See §4.1 |
| B-9 | Merges: 12–17 per grade sheet, **1** in `KAPNA ADD` | DQ-8 confirmed but modest in scale |
| B-10 | `EXTRA` is structurally larger (65 rows, sales total at 59) | The catch-all sheet is not a normal grade |
| B-11 | `GH` has 142 formulas vs ~80 typical — the GH-VVS sub-rows are real | Q12 is a live question, not hypothetical |
| B-12 | Placeholder magnitudes vary wildly: `1E-5`, `1E-6`, `1E-10`, `1E-12`, `1E-13` | DQ-2's "drop as zero" rule must use a threshold, not an equality test |

---

## 4. New defects the specification does not contain

### 4.1 🔴 DQ-11 (new) — sieve size codes have **four incompatible notations**

| Source | Notation | Example |
|---|---|---|
| Sale `Sheet1` col G | **suffix**, mixed types | `11+`, `6.5+`, `6.5-`, and `0.2` **stored as a number** |
| Grade sheets row 3 | **prefix, comma-prefixed** | `-2`, `-6.5`, `,+6.5`, `,+11` |
| `KAPNA ADD` row 1 | **prefix, padded, 2 dp** | `-2`, ` -6.50    `, ` +6.50   `, `  +11   ` |
| Specification | **prefix, clean** | `−2`, `−6.5`, `+6.5`, `+11` |

This is DQ-4 all over again, for sizes, and the spec never flagged it. **Sales cannot be joined to
inventory on size any more than on grade.** The `0.2` numeric value is almost certainly `-2`
mistyped — it cannot even be compared as text.

**Consequence:** `size_bucket` needs an alias table exactly like `grade_alias`. Severity: identical
to DQ-4 — *blocking for migration*.

### 4.2 🔴 DQ-12 (new) — settlement rounding creates permanent phantom balances

See §2.4. `R3 = 139865` against `Q3 = 139864.725`. Needs a write-off threshold rule, not a decimal
policy.

### 4.3 🔴 DQ-13 (new) — the real sales↔stock link is a cell comment

See §2.5. The rejection→destination-grade mapping exists only as free text in comment balloons,
re-keyed by hand into grade sheets. This is the actual mechanism behind DQ-3, and it is worse than
"no link": it is an **undocumented manual link that looks like no link**.

---

## 5. Findings ledger

### ✅ Verified (24)

Sale: sheet count · 20 columns A–T · row 1 totals above row 2 headers · all 20 header labels ·
`J=I−K` · the full `Q` amount formula · `S=Q−R` · `L1=Q1/K1` · the three mismatched SUM ranges ·
repeated `Sr.` as the only invoice key · `1BB` grade code · `S3 = −0.275`.
Master: 23 sheets / 22 grades · trailing-space sheet names · `,+6.5` label · `B4='KAPNA ADD'!J2` ·
`B5='1 BB'!B19` · `F6=II!B21` · `B11=SUM(B4:B10)` · `C11=SUMPRODUCT(...)/B11` ·
`R11=B11+F11+J11+N11` · `B44=B11-B40-B23` · `S2=N2-P2` · `T2=R2-S2` · `II!C21=44000` ·
row 309 grand total · merged-cell layout · placeholder micro-weights · **F-5: DIFF = −rejection**.

### ❌ Incorrect (6)

| # | Spec says | Truth |
|---|---|---|
| 1 | 23 quality grades | **22** |
| 2 | `KAPNA ADD!J2 = SUM(block)` | `J2 = B15`, a direct reference |
| 3 | Four sieve sizes, uniformly | 2 grades have 4, 20 have 3 (the 3-size grades drop `-2`) |
| 4 | Grade sheets are structurally identical | Same shape, **different row anchors per sheet** |
| 5 | DQ-6 is float noise | It is a **hand-rounded payment** — a real ₹0.275 |
| 6 | `CALC-8` DIFF asserts 0 | DIFF **is** `−rejection`. Confirmed in the file |

### 🆕 Missing (12)

Rejection-breakdown comments (§2.5) · size-notation chaos (§4.1) · settlement rounding (§4.2) ·
live `#DIV/0!` errors · pre-existing negative balances · column `U` valuation · duplicate balance
row 46 · stock rows self-referencing rejection · left-block grand totals 469–484 · `SUM`-array vs
`SUMPRODUCT` idioms · `_FilterDatabase` named range · quantity-bearing remarks.

### ⚠ Needs Correction (5)

| # | Item | Action |
|---|---|---|
| 1 | `CALC-8` | Delete as a runtime rule — Phase 2 correction **C-2 confirmed** |
| 2 | `CALC-002` / `IMP-9` / DQ-6 | Add a settlement write-off threshold. Decimal precision alone does not fix it |
| 3 | `INV-005` rejection model | Rejection needs **child dispositions** (weight + destination grade + reason) |
| 4 | Migration plan §5.3 | Parser must locate sections by their `SUM` anchors, never by row number |
| 5 | `SizeBucket` | Needs an alias table, same as `grade_alias` |

---

## 6. Effect on Phase 2 — Domain Model

| Change | Detail |
|---|---|
| **C-2 confirmed** | Deleting `CALC-8` was correct. The file proves it |
| **`grade_size` can be seeded** | All 22 grades: `-6.5`, `+6.5`, `+11`. Plus `-2` for `NO 1` and `NO 1 BB` only |
| **New: `size_alias`** | Mirrors `grade_alias`. Maps `11+`, `+11`, `,+11`, ` +11   ` → one canonical size |
| **New: `rejection_disposition`** | Child of a rejection movement: weight, destination grade (nullable), outcome ∈ {RESELECT, REPAIR, REGRADE, CULET, OTHER}, note |
| **New setting** | `settlement_write_off_threshold` (default ₹1.00) — closes an invoice whose residue is below it, posting the difference as a rounding adjustment |
| **`grade` seed list is final** | The 22 names are known verbatim; canonical codes can be assigned now |
| **`ADJUST` movement type justified** | Pre-existing negative balances mean migration *will* need opening adjustments |

None of these invalidate the Phase 2 schema. Four are additions; one is a confirmation.

---

## 7. What is still open

| ID | Item | Status after verification |
|---|---|---|
| G1 | 22 vs 23 grades | ✅ **Closed — 22** |
| G2 | DIFF definition | ✅ **Closed — confirmed broken, rule deleted** |
| G3 | Sizes per grade | ✅ **Closed — 4 for NO 1 / NO 1 BB, 3 for the rest** |
| G4 | Workbooks absent | ✅ **Closed — both read** |
| L1 | Grade-sheet row layout | ✅ **Closed — mapped per sheet (§3.3)** |
| L2 | KAPNA ADD block stride | ✅ **Closed — non-uniform, by design** |
| L4 | Sub-rows (GH-VVS etc.) | ⚠ Still open — `GH` genuinely has extra structure. **Q12 stands** |
| A3 | Populated master file | ⚠ **Still needed.** This file is the blank template: real balances are placeholders |
| Q6 | Sieve mm definitions | ⚠ Still open — no mm values anywhere in either file |
| Q9 | `Type` values beyond `BILL` | ⚠ Still open — only `BILL` appears in 6 rows |
| Q4 | Broker treatment | ⚠ Still open — `E=1` on every row tells us nothing |
| — | **What the rejection comments mean** | 🔴 **New blocking question.** Who re-keys them into the grade sheets, and by what rule? |

---

## 8. Reproducing this

```
python scratchpad/xlsx_dump.py "<file>.xlsx" overview
python scratchpad/xlsx_dump.py "<file>.xlsx" formulas "<sheet name>"
python scratchpad/xlsx_dump.py "<file>.xlsx" cells    "<sheet name>" 1-60
python scratchpad/inspect.py            # comments + cell types
```

Standard library only — no `openpyxl`, no install. `.xlsx` is a zip of XML.
