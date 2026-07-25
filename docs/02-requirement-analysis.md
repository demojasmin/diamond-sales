# 02 · Requirement Analysis

Status: **draft, awaiting client sign-off**
Source: `Diamond_Sales_System_Requirements.html` v1.0 (2026-07-24), built on the forensics in
[01-workbook-forensics.md](01-workbook-forensics.md) and the glossary in [00-overview.md](00-overview.md)
Audience: client (business sign-off) and the build team (basis for Phase 2 architecture).

> **Rule followed throughout:** no business rule was invented. Anything not evidenced by a
> workbook cell or an explicit client statement is marked **⚠ ASSUMED** and carries an open
> question. Assumptions become facts only when the client signs them off.

---

## 1. Why this project exists

### 1.1 As-is

Two Excel workbooks, edited by hand, not linked to each other.

| | Workbook | What it does | Who edits |
|---|---|---|---|
| A | `Sale File Sample.xlsx` | One flat sheet. One row per sale **line**. An invoice is just several rows sharing a serial number (`Sr.`). Excel computes rejection, amount, outstanding. | Sales staff |
| B | `Blank new master file.xlsx` | `KAPNA ADD` rough-intake ledger + 22 per-grade sheets (Stock → Rejection → Sales → Balance × sieve size), rolled up and DIFF-audited back in `KAPNA ADD`. | Stock manager / owner |

### 1.2 The problem, stated once

**A sale in workbook A is never deducted from stock in workbook B.** Reconciliation is manual,
so balances drift, and the owner cannot trust any inventory or margin number he reads.

Everything below is downstream of closing that gap. Nine further defects make it worse:

| ID | Defect | Business consequence |
|---|---|---|
| DQ-1 | Totals use hard-coded ranges (`SUM(K3:K854)`, `Q3:Q720`, `S3:S659`) | Sales and receivables are **silently under-reported** once rows pass the ceiling |
| DQ-2 | `1e-08` / `1e-12` placeholder weights to dodge div-by-zero | Pollutes weighted averages |
| DQ-3 | No sales ↔ stock link | **The primary problem** |
| DQ-4 | Grade codes differ per artifact (`1BB` / `1 BB` / `NO 1 BB`) | Sales cannot be joined to inventory at all |
| DQ-5 | Sheet names carry trailing spaces / punctuation | Fragile references |
| DQ-6 | Float noise (`−0.2749…` outstanding) | Phantom receivables |
| DQ-7 | Bidirectional grade coupling (`'1 '↔II`) | Structural edits break references |
| DQ-8 | Merged cells used as form layout | Not migratable as-is |
| DQ-9 | Invoice identity is only a repeated `Sr.` | No invoice entity; header data can diverge row to row |
| DQ-10 | Ex-rate and broker % constant (=1) in the sample | Real-world usage unverified |

### 1.3 To-be, in one line each

- **Windows desktop** — sales staff enter invoices keyboard-first; posting deducts stock automatically.
- **Android** — the owner reads dashboards, receivables, stock and aging. Read-mostly.
- **Backend** — one deterministic calculation engine, one append-only stock ledger, audit trail, offline sync.

### 1.4 Business outcomes this must deliver (the acceptance bar for the whole programme)

| # | Outcome | How it will be measured |
|---|---|---|
| BO-1 | Inventory balance is trustworthy without manual reconciliation | DIFF audit = 0 across all grades, computed, not typed |
| BO-2 | Sales entry is **no slower** than Excel | Timed side-by-side: a 3-line invoice, keyboard only |
| BO-3 | Owner sees sales, outstanding and stock without opening a file | Android dashboard, current within one sync cycle |
| BO-4 | Every number is attributable and reversible | Audit log covers 100 % of mutations |
| BO-5 | Totals include every record | No fixed row ceilings anywhere |

---

## 2. Stakeholders and user roles

### 2.1 Stakeholders

| Stakeholder | Interest | Needed from them |
|---|---|---|
| Owner (sponsor) | Trustworthy numbers, mobile visibility, margin | Sign-off on Appendix A improvements and Appendix B questions; UAT |
| Stock manager | Intake, conversions, rejections, reconciliation | Walkthrough of the real reconciliation routine; the real price lists |
| Sales staff (2–5 ⚠ ASSUMED) | Speed of entry | Time-and-motion observation of current Excel entry |
| Accountant | Exports that still work downstream | The report formats they consume today |
| Build team | Complete, unambiguous spec | This document, signed |

### 2.2 Roles in the system

Three roles. Deliberately three — a fourth (e.g. a separate stock-manager role) is available if
Q11 says stock work belongs to a dedicated person rather than the manager.

| Role | Who | Can do | Cannot do |
|---|---|---|---|
| **Sales Person** | Sales floor staff | Create/edit **own** draft invoices; post own invoices; record receipts; view own invoices; limited export | Master data, stock movements, margin/cost, other users' invoices, edit posted invoices (may only raise a change request), cancel, audit log, dashboard |
| **Manager** | Operations / stock manager | Everything Sales Person can, on **all** invoices; edit posted invoices (audited); cancel (audited); rough intake, conversions, rejections, adjustments; master data (grades, buyers, brokers, prices); dashboard; full export; audit log; conflict inbox | User administration; margin visibility is **configurable**, off by default |
| **Owner / Admin** | Business owner | Everything, including user & role administration, margin/cost/profitability, settings | — |

**Role rules**

- BR-ROLE-1 — Every action is attributable to exactly one authenticated user. No shared logins.
- BR-ROLE-2 — A posted invoice is never edited silently: Sales Person raises a change request, Manager or Owner applies it, audit records before/after.
- BR-ROLE-3 — Deactivating a user invalidates sessions immediately and refuses login; their historical records remain.
- BR-ROLE-4 — Role change takes effect on the user's next action, and is audited.
- ⚠ ASSUMED — one physical location, one company entity. Multi-branch is out of scope (Q13).

---

## 3. The complete business workflow

### 3.1 End-to-end, as the business actually runs

