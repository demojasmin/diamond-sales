using System.Globalization;
using System.IO;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace DiamondDesktop;

/// <summary>
/// Reads the stock position out of the client's PRINTED stock sheet — the same figures the Stock
/// report tab draws, arriving as a PDF instead of a workbook.
///
/// A PDF has no cells. It has glyphs at coordinates, and any parser that reads it as a stream of
/// text will happily hand one grade's carats to the row above it. So the table is rebuilt from
/// geometry:
///
///   ROWS    every word whose baseline sits within a couple of points is one row. The sheet prints
///           a grade's label a fraction lower than its own numbers ("DX1" sits 0.6pt below the
///           3.84 beside it), which is exactly the gap that splits a row in two if you round.
///
///   COLUMNS the numbers are centre-aligned, and every column's centres agree to within a tenth of
///           a point, so they cluster cleanly. The size headings ("14+", "1/5", "1/4") sit centred
///           over the WEIGHT column of their block; the column immediately right of each is that
///           size's rate. Nothing here is a hardcoded x — the headings locate their own columns, so
///           a sheet printed at another width or with a fourth size still reads.
///
/// The sheet prints its own subtotals and a TOTAL, and those are used as a checksum: if the rows
/// this reads do not add up to the figures the sheet itself states, the file is REFUSED rather
/// than imported short. That is the whole reason to bother with geometry — a parse that is nearly
/// right is worse than one that admits it failed, because stock imports replace.
/// </summary>
public static class PdfStockFile
{
    /// Same placeholder rule as the workbook: anything under this is not a real holding.
    /// <summary>
    /// What the preview calls a row the sheet printed without a grade name.
    ///
    /// Shown rather than skipped, so the screen matches the paper line for line. It is never a
    /// grade the catalogue could match, which is deliberate: a row carrying carats under this
    /// name stops the import instead of being guessed at.
    /// </summary>
    public const string NoGradeName = "(no grade name)";

    public const decimal Sentinel = 0.001m;

    /// Two words belong to the same printed row when their baselines are within this many points.
    /// The sheet's own rows are 14.3pt apart and its worst label/number offset is 0.7pt, so there
    /// is an order of magnitude of headroom either way.
    private const double RowTolerance = 2.5;

    /// Two numbers belong to the same column when their centres are within this many points. The
    /// sheet's own columns agree to 0.2pt and sit 35pt apart at their closest.
    private const double ColumnTolerance = 6.0;

    private sealed record Word(string Text, double X, double Y)
    {
        public bool IsNumber => decimal.TryParse(Text, NumberStyles.Float | NumberStyles.AllowThousands,
                                                 CultureInfo.InvariantCulture, out _);
        public decimal Number => decimal.Parse(Text, NumberStyles.Float | NumberStyles.AllowThousands,
                                               CultureInfo.InvariantCulture);
    }

