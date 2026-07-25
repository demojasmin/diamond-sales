# 09 · Manual Test Pack

Test data is taken from the real workbooks so the expected figures can be checked against the
spreadsheet itself. Every number below has a source: `Sale!Q3`, `Sale!J4`, `KAPNA ADD`.

Automated equivalent: `dotnet run --project DiamondCalc.Tests` (95 checks, ~2 s).

---

## 0 · Setup

```
dotnet run --project DiamondApi --urls http://localhost:5000     # terminal 1, leave running
dotnet run --project DiamondDesktop                              # terminal 2
```

Sign in `owner` / `owner`.

**For the exact expected numbers below, start from a clean database:** stop the API, delete
`DiamondApi/diamond.db`, start it again. It re-seeds 22 grades, 4 sizes, 3 buyers, 3 brokers and
the settings. If you don't reset, balances will include whatever you entered earlier — the *changes*
still match, the absolute figures won't.

---

## 1 · Master data tab — MDM-001/002/004

| TC | Do | Expected |
|---|---|---|
| **1.1** | Open the tab | 22 grades listed: NO 1, NO 1 BB, NO II, EX 1, NO 2, NO DX, NO 3…7, TOP-COL, COL, OW, LC-1…3, GH, LB-1, LB-2, +14, EXTRA |
| **1.2** | Look at the **Sizes** column for `NO_1` | `-2  -6.5  +6.5  +11` (four) |
| **1.3** | Look at `NO_II` | `-6.5  +6.5  +11` (three — the workbook has no `-2` column on that sheet) |
| **1.4** | Look at the **Aliases** column for `NO_1_BB` | Contains `1 BB`, `1BB`, `NO 1 BB` — the three spellings that made sales unjoinable to stock (DQ-4) |
| **1.5** | Buyers grid | ABC Company (terms 45), Z K ENTERPRISE (0), QUEST DIAMOND (0) |
| **1.6** | Add buyer: name `TEST BUYER`, terms `30` → Add buyer | Appears in the grid, and in the Buyer dropdown on Sales entry |
| **1.7** | Add the same name again | Refused: `DUPLICATE: Buyer already exists` |
| **1.8** | Add broker: `TEST BROKER`, `1.5` → Add broker | Appears in both places |

---

## 2 · Intake & movements tab — INV-001

| TC | Data | Expected |
|---|---|---|
| **2.1** | Grade `NO 1`, Size `+6.5`, Weight `500`, Price `900` → Add intake | "Intake recorded · 500.0000 ct" |
| **2.2** | Grade `NO II`, Size `+11`, Weight `250`, Price `1200` → Add intake | Recorded |
| **2.3** | Grade `NO II`, Size — open the list | Only three sizes offered; `-2` is not selectable for NO II |
| **2.4** | Weight `0` → Add intake | Refused: "Weight must be positive" |

---

## 3 · Stock tab — INV-002/003

| TC | Do | Expected |
|---|---|---|
| **3.1** | Refresh | `NO_1 +6.5` → 500.0000 ct, avg 900.00, value **450,000.00**<br>`NO_II +11` → 250.0000 ct, avg 1200.00, value **300,000.00** |
| **3.2** | Read the summary line | `750.0000 ct · value 750,000.00` |
| **3.3** | Select the NO_1 row → Show movements | One INTAKE row, +500.0000 |
| **3.4** | Run invariants | "All invariants hold (INV-1…INV-6)" |

---

## 4 · Sales entry tab — SALES-001, CALC-1/2/4/5/10

The line is the one from the real sales sheet (`Sale` row 4).

