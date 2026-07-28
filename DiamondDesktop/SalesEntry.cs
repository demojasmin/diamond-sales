using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using DiamondCalc;
using DiamondDesktop.Data;

namespace DiamondDesktop;

/// <summary>
/// Grades and sieve sizes, straight from Supabase. The collections are filled in place rather than
/// replaced, so the XAML bindings that captured them at load time keep showing the live list.
/// </summary>
public static class Catalogue
{
    public static ObservableCollection<Grade> Grades { get; } = [];
    public static ObservableCollection<SizeBucket> AllSizes { get; } = [];

    public static readonly IReadOnlyList<string> DocTypes = ["BILL"];

    /// Every invoice is billed in INR — there is no currency picker on the entry screen, but
    /// sales_invoice still needs the id. Zero means the catalogue has not loaded, or INR is not
    /// in the currency table; either way no invoice may be saved.
    public static long BaseCurrencyId { get; private set; }

    public static async Task LoadAsync()
    {
        var grades = await Repo.GradesAsync();
        var sizes = await Repo.SizesAsync();
        var currencies = await Repo.CurrenciesAsync();

        Grades.Clear();
        foreach (var g in grades) Grades.Add(g);

        AllSizes.Clear();
        foreach (var s in sizes) AllSizes.Add(s);

        // INR or nothing. Falling back to whatever sorted first stamped every invoice with an
        // arbitrary currency_id, which changes what each amount on it MEANS — and silently, since
        // the entry screen has no currency to show. Refusing the save is the honest failure.
        BaseCurrencyId = currencies.FirstOrDefault(c => c.Code.Equals("INR", StringComparison.OrdinalIgnoreCase))?.CurrencyId ?? 0;
    }

    /// docs/04 §3.4: only NO 1 and NO 1 BB carry the smallest bucket.
    /// CLIENT-SIDE ONLY — Supabase has no grade_size table yet (MDM-004), so the Android app cannot
    /// enforce this and neither can the database. It belongs on the server.
    public static IReadOnlyList<SizeBucket> SizesFor(Grade? grade) =>
        grade is null || grade.Code is "NO 1" or "NO 1 BB"
            ? AllSizes
            : AllSizes.Where(s => s.Code != "-2").ToList();
}

/// A buyer or broker as the entry screen needs it: an id, a name, and its default.
public sealed record PartyRef(long Id, string Name, int? DefaultTermsDays = null, decimal? DefaultBrokerPct = null)
{
    public override string ToString() => Name;
}

public abstract class Notifier : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// One parcel line being typed. DiamondCalc drives the figures shown here so the user sees numbers
/// as they type — none of them is ever persisted. Postgres recomputes every amount on save.
/// </summary>
public sealed class SaleLine : Notifier
{
    private Grade? _grade;
    private SizeBucket? _size;
    private decimal _grossWeightCt, _selectionCt, _pricePerCt, _less1Pct, _less2Pct;
    private decimal _exRate = 1m;
    private decimal _rejectionCt, _amount;
    private string? _remark, _error;
    private bool _incomplete;

    public Grade? Grade
    {
        get => _grade;
        set
        {
            Set(ref _grade, value);
            Raise(nameof(AllowedSizes));
            if (_size is not null && !AllowedSizes.Contains(_size)) Size = null;   // grade_size, enforced at entry
        }
    }

    public IReadOnlyList<SizeBucket> AllowedSizes => Catalogue.SizesFor(_grade);

    public SizeBucket? Size { get => _size; set => Set(ref _size, value); }
    public decimal GrossWeightCt { get => _grossWeightCt; set => Set(ref _grossWeightCt, value); }
    public decimal SelectionCt { get => _selectionCt; set => Set(ref _selectionCt, value); }
    public decimal PricePerCt { get => _pricePerCt; set => Set(ref _pricePerCt, value); }
    public decimal ExRate { get => _exRate; set => Set(ref _exRate, value); }
    public decimal Less1Pct { get => _less1Pct; set => Set(ref _less1Pct, value); }
    public decimal Less2Pct { get => _less2Pct; set => Set(ref _less2Pct, value); }
    public string? Remark { get => _remark; set => Set(ref _remark, value); }

    public decimal RejectionCt { get => _rejectionCt; private set => Set(ref _rejectionCt, value); }
    public decimal Amount { get => _amount; private set => Set(ref _amount, value); }

