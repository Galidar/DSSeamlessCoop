/*
 * Dark Souls 3 - Open Server
 * Copyright (C) 2021 Tim Leonard
 *
 * This program is free software; licensed under the MIT license.
 * You should have received a copy of the license along with this program.
 * If not, see <https://opensource.org/licenses/MIT>.
 */

#pragma once

#include <string>
#include <filesystem>
#include "ThirdParty/nlohmann/json.hpp"

// Configuration saved and loaded at runtime by the server from a configuration file.
class RuntimeConfig
{
public:

    // Name of server being joined.
    std::string ServerName = "Dark Souls 3 Server";

    // Hostname of server being joined.
    std::string ServerHostname = "";

    // Public key of server being joined.
    std::string ServerPublicKey = "";

    // Type of game we are being injected into.
    std::string ServerGameType = "";

    // Login port to connect to on server.
    int ServerPort = 50050;

    // If we should use seperate saves from the retail ones.
    bool EnableSeperateSaveFiles = true;

    // If DS2 should load loose mod files through the injector instead of
    // relying on ModEngine's dinput8 loader.
    bool EnableModFileOverrides = false;

    // Absolute path, or path relative to the game directory, containing the
    // DS2 override tree: Param/, map/, menu/, ezstate/, enc_regulation.bnd.dcx.
    std::string ModOverrideDirectory = "";

    // Cache positive and negative override lookups. Disable when editing loose
    // files while the game is running.
    bool CacheModFilePaths = true;

    // Extension to use when redirecting saves. DS2 Multiplayer Overhaul's
    // ModEngine config uses .sl3; DSSeamlessCoop defaults to .ds3os.
    std::string SaveFileExtension = ".ds3os";

    // DS2 shadow-map resolution patches from ModEngine's rendering section.
    bool EnableDs2ShadowResolutionPatches = false;
    int Ds2DirectionalShadowResolution = 2048;
    int Ds2DynamicAtlasShadowResolution = 1024;
    int Ds2DynamicPointShadowResolution = 256;
    int Ds2DynamicSpotShadowResolution = 512;

    // Bonfire-native DS2 runtime command/event bridge. This is the first
    // control surface for the new DS2 seamless-style runtime.
    bool EnableDs2NativeRuntime = false;
    std::string Ds2NativeRuntimeSessionId = "";
    std::string Ds2NativeRuntimeEventLog = "";
    std::string Ds2NativeRuntimeCommandInbox = "";

public:

    bool Save(const std::filesystem::path& Path);
    bool Load(const std::filesystem::path& Path);
    bool Serialize(nlohmann::json& Json, bool Loading);

};
