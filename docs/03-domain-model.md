# 03 · Phase 2 — Domain Model

Status: **draft**
Covers spec §2.1 data model · §2.2 diamond attributes · §2.3 calculation engine · §2.4 roles.
Built on [01-workbook-forensics.md](01-workbook-forensics.md) and [02-requirement-analysis.md](02-requirement-analysis.md).

> **Design stance.** This is a small-data, high-integrity domain: ~10k stock movements a year,
> ~3k sale lines, ≤10 users. Correctness, auditability and offline behaviour are the only things
> that matter. Nothing here is sized for scale, and every table earns its place — where the spec
> proposed an entity we don't yet need, we say so and record what would bring it back.

---

## 0. Corrections applied to the spec's model

| # | Spec said | Model does | Why |
|---|---|---|---|
| C-1 | Sizes are a global list of four | `grade_size` junction — sizes are **per grade** | G3 / Q6: II and EX use three |
| C-2 | `CALC-8 diff = balance − (stock − sale)`, assert 0 | **Deleted as a runtime rule.** Survives only as a migration-time reconciliation | F-5: it equals −rejection. And in this model there is no second number to reconcile *against* — see §3.8 |
| C-3 | `RoughIntake` is its own entity | Dropped. Intake **is** a `stock_movement` of type `INTAKE` | Identical columns. One table, one write path, one balance query |
| C-4 | `Currency` entity | Dropped. Base currency in settings, `ex_rate` already on the line | Q7: always 1. A table for a constant |
| C-5 | Balance = `Σintake + Σconvert_in − Σconvert_out − Σrejection − Σsale` | Balance = `SUM(weight_ct)` | Signed weights + a CHECK per type. Same rule, no code |
| C-6 | Permissions implied as data | Role is an enum; the capability map is code | 3 fixed roles. A permissions table is a product feature nobody asked for |
| C-7 | `broker_pct` on the invoice header | Kept on the header, **but** the engine takes it as a parameter | F-4: it is per-line in Excel. Migration must handle invoices whose lines disagree |
| C-8 | — | Added `app_user`, `audit_entry`, `change_request`, `app_setting` | Required by AUTH-001/002, AUD-001, SALES-002, CFG-001/002 and absent from §2.1 |

### Applied after workbook verification (2026-07-25)

Five changes from [04-workbook-verification.md](04-workbook-verification.md). Four additions, one
confirmation — the schema below was not invalidated by anything in the real files.

| # | Change | Driver |
|---|---|---|
| V-1 | **C-2 confirmed.** `KAPNA ADD!T2 = −4.0199999999999996E-6`, exactly `−'1 '!B23`. DIFF is minus rejection in the live file. Deleting `CALC-8` was right | F-5 verified |
| V-2 | **`size_alias` added** — mirrors `grade_alias` | DQ-11: four notations for four sizes (`11+` / `,+11` / ` +11   ` / `+11`), and one size stored as the *number* `0.2` |
| V-3 | **`rejection_disposition` added** — rejection is a parent quantity with child destinations | DQ-13: the sales workbook's comments split each rejection by destination grade (`13.46 Selection · 4.62 Reparing · 6.31 FL+Col+II`). `INV-005` as specified is materially incomplete |
| V-4 | **`settlement_write_off_threshold` setting added** | DQ-12: `Rec. Amt = 139865` against an amount of `139864.725`. Decimal precision cannot fix a hand-rounded payment |
| V-5 | **`grade_size` is now seedable** | G3 closed: all 22 grades take `-6.5`, `+6.5`, `+11`; only `NO 1` and `NO 1 BB` also take `-2` |

---

## 1. Entity map

```
  app_user ──creates──┐
                      │
   grade ──┬── grade_alias                    ┌── sales_line ──┐
           ├── grade_size ──── size_bucket    │                │
           └── price_list                     │                │
                                              │                │
   buyer ────┐                                │                │
   broker ───┼──────────▶ sales_invoice ──────┘                │
             │                 │  │                            │
             │                 │  └──── receipt                │
             │                 │                               │
             │                 └──── change_request            │
             │                                                 │
             └─────────────────  stock_movement ◀──────────────┘
                                  (ref_type / ref_id)
                                        │
   audit_entry ──── every table above ──┘        app_setting
```

