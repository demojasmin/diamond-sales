# 08 · Phase 2 — Migration Design

Status: **draft** · ⚠ **designed against the blank template — A3 still outstanding**
Covers [02 §14](02-requirement-analysis.md) deliverable 5. Implements MIG-001/002/003
([05 §2](05-backlog.md)) against the facts established in
[04-workbook-verification.md](04-workbook-verification.md).

> Everything here is derived from the two real files. Where the file could not answer a question,
> the row goes to an **exceptions report** — nothing is guessed, ever. That rule is what makes the
> cut-over reconciliation meaningful.

---

## 1. Order

```
  1 masters  →  2 opening stock  →  3 sales  →  4 receipts  →  5 reconcile  →  6 sign-off
     ▲                                                              │
     └──────── exceptions report, re-run until empty ───────────────┘
```

Every stage is **re-runnable against an empty database and produces an identical result**
(NFR-INT-5). The migration is a program that is run many times during rehearsal and exactly once
for real, not a script someone babysits.

---

## 2. Stage 1 — masters (MIG-001)

### 2.1 Grades — the seed list is final

22 grades, names verbatim from the workbook ([04 §3.1](04-workbook-verification.md)). Canonical
codes are assigned now; every legacy spelling becomes a `grade_alias` row.

| # | Sheet name (verbatim) | Canonical `code` | `display_name` | Known aliases |
|---|---|---|---|---|
| 1 | `1 ` (trailing space) | `NO_1` | NO 1 | `1 `, `1`, `NO 1` |
| 2 | `1 BB` | `NO_1_BB` | NO 1 BB | `1 BB`, `1BB`, `NO 1 BB` |
| 3 | `II` | `NO_II` | NO II | `II`, `NO II` |
| 4 | `EX 1` | `EX_1` | EX 1 | `EX 1`, `EX1`, `Ex1` |
| 5 | `NO-2 ` (trailing space) | `NO_2` | NO 2 | `NO-2 `, `NO-2`, `2` |
| 6 | `NO-DX` | `NO_DX` | NO DX | `NO-DX`, `DX` |
| 7–11 | `NO-3` … `NO-7` | `NO_3` … `NO_7` | NO 3 … NO 7 | `NO-3`, `3`, … |
| 12 | `TOP-COL` | `TOP_COL` | TOP-COL | `TOP-COL`, `Col` |
| 13 | `COL ` (trailing space) | `COL` | COL | `COL `, `COL` |
| 14 | `OW` | `OW` | OW | |
| 15–17 | `LC-1` … `LC-3` | `LC_1` … `LC_3` | LC-1 … LC-3 | |
| 18 | `GH` | `GH` | GH | `GH`, `GH-VVS` ⚠ Q12 |
| 19–20 | `LB-1`, `LB-2` | `LB_1`, `LB_2` | LB-1, LB-2 | |
| 21 | `+14` | `PLUS_14` | +14 | `+14`, `14` |
| 22 | `EXTRA` | `EXTRA` | EXTRA | |

**A canonical code never contains a space, a sign or punctuation.** The trailing space on `1 ` is
precisely DQ-5, and MDM-001's new acceptance criterion says it must not reach the database.

Aliases marked from the sales sheet (`1BB`, `II`) are confirmed present; the rest are seeded
defensively and extended by stage 3 whenever an unrecognised spelling appears.

⚠ **Q12 open:** `GH` carries 142 formulas against a typical ~80 — the GH-VVS sub-rows are real
structure ([04 §3.8 B-11](04-workbook-verification.md)). Until the client answers, GH-VVS migrates
as a `price_list` row under `GH`, and the exceptions report names it.

### 2.2 Sizes — four canonical, four notations (MDM-004)

| Canonical | Aliases found in the files |
|---|---|
| `-2` | `-2`, `1/5` / `0.2` ⚠ (see below) |
| `-6.5` | `-6.5`, `6.5-`, ` -6.50    `, `,-6.5` |
| `+6.5` | `+6.5`, `6.5+`, `,+6.5`, ` +6.50   ` |
| `+11` | `+11`, `11+`, `,+11`, `  +11   ` |

Aliases are matched **after** trimming and collapsing internal whitespace, and matching is
case-insensitive. Any unmatched value is an exception, not a guess.

⚠ **The `0.2` cell.** One sales `Size` cell holds the *number* `0.2`, displayed as `1/5` by a
fraction number format. It is almost certainly `-2` typed into a cell Excel decided was a fraction.
**It migrates to the exceptions report for manual mapping — never silently coerced** (MDM-004 AC 3).
One cell, one human decision, no invented data.

### 2.3 `grade_size`

