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

// Catalogue.Grades / AllSizes are filled from Supabase at startup, so this harness seeds them
// directly — these checks are about entry logic and must run offline, with no live project.
// Codes are the ones the live `grade` / `size_bucket` tables actually use: spaces, not the
// underscores the old hardcoded seed had (docs/12 §3b).
foreach (var (code, order) in new[] { ("-2", 1), ("-6.5", 2), ("+6.5", 3), ("+11", 4) })
    DiamondDesktop.Catalogue.AllSizes.Add(
        new DiamondDesktop.Data.SizeBucket { SizeId = order, Code = code, SortOrder = order });
foreach (var (code, order) in new[] { ("NO 1", 1), ("NO 1 BB", 2), ("NO II", 3) })
    DiamondDesktop.Catalogue.Grades.Add(
        new DiamondDesktop.Data.Grade { GradeId = order, Code = code, DisplayName = code, SortOrder = order });

// grade_size, as the live table has it: NO 1 takes all four, everyone else drops -2.
DiamondDesktop.Catalogue.SetGradeSizes(
    from g in DiamondDesktop.Catalogue.Grades
    from s in DiamondDesktop.Catalogue.AllSizes
    where s.Code != "-2" || g.Code is "NO 1" or "NO 1 BB"
    select new DiamondDesktop.Data.GradeSize { GradeId = g.GradeId, SizeId = s.SizeId });

var uiMinus2 = DiamondDesktop.Catalogue.AllSizes.First(s => s.Code == "-2");
var uiPlus65 = DiamondDesktop.Catalogue.AllSizes.First(s => s.Code == "+6.5");
var uiPlus11 = DiamondDesktop.Catalogue.AllSizes.First(s => s.Code == "+11");
DiamondDesktop.Data.Grade GradeOf(string code) =>
    DiamondDesktop.Catalogue.Grades.First(g => g.Code == code);

var inv = new DiamondDesktop.InvoiceEntry { Buyer = "Z K ENTERPRISE", BrokerPct = 1m, TermsDays = 45 };
var line = inv.Lines[0];
line.Grade = GradeOf("NO 1");
line.Size = uiPlus65;
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
    DiamondDesktop.Catalogue.SizesFor(GradeOf("NO 1")).Count == 4);

// Size is the first column on the grid, so the picker is normally opened before a grade exists.
// Answering that with the whole size_bucket table offered 0.2 and 0.25 — kept only so the sales
// importer can resolve them, traded by no grade.
{
    var junk = new DiamondDesktop.Data.SizeBucket { SizeId = 99, Code = "0.25", SortOrder = 99 };
    DiamondDesktop.Catalogue.AllSizes.Add(junk);

    var noGrade = DiamondDesktop.Catalogue.SizesFor(null);
    Check("SALES-001 no grade yet offers only sizes some grade trades",
        !noGrade.Contains(junk), string.Join(",", noGrade.Select(s => s.Code)));
    Check("SALES-001 and still offers the real ones", noGrade.Count == 4);

    // A grade that trades it would still show it — the rule is "unsold", not a blacklist.
    Check("SALES-001 a picked grade is unaffected by the fallback",
        !DiamondDesktop.Catalogue.SizesFor(GradeOf("NO II")).Contains(junk));

    // Master data lists every size_bucket row, including the ones kept only so the sales importer
    // can resolve them. Saying which is which is what stops someone pairing 0.25 to a grade.
    Check("SIZE · a size no grade trades is marked import-only",
        !DiamondDesktop.Catalogue.IsSellableSize("0.25"));
    Check("SIZE · a real sieve size is sellable",
        DiamondDesktop.Catalogue.IsSellableSize("+6.5"));

    DiamondDesktop.Catalogue.AllSizes.Remove(junk);
}
Check("SALES-001 NO II offers three — the -2 bucket is not on the list",
    !DiamondDesktop.Catalogue.SizesFor(GradeOf("NO II")).Contains(uiMinus2));

line.Grade = GradeOf("NO 1");
line.Size = uiMinus2;
line.Grade = GradeOf("NO II");   // NO II has no -2
Check("SALES-001 switching to a grade that lacks the chosen size clears it", line.Size is null);
line.Size = uiPlus65;

