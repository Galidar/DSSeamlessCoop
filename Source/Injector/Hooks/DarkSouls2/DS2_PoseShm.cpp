/*
 * Plan v3 Track B implementation — read peer poses from the
 * Local\BonfireDS2PoseV1 shared section and push them into
 * DS2_RenderHook_SetPeerPoses() at ~200 Hz.
 *
 * v2.9.3 Track C Phase 1 extension:
 *   The poll thread now does client-side **temporal interpolation**
 *   between the last two received network snapshots, so the
 *   render hook gets a smooth continuous position regardless of
 *   when the BonfireService write actually landed. This is the
 *   same technique every multiplayer FPS uses ("entity buffering"
 *   or "interpolation delay"):
 *
 *     - Two snapshots per peer:
 *         prev  (older, taken at t-1)
 *         curr  (newer, taken at t)
 *     - Render at t_now using lerp(prev, curr, alpha) where
 *         alpha = clamp((now - t_curr) / (t_curr - t_prev), 0, 1)
 *     - Yaw uses shortest-arc interpolation so a peer turning
 *       from +175° to -175° rotates 10° via ±180°, not 350° the
 *       other way.
 *
 *   Cost: one packet's worth of delay (~33 ms at 30 Hz) traded
 *   for perfectly smooth motion between updates. The end-to-end
 *   pose lag stays sub-30 ms because the inner SHM read is still
 *   200 Hz.
 *
 *   Peer identity is matched by sender_id across snapshots. If
 *   the producer never sets sender_id (JSON fallback path) we
 *   fall back to index-based matching, which behaves like the
 *   pre-interpolation snap.
 *
 * If BonfireService is not yet running we retry OpenFileMappingW
 * once every ~250 ms instead of every 5 ms — keeps the polling
 * loop cheap when the user is at the main menu.
 */

#include "DS2_PoseShm.h"

#include "Shared/Core/Utils/Logging.h"

#include <Windows.h>
#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstring>

#ifndef M_PI
#define M_PI 3.14159265358979323846
#endif

namespace DS2_PoseShm
{
    namespace
    {
        std::atomic<HANDLE>             g_mapping{nullptr};
        std::atomic<const Shm*>         g_view{nullptr};
        std::atomic<std::uint32_t>      g_last_gen{0};
        std::atomic<std::uint64_t>      g_torn_count{0};
        std::atomic<std::uint64_t>      g_snapshot_count{0};
        std::atomic<std::uint64_t>      g_interp_frames{0};
        std::atomic<bool>               g_thread_running{false};

        // Atomically load the generation field. On x86/x64 a naturally
        // aligned 32-bit load is atomic, but we wrap it for clarity
        // and to add the acquire fence.
        std::uint32_t LoadGeneration(const Shm* shm)
        {
            return std::atomic_load_explicit(
                reinterpret_cast<const std::atomic<std::uint32_t>*>(
                    &shm->header.generation),
                std::memory_order_acquire);
        }
    }

    bool Open()
    {
        if (g_view.load(std::memory_order_acquire) != nullptr)
            return true;

        HANDLE map = OpenFileMappingW(
            FILE_MAP_READ, FALSE, kMapName);
        if (map == nullptr)
        {
            // BonfireService not running yet, or denied access.
            return false;
        }

        void* view = MapViewOfFile(
            map, FILE_MAP_READ, 0, 0, kTotalSize);
        if (view == nullptr)
        {
            CloseHandle(map);
            return false;
        }

        // Sanity-check the magic + version before swapping in the
        // pointer. A stale or unrelated mapping at the same name
        // would otherwise let us copy 1 KB of garbage into the
        // peer-pose table.
        const auto* shm = static_cast<const Shm*>(view);
        if (shm->header.magic != kMagic ||
            shm->header.version != kVersion)
        {
            UnmapViewOfFile(view);
            CloseHandle(map);
            return false;
        }

        g_mapping.store(map, std::memory_order_release);
        g_view.store(shm, std::memory_order_release);
        return true;
    }

    void Close()
    {
        if (auto* view = g_view.exchange(nullptr, std::memory_order_acq_rel))
        {
            UnmapViewOfFile(const_cast<Shm*>(view));
        }
        if (HANDLE map = g_mapping.exchange(nullptr, std::memory_order_acq_rel))
        {
            CloseHandle(map);
        }
    }

