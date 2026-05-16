using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Bonfire.Service.Modules;

/// <summary>
/// Live-propagation client for Server.exe's WebUI <c>/settings</c> endpoint.
///
/// When <see cref="Ds2NativeSessionCoordinator.StampManifestIfChanged"/>
/// writes a new manifest into <c>config.json</c>, Server.exe (which loaded
/// its config once at boot and caches it in memory) keeps publishing the
/// stale <c>ServerDescription</c> to the master server until the user
/// restarts it. This client closes that gap by POSTing the new
/// <c>serverName</c>/<c>serverDescription</c> to the running Server.exe so
/// its in-memory <c>RuntimeConfig</c> updates and the next 30s master
/// heartbeat carries the fresh manifest — no Server.exe restart needed.
///
/// The flow is authenticated: Server.exe generates random
/// <c>WebUIServerUsername</c>/<c>WebUIServerPassword</c> on first boot and
/// persists them to <c>config.json</c>; we read them from there, do a
/// POST <c>/auth</c> handshake to get a short-lived <c>Auth-Token</c>, and
/// then POST <c>/settings</c> with that header.
/// </summary>
public static class Ds2NativeWebUIPush
{
    public sealed record PushResult(
        bool Success,
        bool ServerReachable,
        string Status,
        string? ErrorMessage);

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(3),
    };

    static Ds2NativeWebUIPush()
    {
        Http.DefaultRequestHeaders.Accept.Clear();
        Http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Bonfire-LivePush/1.0");
    }

    private sealed class AuthRequest
    {
        [JsonPropertyName("username")] public string Username { get; set; } = "";
        [JsonPropertyName("password")] public string Password { get; set; } = "";
    }

    private sealed class AuthResponse
    {
        [JsonPropertyName("token")] public string? Token { get; set; }
    }

    private sealed class SettingsRequest
    {
        [JsonPropertyName("serverName")] public string ServerName { get; set; } = "";
        [JsonPropertyName("serverDescription")] public string ServerDescription { get; set; } = "";
    }

    /// <summary>
    /// Push the current <c>ServerName</c> + <c>ServerDescription</c> from
    /// <paramref name="cfg"/> to the running Server.exe's WebUI. Returns a
    /// structured result so the coordinator can flip
    /// <c>manifest_stale_pending_restart</c> accordingly.
    /// </summary>
    public static async Task<PushResult> PushAsync(
        ServerConfig cfg,
        int webUiPort,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(cfg.WebUIServerUsername) ||
            string.IsNullOrEmpty(cfg.WebUIServerPassword))
        {
            return new PushResult(
                Success: false,
                ServerReachable: false,
                Status: "credentials_missing",
                ErrorMessage:
                    "WebUI credentials are empty — Server.exe has not booted " +
                    "long enough to generate them, or the WebUI is disabled.");
        }

        if (webUiPort <= 0) webUiPort = 50005;
        var baseUrl = $"http://127.0.0.1:{webUiPort}";

        try
        {
            // Step 1: auth.
            var authReq = new AuthRequest
            {
                Username = cfg.WebUIServerUsername,
                Password = cfg.WebUIServerPassword,
            };
            using var authResp = await Http.PostAsJsonAsync(
                $"{baseUrl}/auth", authReq, ct);
            if (!authResp.IsSuccessStatusCode)
            {
                return new PushResult(
                    Success: false,
                    ServerReachable: true,
                    Status: "auth_failed_http_" + (int)authResp.StatusCode,
                    ErrorMessage:
                        "Server.exe rejected WebUI credentials (status " +
                        (int)authResp.StatusCode + ").");
            }

            var authBody = await authResp.Content.ReadFromJsonAsync<AuthResponse>(
                cancellationToken: ct);
            if (string.IsNullOrEmpty(authBody?.Token))
            {
                return new PushResult(
                    Success: false,
                    ServerReachable: true,
                    Status: "auth_no_token",
                    ErrorMessage:
                        "WebUI /auth response did not include a token.");
            }

            // Step 2: settings push with Auth-Token header.
            var settingsReq = new SettingsRequest
            {
                ServerName = cfg.ServerName,
                ServerDescription = cfg.ServerDescription,
            };
            using var settingsMsg = new HttpRequestMessage(
                HttpMethod.Post, $"{baseUrl}/settings");
            settingsMsg.Headers.TryAddWithoutValidation("Auth-Token", authBody.Token);
            settingsMsg.Content = JsonContent.Create(settingsReq);

            using var settingsResp = await Http.SendAsync(settingsMsg, ct);
            if (!settingsResp.IsSuccessStatusCode)
            {
                return new PushResult(
                    Success: false,
                    ServerReachable: true,
                    Status: "settings_failed_http_" + (int)settingsResp.StatusCode,
                    ErrorMessage:
                        "Server.exe rejected settings push (status " +
                        (int)settingsResp.StatusCode + ").");
            }

            return new PushResult(
                Success: true,
                ServerReachable: true,
                Status: "pushed",
                ErrorMessage: null);
        }
        catch (TaskCanceledException ex)
        {
            return new PushResult(
                Success: false,
                ServerReachable: false,
                Status: "timeout",
                ErrorMessage: "Push timed out: " + ex.Message);
        }
        catch (HttpRequestException ex)
        {
            // Most commonly: connection refused because Server.exe is down or
            // not listening on the WebUI port yet.
            return new PushResult(
                Success: false,
                ServerReachable: false,
                Status: "connect_failed",
                ErrorMessage:
                    "Could not reach Server.exe WebUI (probably down): " +
                    ex.Message);
        }
        catch (Exception ex)
        {
            return new PushResult(
                Success: false,
                ServerReachable: false,
                Status: "unexpected",
                ErrorMessage: ex.Message);
        }
    }
}
