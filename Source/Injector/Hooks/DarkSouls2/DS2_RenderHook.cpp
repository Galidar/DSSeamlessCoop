/*
 * DSSeamlessCoop — Bonfire HKMP overlay render hook (2026-05-16, v8c)
 *
 * Phase-1 strategy v8c: DS2 SOTFS imports `D3D11CreateDevice` (NOT the
 * AndSwapChain variant — verified via dumpbin on the actual exe). It
 * uses the canonical 2-step D3D11 init flow:
 *   1. D3D11CreateDevice → get an ID3D11Device
 *   2. Query device → IDXGIDevice → IDXGIAdapter → IDXGIFactory
 *   3. factory->CreateSwapChain(device, &desc, &swapchain)
 *
 * So we:
 *   1. Hook the d3d11.dll export `D3D11CreateDevice`.
 *   2. Inside that hook, forward to original, then walk the device →
 *      factory chain and VMT-hook `IDXGIFactory::CreateSwapChain`
 *      (vtable index 10).
 *   3. Inside the factory hook, forward to original, then VMT-hook the
 *      returned IDXGISwapChain's `Present` (vtable index 8).
 *
 * This coexists with the "Darksouls Lighting Engine" mod because we
 * never create a dummy swap chain — we only observe DS2's real chain
 * after the lighting engine has fully set it up.
 */

#include "Injector/Hooks/DarkSouls2/DS2_RenderHook.h"
#include "Injector/Hooks/DarkSouls2/DS2_NativeRuntimeHook.h"
#include "Injector/Injector/Injector.h"
#include "Shared/Core/Utils/Logging.h"
#include "ThirdParty/detours/src/detours.h"

#include <Windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <dxgi.h>

#include <atomic>
#include <algorithm>
#include <cstdint>
#include <cstring>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "dxguid.lib")
#pragma comment(lib, "d3dcompiler.lib")

namespace
{
    using PresentFn = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT);

    using CreateDeviceFn = HRESULT(WINAPI*)(
        IDXGIAdapter*, D3D_DRIVER_TYPE, HMODULE, UINT,
        const D3D_FEATURE_LEVEL*, UINT, UINT,
        ID3D11Device**, D3D_FEATURE_LEVEL*, ID3D11DeviceContext**);

    using FactoryCreateSwapChainFn = HRESULT(STDMETHODCALLTYPE*)(
        IDXGIFactory*, IUnknown*, DXGI_SWAP_CHAIN_DESC*, IDXGISwapChain**);

    CreateDeviceFn s_original_create_device = nullptr;
    FactoryCreateSwapChainFn s_original_factory_create_sc = nullptr;
    PresentFn s_original_present = nullptr;

    std::atomic<uint64_t> s_frame_count{0};
    std::atomic<IDXGISwapChain*> s_observed_swap{nullptr};

    LONG s_install_attempted = 0;
    LONG s_create_device_hooked = 0;
    LONG s_factory_hooked = 0;
    LONG s_present_hooked = 0;
    LONG s_installed = 0;
    HANDLE s_install_thread = nullptr;

    // ── Phase 2/3 overlay draw state ─────────────────────────────────
    // Cached from HookedCreateDevice and used inside HookedPresent.
    // Refs are held until process exit; we never release.
    ID3D11Device*           s_d3d_device = nullptr;
    ID3D11DeviceContext*    s_d3d_context = nullptr;
    ID3D11VertexShader*     s_overlay_vs = nullptr;
    ID3D11PixelShader*      s_overlay_ps = nullptr;
    ID3D11Buffer*           s_overlay_cbuffer = nullptr;  // Phase 3 dynamic cbuffer
    ID3D11VertexShader*     s_screen_vs = nullptr;        // 2D in-game prompt
    ID3D11PixelShader*      s_screen_ps = nullptr;
    ID3D11Buffer*           s_screen_cbuffer = nullptr;
    LONG                    s_overlay_init_state = 0;  // 0=pending, 1=ok, 2=failed

    SRWLOCK s_invite_prompt_lock = SRWLOCK_INIT;
    bool s_invite_prompt_visible = false;
    char s_invite_prompt_title[96] = {};
    char s_invite_prompt_body[128] = {};
    char s_invite_prompt_hint[96] = {};

    // ── Phase 3: Map/Unmap hooks to capture DS2's per-frame VP ───────
    // Hooks the device context's vtable[14]=Map and vtable[15]=Unmap.
    // When DS2 maps a small dynamic constant buffer with WRITE_DISCARD,
    // we treat the FIRST 64 bytes as a candidate view-projection matrix
    // and stash them in s_captured_vp[16]. Cero GPU sync — we only read
    // a CPU pointer that DS2 itself just wrote.
    using MapFn = HRESULT (STDMETHODCALLTYPE*)(
        ID3D11DeviceContext*, ID3D11Resource*, UINT,
        D3D11_MAP, UINT, D3D11_MAPPED_SUBRESOURCE*);
    using UnmapFn = void (STDMETHODCALLTYPE*)(
        ID3D11DeviceContext*, ID3D11Resource*, UINT);
    using UpdateSubresourceFn = void (STDMETHODCALLTYPE*)(
        ID3D11DeviceContext*, ID3D11Resource*, UINT,
        const D3D11_BOX*, const void*, UINT, UINT);

    MapFn               s_original_map         = nullptr;
    UnmapFn             s_original_unmap       = nullptr;
    UpdateSubresourceFn s_original_update_sub  = nullptr;
    LONG                s_map_unmap_hooked     = 0;
    std::atomic<uint64_t> s_total_update_sub_calls{0};
    std::atomic<uint64_t> s_update_cb_calls{0};
    UINT                s_first_update_cb_sizes[4] = { 0, 0, 0, 0 };

    // v11f: per-frame first-capture state. HookedPresent bumps the
    // frame marker each frame; HookedUpdateSubresource only captures
    // the FIRST CB update that matches our heuristic per frame. This
    // narrows the candidate from "any cbuffer DS2 writes" to "the
    // earliest one in each frame" — typically per-frame globals
    // like the camera view-projection matrix.
    std::atomic<uint64_t> s_frame_marker{0};
    std::atomic<uint64_t> s_last_capture_frame{0xFFFFFFFFFFFFFFFFull};
    std::atomic<UINT>     s_captured_cb_size{0};

    // In-flight Map state. Assumes single-threaded immediate-context
    // usage (DS2's pattern). For deferred contexts this would race.
    ID3D11Resource* s_inflight_resource = nullptr;
    void*           s_inflight_ptr      = nullptr;
    UINT            s_inflight_size     = 0;
    bool            s_inflight_is_vp_candidate = false;

    // Captured VP, atomic-ish via mutex-free single-thread write +
    // memcpy on read. The Present hook runs on the same render thread
    // as Map/Unmap so contention is minimal.
    float                 s_captured_vp[16] = {};
    std::atomic<uint64_t> s_vp_capture_count{0};

    // v14: live VP read from DS2 memory (replaces the cbuffer-capture
    // path which never contained a usable VP matrix — verified via
    // Cheat Engine on 2026-05-16). See TryReadCameraVP() below for the
    // pointer chain and offsets.
    std::atomic<uint64_t> s_live_vp_read_count{0};
    std::atomic<uint64_t> s_live_vp_fail_count{0};

    // v16 (Phase 4a): multi-actor render. DrawOverlay iterates over
    // this table each frame and emits one cube per entry. The host's
    // own pose is folded in implicitly at draw time (peer 0 is always
    // the host, read from chr+0x90). Slots 1..N are filled either by
    // (a) a hard-coded ghost during 4a sanity-check, or (b) the IPC
    // setter DS2_RenderHook_SetPeerPoses() in 4b once BonfireService
    // starts publishing peer poses from the network.
    constexpr int kMaxPeers = 16;
    struct PeerPoseEntry
    {
        float position[3];
        float yaw_radians;
        float color[3];
        uint32_t valid;   // 0 = empty slot, 1 = active
    };
    static_assert(sizeof(PeerPoseEntry) == 32,
                  "PeerPoseEntry layout must stay POD/32-byte for IPC");

    // Double-buffered table. Reader (Present thread) takes a shared
    // snapshot; setter (BonfireService bridge thread) writes
    // exclusively. SRWLock is the lightest reader-writer primitive
    // Win32 offers — same one CE uses internally.
    SRWLOCK         s_peer_table_lock = SRWLOCK_INIT;
    PeerPoseEntry   s_peer_table[kMaxPeers] = {};
    std::atomic<int> s_peer_table_count{0};

    // Diagnostic counters for the heartbeat.
    std::atomic<uint64_t> s_multi_draw_frames{0};
    std::atomic<uint64_t> s_multi_draw_cubes_total{0};

    // v11g: capture full 256 bytes (64 floats) of the first-of-frame
    // cbuffer so we can dump the entire content via the heartbeat and
    // visually identify where the VP matrix actually lives. Once we
    // know the offset we'll switch s_captured_vp to read from that
    // exact position.
    float                 s_captured_buffer_full[64] = {};
    UINT                  s_captured_buffer_size = 0;
    // v12: parallel diagnostic capture of the 576-byte cbuffer (the
    // second candidate). Independent of the primary VP filter so we
    // can compare 160-byte vs 576-byte dumps side-by-side and pick
    // the winner without another rebuild.
    float                 s_captured_buffer_576[144] = {};
    UINT                  s_captured_buffer_576_size = 0;
    std::atomic<uint64_t> s_last_capture_576_frame{0xFFFFFFFFFFFFFFFFull};

    // Diagnostic counters that increment on EVERY Map / Unmap call
    // regardless of filter — lets us distinguish "hook never fires"
    // (counters 0) from "hook fires but filter rejects" (counters
    // grow but vp_capture_count stays 0).
    std::atomic<uint64_t> s_total_map_calls{0};
    std::atomic<uint64_t> s_total_unmap_calls{0};
    std::atomic<uint64_t> s_map_cb_dynamic{0};   // Map of dynamic cb (any size)
    std::atomic<uint64_t> s_map_buffer_any{0};   // Map of any buffer
    UINT                  s_first_cb_byte_widths[4] = { 0, 0, 0, 0 };  // capture sizes of first 4 cbuffer maps

    // ── Phase 3: Map/Unmap hook implementation ───────────────────────
    // Track every Map that targets a small dynamic constant buffer.
    // On the matching Unmap we copy the first 64 bytes (candidate VP)
    // into s_captured_vp before forwarding to the original Unmap.
    HRESULT STDMETHODCALLTYPE HookedMap(
        ID3D11DeviceContext* ctx,
        ID3D11Resource*      resource,
        UINT                 subresource,
        D3D11_MAP            map_type,
        UINT                 map_flags,
        D3D11_MAPPED_SUBRESOURCE* out_mapped)
    {
        s_total_map_calls.fetch_add(1, std::memory_order_relaxed);

        HRESULT hr = s_original_map(
            ctx, resource, subresource, map_type, map_flags, out_mapped);
        if (FAILED(hr) || out_mapped == nullptr || out_mapped->pData == nullptr)
        {
            return hr;
        }

        // Reset in-flight unless this map is a VP candidate.
        s_inflight_is_vp_candidate = false;

        D3D11_RESOURCE_DIMENSION dim = D3D11_RESOURCE_DIMENSION_UNKNOWN;
        resource->GetType(&dim);
        if (dim != D3D11_RESOURCE_DIMENSION_BUFFER) return hr;

        s_map_buffer_any.fetch_add(1, std::memory_order_relaxed);

        D3D11_BUFFER_DESC bd = {};
        static_cast<ID3D11Buffer*>(resource)->GetDesc(&bd);

        const bool is_cb = (bd.BindFlags & D3D11_BIND_CONSTANT_BUFFER) != 0;
        if (!is_cb) return hr;

        // Capture the first few unique cbuffer sizes so we can see
        // what DS2 actually uses (diagnostic). Stored as packed array
        // — not bulletproof against races but good enough for debug.
        const uint64_t cb_idx = s_map_cb_dynamic.fetch_add(1, std::memory_order_relaxed);
        if (cb_idx < 4) s_first_cb_byte_widths[cb_idx] = bd.ByteWidth;

        // Widened filter: any cbuffer in 16..16384 bytes counts as a
        // VP candidate. Drop the WRITE_DISCARD-only check — accept
        // any map type that has a writable pData (it does here since
        // we already passed the FAILED check).
        const bool size_ok = bd.ByteWidth >= 16 && bd.ByteWidth <= 16384;
        if (!size_ok) return hr;

        s_inflight_resource         = resource;
        s_inflight_ptr              = out_mapped->pData;
        s_inflight_size             = bd.ByteWidth;
        s_inflight_is_vp_candidate  = true;
        return hr;
    }

    void STDMETHODCALLTYPE HookedUnmap(
        ID3D11DeviceContext* ctx,
        ID3D11Resource*      resource,
        UINT                 subresource)
    {
        s_total_unmap_calls.fetch_add(1, std::memory_order_relaxed);

        if (s_inflight_is_vp_candidate &&
            s_inflight_resource == resource &&
            s_inflight_ptr != nullptr &&
            s_inflight_size >= 64)
        {
            memcpy(s_captured_vp, s_inflight_ptr, sizeof(s_captured_vp));
            s_vp_capture_count.fetch_add(1, std::memory_order_relaxed);
        }
        s_inflight_resource        = nullptr;
        s_inflight_ptr             = nullptr;
        s_inflight_size            = 0;
        s_inflight_is_vp_candidate = false;

        s_original_unmap(ctx, resource, subresource);
    }

    // DS2 SOTFS updates its constant buffers via UpdateSubresource
    // (NOT Map/Unmap — verified empirically by counting 729k Map
    // calls in 30s with 0 of them targeting a CONSTANT_BUFFER).
    // pSrcData points to the new bytes the engine wants in the
    // resource, so a 64-byte memcpy is enough to grab the candidate
    // VP without any GPU sync.
    void STDMETHODCALLTYPE HookedUpdateSubresource(
        ID3D11DeviceContext* ctx,
        ID3D11Resource*      dst_resource,
        UINT                 dst_subresource,
        const D3D11_BOX*     dst_box,
        const void*          src_data,
        UINT                 src_row_pitch,
        UINT                 src_depth_pitch)
    {
        s_total_update_sub_calls.fetch_add(1, std::memory_order_relaxed);

        if (dst_resource != nullptr && src_data != nullptr)
        {
            D3D11_RESOURCE_DIMENSION dim = D3D11_RESOURCE_DIMENSION_UNKNOWN;
            dst_resource->GetType(&dim);
            if (dim == D3D11_RESOURCE_DIMENSION_BUFFER)
            {
                D3D11_BUFFER_DESC bd = {};
                static_cast<ID3D11Buffer*>(dst_resource)->GetDesc(&bd);

                const bool is_cb =
                    (bd.BindFlags & D3D11_BIND_CONSTANT_BUFFER) != 0;
                if (is_cb)
                {
                    const uint64_t cb_idx = s_update_cb_calls.fetch_add(
                        1, std::memory_order_relaxed);
                    if (cb_idx < 4)
                    {
                        s_first_update_cb_sizes[cb_idx] = bd.ByteWidth;
                    }
                    // v12 parallel diagnostic: ALSO capture a 576-byte
                    // cbuffer (independent of the primary VP path) so
                    // we can compare side-by-side. Runs first so its
                    // frame gate is separate.
                    if (bd.ByteWidth == 576)
                    {
                        const uint64_t cur_f =
                            s_frame_marker.load(std::memory_order_acquire);
                        uint64_t exp576 = s_last_capture_576_frame.load(
                            std::memory_order_acquire);
                        if (exp576 != cur_f &&
                            s_last_capture_576_frame.compare_exchange_strong(
                                exp576, cur_f,
                                std::memory_order_acq_rel))
                        {
                            const UINT n =
                                bd.ByteWidth < sizeof(s_captured_buffer_576)
                                ? bd.ByteWidth
                                : static_cast<UINT>(sizeof(s_captured_buffer_576));
                            memcpy(s_captured_buffer_576, src_data, n);
                            s_captured_buffer_576_size = n;
                        }
                    }

                    // v12 primary: filter to size=160 (textbook
                    // "camera uniforms" — VP 64 + view 64 + cam_pos 16
                    // + viewport 16). This is what the shader reads.
                    if (bd.ByteWidth == 160)
                    {
                        const uint64_t cur_frame =
                            s_frame_marker.load(std::memory_order_acquire);
                        uint64_t expected = s_last_capture_frame.load(
                            std::memory_order_acquire);
                        if (expected != cur_frame &&
                            s_last_capture_frame.compare_exchange_strong(
                                expected, cur_frame,
                                std::memory_order_acq_rel))
                        {
                            memcpy(s_captured_vp, src_data, sizeof(s_captured_vp));
                            // v11g: also copy as much as we can (up to
                            // 256 bytes) so the heartbeat can dump the
                            // full cbuffer contents for offset hunting.
                            const UINT full_bytes =
                                bd.ByteWidth < sizeof(s_captured_buffer_full)
                                ? bd.ByteWidth
                                : static_cast<UINT>(sizeof(s_captured_buffer_full));
                            memcpy(s_captured_buffer_full, src_data, full_bytes);
                            s_captured_buffer_size = full_bytes;
                            s_captured_cb_size.store(
                                bd.ByteWidth, std::memory_order_release);
                            s_vp_capture_count.fetch_add(
                                1, std::memory_order_relaxed);
                        }
                    }
                }
            }
        }

        s_original_update_sub(
            ctx, dst_resource, dst_subresource, dst_box,
            src_data, src_row_pitch, src_depth_pitch);
    }

    bool HookContextMapUnmap(ID3D11DeviceContext* ctx)
    {
        if (ctx == nullptr) return false;
        if (InterlockedCompareExchange(&s_map_unmap_hooked, 1, 0) != 0)
        {
            return true;
        }

        // ID3D11DeviceContext vtable (after IUnknown + ID3D11DeviceChild):
        //   index 14 = Map
        //   index 15 = Unmap
        //   index 48 = UpdateSubresource
        void** vtable = *reinterpret_cast<void***>(ctx);
        MapFn               target_map        = reinterpret_cast<MapFn>(vtable[14]);
        UnmapFn             target_unmap      = reinterpret_cast<UnmapFn>(vtable[15]);
        UpdateSubresourceFn target_update_sub = reinterpret_cast<UpdateSubresourceFn>(vtable[48]);
        Log("DS2_RenderHook: ctx=%p vt=%p Map=%p Unmap=%p UpdSub=%p",
            ctx, vtable, target_map, target_unmap, target_update_sub);

        s_original_map        = target_map;
        s_original_unmap      = target_unmap;
        s_original_update_sub = target_update_sub;

        LONG err = DetourTransactionBegin();
        if (err == NO_ERROR) err = DetourUpdateThread(GetCurrentThread());
        if (err == NO_ERROR)
        {
            err = DetourAttach(
                reinterpret_cast<PVOID*>(&s_original_map),
                reinterpret_cast<PVOID>(HookedMap));
        }
        if (err == NO_ERROR)
        {
            err = DetourAttach(
                reinterpret_cast<PVOID*>(&s_original_unmap),
                reinterpret_cast<PVOID>(HookedUnmap));
        }
        if (err == NO_ERROR)
        {
            err = DetourAttach(
                reinterpret_cast<PVOID*>(&s_original_update_sub),
                reinterpret_cast<PVOID>(HookedUpdateSubresource));
        }
        if (err == NO_ERROR) err = DetourTransactionCommit();

        if (err != NO_ERROR)
        {
            Log("DS2_RenderHook: DetourAttach(Map/Unmap/UpdSub) failed err=%ld", err);
            s_original_map = nullptr;
            s_original_unmap = nullptr;
            s_original_update_sub = nullptr;
            InterlockedExchange(&s_map_unmap_hooked, 0);
            return false;
        }

        Log("DS2_RenderHook: Map/Unmap/UpdateSubresource detours installed");
        return true;
    }

    // ── Phase 3 shader sources ───────────────────────────────────────
    // Vertex shader reads a constant buffer at slot 0 containing:
    //   - VP matrix captured by HookedMap/HookedUnmap (see s_captured_vp)
    //   - anchor: world-space anchor (host player position from the
    //     locked gm+0x18 → +0x50 → +0x90 chain)
    //   - scale: dimensions of the visual placeholder
    // Generates 6 vertices for a flat horizontal quad 5 units above
    // the anchor. World-space → clip-space via mul(world, VP).
    //
    // row_major hint forces HLSL to interpret the cbuffer floats
    // row-by-row, matching most engines including Souls. If the cube
    // shows up in the wrong place after first run we flip to
    // column_major in the next iteration.
    static constexpr const char* kOverlayVSSource = R"(
