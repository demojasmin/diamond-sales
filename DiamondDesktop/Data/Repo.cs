using System.Text.Json;
using Supabase.Postgrest.Exceptions;
using static Supabase.Postgrest.Constants;

namespace DiamondDesktop.Data;

public sealed record DraftInvoice(long? InvoiceId, Guid ClientRef, DateOnly InvoiceDate, long BuyerId,
    long? BrokerId, decimal BrokerPct, int TermsDays, string DocType, long CurrencyId, List<DraftLine> Lines);

public sealed record DraftLine(long GradeId, long SizeId, decimal GrossWeightCt, decimal SelectionCt,
    decimal PricePerCt, decimal ExRate, decimal Less1Pct, decimal Less2Pct, string? Remark);

public sealed record Shortfall(string GradeCode, string SizeCode, decimal BalanceCt, decimal NeededCt);

public sealed record PostOutcome(bool Ok, bool NeedsOverride, string? InvoiceNo, string? Message,
    List<Shortfall> Shortfalls);

/// <summary>Every read comes from a view or a plain table; every computed number is Postgres'.</summary>
public static class Repo
{
    /// PostgREST answers with at most 1000 rows and gives no sign that more exist. Every read that
    /// can outgrow that has to page, or it silently shows part of the truth.
    private const int PageSize = 1000;

    /// <summary>
    /// Reads a query to the end, a page at a time. The query must carry an ordering that breaks
    /// ties, otherwise rows can repeat or vanish between requests.
    /// </summary>
    private static async Task<List<T>> AllPagesAsync<T>(
        Func<Supabase.Postgrest.Interfaces.IPostgrestTable<T>> query)
        where T : Supabase.Postgrest.Models.BaseModel, new()
    {
        var all = new List<T>();
        for (int offset = 0; ; offset += PageSize)
        {
            var page = (await query().Range(offset, offset + PageSize - 1).Get()).Models;
            all.AddRange(page);
            if (page.Count < PageSize) return all;
        }
    }

    // ---------- reads ----------

    public static async Task<List<Grade>> GradesAsync() =>
        (await Db.Client.From<Grade>().Filter("active", Operator.Equals, "true")
            .Order("sort_order", Ordering.Ascending).Get()).Models;

    public static async Task<List<SizeBucket>> SizesAsync() =>
        (await Db.Client.From<SizeBucket>().Order("sort_order", Ordering.Ascending).Get()).Models;

    public static async Task<List<Buyer>> BuyersAsync() =>
        (await Db.Client.From<Buyer>().Filter("active", Operator.Equals, "true")
            .Order("name", Ordering.Ascending).Get()).Models;

    public static async Task<List<Broker>> BrokersAsync() =>
        (await Db.Client.From<Broker>().Filter("active", Operator.Equals, "true")
            .Order("name", Ordering.Ascending).Get()).Models;

    public static async Task<List<Currency>> CurrenciesAsync() =>
        (await Db.Client.From<Currency>().Order("code", Ordering.Ascending).Get()).Models;

    public static async Task<Dictionary<string, string>> ConfigAsync() =>
        (await Db.Client.From<AppConfig>().Get()).Models.ToDictionary(c => c.Key, c => c.Value ?? "");

    public static async Task<List<PriceList>> PricesAsync() =>
        (await Db.Client.From<PriceList>()
            .Order("grade_id", Ordering.Ascending).Order("size_id", Ordering.Ascending)
            .Order("effective_from", Ordering.Descending).Get()).Models;

    // Paged for the same reason as ImportedInvoiceIdsAsync: 1370 invoices exist after an import and
    // an unpaged read returns 1000 of them with no indication that anything is missing. A screen
    // that quietly drops the oldest 370 invoices is worse than one that is slow.
    public static async Task<List<VInvoice>> InvoicesAsync() =>
        await AllPagesAsync(() => Db.Client.From<VInvoice>()
            .Order("invoice_date", Ordering.Descending).Order("invoice_id", Ordering.Descending));

