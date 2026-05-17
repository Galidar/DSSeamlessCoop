/*
 * Plan v3 Track C Phase 2A — shared-memory char_data publication.
 *
 * Sibling of Ds2PoseShm. Carries the heavier "character data"
 * fields that change slowly (HP, equipment, zone, name) so the
 * Injector can render UI overlays, tooltips, and — eventually —
 * a real engine-cooperative phantom from the receiver's data.
 *
 * Cadence: 1 Hz on the writer side (matches how often a typical
 * player's HP/equipment changes). The reader can poll faster if
 * it cares about diff-time but there's no payload churn between
 * generations most of the time.
 *
 * Layout (binary-compatible mirror in
 * Source/Injector/Hooks/DarkSouls2/DS2_CharDataShm.h — keep both
 * in sync):
 *
 *   offset  size   field
 *      0      4    magic       = 'BCD1' = 0x31444342 little-endian
 *      4      4    version     = 1
 *      8      4    generation  (seqlock — odd=writing, even=stable)
 *     12      4    peer_count  (0..16)
 *     16      8    writer_pid
 *     24      8    timestamp_ticks  (DateTime.UtcNow.Ticks)
 *     32     32    reserved (zero) — header is 64 bytes total
 *     64   xxxx    peers[16] × kPeerSize each
 *
 * Each peer entry (272 bytes — chosen as a round number with
 * headroom for future fields):
 *
 *     0      8    sender_id (int64)
 *     8      4    hp_current
 *    12      4    hp_max_with_buffs
 *    16      4    hp_max_base
 *    20      4    equip_load_max         (f32)
 *    24      4    equip_weight_current   (f32)
 *    28      4    zone_primary
 *    32      4    zone_secondary
 *    36      4    is_phantom (uint, 0/1)
 *    40     64    name (UTF-16 LE, 32 wchar fixed buffer, NUL-pad)
 *   104     88    equipment[22] × u32 item_id
 *   192     80    reserved (zero) — pads to 272
 *
 * Section name is fixed: `Local\BonfireDS2CharDataV1`.
 */

using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace Bonfire.Service.Modules;

