# diamond-sales

Replacing a two-workbook Excel sales & inventory process with a Windows desktop app
(sales entry), an Android app (owner dashboard), and a shared backend holding one
calculation engine and an append-only stock ledger.

> **Start here if you are new: [docs/12-wpf-supabase-handoff.md](docs/12-wpf-supabase-handoff.md)** —
> the desktop app now writes to Supabase, not to `DiamondApi`. That file says what is verified,
> what is not, and what was deliberately dropped.

- Spec overview: [docs/00-overview.md](docs/00-overview.md)
- Phase 1 workbook forensics: [docs/01-workbook-forensics.md](docs/01-workbook-forensics.md) - verified 2026-07-25
- **Verification report: [docs/04-workbook-verification.md](docs/04-workbook-verification.md)** - 24 verified, 6 incorrect, 12 missed
- Requirement analysis: [docs/02-requirement-analysis.md](docs/02-requirement-analysis.md) - draft, awaiting sign-off
- **Phase 1 closure & sign-off pack: [docs/phase-1-closure.md](docs/phase-1-closure.md)** - client action required
- Phase 2 domain model: [docs/03-domain-model.md](docs/03-domain-model.md) - schema, calc engine, roles
- Phase 2 solution architecture: [docs/06-solution-architecture.md](docs/06-solution-architecture.md) - components, auth, sync, backup, deployment (blocked on D5)
- Phase 2 API contract: [docs/07-api-contract.md](docs/07-api-contract.md) - endpoints, DTOs, errors, idempotency, sync protocol
- Phase 2 migration design: [docs/08-migration-design.md](docs/08-migration-design.md) - grade/size seed lists, parser rules, cut-over reconciliation
- Phase 3 build backlog: [docs/05-backlog.md](docs/05-backlog.md) - 35 stories, 192 pts, build order
- **Implementation status: [docs/11-implementation-status.md](docs/11-implementation-status.md)** - every story, built or blocked
- Phase 4 owner dashboard: [docs/10-dashboard.md](docs/10-dashboard.md) - W1...W15, filters, drill-downs
- Manual test pack: [docs/09-test-plan.md](docs/09-test-plan.md) - ~60 cases across every tab, real workbook figures
- Full requirements: `Diamond_Sales_System_Requirements.html` (not yet committed - see G4)

## Code

- [DiamondCalc/](DiamondCalc/) - the calculation engine. CALC-1...11 as pure functions, zero dependencies.
- [DiamondApi/](DiamondApi/) - the backend: schema, auth, stock ledger, posting, receipts, audit, dashboard.
- [DiamondDesktop/](DiamondDesktop/) - WPF client: sales entry, invoices, receivables, stock, intake,
  conversions, rejections, master data, dashboard, audit, users, settings.
- [DiamondCalc.Tests/](DiamondCalc.Tests/) - 129 checks across all three, verified against the real
  workbook figures.

```
dotnet run --project DiamondCalc.Tests                     # everything, ~2s
dotnet run --project DiamondApi --urls http://localhost:5000
dotnet run --project DiamondDesktop                        # sign in: owner / owner
```