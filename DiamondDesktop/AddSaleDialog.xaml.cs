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
    private int _added;

    public int Added => _added;
    public string BuyerName => _deal.DealBuyerName;

    public AddSaleDialog(InvoiceEntry entry, Action<SaleLine> addLine)
    {
        InitializeComponent();
        _addLine = addLine;
        _deal.SetInvoiceDate(entry.InvoiceDate);
        DealFields.DataContext = _deal;

        // Filled from code, as every picker in this app is: PickerSearch wraps a local ItemsSource.
        BuyerBox.ItemsSource = entry.Buyers;
        BrokerBox.ItemsSource = entry.Brokers;
        TypeBox.ItemsSource = entry.DocTypes;

        // The scratch entry opens with one row to type into, like the Sales entry screen.
        foreach (var line in _draft.Lines) line.TakeDealFrom(_deal);
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
    }

    private void ShowReady() => AddedText.Text = $"{_draft.RealLines.Count} line(s) ready";

    /// The buyer's usual terms and the broker's usual percentage, when nothing is typed yet -- the
    /// same defaults the entry's own deal applies.
    private void Buyer_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (BuyerBox.SelectedItem is PartyRef { DefaultTermsDays: { } terms } && _deal.DealTermsDays == 0)
            _deal.DealTermsDays = terms;
    }

    private void Broker_Changed(object sender, SelectionChangedEventArgs e)
    {
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
        if (_deal.DealBuyer is null) { BuyerBox.Focus(); return "Pick a buyer. Every line has to say who it is sold to."; }
        if (_deal.DealTermsDays is < 0 or > 365) { TermsBox.Focus(); return "Terms must be between 0 and 365 days."; }
        if (_deal.DealBrokerPct is < 0 or > 100) { PctBox.Focus(); return "Broker % must be between 0 and 100."; }

        var lines = _draft.RealLines;
        if (lines.Count == 0) return "Type at least one line in the table.";

        foreach (var l in lines)
        {
            int n = _draft.Lines.IndexOf(l) + 1;
            string? bad = l.Size is null ? "pick the size"
                        : l.Grade is null ? "pick the grade"
                        : l.GrossWeightCt <= 0 ? "enter the weight"
                        : l.SelectionCt < 0 || l.SelectionCt > l.GrossWeightCt ? "selection must be between 0 and the weight"
                        : l.PricePerCt < 0 ? "price per carat cannot be negative"
                        : null;
            if (bad is not null) return $"Line {n}: {bad}.";
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

        // A fresh line per row, so nothing on the entry is wired to this window's scratch entry.
        foreach (var l in _draft.RealLines)
        {
            var line = new SaleLine
            {
                Grade = l.Grade, Size = l.Size,
                GrossWeightCt = l.GrossWeightCt, SelectionCt = l.SelectionCt,
                PricePerCt = l.PricePerCt, ExRate = l.ExRate == 0 ? 1 : l.ExRate,
                Less1Pct = l.Less1Pct, Less2Pct = l.Less2Pct, Remark = l.Remark,
            };
            line.TakeDealFrom(_deal);
            _addLine(line);
            _added++;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
