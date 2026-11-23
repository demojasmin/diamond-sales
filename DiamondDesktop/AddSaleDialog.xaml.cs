using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DiamondDesktop;

/// <summary>
/// Add sale: one deal (date, buyer, broker, broker %, terms, type) and a table of lines typed
/// directly in it, laid out like the Invoice lines table. Add to invoice hands every filled-in line,
/// carrying exactly this deal, to <c>addLine</c>, which puts it on the entry. Lines already on the
/// entry are never touched.
///
/// The table runs on its own scratch InvoiceEntry, so Rejection and Amount are worked out by the same
/// Recalculate the main table uses. Nothing here holds stock: a line holds its carats once it is on
/// the entry, exactly as one typed there does.
///
/// Same rules as the Deal Details drawer and the grid: a buyer is required, terms 0-365, broker %
/// 0-100, and a line needs a size, a grade, a weight, and a selection no bigger than the weight.
/// </summary>
public partial class AddSaleDialog : Window
{
    private readonly SaleLine _deal = new();
    private readonly InvoiceEntry _draft = new();
    private readonly Action<SaleLine> _addLine;
    private readonly Func<SaleLine, IReadOnlyList<SaleLine>, Task<string?>>? _saveDraft;
    private readonly Dictionary<SaleLine, SaleLine> _sourceOf = [];   // Edit: row here -> line on the entry
    private readonly bool _editing;
    private readonly string _primaryText = "Save";
    private int _added;

    public int Added => _added;
    public string BuyerName => _deal.DealBuyerName;

    /// Save as Draft went through: the sale is a draft memo now, not lines on the entry.
    public bool SavedAsDraft { get; private set; }

    /// Edit sale, on Save: each row with the entry line it came from (null for a row added here).
    /// Entry lines of the deal that are not in it were removed in the window.
    public IReadOnlyList<(SaleLine Line, SaleLine? Source)>? EditResult { get; private set; }

    /// <param name="saveDraft">Save as Draft: the deal and its lines; returns why it failed, or null.</param>
    /// <param name="editing">Edit sale (Nov 2026): the deal's lines on the entry, shown pre-filled.</param>
    public AddSaleDialog(InvoiceEntry entry, Action<SaleLine> addLine,
                         Func<SaleLine, IReadOnlyList<SaleLine>, Task<string?>>? saveDraft = null,
                         IReadOnlyList<SaleLine>? editing = null,
                         string? title = null, string? primaryText = null)
    {
        InitializeComponent();
        _addLine = addLine;
        _saveDraft = saveDraft;
        _editing = editing is { Count: > 0 };
        DraftButton.Visibility = saveDraft is null ? Visibility.Collapsed : Visibility.Visible;
        // TODAY, not the entry's date. The entry's date is whatever a restored or reopened entry was
        // typed on (03-09-2026 on Demo), so a new sale opened silently back-dated. A different
        // date is still one click away in the box.
        _deal.SetInvoiceDate(DateTime.Today);
        if (_editing) _deal.TakeDealFrom(editing![0]);
        MainWindow.BlockPastDates(DealDate, _deal.DealDate);    // past days greyed out (Nov 2026)
        DealFields.DataContext = _deal;

        // Filled from code, as every picker in this app is: PickerSearch wraps a local ItemsSource.
        BuyerBox.ItemsSource = entry.Buyers;
        BrokerBox.ItemsSource = new[] { PartyRef.NoBroker }.Concat(entry.Brokers).ToList();
        TypeBox.ItemsSource = entry.DocTypes;

        // Edit sale: the deal's lines, as copies, so Cancel leaves the entry exactly as it was.
        if (_editing)
        {
            Title = title ?? "Edit sale";
            DialogTitle.Text = Title;
            _primaryText = primaryText ?? "Save";
            _draft.Lines.Clear();
            foreach (var source in editing!)
            {
                var copy = new SaleLine();
                MainWindow.ApplyFigures(source, copy);
                _draft.Lines.Add(copy);
                _sourceOf[copy] = source;
            }
        }

        // The scratch entry opens with one row to type into, like the Sales entry screen.
        foreach (var line in _draft.Lines) line.TakeDealFrom(_deal);
        _draft.Recalculate();
        LinesGrid.ItemsSource = _draft.Lines;
        _draft.PropertyChanged += (_, _) => ShowReady();

        // Every line carries the deal, so a change to it goes onto every row -- and the broker %
        // is part of each line's amount.
        _deal.PropertyChanged += Deal_Changed;

        ShowReady();
        Loaded += (_, _) => BuyerBox.Focus();
    }

