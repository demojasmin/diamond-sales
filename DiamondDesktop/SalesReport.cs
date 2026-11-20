using System.Windows;
using System.Windows.Controls;
using DiamondDesktop.Data;

namespace DiamondDesktop;

/// <summary>One sold line on the Sales report, with its invoice's buyer, broker and payment state.</summary>
public sealed record SalesReportRow(
    long InvoiceId, string InvoiceNo, DateOnly Date, string Buyer, string Broker, string Grade, string Size,
    decimal WeightCt, decimal SelectionCt, decimal RejectionCt, decimal PricePerCt, decimal ExRate,
    decimal Less1Pct, decimal Less2Pct, decimal BrokerPct, decimal Amount, decimal BrokerCost,
    string Payment, string? Remark)
{
    /// Selling rate per selected carat after every deduction -- Amount / Selection.
    public decimal NetRate => SelectionCt == 0 ? 0 : Math.Round(Amount / SelectionCt, 2);
}

/// <summary>A group on the Sales report summary (by buyer, grade, size, broker or month).</summary>
public sealed record SalesReportGroup(
    string Name, int Invoices, int Lines, decimal WeightCt, decimal SelectionCt, decimal RejectionCt,
    decimal Amount, decimal BrokerCost, decimal AvgRate, decimal SharePct);

/// <summary>The figures the Sales report shows. Pure, so it can be checked without a database.</summary>
public static class SalesReportCalc
{
    /// <summary>
    /// Joins posted lines to their invoices. A line whose invoice is not in the list (it cannot be,
    /// both come from the same database) is kept with blank party names rather than dropped.
    /// </summary>
    public static List<SalesReportRow> Rows(IEnumerable<VSalesLine> lines, IEnumerable<VInvoice> invoices)
    {
        var byId = invoices.GroupBy(i => i.InvoiceId).ToDictionary(g => g.Key, g => g.First());
        return lines.Select(l =>
        {
            byId.TryGetValue(l.InvoiceId, out var inv);
            return new SalesReportRow(
                l.InvoiceId, l.InvoiceNo ?? "", l.InvoiceDate,
                inv?.BuyerName ?? "", inv?.BrokerName ?? "",
                GradeNames.Short(l.GradeCode), SizeNames.Short(l.SizeCode),
                l.GrossWeightCt, l.SelectionCt, l.RejectionCt, l.PricePerCt, l.ExRate,
                l.Less1Pct, l.Less2Pct, l.BrokerPct, l.Amount,
                Math.Max(l.AmountPreBroker - l.Amount, 0),
                Payment(inv), l.Remark);
        })
        .OrderBy(r => r.Date).ThenBy(r => r.InvoiceNo, StringComparer.Ordinal).ToList();
    }

    public static string Payment(VInvoice? inv) =>
        inv is null ? "" : inv.Outstanding <= 0 ? "Paid" : inv.IsOverdue ? "Overdue" : "Pending";

    public static List<SalesReportGroup> Group(IReadOnlyList<SalesReportRow> rows, string by)
    {
        decimal total = rows.Sum(r => r.Amount);
        Func<SalesReportRow, string> key = by switch
        {
            "Grade"  => r => r.Grade,
            "Size"   => r => r.Size,
            "Broker" => r => r.Broker.Length == 0 ? "No broker" : r.Broker,
            "Month"  => r => r.Date.ToString("yyyy-MM"),
            _        => r => r.Buyer,
        };
        return rows.GroupBy(key).Select(g =>
        {
            decimal sel = g.Sum(r => r.SelectionCt), amt = g.Sum(r => r.Amount);
            return new SalesReportGroup(
                g.Key, g.Select(r => r.InvoiceId).Distinct().Count(), g.Count(),
                g.Sum(r => r.WeightCt), sel, g.Sum(r => r.RejectionCt), amt, g.Sum(r => r.BrokerCost),
                sel == 0 ? 0 : Math.Round(amt / sel, 2),
                total == 0 ? 0 : Math.Round(amt / total * 100, 1));
        })
        .OrderBy(g => by == "Month" ? g.Name : "").ThenByDescending(g => by == "Month" ? 0 : g.Amount)
        .ToList();
    }
}