    /// <param name="gradeLabelToCode">"1BB" → "NO 1 BB". The printed labels, which are not codes.</param>
    /// <param name="sizeLabelToCode">"1/5" → "0.2". Likewise.</param>
    public static StockImportPlan Plan(string path,
                                       IReadOnlyDictionary<string, string> gradeLabelToCode,
                                       IReadOnlyDictionary<string, string> sizeLabelToCode)
    {
        var plan = new StockImportPlan();

        List<Word> words;
        try
        {
            words = ReadWords(path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            plan.Problems.Add(new ImportProblem(
                $"This file could not be opened as a PDF. {ex.Message}"));
            return plan;
        }

        if (words.Count == 0)
        {
            plan.Problems.Add(new ImportProblem(
                "This PDF holds no readable text. A scan or a photograph of a stock sheet is an "
                + "image, not text, and cannot be imported — export the sheet to PDF from the "
                + "spreadsheet instead of scanning the printout."));
            return plan;
        }

        var allRows = GroupIntoRows(words);

        // ── one table, or several stacked on the page ───────────────────────────
        // A sheet may print more than one table down the page: three sieve sizes, its own totals,
        // then three more sizes below with a second set. The columns land at IDENTICAL x-positions
        // in both, so reading the page as a single table silently adds the lower table's carats to
        // the upper table's sizes -- which is exactly what happened: a sheet whose three columns
        // print 21.35, 64.36 and 18.53 read as 23.88, 65.35 and 18.53, each inflated by the figure
        // sitting under it in the second table.
        //
        // So each heading row starts a new table, and every table gets its own columns, its own
        // sizes and its own arithmetic. A page with one table yields one section and behaves
        // exactly as before.
        var sections = Sections(allRows, sizeLabelToCode);
        if (sections.Count == 0)
        {
            FindSizeColumns(allRows, DataColumns(allRows), sizeLabelToCode, plan, out _);
            return plan;
        }

        var mismatches = new List<Mismatch>();
        int lineNo = 0;

        // The table the orphan-column explanation should look at.
        //
        // `faulted` is the one whose own subtotals disagreed. `firstRead` is the fallback, and it
        // earns its place: when a sieve is missing from the catalogue its column is never claimed
        // at all, so every column that WAS read still sums correctly and only the grand total is
        // short. There is no faulted section then -- but there is very much an orphan column, and
        // naming it is the difference between "add that size" and "this file is unreadable".
        (List<List<Word>> Rows, List<double> Columns, List<SizeColumn> Sizes, int HeadingRow)? faulted = null;
        (List<List<Word>> Rows, List<double> Columns, List<SizeColumn> Sizes, int HeadingRow)? firstRead = null;

        foreach (var rows in sections)
        {
        // ── the columns ─────────────────────────────────────────────────────────
        // Taken from the sheet's own SUBTOTAL line, not from every number on the page. The subtotal
        // sits under each data column by definition, so it names them exactly — while "cluster all
        // the numbers" also picks up the label margin, because these sheets print grades called
        // "2", "3", "4" and "1 BB" whose digits cluster into columns of their own.
        var columns = DataColumns(rows);
        var sizes = FindSizeColumns(rows, columns, sizeLabelToCode, plan, out int headingRow);
        if (sizes.Count == 0) continue;

        foreach (var s in sizes)
            if (!plan.SizeOrder.Contains(s.Code)) plan.SizeOrder.Add(s.Code);

        // ── every row that names a grade ────────────────────────────────────────
        // Left of the FIRST data column is the grade. A sheet printed as two side-by-side panels
        // repeats the label at each panel edge and again at the right margin; those copies are
        // ignored, because reading one would double the holding it sits beside.
        double labelLimit = columns[0] - ColumnTolerance * 2;

        // The sheet's OWN subtotal line carries no grade either, and telling it apart from a
        // holding row whose grade cell was left blank is the whole difficulty here.
        //
        // Position settles it: the subtotal sits directly under the data, so it is the LAST
        // unlabelled line carrying numbers. Everything unlabelled above it is a printed row of
        // the table -- this sheet has two, above "1 MB" -- and those were being swallowed by a
        // rule written when the only unlabelled line was the subtotal.
        int subtotalRow = -1;
        for (int r = 0; r < rows.Count; r++)
        {
            if (r == headingRow) continue;
            var candidate = rows[r];
            if (candidate.Any(w => w.Text.Equals("TOTAL", StringComparison.OrdinalIgnoreCase))) continue;
            if (candidate.Any(w => w.X < labelLimit)) continue;   // it names something
            if (!candidate.Any(w => w.IsNumber)) continue;        // a blank spacer, not a line
            subtotalRow = r;
        }

        int unnamed = 0;

        for (int r = 0; r < rows.Count; r++)
        {
            if (r == headingRow) continue;                 // the sizes, not a grade
            var row = rows[r];
            lineNo++;
            string label = Normalise(string.Join(" ", row.Where(w => w.X < labelLimit).Select(w => w.Text)));

            // Looked for across the WHOLE row, not in the label margin. The foot of these sheets
            // prints "TOTAL" inside the first weight column rather than out at the left, so the
            // label read above comes back EMPTY on that line -- and testing the label alone let the
            // grand total through as an unnamed data row carrying 27,933 ct, which is the rate.
            if (row.Any(w => w.Text.Equals("TOTAL", StringComparison.OrdinalIgnoreCase))) continue;

            bool nameless = label.Length == 0;
            if (nameless)
            {
                if (r == subtotalRow) continue;            // the sheet's own subtotal
                if (!row.Any(w => w.IsNumber)) continue;   // a blank spacer between blocks

                // Named for the screen so the preview matches the paper row for row. Numbered
                // when there is more than one, because two rows sharing a label would collide
                // in Printed and the second would overwrite the first.
                unnamed++;
                label = unnamed == 1 ? NoGradeName : $"{NoGradeName} ({unnamed})";
            }

            // Every cell as printed, before any judgement about what is a holding. This is what the
            // preview renders, and it is the only place a printed 0.00 survives — below, a zero
            // weight yields no row and becomes indistinguishable from a cell nobody wrote in.
            foreach (var size in sizes)
                plan.Printed[(label, size.Code)] =
                    (At(row, size.WeightX), size.PriceX is { } rx ? At(row, rx) : null);

            // Carats on a line the sheet printed no grade against. There is no honest way to place
            // these: the row above is a guess, the row below is a guess, and a guess here puts
            // somebody's stock under the wrong grade with nothing on screen to say so.
            //
            // A row like this printing 0.00 is fine and common -- it is a spacer or a bucket the
            // office keeps on the form. Only weight makes it a problem, and then it stops the
            // import outright rather than being reported as a skipped line, because an import
            // REPLACES and a line silently dropped is stock that quietly ceases to exist.
            if (nameless)
            {
                // Listed either way. The preview exists to be checked against the paper, and a row
                // the paper prints must appear on it -- that is the whole point of GradeOrder.
                if (!plan.GradeOrder.Contains(label)) plan.GradeOrder.Add(label);

                decimal onNamelessRow = sizes.Sum(s => Math.Abs(At(row, s.WeightX) ?? 0m));
                if (onNamelessRow >= Sentinel)
                {
                    plan.Problems.Add(new ImportProblem(
                        $"Line {lineNo} carries {onNamelessRow:N4} ct but the sheet prints no grade "
                        + "name against it, so there is no way to tell which grade the carats belong "
                        + "to. Nothing has been imported. Add the grade name to the sheet and export "
                        + "it again."));
                }
                continue;
            }

            if (GradeCode(label, gradeLabelToCode) is not { } gradeCode)
            {
                // Only a complaint if the line actually carries carats. The sheet's title block and
                // its footer sit in the same left column and name no grade, and refusing a file over
                // "BKC" would make the importer unusable.
                decimal stranded = sizes.Sum(s => At(row, s.WeightX) ?? 0m);
                if (Math.Abs(stranded) < Sentinel)
                {
                    // Empty, and unknown to the catalogue — "-2 BB" and "LB 3" on one of these
                    // sheets. Nothing to import, but the line is on the paper, so it is listed.
                    if (!plan.GradeOrder.Contains(label)) plan.GradeOrder.Add(label);
                    continue;
                }

                plan.Exceptions.Add(new ImportProblem(
                    $"Line {lineNo}: grade \"{label}\" is not in the catalogue and has no alias, so "
                    + $"{stranded:N4} ct could not be placed."));
                plan.SkippedRows++;
                // Holdings, not sheet rows — which is what UnplacedRows is documented to count and
                // what the message compares against. Incrementing by one counted a single unit per
                // GRADE while plan.Rows counts one per CELL, so a grade holding two sizes reported
                // "58 of 59" over a sheet that prints 60 holdings.
                plan.UnplacedRows += sizes.Count(s => Math.Abs(At(row, s.WeightX) ?? 0m) >= Sentinel);
                plan.UnplacedCarats += Math.Abs(stranded);
                if (!plan.UnplacedLabels.Contains(label)) plan.UnplacedLabels.Add(label);
                if (!plan.GradeOrder.Contains(label)) plan.GradeOrder.Add(label);
                continue;
            }

            // Listed whether or not it holds anything. A grade printed at 0.00 across every size
            // yields no holding and would vanish from a preview built from holdings alone.
            if (!plan.GradeOrder.Contains(label)) plan.GradeOrder.Add(label);

            foreach (var size in sizes)
            {
                decimal? weight = At(row, size.WeightX);
                if (weight is not { } w || Math.Abs(w) < Sentinel) continue;

                // A rate with no weight is just this month's price for a bucket the client is out
                // of — printed on every sheet, and not a holding. Zero when the sheet prints no
                // rates at all: replace_imported_stock takes the parcel either way, and a fabricated
                // price would be indistinguishable from a real one on the Stock page afterwards.
                decimal price = size.PriceX is { } px ? At(row, px) ?? 0m : 0m;
                if (price < 0) price = 0;

                plan.Rows.Add(new StockRow(gradeCode, size.Code, w, price,
                                           lineNo, label, size.Label));
            }
        }

        // This table's own subtotals, against what was read from this table's columns.
        firstRead ??= (rows, columns, sizes, headingRow);
        var sectionMismatches = PrintedTotalMismatches(plan, rows, sizes, labelLimit);
        if (sectionMismatches.Count > 0) faulted ??= (rows, columns, sizes, headingRow);
        mismatches.AddRange(sectionMismatches);
        }

        if (plan.Rows.Count == 0)
        {
            plan.Problems.Add(new ImportProblem(
                plan.SkippedRows > 0
                    ? $"All {plan.SkippedRows} holding(s) were rejected. First: {plan.Exceptions[0].Message}"
                    : "This PDF holds no stock figures that could be read. Check that it is a stock "
                      + "sheet and not a sales report or an invoice."));
            return plan;
        }

        // ── the sheet's own arithmetic ──────────────────────────────────────────
        // Run before the unplaced-grade message so that message can say whether the rest of the
        // sheet was read correctly, which is the difference between "add three grades" and "this
        // file is unreadable".
        // The foot of the sheet totals every table on it, so this one is checked against the whole
        // plan rather than against any single section.
        if (GrandTotal(allRows) is { } stated && Math.Abs(plan.TotalCarats - stated) >= 0.005m)
            mismatches.Add(new Mismatch(stated - plan.TotalCarats, plan.UnplacedCarats,
                $"This sheet states a total of {stated:N2} ct but the lines read add up to "
                + $"{plan.TotalCarats:N2} ct. The sheet could not be read reliably, so nothing is "
                + "imported."));

        // A shortfall the unplaced grades fully account for is NOT a failed reading. The lines that
        // could not be placed are exactly the lines missing from the sums, so the parse is sound
        // and the catalogue is the only thing lacking.
        //
        // Reporting both put six problems in front of the user for one cause, five of them saying
        // "the sheet could not be read reliably" about a sheet that had been read perfectly.
        bool accountedFor = plan.UnplacedCarats > 0
                         && mismatches.All(m => Math.Abs(m.Shortfall - m.Unplaced) < 0.005m);

        // Same rule as the workbook importer, and for the same reason: an import REPLACES, so a
        // holding that could not be placed is not skipped, it is gone.
        if (plan.UnplacedRows > 0)
        {
            string named = string.Join(", ", plan.UnplacedLabels.Take(5).Select(z => $"\"{z}\""))
                         + (plan.UnplacedLabels.Count > 5 ? ", …" : "");

            plan.Problems.Add(new ImportProblem(
                $"{plan.UnplacedRows:N0} line(s) totalling {plan.UnplacedCarats:N4} ct name a grade "
                + $"the catalogue does not have ({named}). Importing would place only "
                + $"{plan.Rows.Count:N0} of {plan.Rows.Count + plan.UnplacedRows:N0} line(s) and lose "
                + $"the rest, so the file is refused."
                + (accountedFor
                    // Worth saying plainly. Everything else on the sheet reconciled to its own
                    // printed totals, so this is a catalogue to fill in, not a file to re-export.
                    ? $" The rest of the sheet was read correctly — {plan.Rows.Count:N0} holding(s) "
                      + $"and {plan.TotalCarats:N4} ct, which is exactly the "
                      + $"{plan.TotalCarats + plan.UnplacedCarats:N4} ct the sheet totals, less what "
                      + $"could not be placed. Add {(plan.UnplacedLabels.Count == 1 ? "that grade" : "those grades")} "
                      + "in Master data and import again."
                    : " Add the missing grade, or check that this is the right sheet.")));
        }

        // Only when the sums do NOT reconcile to the unplaced lines. Otherwise they are the same
        // fact restated once per column, in the alarming words reserved for a sheet that could not
        // be read at all.
        if (!accountedFor && mismatches.Count > 0)
        {
            // WHY the sums disagree, when it can be said. The printed total is the authority on
            // whether the reading is complete; this only names the culprit, and only once the
            // checksum has already decided the file is short.
            //
            // It is not a gate of its own, and that was the mistake worth recording: as a separate
            // check it refused a sheet over a stray 1.00 ct column while that same sheet reconciled
            // to its own printed total exactly. A column carrying figures is not necessarily a
            // column carrying stock.
            var orphan = (faulted ?? firstRead) is { } f
                ? OrphanColumn(f.Rows, f.Columns, f.Sizes, f.HeadingRow)
                : null;
            decimal short_ = mismatches.Max(m => m.Shortfall);

            // Recorded as data too. The message below tells the user what happened; this lets the
            // caller offer to fix it without anyone editing the catalogue by hand.
            if (orphan is { Heading: { } h }) plan.UnknownSizes.Add(new UnknownSize(h, short_));

            plan.Problems.Add(orphan is { } lost && lost.Heading is { } head
                ? new ImportProblem(
                    $"This sheet states a total of {plan.TotalCarats + short_:N2} ct but only "
                    + $"{plan.TotalCarats:N2} ct could be read. The {short_:N2} ct missing sits under "
                    + $"the heading \"{head}\", which is not a sieve size this system holds — so that "
                    + "whole column was left out. Nothing has been imported. Add that size in Master "
                    + "data and import again, or check this is the sheet you meant.")
                : new ImportProblem(mismatches[0].Message));

            // Any remaining columns that disagree, once the leading explanation is given.
            foreach (var m in mismatches.Skip(1)) plan.Problems.Add(new ImportProblem(m.Message));
        }

        return plan;
    }

    // ── the checksum ────────────────────────────────────────────────────────────

    /// <summary>
    /// The sheet states its own subtotal under each size and a TOTAL at the foot. Both are compared
    /// against what was read.
    ///
    /// This is the check that makes a geometric parse trustworthy. Every failure mode this reader
    /// has — a row split in two, a number landing in the wrong column, a grade absorbed into its
    /// neighbour — changes one of these sums, so a file that reconciles to the printed figures has
    /// been read correctly in the only sense that matters.
    /// </summary>
    /// <param name="Shortfall">Printed minus read.</param>
    /// <param name="Unplaced">
    /// How much of that shortfall is already explained by lines whose grade the catalogue does not
    /// have. When the two agree the reading is sound and only the catalogue is short.
    /// </param>
    private sealed record Mismatch(decimal Shortfall, decimal Unplaced, string Message);


    /// <summary>
    /// The page split into tables: each heading row starts one, and it runs to the next heading.
    ///
    /// A heading row is one naming two or more sieve sizes -- the same test FindSizeColumns uses to
    /// recognise the one it settles on. Two is the threshold because a single size word can appear
    /// in a title or a note, while two side by side is a header.
    /// </summary>
    private static List<List<List<Word>>> Sections(
        List<List<Word>> rows, IReadOnlyDictionary<string, string> sizeLabelToCode)
    {
        var starts = new List<int>();
        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            if (row.Count(w => SizeCode(w.Text, sizeLabelToCode) is not null) < 2) continue;

            // AND nothing numeric that is not itself a size. Two size-looking words is not enough
            // on these sheets: the grades are called "2", "3", "7" and "-2 MB", so a row of
            // HOLDINGS matches that test and would start a table of its own -- which is what broke
            // the six-size sheet, splitting it at a grade line and reading nothing at all.
            //
            // A heading carries sizes and words like WEIGHT and RATE. A holding carries carats and
            // a rate, and those are numbers no size ever spells.
            if (row.Any(w => w.IsNumber && SizeCode(w.Text, sizeLabelToCode) is null)) continue;

            starts.Add(r);
        }

        var sections = new List<List<List<Word>>>();
        for (int i = 0; i < starts.Count; i++)
        {
            int from = starts[i];
            int to = i + 1 < starts.Count ? starts[i + 1] : rows.Count;
            sections.Add(rows.GetRange(from, to - from));
        }
        return sections;
    }

