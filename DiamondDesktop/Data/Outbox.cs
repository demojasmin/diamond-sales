using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DiamondDesktop.Data;

/// Writes parked on disk while the network is down, replayed in order when it comes back.
/// `operation` is the PostgREST path the write targets — a table name ("receipt") or "rpc/post_invoice" —
/// so one code path covers both. `payloadJson` is the raw body, client_ref already inside it.
public static class Outbox
{
    private const string Url = "https://nzcvjaixgqoliyrotstz.supabase.co";
    private const string AnonKey = "sb_publishable_bkIjJlfcQZDrXD6-l7i1uQ_v6OLf9Un";

    private static readonly string DbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SolitaireDesk", "outbox.db");

    private static readonly HttpClient Http = new();
    private static readonly SemaphoreSlim ReplayLock = new(1, 1);
    private static readonly Task Ready = InitAsync();

    /// Set after login. RLS is the boundary, so a replayed write must carry the user's token.
    public static string? AccessToken { get; set; }

    /// Raised off the UI thread — subscribers must marshal.
    public static event Action<int>? PendingChanged;

    public static async Task EnqueueAsync(string operation, string payloadJson, Guid clientRef)
    {
        await using var db = await OpenAsync();

        // OR IGNORE: the unique client_ref makes a double-queue a no-op instead of an error the caller must handle.
        var cmd = db.CreateCommand();
        cmd.CommandText = "insert or ignore into outbox(operation,payload,client_ref,queued_at) values($o,$p,$c,$t)";
        cmd.Parameters.AddWithValue("$o", operation);
        cmd.Parameters.AddWithValue("$p", payloadJson);
        cmd.Parameters.AddWithValue("$c", clientRef.ToString());
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync();

        PendingChanged?.Invoke(await CountAsync(db));
    }

    public static async Task<int> PendingCountAsync()
    {
        await using var db = await OpenAsync();
        return await CountAsync(db);
    }

    public static async Task<(int Sent, int Failed)> ReplayAsync()
    {
        if (!await ReplayLock.WaitAsync(0)) return (0, 0);   // a replay is already in flight
        try
        {
            await using var db = await OpenAsync();
            int pending = await CountAsync(db), sent = 0;

            foreach (var (id, operation, payload) in await ReadQueueAsync(db))
            {
                string? error = await SendAsync(operation, payload);
                if (error is not null)
                {
                    // Stop dead: later rows may depend on this one (an invoice must exist before its post).
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
            request.Headers.Add("Authorization", $"Bearer {AccessToken ?? AnonKey}");

            var response = await Http.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();

            // An RPC that declines (needs_override, shortfalls) answers 200. Dropping it would lose the decision.
            if (response.IsSuccessStatusCode)
                return Field(body, "ok") == "false" ? body : null;

            // 23505 means the row already landed on an attempt whose response we never saw. That is the
            // entire point of client_ref — the duplicate is proof of success, not a failure.
            return Field(body, "code") == "23505" ? null : $"{(int)response.StatusCode} {body}";
        }
        catch (HttpRequestException ex) { return ex.Message; }   // still offline; try again next reconnect
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

    private static async Task<List<(long Id, string Operation, string Payload)>> ReadQueueAsync(SqliteConnection db)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = "select id, operation, payload from outbox order by id";
        await using var reader = await cmd.ExecuteReaderAsync();

        // Materialised up front: the loop deletes rows on the same connection.
        List<(long, string, string)> rows = [];
        while (await reader.ReadAsync()) rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
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
              client_ref text unique,
              queued_at text not null,
              attempts int default 0,
              last_error text)
            """;
        await cmd.ExecuteNonQueryAsync();
    }
}
