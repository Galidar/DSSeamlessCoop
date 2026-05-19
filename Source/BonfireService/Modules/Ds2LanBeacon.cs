/*
 * Plan v3 Track A — item-driven LAN discovery beacon.
 *
 * Goal: a host that opens a session in-game (Blessed Eye Orb fires
 * `session.create`) should be visible to every Bonfire on the same
 * local network WITHOUT going through the master server. A guest
 * that uses the Crystal Eye Orb in-game can then auto-pick that
 * host as the JoinTarget — no UI clicks, no manual address entry.
 *
 * Mechanism: UDP multicast on `239.255.42.42:50032`. The host
 * sends a small JSON line every BroadcastInterval; every Bonfire
 * runs a listener that keeps a TTL-bounded cache of recent
 * beacons. Multicast (rather than broadcast 255.255.255.255) is
 * polite to non-Bonfire subnets — routers and Wi-Fi APs that
 * filter unrelated broadcast traffic still propagate
 * administratively-scoped multicast within the local segment.
 *
 * Beacon payload (UTF-8 JSON, small enough for one UDP packet):
 *
 *   {
 *     "kind"          : "ds2-bonfire-beacon-v1",
 *     "session_id"    : "<uuid>",
 *     "host_endpoint" : "192.168.1.10:50031",   // pose bridge/debug
 *     "server_id"     : "<master-server-id>",
 *     "hostname"      : "190.114.x.x",
 *     "private_hostname": "192.168.1.10",
 *     "login_port"    : 50050,
 *     "game_type"     : "DarkSouls2",
 *     "invite_kind"   : "saponita_direct",
 *     "host_name"     : "Galidar's Fire",
 *     "started_at_utc": "2026-05-17T03:00:00Z",
 *     "sender_id"     : 1234567890,
 *     "sequence"      : 42
 *   }
 *
 * The listener runs from BonfireService startup so a guest can
 * pick up host beacons even before the orb fires. The broadcaster
 * starts on `session.create` and stops on `session.leave`.
 */

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bonfire.Service.Modules;

public static class Ds2LanBeacon
{
    public const string Kind = "ds2-bonfire-beacon-v1";
    public const string MulticastAddress = "239.255.42.42";
    public const int Port = 50032;
    public static readonly TimeSpan BroadcastInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan BeaconTtl = TimeSpan.FromSeconds(8);

    private static readonly object Lock = new();
    private static CancellationTokenSource? _cts;
    private static Task? _broadcastTask;
    private static Task? _listenerTask;
    private static UdpClient? _broadcaster;
    private static UdpClient? _listener;
    private static BeaconInfo? _hostBeacon;
    private static long _broadcastSequence;
    private static long _broadcastCount;
    private static long _receivedCount;
    private static long _droppedCount;

    private static readonly ConcurrentDictionary<string, CachedBeacon> _cache = new();

    public sealed record BeaconInfo(
        string SessionId,
        string HostEndpoint,
        string HostName,
        DateTime StartedAtUtc,
        long SenderId,
        string ServerId,
        string Hostname,
        string PrivateHostname,
        int LoginPort,
        string GameType,
        bool PasswordRequired,
        string InviteKind);

    public sealed record CachedBeacon(
        string SessionId,
        string HostEndpoint,
        string HostName,
        DateTime StartedAtUtc,
        long SenderId,
        string ServerId,
        string Hostname,
        string PrivateHostname,
        int LoginPort,
        string GameType,
        bool PasswordRequired,
        string InviteKind,
        long Sequence,
        IPAddress SourceIp,
        DateTime FirstSeenUtc,
        DateTime LastSeenUtc,
        long SeenCount);

    // ── Listener (always-on) ─────────────────────────────────────────