    /// <summary>
    /// The last TOTAL the sheet prints, which is its grand total across every table on the page.
    ///
    /// Read as the first number to the RIGHT of the word, never at a column x: on a sheet that
    /// prints the label inside the first weight column every number is displaced one place, and
    /// the column rule then returns the neighbouring RATE. That is how a sheet totalling 107.76 ct
    /// came to be compared against 27,933 and refused.
    /// </summary>
    private static decimal? GrandTotal(List<List<Word>> rows)
    {
        decimal? last = null;
        foreach (var row in rows)
        {
            var label = row.FirstOrDefault(w => w.Text.Equals("TOTAL", StringComparison.OrdinalIgnoreCase));
            if (label is null) continue;

            if (row.Where(w => w.IsNumber && w.X > label.X).OrderBy(w => w.X)
                   .Select(w => (decimal?)w.Number).FirstOrDefault() is { } v)
                last = v;
        }
        return last;
    }

    private static List<Mismatch> PrintedTotalMismatches(StockImportPlan plan, List<List<Word>> rows,
                                                         List<SizeColumn> sizes, double labelLimit)
    {
        var found = new List<Mismatch>();
        // What each size lost to a grade the catalogue does not have, so a shortfall can be
        // attributed rather than merely noticed.
        var unplacedBySize = UnplacedBySize(plan, rows, sizes, labelLimit);

        // The subtotal line is the last one carrying numbers in the weight columns but naming no
        // grade, and the TOTAL line names itself.
        var stated = new Dictionary<string, decimal>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            // The foot of the sheet does not print "TOTAL" in the label margin — it prints it in
            // the first size's own column, which is why this looks across the whole row. Reading it
            // as an unlabelled line instead made the grand total masquerade as a size subtotal.
            // A TOTAL line ends this table. Its own figure is the section's, and the sheet's
            // grand total is checked separately against the whole plan -- see GrandTotal.
            if (row.Any(w => w.Text.Equals("TOTAL", StringComparison.OrdinalIgnoreCase))) continue;

            // A subtotal names no grade. Tested the same way the reader tests it — anything in the
            // label margin — because on this sheet six of the grades ARE numbers ("2", "3", "4"…),
            // and "has a non-numeric word" would call those rows unlabelled too.
            if (row.Any(w => w.X < labelLimit)) continue;

            foreach (var size in sizes)
                if (At(row, size.WeightX) is { } v) stated[size.Code] = v;
        }