cbuffer OverlayCB : register(b0)
{
    float4x4 VP;
    float3   anchor;
    float    scale;
    float3   color;
    float    yaw_radians;
};

struct VSOut
{
    float4 pos    : SV_Position;
    float3 normal : NORMAL;
    float3 color  : COLOR;
};

// v15 (Phase 3) cube → v16 (Phase 4a) per-instance color + yaw →
// v19 (Phase 5a) humanoid placeholder.
//
// Peer placeholder is now TWO boxes — a torso (~0.5 × 1.2 × 0.3 world
// units) and a head (~0.3 cube on top). That's the first step toward
// "peers look like players". Total 72 vertices, indexed by SV_VertexID:
//   id 0..35  → torso box (cube_verts as before)
//   id 36..71 → head box (same cube_verts, different position+size)
//
// Both boxes share the cbuffer's anchor / scale / yaw / color and use
// the same lambert shader. The 'scale' uniform now controls the
// overall size of the silhouette — at scale=1 the figure is roughly
// player-sized (~1.7 m tall in DS2 world units).
VSOut main(uint id : SV_VertexID)
{
    const float3 cube_verts[36] = {
        // -Y face (bottom)
        float3(-1,-1,-1), float3( 1,-1,-1), float3(-1,-1, 1),
        float3( 1,-1,-1), float3( 1,-1, 1), float3(-1,-1, 1),
        // +Y face (top)
        float3(-1, 1,-1), float3(-1, 1, 1), float3( 1, 1,-1),
        float3( 1, 1,-1), float3(-1, 1, 1), float3( 1, 1, 1),
        // -X face (left)
        float3(-1,-1,-1), float3(-1,-1, 1), float3(-1, 1,-1),
        float3(-1, 1,-1), float3(-1,-1, 1), float3(-1, 1, 1),
        // +X face (right)
        float3( 1,-1,-1), float3( 1, 1,-1), float3( 1,-1, 1),
        float3( 1, 1,-1), float3( 1, 1, 1), float3( 1,-1, 1),
        // -Z face (back)
        float3(-1,-1,-1), float3(-1, 1,-1), float3( 1,-1,-1),
        float3( 1,-1,-1), float3(-1, 1,-1), float3( 1, 1,-1),
        // +Z face (front)
        float3(-1,-1, 1), float3( 1,-1, 1), float3(-1, 1, 1),
        float3(-1, 1, 1), float3( 1,-1, 1), float3( 1, 1, 1)
    };
    const float3 face_normals[6] = {
        float3( 0,-1, 0), float3( 0, 1, 0),
        float3(-1, 0, 0), float3( 1, 0, 0),
        float3( 0, 0,-1), float3( 0, 0, 1)
    };

    // Each box is a (half_size, center) pair. Both quantities are in
    // local body space — Y up, +Z = facing direction, feet at y=0.
    // half_size is half of the box's full extent on each axis so a
    // [-1,+1] unit cube vert maps to [-half, +half] when multiplied.
    const float3 box_half_size[2] = {
        float3(0.25, 0.6, 0.15),  // torso: 0.5 w × 1.2 h × 0.3 d
        float3(0.15, 0.15, 0.15)  // head: 0.3 cube
    };
    const float3 box_center[2] = {
        float3(0, 0.6, 0),    // torso center: y = 0.6 (so feet at 0, top at 1.2)
        float3(0, 1.35, 0)    // head center: y = 1.35 (sits on top of torso)
    };

    uint box_idx = id / 36;
    uint vert_idx = id % 36;

    // Local-body position: unit cube vert → box-sized → translated
    // into the body's coordinate frame.
    float3 local = cube_verts[vert_idx] * box_half_size[box_idx]
                 + box_center[box_idx];

    // Uniform scale applied to the whole figure — at scale=1.0 the
    // silhouette is ~1.65 m tall (head top at y=1.5 then × scale).
    float3 v = local * scale;

    // Yaw rotation around world Y so peers face the direction they're
    // actually facing in-world.
    float cy = cos(yaw_radians);
    float sy = sin(yaw_radians);
    float3 rotated = float3(
        cy * v.x + sy * v.z,
        v.y,
       -sy * v.x + cy * v.z);

    // Translate to peer's world position (feet anchor).
    float3 world_pos = anchor + rotated;

    VSOut o;
    o.pos    = mul(VP, float4(world_pos, 1.0));
    // Rotate normal too so lambert shading stays consistent after
    // yaw rotation. Face normal is the same per-face regardless of
    // which box we're in — it's a property of the cube primitive.
    float3 n = face_normals[vert_idx / 6];
    o.normal = float3(cy * n.x + sy * n.z, n.y, -sy * n.x + cy * n.z);
    o.color  = color;
    return o;
}
)";

    static constexpr const char* kOverlayPSSource = R"(