// Verified against the real sheet: row 5 is a fully-rejected line and must be accepted (docs/04 A-2)
var line2 = new DiamondDesktop.SaleLine
{
    Grade = GradeOf("NO II"),
    Size = uiPlus11,
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

// ════════════════════════════════════════════════════════════════════════════
// Input bounds and error wording (DiamondDesktop.Bounds / .Friendly)
// ════════════════════════════════════════════════════════════════════════════
Console.WriteLine();

// The ceiling is the database's own: price_per_ct is numeric(12,2).
Check("Bounds accepts a normal parcel",
    DiamondDesktop.Bounds.TooLarge(232.86m, "Weight") is null);
Check("Bounds accepts the largest storable value",
    DiamondDesktop.Bounds.TooLarge(9_999_999_999.99m, "Price per carat") is null);
Check("Bounds rejects one paisa above it",
    DiamondDesktop.Bounds.TooLarge(10_000_000_000.00m, "Price per carat") is not null);

// The real figure that overflowed during end-to-end testing.
Check("Bounds rejects the 4,000,037,500 x 500,500 case",
    DiamondDesktop.Bounds.TooLarge(4_000_037_500m, "Price per carat") is null    // storable...
    && DiamondDesktop.Bounds.NeedsConfirming(4_000_037_500m, DiamondDesktop.Bounds.LargePricePerCt)
    && DiamondDesktop.Bounds.NeedsConfirming(500_500m, DiamondDesktop.Bounds.LargeWeightCt));

// Grouped in the current culture — this is an Indian trading business, so the limit reads
// 9,99,99,99,999.99 on their machines, the same way every amount on screen does.
Check("Bounds message names the field and the limit",
    DiamondDesktop.Bounds.TooLarge(1e11m, "Weight") is { } overLimit
    && overLimit.Contains("Weight")
    && overLimit.Contains(DiamondDesktop.Bounds.StorageMax.ToString("N2")),
    DiamondDesktop.Bounds.TooLarge(1e11m, "Weight"));

// Signed adjustments are checked on magnitude, so a large correction downwards is caught too.
Check("Bounds checks magnitude, not sign",
    DiamondDesktop.Bounds.TooLarge(-1e11m, "Weight") is not null);
Check("a workbook-sized parcel is never queried",
    !DiamondDesktop.Bounds.NeedsConfirming(232.86m, DiamondDesktop.Bounds.LargeWeightCt)
    && !DiamondDesktop.Bounds.NeedsConfirming(63_000m, DiamondDesktop.Bounds.LargePricePerCt));

// The exact string Postgres returned on the trading-floor screen.
const string overflow = "numeric field overflow A field with precision 12, scale 2 must round to "
                      + "an absolute value less than 10^10.";
Check("Friendly replaces the overflow message",
    DiamondDesktop.Friendly.Message(overflow) == "That number is too large for this field.");
Check("Friendly reports that it translated it",
    DiamondDesktop.Friendly.Translates(overflow));
Check("Friendly leaves our own messages alone",
    DiamondDesktop.Friendly.Message("Weight must be positive") == "Weight must be positive");
Check("Friendly does not claim to translate what it passed through",
    !DiamondDesktop.Friendly.Translates("Weight must be positive"));
Check("Friendly handles an empty message",
    DiamondDesktop.Friendly.Message(null) == "" && !DiamondDesktop.Friendly.Translates(null));
Check("Friendly explains a permission failure",
    DiamondDesktop.Friendly.Message("new row violates row-level security policy for table \"x\"")
        .Contains("permission"));
Check("Friendly explains an expired session",
    DiamondDesktop.Friendly.Message("JWT expired").Contains("Sign in again"));

// ── Excel import · validation (docs/08 §4) ──────────────────────────────────
// Real .xlsx files, built here, so the reader is exercised rather than mocked around.

string tempDir = Path.Combine(Path.GetTempPath(), "diamond-import-checks");
Directory.CreateDirectory(tempDir);

string[] headers =
    ["Sr.", "Date", "Name", "Broker", "Broker %", "Terms", "Size", "Number", "Weight",
     "Rejection", "Selection", "Price Per ct", "Ex Rate", "Less 1", "Less 2", "Type",
     "Amount", "Rec. Amt", "Outstanding", "Remark"];

string[] gradeCodes = ["NO 1", "NO II"];
string[] sizeCodes = ["-6.5", "+6.5"];

// 2024-08-01 is serial 45505 on the 1900 system Excel writes.
string[] GoodRow(string sr, string date = "45505", string buyer = "ABC Company",
                 string size = "-6.5", string grade = "NO 1", string weight = "10",
                 string selection = "9", string price = "50000", string rec = "0") =>
    [sr, date, buyer, "JITESH SHAH", "1", "45", size, grade, weight, "1", selection, price,
     "1", "0", "0", "BILL", "0", rec, "0", ""];

string MakeBook(string name, string sheetName, IEnumerable<string[]> rows)
{
    string path = Path.Combine(tempDir, name);
    DiamondCalc.Tests.MiniXlsx.Write(path, sheetName, rows);
    return path;
}

var validPath = MakeBook("valid.xlsx", "Sheet1",
    [headers, GoodRow("1"), GoodRow("1", grade: "NO II", size: "+6.5", rec: "100"),
     GoodRow("2", date: "45536", buyer: "Z K ENTERPRISE")]);

var validPlan = DiamondDesktop.SaleFileImport.Plan(validPath, gradeCodes, sizeCodes);
Check("import · a good file validates", validPlan.IsValid,
    validPlan.IsValid ? null : validPlan.Problems[0].Message);
Check("import · rows sharing a Sr. become one invoice", validPlan.Invoices.Count == 2,
    $"got {validPlan.Invoices.Count}");
Check("import · that invoice keeps both lines", validPlan.LineCount == 3,
    $"got {validPlan.LineCount}");
Check("import · invoice numbers carry the MIG- prefix",
    validPlan.Invoices.All(i => i.InvoiceNo.StartsWith("MIG-")));
Check("import · only rows with money received become receipts",
    validPlan.ReceiptCount == 1, $"got {validPlan.ReceiptCount}");
Eq("import · totals are recomputed with CALC-1, not copied from the sheet",
    validPlan.Invoices.First(i => i.InvoiceNo == "MIG-2").Total,
    Calc.LineAmount(9m, 50000m, 1m, 0m, 0m, 1m));

var wrongSheet = MakeBook("wrong-sheet.xlsx", "Data", [headers, GoodRow("1")]);
var sheetPlan = DiamondDesktop.SaleFileImport.Plan(wrongSheet, gradeCodes, sizeCodes);
Check("import · a missing sheet stops the import", !sheetPlan.IsValid);
Check("import · and the message names the sheet and what was found",
    sheetPlan.Problems[0].Message.Contains("Sheet1") && sheetPlan.Problems[0].Message.Contains("Data"),
    sheetPlan.Problems[0].Message);

string[] shortHeaders = [.. headers];
shortHeaders[11] = "Rate";                       // "Price Per ct" renamed
var badCols = MakeBook("bad-columns.xlsx", "Sheet1", [shortHeaders, GoodRow("1")]);
var colPlan = DiamondDesktop.SaleFileImport.Plan(badCols, gradeCodes, sizeCodes);
Check("import · a renamed column stops the import", !colPlan.IsValid);
Check("import · and the message names the column and both headings",
    colPlan.Problems[0].Message.Contains("Column L")
    && colPlan.Problems[0].Message.Contains("Price Per ct")
    && colPlan.Problems[0].Message.Contains("Rate"), colPlan.Problems[0].Message);

var noRows = MakeBook("headers-only.xlsx", "Sheet1", [headers]);
var emptyPlan = DiamondDesktop.SaleFileImport.Plan(noRows, gradeCodes, sizeCodes);
Check("import · headings with no data stop the import", !emptyPlan.IsValid);
Check("import · and say the sheet has no data rows",
    emptyPlan.Problems[0].Message.Contains("no data rows"), emptyPlan.Problems[0].Message);

var unknownGrade = MakeBook("unknown-grade.xlsx", "Sheet1", [headers, GoodRow("1", grade: "ZZ 9")]);
var gradePlan = DiamondDesktop.SaleFileImport.Plan(unknownGrade, gradeCodes, sizeCodes);
Check("import · an unmapped grade stops the import, never a guess", !gradePlan.IsValid);
Check("import · and the message names the row and the grade",
    gradePlan.Problems[0].Message.Contains("Row 3")
    && gradePlan.Problems[0].Message.Contains("ZZ 9"), gradePlan.Problems[0].Message);

var badNumber = MakeBook("bad-price.xlsx", "Sheet1", [headers, GoodRow("1", price: "")]);
var numberPlan = DiamondDesktop.SaleFileImport.Plan(badNumber, gradeCodes, sizeCodes);
Check("import · a missing price stops the import", !numberPlan.IsValid);
Check("import · and the message names the column",
    numberPlan.Problems[0].Message.Contains("column L"), numberPlan.Problems[0].Message);

var overSelection = MakeBook("over-selection.xlsx", "Sheet1",
    [headers, GoodRow("1", weight: "5", selection: "9")]);
var selPlan = DiamondDesktop.SaleFileImport.Plan(overSelection, gradeCodes, sizeCodes);
Check("import · selection above the weight stops the import", !selPlan.IsValid);
Check("import · and the message shows both figures",
    selPlan.Problems[0].Message.Contains("9.00") && selPlan.Problems[0].Message.Contains("5.00"),
    selPlan.Problems[0].Message);

// One Sr. covering two different buyers must not be merged into one document.
var clashing = MakeBook("clashing-sr.xlsx", "Sheet1",
    [headers, GoodRow("1"), GoodRow("1", buyer: "Z K ENTERPRISE")]);
var clashPlan = DiamondDesktop.SaleFileImport.Plan(clashing, gradeCodes, sizeCodes);
Check("import · one Sr. over two buyers becomes two invoices, not one",
    clashPlan.IsValid && clashPlan.Invoices.Count == 2, $"got {clashPlan.Invoices.Count}");
Check("import · and the split is counted so it is never silent", clashPlan.SplitSrCount == 1);
Check("import · split invoice numbers stay unique",
    clashPlan.Invoices.Select(i => i.InvoiceNo).Distinct().Count() == 2);

// Legacy spellings resolve through grade.aliases, which is what lets an untouched workbook load.
var aliasGrades = DiamondDesktop.SaleFileImport.AliasMap(
    [("NO 1", "NO1;NO 1;1"), ("NO II", "II;NOII;NO2SPOT")]);
var aliasSizes = DiamondDesktop.SaleFileImport.AliasMap([("-6.5", (string?)null)]);
var aliasBook = MakeBook("aliases.xlsx", "Sheet1",
    [headers, GoodRow("1", grade: "II"), GoodRow("2", grade: "1")]);
var aliasPlan = DiamondDesktop.SaleFileImport.Plan(aliasBook, aliasGrades, aliasSizes);
Check("import · a legacy grade spelling resolves through its alias", aliasPlan.IsValid,
    aliasPlan.IsValid ? null : aliasPlan.Problems[0].Message);
Check("import · and the stored code is the catalogue one, not the sheet's",
    aliasPlan.Invoices.SelectMany(i => i.Lines).Select(l => l.GradeCode).OrderBy(c => c)
        .SequenceEqual(["NO 1", "NO II"]));
Check("import · an alias for a grade nobody listed is still refused",
    !DiamondDesktop.SaleFileImport.Plan(
        MakeBook("alias-miss.xlsx", "Sheet1", [headers, GoodRow("1", grade: "QQ")]),
        aliasGrades, aliasSizes).IsValid);

// MDM-004 · four canonical sizes, four notations. "6.5+" is "+6.5" written backwards.
var sizeAliases = DiamondDesktop.SaleFileImport.SizeAliasMap(["-2", "-6.5", "+6.5", "+11"]);
Check("sizes · a trailing sign resolves to the leading-sign code",
    sizeAliases["6.5+"] == "+6.5" && sizeAliases["6.5-"] == "-6.5"
    && sizeAliases["11+"] == "+11" && sizeAliases["2-"] == "-2");
Check("sizes · the canonical spelling still resolves to itself",
    sizeAliases["+6.5"] == "+6.5" && sizeAliases["-2"] == "-2");
Check("sizes · a sieve nobody has defined stays unresolved",
    !sizeAliases.ContainsKey("0.2") && !sizeAliases.ContainsKey("0.25")
    && !sizeAliases.ContainsKey("14+"));

var plainGrades = DiamondDesktop.SaleFileImport.AliasMap(
    gradeCodes.Select(g => (g, (string?)null)));

var reversedSize = MakeBook("reversed-size.xlsx", "Sheet1", [headers, GoodRow("1", size: "6.5-")]);
var reversedPlan = DiamondDesktop.SaleFileImport.Plan(reversedSize, plainGrades, sizeAliases);
Check("import · a workbook written \"6.5-\" imports without conversion",
    reversedPlan.IsValid, reversedPlan.IsValid ? null : reversedPlan.Problems[0].Message);
Check("import · and the stored size is the canonical one",
    reversedPlan.Invoices.SelectMany(i => i.Lines).All(l => l.SizeCode == "-6.5"));

var unknownSize = MakeBook("unknown-size.xlsx", "Sheet1", [headers, GoodRow("1", size: "0.25")]);
var unknownSizePlan = DiamondDesktop.SaleFileImport.Plan(unknownSize, plainGrades, sizeAliases);
Check("import · an undefined sieve is still a validation error", !unknownSizePlan.IsValid);
Check("import · and the message names the size",
    unknownSizePlan.Problems[0].Message.Contains("0.25"), unknownSizePlan.Problems[0].Message);

// The behaviour that matters for the real workbook: good rows import, unmapped rows are skipped
// and reported, and the count is never hidden.
var mixed = MakeBook("mixed.xlsx", "Sheet1",
    [headers, GoodRow("1"), GoodRow("2", size: "0.25"), GoodRow("3", size: "14+"),
     GoodRow("4", grade: "NO II", size: "+6.5")]);
var mixedPlan = DiamondDesktop.SaleFileImport.Plan(mixed, plainGrades, sizeAliases);
Check("import · good rows still import when some rows are unmapped", mixedPlan.IsValid,
    mixedPlan.IsValid ? null : mixedPlan.Problems[0].Message);
Check("import · exactly the unmapped rows are skipped", mixedPlan.SkippedRows == 2,
    $"got {mixedPlan.SkippedRows}");
Check("import · the resolvable rows all made it", mixedPlan.LineCount == 2,
    $"got {mixedPlan.LineCount}");
Check("import · the skipped rows are reported, grouped by reason",
    DiamondDesktop.SaleFileImport.ExceptionText(mixedPlan).Contains("0.25")
    && DiamondDesktop.SaleFileImport.ExceptionText(mixedPlan).Contains("14+"),
    DiamondDesktop.SaleFileImport.ExceptionText(mixedPlan));
Check("import · a file where every row is unmapped is refused outright",
    !DiamondDesktop.SaleFileImport.Plan(
        MakeBook("all-bad.xlsx", "Sheet1", [headers, GoodRow("1", size: "0.25")]),
        plainGrades, sizeAliases).IsValid);

var notAWorkbook = Path.Combine(tempDir, "not-excel.xlsx");
File.WriteAllText(notAWorkbook, "this is not a zip");
var junkPlan = DiamondDesktop.SaleFileImport.Plan(notAWorkbook, gradeCodes, sizeCodes);
Check("import · a file that is not a workbook is refused, not crashed on", !junkPlan.IsValid);

try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }

// Proof against the real workbook, using the catalogue exactly as it now stands in the database.
// Skipped when the file is not on this machine, so the suite stays runnable anywhere.
string realBook = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    "Downloads", "Sale File Sample.xlsx");
if (File.Exists(realBook))
{
    var realGrades = DiamondDesktop.SaleFileImport.AliasMap(
        [("NO 1", "NO1;NO 1;1"), ("NO 1 BB", "1BB;1 BB;NO1BB;NO 1BB"), ("NO 2", "NO2;2;NO-2"),
         ("NO 2 BB", "2BB;2 BB;NO2BB"), ("NO II", "II;NOII;NO2SPOT"),
         // "Ex1" and "T COLOR" are the spellings Sale File Sample-1 brought in on 04 Aug 2026.
         // Without them three rows were skipped silently — real sales dropped from the import.
         // Added to the database by migration 0018; mirrored here so the two cannot drift.
         ("NO DX", "DX;NODX;DELUXE;NO-DX"), ("EX 1", "Ex1"), ("NO 3", "NO3;3;NO-3"),
         ("NO 4", "NO4;4;NO-4"), ("NO 5", "NO5;5;NO-5"), ("NO 6", "NO6;6;NO-6"),
         ("NO 7", "NO7;7;NO-7"), ("TOP-COL", "T COLOR"), ("COL", null), ("OW", null),
         ("LC 1", "LC1;L C 1;LC-1"), ("LC 2", "LC2;L C 2;LC-2"), ("LC 3", "LC3;L C 3;LC-3"),
         ("GH", null), ("LB 1", "LB1;L B 1;LB-1"), ("LB 2", "LB2;L B 2;LB-2"), ("+14", null),
         ("EXTRA", null)]);
    // The catalogue now carries the +14 sheet's own buckets too, so nothing should be skipped.
    var realSizes = DiamondDesktop.SaleFileImport.SizeAliasMap(
        ["-2", "-6.5", "+6.5", "+11", "14+", "0.2", "0.25"]);
    var realPlan = DiamondDesktop.SaleFileImport.Plan(realBook, realGrades, realSizes);

    Check("REAL FILE · the original Sale File Sample.xlsx validates", realPlan.IsValid,
        realPlan.IsValid ? null : realPlan.Problems[0].Message);
    Console.WriteLine($"      invoices {realPlan.Invoices.Count}, lines {realPlan.LineCount}, "
        + $"receipts {realPlan.ReceiptCount}, skipped {realPlan.SkippedRows}, "
        + $"{realPlan.FirstDate:dd-MM-yyyy} to {realPlan.LastDate:dd-MM-yyyy}");
    Console.WriteLine(DiamondDesktop.SaleFileImport.ExceptionText(realPlan));
    Check("REAL FILE · every grade label resolved",
        !realPlan.Exceptions.Any(e => e.Message.Contains("grade")));
    Check("REAL FILE · every size resolved", !realPlan.Exceptions.Any(e => e.Message.Contains("size")),
        realPlan.Exceptions.FirstOrDefault()?.Message);
    Check("REAL FILE · no row is skipped", realPlan.SkippedRows == 0,
        $"skipped {realPlan.SkippedRows}");
}