Two append-only ledgers carry all derived truth: **`stock_movement`** (balances) and
**`receipt`** (outstanding). Nothing else is ever aggregated into a stored figure.

---

## 2. Data model

### 2.1 Conventions

| Decision | Choice | Reason |
|---|---|---|
| **Primary keys** | `uuid` (v7, time-ordered) on every table | Clients create invoices, lines, receipts *and* master rows (MDM-002 has inline add-new) while offline. One rule beats a per-table judgement call |
| **Money** | `decimal(18,2)` | Never `float`. BR-ROUND-6 — this is what DQ-6 was |
| **Carats** | `decimal(18,4)` | BR-ROUND-2 |
| **Percentages** | `decimal(5,2)`, `0–100` | Stored as typed, e.g. `1.50` = 1.5 %. Never as a 0–1 fraction — the workbook uses whole numbers and so will everyone reading the data |
| **Deletes** | None. Status columns + compensating entries | BR-INV-1, AUD-002 |
| **Timestamps** | `timestamptz`, UTC | Offline clients in one zone today, but clock discipline is free now and expensive later |
| **Enums** | `varchar` + `CHECK` | Portable across PostgreSQL and SQL Server; readable in a raw table dump, which is what you want at 2am |

### 2.2 Master data

```sql
CREATE TABLE app_user (
  user_id        uuid PRIMARY KEY,
  username       varchar(60)  NOT NULL UNIQUE,
  display_name   varchar(120) NOT NULL,
  password_hash  varchar(255) NOT NULL,          -- argon2id
  role           varchar(12)  NOT NULL CHECK (role IN ('SALES','MANAGER','OWNER')),
  active         boolean      NOT NULL DEFAULT true,
  failed_logins  int          NOT NULL DEFAULT 0,
  locked_until   timestamptz  NULL,
  created_at     timestamptz  NOT NULL
);

CREATE TABLE grade (
  grade_id      uuid PRIMARY KEY,
  code          varchar(20) NOT NULL UNIQUE,     -- canonical, e.g. NO_1_BB
  display_name  varchar(60) NOT NULL,            -- as shown, e.g. "NO 1 BB"
  sort_order    int         NOT NULL,
  active        boolean     NOT NULL DEFAULT true
);

CREATE TABLE grade_alias (                        -- resolves DQ-4/DQ-5
  alias      varchar(40) PRIMARY KEY,             -- '1BB', '1 BB', 'NO 1 BB', '1 '
  grade_id   uuid NOT NULL REFERENCES grade
);

CREATE TABLE size_bucket (
  size_id     uuid PRIMARY KEY,
  code        varchar(10) NOT NULL UNIQUE,       -- '-2','-6.5','+6.5','+11'
  lower_mm    decimal(6,2) NULL,                 -- unknown until Q6
  upper_mm    decimal(6,2) NULL,
  sort_order  int NOT NULL
);

CREATE TABLE size_alias (                         -- V-2 / DQ-11: four notations, one size
  alias    varchar(20) PRIMARY KEY,               -- '11+', '+11', ',+11', ' +11   ', '0.2'
  size_id  uuid NOT NULL REFERENCES size_bucket
);

CREATE TABLE grade_size (                         -- C-1 / G3: sizes are per grade
  grade_id  uuid NOT NULL REFERENCES grade,
  size_id   uuid NOT NULL REFERENCES size_bucket,
  PRIMARY KEY (grade_id, size_id)
);
-- V-5 seed, verified from the workbook:
--   every grade      -> '-6.5', '+6.5', '+11'
--   NO 1, NO 1 BB    -> also '-2'   (they are the only two 4-size grades)

CREATE TABLE buyer (
  buyer_id            uuid PRIMARY KEY,
  name                varchar(120) NOT NULL UNIQUE,
  default_terms_days  int NOT NULL DEFAULT 0 CHECK (default_terms_days >= 0),
  credit_limit        decimal(18,2) NULL,
  active              boolean NOT NULL DEFAULT true
);

CREATE TABLE broker (
  broker_id           uuid PRIMARY KEY,
  name                varchar(120) NOT NULL UNIQUE,
  default_broker_pct  decimal(5,2) NOT NULL DEFAULT 0 CHECK (default_broker_pct BETWEEN 0 AND 100),
  active              boolean NOT NULL DEFAULT true
);

CREATE TABLE price_list (
  price_id        uuid PRIMARY KEY,
  grade_id        uuid NOT NULL REFERENCES grade,
  size_id         uuid NOT NULL REFERENCES size_bucket,
  context         varchar(10) NOT NULL CHECK (context IN ('STOCK','REJECTION','SALE')),
  price_per_ct    decimal(18,2) NOT NULL CHECK (price_per_ct >= 0),
  effective_from  date NOT NULL,
  effective_to    date NULL,                     -- NULL = current
  CHECK (effective_to IS NULL OR effective_to > effective_from)
);
CREATE UNIQUE INDEX ux_price_current ON price_list (grade_id, size_id, context)
  WHERE effective_to IS NULL;                     -- one open price per combination
```

