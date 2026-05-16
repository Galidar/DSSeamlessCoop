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
