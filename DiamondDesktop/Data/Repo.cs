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
/// <param name="BatchId">
/// What the database stamped on this import's parcels. Read back from the function rather than sent
/// and assumed, so the id shown afterwards is provably the id in the table.
/// </param>
public sealed record StockImportResult(int Parcels, int ReplacedParcels,
                                       decimal TotalCarats, decimal TotalValue,
                                       Guid? BatchId = null);

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

    // Column names, once each. They are strings because PostgREST names columns in the URL, and a
    // typo in one is a filter that silently matches nothing rather than a compile error.
    private const string InvoiceIdColumn = "invoice_id";
    private const string MovementIdColumn = "movement_id";
    private const string GradeIdColumn = "grade_id";
    private const string SizeIdColumn = "size_id";
    private const string GradeCodeColumn = "grade_code";
    private const string SizeCodeColumn = "size_code";

    /// <summary>
    /// Reads a query to the end, a page at a time. The query must carry an ordering that breaks
    /// ties, otherwise rows can repeat or vanish between requests.
    /// </summary>
    private static async Task<List<T>> AllPagesAsync<T>(
        Func<Supabase.Postgrest.Interfaces.IPostgrestTable<T>> query)
        where T : Supabase.Postgrest.Models.BaseModel, new()
    {
        var all = new List<T>();
        // A while, not a for: the stop condition is the size of the page just fetched, which no
        // counter in a for-header can test. A short page is the last page — PostgREST gives no
        // other sign that the end has been reached.
        int offset = 0;
        while (true)
        {
            var page = (await query().Range(offset, offset + PageSize - 1).Get()).Models;
            all.AddRange(page);
            if (page.Count < PageSize) return all;
            offset += PageSize;
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

    /// <summary>
    /// Buyers for a picker, which means the ACTIVE ones -- deactivating a buyer is how the office
    /// stops new invoices being written against it.
    ///
    /// <paramref name="activeOnly"/> false is for Master data, and only for Master data. That page
    /// is where a buyer is deactivated, and while it read this list filtered the row vanished the
    /// moment it was switched off: no way to see it, no way to switch it back on, and the button
    /// that did it still saying "or deactivate it". A one-way door nobody meant to build.
    /// </summary>
    public static async Task<List<Buyer>> BuyersAsync(bool activeOnly = true)
    {
        var q = Db.Client.From<Buyer>().Order("name", Ordering.Ascending);
        return (await (activeOnly ? q.Filter("active", Operator.Equals, "true") : q).Get()).Models;
    }

    /// <inheritdoc cref="BuyersAsync"/>
    public static async Task<List<Broker>> BrokersAsync(bool activeOnly = true)
    {
        var q = Db.Client.From<Broker>().Order("name", Ordering.Ascending);
        return (await (activeOnly ? q.Filter("active", Operator.Equals, "true") : q).Get()).Models;
    }

    public static async Task<List<Currency>> CurrenciesAsync() =>
        (await Db.Client.From<Currency>().Order("code", Ordering.Ascending).Get()).Models;

    public static async Task<Dictionary<string, string>> ConfigAsync() =>
        (await Db.Client.From<AppConfig>().Get()).Models.ToDictionary(c => c.Key, c => c.Value ?? "");

    public static async Task<List<PriceList>> PricesAsync() =>
        (await Db.Client.From<PriceList>()
            .Order(GradeIdColumn, Ordering.Ascending).Order(SizeIdColumn, Ordering.Ascending)
            .Order("effective_from", Ordering.Descending).Get()).Models;

    // Paged for the same reason as ImportedInvoiceIdsAsync: 1370 invoices exist after an import and
    // an unpaged read returns 1000 of them with no indication that anything is missing. A screen
    // that quietly drops the oldest 370 invoices is worse than one that is slow.
    public static async Task<List<VInvoice>> InvoicesAsync() =>
        await AllPagesAsync(() => Db.Client.From<VInvoice>()
            .Order("invoice_date", Ordering.Descending).Order(InvoiceIdColumn, Ordering.Descending));

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
        (await Db.Client.From<VSalesLine>().Filter(InvoiceIdColumn, Operator.Equals, invoiceId)
            .Order("line_id", Ordering.Ascending).Get()).Models;

    // size_id, not size_code: the codes sort as text, which puts "+11" before "+6.5" and reads as
    // nonsense to anyone who knows a sieve. size_id follows size_bucket.sort_order (-2, -6.5,
    // +6.5, +11), the same ordering PricesAsync already uses.
    public static async Task<List<VStockPosition>> StockAsync() =>
        (await Db.Client.From<VStockPosition>()
            .Order(GradeCodeColumn, Ordering.Ascending).Order(SizeIdColumn, Ordering.Ascending).Get()).Models;

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
            .Filter(GradeCodeColumn, Operator.Equals, gradeCode)
            .Filter(SizeCodeColumn, Operator.Equals, sizeCode)
            .Order("movement_date", Ordering.Descending).Order(MovementIdColumn, Ordering.Descending));

    /// <summary>
    /// Manual adjustments -- the corrections someone typed, as opposed to the movements a sale or
    /// an import wrote. Read from the TABLE rather than v_stock_movement because the view does not
    /// expose created_by, and "who changed this" is the whole point of showing them.
    ///
    /// Filtered server side. No new table: an adjustment has always been an ordinary ledger row.
    /// </summary>
    public static async Task<List<StockMovement>> ManualAdjustmentsAsync() =>
        await AllPagesAsync(() => Db.Client.From<StockMovement>()
            .Filter("movement_type", Operator.Equals, Movement.ADJUST)
            .Filter("ref_type", Operator.Equals, "manual")
            .Order(MovementIdColumn, Ordering.Descending));

    /// <summary>
    /// The movements the last stock import placed -- what the workbook said when it was imported.
    ///
    /// Same shape as RejectionsAsync: an existing view, filtered server side, no new table and no
    /// new calculation. The office reads a BALANCE STOCK figure off their sheet and the app shows
    /// a different one; this is the first of those two numbers, so the screen can show both and
    /// the difference stops looking like a fault.
    /// </summary>
    public static async Task<List<VStockMovement>> ImportedStockAsync() =>
        await AllPagesAsync(() => Db.Client.From<VStockMovement>()
            .Filter("ref_type", Operator.Equals, StockImportRef)
            .Order(MovementIdColumn, Ordering.Descending));

    /// <summary>
    /// Every REJECTION movement, filtered in the DATABASE rather than after the fact.
    ///
    /// There is no stored rejection total and no view that offers one -- v_stock_position carries
    /// balances, not movement types -- so the figure is the sum of these rows. Filtering server
    /// side keeps the read at the rejections instead of the whole ledger, which matters once the
    /// migration lands and this table is measured in thousands.
    ///
    /// Both origins are included: a rejection taken off an invoice line (ref_type sales_line) and
    /// one recorded by hand on Intake and movements. Both are carats that left stock as rejected.
    /// </summary>
    public static async Task<List<VStockMovement>> RejectionsAsync() =>
        await AllPagesAsync(() => Db.Client.From<VStockMovement>()
            .Filter("movement_type", Operator.Equals, Movement.REJECTION)
            .Order(MovementIdColumn, Ordering.Descending));

    /// <summary>
    /// Every SALE movement, filtered in the DATABASE, exactly as RejectionsAsync is.
    ///
    /// No new table, no new view and no new arithmetic: these are the same rows the ledger and
    /// v_reconciliation already read. The Stock report sums them so it can show what has been sold
    /// out of the buckets on screen beside what is left in them.
    ///
    /// SALE only. A REJECTION is carats that left as rejected, not as a sale, and it has its own
    /// figure on that card already -- adding the two together would say the parcel was sold twice.
    /// </summary>
    public static async Task<List<VStockMovement>> SalesAsync() =>
        await AllPagesAsync(() => Db.Client.From<VStockMovement>()
            .Filter("movement_type", Operator.Equals, Movement.SALE)
            .Order(MovementIdColumn, Ordering.Descending));

    /// <summary>
    /// Holds what a line currently says against its bucket (0043).
    ///
    /// Called again every time that line changes, and the database UPSERTS on
    /// (client_ref, line_key) -- so this is the same hold being corrected, never a second one.
    /// A weight of zero releases instead, leaving no row claiming nothing.
    ///
    /// Returns null when it worked, or the database's own sentence when it refused -- which it
    /// does when the line is larger than the bucket holds. NOT thrown: this is called while
    /// somebody is typing, and an exception out of an `async void` cell handler ends the process.
    /// </summary>
    public static async Task<string?> ReserveLineAsync(
        Guid clientRef, Guid lineKey, long gradeId, long sizeId, decimal weightCt)
    {
        try
        {
            await Db.Client.Rpc("reserve_line", new Dictionary<string, object?>
            {
                ["p_client_ref"] = clientRef,
                ["p_line_key"]   = lineKey.ToString(),
                ["p_grade_id"]   = gradeId,
                ["p_size_id"]    = sizeId,
                ["p_weight_ct"]  = weightCt,
            });
            Db.NoteTransport(null);
            return null;
        }
        catch (Exception e) { Db.NoteTransport(e); return Friendly.Message(e.Message); }
    }

    /// <summary>Gives one line's carats back: the row was removed, or emptied.</summary>
    public static async Task<string?> ReleaseLineAsync(Guid clientRef, Guid lineKey)
    {
        try
        {
            await Db.Client.Rpc("release_line", new Dictionary<string, object?>
            {
                ["p_client_ref"] = clientRef,
                ["p_line_key"]   = lineKey.ToString(),
            });
            Db.NoteTransport(null);
            return null;
        }
        catch (Exception e) { Db.NoteTransport(e); return Friendly.Message(e.Message); }
    }

    /// <summary>
    /// MOVE TO STOCK. Returns everything this entry is holding, and reports how much that was.
    ///
    /// It cannot return more than was taken: the function deletes only the rows this client_ref
    /// holds, and each holds exactly what its line last said. There is no figure sent for the
    /// database to get wrong. Pressing it twice returns the carats once and then zero.
    /// </summary>
    public static async Task<(string? Failure, decimal Returned, int Lines)> ReleaseEntryAsync(Guid clientRef)
    {
        try
        {
            var res = await Db.Client.Rpc("release_entry", new Dictionary<string, object?>
            {
                ["p_client_ref"] = clientRef,
            });
            Db.NoteTransport(null);

            var body = System.Text.Json.JsonDocument.Parse(res.Content ?? "{}").RootElement;
            return (null,
                    body.TryGetProperty("returned", out var ct) ? ct.GetDecimal() : 0m,
                    body.TryGetProperty("lines", out var n) ? n.GetInt32() : 0);
        }
        catch (Exception e) { Db.NoteTransport(e); return (Friendly.Message(e.Message), 0m, 0); }
    }

    public static async Task<List<VReceivablesAgeing>> ReceivablesAsync() =>
        await AllPagesAsync(() => Db.Client.From<VReceivablesAgeing>()
            .Order("due_date", Ordering.Ascending).Order(InvoiceIdColumn, Ordering.Ascending));

    public static async Task<List<VReconciliation>> ReconciliationAsync() =>
        (await Db.Client.From<VReconciliation>()
            .Order(GradeCodeColumn, Ordering.Ascending).Order(SizeCodeColumn, Ordering.Ascending).Get()).Models;

    // ---------- Excel import (docs/08 §4) ----------

    /// Imported invoices are exactly those numbered MIG-. Live invoices take their numbers from
    /// next_invoice_no(), so the two series can never overlap and a re-import can find its own
    /// previous rows without touching anything a user typed.
    public const string ImportedPrefix = "MIG-";

    /// <summary>
    /// Whether an invoice came from a workbook rather than from the entry screen. The number is
    /// the only thing that says so, and the two series can never overlap — which is what lets a
    /// list of both be split without a second query.
    /// </summary>
    public static bool IsImported(string? invoiceNo) =>
        invoiceNo?.StartsWith(ImportedPrefix, StringComparison.Ordinal) == true;

    /// Unpaged, this silently missed 366 of 1366 imported invoices: a re-import then deleted 1000,
    /// inserted 1366, and left the remainder behind as duplicate MIG- numbers.
    public static async Task<List<long>> ImportedInvoiceIdsAsync() =>
        (await AllPagesAsync(() => Db.Client.From<ImportedInvoice>()
            .Filter("invoice_no", Operator.Like, ImportedPrefix + "%")
            .Select(InvoiceIdColumn)
            .Order(InvoiceIdColumn, Ordering.Ascending)))
        .Select(i => i.InvoiceId).ToList();

    /// <summary>
    /// Clears a previous import: receipts, then lines, then the invoices themselves — children
    /// first, so a failure part-way can never orphan a line against a deleted invoice.
    /// Ids are deleted in batches because they travel in the URL.
    /// </summary>
    /// <summary>What replace_imported_sales wrote, straight from its jsonb result.</summary>
    /// <param name="StockLines">
    /// 0049. How many imported lines took carats OUT of stock. Every line that sold anything does,
    /// so this normally matches the line count — a line reading zero sold is the only one that
    /// does not.
    /// </param>
    /// <param name="StockCt">
    /// 0050. The WEIGHT those lines took out, summed off the movements themselves rather than off
    /// the lines — the movements are what actually left stock.
    /// </param>
    /// <param name="ShortCt">
    /// 0051. The carats the sheet wanted and the shelf did not have. An import empties a bucket but
    /// never overdraws it, so this is the difference between what the lines say and what left —
    /// zero on a sheet the stock could cover, which is the normal case.
    /// </param>
    /// <param name="ShortBuckets">0051. How many grade × size buckets ran out.</param>
    public sealed record SalesImportOutcome(int Deleted, int Invoices, int Lines, int Receipts,
                                            int StockLines = 0, decimal StockCt = 0m,
                                            decimal ShortCt = 0m, int ShortBuckets = 0);

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
                                      Int(outcome, "lines"), Int(outcome, "receipts"),
                                      Int(outcome, "stock_lines"), Num(outcome, "stock_ct"),
                                      Num(outcome, "short_ct"), Int(outcome, "short_buckets"));
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
        ["buyer", "price_list", "receipt", "sales_invoice", "sales_line", "stock_movement",
         // Since 0045. A sales entry takes carats out of the available position the moment a line
         // is typed, and until now that was the one act on a screen that left no trace of who.
         "stock_reservation"];

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

    /// <summary>
    /// Corrects a POSTED invoice: 0040 replaces its lines and rewrites the stock movements it
    /// wrote, in one transaction, keeping the invoice number and the POSTED status.
    ///
    /// Separate from SaveDraftAsync on purpose. That one filters on status = DRAFT and treats
    /// zero rows as the refusal, which is exactly right for a draft and exactly wrong here -- a
    /// posted invoice has movements to give back first, and doing it client-side would mean two
    /// round trips with the stock returned but the lines not yet rewritten if the second failed.
    ///
    /// The reason is required by the function, not merely by the screen: an unexplained edit of a
    /// document the office has already acted on is worse than no edit.
    /// </summary>
    public static async Task<WriteResult> EditPostedAsync(DraftInvoice d, string reason)
    {
        if (d.InvoiceId is not { } id) return new WriteResult("This invoice has never been saved.");
        if (string.IsNullOrWhiteSpace(reason)) return new WriteResult("A reason for the update is required.");

        try
        {
            var res = await Db.Client.Rpc("edit_posted_invoice", new Dictionary<string, object?>
            {
                ["p_invoice_id"]   = id,
                ["p_invoice_date"] = D(d.InvoiceDate),
                ["p_buyer_id"]     = d.BuyerId,
                ["p_broker_id"]    = d.BrokerId,
                ["p_broker_pct"]   = d.BrokerPct,
                ["p_terms_days"]   = d.TermsDays,
                ["p_doc_type"]     = d.DocType,
                ["p_currency_id"]  = d.CurrencyId,
                ["p_reason"]       = reason.Trim(),
                ["p_lines"] = d.Lines.Select(l => new Dictionary<string, object?>
                {
                    ["grade_id"]        = l.GradeId,
                    ["size_id"]         = l.SizeId,
                    ["gross_weight_ct"] = l.GrossWeightCt,
                    ["selection_ct"]    = l.SelectionCt,
                    ["price_per_ct"]    = l.PricePerCt,
                    // A zero rate would silently zero the whole invoice, the same trap SaveDraftAsync guards.
                    ["ex_rate"]         = l.ExRate == 0 ? 1 : l.ExRate,
                    ["less1_pct"]       = l.Less1Pct,
                    ["less2_pct"]       = l.Less2Pct,
                    ["remark"]          = l.Remark,
                }).ToList(),
            });
            return Outcome(res.Content);
        }
        catch (Exception e) { return new WriteResult(Err(e)); }
    }

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
                    .Filter(InvoiceIdColumn, Operator.Equals, id)
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

                await Db.Client.From<SalesLine>().Filter(InvoiceIdColumn, Operator.Equals, id).Delete();
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

    private const string NotEnoughStock = "Not enough stock for this invoice.";

    public static async Task<PostOutcome> PostAsync(long invoiceId, bool over = false)
    {
        try
        {
            var res = await Db.Client.Rpc("post_invoice",
                new Dictionary<string, object?> { ["p_invoice_id"] = invoiceId, ["p_override"] = over });

            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(res.Content) ? "{}" : res.Content);
            var r = doc.RootElement;

            var ok = r.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            var needs = r.TryGetProperty("needs_override", out var nEl) && nEl.ValueKind == JsonValueKind.True;
            List<Shortfall> shortfalls = r.TryGetProperty("shortfalls", out var sf) ? Shortfalls(sf) : [];

            return new PostOutcome(ok, needs, Str(r, "invoice_no"), PostMessage(ok, needs, r), shortfalls);
        }
        catch (Exception e)
        {
            string text = Err(e);
            return RaisedShortfalls(text) ?? new PostOutcome(false, false, null, text, []);
        }
    }

    /// Nothing to say when it posted. When stock is the reason, the shortfall wording — the grid
    /// underneath carries the detail. Otherwise whatever the database said.
    private static string? PostMessage(bool ok, bool needsOverride, JsonElement r)
    {
        if (ok) return null;
        if (needsOverride) return NotEnoughStock;
        return Str(r, "message") ?? "Posting refused.";
    }

    /// <summary>The shortfall array, from wherever it arrived. Empty for anything that is not one.</summary>
    private static List<Shortfall> Shortfalls(JsonElement array) =>
        array.ValueKind != JsonValueKind.Array
            ? []
            : [.. array.EnumerateArray().Select(x => new Shortfall(
                Str(x, "grade_code") ?? "", Str(x, "size_code") ?? "",
                Num(x, "balance_ct"), Num(x, "needed_ct")))];

    /// <summary>
    /// Under negative_stock = block the function RAISES rather than returning needs_override, so the
    /// shortfalls arrive inside the exception text as "Posting would take stock negative: [ ... ]".
    /// Left alone the user is shown raw jsonb; the figures are all there, so lift them out and
    /// report them the same way the warn path already does.
    ///
    /// Null when this was some other failure, which the caller reports as its own text.
    /// </summary>
    private static PostOutcome? RaisedShortfalls(string text)
    {
        int bracket = text.IndexOf('[');
        if (bracket < 0 || !text.Contains("stock negative", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(text[bracket..]);
            var shortfalls = Shortfalls(doc.RootElement);
            return shortfalls.Count > 0
                ? new PostOutcome(false, false, null, NotEnoughStock, shortfalls)
                : null;
        }
        catch (JsonException) { return null; }      // fall back to the raw text
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

    /// <param name="pricePerCt">
    /// What the added carats are worth. Only read when the weight is POSITIVE, because
    /// v_stock_position averages cost over inward movements only — a rate on a removal would be
    /// stored and never looked at.
    ///
    /// Supplying it is not optional in spirit. Adding carats at no stated rate is what drove
    /// NO 1 BB × 14+ from 58,000 to 16,153 a carat, and post_invoice stamps sales_line.cost_per_ct
    /// from that same average, so the damage does not stop at one report. Null still goes through
    /// the old function, which is how the Intake page's own adjustment has always behaved; 0028
    /// refuses it on the new one.
    /// </param>
    public static async Task<WriteResult> AdjustAsync(long gradeId, long sizeId, decimal signedWeightCt, string reason,
        Guid? clientRef = null, decimal? pricePerCt = null)
    {
        // Returned, not thrown: every sibling reports failure this way, and callers are async void
        // click handlers where an escaping exception takes the whole app down.
        if (string.IsNullOrWhiteSpace(reason)) return new WriteResult("An adjustment reason is required.");
        try
        {
            var args = new Dictionary<string, object?>
            {
                ["p_grade_id"] = gradeId,
                ["p_size_id"] = sizeId,
                ["p_weight_ct"] = signedWeightCt,
                ["p_reason"] = reason,
                ["p_date"] = D(Today),
                ["p_client_ref"] = (clientRef ?? Guid.NewGuid()).ToString()
            };

            Supabase.Postgrest.Responses.BaseResponse res;
            if (pricePerCt is null)
            {
                res = await Db.Client.Rpc("adjust_stock", args);
            }
            else
            {
                args["p_price_per_ct"] = pricePerCt;
                // Falls back for the same reason the stock import does: the app may be newer than
                // the database. Without 0028 the rate cannot be recorded, and an adjustment that
                // silently drops it is the bug this argument exists to fix — so it says so.
                try
                {
                    res = await Db.Client.Rpc("adjust_stock_at_cost", args);
                }
                catch (Exception e) when (IsMissingFunction(e))
                {
                    return new WriteResult(
                        "This database cannot record the rate on an adjustment yet, and recording "
                        + "the weight without it would wreck this bucket's average cost. Apply "
                        + "migration 0028 and try again.");
                }
            }

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
    /// <summary>
    /// A cheap fingerprint of the imported stock position: how many movements the last import left
    /// and the highest id among them. Both change whenever anyone re-imports, because a replace
    /// deletes and re-inserts.
    ///
    /// This is what makes an offline import safe to replay. A queued replace is not an append — it
    /// deletes everything the previous import wrote — so replaying one blind, hours later, would
    /// silently revert a colleague's newer import. The queue therefore records the fingerprint it
    /// expected the server to have, and refuses to auto-apply if the server has moved since.
    ///
    /// Null when the read fails, which the caller must treat as "cannot prove it is safe" rather
    /// than as "nothing has changed".
    /// </summary>
    public static async Task<string?> ImportedStockFingerprintAsync()
    {
        try
        {
            var ids = await ImportedStockIdsAsync();
            return $"{ids.Count}:{(ids.Count == 0 ? 0 : ids.Max())}";
        }
        catch (Exception) { return null; }
    }

    public static async Task<List<long>> ImportedStockIdsAsync() =>
        (await AllPagesAsync(() => Db.Client.From<StockMovement>()
            .Filter("ref_type", Operator.Equals, StockImportRef)
            .Select("movement_id,ref_id")
            .Order(MovementIdColumn, Ordering.Ascending)))
        // The "!" stays: HasValue is tested in a different lambda, which flow analysis cannot see
        // through. Dropping it buys a CS8629 in exchange for nothing.
        .Where(m => m.RefId.HasValue).Select(m => m.RefId!.Value).Distinct().ToList();

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
    /// <summary>
    /// The arguments replace_imported_stock takes. Shared so the online call and the offline queue
    /// send byte-identical bodies — a queued import that differed from the one the user confirmed
    /// would be a different import, arriving under the same name.
    /// </summary>
    private static Dictionary<string, object?> StockImportArgs(
        IReadOnlyList<StockRow> rows, DateOnly asAt,
        IReadOnlyDictionary<string, long> gradeIds, IReadOnlyDictionary<string, long> sizeIds) =>
        new()
        {
            ["p_as_at"] = asAt.ToString("yyyy-MM-dd"),
            ["p_rows"] = rows.Select(r => new Dictionary<string, object?>
            {
                ["grade_id"] = gradeIds[r.GradeCode],
                ["size_id"] = sizeIds[r.SizeCode],
                ["weight_ct"] = r.WeightCt,
                ["price_per_ct"] = r.PricePerCt,
            }).ToList(),
        };

    /// <summary>The same body as a JSON string, for the offline queue.</summary>
    public static string StockImportPayload(
        IReadOnlyList<StockRow> rows, DateOnly asAt,
        IReadOnlyDictionary<string, long> gradeIds, IReadOnlyDictionary<string, long> sizeIds) =>
        System.Text.Json.JsonSerializer.Serialize(StockImportArgs(rows, asAt, gradeIds, sizeIds));

    /// <summary>
    /// Every stock import that is still standing, newest first. Read before an import so the choice
    /// between replacing and appending is made against what is actually there.
    ///
    /// NULL when the list could not be read at all — which is what happens before 0027 is applied,
    /// because the view does not exist yet. Deliberately not an empty list: "there are no imports"
    /// and "I cannot see the imports" are opposite answers, and the caller is about to offer to
    /// DELETE whatever is there. Returning [] told the user nothing had been imported while 2,950
    /// carats sat in the ledger.
    /// </summary>
    public static async Task<List<VStockImportBatch>?> StockImportBatchesAsync()
    {
        try
        {
            return (await Db.Client.From<VStockImportBatch>()
                .Order("last_intake_id", Ordering.Descending)
                .Limit(50).Get()).Models;
        }
        catch (Exception) { return null; }
    }

    /// <summary>How to land a sheet, as opposed to what is on it.</summary>
    /// <param name="Replace">
    /// True replaces every previously imported holding, which is what a full stock count means.
    /// False adds this sheet on top of what is already there — two parcel lots counted separately,
    /// both standing. The database does the choosing inside one transaction either way.
    /// </param>
    /// <param name="Batch">Stamped on the parcels so this import can be told from the last one.</param>
    /// <param name="Source">Which reader produced the rows: "excel" or "pdf".</param>
    public sealed record StockImportMode(bool Replace = true, Guid? Batch = null, string Source = "excel");

    public static async Task<StockImportResult> ImportStockAsync(
        IReadOnlyList<StockRow> rows, DateOnly asAt,
        IReadOnlyDictionary<string, long> gradeIds, IReadOnlyDictionary<string, long> sizeIds,
        IProgress<ImportProgress>? progress = null, StockImportMode? mode = null)
    {
        mode ??= new StockImportMode();
        bool replace = mode.Replace;

        progress?.Report(new ImportProgress(
            (replace ? "Replacing the stock position… " : "Adding to the stock position… ")
            + $"{rows.Count:N0} holding(s)", 0, rows.Count));

        var args = StockImportArgs(rows, asAt, gradeIds, sizeIds);
        args["p_replace"] = replace;
        args["p_batch"] = (mode.Batch ?? Guid.NewGuid()).ToString();
        args["p_source"] = mode.Source;

        // import_stock arrives with 0027. Until that is applied the database still only knows
        // replace_imported_stock, and an app that has been updated first must not lose the ability
        // to import a workbook while it waits — a replace means exactly the same thing to both
        // functions, so it falls back rather than failing. An APPEND has no older equivalent and
        // says so plainly instead of quietly replacing, which would delete the position it was
        // asked to add to.
        Supabase.Postgrest.Responses.BaseResponse res;
        try
        {
            res = await Db.Client.Rpc("import_stock", args);
        }
        catch (Exception e) when (replace && IsMissingFunction(e))
        {
            res = await Db.Client.Rpc("replace_imported_stock",
                                      StockImportArgs(rows, asAt, gradeIds, sizeIds));
        }

        var outcome = Json(res.Content);
        int written = Int(outcome, "written");
        int deleted = Int(outcome, "deleted");

        // The function returns what it wrote. A short count means rows were dropped silently, which
        // an import must never do quietly — and because it is one transaction, saying so is the
        // only thing left to do; nothing partial can be sitting in the database.
        if (written != rows.Count)
            throw new InvalidOperationException(
                $"Sent {rows.Count} holding(s) but the database wrote {written}.");

        progress?.Report(new ImportProgress(replace ? "Stock replaced" : "Stock added",
                                            rows.Count, rows.Count));

        return new StockImportResult(written, deleted,
                                     rows.Sum(r => r.WeightCt),
                                     rows.Sum(r => r.WeightCt * r.PricePerCt),
                                     Guid.TryParse(Str(outcome, "batch"), out var id) ? id : null);
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
    /// Adds a grade the catalogue does not have, with the size pairings 0018's rule gives it (0030).
    ///
    /// Idempotent, and alias-aware: a code that is already a grade — or already an ALIAS of one —
    /// returns that grade rather than creating a second row for the same goods.
    ///
    /// Deliberately never called by the importer on its own. A typo on a printed sheet would
    /// otherwise become a permanent grade holding real carats. The user is shown the names first.
    /// </summary>
    public static async Task<WriteResult> AddGradeAsync(string code, string? displayName = null)
    {
        try
        {
            var res = await Db.Client.Rpc("add_grade", new Dictionary<string, object?>
            {
                ["p_code"] = code,
                ["p_display_name"] = displayName,
            });
            return Outcome(res.Content);
        }
        catch (Exception e) { return new WriteResult(Err(e)); }
    }

    /// <summary>
    /// Adds a sieve size named on a stock sheet, with the grade pairings 0018's rule gives it (0034).
    ///
    /// Idempotent and NOTATION-AWARE: "6.5+" returns the existing "+6.5" rather than making a twin.
    /// That matters more here than it did for grades — these sheets write the same bucket four ways,
    /// and two rows for one sieve would split the position between them with nothing to flag it.
    ///
    /// Never called on its own initiative. The user is shown the heading and the carats first.
    /// </summary>
    public static async Task<WriteResult> AddSizeAsync(string code)
    {
        try
        {
            var res = await Db.Client.Rpc("add_size", new Dictionary<string, object?>
            {
                ["p_code"] = code,
            });
            return Outcome(res.Content);
        }
        catch (Exception e) { return new WriteResult(Err(e)); }
    }

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
            await Db.Client.From<Grade>().Filter(GradeIdColumn, Operator.Equals, gradeId)
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
                .Filter(GradeIdColumn, Operator.Equals, gradeId)
                .Filter(SizeIdColumn, Operator.Equals, sizeId)
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

    // The ValueKind guard is not decoration: TryGetProperty THROWS on anything that is not an
    // object, and Json() returns a default JsonElement when an RPC answers with something
    // unparseable. Reading a field off that reply must come back null, not take down the call.
    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

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
            using var doc = JsonDocument.Parse(json);
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
            using var doc = JsonDocument.Parse(json);
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

    /// <summary>
    /// PostgREST could not find the function that was called — the database is behind the app.
    ///
    /// Read off the error's own `code` field rather than by matching words in the message. Err()
    /// deliberately drops the code when it composes something readable for a user, so testing its
    /// output for "PGRST202" never matched and the fallback it guarded never ran.
    /// </summary>
    static bool IsMissingFunction(Exception e)
    {
        if (e is not PostgrestException { Content: { } content }) return false;
        try
        {
            using var doc = JsonDocument.Parse(content);
            return Str(doc.RootElement, "code") == "PGRST202";
        }
        catch (JsonException) { return false; }
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
