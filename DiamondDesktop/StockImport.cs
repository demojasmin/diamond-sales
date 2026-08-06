using System.Globalization;
using System.IO;

namespace DiamondDesktop;

/// <summary>One grade × size holding, as the workbook states it.</summary>
public sealed record StockRow(string GradeCode, string SizeCode, decimal WeightCt, decimal PricePerCt,
                              int SourceRow, string GradeLabel, string SizeLabel);

public sealed class StockImportPlan
{
    public List<StockRow> Rows { get; } = [];
    public List<ImportProblem> Problems { get; } = [];
    public List<ImportProblem> Exceptions { get; } = [];
    public int SkippedRows { get; set; }

    /// Holdings that carry real weight but name a grade or sieve size the catalogue does not have.
    /// Counted apart from SkippedRows because these are the only skips that LOSE CARATS: a blank or
    /// sentinel balance is filtered out before the lookups, so anything reaching them has weight.
    public int UnplacedRows { get; set; }
    public decimal UnplacedCarats { get; set; }

    /// The grade or size labels the catalogue did not recognise, for the message.
    public List<string> UnplacedLabels { get; } = [];

    public bool IsValid => Problems.Count == 0;
    public decimal TotalCarats => Rows.Sum(r => r.WeightCt);
    public decimal TotalValue => Rows.Sum(r => r.WeightCt * r.PricePerCt);
    public int GradeCount => Rows.Select(r => r.GradeCode).Distinct().Count();
}

/// <summary>
/// Reads the stock position out of a KAPNA ADD workbook.
///
/// Only that one sheet is read. It is a consolidated view whose cells are formulas pointing at the
/// twenty-two grade sheets ("N2 = '1 '!B11"), so it already carries every grade's position and
/// there is nothing to gain from parsing the others.
///
/// The sheet is blocks, not a table. Each grade occupies a run of rows: column A repeats the grade
/// name, column M names the size, and the block ends with a TOTAL row. The columns are:
///
///     M  size bucket        N  total stock ct      O  price per carat
///     P  sale ct            Q  sale rate          R  BALANCE STK ct   ← what is imported
///
/// Column O is headed "VALUE" but holds a per-carat rate, not an extended amount — the values sit
/// in the 40,000-58,000 range alongside the sheets' own PRICE columns. Reading it as a total would
/// overstate stock value by roughly the carat weight.
/// </summary>
public static class StockFileImport
{
    public const string SheetName = "KAPNA ADD";

    private const string GradeColumn = "A";
    private const string SizeColumn = "M";
    private const string PriceColumn = "O";
    private const string BalanceColumn = "R";

    /// Weights below this are the workbook's own placeholders (1E-8, 1E-5), written to keep its
    /// average formulas from dividing by zero. Importing them would create phantom parcels.
    public const decimal Sentinel = 0.001m;

