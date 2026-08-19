using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DiamondDesktop;

/// <summary>
/// Everything the PDF reader took off a stock sheet, before any of it is written.
///
/// A stock import replaces a whole position, and this one is read out of a print rather than out of
/// cells — so the figures are shown back in the shape the sheet prints them, grade by grade with
/// each size's carats beside its rate, for somebody to hold against the paper. The window writes
/// nothing and decides nothing; it returns whether the user recognised the numbers.
/// </summary>
public partial class PdfStockPreview : Window
{
    private bool _accepted;

    private PdfStockPreview() => InitializeComponent();

    /// <summary>Returns true when the figures were confirmed.</summary>
    /// <param name="sizeLabel">
    /// Turns a catalogue size code back into what the sheet printed. Grades need no such thing:
    /// the plan carries the printed grade labels themselves, which is the only way a line the
    /// catalogue has no code for can be shown at all.
    /// </param>
    public static bool Confirm(Window owner, StockImportPlan plan, string fileName,
                               Func<string, string> sizeLabel)
    {
        var w = new PdfStockPreview
        {
            Owner = owner,
            Title = "Check the sheet",
        };

        w.Subhead.Text = $"{fileName} · {plan.Rows.Count:N0} holding(s) across "
                       + $"{plan.GradeCount:N0} grade(s)";
        // The sheet's own second total line: everything, and what it averages. Same weighted
        // average as the columns above, over the whole parcel — the sheet prints 29,816.
        //
        // Carats alone when the sheet states no rates. "avg 0 /ct · value 0.00" was arithmetic on
        // figures that were never on the page, and a zero reads as a measurement rather than as an
        // absence — the same reason an untraded cell above is left blank instead of showing 0.00.
        w.FooterNote.Text = $"{plan.TotalCarats:N4} ct"
            + (plan.TotalValue == 0m || plan.TotalCarats == 0m
                ? " · this sheet states no rates"
                : $" · avg {plan.TotalValue / plan.TotalCarats:N0} /ct · value {Money.Short(plan.TotalValue)}");

        // The parse reconciles to the sheet's own subtotals, or the plan would not be valid — the
        // reader refuses a sheet whose lines do not add up to what it prints. Saying so here is the
        // difference between "we read something" and "we read it correctly".
        w.ChecksumText.Text =
            $"These lines add up to {plan.TotalCarats:N2} ct, which is the total this sheet prints "
            + "for itself. Every size subtotal agrees too, so nothing on the page was missed or "
            + "read twice.";

        w.Draw(plan, sizeLabel);

        // Deliberately well short of the screen. A window that grows to fill the display is not
        // a dialog any more, and these sheets run to 24 rows and six sizes. Past the cap the
        // ScrollViewer takes over in BOTH directions, so no row or column is ever cut off --
        // scrolling to reach a figure is a small cost, not finding it at all is not.
        var screen = SystemParameters.WorkArea;
        w.MaxWidth = Math.Min(screen.Width - 80, 1100);
        w.MaxHeight = screen.Height * 0.7;

        w.ShowDialog();
        return w._accepted;
    }

