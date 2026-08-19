# 05 · Phase 3 — Build Backlog

Status: **draft**
Covers spec §3 (28 stories, 12 epics), reconciled against
[04-workbook-verification.md](04-workbook-verification.md).

> This document does **not** re-transcribe the 28 stories — they are in the spec HTML and unchanged
> unless listed below. It records what verification **changed**, what it **added**, and the order
> things get built in.

---

## 0. What verification did to the backlog

| | Count | Points |
|---|---|---|
| Stories unchanged | 21 | — |
| Stories **amended** (acceptance criteria wrong or incomplete) | 7 | — |
| Stories **added** (gaps the spec never had) | 7 | 31 |
| **Total backlog** | **35** | **192** |

Two of the additions are not enhancements — they are **holes**. The spec's MVP ships "migration"
and the business gives up a file it could copy to a pen drive, yet **neither migration nor backup
had a story, points, or acceptance criteria**. They were scope with no work attached.

---

## 1. Amended stories

### MDM-001 · Canonical Grade dictionary — **amend**

- ✅ The 22 grade names are now known verbatim. The seed list is no longer an unknown.
- ❌ AC "Given the 22 grades in the workbook…" — spec elsewhere says 23. **22 is correct.**
- ➕ New AC: *Given the sheet name `1 ` with a trailing space, when imported, then it maps to canonical `NO_1` and the trailing space never reaches the database.*

### MDM-003 · Price list — **amend**

- ➕ New AC: *Given a grade/size whose stored price is a placeholder below 1e-4, when imported, then it is treated as absent, not as a price.* (DQ-2: placeholders range `1e-5` … `1e-13`, so this must be a **threshold test, not an equality test**.)

### CALC-002 · Rounding & precision — **amend, and its premise was wrong**

The story exists to kill "phantom −0.27 outstanding values (DQ-6)". Verification shows `−0.275` is
**not** float noise — it is a buyer paying a round ₹139,865 against ₹139,864.725. Decimal precision
never fixes that.

- ❌ Remove the claim that this story resolves DQ-6.
- ➡ DQ-6 moves to **PAY-003** (new).
- ✅ Keep everything else: money 2 dp, carats 4 dp, one rounding boundary at the line.

### INV-002 · Stock ledger — **amend**

- ❌ Remove AC *"Given DIFF audit runs, then reported vs derived balance match (CALC-8 = 0)."*
  CALC-8 is deleted — confirmed in the file as `−rejection`. There is no second number to compare.
- ➕ Replace with invariants INV-1…INV-6 from [03 §3.9](03-domain-model.md), each a test:
  conversions conserve weight, signs match types, a posted invoice has one SALE movement per line,
  **a cancelled invoice's movements sum to zero**.

### INV-005 · Rejection recording — **amend, materially**

As written it records a scalar. The workbook's comments show rejection is a parent quantity split
by destination: `13.46 Selection · 4.62 Reparing · 6.31 FL+Col+II`.

- ➕ New AC: *Given a rejection of Y ct, when I record dispositions, then their weights must sum to Y before I can save.*
- ➕ New AC: *Given a disposition with outcome REGRADE, then a destination grade is mandatory.*
- ⬆ Points **3 → 5**.

### SALES-001 · Sales entry — **amend**

- ➕ New AC: *Given a line where selection = 0 and the whole parcel is rejected, when I save, then it is accepted and its amount is 0.00.* (Verified: row 5 of the real sheet is exactly this. A validation that blocks zero-value lines would reject real business.)
- ➕ New AC: *Given terms = 0, when I save, then the due date is the invoice date.* (Verified: invoice 3 has terms 0.)

### SALES-003 · Post invoice — **amend**

- ➕ New AC: *Given a grade/size whose balance is already negative before posting, when I post, then the configured policy applies and the pre-existing negative is shown, not hidden.* (Verified: `KAPNA ADD!R309 = −0.0127` — the workbook ships with negative balances.)

---

## 2. New stories

### MDM-004 · Size master with aliases · **Must · MVP · 3 pts · Both**

> As a manager, I want one canonical list of sieve sizes with their aliases, so that sales lines and
> stock rows can be matched on size.

