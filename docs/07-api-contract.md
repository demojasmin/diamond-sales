# 07 · Phase 2 — API Contract

Status: **draft**
Covers [02 §14](02-requirement-analysis.md) deliverable 4: endpoints, DTOs, error model,
idempotency and the sync protocol. Entities and rules are in
[03-domain-model.md](03-domain-model.md); component layout in
[06-solution-architecture.md](06-solution-architecture.md).

---

## 1. Conventions

| | |
|---|---|
| Base | `/api/v1` |
| Format | JSON, `camelCase`. Dates `YYYY-MM-DD`, timestamps RFC 3339 UTC |
| Ids | `uuid` v7, **client-generated** for every created entity (offline safety) |
| Money & carats | JSON **strings**, not numbers — `"139864.73"`. A JSON number is an IEEE double, and this project exists because someone's arithmetic drifted |
| Auth | `Authorization: Bearer <jwt>` on everything except `/auth/login` |
| Idempotency | `Idempotency-Key: <uuid>` required on every POST/PUT/PATCH |
| Role check | Server-side on every endpoint, per the [03 §4](03-domain-model.md) matrix |

Derived values (`amountTotal`, `outstanding`, `balanceCt`) are **returned but never accepted**. A
client that sends one gets `422 COMPUTED_FIELD_SUPPLIED` — silently ignoring it is how a client
starts believing it owns the number.

---

## 2. Error model

```json
{
  "error": {
    "code": "SELECTION_EXCEEDS_GROSS",
    "message": "Selection 112.90 ct exceeds gross weight 112.89 ct.",
    "field": "lines[2].selectionCt",
    "details": { "grossWeightCt": "112.89", "selectionCt": "112.90" }
  }
}
```

| HTTP | When |
|---|---|
| 400 | Malformed request |
| 401 / 403 | Not authenticated / role not permitted |
| 404 | Unknown id |
| 409 | State conflict — already posted, version mismatch, duplicate `invoice_no` |
| 422 | Business-rule violation (the table below) |
| 423 | Account locked |

**Named codes, because clients branch on them:**

`SELECTION_EXCEEDS_GROSS` · `SIZE_NOT_VALID_FOR_GRADE` · `NEGATIVE_STOCK` (warning or block per
`negative_stock_policy`) · `INVOICE_ALREADY_POSTED` · `INVOICE_CANCELLED` ·
`DISPOSITIONS_DO_NOT_SUM` · `REGRADE_REQUIRES_GRADE` · `CONVERSION_NOT_BALANCED` ·
`OVERPAYMENT` (warning) · `COMPUTED_FIELD_SUPPLIED` · `PRICE_MISSING` · `VERSION_CONFLICT` ·
`ALIAS_UNMAPPED`.

A **warning** returns `200` with a `warnings[]` array plus an `overrideToken`; re-posting with
`overrideToken` proceeds and records the override in the audit log. Warnings that cannot be
overridden are errors, and errors that can be waved through are warnings — there is no third thing.

---

## 3. Idempotency

Every mutating call carries `Idempotency-Key`. The server stores `(key, request_hash, response)`
and:

- **same key, same body** → returns the stored response, does nothing twice;
- **same key, different body** → `409 IDEMPOTENCY_KEY_REUSED`;
- **new key** → executes.

Keys are retained 30 days. This is what makes an outbox drain over a flaky connection safe: a
timeout is not evidence that the write failed, so a client that retries must not double-post an
invoice or double-deduct stock.

---

## 4. Endpoints

### 4.1 Auth

| Method | Path | Role | Notes |
|---|---|---|---|
| POST | `/auth/login` | — | → `{ token, refreshToken, user }`. Failure writes `LOGIN_FAIL`; 5 failures → `423` |
| POST | `/auth/refresh` | any | |
| POST | `/auth/logout` | any | Invalidates the refresh token |
| GET | `/users` · POST `/users` · PATCH `/users/{id}` | Owner | AUTH-002. Deactivation invalidates live sessions |

### 4.2 Master data (MDM-001/002/004, Manager+)

