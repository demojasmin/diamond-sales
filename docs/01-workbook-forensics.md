# 01 · Phase 1 — Workbook Forensics

Source: `Diamond_Sales_System_Requirements.html` v1.0 (2026-07-24), sections 1.1–1.5.
This is the plain-text working copy of the spec's **Phase 1 · Forensics** group, with the
findings restated for the build team and each claim given a verification status.

> ### ✅ VERIFIED against the real workbooks — 2026-07-25
>
> Both files have now been read directly. **Do not read this document alone** — read
> [04-workbook-verification.md](04-workbook-verification.md) beside it, which marks every claim
> below Verified / Incorrect / Missing / Needs Correction.
>
> **24 claims verified, 6 found incorrect, 12 things missed entirely.** The V column below records
> what each row was *before* verification (**S** = stated in the spec, **D** = derived from the
> spec's own evidence, **?** = contradiction found). Corrections since:
>
> | Correction | Detail |
> |---|---|
> | ❌ 23 grades → **22** | G1 closed |
> | ❌ Sheets "structurally identical" | Same shape, **different row anchors per sheet** |
> | ❌ Four sizes uniformly | **4 for `NO 1` / `NO 1 BB`, 3 for the other 20** — G3 closed |
> | ❌ `KAPNA ADD!J2 = SUM(block)` | It is `J2 = B15`, a direct reference |
> | ❌ DQ-6 is "float noise" | It is a **hand-rounded payment**. See §1.5 note |
> | ✅ **F-5 confirmed numerically** | `T2 = −4.0199999999999996E-6` = exactly `−'1 '!B23` |
> | 🆕 The sales↔stock link **exists** | As cell comments breaking rejection down by destination grade |

---

## 1.1 · Tab inventory

### Workbook A — `Sale File Sample.xlsx`

| Tab | Purpose | Owner | Class | Notes | V |
|---|---|---|---|---|---|
| `Sheet1` | One row per **sale line item**; an invoice spans rows sharing one `Sr.` | Sales staff | Transactional | 20 cols (A–T). **Row 2 = headers, row 1 = grand-total formulas.** 6 sample rows; template sized for ~1,000+ | S |

Two structural oddities that drive real requirements:

- **Totals live *above* the headers**, in row 1. Data starts at row 3. This is why the `SUM` ranges are hard-coded and why they rotted (DQ-1).
- **There is no invoice entity.** `Sr.` repeating down rows is the only thing binding lines together (DQ-9).

### Column map for `Sheet1` (A–T)

Reconstructed from the spec's traceability tags. This is the single most useful artifact in Phase 1
— it is what the sales-entry grid must mirror (NFR-USE-1).

| Col | Field | Kind | Notes | V |
|---|---|---|---|---|
| A | `Sr.` | Manual | Serial no. Repeats across an invoice's lines. Becomes `SalesInvoice` | S |
| B | Invoice date | Manual | Inferred: the header entity's source is `Sale · A,B,C,D,E,F,P`, and B is the only unassigned header column | **D** |
| C | Buyer | Manual | → `Buyer` master | S |
| D | Broker | Manual | → `Broker` master | S |
| E | Broker % | Manual | Used in the amount formula. **Sits on the line in Excel, promoted to the header in the model** — see finding F-4 | S |
| F | Terms (days) | Manual | Credit period → due date | S |
| G | Size | Manual | Sieve bucket | S |
| H | "Number" | Manual | The **grade** code. Note the header word is `Number`, not `Grade` | S |
| I | Weight | Manual | Gross carats offered | S |
| J | Rejection | **Computed** | `=I−K` | S |
| K | Selection | Manual | Carats accepted = the sold quantity | S |
| L | Price / ct | Manual | Rate before discounts. **L1 holds the blended rate**, not a price | S |
| M | Ex rate | Manual | Always 1 in the sample | S |
| N | Less 1 % | Manual | First discount | S |
| O | Less 2 % | Manual | Second discount, compounds on the first | S |
| P | Type | Manual | `BILL` in the sample; other values unknown (Q9) | S |
| Q | Amount | **Computed** | The whole pricing formula | S |
| R | Rec. Amt | Manual | Amount received. **One cell, overwritten** — no payment history | S |
| S | Outstanding | **Computed** | `=Q−R` | S |
| T | Remark | Manual | Free text | S |

Cross-check: the spec's manual-entry list is "Sale B–P, R, T" — which is exactly the table above
minus J, Q, S (the three computed columns) and minus A. Consistent. **D**

### Workbook B — `Blank new master file.xlsx`, 23 sheets

| Tab(s) | Purpose | Owner | Class | Notes | V |
|---|---|---|---|---|---|
| `KAPNA ADD` | Rough intake ledger (cols **A–K**) + per-grade stock reconciliation (cols **M–T**) + grand total (**row 309**) | Owner / stock mgr | Master + Derived | Grade blocks down col A; right side pulls Stock/Sale/Balance from every grade sheet and computes DIFF | S |
| `1 `, `1 BB`, `II`, `EX 1` | Top grades: NO 1, NO 1 BB, NO II, EX 1 | Stock mgr | Derived + Transactional | Stock / Rejection / Sales / Balance × sieve sizes | S |
| `NO-2 ` … `NO-7`, `NO-DX` | Lower polished grades | Stock mgr | Derived + Transactional | Structurally identical template | S |
| `TOP-COL`, `COL `, `OW` | Top-colour / colour / off-white | Stock mgr | Derived + Transactional | — | S |
| `LC-1`…`LC-3`, `GH`, `LB-1`, `LB-2`, `+14`, `EXTRA` | LC / GH / LB grades, +14 overflow, misc catch-all | Stock mgr | Derived + Transactional | `GH` has **GH-VVS sub-rows**; `+14` sparse | S |

**Count check (D).** 4 + 7 + 3 + 8 = **22 grade sheets**. Plus `KAPNA ADD` = **23 sheets**. So:

- "23 sheets" ✔ correct
- "23 quality grades" (KPI strip) ✘ wrong — that is the *sheet* count
- "23 grade blocks down col A" in the KAPNA ADD row above ✘ inconsistent with 22 sheets — **either KAPNA ADD carries a 23rd grade that has no sheet of its own, or the number is simply wrong.** Only the file settles it. **?**

This is item **G1**, now pinned down precisely rather than left as "22 vs 23".

### The one insight that shapes the whole build

> The 22 grade sheets are **instances of one template**. The app needs **one** parameterised
> grade-ledger, not 22 hand-built screens. — `IMP-1`

Consequence for Phase 2: grade is *data*, never structure. Adding a grade must be a row, not a
screen, not a migration, not a deployment (NFR-MNT-2).

### Layout facts to extract from the real file (blocking)

| # | Unknown | Why it matters | V |
|---|---|---|---|
| L1 | The row layout of one grade sheet: which rows are the Stock section, Rejection section, Sales section, Balance row | The migration parser is written against this. Known anchors so far: **B11** stock total, **B23** rejection, **B40** sales, **B44** balance | ? |
| L2 | The block layout in `KAPNA ADD` cols M–T: block start rows and stride | Grand total is `N309 = N11+N25+…+N308`. A uniform stride of 14 from row 11 does not land on 308 — `(308−11)/14 = 21.2`. So the stride is **not uniform**, or the block count differs, or a row number in the spec is a typo | **?** |
| L3 | Which sizes each grade actually uses | Q6 says II and EX use 3, NO 1 uses 4. Grade → allowed sizes is a real relationship (**G3**) | ? |
| L4 | The sub-rows (`GH-VVS`, `NO 1 MB`, `GH-VVS MA`) | Q12: distinct grades, or price/conversion rows folded into Grade + PriceList? | ? |

---

## 1.2 · Cross-tab dependency graph

Two dependency systems exist. **Within** the master file, sheets form a directed graph.
**Between** the two workbooks there is **no live link** — that gap is the project.

```
  ┌───────────────────────┐     ┌───────────────────────┐     ┌────────────────────────┐
  │ ① ROUGH INTAKE        │     │ ② GRADE SHEETS  ×22   │     │ ③ RECONCILIATION       │
  │ KAPNA ADD  A–K        │────▶│ stock rows pull rough │────▶│ KAPNA ADD  M–T         │
  │ parcels by grade+size │     │ totals + converted    │     │ reads each grade back,  │
  │ each block totalled   │     │ material ("avelu")    │     │ computes DIFF,          │
  │                       │     │ Stock/Rej/Sales/Bal   │     │ grand total row 309     │
  │ KAPNA ADD!J2=SUM(blk) │     │ '1 '!B4='KAPNA ADD'!J2│     │ KAPNA ADD!N2='1 '!B11  │
  └───────────────────────┘     └───────────┬───────────┘     └────────────────────────┘
                                            │  ▲
                                            └──┘  grade ⇄ grade conversion links
                                              (bidirectional — the fragile part)

        ══════════ NO LINK AT ALL ══════════
  Sale File Sample.xlsx ─────╳─────▶ stock never decreases
```

### Representative edges

| Edge | Formula evidence | Business meaning | V |
|---|---|---|---|
| KAPNA ADD → `'1 '` stock | `'1 '!B4 = 'KAPNA ADD'!J2` | Grade opening stock = rough intake total for that grade/size | S |
| `'1 BB'` → `'1 '` | `'1 '!B5 = '1 BB'!B19` | NO 1 stock includes material converted from NO 1 BB | S |
| 7 grades → `II` | `II!B5 = '1 '!F18` … `II!J11 = 'NO-2 '!J26` | Grade II aggregates converted material from **7** other sheets | S |
| grade → KAPNA ADD (right block) | `N2='1 '!B11` · `P2='1 '!B40` · `R2='1 '!B44` | Reconciliation reads stock / sale / balance back per grade | S |
| all blocks → grand total | `N309 = N11+N25+…+N308` | Company-wide stock / sale / balance | S |

### Finding F-1 — the coupling is bidirectional and Excel cannot see it

`'1 '!F6 = II!B21` while `II!B5 = '1 '!F18`. Different cell blocks, so Excel never raises a circular
reference — but the two sheets depend on each other. Any structural edit (insert a row, resort a
section) silently breaks a reference or creates a real cycle. This is **DQ-7**.

**Build consequence:** conversions must be modelled as **explicit, directed, dated events**
(`CONVERT_OUT` + `CONVERT_IN`), never as a cell reading another sheet. `IMP-11` → story INV-004.

### Finding F-2 — the graph is positional, not keyed

Every edge is a *cell address*. Nothing says "NO 1's stock includes conversions from NO 1 BB" —
it says "the value at B19 of that sheet". Move the row, break the meaning. This is **DQ-8/IMP-2**
territory and is why the relational model keys on `grade_id`, never on position.

---

## 1.3 · Formula catalogue

### Sales log

| Cell | Excel formula | Business rule | V |
|---|---|---|---|
| `J` Rejection | `=I3-K3` | Rejection carats = gross weight − selection | S |
| `Q` Amount | `=((((K3*L3*M3)*(100-N3)/100)*(100-O3)/100)*(100-E3)/100)` | Selection × price/ct × ex-rate, then reduced successively by Less 1 %, Less 2 %, Broker %. **Discounts compound** | S |
| `S` Outstanding | `=Q3-R3` | Invoice amount − amount received | S |
| `L1` Blended rate | `=Q1/K1` | Total amount ÷ total carats | S |
| `K1` / `Q1` / `S1` totals | `=SUM(K3:K854)` · `=SUM(Q3:Q720)` · `=SUM(S3:S659)` | Total carats / sales value / outstanding. **Three different end rows** → DQ-1 | S |

### Finding F-3 — the three SUM ranges disagree, and the damage is asymmetric

They stop at rows **854**, **720**, **659**. Once the sheet passes row 659, outstanding
under-reports first, then sales value at 720, then carats at 854. The business sees *money owed*
go wrong **before** it sees *money earned* go wrong — the least visible failure mode possible,
because a shrinking receivables total looks like good news. **D**

### Finding F-4 — broker % is a line-level field used as an invoice-level concept

The formula reads `E3` — column E, on the line's own row. Every line of an invoice therefore carries
its own broker %, and nothing in Excel forces them to agree. The domain model promotes broker % to
the **invoice header** (`SalesInvoice.broker_pct`), which is almost certainly right but **is a
change in behaviour**: if any historical invoice has differing broker % across its lines, migration
must decide what to do. Add to the migration checklist. **D**

### Inventory master — repeated patterns

| Pattern | Example | Business rule | V |
|---|---|---|---|
| Section total weight | `'1 '!B11 = SUM(B4:B10)` | Sum of weights in a section, per size | S |
| **Weighted-avg price** | `C11 = SUMPRODUCT(B4:B10*C4:C10)/B11` | `Σ(weight × price) ÷ Σweight`. **The core valuation rule, everywhere** | S |
| All-size roll-up | `R11 = B11+F11+J11+N11` | Total across the four sieve sizes → columns B, F, J, N are the four size blocks | S |
| **Balance stock** | `B44 = B11-B40-B23` | Balance = opening stock − sales − **rejection**, per grade & size | S |
| Reconciliation | `S2 = N2-P2` · `T2 = R2-S2` | Expected balance = stock − sale; DIFF must be 0 | S |
| Grand total | `N309 = N11+N25+…+N308` | Whole-business stock / sale / balance | S |

### Finding F-5 — DIFF is mathematically equal to negative rejection, never to zero ⚠

This is the most important finding in Phase 1. Substitute the spec's own cells:

```
  N2 = '1 '!B11                     stock
  P2 = '1 '!B40                     sale
  R2 = '1 '!B44 = B11 − B40 − B23   balance  (nets rejection)

  S2 = N2 − P2  = B11 − B40                          expected balance (ignores rejection)
  T2 = R2 − S2  = (B11 − B40 − B23) − (B11 − B40)
                = − B23
                = − rejection
```

**DIFF = −rejection, exactly.** It is zero only when a grade has zero rejection. The spec asserts
"DIFF must be zero; non-zero means a grade sheet is internally inconsistent" — but by its own
formulas, a non-zero DIFF is the *normal* state of any grade that has ever rejected a carat.

Three possibilities, and only the real workbook says which:

| | Reading | Consequence |
|---|---|---|
| a | The spec mis-transcribed `S2`; the real formula subtracts rejection too | DIFF is a genuine audit, spec text needs fixing |
| b | The formula is right and DIFF genuinely shows `−rejection` | It is a *rejection report* mislabelled as an audit, and the business has never had a working reconciliation check |
| c | `B44`'s `−B23` term is something other than rejection | Row anchors are wrong; L1 above must be resolved first |

**Either way, `CALC-8` as specified cannot ship.** It must become
`diff = reported_balance − derived_balance` with rejection on **both** sides. This is item **G2**,
risk **R8**, and it is the invariant on which the entire "trustworthy inventory" claim rests.
**D — provable from the spec alone, no file needed.**

### Finding F-6 — weighted average is the only averaging rule in the business

`SUMPRODUCT(weights × prices) / Σweights` appears at every level: section, size, grade, company.
There is **no** arithmetic mean anywhere. Any roll-up in the new system that averages prices by
simple mean is a bug, not a rounding difference. This is why `CALC-6` is referenced by `CALC-9`.
**D**

---

## 1.4 · Calculated fields, rollups, lookups & manual overrides

| Type | Where | Detail | V |
|---|---|---|---|
| **Calculated field** | Sale J, Q, S; every grade total & average | Fully deterministic → moves into the calc engine | S |
| **Rollup** | Grade R/S cols; KAPNA ADD blocks + row 309 | The aggregation hierarchy is **size → grade → company** | S |
| **Lookup / cross-ref** | Grade stock rows; KAPNA ADD right block | Positional cell links (`='1 BB'!B19`) — brittle, not keyed → `IMP-2` | S |
| **Manual entry** | Sale B–P, R, T; grade rejection/sales price tables; rough intake weights/prices | Human-entered. **Rec. Amt and price judgements are the true override points** | S |
| **Hardcoded judgement** | Rejection/sales unit prices typed into grade sheets (`II!C21 = 44000`) | Price lists scattered as loose constants across 22 sheets → `IMP-4` | S |

### Finding F-7 — only two things in this business are genuine human judgement

Strip out what is arithmetic and what is transcription, and the irreducible human inputs are:

1. **Price** — what a parcel is worth, per grade × size × context. Currently typed as bare
   constants into 22 sheets with no date, no author, no history. → `PriceList`, effective-dated (MDM-003).
2. **Receipt** — how much money actually arrived. Currently one overwritable cell per invoice. → `Receipt` ledger (PAY-001).

Everything else is either an entry of an observed fact (weight, selection, buyer) or a formula.
That is the whole justification for the calc-engine approach: **the machine can own all of it
except those two**, and both of those deserve history rather than a cell.

### Finding F-8 — no dates on anything in the inventory workbook

The grade sheets and their price constants carry no date column in any evidence the spec presents.
Stock is a **position**, never a **history**. That is why "inventory aging" (W12) is impossible today
and why Q5 has to ask what an aging date would even mean. The movement ledger changes stock from a
number into a dated history — which is the second-biggest structural change after the sales↔stock
link. **D**

---

## 1.5 · Data-quality risks

| # | Risk | Evidence | Impact | V |
|---|---|---|---|---|
| **DQ-1** | Inconsistent total ranges | `K1→854`, `Q1→720`, `S1→659` | Grand totals silently exclude rows past each hard-coded end → under-reported sales and receivables. See F-3 | S |
| **DQ-2** | Div-by-zero placeholder hack | `'1 '!B17 = 1e-08`, `II!B21 = 1e-12` | Near-zero weights seed empty price tables; pollute averages and totals with micro-quantities | S |
| **DQ-3** | **No link between sales log & stock** | Two separate files | Stock balances drift from reality. **The primary business problem** | S |
| **DQ-4** | Grade codes not standardised | `'1BB'` vs `'1 BB'` vs `'NO 1 BB'` | Cannot join sales to inventory at all without a canonical dictionary | S |
| **DQ-5** | Sheet names carry trailing spaces / punctuation | `'1 '`, `'NO-2 '`, `',+6.5'` | Fragile references, error-prone entry | S |
| **DQ-6** | ~~Rounding / float noise~~ **Misdiagnosed** | `S3 = -0.2749…` | ❌ **Incorrect.** `R3 = 139865` was typed as a round figure against `Q3 = 139864.725`. This is a real ₹0.275 settlement difference, not float error. Needs a write-off threshold, not decimal precision | ❌ |
| **DQ-11** 🆕 | **Sieve codes have four incompatible notations** | Sale `11+`,`6.5-`,`0.2`(numeric) vs grade `,+6.5` vs KAPNA ` +6.50   ` | Sales cannot be joined to inventory on size, exactly as DQ-4 blocks joining on grade. Needs a size alias table | 🆕 |
| **DQ-12** 🆕 | **Settlement rounding leaves phantom balances** | `S3` above | Every hand-rounded payment leaves a permanent residue in receivables | 🆕 |
| **DQ-13** 🆕 | **The sales↔stock link exists — as cell comments** | `J4`,`J6`,`J7` comments | Rejection is split by destination grade in free text, re-keyed by hand into grade sheets. Worse than no link: an undocumented manual one | 🆕 |
| **DQ-7** | Bidirectional grade coupling | `'1 '!F6=II!B21` ⇄ `II!B5='1 '!F18` | Structural edits risk broken references or circularity. See F-1 | S |
| **DQ-8** | Merged cells as form layout | Every grade sheet | Presentational only — must be **dropped**, not migrated | S |
| **DQ-9** | Multi-line invoices keyed only by repeated `Sr.` | `A6 = A7 = A8 = 3` | No invoice entity; header data repeats per row and can diverge. See F-4 | S |
| **DQ-10** | Ex-rate & broker % constant in sample | `M = 1`, `E = 1` | Multi-currency / broker-deal behaviour unverified → Q4 / Q7 | S |

### DQ severity, re-ranked for the build

The spec lists these flat. They are not equal:

| Tier | Items | Why |
|---|---|---|
| **Structural** — the system exists to fix these | DQ-3, DQ-9, DQ-7 | Missing links and missing entities. Cannot be patched in Excel; require the new model |
| **Silent-wrong** — nobody knows the numbers are wrong | DQ-1, DQ-2, DQ-6 | These produce plausible figures that are simply incorrect. **Migration must not carry them forward — recompute, never copy** |
| **Blocking-for-migration** | DQ-4, DQ-5 | Until grade codes are canonicalised, no sale can be joined to any stock row. First task in the migration sequence |
| **Cosmetic-only** | DQ-8 | Drop on import; no rule attached |
| **Unverified** | DQ-10 | Sample size of one value. Needs the client, not the file |

### Finding F-9 — one risk the spec's own DQ list misses

**Row 1 holds the totals, above the row-2 headers.** Every DQ item treats the sheet as data with a
totals row; nothing flags that the totals are *above* the data and therefore had to be given fixed
end-rows by hand — which is the direct cause of DQ-1. Fix the layout assumption and DQ-1 could not
have happened. Recorded here so the migration parser does not read row 1 as a data row. **D**

---

## Phase 1 output — what the rest of the build inherits

| Forensic finding | Becomes |
|---|---|
| One grade-sheet template ×22 | One parameterised ledger · `IMP-1` · MDM-001, INV-002 |
| Positional cross-sheet links | Keyed FKs + explicit conversion events · `IMP-2`, `IMP-11` · INV-004 |
| Sale J / Q / S formulas | `CALC-1`, `CALC-2`, `CALC-3` |
| `SUMPRODUCT` weighted average | `CALC-6`, and `CALC-9` roll-ups |
| `B44` balance | `CALC-7`, as a signed sum over a movement ledger |
| DIFF column | `CALC-8` — **but corrected first, see F-5** |
| Scattered price constants | `PriceList`, effective-dated · MDM-003 |
| Single `Rec. Amt` cell | `Receipt` ledger · PAY-001 |
| Repeated `Sr.` | `SalesInvoice` header entity · `IMP-5` |
| No dates on stock | Dated `StockMovement` history |

## Open items this phase leaves behind

| ID | Item | Blocks |
|---|---|---|
| **G1** | 22 grade sheets vs "23 grade blocks" in KAPNA ADD col A — is there a 23rd grade with no sheet? | Grade master seeding |
| **G2 / F-5** | DIFF = −rejection. `CALC-8` cannot ship as written | The entire reconciliation invariant |
| **G3 / L3** | Grade → allowed sizes is a real relationship, not a global list of four | Data model, validation rule BR-VAL-5 |
| **G4** | Workbooks absent — nothing above is verified | All of it |
| **L1** | Grade-sheet row layout (Stock / Rejection / Sales / Balance sections) | Migration parser |
| **L2** | KAPNA ADD block stride — `N11+N25+…+N308` is not a uniform 14-row step | Migration parser |
| **L4** | Sub-rows `GH-VVS`, `NO 1 MB`, `GH-VVS MA` — grades or price rows? (Q12) | Grade master |
| **F-4** | Broker % is per-line in Excel, per-invoice in the model. What if historical lines disagree? | Migration rule |

---

Next: [02-requirement-analysis.md](02-requirement-analysis.md) — functional and non-functional
requirements, roles, workflows, business rules, and the pre-development checklist built on top of
these findings.
