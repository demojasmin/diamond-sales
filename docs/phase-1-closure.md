# Phase 1 — Closure & Sign-off Pack

Date: 2026-07-25 · Version 1.0 · **Client action required**
Covers: [01-workbook-forensics.md](01-workbook-forensics.md) · [02-requirement-analysis.md](02-requirement-analysis.md)

This is the one page to read if you read nothing else. It states what Phase 1 produced, what is
blocked, and exactly what we need from you to start Phase 2.

---

## 1. Deliverable status

| # | Deliverable | Status | Where |
|---|---|---|---|
| 1 | Workbook forensics — tab inventory, dependency graph, formula catalogue, calculated fields, data-quality risks | ✅ Written · ⚠ **unverified** | [01](01-workbook-forensics.md) |
| 2 | Business context, problem statement, target outcomes | ✅ | [02 §1](02-requirement-analysis.md) |
| 3 | Stakeholders & user roles | ✅ | [02 §2](02-requirement-analysis.md) |
| 4 | Complete business workflow (W1–W9 + invoice state machine) | ✅ | [02 §3](02-requirement-analysis.md) |
| 5 | Feature catalogue — 28 stories, 12 epics | ✅ | [02 §4](02-requirement-analysis.md) |
| 6 | Functional requirements — 60, each traced to a story and a source cell | ✅ | [02 §5](02-requirement-analysis.md) |
| 7 | Business rules — 6 families | ✅ | [02 §6](02-requirement-analysis.md) |
| 8 | Non-functional requirements — measurable targets | ✅ | [02 §7](02-requirement-analysis.md) |
| 9 | Pre-development checklist | ✅ | [02 §8](02-requirement-analysis.md) |
| 10 | Gaps, open questions, scope, risks | ✅ | [02 §9–12](02-requirement-analysis.md) |

**Analysis is done. Phase 1 does not close until section 5 below is signed.**

---

## 2. The three things you need to know

### 2.1 Nothing has been verified against your actual files

The two workbooks are not in our hands. Every formula, cell and row number in the forensics came
from the written specification, not from opening `Sale File Sample.xlsx` or
`Blank new master file.xlsx`. It may all be correct. We cannot say that it is.

### 2.2 Your reconciliation check does not work

The DIFF column that is supposed to prove your stock figures are consistent is, by its own
formulas, always equal to **minus your rejection carats** — never zero. Full proof in
[01 · Finding F-5](01-workbook-forensics.md). Either the specification mis-transcribed a formula,
or the check has never actually worked and nobody could have known.

This matters because DIFF is the number that would tell you your inventory is trustworthy. We need
to see the real file to determine which it is.

### 2.3 Your sales and receivables totals are almost certainly under-reported today

The three grand totals in the sales sheet stop at three different hard-coded rows — 854, 720 and
659. Any row past 659 is excluded from **Outstanding** first, then from **sales value** at 720.
A receivables figure that quietly shrinks looks like customers paying. It isn't.

If your sales log has passed row 659, the totals you have been reading are wrong, and the new
system's corrected figures will be **higher**, not different-by-error. Worth knowing before you
compare the two.

---

## 3. What we need from you

### 3.1 Files — blocking, nothing meaningful proceeds without these

| # | What | Why |
|---|---|---|
| **A1** | `Sale File Sample.xlsx` — the real file | Verify every formula and column |
| **A2** | `Blank new master file.xlsx` — the real file | The only source for the grade list, per-grade sizes, and the price constants |
| **A3** | **A populated master file**, not the blank template | The blank one has no balances. Migration cannot be designed against an empty workbook |
| **A4** | Your real price lists (stock / rejection / sale) | Currently scattered as loose numbers inside the sheets |
| **A5** | A sample printed bill as you issue it today | We have no layout, and no idea whether tax appears on it |
| **A6** | Whatever export your accountant currently receives | So we don't break their process |

### 3.2 Decisions — please answer in the right-hand column

| # | Decision | If you don't answer, we will assume | Your answer |
|---|---|---|---|
| **D1** | Answers to Q1–Q16 (section 3.3) | The stated assumptions | |
| **D2** | Approve or reject each Excel improvement IMP-1…13 individually | All accepted | |
| **D3** | Do we handle **purchases, suppliers and money you owe**? Today only money owed *to* you is modelled | Out of scope | |
| **D4** | Does the printed bill need **GST / tax**? | Not required | |
| **D5** | **Where does the server live** — a box in your office, a rented server, or cloud? | ❗ **No default. This one genuinely blocks us** — the Android app and offline working both depend on it | |
| **D6** | Do you track **pieces** as well as carats? | Carats only | |
| **D7** | How many people, and who, in each role (Sales / Manager / Owner)? | ❗ Blocking for user setup | |