    private void Draw(StockImportPlan plan, Func<string, string> sizeLabel)
    {
        // The sheet's own column order, not the order the rows happened to be built in. Those
        // differ: rows are made grade by grade, so the first grade holding stock decides what is
        // discovered first — which put "-6.5" third on a sheet that prints it first.
        var sizes = plan.SizeOrder.Count > 0
            ? plan.SizeOrder.Where(c => plan.Rows.Any(r => r.SizeCode == c)).ToList()
            : plan.Rows.Select(r => r.SizeCode).Distinct().ToList();
        // Every line the sheet PRINTS, in its order — including the ones holding nothing. Built
        // from holdings alone, this dropped "-2 BB", "LB 3" and "14+" from a 24-row sheet, and the
        // whole point of the window is that it can be held against the paper.
        var grades = plan.GradeOrder.Count > 0
            ? plan.GradeOrder
            : plan.Rows.Select(r => r.GradeLabel).Distinct().ToList();

        // Some sheets print carats only. Six columns of "0" for a rate the sheet never stated is
        // not the sheet shown back — it is an invention, and a reader checking figures against
        // paper has to work out that the zeros mean nothing.
        bool rates = plan.Printed.Count > 0
            ? plan.Printed.Values.Any(c => c.Rate is not null)
            : plan.Rows.Any(r => r.PricePerCt != 0m);
        int span = rates ? 2 : 1;

        Table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        foreach (var _ in sizes)
            for (int c = 0; c < span; c++)
                Table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });

        int row = 0;
        Table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Cell("Grade", 0, row, header: true, right: false);
        for (int i = 0; i < sizes.Count; i++)
        {
            Cell(sizeLabel(sizes[i]) + " ct", 1 + i * span, row, header: true);
            if (rates) Cell("rate", 2 + i * span, row, header: true);
        }

        foreach (string grade in grades)
        {
            row++;
            Table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            // Matched on the label the sheet prints, not on a catalogue code — a line the
            // catalogue has no grade for still has to appear, and has no code to be found by.
            Cell(grade, 0, row, right: false);

            for (int i = 0; i < sizes.Count; i++)
            {
                // Straight from what the sheet printed, so a cell reading 0.00 shows 0.00 and a
                // cell left blank stays blank. Reading these off the holdings instead rendered
                // both as empty — the sheet prints 0.00 in dozens of cells and they disappeared.
                decimal? ct, rate;
                if (plan.Printed.TryGetValue((grade, sizes[i]), out var printed))
                    (ct, rate) = printed;
                else
                {
                    var hit = plan.Rows.FirstOrDefault(r => r.GradeLabel == grade && r.SizeCode == sizes[i]);
                    (ct, rate) = (hit?.WeightCt, hit?.PricePerCt);
                }

                Cell(ct is { } c ? c.ToString("N2") : "", 1 + i * span, row);
                if (rates) Cell(rate is { } v ? v.ToString("N0") : "", 2 + i * span, row, muted: true);
            }
        }

        // The totals, both of them, exactly as the sheet prints them at its own foot: carats under
        // each size and the rate beside it.
        //
        // That rate is a WEIGHTED average -- the parcel's value over its weight -- not the average
        // of the rates in the column. On this sheet the two differ by thousands a carat, because
        // 53.97 ct at 30,000 and 0.16 ct at 19,000 do not carry equal weight in what the parcel is
        // worth. Checked against the printed figures: 29,567 / 28,550 / 34,659, all to the rupee.
        row++;
        Table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Cell("TOTAL", 0, row, header: true, right: false);
        for (int i = 0; i < sizes.Count; i++)
        {
            var forSize = plan.Rows.Where(r => r.SizeCode == sizes[i]).ToList();
            decimal ct = forSize.Sum(r => r.WeightCt);
            decimal value = forSize.Sum(r => r.WeightCt * r.PricePerCt);

            Cell($"{ct:N2}", 1 + i * span, row, header: true);
            if (rates) Cell(ct == 0 ? "" : $"{value / ct:N0}", 2 + i * span, row, header: true);
        }
    }

    private void Cell(string text, int column, int row, bool header = false, bool muted = false,
                      bool right = true)
    {
        var block = new TextBlock
        {
            Text = text,
            Padding = new Thickness(9, 5, 9, 5),
            TextAlignment = right ? TextAlignment.Right : TextAlignment.Left,
            FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal,
            FontFamily = right ? (FontFamily)FindResource("MonoFont") : FontFamily,
        };
        if (muted) block.SetResourceReference(ForegroundProperty, "TextMutedBrush");

        var border = new Border
        {
            Child = block,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        if (header) border.SetResourceReference(Border.BackgroundProperty, "Surface2Brush");

        Grid.SetColumn(border, column);
        Grid.SetRow(border, row);
        Table.Children.Add(border);
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        _accepted = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();       // never confirms an import by accident
    }
}