**`grade_alias` as its own table, not a column.** The spec put `aliases` on `grade`. A lookup table
makes the import join a one-liner and the uniqueness constraint real — two aliases cannot both claim
to be `1 BB`, which is precisely the failure DQ-4 describes.

### 2.3 Sales

```sql
CREATE TABLE sales_invoice (
  invoice_id     uuid PRIMARY KEY,               -- client-generated, offline-safe
  invoice_no     varchar(20) NULL UNIQUE,        -- server-assigned AT POST, not before
  invoice_date   date NOT NULL,
  buyer_id       uuid NOT NULL REFERENCES buyer,
  broker_id      uuid NULL REFERENCES broker,
  broker_pct     decimal(5,2) NOT NULL DEFAULT 0 CHECK (broker_pct BETWEEN 0 AND 100),
  terms_days     int NOT NULL DEFAULT 0 CHECK (terms_days >= 0),
  doc_type       varchar(20) NOT NULL DEFAULT 'BILL',
  status         varchar(10) NOT NULL CHECK (status IN ('DRAFT','POSTED','CANCELLED')),
  version        int NOT NULL DEFAULT 1,         -- post idempotency key
  created_by     uuid NOT NULL REFERENCES app_user,
  created_at     timestamptz NOT NULL,
  posted_by      uuid NULL REFERENCES app_user,
  posted_at      timestamptz NULL,
  cancelled_by   uuid NULL REFERENCES app_user,
  cancelled_at   timestamptz NULL,
  cancel_reason  varchar(500) NULL,
  CHECK (status <> 'POSTED'    OR (invoice_no IS NOT NULL AND posted_at IS NOT NULL)),
  CHECK (status <> 'CANCELLED' OR cancel_reason IS NOT NULL)
);

CREATE TABLE sales_line (
  line_id          uuid PRIMARY KEY,
  invoice_id       uuid NOT NULL REFERENCES sales_invoice ON DELETE CASCADE,
  line_no          int  NOT NULL,
  grade_id         uuid NOT NULL REFERENCES grade,
  size_id          uuid NOT NULL REFERENCES size_bucket,
  gross_weight_ct  decimal(18,4) NOT NULL CHECK (gross_weight_ct >  0),
  selection_ct     decimal(18,4) NOT NULL CHECK (selection_ct    >= 0),
  rejection_ct     decimal(18,4) GENERATED ALWAYS AS (gross_weight_ct - selection_ct) STORED,
  price_per_ct     decimal(18,2) NOT NULL CHECK (price_per_ct >= 0),
  ex_rate          decimal(12,6) NOT NULL DEFAULT 1 CHECK (ex_rate > 0),
  less1_pct        decimal(5,2)  NOT NULL DEFAULT 0 CHECK (less1_pct BETWEEN 0 AND 100),
  less2_pct        decimal(5,2)  NOT NULL DEFAULT 0 CHECK (less2_pct BETWEEN 0 AND 100),
  amount           decimal(18,2) NOT NULL,       -- CALC-1, rounded, stored. See §3.2
  remark           varchar(500) NULL,
  UNIQUE (invoice_id, line_no),
  CHECK  (selection_ct <= gross_weight_ct),      -- BR-VAL-1, in the database (NFR-INT-3)
  FOREIGN KEY (grade_id, size_id) REFERENCES grade_size  -- C-1: size must be valid for the grade
);

CREATE TABLE receipt (
  receipt_id    uuid PRIMARY KEY,
  invoice_id    uuid NOT NULL REFERENCES sales_invoice,
  receipt_date  date NOT NULL,
  amount        decimal(18,2) NOT NULL CHECK (amount > 0),
  method        varchar(20) NOT NULL,
  created_by    uuid NOT NULL REFERENCES app_user,
  created_at    timestamptz NOT NULL
);

CREATE TABLE change_request (                     -- SALES-002: sales staff cannot edit posted
  request_id     uuid PRIMARY KEY,
  invoice_id     uuid NOT NULL REFERENCES sales_invoice,
  requested_by   uuid NOT NULL REFERENCES app_user,
  requested_at   timestamptz NOT NULL,
  proposed       jsonb NOT NULL,                  -- the edit, not yet applied
  status         varchar(10) NOT NULL CHECK (status IN ('OPEN','APPROVED','REJECTED')),
  decided_by     uuid NULL REFERENCES app_user,
  decided_at     timestamptz NULL,
  decision_note  varchar(500) NULL
);
```

