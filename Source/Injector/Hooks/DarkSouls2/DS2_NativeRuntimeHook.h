/*
 * Dark Souls 3 - Open Server
 * Copyright (C) 2021 Tim Leonard
 *
 * This program is free software; licensed under the MIT license.
 * You should have received a copy of the license along with this program.
 * If not, see <https://opensource.org/licenses/MIT>.
 */

#pragma once

#include "Injector/Hooks/Hook.h"

#include <cstdint>

// Baseline entry point for the Bonfire-native DS2 seamless runtime rebuild.
class DS2_NativeRuntimeHook : public Hook
{
public:
    virtual bool Install(Injector& injector) override;
    virtual void Uninstall() override;
    virtual const char* GetName() override;
};

// Returns the resolved address of DS2's GameManagerImp global pointer
// (the static slot patched at AOB scan time). Zero until the worker
// thread has run its first install pass. Other hooks (DS2_RenderHook)
// use this to walk the locked host transform chain.
uintptr_t DS2_NativeRuntimeHook_GetGameManagerImpAddress();