    private void Deal_Changed(object? sender, PropertyChangedEventArgs e)
    {
        foreach (var line in _draft.Lines) line.TakeDealFrom(_deal);
        _draft.Recalculate();
        ShowReady();                          // the guide line moves on once a buyer is picked
    }

    /// <summary>
    /// The footer, the button and the guide line, from what is typed. Display only: Save_Click still
    /// runs Problem() and refuses on its own, exactly as before.
    /// </summary>
    private void ShowReady()
    {
        var lines = _draft.RealLines;
        var ready = lines.Where(l => LineIssue(l) is null).ToList();
        decimal ct = ready.Sum(l => l.SelectionCt), amount = ready.Sum(l => l.Amount);

        AddedText.Text = ready.Count == 0
            ? "No line ready yet"
            : $"{ready.Count} line(s) ready · {ct:N2} ct · {Money.Exact(amount)}";
        SaveButton.Content = _editing ? _primaryText
            : ready.Count > 0 && ready.Count == lines.Count
            ? (ready.Count == 1 ? "Add 1 line to invoice" : $"Add {ready.Count} lines to invoice")
            : "Add to invoice";

        var unfinished = lines.FirstOrDefault(l => LineIssue(l) is not null);
        GuideText.Text =
            _deal.DealBuyer is null
                ? "Step 1 — choose the buyer (required). Broker, broker %, terms and type are optional; the due date follows from the terms."
            : lines.Count == 0
                ? $"Step 2 — type the first parcel for {_deal.DealBuyerName}: size, grade, weight, selection and price/ct. Add Line gives another row."
            : unfinished is not null
                ? $"Step 2 — line {_draft.Lines.IndexOf(unfinished) + 1}: {LineIssue(unfinished)}."
            : _editing
                ? $"Step 3 — press {_primaryText}. {ready.Count} line(s) for {_deal.DealBuyerName}."
            : $"Step 3 — press Add to invoice. {ready.Count} line(s) for {_deal.DealBuyerName} will appear on the Sales entry, ready to confirm.";
    }

    /// What still stops one line being added, or null. The same rules Problem() applies on Save.
    private static string? LineIssue(SaleLine l) =>
        l.Size is null ? "pick the size"
        : l.Grade is null ? "pick the grade"
        : l.GrossWeightCt <= 0 ? "enter the weight"
        : l.SelectionCt < 0 || l.SelectionCt > l.GrossWeightCt ? "selection must be between 0 and the weight"
        : l.PricePerCt < 0 ? "price per carat cannot be negative"
        : null;

