using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace DiamondDesktop;

// Response shapes from docs/07. Only the fields a screen actually shows.
public sealed record GradeDto(Guid GradeId, string Code, string DisplayName, int SortOrder, bool Active,
                              List<string> Aliases, List<SizeRefDto> Sizes);
public sealed record SizeRefDto(Guid SizeId, string Code, int SortOrder);
public sealed record SizeDto(Guid SizeId, string Code, int SortOrder);
public sealed record BuyerDto(Guid BuyerId, string Name, int DefaultTermsDays, bool Active);
public sealed record BrokerDto(Guid BrokerId, string Name, decimal DefaultBrokerPct, bool Active);
public sealed record UserDto(Guid UserId, string Username, string DisplayName, string Role, bool Active);
public sealed record InvoiceRow(Guid InvoiceId, string? InvoiceNo, DateOnly InvoiceDate, string Status,
                                string? Buyer, decimal Total, decimal Outstanding, DateOnly DueDate);
public sealed record ReceivableRow(Guid InvoiceId, string? InvoiceNo, string Buyer, DateOnly InvoiceDate,
                                   DateOnly DueDate, decimal Outstanding, int DaysOverdue, bool IsOverdue, string Bucket);
public sealed record ReceivablesDto(decimal Total, Dictionary<string, decimal> Buckets, List<ReceivableRow> Invoices);
public sealed record StockRowDto(Guid GradeId, string GradeCode, Guid SizeId, string SizeCode,
                                 decimal BalanceCt, decimal AvgPricePerCt, decimal Value);
public sealed record StockDto(List<StockRowDto> Rows, decimal TotalCarats, decimal TotalValue);
public sealed record MovementDto(Guid MovementId, DateOnly MovementDate, string MovementType, decimal WeightCt,
                                 decimal PricePerCt, string RefType, Guid RefId, string? Reason);
public sealed record AuditRow(Guid AuditId, string EntityType, Guid EntityId, string Action,
                              string? Before, string? After, Guid? UserId, DateTime OccurredAt);
public sealed record SettingRow(string Key, string Value);
public sealed record DashboardDto(decimal TotalSales, decimal CaratsSold, decimal BlendedRate, decimal Outstanding,
                                  decimal InventoryValue, decimal InventoryCarats,
                                  decimal BrokerCost, int InvoiceCount, decimal PriorSales);
public sealed record BarDto(string Label, decimal Value, string? Secondary, decimal? Delta, Guid? DrillId);
public sealed record MarginDto(decimal Total, List<BarDto> Rows, string CostBasis);
public sealed record LowStockDto(string GradeCode, string SizeCode, decimal BalanceCt);
public sealed record OverdueDto(string? InvoiceNo, DateOnly Due, decimal Outstanding);
public sealed record AlertsDto(int OverdueCount, decimal OverdueValue, int LowStockCount, int NegativeCount,
                               List<LowStockDto> LowStock, List<OverdueDto> Overdue);
// RPT-002 needs the whole invoice, not the list row.
public sealed record InvoiceLineDetail(Guid LineId, Guid GradeId, Guid SizeId, decimal GrossWeightCt,
                                       decimal SelectionCt, decimal RejectionCt, decimal PricePerCt,
                                       decimal Less1Pct, decimal Less2Pct, decimal Amount, string? Remark)
{
    public string GradeCode { get; set; } = "";
    public string SizeCode { get; set; } = "";
}

public sealed record InvoiceDetail(Guid InvoiceId, string? InvoiceNo, DateOnly InvoiceDate, string? Buyer,
                                   decimal BrokerPct, int TermsDays, string DocType, string Status,
                                   List<InvoiceLineDetail> Lines, decimal AmountTotal, decimal Outstanding,
                                   DateOnly DueDate);

public sealed record PriceRow(Guid PriceId, Guid GradeId, Guid SizeId, string Context,
                              decimal PricePerCt, DateOnly EffectiveFrom, DateOnly? EffectiveTo);

public sealed record ChangeRequestRow(Guid RequestId, Guid InvoiceId, Guid RequestedBy, DateTime RequestedAt,
                                      string Proposed, string Status, string? DecisionNote);

public sealed record BackupRow(string File, long SizeKb, DateTime CreatedUtc);

public sealed record ApiError(string Code, string Message);
public sealed record PostFailure(ApiError Error, List<string>? Warnings, string? OverrideToken);