    /// Non-null blocks the save and shows on the row. AC: "selection > weight … blocked with a clear message".
    public string? Error { get => _error; private set { Set(ref _error, value); Raise(nameof(HasConflict)); } }

    /// <summary>
    /// A line still being typed — a required value simply is not there yet — as opposed to one that
    /// contradicts itself. Both block the save; the difference is only whether the row is coloured.
    /// </summary>
    public bool IsIncomplete
    {
        get => _incomplete;
        private set { Set(ref _incomplete, value); Raise(nameof(HasConflict)); }
    }

    /// <summary>
    /// What the row's warning colour binds to. Picking a grade used to turn the row red instantly,
    /// because Weight was still 0 — the app shouting about a field the user was on their way to
    /// filling in. Red is now reserved for values that cannot be reconciled: selection above
    /// weight, a negative price, a size the grade does not carry.
    /// </summary>
    public bool HasConflict => _error is not null && !_incomplete;

    public bool IsBlank => _grade is null && _size is null && _grossWeightCt == 0 && _selectionCt == 0 && _pricePerCt == 0;

    internal void Recalculate(decimal brokerPct)
    {
        if (IsBlank) { Error = null; IsIncomplete = false; RejectionCt = 0; Amount = 0; return; }

        // Field rules first. The engine throws on the same bad input, but its message names a C#
        // parameter — a half-typed line used to read "grossWeightCt is out of range" instead of
        // "Grade is required", which is both cryptic and the wrong problem to point at.
        var (invalid, incomplete) = Validate();
        if (invalid is not null)
        {
            RejectionCt = 0;
            Amount = 0;
            IsIncomplete = incomplete;
            Error = invalid;
            return;
        }

        try
        {
            RejectionCt = Calc.Rejection(GrossWeightCt, SelectionCt);
            Amount = Calc.LineAmount(SelectionCt, PricePerCt, ExRate, Less1Pct, Less2Pct, brokerPct);
            IsIncomplete = false;
            Error = null;
        }
        catch (ArgumentException e)          // covers ArgumentOutOfRangeException too
        {
            RejectionCt = 0;
            Amount = 0;
            IsIncomplete = false;            // the engine only throws on values that contradict
            Error = e is ArgumentOutOfRangeException r ? $"{FieldName(r.ParamName)} is out of range" : e.Message;
        }
    }

    /// Turns an engine parameter name into the column heading the user is actually looking at.
    private static string FieldName(string? parameter) => parameter switch
    {
        "grossWeightCt" => "Weight",
        "selectionCt" => "Selection",
        "pricePerCt" => "Price/ct",
        "exRate" => "Ex Rate",
        "less1Pct" => "Less 1",
        "less2Pct" => "Less 2",
        "brokerPct" => "Broker %",
        _ => parameter ?? "A value",
    };

    /// <summary>
    /// The message, and whether it is merely "not typed yet". Both still block the save — the flag
    /// only decides whether the row is worth colouring while the user is still mid-line.
    /// </summary>
    private (string? Message, bool Incomplete) Validate()
    {
        if (Grade is null) return ("Grade is required", true);
        if (Size is null) return ("Size is required", true);
        if (GrossWeightCt <= 0) return ("Weight must be greater than 0", true);

        // Not "not yet" — these two are values that cannot both be right.
        if (!AllowedSizes.Contains(Size)) return ($"{Grade.DisplayName ?? Grade.Code} does not use size {Size.Code}", false);
        if (PricePerCt < 0) return ("Price cannot be negative", false);
        return (null, false);
    }
}

/// <summary>The invoice being typed. Header values apply to every line — broker % included (docs/03 C-7).</summary>
public sealed class InvoiceEntry : Notifier
{
    private DateTime _invoiceDate = DateTime.Today;
    private string? _buyer, _broker;
    private decimal _brokerPct;
    private int _termsDays;
    private string _docType = "BILL";

    public InvoiceEntry()
    {
        Lines.CollectionChanged += OnLinesChanged;
        Lines.Add(new SaleLine());
    }

    public ObservableCollection<SaleLine> Lines { get; } = [];

    public IReadOnlyList<Grade> Grades => Catalogue.Grades;
    public IReadOnlyList<string> DocTypes => Catalogue.DocTypes;

    public ObservableCollection<PartyRef> Buyers { get; } = [];
    public ObservableCollection<PartyRef> Brokers { get; } = [];

