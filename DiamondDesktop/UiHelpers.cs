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

public static class Words
{
    /// <summary>
    /// "1 invoice" / "2 invoices". Written inline this is a ternary inside an interpolation inside
    /// whatever wraps it, which is three levels deep to read for one letter of difference.
    /// </summary>
    public static string Plural(int n, string one, string many) => $"{n:N0} {(n == 1 ? one : many)}";

    public static string Plural(int n, string noun) => Plural(n, noun, noun + "s");
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
///
/// Pass <c>Loaded</c> when the source is a grid's ItemsSource. There, null does not mean "the
/// server returned nothing" — it means no load has finished yet, because the handlers assign
/// ItemsSource only after their await returns. Treating the two alike made every grid state
/// "No invoices yet" for the ~1.8s a full read takes, which is a claim about the data, not a
/// progress indicator. With Loaded, the panel waits for a real answer before making one.
/// </summary>
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool empty = value switch
        {
            null => !string.Equals(parameter as string, "Loaded", StringComparison.OrdinalIgnoreCase),
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

/// <summary>
/// The state a drill-down row is in, derived from figures the database already returned:
/// overdue when the view says so, paid when nothing is outstanding, pending otherwise.
///
/// Presentation only — no rule is decided here. v_invoice computes is_overdue and outstanding;
/// this just picks the word for them.
/// </summary>
public sealed class InvoiceStateConverter : IValueConverter
{
    /// <summary>
    /// The word the badge shows. Public and static so the drill-down search can match exactly what
    /// is on screen: the grid displays "Overdue" while that row's Status column holds "POSTED", so
    /// a search over the raw field found nothing for the very word the user was reading.
    /// Presentation only — the derivation is unchanged, it just has one home now.
    /// </summary>
    public static string State(DiamondDesktop.Data.VInvoice invoice) => invoice switch
    {
        { Status: "CANCELLED" } => "Cancelled",
        { Status: "DRAFT" } => "Draft",
        { IsOverdue: true } => "Overdue",
        { Outstanding: <= 0 } => "Paid",
        _ => "Pending",
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        // DrillRow wraps an invoice for the dashboard table; unwrapping here keeps the status
        // badge working from one template whether it is handed the invoice or the row.
        value switch
        {
            DiamondDesktop.Data.VInvoice invoice => State(invoice),
            DrillRow row => State(row.Invoice),
            _ => "",
        };

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>
/// One audited change, shaped for the screen. The query behind it is unchanged — Repo.AuditAsync
/// still returns the same 500 rows — this only replaces the flattened "k=v, k=v, …" string the old
/// grid showed with something a person can read and search.
/// </summary>
public sealed class AuditRow
{
    public DateTime ChangedAt { get; init; }
    public string Entity { get; init; } = "";
    public string Action { get; init; } = "";
    public long? RecordId { get; init; }
    public Guid? ChangedBy { get; init; }
    public IReadOnlyList<AuditField> Fields { get; init; } = [];

    public string When => ChangedAt.ToString("dd-MM-yyyy HH:mm:ss");
    public string Time => ChangedAt.ToString("HH:mm:ss");
    public string Record => RecordId is { } id ? $"#{id}" : "";

    /// <summary>
    /// Who made the change, resolved through <see cref="Names"/>.
    ///
    /// The page has always been titled "Every change, who and when" and never showed the who:
    /// changed_by was captured, mapped and then bound to nothing. An audit trail that proves only
    /// THAT something happened answers the second question anyone asks and not the first.
    ///
    /// Null means the row came from a SECURITY DEFINER function with no signed-in user behind it
    /// — a migration, a trigger, a scheduled job. That is "System", not an unknown person.
    /// A UUID that resolves to nobody is shown short: the account was deleted, and eight
    /// characters is enough to match rows to each other without pretending to name someone.
    /// </summary>
    public static IReadOnlyDictionary<Guid, string> Names { get; set; } =
        new Dictionary<Guid, string>();

    public string By
    {
        get
        {
            if (ChangedBy is not { } id) return "System";

            return Names.TryGetValue(id, out string? name) && !string.IsNullOrWhiteSpace(name)
                ? name
                : id.ToString()[..8];
        }
    }

    /// <summary>
    /// What kind of change this is, said once so the field list does not have to repeat it. An
    /// insert has no previous values at all; a delete has nothing after it. Stating that here is
    /// what lets the rows below drop the empty half without hiding anything.
    /// </summary>
    public string Nature => Action switch
    {
        "INSERT" => $"New record · {Fields.Count} value{(Fields.Count == 1 ? "" : "s")}, no previous values",
        "DELETE" => $"Removed record · {Fields.Count} value{(Fields.Count == 1 ? "" : "s")} as they stood",
        "UPDATE" => Fields.Count(f => f.Changed) is var n && n > 0
            ? $"{n} of {Fields.Count} fields changed"
            : "No field changed",
        _ => FieldCount,
    };
    public string FieldCount => $"{Fields.Count} field{(Fields.Count == 1 ? "" : "s")}";

    /// What changed, in a column's worth of room. An UPDATE names the fields that actually differ;
    /// anything else reports its size, because listing every column of an INSERT says nothing.
    public string Summary { get; init; } = "";

    /// Lowercased haystack for the filter box: entity, action, record and every field name and
    /// value, so searching for what is on screen finds it.
    public string Search { get; init; } = "";

    public static AuditRow From(DiamondDesktop.Data.AuditLog log)
    {
        var before = log.OldValues ?? new Dictionary<string, object>();
        var after = log.NewValues ?? new Dictionary<string, object>();

        var names = before.Keys.Concat(after.Keys).Distinct(StringComparer.Ordinal)
                          .OrderBy(k => k, StringComparer.Ordinal).ToList();

        var fields = names.Select(n => new AuditField(
            n,
            before.TryGetValue(n, out var b) ? Text(b) : "",
            after.TryGetValue(n, out var a) ? Text(a) : "")).ToList();

        var changed = fields.Where(f => f.Changed).Select(f => f.Name).ToList();
        string summary = log.Action switch
        {
            "UPDATE" when changed.Count == 0 => "no field changed",
            "UPDATE" when changed.Count <= 3 => string.Join(", ", changed),
            "UPDATE" => $"{changed.Count} fields changed",
            _ when fields.Count == 0 => "",
            _ => $"{fields.Count} field{(fields.Count == 1 ? "" : "s")}",
        };

        return new AuditRow
        {
            ChangedAt = log.ChangedAt,
            ChangedBy = log.ChangedBy,
            Entity = log.AuditedTable,
            Action = log.Action,
            RecordId = log.RecordId,
            Fields = fields,
            Summary = summary,
            // Entity, action and record only. Column names were the first mistake — "price"
            // matched every sales_line row through price_per_ct — and searching the values was
            // the second: a receipt whose amount happens to contain the digits typed is not what
            // anyone means by a search result. The record is indexed bare and as "#1", because
            // that is how the column displays it.
            Search = string.Join(' ', new[]
                {
                    log.AuditedTable,
                    log.Action,
                    log.RecordId?.ToString() ?? "",
                    log.RecordId is { } rid ? $"#{rid}" : "",
                }
                .Where(x => x.Length > 0))
                .ToLowerInvariant(),
        };
    }

    /// Values arrive as JSON tokens, so ToString on a null is not the same as an absent key.
    private static string Text(object? value) => value?.ToString() ?? "";
}

/// <param name="Changed">True only when both sides are present and differ, so an INSERT does not
/// light up every field as a change.</param>
public sealed record AuditField(string Name, string Before, string After)
{
    public bool Changed => Before.Length > 0 && After.Length > 0 && Before != After;

    /// <summary>
    /// True when there are genuinely two sides to compare. Only then do BEFORE and AFTER labels
    /// earn their space: on an insert every Before is empty, and nine rows of "BEFORE —" say
    /// nothing the INSERT badge has not already said.
    /// </summary>
    public bool HasBoth => Before.Length > 0 && After.Length > 0;

    /// The one value that exists, for the single-sided case — an insert's new value or a delete's
    /// last value. Never hides anything: if both sides exist, HasBoth is true and both are shown.
    public string OnlyValue => After.Length > 0 ? After : Before;

    public string BeforeText => Before.Length > 0 ? Before : "\u2014";
    public string AfterText => After.Length > 0 ? After : "\u2014";
}

/// <summary>
/// "Asha Patel" to "AP". The same rule the window's profile chip uses, exposed as a converter so
/// the user list can draw an avatar without a photo — the schema stores none.
/// </summary>
public sealed class InitialsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var words = (value as string ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 ? "?" : string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0])));
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>
/// One row of app_config, dressed for the screen.
///
/// The key is never changed and never hidden — it is what <c>Repo.SetConfigAsync</c> writes and what
/// a support call will ask for. The label is only how it is read.
/// </summary>
public sealed class SettingItem : System.ComponentModel.INotifyPropertyChanged
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required string Description { get; init; }
    public required string Category { get; init; }