```
   PURCHASE                 PREPARE                      SELL                    COLLECT
 ┌───────────┐          ┌────────────┐            ┌──────────────┐          ┌───────────┐
 │  Rough    │          │  Sort /    │            │  Offer a     │          │  Receipt  │
 │  parcel   │─INTAKE──▶│  re-sort   │─CONVERT───▶│  parcel to   │─SALE────▶│  against  │
 │  arrives  │          │  by grade  │            │  a buyer     │          │  invoice  │
 └───────────┘          └────────────┘            └──────┬───────┘          └─────┬─────┘
   KAPNA ADD              "avelu" links                  │                        │
                                                    REJECTION                OUTSTANDING
                                                (unaccepted carats           = amount − Σ receipts
                                                  return to stock)
                                                         │
                                                         ▼
                                            ┌──────────────────────────┐
                                            │  Stock ledger (one)      │
                                            │  balance = Σ movements   │
                                            │  DIFF audit must be 0    │
                                            └──────────────────────────┘
```

### 3.2 Workflow W1 — Rough intake (Kapna Add)

1. A rough parcel arrives. Stock manager records date, **grade**, **sieve size**, **weight (ct)**, **price/ct**.
2. System writes an `INTAKE` movement (positive carats).
3. Grade × size balance increases; weighted-average cost recomputes (CALC-6).
4. **Open:** no supplier and no payable is captured today (see §9 gap GAP-1). If rough is bought on credit, this workflow is half-modelled.

*Source:* `Master · KAPNA ADD · A–K`. *Story:* INV-001.

### 3.3 Workflow W2 — Grade-to-grade conversion ("avelu")

1. Material is re-sorted; carats move from grade A / size to grade B / size.
2. System writes a **paired** movement: `CONVERT_OUT` from source, `CONVERT_IN` to target, same weight.
3. Total company carats are unchanged — that is the invariant to test.
4. Replaces the workbook's positional cell links (`'1 '!F6 = II!B21`), which cause the bidirectional coupling of DQ-7.

*Source:* `Master · grade sheets`. *Story:* INV-004.

### 3.4 Workflow W3 — Sales entry (the hot path, must beat Excel on speed)

1. Sales person starts an invoice: date, buyer, broker, broker %, terms, doc type.
   Buyer selection pre-fills terms; broker selection pre-fills broker %.
2. Adds N lines: grade, size, gross weight, selection (accepted ct), price/ct, ex-rate, Less 1 %, Less 2 %, remark.
3. As each line completes, the engine computes **live**: rejection = weight − selection (CALC-2), amount (CALC-1), invoice total (CALC-4).
4. Enter adds a line, header retained. Ctrl+S saves. Validation blocks selection > weight.
5. Invoice is saved as **DRAFT**. Draft does **not** touch stock.

*Source:* `Sale · Sheet1 · A–T`. *Stories:* SALES-001, CALC-001.

### 3.5 Workflow W4 — Posting (the gap being closed)

1. Sales person or manager posts a valid draft.
2. System validates, then writes one `SALE` movement per line and sets status **POSTED**.
3. Balances drop by the sold carats — the link the workbooks never had.
4. If a post would drive a grade × size balance negative, behaviour follows configured policy: **block / warn / allow**, default **warn** (Q10, CFG-003).
5. Posting is **idempotent**, keyed by invoice + version, so a retried sync cannot double-deduct.
6. Optionally (per config) a `REJECTION` movement is recorded for the line's rejected carats.

*Stories:* SALES-003, INV-002, INV-005.

### 3.6 Workflow W5 — Correction and cancellation

- **Draft edit** — free, recomputes everything.
- **Posted edit** — Sales Person may only raise a change request; Manager/Owner applies it; stock movements are re-applied to match the new lines; audit records before/after.
- **Cancel** — status → CANCELLED, all stock movements reversed by compensating entries (never deleted), reason text mandatory, receipts flagged for attention.

*Stories:* SALES-002, SALES-004, AUD-001.

### 3.7 Workflow W6 — Receipts and receivables

1. A payment arrives against an invoice: date, amount, method.
2. Outstanding is **derived**, never stored: `Σ line amounts − Σ receipts` (CALC-3).
3. Partial payments are ordinary. Over-payment warns and is treated as advance/credit.
4. Due date = invoice date + terms days (CALC-10); overdue when today > due and outstanding > 0.
5. Receivables view ages outstanding into 0–30 / 31–60 / 61–90 / 90+ buckets, per buyer.

*Source:* `Sale · R, S, F`. *Stories:* PAY-001, PAY-002.

### 3.8 Workflow W7 — Reconciliation (replaces DIFF by hand)

1. Derived balance per grade × size = Σ INTAKE + Σ CONVERT_IN − Σ CONVERT_OUT − Σ REJECTION − Σ SALE (CALC-7).
2. DIFF audit runs as an automated invariant, not a column someone reads (CALC-8).
3. Any non-zero DIFF is an incident, surfaced to the manager, not a number to be eyeballed.

⚠ **Spec defect carried forward (G2):** CALC-8 as written (`balance − (stock − sale)`) is non-zero by
construction whenever rejection ≠ 0, because CALC-7 also subtracts rejection. The rule must be
`diff = reported_balance − derived_balance` with rejection on both sides. Must be corrected before Phase 2.

### 3.9 Workflow W8 — Owner review (Android)

Open app → pick date range (default current month) → read the five numbers that matter:
total sales, blended rate/ct, outstanding, inventory value, top movers → tap any KPI to drill to
the underlying records. Read-only. Posted invoices only.

*Stories:* DASH-001, DASH-002. *Detail:* spec Phase 4, widgets W1–W15.

### 3.9a Workflow W9 — Offline and sync

Both clients write locally and queue an outbox. On reconnect, queued changes upload and server
state downloads. Movements are append-only so they merge additively (no lost update). Invoice
headers/lines before post are owned records: last-writer-wins **within audit**, superseded version
retained. Unresolvable conflicts go to a manager conflict inbox, never silently dropped.

*Stories:* SYNC-001, SYNC-002.

### 3.10 Invoice state machine

```
   ┌────────┐   post    ┌────────┐   cancel   ┌───────────┐
   │ DRAFT  │──────────▶│ POSTED │───────────▶│ CANCELLED │
   └───┬────┘           └───┬────┘            └───────────┘
       │ edit (free)        │ edit (audited, manager/owner only,
       └────────────────────┘        stock re-applied)
```

- Stock is affected **only** by POSTED and by the reversal on CANCELLED.
- CANCELLED is terminal. A cancelled invoice is never re-opened; a new invoice is raised.
- ⚠ ASSUMED — no partial cancellation of individual lines. Confirm (Q14).

---

