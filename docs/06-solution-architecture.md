# 06 · Phase 2 — Solution Architecture

Status: **draft**
Covers [02 §14](02-requirement-analysis.md) deliverables 1, 6 and 7: solution structure,
non-functional design (auth, audit, offline outbox, backup), and deployment topology.

> **Offline outbox — designed, not built (05 Aug 2026).** An `Outbox.cs` existed in the desktop app
> implementing a SQLite queue and replay, with no call site anywhere in the repository; it was
> removed rather than left to imply a capability that was not there. A write during a network drop
> currently fails and the work is lost. Everything below describing the outbox is the intended
> design, not the shipped behaviour.
Data model and calculation rules are in [03-domain-model.md](03-domain-model.md).

> **Stance.** Ten users, ~3,000 sale lines and ~10,000 stock movements a year. Nothing here is
> sized for scale. Every component listed exists because a story needs it; anything that could be
> added "for later" is in §7 with the trigger that would bring it back.

---

## 1. Components

| # | Component | Stack | Why it exists |
|---|---|---|---|
| 1 | **DiamondCalc** | .NET class library, **zero dependencies** | CALC-1…11. Referenced by the API. Never talks to a database (§3) |
| 2 | **DiamondApi** | ASP.NET Core minimal API + EF Core | The only writer to the database. Auth, audit, sync, calc invocation |
| 3 | **Database** | PostgreSQL (SQL Server if the client is Microsoft-aligned) | Schema per [03 §2](03-domain-model.md) |
| 4 | **Desktop** | .NET + WPF, local SQLite | SALES-001 keyboard entry, INV-001/004/005, MDM screens |
| 5 | **Android** | Kotlin + Compose, Room | DASH-001/002, PAY-001, INV-003 read-only |

```
  WPF desktop ──┐                        ┌── DiamondCalc  (pure, no I/O)
   SQLite cache │                        │
                ├── HTTPS ── DiamondApi ─┤
  Android    ───┘                        └── EF Core ── PostgreSQL
   Room cache
```

**One writer.** Neither client writes to the database directly and neither re-implements a
calculation. That is the whole reason the workbook drifted: two artifacts, each doing its own
arithmetic, with nothing forcing them to agree.

### 1.1 Repository layout

```
DiamondCalc/          calculation engine — no dependencies, no I/O
DiamondCalc.Tests/    one runnable check per rule: `dotnet run --project DiamondCalc.Tests`
DiamondApi/           web API, EF Core model, migrations, auth, audit
docs/                 this specification set
```

Desktop and Android join as `DiamondDesktop/` and `android/` when block 5 of the build order
starts ([05 §3](05-backlog.md)). They are not scaffolded now — an empty WPF project that nobody
opens for three months is a merge conflict waiting to happen, not progress.

> `ponytail:` no solution file, no `Directory.Build.props`, no shared abstractions project. Three
> projects do not need orchestration. Add a `.sln` when a fourth project or an IDE workflow needs it.

### 1.2 API internal structure

Minimal API endpoints → a handler per use case → EF Core. **No repository layer, no service
interfaces, no MediatR.** `DbContext` is already a unit of work and already mockable; wrapping it
in `IInvoiceRepository` to call it from exactly one place buys nothing and costs a file per entity.

The one deliberate seam is **DiamondCalc**, and it is a seam for a reason that survives review:
desktop and Android will link the same rules, so they cannot live inside the web project.

---

## 2. Authentication & authorisation

| Concern | Decision |
|---|---|
| Passwords | **argon2id**, per-user salt. Never MD5/SHA-family, never unsalted |
| Sessions | JWT, `session_timeout_min` (default 60) from `app_setting`, refresh on activity |
| Lockout | `lockout_attempts` (default 5) → `app_user.locked_until`. Every failure writes an `audit_entry` with `action='LOGIN_FAIL'` and a NULL user where the username was unknown |
| Transport | HTTPS only. HTTP is refused, not redirected, once deployed |
| Authorisation | Server-side on **every** endpoint against the role matrix in [03 §4](03-domain-model.md) (NFR-SEC-4) |
| Android | Biometric unlock re-opens a stored refresh token. It is a convenience over an existing session, never a second way to authenticate |

**Client-side hiding is courtesy, not security.** Every endpoint re-checks the caller's role even
when the UI could not have produced the request. A three-role system has no excuse for a missing
check.

`manager_sees_margin` is a **setting**, not a permission row ([03 §4](03-domain-model.md)). It gates
the margin fields of DASH-001/W7 server-side.

---

## 3. Where the calculation engine sits

```
  request → handler → DiamondCalc (pure) → entity → EF Core → database
```

DiamondCalc takes decimals and returns decimals. It never loads an entity, never sees a
`DbContext`, and has no package references — which is what makes "the same inputs give the same
answer on every platform" (CALC-001, AC 4) a property of the code rather than a promise.

**Stored vs derived, restated because it is the whole design:**

| Value | Stored? |
|---|---|
| `sales_line.amount` | **Stored** — it is the rounding boundary and it was printed on a bill |
| Invoice total | Derived: Σ stored line amounts |
| Outstanding | Derived: total − Σ receipts. Never stored |
| Grade × size balance | Derived: `SUM(weight_ct)`. Never stored |

