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

        // Replace, not merge: a previous import is cleared before anything new lands, so a
        // re-import of a corrected file cannot leave yesterday's rows behind it.
        int deleted = await Repo.DeleteImportedAsync(progress);

        Guid? user = Db.UserId;
        int invoices = 0, lines = 0, receipts = 0;

        foreach (var chunk in plan.Invoices.Chunk(100))
        {
            progress?.Report(new ImportProgress(
                $"Importing invoices… {invoices:N0} of {plan.Invoices.Count:N0}",
                invoices, plan.Invoices.Count));

            var toInsert = chunk.Select(inv => new ImportedInvoice
            {
                InvoiceNo = inv.InvoiceNo,
                InvoiceDate = inv.Date,
                BuyerId = buyerMap[inv.Buyer],
                BrokerId = inv.Broker is null ? null : brokerMap[inv.Broker],
                BrokerPct = inv.BrokerPct,
                TermsDays = inv.TermsDays,
                DocType = inv.DocType,
                CurrencyId = currencyId,
                Status = InvoiceStatus.POSTED,
                CreatedBy = user,
                UpdatedBy = user,
            }).ToList();

            var saved = await Repo.InsertImportedInvoicesAsync(toInsert);
            invoices += saved.Count;

            // Insert returns rows in the order they were sent, but pairing by invoice_no rather
            // than by position means a reordered response cannot silently attach lines to the
            // wrong document.
            var idByNo = saved.Where(s => s.InvoiceNo is not null)
                .ToDictionary(s => s.InvoiceNo!, s => s.InvoiceId, StringComparer.Ordinal);

            var lineRows = new List<SalesLine>();
            var receiptRows = new List<Receipt>();
            foreach (var inv in chunk)
            {
                if (!idByNo.TryGetValue(inv.InvoiceNo, out long id)) continue;

                lineRows.AddRange(inv.Lines.Select(l => new SalesLine
                {
                    InvoiceId = id,
                    GradeId = grades[l.GradeCode],
                    SizeId = sizes[l.SizeCode],
                    GrossWeightCt = l.GrossCt,
                    SelectionCt = l.SelectionCt,
                    PricePerCt = l.PricePerCt,
                    ExRate = l.ExRate,
                    Less1Pct = l.Less1Pct,
                    Less2Pct = l.Less2Pct,
                }));

                // The sheet records no payment date, so the invoice date stands in — an assumption,
                // and docs/08 §5 says to declare it rather than bury it.
                if (inv.Received > 0)
                    receiptRows.Add(new Receipt
                    {
                        InvoiceId = id,
                        ReceiptDate = inv.Date,
                        Amount = inv.Received,
                        Method = "IMPORTED",
                    });
            }

            foreach (var part in lineRows.Chunk(500))
            {
                await Repo.InsertLinesAsync(part.ToList());
                lines += part.Length;
            }
            foreach (var part in receiptRows.Chunk(500))
            {
                await Repo.InsertReceiptsAsync(part.ToList());
                receipts += part.Length;
            }
        }

        return new ImportResult(deleted, invoices, lines, receipts, buyersCreated, brokersCreated);
    }

    private static T Commonest<T>(IEnumerable<T> values) where T : notnull =>
        values.GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key;
}