    public static StockImportPlan Plan(string path,
                                       IReadOnlyDictionary<string, string> gradeMap,
                                       IReadOnlyDictionary<string, string> sizeMap)
    {
        var plan = new StockImportPlan();

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
            return plan;
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

        string? gradeLabel = null;
        foreach (var row in rows)
        {
            string a = row[GradeColumn].Trim();

            // Column A repeats the grade down its block and reads TOTAL on the closing row. The
            // sheet's footer lists bare sizes in that same column ("-2", "14", "18"), which name no
            // grade — but so does the grade "+14", and decimal.TryParse accepts a leading plus, so
            // rejecting anything numeric silently handed +14's holdings to the block above it.
            // The catalogue decides instead: a number it knows is a grade, one it does not is
            // footer. Anything non-numeric is taken as a grade either way, so an unknown one is
            // reported rather than absorbed into its predecessor.
            if (a.Length > 0 && !a.Equals("TOTAL", StringComparison.OrdinalIgnoreCase)
                             && (!IsNumeric(a) || gradeMap.ContainsKey(Key(a))))
                gradeLabel = a;

            string sizeLabel = row[SizeColumn].Trim();
            if (gradeLabel is null || sizeLabel.Length == 0) continue;

            // A block's header row repeats the grade in M, and its last row reads TOTAL. Neither
            // is a holding.
            if (sizeLabel.Equals("TOTAL", StringComparison.OrdinalIgnoreCase)) continue;
            if (SizeKey(sizeLabel) is null) continue;

            string balanceText = row[BalanceColumn].Trim();
            if (balanceText.Length == 0) continue;

            if (!decimal.TryParse(balanceText, NumberStyles.Float, CultureInfo.InvariantCulture,
                                  out decimal weight))
            {
                plan.Exceptions.Add(new ImportProblem(
                    $"Row {row.Number}: {gradeLabel} × {sizeLabel} has a balance of "
                    + $"\"{balanceText}\", which is not a number."));
                plan.SkippedRows++;
                continue;
            }

            if (Math.Abs(weight) < Sentinel) continue;      // the sheet's own placeholder

            if (!gradeMap.TryGetValue(Key(gradeLabel), out string? gradeCode))
            {
                plan.Exceptions.Add(new ImportProblem(
                    $"Row {row.Number}: grade \"{gradeLabel}\" is not in the catalogue and has no "
                    + $"alias, so {weight:N4} ct could not be placed."));
                plan.SkippedRows++;
                plan.UnplacedRows++;
                plan.UnplacedCarats += Math.Abs(weight);
                if (!plan.UnplacedLabels.Contains(gradeLabel)) plan.UnplacedLabels.Add(gradeLabel);
                continue;
            }

            if (!sizeMap.TryGetValue(SizeKey(sizeLabel)!, out string? sizeCode))
            {
                plan.Exceptions.Add(new ImportProblem(
                    $"Row {row.Number}: {gradeLabel} × size \"{sizeLabel}\" — that sieve size is not "
                    + $"in the catalogue, so {weight:N4} ct could not be placed."));
                plan.SkippedRows++;
                plan.UnplacedRows++;
                plan.UnplacedCarats += Math.Abs(weight);
                if (!plan.UnplacedLabels.Contains(sizeLabel)) plan.UnplacedLabels.Add(sizeLabel);
                continue;
            }

            decimal.TryParse(row[PriceColumn].Trim(), NumberStyles.Float,
                             CultureInfo.InvariantCulture, out decimal price);
            if (price < 0) price = 0;

            plan.Rows.Add(new StockRow(gradeCode, sizeCode, weight, price,
                                       row.Number, gradeLabel, sizeLabel));
        }

        if (plan.Rows.Count == 0)
        {
            plan.Problems.Add(new ImportProblem(
                plan.SkippedRows > 0
                    ? $"All {plan.SkippedRows} holding(s) were rejected. First: {plan.Exceptions[0].Message}"
                    : $"\"{SheetName}\" holds no stock figures that could be read."));
        }
        // A holding that names an unknown grade or sieve size carries carats that would simply
        // vanish -- the import replaces the whole position, so what is not placed is not merely
        // skipped, it is gone. Refusing the file is the only honest answer: importing 3 of 68
        // holdings and reporting success would leave the book looking complete and wrong.
        //
        // A correct workbook reaches here with UnplacedRows = 0, so this cannot block real work.
        // If a size is genuinely new, add it to the Size Master and import again.
        else if (plan.UnplacedRows > 0)
        {
            string named = string.Join(", ", plan.UnplacedLabels.Take(5).Select(z => $"\"{z}\""))
                         + (plan.UnplacedLabels.Count > 5 ? ", …" : "");

            plan.Problems.Add(new ImportProblem(
                $"{plan.UnplacedRows:N0} holding(s) totalling {plan.UnplacedCarats:N4} ct name a grade "
                + $"or sieve size the catalogue does not have ({named}). Importing would place only "
                + $"{plan.Rows.Count:N0} of {plan.Rows.Count + plan.UnplacedRows:N0} holding(s) and "
                + $"lose the rest, so the file is refused. Add the missing entries to the Size "
                + $"Master, or check that this is the right workbook."));
        }

        return plan;
    }

    private static bool IsNumeric(string s) =>
        decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    private static string Key(string s) =>
        string.Join(" ", s.Trim().ToUpperInvariant().Split((char[]?)null,
                                                           StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Normalises a sieve size to one bucket. The stock sheet writes sizes bare ("6.5", "11") while
    /// the catalogue signs them ("+6.5", "+11"), and one label carries Excel's text-forcing
    /// apostrophe ("'+18"). An unsigned size is the positive bucket, which is how the sheet uses it.
    /// Returns null when the text is not a size at all.
    /// </summary>
    public static string? SizeKey(string raw)
    {
        string s = raw.Trim().TrimStart('\'').TrimStart(',').Trim();
        if (s.Length == 0) return null;

        string sign = "";
        if (s.StartsWith('+') || s.EndsWith('+')) sign = "+";
        else if (s.StartsWith('-') || s.EndsWith('-')) sign = "-";

        string core = s.Trim('+', '-').Trim();
        if (!decimal.TryParse(core, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal n))
            return null;

        return (sign.Length == 0 ? "+" : sign) + n.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The catalogue's own codes, keyed the same way, so both sides meet in the middle.</summary>
    public static Dictionary<string, string> SizeMap(IEnumerable<string> codes)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string code in codes)
            if (SizeKey(code) is { } k)
                map[k] = code;
        return map;
    }

    public static string ProblemText(StockImportPlan plan) =>
        string.Join("\n", plan.Problems.Select(p => "  • " + p.Message));

    public static string ExceptionText(StockImportPlan plan, int max = 12)
    {
        var lines = plan.Exceptions.Take(max).Select(x => "  • " + x.Message).ToList();
        if (plan.Exceptions.Count > max)
            lines.Add($"  • … and {plan.Exceptions.Count - max:N0} more");
        return string.Join("\n", lines);
    }
}
