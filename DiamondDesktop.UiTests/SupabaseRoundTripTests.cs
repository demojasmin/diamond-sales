using System.Net.Http.Json;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace DiamondDesktop.UiTests;

/// <summary>
/// §9's real acceptance test: "an invoice created on the desktop appears on the Android app after
/// refresh, WITH THE SAME TOTAL TO THE PAISA."
///
/// The phone reads v_invoice, so reading v_invoice over REST is exactly what it would see. If the
/// desktop and the view ever disagree, something has broken the golden rule and this fails.
/// </summary>
public sealed class SupabaseRoundTripTests(AppFixture fx) : IClassFixture<AppFixture>
{
    private const string Url = "https://nzcvjaixgqoliyrotstz.supabase.co";
    private const string AnonKey = "sb_publishable_bkIjJlfcQZDrXD6-l7i1uQ_v6OLf9Un";

    private readonly SalesEntryPage p = new(fx);

    private static async Task<HttpClient> ApiAsync()
    {
        var http = new HttpClient { BaseAddress = new Uri(Url) };
        http.DefaultRequestHeaders.Add("apikey", AnonKey);

        var auth = await http.PostAsJsonAsync("/auth/v1/token?grant_type=password", new
        {
            email = Environment.GetEnvironmentVariable("SOLITAIRE_EMAIL") ?? "demojasmin89@gmail.com",
            password = Environment.GetEnvironmentVariable("SOLITAIRE_PASSWORD") ?? "Priya@Hexa@123",
        });
        string token = (await auth.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return http;
    }

    private static async Task<JsonElement[]> GetAsync(HttpClient http, string path) =>
        (await http.GetFromJsonAsync<JsonElement[]>($"/rest/v1/{path}"))!;

    [Fact]
    public async Task An_invoice_typed_on_the_desktop_reaches_supabase_with_the_same_total()
    {
        using var api = await ApiAsync();

        // Stock first, or posting hits the negative-stock warning and stops for a human.
        var grades = await GetAsync(api, "grade?select=grade_id&code=eq.NO%201");
        var sizes = await GetAsync(api, "size_bucket?select=size_id&code=eq.-6.5");
        long gradeId = grades[0].GetProperty("grade_id").GetInt64();
        long sizeId = sizes[0].GetProperty("size_id").GetInt64();

        await api.PostAsJsonAsync("/rest/v1/stock_movement", new
        {
            movement_date = DateTime.Today.ToString("yyyy-MM-dd"),
            grade_id = gradeId, size_id = sizeId,
            movement_type = "INTAKE", weight_ct = 50m, price_per_ct = 500m, ref_type = "uitest",
        });

        var before = await GetAsync(api, "v_invoice?select=invoice_id");

        // ---- everything below happens in the real app, through the real keyboard ----
        p.StartFreshInvoice().Buyer("QUEST DIAMOND")
         .FillLine("NO 1", "-6.5", weight: "10", selection: "6", price: "1000");

        // 6 x 1000, no discounts, no broker -> the desktop shows this from DiamondCalc while typing.
        Assert.Equal("6,000.00", p.Cell(SalesEntryPage.ColAmount));

        p.Click("SaveDraft");
        Assert.StartsWith("Draft saved", p.Status);

        p.Click("Post");
        Wait.UntilInputIsProcessed();
        p.DismissModal("OK");                       // "Posted" confirmation, if one appears

        // ---- and now: what would the phone see? ----
        var after = await GetAsync(api, "v_invoice?select=invoice_id,invoice_no,status,amount_total,carats_sold,outstanding&order=invoice_id.desc");
        Assert.True(after.Length == before.Length + 1, $"expected one new invoice, got {after.Length - before.Length}");

        var mine = after[0];
        Assert.Equal("POSTED", mine.GetProperty("status").GetString());

        // invoice_no is assigned by post_invoice(), never by the client (docs/03 §2.3).
        string? no = mine.GetProperty("invoice_no").GetString();
        Assert.Matches(@"^INV-\d{4}-\d{5}$", no ?? "");

        // THE test. Postgres recomputed this from the raw inputs; the desktop showed 6,000.00 from
        // DiamondCalc. If these ever differ, a client has invented its own definition of a rupee.
        Assert.Equal(6000.00m, mine.GetProperty("amount_total").GetDecimal());
        Assert.Equal(6.0000m, mine.GetProperty("carats_sold").GetDecimal());
        Assert.Equal(6000.00m, mine.GetProperty("outstanding").GetDecimal());

        // CALC-8: the sales ledger and the stock ledger agree (§9's other done-criterion).
        var bad = await GetAsync(api, "v_reconciliation?select=grade_code&reconciles=is.false");
        Assert.Empty(bad);

        // Tidy: cancel returns the carats, then neutralise the intake. The ledger is append-only,
        // so the rows stay - the balance is what has to come back to zero.
        long invoiceId = mine.GetProperty("invoice_id").GetInt64();
        await api.PostAsJsonAsync("/rest/v1/rpc/cancel_invoice",
            new { p_invoice_id = invoiceId, p_reason = "UI round-trip test" });
        await api.PostAsJsonAsync("/rest/v1/stock_movement", new
        {
            movement_date = DateTime.Today.ToString("yyyy-MM-dd"),
            grade_id = gradeId, size_id = sizeId,
            movement_type = "ADJUST", weight_ct = -50m, price_per_ct = 500m,
            ref_type = "uitest", reason = "Reverse the UI round-trip test intake",
        });
    }
}
