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
#include "Injector/Injector/Injector.h"
#include "Shared/Core/Utils/Logging.h"
#include "ThirdParty/detours/src/detours.h"

#include <Windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <dxgi.h>

#include <atomic>
#include <cstdint>

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

    // ── Phase 2 overlay draw state ───────────────────────────────────
    // Cached from HookedCreateDevice and used inside HookedPresent.
    // Refs are held until process exit; we never release.
    ID3D11Device*           s_d3d_device = nullptr;
    ID3D11DeviceContext*    s_d3d_context = nullptr;
    ID3D11VertexShader*     s_overlay_vs = nullptr;
    ID3D11PixelShader*      s_overlay_ps = nullptr;
    LONG                    s_overlay_init_state = 0;  // 0=pending, 1=ok, 2=failed

    // ── Phase 2 shader sources ───────────────────────────────────────
    // Vertex shader: no input layout, uses SV_VertexID to fetch one of
    // six precomputed NDC corners of a small top-left overlay quad.
    // Pixel shader: outputs a solid color (chosen so it's clearly NOT
    // a DS2 game element — bright green tinted toward cyan).
    static constexpr const char* kOverlayVSSource = R"(
struct VSOut { float4 pos : SV_Position; };
VSOut main(uint id : SV_VertexID)
{
    // Six vertices forming two triangles in the top-left corner of
    // the framebuffer. NDC: x∈[-1,+1] right-positive, y∈[-1,+1]
    // up-positive. Quad placed at x∈[-0.95,-0.55], y∈[0.65, 0.95].
    float2 corners[6] = {
        float2(-0.95,  0.95),  // top-left
        float2(-0.55,  0.95),  // top-right
        float2(-0.95,  0.65),  // bottom-left
        float2(-0.55,  0.95),  // top-right (repeat)
        float2(-0.55,  0.65),  // bottom-right
        float2(-0.95,  0.65)   // bottom-left (repeat)
    };
    VSOut o;
    o.pos = float4(corners[id], 0.5, 1.0);
    return o;
}
)";

    static constexpr const char* kOverlayPSSource = R"(
float4 main() : SV_Target
{
    // Bright magenta — won't blend in with anything DS2 normally draws.
    return float4(1.0, 0.1, 0.9, 0.85);
}
)";

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

        InterlockedExchange(&s_overlay_init_state, 1);
        Log("DS2_RenderHook: Phase-2 overlay shaders compiled OK "
            "(vs=%p ps=%p)", s_overlay_vs, s_overlay_ps);
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

                    // ── Apply our minimal overlay state ──────────────
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

                    s_d3d_context->Draw(6, 0);
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
    }

    HRESULT STDMETHODCALLTYPE HookedPresent(
        IDXGISwapChain* swap, UINT SyncInterval, UINT Flags)
    {
        s_frame_count.fetch_add(1, std::memory_order_relaxed);

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
            Log("DS2_RenderHook: cached device=%p context=%p for Phase-2 overlay",
                s_d3d_device, s_d3d_context);
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
    InterlockedExchange(&s_installed, 0);
    Log("DS2_RenderHook: detours removed");
}

const char* DS2_RenderHook::GetName()
{
    return "DS2 Render Hook (HKMP overlay v8c — D3D11CreateDevice→factory→present)";
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
