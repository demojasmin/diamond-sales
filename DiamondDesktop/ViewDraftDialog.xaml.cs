using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DiamondDesktop.Data;

namespace DiamondDesktop;

/// <summary>
/// View Draft (Nov 2026): one row per draft memo with the draft's own details. Returns the draft
/// to Edit, or the ticked drafts to mark Ready; the caller does the work.
/// </summary>
public partial class ViewDraftDialog : Window
{
    /// One row: a draft and its tick.
    public sealed class DraftRow(VInvoice draft) : INotifyPropertyChanged
    {
        public VInvoice Draft { get; } = draft;

        /// When it was last saved, on this PC's clock.
        public DateTime SavedAt => Draft.UpdatedAt.Kind == DateTimeKind.Local ? Draft.UpdatedAt : Draft.UpdatedAt.ToLocalTime();

        public string CreatedBy => string.IsNullOrWhiteSpace(Draft.Salesperson) ? "—" : Draft.Salesperson!;

        private bool _selected;
        public bool Selected
        {
            get => _selected;
            set
            {
                if (_selected == value) return;
                _selected = value;
                PropertyChanged?.Invoke(this, new(nameof(Selected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly List<DraftRow> _rows;

    public IReadOnlyList<VInvoice> Chosen { get; private set; } = [];

    /// The Edit icon was pressed: the draft to open in the Add sale layout.
    public VInvoice? EditRequested { get; private set; }

    public ViewDraftDialog(IReadOnlyList<VInvoice> drafts)
    {
        InitializeComponent();
        _rows = drafts.Select(d => new DraftRow(d)).ToList();
        foreach (var r in _rows) r.PropertyChanged += (_, _) => ShowCounts();
        Drafts.ItemsSource = _rows;
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowCounts();
    }

    private void ShowCounts()
    {
        int ticked = _rows.Count(r => r.Selected);
        CountText.Text = (_rows.Count == 1 ? "1 draft" : $"{_rows.Count} drafts")
            + ". Drafts hold no stock. Edit opens one in the Add sale layout, where it can be changed or marked Ready."
            + (ticked > 0 ? $"  {ticked} ticked." : "");
        ReadyButton.Visibility = ticked > 0 ? Visibility.Visible : Visibility.Collapsed;
        ReadyButton.Content = ticked == 1 ? "Ready (1 draft)" : $"Ready ({ticked} drafts)";
    }

    private void TickAll_Click(object sender, RoutedEventArgs e)
    {
        bool on = (sender as CheckBox)?.IsChecked == true;
        foreach (var r in _rows) r.Selected = on;
    }

    private void EditRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DraftRow row) return;
        EditRequested = row.Draft;
        DialogResult = true;
    }

    private void Drafts_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Drafts.SelectedItem is not DraftRow row) return;
        EditRequested = row.Draft;
        DialogResult = true;
    }

    private void Ready_Click(object sender, RoutedEventArgs e)
    {
        Chosen = _rows.Where(r => r.Selected).Select(r => r.Draft).ToList();
        if (Chosen.Count > 0) DialogResult = true;
    }
}
