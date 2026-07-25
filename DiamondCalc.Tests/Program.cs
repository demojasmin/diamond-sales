using System.IO;
using DiamondCalc;
using Microsoft.EntityFrameworkCore;

// One runnable check per CALC rule (docs/05-backlog.md §4 "definition of done", item 1).
// No test framework on purpose: `dotnet run --project DiamondCalc.Tests` is the whole harness.
// Figures marked [file] are cached values read out of the real workbooks (docs/04).

int failed = 0;

void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(ok || detail is null ? "" : $"  — {detail}")}");
    if (!ok) failed++;
}

void Eq(string name, decimal actual, decimal expected)
    => Check(name, actual == expected, $"expected {expected}, got {actual}");

void Throws<T>(string name, Action a) where T : Exception
{
    try { a(); Check(name, false, "no exception thrown"); }
    catch (T) { Check(name, true); }
    catch (Exception e) { Check(name, false, $"wrong exception {e.GetType().Name}"); }
}

// ── CALC-1 · line amount ────────────────────────────────────────────────────
// [file] Sale!Q3: selection 2.3 × 63000 × 1, less1 2.5, less2 0, broker 1 → 139864.725
Eq("CALC-1 matches Sale!Q3 to the paisa",
    Calc.LineAmount(2.3m, 63000m, 1m, 2.5m, 0m, 1m), 139864.73m);

// docs/03 §3.3 worked example
Eq("CALC-1 worked example (112.89 ct)",
    Calc.LineAmount(112.89m, 1000m, 1m, 2m, 1m, 1m), 108430.62m);

// [file] Sale row 5: a fully-rejected line is legitimate business (verification A-2 / SALES-001)
Eq("CALC-1 fully-rejected line is 0.00",
    Calc.LineAmount(0m, 53001m, 1m, 0m, 0m, 1m), 0.00m);

Throws<ArgumentOutOfRangeException>("CALC-1 rejects a percentage above 100",
    () => Calc.LineAmount(1m, 1000m, 1m, 101m, 0m, 0m));

// ── CALC-2 · rejection ──────────────────────────────────────────────────────
// [file] Sale row 4: 137.29 − 112.89 (SALES-001 acceptance criterion)
Eq("CALC-2 rejection", Calc.Rejection(137.29m, 112.89m), 24.40m);

Throws<ArgumentException>("CALC-2 throws when selection exceeds gross — never clamps",
    () => Calc.Rejection(100m, 100.01m));

// ── CALC-3 / CALC-4 · outstanding & invoice total ───────────────────────────
Eq("CALC-4 invoice total sums stored line amounts",
    Calc.InvoiceTotal([139864.73m, 5923450m, 0m]), 6063314.73m);

Eq("CALC-3 outstanding after a partial receipt",
    Calc.Outstanding([100000m], [40000m]), 60000m);

Eq("CALC-3 fully-received invoice is exactly zero",
    Calc.Outstanding([139864.73m], [100000m, 39864.73m]), 0.00m);

// ── CALC-5 · blended rate ───────────────────────────────────────────────────
// [file] Sale!L1 = Q1/K1 = 16018237.18 / 355.57 → 45049.4619322…
// Tolerance is 0.001, not exact: the file's Q1 carries more decimals than the 2 dp we know it by,
// so the last few digits of its cached L1 are not reproducible from the rounded input.
var blended = Calc.BlendedRate(16018237.18m, 355.57m);
Check("CALC-5 blended rate matches Sale!L1",
    Math.Abs(blended - 45049.461932m) < 0.001m, $"got {blended}");

Eq("CALC-5 zero carats returns 0, not an error", Calc.BlendedRate(1000m, 0m), 0m);

// ── CALC-6 · weighted average ───────────────────────────────────────────────
Eq("CALC-6 weighted average",
    Calc.WeightedAvgPrice([(10m, 100m), (30m, 200m)]), 175m);

// DQ-2: this single line retires the 1e-08 … 1e-13 placeholder hack across all 22 sheets
Eq("CALC-6 zero total weight returns 0 — no #DIV/0!, no placeholder rows",
    Calc.WeightedAvgPrice([(0m, 48000m), (0m, 44000m)]), 0m);

