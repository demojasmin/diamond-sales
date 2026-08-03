using System.IO;
using Newtonsoft.Json;

namespace DiamondDesktop.Data;

/// <summary>
/// Named AppSettings, not AppConfig: AppConfig is already the app_config table model.
/// Where the app points and what it signs requests with, read from a file beside the executable
/// instead of compiled in. Pointing a build at another client's project is now editing a text file,
/// not editing two source files and rebuilding.
///
/// The anon key belongs here, not in a secret store: it is publishable by design and every request
/// still rides the signed-in user's token, with RLS as the actual boundary. What does NOT belong
/// here is the service_role key or the database password — neither has ever been in this app, and
/// neither should be, because anything shipped to a desktop is readable by whoever holds the file.
/// </summary>
public sealed class AppSettings
{
    public string Url { get; init; } = "";
    public string AnonKey { get; init; } = "";

    public const string FileName = "appsettings.json";

    /// The values the app shipped with before this file existed. Kept as the fallback so an
    /// existing install that has no appsettings.json next to it still starts and still reaches the
    /// same project — a missing config file must not look like a broken build.
    private static readonly AppSettings Shipped = new()
    {
        Url = "https://nzcvjaixgqoliyrotstz.supabase.co",
        AnonKey = "sb_publishable_bkIjJlfcQZDrXD6-l7i1uQ_v6OLf9Un",
    };

    private static AppSettings? _loaded;

    /// <summary>
    /// Read once. Order: a file beside the executable, then the shipped defaults.
    /// A malformed or half-filled file falls back rather than throwing — this runs before any
    /// window exists, so an exception here is a process that dies with no message at all.
    /// </summary>
    public static AppSettings Current => _loaded ??= Load();

    /// Set when the file was present but unusable, so the app can say so once it has a window.
    public static string? Problem { get; private set; }

    private static AppSettings Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, FileName);
        if (!File.Exists(path)) return Shipped;

        try
        {
            var read = JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(path));
            if (read is null || string.IsNullOrWhiteSpace(read.Url) || string.IsNullOrWhiteSpace(read.AnonKey))
            {
                Problem = $"{FileName} is missing Url or AnonKey — using the built-in defaults.";
                return Shipped;
            }
            return read;
        }
        catch (Exception e)
        {
            Problem = $"{FileName} could not be read ({e.Message}) — using the built-in defaults.";
            return Shipped;
        }
    }
}
