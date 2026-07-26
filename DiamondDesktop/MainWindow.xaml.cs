using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DiamondDesktop.Data;

namespace DiamondDesktop;

public sealed class DispositionRow
{
    public decimal WeightCt { get; set; }
    public string Outcome { get; set; } = "RESELECT";
    public string? ToGradeCode { get; set; }
    public string? Note { get; set; }
}

public partial class MainWindow : Window
{
    private InvoiceEntry _invoice = new();
    private readonly ObservableCollection<DispositionRow> _dispositions = [];
    private bool _saving;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _invoice;

        InputBindings.Add(new KeyBinding(new RelayCommand(async () => await SaveDraftAsync()), Key.S, ModifierKeys.Control));
        DispositionGrid.ItemsSource = _dispositions;
        WhoAmI.Text = Db.CurrentUser?.FullName ?? "";
        Initials.Text = Initialise(Db.CurrentUser?.FullName);
        UsersTab.Visibility = Db.IsOwner ? Visibility.Visible : Visibility.Collapsed;

        // DisplayMemberPath, not ToString on the model: Grade and SizeBucket are wire types shared
        // with the database layer and have no business knowing how a combo renders them.
        foreach (var box in new[] { IntakeGrade, ConvFromGrade, ConvToGrade, RejGrade, AdjGrade,
                                    FilterGrade, PriceGradePicker })
        {
            box.ItemsSource = Catalogue.Grades;
            box.DisplayMemberPath = nameof(Grade.DisplayName);
        }

        // Size lists start with every bucket and narrow to that grade's sizes on selection —
        // opening one before picking a grade used to show an empty popup.
        foreach (var box in new[] { IntakeSize, ConvFromSize, ConvToSize, RejSize, AdjSize, PriceSizePicker })
        {
            box.ItemsSource = Catalogue.AllSizes;
            box.DisplayMemberPath = nameof(SizeBucket.Code);
        }

