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

    /// <summary>
    /// The sizes new work may be booked against — AllSizes minus the retired ones.
    ///
    /// Two collections rather than one filtered at the point of use, because the pickers bind to
    /// this ONCE at load and the comment above holds: a list that gets replaced rather than filled
    /// in place leaves every bound ComboBox showing the catalogue as it was at startup. A LINQ
    /// Where would hand each of them a dead snapshot.
    ///
    /// AllSizes keeps every row on purpose. It resolves size_id to a code for the price grid and
    /// builds the stock report's columns, and stock under a retired sieve still exists — hiding it
    /// from the report would be preserving figures nobody can see.
    ///
    /// Maintained as a projection of AllSizes rather than filled alongside it. Filling both meant
    /// every caller that touched one had to remember the other, and the first one that forgot was
    /// the test harness — which seeds AllSizes directly and left the sales grid offering no sizes
    /// at all. A rule the code enforces beats a rule everybody has to remember.
    /// </summary>
    public static ObservableCollection<SizeBucket> ActiveSizes { get; } = [];

    /// <summary>
    /// The grades a SALE may be written against: the catalogue, less the two nobody sells from.
    ///
    ///   ZZ TEST         test data. It exists because somebody needed a grade to prove an import
    ///                   with, and it has no business being one keystroke from a real invoice.
    ///   Unknown Grade   0039's landing place for a sheet row that printed no grade name. It is a
    ///                   holding pen: the carats are real and belong on the Stock page, but the
    ///                   correction path is an ADJUST onto the right bucket, not a sale out of it.
    ///                   Selling from it would put a parcel on an invoice under a name that says
    ///                   the office does not know what it is.
    ///
    /// Hidden from the PICKER only. Both grades keep their rows, their stock, their history and
    /// their place on every report -- this changes what can be typed, not what exists, and no
    /// stock or reservation logic reads this list.
    ///
    /// A projection maintained in place, exactly as ActiveSizes is and for the same reason: the
    /// pickers bind once at load, so a list that gets replaced leaves every bound ComboBox holding
    /// the catalogue as it was at startup.
    /// </summary>
    public static ObservableCollection<Grade> SellableGrades { get; } = [];

    /// <summary>
    /// The codes a sale may not be written against, on the desk's instruction.
    ///
    /// Matched on CODE, trimmed and case-insensitively, because that is the field the import and
    /// the master-data screen both write. Names can be edited; a code is a fixed point.
    ///
    /// "+14" IS DIFFERENT FROM THE OTHER TWO and worth knowing before this list is trusted. ZZ TEST
    /// is test data and Unknown Grade is a holding pen, so neither should ever be sold from. "+14"
    /// is a grade that HOLDS REAL CARATS on Demo, and hiding it here means those carats cannot be
    /// put on an invoice from this screen at all. That is what was asked for; it is one line to
    /// undo if it turns out to have been a sieve label that landed in the grade column rather than
    /// a grade nobody trades.
    /// </summary>
    private static readonly string[] NotForSale = ["ZZ TEST", "Unknown Grade", "+14"];

    private static bool Sellable(Grade g) =>
        !NotForSale.Contains(g.Code?.Trim(), StringComparer.OrdinalIgnoreCase);

    static Catalogue()
    {
        // ponytail: rebuilds the whole list per change — the catalogue is a dozen sieves, and it
        // is written twice a session. Index the delta if that ever stops being true.
        AllSizes.CollectionChanged += (_, _) =>
        {
            ActiveSizes.Clear();
            foreach (var s in AllSizes.Where(s => s.Active)) ActiveSizes.Add(s);
        };

        Grades.CollectionChanged += (_, _) =>
        {
            SellableGrades.Clear();
            foreach (var g in Grades.Where(Sellable)) SellableGrades.Add(g);
        };
    }

    /// <summary>
    /// What the desk is writing: a bill, a sale off the books, an export, or one priced in
    /// dollars. sales_invoice.doc_type is varchar(20) with no CHECK constraint -- docs/03 Q9
    /// records that as deliberately open -- so these need no migration.
    ///
    /// UPPERCASE because that is what the sale workbook importer stores: ExcelImport upper-cases
    /// column P, and a picker offering "Export" beside imported rows reading "EXPORT" would be
    /// two spellings of one thing in one column.
    ///
    /// BILL stays first: it is the default on a new invoice and the overwhelming majority of them.
    ///
    /// Nothing filters on doc_type -- not the reconciliation, the ageing bands or the margin
    /// views -- so an invoice written under any of these is counted everywhere a BILL is. That is
    /// the intended behaviour and it is why this is a one-line change; if a report should ever
    /// EXCLUDE a type, that is a decision to make there and not by leaving the type unavailable.
    ///
    /// A DOLLAR BILL is priced per carat in dollars and converted by the line's own Ex Rate, which
    /// the entry grid already carries. There is still no currency picker: the invoice totals in
    /// INR either way, which is what sales_invoice stores.
    /// </summary>
    public static readonly IReadOnlyList<string> DocTypes =
        ["BILL", "WITHOUT BILL", "EXPORT", "DOLLAR BILL"];

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
        || AllSizes.Any(s => s.Code == code && s.Active && _sellableSizes.Contains(s.SizeId));

    /// <summary>
    /// Whether a sieve has been retired: still in the catalogue so history reads, closed to new
    /// work. Distinct from "import only" — that is a size no grade sells, which is a statement
    /// about the grade pairings, not about whether the office still uses the sieve at all.
    /// </summary>
    /// <summary>
    /// Matched on the sieve key, not the text, so a file printing "6.5+" recognises the retired
    /// "+6.5" as the same sieve. Without that the import offers to ADD a size the database will
    /// only ever restore, and the popup describes the wrong act.
    /// </summary>
    public static bool IsRetiredSize(string printed) =>
        AllSizes.Any(s => !s.Active
                          && StockFileImport.SizeKey(s.Code) == StockFileImport.SizeKey(printed));

    public static IReadOnlyList<SizeBucket> SizesFor(Grade? grade)
    {
        // ActiveSizes, not AllSizes: a retired sieve may still be paired in grade_size — 0035
        // switches the size off and leaves the pairings alone — so the pairing check below would
        // happily offer it. Nothing on the sales screen may write to a retired size.
        if (_gradeSizes.Count == 0) return ActiveSizes;

        var allowed = grade is not null && _gradeSizes.TryGetValue(grade.GradeId, out var forGrade)
            ? forGrade
            : _sellableSizes;

        return ActiveSizes.Where(s => allowed.Contains(s.SizeId)).ToList();
    }
}

