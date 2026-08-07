using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DiamondDesktop.Data;

/// NOT WIRED. Nothing calls this yet — no write path enqueues, nothing triggers ReplayAsync, and
/// no screen shows the pending count. It was dead code before it was deleted and it is dead code
/// now; restored because SYNC-001/FR-SYNC-1 make offline entry a Must and this is the right shape
/// for it, not because the app has offline support.
///
/// What working offline needs on top of this: every Repo write branching to EnqueueAsync when
/// Db.IsOnline is false, a reconnect trigger calling ReplayAsync, PendingChanged bound to the sync
/// chip, and a stable client_ref per user action rather than per attempt (see ConvertAsync).
///
/// Writes parked on disk while the network is down, replayed in order when it comes back.
/// `operation` is the PostgREST path the write targets — a table name ("receipt") or "rpc/post_invoice" —
/// so one code path covers both. `payloadJson` is the raw body, client_ref already inside it.
public static class Outbox
{
    // Same source as Db, so the two can never point at different projects.
    private static string Url => AppSettings.Current.Url;
    private static string AnonKey => AppSettings.Current.AnonKey;

    /// <summary>
    /// Where the queue lives. SOLITAIREDESK_OUTBOX overrides it, which exists for one reason: the
    /// test suite must never touch the real one. It did once — a test queued an import, could not
    /// delete the file because the running app held it open, and the app then showed a stranded
    /// "1 held — needs attention" for an import nobody had made. A test that can write to the
    /// user's live state is a test that can lie to the user.
    /// </summary>
    private static readonly string DbPath =
        Environment.GetEnvironmentVariable("SOLITAIREDESK_OUTBOX") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                           "SolitaireDesk", "outbox.db");

    private static readonly HttpClient Http = new();
    private static readonly SemaphoreSlim ReplayLock = new(1, 1);
    private static readonly Task Ready = InitAsync();

    /// Raised off the UI thread — subscribers must marshal.
    public static event Action<int>? PendingChanged;

    /// Set when a replay stopped because a queued REPLACE could no longer be proven safe. Cleared
    /// at the start of every replay. Null means the queue is simply empty or still sending.
    public static string? Blocked { get; private set; }

    /// <param name="guard">
    /// What the caller believed the server held when this was queued, for operations that REPLACE
    /// rather than append. Replay refuses to send unless the server still matches — see
    /// <see cref="ReplayAsync"/>. Null for appends, which are safe to replay whatever else happened.
    /// </param>
    public static async Task EnqueueAsync(string operation, string payloadJson, Guid clientRef,
                                          string? guard = null)
    {
        await using var db = await OpenAsync();

        // OR IGNORE: the unique (operation, client_ref) makes a double-queue a no-op instead of an error
        // the caller must handle.
        var cmd = db.CreateCommand();
        cmd.CommandText = "insert or ignore into outbox(operation,payload,client_ref,queued_at,guard) "
                        + "values($o,$p,$c,$t,$g)";
        cmd.Parameters.AddWithValue("$o", operation);
        cmd.Parameters.AddWithValue("$p", payloadJson);
        cmd.Parameters.AddWithValue("$c", clientRef.ToString());
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$g", (object?)guard ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();

        PendingChanged?.Invoke(await CountAsync(db));
    }

    /// <summary>
    /// What is waiting, oldest first, for a screen that wants to show it rather than just count it.
    /// </summary>
    public static async Task<List<(string Operation, DateTime QueuedAt, string? LastError)>> PendingAsync()
    {
        await using var db = await OpenAsync();
        var cmd = db.CreateCommand();
        cmd.CommandText = "select operation, queued_at, last_error from outbox order by id";
        await using var reader = await cmd.ExecuteReaderAsync();

        List<(string, DateTime, string?)> rows = [];
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0),
                      DateTime.TryParse(reader.GetString(1),
                          System.Globalization.CultureInfo.InvariantCulture,
                          System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : DateTime.MinValue,
                      reader.IsDBNull(2) ? null : reader.GetString(2)));
        return rows;
    }

    public static async Task<int> PendingCountAsync()
    {
        await using var db = await OpenAsync();
        return await CountAsync(db);
    }

    /// <summary>
    /// Sends what is queued, oldest first. Returns what went and what is still waiting.
    /// </summary>
    /// <param name="currentGuard">
    /// Reads the server's present state for an operation, so a REPLACE can be checked before it is
    /// sent. An import queued offline deletes everything the previous import wrote — replaying that
    /// blind, hours later, would revert a colleague's newer import without a word. If the server no
    /// longer matches what the queue expected, the entry is HELD, not sent and not discarded, and
    /// <see cref="Blocked"/> says so.
    ///
    /// Held rather than dropped because the file is the user's work: the safe failure is "still
    /// waiting, come and look", never "quietly gone" and never "quietly overwrote someone".
    /// </param>
    public static async Task<(int Sent, int Failed)> ReplayAsync(
        Func<string, Task<string?>>? currentGuard = null)
    {
        if (!await ReplayLock.WaitAsync(0)) return (0, 0);   // a replay is already in flight
        try
        {
            Blocked = null;
            await using var db = await OpenAsync();
            int pending = await CountAsync(db), sent = 0;

            foreach (var (id, operation, payload, guard) in await ReadQueueAsync(db))
            {
                if (guard is not null)
                {
                    // No verifier, or a verifier that cannot read the server, means we cannot prove
                    // the replace is still safe. "Unknown" is not "unchanged".
                    string? now = currentGuard is null ? null : await currentGuard(operation);
                    if (now is null || now != guard)
                    {
                        Blocked = now is null
                            ? "Could not check whether the server changed while you were offline. "
                              + "The queued import is still waiting."
                            : "Someone else imported after this one was queued. The queued import is "
                              + "still waiting — applying it now would undo their work.";
                        return (sent, 1);
                    }
                }

                string? error = await SendAsync(operation, payload);
                if (error is not null)
                {
                    // Stop dead: later rows may depend on this one (an invoice must exist before its post).
                    // ponytail: a permanently-refused row (RLS denial, needs_override) therefore blocks the
                    // queue forever. `attempts` is recorded but never acted on — add a dead-letter view over
                    // attempts > N when the first row actually wedges.
                    var fail = db.CreateCommand();
                    fail.CommandText = "update outbox set attempts=attempts+1, last_error=$e where id=$id";
                    fail.Parameters.AddWithValue("$e", error);
                    fail.Parameters.AddWithValue("$id", id);
                    await fail.ExecuteNonQueryAsync();
                    return (sent, 1);
                }

                var done = db.CreateCommand();
                done.CommandText = "delete from outbox where id=$id";
                done.Parameters.AddWithValue("$id", id);
                await done.ExecuteNonQueryAsync();

                PendingChanged?.Invoke(pending - ++sent);
            }
            return (sent, 0);
        }
        finally { ReplayLock.Release(); }
    }

    /// Returns null on success, else a message worth showing a human.
    private static async Task<string?> SendAsync(string operation, string payload)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{Url}/rest/v1/{operation}")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("apikey", AnonKey);
            // Read the live session, never a copy taken at login: a replay can land hours later, well past
            // a token refresh, and an expired JWT would stall the whole queue on a 401.
            request.Headers.Add("Authorization", $"Bearer {Db.Client.Auth.CurrentSession?.AccessToken ?? AnonKey}");

            var response = await Http.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();

            // An RPC that declines (needs_override, shortfalls) answers 200. Dropping it would lose the decision.
            if (response.IsSuccessStatusCode)
                return Field(body, "ok") == "false" ? body : null;

            // 23505 *on client_ref* means the row already landed on an attempt whose response we never saw —
            // that is the entire point of client_ref, so the duplicate is proof of success. Any other unique
            // violation (invoice_no, currency.code, app_config.key) is a real failure and must not be dropped.
            return Field(body, "code") == "23505" && body.Contains("client_ref", StringComparison.Ordinal)
                ? null
                : $"{(int)response.StatusCode} {body}";
        }
        // TaskCanceledException is what a timed-out HttpClient throws; it is offline, not a crash.
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return ex.Message; }
    }

    private static string? Field(string json, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var v)
                ? v.ToString().ToLowerInvariant()
                : null;
        }
        catch { return null; }
    }

    private static async Task<List<(long Id, string Operation, string Payload, string? Guard)>>
        ReadQueueAsync(SqliteConnection db)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = "select id, operation, payload, guard from outbox order by id";
        await using var reader = await cmd.ExecuteReaderAsync();

        // Materialised up front: the loop deletes rows on the same connection.
        List<(long, string, string, string?)> rows = [];
        while (await reader.ReadAsync())
            rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                      reader.IsDBNull(3) ? null : reader.GetString(3)));
        return rows;
    }

    private static async Task<int> CountAsync(SqliteConnection db)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = "select count(*) from outbox";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task<SqliteConnection> OpenAsync()
    {
        await Ready;
        var db = new SqliteConnection($"Data Source={DbPath}");
        await db.OpenAsync();
        return db;
    }

    private static async Task InitAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        await using var db = new SqliteConnection($"Data Source={DbPath}");
        await db.OpenAsync();

        var cmd = db.CreateCommand();
        cmd.CommandText = """
            create table if not exists outbox(
              id integer primary key autoincrement,
              operation text not null,
              payload text not null,
              client_ref text not null,
              queued_at text not null,
              attempts int default 0,
              last_error text,
              -- Scoped to the operation: one business entity legitimately queues an insert AND a later RPC
              -- under the same client_ref. A bare unique(client_ref) would silently swallow the second.
              unique(operation, client_ref))
            """;
        await cmd.ExecuteNonQueryAsync();

        // Added after the table shipped, so an existing outbox.db on a desk has to gain it rather
        // than be recreated — recreating would discard whatever is queued, which is the one thing
        // this file exists to prevent. SQLite has no "add column if not exists"; asking the schema
        // is the portable way.
        var cols = db.CreateCommand();
        cols.CommandText = "select count(*) from pragma_table_info('outbox') where name='guard'";
        if (Convert.ToInt32(await cols.ExecuteScalarAsync()) == 0)
        {
            var add = db.CreateCommand();
            add.CommandText = "alter table outbox add column guard text";
            await add.ExecuteNonQueryAsync();
        }
    }
}
