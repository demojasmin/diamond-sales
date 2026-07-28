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

        // Ctrl+S goes through the same busy scope as the button — it is the same operation and was
        // the one route that left the buttons live mid-save.
        InputBindings.Add(new KeyBinding(
            new RelayCommand(async () => { using (Busy(SaveDraft, "Saving…", AddLineButton, New, SaveDraft, Post)) await SaveDraftAsync(); }),
            Key.S, ModifierKeys.Control));
        DispositionGrid.ItemsSource = _dispositions;
        WhoAmI.Text = Db.CurrentUser?.FullName ?? "";
        Initials.Text = Initialise(Db.CurrentUser?.FullName);
        UsersTab.Visibility = Db.IsOwner ? Visibility.Visible : Visibility.Collapsed;

        // ItemTemplate, not ToString on the model: Grade and SizeBucket are wire types shared with
        // the database layer and have no business knowing how a combo renders them.
        //
        // And not DisplayMemberPath either. The design's combo template draws its CLOSED state from
        // SelectionBoxItemTemplate, which DisplayMemberPath leaves unset, so the dropdown listed
        // "No. 3" while the box itself showed "DiamondDesktop.Data.Grade" clipped to "Diamon". The
        // popup was right and the selection was wrong — on all thirteen of these.
        foreach (var box in new[] { IntakeGrade, ConvFromGrade, ConvToGrade, RejGrade, AdjGrade,
                                    FilterGrade, PriceGradePicker })
        {
            box.ItemsSource = Catalogue.Grades;
            box.ItemTemplate = (DataTemplate)FindResource("GradeNameTemplate");
            box.ItemContainerStyle = (Style)FindResource("GradeItemContainer");
        }

        // Size lists start with every bucket and narrow to that grade's sizes on selection —
        // opening one before picking a grade used to show an empty popup.
        foreach (var box in new[] { IntakeSize, ConvFromSize, ConvToSize, RejSize, AdjSize, PriceSizePicker })
        {
            box.ItemsSource = Catalogue.AllSizes;
            box.ItemTemplate = (DataTemplate)FindResource("SizeCodeTemplate");
            box.ItemContainerStyle = (Style)FindResource("SizeItemContainer");
        }

        // Focus the first field the user actually fills. The DatePicker was taking startup focus and
        // a DatePickerTextBox selects its whole contents when focused — so the app opened with the
        // date highlighted blue, looking like something was wrong with it. The date already defaults
        // to today; the buyer is the first real decision.
        Loaded += (_, _) => BuyerPicker.Focus();

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
        if (e.Key == Key.Delete) { DeleteLine(e); return; }
        if (e.Key != Key.Enter) return;

        Grid.CommitEdit(DataGridEditingUnit.Row, true);
        if (!ReferenceEquals(Grid.CurrentItem, _invoice.Lines.LastOrDefault())) return;

        AddLine();
        e.Handled = true;
    }

    /// <summary>
    /// Delete removes a whole parcel line, so it asks first. The grid's own row deletion is off
    /// (CanUserDeleteRows="False") — it removed a typed line silently, and on a screen where the
    /// hands never leave the keyboard that is one stray keystroke away from losing a parcel.
    /// </summary>
    private void DeleteLine(KeyEventArgs e)
    {
        // Inside an editor, Delete belongs to the text being typed, not to the invoice.
        if (Keyboard.FocusedElement is TextBox) return;
        if (Grid.CurrentItem is not SaleLine line) return;

        e.Handled = true;

        // Nothing typed, or it is the last row left to type into: just leave it alone.
        if (line.IsBlank || _invoice.Lines.Count == 1) return;

        string what = line.Grade is null ? $"line {_invoice.Lines.IndexOf(line) + 1}"
                                         : $"{line.Grade.DisplayName} · {line.GrossWeightCt:N2} ct";

        if (MessageBox.Show($"Remove {what}?", "Remove line",
                            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _invoice.Lines.Remove(line);
        }
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

    private async void SaveDraft_Click(object sender, RoutedEventArgs e)
    {
        using (Busy(SaveDraft, "Saving…", AddLineButton, New, SaveDraft, Post)) await SaveDraftAsync();
    }

    /// <summary>
    /// Disables the whole action row for the length of an operation and says what is happening on
    /// the button that started it. The `_saving` flag already stopped a double-click from booking
    /// two invoices — but silently: the buttons stayed lit and the second click just vanished, so
    /// a slow network looked like a dead app. This is the visible half of that guard.
    /// </summary>
    private IDisposable Busy(Button button, string label, params Button[] alsoDisable)
    {
        var row = alsoDisable.Length == 0 ? [button] : alsoDisable;
        object original = button.Content;

        foreach (var b in row) b.IsEnabled = false;
        button.Content = label;
        Mouse.OverrideCursor = Cursors.Wait;

        return new Scope(() =>
        {
            Mouse.OverrideCursor = null;
            button.Content = original;
            foreach (var b in row) b.IsEnabled = true;
        });
    }

    /// ponytail: a two-line IDisposable beats threading try/finally through every async handler.
    private sealed class Scope(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    private async Task<bool> SaveDraftAsync()
    {
        // Every save route — the button, Ctrl+S and Post — comes through here, so one guard closes
        // the hole for all three: the buttons stay live across the round trip, and a second click
        // arriving while the first insert is still in flight sees InvoiceId still null and books a
        // SECOND invoice for the same parcels.
        if (_saving) return false;

        Grid.CommitEdit();

        // Saying what is wrong and then leaving the user to find it is half a message. Send the
        // caret to the field that has to change.
        if (_invoice.Validate() is { } error) { Say(error); FocusFirstProblem(error); return false; }
        if (_invoice.BuyerId is not { } buyerId) { Say("Pick a buyer from the list"); BuyerPicker.Focus(); return false; }
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

    /// Puts focus where the message points. Line errors already colour their row, so the grid only
    /// needs scrolling to the offender; header problems need the caret in the control itself.
    private void FocusFirstProblem(string error)
    {
        if (error.StartsWith("Buyer")) { BuyerPicker.Focus(); return; }
        if (error.StartsWith("Terms")) { TermsBox.Focus(); TermsBox.SelectAll(); return; }

        if (_invoice.RealLines.FirstOrDefault(l => l.Error is not null) is { } bad)
        {
            Grid.ScrollIntoView(bad);
            Grid.CurrentCell = new DataGridCellInfo(bad, Grid.Columns[0]);
            Grid.Focus();
            return;
        }

        // "needs at least one line" — the row is there, it is just empty.
        if (_invoice.Lines.FirstOrDefault() is { } first)
        {
            Grid.CurrentCell = new DataGridCellInfo(first, Grid.Columns[0]);
            Grid.Focus();
        }
    }

    /// <summary>
    /// WPF remembers whichever month you last paged to. Click "‹" to glance at June, close the
    /// picker, reopen it — and it is still on June while the field says 27-07-2026. Every open
    /// starts at the date the invoice actually carries.
    /// </summary>
    private void InvoiceDatePicker_CalendarOpened(object sender, RoutedEventArgs e)
    {
        var picker = (DatePicker)sender;
        picker.DisplayDate = picker.SelectedDate ?? DateTime.Today;
    }

    /// <summary>
    /// Picking a date closes the calendar and hands focus back to the text box, which selects its
    /// whole contents — so the field was left showing a blue highlight over the date you had just
    /// chosen. Drops the caret at the end instead, keeping the field focused and editable.
    /// Queued at Input priority because WPF restores that selection after this event returns.
    /// </summary>
    private void InvoiceDatePicker_CalendarClosed(object sender, RoutedEventArgs e)
    {
        var picker = (DatePicker)sender;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            if (picker.Template?.FindName("PART_TextBox", picker) is TextBox box)
                box.Select(box.Text.Length, 0);
        });
    }

    private async void ReloadCatalogue_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        button.IsEnabled = false;
        try
        {
            await Catalogue.LoadAsync();
            Say(Catalogue.Grades.Count > 0
                ? $"Catalogue loaded · {Catalogue.Grades.Count} grades"
                : "Still no grades — the database returned an empty list", ok: Catalogue.Grades.Count > 0);
        }
        catch (Exception ex) { Say(ex.Message); }
        finally { button.IsEnabled = true; }
    }

    private async void Post_Click(object sender, RoutedEventArgs e)
    {
        // Post saves first, so the whole save-then-post round trip sits inside one busy scope —
        // otherwise the buttons came back to life in the gap between the two calls.
        using var busy = Busy(Post, "Posting…", AddLineButton, New, SaveDraft, Post);

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
    {
        using (Busy(InvoiceRefresh, "Loading…"))
            InvoiceGrid.ItemsSource = await Read(Repo.InvoicesAsync);
    }

    private async void Receipt_Click(object sender, RoutedEventArgs e)
    {
        if (InvoiceGrid.SelectedItem is not VInvoice invoice) { Say("Select an invoice first"); return; }
        // A cancelled invoice has had its stock returned and owes nothing. Cash booked against it
        // lands in the receipt ledger against a document that no longer exists.
        if (invoice.Status == InvoiceStatus.CANCELLED) { Say("That invoice is cancelled — nothing can be received against it"); return; }
        if (!decimal.TryParse(ReceiptAmount.Text, out decimal amount) || amount <= 0) { Say("Enter a receipt amount"); return; }

        string method = (ReceiptMethod.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "CASH";

        string? failure;
        using (Busy(RecordReceipt, "Recording…", InvoiceRefresh, RecordReceipt, CancelInvoiceButton))
        {
            failure = await Repo.ReceiptAsync(invoice.InvoiceId, amount, method);
        }
        if (failure is not null) { Say(failure); return; }

        ReceiptAmount.Text = "";
        Say($"Receipt recorded · {amount:N2} {method}", ok: true);
        LoadInvoices_Click(sender, e);
    }

    private async void CancelInvoice_Click(object sender, RoutedEventArgs e)
    {
        if (InvoiceGrid.SelectedItem is not VInvoice invoice) { Say("Select an invoice first"); return; }

        // Already cancelled: the RPC would refuse it anyway, and asking for a reason first makes
        // the user do work before being told no.
        if (invoice.Status == InvoiceStatus.CANCELLED) { Say("That invoice is already cancelled"); return; }

        // Name the invoice being reversed. "Cancel invoice" alone, with the selection off-screen on
        // a long list, is how the wrong one gets cancelled.
        if (MessageBox.Show(
                $"Cancel {invoice.InvoiceNo} for {invoice.BuyerName}?\n\n"
                + $"{invoice.AmountTotal:N2} will be reversed and the stock returned. This cannot be undone.",
                "Cancel invoice", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        // The RPC rejects a blank reason, so there is no point sending one.
        var reason = Prompt.Ask("Why is this invoice being cancelled?", "Cancel invoice");
        if (string.IsNullOrWhiteSpace(reason)) { Say("A cancellation reason is required"); return; }

        string? failure;
        using (Busy(CancelInvoiceButton, "Cancelling…", InvoiceRefresh, RecordReceipt, CancelInvoiceButton))
        {
            failure = await Repo.CancelAsync(invoice.InvoiceId, reason);
        }
        Say(failure ?? $"Cancelled {invoice.InvoiceNo} · stock returned", ok: failure is null);
        LoadInvoices_Click(sender, e);
    }

    private async void LoadReceivables_Click(object sender, RoutedEventArgs e)
    {
        List<VReceivablesAgeing>? rows;
        using (Busy(ReceivablesRefresh, "Loading…", ReceivablesRefresh, ReceivablesExport))
            rows = await Read(Repo.ReceivablesAsync);

        if (rows is null) return;

        ReceivablesGrid.ItemsSource = rows;

        // An empty run used to leave the previous total on screen, which reads as stale data rather
        // than as "nothing is owed".
        ReceivablesSummary.Text = rows.Count == 0
            ? ""
            : $"Total {rows.Sum(r => r.Outstanding):N2}   ·   " + string.Join("   ",
                rows.GroupBy(r => r.AgeBucket).OrderBy(g => g.Key)
                    .Select(g => $"{g.Key} {g.Sum(r => r.Outstanding):N2}"));
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

    /// Every bucket, filtered or not. The summary totals this, never the filtered view — a company
    /// position that changes when you tick a display checkbox would be worse than useless.
    private List<VStockPosition> _stock = [];
    private HashSet<(string Grade, string Size)> _traded = [];

    private async void LoadStock_Click(object sender, RoutedEventArgs e)
    {
        List<VStockPosition>? rows;
        HashSet<(string, string)>? traded;
        using (Busy(StockRefresh, "Loading…", StockRefresh, StockExport, RunInvariants))
        {
            rows = await Read(Repo.StockAsync);
            traded = await Read(Repo.MovementBucketsAsync);
        }

        if (rows is null) return;

        _stock = rows;
        _traded = traded ?? [];        // filter falls back to showing everything, never to hiding
        ApplyStockFilter();

        // Blank rather than a stale total when nothing comes back — a leftover figure over an empty
        // grid reads as data that failed to draw.
        StockSummary.Text = rows.Count == 0
            ? ""
            : $"{rows.Sum(r => r.BalanceCt):N4} ct   ·   value {rows.Sum(r => r.StockValue):N2}";
    }

    private void HideEmpty_Changed(object sender, RoutedEventArgs e) => ApplyStockFilter();

    private void ApplyStockFilter()
    {
        // IsChecked="True" in the markup raises Checked while InitializeComponent is still parsing,
        // and the checkbox sits above the grid in the tree — so this runs once with StockGrid still
        // null. Unhandled, that took the whole window down before it ever appeared.
        if (StockGrid is null) return;

        // A bucket is "empty" only if it holds nothing AND has never moved anything. Balance alone
        // is the wrong test: NO 1 BB × -2 sits at zero because its only invoice was cancelled, and
        // its ledger is exactly what someone would go looking for.
        var show = HideEmptyBuckets.IsChecked == true
            ? _stock.Where(r => r.BalanceCt != 0 || _traded.Contains((r.GradeCode, r.SizeCode)))
                    .ToList()
            : _stock;

        StockGrid.ItemsSource = show;
        if (_stock.Count != 0 && show.Count != _stock.Count)
            Say($"Showing {show.Count} of {_stock.Count} buckets", ok: true);
    }

    private async void Movements_Click(object sender, RoutedEventArgs e)
    {
        if (StockGrid.SelectedItem is not VStockPosition row) { Say("Select a grade × size row"); return; }

        List<VStockMovement>? rows;
        using (Busy(ShowMovements, "Loading…", ShowMovements, StockRefresh))
            rows = await Read(() => Repo.MovementsAsync(row.GradeCode, row.SizeCode));

        MovementGrid.ItemsSource = rows;
        if (rows is null) return;                       // Read already reported the failure

        // A bucket that has never been traded loads successfully and returns nothing. Leaving the
        // "press Show movements" prompt up made that look like the button had failed.
        if (rows.Count == 0)
            MovementHint.Text = $"No movements for {row.GradeCode} × {row.SizeCode}.\n" +
                                "Nothing has been taken in, sold or adjusted in this bucket.";

        // Name the bucket being shown. Two grids side by side, and nothing said which row the right
        // one belonged to once you looked away.
        Say(rows.Count == 0
                ? $"No movements for {row.GradeCode} × {row.SizeCode}"
                : $"Movements for {row.GradeCode} × {row.SizeCode} · {rows.Count} entries",
            ok: rows.Count > 0);
    }

    private async void Invariants_Click(object sender, RoutedEventArgs e)
    {
        List<VReconciliation>? rows;
        using (Busy(RunInvariants, "Checking…", RunInvariants, StockRefresh))
            rows = await Read(Repo.ReconciliationAsync);

        if (rows is null) return;

        var broken = rows.Where(r => !r.Reconciles).ToList();

        // Owned by this window: an unowned MessageBox is a separate top-level window that can land
        // behind the app, leaving a screen that looks frozen.
        MessageBox.Show(this,
            broken.Count == 0
                ? $"Stock moved out matches stock invoiced, on every grade × size.\n\n{rows.Count} buckets checked."
                : string.Join("\n", broken.Select(r =>
                    $"{r.GradeCode} × {r.SizeCode} — moved {r.MovedOutCt:N4} ct, invoiced {r.SoldOnInvoicesCt:N4} ct, off by {r.DiffCt:N4}")),
            "Ledger integrity", MessageBoxButton.OK,
            broken.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);

        Say(broken.Count == 0
            ? $"Invariants pass · {rows.Count} buckets"
            : $"{broken.Count} bucket(s) do not reconcile", ok: broken.Count == 0);
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

        if (Bounds.TooLarge(weight, "Weight") is { } tooHeavy) { Say(tooHeavy); IntakeWeight.Focus(); return; }
        if (Bounds.TooLarge(price, "Price per carat") is { } tooDear) { Say(tooDear); IntakePrice.Focus(); return; }
        if (!ConfirmLarge(weight, price)) return;

        string? failure;
        using (Busy((Button)sender, "Recording…"))
            failure = await Repo.IntakeAsync(grade.GradeId, size.SizeId, weight, price);

        if (failure is not null) { Say(failure); return; }

        // Clear the parcel figures on success. They used to stay put, so entering several parcels
        // in a row meant typing over the previous numbers — and a mistimed keystroke appended
        // instead of replacing, which is how 500 ct became 500,500.
        IntakeWeight.Text = "";
        IntakePrice.Text = "";
        IntakeWeight.Focus();
        Say($"Intake recorded · {weight:N4} ct", ok: true);
    }

    /// <summary>
    /// Asks about figures that are storable but improbable. It invents no limit — the workbook's
    /// largest parcel is 232.86 ct at 63,000/ct, so these thresholds sit far outside normal
    /// trading without forbidding anything.
    /// </summary>
    private bool ConfirmLarge(decimal weight, decimal price)
    {
        var odd = new List<string>();
        if (Bounds.NeedsConfirming(weight, Bounds.LargeWeightCt)) odd.Add($"{weight:N4} carats");
        if (Bounds.NeedsConfirming(price, Bounds.LargePricePerCt)) odd.Add($"{price:N2} per carat");
        if (odd.Count == 0) return true;

        // Owned by this window: an unowned MessageBox is a separate top-level window that can end up
        // BEHIND the app, leaving a screen that looks frozen because the click went to a dialog
        // nobody can see.
        return MessageBox.Show(this,
            $"That is {string.Join(" at ", odd)}.\n\nIs that right?",
            "Unusually large figure", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        var (fromGrade, fromSize) = Pick(ConvFromGrade, ConvFromSize);
        var (toGrade, toSize) = Pick(ConvToGrade, ConvToSize);
        if (fromGrade is null || fromSize is null || toGrade is null || toSize is null) { Say("Pick both sides"); return; }
        if (!decimal.TryParse(ConvWeight.Text, out decimal weight) || weight <= 0) { Say("Weight must be positive"); return; }
        if (!TypedPrice(ConvPrice, out decimal? price)) { Say("Price/ct must be a number, or left blank"); return; }

        if (Bounds.TooLarge(weight, "Weight") is { } tooHeavy) { Say(tooHeavy); ConvWeight.Focus(); return; }
        if (price is { } p && Bounds.TooLarge(p, "Price per carat") is { } tooDear) { Say(tooDear); ConvPrice.Focus(); return; }
        if (!ConfirmLarge(weight, price ?? 0)) return;

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

        if (Bounds.TooLarge(weight, "Weight") is { } tooHeavy) { Say(tooHeavy); RejWeight.Focus(); return; }
        if (price is { } rp && Bounds.TooLarge(rp, "Price per carat") is { } tooDear) { Say(tooDear); RejPrice.Focus(); return; }
        if (!ConfirmLarge(weight, price ?? 0)) return;

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

        // Signed on purpose (docs/12 §7) — TooLarge tests the magnitude, so a big correction
        // downwards is checked the same as a big one upwards.
        if (Bounds.TooLarge(weight, "Weight") is { } tooHeavy) { Say(tooHeavy); AdjWeight.Focus(); return; }
        if (!ConfirmLarge(weight, 0)) return;

        string? failure = await Repo.AdjustAsync(grade.GradeId, size.SizeId, weight, AdjReason.Text.Trim());
        Say(failure ?? "Adjustment recorded — it stays visible in the ledger forever", ok: failure is null);
    }

    // ── Master data ─────────────────────────────────────────────────────────

    private async Task LoadMasterAsync()
    {
        // Held whole so search and the status filter work on the full catalogue rather than on
        // whatever the grid happens to be showing.
        _grades = await Read(Repo.GradesAsync) ?? [];
        ApplyGradeFilter();

        SizeGrid.ItemsSource = await Read(Repo.SizesAsync);
        var buyers = await Read(Repo.BuyersAsync) ?? [];
        var brokers = await Read(Repo.BrokersAsync) ?? [];
        BuyerGrid.ItemsSource = buyers;
        BrokerGrid.ItemsSource = brokers;
        BuyerCount.Text = buyers.Count.ToString();
        BrokerCount.Text = brokers.Count.ToString();
        if (Db.IsManagerOrOwner) await LoadPricesAsync();
    }

    private List<Grade> _grades = [];
    private void GradeFilter_Changed(object sender, TextChangedEventArgs e)
    {
        // The clear button only earns its space once there is something to clear.
        if (GradeSearchClear is not null)
            GradeSearchClear.Visibility = string.IsNullOrEmpty(GradeSearch.Text)
                ? Visibility.Collapsed : Visibility.Visible;
        ApplyGradeFilter();
    }

    /// <summary>
    /// WPF lets the mouse wheel change a ComboBox's selection whenever the pointer happens to be
    /// over it. On this page that silently rewrote the grade a price was about to be saved
    /// against — scrolling the page is not a choice about which grade you meant. The wheel is
    /// swallowed and passed to the nearest scrollable parent instead.
    /// </summary>
    private void NoWheelChange(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ComboBox { IsDropDownOpen: false } combo) return;

        e.Handled = true;
        var bubbled = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = combo,
        };
        (combo.Parent as UIElement)?.RaiseEvent(bubbled);
    }

    private void ClearGradeSearch_Click(object sender, RoutedEventArgs e)
    {
        GradeSearch.Clear();
        GradeSearch.Focus();          // carry on typing rather than hunting for the box again
    }

    private void GradeStatus_Changed(object sender, SelectionChangedEventArgs e) => ApplyGradeFilter();

    /// <summary>
    /// Client-side search and status filter over the loaded catalogue. Purely a view concern —
    /// no query changes, and the grid still binds the same Grade objects, so alias editing and its
    /// save path are untouched.
    /// </summary>
    private void ApplyGradeFilter()
    {
        if (GradeGrid is null) return;                 // fires while the tab is still being parsed

        string term = GradeSearch?.Text.Trim() ?? "";
        int status = GradeStatusFilter?.SelectedIndex ?? 0;

        var shown = _grades.Where(g =>
            (status == 0 || (status == 1) == g.Active) &&
            (term.Length == 0
             || g.Code.Contains(term, StringComparison.OrdinalIgnoreCase)
             || (g.DisplayName ?? "").Contains(term, StringComparison.OrdinalIgnoreCase)
             || (g.Aliases ?? "").Contains(term, StringComparison.OrdinalIgnoreCase))).ToList();

        if (GradeCount is not null)
            GradeCount.Text = shown.Count == _grades.Count
                ? $"{_grades.Count}"
                : $"{shown.Count} of {_grades.Count}";

        // The grid scrolls; 23 grades never needed paging, and a pager over a scrollable list is
        // two ways to move through the same data.
        GradeGrid.ItemsSource = shown;
    }

    /// <summary>
    /// Shared checks for the two add-forms. Both used to call TryParse and throw the result away,
    /// so "abc" silently became 0 — a buyer saved with terms nobody chose, and no complaint.
    /// </summary>
    private static string? BadName(string name, IEnumerable<string> existing, string what)
    {
        if (name.Length == 0) return $"Enter a {what} name.";
        if (name.Length < 2) return $"That {what} name is too short.";
        return existing.Any(n => string.Equals(n.Trim(), name, StringComparison.OrdinalIgnoreCase))
            ? $"{name} is already in the list."
            : null;
    }

    /// <summary>
    /// The buyer rules, unchanged and now in one place: the dialog calls this, so the modal and the
    /// rules cannot drift apart. Returns an error, or null with <paramref name="terms"/> set.
    /// </summary>
    private static string? ValidateBuyer(string name, string termsText,
                                         IEnumerable<string> existing, out int terms)
    {
        terms = 0;
        if (BadName(name, existing, "buyer") is { } nameProblem) return nameProblem;
        if (!int.TryParse(termsText, out terms)) return "Terms must be a whole number of days.";

        // 0 is valid — the workbook has invoices due on the day they are raised (docs/04 A-3).
        return terms is < 0 or > 365 ? "Terms must be between 0 and 365 days." : null;
    }

    private static string? ValidateBroker(string name, string pctText,
                                          IEnumerable<string> existing, out decimal pct)
    {
        pct = 0;
        if (BadName(name, existing, "broker") is { } nameProblem) return nameProblem;
        if (!decimal.TryParse(pctText, out pct)) return "Broker % must be a number.";

        // CALC-1 multiplies by (100 - brokerPct)/100, so anything outside 0-100 inverts the amount.
        return pct is < 0 or > 100 ? "Broker % must be between 0 and 100." : null;
    }

    private async void AddBuyer_Click(object sender, RoutedEventArgs e)
    {
        var existing = (BuyerGrid.ItemsSource as IEnumerable<Buyer>)?.Select(b => b.Name).ToList() ?? [];

        var values = AppFormDialog.Show(this, "Add buyer", "Add a buyer",
            "Terms default the due date on every invoice raised for this buyer.",
            [new FormFieldSpec("Buyer name"), new FormFieldSpec("Terms (days)", "0", Numeric: true, MaxLength: 4)],
            v => ValidateBuyer(v[0], v[1], existing, out _),
            "Add buyer");
        if (values is null) return;                       // cancelled

        ValidateBuyer(values[0], values[1], existing, out int terms);
        string? failure = await Repo.AddBuyerAsync(values[0], terms);

        Say(failure ?? $"Buyer “{values[0]}” added", ok: failure is null);
        if (failure is null) { await LoadMasterAsync(); await LoadPartiesAsync(); }
    }

    private async void AddBroker_Click(object sender, RoutedEventArgs e)
    {
        var existing = (BrokerGrid.ItemsSource as IEnumerable<Broker>)?.Select(b => b.Name).ToList() ?? [];

        var values = AppFormDialog.Show(this, "Add broker", "Add a broker",
            "The default commission is applied to new invoices; it stays editable per invoice.",
            [new FormFieldSpec("Broker name"), new FormFieldSpec("Default %", "1", Numeric: true, MaxLength: 6)],
            v => ValidateBroker(v[0], v[1], existing, out _),
            "Add broker");
        if (values is null) return;

        ValidateBroker(values[0], values[1], existing, out decimal pct);
        string? failure = await Repo.AddBrokerAsync(values[0], pct);

        Say(failure ?? $"Broker “{values[0]}” added", ok: failure is null);
        if (failure is null) { await LoadMasterAsync(); await LoadPartiesAsync(); }
    }

    /// <summary>
    /// Saves an edited alias list. Aliases are the only editable cell on the grid — they decide
    /// whether a workbook spelling resolves on import, and there was previously nowhere in the app
    /// to see them, let alone correct one.
    /// </summary>
    private async void GradeAlias_Committed(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (e.Row.Item is not Grade grade) return;
        if (e.EditingElement is not TextBox box) return;

        string typed = box.Text.Trim();
        string tidy = string.Join(';', typed.Split(';', StringSplitOptions.RemoveEmptyEntries
                                                        | StringSplitOptions.TrimEntries));
        box.Text = tidy;

        if (string.Equals(tidy, grade.Aliases?.Trim() ?? "", StringComparison.Ordinal)) return;

        string? failure = await Repo.SetGradeAliasesAsync(grade.GradeId, tidy);
        if (failure is not null)
        {
            Say(failure);
            await LoadMasterAsync();          // put the grid back to what the database holds
            return;
        }
        grade.Aliases = tidy;
        Say($"Aliases for {grade.Code} saved", ok: true);
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

    // ── Excel import ────────────────────────────────────────────────────────

    /// <summary>
    /// Validate the whole file, warn before replacing anything, then import — in that order, and
    /// never overlapping. Nothing is written until the file has passed every check and the user has
    /// said yes to losing the previous import, so a bad workbook cannot leave the database
    /// half-way between two datasets.
    /// </summary>
    private async void ImportExcel_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the sale workbook to import",
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            CheckFileExists = true,
        };
        if (picker.ShowDialog(this) != true) return;

        ImportPlan? plan;
        List<Grade>? grades;
        List<SizeBucket>? sizes;

        using (Busy(ImportExcel, "Checking…", ImportExcel))
        {
            grades = await Read(Repo.GradesAsync);
            sizes = await Read(Repo.SizesAsync);
            if (grades is null || sizes is null) return;

            // Grades carry aliases for the spellings the legacy workbooks use ("II" → "NO II").
            // Sizes need no stored aliases: their variants are notation (MDM-004), so a rule covers
            // every catalogue code without anything to seed or maintain.
            var gradeMap = SaleFileImport.AliasMap(grades.Select(g => (g.Code, g.Aliases)));
            var sizeMap = SaleFileImport.SizeAliasMap(sizes.Select(s => s.Code));

            // Parsing a large sheet is CPU work; off the UI thread so the window stays alive.
            plan = await Task.Run(() => SaleFileImport.Plan(picker.FileName, gradeMap, sizeMap));
        }

        // 1-3 · nothing missing, or stop and say exactly what.
        if (!plan.IsValid)
        {
            ImportStatus.Text = "";
            MessageBox.Show(this, SaleFileImport.ProblemText(plan), "Cannot import this file",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            Say($"Import cancelled · {plan.Problems.Count} problem(s) in the file");
            return;
        }

        // 4-5 · existing data is destroyed, so say so plainly and get a yes first.
        var existing = await Read(Repo.ImportedInvoiceIdsAsync);
        if (existing is null) return;

        string scope = existing.Count == 0
            ? "There is no previous import to replace."
            : $"This will DELETE the {existing.Count:N0} previously imported invoice(s), " +
              "along with their lines and receipts.";

        // Skipped rows are named before the user commits, never after. Silently importing 1,369 of
        // 1,437 rows and reporting only the good news is how a migration loses data unnoticed.
        var skipped = plan.SkippedRows == 0
            ? null
            : SaleFileImport.ExceptionText(plan)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimStart(' ', '•').Trim());

        bool confirmed = AppDialog.Confirm(this,
            title: "Replace imported sales data",
            headline: "Replace imported sales data?",
            subhead: $"{System.IO.Path.GetFileName(picker.FileName)} · checked and ready",
            facts:
            [
                ("Invoices", $"{plan.Invoices.Count:N0}"),
                ("Lines", $"{plan.LineCount:N0}"),
                ("Receipts", $"{plan.ReceiptCount:N0}"),
                ("Dates", $"{plan.FirstDate:dd MMM yyyy} — {plan.LastDate:dd MMM yyyy}"),
            ],
            emphasis: scope + " Invoices entered in the app are numbered separately and are not "
                      + "affected.",
            listTitle: plan.SkippedRows == 0
                ? null
                : $"{plan.SkippedRows:N0} row(s) will be skipped and not imported",
            bullets: skipped,
            primaryText: "Import now",
            secondaryText: "Cancel");
        if (!confirmed) { Say("Import cancelled"); return; }

        // 6-9 · clear the old, write the new, report what landed.
        // The whole window is disabled behind this, so a second import cannot be started and no
        // screen can read the tables while they are half-replaced. try/finally, because a network
        // failure must still give the app back.
        ImportResult? result;
        var progressDialog = AppProgressDialog.Start(this, "Importing data, please wait…");
        try
        {
            result = await Read(() => SaleImporter.RunAsync(plan, progressDialog.Progress));
        }
        finally
        {
            progressDialog.Finish();
        }

        ImportStatus.Text = "";
        if (result is null) { Say("Import failed — nothing further was written"); return; }

        // Same three notes as before, now as their own lines rather than run together in a
        // paragraph — each is a separate thing the user may need to act on.
        var notes = new List<string>();
        if (plan.SkippedRows > 0)
            notes.Add($"{plan.SkippedRows:N0} row(s) were skipped: the catalogue could not resolve "
                      + "their grade or size.");
        if (result.BuyersCreated > 0 || result.BrokersCreated > 0)
            notes.Add($"Added {result.BuyersCreated} buyer(s) and {result.BrokersCreated} broker(s) "
                      + "named in the file.");
        if (plan.SplitSrCount > 0)
            notes.Add($"{plan.SplitSrCount} Sr. number(s) covered rows with different dates or "
                      + "buyers and became separate invoices.");

        AppDialog.Info(this,
            title: "Import complete",
            headline: "Import complete",
            subhead: $"{System.IO.Path.GetFileName(picker.FileName)} · "
                     + $"{plan.FirstDate:dd MMM yyyy} — {plan.LastDate:dd MMM yyyy}",
            facts:
            [
                ("Invoices imported", $"{result.Invoices:N0}"),
                ("Lines imported", $"{result.Lines:N0}"),
                ("Receipts imported", $"{result.Receipts:N0}"),
                ("Previous invoices replaced", $"{result.DeletedInvoices:N0}"),
            ],
            listTitle: notes.Count == 0 ? null : "Worth knowing",
            bullets: notes,
            note: notes.Count == 0 ? "Every row in the workbook was imported." : null);
        Say($"Imported {result.Invoices:N0} invoices · {result.Lines:N0} lines · " +
            $"{result.Receipts:N0} receipts", ok: true);
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

        // Every message on every screen passes through here, so this is the one place a database
        // failure has to be made readable. The original is kept on the tooltip — a support call
        // still needs the real text, it just should not be the first thing a user reads.
        Status.Text = Friendly.Message(message);
        Status.ToolTip = Friendly.Translates(message) ? message : null;
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
