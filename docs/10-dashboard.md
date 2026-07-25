# 10 · Phase 4 — Owner Dashboard

Status: **built** on the desktop client and the API · Android deferred (blocked on **D5**)
Implements the spec's Phase 4: fifteen widgets, global filters, drill-downs.

---

## 1. What was built, and where

The spec puts this dashboard on Android. Android is block 8 of the build order and needs D5 (where
the server lives) answered first, so the widgets were built as **API endpoints plus a desktop
screen**. When the Android app is written it calls the same endpoints and computes nothing itself —
which is the rule that keeps every platform agreeing (FR-CALC-13).

```
  GET /api/v1/dashboard/*  ──┬──▶  Desktop · Dashboard tab   (built)
                             └──▶  Android · Compose         (not built — D5)
```

---

## 2. Widget coverage

| # | Widget | Endpoint | Rule | Where it shows |
|---|---|---|---|---|
| W1 | Total sales | `/dashboard/summary` | CALC-4 | KPI tile + "vs prior period" |
| W2 | Carats sold | `/dashboard/summary` | Σ selection | KPI tile |
| W3 | Blended rate/ct | `/dashboard/summary` | CALC-5 | KPI tile |
| W4 | Sales by period | `/dashboard/sales-by?dimension=period&bucket=day\|week\|month` | CALC-4 | Breakdown list |
| W5 | Sales by salesperson | `…dimension=salesperson` | CALC-4/5 | Breakdown list |
| W6 | Sales by buyer | `…dimension=buyer` | CALC-4 + % share | Breakdown list |
| W7 | Margin / profit | `/dashboard/margin` | revenue − weighted-avg cost | KPI tile + breakdown |
| W8 | Avg rate/ct by grade | `…dimension=grade` | CALC-6 | Breakdown list |
| W9 | Outstanding receivables | `/dashboard/summary` | CALC-3 | KPI tile |
| W10 | Receivables ageing | `/dashboard/ageing` | CALC-3 + CALC-10 | Breakdown list |
| W11 | Inventory value | `/dashboard/summary` · `/dashboard/inventory` | CALC-7 × CALC-6 | KPI tile + breakdown |
| W12 | Inventory aging | `/dashboard/inventory-aging` | FIFO by intake date | Breakdown list |
| W13 | Top movers | `/dashboard/top-movers` | Σ carats, ranked, vs prior | Breakdown list |
| W14 | Broker cost | `/dashboard/broker-cost` | CALC-11 | KPI tile + breakdown |
| W15 | Alerts strip | `/dashboard/alerts` | CALC-10 + thresholds | Red strip, hidden when clear |

Drill-down: `/dashboard/invoices` returns the invoices behind whatever is currently filtered. Same
filters, no second concept.

---

## 3. Global filters

Every endpoint accepts the same query string, and the UI has one filter bar for all of them.

| Parameter | Values |
|---|---|
| `range` | `TODAY` · `WEEK` · `MONTH` · `QUARTER` · `FY` · `ALL` · `CUSTOM` |
| `from` / `to` | dates, used when `range=CUSTOM` |
| `buyerId` · `brokerId` · `gradeId` · `sizeId` · `salespersonId` | uuid |

`FY` runs **April–March** (Q16's assumption). Default range on screen is **This month**, as the spec
asks.

Posted invoices only. A draft is not a sale, and a cancelled one is not either.

---

## 4. Three decisions worth recording

**W7's cost basis is weighted-average stock cost** — Q3's stated assumption, and the endpoint
returns `costBasis: "WEIGHTED_AVG_STOCK_COST (Q3)"` so the number is never read without its
definition. If the client answers "rough intake price" instead, one function changes.

**Cost basis counts acquisitions only.** Stock valuation and margin take their price from `INTAKE`
and `CONVERT_IN` movements — never `ADJUST`. A cancellation reversal re-adds carats at the *sale*
price, and a physical-count correction has no meaningful price at all; letting either re-price stock
inflated inventory value and turned a real margin negative. Found by the Phase 4 checks, not in
production.

**W14 reports broker cost even when no broker is named.** If broker % was charged, the money left
the deal whether or not anyone typed a name; those invoices group under `(no broker named)`. Hiding
them would under-report the total. This is also the shape of the F-4 problem in migration.

---

## 5. Permissions

| Widget | Who |
|---|---|
| All of W1…W15 | Manager and Owner |
| **W7 margin** | Owner always; Manager only if `manager_sees_margin` is on (docs/03 §4) |

A Sales user gets `403` from every dashboard endpoint, and the tab shows the refusal rather than an
empty screen.

---

## 6. Deliberately not built

| Thing | Why |
|---|---|
| Android client | Block 8; blocked on D5 |
| Push-driven alert delivery (NOTIF-001) | Phase 3 of delivery; needs D5 |
| Sparkline on W1 | The number and its vs-prior delta carry the same information; a chart library does not |
| Treemap for W11 | Ranked bars answer "which grade holds my money" just as well |

> `ponytail:` charts are bars drawn as rectangles with widths computed in C#. No chart library, no
> converters. Add one only if a widget genuinely needs a shape a bar cannot make.

---

## 7. Checks

`dotnet run --project DiamondCalc.Tests` — 124 checks, of which ~25 cover this phase: every widget's
arithmetic, the filters, the FY/month presets, the margin cost basis, and the FIFO aging bands
reconciling to the stock position.