struct PSIn
{
    float4 pos    : SV_Position;
    float3 normal : NORMAL;
    float3 color  : COLOR;
};

float4 main(PSIn input) : SV_Target
{
    // v15 → v16: same cheap lambert shade, but the base color is now
    // per-instance (carried over from VS via the COLOR semantic).
    float3 light_dir = normalize(float3(0.4, -1.0, 0.3));
    float ndl = saturate(dot(normalize(input.normal), -light_dir));
    float3 shaded = input.color * (0.35 + 0.65 * ndl);
    return float4(shaded, 0.95);
}
)";

    static constexpr const char* kScreenVSSource = R"(
cbuffer ScreenCB : register(b0)
{
    float4 rect;
    float4 color;
    float2 screen;
    float2 _pad;
};

struct VSOut
{
    float4 pos   : SV_Position;
    float4 color : COLOR;
};

VSOut main(uint id : SV_VertexID)
{
    const float2 verts[6] = {
        float2(0,0), float2(1,0), float2(0,1),
        float2(1,0), float2(1,1), float2(0,1)
    };
    float2 p = rect.xy + verts[id] * rect.zw;
    float2 clip = float2((p.x / screen.x) * 2.0 - 1.0,
                         1.0 - (p.y / screen.y) * 2.0);
    VSOut o;
    o.pos = float4(clip, 0.0, 1.0);
    o.color = color;
    return o;
}
)";

    static constexpr const char* kScreenPSSource = R"(
struct PSIn
{
    float4 pos   : SV_Position;
    float4 color : COLOR;
};