    /// <summary>
    /// Starts the multicast listener idempotently. Called once on
    /// BonfireService boot so the local cache fills before the user
    /// touches an orb.
    /// </summary>
    public static void EnsureListenerRunning()
    {
        lock (Lock)
        {
            if (_listenerTask is not null) return;
            try
            {
                _cts ??= new CancellationTokenSource();
                _listener = new UdpClient(AddressFamily.InterNetwork);
                _listener.Client.SetSocketOption(
                    SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listener.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
                _listener.JoinMulticastGroup(IPAddress.Parse(MulticastAddress));
                _listenerTask = Task.Run(() => ListenerLoop(_cts.Token));
                DebugLog($"EnsureListenerRunning: bound :{Port} group={MulticastAddress}");
            }
            catch (Exception ex)
            {
                DebugLog($"EnsureListenerRunning failed: {ex.GetType().Name}: {ex.Message}");
                try { _listener?.Dispose(); } catch { }
                _listener = null;
                _listenerTask = null;
            }
        }
    }

    private static async Task ListenerLoop(CancellationToken ct)
    {
        DebugLog("ListenerLoop: started");
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            try
            {
                var result = await _listener.ReceiveAsync(ct);
                System.Threading.Interlocked.Increment(ref _receivedCount);
                if (!TryParseBeacon(result.Buffer, out var beacon, out var sequence))
                {
                    System.Threading.Interlocked.Increment(ref _droppedCount);
                    continue;
                }
                // Drop our own beacons — the multicast loopback bit is
                // on by default on Windows, which means every host
                // also receives its own broadcasts.
                if (_hostBeacon is { } self && beacon.SenderId == self.SenderId)
                {
                    continue;
                }

                var now = DateTime.UtcNow;
                _cache.AddOrUpdate(
                    beacon.SessionId,
                    _ => new CachedBeacon(
                        beacon.SessionId, beacon.HostEndpoint, beacon.HostName,
                        beacon.StartedAtUtc, beacon.SenderId,
                        beacon.ServerId, beacon.Hostname, beacon.PrivateHostname,
                        beacon.LoginPort, beacon.GameType, beacon.PasswordRequired,
                        beacon.InviteKind, sequence,
                        result.RemoteEndPoint.Address, now, now, 1),
                    (_, prev) => new CachedBeacon(
                        beacon.SessionId, beacon.HostEndpoint, beacon.HostName,
                        beacon.StartedAtUtc, beacon.SenderId,
                        beacon.ServerId, beacon.Hostname, beacon.PrivateHostname,
                        beacon.LoginPort, beacon.GameType, beacon.PasswordRequired,
                        beacon.InviteKind, sequence,
                        result.RemoteEndPoint.Address, prev.FirstSeenUtc, now,
                        prev.SeenCount + 1));

                PruneCache(now);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                DebugLog($"ListenerLoop iter error: {ex.GetType().Name}: {ex.Message}");
                await Task.Delay(500, CancellationToken.None);
            }
        }
        DebugLog("ListenerLoop: exited");
    }

