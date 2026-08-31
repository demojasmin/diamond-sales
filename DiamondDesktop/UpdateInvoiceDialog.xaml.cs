using System.Windows;
using System.Windows.Controls;
using DiamondDesktop.Data;

namespace DiamondDesktop;

/// <summary>
/// Updating a sale that has already moved stock, in one modal.
///
/// It binds to an <see cref="InvoiceEntry"/> — the same object the entry screen uses — so
/// Rejection, Amount, the totals and the due date are computed by exactly the code that computes
/// them there. A second implementation of any of those would be a second set of figures to keep in
/// step, and the one that drifts is always the one nobody looks at.
///
/// It writes nothing. <see cref="Entry"/> and <see cref="Reason"/> are read by the caller, which
/// hands them to edit_posted_invoice (0040) — one transaction that gives the old carats back,
/// replaces the lines and takes the new figures out again. Cancel leaves the invoice, its lines
/// and its stock exactly as they were.
/// </summary>
public partial class UpdateInvoiceDialog : Window
{
    /// The invoice as edited. Only meaningful when ShowDialog returned true.
    public InvoiceEntry Entry { get; }

    /// Why it was updated. 0040 refuses a blank one, and so does this form.
    public string Reason => ReasonBox.Text.Trim();

    /// <param name="pageResources">
    /// The Sales entry page's own resource dictionary.
    ///
    /// This window borrows that page's markup wholesale -- the header fields, the line grid, the
    /// cell styles -- and those styles are declared on MainWindow, where a separate window cannot
    /// see them. Merged BEFORE InitializeComponent, because StaticResource is resolved while the
    /// XAML is being parsed and anything arriving afterwards is too late.
    ///
    /// Borrowed rather than copied: one definition, so the modal cannot drift away from the screen
    /// it is meant to mirror.
    /// </param>
    public UpdateInvoiceDialog(InvoiceEntry entry, string invoiceNo, ResourceDictionary? pageResources = null)
    {
        if (pageResources is not null) Resources.MergedDictionaries.Add(pageResources);

        InitializeComponent();
        Entry = entry;
        DataContext = entry;
        Title = $"Update {invoiceNo}";
        Headline.Text = $"Update {invoiceNo}";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// The header tick box. Held by reference: it lives inside a column's HeaderTemplate, where
    /// FindName cannot reach it.
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
    /// makes you want.
    /// </summary>
    private void TickAllLines_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box) return;

