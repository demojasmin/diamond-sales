using System.Globalization;
using System.Text.RegularExpressions;
using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using DiamondCalc;

namespace DiamondDesktop;

/// <summary>
/// Reads an .xlsx with the framework only — a zip of XML parts is all it is, and a spreadsheet
/// library would be a dependency earned by nothing. Values come back as raw strings keyed by
/// column letter; interpreting them is the caller's job.
/// </summary>
public static class Xlsx
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PR = "http://schemas.openxmlformats.org/package/2006/relationships";

    public sealed record Row(int Number, IReadOnlyDictionary<string, string> Cells)
    {
        public string this[string column] => Cells.GetValueOrDefault(column, "");
    }

    /// <summary>Sheet names in workbook order. Throws if the file is not a readable xlsx.</summary>
    public static List<string> SheetNames(string path)
    {
        using var zip = OpenShared(path);
        return Part(zip, "xl/workbook.xml").Descendants(S + "sheet")
            .Select(e => (string?)e.Attribute("name") ?? "").ToList();
    }

    public static List<Row> ReadSheet(string path, string sheetName)
    {
        using var zip = OpenShared(path);

        var sheet = Part(zip, "xl/workbook.xml").Descendants(S + "sheet").FirstOrDefault(
            e => string.Equals((string?)e.Attribute("name"), sheetName, StringComparison.Ordinal))
            ?? throw new InvalidDataException($"The workbook has no sheet named \"{sheetName}\".");

        string rid = (string?)sheet.Attribute(R + "id") ?? "";
        string target = Part(zip, "xl/_rels/workbook.xml.rels").Descendants(PR + "Relationship")
            .First(e => (string?)e.Attribute("Id") == rid).Attribute("Target")!.Value;
        // A relationship Target beginning with "/" is package-absolute; anything else is relative
        // to the part that declared it, which for workbook.xml.rels means "xl/". Prefixing both
        // alike produced "xl/xl/worksheets/sheet1.xml" and rejected the file as corrupt. Excel
        // writes the relative form, so this never showed on a hand-made workbook — openpyxl and
        // several other writers use the absolute one.
        string part = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;

        // Shared strings are optional: a sheet can carry every string inline.
        var shared = zip.GetEntry("xl/sharedStrings.xml") is null
            ? []
            : Part(zip, "xl/sharedStrings.xml").Elements(S + "si")
                .Select(si => string.Concat(si.Descendants(S + "t").Select(t => t.Value))).ToList();

        var rows = new List<Row>();
        foreach (var re in Part(zip, part).Descendants(S + "row"))
        {
            var cells = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var ce in re.Elements(S + "c"))
            {
                string reference = (string?)ce.Attribute("r") ?? "";
                string column = new string(reference.TakeWhile(char.IsLetter).ToArray());
                string type = (string?)ce.Attribute("t") ?? "";

                // A cell with a formula carries its last cached result in <v>; that is what a
                // reader should use, because the formula itself is not a value.
                string? text = type switch
                {
                    "s" when int.TryParse(ce.Element(S + "v")?.Value, out int i) && i < shared.Count
                        => shared[i],
                    "inlineStr" => string.Concat(ce.Descendants(S + "t").Select(t => t.Value)),
                    _ => ce.Element(S + "v")?.Value,
                };
                if (!string.IsNullOrEmpty(text)) cells[column] = text;
            }
            if (cells.Count > 0 && int.TryParse((string?)re.Attribute("r"), out int n))
                rows.Add(new Row(n, cells));
        }
        return rows;
    }

    /// <summary>
    /// Opens the workbook without demanding exclusive access. ZipFile.OpenRead refuses a file that
    /// Excel already has open, and having the sheet open while importing it is the normal case, not
    /// an odd one — being told "used by another process" instead is a poor reason to fail.
    /// </summary>
    private static ZipArchive OpenShared(string path) =>
        new(new FileStream(path, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete),
            ZipArchiveMode.Read);

    private static XElement Part(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name)
            ?? throw new InvalidDataException($"This file is missing \"{name}\" and is not a valid Excel workbook.");
        using var stream = entry.Open();
        return XElement.Load(stream);
    }
}