All 22 grades get `-6.5`, `+6.5`, `+11`. **`NO_1` and `NO_1_BB` additionally get `-2`** — they are
the only 4-size grades ([04 §3.4](04-workbook-verification.md)). Seeded directly; not inferred at
runtime.

### 2.4 Buyers & brokers

Distinct values from `Sheet1` col C (`Name`) and col D (`Broker`), trimmed and case-folded for
duplicate detection. `default_terms_days` seeds from the most common `Terms` value per buyer;
`default_broker_pct` from the most common `Broker %` per broker. Both are defaults, editable
afterwards — the migration is not the last word on master data.

---

## 3. Stage 2 — opening stock (MIG-002)

### 3.1 The parser rule that everything depends on

**Sections are located by their `SUM` anchor, never by row number.** Row anchors differ on every
sheet — sales totals sit at rows 39, 40, 41, 42, 43, 44, 49 and 59 depending on the grade
([04 §3.3](04-workbook-verification.md)). A fixed-row parser would silently read the wrong block on
most of the 22 sheets, and produce numbers that look plausible.

```
for each grade sheet:
    read row 3 headers  →  the sizes this sheet uses (3 or 4) and their column positions
    find each total cell of the form  =SUM(<col><a>:<col><b>)
        →  that cell is a section total; rows a..b are its members
    order the sections down the sheet: stock, rejection, sales, balance
    the roll-up column follows the size count: R/S on 4-size sheets, N/O on 3-size sheets
```

Column positions are read from the header row too. Nothing about the layout is hard-coded — that is
the direct consequence of [04 §3.3](04-workbook-verification.md) and [04 §3.4](04-workbook-verification.md).

### 3.2 What becomes what