    private PartyRef? _selectedBuyer, _selectedBroker;

    public PartyRef? SelectedBuyer
    {
        get => _selectedBuyer;
        set
        {
            Set(ref _selectedBuyer, value);
            Buyer = value?.Name;
            BuyerId = value?.Id;
            if (value?.DefaultTermsDays is { } terms && TermsDays == 0) TermsDays = terms;
            Raise(nameof(Buyer));
        }
    }

    public PartyRef? SelectedBroker
    {
        get => _selectedBroker;
        set
        {
            Set(ref _selectedBroker, value);
            Broker = value?.Name;
            BrokerId = value?.Id;
            if (value?.DefaultBrokerPct is { } pct && BrokerPct == 0) BrokerPct = pct;
            Raise(nameof(Broker));
        }
    }

    public long? BuyerId { get; private set; }
    public long? BrokerId { get; private set; }

    /// Client-generated and offline-safe: it survives a retry whose response never arrived.
    public Guid ClientRef { get; } = Guid.CreateVersion7();

    /// The real primary key. Null until the first save comes back from Postgres.
    public long? InvoiceId { get; set; }

    public string Status { get; set; } = InvoiceStatus.DRAFT;

    public DateTime InvoiceDate { get => _invoiceDate; set { Set(ref _invoiceDate, value); Recalculate(); } }
    public string? Buyer { get => _buyer; set => Set(ref _buyer, value); }
    public string? Broker { get => _broker; set => Set(ref _broker, value); }
    public decimal BrokerPct { get => _brokerPct; set { Set(ref _brokerPct, value); Recalculate(); } }
    public int TermsDays { get => _termsDays; set { Set(ref _termsDays, value); Recalculate(); } }
    public string DocType { get => _docType; set => Set(ref _docType, value); }

    public decimal TotalCarats { get; private set; }
    public decimal TotalAmount { get; private set; }
    public decimal BlendedRate { get; private set; }

    /// CALC-10. Terms of 0 is valid and means the invoice date (docs/04 A-3).
    public DateOnly DueDate => Calc.DueDate(DateOnly.FromDateTime(InvoiceDate), Math.Max(TermsDays, 0));

    public IReadOnlyList<SaleLine> RealLines => Lines.Where(l => !l.IsBlank).ToList();

    /// How many lines actually carry data. Presentation only — it drives the "nothing typed yet"
    /// hint on the entry screen. RealLines is recomputed on demand and raises nothing, so a hint
    /// bound to it would never update.
    public int LineCount => RealLines.Count;

    public void Recalculate()
    {
        foreach (var line in Lines) line.Recalculate(BrokerPct);

        var real = RealLines;
        TotalCarats = real.Sum(l => l.SelectionCt);
        TotalAmount = Calc.InvoiceTotal(real.Select(l => l.Amount));
        BlendedRate = Calc.BlendedRate(TotalAmount, TotalCarats);

        Raise(nameof(TotalCarats));
        Raise(nameof(TotalAmount));
        Raise(nameof(BlendedRate));
        Raise(nameof(DueDate));
        Raise(nameof(LineCount));
    }

    /// Null when the invoice can be saved; otherwise the first thing wrong with it.
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Buyer)) return "Buyer is required";
        if (TermsDays < 0) return "Terms cannot be negative";
        if (RealLines.Count == 0) return "An invoice needs at least one line";

        var bad = RealLines.FirstOrDefault(l => l.Error is not null);
        return bad is null ? null : $"Line {Lines.IndexOf(bad) + 1}: {bad.Error}";
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (SaleLine line in e.OldItems ?? Array.Empty<object>()) line.PropertyChanged -= OnLineChanged;
        foreach (SaleLine line in e.NewItems ?? Array.Empty<object>()) line.PropertyChanged += OnLineChanged;
        Recalculate();
    }

    private void OnLineChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Derived properties are set by Recalculate itself — reacting to them would recurse.
        // IsIncomplete and HasConflict belong on this list for the same reason: Error's setter
        // raises HasConflict, so leaving it off sent Recalculate straight back into itself.
        if (e.PropertyName is nameof(SaleLine.RejectionCt) or nameof(SaleLine.Amount)
            or nameof(SaleLine.Error) or nameof(SaleLine.AllowedSizes)
            or nameof(SaleLine.IsIncomplete) or nameof(SaleLine.HasConflict)) return;
        Recalculate();
    }
}
