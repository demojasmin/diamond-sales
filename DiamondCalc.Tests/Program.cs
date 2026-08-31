using System.IO;
using DiamondCalc;
using Microsoft.EntityFrameworkCore;

// One runnable check per CALC rule (docs/05-backlog.md §4 "definition of done", item 1).
// No test framework on purpose: `dotnet run --project DiamondCalc.Tests` is the whole harness.
// Figures marked [file] are cached values read out of the real workbooks (docs/04).

// `dotnet run --project DiamondCalc.Tests -- sample-pdf <path>` writes the one stock sheet no
// client has sent us: unnamed rows carrying real carats. Before Unknown Grade existed the whole
// file was refused over them, and there was no way to try that by hand.
if (args is ["sample-pdf", var samplePath, ..])
{
    // An optional second argument renames the sheet's LB 2 row. Pass a name the catalogue does
    // not have and the import offers to add it, which is the only way to exercise that dialog
    // without editing the catalogue by hand first.
    string swap = args.Length > 2 ? args[2] : "LB 2";
    var sheet = DiamondCalc.Tests.MiniPdf.UnnamedRowSheet
        .Select(c => c.Text == "LB 2" ? (swap, c.CentreX, c.Y) : c);

    DiamondCalc.Tests.MiniPdf.Write(samplePath, sheet);
    Console.WriteLine($"Wrote {samplePath} - 8 holdings, 11.00 ct, two rows with no grade name"
                      + (swap == "LB 2" ? "." : $", and a grade called \"{swap}\"."));
    return 0;
}

// `dotnet run --project DiamondCalc.Tests -- read-pdf <path>` runs one sheet through the real
// reader and prints what it made of it. For checking a sheet before it is sent, without the
// round trip of installing, signing in and importing.
if (args is ["read-pdf", var readPath])
{
    var cat = new[]
    {
        ("NO 1 BB", "1BB;1 BB"), ("NO II", "II;#"), ("EX 1", "EX1;Ex1"), ("NO 2", "2;NO2"),
        ("NO DX", "DX;Dx;DX1"), ("NO 3", "3"), ("NO 4", "4"), ("NO 5", "5"), ("NO 6", "6"),
        ("NO 7", "7"), ("TOP-COL", "TOP co"), ("COL", "color"), ("OW", null), ("GH", "GH VS"),
        ("LC 1", null), ("LC 2", null), ("LB 1", null), ("LB 2", null), ("FL", null),
        ("1MB", "1 MB"), ("-2 MB", null), ("EXTRA", null),
        (DiamondDesktop.PdfStockFile.UnknownGrade, null),
    }
    .Select((g, i) => new DiamondDesktop.Data.Grade
    {
        GradeId = i + 1, Code = g.Item1, Aliases = g.Item2, DisplayName = g.Item1,
    })
    .ToList();

    var read = DiamondDesktop.PdfStockFile.Plan(
        readPath,
        DiamondDesktop.MainWindow.PdfGradeLabelMap(cat),
        DiamondDesktop.MainWindow.PdfSizeLabelMap(["-6.5", "+6.5", "+11", "0.2", "0.25", "1/6"]));

    Console.WriteLine($"{Path.GetFileName(readPath)}: {(read.IsValid ? "READS" : "REFUSED")}");
    Console.WriteLine($"  {read.Rows.Count} holding(s), {read.TotalCarats:N2} ct");
    Console.WriteLine($"  sizes: {string.Join(", ", read.SizeOrder)}");
    Console.WriteLine($"  grades: {string.Join(", ", read.GradeOrder)}");
    foreach (string size in read.SizeOrder)
        Console.WriteLine($"    {size,-6} {read.Rows.Where(r => r.SizeCode == size).Sum(r => r.WeightCt),8:N2} ct");
    if (!read.IsValid) Console.WriteLine(DiamondDesktop.PdfStockFile.ProblemText(read));
    return read.IsValid ? 0 : 1;
}

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

// ── CALC-AGE · the three-way ageing contract ────────────────────────────────
// The same nine inputs are asserted in Android's AgeingBoundaryTest and in
// supabase/VERIFY_AGEING.sql. The rule is written out three times by design
// (CALC-001); this shared table is the only thing that stops the three drifting.
// Each value is a boundary or one step off it — a test over 15, 45 and 120 would
// agree with every wrong implementation of this rule.
foreach (var (days, band) in new (int, string)[]
{
    (-1, "not due"), (0, "not due"), (1, "1-30"), (30, "1-30"),
    (31, "31-60"), (60, "31-60"), (61, "61-90"), (90, "61-90"), (91, "90+"),
})
    Check($"CALC-AGE {days} days past due is '{band}'", Calc.AgeBucket(days) == band);

// Fifty paise, the shared settled threshold. This engine used `> 0`, the server
// views `> 0.01` and Android 0.50 — so an invoice owing forty paise was overdue
// here, in the receivables book on the server, and settled on the phone.
Eq("CALC-10 the settled threshold is fifty paise", Calc.SettledBelow, 0.50m);

Check("CALC-10 forty paise outstanding is rounding dust, not a debt",
    !Calc.IsOverdue(new DateOnly(2025, 12, 1), 0.40m, new DateOnly(2026, 7, 25)));

Check("CALC-10 fifty-one paise is still a debt",
    Calc.IsOverdue(new DateOnly(2025, 12, 1), 0.51m, new DateOnly(2026, 7, 25)));

// Which side of the boundary fifty paise itself falls on -- the tenth input of the
// three-way table. Android tested `< 0.5` and so called this one open while this
// engine and the views called it closed. `> 0.50` here, `> 0.50` in
// v_invoice.is_overdue and v_receivables_ageing, `<= 0.50` in Calc.isSettled.
Check("CALC-10 fifty paise exactly is settled, not owed",
    !Calc.IsOverdue(new DateOnly(2025, 12, 1), 0.50m, new DateOnly(2026, 7, 25)));