**Three decisions worth defending:**

1. **`invoice_no` is assigned at post, not at create.** Two offline clients cannot both mint
   "INV-0042". The client owns `invoice_id` (a uuid, collision-free); the server owns the
   human-readable number and assigns it in one transaction with the post. Drafts have no number,
   which is honest — a draft is not yet a document.
2. **`rejection_ct` is a generated column.** It is `gross − selection` by definition (CALC-2). A
   generated column makes it impossible to store a value that disagrees with its own inputs, and it
   is still queryable and indexable. The database enforces the rule; nobody has to remember it.
3. **`amount` is stored, not computed on read.** See §3.2 — it is the rounding boundary, and the
   invoice total is the sum of *stored* line amounts. Recomputing on read would eventually disagree
   with what was printed on a bill.

### 2.4 Inventory

```sql
CREATE TABLE stock_movement (
  movement_id           uuid PRIMARY KEY,
  movement_date         date NOT NULL,
  grade_id              uuid NOT NULL REFERENCES grade,
  size_id               uuid NOT NULL REFERENCES size_bucket,
  movement_type         varchar(12) NOT NULL CHECK (movement_type IN
                          ('INTAKE','CONVERT_IN','CONVERT_OUT','REJECTION','SALE','ADJUST')),
  weight_ct             decimal(18,4) NOT NULL CHECK (weight_ct <> 0),   -- SIGNED
  price_per_ct          decimal(18,2) NOT NULL DEFAULT 0,
  ref_type              varchar(12) NOT NULL CHECK (ref_type IN
                          ('INTAKE','INVOICE','CONVERSION','REJECTION','ADJUST')),
  ref_id                uuid NOT NULL,           -- invoice_id, conversion group, intake batch…
  counterparty_grade_id uuid NULL REFERENCES grade,
  counterparty_size_id  uuid NULL REFERENCES size_bucket,
  reason                varchar(500) NULL,       -- mandatory for ADJUST
  created_by            uuid NOT NULL REFERENCES app_user,
  created_at            timestamptz NOT NULL,

  -- C-5: the sign is the rule. Balance becomes SUM(weight_ct), nothing else.
  CHECK (
    (movement_type IN ('INTAKE','CONVERT_IN')                  AND weight_ct > 0) OR
    (movement_type IN ('CONVERT_OUT','REJECTION','SALE')       AND weight_ct < 0) OR
    (movement_type = 'ADJUST')                                                    ),
  CHECK (movement_type <> 'ADJUST' OR reason IS NOT NULL),
  FOREIGN KEY (grade_id, size_id) REFERENCES grade_size
);

-- V-3 / DQ-13: a rejection is not a scalar. The sales workbook records, in cell comments,
-- where each rejected parcel actually went: "13.46 Selection | 4.62 Reparing | 6.31 FL+Col+II".
-- That free text IS the sales->stock link the spec believed did not exist.
CREATE TABLE rejection_disposition (
  disposition_id  uuid PRIMARY KEY,
  movement_id     uuid NOT NULL REFERENCES stock_movement,   -- the REJECTION movement
  weight_ct       decimal(18,4) NOT NULL CHECK (weight_ct > 0),
  outcome         varchar(12) NOT NULL CHECK (outcome IN
                    ('RESELECT','REPAIR','REGRADE','CULET','OTHER')),
  to_grade_id     uuid NULL REFERENCES grade,   -- required when outcome = 'REGRADE'
  note            varchar(200) NULL,
  CHECK (outcome <> 'REGRADE' OR to_grade_id IS NOT NULL)
);

CREATE INDEX ix_movement_bucket ON stock_movement (grade_id, size_id, movement_date);
CREATE INDEX ix_movement_ref    ON stock_movement (ref_type, ref_id);
```

