/*
 * Plan v3 Track B implementation — read peer poses from the
 * Local\BonfireDS2PoseV1 shared section and push them into
 * DS2_RenderHook_SetPeerPoses() at ~200 Hz.
 *
 * The polling thread is single-purpose so the existing runtime
 * worker (event log, heartbeat, command inbox) stays untouched and
 * its 2-second Sleep cadence does not bottleneck pose delivery.
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

namespace DS2_PoseShm
{
    namespace
    {
        std::atomic<HANDLE>             g_mapping{nullptr};
        std::atomic<const Shm*>         g_view{nullptr};
        std::atomic<std::uint32_t>      g_last_gen{0};
        std::atomic<std::uint64_t>      g_torn_count{0};
        std::atomic<std::uint64_t>      g_snapshot_count{0};
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

    bool TryReadLatest(DS2_PeerPose* poses, int* out_count)
    {
        if (poses == nullptr || out_count == nullptr) return false;
        const Shm* shm = g_view.load(std::memory_order_acquire);
        if (shm == nullptr) return false;

        // Up to 4 seqlock retries before giving up — at ~30 Hz writer
        // cadence and a single-digit-microsecond critical section,
        // a torn read clears itself within one retry on a modern CPU.
        for (int attempt = 0; attempt < 4; ++attempt)
        {
            const std::uint32_t g1 = LoadGeneration(shm);
            if ((g1 & 1u) != 0u)
            {
                // Mid-write — back off briefly and try again.
                g_torn_count.fetch_add(1, std::memory_order_relaxed);
                continue;
            }

            const std::uint32_t prev = g_last_gen.load(
                std::memory_order_relaxed);
            if (g1 == prev)
            {
                // No new snapshot since last successful read — caller
                // doesn't need to push the same data to the render
                // hook again.
                return false;
            }

            // Snapshot the payload.
            std::uint32_t count = shm->header.peer_count;
            if (count > kMaxPeers) count = kMaxPeers;
            Peer copy[kMaxPeers] = {};
            for (std::uint32_t i = 0; i < count; ++i)
            {
                copy[i] = shm->peers[i];
            }
            std::atomic_thread_fence(std::memory_order_acquire);

            // Verify we did not race the writer.
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
        DWORD WINAPI PollThreadProc(LPVOID)
        {
            constexpr DWORD kFastPollMs = 5;     // ~200 Hz when SHM open
            constexpr DWORD kSlowPollMs = 250;   // when service not yet up
            DWORD next_reopen = 0;
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

                DS2_PeerPose poses[kMaxPeers] = {};
                int count = 0;
                if (TryReadLatest(poses, &count))
                {
                    DS2_RenderHook_SetPeerPoses(poses, count);
                }
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
        Log("DS2_PoseShm: poll thread started (5 ms fast / 250 ms slow).");
        return true;
    }
}