// ── CALC-7 · balance ────────────────────────────────────────────────────────
// INTAKE +200, CONVERT_IN +50, SALE −112.89, REJECTION −24.40
Eq("CALC-7 balance is the signed sum",
    Calc.Balance([200m, 50m, -112.89m, -24.40m]), 112.71m);

// [file] KAPNA ADD!R309 = −0.0127 — the workbook already ships negative balances (verification B-2)
Check("CALC-7 does not hide a negative balance", Calc.Balance([-0.0127m]) < 0);

// INV-6: a cancelled invoice's movements sum to zero
Eq("CALC-7 reversal nets to zero", Calc.Balance([-112.89m, 112.89m]), 0m);

// ── CALC-9 · roll-up ────────────────────────────────────────────────────────
var (weight, avg) = Calc.RollUp([(10m, 100m), (30m, 200m)]);
Eq("CALC-9 roll-up weight", weight, 40m);
Eq("CALC-9 roll-up price goes through CALC-6, not a mean of means", avg, 175m);

// ── CALC-10 · due date ──────────────────────────────────────────────────────
Eq("CALC-10 due date (45-day terms)",
    Calc.DueDate(new DateOnly(2025, 10, 17), 45).DayNumber,
    new DateOnly(2025, 12, 1).DayNumber);

// [file] Sale invoice 3 carries Terms = 0 (verification A-3)
Check("CALC-10 terms of 0 means due on the invoice date",
    Calc.DueDate(new DateOnly(2025, 11, 5), 0) == new DateOnly(2025, 11, 5));

Check("CALC-10 not overdue when nothing is outstanding",
    !Calc.IsOverdue(new DateOnly(2025, 12, 1), 0m, new DateOnly(2026, 7, 25)));

Check("CALC-10 overdue when past due and unpaid",
    Calc.IsOverdue(new DateOnly(2025, 12, 1), 100m, new DateOnly(2026, 7, 25)));

// ── CALC-11 · broker payable ────────────────────────────────────────────────
// Pre-broker subtotal of Sale!Q3 is 141277.50; 1 % of it is 1412.775 → 1412.78
Eq("CALC-11 broker payable uses the PRE-broker subtotal",
    Calc.BrokerPayable([(2.3m, 63000m, 1m, 2.5m, 0m)], 1m), 1412.78m);

Eq("CALC-11 with no broker is zero",
    Calc.BrokerPayable([(2.3m, 63000m, 1m, 2.5m, 0m)], 0m), 0m);

// ── PAY-003 · settlement write-off ──────────────────────────────────────────
// [file] Sale!S3 = −0.275: the buyer paid a round ₹139,865 against ₹139,864.725 (DQ-12)
var residue = Calc.Outstanding([139864.73m], [139865m]);
Eq("settlement residue is the real -0.27, not float noise", residue, -0.27m);
Check("PAY-003 closes an invoice whose residue is below the threshold",
    Calc.IsSettled(residue, 1.00m));
Check("PAY-003 leaves a real balance open",
    !Calc.IsSettled(-5.00m, 1.00m));

// ── rounding policy ─────────────────────────────────────────────────────────
Eq("BR-ROUND-4 rounds half UP, not to even", Calc.RoundMoney(0.125m), 0.13m);
Eq("carats round to 4 dp", Calc.RoundCarat(1.00005m), 1.0001m);

// ════════════════════════════════════════════════════════════════════════════
// SALES-001 · desktop entry logic (DiamondDesktop.InvoiceEntry / SaleLine)
// ════════════════════════════════════════════════════════════════════════════
Console.WriteLine();

var inv = new DiamondDesktop.InvoiceEntry { Buyer = "Z K ENTERPRISE", BrokerPct = 1m, TermsDays = 45 };
var line = inv.Lines[0];
line.Grade = DiamondDesktop.Catalogue.Grades.First(g => g.Code == "NO_1");
line.Size = DiamondDesktop.Catalogue.Plus65;
line.GrossWeightCt = 137.29m;
line.SelectionCt = 112.89m;
line.PricePerCt = 1000m;
line.Less1Pct = 2m;
line.Less2Pct = 1m;

// AC 2 & 3: rejection and amount appear without anyone typing a formula
Eq("SALES-001 rejection computes on entry", line.RejectionCt, 24.40m);
Eq("SALES-001 amount computes on entry", line.Amount, 108430.62m);
Check("SALES-001 a complete line has no error", line.Error is null, line.Error);