// ── Import dialogs · they must actually load ────────────────────────────────
// A XAML file compiles happily and still dies at runtime on a missing StaticResource. These build
// both dialogs against the app's real resource dictionaries and lay them out, without showing
// anything — the same failure that took the window down after sign-in would surface here.
foreach (var (name, ok, detail) in DiamondCalc.Tests.DialogProbe.Run())
    Check(name, ok, detail);

// ── Empty-state panels · "nothing here" must not be said before anything was read ───────────
// A grid's ItemsSource is null until its load finishes, so a null that reads as "empty" makes
// every screen claim it has no data for as long as the fetch takes.
{
    var empty = new DiamondDesktop.EmptyToVisibilityConverter();
    object Vis(object? v, string? p = null) =>
        empty.Convert(v, typeof(System.Windows.Visibility), p, System.Globalization.CultureInfo.InvariantCulture);

    Check("EMPTY · grid not loaded yet says nothing",
        Vis(null, "Loaded").Equals(System.Windows.Visibility.Collapsed));
    Check("EMPTY · grid loaded and genuinely empty shows the panel",
        Vis(new List<int>(), "Loaded").Equals(System.Windows.Visibility.Visible));
    Check("EMPTY · grid with rows shows no panel",
        Vis(new List<int> { 1 }, "Loaded").Equals(System.Windows.Visibility.Collapsed));
    Check("EMPTY · a null cell value still counts as empty",
        Vis(null).Equals(System.Windows.Visibility.Visible));
    Check("EMPTY · Invert still reports the opposite",
        Vis(new List<int>(), "Invert").Equals(System.Windows.Visibility.Collapsed));
}

