using System.IO;
using System.Security.Cryptography;
using System.Text;
using DiamondDesktop.Data;
using Newtonsoft.Json.Linq;

namespace InvProbe;

/// <summary>
/// Everything the suites need to know about THIS machine, in one place.
///
/// The suites used to carry <c>C:\Users\jbc\Downloads\...</c> and the session path inline, which
/// made them work on exactly one computer and put a person's home directory in the repository.
/// Both are read from the environment now, with defaults that are correct for a normal install
/// rather than for one person's.
///
/// Nothing here is secret: the session file is written by the app under DPAPI and can only be
/// decrypted by the Windows account that created it, so pointing at it is not a way to share a
/// login. A suite run by someone who has never signed in simply gets a clear message.
/// </summary>
public static class ProbeEnv
{
    /// <summary>
    /// Where the sample and stock workbooks live. Override with PROBE_DATA_DIR.
    /// Defaults to the current user's Downloads, which is where the imports are driven from.
    /// </summary>
    public static string DataDir =>
        Environment.GetEnvironmentVariable("PROBE_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>A workbook by file name, resolved against <see cref="DataDir"/>.</summary>
    public static string Data(string fileName) => Path.Combine(DataDir, fileName);

    /// <summary>
    /// The desktop app's saved session. Override with SOLITAIRE_SESSION.
    /// Same location AppSettings uses, so the two cannot drift apart.
    /// </summary>
    public static string SessionFile =>
        Environment.GetEnvironmentVariable("SOLITAIRE_SESSION")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "SolitaireDesk", "session.dat");

    /// <summary>
    /// Restores the desktop's session so a suite reads the same database the app does.
    ///
    /// Fails with a sentence rather than a stack trace: "no session" is the normal state on a
    /// machine where nobody has signed in yet, and a FileNotFoundException does not say that.
    /// </summary>
    public static async Task SignInAsync()
    {
        await Db.InitializeAsync();
        if (Db.Client.Auth.CurrentUser is not null) return;

        string path = SessionFile;
        if (!File.Exists(path))
        {
            // Db.InitializeAsync signs out — and DELETES this file — when it can reach the server
            // but cannot refresh the token. So "missing" usually means "expired a moment ago",
            // not "never signed in", and saying only the latter sends people looking in the wrong
            // place. Both readings are offered.
            Console.WriteLine($"No usable session at {path}.");
            Console.WriteLine("Either nobody has signed in on this machine, or the saved token "
                            + "expired and was cleared. Open Solitaire Desk and sign in, or set "
                            + "SOLITAIRE_SESSION to another session file.");
            Environment.Exit(3);
        }

        var json = JObject.Parse(Encoding.UTF8.GetString(
            ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser)));
        await Db.Client.Auth.SetSession(json["access_token"]!.ToString(),
                                        json["refresh_token"]!.ToString());
    }

    /// <summary>
    /// Loads the profile behind the session. SetSession restores the token but not the profile, so
    /// Db.CurrentUser stays null and every role-gated control reads as denied — a suite that skips
    /// this measures a permission state the app never actually shows.
    /// </summary>
    public static async Task LoadProfileAsync()
    {
        if (Db.CurrentUser is not null) return;
        var load = typeof(Db).GetMethod("LoadProfileAsync", System.Reflection.BindingFlags.NonPublic
                                                          | System.Reflection.BindingFlags.Static);
        if (load is not null) await (Task<string?>)load.Invoke(null, null)!;
    }
}
