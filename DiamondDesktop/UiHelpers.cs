using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace DiamondDesktop;

/// <summary>
/// Keeps characters that are not part of a number out of a numeric box.
///
/// Without this, a WPF binding to a decimal simply fails on bad input: the property keeps its old
/// value, nothing is raised, and the box goes on showing what was typed. The user sees "12x" in
/// Price and an Amount computed from the previous price — wrong, and with no message anywhere.
/// Rejecting the keystroke is the only version of this the user can actually see.
/// </summary>
public static class NumericInput
{
    public static readonly DependencyProperty IsNumericProperty = DependencyProperty.RegisterAttached(
        "IsNumeric", typeof(bool), typeof(NumericInput), new PropertyMetadata(false, OnChanged));

    /// Intake and stock adjustments are signed — an ADJUST row gives carats back (docs/12 §7).
    public static readonly DependencyProperty AllowNegativeProperty = DependencyProperty.RegisterAttached(
        "AllowNegative", typeof(bool), typeof(NumericInput), new PropertyMetadata(false));

    public static void SetIsNumeric(DependencyObject o, bool v) => o.SetValue(IsNumericProperty, v);
    public static bool GetIsNumeric(DependencyObject o) => (bool)o.GetValue(IsNumericProperty);
    public static void SetAllowNegative(DependencyObject o, bool v) => o.SetValue(AllowNegativeProperty, v);
    public static bool GetAllowNegative(DependencyObject o) => (bool)o.GetValue(AllowNegativeProperty);

    private static void OnChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not TextBox box) return;

        if ((bool)e.NewValue)
        {
            box.PreviewTextInput += OnTextInput;
            box.PreviewKeyDown += OnKeyDown;
            DataObject.AddPastingHandler(box, OnPaste);
        }
        else
        {
            box.PreviewTextInput -= OnTextInput;
            box.PreviewKeyDown -= OnKeyDown;
            DataObject.RemovePastingHandler(box, OnPaste);
        }
    }

    /// Space never belongs in a number, and PreviewTextInput does not see it.
    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space) e.Handled = true;
    }

    private static void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        var box = (TextBox)sender;
        e.Handled = !IsAcceptable(Proposed(box, e.Text), GetAllowNegative(box));
    }

    private static void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        var box = (TextBox)sender;
        string pasted = e.DataObject.GetData(typeof(string)) as string ?? "";

        if (!IsAcceptable(Proposed(box, pasted), GetAllowNegative(box))) e.CancelCommand();
    }

    /// What the box would hold if this input were accepted — selection replaced, caret respected.
    private static string Proposed(TextBox box, string incoming)
    {
        string text = box.Text;
        int start = box.SelectionStart, length = box.SelectionLength;

        return text.Remove(start, length).Insert(start, incoming);
    }

    /// <summary>
    /// True for anything that could still become a number. Deliberately looser than
    /// <c>decimal.TryParse</c>: "" , "-" and "12." are all mid-typing states, and rejecting them
    /// makes the field impossible to type into.
    /// </summary>
    private static bool IsAcceptable(string text, bool allowNegative)
    {
        if (text.Length == 0) return true;

        string separator = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
        int digits = 0, separators = 0;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (char.IsDigit(c)) { digits++; continue; }

            if (c == '-')
            {
                if (!allowNegative || i != 0) return false;      // a sign is only a sign in front
                continue;
            }

            if (separator.Length == 1 && c == separator[0] && ++separators <= 1) continue;

            return false;
        }

        // "-" and "." alone are fine to be typing; "-." is not going anywhere.
        return digits > 0 || text.Length == 1;
    }
}

/// <summary>
/// The Sun…Sat heading for a calendar column.
///
/// WPF fills those cells from <c>ShortestDayNames</c>, which in most cultures is a single letter —
/// giving two "S"s and two "T"s in one row. The column index plus the culture's first day of the
/// week is enough to name the day properly, so the heading reads "Sun" rather than "S".
/// </summary>
public sealed class DayTitleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not System.Windows.DependencyObject cell) return value ?? "";

        var format = CultureInfo.CurrentCulture.DateTimeFormat;
        int column = (int)cell.GetValue(System.Windows.Controls.Grid.ColumnProperty);
        int day = ((int)format.FirstDayOfWeek + column) % 7;

        return format.AbbreviatedDayNames[day];
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}

