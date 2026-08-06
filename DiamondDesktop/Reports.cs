using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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

        try { dialog.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator, $"Invoice {invoice.InvoiceNo}"); }
        catch (Exception e) { return $"Could not print {invoice.InvoiceNo} — {e.Message}"; }
        return $"Sent {invoice.InvoiceNo} to the printer";
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
