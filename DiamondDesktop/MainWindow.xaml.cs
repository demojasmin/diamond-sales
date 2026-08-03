using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
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
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); Status.Text = ""; Status.ToolTip = null; };

        // A failure has no timer on purpose, so it needs a way out that is not "wait for the next
        // message". Clicking the bar dismisses whatever is in it.
        Status.MouseLeftButtonUp += (_, _) =>
        {
            _statusTimer.Stop();
            Status.Text = "";
            Status.ToolTip = null;
        };

        // Master Data shortcuts. Scoped by hand rather than by InputBindings: Ctrl+N and Ctrl+F
        // belong to other screens too, and a window-level binding would fire on all of them.
        PreviewKeyDown += MasterData_Keys;

        DispositionGrid.ItemsSource = _dispositions;

        // A config file that could not be read falls back to the shipped values rather than
        // killing a process that has no window yet — but silently pointing at the wrong project
        // is exactly the kind of thing nobody notices until the numbers are wrong, so it is said
        // as soon as there is somewhere to say it.
        if (AppSettings.Problem is { } configProblem) Say(configProblem);

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
                                    LedgerGrade, FilterGrade, PriceGradePicker })
        {
            box.ItemsSource = Catalogue.Grades;
            box.ItemTemplate = (DataTemplate)FindResource("GradeNameTemplate");
            box.ItemContainerStyle = (Style)FindResource("GradeItemContainer");
        }

        // Size lists start with every bucket and narrow to that grade's sizes on selection —
        // opening one before picking a grade used to show an empty popup.
        foreach (var box in new[] { IntakeSize, ConvFromSize, ConvToSize, RejSize, AdjSize,
                                    LedgerSize, PriceSizePicker })
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
        // Starting a new invoice throws away whatever is on screen. Typed lines that were never
        // saved are gone with no way back, so they are worth one question first. Only asked when
        // there is something to lose: a saved draft has an InvoiceId, and a blank form has no
        // real lines. Post calls this too, and by then the invoice is saved.
        if (_invoice.InvoiceId is null && _invoice.RealLines.Count > 0
            && MessageBox.Show(this,
                   $"This invoice has {_invoice.RealLines.Count} line(s) that have not been saved.\n\n"
                   + "Start a new one and lose them?",
                   "Unsaved invoice", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

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
        BeginBusy();                      // writes get the same bar as reads

        return new Scope(() =>
        {
            Mouse.OverrideCursor = null;
            button.Content = original;
            foreach (var b in row) b.IsEnabled = true;
            EndBusy();
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

    /// Every invoice the screen shows. Repo.InvoicesAsync is unchanged.
    private List<VInvoice> _invoices = [];

    private async void LoadInvoices_Click(object sender, RoutedEventArgs e)
    {
        List<VInvoice>? rows;
        using (Busy(InvoiceRefresh, "Loading…"))
            rows = await Read(Repo.InvoicesAsync);
        if (rows is null) return;

        _invoices = rows;
        InvoiceChip.Text = $"{_invoices.Count:N0} invoice{(_invoices.Count == 1 ? "" : "s")}";

        // The buyer list can only offer buyers that actually appear.
        object? keep = InvoiceBuyer.SelectedItem;
        InvoiceBuyer.ItemsSource = new[] { "All buyers" }
            .Concat(_invoices.Select(i => i.BuyerName).Distinct().OrderBy(b => b, StringComparer.Ordinal))
            .ToList();
        InvoiceBuyer.SelectedItem = keep is string s && InvoiceBuyer.Items.Contains(s) ? s : "All buyers";

        ApplyInvoiceFilter();
    }

    private void InvoiceFilter_Changed(object sender, RoutedEventArgs e) => ApplyInvoiceFilter();

    private void ClearInvoiceSearch_Click(object sender, RoutedEventArgs e)
    {
        InvoiceSearch.Clear();
        InvoiceSearch.Focus();
    }

    private void ClearInvoiceFilters_Click(object sender, RoutedEventArgs e)
    {
        InvoiceStatusFilter.SelectedIndex = 0;
        if (InvoiceBuyer.Items.Count > 0) InvoiceBuyer.SelectedIndex = 0;
        InvoiceSearch.Clear();
        ApplyInvoiceFilter();
    }

    /// <summary>
    /// Narrows what is listed. No query runs — the same invoices are already in memory, which is
    /// why the count above the list always describes the list under it.
    ///
    /// Status matches the badge word rather than the raw column: the grid shows "Overdue", and a
    /// filter that cannot find what is written on screen is not a filter.
    /// </summary>
    private void ApplyInvoiceFilter()
    {
        if (InvoiceGrid is null) return;

        string status = (InvoiceStatusFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        string buyer = InvoiceBuyer.SelectedIndex <= 0 ? "" : InvoiceBuyer.SelectedItem as string ?? "";
        string term = InvoiceSearch?.Text.Trim() ?? "";

        var shown = _invoices
            .Where(i => status.Length == 0 || InvoiceStateConverter.State(i) == status)
            .Where(i => buyer.Length == 0 || i.BuyerName == buyer)
            .Where(i => term.Length == 0
                        || (i.InvoiceNo ?? "").Contains(term, StringComparison.OrdinalIgnoreCase)
                        || (i.BuyerName ?? "").Contains(term, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (InvoiceSearchClear is not null)
            InvoiceSearchClear.Visibility = string.IsNullOrEmpty(InvoiceSearch?.Text)
                ? Visibility.Collapsed : Visibility.Visible;

        InvoiceGrid.ItemsSource = shown;

        // The figures go in the page header, where every other page keeps its totals. They used to
        // sit on their own line between the filters and the table, which cost a row of invoices to
        // say what the header could say for free.
        decimal owed = shown.Sum(i => i.Outstanding);
        InvoiceSubtitle.Text = shown.Count == 0
            ? "Search, edit, post, receipts"
            : $"{shown.Count:N0} invoice{(shown.Count == 1 ? "" : "s")} · {Money.Short(owed)} outstanding";

        // The line above the table earns its space only when it has something the header cannot
        // say: that the filters matched nothing.
        InvoiceCount.Text = shown.Count == 0
            ? "Nothing matches these filters"
            : "Select an invoice to record a receipt, print or cancel it";
    }

    /// <summary>
    /// Makes a star column re-share the width available to it. A DataGrid star column keeps the
    /// width it computed before the grid changed size, so a drawer opening beside it leaves the
    /// column either overflowing or — more often — well short, with dead space to its right.
    ///
    /// Two dispatcher passes, not one: setting Auto and star back to back in a single callback
    /// leaves the column exactly where it was, because the grid never measures the Auto state and
    /// the star has nothing to re-share from. Auto has to survive one layout pass first.
    /// </summary>
    private void ResharStar(DataGridColumn? column)
    {
        if (column is null) return;
        Dispatcher.BeginInvoke(() =>
        {
            column.Width = DataGridLength.Auto;
            Dispatcher.BeginInvoke(
                () => column.Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                DispatcherPriority.Loaded);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Fills the detail drawer from the selected row. Nothing is fetched: every figure below is
    /// already on the VInvoice the grid is bound to.
    /// </summary>
    private void InvoiceRow_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (InvoiceDetailCard is null) return;

        bool open = InvoiceGrid.SelectedItem is VInvoice;
        InvoiceDetailCard.DataContext = InvoiceGrid.SelectedItem;
        InvoiceDetailCard.Visibility = open ? Visibility.Visible : Visibility.Collapsed;

        // Hand the layout to the size handler rather than setting the two column widths here.
        // It does the same thing AND re-shares the star column, which opening the drawer needs
        // just as much as a resize does: the split itself does not change width when the drawer
        // appears, so SizeChanged never fires, the star column kept whatever it had computed
        // during an intermediate pass, and the list sat 296px short of its own card.
        InvoiceSplit_SizeChanged(InvoiceSplit, null!);

        // A summary, not the invoice. Amount, what has been received against it, what is still
        // owed and when it was due — enough to decide whether to take a payment. Carats, rates,
        // broker splits and document details belong on the printed bill, which carries them all.
        InvoiceFacts.ItemsSource = InvoiceGrid.SelectedItem is not VInvoice inv ? null : new[]
        {
            new { Label = "Amount", Value = Money.Short(inv.AmountTotal) },
            new { Label = "Received", Value = Money.Short(inv.Received) },
            new { Label = "Outstanding", Value = Money.Short(inv.Outstanding) },
            new { Label = "Due", Value = inv.DueDate.ToString("dd MMM yyyy")
                                       + (inv.IsOverdue ? $" · {inv.DaysOverdue:N0} days overdue" : "") },
        };

        InvoiceSplit_SizeChanged(InvoiceSplit, null!);
    }

    // 280, not 330: the drawer is a four-line summary and two actions now, and every pixel it
    // gives back is a pixel of invoice list.
    private const double InvoiceDrawerWidth = 280;

    private void CloseInvoiceDetail_Click(object sender, RoutedEventArgs e) => InvoiceGrid.UnselectAll();

    /// The drawer keeps its width; the list gives way, down to the MinWidth on its column.
    private void InvoiceSplit_SizeChanged(object sender, SizeChangedEventArgs? e)
    {
        if (InvoiceDetailCard is null) return;

        bool open = InvoiceDetailCard.Visibility == Visibility.Visible;
        double width = e?.NewSize.Width ?? InvoiceSplit.ActualWidth;
        if (width <= 0) return;

        // Below this the list would be narrower than its own columns, so the drawer stands down.
        bool room = width - InvoiceDrawerWidth - 16 >= 420;
        bool showing = open && room;
        InvoiceDetailCol.Width = new GridLength(showing ? InvoiceDrawerWidth : 0);
        InvoiceDetailGap.Width = new GridLength(showing ? 16 : 0);
        InvoiceDetailCard.Visibility = showing ? Visibility.Visible : Visibility.Collapsed;

        // All seven columns fit beside the drawer — 700px of fixed columns plus a star for the
        // buyer — but only if the star actually re-shares. A DataGrid star column keeps the width
        // it computed before the grid narrowed, so opening the drawer left Buyer at its old size
        // and pushed Amount, Outstanding and Due off the right edge behind a scrollbar.
        //
        // Auto makes the column re-derive; star then divides what is left. Queued at Loaded so it
        // runs after this layout pass rather than inside it.
        ResharStar(ColBuyer);
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

        _receivables = rows;
        ReceivablesChip.Text = $"{rows.Count:N0} invoice{(rows.Count == 1 ? "" : "s")}";

        // An empty run used to leave the previous total on screen, which reads as stale data rather
        // than as "nothing is owed".
        ReceivablesSummary.Text = rows.Count == 0
            ? "Nothing outstanding"
            : $"Total {Money.Short(rows.Sum(r => r.Outstanding))} across "
              + $"{rows.Select(r => r.BuyerName).Distinct().Count():N0} buyer(s)";

        // Both lists offer only what actually came back.
        object? keepBucket = ReceivablesBucket.SelectedItem;
        ReceivablesBucket.ItemsSource = new[] { "All ages" }
            .Concat(rows.Select(r => r.AgeBucket).Distinct().OrderBy(Age)).ToList();
        ReceivablesBucket.SelectedItem =
            keepBucket is string kb && ReceivablesBucket.Items.Contains(kb) ? kb : "All ages";

        object? keepBuyer = ReceivablesBuyer.SelectedItem;
        ReceivablesBuyer.ItemsSource = new[] { "All buyers" }
            .Concat(rows.Select(r => r.BuyerName).Distinct().OrderBy(b => b, StringComparer.Ordinal)).ToList();
        ReceivablesBuyer.SelectedItem =
            keepBuyer is string kn && ReceivablesBuyer.Items.Contains(kn) ? kn : "All buyers";

        ApplyReceivablesFilter();
    }

    /// Every receivable the screen shows. Repo.ReceivablesAsync is unchanged.
    private List<VReceivablesAgeing> _receivables = [];

    /// <summary>
    /// Ages in the order a collections person reads them. Sorting the bucket labels as text happens
    /// to work for these four, but only by accident — "100+" would land before "31-60".
    /// </summary>
    private static int Age(string bucket) => bucket switch
    {
        "0-30" => 0, "31-60" => 1, "61-90" => 2, "90+" => 3, _ => 4,
    };

    private void ReceivablesFilter_Changed(object sender, RoutedEventArgs e) => ApplyReceivablesFilter();

    private void ClearReceivablesSearch_Click(object sender, RoutedEventArgs e)
    {
        ReceivablesSearch.Clear();
        ReceivablesSearch.Focus();
    }

    private void ClearReceivablesFilters_Click(object sender, RoutedEventArgs e)
    {
        if (ReceivablesBucket.Items.Count > 0) ReceivablesBucket.SelectedIndex = 0;
        if (ReceivablesBuyer.Items.Count > 0) ReceivablesBuyer.SelectedIndex = 0;
        ReceivablesSearch.Clear();
        ApplyReceivablesFilter();
    }

    /// <summary>
    /// Narrows the list and re-totals the ageing tiles from what is shown. No query runs — the same
    /// rows are already in memory, so the tiles always describe the list beneath them.
    /// </summary>
    private void ApplyReceivablesFilter()
    {
        if (ReceivablesGrid is null) return;

        string bucket = ReceivablesBucket.SelectedIndex <= 0 ? "" : ReceivablesBucket.SelectedItem as string ?? "";
        string buyer = ReceivablesBuyer.SelectedIndex <= 0 ? "" : ReceivablesBuyer.SelectedItem as string ?? "";
        string term = ReceivablesSearch?.Text.Trim() ?? "";

        var shown = _receivables
            .Where(r => bucket.Length == 0 || r.AgeBucket == bucket)
            .Where(r => buyer.Length == 0 || r.BuyerName == buyer)
            .Where(r => term.Length == 0
                        || (r.InvoiceNo ?? "").Contains(term, StringComparison.OrdinalIgnoreCase)
                        || r.BuyerName.Contains(term, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (ReceivablesSearchClear is not null)
            ReceivablesSearchClear.Visibility = string.IsNullOrEmpty(ReceivablesSearch?.Text)
                ? Visibility.Collapsed : Visibility.Visible;

        ReceivablesGrid.ItemsSource = shown;

        void Tile(string age, TextBlock value, TextBlock caption)
        {
            var inBucket = shown.Where(r => r.AgeBucket == age).ToList();
            value.Text = Money.Short(inBucket.Sum(r => r.Outstanding));
            caption.Text = $"{inBucket.Count:N0} invoice{(inBucket.Count == 1 ? "" : "s")}";
        }

        Tile("0-30", RecKpiFresh, RecKpiFreshCount);
        Tile("31-60", RecKpi3060, RecKpi3060Count);
        Tile("61-90", RecKpi6190, RecKpi6190Count);
        Tile("90+", RecKpi90, RecKpi90Count);

        ReceivablesCount.Text = shown.Count == 0
            ? "Nothing matches these filters"
            : $"{shown.Count:N0} invoice{(shown.Count == 1 ? "" : "s")} · "
              + $"{Money.Short(shown.Sum(r => r.Outstanding))} outstanding";
    }

    /// <summary>
    /// Shows the selected row's buyer in full: every unpaid invoice of theirs, the oldest, and the
    /// split by age. Grouped from rows already loaded — picking a row fetches nothing.
    /// </summary>
    private void ReceivablesRow_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (ReceivablesDetailCard is null) return;

        bool open = ReceivablesGrid.SelectedItem is VReceivablesAgeing;
        ReceivablesDetailCard.Visibility = open ? Visibility.Visible : Visibility.Collapsed;

        if (ReceivablesGrid.SelectedItem is VReceivablesAgeing row)
        {
            // Their whole position, not just this invoice — the filters above do not narrow it,
            // because a part of what is owed is not what a collections call is about.
            var mine = _receivables.Where(r => r.BuyerId == row.BuyerId).ToList();
            var oldest = mine.MaxBy(r => r.DaysOverdue);

            ReceivablesDetailName.Text = row.BuyerName;
            ReceivablesDetailTotal.Text = Money.Short(mine.Sum(r => r.Outstanding));
            ReceivablesDetailMeta.Text =
                $"{mine.Count:N0} unpaid invoice{(mine.Count == 1 ? "" : "s")}"
                + (oldest is null ? "" : $" · oldest {oldest.DaysOverdue:N0} days");

            ReceivablesFacts.ItemsSource = new[]
                {
                    new { Label = "This invoice", Value = row.InvoiceNo ?? "—" },
                    new { Label = "Outstanding", Value = Money.Short(row.Outstanding) },
                    new { Label = "Due", Value = row.DueDate.ToString("dd MMM yyyy") },
                    new { Label = "Days overdue", Value = row.IsOverdue ? row.DaysOverdue.ToString("N0") : "not yet due" },
                    new { Label = "Age", Value = row.AgeBucket },
                }
                .Concat(mine.GroupBy(r => r.AgeBucket).OrderBy(g => Age(g.Key))
                    .Select(g => new
                    {
                        Label = $"{g.Key} days",
                        Value = $"{Money.Short(g.Sum(r => r.Outstanding))}  ({g.Count()})",
                    }))
                .ToList();
        }

        ReceivablesSplit_SizeChanged(ReceivablesSplit, null!);
    }

    private const double ReceivablesDrawerWidth = 320;

    private void CloseReceivablesDetail_Click(object sender, RoutedEventArgs e) =>
        ReceivablesGrid.UnselectAll();

    private void ReceivablesSplit_SizeChanged(object sender, SizeChangedEventArgs? e)
    {
        if (ReceivablesDetailCard is null) return;

        double width = e?.NewSize.Width ?? ReceivablesSplit.ActualWidth;
        if (width <= 0) return;

        bool open = ReceivablesDetailCard.Visibility == Visibility.Visible;
        bool room = width - ReceivablesDrawerWidth - 16 >= 420;
        bool showing = open && room;

        ReceivablesDetailCol.Width = new GridLength(showing ? ReceivablesDrawerWidth : 0);
        ReceivablesDetailGap.Width = new GridLength(showing ? 16 : 0);
        ReceivablesDetailCard.Visibility = showing ? Visibility.Visible : Visibility.Collapsed;

        // Same DataGrid quirk as the Invoices list: a star column keeps the width it computed
        // before the grid narrowed, so the drawer would push the right-hand columns off the edge.
        // Auto forces it to re-derive; star then re-shares what is left.
        ResharStar(RecColBuyer);
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

    /// The grade filter's labels, and the code each one stands for. The combo shows the label; the
    /// filter still compares codes, which is what v_stock_position carries.
    private Dictionary<string, string> _stockGradeCodes = [];

    /// The grade's name — "No. 1 Clean". Falls back to the code when the catalogue has no display
    /// name for it, so a grade that exists only in the stock data is still listed rather than
    /// silently dropped.
    private static string GradeLabel(string code, Grade? grade) =>
        string.IsNullOrWhiteSpace(grade?.DisplayName) ? code : grade!.DisplayName!;
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

        StockChip.Text = $"{rows.Count:N0} bucket{(rows.Count == 1 ? "" : "s")}";

        // Both lists offer only what actually came back.
        //
        // Labelled "No. 1 Clean (NO 1)", not "NO 1". Every other grade picker in the app shows the
        // display name, so a filter that showed only the code read as a different vocabulary — and
        // the code still has to be visible here, because it is what the table's GRADE column shows.
        // The label is what the combo displays; _stockGradeCodes maps it back for the filter, so
        // the comparison is still against the code and nothing about the filtering changed.
        object? keepGrade = StockGradeFilter.SelectedItem;
        _stockGradeCodes = rows.Select(r => r.GradeCode).Distinct()
            .Select(code => (Code: code, Grade: Catalogue.Grades.FirstOrDefault(g => g.Code == code)))
            .OrderBy(x => x.Grade?.SortOrder ?? int.MaxValue).ThenBy(x => x.Code, StringComparer.Ordinal)
            .ToDictionary(x => GradeLabel(x.Code, x.Grade), x => x.Code);

        StockGradeFilter.ItemsSource = new[] { "All grades" }.Concat(_stockGradeCodes.Keys).ToList();
        StockGradeFilter.SelectedItem =
            keepGrade is string kg && StockGradeFilter.Items.Contains(kg) ? kg : "All grades";

        object? keepSize = StockSizeFilter.SelectedItem;
        StockSizeFilter.ItemsSource = new[] { "All sizes" }
            .Concat(rows.Select(r => r.SizeCode).Distinct().OrderBy(z => z, StringComparer.Ordinal)).ToList();
        StockSizeFilter.SelectedItem =
            keepSize is string kz && StockSizeFilter.Items.Contains(kz) ? kz : "All sizes";

        ApplyStockFilter();

        // Blank rather than a stale total when nothing comes back — a leftover figure over an empty
        // grid reads as data that failed to draw.
        StockSummary.Text = rows.Count == 0
            ? ""
            : $"{rows.Sum(r => r.BalanceCt):N4} ct   ·   value {Money.Short(rows.Sum(r => r.StockValue))}";
    }

    private void HideEmpty_Changed(object sender, RoutedEventArgs e) => ApplyStockFilter();

    private void StockFilter_Changed(object sender, RoutedEventArgs e) => ApplyStockFilter();

    private void ClearStockSearch_Click(object sender, RoutedEventArgs e)
    {
        StockSearch.Clear();
        StockSearch.Focus();
    }

    private void ClearStockFilters_Click(object sender, RoutedEventArgs e)
    {
        if (StockGradeFilter.Items.Count > 0) StockGradeFilter.SelectedIndex = 0;
        if (StockSizeFilter.Items.Count > 0) StockSizeFilter.SelectedIndex = 0;
        StockSearch.Clear();
        ApplyStockFilter();
    }

    private void ApplyStockFilter()
    {
        // IsChecked="True" in the markup raises Checked while InitializeComponent is still parsing,
        // and the checkbox sits above the grid in the tree — so this runs once with StockGrid still
        // null. Unhandled, that took the whole window down before it ever appeared. The filter
        // boxes are parsed after the checkbox too, so they are guarded with it.
        if (StockGrid is null || StockGradeFilter is null || StockSizeFilter is null) return;

        // Ticked means ticked: a bucket holding nothing is hidden, whatever its history. The rule
        // used to keep zero-balance buckets that had a ledger — NO 1 BB × -2 sits at zero because
        // its only invoice was cancelled — but a row badged "Empty" showing under a ticked "Hide
        // empty buckets" reads as a broken filter. Untick to get those buckets back.
        IEnumerable<VStockPosition> rows = HideEmptyBuckets.IsChecked == true
            ? _stock.Where(r => r.BalanceCt != 0)
            : _stock;

        string gradeLabel = StockGradeFilter.SelectedIndex <= 0 ? "" : StockGradeFilter.SelectedItem as string ?? "";
        string grade = gradeLabel.Length == 0 ? ""
                     : _stockGradeCodes.TryGetValue(gradeLabel, out var code) ? code : gradeLabel;
        string size = StockSizeFilter.SelectedIndex <= 0 ? "" : StockSizeFilter.SelectedItem as string ?? "";
        string term = StockSearch?.Text.Trim() ?? "";

        var show = rows
            .Where(r => grade.Length == 0 || r.GradeCode == grade)
            .Where(r => size.Length == 0 || r.SizeCode == size)
            .Where(r => term.Length == 0
                        || r.GradeCode.Contains(term, StringComparison.OrdinalIgnoreCase)
                        || (r.GradeName ?? "").Contains(term, StringComparison.OrdinalIgnoreCase)
                        || r.SizeCode.Contains(term, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (StockSearchClear is not null)
            StockSearchClear.Visibility = string.IsNullOrEmpty(StockSearch?.Text)
                ? Visibility.Collapsed : Visibility.Visible;

        StockGrid.ItemsSource = show;

        // The tiles report what is on screen, so they never describe a set the list is not showing.
        StockKpiCarats.Text = show.Sum(r => r.BalanceCt).ToString("N4");
        StockKpiCaratsNote.Text = $"across {show.Count:N0} bucket{(show.Count == 1 ? "" : "s")}";
        StockKpiValue.Text = Money.Short(show.Sum(r => r.StockValue));
        StockKpiValueNote.Text = "At average cost";

        int held = show.Count(r => r.BalanceCt > 0);
        StockKpiActive.Text = held.ToString("N0");
        StockKpiActiveNote.Text = "Holding a balance";

        int negative = show.Count(r => r.BalanceCt < 0);
        StockKpiNegative.Text = negative.ToString("N0");
        StockKpiNegativeNote.Text = negative == 0
            ? "Nothing to reconcile"
            : "Stock left that never arrived";

        StockCount.Text = show.Count == 0
            ? "Nothing matches these filters"
            : $"{show.Count:N0} bucket{(show.Count == 1 ? "" : "s")} shown of {_stock.Count:N0}";

        // Which empty this is decides what to do about it, so the hint says which one it is.
        bool filtered = grade.Length != 0 || size.Length != 0 || term.Length != 0;
        const string nothingLoaded = "No stock positions.\nPress Refresh, or record an intake first.";
        StockHint.Text =
            _stock.Count == 0 ? nothingLoaded
            : filtered ? "No bucket matches these filters.\nPress Clear to see them all."
            : HideEmptyBuckets.IsChecked == true
                ? $"No bucket is holding a balance.\nAll {_stock.Count:N0} are empty — untick Hide empty buckets to list them."
                : nothingLoaded;

        if (_stock.Count != 0 && show.Count != _stock.Count)
            Say($"Showing {show.Count} of {_stock.Count} buckets", ok: true);
    }

    /// Opens the drawer on the selected bucket. Nothing is fetched here — the movements still load
    /// only when Show movements is pressed.
    private void StockRow_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (MovementSubtitle is null) return;

        var row = StockGrid.SelectedItem as VStockPosition;

        // The ledger belongs to the row it was loaded for. Left in place across a selection change
        // it would sit under another bucket's heading, reading as that bucket's history.
        MovementList.ItemsSource = null;
        MovementHint.Text = "Press Show movements\nto load this bucket's ledger.";

        StockMoveCard.DataContext = row;
        MovementSubtitle.Text = row is not null
            ? $"{row.GradeCode} × {row.SizeCode}"
              + (row.AgeDays is { } age ? $" · oldest intake {age:N0} days" : "")
            : "Select a bucket in the list";

        StockMoveCard.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        StockSplit_SizeChanged(StockSplit, null!);
    }

    /// Trims a grid to a whole number of rows. A DataGrid fills whatever height it is given, so the
    /// last row is sliced wherever the card happens to end — and the slice reads as a second rule
    /// under the table, with a strip of nothing between the two.
    private void SnapGridHeight(object sender, SizeChangedEventArgs e)
    {
        var grid = (DataGrid)sender;
        if (grid.RowHeight <= 0 || e.NewSize.Height <= 0) return;

        double chrome = grid.ColumnHeaderHeight + grid.BorderThickness.Top + grid.BorderThickness.Bottom;
        // Measure against the height the grid was offered, not the height it was trimmed to, or
        // each pass would shave another row off the one before.
        double offered = ((FrameworkElement)grid.Parent).ActualHeight;
        int rows = (int)Math.Floor((offered - chrome) / grid.RowHeight);
        if (rows < 1) { grid.MaxHeight = double.PositiveInfinity; return; }

        double wanted = chrome + rows * grid.RowHeight;
        if (Math.Abs(grid.MaxHeight - wanted) > 0.5) grid.MaxHeight = wanted;
    }

    private const double StockDrawerWidth = 340;

    private void CloseStockDetail_Click(object sender, RoutedEventArgs e) => StockGrid.UnselectAll();

    /// The drawer keeps its width; the list gives way, down to the MinWidth on its column. Same
    /// shape as InvoiceSplit_SizeChanged.
    private void StockSplit_SizeChanged(object sender, SizeChangedEventArgs? e)
    {
        if (StockMoveCard is null) return;

        bool open = StockGrid?.SelectedItem is VStockPosition;
        double width = e?.NewSize.Width ?? StockSplit.ActualWidth;
        if (width <= 0) return;

        // Below this the list would be narrower than its own columns, so the drawer stands down.
        // 640 is what the list's own columns need — 575px of them plus the card's padding and a
        // scrollbar. Below it the star VALUE column starts taking width off the Auto ones and the
        // cells mangle ("In stoc", "60,000.0"), which is the failure the Auto columns ended. The
        // constraint lives here rather than as a MinWidth on the column, because a MinWidth is a
        // floor on the grid: it would report itself wide enough and justify the drawer it cannot fit.
        bool room = width - StockDrawerWidth - 16 >= 640;
        bool showing = open && room;

        StockDetailCol.Width = new GridLength(showing ? StockDrawerWidth : 0);
        StockDetailGap.Width = new GridLength(showing ? 16 : 0);
        StockMoveCard.Visibility = showing ? Visibility.Visible : Visibility.Collapsed;

        // Same DataGrid quirk as the Invoices list: a star column keeps the width it computed
        // before the grid narrowed, so opening the drawer would push VALUE off the right edge.
        // Auto forces it to re-derive; star then re-shares what is left.
        ResharStar(StockColValue);
    }

    private async void Movements_Click(object sender, RoutedEventArgs e)
    {
        if (StockGrid.SelectedItem is not VStockPosition row) { Say("Select a grade × size row"); return; }

        List<VStockMovement>? rows;
        using (Busy(ShowMovements, "Loading…", ShowMovements, StockRefresh))
            rows = await Read(() => Repo.MovementsAsync(row.GradeCode, row.SizeCode));

        MovementList.ItemsSource = rows;
        if (rows is null) return;                       // Read already reported the failure

        // A bucket that has never been traded loads successfully and returns nothing. Leaving the
        // "press Show movements" prompt up made that look like the button had failed.
        if (rows.Count == 0)
            MovementHint.Text = $"No movements for {row.GradeCode} × {row.SizeCode}.\n" +
                                "Nothing has been taken in, sold or adjusted here.";

        // No Say here. The drawer is open on the bucket, headed with its grade and size, and either
        // lists the entries or says there are none — repeating that in the status bar said the same
        // sentence twice on one screen.
    }

    private async void Invariants_Click(object sender, RoutedEventArgs e)
    {
        List<VReconciliation>? rows;
        using (Busy(RunInvariants, "Checking…", RunInvariants, StockRefresh))
            rows = await Read(Repo.ReconciliationAsync);

        if (rows is null) return;

        var broken = rows.Where(r => !r.Reconciles).ToList();

        // AppDialog, not MessageBox: the same shell every other dialog in the app uses, with the
        // findings in a scrolling list instead of sixty lines crammed into a system alert that
        // cannot be scrolled or read. Same query, same pass rule, same outcome wording.
        decimal shortfall = broken.Sum(r => Math.Abs(r.DiffCt));

        AppDialog.Info(this, "Ledger integrity",
            broken.Count == 0
                ? "Everything reconciles"
                : $"{broken.Count:N0} bucket{(broken.Count == 1 ? "" : "s")} do not reconcile",
            broken.Count == 0
                ? "Stock moved out matches stock invoiced, on every grade × size."
                : "These buckets have carats on invoices that were never moved out of stock, or the other way round.",
            new (string, string)[]
            {
                ("Buckets checked", $"{rows.Count:N0}"),
                ("Reconciled", $"{rows.Count - broken.Count:N0}"),
                ("Off", $"{broken.Count:N0}"),
                ("Total difference", $"{shortfall:N4} ct"),
            },
            broken.Count == 0 ? null : "Where they differ",
            broken.Count == 0 ? null
                : broken.OrderByDescending(r => Math.Abs(r.DiffCt)).Select(r =>
                    $"{r.GradeCode} × {r.SizeCode} — moved {r.MovedOutCt:N4} ct, "
                    + $"invoiced {r.SoldOnInvoicesCt:N4} ct, off by {r.DiffCt:N4} ct"),
            broken.Count == 0 ? null : "Nothing has been changed. This is a read-only check.");

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
    private void LedgerGrade_Changed(object sender, SelectionChangedEventArgs e) => FillSizes(LedgerGrade, LedgerSize);

    // What this session has posted. Counts, not totals: this page writes to the ledger, it does not
    // report on it, and a figure that looked like a stock total would be read as one.
    private int _opIntakes, _opConversions, _opRejections, _opAdjustments;

    private void CountOp(TextBlock tile, ref int tally)
    {
        tally++;
        tile.Text = tally.ToString("N0");
    }

    /// The ledger panel sits beside the forms when there is room for both, and drops underneath
    /// when there is not. Same shape as DashSplit_SizeChanged on the Dashboard.
    private void IntakeSplit_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IntakeLedgerCard is null) return;

        // 380 for the ledger, 16 for the gap, and the forms need about 700 before their fields
        // start wrapping to a third row.
        bool narrow = e.NewSize.Width < 1096;

        System.Windows.Controls.Grid.SetColumn(IntakeLedgerCard, narrow ? 0 : 2);
        System.Windows.Controls.Grid.SetRow(IntakeLedgerCard, narrow ? 2 : 0);
        System.Windows.Controls.Grid.SetColumnSpan(IntakeLedgerCard, narrow ? 3 : 1);

        IntakeGapCol.Width = new GridLength(narrow ? 0 : 16);
        IntakeLedgerCol.Width = new GridLength(narrow ? 0 : 380);
        IntakeStackGap.Height = new GridLength(narrow ? 14 : 0);
        IntakeStackRow.Height = narrow ? new GridLength(240) : new GridLength(0);
    }

    /// The same per-bucket read the Stock page uses. There is no all-movements query and this page
    /// is not the place to add one, so the ledger is scoped to the bucket you name.
    private async void LedgerLoad_Click(object sender, RoutedEventArgs e)
    {
        var (grade, size) = Pick(LedgerGrade, LedgerSize);
        if (grade is null || size is null) { Say("Pick a grade and size"); return; }

        List<VStockMovement>? rows;
        using (Busy(LedgerLoad, "Loading…"))
            rows = await Read(() => Repo.MovementsAsync(grade.Code, size.Code));

        LedgerList.ItemsSource = rows;
        if (rows is null) return;                       // Read already reported the failure

        LedgerSubtitle.Text = rows.Count == 0
            ? $"{grade.Code} × {size.Code}"
            : $"{grade.Code} × {size.Code} · {rows.Count} movement{(rows.Count == 1 ? "" : "s")}";

        // A bucket that has never been traded loads successfully and returns nothing. One message
        // for both states makes a finished load look like a button that did nothing.
        if (rows.Count == 0)
            LedgerHint.Text = $"No movements for {grade.Code} × {size.Code}."
                            + "\nNothing has been taken in, sold or adjusted here.";
    }

    /// Reloads the ledger after an operation that wrote to the bucket it is showing, so the panel
    /// never sits there contradicting what was just posted.
    private void RefreshLedgerIfShowing(Grade grade, SizeBucket size)
    {
        if (LedgerList.ItemsSource is null) return;
        var (shown, shownSize) = Pick(LedgerGrade, LedgerSize);
        if (shown?.Code == grade.Code && shownSize?.Code == size.Code)
            LedgerLoad_Click(LedgerLoad, new RoutedEventArgs());
    }

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
        CountOp(OpKpiIntake, ref _opIntakes);
        RefreshLedgerIfShowing(grade, size);
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

        // A conversion moves carats from one bucket to another. Both sides the same is not a
        // conversion — it books a movement out and a movement in for the same bucket, leaving the
        // balance where it started and two entries in the ledger explaining nothing.
        if (fromGrade.GradeId == toGrade.GradeId && fromSize.SizeId == toSize.SizeId)
        { Say("From and To are the same bucket — a conversion has to move carats somewhere else"); return; }

        if (!decimal.TryParse(ConvWeight.Text, out decimal weight) || weight <= 0) { Say("Weight must be positive"); return; }
        if (!TypedPrice(ConvPrice, out decimal? price)) { Say("Price/ct must be a number, or left blank"); return; }

        if (Bounds.TooLarge(weight, "Weight") is { } tooHeavy) { Say(tooHeavy); ConvWeight.Focus(); return; }
        if (price is { } p && Bounds.TooLarge(p, "Price per carat") is { } tooDear) { Say(tooDear); ConvPrice.Focus(); return; }
        if (!ConfirmLarge(weight, price ?? 0)) return;

        string? failure = await Repo.ConvertAsync(fromGrade.GradeId, fromSize.SizeId,
                                                  toGrade.GradeId, toSize.SizeId, weight, price);
        if (failure is null)
        {
            CountOp(OpKpiConvert, ref _opConversions);
            RefreshLedgerIfShowing(fromGrade, fromSize);
            RefreshLedgerIfShowing(toGrade, toSize);
        }
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
        CountOp(OpKpiReject, ref _opRejections);
        RefreshLedgerIfShowing(grade, size);
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
        if (failure is null)
        {
            CountOp(OpKpiAdjust, ref _opAdjustments);
            RefreshLedgerIfShowing(grade, size);
        }
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

        // The header chip counts what the page is actually managing, the same way every other
        // page's does — not a stale "Not loaded yet" once the load has finished.
        MasterChip.Text = $"{_grades.Count:N0} grade{(_grades.Count == 1 ? "" : "s")} · "
                        + $"{buyers.Count:N0} buyer{(buyers.Count == 1 ? "" : "s")} · "
                        + $"{brokers.Count:N0} broker{(brokers.Count == 1 ? "" : "s")}";

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

    /// <summary>
    /// Escape clears the search box it is pressed in. Seven of the eight boxes advertised
    /// "Clear search (Esc)" on their clear button and only Master Data actually did it — the
    /// shortcut was written per page rather than for the control.
    /// </summary>
    private void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || sender is not TextBox box || box.Text.Length == 0) return;

        // Only when there is a search to clear; otherwise Escape belongs to whatever else is
        // listening, including a dialog's own cancel.
        box.Clear();
        e.Handled = true;
    }

    /// <summary>
    /// Ctrl+F, Esc and Ctrl+N, active only while Master Data is the visible tab. Sales entry
    /// already owns Ctrl+N for a new invoice, so this must never reach it.
    /// </summary>
    private void MasterData_Keys(object sender, KeyEventArgs e)
    {
        if (MasterSplit is null || !MasterSplit.IsVisible) return;

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        if (ctrl && e.Key == Key.F)
        {
            GradeSearch.Focus();
            GradeSearch.SelectAll();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.N)
        {
            // Whichever party list is showing is the one you meant to add to.
            bool brokers = PartyTabs?.SelectedIndex == 1;
            if (brokers) AddBroker_Click(this, new RoutedEventArgs());
            else AddBuyer_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && GradeSearch.Text.Length > 0)
        {
            // Only when there is a search to clear; otherwise Escape belongs to whatever else
            // is listening, including a dialog's own cancel.
            GradeSearch.Clear();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Below this width the two cards cannot both hold their content, and the alternative is a
    /// horizontal scrollbar across the whole page. The sidebar drops underneath instead.
    /// </summary>
    private void MasterSplit_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (SidebarCard is null) return;

        bool narrow = e.NewSize.Width < 980;

        // Fully qualified: this window has a DataGrid field literally named Grid, which shadows
        // the type and makes the attached-property calls fail to resolve.
        System.Windows.Controls.Grid.SetColumn(SidebarCard, narrow ? 0 : 2);
        System.Windows.Controls.Grid.SetRow(SidebarCard, narrow ? 2 : 0);
        System.Windows.Controls.Grid.SetColumnSpan(SidebarCard, narrow ? 3 : 1);
        System.Windows.Controls.Grid.SetColumnSpan(GradesCard, narrow ? 3 : 1);

        GapCol.Width = new GridLength(narrow ? 0 : 18);
        SideCol.Width = narrow ? new GridLength(0) : new GridLength(32, GridUnitType.Star);
        StackGap.Height = new GridLength(narrow ? 18 : 0);
        StackRow.Height = new GridLength(narrow ? 320 : 0);
        SidebarCard.Height = narrow ? 320 : double.NaN;
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

        // Clearing every alias is the one destructive edit on this page: those spellings are what
        // let a workbook import resolve, and losing them fails the next import silently.
        if (tidy.Length == 0 && (grade.Aliases ?? "").Trim().Length > 0)
        {
            bool go = AppDialog.Confirm(this, "Remove all aliases",
                $"Remove every alias from {grade.Code}?", null,
                [("Grade", grade.Code), ("Aliases to remove", grade.Aliases!.Trim())],
                "Imports resolve workbook spellings through these aliases. Without them, rows using "
                + "those spellings will be skipped on the next import.",
                null, null, "Remove them", "Keep them");

            if (!go) { box.Text = grade.Aliases; await LoadMasterAsync(); return; }
        }

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
    /// <summary>
    /// What the data actually covers, captured from the unfiltered read that already happens here.
    /// An empty screen can then say WHY it is empty instead of only that it is. No extra query.
    /// </summary>
    private DateOnly? _dataFrom, _dataTo;
    private int _postedTotal;

    /// <summary>
    /// What each invoice contributes once the grade filter is applied — its own totals when no
    /// grade is chosen, and only that grade's lines when one is. Empty means "no grade filter".
    ///
    /// Every figure on this page reads through <see cref="Sale"/> rather than off VInvoice, so the
    /// tiles, the trend, the breakdowns and the table cannot disagree about what a filtered sale
    /// is worth.
    /// </summary>
    private Dictionary<long, (decimal Amount, decimal Carats, decimal Broker)> _gradeShare = [];

    /// One invoice's contribution under the current filters.
    private (decimal Amount, decimal Carats, decimal Broker) Sale(VInvoice i) =>
        _gradeShare.Count == 0
            ? (i.AmountTotal, i.CaratsSold, i.BrokerPayable)
            : _gradeShare.TryGetValue(i.InvoiceId, out var share) ? share : (0m, 0m, 0m);

    private async Task<List<VInvoice>> FilteredInvoicesAsync()
    {
        var (from, to) = Period();
        var all = await Read(Repo.InvoicesAsync) ?? [];

        var posted = all.Where(i => i.Status == InvoiceStatus.POSTED).ToList();
        _postedTotal = posted.Count;
        _dataFrom = posted.Count == 0 ? null : posted.Min(i => i.InvoiceDate);
        _dataTo = posted.Count == 0 ? null : posted.Max(i => i.InvoiceDate);

        var rows = all.Where(i => i.Status == InvoiceStatus.POSTED
                               && i.InvoiceDate >= from && i.InvoiceDate <= to);
        if (FilterBuyer.SelectedItem is Buyer buyer) rows = rows.Where(i => i.BuyerId == buyer.BuyerId);
        var list = rows.ToList();

        // Grade lives on the line, not the invoice, so a grade filter needs the lines. Read only
        // when a grade is actually chosen — this is the one query on the page that is not needed
        // for the default view.
        _gradeShare = [];
        if (FilterGrade.SelectedItem is Grade grade)
        {
            var lines = await Read(() => Repo.SalesLinesAsync(from, to)) ?? [];
            _gradeShare = lines
                .Where(l => l.GradeCode == grade.Code)
                .GroupBy(l => l.InvoiceId)
                .ToDictionary(g => g.Key, g => (
                    Amount: g.Sum(l => l.Amount),
                    Carats: g.Sum(l => l.SelectionCt),
                    // Broker is a percentage of the pre-broker amount on the line, the same way
                    // v_invoice derives the invoice's own payable.
                    Broker: g.Sum(l => l.AmountPreBroker * l.BrokerPct / 100m)));

            // An invoice with no line of this grade contributes nothing, so it leaves the page
            // rather than sitting in the table at zero.
            list = list.Where(i => _gradeShare.ContainsKey(i.InvoiceId)).ToList();
        }

        return list;
    }

    /// Set while a handler is changing the other control, so the two do not answer each other.
    private bool _syncingRange;

    private void Range_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (FromDate is null || _syncingRange) return;    // fires once during XAML load

        // A named range computes its own dates, so leaving the last custom pair sitting in the
        // boxes would show two dates that no longer describe what is on screen.
        if ((RangePicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() != "CUSTOM")
        {
            _syncingRange = true;
            FromDate.SelectedDate = ToDate.SelectedDate = null;
            FromDate.DisplayDateEnd = ToDate.DisplayDateStart = null;
            _syncingRange = false;
        }
    }

    /// <summary>
    /// The date boxes used to be disabled unless the range was already "Custom…", which reads as
    /// two broken fields — there is nothing on them to say the drop-down is the way in. Picking a
    /// date now selects Custom itself. Period() is unchanged: it still reads these two boxes only
    /// when the range says CUSTOM.
    /// </summary>
    private void CustomDate_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (RangePicker is null || _syncingRange) return;

        SyncDateBounds();

        if (FromDate.SelectedDate is null && ToDate.SelectedDate is null) return;
        if ((RangePicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "CUSTOM") return;

        _syncingRange = true;
        RangePicker.SelectedItem = RangePicker.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => i.Tag?.ToString() == "CUSTOM");
        _syncingRange = false;
    }

    /// <summary>
    /// Keeps the two calendars describing a range that can exist. A "To" earlier than "From"
    /// matches nothing at all, and an empty dashboard reads as missing data rather than as an
    /// impossible filter — so the days are greyed out instead of being offered and then failing.
    /// Also opens the second calendar near the first: left to itself it opens on today, which is
    /// how the two ended up months apart.
    /// </summary>
    private void SyncDateBounds()
    {
        if (FromDate is null || ToDate is null) return;

        ToDate.DisplayDateStart = FromDate.SelectedDate;
        FromDate.DisplayDateEnd = ToDate.SelectedDate;

        if (FromDate.SelectedDate is { } from)
        {
            if (ToDate.SelectedDate is null) ToDate.DisplayDate = from;

            // Repair a range already inverted before these bounds existed.
            if (ToDate.SelectedDate is { } to && to < from)
            {
                _syncingRange = true;
                ToDate.SelectedDate = from;
                _syncingRange = false;
                Say("\"To\" was before \"From\" — the end date has been moved to match the start");
            }
        }
    }

    /// <summary>
    /// A wheel over an inner list should keep scrolling the page once that list has nowhere left
    /// to go. WPF stops at the innermost scroller instead of bubbling, so hovering the bar list or
    /// the invoice table left the page apparently stuck.
    /// </summary>
    private void InnerWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || DashScroll is null) return;

        var inner = sender as ScrollViewer ?? Descendant<ScrollViewer>((DependencyObject)sender);
        if (inner is not null)
        {
            bool canGoUp = e.Delta > 0 && inner.VerticalOffset > 0.5;
            bool canGoDown = e.Delta < 0 && inner.VerticalOffset < inner.ScrollableHeight - 0.5;
            if (canGoUp || canGoDown) return;             // the inner list still has room
        }

        e.Handled = true;
        DashScroll.ScrollToVerticalOffset(DashScroll.VerticalOffset - e.Delta);
    }

    private static T? Descendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            if (Descendant<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    private void ClearDrillSearch_Click(object sender, RoutedEventArgs e)
    {
        DrillSearch.Clear();
        DrillSearch.Focus();
    }

    private List<VInvoice> _drill = [];

    /// <summary>
    /// Plots invoice totals over time from the rows already fetched for the drill-down. No query
    /// and no server-side aggregation: the same invoices that fill the table below are grouped
    /// here, so the two can never disagree.
    ///
    /// Points are computed against the canvas's real size rather than in a fixed 100x40 space that
    /// a Viewbox then stretches. A Viewbox scales the stroke with the geometry, and the two axes
    /// here scale by very different factors — roughly 11x across against 2.6x down — so a 2px line
    /// came out as a wedge that changed thickness with its own slope. Measuring is safe because
    /// the canvas raises SizeChanged, which redraws.
    /// </summary>
    private void DrawTrend(List<VInvoice> invoices)
    {
        _trend = invoices;
        RenderTrend();
    }

    /// The invoices behind the trend, kept so a resize can redraw without refetching.
    private List<VInvoice> _trend = [];

    private void TrendCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RenderTrend();

    /// Where each point landed, so the cursor can be matched to one without re-deriving the maths.
    private readonly List<(double X, double Y, string When, decimal Value, decimal Share)> _trendPoints = [];
    private double _plotTop, _plotBottom;

    /// <summary>
    /// Picks the point nearest the cursor horizontally and shows its figures. Nearest-by-x rather
    /// than hit-testing a marker: a 9px target is a game of skill, and every x inside the plot has
    /// exactly one bucket it belongs to.
    /// </summary>
    private void TrendCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_trendPoints.Count == 0 || TrendTip is null) { HideTrendTip(); return; }

        var at = e.GetPosition(TrendCanvas);
        int nearest = 0;
        for (int i = 1; i < _trendPoints.Count; i++)
            if (Math.Abs(_trendPoints[i].X - at.X) < Math.Abs(_trendPoints[nearest].X - at.X))
                nearest = i;

        var p = _trendPoints[nearest];

        TrendHighlight.Visibility = Visibility.Visible;
        Canvas.SetLeft(TrendHighlight, p.X - 6.5);
        Canvas.SetTop(TrendHighlight, p.Y - 6.5);

        TrendRule.Visibility = Visibility.Visible;
        TrendRule.X1 = TrendRule.X2 = p.X;
        TrendRule.Y1 = _plotTop;
        TrendRule.Y2 = _plotBottom;

        TipWhen.Text = p.When;
        TipValue.Text = Money.Short(p.Value);
        TipShare.Text = $"{p.Share:N1}% of the period";

        // Measured before placing, so the card is clamped by its real width rather than a guess,
        // and flips to the left of the point when it would otherwise leave the plot.
        TrendTip.Visibility = Visibility.Visible;
        TrendTip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double tw = TrendTip.DesiredSize.Width, th = TrendTip.DesiredSize.Height;

        double left = p.X + 14;
        if (left + tw > TrendCanvas.ActualWidth) left = p.X - 14 - tw;
        Canvas.SetLeft(TrendTip, Math.Max(0, left));
        Canvas.SetTop(TrendTip, Math.Clamp(p.Y - th - 12, 0, Math.Max(0, TrendCanvas.ActualHeight - th)));
    }

    private void TrendCanvas_MouseLeave(object sender, MouseEventArgs e) => HideTrendTip();

    private void HideTrendTip()
    {
        if (TrendTip is not null) TrendTip.Visibility = Visibility.Collapsed;
        if (TrendHighlight is not null) TrendHighlight.Visibility = Visibility.Collapsed;
        if (TrendRule is not null) TrendRule.Visibility = Visibility.Collapsed;
    }

    private void RenderTrend()
    {
        if (TrendPath is null || TrendCanvas is null) return;

        double w = TrendCanvas.ActualWidth, h = TrendCanvas.ActualHeight;
        if (w <= 1 || h <= 1) return;          // not laid out yet; SizeChanged will call back

        TrendPath.Points.Clear();
        TrendFill.Points.Clear();

        // Sale(i), not AmountTotal: with a grade filter set, an invoice's contribution is that
        // grade's lines only, and the trend has to plot the same money the tiles add up.
        var dated = _trend.Where(i => Sale(i).Amount > 0).ToList();
        if (dated.Count < 2)
        {
            // One point is not a trend, and an empty chart with a stray dot reads as broken. Say
            // which of the two it is: "no invoices" in front of a single invoice sends people
            // looking for missing data that is not missing.
            TrendEmpty.Text = dated.Count == 1
                ? "Only one invoice in this selection — a trend needs at least two points."
                : EmptyReason();
            TrendEmpty.Visibility = Visibility.Visible;
            TrendCaption.Text = "";
            TrendPeak.Text = "";
            _trendPoints.Clear();
            HideTrendTip();
            return;
        }
        TrendEmpty.Visibility = Visibility.Collapsed;

        // Day buckets over a short range, months over a long one — 700 daily points across the
        // canvas is a solid block, not a line.
        var span = dated.Max(i => i.InvoiceDate).DayNumber - dated.Min(i => i.InvoiceDate).DayNumber;
        bool byMonth = span > 92;

        var buckets = dated
            .GroupBy(i => byMonth
                ? new DateOnly(i.InvoiceDate.Year, i.InvoiceDate.Month, 1)
                : i.InvoiceDate)
            .Select(g => (Key: g.Key, Total: g.Sum(x => Sale(x).Amount)))
            .OrderBy(p => p.Key)
            .ToList();

        decimal peak = buckets.Max(p => p.Total);
        decimal total = buckets.Sum(p => p.Total);
        if (peak <= 0) { TrendEmpty.Visibility = Visibility.Visible; return; }

        // Room at the left for the value labels and at the foot for the dates, so the line is
        // never drawn under its own axis.
        const double padTop = 10, padBottom = 20, padLeft = 62, padRight = 8;
        double plot = h - padTop - padBottom;
        double plotWidth = Math.Max(1, w - padLeft - padRight);

        // Everything drawn last time goes, or the canvas accumulates a chart per refresh — but only
        // what this method drew. The named children come from XAML (the two polylines, the guide
        // line, the highlight and the tip card); removing those left the hover furniture set to
        // Visible while no longer being in the tree, so the code looked right and nothing appeared.
        for (int i = TrendCanvas.Children.Count - 1; i >= 0; i--)
            if (TrendCanvas.Children[i] is FrameworkElement drawn && drawn.Name.Length == 0)
                TrendCanvas.Children.RemoveAt(i);

        double X(int i) => padLeft + (buckets.Count == 1 ? plotWidth / 2 : i * plotWidth / (buckets.Count - 1));
        double Y(decimal v) => padTop + plot - (double)(v / peak) * plot;

        // ── Gridlines and value labels · four steps of the real peak, not a rounded invention ──
        for (int step = 0; step <= 4; step++)
        {
            decimal value = peak * step / 4;
            double y = Y(value);

            TrendCanvas.Children.Add(Line(padLeft, y, w - padRight, y,
                (Brush)FindResource("BorderBrush"), step == 0 ? 1 : 0.6));

            var label = new TextBlock
            {
                Text = Short(value),
                FontSize = 10,
                Foreground = (Brush)FindResource("TextMutedBrush"),
                Width = padLeft - 8,
                TextAlignment = TextAlignment.Right,
            };
            Canvas.SetLeft(label, 0);
            Canvas.SetTop(label, y - 7);
            TrendCanvas.Children.Add(label);
        }

        // ── The line itself ──
        for (int i = 0; i < buckets.Count; i++)
        {
            var p = new Point(X(i), Y(buckets[i].Total));
            TrendPath.Points.Add(p);
            TrendFill.Points.Add(p);
        }
        TrendFill.Points.Add(new Point(X(buckets.Count - 1), padTop + plot));
        TrendFill.Points.Add(new Point(X(0), padTop + plot));

        // ── A marker per point, each carrying its own figures ──
        string pointUnit = byMonth ? "MMM yyyy" : "dd MMM yyyy";
        _trendPoints.Clear();
        for (int i = 0; i < buckets.Count; i++)
        {
            var dot = new Ellipse
            {
                Width = 9, Height = 9,
                Fill = (Brush)FindResource("SurfaceBrush"),
                Stroke = (Brush)FindResource("AccentBrush"),
                StrokeThickness = 2,
                // Real numbers for this point only — the bucket, its total, and its share of the
                // period. Nothing here is a restatement of the axis.
                ToolTip = $"{buckets[i].Key.ToString(pointUnit)}\n{Money.Short(buckets[i].Total)}\n"
                          + $"{(total == 0 ? 0 : buckets[i].Total / total * 100):N1}% of the period",
            };
            Canvas.SetLeft(dot, X(i) - 4.5);
            Canvas.SetTop(dot, Y(buckets[i].Total) - 4.5);
            TrendCanvas.Children.Add(dot);

            _trendPoints.Add((X(i), Y(buckets[i].Total),
                buckets[i].Key.ToString(pointUnit),
                buckets[i].Total,
                total == 0 ? 0 : buckets[i].Total / total * 100));
        }

        _plotTop = padTop;
        _plotBottom = padTop + plot;

        // ── Date labels · first, middle and last. More than three collide below about 700px. ──
        foreach (int i in buckets.Count <= 2 ? [0, buckets.Count - 1] : new[] { 0, buckets.Count / 2, buckets.Count - 1 })
        {
            var label = new TextBlock
            {
                Text = buckets[i].Key.ToString(byMonth ? "MMM yy" : "dd MMM"),
                FontSize = 10,
                Foreground = (Brush)FindResource("TextMutedBrush"),
            };
            label.Measure(new Size(200, 20));
            double x = Math.Clamp(X(i) - label.DesiredSize.Width / 2,
                                  padLeft, w - padRight - label.DesiredSize.Width);
            Canvas.SetLeft(label, x);
            Canvas.SetTop(label, padTop + plot + 4);
            TrendCanvas.Children.Add(label);
        }

        TrendCaption.Text = $"{buckets.Count} {(byMonth ? "month" : "day")}s · "
                            + $"{buckets[0].Key:dd MMM yyyy} to {buckets[^1].Key:dd MMM yyyy}";
        TrendPeak.Text = $"peak {Money.Short(peak)}";

        // Real figures, not a restatement of the axis: the highest and lowest buckets by name and
        // amount, plus the total the line adds up to.
        var high = buckets.MaxBy(b => b.Total);
        var low = buckets.MinBy(b => b.Total);
        string unit = byMonth ? "MMM yyyy" : "dd MMM yyyy";
        TrendCanvas.ToolTip =
            $"{buckets.Count} {(byMonth ? "months" : "days")} plotted\n"
            + $"Highest · {high.Key.ToString(unit)} · {Money.Short(high.Total)}\n"
            + $"Lowest · {low.Key.ToString(unit)} · {Money.Short(low.Total)}\n"
            + $"Total · {Money.Short(buckets.Sum(b => b.Total))}";
    }

    /// <summary>
    /// Why the current filters produced nothing. "Nothing in this period" is true but useless: it
    /// does not say whether the range is before the data, after it, or narrowed away by a buyer.
    /// Every branch below is a fact about rows already in memory.
    /// </summary>
    private string EmptyReason()
    {
        var (from, to) = Period();
        string span = $"{from:dd MMM yyyy} to {to:dd MMM yyyy}";

        if (_postedTotal == 0)
            return "No posted invoices exist yet.";

        if (_dataTo is { } last && from > last)
            return $"No sales in {span}. The most recent sale was {last:dd MMM yyyy} — "
                   + "try All time, or move the range back.";

        if (_dataFrom is { } first && to < first)
            return $"No sales in {span}. Sales start on {first:dd MMM yyyy}.";

        if (FilterBuyer.SelectedItem is Buyer buyer)
            return $"No sales for {buyer.Name} in {span}.";

        return $"No sales in {span}.";
    }

    /// <summary>
    /// What is wrong with the filter bar, if anything, said before a load runs rather than after it
    /// returns nothing. Only impossible states are refused — an empty result is a legitimate answer.
    /// </summary>
    private string? FilterProblem()
    {
        bool custom = (RangePicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "CUSTOM";
        if (!custom) return null;

        if (FromDate.SelectedDate is null && ToDate.SelectedDate is null)
            return "Custom range: pick a From and a To date, or choose a named range.";

        if (FromDate.SelectedDate is { } f && ToDate.SelectedDate is { } t && t < f)
            return "Custom range: \"To\" is before \"From\".";

        return null;
    }

    /// Range and buyer applied; search still to come. Held so a keystroke costs no round trip.
    private List<VInvoice> _ranged = [];

    private static Line Line(double x1, double y1, double x2, double y2, Brush brush, double thickness) =>
        new()
        {
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
            Stroke = brush, StrokeThickness = thickness,
            SnapsToDevicePixels = true,
        };

    /// A crore/lakh short form for an axis label. 5,99,71,04,003.82 does not fit beside a chart,
    /// and the exact figure is a hover away on every point.
    private static string Short(decimal v) => v switch
    {
        >= 10_000_000m => $"{v / 10_000_000m:0.##} Cr",
        >= 100_000m => $"{v / 100_000m:0.##} L",
        >= 1_000m => $"{v / 1_000m:0.#} K",
        _ => v.ToString("0.##"),
    };

    private void DrillSearch_Changed(object sender, TextChangedEventArgs e) => ApplyDashboardSearch();

    /// <summary>
    /// Applies the search and re-scopes everything derived from it — the four Sales tiles, the
    /// trend, the breakdown and the table. One filtered set feeds all four, so they cannot
    /// disagree about which invoices they are describing.
    ///
    /// The tiles are summed here rather than read from dashboard_summary. That is not a new rule:
    /// the RPC and this sum were verified equal across four ranges on count, amount, carats and
    /// blended rate, and the breakdowns have always grouped these same server-computed values.
    /// What it buys is a buyer filter that reaches the tiles instead of stopping at the charts.
    /// </summary>
    private async void ApplyDashboardSearch()
    {
        if (DrillGrid is null) return;

        string term = DrillSearch?.Text.Trim() ?? "";
        var shown = term.Length == 0
            ? _ranged
            : _ranged.Where(i =>
                (i.InvoiceNo ?? "").Contains(term, StringComparison.OrdinalIgnoreCase)
                || (i.BuyerName ?? "").Contains(term, StringComparison.OrdinalIgnoreCase)
                || InvoiceStateConverter.State(i).Contains(term, StringComparison.OrdinalIgnoreCase)
                || i.Status.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();

        _drill = shown;

        // The X was in the markup from the start and never appeared: every other search toggles it
        // here, and this one was missed. Collapsed while the box is empty, so it does not sit there
        // offering to clear nothing.
        if (DrillSearchClear is not null)
            DrillSearchClear.Visibility = string.IsNullOrEmpty(DrillSearch?.Text)
                ? Visibility.Collapsed : Visibility.Visible;

        decimal amount = shown.Sum(i => Sale(i).Amount);
        decimal carats = shown.Sum(i => Sale(i).Carats);

        KpiSales.Text = Money.Short(amount);
        KpiCarats.Text = carats.ToString("N2");
        KpiRate.Text = Money.Short(carats == 0 ? 0 : amount / carats);
        KpiBroker.Text = Money.Short(shown.Sum(i => Sale(i).Broker));
        KpiCount.Text = shown.Count.ToString();

        // Wrapped so the AMOUNT column can show what each invoice contributes under the current
        // filters — the same figure the tile above sums — rather than the invoice's own total.
        DrillGrid.ItemsSource = shown
            .Select(i => new DrillRow { Invoice = i, ScopedAmount = Sale(i).Amount })
            .ToList();
        if (DrillCount is not null)
            DrillCount.Text = $"{shown.Count:N0} invoice{(shown.Count == 1 ? "" : "s")}";

        DrawTrend(shown);

        // Grade now reaches sales too, by way of the lines. The note explains what the sales
        // figures mean while it is set: one grade's share of the invoices it appears on, not the
        // whole of those invoices.
        SalesScopeNote.Visibility = FilterGrade.SelectedItem is Grade
            ? Visibility.Visible : Visibility.Collapsed;

        await LoadBreakdownAsync();
    }

    /// <summary>
    /// Client-side filter over the drill-down list. Presentation only — the KPI figures above
    /// deliberately do not change, because they describe the filtered period, not this text box.
    /// </summary>

    /// <summary>Quick actions: select a tab that already exists. No new command.</summary>
    private void DashGo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string header }) return;

        foreach (var item in Tabs.Items.OfType<TabItem>())
        {
            if (item.Header as string == header) { item.IsSelected = true; return; }
        }
    }

    /// <summary>
    /// KPI tiles per row. Four across only while each still has room for a full figure; below that
    /// the tiles get wider, not narrower, because a clipped amount is worse than a taller card.
    /// </summary>
    private void KpiRow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.UniformGrid row) return;

        row.Columns = e.NewSize.Width switch
        {
            < 620 => 1,
            < 940 => 2,
            _ => 4,
        };
    }

    /// <summary>
    /// Below this width the chart column and the drill-down cannot both hold their content, and a
    /// fixed two-column grid clips rather than reflows. The drill-down drops underneath instead.
    /// </summary>
    private void DashSplit_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DashDrillCard is null) return;

        bool narrow = e.NewSize.Width < 1080;

        System.Windows.Controls.Grid.SetColumn(DashDrillCard, narrow ? 0 : 2);
        System.Windows.Controls.Grid.SetRow(DashDrillCard, narrow ? 2 : 0);
        System.Windows.Controls.Grid.SetColumnSpan(DashDrillCard, narrow ? 3 : 1);
        System.Windows.Controls.Grid.SetColumnSpan(DashChartPanel, narrow ? 3 : 1);

        DashGapCol.Width = new GridLength(narrow ? 0 : 18);
        DashDrillCol.Width = narrow ? new GridLength(0) : new GridLength(470);
        DashStackGap.Height = new GridLength(narrow ? 12 : 0);
        DashStackRow.Height = new GridLength(narrow ? 300 : 0);
        DashDrillCard.Height = narrow ? 300 : double.NaN;

        // A fixed height, not a minimum: the section sits inside the page ScrollViewer and so is
        // measured against infinite height. Left unbounded the invoice table renders all its rows
        // and never shows a scrollbar of its own — it just makes the page longer.
        double wanted = narrow ? 772 : 460;
        if (Math.Abs(DashSplit.Height - wanted) > 0.5) DashSplit.Height = wanted;
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        RangePicker.SelectedIndex = 5;                    // All time
        _syncingRange = true;
        FromDate.SelectedDate = ToDate.SelectedDate = null;
        FromDate.DisplayDateEnd = ToDate.DisplayDateStart = null;
        _syncingRange = false;
        FilterBuyer.SelectedItem = null;
        FilterGrade.SelectedItem = null;
        DrillSearch.Clear();
        LoadDashboard_Click(sender, e);
    }

    private async void LoadDashboard_Click(object sender, RoutedEventArgs e)
    {
        // A message belongs to the load that raised it. "Nothing in this period" was surviving the
        // reload that filled the page, so it sat there in red under a screen full of data.
        Status.Text = "";

        if (FilterProblem() is { } problem) { Say(problem); return; }

        var (from, to) = Period();

        DashboardSummary summary;
        try { summary = await Repo.DashboardAsync(from, to); }
        catch (Exception ex) { Say(ex.Message); return; }

        KpiSales.Text = Money.Short(summary.SalesAmount);
        KpiCarats.Text = summary.CaratsSold.ToString("N2");
        KpiRate.Text = Money.Short(summary.BlendedRate);
        KpiOutstanding.Text = Money.Short(summary.OutstandingTotal);
        KpiInventory.Text = Money.Short(summary.StockValue);
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

        // Range and buyer applied. Search is applied after this, in ApplyDashboardSearch, so
        // typing re-scopes the page without another read.
        _ranged = await FilteredInvoicesAsync();
        ApplyDashboardSearch();

        DashSyncChip.Text = $"Updated {DateTime.Now:HH:mm}";
        DashCatalogue.Text = $"{Catalogue.Grades.Count} grades · {_invoice.Buyers.Count} buyers";

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
        // Across every posted invoice, not the selected range — dashboard_summary returns the same
        // figure whatever p_from and p_to are. Sitting under a filter bar without saying so, it
        // reads as though the filters produced it.
        if (summary.OverdueCount > 0)
            parts.Add($"{summary.OverdueCount} overdue invoice(s) worth {summary.OverdueTotal:N2} "
                      + "across all posted invoices (not just this range)");
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
                var invoices = _drill;
                bars = which switch
                {
                    "salesperson" => Group(invoices, i => i.Salesperson ?? "unattributed", i => Sale(i).Amount),
                    "buyer" => Group(invoices, i => i.BuyerName, i => Sale(i).Amount),
                    _ => Group(invoices, i => i.BrokerName ?? "no broker", i => Sale(i).Broker),
                };
                break;
            }

            case "period":
            {
                var invoices = _drill;
                bars = Group(invoices, i => Bucket(i.InvoiceDate, bucket), i => Sale(i).Amount)
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

        // The share each bar takes of its track, as star weights. Same proportion as before —
        // |value| / max — expressed so the layout scales it instead of a hardcoded 420px.
        BarList.ItemsSource = bars.Select(b =>
        {
            double share = max == 0 ? 0 : (double)(Math.Abs(b.Value) / max);
            return new
            {
                b.Label,
                b.Secondary,
                ValueText = money ? Money.Short(b.Value) : $"{b.Value:N2} ct",
                DeltaText = "",
                BarStar = new GridLength(share, GridUnitType.Star),
                RestStar = new GridLength(1 - share, GridUnitType.Star),
            };
        }).ToList();

        // Withdrawn as soon as it stops being true: switching breakdowns is the common way to go
        // from an empty one to a full one, and the advice must not linger past that.
        if (bars.Count == 0) Say(EmptyReason());
        else if (Status.Text.StartsWith("No sales", StringComparison.Ordinal)
                 || Status.Text.StartsWith("No posted", StringComparison.Ordinal)) Status.Text = "";
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

    /// Everything the audit screen shows comes from this one list. Repo.AuditAsync is unchanged.
    private List<AuditRow> _audit = [];

    private async void LoadAudit_Click(object sender, RoutedEventArgs e)
    {
        var rows = await Read(() => Repo.AuditAsync());
        if (rows is null) return;

        // AuditRow.From keeps the same mapping the flattened columns used — AuditedTable, not
        // TableName, because TableName is BaseModel's own and reads "audit_log" on every row.
        _audit = rows.Select(AuditRow.From).ToList();

        AuditChip.Text = $"{_audit.Count:N0} entries";
        AuditSpan.Text = _audit.Count == 0
            ? "Nothing recorded yet"
            : $"{_audit[^1].ChangedAt:dd MMM yyyy} to {_audit[0].ChangedAt:dd MMM yyyy} · "
              + $"{_audit.Select(a => a.Entity).Distinct().Count()} entities";

        // The entity list can only offer what actually came back.
        object? keep = AuditEntity.SelectedItem;
        AuditEntity.ItemsSource = new[] { "All entities" }
            .Concat(_audit.Select(a => a.Entity).Distinct().OrderBy(x => x, StringComparer.Ordinal))
            .ToList();
        AuditEntity.SelectedItem = keep is string s && AuditEntity.Items.Contains(s) ? s : "All entities";

        ApplyAuditFilter();
    }

    private void AuditFilter_Changed(object sender, RoutedEventArgs e) => ApplyAuditFilter();

    private void ClearAuditSearch_Click(object sender, RoutedEventArgs e)
    {
        AuditSearch.Clear();
        AuditSearch.Focus();
    }

    private void ClearAuditFilters_Click(object sender, RoutedEventArgs e)
    {
        AuditAction.SelectedIndex = 0;
        if (AuditEntity.Items.Count > 0) AuditEntity.SelectedIndex = 0;
        AuditSearch.Clear();
        ApplyAuditFilter();
    }

    /// <summary>
    /// Narrows what is displayed. No query runs here — the same rows are already in memory, which
    /// is why the counts above the table describe exactly what is under it.
    /// </summary>
    private void ApplyAuditFilter()
    {
        if (AuditGrid is null) return;

        string action = (AuditAction.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        string entity = AuditEntity.SelectedIndex <= 0 ? "" : AuditEntity.SelectedItem as string ?? "";
        string term = AuditSearch?.Text.Trim().ToLowerInvariant() ?? "";

        var shown = _audit
            .Where(a => action.Length == 0 || a.Action == action)
            .Where(a => entity.Length == 0 || a.Entity == entity)
            .Where(a => term.Length == 0 || a.Search.Contains(term, StringComparison.Ordinal))
            .ToList();

        if (AuditSearchClear is not null)
            AuditSearchClear.Visibility = string.IsNullOrEmpty(AuditSearch?.Text)
                ? Visibility.Collapsed : Visibility.Visible;

        AuditGrid.ItemsSource = shown;
        AuditTimeline.ItemsSource = shown.Take(12).ToList();

        AuditKpiTotal.Text = shown.Count.ToString("N0");
        AuditKpiTotalNote.Text = shown.Count == _audit.Count
            ? "All loaded entries"
            : $"of {_audit.Count:N0} loaded";
        AuditKpiInsert.Text = shown.Count(a => a.Action == "INSERT").ToString("N0");
        AuditKpiUpdate.Text = shown.Count(a => a.Action == "UPDATE").ToString("N0");
        AuditKpiDelete.Text = shown.Count(a => a.Action == "DELETE").ToString("N0");

        AuditCount.Text = shown.Count == 0
            ? "Nothing matches these filters"
            : $"{shown.Count:N0} entr{(shown.Count == 1 ? "y" : "ies")} · select a row to see the fields";
    }

    /// <summary>
    /// Opens the detail drawer on the selected entry. Nothing is fetched — the row already carries
    /// its fields — so this is only a question of what is on screen.
    /// </summary>
    private void AuditRow_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (AuditDetailCard is null) return;

        bool open = AuditGrid.SelectedItem is AuditRow;
        AuditDetailCard.DataContext = AuditGrid.SelectedItem;
        AuditDetailCard.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        AuditDetailCol.Width = new GridLength(open ? DrawerWidth : 0);
        AuditDetailGap.Width = new GridLength(open ? 16 : 0);

        // The table gives up the room, not the timeline, while there is space for both.
        AuditSplit_SizeChanged(AuditSplit, null!);
    }

    private const double DrawerWidth = 340;

    private void CloseAuditDetail_Click(object sender, RoutedEventArgs e) => AuditGrid.UnselectAll();

    /// <summary>
    /// Three panels want the width and only two can have it. The timeline goes first — it is the
    /// newest rows of the same list, so nothing is lost that the table is not already showing. The
    /// drawer stays, because it is the only place the selected entry's fields appear.
    /// </summary>
    private void AuditSplit_SizeChanged(object sender, SizeChangedEventArgs? e)
    {
        if (AuditTimelineCard is null) return;

        double width = e?.NewSize.Width ?? AuditSplit.ActualWidth;
        if (width <= 0) return;

        // Judged on what the table would be left with, not on the window size. The table is the
        // point of the screen; keeping the timeline while it drops to 600px only bought a second
        // copy of the newest rows at the cost of the columns being cut off.
        bool open = AuditDetailCard?.Visibility == Visibility.Visible;
        double forTable = width - (open ? DrawerWidth + 16 : 0) - (300 + 16);
        bool room = forTable >= 700;

        AuditTimelineCard.Visibility = room ? Visibility.Visible : Visibility.Collapsed;
        AuditTimelineCol.Width = new GridLength(room ? 300 : 0);
        AuditGapCol.Width = new GridLength(room ? 16 : 0);
    }

    /// Everything the Users screen shows comes from this one list. Repo.UsersAsync is unchanged.
    private List<Profile> _users = [];

    private async void LoadUsers_Click(object sender, RoutedEventArgs e)
    {
        var rows = await Read(Repo.UsersAsync);
        if (rows is null) return;

        _users = rows;
        UserChip.Text = $"{_users.Count:N0} account{(_users.Count == 1 ? "" : "s")}";
        UserSubtitle.Text = $"{_users.Count(u => u.Active):N0} active · "
                            + $"{_users.Select(u => u.Role).Distinct().Count()} role(s) · managed in Supabase";

        // The role list can only offer roles that actually came back.
        object? keep = UserRole.SelectedItem;
        UserRole.ItemsSource = new[] { "All roles" }
            .Concat(_users.Select(u => u.Role).Distinct().OrderBy(r => r, StringComparer.Ordinal))
            .ToList();
        UserRole.SelectedItem = keep is string s && UserRole.Items.Contains(s) ? s : "All roles";

        ApplyUserFilter();

        // Creating an account needs the service_role key, which must never ship in a desktop binary.
        Say("Read-only — accounts are created and deactivated in the Supabase dashboard", ok: true);
    }

    private void UserFilter_Changed(object sender, RoutedEventArgs e) => ApplyUserFilter();

    private void ClearUserSearch_Click(object sender, RoutedEventArgs e)
    {
        UserSearch.Clear();
        UserSearch.Focus();
    }

    private void ClearUserFilters_Click(object sender, RoutedEventArgs e)
    {
        if (UserRole.Items.Count > 0) UserRole.SelectedIndex = 0;
        UserStatus.SelectedIndex = 0;
        UserSearch.Clear();
        ApplyUserFilter();
    }

    /// <summary>
    /// Narrows what is displayed. No query runs here — the same accounts are already in memory,
    /// which is why the counts above the list always describe the list under it.
    /// </summary>
    private void ApplyUserFilter()
    {
        if (UserGrid is null) return;

        string role = UserRole.SelectedIndex <= 0 ? "" : UserRole.SelectedItem as string ?? "";
        string status = (UserStatus.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        string term = UserSearch?.Text.Trim() ?? "";

        var shown = _users
            .Where(u => role.Length == 0 || u.Role == role)
            .Where(u => status.Length == 0 || (status == "ACTIVE" ? u.Active : !u.Active))
            .Where(u => term.Length == 0
                        || (u.FullName ?? "").Contains(term, StringComparison.OrdinalIgnoreCase)
                        || u.Role.Contains(term, StringComparison.OrdinalIgnoreCase)
                        || u.Id.ToString().Contains(term, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (UserSearchClear is not null)
            UserSearchClear.Visibility = string.IsNullOrEmpty(UserSearch?.Text)
                ? Visibility.Collapsed : Visibility.Visible;

        UserGrid.ItemsSource = shown;

        UserKpiTotal.Text = shown.Count.ToString("N0");
        UserKpiTotalNote.Text = shown.Count == _users.Count
            ? "All loaded accounts"
            : $"of {_users.Count:N0} loaded";
        UserKpiActive.Text = shown.Count(u => u.Active).ToString("N0");
        UserKpiOwners.Text = shown.Count(u => u.Role == "owner").ToString("N0");

        // Staff is every account that is not an owner, whatever it is called. Counting only a role
        // literally named "staff" would silently omit managers and salespeople.
        UserKpiStaff.Text = shown.Count(u => u.Role != "owner").ToString("N0");

        UserCount.Text = shown.Count == 0
            ? "No accounts match these filters"
            : $"{shown.Count:N0} account{(shown.Count == 1 ? "" : "s")}";
    }

    /// The id is what gets pasted into Supabase when creating the matching profile row.
    private void CopyUserId_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Profile user) return;

        try
        {
            Clipboard.SetText(user.Id.ToString());
            Say($"Account id for {user.FullName} copied", ok: true);
        }
        catch (Exception ex)
        {
            // The clipboard can be held by another process; that is not this app being broken.
            Say(ex.Message);
        }
    }

    /// Every setting the screen shows. Repo.ConfigAsync is unchanged.
    private List<SettingItem> _settings = [];

    private async void LoadSettings_Click(object sender, RoutedEventArgs e)
    {
        var config = await Read(Repo.ConfigAsync);
        if (config is null) return;

        _settings = config.OrderBy(c => c.Key)
                          .Select(c => SettingItem.From(c.Key, c.Value))
                          .ToList();

        SettingChip.Text = $"{_settings.Count:N0} setting{(_settings.Count == 1 ? "" : "s")}";
        SettingSubtitle.Text = "Policies and thresholds · owner only";
        ApplySettingFilter();
    }

    private void SettingFilter_Changed(object sender, RoutedEventArgs e) => ApplySettingFilter();

    private void ClearSettingSearch_Click(object sender, RoutedEventArgs e)
    {
        SettingSearch.Clear();
        SettingSearch.Focus();
    }

    /// <summary>
    /// Groups the settings into their cards and applies the search. No query runs — the same rows
    /// are already in memory, so a search never costs a round trip.
    /// </summary>
    private void ApplySettingFilter()
    {
        if (SettingCards is null) return;

        string term = SettingSearch?.Text.Trim().ToLowerInvariant() ?? "";
        var shown = term.Length == 0
            ? _settings
            : _settings.Where(x => x.Search.Contains(term, StringComparison.Ordinal)).ToList();

        if (SettingSearchClear is not null)
            SettingSearchClear.Visibility = string.IsNullOrEmpty(SettingSearch?.Text)
                ? Visibility.Collapsed : Visibility.Visible;

        // A card with nothing in it is dropped rather than left as an empty frame.
        SettingCards.ItemsSource = SettingItem.Categories
            .Select(c => new
            {
                Title = c,
                Items = shown.Where(x => x.Category == c).ToList(),
            })
            .Where(g => g.Items.Count > 0)
            .Select(g => new
            {
                g.Title,
                Caption = $"{g.Items.Count} setting{(g.Items.Count == 1 ? "" : "s")}",
                g.Items,
            })
            .ToList();

        SettingEmpty.Visibility = shown.Count == 0 && _settings.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;

        ShowDirtyCount();
    }

    private void ShowDirtyCount()
    {
        if (SettingDirty is null) return;

        int dirty = _settings.Count(x => x.IsDirty);
        SettingDirty.Text = dirty == 0 ? "" : $"{dirty} unsaved change{(dirty == 1 ? "" : "s")}";
    }

    private void DiscardSettings_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _settings) item.Value = item.OriginalValue;
        ApplySettingFilter();
        Say("Changes discarded", ok: true);
    }

    /// <summary>
    /// Writes every changed setting. Still one <c>Repo.SetConfigAsync(key, value)</c> per key —
    /// the same call the old select-a-row-and-save button made — just applied to whatever the user
    /// edited rather than to the single selected row.
    /// </summary>
    private async void SaveSetting_Click(object sender, RoutedEventArgs e)
    {
        var dirty = _settings.Where(x => x.IsDirty).ToList();
        if (dirty.Count == 0) { Say("Nothing has changed"); return; }

        var failures = new List<string>();
        using (Busy(SaveSettings, "Saving…", SaveSettings))
        {
            foreach (var item in dirty)
            {
                // Each key is its own write, so a refusal on one does not silently roll back the
                // others — the database is the authority on who may change what.
                if (await Repo.SetConfigAsync(item.Key, item.Value) is { } failure)
                    failures.Add($"{item.Key}: {failure}");
            }
        }

        if (failures.Count > 0)
        {
            Say(failures[0]);
            LoadSettings_Click(sender, e);       // show what actually landed
            return;
        }

        Say($"Saved {dirty.Count} setting{(dirty.Count == 1 ? "" : "s")}", ok: true);
        LoadSettings_Click(sender, e);
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
        BeginBusy();
        try { return await read(); }
        catch (Exception ex) { Say(ex.Message); return null; }
        finally { EndBusy(); }
    }

    /// <summary>
    /// How many reads or writes are in flight. Counted rather than a flag because they nest —
    /// a Busy scope around a handler that itself calls Read would otherwise switch the bar off
    /// halfway through the work it is reporting.
    /// </summary>
    private int _inFlight;

    private void BeginBusy()
    {
        if (++_inFlight != 1 || BusyVeil is null) return;

        BusyVeil.Visibility = Visibility.Visible;
        Fade(BusyVeil, 1, 160, null);
    }

    private void EndBusy()
    {
        if (--_inFlight > 0) return;

        _inFlight = 0;
        if (BusyVeil is null) return;

        // Collapsed only after the fade, or the veil vanishes mid-animation and the page snaps in.
        Fade(BusyVeil, 0, 220, () => { if (_inFlight == 0) BusyVeil.Visibility = Visibility.Collapsed; });
    }

    private static void Fade(UIElement target, double to, int ms, Action? then)
    {
        var fade = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms));
        if (then is not null) fade.Completed += (_, _) => then();
        target.BeginAnimation(OpacityProperty, fade);
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

        // How long it stays depends on what it is. The rule used to be "confirmations clear,
        // everything else is permanent", which left a prompt like "Pick a grade and size" sitting
        // in the bar long after the user had picked one.
        //
        //   confirmation ...... 4s. It reports something that already happened.
        //   prompt / refusal ... 8s. Long enough to read twice, short enough not to describe a
        //                        screen the user has since moved on from.
        //   backend failure .... stays. A read that failed leaves an empty grid behind, and an
        //                        empty grid with no message reads as "there is no data" rather
        //                        than "this did not load". Friendly.Translates is what tells the
        //                        two apart: it only rewrites database and transport errors.
        _statusTimer.Stop();
        if (ok) { _statusTimer.Interval = ConfirmationLinger; _statusTimer.Start(); }
        else if (!Friendly.Translates(message)) { _statusTimer.Interval = PromptLinger; _statusTimer.Start(); }
    }

    /// Status text is transient: it clears itself so a stale instruction cannot be mistaken for
    /// the current state of the screen. The interval is set per message — see Say.
    private static readonly TimeSpan ConfirmationLinger = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan PromptLinger = TimeSpan.FromSeconds(8);
    private readonly DispatcherTimer _statusTimer = new() { Interval = ConfirmationLinger };
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