    public static async Task<List<VSalesLine>> LinesAsync(long invoiceId) =>
        (await Db.Client.From<VSalesLine>().Filter("invoice_id", Operator.Equals, invoiceId)
            .Order("line_id", Ordering.Ascending).Get()).Models;

    // size_id, not size_code: the codes sort as text, which puts "+11" before "+6.5" and reads as
    // nonsense to anyone who knows a sieve. size_id follows size_bucket.sort_order (-2, -6.5,
    // +6.5, +11), the same ordering PricesAsync already uses.
    public static async Task<List<VStockPosition>> StockAsync() =>
        (await Db.Client.From<VStockPosition>()
            .Order("grade_code", Ordering.Ascending).Order("size_id", Ordering.Ascending).Get()).Models;

    /// The grade × size buckets that have any ledger entry at all, for the Stock page's
    /// "hide empty buckets" filter. Balance alone cannot answer this: a bucket sold down to zero,
    /// or one whose only invoice was cancelled, still has a ledger worth reading.
    // ponytail: pulls two columns for every movement and dedupes client-side, because PostgREST has
    // no DISTINCT. Fine at ledger sizes measured in thousands; add a has_movements column to
    // v_stock_position if this ever gets heavy.
    public static async Task<HashSet<(string Grade, string Size)>> MovementBucketsAsync() =>
        (await Db.Client.From<VStockMovement>().Select("grade_code,size_code").Get())
            .Models.Select(m => (m.GradeCode, m.SizeCode)).ToHashSet();

    public static async Task<List<VStockMovement>> MovementsAsync(string gradeCode, string sizeCode) =>
        (await Db.Client.From<VStockMovement>()
            .Filter("grade_code", Operator.Equals, gradeCode)
            .Filter("size_code", Operator.Equals, sizeCode)
            .Order("movement_date", Ordering.Descending).Order("movement_id", Ordering.Descending).Get()).Models;

    public static async Task<List<VReceivablesAgeing>> ReceivablesAsync() =>
        await AllPagesAsync(() => Db.Client.From<VReceivablesAgeing>()
            .Order("due_date", Ordering.Ascending).Order("invoice_id", Ordering.Ascending));

    public static async Task<List<VReconciliation>> ReconciliationAsync() =>
        (await Db.Client.From<VReconciliation>()
            .Order("grade_code", Ordering.Ascending).Order("size_code", Ordering.Ascending).Get()).Models;

    // ---------- Excel import (docs/08 §4) ----------

    /// Imported invoices are exactly those numbered MIG-. Live invoices take their numbers from
    /// next_invoice_no(), so the two series can never overlap and a re-import can find its own
    /// previous rows without touching anything a user typed.
    private const string ImportedPrefix = "MIG-";
    private const string InvoiceIdColumn = "invoice_id";

    /// Unpaged, this silently missed 366 of 1366 imported invoices: a re-import then deleted 1000,
    /// inserted 1366, and left the remainder behind as duplicate MIG- numbers.
    public static async Task<List<long>> ImportedInvoiceIdsAsync()
    {
        var ids = new List<long>();
        for (int offset = 0; ; offset += PageSize)
        {
            var page = (await Db.Client.From<ImportedInvoice>()
                .Filter("invoice_no", Operator.Like, ImportedPrefix + "%")
                .Select("invoice_id")
                .Order("invoice_id", Ordering.Ascending)
                .Range(offset, offset + PageSize - 1).Get()).Models;

            ids.AddRange(page.Select(i => i.InvoiceId));
            if (page.Count < PageSize) return ids;
        }
    }

