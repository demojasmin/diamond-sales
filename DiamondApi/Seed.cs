using Microsoft.EntityFrameworkCore;

namespace DiamondApi;

/// Seed lists are the ones verified in the real workbook — docs/08 §2. Nothing here is invented.
public static class Seed
{
    // (code, display, extra aliases). Sheet names carry trailing spaces; canonical codes never do (DQ-5).
    private static readonly (string Code, string Display, string[] Aliases)[] GradeSeed =
    [
        ("NO_1",    "NO 1",    ["1 ", "1", "NO 1"]),
        ("NO_1_BB", "NO 1 BB", ["1 BB", "1BB", "NO 1 BB"]),
        ("NO_II",   "NO II",   ["II", "NO II"]),
        ("EX_1",    "EX 1",    ["EX 1", "EX1", "Ex1"]),
        ("NO_2",    "NO 2",    ["NO-2 ", "NO-2", "2"]),
        ("NO_DX",   "NO DX",   ["NO-DX", "DX"]),
        ("NO_3",    "NO 3",    ["NO-3", "3"]),
        ("NO_4",    "NO 4",    ["NO-4", "4"]),
        ("NO_5",    "NO 5",    ["NO-5", "5"]),
        ("NO_6",    "NO 6",    ["NO-6", "6"]),
        ("NO_7",    "NO 7",    ["NO-7", "7"]),
        ("TOP_COL", "TOP-COL", ["TOP-COL"]),
        ("COL",     "COL",     ["COL ", "COL"]),
        ("OW",      "OW",      ["OW"]),
        ("LC_1",    "LC-1",    ["LC-1"]),
        ("LC_2",    "LC-2",    ["LC-2"]),
        ("LC_3",    "LC-3",    ["LC-3"]),
        ("GH",      "GH",      ["GH"]),
        ("LB_1",    "LB-1",    ["LB-1"]),
        ("LB_2",    "LB-2",    ["LB-2"]),
        ("PLUS_14", "+14",     ["+14", "14"]),
        ("EXTRA",   "EXTRA",   ["EXTRA"]),
    ];

    // docs/04 §4.1 — four notations for four sizes. The `0.2` cell is left OUT deliberately:
    // MDM-004 AC 3 says it is flagged for manual mapping, never silently coerced.
    private static readonly (string Code, int Sort, string[] Aliases)[] SizeSeed =
    [
        ("-2",   1, ["-2", "2-"]),
        ("-6.5", 2, ["-6.5", "6.5-", "-6.50", ",-6.5"]),
        ("+6.5", 3, ["+6.5", "6.5+", "+6.50", ",+6.5"]),
        ("+11",  4, ["+11", "11+", "+11.00", ",+11"]),
    ];

    /// docs/04 §3.4 — only these two grades use the smallest bucket.
    private static readonly string[] FourSizeGrades = ["NO_1", "NO_1_BB"];

    public static readonly (string Key, string Value)[] SettingSeed =
    [
        ("base_currency", "INR"),
        ("money_dp", "2"),
        ("carat_dp", "4"),
        ("rounding", "HALF_UP"),
        ("negative_stock_policy", "WARN"),          // Q10 assumption
        ("session_timeout_min", "60"),
        ("lockout_attempts", "5"),
        ("auto_reject_on_post", "false"),
        ("manager_sees_margin", "false"),
        ("settlement_write_off_threshold", "1.00"), // docs/03 V-4
        ("low_stock_threshold_ct", "5.00"),         // W15 alerts strip
    ];

    public static void Run(DiamondDb db)
    {
        db.Database.EnsureCreated();

        if (!db.Grades.Any())
        {
            int order = 1;
            var seen = new HashSet<string>();       // an alias belongs to exactly one grade — that is the DQ-4 fix
            foreach (var (code, display, aliases) in GradeSeed)
            {
                var grade = new Grade { Code = code, DisplayName = display, SortOrder = order++ };
                db.Grades.Add(grade);
                foreach (var alias in aliases.Select(Normalise).Where(seen.Add))
                    db.GradeAliases.Add(new GradeAlias { Alias = alias, GradeId = grade.GradeId });
            }
        }

        if (!db.Sizes.Any())
        {
            var seen = new HashSet<string>();
            foreach (var (code, sort, aliases) in SizeSeed)
            {
                var size = new SizeBucket { Code = code, SortOrder = sort };   // mm ranges unknown — Q6
                db.Sizes.Add(size);
                foreach (var alias in aliases.Select(Normalise).Where(seen.Add))
                    db.SizeAliases.Add(new SizeAlias { Alias = alias, SizeId = size.SizeId });
            }
        }

        db.SaveChanges();

        if (!db.GradeSizes.Any())
        {
            var sizes = db.Sizes.ToDictionary(s => s.Code);
            foreach (var grade in db.Grades.ToList())
            {
                string[] codes = FourSizeGrades.Contains(grade.Code)
                    ? ["-2", "-6.5", "+6.5", "+11"]
                    : ["-6.5", "+6.5", "+11"];
                foreach (var code in codes)
                    db.GradeSizes.Add(new GradeSize { GradeId = grade.GradeId, SizeId = sizes[code].SizeId });
            }
        }

        foreach (var (key, value) in SettingSeed)
            if (!db.Settings.Any(s => s.Key == key))
                db.Settings.Add(new AppSetting { Key = key, Value = value });

        if (!db.Users.Any())
            db.Users.Add(new AppUser
            {
                Username = "owner",
                DisplayName = "Owner",
                Role = Roles.Owner,
                // ponytail: a known first-run credential, printed at startup. Replace with a
                // forced password change (AUTH-002) before this is reachable from outside the office.
                PasswordHash = Auth.Hash("owner"),
            });

        // Buyers and brokers seen in the sample sales file — real data, useful the moment you open the app.
        foreach (var name in new[] { "ABC Company", "Z K ENTERPRISE", "QUEST DIAMOND" })
            if (!db.Buyers.Any(b => b.Name == name))
                db.Buyers.Add(new Buyer { Name = name, DefaultTermsDays = name == "ABC Company" ? 45 : 0 });

        foreach (var name in new[] { "JITESH SHAH", "PARESH MEHTA", "RAJU PATEL" })
            if (!db.Brokers.Any(b => b.Name == name))
                db.Brokers.Add(new Broker { Name = name, DefaultBrokerPct = 1m });

        db.SaveChanges();
    }

    /// Alias matching is trim + collapse whitespace + upper — docs/08 §2.2. `1 ` and `1` are the same thing.
    public static string Normalise(string raw) =>
        string.Join(' ', raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
              .ToUpperInvariant();
}
