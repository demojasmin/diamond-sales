# 00 · Overview

Source: `Diamond_Sales_System_Requirements.html` v1.0 (2026-07-24).
This file is the plain-text working copy of the spec's **Overview** group —
Executive Summary, How to read, Domain glossary. Phase 1 onward is not covered here.

---

## The business

A diamond trading business runs its entire sales operation from two Excel workbooks.
They are **not linked**, and that gap is the whole reason this project exists.

| | Workbook | Shape |
|---|---|---|
| **A** | `Sale File Sample.xlsx` | 1 sheet. One row per sale line item. An invoice spans several rows sharing a serial no (`Sr.`). 20 cols (A–T), row 2 = headers, row 1 = grand totals. |
| **B** | `Blank new master file.xlsx` | 23 sheets = `KAPNA ADD` (rough intake ledger + reconciliation) + 22 per-grade sheets, all instances of one template. |

**Workbook A** — sales staff type buyer, broker, grade, weight, selection (accepted carats),
rate, discounts. Excel computes rejection, invoice amount, outstanding.

**Workbook B** — rough intake by grade and size, then per grade:
Stock → Rejection → Sales → Balance across four sieve sizes. Rolls up into `KAPNA ADD`,
where a `DIFF` column audits that balance = stock − sale.

### The core problem

> A sale recorded in workbook A is **never** automatically deducted from the grade sheet's
> stock in workbook B. Reconciliation is entirely manual, so inventory balances drift from
> reality and the owner cannot trust the numbers.

Everything else in the spec is downstream of closing that gap.

### Target state

- **Windows desktop** (sales staff) — keyboard-first entry, live calcs, automatic stock deduction on post.
- **Android** (owner) — dashboards, receivables, inventory aging. Read-mostly.
- **Shared backend** — one deterministic calculation engine replacing all cross-sheet formulas, plus audit trail and offline multi-user sync.

### Scale of the spec

| Count | What |
|---|---|
| 2 | workbooks reverse-engineered |
| 22 | grade sheets (see open item G1 below) |
| 4 | sieve sizes: `−2`, `−6.5`, `+6.5`, `+11` |
| 11 | deterministic calc rules (CALC-1…11) |
| 10 | data-quality risks (DQ-1…10) |
| 28 | user stories across 12 epics |
| 3 | delivery phases (MVP → Phase 3) |

---

## How to read the spec

| Convention | Meaning |
|---|---|
| `Sale · Sheet1 · Q` | Traceability tag — the exact workbook / sheet / cell a requirement came from. |
| `IMP-n` | An improvement on the current Excel process. Listed separately (Appendix A) so the client can approve or reject each one independently. |
| `CALC-n` | A rule in the calculation engine (§2.3). Stories reference these instead of repeating formulas. |
| Must / Should / Could | MoSCoW priority. Effort in Fibonacci story points. |
| `Qn` | Open question (Appendix B). Each carries the assumption used in the spec. No business rule was invented. |

---

## Domain glossary

Trade vocabulary, taken directly from the workbooks. Read this before any other section.

| Term | Meaning in this business | Source |
|---|---|---|
| **Kapna** | Rough (uncut) diamond parcel as it arrives. "KAPNA ADD" = adding rough intake to stock. | Master · KAPNA ADD |
| **Grade / quality** | A trade code bundling colour + clarity + make into one label (NO 1, NO II, EX 1, TOP-COL, GH, LC-1…). Each has its own worksheet. | Master · tab names; Sale · H "Number" |
| **Sieve size** | Physical size bucket from sieving: `−2`, `−6.5`, `+6.5`, `+11`. `+` = retained on that mesh (bigger); `−` = passed through (smaller). | Master · size headers; Sale · G |
| **Weight** | Gross carats of the parcel offered. | Sale · I |
| **Selection** | Carats the buyer actually accepted — the sold quantity. | Sale · K |
| **Rejection** | Carats not accepted = Weight − Selection. | Sale · J |
| **Rate / Price per ct** | Price per carat before discounts. | Sale · L |
| **Less 1 / Less 2** | Two successive percentage discounts on the line. They **compound**. | Sale · N, O |
| **Broker %** | Broker's percentage. In the sheet it is deducted from the amount; exact accounting treatment is open (Q4). | Sale · E |
| **Terms** | Credit period in days before payment is due. | Sale · F |
| **Ex Rate** | Exchange rate multiplier. Always 1 in the sample — base currency INR. | Sale · M |
| **Avelu** ("…ma thi avelu") | Gujarati "came from" — material re-sorted from one grade into another. To be modelled as an explicit grade-to-grade conversion event. | Master · grade sheets |
| **DIFF** | Audit column: reported balance − (stock − sale). Must be zero; non-zero means a grade sheet is internally inconsistent. | Master · KAPNA ADD · T |

---

## Open items raised while reading the Overview

These are spec inconsistencies, not client questions. Fix before Phase 2 (Domain Model) is built.

| ID | Item |
|---|---|
| **G1** | **22 vs 23 grades.** The KPI strip says 23 grades, MDM-001 says 22, the tab list says 23 sheets (= `KAPNA ADD` + 22 grade sheets). 22 grades is the consistent reading. Confirm and correct the spec. |
| **G2** | **DIFF is defined two ways.** The glossary and CALC-8 both say `diff = balance − (stock − sale)` and assert it is 0. But CALC-7's balance also subtracts rejection (Excel `B44 = B11 − B40 − B23`), so DIFF is non-zero by construction whenever rejection ≠ 0. The rule must be `diff = reported_balance − derived_balance`, with rejection included on both sides. |
| **G3** | **Sieve buckets are not uniform.** The Overview says four sizes, but Q6 notes II and EX use only three. Whatever the model becomes, grade → allowed sizes is a real relationship, not a global list. |
| **G4** | **Source workbooks are not in this repo.** Only this spec exists. The two `.xlsx` files are needed before Phase 1 forensics can be verified rather than trusted. |

---

## Not in the Overview, but missing from the whole spec

Flagged here so it is not discovered mid-build.

- **Purchase / supplier side.** `RoughIntake` records a price but no supplier and no payable. Receivables are modelled; payables are not. If rough is bought on credit, half the ledger is absent.
- **GST / tax.** Not in the sales sheet, but RPT-002 prints a BILL. Confirm whether the printed document needs tax lines.
- **Stock reservation on DRAFT.** Two salespeople can draft-sell the same parcel; the clash only surfaces at post time. Q10 covers negative stock, not double-selling.
- **Backup / restore.** They are giving up a file they could copy to a pen drive. No story covers this.
- **Deployment.** Nowhere states where the server runs — office LAN box, VPS, or cloud. Android + offline sync means it must be reachable from outside, which carries cost and security consequences.
- **Pieces count.** Parcels are often tracked as pieces + carats. Not in the sheet; confirm it is genuinely not needed.