    /// The value as it was loaded. Save writes only rows where this and <see cref="Value"/> differ.
    public required string OriginalValue { get; init; }

    private string _value = "";
    public string Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            Raise(nameof(Value));
            Raise(nameof(IsDirty));
        }
    }

    public bool IsDirty => Value != OriginalValue;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

    // ── The catalogue ───────────────────────────────────────────────────────────────────────────
    // Every key documented in docs/03 §2.5. A key that is NOT listed here still appears, under
    // "Other" — a settings screen that silently omits a row it does not recognise is worse than one
    // that shows a raw key.
    // ── The catalogue ───────────────────────────────────────────────────────────────────────────
    // Keyed on what app_config actually contains, verified against the live table — docs/03 §2.5
    // describes an earlier naming (money_dp, negative_stock_policy, lockout_attempts) that the
    // database does not use. Those older names are kept as well, so a client database seeded from
    // the document still reads properly.
    //
    // A key that is NOT listed here still appears, under "Other". A settings screen that silently
    // omits a row it does not recognise is worse than one that shows a raw key.
    private static readonly Dictionary<string, (string Category, string Label, string Description)> Known =
        new(StringComparer.Ordinal)
        {
            // ── as the database has them ──
            ["company_name"] = ("General", "Company name",
                "The trading name shown on printed bills."),
            ["base_currency"] = ("General", "Base currency",
                "The currency every stored amount is expressed in."),
            ["money_precision"] = ("General", "Money decimal places",
                "How many decimals amounts are rounded and displayed to."),
            ["carat_precision"] = ("General", "Carat decimal places",
                "How many decimals weights are rounded and displayed to."),
            ["alert_overdue_days"] = ("General", "Overdue after (days)",
                "How many days past the due date an invoice is counted as overdue."),

            ["negative_stock"] = ("Inventory", "Negative stock policy",
                "What happens when a sale would take a bucket below zero — BLOCK, WARN or ALLOW."),
            ["alert_low_stock_ct"] = ("Inventory", "Low stock threshold (ct)",
                "A grade and size holding fewer carats than this is reported as low."),

            ["session_timeout_min"] = ("Security", "Session timeout (minutes)",
                "How long a signed-in session stays valid without activity."),
            ["max_login_attempts"] = ("Security", "Lockout after failed attempts",
                "How many failed sign-ins lock an account."),
            ["lockout_minutes"] = ("Security", "Lockout lasts (minutes)",
                "How long an account stays locked after too many failed sign-ins."),

            // ── the names docs/03 §2.5 uses, in case a database is seeded from it ──
            ["money_dp"] = ("General", "Money decimal places",
                "How many decimals amounts are rounded and displayed to."),
            ["carat_dp"] = ("General", "Carat decimal places",
                "How many decimals weights are rounded and displayed to."),
            ["rounding"] = ("General", "Rounding mode",
                "How a half value is resolved — HALF_UP rounds it away from zero."),
            ["settlement_write_off_threshold"] = ("General", "Settlement write-off threshold",
                "An outstanding balance smaller than this closes the invoice and posts the residue "
                + "as a rounding adjustment, instead of leaving a phantom receivable."),
            ["negative_stock_policy"] = ("Inventory", "Negative stock policy",
                "What happens when a sale would take a bucket below zero — BLOCK, WARN or ALLOW."),
            ["auto_reject_on_post"] = ("Inventory", "Auto-reject on post",
                "Whether posting an invoice also writes the rejection carats as a stock movement."),
            ["lockout_attempts"] = ("Security", "Lockout after failed attempts",
                "How many failed sign-ins lock an account."),
            ["manager_sees_margin"] = ("Security", "Managers can see margin",
                "Whether the margin figures are visible to managers as well as owners."),
        };

    public static readonly string[] Categories = ["General", "Inventory", "Security", "Other"];

    /// <summary>
    /// What each known key will accept. The value box is plain text and every value was written
    /// through as typed, so "abc" reached carat_precision and a decimal-places setting that every
    /// screen reads became a string no screen could parse. A key that is not listed keeps the old
    /// behaviour — unrecognised settings are shown rather than hidden, and the database stays the
    /// authority on them.
    ///
    /// Bounds, not guesses: precision is capped at the numeric(_,4) carats and (_,2) money the
    /// schema stores; the policy words are the three negative_stock_policy() recognises.
    /// </summary>
    private static readonly Dictionary<string, Func<string, string?>> Rules =
        new(StringComparer.Ordinal)
        {
            ["money_precision"] = v => WholeBetween(v, 0, 2),
            ["money_dp"] = v => WholeBetween(v, 0, 2),
            ["carat_precision"] = v => WholeBetween(v, 0, 4),
            ["carat_dp"] = v => WholeBetween(v, 0, 4),
            ["alert_overdue_days"] = v => WholeBetween(v, 0, 3650),
            ["session_timeout_min"] = v => WholeBetween(v, 1, 1440),
            ["max_login_attempts"] = v => WholeBetween(v, 1, 100),
            ["lockout_attempts"] = v => WholeBetween(v, 1, 100),
            ["lockout_minutes"] = v => WholeBetween(v, 1, 1440),
            ["alert_low_stock_ct"] = v => NonNegativeNumber(v),
            ["settlement_write_off_threshold"] = v => NonNegativeNumber(v),
            ["negative_stock"] = v => OneOf(v, "BLOCK", "WARN", "ALLOW"),
            ["negative_stock_policy"] = v => OneOf(v, "BLOCK", "WARN", "ALLOW"),
            ["rounding"] = v => OneOf(v, "HALF_UP", "HALF_EVEN"),
            ["auto_reject_on_post"] = v => OneOf(v, "true", "false"),
            ["manager_sees_margin"] = v => OneOf(v, "true", "false"),
            ["company_name"] = v => string.IsNullOrWhiteSpace(v) ? "Company name cannot be empty." : null,
            ["base_currency"] = v => v.Trim().Length == 3 ? null : "Base currency is a three-letter code, such as INR.",
        };

    /// <summary>Null when the value may be saved; otherwise why not.</summary>
    public string? Problem => Rules.TryGetValue(Key, out var rule) ? rule(Value ?? "") : null;

    private static string? WholeBetween(string value, int low, int high) =>
        int.TryParse(value?.Trim(), out int n) && n >= low && n <= high
            ? null
            : $"Enter a whole number between {low} and {high}.";

    private static string? NonNegativeNumber(string value) =>
        decimal.TryParse(value?.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal d) && d >= 0
            ? null
            : "Enter a number of 0 or more.";

    private static string? OneOf(string value, params string[] allowed) =>
        allowed.Any(a => string.Equals(a, value?.Trim(), StringComparison.OrdinalIgnoreCase))
            ? null
            : $"Must be one of: {string.Join(", ", allowed)}.";

    public static SettingItem From(string key, string value)
    {
        var known = Known.TryGetValue(key, out var k)
            ? k
            : ("Other", Humanise(key), "Not one of the documented settings — shown so it cannot be missed.");

        return new SettingItem
        {
            Key = key,
            Category = known.Item1,
            Label = known.Item2,
            Description = known.Item3,
            OriginalValue = value,
            Value = value,
        };
    }

    /// "some_unknown_key" to "Some unknown key" — a fallback label, not a translation.
    private static string Humanise(string key) =>
        key.Length == 0 ? key
            : char.ToUpperInvariant(key[0]) + key[1..].Replace('_', ' ');
}