/// The only thing in the desktop app that talks to the server. Every screen goes through it.
public sealed class Api
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public Api(string baseUrl) => _http = new HttpClient { BaseAddress = new Uri(baseUrl) };

    public UserDto? User { get; private set; }
    public bool IsManager => User?.Role is "MANAGER" or "OWNER";
    public bool IsOwner => User?.Role is "OWNER";

    public async Task<string?> LoginAsync(string username, string password)
    {
        var response = await _http.PostAsJsonAsync("/api/v1/auth/login", new { username, password }, Json);
        if (!response.IsSuccessStatusCode)
            return (await Read<Dictionary<string, ApiError>>(response))?["error"].Message ?? "Login failed";

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());
        User = body.GetProperty("user").Deserialize<UserDto>(Json);
        return null;
    }

    /// Set when a call fails, so a screen can say *why* instead of just showing nothing.
    public string? LastError { get; private set; }

    public async Task<T?> GetAsync<T>(string path)
    {
        try
        {
            var response = await _http.GetAsync(path);
            if (response.IsSuccessStatusCode) { LastError = null; return await Read<T>(response); }

            LastError = await ErrorText(response);
            return default;
        }
        catch (Exception ex)          // network down, server stopped, malformed payload
        {
            LastError = ex.Message;
            return default;
        }
    }

    /// Returns null on success, or the server's message. Every screen shows it verbatim — the
    /// server owns the wording of a business rule, not the client.
    public async Task<string?> PostAsync(string path, object? payload = null)
    {
        try
        {
            var response = await _http.PostAsJsonAsync(path, payload ?? new { }, Json);
            return response.IsSuccessStatusCode ? null : await ErrorText(response);
        }
        catch (Exception ex) { return LastError = ex.Message; }
    }

    /// One call a screen can make to prove the server is really there.
    public async Task<(bool Ok, string Message)> PingAsync()
    {
        var grades = await GetAsync<List<GradeDto>>("/api/v1/grades");
        var buyers = await GetAsync<List<BuyerDto>>("/api/v1/buyers");

        return grades is null || buyers is null
            ? (false, $"Offline — {LastError ?? "no response"}")
            : (true, $"Connected · {grades.Count} grades · {buyers.Count} buyers");
    }

    public async Task<string?> DeleteAsync(string path)
    {
        var response = await _http.DeleteAsync(path);
        return response.IsSuccessStatusCode ? null : await ErrorText(response);
    }

    public async Task<string?> PutAsync(string path, object payload)
    {
        var response = await _http.PutAsJsonAsync(path, payload, Json);
        return response.IsSuccessStatusCode ? null : await ErrorText(response);
    }

    /// Posting is the one call that can come back "confirm this" (docs/07 §4.4).
    public async Task<(bool Ok, string? Message, List<string> Warnings, string? OverrideToken)> PostInvoiceAsync(
        Guid invoiceId, string? overrideToken)
    {
        string path = $"/api/v1/invoices/{invoiceId}/post";
        if (overrideToken is not null) path += $"?overrideToken={Uri.EscapeDataString(overrideToken)}";

        var response = await _http.PostAsync(path, null);
        if (response.IsSuccessStatusCode) return (true, null, [], null);

        var failure = await Read<PostFailure>(response);
        return (false, failure?.Error.Message ?? "Post failed", failure?.Warnings ?? [], failure?.OverrideToken);
    }

    /// Loads the master data the sales screen needs, once, after login.
    public async Task LoadCatalogueAsync()
    {
        var grades = await GetAsync<List<GradeDto>>("/api/v1/grades") ?? [];
        var sizes = await GetAsync<List<SizeDto>>("/api/v1/sizes") ?? [];
        if (grades.Count == 0 || sizes.Count == 0) return;      // offline: keep the seed already loaded

        var buckets = sizes.Select(s => new SizeBucket(s.Code, s.SizeId)).ToList();
        var byCode = buckets.ToDictionary(b => b.Code);

        Catalogue.Load(
            grades.Select(g => new Grade(g.Code, g.DisplayName, g.GradeId)),
            buckets,
            grades.ToDictionary(g => g.Code,
                                g => (IReadOnlyList<SizeBucket>)g.Sizes.Select(s => byCode[s.Code]).ToList()));
    }

    private static async Task<T?> Read<T>(HttpResponseMessage response)
        => await response.Content.ReadFromJsonAsync<T>(Json);

    private static async Task<string> ErrorText(HttpResponseMessage response)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
            return body.TryGetProperty("error", out var error)
                ? $"{error.GetProperty("code").GetString()}: {error.GetProperty("message").GetString()}"
                : response.StatusCode.ToString();
        }
        catch { return response.StatusCode.ToString(); }
    }
}