## 4. Feature catalogue — every feature, what it replaces

12 epics, 28 stories. Priority is MoSCoW from the spec; Phase is the spec's delivery phasing.

### AUTH — Authentication & roles

| Story | Feature | Replaces | Pri | Phase | Platform |
|---|---|---|---|---|---|
| AUTH-001 | Login with role-scoped session; lockout after 5 failed attempts | An unprotected shared file where anyone can edit any cell | Must | MVP | Both |
| AUTH-002 | User & role administration; deactivate leavers | Implicit trust | Must | MVP | Backend |

### MDM — Master data

| Story | Feature | Replaces | Pri | Phase | Platform |
|---|---|---|---|---|---|
| MDM-001 | Canonical **Grade** dictionary with aliases, sort order, active flag | 22 sheet names + inconsistent codes (DQ-4/5) | Must | MVP | Both |
| MDM-002 | **Buyer** & **Broker** masters with default terms / default broker % | Re-typed names on every row | Must | MVP | Both |
| MDM-003 | **Grade × Size price list**, effective-dated, contexts STOCK/REJECTION/SALE | Prices hard-typed in cells (`II!C21=44000`) | Should | P2 | Both |

### SALES — Sales entry

| Story | Feature | Replaces | Pri | Phase | Platform |
|---|---|---|---|---|---|
| SALES-001 | Keyboard-first multi-line invoice entry with live calculation | Typing rows into Sheet1 | Must | MVP | Desktop |
| SALES-002 | Search, reopen, correct an invoice; change-request path for posted | Hunting rows by repeated `Sr.` | Must | MVP | Desktop |
| SALES-003 | **Post** — finalise and commit stock deduction | Nothing — this never existed (DQ-3) | Must | MVP | Desktop |
| SALES-004 | Cancel / void with stock reversal and mandatory reason | Deleting rows and silently changing totals | Should | P2 | Desktop |

### CALC — Calculation engine

| Story | Feature | Replaces | Pri | Phase | Platform |
|---|---|---|---|---|---|
| CALC-001 | One server-side deterministic engine, rules CALC-1…11 | Per-cell formulas, placeholder hacks, fixed SUM ranges | Must | MVP | Backend |
| CALC-002 | Rounding & precision policy | Float noise (DQ-6) | Must | MVP | Backend |

### INV — Inventory

| Story | Feature | Replaces | Pri | Phase | Platform |
|---|---|---|---|---|---|
| INV-001 | Rough intake entry by grade × size | KAPNA ADD left block | Must | MVP | Desktop |
| INV-002 | Append-only movement ledger; balances derived, DIFF automated | Stock/Rejection/Sales/Balance sections + cross-sheet links | Must | MVP | Backend |
| INV-003 | Stock position: balance, weighted-avg price, value per grade × size, drill to movements | Navigating 22 tabs by hand | Must | MVP | Both |
| INV-004 | Explicit grade-to-grade conversion (avelu) | Positional cell links (DQ-7) | Should | P2 | Desktop |
| INV-005 | Rejection recording, standalone or from a sales line | Grade-sheet Rejection tables | Should | P2 | Desktop |

### PAY — Payments

| Story | Feature | Replaces | Pri | Phase | Platform |
|---|---|---|---|---|---|
| PAY-001 | Receipts against invoices; partial payments; over-payment warning | A single overwrite-prone `Rec. Amt` cell | Must | MVP | Both |
| PAY-002 | Receivables ledger with due dates and ageing buckets | One `Outstanding` grand total | Must | P2 | Both |

### SYNC — Offline & sync

| Story | Feature | Replaces | Pri | Phase | Platform |
|---|---|---|---|---|---|
| SYNC-001 | Offline entry with outbox and sync | One-file-one-editor | Must | P2 | Both |
| SYNC-002 | Conflict detection & resolution, manager conflict inbox | Nothing | Must | P2 | Backend |

### DASH / RPT / AUD / NOTIF / CFG

| Story | Feature | Pri | Phase | Platform |
|---|---|---|---|---|
| DASH-001 | Owner dashboard: sales, blended rate, outstanding, stock, top movers, drill-down (W1–W6, W9–W11, W13) | Must | P2 | Android |
| DASH-002 | Salesperson & buyer performance analytics; broker cost (W5, W7, W12, W14, W15) | Should | P3 | Android |
| RPT-001 | Excel / PDF export of sales and stock | Should | P2 | Both |
| RPT-002 | Invoice document print (the `Type = BILL` implication) | Could | P3 | Desktop |
| AUD-001 | Immutable change log: who / when / entity / before / after | Must | MVP | Backend |
| NOTIF-001 | Overdue-receivable and low-stock alerts | Should | P3 | Android |
| CFG-001 | Company, base currency, precision settings | Should | P3 | Both |
| CFG-002 | Session/security, negative-stock policy, alert thresholds | Should | P3 | Both |

---

## 5. Functional requirements

Traced to story and to the source cell. `FR-<epic>-<n>`.

### 5.1 Authentication & authorisation

| ID | Requirement | Story |
|---|---|---|
| FR-AUTH-1 | The system shall authenticate users by username + password, issuing a session scoped to the user's role. | AUTH-001 |
| FR-AUTH-2 | The system shall deny and log failed login attempts, and lock an account after 5 consecutive failures. | AUTH-001 |
| FR-AUTH-3 | The system shall hide or disable every capability the current role does not hold (§2.2 matrix). | AUTH-001 |
| FR-AUTH-4 | An admin shall create users, assign roles, and deactivate users; deactivation invalidates live sessions. | AUTH-002 |
| FR-AUTH-5 | Android shall support biometric unlock after the first credentialed login. | AUTH-001 |

### 5.2 Master data