/// A buyer or broker as the entry screen needs it: an id, a name, and its default.
/// <param name="Active">
/// False only for a party carried on an invoice that was written before it was deactivated. Such a
/// one is added to THAT invoice's own picker so the name it was billed to stays on screen, and is
/// filtered out when a new invoice is started -- an inactive buyer must not be selectable for new
/// trade, and must not silently vanish from old trade either.
/// </param>
public sealed record PartyRef(long Id, string Name, int? DefaultTermsDays = null,
                              decimal? DefaultBrokerPct = null, bool Active = true)
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
    private bool _incomplete, _selected;

    /// <summary>
    /// Ticked in the grid's first column, for deleting several lines at once.
    ///
    /// SELECTION ONLY. It is never saved, never posted and never counted -- IsBlank ignores it, so
    /// ticking an empty row does not turn it into a line, and RealLines is the same list whether
    /// anything is ticked or not. A tick that changed what sells would be a second, invisible way
    /// to alter an invoice.
    /// </summary>
    public bool Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    public Grade? Grade
    {
        get => _grade;
        set
        {
            // A SEARCH MISS IS NOT A CLEAR. An editable ComboBox writes SelectedItem = null the
            // moment its text stops matching an item, so typing one character over a chosen grade
            // emptied the cell -- the weight and remark stayed, and the bucket silently went. The
            // cell offers no blank to choose, so a null arriving while there is text in the box can
            // only have come from the search, and it is refused. Picker_LostFocus puts the chosen
            // name back when the cell is left, so nothing is stranded.
            //
            // Keyed on the null itself, not on whether the box has text: WPF nulls the selection
            // and rewrites Text in an order that is not worth depending on, and a guard that
            // guessed at that order let the clear through anyway. NOTHING in this app clears a
            // grade -- the one place that clears a SIZE writes the field directly, below -- so a
            // null arriving here has only one possible source.
            if (value is null && _grade is not null) return;

            Set(ref _grade, value);
            Raise(nameof(AllowedSizes));

            // The box shows what was chosen, not what was typed to find it.
            _gradeFilter = value?.ShortName ?? "";
            Raise(nameof(GradeFilter));
            Raise(nameof(GradePlaceholder));

            // NOT GradeChoices. Raising it here swapped the ComboBox's ItemsSource in the middle of
            // WPF applying this very selection: the chosen item was not in the new list, so WPF
            // wrote SelectedItem back to null, which re-entered this setter, which raised again.
            // That is unbounded recursion, and a StackOverflowException takes the process down
            // without reaching App's DispatcherUnhandledException handler -- the app simply
            // vanished the moment a size or grade was picked.
            //
            // Nothing is lost by leaving it: GradeChoices already reads as the FULL list once the
            // box matches what is chosen (see below), and the filter's own setter raises it on the
            // next keystroke and on the way out of the cell.
            // grade_size, enforced at entry: the size chosen is not offered under the new grade.
            // Written through the FIELD, because the setter refuses nulls so that a search miss
            // cannot empty the cell, and this is the one clear the code actually means. Raising
            // Size by hand keeps everything downstream identical -- OnLineChanged still sees it,
            // so the hold on the old bucket is still released.
            if (_size is not null && !AllowedSizes.Contains(_size))
            {
                _size = null;
                _sizeFilter = "";
                Raise(nameof(Size));
                Raise(nameof(SizeFilter));
                Raise(nameof(SizePlaceholder));
            }
        }
    }

    public IReadOnlyList<SizeBucket> AllowedSizes => Catalogue.SizesFor(_grade);

    // ── typing in a picker narrows it ──────────────────────────────────────
    //
    // PER LINE, and that is the whole reason these exist rather than a CollectionView filter.
    // WPF hands every ItemsControl bound to the same collection the SAME default view, so
    // filtering it in one cell would filter the picker in every other row at once. A list per
    // line cannot do that to its neighbours.
    //
    // The text is held here too, so a half-typed word survives the row scrolling out of view and
    // its container being recycled.

    private string _gradeFilter = "", _sizeFilter = "";

    /// What has been typed into the Grade box. Setting it re-narrows GradeChoices and nothing else
    /// -- in particular it never touches Grade, so text matching nothing cannot silently unset a
    /// bucket that is already chosen.
    public string GradeFilter
    {
        get => _gradeFilter;
        set { Set(ref _gradeFilter, value ?? ""); Raise(nameof(GradeChoices)); Raise(nameof(GradePlaceholder)); }
    }

    public string SizeFilter
    {
        get => _sizeFilter;
        set { Set(ref _sizeFilter, value ?? ""); Raise(nameof(SizeChoices)); Raise(nameof(SizePlaceholder)); }
    }

    /// <summary>
    /// The choices, narrowed to what has been typed.
    ///
    /// Text that EQUALS what is already chosen is not a search -- it is the box showing the
    /// selection. Narrowing on it would leave the drop-down offering the one grade already picked,
    /// so opening it again to change your mind showed a list of one.
    /// </summary>
    public IReadOnlyList<Grade> GradeChoices =>
        _gradeFilter == (_grade?.ShortName ?? "")
            ? Catalogue.SellableGrades
            : Narrow(Catalogue.SellableGrades, _gradeFilter, g => g.ShortName, g => g.Code);

    public IReadOnlyList<SizeBucket> SizeChoices =>
        _sizeFilter == (_size?.ShortName ?? "")
            ? AllowedSizes
            : Narrow(AllowedSizes, _sizeFilter, z => z.ShortName, z => z.Code);

    /// <summary>
    /// The rows whose name or code CONTAINS what was typed, case- and space-insensitively.
    ///
    /// Contains rather than StartsWith: the sheet writes "NO 1 BB" and the desk says "BB", and a
    /// picker that only answers to the first letter is barely faster than scrolling. Spaces are
    /// dropped from both sides so "no1" finds "NO 1".
    ///
    /// An empty box returns EVERYTHING, and so does a search matching nothing -- an empty
    /// drop-down under a typo looks like a broken picker, and the text is about to be reverted
    /// anyway. See MainWindow's Picker_LostFocus.
    /// </summary>
    private static IReadOnlyList<T> Narrow<T>(IReadOnlyList<T> all, string typed,
                                              params Func<T, string?>[] fields)
    {
        string want = typed.Replace(" ", "").Trim();
        if (want.Length == 0) return all;

        var hit = all.Where(x => fields.Any(f =>
                       f(x)?.Replace(" ", "").Contains(want, StringComparison.OrdinalIgnoreCase) == true))
                     .ToList();
        return hit.Count > 0 ? hit : all;
    }

    /// The grey hint shows only on a cell that is both unchosen AND unstarted. An editable combo
    /// draws the typed text in the same place, so keying off the selection alone left the hint
    /// sitting on top of what was being typed.
    public bool GradePlaceholder => _grade is null && _gradeFilter.Length == 0;
    public bool SizePlaceholder  => _size  is null && _sizeFilter.Length  == 0;



    /// <summary>
    /// The catalogue was reloaded underneath this line.
    ///
    /// Two things go wrong without this. AllowedSizes is a computed property that only announces
    /// itself when Grade is set, so a sieve added or retired since sign-in never reaches the
    /// picker. And the reload replaces every Grade and SizeBucket with a new object while the
    /// line still holds the old one — the pickers bind SelectedItem by REFERENCE, so both cells
    /// would blank out and a half-typed line would look like lost work.
    ///
    /// Re-pointed by code rather than by id, because a size that was deleted and created again
    /// keeps its code and not its id, and the code is what the person typed against.
    /// </summary>
    public void CatalogueChanged()
    {
        // _gradeWas first, because by the time this runs the picker has usually already nulled
        // _grade -- see RememberCatalogue.
        if ((_gradeWas ?? _grade?.Code) is { } gc)
            _grade = Catalogue.Grades.FirstOrDefault(x => x.Code == gc) ?? _grade;
        if ((_sizeWas ?? _size?.Code) is { } sc)
            _size = Catalogue.AllSizes.FirstOrDefault(x => x.Code == sc) ?? _size;

        _gradeWas = _sizeWas = null;

        Raise(nameof(Grade));
        Raise(nameof(AllowedSizes));
        Raise(nameof(Size));
    }

    /// <summary>
    /// What this line was showing, captured BEFORE the catalogue is reloaded.
    ///
    /// CatalogueChanged alone was not enough and the reason is WPF, not this class. The pickers
    /// bind SelectedItem to a ComboBox whose ItemsSource IS Catalogue.Grades; clearing that
    /// collection makes the Selector drop its selection and write the resulting null straight back
    /// down the two-way binding. So the line is already blank before the repair runs, and
    /// re-pointing "the object it still holds" has nothing to re-point.
    ///
    /// Weight, selection and price survived a reload precisely because nothing about them is bound
    /// to a collection -- which is what made the loss look arbitrary rather than mechanical.
    /// </summary>
    public void RememberCatalogue()
    {
        _gradeWas = _grade?.Code;
        _sizeWas = _size?.Code;
    }

    private string? _gradeWas, _sizeWas;

    public SizeBucket? Size
    {
        get => _size;
        set
        {
            // Same guard as Grade above, for the same reason. The one deliberate clear bypasses it
            // by writing the field.
            if (value is null && _size is not null) return;

            Set(ref _size, value);
            _sizeFilter = value?.ShortName ?? "";
            Raise(nameof(SizeFilter));
            Raise(nameof(SizePlaceholder));
            // NOT SizeChoices, for the reason spelled out in the Grade setter above.
        }
    }
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

    /// <summary>
    /// This row's handle, for the reservation it holds against stock (0043).
    ///
    /// Created with the line and never changed, which is what makes re-reserving an EDIT rather
    /// than a second hold: the database keys on (client_ref, line_key), so correcting 10.00 to
    /// 1.00 updates the same row instead of holding 11.
    ///
    /// Not the line_id -- a line being typed has no sales_line row yet, and the whole point is
    /// that stock moves before anything is saved.
    /// </summary>
    /// <summary>
    /// This line's half of the reservation key. init, not get-only, so a Sales entry restored from
    /// disk carries the keys its holds were made under -- re-sending them corrects those holds
    /// instead of adding a second set (EntryStore).
    /// </summary>
    public Guid LineKey { get; init; } = Guid.CreateVersion7();

    // ── the deal this line is sold under ───────────────────────────────────
    //
    // PER LINE, on the desk's instruction: every row may name its own buyer, broker, broker
    // percentage, terms and document type.
    //
    // WHAT THE DATABASE CAN HOLD. These six are columns on sales_invoice, not on sales_line --
    // v_sales_line.amount multiplies by the INVOICE's broker_pct, a receipt and an outstanding
    // balance and a credit limit and an ageing bucket all key on invoice.buyer_id, and a printed
    // bill carries one buyer's name at the top. So a row carrying its own deal cannot mean a
    // second buyer on one invoice; it means the entry produces MORE THAN ONE invoice.
    //
    // Rows whose deal is identical are one invoice with several lines -- which is why an entry
    // where every row names the same buyer still writes exactly one invoice, as it always did.
    // See InvoiceEntry.Deals.

    private PartyRef? _dealBuyer, _dealBroker;
    private decimal _dealBrokerPct;
    private int _dealTermsDays;
    private string _dealDocType = "BILL";

    public PartyRef? DealBuyer
    {
        get => _dealBuyer;
        set { Set(ref _dealBuyer, value); RaiseDeal(); }
    }

    public PartyRef? DealBroker
    {
        get => _dealBroker;
        set { Set(ref _dealBroker, value); RaiseDeal(); }
    }

    public decimal DealBrokerPct
    {
        get => _dealBrokerPct;
        set { Set(ref _dealBrokerPct, value); RaiseDeal(); }
    }

    public int DealTermsDays
    {
        get => _dealTermsDays;
        set { Set(ref _dealTermsDays, value); RaiseDeal(); }
    }

    public string DealDocType
    {
        get => _dealDocType;
        set { Set(ref _dealDocType, string.IsNullOrWhiteSpace(value) ? "BILL" : value); RaiseDeal(); }
    }

    /// <summary>
    /// The date this line's money is due. Derived, not stored: it is the invoice date plus this
    /// line's own terms, which is the rule the header has always stated and the one the database
    /// applies (sales_invoice carries terms_days, never a due date).
    ///
    /// SETTABLE, because the drawer offers it as a field and a desk thinks in dates as often as in
    /// day counts. Setting it writes TERMS, so the two can never disagree -- and a date before the
    /// invoice date is refused rather than stored as negative terms.
    /// </summary>
    public DateOnly DealDueDate
    {
        get => Calc.DueDate(DateOnly.FromDateTime(_dealInvoiceDate), Math.Max(_dealTermsDays, 0));
        set
        {
            int days = value.DayNumber - DateOnly.FromDateTime(_dealInvoiceDate).DayNumber;
            DealTermsDays = Math.Clamp(days, 0, 365);
        }
    }

    /// <summary>
    /// The date this line's invoice is written on, and what its due date is measured from.
    ///
    /// PART OF THE DEAL, so it sits in the drawer with the rest of it and two rows dated
    /// differently are two invoices. sales_invoice.invoice_date is per invoice, so this costs
    /// nothing to express -- and a desk back-dating one parcel while today's others stand is a
    /// real thing to want.
    ///
    /// The header's Date is the SEED: it fills this in on every new row, so an entry written on
    /// one day still behaves as one date unless a line is deliberately changed.
    /// </summary>
    public DateTime DealDate
    {
        get => _dealInvoiceDate;
        set { Set(ref _dealInvoiceDate, value); RaiseDeal(); }
    }

    private DateTime _dealInvoiceDate = DateTime.Today;

    internal void SetInvoiceDate(DateTime date)
    {
        if (_dealInvoiceDate == date) return;
        _dealInvoiceDate = date;
        Raise(nameof(DealDate));
        RaiseDeal();
    }

    private void RaiseDeal()
    {
        Raise(nameof(DealDueDate));
        Raise(nameof(DealSummary));
        Raise(nameof(DealTerms));
        Raise(nameof(DealLine2));
        Raise(nameof(HasBuyer));
        Raise(nameof(DealProblem));
        Raise(nameof(DealKey));
    }

    /// <summary>
    /// What the grid's Deal Details cell reads. Three lines: buyer, broker, then the terms.
    ///
    /// Presentation only, and deliberately not the thing anything groups on -- see DealKey. Two
    /// deals that print the same summary but differ in a field the summary does not show must
    /// still be two invoices.
    /// </summary>
    public string DealBuyerName => _dealBuyer?.Name ?? "No buyer";
    public string DealBrokerName => _dealBroker?.Name ?? "No broker";
    public string DealTerms => $"{_dealBrokerPct:0.##}% · {Math.Max(_dealTermsDays, 0)}d · {_dealDocType}";

    /// <summary>
    /// The cell's second line: broker and terms together.
    ///
    /// Three stacked lines needed about 48px and the grid's rows are 42, so the third was sliced
    /// through the middle -- the deal was the one column on this screen you could not actually
    /// read. Two lines fit with room to spare, and DealSummary still carries all of it on the
    /// tooltip, so nothing is lost, only folded.
    /// </summary>
    public string DealLine2 =>
        (_dealBroker is null ? "No broker" : _dealBroker.Name) + " · " + DealTerms;
    public string DealSummary => $"{DealBuyerName} · {DealBrokerName} · {DealTerms}";

    /// A line with no buyer cannot be sold: sales_invoice.buyer_id is NOT NULL, and the cell says so
    /// in place of a name rather than leaving the row looking finished.
    public bool HasBuyer => _dealBuyer is not null;

    /// <summary>
    /// What is wrong with this line's DEAL, or null. Blocks the save; does NOT block the sums.
    ///
    /// Separate from Error, and that separation is the point. Error means the figures cannot be
    /// computed -- no grade, no weight, a selection above the gross -- so Recalculate zeroes the
    /// row and waits. A missing buyer is not that: the carats, the rejection and the amount are
    /// all perfectly well defined, the desk simply has not said who is buying yet. Folding it into
    /// Error blanked the Amount column on every row until a buyer was picked, which on a
    /// keyboard-first screen means the figures disappear while they are being typed.
    ///
    /// So: reported by Problems(), shown on the Deal Details cell, and ignored by the arithmetic.
    /// </summary>
    public string? DealProblem =>
        _dealBuyer is null ? "Buyer is required — open Deal Details on this line"
        : _dealTermsDays is < 0 or > 365 ? "Terms must be between 0 and 365 days"
        : _dealBrokerPct is < 0 or > 100 ? "Broker % must be between 0 and 100"
        : null;

    /// <summary>
    /// What makes two lines the SAME invoice. Every field the database stores on sales_invoice,
    /// and nothing else -- the due date is derived from terms, so including it would be counting
    /// the same fact twice.
    /// </summary>
    public (DateOnly Date, long? Buyer, long? Broker, decimal Pct, int Terms, string Doc) DealKey =>
        (DateOnly.FromDateTime(_dealInvoiceDate), _dealBuyer?.Id, _dealBroker?.Id,
         _dealBrokerPct, Math.Max(_dealTermsDays, 0), _dealDocType);

    /// <summary>Copies another line's deal onto this one. The drawer's Save, and a new row's default.</summary>
    public void TakeDealFrom(SaleLine other)
    {
        _dealBuyer = other._dealBuyer;
        _dealBroker = other._dealBroker;
        _dealBrokerPct = other._dealBrokerPct;
        _dealTermsDays = other._dealTermsDays;
        _dealDocType = other._dealDocType;
        _dealInvoiceDate = other._dealInvoiceDate;

        Raise(nameof(DealBuyer)); Raise(nameof(DealBroker)); Raise(nameof(DealBrokerPct));
        Raise(nameof(DealTermsDays)); Raise(nameof(DealDocType)); Raise(nameof(DealDate));
        Raise(nameof(DealBuyerName)); Raise(nameof(DealBrokerName));
        RaiseDeal();
    }

    public bool IsBlank => _grade is null && _size is null && _grossWeightCt == 0 && _selectionCt == 0 && _pricePerCt == 0;

    /// <summary>
    /// Whether this row can be ticked. FALSE for the empty row at the bottom.
    ///
    /// Ticking a blank row did nothing -- Remove counts real lines only -- so the box sat there
    /// filled in, claiming to have selected something, while the Remove button stayed away. A
    /// control that accepts a click and then ignores it is worse than one that is not there.
    /// </summary>
    public bool Selectable => !IsBlank;


    internal void Recalculate(decimal brokerPct)
    {
        Raise(nameof(Selectable));

        if (IsBlank)
        {
            // A row that has just been emptied cannot stay ticked, or the count would include a
            // line Remove will not touch.
            Selected = false;
            Error = null; IsIncomplete = false; RejectionCt = 0; Amount = 0;
            return;
        }

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
        if (!AllowedSizes.Contains(Size)) return ($"{Grade.ShortName} does not use size {Size.Code}", false);
        if (PricePerCt < 0) return ("Price cannot be negative", false);
        return (null, false);
    }
}