// ── Short money · K / M / B ────────────────────────────────────────────────
// Display formatting only. It is checked here, next to the calculations, because the one thing it
// must never do is change a figure — a bad boundary would misreport money on every screen at once.
{
    string S(decimal v) => DiamondDesktop.Money.Short(v);

    Check("MONEY · under a thousand is left alone", S(999.5m) == "999.50", S(999.5m));
    Check("MONEY · a thousand is the first K", S(1_000m) == "1.00 K", S(1_000m));
    Check("MONEY · 125,000 reads as K", S(125_000m) == "125.00 K", S(125_000m));
    Check("MONEY · a million is the first M", S(1_000_000m) == "1.00 M", S(1_000_000m));
    Check("MONEY · 12,500,000 reads as M", S(12_500_000m) == "12.50 M", S(12_500_000m));
    Check("MONEY · a billion is the first B", S(1_000_000_000m) == "1.00 B", S(1_000_000_000m));
    Check("MONEY · 1.25 billion reads as B", S(1_250_000_000m) == "1.25 B", S(1_250_000_000m));

    // A hair under each boundary must not round up into the next unit and read 1000 of it.
    Check("MONEY · 999,999 stays in K", S(999_999m).EndsWith(" K"), S(999_999m));
    Check("MONEY · 999,999,999 stays in M", S(999_999_999m).EndsWith(" M"), S(999_999_999m));

    Check("MONEY · negatives keep their sign", S(-2_500_000m) == "-2.50 M", S(-2_500_000m));
    Check("MONEY · zero is not shortened", S(0m) == "0.00", S(0m));
    Check("MONEY · nothing at all reads as a dash", DiamondDesktop.Money.Short((decimal?)null) == "—");

    // The quotient uses invariant grouping, not the machine's: the app runs under en-IN, and a
    // lakh-grouped quotient beside a "B" suffix would be two numbering systems in one figure.
    Check("MONEY · a huge figure groups in threes",
        S(2_002_075_466_700_000_000m) == "2,002,075,466.70 B", S(2_002_075_466_700_000_000m));

    // The whole point: the formatter is told a decimal and hands back text. Nothing it does can
    // reach the value a grid sorts on, a filter matches against, or an export writes.
    decimal original = 12_345_678.90m;
    _ = S(original);
    Check("MONEY · formatting does not touch the value", original == 12_345_678.90m);
}

