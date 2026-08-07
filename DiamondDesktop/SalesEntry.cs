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
        var pairs = await Repo.GradeSizesAsync();
        var currencies = await Repo.CurrenciesAsync();

        Grades.Clear();
        foreach (var g in grades) Grades.Add(g);

        AllSizes.Clear();
        foreach (var s in sizes) AllSizes.Add(s);

        SetGradeSizes(pairs);

        // INR or nothing. Falling back to whatever sorted first stamped every invoice with an
        // arbitrary currency_id, which changes what each amount on it MEANS — and silently, since
        // the entry screen has no currency to show. Refusing the save is the honest failure.
        BaseCurrencyId = currencies.FirstOrDefault(c => c.Code.Equals("INR", StringComparison.OrdinalIgnoreCase))?.CurrencyId ?? 0;
    }

    /// Which sizes each grade trades in, straight from grade_size — the same table the sales_line
    /// trigger enforces (0018), so the picker can no longer offer a combination the save will reject.
    /// The old rule here was hardcoded ("everyone but NO 1 drops -2") and got +14 wrong: it sieves
    /// on +14/+18/+23 and on nothing else.
    private static readonly Dictionary<long, HashSet<long>> _gradeSizes = [];

    /// Every size that at least one grade trades in. Size is the FIRST column on the entry grid,
    /// so the picker is normally opened before a grade exists — and answering that with the whole
    /// size_bucket table offered 0.2 and 0.25, which are corrupt cells from the sales workbook
    /// kept only so the importer can resolve them. No grade trades them, so nothing should offer
    /// them.
    private static readonly HashSet<long> _sellableSizes = [];

    public static void SetGradeSizes(IEnumerable<GradeSize> pairs)
    {
        _gradeSizes.Clear();
        _sellableSizes.Clear();

        foreach (var p in pairs)
        {
            if (!_gradeSizes.TryGetValue(p.GradeId, out var set))
                _gradeSizes[p.GradeId] = set = [];
            set.Add(p.SizeId);
            _sellableSizes.Add(p.SizeId);
        }
    }

    /// <summary>
    /// The sizes a grade trades in — or, before a grade is chosen, every size that some grade
    /// trades in. Never the raw size_bucket table: that holds rows kept for the importer alone.
    ///
    /// Falls back to the full list only when grade_size has not loaded at all. Showing nothing
    /// there would read as "this grade sells nothing" rather than "the catalogue is still coming".
    /// </summary>
    /// <summary>Whether any grade trades this size at all. See <see cref="_sellableSizes"/>.</summary>
    public static bool IsSellableSize(string code) =>
        _sellableSizes.Count == 0
        || AllSizes.Any(s => s.Code == code && _sellableSizes.Contains(s.SizeId));

    public static IReadOnlyList<SizeBucket> SizesFor(Grade? grade)
    {
        if (_gradeSizes.Count == 0) return AllSizes;

        var allowed = grade is not null && _gradeSizes.TryGetValue(grade.GradeId, out var forGrade)
            ? forGrade
            : _sellableSizes;

        return AllSizes.Where(s => allowed.Contains(s.SizeId)).ToList();
    }
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
            Error = e is ArgumentOutOfRangeException r
                ? $"{FieldName(r.ParamName)} is out of range"
                : Sentence(e);
        }
    }

    /// <summary>
    /// The engine's own sentence, without the parameter clause .NET staples onto Message whenever
    /// a paramName was supplied. Out-of-range errors map to a column heading above; everything else
    /// used to reach the user reading "selection 15 exceeds gross 10 (Parameter 'selectionCt')" —
    /// a C# identifier in front of someone typing an invoice. Cut by ParamName rather than by
    /// splitting on a newline: the runtime joins it with a space, not a line break.
    /// </summary>
    private static string Sentence(ArgumentException e)
    {
        if (e.ParamName is null) return e.Message;
        int cut = e.Message.IndexOf($"(Parameter '{e.ParamName}')", StringComparison.Ordinal);
        return (cut >= 0 ? e.Message[..cut] : e.Message).TrimEnd();
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
        // Selection is deliberately NOT required to be above zero. A parcel can be rejected in
        // full, which is a real trade and leaves the line — and the whole invoice — at zero value.
        // That is why a zero-amount invoice can be posted (docs/11 §Gaps, item 4). Whether the app
        // should warn before posting one is a business decision, not a validation bug.
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

    /// <summary>
    /// Zero only while the screen is genuinely untouched: one row, nothing typed into it. The
    /// empty-state hint binds to this through the Empty converter, which shows on zero.
    ///
    /// It used to bind to LineCount, and a blank row is not a line — so pressing Enter nine times
    /// left the hint sitting on top of nine empty rows, telling the user to do the thing they had
    /// visibly started. Counting the extra rows as well as the filled ones fixes that: adding a row
    /// is starting work, even before anything is typed into it.
    /// </summary>
    public int EntriesStarted => Math.Max(Lines.Count - 1, 0) + RealLines.Count;

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
        Raise(nameof(EntriesStarted));
    }

    /// Null when the invoice can be saved; otherwise the first thing wrong with it.
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Buyer)) return "Buyer is required";

        // The same 0-365 rule the Add buyer dialog enforces (MainWindow.ValidateBuyer). It was
        // missing here, so terms of 9,999 days were accepted on the invoice itself and put the due
        // date decades out. One rule, both places.
        if (TermsDays is < 0 or > 365) return "Terms must be between 0 and 365 days";

        // Broker % is a header field, but the only thing checking it was Calc.Pct() throwing once
        // per line — so an out-of-range percentage reddened every row with "Broker % is out of
        // range" and sent the user hunting through the grid for a fault in the header.
        if (BrokerPct is < 0 or > 100) return "Broker % must be between 0 and 100";

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