**Why dispositions are their own table.** A `REGRADE` disposition is a conversion in disguise — it
is how material actually moves between grades in this business, and today it happens by someone
reading a comment balloon and retyping numbers into another sheet. Modelling it explicitly is what
makes "avelu" auditable.

> `ponytail:` a `REGRADE` disposition does not auto-create the paired `CONVERT_OUT`/`CONVERT_IN`
> movements yet — it records intent, and the stock manager posts the conversion (INV-004). Wire the
> automation once the client confirms the re-keying rule, which is currently unknown (§7).

**Signed weights collapse CALC-7 into `SUM()`.** The spec wrote balance as a five-term formula with
three subtractions. Encode the sign in the row and enforce it with a CHECK, and the balance query
is `SELECT SUM(weight_ct) … GROUP BY grade_id, size_id`. There is then no place for the formula to
be typed wrong, on any platform, ever — which was the whole point of CALC-001.

**Conversions have no header table.** A conversion is two rows sharing one `ref_id` with
`ref_type = 'CONVERSION'`, one `CONVERT_OUT` and one `CONVERT_IN`, each naming the other's
grade/size in `counterparty_*`. Conservation is then an invariant, not a schema feature:
`SUM(weight_ct) = 0` for every conversion `ref_id`.

> `ponytail:` no conversion header table — the pair is identified by shared `ref_id`, and
> conservation is enforced in the write path plus invariant INV-1. Add a `conversion` header if
> conversions ever need their own attributes (a note, an operator, a loss allowance).

### 2.5 Cross-cutting

```sql
CREATE TABLE audit_entry (                        -- AUD-001, append-only, no updates ever
  audit_id     uuid PRIMARY KEY,
  entity_type  varchar(40) NOT NULL,
  entity_id    uuid NOT NULL,
  action       varchar(12) NOT NULL CHECK (action IN
                 ('CREATE','UPDATE','POST','CANCEL','DELETE','LOGIN_FAIL')),
  before       jsonb NULL,
  after        jsonb NULL,
  user_id      uuid NULL REFERENCES app_user,     -- NULL for failed logins
  occurred_at  timestamptz NOT NULL
);
CREATE INDEX ix_audit_entity ON audit_entry (entity_type, entity_id, occurred_at);
CREATE INDEX ix_audit_user   ON audit_entry (user_id, occurred_at);

CREATE TABLE app_setting (                        -- CFG-001/002
  key         varchar(60) PRIMARY KEY,
  value       varchar(500) NOT NULL,
  updated_by  uuid NULL REFERENCES app_user,
  updated_at  timestamptz NOT NULL
);
```

Seed settings: `base_currency=INR`, `money_dp=2`, `carat_dp=4`, `rounding=HALF_UP`,
`negative_stock_policy=WARN` (Q10), `session_timeout_min=60`, `lockout_attempts=5`,
`auto_reject_on_post=false` (INV-005), `manager_sees_margin=false` (§4),
**`settlement_write_off_threshold=1.00`** (V-4).

**V-4, the settlement rule.** When `|outstanding|` falls below the threshold, the invoice closes and
the residue posts as a rounding adjustment. This is not cosmetic: the sample file's only outstanding
balance in the entire workbook is **₹−0.275**, created by a buyer paying a round ₹139,865 against
₹139,864.725. Without this rule every hand-rounded payment leaves a permanent phantom receivable —
which is exactly the "numbers I can't trust" complaint that started this project.

`jsonb` on PostgreSQL; `nvarchar(max)` with `ISJSON` on SQL Server.

### 2.6 What is deliberately absent

