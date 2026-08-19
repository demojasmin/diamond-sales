using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Supabase.Gotrue;
using Supabase.Gotrue.Exceptions;
using Supabase.Gotrue.Interfaces;

namespace DiamondDesktop.Data;

/// <summary>
/// The Supabase projects this install can reach, and which one is currently in play.
///
/// ONE CLIENT PER WORKSPACE, built once and kept. A Supabase client owns a token-refresh timer and
/// a session handler, so rebuilding one per sign-in attempt would leave those running against a
/// project nobody is signed in to any more.
///
/// The workspaces are ISOLATED, and that is the whole point of this type. Nothing is shared between
/// them: not the client, not the saved session, not the offline queue. A token minted by one is
/// never presented to another, because each keeps its own session file named after its project ref.
///
/// The anon key is publishable on purpose: RLS is the boundary and every request rides the
/// signed-in user's token.
/// </summary>
public static class Db
{
    private const string Offline = "No connection to the server.";

    /// <summary>
    /// The two refusals that mean "the password was right, this account just cannot be used HERE".
    ///
    /// Named because the sign-in loop has to tell them apart from a wrong password. An account
    /// deactivated in one project is how the same address is kept out of it while still belonging
    /// to another, and counting that as a failed attempt would slowly lock the address out of a
    /// project it was never trying to enter.
    /// </summary>
    public const string Deactivated = "This account is deactivated.";
    public const string NoProfile = "This login has no profile. Ask the owner to set one up.";

    /// Where "the workspace this desk used last" is written down, so a restart reaches for the right
    /// project first instead of whichever happens to be listed first.
    private static readonly string LastUsedFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SolitaireDesk", "workspace.txt");

    private static readonly Dictionary<string, Supabase.Client> Clients = [];
    // The interface, not EncryptedSession itself: that type is file-local, and a file-local type
    // cannot appear in a member signature of a type that is not.
    private static readonly Dictionary<string, IGotrueSessionPersistence<Session>> SessionStores = [];
    private static readonly Dictionary<string, Task<string?>> Initialising = [];

    /// <summary>Every project a sign-in may be tried against, the last-used one first.</summary>
    public static IReadOnlyList<Workspace> Workspaces { get; } = InTryOrder();

    /// <summary>
    /// The project every call below talks to.
    ///
    /// Never null. It starts at the last-used workspace and moves only when a sign-in succeeds
    /// somewhere else -- a failed probe must not leave the app pointing at a database the user
    /// never got into.
    /// </summary>
    public static Workspace Active { get; private set; } = Workspaces[0];

    public static Supabase.Client Client => ClientFor(Active);

    private static IReadOnlyList<Workspace> InTryOrder()
    {
        var all = AppSettings.All;
        string? last = null;
        try { last = File.ReadAllText(LastUsedFile).Trim(); }
        catch { /* never used on this desk, or unreadable -- the listed order stands */ }

        return string.IsNullOrEmpty(last)
            ? all
            : [.. all.OrderByDescending(w => string.Equals(w.Name, last, StringComparison.OrdinalIgnoreCase))];
    }

    private static Supabase.Client ClientFor(Workspace w)
    {
        if (Clients.TryGetValue(w.Ref, out var existing)) return existing;

        var sessions = new EncryptedSession(w.Ref);
        SessionStores[w.Ref] = sessions;

        var client = new Supabase.Client(w.Url, w.AnonKey, new Supabase.SupabaseOptions
        {
            AutoRefreshToken = true,
            AutoConnectRealtime = false,
            SessionHandler = sessions
        });
        Clients[w.Ref] = client;
        return client;
    }

    /// <summary>
    /// Points every later call at this project. Does NOT sign anybody in, and deliberately does not
    /// remember the choice -- only a successful sign-in does that.
    /// </summary>
    public static void Use(Workspace w)
    {
        if (w.Ref == Active.Ref) return;
        Active = w;
        CurrentUser = null;      // whoever was signed in belonged to the other project
    }