Verification found **four incompatible notations** for the same four sizes — sales uses `11+`,
`6.5+`, `6.5-` and stores one size as the *number* `0.2`; grade sheets use `-2`, `,+6.5`; KAPNA ADD
uses ` +6.50   `. This is DQ-4 all over again for sizes, and the spec never flagged it.

**AC**
1. Given the four canonical sizes, when I open the Size master, then `-2`, `-6.5`, `+6.5`, `+11` are present with sort order.
2. Given the alias `11+`, `,+11`, or ` +11   `, when data is imported, then all three resolve to `+11`.
3. Given the numeric value `0.2` in a sales Size cell, when imported, then it is flagged for manual mapping and **not** silently coerced.
4. Given grade `NO II`, when I pick a size on a sales line, then only that grade's three sizes are offered — not four.

**Rules** `size_bucket` + `size_alias` + `grade_size` · **Deps** MDM-001 · **Trace** DQ-11

### PAY-003 · Settlement write-off · **Must · MVP · 2 pts · Both**

> As an owner, I want invoices with a trivial residue to close automatically, so that hand-rounded
> payments stop leaving phantom balances in receivables.

The entire sample workbook has exactly one outstanding balance: **₹−0.275**, from a buyer paying a
round figure. Left alone, every rounded payment does this forever.

**AC**
1. Given an invoice whose |outstanding| is below `settlement_write_off_threshold`, when a receipt is recorded, then the invoice closes and the residue posts as a rounding adjustment.
2. Given the residue, when written off, then it is visible as an adjustment — never silently discarded.
3. Given |outstanding| above the threshold, then nothing is written off.

**Rules** CALC-3 + threshold setting · **Deps** PAY-001 · **Trace** DQ-12

### INV-006 · Rejection disposition capture · **Should · P2 · 5 pts · Desktop**

> As a stock manager, I want to record where rejected carats went, so that re-grading is data
> instead of a comment balloon someone retypes.

**AC**
1. Given a rejection, when I add dispositions, then each has a weight, an outcome (RESELECT / REPAIR / REGRADE / CULET / OTHER) and an optional note.
2. Given dispositions, when I save, then their weights sum to the rejection total.
3. Given a REGRADE disposition, then a destination grade is mandatory.
4. Given a REGRADE disposition, when the stock manager posts the conversion, then paired CONVERT_OUT / CONVERT_IN movements are created and linked back to it.

**Deps** INV-005, INV-004 · **Trace** DQ-13 · **Blocked by:** nobody has yet explained *who* re-keys these today and by what rule.

### MIG-001 · Master data migration · **Must · MVP · 5 pts · Backend**

> As the business, I want grades, sizes, buyers and brokers loaded from the workbooks, so that
> everything else has something to reference.

**AC**
1. Given both workbooks, when migration runs, then all 22 grades, 4 sizes, and every distinct buyer and broker exist with canonical codes.
2. Given every legacy spelling encountered, then an alias row exists mapping it to its canonical record.
3. Given an unmappable value, then it appears on an exceptions report — never guessed.
4. Given migration re-run on the same inputs, then the result is identical (NFR-INT-5).

### MIG-002 · Opening stock & sales migration · **Must · MVP · 8 pts · Backend**

**AC**
1. Given the grade sheets, when migrated, then each grade × size balance becomes INTAKE movements — **located by their `SUM` anchor, not by row number** (row anchors differ per sheet: sales totals sit at row 40, 39, 49, 41, 43…).
2. Given placeholder weights below 1e-4, then they migrate as zero.
3. Given `Sheet1` rows grouped by `Sr.`, when migrated, then each group becomes one invoice with N lines, and amounts are **recomputed** with CALC-1 rather than copied.
4. Given a `Rec. Amt`, then a Receipt is created.
5. Given historical invoices, when posted, then stock is **not** re-deducted — opening balances are already net.
6. Given an invoice whose lines carry differing broker %, then it is flagged for manual review (F-4).

### MIG-003 · Cut-over reconciliation report · **Must · MVP · 5 pts · Backend**

This is what CALC-8 becomes. Not a runtime rule — a one-off proof that migration was faithful.