// ── a database refusal has to be readable ──────────────────────────────────
// PostgREST reports a failure as a JSON object, not a sentence, so the status bar was
// printing the envelope -- {"code":"23514","details":null,... -- and cutting the actual
// explanation off part-way. Below is the real message 0038 raises, in the real envelope.
{
    const string envelope =
        "{\"code\":\"23514\",\"details\":null,\"hint\":null,\"message\":" +
        "\"This sheet cannot replace the current stock: 3 bucket(s) would be left below zero.\\n\\n" +
        "  - NO II x -6.5: the sheet brings 5.5 ct but 114.18 ct has gone out against it, leaving -108.68 ct\\n" +
        "  - COL x +6.5: the sheet brings 0.86 ct but 31.2 ct has gone out against it, leaving -30.34 ct\\n\\n" +
        "Nothing has been imported.\"}";

    string shown = DiamondDesktop.Friendly.Message(envelope);

    Check("REFUSAL · the JSON envelope is opened",
        !shown.StartsWith("{") && !shown.Contains("\"code\""),
        shown[..Math.Min(58, shown.Length)]);

    Check("REFUSAL · the sentence survives",
        shown.StartsWith("This sheet cannot replace the current stock"),
        shown[..Math.Min(58, shown.Length)]);

    // The half that matters most. Showing one bucket and silently dropping two is worse than
    // showing none, because a partial list reads as a complete one.
    Check("REFUSAL · every named bucket survives",
        shown.Contains("NO II x -6.5") && shown.Contains("COL x +6.5"), $"{shown.Length} chars");

    Check("REFUSAL · and the carats behind each one",
        shown.Contains("114.18") && shown.Contains("-108.68")
        && shown.Contains("31.2") && shown.Contains("-30.34"));

    Check("REFUSAL · the closing explanation survives",
        shown.Contains("Nothing has been imported"));

    // The bar is one line and does not wrap, so it flattens -- but flattening may not DROP
    // anything, which is exactly what showing the first line only would have done.
    string oneLine = System.Text.RegularExpressions.Regex
        .Replace(shown, "[\r\n]+", "  ·  ").Trim();
    Check("REFUSAL · flattened for the status bar, nothing dropped",
        oneLine.Contains("NO II x -6.5") && oneLine.Contains("COL x +6.5")
        && !oneLine.Contains('\n'), oneLine[..Math.Min(66, oneLine.Length)]);

    // The dialog splits that same text into headline / bullets / note, by the rule the window
    // uses: a line starting "- " is a bucket, anything else is prose.
    var refusalLines = shown.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    var refusalBullets = refusalLines.Where(l => l.StartsWith("- ", StringComparison.Ordinal)).ToList();
    var refusalProse = refusalLines.Where(l => !l.StartsWith("- ", StringComparison.Ordinal)).ToList();

    Check("REFUSAL · the dialog finds one bullet per bucket",
        refusalBullets.Count == 2, string.Join(" | ", refusalBullets));
    Check("REFUSAL · and a headline plus a closing note",
        refusalProse.Count == 2, string.Join(" | ", refusalProse));

    // Everything the app writes itself must pass through untouched.
    Check("REFUSAL · a plain message is left alone",
        DiamondDesktop.Friendly.Message("Pick a buyer from the list") == "Pick a buyer from the list");
    Check("REFUSAL · and so is text that merely starts with a brace",
        DiamondDesktop.Friendly.Message("{not json") == "{not json");
}

// ── "Show zero values" on the stock report ─────────────────────────────────
// A bucket nobody has traded is left blank by default: a zero reads as "measured and found to
// be nothing" rather than "nothing here", and 27 grades by 11 sizes is 297 cells to bury 67
// real figures in. Some clients' sheets print 0.00 in every cell all the same, and holding the
// screen against one of those is easier when the two agree -- so it is a toggle, not a rule.
{
    Check("ZEROS · off · an untraded bucket is left blank",
        DiamondDesktop.MainWindow.ReportFigure(0m, false) == "",
        $"\"{DiamondDesktop.MainWindow.ReportFigure(0m, false)}\"");

    Check("ZEROS · on · the same bucket prints 0.00",
        DiamondDesktop.MainWindow.ReportFigure(0m, true) == "0.00",
        DiamondDesktop.MainWindow.ReportFigure(0m, true));

    // A figure that is not zero must read the same either way -- the toggle governs empties
    // only, and a balance changing format with a checkbox would be its own bug.
    foreach (decimal v in new[] { 4.87m, 0.14m, 1478.38m, 0.005m })
    {
        Check($"ZEROS · {v:N2} reads the same in both states",
            DiamondDesktop.MainWindow.ReportFigure(v, false)
            == DiamondDesktop.MainWindow.ReportFigure(v, true),
            DiamondDesktop.MainWindow.ReportFigure(v, false));
    }

    Check("ZEROS · and to two places, as the sheet prints it",
        DiamondDesktop.MainWindow.ReportFigure(4.87m, false) == "4.87"
        && DiamondDesktop.MainWindow.ReportFigure(1478.38m, true) == "1,478.38",
        DiamondDesktop.MainWindow.ReportFigure(1478.38m, true));

    // A negative balance is stock left that never arrived -- it must show in BOTH states,
    // because hiding it is how -94.34 ct went unnoticed for a day.
    Check("ZEROS · a negative balance is never hidden by either state",
        DiamondDesktop.MainWindow.ReportFigure(-18.30m, false) == "-18.30"
        && DiamondDesktop.MainWindow.ReportFigure(-18.30m, true) == "-18.30",
        DiamondDesktop.MainWindow.ReportFigure(-18.30m, false));
}

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
// A new invoice opens with NO rows -- the grid holds only the lines you asked for -- so the
// first line is added here rather than assumed.
inv.Lines.Add(new DiamondDesktop.SaleLine());
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