public partial class MainWindow
{
    private List<SalesReportRow> _srAll = [];
    private List<VInvoice> _srInvoices = [];
    private bool _srLoading;

    private const string SrAllBuyers = "All buyers", SrAllGrades = "All grades";

    /// Opens on this month, the period the office most often asks about.
    private void SalesReportDefaults()
    {
        if (SrFrom.SelectedDate is not null) return;
        ApplyRange("MONTH");
    }

    /// From and To for a named period. Financial year runs April to March, as on the Dashboard.
    public static (DateTime From, DateTime To) SalesReportRange(string tag, DateTime today)
    {
        var month = new DateTime(today.Year, today.Month, 1);
        int fyStart = today.Month >= 4 ? today.Year : today.Year - 1;
        var quarter = new DateTime(today.Year, (today.Month - 1) / 3 * 3 + 1, 1);
        return tag switch
        {
            "LASTMONTH" => (month.AddMonths(-1), month.AddDays(-1)),
            "QUARTER"   => (quarter, today),
            "FY"        => (new DateTime(fyStart, 4, 1), today),
            "ALL"       => (new DateTime(2000, 1, 1), today),
            _           => (month, today),
        };
    }

    private void ApplyRange(string tag)
    {
        var (from, to) = SalesReportRange(tag, DateTime.Today);
        SrFrom.SelectedDate = from;
        SrTo.SelectedDate = to;
    }

