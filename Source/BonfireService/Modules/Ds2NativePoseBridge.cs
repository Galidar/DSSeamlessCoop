/*
 * Ds2NativePoseBridge — Phase 4c of the HKMP-style overlay (2026-05-16).
 *
 * Closes the loop between the host's Injector and peer Injectors over a
 * UDP backbone:
 *
 *           (this PC)                             (peer PC)
 *
 *   DS2 ── Injector ── events.jsonl          events.jsonl ── Injector ── DS2
 *               (writes player.live_transform)
 *                          │                            ▲
 *                          ▼                            │
 *                Ds2NativePoseBridge          Ds2NativePoseBridge
 *                          │                            ▲
 *                          ▼  UDP unicast 32-byte pkt   │
 *                          └──────────────────────────►─┘
 *                          ◄──────────────────────────  │
 *                          │                            │
 *                          ▼                            ▼
 *                commands.jsonl                 commands.jsonl
 *                (render.set_peer_poses)        (render.set_peer_poses)
 *
 * Wire format (little-endian, fixed 32 bytes — same on both ends):
 *
 *   offset  size  field
 *      0     4    magic = 0x42_4E_43_42 ('BNCB' big-endian / ascii)
 *      4     1    version = 1
 *      5     1    reserved
 *      6     2    sequence
 *      8     8    sender_id  (hash of MachineName at startup, stable per process)
 *     16     4    px         (float, world-space)
 *     20     4    py
 *     24     4    pz
 *     28     4    yaw_radians
 *
 * Color is NOT transmitted — each peer is tinted client-side from
 * its sender_id so two peers get visually distinct cubes without
 * needing to agree on a colour scheme.
 *
 * Phase 4c MVP: peers are configured manually via the RPC method
 * `ds2_runtime.pose_bridge.start`. A future Phase 4d can do peer
 * discovery (LAN beacon, master server piggyback, etc.).
 */

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bonfire.Service.Modules;

public static class Ds2NativePoseBridge
{
    // ── Wire constants ───────────────────────────────────────────────
    private const uint Magic = 0x42_4E_43_42u; // 'BNCB'
    private const byte WireVersion = 1;
    private const int PacketSize = 32;

    // ── Timing knobs ─────────────────────────────────────────────────
    // Local pose poll: how often we re-read events.jsonl to look for a
    // new player.live_transform. v0 used 33 ms ≈ 30 Hz which produced
    // visible game slowdown — contention on events.jsonl writes from
    // the Injector side. v1 drops to 100 ms ≈ 10 Hz: still well above
    // human animation perception threshold, and now the file is mostly
    // closed from the bridge side when the Injector wants to write.
    private static readonly TimeSpan PosePollInterval = TimeSpan.FromMilliseconds(100);

    // Inbox write cadence: how often we re-publish the current peer
    // table to the local Injector. Bumped from 50 ms → 100 ms for the
    // same contention reasons as above; the Injector's command poll
    // loop is on a 1 s tick anyway, so faster writes are wasted.
    private static readonly TimeSpan InboxWriteInterval = TimeSpan.FromMilliseconds(100);

    // Peer TTL: drop a peer from the table if we haven't seen any
    // pose update from them in this window. Bigger = smoother on
    // jittery LAN; smaller = ghosts disappear faster when the peer
    // closes their client.
    private static readonly TimeSpan PeerTtl = TimeSpan.FromSeconds(1.5);

    // ── State ────────────────────────────────────────────────────────
    private static readonly object Lock = new();
    private static CancellationTokenSource? _cts;
    private static Task? _watcherTask;
    private static Task? _listenerTask;
    private static Task? _inboxWriterTask;
    // v1: separate UdpClient instances for send and receive. The single
    // shared client used in v0 deadlocked or got into an inconsistent
    // state under concurrent Send + ReceiveAsync because UdpClient is
    // explicitly NOT documented thread-safe. With two clients the
    // listener can stay parked on ReceiveAsync forever while the
    // broadcaster fires Send freely from the watcher thread.
    private static UdpClient? _udpRecv;
    private static UdpClient? _udpSend;
    private static int _localPort;
    private static long _localSenderId;
    private static readonly List<IPEndPoint> _peerEndpoints = new();
    private static readonly ConcurrentDictionary<long, PeerEntry> _peerTable = new();
    private static ushort _sequence;
    private static long _broadcastCount;
    private static long _receivedCount;
    private static long _droppedCount;

