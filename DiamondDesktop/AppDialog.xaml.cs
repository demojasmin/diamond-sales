using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DiamondDesktop;

/// <summary>
/// The app's own dialog, in place of MessageBox for the two import steps.
///
/// MessageBox draws itself from the OS: system font, system buttons, one blob of text with manual
/// line breaks doing the layout. That is fine for "are you sure", but these two carry figures a
/// user has to read carefully before destroying a dataset, and numbers packed into a paragraph are
/// hard to check. Here the counts sit in an aligned two-column card, the destructive line gets its
/// own tinted block, and the buttons follow the app's own styles.
///
/// Presentation only: the caller decides what to say and what the answer means.
/// </summary>
public partial class AppDialog : Window
{
    public enum Tone { Warning, Info }

    private bool _primaryChosen;

    private AppDialog() => InitializeComponent();

    /// <summary>Asks a question. Returns true when the primary button was chosen.</summary>
    public static bool Confirm(Window owner, string title, string headline, string? subhead,
                               IEnumerable<(string Label, string Value)> facts,
                               string? emphasis, string? listTitle, IEnumerable<string>? bullets,
                               string primaryText, string secondaryText) =>
        Build(owner, Tone.Warning, title, headline, subhead, facts, emphasis, listTitle, bullets,
              primaryText, secondaryText, null).ShowThenAnswer();

    /// <summary>States an outcome. One button, nothing to decide.</summary>
    public static void Info(Window owner, string title, string headline, string? subhead,
                            IEnumerable<(string Label, string Value)> facts,
                            string? listTitle, IEnumerable<string>? bullets, string? note)
    {
        var dialog = Build(owner, Tone.Info, title, headline, subhead, facts, null, listTitle,
                           bullets, "Done", null, note);
        dialog.ShowDialog();
    }

    private bool ShowThenAnswer()
    {
        ShowDialog();
        return _primaryChosen;
    }

    private static AppDialog Build(Window owner, Tone tone, string title, string headline,
                                   string? subhead,
                                   IEnumerable<(string Label, string Value)> facts,
                                   string? emphasis, string? listTitle,
                                   IEnumerable<string>? bullets, string primaryText,
                                   string? secondaryText, string? note)
    {
        var d = new AppDialog { Owner = owner, Title = title };

        d.Headline.Text = headline;
        d.Subhead.Text = subhead ?? "";
        d.Subhead.Visibility = string.IsNullOrWhiteSpace(subhead)
            ? Visibility.Collapsed : Visibility.Visible;

        if (tone == Tone.Info)
        {
            d.IconGlyph.Text = "";                                  // completed
            d.IconGlyph.SetResourceReference(ForegroundProperty, "SuccessBrush");
            d.IconBadge.SetResourceReference(Border.BackgroundProperty, "SuccessSoftBrush");
        }

        var rows = facts.ToList();
        for (int i = 0; i < rows.Count; i++)
        {
            d.FactsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock { Text = rows[i].Label, Style = (Style)d.FindResource("FactLabel") };
            var value = new TextBlock { Text = rows[i].Value, Style = (Style)d.FindResource("FactValue") };
            if (i > 0) label.Margin = new Thickness(0, 7, 14, 0);
            if (i > 0) value.Margin = new Thickness(0, 7, 0, 0);

            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            d.FactsGrid.Children.Add(label);
            d.FactsGrid.Children.Add(value);
        }
        d.FactsCard.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        if (!string.IsNullOrWhiteSpace(emphasis))
        {
            d.EmphasisText.Text = emphasis;
            d.EmphasisCard.Visibility = Visibility.Visible;
        }

        var items = bullets?.ToList() ?? [];
        if (items.Count > 0)
        {
            d.ListTitle.Text = listTitle ?? "";
            d.ListItems.ItemsSource = items;
            d.ListSection.Visibility = Visibility.Visible;
        }

        if (!string.IsNullOrWhiteSpace(note))
        {
            d.FooterNote.Text = note;
            d.FooterNote.Visibility = Visibility.Visible;
        }

        d.PrimaryButton.Content = primaryText;
        if (secondaryText is null)
        {
            // A single-button dialog: Escape and the close box must still answer it, and a lone
            // button belongs where the primary sits, not beside a gap.
            d.SecondaryButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            d.SecondaryButton.Content = secondaryText;
        }
        return d;
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        _primaryChosen = true;
        Close();
    }

    private void Secondary_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();          // never answers "yes" by accident
    }
}