    /// The buyer's usual terms and the broker's usual percentage, when nothing is typed yet -- the
    /// same defaults the entry's own deal applies.
    private void Buyer_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (BuyerBox.SelectedItem is PartyRef { DefaultTermsDays: { } terms } && _deal.DealTermsDays == 0)
            _deal.DealTermsDays = terms;
    }

    private void Broker_Changed(object sender, SelectionChangedEventArgs e)
    {
        // "No broker": no broker's cut either.
        if (BrokerBox.SelectedItem is PartyRef { IsNoBroker: true }) { _deal.DealBrokerPct = 0; return; }
        if (BrokerBox.SelectedItem is PartyRef { DefaultBrokerPct: { } pct } && _deal.DealBrokerPct == 0)
            _deal.DealBrokerPct = pct;
    }

    /// One click edits a text cell, as on the Sales entry table.
    private void Cell_Down(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGridCell { IsEditing: false, IsReadOnly: false } cell) return;
        if (cell.Column is not DataGridTextColumn) return;
        if (!cell.IsFocused) cell.Focus();
        LinesGrid.BeginEdit(e);
    }

    private void AddLine_Click(object sender, RoutedEventArgs e)
    {
        LinesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var line = new SaleLine();
        _draft.Lines.Add(line);
        line.TakeDealFrom(_deal);            // joining the entry stamps its date; the deal goes back on
        LinesGrid.ScrollIntoView(line);
        LinesGrid.CurrentCell = new DataGridCellInfo(line, LinesGrid.Columns[0]);
        LinesGrid.Focus();
        ShowReady();
    }

    private void RemoveLine_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SaleLine line) return;
        LinesGrid.CancelEdit();
        _draft.Lines.Remove(line);
        if (_draft.Lines.Count == 0) AddLine_Click(sender, e);
        ShowReady();
    }

    private string? Problem()
    {
        if (_deal.DealDate.Date < DateTime.Today)
        { DealDate.Focus(); return $"The date {_deal.DealDate:dd-MM-yyyy} is in the past. Choose today or a later date."; }
        if (_deal.DealBuyer is null) { BuyerBox.Focus(); return "Pick a buyer. Every line has to say who it is sold to."; }
        if (_deal.DealTermsDays is < 0 or > 365) { TermsBox.Focus(); return "Terms must be between 0 and 365 days."; }
        if (_deal.DealBrokerPct is < 0 or > 100) { PctBox.Focus(); return "Broker % must be between 0 and 100."; }

        var lines = _draft.RealLines;
        if (lines.Count == 0) return "Type at least one line in the table.";

        foreach (var l in lines)
        {
            int n = _draft.Lines.IndexOf(l) + 1;
            if (LineIssue(l) is { } bad) return $"Line {n}: {bad}.";
        }
        return null;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_added > 0) return;               // already added: a second press must not add them twice
        LinesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        LinesGrid.CommitEdit(DataGridEditingUnit.Row, true);

        string? problem = Problem();
        ErrorText.Text = problem ?? "";
        ErrorBox.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        if (problem is not null) return;

        if (_editing)
        {
            EditResult = Fresh().Select(p => (p.Line, _sourceOf.GetValueOrDefault(p.Row))).ToList();
            DialogResult = true;
            return;
        }

        foreach (var (line, _) in Fresh())
        {
            _addLine(line);
            _added++;
        }
        DialogResult = true;
    }

    /// A fresh line per row, carrying the deal, so nothing on the entry is wired to this window.
    private List<(SaleLine Line, SaleLine Row)> Fresh() =>
        _draft.RealLines.Select(l =>
        {
            var line = new SaleLine
            {
                Grade = l.Grade, Size = l.Size,
                GrossWeightCt = l.GrossWeightCt, SelectionCt = l.SelectionCt,
                PricePerCt = l.PricePerCt, ExRate = l.ExRate == 0 ? 1 : l.ExRate,
                Less1Pct = l.Less1Pct, Less2Pct = l.Less2Pct, Remark = l.Remark,
            };
            line.TakeDealFrom(_deal);
            if (line.DealBroker?.IsNoBroker == true) line.DealBroker = null;   // a choice, never stored
            return (line, l);
        }).ToList();

    /// <summary>
    /// What stops this sale being kept as a draft, or null. Looser than Add to invoice: a draft may
    /// still lack weights and prices. It needs what the database needs to hold it at all -- a buyer,
    /// and on every line a size, a grade and a selection no bigger than the weight.
    /// </summary>
    private string? DraftProblem()
    {
        if (_deal.DealBuyer is null) { BuyerBox.Focus(); return "Pick a buyer. A draft has to say who it is for."; }
        if (_deal.DealTermsDays is < 0 or > 365) { TermsBox.Focus(); return "Terms must be between 0 and 365 days."; }
        if (_deal.DealBrokerPct is < 0 or > 100) { PctBox.Focus(); return "Broker % must be between 0 and 100."; }

        var lines = _draft.RealLines;
        if (lines.Count == 0) return "Type at least one line in the table.";
        foreach (var l in lines)
        {
            int n = _draft.Lines.IndexOf(l) + 1;
            if (l.Size is null) return $"Line {n}: pick the size.";
            if (l.Grade is null) return $"Line {n}: pick the grade.";
            if (l.GrossWeightCt < 0 || l.SelectionCt < 0 || l.SelectionCt > l.GrossWeightCt)
                return $"Line {n}: selection must be between 0 and the weight.";
            if (l.PricePerCt < 0) return $"Line {n}: price per carat cannot be negative.";
        }
        return null;
    }

    private async void SaveDraft_Click(object sender, RoutedEventArgs e)
    {
        if (_saveDraft is null || SavedAsDraft || _added > 0) return;
        LinesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        LinesGrid.CommitEdit(DataGridEditingUnit.Row, true);

        string? problem = DraftProblem();
        if (problem is null)
        {
            DraftButton.IsEnabled = SaveButton.IsEnabled = false;
            DraftButton.Content = "Saving…";
            var deal = new SaleLine();
            deal.TakeDealFrom(_deal);
            if (deal.DealBroker?.IsNoBroker == true) deal.DealBroker = null;
            problem = await _saveDraft(deal, Fresh().Select(p => p.Line).ToList());
            DraftButton.IsEnabled = SaveButton.IsEnabled = true;
            DraftButton.Content = "Save as Draft";
        }

        ErrorText.Text = problem ?? "";
        ErrorBox.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        if (problem is not null) return;

        SavedAsDraft = true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// A date typed by hand that is past, or not a date at all: said in the window, not swallowed.
    private void DealDate_Invalid(object? sender, DatePickerDateValidationErrorEventArgs e)
    {
        ErrorText.Text = $"\"{e.Text}\" cannot be used as the date: choose today or a later date.";
        ErrorBox.Visibility = Visibility.Visible;
        e.ThrowException = false;
    }
}