/// <summary>
/// One numbered button in the pager. <paramref name="Current"/> travels WITH the number rather
/// than being worked out in a trigger: the row is an ItemsControl, and an item that cannot say
/// whether it is the page you are on would need each button to reach back up the tree to ask.
/// </summary>
public sealed record PageChip(int Number, bool Current);

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

        // ONE OPENING ROW, on the desk's word: a keyboard-first screen should be typeable the
        // moment it appears, not after a click on Add line.
        //
        // It was taken out for a fair reason -- an untouched screen still read "1 line", and a
        // finished invoice trailed an empty row under its last parcel. That cost is real but it is
        // cosmetic, and it is already paid for elsewhere: the chip counts RealLines, so a blank row
        // is not counted, and Problems() refuses an invoice that has no real line on it. The row
        // costs nothing and saves a click on every invoice.
        Lines.Add(new SaleLine());
    }

    /// <summary>Capture what the screen is showing, BEFORE a reload. See <see cref="SaleLine.RememberCatalogue"/>.</summary>
    public void RememberCatalogue()
    {
        // The party, not its id. A reload refills Buyers from the ACTIVE list, so an invoice
        // written to a buyer that has since been deactivated would find nothing to re-point to and
        // lose the name off the screen. Holding the row itself means it can be put back.
        _buyerWas = _selectedBuyer;
        _brokerWas = _selectedBroker;
        foreach (var line in Lines) line.RememberCatalogue();
    }

    /// <summary>Put it back afterwards. See <see cref="SaleLine.CatalogueChanged"/>.</summary>
    public void CatalogueChanged()
    {
        // The fields, not the SelectedBuyer property: its setter fills in the party's default terms
        // and broker percentage, which is right when a person picks a buyer and wrong when a reload
        // re-points the same one. Restoring is not choosing.
        if (_buyerWas is { } b)
        {
            // Not in the reloaded list and known to be inactive: this invoice's own buyer, put back
            // on this invoice's own picker. Never for an active one that simply failed to reload --
            // that is a read problem, and inventing the row would hide it.
            var buyer = Buyers.FirstOrDefault(x => x.Id == b.Id);
            if (buyer is null && !b.Active) { Buyers.Add(b); buyer = b; }
            if (buyer is not null) { _selectedBuyer = buyer; Buyer = buyer.Name; BuyerId = buyer.Id; }
        }
        if (_brokerWas is { } r)
        {
            var broker = Brokers.FirstOrDefault(x => x.Id == r.Id);
            if (broker is null && !r.Active) { Brokers.Add(r); broker = r; }
            if (broker is not null) { _selectedBroker = broker; Broker = broker.Name; BrokerId = broker.Id; }
        }
        _buyerWas = _brokerWas = null;

        Raise(nameof(SelectedBuyer));
        Raise(nameof(Buyer));
        Raise(nameof(SelectedBroker));
        Raise(nameof(Broker));

        foreach (var line in Lines) line.CatalogueChanged();
    }

    private PartyRef? _buyerWas, _brokerWas;

    public ObservableCollection<SaleLine> Lines { get; } = [];

    public IReadOnlyList<Grade> Grades => Catalogue.Grades;
    /// <summary>
    /// The document types this invoice may carry.
    ///
    /// PER INVOICE, not the static catalogue list, for the same reason the buyer list is: an
    /// imported sheet can carry a type this app does not offer. Bound to a list without it, the
    /// ComboBox finds no match, leaves SelectedItem null, and writes that null straight back down
    /// the two-way binding -- so merely opening the invoice blanked a field nobody had touched,
    /// and saving stored the blank. The picker carries whatever the invoice already says.
    /// </summary>
    public IReadOnlyList<string> DocTypes => _docTypes;

    private readonly System.Collections.ObjectModel.ObservableCollection<string> _docTypes = new(Catalogue.DocTypes);

    /// <summary>
    /// Puts a document type this invoice already carries into the picker, if the catalogue does
    /// not offer it. Call BEFORE setting DocType, or the binding has already nulled it.
    /// </summary>
    public void KeepDocType(string? docType)
    {
        if (!string.IsNullOrWhiteSpace(docType) && !_docTypes.Contains(docType))
            _docTypes.Add(docType);
    }

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
            // Down onto every line, for the reason spelled out above the header fields.
            foreach (var line in Lines) line.DealBuyer = value;
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
            foreach (var line in Lines) line.DealBroker = value;
            if (value?.DefaultBrokerPct is { } pct && BrokerPct == 0) BrokerPct = pct;
            Raise(nameof(Broker));
        }
    }

    public long? BuyerId { get; private set; }
    public long? BrokerId { get; private set; }

    /// Client-generated and offline-safe: it survives a retry whose response never arrived.
    public Guid ClientRef { get; init; } = Guid.CreateVersion7();

    /// The real primary key. Null until the first save comes back from Postgres.
    public long? InvoiceId { get; set; }

    public string Status { get; set; } = InvoiceStatus.DRAFT;

    public DateTime InvoiceDate
    {
        get => _invoiceDate;
        set
        {
            Set(ref _invoiceDate, value);
            // Every line's due date is measured from it, and the line answers for its own.
            foreach (var line in Lines) line.SetInvoiceDate(value);
            Recalculate();
        }
    }

    // ── the header fields ──────────────────────────────────────────────────
    //
    // NOT on the Sales entry screen any more: the deal is the LINE's, and this markup no longer
    // offers a global buyer or broker. They stay on the model because the Update modal and a
    // reopened draft both edit an invoice that already IS one deal, where the header is the only
    // place that deal can live.
    //
    // Setting one PUSHES IT DOWN to every line, which is what keeps those two paths honest: an
    // invoice opened for correction has one buyer, so its lines must all carry it, so Deals sees
    // one group and writes one invoice back. Without the push-down a corrected invoice would come
    // back as a row of buyer-less lines.
    public string? Buyer { get => _buyer; set => Set(ref _buyer, value); }
    public string? Broker { get => _broker; set => Set(ref _broker, value); }

    public decimal BrokerPct
    {
        get => _brokerPct;
        set
        {
            Set(ref _brokerPct, value);
            foreach (var line in Lines) line.DealBrokerPct = value;
            Recalculate();
        }
    }

    public int TermsDays
    {
        get => _termsDays;
        set
        {
            Set(ref _termsDays, value);
            foreach (var line in Lines) line.DealTermsDays = value;
            Recalculate();
        }
    }

    public string DocType
    {
        get => _docType;
        set
        {
            Set(ref _docType, value);
            foreach (var line in Lines) line.DealDocType = value;
        }
    }

    public decimal TotalCarats { get; private set; }
    public decimal TotalAmount { get; private set; }
    public decimal BlendedRate { get; private set; }

    /// CALC-10. Terms of 0 is valid and means the invoice date (docs/04 A-3).
    public DateOnly DueDate => Calc.DueDate(DateOnly.FromDateTime(InvoiceDate), Math.Max(TermsDays, 0));

    public IReadOnlyList<SaleLine> RealLines => Lines.Where(l => !l.IsBlank).ToList();

    // ── what the grid shows: the same lines, narrowed and paged ────────────
    //
    // THE SAME OBJECTS, and that is the whole safety story. VisibleLines holds references to the
    // very SaleLine instances in Lines, so a cell edited through the grid edits the line itself:
    // the hold it placed, the totals, Problems() and the payload all read Lines and never this.
    // Filtering and paging change WHAT IS ON SCREEN and nothing else. A view that copied its rows
    // would silently detach every edit from the invoice.

    /// <summary>The rows the grid is bound to: Lines, less what the filters exclude, one page at a time.</summary>
    public ObservableCollection<SaleLine> VisibleLines { get; } = [];

    /// 20, matching the reference. A page is a screenful; more and the point of paging is lost,
    /// fewer and an ordinary invoice needs paging it never wanted.
    public const int DefaultPageSize = 20;

    /// What the "Show on page by" list offers. A fixed set rather than a free number: a typed page
    /// size is a box to validate and a way to ask for one row a page.
    public static readonly IReadOnlyList<int> PageSizes = [10, 20, 50, 100];

    private int _pageSize = DefaultPageSize;

    /// <summary>
    /// How many rows a page holds. Changing it goes back to the FIRST page: the row that was on
    /// screen is on a different page now whatever we do, and "page 1" is the one answer that is
    /// never a page that no longer exists.
    /// </summary>
    public int PageSize
    {
        get => _pageSize;
        set
        {
            if (value <= 0 || value == _pageSize) return;
            _pageSize = value;
            _page = 0;
            Raise(nameof(PageSize));
            Repage();
        }
    }

    private DateTime? _filterDate;
    private PartyRef? _filterBuyer, _filterBroker;
    private string? _filterType;
    private int _page;

    /// <summary>
    /// The four filters. Null on each means "no opinion", so an untouched screen shows everything --
    /// the default the desk asked for, and the reason none of these is an enum with an "All"
    /// member: absence is easier to reason about than a sentinel.
    /// </summary>
    public DateTime? FilterDate   { get => _filterDate;   set { Set(ref _filterDate, value);   Repage(); } }
    public PartyRef? FilterBuyer  { get => _filterBuyer;  set { Set(ref _filterBuyer, value);  Repage(); } }
    public PartyRef? FilterBroker { get => _filterBroker; set { Set(ref _filterBroker, value); Repage(); } }
    public string?   FilterType   { get => _filterType;   set { Set(ref _filterType, value);   Repage(); } }

    public bool AnyFilter => _filterDate is not null || _filterBuyer is not null
                          || _filterBroker is not null || !string.IsNullOrWhiteSpace(_filterType);

    /// <summary>Every line the filters admit, in the order it was typed.</summary>
    public IReadOnlyList<SaleLine> FilteredLines =>
        Lines.Where(l =>
                 (_filterDate is not { } d || l.DealDate.Date == d.Date)
              && (_filterBuyer is not { } b || l.DealBuyer?.Id == b.Id)
              && (_filterBroker is not { } r || l.DealBroker?.Id == r.Id)
              && (string.IsNullOrWhiteSpace(_filterType)
                  || string.Equals(l.DealDocType, _filterType, StringComparison.OrdinalIgnoreCase)))
             .ToList();

    public int PageCount => Math.Max(1, (FilteredLines.Count + PageSize - 1) / PageSize);

    /// Clamped on read as well as on write: rows are removed from under it, and a page number
    /// pointing past the end would show an empty grid with lines still on the invoice.
    public int Page
    {
        get => Math.Clamp(_page, 0, PageCount - 1);
        set { _page = Math.Clamp(value, 0, PageCount - 1); Repage(); }
    }

    public bool HasPages => PageCount > 1;
    public string PageLabel => $"Page {Page + 1} of {PageCount}";

    /// Whether the chevrons can go anywhere. Disabled at the ends rather than hidden: a control
    /// that moves out from under the cursor is worse than one that is plainly spent.
    public bool CanPrevPage => Page > 0;
    public bool CanNextPage => Page < PageCount - 1;

    /// <summary>
    /// The numbered buttons, and which one is the page you are on.
    ///
    /// A WINDOW OF SEVEN, centred on the current page and clamped to the ends, because the numbers
    /// are laid out in a row inside a card: forty pages of an entry would push the chevrons off the
    /// edge of that card, which is the one thing this row must never do. Seven is what the
    /// reference shows plus room either side.
    /// </summary>
    public IReadOnlyList<PageChip> PageNumbers { get; private set; } = [];

    /// How many numbers the row shows at most.
    public const int PageWindow = 7;

    private IReadOnlyList<PageChip> BuildPageNumbers()
    {
        int total = PageCount, here = Page;                 // both 0-based inside, 1-based on screen
        int first = Math.Max(0, Math.Min(here - PageWindow / 2, total - PageWindow));
        int last = Math.Min(total - 1, first + PageWindow - 1);

        var chips = new List<PageChip>();
        for (int i = first; i <= last; i++) chips.Add(new PageChip(i + 1, i == here));
        return chips;
    }

    /// <summary>
    /// What the grid is showing, in words, because a filtered grid that says nothing looks like an
    /// invoice that has lost rows -- which is exactly the fright a hidden filter gives.
    /// </summary>
    public string ShowingLabel =>
        Lines.Count == FilteredLines.Count
            ? $"{Lines.Count} row{(Lines.Count == 1 ? "" : "s")}"
            : $"{FilteredLines.Count} of {Lines.Count} rows shown · filtered";

    /// <summary>
    /// Rebuilds the page in place.
    ///
    /// Cleared and refilled rather than replaced: the grid binds to this collection once, and
    /// handing it a new one would leave it showing the list as it stood at load -- the same trap
    /// Catalogue.ActiveSizes documents.
    /// </summary>
    public void Repage()
    {
        var page = FilteredLines.Skip(Page * PageSize).Take(PageSize).ToList();

        VisibleLines.Clear();
        foreach (var line in page) VisibleLines.Add(line);

        // Rebuilt here, not computed on demand: the chips carry which one is current, so they have
        // to be replaced whenever the page moves rather than re-read by a binding that has no
        // reason to notice.
        PageNumbers = BuildPageNumbers();

        Raise(nameof(FilteredLines));
        Raise(nameof(PageCount));
        Raise(nameof(Page));
        Raise(nameof(HasPages));
        Raise(nameof(PageLabel));
        Raise(nameof(PageNumbers));
        Raise(nameof(CanPrevPage));
        Raise(nameof(CanNextPage));
        Raise(nameof(ShowingLabel));
        Raise(nameof(AnyFilter));
    }

    /// <summary>Clears all four filters and returns to the first page.</summary>
    public void ClearFilters()
    {
        _filterDate = null; _filterBuyer = null; _filterBroker = null; _filterType = null;
        _page = 0;

        Raise(nameof(FilterDate)); Raise(nameof(FilterBuyer));
        Raise(nameof(FilterBroker)); Raise(nameof(FilterType));
        Repage();
    }

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
        // The LINE's broker percentage, not the header's: it is part of that line's own deal now,
        // and two rows on this screen may be sold at different rates. v_sales_line still multiplies
        // by the invoice's broker_pct on the server -- which agrees, because lines only share an
        // invoice when their whole deal matches, percentage included. See Deals.
        foreach (var line in Lines) line.Recalculate(line.DealBrokerPct);

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
    public string? Validate() => Problems().FirstOrDefault();

    /// <summary>
    /// How many lines a SALE must carry.
    ///
    /// ONE, on the desk's instruction. It was two for a while -- "one line is not enough to
    /// confirm" -- and that turned out to be wrong for the reason the note here always warned it
    /// might be: a single-line invoice is ordinary in most trading, and refusing one refused real
    /// sales.
    ///
    /// At 1 the rule can no longer fire: ConfirmProblems only raises it when the entry has MORE
    /// than nothing and FEWER than this, and no count is both. It is kept as a number rather than
    /// deleted because it is the one place the answer lives, and the last two changes to it were
    /// both a change of mind.
    /// </summary>
    public const int MinLinesToConfirm = 1;

    /// <summary>
    /// Everything Problems() refuses, PLUS the minimum line count.
    ///
    /// Separate from Problems() deliberately, and this is the whole reason it exists as its own
    /// method. Problems() gates four things: Confirm sale, Print memo, the Update modal, and
    /// correcting an already-posted invoice. Putting the minimum in there would stop a one-line
    /// MEMO being printed and make every single-line invoice already on the books uneditable --
    /// neither of which was asked for. Only confirming a NEW sale carries the rule.
    /// </summary>
    /// <summary>
    /// This invoice as the payload that is saved: the header, and EVERY real line.
    ///
    /// Extracted from SaveDraftAsync so the claim "all the rows are sent" can be tested rather than
    /// read. Confirming two rows and seeing one invoice in the list was reported as a lost line
    /// four times; the line was never lost, but nothing in the suite could demonstrate that, so
    /// each answer was another reading of the same code. Now a probe builds three rows and counts
    /// what comes out.
    ///
    /// RealLines, not Lines: a blank row is not a line. That is the same rule the chip, the totals
    /// and Problems() use, so the payload cannot disagree with what the screen says it holds.
    /// </summary>
    /// <summary>
    /// The invoices this entry would write: its real lines gathered by the deal they are sold
    /// under, in the order those deals first appear on screen.
    ///
    /// ONE GROUP IS THE ORDINARY CASE and the reason this is grouping rather than one-invoice-per-
    /// row. An entry where every line names the same buyer produces exactly one invoice with every
    /// line on it, which is what this screen has always done. Rows only separate when their deal
    /// actually differs -- and then they must, because sales_invoice holds one buyer, one broker,
    /// one percentage, one terms and one document type, and there is nowhere to put a second.
    ///
    /// GroupBy is stable in LINQ-to-Objects, so the lines inside a group stay in the order they
    /// were typed and the groups stay in the order they were started. Both matter: the order is
    /// what prints, and the desk reads the confirmation against the screen.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<SaleLine>> Deals => GroupDeals(RealLines);

    /// <summary>
    /// The same grouping, over whichever lines are being acted on.
    ///
    /// GroupBy is stable in LINQ-to-Objects, so lines keep the order they were typed and deals the
    /// order they were started — which is what makes a confirmation readable against the screen.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<SaleLine>> GroupDeals(IEnumerable<SaleLine> lines) =>
        lines.GroupBy(l => l.DealKey)
             .Select(g => (IReadOnlyList<SaleLine>)g.ToList())
             .ToList();

    /// <summary>
    /// The same lines gathered for the APPROVAL NOTE: one note per buyer AND broker, and nothing
    /// else in the key.
    ///
    /// DELIBERATELY NOT DealKey, which is what GroupDeals uses. That key is every column
    /// sales_invoice stores -- date, buyer, broker, broker %, terms, doc type -- because two lines
    /// differing in any of them cannot share an invoice. A note is not an invoice. It is the sheet
    /// the goods travel on, and the question it answers is "who is this parcel going to, through
    /// whom", so two lines for KIRAN EXPORTS through JITESH SHAH belong on one sheet even when one
    /// is 30-day and the other 60-day.
    ///
    /// Under DealKey they were two sheets, and the desk was handed two pieces of paper for one
    /// delivery to one buyer. This is the fix for that, and it changes ONLY the printed note --
    /// GroupDeals is untouched, so the invoices written on Confirm still split exactly as before.
    ///
    /// WHAT THAT COSTS, stated rather than discovered: a merged note prints ONE set of header
    /// terms, taken from its first line. Where the merged deals disagree on terms, broker % or doc
    /// type, the sheet shows the first line's. The rows and their money are every line's own and
    /// are not affected.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<SaleLine>> GroupForApproval(IEnumerable<SaleLine> lines) =>
        lines.GroupBy(l => (Buyer: l.DealBuyer?.Id, Broker: l.DealBroker?.Id))
             .Select(g => (IReadOnlyList<SaleLine>)g.ToList())
             .ToList();

    /// <summary>
    /// THE TICKED LINES: what Remove, Print memo and Confirm sale all act on.
    ///
    /// A tick used to be selection for Remove alone, and the other two acted on the whole entry.
    /// On the desk's instruction it now scopes all three — so four rows with two ticked confirm
    /// those two, and the other two stay on screen still holding their carats.
    ///
    /// RealLines, so a ticked blank row is not a line. That rule has never moved: the chip, the
    /// totals and the payload all agree on what counts as one.
    /// </summary>
    public IReadOnlyList<SaleLine> SelectedLines => RealLines.Where(l => l.Selected).ToList();

    /// <summary>The invoices the TICKED lines would write, one per deal.</summary>
    public IReadOnlyList<IReadOnlyList<SaleLine>> SelectedDeals => GroupDeals(SelectedLines);

    /// <summary>
    /// Every invoice this entry would write, one per deal. The lines' own buyer and terms, not the
    /// header's -- ToDraft below is what the Update modal still uses, where an invoice already IS
    /// one deal and the header is the only place it lives.
    ///
    /// Each carries its OWN client_ref rather than the entry's. They are separate documents and
    /// the id is what makes a retry idempotent per document; sharing one would make a replay of
    /// the second invoice collide with the first.
    /// </summary>
    public IReadOnlyList<DraftInvoice> ToDrafts(long currencyId) => ToDrafts(currencyId, RealLines);

    /// <summary>The same, over whichever lines are being confirmed or printed.</summary>
    public IReadOnlyList<DraftInvoice> ToDrafts(long currencyId, IEnumerable<SaleLine> lines_) =>
        GroupDeals(lines_).Select(lines =>
        {
            var head = lines[0];
            return new DraftInvoice(
                null, Guid.CreateVersion7(), DateOnly.FromDateTime(head.DealDate),
                head.DealBuyer!.Id, head.DealBroker?.Id, head.DealBrokerPct,
                Math.Max(head.DealTermsDays, 0), head.DealDocType, currencyId,
                lines.Select(l => new DraftLine(
                    l.Grade!.GradeId, l.Size!.SizeId, l.GrossWeightCt, l.SelectionCt,
                    l.PricePerCt, l.ExRate, l.Less1Pct, l.Less2Pct, l.Remark)).ToList());
        }).ToList();

    public DraftInvoice ToDraft(long buyerId, long currencyId) =>
        new(InvoiceId, ClientRef, DateOnly.FromDateTime(InvoiceDate), buyerId, BrokerId,
            BrokerPct, TermsDays, DocType, currencyId,
            RealLines.Select(l => new DraftLine(
                l.Grade!.GradeId, l.Size!.SizeId, l.GrossWeightCt, l.SelectionCt,
                l.PricePerCt, l.ExRate, l.Less1Pct, l.Less2Pct, l.Remark)).ToList());

    /// <summary>
    /// What stops the TICKED lines being acted on, or nothing.
    ///
    /// NOTHING TICKED IS A REFUSAL, not an empty action. Confirm sale and Print memo are only on
    /// screen once a row is ticked, but a keyboard or a stale click can still reach them, and a
    /// button that quietly does nothing is worse than one that says why.
    /// </summary>
    public IReadOnlyList<string> SelectionProblems(string action)
    {
        // AN EMPTY ENTRY IS NOT A TICKING PROBLEM. With no row to tick, "nothing is selected" tells
        // the desk to do something it cannot; "An invoice needs at least one line" is the fault, and
        // Problems() already says it. This is also what keeps the block chip -- which reads
        // ConfirmProblems() on every keystroke -- from nagging about ticks on a blank screen.
        if (RealLines.Count == 0) return Problems();

        var ticked = SelectedLines;
        return ticked.Count == 0
            ? [$"{NothingTicked} {action} — nothing is selected"]
            : Problems(ticked);
    }

    /// <summary>
    /// How a "nothing is ticked" refusal opens.
    ///
    /// Named so the screen can tell it apart from a FIELD fault. It belongs to no box and no row --
    /// nothing is wrong with the invoice, the desk simply has not ticked anything -- and the code
    /// that pins a refusal to the control at fault has to know not to try, or it pins this one to
    /// whichever cell happens to be unfinished and writes "Tick the lines to confirm" over a Size
    /// dropdown.
    /// </summary>
    public const string NothingTicked = "Tick the lines to";

    /// <summary>What stops the TICKED lines being confirmed. The button state and the chip read this.</summary>
    public IReadOnlyList<string> ConfirmProblems() => ConfirmProblems(null);

    /// <summary>
    /// The same, over an explicit set — the row a per-row Confirm icon sits on, which is its own
    /// selection and is not required to be ticked.
    /// </summary>
    public IReadOnlyList<string> ConfirmProblems(IReadOnlyList<SaleLine>? chosen)
    {
        var found = (chosen is null ? SelectionProblems("confirm") : Problems(chosen)).ToList();

        // ON THE ENTRY, not on the selection. The rule guards a half-typed SCREEN -- one line is
        // not an invoice worth confirming -- and applying it to the ticks instead would forbid
        // confirming a single row out of four, which is the whole point of ticking.
        //
        // Only once there IS a line: an empty invoice already says "needs at least one line", and
        // two refusals for one blank screen is noise.
        if (RealLines.Count is > 0 && RealLines.Count < MinLinesToConfirm)
            found.Add($"A sale needs at least {MinLinesToConfirm} lines — this one has {RealLines.Count}");

        return found;
    }

    /// <summary>
    /// EVERYTHING wrong with the invoice, header first, then the lines in the order they sit on
    /// screen. Validate() is the first of these, so every existing caller sees exactly what it saw
    /// before -- one rule set, two shapes, rather than a second list that drifts out of step.
    ///
    /// The list exists because Confirm sale is a sign-off. Being told "Buyer is required", fixing
    /// it, being told "Line 2: Selection is above weight", fixing that, and being told about line 4
    /// is three refusals for one click. The desk should see the whole of what is wrong at once.
    /// </summary>
    public IReadOnlyList<string> Problems() => Problems(RealLines);

    /// <summary>
    /// The same rules, over whichever lines are being acted on — the whole entry for a memo save
    /// or a correction, the TICKED ones for Confirm sale and Print memo.
    ///
    /// Numbered by the row's place in the GRID, not its place in the subset, so "Line 3" is the
    /// third row on screen whether or not rows one and two are ticked.
    /// </summary>
    public IReadOnlyList<string> Problems(IReadOnlyList<SaleLine> lines)
    {
        var found = new List<string>();

        // Buyer, terms and broker % are checked ON THE LINE now (see SaleLine.Validate) -- they are
        // the line's own since the deal moved onto the row, and a single header message could only
        // ever name one of however many rows were missing one. They still block the save exactly as
        // they did; the difference is that the refusal says WHICH line.

        if (lines.Count == 0)
            found.Add("An invoice needs at least one line");
        else
            // Not FirstOrDefault: a half-filled row three lines down is as much a reason to refuse
            // as the first one, and hiding it until the first is fixed makes the refusal look new.
            //
            // The figures first, then the deal, per line -- so a row that is wrong in both says so
            // once about each rather than hiding one behind the other. Three rows short of a buyer
            // are three refusals, and the desk fixes them in one pass.
            foreach (var l in lines)
            {
                if (l.Error is { } bad) found.Add($"Line {Lines.IndexOf(l) + 1}: {bad}");
                if (l.DealProblem is { } deal) found.Add($"Line {Lines.IndexOf(l) + 1}: {deal}");
            }

        return found;
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (SaleLine line in e.OldItems ?? Array.Empty<object>())
        {
            line.PropertyChanged -= OnLineChanged;
            LineDropped?.Invoke(line);
        }

        foreach (SaleLine line in e.NewItems ?? Array.Empty<object>())
        {
            line.PropertyChanged += OnLineChanged;

            // A NEW ROW NAMES NOBODY. It used to copy the deal off the row above it, on the theory
            // that most entries are several parcels to one buyer -- but that made a buyer appear in
            // Deal Details that the desk had never chosen for that line, on a screen whose whole
            // point is that the deal belongs to the row. Pressing Add line and finding the drawer
            // already filled in is the global Buyer field back in everything but name.
            //
            // THE HEADER'S DEAL IS STILL INHERITED, and only the header's. That is the path a
            // reopened draft, a restored entry and the Update modal all come in on: each sets the
            // buyer first and adds the lines after, and without this every line arrived sold to
            // nobody on an invoice that plainly has one. A fresh entry has no header deal -- nobody
            // can set one, there is no control for it -- so a row typed by hand starts empty.
            //
            // Keyed on the DEAL being unset, not on the row being blank: a line arrives carrying
            // figures often enough (the importer, the Update modal, every test), and gating on
            // IsBlank left all of those sold to nobody. A line that already names a buyer is never
            // touched.
            if (line.DealBuyer is null && _selectedBuyer is not null)
            {
                line.DealBuyer = _selectedBuyer;
                line.DealBroker = _selectedBroker;
                line.DealBrokerPct = _brokerPct;
                line.DealTermsDays = _termsDays;
                line.DealDocType = _docType;
            }

            line.SetInvoiceDate(_invoiceDate);
        }

        // A row added or removed changes what the page holds, and can change how many pages there
        // are. Rebuilt here so the grid never shows a line the invoice no longer has.
        Repage();

        Recalculate();
    }

    /// <summary>
    /// A line's bucket or weight changed, so what it holds against stock has to change with it
    /// (0043). Raised for grade, size and weight only -- a price or a remark moves no carats.
    /// </summary>
    public event Action<SaleLine>? LineHoldChanged;

    /// <summary>A line left the invoice, so whatever it was holding must go back.</summary>
    public event Action<SaleLine>? LineDropped;

    private void OnLineChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A line's DEAL decides whether a filter admits it, so editing the buyer on a row while a
        // buyer filter is set can move that row off the page it is sitting on. Rebuilt only when a
        // filter is actually set: with none, every line is on the page already and this would be a
        // rebuild per keystroke for no visible change.
        if (AnyFilter && e.PropertyName is nameof(SaleLine.DealBuyer) or nameof(SaleLine.DealBroker)
                                        or nameof(SaleLine.DealDocType) or nameof(SaleLine.DealDate))
            Repage();

        if (sender is SaleLine changed
            && e.PropertyName is nameof(SaleLine.Grade) or nameof(SaleLine.Size)
                              or nameof(SaleLine.GrossWeightCt))
            LineHoldChanged?.Invoke(changed);

        // Derived properties are set by Recalculate itself — reacting to them would recurse.
        // IsIncomplete and HasConflict belong on this list for the same reason: Error's setter
        // raises HasConflict, so leaving it off sent Recalculate straight back into itself.
        if (e.PropertyName is nameof(SaleLine.RejectionCt) or nameof(SaleLine.Amount)
            or nameof(SaleLine.Error) or nameof(SaleLine.AllowedSizes)
            or nameof(SaleLine.IsIncomplete) or nameof(SaleLine.HasConflict)
            // Ticking a row is not an edit. Recalculating on it would rebuild every total on the
            // screen for a checkbox that changes no figure.
            or nameof(SaleLine.Selected)
            // Selectable is derived from IsBlank and raised BY Recalculate, so reacting to it sent
            // the whole invoice straight back into Recalculate -- a stack overflow, not a slow
            // screen. Same reason RejectionCt and Amount are on this list.
            or nameof(SaleLine.Selectable)
            // Typing in a picker narrows a list. It moves no figure, so rebuilding every total on
            // the invoice per keystroke would be work with no reader -- and GradeChoices is raised
            // BY the filter's setter, which is the same recursion Selectable caused.
            or nameof(SaleLine.GradeFilter) or nameof(SaleLine.SizeFilter)
            or nameof(SaleLine.GradeChoices) or nameof(SaleLine.SizeChoices)
            or nameof(SaleLine.GradePlaceholder) or nameof(SaleLine.SizePlaceholder)
            // The deal's DERIVED faces. Every one of them is raised BY a deal setter that has
            // already asked for a recalculate, so reacting to them would run the whole invoice
            // again once per field -- the same recursion Selectable caused. The deal fields
            // themselves are NOT on this list: DealBrokerPct moves every amount on the row.
            or nameof(SaleLine.DealSummary) or nameof(SaleLine.DealTerms) or nameof(SaleLine.DealLine2)
            or nameof(SaleLine.DealDueDate) or nameof(SaleLine.DealKey)
            or nameof(SaleLine.HasBuyer)
            or nameof(SaleLine.DealBuyerName) or nameof(SaleLine.DealBrokerName)) return;
        Recalculate();
    }
}