    bool IsOpen()
    {
        return g_view.load(std::memory_order_acquire) != nullptr;
    }

    std::uint32_t LastGeneration()
    {
        return g_last_gen.load(std::memory_order_relaxed);
    }

    std::uint64_t TornReadCount()
    {
        return g_torn_count.load(std::memory_order_relaxed);
    }

    std::uint64_t SnapshotCount()
    {
        return g_snapshot_count.load(std::memory_order_relaxed);
    }

    // Read latest into the caller-allocated DS2_PeerPose array.
    // Returns true on success (caller should re-push to render hook).
    // Returns false on no-new-snapshot OR torn-after-4-retries.
    //
    // v2.9.3: now also fills DS2_PeerPose.sender_id so the
    // interpolator can match peers across snapshots.
    bool TryReadLatest(DS2_PeerPose* poses, int* out_count)
    {
        if (poses == nullptr || out_count == nullptr) return false;
        const Shm* shm = g_view.load(std::memory_order_acquire);
        if (shm == nullptr) return false;

        for (int attempt = 0; attempt < 4; ++attempt)
        {
            const std::uint32_t g1 = LoadGeneration(shm);
            if ((g1 & 1u) != 0u)
            {
                g_torn_count.fetch_add(1, std::memory_order_relaxed);
                continue;
            }

            const std::uint32_t prev = g_last_gen.load(
                std::memory_order_relaxed);
            if (g1 == prev)
            {
                return false;
            }

            std::uint32_t count = shm->header.peer_count;
            if (count > kMaxPeers) count = kMaxPeers;
            Peer copy[kMaxPeers] = {};
            for (std::uint32_t i = 0; i < count; ++i)
            {
                copy[i] = shm->peers[i];
            }
            std::atomic_thread_fence(std::memory_order_acquire);

            const std::uint32_t g2 = LoadGeneration(shm);
            if (g2 != g1)
            {
                g_torn_count.fetch_add(1, std::memory_order_relaxed);
                continue;
            }

            for (std::uint32_t i = 0; i < count; ++i)
            {
                poses[i].position[0] = copy[i].position[0];
                poses[i].position[1] = copy[i].position[1];
                poses[i].position[2] = copy[i].position[2];
                poses[i].yaw_radians = copy[i].yaw_radians;
                poses[i].color[0]    = copy[i].color[0];
                poses[i].color[1]    = copy[i].color[1];
                poses[i].color[2]    = copy[i].color[2];
                poses[i].valid       = copy[i].valid;
                poses[i].sender_id   = copy[i].sender_id;
            }
            *out_count = static_cast<int>(count);
            g_last_gen.store(g1, std::memory_order_relaxed);
            g_snapshot_count.fetch_add(1, std::memory_order_relaxed);
            return true;
        }
        return false;
    }

    namespace
    {
        // ── Per-peer history for client-side interpolation ───────────
        //
        // Tracks the two most recent network snapshots per peer so the
        // render thread always has something to interpolate from. The
        // table is private to the poll thread (no cross-thread access)
        // so no synchronisation needed beyond the SetPeerPoses call at
        // the end of each iteration.

        struct HistorySlot
        {
            bool          occupied = false;
            std::int64_t  sender_id = 0;

            // "prev" = older snapshot, "curr" = newer.
            // has_prev tells us whether we can interpolate or must
            // snap to curr.
            bool          has_prev = false;
            LARGE_INTEGER prev_qpc = {};
            LARGE_INTEGER curr_qpc = {};

            float prev_pos[3]  = {};
            float prev_yaw     = 0.0f;

            float curr_pos[3]  = {};
            float curr_yaw     = 0.0f;

            float color[3]     = {1.0f, 1.0f, 1.0f};
            std::uint32_t valid = 0;
        };

        HistorySlot   s_history[kMaxPeers];
        LARGE_INTEGER s_qpc_freq = {};

        // Shortest-arc lerp for an angle in radians.
        float LerpAngle(float a, float b, float t)
        {
            constexpr float kPi = static_cast<float>(M_PI);
            constexpr float kTwoPi = kPi * 2.0f;
            float diff = b - a;
            while (diff > kPi)  diff -= kTwoPi;
            while (diff < -kPi) diff += kTwoPi;
            return a + diff * t;
        }