| Not built | Would be needed if | Tracked as |
|---|---|---|
| `supplier`, `purchase`, `payable` | D3 says purchases are in scope | GAP-1 |
| `currency` table | Q7 says foreign-currency deals happen | C-4 |
| `permission` / `role_permission` tables | Roles become customer-configurable | C-6 |
| Tax / GST lines | D4 says the bill needs tax | GAP-2 |
| Stock reservation on DRAFT | Double-selling turns out to be a real problem | GAP-3 |
| `piece_count` on lines and movements | D6 says pieces are tracked | GAP-6 |
| Materialised balance table | Balance queries exceed NFR-PERF-3 (~10k rows/yr — they won't) | — |

---

## 3. Calculation engine

One .NET project, **no dependencies, no I/O, no database access** — pure functions over decimals.
Every rule below is one function with one test. This is the single implementation for desktop,
Android and reports (FR-CALC-13).

### 3.1 Signatures

| Rule | Function | Notes |
|---|---|---|
| CALC-1 | `LineAmount(selection, pricePerCt, exRate, less1Pct, less2Pct, brokerPct) → decimal` | Discounts compound, in this order |
| CALC-2 | `Rejection(grossWeight, selection) → decimal` | Throws if `selection > gross` |
| CALC-3 | `Outstanding(lineAmounts, receiptAmounts) → decimal` | Never stored |
| CALC-4 | `InvoiceTotal(lineAmounts) → decimal` | Σ of **stored, rounded** line amounts |
| CALC-5 | `BlendedRate(totalAmount, totalCarats) → decimal` | `0` if carats = 0 |
| CALC-6 | `WeightedAvgPrice((weight, price)[]) → decimal` | `0` if Σweight = 0 — no placeholder rows |
| CALC-7 | `Balance(movements) → decimal` | `Σ weight_ct`. Signed |
| CALC-8 | *(removed as a runtime rule — see §3.8)* | Migration-time only |
| CALC-9 | `RollUp(...)` | Carats add; prices go through CALC-6, never a mean |
| CALC-10 | `DueDate(invoiceDate, termsDays)` · `IsOverdue(due, outstanding, today)` | |
| CALC-11 | `BrokerPayable(lines, brokerPct) → decimal` | Pre-broker amount × broker % |

### 3.2 Rounding — where, exactly

The rule that prevents every penny-mismatch bug:

```
  intermediate arithmetic   →  full decimal precision, no rounding
  line amount               →  round to 2 dp  ← THE ONLY ROUNDING BOUNDARY
  invoice total             →  Σ of already-rounded line amounts
  outstanding               →  Σ rounded amounts − Σ receipts (both already 2 dp)
  averages & blended rates  →  computed unrounded, rounded for DISPLAY only
```

The invoice total is the sum of the stored line amounts — **not** the unrounded computation
re-summed. Those two differ by up to half a paisa per line, and the first one is what was printed on
the bill. Round-half-up (BR-ROUND-4).

### 3.3 CALC-1, precisely

```
amount = ROUND( selection × pricePerCt × exRate
                × (1 − less1Pct/100)
                × (1 − less2Pct/100)
                × (1 − brokerPct/100), 2 )
```

Order matters and matches `Sale · Q` exactly. `brokerPct` is a **parameter**, not read from the
line — it lives on the invoice header (C-7) but is applied per line, which is how the workbook does
it and how CALC-11 stays consistent with CALC-1.

Test: `selection=112.89, price=1000, ex=1, less1=2, less2=1, broker=1` →
`112.89 × 1000 = 112890 → ×0.98 = 110632.20 → ×0.99 = 109525.878 → ×0.99 = 108430.61922` →
**`108430.62`**.

### 3.4 CALC-2

`rejection = gross − selection`, and `selection > gross` is an **error, not a clamp**. The spec says
"≥0 enforced"; clamping would silently discard a data-entry mistake. The database CHECK and the
engine agree: it throws.

Test: `137.29 − 112.89 = 24.40` (SALES-001 acceptance criterion).

### 3.5 CALC-6 — the div-by-zero fix

`Σw = 0 → return 0`. No exception, no placeholder row. This one function retires DQ-2 and the
`1e-08` / `1e-12` hack across all 22 sheets.

### 3.6 CALC-7 — balance

`SUM(weight_ct)` over the bucket. With the sign CHECK in §2.4 the arithmetic is structural: the
engine function exists only so callers have one name for it.

### 3.7 CALC-11 — broker payable

```
brokerPayable = ROUND( Σ( selection × price × exRate × (1−less1/100) × (1−less2/100) ) × brokerPct/100, 2 )
```

Note the **pre-broker** subtotal — the amount before the broker deduction of CALC-1. Under the Q4
assumption (deduction *and* payable), the two are consistent by construction: what is deducted from
the buyer is what is owed to the broker. **If Q4 comes back "deduction only", CALC-11 and widget
W14 are deleted, not adjusted.**

### 3.8 CALC-8 — why it does not survive

In the workbook, DIFF compared two independently-maintained numbers: a *reported* balance typed into
a grade sheet, and a *derived* one (stock − sale). Drift between them was possible, so an audit
column made sense — and by F-5 it never worked anyway.

**In this model there is no second number.** Balance is `SUM(weight_ct)` and is stored nowhere.
There is nothing to reconcile it against, because the thing DIFF was auditing cannot occur by
construction. Keeping a runtime check that compares a number to itself would be theatre.

What replaces it:

| Purpose | Replacement |
|---|---|
| Prove migrated data matches the workbook | **One-off reconciliation report** at cut-over: workbook-reported balance vs our derived balance, per grade × size. Non-zero rows are investigated before go-live, and any residual becomes an explicit `ADJUST` movement with a reason — visible forever, not silently absorbed |
| Ongoing integrity | Invariants INV-1…INV-5 below, run as tests and as a nightly check |

This is a **change to the spec**, not an omission. It needs client sign-off alongside G2.

### 3.9 Invariants (the real audit)

| # | Invariant | Enforced by |
|---|---|---|
| INV-1 | Every conversion `ref_id` sums to zero — weight is conserved | Write path + test + nightly |
| INV-2 | Movement sign matches movement type | DB CHECK |
| INV-3 | A POSTED invoice has ≥1 line and exactly one `SALE` movement per line | Write path + test |
| INV-4 | `selection ≤ gross` on every line | DB CHECK + engine |
| INV-5 | Every `SALE` movement traces to a line of a POSTED (not CANCELLED) invoice | Nightly |
| INV-6 | A CANCELLED invoice's movements sum to zero — the reversal is complete | Write path + test |

INV-6 is the one that actually replaces DIFF's *intent*: it proves stock returned when a sale was
undone.

---

## 4. Roles & permissions

Three roles, fixed. `role` is an enum column; the capability map is a static table in code
(C-6). No permissions schema until someone asks to customise a role.

| Capability | Sales | Manager | Owner |
|---|---|---|---|
| Create / edit **own** draft invoice | ✔ | ✔ all | ✔ |
| Post invoice → deduct stock | ✔ own | ✔ all | ✔ |
| Edit posted invoice | ✖ → `change_request` | ✔ audited | ✔ audited |
| Approve a change request | ✖ | ✔ | ✔ |
| Cancel invoice | ✖ | ✔ audited | ✔ |
| Record receipt | ✔ | ✔ | ✔ |
| Master data (grade, buyer, broker, price) | ✖ | ✔ | ✔ |
| Rough intake / conversion / rejection / adjust | ✖ | ✔ | ✔ |
| Owner dashboard | ✖ | ✔ | ✔ |
| Margin / cost / profitability | ✖ | setting `manager_sees_margin` | ✔ |
| Users & roles | ✖ | ✖ | ✔ |
| Export | own only | ✔ | ✔ |
| Audit log | ✖ | ✔ | ✔ |

**Enforcement is server-side on every endpoint** (NFR-SEC-4). The client hides what a role cannot
do; hiding is courtesy, not security.

The single "configurable" cell — manager margin visibility — is **one setting**, not a permission
system. That distinction is the difference between a week of work and an afternoon.

---

## 5. Diamond attribute coverage

| Concept | In the workbook | Model home |
|---|---|---|
| Carat weight | Yes | `sales_line.gross_weight_ct` / `selection_ct` · `stock_movement.weight_ct` |
| Colour / clarity | Encoded inside grade codes (TOP-COL, OW, GH, LC, VVS) | `grade` |
| Rate per carat | Yes | `sales_line.price_per_ct` · `price_list` |
| Discounts | Less 1, Less 2, broker % | `sales_line.less1_pct/less2_pct` · `sales_invoice.broker_pct` |
| Buyer / broker / terms | Yes | `buyer` · `broker` · `sales_invoice.terms_days` |
| Sieve size | Yes | `size_bucket`, **restricted per grade** by `grade_size` |
| Lot / parcel | Implicit | `stock_movement.ref_id` groups an intake batch |
| Shape / cut / certificate | **No** | Not modelled. Q1/Q2 — add nullable columns if the answer changes |
| Pieces | **No** | Not modelled. D6 |

Grade is the primary axis because that is how this business actually trades — one code bundling
colour, clarity and make. The GIA 4-C axes are not the domain here, and modelling them "just in
case" would add columns nobody fills in.

---

## 6. Worked example — one sale, end to end

Sales person sells 112.89 ct of NO 1 / +6.5 at ₹1,000/ct, Less 1 = 2 %, Less 2 = 1 %, broker 1 %,
from a gross parcel of 137.29 ct. Buyer terms 45 days.

| Step | What happens | Evidence |
|---|---|---|
| 1 | Draft invoice created offline. `invoice_id` = uuid from the client. `invoice_no` still NULL | §2.3 |
| 2 | Line saved. `rejection_ct` = **24.40** by generated column. `amount` = **108,430.62** by CALC-1 | §3.3, §3.4 |
| 3 | **Stock unchanged** — drafts never move stock | BR-INV-5 |
| 4 | Post. Server assigns `invoice_no`, status → POSTED, writes one movement: `SALE`, `−112.89` ct, `ref_type=INVOICE`, `ref_id=invoice_id` | §2.4, INV-3 |
| 5 | NO 1 / +6.5 balance drops by 112.89 — `SUM(weight_ct)`, no formula | §3.6 |
| 6 | Optional `REJECTION` movement of `−24.40` if `auto_reject_on_post` is on | INV-005, §2.5 |
| 7 | Due date = invoice date + 45 | CALC-10 |
| 8 | Receipt of 50,000 recorded → outstanding = **58,430.62**, derived, never stored | CALC-3 |
| 9 | Every step wrote an `audit_entry` with before/after | AUD-001 |
| 10 | If cancelled: compensating movements, sum to zero, invoice retained | INV-6 |

Step 5 is the entire project. It is the line the two spreadsheets never had.

---

## 7. Open items carried into Phase 3

| ID | Item | Blocks |
|---|---|---|
| ~~G1~~ | ✅ Closed — **22 grades**, names known verbatim | — |
| G2 / C-2 | ✅ Confirmed broken in the file. Still needs client **sign-off** on deleting it | Reconciliation design |
| ~~G3 / L3~~ | ✅ Closed — 4 sizes for `NO 1` / `NO 1 BB`, 3 for the other 20 | — |
| ~~L1 / L2~~ | ✅ Closed — row anchors mapped per sheet; stride is non-uniform by design | Migration parser must find sections by their `SUM` anchor, not by row number |
| 🔴 **NEW** | **Who re-keys the rejection comments into the grade sheets, and by what rule?** | `rejection_disposition` semantics |
| A3 | A **populated** master file — the one supplied is the blank template | Migration, opening balances |
| Q4 | Broker treatment | Whether CALC-11 exists at all |
| Q6 | Sieve mm definitions | `size_bucket.lower_mm` / `upper_mm` are nullable until answered |
| Q9 | `doc_type` values beyond BILL | The CHECK constraint is currently open |
| Q12 | Sub-grades (GH-VVS, NO 1 MB) | Whether they are `grade` rows or `price_list` rows |
| D3 | Purchases in scope | Whether `supplier` / `payable` join the model |
| F-4 | Historical invoices with per-line broker % | Migration rule |

---

## 8. What Phase 2 deliberately did not decide

Solution structure, API contract, sync protocol, migration implementation and UI are the spec's
**Phase 5**. This document defines *what the data is and what the rules are* — nothing about how it
is served or shipped.

The one structural implication worth stating now: the calculation engine is a **standalone project
with no dependencies**, referenced by the API. Every rule in §3 gets a unit test before anything
calls it.
