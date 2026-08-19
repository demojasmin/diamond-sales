# 11 · Implementation Status

Every story in [05-backlog.md](05-backlog.md), against what actually exists in the code.
Checks: `dotnet run --project DiamondCalc.Tests` — **129 passing**.

| Legend | |
|---|---|
| ✅ | Built and covered by checks |
| 🟡 | Partly built — what is missing is named |
| 🔴 | Not built — the blocker is named |

---

## MVP block

| Story | State | Where |
|---|---|---|
| CALC-001 · calculation engine | ✅ | `DiamondCalc/Calc.cs` — CALC-1…11, 29 checks |
| CALC-002 · rounding & precision | ✅ | one rounding boundary at the line; round-half-up |
| AUTH-001 · login with roles | ✅ | PBKDF2, sessions, 5-attempt lockout, audited failures |
| AUTH-002 · user administration | ✅ | Users tab (Owner only); deactivation kills live sessions |
| AUD-001 · immutable change log | ✅ | `audit_entry`, append-only, Audit tab |
| MDM-001 · grade dictionary | ✅ | 22 grades + aliases, seeded from the workbook |
| MDM-002 · buyer & broker masters | ✅ | defaults pre-fill the sales header |
| MDM-004 · size master with aliases | ✅ | 4 sizes, 4 notations, `grade_size` per grade |
| INV-001 · rough intake | ✅ | Intake tab → `INTAKE` movements |
| INV-002 · stock ledger | ✅ | signed movements; balance = `SUM`; invariants INV-1…6 |
| INV-003 · stock position | ✅ | Stock tab, drill to movements |
| SALES-001 · keyboard sales entry | ✅ | live CALC-1/2, Enter adds a line, validation blocks bad rows |
| SALES-002 · edit / correct | ✅ | edit as manager; **change requests** for sales staff |
| SALES-003 · post → deduct stock | ✅ | negative-stock policy with override token |
| PAY-001 · receipts | ✅ | partial payments, over-payment warning |
| PAY-003 · settlement write-off | ✅ | closes the ₹0.275-style residue visibly |
| OPS-001 · backup & restore | ✅ | `VACUUM INTO` snapshot, Settings tab, **restore verified by a check** |
| MIG-001 · master migration | 🔴 | **A3** — the supplied master workbook is the blank template |
| MIG-002 · opening stock & sales | 🔴 | same |
| MIG-003 · cut-over reconciliation | 🔴 | same. Design is complete in [08](08-migration-design.md) |

## Phase 2 block

| Story | State | Where |
|---|---|---|
| SALES-004 · cancel with reversal | ✅ | compensating movements; INV-6 proves they net to zero |
| INV-004 · grade-to-grade conversion | ✅ | paired movements, conservation enforced |
| INV-005 · rejection recording | ✅ | Intake & movements tab |
| INV-006 · rejection dispositions | ✅ | must sum to the rejection; REGRADE needs a destination |
| MDM-003 · price list | ✅ | effective-dated; setting a price closes the previous row |
| PAY-002 · receivables ageing | ✅ | Receivables tab, five buckets |
| DASH-001 · owner dashboard | ✅ | all 15 widgets — [10](10-dashboard.md) |
| RPT-001 · export | ✅ | CSV of any grid, matching the screen, date-stamped |
| SYNC-001 · offline entry | 🔴 | **D5** — no outbox until the deployment topology is decided |
| SYNC-002 · conflict resolution | 🟡 | client-generated ids, idempotency keys, invoice versions exist; no conflict inbox |

## Phase 3 block

| Story | State | Where |
|---|---|---|
| DASH-002 · salesperson & buyer analytics | ✅ | dashboard breakdowns W5/W6 |
| RPT-002 · invoice print | ✅ | FlowDocument + system print dialog (includes Print-to-PDF) |
| CFG-001 · company / currency / precision | ✅ | Settings tab |
| CFG-002 · security & policy settings | ✅ | timeout, lockout, negative stock, thresholds |
| NOTIF-001 · overdue & low-stock alerts | 🟡 | W15 alerts strip computes and shows both; **push delivery** needs D5 |

---

## What is genuinely left, and why

| # | Item | Blocker | Who unblocks it |
|---|---|---|---|
| 1 | Migration (MIG-001/002/003) | **A3** — a *populated* master workbook. The one supplied is the blank template; its balances are `1e-13` placeholders | Client |
| 2 | Offline outbox & sync (SYNC-001/002) | **D5** — where the server runs decides whether offline is even needed, and how sync reaches it | Client |
| 3 | Push notifications (NOTIF-001) | **D5** + Android | Client |
| 4 | Android app | Block 8; needs D5. Every dashboard endpoint it will call already exists | Client |

Nothing else in the backlog is waiting on us.

---

## Deviations worth knowing

| Decision | Instead of | Why |
|---|---|---|
| PBKDF2-SHA256 (210k iterations) | argon2id ([06 §2](06-solution-architecture.md)) | argon2 needs a package; PBKDF2 is in the BCL and sound |
| SQLite + `EnsureCreated` | PostgreSQL + migrations | Swap the provider when a server exists (D5) |
| CSV export | .xlsx | Excel opens CSV natively; a spreadsheet library is a dependency for no gain |
| Print via `FlowDocument` | a PDF library | The Windows print dialog includes Print-to-PDF |
| JSON numbers for money | JSON strings ([07 §1](07-api-contract.md)) | Both clients are .NET `decimal` today; revisit if a JavaScript client appears |
