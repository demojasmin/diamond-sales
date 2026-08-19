namespace DiamondCalc;

/// <summary>
/// The calculation engine — docs/03-domain-model.md §3.
/// Pure functions over <see cref="decimal"/>. No I/O, no database, no dependencies.
/// This is the single implementation for backend, desktop and Android (FR-CALC-13).
/// </summary>
public static class Calc
{
    public const int MoneyDp = 2;   // BR-ROUND-6
    public const int CaratDp = 4;   // BR-ROUND-2

    /// Round-half-up (BR-ROUND-4). Banker's rounding is NOT the policy here.
    public static decimal RoundMoney(decimal v) => Math.Round(v, MoneyDp, MidpointRounding.AwayFromZero);

    public static decimal RoundCarat(decimal v) => Math.Round(v, CaratDp, MidpointRounding.AwayFromZero);

    /// <summary>CALC-1 · line amount. Discounts compound, in this order. The only rounding boundary.</summary>
    public static decimal LineAmount(
        decimal selectionCt, decimal pricePerCt, decimal exRate,
        decimal less1Pct, decimal less2Pct, decimal brokerPct)
    {
        if (selectionCt < 0) throw new ArgumentOutOfRangeException(nameof(selectionCt));
        if (pricePerCt < 0) throw new ArgumentOutOfRangeException(nameof(pricePerCt));
        if (exRate <= 0) throw new ArgumentOutOfRangeException(nameof(exRate));
        Pct(less1Pct, nameof(less1Pct));
        Pct(less2Pct, nameof(less2Pct));
        Pct(brokerPct, nameof(brokerPct));

        return RoundMoney(PreBroker(selectionCt, pricePerCt, exRate, less1Pct, less2Pct)
                          * (1 - brokerPct / 100m));
    }

    /// <summary>CALC-2 · rejection carats. selection &gt; gross is an error, never a clamp (§3.4).</summary>
    public static decimal Rejection(decimal grossWeightCt, decimal selectionCt)
    {
        if (grossWeightCt <= 0) throw new ArgumentOutOfRangeException(nameof(grossWeightCt));
        if (selectionCt < 0) throw new ArgumentOutOfRangeException(nameof(selectionCt));
        if (selectionCt > grossWeightCt)
            throw new ArgumentException($"selection {selectionCt} exceeds gross {grossWeightCt}", nameof(selectionCt));

        return RoundCarat(grossWeightCt - selectionCt);
    }

    /// <summary>CALC-3 · invoice outstanding. Derived, never stored.</summary>
    public static decimal Outstanding(IEnumerable<decimal> lineAmounts, IEnumerable<decimal> receiptAmounts)
        => InvoiceTotal(lineAmounts) - receiptAmounts.Sum();

    /// <summary>CALC-4 · invoice total = Σ of already-rounded line amounts (§3.2).</summary>
    public static decimal InvoiceTotal(IEnumerable<decimal> lineAmounts) => lineAmounts.Sum();

    /// <summary>CALC-5 · blended rate per carat. Unrounded — round for display only.</summary>
    public static decimal BlendedRate(decimal totalAmount, decimal totalCarats)
        => totalCarats == 0 ? 0 : totalAmount / totalCarats;

    /// <summary>CALC-6 · weighted-average price. Σw = 0 returns 0 — this retires DQ-2's placeholder rows.</summary>
    public static decimal WeightedAvgPrice(IEnumerable<(decimal WeightCt, decimal PricePerCt)> items)
    {
        decimal w = 0, wp = 0;
        foreach (var (weight, price) in items) { w += weight; wp += weight * price; }
        return w == 0 ? 0 : wp / w;
    }

    /// <summary>CALC-7 · grade × size balance = Σ signed movement weights. The sign is in the row (§2.4).</summary>
    public static decimal Balance(IEnumerable<decimal> signedWeightsCt) => signedWeightsCt.Sum();

    /// <summary>CALC-9 · roll-up. Carats add; price goes through CALC-6, never a mean of means.</summary>
    public static (decimal WeightCt, decimal AvgPricePerCt) RollUp(
        IEnumerable<(decimal WeightCt, decimal PricePerCt)> items)
    {
        var list = items as ICollection<(decimal, decimal)> ?? items.ToList();
        return (list.Sum(i => i.Item1), WeightedAvgPrice(list));
    }

