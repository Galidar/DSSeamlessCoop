/*
 * Plan v3 Track B — Injector side of the shared-memory pose pipe.
 *
 * Mirrors Source/BonfireService/Modules/Ds2PoseShm.cs. The C# side
 * creates the named section (`Local\BonfireDS2PoseV1`) and writes
 * snapshots of the peer-pose table on every UDP tick (~30 Hz). This
 * header exposes a tiny C++ reader that the runtime worker thread
 * polls at ~200 Hz, ferrying the latest snapshot into
 * DS2_RenderHook_SetPeerPoses().
 *
 * The seqlock protocol:
 *   - The writer bumps `generation` from `n` (even, stable) to
 *     `n+1` (odd, writing in progress), writes the payload, then
 *     bumps to `n+2` (even, new stable snapshot).
 *   - Readers sample `generation`, copy the payload, sample
 *     `generation` again. If either sample is odd or the two
 *     samples differ, the read was torn → retry.
 *
 * No kernel synchronization object is involved, so this is wait-free
 * for the writer and lock-free for the reader. Worst-case the
 * reader spins a handful of times during the writer's microsecond-
 * scale critical section.
 */

#pragma once

#include "DS2_RenderHook.h"

#include <atomic>
#include <cstdint>

namespace DS2_PoseShm
{
    constexpr const wchar_t* kMapName = L"Local\\BonfireDS2PoseV1";
    constexpr std::uint32_t kMagic = 0x31535042u; // 'BPS1'
    constexpr std::uint32_t kVersion = 1u;
    constexpr std::uint32_t kMaxPeers = 16u;
    constexpr std::uint32_t kHeaderSize = 64u;
    constexpr std::uint32_t kPeerSize = 64u;
    constexpr std::uint32_t kTotalSize = kHeaderSize + kMaxPeers * kPeerSize;

#pragma pack(push, 1)
    struct Header
    {
        std::uint32_t magic;            // 'BPS1'
        std::uint32_t version;          // 1
        std::uint32_t generation;       // seqlock — atomic on x86 for naturally-aligned u32
        std::uint32_t peer_count;       // 0..kMaxPeers
        std::int64_t  writer_pid;       // diagnostic
        std::int64_t  timestamp_ticks;  // .NET DateTime.Ticks, diagnostic
        std::uint32_t reserved[8];      // pad to 64 bytes
    };
    static_assert(sizeof(Header) == kHeaderSize,
                  "Ds2PoseShm header layout drift");

    struct Peer
    {
        std::int64_t  sender_id;        // host-assigned peer ID
        float         position[3];
        float         yaw_radians;
        float         color[3];
        std::uint32_t valid;
        std::uint32_t pad[6];           // round to 64 bytes
    };
    static_assert(sizeof(Peer) == kPeerSize,
                  "Ds2PoseShm peer layout drift");

    struct Shm
    {
        Header header;
        Peer   peers[kMaxPeers];
    };
    static_assert(sizeof(Shm) == kTotalSize,
                  "Ds2PoseShm total layout drift");
#pragma pack(pop)

    /// Open the section (lazy). Returns true once mapped. Subsequent
    /// calls are no-ops. Safe to call from any thread; the reader is
    /// single-threaded in practice (the runtime worker).
    bool Open();

    /// Release the mapping. Called from the Injector shutdown path.
    void Close();

    /// True if the named section is currently mapped.
    bool IsOpen();

    /// Last generation the reader successfully observed. Diagnostic.
    std::uint32_t LastGeneration();

    /// Number of seqlock retries seen since process start. Diagnostic.
    std::uint64_t TornReadCount();

    /// Number of successful snapshot reads. Diagnostic.
    std::uint64_t SnapshotCount();

    /// Try to read the latest snapshot. On success returns true and
    /// fills `poses` (caller-allocated, capacity kMaxPeers) and
    /// `out_count`. Returns false if the section is not mapped, the
    /// header is malformed, or no new generation has appeared since
    /// the last successful read. After 4 retry attempts the function
    /// gives up on this tick and returns false; the next tick will
    /// see the new stable generation.
    bool TryReadLatest(DS2_PeerPose* poses, int* out_count);

    /// Start the polling thread that ferries snapshots from the
    /// section into DS2_RenderHook_SetPeerPoses() at ~200 Hz. Safe to
    /// call multiple times — second call is a no-op. The thread exits
    /// when Close() is called (or process shutdown).
    bool StartPollThread();
}