| ID | Requirement | Story / Source |
|---|---|---|
| FR-MDM-1 | The system shall hold a Grade master with unique canonical code, display name, alias list, sort order, active flag. | MDM-001 · `Master · tabs` |
| FR-MDM-2 | Grade on any transaction shall be selected from the canonical list; free text shall not be accepted. | MDM-001 |
| FR-MDM-3 | Import shall resolve legacy aliases (`1BB`, `1 BB`, `NO 1 BB`) to one canonical grade. | MDM-001 · DQ-4 |
| FR-MDM-4 | An inactive grade shall remain on historical records but be hidden from new entry. | MDM-001 |
| FR-MDM-5 | The system shall hold a Size master for the sieve buckets (`−2`, `−6.5`, `+6.5`, `+11`) and shall record which sizes are valid **per grade** (G3/Q6 — not all grades use four). | MDM-001 · `Master · size headers` |
| FR-MDM-6 | The system shall hold Buyer master with default terms days and Broker master with default broker %, both pre-filling on invoice entry and both overridable per invoice. | MDM-002 · `Sale · C,D,E,F` |
| FR-MDM-7 | Duplicate buyer/broker names shall raise a warning on create. | MDM-002 |
| FR-MDM-8 | The system shall hold an effective-dated price list keyed by grade × size × context {STOCK, REJECTION, SALE}. | MDM-003 |
| FR-MDM-9 | Valuation as of a date shall use the price effective on that date; a missing price shall value at 0 and raise a "price missing" flag. | MDM-003 |

### 5.3 Sales

| ID | Requirement | Story / Source |
|---|---|---|
| FR-SALES-1 | An invoice shall be a first-class entity with header (no, date, buyer, broker, broker %, terms, doc type, currency, status) and one or more lines. | SALES-001 · DQ-9 |
| FR-SALES-2 | A line shall capture grade, size, gross weight ct, selection ct, price/ct, ex-rate, Less 1 %, Less 2 %, remark; rejection and amount shall be computed and read-only. | SALES-001 · `Sale · G–T` |
| FR-SALES-3 | Entry shall be completable entirely from the keyboard: Tab/Shift-Tab between fields, Enter to add a line retaining the header, Ctrl+S to save, Esc to cancel a line. | SALES-001 · BO-2 |
| FR-SALES-4 | Derived values shall update on field exit, before save. | SALES-001 |
| FR-SALES-5 | Save shall be blocked with a field-level message when a mandatory field is empty or selection > gross weight. | SALES-001 |
| FR-SALES-6 | Users shall search invoices by invoice no, buyer, or date range. | SALES-002 |
| FR-SALES-7 | Editing a draft shall recompute all derived values and any stock impact. | SALES-002 |
| FR-SALES-8 | A Sales Person shall not edit a posted invoice directly; the system shall record a change request for manager approval. | SALES-002 · §2.4 |
| FR-SALES-9 | Posting shall set status POSTED and create one SALE stock movement per line. | SALES-003 |
| FR-SALES-10 | Posting that would drive a grade × size balance negative shall block, warn, or allow per configured policy (default warn). | SALES-003 · Q10 · CFG-003 |
| FR-SALES-11 | Posting shall be idempotent, keyed by invoice + version. | SYNC-002 |
| FR-SALES-12 | Cancelling a posted invoice shall reverse its stock movements by compensating entries, require a reason, and warn if receipts exist. | SALES-004 |

### 5.4 Calculation

| ID | Requirement | Rule | Source |
|---|---|---|---|
| FR-CALC-1 | Line amount = `selection × price/ct × ex_rate × (1−less1/100) × (1−less2/100) × (1−broker%/100)` — discounts compound. | CALC-1 | `Sale · Q` |
| FR-CALC-2 | Line rejection ct = `gross_weight − selection`, enforced ≥ 0. | CALC-2 | `Sale · J` |
| FR-CALC-3 | Invoice outstanding = `Σ line amount − Σ receipt amount`. Never stored. | CALC-3 | `Sale · S` |
| FR-CALC-4 | Invoice total = `Σ line amount`. | CALC-4 | `Sale · Q1` |
| FR-CALC-5 | Blended rate/ct = `Σ amount ÷ Σ selection ct`, for any grouping. | CALC-5 | `Sale · L1` |
| FR-CALC-6 | Weighted-average price = `Σ(wᵢ·pᵢ) ÷ Σwᵢ`; if `Σw = 0` the result is 0, not an error and not a placeholder row. | CALC-6 | `Master · SUMPRODUCT` · fixes DQ-2 |
| FR-CALC-7 | Grade × size balance = `Σ INTAKE + Σ CONVERT_IN − Σ CONVERT_OUT − Σ REJECTION − Σ SALE`. | CALC-7 | `Master · B44` |
| FR-CALC-8 | The reconciliation DIFF shall be computed automatically and asserted to be zero. **Definition to be corrected per G2.** | CALC-8 | `Master · T=R−S` |
| FR-CALC-9 | Roll-ups: grade = Σ over its sizes; company = Σ over grades; prices roll up by CALC-6, never by simple average. | CALC-9 | `Master · row 309` |
| FR-CALC-10 | Due date = `invoice_date + terms_days`; overdue when today > due and outstanding > 0. | CALC-10 | `Sale · F` |
| FR-CALC-11 | Broker payable = `Σ(selection × price × ex_rate × (1−less1/100) × (1−less2/100)) × broker%/100`. | CALC-11 | `Sale · E` · Q4 |
| FR-CALC-12 | Every aggregate shall span all qualifying records; no fixed row ceiling shall exist anywhere. | IMP-10 | fixes DQ-1 |
| FR-CALC-13 | Identical inputs shall produce identical outputs on desktop, Android and reports. The engine is the single implementation. | CALC-001 | — |

### 5.5 Inventory

| ID | Requirement | Story |
|---|---|---|
| FR-INV-1 | The system shall record rough intake (date, grade, size, weight, price/ct) creating an INTAKE movement. | INV-001 |
| FR-INV-2 | Stock shall be held as an append-only movement ledger with types INTAKE, CONVERT_IN, CONVERT_OUT, REJECTION, SALE, ADJUST; weights signed; each movement referencing its source document. | INV-002 |
| FR-INV-3 | Balances shall be derived from movements, never stored as a mutable figure. | INV-002 |
| FR-INV-4 | A stock position view shall show, per grade × size: balance ct, weighted-avg price, value; and drill down to the movements that produced it. | INV-003 |
| FR-INV-5 | A conversion shall create a balanced pair (CONVERT_OUT + CONVERT_IN) of equal weight, with source and target linked. | INV-004 |
| FR-INV-6 | Total company carats shall be unchanged by any conversion. | INV-004 |
| FR-INV-7 | Rejections shall be recordable directly, and optionally auto-created from a posted sales line's rejection carats (per config). | INV-005 |
| FR-INV-8 | Company totals shall equal the grand total of all grades (the `row 309` equivalent). | INV-003 |

### 5.6 Payments