### 3.3 Questions — 16, each with the assumption we'll use if you don't answer

| # | Question | Assumption | Your answer |
|---|---|---|---|
| Q1 | Do you ever need shape / cut / individual certification, or is it always grade + size parcels? | Parcels only | |
| Q2 | Do any goods carry GIA/IGI certificates tracked per stone? | No | |
| Q3 | For margin — cost basis is rough intake price, weighted-average stock cost, or standard cost? | Weighted-average stock cost | |
| Q4 | Is broker % a discount off the buyer's price, a commission you pay the broker, or both? | Both | |
| Q5 | Inventory aging counts from rough intake, or from when material entered its current grade? | Original intake | |
| Q6 | Exact sieve definitions in mm? Do some grades really use only 3 buckets? | Grade-specific, as seen | |
| Q7 | Is Ex Rate for foreign-currency deals, or always 1? | Always 1, INR | |
| Q8 | Do payment terms run from invoice date or dispatch date? | Invoice date | |
| Q9 | What do `Type` values other than `BILL` mean? | `BILL` = final sale | |
| Q10 | Should posting be blocked, warned, or allowed when stock would go negative? | Warn | |
| Q11 | Who owns rough intake and conversions — a stock manager, or you? | Manager | |
| Q12 | Are sub-rows like "NO 1 MB" and "GH-VVS MA" separate stock grades? | No — price/conversion rows | |
| Q13 | One company and one location, or several? | One | |
| Q14 | Can a single line be cancelled, or only a whole invoice? | Whole invoice | |
| Q15 | Any Gujarati-language screens needed? | English only | |
| Q16 | Reporting financial year — April to March? | April–March | |

---

## 4. Open items we cannot close ourselves

| ID | Item | Needs |
|---|---|---|
| G1 | 22 grade sheets, but KAPNA ADD is described as having 23 grade blocks. Is there a 23rd grade with no sheet? | A2 |
| G2 | DIFF = −rejection. The reconciliation rule must be rewritten before it is built | A2 + your confirmation |
| G3 | Which sizes each grade actually uses — it is not four across the board | A2 / Q6 |
| L1 | The row layout of a grade sheet (which rows are stock, rejection, sales, balance) | A2 |
| L2 | The block layout in KAPNA ADD — the grand-total formula's row steps don't add up | A2 |
| L4 | Whether GH-VVS and similar sub-rows are grades | A2 / Q12 |
| F-4 | Broker % sits on each line in Excel but belongs on the invoice. What if old invoices have different values across their lines? | A1 |
| GAP-1 | No supplier or payable side exists anywhere in the specification | D3 |
| GAP-3 | Two salespeople can draft-sell the same parcel; the clash only appears at posting | Your call on whether that matters |
| GAP-4 | No backup or restore. You are giving up a file you could copy to a pen drive | Confirm it's required (we recommend yes) |

**One item is ours, not yours:** the specification HTML itself is not yet in version control. Say
the word and it goes in.

---

## 5. Sign-off

Phase 1 closes, and Phase 2 begins, when the following are agreed. Tick, sign, return.

- [ ] The business workflow described in [02 §3](02-requirement-analysis.md) is how the business actually operates
- [ ] The three roles and their permissions ([02 §2.2](02-requirement-analysis.md)) are correct
- [ ] The business rules ([02 §6](02-requirement-analysis.md)) are correct — particularly every rule marked ⚠ ASSUMED
- [ ] Q1–Q16 answered, or their assumptions accepted as written
- [ ] IMP-1…13 approved or rejected individually
- [ ] D1–D7 decided, D5 in particular
- [ ] Volume assumptions confirmed: roughly 1,000–3,000 sale lines a year, up to 10 users
- [ ] The MVP scope in [02 §11](02-requirement-analysis.md) is agreed and frozen for the first release
- [ ] Files A1–A6 supplied

| | Name | Signature | Date |
|---|---|---|---|
| Client / Owner | | | |
| Solution Architect | | | |

---

## 6. What happens next

On sign-off, Phase 2 (Domain Model) produces: the corrected relational schema, the calculation
engine specified rule-by-rule with a test for each, the API contract, and the migration design —
built against your real workbooks rather than a description of them.

**Estimated blocked time:** Phase 2 design can start on sign-off alone. It cannot *finish* without
A1–A3, because the data model has to match what is actually in those files.
