using DiamondCalc;

namespace DiamondApi;

/// Phase 4 — the owner's fifteen widgets. Global filters apply to every one of them.
/// Posted invoices only: a draft is not a sale (docs/07 §4.7).
public sealed record DashFilter(
    DateOnly? From = null, DateOnly? To = null,
    Guid? BuyerId = null, Guid? BrokerId = null,
    Guid? GradeId = null, Guid? SizeId = null,
    Guid? SalespersonId = null);

/// One row of any breakdown. Every widget below returns these, so one UI list renders all of them.
public sealed record Bar(string Label, decimal Value, string? Secondary = null, decimal? Delta = null, Guid? DrillId = null);

// Named, not anonymous: these cross an assembly boundary (clients, tests) and a contract that only
// exists inside one assembly is not a contract.
public sealed record DashSummary(decimal TotalSales, decimal CaratsSold, decimal BlendedRate, decimal Outstanding,
                                 decimal InventoryValue, decimal InventoryCarats, decimal BrokerCost,
                                 int InvoiceCount, decimal PriorSales);

public sealed record DashMargin(decimal Total, List<Bar> Rows, string CostBasis);

public sealed record LowStock(string GradeCode, string SizeCode, decimal BalanceCt);
public sealed record OverdueInvoice(string? InvoiceNo, DateOnly Due, decimal Outstanding);
public sealed record DashAlerts(int OverdueCount, decimal OverdueValue, int LowStockCount, int NegativeCount,
                                List<LowStock> LowStockRows, List<OverdueInvoice> Overdue);

public static class Dashboard
{
    // ── the working set ─────────────────────────────────────────────────────

    public static List<SalesInvoice> PostedInvoices(DiamondDb db, DashFilter f)
        => db.Invoices.Where(i => i.Status == InvoiceStatus.Posted).AsEnumerable()
             .Where(i => (f.From is null || i.InvoiceDate >= f.From)
                      && (f.To is null || i.InvoiceDate <= f.To)
                      && (f.BuyerId is null || i.BuyerId == f.BuyerId)
                      && (f.BrokerId is null || i.BrokerId == f.BrokerId)
                      && (f.SalespersonId is null || i.CreatedBy == f.SalespersonId))
             .ToList();

    public static List<SalesLine> Lines(DiamondDb db, DashFilter f)
    {
        var ids = PostedInvoices(db, f).Select(i => i.InvoiceId).ToHashSet();
        return db.Lines.AsEnumerable()
                 .Where(l => ids.Contains(l.InvoiceId)
                          && (f.GradeId is null || l.GradeId == f.GradeId)
                          && (f.SizeId is null || l.SizeId == f.SizeId))
                 .ToList();
    }

    // ── W1, W2, W3, W9, W11, W14 · the headline numbers ────────────────────

    public static DashSummary Summary(DiamondDb db, DashFilter f)
    {
        var invoices = PostedInvoices(db, f);
        var lines = Lines(db, f);
        decimal amount = Calc.InvoiceTotal(lines.Select(l => l.Amount));
        decimal carats = lines.Sum(l => l.SelectionCt);
        var stock = Stock.Position(db);

        if (f.GradeId is not null) stock = stock.Where(r => r.GradeId == f.GradeId).ToList();
        if (f.SizeId is not null) stock = stock.Where(r => r.SizeId == f.SizeId).ToList();

        return new DashSummary(
            TotalSales: amount,                                        // W1
            CaratsSold: carats,                                        // W2
            BlendedRate: Calc.BlendedRate(amount, carats),             // W3 · CALC-5
            Outstanding: invoices.Sum(i => Invoices.Outstanding(db, i.InvoiceId)),   // W9
            InventoryValue: stock.Sum(r => r.Value),                   // W11
            InventoryCarats: stock.Sum(r => r.BalanceCt),
            BrokerCost: BrokerCostTotal(db, f),                        // W14 · CALC-11
            InvoiceCount: invoices.Count,
            PriorSales: PriorPeriodSales(db, f));                      // W1 vs-prior
    }

    /// W1's "vs prior" — the same span of days, immediately before the filtered window.
    private static decimal PriorPeriodSales(DiamondDb db, DashFilter f)
    {
        if (f.From is null || f.To is null) return 0;

        int days = f.To.Value.DayNumber - f.From.Value.DayNumber + 1;
        var prior = f with { From = f.From.Value.AddDays(-days), To = f.From.Value.AddDays(-1) };
        return Calc.InvoiceTotal(Lines(db, prior).Select(l => l.Amount));
    }