/// <summary>One problem found during validation, worded for someone who has the file open.</summary>
public sealed record ImportProblem(string Message);

/// <summary>A sale line as it appears in the workbook, already parsed and checked.</summary>
public sealed record SaleRow(
    int ExcelRow, int Sr, DateOnly Date, string Buyer, string Broker, decimal BrokerPct,
    int TermsDays, string SizeCode, string GradeCode, decimal GrossCt, decimal SelectionCt,
    decimal PricePerCt, decimal ExRate, decimal Less1Pct, decimal Less2Pct, string DocType,
    decimal ReceivedAmount);

/// <summary>An invoice assembled from one or more workbook rows sharing a Sr. and a header.</summary>
public sealed record PlannedInvoice(
    string InvoiceNo, DateOnly Date, string Buyer, string? Broker, decimal BrokerPct,
    int TermsDays, string DocType, List<SaleRow> Lines, decimal Total, decimal Received);

public sealed class ImportPlan
{
    /// Structural faults: a missing sheet, a renamed column, an unreadable file. These stop the
    /// import, because nothing below them can be trusted.
    public List<ImportProblem> Problems { get; } = [];

    /// Row-level faults: a grade or size the catalogue does not know, a missing figure. docs/08 §4
    /// calls these exceptions, not failures — the row is skipped and reported, and the rest of the
    /// file still imports. Aborting 1,369 good rows over 68 unmapped ones helps nobody.
    public List<ImportProblem> Exceptions { get; } = [];
    public int SkippedRows { get; set; }
    public List<PlannedInvoice> Invoices { get; } = [];
    public List<string> Buyers { get; } = [];
    public List<string> Brokers { get; } = [];
    public int LineCount => Invoices.Sum(i => i.Lines.Count);
    public int ReceiptCount => Invoices.Count(i => i.Received > 0);
    public int SplitSrCount { get; set; }
    public DateOnly? FirstDate => Invoices.Count == 0 ? null : Invoices.Min(i => i.Date);
    public DateOnly? LastDate => Invoices.Count == 0 ? null : Invoices.Max(i => i.Date);
    public bool IsValid => Problems.Count == 0;
}

/// <summary>
/// Validates and plans an import of the sale workbook, per docs/08 §4: rows group by Sr. into one
/// invoice with N lines, amounts are recomputed with CALC-1 rather than copied, and grade and size
/// resolve against the catalogue — an unknown code is an exception, never a guess.
///
/// Nothing here touches the database. The whole file is checked before anything is written, so a
/// bad workbook cannot leave a half-finished import behind.
/// </summary>
public static class SaleFileImport
{
    public const string SheetName = "Sheet1";
    public const int HeaderRow = 2;
    public const int FirstDataRow = 3;
    private static readonly DateOnly Epoch = new(1899, 12, 30);

    /// The columns the importer reads, with the heading each must carry.
    public static readonly (string Column, string Header)[] RequiredColumns =
    [
        ("A", "Sr."), ("B", "Date"), ("C", "Name"), ("D", "Broker"), ("E", "Broker %"),
        ("F", "Terms"), ("G", "Size"), ("H", "Number"), ("I", "Weight"), ("K", "Selection"),
        ("L", "Price Per ct"), ("M", "Ex Rate"), ("N", "Less 1"), ("O", "Less 2"),
        ("P", "Type"), ("R", "Rec. Amt"),
    ];

    private const int MaxReportedProblems = 12;

