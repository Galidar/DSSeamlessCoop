/*
 * Master server queries — list public servers, fetch public keys.
 *
 * Endpoint shape (from upstream):
 *   GET  /api/v1/servers
 *     -> { "status": "success", "servers": [ ... ServerEntry ... ] }
 *   POST /api/v1/servers/<id>/public_key
 *     body: { "password": "..." }
 *     -> { "status": "success", "public_key": "..." }
 */

using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Bonfire.Service.Modules;

public sealed record ServerEntry(
    string Id,
    string Name,
    string Description,
    string GameType,
    string Hostname,
    string PrivateHostname,
    string IpAddress,
    int Port,
    int PlayerCount,
    bool PasswordRequired,
    bool AllowSharding,
    bool IsShard,
    bool IsRelayed,
    string ModsWhiteList,
    string ModsBlackList,
    string ModsRequiredList,
    string WebAddress);

public static class MasterServer
{
    public const string DefaultUrl = "http://ds3os-master.timleonard.uk:50020";
    public static string BaseUrl { get; set; } = DefaultUrl;

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    static MasterServer()
    {
        Http.DefaultRequestHeaders.Accept.Clear();
        Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Bonfire/0.1");
    }

    private class BaseResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }

    private sealed class ListServersResponse : BaseResponse
    {
        [JsonPropertyName("servers")] public List<ServerJson>? Servers { get; set; }
    }

    private sealed class PublicKeyRequest
    {
        [JsonPropertyName("password")] public string Password { get; set; } = "";
    }

    // The master server returns the response key as PascalCase "PublicKey"
    // (see Source/MasterServer/src/routes/api/v1/servers.js — `res.json({
    // "status":"success", "PublicKey": GActiveServers[i].PublicKey })`).
    private sealed class PublicKeyResponse : BaseResponse
    {
        [JsonPropertyName("PublicKey")] public string? PublicKey { get; set; }
    }

    // The upstream server JSON uses PascalCase keys ("Id", "Name", ...). We
    // keep the same casing so deserialisation is direct.
    private sealed class ServerJson
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public int Port { get; set; }
        public string? Hostname { get; set; }
        public string? PrivateHostname { get; set; }
        public string? IpAddress { get; set; }
        public int PlayerCount { get; set; }
        public bool PasswordRequired { get; set; }
        public string? ModsWhiteList { get; set; }
        public string? ModsBlackList { get; set; }
        public string? ModsRequiredList { get; set; }
        public bool AllowSharding { get; set; }
        public string? WebAddress { get; set; }
        public bool IsShard { get; set; }
        public bool IsRelayed { get; set; }
        public string? GameType { get; set; }
    }

    public static async Task<List<ServerEntry>?> ListServersAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await Http.GetAsync($"{BaseUrl}/api/v1/servers", ct);
            if (!resp.IsSuccessStatusCode) return null;

            var parsed = await resp.Content.ReadFromJsonAsync<ListServersResponse>(cancellationToken: ct);
            if (parsed is null || parsed.Status != "success" || parsed.Servers is null)
                return null;

            return parsed.Servers.Select(s => new ServerEntry(
                Id: !string.IsNullOrEmpty(s.Id) ? s.Id! : (s.IpAddress ?? ""),
                Name: s.Name ?? "",
                Description: s.Description ?? "",
                GameType: s.GameType ?? "",
                Hostname: s.Hostname ?? "",
                PrivateHostname: s.PrivateHostname ?? "",
                IpAddress: s.IpAddress ?? "",
                Port: s.Port,
                PlayerCount: s.PlayerCount,
                PasswordRequired: s.PasswordRequired,
                AllowSharding: s.AllowSharding,
                IsShard: s.IsShard,
                IsRelayed: s.IsRelayed,
                ModsWhiteList: s.ModsWhiteList ?? "",
                ModsBlackList: s.ModsBlackList ?? "",
                ModsRequiredList: s.ModsRequiredList ?? "",
                WebAddress: s.WebAddress ?? "")
            ).ToList();
        }
        catch
        {
            return null;
        }
    }

    public static async Task<string?> GetPublicKeyAsync(
        string serverId, string password, CancellationToken ct = default)
    {
        try
        {
            var req = new PublicKeyRequest { Password = password };
            var resp = await Http.PostAsJsonAsync(
                $"{BaseUrl}/api/v1/servers/{serverId}/public_key", req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var parsed = await resp.Content.ReadFromJsonAsync<PublicKeyResponse>(cancellationToken: ct);
            if (parsed is null || parsed.Status != "success") return null;
            return parsed.PublicKey;
        }
        catch
        {
            return null;
        }
    }
}
