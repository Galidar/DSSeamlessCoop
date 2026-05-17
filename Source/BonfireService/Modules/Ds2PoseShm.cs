/*
 * Plan v3 Track B — shared-memory IPC for sub-10 ms pose delivery
 * to the DS2 Injector.
 *
 * Replaces the commands.jsonl hot-path for `render.set_peer_poses`
 * with a single named section that BonfireService writes on every
 * UDP pose tick and the Injector reads on a fast spin (5 ms cycle,
 * 200 Hz). End-to-end latency drops from the old worker-thread
 * Sleep(2000) ≈ 2 s down to network RTT + one frame.
 *
 * Layout (binary-compatible mirror in
 * Source/Injector/Hooks/DarkSouls2/DS2_PoseShm.h — keep both in sync):
 *
 *   offset  size  field
 *      0     4    magic       = 'BPS1' = 0x31535042 little-endian
 *      4     4    version     = 1
 *      8     4    generation  (seqlock — odd = writing, even = stable)
 *     12     4    peer_count  (0..16)
 *     16     8    writer_pid
 *     24     8    timestamp_ticks  (DateTime.UtcNow.Ticks, diagnostic only)
 *     32    32    reserved (zero) — bumps header to 64 bytes total
 *     64  1024    peers[16] × 64 bytes each
 *
 * Each `peer` entry (64 bytes):
 *     0     8    sender_id (int64)
 *     8    12    position[3] (float32 x, y, z)
 *    20     4    yaw_radians (float32)
 *    24    12    color[3]    (float32 r, g, b)
 *    36     4    valid       (uint32, 1 = active, 0 = empty)
 *    40    24    pad (zero)
 *
 * Section name is fixed: `Local\BonfireDS2PoseV1`. The "Local\"
 * namespace is per Windows logon session so two RDP users on the
 * same host don't collide. Within one session only one Bonfire
 * pose bridge runs at a time (enforced by the existing
 * Ds2NativePoseBridge.Lock), so a single section suffices.
 *
 * Concurrency: single producer (BonfireService inbox writer
 * thread), N consumers (Injector worker thread, occasional debug
 * readers). The seqlock pattern means readers re-try on torn reads
 * but never block writers and never need a kernel object.
 */

using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace Bonfire.Service.Modules;

internal static class Ds2PoseShm
{
    public const string MapName = "Local\\BonfireDS2PoseV1";
    public const uint Magic = 0x31535042u; // 'BPS1'
    public const uint Version = 1u;
    public const int MaxPeers = 16;
    public const int HeaderSize = 64;
    public const int PeerSize = 64;
    public const int TotalSize = HeaderSize + MaxPeers * PeerSize; // 1088 bytes

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct PeerEntry
    {
        public long SenderId;
        public float Px;
        public float Py;
        public float Pz;
        public float YawRadians;
        public float ColorR;
        public float ColorG;
        public float ColorB;
        public uint Valid;
        // 24 bytes padding to round to 64
        public uint Pad0, Pad1, Pad2, Pad3, Pad4, Pad5;
    }

    private static readonly object Lock = new();
    private static MemoryMappedFile? _file;
    private static MemoryMappedViewAccessor? _view;
    private static uint _generation; // last published, monotonically even

    public static bool IsOpen
    {
        get
        {
            lock (Lock) { return _view is not null; }
        }
    }

    public static uint LastGeneration
    {
        get { lock (Lock) { return _generation; } }
    }

    /// <summary>
    /// Open (or create) the shared section and seed the header with the
    /// magic+version. Safe to call multiple times — second call is a
    /// no-op. Returns false if the OS denies the mapping (e.g. another
    /// process owns it with incompatible permissions); the caller should
    /// fall back to the commands.jsonl path in that case.
    /// </summary>
    public static bool TryOpen()
    {
        lock (Lock)
        {
            if (_view is not null) return true;
            try
            {
                _file = MemoryMappedFile.CreateOrOpen(
                    MapName,
                    TotalSize,
                    MemoryMappedFileAccess.ReadWrite);
                _view = _file.CreateViewAccessor(0, TotalSize, MemoryMappedFileAccess.ReadWrite);

                // Seed the header. Writers atomically bump generation —
                // we set it to 0 so the first real write transitions
                // 0 → 1 (writing) → 2 (stable).
                _view.Write(0, Magic);
                _view.Write(4, Version);
                _view.Write(8, 0u); // generation
                _view.Write(12, 0u); // peer_count
                _view.Write(16, (long)Process.GetCurrentProcess().Id);
                _view.Write(24, DateTime.UtcNow.Ticks);
                _generation = 0;
                return true;
            }
            catch (Exception)
            {
                try { _view?.Dispose(); } catch { }
                try { _file?.Dispose(); } catch { }
                _view = null;
                _file = null;
                return false;
            }
        }
    }

    public static void Close()
    {
        lock (Lock)
        {
            try { _view?.Dispose(); } catch { }
            try { _file?.Dispose(); } catch { }
            _view = null;
            _file = null;
            _generation = 0;
        }
    }

    /// <summary>
    /// Publishes a snapshot of the peer table. Writes with the seqlock
    /// pattern: generation goes odd (writing) → write payload → even
    /// (stable). Readers retry on odd values or torn reads.
    /// </summary>
    public static void Publish(ReadOnlySpan<PeerEntry> peers)
    {
        lock (Lock)
        {
            if (_view is null) return;
            var count = Math.Min(peers.Length, MaxPeers);

            // Step 1: bump generation to odd to mark writing.
            var writing = unchecked(_generation + 1u);
            _view.Write(8, writing);
            // Memory barrier: ensure the generation write is visible
            // before payload writes. .NET on x86/x64 has acq-rel by
            // default for normal writes but we still force a fence.
            System.Threading.Thread.MemoryBarrier();

            // Step 2: write the payload.
            _view.Write(12, (uint)count);
            _view.Write(24, DateTime.UtcNow.Ticks);
            for (int i = 0; i < count; i++)
            {
                var offset = HeaderSize + (long)i * PeerSize;
                // Write<T>(long, ref T) — the by-ref overload is the
                // generic-struct one. The by-value overload only
                // covers primitives and would clash on PeerEntry.
                var entry = peers[i];
                _view.Write(offset, ref entry);
            }
            // Zero-out the unused slots so a stale slot from a previous
            // larger publish doesn't leak through the seqlock window.
            var blank = default(PeerEntry);
            for (int i = count; i < MaxPeers; i++)
            {
                var offset = HeaderSize + (long)i * PeerSize;
                _view.Write(offset, ref blank);
            }

            // Step 3: bump generation to even (stable).
            System.Threading.Thread.MemoryBarrier();
            var stable = unchecked(writing + 1u);
            _view.Write(8, stable);
            _generation = stable;
        }
    }
}