| ID | Requirement | Story |
|---|---|---|
| FR-PAY-1 | Multiple receipts shall be recordable against one invoice, each with date, amount > 0, method. | PAY-001 |
| FR-PAY-2 | Outstanding shall update from receipts automatically per CALC-3. | PAY-001 |
| FR-PAY-3 | A receipt exceeding outstanding shall warn and be treated as advance/credit. | PAY-001 |
| FR-PAY-4 | The receivables view shall list buyer, invoice, amount, due date, days overdue, filterable by buyer. | PAY-002 |
| FR-PAY-5 | Ageing shall bucket outstanding into 0–30 / 31–60 / 61–90 / 90+ days with correct bucket totals. | PAY-002 |

### 5.7 Sync, dashboard, reporting, audit, notifications, config

| ID | Requirement | Story |
|---|---|---|
| FR-SYNC-1 | Both clients shall create, edit and post while offline, queueing changes locally. | SYNC-001 |
| FR-SYNC-2 | On reconnect, queued changes shall upload and server state download; both clients then show the same data. | SYNC-001 |
| FR-SYNC-3 | Sync status and pending-change count shall be visible to the user at all times. | SYNC-001 |
| FR-SYNC-4 | Concurrent movements shall merge additively with no lost update; conflicting invoice edits resolve last-writer-wins with the superseded version retained in audit. | SYNC-002 |
| FR-SYNC-5 | Unresolvable conflicts shall be queued to a manager conflict inbox and never silently dropped. | SYNC-002 |
| FR-DASH-1 | The Android dashboard shall present widgets W1–W15 per spec Phase 4, over **posted invoices only**, filterable by date range (default current month), salesperson, buyer, broker, grade, size. | DASH-001/002 |
| FR-DASH-2 | Every KPI shall drill down to the records that produced it. | DASH-001 |
| FR-DASH-3 | Inventory-value widgets shall refresh only after a completed stock sync, to avoid mid-movement flicker. | DASH-001 |
| FR-RPT-1 | Sales and stock views shall export to .xlsx matching on-screen data, and stock position to print-formatted PDF; filenames date-stamped. | RPT-001 |
| FR-RPT-2 | A posted invoice shall print as a document showing buyer, lines, amounts, terms, totals, and a broker line if configured. | RPT-002 |
| FR-AUD-1 | Every create/edit/cancel/post of invoices, receipts, stock movements and master data shall write an append-only audit entry: user, timestamp, entity, before, after. | AUD-001 |
| FR-AUD-2 | Audit entries shall never be edited or deleted. | AUD-001 |
| FR-AUD-3 | Managers and owners shall filter the audit log by user, entity and date. | AUD-001 |
| FR-NOTIF-1 | The owner shall be notified when an invoice passes its due date unpaid, and when a grade × size balance falls below its threshold. | NOTIF-001 |
| FR-CFG-1 | Admin shall configure company details, base currency, and rounding precision. | CFG-001 |
| FR-CFG-2 | Admin shall configure session timeout and lockout, negative-stock policy (block/warn/allow), overdue-day and low-stock thresholds. | CFG-002 |

---

## 6. Business rules

Split by kind. **Every rule cites its evidence.** Rules marked ⚠ are inferred and need sign-off.

### 6.1 Calculation rules

Rules BR-CALC-1…11 are exactly FR-CALC-1…11 above (spec CALC-1…11). Not repeated.
Three properties bind all of them:

| ID | Rule | Evidence |
|---|---|---|
| BR-CALC-P1 | Discounts **compound**, they do not add. Less 1, then Less 2, then broker %, each applied to the running amount. | `Sale · Q` nested formula |
| BR-CALC-P2 | Averages are **weight-weighted**, never arithmetic. This is the core valuation rule everywhere in the master file. | `Master · SUMPRODUCT` |
| BR-CALC-P3 | Derived values are computed, never entered, and are visually distinct and read-only in every UI. | CALC-001 |

### 6.2 Rounding & precision

| ID | Rule | Evidence |
|---|---|---|
| BR-ROUND-1 | Money is stored and displayed to 2 dp. | CALC-002 |
| BR-ROUND-2 | Carats are stored to 4 dp, displayed 2–4 dp. | CALC-002 |
| BR-ROUND-3 | Averages are computed unrounded and rounded only for display. | §2.3 |
| BR-ROUND-4 | Rounding is round-half-up at persistence; banker's rounding is a configurable alternative. | CALC-002 |
| BR-ROUND-5 | A fully-received invoice shows outstanding exactly `0.00`, never `−0.27`. | fixes DQ-6 |
| BR-ROUND-6 | Money is stored as `decimal`, never floating point. ⚠ implementation rule, non-negotiable | fixes DQ-6 |

### 6.3 Validation rules

| ID | Rule | Evidence |
|---|---|---|
| BR-VAL-1 | `0 ≤ selection ≤ gross_weight` on every line. | §2.1 SalesLine constraint |
| BR-VAL-2 | Rejection is never negative. | CALC-2 |
| BR-VAL-3 | Receipt amount > 0. | §2.1 Receipt |
| BR-VAL-4 | Grade, size, buyer are references to master records, never free text. | MDM-001/002 |
| BR-VAL-5 | Size must be valid **for the chosen grade** (not all grades use all four buckets). ⚠ pending Q6 | G3 · Q6 |
| BR-VAL-6 | Grade code is unique; buyer and broker names are unique. | §2.1 |
| BR-VAL-7 | An invoice must have at least one line before it can be posted. ⚠ inferred | — |
| BR-VAL-8 | Terms days ≥ 0; discount percentages within 0–100; ex-rate > 0. ⚠ inferred | — |

### 6.4 Inventory integrity rules

| ID | Rule | Evidence |
|---|---|---|
| BR-INV-1 | The movement ledger is append-only. Corrections are compensating movements, never updates or deletes. | §5.2 offline design |
| BR-INV-2 | Balance is always derived, never stored. | §2.1 design principle |
| BR-INV-3 | A conversion conserves weight: `CONVERT_OUT weight = CONVERT_IN weight`. | INV-004 |
| BR-INV-4 | DIFF must be zero for every grade × size. Non-zero is an incident. | CALC-8 · `Master · T` |
| BR-INV-5 | Draft invoices never affect stock. Only posting does. | SALES-003 |
| BR-INV-6 | Negative stock policy is configurable; default is warn, not block. | Q10 · CFG-003 |
| BR-INV-7 | Placeholder micro-weights (`1e-08`, `1e-13`) are treated as zero and never migrated. | DQ-2 · IMP-8 |
| BR-INV-8 | Every movement carries a reference to the document that caused it (invoice, intake, conversion, adjustment). | §2.1 StockMovement |

