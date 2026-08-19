using System.Globalization;
using System.IO;

namespace DiamondDesktop;

/// <summary>A column heading naming a sieve size the catalogue lacks, and what stands under it.</summary>
public sealed record UnknownSize(string Label, decimal Carats);

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

    /// <summary>
    /// Column headings that name a sieve size the catalogue does not have, with the carats standing
    /// under each.
    ///
    /// Carried as DATA rather than only as a message so the caller can offer to add them. A message
    /// can only be read; this can be acted on -- which is the difference between "a developer must
    /// write a migration" and "the owner ticks yes".
    /// </summary>
    public List<UnknownSize> UnknownSizes { get; } = [];

    /// <summary>
    /// The size columns in the order the source lays them out, left to right.
    ///
    /// Recorded because the rows themselves cannot be trusted to reveal it: they are built grade by
    /// grade, so the first grade with a holding decides the order everything after it is discovered
    /// in. On one sheet that put "-6.5" third, behind two columns printed to its right.
    ///
    /// Empty from the workbook importer, whose consumer has no column order to preserve.
    /// </summary>
    public List<string> SizeOrder { get; } = [];

    /// <summary>
    /// Every grade line the source PRINTS, in order, including the ones holding nothing.
    ///
    /// Rows only carry holdings, and a bucket at 0.00 is not one — importing it would create a
    /// phantom parcel. But a preview built from holdings alone silently drops the empty lines, and
    /// somebody checking the screen against the paper then finds the paper has rows the app does
    /// not. Kept apart from Rows precisely so what is DISPLAYED and what is IMPORTED can differ.
    /// </summary>
    public List<string> GradeOrder { get; } = [];

    /// <summary>
    /// What the source actually printed in each cell, keyed by the labels it printed them under.
    ///
    /// Rows cannot answer this. A cell reading 0.00 yields no holding — importing it would create a
    /// parcel of nothing — so in Rows it is indistinguishable from a cell that was left blank. On
    /// the paper the two are not the same thing at all: 0.00 is a count that came to nothing, blank
    /// is a bucket nobody counted, and a preview that renders both as empty is not the sheet.
    ///
    /// Empty from the workbook importer, whose consumer shows no cells.
    /// </summary>
    public Dictionary<(string Grade, string Size), (decimal? Ct, decimal? Rate)> Printed { get; } = [];

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
                NoteUnknownSize(plan.UnknownSizes, sizeLabel, Math.Abs(weight));
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
    /// <summary>
    /// A printed size label reduced to what should actually be STORED as a code.
    ///
    /// Leading apostrophes and commas are how a spreadsheet protects a cell from being read as a
    /// formula, not part of the sieve -- the stock workbooks write "'+18" and ",-2 MB". The label
    /// keeps its punctuation for DISPLAY, because that is what is printed on the paper the user
    /// checks against; the code must not, or the catalogue ends up with a sieve called "'+18".
    ///
    /// TrimStart with a SET strips a run of them, where two chained single-character calls strip
    /// one each and let ",,+18" through. public.sieve_key strips the same set as of 0037.
    /// </summary>
    public static string CleanCode(string raw) => raw.Trim().TrimStart('\'', ',', ' ').Trim();

    public static string? SizeKey(string raw)
    {
        string s = CleanCode(raw);
        if (s.Length == 0) return null;

        // "1/5" is 0.2, and the catalogue calls that bucket "0.2". Division rather than a lookup
        // table, so it holds for any fraction a sheet prints -- including ones nobody has thought
        // of yet, which is the whole point of not naming sizes in code.
        //
        // This mirrors public.sieve_key (0034) exactly. It has to: that function is what decides
        // whether add_size returns an existing row or creates one, so a C# rule that disagreed
        // would offer to add a sieve the database then refuses -- or worse, agree to add one the
        // database happily twins.
        int slash = s.IndexOf('/');
        if (slash > 0)
        {
            if (Num(s[..slash]) is { } over && Num(s[(slash + 1)..]) is { } under && under != 0m)
                return "+" + Plain(decimal.Round(over / under, 4, MidpointRounding.AwayFromZero));
            return null;
        }

        string sign = "";
        if (s.StartsWith('+') || s.EndsWith('+')) sign = "+";
        else if (s.StartsWith('-') || s.EndsWith('-')) sign = "-";

        if (Num(s.Trim('+', '-')) is not { } n) return null;

        return (sign.Length == 0 ? "+" : sign) + Plain(n);

        static decimal? Num(string t) =>
            decimal.TryParse(t.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal v)
                ? v : null;
    }

    /// <summary>
    /// A number as a sieve key spells it: four decimal places at most, no trailing zeros.
    ///
    /// "6.50" and "6.5" are one sieve, but decimal keeps whatever scale it was parsed with, so
    /// ToString would key them apart. Postgres does this with trim_scale(round(x, 4)); four places
    /// is the carat precision this system already uses, so two sieves differing later than that
    /// are not two sieves.
    /// </summary>
    private static string Plain(decimal n) => n.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>
    /// Records a sieve size the sheet names and the catalogue lacks, so the caller can OFFER to add
    /// it rather than only report it.
    ///
    /// Merged on the sieve key, never on the text. A workbook that writes "1/5" on one row and
    /// "0.2" on another is naming ONE missing sieve, and offering it twice would walk the user into
    /// creating exactly the twin this rule exists to prevent. The first spelling seen is the one
    /// shown, because that is what is printed on the paper they will check it against.
    /// </summary>
    public static void NoteUnknownSize(List<UnknownSize> into, string label, decimal carats)
    {
        string? key = SizeKey(label);
        int at = into.FindIndex(u => SizeKey(u.Label) == key);
        if (at < 0)
            into.Add(new UnknownSize(label, carats));
        else
            into[at] = into[at] with { Carats = into[at].Carats + carats };
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