    // Pose source: latest valid pose read from events.jsonl by the
    // watcher. The broadcaster pulls from here at PosePollInterval.
    private static volatile PoseSample? _latestLocalPose;
    private static string? _lastEventLogPath;
    private static long _lastEventLogOffset;

    // Cached path resolution. v0 called Directory.EnumerateFiles on
    // every loop iteration (~20 times/sec across 3 loops) which —
    // combined with the Injector's own writes — caused noticeable
    // game slowdown. The session file path doesn't change during a
    // session, so we cache it for kSessionPathTtl and re-resolve
    // only when stale.
    private static readonly TimeSpan SessionPathTtl = TimeSpan.FromSeconds(2);
    private static string? _cachedEventLogPath;
    private static DateTime _cachedEventLogAt = DateTime.MinValue;
    private static string? _cachedCommandsInboxPath;

    private sealed record PeerEntry(
        long SenderId,
        float Px,
        float Py,
        float Pz,
        float YawRadians,
        DateTime LastSeenUtc);

    private sealed record PoseSample(
        float Px,
        float Py,
        float Pz,
        float YawRadians,
        DateTime SampledAtUtc);

    private static string RuntimeRoot =>
        Path.Combine(Paths.InstallRoot, "Runtime", "DS2Native");

    // ── Public API (called from Rpc/Methods.cs) ──────────────────────

    public static JsonObject Start(int localPort, IReadOnlyList<string> peerEndpoints)
    {
        lock (Lock)
        {
            if (_watcherTask is not null)
                return Status();

            _localPort = localPort > 0 ? localPort : 50031;
            // Receive socket — bound to the well-known port that peers
            // unicast their pose packets to.
            _udpRecv = new UdpClient(_localPort, AddressFamily.InterNetwork);
            _udpRecv.Client.ReceiveBufferSize = 1 << 16;
            // Send socket — ephemeral local port, just for outbound.
            // Keeping send/receive on separate sockets sidesteps
            // UdpClient's non-thread-safety guarantees.
            _udpSend = new UdpClient(AddressFamily.InterNetwork);
            _udpSend.Client.SendBufferSize = 1 << 16;
            DebugLog($"Start: recv bound on :{_localPort}, send on ephemeral port");

            _peerEndpoints.Clear();
            foreach (var raw in peerEndpoints)
            {
                if (TryParseEndpoint(raw, out var ep))
                    _peerEndpoints.Add(ep);
            }

            _localSenderId = StableMachineHash();
            _peerTable.Clear();
            _sequence = 0;
            _broadcastCount = 0;
            _receivedCount = 0;
            _droppedCount = 0;
            _latestLocalPose = null;
            _lastEventLogPath = null;
            _lastEventLogOffset = 0;
            _cachedEventLogPath = null;
            _cachedCommandsInboxPath = null;
            _cachedEventLogAt = DateTime.MinValue;

            _cts = new CancellationTokenSource();
            _watcherTask     = Task.Run(() => WatcherLoopAsync(_cts.Token));
            _listenerTask    = Task.Run(() => ListenerLoopAsync(_cts.Token));
            _inboxWriterTask = Task.Run(() => InboxWriterLoopAsync(_cts.Token));
        }
        return Status();
    }

    public static JsonObject Stop()
    {
        lock (Lock)
        {
            try { _cts?.Cancel(); } catch { }
            try { _udpRecv?.Dispose(); } catch { }
            try { _udpSend?.Dispose(); } catch { }
            _udpRecv = null;
            _udpSend = null;
            _watcherTask = null;
            _listenerTask = null;
            _inboxWriterTask = null;
            _cts = null;
            _peerTable.Clear();
            DebugLog("Stop: all sockets and tasks released");
        }
        return Status();
    }