/// <summary>
/// The word on a stock bucket's badge. A converter rather than a computed property on
/// VStockPosition, because that type mirrors the database view and gains nothing from knowing how
/// a badge reads.
///
/// Zero and negative are deliberately different: zero is a bucket that has sold out, negative means
/// stock left that never arrived — a reconciliation fault, not a level.
/// </summary>
public sealed class StockStateConverter : IValueConverter
{
    /// Every stock badge in the app resolves through here — the grid column, the movements drawer
    /// and the clipboard export — so "Low" appears in all three from this one line.
    /// alert_low_stock_ct of 0 disables the band rather than marking every bucket low.
    public static string State(decimal balance) => balance switch
    {
        < 0 => "Negative",
        0 => "Empty",
        _ when Data.Policy.LowStockCt > 0 && balance <= Data.Policy.LowStockCt => "Low",
        _ => "In stock",
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is decimal balance ? State(balance) : "";

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>
/// International short form for money: 125,000 reads as "125.00 K", 12,500,000 as "12.50 M",
/// 1,250,000,000 as "1.25 B".
///
/// Display only — every caller keeps the decimal it was given. Sorting binds to the model
/// property, filtering matches against the exact figure, and the CSV export reads the property
/// rather than the rendered text, so ordering, search, exports and APIs are all untouched.
///
/// Money only, deliberately. Carat weights stay exact: a parcel weight is reconciled against a
/// physical packet, and "60.00 K ct" cannot be checked against anything.
/// </summary>
public static class Money
{
    private const decimal Billion = 1_000_000_000m, Million = 1_000_000m, Thousand = 1_000m;

    /// <summary>The plain figure, at the configured decimals. See Policy.MoneyPrecision.</summary>
    public static string Exact(decimal value) => Data.Policy.Format(value);

    public static string Exact(decimal? value) => value is { } v ? Exact(v) : "—";

    public static string Short(decimal value)
    {
        decimal size = Math.Abs(value);
        // Below a thousand there is nothing to shorten, and rounding a small figure to the
        // configured decimals is what every other number on the screen already does.
        if (size < Thousand) return Exact(value);

        (decimal unit, string suffix) = size >= Billion ? (Billion, " B")
                                      : size >= Million ? (Million, " M")
                                                        : (Thousand, " K");

        // Two decimals on the quotient regardless of money_precision: 1.25 M, not 1.3 M, so the
        // figure still carries the precision someone would read out loud. At money_precision 0 a
        // shortened figure would otherwise collapse to "1 M" and lose a quarter of a billion.
        //
        // Invariant grouping on the quotient, not the machine's: the app runs under en-IN, whose
        // groups are 2,10,00,000 — pairing a lakh-grouped quotient with a "B" suffix reads as two
        // different systems in one figure. This only shows at all past a thousand billion.
        return (value / unit).ToString("N2", CultureInfo.InvariantCulture) + suffix;
    }

    public static string Short(decimal? value) => value is { } v ? Short(v) : "—";
}

/// Binds the short form to a money column or label. ConverterParameter="Exact" gives the plain
/// figure back, for the places that need to stay reconcilable.
public sealed class ShortMoneyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return "—";
        if (value is not decimal d)
        {
            if (!decimal.TryParse(System.Convert.ToString(value, culture), NumberStyles.Any, culture, out d))
                return value.ToString() ?? "";
        }
        return string.Equals(parameter as string, "Exact", StringComparison.OrdinalIgnoreCase)
            ? Money.Exact(d)
            : Money.Short(d);
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c)
        => Binding.DoNothing;
}


