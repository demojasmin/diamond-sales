using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using DiamondCalc;

namespace DiamondDesktop;

public sealed record Grade(string Code, string DisplayName, Guid Id = default)
{
    public override string ToString() => DisplayName;
}

public sealed record SizeBucket(string Code, Guid Id = default)
{
    public override string ToString() => Code;
}

/// <summary>
/// Seed lists, verbatim from the workbook (docs/08 §2). In-memory until MDM-001/002/004 exist —
/// this screen is being built before its master-data screens on purpose (docs/05 §3 block 5).
/// </summary>
public static class Catalogue
{
    public static readonly SizeBucket Minus2 = new("-2");
    public static readonly SizeBucket Minus65 = new("-6.5");
    public static readonly SizeBucket Plus65 = new("+6.5");
    public static readonly SizeBucket Plus11 = new("+11");

    public static IReadOnlyList<SizeBucket> AllSizes { get; private set; } = [Minus2, Minus65, Plus65, Plus11];

    public static IReadOnlyList<Grade> Grades { get; private set; } =
    [
        new("NO_1", "NO 1"), new("NO_1_BB", "NO 1 BB"), new("NO_II", "NO II"), new("EX_1", "EX 1"),
        new("NO_2", "NO 2"), new("NO_DX", "NO DX"), new("NO_3", "NO 3"), new("NO_4", "NO 4"),
        new("NO_5", "NO 5"), new("NO_6", "NO 6"), new("NO_7", "NO 7"), new("TOP_COL", "TOP-COL"),
        new("COL", "COL"), new("OW", "OW"), new("LC_1", "LC-1"), new("LC_2", "LC-2"),
        new("LC_3", "LC-3"), new("GH", "GH"), new("LB_1", "LB-1"), new("LB_2", "LB-2"),
        new("PLUS_14", "+14"), new("EXTRA", "EXTRA"),
    ];

    private static Dictionary<string, IReadOnlyList<SizeBucket>>? _sizesByGrade;

    /// docs/04 §3.4: only NO 1 and NO 1 BB carry the smallest bucket. The other 20 use three sizes.
    /// Once the server has spoken (grade_size), its answer wins over this hard-coded rule.
    public static IReadOnlyList<SizeBucket> SizesFor(Grade? grade) =>
        grade is null ? AllSizes
        : _sizesByGrade is not null && _sizesByGrade.TryGetValue(grade.Code, out var sizes) ? sizes
        : grade.Code is "NO_1" or "NO_1_BB" ? AllSizes
        : [Minus65, Plus65, Plus11];

    /// Called once after login. Until then the screen runs on the seed above — which matches the server's.
    public static void Load(IEnumerable<Grade> grades, IEnumerable<SizeBucket> sizes,
                            Dictionary<string, IReadOnlyList<SizeBucket>> sizesByGrade)
    {
        Grades = grades.ToList();
        AllSizes = sizes.ToList();
        _sizesByGrade = sizesByGrade;
    }

    public static readonly IReadOnlyList<string> DocTypes = ["BILL"];
}

/// A buyer or broker as the entry screen needs it: an id, a name, and its default.
public sealed record PartyRef(Guid Id, string Name, int? DefaultTermsDays = null, decimal? DefaultBrokerPct = null)
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

/// <summary>One parcel line. Every derived value here comes from DiamondCalc — never from arithmetic typed twice.</summary>
public sealed class SaleLine : Notifier
{
    private Grade? _grade;
    private SizeBucket? _size;
    private decimal _grossWeightCt, _selectionCt, _pricePerCt, _less1Pct, _less2Pct;
    private decimal _exRate = 1m;
    private decimal _rejectionCt, _amount;
    private string? _remark, _error;

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
    public string? Error { get => _error; private set => Set(ref _error, value); }

    public bool IsBlank => _grade is null && _size is null && _grossWeightCt == 0 && _selectionCt == 0 && _pricePerCt == 0;

    internal void Recalculate(decimal brokerPct)
    {
        if (IsBlank) { Error = null; RejectionCt = 0; Amount = 0; return; }

        // Field rules first. The engine throws on the same bad input, but its message names a C#
        // parameter — a half-typed line used to read "grossWeightCt is out of range" instead of
        // "Grade is required", which is both cryptic and the wrong problem to point at.
        if (Validate() is { } invalid)
        {
            RejectionCt = 0;
            Amount = 0;
            Error = invalid;
            return;
        }

        try
        {
            RejectionCt = Calc.Rejection(GrossWeightCt, SelectionCt);
            Amount = Calc.LineAmount(SelectionCt, PricePerCt, ExRate, Less1Pct, Less2Pct, brokerPct);
            Error = null;
        }
        catch (ArgumentException e)          // covers ArgumentOutOfRangeException too
        {
            RejectionCt = 0;
            Amount = 0;
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

    private string? Validate()
    {
        if (Grade is null) return "Grade is required";
        if (Size is null) return "Size is required";
        if (!AllowedSizes.Contains(Size)) return $"{Grade.DisplayName} does not use size {Size.Code}";
        if (GrossWeightCt <= 0) return "Weight must be greater than 0";
        if (PricePerCt < 0) return "Price cannot be negative";
        return null;
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

    /// Filled from the API after login (MDM-002). Picking one sets Buyer and BuyerId together.
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

    public Guid? BuyerId { get; private set; }
    public Guid? BrokerId { get; private set; }
    public Guid InvoiceId { get; set; } = Guid.CreateVersion7();   // client-generated, offline-safe
    public string Status { get; set; } = "DRAFT";
    public bool Saved { get; set; }                                 // has the server seen this id yet?

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
        if (e.PropertyName is nameof(SaleLine.RejectionCt) or nameof(SaleLine.Amount)
            or nameof(SaleLine.Error) or nameof(SaleLine.AllowedSizes)) return;
        Recalculate();
    }
}