        foreach (var size in sizes)
        {
            if (!stated.TryGetValue(size.Code, out decimal printed)) continue;

            decimal read = plan.Rows.Where(r => r.SizeCode == size.Code).Sum(r => r.WeightCt);
            if (Math.Abs(read - printed) < 0.005m) continue;

            unplacedBySize.TryGetValue(size.Code, out decimal lost);
            found.Add(new Mismatch(printed - read, lost,
                $"Size {size.Label}: this sheet states {printed:N2} ct but the lines above it add up "
                + $"to {read:N2} ct — a difference of {Math.Abs(read - printed):N2} ct. The sheet "
                + $"could not be read reliably, so nothing is imported."));
        }


        return found;
    }

    /// <summary>
    /// Per size, the carats sitting on lines whose grade the catalogue does not have.
    ///
    /// Re-walked here rather than accumulated during the read, because the read records one figure
    /// per rejected LINE and the subtotals are per COLUMN — and "the sums are short by exactly what
    /// we could not place" is only worth saying if it is true column by column.
    /// </summary>
    private static Dictionary<string, decimal> UnplacedBySize(StockImportPlan plan,
                                                              List<List<Word>> rows,
                                                              List<SizeColumn> sizes,
                                                              double labelLimit)
    {
        var lost = new Dictionary<string, decimal>(StringComparer.Ordinal);
        if (plan.UnplacedRows == 0) return lost;

        foreach (var row in rows)
        {
            string label = Normalise(string.Join(" ", row.Where(w => w.X < labelLimit).Select(w => w.Text)));
            if (!plan.UnplacedLabels.Contains(label)) continue;

            foreach (var size in sizes)
                if (At(row, size.WeightX) is { } v && Math.Abs(v) >= Sentinel)
                    lost[size.Code] = lost.GetValueOrDefault(size.Code) + Math.Abs(v);
        }
        return lost;
    }

    // ── geometry ────────────────────────────────────────────────────────────────

    private static List<Word> ReadWords(string path)
    {
        using var doc = PdfDocument.Open(path);
        var all = new List<Word>();

        // Pages are stacked on one Y axis so a sheet that ran to two pages still groups into rows
        // without page two's first line merging into page one's last.
        double offset = 0;
        foreach (var page in doc.GetPages())
        {
            foreach (var w in NearestNeighbourWordExtractor.Instance.GetWords(page.Letters))
            {
                string text = w.Text.Trim();
                if (text.Length == 0) continue;
                var box = w.BoundingBox;
                all.Add(new Word(text, (box.Left + box.Right) / 2, box.Bottom - offset));
            }
            offset += page.Height;
        }
        return all;
    }

    /// Top to bottom, each row left to right.
    private static List<List<Word>> GroupIntoRows(List<Word> words)
    {
        var rows = new List<List<Word>>();
        foreach (var w in words.OrderByDescending(w => w.Y))
        {
            var row = rows.Count > 0 && Math.Abs(rows[^1][0].Y - w.Y) <= RowTolerance
                ? rows[^1] : AddRow(rows);
            row.Add(w);
        }
        foreach (var row in rows) row.Sort((a, b) => a.X.CompareTo(b.X));
        return rows;

        static List<Word> AddRow(List<List<Word>> rows) { var r = new List<Word>(); rows.Add(r); return r; }
    }

    /// <summary>
    /// Where the data columns are, left to right.
    ///
    /// FIRST CHOICE is the sheet's own subtotal line -- the last row above TOTAL that is nothing
    /// but numbers. It sits under every data column by definition and nowhere else, so it names
    /// the table exactly, with no threshold to tune and no way for the label margin to creep in.
    /// Nothing here knows a size, a column count or an x: the sheet points at its own columns.
    ///
    /// FALLBACK, when a sheet prints no subtotal, is alignment that RECURS. One number at an
    /// x-position is a coincidence; the same x on row after row is a column. Support is counted
    /// only from numbers that have something printed to their left, which is what keeps the label
    /// margin out -- these sheets have grades called "2", "3" and "7", and their digits cluster at
    /// their own x on plenty of rows.
    ///
    /// The fallback is second, not first, and that order was learned: making it primary read a
    /// six-size sheet as 286.61 ct against its own printed 258.84 and invented a rate column for
    /// every size, because a phantom column shifts each heading onto its neighbour.
    /// </summary>
    private static List<double> DataColumns(List<List<Word>> rows)
    {
        int total = rows.FindIndex(r => r.Any(w => w.Text.Equals("TOTAL", StringComparison.OrdinalIgnoreCase)));
        if (total < 0) total = rows.Count;

        for (int i = total - 1; i >= 0; i--)
        {
            var row = rows[i];
            if (row.Count >= 2 && row.All(w => w.IsNumber))
                return Cluster(row.Select(w => w.X));
        }

        return RecurringColumns(rows);
    }

    /// <summary>
    /// Columns inferred from repetition, for a sheet that states no subtotal.
    ///
    /// Better than clustering every number on the page, which was the old fallback: that admitted
    /// any digit printed anywhere, so a grade named "4" became a column of its own.
    /// </summary>
    private static List<double> RecurringColumns(List<List<Word>> rows)
    {
        var dataRows = rows.Where(r => r.Any(w => w.IsNumber)).ToList();
        if (dataRows.Count == 0) return [];

        var centres = Cluster(dataRows.SelectMany(r => r).Where(w => w.IsNumber).Select(w => w.X));

        // Counted only where something is printed to the left. A data column always has at least
        // the grade beside it; the label margin, being leftmost, never does.
        var support = centres.Select(c => dataRows.Count(
            r => r.Any(w => w.IsNumber && Math.Abs(w.X - c) <= ColumnTolerance
                            && r.Any(left => left.X < w.X - ColumnTolerance)))).ToList();

        // A proportion, never a row count, so the rule holds on a sheet of six lines and one of
        // sixty. Low enough that a size only two grades carry still counts.
        int floor = Math.Max(2, (int)Math.Ceiling(dataRows.Count * 0.25));
        var kept = centres.Where((_, i) => support[i] >= floor).ToList();

        // If nothing clears the bar, hand back what we have rather than reporting no columns. The
        // heading match and the printed total both still have to agree, so a poor guess here
        // refuses the file -- it cannot import a wrong one.
        return kept.Count > 0 ? kept : centres;
    }

    /// x-positions collapsed into columns, left to right.
    private static List<double> Cluster(IEnumerable<double> xs)
    {
        var centres = new List<double>();
        foreach (double x in xs.OrderBy(x => x))
            if (centres.Count == 0 || x - centres[^1] > ColumnTolerance)
                centres.Add(x);
        return centres;
    }

    /// <param name="Index">Which data column holds the carats, so its neighbour can be found.</param>
    /// <param name="PriceX">Null on a sheet that prints carats without a rate beside them.</param>
    private sealed record SizeColumn(string Label, string Code, double WeightX, int Index,
                                     double? PriceX = null);

    /// <summary>
    /// Finds the heading row and, from it, which column holds each size's weight and which its rate.
    ///
    /// The heading row is the topmost row naming two or more known sizes — "topmost" because the
    /// sheet repeats nothing above it, and "two or more" so a stray word that happens to read like
    /// a size cannot pass for a heading.
    /// </summary>
    /// <param name="headingRow">
    /// Which row it was. The caller skips it: on one sheet the heading sits in the label margin, so
    /// listing every printed line would otherwise offer "6.5-" as a grade.
    /// </param>
    private static List<SizeColumn> FindSizeColumns(List<List<Word>> rows, List<double> columns,
                                                    IReadOnlyDictionary<string, string> sizeLabelToCode,
                                                    StockImportPlan plan, out int headingRow)
    {
        var found = new List<SizeColumn>();
        headingRow = -1;

        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            var heads = row.Where(w => SizeCode(w.Text, sizeLabelToCode) is not null)
                           .OrderBy(w => w.X).ToList();
            if (heads.Count < 2) continue;
            headingRow = r;

            // Left to right, each heading takes the nearest column nobody has claimed. Nearest
            // alone is not enough: one sheet prints its first heading well left of its own column,
            // near enough to a neighbour's to steal it, and a stolen column silently reads one
            // size's carats as another's.
            var claimed = new HashSet<int>();
            foreach (var head in heads)
            {
                string? code = SizeCode(head.Text, sizeLabelToCode);
                if (code is null || found.Any(s => s.Code == code)) continue;

                int i = NearestFreeColumn(columns, head.X, claimed);
                if (i < 0) continue;
                claimed.Add(i);
                found.Add(new SizeColumn(head.Text, code, columns[i], i));
            }
            break;
        }

        if (found.Count == 0)
        {
            // The conclusion first. This said "No size headings were found on this sheet", which is
            // true and reads as the importer failing — when the real answer is almost always that
            // the file is an invoice, a receipt or a report that happens to live in the same folder
            // as the stock sheets.
            plan.Problems.Add(new ImportProblem(
                "This does not look like a stock sheet. It has no row naming the sieve sizes "
                + "(\"14+\", \"1/5\", \"1/4\", \"6.5-\" and so on), which is how the carat columns "
                + "are found. If you meant an invoice, a receipt or a report, those cannot be "
                + "imported as stock — choose the sheet the office prints for the stock count."));
            return found;
        }

        // ── which sizes carry a rate, and which are weight alone ────────────────
        // A column nobody claimed, sitting immediately right of a size's own column, is that size's
        // rate. Some sheets print carats and rate in pairs; others print carats only, and inventing
        // a rate column on those would read the NEXT size's weights as this one's prices.
        var weightColumns = found.Select(s => s.Index).ToHashSet();
        return found
            .Select(s => s with
            {
                PriceX = s.Index + 1 < columns.Count && !weightColumns.Contains(s.Index + 1)
                    ? columns[s.Index + 1] : (double?)null,
            })
            .OrderBy(s => s.WeightX)
            .ToList();
    }

    /// <summary>
    /// The catalogue code a printed grade names, or null.
    ///
    /// Tried as printed, then with the spaces taken out. One sheet sets the grade as a single word,
    /// "1BB"; another sets the same grade as two, "1 BB", with a gap no wider than the one inside
    /// "LB 2" — so the spacing cannot be used to tell a one-word label from a two-word one, and
    /// both have to key the same. The spaced form is tried first so "LC 1" and "LC 2" stay distinct
    /// grades rather than collapsing towards each other.
    /// </summary>
    private static string? GradeCode(string label, IReadOnlyDictionary<string, string> map)
    {
        if (map.TryGetValue(label, out string? direct)) return direct;
        return map.TryGetValue(label.Replace(" ", ""), out string? tight) ? tight : null;
    }

    /// <summary>
    /// The catalogue code a printed size names, or null.
    ///
    /// Tried as printed first, then through the same normalisation the workbook importer uses —
    /// which is what lets one sheet write "6.5-" and "11+" where another writes "-6.5" and "+11"
    /// for the same two buckets. The notation is the client's; the bucket is the same.
    /// </summary>
    private static string? SizeCode(string text, IReadOnlyDictionary<string, string> map)
    {
        if (map.TryGetValue(Normalise(text), out string? direct)) return direct;
        return StockFileImport.SizeKey(text) is { } key && map.TryGetValue(key, out string? viaKey)
            ? viaKey : null;
    }

    private static int NearestFreeColumn(List<double> columns, double x, HashSet<int> claimed)
    {
        int best = -1;
        double bestGap = double.MaxValue;
        for (int i = 0; i < columns.Count; i++)
        {
            if (claimed.Contains(i)) continue;
            double gap = Math.Abs(columns[i] - x);
            if (gap < bestGap) { bestGap = gap; best = i; }
        }
        return best;
    }

    /// The number sitting in one column on one row, or null when the cell is blank.
    private static decimal? At(List<Word> row, double columnX)
    {
        foreach (var w in row)
            if (w.IsNumber && Math.Abs(w.X - columnX) <= ColumnTolerance) return w.Number;
        return null;
    }

    /// <summary>
    /// Collapses the runs of spaces that a printed label arrives in: the sheet sets "LC 1" as two
    /// words and "TOP co" as two more, and they have to key the same as the map spells them.
    ///
    /// A leading period or comma goes too. One sheet sets its "-2 BB" and "-2 MB" rows as ".-2 BB"
    /// and ",-2 MB" — an artefact of how the cell is formatted, not part of the name, and it is
    /// shown to the user as well as matched on. StockFileImport.SizeKey has stripped the same two
    /// characters off sieve sizes since the workbook importer was written; a leading MINUS is left
    /// alone, because on these sheets that one means something.
    /// </summary>

    /// <param name="Carats">What would be dropped. The number is the argument, not the adjective.</param>
    /// <param name="Heading">Whatever is printed over it, if anything.</param>
    private sealed record Orphan(decimal Carats, string? Heading);

    /// <summary>
    /// The first column carrying figures that neither a size nor a rate accounts for, or null.
    ///
    /// Rate columns are excluded because their figures are prices, not carats -- and their
    /// magnitude would swamp any threshold. Everything else that holds numbers on the data rows is
    /// either a size we placed or a size we did not, and the second kind is what this finds.
    ///
    /// The TOTAL and subtotal lines are skipped: they restate the columns above them, so counting
    /// them would double the evidence and, on a sheet with a grand total in its own column, invent
    /// an orphan out of the checksum itself.
    /// </summary>
    private static Orphan? OrphanColumn(List<List<Word>> rows, List<double> columns,
                                        List<SizeColumn> sizes, int headingRow)
    {
        var accounted = sizes.Select(s => s.WeightX)
            .Concat(sizes.Where(s => s.PriceX is not null).Select(s => s.PriceX!.Value))
            .ToList();

        int total = rows.FindIndex(r => r.Any(w => w.Text.Equals("TOTAL", StringComparison.OrdinalIgnoreCase)));

        foreach (double c in columns)
        {
            if (accounted.Any(a => Math.Abs(a - c) <= ColumnTolerance)) continue;

            decimal carats = 0m;
            for (int r = 0; r < rows.Count; r++)
            {
                if (r == headingRow) continue;
                if (total >= 0 && r >= total) continue;             // the grand total restates it
                if (rows[r].All(w => w.IsNumber)) continue;         // a subtotal line, likewise
                if (At(rows[r], c) is { } v) carats += Math.Abs(v);
            }

            if (carats < Sentinel) continue;                        // an empty column is not a loss

            string? heading = headingRow >= 0
                ? rows[headingRow].Where(w => Math.Abs(w.X - c) <= ColumnTolerance * 2)
                                  .Select(w => w.Text).FirstOrDefault()
                : null;
            return new Orphan(carats, heading);
        }
        return null;
    }

    public static string Normalise(string s) =>
        string.Join(" ", s.Trim().TrimStart('.', ',').Trim()
                          .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static string ProblemText(StockImportPlan plan) =>
        string.Join("\n", plan.Problems.Select(p => "  • " + p.Message));
}