### 6.5 Sales & receivable lifecycle rules

| ID | Rule | Evidence |
|---|---|---|
| BR-LIFE-1 | Invoice status is DRAFT → POSTED → CANCELLED. CANCELLED is terminal. | §2.1 |
| BR-LIFE-2 | Posting is idempotent by invoice + version. | §5.2 |
| BR-LIFE-3 | Outstanding is derived from the receipt ledger, never a stored figure. | IMP-6 |
| BR-LIFE-4 | Due date runs from **invoice date**, not dispatch date. ⚠ ASSUMED — Q8 | Q8 |
| BR-LIFE-5 | Broker % is deducted from the amount **and** separately reported as a payable. ⚠ ASSUMED — Q4 | Q4 · CALC-11 |
| BR-LIFE-6 | Base currency is INR; ex-rate defaults to 1; multi-currency supported but off by default. ⚠ ASSUMED — Q7 | Q7 |
| BR-LIFE-7 | Historical invoices migrated at cut-over are posted **without** re-deducting stock, because opening balances are already net. | §5.3 migration |

### 6.6 Access rules

BR-ROLE-1…4 in §2.2, plus:

| ID | Rule | Evidence |
|---|---|---|
| BR-ROLE-5 | Margin, cost and profitability are visible to Owner always, Manager only if configured, Sales Person never. | §2.4 |
| BR-ROLE-6 | Sales Person exports are limited to their own records. | §2.4 |

---

## 7. Non-functional requirements

Each NFR has a **measurable** target. An NFR without a number is an opinion.

### 7.1 Performance

| ID | Requirement | Target |
|---|---|---|
| NFR-PERF-1 | Live line calculation on keystroke/field-exit | < 50 ms, local, no server round-trip |
| NFR-PERF-2 | Save or post an invoice, online | < 1 s p95 |
| NFR-PERF-3 | Stock position view (all grades × sizes) | < 2 s p95 |
| NFR-PERF-4 | Dashboard widget load over a 1-month range on 4G | < 3 s p95 |
| NFR-PERF-5 | Full sync after a working day offline | < 30 s |
| NFR-PERF-6 | **Entry speed vs Excel** — a 3-line invoice keyboard-only | ≤ Excel time, measured at UAT (BO-2) |

### 7.2 Volume & capacity (⚠ ASSUMED — confirm at §10 R1)

| ID | Assumption | Basis |
|---|---|---|
| NFR-VOL-1 | ~1,000–3,000 sale lines/year; template sized ~1,000+ rows, `SUM` to row 854 | `Sale · Sheet1` |
| NFR-VOL-2 | 22 grades × up to 4 sizes = ≤ 88 stock buckets | `Master · tabs` |
| NFR-VOL-3 | ≤ 10 concurrent users; ≤ 5 sales staff | ⚠ ASSUMED |
| NFR-VOL-4 | Movement ledger growth ≤ ~10k rows/year | derived from above |
| NFR-VOL-5 | 10-year retention with no archival step needed at this volume | derived |

**Architectural consequence:** this is a *small-data, high-integrity* system. Correctness,
auditability and offline behaviour outrank throughput and scale. No requirement here justifies
sharding, message buses, microservices or a caching tier.

### 7.3 Availability & offline

| ID | Requirement |
|---|---|
| NFR-AVL-1 | Sales entry shall function fully with **zero** network connectivity, for a full working day. |
| NFR-AVL-2 | Android dashboard shall display last-synced data with an explicit staleness timestamp when offline. |
| NFR-AVL-3 | Server target 99 % during business hours (⚠ ASSUMED — depends on deployment choice, R4). |
| NFR-AVL-4 | No single client failure loses queued data: the outbox survives app restart and OS restart. |

### 7.4 Data integrity & auditability

| ID | Requirement |
|---|---|
| NFR-INT-1 | The server is authoritative for all derived values; clients never persist a computed figure as truth. |
| NFR-INT-2 | 100 % of mutations produce an audit record. Audit is append-only at the storage layer. |
| NFR-INT-3 | Referential integrity enforced in the database, not only in application code. |
| NFR-INT-4 | The DIFF invariant (CALC-8) runs as an automated check, and its failure raises an alert. |
| NFR-INT-5 | Migration is reproducible: re-running it on the same workbooks yields identical output. |

### 7.5 Security

| ID | Requirement |
|---|---|
| NFR-SEC-1 | Passwords hashed with bcrypt or argon2. Never reversible, never logged. |
| NFR-SEC-2 | All client↔server traffic over TLS. |
| NFR-SEC-3 | Sessions expire on configured idle timeout; account locks after 5 failed logins. |
| NFR-SEC-4 | Authorisation enforced **server-side** on every endpoint. Hiding UI is not access control. |
| NFR-SEC-5 | Local client caches (SQLite / Room) containing business data are encrypted at rest on the device — mandatory for Android, which leaves the premises. |
| NFR-SEC-6 | Audit log is readable only by Manager and Owner. |

### 7.6 Usability

| ID | Requirement |
|---|---|
| NFR-USE-1 | The sales grid mirrors the workbook column order so staff recognise it on day one: Size, Grade, Weight, Selection, Rejection(ro), Price/ct, ExRate, Less1, Less2, Amount(ro), Remark. |
| NFR-USE-2 | Every sales-entry action reachable by keyboard; the mouse is never required. |
| NFR-USE-3 | Numeric fields right-aligned, tabular figures, thousands grouping, ₹ formatting. |
| NFR-USE-4 | Validation messages are inline, field-level, and state the fix. |
| NFR-USE-5 | Computed fields are visually distinct and non-focusable. |
| NFR-USE-6 | Trade vocabulary in the UI is the business's own (Kapna, selection, rejection, less, avelu) — not translated into generic ERP terms. |
| NFR-USE-7 | English UI. ⚠ ASSUMED — Gujarati labels not required (Q15). |

### 7.7 Maintainability & operability

