using System.Globalization;

namespace DiamondDesktop.Data;

/// <summary>
/// The app_config values the client acts on, read once at sign-in and refreshed whenever the
/// Settings page saves.
///
/// Three of these settings used to save and do nothing: the Settings page wrote them, and no code
/// anywhere read them back. money_precision lost to a hardcoded "N2" in every formatter,
/// alert_low_stock_ct had no consumer at all, and session_timeout_min was read only by
/// DiamondApi/Auth.cs — a service this desktop never calls. A control that writes a value nothing
/// obeys is worse than no control: it reports a policy that is not in force.
///
/// Deliberately NOT the rounding policy. Calc.MoneyDp stays 2 because that is the boundary at
/// which amounts are computed and stored (BR-ROUND-6) — changing it would change what is written
/// to sales_line, not merely how it reads. money_precision is a display setting and only display
/// asks for it.
/// </summary>
public static class Policy
{
    /// Decimal places for money on screen. app_config.money_precision, 0-2.
    public static int MoneyPrecision { get; private set; } = 2;

    /// A bucket at or below this many carats — but still above zero — is reported as low.
    /// app_config.alert_low_stock_ct.
    public static decimal LowStockCt { get; private set; }

    /// Minutes of no keyboard or mouse activity before the session is ended.
    /// app_config.session_timeout_min, 1-1440.
    public static int SessionTimeoutMin { get; private set; } = 60;

    /// Consecutive failed sign-ins before an account is locked. app_config.max_login_attempts.
    public static int MaxLoginAttempts { get; private set; } = 5;

    /// Raised after Apply, so anything already on screen can re-render against the new values.
    public static event Action? Changed;

    public static async Task LoadAsync()
    {
        try { Apply(await Repo.ConfigAsync()); }
        catch { /* the defaults above are the shipped policy; a config read must not block sign-in */ }
    }

    public static void Apply(IReadOnlyDictionary<string, string> config)
    {
        MoneyPrecision = Clamp(Int(config, "money_precision", MoneyPrecision), 0, 2);
        LowStockCt = Math.Max(0, Dec(config, "alert_low_stock_ct", LowStockCt));
        SessionTimeoutMin = Clamp(Int(config, "session_timeout_min", SessionTimeoutMin), 1, 1440);

        // One key, not two. DiamondApi read "lockout_attempts" while the Settings page wrote
        // "max_login_attempts", so the number on screen could never reach the code enforcing it.
        // The older key is still accepted as a fallback for a database seeded before the rename.
        MaxLoginAttempts = Clamp(Int(config, "max_login_attempts",
                                     Int(config, "lockout_attempts", MaxLoginAttempts)), 1, 100);

        Changed?.Invoke();
    }

    /// <summary>Money as it should read on screen — the one place that decides the decimals.</summary>
    public static string Format(decimal value) => value.ToString("N" + MoneyPrecision);

    private static int Clamp(int v, int lo, int hi) => Math.Min(hi, Math.Max(lo, v));

    private static int Int(IReadOnlyDictionary<string, string> c, string key, int fallback) =>
        c.TryGetValue(key, out string? s) && int.TryParse(s, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int v) ? v : fallback;

    private static decimal Dec(IReadOnlyDictionary<string, string> c, string key, decimal fallback) =>
        c.TryGetValue(key, out string? s) && decimal.TryParse(s, NumberStyles.Float,
            CultureInfo.InvariantCulture, out decimal v) ? v : fallback;
}