**AC**
1. Given migration complete, when the report runs, then it shows per grade × size: workbook-reported balance, our derived balance, and the difference.
2. Given a non-zero difference, then it is listed for investigation and **blocks go-live** until dispositioned.
3. Given an accepted residual, when recorded, then it becomes an ADJUST movement with a reason — visible forever, not absorbed.
4. Given the sales totals, then they are compared against `K1`/`Q1`/`S1` **after** correcting the SUM-range bug, and our figures should be **≥** the workbook's.

### OPS-001 · Backup & restore · **Must · MVP · 3 pts · Backend**

> As an owner, I want automated backups and a rehearsed restore, so that giving up my Excel file
> isn't a step backwards.

They can currently copy a workbook to a pen drive. If the new system can't beat that, it is a
regression in resilience regardless of every other feature.

**AC**
1. Given a daily schedule, when it runs, then a complete database backup is produced and its success or failure is reported.
2. Given a backup, when a restore is rehearsed before go-live, then the restored system reconciles to the same figures.
3. Given a failed backup, then someone is notified.

**Trace** GAP-4, NFR-MNT-3

---

## 3. Build order

Dependency-driven, not priority-driven — priority decides *whether*, dependencies decide *when*.

| # | Block | Stories | Pts | Why here |
|---|---|---|---|---|
| **1** | **Calculation engine** | CALC-001, CALC-002 | 10 | Zero dependencies, everything depends on it. Pure functions, no UI, no database. A test per rule before anything calls it |
| **2** | **Identity** | AUTH-001, AUTH-002, AUD-001 | 13 | Audit needs users; every mutation after this point is attributable |
| **3** | **Master data** | MDM-001, MDM-002, MDM-004 | 9 | Nothing can reference a grade that doesn't exist |
| **4** | **Stock ledger** | INV-002, INV-001, INV-003 | 18 | The ledger before anything that writes to it |
| **5** | **Sales** | SALES-001, SALES-002, SALES-003 | 29 | ⚠ **First UI work.** SALES-001 alone is 13 pts |
| **6** | **Money in** | PAY-001, PAY-003 | 7 | |
| **7** | **Migration & go-live** | MIG-001, MIG-002, MIG-003, OPS-001 | 21 | Last in build order, first in risk. Cannot be designed against the blank template |
| | **MVP total** | 20 stories | **107** | |
| **8** | Phase 2 | DASH-001, PAY-002, SYNC-001/002, INV-004/005/006, MDM-003, RPT-001 | 62 | |
| **9** | Phase 3 | DASH-002, NOTIF-001, RPT-002, CFG-001/002 | 23 | |
| | **Grand total** | **35 stories** | **192** | |

**Block 1 is buildable today.** It needs no database, no client answers, no workbooks — CALC-1…11
are fully specified and verified against the real formulas. Everything from block 4 onward is
partly blocked (see §5).

---

## 4. Definition of done

A story is done when:

1. Acceptance criteria pass as automated tests.
2. Every calculation goes through the engine — no arithmetic re-implemented in a client.
3. Mutations write an audit entry.
4. Authorisation is enforced **server-side**, not by hiding UI.
5. Money is `decimal`. Any `float` in a money path is a defect, not a style preference.
6. No fixed row/record ceiling anywhere (IMP-10).
7. Deliberate shortcuts carry a `ponytail:` comment naming the ceiling and the upgrade path.

---

## 5. Still blocked

| Blocker | Blocks | Owner |
|---|---|---|
| **A3 — a populated master file.** The supplied one is the blank template; balances are `1e-13` placeholders | MIG-002, MIG-003 | Client |
| **Who re-keys the rejection comments, and by what rule?** | INV-006 | Client |
| **D5 — where the server runs** | SYNC-001/002, OPS-001, DASH-001 | Client |
| Q4 broker treatment | CALC-11, W14, RPT-002 | Client |
| Q6 sieve mm | `size_bucket.lower_mm` stays null | Client |
| Q9 `Type` values | `doc_type` CHECK stays open | Client |
| Q12 GH-VVS sub-rows | Grade seed — `GH` has 142 formulas vs ~80 typical, so the extra structure is real | Client |
| G2 sign-off on deleting CALC-8 | MIG-003 | Client |

None of these block **block 1**.