        _ = LoadPartiesAsync();
    }

    // ── Sales entry ─────────────────────────────────────────────────────────

    private async Task LoadPartiesAsync()
    {
        try
        {
            await Catalogue.LoadAsync();
            var buyers = await Repo.BuyersAsync();
            var brokers = await Repo.BrokersAsync();

            _invoice.Buyers.Clear();
            foreach (var b in buyers) _invoice.Buyers.Add(new PartyRef(b.BuyerId, b.Name, b.DefaultTermsDays));

            _invoice.Brokers.Clear();
            foreach (var b in brokers) _invoice.Brokers.Add(new PartyRef(b.BrokerId, b.Name, null, b.DefaultBrokerPct));

            FilterBuyer.ItemsSource = buyers;                  // the dashboard's buyer filter

            Pill(true, $"Connected · {Catalogue.Grades.Count} grades · {buyers.Count} buyers");

            // An empty picker looks like a broken screen. Say which it is.
            if (buyers.Count == 0) Say("No buyers came back from Supabase — add one on the Master data tab");
        }
        catch (Exception ex)
        {
            Pill(false, Db.IsOnline ? "Server refused the request" : "Offline");
            Say(ex.Message);
        }
    }

    /// AC 1: "fill header + one line, press Enter, then a new blank line appears with the header retained".
    private void Grid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        Grid.CommitEdit(DataGridEditingUnit.Row, true);
        if (!ReferenceEquals(Grid.CurrentItem, _invoice.Lines.LastOrDefault())) return;

        AddLine();
        e.Handled = true;
    }

    private void AddLine_Click(object sender, RoutedEventArgs e) => AddLine();

    /// <summary>
    /// A ComboBox living inside a DataGrid cell never sees the first click — the cell eats it to
    /// take selection focus, so the user has to click twice. This gives the first click to the
    /// list, which is what someone entering a parcel expects.
    /// </summary>
    private void CellCombo_Down(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ComboBox combo || combo.IsDropDownOpen || combo.IsKeyboardFocusWithin) return;

        combo.Focus();
        combo.IsDropDownOpen = true;
        e.Handled = true;
    }

    /// <summary>
    /// A DataGridCell spends the first click becoming the current cell and only enters edit mode on
    /// the second, so the first figure typed on a fresh screen is silently dropped. This puts the
    /// cell straight into edit so one click is enough.
    ///
    /// Text columns only: the Size and Grade cells hold ComboBoxes with their own first-click
    /// handler (<see cref="CellCombo_Down"/>), and beginning an edit under them would fight it.
    /// </summary>
    private void Cell_Down(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGridCell { IsEditing: false, IsReadOnly: false } cell) return;
        if (cell.Column is not DataGridTextColumn) return;

        if (!cell.IsFocused) cell.Focus();
        Grid.BeginEdit(e);
    }

    private void AddLine()
    {
        var line = new SaleLine();
        _invoice.Lines.Add(line);
        Grid.ScrollIntoView(line);
        Grid.CurrentCell = new DataGridCellInfo(line, Grid.Columns[0]);
    }

    private void NewInvoice_Click(object sender, RoutedEventArgs e)
    {
        var buyers = _invoice.Buyers.ToList();
        var brokers = _invoice.Brokers.ToList();

        _invoice = new InvoiceEntry();
        foreach (var b in buyers) _invoice.Buyers.Add(b);
        foreach (var b in brokers) _invoice.Brokers.Add(b);

        DataContext = _invoice;
        Status.Text = "";
    }

    private async void SaveDraft_Click(object sender, RoutedEventArgs e) => await SaveDraftAsync();

    private async Task<bool> SaveDraftAsync()
    {
        // Every save route — the button, Ctrl+S and Post — comes through here, so one guard closes
        // the hole for all three: the buttons stay live across the round trip, and a second click
        // arriving while the first insert is still in flight sees InvoiceId still null and books a
        // SECOND invoice for the same parcels.
        if (_saving) return false;

        Grid.CommitEdit();

        if (_invoice.Validate() is { } error) { Say(error); return false; }
        if (_invoice.BuyerId is not { } buyerId) { Say("Pick a buyer from the list"); return false; }
        if (Catalogue.BaseCurrencyId == 0) { Say("No INR row in the currency table — an invoice cannot be priced without it"); return false; }

        var draft = new DraftInvoice(
            _invoice.InvoiceId, _invoice.ClientRef, DateOnly.FromDateTime(_invoice.InvoiceDate),
            buyerId, _invoice.BrokerId, _invoice.BrokerPct, _invoice.TermsDays, _invoice.DocType,
            Catalogue.BaseCurrencyId,
            _invoice.RealLines.Select(l => new DraftLine(
                l.Grade!.GradeId, l.Size!.SizeId, l.GrossWeightCt, l.SelectionCt,
                l.PricePerCt, l.ExRate, l.Less1Pct, l.Less2Pct, l.Remark)).ToList());

        try
        {
            _saving = true;
            // Keeping the returned id makes the next save an update instead of a second invoice.
            _invoice.InvoiceId = await Repo.SaveDraftAsync(draft);
        }
        catch (Exception ex) { Say(ex.Message); return false; }
        finally { _saving = false; }

        // No amount here on purpose: the saved invoice's total is Postgres', and it is shown on the
        // Invoices tab where it comes from v_invoice.
        Say($"Draft saved · {_invoice.RealLines.Count} line(s)", ok: true);
        return true;
    }

    private async void Post_Click(object sender, RoutedEventArgs e)
    {
        if (!await SaveDraftAsync() || _invoice.InvoiceId is not { } id) return;

        var outcome = await Repo.PostAsync(id);

        if (outcome.NeedsOverride)
        {
            string shortfalls = string.Join("\n", outcome.Shortfalls.Select(s =>
                $"{s.GradeCode} × {s.SizeCode} — balance {s.BalanceCt:N4} ct, needs {s.NeededCt:N4} ct"));

            var answer = MessageBox.Show($"{outcome.Message}\n\n{shortfalls}\n\nPost anyway?",
                "Negative stock", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) { Say(outcome.Message ?? "Not posted"); return; }

            outcome = await Repo.PostAsync(id, over: true);
        }

        if (!outcome.Ok) { Say(outcome.Message ?? "Post failed"); return; }

        // The invoice number is assigned at post, by post_invoice() — never by this app.
        Say($"Posted as {outcome.InvoiceNo} · stock deducted", ok: true);
        MessageBox.Show($"Posted as {outcome.InvoiceNo}.", "Invoice posted",
                        MessageBoxButton.OK, MessageBoxImage.Information);
        NewInvoice_Click(sender, e);
    }

    // ── Invoices, receipts, receivables ─────────────────────────────────────

    private async void LoadInvoices_Click(object sender, RoutedEventArgs e)
        => InvoiceGrid.ItemsSource = await Read(Repo.InvoicesAsync);

    private async void Receipt_Click(object sender, RoutedEventArgs e)
    {
        if (InvoiceGrid.SelectedItem is not VInvoice invoice) { Say("Select an invoice first"); return; }
        // A cancelled invoice has had its stock returned and owes nothing. Cash booked against it
        // lands in the receipt ledger against a document that no longer exists.
        if (invoice.Status == InvoiceStatus.CANCELLED) { Say("That invoice is cancelled — nothing can be received against it"); return; }
        if (!decimal.TryParse(ReceiptAmount.Text, out decimal amount) || amount <= 0) { Say("Enter a receipt amount"); return; }

        string method = (ReceiptMethod.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "CASH";
        string? failure = await Repo.ReceiptAsync(invoice.InvoiceId, amount, method);
        if (failure is not null) { Say(failure); return; }

        ReceiptAmount.Text = "";
        Say("Receipt recorded", ok: true);
        LoadInvoices_Click(sender, e);
    }

    private async void CancelInvoice_Click(object sender, RoutedEventArgs e)
    {
        if (InvoiceGrid.SelectedItem is not VInvoice invoice) { Say("Select an invoice first"); return; }

        // The RPC rejects a blank reason, so there is no point sending one.
        var reason = Prompt.Ask("Why is this invoice being cancelled?", "Cancel invoice");
        if (string.IsNullOrWhiteSpace(reason)) { Say("A cancellation reason is required"); return; }

        string? failure = await Repo.CancelAsync(invoice.InvoiceId, reason);
        Say(failure ?? "Cancelled · stock returned", ok: failure is null);
        LoadInvoices_Click(sender, e);
    }

    private async void LoadReceivables_Click(object sender, RoutedEventArgs e)
    {
        var rows = await Read(Repo.ReceivablesAsync);
        if (rows is null) return;

        ReceivablesGrid.ItemsSource = rows;
        ReceivablesSummary.Text = $"Total {rows.Sum(r => r.Outstanding):N2}   ·   " + string.Join("   ",
            rows.GroupBy(r => r.AgeBucket).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Sum(r => r.Outstanding):N2}"));
    }

    // ── RPT-001 · export · RPT-002 · print ──────────────────────────────────

    private void ExportInvoices_Click(object sender, RoutedEventArgs e)
        => Say(Reports.ExportGrid(InvoiceGrid, "sales") ?? "", ok: true);

    private void ExportReceivables_Click(object sender, RoutedEventArgs e)
        => Say(Reports.ExportGrid(ReceivablesGrid, "receivables") ?? "", ok: true);

    private void ExportStock_Click(object sender, RoutedEventArgs e)
        => Say(Reports.ExportGrid(StockGrid, "stock") ?? "", ok: true);

    private async void PrintInvoice_Click(object sender, RoutedEventArgs e)
    {
        if (InvoiceGrid.SelectedItem is not VInvoice invoice) { Say("Select an invoice first"); return; }

        var lines = await Read(() => Repo.LinesAsync(invoice.InvoiceId));
        if (lines is null) return;

        Say(Reports.PrintInvoice(invoice, lines, "Diamond Sales & Inventory") ?? "", ok: true);
    }

    // ── Stock ───────────────────────────────────────────────────────────────

    private async void LoadStock_Click(object sender, RoutedEventArgs e)
    {
        var rows = await Read(Repo.StockAsync);
        if (rows is null) return;

        StockGrid.ItemsSource = rows;
        StockSummary.Text = $"{rows.Sum(r => r.BalanceCt):N4} ct   ·   value {rows.Sum(r => r.StockValue):N2}";
    }

    private async void Movements_Click(object sender, RoutedEventArgs e)
    {
        if (StockGrid.SelectedItem is not VStockPosition row) { Say("Select a grade × size row"); return; }
        MovementGrid.ItemsSource = await Read(() => Repo.MovementsAsync(row.GradeCode, row.SizeCode));
    }

    private async void Invariants_Click(object sender, RoutedEventArgs e)
    {
        var rows = await Read(Repo.ReconciliationAsync);
        if (rows is null) return;

        var broken = rows.Where(r => !r.Reconciles).ToList();
        MessageBox.Show(
            broken.Count == 0
                ? "Stock moved out matches stock invoiced, on every grade × size."
                : string.Join("\n", broken.Select(r =>
                    $"{r.GradeCode} × {r.SizeCode} — moved {r.MovedOutCt:N4} ct, invoiced {r.SoldOnInvoicesCt:N4} ct, off by {r.DiffCt:N4}")),
            "Ledger integrity", MessageBoxButton.OK,
            broken.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    // ── Intake, conversion, rejection, adjustment ───────────────────────────

    private static void FillSizes(ComboBox gradeBox, ComboBox sizeBox)
    {
        sizeBox.ItemsSource = Catalogue.SizesFor(gradeBox.SelectedItem as Grade);
        sizeBox.SelectedIndex = 0;
    }

    private void IntakeGrade_Changed(object sender, SelectionChangedEventArgs e) => FillSizes(IntakeGrade, IntakeSize);
    private void ConvFromGrade_Changed(object sender, SelectionChangedEventArgs e) => FillSizes(ConvFromGrade, ConvFromSize);
    private void ConvToGrade_Changed(object sender, SelectionChangedEventArgs e) => FillSizes(ConvToGrade, ConvToSize);
    private void RejGrade_Changed(object sender, SelectionChangedEventArgs e) => FillSizes(RejGrade, RejSize);
    private void AdjGrade_Changed(object sender, SelectionChangedEventArgs e) => FillSizes(AdjGrade, AdjSize);

    private async void Intake_Click(object sender, RoutedEventArgs e)
    {
        var (grade, size) = Pick(IntakeGrade, IntakeSize);
        if (grade is null || size is null) { Say("Pick a grade and size"); return; }
        if (!decimal.TryParse(IntakeWeight.Text, out decimal weight) || weight <= 0) { Say("Weight must be positive"); return; }
        // rough_intake.price_per_ct is not nullable and feeds v_stock_position's avg_cost and
        // stock_value. A blank box parsed to 0 booked the parcel at zero value and dragged the
        // grade's average cost down with it, silently. Zero is fine — but it has to be typed.
        if (!decimal.TryParse(IntakePrice.Text, out decimal price) || price < 0)
        { Say("Enter the price per carat — it sets this parcel's cost basis"); return; }

        string? failure = await Repo.IntakeAsync(grade.GradeId, size.SizeId, weight, price);
        Say(failure ?? $"Intake recorded · {weight:N4} ct", ok: failure is null);
    }

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        var (fromGrade, fromSize) = Pick(ConvFromGrade, ConvFromSize);
        var (toGrade, toSize) = Pick(ConvToGrade, ConvToSize);
        if (fromGrade is null || fromSize is null || toGrade is null || toSize is null) { Say("Pick both sides"); return; }
        if (!decimal.TryParse(ConvWeight.Text, out decimal weight) || weight <= 0) { Say("Weight must be positive"); return; }
        if (!TypedPrice(ConvPrice, out decimal? price)) { Say("Price/ct must be a number, or left blank"); return; }

        string? failure = await Repo.ConvertAsync(fromGrade.GradeId, fromSize.SizeId,
                                                  toGrade.GradeId, toSize.SizeId, weight, price);
        Say(failure ?? $"Converted {weight:N4} ct · total carats unchanged", ok: failure is null);
    }

    private async void Reject_Click(object sender, RoutedEventArgs e)
    {
        // The row the user is still typing holds its weight in the cell editor, not on the object.
        // Without this the count below reads 0 and the "N were NOT saved" warning never appears —
        // which is the one thing this screen must not do.
        DispositionGrid.CommitEdit(DataGridEditingUnit.Row, true);

        var (grade, size) = Pick(RejGrade, RejSize);
        if (grade is null || size is null) { Say("Pick a grade and size"); return; }
        if (!decimal.TryParse(RejWeight.Text, out decimal weight) || weight <= 0) { Say("Weight must be positive"); return; }
        if (!TypedPrice(RejPrice, out decimal? price)) { Say("Price/ct must be a number, or left blank"); return; }

        string? failure = await Repo.RejectionAsync(grade.GradeId, size.SizeId, weight, price);
        if (failure is not null) { Say(failure); return; }

        // ponytail: dispositions have no Supabase table yet, so they are typed and not stored.
        // Say so rather than swallow them; wire them up when the table exists.
        int typed = _dispositions.Count(d => d.WeightCt > 0);
        _dispositions.Clear();
        Say(typed == 0
            ? $"Rejection recorded · {weight:N4} ct"
            : $"Rejection recorded · {weight:N4} ct — {typed} disposition(s) were NOT saved, there is no table for them yet",
            ok: typed == 0);
    }

    private async void Adjust_Click(object sender, RoutedEventArgs e)
    {
        var (grade, size) = Pick(AdjGrade, AdjSize);
        if (grade is null || size is null) { Say("Pick a grade and size"); return; }
        if (!decimal.TryParse(AdjWeight.Text, out decimal weight) || weight == 0) { Say("Adjust by a non-zero weight"); return; }
        if (string.IsNullOrWhiteSpace(AdjReason.Text)) { Say("An adjustment needs a reason"); return; }

        string? failure = await Repo.AdjustAsync(grade.GradeId, size.SizeId, weight, AdjReason.Text.Trim());
        Say(failure ?? "Adjustment recorded — it stays visible in the ledger forever", ok: failure is null);
    }

    // ── Master data ─────────────────────────────────────────────────────────

    private async Task LoadMasterAsync()
    {
        GradeGrid.ItemsSource = await Read(Repo.GradesAsync);
        BuyerGrid.ItemsSource = await Read(Repo.BuyersAsync);
        BrokerGrid.ItemsSource = await Read(Repo.BrokersAsync);
        if (Db.IsManagerOrOwner) await LoadPricesAsync();
    }

    private async void AddBuyer_Click(object sender, RoutedEventArgs e)
    {
        int.TryParse(NewBuyerTerms.Text, out int terms);
        string? failure = await Repo.AddBuyerAsync(NewBuyerName.Text.Trim(), terms);

        Say(failure ?? "Buyer added", ok: failure is null);
        if (failure is null) { NewBuyerName.Text = ""; await LoadMasterAsync(); await LoadPartiesAsync(); }
    }

    private async void AddBroker_Click(object sender, RoutedEventArgs e)
    {
        decimal.TryParse(NewBrokerPct.Text, out decimal pct);
        string? failure = await Repo.AddBrokerAsync(NewBrokerName.Text.Trim(), pct);

        Say(failure ?? "Broker added", ok: failure is null);
        if (failure is null) { NewBrokerName.Text = ""; await LoadMasterAsync(); await LoadPartiesAsync(); }
    }

    // ── MDM-003 · price list ────────────────────────────────────────────────

    private void PriceGrade_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (PriceSizePicker is null) return;
        FillSizes(PriceGradePicker, PriceSizePicker);
    }

    private async Task LoadPricesAsync()
    {
        var prices = await Read(Repo.PricesAsync);
        if (prices is null) return;

        var grades = Catalogue.Grades.ToDictionary(g => g.GradeId, g => g.DisplayName ?? g.Code);
        var sizes = Catalogue.AllSizes.ToDictionary(s => s.SizeId, s => s.Code);

        PriceGrid.ItemsSource = prices.Select(p => new
        {
            GradeCode = grades.GetValueOrDefault(p.GradeId, "?"),
            SizeCode = sizes.GetValueOrDefault(p.SizeId, "?"),
            p.Context, p.PricePerCt, p.EffectiveFrom,
        }).ToList();
    }

    private async void AddPrice_Click(object sender, RoutedEventArgs e)
    {
        var (grade, size) = Pick(PriceGradePicker, PriceSizePicker);
        if (grade is null || size is null) { Say("Pick a grade and size"); return; }
        if (!decimal.TryParse(PriceValue.Text, out decimal price) || price < 0) { Say("Enter a price"); return; }

        string context = (PriceContext.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "SALE";

        // Repo closes the previous open price and opens a new one — prices are never edited in place,
        // so a valuation as of any past date still finds the price that applied then.
        string? failure = await Repo.SetPriceAsync(grade.GradeId, size.SizeId, context, price);

        Say(failure ?? $"{grade.DisplayName} {size.Code} {context} = {price:N2} from today", ok: failure is null);
        if (failure is null) await LoadPricesAsync();
    }

    // ── PHASE 4 · owner dashboard ───────────────────────────────────────────

    /// The filter bar as a date window. Postgres does the KPI arithmetic; the window is all it needs.
    private (DateOnly From, DateOnly To) Period()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        return (RangePicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "TODAY" => (today, today),
            "WEEK" => (today.AddDays(-(int)DateTime.Today.DayOfWeek), today),
            "MONTH" => (new DateOnly(today.Year, today.Month, 1), today),
            "QUARTER" => (new DateOnly(today.Year, (today.Month - 1) / 3 * 3 + 1, 1), today),
            "FY" => (new DateOnly(today.Month >= 4 ? today.Year : today.Year - 1, 4, 1), today),  // India: FY starts 1 April
            "CUSTOM" => (Day(FromDate.SelectedDate) ?? today.AddMonths(-1), Day(ToDate.SelectedDate) ?? today),
            _ => (new DateOnly(2000, 1, 1), today),           // All time
        };

        static DateOnly? Day(DateTime? d) => d is null ? null : DateOnly.FromDateTime(d.Value);
    }

    /// The invoices the filter bar selects. Every breakdown groups over this list — client-side
    /// grouping of server-computed amounts, which is what the golden rule allows.
    private async Task<List<VInvoice>> FilteredInvoicesAsync()
    {
        var (from, to) = Period();
        var all = await Read(Repo.InvoicesAsync) ?? [];

        var rows = all.Where(i => i.Status == InvoiceStatus.POSTED
                               && i.InvoiceDate >= from && i.InvoiceDate <= to);
        if (FilterBuyer.SelectedItem is Buyer buyer) rows = rows.Where(i => i.BuyerId == buyer.BuyerId);
        return rows.ToList();
    }

    private void Range_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (FromDate is null) return;                     // fires once during XAML load

        bool custom = (RangePicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "CUSTOM";
        FromDate.IsEnabled = ToDate.IsEnabled = custom;
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        RangePicker.SelectedIndex = 5;                    // All time
        FilterBuyer.SelectedItem = null;
        FilterGrade.SelectedItem = null;
        LoadDashboard_Click(sender, e);
    }

    private async void LoadDashboard_Click(object sender, RoutedEventArgs e)
    {
        var (from, to) = Period();

        DashboardSummary summary;
        try { summary = await Repo.DashboardAsync(from, to); }
        catch (Exception ex) { Say(ex.Message); return; }

        KpiSales.Text = summary.SalesAmount.ToString("N2");
        KpiCarats.Text = summary.CaratsSold.ToString("N2");
        KpiRate.Text = summary.BlendedRate.ToString("N2");
        KpiOutstanding.Text = summary.OutstandingTotal.ToString("N2");
        KpiInventory.Text = summary.StockValue.ToString("N2");
        KpiInventoryCt.Text = $"{summary.StockCarats:N2} ct";
        KpiCount.Text = summary.InvoiceCount.ToString();

        // W7 · margin needs a cost basis per sold parcel, which no view exposes yet.
        KpiMargin.Text = "—";
        KpiMarginBasis.Text = "no cost basis in the database yet";

        // W1's vs-prior: the same number of days, immediately before this window.
        int days = to.DayNumber - from.DayNumber + 1;
        try
        {
            var prior = await Repo.DashboardAsync(from.AddDays(-days), from.AddDays(-1));
            KpiSalesDelta.Text = prior.SalesAmount == 0
                ? "no prior period"
                : $"{(summary.SalesAmount - prior.SalesAmount) / prior.SalesAmount * 100m:+0.0;-0.0}% vs prior {prior.SalesAmount:N0}";
        }
        catch (Exception) { KpiSalesDelta.Text = "no prior period"; }

        var invoices = await FilteredInvoicesAsync();
        KpiBroker.Text = invoices.Sum(i => i.BrokerPayable).ToString("N2");

        DrillGrid.ItemsSource = invoices;

        await LoadAlertsAsync(summary);
        await LoadBreakdownAsync();
    }

    /// W15 · the alerts strip. Hidden entirely when there is nothing wrong.
    private async Task LoadAlertsAsync(DashboardSummary summary)
    {
        var stock = await Read(Repo.StockAsync) ?? [];
        int negative = stock.Count(s => s.BalanceCt < 0);

        if (summary.OverdueCount == 0 && negative == 0)
        {
            AlertStrip.Visibility = Visibility.Collapsed;
            return;
        }

        var parts = new List<string>();
        if (summary.OverdueCount > 0) parts.Add($"{summary.OverdueCount} overdue invoice(s) worth {summary.OverdueTotal:N2}");
        if (negative > 0) parts.Add($"{negative} grade/size showing NEGATIVE stock");

        AlertText.Text = string.Join("   ·   ", parts);
        AlertStrip.Visibility = Visibility.Visible;
    }

    private void Breakdown_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (BarList is null) return;                       // fires during XAML load
        _ = LoadBreakdownAsync();
    }

    /// One list renders every breakdown. Each is a LINQ grouping over figures the database computed.
    private async Task LoadBreakdownAsync()
    {
        string which = (BreakdownPicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "period";
        string bucket = (PeriodBucket.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "day";
        PeriodBucket.Visibility = which == "period" ? Visibility.Visible : Visibility.Hidden;

        List<(string Label, decimal Value, string? Secondary)> bars;
        bool money = true;

        switch (which)
        {
            case "salesperson":
            case "buyer":
            case "broker-cost":
            {
                var invoices = await FilteredInvoicesAsync();
                bars = which switch
                {
                    "salesperson" => Group(invoices, i => i.Salesperson ?? "unattributed", i => i.AmountTotal),
                    "buyer" => Group(invoices, i => i.BuyerName, i => i.AmountTotal),
                    _ => Group(invoices, i => i.BrokerName ?? "no broker", i => i.BrokerPayable),
                };
                break;
            }

            case "period":
            {
                var invoices = await FilteredInvoicesAsync();
                bars = Group(invoices, i => Bucket(i.InvoiceDate, bucket), i => i.AmountTotal)
                    .OrderBy(b => b.Label).ToList();
                break;
            }

            case "ageing":
            {
                var rows = await Read(Repo.ReceivablesAsync) ?? [];
                bars = Group(rows, r => r.AgeBucket, r => r.Outstanding);
                break;
            }

            case "inventory":
            case "inventory-aging":
            {
                var stock = await Read(Repo.StockAsync) ?? [];
                if (FilterGrade.SelectedItem is Grade grade)
                    stock = stock.Where(s => s.GradeCode == grade.Code).ToList();

                if (which == "inventory")
                    bars = Group(stock, s => s.GradeCode, s => s.StockValue);
                else
                {
                    money = false;
                    bars = Group(stock, s => AgeBand(s.AgeDays), s => s.BalanceCt);
                }
                break;
            }

            default:
                bars = [];
                break;
        }

        // Bar widths are computed here, not bound through a converter — one less moving part.
        decimal max = bars.Count == 0 ? 0 : bars.Max(b => Math.Abs(b.Value));

        BarList.ItemsSource = bars.Select(b => new
        {
            b.Label,
            b.Secondary,
            ValueText = money ? b.Value.ToString("N2") : $"{b.Value:N2} ct",
            DeltaText = "",
            BarWidth = max == 0 ? 0 : (double)(Math.Abs(b.Value) / max) * 420,
        }).ToList();

        if (bars.Count == 0) Say("Nothing in this period — widen the range or clear the filters");
    }

    private static List<(string Label, decimal Value, string? Secondary)> Group<T>(
        IEnumerable<T> rows, Func<T, string> label, Func<T, decimal> value)
        => rows.GroupBy(label)
               .Select(g => (g.Key, g.Sum(value), (string?)$"{g.Count()} row(s)"))
               .OrderByDescending(b => Math.Abs(b.Item2))
               .ToList();

    private static string Bucket(DateOnly date, string bucket) => bucket switch
    {
        "month" => date.ToString("yyyy-MM"),
        "week" => date.AddDays(-(int)date.DayOfWeek).ToString("yyyy-MM-dd"),
        _ => date.ToString("yyyy-MM-dd"),
    };

    private static string AgeBand(int? days) => days switch
    {
        null => "unknown",
        < 31 => "0-30 days",
        < 91 => "31-90 days",
        < 181 => "91-180 days",
        _ => "over 180 days",
    };

    // ── Audit, users, settings ──────────────────────────────────────────────

    private async void LoadAudit_Click(object sender, RoutedEventArgs e)
    {
        var rows = await Read(() => Repo.AuditAsync());
        if (rows is null) return;

        AuditGrid.ItemsSource = rows.Select(r => new
        {
            r.ChangedAt,
            // AuditedTable, not TableName: TableName is BaseModel's own and reads "audit_log" on
            // every row, so the Entity column would name the audit table instead of the audited one.
            Entity = r.AuditedTable,
            r.Action,
            Before = Flatten(r.OldValues),
            After = Flatten(r.NewValues),
        }).ToList();

        static string Flatten(Dictionary<string, object>? values)
            => values is null ? "" : string.Join(", ", values.Select(v => $"{v.Key}={v.Value}"));
    }

    private async void LoadUsers_Click(object sender, RoutedEventArgs e)
    {
        UserGrid.ItemsSource = await Read(Repo.UsersAsync);

        // Creating an account needs the service_role key, which must never ship in a desktop binary.
        Say("Read-only — accounts are created and deactivated in the Supabase dashboard", ok: true);
    }

    private async void LoadSettings_Click(object sender, RoutedEventArgs e)
    {
        var config = await Read(Repo.ConfigAsync);
        if (config is null) return;

        SettingGrid.ItemsSource = config.OrderBy(c => c.Key).ToList();
    }

    private async void SaveSetting_Click(object sender, RoutedEventArgs e)
    {
        if (SettingGrid.SelectedItem is not KeyValuePair<string, string> setting) { Say("Select a setting"); return; }

        string? failure = await Repo.SetConfigAsync(setting.Key, SettingValue.Text);
        Say(failure ?? $"{setting.Key} = {SettingValue.Text}", ok: failure is null);
        if (failure is null) LoadSettings_Click(sender, e);
    }

    // ── plumbing ────────────────────────────────────────────────────────────

    /// Reads throw; a screen shows the reason instead of an unexplained empty grid.
    private async Task<T?> Read<T>(Func<Task<T>> read) where T : class
    {
        try { return await read(); }
        catch (Exception ex) { Say(ex.Message); return null; }
    }

    private void Pill(bool ok, string message)
    {
        SyncText.Text = message;
        SyncText.Foreground = (Brush)FindResource(ok ? "TextMutedBrush" : "DangerBrush");
    }

    /// Initials for the avatar chip — "Asha Patel" → "AP".
    private static string Initialise(string? name)
    {
        var words = (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 ? "?" : string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0])));
    }

    private void ThemeToggle_Click(object sender, RoutedEventArgs e) => ThemeManager.Toggle();

    /// Top-bar caption per screen. Same strings the nav uses, so the two never disagree.
    private static readonly Dictionary<string, string> ScreenSubtitles = new()
    {
        ["Sales entry"] = "Keyboard-first invoice entry",
        ["Invoices"] = "Search, edit, post, receipts",
        ["Receivables"] = "Ageing and collections",
        ["Stock"] = "Grade × sieve-size position",
        ["Intake & movements"] = "Rough intake, conversions, rejections",
        ["Master data"] = "Grades, buyers, brokers",
        ["Dashboard"] = "Trading position at a glance",
        ["Audit"] = "Every change, who and when",
        ["Users"] = "Accounts and roles",
        ["Settings"] = "Policies and thresholds",
    };

    private void Tabs_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != Tabs || Tabs.SelectedItem is not TabItem tab) return;

        string header = tab.Header?.ToString() ?? "";
        ScreenTitle.Text = header;
        ScreenSub.Text = ScreenSubtitles.GetValueOrDefault(header, "");
        Status.Text = "";                      // a message belongs to the screen that raised it

        switch (header)
        {
            case "Invoices": LoadInvoices_Click(sender, e); break;
            case "Receivables": LoadReceivables_Click(sender, e); break;
            case "Stock": LoadStock_Click(sender, e); break;
            case "Master data": _ = LoadMasterAsync(); break;
            case "Dashboard": LoadDashboard_Click(sender, e); break;
            case "Audit": LoadAudit_Click(sender, e); break;
            case "Users": LoadUsers_Click(sender, e); break;
            case "Settings": LoadSettings_Click(sender, e); break;
        }
    }

    private static (Grade?, SizeBucket?) Pick(ComboBox gradeBox, ComboBox sizeBox)
        => (gradeBox.SelectedItem as Grade, sizeBox.SelectedItem as SizeBucket);

    /// <summary>
    /// An optional price box. Blank means NULL — "no price given" — not zero: a persisted 0 is a
    /// real price per carat to every view that reads it, and nothing afterwards can tell the two
    /// apart. False means the box holds something that is not a price at all.
    /// </summary>
    private static bool TypedPrice(TextBox box, out decimal? price)
    {
        price = null;
        if (string.IsNullOrWhiteSpace(box.Text)) return true;
        if (!decimal.TryParse(box.Text, out decimal typed) || typed < 0) return false;

        price = typed;
        return true;
    }

    private void Say(string message, bool ok = false)
    {
        // Token brushes, not Brushes.SeaGreen/Firebrick — those don't follow the light/dark swap.
        Status.Foreground = (Brush)FindResource(ok ? "SuccessBrush" : "DangerBrush");
        Status.Text = message;
    }
}

public sealed class RelayCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
}

/// ponytail: WPF has no input box. Twelve lines beats a dialog-library dependency.
public static class Prompt
{
    public static string? Ask(string question, string title)
    {
        var box = new TextBox { Margin = new Thickness(0, 8, 0, 12), MinWidth = 320 };
        var ok = new Button { Content = "OK", IsDefault = true, Padding = new Thickness(16, 4, 16, 4) };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(ok);

        var window = new Window
        {
            Title = title, Content = panel, SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.NoResize,
        };
        ok.Click += (_, _) => window.DialogResult = true;
        box.Focus();

        return window.ShowDialog() == true ? box.Text : null;
    }
}
