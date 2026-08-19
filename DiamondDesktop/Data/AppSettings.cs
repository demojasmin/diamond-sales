using System.IO;
using Newtonsoft.Json;

namespace DiamondDesktop.Data;

/// <summary>
/// One Supabase project the app can sign in to.
///
/// The anon key belongs here, not in a secret store: it is publishable by design and every request
/// still rides the signed-in user's token, with RLS as the actual boundary. What does NOT belong
/// here is the service_role key or the database password -- neither has ever been in this app, and
/// neither should be, because anything shipped to a desktop is readable by whoever holds the file.
/// </summary>
public sealed class Workspace
{
    /// Shown to the user, and the name written down as "the one last used". Not an identifier.
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public string AnonKey { get; init; } = "";

    /// <summary>
    /// The project reference out of the URL -- "abcd1234" from https://abcd1234.supabase.co.
    ///
    /// This is what keeps the workspaces apart on disk. The saved session and the offline queue are
    /// both named after it, so a token minted by one project can never be handed to another and a
    /// write queued against one project's grade ids can never replay into another's.
    /// </summary>
    public string Ref =>
        Uri.TryCreate(Url, UriKind.Absolute, out var u) && u.Host.Split('.').FirstOrDefault() is { Length: > 0 } first
            ? first
            : Name.ToLowerInvariant();

    public bool IsUsable => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(AnonKey);
}

/// <summary>
/// Named AppSettings, not AppConfig: AppConfig is already the app_config table model.
/// Which projects the app can reach, read from a file beside the executable instead of compiled in.
///
/// Two shapes are accepted. A "Workspaces" array is the current one. A bare Url/AnonKey pair is what
/// installs shipped before this existed, and is read as a single workspace so an older config file
/// on a desk still works untouched.
/// </summary>
public sealed class AppSettings
{
    // Legacy single-project shape. Kept readable, never written.
    public string Url { get; init; } = "";
    public string AnonKey { get; init; } = "";

    public List<Workspace>? Workspaces { get; init; }

    public const string FileName = "appsettings.json";

    /// <summary>
    /// Where the app points when appsettings.json is missing or unusable: NOWHERE.
    ///
    /// This used to name a real project -- the first client's -- so that an install with no config
    /// file still started and still worked. That was defensible with one client. With two it is a
    /// cross-client data leak waiting for a typo: a malformed file on the second client's desk
    /// would silently connect their app to the FIRST client's database, with no error, no dialog,
    /// and every screen working normally. Nobody would find out until the figures disagreed.
    ///
    /// ".invalid" is reserved by RFC 2606 and never resolves, so the client still constructs (a
    /// throw here would kill the process before any window exists) and every call fails as a
    /// connection error, while Problem below says plainly what is actually wrong.
    /// </summary>
    private static readonly Workspace NotConfigured = new()
    {
        Name = "Not configured",
        Url = "https://not-configured.invalid",
        AnonKey = "not-configured",
    };

    private static AppSettings? _loaded;
    private static IReadOnlyList<Workspace>? _workspaces;

    public static AppSettings Current => _loaded ??= Load();

    /// <summary>
    /// Every project the app may sign in to, in the order they are tried.
    ///
    /// Never empty: a bad config yields the single unreachable placeholder above rather than an
    /// empty list, because every caller would otherwise need a "what if there are none" branch for
    /// a state that only means "the file is wrong", which Problem already says.
    /// </summary>
    public static IReadOnlyList<Workspace> All => _workspaces ??= Resolve(Current);

    /// Set when the file was present but unusable, so the app can say so once it has a window.
    public static string? Problem { get; private set; }

    private static IReadOnlyList<Workspace> Resolve(AppSettings s)
    {
        var listed = (s.Workspaces ?? []).Where(w => w.IsUsable).ToList();
        if (listed.Count > 0) return listed;

        if (!string.IsNullOrWhiteSpace(s.Url) && !string.IsNullOrWhiteSpace(s.AnonKey))
            return [new Workspace { Name = "Default", Url = s.Url, AnonKey = s.AnonKey }];

        return [NotConfigured];
    }

    private static AppSettings Load()
    {
        // Every failure below says WHICH FILE and WHERE. The one thing this must never do is
        // carry on quietly against some other database.
        const string Fix = "The app cannot reach any database until it is fixed.";

        string path = Path.Combine(AppContext.BaseDirectory, FileName);
        if (!File.Exists(path))
        {
            Problem = $"{FileName} was not found beside the application ({path}). {Fix}";
            return new AppSettings();
        }

        try
        {
            var read = JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(path));
            if (read is null || Resolve(read)[0] == NotConfigured)
            {
                Problem = $"{FileName} lists no usable workspace -- each one needs a Url and an AnonKey. {Fix}";
                return new AppSettings();
            }
            return read;
        }
        catch (Exception e)
        {
            Problem = $"{FileName} could not be read ({e.Message}). {Fix}";
            return new AppSettings();
        }
    }
}