// A retired sieve (0035: 14+ holds 281.99 ct in Priya and 28.00 ct in Demo, so it is switched
// off rather than deleted). It must vanish from everything that creates new work, and stay
// everywhere history is read — the stock is still there, and figures nobody can see are not
// preserved figures.
{
    var retired = new DiamondDesktop.Data.SizeBucket
    { SizeId = 98, Code = "14+", SortOrder = 98, Active = false };
    DiamondDesktop.Catalogue.AllSizes.Add(retired);

    Check("SIZE · a retired sieve stays in AllSizes so history still resolves",
        DiamondDesktop.Catalogue.AllSizes.Contains(retired));
    Check("SIZE · but never reaches a picker",
        !DiamondDesktop.Catalogue.ActiveSizes.Contains(retired),
        string.Join(",", DiamondDesktop.Catalogue.ActiveSizes.Select(s => s.Code)));
    Check("SIZE · nor the sales grid, with no grade picked",
        !DiamondDesktop.Catalogue.SizesFor(null).Contains(retired));

    // grade_size pairings are deliberately left alone by 0035, so the pairing check would still
    // offer this size. Retirement has to win over the pairing, not read through it.
    DiamondDesktop.Catalogue.SetGradeSizes(
        [.. DiamondDesktop.Catalogue.AllSizes.Select(
            s => new DiamondDesktop.Data.GradeSize { GradeId = GradeOf("NO II").GradeId, SizeId = s.SizeId })]);
    Check("SIZE · a retired sieve still paired to a grade is still refused",
        !DiamondDesktop.Catalogue.SizesFor(GradeOf("NO II")).Contains(retired));

    Check("SIZE · and is reported as retired, not as import-only",
        DiamondDesktop.Catalogue.IsRetiredSize("14+")
        && !DiamondDesktop.Catalogue.IsSellableSize("14+"));

    // A file printing the OTHER notation of a retired sieve must be recognised as the same one.
    // Without this the popup offers to ADD a size the database will only ever restore, and it
    // describes the wrong act to the person deciding.
    Check("SIZE · a retired sieve is recognised through its other notation",
        DiamondDesktop.Catalogue.IsRetiredSize("+14"),
        $"14+ retired, +14 recognised = {DiamondDesktop.Catalogue.IsRetiredSize("+14")}");
    Check("SIZE · and a live sieve is never mistaken for a retired one",
        !DiamondDesktop.Catalogue.IsRetiredSize("6.5+")
        && !DiamondDesktop.Catalogue.IsRetiredSize("+6.5"));

    // The stock report resolves sizes by CODE, not through a picker, so it is the one screen
    // that can still reach a retired sieve — deliberately, because its stock is real and must
    // stay on the report. Reading it is fine; adjusting it is a new movement and is not.
    var found = DiamondDesktop.Catalogue.AllSizes.FirstOrDefault(z => z.Code == "14+");
    Check("SIZE · the report can still resolve a retired sieve to show its stock",
        found is not null);
    Check("SIZE · but the code path that adjusts it sees it as closed",
        found is { Active: false });

    DiamondDesktop.Catalogue.AllSizes.Remove(retired);
    Check("SIZE · restoring it puts it back in the pickers",
        DiamondDesktop.Catalogue.AllSizes.All(s => s.Active)
        && DiamondDesktop.Catalogue.ActiveSizes.Count == DiamondDesktop.Catalogue.AllSizes.Count);

    // Put the live pairings back — every check below this block reads them.
    DiamondDesktop.Catalogue.SetGradeSizes(
        from g in DiamondDesktop.Catalogue.Grades
        from s in DiamondDesktop.Catalogue.AllSizes
        where s.Code != "-2" || g.Code is "NO 1" or "NO 1 BB"
        select new DiamondDesktop.Data.GradeSize { GradeId = g.GradeId, SizeId = s.SizeId });
}
// ── A sieve the file names and the catalogue lacks ──────────────────────────
//
// One rule decides this for all three importers, and it has to be the SAME rule the database
// uses in public.sieve_key — that function is what makes add_size return an existing row rather
// than create a twin. A C# rule that disagreed would either offer to add a sieve the database
// then refuses, or agree to add one it happily twins.
{
    string? K(string s) => DiamondDesktop.StockFileImport.SizeKey(s);

    Check("SIZEKEY · the sign may sit on either end",
        K("6.5+") == K("+6.5") && K("6.5-") == K("-6.5")
        && K("11+") == K("+11") && K("2-") == K("-2"));

    Check("SIZEKEY · an unsigned size is the positive bucket",
        K("6.5") == K("+6.5") && K("11") == K("+11"));

    // The half that was missing entirely: 1/5 IS 0.2, by division, not by a lookup table.
    Check("SIZEKEY · a fraction resolves to the decimal bucket",
        K("1/5") == K("0.2") && K("1/4") == K("0.25"), $"1/5={K("1/5")} 0.2={K("0.2")}");
    Check("SIZEKEY · and one that does not divide evenly keeps four places",
        K("1/6") == "+0.1667" && K("1/3") == "+0.3333", $"{K("1/6")} {K("1/3")}");
    Check("SIZEKEY · 1/6 and 1/3 are not the same sieve", K("1/6") != K("1/3"));

    // "6.50" and "6.5" are one sieve. decimal keeps the scale it was parsed with, so without
    // trimming these key apart and the catalogue's own code stops matching the sheet's.
    Check("SIZEKEY · trailing zeros are notation, not precision",
        K("6.50") == K("6.5") && K("+11.00") == K("+11"), $"{K("6.50")} vs {K("6.5")}");

    Check("SIZEKEY · a word is not a size", K("Weight") is null && K("TOTAL") is null);
    Check("SIZEKEY · and neither is a division by zero", K("1/0") is null);
    // The workbooks really do print these. The database agreed with none of it before 0037,
    // so add_size would have stored a sieve literally coded "'+18" whose key matched nothing —
    // and the next sheet writing "+18" would have made a second row for the same sieve.
    Check("SIZEKEY · Excel's text-forcing apostrophe is ignored", K("'+18") == K("+18"));
    Check("SIZEKEY · so is a leading comma", K(",-2") == K("-2"));
    Check("SIZEKEY · and a run of them, not just one",
        K(",,+18") == K("+18") && K("', +18") == K("+18"), $"{K(",,+18")} {K("', +18")}");

    Check("SIZECODE · what gets STORED loses the spreadsheet's punctuation",
        DiamondDesktop.StockFileImport.CleanCode("'+18") == "+18"
        && DiamondDesktop.StockFileImport.CleanCode(",-2") == "-2",
        DiamondDesktop.StockFileImport.CleanCode("'+18"));
    Check("SIZECODE · but a clean label is left exactly as printed",
        DiamondDesktop.StockFileImport.CleanCode("1/6") == "1/6"
        && DiamondDesktop.StockFileImport.CleanCode("6.5+") == "6.5+");

    // The catalogue side of the same rule: every notation the sheets use must find its row.
    var catalogue = DiamondDesktop.StockFileImport.SizeMap(["-2", "+6.5", "+11", "0.2", "0.25", "1/6"]);
    foreach (var (printed, expected) in new[]
             { ("2-", "-2"), ("6.5+", "+6.5"), ("11+", "+11"),
               ("1/5", "0.2"), ("1/4", "0.25"), ("1/6", "1/6"), ("6.50", "+6.5") })
        Check($"SIZEMAP · \"{printed}\" resolves to the existing \"{expected}\"",
            DiamondDesktop.StockFileImport.SizeKey(printed) is { } k
            && catalogue.TryGetValue(k, out string? got) && got == expected,
            DiamondDesktop.StockFileImport.SizeKey(printed) is { } k2
            && catalogue.TryGetValue(k2, out string? g2) ? g2 : "unresolved");

    Check("SIZEMAP · a sieve genuinely absent stays unresolved",
        DiamondDesktop.StockFileImport.SizeKey("+23") is { } miss && !catalogue.ContainsKey(miss));

    // The eight labels the office actually writes, against the catalogue as it stands. Every one
    // must land on a row that ALREADY EXISTS — none may be offered as new, because offering one
    // is how a twin gets created. Spelled out as a set rather than folded into the loop above so
    // that if the office adds a ninth, the gap is a failing line and not a silent omission.
    string[] asked = ["2-", "6.5-", "6.5+", "11+", "1/6", "1/5", "1/4", "1/3"];
    var live = DiamondDesktop.StockFileImport.SizeMap(
        ["-2", "-6.5", "+6.5", "+11", "0.2", "0.25", "1/6", "1/3"]);

    foreach (string label in asked)
        Check($"ASKED · \"{label}\" resolves to an existing sieve, never a new one",
            DiamondDesktop.StockFileImport.SizeKey(label) is { } k && live.ContainsKey(k),
            DiamondDesktop.StockFileImport.SizeKey(label) is { } k2 && live.TryGetValue(k2, out string? r)
                ? r : "WOULD BE OFFERED AS NEW");

    // Eight labels, eight different sieves — six of them written the other way round from the
    // catalogue's own spelling, and two (1/6, 1/3) spelled identically. None is a synonym of
    // another, so a collision here would mean sieve_key is folding two real sieves into one.
    Check("ASKED · the eight name eight distinct sieves, none folded together",
        asked.Select(DiamondDesktop.StockFileImport.SizeKey).Distinct().Count() == 8
        && asked.Select(a => live[DiamondDesktop.StockFileImport.SizeKey(a)!]).Distinct().Count() == 8,
        string.Join(",", asked.Select(a => live[DiamondDesktop.StockFileImport.SizeKey(a)!])));

    // The same eight with a spreadsheet's punctuation in front, which is how they arrive.
    foreach (string label in asked)
        Check($"ASKED · and still does when Excel writes it as \"'{label}\"",
            DiamondDesktop.StockFileImport.SizeKey("'" + label) is { } k && live.ContainsKey(k),
            DiamondDesktop.StockFileImport.SizeKey("'" + label) ?? "unparsed");
}

// The offer itself: what gets shown, and — the costly half — what does NOT get shown twice.
{
    var found = new List<DiamondDesktop.UnknownSize>();
    DiamondDesktop.StockFileImport.NoteUnknownSize(found, "1/5", 10m);
    DiamondDesktop.StockFileImport.NoteUnknownSize(found, "0.2", 5m);
    Check("UNKNOWN · two spellings of one missing sieve are offered ONCE",
        found.Count == 1, string.Join(",", found.Select(u => u.Label)));
    Check("UNKNOWN · under the spelling the file printed first",
        found[0].Label == "1/5", found[0].Label);
    Check("UNKNOWN · with the weight of both rows behind it",
        found[0].Carats == 15m, $"{found[0].Carats}");

    DiamondDesktop.StockFileImport.NoteUnknownSize(found, "+23", 2m);
    Check("UNKNOWN · a genuinely different sieve is offered separately",
        found.Count == 2 && found[1].Label == "+23");

    Check("UNKNOWN · nothing is offered when the file names nothing new",
        new List<DiamondDesktop.UnknownSize>().Count == 0);
}

// The sales workbook resolves sizes through its own map. It must agree with the stock side, or
// the same file would import one way as stock and another as sales.
{
    var sale = DiamondDesktop.SaleFileImport.SizeAliasMap(["-2", "+6.5", "+11", "0.2", "1/6"]);
    Check("SALESMAP · the literal spellings still resolve exactly as before",
        sale["+6.5"] == "+6.5" && sale["6.5+"] == "+6.5" && sale["2-"] == "-2");
    Check("SALESMAP · and a fraction now finds its decimal bucket",
        DiamondDesktop.StockFileImport.SizeKey("1/5") is { } k && sale.TryGetValue(k, out string? v)
        && v == "0.2", "1/5");
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

// ── PDF stock sheet · a row the sheet printed no grade against ───────────────────────
//
// It used to REFUSE the whole file. Now it imports under "Unknown Grade", and the point of
// these checks is the one word in that sentence that matters: nothing is LOST. Every printed
// holding reaches plan.Rows with its own size, weight and rate, and the sheet's own subtotals
// reconcile with the unnamed rows counted in.
//
// The real client sheets are not in the repository and every one of them prints 0.00 on its
// unnamed rows, so the case was untestable until MiniPdf. This sheet is built to carry weight
// there — twice, in two different columns, on two separate rows.
{
    var grades = new (string Code, string? Aliases)[]
    {
        ("NO 1 BB", "1BB;1 BB"), ("LB 2", "LB2"), (DiamondDesktop.PdfStockFile.UnknownGrade, null),
    }
    .Select((g, i) => new DiamondDesktop.Data.Grade
    {
        GradeId = i + 1, Code = g.Code, Aliases = g.Aliases, DisplayName = g.Code,
    })
    .ToList();

    var sizeMap = DiamondDesktop.MainWindow.PdfSizeLabelMap(["-6.5", "+6.5"]);

    // The sheet itself lives in MiniPdf, so `sample-pdf` hands the desk the same one these
    // checks read.
    var sheet = DiamondCalc.Tests.MiniPdf.UnnamedRowSheet;

    string pdf = Path.Combine(Path.GetTempPath(), "unnamed-row-stock-sheet.pdf");
    DiamondCalc.Tests.MiniPdf.Write(pdf, sheet);

    try
    {
        var plan = DiamondDesktop.PdfStockFile.Plan(
            pdf, DiamondDesktop.MainWindow.PdfGradeLabelMap(grades), sizeMap);

        string why = DiamondDesktop.PdfStockFile.ProblemText(plan);
        Check("PDF-UNNAMED · the sheet reads instead of being refused", plan.IsValid, why);

        // The whole claim, in one line: eight printed holdings, eight imported.
        Check("PDF-UNNAMED · every printed holding is imported, none dropped",
            plan.Rows.Count == 8, $"{plan.Rows.Count} of 8");

        Eq("PDF-UNNAMED · and they total what the sheet's TOTAL line prints", plan.TotalCarats, 11.00m);
        Eq("PDF-UNNAMED · nothing is left unplaced", plan.UnplacedCarats, 0m);

        var unknown = plan.Rows
            .Where(r => r.GradeCode == DiamondDesktop.PdfStockFile.UnknownGrade)
            .ToList();

        Check("PDF-UNNAMED · both blank-name rows land under Unknown Grade",
            unknown.Count == 4, $"{unknown.Count} of 4 cells");

        // Size, carats AND rate — a row imported under the right grade with the wrong figures is
        // still a lost row, and the rate is the one the old code never got as far as reading.
        var small = unknown.FirstOrDefault(r => r.SizeCode == "-6.5" && r.WeightCt == 1.50m);
        var big = unknown.FirstOrDefault(r => r.SizeCode == "+6.5" && r.WeightCt == 0.75m);

        Check("PDF-UNNAMED · with the size and carats the sheet printed", small is not null && big is not null,
            string.Join(" ", unknown.Select(r => $"{r.SizeCode}={r.WeightCt:N2}")));
        Check("PDF-UNNAMED · and the rate beside them, not a zero",
            small?.PricePerCt == 25000m && big?.PricePerCt == 22000m,
            $"{small?.PricePerCt} / {big?.PricePerCt}");

        // Both subtotals, because a row placed under the wrong column reconciles to neither.
        Eq("PDF-UNNAMED · the -6.5 column totals the 6.75 ct the sheet states",
            plan.Rows.Where(r => r.SizeCode == "-6.5").Sum(r => r.WeightCt), 6.75m);
        Eq("PDF-UNNAMED · the +6.5 column totals the 4.25 ct the sheet states",
            plan.Rows.Where(r => r.SizeCode == "+6.5").Sum(r => r.WeightCt), 4.25m);

        // What the preview draws. Two unnamed rows must stay two rows on screen — sharing one
        // label would collide in Printed and the second would overwrite the first.
        Check("PDF-UNNAMED · the preview lists all four printed rows",
            plan.GradeOrder.Count == 4, string.Join(" | ", plan.GradeOrder));
        Check("PDF-UNNAMED · the unnamed ones shown as Unknown Grade, told apart by a number",
            plan.GradeOrder.Contains("Unknown Grade")
            && plan.GradeOrder.Contains("Unknown Grade (2)"),
            string.Join(" | ", plan.GradeOrder));
        Check("PDF-UNNAMED · each with its own printed cell, neither overwriting the other",
            plan.Printed[("Unknown Grade", "-6.5")] == (1.50m, 25000m)
            && plan.Printed[("Unknown Grade (2)", "-6.5")] == (0.25m, 12000m),
            $"{plan.Printed[("Unknown Grade", "-6.5")]} / {plan.Printed[("Unknown Grade (2)", "-6.5")]}");

        // A database that has not had 0039 applied. The file is still refused rather than
        // imported short -- but the refusal names the ONE grade to add, not one per blank row,
        // which is what the app's "add it and carry on" dialog offers.
        var without = grades.Where(g => g.Code != DiamondDesktop.PdfStockFile.UnknownGrade).ToList();
        var stale = DiamondDesktop.PdfStockFile.Plan(
            pdf, DiamondDesktop.MainWindow.PdfGradeLabelMap(without), sizeMap);

        Check("PDF-UNNAMED · without the grade in the catalogue the file is refused, not imported short",
            !stale.IsValid);
        Check("PDF-UNNAMED · and one grade is offered, not one per blank row",
            stale.UnplacedLabels.Count == 1
            && stale.UnplacedLabels[0] == DiamondDesktop.PdfStockFile.UnknownGrade,
            string.Join(", ", stale.UnplacedLabels));
        Eq("PDF-UNNAMED · the carats it names are the blank rows' own", stale.UnplacedCarats, 3.00m);
        Check("PDF-UNNAMED · reported as a catalogue to fill in, not a sheet that cannot be read",
            !DiamondDesktop.PdfStockFile.ProblemText(stale).Contains("could not be read reliably"),
            DiamondDesktop.PdfStockFile.ProblemText(stale));
    }
    finally
    {
        try { File.Delete(pdf); } catch (IOException) { }
    }
}

// ---- Grade marks - one vocabulary, and it is the printed sheet's ---------------------
//
// The app used to show three different names for one grade: the code on the Stock table, the
// display name in every picker, and the mark on the Stock report. Somebody holding the paper
// against the screen had to translate. These pin the one that won.
{
    string Short(string code) => DiamondDesktop.Data.GradeNames.Short(code);

    Check("MARK - a grade whose mark differs from its code uses the mark",
        Short("NO II") == "#" && Short("NO 1 BB") == "1BB" && Short("TOP-COL") == "TOP co"
        && Short("COL") == "color" && Short("NO DX") == "DX1",
        $"{Short("NO II")} {Short("NO 1 BB")} {Short("TOP-COL")}");

    Check("MARK - a grade the sheet prints as its own code is left alone",
        Short("OW") == "OW" && Short("GH") == "GH" && Short("LC 1") == "LC 1"
        && Short("FL") == "FL" && Short("Unknown Grade") == "Unknown Grade",
        Short("Unknown Grade"));

    // A picker entry with no text is a row nobody can pick, and the model binds straight to this.
    Check("MARK - never blank, whatever the catalogue holds",
        Short("") == "" && Short(null) == ""
        && new DiamondDesktop.Data.Grade { Code = "NO 5" }.ShortName == "5",
        new DiamondDesktop.Data.Grade { Code = "NO 5" }.ShortName);

    // The PDF reader matches sheets on these same marks. Moving the map out of MainWindow must
    // not have cost it one of them.
    var map = DiamondDesktop.MainWindow.PdfGradeLabelMap(
        new[] { "NO II", "NO 1 BB", "TOP-COL", "COL", "NO DX", "OW" }
            .Select(c => new DiamondDesktop.Data.Grade { Code = c, DisplayName = c }));

    Check("MARK - and the PDF reader still resolves every one of them",
        new[] { ("#", "NO II"), ("1BB", "NO 1 BB"), ("TOP co", "TOP-COL"), ("color", "COL"),
                ("DX1", "NO DX"), ("OW", "OW") }
            .All(t => map.TryGetValue(t.Item1, out string? code) && code == t.Item2));
}

// ---- Zero figures - the sheet prints 0.00 and so must the screen ---------------------
{
    // The rate cell had its own rule and blanked a zero unconditionally, so a bucket showing
    // 0.00 ct sat beside an empty rate and the pair contradicted each other on the same row.
    Check("ZERO - a rate honours the same switch as the carats beside it",
        DiamondDesktop.MainWindow.ReportFigure(0m, true, "N0") == "0"
        && DiamondDesktop.MainWindow.ReportFigure(0m, false, "N0") == "",
        DiamondDesktop.MainWindow.ReportFigure(0m, true, "N0"));

    Check("ZERO - a rate is whole rupees, never 2dp",
        DiamondDesktop.MainWindow.ReportFigure(27933m, true, "N0") == "27,933",
        DiamondDesktop.MainWindow.ReportFigure(27933m, true, "N0"));

    Check("ZERO - carats keep their two decimals with no format asked for",
        DiamondDesktop.MainWindow.ReportFigure(0m, true) == "0.00");
}

// ---- Sieve marks - "1/5", not the 0.2 the catalogue stores -------------------------
//
// The report knew these two and nothing else in the app did, which is how "the PDF has 1/4 and
// 1/5 but they are missing from the app" was both true and not true at once.
{
    string Short(string code) => DiamondDesktop.Data.SizeNames.Short(code);

    Check("SIEVE - a fifth and a quarter of a carat read as the sheet writes them",
        Short("0.2") == "1/5" && Short("0.25") == "1/4", $"{Short("0.2")} {Short("0.25")}");

    Check("SIEVE - a sieve the sheet prints as its own code is left alone",
        Short("-6.5") == "-6.5" && Short("+11") == "+11" && Short("14+") == "14+"
        && Short("1/6") == "1/6", Short("1/6"));

    Check("SIEVE - never blank, and the models agree with the map",
        Short("") == "" && Short(null) == ""
        && new DiamondDesktop.Data.SizeBucket { Code = "0.25" }.ShortName == "1/4"
        && new DiamondDesktop.Data.VStockPosition { SizeCode = "0.2" }.SizeShort == "1/5");

    // The PDF reader resolves a sheet's "1/5" to the catalogue's 0.2 through this same map, and
    // through sieve notation as a second route. Moving it must not have cost either.
    var map = DiamondDesktop.MainWindow.PdfSizeLabelMap(["-6.5", "+6.5", "+11", "0.2", "0.25"]);

    Check("SIEVE - and the PDF reader still resolves 1/5 and 1/4 to the stored codes",
        map.TryGetValue("1/5", out string? fifth) && fifth == "0.2"
        && map.TryGetValue("1/4", out string? quarter) && quarter == "0.25",
        string.Join(",", map.Keys));

    // The number itself has to keep working: one sheet prints 1/5 and another prints 0.2.
    Check("SIEVE - the number the catalogue stores still resolves too",
        map.ContainsKey("0.2") && map.ContainsKey("0.25"));
}

// ---- Which sieve columns the Stock report opens on ----------------------------------
//
// Wrong three times, each time in a different direction, so the rule is pinned here rather
// than left in a method nothing could reach.
{
    string[] sheet = ["-6.5", "+6.5", "+11", "1/6", "0.2", "0.25"];   // the client's own sheet
    string[] held  = ["-6.5", "+6.5", "+11", "1/6", "0.2"];            // 1/4 prints 0.00 throughout

    bool On(string code) => DiamondDesktop.MainWindow.ReportSizeTicked(code, sheet, held);

    Check("COLUMNS - every column the sheet carries is ticked",
        sheet.All(On), string.Join(",", sheet.Where(z => !On(z))));

    // The one this exists for. A column of 0.00 is a column: the office counts it and the paper
    // shows it, and absence of stock is not absence of a column.
    Check("COLUMNS - including one printed at 0.00 down its whole length", On("0.25"));

    Check("COLUMNS - a sieve the sheet does not carry stays off",
        !On("14+") && !On("1/3") && !On("-2"));

    // The floor under the rule: whatever the sheet said, stock is never hidden.
    Check("COLUMNS - a sieve holding carats is ticked even when the sheet never had it",
        DiamondDesktop.MainWindow.ReportSizeTicked("14+", sheet, ["14+"]));

    // Before any sheet has been imported.
    Check("COLUMNS - with no sheet recorded, what holds stock decides",
        DiamondDesktop.MainWindow.ReportSizeTicked("-6.5", [], held)
        && !DiamondDesktop.MainWindow.ReportSizeTicked("0.25", [], held));

    // A report opening on no columns at all reads as broken, not as "there is nothing here".
    Check("COLUMNS - an empty position ticks everything rather than nothing",
        DiamondDesktop.MainWindow.ReportSizeTicked("+23", [], []));
}

// ---- Every rate the sheet prints, all the way to the screen -------------------------
//
// The client's sheet runs 1/5 and 1/4 at 0.00 down their whole length and prints 63,000 and
// 68,000 beside them. Those are prices, not costs -- there is no weight for a cost to be an
// average OF -- so the ledger has nowhere to put them and the report showed two empty columns
// where the paper showed a figure in every cell. The parser now keeps them.
{
    var grades = new[] { ("NO 1 BB", "1BB;1 BB"), ("LB 2", "LB2"),
                         (DiamondDesktop.PdfStockFile.UnknownGrade, (string?)null) }
        .Select((g, i) => new DiamondDesktop.Data.Grade
        {
            GradeId = i + 1, Code = g.Item1, Aliases = g.Item2, DisplayName = g.Item1,
        })
        .ToList();

    // 1/5 and 1/4 added to the sample sheet, both 0.00 the whole way down, both priced -- the
    // exact shape of the columns that were coming through blank.
    var sheet = new List<(string Text, double CentreX, double Y)>(
        DiamondCalc.Tests.MiniPdf.UnnamedRowSheet)
    {
        ("1/5", 440, 530), ("RATE", 510, 530),
        ("1/4", 580, 530), ("RATE", 650, 530),
        ("0.00", 440, 510), ("63000", 510, 510), ("0.00", 580, 510), ("68000", 650, 510),
        ("0.00", 440, 490), ("51000", 510, 490), ("0.00", 580, 490), ("53000", 650, 490),
        ("0.00", 440, 470), ("19000", 510, 470), ("0.00", 580, 470), ("19000", 650, 470),
        ("0.00", 440, 450), ("15000", 510, 450), ("0.00", 580, 450), ("15000", 650, 450),
        ("0.00", 440, 420), ("37000", 510, 420), ("0.00", 580, 420), ("38750", 650, 420),
    };

    string pdf = Path.Combine(Path.GetTempPath(), "priced-empty-columns.pdf");
    DiamondCalc.Tests.MiniPdf.Write(pdf, sheet);

    try
    {
        var plan = DiamondDesktop.PdfStockFile.Plan(
            pdf,
            DiamondDesktop.MainWindow.PdfGradeLabelMap(grades),
            DiamondDesktop.MainWindow.PdfSizeLabelMap(["-6.5", "+6.5", "0.2", "0.25"]));

        Check("RATES - the sheet still reads with two empty priced columns on it",
            plan.IsValid, DiamondDesktop.PdfStockFile.ProblemText(plan));

        Eq("RATES - and still totals what it prints", plan.TotalCarats, 11.00m);

        // All four columns are found, including the two that hold nothing at all. A column of
        // 0.00 that is dropped takes its prices with it.
        Check("RATES - all four size columns are read, not just the two holding stock",
            plan.SizeOrder.Count == 4, string.Join(",", plan.SizeOrder));

        // The point of the whole exercise.
        Check("RATES - a rate beside 0.00 carats is kept, not discarded with the weight",
            plan.PrintedRates.GetValueOrDefault(("NO 1 BB", "0.2")) == 63000m
            && plan.PrintedRates.GetValueOrDefault(("NO 1 BB", "0.25")) == 68000m,
            $"{plan.PrintedRates.GetValueOrDefault(("NO 1 BB", "0.2"))} / "
            + $"{plan.PrintedRates.GetValueOrDefault(("NO 1 BB", "0.25"))}");

        Check("RATES - including on a row the sheet printed no grade name against",
            plan.PrintedRates.GetValueOrDefault((DiamondDesktop.PdfStockFile.UnknownGrade, "0.2"))
                == 51000m,
            $"{plan.PrintedRates.GetValueOrDefault((DiamondDesktop.PdfStockFile.UnknownGrade, "0.2"))}");

        // The rates that DO ride a holding are unchanged: those become cost, and this must not
        // have quietly replaced one with the other.
        Check("RATES - a rate on a real holding still rides the holding",
            plan.Rows.Any(r => r.GradeCode == "NO 1 BB" && r.SizeCode == "-6.5"
                               && r.WeightCt == 4.00m && r.PricePerCt == 30000m),
            string.Join(" ", plan.Rows.Select(r => $"{r.SizeCode}:{r.WeightCt}@{r.PricePerCt}")));

        Check("RATES - and is recorded as a printed rate as well",
            plan.PrintedRates.GetValueOrDefault(("NO 1 BB", "-6.5")) == 30000m);

        // A zero rate is not a rate. Storing it would print 0 where the sheet prints nothing.
        Check("RATES - nothing is invented where the sheet priced nothing",
            !plan.PrintedRates.ContainsKey(("LB 2", "1/3")));

        // ---- and the round trip that carries them to the next session --------------
        string packed = DiamondDesktop.MainWindow.FormatSheetRates(plan.PrintedRates);
        var back = DiamondDesktop.MainWindow.ParseSheetRates(packed);

        Check("RATES - every rate survives the round trip through app_config",
            back.Count == plan.PrintedRates.Count
            && plan.PrintedRates.All(r => back.GetValueOrDefault((r.Key.GradeCode, r.Key.SizeCode))
                                          == r.Value),
            $"{back.Count} of {plan.PrintedRates.Count}");

        Check("RATES - 63,000 against 1/4 is still 63,000 after the round trip",
            back.GetValueOrDefault(("NO 1 BB", "0.2")) == 63000m,
            $"{back.GetValueOrDefault(("NO 1 BB", "0.2"))}");

        // A hand-edited config value must not be able to stop the report drawing.
        Check("RATES - a malformed entry is dropped, never thrown",
            DiamondDesktop.MainWindow.ParseSheetRates("NO 1,-6.5;;x,y,z;NO 2,+11,900")
                is { Count: 1 } salvaged
            && salvaged.GetValueOrDefault(("NO 2", "+11")) == 900m);

        Check("RATES - and an empty or absent value is simply no rates",
            DiamondDesktop.MainWindow.ParseSheetRates("").Count == 0
            && DiamondDesktop.MainWindow.ParseSheetRates(null).Count == 0);
    }
    finally
    {
        try { File.Delete(pdf); } catch (IOException) { }
    }
}

// ---- The subtotal rate under a column holding nothing -------------------------------
//
// Pinned to the client's own 1/4 column, which prints 0.00 carats down its whole length and
// still states 34,921 at the foot. The app divided by the zero weight and printed 0.
{
    // The nineteen rates the sheet prints down 1/4, in its own order.
    decimal[] quarterCarat =
    [
        68000, 53000, 53000, 47000, 43000, 40000, 37500, 34000, 30000, 27000,
        23000, 38500, 34500, 27500, 19500, 15000, 28000, 26000, 19000,
    ];

    Check("SUBTOTAL - an unweighted column averages its printed rates, as the sheet does",
        DiamondDesktop.MainWindow.SubtotalRate(0m, 0m, quarterCarat, true) == "34,921",
        DiamondDesktop.MainWindow.SubtotalRate(0m, 0m, quarterCarat, true));

    // A grade the office does not price is not a zero in the average -- counting it would drag
    // the figure down by however many grades happen not to trade that sieve.
    Check("SUBTOTAL - a bucket the sheet never priced is left out of the average",
        DiamondDesktop.MainWindow.SubtotalRate(0m, 0m, [.. quarterCarat, 0m, 0m, 0m], true)
            == "34,921");

    // The weighted half must be untouched: 1/5 states 26,934 against 0.99 ct on the same sheet.
    Check("SUBTOTAL - a column WITH weight is still value over weight, not an average",
        DiamondDesktop.MainWindow.SubtotalRate(0.99m, 26664.66m, [19500, 32500, 25000], true)
            == "26,934",
        DiamondDesktop.MainWindow.SubtotalRate(0.99m, 26664.66m, [19500, 32500, 25000], true));

    // Weighting is the whole point of the first rule, so prove the two rules disagree: these
    // same rates plain-averaged would be 25,667.
    Check("SUBTOTAL - and the two rules genuinely differ",
        DiamondDesktop.MainWindow.SubtotalRate(0m, 0m, [19500, 32500, 25000], true) == "25,667");

    // No weight and no prices at all is the one case with nothing to say.
    Check("SUBTOTAL - a column with neither weight nor a printed rate follows the zero switch",
        DiamondDesktop.MainWindow.SubtotalRate(0m, 0m, [], true) == "0"
        && DiamondDesktop.MainWindow.SubtotalRate(0m, 0m, [], false) == "",
        DiamondDesktop.MainWindow.SubtotalRate(0m, 0m, [], true));

    // Half a rupee rounds up, matching the sheet's own arithmetic.
    Check("SUBTOTAL - rounded to the rupee, away from zero",
        DiamondDesktop.MainWindow.SubtotalRate(0m, 0m, [1000m, 1001m], true) == "1,001");
}

// ---- Document types on Sales entry --------------------------------------------------
{
    var types = DiamondDesktop.Catalogue.DocTypes;

    Check("DOCTYPE - the desk can write all four kinds of invoice",
        types.SequenceEqual(new[] { "BILL", "WITHOUT BILL", "EXPORT", "DOLLAR BILL" }),
        string.Join(" | ", types));

    // BILL is what a new invoice opens on and what the importer falls back to, so it must be the
    // one the picker lands on.
    Check("DOCTYPE - BILL stays the default", types[0] == "BILL", types[0]);

    // sales_invoice.doc_type is varchar(20). A value longer than that is refused by the database
    // at save, which is the worst moment to find out.
    Check("DOCTYPE - every one fits the column",
        types.All(t => t.Length <= 20), string.Join(",", types.Where(t => t.Length > 20)));

    // The sale workbook importer upper-cases column P. A picker offering "Export" beside imported
    // rows reading "EXPORT" would be two spellings of one thing in one column.
    Check("DOCTYPE - stored as the importer stores them, in upper case",
        types.All(t => t == t.ToUpperInvariant()), string.Join(",", types));

    Check("DOCTYPE - and none is a duplicate of another",
        types.Distinct().Count() == types.Count);
}

// ---- The busy latch - "Refreshing..." must go back to "Refresh" ---------------------
//
// The exact sequence the header Refresh produces. ReloadCurrentTab starts an async void page
// load and does not await it, so the refresh's scope ends FIRST and the page's ends second.
{
    object button = new();

    // 1 - the plain case: one operation, start to finish.
    {
        var latch = new DiamondDesktop.BusyLatch();
        latch.Claim(button, "Refresh");
        Check("BUSY - one operation puts the caption back",
            latch.Release(button, out object? back) && (string?)back == "Refresh", $"{back}");
        Check("BUSY - and nothing is left holding it", latch.Idle);
    }

    // 2 - THE BUG. Refresh claims, the page load claims, refresh releases first.
    {
        var latch = new DiamondDesktop.BusyLatch();
        string showing = "Refresh";

        latch.Claim(button, showing); showing = "Refreshing...";     // refresh starts
        latch.Claim(button, showing); showing = "Loading...";        // page load starts

        // The refresh finishes first and must NOT put anything back: the page is still loading
        // and the button is still telling the truth.
        bool refreshRestores = latch.Release(button, out object? afterRefresh);
        if (refreshRestores) showing = (string?)afterRefresh ?? showing;

        Check("BUSY - the first to finish leaves the caption alone", !refreshRestores, showing);
        Check("BUSY - so the button still says what is still happening",
            showing == "Loading...", showing);

        // The page load finishes last and puts back what the FIRST claim found - not what it
        // found itself, which was "Refreshing..." and is how the button used to stick.
        bool pageRestores = latch.Release(button, out object? afterPage);
        if (pageRestores) showing = (string?)afterPage ?? showing;

        Check("BUSY - the last to finish restores the original", pageRestores);
        Check("BUSY - which is Refresh, never Refreshing...", showing == "Refresh", showing);
        Check("BUSY - and the latch is idle again", latch.Idle);
    }

    // 3 - the other order, in case a page load ever finishes first.
    {
        var latch = new DiamondDesktop.BusyLatch();
        latch.Claim(button, "Refresh");
        latch.Claim(button, "Refreshing...");

        bool firstHeld = latch.Release(button, out _);
        bool lastRestores = latch.Release(button, out object? last);
        Check("BUSY - order does not matter, only depth",
            !firstHeld && lastRestores && (string?)last == "Refresh" && latch.Idle, $"{last}");
    }

    // 4 - three deep, because nothing bounds the nesting.
    {
        var latch = new DiamondDesktop.BusyLatch();
        latch.Claim(button, "Refresh");
        latch.Claim(button, "Refreshing...");
        latch.Claim(button, "Loading...");

        bool a = latch.Release(button, out _);
        bool b = latch.Release(button, out _);
        bool c = latch.Release(button, out object? deep);
        Check("BUSY - any depth unwinds to the same caption",
            !a && !b && c && (string?)deep == "Refresh", $"{deep}");
    }

    // 5 - an error is just an early release. The using block runs it either way, so the only
    // thing that matters is that a release with nothing held cannot throw or invent a caption.
    {
        var latch = new DiamondDesktop.BusyLatch();
        Check("BUSY - releasing something nobody holds is harmless",
            !latch.Release(button, out object? none) && none is null && latch.Idle);

        latch.Claim(button, "Refresh");
        latch.Release(button, out _);
        Check("BUSY - and a second release after a failure changes nothing",
            !latch.Release(button, out _) && latch.Idle);
    }

    // 6 - enabling is counted too: an inner scope finishing must not re-enable a button the
    // outer one is still working behind.
    {
        var latch = new DiamondDesktop.BusyLatch();
        object signOut = new();

        latch.Disable(button); latch.Disable(signOut);   // refresh disables both
        latch.Disable(button);                           // the page load disables the button again

        Check("BUSY - the inner scope does not re-enable a button still in use",
            !latch.Enable(button), "button must stay disabled");
        Check("BUSY - a button only that scope held is released",
            latch.Enable(signOut));
        Check("BUSY - and the last release enables it", latch.Enable(button) && latch.Idle);
    }
}

Console.WriteLine();
Console.WriteLine(failed == 0 ? "All checks passed." : $"{failed} check(s) FAILED.");
return failed == 0 ? 0 : 1;