| ID | Requirement |
|---|---|
| NFR-MNT-1 | The calculation engine exists in exactly one place, is unit-tested rule by rule, and is the only implementation of CALC-1…11. |
| NFR-MNT-2 | Adding a grade or a size is data entry, not code or screen work (IMP-1). |
| NFR-MNT-3 | Automated backup of the server database, daily, restore rehearsed before go-live. The business is giving up a file they could copy to a pen drive — this is a hard requirement, not a nicety. |
| NFR-MNT-4 | Desktop client updates without reinstalling by hand on each machine. ⚠ ASSUMED — confirm deployment appetite. |
| NFR-MNT-5 | Structured server logs with correlation IDs across sync operations. |

### 7.8 Compliance & localisation

| ID | Requirement |
|---|---|
| NFR-CMP-1 | Base currency INR; ₹ formatting; Indian date convention. |
| NFR-CMP-2 | Financial year handling for reporting periods. ⚠ ASSUMED Apr–Mar (Q16). |
| NFR-CMP-3 | GST / tax on printed bills — **unresolved**, see GAP-2. |

---

## 8. What we need before development starts

Nothing in Phase 2 should begin until these are closed. Owner column = who supplies it.

### 8.1 Artifacts (blocking)

| # | Artifact | Why blocking | Owner |
|---|---|---|---|
| A1 | **`Sale File Sample.xlsx`** — the real file | Phase 1 forensics are currently *trusted, not verified*. Needed to confirm every formula and column. | Client |
| A2 | **`Blank new master file.xlsx`** — the real file | Same; also the only source for grade list, size-per-grade, and the price constants. | Client |
| A3 | **A populated master file**, not the blank template | The blank file has no real balances. Migration cannot be designed against an empty workbook. | Client |
| A4 | The real **price lists** (stock / rejection / sale) | Scattered as constants today (`II!C21=44000`). Needed for MDM-003 and for valuation. | Client |
| A5 | A sample **printed invoice / bill** as issued today | RPT-002 has no layout, and tax treatment is unknown. | Client |
| A6 | Current **exports the accountant consumes** | RPT-001 must not break a downstream process. | Client / accountant |
| A7 | The **commit of the spec HTML** into this repo | Only `docs/` exists; the source of truth is outside version control (G4). | Us |

### 8.2 Decisions the client must make (blocking)

| # | Decision | Default if unanswered |
|---|---|---|
| D1 | Answers to Q1–Q12 (§10) | Assumptions in this document stand |
| D2 | Approve or reject each of IMP-1…13 individually | All accepted |
| D3 | Is the purchase/payable side in scope? (GAP-1) | Out of scope for v1 |
| D4 | GST/tax on the printed bill? (GAP-2) | Not required for v1 |
| D5 | Where does the server run — office box, VPS, cloud? (GAP-5) | Blocking, no default: it drives cost, security, and whether Android can reach it at all |
| D6 | Are pieces tracked alongside carats? (GAP-6) | Not tracked |
| D7 | Number of users and named individuals per role | Blocking for AUTH-002 |

### 8.3 Environment & access (blocking for build, not for design)

| # | Item |
|---|---|
| E1 | Server host or cloud subscription, with network reachable from outside the office (Android + offline sync require it) |
| E2 | Database instance (PostgreSQL or SQL Server — decision belongs to Phase 2) |
| E3 | Source control, CI, and an artifact store for desktop client builds |
| E4 | Windows machine spec on the sales floor, and their OS version |
| E5 | Android device models and minimum OS version the owner will use |
| E6 | Code-signing arrangement for the Windows client and a Play Store account (or sideload policy) |
| E7 | Backup target and retention policy |

### 8.4 Sign-offs required to close Phase 1

- [ ] Business workflow (§3) confirmed as how the business actually operates
- [ ] Role matrix (§2.2) confirmed
- [ ] Business rules (§6) confirmed, especially every ⚠ ASSUMED rule
- [ ] Q1–Q12 answered (§10)
- [ ] IMP-1…13 approved or rejected individually
- [ ] Gaps GAP-1…7 dispositioned (in scope / out of scope)
- [ ] Spec defects G1, G2, G3 corrected in the source spec
- [ ] Volume assumptions NFR-VOL-1…5 confirmed
- [ ] MVP scope agreed as the spec's MVP list, or amended

---

## 9. Gaps in the spec itself

Raised so they are not discovered mid-build. Carried forward from [00-overview.md](00-overview.md), plus new.

| ID | Gap | Consequence if ignored |
|---|---|---|
| GAP-1 | **No purchase / supplier / payable side.** `RoughIntake` records a price but no supplier and no liability. | Receivables are modelled, payables are not. If rough is bought on credit, half the ledger is missing and margin is unverifiable. |
| GAP-2 | **GST / tax** absent, yet RPT-002 prints a BILL. | A printed bill that is not tax-compliant is unusable. |
| GAP-3 | **No stock reservation on DRAFT.** Two salespeople can draft-sell the same parcel; the clash only appears at post. Q10 covers negative stock, not double-selling. | Oversell on the trading floor. |
| GAP-4 | **No backup/restore story.** | They are giving up a file they could copy to a pen drive. Regression in resilience. |
| GAP-5 | **Deployment location unstated.** | Android + offline sync needs external reachability. Cost, security, and feasibility all hang on this. |
| GAP-6 | **Pieces count not modelled.** Parcels are often tracked as pieces + carats. | If pieces matter, the whole line model changes. |
| GAP-7 | **No opening-receivables migration.** Migration covers stock and sales, but not "who owed what on day one" independent of migrated invoices. | Day-one receivables may be wrong. |

### Spec defects to correct (from Overview)

| ID | Defect |
|---|---|
| G1 | 22 vs 23 grades — the KPI strip, MDM-001 and the tab list disagree. 22 grades is the consistent reading. |
| G2 | **CALC-8 DIFF is wrong as written.** It is non-zero by construction whenever rejection ≠ 0, because CALC-7's balance also subtracts rejection. Must become `diff = reported_balance − derived_balance`. **This is the audit rule that the whole trust story depends on — fix before Phase 2.** |
| G3 | Sieve buckets are not uniform per grade (Q6 says II and EX use three). Grade → allowed sizes is a real relationship, not a global list. |
| G4 | The two source workbooks are not in this repo. Forensics are trusted, not verified. |

---

## 10. Open questions for the client

Q1–Q12 are from the spec with its assumptions. Q13–Q16 are new, raised by this analysis.

