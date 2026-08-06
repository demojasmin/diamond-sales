namespace DiamondDesktop.Data;

/// <summary>
/// A progress report. Total is 0 while the step has no countable work, which the dialog shows as an
/// indeterminate bar rather than a bar stuck at zero.
/// </summary>
public sealed record ImportProgress(string Message, int Done = 0, int Total = 0);

public sealed record ImportResult(
    int DeletedInvoices, int Invoices, int Lines, int Receipts,
    int BuyersCreated, int BrokersCreated);

/// <summary>
/// Executes a validated <see cref="ImportPlan"/> against Supabase, per docs/08 §4-5.
///
/// Imported invoices are written straight to POSTED with a MIG- number and deliberately produce no
/// stock movements: the migrated opening balance is already net of these sales, so posting them
/// through post_invoice() would deduct the same carats twice. That is also why the import never
/// needs DELETE on stock_movement, which the ledger does not grant to anyone.
///
/// The plan is validated in full before this runs, so the only failures left are network ones.
/// </summary>
public static class SaleImporter
{
    public static async Task<ImportResult> RunAsync(ImportPlan plan, IProgress<ImportProgress>? progress = null)
    {
        progress?.Report(new ImportProgress("Reading the catalogue…"));
        var grades = (await Repo.GradesAsync())
            .ToDictionary(g => g.Code.Trim(), g => g.GradeId, StringComparer.OrdinalIgnoreCase);
        var sizes = (await Repo.SizesAsync())
            .ToDictionary(s => s.Code.Trim(), s => s.SizeId, StringComparer.OrdinalIgnoreCase);
        var currencies = await Repo.CurrenciesAsync();
        long currencyId = currencies
            .FirstOrDefault(c => c.Code.Equals("INR", StringComparison.OrdinalIgnoreCase))
            ?.CurrencyId ?? currencies[0].CurrencyId;

        // Defaults seeded from the file itself: the commonest Terms per buyer, the commonest
        // Broker % per broker (docs/08 §2.4). Both stay editable afterwards.
        var allLines = plan.Invoices.SelectMany(i => i.Lines).ToList();
        var buyerDefaults = allLines.GroupBy(l => l.Buyer, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, TermsDays: Commonest(g.Select(l => l.TermsDays)))).ToList();
        var brokerDefaults = allLines.Where(l => l.Broker.Length > 0)
            .GroupBy(l => l.Broker, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Pct: Commonest(g.Select(l => l.BrokerPct)))).ToList();

        progress?.Report(new ImportProgress("Checking buyers and brokers…"));
        var (buyerMap, buyersCreated) = await Repo.EnsureBuyersAsync(buyerDefaults);
        var (brokerMap, brokersCreated) = await Repo.EnsureBrokersAsync(brokerDefaults);

        // Replace, not merge — but as ONE transaction now, through replace_imported_sales (0018).
        //
        // It used to be a delete loop followed by an insert loop over separate HTTP calls. Between
        // them the old dataset was gone and the new one had not arrived, and anything failing in
        // that gap — a dropped connection, a rejected row — left the database holding neither. The
        // delete now lives inside the function, so a refusal of any kind rolls the whole thing back
        // and yesterday's import is still there.
        progress?.Report(new ImportProgress(
            $"Replacing the sale history… {plan.Invoices.Count:N0} invoice(s)", 0, plan.Invoices.Count));

        var payload = new Dictionary<string, object?>
        {
            ["currency_id"] = currencyId,
            ["invoices"] = plan.Invoices.Select(inv => new Dictionary<string, object?>
            {
                ["invoice_no"] = inv.InvoiceNo,
                ["invoice_date"] = inv.Date.ToString("yyyy-MM-dd"),
                ["buyer_id"] = buyerMap[inv.Buyer],
                ["broker_id"] = inv.Broker is null ? null : brokerMap[inv.Broker],
                ["broker_pct"] = inv.BrokerPct,
                ["terms_days"] = inv.TermsDays,
                ["doc_type"] = inv.DocType,

                // Capped at the invoice total, exactly as the row-by-row importer did. The
                // workbook's Rec. Amt is rounded to whole rupees while the amount is recomputed by
                // Postgres from the lines, so a paid invoice can arrive with 1,39,865.00 received
                // against 1,39,864.73 owed. Left as-is that is an outstanding of -0.27 for ever,
                // and the Dashboard and Invoices page then disagree because one floors at zero and
                // the other does not. The residue is rounding, not money.
                ["received"] = inv.Received <= 0 ? 0m
                             : inv.Total > 0 ? Math.Min(inv.Received, inv.Total)
                             : inv.Received,

                ["lines"] = inv.Lines.Select(l => new Dictionary<string, object?>
                {
                    ["grade_id"] = grades[l.GradeCode],
                    ["size_id"] = sizes[l.SizeCode],
                    ["gross_weight_ct"] = l.GrossCt,
                    ["selection_ct"] = l.SelectionCt,
                    ["price_per_ct"] = l.PricePerCt,
                    ["ex_rate"] = l.ExRate,
                    ["less1_pct"] = l.Less1Pct,
                    ["less2_pct"] = l.Less2Pct,
                }).ToList(),
            }).ToList(),
        };

        var outcome = await Repo.ReplaceImportedSalesAsync(payload);

        progress?.Report(new ImportProgress("Sale history replaced",
                                            plan.Invoices.Count, plan.Invoices.Count));

        // A short count means rows were dropped. Because this was one transaction there is nothing
        // half-written to clean up — but it still has to be said rather than reported as success.
        if (outcome.Invoices != plan.Invoices.Count)
            throw new InvalidOperationException(
                $"Sent {plan.Invoices.Count} invoice(s) but the database wrote {outcome.Invoices}.");

        return new ImportResult(outcome.Deleted, outcome.Invoices, outcome.Lines, outcome.Receipts,
                                buyersCreated, brokersCreated);
    }

    private static T Commonest<T>(IEnumerable<T> values) where T : notnull =>
        values.GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key;
}