    // ── W4 · sales by period ────────────────────────────────────────────────

    public static List<Bar> SalesByPeriod(DiamondDb db, DashFilter f, string bucket)
    {
        var invoiceDates = PostedInvoices(db, f).ToDictionary(i => i.InvoiceId, i => i.InvoiceDate);

        return Lines(db, f)
            .GroupBy(l => Bucket(invoiceDates[l.InvoiceId], bucket))
            .OrderBy(g => g.Key)
            .Select(g => new Bar(g.Key, Calc.InvoiceTotal(g.Select(l => l.Amount)),
                                 $"{g.Sum(l => l.SelectionCt):N2} ct"))
            .ToList();
    }

    private static string Bucket(DateOnly date, string bucket) => bucket switch
    {
        "month" => $"{date:yyyy-MM}",
        "week" => $"{date:yyyy}-W{System.Globalization.ISOWeek.GetWeekOfYear(date.ToDateTime(TimeOnly.MinValue)):D2}",
        _ => $"{date:yyyy-MM-dd}",
    };

    // ── W5 · by salesperson · W6 · by buyer · W8 · avg rate by grade ────────

    public static List<Bar> SalesBySalesperson(DiamondDb db, DashFilter f)
    {
        var users = db.Users.ToDictionary(u => u.UserId, u => u.DisplayName);
        var invoices = PostedInvoices(db, f);
        var lines = Lines(db, f).ToLookup(l => l.InvoiceId);

        return invoices.GroupBy(i => i.CreatedBy)
            .Select(g =>
            {
                var own = g.SelectMany(i => lines[i.InvoiceId]).ToList();
                decimal amount = Calc.InvoiceTotal(own.Select(l => l.Amount));
                decimal carats = own.Sum(l => l.SelectionCt);
                return new Bar(users.GetValueOrDefault(g.Key, "(unknown)"), amount,
                               $"{carats:N2} ct @ {Calc.BlendedRate(amount, carats):N0}/ct", null, g.Key);
            })
            .OrderByDescending(b => b.Value).ToList();
    }

    public static List<Bar> SalesByBuyer(DiamondDb db, DashFilter f)
    {
        var buyers = db.Buyers.ToDictionary(b => b.BuyerId, b => b.Name);
        var invoices = PostedInvoices(db, f);
        var lines = Lines(db, f).ToLookup(l => l.InvoiceId);
        decimal total = Calc.InvoiceTotal(Lines(db, f).Select(l => l.Amount));

        return invoices.GroupBy(i => i.BuyerId)
            .Select(g =>
            {
                decimal amount = Calc.InvoiceTotal(g.SelectMany(i => lines[i.InvoiceId]).Select(l => l.Amount));
                decimal share = total == 0 ? 0 : amount / total * 100m;
                return new Bar(buyers.GetValueOrDefault(g.Key, "(unknown)"), amount, $"{share:N1}% of revenue", null, g.Key);
            })
            .OrderByDescending(b => b.Value).ToList();
    }

    public static List<Bar> AvgRateByGrade(DiamondDb db, DashFilter f)
    {
        var grades = db.Grades.ToDictionary(g => g.GradeId);

        return Lines(db, f).GroupBy(l => l.GradeId)
            .Select(g =>
            {
                decimal amount = Calc.InvoiceTotal(g.Select(l => l.Amount));
                decimal carats = g.Sum(l => l.SelectionCt);
                return new Bar(grades[g.Key].DisplayName, Calc.BlendedRate(amount, carats),
                               $"{carats:N2} ct", null, g.Key);        // W8 · weighted, never a mean of means
            })
            .OrderByDescending(b => b.Value).ToList();
    }

    // ── W7 · margin ─────────────────────────────────────────────────────────