    /// A period picked: fill both dates (guarded, so that is one load, not two) and load.
    private async void SrRange_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_srLoading || !IsLoaded || SrRange.SelectedItem is not ComboBoxItem { Tag: string tag } || tag == "CUSTOM")
            return;
        _srLoading = true;
        try { ApplyRange(tag); } finally { _srLoading = false; }
        await LoadSalesReportAsync();
    }

    private async void LoadSalesReport_Click(object sender, RoutedEventArgs e) => await LoadSalesReportAsync();

    private async Task LoadSalesReportAsync()
    {
        if (_srLoading) return;

        // Set BEFORE the defaults: filling the two dates raises SrDate_Changed, which would
        // otherwise start a second load inside this one.
        _srLoading = true;
        try
        {
            SalesReportDefaults();
            var from = DateOnly.FromDateTime(SrFrom.SelectedDate ?? DateTime.Today);
            var to = DateOnly.FromDateTime(SrTo.SelectedDate ?? DateTime.Today);
            if (to < from) { Refuse("The To date is before the From date"); return; }

            var lines = await Read(() => Repo.SalesLinesAsync(from, to));
            var invoices = await Read(Repo.InvoicesAsync);
            if (lines is null || invoices is null) return;

            _srInvoices = invoices;
            _srAll = SalesReportCalc.Rows(lines, invoices);

            // The pickers list only what this period actually sold, so every choice finds rows.
            Fill(SrBuyer, SrAllBuyers, _srAll.Select(r => r.Buyer));
            Fill(SrGrade, SrAllGrades, _srAll.Select(r => r.Grade));
            SrPeriod.Text = $"{from:dd MMM yyyy} – {to:dd MMM yyyy}";
        }
        finally { _srLoading = false; }

        ShowSalesReport();

        static void Fill(ComboBox box, string all, IEnumerable<string> names)
        {
            string? keep = box.SelectedItem as string;
            var items = new List<string> { all };
            items.AddRange(names.Where(n => n.Length > 0).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            box.ItemsSource = items;
            box.SelectedItem = keep is not null && items.Contains(keep) ? keep : all;
        }
    }

    private void SrFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_srLoading && IsLoaded) ShowSalesReport();
    }

    private async void SrDate_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_srLoading || !IsLoaded || SrFrom.SelectedDate is null || SrTo.SelectedDate is null) return;

        // A date typed by hand is no longer the named period, so the picker says Custom.
        _srLoading = true;
        try { SrRange.SelectedIndex = SrRange.Items.Count - 1; } finally { _srLoading = false; }
        await LoadSalesReportAsync();
    }

    private void SrClear_Click(object sender, RoutedEventArgs e)
    {
        SrBuyer.SelectedIndex = 0;
        SrGrade.SelectedIndex = 0;
        SrSearch.Text = "";
        ShowSalesReport();
    }

    private void ShowSalesReport()
    {
        if (SrLines is null) return;

        // Index 0 is "All buyers" / "All grades".
        string? buyer = SrBuyer.SelectedIndex > 0 ? SrBuyer.SelectedItem as string : null;
        string? grade = SrGrade.SelectedIndex > 0 ? SrGrade.SelectedItem as string : null;
        string term = SrSearch.Text.Trim();

        var rows = _srAll.Where(r =>
                (buyer is null || r.Buyer == buyer)
             && (grade is null || r.Grade == grade)
             && (term.Length == 0
                 || r.InvoiceNo.Contains(term, StringComparison.OrdinalIgnoreCase)
                 || r.Buyer.Contains(term, StringComparison.OrdinalIgnoreCase)
                 || r.Broker.Contains(term, StringComparison.OrdinalIgnoreCase)
                 || (r.Remark ?? "").Contains(term, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        SrLines.ItemsSource = rows;
        SrSummaryGrid.ItemsSource = SalesReportCalc.Group(rows, (SrGroupBy.SelectedItem as ComboBoxItem)?.Content as string ?? "Buyer");

        // Lines and carats are sums of what is shown. Received and outstanding belong to INVOICES,
        // so they are counted once per invoice -- the whole invoice, even when a grade filter shows
        // only part of it, which the caption says.
        var ids = rows.Select(r => r.InvoiceId).ToHashSet();
        var invs = _srInvoices.Where(i => ids.Contains(i.InvoiceId)).ToList();
        decimal sel = rows.Sum(r => r.SelectionCt), amount = rows.Sum(r => r.Amount);

        SrKInvoices.Text = $"{ids.Count:N0}";
        SrKLines.Text = $"{rows.Count:N0} line(s)";
        SrKCarats.Text = $"{sel:N2} ct";
        SrKGross.Text = $"Weight {rows.Sum(r => r.WeightCt):N2} · Rejection {rows.Sum(r => r.RejectionCt):N2}";
        SrKAmount.Text = Money.Short(amount);
        SrKRate.Text = sel == 0 ? "Avg rate —" : $"Avg rate {Money.Exact(Math.Round(amount / sel, 2))} / ct";
        SrKBroker.Text = Money.Short(rows.Sum(r => r.BrokerCost));
        SrKReceived.Text = Money.Short(invs.Sum(i => i.Received));
        SrKOutstanding.Text = Money.Short(invs.Sum(i => i.Outstanding));
        SrKOverdue.Text = $"{invs.Count(i => i.IsOverdue && i.Outstanding > 0):N0} overdue invoice(s)";

        SrChip.Text = rows.Count == _srAll.Count ? $"{rows.Count:N0} line(s)" : $"{rows.Count:N0} of {_srAll.Count:N0} line(s)";
        SrEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SrSummaryEmpty.Visibility = SrEmpty.Visibility;
        // Says WHEN the last sale was, so an empty month reads as "nothing sold yet" rather than as
        // a report that failed to load.
        var latest = _srInvoices.Where(i => i.Status == InvoiceStatus.POSTED)
                                .Select(i => (DateOnly?)i.InvoiceDate).Max();
        SrEmpty.Text = _srAll.Count > 0
            ? "No lines match these filters. Press Clear to show them all."
            : latest is { } last
                ? $"No confirmed sales in this period. The latest confirmed sale is dated {last:dd MMM yyyy} — choose a period that includes it, or All time."
                : "No confirmed sales yet. Sales appear here once they are confirmed on Sales entry.";
    }

    private void ExportSalesReport_Click(object sender, RoutedEventArgs e)
        => Say(Reports.ExportGrid(SrLines, "sales-report") ?? "", ok: true);

    private void ExportSalesSummary_Click(object sender, RoutedEventArgs e)
        => Say(Reports.ExportGrid(SrSummaryGrid, "sales-summary") ?? "", ok: true);
}