| Method | Path | Notes |
|---|---|---|
| GET | `/grades?active=true` | With aliases and permitted sizes |
| POST · PATCH | `/grades` · `/grades/{id}` | `code` is immutable after creation |
| GET · POST · DELETE | `/grades/{id}/aliases` | |
| GET | `/sizes` | Canonical four, with aliases |
| GET · POST · DELETE | `/grades/{id}/sizes` | The `grade_size` pairs. **`NO 1` and `NO 1 BB` have four; the other 20 have three** ([04 §3.4](04-workbook-verification.md)) |
| GET · POST · PATCH | `/buyers` · `/brokers` | Duplicate name → `409` |
| GET · POST | `/prices` | Effective-dated. `POST` closes the open row and opens a new one — prices are never edited in place |
| GET | `/prices/lookup?gradeId&sizeId&context&asOf` | `PRICE_MISSING` when absent — never a silent 0 |

### 4.3 Sales

| Method | Path | Role | Notes |
|---|---|---|---|
| GET | `/invoices?status&buyerId&from&to&page` | own / all by role | |
| GET | `/invoices/{id}` | | Includes lines, receipts, `amountTotal`, `outstanding`, `dueDate` |
| POST | `/invoices` | Sales+ | Client supplies `invoiceId` and line ids. Created `DRAFT`, no `invoiceNo` |
| PUT | `/invoices/{id}` | owner of draft / Manager | Full replace with `version`; mismatch → `409 VERSION_CONFLICT` |
| POST | `/invoices/{id}/post` | per matrix | **The one that matters** — §4.4 |
| POST | `/invoices/{id}/cancel` | Manager+ | `{ reason }` required. Writes compensating movements summing to zero (INV-6) |
| POST | `/invoices/{id}/change-requests` | Sales | SALES-002 — a sales person cannot edit a posted invoice, only propose |
| POST | `/change-requests/{id}/decide` | Manager+ | `{ decision, note }`. Approval applies the edit and re-runs the post effects |

**Invoice DTO** (response; request omits every computed field)

```json
{
  "invoiceId": "0192f3…", "invoiceNo": "INV-0042", "version": 3,
  "invoiceDate": "2025-10-17", "buyerId": "…", "brokerId": "…",
  "brokerPct": "1.00", "termsDays": 45, "docType": "BILL", "status": "POSTED",
  "lines": [{
    "lineId": "…", "lineNo": 1, "gradeId": "…", "sizeId": "…",
    "grossWeightCt": "137.2900", "selectionCt": "112.8900",
    "rejectionCt": "24.4000",          // computed
    "pricePerCt": "1000.00", "exRate": "1.000000",
    "less1Pct": "2.00", "less2Pct": "1.00",
    "amount": "108430.62",             // computed, stored
    "remark": "7.80 culet repair"
  }],
  "amountTotal": "108430.62",          // computed
  "receipts": [{ "receiptId": "…", "receiptDate": "2025-11-02", "amount": "50000.00", "method": "RTGS" }],
  "outstanding": "58430.62",           // computed
  "dueDate": "2025-12-01", "isOverdue": false
}
```

`brokerPct` sits on the header and is applied per line — matching the workbook, and keeping CALC-1
and CALC-11 consistent by construction ([03 §3.3](03-domain-model.md), C-7).

### 4.4 `POST /invoices/{id}/post`

The single most important call in the system. One transaction:

1. Validate: ≥1 line, every `selection ≤ gross`, every `(grade, size)` in `grade_size`.
2. Recompute every line amount with CALC-1 — the **server's** figure is authoritative.
3. Check the resulting balance per grade × size against `negative_stock_policy`
   (`BLOCK` → `422 NEGATIVE_STOCK`; `WARN` → `200` + warning + `overrideToken`; `ALLOW` → proceed).
   **A balance that was already negative before this invoice is reported, not hidden**
   ([04 §3.8 B-2](04-workbook-verification.md) — the workbook ships negative).
4. Assign `invoiceNo`, set `status=POSTED`, `postedAt`, `postedBy`.
5. Insert one `SALE` movement per line, signed negative, `refType=INVOICE`, `refId=invoiceId` (INV-3).
6. If `auto_reject_on_post`, insert a `REJECTION` movement per line with rejection > 0.
7. Write audit entries.

Idempotent on `(invoiceId, version)`: a replay returns the original result and moves no stock twice.

### 4.5 Payments