    /// Cost basis = weighted-average stock cost (Q3's stated assumption — confirm before this ships).
    public static DashMargin Margin(DiamondDb db, DashFilter f)
    {
        var grades = db.Grades.ToDictionary(g => g.GradeId);
        var costByBucket = Stock.Position(db).ToDictionary(r => (r.GradeId, r.SizeId), r => r.AvgPricePerCt);

        var rows = Lines(db, f).GroupBy(l => l.GradeId).Select(g =>
        {
            decimal revenue = Calc.InvoiceTotal(g.Select(l => l.Amount));
            decimal cost = g.Sum(l => l.SelectionCt * costByBucket.GetValueOrDefault((l.GradeId, l.SizeId), 0m));
            return new Bar(grades[g.Key].DisplayName, Calc.RoundMoney(revenue - cost),
                           revenue == 0 ? "—" : $"{(revenue - cost) / revenue * 100m:N1}% margin", null, g.Key);
        }).OrderByDescending(b => b.Value).ToList();

        return new DashMargin(rows.Sum(r => r.Value), rows, "WEIGHTED_AVG_STOCK_COST (Q3)");
    }

    // ── W10 · receivables ageing ────────────────────────────────────────────

    public static List<Bar> Ageing(DiamondDb db, DashFilter f)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var buckets = new[] { "current", "0-30", "31-60", "61-90", "90+" }
            .ToDictionary(b => b, _ => 0m);

        foreach (var invoice in PostedInvoices(db, f))
        {
            decimal outstanding = Invoices.Outstanding(db, invoice.InvoiceId);
            if (outstanding == 0) continue;

            int overdue = today.DayNumber - Calc.DueDate(invoice.InvoiceDate, invoice.TermsDays).DayNumber;
            string bucket = overdue <= 0 ? "current"
                          : overdue <= 30 ? "0-30"
                          : overdue <= 60 ? "31-60"
                          : overdue <= 90 ? "61-90" : "90+";
            buckets[bucket] += outstanding;
        }