    /// <summary>
    /// Clears a previous import: receipts, then lines, then the invoices themselves — children
    /// first, so a failure part-way can never orphan a line against a deleted invoice.
    /// Ids are deleted in batches because they travel in the URL.
    /// </summary>
    public static async Task<int> DeleteImportedAsync(IProgress<ImportProgress>? progress = null)
    {
        var ids = await ImportedInvoiceIdsAsync();
        if (ids.Count == 0) return 0;

        int done = 0;
        foreach (var chunk in ids.Chunk(200))
        {
            var list = chunk.Select(id => id.ToString()).ToList();
            done += chunk.Length;
            progress?.Report(new ImportProgress(
                $"Removing the previous import… {done:N0} of {ids.Count:N0}",
                done, ids.Count));
            await Db.Client.From<Receipt>().Filter(InvoiceIdColumn, Operator.In, list).Delete();
            await Db.Client.From<SalesLine>().Filter(InvoiceIdColumn, Operator.In, list).Delete();
            await Db.Client.From<ImportedInvoice>().Filter(InvoiceIdColumn, Operator.In, list).Delete();
        }
        return ids.Count;
    }

    /// <summary>Creates any buyer named in the file that the database does not have yet, seeding
    /// default terms from the file (docs/08 §2.4). Returns name → id for every buyer needed.</summary>
    public static async Task<(Dictionary<string, long> Map, int Created)> EnsureBuyersAsync(
        IReadOnlyCollection<(string Name, int TermsDays)> wanted)
    {
        var existing = (await Db.Client.From<Buyer>().Get()).Models;
        var map = existing.ToDictionary(b => b.Name.Trim(), b => b.BuyerId, StringComparer.OrdinalIgnoreCase);

        var missing = wanted.Where(w => !map.ContainsKey(w.Name)).ToList();
        if (missing.Count > 0)
        {
            var made = (await Db.Client.From<Buyer>().Insert(missing.Select(w => new Buyer
            {
                Name = w.Name, DefaultTermsDays = w.TermsDays, Active = true,
            }).ToList())).Models;
            foreach (var b in made) map[b.Name.Trim()] = b.BuyerId;
        }
        return (map, missing.Count);
    }

    public static async Task<(Dictionary<string, long> Map, int Created)> EnsureBrokersAsync(
        IReadOnlyCollection<(string Name, decimal Pct)> wanted)
    {
        var existing = (await Db.Client.From<Broker>().Get()).Models;
        var map = existing.ToDictionary(b => b.Name.Trim(), b => b.BrokerId, StringComparer.OrdinalIgnoreCase);

        var missing = wanted.Where(w => !map.ContainsKey(w.Name)).ToList();
        if (missing.Count > 0)
        {
            var made = (await Db.Client.From<Broker>().Insert(missing.Select(w => new Broker
            {
                Name = w.Name, DefaultBrokerPct = w.Pct, Active = true,
            }).ToList())).Models;
            foreach (var b in made) map[b.Name.Trim()] = b.BrokerId;
        }
        return (map, missing.Count);
    }

    public static async Task<List<ImportedInvoice>> InsertImportedInvoicesAsync(
        List<ImportedInvoice> rows) =>
        (await Db.Client.From<ImportedInvoice>().Insert(rows)).Models;

    public static async Task InsertLinesAsync(List<SalesLine> rows) =>
        await Db.Client.From<SalesLine>().Insert(rows);

    public static async Task InsertReceiptsAsync(List<Receipt> rows) =>
        await Db.Client.From<Receipt>().Insert(rows);

    public static async Task<List<AuditLog>> AuditAsync(int limit = 500) =>
        (await Db.Client.From<AuditLog>().Order("changed_at", Ordering.Descending).Limit(limit).Get()).Models;

    public static async Task<List<Profile>> UsersAsync() =>
        (await Db.Client.From<Profile>().Order("full_name", Ordering.Ascending).Get()).Models;

    public static async Task<DashboardSummary> DashboardAsync(DateOnly from, DateOnly to)
    {
        var rows = await Db.Client.Rpc<List<DashboardSummary>>("dashboard_summary",
            new Dictionary<string, object?> { ["p_from"] = D(from), ["p_to"] = D(to) });
        return rows?.FirstOrDefault() ?? throw new InvalidOperationException("dashboard_summary returned no rows.");
    }

    // ---------- writes ----------

