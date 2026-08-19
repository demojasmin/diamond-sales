using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DiamondDesktop.Data;

/// <summary>Whether the call worked, and what to tell the user if it did not.</summary>
public sealed record AdminResult(bool Ok, string? Code, string? Message)
{
    public static readonly AdminResult Success = new(true, null, null);
}

/// <summary>
/// The desktop half of the admin-users Edge Function.
///
/// Creating a login, resetting a password and changing a role all need the
/// service_role key, which bypasses RLS entirely. That key is NOT here, and must
/// never be: an .exe is not a secret, and a key in appsettings.json is a key in
/// everyone's hands. What travels is the signed-in user's own access token; the
/// function checks on the server that the caller is an owner and then acts with
/// its own credentials, which never leave Supabase.
///
/// Plain HttpClient rather than the SDK's Functions client, because the function
/// answers with a code and a sentence on 4xx and both are worth showing --
/// "This is the only active owner" is the difference between a user who
/// understands what happened and one who tries again.
/// </summary>
public static class AdminUsers
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static async Task<AdminResult> CallAsync(object payload)
    {
        string? token = Db.Client.Auth.CurrentSession?.AccessToken;
        if (string.IsNullOrEmpty(token))
            return new AdminResult(false, "NO_SESSION", "Sign in again.");

        var request = new HttpRequestMessage(HttpMethod.Post,
            $"{AppSettings.Current.Url.TrimEnd('/')}/functions/v1/admin-users")
        {
            Content = new StringContent(JsonConvert.SerializeObject(payload),
                                        Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            var response = await Http.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();

            // A gateway or a cold start can answer with something that is not
            // JSON at all; treating that as a parse crash would hide the status.
            JObject? parsed = null;
            try { parsed = JObject.Parse(body); } catch { /* not json */ }

            if (response.IsSuccessStatusCode && parsed?["ok"]?.Value<bool>() == true)
                return AdminResult.Success;

            return new AdminResult(false,
                parsed?["code"]?.ToString() ?? response.StatusCode.ToString(),
                parsed?["message"]?.ToString()
                    ?? $"The server refused the change ({(int)response.StatusCode}).");
        }
        catch (TaskCanceledException)
        {
            return new AdminResult(false, "TIMEOUT", "The server did not answer. Try again.");
        }
        catch (HttpRequestException e)
        {
            return new AdminResult(false, "OFFLINE", $"Could not reach the server. {e.Message}");
        }
    }

    public static Task<AdminResult> CreateAsync(string email, string fullName, string role, string password) =>
        CallAsync(new { action = "create", email, full_name = fullName, role, password });

    public static Task<AdminResult> SetActiveAsync(Guid userId, bool active) =>
        CallAsync(new { action = active ? "activate" : "deactivate", user_id = userId.ToString() });

    public static Task<AdminResult> ChangeRoleAsync(Guid userId, string role) =>
        CallAsync(new { action = "change_role", user_id = userId.ToString(), role });

    public static Task<AdminResult> ResetPasswordAsync(Guid userId, string password) =>
        CallAsync(new { action = "reset_password", user_id = userId.ToString(), password });
}