| TC | Data | Expected |
|---|---|---|
| **4.1** | Buyer `ABC Company` | Terms auto-fills **45** |
| **4.2** | Broker `RAJU PATEL` | Broker % auto-fills **1** |
| **4.3** | Read the Due field | Invoice date + 45 days |
| **4.4** | Row 1: Size `+6.5`, Grade `NO 1`, Weight `137.29`, Selection `112.89` | Rejection = **24.40** the moment you leave the cell |
| **4.5** | Price/ct `1000`, Less 1 `2`, Less 2 `1` | Amount = **108,430.62**<br>(112.89 × 1000 → ×0.98 → ×0.99 → ×0.99 broker) |
| **4.6** | Footer | Carats 112.89 · Amount 108,430.62 · Blended rate/ct 960.50 |
| **4.7** | Set Selection to `200` | Row turns pink; tooltip "selection 200 exceeds gross 137.29"; Amount blanks; Save is refused |
| **4.8** | Back to `112.89`, press **Enter** | A second line appears; header values kept |
| **4.9** | Line 2: Size `+11`, Grade `NO II`, Weight `15.39`, Selection `0`, Price `53001` | Accepted. Amount **0.00**, Rejection **15.39** — a fully rejected parcel is legitimate (`Sale` row 5) |
| **4.10** | Set Terms to `0` | Due date = invoice date (`Sale` invoice 3 has terms 0) |
| **4.11** | Set Terms back to `45`, **Save draft** | "Draft saved · 108,430.62" |
| **4.12** | Change Broker % to `0`, watch line 1 | Amount rises to **109,525.88** — the header % applies to every line |
| **4.13** | Set Broker % back to `1` | Amount returns to 108,430.62 |

---

## 5 · Posting — SALES-003, the point of the project

| TC | Do | Expected |
|---|---|---|
| **5.1** | **Post** | Dialog: posted, carats out 112.89. Footer shows the invoice number |
| **5.2** | **Stock** → Refresh | `NO_1 +6.5` is now **387.11 ct** (500 − 112.89) |
| **5.3** | Select that row → Show movements | INTAKE +500.0000 **and** SALE −112.8900, the sale linked to the invoice |
| **5.4** | **Invoices** → Refresh | Status POSTED, Amount 108,430.62, Outstanding 108,430.62 |

### 5b · Negative stock — Q10 policy

| TC | Do | Expected |
|---|---|---|
| **5.5** | New invoice: buyer `ABC Company`, line NO 1 / +6.5, Weight `9999`, Selection `9999`, Price `1000` → Post | Warning dialog: "balance goes negative (387.1100 → −9611.8900)" with Yes/No |
| **5.6** | Choose **No** | Nothing posts; stock unchanged |
| **5.7** | Post again, choose **Yes** | Posts. Stock shows a negative balance — visible, not hidden |
| **5.8** | **Settings** → set `negative_stock_policy` to `BLOCK` → repeat 5.5 | Refused outright, no override offered |
| **5.9** | Set it back to `WARN` | Warning behaviour returns |

---

## 6 · Invoices & receipts — PAY-001/003, SALES-004

| TC | Do | Expected |
|---|---|---|
| **6.1** | Select the 108,430.62 invoice, Receipt `50000`, method RTGS → Record | Outstanding **58,430.62** |
| **6.2** | Receipt `58431` (a round figure, as a buyer would pay) | "Residue of −0.38 written off as a rounding adjustment"; Outstanding **0.00** |
| **6.3** | Receipt `1000` on the same invoice | Warning: receipt exceeds outstanding — treat as advance/credit |
| **6.4** | Select the 9999 ct invoice → **Cancel invoice**, reason `entered twice` | Status CANCELLED |
| **6.5** | **Stock** → Refresh | Balance back to **387.11 ct** — cancelling returned the stock |
| **6.6** | **Stock** → Run invariants | Still all hold (INV-6 proves the reversal netted to zero) |
| **6.7** | Cancel with a blank reason | Refused |

---

## 7 · Receivables — PAY-002

| TC | Do | Expected |
|---|---|---|
| **7.1** | Refresh | Only invoices with a non-zero balance; the settled one is gone |
| **7.2** | Read the summary | Total plus per-bucket figures: current / 0-30 / 31-60 / 61-90 / 90+ |
| **7.3** | Check Days overdue | 0 while inside terms; counts up past the due date |

