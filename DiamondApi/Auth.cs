using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace DiamondApi;

/// AUTH-001/002. Opaque random bearer tokens in a table — no JWT library, nothing to mis-configure.
public static class Auth
{
    private const int Iterations = 210_000;      // OWASP 2023 guidance for PBKDF2-SHA256
    private const int SaltBytes = 16, KeyBytes = 32;

    // ponytail: PBKDF2-SHA256 from the BCL, not the argon2id docs/06 §2 specifies — argon2 needs a
    // package. PBKDF2 at this iteration count is a sound choice; swap in Konscious.Argon2 if the
    // client's security review asks for memory-hardness.
    public static string Hash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeyBytes);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2") return false;

        byte[] salt = Convert.FromBase64String(parts[2]);
        byte[] expected = Convert.FromBase64String(parts[3]);
        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, int.Parse(parts[1]), HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);   // never `==` on a hash
    }

    public sealed record LoginResult(string? Token, AppUser? User, string? Error, int StatusCode);

    public static LoginResult Login(DiamondDb db, string username, string password)
    {
        var user = db.Users.FirstOrDefault(u => u.Username == username);
        // max_login_attempts is the key the Settings page writes; lockout_attempts is what this
        // file read for its whole life, so the configured number never reached the code enforcing
        // it. One name, both readers — the old key stays as a fallback for databases seeded before
        // the rename (mirrored in public.max_login_attempts(), migration 0025).
        int lockoutAttempts = Settings.Int(db, "max_login_attempts",
                              Settings.Int(db, "lockout_attempts", 5));

        if (user is null || !user.Active)
        {
            db.Audit.Add(new AuditEntry { EntityType = "AppUser", Action = "LOGIN_FAIL", After = username });
            db.SaveChanges();
            return new(null, null, "Invalid username or password", 401);
        }

        if (user.LockedUntil is { } until && until > DateTime.UtcNow)
            return new(null, null, $"Account locked until {until:HH:mm} UTC", 423);

        if (!Verify(password, user.PasswordHash))
        {
            user.FailedLogins++;
            if (user.FailedLogins >= lockoutAttempts)
            {
                user.LockedUntil = DateTime.UtcNow.AddMinutes(15);
                user.FailedLogins = 0;
            }
            db.Audit.Add(new AuditEntry { EntityType = "AppUser", EntityId = user.UserId, Action = "LOGIN_FAIL", UserId = user.UserId });
            db.SaveChanges();
            return new(null, null, "Invalid username or password", 401);
        }

        user.FailedLogins = 0;
        user.LockedUntil = null;

        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        db.Sessions.Add(new Session
        {
            Token = token,
            UserId = user.UserId,
            ExpiresAt = DateTime.UtcNow.AddMinutes(Settings.Int(db, "session_timeout_min", 60)),
        });
        db.SaveChanges();
        return new(token, user, null, 200);
    }

    /// Resolves the bearer token on every request. Expired sessions are deleted, not merely rejected.
    public static AppUser? Resolve(DiamondDb db, HttpContext http)
    {
        string? header = http.Request.Headers.Authorization.FirstOrDefault();
        if (header is null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;

        string token = header["Bearer ".Length..].Trim();
        var session = db.Sessions.FirstOrDefault(s => s.Token == token);
        if (session is null) return null;

        if (session.ExpiresAt <= DateTime.UtcNow)
        {
            db.Sessions.Remove(session);
            db.SaveChanges();
            return null;
        }

        var user = db.Users.FirstOrDefault(u => u.UserId == session.UserId && u.Active);
        if (user is null) return null;

        session.ExpiresAt = DateTime.UtcNow.AddMinutes(Settings.Int(db, "session_timeout_min", 60));
        db.SaveChanges();
        return user;
    }

    public static void Logout(DiamondDb db, string token)
        => db.Sessions.Where(s => s.Token == token).ExecuteDelete();
}

public static class Settings
{
    public static string? Get(DiamondDb db, string key) => db.Settings.FirstOrDefault(s => s.Key == key)?.Value;

    public static int Int(DiamondDb db, string key, int fallback)
        => int.TryParse(Get(db, key), out int v) ? v : fallback;

    public static decimal Dec(DiamondDb db, string key, decimal fallback)
        => decimal.TryParse(Get(db, key), out decimal v) ? v : fallback;

    public static bool Bool(DiamondDb db, string key, bool fallback)
        => bool.TryParse(Get(db, key), out bool v) ? v : fallback;
}