    public static JsonObject Status()
    {
        var running = _watcherTask is not null;
        var peers = new JsonArray();
        foreach (var (_, entry) in _peerTable)
        {
            peers.Add(new JsonObject
            {
                ["sender_id"] = entry.SenderId,
                ["position"] = new JsonArray { entry.Px, entry.Py, entry.Pz },
                ["yaw_radians"] = entry.YawRadians,
                ["age_ms"] = (DateTime.UtcNow - entry.LastSeenUtc).TotalMilliseconds,
            });
        }

        var endpoints = new JsonArray();
        foreach (var ep in _peerEndpoints)
        {
            endpoints.Add(ep.ToString());
        }

        return new JsonObject
        {
            ["running"] = running,
            ["local_port"] = _localPort,
            ["local_sender_id"] = _localSenderId,
            ["broadcast_count"] = _broadcastCount,
            ["received_count"] = _receivedCount,
            ["dropped_count"] = _droppedCount,
            ["peer_endpoints"] = endpoints,
            ["peer_table"] = peers,
            ["latest_local_pose"] = _latestLocalPose is { } lp
                ? new JsonObject
                {
                    ["px"] = lp.Px, ["py"] = lp.Py, ["pz"] = lp.Pz,
                    ["yaw_radians"] = lp.YawRadians,
                    ["sampled_utc"] = lp.SampledAtUtc.ToString("O"),
                }
                : null,
        };
    }

    // ── Local pose watcher ───────────────────────────────────────────
    //
    // Tails the latest *.events.jsonl in the runtime dir and extracts
    // player.live_transform → updates _latestLocalPose. Same time as
    // it does that it ALSO broadcasts the new pose, so this is the
    // "outbound" half of the bridge.
    private static async Task WatcherLoopAsync(CancellationToken ct)
    {
        DebugLog("WatcherLoop: started");
        long iter = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var path = LatestEventLog();
                if (path != null && _lastEventLogPath != path)
                {
                    DebugLog($"WatcherLoop: switched session log to {Path.GetFileName(path)}");
                    _lastEventLogPath = path;
                    _lastEventLogOffset = 0;
                }
                if (_lastEventLogPath != null)
                {
                    ScanForNewPoses(_lastEventLogPath, ref _lastEventLogOffset);
                }

                if (_latestLocalPose is { } pose)
                {
                    BroadcastPose(pose);
                }
            }
            catch (Exception ex)
            {
                DebugLog($"WatcherLoop iter={iter} threw {ex.GetType().Name}: {ex.Message}");
            }
            iter++;
            if (iter % 100 == 0)
            {
                var p = _latestLocalPose;
                DebugLog($"WatcherLoop alive: iter={iter} hasPose={p != null} broadcast={_broadcastCount}");
            }
            try { await Task.Delay(PosePollInterval, ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { DebugLog($"WatcherLoop Task.Delay threw {ex.GetType().Name}: {ex.Message}"); break; }
        }
        DebugLog($"WatcherLoop: exiting (iter={iter})");
    }

    private static string? LatestEventLog()
    {
        // Cheap fast path: cached path still valid AND still recent.
        var now = DateTime.UtcNow;
        if (_cachedEventLogPath is not null &&
            now - _cachedEventLogAt < SessionPathTtl)
        {
            return _cachedEventLogPath;
        }

        if (!Directory.Exists(RuntimeRoot))
        {
            _cachedEventLogPath = null;
            _cachedCommandsInboxPath = null;
            _cachedEventLogAt = now;
            return null;
        }
        var newest = Directory.EnumerateFiles(RuntimeRoot, "*.events.jsonl")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (newest != _cachedEventLogPath)
        {
            // New session — invalidate the derived commands-inbox cache too.
            _cachedEventLogPath = newest;
            _cachedCommandsInboxPath = null;
        }
        _cachedEventLogAt = now;
        return _cachedEventLogPath;
    }