---

## 8 · Conversions & rejections — INV-004/005/006

| TC | Data | Expected |
|---|---|---|
| **8.1** | Convert From `NO 1` `+6.5` → To `NO II` `+11`, Weight `10`, Price `900` | "Converted 10.0000 ct · total carats unchanged" |
| **8.2** | **Stock** → Refresh | NO 1 +6.5 down 10, NO II +11 up 10; company total unchanged |
| **8.3** | Run invariants | Hold (INV-1: conversions conserve weight) |
| **8.4** | Rejection NO 1 `+6.5` Weight `24.40`, dispositions `13.46 RESELECT` + `4.62 REPAIR` only → Record | Refused: `DISPOSITIONS_DO_NOT_SUM` |
| **8.5** | Add a third row `6.32 REGRADE`, leave To grade blank | Refused: `REGRADE_REQUIRES_GRADE` |
| **8.6** | Set To grade `NO II` → Record | Accepted — this is the comment-balloon breakdown from `Sale!J4`, finally structured |
| **8.7** | Adjustment NO 1 `+6.5` Weight `-5`, no reason | Refused: an adjustment needs a reason |
| **8.8** | Same with reason `physical count` | Recorded, visible forever in movements |

---

## 9 · Dashboard — DASH-001

| TC | Do | Expected |
|---|---|---|
| **9.1** | Refresh | Total sales, carats sold, blended rate/ct, outstanding, inventory value & carats, broker cost, posted invoice count |
| **9.2** | Cross-check blended rate | = total sales ÷ carats sold (CALC-5) |
| **9.3** | Cross-check inventory value | Matches the Stock tab's total |
| **9.4** | Top movers | Grades ranked by carats sold |

---

## 10 · Audit — AUD-001

| TC | Do | Expected |
|---|---|---|
| **10.1** | Refresh | Every action from this session: CREATE, POST, CANCEL, LOGIN_FAIL, settings UPDATE |
| **10.2** | Find the POST row | `after` holds the invoice number and total |
| **10.3** | Find the settings change | `before` and `after` both present |

---

## 11 · Users & roles — AUTH-001/002, §2.4 matrix

| TC | Do | Expected |
|---|---|---|
| **11.1** | Users tab → add `asha` / `Asha` / `asha123` / **SALES** | Created |
| **11.2** | Close the app, sign in as `asha` | Signs in; footer says SALES |
| **11.3** | Look at the tabs | No **Users** tab |
| **11.4** | **Stock** → Refresh | "Manager or Owner only" |
| **11.5** | **Dashboard** → Refresh | Same refusal |
| **11.6** | **Invoices** → Refresh | Only her own invoices — none yet |
| **11.7** | Enter and post her own invoice | Works |
| **11.8** | Sign back in as `owner`, Users tab, select asha → Deactivate | Active unticked; her sessions are killed |
| **11.9** | Try signing in as asha | Refused |
| **11.10** | Sign in as `owner` with a wrong password five times | 5th attempt: account locked for 15 minutes (`lockout_attempts`) |

---

## 12 · Settings — CFG-001/002

| TC | Do | Expected |
|---|---|---|
| **12.1** | Refresh | 10 settings including `negative_stock_policy`, `settlement_write_off_threshold`, `session_timeout_min`, `auto_reject_on_post` |
| **12.2** | Set `settlement_write_off_threshold` to `0.01`, repeat 6.2 | The −0.38 residue is **not** written off — it stays outstanding |
| **12.3** | Set `auto_reject_on_post` to `true`, post an invoice with rejection carats | A REJECTION movement appears alongside the SALE in Stock → movements |
| **12.4** | Sign in as a SALES user and try to change a setting | Refused — Owner only |

---

## What this pack cannot test yet

| Epic | Why |
|---|---|
| SYNC | The offline outbox is not built (blocked on D5) |
| RPT | Excel/PDF export not built |
| NOTIF | Android push, also blocked on D5 |
| MIG | Needs A3, a populated master workbook |