    public static async Task<long> SaveDraftAsync(DraftInvoice d)
    {
        try
        {
            long id;
            if (d.InvoiceId is null)
            {
                var inserted = (await Db.Client.From<SalesInvoice>().Insert(new SalesInvoice
                {
                    InvoiceDate = d.InvoiceDate,
                    BuyerId = d.BuyerId,
                    BrokerId = d.BrokerId,
                    BrokerPct = d.BrokerPct,
                    TermsDays = d.TermsDays,
                    DocType = d.DocType,
                    CurrencyId = d.CurrencyId,
                    Status = InvoiceStatus.DRAFT,
                    CreatedBy = Db.UserId,
                    UpdatedBy = Db.UserId,
                    ClientRef = d.ClientRef
                })).Models.FirstOrDefault() ?? throw new InvalidOperationException("Insert returned no invoice.");
                id = inserted.InvoiceId;
            }
            else
            {
                // Set() names the columns, so invoice_no / status / created_by are never sent back:
                // Update(model) would PATCH every mapped column and could drag a row that post_invoice
                // posted a moment ago back to DRAFT with a null invoice_no. The status filter makes
                // "is this still editable" Postgres' decision, not a stale read's — zero rows is the refusal.
                id = d.InvoiceId.Value;
                var updated = await Db.Client.From<SalesInvoice>()
                    .Filter("invoice_id", Operator.Equals, id)
                    .Filter("status", Operator.Equals, InvoiceStatus.DRAFT)
                    .Set(x => x.InvoiceDate, d.InvoiceDate)
                    .Set(x => x.BuyerId, d.BuyerId)
                    .Set(x => x.BrokerId, d.BrokerId)
                    .Set(x => x.BrokerPct, d.BrokerPct)
                    .Set(x => x.TermsDays, d.TermsDays)
                    .Set(x => x.DocType, d.DocType)
                    .Set(x => x.CurrencyId, d.CurrencyId)
                    .Set(x => x.UpdatedBy, Db.UserId)
                    .Update();

                if (updated.Models.Count == 0)
                    throw new InvalidOperationException("This invoice is no longer an editable draft.");

                await Db.Client.From<SalesLine>().Filter("invoice_id", Operator.Equals, id).Delete();
            }

            if (d.Lines.Count > 0)
                await Db.Client.From<SalesLine>().Insert(d.Lines.Select(l => new SalesLine
                {
                    InvoiceId = id,
                    GradeId = l.GradeId,
                    SizeId = l.SizeId,
                    GrossWeightCt = l.GrossWeightCt,
                    SelectionCt = l.SelectionCt,
                    PricePerCt = l.PricePerCt,
                    ExRate = l.ExRate == 0 ? 1 : l.ExRate, // a zero rate would silently zero the whole invoice
                    Less1Pct = l.Less1Pct,
                    Less2Pct = l.Less2Pct,
                    Remark = l.Remark
                }).ToList());

            return id;
        }
        catch (PostgrestException e)
        {
            throw new InvalidOperationException(Err(e), e);
        }
    }