internal static class Ds2CharDataShm
{
    public const string MapName = "Local\\BonfireDS2CharDataV1";
    public const uint Magic = 0x31444342u; // 'BCD1'
    public const uint Version = 1u;
    public const int MaxPeers = 16;
    public const int HeaderSize = 64;
    public const int PeerSize = 272;
    public const int TotalSize = HeaderSize + MaxPeers * PeerSize; // 4416 bytes
    public const int NameMaxBytes = 64; // 32 wchar UTF-16 slots
    public const int EquipmentSlotCount = 22;

    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = PeerSize)]
    public struct PeerEntry
    {
        public long SenderId;            // +0x00
        public uint HpCurrent;           // +0x08
        public uint HpMaxWithBuffs;      // +0x0C
        public uint HpMaxBase;           // +0x10
        public float EquipLoadMax;       // +0x14
        public float EquipWeightCurrent; // +0x18
        public uint ZonePrimary;         // +0x1C
        public uint ZoneSecondary;       // +0x20
        public uint IsPhantom;           // +0x24
        // +0x28..+0x67  name UTF-16 LE (NameMaxBytes bytes)
        // +0x68..+0xBF  equipment[22] u32 item_id (88 bytes)
        // +0xC0..+0x10F reserved (80 bytes pad to 272)
        // Inline-stored byte arrays — must marshal manually.
    }

    private static readonly object Lock = new();
    private static MemoryMappedFile? _file;
    private static MemoryMappedViewAccessor? _view;
    private static uint _generation;

    public static bool IsOpen
    {
        get { lock (Lock) { return _view is not null; } }
    }

    public static uint LastGeneration
    {
        get { lock (Lock) { return _generation; } }
    }

    public static bool TryOpen()
    {
        lock (Lock)
        {
            if (_view is not null) return true;
            try
            {
                _file = MemoryMappedFile.CreateOrOpen(
                    MapName, TotalSize, MemoryMappedFileAccess.ReadWrite);
                _view = _file.CreateViewAccessor(0, TotalSize, MemoryMappedFileAccess.ReadWrite);
                _view.Write(0, Magic);
                _view.Write(4, Version);
                _view.Write(8, 0u);
                _view.Write(12, 0u);
                _view.Write(16, (long)Process.GetCurrentProcess().Id);
                _view.Write(24, DateTime.UtcNow.Ticks);
                _generation = 0;
                return true;
            }
            catch
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
    /// Snapshot input for publishing one peer entry. The bridge calls
    /// Publish with the full list of currently-active peers at ~1 Hz.
    /// </summary>
    public sealed record PeerSnapshot(
        long SenderId,
        uint HpCurrent,
        uint HpMaxWithBuffs,
        uint HpMaxBase,
        float EquipLoadMax,
        float EquipWeightCurrent,
        uint ZonePrimary,
        uint ZoneSecondary,
        bool IsPhantom,
        string Name,
        uint[] EquipmentSlots);

    public static void Publish(IReadOnlyList<PeerSnapshot> peers)
    {
        lock (Lock)
        {
            if (_view is null) return;
            int count = Math.Min(peers.Count, MaxPeers);

            // seqlock write fence.
            var writing = unchecked(_generation + 1u);
            _view.Write(8, writing);
            System.Threading.Thread.MemoryBarrier();

            _view.Write(12, (uint)count);
            _view.Write(24, DateTime.UtcNow.Ticks);

            for (int i = 0; i < count; i++)
            {
                WritePeerEntry(HeaderSize + (long)i * PeerSize, peers[i]);
            }
            // Zero unused slots.
            var blank = new byte[PeerSize];
            for (int i = count; i < MaxPeers; i++)
            {
                _view.WriteArray(HeaderSize + (long)i * PeerSize, blank, 0, blank.Length);
            }

            System.Threading.Thread.MemoryBarrier();
            var stable = unchecked(writing + 1u);
            _view.Write(8, stable);
            _generation = stable;
        }
    }

    private static void WritePeerEntry(long baseOff, PeerSnapshot s)
    {
        if (_view is null) return;
        _view.Write(baseOff + 0x00, s.SenderId);
        _view.Write(baseOff + 0x08, s.HpCurrent);
        _view.Write(baseOff + 0x0C, s.HpMaxWithBuffs);
        _view.Write(baseOff + 0x10, s.HpMaxBase);
        _view.Write(baseOff + 0x14, s.EquipLoadMax);
        _view.Write(baseOff + 0x18, s.EquipWeightCurrent);
        _view.Write(baseOff + 0x1C, s.ZonePrimary);
        _view.Write(baseOff + 0x20, s.ZoneSecondary);
        _view.Write(baseOff + 0x24, s.IsPhantom ? 1u : 0u);

        // Name UTF-16 LE, fixed NameMaxBytes window, NUL-padded.
        var nameBytes = new byte[NameMaxBytes];
        var encoded = Encoding.Unicode.GetBytes(s.Name ?? "");
        Array.Copy(encoded, nameBytes, Math.Min(encoded.Length, NameMaxBytes - 2));
        _view.WriteArray(baseOff + 0x28, nameBytes, 0, NameMaxBytes);

        // Equipment slots — 22 × u32 = 88 bytes at +0x68.
        var slotBytes = new byte[EquipmentSlotCount * 4];
        var slots = s.EquipmentSlots ?? Array.Empty<uint>();
        for (int i = 0; i < EquipmentSlotCount; i++)
        {
            uint v = i < slots.Length ? slots[i] : 0u;
            slotBytes[i * 4 + 0] = (byte)(v & 0xFF);
            slotBytes[i * 4 + 1] = (byte)((v >> 8) & 0xFF);
            slotBytes[i * 4 + 2] = (byte)((v >> 16) & 0xFF);
            slotBytes[i * 4 + 3] = (byte)((v >> 24) & 0xFF);
        }
        _view.WriteArray(baseOff + 0x68, slotBytes, 0, slotBytes.Length);
    }
}