| Workbook | Becomes |
|---|---|
| Grade × size **balance** (the sheet's own closing figure) | One `INTAKE` movement per grade × size, dated the cut-over date, priced at the sheet's weighted-average |
| Rough intake detail in `KAPNA ADD` cols A–K | Retained as the intake batch's provenance where it is legible; **not** re-added as stock (it is already inside the balance) |
| Historical rejections / sales inside the grade sheets | **Not migrated as movements.** They are already netted into the balance. Re-posting them would double-count |

**Opening balances are migrated as a position, not as a history.** Reconstructing years of movements
from a workbook that cannot even reconcile itself would import its errors with full fidelity. The
opening position is one dated `INTAKE` per bucket; everything after cut-over is real history.

### 3.3 Known traps, each with its rule

| Trap | Evidence | Rule |
|---|---|---|
| Placeholder micro-weights | `1E-5` … `1E-13` across sheets ([04 B-12](04-workbook-verification.md)) | `abs(w) < 1e-4` → **0**. A threshold, never an equality test |
| `#DIV/0!` cached prices | `'1 '!C11`, `G11`, `K11`, `O11`, `S11`, `C44`, `KAPNA ADD!O2` … ([04 B-1](04-workbook-verification.md)) | Price = 0 **and** the row goes to the exceptions report. An error is not a number |
| Pre-existing negative balances | `'1 '!B44 = −2.02E-6`, `KAPNA ADD!R309 = −0.0127` ([04 B-2](04-workbook-verification.md)) | Migrate the negative as-is, then raise it in MIG-003. Never clamp to zero — that would hide the very drift this project is here to fix |
| **Row 44 vs row 46** | Two balance figures per grade: same weight, different price basis (`C44 = C11` weighted-avg, `C46 = C4` opening) ([04 B-4](04-workbook-verification.md)) | 🔴 **Undecided — needs the client.** Default: **row 44**, weighted-average, matching CALC-6. Every migrated price is flagged until confirmed |
| Stock rows that re-add rejection | `'1 '!B8 = B19`, `B9 = B20` ([04 B-5](04-workbook-verification.md)) | Only the section **totals** are read, so the double-count inside the sheet does not propagate. Listed in the report for the client to explain |
| `KAPNA ADD!J2` is not a `SUM` | `J2 = B15`, a direct reference ([04 §3.6](04-workbook-verification.md)) | The intake block is transposed. Follow the reference; do not assume a block sum |

---

## 4. Stage 3 — sales

| Step | Rule |
|---|---|
| Group | `Sheet1` rows grouped by `Sr.` → one `sales_invoice` with N `sales_line` rows (DQ-9) |
| Header | Date, buyer, broker, broker %, terms, type taken from the **first** row of the group; a group whose header values disagree across rows goes to the exceptions report |
| Broker % | Per-line in Excel, per-invoice in the model. Consistent in the sample, but **the check is mandatory** — F-4 |
| Amounts | **Recomputed with CALC-1**, never copied. The stored floats carry 10+ decimals of noise |
| Rounding delta | Where the recomputed amount differs from the cached one by more than ₹0.01, the row is reported. Expected on every row: `Q3 = 139864.725` becomes `139864.73` |
| Zero-selection lines | Migrate normally. A fully-rejected line is real business ([04 A-2](04-workbook-verification.md)) |
| `Terms = 0` | Valid. Due date = invoice date ([04 A-3](04-workbook-verification.md)) |
| Grade / size | Resolved through the alias tables. Unmapped → exception, never a guess |
| Status | `POSTED`, with the historical date, and **no `SALE` movements** — the opening balance is already net (§3.2). This is the one place migration deliberately breaks the posting rules, and it is why the cut-over date matters |
| `invoice_no` | Assigned from `Sr.` with a `MIG-` prefix so migrated numbers never collide with the live sequence |

### 4.1 Rejection comments → dispositions

The rejection-breakdown comments ([04 §2.5](04-workbook-verification.md)) are the real sales↔stock
link, and they are free text: `13.46 Selection · 4.62 Reparing · 6.31 FL+Col+II`.

**They are parsed on a best-effort basis into `rejection_disposition` rows and every one is
flagged for review.** Two of the three sample comments do not even sum to their rejection total
(24.39 vs 24.40; 0.07 vs 15.82). Text this loose cannot be trusted into a ledger unreviewed. Where
the weights do not sum, the dispositions import as **draft** and MIG-003 lists them.

The same applies to quantity-bearing remarks — `7.80 culet repair` ([04 A-4](04-workbook-verification.md)).

> `ponytail:` the parser is a regex for `<number> <words>` plus a synonym table
> (`Selection→RESELECT`, `Reparing|Repair→REPAIR`, `Culet→CULET`, a known grade name→`REGRADE`).
> Three comments exist in the sample. Anything cleverer is speculation about text nobody has seen.

---

## 5. Stage 4 — receipts

One `Rec. Amt` > 0 → one `receipt`, dated the invoice date (the sheet records no payment date —
this is an **assumption**, and it is listed in the exceptions report so the client sees it).

Where the residue falls below `settlement_write_off_threshold`, the invoice settles and the
difference posts as a rounding adjustment (PAY-003). The sample's one outstanding balance, ₹−0.275,
closes here instead of being carried into the new system as a phantom receivable
([04 §2.4](04-workbook-verification.md)).

---

## 6. Stage 5 — the reconciliation report (MIG-003)

This is what `CALC-8` becomes: not a runtime rule, a one-off proof that migration was faithful.

**Per grade × size:**

| Column | Source |
|---|---|
| Workbook balance | The grade sheet's own closing figure |
| Our balance | `SUM(weight_ct)` |
| Difference | The two, subtracted |
| Value | Our balance × weighted-avg price |

**Company level:** total stock, total sales, total outstanding — the last two compared against
`K1` / `Q1` / `S1` **after correcting the SUM-range bug**. Our figures should be **greater than or
equal to** the workbook's, because the sheet's totals stop at rows 854 / 720 / 659 and ours do not.
A figure that comes out *lower* is a migration defect, not an improvement.

**Every non-zero difference blocks go-live** until it is dispositioned. An accepted residual becomes
an `ADJUST` movement carrying a reason — visible forever, never absorbed.

**Also reported:** every exception from stages 1–4, the count of recomputed amounts that moved,
every draft disposition, and every `#DIV/0!` price.

---

## 7. Stage 6 — cut-over

1. Rehearse the full migration against a copy. Run the report. Fix. Repeat until the exceptions list
   is empty or every remaining line is explicitly accepted.
2. **Rehearse the restore** (OPS-001) and re-run the reconciliation against the restored copy.
3. Freeze the workbooks. Migrate for real. Record the cut-over date.
4. Parallel run for one cycle — app and workbook side by side ([02 §8](02-requirement-analysis.md)).
5. Sign off on the validation report: per-grade stock, per-buyer outstanding, total sales.

After the cut-over date the workbook is read-only history. Two systems maintained in parallel
indefinitely is how the original problem was built.

---

## 8. Blocked

| # | Blocker | Effect |
|---|---|---|
| 🔴 **A3** | The supplied master file is the **blank template** — its balances are `1e-13` placeholders | Stages 2 and 5 cannot be tested against real numbers. The design is complete; the rehearsal is not possible |
| 🔴 **B-4** | Row 44 vs row 46 — which is "the" balance | Every migrated stock **price** is provisional |
| 🔴 | Who re-keys the rejection comments, and by what rule | §4.1 stays best-effort |
| Q12 | GH-VVS sub-rows | Whether `GH` seeds one grade or several |
| F-4 | Per-line broker % on historical invoices | Rule exists (report and stop); untested until a file shows one |
| G2 | Sign-off on deleting `CALC-8` | MIG-003 is the agreed replacement |