/// <summary>
/// Range checks for the carat and money boxes on the movement forms.
///
/// Deliberately two different things. The HARD ceiling is the database's own: `price_per_ct` is
/// numeric(12,2), so anything from 10^10 up cannot be stored and Postgres rejects the row with
/// "numeric field overflow … precision 12, scale 2". Catching that here turns a schema message
/// into a sentence. The SOFT ceiling invents no rule — it asks. A parcel of 500,500 ct went in
/// unchallenged during testing (about 100 kg of diamonds); nothing in the workbook says that is
/// illegal, only that nobody meant to type it.
/// </summary>
public static class Bounds
{
    /// numeric(12,2) holds up to 9,999,999,999.99. Proven by the overflow the database returned.
    public const decimal StorageMax = 9_999_999_999.99m;

    /// Above this a human is asked to confirm. The largest parcel in the source workbook is
    /// 232.86 ct and the highest rate 63,000/ct, so these are far outside normal without
    /// forbidding anything.
    public const decimal LargeWeightCt = 10_000m;
    public const decimal LargePricePerCt = 1_000_000m;

    /// <summary>Null when the value is storable; otherwise the message to show.</summary>
    public static string? TooLarge(decimal value, string field) =>
        Math.Abs(value) > StorageMax
            ? $"{field} is larger than this system can store. The maximum is {StorageMax:N2}."
            : null;

    /// <summary>True when the figure is worth a second look before it is written.</summary>
    public static bool NeedsConfirming(decimal value, decimal threshold) => Math.Abs(value) > threshold;
}

/// <summary>
/// Turns a database or transport failure into something a person on a trading floor can act on.
///
/// Raw text is never thrown away — the caller keeps it on the status bar's tooltip — but
/// "numeric field overflow A field with precision 12, scale 2 must round to an absolute value
/// less than 10^10" is a message about a column definition, not about what the user did.
/// </summary>
public static class Friendly
{
    private static readonly (string Needle, string Message)[] Known =
    [
        ("numeric field overflow", "That number is too large for this field."),
        ("duplicate key value",    "That already exists."),
        ("violates foreign key",   "Something this refers to no longer exists — refresh and try again."),
        ("violates row-level security", "You do not have permission to do that."),
        ("violates check constraint",   "That value is not allowed here."),
        ("not-null constraint",    "A required value is missing."),
        ("jwt expired",            "Your session has expired. Sign in again."),
        ("invalid input syntax",   "One of the values is not in the format expected."),
        ("could not connect",      "Cannot reach the server. Check your connection."),
        ("no such host",           "Cannot reach the server. Check your connection."),
        ("timed out",              "The server took too long to answer. Try again."),
        ("timeout",                "The server took too long to answer. Try again."),
    ];

    /// <summary>The friendlier wording, or the original when nothing matches.</summary>
    public static string Message(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        foreach (var (needle, message) in Known)
            if (raw.Contains(needle, StringComparison.OrdinalIgnoreCase)) return message;

        return raw;
    }

    /// <summary>True when <see cref="Message"/> would replace the text — the caller keeps the original.</summary>
    public static bool Translates(string? raw) => !string.IsNullOrWhiteSpace(raw) && Message(raw) != raw;
}

/// <summary>
/// Visible when a collection is null or empty — the switch behind every "nothing here yet" panel.
/// Pass <c>Invert</c> as the parameter to show something only once there IS data.
/// </summary>
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool empty = value switch
        {
            null => true,
            int count => count == 0,
            System.Collections.ICollection c => c.Count == 0,
            System.Collections.IEnumerable e => !e.GetEnumerator().MoveNext(),
            _ => false,
        };

        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase)) empty = !empty;

        return empty ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}
