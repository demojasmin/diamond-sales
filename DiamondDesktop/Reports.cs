using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using DiamondDesktop.Data;
using Microsoft.Win32;

namespace DiamondDesktop;

/// RPT-001 · export and RPT-002 · invoice print. Both use what WPF and the BCL already provide —
/// no Excel interop, no PDF library, no reporting engine.
public static class Reports
{
    /// <summary>
    /// RPT-001. Writes whatever a grid is showing to CSV, which Excel opens natively.
    /// Columns come from the grid itself, so the file matches the screen exactly (AC 1).
    /// </summary>
    public static string? ExportGrid(DataGrid grid, string suggestedName)
    {
        if (grid.ItemsSource is null) return "Nothing to export";

        var dialog = new SaveFileDialog
        {
            Filter = "CSV (Excel)|*.csv",
            FileName = $"{suggestedName}-{DateTime.Now:yyyyMMdd}.csv",   // date-stamped, per the story
        };
        if (dialog.ShowDialog() != true) return null;

        // Every column, not just the bound ones. A DataGridTemplateColumn — STATUS on Invoices and
        // Stock, BUCKET on Receivables — is not a DataGridBoundColumn, so filtering on that type
        // dropped it from the file silently: a CANCELLED invoice exported identical to a POSTED one,
        // same amount, no marker. ClipboardContentBinding is WPF's own answer to "what is this
        // column's value when it leaves the screen"; DataGridBoundColumn sets it from Binding
        // automatically, and the template columns declare it in the markup.
        var columns = grid.Columns
            .Select(c => (Header: c.Header?.ToString() ?? "",
                          Path: (c.ClipboardContentBinding as System.Windows.Data.Binding)?.Path.Path))
            .Where(c => c.Path is not null)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", columns.Select(c => Quote(c.Header))));

        foreach (var row in grid.ItemsSource)
            sb.AppendLine(string.Join(",", columns.Select(c => Quote(ValueOf(row, c.Path!)))));

        // Yesterday's export is usually still open in Excel, which locks it. The callers are plain
        // void click handlers, so an escaping IOException kills the app — and with it whatever
        // invoice was half-typed on the entry screen.
        try { File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.UTF8); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"Could not write {dialog.FileName} — {e.Message}";
        }
        return $"Exported to {dialog.FileName}";
    }

    /// <summary>RPT-002. Builds the bill as a FlowDocument and hands it to the system print dialog
    /// — which includes "Microsoft Print to PDF", so this covers print and PDF in one path.
    /// Both arguments are views: every figure printed is the one Postgres computed.</summary>
    public static string? PrintInvoice(VInvoice invoice, List<VSalesLine> lines, string companyName)
    {
        var dialog = new PrintDialog();
        // No printer installed makes ShowDialog itself throw, and this is called from an async void
        // handler where that ends the process rather than the print job.
        try { if (dialog.ShowDialog() != true) return null; }
        catch (Exception e) { return $"No printer available — {e.Message}"; }

        var doc = BuildApprovalNote(invoice, lines, new Letterhead(Name: companyName));

        try { dialog.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator, $"Invoice {invoice.InvoiceNo}"); }
        catch (Exception e) { return $"Could not print {invoice.InvoiceNo} — {e.Message}"; }
        return $"Sent {invoice.InvoiceNo} to the printer";
    }

    /// <summary>
    /// The printed letterhead. Only the name is configurable — it is the one piece the app already
    /// holds (app_config.company_name), and it is what differs between the two databases.
    ///
    /// The rest is the stationery: it is the same on every pad in the drawer, it is not invoice
    /// data, and putting it in app_config would mean a migration and five more Settings fields to
    /// maintain for values that change roughly never. When they do change, they change here.
    /// </summary>
    public sealed record Letterhead(
        string Name = "PRIYA GEMS",
        string Trade = "IMPORTERS,  EXPORTERS,  MANUFACTURERS OF DIAMONDS",
        string Address = "A-101/B, Tanvi's Diamond Ind. Premises Estate, S.V. Road, Dahisar (E), Mumbai - 400 068.",
        string Email = "priyagems13@gmail.com",
        string Tel = "3392 7439",
        string Qbc = "7439",
        string Gstin = "27AGRPB2357E1ZH",
        string Jurisdiction = "Subject to Mumbai Jurisdiction.");

    /// <summary>
    /// The approval note, laid out as the printed pad it replaces.
    ///
    /// Sized to A4 explicitly rather than left to the printer: a FlowDocument with no PageWidth
    /// reflows to whatever the driver reports, so the same note came out with different line breaks
    /// on two machines and the preview matched neither. 794 x 1123 is A4 at 96dpi, and the padding
    /// is the 12mm the pad leaves around its own frame.
    ///
    /// Every figure on it comes from the invoice. Nothing is computed here — the weights and rates
    /// are printed exactly as sales_line stores them.
    /// </summary>
    public static FlowDocument BuildApprovalNote(VInvoice invoice, List<VSalesLine> lines, Letterhead head)
    {
        var doc = new FlowDocument
        {
            PageWidth = A4Width,
            PageHeight = A4Height,
            PagePadding = new Thickness(A4Margin),

            // The usable width, NOT double.PositiveInfinity. Infinity is right for a document that
            // must never split into text columns, and catastrophic here: it makes the content area
            // unbounded, so every GridUnitType.Star column resolves to nothing and the table lays
            // out one character per line. One column exactly as wide as the page does both jobs.
            ColumnWidth = Usable,

            // Without this the text column is allowed to GROW past ColumnWidth, the content area
            // stops being a known width, and GridUnitType.Star resolves to nothing -- which prints
            // as one character per line down a column two pixels wide. Every column below is a
            // fixed width for the same reason: a printed form is a fixed layout, and star widths in
            // a FlowDocument table are one property away from collapsing silently.
            IsColumnWidthFlexible = false,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11,
            TextAlignment = TextAlignment.Left,
        };

        // ── the letterhead ─────────────────────────────────────────────────
        var banner = new Table { CellSpacing = 0, Margin = new Thickness(0) };
        banner.Columns.Add(new TableColumn { Width = new GridLength(150) });
        banner.Columns.Add(new TableColumn { Width = new GridLength(Usable - 300) });
        banner.Columns.Add(new TableColumn { Width = new GridLength(150) });
        var bannerRows = new TableRowGroup();
        banner.RowGroups.Add(bannerRows);

        var top = new TableRow();
        top.Cells.Add(Plain(Mark()));
        top.Cells.Add(Plain(new Paragraph(new Underline(new Bold(new Run("APPROVAL NOTE"))))
        { TextAlignment = TextAlignment.Center, FontSize = 13, Margin = new Thickness(0, 6, 0, 0) }));
        top.Cells.Add(Plain(new Paragraph(new Run($"Tel.: {head.Tel}\nQBC : {head.Qbc}"))
        { TextAlignment = TextAlignment.Right, FontSize = 10, Margin = new Thickness(0, 4, 0, 0) }));
        bannerRows.Rows.Add(top);
        doc.Blocks.Add(banner);

        doc.Blocks.Add(new Paragraph(new Bold(new Run(head.Name)))
        {
            FontSize = 30,
            TextAlignment = TextAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x4E, 0x8A)),
            Margin = new Thickness(0, 2, 0, 2),
        });

        // The trade line sits on a tint on the pad, which is what separates the name from the
        // address without a rule between them.
        doc.Blocks.Add(new Paragraph(new Bold(new Run(head.Trade)))
        {
            FontSize = 10,
            TextAlignment = TextAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(0xDE, 0xE7, 0xF2)),
            Padding = new Thickness(0, 3, 0, 3),
            Margin = new Thickness(0, 0, 0, 4),
        });

        doc.Blocks.Add(new Paragraph(new Run($"{head.Address}\nEmail : {head.Email}"))
        { FontSize = 9.5, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 12) });

        // ── who it is going to ─────────────────────────────────────────────
        // The pad rules a blank for each of these. A value we hold is printed on the rule; one we
        // do not — a mobile number the app has never stored — stays a rule to be written on.
        doc.Blocks.Add(new Paragraph(new Run($"No. {invoice.InvoiceNo ?? "DRAFT"}          "
                                             + "Date : " + invoice.InvoiceDate.ToString(@"dd  \/  MM  \/  yyyy", CultureInfo.InvariantCulture)))
        { TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 0, 8) });

        doc.Blocks.Add(Ruled("To,", invoice.BuyerName, 6));
        doc.Blocks.Add(TwoUp($"Through  {invoice.BrokerName ?? ""}", "Mob.:", ruleRight: true));

        doc.Blocks.Add(new Paragraph(new Run(
            "Please receive the following goods on approval for Export / Local Sale / Assortment / Mfgr."))
        { FontSize = 10, Margin = new Thickness(0, 8, 0, 6) });

        // ── the goods ──────────────────────────────────────────────────────
        var table = new Table { CellSpacing = 0 };
        // 40 + 336 + 88 + 108 + 132 = Usable. Stated as figures rather than as stars so the sum
        // is checkable by eye against the page.
        foreach (double w in new[] { 40d, Usable - 368, 88d, 108d, 132d })
            table.Columns.Add(new TableColumn { Width = new GridLength(w) });

        var rows = new TableRowGroup();
        table.RowGroups.Add(rows);

        rows.Rows.Add(NoteRow(true,
            "S.\nNo", "PARTICULARS", "WEIGHT", "RATE per\nCarat\n₹ / $", "REMARKS"));

        int n = 0;
        foreach (var l in lines)
        {
            n++;
            rows.Rows.Add(NoteRow(false,
                n.ToString(CultureInfo.InvariantCulture),
                $"{l.GradeCode}   {l.SizeCode}",
                N(l.GrossWeightCt),
                N(l.PricePerCt),
                l.Remark ?? ""));
        }

        // The pad has eleven ruled lines whether or not they are used, and a note that stops after
        // two rows invites a third being added later in a different hand. Ruled to the same depth.
        for (int i = n + 1; i <= NoteRows; i++)
            rows.Rows.Add(NoteRow(false, i.ToString(CultureInfo.InvariantCulture), "", "", "", ""));

        doc.Blocks.Add(table);

        // ── the acknowledgement, and who signs ─────────────────────────────
        var feet = new Table { CellSpacing = 0, Margin = new Thickness(0, 6, 0, 0) };
        feet.Columns.Add(new TableColumn { Width = new GridLength(Usable - 200) });
        feet.Columns.Add(new TableColumn { Width = new GridLength(200) });
        var feetRows = new TableRowGroup();
        feet.RowGroups.Add(feetRows);

        var ack = new TableRow();
        var terms = new Paragraph { FontSize = 9, Margin = new Thickness(0) };
        terms.Inlines.Add(new Run($"Acknowledgement of entrustment as per the\nconditions of reserve.\n{head.Jurisdiction}\n"));
        // GSTIN sits with the terms it belongs to, as it does on the pad -- not orphaned below.
        terms.Inlines.Add(new Bold(new Run($"GSTIN : {head.Gstin}")) { FontSize = 9.5 });
        ack.Cells.Add(Plain(terms));
        ack.Cells.Add(Plain(new Paragraph(new Bold(new Run($"For  {head.Name}")))
        { FontSize = 13, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 4, 0, 0) }));
        feetRows.Rows.Add(ack);
        doc.Blocks.Add(feet);

        doc.Blocks.Add(new Paragraph { Margin = new Thickness(0, 0, 0, 30) });   // room to sign in

        doc.Blocks.Add(TwoUp("Thru / Receiver's Sign", "Authorised Sign.", ruleRight: false));

        return doc;
    }

    /// The pad's ruled depth. Fewer lines than this and the rules are drawn anyway.
    private const int NoteRows = 11;

    /// A4 at 96dpi, the margin the pad leaves around its own frame, and what is left to print in.
    private const double A4Width = 794, A4Height = 1123, A4Margin = 45;
    private const double Usable = A4Width - (2 * A4Margin);

    /// A borderless cell, for laying two things side by side rather than for tabulating.
    private static TableCell Plain(Block content) => new(content) { Padding = new Thickness(0) };

    /// The logo mark. Not the printed one — three squares on their corner, which is what it reads
    /// as at this size and is honest about being a stand-in rather than a copy.
    private static BlockUIContainer Mark()
    {
        var canvas = new Canvas { Width = 108, Height = 52 };
        (double x, double y, double s, byte r, byte g, byte b)[] gems =
        [
            (2, 10, 22, 0x2B, 0x5F, 0xA8), (26, 2, 16, 0x4E, 0x8F, 0xD0), (44, 12, 12, 0x8F, 0xBE, 0xE6),
        ];
        foreach (var (x, y, s, r, g, b) in gems)
        {
            var gem = new System.Windows.Shapes.Rectangle
            {
                Width = s, Height = s,
                Fill = new SolidColorBrush(Color.FromRgb(r, g, b)),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(45),
            };
            // A Canvas positions by its attached properties. Margin is ignored here, which is why
            // the three gems piled up in the corner the first time.
            Canvas.SetLeft(gem, x);
            Canvas.SetTop(gem, y);
            canvas.Children.Add(gem);
        }
        return new BlockUIContainer(canvas) { Margin = new Thickness(0) };
    }

    /// <summary>
    /// A label, then whatever we know, then a rule running to the edge of the space — the pad's
    /// "To, ________" line.
    ///
    /// The rule is the paragraph's own bottom border, not underlined spaces. Padding a Run out to a
    /// guessed width is how a ruled line ends up two characters short on one printer and wrapping
    /// onto a second line on the next; a border is measured by the layout and always reaches
    /// exactly as far as the column does.
    /// </summary>
    private static Paragraph Ruled(string label, string? value, double after) =>
        new(new Run(string.IsNullOrWhiteSpace(value) ? label : $"{label}  {value}"))
        {
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, after),
            Padding = new Thickness(0, 0, 0, 3),
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(0, 0, 0, 0.8),
        };

    /// <summary>
    /// Two fields on one line — "Through ____  Mob.: ____", or a ruled signature beside a plain
    /// caption. <paramref name="ruleRight"/> is false where the pad prints a caption rather than a
    /// blank to write on.
    /// </summary>
    private static Table TwoUp(string left, string right, bool ruleRight)
    {
        var t = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 6) };
        t.Columns.Add(new TableColumn { Width = new GridLength(Usable - 252) });
        t.Columns.Add(new TableColumn { Width = new GridLength(12) });     // the gap between them
        t.Columns.Add(new TableColumn { Width = new GridLength(240) });
        var g = new TableRowGroup();
        t.RowGroups.Add(g);

        var row = new TableRow();
        row.Cells.Add(Plain(Ruled(left, null, 0)));
        row.Cells.Add(Plain(new Paragraph()));
        row.Cells.Add(Plain(ruleRight
            ? Ruled(right, null, 0)
            : new Paragraph(new Run(right))
              { TextAlignment = TextAlignment.Right, Margin = new Thickness(0) }));
        g.Rows.Add(row);
        return t;
    }

    /// One ruled row of the goods table. Every side bordered, which is what makes it read as a grid
    /// rather than as a list — CellSpacing is 0, so neighbours share the rule.
    private static TableRow NoteRow(bool header, params string[] cells)
    {
        var row = new TableRow();
        foreach (string cell in cells)
            row.Cells.Add(new TableCell(new Paragraph(header ? new Bold(new Run(cell)) : new Run(cell))
            {
                TextAlignment = header ? TextAlignment.Center : TextAlignment.Left,
                Margin = new Thickness(0),
                FontSize = header ? 9.5 : 10.5,
            })
            {
                // Data rows are deep enough to be written in. Eleven of them at the header's
                // height would be a table nobody can fill without a fine pen.
                Padding = new Thickness(5, header ? 4 : 10, 5, header ? 4 : 10),
                BorderBrush = Brushes.Black,
                BorderThickness = new Thickness(0.8),
            });
        return row;
    }

    /// <summary>
    /// The bill itself, with no printer involved.
    ///
    /// Split out of PrintInvoice so it can be checked: the document used to be built inside a
    /// method that opens a PrintDialog first, so nothing about the one artefact a buyer physically
    /// receives could be tested without a printer attached. Every figure below reaches a customer.
    /// </summary>
    public static FlowDocument BuildInvoice(VInvoice invoice, List<VSalesLine> lines, string companyName)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(50),
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize = 12,
            ColumnWidth = double.PositiveInfinity,
        };

        doc.Blocks.Add(Heading(companyName, 20));
        doc.Blocks.Add(Heading($"{invoice.DocType} · {invoice.InvoiceNo ?? "DRAFT"}", 14));

        // The bill is the complete record of the invoice now. The drawer on screen shows a summary
        // and sends the rest here, so everything it stopped showing has to be on this page.
        doc.Blocks.Add(new Paragraph(new Run(
            $"Date {invoice.InvoiceDate:dd-MM-yyyy}\nBuyer {invoice.BuyerName}\n" +
            $"Terms {invoice.TermsDays} days · Due {invoice.DueDate:dd-MM-yyyy}\n" +
            $"Salesperson {invoice.Salesperson ?? "—"}"
            + (string.IsNullOrWhiteSpace(invoice.BrokerName) ? "" : $"\nBroker {invoice.BrokerName}")))
        { Margin = new Thickness(0, 0, 0, 14) });

        // Rejection and Ex rate are on the line and were not printed before. A bill that cannot be
        // checked against the parcel it describes is not much of a bill.
        // Remark is typed per line on the entry screen and stored on sales_line, but until now no
        // screen or document read it back — the note was captured and then unreachable. The bill is
        // where it belongs: it is the one place that claims to be the complete record of the line.
        var table = new Table { CellSpacing = 0 };
        for (int i = 0; i < 10; i++) table.Columns.Add(new TableColumn());
        var body = new TableRowGroup();
        table.RowGroups.Add(body);

        body.Rows.Add(Row(bold: true, "Grade", "Size", "Weight ct", "Selection ct", "Rejection ct",
                                      "Price/ct", "Ex rate", "Less 1/2", "Amount", "Remark"));
        foreach (var l in lines)
            body.Rows.Add(Row(false, l.GradeCode, l.SizeCode, N(l.GrossWeightCt), N(l.SelectionCt),
                              N(l.RejectionCt), N(l.PricePerCt), N(l.ExRate),
                              $"{l.Less1Pct}/{l.Less2Pct}", N(l.Amount), l.Remark ?? ""));

        // A totals row under the columns it totals, so the carats can be checked by eye.
        body.Rows.Add(Row(bold: true, "Total", "", N(lines.Sum(l => l.GrossWeightCt)),
                          N(lines.Sum(l => l.SelectionCt)), N(lines.Sum(l => l.RejectionCt)),
                          "", "", "", N(invoice.AmountTotal), ""));

        doc.Blocks.Add(table);

        var summary = new Table { CellSpacing = 0, Margin = new Thickness(0, 14, 0, 0) };
        summary.Columns.Add(new TableColumn());
        summary.Columns.Add(new TableColumn());
        var totals = new TableRowGroup();
        summary.RowGroups.Add(totals);

        totals.Rows.Add(Row(false, "Carats sold", N(invoice.CaratsSold)));
        totals.Rows.Add(Row(false, "Blended rate / ct", invoice.BlendedRate is { } rate ? N(rate) : "—"));
        totals.Rows.Add(Row(false, "Broker %", N(invoice.BrokerPct)));
        totals.Rows.Add(Row(false, "Broker payable", N(invoice.BrokerPayable)));
        totals.Rows.Add(Row(bold: true, "Invoice total", N(invoice.AmountTotal)));
        totals.Rows.Add(Row(false, "Received", N(invoice.Received)));
        totals.Rows.Add(Row(bold: true, "Outstanding", N(invoice.Outstanding)));

        // Cost is stamped at posting (0019) and only exists for invoices posted through this app.
        // "Cost not available" rather than a zero: a nil cost would print as a 100% margin on a
        // parcel whose purchase price simply was never recorded.
        totals.Rows.Add(Row(false, "Cost of goods",
            invoice.CostTotal is { } cost ? N(cost) : "Cost not available"));
        totals.Rows.Add(Row(bold: true, "Margin",
            invoice.Margin is { } m ? N(m) : "Cost not available"));
        doc.Blocks.Add(summary);

        if (invoice.BrokerPct > 0)
            doc.Blocks.Add(new Paragraph(new Run(
                $"Broker {invoice.BrokerPct}% is already deducted from the amount above."))
            { FontSize = 10, Foreground = System.Windows.Media.Brushes.Gray });

        return doc;
    }

    private static Paragraph Heading(string text, double size)
        => new(new Bold(new Run(text))) { FontSize = size, Margin = new Thickness(0, 0, 0, 6) };

    private static TableRow Row(bool bold, params string[] cells)
    {
        var row = new TableRow();
        foreach (var cell in cells)
            row.Cells.Add(new TableCell(new Paragraph(bold ? new Bold(new Run(cell)) : new Run(cell)))
            {
                Padding = new Thickness(4),
                BorderBrush = System.Windows.Media.Brushes.LightGray,
                BorderThickness = new Thickness(0, 0, 0, bold ? 1 : 0.5),
            });
        return row;
    }

    private static string N(decimal value) => value.ToString("N2", CultureInfo.InvariantCulture);

    /// <summary>
    /// CSV escaping, plus the formula guard. Excel treats a cell opening with = + - @ (or a control
    /// character) as a formula, so a buyer named <c>=cmd|'/c calc'!A1</c> — and buyer, broker and
    /// remark are all free text the app itself accepts — executed on open. An apostrophe in front
    /// makes Excel show the text and evaluate nothing.
    ///
    /// Numbers are left exactly as they are: <c>-1500.00</c> opens with '-' and would otherwise be
    /// quoted into a string, which is the one thing an export of money must never do.
    /// </summary>
    private static string Quote(string? value)
    {
        value ??= "";

        if (value.Length > 0 && Risky.Contains(value[0])
            && !decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
            value = "'" + value;

        return value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }

    /// The characters Excel and LibreOffice read as "this cell is a formula".
    private static readonly char[] Risky = ['=', '+', '-', '@', '\t', '\r'];

    private static string ValueOf(object row, string path)
    {
        object? current = row;
        foreach (var part in path.Split('.'))
        {
            current = current?.GetType().GetProperty(part)?.GetValue(current);
            if (current is null) return "";
        }
        return current is decimal d ? d.ToString("0.00", CultureInfo.InvariantCulture)
             : current is DateOnly date ? date.ToString("yyyy-MM-dd")
             : current.ToString() ?? "";
    }
}
