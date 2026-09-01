using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    /// <param name="head">
    /// Whose note this is. Defaults to the printed pad's own letterhead.
    ///
    /// It used to take a bare company NAME, and both callers handed it the application's title --
    /// so the note went out headed "Diamond Sales & Inventory" over Priya Gems' address, telephone,
    /// email and GSTIN. A letterhead is one identity: the name cannot be supplied separately from
    /// the address it sits above without the two contradicting each other.
    /// </param>
    public static string? PrintInvoice(VInvoice invoice, List<VSalesLine> lines, Letterhead? head = null)
        => PrintApprovalNotes([(invoice, lines)], head);

    /// <summary>
    /// One document, ONE APPROVAL NOTE PER BUYER, in the order they were selected.
    ///
    /// It used to be one call per buyer, so two buyers meant two print dialogs and two separate
    /// documents — two jobs to collect from the printer, and no way to save them as a single PDF.
    /// One dialog, one paginator, a page break before every note after the first.
    ///
    /// EACH NOTE IS BUILT BY BuildApprovalNote, UNCHANGED. Nothing about the template knows there
    /// is more than one: a buyer's page is the same page it was, carrying that buyer's own lines
    /// and nobody else's, because the lines were divided before they got here.
    ///
    /// The FIRST note's document IS the document. Its page size, padding, column width and font
    /// are the note's own, and reusing it rather than declaring a second set means the composite
    /// can never drift from the single-buyer case — which is also why one buyer still comes out
    /// exactly as it always did, one page, no wrapper.
    /// </summary>
    public static string? PrintApprovalNotes(
        IReadOnlyList<(VInvoice Invoice, List<VSalesLine> Lines)> notes, Letterhead? head = null)
    {
        if (notes.Count == 0) return "Nothing to print";

        var dialog = new PrintDialog();
        // No printer installed makes ShowDialog itself throw, and this is called from an async void
        // handler where that ends the process rather than the print job.
        try { if (dialog.ShowDialog() != true) return null; }
        catch (Exception e) { return $"No printer available — {e.Message}"; }

        var doc = BuildApprovalNotes(notes, head);
        string what = notes.Count == 1
            ? $"Invoice {notes[0].Invoice.InvoiceNo}"
            : $"{notes.Count} approval notes";

        try { dialog.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator, what); }
        catch (Exception e) { return $"Could not print {what} — {e.Message}"; }

        return notes.Count == 1
            ? $"Sent {notes[0].Invoice.InvoiceNo} to the printer"
            : $"Sent {notes.Count} approval notes to the printer, one page each";
    }

    /// <summary>
    /// The notes as one printable document. Split out of PrintApprovalNotes so the page breaks can
    /// be checked without a printer.
    /// </summary>
    public static FlowDocument BuildApprovalNotes(
        IReadOnlyList<(VInvoice Invoice, List<VSalesLine> Lines)> notes, Letterhead? head = null)
    {
        var stationery = head ?? new Letterhead();
        var doc = BuildApprovalNote(notes[0].Invoice, notes[0].Lines, stationery);

        for (int i = 1; i < notes.Count; i++)
        {
            var next = BuildApprovalNote(notes[i].Invoice, notes[i].Lines, stationery);

            // MOVED, not copied. A Block belongs to one parent, so it has to leave the document it
            // was built in before it can join this one — and taking a snapshot first is what stops
            // the collection being edited while it is walked.
            var section = new Section { BreakPageBefore = true, Margin = new Thickness(0) };
            foreach (var block in next.Blocks.ToList())
            {
                next.Blocks.Remove(block);
                section.Blocks.Add(block);
            }
            doc.Blocks.Add(section);
        }

        return doc;
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
            PagePadding = new Thickness(MarginSide, MarginTop, MarginSide, MarginBottom),

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
            // Arial, as the reference sets it. Segoe UI is Windows' screen face and set the note
            // half a millimetre narrower per line, so the ruled columns and the text in them came
            // off two different drawings.
            FontFamily = new FontFamily("Arial"),
            FontSize = 16,
            Foreground = Ink,
            TextAlignment = TextAlignment.Left,
        };

        // ── the letterhead ─────────────────────────────────────────────────
        var banner = new Table { CellSpacing = 0, Margin = new Thickness(0) };
        banner.Columns.Add(new TableColumn { Width = new GridLength(132) });
        banner.Columns.Add(new TableColumn { Width = new GridLength(Usable - 264) });
        banner.Columns.Add(new TableColumn { Width = new GridLength(132) });
        var bannerRows = new TableRowGroup();
        banner.RowGroups.Add(bannerRows);

        var top = new TableRow();
        // The left cell is deliberately empty: the reference reserves the width so the title sits
        // on the page's centre line rather than the centre of what is left beside the telephone.
        top.Cells.Add(Plain(new Paragraph()));
        top.Cells.Add(Plain(new Paragraph(new Underline(new Bold(new Run("APPROVAL NOTE"))))
        { TextAlignment = TextAlignment.Center, FontSize = 20, Margin = new Thickness(0, 2, 0, 0) }));
        top.Cells.Add(Plain(new Paragraph(new Run($"Tel.: {head.Tel}" + Environment.NewLine + $"O.BC : {head.Qbc}"))
        { TextAlignment = TextAlignment.Right, FontSize = 17.5, Margin = new Thickness(0) }));
        bannerRows.Rows.Add(top);
        doc.Blocks.Add(banner);

        // ── the mark beside the wordmark ───────────────────────────────────
        // One row, not a centred stack: the reference sets the mark 128 square at the wordmark's
        // left, and the two read as one lockup only while they sit on the same line.
        var lockup = new Table { CellSpacing = 0, Margin = new Thickness(0, 6, 0, 0) };
        lockup.Columns.Add(new TableColumn { Width = new GridLength(128) });
        lockup.Columns.Add(new TableColumn { Width = new GridLength(20) });   // the reference's gap
        lockup.Columns.Add(new TableColumn { Width = new GridLength(Usable - 148) });
        var lockupRows = new TableRowGroup();
        lockup.RowGroups.Add(lockupRows);

        var lockupRow = new TableRow();
        lockupRow.Cells.Add(Plain(Mark()));
        lockupRow.Cells.Add(Plain(new Paragraph()));

        var name = new Section { Margin = new Thickness(0) };
        name.Blocks.Add(new Paragraph(new Bold(new Run(head.Name)))
        {
            FontSize = 56,
            TextAlignment = TextAlignment.Center,
            Foreground = Wordmark,
            Margin = new Thickness(0),
        });
        // The trade line sits on a tint on the pad, which is what separates the name from the
        // address without a rule between them.
        name.Blocks.Add(new Paragraph(new Bold(new Run(head.Trade)))
        {
            FontSize = 14.5,
            TextAlignment = TextAlignment.Center,
            Background = Band,
            Padding = new Thickness(6, 3, 6, 4),
            Margin = new Thickness(0, 8, 0, 0),
        });
        lockupRow.Cells.Add(Plain(name));
        lockupRows.Rows.Add(lockupRow);
        doc.Blocks.Add(lockup);

        doc.Blocks.Add(new Paragraph(new Run($"{head.Address}" + Environment.NewLine + $"Email : {head.Email}"))
        { FontSize = 14, LineHeight = 21, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 8, 0, 0) });

        // ── who it is going to ─────────────────────────────────────────────
        // The pad rules a blank for each of these. A value we hold is printed on the rule; one we
        // do not — a mobile number the app has never stored — stays a rule to be written on.
        // DATE ONLY, right-aligned above "To,". The pad has no invoice-number field -- the number
        // this app assigns is its own, and printing it here would put a figure on the note that the
        // office has never seen on one. The reference is the source of truth for what is on it.
        var when = new Paragraph { TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        when.Inlines.Add(new Run("Date : "));
        when.Inlines.Add(new Run(
            invoice.InvoiceDate.ToString(@"dd  \/  MM  \/  yyyy", CultureInfo.InvariantCulture))
        { Foreground = Data });
        doc.Blocks.Add(when);

        doc.Blocks.Add(Ruled("To,", invoice.BuyerName, 0, top: 12));
        doc.Blocks.Add(TwoUp("Through", "Mob.:", ruleRight: true, top: 14,
                             leftValue: invoice.BrokerName));

        doc.Blocks.Add(new Paragraph(new Run(
            "Please receive the following goods on approval for Export / Local Sale / Assortment / Mfgr."))
        { FontSize = 14.5, Margin = new Thickness(0, 9, 0, 0) });

        // ── the goods ──────────────────────────────────────────────────────
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 6, 0, 0) };

        // The reference's own column shares, applied to the usable width:
        //
        //     S. No 7.5%   PARTICULARS 42%   WEIGHT 17%   RATE 17%   REMARKS 16.5%
        //
        // PARTICULARS takes what is LEFT rather than its own 42%, so the five columns always sum
        // to the page exactly whatever the margins are -- a rounded share leaves a hairline of
        // white down the right edge that reads as a printing fault.
        double[] share = [0.075, 0, 0.17, 0.17, 0.165];
        double rest = Usable * (1 - (0.075 + 0.17 + 0.17 + 0.165));
        for (int c = 0; c < share.Length; c++)
            table.Columns.Add(new TableColumn
            { Width = new GridLength(c == 1 ? rest : Usable * share[c]) });

        // The watermark, pre-composited into a bitmap the shape of the table and painted with
        // every ImageBrush default -- no Viewport, no TileMode, no Opacity.
        //
        // The obvious way to write this is one brush holding the logo, positioned with a Viewport.
        // That silently destroys the print: a brush that paints only PART of its bounding box makes
        // the Microsoft Print to PDF driver emit a ZERO-BYTE file. No exception and no failed-job
        // notification -- the app says "Sent to the printer" and the user gets a PDF that will not
        // open. Measured, not guessed: a full-box brush prints, the same brush with a Viewport does
        // not, and opacity turned out to be innocent. XPS serialisation of either is fine, so this
        // only ever appears on paper.
        //
        // Composing the mark into the bitmap instead means the brush has nothing left to configure.
        if (WatermarkOrNull() is { } watermark)
            table.Background = new ImageBrush(watermark);

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
        var terms = new Paragraph { FontSize = 15.5, LineHeight = 20, Margin = new Thickness(0) };
        terms.Inlines.Add(new Run($"Acknowledgement of entrustment as per the\nconditions of reserve.\n{head.Jurisdiction}\n"));
        // GSTIN sits with the terms it belongs to, as it does on the pad -- not orphaned below.
        terms.Inlines.Add(new Bold(new Run($"GSTIN : {head.Gstin}")));
        ack.Cells.Add(Plain(terms));
        var signOff = new Paragraph
        {
            FontSize = 22,
            Foreground = SignOff,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(0, 2, 0, 0),
        };
        signOff.Inlines.Add(new Run("For "));
        signOff.Inlines.Add(new Bold(new Run(head.Name)));
        ack.Cells.Add(Plain(signOff));
        feetRows.Rows.Add(ack);
        doc.Blocks.Add(feet);

        doc.Blocks.Add(new Paragraph { Margin = new Thickness(0, 0, 0, 34) });   // room to sign in

        doc.Blocks.Add(TwoUp("Thru / Receiver's Sign", "Authorised Sign.", ruleRight: false));

        return doc;
    }

    /// The pad's ruled depth. Fewer lines than this and the rules are drawn anyway.
    private const int NoteRows = 11;

    /// How deep each ruled row is above and below its text.
    ///
    /// This is the ONE figure that decides whether the note is one sheet or two: the letterhead,
    /// the eleven rules, the acknowledgement and the signatures are all fixed, so whatever is left
    /// of the page is divided between eleven rows. At the reference's 16pt it is 10 -- 13 was
    /// right while the text was 12pt and pushed the signatures onto a second sheet at 16.
    private const double RowPad = 10;

    /// The watermark's printed size, square, as the reference sets it.
    private const double Watermark = 270;

    /// Roughly how deep the ruled table comes out at eleven rows. Only the watermark uses it, and
    /// only to keep itself square -- a note long enough to change this is already a second sheet.
    private const double TableDepth = 524;

    /// A4 at 96dpi. The reference sets its own page box in millimetres -- 9mm top, 8mm sides,
    /// 7mm bottom -- and a CSS px IS a WPF device-independent pixel (both 1/96 inch), so every
    /// size taken off the reference carries over as the same number. 1mm = 96/25.4 px.
    private const double A4Width = 794, A4Height = 1123;
    private const double Mm = 96 / 25.4;
    private const double MarginTop = 9 * Mm, MarginSide = 8 * Mm, MarginBottom = 7 * Mm;
    private const double Usable = A4Width - (2 * MarginSide);

    /// The reference's palette. Named rather than repeated so the note cannot drift a shade at a
    /// time: every rule, every letter and both blues below are one of these four.
    private static readonly SolidColorBrush Ink = Frozen(0x1B, 0x2A, 0x6B);        // all text and rules
    private static readonly SolidColorBrush Wordmark = Frozen(0x2A, 0x56, 0xB5);   // PRIYA GEMS
    private static readonly SolidColorBrush Band = Frozen(0xB7, 0xD3, 0xEF);       // the trade tint
    private static readonly SolidColorBrush SignOff = Frozen(0x2A, 0x55, 0xA5);    // "For PRIYA GEMS"

    /// What the desk TYPED, as against the stationery it is typed onto.
    ///
    /// The pad separates the two the same way and has always done: the form is printed, the
    /// entries are written on it in pen. Printing both in one colour loses that -- a buyer's name
    /// reads as part of the letterhead, and a weight reads as part of the ruling. This is the one
    /// line to change to re-colour every value on the note.
    private static readonly SolidColorBrush Data = Frozen(0x11, 0x11, 0x11);

    /// Frozen because these are shared across every note printed in the session, and a frozen brush
    /// is the one kind WPF may use from any thread without cloning it first.
    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// The letterhead's logo, or null if it could not be loaded.
    ///
    /// Null rather than throwing, and loaded on demand rather than in a static field: this runs
    /// inside an async void click handler, where an exception is not caught by anything and ends
    /// the process -- taking a half-typed invoice with it. A note that prints with a blank corner
    /// is a bad note; a note that closes the app is a lost afternoon.
    /// </summary>
    private static BitmapImage? LogoOrNull()
    {
        if (_logo is not null || _logoTried) return _logo;
        _logoTried = true;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri(
                "pack://application:,,,/DiamondDesktop;component/Assets/priya-gems-logo.png",
                UriKind.Absolute);
            img.CacheOption = BitmapCacheOption.OnLoad;   // decode now, so a later failure cannot surprise a printer
            img.EndInit();
            img.Freeze();
            _logo = img;
        }
        catch { _logo = null; }
        return _logo;
    }
    private static BitmapImage? _logo;
    private static bool _logoTried;

    /// <summary>
    /// The watermark, drawn once into a bitmap shaped like the table it sits behind.
    ///
    /// The mark is composed INTO this bitmap -- at the reference's 270 square, centred, its top 18%
    /// of the way down -- rather than positioned by the brush. See the note at its use: a brush
    /// that paints only part of its box does not print.
    ///
    /// Rendered at twice its printed size so it stays clean at a printer's resolution rather than
    /// the screen's, and converted to Bgr24 so it carries no alpha channel at all.
    /// </summary>
    private static BitmapSource? WatermarkOrNull()
    {
        if (_watermark is not null || _watermarkTried) return _watermark;
        _watermarkTried = true;
        try
        {
            if (LogoOrNull() is not { } logo) return null;

            const double scale = 2;
            int w = (int)Math.Round(Usable * scale), h = (int)Math.Round(TableDepth * scale);
            double side = Watermark * scale;

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
                dc.PushOpacity(0.13);                              // the reference's own value
                dc.DrawImage(logo, new Rect((w - side) / 2, 0.18 * h, side, side));
                dc.Pop();
            }

            var rendered = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(visual);

            // Bgr24 has no alpha channel to misread. Pbgra32 would be fully opaque here anyway, but
            // "opaque in every pixel" and "cannot express transparency" are not the same promise.
            var opaque = new FormatConvertedBitmap(rendered, PixelFormats.Bgr24, null, 0);
            opaque.Freeze();
            _watermark = opaque;
        }
        catch { _watermark = null; }
        return _watermark;
    }
    private static BitmapSource? _watermark;
    private static bool _watermarkTried;

    /// A borderless cell, for laying two things side by side rather than for tabulating.
    private static TableCell Plain(Block content) => new(content) { Padding = new Thickness(0) };

    /// <summary>
    /// The letterhead's mark, 128 square as the reference sets it.
    ///
    /// A BlockUIContainer rather than an InlineUIContainer: this sits in its own table cell beside
    /// the wordmark, and an inline image would be laid out on the wordmark's baseline -- which at
    /// 56pt puts a 128pt mark most of the way off the top of the page.
    ///
    /// Falls back to an empty block when the logo will not load, so the note still prints.
    /// </summary>
    private static Block Mark()
    {
        if (LogoOrNull() is not { } logo) return new Paragraph { Margin = new Thickness(0) };

        return new BlockUIContainer(new Image
        {
            Source = logo,
            Width = 128,
            Height = 128,
            Stretch = Stretch.Uniform,   // the reference's object-fit: contain
        })
        { Margin = new Thickness(0) };
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
    private static Paragraph Ruled(string label, string? value, double after, double top = 0)
    {
        var line = new Paragraph
        {
            FontSize = 16,
            Margin = new Thickness(0, top, 0, after),
            Padding = new Thickness(0, 0, 0, 3),
            BorderBrush = Ink,
            BorderThickness = new Thickness(0, 0, 0, 1.5),
        };

        // Two runs, not one interpolated string: "To," is printed on the pad and the buyer's name
        // is written on it, so they are not the same kind of thing and do not take the same colour.
        line.Inlines.Add(new Run(label));
        if (!string.IsNullOrWhiteSpace(value))
            line.Inlines.Add(new Run($"  {value}") { Foreground = Data });
        return line;
    }

    /// <summary>
    /// Two fields on one line — "Through ____  Mob.: ____", or a ruled signature beside a plain
    /// caption. <paramref name="ruleRight"/> is false where the pad prints a caption rather than a
    /// blank to write on.
    /// </summary>
    /// <param name="leftValue">
    /// What was typed into the left-hand field, kept SEPARATE from its label.
    ///
    /// The broker used to be interpolated into <paramref name="left"/> before it got here, so the
    /// whole line arrived as one string and the name printed in the stationery's colour while every
    /// other typed value printed in the data colour. A label and a value are not the same kind of
    /// thing and cannot share a run.
    /// </param>
    private static Table TwoUp(string left, string right, bool ruleRight, double top = 0,
                               string? leftValue = null)
    {
        var t = new Table { CellSpacing = 0, Margin = new Thickness(0, top, 0, 0) };
        t.Columns.Add(new TableColumn { Width = new GridLength(Usable - 252) });
        t.Columns.Add(new TableColumn { Width = new GridLength(12) });     // the gap between them
        t.Columns.Add(new TableColumn { Width = new GridLength(240) });
        var g = new TableRowGroup();
        t.RowGroups.Add(g);

        var row = new TableRow();
        row.Cells.Add(Plain(Ruled(left, leftValue, 0)));
        row.Cells.Add(Plain(new Paragraph()));
        row.Cells.Add(Plain(ruleRight
            ? Ruled(right, null, 0)
            : new Paragraph(new Run(right))
              { FontSize = 16, TextAlignment = TextAlignment.Right, Margin = new Thickness(0) }));
        g.Rows.Add(row);
        return t;
    }

    /// One ruled row of the goods table. Every side bordered, which is what makes it read as a grid
    /// rather than as a list — CellSpacing is 0, so neighbours share the rule.
    private static TableRow NoteRow(bool header, params string[] cells)
    {
        var row = new TableRow();
        bool centreFirst = false;
        foreach (string cell in cells)
        {
            centreFirst = row.Cells.Count == 0;
            // NOT bold. The reference sets the whole head row at normal weight -- the rules are
            // what separate it from the body, and bold on top of them reads as a spreadsheet
            // rather than as the pad this replaces.
            // The row number is printed on the pad; what sits beside it is written in. The head
            // row is all stationery, so it keeps the form's colour throughout.
            row.Cells.Add(new TableCell(new Paragraph(new Run(cell)
            { Foreground = header || centreFirst ? Ink : Data })
            {
                // The S. No column is read down its left edge on the reference, header included.
                // Everything else in the head row is centred over its column.
                TextAlignment = centreFirst ? TextAlignment.Left
                              : header ? TextAlignment.Center
                              : TextAlignment.Left,
                Margin = new Thickness(0),
                FontSize = 16,
            })
            {
                // Deep enough to be WRITTEN IN, which is the whole job of this table: it goes out
                // as paper and comes back filled in by hand. 13 is the most generous depth that
                // still fits the header, eleven rows, the acknowledgement and the signatures on ONE
                // sheet -- the pad is one sheet, and a note continuing overleaf is a note whose
                // second half goes missing.
                Padding = centreFirst
                    ? new Thickness(8, header ? 5 : RowPad, 4, header ? 5 : RowPad)
                    : new Thickness(6, header ? 5 : RowPad, 6, header ? 5 : RowPad),
                BorderBrush = Ink,
                BorderThickness = new Thickness(1.6),
            });
            centreFirst = false;
        }
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