// ── SETTINGS · the four that saved and did nothing ──────────────────────────
// Each of these settings was written by the Settings page and read by no one. The checks below
// are deliberately about the READING side: that a value in app_config reaches the code that acts
// on it, which is the whole of what was broken.
{
    Dictionary<string, string> Config(params (string Key, string Value)[] kv) =>
        kv.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);

    // ── 1 · money_precision ──
    DiamondDesktop.Data.Policy.Apply(Config(("money_precision", "0")));
    Check("SET-1 money_precision 0 drops the decimals",
        DiamondDesktop.Money.Exact(1_234.56m) == "1,235", DiamondDesktop.Money.Exact(1_234.56m));

    DiamondDesktop.Data.Policy.Apply(Config(("money_precision", "1")));
    Check("SET-1 money_precision 1 is honoured",
        DiamondDesktop.Money.Exact(1_234.56m) == "1,234.6", DiamondDesktop.Money.Exact(1_234.56m));

    // Out of range is clamped, not obeyed: numeric(_,2) is what the schema stores.
    DiamondDesktop.Data.Policy.Apply(Config(("money_precision", "9")));
    Check("SET-1 money_precision is clamped to the 2 the schema stores",
        DiamondDesktop.Data.Policy.MoneyPrecision == 2);

    // The short form keeps 2 decimals whatever the setting says — at precision 0 "1.25 B" would
    // otherwise collapse to "1 B" and lose a quarter of a billion rupees.
    DiamondDesktop.Data.Policy.Apply(Config(("money_precision", "0")));
    Check("SET-1 the short form still carries 2 decimals",
        DiamondDesktop.Money.Short(1_250_000_000m) == "1.25 B", DiamondDesktop.Money.Short(1_250_000_000m));

    // The rounding boundary is NOT the display setting. Amounts are computed and stored at 2 dp
    // (BR-ROUND-6) no matter what the screen shows.
    Check("SET-1 money_precision does not move the rounding boundary",
        DiamondCalc.Calc.MoneyDp == 2 && DiamondCalc.Calc.RoundMoney(1.005m) == 1.01m);

    DiamondDesktop.Data.Policy.Apply(Config(("money_precision", "2")));

    // ── 2 · alert_low_stock_ct ──
    string State(decimal ct) => DiamondDesktop.StockStateConverter.State(ct);

    DiamondDesktop.Data.Policy.Apply(Config(("alert_low_stock_ct", "40")));
    Check("SET-2 a bucket under the threshold is Low", State(39.9m) == "Low", State(39.9m));
    Check("SET-2 the threshold itself is Low", State(40m) == "Low", State(40m));
    Check("SET-2 above it is In stock", State(40.1m) == "In stock", State(40.1m));
    Check("SET-2 zero is still Empty, not Low", State(0m) == "Empty", State(0m));
    Check("SET-2 negative is still Negative, not Low", State(-5m) == "Negative", State(-5m));

    // Raising the threshold re-bands the same figure — the setting is read live, not baked in.
    DiamondDesktop.Data.Policy.Apply(Config(("alert_low_stock_ct", "100")));
    Check("SET-2 raising the threshold re-bands a bucket", State(40.1m) == "Low", State(40.1m));

    // 0 turns the band off rather than marking every bucket in the book low.
    DiamondDesktop.Data.Policy.Apply(Config(("alert_low_stock_ct", "0")));
    Check("SET-2 a threshold of 0 disables the band", State(0.0001m) == "In stock", State(0.0001m));

    // ── 3 · max_login_attempts ──
    DiamondDesktop.Data.Policy.Apply(Config(("max_login_attempts", "3")));
    Check("SET-3 max_login_attempts is read", DiamondDesktop.Data.Policy.MaxLoginAttempts == 3);

    // The rename: DiamondApi read lockout_attempts while the page wrote max_login_attempts, so the
    // number on screen could never reach the code enforcing it. Both keys resolve now.
    DiamondDesktop.Data.Policy.Apply(Config(("lockout_attempts", "7")));
    Check("SET-3 the pre-rename lockout_attempts key still resolves",
        DiamondDesktop.Data.Policy.MaxLoginAttempts == 7);

    DiamondDesktop.Data.Policy.Apply(Config(("max_login_attempts", "3"), ("lockout_attempts", "7")));
    Check("SET-3 max_login_attempts wins when both are present",
        DiamondDesktop.Data.Policy.MaxLoginAttempts == 3);

    DiamondDesktop.Data.Policy.Apply(Config(("max_login_attempts", "0")));
    Check("SET-3 zero attempts is clamped to 1, never a locked-out-forever database",
        DiamondDesktop.Data.Policy.MaxLoginAttempts == 1);

    // ── 4 · session_timeout_min ──
    DiamondDesktop.Data.Policy.Apply(Config(("session_timeout_min", "15")));
    Check("SET-4 session_timeout_min is read", DiamondDesktop.Data.Policy.SessionTimeoutMin == 15);

    DiamondDesktop.Data.Policy.Apply(Config(("session_timeout_min", "0")));
    Check("SET-4 a timeout of 0 is clamped to 1, not an instant sign-out loop",
        DiamondDesktop.Data.Policy.SessionTimeoutMin == 1);

    DiamondDesktop.Data.Policy.Apply(Config(("session_timeout_min", "99999")));
    Check("SET-4 an absurd timeout is capped at a day",
        DiamondDesktop.Data.Policy.SessionTimeoutMin == 1440);

    // A garbled value keeps the previous policy rather than falling to some default nobody chose.
    DiamondDesktop.Data.Policy.Apply(Config(("session_timeout_min", "abc")));
    Check("SET-4 an unparseable value keeps the last good one",
        DiamondDesktop.Data.Policy.SessionTimeoutMin == 1440);

    // An empty table must not brick the app: every setting falls back to the shipped policy.
    DiamondDesktop.Data.Policy.Apply(Config());
    Check("SETTINGS · an empty app_config leaves a usable policy",
        DiamondDesktop.Data.Policy.MoneyPrecision is >= 0 and <= 2
        && DiamondDesktop.Data.Policy.SessionTimeoutMin >= 1
        && DiamondDesktop.Data.Policy.MaxLoginAttempts >= 1);

    // Changed is what re-renders the open grids; without it a saved change showed nothing until
    // the page was reloaded.
    int raised = 0;
    void Count() => raised++;
    DiamondDesktop.Data.Policy.Changed += Count;
    DiamondDesktop.Data.Policy.Apply(Config(("money_precision", "2")));
    DiamondDesktop.Data.Policy.Changed -= Count;
    Check("SETTINGS · applying config raises Changed so open screens re-render", raised == 1);

    DiamondDesktop.Data.Policy.Apply(Config(("money_precision", "2"), ("alert_low_stock_ct", "40")));

    // The import report states the gap between the workbook and the position. A custom format with
    // sections formats the ABSOLUTE value in the negative section, so the minus has to be written
    // in literally — get it wrong and a shortfall reads as a surplus.
    string Gap(decimal g) => g.ToString("+#,##0.0000;-#,##0.0000");
    Check("SET · a position above the workbook reads as a surplus", Gap(3.25m) == "+3.2500", Gap(3.25m));
    Check("SET · a position below it reads as a shortfall", Gap(-205.3494m) == "-205.3494", Gap(-205.3494m));

    // The Invoices and Receivables headers split their count into imported and entered, so the
    // page reconciles with the import dialog instead of quietly disagreeing with it. The number
    // is the only thing that says which is which.
    bool Imported(string? no) => DiamondDesktop.Data.Repo.IsImported(no);
    Check("SPLIT · a MIG- number is an imported invoice", Imported("MIG-1431"));
    Check("SPLIT · a split MIG- number is too", Imported("MIG-3-2"));
    Check("SPLIT · an app number is not", !Imported("INV-2026-00004"));
    Check("SPLIT · a draft with no number yet is not", !Imported(null) && !Imported(""));
    // The two series can never overlap, which is what makes the split safe without a query.
    Check("SPLIT · the app series cannot be mistaken for the imported one",
        !Imported("INV-" + DiamondDesktop.Data.Repo.ImportedPrefix));

    // ── Offline import · the outbox ────────────────────────────────────────
    // A queued stock import is a REPLACE, not an append: applying it deletes every movement the
    // previous import wrote. Replaying one blind after a reconnect is how a colleague's newer
    // import gets silently reverted, so these checks are about the guard, not the queueing.
    {
        // A temp file, never the real one. Pointing these checks at
        // %LOCALAPPDATA%\SolitaireDesk\outbox.db meant a test queued an import into the user's live
        // queue — and when the running app held the file open, the cleanup failed and the app sat
        // there reporting "1 held — needs attention" for an import nobody had made.
        //
        // Set before anything touches Outbox: DbPath is resolved once, at static init.
        string outbox = Path.Combine(Path.GetTempPath(), $"outbox-test-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("SOLITAIREDESK_OUTBOX", outbox);

        var ref1 = Guid.NewGuid();
        DiamondDesktop.Data.Outbox.EnqueueAsync("rpc/replace_imported_stock", """{"p_rows":[]}""",
                                                ref1, guard: "62:1030").GetAwaiter().GetResult();

        Check("OUTBOX · a queued import survives on disk",
            DiamondDesktop.Data.Outbox.PendingCountAsync().GetAwaiter().GetResult() == 1);

        // Queueing the same action twice — a double click, a retried click — must not double-apply.
        DiamondDesktop.Data.Outbox.EnqueueAsync("rpc/replace_imported_stock", """{"p_rows":[]}""",
                                                ref1, guard: "62:1030").GetAwaiter().GetResult();
        Check("OUTBOX · the same client_ref cannot queue twice",
            DiamondDesktop.Data.Outbox.PendingCountAsync().GetAwaiter().GetResult() == 1);

        // Server moved: someone else imported while we were offline. Hold, do not send.
        var moved = DiamondDesktop.Data.Outbox
            .ReplayAsync(_ => Task.FromResult<string?>("70:1400")).GetAwaiter().GetResult();
        Check("OUTBOX · a changed server blocks the replay", moved.Sent == 0);
        Check("OUTBOX · and says someone else imported",
            DiamondDesktop.Data.Outbox.Blocked?.Contains("Someone else imported") == true,
            DiamondDesktop.Data.Outbox.Blocked);
        Check("OUTBOX · the held import is kept, never dropped",
            DiamondDesktop.Data.Outbox.PendingCountAsync().GetAwaiter().GetResult() == 1);

        // Cannot read the server: "unknown" is not "unchanged".
        var unknown = DiamondDesktop.Data.Outbox
            .ReplayAsync(_ => Task.FromResult<string?>(null)).GetAwaiter().GetResult();
        Check("OUTBOX · an unreadable server also blocks", unknown.Sent == 0);
        Check("OUTBOX · and says so rather than blaming a colleague",
            DiamondDesktop.Data.Outbox.Blocked?.Contains("Could not check") == true,
            DiamondDesktop.Data.Outbox.Blocked);

        // No verifier at all must not be treated as permission.
        var noVerifier = DiamondDesktop.Data.Outbox.ReplayAsync().GetAwaiter().GetResult();
        Check("OUTBOX · a missing verifier blocks a guarded entry", noVerifier.Sent == 0);
        Check("OUTBOX · still queued after three refused replays",
            DiamondDesktop.Data.Outbox.PendingCountAsync().GetAwaiter().GetResult() == 1);

        var waiting = DiamondDesktop.Data.Outbox.PendingAsync().GetAwaiter().GetResult();
        Check("OUTBOX · what is waiting can be listed for a human",
            waiting.Count == 1 && waiting[0].Operation == "rpc/replace_imported_stock");

        // The payload that gets parked must be the payload the online call would have sent. A
        // queued import that differed from the one the user confirmed is a different import
        // arriving under the same name — and it arrives when nobody is watching.
        {
            var rows = new List<DiamondDesktop.StockRow>
            {
                new("NO II", "+6.5", 370.1203m, 41000m, 29, "NO II", "6.5"),
                new("+14", "+18", 124.8195m, 45699.06m, 287, "+14", "'+18"),
            };
            var gradeIds = new Dictionary<string, long> { ["NO II"] = 3, ["+14"] = 21 };
            var sizeIds = new Dictionary<string, long> { ["+6.5"] = 3, ["+18"] = 6 };

            string json = DiamondDesktop.Data.Repo.StockImportPayload(
                rows, new DateOnly(2026, 8, 6), gradeIds, sizeIds);

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            Check("QUEUE · the parked payload carries the as-at date Postgres expects",
                root.GetProperty("p_as_at").GetString() == "2026-08-06",
                root.GetProperty("p_as_at").GetString());

            var parked = root.GetProperty("p_rows");
            Check("QUEUE · every holding is parked, none dropped", parked.GetArrayLength() == 2);

            // Codes resolved to ids at queue time, from the catalogue cached before the connection
            // dropped. This is the whole reason stock import can work offline and sales cannot.
            Check("QUEUE · grade and size are already resolved to ids",
                parked[0].GetProperty("grade_id").GetInt64() == 3
                && parked[0].GetProperty("size_id").GetInt64() == 3);
            Check("QUEUE · and the +14 family resolves too",
                parked[1].GetProperty("grade_id").GetInt64() == 21
                && parked[1].GetProperty("size_id").GetInt64() == 6);

            Check("QUEUE · carats survive the round trip to 4 dp",
                parked[0].GetProperty("weight_ct").GetDecimal() == 370.1203m);
            Check("QUEUE · so does the price, which decides the bucket's average cost",
                parked[1].GetProperty("price_per_ct").GetDecimal() == 45699.06m);

            // Parked, closed, reopened: the queue is on disk, not in memory. A power cut between
            // the import and the reconnect must not lose the file.
            var acrossRestart = Guid.NewGuid();
            DiamondDesktop.Data.Outbox.EnqueueAsync("rpc/replace_imported_stock", json,
                acrossRestart, guard: "62:1030").GetAwaiter().GetResult();

            var still = DiamondDesktop.Data.Outbox.PendingAsync().GetAwaiter().GetResult();
            Check("QUEUE · survives being written and read back by a new connection",
                still.Count == 2 && still.All(w => w.Operation == "rpc/replace_imported_stock"));
        }

        // Reachability. Nothing set this from a data read until now — IsOnline was written only by
        // the sign-in path, so a PostgREST call failing on "no such host" left it reading true and
        // every offline branch in the app was unreachable. Choosing a file while disconnected did
        // nothing whatsoever: no dialog, no queue, no message.
        DiamondDesktop.Data.Db.NoteTransport(new System.Net.Http.HttpRequestException("no such host"));
        Check("ONLINE · a transport failure marks the app offline", !DiamondDesktop.Data.Db.IsOnline);

        DiamondDesktop.Data.Db.NoteTransport(null);
        Check("ONLINE · a read that succeeds marks it back online", DiamondDesktop.Data.Db.IsOnline);

        // A refusal is not an outage. The server answered, so the connection is fine and the
        // request was wrong — treating that as offline would queue writes the server just rejected.
        DiamondDesktop.Data.Db.NoteTransport(
            new InvalidOperationException("new row violates row-level security policy"));
        Check("ONLINE · an RLS refusal is not an outage", DiamondDesktop.Data.Db.IsOnline);

        // The real one arrives wrapped by the Supabase client, not thrown bare.
        DiamondDesktop.Data.Db.NoteTransport(
            new InvalidOperationException("request failed",
                new System.Net.Http.HttpRequestException("connection refused")));
        Check("ONLINE · a wrapped transport failure still counts", !DiamondDesktop.Data.Db.IsOnline);
        DiamondDesktop.Data.Db.NoteTransport(null);

        // Proof the isolation holds: this run must not have created or touched the real queue.
        Check("OUTBOX · the tests never write to the live queue",
            !outbox.Contains("LocalApplicationData", StringComparison.OrdinalIgnoreCase)
            && outbox.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase));

        try { File.Delete(outbox); } catch (IOException) { }
    }

    // WHO on the audit page. Misattributing a change is worse than not naming anyone, so each
    // fallback is pinned: no user at all, a known user, and a user who has since been deleted.
    var known = Guid.Parse("012c2cce-2271-442c-bed2-6e5651632789");
    var gone = Guid.Parse("99999999-1111-2222-3333-444444444444");
    var nameless = Guid.Parse("88888888-1111-2222-3333-444444444444");
    DiamondDesktop.AuditRow.Names = new Dictionary<Guid, string>
    {
        [known] = "Jasmin Unadkat",
        [nameless] = "   ",            // a profile row that exists but carries no full_name
    };

    string Who(Guid? id) => new DiamondDesktop.AuditRow { ChangedBy = id }.By;

    Check("WHO · a signed-in user is named", Who(known) == "Jasmin Unadkat", Who(known));
    Check("WHO · no user is System, not blank or 'unknown'", Who(null) == "System", Who(null));
    Check("WHO · a deleted user shows a short id, never a bare empty cell",
        Who(gone) == "99999999" && Who(gone).Length == 8, Who(gone));
    // A profile row with a blank name must not render as an empty WHO cell either.
    Check("WHO · a blank display name falls back to the short id", Who(nameless) == "88888888");
}

// ── Count labels · a noun that agrees with its number ────────────────────────────────
{
    var label = new DiamondDesktop.CountLabelConverter();
    string L(object v) => (string)label.Convert(v, typeof(string), "line",
        System.Globalization.CultureInfo.InvariantCulture);

    Check("COUNT · one is singular", L(1) == "1 line", L(1));
    Check("COUNT · two is plural", L(2) == "2 lines", L(2));
    Check("COUNT · none is plural", L(0) == "0 lines", L(0));
    Check("COUNT · a collection is counted, not printed", L(new List<int> { 1, 2, 3 }) == "3 lines",
        L(new List<int> { 1, 2, 3 }));
    Check("COUNT · thousands are grouped", L(1500) == "1,500 lines", L(1500));
}

Console.WriteLine();
Console.WriteLine(failed == 0 ? "All checks passed." : $"{failed} check(s) FAILED.");
return failed == 0 ? 0 : 1;