        bool on = box.IsChecked == true;
        foreach (var line in Entry.Lines) line.Selected = on;
        TickCountChanged();
    }

    /// <summary>Keeps the header box and the Remove button honest about what is ticked.</summary>
    private void TickCountChanged()
    {
        int n = Entry.Lines.Count(l => l.Selected);

        if (_tickAll is not null)
            _tickAll.IsChecked = Entry.Lines.Count == 0 || n == 0 ? false
                               : n == Entry.Lines.Count ? true
                               : null;                       // some, but not all

        if (DeleteLines is null) return;
        DeleteLinesLabel.Text = n == 1 ? "Remove 1 line" : $"Remove {n} lines";
        DeleteLines.Visibility = n == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Removes every ticked line at once, after one question naming how many.</summary>
    private void DeleteLines_Click(object sender, RoutedEventArgs e)
    {
        var doomed = Entry.Lines.Where(l => l.Selected).ToList();
        if (doomed.Count == 0) return;

        // Only asked when there is typing to lose. Clearing rows that are entirely empty is not a
        // decision worth interrupting for.
        if (doomed.Any(l => !l.IsBlank)
            && MessageBox.Show(this,
                   doomed.Count == 1 ? "Remove 1 line from this invoice?"
                                     : $"Remove {doomed.Count} lines from this invoice?",
                   "Remove lines", MessageBoxButton.YesNo, MessageBoxImage.Question)
               != MessageBoxResult.Yes)
            return;

        foreach (var line in doomed) Entry.Lines.Remove(line);
        TickCountChanged();
        Dismiss();
    }

    /// <summary>
    /// Prints THIS invoice, the one on the form. It saves nothing -- printing is what goes out
    /// with the parcel, and an update that has not been saved must not reach the ledger by way of
    /// the printer.
    /// </summary>
    private void PrintMemo_Click(object sender, RoutedEventArgs e)
    {
        LineGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);

        var lines = Entry.RealLines;
        if (lines.Count == 0) return;

        var memo = new VInvoice
        {
            InvoiceNo   = Title.Replace("Update ", ""),
            InvoiceDate = DateOnly.FromDateTime(Entry.InvoiceDate),
            BuyerName   = Entry.Buyer ?? "",
            BrokerName  = Entry.SelectedBroker?.Name,
            BrokerPct   = Entry.BrokerPct,
            TermsDays   = Entry.TermsDays,
            DocType     = Entry.DocType ?? "",
            Status      = InvoiceStatus.POSTED,
            AmountTotal = Entry.TotalAmount,
            CaratsSold  = Entry.TotalCarats,
            Outstanding = Entry.TotalAmount,
        };
        var rows = lines.Select(l => new VSalesLine
        {
            GradeCode = l.Grade?.ShortName ?? "", SizeCode = l.Size?.Code ?? "",
            GrossWeightCt = l.GrossWeightCt, SelectionCt = l.SelectionCt,
            RejectionCt = l.RejectionCt, PricePerCt = l.PricePerCt,
            ExRate = l.ExRate, Less1Pct = l.Less1Pct, Less2Pct = l.Less2Pct,
            BrokerPct = Entry.BrokerPct, Amount = l.Amount, Remark = l.Remark,
        }).ToList();

        Reports.PrintInvoice(memo, rows);
    }

    /// Add line, as the entry screen has it. The dialog binds the same InvoiceEntry, so a line
    /// added here is a line on that invoice.
    private void AddLine_Click(object sender, RoutedEventArgs e)
    {
        var line = new SaleLine();
        Entry.Lines.Add(line);
        TickCountChanged();
        LineGrid.ScrollIntoView(line);
        LineGrid.CurrentCell = new System.Windows.Controls.DataGridCellInfo(line, LineGrid.Columns[0]);
        Dismiss();
    }

    /// <summary>
    /// Takes one line off. Asked about only when there is typing to lose, exactly as the entry
    /// screen asks -- and it does NOT put a replacement row back: an invoice with no lines is
    /// refused on Save, which is a better place to say so than a row appearing by itself.
    /// </summary>
    private void RemoveLine_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SaleLine line }) return;

        if (!line.IsBlank)
        {
            string what = line.Grade is null ? $"line {Entry.Lines.IndexOf(line) + 1}"
                                             : $"{line.Grade.ShortName} · {line.GrossWeightCt:N2} ct";
            if (MessageBox.Show(this, $"Remove {what}?", "Remove line",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
        }

        Entry.Lines.Remove(line);
        TickCountChanged();
        Dismiss();
    }

    /// <summary>
    /// Clears the refusal as soon as the thing it complained about changes.
    ///
    /// It only reappeared on the next Save, so "A reason is required." sat in red under a box with
    /// a reason typed into it -- the form contradicting itself, and reading as though the reason
    /// had been rejected rather than simply not yet re-checked.
    /// </summary>
    private void Dismiss(object sender, RoutedEventArgs e) => Dismiss();

    private void Dismiss(object? sender, System.Windows.Controls.DataGridCellEditEndingEventArgs e) => Dismiss();

    private void Dismiss() => ErrorText.Visibility = Visibility.Collapsed;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // Commit whatever cell is still being typed into. Without this, a figure changed and left
        // under the caret is not on the object yet and would be saved at its old value.
        LineGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);

        // The same rules the entry screen enforces, from the same list, so an invoice cannot be
        // corrected into a state a new one could not have been created in.
        var problems = Entry.Problems().ToList();

        // 0040 stores the reason on every movement it writes, and refuses without one. Asked here
        // rather than after the round trip: a refusal that crosses the network to tell you about an
        // empty box on screen is a worse way to learn it.
        if (Reason.Length == 0) problems.Insert(0, "A reason is required.");

        if (problems.Count > 0)
        {
            ErrorText.Text = string.Join("\n", problems);
            ErrorText.Visibility = Visibility.Visible;
            if (Reason.Length == 0) ReasonBox.Focus();
            return;
        }

        ErrorText.Visibility = Visibility.Collapsed;
        DialogResult = true;
    }
}
