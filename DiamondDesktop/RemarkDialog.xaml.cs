using System.Windows;

namespace DiamondDesktop;

/// <summary>Add/Edit Remark (Nov 2026): one line's remark. <see cref="Remark"/> holds it on Save.</summary>
public partial class RemarkDialog : Window
{
    public string? Remark { get; private set; }

    /// <param name="readOnly">A confirmed sale: shown, not changed.</param>
    public RemarkDialog(string? remark, string line, bool readOnly = false)
    {
        InitializeComponent();
        RemarkBox.Text = remark ?? "";
        LineText.Text = line;
        if (readOnly)
        {
            Title = Heading.Text = "Remark";
            RemarkBox.IsReadOnly = true;
            SaveButton.Visibility = Visibility.Collapsed;
        }
        Loaded += (_, _) => { RemarkBox.Focus(); RemarkBox.CaretIndex = RemarkBox.Text.Length; };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string text = RemarkBox.Text.Trim();
        Remark = text.Length == 0 ? null : text;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