    /// <summary>CALC-10 · due date. Terms of 0 is valid and means "due on the invoice date" (verification A-3).</summary>
    public static DateOnly DueDate(DateOnly invoiceDate, int termsDays)
        => termsDays < 0
            ? throw new ArgumentOutOfRangeException(nameof(termsDays))
            : invoiceDate.AddDays(termsDays);

    /// <summary>
    /// The residue below which an invoice is considered settled — fifty paise.
    ///
    /// Money rounds to 2dp, so anything under half a rupee is rounding dust rather than a debt.
    /// This is the shared authority for that judgement across all three clients: Android's
    /// <c>Calc.SETTLED_BELOW</c> and the server's <c>v_invoice.is_overdue</c> /
    /// <c>v_receivables_ageing</c> use the same figure. Three different values were in play —
    /// this engine used <c>&gt; 0</c>, the views <c>&gt; 0.01</c>, Android <c>0.50</c> — so an
    /// invoice owing forty paise was overdue here, in the receivables book on the server, and
    /// settled on the phone. One invoice, three answers.
    /// </summary>
    public const decimal SettledBelow = 0.50m;

    public static bool IsOverdue(DateOnly dueDate, decimal outstanding, DateOnly today)
        => today > dueDate && outstanding > SettledBelow;

    /// <summary>
    /// CALC-AGE · the receivables ageing band for a number of days past due.
    ///
    /// Lives here rather than in the window so it can be tested against the same nine boundary
    /// inputs as Android's <c>Calc.ageingBucket</c> and the server's <c>v_receivables_ageing</c>.
    /// The rule is written out three times by design (CALC-001); the shared table of inputs is
    /// the only thing that keeps the three from drifting apart unnoticed.
    ///
    /// <b>Zero is "not due", not "just barely late".</b> <c>days_overdue</c> floors at zero on the
    /// server, so an invoice due next month and one thirty days late both arrived as 0 and were
    /// filed together under "0-30" — putting money that is not late at all in the same band as
    /// money that is.
    /// </summary>
    public static string AgeBucket(int daysPastDue) => daysPastDue switch
    {
        <= 0  => "not due",
        <= 30 => "1-30",
        <= 60 => "31-60",
        <= 90 => "61-90",
        _     => "90+",
    };

    /// <summary>CALC-11 · broker payable = Σ pre-broker line subtotals × broker %. Deleted entirely if Q4 comes back "deduction only".</summary>
    public static decimal BrokerPayable(
        IEnumerable<(decimal SelectionCt, decimal PricePerCt, decimal ExRate, decimal Less1Pct, decimal Less2Pct)> lines,
        decimal brokerPct)
    {
        Pct(brokerPct, nameof(brokerPct));
        decimal subtotal = lines.Sum(l => PreBroker(l.SelectionCt, l.PricePerCt, l.ExRate, l.Less1Pct, l.Less2Pct));
        return RoundMoney(subtotal * brokerPct / 100m);
    }

    /// <summary>
    /// PAY-003 / V-4 · settlement write-off. A hand-rounded payment leaves a residue that decimal
    /// precision can never remove (verification DQ-12: ₹139,865 paid against ₹139,864.725).
    /// </summary>
    public static bool IsSettled(decimal outstanding, decimal writeOffThreshold)
        => writeOffThreshold < 0
            ? throw new ArgumentOutOfRangeException(nameof(writeOffThreshold))
            : Math.Abs(outstanding) < writeOffThreshold;

    /// The amount before the broker deduction — shared by CALC-1 and CALC-11 so they cannot drift apart.
    private static decimal PreBroker(decimal selectionCt, decimal pricePerCt, decimal exRate, decimal less1Pct, decimal less2Pct)
        => selectionCt * pricePerCt * exRate * (1 - less1Pct / 100m) * (1 - less2Pct / 100m);

    private static void Pct(decimal value, string name)
    {
        if (value is < 0 or > 100) throw new ArgumentOutOfRangeException(name, value, "percent must be 0–100");
    }
}