    private static bool TryParseBeacon(
        byte[] payload,
        out BeaconInfo beacon,
        out long sequence)
    {
        beacon = default!;
        sequence = 0;
        try
        {
            var node = JsonNode.Parse(payload);
            if (node is not JsonObject obj) return false;
            if (obj["kind"]?.GetValue<string>() != Kind) return false;

            var sessionId = obj["session_id"]?.GetValue<string>();
            var hostEndpoint = obj["host_endpoint"]?.GetValue<string>();
            var hostName = obj["host_name"]?.GetValue<string>() ?? "";
            var serverId = obj["server_id"]?.GetValue<string>() ?? "";
            var hostname = obj["hostname"]?.GetValue<string>() ?? "";
            var privateHostname = obj["private_hostname"]?.GetValue<string>() ?? "";
            var loginPort = obj["login_port"]?.GetValue<int>() ?? 0;
            var gameType = obj["game_type"]?.GetValue<string>() ?? "DarkSouls2";
            var passwordRequired =
                obj["password_required"]?.GetValue<bool>() ?? false;
            var inviteKind =
                obj["invite_kind"]?.GetValue<string>() ?? "saponita_direct";
            var startedRaw = obj["started_at_utc"]?.GetValue<string>();
            var senderId = obj["sender_id"]?.GetValue<long>() ?? 0L;
            sequence = obj["sequence"]?.GetValue<long>() ?? 0L;

            if (string.IsNullOrWhiteSpace(sessionId)) return false;
            if (string.IsNullOrWhiteSpace(hostEndpoint)) return false;
            DateTime started = DateTime.TryParse(
                startedRaw, null,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var dt) ? dt : DateTime.UtcNow;
            beacon = new BeaconInfo(
                sessionId!, hostEndpoint!, hostName, started, senderId,
                serverId, hostname, privateHostname, loginPort, gameType,
                passwordRequired, inviteKind);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void PruneCache(DateTime now)
    {
        foreach (var (sessionId, entry) in _cache)
        {
            if (now - entry.LastSeenUtc > BeaconTtl)
            {
                _cache.TryRemove(sessionId, out _);
            }
        }
    }

    // ── Host broadcaster ─────────────────────────────────────────────

    /// <summary>
    /// Starts (or replaces) the 1 Hz host beacon. The beacon advertises
    /// the host's local endpoint so any LAN-attached Bonfire can join
    /// without touching the master list.
    /// </summary>
    public static void StartHostBroadcast(
        string sessionId,
        string hostEndpoint,
        string hostName,
        long senderId,
        string serverId,
        string hostname,
        string privateHostname,
        int loginPort,
        string gameType,
        bool passwordRequired,
        string inviteKind)
    {
        lock (Lock)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) sessionId = Guid.NewGuid().ToString("D");
            if (string.IsNullOrWhiteSpace(hostEndpoint)) return;
            _hostBeacon = new BeaconInfo(
                sessionId, hostEndpoint, hostName ?? "",
                DateTime.UtcNow, senderId,
                serverId ?? "", hostname ?? "", privateHostname ?? "",
                loginPort, gameType ?? "DarkSouls2", passwordRequired,
                inviteKind ?? "saponita_direct");
            _broadcastSequence = 0;

            if (_broadcastTask is not null) return; // already running; payload updated

            _cts ??= new CancellationTokenSource();
            try
            {
                _broadcaster = new UdpClient(AddressFamily.InterNetwork);
                _broadcaster.Client.SetSocketOption(
                    SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _broadcaster.Client.SetSocketOption(
                    SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4);
                _broadcastTask = Task.Run(() => BroadcastLoop(_cts.Token));
                DebugLog($"StartHostBroadcast: session={sessionId} endpoint={hostEndpoint} name=\"{hostName}\"");
            }
            catch (Exception ex)
            {
                DebugLog($"StartHostBroadcast failed: {ex.GetType().Name}: {ex.Message}");
                try { _broadcaster?.Dispose(); } catch { }
                _broadcaster = null;
                _broadcastTask = null;
            }
        }
    }

    public static void StopHostBroadcast()
    {
        lock (Lock)
        {
            _hostBeacon = null;
            try { _broadcaster?.Dispose(); } catch { }
            _broadcaster = null;
            // _broadcastTask exits the loop next iteration when
            // _hostBeacon == null.
            _broadcastTask = null;
            DebugLog("StopHostBroadcast: cleared host beacon");
        }
    }

    private static async Task BroadcastLoop(CancellationToken ct)
    {
        var groupEp = new IPEndPoint(IPAddress.Parse(MulticastAddress), Port);
        DebugLog("BroadcastLoop: started");
        while (!ct.IsCancellationRequested)
        {
            BeaconInfo? snapshot;
            UdpClient? client;
            lock (Lock)
            {
                snapshot = _hostBeacon;
                client = _broadcaster;
            }
            if (snapshot is null || client is null)
            {
                // Host broadcast cleared — exit.
                break;
            }
            try
            {
                var seq = System.Threading.Interlocked.Increment(ref _broadcastSequence);
                var node = new JsonObject
                {
                    ["kind"] = Kind,
                    ["session_id"] = snapshot.SessionId,
                    ["host_endpoint"] = snapshot.HostEndpoint,
                    ["host_name"] = snapshot.HostName,
                    ["server_id"] = snapshot.ServerId,
                    ["hostname"] = snapshot.Hostname,
                    ["private_hostname"] = snapshot.PrivateHostname,
                    ["login_port"] = snapshot.LoginPort,
                    ["game_type"] = snapshot.GameType,
                    ["password_required"] = snapshot.PasswordRequired,
                    ["invite_kind"] = snapshot.InviteKind,
                    ["started_at_utc"] = snapshot.StartedAtUtc.ToString("O"),
                    ["sender_id"] = snapshot.SenderId,
                    ["sequence"] = seq,
                };
                var bytes = Encoding.UTF8.GetBytes(node.ToJsonString());
                await client.SendAsync(bytes, bytes.Length, groupEp);
                System.Threading.Interlocked.Increment(ref _broadcastCount);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                DebugLog($"BroadcastLoop send error: {ex.GetType().Name}: {ex.Message}");
            }
            try { await Task.Delay(BroadcastInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
        DebugLog("BroadcastLoop: exited");
    }

    // ── Query API ────────────────────────────────────────────────────

    /// <summary>
    /// Return a snapshot of currently-known beacons, newest first.
    /// Caller must not rely on `SourceIp` matching `HostEndpoint`
    /// hostname — `HostEndpoint` is what the host claims, `SourceIp`
    /// is where the packet actually came from. We use the latter as
    /// the canonical address for the pose bridge to avoid host-side
    /// misconfiguration breaking the auto-join.
    /// </summary>
    public static IReadOnlyList<CachedBeacon> GetRecentBeacons()
    {
        PruneCache(DateTime.UtcNow);
        return _cache.Values
            .OrderByDescending(b => b.LastSeenUtc)
            .ToList();
    }

    /// <summary>
    /// Pick the freshest non-self beacon to auto-arm as a JoinTarget.
    /// Returns null when the cache is empty (guest should fall back
    /// to the Bonfire UI / master list path).
    /// </summary>
    public static CachedBeacon? PickStrongest()
    {
        var now = DateTime.UtcNow;
        return _cache.Values
            .Where(b => (now - b.LastSeenUtc) < BeaconTtl)
            .OrderByDescending(b => b.LastSeenUtc)
            .ThenByDescending(b => b.SeenCount)
            .FirstOrDefault();
    }

    public static JsonObject Status()
    {
        var listening = _listenerTask is not null;
        var broadcasting = _hostBeacon is not null;
        var beacons = new JsonArray();
        foreach (var b in GetRecentBeacons())
        {
            beacons.Add(new JsonObject
            {
                ["session_id"] = b.SessionId,
                ["host_endpoint"] = b.HostEndpoint,
                ["host_name"] = b.HostName,
                ["server_id"] = b.ServerId,
                ["hostname"] = b.Hostname,
                ["private_hostname"] = b.PrivateHostname,
                ["login_port"] = b.LoginPort,
                ["game_type"] = b.GameType,
                ["password_required"] = b.PasswordRequired,
                ["invite_kind"] = b.InviteKind,
                ["sender_id"] = b.SenderId,
                ["sequence"] = b.Sequence,
                ["source_ip"] = b.SourceIp.ToString(),
                ["age_ms"] = (DateTime.UtcNow - b.LastSeenUtc).TotalMilliseconds,
                ["seen_count"] = b.SeenCount,
            });
        }
        return new JsonObject
        {
            ["listening"] = listening,
            ["broadcasting"] = broadcasting,
            ["multicast_address"] = MulticastAddress,
            ["port"] = Port,
            ["broadcast_count"] = _broadcastCount,
            ["received_count"] = _receivedCount,
            ["dropped_count"] = _droppedCount,
            ["host_beacon"] = _hostBeacon is { } hb
                ? new JsonObject
                {
                    ["session_id"] = hb.SessionId,
                    ["host_endpoint"] = hb.HostEndpoint,
                    ["host_name"] = hb.HostName,
                    ["server_id"] = hb.ServerId,
                    ["hostname"] = hb.Hostname,
                    ["private_hostname"] = hb.PrivateHostname,
                    ["login_port"] = hb.LoginPort,
                    ["game_type"] = hb.GameType,
                    ["password_required"] = hb.PasswordRequired,
                    ["invite_kind"] = hb.InviteKind,
                    ["sender_id"] = hb.SenderId,
                    ["started_at_utc"] = hb.StartedAtUtc.ToString("O"),
                }
                : null,
            ["recent_beacons"] = beacons,
        };
    }

    public static void ShutdownAll()
    {
        lock (Lock)
        {
            try { _cts?.Cancel(); } catch { }
            try { _broadcaster?.Dispose(); } catch { }
            try { _listener?.Dispose(); } catch { }
            _broadcaster = null;
            _listener = null;
            _broadcastTask = null;
            _listenerTask = null;
            _hostBeacon = null;
            _cts = null;
            _cache.Clear();
        }
    }

    // ── Debug ────────────────────────────────────────────────────────

    private static readonly object DebugLogLock = new();
    private static string DebugLogPath =>
        Path.Combine(Paths.InstallRoot, "Runtime", "DS2Native", "lan_beacon.debug.log");
    private static void DebugLog(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DebugLogPath)!);
            var line = $"{DateTime.UtcNow:O}  {message}{Environment.NewLine}";
            lock (DebugLogLock)
            {
                File.AppendAllText(DebugLogPath, line, new UTF8Encoding(false));
            }
        }
        catch
        {
            // Best effort.
        }
    }
}