/// <summary>
/// One row of the dashboard's drill-down table. It wraps the invoice rather than replacing it, so
/// the STATUS template and the sort paths still read the same properties, and adds the one figure
/// the table could not carry: what this invoice contributes under the current filters. With a
/// grade set that is the grade's share; without one it is the invoice total.
/// </summary>
public sealed class DrillRow
{
    public required DiamondDesktop.Data.VInvoice Invoice { get; init; }
    public required decimal ScopedAmount { get; init; }

    public string? InvoiceNo => Invoice.InvoiceNo;
    public string BuyerName => Invoice.BuyerName;
    public DateOnly InvoiceDate => Invoice.InvoiceDate;
    public decimal Outstanding => Invoice.Outstanding;
    public bool IsOverdue => Invoice.IsOverdue;
    public string Status => Invoice.Status;
}

/// <summary>
/// "1 line", "2 lines" — a count with a noun that agrees with it. The rest of the app builds these
/// in code with a ternary; a chip bound straight to a count in XAML cannot, and read "1 lines".
/// Pass the singular noun as the parameter.
/// </summary>
public sealed class CountLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string noun = parameter as string ?? "item";
        int count = value switch
        {
            int i => i,
            System.Collections.ICollection c => c.Count,
            _ => int.TryParse(System.Convert.ToString(value, culture), out int n) ? n : 0,
        };
        return Words.Plural(count, noun);
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}