    public static async Task<PostOutcome> PostAsync(long invoiceId, bool over = false)
    {
        try
        {
            var res = await Db.Client.Rpc("post_invoice",
                new Dictionary<string, object?> { ["p_invoice_id"] = invoiceId, ["p_override"] = over });

            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(res.Content) ? "{}" : res.Content!);
            var r = doc.RootElement;

            var ok = r.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            var needs = r.TryGetProperty("needs_override", out var nEl) && nEl.ValueKind == JsonValueKind.True;

            List<Shortfall> shortfalls = [];
            if (r.TryGetProperty("shortfalls", out var sf) && sf.ValueKind == JsonValueKind.Array)
                shortfalls = [.. sf.EnumerateArray().Select(x => new Shortfall(
                    Str(x, "grade_code") ?? "", Str(x, "size_code") ?? "",
                    Num(x, "balance_ct"), Num(x, "needed_ct")))];

            var message = ok ? null
                : needs ? "Not enough stock for this invoice."
                : Str(r, "message") ?? "Posting refused.";

            return new PostOutcome(ok, needs, Str(r, "invoice_no"), message, shortfalls);
        }
        catch (Exception e)
        {
            return new PostOutcome(false, false, null, Err(e), []);
        }
    }

    public static async Task<string?> CancelAsync(long invoiceId, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "A cancellation reason is required.";
        try
        {
            var res = await Db.Client.Rpc("cancel_invoice",
                new Dictionary<string, object?> { ["p_invoice_id"] = invoiceId, ["p_reason"] = reason });
            return Refused(res.Content);
        }
        catch (Exception e) { return Err(e); }
    }

    public static async Task<string?> ReceiptAsync(long invoiceId, decimal amount, string method)
    {
        try
        {
            await Db.Client.From<Receipt>().Insert(new Receipt
            {
                InvoiceId = invoiceId,
                ReceiptDate = Today,
                Amount = amount,
                Method = method,
                CreatedBy = Db.UserId
            });
            return null;
        }
        catch (Exception e) { return Err(e); }
    }

    public static async Task<string?> IntakeAsync(long gradeId, long sizeId, decimal weightCt, decimal pricePerCt)
    {
        try
        {
            var intake = (await Db.Client.From<RoughIntake>().Insert(new RoughIntake
            {
                IntakeDate = Today,
                GradeId = gradeId,
                SizeId = sizeId,
                WeightCt = weightCt,
                PricePerCt = pricePerCt,
                CreatedBy = Db.UserId
            })).Models.FirstOrDefault();
            if (intake is null) return "Intake insert returned no row.";

            // ponytail: two statements, not one transaction — a crash between them leaves an intake
            // with no movement, which v_reconciliation will show. Move both into an RPC if that ever fires.
            await Db.Client.From<StockMovement>().Insert(new StockMovement
            {
                MovementDate = Today,
                GradeId = gradeId,
                SizeId = sizeId,
                MovementType = Movement.INTAKE,
                WeightCt = weightCt,
                PricePerCt = pricePerCt,
                RefType = "rough_intake",
                RefId = intake.IntakeId,
                CreatedBy = Db.UserId
            });
            return null;
        }
        catch (Exception e) { return Err(e); }
    }

    public static async Task<string?> ConvertAsync(long fromGrade, long fromSize, long toGrade, long toSize,
        decimal weightCt, decimal? price)
    {
        try
        {
            var res = await Db.Client.Rpc("convert_stock", new Dictionary<string, object?>
            {
                ["p_from_grade_id"] = fromGrade,
                ["p_from_size_id"] = fromSize,
                ["p_to_grade_id"] = toGrade,
                ["p_to_size_id"] = toSize,
                ["p_weight_ct"] = weightCt,
                ["p_price_per_ct"] = price,
                ["p_date"] = D(Today),
                // ponytail: a fresh ref per call, so a retry after a timeout converts twice.
                // Thread the caller's ref through when the UI grows a retry button.
                ["p_client_ref"] = Guid.NewGuid().ToString()
            });
            return Refused(res.Content);
        }
        catch (Exception e) { return Err(e); }
    }

    public static async Task<string?> RejectionAsync(long gradeId, long sizeId, decimal weightCt, decimal? price)
    {
        try
        {
            await Db.Client.From<StockMovement>().Insert(new StockMovement
            {
                MovementDate = Today,
                GradeId = gradeId,
                SizeId = sizeId,
                MovementType = Movement.REJECTION,
                WeightCt = weightCt,
                PricePerCt = price,
                RefType = "manual",
                CreatedBy = Db.UserId
            });
            return null;
        }
        catch (Exception e) { return Err(e); }
    }

    public static async Task<string?> AdjustAsync(long gradeId, long sizeId, decimal signedWeightCt, string reason)
    {
        // Returned, not thrown: every sibling reports failure this way, and callers are async void
        // click handlers where an escaping exception takes the whole app down.
        if (string.IsNullOrWhiteSpace(reason)) return "An adjustment reason is required.";
        try
        {
            await Db.Client.From<StockMovement>().Insert(new StockMovement
            {
                MovementDate = Today,
                GradeId = gradeId,
                SizeId = sizeId,
                MovementType = Movement.ADJUST,
                WeightCt = signedWeightCt,
                RefType = "manual",
                Reason = reason,
                CreatedBy = Db.UserId
            });
            return null;
        }
        catch (Exception e) { return Err(e); }
    }

    public static async Task<string?> AddBuyerAsync(string name, int termsDays)
    {
        try
        {
            await Db.Client.From<Buyer>().Insert(new Buyer { Name = name, DefaultTermsDays = termsDays, Active = true });
            return null;
        }
        catch (Exception e) { return Err(e); }
    }

    /// <summary>
    /// Saves an edited alias list for one grade. Only this column is writable from Master data:
    /// code, name and sort order are referenced by imports and stock, and changing them from a
    /// grid cell would be a schema-level act dressed up as a typo.
    /// </summary>
    public static async Task<string?> SetGradeAliasesAsync(long gradeId, string aliases)
    {
        try
        {
            await Db.Client.From<Grade>().Filter("grade_id", Operator.Equals, gradeId)
                .Set(g => g.Aliases!, aliases.Length == 0 ? null! : aliases).Update();
            return null;
        }
        catch (Exception e) { return Err(e); }
    }

    public static async Task<string?> AddBrokerAsync(string name, decimal pct)
    {
        try
        {
            await Db.Client.From<Broker>().Insert(new Broker { Name = name, DefaultBrokerPct = pct, Active = true });
            return null;
        }
        catch (Exception e) { return Err(e); }
    }

    public static async Task<string?> SetPriceAsync(long gradeId, long sizeId, string context, decimal price)
    {
        try
        {
            var open = (await Db.Client.From<PriceList>()
                .Filter("grade_id", Operator.Equals, gradeId)
                .Filter("size_id", Operator.Equals, sizeId)
                .Filter("context", Operator.Equals, context).Get())
                .Models.Where(p => p.EffectiveTo is null);

            foreach (var row in open)
            {
                row.EffectiveTo = Today.AddDays(-1);
                await Db.Client.From<PriceList>().Update(row);
            }

            await Db.Client.From<PriceList>().Insert(new PriceList
            {
                GradeId = gradeId,
                SizeId = sizeId,
                Context = context,
                PricePerCt = price,
                EffectiveFrom = Today
            });
            return null;
        }
        catch (Exception e) { return Err(e); }
    }

    public static async Task<string?> SetConfigAsync(string key, string value)
    {
        try
        {
            var row = await Db.Client.From<AppConfig>().Filter("key", Operator.Equals, key).Single();
            if (row is null)
                await Db.Client.From<AppConfig>().Insert(new AppConfig { Key = key, Value = value });
            else
            {
                row.Value = value;
                await Db.Client.From<AppConfig>().Update(row);
            }
            return null;
        }
        catch (Exception e) { return Err(e); }
    }

    // ---------- plumbing ----------

    static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    static string D(DateOnly d) => d.ToString("yyyy-MM-dd");

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static decimal Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;

    /// <summary>An RPC that answered { ok:false } instead of raising — turn it back into a message.</summary>
    static string? Refused(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json!);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            if (r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
                return Str(r, "message") ?? Str(r, "error") ?? "The database refused the operation.";
            return null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>RLS denials, check constraints and the negative-stock refusal are all just text to the user.</summary>
    static string Err(Exception e)
    {
        if (e is PostgrestException { Content: { } content } && !string.IsNullOrWhiteSpace(content))
        {
            try
            {
                using var doc = JsonDocument.Parse(content);
                var r = doc.RootElement;
                var text = string.Join(" ", new[] { Str(r, "message"), Str(r, "details"), Str(r, "hint") }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
                if (text.Length > 0) return text;
            }
            catch (JsonException) { return content; }
        }
        return e.Message;
    }
}
