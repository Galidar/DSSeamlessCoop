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
#include <dxgi.h>

#include <atomic>
#include <cstdint>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "dxguid.lib")

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

        // Phase 2+: draw call splicing goes here.

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