float4 main(PSIn input) : SV_Target
{
    return input.color;
}
)";

    // Layout MUST match the HLSL cbuffer.
    //  v15 was 80 bytes (5×16).
    //  v16 adds color + yaw_radians = 6×16 = 96 bytes.
    struct OverlayCBData
    {
        float vp[16];          // offsets   0..63
        float anchor[3];       // offsets  64..75
        float scale;           // offset   76..79
        float color[3];        // offsets  80..91
        float yaw_radians;     // offset   92..95
    };
    static_assert(sizeof(OverlayCBData) == 96,
                  "OverlayCBData must match HLSL cbuffer (96 bytes)");

    struct ScreenCBData
    {
        float rect[4];
        float color[4];
        float screen[2];
        float pad[2];
    };
    static_assert(sizeof(ScreenCBData) == 48,
                  "ScreenCBData must match HLSL cbuffer (48 bytes)");

    // Compile both shaders + create shader objects. Called once on the
    // first Present that observes a cached device pointer. Sets
    // s_overlay_init_state to 1 on success or 2 on failure (so we
    // don't retry every frame after a failure).
    void LazyInitOverlay()
    {
        if (InterlockedCompareExchange(&s_overlay_init_state, 1 /* ok-pending */, 0) != 0)
        {
            return;  // Already initialized or failed
        }

        if (s_d3d_device == nullptr)
        {
            // Device not cached yet — leave state pending so we retry.
            InterlockedExchange(&s_overlay_init_state, 0);
            return;
        }

        ID3DBlob* vs_blob = nullptr;
        ID3DBlob* ps_blob = nullptr;
        ID3DBlob* err_blob = nullptr;

        HRESULT hr = D3DCompile(
            kOverlayVSSource, strlen(kOverlayVSSource),
            "ds2_overlay_vs", nullptr, nullptr,
            "main", "vs_4_0", 0, 0, &vs_blob, &err_blob);
        if (FAILED(hr))
        {
            Log("DS2_RenderHook: VS compile failed hr=0x%08lX msg=%s",
                static_cast<unsigned long>(hr),
                err_blob ? static_cast<const char*>(err_blob->GetBufferPointer())
                         : "(no message)");
            if (err_blob) err_blob->Release();
            InterlockedExchange(&s_overlay_init_state, 2);
            return;
        }
        if (err_blob) { err_blob->Release(); err_blob = nullptr; }

        hr = D3DCompile(
            kOverlayPSSource, strlen(kOverlayPSSource),
            "ds2_overlay_ps", nullptr, nullptr,
            "main", "ps_4_0", 0, 0, &ps_blob, &err_blob);
        if (FAILED(hr))
        {
            Log("DS2_RenderHook: PS compile failed hr=0x%08lX msg=%s",
                static_cast<unsigned long>(hr),
                err_blob ? static_cast<const char*>(err_blob->GetBufferPointer())
                         : "(no message)");
            vs_blob->Release();
            if (err_blob) err_blob->Release();
            InterlockedExchange(&s_overlay_init_state, 2);
            return;
        }
        if (err_blob) { err_blob->Release(); err_blob = nullptr; }

        hr = s_d3d_device->CreateVertexShader(
            vs_blob->GetBufferPointer(), vs_blob->GetBufferSize(),
            nullptr, &s_overlay_vs);
        if (FAILED(hr))
        {
            Log("DS2_RenderHook: CreateVertexShader failed hr=0x%08lX",
                static_cast<unsigned long>(hr));
            vs_blob->Release();
            ps_blob->Release();
            InterlockedExchange(&s_overlay_init_state, 2);
            return;
        }

        hr = s_d3d_device->CreatePixelShader(
            ps_blob->GetBufferPointer(), ps_blob->GetBufferSize(),
            nullptr, &s_overlay_ps);
        vs_blob->Release();
        ps_blob->Release();
        if (FAILED(hr))
        {
            Log("DS2_RenderHook: CreatePixelShader failed hr=0x%08lX",
                static_cast<unsigned long>(hr));
            s_overlay_vs->Release();
            s_overlay_vs = nullptr;
            InterlockedExchange(&s_overlay_init_state, 2);
            return;
        }

        // Phase 3: dynamic constant buffer the VS reads each frame.
        // 80 bytes per OverlayCBData. USAGE_DYNAMIC + CPU_ACCESS_WRITE
        // lets us Map(WRITE_DISCARD) per draw for fresh VP + anchor.
        D3D11_BUFFER_DESC cb_desc = {};
        cb_desc.ByteWidth      = sizeof(OverlayCBData);
        cb_desc.Usage          = D3D11_USAGE_DYNAMIC;
        cb_desc.BindFlags      = D3D11_BIND_CONSTANT_BUFFER;
        cb_desc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        hr = s_d3d_device->CreateBuffer(&cb_desc, nullptr, &s_overlay_cbuffer);
        if (FAILED(hr) || s_overlay_cbuffer == nullptr)
        {
            Log("DS2_RenderHook: CreateBuffer(overlay cbuffer) failed hr=0x%08lX",
                static_cast<unsigned long>(hr));
            s_overlay_vs->Release(); s_overlay_vs = nullptr;
            s_overlay_ps->Release(); s_overlay_ps = nullptr;
            InterlockedExchange(&s_overlay_init_state, 2);
            return;
        }

        ID3DBlob* screen_vs_blob = nullptr;
        ID3DBlob* screen_ps_blob = nullptr;
        err_blob = nullptr;
        hr = D3DCompile(
            kScreenVSSource, strlen(kScreenVSSource),
            "ds2_screen_vs", nullptr, nullptr,
            "main", "vs_4_0", 0, 0, &screen_vs_blob, &err_blob);
        if (FAILED(hr))
        {
            Log("DS2_RenderHook: screen VS compile failed hr=0x%08lX msg=%s",
                static_cast<unsigned long>(hr),
                err_blob ? static_cast<const char*>(err_blob->GetBufferPointer())
                         : "(no message)");
            if (err_blob) err_blob->Release();
        }
        if (err_blob) { err_blob->Release(); err_blob = nullptr; }

        if (screen_vs_blob != nullptr)
        {
            hr = D3DCompile(
                kScreenPSSource, strlen(kScreenPSSource),
                "ds2_screen_ps", nullptr, nullptr,
                "main", "ps_4_0", 0, 0, &screen_ps_blob, &err_blob);
            if (FAILED(hr))
            {
                Log("DS2_RenderHook: screen PS compile failed hr=0x%08lX msg=%s",
                    static_cast<unsigned long>(hr),
                    err_blob ? static_cast<const char*>(err_blob->GetBufferPointer())
                             : "(no message)");
                if (err_blob) err_blob->Release();
                screen_vs_blob->Release();
                screen_vs_blob = nullptr;
            }
            if (err_blob) { err_blob->Release(); err_blob = nullptr; }
        }

        if (screen_vs_blob != nullptr && screen_ps_blob != nullptr)
        {
            HRESULT screen_hr = s_d3d_device->CreateVertexShader(
                screen_vs_blob->GetBufferPointer(),
                screen_vs_blob->GetBufferSize(),
                nullptr,
                &s_screen_vs);
            if (SUCCEEDED(screen_hr))
            {
                screen_hr = s_d3d_device->CreatePixelShader(
                    screen_ps_blob->GetBufferPointer(),
                    screen_ps_blob->GetBufferSize(),
                    nullptr,
                    &s_screen_ps);
            }
            screen_vs_blob->Release();
            screen_ps_blob->Release();

            D3D11_BUFFER_DESC screen_cb_desc = {};
            screen_cb_desc.ByteWidth = sizeof(ScreenCBData);
            screen_cb_desc.Usage = D3D11_USAGE_DYNAMIC;
            screen_cb_desc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
            screen_cb_desc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
            if (SUCCEEDED(screen_hr))
            {
                screen_hr = s_d3d_device->CreateBuffer(
                    &screen_cb_desc, nullptr, &s_screen_cbuffer);
            }
            if (FAILED(screen_hr) ||
                s_screen_vs == nullptr ||
                s_screen_ps == nullptr ||
                s_screen_cbuffer == nullptr)
            {
                Log("DS2_RenderHook: in-game prompt pipeline unavailable hr=0x%08lX",
                    static_cast<unsigned long>(screen_hr));
                if (s_screen_vs) { s_screen_vs->Release(); s_screen_vs = nullptr; }
                if (s_screen_ps) { s_screen_ps->Release(); s_screen_ps = nullptr; }
                if (s_screen_cbuffer) { s_screen_cbuffer->Release(); s_screen_cbuffer = nullptr; }
            }
        }

        InterlockedExchange(&s_overlay_init_state, 1);
        Log("DS2_RenderHook: Phase-3 overlay pipeline ready "
            "(vs=%p ps=%p cb=%p)",
            s_overlay_vs, s_overlay_ps, s_overlay_cbuffer);
    }

    // v14: Read DS2's live camera state directly from memory and
    // build a View-Projection matrix from it. Replaces the cbuffer
    // hook capture path (s_captured_vp) which never contained a
    // usable VP matrix — DS2 SOTFS composes V × P in the shader and
    // never writes the combined VP to any cbuffer we can snoop.
    //
    // Pointer chain (resolved interactively via Cheat Engine on
    // 2026-05-16, anchored at the gm_imp_global global resolved by
    // DS2_NativeRuntimeHook's AOB):
    //
    //   gm_imp_global ──(read pointer)──▶ gm
    //   gm + 0xD0     ──(read pointer)──▶ p1  (= player ChrIns also)
    //   p1 + 0xE8     ──(read pointer)──▶ p2
    //   p2 + 0x18     ──(read pointer)──▶ p3
    //   p3 + 0x28     ──(read pointer)──▶ camCfg
    //
    // camCfg layout (verified LIVE — values track player input):
    //   +0x4E0  float[16]  camera-to-world transform (row-major)
    //                       row 0 = right axis (rx, ry, rz, 0)
    //                       row 1 = up axis    (ux, uy, uz, 0)
    //                       row 2 = forward    (fx, fy, fz, 0)
    //                       row 3 = eye pos    (ex, ey, ez, 1)
    //   +0x520  float[16]  D3D LH perspective projection (row-major)
    //                       m[0][0] = 1 / (aspect * tan(fovY/2))
    //                       m[1][1] = 1 / tan(fovY/2)
    //                       m[2][2] = far/(far-near) ≈ 1.0
    //                       m[2][3] = 1.0
    //                       m[3][2] = -near*m[2][2] ≈ -0.1
    //                       m[3][3] = 0
    //   +0xD04  float      camFovY (radians; default ~0.7679 ≈ 44°)
    //   +0xD18  float      cam distance from target (default 3.6)
    //   +0xD10  float      target Y offset above player feet (1.42)
    //
    // We compute VP = view * proj where view = inverse(cam_to_world)
    // (transpose for orthonormal R, negated translation). Output is
    // row-major matching how the existing HLSL shader expects it via
    // mul(VP, float4(world_pos, 1.0)).
    bool TryReadCameraVP(float vp_out[16])
    {
        const uintptr_t gm_imp_global =
            DS2_NativeRuntimeHook_GetGameManagerImpAddress();
        if (gm_imp_global == 0)
        {
            s_live_vp_fail_count.fetch_add(1, std::memory_order_relaxed);
            return false;
        }

        __try
        {
            uintptr_t gm = *reinterpret_cast<const uintptr_t*>(gm_imp_global);
            if (gm == 0) { s_live_vp_fail_count.fetch_add(1, std::memory_order_relaxed); return false; }
            uintptr_t p1 = *reinterpret_cast<const uintptr_t*>(gm + 0xD0);
            if (p1 == 0) { s_live_vp_fail_count.fetch_add(1, std::memory_order_relaxed); return false; }
            uintptr_t p2 = *reinterpret_cast<const uintptr_t*>(p1 + 0xE8);
            if (p2 == 0) { s_live_vp_fail_count.fetch_add(1, std::memory_order_relaxed); return false; }
            uintptr_t p3 = *reinterpret_cast<const uintptr_t*>(p2 + 0x18);
            if (p3 == 0) { s_live_vp_fail_count.fetch_add(1, std::memory_order_relaxed); return false; }
            uintptr_t camCfg = *reinterpret_cast<const uintptr_t*>(p3 + 0x28);
            if (camCfg == 0) { s_live_vp_fail_count.fetch_add(1, std::memory_order_relaxed); return false; }

            const float* cam_to_world =
                reinterpret_cast<const float*>(camCfg + 0x4E0);
            const float* proj =
                reinterpret_cast<const float*>(camCfg + 0x520);

            // Sanity: the 3x3 rotation part of cam_to_world must be
            // orthonormal. If anything is broken (paused, area swap,
            // null camera) the matrix is often zeroed.
            const float rx = cam_to_world[0],  ry = cam_to_world[1],  rz = cam_to_world[2];
            const float ux = cam_to_world[4],  uy = cam_to_world[5],  uz = cam_to_world[6];
            const float fx = cam_to_world[8],  fy = cam_to_world[9],  fz = cam_to_world[10];
            const float r_mag2 = rx*rx + ry*ry + rz*rz;
            const float u_mag2 = ux*ux + uy*uy + uz*uz;
            const float f_mag2 = fx*fx + fy*fy + fz*fz;
            if (r_mag2 < 0.9f || r_mag2 > 1.1f ||
                u_mag2 < 0.9f || u_mag2 > 1.1f ||
                f_mag2 < 0.9f || f_mag2 > 1.1f)
            {
                s_live_vp_fail_count.fetch_add(1, std::memory_order_relaxed);
                return false;
            }
            // Projection sanity: yScale should be in a reasonable
            // range for any plausible FOV (10°..170°).
            if (proj[5] < 0.5f || proj[5] > 12.0f ||
                proj[11] < 0.5f || proj[11] > 1.5f)
            {
                s_live_vp_fail_count.fetch_add(1, std::memory_order_relaxed);
                return false;
            }

            // view = inverse(cam_to_world). Orthonormal rotation +
            // translation: rotation transposes, translation is the
            // negated eye position projected onto each rotated axis.
            const float ex = cam_to_world[12];
            const float ey = cam_to_world[13];
            const float ez = cam_to_world[14];
            const float view[16] = {
                rx,  ux,  fx,  0.0f,
                ry,  uy,  fy,  0.0f,
                rz,  uz,  fz,  0.0f,
                -(ex*rx + ey*ry + ez*rz),
                -(ex*ux + ey*uy + ez*uz),
                -(ex*fx + ey*fy + ez*fz),
                1.0f
            };

            // VP = view * proj (row-major matrix multiply). Result
            // satisfies world_row · VP = clip_row; equivalently
            // mul(VP, world_col) = clip_col under HLSL's default
            // column-major matrix interpretation when the cbuffer
            // stores rows consecutively.
            for (int row = 0; row < 4; ++row)
            {
                for (int col = 0; col < 4; ++col)
                {
                    float s = 0.0f;
                    for (int k = 0; k < 4; ++k)
                    {
                        s += view[row*4 + k] * proj[k*4 + col];
                    }
                    vp_out[row*4 + col] = s;
                }
            }
            s_live_vp_read_count.fetch_add(1, std::memory_order_relaxed);
            return true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            s_live_vp_fail_count.fetch_add(1, std::memory_order_relaxed);
            return false;
        }
    }

    // Walk the locked host transform chain (gm+0x18 → +0x50 → +0x90)
    // to read the host player's world position. Returns false until
    // the AOB resolver in DS2_NativeRuntimeHook has published the
    // GameManagerImp address.
    bool TryReadHostPosition(float& px, float& py, float& pz)
    {
        const uintptr_t gm_imp_global =
            DS2_NativeRuntimeHook_GetGameManagerImpAddress();
        if (gm_imp_global == 0) return false;

        __try
        {
            uintptr_t gm_imp = *reinterpret_cast<const uintptr_t*>(gm_imp_global);
            if (gm_imp == 0) return false;
            uintptr_t intermediate = *reinterpret_cast<const uintptr_t*>(gm_imp + 0x18);
            if (intermediate == 0) return false;
            uintptr_t chrins = *reinterpret_cast<const uintptr_t*>(intermediate + 0x50);
            if (chrins == 0) return false;
            px = *reinterpret_cast<const float*>(chrins + 0x90);
            py = *reinterpret_cast<const float*>(chrins + 0x94);
            pz = *reinterpret_cast<const float*>(chrins + 0x98);
            return true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return false;
        }
    }

    // ── Phase B (phantom hijacking) skeleton ─────────────────────────
    //
    // When the brother summons via saponita (white sign soapstone),
    // DS2 allocates a phantom ChrIns inside our world. That ChrIns is
    // fully populated — real mesh, real armor, real animation state.
    // Vanilla DS2 keeps the phantom's transform in sync with the peer
    // over Steam P2P each frame. We OVERRIDE that transform from our
    // own UDP backbone (see Ds2NativePoseBridge) so the phantom moves
    // along OUR network-relayed path, decoupling from Steam P2P. End
    // result: real DS2 character mesh + animation, but its world
    // position is driven by the same peer_table the cube overlay uses.
    //
    // STATUS: skeleton with placeholder chain offsets. The real chain
    // needs B1/B2 (Cheat Engine probe with brother summoned via
    // saponita) to confirm. Until then this function is gated OFF and
    // a no-op so it can't accidentally corrupt vanilla ChrIns memory
    // during solo play.
    //
    // Current best guess from §11.4 research:
    //   gm + 0x20 → phantom_root
    //   phantom_root + 0x1A0 → phantom_chrins
    //   phantom_chrins + 0x90  → px, py, pz
    //
    // Once B1 confirms the chain we flip kPhantomHijackEnabled to
    // true and the per-frame writes activate.
    constexpr bool kPhantomHijackEnabled = false;
    constexpr uintptr_t kPhantomRootOffset      = 0x20;   // gm + ?
    constexpr uintptr_t kPhantomChrPtrOffset    = 0x1A0;  // phantom_root + ?
    constexpr uintptr_t kPhantomPositionOffset  = 0x90;   // phantom_chr + ?

    std::atomic<uint64_t> s_phantom_writes{0};
    std::atomic<uint64_t> s_phantom_skips{0};

    // Write a peer's world position into the corresponding phantom
    // ChrIns slot. Called every frame from DrawOverlay AFTER the cube
    // pass, gated on kPhantomHijackEnabled. Wrapped in SEH so a stale
    // pointer can never crash DS2.
    //
    // Returns true if a write actually happened, false if we bailed
    // (no game manager, null chain pointer, gate disabled, etc.). The
    // caller treats both outcomes as fine — phantom hijacking is
    // strictly additive on top of the cube overlay.
    bool TryWritePhantomPose(int peer_slot, float px, float py, float pz)
    {
        (void)peer_slot;  // B2: multiple phantom slots (host + 3) — for
                          //     now we only target the first allocated.

        if (!kPhantomHijackEnabled)
        {
            s_phantom_skips.fetch_add(1, std::memory_order_relaxed);
            return false;
        }

        const uintptr_t gm_imp_global =
            DS2_NativeRuntimeHook_GetGameManagerImpAddress();
        if (gm_imp_global == 0)
        {
            s_phantom_skips.fetch_add(1, std::memory_order_relaxed);
            return false;
        }

        __try
        {
            uintptr_t gm = *reinterpret_cast<const uintptr_t*>(gm_imp_global);
            if (gm == 0) { s_phantom_skips.fetch_add(1, std::memory_order_relaxed); return false; }

            uintptr_t phantom_root = *reinterpret_cast<const uintptr_t*>(
                gm + kPhantomRootOffset);
            if (phantom_root == 0) { s_phantom_skips.fetch_add(1, std::memory_order_relaxed); return false; }

            uintptr_t phantom_chr = *reinterpret_cast<const uintptr_t*>(
                phantom_root + kPhantomChrPtrOffset);
            if (phantom_chr == 0) { s_phantom_skips.fetch_add(1, std::memory_order_relaxed); return false; }

            // Sanity: vptr must be in the DS2 module range, otherwise
            // we're about to scribble on random memory. The Injector
            // module range starts in the 0x7FF7_F000_0000 area on
            // typical Win10 ASLR; if the vptr is wildly outside that,
            // bail.
            uintptr_t vptr = *reinterpret_cast<const uintptr_t*>(phantom_chr);
            if (vptr < 0x7FF7'0000'0000ull || vptr > 0x7FF8'0000'0000ull)
            {
                s_phantom_skips.fetch_add(1, std::memory_order_relaxed);
                return false;
            }

            *reinterpret_cast<float*>(phantom_chr + kPhantomPositionOffset + 0) = px;
            *reinterpret_cast<float*>(phantom_chr + kPhantomPositionOffset + 4) = py;
            *reinterpret_cast<float*>(phantom_chr + kPhantomPositionOffset + 8) = pz;
            s_phantom_writes.fetch_add(1, std::memory_order_relaxed);
            return true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            s_phantom_skips.fetch_add(1, std::memory_order_relaxed);
            return false;
        }
    }

    // v2.9.12 Phase 4c — cube suppression when an engine-rendered
    // phantom already covers a peer.
    //
    // Live RE session (2026-05-17) confirmed: when DS2 vanilla matchmaking
    // summons a peer (e.g. brother via saponita white sign), the peer is
    // allocated into slot 1..5 of the GameManagerImp slot pool and the
    // engine renders him as a real character mesh. Phase 4a always also
    // drew a cube for that same peer because the SHM peer table doesn't
    // know about the engine slot — resulting in a redundant cube on top
    // of the real body.
    //
    // Slot pool layout (live-verified):
    //   gm = *(gm_imp_global)
    //   slot_mgr = *(gm + 0x650)
    //   for slot N in 0..5:
    //     slot_record = slot_mgr + 0x5D0 + N * 0xA90
    //     PlayerCtrl* = *(slot_record + 0xC8)
    //     PlayerCtrl + 0x90..+0x9F = position vec4 (X, Y, Z, 1.0)
    //
    // We consider a slot "active" when its PlayerCtrl pointer is non-null
    // AND the PlayerCtrl's vptr is inside the DS2 module range (rules out
    // the empty pre-allocated 1 MB slot pools that have garbage vtable
    // values pointing back into slot mgr scratch memory).
    constexpr uintptr_t kSlotMgrPtrOffset    = 0x650;
    constexpr uintptr_t kSlotRecordBaseOff   = 0x5D0;
    constexpr uintptr_t kSlotRecordStride    = 0xA90;
    constexpr uintptr_t kSlotPlayerCtrlField = 0xC8;
    constexpr uintptr_t kPlayerCtrlPosOff    = 0x90;
    constexpr int       kMaxPhantomSlots     = 6;
    // 2.5 m squared = ~1.6 m radius. Empirically the SHM peer pose can
    // lag the engine phantom by up to ~1 m during high-speed movement,
    // so 1.6 m is the smallest radius that still catches the brother
    // standing next to you while sprinting alongside.
    constexpr float     kCoverRadiusSq       = 2.5f;

    std::atomic<uint64_t> s_cube_suppress_total{0};
    std::atomic<uint64_t> s_cube_suppress_checks{0};

    bool PeerIsCoveredByActivePhantomSlot(
        const float peer_pos[3])
    {
        s_cube_suppress_checks.fetch_add(1, std::memory_order_relaxed);

        const uintptr_t gm_imp_global =
            DS2_NativeRuntimeHook_GetGameManagerImpAddress();
        if (gm_imp_global == 0) return false;

        __try
        {
            uintptr_t gm = *reinterpret_cast<const uintptr_t*>(gm_imp_global);
            if (gm == 0) return false;

            uintptr_t slot_mgr = *reinterpret_cast<const uintptr_t*>(
                gm + kSlotMgrPtrOffset);
            if (slot_mgr == 0) return false;

            const uintptr_t slot_base = slot_mgr + kSlotRecordBaseOff;
            // Slot 0 is the LOCAL player — skip it; the local player is
            // never represented in the SHM peer table (peer table holds
            // OTHER Bonfire-coop participants only).
            for (int i = 1; i < kMaxPhantomSlots; ++i)
            {
                const uintptr_t rec = slot_base + i * kSlotRecordStride;
                uintptr_t pctrl = *reinterpret_cast<const uintptr_t*>(
                    rec + kSlotPlayerCtrlField);
                if (pctrl == 0) continue;

                uintptr_t vptr = *reinterpret_cast<const uintptr_t*>(pctrl);
                // Active PlayerCtrl vptr lives in the DS2 module .rdata
                // region (~0x7FF7_xxxx_xxxx on Win10 ASLR). Empty
                // pre-allocated slots have a vptr that points back into
                // slot mgr scratch memory (~0x7FF4_xxxx_xxxx heap range),
                // which is how we distinguish active from empty without
                // hardcoding the exact PlayerCtrl::vftable address.
                if (vptr < 0x7FF7'0000'0000ull ||
                    vptr > 0x7FF8'0000'0000ull)
                {
                    continue;
                }

                const float px =
                    *reinterpret_cast<const float*>(pctrl + kPlayerCtrlPosOff + 0);
                const float py =
                    *reinterpret_cast<const float*>(pctrl + kPlayerCtrlPosOff + 4);
                const float pz =
                    *reinterpret_cast<const float*>(pctrl + kPlayerCtrlPosOff + 8);

                const float dx = peer_pos[0] - px;
                const float dy = peer_pos[1] - py;
                const float dz = peer_pos[2] - pz;
                const float d2 = dx*dx + dy*dy + dz*dz;
                if (d2 <= kCoverRadiusSq)
                {
                    s_cube_suppress_total.fetch_add(
                        1, std::memory_order_relaxed);
                    return true;
                }
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            // Pointer chain hit a freed page during area transition or
            // similar. Don't suppress — fall through to drawing the cube
            // so the user still sees SOMETHING at the peer position.
            return false;
        }
        return false;
    }

    const uint8_t* Glyph5x7(char raw)
    {
        static const uint8_t blank[7] = { 0, 0, 0, 0, 0, 0, 0 };
        static const uint8_t digits[10][7] = {
            { 0x0E, 0x11, 0x13, 0x15, 0x19, 0x11, 0x0E },
            { 0x04, 0x0C, 0x04, 0x04, 0x04, 0x04, 0x0E },
            { 0x0E, 0x11, 0x01, 0x02, 0x04, 0x08, 0x1F },
            { 0x1F, 0x02, 0x04, 0x02, 0x01, 0x11, 0x0E },
            { 0x02, 0x06, 0x0A, 0x12, 0x1F, 0x02, 0x02 },
            { 0x1F, 0x10, 0x1E, 0x01, 0x01, 0x11, 0x0E },
            { 0x06, 0x08, 0x10, 0x1E, 0x11, 0x11, 0x0E },
            { 0x1F, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08 },
            { 0x0E, 0x11, 0x11, 0x0E, 0x11, 0x11, 0x0E },
            { 0x0E, 0x11, 0x11, 0x0F, 0x01, 0x02, 0x0C },
        };
        static const uint8_t letters[26][7] = {
            { 0x0E, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11 },
            { 0x1E, 0x11, 0x11, 0x1E, 0x11, 0x11, 0x1E },
            { 0x0E, 0x11, 0x10, 0x10, 0x10, 0x11, 0x0E },
            { 0x1E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x1E },
            { 0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x1F },
            { 0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x10 },
            { 0x0E, 0x11, 0x10, 0x17, 0x11, 0x11, 0x0F },
            { 0x11, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11 },
            { 0x0E, 0x04, 0x04, 0x04, 0x04, 0x04, 0x0E },
            { 0x07, 0x02, 0x02, 0x02, 0x12, 0x12, 0x0C },
            { 0x11, 0x12, 0x14, 0x18, 0x14, 0x12, 0x11 },
            { 0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x1F },
            { 0x11, 0x1B, 0x15, 0x15, 0x11, 0x11, 0x11 },
            { 0x11, 0x19, 0x15, 0x13, 0x11, 0x11, 0x11 },
            { 0x0E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E },
            { 0x1E, 0x11, 0x11, 0x1E, 0x10, 0x10, 0x10 },
            { 0x0E, 0x11, 0x11, 0x11, 0x15, 0x12, 0x0D },
            { 0x1E, 0x11, 0x11, 0x1E, 0x14, 0x12, 0x11 },
            { 0x0F, 0x10, 0x10, 0x0E, 0x01, 0x01, 0x1E },
            { 0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04 },
            { 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E },
            { 0x11, 0x11, 0x11, 0x11, 0x11, 0x0A, 0x04 },
            { 0x11, 0x11, 0x11, 0x15, 0x15, 0x1B, 0x11 },
            { 0x11, 0x11, 0x0A, 0x04, 0x0A, 0x11, 0x11 },
            { 0x11, 0x11, 0x0A, 0x04, 0x04, 0x04, 0x04 },
            { 0x1F, 0x01, 0x02, 0x04, 0x08, 0x10, 0x1F },
        };
        const char c = static_cast<char>(
            raw >= 'a' && raw <= 'z' ? raw - ('a' - 'A') : raw);
        if (c >= '0' && c <= '9') return digits[c - '0'];
        if (c >= 'A' && c <= 'Z') return letters[c - 'A'];
        static const uint8_t colon[7] = { 0, 0x04, 0x04, 0, 0x04, 0x04, 0 };
        static const uint8_t dash[7] = { 0, 0, 0, 0x1F, 0, 0, 0 };
        static const uint8_t dot[7] = { 0, 0, 0, 0, 0, 0x0C, 0x0C };
        static const uint8_t slash[7] = { 0x01, 0x02, 0x02, 0x04, 0x08, 0x08, 0x10 };
        if (c == ':') return colon;
        if (c == '-') return dash;
        if (c == '.') return dot;
        if (c == '/') return slash;
        return blank;
    }

    void DrawScreenRect(
        const D3D11_VIEWPORT& vp,
        float x, float y, float w, float h,
        float r, float g, float b, float a)
    {
        if (s_screen_vs == nullptr || s_screen_ps == nullptr ||
            s_screen_cbuffer == nullptr || w <= 0.0f || h <= 0.0f)
            return;

        D3D11_MAPPED_SUBRESOURCE mcb = {};
        HRESULT hrm = s_d3d_context->Map(
            s_screen_cbuffer, 0, D3D11_MAP_WRITE_DISCARD, 0, &mcb);
        if (FAILED(hrm)) return;

        ScreenCBData* data = reinterpret_cast<ScreenCBData*>(mcb.pData);
        data->rect[0] = x; data->rect[1] = y;
        data->rect[2] = w; data->rect[3] = h;
        data->color[0] = r; data->color[1] = g;
        data->color[2] = b; data->color[3] = a;
        data->screen[0] = vp.Width > 1.0f ? vp.Width : 1.0f;
        data->screen[1] = vp.Height > 1.0f ? vp.Height : 1.0f;
        data->pad[0] = 0.0f; data->pad[1] = 0.0f;
        s_d3d_context->Unmap(s_screen_cbuffer, 0);

        s_d3d_context->VSSetShader(s_screen_vs, nullptr, 0);
        s_d3d_context->PSSetShader(s_screen_ps, nullptr, 0);
        s_d3d_context->VSSetConstantBuffers(0, 1, &s_screen_cbuffer);
        s_d3d_context->Draw(6, 0);
    }

    void DrawBitmapText(
        const D3D11_VIEWPORT& vp,
        const char* text,
        float x, float y, float scale,
        float r, float g, float b)
    {
        if (text == nullptr) return;
        float pen_x = x;
        for (const char* p = text; *p != '\0'; ++p)
        {
            if (*p == ' ')
            {
                pen_x += 4.0f * scale;
                continue;
            }
            const uint8_t* glyph = Glyph5x7(*p);
            for (int row = 0; row < 7; ++row)
            {
                for (int col = 0; col < 5; ++col)
                {
                    if ((glyph[row] & (1 << (4 - col))) == 0)
                        continue;
                    DrawScreenRect(
                        vp,
                        pen_x + col * scale,
                        y + row * scale,
                        scale,
                        scale,
                        r, g, b, 1.0f);
                }
            }
            pen_x += 6.0f * scale;
        }
    }

    float BitmapTextWidth(const char* text, float scale)
    {
        if (text == nullptr) return 0.0f;
        float width = 0.0f;
        for (const char* p = text; *p != '\0'; ++p)
        {
            width += (*p == ' ') ? 4.0f * scale : 6.0f * scale;
        }
        return width;
    }

    void DrawBitmapTextCentered(
        const D3D11_VIEWPORT& vp,
        const char* text,
        float center_x,
        float y,
        float scale,
        float r,
        float g,
        float b)
    {
        const float x = center_x - BitmapTextWidth(text, scale) * 0.5f;
        DrawBitmapText(vp, text, x, y, scale, r, g, b);
    }

    void DrawInvitePrompt(const D3D11_VIEWPORT& vp)
    {
        if (!DS2_RenderHook_IsInvitePromptVisible())
            return;

        // Temporary fallback only. The intended Saponita invite UX is a real
        // DS2 frontend confirm window; keep this restrained until that path is
        // wired instead of exposing the old debug banner.
        char title[96] = {};
        char body[128] = {};
        char hint[96] = {};
        AcquireSRWLockShared(&s_invite_prompt_lock);
        strncpy_s(title, s_invite_prompt_title, _TRUNCATE);
        strncpy_s(body, s_invite_prompt_body, _TRUNCATE);
        strncpy_s(hint, s_invite_prompt_hint, _TRUNCATE);
        ReleaseSRWLockShared(&s_invite_prompt_lock);

        const float width =
            std::min(760.0f, std::max(560.0f, vp.Width - 240.0f));
        const float height = 178.0f;
        const float x = (vp.Width - width) * 0.5f;
        const float y = std::max(120.0f, vp.Height * 0.36f);
        const float center_x = x + width * 0.5f;
        const float gr = 0.45f, gg = 0.42f, gb = 0.39f;
        const float orange_r = 0.95f, orange_g = 0.48f, orange_b = 0.20f;
        const float blue_r = 0.18f, blue_g = 0.36f, blue_b = 0.46f;

        DrawScreenRect(vp, x, y, width, height, 0.025f, 0.023f, 0.021f, 0.92f);
        DrawScreenRect(vp, x + 8.0f, y + 8.0f, width - 16.0f, height - 16.0f,
            0.045f, 0.043f, 0.040f, 0.96f);
        DrawScreenRect(vp, x, y, width, 2.0f, gr, gg, gb, 1.0f);
        DrawScreenRect(vp, x, y + height - 2.0f, width, 2.0f, gr, gg, gb, 1.0f);
        DrawScreenRect(vp, x, y, 2.0f, height, gr, gg, gb, 1.0f);
        DrawScreenRect(vp, x + width - 2.0f, y, 2.0f, height, gr, gg, gb, 1.0f);
        DrawScreenRect(vp, x + 36.0f, y + 82.0f, width - 72.0f, 1.0f,
            0.35f, 0.29f, 0.22f, 0.8f);

        DrawBitmapTextCentered(
            vp, title, center_x, y + 34.0f, 3.0f, 0.88f, 0.86f, 0.78f);
        DrawBitmapTextCentered(
            vp, body, center_x, y + 70.0f, 2.0f, 0.84f, 0.82f, 0.76f);

        const float button_w = 168.0f;
        const float button_h = 38.0f;
        const float button_y = y + 116.0f;
        const float yes_x = center_x - button_w - 22.0f;
        const float no_x = center_x + 22.0f;

        DrawScreenRect(vp, yes_x, button_y, button_w, button_h,
            blue_r, blue_g, blue_b, 0.92f);
        DrawScreenRect(vp, no_x, button_y, button_w, button_h,
            orange_r, orange_g, orange_b, 0.92f);
        DrawScreenRect(vp, yes_x, button_y, button_w, 2.0f, gr, gg, gb, 1.0f);
        DrawScreenRect(vp, no_x, button_y, button_w, 2.0f, gr, gg, gb, 1.0f);
        DrawBitmapTextCentered(
            vp, "SI ENTER", yes_x + button_w * 0.5f, button_y + 11.0f,
            2.0f, 0.92f, 0.90f, 0.84f);
        DrawBitmapTextCentered(
            vp, "NO ESC", no_x + button_w * 0.5f, button_y + 11.0f,
            2.0f, 0.92f, 0.90f, 0.84f);
    }

    // Draw the screen-space overlay quad. Called from HookedPresent
    // BEFORE the chained Present. CRITICAL: save & restore every piece
    // of D3D11 immediate-context state we touch so the lighting engine
    // (which intercepts Present further down the chain to run DLSS /
    // FidelityFX post-passes) sees an unmodified state on entry.
    void DrawOverlay(IDXGISwapChain* swap)
    {
        if (s_overlay_init_state != 1) return;
        if (s_d3d_device == nullptr || s_d3d_context == nullptr) return;
        if (s_overlay_vs == nullptr || s_overlay_ps == nullptr) return;
        if (swap == nullptr) return;

        // ── Save existing pipeline state ──────────────────────────────
        // OM: render targets + depth stencil
        ID3D11RenderTargetView* old_rtv = nullptr;
        ID3D11DepthStencilView* old_dsv = nullptr;
        s_d3d_context->OMGetRenderTargets(1, &old_rtv, &old_dsv);

        // OM: blend / depth-stencil states
        ID3D11BlendState* old_blend = nullptr;
        FLOAT old_blend_factor[4] = { 0, 0, 0, 0 };
        UINT old_sample_mask = 0;
        s_d3d_context->OMGetBlendState(&old_blend, old_blend_factor, &old_sample_mask);

        ID3D11DepthStencilState* old_dss = nullptr;
        UINT old_stencil_ref = 0;
        s_d3d_context->OMGetDepthStencilState(&old_dss, &old_stencil_ref);

        // RS: rasterizer state + viewports + scissor
        ID3D11RasterizerState* old_rs = nullptr;
        s_d3d_context->RSGetState(&old_rs);

        UINT old_vp_count = D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE;
        D3D11_VIEWPORT old_vps[D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE] = {};
        s_d3d_context->RSGetViewports(&old_vp_count, old_vps);

        UINT old_sc_count = D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE;
        D3D11_RECT old_scissors[D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE] = {};
        s_d3d_context->RSGetScissorRects(&old_sc_count, old_scissors);

        // IA: topology, input layout, vertex/index buffer slot 0
        D3D11_PRIMITIVE_TOPOLOGY old_topo;
        s_d3d_context->IAGetPrimitiveTopology(&old_topo);

        ID3D11InputLayout* old_layout = nullptr;
        s_d3d_context->IAGetInputLayout(&old_layout);

        ID3D11Buffer* old_vb = nullptr;
        UINT old_vb_stride = 0, old_vb_offset = 0;
        s_d3d_context->IAGetVertexBuffers(0, 1, &old_vb, &old_vb_stride, &old_vb_offset);

        ID3D11Buffer* old_ib = nullptr;
        DXGI_FORMAT old_ib_fmt = DXGI_FORMAT_UNKNOWN;
        UINT old_ib_offset = 0;
        s_d3d_context->IAGetIndexBuffer(&old_ib, &old_ib_fmt, &old_ib_offset);

        // Shaders — class instances passed as nullptr (we don't use them)
        ID3D11VertexShader* old_vs = nullptr;
        s_d3d_context->VSGetShader(&old_vs, nullptr, nullptr);
        ID3D11PixelShader* old_ps = nullptr;
        s_d3d_context->PSGetShader(&old_ps, nullptr, nullptr);
        ID3D11GeometryShader* old_gs = nullptr;
        s_d3d_context->GSGetShader(&old_gs, nullptr, nullptr);
        ID3D11HullShader* old_hs = nullptr;
        s_d3d_context->HSGetShader(&old_hs, nullptr, nullptr);
        ID3D11DomainShader* old_dsv_shader = nullptr;
        s_d3d_context->DSGetShader(&old_dsv_shader, nullptr, nullptr);

        // Phase 3: also save VS constant buffer slot 0 — we replace it
        // with our own per-draw cbuffer and must restore DS2's so the
        // lighting engine's downstream draws still see expected data.
        ID3D11Buffer* old_vs_cb0 = nullptr;
        s_d3d_context->VSGetConstantBuffers(0, 1, &old_vs_cb0);

        // ── Get our back-buffer RTV ───────────────────────────────────
        ID3D11Texture2D* back_buffer = nullptr;
        HRESULT hr = swap->GetBuffer(0, __uuidof(ID3D11Texture2D),
                                     reinterpret_cast<void**>(&back_buffer));
        if (SUCCEEDED(hr) && back_buffer != nullptr)
        {
            ID3D11RenderTargetView* rtv = nullptr;
            hr = s_d3d_device->CreateRenderTargetView(back_buffer, nullptr, &rtv);
            back_buffer->Release();
            if (SUCCEEDED(hr) && rtv != nullptr)
            {
                DXGI_SWAP_CHAIN_DESC desc = {};
                if (SUCCEEDED(swap->GetDesc(&desc)))
                {
                    D3D11_VIEWPORT vp = {};
                    vp.TopLeftX = 0.0f;
                    vp.TopLeftY = 0.0f;
                    vp.Width    = static_cast<float>(desc.BufferDesc.Width);
                    vp.Height   = static_cast<float>(desc.BufferDesc.Height);
                    vp.MinDepth = 0.0f;
                    vp.MaxDepth = 1.0f;

                    if (DS2_RenderHook_IsInvitePromptVisible())
                    {
                        s_d3d_context->RSSetViewports(1, &vp);
                        s_d3d_context->OMSetRenderTargets(1, &rtv, nullptr);
                        s_d3d_context->OMSetBlendState(nullptr, nullptr, 0xFFFFFFFF);
                        s_d3d_context->OMSetDepthStencilState(nullptr, 0);
                        s_d3d_context->RSSetState(nullptr);
                        s_d3d_context->IASetPrimitiveTopology(
                            D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
                        s_d3d_context->IASetInputLayout(nullptr);
                        s_d3d_context->GSSetShader(nullptr, nullptr, 0);
                        s_d3d_context->HSSetShader(nullptr, nullptr, 0);
                        s_d3d_context->DSSetShader(nullptr, nullptr, 0);
                        DrawInvitePrompt(vp);
                    }

                    // Phase 3 v14: VP from DS2's camera config struct.
                    // Phase 4a v16: build a per-frame draw list of N
                    // cubes (host + ghost + IPC peers) and emit one
                    // Map/Draw cycle per cube. State (RTV / blend /
                    // shaders / cbuffer slot) is set ONCE before the
                    // loop and restored AFTER, so this scales cleanly
                    // to kMaxPeers without re-applying state each
                    // draw.
                    float live_vp[16] = {};
                    const bool has_live_vp = TryReadCameraVP(live_vp);
                    if (has_live_vp && s_overlay_cbuffer != nullptr)
                    {
                        // ── Build the per-frame draw list ─────────────
                        // Slot 0 is always the host (read live from
                        // chr+0x90). Slot 1 is a hard-coded "ghost"
                        // cube 5 m east of the host so we can SEE that
                        // the pipeline scales to N draws — this slot
                        // disappears once Phase 4b/c wires the IPC.
                        // Remaining slots come from s_peer_table[],
                        // which BonfireService will fill via
                        // DS2_RenderHook_SetPeerPoses() in Phase 4b.
                        struct CubeDraw
                        {
                            float pos[3];
                            float yaw_radians;
                            float color[3];
                        };
                        CubeDraw draws[kMaxPeers + 2];
                        int draw_count = 0;

                        // v2.9.17: cubo debug COMPLETAMENTE DESACTIVADO.
                        //
                        // Antes (v2.9.16 y previas): dibujabamos un cubo
                        // magenta para el host + un cubo coloreado por
                        // cada peer en s_peer_table. Esto fue util
                        // durante el desarrollo Phase 4a/4b como
                        // feedback visual de que el SHM peer table
                        // estaba sincronizando.
                        //
                        // Ahora (v2.9.17+): el objetivo es engine-spawn
                        // real (Saponita Desbloqueada). Si el spawn
                        // engine triunfa, el cuerpo real aparece via
                        // mesh + animaciones del engine. Si falla,
                        // queremos VER que falla (no esconderlo con
                        // un cubo simulado) — events.jsonl + heartbeat
                        // diagnostics nos lo dicen.
                        //
                        // El skip via draw_count = 0 deja toda la
                        // infraestructura (peer table, SetPeerPoses,
                        // cube-suppression check) intacta para
                        // diagnosticos future, pero NO emite ningun
                        // cubo en pantalla.
                        const bool kDrawCubeOverlayEnabled = false;
                        if (!kDrawCubeOverlayEnabled) {
                            // Skip todo el cube draw path entirely.
                            // No host cube, no peer cubes.
                            // draw_count stays at 0, downstream loop
                            // does nothing.
                        }
                        else
                        {
                            float host_px = 76.0f, host_py = 1.6f, host_pz = -184.0f;
                            const bool host_ok =
                                TryReadHostPosition(host_px, host_py, host_pz);
                            if (host_ok)
                            {
                                CubeDraw& d = draws[draw_count++];
                                d.pos[0] = host_px;
                                d.pos[1] = host_py;
                                d.pos[2] = host_pz;
                                d.yaw_radians = 0.0f;
                                d.color[0] = 1.0f; d.color[1] = 0.1f; d.color[2] = 0.9f;  // magenta
                            }
                        }

                        // v17 Phase 4b: the hard-coded cyan ghost
                        // from v16 has been removed. Peers must now
                        // arrive via DS2_RenderHook_SetPeerPoses()
                        // (= a 'render.set_peer_poses' command on the
                        // worker inbox). With no IPC peers we draw
                        // exactly ONE magenta cube on the host —
                        // additional cubes are unambiguous proof
                        // that the IPC bridge is alive.

                        // v2.9.17: peer cube loop tambien gateado.
                        // SetPeerPoses sigue funcionando (peer table
                        // se popula desde BonfireService), solo NO
                        // emitimos cubos por cada peer. Manteniendo
                        // la infra disponible para diagnosticos via
                        // GetPeerCount().
                        if (kDrawCubeOverlayEnabled)
                        {
                            // Copy any IPC-supplied peer poses under a
                            // shared lock — minimises contention since
                            // BonfireService updates the table rarely
                            // compared to draw frequency.
                            AcquireSRWLockShared(&s_peer_table_lock);
                            const int peer_n = s_peer_table_count.load(
                                std::memory_order_acquire);
                            for (int i = 0; i < peer_n && draw_count < kMaxPeers + 2; ++i)
                            {
                                const PeerPoseEntry& src = s_peer_table[i];
                                if (!src.valid) continue;
                                // v2.9.12 Phase 4c — cube suppression. If
                                // DS2's vanilla matchmaking has already
                                // summoned this peer into a phantom slot
                                // (e.g. brother via saponita), the engine is
                                // rendering his real character mesh at the
                                // same world position. Drawing a cube on top
                                // is redundant and visually noisy — skip it.
                                if (PeerIsCoveredByActivePhantomSlot(src.position))
                                    continue;
                                CubeDraw& d = draws[draw_count++];
                                d.pos[0] = src.position[0];
                                d.pos[1] = src.position[1];
                                d.pos[2] = src.position[2];
                                d.yaw_radians = src.yaw_radians;
                                d.color[0] = src.color[0];
                                d.color[1] = src.color[1];
                                d.color[2] = src.color[2];
                            }
                            ReleaseSRWLockShared(&s_peer_table_lock);
                        }

                        // ── Apply common overlay state ONCE ──────────
                        s_d3d_context->RSSetViewports(1, &vp);
                        s_d3d_context->OMSetRenderTargets(1, &rtv, nullptr);
                        s_d3d_context->OMSetBlendState(nullptr, nullptr, 0xFFFFFFFF);
                        s_d3d_context->OMSetDepthStencilState(nullptr, 0);
                        s_d3d_context->RSSetState(nullptr);
                        s_d3d_context->IASetPrimitiveTopology(
                            D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
                        s_d3d_context->IASetInputLayout(nullptr);
                        s_d3d_context->VSSetShader(s_overlay_vs, nullptr, 0);
                        s_d3d_context->PSSetShader(s_overlay_ps, nullptr, 0);
                        s_d3d_context->GSSetShader(nullptr, nullptr, 0);
                        s_d3d_context->HSSetShader(nullptr, nullptr, 0);
                        s_d3d_context->DSSetShader(nullptr, nullptr, 0);
                        s_d3d_context->VSSetConstantBuffers(
                            0, 1, &s_overlay_cbuffer);

                        // ── Per-cube map + draw ──────────────────────
                        for (int i = 0; i < draw_count; ++i)
                        {
                            const CubeDraw& cd = draws[i];
                            D3D11_MAPPED_SUBRESOURCE mcb = {};
                            HRESULT hrm = s_d3d_context->Map(
                                s_overlay_cbuffer, 0,
                                D3D11_MAP_WRITE_DISCARD, 0, &mcb);
                            if (FAILED(hrm)) continue;

                            OverlayCBData* data =
                                reinterpret_cast<OverlayCBData*>(mcb.pData);
                            memcpy(data->vp, live_vp, sizeof(data->vp));
                            data->anchor[0] = cd.pos[0];
                            data->anchor[1] = cd.pos[1];
                            data->anchor[2] = cd.pos[2];
                            data->scale = 1.0f;
                            data->color[0] = cd.color[0];
                            data->color[1] = cd.color[1];
                            data->color[2] = cd.color[2];
                            data->yaw_radians = cd.yaw_radians;
                            s_d3d_context->Unmap(s_overlay_cbuffer, 0);

                            // v19 (Phase 5a): 72 verts = torso (36)
                            // + head (36) = humanoid placeholder.
                            // v15..v18 used 36 verts (single cube).
                            s_d3d_context->Draw(72, 0);

                            // Phase B skeleton: write this peer's
                            // pose into the corresponding phantom
                            // ChrIns slot. No-op while
                            // kPhantomHijackEnabled is false (current
                            // default — flip on once B1/B2 confirm
                            // the chain offsets). Slot index 0 means
                            // "first allocated phantom" — for the
                            // single-brother test case this is the
                            // saponita-summoned guest.
                            if (i > 0)  // i==0 is host, skip
                            {
                                TryWritePhantomPose(i - 1,
                                                    cd.pos[0],
                                                    cd.pos[1],
                                                    cd.pos[2]);
                            }
                        }

                        s_multi_draw_frames.fetch_add(1,
                            std::memory_order_relaxed);
                        s_multi_draw_cubes_total.fetch_add(draw_count,
                            std::memory_order_relaxed);

                        // Diagnostic log every 300 frames (~5s @ 60fps).
                        static std::atomic<uint64_t> s_diag_seq{0};
                        const uint64_t seq = s_diag_seq.fetch_add(1) + 1;
                        if (seq % 300 == 1)
                        {
                            const uint64_t reads =
                                s_live_vp_read_count.load(std::memory_order_relaxed);
                            const uint64_t fails =
                                s_live_vp_fail_count.load(std::memory_order_relaxed);
                            // v2.9.17: host_px/py/pz are scoped inside
                            // the (now disabled) cube draw block, so
                            // read host position fresh here just for
                            // diagnostic purposes. Doesn't affect render.
                            float diag_hx = 0.0f, diag_hy = 0.0f, diag_hz = 0.0f;
                            TryReadHostPosition(diag_hx, diag_hy, diag_hz);
                            Log("DS2_RenderHook: drew %d cubes (overlay %s) "
                                "host=(%.2f,%.2f,%.2f) liveVP_row3=%.2f,%.2f,%.2f "
                                "live_reads=%llu live_fails=%llu",
                                draw_count,
                                kDrawCubeOverlayEnabled ? "enabled" : "disabled",
                                diag_hx, diag_hy, diag_hz,
                                live_vp[12], live_vp[13], live_vp[14],
                                static_cast<unsigned long long>(reads),
                                static_cast<unsigned long long>(fails));
                        }
                    }
                }
                rtv->Release();
            }
        }

        // ── Restore the entire pipeline state we saved above ─────────
        s_d3d_context->OMSetRenderTargets(1, &old_rtv, old_dsv);
        if (old_rtv) old_rtv->Release();
        if (old_dsv) old_dsv->Release();

        s_d3d_context->OMSetBlendState(old_blend, old_blend_factor, old_sample_mask);
        if (old_blend) old_blend->Release();

        s_d3d_context->OMSetDepthStencilState(old_dss, old_stencil_ref);
        if (old_dss) old_dss->Release();

        s_d3d_context->RSSetState(old_rs);
        if (old_rs) old_rs->Release();

        s_d3d_context->RSSetViewports(old_vp_count, old_vps);
        s_d3d_context->RSSetScissorRects(old_sc_count, old_scissors);

        s_d3d_context->IASetPrimitiveTopology(old_topo);
        s_d3d_context->IASetInputLayout(old_layout);
        if (old_layout) old_layout->Release();

        s_d3d_context->IASetVertexBuffers(0, 1, &old_vb, &old_vb_stride, &old_vb_offset);
        if (old_vb) old_vb->Release();
        s_d3d_context->IASetIndexBuffer(old_ib, old_ib_fmt, old_ib_offset);
        if (old_ib) old_ib->Release();

        s_d3d_context->VSSetShader(old_vs, nullptr, 0);
        if (old_vs) old_vs->Release();
        s_d3d_context->PSSetShader(old_ps, nullptr, 0);
        if (old_ps) old_ps->Release();
        s_d3d_context->GSSetShader(old_gs, nullptr, 0);
        if (old_gs) old_gs->Release();
        s_d3d_context->HSSetShader(old_hs, nullptr, 0);
        if (old_hs) old_hs->Release();
        s_d3d_context->DSSetShader(old_dsv_shader, nullptr, 0);
        if (old_dsv_shader) old_dsv_shader->Release();

        // Phase 3: restore the VS slot-0 cbuffer DS2 had bound, so
        // the lighting engine's downstream Present passes see the
        // expected matrix data. Even if we skipped the draw above
        // we still restore — VSGetConstantBuffers AddRef'd the ref.
        s_d3d_context->VSSetConstantBuffers(0, 1, &old_vs_cb0);
        if (old_vs_cb0) old_vs_cb0->Release();
    }

    HRESULT STDMETHODCALLTYPE HookedPresent(
        IDXGISwapChain* swap, UINT SyncInterval, UINT Flags)
    {
        s_frame_count.fetch_add(1, std::memory_order_relaxed);
        // v11f: bump frame marker AFTER all the frame's draws but
        // BEFORE the swap chain flips so the next frame's first CB
        // update is the one we capture.
        s_frame_marker.fetch_add(1, std::memory_order_release);

        IDXGISwapChain* expected = nullptr;
        if (s_observed_swap.compare_exchange_strong(
                expected, swap, std::memory_order_acq_rel))
        {
            Log("DS2_RenderHook: first Present captured swap=%p sync=%u flags=0x%X",
                swap, SyncInterval, Flags);
        }

        // Phase 2: lazy-init shaders, then draw the screen-space overlay
        // quad onto the swap chain back buffer BEFORE chaining to the
        // original Present (which the lighting engine intercepts via
        // its wrapper IDXGISwapChain).
        if (s_overlay_init_state == 0)
        {
            LazyInitOverlay();
        }
        DrawOverlay(swap);

        return s_original_present(swap, SyncInterval, Flags);
    }

    bool HookPresentVtableOf(IDXGISwapChain* swap)
    {
        if (swap == nullptr) return false;
        if (InterlockedCompareExchange(&s_present_hooked, 1, 0) != 0)
        {
            return true;
        }

        // IDXGISwapChain vtable[8] = Present
        void** vtable = *reinterpret_cast<void***>(swap);
        PresentFn target = reinterpret_cast<PresentFn>(vtable[8]);
        Log("DS2_RenderHook: swap=%p vtable=%p Present=%p", swap, vtable, target);

        s_original_present = target;
        LONG err = DetourTransactionBegin();
        if (err == NO_ERROR) err = DetourUpdateThread(GetCurrentThread());
        if (err == NO_ERROR)
        {
            err = DetourAttach(
                reinterpret_cast<PVOID*>(&s_original_present),
                reinterpret_cast<PVOID>(HookedPresent));
        }
        if (err == NO_ERROR) err = DetourTransactionCommit();

        if (err != NO_ERROR)
        {
            Log("DS2_RenderHook: DetourAttach(Present) failed err=%ld", err);
            s_original_present = nullptr;
            InterlockedExchange(&s_present_hooked, 0);
            return false;
        }

        InterlockedExchange(&s_installed, 1);
        Log("DS2_RenderHook: Present detour installed — Phase 1 frame counter live");
        return true;
    }

    HRESULT STDMETHODCALLTYPE HookedFactoryCreateSwapChain(
        IDXGIFactory* factory,
        IUnknown* pDevice,
        DXGI_SWAP_CHAIN_DESC* pDesc,
        IDXGISwapChain** ppSwapChain)
    {
        Log("DS2_RenderHook: HookedFactoryCreateSwapChain factory=%p device=%p",
            factory, pDevice);

        HRESULT hr = s_original_factory_create_sc(
            factory, pDevice, pDesc, ppSwapChain);

        if (SUCCEEDED(hr) && ppSwapChain != nullptr && *ppSwapChain != nullptr)
        {
            Log("DS2_RenderHook: factory returned swap=%p", *ppSwapChain);
            HookPresentVtableOf(*ppSwapChain);
        }

        return hr;
    }

    bool HookFactoryCreateSwapChain(IDXGIFactory* factory)
    {
        if (factory == nullptr) return false;
        if (InterlockedCompareExchange(&s_factory_hooked, 1, 0) != 0)
        {
            return true;
        }

        // IDXGIFactory vtable:
        //   10 = CreateSwapChain
        void** vtable = *reinterpret_cast<void***>(factory);
        auto target = reinterpret_cast<FactoryCreateSwapChainFn>(vtable[10]);

        Log("DS2_RenderHook: factory=%p vtable=%p CreateSwapChain=%p",
            factory, vtable, target);

        s_original_factory_create_sc = target;
        LONG err = DetourTransactionBegin();
        if (err == NO_ERROR) err = DetourUpdateThread(GetCurrentThread());
        if (err == NO_ERROR)
        {
            err = DetourAttach(
                reinterpret_cast<PVOID*>(&s_original_factory_create_sc),
                reinterpret_cast<PVOID>(HookedFactoryCreateSwapChain));
        }
        if (err == NO_ERROR) err = DetourTransactionCommit();

        if (err != NO_ERROR)
        {
            Log("DS2_RenderHook: DetourAttach(IDXGIFactory::CreateSwapChain) failed err=%ld",
                err);
            s_original_factory_create_sc = nullptr;
            InterlockedExchange(&s_factory_hooked, 0);
            return false;
        }

        Log("DS2_RenderHook: IDXGIFactory::CreateSwapChain detour installed");
        return true;
    }

    HRESULT WINAPI HookedCreateDevice(
        IDXGIAdapter* adapter,
        D3D_DRIVER_TYPE driver_type,
        HMODULE software,
        UINT flags,
        const D3D_FEATURE_LEVEL* feature_levels,
        UINT num_feature_levels,
        UINT sdk_version,
        ID3D11Device** ppDevice,
        D3D_FEATURE_LEVEL* pFeatureLevel,
        ID3D11DeviceContext** ppImmediateContext)
    {
        Log("DS2_RenderHook: HookedCreateDevice called");

        HRESULT hr = s_original_create_device(
            adapter, driver_type, software, flags,
            feature_levels, num_feature_levels, sdk_version,
            ppDevice, pFeatureLevel, ppImmediateContext);

        if (FAILED(hr) || ppDevice == nullptr || *ppDevice == nullptr)
        {
            return hr;
        }

        // Phase-2: cache the device + immediate context so HookedPresent
        // can spawn its own shaders/draws. AddRef so the refs survive
        // even if DS2 stops using them.
        if (s_d3d_device == nullptr)
        {
            s_d3d_device = *ppDevice;
            s_d3d_device->AddRef();
            if (ppImmediateContext != nullptr && *ppImmediateContext != nullptr)
            {
                s_d3d_context = *ppImmediateContext;
                s_d3d_context->AddRef();
            }
            else
            {
                (*ppDevice)->GetImmediateContext(&s_d3d_context);
            }
            Log("DS2_RenderHook: cached device=%p context=%p for Phase-2/3 overlay",
                s_d3d_device, s_d3d_context);
            // Phase 3: VMT-hook the context's Map/Unmap to capture
            // DS2's per-frame constant-buffer writes (cheap, no GPU
            // sync). Done once on the first device creation.
            HookContextMapUnmap(s_d3d_context);
        }

        // Walk device → IDXGIDevice → IDXGIAdapter → IDXGIFactory.
        // We need the factory because that's the COM object DS2 will
        // call CreateSwapChain on next.
        ID3D11Device* device = *ppDevice;
        IDXGIDevice* dxgi_device = nullptr;
        if (SUCCEEDED(device->QueryInterface(__uuidof(IDXGIDevice),
                                              reinterpret_cast<void**>(&dxgi_device))))
        {
            IDXGIAdapter* adapter_q = nullptr;
            if (SUCCEEDED(dxgi_device->GetAdapter(&adapter_q)) && adapter_q)
            {
                IDXGIFactory* factory = nullptr;
                if (SUCCEEDED(adapter_q->GetParent(
                        __uuidof(IDXGIFactory),
                        reinterpret_cast<void**>(&factory))) && factory)
                {
                    Log("DS2_RenderHook: walked device chain → factory=%p", factory);
                    HookFactoryCreateSwapChain(factory);
                    factory->Release();
                }
                adapter_q->Release();
            }
            dxgi_device->Release();
        }

        return hr;
    }

    bool TryInstallExportHook()
    {
        if (InterlockedCompareExchange(&s_create_device_hooked, 1, 0) != 0)
        {
            return true;
        }

        HMODULE d3d11 = GetModuleHandleW(L"d3d11.dll");
        if (d3d11 == nullptr)
        {
            d3d11 = LoadLibraryW(L"d3d11.dll");
            if (d3d11 == nullptr)
            {
                InterlockedExchange(&s_create_device_hooked, 0);
                return false;
            }
        }

        auto create_fn = reinterpret_cast<CreateDeviceFn>(
            GetProcAddress(d3d11, "D3D11CreateDevice"));
        if (create_fn == nullptr)
        {
            Log("DS2_RenderHook: GetProcAddress(D3D11CreateDevice) failed");
            InterlockedExchange(&s_create_device_hooked, 0);
            return false;
        }

        s_original_create_device = create_fn;
        LONG err = DetourTransactionBegin();
        if (err == NO_ERROR) err = DetourUpdateThread(GetCurrentThread());
        if (err == NO_ERROR)
        {
            err = DetourAttach(
                reinterpret_cast<PVOID*>(&s_original_create_device),
                reinterpret_cast<PVOID>(HookedCreateDevice));
        }
        if (err == NO_ERROR) err = DetourTransactionCommit();

        if (err != NO_ERROR)
        {
            Log("DS2_RenderHook: DetourAttach(D3D11CreateDevice) failed err=%ld", err);
            s_original_create_device = nullptr;
            InterlockedExchange(&s_create_device_hooked, 0);
            return false;
        }

        Log("DS2_RenderHook: D3D11CreateDevice export hook installed "
            "(target=%p) — waiting for DS2's renderer init", create_fn);
        return true;
    }

    DWORD WINAPI InstallThread(LPVOID /*param*/)
    {
        for (int attempt = 0; attempt < 60; ++attempt)
        {
            if (TryInstallExportHook()) return 0;
            Sleep(500);
        }
        Log("DS2_RenderHook: gave up — couldn't install export hook after 30s");
        return 1;
    }
}

bool DS2_RenderHook::Install(Injector& /*injector*/)
{
    if (InterlockedCompareExchange(&s_install_attempted, 1, 0) != 0)
    {
        return true;
    }

    s_install_thread = CreateThread(nullptr, 0, InstallThread, nullptr, 0, nullptr);
    if (s_install_thread == nullptr)
    {
        Log("DS2_RenderHook: CreateThread failed (err=%lu)", GetLastError());
        return false;
    }

    Log("DS2_RenderHook: install thread spawned (v8c — D3D11CreateDevice path)");
    return true;
}

void DS2_RenderHook::Uninstall()
{
    if (s_original_present != nullptr &&
        InterlockedCompareExchange(&s_present_hooked, 0, 1) == 1)
    {
        DetourTransactionBegin();
        DetourUpdateThread(GetCurrentThread());
        DetourDetach(reinterpret_cast<PVOID*>(&s_original_present),
                     reinterpret_cast<PVOID>(HookedPresent));
        DetourTransactionCommit();
        s_original_present = nullptr;
    }
    if (s_original_factory_create_sc != nullptr &&
        InterlockedCompareExchange(&s_factory_hooked, 0, 1) == 1)
    {
        DetourTransactionBegin();
        DetourUpdateThread(GetCurrentThread());
        DetourDetach(reinterpret_cast<PVOID*>(&s_original_factory_create_sc),
                     reinterpret_cast<PVOID>(HookedFactoryCreateSwapChain));
        DetourTransactionCommit();
        s_original_factory_create_sc = nullptr;
    }
    if (s_original_create_device != nullptr &&
        InterlockedCompareExchange(&s_create_device_hooked, 0, 1) == 1)
    {
        DetourTransactionBegin();
        DetourUpdateThread(GetCurrentThread());
        DetourDetach(reinterpret_cast<PVOID*>(&s_original_create_device),
                     reinterpret_cast<PVOID>(HookedCreateDevice));
        DetourTransactionCommit();
        s_original_create_device = nullptr;
    }
    if (s_original_map != nullptr && s_original_unmap != nullptr &&
        InterlockedCompareExchange(&s_map_unmap_hooked, 0, 1) == 1)
    {
        DetourTransactionBegin();
        DetourUpdateThread(GetCurrentThread());
        DetourDetach(reinterpret_cast<PVOID*>(&s_original_map),
                     reinterpret_cast<PVOID>(HookedMap));
        DetourDetach(reinterpret_cast<PVOID*>(&s_original_unmap),
                     reinterpret_cast<PVOID>(HookedUnmap));
        if (s_original_update_sub != nullptr)
        {
            DetourDetach(reinterpret_cast<PVOID*>(&s_original_update_sub),
                         reinterpret_cast<PVOID>(HookedUpdateSubresource));
        }
        DetourTransactionCommit();
        s_original_map = nullptr;
        s_original_unmap = nullptr;
        s_original_update_sub = nullptr;
    }
    InterlockedExchange(&s_installed, 0);
    Log("DS2_RenderHook: detours removed");
}

const char* DS2_RenderHook::GetName()
{
    return "DS2 Render Hook (HKMP overlay v8c — D3D11CreateDevice→factory→present)";
}

void DS2_RenderHook_SetInvitePrompt(
    const char* title,
    const char* body,
    const char* hint)
{
    AcquireSRWLockExclusive(&s_invite_prompt_lock);
    strncpy_s(s_invite_prompt_title, title ? title : "", _TRUNCATE);
    strncpy_s(s_invite_prompt_body, body ? body : "", _TRUNCATE);
    strncpy_s(s_invite_prompt_hint, hint ? hint : "", _TRUNCATE);
    s_invite_prompt_visible = true;
    ReleaseSRWLockExclusive(&s_invite_prompt_lock);
}

void DS2_RenderHook_ClearInvitePrompt()
{
    AcquireSRWLockExclusive(&s_invite_prompt_lock);
    s_invite_prompt_visible = false;
    s_invite_prompt_title[0] = '\0';
    s_invite_prompt_body[0] = '\0';
    s_invite_prompt_hint[0] = '\0';
    ReleaseSRWLockExclusive(&s_invite_prompt_lock);
}

bool DS2_RenderHook_IsInvitePromptVisible()
{
    bool visible = false;
    AcquireSRWLockShared(&s_invite_prompt_lock);
    visible = s_invite_prompt_visible;
    ReleaseSRWLockShared(&s_invite_prompt_lock);
    return visible;
}

uint64_t DS2_RenderHook_GetFrameCount()
{
    return s_frame_count.load(std::memory_order_relaxed);
}

bool DS2_RenderHook_IsInstalled()
{
    return InterlockedCompareExchange(&s_installed, 0, 0) != 0;
}

// Phase-1 diagnostic accessors — each hook stage exposes a flag so the
// runtime heartbeat can pinpoint exactly where the chain breaks if any
// stage misses.
bool DS2_RenderHook_IsCreateDeviceHooked()
{
    return InterlockedCompareExchange(&s_create_device_hooked, 0, 0) != 0;
}

bool DS2_RenderHook_IsFactoryHooked()
{
    return InterlockedCompareExchange(&s_factory_hooked, 0, 0) != 0;
}

bool DS2_RenderHook_IsPresentHooked()
{
    return InterlockedCompareExchange(&s_present_hooked, 0, 0) != 0;
}

uint64_t DS2_RenderHook_GetVPCaptureCount()
{
    return s_vp_capture_count.load(std::memory_order_relaxed);
}

bool DS2_RenderHook_GetCapturedVP(float out_vp[16])
{
    if (s_vp_capture_count.load(std::memory_order_relaxed) == 0) return false;
    memcpy(out_vp, s_captured_vp, sizeof(float) * 16);
    return true;
}

uint64_t DS2_RenderHook_GetTotalMapCalls()
{
    return s_total_map_calls.load(std::memory_order_relaxed);
}

uint64_t DS2_RenderHook_GetTotalUnmapCalls()
{
    return s_total_unmap_calls.load(std::memory_order_relaxed);
}

uint64_t DS2_RenderHook_GetCBufferMapCount()
{
    return s_map_cb_dynamic.load(std::memory_order_relaxed);
}

void DS2_RenderHook_GetFirstCBSizes(uint32_t out_sizes[4])
{
    for (int i = 0; i < 4; ++i)
    {
        out_sizes[i] = s_first_cb_byte_widths[i];
    }
}

uint64_t DS2_RenderHook_GetTotalUpdateSubCalls()
{
    return s_total_update_sub_calls.load(std::memory_order_relaxed);
}

uint64_t DS2_RenderHook_GetUpdateCBCallCount()
{
    return s_update_cb_calls.load(std::memory_order_relaxed);
}

void DS2_RenderHook_GetFirstUpdateCBSizes(uint32_t out_sizes[4])
{
    for (int i = 0; i < 4; ++i)
    {
        out_sizes[i] = s_first_update_cb_sizes[i];
    }
}

uint32_t DS2_RenderHook_GetCapturedCBSize()
{
    return s_captured_cb_size.load(std::memory_order_relaxed);
}

uint32_t DS2_RenderHook_GetCapturedBufferDump(float out[64])
{
    const uint32_t valid_bytes = s_captured_buffer_size;
    if (valid_bytes == 0) return 0;
    const uint32_t valid_floats = valid_bytes / sizeof(float);
    memcpy(out, s_captured_buffer_full,
           valid_floats * sizeof(float));
    return valid_floats;
}

uint32_t DS2_RenderHook_GetCapturedBuffer576Dump(float out[144])
{
    const uint32_t valid_bytes = s_captured_buffer_576_size;
    if (valid_bytes == 0) return 0;
    const uint32_t valid_floats = valid_bytes / sizeof(float);
    memcpy(out, s_captured_buffer_576,
           valid_floats * sizeof(float));
    return valid_floats;
}

uint64_t DS2_RenderHook_GetLiveVPReadCount()
{
    return s_live_vp_read_count.load(std::memory_order_relaxed);
}

uint64_t DS2_RenderHook_GetLiveVPFailCount()
{
    return s_live_vp_fail_count.load(std::memory_order_relaxed);
}

bool DS2_RenderHook_TryGetLiveVP(float out_vp[16])
{
    return TryReadCameraVP(out_vp);
}

// ── Phase 4a (v16): multi-actor public API ──────────────────────────

void DS2_RenderHook_SetPeerPoses(
    const DS2_PeerPose* poses, int count)
{
    if (count < 0) count = 0;
    if (count > kMaxPeers) count = kMaxPeers;

    AcquireSRWLockExclusive(&s_peer_table_lock);
    for (int i = 0; i < count; ++i)
    {
        s_peer_table[i].position[0]  = poses[i].position[0];
        s_peer_table[i].position[1]  = poses[i].position[1];
        s_peer_table[i].position[2]  = poses[i].position[2];
        s_peer_table[i].yaw_radians  = poses[i].yaw_radians;
        s_peer_table[i].color[0]     = poses[i].color[0];
        s_peer_table[i].color[1]     = poses[i].color[1];
        s_peer_table[i].color[2]     = poses[i].color[2];
        s_peer_table[i].valid        = poses[i].valid ? 1u : 0u;
    }
    // Clear any stale entries past the new count so the renderer
    // doesn't keep drawing departed peers.
    for (int i = count; i < kMaxPeers; ++i)
    {
        s_peer_table[i].valid = 0u;
    }
    s_peer_table_count.store(count, std::memory_order_release);
    ReleaseSRWLockExclusive(&s_peer_table_lock);
}

int DS2_RenderHook_GetPeerCount()
{
    return s_peer_table_count.load(std::memory_order_acquire);
}

uint64_t DS2_RenderHook_GetMultiDrawFrames()
{
    return s_multi_draw_frames.load(std::memory_order_relaxed);
}

uint64_t DS2_RenderHook_GetMultiDrawCubesTotal()
{
    return s_multi_draw_cubes_total.load(std::memory_order_relaxed);
}

uint64_t DS2_RenderHook_GetCubeSuppressTotal()
{
    return s_cube_suppress_total.load(std::memory_order_relaxed);
}

uint64_t DS2_RenderHook_GetCubeSuppressChecks()
{
    return s_cube_suppress_checks.load(std::memory_order_relaxed);
}