    /// <summary>
    /// Maps every spelling a workbook may use to the catalogue code it means: the code itself, plus
    /// each entry in grade.aliases (docs/08 §4 — resolved through the alias tables, never guessed).
    /// Anything absent from this map is an error, not an approximation.
    /// </summary>
    /// <summary>
    /// MDM-004 · four canonical sizes, four notations. A sieve code is a sign and a number, and the
    /// workbooks write the sign on either end: "+6.5" and "6.5+" are the same bucket, as are "-6.5"
    /// and "6.5-". That is notation, not business meaning, so it is a rule rather than a table —
    /// nothing to seed, nothing to keep in step with the catalogue.
    ///
    /// Deliberately narrow. "0.2", "0.25" and "14+" carry no sign that maps to a catalogue bucket
    /// and are left unresolved, so validation still reports them.
    /// </summary>
    public static Dictionary<string, string> SizeAliasMap(IEnumerable<string> codes)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in codes)
        {
            string code = raw.Trim();
            map[code] = code;
            if (code.Length > 1 && (code[0] == '+' || code[0] == '-'))
                map.TryAdd(code[1..] + code[0], code);       // "+6.5" also accepts "6.5+"
        }
        return map;
    }

    public static Dictionary<string, string> AliasMap(
        IEnumerable<(string Code, string? Aliases)> catalogue)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (code, aliases) in catalogue)
        {
            map[code.Trim()] = code.Trim();
            foreach (string alias in (aliases ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries
                                                                | StringSplitOptions.TrimEntries))
                map.TryAdd(alias, code.Trim());     // the code itself always wins a collision
        }
        return map;
    }

    /// <summary>Convenience for callers with no aliases to offer: every code maps to itself.</summary>
    public static ImportPlan Plan(string path,
                                  IReadOnlyCollection<string> knownGrades,
                                  IReadOnlyCollection<string> knownSizes) =>
        Plan(path,
             AliasMap(knownGrades.Select(g => (g, (string?)null))),
             AliasMap(knownSizes.Select(s => (s, (string?)null))));

    public static ImportPlan Plan(string path,
                                  IReadOnlyDictionary<string, string> gradeMap,
                                  IReadOnlyDictionary<string, string> sizeMap)
    {
        var plan = new ImportPlan();

        List<string> sheets;
        try
        {
            sheets = Xlsx.SheetNames(path);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException
                                      or UnauthorizedAccessException)
        {
            plan.Problems.Add(new ImportProblem(
                $"This file could not be opened as an Excel workbook. {ex.Message}"));
            return plan;
        }

        if (!sheets.Contains(SheetName, StringComparer.Ordinal))
        {
            plan.Problems.Add(new ImportProblem(
                $"The sheet \"{SheetName}\" is missing. This workbook has: " +
                (sheets.Count == 0 ? "no sheets at all." : string.Join(", ", sheets) + ".")));
            return plan;                              // nothing else can be checked without it
        }

        List<Xlsx.Row> rows;
        try
        {
            rows = Xlsx.ReadSheet(path, SheetName);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            plan.Problems.Add(new ImportProblem($"\"{SheetName}\" could not be read. {ex.Message}"));
            return plan;
        }

        var header = rows.FirstOrDefault(r => r.Number == HeaderRow);
        if (header is null)
        {
            plan.Problems.Add(new ImportProblem(
                $"Row {HeaderRow} is empty, so the column headings could not be found."));
            return plan;
        }

        var missing = RequiredColumns
            .Where(c => !string.Equals(header[c.Column].Trim(), c.Header, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (missing.Count > 0)
        {
            foreach (var (column, expected) in missing)
            {
                string found = header[column].Trim();
                plan.Problems.Add(new ImportProblem(
                    $"Column {column} should be \"{expected}\" but " +
                    (found.Length == 0 ? "is empty." : $"reads \"{found}\".")));
            }
            return plan;                              // column meanings are unsafe to assume
        }

        var data = rows.Where(r => r.Number >= FirstDataRow).ToList();
        if (data.Count == 0)
        {
            plan.Problems.Add(new ImportProblem(
                $"\"{SheetName}\" has headings but no data rows below row {HeaderRow}."));
            return plan;
        }

        var parsed = new List<SaleRow>();
        foreach (var row in data)
        {
            int before = plan.Exceptions.Count;
            var sale = ParseRow(row, gradeMap, sizeMap, plan.Exceptions);
            if (sale is not null) parsed.Add(sale);
            else if (plan.Exceptions.Count > before) plan.SkippedRows++;
        }

        // Every row rejected means the file is not what it claims to be, not that it has a few
        // bad rows — that is structural, and it stops the import.
        if (parsed.Count == 0)
        {
            plan.Problems.Add(new ImportProblem(
                plan.SkippedRows > 0
                    ? $"All {plan.SkippedRows} data row(s) were rejected. First: "
                      + plan.Exceptions[0].Message
                    : $"No usable sale rows were found below row {HeaderRow}."));
            return plan;
        }

        Group(parsed, plan);
        return plan;
    }

    private static SaleRow? ParseRow(Xlsx.Row row,
                                     IReadOnlyDictionary<string, string> gradeMap,
                                     IReadOnlyDictionary<string, string> sizeMap,
                                     List<ImportProblem> problems)
    {
        // A row with no date and no weight is trailing formatting, not a record. Skipping it
        // silently is right; complaining about it would make every real file look broken.
        if (row["B"].Length == 0 && row["I"].Length == 0 && row["C"].Length == 0) return null;

        int before = problems.Count;

        DateOnly date = default;
        if (!TryDecimal(row["B"], out decimal serial) || serial < 1)
            problems.Add(new ImportProblem($"Row {row.Number}: the date is missing or unreadable."));
        else
            date = Epoch.AddDays((int)serial);

        string buyer = row["C"].Trim();
        if (buyer.Length == 0)
            problems.Add(new ImportProblem($"Row {row.Number}: the buyer name (column C) is empty."));

        // The workbook's spelling is resolved to the catalogue code here, so everything
        // downstream deals in canonical codes only.
        string grade = row["H"].Trim();
        if (grade.Length == 0)
            problems.Add(new ImportProblem($"Row {row.Number}: the grade (column H) is empty."));
        else if (!gradeMap.TryGetValue(grade, out string? resolvedGrade))
            problems.Add(new ImportProblem(
                $"Row {row.Number}: grade \"{grade}\" is not in the catalogue and has no alias."));
        else
            grade = resolvedGrade;

        string size = row["G"].Trim();
        if (size.Length == 0)
            problems.Add(new ImportProblem($"Row {row.Number}: the size (column G) is empty."));
        else if (!sizeMap.TryGetValue(size, out string? resolvedSize))
            problems.Add(new ImportProblem(
                $"Row {row.Number}: size \"{size}\" is not in the catalogue and has no alias."));
        else
            size = resolvedSize;

        decimal gross = Need(row, "I", "weight", row.Number, problems);
        decimal selection = Need(row, "K", "selection", row.Number, problems);
        decimal price = Need(row, "L", "price per ct", row.Number, problems);

        if (selection > gross && problems.Count == before)
            problems.Add(new ImportProblem(
                $"Row {row.Number}: selection {selection:N2} ct is greater than the weight {gross:N2} ct."));

        if (problems.Count != before) return null;

        TryDecimal(row["A"], out decimal sr);
        TryDecimal(row["E"], out decimal brokerPct);
        TryDecimal(row["F"], out decimal terms);
        TryDecimal(row["N"], out decimal less1);
        TryDecimal(row["O"], out decimal less2);
        TryDecimal(row["R"], out decimal received);
        decimal exRate = TryDecimal(row["M"], out decimal x) && x > 0 ? x : 1m;
        string docType = row["P"].Trim().Length == 0 ? "BILL" : row["P"].Trim().ToUpperInvariant();

        return new SaleRow(row.Number, (int)sr, date, buyer, row["D"].Trim(), brokerPct,
                           (int)terms, size, grade, gross, selection, price, exRate,
                           less1, less2, docType, received);
    }

    private static decimal Need(Xlsx.Row row, string column, string what, int number,
                                List<ImportProblem> problems)
    {
        if (row[column].Trim().Length == 0)
        {
            problems.Add(new ImportProblem($"Row {number}: the {what} (column {column}) is empty."));
            return 0;
        }
        if (!TryDecimal(row[column], out decimal value) || value < 0)
        {
            problems.Add(new ImportProblem(
                $"Row {number}: the {what} (column {column}) reads \"{row[column]}\", " +
                "which is not a number of at least zero."));
            return 0;
        }
        return value;
    }

    private static bool TryDecimal(string text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>
    /// Rows group by Sr. into one invoice (docs/08 §4). Where one Sr. carries rows that disagree on
    /// the header — a different date or buyer — they become separate invoices rather than being
    /// merged, because merging two buyers' rows into one document would invent a sale that never
    /// happened. The count is reported so the split is never silent.
    /// </summary>
    private static void Group(List<SaleRow> rows, ImportPlan plan)
    {
        var groups = rows
            .GroupBy(r => (r.Sr, r.Date, r.Buyer, r.Broker, r.BrokerPct, r.TermsDays, r.DocType))
            .OrderBy(g => g.Key.Date).ThenBy(g => g.Key.Sr)
            .ToList();

        var perSr = groups.GroupBy(g => g.Key.Sr).ToDictionary(g => g.Key, g => g.Count());
        plan.SplitSrCount = perSr.Count(kv => kv.Value > 1);

        var seq = new Dictionary<int, int>();
        foreach (var group in groups)
        {
            int sr = group.Key.Sr;
            int n = seq.TryGetValue(sr, out int prev) ? prev + 1 : 1;
            seq[sr] = n;

            var lines = group.ToList();
            decimal total = lines.Sum(l => Calc.LineAmount(
                l.SelectionCt, l.PricePerCt, l.ExRate, l.Less1Pct, l.Less2Pct, l.BrokerPct));

            plan.Invoices.Add(new PlannedInvoice(
                InvoiceNo: perSr[sr] > 1 ? $"MIG-{sr}-{n}" : $"MIG-{sr}",
                Date: group.Key.Date, Buyer: group.Key.Buyer,
                Broker: group.Key.Broker.Length == 0 ? null : group.Key.Broker,
                BrokerPct: group.Key.BrokerPct, TermsDays: group.Key.TermsDays,
                DocType: group.Key.DocType, Lines: lines, Total: Calc.RoundMoney(total),
                Received: Calc.RoundMoney(lines.Sum(l => l.ReceivedAmount))));
        }

        plan.Buyers.AddRange(rows.Select(r => r.Buyer)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(b => b));
        plan.Brokers.AddRange(rows.Select(r => r.Broker).Where(b => b.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(b => b));
    }

    /// <summary>The skipped rows, grouped so 68 rows on three sizes read as three lines.</summary>
    public static string ExceptionText(ImportPlan plan)
    {
        if (plan.Exceptions.Count == 0) return "";
        var grouped = plan.Exceptions
            .GroupBy(e => Regex.Replace(e.Message, @"^Row \d+: ", ""))
            .OrderByDescending(g => g.Count())
            .Take(MaxReportedProblems)
            .Select(g => $"  •  {g.Count()} row(s): {g.Key}");
        return string.Join(Environment.NewLine, grouped);
    }

    /// <summary>The validation failure, worded for a message box. Caps the list so one broken
    /// column does not produce a thousand-line dialog.</summary>
    public static string ProblemText(ImportPlan plan)
    {
        var shown = plan.Problems.Take(MaxReportedProblems).Select(p => "  •  " + p.Message);
        string more = plan.Problems.Count > MaxReportedProblems
            ? $"\n  …and more. Fix these first."
            : "";
        return "This file cannot be imported:\n\n" + string.Join("\n", shown) + more;
    }
}
