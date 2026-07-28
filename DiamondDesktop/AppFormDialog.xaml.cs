using System.Windows;
using System.Windows.Controls;

namespace DiamondDesktop;

/// <summary>One field on a small form: a label, a starting value, and whether it takes numbers.</summary>
public sealed record FormFieldSpec(string Label, string Value = "", bool Numeric = false,
                                   int MaxLength = 80);

/// <summary>
/// A modal that collects a few values and will not close until they pass the caller's own check.
///
/// AppDialog reports; this one asks. It deliberately does no validating of its own — the caller
/// hands in a delegate, so the rules stay in one place and cannot drift away from the inline path
/// they replaced.
/// </summary>
public partial class AppFormDialog : Window
{
    private readonly List<TextBox> _boxes = [];
    private Func<string[], string?>? _validate;
    private string[]? _result;

    private AppFormDialog() => InitializeComponent();

    /// <summary>
    /// Shows the form. Returns the entered values, or null if the user cancelled.
    /// <paramref name="validate"/> returns an error to show, or null to accept and close.
    /// </summary>
    public static string[]? Show(Window owner, string title, string headline, string? subhead,
                                 IEnumerable<FormFieldSpec> fields,
                                 Func<string[], string?> validate, string primaryText)
    {
        var d = new AppFormDialog { Owner = owner, Title = title, _validate = validate };
        d.Headline.Text = headline;
        d.Subhead.Text = subhead ?? "";
        d.Subhead.Visibility = string.IsNullOrWhiteSpace(subhead)
            ? Visibility.Collapsed : Visibility.Visible;
        d.PrimaryButton.Content = primaryText;

        foreach (var f in fields)
        {
            var label = new TextBlock { Text = f.Label.ToUpperInvariant() };
            label.SetResourceReference(StyleProperty, "FormLabel");

            var box = new TextBox { Text = f.Value, MaxLength = f.MaxLength, Margin = new Thickness(0, 0, 0, 14) };
            box.SetResourceReference(StyleProperty, f.Numeric ? "FormNumeric" : "FormInput");

            d.Fields.Children.Add(label);
            d.Fields.Children.Add(box);
            d._boxes.Add(box);
        }

        // Focus the first field, not the button: the user opened this to type.
        d.Loaded += (_, _) => { d._boxes.FirstOrDefault()?.Focus(); d._boxes.FirstOrDefault()?.SelectAll(); };

        d.ShowDialog();
        return d._result;
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        var values = _boxes.Select(b => b.Text.Trim()).ToArray();
        string? problem = _validate?.Invoke(values);

        if (problem is not null)
        {
            // Stay open. Closing on a bad value and reporting it behind the dialog would make the
            // user reopen the form and retype everything.
            ErrorText.Text = problem;
            ErrorBox.Visibility = Visibility.Visible;
            _boxes.FirstOrDefault()?.Focus();
            return;
        }

        _result = values;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