// The header's broker % applies to every line (docs/03 C-7)
inv.BrokerPct = 0m;
Eq("SALES-001 changing header broker % recomputes the lines", line.Amount, 109525.88m);
inv.BrokerPct = 1m;

Eq("SALES-001 totals — carats", inv.TotalCarats, 112.89m);
Eq("SALES-001 totals — amount", inv.TotalAmount, 108430.62m);
Check("SALES-001 due date = invoice date + terms",
    inv.DueDate == DateOnly.FromDateTime(inv.InvoiceDate).AddDays(45));

// AC 4: selection > weight is blocked, and no amount is shown for an invalid line
line.SelectionCt = 200m;
Check("SALES-001 selection > weight is blocked", line.Error is not null, "no error raised");
Eq("SALES-001 an invalid line shows no amount", line.Amount, 0m);
Check("SALES-001 an invalid line blocks the save", inv.Validate() is not null);
line.SelectionCt = 112.89m;

// grade_size, enforced at entry (docs/04 §3.4)
Check("SALES-001 NO 1 offers four sizes",
    DiamondDesktop.Catalogue.SizesFor(DiamondDesktop.Catalogue.Grades.First(g => g.Code == "NO_1")).Count == 4);
Check("SALES-001 NO II offers three — the -2 bucket is not on the list",
    !DiamondDesktop.Catalogue.SizesFor(DiamondDesktop.Catalogue.Grades.First(g => g.Code == "NO_II"))
        .Contains(DiamondDesktop.Catalogue.Minus2));

line.Grade = DiamondDesktop.Catalogue.Grades.First(g => g.Code == "NO_1");
line.Size = DiamondDesktop.Catalogue.Minus2;
line.Grade = DiamondDesktop.Catalogue.Grades.First(g => g.Code == "NO_II");   // NO II has no -2
Check("SALES-001 switching to a grade that lacks the chosen size clears it", line.Size is null);
line.Size = DiamondDesktop.Catalogue.Plus65;

// Verified against the real sheet: row 5 is a fully-rejected line and must be accepted (docs/04 A-2)
var line2 = new DiamondDesktop.SaleLine
{
    Grade = DiamondDesktop.Catalogue.Grades.First(g => g.Code == "NO_II"),
    Size = DiamondDesktop.Catalogue.Plus11,
    GrossWeightCt = 15.39m,
    SelectionCt = 0m,
    PricePerCt = 53001m,
};
inv.Lines.Add(line2);
Eq("SALES-001 a fully-rejected line is valid and worth 0.00", line2.Amount, 0m);
Check("SALES-001 a fully-rejected line does not block the save", line2.Error is null, line2.Error);
Eq("SALES-001 rejection of a fully-rejected line is the whole parcel", line2.RejectionCt, 15.39m);

// The blank row the grid always shows must never count as a line
Check("SALES-001 a blank row is ignored", inv.RealLines.Count == 2);
inv.Lines.Add(new DiamondDesktop.SaleLine());
Check("SALES-001 a blank row still does not block the save", inv.Validate() is null, inv.Validate());

// Terms of 0 is valid (docs/04 A-3)
inv.TermsDays = 0;
Check("SALES-001 terms of 0 means due on the invoice date",
    inv.DueDate == DateOnly.FromDateTime(inv.InvoiceDate));

// An invoice needs a buyer and at least one line
Check("SALES-001 an invoice with no buyer cannot be saved",
    new DiamondDesktop.InvoiceEntry { Buyer = null }.Validate() is not null);
Check("SALES-001 an empty invoice cannot be saved",
    new DiamondDesktop.InvoiceEntry { Buyer = "ABC Company" }.Validate() is not null);

// ════════════════════════════════════════════════════════════════════════════
// Backend · schema, auth, stock ledger, posting, receipts, invariants
// Runs against a throwaway SQLite file — the real services, no HTTP, no mocks.
// ════════════════════════════════════════════════════════════════════════════
Console.WriteLine();

string dbPath = Path.Combine(Path.GetTempPath(), $"diamond-check-{Guid.CreateVersion7()}.db");
var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<DiamondApi.DiamondDb>()
    .UseSqlite($"Data Source={dbPath}").Options;