    private static void ScanForNewPoses(string path, ref long offset)
    {
        long size;
        try { size = new FileInfo(path).Length; }
        catch { return; }
        if (size < offset) { offset = 0; }      // file truncated/rotated
        if (size == offset) { return; }

        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        // Re-read line by line — we only care about the LATEST
        // player.live_transform in this scan window so the loop can
        // overwrite _latestLocalPose freely.
        string? line;
        long newOffset = offset;
        while ((line = reader.ReadLine()) != null)
        {
            newOffset += Encoding.UTF8.GetByteCount(line) + 1; // +1 for newline
            if (line.IndexOf("\"player.live_transform\"", StringComparison.Ordinal) < 0)
                continue;
            JsonNode? node;
            try { node = JsonNode.Parse(line); } catch { continue; }
            if (node is not JsonObject obj) continue;
            if (obj["data"] is not JsonObject data) continue;
            if (data["resolved"]?.GetValue<bool>() != true) continue;

            var px = TryFloat(data, "px");
            var py = TryFloat(data, "py");
            var pz = TryFloat(data, "pz");
            if (px is null || py is null || pz is null) continue;

            // Yaw — derived from the two candidate cos/sin pairs
            // exposed at offsets 0x60 / 0x80 of chr (see §13 of
            // HKMP_OVERLAY_RESEARCH.md). The Injector publishes the
            // raw candidates without disambiguating, so we use the
            // first matched cos/sin pair.
            float yaw = 0f;
            if (data["yaw_candidates"] is JsonArray yc && yc.Count >= 2)
            {
                var cos = yc[0]?["val"]?.GetValue<double>();
                var sin = yc[1]?["val"]?.GetValue<double>();
                if (cos.HasValue && sin.HasValue)
                {
                    yaw = (float)Math.Atan2(sin.Value, cos.Value);
                }
            }

            _latestLocalPose = new PoseSample(
                px.Value, py.Value, pz.Value, yaw, DateTime.UtcNow);
        }
        offset = newOffset;
    }

    // ── UDP broadcaster (called from the watcher loop) ───────────────
    private static void BroadcastPose(PoseSample pose)
    {
        if (_udpSend is null || _peerEndpoints.Count == 0) return;

        Span<byte> packet = stackalloc byte[PacketSize];
        BinaryPrimitives.WriteUInt32LittleEndian(packet[0..4], Magic);
        packet[4] = WireVersion;
        packet[5] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(packet[6..8], _sequence++);
        BinaryPrimitives.WriteInt64LittleEndian(packet[8..16], _localSenderId);
        BinaryPrimitives.WriteSingleLittleEndian(packet[16..20], pose.Px);
        BinaryPrimitives.WriteSingleLittleEndian(packet[20..24], pose.Py);
        BinaryPrimitives.WriteSingleLittleEndian(packet[24..28], pose.Pz);
        BinaryPrimitives.WriteSingleLittleEndian(packet[28..32], pose.YawRadians);

        var bytes = packet.ToArray();
        foreach (var ep in _peerEndpoints)
        {
            try
            {
                _udpSend.Send(bytes, bytes.Length, ep);
                Interlocked.Increment(ref _broadcastCount);
            }
            catch (Exception ex)
            {
                // peer unreachable — fine, UDP is fire-and-forget.
                DebugLog($"BroadcastPose send to {ep} failed: {ex.GetType().Name} {ex.Message}");
            }
        }
    }

    // ── UDP listener ─────────────────────────────────────────────────
    private static async Task ListenerLoopAsync(CancellationToken ct)
    {
        if (_udpRecv is null) return;
        DebugLog("ListenerLoop: started");
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udpRecv.ReceiveAsync(ct);
            }
            catch (OperationCanceledException) { DebugLog("ListenerLoop: cancelled"); break; }
            catch (ObjectDisposedException) { DebugLog("ListenerLoop: socket disposed"); break; }
            catch (Exception ex)
            {
                DebugLog($"ListenerLoop ReceiveAsync threw {ex.GetType().Name}: {ex.Message}");
                Interlocked.Increment(ref _droppedCount);
                continue;
            }

            if (result.Buffer.Length != PacketSize)
            {
                Interlocked.Increment(ref _droppedCount);
                continue;
            }
            var pkt = result.Buffer.AsSpan();
            var magic = BinaryPrimitives.ReadUInt32LittleEndian(pkt[0..4]);
            if (magic != Magic || pkt[4] != WireVersion)
            {
                Interlocked.Increment(ref _droppedCount);
                continue;
            }
            var senderId = BinaryPrimitives.ReadInt64LittleEndian(pkt[8..16]);