| Method | Path | Notes |
|---|---|---|
| POST | `/invoices/{id}/receipts` | `OVERPAYMENT` warning if it exceeds outstanding. If the residue falls below `settlement_write_off_threshold`, the response carries `"settled": true` and the write-off adjustment (PAY-003) |
| GET | `/receivables?buyerId&bucket` | PAY-002 ageing: `0-30 / 31-60 / 61-90 / 90+` |

### 4.6 Inventory

| Method | Path | Role | Notes |
|---|---|---|---|
| GET | `/stock?gradeId&sizeId&asOf` | Manager+ | Balance ct, weighted-avg price, value per grade × size, plus company total |
| GET | `/stock/{gradeId}/{sizeId}/movements` | Manager+ | The drill-down that proves the balance |
| POST | `/intake` | Manager+ | INV-001. Batch of rows → `INTAKE` movements sharing one `refId` |
| POST | `/conversions` | Manager+ | INV-004. `{ fromGradeId, fromSizeId, toGradeId, toSizeId, weightCt, pricePerCt }` → the paired `CONVERT_OUT`/`CONVERT_IN`. Server-side check `SUM(weight_ct) = 0` (INV-1) or `422 CONVERSION_NOT_BALANCED` |
| POST | `/rejections` | Manager+ | INV-005/006. Optional `dispositions[]`, which must sum to the rejection weight or `422 DISPOSITIONS_DO_NOT_SUM`; `REGRADE` requires `toGradeId` |
| POST | `/adjustments` | Manager+ | `ADJUST` movement, `reason` mandatory. The only way a balance changes without a business event, and it is always visible |

### 4.7 Dashboard & reports

| Method | Path | Notes |
|---|---|---|
| GET | `/dashboard/summary?from&to&…` | W1–W3, W9, W11, W13 in one call — a phone on 3G should not make eight |
| GET | `/dashboard/sales-by?dimension=period\|salesperson\|buyer\|grade&from&to` | W4–W6, W8 |
| GET | `/dashboard/margin?from&to` | W7. `403` unless Owner, or Manager with `manager_sees_margin` |
| GET | `/reports/sales.xlsx` · `/reports/stock.pdf` | RPT-001 |
| GET | `/audit?entityType&entityId&userId&from&to` | Manager+ |

### 4.8 Settings

`GET /settings` · `PATCH /settings` (Owner). Keys per [03 §2.5](03-domain-model.md).

---

## 5. Sync protocol

```
  client                                   server
    │  POST /sync/push  { ops: [...] }        │   each op carries op_id + Idempotency-Key
    │ ───────────────────────────────────────▶│   applied in order, per-op result returned
    │ ◀───────────────────────────────────────│   { results: [{opId, status, error?}] }
    │                                          │
    │  GET /sync/changes?since=<cursor>        │
    │ ───────────────────────────────────────▶│
    │ ◀───────────────────────────────────────│   { changes: {...}, cursor: "<next>" }
```

| Rule | Detail |
|---|---|
| Cursor | Opaque server token (a change sequence number). Clients store it and never parse it |
| Ordering | Ops apply in the order the client queued them. One failing op does not abort the batch — it comes back with an error and the client keeps it in the outbox |
| Partial failure | `207`-style per-op results. A whole-batch rollback would strand every good op behind one bad one |
| Conflict | `VERSION_CONFLICT` on an invoice → the later version wins, the superseded one is retained in audit. Anything the server cannot decide goes to the conflict inbox (`GET /conflicts`, Manager+) |
| Movements | Never conflict — append-only inserts, and the balance is a `SUM` |

> `ponytail:` no CRDTs, no vector clocks, no bidirectional merge engine. Movements are additive and
> invoices have a single owner, which covers every conflict this business can actually produce.
> Revisit only if two people genuinely need to edit one invoice simultaneously.

---

## 6. What this contract does not cover

| Not specified | Why | Trigger |
|---|---|---|
| Push notification transport (NOTIF-001) | Phase 3, and it depends on D5 | Deployment decided |
| Supplier / purchase endpoints | Not in the model | D3 = in scope |
| Tax fields on the invoice DTO | Not in the model | D4 = tax required |
| Stock reservation on draft | GAP-3 | Client says double-selling matters |
| Rate limiting, API keys | 10 known users behind auth | External integration |
