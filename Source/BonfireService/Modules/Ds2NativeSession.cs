using System.Globalization;
using System.Text.Json.Nodes;

namespace Bonfire.Service.Modules;

/// <summary>
/// Protocol module for DS2 Native Session advertisement. Defines the manifest
/// schema embedded into <c>ServerDescription</c> behind a sentinel marker, plus
/// helpers to serialize, embed, strip, and parse it. The same module is reused
/// by other Bonfire clients when scanning master server entries.
/// </summary>
public static class Ds2NativeSession
{
    /// <summary>
    /// Sentinel that prefixes the manifest JSON inside <c>ServerDescription</c>.
    /// Versioned so future schema changes can coexist with old listings.
    /// </summary>
    public const string ManifestSentinel = "%%BNS-DS2-V1%%";

    public const int CurrentRuntimeVersion = 1;

    /// <summary>
    /// <c>Server.exe</c> re-publishes ServerName/ServerDescription/etc. to the
    /// master server on this interval (see <c>Server::PollServerAdvertisement</c>
    /// and <c>RuntimeConfig::AdvertiseHearbeatTime</c>). Used by service_state
    /// to tell the UI how long a stamp can take to propagate after a restart.
    /// </summary>
    public const int HeartbeatPropagationSeconds = 30;

    public sealed record Manifest(
        string SessionId,
        string Mode,
        string Stage,
        string Intent,
        string RulePreset,
        int TauntCount,
        int InfectionCount,
        int CurseCount,
        int RecoveryCount,
        int RuntimeVersion,
        DateTime TimestampUtc)
    {
        public JsonObject ToJson() => new()
        {
            ["session_id"] = SessionId,
            ["mode"] = Mode,
            ["stage"] = Stage,
            ["intent"] = Intent,
            ["rules"] = RulePreset,
            ["taunt"] = TauntCount,
            ["infection"] = InfectionCount,
            ["curse"] = CurseCount,
            ["recovery"] = RecoveryCount,
            ["runtime_ver"] = RuntimeVersion,
            ["ts"] = TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
        };

        public string ToCompactJson() => ToJson().ToJsonString();

        public string ToSentinelLine() => ManifestSentinel + ToCompactJson();
    }

    public static string EmbedInDescription(string? baseDescription, Manifest manifest)
    {
        var stripped = StripManifestLine(baseDescription);
        if (string.IsNullOrEmpty(stripped))
            return manifest.ToSentinelLine();
        return stripped.TrimEnd('\r', '\n', ' ', '\t') + "\n" + manifest.ToSentinelLine();
    }

    public static string StripManifestLine(string? description)
    {
        if (string.IsNullOrEmpty(description))
            return "";
        var idx = description.IndexOf(ManifestSentinel, StringComparison.Ordinal);
        if (idx < 0)
            return description;
        var lineStart = idx == 0 ? -1 : description.LastIndexOf('\n', idx - 1);
        var prefix = lineStart < 0 ? "" : description[..lineStart];
        return prefix.TrimEnd('\r', '\n', ' ', '\t');
    }

    public static bool DescriptionContainsManifest(string? description)
    {
        return !string.IsNullOrEmpty(description) &&
               description.IndexOf(ManifestSentinel, StringComparison.Ordinal) >= 0;
    }

    public static Manifest? TryParseFromDescription(string? description)
    {
        if (string.IsNullOrEmpty(description))
            return null;
        var idx = description.IndexOf(ManifestSentinel, StringComparison.Ordinal);
        if (idx < 0)
            return null;
        var jsonStart = idx + ManifestSentinel.Length;
        if (jsonStart >= description.Length)
            return null;
        var endIdx = description.IndexOf('\n', jsonStart);
        var json = (endIdx < 0 ? description[jsonStart..] : description[jsonStart..endIdx]).Trim();
        return TryParseManifestJson(json);
    }

    public static Manifest? TryParseManifestJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject node)
                return null;
            return new Manifest(
                SessionId: GetStringField(node, "session_id"),
                Mode: GetStringField(node, "mode", "solo"),
                Stage: GetStringField(node, "stage"),
                Intent: GetStringField(node, "intent"),
                RulePreset: GetStringField(node, "rules"),
                TauntCount: GetIntField(node, "taunt"),
                InfectionCount: GetIntField(node, "infection"),
                CurseCount: GetIntField(node, "curse"),
                RecoveryCount: GetIntField(node, "recovery"),
                RuntimeVersion: GetIntField(node, "runtime_ver"),
                TimestampUtc: TryParseDate(GetStringField(node, "ts")));
        }
        catch
        {
            return null;
        }
    }

    private static string GetStringField(JsonObject node, string key, string fallback = "")
    {
        try { return node[key]?.GetValue<string>() ?? fallback; }
        catch { return fallback; }
    }

    private static int GetIntField(JsonObject node, string key)
    {
        try { return node[key]?.GetValue<int>() ?? 0; }
        catch { return 0; }
    }

    private static DateTime TryParseDate(string value)
    {
        return DateTime.TryParse(
            value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : DateTime.MinValue;
    }
}