        // Find or allocate a history slot for the given sender_id.
        // If sender_id == 0 (no identity available, e.g. the JSON
        // fallback path) we use index-based matching: slot i ↔ peer
        // index i. Returns nullptr if all slots are taken.
        HistorySlot* AcquireSlot(std::int64_t sender_id, int peer_index)
        {
            if (sender_id != 0)
            {
                for (int i = 0; i < kMaxPeers; ++i)
                {
                    if (s_history[i].occupied && s_history[i].sender_id == sender_id)
                        return &s_history[i];
                }
                for (int i = 0; i < kMaxPeers; ++i)
                {
                    if (!s_history[i].occupied)
                    {
                        s_history[i] = HistorySlot{};
                        s_history[i].occupied = true;
                        s_history[i].sender_id = sender_id;
                        return &s_history[i];
                    }
                }
                return nullptr;
            }
            // Fallback: index-based slot reuse.
            if (peer_index < 0 || peer_index >= kMaxPeers) return nullptr;
            HistorySlot& s = s_history[peer_index];
            if (!s.occupied)
            {
                s = HistorySlot{};
                s.occupied = true;
                s.sender_id = 0;
            }
            return &s;
        }

        // Mark all slots not present in the latest snapshot as
        // departed. Called after the snapshot ingest.
        void EvictMissingSlots(const std::int64_t* seen_ids, int seen_count,
                               bool used_index_matching)
        {
            if (used_index_matching)
            {
                // Index-based: slots past seen_count are gone.
                for (int i = seen_count; i < kMaxPeers; ++i)
                {
                    s_history[i].occupied = false;
                    s_history[i].valid = 0;
                }
                return;
            }
            for (int i = 0; i < kMaxPeers; ++i)
            {
                if (!s_history[i].occupied) continue;
                bool seen = false;
                for (int j = 0; j < seen_count; ++j)
                {
                    if (seen_ids[j] == s_history[i].sender_id) { seen = true; break; }
                }
                if (!seen)
                {
                    s_history[i].occupied = false;
                    s_history[i].valid = 0;
                }
            }
        }

        // Push the latest snapshot into the history table, rotating
        // prev←curr and curr←incoming on each new generation.
        void IngestSnapshot(const DS2_PeerPose* poses, int count,
                            LARGE_INTEGER now)
        {
            std::int64_t seen_ids[kMaxPeers] = {};
            int seen_count = 0;
            bool used_index_matching = false;

            for (int i = 0; i < count; ++i)
            {
                HistorySlot* slot = AcquireSlot(poses[i].sender_id, i);
                if (slot == nullptr) continue;
                if (poses[i].sender_id == 0) used_index_matching = true;
                seen_ids[seen_count++] = slot->sender_id;

                // Rotate prev←curr, curr←incoming.
                if (slot->valid)
                {
                    slot->prev_pos[0] = slot->curr_pos[0];
                    slot->prev_pos[1] = slot->curr_pos[1];
                    slot->prev_pos[2] = slot->curr_pos[2];
                    slot->prev_yaw    = slot->curr_yaw;
                    slot->prev_qpc    = slot->curr_qpc;
                    slot->has_prev    = true;
                }
                slot->curr_pos[0] = poses[i].position[0];
                slot->curr_pos[1] = poses[i].position[1];
                slot->curr_pos[2] = poses[i].position[2];
                slot->curr_yaw    = poses[i].yaw_radians;
                slot->curr_qpc    = now;
                slot->color[0]    = poses[i].color[0];
                slot->color[1]    = poses[i].color[1];
                slot->color[2]    = poses[i].color[2];
                slot->valid       = poses[i].valid;
            }

            EvictMissingSlots(seen_ids, seen_count, used_index_matching);
        }

