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

/// <summary>What a stock import landed, for the report shown afterwards.</summary>
public sealed record StockImportResult(int Parcels, int ReplacedParcels,
                                       decimal TotalCarats, decimal TotalValue);

/// <summary>
/// A stock write that either failed, or went through with something worth saying. The warning
/// carries the negative-stock policy's WARN case: before 0014 the policy could only block or stay
/// silent, so a bucket going below zero under the default policy told the user nothing at all.
/// </summary>
public sealed record WriteResult(string? Failure, string? Warning = null)
{
    public bool Ok => Failure is null;
}

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

    public static async Task<List<GradeSize>> GradeSizesAsync() =>
        (await Db.Client.From<GradeSize>().Get()).Models;

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

    /// <summary>
    /// Every posted sales line in a date range, paged to the end. The dashboard's grade filter
    /// needs this: v_invoice carries no grade — grade lives on the line — so without the lines a
    /// grade can only narrow the inventory charts, which is what it used to do.
    ///
    /// Ordered by line_id because AllPagesAsync needs a tie-breaking order to page safely.
    /// </summary>
    public static async Task<List<VSalesLine>> SalesLinesAsync(DateOnly from, DateOnly to) =>
        await AllPagesAsync(() => Db.Client.From<VSalesLine>()
            .Filter("status", Operator.Equals, InvoiceStatus.POSTED)
            .Filter("invoice_date", Operator.GreaterThanOrEqual, D(from))
            .Filter("invoice_date", Operator.LessThanOrEqual, D(to))
            .Order("line_id", Ordering.Ascending));

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

    /// Paged: a bucket that has been traded for years outgrows the 1000-row answer, and an
    /// unpaged read gave no sign of it — the ledger just stopped, and its "N movements" caption
    /// reported the truncated count as fact. movement_id breaks ties so pages cannot overlap.
    public static async Task<List<VStockMovement>> MovementsAsync(string gradeCode, string sizeCode) =>
        await AllPagesAsync(() => Db.Client.From<VStockMovement>()
            .Filter("grade_code", Operator.Equals, gradeCode)
            .Filter("size_code", Operator.Equals, sizeCode)
            .Order("movement_date", Ordering.Descending).Order("movement_id", Ordering.Descending));

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
    public const string ImportedPrefix = "MIG-";
    private const string InvoiceIdColumn = "invoice_id";

    /// <summary>
    /// Whether an invoice came from a workbook rather than from the entry screen. The number is
    /// the only thing that says so, and the two series can never overlap — which is what lets a
    /// list of both be split without a second query.
    /// </summary>
    public static bool IsImported(string? invoiceNo) =>
        invoiceNo?.StartsWith(ImportedPrefix, StringComparison.Ordinal) == true;

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
    /// <summary>What replace_imported_sales wrote, straight from its jsonb result.</summary>
    public sealed record SalesImportOutcome(int Deleted, int Invoices, int Lines, int Receipts);

    /// <summary>
    /// Clears the previous sale import and writes the new one in a single transaction (0018).
    ///
    /// This replaced a delete loop that ran BEFORE the inserts, over separate HTTP calls: between
    /// the two the old dataset was gone and the new one had not arrived, and anything failing in
    /// that window left the database holding neither, with no way back. The delete is now inside
    /// the function, so a failure of any kind — including one that refuses the payload outright —
    /// rolls back and leaves the previous import untouched.
    /// </summary>
    public static async Task<SalesImportOutcome> ReplaceImportedSalesAsync(
        Dictionary<string, object?> payload)
    {
        var res = await Db.Client.Rpc("replace_imported_sales",
                                      new Dictionary<string, object?> { ["p_payload"] = payload });

        var outcome = Json(res.Content);
        return new SalesImportOutcome(Int(outcome, "deleted"), Int(outcome, "invoices"),
                                      Int(outcome, "lines"), Int(outcome, "receipts"));
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

    // The chunked invoice / line / receipt inserts that used to live here went with the delete loop
    // they paired with: replace_imported_sales writes all three inside one transaction now, so
    // there is nothing left for the client to sequence and nothing to get half-done.

    /// How many audit rows one load takes. Public so the screen can say when it hit the ceiling
    /// rather than presenting a truncated history as a complete one.
    public const int AuditLimit = 500;

    /// <summary>
    /// The newest <paramref name="limit"/> audit rows, optionally for one table.
    ///
    /// The entity filter has to run in the DATABASE, not over what was fetched. One bulk import
    /// deletes a thousand receipts, those thousand rows are the newest thousand, and a page that
    /// loads 500 and filters in memory can no longer see a single stock movement or invoice — the
    /// trail is intact, the screen just never asked for it.
    /// </summary>
    public static async Task<List<AuditLog>> AuditAsync(string? entity = null, int limit = AuditLimit)
    {
        var query = Db.Client.From<AuditLog>().Order("changed_at", Ordering.Descending);
        if (!string.IsNullOrEmpty(entity))
            query = query.Filter("table_name", Operator.Equals, entity);
        return (await query.Limit(limit).Get()).Models;
    }

    /// <summary>
    /// The tables that carry an audit trigger (0004). Offered by the Audit page regardless of what
    /// the current page of rows happens to contain, so a filter is reachable even when one noisy
    /// table fills the whole limit. Add to this when a trigger is added.
    /// </summary>
    public static readonly string[] AuditedTables =
        ["buyer", "price_list", "receipt", "sales_invoice", "sales_line", "stock_movement"];

    public static async Task<List<Profile>> UsersAsync() =>
        (await Db.Client.From<Profile>().Order("full_name", Ordering.Ascending).Get()).Models;

    public static async Task<DashboardSummary> DashboardAsync(DateOnly from, DateOnly to)
    {
        var rows = await Db.Client.Rpc<List<DashboardSummary>>("dashboard_summary",
            new Dictionary<string, object?> { ["p_from"] = D(from), ["p_to"] = D(to) });
        return rows?.FirstOrDefault() ?? throw new InvalidOperationException("dashboard_summary returned no rows.");
    }

    /// <summary>
    /// W7 · margin over POSTED invoices in the range (0019). Separate from dashboard_summary
    /// because that function's body is not in this repository; a second call is cheaper than
    /// rewriting it from a guess.
    /// </summary>
    public static async Task<MarginSummary> MarginAsync(DateOnly from, DateOnly to)
    {
        var rows = await Db.Client.Rpc<List<MarginSummary>>("margin_summary",
            new Dictionary<string, object?> { ["p_from"] = D(from), ["p_to"] = D(to) });
        return rows?.FirstOrDefault() ?? throw new InvalidOperationException("margin_summary returned no rows.");
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
            // Under negative_stock = block the function RAISES rather than returning
            // needs_override, so the shortfalls arrive inside the exception text as
            // "Posting would take stock negative: [ ... ]". Left alone the user is shown raw
            // jsonb; the figures are all there, so lift them out and report them the same way
            // the warn path already does.
            string text = Err(e);
            int bracket = text.IndexOf('[');
            if (bracket >= 0 && text.Contains("stock negative", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var doc = JsonDocument.Parse(text[bracket..]);
                    var shortfalls = doc.RootElement.EnumerateArray().Select(x => new Shortfall(
                        Str(x, "grade_code") ?? "", Str(x, "size_code") ?? "",
                        Num(x, "balance_ct"), Num(x, "needed_ct"))).ToList();

                    if (shortfalls.Count > 0)
                        return new PostOutcome(false, false, null,
                            "Not enough stock for this invoice.", shortfalls);
                }
                catch (JsonException) { /* fall through to the raw text */ }
            }
            return new PostOutcome(false, false, null, text, []);
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

    /// <summary>
    /// Every receipt booked against one invoice, oldest first. The table was written to and never
    /// read back, so two receipts of 25,000 looked exactly like one of 50,000 — the outstanding
    /// figure was the only trace payment left, and it carries no date, method or history.
    /// </summary>
    public static async Task<List<Receipt>> ReceiptsAsync(long invoiceId) =>
        (await Db.Client.From<Receipt>()
                        .Filter(InvoiceIdColumn, Operator.Equals, invoiceId.ToString())
                        .Order("receipt_date", Ordering.Ascending)
                        .Get()).Models;

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

    /// <param name="clientRef">
    /// Identifies the OPERATION, not the attempt. 0007 puts a unique index on stock_movement
    /// .client_ref precisely so a replay of a write whose response was lost is discarded instead of
    /// duplicated — but that only works if the retry sends the SAME ref. Minting one per call made
    /// the index unreachable: every attempt looked like a new operation. Callers that can retry
    /// generate it once and pass it on both tries.
    /// </param>
    public static async Task<WriteResult> ConvertAsync(long fromGrade, long fromSize, long toGrade, long toSize,
        decimal weightCt, decimal? price, Guid? clientRef = null)
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
                ["p_client_ref"] = (clientRef ?? Guid.NewGuid()).ToString()
            });
            return Outcome(res.Content);
        }
        catch (Exception e) { return Wrote(e); }
    }

    /// Through record_rejection rather than a direct insert: a bare insert asked nothing about the
    /// balance, in the client or the schema, so rejecting 500 ct out of a bucket holding 10 posted
    /// silently and left it at -490. The RPC applies the same guard conversion and posting use.
    /// <summary>
    /// Records a rejection and, in the same transaction, the dispositions describing where the
    /// rejected carats went. The dispositions used to be typed on screen and thrown away — the app
    /// said so, but a form that discards what it collects is still a form that discards what it
    /// collects. rejection_disposition (0018) is where they land.
    /// </summary>
    public static async Task<WriteResult> RejectionAsync(
        long gradeId, long sizeId, decimal weightCt, decimal? price,
        IReadOnlyList<(decimal WeightCt, long? ToGradeId, string? Note)>? dispositions = null,
        Guid? clientRef = null)
    {
        try
        {
            var res = await Db.Client.Rpc("record_rejection", new Dictionary<string, object?>
            {
                ["p_grade_id"] = gradeId,
                ["p_size_id"] = sizeId,
                ["p_weight_ct"] = weightCt,
                ["p_price_per_ct"] = price,
                ["p_date"] = D(Today),
                ["p_client_ref"] = (clientRef ?? Guid.NewGuid()).ToString(),
                ["p_dispositions"] = (dispositions ?? [])
                    .Where(d => d.WeightCt > 0)
                    .Select(d => new Dictionary<string, object?>
                    {
                        ["weight_ct"] = d.WeightCt,
                        ["to_grade_id"] = d.ToGradeId,
                        ["note"] = d.Note,
                    }).ToList(),
            });
            return Outcome(res.Content);
        }
        catch (Exception e) { return Wrote(e); }
    }

    public static async Task<WriteResult> AdjustAsync(long gradeId, long sizeId, decimal signedWeightCt, string reason,
        Guid? clientRef = null)
    {
        // Returned, not thrown: every sibling reports failure this way, and callers are async void
        // click handlers where an escaping exception takes the whole app down.
        if (string.IsNullOrWhiteSpace(reason)) return new WriteResult("An adjustment reason is required.");
        try
        {
            var res = await Db.Client.Rpc("adjust_stock", new Dictionary<string, object?>
            {
                ["p_grade_id"] = gradeId,
                ["p_size_id"] = sizeId,
                ["p_weight_ct"] = signedWeightCt,
                ["p_reason"] = reason,
                ["p_date"] = D(Today),
                ["p_client_ref"] = (clientRef ?? Guid.NewGuid()).ToString()
            });
            return Outcome(res.Content);
        }
        catch (Exception e) { return Wrote(e); }
    }

    // ---------- Stock import ----------

    /// Imported opening stock is exactly the movements tagged with this ref_type. A hand-entered
    /// intake writes "rough_intake", so the two can never be confused and a re-import can find its
    /// own previous rows without touching anything a user typed.
    public const string StockImportRef = "stock_import";

    /// <summary>The rough_intake ids a previous stock import created, found through its movements.</summary>
    public static async Task<List<long>> ImportedStockIdsAsync()
    {
        var ids = new List<long>();
        for (int offset = 0; ; offset += PageSize)
        {
            var page = (await Db.Client.From<StockMovement>()
                .Filter("ref_type", Operator.Equals, StockImportRef)
                .Select("movement_id,ref_id")
                .Order("movement_id", Ordering.Ascending)
                .Range(offset, offset + PageSize - 1).Get()).Models;

            ids.AddRange(page.Where(m => m.RefId.HasValue).Select(m => m.RefId!.Value));
            if (page.Count < PageSize) return ids.Distinct().ToList();
        }
    }

    // delete_imported_stock() (0016) is no longer called from here: replace_imported_stock does the
    // clear inside the same transaction as the rewrite, which is the whole point. The function
    // stays in the database — it is still the right tool for "clear the import and put nothing
    // back" — but nothing in the app should reach for it during an import again.

    /// <summary>
    /// Replaces the imported opening stock with the workbook's. Each holding becomes a rough_intake
    /// parcel and a matching INTAKE movement, which is what v_stock_position reads for both balance
    /// and average cost.
    ///
    /// One call to replace_imported_stock (0018), so the clear and the rewrite are one transaction.
    /// It used to be a delete loop followed by an insert loop over separate HTTP calls: a failure
    /// between them left the ledger emptied and nothing put back, which is exactly what happened on
    /// 05 Aug 2026 — 133 movements deleted, replacement never landed, position went negative.
    /// Now the whole thing rolls back and the previous import is still there.
    /// </summary>
    public static async Task<StockImportResult> ImportStockAsync(
        IReadOnlyList<StockRow> rows, DateOnly asAt,
        IReadOnlyDictionary<string, long> gradeIds, IReadOnlyDictionary<string, long> sizeIds,
        IProgress<ImportProgress>? progress = null)
    {
        progress?.Report(new ImportProgress(
            $"Replacing the stock position… {rows.Count:N0} holding(s)", 0, rows.Count));

        var payload = rows.Select(r => new Dictionary<string, object?>
        {
            ["grade_id"] = gradeIds[r.GradeCode],
            ["size_id"] = sizeIds[r.SizeCode],
            ["weight_ct"] = r.WeightCt,
            ["price_per_ct"] = r.PricePerCt,
        }).ToList();

        var res = await Db.Client.Rpc("replace_imported_stock", new Dictionary<string, object?>
        {
            ["p_as_at"] = asAt.ToString("yyyy-MM-dd"),
            ["p_rows"] = payload,
        });

        var outcome = Json(res.Content);
        int written = Int(outcome, "written");
        int deleted = Int(outcome, "deleted");

        // The function returns what it wrote. A short count means rows were dropped silently, which
        // an import must never do quietly — and because it is one transaction, saying so is the
        // only thing left to do; nothing partial can be sitting in the database.
        if (written != rows.Count)
            throw new InvalidOperationException(
                $"Sent {rows.Count} holding(s) but the database wrote {written}.");

        progress?.Report(new ImportProgress("Stock replaced", rows.Count, rows.Count));

        return new StockImportResult(written, deleted,
                                     rows.Sum(r => r.WeightCt),
                                     rows.Sum(r => r.WeightCt * r.PricePerCt));
    }

    /// The jsonb an RPC returned, or an empty object when it returned nothing parseable.
    private static System.Text.Json.JsonElement Json(string? content)
    {
        try { return System.Text.Json.JsonDocument.Parse(content ?? "{}").RootElement; }
        catch (System.Text.Json.JsonException) { return default; }
    }

    private static int Int(System.Text.Json.JsonElement e, string name) =>
        e.ValueKind == System.Text.Json.JsonValueKind.Object
        && e.TryGetProperty(name, out var v) && v.TryGetInt32(out int n) ? n : 0;

    /// <summary>
    /// Renames a buyer, resets its default terms, or deactivates it. Set() names the columns, so
    /// nothing else on the row is sent — and buyer_id is untouched, which is what every invoice
    /// ever raised for them points at. A rename is not a new buyer.
    /// </summary>
    public static async Task<string?> UpdateBuyerAsync(long buyerId, string name, int termsDays, bool active)
    {
        try
        {
            await Db.Client.From<Buyer>()
                .Filter("buyer_id", Operator.Equals, buyerId.ToString())
                .Set(x => x.Name, name)
                .Set(x => x.DefaultTermsDays, termsDays)
                .Set(x => x.Active, active)
                .Update();
            return null;
        }
        catch (Exception e) { return Err(e); }
    }

    public static async Task<string?> UpdateBrokerAsync(long brokerId, string name, decimal pct, bool active)
    {
        try
        {
            await Db.Client.From<Broker>()
                .Filter("broker_id", Operator.Equals, brokerId.ToString())
                .Set(x => x.Name, name)
                .Set(x => x.DefaultBrokerPct, pct)
                .Set(x => x.Active, active)
                .Update();
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

    // ---------- sign-in lockout (0025) ----------

    /// <summary>Seconds this account is still locked for, or 0. Asked before the password is sent.</summary>
    public static Task<int> LoginLockedForAsync(string email) =>
        LockRpc("login_locked_for", email);

    /// <summary>Records a refused sign-in and returns the seconds it is now locked for.</summary>
    public static Task<int> NoteLoginFailureAsync(string email) =>
        LockRpc("note_login_failure", email);

    public static async Task ClearLoginFailuresAsync(string email)
    {
        try { await Db.Client.Rpc("clear_login_failures", new Dictionary<string, object?> { ["p_email"] = email }); }
        catch { /* the lock lapses on its own; a failure here must never block a valid sign-in */ }
    }

    /// A lockout that cannot be reached must not become a lockout that cannot be escaped: if the
    /// server is unreachable or the migration has not been applied, sign-in proceeds. The password
    /// is still the boundary — this only decides whether we bother asking.
    private static async Task<int> LockRpc(string fn, string email)
    {
        try
        {
            var res = await Db.Client.Rpc(fn, new Dictionary<string, object?> { ["p_email"] = email });
            return int.TryParse(res?.Content?.Trim('"', ' ', '\n'), out int seconds) ? seconds : 0;
        }
        catch { return 0; }
    }

    // ---------- plumbing ----------

    static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    static string D(DateOnly d) => d.ToString("yyyy-MM-dd");

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static decimal Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;

    /// <summary>
    /// The { ok, warning } answer the stock-writing RPCs give. A refusal becomes the failure
    /// message; a warning rides along with a success.
    /// </summary>
    static WriteResult Outcome(string? json)
    {
        var failure = Refused(json);
        if (failure is not null || string.IsNullOrWhiteSpace(json)) return new WriteResult(failure);
        try
        {
            using var doc = JsonDocument.Parse(json!);
            return new WriteResult(null, doc.RootElement.ValueKind == JsonValueKind.Object
                ? Str(doc.RootElement, "warning")
                : null);
        }
        catch (JsonException) { return new WriteResult(null); }
    }

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
    /// <summary>
    /// Turns a failed stock write into a result — except when the failure IS the success.
    ///
    /// A unique violation on client_ref means this exact operation already landed and we simply
    /// never saw the acknowledgement. That is the whole purpose of 0007's index, so reporting it
    /// as an error would train users to retry until they had duplicated the movement by some other
    /// route. Any other unique violation is a real clash and is reported.
    ///
    /// Outbox.SendAsync has always drawn this distinction; the direct write paths did not, so a
    /// manual retry after a timeout showed "duplicate key value violates unique constraint".
    /// </summary>
    static WriteResult Wrote(Exception e)
    {
        string text = Err(e);
        return text.Contains("client_ref", StringComparison.Ordinal)
               && text.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
            ? new WriteResult(null, "Already recorded — this was sent once before.")
            : new WriteResult(text);
    }

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