    private static void RememberActive()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LastUsedFile)!);
            File.WriteAllText(LastUsedFile, Active.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static Profile? CurrentUser { get; private set; }
    public static Guid? UserId => Guid.TryParse(Client.Auth.CurrentUser?.Id, out var id) ? id : null;
    // Compared case- and whitespace-insensitively. profile.role is free text with no CHECK
    // constraint behind it, so a row seeded as "Owner" or "OWNER " used to match nothing and
    // silently stripped a real owner of every permission — imports greyed out, no explanation.
    // It fails closed, which is the safe direction, but it is still the wrong answer.
    public static bool IsManagerOrOwner => Role is "manager" or "owner";
    public static bool IsOwner => Role is "owner";

    private static string? Role => CurrentUser?.Role?.Trim().ToLowerInvariant();
    public static bool IsOnline { get; private set; } = true;

    /// <summary>
    /// Records what a data call just proved about reachability.
    ///
    /// Until this existed, IsOnline was only ever set by Db's own sign-in path — a PostgREST read
    /// failing with "no such host" left it stuck on true. Everything keyed off it was therefore
    /// dead: the offline import branch never ran, and picking a file while disconnected did
    /// nothing at all, with no dialog and no message.
    ///
    /// Only transport failures count. A 400 or an RLS refusal means the server answered, which
    /// proves the connection is fine and the request was wrong.
    /// </summary>
    public static void NoteTransport(Exception? failure)
    {
        if (failure is null) { IsOnline = true; return; }

        for (var ex = failure; ex is not null; ex = ex.InnerException)
            if (ex is HttpRequestException or TaskCanceledException) { IsOnline = false; return; }
    }

    /// <summary>
    /// Restores and refreshes a saved session for the ACTIVE workspace if one is on disk. Null means
    /// "ready" -- check CurrentUser to decide whether the login window is still needed.
    ///
    /// Cached per workspace rather than per process. Cached once, the second workspace tried during
    /// a sign-in would hand back the FIRST one's result and never initialise its own client, which
    /// reads as "wrong email or password" against a project that was never asked.
    /// </summary>
    public static Task<string?> InitializeAsync()
    {
        string key = Active.Ref;
        if (Initialising.TryGetValue(key, out var running)) return running;

        var task = InitialiseCoreAsync();
        Initialising[key] = task;
        return task;
    }

    private static async Task<string?> InitialiseCoreAsync()
    {
        try
        {
            await Client.InitializeAsync();
            IsOnline = true;
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }

        if (Client.Auth.CurrentSession is null) return null;

        return await AdoptSessionAsync();
    }

    public static async Task<string?> SignInAsync(string email, string password)
    {
        if (await InitializeAsync() is { } notReady) return notReady;

        try
        {
            var session = await Client.Auth.SignIn(email, password);
            IsOnline = true;
            if (session?.User is null) return "Sign-in failed. Check your email and password.";
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }

        // Written here, not when the workspace was selected: this is the first moment we know the
        // credentials belong to THIS project, and it is what stops the next start from probing the
        // wrong one first.
        string? problem = await AdoptSessionAsync();
        if (problem is null) RememberActive();
        return problem;
    }

    public static async Task SignOutAsync()
    {
        CurrentUser = null;
        // Local scope only: signing out at this desk must not kick the same user off their other machines.
        try { await Client.Auth.SignOut(Constants.SignOutScope.Local); }
        catch { /* offline or an expired token — the server-side session lapses on its own */ }

        // Gotrue skips its own cleanup when that call throws, and a surviving session file would sign
        // the next person at this desk in as the previous user. Only THIS workspace's file goes --
        // signing out of one project must not sign anybody out of the other.
        if (SessionStores.TryGetValue(Active.Ref, out var store)) store.DestroySession();
    }

    /// A token without a usable profile row is worse than no token: RLS denies everything and the user
    /// stares at blank screens. Drop the session and say why — but only on an answer we actually got, or a
    /// blip at startup would burn a saved login the user cannot replace until the network is back.
    private static async Task<string?> AdoptSessionAsync()
    {
        string? problem = await LoadProfileAsync();
        if (problem is not null && IsOnline) await SignOutAsync();
        return problem;
    }

    private static async Task<string?> LoadProfileAsync()
    {
        if (UserId is not { } uid) return "Signed in, but the account has no id. Sign in again.";

        try
        {
            var rows = await Client.From<Profile>()
                .Filter("id", Supabase.Postgrest.Constants.Operator.Equals, uid.ToString())
                .Get();
            IsOnline = true;
            CurrentUser = rows.Models.FirstOrDefault();
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }

        if (CurrentUser is null) return NoProfile;
        if (!CurrentUser.Active) { CurrentUser = null; return Deactivated; }
        return null;
    }

    private static string Fail(Exception ex)
    {
        string message = ex switch
        {
            GotrueException
            {
                Reason: FailureHint.Reason.UserBadLogin or FailureHint.Reason.UserBadPassword
                     or FailureHint.Reason.UserBadEmailAddress or FailureHint.Reason.UserBadMultiple
            } => "Wrong email or password.",
            GotrueException { Reason: FailureHint.Reason.UserEmailNotConfirmed } => "Email not confirmed yet.",
            GotrueException { Reason: FailureHint.Reason.UserTooManyRequests } => "Too many attempts. Wait a minute.",
            GotrueException { Reason: FailureHint.Reason.Offline } => Offline,
            GotrueException
            {
                Reason: FailureHint.Reason.ExpiredRefreshToken or FailureHint.Reason.InvalidRefreshToken
                     or FailureHint.Reason.NoSessionFound
            } => "Session expired. Sign in again.",
            HttpRequestException or TaskCanceledException => Offline,
            _ => ex.Message
        };

        IsOnline = message != Offline;   // the server answering "no" still proves we reached it
        return message;
    }
}

/// DPAPI ties the ciphertext to this Windows account, so a copied session.dat is useless on another
/// machine or under another user.
file sealed class EncryptedSession(string projectRef) : IGotrueSessionPersistence<Session>
{
    /// <summary>
    /// One file per project, named after its ref.
    ///
    /// Two workspaces sharing a single session.dat would restore whichever token was written last
    /// against whichever project happens to be active -- signing somebody in to a database that
    /// never issued their login, with their own project's data nowhere to be seen.
    /// </summary>
    private readonly string SessionFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SolitaireDesk", $"session-{projectRef}.dat");

    public void SaveSession(Session session)
    {
        // Persistence is a convenience: a failed write must never break a sign-in that already succeeded.
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SessionFile)!);
            byte[] plain = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(session));
            File.WriteAllBytes(SessionFile, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { }
    }

    public Session? LoadSession()
    {
        try
        {
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(SessionFile), null, DataProtectionScope.CurrentUser);
            return JsonConvert.DeserializeObject<Session>(Encoding.UTF8.GetString(plain));
        }
        catch { return null; }   // missing, corrupt, or sealed for a different Windows user — just sign in again
    }

    public void DestroySession()
    {
        try { File.Delete(SessionFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