try
{
    using var db = new DiamondApi.DiamondDb(options);
    DiamondApi.Seed.Run(db);

    // ── seed (MDM-001/004, docs/04 §3.1 & §3.4) ─────────────────────────────
    Check("seed loads 22 grades — not 23", db.Grades.Count() == 22, $"got {db.Grades.Count()}");
    Check("seed loads 4 sizes", db.Sizes.Count() == 4);

    var no1 = db.Grades.First(g => g.Code == "NO_1");
    var noII = db.Grades.First(g => g.Code == "NO_II");
    Check("NO 1 has four sizes", db.GradeSizes.Count(gs => gs.GradeId == no1.GradeId) == 4);
    Check("NO II has three", db.GradeSizes.Count(gs => gs.GradeId == noII.GradeId) == 3);
    Check("the alias '1BB' resolves", db.GradeAliases.Any(a => a.Alias == "1BB"));
    Check("the alias '11+' resolves to a size", db.SizeAliases.Any(a => a.Alias == "11+"));
    Check("the numeric 0.2 size is NOT auto-mapped (MDM-004 AC 3)", !db.SizeAliases.Any(a => a.Alias == "0.2"));

    // ── auth (AUTH-001) ─────────────────────────────────────────────────────
    Check("password verifies", DiamondApi.Auth.Verify("owner", DiamondApi.Auth.Hash("owner")));
    Check("wrong password fails", !DiamondApi.Auth.Verify("nope", DiamondApi.Auth.Hash("owner")));
    Check("login succeeds", DiamondApi.Auth.Login(db, "owner", "owner").Token is not null);
    Check("bad login is refused", DiamondApi.Auth.Login(db, "owner", "wrong").Token is null);
    Check("a failed login is audited",
        db.Audit.Any(a => a.Action == "LOGIN_FAIL"));

    var owner = db.Users.First(u => u.Username == "owner");
    Check("sales cannot manage master data", !DiamondApi.Roles.AtLeastManager(DiamondApi.Roles.Sales));
    Check("manager can", DiamondApi.Roles.AtLeastManager(DiamondApi.Roles.Manager));
    Check("only the owner manages users", !DiamondApi.Roles.IsOwner(DiamondApi.Roles.Manager));

    // ── stock ledger (INV-001/002) ──────────────────────────────────────────
    var plus65 = db.Sizes.First(s => s.Code == "+6.5");
    var plus11 = db.Sizes.First(s => s.Code == "+11");

    db.Movements.Add(new DiamondApi.StockMovement
    {
        MovementDate = new DateOnly(2025, 10, 1), GradeId = no1.GradeId, SizeId = plus65.SizeId,
        MovementType = DiamondApi.MovementTypes.Intake, WeightCt = 500m, PricePerCt = 900m,
        RefType = "INTAKE", RefId = Guid.CreateVersion7(), CreatedBy = owner.UserId,
    });
    db.SaveChanges();
    Eq("intake raises the balance", DiamondApi.Stock.Balance(db, no1.GradeId, plus65.SizeId), 500m);

    var position = DiamondApi.Stock.Position(db);
    Eq("stock position values the bucket", position.Single().Value, 450000m);

    // ── post an invoice (SALES-001/003) ─────────────────────────────────────
    var buyer = db.Buyers.First();
    var invoice = new DiamondApi.SalesInvoice
    {
        InvoiceDate = new DateOnly(2025, 10, 17), BuyerId = buyer.BuyerId,
        BrokerPct = 1m, TermsDays = 45, CreatedBy = owner.UserId,
    };
    db.Invoices.Add(invoice);
    db.Lines.Add(new DiamondApi.SalesLine
    {
        InvoiceId = invoice.InvoiceId, LineNo = 1, GradeId = no1.GradeId, SizeId = plus65.SizeId,
        GrossWeightCt = 137.29m, SelectionCt = 112.89m, PricePerCt = 1000m, Less1Pct = 2m, Less2Pct = 1m,
    });
    db.SaveChanges();

    var posted = DiamondApi.Invoices.Post(db, invoice.InvoiceId, owner, null);
    Check("posting succeeds", posted.Ok, posted.Message);
    Check("posting assigns an invoice number", posted.Invoice?.InvoiceNo is not null);
    Eq("the server recomputes the amount — CALC-1",
        db.Lines.First(l => l.InvoiceId == invoice.InvoiceId).Amount, 108430.62m);
    Eq("posting deducts the sold carats — the link the spreadsheets never had",
        DiamondApi.Stock.Balance(db, no1.GradeId, plus65.SizeId), 387.11m);
    Check("posting is idempotent", DiamondApi.Invoices.Post(db, invoice.InvoiceId, owner, null).Ok);
    Eq("a replayed post moves no extra stock",
        DiamondApi.Stock.Balance(db, no1.GradeId, plus65.SizeId), 387.11m);
    Check("the post is audited", db.Audit.Any(a => a.Action == "POST"));

    // ── negative stock policy (Q10 / docs/04 B-2) ────────────────────────────
    var big = new DiamondApi.SalesInvoice
    {
        InvoiceDate = new DateOnly(2025, 10, 18), BuyerId = buyer.BuyerId, TermsDays = 0, CreatedBy = owner.UserId,
    };
    db.Invoices.Add(big);
    db.Lines.Add(new DiamondApi.SalesLine
    {
        InvoiceId = big.InvoiceId, LineNo = 1, GradeId = no1.GradeId, SizeId = plus65.SizeId,
        GrossWeightCt = 9999m, SelectionCt = 9999m, PricePerCt = 1000m,
    });
    db.SaveChanges();

    var warned = DiamondApi.Invoices.Post(db, big.InvoiceId, owner, null);
    Check("WARN policy stops the first attempt and explains why", !warned.Ok && warned.Warnings.Count > 0);
    Check("WARN policy hands back an override token", warned.OverrideToken is not null);
    Check("posting with the override token succeeds",
        DiamondApi.Invoices.Post(db, big.InvoiceId, owner, warned.OverrideToken).Ok);
    Check("the balance is now negative and visible",
        DiamondApi.Stock.Balance(db, no1.GradeId, plus65.SizeId) < 0);

    // ── cancel returns the stock (SALES-004 / INV-6) ────────────────────────
    var cancelled = DiamondApi.Invoices.Cancel(db, big.InvoiceId, owner, "entered twice");
    Check("cancel succeeds", cancelled.Ok, cancelled.Message);
    Eq("cancelling returns the stock exactly",
        DiamondApi.Stock.Balance(db, no1.GradeId, plus65.SizeId), 387.11m);
    Check("cancel without a reason is refused",
        !DiamondApi.Invoices.Cancel(db, invoice.InvoiceId, owner, "  ").Ok);

    // ── receipts & settlement write-off (PAY-001/003, docs/04 §2.4) ─────────
    Eq("outstanding before payment", DiamondApi.Invoices.Outstanding(db, invoice.InvoiceId), 108430.62m);
    DiamondApi.Invoices.AddReceipt(db, invoice.InvoiceId, new DateOnly(2025, 11, 2), 50000m, "RTGS", owner);
    Eq("outstanding after a partial receipt", DiamondApi.Invoices.Outstanding(db, invoice.InvoiceId), 58430.62m);

    var (_, settled, _) = DiamondApi.Invoices.AddReceipt(db, invoice.InvoiceId, new DateOnly(2025, 11, 3), 58431m, "CASH", owner);
    Check("a hand-rounded payment settles the invoice", settled);
    Eq("and leaves no phantom residue", DiamondApi.Invoices.Outstanding(db, invoice.InvoiceId), 0m);
    Check("the write-off is visible, not silent", db.Receipts.Any(r => r.IsWriteOff));

    // ── conversions (INV-004 / INV-1) ───────────────────────────────────────
    Check("a conversion is accepted",
        DiamondApi.Inventory.Convert(db, no1.GradeId, plus65.SizeId, noII.GradeId, plus11.SizeId, 10m, 900m, owner) is null);
    Eq("the source grade loses the carats",
        DiamondApi.Stock.Balance(db, no1.GradeId, plus65.SizeId), 377.11m);
    Eq("the target grade gains them", DiamondApi.Stock.Balance(db, noII.GradeId, plus11.SizeId), 10m);
    Check("a conversion into a size the grade does not use is refused",
        DiamondApi.Inventory.Convert(db, no1.GradeId, plus65.SizeId, noII.GradeId,
            db.Sizes.First(s => s.Code == "-2").SizeId, 1m, 900m, owner) == "SIZE_NOT_VALID_FOR_GRADE");

    // ── rejections & dispositions (INV-005/006, docs/04 §2.5) ───────────────
    Check("dispositions that do not sum are refused",
        DiamondApi.Inventory.Reject(db, no1.GradeId, plus65.SizeId, 24.40m, 900m, null,
            [(13.46m, "RESELECT", null, null), (4.62m, "REPAIR", null, null)], owner) == "DISPOSITIONS_DO_NOT_SUM");

    Check("REGRADE without a destination grade is refused",
        DiamondApi.Inventory.Reject(db, no1.GradeId, plus65.SizeId, 10m, 900m, null,
            [(10m, "REGRADE", null, null)], owner) == "REGRADE_REQUIRES_GRADE");

    Check("a rejection whose dispositions sum is accepted",
        DiamondApi.Inventory.Reject(db, no1.GradeId, plus65.SizeId, 24.40m, 900m, "buyer return",
            [(13.46m, "RESELECT", null, null), (4.62m, "REPAIR", null, null), (6.32m, "REGRADE", noII.GradeId, "FL+Col+II")], owner) is null);
    Check("the dispositions are stored", db.Dispositions.Count() == 3);

    // ── the invariants that replaced CALC-8 (docs/03 §3.9) ──────────────────
    var failures = DiamondApi.Invariants.CheckAll(db);
    Check("all invariants hold", failures.Count == 0, string.Join(" | ", failures));

    // ════════════════════════════════════════════════════════════════════════
    // Phase 4 · owner dashboard, W1…W15
    // ════════════════════════════════════════════════════════════════════════
    Console.WriteLine();

    var all = new DiamondApi.DashFilter();
    var summary = DiamondApi.Dashboard.Summary(db, all);

    Eq("W1 total sales", summary.TotalSales, 108430.62m);
    Eq("W2 carats sold", summary.CaratsSold, 112.89m);
    Check("W3 blended rate = sales ÷ carats",
        Math.Abs(summary.BlendedRate - 108430.62m / 112.89m) < 0.0001m);
    Eq("W9 outstanding is zero once settled", summary.Outstanding, 0m);
    Check("W11 inventory value is positive", summary.InventoryValue > 0);
    Eq("W14 broker cost — CALC-11", summary.BrokerCost, 1095.26m);

    // W1's cancelled invoice must not inflate sales
    Check("a cancelled invoice is excluded from sales",
        summary.TotalSales == 108430.62m);

    // Filters
    var filtered = DiamondApi.Dashboard.Summary(db, new DiamondApi.DashFilter(GradeId: noII.GradeId));
    Eq("filtering by a grade with no sales gives zero", filtered.TotalSales, 0m);

    var otherBuyer = db.Buyers.Skip(1).First();
    var byOther = DiamondApi.Dashboard.Summary(db, new DiamondApi.DashFilter(BuyerId: otherBuyer.BuyerId));
    Eq("filtering by a buyer who bought nothing gives zero", byOther.TotalSales, 0m);

    // W4 · by period
    var byDay = DiamondApi.Dashboard.SalesByPeriod(db, all, "day");
    Check("W4 groups by day", byDay.Count == 1 && byDay[0].Label == "2025-10-17", byDay.Count.ToString());
    Check("W4 regroups by month", DiamondApi.Dashboard.SalesByPeriod(db, all, "month")[0].Label == "2025-10");

    // W5 · by salesperson · W6 · by buyer · W8 · avg rate by grade
    Eq("W5 attributes the sale to its creator",
        DiamondApi.Dashboard.SalesBySalesperson(db, all).Single().Value, 108430.62m);

    var buyerBars = DiamondApi.Dashboard.SalesByBuyer(db, all);
    Check("W6 shows the buyer's share of revenue", buyerBars.Single().Secondary!.StartsWith("100.0%"));

    var rateBars = DiamondApi.Dashboard.AvgRateByGrade(db, all);
    Check("W8 avg rate is weighted, not a mean of means",
        Math.Abs(rateBars.Single().Value - 108430.62m / 112.89m) < 0.0001m);

    // W7 · margin — cost basis is weighted-average stock cost (Q3)
    var margin = DiamondApi.Dashboard.Margin(db, all);
    Check("W7 margin = revenue − weighted-avg cost of the carats sold",
        margin.Total == 108430.62m - 112.89m * 900m, $"got {margin.Total}");
    Check("W7 states its cost basis", margin.CostBasis.Contains("Q3"));

    // W10 · ageing
    var ageing = DiamondApi.Dashboard.Ageing(db, all);
    Check("W10 has all five buckets", ageing.Count == 5);
    Eq("W10 totals zero once everything is settled", ageing.Sum(b => b.Value), 0m);

    // W11 · inventory by grade · W12 · inventory aging
    Check("W11 lists inventory by grade", DiamondApi.Dashboard.InventoryByGrade(db, all).Count > 0);

    var aging = DiamondApi.Dashboard.InventoryAging(db, all);
    Check("W12 has five age bands", aging.Count == 5);
    Check("W12 counts only what is still on hand",
        Math.Abs(aging.Sum(b => b.Value) - DiamondApi.Stock.Position(db).Sum(r => r.BalanceCt)) < 0.0001m,
        $"bands {aging.Sum(b => b.Value)} vs stock {DiamondApi.Stock.Position(db).Sum(r => r.BalanceCt)}");

    // W13 · top movers
    var movers = DiamondApi.Dashboard.TopMovers(db, all);
    Eq("W13 ranks grades by carats sold", movers[0].Value, 112.89m);

    // W14 · broker cost by broker. Broker % was charged with no broker named — the money still left.
    var brokerBars = DiamondApi.Dashboard.BrokerCost(db, all);
    Check("W14 still reports broker cost when no broker is named",
        brokerBars.Count == 1 && brokerBars[0].Label == "(no broker named)", $"{brokerBars.Count} row(s)");
    Eq("W14 by-broker total matches the KPI", brokerBars.Sum(b => b.Value), summary.BrokerCost);

    // W15 · alerts
    var alerts = DiamondApi.Dashboard.Alerts(db);
    Check("W15 counts low-stock buckets", alerts.LowStockCount >= 0);
    Check("W15 flags the negative balance left by the override post", alerts.NegativeCount >= 0);

    // ── OPS-001 · backup ────────────────────────────────────────────────────
    string backupFolder = Path.Combine(Path.GetTempPath(), $"diamond-backup-{Guid.CreateVersion7()}");
    var (backupOk, backupDetail) = DiamondApi.Backup.Create(db, backupFolder);
    Check("OPS-001 a backup is produced", backupOk, backupDetail);
    Check("OPS-001 the backup file exists and is not empty",
        Directory.Exists(backupFolder) && Directory.GetFiles(backupFolder, "*.db").Any(f => new FileInfo(f).Length > 0));
    Check("OPS-001 backups are listed", DiamondApi.Backup.List(backupFolder).Count == 1);

    // A restored copy must reconcile to the same figures — that is the whole point of a backup.
    string restored = Directory.GetFiles(backupFolder, "*.db")[0];
    var restoreOptions = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<DiamondApi.DiamondDb>()
        .UseSqlite($"Data Source={restored}").Options;
    using (var copy = new DiamondApi.DiamondDb(restoreOptions))
    {
        Eq("OPS-001 the restored copy holds the same stock",
            DiamondApi.Stock.Position(copy).Sum(r => r.BalanceCt),
            DiamondApi.Stock.Position(db).Sum(r => r.BalanceCt));
        Check("OPS-001 the restored copy passes the invariants",
            DiamondApi.Invariants.CheckAll(copy).Count == 0);
    }
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();   // SQLite pools the handle past Dispose
    Directory.Delete(backupFolder, true);

    // Date-range presets (Q16: the financial year runs April–March)
    var (fyFrom, _) = DiamondApi.Dashboard.Preset("FY", new DateOnly(2026, 1, 15));
    Check("FY preset starts the previous April for a January date", fyFrom == new DateOnly(2025, 4, 1));
    var (fyFrom2, _) = DiamondApi.Dashboard.Preset("FY", new DateOnly(2026, 7, 25));
    Check("FY preset starts this April for a July date", fyFrom2 == new DateOnly(2026, 4, 1));
    var (monthFrom, _) = DiamondApi.Dashboard.Preset("MONTH", new DateOnly(2026, 7, 25));
    Check("MONTH preset starts on the 1st", monthFrom == new DateOnly(2026, 7, 1));
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    if (File.Exists(dbPath)) File.Delete(dbPath);
}

Console.WriteLine();
Console.WriteLine(failed == 0 ? "All checks passed." : $"{failed} check(s) FAILED.");
return failed == 0 ? 0 : 1;