---

## 4. Audit storage

`audit_entry` is append-only: `INSERT` only, no `UPDATE`, no `DELETE`, enforced by the API and by a
database role that lacks those grants on that one table. Entries carry `before`/`after` JSON.

| Question | Answer |
|---|---|
| What is audited | Every mutation of invoice, line, receipt, movement, disposition, master data, user, setting — plus failed logins |
| Retention | Forever. ~10k mutations a year is a rounding error in storage terms |
| Who reads it | Manager and Owner (role matrix) |
| Growth | Indexed by `(entity_type, entity_id, occurred_at)` and `(user_id, occurred_at)` |

> `ponytail:` no separate audit database, no event-sourcing framework, no CDC. One table, two
> indexes. Revisit only if audit writes ever measurably slow a transaction — at this volume they
> will not.

---

## 5. Offline & sync (SYNC-001/002)

Both clients are local-first: write to the local store, queue an **outbox**, drain it when the
network returns.

**Outbox row:** `op_id (uuid)`, `entity_type`, `entity_id`, `operation`, `payload`, `created_at`,
`attempts`, `last_error`. Drained in creation order.

| Rule | Detail |
|---|---|
| **Client-generated ids** | Every id is a uuid v7 minted by the client. Two offline clients never collide |
| **Idempotency** | Every mutating request carries `op_id`. The server records applied `op_id`s and returns the original result on a replay. A retry after a timeout is therefore free |
| **Invoice numbers** | Assigned **by the server at post**, never offline. `invoice_no` is NULL on a draft ([03 §2.3](03-domain-model.md)) |
| **Movements merge** | Append-only inserts merge additively. Two clients selling from the same bucket cannot lost-update each other — the balance is a `SUM` |
| **Invoice edits** | Owned mutable records. Later `version` wins; the superseded version is retained in the audit log, never discarded |
| **Server authoritative** | Balances and outstanding are recomputed server-side. A client's cached derivation is a display value |
| **Unresolvable conflict** | Manager conflict inbox. Never a silent drop |
| **Pull** | `GET /sync/changes?since=<cursor>` returns rows changed after the cursor, per entity type |

**The one honest gap:** stock is not reserved on DRAFT ([03 §2.6](03-domain-model.md), GAP-3). Two
salespeople can draft-sell the same parcel offline and both discover it at post time, where the
negative-stock policy (Q10, default WARN) applies. Reservation is a real feature with a real cost;
it is not in the MVP and the client should know that explicitly.

---

## 6. Backup & restore (OPS-001)

They can copy a workbook to a pen drive today. Anything less resilient than that is a regression,
whatever else the system does.

| | Decision |
|---|---|
| What | Nightly full database dump (`pg_dump` / native backup), plus WAL archiving if the host supports it |
| Where | Two destinations, one of them **off the machine that runs the database** |
| Retention | 30 daily, 12 monthly |
| Verification | The backup job reports success **and** failure. A silent backup is not a backup |
| Restore | **Rehearsed before go-live** — restore to a scratch instance and re-run the MIG-003 reconciliation against it |

Concrete plan depends on D5 (§7). Nothing above is exotic on any of the three options.

---

## 7. Deployment topology — 🔴 blocked on D5

D5 is the one open decision with **no default**. Android and offline sync both require the API to be
reachable from outside the office, which is a cost and security consequence, not a preference.

| Option | Works for | Cost | Risk |
|---|---|---|---|
| **A · Office box** | Desktop only | Lowest | Android needs a VPN or port-forward. Backups and uptime become someone's unpaid job. Power/ISP outage stops trading |
| **B · Small VPS** (recommended) | Everything | ~₹1–2k/month | Needs TLS, a firewall and patching. Standard, well-trodden |
| **C · Managed cloud** | Everything | Highest | Least operational work, most vendor coupling |

**Recommendation: B.** One small VPS, managed PostgreSQL or self-hosted with the backup regime in
§6, TLS via Let's Encrypt. It is the cheapest option that does not make the Android app a second
project.

Environments: `dev` (local SQLite for the API's own tests), `staging` (migration rehearsal, restore
rehearsal), `prod`. No staging until migration work starts — a third empty environment is
maintenance with no user.

---

## 8. Non-functional targets and what carries them

| NFR | Carried by |
|---|---|
| Sales entry keystroke response | Desktop computes CALC-1/2 **locally** through the same engine and confirms on save. No round-trip per keystroke |
| Integrity | Constraints live in the database, not only in code ([03 §2](03-domain-model.md)); INV-1…6 run as tests and nightly |
| Auditability | §4 |
| Offline | §5 |
| Recoverability | §6 |
| Determinism across platforms | §3 — one engine, linked, not reimplemented |

---

## 9. Open items

| ID | Item | Blocks |
|---|---|---|
| **D5** | Where the server runs | §7 entirely; OPS-001; the Android app's reachability |
| D7 | How many users, in which roles | Seed data for AUTH-002 |
| — | Whether desktop needs to work with the office box switched off | Whether the local SQLite cache is a cache or a second source of truth |