            var px  = BinaryPrimitives.ReadSingleLittleEndian(pkt[16..20]);
            var py  = BinaryPrimitives.ReadSingleLittleEndian(pkt[20..24]);
            var pz  = BinaryPrimitives.ReadSingleLittleEndian(pkt[24..28]);
            var yaw = BinaryPrimitives.ReadSingleLittleEndian(pkt[28..32]);

            // Loopback visual nudge: when we receive our OWN packet
            // (the only way this happens in real deployments is the
            // single-PC loopback test where peers={127.0.0.1:<our
            // port>}) we shift the position +5 m on X so the ghost
            // cube doesn't z-fight with the host's own magenta cube.
            // On a real LAN this branch is never hit — senderId is
            // derived from MachineName/ProcessId so each PC's id is
            // distinct.
            if (senderId == _localSenderId)
            {
                px += 5.0f;
            }

            _peerTable[senderId] = new PeerEntry(
                senderId, px, py, pz, yaw, DateTime.UtcNow);
            Interlocked.Increment(ref _receivedCount);
        }
    }

    // ── Inbox writer ─────────────────────────────────────────────────
    //
    // Drains the (TTL-pruned) peer table into a single
    // render.set_peer_poses command appended to commands.jsonl, at
    // InboxWriteInterval. Picks the color deterministically from the
    // sender_id so two peers always get visually distinct cubes.
    private static async Task InboxWriterLoopAsync(CancellationToken ct)
    {
        DebugLog("InboxWriterLoop: started");
        long iter = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                WritePeerTableToInbox();
            }
            catch (Exception ex)
            {
                DebugLog($"InboxWriterLoop iter={iter} WritePeerTableToInbox threw {ex.GetType().Name}: {ex.Message}");
            }
            iter++;
            // Periodic heartbeat — confirms the loop is alive even
            // when commands.jsonl writes are silent.
            if (iter % 100 == 0)
            {
                DebugLog($"InboxWriterLoop alive: iter={iter} peers={_peerTable.Count} broadcast={_broadcastCount} received={_receivedCount}");
            }
            try { await Task.Delay(InboxWriteInterval, ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { DebugLog($"InboxWriterLoop Task.Delay threw {ex.GetType().Name}: {ex.Message}"); break; }
        }
        DebugLog($"InboxWriterLoop: exiting (iter={iter})");
    }

    private static void WritePeerTableToInbox()
    {
        var commandsInbox = LatestCommandsInbox();
        if (commandsInbox is null) return;

        // Prune expired peers.
        var now = DateTime.UtcNow;
        foreach (var (id, entry) in _peerTable)
        {
            if (now - entry.LastSeenUtc > PeerTtl)
            {
                _peerTable.TryRemove(id, out _);
            }
        }

        // Build the command. Even when the table is empty we still
        // write a "clear" command — keeps the renderer in sync if a
        // peer just disconnected.
        var peers = new JsonArray();
        foreach (var (_, entry) in _peerTable)
        {
            var (r, g, b) = ColorFromSenderId(entry.SenderId);
            peers.Add(new JsonObject
            {
                ["position"] = new JsonArray { entry.Px, entry.Py, entry.Pz },
                ["yaw_radians"] = entry.YawRadians,
                ["color"] = new JsonArray { r, g, b },
                ["valid"] = true,
            });
        }
        // ToJsonString() without options uses the default compact
        // writer — same convention as Ds2NativeRuntimeBridge.SendCommand
        // and friends. Passing JsonSerializerOptions here breaks under
        // PublishSingleFile because the JSON source generator hasn't
        // emitted metadata for our anon JsonObject shape.
        var line = new JsonObject
        {
            ["command"] = "render.set_peer_poses",
            ["peers"] = peers,
        }.ToJsonString();

        // Use UTF-8 no-BOM and append a single newline so the
        // Injector's std::getline picks the line cleanly. Multiple
        // writes per second are OK — the Injector's PollCommandInbox
        // is incremental.
        File.AppendAllText(commandsInbox, line + "\n", new UTF8Encoding(false));
    }

    private static string? LatestCommandsInbox()
    {
        // LatestEventLog manages its own TTL; we just piggyback on
        // its cache invalidation to know when to recompute.
        var eventLog = LatestEventLog();
        if (eventLog is null) return null;
        if (_cachedCommandsInboxPath is not null) return _cachedCommandsInboxPath;

        var fileName = Path.GetFileName(eventLog);
        if (!fileName.EndsWith(".events.jsonl", StringComparison.OrdinalIgnoreCase))
            return null;
        var stem = fileName[..^".events.jsonl".Length];
        _cachedCommandsInboxPath = Path.Combine(
            Path.GetDirectoryName(eventLog) ?? RuntimeRoot,
            stem + ".commands.jsonl");
        return _cachedCommandsInboxPath;
    }

    // ── Helpers ──────────────────────────────────────────────────────

    // Append-only debug log so we can diagnose the bridge from
    // outside without RPC stdin access. Each line is timestamped UTC
    // ISO. Path is well-known + sibling of the runtime root. Best
    // effort — never throws.
    private static readonly object DebugLogLock = new();
    private static string DebugLogPath =>
        Path.Combine(Paths.InstallRoot, "Runtime", "DS2Native", "pose_bridge.debug.log");
    private static void DebugLog(string message)
    {
        try
        {
            lock (DebugLogLock)
            {
                File.AppendAllText(
                    DebugLogPath,
                    $"{DateTime.UtcNow:O} {message}\n",
                    new UTF8Encoding(false));
            }
        }
        catch { /* swallow */ }
    }

    private static long StableMachineHash()
    {
        // Mix machine name + user name + process PID so two instances
        // on the same machine (e.g. loopback test with two
        // BonfireService processes) still get distinct sender IDs.
        var seed = Environment.MachineName + "|" +
                   Environment.UserName + "|" +
                   Environment.ProcessId;
        unchecked
        {
            long hash = 1469598103934665603L; // FNV-1a 64-bit offset basis
            foreach (var ch in seed)
            {
                hash ^= ch;
                hash *= 1099511628211L;
            }
            return hash;
        }
    }

    private static bool TryParseEndpoint(string raw, out IPEndPoint endpoint)
    {
        endpoint = new IPEndPoint(IPAddress.None, 0);
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var idx = raw.LastIndexOf(':');
        if (idx <= 0) return false;
        var host = raw[..idx];
        var portStr = raw[(idx + 1)..];
        if (!int.TryParse(portStr, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var port))
        {
            return false;
        }
        if (!IPAddress.TryParse(host, out var ip))
        {
            // Allow hostnames — resolve synchronously, ok at start time.
            try
            {
                var addrs = Dns.GetHostAddresses(host);
                ip = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (ip is null) return false;
            }
            catch { return false; }
        }
        endpoint = new IPEndPoint(ip, port);
        return true;
    }

    private static float? TryFloat(JsonObject obj, string key)
    {
        try
        {
            var v = obj[key];
            if (v is null) return null;
            return (float)v.GetValue<double>();
        }
        catch { return null; }
    }

    // Deterministic HSV → RGB color from sender_id so the same peer
    // always shows up with the same tint across both ends. The hash
    // is mixed with the golden ratio constant to spread hues evenly
    // across the colour wheel for small populations.
    private static (float r, float g, float b) ColorFromSenderId(long senderId)
    {
        var bucket = unchecked((uint)senderId) * 2654435769u;
        var hue = (bucket % 360u) / 360.0f;
        return HsvToRgb(hue, 0.85f, 1.0f);
    }

    private static (float r, float g, float b) HsvToRgb(float h, float s, float v)
    {
        float c = v * s;
        float x = c * (1 - Math.Abs((h * 6f) % 2f - 1));
        float m = v - c;
        float r1, g1, b1;
        var sextant = (int)Math.Floor(h * 6f) % 6;
        switch (sextant)
        {
            case 0: r1 = c; g1 = x; b1 = 0; break;
            case 1: r1 = x; g1 = c; b1 = 0; break;
            case 2: r1 = 0; g1 = c; b1 = x; break;
            case 3: r1 = 0; g1 = x; b1 = c; break;
            case 4: r1 = x; g1 = 0; b1 = c; break;
            default: r1 = c; g1 = 0; b1 = x; break;
        }
        return (r1 + m, g1 + m, b1 + m);
    }
}