| # | Question | Assumption if unanswered |
|---|---|---|
| Q1 | Shape / cut / individual certification ever needed, or always grade+size parcel trading? | Parcel by grade only; 4-C fields optional/nullable |
| Q2 | Do any goods carry lab certificates (GIA/IGI) tracked per stone? | No for MVP; `cert_no` optional |
| Q3 | Margin cost basis — rough intake price, weighted-avg stock cost, or standard cost? | Weighted-avg stock cost (CALC-6) |
| Q4 | Is broker % a deduction from the buyer's price, a commission payable, or both? | Both — deducted per `Sale · Q` **and** separately reported (CALC-11) |
| Q5 | Inventory aging from rough intake date or entry into current grade post-conversion? | Original intake date |
| Q6 | Exact sieve definitions (mm)? Do some grades really use only three buckets? | Grade-specific bucket sets as seen (II/EX 3, NO 1 uses 4) |
| Q7 | Is Ex Rate for foreign-currency deals or always 1 (INR)? | Base INR, ex-rate 1, multi-currency off by default |
| Q8 | Do terms run from invoice date or dispatch date? | Invoice date (CALC-10) |
| Q9 | What do `Type` values other than `BILL` mean (memo / approval / consignment)? | `BILL` = final sale; others to be enumerated |
| Q10 | Should posting be blocked, warned, or allowed on negative stock? | Configurable, default warn (CFG-003) |
| Q11 | Who owns rough intake & conversions — a dedicated stock manager or the owner? | Manager role |
| Q12 | Are sub-grade rows ("NO 1 MB", "GH-VVS MA") distinct stock grades needing master entries? | Conversion targets / price rows, folded into Grade + PriceList |
| **Q13** | One company and one location, or multiple entities/branches? | Single entity, single location |
| **Q14** | Can a single line be cancelled, or only a whole invoice? | Whole invoice only |
| **Q15** | Is any Gujarati-language UI needed? | English only |
| **Q16** | Reporting financial year — April–March? | April–March |

---

## 11. Scope

### In scope for v1 (MVP, per spec §5.4)

Auth & roles · Grade/Buyer/Broker masters · keyboard sales entry with live calcs · posting with
automatic stock deduction · rough intake · stock position · receipts & outstanding · calculation
engine & rounding · audit trail · data migration from both workbooks.

Stories: `AUTH-001/002, MDM-001/002, SALES-001/002/003, CALC-001/002, INV-001/002/003, PAY-001, AUD-001`.

### Explicitly out of scope for v1

| Item | When |
|---|---|
| Android dashboard, receivables ageing, offline sync, conversions, rejections, price list, exports | Phase 2 |
| Margin/profitability, broker cost, top movers, notifications, invoice print, settings screens | Phase 3 |
| Purchases / suppliers / payables | Not scoped — pending D3 |
| GST / tax computation | Not scoped — pending D4 |
| Per-stone certification, shape, cut | Not scoped — pending Q1/Q2 |
| Multi-branch, multi-entity | Not scoped — pending Q13 |

### Constraints

| ID | Constraint |
|---|---|
| C1 | Clients are **Windows desktop** and **Android**. No web client is in scope. |
| C2 | Base currency INR. |
| C3 | Sales entry must work offline — the trading floor's network is not assumed reliable. |
| C4 | Entry speed must at least match Excel, or staff will not adopt it. This is a hard constraint, not a wish. |
| C5 | Migration must run against the client's live workbooks with a parallel-run period before cut-over. |

---

## 12. Risks

| ID | Risk | L | I | Mitigation |
|---|---|---|---|---|
| R1 | **Source workbooks not yet supplied.** Every forensic claim is unverified. | H | H | Blocking artifact A1–A3; no Phase 2 data model is final until they are read. |
| R2 | **Adoption fails on entry speed.** Staff revert to Excel. | M | H | SALES-001 keyboard-first design; timed UAT against Excel (BO-2); observe real entry before designing the grid. |
| R3 | **Migration cannot reconcile.** Non-zero DIFF at cut-over, and no one knows the true opening balance. | H | H | Reconcile before go-live; parallel run for one cycle; sign-off on a validation report; treat unreconcilable grades as an explicit opening adjustment, recorded as an ADJUST movement with a reason. |
| R4 | **Deployment undecided (GAP-5).** Android + sync need an externally reachable server. | H | M | D5 is a blocking decision. |
| R5 | **Scope creep from the missing purchase side (GAP-1).** | M | H | D3 decision, in writing, before Phase 2. |
| R6 | **Offline sync complexity underestimated.** SYNC-001 is 13 points, the joint-largest story. | M | M | Append-only ledger design removes the hard case; only invoice headers need conflict rules. Keep it in Phase 2, never in MVP. |
| R7 | **Grade/alias mapping wrong.** Sales silently posts against the wrong grade. | M | H | Alias mapping reviewed and signed by the stock manager before migration; import report lists every alias resolution. |
| R8 | **G2 DIFF defect shipped.** The audit that proves the system is trustworthy is itself wrong. | M | H | Correct the rule in the spec now; unit-test the invariant with rejection ≠ 0. |
| R9 | **Client unavailable for the 16 open questions.** | M | M | Documented defaults everywhere; proceed on assumptions, flag each in the build, revisit at UAT. |

---

## 13. Phase 1 exit criteria

Phase 1 is complete when:

1. All §8.4 sign-offs are obtained.
2. Blocking artifacts A1–A3 are in hand and §1.2's forensic claims are **verified against the real files**, not trusted.
3. Q1–Q16 are answered, or their defaults are explicitly accepted in writing.
4. G1–G3 are corrected in the source spec.
5. GAP-1…7 are each marked in-scope or out-of-scope.
6. MVP scope is agreed and frozen for the first release.

---

## 14. What Phase 2 will produce

Named here only so the boundary of Phase 1 is clear. **Not started until this document is approved.**

- Solution architecture and project structure (.NET backend, WPF desktop, Kotlin/Compose Android)
- Normalised data model and physical schema, from spec §2.1, corrected for G1–G3
- The calculation engine as a testable, dependency-free component with a test per CALC rule
- API contract (endpoints, DTOs, error model, idempotency and sync protocol)
- Migration design against the real workbooks
- Non-functional design: auth, audit storage, offline outbox, backup
- Environment and deployment topology, once D5 is answered
