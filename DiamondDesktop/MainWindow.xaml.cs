using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

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
    private readonly Api _api;
    private InvoiceEntry _invoice = new();
    private readonly ObservableCollection<DispositionRow> _dispositions = [];
    private string? _pendingOverrideToken;

    public MainWindow(Api api)
    {
        InitializeComponent();
        _api = api;
        DataContext = _invoice;

        InputBindings.Add(new KeyBinding(new RelayCommand(async () => await SaveDraftAsync()), Key.S, ModifierKeys.Control));
        DispositionGrid.ItemsSource = _dispositions;
        WhoAmI.Text = api.User?.DisplayName ?? "";
        Initials.Text = Initialise(api.User?.DisplayName);
        UsersTab.Visibility = api.IsOwner ? Visibility.Visible : Visibility.Collapsed;

        foreach (var box in new[] { IntakeGrade, ConvFromGrade, ConvToGrade, RejGrade, AdjGrade,
                                    FilterGrade, PriceGradePicker })
            box.ItemsSource = Catalogue.Grades;

        // Size lists used to fill only once a grade was chosen, so opening one first showed an
        // empty popup. They start with all four and narrow to that grade's sizes on selection.
        foreach (var box in new[] { IntakeSize, ConvFromSize, ConvToSize, RejSize, AdjSize, PriceSizePicker })
            box.ItemsSource = Catalogue.AllSizes;

        // Sales staff propose changes; managers decide. Each sees only their own button.
        ChangeRequestButton.Visibility = api.IsManager ? Visibility.Collapsed : Visibility.Visible;
        ReviewRequestsButton.Visibility = api.IsManager ? Visibility.Visible : Visibility.Collapsed;

        _ = LoadPartiesAsync();
    }

    // ── Sales entry ─────────────────────────────────────────────────────────

    private async Task LoadPartiesAsync()
    {
        // The top-bar pill reports the real connection, not a hard-coded "Connected".
        var (ok, message) = await _api.PingAsync();
        SyncText.Text = message;
        SyncText.Foreground = ok
            ? (System.Windows.Media.Brush)FindResource("TextMutedBrush")
            : (System.Windows.Media.Brush)FindResource("DangerBrush");
        if (!ok) { Say(message); return; }

        var buyers = await _api.GetAsync<List<BuyerDto>>("/api/v1/buyers") ?? [];
        var brokers = await _api.GetAsync<List<BrokerDto>>("/api/v1/brokers") ?? [];

        _invoice.Buyers.Clear();
        foreach (var b in buyers) _invoice.Buyers.Add(new PartyRef(b.BuyerId, b.Name, b.DefaultTermsDays));

        _invoice.Brokers.Clear();
        foreach (var b in brokers) _invoice.Brokers.Add(new PartyRef(b.BrokerId, b.Name, null, b.DefaultBrokerPct));

        FilterBuyer.ItemsSource = buyers;                  // the dashboard's buyer filter

        // An empty picker looks like a broken screen. Say which it is.
        if (buyers.Count == 0) Say("No buyers came back from the server — add one on the Master data tab");
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
        _pendingOverrideToken = null;
        Status.Text = "";
    }

    private async void SaveDraft_Click(object sender, RoutedEventArgs e) => await SaveDraftAsync();

    private async Task<bool> SaveDraftAsync()
    {
        Grid.CommitEdit();

        if (_invoice.Validate() is { } error) { Say(error); return false; }
        if (_invoice.BuyerId is null) { Say("Pick a buyer from the list"); return false; }

        var payload = new
        {
            invoiceId = _invoice.InvoiceId,
            invoiceDate = DateOnly.FromDateTime(_invoice.InvoiceDate),
            buyerId = _invoice.BuyerId,
            brokerId = _invoice.BrokerId,
            brokerPct = _invoice.BrokerPct,
            termsDays = _invoice.TermsDays,
            docType = _invoice.DocType,
            lines = _invoice.RealLines.Select(l => new
            {
                gradeId = l.Grade!.Id,
                sizeId = l.Size!.Id,
                grossWeightCt = l.GrossWeightCt,
                selectionCt = l.SelectionCt,
                pricePerCt = l.PricePerCt,
                exRate = l.ExRate,
                less1Pct = l.Less1Pct,
                less2Pct = l.Less2Pct,
                remark = l.Remark,
            }).ToList(),
        };

        // Same id on create and update: the client owns the id, so re-saving is not a second invoice.
        string? failure = _invoice.Status == "DRAFT" && !_invoice.Saved
            ? await _api.PostAsync("/api/v1/invoices", payload)
            : await _api.PutAsync($"/api/v1/invoices/{_invoice.InvoiceId}", payload);

        if (failure is not null) { Say(failure); return false; }

        _invoice.Saved = true;
        Say($"Draft saved · {_invoice.TotalAmount:N2}", ok: true);
        return true;
    }

    private async void Post_Click(object sender, RoutedEventArgs e)
    {
        if (!await SaveDraftAsync()) return;

        var (ok, message, warnings, token) = await _api.PostInvoiceAsync(_invoice.InvoiceId, _pendingOverrideToken);
        _pendingOverrideToken = null;

        if (ok)
        {
            Say($"Posted · stock deducted · {_invoice.TotalAmount:N2}", ok: true);
            MessageBox.Show($"Posted.\n\nCarats out {_invoice.TotalCarats:N2}\nAmount {_invoice.TotalAmount:N2}",
                            "Invoice posted", MessageBoxButton.OK, MessageBoxImage.Information);
            NewInvoice_Click(sender, e);
            return;
        }

        if (token is not null)
        {
            var answer = MessageBox.Show(
                string.Join("\n", warnings) + "\n\nPost anyway?",
                "Negative stock", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (answer == MessageBoxResult.Yes)
            {
                _pendingOverrideToken = token;
                Post_Click(sender, e);
                return;
            }
        }

        Say(message ?? "Post failed");
    }

    // ── Invoices, receipts, receivables ─────────────────────────────────────

    private async void LoadInvoices_Click(object sender, RoutedEventArgs e)
        => InvoiceGrid.ItemsSource = await _api.GetAsync<List<InvoiceRow>>("/api/v1/invoices");

    private async void Receipt_Click(object sender, RoutedEventArgs e)
    {
        if (InvoiceGrid.SelectedItem is not InvoiceRow invoice) { Say("Select an invoice first"); return; }
        if (!decimal.TryParse(ReceiptAmount.Text, out decimal amount) || amount <= 0) { Say("Enter a receipt amount"); return; }

        string method = (ReceiptMethod.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "CASH";
        string? failure = await _api.PostAsync($"/api/v1/invoices/{invoice.InvoiceId}/receipts",
            new { receiptDate = DateOnly.FromDateTime(DateTime.Today), amount, method });

        if (failure is not null) { Say(failure); return; }

        ReceiptAmount.Text = "";
        Say("Receipt recorded", ok: true);
        LoadInvoices_Click(sender, e);
    }

    private async void CancelInvoice_Click(object sender, RoutedEventArgs e)
    {
        if (InvoiceGrid.SelectedItem is not InvoiceRow invoice) { Say("Select an invoice first"); return; }

        var reason = Prompt.Ask("Why is this invoice being cancelled?", "Cancel invoice");
        if (string.IsNullOrWhiteSpace(reason)) return;

        string? failure = await _api.PostAsync($"/api/v1/invoices/{invoice.InvoiceId}/cancel", new { reason });
        Say(failure ?? "Cancelled · stock returned", ok: failure is null);
        LoadInvoices_Click(sender, e);
    }

    private async void LoadReceivables_Click(object sender, RoutedEventArgs e)
    {
        var data = await _api.GetAsync<ReceivablesDto>("/api/v1/receivables");
        if (data is null) return;

        ReceivablesGrid.ItemsSource = data.Invoices;
        ReceivablesSummary.Text = $"Total {data.Total:N2}   ·   " +
            string.Join("   ", data.Buckets.Select(b => $"{b.Key} {b.Value:N2}"));
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
        if (InvoiceGrid.SelectedItem is not InvoiceRow row) { Say("Select an invoice first"); return; }

        var detail = await _api.GetAsync<InvoiceDetail>($"/api/v1/invoices/{row.InvoiceId}");
        if (detail is null) { Say(_api.LastError ?? "Could not load the invoice"); return; }

        // The bill prints grade and size names, which the invoice payload carries as ids.
        var grades = Catalogue.Grades.ToDictionary(g => g.Id, g => g.DisplayName);
        var sizes = Catalogue.AllSizes.ToDictionary(s => s.Id, s => s.Code);
        foreach (var line in detail.Lines)
        {
            line.GradeCode = grades.GetValueOrDefault(line.GradeId, "?");
            line.SizeCode = sizes.GetValueOrDefault(line.SizeId, "?");
        }

        Say(Reports.PrintInvoice(detail, "Diamond Sales & Inventory") ?? "", ok: true);
    }

    // ── SALES-002 · change requests ─────────────────────────────────────────

    private async void ChangeRequest_Click(object sender, RoutedEventArgs e)
    {
        if (InvoiceGrid.SelectedItem is not InvoiceRow row) { Say("Select an invoice first"); return; }

        string? what = Prompt.Ask($"What should change on {row.InvoiceNo}?", "Request a change");
        if (string.IsNullOrWhiteSpace(what)) return;

        // A sales person cannot edit a posted invoice — they propose, a manager decides.
        string? failure = await _api.PostAsync($"/api/v1/invoices/{row.InvoiceId}/change-requests",
            new { requested = what });
        Say(failure ?? "Change request raised — a manager will review it", ok: failure is null);
    }

    private async void ReviewRequests_Click(object sender, RoutedEventArgs e)
    {
        var requests = await _api.GetAsync<List<ChangeRequestRow>>("/api/v1/change-requests");
        if (requests is null) { Say(_api.LastError ?? "Manager or Owner only"); return; }
        if (requests.Count == 0) { Say("No open change requests", ok: true); return; }

        var first = requests[0];
        var answer = MessageBox.Show(
            $"{requests.Count} open request(s).\n\nOldest:\n{first.Proposed}\n\nApprove it?",
            "Change requests", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return;

        string decision = answer == MessageBoxResult.Yes ? "APPROVE" : "REJECT";
        string? failure = await _api.PostAsync(
            $"/api/v1/change-requests/{first.RequestId}/decide?decision={decision}");
        Say(failure ?? $"Request {decision.ToLowerInvariant()}d", ok: failure is null);
    }

    // ── Stock ───────────────────────────────────────────────────────────────

    private async void LoadStock_Click(object sender, RoutedEventArgs e)
    {
        var data = await _api.GetAsync<StockDto>("/api/v1/stock");
        if (data is null) { Say("Manager or Owner only"); return; }

        StockGrid.ItemsSource = data.Rows;
        StockSummary.Text = $"{data.TotalCarats:N4} ct   ·   value {data.TotalValue:N2}";
    }

    private async void Movements_Click(object sender, RoutedEventArgs e)
    {
        if (StockGrid.SelectedItem is not StockRowDto row) { Say("Select a grade × size row"); return; }
        MovementGrid.ItemsSource = await _api.GetAsync<List<MovementDto>>(
            $"/api/v1/stock/{row.GradeId}/{row.SizeId}/movements");
    }

    private async void Invariants_Click(object sender, RoutedEventArgs e)
    {
        var result = await _api.GetAsync<System.Text.Json.JsonElement>("/api/v1/invariants");
        var failures = result.TryGetProperty("failures", out var list)
            ? list.EnumerateArray().Select(f => f.GetString()).ToList()
            : [];

        MessageBox.Show(failures.Count == 0 ? "All invariants hold (INV-1…INV-6)." : string.Join("\n", failures),
                        "Ledger integrity", MessageBoxButton.OK,
                        failures.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
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
        decimal.TryParse(IntakePrice.Text, out decimal price);

        string? failure = await _api.PostAsync("/api/v1/intake", new
        {
            intakeDate = DateOnly.FromDateTime(DateTime.Today),
            rows = new[] { new { gradeId = grade.Id, sizeId = size.Id, weightCt = weight, pricePerCt = price } },
        });

        Say(failure ?? $"Intake recorded · {weight:N4} ct", ok: failure is null);
    }

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        var (fromGrade, fromSize) = Pick(ConvFromGrade, ConvFromSize);
        var (toGrade, toSize) = Pick(ConvToGrade, ConvToSize);
        if (fromGrade is null || fromSize is null || toGrade is null || toSize is null) { Say("Pick both sides"); return; }
        if (!decimal.TryParse(ConvWeight.Text, out decimal weight) || weight <= 0) { Say("Weight must be positive"); return; }
        decimal.TryParse(ConvPrice.Text, out decimal price);

        string? failure = await _api.PostAsync("/api/v1/conversions", new
        {
            fromGradeId = fromGrade.Id, fromSizeId = fromSize.Id,
            toGradeId = toGrade.Id, toSizeId = toSize.Id,
            weightCt = weight, pricePerCt = price,
        });

        Say(failure ?? $"Converted {weight:N4} ct · total carats unchanged", ok: failure is null);
    }

    private async void Reject_Click(object sender, RoutedEventArgs e)
    {
        var (grade, size) = Pick(RejGrade, RejSize);
        if (grade is null || size is null) { Say("Pick a grade and size"); return; }
        if (!decimal.TryParse(RejWeight.Text, out decimal weight) || weight <= 0) { Say("Weight must be positive"); return; }
        decimal.TryParse(RejPrice.Text, out decimal price);

        var byCode = Catalogue.Grades.ToDictionary(g => g.DisplayName, g => g.Id);
        var dispositions = _dispositions.Where(d => d.WeightCt > 0).Select(d => new
        {
            weightCt = d.WeightCt,
            outcome = d.Outcome,
            toGradeId = d.ToGradeCode is not null && byCode.TryGetValue(d.ToGradeCode, out var id) ? id : (Guid?)null,
            note = d.Note,
        }).ToList();

        string? failure = await _api.PostAsync("/api/v1/rejections", new
        {
            gradeId = grade.Id, sizeId = size.Id, weightCt = weight, pricePerCt = price,
            reason = (string?)null, dispositions,
        });

        if (failure is null) _dispositions.Clear();
        Say(failure ?? $"Rejection recorded · {dispositions.Count} disposition(s)", ok: failure is null);
    }

    private async void Adjust_Click(object sender, RoutedEventArgs e)
    {
        var (grade, size) = Pick(AdjGrade, AdjSize);
        if (grade is null || size is null) { Say("Pick a grade and size"); return; }
        if (!decimal.TryParse(AdjWeight.Text, out decimal weight) || weight == 0) { Say("Adjust by a non-zero weight"); return; }
        if (string.IsNullOrWhiteSpace(AdjReason.Text)) { Say("An adjustment needs a reason"); return; }

        string? failure = await _api.PostAsync("/api/v1/adjustments", new
        {
            gradeId = grade.Id, sizeId = size.Id, weightCt = weight, pricePerCt = 0m, reason = AdjReason.Text,
        });

        Say(failure ?? "Adjustment recorded — it stays visible in the ledger forever", ok: failure is null);
    }

    // ── Master data, dashboard, audit, users, settings ──────────────────────

    private async void LoadMaster()
    {
        var grades = await _api.GetAsync<List<GradeDto>>("/api/v1/grades") ?? [];
        GradeGrid.ItemsSource = grades.Select(g => new
        {
            g.Code, g.DisplayName,
            SizeList = string.Join("  ", g.Sizes.OrderBy(s => s.SortOrder).Select(s => s.Code)),
            AliasList = string.Join(", ", g.Aliases),
        }).ToList();

        BuyerGrid.ItemsSource = await _api.GetAsync<List<BuyerDto>>("/api/v1/buyers");
        BrokerGrid.ItemsSource = await _api.GetAsync<List<BrokerDto>>("/api/v1/brokers");
        if (_api.IsManager) await LoadPricesAsync();
    }

    private async void AddBuyer_Click(object sender, RoutedEventArgs e)
    {
        int.TryParse(NewBuyerTerms.Text, out int terms);
        string? failure = await _api.PostAsync("/api/v1/buyers",
            new { name = NewBuyerName.Text.Trim(), defaultTermsDays = terms, active = true });

        Say(failure ?? "Buyer added", ok: failure is null);
        if (failure is null) { NewBuyerName.Text = ""; LoadMaster(); await LoadPartiesAsync(); }
    }

    private async void AddBroker_Click(object sender, RoutedEventArgs e)
    {
        decimal.TryParse(NewBrokerPct.Text, out decimal pct);
        string? failure = await _api.PostAsync("/api/v1/brokers",
            new { name = NewBrokerName.Text.Trim(), defaultBrokerPct = pct, active = true });

        Say(failure ?? "Broker added", ok: failure is null);
        if (failure is null) { NewBrokerName.Text = ""; LoadMaster(); await LoadPartiesAsync(); }
    }

    // ── Phase 4 · owner dashboard ───────────────────────────────────────────

    /// The global filter bar, turned into the query string every widget shares.
    private string Filters()
    {
        var parts = new List<string>();
        string range = (RangePicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "MONTH";
        parts.Add($"range={range}");

        if (range == "CUSTOM")
        {
            if (FromDate.SelectedDate is { } from) parts.Add($"from={from:yyyy-MM-dd}");
            if (ToDate.SelectedDate is { } to) parts.Add($"to={to:yyyy-MM-dd}");
        }
        if (FilterBuyer.SelectedItem is BuyerDto buyer) parts.Add($"buyerId={buyer.BuyerId}");
        if (FilterGrade.SelectedItem is Grade grade && grade.Id != Guid.Empty) parts.Add($"gradeId={grade.Id}");

        return "?" + string.Join("&", parts);
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
        string query = Filters();

        var data = await _api.GetAsync<DashboardDto>($"/api/v1/dashboard/summary{query}");
        if (data is null) { Say("Manager or Owner only"); return; }

        KpiSales.Text = data.TotalSales.ToString("N2");
        KpiCarats.Text = data.CaratsSold.ToString("N2");
        KpiRate.Text = data.BlendedRate.ToString("N2");
        KpiOutstanding.Text = data.Outstanding.ToString("N2");
        KpiInventory.Text = data.InventoryValue.ToString("N2");
        KpiInventoryCt.Text = $"{data.InventoryCarats:N2} ct";
        KpiBroker.Text = data.BrokerCost.ToString("N2");
        KpiCount.Text = data.InvoiceCount.ToString();

        // W1's vs-prior: same number of days, immediately before this window.
        KpiSalesDelta.Text = data.PriorSales == 0
            ? "no prior period"
            : $"{(data.TotalSales - data.PriorSales) / data.PriorSales * 100m:+0.0;-0.0}% vs prior {data.PriorSales:N0}";

        // W7 · margin is Owner-only unless manager_sees_margin is on, so a 403 here is correct behaviour.
        var margin = await _api.GetAsync<MarginDto>($"/api/v1/dashboard/margin{query}");
        KpiMargin.Text = margin is null ? "—" : margin.Total.ToString("N2");
        KpiMarginBasis.Text = margin is null ? "Owner only" : "weighted-avg cost (Q3)";

        await LoadAlertsAsync();
        await LoadBreakdownAsync();

        DrillGrid.ItemsSource = await _api.GetAsync<List<InvoiceRow>>($"/api/v1/dashboard/invoices{query}");
    }

    /// W15 · the alerts strip. Hidden entirely when there is nothing wrong.
    private async Task LoadAlertsAsync()
    {
        var alerts = await _api.GetAsync<AlertsDto>("/api/v1/dashboard/alerts");
        if (alerts is null || (alerts.OverdueCount == 0 && alerts.LowStockCount == 0))
        {
            AlertStrip.Visibility = Visibility.Collapsed;
            return;
        }

        var parts = new List<string>();
        if (alerts.OverdueCount > 0) parts.Add($"{alerts.OverdueCount} overdue invoice(s) worth {alerts.OverdueValue:N2}");
        if (alerts.LowStockCount > 0) parts.Add($"{alerts.LowStockCount} grade/size below the low-stock threshold");
        if (alerts.NegativeCount > 0) parts.Add($"{alerts.NegativeCount} of them NEGATIVE");

        AlertText.Text = string.Join("   ·   ", parts);
        AlertStrip.Visibility = Visibility.Visible;
    }

    private void Breakdown_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (BarList is null) return;                       // fires during XAML load
        _ = LoadBreakdownAsync();
    }

    /// One list renders every breakdown — the server returns the same Bar shape for all of them.
    private async Task LoadBreakdownAsync()
    {
        string query = Filters();
        string which = (BreakdownPicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "period";
        string bucket = (PeriodBucket.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "day";
        PeriodBucket.Visibility = which == "period" ? Visibility.Visible : Visibility.Hidden;

        List<BarDto> bars = which switch
        {
            "period" or "salesperson" or "buyer" or "grade" =>
                await _api.GetAsync<List<BarDto>>($"/api/v1/dashboard/sales-by{query}&dimension={which}&bucket={bucket}") ?? [],
            "margin" => (await _api.GetAsync<MarginDto>($"/api/v1/dashboard/margin{query}"))?.Rows ?? [],
            _ => await _api.GetAsync<List<BarDto>>($"/api/v1/dashboard/{which}{query}") ?? [],
        };

        // Bar widths are computed here, not bound through a converter — one less moving part.
        decimal max = bars.Count == 0 ? 0 : bars.Max(b => Math.Abs(b.Value));
        bool money = which is not ("inventory-aging" or "top-movers");

        BarList.ItemsSource = bars.Select(b => new
        {
            b.Label,
            b.Secondary,
            ValueText = money ? b.Value.ToString("N2") : $"{b.Value:N2} ct",
            DeltaText = b.Delta is null or 0 ? "" : $"{b.Delta:+0.00;-0.00} vs prior",
            BarWidth = max == 0 ? 0 : (double)(Math.Abs(b.Value) / max) * 420,
        }).ToList();

        if (bars.Count == 0) Say("Nothing in this period — widen the range or clear the filters");
    }

    private async void LoadAudit_Click(object sender, RoutedEventArgs e)
        => AuditGrid.ItemsSource = await _api.GetAsync<List<AuditRow>>("/api/v1/audit");

    private async void LoadUsers_Click(object sender, RoutedEventArgs e)
        => UserGrid.ItemsSource = await _api.GetAsync<List<UserDto>>("/api/v1/users");

    private async void AddUser_Click(object sender, RoutedEventArgs e)
    {
        string role = (NewUserRole.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "SALES";
        string? failure = await _api.PostAsync("/api/v1/users", new
        {
            username = NewUserName.Text.Trim(),
            displayName = NewUserDisplay.Text.Trim(),
            password = NewUserPassword.Password,
            role,
        });

        Say(failure ?? "User created", ok: failure is null);
        if (failure is null) LoadUsers_Click(sender, e);
    }

    private async void DeactivateUser_Click(object sender, RoutedEventArgs e)
    {
        if (UserGrid.SelectedItem is not UserDto user) { Say("Select a user"); return; }

        // Deactivation, not deletion — the account stays on every record it ever touched.
        string? failure = await _api.DeleteAsync($"/api/v1/users/{user.UserId}");
        Say(failure ?? "User deactivated · their sessions are dead", ok: failure is null);
        LoadUsers_Click(sender, e);
    }

    // ── MDM-003 · price list ────────────────────────────────────────────────

    private void PriceGrade_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (PriceSizePicker is null) return;
        FillSizes(PriceGradePicker, PriceSizePicker);
    }

    private async Task LoadPricesAsync()
    {
        var prices = await _api.GetAsync<List<PriceRow>>("/api/v1/prices") ?? [];
        var grades = Catalogue.Grades.ToDictionary(g => g.Id, g => g.DisplayName);
        var sizes = Catalogue.AllSizes.ToDictionary(s => s.Id, s => s.Code);

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

        // The server closes the previous open price and opens a new one — prices are never edited
        // in place, so a valuation as of any past date still finds the price that applied then.
        string? failure = await _api.PostAsync("/api/v1/prices", new
        {
            gradeId = grade.Id, sizeId = size.Id, context,
            pricePerCt = price, effectiveFrom = DateOnly.FromDateTime(DateTime.Today),
        });

        Say(failure ?? $"{grade.DisplayName} {size.Code} {context} = {price:N2} from today", ok: failure is null);
        if (failure is null) await LoadPricesAsync();
    }

    // ── OPS-001 · backup ────────────────────────────────────────────────────

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        string? failure = await _api.PostAsync("/api/v1/admin/backup");
        Say(failure ?? "Backup taken", ok: failure is null);
        await LoadBackupsAsync();
    }

    private async Task LoadBackupsAsync()
        => BackupGrid.ItemsSource = await _api.GetAsync<List<BackupRow>>("/api/v1/admin/backups");

    private async void LoadSettings_Click(object sender, RoutedEventArgs e)
    {
        SettingGrid.ItemsSource = await _api.GetAsync<List<SettingRow>>("/api/v1/settings");
        if (_api.IsOwner) await LoadBackupsAsync();
    }

    private async void SaveSetting_Click(object sender, RoutedEventArgs e)
    {
        if (SettingGrid.SelectedItem is not SettingRow setting) { Say("Select a setting"); return; }

        string? failure = await _api.PutAsync("/api/v1/settings", new { key = setting.Key, value = SettingValue.Text });
        Say(failure ?? $"{setting.Key} = {SettingValue.Text}", ok: failure is null);
        if (failure is null) LoadSettings_Click(sender, e);
    }

    // ── plumbing ────────────────────────────────────────────────────────────

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

        switch (tab.Header?.ToString())
        {
            case "Invoices": LoadInvoices_Click(sender, e); break;
            case "Receivables": LoadReceivables_Click(sender, e); break;
            case "Stock": LoadStock_Click(sender, e); break;
            case "Master data": LoadMaster(); break;
            case "Dashboard": LoadDashboard_Click(sender, e); break;
            case "Audit": LoadAudit_Click(sender, e); break;
            case "Users": LoadUsers_Click(sender, e); break;
            case "Settings": LoadSettings_Click(sender, e); break;
        }
    }

    private static (Grade?, SizeBucket?) Pick(ComboBox gradeBox, ComboBox sizeBox)
        => (gradeBox.SelectedItem as Grade, sizeBox.SelectedItem as SizeBucket);

    private void Say(string message, bool ok = false)
    {
        Status.Foreground = ok ? System.Windows.Media.Brushes.SeaGreen : System.Windows.Media.Brushes.Firebrick;
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
