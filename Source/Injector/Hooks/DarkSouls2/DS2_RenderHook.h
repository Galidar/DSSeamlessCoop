/*
 * DSSeamlessCoop — Bonfire HKMP overlay (2026-05-16)
 *
 * Phase-1 render-pipeline hook for DS2 SOTFS.
 *
 * Approach: install a Detour on IDXGISwapChain::Present so we get a
 * per-frame callback inside DS2's render thread. Phase 1 emits a
 * heartbeat counter; Phase 2+ will use the same hook point to draw our
 * own meshes for peer players (HKMP-style overlay) without depending on
 * vanilla DS2 phantom/summon mechanics.
 *
 * See Docs/HKMP_OVERLAY_RESEARCH.md Track 5 (Camino 2) for the plan.
 */

#pragma once

#include "Injector/Hooks/Hook.h"

#include <cstdint>

class DS2_RenderHook : public Hook
{
public:
    virtual bool Install(Injector& injector) override;
    virtual void Uninstall() override;
    virtual const char* GetName() override;
};

// Public accessor used by the runtime worker thread to fold the live
// frame count into its heartbeat payload (Phase-1 verification that the
// Present hook is firing).
uint64_t DS2_RenderHook_GetFrameCount();

// True once the Present detour has been attached successfully (entire
// pipeline live: D3D11CreateDevice → factory → Present all hooked).
bool DS2_RenderHook_IsInstalled();

// Per-stage diagnostic accessors. Heartbeats fold each of these in so
// we can pinpoint where the chain breaks if any stage doesn't fire.
bool DS2_RenderHook_IsCreateDeviceHooked();
bool DS2_RenderHook_IsFactoryHooked();
bool DS2_RenderHook_IsPresentHooked();

// Phase 3 diagnostic: how many times the Map/Unmap hooks captured a
// constant-buffer write that fit the VP filter (small + dynamic +
// CB-bound). Zero means the filter never matched.
uint64_t DS2_RenderHook_GetVPCaptureCount();

// Copy the latest captured 4x4 VP candidate (16 floats) into out_vp.
// Returns true if at least one capture has happened. Used for
// post-hoc diagnosis when the world-space quad doesn't render.
bool DS2_RenderHook_GetCapturedVP(float out_vp[16]);

// Total times HookedMap / HookedUnmap fired regardless of any
// filter. Zero ⇒ the vtable-hook on Map didn't catch DS2's calls
// (wrong vtable, wrong index, or DS2 uses UpdateSubresource).
uint64_t DS2_RenderHook_GetTotalMapCalls();
uint64_t DS2_RenderHook_GetTotalUnmapCalls();

// How many of those Map calls targeted a buffer with
// BIND_CONSTANT_BUFFER set. Also exposes the first 4 ByteWidth values
// observed so we can see what cbuffer sizes DS2 actually uses.
uint64_t DS2_RenderHook_GetCBufferMapCount();
void     DS2_RenderHook_GetFirstCBSizes(uint32_t out_sizes[4]);

// DS2 SOTFS updates its cbuffers via UpdateSubresource (verified
// empirically). These accessors expose the same per-call counters
// but for that path — total calls + those targeting CONSTANT_BUFFER
// + sizes of the first 4 unique cbuffer updates observed.
uint64_t DS2_RenderHook_GetTotalUpdateSubCalls();
uint64_t DS2_RenderHook_GetUpdateCBCallCount();
void     DS2_RenderHook_GetFirstUpdateCBSizes(uint32_t out_sizes[4]);

// v11f diagnostic: byte width of the cbuffer that was selected by the
// "first CB update per frame" heuristic. Zero until the first capture.
uint32_t DS2_RenderHook_GetCapturedCBSize();

// v11g diagnostic: copy up to 64 floats (256 bytes) of the most-recent
// first-of-frame cbuffer capture into out. Returns the number of valid
// floats copied (0 if no capture has happened yet).
uint32_t DS2_RenderHook_GetCapturedBufferDump(float out[64]);

// v12 diagnostic: parallel dump of the 576-byte cbuffer (the other
// strong VP candidate). Up to 144 floats (576 bytes).
uint32_t DS2_RenderHook_GetCapturedBuffer576Dump(float out[144]);

// v14 (2026-05-16): live VP construction from DS2 memory. Replaces
// the cbuffer-capture VP path. See TryReadCameraVP() in
// DS2_RenderHook.cpp for the gm_imp_global → camCfg pointer chain
// and the offsets we read each frame.
//
// GetLiveVPReadCount returns the number of frames where the chain
// resolved and the matrices passed sanity (>0 ⇒ overlay is drawing
// with real VP). GetLiveVPFailCount is non-zero in early frames
// before the AOB resolves, or if DS2 zeroes the camera matrix during
// loading screens / area transitions.
uint64_t DS2_RenderHook_GetLiveVPReadCount();
uint64_t DS2_RenderHook_GetLiveVPFailCount();

// Snapshot the current live VP into out_vp (row-major) for the
// heartbeat / external diagnostics. Returns false if the chain
// hasn't resolved yet on this frame. Safe to call from any thread —
// the underlying read is wrapped in __try/__except.
bool DS2_RenderHook_TryGetLiveVP(float out_vp[16]);

// ── Phase 4a (v16, 2026-05-16): multi-actor public API ──────────────
//
// The render hook can now draw N cubes per frame, one per "peer
// pose". Slot 0 is always the host (read live from chr+0x90 inside
// DrawOverlay); slots 1..N come from this setter. During Phase 4a
// we also emit a hard-coded ghost cube at host+(5,0,0) for visual
// proof that the multi-draw pipeline works without any network in
// the loop.
//
// In Phase 4b the BonfireService bridge thread will call
// DS2_RenderHook_SetPeerPoses() at ~30 Hz with the array of peer
// poses received over the UDP backbone. The lock around the table
// is an SRWLock — readers (the Present thread) take shared, the
// setter takes exclusive. Reads are wait-free on the contention
// path.
struct DS2_PeerPose
{
    float position[3];     // world-space feet position
    float yaw_radians;     // facing direction, rotation around world Y
    float color[3];        // RGB tint for this peer's cube
    uint32_t valid;        // 0 = slot empty / departed, 1 = active
    // v2.9.3 Track C Phase 1: per-peer identity carried alongside the
    // pose so the client-side interpolator (DS2_PoseShm) can match a
    // peer across snapshots even when the source-side iteration
    // order changes. Zero = "anonymous" / no identity available.
    int64_t sender_id;
};

// Publish a new peer-pose table. `count` is clamped to the internal
// kMaxPeers (currently 16). Slots past `count` are zeroed so the
// renderer doesn't keep drawing stale entries. Safe to call from
// any thread.
void DS2_RenderHook_SetPeerPoses(const DS2_PeerPose* poses, int count);

// Diagnostic accessors used by the runtime heartbeat.
int      DS2_RenderHook_GetPeerCount();
uint64_t DS2_RenderHook_GetMultiDrawFrames();
uint64_t DS2_RenderHook_GetMultiDrawCubesTotal();
