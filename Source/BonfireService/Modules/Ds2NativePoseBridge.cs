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
    private const uint Magic = 0x42_4E_43_42u; // 'BNCB' — pose packet
    private const byte WireVersion = 1;
    private const int PacketSize = 32;

    // Track C Phase 2A: char_data packet. Same envelope (magic + version
    // + sequence + sender_id) as the pose packet, then the heavier
    // fields. Sent at ~1 Hz from the new char-data publisher task; the
    // listener dispatches by magic.
    //
    // Wire format (little-endian, fixed 200 bytes):
    //
    //   offset  size  field
    //     0       4   magic = 0x42_4E_43_44 ('BNCD')
    //     4       1   version = 1
    //     5       1   reserved
    //     6       2   sequence
    //     8       8   sender_id
    //    16       4   hp_current
    //    20       4   hp_max_with_buffs
    //    24       4   hp_max_base
    //    28       4   equip_load_max          (f32)
    //    32       4   equip_weight_current    (f32)
    //    36       4   zone_primary
    //    40       4   zone_secondary
    //    44       4   is_phantom              (uint 0/1)
    //    48      64   name UTF-16 LE          (32 wchar, NUL-pad)
    //   112      88   equipment[22] × u32 item_id
    private const uint CharDataMagic = 0x42_4E_43_44u; // 'BNCD'
    private const int CharDataPacketSize = 200;
    private const int CharDataNameBytes = 64;
    private const int CharDataSlotCount = Ds2MemoryReader.EquipmentSlotCount; // 22

    // ── Timing knobs ─────────────────────────────────────────────────
    // Local pose poll: how often we re-read events.jsonl to look for a
    // new player.live_transform. v0 used 33 ms ≈ 30 Hz which produced
    // visible game slowdown — contention on events.jsonl writes from
    // the Injector side. v1 drops to 100 ms ≈ 10 Hz: still well above
    // human animation perception threshold, and now the file is mostly
    // closed from the bridge side when the Injector wants to write.
    private static readonly TimeSpan PosePollInterval = TimeSpan.FromMilliseconds(100);

    // Inbox write cadence: how often we re-publish the current peer
    // table.
    //
    // v2.9.1 (Plan v3 Track B): we now write to a shared-memory
    // ringbuffer (Ds2PoseShm) instead of only the commands.jsonl file.
    // The Injector reads SHM on a 5 ms cycle so we can re-publish at
    // 30 Hz (33 ms) without contention — the file path still ticks at
    // 100 ms as a debug/fallback channel for non-SHM consumers.
    private static readonly TimeSpan InboxWriteInterval = TimeSpan.FromMilliseconds(33);

    // Diagnostic: how often we drop a line into the debug log to confirm
    // the SHM path is alive. Computed as multiples of InboxWriteInterval
    // (so ~3 s between SHM heartbeats at 30 Hz).
    private const int ShmHeartbeatEveryIter = 90;

    // Track C Phase 2A: char_data poll/publish cadence. char_data
    // doesn't change frame-to-frame (HP only changes on damage, gear
    // changes on equip) so 1 Hz is plenty. Listener accepts updates
    // at any rate.
    private static readonly TimeSpan CharDataPublishInterval = TimeSpan.FromSeconds(1);

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
    // Track C Phase 2A — separate slow-cadence publisher for char_data.
    private static Task? _charDataPublisherTask;
    // Latest snapshot we read from local DS2 memory. Refreshed every
    // CharDataPublishInterval. Null when DS2 isn't running or we're
    // mid-area-transition.
    private static Ds2MemoryReader.Ds2CharSnapshot? _latestLocalCharSnapshot;
    private static ushort _charDataSequence;
    private static long _charDataBroadcastCount;
    private static long _charDataReceivedCount;
    private static long _charDataDroppedCount;
    // sender_id → last received PeerCharData. Pruned alongside the pose
    // peer table on the same TTL.
    private static readonly ConcurrentDictionary<long, PeerCharData> _peerCharData = new();
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

    // Track C Phase 2A peer char_data snapshot. Same shape as
    // Ds2MemoryReader.Ds2CharSnapshot but without position (pose
    // travels separately at 30 Hz) and with a LastSeen timestamp for
    // TTL pruning.
    private sealed record PeerCharData(
        long SenderId,
        bool IsPhantom,
        string Name,
        uint HpCurrent,
        uint HpMaxWithBuffs,
        uint HpMaxBase,
        float EquipLoadMax,
        float EquipWeightCurrent,
        uint ZonePrimary,
        uint ZoneSecondary,
        uint[] EquipmentSlots,
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

            // Plan v3 Track B: open the shared-memory peer-pose pipe.
            // Best-effort — if the OS refuses the mapping the bridge
            // still works via commands.jsonl fallback.
            var shmReady = Ds2PoseShm.TryOpen();
            DebugLog(shmReady
                ? $"Start: shared-memory pose pipe open at {Ds2PoseShm.MapName}"
                : "Start: shared-memory pose pipe FAILED to open — falling back to commands.jsonl only");

            // Track C Phase 2A: open the char_data SHM. Failure here is
            // non-fatal — the UDP path keeps working, the Injector just
            // won't see char_data fields until the section comes back.
            var charShmReady = Ds2CharDataShm.TryOpen();
            DebugLog(charShmReady
                ? $"Start: char_data pipe open at {Ds2CharDataShm.MapName}"
                : "Start: char_data pipe FAILED to open — char_data still rides UDP though");

            // Reset char_data state (matches the existing pose-state reset above).
            _latestLocalCharSnapshot = null;
            _charDataSequence = 0;
            _charDataBroadcastCount = 0;
            _charDataReceivedCount = 0;
            _charDataDroppedCount = 0;
            _peerCharData.Clear();

            _cts = new CancellationTokenSource();
            _watcherTask     = Task.Run(() => WatcherLoopAsync(_cts.Token));
            _listenerTask    = Task.Run(() => ListenerLoopAsync(_cts.Token));
            _inboxWriterTask = Task.Run(() => InboxWriterLoopAsync(_cts.Token));
            _charDataPublisherTask = Task.Run(() => CharDataPublisherLoopAsync(_cts.Token));
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
            _charDataPublisherTask = null;
            _cts = null;
            _peerTable.Clear();
            _peerCharData.Clear();
            _latestLocalCharSnapshot = null;
            // Plan v3 Track B: release the shared section so a clean
            // restart re-seeds the header (avoids stale generation
            // confusing a consumer that re-attaches mid-restart).
            Ds2PoseShm.Close();
            // Track C Phase 2A: same for char_data section.
            Ds2CharDataShm.Close();
            DebugLog("Stop: all sockets and tasks released");
        }
        return Status();
    }

    // Lightweight check used by Ds2NativeSessionCoordinator to gate
    // auto-start without needing a full Status() snapshot.
    public static bool IsRunning
    {
        get
        {
            lock (Lock)
            {
                return _watcherTask is not null;
            }
        }
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
            // Plan v3 Track B diagnostics — exposes whether the shared
            // section is alive and the current generation so the UI or
            // a debug RPC can show the user that low-latency IPC is in
            // play (instead of falling back to file polling).
            ["shm_open"] = Ds2PoseShm.IsOpen,
            ["shm_generation"] = Ds2PoseShm.LastGeneration,
            ["shm_map_name"] = Ds2PoseShm.MapName,

            // Track C Phase 2A — char_data round-trip diagnostics.
            ["char_data"] = BuildCharDataStatus(),
        };
    }

    private static JsonObject BuildCharDataStatus()
    {
        var peers = new JsonArray();
        foreach (var (_, e) in _peerCharData)
        {
            var slots = new JsonArray();
            foreach (var v in e.EquipmentSlots)
                slots.Add(v);
            peers.Add(new JsonObject
            {
                ["sender_id"]    = e.SenderId,
                ["is_phantom"]   = e.IsPhantom,
                ["name"]         = e.Name,
                ["hp_current"]   = e.HpCurrent,
                ["hp_max_buff"]  = e.HpMaxWithBuffs,
                ["hp_max_base"]  = e.HpMaxBase,
                ["equip_load_max"] = e.EquipLoadMax,
                ["equip_weight"]   = e.EquipWeightCurrent,
                ["zone_primary"]   = e.ZonePrimary,
                ["zone_secondary"] = e.ZoneSecondary,
                ["age_ms"]         = (DateTime.UtcNow - e.LastSeenUtc).TotalMilliseconds,
                ["equipment_slots"] = slots,
            });
        }

        JsonObject? localObj = null;
        if (_latestLocalCharSnapshot is { } local)
        {
            var slots = new JsonArray();
            foreach (var v in local.EquipmentSlots)
                slots.Add(v);
            localObj = new JsonObject
            {
                ["is_phantom"]   = local.IsPhantom,
                ["name"]         = local.Name,
                ["hp_current"]   = local.HpCurrent,
                ["hp_max_buff"]  = local.HpMaxWithBuffs,
                ["hp_max_base"]  = local.HpBaseMax,
                ["equip_load_max"] = local.EquipLoadMax,
                ["equip_weight"]   = local.EquipWeightCurrent,
                ["zone_primary"]   = local.ZonePrimary,
                ["zone_secondary"] = local.ZoneSecondary,
                ["equipment_slots"] = slots,
            };
        }

        return new JsonObject
        {
            ["shm_open"]         = Ds2CharDataShm.IsOpen,
            ["shm_generation"]   = Ds2CharDataShm.LastGeneration,
            ["shm_map_name"]     = Ds2CharDataShm.MapName,
            ["broadcast_count"]  = _charDataBroadcastCount,
            ["received_count"]   = _charDataReceivedCount,
            ["dropped_count"]    = _charDataDroppedCount,
            ["local_snapshot"]   = localObj,
            ["peer_snapshots"]   = peers,
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
                // v2.8.4: PRIMARY pose source — read DS2 process
                // memory directly via Ds2MemoryReader. Bypasses the
                // Injector's worker thread (which has a habit of
                // hanging on chain-walk failures during DS2's
                // pre-world loading), so the broadcaster runs even
                // when events.jsonl is frozen.
                if (Ds2MemoryReader.TryReadHostPose(out var px, out var py, out var pz))
                {
                    _latestLocalPose = new PoseSample(px, py, pz, 0f, DateTime.UtcNow);
                }
                else
                {
                    // Fallback: tail events.jsonl as before. Kept so
                    // Bonfire-only deployments without injector
                    // access (e.g. a future relay-only mode) still
                    // have a pose source. Also covers the small
                    // window before the AOB scan resolves.
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

            // Track C Phase 2A: dispatch by magic so the same socket
            // handles both pose (32-byte) and char_data (200-byte)
            // packets. Anything that doesn't match a known magic +
            // version + length is dropped.
            if (result.Buffer.Length < 8)
            {
                Interlocked.Increment(ref _droppedCount);
                continue;
            }
            var pktAll = result.Buffer.AsSpan();
            var magic = BinaryPrimitives.ReadUInt32LittleEndian(pktAll[0..4]);

            if (magic == CharDataMagic)
            {
                HandleCharDataPacket(result.Buffer, result.RemoteEndPoint);
                continue;
            }

            if (magic != Magic ||
                result.Buffer.Length != PacketSize ||
                pktAll[4] != WireVersion)
            {
                Interlocked.Increment(ref _droppedCount);
                continue;
            }
            var pkt = pktAll;
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

            // Auto-discovery: the first valid packet we receive from
            // an IP that isn't yet in our broadcast list automatically
            // adds it. This is how the host learns about a guest in
            // the bonfire-co-op flow — the guest sends first (they
            // know the host IP from join target), the host sees the
            // packet and adds the guest's IP as a peer for outbound
            // broadcasts. No additional handshake needed.
            if (senderId != _localSenderId)
            {
                MaybeRegisterDiscoveredPeer(result.RemoteEndPoint);
            }
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
            // when commands.jsonl writes are silent. Cadence widened
            // from 100→300 iters now that SHM bumps the loop to 30 Hz.
            if (iter % 300 == 0)
            {
                DebugLog($"InboxWriterLoop alive: iter={iter} peers={_peerTable.Count} broadcast={_broadcastCount} received={_receivedCount} shm_open={Ds2PoseShm.IsOpen} shm_gen={Ds2PoseShm.LastGeneration}");
            }
            try { await Task.Delay(InboxWriteInterval, ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { DebugLog($"InboxWriterLoop Task.Delay threw {ex.GetType().Name}: {ex.Message}"); break; }
        }
        DebugLog($"InboxWriterLoop: exiting (iter={iter})");
    }

    private static void WritePeerTableToInbox()
    {
        // Prune expired peers first (shared between SHM + file paths).
        var now = DateTime.UtcNow;
        foreach (var (id, entry) in _peerTable)
        {
            if (now - entry.LastSeenUtc > PeerTtl)
            {
                _peerTable.TryRemove(id, out _);
            }
        }

        // Plan v3 Track B — fast path: publish the snapshot to the
        // shared-memory pipe. The Injector reads this at 200 Hz so
        // end-to-end latency collapses to network RTT + one frame.
        // Builds a stack-allocated array (max 16 peers × 64 bytes =
        // 1 KB) so the hot path never touches the managed heap.
        Span<Ds2PoseShm.PeerEntry> shmPeers =
            stackalloc Ds2PoseShm.PeerEntry[Ds2PoseShm.MaxPeers];
        int shmCount = 0;
        foreach (var (_, entry) in _peerTable)
        {
            if (shmCount >= Ds2PoseShm.MaxPeers) break;
            var (r, g, b) = ColorFromSenderId(entry.SenderId);
            shmPeers[shmCount++] = new Ds2PoseShm.PeerEntry
            {
                SenderId = entry.SenderId,
                Px = entry.Px,
                Py = entry.Py,
                Pz = entry.Pz,
                YawRadians = entry.YawRadians,
                ColorR = r,
                ColorG = g,
                ColorB = b,
                Valid = 1u,
            };
        }
        if (Ds2PoseShm.IsOpen)
        {
            Ds2PoseShm.Publish(shmPeers[..shmCount]);
        }

        // Slow path / debug fallback: keep appending to commands.jsonl
        // so an older Injector build still receives pose updates and
        // the file remains a forensic record of what was published.
        // The file write cadence drops to every 3rd iteration (~100 ms)
        // to reduce disk pressure now that SHM owns the hot path.
        var commandsInbox = LatestCommandsInbox();
        if (commandsInbox is null) return;

        if (System.Threading.Interlocked.Increment(ref _commandsInboxTick) % 3 != 0)
        {
            return;
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

    // Counter used to slow the commands.jsonl writes once SHM owns the
    // hot path. v2.9.1 Track B: SHM ticks at 30 Hz, file ticks at 10 Hz.
    private static int _commandsInboxTick;

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

    // ── Track C Phase 2A: char_data publisher / handler ──────────────

    /// <summary>
    /// Periodic loop that reads the local player's char_data via
    /// Ds2MemoryReader, broadcasts it to peer endpoints over UDP, and
    /// republishes the merged local+peer table to the Ds2CharDataShm
    /// section. Runs at CharDataPublishInterval (1 Hz).
    /// </summary>
    private static async Task CharDataPublisherLoopAsync(CancellationToken ct)
    {
        DebugLog("CharDataPublisherLoop: started");
        long iter = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // 1. Read local snapshot (best-effort — DS2 may be loading).
                var local = Ds2MemoryReader.TryReadHostCharData();
                if (local is not null)
                {
                    _latestLocalCharSnapshot = local;
                    // 2. Broadcast to peers so they can build their own SHM.
                    BroadcastLocalCharData(local);
                }

                // 3. Prune stale peer entries (same TTL as the pose
                //    peer table — char_data updates piggyback on the
                //    same liveness signal).
                var now = DateTime.UtcNow;
                foreach (var (id, e) in _peerCharData)
                {
                    if (now - e.LastSeenUtc > PeerTtl)
                    {
                        _peerCharData.TryRemove(id, out _);
                    }
                }

                // 4. Publish the merged local + peer table to the SHM
                //    so the Injector can read every char_data snapshot
                //    in one place. Local first, peers after.
                if (Ds2CharDataShm.IsOpen)
                {
                    var combined = new List<Ds2CharDataShm.PeerSnapshot>();
                    if (local is not null)
                    {
                        combined.Add(new Ds2CharDataShm.PeerSnapshot(
                            _localSenderId,
                            local.HpCurrent, local.HpMaxWithBuffs, local.HpBaseMax,
                            local.EquipLoadMax, local.EquipWeightCurrent,
                            local.ZonePrimary, local.ZoneSecondary,
                            local.IsPhantom, local.Name ?? "",
                            local.EquipmentSlots));
                    }
                    foreach (var (_, e) in _peerCharData)
                    {
                        combined.Add(new Ds2CharDataShm.PeerSnapshot(
                            e.SenderId,
                            e.HpCurrent, e.HpMaxWithBuffs, e.HpMaxBase,
                            e.EquipLoadMax, e.EquipWeightCurrent,
                            e.ZonePrimary, e.ZoneSecondary,
                            e.IsPhantom, e.Name ?? "",
                            e.EquipmentSlots));
                    }
                    Ds2CharDataShm.Publish(combined);
                }
            }
            catch (Exception ex)
            {
                DebugLog($"CharDataPublisherLoop iter={iter} threw {ex.GetType().Name}: {ex.Message}");
            }
            iter++;
            if (iter % 30 == 0)
            {
                DebugLog($"CharDataPublisherLoop alive: iter={iter} localSnap={(_latestLocalCharSnapshot != null)} peers={_peerCharData.Count} broadcast={_charDataBroadcastCount} received={_charDataReceivedCount}");
            }
            try { await Task.Delay(CharDataPublishInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
        DebugLog($"CharDataPublisherLoop: exiting (iter={iter})");
    }

    private static void BroadcastLocalCharData(Ds2MemoryReader.Ds2CharSnapshot s)
    {
        if (_udpSend is null || _peerEndpoints.Count == 0) return;

        // Pack into the BNCD wire envelope (200 bytes exactly).
        Span<byte> packet = stackalloc byte[CharDataPacketSize];
        packet.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(packet[0..4], CharDataMagic);
        packet[4] = WireVersion;
        packet[5] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(packet[6..8], _charDataSequence++);
        BinaryPrimitives.WriteInt64LittleEndian(packet[8..16], _localSenderId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet[16..20], s.HpCurrent);
        BinaryPrimitives.WriteUInt32LittleEndian(packet[20..24], s.HpMaxWithBuffs);
        BinaryPrimitives.WriteUInt32LittleEndian(packet[24..28], s.HpBaseMax);
        BinaryPrimitives.WriteSingleLittleEndian(packet[28..32], s.EquipLoadMax);
        BinaryPrimitives.WriteSingleLittleEndian(packet[32..36], s.EquipWeightCurrent);
        BinaryPrimitives.WriteUInt32LittleEndian(packet[36..40], s.ZonePrimary);
        BinaryPrimitives.WriteUInt32LittleEndian(packet[40..44], s.ZoneSecondary);
        BinaryPrimitives.WriteUInt32LittleEndian(packet[44..48], s.IsPhantom ? 1u : 0u);

        // Name: encode + clamp to the 64-byte fixed window so we never
        // truncate mid-surrogate (DS2 names are ASCII-derived in
        // practice — "Player_0001" or "NetworkPlayer_*" — but stay
        // defensive).
        var nameBytes = Encoding.Unicode.GetBytes(s.Name ?? "");
        var nameCopyLen = Math.Min(nameBytes.Length, CharDataNameBytes - 2);
        nameBytes.AsSpan(0, nameCopyLen).CopyTo(packet[48..(48 + nameCopyLen)]);

        // Equipment array: 22 × u32.
        var slots = s.EquipmentSlots ?? Array.Empty<uint>();
        int slotBase = 48 + CharDataNameBytes; // = 112
        for (int i = 0; i < CharDataSlotCount; i++)
        {
            uint v = i < slots.Length ? slots[i] : 0u;
            BinaryPrimitives.WriteUInt32LittleEndian(
                packet[(slotBase + i * 4)..(slotBase + (i + 1) * 4)], v);
        }

        var bytes = packet.ToArray();
        foreach (var ep in _peerEndpoints)
        {
            try
            {
                _udpSend.Send(bytes, bytes.Length, ep);
                Interlocked.Increment(ref _charDataBroadcastCount);
            }
            catch (Exception ex)
            {
                DebugLog($"BroadcastLocalCharData to {ep} failed: {ex.GetType().Name} {ex.Message}");
            }
        }
    }

    private static void HandleCharDataPacket(byte[] buffer, IPEndPoint remoteEp)
    {
        try
        {
            if (buffer.Length != CharDataPacketSize)
            {
                Interlocked.Increment(ref _charDataDroppedCount);
                return;
            }
            var pkt = buffer.AsSpan();
            if (pkt[4] != WireVersion)
            {
                Interlocked.Increment(ref _charDataDroppedCount);
                return;
            }
            var senderId         = BinaryPrimitives.ReadInt64LittleEndian(pkt[8..16]);
            var hpCurrent        = BinaryPrimitives.ReadUInt32LittleEndian(pkt[16..20]);
            var hpMaxWithBuffs   = BinaryPrimitives.ReadUInt32LittleEndian(pkt[20..24]);
            var hpMaxBase        = BinaryPrimitives.ReadUInt32LittleEndian(pkt[24..28]);
            var equipLoadMax     = BinaryPrimitives.ReadSingleLittleEndian(pkt[28..32]);
            var equipWeightCur   = BinaryPrimitives.ReadSingleLittleEndian(pkt[32..36]);
            var zonePrimary      = BinaryPrimitives.ReadUInt32LittleEndian(pkt[36..40]);
            var zoneSecondary    = BinaryPrimitives.ReadUInt32LittleEndian(pkt[40..44]);
            var isPhantomFlag    = BinaryPrimitives.ReadUInt32LittleEndian(pkt[44..48]);

            // Decode UTF-16 name, walk to first NUL pair or buffer end.
            var nameSlice = pkt.Slice(48, CharDataNameBytes);
            int nameLen = 0;
            while (nameLen + 1 < nameSlice.Length)
            {
                if (nameSlice[nameLen] == 0 && nameSlice[nameLen + 1] == 0) break;
                nameLen += 2;
            }
            var name = nameLen > 0
                ? Encoding.Unicode.GetString(nameSlice[..nameLen])
                : "";

            int slotBase = 48 + CharDataNameBytes;
            var slots = new uint[CharDataSlotCount];
            for (int i = 0; i < CharDataSlotCount; i++)
            {
                slots[i] = BinaryPrimitives.ReadUInt32LittleEndian(
                    pkt[(slotBase + i * 4)..(slotBase + (i + 1) * 4)]);
            }

            // Don't store our own loopback packets — the publisher
            // already publishes _latestLocalCharSnapshot directly.
            if (senderId == _localSenderId)
            {
                Interlocked.Increment(ref _charDataReceivedCount);
                return;
            }

            _peerCharData[senderId] = new PeerCharData(
                senderId, isPhantomFlag != 0, name,
                hpCurrent, hpMaxWithBuffs, hpMaxBase,
                equipLoadMax, equipWeightCur,
                zonePrimary, zoneSecondary,
                slots, DateTime.UtcNow);
            Interlocked.Increment(ref _charDataReceivedCount);

            // Same auto-discovery hook as the pose listener.
            MaybeRegisterDiscoveredPeer(remoteEp);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _charDataDroppedCount);
            DebugLog($"HandleCharDataPacket from {remoteEp} threw {ex.GetType().Name}: {ex.Message}");
        }
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

    // Called from the listener when an unknown senderId sends a
    // valid packet. We use the source IP + the configured local
    // port (NOT the ephemeral source port) as the broadcast target.
    // This means both ends must be running on the same well-known
    // port — fine for the bonfire-co-op flow.
    private static void MaybeRegisterDiscoveredPeer(IPEndPoint remote)
    {
        var target = new IPEndPoint(remote.Address, _localPort);
        lock (Lock)
        {
            foreach (var existing in _peerEndpoints)
            {
                if (existing.Equals(target)) return;
            }
            _peerEndpoints.Add(target);
            DebugLog($"Discovered new peer: {target}");
        }
    }

    // Public helper for Ds2NativeSessionCoordinator. Adds a peer
    // endpoint to the broadcast list if the bridge is running. Safe
    // to call at any time; no-op if the endpoint is already present
    // or the bridge isn't running.
    public static bool AddPeer(string endpointRaw)
    {
        if (!TryParseEndpoint(endpointRaw, out var ep)) return false;
        lock (Lock)
        {
            if (_watcherTask is null) return false;
            foreach (var existing in _peerEndpoints)
            {
                if (existing.Equals(ep)) return true;
            }
            _peerEndpoints.Add(ep);
            DebugLog($"Manually added peer: {ep}");
            return true;
        }
    }

    // Public helper for the session coordinator. Starts the bridge
    // if it's not already running. Different from the RPC-driven
    // Start in that callers don't need to construct the peer list
    // — they can pass an empty list and rely on auto-discovery
    // once the first packet arrives. Useful for the HOST side of
    // the bonfire-co-op flow where the host doesn't know the guest's
    // IP until they connect.
    public static bool EnsureStarted(int localPort, IReadOnlyList<string> initialPeers)
    {
        lock (Lock)
        {
            if (_watcherTask is not null) return false; // already running
        }
        Start(localPort, initialPeers);
        return true;
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
