using System.Collections.ObjectModel;
using System.Globalization;
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
    // ── Wording used on more than one screen ────────────────────────────────
    // Named once so the same sentence cannot drift into four slightly different ones, which is
    // what happened to the empty-filter line before it was pulled up here.
    private const string Loading = "Loading…";
    private const string AllBuyers = "All buyers";
    private const string NoFilterMatch = "Nothing matches these filters";
    private const string DayMonthYear = "dd MMM yyyy";
    private const string PickGradeSize = "Pick a grade and size";
    private const string WeightField = "Weight";
    private const string CustomRange = "CUSTOM";

    /// "This month" — must match RangePicker's SelectedIndex in the XAML. Clear returns here, so
    /// the page after Clear is the page you opened.
    private const int DefaultRangeIndex = 2;

    /// Shared with the XAML side's CountLabelConverter, so a list and its caption pluralise alike.
    private static string Plural(int n, string one, string many) => Words.Plural(n, one, many);

    private static string Plural(int n, string noun) => Words.Plural(n, noun);

    private InvoiceEntry _invoice = new();

    /// Started once the first config read lands, so it counts against the configured timeout
    /// rather than the default.
    private IdleTimeout? _idle;

    /// <summary>
    /// The imported-stock fingerprint as this machine last saw it online. Captured on every
    /// successful catalogue load, and handed to the outbox when an import is queued offline so the
    /// replay can tell "nothing changed" from "someone else imported".
    /// </summary>
    private string? _lastStockFingerprint;

    private DispatcherTimer? _syncTimer;

    /// <summary>
    /// Sends anything the outbox is holding, as soon as there is a connection to send it over.
    ///
    /// Polled rather than event-driven: Windows' network-availability events fire on an interface
    /// coming up, which is not the same as this Supabase project being reachable, and a desk on
    /// hotel wifi sees plenty of the former without the latter.
    /// </summary>
    private void StartSyncWatcher()
    {
        Outbox.PendingChanged += n => Dispatcher.BeginInvoke(() => ShowPending(n));

        _syncTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(45),
        };
        _syncTimer.Tick += async (_, _) => await TrySyncAsync();
        _syncTimer.Start();

        _ = TrySyncAsync();      // anything parked by the previous run goes now
    }

    private async Task TrySyncAsync()
    {
        if (await Outbox.PendingCountAsync() == 0) { ShowPending(0); return; }

        // No !Db.IsOnline guard. That flag only returns to true when a read SUCCEEDS
        // (Db.NoteTransport, called from Read), so skipping the attempt while it is false meant the
        // one thing that could clear it never ran: a queued import sat there every 45 seconds until
        // the user happened to open a tab that read successfully. For a feature whose whole promise
        // is "it goes when the connection returns", waiting to be told the connection returned by
        // something else is the wrong way round.
        //
        // Attempting costs one timeout. SendAsync already catches the transport exceptions and
        // returns a message, and a failed replay leaves every queued row on disk untouched.

        // The verifier the outbox uses to decide whether a queued REPLACE is still safe to apply.
        var (sent, _) = await Outbox.ReplayAsync(op =>
            op == "rpc/replace_imported_stock"
                ? Repo.ImportedStockFingerprintAsync()
                : Task.FromResult<string?>(null));

        if (sent > 0)
        {
            _lastStockFingerprint = await Repo.ImportedStockFingerprintAsync();
            Say($"Saved import applied — {Plural(sent, "queued change")} sent", ok: true);
            ReloadCurrentTab();
        }
        else if (Outbox.Blocked is { } why)
        {
            Say(why);
        }

        await ShowPendingAsync();
    }

    private async Task ShowPendingAsync() => ShowPending(await Outbox.PendingCountAsync());

    /// The connection pill doubles as the queue's only visible home. A parked import that nothing
    /// on screen mentions is indistinguishable from an import that was silently dropped.
    private void ShowPending(int pending)
    {
        if (SyncPill is null) return;

        SyncPill.Visibility = pending == 0 ? Visibility.Collapsed : Visibility.Visible;
        SyncPillText.Text = Outbox.Blocked is null
            ? $"{pending:N0} waiting to sync"
            : $"{pending:N0} held — needs attention";
    }

    /// <summary>
    /// Money decimals and the low-stock band are read by converters, and a converter only runs when
    /// its row is realised — so a saved change left every grid already on screen showing the old
    /// policy until it was reloaded. Re-render them instead: no query, just the same rows drawn
    /// against the new values.
    /// </summary>
    private void RerenderForPolicy()
    {
        foreach (var grid in new[] { StockGrid, InvoiceGrid, ReceivablesGrid })
            if (grid?.ItemsSource is not null) grid.Items.Refresh();
    }
    private readonly ObservableCollection<DispositionRow> _dispositions = [];
    private bool _saving;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _invoice;

        // The FIRST invoice needs watching too. _invoice is created at its field initialiser, so
        // the two places that call WatchHolds -- Clear, and opening a memo -- both run only after
        // one has already been replaced. Without this the screen you land on holds no stock at all,
        // and typing a line moved nothing until you had started a second invoice.
        WatchHolds(null, _invoice);

        // The last word on what was on screen. The per-line saves below only fire for grade, size
        // and weight -- the fields that move stock -- so a price or a remark typed after the last
        // of those would otherwise not be in the file. Closing catches the lot.
        //
        // It SAVES on close. It does not release: the holds are meant to outlive the process, and
        // this is what lets the entry come back to them.
        //
        // ONLY ONCE THE RESTORE HAS RUN. _invoice starts as a fresh blank entry and the restore is
        // several round trips away; closing the window before it lands would have saved that blank
        // over the file -- and a save with no lines DELETES it. One quick close after launch was
        // enough to lose the stored entry and strand every carat it was holding, which is exactly
        // the failure this whole mechanism exists to prevent.
        Closing += (_, _) => { if (_entryRestored) SaveEntry(); };

        // Ctrl+S goes through the same busy scope as the button — it is the same operation and was
        // the one route that left the buttons live mid-save.
        InputBindings.Add(new KeyBinding(
            // Ctrl+P, not Ctrl+S. Nothing on this screen saves any more: Print memo prints, and
            // Confirm sale is the only thing that writes. A Ctrl+S that quietly printed would be
            // the worst of both, and one that did nothing would be worse still.
            new RelayCommand(() => PrintMemo_Click(this, new RoutedEventArgs())),
            Key.P, ModifierKeys.Control));

        // Ctrl+S back alongside it, because the two are different jobs: Ctrl+P puts paper in the
        // buyer's hand and writes nothing, Ctrl+S parks the invoice so the desk can come back to
        // it. Same handler as the button, so it goes through the same busy scope.
        InputBindings.Add(new KeyBinding(
            new RelayCommand(() => SaveDraft_Click(this, new RoutedEventArgs())),
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

        // A trailing blank row instead of the DataGrid's add-placeholder. The cells hold live
        // editors now, and an editor inside a CellTemplate never triggers the edit-begin that turns
        // the placeholder into a real item — so the first disposition anyone typed was silently
        // discarded. A row that already exists has nothing to become.
        EnsureTrailingDisposition();
        DispositionGrid.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler((_, _) => EnsureTrailingDisposition()));

        // A config file that could not be read falls back to the shipped values rather than
        // killing a process that has no window yet — but silently pointing at the wrong project
        // is exactly the kind of thing nobody notices until the numbers are wrong, so it is said
        // as soon as there is somewhere to say it.
        if (AppSettings.Problem is { } configProblem) Say(configProblem);

        WhoAmI.Text = Db.CurrentUser?.FullName ?? "";
        Initials.Text = Initialise(Db.CurrentUser?.FullName);
        UsersTab.Visibility = Db.IsOwner ? Visibility.Visible : Visibility.Collapsed;
        ApplyRolePermissions();

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
            box.ItemsSource = Catalogue.ActiveSizes;
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
        // Before anything is read. Catalogue.LoadAsync and the two party reads below all CLEAR an
        // ObservableCollection that a picker is bound to, and WPF answers a cleared ItemsSource by
        // dropping the selection and writing that null back down the two-way binding -- so a
        // half-typed invoice loses its grade, size, buyer and broker the moment the read starts,
        // and any repair afterwards has nothing left to work from. See SaleLine.RememberCatalogue.
        //
        // Here rather than in each of the eight callers: every one of them has the same problem and
        // only this method knows when the clearing happens.
        _invoice.RememberCatalogue();

        try
        {
            // Before the catalogue: money_precision and alert_low_stock_ct decide how the first
            // render of every figure and every stock badge reads.
            await Policy.LoadAsync();
            if (_idle is null)
            {
                _idle = new IdleTimeout(this, () => _inFlight > 0, ReloadCurrentTab);
                Policy.Changed += RerenderForPolicy;
                StartSyncWatcher();
            }

            // Captured while we can. If the connection drops later, this is what an offline import
            // is measured against when it eventually replays.
            _lastStockFingerprint = await Repo.ImportedStockFingerprintAsync();

            await Catalogue.LoadAsync();
            var buyers = await Repo.BuyersAsync();
            var brokers = await Repo.BrokersAsync();

            _invoice.Buyers.Clear();
            foreach (var b in buyers) _invoice.Buyers.Add(new PartyRef(b.BuyerId, b.Name, b.DefaultTermsDays));

            _invoice.Brokers.Clear();
            foreach (var b in brokers) _invoice.Brokers.Add(new PartyRef(b.BrokerId, b.Name, null, b.DefaultBrokerPct));

            FilterBuyer.ItemsSource = buyers;                  // the dashboard's buyer filter

            Pill(true, $"{Db.Active.Name} · {Catalogue.Grades.Count} grades · {buyers.Count} buyers");

            // An empty picker looks like a broken screen. Say which it is.
            if (buyers.Count == 0) Say("No buyers came back from Supabase — add one on the Master data tab");

            // LAST, and only here: a stored line names its grade and size by id, and until the
            // catalogue is loaded there is nothing to resolve them against.
            await RestoreEntryAsync();
        }
        catch (Exception ex)
        {
            Pill(false, Db.IsOnline ? "Server refused the request" : "Offline");
            Say(ex.Message);
        }
        finally
        {
            // In a finally: a read that fails half way through has still cleared whatever it got to,
            // so the screen needs putting back whether or not the rest of it worked.
            _invoice.CatalogueChanged();
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
                                         : $"{line.Grade.ShortName} · {line.GrossWeightCt:N2} ct";

        if (MessageBox.Show(this, $"Remove {what}?", "Remove line",
                            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _invoice.Lines.Remove(line);
        }
    }

    /// <summary>
    /// Ticking is for removing several lines in one go. It changes no figure: the totals, the
    /// summary and what posts are identical whether anything is ticked or not.
    /// </summary>
    /// The header tick box. Held by reference because it lives inside a column's HeaderTemplate,
    /// where FindName cannot reach it.
    private CheckBox? _tickAll;

    private void TickAllLoaded(object sender, RoutedEventArgs e)
    {
        _tickAll = sender as CheckBox;
        TickCountChanged();
    }

    private void TickLine_Click(object sender, RoutedEventArgs e) => TickCountChanged();

    /// <summary>
    /// All or nothing. The box shows "some" when the rows disagree, but a CLICK never lands on
    /// that state -- from indeterminate it ticks everything, which is what a half-ticked list
    /// makes you want. Cycling a user into "some" would be a third meaning nobody asked for.
    /// </summary>
    private void TickAllLines_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box) return;

        // EVERY line, blank ones included: they are removable, so they are tickable.
        // From indeterminate WPF hands us true, which is already the answer we want.
        bool on = box.IsChecked == true;
        foreach (var line in _invoice.Lines) line.Selected = on;
        TickCountChanged();
    }

    /// <summary>
    /// Keeps the header box and the Remove button honest about what is ticked.
    ///
    /// Three states, and the middle one is the point: with two of five lines ticked, a header box
    /// showing plain unticked is a lie, and showing plain ticked is a worse one.
    /// </summary>
    /// <summary>The row's own Remove. The same question the Delete key asks, for hands on a mouse.</summary>
    /// <summary>
    /// Shows a remark from its beginning once the caret has moved on.
    ///
    /// A TextBox keeps the scroll position typing left it at. Type a remark longer than the column
    /// and the box stays scrolled to the END, so every later glance at that row shows the tail of
    /// the sentence with no start to it -- which reads as a mangled value rather than a long one.
    /// Losing focus is the moment it stops being something being typed and becomes something being
    /// read, so that is where it goes home. The value itself is never touched; the tooltip carries
    /// the whole of it for anything the column cannot fit.
    /// </summary>
    private void RemarkBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box) return;
        box.CaretIndex = 0;
        box.ScrollToHome();
    }

    private void RemoveLine_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SaleLine line }) return;

        // Only asked when there is typing to lose. Clearing a row that is entirely empty is not a
        // decision worth interrupting for, and now that Remove is on every row it is the commonest
        // thing the button does.
        if (!line.IsBlank)
        {
            string what = line.Grade is null ? $"line {_invoice.Lines.IndexOf(line) + 1}"
                                             : $"{line.Grade.ShortName} · {line.GrossWeightCt:N2} ct";

            if (MessageBox.Show(this, $"Remove {what}?", "Remove line",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
        }

        _invoice.Lines.Remove(line);

        // The grid always keeps one row to type into, so taking the last line off is not a special
        // case -- it clears that row rather than emptying the table. Same rule the screen opens
        // with, and the same one OpenInvoiceAsync applies to an invoice that came back with none.
        if (_invoice.Lines.Count == 0) AddLine();
        TickCountChanged();
    }

    private void TickCountChanged()
    {
        var lines = _invoice.Lines;
        int n = lines.Count(l => l.Selected);

        if (_tickAll is not null)
            _tickAll.IsChecked = lines.Count == 0 || n == 0 ? false
                               : n == lines.Count ? true
                               : null;                       // some, but not all

        // Called from the constructor's path too, before the template has produced the button.
        if (DeleteLines is null) return;

        // The label is a TextBlock inside the button now, because the button holds an icon beside
        // it. Content is left alone -- Busy() swaps Content for "Saving…" and puts it back, and it
        // must find the whole icon-and-label panel there, not a bare string.
        DeleteLinesLabel.Text = n == 1 ? "Remove 1 line" : $"Remove {n} lines";
        DeleteLines.Visibility = n == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Removes every ticked line at once, after one question naming how many.
    ///
    /// Blank rows count. Four empty lines added by mistake are exactly what this is for, and the
    /// per-row Remove would mean four confirmations. The grid always keeps one row to type into.
    /// </summary>
    private void DeleteLines_Click(object sender, RoutedEventArgs e)
    {
        var doomed = _invoice.Lines.Where(l => l.Selected).ToList();
        if (doomed.Count == 0) return;

        // Only asked when there is typing to lose. Removing rows that are entirely empty is not
        // a decision worth interrupting for.
        if (doomed.Any(l => !l.IsBlank)
            && MessageBox.Show(this,
                   doomed.Count == 1 ? "Remove 1 line from this invoice?"
                                     : $"Remove {doomed.Count} lines from this invoice?",
                   "Remove lines", MessageBoxButton.YesNo, MessageBoxImage.Question)
               != MessageBoxResult.Yes)
            return;

        foreach (var line in doomed) _invoice.Lines.Remove(line);

        TickCountChanged();
        Say($"Removed {doomed.Count} line(s)", ok: true);
    }

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

    private void AddLine_Click(object sender, RoutedEventArgs e) => AddLine();

    /// <summary>
    /// Scrolls a line into view, whichever control owns the offset.
    ///
    /// ScrollIntoView alone is enough while the grid is its own scroller, and it is the call that
    /// realises the row's container. It goes silently dead the moment the grid is content-sized
    /// inside a scroller instead -- which this screen has been both ways -- and a line appended
    /// past the fold then stays off screen with the caret sitting in it. BringIntoView on the row's
    /// own container walks up to whatever actually scrolls, so both layouts behave the same.
    ///
    /// A dispatcher pass first, because a row added a moment ago has no container until layout
    /// has run.
    /// </summary>
    private void ShowRow(object row)
    {
        Grid.ScrollIntoView(row);
        Dispatcher.BeginInvoke(
            () => (Grid.ItemContainerGenerator.ContainerFromItem(row) as FrameworkElement)?.BringIntoView(),
            DispatcherPriority.Loaded);
    }

    private void AddLine()
    {
        var line = new SaleLine();
        _invoice.Lines.Add(line);
        TickCountChanged();
        ShowRow(line);
        Grid.CurrentCell = new DataGridCellInfo(line, Grid.Columns[0]);
    }

    // ── Stock is held as the invoice is typed (0043) ────────────────────────

    /// <summary>
    /// Listens to the invoice on screen, so every line change reaches the reservation.
    ///
    /// Called wherever _invoice is replaced. The old entry is unsubscribed first: an abandoned
    /// InvoiceEntry still raising into these handlers would hold carats for an invoice nobody can
    /// see any more.
    /// </summary>
    private void WatchHolds(InvoiceEntry? previous, InvoiceEntry current)
    {
        if (previous is not null)
        {
            previous.LineHoldChanged -= OnLineHoldChanged;
            previous.LineDropped -= OnLineDropped;
        }
        current.LineHoldChanged += OnLineHoldChanged;
        current.LineDropped += OnLineDropped;
    }

    /// <summary>
    /// Writes the entry to disk after anything that could have changed what it holds.
    ///
    /// It exists because the holds outlive the process. Nothing releases a reservation when the app
    /// closes -- deliberately, on the desk's word -- so the entry that made those holds has to come
    /// back with them, or the carats are spoken for by an invoice no longer on any screen.
    /// </summary>
    /// <summary>
    /// True once RestoreEntryAsync has run -- whether it found an entry or not. Until then the
    /// screen holds a fresh blank entry that must never be written over the stored one. See the
    /// Closing handler in the constructor.
    /// </summary>
    private bool _entryRestored;

    private void SaveEntry()
    {
        var e = _invoice;
        EntryStore.Save(new EntryStore.StoredEntry(
            e.ClientRef, e.InvoiceDate, e.Buyer, e.Broker, e.BrokerPct, e.TermsDays, e.DocType,
            // Blank rows are not saved. The opening row is scaffolding, not work, and a file holding
            // only scaffolding would restore a screen indistinguishable from a fresh one.
            [.. e.Lines.Where(l => !l.IsBlank).Select(l => new EntryStore.StoredLine(
                l.LineKey, l.Grade?.GradeId, l.Size?.SizeId, l.GrossWeightCt, l.SelectionCt,
                l.PricePerCt, l.ExRate, l.Less1Pct, l.Less2Pct, l.Remark))]));
    }

    /// <summary>
    /// Puts back the Sales entry the app was closed on, and re-states its holds.
    ///
    /// RE-STATING IS NOT RE-RESERVING. reserve_line upserts on (client_ref, line_key); the restored
    /// entry carries the same pair, so each call corrects the hold that is already there. That is
    /// what makes it safe to send them rather than trusting the file: if the app died between a
    /// keystroke and its round trip, this is the pass that repairs it.
    ///
    /// Called after the catalogue and the buyer list have loaded, because a stored line names a
    /// grade and a size by id and there is nothing to resolve them against until then.
    /// </summary>
    private async Task RestoreEntryAsync()
    {
        // Set FIRST, and on both paths: nothing to restore is a settled state too, and the window
        // may be saved on close from here on either way.
        _entryRestored = true;

        if (EntryStore.Load() is not { } stored) return;

        var leaving = _invoice;
        var entry = new InvoiceEntry { ClientRef = stored.ClientRef };
        foreach (var b in leaving.Buyers) entry.Buyers.Add(b);
        foreach (var b in leaving.Brokers) entry.Brokers.Add(b);

        entry.InvoiceDate = stored.InvoiceDate;
        entry.BrokerPct   = stored.BrokerPct;
        entry.TermsDays   = stored.TermsDays;
        if (stored.DocType is { Length: > 0 } doc) entry.DocType = doc;
        entry.SelectedBuyer  = entry.Buyers.FirstOrDefault(p => p.Name == stored.Buyer);
        entry.SelectedBroker = entry.Brokers.FirstOrDefault(p => p.Name == stored.Broker);
        entry.Buyer  = stored.Buyer;
        entry.Broker = stored.Broker;

        entry.Lines.Clear();                                   // drop the constructor's opening row
        foreach (var l in stored.Lines)
            entry.Lines.Add(new SaleLine
            {
                LineKey        = l.LineKey,
                Grade          = Catalogue.Grades.FirstOrDefault(g => g.GradeId == l.GradeId),
                Size           = Catalogue.AllSizes.FirstOrDefault(z => z.SizeId == l.SizeId),
                GrossWeightCt  = l.GrossWeightCt,
                SelectionCt    = l.SelectionCt,
                PricePerCt     = l.PricePerCt,
                ExRate         = l.ExRate,
                Less1Pct       = l.Less1Pct,
                Less2Pct       = l.Less2Pct,
                Remark         = l.Remark,
            });
        entry.Lines.Add(new SaleLine());                       // and a fresh one to carry on typing

        _invoice = entry;
        WatchHolds(leaving, _invoice);
        DataContext = _invoice;

        // _held is empty, so every line is sent. See the summary: this is the repair pass, and the
        // unique key is what makes it cost nothing when there was nothing to repair.
        foreach (var line in _invoice.Lines) await HoldLineAsync(line);

        Say($"Picked up where you left off — {_invoice.RealLines.Count} line(s) still holding stock",
            ok: true);
    }

    // async void, and deliberately: these are event handlers on a view model, there is no caller to
    // await them, and the work must not block the keystroke that raised it. Both callees swallow
    // their own failures into the status bar rather than throwing.
    private async void OnLineHoldChanged(SaleLine line) => await HoldLineAsync(line);
    private async void OnLineDropped(SaleLine line) => await DropLineAsync(line);



    /// The lines whose reservation is already what the line says, so an edit that changed nothing
    /// worth holding does not go back to the database. Keyed by LineKey, valued by what was sent.
    private readonly Dictionary<Guid, (long Grade, long Size, decimal Ct)> _held = [];

    /// <summary>
    /// Holds this line's carats, or corrects the hold it already has.
    ///
    /// Called whenever a line changes. The database keys on (client_ref, line_key), so this is an
    /// UPDATE of the same hold rather than a second one -- correcting 10.00 to 1.00 leaves one
    /// reservation holding 1.00, which is what stops the bucket falling by 11.
    ///
    /// NOT awaited by the caller, and it must not be: this runs while somebody is typing, and a
    /// round trip between keystrokes would make the grid unusable. A refusal reaches the status
    /// bar; it does not interrupt.
    /// </summary>
    private async Task HoldLineAsync(SaleLine line)
    {
        if (line.Grade is not { } grade || line.Size is not { } size)
        {
            // Not a bucket yet. If this line was holding something under an earlier grade or size,
            // that hold has to go -- otherwise clearing the grade would strand the carats.
            await DropLineAsync(line);
            return;
        }

        var want = (grade.GradeId, size.SizeId, line.GrossWeightCt);
        if (_held.TryGetValue(line.LineKey, out var have) && have == want) return;

        string? failure = await Repo.ReserveLineAsync(
            _invoice.ClientRef, line.LineKey, grade.GradeId, size.SizeId, line.GrossWeightCt);

        if (failure is not null) { Say(failure); return; }

        if (line.GrossWeightCt <= 0) _held.Remove(line.LineKey);
        else _held[line.LineKey] = want;

        SaveEntry();

        RefreshStockIfShowing();
    }

    /// <summary>Gives one line's carats back. The row was removed, or emptied of its bucket.</summary>
    private async Task DropLineAsync(SaleLine line)
    {
        if (!_held.ContainsKey(line.LineKey)) return;      // it was holding nothing

        string? failure = await Repo.ReleaseLineAsync(_invoice.ClientRef, line.LineKey);
        if (failure is not null) { Say(failure); return; }

        // AFTER the round trip, never before. Forgetting the hold first meant a refused release
        // left the carats held in the database with nothing on this side that knew about them.
        _held.Remove(line.LineKey);
        SaveEntry();

        RefreshStockIfShowing();
    }

    /// <summary>
    /// Gives back everything this entry holds. Move to stock, and starting a new invoice.
    ///
    /// It cannot return more than was taken -- the database deletes only the rows this entry
    /// holds -- so there is no figure here for this side to get wrong.
    /// </summary>
    private async Task<decimal> DropEntryAsync()
    {
        var (failure, returned, _) = await Repo.ReleaseEntryAsync(_invoice.ClientRef);
        if (failure is not null) { Say(failure); return 0m; }

        // ONLY once the database has actually let go. This ran first, and a release that was
        // refused -- offline, or a server error -- then left the carats held with the local record
        // of them already deleted: no file, no cache, no client_ref, and so no way back to them
        // from any screen. Keeping the record until the release succeeds means a failure is
        // retryable rather than permanent.
        _held.Clear();
        EntryStore.Clear();

        RefreshStockIfShowing();
        return returned;
    }

    /// The Stock page reads a balance that a reservation has just changed. Reloaded only when it
    /// is the page on screen: a round trip per keystroke to repaint a tab nobody is looking at is
    /// a cost with no reader.
    private void RefreshStockIfShowing()
    {
        if (Tabs.SelectedItem is TabItem { Header: "Stock" }) LoadStock_Click(this, new RoutedEventArgs());
    }

    private async void NewInvoice_Click(object sender, RoutedEventArgs e)
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

        // Whatever this entry was holding goes back. An abandoned invoice must not keep carats
        // spoken for -- nobody would ever come back to release them, and the bucket would read
        // short for ever with nothing on screen to explain it.
        //
        // Before the entry is replaced, because DropEntryAsync releases against _invoice.ClientRef
        // and a new InvoiceEntry carries a different one.
        await DropEntryAsync();

        ResetEntry();
    }

    /// <summary>
    /// Puts a fresh invoice on screen. Shared by Clear and by Move to stock, which both END the
    /// entry -- one abandons it, the other hands its carats back -- and leaving the finished rows
    /// on screen after either said the invoice was still holding stock when it was holding nothing.
    ///
    /// Releases nothing itself. Both callers give the carats back before they get here, and they
    /// must: the release goes against the OLD ClientRef, and this is the line that replaces it.
    /// </summary>
    private void ResetEntry()
    {
        // Active only. Reopening a draft written to a since-deactivated buyer adds that buyer to
        // THAT invoice's picker so the name stays on screen -- carrying it into a brand new invoice
        // would make it selectable for new trade, which is the one thing deactivating it meant to
        // stop. See PartyRef.Active.
        var buyers = _invoice.Buyers.Where(b => b.Active).ToList();
        var brokers = _invoice.Brokers.Where(b => b.Active).ToList();

        var leaving = _invoice;
        _invoice = new InvoiceEntry();
        WatchHolds(leaving, _invoice);
        foreach (var b in buyers) _invoice.Buyers.Add(b);
        foreach (var b in brokers) _invoice.Brokers.Add(b);

        DataContext = _invoice;
        TickCountChanged();
        Status.Text = "";
    }

    /// <summary>
    /// Prints the invoice ON SCREEN. It saves nothing.
    ///
    /// That is the whole distinction from Confirm sale: this is the piece of paper that goes out
    /// with the parcel while the deal is still being agreed, and it must not book anything. No
    /// invoice row, no invoice number, no stock movement, and nothing to undo if the buyer walks
    /// away -- so there is also no record it could duplicate.
    ///
    /// The note is built from _invoice, never from the database, because there is nothing in the
    /// database to build it from. VInvoice and VSalesLine here are carriers for the printer, not
    /// rows: they are never saved, and their ids are deliberately zero.
    /// </summary>
    private void PrintMemo_Click(object sender, RoutedEventArgs e)
    {
        Grid.CommitEdit();

        var lines = _invoice.RealLines;
        if (lines.Count == 0) { Say("Nothing to print — add a line first"); return; }

        var memo = new VInvoice
        {
            InvoiceNo    = "MEMO",
            InvoiceDate  = DateOnly.FromDateTime(_invoice.InvoiceDate),
            BuyerName    = _invoice.Buyer ?? "",
            BrokerName   = _invoice.SelectedBroker?.Name,
            BrokerPct    = _invoice.BrokerPct,
            TermsDays    = _invoice.TermsDays,
            DocType      = _invoice.DocType ?? "",
            Status       = InvoiceStatus.DRAFT,
            AmountTotal  = _invoice.TotalAmount,
            CaratsSold   = _invoice.TotalCarats,
            Outstanding  = _invoice.TotalAmount,
            BlendedRate  = _invoice.BlendedRate,
        };

        var rows = lines.Select(l => new VSalesLine
        {
            GradeCode     = l.Grade?.ShortName ?? "",
            SizeCode      = l.Size?.Code ?? "",
            GrossWeightCt = l.GrossWeightCt,
            SelectionCt   = l.SelectionCt,
            RejectionCt   = l.RejectionCt,
            PricePerCt    = l.PricePerCt,
            ExRate        = l.ExRate,
            Less1Pct      = l.Less1Pct,
            Less2Pct      = l.Less2Pct,
            BrokerPct     = _invoice.BrokerPct,
            Amount        = l.Amount,
            Remark        = l.Remark,
        }).ToList();

        Say(Reports.PrintInvoice(memo, rows) ?? "", ok: true);
    }

    private async void SaveDraft_Click(object sender, RoutedEventArgs e)
    {
        // Saving a correction IS the correction. SaveDraftAsync filters its UPDATE on
        // status = DRAFT, so letting this through would answer "no longer an editable draft" for
        // an invoice the screen has deliberately opened for correction.
        if (CorrectingPosted)
        {
            if (await SaveCorrectionAsync()) NewInvoice_Click(sender, e);
            return;
        }

        using (Busy(SaveDraft, "Saving…", AddLineButton, SaveDraft, MoveToStock, Post))
            await SaveDraftAsync();
    }

    /// <summary>
    /// Disables the whole action row for the length of an operation and says what is happening on
    /// the button that started it. The `_saving` flag already stopped a double-click from booking
    /// two invoices — but silently: the buttons stayed lit and the second click just vanished, so
    /// a slow network looked like a dead app. This is the visible half of that guard.
    /// </summary>
    /// <param name="button">
    /// Whose caption changes. COUNTED, not simply set: two operations can own the same button at
    /// once and the outer one does not finish last.
    ///
    /// The header Refresh does exactly that. It reloads the catalogue and then the current page,
    /// and the page load is an async void that outlives it: the outer scope restored "Refresh",
    /// the page's own scope then restored what IT had captured on entry -- "Refreshing..." -- and
    /// the button sat there saying it was still working when nothing was. Only the outermost
    /// scope may put a caption back, and the caption it puts back is the one nobody had touched.
    /// </param>
    private IDisposable Busy(Button button, string label, params Button[] alsoDisable)
    {
        var row = alsoDisable.Length == 0 ? [button] : alsoDisable;

        _busy.Claim(button, button.Content);
        foreach (var b in row) _busy.Disable(b);

        foreach (var b in row) b.IsEnabled = false;
        button.Content = label;
        Mouse.OverrideCursor = Cursors.Wait;
        BeginBusy();                      // writes get the same bar as reads

        return new Scope(() =>
        {
            // MARSHALLED, for the same reason Say is. Every caller here is the using-block of an
            // `async void` handler, and if the await inside it resumed anywhere but the UI thread
            // this runs there too -- where Mouse.OverrideCursor throws "the calling thread must be
            // STA" from a finally block, turning a handled failure into an unhandled one that ends
            // the process.
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(Release); return; }
            Release();

            void Release()
            {
                Mouse.OverrideCursor = null;

                // Only the last holder puts a caption back, and it puts back the one the FIRST
                // holder found. Whoever finishes first leaves the button saying what is still
                // happening.
                if (_busy.Release(button, out object? original)) button.Content = original;

                foreach (var b in row)
                    if (_busy.Enable(b)) b.IsEnabled = true;

                EndBusy();
            }
        });
    }

    /// Who is holding which button, while operations overlap on it. See BusyLatch.
    private readonly BusyLatch _busy = new();

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
        FieldError.ClearAll();

        // The same list Confirm sale uses, shown the same way: everything wrong at once, in a
        // dialog, with the caret sent to the first of them afterwards.
        if (!Complete("This memo cannot be saved")) return false;
        if (_invoice.BuyerId is not { } buyerId) { Field(BuyerPicker, "Pick a buyer from the list"); return false; }
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
        Say($"Saved · {_invoice.RealLines.Count} line(s)", ok: true);
        return true;
    }

    /// <summary>
    /// Shows the message where it belongs and puts the caret there.
    ///
    /// A header problem names its own field, so it is written under that control. A LINE problem
    /// cannot be: the offending value is a cell in a grid that scrolls, and a message pinned under
    /// the grid would point at nothing in particular. Those rows already colour themselves and
    /// carry the text on their tooltip, so the grid is scrolled to the offender and the sentence
    /// stays in the status bar, where it is at least next to the row it is about.
    /// </summary>
    private void FocusFirstProblem(string error)
    {
        if (error.StartsWith("Buyer")) { Field(BuyerPicker, error); return; }
        if (error.StartsWith("Terms")) { Field(TermsBox, error); return; }
        if (error.StartsWith("Broker %")) { Field(BrokerPctBox, error); return; }

        if (_invoice.RealLines.FirstOrDefault(l => l.Error is not null) is { } bad)
        {
            ShowLineError(bad, error);
            return;
        }

        // "needs at least one line" and anything else that is about the invoice rather than about
        // one cell. There is no field to pin it to, so the bar is the honest place for it.
        Say(error);

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
    /// Catalogue.LoadAsync, with the entry screen's selections carried across it. Same reasoning as
    /// LoadPartiesAsync above, for the paths that reload the catalogue alone.
    /// </summary>
    private async Task ReloadCatalogueAsync()
    {
        _invoice.RememberCatalogue();
        await Catalogue.LoadAsync();
        _invoice.CatalogueChanged();
    }

    private async void ReloadCatalogue_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        button.IsEnabled = false;
        try
        {
            await ReloadCatalogueAsync();
            Say(Catalogue.Grades.Count > 0
                ? $"Catalogue loaded · {Catalogue.Grades.Count} grades"
                : "Still no grades — the database returned an empty list", ok: Catalogue.Grades.Count > 0);
        }
        catch (Exception ex) { Say(ex.Message); }
        finally { button.IsEnabled = true; }
    }

    /// <summary>
    /// True while the entry screen is holding a POSTED invoice opened for correction.
    ///
    /// A draft on this screen is unfinished work; a posted invoice on it is a document the office
    /// has already acted on. The buttons say which, and Post routes to a different call entirely.
    /// </summary>
    private bool CorrectingPosted =>
        _invoice.InvoiceId is not null && _invoice.Status == InvoiceStatus.POSTED;

    /// <summary>
    /// Saves a correction to a POSTED invoice: 0040 gives the stock back, replaces the lines,
    /// re-checks the negative-stock policy against the new figures and writes the movements
    /// again, all in one transaction. A refusal anywhere in that leaves the invoice and its stock
    /// exactly as they were.
    ///
    /// Not SaveDraftAsync + PostAsync. Two calls would mean a window in which the carats had been
    /// returned but the corrected lines had not landed, and a crash in the gap would leave stock
    /// that no invoice accounts for.
    /// </summary>
    private async Task<bool> SaveCorrectionAsync()
    {
        if (_invoice.Validate() is { } error) { FocusFirstProblem(error); return false; }
        if (_invoice.BuyerId is not { } buyerId) { Field(BuyerPicker, "Pick a buyer from the list"); return false; }
        if (_invoice.InvoiceId is not { } id) return false;
        if (Catalogue.BaseCurrencyId == 0) { Say("No INR row in the currency table — an invoice cannot be priced without it"); return false; }

        // Asked before anything is sent. The database requires it too, but a refusal after the
        // round trip would be a worse way to learn it.
        var answer = AppFormDialog.Show(this,
            "Update this invoice",
            $"Why is {_invoice.Buyer}'s invoice being updated?",
            "The reason is stored on every stock movement this update writes, so the ledger "
            + "says why the figures moved.",
            [new FormFieldSpec("Reason", "")],
            v => string.IsNullOrWhiteSpace(v[0]) ? "A reason is required." : null,
            "Save the update");

        if (answer is null) { Say("Update cancelled — nothing was changed"); return false; }

        var draft = new DraftInvoice(
            id, _invoice.ClientRef, DateOnly.FromDateTime(_invoice.InvoiceDate),
            buyerId, _invoice.BrokerId, _invoice.BrokerPct, _invoice.TermsDays, _invoice.DocType,
            Catalogue.BaseCurrencyId,
            _invoice.RealLines.Select(l => new DraftLine(
                l.Grade!.GradeId, l.Size!.SizeId, l.GrossWeightCt, l.SelectionCt,
                l.PricePerCt, l.ExRate, l.Less1Pct, l.Less2Pct, l.Remark)).ToList());

        WriteResult result;
        try
        {
            _saving = true;
            using (Busy(Post, "Updating…", AddLineButton, SaveDraft, MoveToStock, Post))
                result = await Repo.EditPostedAsync(draft, answer[0]);
        }
        finally { _saving = false; }

        if (!result.Ok)
        {
            // The database's own sentence, which names the buckets when the refusal is about stock.
            AppDialog.Refused(this,
                title: "Update refused",
                headline: "The invoice and its stock are exactly as they were",
                subhead: _invoice.Buyer,
                facts: [],
                listTitle: "What the database said",
                bullets: [Friendly.Message(result.Failure ?? "The update was refused.")],
                note: "Nothing was written. The whole update is one transaction, so the stock "
                    + "it would have returned was returned only inside it.");
            Say(result.Failure ?? "Update refused");
            return false;
        }

        Say("Update saved · stock rewritten to match", ok: true);
        return true;
    }

    /// <summary>
    /// What the confirmation shows: the figures, and one line per parcel.
    ///
    /// Pure and static so the summary can be checked without a dialog on screen. The numbers here
    /// are the ones the carats will move by, so they are worth a test of their own -- a summary
    /// that agrees with the screen but not with what posts is worse than no summary.
    ///
    /// GROSS is the headline, not selection. The whole parcel leaves the bucket: what is sold and
    /// what is rejected both come out of the stock they were counted in, and post_invoice checks
    /// the balance against gross for exactly that reason. A summary leading with "2.00 ct sold"
    /// would understate what the desk is about to give up.
    /// </summary>
    public static (List<(string Label, string Value)> Facts, List<string> Lines) SaleSummary(InvoiceEntry invoice)
    {
        var lines = invoice.RealLines;
        decimal gross = lines.Sum(l => l.GrossWeightCt);
        decimal sold = lines.Sum(l => l.SelectionCt);
        decimal rejected = lines.Sum(l => l.RejectionCt);

        var facts = new List<(string, string)>
        {
            ("Buyer", invoice.Buyer ?? "—"),
            ("Leaving stock", $"{gross:N2} ct"),
            ("of which sold", $"{sold:N2} ct"),
            ("of which rejected", $"{rejected:N2} ct"),
            ("Amount", Money.Short(invoice.TotalAmount)),
            ("Due", invoice.DueDate.ToString("dd MMM yyyy")),
        };

        var bullets = lines.Select(l =>
            $"{l.Grade?.ShortName ?? "?"} × {l.Size?.Code ?? "?"}  —  {l.GrossWeightCt:N2} ct out"
            + $"  ({l.SelectionCt:N2} sold, {l.RejectionCt:N2} rejected)"
            + $"  @ {l.PricePerCt:N2}").ToList();

        return (facts, bullets);
    }

    /// <summary>
    /// The sign-off before any carat moves. Cancel here writes nothing at all -- not a draft, not
    /// a movement -- because it is asked before SaveDraftAsync runs.
    /// </summary>
    private bool ConfirmSale()
    {
        var (facts, bullets) = SaleSummary(_invoice);
        int n = bullets.Count;

        return AppDialog.Confirm(this,
            title: "Confirm this sale",
            headline: $"{_invoice.RealLines.Sum(l => l.GrossWeightCt):N2} ct will leave stock",
            subhead: _invoice.Buyer,
            facts: facts,
            emphasis: "The WHOLE parcel leaves the bucket it was counted in — what is sold and what "
                    + "is rejected both. Nothing moves until you confirm, and the stock is taken in "
                    + "the same transaction that posts the invoice.",
            listTitle: n == 1 ? "The line" : $"The {n} lines",
            bullets: bullets,
            primaryText: "Confirm & move to stock",
            secondaryText: "Cancel");
    }

    /// <summary>
    /// The gate in front of the summary. True when the invoice can be confirmed; otherwise it says
    /// so and NOTHING is written -- this runs before SaveDraftAsync, so a refusal costs no draft,
    /// no movement and no invoice number.
    ///
    /// A dialog rather than the status bar alone. The bar is a single line at the very bottom edge
    /// of the window, a long way from the button that was just pressed, and it was the only thing
    /// answering a click on Confirm sale that could not go through -- so the click read as having
    /// done nothing at all. The bar and the field marker still happen underneath, because knowing
    /// WHERE the first problem is matters once the dialog closes.
    /// </summary>
    private bool ReadyToConfirm() => Complete("This sale cannot be confirmed");

    /// <summary>
    /// One gate, two doors. Save memo and Confirm sale enforce the same rules and report them the
    /// same way -- the only difference between the two is what happens after they pass.
    /// </summary>
    private bool Complete(string title)
    {
        var problems = _invoice.Problems();
        if (problems.Count == 0) return true;

        // UNDER THE FIELDS, not in a dialog. A modal listing "Buyer is required" states the fault
        // somewhere the fault is not: it covers the form, and the desk has to read it, remember it,
        // dismiss it, and then go hunting for which box it meant. Written under the box instead,
        // the message and the thing to fix are the same place, and nothing has to be memorised.
        //
        // EVERY problem at once, one per field, which is what the dialog was right about: being
        // told "Buyer is required", fixing it, and then being told about line 2 is two refusals for
        // one click. The dialog's other promise -- that nothing was saved -- goes to the status bar,
        // since it is about the press rather than about any one box.
        FieldError.ClearAll();
        foreach (var problem in problems) MarkProblem(problem);

        Say($"{title} — nothing has been saved and no stock has moved");

        // The caret lands on the first one, so the keyboard is already where the work is.
        FocusFirstProblem(problems[0]);
        return false;
    }

    /// <summary>
    /// Writes ONE problem under the field it is about, without moving the caret.
    ///
    /// Split from FocusFirstProblem, which does the same matching and then also focuses and scrolls.
    /// Doing that for every problem would leave the caret wherever the LAST one happened to be;
    /// the two jobs are shown-all and focus-first, and only the second one moves anything.
    /// </summary>
    private void MarkProblem(string problem)
    {
        if (problem.StartsWith("Buyer")) { FieldError.Show(BuyerPicker, problem); return; }
        if (problem.StartsWith("Terms")) { FieldError.Show(TermsBox, problem); return; }
        if (problem.StartsWith("Broker %")) { FieldError.Show(BrokerPctBox, problem); return; }

        // A line problem names its row, and the row colours itself and carries the text on its
        // tooltip already. FocusFirstProblem pins the first of them to its actual cell.
    }

    /// The grade x size this invoice draws on, remembered across the reset that follows a post.
    private List<(string Grade, string Size)> _lastSaleBuckets = [];

    /// What a "Move to stock" press asked to see. Consumed by the Stock filter rebuild, once.
    private string? _pendingStockGrade, _pendingStockSize;

    /// <summary>
    /// PUTS THE CARATS BACK. This invoice's lines have been holding stock since they were typed
    /// (0043); this returns every one of those holds and then shows the buckets they went back to.
    ///
    /// It cannot return more than was taken. release_entry deletes only the rows this entry holds,
    /// and each holds exactly what its line last said -- no figure is sent for the database to get
    /// wrong. Pressing it twice returns the carats once and then nothing.
    ///
    /// It does NOT touch a confirmed sale. Once Confirm sale has run, the holds are gone and the
    /// carats are out on SALE and REJECTION movements instead; reversing one of those is Cancel
    /// invoice's job, and it writes its own reasoned entries on the ledger.
    ///
    /// One grade or one size only for the filter. A sale spanning four buckets cannot be expressed
    /// by two combo boxes, and guessing one of the four would be worse than showing the lot.
    /// </summary>
    private async void MoveToStock_Click(object sender, RoutedEventArgs e)
    {
        Grid.CommitEdit();
        var buckets = _invoice.RealLines.Count > 0
            ? _invoice.RealLines.Where(l => l.Grade is not null && l.Size is not null)
                                .Select(l => (Grade: l.Grade!.Code, Size: l.Size!.Code)).ToList()
            : _lastSaleBuckets;

        // Nothing to look up. This button exists to open Stock ALREADY FILTERED to the buckets on
        // the invoice, so with no line and no remembered sale there is no filter to carry -- it
        // used to switch tabs anyway and land on the whole unfiltered position, which looks like
        // the button did something and did it wrong. Said out loud instead, on the screen the press
        // came from, and the tab does not move.
        if (buckets.Count == 0)
        {
            // Under the Grade cell of the row it is about, not in the status bar. The bar is for
            // things that belong to the screen rather than to a control -- a transport failure, a
            // saved memo -- and it sits at the very bottom of the window, a long way from the empty
            // row that caused this. The message and the box to fix are the same place now.
            const string fix = "Pick a grade and size on this line first — there is nothing to look up in stock yet";

            FieldError.ClearAll();
            if (_invoice.Lines.FirstOrDefault() is { } first) ShowLineError(first, fix);
            else Say(fix);                       // no row at all: nothing to pin it to
            return;
        }

        var grades = buckets.Select(b => b.Grade).Distinct().ToList();
        var sizes = buckets.Select(b => b.Size).Distinct().ToList();
        _pendingStockGrade = grades.Count == 1 ? grades[0] : null;
        _pendingStockSize = sizes.Count == 1 ? sizes[0] : null;

        // THE RETURN, before the navigation. Read the buckets first (above) because releasing the
        // holds is what makes the lines stop pointing at them.
        decimal returned;
        using (Busy(MoveToStock, "Returning\u2026", AddLineButton, SaveDraft, MoveToStock, Post))
            returned = await DropEntryAsync();

        // The rows go with the carats. Leaving them on screen after the stock has gone back read
        // as an invoice still holding something, and the next thing typed would have joined an
        // entry whose earlier lines had already been returned.
        //
        // Only when something actually came back. A refused release must leave the lines exactly
        // where they are, or the one screen that could retry it has just been cleared.
        if (returned > 0) ResetEntry();

        Say(returned <= 0
                ? "Nothing was being held \u2014 this invoice has moved no stock"
                : $"{returned:N4} ct returned to stock — the entry is cleared",
            ok: true);

        // Selecting the tab is what loads the page -- Tabs_SelectionChanged calls LoadStock_Click.
        // Setting the filters here instead would only have them rebuilt over by that load.
        foreach (var tab in Tabs.Items.OfType<TabItem>())
            if (tab.Header as string == "Stock") { tab.IsSelected = true; return; }
    }

    private async void Post_Click(object sender, RoutedEventArgs e)
    {
        // A posted invoice on this screen is a correction, not a post. One call, one transaction.
        if (CorrectingPosted)
        {
            if (await SaveCorrectionAsync()) NewInvoice_Click(sender, e);
            return;
        }

        // Validated BEFORE the summary, not after. A summary built from figures that cannot post
        // shows the desk a sale it is about to make and then refuses it, which reads as the app
        // changing its mind.
        if (!ReadyToConfirm()) return;

        // Asked before SaveDraftAsync, so Cancel writes nothing at all: no draft, no movement,
        // no invoice number consumed.
        if (!ConfirmSale()) { Say("Not confirmed — nothing was posted and no stock moved"); return; }

        // Post saves first, so the whole save-then-post round trip sits inside one busy scope —
        // otherwise the buttons came back to life in the gap between the two calls. That scope is
        // also what stops a second click landing while the first is in flight; post_invoice is
        // idempotent besides (it answers 'already_posted' rather than deducting twice), so a
        // duplicate movement is not expressible from here.
        using var busy = Busy(Post, "Posting…", AddLineButton, SaveDraft, MoveToStock, Post);

        if (!await SaveDraftAsync() || _invoice.InvoiceId is not { } id) return;

        var outcome = await Repo.PostAsync(id);

        // No override branch. Overselling is refused outright (negative_stock = block), and the
        // app must not offer a "post anyway" it cannot honour: under block the server raises rather
        // than returning needs_override, so a Yes here only produced a second refusal. If the
        // policy is ever loosened back to warn, the server will start returning needs_override
        // again and this will report it as a plain refusal — which is the safe direction to fail.
        if (!outcome.Ok)
        {
            // Under negative_stock = block there is no "post anyway": the server refused and no
            // answer here changes that. So this states what is short and stops -- offering a
            // choice that cannot be honoured is worse than offering none.
            if (outcome.Shortfalls.Count > 0)
            {
                string short_ = string.Join(Environment.NewLine, outcome.Shortfalls.Select(sf =>
                    $"{sf.GradeCode} × {sf.SizeCode} — {sf.BalanceCt:N4} ct on hand, {sf.NeededCt:N4} ct needed"));

                MessageBox.Show(this,
                    $"{outcome.Message}{Environment.NewLine}{Environment.NewLine}"
                    + $"{short_}{Environment.NewLine}{Environment.NewLine}"
                    + "Take the stock in, or reduce the invoice, then confirm again.",
                    "Not enough stock", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            Say(outcome.Message ?? "The sale was not confirmed");
            return;
        }

        // Captured before NewInvoice_Click clears the screen, so "Move to stock" can still show
        // the buckets the carats came out of.
        _lastSaleBuckets = _invoice.RealLines
            .Where(l => l.Grade is not null && l.Size is not null)
            .Select(l => (Grade: l.Grade!.Code, Size: l.Size!.Code)).Distinct().ToList();

        // The holds are gone: release_on_post (0043) deletes them in the same transaction that set
        // the invoice POSTED, so the carats are now out on SALE and REJECTION movements instead.
        // This side's cache has to agree, or a later edit would think it still held something and
        // skip the reservation it needs to make.
        _held.Clear();
        EntryStore.Clear();   // it is a sale now, not an entry to pick back up

        // It is a sale now, so it must leave the memo list and appear among the invoices. Both come
        // from the same list, so one reload settles both -- and a reload rather than a local edit,
        // so what is on screen is what the database actually holds.
        //
        // AWAITED, and before the dialog: the desk clicks OK and looks straight at a list that has
        // already caught up, rather than at one still a round trip behind.
        await LoadInvoicesAsync();

        // The invoice number is assigned at post, by post_invoice() — never by this app.
        Say($"Sale {outcome.InvoiceNo} confirmed · stock deducted", ok: true);
        MessageBox.Show(this, $"Recorded as sale {outcome.InvoiceNo}.", "Sale confirmed",
                        MessageBoxButton.OK, MessageBoxImage.Information);
        NewInvoice_Click(sender, e);
    }

    // ── Invoices, receipts, receivables ─────────────────────────────────────

    /// Every invoice the screen shows. Repo.InvoicesAsync is unchanged.
    private List<VInvoice> _invoices = [];

    private async void LoadInvoices_Click(object sender, RoutedEventArgs e) => await LoadInvoicesAsync();

    /// <summary>
    /// The same reload, awaitable. Confirm Sale has to KNOW both lists have come back before it
    /// announces the sale: until this returns, the memo it just posted is still sitting in the memo
    /// list on Sales entry and still missing from the invoices. Fired and forgotten, that gap was
    /// whatever the round trip took -- and if the read failed it never closed at all.
    /// </summary>
    private async Task LoadInvoicesAsync()
    {
        List<VInvoice>? rows;
        using (Busy(RefreshCatalogue, Loading))
            rows = await Read(Repo.InvoicesAsync);
        if (rows is null) return;

        _invoices = rows;
        // Sales, to agree with the list underneath. Counting _invoices counted the memos too,
        // so the chip said "5 invoices" over a table showing 4.
        InvoiceChip.Text = Plural(_invoices.Count(i => i.Status != InvoiceStatus.DRAFT), "invoice");

        // The buyer list can only offer buyers that actually appear.
        object? keep = InvoiceBuyer.SelectedItem;
        InvoiceBuyer.ItemsSource = new[] { AllBuyers }
            .Concat(_invoices.Select(i => i.BuyerName).Distinct().OrderBy(b => b, StringComparer.Ordinal))
            .ToList();
        InvoiceBuyer.SelectedItem = keep is string s && InvoiceBuyer.Items.Contains(s) ? s : AllBuyers;

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
            // BEFORE the status filter, not as one of its arms. A memo is work in progress, not a
            // sale, and this page lists sales -- so there is no setting that brings drafts back,
            // which is the point: "Everything" and a "Memo" entry both used to, and a memo shown
            // among the invoices reads as a sale that was never confirmed and never moved stock.
            // Unconfirmed memos are listed in full on Sales entry, which is where they are written.
            .Where(i => i.Status != InvoiceStatus.DRAFT)
            // Sale and Cancelled are read off the invoice's own status, the same value the STATUS
            // column prints. Paid, Pending and Overdue are where the money stands, which is what
            // InvoiceStateConverter derives. Answering both from State() made "Sale" impossible to
            // ask for at all: State never returns it, because a posted invoice is always described
            // by its payment instead.
            .Where(i => status switch
            {
                "" => true,
                "Sale" => i.Status == InvoiceStatus.POSTED,
                "Cancelled" => i.Status == InvoiceStatus.CANCELLED,
                _ => InvoiceStateConverter.State(i) == status,
            })
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
        // Cancelled invoices owe nothing. v_invoice still reports their full amount as
        // outstanding, which made this total disagree with Receivables and the Dashboard --
        // both of which already exclude them.
        decimal owed = shown.Sum(i => i.Outstanding);
        InvoiceSubtitle.Text = shown.Count == 0
            ? "Search, edit, post, receipts"
            : $"{Plural(shown.Count, "invoice")}{ImportedSplit(shown.Select(i => i.InvoiceNo))} "
              + $"· {Money.Short(owed)} outstanding";

        // The line above the table earns its space only when it has something the header cannot
        // say: that the filters matched nothing.
        InvoiceCount.Text = shown.Count == 0
            ? NoFilterMatch
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
    /// Fills the detail drawer from the selected row. Every summary figure is already on the
    /// VInvoice the grid is bound to, so the drawer opens without waiting; only the receipt history
    /// is fetched, and it is appended after the summary is already showing.
    /// </summary>
    private async void InvoiceRow_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (InvoiceDetailCard is null) return;

        bool open = InvoiceGrid.SelectedItem is VInvoice;
        InvoiceDetailCard.DataContext = InvoiceGrid.SelectedItem;
        InvoiceDetailCard.Visibility = open ? Visibility.Visible : Visibility.Collapsed;

        // Editing is a draft-only action, so the button is lit only for a draft. A posted invoice
        // has already moved stock and reopening it would let the same carats go out twice.
        if (EditDraftButton is not null)
        {
            var sel = InvoiceGrid.SelectedItem as VInvoice;
            bool draft = sel is { Status: InvoiceStatus.DRAFT };

            // Any POSTED invoice can be corrected, imported ones included (0040, 0042). An
            // imported invoice carries no stock movements of its own -- its carats are already
            // out of the imported opening balance -- so the database moves only the DIFFERENCE
            // its correction makes, as a signed ADJUST. That is what makes this safe to enable;
            // before 0042 it would have deducted every carat on the invoice a second time.
            bool imported = Repo.IsImported(sel?.InvoiceNo);
            bool posted = sel is { Status: InvoiceStatus.POSTED };

            EditDraftButton.IsEnabled = draft || posted;
            // Off DRAFT, not off "is it enabled". posted is already false for an imported
            // invoice -- so keying the caption off it called MIG-2, a SALE, an "Edit memo",
            // which is the one thing it is not. The status decides the word; whether the
            // button is lit is a separate question the tooltip answers.
            EditDraftButton.Content = draft ? "Edit memo" : "Update invoice";
            EditDraftButton.ToolTip = posted && imported
                ? "Open this imported sale to update it. Its carats are already in the opening "
                  + "balance, so saving moves only the difference the correction makes."
                : posted
                ? "Open this posted invoice to update it. Saving rewrites its stock movements in one step."
                : draft
                ? "Open this memo in Sales entry"
                // Cancelled, and anything else that is neither. Names the path that DOES work:
                // the old text said only what was refused, which is no help to somebody holding
                // an invoice with a wrong figure on it.
                : "Only a memo or a posted sale can be edited. A cancelled invoice has already "
                  + "had its stock returned, so there is nothing to correct.";
        }

        // Hand the layout to the size handler rather than setting the two column widths here.
        // It does the same thing AND re-shares the star column, which opening the drawer needs
        // just as much as a resize does: the split itself does not change width when the drawer
        // appears, so SizeChanged never fires, the star column kept whatever it had computed
        // during an intermediate pass, and the list sat 296px short of its own card.
        InvoiceSplit_SizeChanged(InvoiceSplit, null!);

        // A summary, not the invoice. Amount, what has been received against it, what is still
        // owed and when it was due — enough to decide whether to take a payment. Carats, rates,
        // broker splits and document details belong on the printed bill, which carries them all.
        if (InvoiceGrid.SelectedItem is not VInvoice inv) { InvoiceFacts.ItemsSource = null; return; }

        var facts = new List<object>
        {
            new { Label = "Amount", Value = Money.Short(inv.AmountTotal) },
            new { Label = "Received", Value = Money.Short(inv.Received) },
            new { Label = "Outstanding", Value = Money.Short(inv.Outstanding) },
            new { Label = "Due", Value = DueLabel(inv) },
        };
        InvoiceFacts.ItemsSource = facts;

        InvoiceSplit_SizeChanged(InvoiceSplit, null!);

        // The receipt history, appended once it arrives. "Received 50,000" says a total and nothing
        // else — not when, not how, not whether it was one payment or six — even though receipt
        // carries all of it. This is the only place that reads the table back.
        //
        // Awaited after the summary is already on screen, so picking a row still feels instant, and
        // guarded on the selection not having moved on: on a fast scroll through the list the reads
        // return out of order, and the last one to arrive would otherwise win.
        long selected = inv.InvoiceId;
        List<Receipt> receipts;
        try { receipts = await Repo.ReceiptsAsync(selected); }
        catch (Exception) { return; }                       // the summary stands; no error banner for a detail read

        if ((InvoiceGrid.SelectedItem as VInvoice)?.InvoiceId != selected) return;

        foreach (var r in receipts)
            facts.Add(new
            {
                Label = $"Receipt · {r.ReceiptDate.ToString(DayMonthYear)}"
                        + (string.IsNullOrWhiteSpace(r.Method) ? "" : $" · {r.Method}"),
                Value = Money.Short(r.Amount),
            });

        // ItemsSource is a plain List, so it has to be re-set rather than mutated in place.
        InvoiceFacts.ItemsSource = null;
        InvoiceFacts.ItemsSource = facts;
        InvoiceSplit_SizeChanged(InvoiceSplit, null!);
    }

    /// The due date, with the lateness appended only when there is any — a bare "0 days overdue"
    /// on a bill due today reads as a fault.
    private static string DueLabel(VInvoice inv)
    {
        string due = inv.DueDate.ToString(DayMonthYear);
        return inv.IsOverdue ? $"{due} · {inv.DaysOverdue:N0} days overdue" : due;
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
        if (InvoiceGrid.SelectedItem is not VInvoice invoice) { Field(InvoiceGrid, "Select an invoice first"); return; }
        // A cancelled invoice has had its stock returned and owes nothing. Cash booked against it
        // lands in the receipt ledger against a document that no longer exists.
        if (invoice.Status == InvoiceStatus.CANCELLED) { Say("That invoice is cancelled — nothing can be received against it"); return; }

        // A draft is not a debt yet: it carries no invoice number and its amount is still being
        // typed. Cash was being booked against one holding 0.00, which put the outstanding at -1.00
        // and left the receivables ledger owing money to a document that had never been issued.
        if (invoice.Status != InvoiceStatus.POSTED)
        { Say("That invoice is not posted yet — post it before recording a receipt"); return; }

        if (!decimal.TryParse(ReceiptAmount.Text, out decimal amount) || amount <= 0)
        { Field(ReceiptAmount, "Enter a receipt amount"); return; }

        // Nothing may be received beyond what is owed. Without this the outstanding goes negative,
        // and the Dashboard then disagrees with the Invoices page: dashboard_summary floors each
        // invoice at zero while the page sums the raw figure, so an over-receipt shows up as a
        // discrepancy between two screens rather than as the data error it is.
        if (amount > invoice.Outstanding)
        {
            Say(invoice.Outstanding <= 0
                ? $"{invoice.InvoiceNo} is already settled — nothing is outstanding"
                : $"That is more than is owed. {invoice.InvoiceNo} has {invoice.Outstanding:N2} outstanding");
            ReceiptAmount.Focus();
            ReceiptAmount.SelectAll();
            return;
        }

        string method = (ReceiptMethod.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "CASH";

        string? failure;
        using (Busy(RecordReceipt, "Recording…", RefreshCatalogue, RecordReceipt, CancelInvoiceButton))
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
        if (InvoiceGrid.SelectedItem is not VInvoice invoice) { Field(InvoiceGrid, "Select an invoice first"); return; }

        // Already cancelled: the RPC would refuse it anyway, and asking for a reason first makes
        // the user do work before being told no.
        if (invoice.Status == InvoiceStatus.CANCELLED) { Say("That invoice is already cancelled"); return; }

        // Name the invoice being reversed. "Cancel invoice" alone, with the selection off-screen on
        // a long list, is how the wrong one gets cancelled.
        //
        // AppDialog.Confirm, like every other destructive prompt in the app. This was the last
        // plain MessageBox left, and it guarded the one action that cannot be undone — so the least
        // legible prompt in the app sat in front of the most irreversible step. A facts table also
        // shows the figures the decision actually turns on: what is owed, and whether the buyer has
        // already paid against it.
        var facts = new List<(string, string)>
        {
            ("Invoice", invoice.InvoiceNo ?? "—"),
            ("Buyer", invoice.BuyerName ?? "—"),
            ("Amount", Money.Exact(invoice.AmountTotal)),
            ("Outstanding", Money.Exact(invoice.Outstanding)),
        };

        var bullets = new List<string>
        {
            "The invoice is marked CANCELLED. It stops counting towards Receivables and the Dashboard.",
            "The carats it sold are returned to stock as compensating entries — the original "
            + "movements stay in the ledger, which is append-only.",
            "Nothing can be received against it afterwards, and the invoice number is not reused.",
        };

        // Money already received is the one fact that turns a routine cancellation into a
        // conversation with the buyer, so it is said outright rather than left to be inferred from
        // Amount minus Outstanding.
        decimal received = invoice.AmountTotal - invoice.Outstanding;
        if (received > 0)
            bullets.Add($"{Money.Exact(received)} has already been received against this invoice. "
                      + "Cancelling does not refund it.");

        if (!AppDialog.Confirm(this,
                title: "Cancel invoice",
                headline: $"Cancel {invoice.InvoiceNo}?",
                subhead: invoice.BuyerName,
                facts: facts,
                emphasis: "This cannot be undone.",
                listTitle: "What happens",
                bullets: bullets,
                primaryText: "Cancel invoice",
                secondaryText: "Keep it"))
            return;

        // AppFormDialog, not Prompt.Ask: the raw prompt was an unstyled box with a single OK and no
        // way out but the title bar, and a blank answer closed it, dropped the cancellation and
        // reported the refusal in the status bar — behind a dialog that had already gone. This one
        // validates in place and stays open, so the reason is typed once.
        //
        // The RPC rejects a blank reason, so the same rule is enforced here rather than round-
        // tripping to be told no.
        var answers = AppFormDialog.Show(this,
            title: "Cancel invoice",
            headline: $"Why is {invoice.InvoiceNo} being cancelled?",
            subhead: "Recorded against the cancellation and shown in the audit trail. "
                   + "Write what someone reading this in a year would need to know.",
            fields: [new FormFieldSpec("Reason", MaxLength: 200)],
            validate: v => string.IsNullOrWhiteSpace(v[0])
                ? "A cancellation reason is required."
                : null,
            primaryText: "Cancel invoice");

        // Null means the user backed out of the form, which is not a refusal to explain — it is a
        // decision not to cancel. Saying "a reason is required" there would be scolding them for
        // changing their mind.
        if (answers is null) return;

        string reason = answers[0];

        string? failure;
        using (Busy(CancelInvoiceButton, "Cancelling…", RefreshCatalogue, RecordReceipt, CancelInvoiceButton))
        {
            failure = await Repo.CancelAsync(invoice.InvoiceId, reason);
        }
        Say(failure ?? $"Cancelled {invoice.InvoiceNo} · stock returned", ok: failure is null);
        LoadInvoices_Click(sender, e);
    }

    private async void LoadReceivables_Click(object sender, RoutedEventArgs e)
    {
        List<VReceivablesAgeing>? rows;
        using (Busy(RefreshCatalogue, Loading, RefreshCatalogue, ReceivablesExport))
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
        ReceivablesBuyer.ItemsSource = new[] { AllBuyers }
            .Concat(rows.Select(r => r.BuyerName).Distinct().OrderBy(b => b, StringComparer.Ordinal)).ToList();
        ReceivablesBuyer.SelectedItem =
            keepBuyer is string kn && ReceivablesBuyer.Items.Contains(kn) ? kn : AllBuyers;

        ApplyReceivablesFilter();
    }

    /// Every receivable the screen shows. Repo.ReceivablesAsync is unchanged.
    private List<VReceivablesAgeing> _receivables = [];

    /// <summary>
    /// Ages in the order a collections person reads them. Sorting the bucket labels as text happens
    /// to work for these by accident — "100+" would land before "31-60", and "not due" before both.
    ///
    /// The band names come from v_receivables_ageing and are the same five Android and the server
    /// agree on (CALC-AGE). Keep this in step with DiamondCalc.Calc.AgeBucket.
    /// </summary>
    private static int Age(string bucket) => bucket switch
    {
        "not due" => 0, "1-30" => 1, "31-60" => 2, "61-90" => 3, "90+" => 4, _ => 5,
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

            // The caption that used to split "not yet due" out of this tile is gone: the view now
            // has a band for it, so the figure is separated where it belongs instead of being
            // annotated after the fact. Keeping both would report the same money twice.
        }

        Tile("not due", RecKpiNotDue, RecKpiNotDueCount);
        Tile("1-30", RecKpiFresh, RecKpiFreshCount);
        Tile("31-60", RecKpi3060, RecKpi3060Count);
        Tile("61-90", RecKpi6190, RecKpi6190Count);
        Tile("90+", RecKpi90, RecKpi90Count);

        ReceivablesCount.Text = shown.Count == 0
            ? NoFilterMatch
            : $"{Plural(shown.Count, "invoice")}{ImportedSplit(shown.Select(r => r.InvoiceNo))} · "
              + $"{Money.Short(shown.Sum(r => r.Outstanding))} outstanding";
    }

    /// <summary>
    /// " · 1,438 imported · 5 entered here", or "" when the list is all one kind.
    ///
    /// A single total blends two populations that are compared against different things: the
    /// import dialog reports 1,438, the page said 1,443, and nothing on screen accounted for the
    /// difference. The same misreading as the stock page's 62 against 63 — a count that is true of
    /// one scope, read as the answer for another.
    ///
    /// Shown only when the list actually holds both. On a book with no import, or before any
    /// invoice has been typed, the split is noise.
    /// </summary>
    private static string ImportedSplit(IEnumerable<string?> invoiceNumbers)
    {
        var numbers = invoiceNumbers.ToList();
        int imported = numbers.Count(Repo.IsImported);
        int entered = numbers.Count - imported;

        return imported == 0 || entered == 0
            ? ""
            : $" · {imported:N0} imported · {entered:N0} entered here";
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
                    new { Label = "Due", Value = row.DueDate.ToString(DayMonthYear) },
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

    /// Adds a party this invoice needs but the picker no longer offers, and hands it straight back.
    private static PartyRef Retired(ObservableCollection<PartyRef> into, PartyRef party)
    {
        into.Add(party);
        return party;
    }

    /// <summary>
    /// Rebuild an entry screen from a saved draft.
    ///
    /// Pure and static so it can be tested without a database: the two views in, an InvoiceEntry
    /// out, and every line the catalogue cannot account for named in <paramref name="unresolved"/>
    /// rather than quietly dropped. A draft that comes back one line short and posts anyway is a
    /// parcel sold that nobody billed for.
    ///
    /// ORDER IS NOT COSMETIC. SelectedBuyer fills in the party's default terms when TermsDays is
    /// still 0, and SelectedBroker does the same for BrokerPct — helpful when a person picks a
    /// party, wrong when a saved invoice is being restored. So the parties go on first and the
    /// saved figures overwrite them afterwards. Grade before Size for the same kind of reason:
    /// setting Grade clears a Size that grade does not trade in.
    /// </summary>
    public static InvoiceEntry BuildDraft(
        VInvoice inv, IReadOnlyList<VSalesLine> lines,
        IEnumerable<PartyRef> buyers, IEnumerable<PartyRef> brokers,
        IReadOnlyList<Grade> grades, IReadOnlyList<SizeBucket> sizes,
        out List<string> unresolved)
    {
        var entry = new InvoiceEntry { InvoiceId = inv.InvoiceId, Status = inv.Status };

        foreach (var b in buyers) entry.Buyers.Add(b);
        foreach (var b in brokers) entry.Brokers.Add(b);

        entry.InvoiceDate = inv.InvoiceDate.ToDateTime(TimeOnly.MinValue);
        // Before the assignment, not after: the picker must already hold the value or the binding
        // nulls it on the way in.
        //
        // A blank falls back to the catalogue's first type rather than staying blank. An empty
        // picker is not a neutral display: sales_invoice.doc_type is written back on save, so a
        // box showing nothing would store nothing, and an invoice that merely arrived without a
        // type would lose the chance of having one. BILL is the same default the importer itself
        // applies (0022: coalesce(doc_type, 'BILL')), so this agrees with how the row was made.
        entry.KeepDocType(inv.DocType);
        entry.DocType = string.IsNullOrWhiteSpace(inv.DocType)
                      ? entry.DocTypes.FirstOrDefault()
                      : inv.DocType;

        // If the party has been deactivated since the draft was written it is not in the picker's
        // list -- correctly, nothing NEW may be billed to it. But this invoice already was, and a
        // buyer that quietly empties itself on reopening is a draft that posts to nobody. Put it
        // back on this invoice alone, flagged so New does not inherit it.
        entry.SelectedBuyer = entry.Buyers.FirstOrDefault(p => p.Id == inv.BuyerId)
                              ?? Retired(entry.Buyers, new PartyRef(inv.BuyerId, inv.BuyerName,
                                                                    inv.TermsDays, null, Active: false));
        entry.SelectedBroker = inv.BrokerId is { } brokerId
            ? entry.Brokers.FirstOrDefault(p => p.Id == brokerId)
              ?? Retired(entry.Brokers, new PartyRef(brokerId, inv.BrokerName ?? "(inactive broker)",
                                                     null, inv.BrokerPct, Active: false))
            : null;

        // After the parties, so what was saved wins over what they default to.
        entry.TermsDays = inv.TermsDays;
        entry.BrokerPct = inv.BrokerPct;

        // The constructor seeds one blank line for typing into. A restored invoice brings its own.
        entry.Lines.Clear();

        unresolved = [];
        foreach (var l in lines)
        {
            var grade = grades.FirstOrDefault(g => g.GradeId == l.GradeId);
            var size = sizes.FirstOrDefault(s => s.SizeId == l.SizeId);

            // Retired or deleted since the draft was written. Named, never guessed at.
            if (grade is null || size is null)
            {
                unresolved.Add($"{l.GradeCode} × {l.SizeCode}");
                continue;
            }

            entry.Lines.Add(new SaleLine
            {
                Grade = grade,
                Size = size,
                GrossWeightCt = l.GrossWeightCt,
                SelectionCt = l.SelectionCt,
                PricePerCt = l.PricePerCt,
                ExRate = l.ExRate == 0 ? 1m : l.ExRate,
                Less1Pct = l.Less1Pct,
                Less2Pct = l.Less2Pct,
                Remark = l.Remark,
            });
        }

        // Never leave the grid with no row at all: the screen has no way to start one.
        if (entry.Lines.Count == 0) entry.Lines.Add(new SaleLine());

        return entry;
    }

    /// <summary>
    /// Open the selected DRAFT back on the entry screen, ready to be posted.
    ///
    /// Only a draft. A POSTED invoice has already moved stock, and reopening one to post it again
    /// would deduct the same carats twice. The database refuses it independently — SaveDraftAsync
    /// filters its UPDATE on status = DRAFT and treats zero rows as the refusal — so this is the
    /// second lock rather than the only one.
    /// </summary>
    /// <summary>
    /// Update a confirmed sale: load it, show it in its own modal pre-filled, and on Save hand the
    /// corrected figures to 0040 in one transaction.
    ///
    /// Nothing is written unless Save is pressed, and a refusal inside that transaction rolls the
    /// whole of it back -- the invoice, its lines and every stock movement stay as they were.
    /// </summary>
    private async Task UpdateSaleAsync(VInvoice inv)
    {
        List<VSalesLine>? lines;
        using (Busy(EditDraftButton, "Opening…", EditDraftButton, PrintBill, RecordReceipt, CancelInvoiceButton))
            lines = await Read(() => Repo.LinesAsync(inv.InvoiceId));
        if (lines is null) return;

        var entry = BuildDraft(inv, lines, _invoice.Buyers, _invoice.Brokers,
                               Catalogue.Grades, Catalogue.AllSizes, out var unresolved);

        // The same refusal opening a memo gets, for the same reason: correcting what DID resolve
        // would rewrite the invoice without the lines that did not, and silently drop them.
        if (unresolved.Count > 0)
        {
            AppDialog.Refused(this,
                title: "Sale cannot be updated",
                headline: $"{unresolved.Count} line(s) name a grade or sieve the catalogue no longer has",
                subhead: inv.BuyerName,
                facts: [],
                listTitle: "Which lines",
                bullets: unresolved,
                note: "Restore them on Master data, then update the sale again. Nothing was changed.");
            return;
        }

        var form = new UpdateInvoiceDialog(entry, inv.InvoiceNo ?? "this invoice", Resources) { Owner = this };
        if (form.ShowDialog() != true)
        {
            return;
        }

        entry.InvoiceId = inv.InvoiceId;

        if (entry.BuyerId is not { } buyerId) { Say("Pick a buyer"); return; }
        if (Catalogue.BaseCurrencyId == 0)
        {
            Say("No INR row in the currency table — an invoice cannot be priced without it");
            return;
        }

        var draft = new DraftInvoice(
            inv.InvoiceId, entry.ClientRef, DateOnly.FromDateTime(entry.InvoiceDate),
            buyerId, entry.BrokerId, entry.BrokerPct, entry.TermsDays, entry.DocType,
            Catalogue.BaseCurrencyId,
            entry.RealLines.Select(l => new DraftLine(
                l.Grade!.GradeId, l.Size!.SizeId, l.GrossWeightCt, l.SelectionCt,
                l.PricePerCt, l.ExRate, l.Less1Pct, l.Less2Pct, l.Remark)).ToList());

        WriteResult result;
        using (Busy(EditDraftButton, "Saving…", EditDraftButton, PrintBill, RecordReceipt, CancelInvoiceButton))
            result = await Repo.EditPostedAsync(draft, form.Reason);

        if (!result.Ok)
        {
            // The database's own sentence, which names the buckets when the refusal is about stock.
            AppDialog.Refused(this,
                title: "Update refused",
                headline: "The invoice and its stock are exactly as they were",
                subhead: inv.BuyerName,
                facts: [],
                listTitle: "What the database said",
                bullets: [Friendly.Message(result.Failure ?? "The update was refused.")],
                note: "Nothing was written. The whole update is one transaction, so the stock it "
                    + "would have returned was returned only inside it.");
            Say(result.Failure ?? "Update refused");
            return;
        }

        Say($"{inv.InvoiceNo} updated · stock adjusted to match", ok: true);
        LoadInvoices_Click(this, new RoutedEventArgs());
    }

    private async void EditDraft_Click(object sender, RoutedEventArgs e)
    {
        if (InvoiceGrid.SelectedItem is not VInvoice inv) { Field(InvoiceGrid, "Select an invoice first"); return; }

        if (inv.Status is not (InvoiceStatus.DRAFT or InvoiceStatus.POSTED))
        {
            AppDialog.Refused(this,
                title: "Not a memo",
                headline: $"{inv.InvoiceNo ?? "This invoice"} is {StatusWordConverter.Word(inv.Status)}, so it cannot be edited",
                subhead: inv.BuyerName,
                facts: [],
                listTitle: null,
                bullets: null,
                note: "This invoice has already taken its carats out of stock. Editing and confirming it "
                    + "again would take them out a second time. Nothing was opened.");
            return;
        }

        // A confirmed sale is corrected in its own modal, pre-filled, with Save and Cancel. It is
        // not the same act as opening a memo: its carats have already left stock, so saving means
        // giving them back and taking the new figures out again, and that belongs in a window that
        // says so rather than in the entry screen quietly changing under the user.
        if (inv.Status == InvoiceStatus.POSTED) { await UpdateSaleAsync(inv); return; }

        // The same question New asks, for the same reason: typed lines that were never saved are
        // gone with no way back. A draft already on screen has an InvoiceId and is safe to replace.
        if (_invoice.InvoiceId is null && _invoice.RealLines.Count > 0
            && MessageBox.Show(this,
                   $"The invoice on screen has {_invoice.RealLines.Count} line(s) that have not been saved.\n\n"
                   + $"Open the memo for {inv.BuyerName} and lose them?",
                   "Unsaved invoice", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        // Whatever the entry being replaced was holding goes back, exactly as Clear does. Without
        // this, typing a line and then opening a different memo left the carats spoken for by an
        // invoice no longer on any screen -- the same stranding, reached by a different door.
        //
        // Before the swap, because DropEntryAsync releases against _invoice.ClientRef and the memo
        // about to be opened carries a different one.
        if (_invoice.InvoiceId is null) await DropEntryAsync();

        await OpenInvoiceAsync(inv);
    }

    /// <summary>
    /// Loads an invoice into Sales entry. Shared by Edit memo and the memo picker, because they
    /// are one act reached two ways -- and a second copy of this would be a second place for the
    /// unresolved-line refusal to be forgotten.
    /// </summary>
    private async Task OpenInvoiceAsync(VInvoice inv)
    {
        List<VSalesLine>? lines;
        using (Busy(EditDraftButton, "Opening…", EditDraftButton, PrintBill, RecordReceipt, CancelInvoiceButton))
            lines = await Read(() => Repo.LinesAsync(inv.InvoiceId));
        if (lines is null) return;

        var entry = BuildDraft(inv, lines, _invoice.Buyers, _invoice.Brokers,
                               Catalogue.Grades, Catalogue.AllSizes, out var unresolved);

        // A draft that cannot be rebuilt in full is not opened at all. Posting what DID resolve
        // would bill for part of a parcel and leave the rest unexplained on nobody's screen.
        if (unresolved.Count > 0)
        {
            AppDialog.Refused(this,
                title: "Memo cannot be reopened",
                headline: $"{unresolved.Count} line(s) name a grade or sieve the catalogue no longer has",
                subhead: inv.BuyerName,
                facts: [],
                listTitle: "Which lines",
                bullets: unresolved,
                note: "Restore them on Master data, then open the memo again. Nothing was opened "
                    + "and the memo is untouched.");
            Say($"Memo not opened — {unresolved.Count} line(s) reference a missing grade or size");
            return;
        }

        var leaving = _invoice;
        _invoice = entry;
        WatchHolds(leaving, _invoice);
        DataContext = _invoice;
        TickCountChanged();

        Tabs.SelectedItem = SalesEntryTab;

        Say($"{(inv.Status == InvoiceStatus.DRAFT ? "Memo" : "Sale")} for {inv.BuyerName} opened · "
            + $"{_invoice.RealLines.Count} line(s)", ok: true);
    }

    private async void PrintInvoice_Click(object sender, RoutedEventArgs e)
    {
        if (InvoiceGrid.SelectedItem is not VInvoice invoice) { Field(InvoiceGrid, "Select an invoice first"); return; }

        var lines = await Read(() => Repo.LinesAsync(invoice.InvoiceId));
        if (lines is null) return;

        Say(Reports.PrintInvoice(invoice, lines) ?? "", ok: true);
    }

    // ── Stock ───────────────────────────────────────────────────────────────

    /// Every bucket, filtered or not. The summary totals this, never the filtered view — a company
    /// position that changes when you tick a display checkbox would be worse than useless.
    private List<VStockPosition> _stock = [];

    /// The grade filter's labels, and the code each one stands for. The combo shows the label; the
    /// filter still compares codes, which is what v_stock_position carries.
    private Dictionary<string, string> _stockGradeCodes = [];

    /// The same, for the sieve filter: "1/5" on screen, "0.2" in the comparison.
    private Dictionary<string, string> _stockSizeCodes = [];

    /// The mark the printed sheet uses — "1BB", "#", "OW". The grade is passed for the sake of a
    /// catalogue that may know a name this app does not, but the mark wins: the filter, the table's
    /// GRADE column and the paper on the desk all have to read the same.
    private static string GradeLabel(string code, Grade? grade) =>
        grade is not null ? grade.ShortName : GradeNames.Short(code);

    private async void LoadStock_Click(object sender, RoutedEventArgs e)
    {
        // The traded-buckets read that used to happen here went with the filter that needed it:
        // "Hide empty buckets" now means ticked, whatever a bucket's history (see ApplyStockFilter),
        // so the second round trip fetched a set nothing consulted.
        List<VStockPosition>? rows;
        List<VStockMovement>? rejections;
        List<VStockMovement>? imported;
        List<VStockMovement>? sold;
        using (Busy(RefreshCatalogue, Loading, RefreshCatalogue, StockExport, RunInvariants))
        {
            rows = await Read(Repo.StockAsync);
            // Inside the same busy scope: the breakdown card sits above the list and a second
            // round trip after the buttons came back would repaint it a moment later, which reads
            // as the figure correcting itself.
            rejections = await Read(Repo.RejectionsAsync);
            imported = await Read(Repo.ImportedStockAsync);
            sold = await Read(Repo.SalesAsync);
        }

        if (rows is null) return;

        _stock = rows;
        _rejections = rejections ?? [];
        _importedStock = imported ?? [];
        _sales = sold ?? [];

        StockChip.Text = Plural(rows.Count, "bucket");

        // Both lists offer only what actually came back.
        //
        // Labelled "No. 1 Clean (NO 1)", not "NO 1". Every other grade picker in the app shows the
        // display name, so a filter that showed only the code read as a different vocabulary — and
        // the code still has to be visible here, because it is what the table's GRADE column shows.
        // The label is what the combo displays; _stockGradeCodes maps it back for the filter, so
        // the comparison is still against the code and nothing about the filtering changed.
        object? keepGrade = StockGradeFilter.SelectedItem;
        var listed = rows.Select(r => r.GradeCode).Distinct()
            .Select(code => (Code: code, Grade: Catalogue.Grades.FirstOrDefault(g => g.Code == code)))
            .OrderBy(x => x.Grade?.SortOrder ?? int.MaxValue).ThenBy(x => x.Code, StringComparer.Ordinal)
            .ToList();

        // Built by hand rather than with ToDictionary, because the key is now a MARK and marks are
        // shorter than display names -- two catalogue rows could be edited into the same one, and
        // ToDictionary answers that with an exception on a screen that was only being refreshed.
        // The code settles it, and an entry reading "2 (NO 2)" is a catalogue to tidy, not a crash.
        _stockGradeCodes = [];
        foreach (var x in listed)
        {
            string label = GradeLabel(x.Code, x.Grade);
            _stockGradeCodes[_stockGradeCodes.ContainsKey(label) ? $"{label} ({x.Code})" : label] = x.Code;
        }

        // A "Move to stock" press asked for one bucket. Honoured once, then forgotten, so the next
        // visit to this page keeps whatever the user themselves last chose.
        if (_pendingStockGrade is { } wantGrade)
            keepGrade = _stockGradeCodes.FirstOrDefault(kv => kv.Value == wantGrade).Key ?? keepGrade;
        _pendingStockGrade = null;

        StockGradeFilter.ItemsSource = new[] { "All grades" }.Concat(_stockGradeCodes.Keys).ToList();
        StockGradeFilter.SelectedItem =
            keepGrade is string kg && StockGradeFilter.Items.Contains(kg) ? kg : "All grades";

        object? keepSize = StockSizeFilter.SelectedItem;

        // The CATALOGUE's sizes, in the catalogue's order -- not only the ones currently holding
        // stock. Built from the rows, a sieve everybody trades but that happens to be empty today
        // simply vanished from the filter, so there was no way to ask "what is in 1/4" and be told
        // "nothing". A size that holds nothing is an answer; a size missing from the list is a
        // question about whether the app knows it exists.
        //
        // Union rather than replace: a retired size can still hold stock, and a filter that cannot
        // reach its own rows would be worse than the fault being fixed.
        // Labelled the way the sheet writes them -- "1/5", not the 0.2 the catalogue stores -- and
        // mapped back to codes the same way the grade filter is, so the comparison below is still
        // against v_stock_position's own SizeCode and nothing about the filtering changed.
        _stockSizeCodes = [];
        foreach (string code in Catalogue.AllSizes.Select(z => z.Code)
                                    .Concat(rows.Select(r => r.SizeCode))
                                    .Distinct(StringComparer.Ordinal))
        {
            string label = SizeNames.Short(code);
            _stockSizeCodes[_stockSizeCodes.ContainsKey(label) ? $"{label} ({code})" : label] = code;
        }

        if (_pendingStockSize is { } wantSize)
            keepSize = _stockSizeCodes.FirstOrDefault(kv => kv.Value == wantSize).Key ?? keepSize;
        _pendingStockSize = null;

        StockSizeFilter.ItemsSource = new[] { "All sizes" }.Concat(_stockSizeCodes.Keys).ToList();
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

        string gradeLabel = StockGradeFilter.SelectedIndex <= 0 ? "" : StockGradeFilter.SelectedItem as string ?? "";
        string grade = StockGradeCode(gradeLabel);
        string sizeLabel = StockSizeFilter.SelectedIndex <= 0 ? "" : StockSizeFilter.SelectedItem as string ?? "";
        string size = sizeLabel.Length == 0 ? ""
            : _stockSizeCodes.TryGetValue(sizeLabel, out var sc) ? sc : sizeLabel;
        string term = StockSearch?.Text.Trim() ?? "";

        // Grade, size and search FIRST; "hide empty" last. The order used to be the other way
        // round, and it cost this screen its only honest answer: picking a grade that holds
        // nothing -- Unknown Grade, whose rows the sheet prints 0.00 against -- emptied the grid
        // and said "No bucket matches these filters", which is not true. The buckets match. They
        // are empty, and that is a different sentence. Counting them needs them filtered but not
        // yet hidden.
        var matched = _stock
            .Where(r => grade.Length == 0 || r.GradeCode == grade)
            .Where(r => size.Length == 0 || r.SizeCode == size)
            .Where(r => term.Length == 0
                        || r.GradeCode.Contains(term, StringComparison.OrdinalIgnoreCase)
                        // The mark as well as the code and the full name. All three name the same
                        // grade and any of them may be what the user has in front of them.
                        || GradeNames.Short(r.GradeCode).Contains(term, StringComparison.OrdinalIgnoreCase)
                        || (r.GradeName ?? "").Contains(term, StringComparison.OrdinalIgnoreCase)
                        || r.SizeCode.Contains(term, StringComparison.OrdinalIgnoreCase)
                        // "1/5" finds the bucket the catalogue calls 0.2, which is the only name
                        // for it anyone outside this database uses.
                        || SizeNames.Short(r.SizeCode).Contains(term, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Ticked means ticked: a bucket holding nothing is hidden, whatever its history. The rule
        // used to keep zero-balance buckets that had a ledger -- NO 1 BB against -2 sits at zero
        // because its only invoice was cancelled -- but a row badged "Empty" showing under a
        // ticked "Hide empty buckets" reads as a broken filter. Untick to get those buckets back.
        var show = HideEmptyBuckets.IsChecked == true
            ? matched.Where(r => r.BalanceCt != 0).ToList()
            : matched;

        // What the TICK took away, as against what the filters never matched. That difference is
        // the whole of the message below.
        int emptied = matched.Count - show.Count;

        if (StockSearchClear is not null)
            StockSearchClear.Visibility = string.IsNullOrEmpty(StockSearch?.Text)
                ? Visibility.Collapsed : Visibility.Visible;

        StockGrid.ItemsSource = show;
        ShowStockKpis(show);
        ShowStockBreakdown(grade, size, term, show);

        StockCount.Text = show.Count == 0
            ? NoFilterMatch
            : $"{Plural(show.Count, "bucket")} shown of {_stock.Count:N0}";

        bool filtered = grade.Length != 0 || size.Length != 0 || term.Length != 0;
        // The hint is the grid's EMPTY state, so the count only means anything when the grid is
        // empty. Passed as zero otherwise rather than left to a caller to remember.
        StockHint.Text = StockEmptyHint(filtered, show.Count == 0 ? emptied : 0);

        if (_stock.Count != 0 && show.Count != _stock.Count)
            Say($"Showing {show.Count} of {_stock.Count} buckets", ok: true);
    }

    /// <summary>
    /// The breakdown card: carats held, and carats rejected to date.
    ///
    /// The two are NOT parts of one whole. Held comes from v_stock_position; rejected is the sum
    /// of REJECTION rows in v_stock_movement, and those carats left stock when the rejection was
    /// recorded. Subtracting one from the other, or showing them as a split of the same bar, would
    /// deduct the same carats twice.
    ///
    /// Filtered by grade and size against the buckets currently on screen, so picking a grade
    /// narrows both figures together and the card never describes a set the list is not showing.
    /// </summary>
    private void ShowStockBreakdown(string grade, string size, string term,
                                    List<VStockPosition> show)
    {
        if (StockBreakdownTotal is null) return;
        bool filtered = grade.Length != 0 || size.Length != 0 || term.Length != 0;

        // Filtered by the SAME grade/size/search the list uses -- but not by which buckets survived
        // "hide empty". Matching against the displayed rows lost every rejection whose bucket has
        // since been emptied: NO 1 BB x -2 holds nothing today and 3.87 ct were rejected out of it,
        // so the card read 200.47 where the ledger says 204.34. A rejection does not stop having
        // happened because the bucket it came from is now empty.
        var rejected = _rejections
            .Where(m => grade.Length == 0 || m.GradeCode == grade)
            .Where(m => size.Length == 0 || m.SizeCode == size)
            .Where(m => term.Length == 0
                        || m.GradeCode.Contains(term, StringComparison.OrdinalIgnoreCase)
                        || m.SizeCode.Contains(term, StringComparison.OrdinalIgnoreCase))
            .ToList();

        StockBreakdownTotal.Text = $"{show.Sum(r => r.BalanceCt):N4} ct";

        // Says what the figure IS relative to the workbook, because that is the question the
        // office actually asks: their sheet reads 2,950.84 and the app reads 2,748.74, and without
        // a word here the two look like a disagreement rather than a position that has moved.
        // The number itself is unchanged -- this is the caption under it.
        // Both numbers, on one line, with the arithmetic between them. The office reads their
        // sheet's BALANCE STOCK and this screen's total; naming the relationship in words was not
        // enough, because the figure they wanted to reconcile against was not on screen at all.
        decimal held = show.Sum(r => r.BalanceCt);
        decimal atImport = _importedStock
            .Where(m => grade.Length == 0 || m.GradeCode == grade)
            .Where(m => size.Length == 0 || m.SizeCode == size)
            .Where(m => term.Length == 0
                        || m.GradeCode.Contains(term, StringComparison.OrdinalIgnoreCase)
                        || m.SizeCode.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Sum(m => m.WeightCt);

        // "Out since" counts MOVEMENTS, and reservations are not movements. Measured against the
        // available balance it read 26.0000 ct out where 21.0000 had actually left the ledger, and
        // the office reconciles this line against their own sheet -- so the five held carats made
        // the reconciliation fail against a figure nothing had shipped.
        //
        // The hold is named separately instead, which also closes the arithmetic on screen:
        // at import, less what moved, less what is spoken for, is the total above.
        decimal reserved = show.Sum(r => r.ReservedCt);
        decimal moved = held + reserved - atImport;

        StockBreakdownTotalNote.Text = atImport == 0m
            ? (filtered ? "Carats on hand · filtered" : "Carats on hand")
            : $"Workbook at import: {atImport:N4} ct · {Math.Abs(moved):N4} ct "
              + (moved < 0 ? "out since" : "in since")
              + (reserved > 0 ? $" · {reserved:N4} ct reserved" : "")
              + (filtered ? " · filtered" : "");

        decimal ct = rejected.Sum(m => m.WeightCt);
        StockBreakdownRejection.Text = $"{ct:N4} ct";
        StockBreakdownRejectionNote.Text = rejected.Count == 0
            ? "Nothing rejected from these buckets"
            : $"{Plural(rejected.Count, "rejection")} to date · already out of stock";

        // Where they came from, because a rejection off an invoice line and one entered by hand
        // are different acts and the split is the first thing anyone asks about.
        int onSales = rejected.Count(m => m.RefType == "sales_line");
        StockBreakdownScope.Text = rejected.Count == 0
            ? ""
            : $"{onSales:N0} on sales lines · {rejected.Count - onSales:N0} recorded by hand";
    }

    /// The combo shows "No. 1 Clean (NO 1)"; v_stock_position carries the code. Empty label means
    /// no grade filter. A label with no mapping falls back to itself rather than filtering to
    /// nothing, so a grade present only in the stock data still filters.
    private string StockGradeCode(string gradeLabel)
    {
        if (gradeLabel.Length == 0) return "";
        return _stockGradeCodes.TryGetValue(gradeLabel, out var code) ? code : gradeLabel;
    }

    /// The tiles report what is on screen, so they never describe a set the list is not showing.
    private void ShowStockKpis(List<VStockPosition> show)
    {
        // The tiles report what is on screen. That is right — but silently, the caption read
        // "across 3 buckets" whether that was the company position or one grade's slice of it,
        // while the header above kept totalling all 207. Two different numbers, both unlabelled.
        // Say which one this is.
        bool filtered = show.Count != _stock.Count;

        StockKpiCarats.Text = show.Sum(r => r.BalanceCt).ToString("N4");
        StockKpiCaratsNote.Text = filtered
            ? $"across {show.Count:N0} of {_stock.Count:N0} buckets · filtered"
            : $"across {Plural(show.Count, "bucket")}";
        StockKpiValue.Text = Money.Short(show.Sum(r => r.StockValue));
        StockKpiValueNote.Text = filtered ? "At average cost · filtered" : "At average cost";

        StockKpiActive.Text = show.Count(r => r.BalanceCt > 0).ToString("N0");
        StockKpiActiveNote.Text = "Holding a balance";

        int negative = show.Count(r => r.BalanceCt < 0);
        StockKpiNegative.Text = negative.ToString("N0");
        StockKpiNegativeNote.Text = negative == 0
            ? "Nothing to reconcile"
            : "Stock left that never arrived";
    }

    /// Which empty this is decides what to do about it, so the hint says which one it is.
    /// <param name="emptied">
    /// How many buckets DID match the filters and were then dropped for holding nothing. Any
    /// number above zero means the grid is empty because of the tick, not because of the filters
    /// -- and telling the office "no bucket matches" about a grade that plainly exists is how a
    /// screen loses its credibility.
    /// </param>
    private string StockEmptyHint(bool filtered, int emptied)
    {
        const string nothingLoaded = "No stock positions.\nPress Refresh, or record an intake first.";

        if (_stock.Count == 0) return nothingLoaded;

        if (emptied > 0)
            return $"{Plural(emptied, "bucket")} here, and every one of them is empty. "
                   + "Untick Hide empty buckets to see them.";

        if (filtered) return "No bucket matches these filters.\nPress Clear to see them all.";
        if (HideEmptyBuckets.IsChecked == true)
            return $"No bucket is holding a balance.\nAll {_stock.Count:N0} are empty — "
                   + "untick Hide empty buckets to list them.";
        return nothingLoaded;
    }

    /// "NO II × -6.5 · oldest intake 91 days". The age is appended only when the bucket has one.
    private static string BucketSubtitle(VStockPosition? row)
    {
        if (row is null) return "Select a bucket in the list";
        string bucket = $"{row.GradeCode} × {row.SizeCode}";
        return row.AgeDays is { } age ? $"{bucket} · oldest intake {age:N0} days" : bucket;
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
        MovementSubtitle.Text = BucketSubtitle(row);

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
        if (StockGrid.SelectedItem is not VStockPosition row) { Field(StockGrid, "Select a grade × size row"); return; }

        List<VStockMovement>? rows;
        using (Busy(ShowMovements, Loading, ShowMovements, RefreshCatalogue))
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
        using (Busy(RunInvariants, "Checking…", RunInvariants, RefreshCatalogue))
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
                : $"{Plural(broken.Count, "bucket")} do not reconcile",
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

    /// <summary>
    /// Who may do what. Reading is open to everyone signed in; the writes that move stock or money
    /// are not.
    ///
    /// Until now the only gate in the app was the Users tab, so any signed-in account could adjust
    /// a bucket by any weight with a free-text reason — the single most consequential write there
    /// is, because it can create or destroy carats outright. Intake, conversion and rejection sit
    /// with it. Cancelling an invoice reverses a posted document, and the two imports replace whole
    /// datasets, so both are owner-or-manager as well.
    ///
    /// Recording a receipt is left open: it books cash against an invoice that already exists, and
    /// the person taking payment is not always the person who may adjust stock.
    ///
    /// This is the UI half only. The database half is RLS, which is not in this repository —
    /// disabling a button stops the honest mistake, not a determined caller.
    /// </summary>
    private void ApplyRolePermissions()
    {
        bool mayWriteStock = Db.IsManagerOrOwner;

        foreach (var b in new[] { IntakeButton, ConvertButton, RejectButton, AdjustButton })
        {
            b.IsEnabled = mayWriteStock;
            if (!mayWriteStock)
                b.ToolTip = "Only a manager or owner may record stock movements.";
        }

        CancelInvoiceButton.IsEnabled = mayWriteStock;
        if (!mayWriteStock)
            CancelInvoiceButton.ToolTip = "Only a manager or owner may cancel an invoice.";

        // Replacing a whole dataset is administration, not day-to-day entry.
        // Creating and managing accounts is the owner's alone. The Edge Function checks this again
        // server-side -- this only keeps the button from offering what the server will refuse.
        NewUserButton.IsEnabled = Db.IsOwner;
        if (!Db.IsOwner) NewUserButton.ToolTip = "Only the owner may manage user accounts.";

        bool mayImport = Db.IsOwner;
        foreach (var b in new[] { ImportExcel, ImportStock })
        {
            b.IsEnabled = mayImport;
            if (!mayImport)
                b.ToolTip = "Only the owner may replace imported data.";
        }
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

    /// <summary>
    /// Reloads the catalogue the pickers are built from, and the bucket ledger if one is open.
    /// The session counters above are left alone deliberately — they report what this session
    /// recorded, and zeroing them on a refresh would erase the only record of it.
    /// </summary>
    private async void RefreshMovements_Click(object sender, RoutedEventArgs e)
    {
        await ReloadCatalogueAsync();

        if (LedgerGrade.SelectedItem is Grade && LedgerSize.SelectedItem is SizeBucket)
            LedgerLoad_Click(sender, e);

        Say("Catalogue reloaded", ok: true);
    }

    // What this session has posted. Counts, not totals: this page writes to the ledger, it does not
    // report on it, and a figure that looked like a stock total would be read as one.
    private int _opIntakes, _opConversions, _opRejections, _opAdjustments;

    private static void CountOp(TextBlock tile, ref int tally)
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
        if (grade is null || size is null) { Field(LedgerGrade, PickGradeSize); return; }

        List<VStockMovement>? rows;
        using (Busy(LedgerLoad, Loading))
            rows = await Read(() => Repo.MovementsAsync(grade.Code, size.Code));

        LedgerList.ItemsSource = rows;
        if (rows is null) return;                       // Read already reported the failure

        LedgerSubtitle.Text = rows.Count == 0
            ? $"{grade.Code} × {size.Code}"
            : $"{grade.Code} × {size.Code} · {Plural(rows.Count, "movement")}";

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
        if (grade is null || size is null) { Field(IntakeGrade, PickGradeSize); return; }
        if (!decimal.TryParse(IntakeWeight.Text, out decimal weight) || weight <= 0)
        { Field(IntakeWeight, "Weight must be positive"); return; }
        // rough_intake.price_per_ct is not nullable and feeds v_stock_position's avg_cost and
        // stock_value. A blank box parsed to 0 booked the parcel at zero value and dragged the
        // grade's average cost down with it, silently. Zero is fine — but it has to be typed.
        if (!decimal.TryParse(IntakePrice.Text, out decimal price) || price < 0)
        { Field(IntakePrice, "Enter the price per carat — it sets this parcel's cost basis"); return; }

        if (Bounds.TooLarge(weight, WeightField) is { } tooHeavy) { Say(tooHeavy); IntakeWeight.Focus(); return; }
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
        if (fromGrade is null || fromSize is null || toGrade is null || toSize is null)
        { Field(fromGrade is null || fromSize is null ? ConvFromGrade : ConvToGrade, "Pick both sides"); return; }

        // A conversion moves carats from one bucket to another. Both sides the same is not a
        // conversion — it books a movement out and a movement in for the same bucket, leaving the
        // balance where it started and two entries in the ledger explaining nothing.
        if (fromGrade.GradeId == toGrade.GradeId && fromSize.SizeId == toSize.SizeId)
        { Say("From and To are the same bucket — a conversion has to move carats somewhere else"); return; }

        if (!decimal.TryParse(ConvWeight.Text, out decimal weight) || weight <= 0)
        { Field(ConvWeight, "Weight must be positive"); return; }
        if (!TypedPrice(ConvPrice, out decimal? price))
        { Field(ConvPrice, "Price/ct must be a number, or left blank"); return; }

        if (Bounds.TooLarge(weight, WeightField) is { } tooHeavy) { Say(tooHeavy); ConvWeight.Focus(); return; }
        if (price is { } p && Bounds.TooLarge(p, "Price per carat") is { } tooDear) { Say(tooDear); ConvPrice.Focus(); return; }
        if (!ConfirmLarge(weight, price ?? 0)) return;

        // Busy disables the button for the round trip. Without it a second click posted a second
        // conversion, and convert_stock sends a fresh client_ref each call so nothing downstream
        // could tell the two apart.
        WriteResult result;
        using (Busy((Button)sender, "Converting…"))
            result = await Repo.ConvertAsync(fromGrade.GradeId, fromSize.SizeId,
                                             toGrade.GradeId, toSize.SizeId, weight, price);

        if (!result.Ok) { Say(result.Failure!); return; }

        ConvWeight.Text = "";
        ConvPrice.Text = "";
        CountOp(OpKpiConvert, ref _opConversions);
        RefreshLedgerIfShowing(fromGrade, fromSize);
        RefreshLedgerIfShowing(toGrade, toSize);
        Announce($"Converted {weight:N4} ct · total carats unchanged", result.Warning);
    }

    /// <summary>
    /// Confirms a stock write, or reports the warning the database sent back with it. A warning
    /// means the bucket has gone below zero under the WARN policy — the write happened, and saying
    /// so quietly in green would be worse than not saying it at all.
    /// </summary>
    private void Announce(string done, string? warning) =>
        Say(warning is null ? done : $"{done} — {warning}", ok: warning is null);

    private async void Reject_Click(object sender, RoutedEventArgs e)
    {
        // The row the user is still typing holds its weight in the cell editor, not on the object.
        // Without this the count below reads 0 and the "N were NOT saved" warning never appears —
        // which is the one thing this screen must not do.
        DispositionGrid.CommitEdit(DataGridEditingUnit.Row, true);

        var (grade, size) = Pick(RejGrade, RejSize);
        if (grade is null || size is null) { Field(RejGrade, PickGradeSize); return; }
        if (!decimal.TryParse(RejWeight.Text, out decimal weight) || weight <= 0)
        { Field(RejWeight, "Weight must be positive"); return; }
        if (!TypedPrice(RejPrice, out decimal? price))
        { Field(RejPrice, "Price/ct must be a number, or left blank"); return; }

        if (Bounds.TooLarge(weight, WeightField) is { } tooHeavy) { Say(tooHeavy); RejWeight.Focus(); return; }
        if (price is { } rp && Bounds.TooLarge(rp, "Price per carat") is { } tooDear) { Say(tooDear); RejPrice.Focus(); return; }
        if (!ConfirmLarge(weight, price ?? 0)) return;

        // Dispositions travel with the rejection now, into rejection_disposition (0018), in the
        // same transaction as the movement they describe. They used to be counted and discarded.
        var gradeByCode = Catalogue.Grades
            .GroupBy(g => g.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().GradeId, StringComparer.OrdinalIgnoreCase);

        List<(decimal WeightCt, long? ToGradeId, string? Note)> typed = _dispositions
            .Where(d => d.WeightCt > 0)
            .Select(d => (
                d.WeightCt,
                ToGradeId: d.ToGradeCode is { } code && gradeByCode.TryGetValue(code, out long id)
                           ? id : (long?)null,
                Note: (string?)(string.IsNullOrWhiteSpace(d.Note) ? d.Outcome : $"{d.Outcome} · {d.Note}")))
            .ToList();

        // A disposition must not claim more than was rejected. The grid accepts any weight, and
        // over-allocating one rejection across several destinations is the one error here that
        // produces a plausible-looking row nobody would question later.
        decimal allocated = typed.Sum(d => d.WeightCt);
        if (allocated > weight)
        {
            Say($"The dispositions come to {allocated:N4} ct but only {weight:N4} ct was rejected");
            return;
        }

        WriteResult result;
        using (Busy((Button)sender, "Recording…"))
            result = await Repo.RejectionAsync(grade.GradeId, size.SizeId, weight, price, typed);

        if (!result.Ok) { Say(result.Failure!); return; }

        int kept = typed.Count;
        _dispositions.Clear();
        EnsureTrailingDisposition();
        RejWeight.Text = "";
        RejPrice.Text = "";
        CountOp(OpKpiReject, ref _opRejections);
        RefreshLedgerIfShowing(grade, size);

        string note = string.Join(" — ", new[]
        {
            kept == 0 ? null : $"{kept} disposition(s) saved",
            result.Warning
        }.Where(s => s is not null));

        Say(note.Length == 0
            ? $"Rejection recorded · {weight:N4} ct"
            : $"Rejection recorded · {weight:N4} ct — {note}",
            ok: result.Warning is null);
    }

    private async void Adjust_Click(object sender, RoutedEventArgs e)
    {
        var (grade, size) = Pick(AdjGrade, AdjSize);
        if (grade is null || size is null) { Field(AdjGrade, PickGradeSize); return; }
        if (!decimal.TryParse(AdjWeight.Text, out decimal weight) || weight == 0)
        { Field(AdjWeight, "Adjust by a non-zero weight"); return; }
        if (string.IsNullOrWhiteSpace(AdjReason.Text))
        { Field(AdjReason, "An adjustment needs a reason"); return; }

        // Signed on purpose (docs/12 §7) — TooLarge tests the magnitude, so a big correction
        // downwards is checked the same as a big one upwards.
        if (Bounds.TooLarge(weight, WeightField) is { } tooHeavy) { Say(tooHeavy); AdjWeight.Focus(); return; }
        if (!ConfirmLarge(weight, 0)) return;

        WriteResult result;
        using (Busy((Button)sender, "Adjusting…"))
            result = await Repo.AdjustAsync(grade.GradeId, size.SizeId, weight, AdjReason.Text.Trim());

        if (!result.Ok) { Say(result.Failure!); return; }

        AdjWeight.Text = "";
        AdjReason.Text = "";
        CountOp(OpKpiAdjust, ref _opAdjustments);
        RefreshLedgerIfShowing(grade, size);
        Announce("Adjustment recorded — it stays visible in the ledger forever", result.Warning);
    }

    // ── Master data ─────────────────────────────────────────────────────────

    private async Task LoadMasterAsync()
    {
        // Held whole so search and the status filter work on the full catalogue rather than on
        // whatever the grid happens to be showing.
        _grades = await Read(Repo.GradesAsync) ?? [];
        ApplyGradeFilter();

        SizeGrid.ItemsSource = await Read(Repo.SizesAsync);
        // Every buyer and broker, not just the active ones. This is the page that deactivates
        // them, and reading the filtered list meant a row switched off here disappeared from here
        // -- unviewable and unrestorable, by the only screen that could have restored it. The
        // status badge on each row already says which is which.
        var buyers = await Read(() => Repo.BuyersAsync(activeOnly: false)) ?? [];
        var brokers = await Read(() => Repo.BrokersAsync(activeOnly: false)) ?? [];
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
    /// <summary>
    /// Caps the Grades table at a WHOLE number of rows, so its bottom edge never lands mid-row.
    ///
    /// Every other grid in the app renders plain text, and a last row clipped by the viewport
    /// reads as exactly what it is -- there is more below. The alias column is the one editable
    /// cell in the app, and EditableCell draws it as a real input box (32 high inside a 4px
    /// margin). Cut that in half and it does not read as a row continuing past the edge; it reads
    /// as a broken field, which is how it was reported.
    ///
    /// Measured off the grid's OWN properties rather than the numbers in AppDataGrid, so restyling
    /// a row taller cannot leave this arithmetic quietly wrong.
    ///
    /// The cost is up to one row's worth of space above the hint text. That is the honest trade:
    /// a table that stops where a row stops, rather than one that appears to contain half a box.
    /// </summary>
    private void GradeArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double row = GradeGrid.RowHeight;
        if (double.IsNaN(row) || row <= 0) return;      // rows sized to content: nothing to snap to

        // The chrome above the rows: the column header, and the grid's own top and bottom border.
        double chrome = GradeGrid.ColumnHeaderHeight + GradeGrid.BorderThickness.Top
                                                     + GradeGrid.BorderThickness.Bottom;

        // At least one row, however cramped the card gets -- a table showing none of its rows is
        // worse than one showing a single row and scrolling.
        int rows = Math.Max(1, (int)Math.Floor((e.NewSize.Height - chrome) / row));
        GradeGrid.MaxHeight = chrome + rows * row;
    }

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
             // The mark too, now that it is what every other screen shows. Searching Master data
             // for the name you just read off a picker has to find the row.
             || g.ShortName.Contains(term, StringComparison.OrdinalIgnoreCase)
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
    /// Rename a buyer, change its default terms, or deactivate it.
    ///
    /// Add was the only operation this page had, so a typo in a name was permanent from inside the
    /// app and a buyer who stopped trading stayed in every picker for ever. The same dialog and the
    /// same ValidateBuyer rules as Add — the name is still checked for duplicates, and the buyer's
    /// own current name is excluded from that check so saving it unchanged is not a collision.
    ///
    /// The row is edited, never replaced: buyer_id is what every invoice ever raised points at.
    /// </summary>
    private async void EditBuyer_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Buyer buyer) return;

        var others = (BuyerGrid.ItemsSource as IEnumerable<Buyer>)?
            .Where(b => b.BuyerId != buyer.BuyerId).Select(b => b.Name).ToList() ?? [];

        var values = AppFormDialog.Show(this, "Edit buyer", $"Edit {buyer.Name}",
            "Renaming does not touch the invoices already raised for this buyer — they follow the "
            + "record, not the name. Active decides whether it appears in the Sales entry picker.",
            [new FormFieldSpec("Buyer name", buyer.Name),
             new FormFieldSpec("Terms (days)", buyer.DefaultTermsDays.ToString(), Numeric: true, MaxLength: 4),
             new FormFieldSpec("Active (yes/no)", buyer.Active ? "yes" : "no", MaxLength: 3)],
            v => ValidateBuyer(v[0], v[1], others, out _) ?? YesNo(v[2], out _),
            "Save buyer");
        if (values is null) return;

        ValidateBuyer(values[0], values[1], others, out int terms);
        YesNo(values[2], out bool active);

        string? failure = await Repo.UpdateBuyerAsync(buyer.BuyerId, values[0], terms, active);
        Say(failure ?? $"Buyer “{values[0]}” saved", ok: failure is null);
        if (failure is null) { await LoadMasterAsync(); await LoadPartiesAsync(); }
    }

    private async void EditBroker_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Broker broker) return;

        var others = (BrokerGrid.ItemsSource as IEnumerable<Broker>)?
            .Where(b => b.BrokerId != broker.BrokerId).Select(b => b.Name).ToList() ?? [];

        var values = AppFormDialog.Show(this, "Edit broker", $"Edit {broker.Name}",
            "The commission here is only the default for new invoices; it stays editable per "
            + "invoice and changes nothing already posted.",
            [new FormFieldSpec("Broker name", broker.Name),
             new FormFieldSpec("Default %", broker.DefaultBrokerPct.ToString("0.##"), Numeric: true, MaxLength: 6),
             new FormFieldSpec("Active (yes/no)", broker.Active ? "yes" : "no", MaxLength: 3)],
            v => ValidateBroker(v[0], v[1], others, out _) ?? YesNo(v[2], out _),
            "Save broker");
        if (values is null) return;

        ValidateBroker(values[0], values[1], others, out decimal pct);
        YesNo(values[2], out bool active);

        string? failure = await Repo.UpdateBrokerAsync(broker.BrokerId, values[0], pct, active);
        Say(failure ?? $"Broker “{values[0]}” saved", ok: failure is null);
        if (failure is null) { await LoadMasterAsync(); await LoadPartiesAsync(); }
    }

    /// A yes/no field. Typed rather than a checkbox because AppFormDialog takes text fields only,
    /// and adding a control type for one flag is more surface than the flag is worth.
    private static string? YesNo(string text, out bool value)
    {
        string t = text.Trim().ToLowerInvariant();
        value = t is "yes" or "y" or "true" or "1";
        return t is "yes" or "y" or "true" or "1" or "no" or "n" or "false" or "0"
            ? null
            : "Active must be yes or no.";
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

        var grades = Catalogue.Grades.ToDictionary(g => g.GradeId, g => g.ShortName);
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
        if (grade is null || size is null) { Field(PriceGradePicker, PickGradeSize); return; }
        if (!decimal.TryParse(PriceValue.Text, out decimal price) || price < 0)
        { Field(PriceValue, "Enter a price"); return; }

        string context = (PriceContext.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "SALE";

        // Repo closes the previous open price and opens a new one — prices are never edited in place,
        // so a valuation as of any past date still finds the price that applied then.
        string? failure = await Repo.SetPriceAsync(grade.GradeId, size.SizeId, context, price);

        Say(failure ?? $"{grade.ShortName} {size.Code} {context} = {price:N2} from today", ok: failure is null);
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
            CustomRange => (Day(FromDate.SelectedDate) ?? today.AddMonths(-1), Day(ToDate.SelectedDate) ?? today),
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
    private (decimal Amount, decimal Carats, decimal Broker) Sale(VInvoice i)
    {
        if (_gradeShare.Count == 0) return (i.AmountTotal, i.CaratsSold, i.BrokerPayable);
        return _gradeShare.TryGetValue(i.InvoiceId, out var share) ? share : (0m, 0m, 0m);
    }

    /// <summary>
    /// Every invoice, cached. The date, buyer and search filters are all applied in memory below,
    /// so this list does not depend on them — yet it was re-fetched on every filter change, paging
    /// 1,443 invoices over two round trips and raising the full-page veil each time. Cleared by
    /// Refresh and by re-entering the tab, which are the moments an invoice could have been posted
    /// or cancelled.
    /// </summary>
    private List<VInvoice>? _dashInvoices;

    private async Task<List<VInvoice>> FilteredInvoicesAsync()
    {
        var (from, to) = Period();
        var all = _dashInvoices ??= await Read(Repo.InvoicesAsync) ?? [];

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
            // Quiet: this one genuinely depends on the range, so it cannot be cached — but it runs
            // in response to a filter, and a filter must not blank the page it is narrowing.
            var lines = await Read(() => Repo.SalesLinesAsync(from, to), veil: false) ?? [];
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
        if ((RangePicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() != CustomRange)
        {
            _syncingRange = true;
            FromDate.SelectedDate = ToDate.SelectedDate = null;
            FromDate.BlackoutDates.Clear(); ToDate.BlackoutDates.Clear();
            _syncingRange = false;

            // A named range is complete the moment it is picked, so it applies itself. Choosing
            // "Custom…" is not: it applies once a date arrives, from CustomDate_Changed.
            AutoApply();
        }
    }

    /// <summary>
    /// Re-runs the dashboard for whatever the filter row now says. Every control that changes the
    /// scope calls this, so the figures can never sit there describing a filter the user has
    /// already moved on from — the state that made the range read "Custom…" while the trend below
    /// still showed the previous period.
    ///
    /// Guarded because the loads overlap: each one is several round trips, and a second started
    /// mid-flight would race the first to write the same tiles. A change arriving during a load
    /// is remembered and run once the load finishes, so the last thing picked always wins.
    /// </summary>
    private bool _dashLoading, _dashPending;

    private async void AutoApply()
    {
        if (RangePicker is null || _syncingRange) return;      // still loading the XAML

        if (_dashLoading) { _dashPending = true; return; }

        _dashLoading = true;
        try
        {
            // Awaits the load itself rather than yielding past an async void call. The first
            // version yielded and hoped the continuation came back on the UI thread — true under
            // WPF, which installs a SynchronizationContext, but not true anywhere without one, and
            // the continuation then touched controls from a thread-pool thread.
            do
            {
                _dashPending = false;
                await LoadDashboardAsync();
            }
            while (_dashPending);
        }
        finally
        {
            _dashLoading = false;
        }
    }

    /// The buyer and grade pickers scope the same page. Without these they only took effect when
    /// Apply was pressed, which is what kept the button necessary.
    private void DashFilter_Changed(object sender, SelectionChangedEventArgs e) => AutoApply();

    /// <summary>
    /// The date boxes used to be disabled unless the range was already "Custom…", which reads as
    /// two broken fields — there is nothing on them to say the drop-down is the way in. Picking a
    /// date now selects Custom itself. Period() is unchanged: it still reads these two boxes only
    /// when the range says CUSTOM.
    /// </summary>
    private void CustomDate_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (RangePicker is null || _syncingRange) return;

        // WHICH box was touched decides which one moves if the range inverts. The one the user
        // just set is the one they meant.
        SyncDateBounds(sender as DatePicker);

        if (FromDate.SelectedDate is null && ToDate.SelectedDate is null) return;

        if ((RangePicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() != CustomRange)
        {
            _syncingRange = true;
            RangePicker.SelectedItem = RangePicker.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => i.Tag?.ToString() == CustomRange);
            _syncingRange = false;
        }

        // Applied on any date at all. SyncDateBounds has already straightened the range if this
        // pick inverted it, so what reaches here always exists — a "From" on its own reads as
        // open-ended, which is how Period() already treats it.
        AutoApply();
    }

    /// <summary>
    /// Keeps the two calendars describing a range that can exist. A "To" earlier than "From"
    /// matches nothing at all, and an empty dashboard reads as missing data rather than as an
    /// impossible filter.
    ///
    /// STRAIGHTENED, NOT FORBIDDEN. This used to black the offending days out, and the trap that
    /// set was worse than the problem: with From and To both on 19 Aug, every day after the 19th
    /// was dead in the From calendar and every day before it was dead in the To calendar. Moving
    /// the range forward to include today was only possible by touching To first, and nothing on
    /// screen said so — two thirds of the month simply refused to be clicked.
    ///
    /// So every day is offered, and a pick that would invert the range drags the other end along
    /// instead. Whichever box the user just set is the one that stands: setting From past To
    /// pushes To out, setting To before From pulls From back. The range is always valid and the
    /// calendar never argues.
    ///
    /// Also opens the second calendar near the first: left to itself it opens on today, which is
    /// how the two ended up months apart.
    /// </summary>
    /// <param name="changed">
    /// The box the user just set, so the other one is the one that gives way. Null on the calls
    /// that are not a user's pick -- a preset range, or the first load -- and then "To" moves, as
    /// it did before.
    /// </param>
    private void SyncDateBounds(DatePicker? changed = null)
    {
        if (FromDate is null || ToDate is null) return;

        // Every day stays pickable. Anything left over from an older build goes with it.
        FromDate.BlackoutDates.Clear();
        ToDate.BlackoutDates.Clear();

        if (FromDate.SelectedDate is { } from && ToDate.SelectedDate is { } to && to < from)
        {
            _syncingRange = true;

            if (ReferenceEquals(changed, ToDate))
            {
                FromDate.SelectedDate = to;
                Say("\"From\" was after \"To\" — the start date has been moved to match the end");
            }
            else
            {
                ToDate.SelectedDate = from;
                Say("\"To\" was before \"From\" — the end date has been moved to match the start");
            }

            _syncingRange = false;
        }

        // The second calendar opens beside the first rather than on today.
        if (FromDate.SelectedDate is { } start && ToDate.SelectedDate is null)
            ToDate.DisplayDate = start;
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

        // Wipe what the last render drew BEFORE deciding whether this one can draw anything. This
        // used to happen further down, past the two early returns — so narrowing the range to a
        // single invoice printed "a trend needs at least two points" over the previous chart's
        // dots and axis, still sitting on the canvas. The message and the picture then said
        // different things, and the picture was two filters out of date.
        //
        // Named children are XAML's (the polylines, guide line, highlight, tip card); only what
        // this method added is anonymous.
        for (int i = TrendCanvas.Children.Count - 1; i >= 0; i--)
            if (TrendCanvas.Children[i] is FrameworkElement drawn && drawn.Name.Length == 0)
                TrendCanvas.Children.RemoveAt(i);

        // Sale(i), not AmountTotal: with a grade filter set, an invoice's contribution is that
        // grade's lines only, and the trend has to plot the same money the tiles add up.
        var dated = _trend.Where(i => Sale(i).Amount > 0).ToList();
        if (dated.Count < 2)
        {
            // One point is not a trend, and an empty chart with a stray dot reads as broken. Say
            // which of the two it is: "no invoices" in front of a single invoice sends people
            // looking for missing data that is not missing.
            //
            // And say what to do about it. The zero-invoice branch has always named the range and
            // pointed at All time; this one just stated the rule and stopped, so a default range of
            // "This month" on a book whose sales ended last month read as a broken chart rather
            // than as a range that needs widening.
            TrendEmpty.Text = dated.Count == 1
                ? "Only one invoice in this selection — a trend needs at least two points." + WidenHint()
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

        double X(int i) => padLeft + (buckets.Count == 1 ? plotWidth / 2 : i * plotWidth / (buckets.Count - 1));
        double Y(decimal v) => padTop + plot - (double)(v / peak) * plot;

        DrawTrendAxis(peak, padLeft, w - padRight, Y);
        DrawTrendLine(buckets, X, Y, padTop + plot);
        DrawTrendMarkers(buckets, byMonth, total, X, Y);

        _plotTop = padTop;
        _plotBottom = padTop + plot;

        DrawTrendDateLabels(buckets, byMonth, X, padLeft, w - padRight, padTop + plot);
        ShowTrendSummary(buckets, byMonth, peak);
    }

    /// Four steps of the real peak, not a rounded invention — each a gridline and its value label.
    private void DrawTrendAxis(decimal peak, double left, double right, Func<decimal, double> Y)
    {
        var scale = AxisScale(peak);

        for (int step = 0; step <= 4; step++)
        {
            decimal value = peak * step / 4;
            double y = Y(value);

            TrendCanvas.Children.Add(Line(left, y, right, y,
                (Brush)FindResource("BorderBrush"), step == 0 ? 1 : 0.6));

            var label = new TextBlock
            {
                Text = scale(value),
                FontSize = 10,
                Foreground = (Brush)FindResource("TextMutedBrush"),
                Width = left - 8,
                TextAlignment = TextAlignment.Right,
            };
            Canvas.SetLeft(label, 0);
            Canvas.SetTop(label, y - 7);
            TrendCanvas.Children.Add(label);
        }
    }

    /// The line, plus the two extra points that close the fill down onto the baseline.
    private void DrawTrendLine(List<(DateOnly Key, decimal Total)> buckets,
                               Func<int, double> X, Func<decimal, double> Y, double baseline)
    {
        for (int i = 0; i < buckets.Count; i++)
        {
            var p = new Point(X(i), Y(buckets[i].Total));
            TrendPath.Points.Add(p);
            TrendFill.Points.Add(p);
        }
        TrendFill.Points.Add(new Point(X(buckets.Count - 1), baseline));
        TrendFill.Points.Add(new Point(X(0), baseline));
    }

    /// A marker per point, each carrying its own figures. Real numbers for that point only — the
    /// bucket, its total, and its share of the period. Nothing here restates the axis.
    private void DrawTrendMarkers(List<(DateOnly Key, decimal Total)> buckets, bool byMonth,
                                  decimal total, Func<int, double> X, Func<decimal, double> Y)
    {
        string pointUnit = byMonth ? "MMM yyyy" : DayMonthYear;
        _trendPoints.Clear();

        for (int i = 0; i < buckets.Count; i++)
        {
            decimal share = total == 0 ? 0 : buckets[i].Total / total * 100;
            double x = X(i), y = Y(buckets[i].Total);

            var dot = new Ellipse
            {
                Width = 9, Height = 9,
                Fill = (Brush)FindResource("SurfaceBrush"),
                Stroke = (Brush)FindResource("AccentBrush"),
                StrokeThickness = 2,
                ToolTip = $"{buckets[i].Key.ToString(pointUnit)}\n{Money.Short(buckets[i].Total)}\n"
                          + $"{share:N1}% of the period",
            };
            Canvas.SetLeft(dot, x - 4.5);
            Canvas.SetTop(dot, y - 4.5);
            TrendCanvas.Children.Add(dot);

            _trendPoints.Add((x, y, buckets[i].Key.ToString(pointUnit), buckets[i].Total, share));
        }
    }

    /// First, middle and last. More than three collide below about 700px.
    private void DrawTrendDateLabels(List<(DateOnly Key, decimal Total)> buckets, bool byMonth,
                                     Func<int, double> X, double left, double right, double baseline)
    {
        int[] at = buckets.Count <= 2
            ? [0, buckets.Count - 1]
            : [0, buckets.Count / 2, buckets.Count - 1];

        foreach (int i in at)
        {
            var label = new TextBlock
            {
                Text = buckets[i].Key.ToString(byMonth ? "MMM yy" : "dd MMM"),
                FontSize = 10,
                Foreground = (Brush)FindResource("TextMutedBrush"),
            };
            label.Measure(new Size(200, 20));
            Canvas.SetLeft(label, Math.Clamp(X(i) - label.DesiredSize.Width / 2,
                                             left, right - label.DesiredSize.Width));
            Canvas.SetTop(label, baseline + 4);
            TrendCanvas.Children.Add(label);
        }
    }

    /// The caption, the peak, and the hover summary — the highest and lowest buckets by name and
    /// amount, plus the total the line adds up to.
    private void ShowTrendSummary(List<(DateOnly Key, decimal Total)> buckets, bool byMonth, decimal peak)
    {
        TrendCaption.Text = $"{Plural(buckets.Count, byMonth ? "month" : "day")} · "
                            + $"{buckets[0].Key:dd MMM yyyy} to {buckets[^1].Key:dd MMM yyyy}";
        TrendPeak.Text = $"peak {Money.Short(peak)}";

        var high = buckets.MaxBy(b => b.Total);
        var low = buckets.MinBy(b => b.Total);
        string unit = byMonth ? "MMM yyyy" : DayMonthYear;
        TrendCanvas.ToolTip =
            $"{Plural(buckets.Count, byMonth ? "month" : "day")} plotted\n"
            + $"Highest · {high.Key.ToString(unit)} · {Money.Short(high.Total)}\n"
            + $"Lowest · {low.Key.ToString(unit)} · {Money.Short(low.Total)}\n"
            + $"Total · {Money.Short(buckets.Sum(b => b.Total))}";
    }

    /// <summary>
    /// Why the current filters produced nothing. "Nothing in this period" is true but useless: it
    /// does not say whether the range is before the data, after it, or narrowed away by a buyer.
    /// Every branch below is a fact about rows already in memory.
    /// </summary>
    /// <summary>
    /// <summary>
    /// Stops a DatePicker leaving its own text highlighted blue after the calendar sets a date.
    ///
    /// WPF re-focuses the DatePickerTextBox when the calendar closes and selects all of it, so the
    /// box you just filled reads as "wrong" or as text about to be overtyped — on the one value
    /// that is definitely correct.
    ///
    /// Registered once, for every DatePicker in the window: the dashboard's From and To, the entry
    /// screen's invoice date, the stock import's as-at. Handling it per-control fixed one picker
    /// and left the rest, and the next date field added would have arrived broken.
    ///
    /// ApplicationIdle, not Loaded or Background: picking a date raises SelectedDateChanged, and
    /// only THEN does the calendar close and re-focus the text box, which is what selects the text.
    /// Anything sooner is overwritten a moment later by the very selection it is trying to clear —
    /// which is exactly why clearing it at Loaded priority appeared to do nothing at all.
    /// </summary>
    /// <summary>
    /// Drops the blue highlight a DatePicker leaves over its own text after the calendar closes.
    ///
    /// Wired per picker in XAML, not once at the window: DatePicker.SelectedDateChangedEvent is
    /// registered RoutingStrategy.Direct, so it never reaches an ancestor handler. That is exactly
    /// why the entry screen's date behaved and the dashboard's From and To did not — one had the
    /// handler on the control, the others were relying on bubbling that does not happen.
    ///
    /// Queued at Input priority because DatePickerTextBox selects all of its text in OnGotFocus,
    /// and closing the calendar hands focus back to it — so the selection lands after this event
    /// returns, and anything cleared synchronously is simply overwritten.
    /// </summary>
    private void DatePicker_CalendarClosed(object sender, RoutedEventArgs e) =>
        Deselect((DatePicker)sender);

    private void Deselect(DatePicker picker) =>
        picker.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (picker.Template?.FindName("PART_TextBox", picker) is TextBox box)
            {
                box.SelectionLength = 0;
                box.CaretIndex = box.Text.Length;
            }
        });

    /// " Sales run from … to … — try All time, or widen the range.", or "" when the range already
    /// covers everything and widening would change nothing.
    ///
    /// Uses the same _dataFrom/_dataTo the zero-invoice message does, so both branches point at the
    /// same dates rather than one of them guessing.
    /// </summary>
    private string WidenHint()
    {
        if (_dataFrom is not { } first || _dataTo is not { } last) return "";

        var (from, to) = Period();
        return from <= first && to >= last
            ? ""      // already showing everything: there is nothing wider to suggest
            : $" Sales run from {first:dd MMM yyyy} to {last:dd MMM yyyy} — "
              + "try All time, or widen the range.";
    }

    private string EmptyReason()
    {
        var (from, to) = Period();
        string span = $"{from:dd MMM yyyy} to {to:dd MMM yyyy}";

        if (_postedTotal == 0)
            return "No posted invoices exist yet.";

        // Invoices in range, all of them worth nothing. The chart plots money and had nothing to
        // plot, so it said "No sales" directly above a drill-down table listing them — two panels
        // on one screen contradicting each other over a parcel that was rejected in full. Both are
        // right; only the wording was wrong.
        int worthless = _trend.Count(i => Sale(i).Amount == 0);
        if (worthless > 0 && worthless == _trend.Count)
            return $"No sales worth anything in {span}. "
                   + $"{Plural(worthless, "invoice")} posted in this period, every one of them 0.00 "
                   + "— fully rejected parcels. They are listed below.";

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
        bool custom = (RangePicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() == CustomRange;
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

    /// <summary>
    /// One unit for the WHOLE axis, taken from its top tick.
    ///
    /// The unit used to be chosen per label, which is right for a figure standing on its own and
    /// wrong for a column of them: a peak of 12,470,000 printed "1.25 Cr" while the tick below it
    /// printed "93.56 L", so a reader had to convert between two counting systems to see that one
    /// was three quarters of the other. Five ticks, three units, on a chart whose whole job is to
    /// let the eye compare them.
    ///
    /// K/M/B rather than crore/lakh, because that is what every other figure in this app already
    /// uses -- the peak caption above this very chart, its own hover, and the invoice table beside
    /// it all read "12.47 M". An axis is the wrong place to introduce a second vocabulary.
    /// </summary>
    private static Func<decimal, string> AxisScale(decimal peak)
    {
        (decimal unit, string suffix) =
            peak >= 1_000_000_000m ? (1_000_000_000m, " B")
          : peak >= 1_000_000m ? (1_000_000m, " M")
          : peak >= 1_000m ? (1_000m, " K")
          : (1m, "");

        return v => v == 0m ? "0"
                            : (v / unit).ToString("0.##", CultureInfo.InvariantCulture) + suffix;
    }

    private void DrillSearch_Changed(object sender, TextChangedEventArgs e) => _ = ApplyDashboardSearchAsync();

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
    private async Task ApplyDashboardSearchAsync()
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
            // Rows of four keep four, exactly as before; only a row that actually holds a fifth
            // tile widens, so adding W7 to the sales row cannot reflow any other row.
            _ => row.Children.Count > 4 ? 5 : 4,
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
        // Back to how the page opens, not to All time. Clear used to jump to All time while the
        // page opens on This month, so pressing it changed the range to something the user had
        // never chosen — "clear" that widens the scope is not clearing, it is a different filter.
        RangePicker.SelectedIndex = DefaultRangeIndex;
        _syncingRange = true;
        FromDate.SelectedDate = ToDate.SelectedDate = null;
        FromDate.BlackoutDates.Clear(); ToDate.BlackoutDates.Clear();
        _syncingRange = false;
        FilterBuyer.SelectedItem = null;
        FilterGrade.SelectedItem = null;
        DrillSearch.Clear();
        LoadDashboard_Click(sender, e);
    }

    /// The click handler stays void for XAML; the work moves into an awaitable method so
    /// AutoApply can wait for a load to finish instead of firing the next one over it.
    ///
    /// Refresh and re-entering the tab drop the cached stock position: those are the moments an
    /// intake or a posted invoice could have moved it. A filter change is not.
    private async void LoadDashboard_Click(object sender, RoutedEventArgs e)
    {
        _dashStock = null;
        _dashInvoices = null;
        await LoadDashboardAsync();
    }

    private async Task LoadDashboardAsync()
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

        // W7 · margin. Cost is stamped onto each line when the invoice is posted (0018), so only
        // invoices posted through this app carry one -- migrated MIG- rows write no stock movements
        // and never can. The caption names the coverage rather than letting a figure drawn from a
        // handful of invoices read as the whole book, and a margin of nothing stays a dash.
        try
        {
            var margin = await Repo.MarginAsync(from, to);
            if (margin.InvoicesCosted == 0)
            {
                KpiMargin.Text = "—";
                KpiMarginBasis.Text = margin.InvoicesTotal == 0
                    ? (margin.InvoicesUncostable > 0
                        ? $"Cost not available · {margin.InvoicesUncostable:N0} migrated invoice(s)"
                        : "no posted invoices in this range")
                    : $"Cost not available · {margin.InvoicesTotal:N0} invoice(s) carry no purchase cost";
            }
            else
            {
                KpiMargin.Text = Money.Short(margin.MarginTotal);
                // Counted against invoices that COULD be costed, not the whole book: migrated
                // invoices write no stock movements, so including them read "3 of 1,438" and made
                // a working figure look broken.
                KpiMarginBasis.Text =
                    $"{margin.MarginPct:0.0}% · cost on {margin.InvoicesCosted:N0} of {margin.InvoicesTotal:N0}"
                    + (margin.InvoicesUncostable > 0 ? $" · {margin.InvoicesUncostable:N0} migrated" : "");
            }
        }
        catch (Exception ex)
        {
            // A margin the app cannot stand behind is worse than none, so nothing is guessed here.
            KpiMargin.Text = "—";
            KpiMarginBasis.Text = "Cost not available";
            Say(ex.Message);
        }

        // W1's vs-prior: the same number of days, immediately before this window.
        //
        // Nothing sold in this period means there is no change to express as a percentage. It used
        // to read "-100.0% vs prior 7,52,00,479", which is arithmetically right and reads as a
        // collapse in trading — against a prior window the user never chose and cannot see. A range
        // that simply lands outside the data is the commonest way to reach it. Say what is true.
        int days = to.DayNumber - from.DayNumber + 1;
        if (summary.SalesAmount == 0)
        {
            KpiSalesDelta.Text = "No sales in the selected period";
        }
        else
        {
            try
            {
                var prior = await Repo.DashboardAsync(from.AddDays(-days), from.AddDays(-1));
                KpiSalesDelta.Text = prior.SalesAmount == 0
                    ? "no prior period"
                    : $"{(summary.SalesAmount - prior.SalesAmount) / prior.SalesAmount * 100m:+0.0;-0.0}% vs prior {prior.SalesAmount:N0}";
            }
            catch (Exception) { KpiSalesDelta.Text = "no prior period"; }
        }

        // Range and buyer applied. Search is applied after this, in ApplyDashboardSearchAsync, so
        // typing re-scopes the page without another read.
        _ranged = await FilteredInvoicesAsync();
        await ApplyDashboardSearchAsync();

        DashSyncChip.Text = $"Updated {DateTime.Now:HH:mm}";
        DashCatalogue.Text = $"{Catalogue.Grades.Count} grades · {_invoice.Buyers.Count} buyers";

        await LoadAlertsAsync(summary);
        await LoadBreakdownAsync();
    }

    /// W15 · the alerts strip. Hidden entirely when there is nothing wrong.
    /// <summary>
    /// The stock position behind the alert strip. Cached because it does NOT depend on the date
    /// range — the Inventory row says so itself: "Position as it stands now, not for the range
    /// above". Re-reading it on every filter change was a wasted round trip, and worse, it went
    /// through Read(), which raises the full-page busy veil: changing a buyer or nudging a date
    /// blanked the whole dashboard behind a spinner to fetch figures that could not have changed.
    ///
    /// Refresh clears it, so an intake or a posted invoice still shows up.
    /// </summary>
    private List<VStockPosition>? _dashStock;

    private async Task LoadAlertsAsync(DashboardSummary summary)
    {
        _dashStock ??= await Read(Repo.StockAsync) ?? [];
        var stock = _dashStock;
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
        // Collapsed, not Hidden. Hidden kept the control's 98px of layout, so every breakdown that
        // is not "by period" drew a gap in the header where a dropdown had been -- which reads as
        // something that failed to load rather than as something that does not apply. Day, week and
        // month describe a time axis; "Sales by buyer" has none, so the control goes away entirely
        // and takes its caption with it.
        PeriodBucketGroup.Visibility = which == "period" ? Visibility.Visible : Visibility.Collapsed;

        var (bars, money) = await BuildBarsAsync(which, bucket);

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

    /// <summary>
    /// The bars for one breakdown, and whether their values are money or carats. Split out of
    /// LoadBreakdownAsync so that method is about rendering and this one about choosing the data.
    /// </summary>
    private async Task<(List<(string Label, decimal Value, string? Secondary)> Bars, bool Money)>
        BuildBarsAsync(string which, string bucket) => which switch
    {
        "salesperson" => (Group(_drill, i => i.Salesperson ?? "unattributed", i => Sale(i).Amount), true),
        "buyer" => (Group(_drill, i => i.BuyerName, i => Sale(i).Amount), true),
        "broker-cost" => (Group(_drill, i => i.BrokerName ?? "no broker", i => Sale(i).Broker), true),

        "period" => (Group(_drill, i => Bucket(i.InvoiceDate, bucket), i => Sale(i).Amount)
                         .OrderBy(b => b.Label).ToList(), true),

        "ageing" => (Group(await Read(Repo.ReceivablesAsync, veil: false) ?? [], r => r.AgeBucket, r => r.Outstanding), true),

        "inventory" => (Group(await FilteredStockAsync(), s => s.GradeCode, s => s.StockValue), true),
        "inventory-aging" => (Group(await FilteredStockAsync(), s => AgeBand(s.AgeDays), s => s.BalanceCt), false),

        _ => (new List<(string Label, decimal Value, string? Secondary)>(), true),
    };

    /// Stock narrowed to the grade on the filter bar, if one is chosen.
    private async Task<List<VStockPosition>> FilteredStockAsync()
    {
        // The same cached position the alert strip uses, for the same reason: the breakdown's
        // grade filter is applied below, in memory.
        var stock = _dashStock ??= await Read(Repo.StockAsync, veil: false) ?? [];
        return FilterGrade.SelectedItem is Grade grade
            ? stock.Where(s => s.GradeCode == grade.Code).ToList()
            : stock;
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
        // Scoped in the database when an entity is chosen. Filtering the newest 500 in memory meant
        // one bulk import — a thousand receipt deletes — hid every other table completely.
        string? scope = AuditEntity?.SelectedIndex > 0 ? AuditEntity.SelectedItem as string : null;

        var rows = await Read(() => Repo.AuditAsync(scope));
        if (rows is null) return;

        // Names before rows, so WHO resolves on the first render rather than after a redraw.
        // The same Repo.UsersAsync the Users page reads — one source for who exists, so the two
        // screens cannot name the same person differently. Quiet: it is a lookup for a column,
        // not the load itself, and it must not fail the page if profiles is unreadable — the
        // trail still stands with UUIDs in it, which is better than no trail.
        if (await Read(Repo.UsersAsync, veil: false) is { } people)
            AuditRow.Names = people
                .GroupBy(p => p.Id)
                .ToDictionary(g => g.Key, g => g.First().FullName ?? "");

        // AuditRow.From keeps the same mapping the flattened columns used — AuditedTable, not
        // TableName, because TableName is BaseModel's own and reads "audit_log" on every row.
        _audit = rows.Select(AuditRow.From).ToList();

        AuditChip.Text = $"{_audit.Count:N0} entries";

        // Repo.AuditAsync takes the newest AuditLimit rows and stops. A full page looked exactly
        // like a complete history, so "it is not in the audit trail" was being read as "it never
        // happened" when the entry had simply scrolled off. Say when the window is full.
        bool capped = _audit.Count >= Repo.AuditLimit;
        AuditSpan.Text = _audit.Count == 0
            ? "Nothing recorded yet"
            : $"{_audit[^1].ChangedAt:dd MMM yyyy} to {_audit[0].ChangedAt:dd MMM yyyy} · "
              + $"{_audit.Select(a => a.Entity).Distinct().Count()} entities"
              + (capped ? $" · newest {Repo.AuditLimit:N0} only, older entries not loaded" : "");

        // Every audited table, not just the ones on this page. Offering only what came back made
        // the filter useless exactly when it was needed most: after a bulk operation the list read
        // "receipt" and nothing else, so there was no way to ask about stock or invoices. The union
        // keeps it self-healing if a table starts appearing that the constant does not know about.
        object? keep = AuditEntity.SelectedItem;
        AuditEntity.ItemsSource = new[] { "All entities" }
            .Concat(Repo.AuditedTables.Concat(_audit.Select(a => a.Entity))
                        .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
            .ToList();
        AuditEntity.SelectedItem = keep is string s && AuditEntity.Items.Contains(s) ? s : "All entities";

        ApplyAuditFilter();
    }

    private void AuditFilter_Changed(object sender, RoutedEventArgs e)
    {
        // Action and search narrow what is already here; entity has to go back to the database,
        // because the rows for that table may never have been in this page at all.
        if (ReferenceEquals(sender, AuditEntity)) LoadAudit_Click(sender, e);
        else ApplyAuditFilter();
    }

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

        // The trail is loaded newest-first and stops at AuditLimit. When that window is full these
        // tiles are counting a page, not a history — and "Inserts 0" then reads as "nothing was
        // ever inserted" when in truth a thousand inserts sit just past the cut. The span line
        // above already says the window is full, but it is above the cards and skipped past. Same
        // failure as the import dialog's carats: a number that is true of one scope, read as the
        // answer for another. So every caption names its own scope.
        bool capped = _audit.Count >= Repo.AuditLimit;

        AuditKpiTotal.Text = shown.Count.ToString("N0");
        AuditKpiTotalNote.Text = (shown.Count == _audit.Count, capped) switch
        {
            (true, true) => $"Newest {Repo.AuditLimit:N0} · older not loaded",
            (true, false) => "All loaded entries",
            (false, true) => $"of {_audit.Count:N0} newest · older not loaded",
            (false, false) => $"of {_audit.Count:N0} loaded",
        };

        AuditKpiInsert.Text = shown.Count(a => a.Action == "INSERT").ToString("N0");
        AuditKpiUpdate.Text = shown.Count(a => a.Action == "UPDATE").ToString("N0");
        AuditKpiDelete.Text = shown.Count(a => a.Action == "DELETE").ToString("N0");

        string scopeNote = $" · in these {Repo.AuditLimit:N0} only";
        AuditKpiInsertNote.Text = "Rows created" + (capped ? scopeNote : "");
        AuditKpiUpdateNote.Text = "Rows amended" + (capped ? scopeNote : "");
        AuditKpiDeleteNote.Text = "Rows removed" + (capped ? scopeNote : "");

        AuditCount.Text = shown.Count == 0
            ? NoFilterMatch
            : $"{Plural(shown.Count, "entry", "entries")} · select a row to see the fields";
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
        // Case-insensitive, matching Db.IsOwner: profile.role has no CHECK constraint, so "Owner"
        // and "owner" both occur and an exact compare counted a real owner as staff.
        UserKpiOwners.Text = shown.Count(IsOwnerRole).ToString("N0");

        // Staff is every account that is not an owner, whatever it is called. Counting only a role
        // literally named "staff" would silently omit managers and salespeople.
        UserKpiStaff.Text = shown.Count(u => !IsOwnerRole(u)).ToString("N0");

        static bool IsOwnerRole(Profile u) =>
            string.Equals(u.Role?.Trim(), "owner", StringComparison.OrdinalIgnoreCase);

        UserCount.Text = shown.Count == 0
            ? "No accounts match these filters"
            : Plural(shown.Count, "account");
    }

    /// The id is what gets pasted into Supabase when creating the matching profile row.
    // ── User administration ────────────────────────────────────────────────
    //
    // Every one of these needs the service_role key, which is deliberately not in this app. They
    // call the admin-users Edge Function with the signed-in user's own token; the function decides
    // whether that user may do it. Disabling the buttons for non-owners (ApplyRolePermissions)
    // stops an honest mistake — it is not the boundary, because anyone can POST to that URL.

    private static readonly string[] UserRoles = ["sales", "manager", "owner"];

    /// Shared by both dialogs: the rules the Edge Function will apply anyway, applied here first so
    /// a typo is answered instantly rather than after a round trip.
    private static string? CheckRole(string role) =>
        UserRoles.Contains(role.Trim().ToLowerInvariant())
            ? null : $"Role must be one of {string.Join(", ", UserRoles)}.";

    private static string? CheckPassword(string password) =>
        password.Length >= 10 ? null : "Password must be at least 10 characters.";

    private async void NewUser_Click(object sender, RoutedEventArgs e)
    {
        var values = AppFormDialog.Show(this, "New user", "Create an account",
            "The password is temporary — give it to the person and have them change it.",
            [
                new FormFieldSpec("Email", MaxLength: 120),
                new FormFieldSpec("Full name", MaxLength: 120),
                new FormFieldSpec("Role", "sales", MaxLength: 12),
                new FormFieldSpec("Temporary password", MaxLength: 72),
            ],
            v =>
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(v[0], @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
                    return "That is not a valid email address.";
                if (v[1].Length < 2) return "Enter the person's full name.";
                return CheckRole(v[2]) ?? CheckPassword(v[3]);
            },
            "Create");

        if (values is null) return;

        using (Busy(NewUserButton, "Creating…"))
        {
            var result = await AdminUsers.CreateAsync(values[0], values[1],
                                                      values[2].Trim().ToLowerInvariant(), values[3]);
            if (!result.Ok) { Say(result.Message ?? "Could not create the account."); return; }
            Say($"{values[1]} can now sign in as {values[2].ToLowerInvariant()}", ok: true);
        }
        LoadUsers_Click(sender, e);
    }

    /// <summary>
    /// One dialog for role, status and password rather than three buttons per row: the row already
    /// carries two, and an owner changing someone's role usually knows the rest of what they want
    /// to change at the same time. Only what actually differs is sent.
    /// </summary>
    private async void ManageUser_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Profile user) return;

        var values = AppFormDialog.Show(this, "Manage user", user.FullName ?? "Account",
            "Leave the password blank to keep the current one.",
            [
                new FormFieldSpec("Role", user.Role, MaxLength: 12),
                new FormFieldSpec("Active (yes/no)", user.Active ? "yes" : "no", MaxLength: 3),
                new FormFieldSpec("New password", MaxLength: 72),
            ],
            v =>
            {
                string? problem = CheckRole(v[0]);
                if (problem is not null) return problem;
                if (v[1].ToLowerInvariant() is not ("yes" or "no")) return "Active must be yes or no.";
                return v[2].Length == 0 ? null : CheckPassword(v[2]);
            },
            "Apply");

        if (values is null) return;

        string role = values[0].Trim().ToLowerInvariant();
        bool active = values[1].Trim().ToLowerInvariant() == "yes";
        string password = values[2];

        var done = new List<string>();
        using (Busy(NewUserButton, "Applying…"))
        {
            // Role before status: deactivating first would be a wasted call if the role change is
            // then refused, and the refusals worth hitting (last owner, self) come from the role.
            if (role != user.Role)
            {
                var r = await AdminUsers.ChangeRoleAsync(user.Id, role);
                if (!r.Ok) { Say(r.Message ?? "Could not change the role."); return; }
                done.Add($"role {user.Role} → {role}");
            }
            if (active != user.Active)
            {
                var r = await AdminUsers.SetActiveAsync(user.Id, active);
                if (!r.Ok) { Say(r.Message ?? "Could not change the status."); return; }
                done.Add(active ? "reactivated" : "deactivated");
            }
            if (password.Length > 0)
            {
                var r = await AdminUsers.ResetPasswordAsync(user.Id, password);
                if (!r.Ok) { Say(r.Message ?? "Could not set the password."); return; }
                done.Add("password reset");
            }
        }

        Say(done.Count == 0
            ? "Nothing changed."
            : $"{user.FullName}: {string.Join(", ", done)}", ok: done.Count > 0);

        if (done.Count > 0) LoadUsers_Click(sender, e);
    }

    /// <summary>
    /// Keeps exactly one empty row at the foot of the dispositions grid, so there is always
    /// somewhere to type. The handler already ignores rows with no weight, so a spare row costs
    /// nothing and never reaches the database.
    /// </summary>
    private void EnsureTrailingDisposition()
    {
        if (_dispositions.Count == 0 || _dispositions[^1].WeightCt > 0)
            _dispositions.Add(new DispositionRow());
    }

    // ── Stock report · the client's own sheet, on screen ────────────────────
    //
    // Reads v_stock_position through Repo.StockAsync(), the same call the Stock page makes.
    // Weight is balance_ct and Rate is avg_cost, both straight from the view. The only
    // arithmetic here is adding up figures Postgres already produced -- a column total and a
    // weight-weighted rate -- which is what the KPI tiles do as well. Nothing is recalculated.

    /// <summary>
    /// The sizes the printed sheet carries, and the notation it prints them in.
    ///
    /// The client writes fractions of a carat as "1/5" and "1/4"; the Size Master stores them as
    /// "0.2" and "0.25". Mapped here rather than in the database because it is a presentation
    /// choice belonging to one report -- adding them as size aliases would change what every
    /// other screen and both importers accept.
    /// </summary>
    /// Moved to <see cref="SizeNames"/>, which the pickers and the models can reach too. It was
    /// this report's private vocabulary, and that is exactly why the rest of the app said "0.2".
    private static Dictionary<string, string> ReportSizeLabels => SizeNames.Marks;

    /// <summary>
    /// The grade labels the printed sheet uses. Same reasoning as the sizes: the sheet writes
    /// "No. II Spotted" as "#", which is a mark on one report and not a grade code anyone can type
    /// into the app.
    /// </summary>
    /// Moved to <see cref="GradeNames"/>, which is where the pickers and the model can both reach
    /// it. Kept as a name here because the PDF label map and this report both read it.
    private static Dictionary<string, string> ReportGradeLabels => GradeNames.Marks;


    /// <summary>
    /// The row order of the printed sheet, which is NOT the catalogue's.
    ///
    /// The catalogue sorts by sort_order and opens NO 1, NO 1 BB, NO 2, NO 2 BB, NO II...; the
    /// client's page runs 1BB, #, EX1, 2, DX1, 3-7, TOP co, color, OW, LC 1, LC 2, GH, LB 1, LB 2.
    /// Somebody reading the screen against the paper has to find the same grade on the same line,
    /// so the paper wins here.
    ///
    /// Grades the sheet does not print -- NO 1, NO 2 BB, LC 3, +14, EXTRA -- follow underneath in
    /// catalogue order rather than being dropped. Hiding a grade that holds stock because one
    /// report never listed it is how carats go missing from a stock sheet; "Hide grades with no
    /// stock" is there for anyone who wants the shorter page.
    /// </summary>
    private static readonly string[] ReportGradeOrder =
    [
        "NO 1 BB", "NO II", "EX 1", "NO 2", "NO DX", "NO 3", "NO 4", "NO 5", "NO 6", "NO 7",
        "TOP-COL", "COL", "OW", "LC 1", "LC 2", "GH", "LB 1", "LB 2",
    ];

    /// Rejection movements, read once per Stock load and filtered on screen with the same
    /// predicate as the position rows, so the card and the list always describe the same set.
    private List<VStockMovement> _rejections = [];

    /// What the last stock import placed -- the workbook's own figure, so the screen can show it
    /// beside the current position instead of leaving the office to wonder why they differ.
    private List<VStockMovement> _importedStock = [];

    /// Every SALE movement. Summed on the Stock report to say what has been sold out of the
    /// buckets on screen; the ledger and v_reconciliation read the same rows.
    private List<VStockMovement> _sales = [];

    /// <summary>
    /// The sieve columns the last imported stock sheet CARRIED, which is not the same thing as
    /// the sieves holding stock.
    ///
    /// The client's sheet prints a 1/4 column at 0.00 the whole way down. It is a column: the
    /// office counts it, the paper shows it, and a report that drops it does not match the paper.
    /// But no zero cell becomes a movement, so after an import there is nothing in the database
    /// to say the column was ever there -- which is why "tick what holds stock" hid it.
    ///
    /// So the importer records the columns it read, in app_config. No schema change, one key, and
    /// every desk opens the report on the same sheet the office is holding.
    ///
    /// Null until read. Empty means no sheet has been imported since this was added, and the
    /// report falls back to what holds stock.
    /// </summary>
    private List<string>? _reportSheetSizes;

    public const string StockSheetSizesKey = "stock_sheet_sizes";
    public const string StockSheetRatesKey = "stock_sheet_rates";

    /// <summary>
    /// The rates the last imported sheet printed, by grade and size.
    ///
    /// The report shows a bucket's AVERAGE COST, which is what the ledger can derive: value over
    /// weight. A bucket holding nothing has neither, so a column the client is out of came to the
    /// screen blank while the paper beside it printed 68,000 in every cell of that column.
    ///
    /// This is the paper's answer, kept for the one screen whose job is to reproduce the paper.
    /// It is never mistaken for cost: it is only consulted where there is no stock to have a cost.
    /// </summary>
    private Dictionary<(string Grade, string Size), decimal>? _reportSheetRates;
    private readonly List<CheckBox> _reportSizeBoxes = [];
    private List<VStockPosition> _reportRows = [];

    private static string SizeLabel(string code) => SizeNames.Short(code);

    private static string GradeLabel(string code) => GradeNames.Short(code);

    /// <summary>
    /// The printed sheet read backwards: what is on the page, answering with the catalogue code.
    ///
    /// These are the inverse of the two maps above, and deliberately built from them rather than
    /// written out again. The Stock report tab and the PDF importer are looking at the same piece
    /// of paper — one drawing it, one reading it — so a label that changes in one place must change
    /// in the other, and two lists cannot be relied on to stay in step.
    ///
    /// A grade the sheet prints as its own code (OW, GH, LC 1) needs no entry in ReportGradeLabels
    /// and is picked up from the catalogue here.
    /// </summary>
    /// <summary>
    /// Takes the grades themselves rather than their codes, so the ALIASES count.
    ///
    /// Aliases are how the catalogue already absorbs the spellings a client's paperwork uses — the
    /// workbook importer has resolved through them since day one. Reading a PDF against codes alone
    /// meant a sheet printing "1 BB" or ",-2 MB" could only be accepted by renaming the grade, when
    /// the catalogue has a field for exactly this.
    /// </summary>
    public static Dictionary<string, string> PdfGradeLabelMap(IEnumerable<Grade> grades)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var g in grades)
        {
            map[PdfStockFile.Normalise(g.Code)] = g.Code;
            // The spaceless form too, so a sheet setting "NO 1 BB" as "1BB" and one setting it as
            // "1 BB" both land on the same grade. TryAdd, not assignment: if two catalogue entries
            // ever collapse to the same letters the first keeps the key rather than the last
            // silently winning.
            map.TryAdd(PdfStockFile.Normalise(g.Code).Replace(" ", ""), g.Code);
        }

        // After the codes, so a code is never displaced by another grade's alias.
        foreach (var g in grades)
            foreach (string alias in (g.Aliases ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                map.TryAdd(PdfStockFile.Normalise(alias), g.Code);
                map.TryAdd(PdfStockFile.Normalise(alias).Replace(" ", ""), g.Code);
            }

        // Last, so a printed label always wins: these are the marks this particular sheet uses.
        foreach (var (code, label) in ReportGradeLabels)
        {
            map[PdfStockFile.Normalise(label)] = code;
            map[PdfStockFile.Normalise(label).Replace(" ", "")] = code;
        }
        return map;
    }

    public static Dictionary<string, string> PdfSizeLabelMap(IEnumerable<string> codes)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string code in codes)
        {
            map[PdfStockFile.Normalise(code)] = code;
            // Keyed by sieve notation as well, which is how one sheet writes "6.5-" and "11+" for
            // the buckets another writes "-6.5" and "+11". Same rule the workbook importer uses.
            if (StockFileImport.SizeKey(code) is { } key) map.TryAdd(key, code);
        }
        foreach (var (code, label) in ReportSizeLabels) map[PdfStockFile.Normalise(label)] = code;
        return map;
    }

    // ── Stock report · read only and edit mode ──────────────────────────────
    //
    // Editing a cell does NOT write to the cell. v_stock_position.balance_ct is derived from the
    // movement ledger and there is nothing there to overwrite -- which is the point: the ledger is
    // append-only (BR-INV-1) and two desks can post at once without one clobbering the other.
    //
    // So a "manual change" is the difference between what the report shows and what the user says
    // it should be, recorded as one ADJUST movement through the same adjust_stock RPC the Intake
    // page uses. The balance then moves because the ledger moved, every other screen agrees
    // immediately, and who/when/why is captured by the audit trigger that already watches
    // stock_movement -- no second source of truth and nothing new to store.

    /// Manual ADJUST rows, so a corrected bucket can be marked and explained on hover.
    private List<StockMovement> _reportAdjustments = [];
    private Dictionary<Guid, string> _reportUserNames = new();

    /// Whether the report prints 0.00 in a bucket holding nothing. Off unless asked for.
    private bool ReportShowingZeros => ReportShowZeros?.IsChecked == true;

    /// <summary>
    /// One figure as the report prints it: blank for an untraded bucket, or 0.00 when the reader
    /// has asked to see zeros.
    ///
    /// Separated out so both states can be asserted without drawing a report.
    /// </summary>
    /// <param name="format">
    /// "N2" for carats, "N0" for a rate. The rate cells used to blank a zero unconditionally, so a
    /// bucket printing 0.00 ct sat beside an empty rate and the pair contradicted each other.
    /// </param>
    public static string ReportFigure(decimal value, bool showZeros, string format = "N2") =>
        value == 0m && !showZeros ? "" : value.ToString(format);

    private bool ReportEditing => ReportEditMode?.IsChecked == true;

    private void ReportEditMode_Click(object sender, RoutedEventArgs e)
    {
        // Belt and braces. The checkbox is hidden for anyone who may not adjust, but a hidden
        // control is still reachable by automation, and the RPC would refuse anyway.
        if (ReportEditing && !Db.IsManagerOrOwner)
        {
            ReportEditMode.IsChecked = false;
            Say("Only a manager or owner may adjust stock.");
            ApplyReportMode();
            return;
        }
        // The chip lives in ApplyReportMode. Redrawing alone made the cells editable while the
        // badge still read "Read only" -- the one thing a mode switch must never get wrong.
        ApplyReportMode();
        DrawStockReport();
    }

    /// <summary>
    /// Shows which mode the page is in, and hides the switch from anyone who cannot use it.
    /// </summary>
    private void ApplyReportMode()
    {
        if (ReportEditMode is null) return;

        ReportEditMode.Visibility = Db.IsManagerOrOwner ? Visibility.Visible : Visibility.Collapsed;
        if (!Db.IsManagerOrOwner) ReportEditMode.IsChecked = false;

        ReportModeText.Text = ReportEditing ? "Edit mode · click a weight to correct it" : "Read only";
        ReportModeChip.SetResourceReference(Border.BackgroundProperty,
            ReportEditing ? "WarningSoftBrush" : "Surface2Brush");
    }

    /// <summary>
    /// A cell the user clicked in edit mode. Asks what the weight should be and why, works out the
    /// difference, and records it. Nothing is written if the figure is unchanged.
    /// </summary>
    private async Task AdjustFromReportAsync(string gradeCode, string sizeCode, decimal current,
                                             decimal currentRate)
    {
        var grade = Catalogue.Grades.FirstOrDefault(g => g.Code == gradeCode);
        var size = Catalogue.AllSizes.FirstOrDefault(z => z.Code == sizeCode);
        if (grade is null || size is null) { Say("That grade or size is no longer in the catalogue."); return; }

        // The stock report still SHOWS retired sieves, because the stock under them is real and
        // hiding it would be preserving figures nobody can see. Editing one is a different act:
        // an adjustment is a new movement, and a retired size takes no new movements. Said in its
        // own words rather than "no longer in the catalogue" — the row is right there on screen,
        // and a message denying it exists reads as a bug.
        if (!size.Active)
        {
            Say($"{SizeLabel(sizeCode)} has been retired — its stock can be read but not adjusted.");
            return;
        }

        var values = AppFormDialog.Show(this, "Correct the stock",
            $"{grade.ShortName} × {SizeLabel(sizeCode)}",
            $"Currently {current:N4} ct at {(currentRate == 0m ? "no recorded rate" : currentRate.ToString("N0") + " a carat")}. "
            + "The difference is recorded as an adjustment on the ledger — the calculated balance "
            + "is never overwritten.",
            [
                new FormFieldSpec("Corrected weight ct", current.ToString("N4"), Numeric: true, MaxLength: 16),
                new FormFieldSpec("Rate per ct", currentRate == 0m ? "" : currentRate.ToString("0.##"),
                                  Numeric: true, MaxLength: 16),
                new FormFieldSpec("Reason", MaxLength: 120),
            ],
            v =>
            {
                if (!decimal.TryParse(v[0], out decimal target))
                    return "Enter the weight this bucket should hold.";
                if (Bounds.TooLarge(Math.Abs(target - current), WeightField) is { } tooHeavy)
                    return tooHeavy;

                decimal change = target - current;
                if (change == 0m)
                    // The rate is an average over the ledger's inward movements, not a figure
                    // stored anywhere, so there is no cell to type a new one into. Re-importing
                    // the sheet rewrites those movements, which is the honest way to restate it.
                    return "That is the weight already shown. A rate on its own cannot be corrected "
                         + "here — it is averaged from the ledger. Re-import the sheet to restate it.";

                // Only asked for when carats are being ADDED: a removal contributes nothing to the
                // average, so a rate on one would be recorded and never read.
                if (change > 0m)
                {
                    if (!decimal.TryParse(v[1], out decimal rate) || rate <= 0m)
                        return $"Enter the rate those {change:N4} ct are worth. Adding carats with no "
                             + "rate would drag this bucket's average cost towards zero.";
                    if (Bounds.TooLarge(rate, "Rate") is { } tooDear)
                        return tooDear;
                }

                // adjust_stock enforces this too; saying it here saves a round trip to be told.
                return string.IsNullOrWhiteSpace(v[2]) ? "A reason is required for every adjustment." : null;
            },
            "Record adjustment");

        if (values is null) return;

        decimal corrected = decimal.Parse(values[0]);
        decimal delta = corrected - current;
        decimal? price = delta > 0m && decimal.TryParse(values[1], out decimal p) ? p : null;

        // The reason carries both figures, so the ledger reads as a correction rather than a bare
        // number: the audit row keeps who and when, and this keeps what it was and what it became.
        string reason = $"Stock report correction: {current:N4} -> {corrected:N4} ct "
                      + $"({(delta < 0 ? "-" : "+")}{Math.Abs(delta):N4})"
                      + (price is { } r ? $" at {r:N0}/ct" : "") + $". {values[2].Trim()}";

        var result = await Read(() => Repo.AdjustAsync(grade.GradeId, size.SizeId, delta, reason,
                                                       pricePerCt: price));
        if (result is null) return;
        if (!result.Ok) { Say(result.Failure!); return; }

        Announce($"Adjusted {gradeCode} × {SizeLabel(sizeCode)} by {(delta < 0 ? "-" : "+")}{Math.Abs(delta):N4} ct",
                 result.Warning);

        // Reload from the database rather than patching the cell: the balance is derived, and the
        // only figure worth showing is the one the view now returns.
        await LoadStockReportAsync();
    }

    /// The most recent manual adjustment on a bucket, for the marker's tooltip.
    private string? ReportAdjustmentNote(long gradeId, long sizeId)
    {
        var last = _reportAdjustments
            .Where(m => m.GradeId == gradeId && m.SizeId == sizeId)
            .OrderByDescending(m => m.MovementId).FirstOrDefault();
        if (last is null) return null;

        string who = last.CreatedBy is { } id && _reportUserNames.TryGetValue(id, out string? name)
            ? name : "someone";
        int total = _reportAdjustments.Count(m => m.GradeId == gradeId && m.SizeId == sizeId);

        return $"Manually adjusted {(last.WeightCt < 0 ? "-" : "+")}{Math.Abs(last.WeightCt):N4} ct"
             + $"\n{who} · {last.MovementDate:dd MMM yyyy}"
             + $"\n{last.Reason}"
             + (total > 1 ? $"\n\n{total} manual adjustments on this bucket in total." : "");
    }

    private async void LoadStockReport_Click(object sender, RoutedEventArgs e) => await LoadStockReportAsync();

    private void ResetStockReport_Click(object sender, RoutedEventArgs e)
    {
        foreach (var box in _reportSizeBoxes)
            box.IsChecked = ReportSizeOnByDefault((string)box.Tag);
        ReportHideEmpty.IsChecked = false;
        // Back to the shipped default, which is ON -- the sheet this screen mirrors prints 0.00.
        ReportShowZeros.IsChecked = true;
        SyncReportAllSizes();
        _ = LoadStockReportAsync();
    }

    /// <summary>
    /// One read, then the whole sheet is drawn from it. Called on tab entry and on Refresh.
    /// </summary>
    private async Task LoadStockReportAsync()
    {
        var rows = await Read(Repo.StockAsync);
        if (rows is null) return;
        _reportRows = rows;

        // The SALE movements this sheet's own SALES figure is summed from.
        //
        // They used to be fetched only by LoadStock_Click, on the STOCK page, and _sales starts
        // empty -- so the report's SALES read "0.00 ct - Nothing sold from these buckets" until
        // somebody happened to open a different screen first, and TOTAL STOCK (on-hand plus sales)
        // silently came out short by the same amount. A figure this page prints is a figure this
        // page has to read.
        //
        // Not fatal if it fails: the sheet itself is drawn from `rows`, so the table and ON HAND
        // still stand. Only the two derived figures would be understated, and Read has already put
        // the failure in the status bar.
        if (await Read(Repo.SalesAsync) is { } sold) _sales = sold;

        // The size tick-boxes are built once, from the catalogue, so a size added to the Size
        // Master appears here without this report knowing about it in advance.
        //
        // The columns the last imported sheet carried. Read once; an import sets them directly, so
        // the answer never depends on a config write having been permitted.
        if (_reportSheetSizes is null)
        {
            var config = await Read(Repo.ConfigAsync) ?? [];

            _reportSheetSizes = config.TryGetValue(StockSheetSizesKey, out string? saved)
                                && saved.Length > 0
                ? [.. saved.Split(',', StringSplitOptions.RemoveEmptyEntries)]
                : [];

            _reportSheetRates = ParseSheetRates(config.GetValueOrDefault(StockSheetRatesKey, ""));
        }

        // Built AFTER the read, not before, so a sieve that HOLDS STOCK is ticked whether or not
        // it is one of the two defaults. It used to open showing 1/5 and 1/4 and nothing else, so
        // a sheet importing 21.35 ct under -6.5 landed in a report that did not draw that column
        // -- carats on the screen's own figures, invisible, with a tick-box the reader had to
        // know to look for. A report that hides imported stock by default is not a report.
        if (_reportSizeBoxes.Count == 0) BuildReportSizeBoxes();

        // Which buckets carry a manual correction, and who made it. Failing to read these must not
        // stop the report drawing -- the figures are the point, the markers are the annotation.
        _reportAdjustments = await Read(Repo.ManualAdjustmentsAsync) ?? [];
        if (_reportAdjustments.Count > 0 && _reportUserNames.Count == 0)
            _reportUserNames = (await Read(Repo.UsersAsync) ?? [])
                .ToDictionary(u => u.Id, u => u.FullName ?? "someone");

        ApplyReportMode();
        DrawStockReport();
    }

    /// The master tick above the sieve list. Two-state on purpose: a partial selection shows it
    /// clear, so the first click always means "give me the whole sheet" and the second "none of it".
    private CheckBox? _reportAllSizes;

    /// <summary>
    /// Whether a sieve opens ticked: the columns the last imported sheet CARRIED, plus anything
    /// holding stock.
    ///
    /// Both halves earn their place, and each was tried alone first.
    ///
    /// A hardcoded pair (0.2 and 0.25) was ticked whether or not the sheet had them, and a sieve
    /// the sheet DID carry stayed off unless it happened to be one of the two.
    ///
    /// "What holds stock" then hid 1/4 -- a column the sheet prints at 0.00 down its whole length,
    /// which is a real column and a real answer. Absence of stock is not absence of a column.
    ///
    /// The second half stays as the floor under both: whatever else is or is not ticked, a sieve
    /// holding carats always has somewhere to be shown. A stock report may not hide stock.
    ///
    /// With no sheet recorded and no stock anywhere, everything is ticked -- a report opening on
    /// no columns at all reads as broken rather than as "there is nothing here".
    /// </summary>
    private bool ReportSizeOnByDefault(string code) =>
        ReportSizeTicked(code, _reportSheetSizes ?? [],
                         [.. _reportRows.Where(r => r.LedgerCt != 0).Select(r => r.SizeCode)]);

    /// <summary>
    /// The printed rates as one config value: "grade,size,rate;grade,size,rate". Codes carry no
    /// comma or semicolon, so nothing needs escaping and the value stays readable in the table.
    /// </summary>
    public static string FormatSheetRates(
        IEnumerable<KeyValuePair<(string GradeCode, string SizeCode), decimal>> rates) =>
        string.Join(";", rates.Select(r => $"{r.Key.GradeCode},{r.Key.SizeCode},{r.Value}"));

    /// <summary>
    /// The inverse. A malformed entry is dropped rather than throwing: this decorates a report,
    /// and a config value somebody has edited by hand must not be able to stop it drawing.
    /// </summary>
    public static Dictionary<(string Grade, string Size), decimal> ParseSheetRates(string? saved)
    {
        var rates = new Dictionary<(string, string), decimal>();
        foreach (string entry in (saved ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split(',');
            if (parts.Length == 3
                && decimal.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    out decimal rate))
                rates[(parts[0], parts[1])] = rate;
        }
        return rates;
    }

    /// <param name="sheetSizes">The columns the last imported sheet carried. May be empty.</param>
    /// <param name="withStock">The sieves currently holding carats.</param>
    public static bool ReportSizeTicked(string code, IReadOnlyCollection<string> sheetSizes,
                                        IReadOnlyCollection<string> withStock) =>
        sheetSizes.Count > 0
            ? sheetSizes.Contains(code) || withStock.Contains(code)
            : withStock.Count == 0 || withStock.Contains(code);

    /// <summary>
    /// The app's own checkbox, in the app's own ink.
    ///
    /// Without this a code-made CheckBox gets WPF's stock template, which pins its foreground to
    /// the SYSTEM control text colour -- a colour that knows nothing about which theme is loaded.
    /// The size row came out in a grey that matched neither the light nor the dark palette, and
    /// looked right only on "All sizes" because that one is SemiBold and read as deliberate.
    ///
    /// FieldCheckBox's own default foreground is TextMutedBrush, which measures 2.98:1 on the dark
    /// surface -- under the floor for a label somebody has to click. So the ink is set over it, the
    /// same way Hide empty buckets on the Stock tab already does (see MainWindow.xaml).
    /// </summary>
    private static CheckBox ReportBox(CheckBox box)
    {
        box.SetResourceReference(FrameworkElement.StyleProperty, "FieldCheckBox");
        box.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        return box;
    }

    private void BuildReportSizeBoxes()
    {
        var panel = new List<UIElement>();

        // Eleven sieves, and the office asking for the whole sheet had eleven clicks and eleven
        // redraws to get there.
        _reportAllSizes = ReportBox(new CheckBox
        {
            Content = "All sizes",
            Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
        });
        _reportAllSizes.Click += ReportAllSizes_Click;
        panel.Add(_reportAllSizes);

        foreach (var size in Catalogue.AllSizes)
        {
            var box = ReportBox(new CheckBox
            {
                Content = SizeLabel(size.Code),
                Tag = size.Code,
                IsChecked = ReportSizeOnByDefault(size.Code),
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            box.Click += LoadStockReport_Click;
            _reportSizeBoxes.Add(box);
            panel.Add(box);
        }
        ReportSizes.ItemsSource = panel;
    }

    private void ReportAllSizes_Click(object sender, RoutedEventArgs e)
    {
        foreach (var box in _reportSizeBoxes) box.IsChecked = _reportAllSizes!.IsChecked == true;
        _ = LoadStockReportAsync();
    }

    /// Keeps the master tick honest after any other route changes the selection — an individual
    /// box, Reset, or the defaults on first load.
    private void SyncReportAllSizes()
    {
        if (_reportAllSizes is null || _reportSizeBoxes.Count == 0) return;
        _reportAllSizes.IsChecked = _reportSizeBoxes.All(b => b.IsChecked == true);
    }

    // The sheet's rules: heavy on the outside and between size groups, hairline between a
    // size's own Weight and Rate. Matching the printed page is the whole point of this screen.
    private static readonly Thickness CellRule = new(0.6);
    private static readonly Thickness GroupRule = new(1.6, 0.6, 0.6, 0.6);

    /// <param name="figure">
    /// True for a cell holding a NUMBER, which is what decides the tabular font.
    ///
    /// It used to be decided by alignment -- right-aligned meant mono -- and the grade name is
    /// printed at BOTH edges of the sheet, right-aligned on the left edge and left-aligned on the
    /// right. So the same grade came out in two different typefaces on the two ends of its own
    /// row, and the two columns of names did not line up with each other.
    /// </param>
    /// <param name="secondary">
    /// True for the rate half of each pair. Every figure on this sheet was drawn at one strength,
    /// so a screen of 300 cells offered the eye no way in: the carats a person came to read and the
    /// rate beside them competed exactly. The rate is reference, not the holding -- it steps back a
    /// tier rather than shrinking, because the sizes on this screen are the printed sheet's and are
    /// not ours to change.
    /// </param>
    private Border ReportCell(string text, bool bold, TextAlignment align, Thickness rule,
                              bool header = false, bool figure = false, bool secondary = false)
    {
        var block = new TextBlock
        {
            Text = text,
            TextAlignment = align,
            FontSize = header ? 14 : 13,
            Margin = new Thickness(8, 4, 8, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        // The figures use the app's tabular font so columns of numbers line up, exactly as they
        // do everywhere else money and carats are shown.
        if (figure || header)
            block.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");

        // Weight from the theme, not from a constant. Cascadia at 13px is legible on white at
        // Normal and thin on #0E1116, so the dark dictionary asks for Medium -- and it is a
        // resource reference rather than a check of ThemeManager.Current so that toggling the
        // theme re-weights a report already on screen. The cells are built once, at load.
        if (bold) block.FontWeight = FontWeights.Bold;
        else block.SetResourceReference(TextBlock.FontWeightProperty, "ReportFigureWeight");

        block.SetResourceReference(TextBlock.ForegroundProperty,
                                   secondary ? "TextMutedBrush" : "TextBrush");

        var cell = new Border { BorderThickness = rule, Child = block, MinWidth = 74 };

        // NOT TextBrush. Ruling the grid in the text colour is right on white -- the sheet is meant
        // to read as the printed one it replaces -- and wrong on black, where it puts #E6EBF2 lines
        // around #E6EBF2 figures and the grid competes with the numbers it is there to separate.
        cell.SetResourceReference(Border.BorderBrushProperty, "ReportRuleBrush");
        return cell;
    }

    /// <summary>
    /// Gives the mouse wheel back to the page, and gives Shift+wheel to the sheet.
    ///
    /// The sheet has its own ScrollViewer so its horizontal bar sits under the last row rather
    /// than at the foot of the window. That scroller has vertical scrolling DISABLED -- the page
    /// outside it does the up and down -- but a ScrollViewer marks every wheel event handled
    /// whether or not it can act on one. So the wheel died the moment the pointer was over the
    /// table, which is most of the screen, and the report could only be scrolled by dragging the
    /// bar. Nothing was broken about the bar; the wheel simply never reached it.
    ///
    /// Preview, because the swallowing happens in the ScrollViewer's own bubbling handler and the
    /// only way in front of it is the tunnelling pass.
    /// </summary>
    private void ReportSheet_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Shift is the usual convention for sideways, and sideways is the one thing this pane
        // actually does. Twenty-four columns is a long way to drag a scrollbar.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            ReportSheetScroll.ScrollToHorizontalOffset(
                ReportSheetScroll.HorizontalOffset - e.Delta);
            e.Handled = true;
            return;
        }

        // Re-raised on the page rather than scrolled by hand, so the wheel keeps the speed the
        // system is set to instead of a number picked here.
        e.Handled = true;
        ReportScroll.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = ReportScroll,
        });
    }

    private void DrawStockReport()
    {
        ReportTable.Children.Clear();
        ReportTable.ColumnDefinitions.Clear();
        ReportTable.RowDefinitions.Clear();

        SyncReportAllSizes();

        var sizes = _reportSizeBoxes.Where(b => b.IsChecked == true)
            .Select(b => (string)b.Tag)
            .OrderBy(code => Catalogue.AllSizes.FirstOrDefault(z => z.Code == code)?.SizeId ?? long.MaxValue)
            .ToList();

        // The printed sheet's order first, then anything else the catalogue holds.
        //
        // Union with whatever HOLDS STOCK, because Repo.GradesAsync filters active = true and a
        // grade can be deactivated while its carats are still on the books. Catalogue.AllSizes has
        // kept retired SIEVES for exactly this reason since it was written; grades never got the
        // same treatment, so switching a grade off deleted it from this report and left its stock
        // counted in the total with no row to account for it. A picker may hide an inactive grade.
        // A stock report may not.
        var grades = Catalogue.Grades.Select(g => g.Code)
            .Concat(_reportRows.Where(r => r.LedgerCt != 0).Select(r => r.GradeCode))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(code =>
            {
                int i = Array.IndexOf(ReportGradeOrder, code);
                return i >= 0 ? i : int.MaxValue;
            })
            .ToList();

        // LedgerCt, not BalanceCt. This sheet is a ledger document: TOTAL - SALES = ON HAND, and
        // reserved carats are in neither term, so reading the available balance made the moment a
        // sales entry held 5 ct look like 5 ct that had ceased to exist. A parcel spoken for is
        // still in the safe, and a stock take would still count it. See 0044.
        decimal Weight(string grade, string size) =>
            _reportRows.FirstOrDefault(r => r.GradeCode == grade && r.SizeCode == size)?.LedgerCt ?? 0m;
        // Cost where there is stock to have a cost; otherwise what the sheet printed.
        //
        // The two are different facts and the fallback only ever fires where the first cannot
        // exist -- a bucket at 0.00 has no value and no weight, so no average. Printing a blank
        // there was not neutral: the paper shows a rate in every cell of the 1/4 column, and a
        // screen that shows nothing where the paper shows a figure reads as an import that lost it.
        decimal Rate(string grade, string size) =>
            _reportRows.FirstOrDefault(r => r.GradeCode == grade && r.SizeCode == size)
                is { LedgerCt: not 0m, AvgCost: { } cost }
                    ? cost
                    : SheetRate(grade, size);

        decimal SheetRate(string grade, string size) =>
            _reportSheetRates?.GetValueOrDefault((grade, size)) ?? 0m;

        if (ReportHideEmpty.IsChecked == true)
            grades = grades.Where(g => sizes.Any(s => Weight(g, s) != 0m)).ToList();

        bool anything = sizes.Count > 0 && grades.Count > 0;
        ReportEmpty.Visibility = anything ? Visibility.Collapsed : Visibility.Visible;
        ReportTitleBox.Visibility = anything ? Visibility.Visible : Visibility.Collapsed;
        if (!anything)
        {
            ReportChip.Text = "Nothing to show";
            // Zeroed, not left as they were. A band still reading 145 ct over an empty sheet is a
            // figure describing a set that is no longer on screen, which is worse than a dash.
            ReportTotalStock.Text = ReportSales.Text = ReportOnHand.Text = "0.00 ct";
            ReportSalesNote.Text = "Nothing sold from these buckets";
            ReportBandScope.Text = "";
            return;
        }

        // 1 label + 2 per size + 1 label, as the sheet prints it.
        int columns = 1 + sizes.Count * 2 + 1;
        for (int c = 0; c < columns; c++)
            ReportTable.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        int row = 0;
        void AddRow() { ReportTable.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); }

        // ── header: the size name centred over its Weight/Rate pair ──────────
        AddRow();
        ReportTable.Children.Add(Place(ReportCell("", false, TextAlignment.Center, CellRule), row, 0));
        for (int i = 0; i < sizes.Count; i++)
        {
            var head = ReportCell(SizeLabel(sizes[i]), true, TextAlignment.Center, GroupRule, header: true);
            System.Windows.Controls.Grid.SetColumnSpan(head, 2);
            ReportTable.Children.Add(Place(head, row, 1 + i * 2));
        }
        ReportTable.Children.Add(Place(ReportCell("", false, TextAlignment.Center, CellRule), row, columns - 1));

        // ── the blank band the sheet leaves under the header ─────────────────
        row++; AddRow();
        for (int c = 0; c < columns; c++)
        {
            var rule = c > 0 && c < columns - 1 && (c - 1) % 2 == 0 ? GroupRule : CellRule;
            ReportTable.Children.Add(Place(ReportCell("", false, TextAlignment.Center, rule), row, c));
        }

        // ── one row per grade ────────────────────────────────────────────────
        foreach (string grade in grades)
        {
            row++; AddRow();
            ReportTable.Children.Add(
                Place(ReportCell(GradeLabel(grade), false, TextAlignment.Right, CellRule,
                                 figure: false), row, 0));

            for (int i = 0; i < sizes.Count; i++)
            {
                string sizeCode = sizes[i];
                decimal w = Weight(grade, sizeCode);
                decimal r = Rate(grade, sizeCode);

                // Blank by default: a zero reads as "measured and found to be nothing" rather than
                // "nothing here", and 27 grades by 11 sizes is 297 cells to bury 67 real figures in.
                //
                // Some clients' sheets print 0.00 in every cell all the same, and checking the
                // screen against one of those is easier when the two agree -- hence the toggle.
                var weightCell = ReportCell(ReportFigure(w, ReportShowingZeros), false,
                                            TextAlignment.Right, GroupRule, figure: true);

                // A bucket someone has corrected by hand is marked and explained, so a reader can
                // tell a figure the ledger derived from one a person typed.
                var ids = _reportRows.FirstOrDefault(x => x.GradeCode == grade && x.SizeCode == sizeCode);
                if (ids is not null && ReportAdjustmentNote(ids.GradeId, ids.SizeId) is { } note)
                {
                    weightCell.ToolTip = note;
                    if (weightCell.Child is TextBlock marked)
                    {
                        marked.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
                        marked.FontStyle = FontStyles.Italic;
                    }
                }

                var rateCell = ReportCell(ReportFigure(r, ReportShowingZeros, "N0"), false,
                                          TextAlignment.Right, CellRule, figure: true, secondary: true);

                if (ReportEditing)
                {
                    // BOTH halves of the pair open the same correction. The two figures are one
                    // fact — carats and what they are worth — and clicking the rate to be told
                    // nothing happens is how the left cell came to look like the only one that
                    // works. The rate is not separately editable, because it is averaged from the
                    // ledger rather than stored; the dialog says so when only it is changed.
                    string capturedGrade = grade;
                    decimal capturedWeight = w;
                    decimal capturedRate = r;

                    foreach (var cell in new[] { weightCell, rateCell })
                    {
                        cell.Cursor = System.Windows.Input.Cursors.Hand;
                        cell.SetResourceReference(Border.BackgroundProperty, "Surface2Brush");
                        cell.ToolTip ??= "Click to correct this holding.";
                        cell.MouseLeftButtonUp += async (_, _) =>
                            await AdjustFromReportAsync(capturedGrade, sizeCode, capturedWeight,
                                                        capturedRate);
                    }
                }

                ReportTable.Children.Add(Place(weightCell, row, 1 + i * 2));
                ReportTable.Children.Add(Place(rateCell, row, 2 + i * 2));
            }

            ReportTable.Children.Add(
                Place(ReportCell(GradeLabel(grade), false, TextAlignment.Left, CellRule), row, columns - 1));
        }

        DrawStockReportTotals(sizes, grades, Weight, Rate);
    }

    private static UIElement Place(UIElement element, int row, int column)
    {
        System.Windows.Controls.Grid.SetRow(element, row);
        System.Windows.Controls.Grid.SetColumn(element, column);
        return element;
    }

    /// <summary>
    /// One size column's subtotal rate, by the same two rules the printed sheet uses.
    ///
    /// WITH WEIGHT it is value over weight -- a weighted average, so 53.97 ct at 30,000 counts for
    /// more than 0.16 ct at 19,000. That half was always right.
    ///
    /// WITHOUT WEIGHT there is nothing to weight it BY, and the sheet falls back to the plain
    /// average of the rates printed down the column. This half was missing: the app divided by a
    /// zero weight, gave up, and printed 0 where the client's 1/4 column states 34,921 -- the mean
    /// of the nineteen rates above it, to the rupee.
    ///
    /// A bucket the sheet never priced is not part of the average. Counting it as a zero would
    /// drag the figure down by however many grades the office happens not to trade.
    /// </summary>
    public static string SubtotalRate(decimal weight, decimal value,
                                      IEnumerable<decimal> printedRates, bool showZeros)
    {
        if (weight != 0m) return (value / weight).ToString("N0");

        var priced = printedRates.Where(r => r > 0m).ToList();
        return priced.Count == 0
            ? ReportFigure(0m, showZeros, "N0")
            : Math.Round(priced.Average(), 0, MidpointRounding.AwayFromZero).ToString("N0");
    }

    /// <summary>
    /// The two totals lines, drawn outside the ruled box exactly as the sheet prints them: a
    /// per-size weight and rate, then one grand total.
    ///
    /// The rate is weighted by carats, not averaged: averaging the rates would give a bucket
    /// holding 0.16 ct the same say as one holding 53.97 ct.
    /// </summary>
    private void DrawStockReportTotals(List<string> sizes, List<string> grades,
                                       Func<string, string, decimal> weight,
                                       Func<string, string, decimal> rate)
    {
        // Drawn into ReportTable itself, not into a grid of its own.
        //
        // They used to be a second Grid with its own Auto columns, and Auto sizes a column to ITS
        // OWN content: the table's first column is as wide as "Unknown Grade", the totals grid's
        // was as wide as "TOTAL", and every column after that was offset by the difference. So the
        // sheet printed 6.75 under a column that was not -6.5, which is worse than printing
        // nothing -- a reader has no way to see that the figure is in the wrong place.
        //
        // One grid, one set of columns, and the two rows cannot drift apart again. The look is
        // unchanged: the sheet prints its totals outside the ruled box, and these cells still
        // carry no border.
        int firstRow = ReportTable.RowDefinitions.Count;
        ReportTable.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });
        ReportTable.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        ReportTable.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });
        ReportTable.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        int subtotalRow = firstRow + 1;
        int totalRow = firstRow + 3;

        var none = new Thickness(0);
        decimal grandWeight = 0m, grandValue = 0m;

        for (int i = 0; i < sizes.Count; i++)
        {
            decimal w = grades.Sum(g => weight(g, sizes[i]));
            decimal v = grades.Sum(g => weight(g, sizes[i]) * rate(g, sizes[i]));
            grandWeight += w;
            grandValue += v;

            ReportTable.Children.Add(Place(
                ReportCell(ReportFigure(w, ReportShowingZeros), true, TextAlignment.Right, none,
                           figure: true), subtotalRow, 1 + i * 2));
            ReportTable.Children.Add(Place(
                ReportCell(SubtotalRate(w, v, grades.Select(g => rate(g, sizes[i])),
                                        ReportShowingZeros),
                           true, TextAlignment.Right, none, figure: true, secondary: true),
                subtotalRow, 2 + i * 2));
        }

        // The grand total sits under the first size's own pair, which is where the sheet prints it.
        ReportTable.Children.Add(Place(
            ReportCell("TOTAL", true, TextAlignment.Right, none), totalRow, 0));
        ReportTable.Children.Add(Place(
            ReportCell(grandWeight.ToString("N2"), true, TextAlignment.Right, none,
                       figure: true), totalRow, 1));
        ReportTable.Children.Add(Place(
            ReportCell(grandWeight == 0m ? ReportFigure(0m, ReportShowingZeros, "N0")
                                         : (grandValue / grandWeight).ToString("N0"),
                       true, TextAlignment.Right, none, figure: true, secondary: true), totalRow, 2));

        // Total Stock . Sales . On Hand, for exactly the grades and sieves this sheet is showing.
        //
        // grandWeight is the sheet's own total, so ON HAND is the figure the table below adds up to
        // and cannot drift from it. TOTAL is derived as on-hand plus sales, which is what makes
        // TOTAL - SALES = ON HAND true whatever the filters are set to, rather than an identity
        // that happens to hold until someone rejects a parcel.
        //
        // Filtered on the SAME grade and sieve lists the sheet was drawn from, so narrowing the
        // sieves narrows all three figures together.
        var shownGrades = grades.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shownSizes = sizes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        decimal soldCt = _sales
            .Where(m => shownGrades.Contains(m.GradeCode) && shownSizes.Contains(m.SizeCode))
            .Sum(m => m.WeightCt);

        ReportOnHand.Text = $"{grandWeight:N2} ct";
        ReportSales.Text = $"{soldCt:N2} ct";
        ReportTotalStock.Text = $"{grandWeight + soldCt:N2} ct";
        ReportSalesNote.Text = soldCt == 0m ? "Nothing sold from these buckets"
                                            : "Carats sold to date";
        ReportBandScope.Text = $"{grades.Count} grade(s) × {sizes.Count} sieve(s)";

        ReportChip.Text = $"{grandWeight:N2} ct";
        ReportSubtitle.Text = $"{grades.Count} grade(s) × {sizes.Count} size(s) · "
                            + $"{string.Join(", ", sizes.Select(SizeLabel))} · as at {DateTime.Now:dd MMM yyyy}";
    }

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

        // The page is also the app's refresh point for these values: this runs after every save,
        // so a new money_precision or low-stock threshold applies without a restart.
        Policy.Apply(config);

        _settings = config.OrderBy(c => c.Key)
                          .Select(c => SettingItem.From(c.Key, c.Value))
                          .ToList();

        SettingChip.Text = $"{_settings.Count:N0} setting{(_settings.Count == 1 ? "" : "s")}";
        SettingSubtitle.Text = "Policies and thresholds · owner only";
        ShowSettingCards();
    }

    /// <summary>
    /// Groups the settings into their cards. No query runs — the rows are already in memory.
    /// </summary>
    private void ShowSettingCards()
    {
        if (SettingCards is null) return;

        // A card with nothing in it is dropped rather than left as an empty frame.
        SettingCards.ItemsSource = SettingItem.Categories
            .Select(c => new
            {
                Title = c,
                Items = _settings.Where(x => x.Category == c).ToList(),
            })
            .Where(g => g.Items.Count > 0)
            .Select(g => new
            {
                g.Title,
                Caption = $"{g.Items.Count} setting{(g.Items.Count == 1 ? "" : "s")}",
                g.Items,
            })
            .ToList();

        SettingEmpty.Visibility = _settings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        ShowDirtyCount();
    }

    private void ShowDirtyCount()
    {
        if (SettingDirty is null) return;

        int dirty = _settings.Count(x => x.IsDirty);
        SettingDirty.Text = dirty == 0 ? "" : Plural(dirty, "unsaved change");
    }

    private void DiscardSettings_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _settings) item.Value = item.OriginalValue;
        ShowSettingCards();
        Say("Changes discarded", ok: true);
    }

    /// <summary>
    /// Scrolls a rejected setting into view and puts the caret in its box. The search box used to
    /// do this by filtering the page down to the offending row — which worked, but only because a
    /// search box happened to be there. Naming the row directly is what was actually meant, and it
    /// leaves the rest of the page visible so the user can see what else is unsaved.
    /// </summary>
    private void ScrollToSetting(SettingItem target)
    {
        // The rows live inside a nested ItemsControl per card, so the container has to be found by
        // DataContext rather than by index.
        foreach (var box in Descendants<TextBox>(SettingCards))
            if (ReferenceEquals(box.DataContext, target))
            {
                box.BringIntoView();
                box.Focus();
                box.SelectAll();
                return;
            }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null) yield break;

        // Templates are expanded lazily, so a card that has never been scrolled to has no visual
        // children yet. Without this the search finds nothing and the caret goes nowhere.
        if (root is FrameworkElement fe) fe.UpdateLayout();

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) yield return hit;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
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

        // Checked before anything is written. The box is plain text and every value used to go
        // through as typed, so "abc" could land in carat_precision — a setting every screen reads
        // and none can parse. Named per key, because "invalid" over a page of settings is useless.
        if (dirty.FirstOrDefault(x => x.Problem is not null) is { } bad)
        {
            Say($"{bad.Label}: {bad.Problem}");
            ScrollToSetting(bad);                // bring the offending row into view
            return;
        }

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
    /// <summary>
    /// The catalogue an import may resolve against: retired sieves taken out.
    ///
    /// Applied to the READ rather than to the alias map, because the same list also builds the
    /// code-to-id dictionary the write uses. Filtering only the map would let a sheet be parsed
    /// against one catalogue and posted against another — the exact mismatch the "read against the
    /// catalogue as it stands right now" note further down exists to prevent.
    ///
    /// A sheet still printing a retired column is not silently dropped: the heading resolves to
    /// nothing, so the reader's own checksum fails and names it. That refusal is the point.
    /// </summary>
    private static List<SizeBucket> ImportableSizes(List<SizeBucket> sizes) =>
        [.. sizes.Where(s => s.Active)];

    private async void ImportExcel_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the sale workbook to import",
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            CheckFileExists = true,
        };
        if (picker.ShowDialog(this) != true) return;

        // Said before the file is read, not after a read fails somewhere in the middle.
        //
        // A sale workbook names buyers and brokers; sales_line needs their ids. A name that has no
        // row yet has no id, and only the database can create one — the last import made 12 buyers
        // and 8 brokers this way. So unlike stock, this cannot be validated, queued and applied
        // later: the payload would be missing the very ids it is built around. Inventing them
        // client-side would collide between machines and lose a buyer on reconnect.
        //
        // Fixing this properly means replace_imported_sales resolving names itself — a migration.
        // Until then, saying so plainly beats a generic transport error and a page that does
        // nothing, which is what this looked like.
        if (!Db.IsOnline)
        {
            AppDialog.Info(this,
                title: "No connection",
                headline: "Sales import needs a connection",
                subhead: System.IO.Path.GetFileName(picker.FileName),
                facts: [],
                listTitle: "Why this one cannot wait offline",
                bullets:
                [
                    "The workbook names buyers and brokers. The database has to create any that are "
                    + "new and hand back their ids before the invoices can reference them.",
                    "Stock import has no such need — grades and sizes already exist, so it can be "
                    + "saved offline and applied later.",
                ],
                note: "Nothing has been read or changed. Try again once the connection is back.");
            return;
        }

        ImportPlan? plan;
        List<Grade>? grades = null;
        List<SizeBucket>? sizes = null;

        // Read as a local function so the workbook can be parsed again against a catalogue that has
        // just gained a size, exactly as the two stock importers do.
        async Task<ImportPlan?> ReadWorkbookAsync()
        {
            using (Busy(ImportExcel, "Checking…", ImportExcel))
            {
                grades = await Read(Repo.GradesAsync);
                sizes = await Read(Repo.SizesAsync);
                if (grades is null || sizes is null) return null;
                sizes = ImportableSizes(sizes);

                // Grades carry aliases for the spellings the legacy workbooks use ("II" → "NO II").
                // Sizes need no stored aliases: their variants are notation (MDM-004), so a rule covers
                // every catalogue code without anything to seed or maintain.
                var gradeMap = SaleFileImport.AliasMap(grades.Select(g => (g.Code, g.Aliases)));
                var sizeMap = SaleFileImport.SizeAliasMap(sizes.Select(s => s.Code));

                // Parsing a large sheet is CPU work; off the UI thread so the window stays alive.
                return await Task.Run(() => SaleFileImport.Plan(picker.FileName, gradeMap, sizeMap));
            }
        }

        plan = await ReadWorkbookAsync();
        if (plan is null) return;

        // A grade the workbook names and the catalogue lacks. Same offer the two stock importers
        // make, and for the same reason -- and offered BEFORE the sizes, because a file naming both
        // should ask once for each rather than sending the user round the loop twice.
        if (plan.UnknownGrades.Count > 0)
        {
            if (!await AddMissingGradesAsync(plan.UnknownGrades,
                                             System.IO.Path.GetFileName(picker.FileName), ImportExcel,
                [
                    ("Read correctly", $"{plan.LineCount:N0} line(s) on {plan.Invoices.Count:N0} invoice(s)"),
                    (plan.UnknownGrades.Count == 1 ? "The name" : "The names",
                        string.Join(", ", plan.UnknownGrades.Take(4))),
                    ("Rows affected", $"{plan.SkippedRows:N0} skipped"),
                ]))
            {
                Say("Import cancelled — a grade in the workbook is not in the catalogue");
                return;
            }

            plan = await ReadWorkbookAsync();
            if (plan is null) return;
        }

        // Same rule as the stock workbook: an unknown sieve is a skipped row here, not a failed
        // file, so refusing to add it has to stop the import explicitly. An invoice imported with
        // its size rows dropped is a sale whose weight no longer matches the paper.
        if (plan.UnknownSizes.Count > 0)
        {
            if (!await AddMissingSizesAsync(plan.UnknownSizes,
                                            System.IO.Path.GetFileName(picker.FileName), ImportExcel))
            {
                Say("Import cancelled — a sieve size in the workbook is not in the catalogue");
                return;
            }

            plan = await ReadWorkbookAsync();
            if (plan is null) return;
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

        if (!ConfirmImport(plan, picker.FileName, existing.Count)) { Say("Import cancelled"); return; }

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

        var notes = ImportNotes(plan, result);

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
        // The import creates buyers for names the database did not have. Without this the Sales
        // entry picker and the dashboard's buyer filter keep the list loaded at startup, so a buyer
        // that now owns invoices cannot be chosen until the app is restarted.
        await LoadPartiesAsync();

        Say($"Imported {result.Invoices:N0} invoices · {result.Lines:N0} lines · " +
            $"{result.Receipts:N0} receipts", ok: true);
    }

    /// <summary>
    /// The "this destroys what is there" dialog. Skipped rows are named before the user commits,
    /// never after — silently importing 1,369 of 1,437 rows and reporting only the good news is
    /// how a migration loses data unnoticed.
    /// </summary>
    // ── Stock import ────────────────────────────────────────────────────────
    // Deliberately its own handler rather than a branch inside the sales one. They read different
    // sheets, map different columns and replace different tables; the only thing they share is the
    // shape of the flow — check, confirm, write, report.

    private async void ImportStock_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the stock workbook to import",
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            CheckFileExists = true,
        };
        if (picker.ShowDialog(this) != true) return;

        StockImportPlan? plan;
        List<Grade>? grades = null;
        List<SizeBucket>? sizes = null;

        // A local function, not a straight-line block, so the workbook can be read a SECOND time
        // against a catalogue that has just gained a size. Same shape as the PDF importer above:
        // a plan read against one catalogue must never be imported against another.
        async Task<StockImportPlan?> ReadWorkbookAsync()
        {
            using (Busy(ImportStock, "Checking…", ImportStock))
            {
                // Offline, fall back to the catalogue already in memory — it was loaded at sign-in and
                // grades and sizes do not change while a connection is down. Without this the handler
                // returned here on the failed read and never reached the offline branch below, so
                // picking a file simply did nothing: no dialog, no queue, no explanation.
                if (Db.IsOnline)
                {
                    grades = await Read(Repo.GradesAsync);
                    sizes = await Read(Repo.SizesAsync);
                }
                else
                {
                    grades = [.. Catalogue.Grades];
                    sizes = [.. Catalogue.ActiveSizes];
                }

                if (grades is null || sizes is null) return null;
                sizes = ImportableSizes(sizes);

                // Never signed in online this session, so there is no cached catalogue to validate
                // against. Saying so beats validating every row against an empty list and reporting
                // that the workbook names grades the catalogue does not have.
                if (grades.Count == 0 || sizes.Count == 0)
                {
                    Say("The catalogue has not loaded yet — connect once before importing offline");
                    return null;
                }

                // Grade aliases are the same ones the sale import uses. Sizes need their own map: the
                // stock sheet writes them bare ("6.5", "11") where the sale file signs them.
                var gradeMap = SaleFileImport.AliasMap(grades.Select(g => (g.Code, g.Aliases)));
                var sizeMap = StockFileImport.SizeMap(sizes.Select(s => s.Code));

                return await Task.Run(() => StockFileImport.Plan(picker.FileName, gradeMap, sizeMap));
            }
        }

        plan = await ReadWorkbookAsync();
        if (plan is null) return;

        // A grade the workbook names and the catalogue lacks. Same offer the printed sheet gets,
        // and for the same reason: the office adds a grade when the office adds a grade, and a
        // workbook naming one is a catalogue that has not caught up rather than a bad file.
        if (plan.Problems.Count == 1 && plan.UnplacedRows > 0
            && await AddMissingGradesAsync(plan, System.IO.Path.GetFileName(picker.FileName),
                                           ImportStock))
        {
            plan = await ReadWorkbookAsync();
            if (plan is null) return;
        }

        // A sieve size the workbook names and the catalogue lacks. Unlike the PDF reader, this one
        // does NOT invalidate the plan over it -- the row is an exception and the rest of the file
        // still imports -- so saying no has to stop the import HERE. Letting it run would post a
        // stock position with a whole sieve silently missing from it, which is worse than no import.
        if (plan.UnknownSizes.Count > 0)
        {
            if (!await AddMissingSizesAsync(plan.UnknownSizes,
                                            System.IO.Path.GetFileName(picker.FileName), ImportStock))
            {
                Say("Stock import cancelled — a sieve size in the workbook is not in the catalogue");
                return;
            }

            plan = await ReadWorkbookAsync();
            if (plan is null) return;
        }

        if (!plan.IsValid)
        {
            ImportStatus.Text = "";
            MessageBox.Show(this, "This file cannot be imported:\n\n" + StockFileImport.ProblemText(plan),
                "Cannot import this file", MessageBoxButton.OK, MessageBoxImage.Warning);
            Say($"Stock import cancelled · {plan.Problems.Count} problem(s) in the file");
            return;
        }

        // The workbook carries no date anywhere — not one cell uses a date format — so the snapshot
        // date has to be asked for. Defaulting it silently would stamp today onto a January count.
        var answer = AppFormDialog.Show(this, "Stock date", "What date is this stock position?",
            "The workbook holds no date of its own, so the parcels need one.",
            [new FormFieldSpec("Date (dd-MM-yyyy)", DateTime.Today.ToString("dd-MM-yyyy"))],
            v => ParseStockDate(v[0]) is null ? "Enter a date as dd-MM-yyyy." : null,
            "Continue");
        if (answer is null) { Say("Stock import cancelled"); return; }
        if (ParseStockDate(answer[0]) is not { } asAt) { Say("Stock import cancelled"); return; }

        // Offline: everything up to here worked from the catalogue already in memory, so the file
        // is fully validated and only the write is impossible. Park it rather than refuse it —
        // refusing means the count is re-keyed later from a workbook nobody can find.
        var existing = Db.IsOnline ? await Read(Repo.ImportedStockIdsAsync) : null;
        if (existing is null && Db.IsOnline) return;

        var gradeIds = grades.ToDictionary(g => g.Code, g => g.GradeId, StringComparer.Ordinal);
        var sizeIds = sizes.ToDictionary(s => s.Code, s => s.SizeId, StringComparer.Ordinal);

        if (existing is null)
        {
            await QueueStockImportAsync(plan, picker.FileName, asAt, gradeIds, sizeIds);
            return;
        }

        if (!ConfirmStockImport(plan, picker.FileName, existing.Count, asAt))
        { Say("Stock import cancelled"); return; }

        StockImportResult? result;
        var progressDialog = AppProgressDialog.Start(this, "Importing stock, please wait…");
        try
        {
            result = await Read(() => Repo.ImportStockAsync(plan.Rows, asAt, gradeIds, sizeIds,
                                                            progressDialog.Progress));
        }
        finally
        {
            progressDialog.Finish();
        }

        ImportStatus.Text = "";
        if (result is null)
        {
            // The database's own words, in full. A refusal can name several buckets and the reason
            // for each; the bar alone showed the first line and cut the rest.
            ShowWriteRefusal("The stock could not be replaced",
                             System.IO.Path.GetFileName(picker.FileName));
            return;
        }

        List<string> notes = [];
        if (plan.SkippedRows > 0)
            notes.AddRange(StockFileImport.ExceptionText(plan)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimStart(' ', '•').Trim()));

        // "Carats" and "Stock value" used to be the labels on these two figures, and they are not
        // the position — they are the sum of what the FILE held. A replace deletes only movements
        // with ref_type = 'stock_import' (0018), so hand intakes, sales, rejections and conversions
        // all survive it. On any database that has traded, this dialog and the Stock page therefore
        // disagree, and the dialog was the one being read as the answer.
        //
        // Both numbers are now shown side by side, each named. Showing only the workbook's invited
        // the misreading; showing only the position would hide whether the file landed. Together
        // they answer the two different questions a user actually has, and the gap between them is
        // the trading that happened since — which is information, not a discrepancy.
        bool everythingLanded = notes.Count == 0;

        var position = await CurrentStockAsync();

        // Grouped as two blocks: what the FILE held, then what the LEDGER now holds. The counts sit
        // beside their own carats so "62" and "63" are read as answers to two different questions
        // rather than as a discrepancy. result.Parcels is the count the database confirmed writing
        // (ImportStockAsync throws if it differs from the rows sent), so it is both "what the
        // workbook held" and "what landed" — one number, not two.
        List<(string, string)> facts =
        [
            ("Buckets in this workbook", $"{result.Parcels:N0}"),
            ("Carats in this workbook", $"{result.TotalCarats:N4}"),
            ("Value in this workbook", Money.Short(result.TotalValue)),
        ];

        // Read back, not recomputed: the same v_stock_position the Stock page reads, so the two
        // screens cannot drift. Omitted rather than guessed if the read fails — the import itself
        // has already committed and must not be reported as failed over a follow-up query.
        if (position is { } p)
        {
            facts.Add(("Current stock buckets", $"{p.Buckets:N0}"));
            facts.Add(("Current stock position", $"{p.Carats:N4} ct"));
            facts.Add(("Current stock value", Money.Short(p.Value)));
        }

        facts.Add(("Previous parcels replaced", $"{result.ReplacedParcels:N0}"));

        if (position is { } q)
        {
            int bucketGap = q.Buckets - result.Parcels;
            decimal caratGap = q.Carats - result.TotalCarats;

            // One sentence covering whichever of the two actually differs, rather than two notes
            // saying the same thing about the same cause.
            List<string> differs = [];
            if (bucketGap != 0) differs.Add($"{Math.Abs(bucketGap):N0} bucket(s)");
            if (Math.Abs(caratGap) >= 0.0001m) differs.Add($"{caratGap:+#,##0.0000;-#,##0.0000} ct");

            if (differs.Count > 0)
                notes.Add($"Current stock differs from this workbook by {string.Join(" and ", differs)}. "
                        + "The difference is caused by hand intakes, sales, conversions, or stock "
                        + "adjustments recorded after the import.");
        }

        notes.Add("Only previously imported parcels were replaced. Hand intakes, sales and "
                + "adjustments are untouched.");

        AppDialog.Info(this,
            title: "Stock import complete",
            headline: "Stock import complete",
            subhead: $"{System.IO.Path.GetFileName(picker.FileName)} · as at {asAt:dd MMM yyyy}",
            facts: facts,
            listTitle: "Worth knowing",
            bullets: notes,
            // Kept: this line prints only when UnplacedRows and SkippedRows are both zero, which is
            // the one assurance the dialog gives that no holding was quietly dropped.
            note: everythingLanded ? "Every holding in the workbook was imported." : null);

        // Same reason as the sale import: the catalogue on screen predates the write.
        await LoadPartiesAsync();

        Say($"Stock imported · {result.Parcels:N0} parcel(s), {result.TotalCarats:N2} ct", ok: true);
    }

    // ── Stock import · the printed sheet ────────────────────────────────────────
    //
    // Same destination as the workbook importer, and deliberately the same plumbing: the PDF reader
    // hands back a StockImportPlan of StockRows, which is exactly what StockFileImport produces, so
    // the validation, the confirmation, the write and the report afterwards are all the existing
    // ones. The only genuinely new decisions are what a print says (PdfStockImport) and whether this
    // sheet REPLACES the imported position or ADDS to it.
    //
    // That second question is the whole reason this is not a file-type filter on the Excel button.
    // A workbook is a full count and can only replace. A printed sheet is often one parcel lot, and
    // two of those both stand.

    private async void ImportStockPdf_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the printed stock sheet to import",
            Filter = "Stock sheet (*.pdf)|*.pdf",
            CheckFileExists = true,
        };
        if (picker.ShowDialog(this) != true) return;

        // Unlike the workbook path there is no offline queue here. A queued replace can be checked
        // against a fingerprint before it replays (0018); a queued APPEND cannot — replaying one
        // after somebody else has imported would add a second copy of the same carats, and there is
        // no way to tell that apart from a genuine second lot. Refusing beats guessing.
        if (!Db.IsOnline)
        {
            Say("A stock sheet can only be imported while connected");
            return;
        }

        // The catalogue this sheet was read against. Held outside the reader because the import
        // needs the same one to turn codes into ids — a sheet read against a catalogue that has
        // since gained a grade would look up an id that was not there when it was parsed.
        List<Grade>? grades = null;
        List<SizeBucket>? sizes = null;

        // Read against the catalogue as it stands right now. Called twice when grades are added in
        // between, because the second read has to see them.
        async Task<StockImportPlan?> ReadSheetAsync()
        {
            using (Busy(ImportStockPdf, "Reading…", ImportStockPdf))
            {
                grades = await Read(Repo.GradesAsync);
                sizes = await Read(Repo.SizesAsync);
                if (grades is null || sizes is null) return null;
                sizes = ImportableSizes(sizes);

                var gradeLabels = PdfGradeLabelMap(grades);
                var sizeLabels = PdfSizeLabelMap(sizes.Select(s => s.Code));

                return await Task.Run(() => PdfStockFile.Plan(picker.FileName, gradeLabels, sizeLabels));
            }
        }

        var plan = await ReadSheetAsync();
        if (plan is null) return;

        // A sheet whose ONLY fault is a name the catalogue has never seen is not a bad sheet — it
        // is a catalogue that has not caught up. The office adds a grade when the office adds a
        // grade, and three sheets in a row have now needed one. Offering it here is the difference
        // between a stock count that loads and a stock count that waits on a developer.
        //
        // The refusal itself stays exactly as strict: nothing is created until the names are on
        // screen and somebody has agreed to them.
        if (plan.Problems.Count == 1 && plan.UnplacedRows > 0
            && await AddMissingGradesAsync(plan, System.IO.Path.GetFileName(picker.FileName),
                                          ImportStockPdf))
        {
            plan = await ReadSheetAsync();
            if (plan is null) return;
        }

        // A column headed with a sieve size the catalogue has never seen. Same reasoning as the
        // grades above, and the same shape: shown first, created only on a yes.
        //
        // NOT created silently, though the database function would allow it. The heading that
        // started this work — "1/6" where another copy of the same sheet said "14+" — was a
        // RELABELLED column, not a new sieve. Creating it unasked would have split one position
        // across two buckets and reported success.
        // Saying no deliberately falls through rather than returning: the plan is already invalid
        // (UnknownSizes is only ever populated by a FAILED checksum), so the branch below names the
        // heading and both totals. A bare "cancelled" would throw that explanation away.
        if (plan.UnknownSizes.Count > 0
            && await AddMissingSizesAsync(plan.UnknownSizes,
                                          System.IO.Path.GetFileName(picker.FileName), ImportStockPdf))
        {
            plan = await ReadSheetAsync();
            if (plan is null) return;
        }

        if (!plan.IsValid)
        {
            ImportStatus.Text = "";
            // Named, because the usual cause is the wrong file: a folder of invoices, receipts and
            // reports with two stock sheets in it. "This sheet cannot be imported" over a dialog
            // that never says WHICH left nothing to check against.
            string name = System.IO.Path.GetFileName(picker.FileName);
            MessageBox.Show(this, $"{name} cannot be imported:\n\n" + PdfStockFile.ProblemText(plan),
                "Cannot import this sheet", MessageBoxButton.OK, MessageBoxImage.Warning);
            Say($"Stock import cancelled · {name}");
            return;
        }

        // Every figure, shown back before anything is written. The reader reconciles to the sheet's
        // own printed totals, but reconciling to the wrong sheet is still the wrong sheet.
        if (!PdfStockPreview.Confirm(this, plan, System.IO.Path.GetFileName(picker.FileName),
                                     SizeLabel))
        { Say("Stock import cancelled"); return; }

        // The print carries no date either — the same reason the workbook is asked.
        var answer = AppFormDialog.Show(this, "Stock date", "What date is this stock position?",
            "The sheet holds no date of its own, so the parcels need one.",
            [new FormFieldSpec("Date (dd-MM-yyyy)", DateTime.Today.ToString("dd-MM-yyyy"))],
            v => ParseStockDate(v[0]) is null ? "Enter a date as dd-MM-yyyy." : null,
            "Continue");
        if (answer is null) { Say("Stock import cancelled"); return; }
        if (ParseStockDate(answer[0]) is not { } asAt) { Say("Stock import cancelled"); return; }

        // Null means the list could not be read, which before 0027 is applied is what happens: the
        // view does not exist. Stopping here rather than carrying on is the whole point — the next
        // dialog offers to DELETE what is already imported, and it cannot honestly offer that
        // against a list it could not read. Treating the failure as "nothing is imported" showed
        // "Already imported: nothing" over a ledger holding 2,950 ct.
        // The "!" is about the GENERIC, not the value: Read<T> is constrained to a non-nullable
        // reference type, and this is the one repo call that answers null on purpose. The null
        // still comes through — Read returns what it was given — and is handled below.
        var batches = await Read<List<VStockImportBatch>>(
            async () => (await Repo.StockImportBatchesAsync())!);
        if (batches is null)
        {
            AppDialog.Info(this,
                title: "Not available yet",
                headline: "This database cannot record stock imports in batches yet",
                subhead: System.IO.Path.GetFileName(picker.FileName),
                facts: [("Sheet read", $"{plan.Rows.Count:N0} holding(s), {plan.TotalCarats:N4} ct")],
                listTitle: "What to do",
                bullets:
                [
                    "Apply migration 0027 (supabase/migrations/0027_stock_import_batches.sql).",
                    "Then import this sheet again — it has already been read and checked, so nothing "
                        + "about it needs redoing.",
                ],
                note: "Nothing was written. The stock position is exactly as it was.");
            Say("Stock import cancelled — migration 0027 has not been applied");
            return;
        }

        var choice = AskReplaceOrAdd(plan, System.IO.Path.GetFileName(picker.FileName), asAt, batches);
        if (choice == AppDialog.Answer.Cancel) { Say("Stock import cancelled"); return; }

        bool replace = choice == AppDialog.Answer.Primary;

        // Unreachable: a plan exists only if both reads succeeded. Stated so the compiler sees it.
        if (grades is null || sizes is null) return;

        var gradeIds = grades.ToDictionary(g => g.Code, g => g.GradeId, StringComparer.Ordinal);
        var sizeIds = sizes.ToDictionary(s => s.Code, s => s.SizeId, StringComparer.Ordinal);

        StockImportResult? result;
        var progressDialog = AppProgressDialog.Start(this, "Importing the stock sheet, please wait…");
        try
        {
            result = await Read(() => Repo.ImportStockAsync(plan.Rows, asAt, gradeIds, sizeIds,
                                                            progressDialog.Progress,
                                                            new Repo.StockImportMode(replace, Guid.NewGuid(), "pdf")));
        }
        finally
        {
            progressDialog.Finish();
        }

        ImportStatus.Text = "";
        // No message of our OWN here -- Read() has already put the database's words in the bar, and
        // a generic "stock import failed" was overwriting them, leaving the user looking at a
        // failure with its reason deleted a moment earlier. What this adds is room: the bar holds
        // one line, and a refusal naming three buckets needs more than one.
        if (result is null)
        {
            ShowWriteRefusal("The stock could not be replaced",
                             System.IO.Path.GetFileName(picker.FileName));
            return;
        }

        await RememberSheetSizesAsync(plan);

        await ReportStockImportAsync(result, plan, System.IO.Path.GetFileName(picker.FileName),
                                     asAt, replace);
    }

    /// <summary>
    /// Records the sieve columns the sheet just imported carried, so the Stock report opens on the
    /// same columns the office is holding rather than on whatever happens to hold carats.
    ///
    /// Written AFTER the import has succeeded, and its own failure is swallowed: this is how the
    /// report chooses its default ticks, and a config table the user may not write is a reason for
    /// the report to fall back, never a reason to report a completed import as failed.
    /// </summary>
    private async Task RememberSheetSizesAsync(StockImportPlan plan)
    {
        if (plan.SizeOrder.Count == 0) return;

        // Held in memory FIRST, so this session shows the right columns whether or not the write
        // below is allowed. app_config is what carries the answer to the next session and to the
        // other desks; it is not what this one depends on.
        _reportSheetSizes = [.. plan.SizeOrder];
        _reportSheetRates = plan.PrintedRates.ToDictionary(r => (r.Key.GradeCode, r.Key.SizeCode),
                                                           r => r.Value);

        // Every tick discarded, including any the user set by hand. A sheet has just replaced the
        // position and the columns it carried are the answer now; keeping the last sheet's
        // selection would show the new stock through the old sheet's window.
        _reportSizeBoxes.Clear();

        try
        {
            await Repo.SetConfigAsync(StockSheetSizesKey, string.Join(",", plan.SizeOrder));
            await Repo.SetConfigAsync(StockSheetRatesKey, FormatSheetRates(plan.PrintedRates));
        }
        catch (Exception) { /* this desk is already right; the next one falls back to stock */ }
    }

    /// <summary>
    /// The one question a printed sheet raises that a workbook does not: does this count replace
    /// what is already imported, or stand beside it?
    ///
    /// Shown every time, and with what is currently imported listed by batch — the choice is
    /// between two real outcomes, and "replace" throws away carats somebody counted. Neither
    /// answer is the safe default, so neither is preselected by being the only button.
    /// </summary>
    /// <summary>
    /// Offers to add the grade names a sheet uses and the catalogue does not have. True when at
    /// least one was added, which means the sheet is worth reading again.
    ///
    /// Named as printed, deliberately. Nobody here knows what "MIX" or "FL" stands for, and a
    /// guessed display name is wrong on every screen for as long as the grade exists. Renaming one
    /// later is a single update; unpicking a wrong name that has been read as correct for a month
    /// is not.
    /// </summary>
    private Task<bool> AddMissingGradesAsync(StockImportPlan plan, string fileName,
                                             System.Windows.Controls.Button busyOn) =>
        AddMissingGradesAsync(plan.UnplacedLabels, fileName, busyOn,
        [
            ("Read correctly", $"{plan.Rows.Count:N0} line(s), {plan.TotalCarats:N2} ct"),
            (plan.UnplacedLabels.Count == 1 ? "Held by that grade" : "Held by those grades",
                $"{plan.UnplacedRows:N0} line(s), {plan.UnplacedCarats:N2} ct"),
            ("Sheet total", $"{plan.TotalCarats + plan.UnplacedCarats:N2} ct"),
        ]);

    /// <summary>
    /// Offers to create the grade names a file uses and the catalogue does not have.
    ///
    /// Shared by the printed sheet, the stock workbook and the sale workbook, because the answer is
    /// the same in all three: a name the catalogue has never seen is a catalogue that has not caught
    /// up, not a bad file. The office adds a grade when the office adds a grade.
    ///
    /// The name created is the one the FILE writes -- "1 BB", not "No. 1 Bottom Black". Existing
    /// grades are never touched or renamed; this only ever adds.
    /// </summary>
    private async Task<bool> AddMissingGradesAsync(IReadOnlyList<string> missing, string fileName,
                                                   System.Windows.Controls.Button busyOn,
                                                   IReadOnlyList<(string Label, string Value)> facts)
    {
        bool one = missing.Count == 1;

        // Worded and buttoned like the sieve-size offer next to it, because it is the same
        // question about the same catalogue and the office should not have to read two dialogs to
        // learn one thing. "New grade found" states the fact first; the question follows it.
        if (!AppDialog.Confirm(this,
                title: one ? "New grade found" : "New grades found",
                headline: one ? $"\"{missing[0]}\" — add this grade?"
                              : $"{missing.Count:N0} new grades on this sheet — add them?",
                subhead: fileName,
                facts: facts,
                emphasis: "Check the name against the paper before saying yes. A grade mistyped on "
                        + "the sheet becomes a second grade holding real stock, and the position "
                        + "then splits across two rows that are the same goods. Adding takes "
                        + "nothing away: the sheet is read again and you still see every figure "
                        + "before anything is imported.",
                listTitle: one ? "The name, exactly as the sheet prints it"
                               : "The names, exactly as the sheet prints them",
                bullets: missing,
                primaryText: "Yes",
                secondaryText: "No"))
            return false;

        var failures = new List<string>();
        using (Busy(busyOn, "Adding…", busyOn))
            foreach (string label in missing)
            {
                // The label as the SHEET prints it -- "1 BB", not "No. 1 Bottom Black". A grade
                // invented under a tidier name would not match the next sheet, which prints the
                // short form, and the office would be asked to add it a second time.
                var wrote = await Repo.AddGradeAsync(label);
                if (!wrote.Ok) failures.Add($"{label} — {wrote.Failure}");
            }

        if (failures.Count > 0)
        {
            AppDialog.Info(this,
                title: "Could not add the grade(s)",
                headline: "The catalogue was not changed",
                subhead: fileName,
                facts: [],
                listTitle: "What the database said",
                bullets: failures,
                note: "Nothing was imported and the stock position is exactly as it was. "
                    + "If this says the function does not exist, apply migration 0030 "
                    + "(supabase/migrations/0030_add_grade.sql).");
            return false;
        }

        // The catalogue moved, so the pickers built from it are stale — and the second read of the
        // sheet goes through Repo.GradesAsync anyway, which will now see the new rows.
        await ReloadCatalogueAsync();
        Say($"Added {missing.Count:N0} grade(s) · {string.Join(", ", missing)}");
        return true;
    }

    /// <summary>
    /// Offers to add the sieve sizes a sheet is headed with that the catalogue does not have. True
    /// when at least one was added, which means the sheet is worth reading again.
    ///
    /// The database decides what "does not have" means, not this dialog: add_size (0034) matches on
    /// the normalised sieve key, so answering yes to "6.5+" when "+6.5" already exists returns the
    /// existing row instead of creating a twin. That check cannot live here — the app would have to
    /// reimplement it and the two would drift.
    /// </summary>
    private async Task<bool> AddMissingSizesAsync(IReadOnlyList<UnknownSize> missing, string fileName,
                                                  System.Windows.Controls.Button busyOn)
    {
        bool one = missing.Count == 1;

        // A sieve the office retired is a different proposition from one nobody has ever seen, and
        // saying "has never been seen before" about a size somebody deliberately switched off last
        // week reads as the app having lost track. Retirement is not permanent -- a sheet turning
        // up next quarter with that column on it is a normal event -- so the offer is the same, but
        // it is honest about which act it is: this one brings a row back, keeping its id and its
        // history, rather than creating anything.
        bool anyRetired = missing.Any(m => Catalogue.IsRetiredSize(m.Label));
        bool allRetired = missing.All(m => Catalogue.IsRetiredSize(m.Label));

        if (!AppDialog.Confirm(this,
                title: one ? "A sieve size this file uses is not in the catalogue"
                           : "Sieve sizes this file uses are not in the catalogue",
                headline: allRetired
                    ? one ? $"\"{missing[0].Label}\" was retired and this file still uses it"
                          : $"{missing.Count:N0} sieve sizes were retired and this file still uses them"
                    : one ? $"\"{missing[0].Label}\" has never been seen before"
                          : $"{missing.Count:N0} sieve sizes have never been seen before",
                subhead: fileName,
                facts: [.. missing.Select(m => (m.Label,
                    $"{m.Carats:N2} ct stands under it"
                    + (Catalogue.IsRetiredSize(m.Label) ? " · retired, would be brought back" : "")))],
                emphasis: anyRetired
                    ? "A retired sieve comes back as the SAME row, so its old stock and history stay "
                    + "attached and nothing is duplicated. Check the heading against the paper all "
                    + "the same: bringing one back means the desk can book new stock to it again."
                    : "Check it against the paper before saying yes. A size relabelled by "
                    + "mistake becomes a second size holding real stock, and the position "
                    + "then splits across two buckets that are the same sieve.",
                listTitle: null,
                bullets: null,
                primaryText: allRetired
                    ? one ? "Bring it back and carry on" : "Bring them back and carry on"
                    : one ? "Add it and carry on" : "Add them and carry on",
                secondaryText: "Cancel"))
            return false;

        var failures = new List<string>();
        using (Busy(busyOn, allRetired ? "Restoring…" : "Adding…", busyOn))
            foreach (var m in missing)
            {
                // The LABEL is what the file printed and what the dialog showed; the CODE is
                // what gets stored. "'+18" is the first and must never be the second.
                var wrote = await Repo.AddSizeAsync(StockFileImport.CleanCode(m.Label));
                if (!wrote.Ok) failures.Add($"{m.Label} — {wrote.Failure}");
            }

        if (failures.Count > 0)
        {
            AppDialog.Info(this,
                title: "Could not add the size(s)",
                headline: "The catalogue was not changed",
                subhead: fileName,
                facts: [],
                listTitle: "What the database said",
                bullets: failures,
                note: "Nothing was imported and the stock position is exactly as it was. "
                    + "If this says the function does not exist, apply migration 0034; if it says the sieve "
                    + "is retired and cannot take new stock, apply 0036, which lets a retired one "
                    + "be brought back "
                    + "(supabase/migrations/0034_add_size.sql).");
            return false;
        }

        await ReloadCatalogueAsync();
        Say($"{(allRetired ? "Restored" : "Added")} {missing.Count:N0} size(s) · "
            + string.Join(", ", missing.Select(m => m.Label)));
        return true;
    }

    private AppDialog.Answer AskReplaceOrAdd(StockImportPlan plan, string fileName, DateOnly asAt,
                                             IReadOnlyList<VStockImportBatch> batches)
    {
        decimal onHand = batches.Sum(b => b.Carats);

        List<(string, string)> facts =
        [
            ("This sheet", $"{plan.Rows.Count:N0} holding(s), {plan.TotalCarats:N4} ct"),
            ("As at", asAt.ToString("dd MMM yyyy")),
            ("Already imported", batches.Count == 0
                ? "nothing"
                : $"{batches.Count:N0} import(s), {onHand:N4} ct"),
        ];

        if (batches.Count > 0)
            facts.Add(("If both are kept", $"{onHand + plan.TotalCarats:N4} ct"));

        // Named, not counted: "2 imports" tells nobody which two, and the person choosing has to
        // recognise the one they are about to delete.
        List<string> bullets = batches
            .Take(6)
            .Select(b => $"{b.Source.ToUpperInvariant()} · {b.AsAt:dd MMM yyyy} · "
                       + $"{b.Parcels:N0} parcel(s), {b.Carats:N4} ct"
                       + (b.BatchId is null ? " · imported before batches were recorded" : ""))
            .ToList();
        if (batches.Count > 6) bullets.Add($"… and {batches.Count - 6:N0} more");

        return AppDialog.Choose(this,
            title: "Replace or add?",
            headline: "Replace the imported stock, or add this sheet to it?",
            subhead: fileName,
            facts: facts,
            emphasis: batches.Count == 0
                ? "Nothing has been imported yet, so both answers do the same thing here."
                : $"Removing the old data deletes {onHand:N4} ct of previously imported stock and "
                + "leaves only this sheet. Hand-entered intakes, sales, conversions and "
                + "adjustments are untouched either way.",
            listTitle: batches.Count == 0 ? null : "Currently imported",
            bullets: bullets,
            primaryText: "Remove old & import latest",
            tertiaryText: "Keep old & add latest",
            cancelText: "Cancel");
    }

    /// <summary>
    /// What an import landed, read back out of the ledger. Shared by both stock importers so the
    /// two cannot come to describe the same write differently.
    /// </summary>
    private async Task ReportStockImportAsync(StockImportResult result, StockImportPlan plan,
                                              string fileName, DateOnly asAt, bool replaced)
    {
        List<string> notes = [];
        if (plan.SkippedRows > 0)
            notes.AddRange(PdfStockFile.ProblemText(plan)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimStart(' ', '•').Trim()));

        var position = await CurrentStockAsync();

        List<(string, string)> facts =
        [
            ("Buckets in this sheet", $"{result.Parcels:N0}"),
            ("Carats in this sheet", $"{result.TotalCarats:N4}"),
            ("Value in this sheet", Money.Short(result.TotalValue)),
        ];

        if (position is { } p)
        {
            facts.Add(("Current stock buckets", $"{p.Buckets:N0}"));
            facts.Add(("Current stock position", $"{p.Carats:N4} ct"));
            facts.Add(("Current stock value", Money.Short(p.Value)));
        }

        facts.Add(("Previous parcels replaced", $"{result.ReplacedParcels:N0}"));
        // The id the database stamped, so this import can be found again in rough_intake.
        if (result.BatchId is { } id) facts.Add(("Import batch", id.ToString()[..8]));

        notes.Add(replaced
            ? "Everything previously imported was replaced by this sheet. Hand intakes, sales and "
              + "adjustments are untouched."
            : "This sheet was added to what was already imported; nothing was removed. Both imports "
              + "now count towards the position, and each is tagged with its own batch.");

        AppDialog.Info(this,
            title: "Stock import complete",
            headline: replaced ? "Stock replaced" : "Stock added",
            subhead: $"{fileName} · as at {asAt:dd MMM yyyy}",
            facts: facts,
            listTitle: "Worth knowing",
            bullets: notes,
            note: plan.SkippedRows == 0 ? "Every holding on the sheet was imported." : null);

        // Everything on screen predates the write. The Stock page and the Stock report are both
        // repainted rather than left to be refreshed by hand — an import that lands while the page
        // still shows the old position is exactly the confusion the breakdown card exists to end.
        // Only if they have been opened: _stock is empty until the page is first loaded, and
        // populating it here would leave the page looking loaded without its filters ever having run.
        await LoadPartiesAsync();
        if (_stock.Count > 0) LoadStock_Click(this, new RoutedEventArgs());
        if (_reportRows.Count > 0) await LoadStockReportAsync();

        Say($"Stock {(replaced ? "replaced" : "added")} · {result.Parcels:N0} parcel(s), "
          + $"{result.TotalCarats:N2} ct", ok: true);
    }

    /// <summary>
    /// The position as the ledger now holds it, for the import report. Reads v_stock_position —
    /// the same view the Stock page reads — so the figure in the dialog and the figure on the page
    /// cannot disagree. Null if the read fails: the import has already committed by this point,
    /// and a follow-up query that times out must not turn a successful import into an error.
    ///
    /// Buckets counts BalanceCt > 0, which is exactly what the page's "Buckets in stock" tile
    /// counts (StockKpiActive). Counting != 0 here instead would report a different 63 from the
    /// one on screen, which is the confusion this dialog exists to end.
    /// </summary>
    private static async Task<(int Buckets, decimal Carats, decimal Value)?> CurrentStockAsync()
    {
        try
        {
            var rows = await Repo.StockAsync();
            return (rows.Count(r => r.BalanceCt > 0),
                    rows.Sum(r => r.BalanceCt),
                    rows.Sum(r => r.StockValue));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// The workbook is Indian-dated and so is the app; the invariant forms are accepted too so a
    /// pasted ISO date is not rejected for being unambiguous.
    private static DateOnly? ParseStockDate(string text) =>
        DateOnly.TryParseExact(text.Trim(), ["dd-MM-yyyy", "d-M-yyyy", "dd/MM/yyyy", "yyyy-MM-dd"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>
    /// Parks a validated stock import until the network is back.
    ///
    /// The guard is the fingerprint of the imported position as this machine last saw it. A stock
    /// import is a REPLACE — it deletes every movement the previous import wrote — so replaying one
    /// blind after a reconnect could erase a colleague's newer import. Replay compares the guard
    /// against the server and holds the entry rather than overwrite anything it cannot account for.
    ///
    /// _lastStockFingerprint is whatever the last successful online read saw. If this session has
    /// never been online it is null, and the queue then refuses to auto-apply at all — "unknown" is
    /// not "unchanged", and the difference is somebody's day of work.
    /// </summary>
    private async Task QueueStockImportAsync(StockImportPlan plan, string path, DateOnly asAt,
                                             Dictionary<string, long> gradeIds,
                                             Dictionary<string, long> sizeIds)
    {
        if (!AppDialog.Confirm(this,
                title: "No connection",
                headline: "Save this import until the connection is back?",
                subhead: System.IO.Path.GetFileName(path),
                facts:
                [
                    ("Parcels", $"{plan.Rows.Count:N0}"),
                    ("Carats in this workbook", $"{plan.TotalCarats:N4}"),
                    ("As at", asAt.ToString("dd MMM yyyy")),
                ],
                emphasis: "The file has already been checked in full. Nothing is written until the "
                        + "connection returns, and it is applied only if no one else has imported "
                        + "in the meantime.",
                listTitle: null, bullets: null,
                primaryText: "Save for later", secondaryText: "Cancel"))
        { Say("Stock import cancelled"); return; }

        string payload = Repo.StockImportPayload(plan.Rows, asAt, gradeIds, sizeIds);

        await Outbox.EnqueueAsync("rpc/replace_imported_stock", payload, Guid.NewGuid(),
                                  guard: _lastStockFingerprint);

        await ShowPendingAsync();
        AppDialog.Info(this,
            title: "Saved for later",
            headline: "Import saved — waiting for a connection",
            subhead: System.IO.Path.GetFileName(path),
            facts:
            [
                ("Parcels waiting", $"{plan.Rows.Count:N0}"),
                ("Carats", $"{plan.TotalCarats:N4}"),
                ("As at", asAt.ToString("dd MMM yyyy")),
            ],
            listTitle: "What happens next",
            bullets:
            [
                "It is applied automatically as soon as the app is online again.",
                "Nothing has changed in the database yet — the Stock page still shows the old position.",
                "If someone else imports before the connection returns, this one is held rather than "
                + "applied, and the header will say so.",
            ],
            note: "Saved on this machine. It survives closing the app.");
    }

    private bool ConfirmStockImport(StockImportPlan plan, string path, int existingCount, DateOnly asAt)
    {
        // Says what is NOT deleted as well as what is. "the movements that came with them" was
        // true but easy to read as "the movements" — and a replace leaves every hand intake, sale
        // and adjustment exactly where it was.
        string scope = existingCount == 0
            ? "There is no previously imported stock to replace."
            : $"This will DELETE the {existingCount:N0} previously imported stock parcel(s) and only "
              + "the movements that came with them. Hand intakes, sales and adjustments are kept.";

        var skipped = plan.SkippedRows == 0
            ? null
            : StockFileImport.ExceptionText(plan)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimStart(' ', '•').Trim());

        return AppDialog.Confirm(this,
            title: "Replace imported stock",
            headline: "Replace imported stock?",
            subhead: $"{System.IO.Path.GetFileName(path)} · checked and ready",
            facts:
            [
                ("Parcels", $"{plan.Rows.Count:N0}"),
                ("Grades", $"{plan.GradeCount:N0}"),
                // Same wording as the completion dialog: these are the workbook's figures, and on
                // a book that has traded they are not the position the Stock page will show.
                ("Carats in this workbook", $"{plan.TotalCarats:N4}"),
                ("Value in this workbook", Money.Short(plan.TotalValue)),
                ("As at", asAt.ToString("dd MMM yyyy")),
            ],
            emphasis: scope,
            listTitle: skipped is null ? null : $"{plan.SkippedRows:N0} holding(s) will be skipped",
            bullets: skipped,
            primaryText: "Replace stock",
            secondaryText: "Cancel");
    }

    private bool ConfirmImport(ImportPlan plan, string path, int existingCount)
    {
        string scope = existingCount == 0
            ? "There is no previous import to replace."
            : $"This will DELETE the {existingCount:N0} previously imported invoice(s), " +
              "along with their lines and receipts.";

        var skipped = plan.SkippedRows == 0
            ? null
            : SaleFileImport.ExceptionText(plan)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimStart(' ', '•').Trim());

        return AppDialog.Confirm(this,
            title: "Replace imported sales data",
            headline: "Replace imported sales data?",
            subhead: $"{System.IO.Path.GetFileName(path)} · checked and ready",
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
    }

    /// What the import did that the four headline figures do not say. Each note is its own line
    /// rather than a paragraph, because each is a separate thing the user may need to act on.
    private static List<string> ImportNotes(ImportPlan plan, ImportResult result)
    {
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

        // Capping a receipt is a change to what the file said, so it is reported rather than done
        // quietly. The residue is the workbook rounding Rec. Amt to whole rupees against an amount
        // Postgres recomputes from the lines.
        var capped = plan.Invoices.Where(i => i.Total > 0 && i.Received > i.Total).ToList();
        if (capped.Count > 0)
            notes.Add($"{capped.Count:N0} receipt(s) exceeded the invoice total by "
                      + $"{capped.Sum(i => i.Received - i.Total):N2} in all, and were capped at the "
                      + "amount owed so no invoice is left over-received.");

        return notes;
    }

    // ── plumbing ────────────────────────────────────────────────────────────

    /// Reads throw; a screen shows the reason instead of an unexplained empty grid.
    /// <param name="veil">
    /// False for reads that answer a filter rather than a page load. The veil is a full-screen
    /// spinner over the whole window: right for "the page is arriving", wrong for "you nudged a
    /// date", where it blanks the very figures being narrowed. Errors are still reported.
    /// </param>
    private async Task<T?> Read<T>(Func<Task<T>> read, bool veil = true) where T : class
    {
        if (veil) BeginBusy();
        try
        {
            var result = await read();
            Db.NoteTransport(null);
            _lastFailure = null;

            // A "cannot reach the server" is deliberately permanent — an empty grid with no message
            // reads as "there is no data" rather than "this did not load". But permanent meant it
            // outlived the reconnect: the network came back, the page filled with 10 settings, and
            // the red line underneath still said the server was unreachable. A read that succeeds
            // is proof the last transport failure is over, so it clears its own obituary.
            if (_transportFailed)
            {
                _transportFailed = false;
                if (Friendly.Translates(Status.Text)) { Status.Text = ""; Status.ToolTip = null; }

                // Put the header back to what it says on a healthy connection, so the pill does not
                // sit on "Offline" through a page that has just loaded.
                Pill(true, $"{Db.Active.Name} · {Catalogue.Grades.Count} grades · {_invoice.Buyers.Count} buyers");
            }
            return result;
        }
        catch (Exception ex)
        {
            // Tells Db what this proved. Nothing else did: IsOnline was set only by the sign-in
            // path, so a read failing on "no such host" left it reading true and every offline
            // branch in the app was unreachable.
            Db.NoteTransport(ex);
            _transportFailed = true;

            // The header said "Connected · 23 grades · 13 buyers" while the footer said the server
            // could not be reached — the pill was written once at sign-in and never revisited. Two
            // parts of one screen contradicting each other about the connection is worse than
            // either message alone.
            if (!Db.IsOnline) Pill(false, "Offline — changes are saved on this machine");

            // Kept whole, unflattened, for callers that can afford a dialog. The status bar gets
            // the same words on one line; a refusal naming several buckets deserves both.
            _lastFailure = Friendly.Message(ex.Message);

            Say(ex.Message);
            return null;
        }
        finally { if (veil) EndBusy(); }
    }

    /// Set when a read failed with a message that stays on screen, so the next successful read
    /// knows there is something stale to clear.
    private bool _transportFailed;

    /// The database's own words from the last failed read or write, with the PostgREST envelope
    /// already opened. Held because the status bar is one line and some refusals are a list.
    private string? _lastFailure;

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

    /// <summary>
    /// Signs out and returns to the login screen.
    ///
    /// RESTARTS the application rather than reusing this window, and that is deliberate. Whoever
    /// signs in next may be on the other database, or on this one with a different role -- and the
    /// navigation, the static catalogue and every tab's cache were all built for the person who is
    /// leaving. Reloading only the parts we remembered to reload is how a "sales" user ends up
    /// looking at the previous owner's Settings tab, or at the other company's figures.
    ///
    /// The same rule already covers a sign-in after an idle timeout. One rule, no exceptions, no
    /// list of caches to keep in step as the app grows.
    /// </summary>
    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        // Signing out mid-write would leave the desk with no window and no idea whether the write
        // landed. The database survives either way -- the person watching does not know that.
        if (_inFlight > 0)
        {
            Say("Something is still being saved. Wait for it to finish, then sign out.");
            return;
        }

        if (!AppDialog.Confirm(this,
                title: "Sign out",
                headline: $"Sign out of {Db.Active.Name}?",
                subhead: Db.CurrentUser?.FullName,
                facts: [],
                emphasis: "Solitaire Desk will close and reopen at the sign-in screen.",
                listTitle: null,
                bullets: null,
                primaryText: "Sign out",
                secondaryText: "Stay signed in"))
            return;

        await Db.SignOutAsync();

        if (Environment.ProcessPath is { } exe) System.Diagnostics.Process.Start(exe);
        Application.Current.Shutdown();
    }

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

    /// <summary>
    /// Reloads the page currently on screen. Used after an idle sign-out and a fresh sign-in: the
    /// window outlived the session, so everything on it was fetched with a token that is gone.
    /// </summary>
    /// <summary>
    /// Reload the catalogue and the parties without signing out.
    ///
    /// LoadPartiesAsync is the same call sign-in makes, so this is exactly a re-read rather than a
    /// second, slightly different refresh path that could drift from it.
    ///
    /// Offline is refused rather than attempted: a failed read here would leave the pickers holding
    /// whatever survived, which is worse than not having tried.
    /// </summary>
    private async void RefreshCatalogue_Click(object sender, RoutedEventArgs e)
    {
        // No !Db.IsOnline guard, for the reason above TrySyncAsync. This is the button somebody
        // presses when the wifi comes back, and refusing to try was how it answered -- so the flag
        // it was consulting could never be cleared by the one control that looks like it should
        // clear it. LoadPartiesAsync has its own try/catch and reports failure through Pill and
        // Say, so the guard was duplicating protection that already existed a layer down.

        // Busy rather than a bare IsEnabled: it swaps the caption too, so the button says what is
        // happening instead of just going dead. Sign out is disabled alongside it — signing out
        // mid-reload restarts the app over a read that is already in flight.
        using (Busy(RefreshCatalogue, "Refreshing…", RefreshCatalogue, SignOut))
        {
            await LoadPartiesAsync();

            // Every Grade and SizeBucket in memory is a NEW object after that call, and the entry
            // grid binds SelectedItem by reference — so a half-typed line would show empty Grade
            // and Size cells and read as lost work. Re-point them to the equivalent rows first.
            _invoice.CatalogueChanged();

            ReloadCurrentTab();
            Say($"Catalogue reloaded · {Catalogue.Grades.Count:N0} grades, "
                + $"{Catalogue.ActiveSizes.Count:N0} sizes, {_invoice.Buyers.Count:N0} buyers");
        }
    }

    /// <summary>
    /// The database's refusal, shown as a dialog rather than a line at the foot of the window.
    ///
    /// A stock import that is refused says WHY, and the why can be a list: which bucket, what the
    /// sheet brings, what has gone out against it. That does not fit on one line, and the one line
    /// it was getting cut the explanation off mid-sentence. The bar still carries the same words --
    /// this adds a place where all of them fit.
    ///
    /// The message is split the way the database writes it: the opening sentence is the headline,
    /// the "- bucket ..." lines become the list, and whatever closes it becomes the note. A message
    /// with no list at all still renders -- it simply has no bullets.
    /// </summary>
    private void ShowWriteRefusal(string title, string fileName)
    {
        if (string.IsNullOrWhiteSpace(_lastFailure)) return;

        var lines = _lastFailure.Split('\n')
                                .Select(l => l.Trim())
                                .Where(l => l.Length > 0)
                                .ToList();

        var bullets = lines.Where(l => l.StartsWith("- ", StringComparison.Ordinal))
                           .Select(l => l[2..].Trim())
                           .ToList();

        var prose = lines.Where(l => !l.StartsWith("- ", StringComparison.Ordinal)).ToList();

        AppDialog.Refused(this,
            title: title,
            headline: prose.Count > 0 ? prose[0] : "The import was refused",
            subhead: fileName,
            facts: [],
            listTitle: bullets.Count > 0 ? "Which buckets" : null,
            bullets: bullets.Count > 0 ? bullets : null,
            note: prose.Count > 1 ? string.Join(" ", prose.Skip(1)) : null);
    }

    private void ReloadCurrentTab()
    {
        _dashStock = null;
        _dashInvoices = null;
        if (Tabs.SelectedItem is TabItem tab) LoadTabData(tab.Header?.ToString() ?? "");
    }

    private void Tabs_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != Tabs || Tabs.SelectedItem is not TabItem tab) return;

        FieldError.ClearAll();

        string header = tab.Header?.ToString() ?? "";
        ScreenTitle.Text = header;
        ScreenSub.Text = ScreenSubtitles.GetValueOrDefault(header, "");
        Status.Text = "";                      // a message belongs to the screen that raised it

        LoadTabData(header);
    }

    /// <summary>
    /// Fetches whatever the named page shows. Separate from Tabs_Changed so the same reload can be
    /// triggered without a tab change — after an idle sign-out and a fresh sign-in, the window is
    /// still showing data fetched with a token that no longer exists.
    /// </summary>
    private void LoadTabData(string header)
    {
        var e = new RoutedEventArgs();

        switch (header)
        {
            // Without this the tab keeps no focus of its own, so WPF hands it to the first
            // focusable control on the page — the date picker — every time you come back. That lit
            // its focus ring on a field nobody had chosen, and put the caret on the one header
            // value that is already correct. Focus belongs where the work starts.
            case "Sales entry":
                FocusWhereEntryResumes();
                break;
            case "Invoices": LoadInvoices_Click(this, e); break;
            case "Receivables": LoadReceivables_Click(this, e); break;
            case "Stock": LoadStock_Click(this, e); break;
            case "Stock report": _ = LoadStockReportAsync(); break;
            // Was missing, and nobody noticed while the page carried a Refresh of its own. It does
            // not any more, so this is the only way its counters are reloaded.
            case "Intake & movements": RefreshMovements_Click(this, e); break;
            case "Master data": _ = LoadMasterAsync(); break;
            case "Dashboard": LoadDashboard_Click(this, e); break;
            case "Audit": LoadAudit_Click(this, e); break;
            case "Users": LoadUsers_Click(this, e); break;
            case "Settings": LoadSettings_Click(this, e); break;
        }
    }

    /// <summary>
    /// Where a keyboard-first invoice screen should resume: the buyer while there is not one, and
    /// the lines afterwards. Deferred to Input priority because the tab's content is still being
    /// realised when SelectionChanged fires, and focusing an unrealised element silently does
    /// nothing — leaving focus on the date picker, which is the whole problem.
    /// </summary>
    private void FocusWhereEntryResumes() => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
    {
        if (_invoice.BuyerId is null) BuyerPicker.Focus();
        else Grid.Focus();
    });

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

    /// <summary>
    /// The column a line message is about. Matched on the message rather than on a code, because
    /// the message is what the user is reading and the two must agree -- a mapping keyed off
    /// something invisible drifts the moment somebody rewords a sentence.
    /// </summary>
    private static string ColumnHeaderFor(string message) =>
        message.Contains("Grade is required") ? "Grade"
        // Move to stock's refusal. It is about the empty line rather than one bad value, and Grade
        // is where that line gets filled in from, so it is written there.
        : message.Contains("Pick a grade and size") ? "Grade"
        : message.Contains("Size is required") || message.Contains("does not use size") ? "Size"
        : message.Contains("Weight") ? "Weight"
        : message.Contains("Price") ? "Price/ct"
        : "";

    /// <summary>
    /// Put a line message on the cell it is about.
    ///
    /// A DataGrid virtualises its rows, so the cell to write under may not exist as a visual at
    /// all until the row is scrolled to and the layout has run -- hence ScrollIntoView and
    /// UpdateLayout before anything is looked up. When the cell still cannot be found the message
    /// goes to the status bar rather than nowhere: a validation message that silently fails to
    /// appear is worse than one in the wrong place.
    /// </summary>
    private void ShowLineError(SaleLine bad, string error)
    {
        ShowRow(bad);
        Grid.UpdateLayout();

        int column = Grid.Columns
            .Select((c, i) => (Header: c.Header?.ToString() ?? "", Index: i))
            .FirstOrDefault(c => c.Header == ColumnHeaderFor(error)).Index;

        Grid.CurrentCell = new DataGridCellInfo(bad, Grid.Columns[column]);
        Grid.Focus();

        var row = Grid.ItemContainerGenerator.ContainerFromItem(bad) as DataGridRow;
        var cells = VisualTree.FindChild<System.Windows.Controls.Primitives.DataGridCellsPresenter>(row);
        if (cells?.ItemContainerGenerator.ContainerFromIndex(column) is DataGridCell cell)
        {
            FieldError.Show(cell, error);
            _lineErrorCell = cell;
        }
        else
            Say(error);
    }

    /// The cell a line message is pinned to, while it is up.
    private DataGridCell? _lineErrorCell;

    /// <summary>
    /// Takes the line message down as soon as the grid moves on.
    ///
    /// Every other field clears its message the moment the user acts on it -- a TextBox on
    /// TextChanged, a picker on SelectionChanged (see FieldError.Show). A DataGridCell is neither,
    /// so a line message had nothing to clear it and stayed on screen. That alone would be untidy;
    /// what made it a bug is that a DataGrid RECYCLES its cell containers, and the adorner belongs
    /// to the container, not to the row. Fix the line and press Enter and the container the message
    /// was written on is re-bound to the blank line that Enter just added -- so the new line came up
    /// wearing the old line's "... is required", drawn over the cell being typed into.
    ///
    /// Moving the current cell is the grid's equivalent of typing in a box, and it covers the case
    /// above exactly: AddLine sets CurrentCell to the new row, which clears the message before that
    /// row is ever drawn.
    /// </summary>
    private void Grid_CurrentCellChanged(object? sender, EventArgs e)
    {
        if (_lineErrorCell is null) return;
        FieldError.Clear(_lineErrorCell);
        _lineErrorCell = null;
    }

    /// <summary>
    /// A validation message shown under the field it is about, with the caret sent there.
    ///
    /// This replaces Say for FIELD problems only. Say still carries everything that is about the
    /// screen rather than about one control -- "Import cancelled", "Memo saved", a transport
    /// failure -- because those belong to no field and a message pinned under an arbitrary box
    /// would be worse than one in the status bar.
    ///
    /// The message text is not touched on the way through: this changes where it appears, not what
    /// it says.
    /// </summary>
    private static void Field(Control target, string message)
    {
        FieldError.Show(target, message);
        target.Focus();
        if (target is TextBox box) box.SelectAll();
    }

    /// <summary>
    /// The status bar. Every message on every screen comes through here.
    ///
    /// THREAD-SAFE ON PURPOSE, and it is not a nicety. Most callers are the catch block of an
    /// `async void` click handler, and an exception escaping one of those does not fail the
    /// operation -- it ends the process, because there is no caller left to catch it. So a
    /// continuation that resumed anywhere but the UI thread turned a database error that had
    /// already been caught and handled into a crash, with the original error never shown.
    ///
    /// Marshalled rather than asserted: a caller reaching this line is already reporting a failure
    /// and must not be handed a second one. BeginInvoke rather than Invoke -- reporting is not
    /// worth blocking a background thread on, and Invoke from a thread the dispatcher is waiting on
    /// would deadlock.
    /// </summary>
    private void Say(string message, bool ok = false)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Say(message, ok)); return; }

        // Token brushes, not Brushes.SeaGreen/Firebrick — those don't follow the light/dark swap.
        Status.Foreground = (Brush)FindResource(ok ? "SuccessBrush" : "DangerBrush");

        // Every message on every screen passes through here, so this is the one place a database
        // failure has to be made readable. The original is kept on the tooltip — a support call
        // still needs the real text, it just should not be the first thing a user reads.
        string friendly = Friendly.Message(message);

        // The bar is a single line that does not wrap, and a database refusal can name several
        // buckets on several lines. Flattened with a separator so every one of them is still on
        // screen instead of the first line only -- and the unflattened text goes on the tooltip,
        // where line breaks survive and a support call can read the whole thing.
        Status.Text = System.Text.RegularExpressions.Regex.Replace(friendly, "[\r\n]+", "  ·  ").Trim();
        Status.ToolTip = friendly.Contains('\n') ? friendly
                       : Friendly.Translates(message) ? message
                       : null;

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
    // Nothing here ever changes its mind about being executable, so there is no event to raise and
    // nothing to subscribe. The accessors exist only because ICommand requires them.
    public event EventHandler? CanExecuteChanged
    {
        add { /* never raised — CanExecute is always true */ }
        remove { /* nothing was ever added */ }
    }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
}

/// ponytail: WPF has no input box. Twelve lines beats a dialog-library dependency.
// Prompt.Ask lived here: a hand-built Window with one OK button, no Cancel, no styling, and no
// validation — a blank answer closed it and the refusal appeared in the status bar behind a dialog
// that had already gone. Its only caller was the cancellation reason, which now uses AppFormDialog
// like every other form in the app. Deleted rather than left available: a second, worse prompt is
// something the next person reaches for.
