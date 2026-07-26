using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Supabase.Gotrue;
using Supabase.Gotrue.Exceptions;
using Supabase.Gotrue.Interfaces;

namespace DiamondDesktop.Data;

/// The one Supabase client for the app. The anon key is publishable on purpose — RLS is the boundary,
/// and every request rides the signed-in user's token.
public static class Db
{
    private const string Url = "https://nzcvjaixgqoliyrotstz.supabase.co";
    private const string AnonKey = "sb_publishable_bkIjJlfcQZDrXD6-l7i1uQ_v6OLf9Un";
    private const string Offline = "No connection to the server.";

    private static readonly IGotrueSessionPersistence<Session> Sessions = new EncryptedSession();

    public static Supabase.Client Client { get; } = new(Url, AnonKey, new Supabase.SupabaseOptions
    {
        AutoRefreshToken = true,
        AutoConnectRealtime = false,
        SessionHandler = Sessions
    });

    public static Profile? CurrentUser { get; private set; }
    public static Guid? UserId => Guid.TryParse(Client.Auth.CurrentUser?.Id, out var id) ? id : null;
    public static bool IsManagerOrOwner => CurrentUser?.Role is "manager" or "owner";
    public static bool IsOwner => CurrentUser?.Role is "owner";
    public static bool IsOnline { get; private set; } = true;

    /// Cached so initialisation happens exactly once however many callers race for it. Sign-in awaits
    /// this too: clicking the button before the window's own Loaded handler finished would otherwise
    /// reach Auth on an unconfigured client, and a fast typist can do that.
    private static Task<string?>? _initialising;

    /// Restores and refreshes a saved session if one is on disk. Null means "ready" — check CurrentUser
    /// to decide whether the login window is still needed.
    public static Task<string?> InitializeAsync() => _initialising ??= InitialiseCoreAsync();

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

        return await AdoptSessionAsync();
    }

    public static async Task SignOutAsync()
    {
        CurrentUser = null;
        // Local scope only: signing out at this desk must not kick the same user off their other machines.
        try { await Client.Auth.SignOut(Constants.SignOutScope.Local); }
        catch { /* offline or an expired token — the server-side session lapses on its own */ }

        // Gotrue skips its own cleanup when that call throws, and a surviving session.dat would sign the
        // next person at this desk in as the previous user.
        Sessions.DestroySession();
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

        if (CurrentUser is null) return "This login has no profile. Ask the owner to set one up.";
        if (!CurrentUser.Active) { CurrentUser = null; return "This account is deactivated."; }
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
file sealed class EncryptedSession : IGotrueSessionPersistence<Session>
{
    private static readonly string SessionFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SolitaireDesk", "session.dat");

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
