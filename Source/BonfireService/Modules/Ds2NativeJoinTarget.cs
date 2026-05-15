using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bonfire.Service.Modules;

/// <summary>
/// One-shot "join target" arming for DS2 Native Sessions. When the Flutter
/// browser clicks Join on a peer Bonfire's session, <see cref="Set"/> stores
/// the target hostname/port/public-key for the next <c>game.launch_local</c>;
/// the launch flow calls <see cref="Consume"/> to read-and-clear so a single
/// arming maps to a single launch.
///
/// Persistence is best-effort to a JSON sidecar under
/// <c>Runtime/DS2Native/join_target.json</c>, so a BonfireService restart
/// between arming and launching does not lose the user's selection.
/// </summary>
public static class Ds2NativeJoinTarget
{
    private static readonly object Lock = new();
    private static JoinTarget? _current;
    private static bool _diskLoadedAttempted;

    private static string PersistencePath =>
        Path.Combine(Paths.InstallRoot, "Runtime", "DS2Native", "join_target.json");

    public sealed record JoinTarget(
        string ServerId,
        string ServerName,
        string Hostname,
        string PrivateHostname,
        int Port,
        string Password,
        string GameType,
        string SessionId,
        string SessionMode,
        DateTime ArmedAtUtc);

    public static JoinTarget? Get()
    {
        lock (Lock)
        {
            if (!_diskLoadedAttempted)
            {
                _diskLoadedAttempted = true;
                _current ??= TryLoadFromDisk();
            }
            return _current;
        }
    }

    public static void Set(JoinTarget target)
    {
        lock (Lock)
        {
            _current = target;
            _diskLoadedAttempted = true;
            PersistToDisk(target);
        }
    }

    /// <summary>
    /// Reads the current target and clears it atomically. Returned target
    /// should be consumed exactly once by <c>game.launch_local</c>.
    /// </summary>
    public static JoinTarget? Consume()
    {
        lock (Lock)
        {
            if (!_diskLoadedAttempted)
            {
                _diskLoadedAttempted = true;
                _current ??= TryLoadFromDisk();
            }
            var t = _current;
            _current = null;
            TryDeleteDiskFile();
            return t;
        }
    }

    public static void Clear()
    {
        lock (Lock)
        {
            _current = null;
            _diskLoadedAttempted = true;
            TryDeleteDiskFile();
        }
    }

    public static JsonObject? ToJson(JoinTarget? t)
    {
        if (t is null) return null;
        return new JsonObject
        {
            ["server_id"] = t.ServerId,
            ["server_name"] = t.ServerName,
            ["hostname"] = t.Hostname,
            ["private_hostname"] = t.PrivateHostname,
            ["port"] = t.Port,
            ["password_set"] = !string.IsNullOrEmpty(t.Password),
            ["game_type"] = t.GameType,
            ["session_id"] = t.SessionId,
            ["session_mode"] = t.SessionMode,
            ["armed_at_utc"] = t.ArmedAtUtc.ToString("O"),
        };
    }

    private static JoinTarget? TryLoadFromDisk()
    {
        try
        {
            if (!File.Exists(PersistencePath)) return null;
            var raw = File.ReadAllText(PersistencePath);
            var node = JsonNode.Parse(raw) as JsonObject;
            if (node is null) return null;
            return new JoinTarget(
                ServerId: GetString(node, "server_id"),
                ServerName: GetString(node, "server_name"),
                Hostname: GetString(node, "hostname"),
                PrivateHostname: GetString(node, "private_hostname"),
                Port: GetInt(node, "port"),
                Password: GetString(node, "password"),
                GameType: GetString(node, "game_type"),
                SessionId: GetString(node, "session_id"),
                SessionMode: GetString(node, "session_mode"),
                ArmedAtUtc: DateTime.TryParse(
                    GetString(node, "armed_at_utc"), null,
                    System.Globalization.DateTimeStyles.AssumeUniversal |
                    System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var dt) ? dt : DateTime.UtcNow);
        }
        catch
        {
            return null;
        }
    }

    private static void PersistToDisk(JoinTarget target)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PersistencePath)!);
            // Persist password verbatim so a service restart between arm and
            // launch does not require the user to retype it. The sidecar
            // lives next to the rest of the per-session runtime files which
            // are already considered local-only state.
            var node = new JsonObject
            {
                ["server_id"] = target.ServerId,
                ["server_name"] = target.ServerName,
                ["hostname"] = target.Hostname,
                ["private_hostname"] = target.PrivateHostname,
                ["port"] = target.Port,
                ["password"] = target.Password,
                ["game_type"] = target.GameType,
                ["session_id"] = target.SessionId,
                ["session_mode"] = target.SessionMode,
                ["armed_at_utc"] = target.ArmedAtUtc.ToString("O"),
            };
            File.WriteAllText(PersistencePath, node.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
            }));
        }
        catch
        {
            // Best effort. In-memory state is the authoritative copy.
        }
    }

    private static void TryDeleteDiskFile()
    {
        try
        {
            if (File.Exists(PersistencePath)) File.Delete(PersistencePath);
        }
        catch
        {
        }
    }

    private static string GetString(JsonNode? node, string key)
    {
        try { return node?[key]?.GetValue<string>() ?? ""; }
        catch { return ""; }
    }

    private static int GetInt(JsonNode? node, string key)
    {
        try { return node?[key]?.GetValue<int>() ?? 0; }
        catch { return 0; }
    }
}