        return buckets.Select(b => new Bar(b.Key, b.Value)).ToList();
    }

    // ── W11 · inventory value by grade · W12 · inventory aging ──────────────

    public static List<Bar> InventoryByGrade(DiamondDb db, DashFilter f)
        => Stock.Position(db)
            .Where(r => (f.GradeId is null || r.GradeId == f.GradeId) && (f.SizeId is null || r.SizeId == f.SizeId))
            .GroupBy(r => r.GradeCode)
            .Select(g => new Bar(g.Key, g.Sum(r => r.Value), $"{g.Sum(r => r.BalanceCt):N2} ct"))
            .Where(b => b.Value != 0)
            .OrderByDescending(b => b.Value).ToList();

    /// W12. Carats still on hand, by how long ago they came in — FIFO: outflows consume the oldest first.
    /// Q5's assumption: age counts from original intake, not from entry into the current grade.
    public static List<Bar> InventoryAging(DiamondDb db, DashFilter f)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var bands = new[] { "0-30 days", "31-60", "61-90", "91-180", "180+" }.ToDictionary(b => b, _ => 0m);

        var movements = db.Movements.AsEnumerable()
            .Where(m => (f.GradeId is null || m.GradeId == f.GradeId) && (f.SizeId is null || m.SizeId == f.SizeId))
            .GroupBy(m => (m.GradeId, m.SizeId));

        foreach (var bucket in movements)
        {
            // Oldest inflows first; each outflow eats into them until it is spent.
            var inflows = bucket.Where(m => m.WeightCt > 0).OrderBy(m => m.MovementDate)
                                .Select(m => (m.MovementDate, Remaining: m.WeightCt)).ToList();
            decimal outflow = -bucket.Where(m => m.WeightCt < 0).Sum(m => m.WeightCt);

            for (int i = 0; i < inflows.Count && outflow > 0; i++)
            {
                decimal eaten = Math.Min(inflows[i].Remaining, outflow);
                inflows[i] = (inflows[i].MovementDate, inflows[i].Remaining - eaten);
                outflow -= eaten;
            }

            foreach (var (date, remaining) in inflows.Where(x => x.Remaining > 0))
            {
                int age = today.DayNumber - date.DayNumber;
                string band = age <= 30 ? "0-30 days"
                            : age <= 60 ? "31-60"
                            : age <= 90 ? "61-90"
                            : age <= 180 ? "91-180" : "180+";
                bands[band] += remaining;
            }
        }

        return bands.Select(b => new Bar(b.Key, b.Value, "carats on hand")).ToList();
    }

    // ── W13 · top movers · W14 · broker cost ────────────────────────────────

    public static List<Bar> TopMovers(DiamondDb db, DashFilter f, int take = 10)
    {
        var grades = db.Grades.ToDictionary(g => g.GradeId);
        var prior = PriorWindow(f);
        var priorCarats = prior is null
            ? []
            : Lines(db, prior).GroupBy(l => l.GradeId).ToDictionary(g => g.Key, g => g.Sum(l => l.SelectionCt));

        return Lines(db, f).GroupBy(l => l.GradeId)
            .Select(g =>
            {
                decimal carats = g.Sum(l => l.SelectionCt);
                decimal before = priorCarats.GetValueOrDefault(g.Key, 0m);
                return new Bar(grades[g.Key].DisplayName, carats,
                               $"{Calc.InvoiceTotal(g.Select(l => l.Amount)):N0}", carats - before, g.Key);
            })
            .OrderByDescending(b => b.Value).Take(take).ToList();
    }

    public static List<Bar> BrokerCost(DiamondDb db, DashFilter f)
    {
        var brokers = db.Brokers.ToDictionary(b => b.BrokerId, b => b.Name);
        var lines = Lines(db, f).ToLookup(l => l.InvoiceId);

        // Grouped by broker INCLUDING the unnamed ones: if broker % was charged, the money left the
        // deal whether or not anybody typed a name. Hiding those rows would under-report W14.
        return PostedInvoices(db, f)
            .GroupBy(i => i.BrokerId)
            .Select(g => new Bar(
                g.Key is null ? "(no broker named)" : brokers.GetValueOrDefault(g.Key.Value, "(unknown)"),
                g.Sum(i => Calc.BrokerPayable(
                    lines[i.InvoiceId].Select(l => (l.SelectionCt, l.PricePerCt, l.ExRate, l.Less1Pct, l.Less2Pct)),
                    i.BrokerPct)),
                null, null, g.Key))
            .Where(b => b.Value != 0)
            .OrderByDescending(b => b.Value).ToList();
    }

    private static decimal BrokerCostTotal(DiamondDb db, DashFilter f) => BrokerCost(db, f).Sum(b => b.Value);

    // ── W15 · alerts strip ──────────────────────────────────────────────────

    public static DashAlerts Alerts(DiamondDb db)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        decimal lowStock = Settings.Dec(db, "low_stock_threshold_ct", 5m);

        var overdue = db.Invoices.Where(i => i.Status == InvoiceStatus.Posted).AsEnumerable()
            .Select(i => new { i.InvoiceId, i.InvoiceNo, Due = Calc.DueDate(i.InvoiceDate, i.TermsDays),
                               Outstanding = Invoices.Outstanding(db, i.InvoiceId) })
            .Where(x => Calc.IsOverdue(x.Due, x.Outstanding, today))
            .ToList();

        var low = Stock.Position(db).Where(r => r.BalanceCt < lowStock).ToList();

        return new DashAlerts(
            OverdueCount: overdue.Count,
            OverdueValue: overdue.Sum(x => x.Outstanding),
            LowStockCount: low.Count,
            NegativeCount: low.Count(r => r.BalanceCt < 0),           // docs/04 B-2: these exist in the workbook
            LowStockRows: low.Select(r => new LowStock(r.GradeCode, r.SizeCode, r.BalanceCt)).ToList(),
            Overdue: overdue.Select(x => new OverdueInvoice(x.InvoiceNo, x.Due, x.Outstanding)).ToList());
    }

    // ── date-range presets ──────────────────────────────────────────────────

    /// Q16's assumption: the financial year runs April–March.
    public static (DateOnly From, DateOnly To) Preset(string name, DateOnly today) => name.ToUpperInvariant() switch
    {
        "TODAY" => (today, today),
        "WEEK" => (today.AddDays(-(int)today.DayOfWeek), today),
        "MONTH" => (new DateOnly(today.Year, today.Month, 1), today),
        "QUARTER" => (new DateOnly(today.Year, today.Month - (today.Month - 1) % 3, 1), today),
        "FY" => today.Month >= 4
                    ? (new DateOnly(today.Year, 4, 1), today)
                    : (new DateOnly(today.Year - 1, 4, 1), today),
        _ => (DateOnly.MinValue, DateOnly.MaxValue),                  // ALL
    };

    private static DashFilter? PriorWindow(DashFilter f)
    {
        if (f.From is null || f.To is null) return null;
        int days = f.To.Value.DayNumber - f.From.Value.DayNumber + 1;
        return f with { From = f.From.Value.AddDays(-days), To = f.From.Value.AddDays(-1) };
    }
}