        // Compute the interpolated snapshot for the current wall clock
        // and write it into out_poses. Returns the active peer count.
        int RenderInterpolated(LARGE_INTEGER now, DS2_PeerPose* out_poses)
        {
            int active = 0;
            for (int i = 0; i < kMaxPeers; ++i)
            {
                const HistorySlot& slot = s_history[i];
                if (!slot.occupied || !slot.valid) continue;

                DS2_PeerPose& dst = out_poses[active++];
                dst.color[0]  = slot.color[0];
                dst.color[1]  = slot.color[1];
                dst.color[2]  = slot.color[2];
                dst.valid     = slot.valid;
                dst.sender_id = slot.sender_id;

                if (!slot.has_prev || s_qpc_freq.QuadPart == 0)
                {
                    // No previous snapshot — snap to curr.
                    dst.position[0] = slot.curr_pos[0];
                    dst.position[1] = slot.curr_pos[1];
                    dst.position[2] = slot.curr_pos[2];
                    dst.yaw_radians = slot.curr_yaw;
                    continue;
                }

                const double freq = static_cast<double>(s_qpc_freq.QuadPart);
                const double dt_packet =
                    static_cast<double>(slot.curr_qpc.QuadPart - slot.prev_qpc.QuadPart)
                    / freq;
                const double dt_now =
                    static_cast<double>(now.QuadPart - slot.curr_qpc.QuadPart)
                    / freq;

                float t = 1.0f;
                if (dt_packet > 1e-6)
                {
                    t = static_cast<float>(dt_now / dt_packet);
                    if (t < 0.0f) t = 0.0f;
                    if (t > 1.0f) t = 1.0f; // Don't extrapolate.
                }
                dst.position[0] = slot.prev_pos[0] + (slot.curr_pos[0] - slot.prev_pos[0]) * t;
                dst.position[1] = slot.prev_pos[1] + (slot.curr_pos[1] - slot.prev_pos[1]) * t;
                dst.position[2] = slot.prev_pos[2] + (slot.curr_pos[2] - slot.prev_pos[2]) * t;
                dst.yaw_radians = LerpAngle(slot.prev_yaw, slot.curr_yaw, t);
            }
            return active;
        }

        DWORD WINAPI PollThreadProc(LPVOID)
        {
            constexpr DWORD kFastPollMs = 5;     // ~200 Hz when SHM open
            constexpr DWORD kSlowPollMs = 250;   // when service not yet up
            DWORD next_reopen = 0;

            QueryPerformanceFrequency(&s_qpc_freq);
            std::memset(s_history, 0, sizeof(s_history));

            while (g_thread_running.load(std::memory_order_acquire))
            {
                if (!IsOpen())
                {
                    if (GetTickCount() >= next_reopen)
                    {
                        if (Open())
                        {
                            Log("DS2_PoseShm: shared section attached (%S).", kMapName);
                        }
                        next_reopen = GetTickCount() + kSlowPollMs;
                    }
                    Sleep(kSlowPollMs);
                    continue;
                }

                // Step 1: ingest any new SHM snapshot into the history.
                DS2_PeerPose incoming[kMaxPeers] = {};
                int incoming_count = 0;
                if (TryReadLatest(incoming, &incoming_count))
                {
                    LARGE_INTEGER now;
                    QueryPerformanceCounter(&now);
                    IngestSnapshot(incoming, incoming_count, now);
                }

                // Step 2: render the interpolated value for "now" and
                // push it to the render hook. We always re-push, even
                // if no new snapshot arrived, so the visible position
                // advances smoothly between network packets.
                LARGE_INTEGER now2;
                QueryPerformanceCounter(&now2);
                DS2_PeerPose render_poses[kMaxPeers] = {};
                int n = RenderInterpolated(now2, render_poses);
                DS2_RenderHook_SetPeerPoses(render_poses, n);
                g_interp_frames.fetch_add(1, std::memory_order_relaxed);

                Sleep(kFastPollMs);
            }
            return 0;
        }
    }

    bool StartPollThread()
    {
        bool expected = false;
        if (!g_thread_running.compare_exchange_strong(
                expected, true, std::memory_order_acq_rel))
        {
            return true; // already running
        }
        HANDLE t = CreateThread(
            nullptr, 0, PollThreadProc, nullptr, 0, nullptr);
        if (t == nullptr)
        {
            g_thread_running.store(false, std::memory_order_release);
            Warning("DS2_PoseShm: failed to start poll thread: %lu", GetLastError());
            return false;
        }
        CloseHandle(t);
        Log("DS2_PoseShm: poll thread started (5 ms tick, client-side interpolation enabled).");
        return true;
    }
}
