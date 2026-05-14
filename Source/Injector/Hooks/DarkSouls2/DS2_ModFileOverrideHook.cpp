/*
 * Dark Souls 3 - Open Server
 * Copyright (C) 2021 Tim Leonard
 *
 * This program is free software; licensed under the MIT license.
 * You should have received a copy of the license along with this program.
 * If not, see <https://opensource.org/licenses/MIT>.
 */

#include "Injector/Hooks/DarkSouls2/DS2_ModFileOverrideHook.h"
#include "Injector/Injector/Injector.h"
#include "Shared/Core/Utils/Logging.h"
#include "Shared/Core/Utils/Strings.h"
#include "ThirdParty/detours/src/detours.h"

#include <Windows.h>

#include <algorithm>
#include <cwctype>
#include <cstring>
#include <filesystem>
#include <mutex>
#include <optional>
#include <string>
#include <unordered_set>
#include <utility>
#include <vector>

namespace
{
    struct DLString
    {
        wchar_t* String;
        void* Unknown;
        uint64_t Length;
        uint64_t Capacity;
    };

    using virtual_to_archive_path_sotfs_p = void* (*)(void* context, DLString* path);
    virtual_to_archive_path_sotfs_p s_original_virtual_to_archive_path = nullptr;

    using create_file_p = HANDLE(WINAPI*)(LPCWSTR lpFileName, DWORD dwDesiredAccess, DWORD dwShareMode,
        LPSECURITY_ATTRIBUTES lpSecurityAttributes, DWORD dwCreationDisposition,
        DWORD dwFlagsAndAttributes, HANDLE hTemplateFile);
    create_file_p s_original_create_file = CreateFileW;

    using nt_set_information_thread_p = LONG(WINAPI*)(HANDLE threadHandle, ULONG threadInfoClass,
        PVOID threadInfo, ULONG threadInfoLength);
    nt_set_information_thread_p s_original_nt_set_information_thread = nullptr;

    std::filesystem::path s_game_root;
    std::filesystem::path s_override_root;
    std::wstring s_save_file_extension = L".ds3os";
    bool s_enable_file_overrides = false;
    bool s_enable_save_rename = false;
    bool s_cache_file_paths = true;
    bool s_thread_hide_hook_attached = false;
    bool s_archive_hook_attached = false;
    bool s_create_file_hook_attached = false;

    std::mutex s_cache_mutex;
    std::unordered_set<std::wstring> s_override_hits;
    std::unordered_set<std::wstring> s_override_misses;

    std::wstring ToLower(std::wstring value)
    {
        std::transform(value.begin(), value.end(), value.begin(), [](wchar_t ch) {
            return static_cast<wchar_t>(towlower(ch));
        });
        return value;
    }

    bool StartsWithInsensitive(const std::wstring& value, const std::wstring& prefix)
    {
        if (value.size() < prefix.size())
        {
            return false;
        }

        return ToLower(value.substr(0, prefix.size())) == ToLower(prefix);
    }

    bool EndsWithInsensitive(const std::wstring& value, const std::wstring& suffix)
    {
        if (value.size() < suffix.size())
        {
            return false;
        }

        return ToLower(value.substr(value.size() - suffix.size())) == ToLower(suffix);
    }

    std::wstring TrimLeadingSlashes(std::wstring value)
    {
        while (!value.empty() && (value.front() == L'/' || value.front() == L'\\'))
        {
            value.erase(value.begin());
        }
        return value;
    }

    std::filesystem::path ResolveOverrideRoot(const std::string& configured_path)
    {
        if (configured_path.empty())
        {
            return {};
        }

        std::wstring wide_path = WidenString(configured_path);
        if (!wide_path.empty() && (wide_path.front() == L'\\' || wide_path.front() == L'/') &&
            (wide_path.size() < 2 || wide_path[1] != L':'))
        {
            return (s_game_root / TrimLeadingSlashes(wide_path)).lexically_normal();
        }

        std::filesystem::path path = wide_path;
        if (path.is_relative())
        {
            path = s_game_root / path;
        }
        return path.lexically_normal();
    }

    std::wstring NormalizeSaveFileExtension(const std::string& configured_extension)
    {
        std::wstring extension = ToLower(WidenString(configured_extension));
        if (extension.empty())
        {
            extension = L".ds3os";
        }
        if (extension.front() != L'.')
        {
            extension.insert(extension.begin(), L'.');
        }
        if (extension.size() < 2 || extension.size() > 16 ||
            extension.find(L'\\') != std::wstring::npos ||
            extension.find(L'/') != std::wstring::npos)
        {
            return L".ds3os";
        }

        return extension;
    }

    bool IsPathUnderOrSame(const std::filesystem::path& path, const std::filesystem::path& root)
    {
        std::wstring value = ToLower(path.lexically_normal().wstring());
        std::wstring prefix = ToLower(root.lexically_normal().wstring());

        if (value == prefix)
        {
            return true;
        }

        if (value.size() <= prefix.size() || value.substr(0, prefix.size()) != prefix)
        {
            return false;
        }

        wchar_t separator = value[prefix.size()];
        return separator == L'\\' || separator == L'/';
    }

    bool IsSafeRelativePath(const std::filesystem::path& relative_path)
    {
        if (relative_path.empty() || relative_path.is_absolute() || relative_path.has_root_name())
        {
            return false;
        }

        for (const std::filesystem::path& part : relative_path)
        {
            std::wstring value = part.wstring();
            if (value == L".." || value.empty())
            {
                return false;
            }
        }

        return true;
    }

    bool PathExists(const std::filesystem::path& path)
    {
        DWORD attrs = GetFileAttributesW(path.c_str());
        return attrs != INVALID_FILE_ATTRIBUTES;
    }

    std::optional<std::filesystem::path> BuildOverridePathFromSuffix(std::wstring suffix)
    {
        suffix = TrimLeadingSlashes(std::move(suffix));
        if (suffix.empty())
        {
            return std::nullopt;
        }

        std::filesystem::path relative_path = std::filesystem::path(suffix).lexically_normal();
        if (!IsSafeRelativePath(relative_path))
        {
            return std::nullopt;
        }

        std::filesystem::path override_path = (s_override_root / relative_path).lexically_normal();
        if (!IsPathUnderOrSame(override_path, s_override_root))
        {
            return std::nullopt;
        }

        return override_path;
    }

    bool OverrideExistsForVirtualPath(const std::wstring& virtual_path, size_t suffix_offset)
    {
        if (!s_enable_file_overrides || virtual_path.size() <= suffix_offset)
        {
            return false;
        }

        if (s_cache_file_paths)
        {
            std::scoped_lock lock(s_cache_mutex);
            if (s_override_hits.find(virtual_path) != s_override_hits.end())
            {
                return true;
            }
            if (s_override_misses.find(virtual_path) != s_override_misses.end())
            {
                return false;
            }
        }

        bool exists = false;
        if (auto override_path = BuildOverridePathFromSuffix(virtual_path.substr(suffix_offset)))
        {
            exists = PathExists(*override_path);
        }

        if (s_cache_file_paths)
        {
            std::scoped_lock lock(s_cache_mutex);
            if (exists)
            {
                s_override_hits.insert(virtual_path);
            }
            else
            {
                s_override_misses.insert(virtual_path);
            }
        }

        return exists;
    }

    void RewritePrefix(DLString* path, size_t prefix_length)
    {
        if (path->Length < prefix_length || path->Capacity <= prefix_length)
        {
            return;
        }

        path->String[0] = L'.';
        for (size_t i = 1; i < prefix_length; i++)
        {
            path->String[i] = L'/';
        }
    }

    void RewriteVirtualPath(DLString* path)
    {
        if (path == nullptr || path->String == nullptr || path->Length < 6)
        {
            return;
        }

        std::wstring virtual_path(path->String, static_cast<size_t>(path->Length));
        if (StartsWithInsensitive(virtual_path, L"data:/"))
        {
            if (OverrideExistsForVirtualPath(virtual_path, 6))
            {
                RewritePrefix(path, 6);
            }
        }
        else if (StartsWithInsensitive(virtual_path, L"gamedata:/"))
        {
            if (OverrideExistsForVirtualPath(virtual_path, 9))
            {
                RewritePrefix(path, 10);
            }
        }
        else if (StartsWithInsensitive(virtual_path, L"game_"))
        {
            if (OverrideExistsForVirtualPath(virtual_path, 10))
            {
                RewritePrefix(path, 10);
            }
        }
    }

    void* VirtualToArchivePathHook(void* context, DLString* path)
    {
        RewriteVirtualPath(path);
        return s_original_virtual_to_archive_path(context, path);
    }

    LONG WINAPI NtSetInformationThreadHook(HANDLE threadHandle, ULONG threadInfoClass,
        PVOID threadInfo, ULONG threadInfoLength)
    {
        constexpr ULONG ThreadHideFromDebugger = 0x11;
        if (threadInfoClass == ThreadHideFromDebugger)
        {
            return 0x1;
        }

        return s_original_nt_set_information_thread(threadHandle, threadInfoClass,
            threadInfo, threadInfoLength);
    }

    std::optional<std::filesystem::path> BuildOverridePathFromRequestedPath(const std::filesystem::path& requested_path)
    {
        if (!s_enable_file_overrides)
        {
            return std::nullopt;
        }

        std::filesystem::path absolute_path = requested_path;
        if (absolute_path.is_relative())
        {
            auto override_path = BuildOverridePathFromSuffix(absolute_path.lexically_normal().wstring());
            if (!override_path || !PathExists(*override_path))
            {
                return std::nullopt;
            }

            return override_path;
        }
        absolute_path = absolute_path.lexically_normal();

        if (!IsPathUnderOrSame(absolute_path, s_game_root) || IsPathUnderOrSame(absolute_path, s_override_root))
        {
            return std::nullopt;
        }

        std::wstring suffix = absolute_path.wstring().substr(s_game_root.wstring().size());
        auto override_path = BuildOverridePathFromSuffix(suffix);
        if (!override_path || !PathExists(*override_path))
        {
            return std::nullopt;
        }

        return override_path;
    }

    std::wstring RewriteSavePath(std::wstring filename)
    {
        const std::wstring extension_sl2_bak = L".sl2.bak";
        const std::wstring extension_sl2 = L".sl2";

        if (EndsWithInsensitive(filename, extension_sl2_bak))
        {
            filename = filename.substr(0, filename.size() - extension_sl2_bak.size()) +
                s_save_file_extension + L".bak";
        }
        else if (EndsWithInsensitive(filename, extension_sl2))
        {
            filename = filename.substr(0, filename.size() - extension_sl2.size()) + s_save_file_extension;
        }

        return filename;
    }

    void PatchWideSaveExtensionInModule(Injector& injector)
    {
        if (s_save_file_extension != L".sl3")
        {
            return;
        }

        std::vector<Injector::AOBByte> pattern = {
            uint8_t('s'), uint8_t(0x00), uint8_t('l'), uint8_t(0x00), uint8_t('2')
        };
        std::vector<intptr_t> matches = injector.SearchAOB(pattern);
        for (intptr_t address : matches)
        {
            DWORD old_protect = 0;
            if (VirtualProtect(reinterpret_cast<LPVOID>(address), pattern.size(),
                    PAGE_EXECUTE_READWRITE, &old_protect))
            {
                *reinterpret_cast<uint8_t*>(address + 4) = uint8_t('3');
                FlushInstructionCache(GetCurrentProcess(), reinterpret_cast<LPCVOID>(address), pattern.size());
                VirtualProtect(reinterpret_cast<LPVOID>(address), pattern.size(), old_protect, &old_protect);
            }
        }

        Log("DS2 save extension string patch: %zu wide .sl2 marker(s) changed to .sl3.", matches.size());
    }

    void PatchInt32(intptr_t address, size_t offset, int value)
    {
        memcpy(reinterpret_cast<void*>(address + offset), &value, sizeof(value));
    }

    void PatchShadowResolutionPattern(Injector& injector,
        const std::vector<Injector::AOBByte>& pattern,
        const std::vector<std::pair<size_t, int>>& patches,
        size_t protect_size,
        const char* label)
    {
        std::vector<intptr_t> matches = injector.SearchAOB(pattern);
        if (matches.empty())
        {
            Log("DS2 shadow resolution patch skipped; pattern not found: %s.", label);
            return;
        }

        intptr_t address = matches[0];
        DWORD old_protect = 0;
        if (!VirtualProtect(reinterpret_cast<LPVOID>(address), protect_size,
                PAGE_EXECUTE_READWRITE, &old_protect))
        {
            Error("Failed to unprotect DS2 shadow resolution patch site: %s.", label);
            return;
        }

        for (const auto& patch : patches)
        {
            PatchInt32(address, patch.first, patch.second);
        }

        FlushInstructionCache(GetCurrentProcess(), reinterpret_cast<LPCVOID>(address), protect_size);
        VirtualProtect(reinterpret_cast<LPVOID>(address), protect_size, old_protect, &old_protect);
        Log("DS2 shadow resolution patch applied: %s at 0x%p.", label, reinterpret_cast<void*>(address));
    }

    void PatchDs2ShadowMapResolution(Injector& injector, const RuntimeConfig& config)
    {
        std::vector<Injector::AOBByte> directional_pattern = {
            uint8_t(0xc7), uint8_t(0x44), uint8_t(0x24), uint8_t(0x28), uint8_t(0x00), uint8_t(0x08), uint8_t(0x00), uint8_t(0x00),
            uint8_t(0x48), uint8_t(0x89), uint8_t(0x44), uint8_t(0x24), uint8_t(0x30), uint8_t(0x48), uint8_t(0x8b), uint8_t(0x47),
            uint8_t(0x40), uint8_t(0xc7), uint8_t(0x44), uint8_t(0x24), uint8_t(0x2c), uint8_t(0x00), uint8_t(0x08), uint8_t(0x00),
            uint8_t(0x00), uint8_t(0x48), uint8_t(0x89),
        };
        std::vector<Injector::AOBByte> dynamic_pattern = {
            uint8_t(0xc7), uint8_t(0x01), uint8_t(0x00), uint8_t(0x02), uint8_t(0x00), uint8_t(0x00), uint8_t(0xc7), uint8_t(0x41),
            uint8_t(0x04), uint8_t(0x02), uint8_t(0x00), uint8_t(0x00), uint8_t(0x00), uint8_t(0xc7), uint8_t(0x41), uint8_t(0x08),
            uint8_t(0x04), uint8_t(0x00), uint8_t(0x00), uint8_t(0x00), uint8_t(0xc7), uint8_t(0x41), uint8_t(0x0c), uint8_t(0x06),
            uint8_t(0x00), uint8_t(0x00), uint8_t(0x00),
        };

        PatchShadowResolutionPattern(injector, directional_pattern, {
            { 4, config.Ds2DirectionalShadowResolution },
            { 21, config.Ds2DirectionalShadowResolution },
        }, 0x20, "directional");

        PatchShadowResolutionPattern(injector, dynamic_pattern, {
            { 2, config.Ds2DynamicSpotShadowResolution },
            { 37, config.Ds2DynamicPointShadowResolution },
            { 72, config.Ds2DynamicAtlasShadowResolution },
        }, 0x80, "dynamic");
    }

    HANDLE OpenOriginalWithRetries(const std::filesystem::path& path, DWORD dwDesiredAccess, DWORD dwShareMode,
        LPSECURITY_ATTRIBUTES lpSecurityAttributes, DWORD dwCreationDisposition,
        DWORD dwFlagsAndAttributes, HANDLE hTemplateFile)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            HANDLE result = s_original_create_file(path.c_str(), dwDesiredAccess, dwShareMode,
                lpSecurityAttributes, dwCreationDisposition, dwFlagsAndAttributes, hTemplateFile);
            if (result != INVALID_HANDLE_VALUE)
            {
                return result;
            }
        }

        return INVALID_HANDLE_VALUE;
    }

    HANDLE WINAPI CreateFileHook(LPCWSTR lpFileName, DWORD dwDesiredAccess, DWORD dwShareMode,
        LPSECURITY_ATTRIBUTES lpSecurityAttributes, DWORD dwCreationDisposition,
        DWORD dwFlagsAndAttributes, HANDLE hTemplateFile)
    {
        if (lpFileName == nullptr)
        {
            return s_original_create_file(lpFileName, dwDesiredAccess, dwShareMode, lpSecurityAttributes,
                dwCreationDisposition, dwFlagsAndAttributes, hTemplateFile);
        }

        std::wstring filename = lpFileName;
        if (s_enable_save_rename)
        {
            filename = RewriteSavePath(filename);
        }

        if (auto override_path = BuildOverridePathFromRequestedPath(filename))
        {
            return OpenOriginalWithRetries(*override_path, dwDesiredAccess, dwShareMode,
                lpSecurityAttributes, dwCreationDisposition, dwFlagsAndAttributes, hTemplateFile);
        }

        return s_original_create_file(filename.c_str(), dwDesiredAccess, dwShareMode, lpSecurityAttributes,
            dwCreationDisposition, dwFlagsAndAttributes, hTemplateFile);
    }
}

bool DS2_ModFileOverrideHook::Install(Injector& injector)
{
    const RuntimeConfig& config = injector.GetConfig();
    s_enable_file_overrides = config.EnableModFileOverrides;
    s_enable_save_rename = config.EnableSeperateSaveFiles;
    s_cache_file_paths = config.CacheModFilePaths;
    s_save_file_extension = NormalizeSaveFileExtension(config.SaveFileExtension);
    s_game_root = std::filesystem::current_path().lexically_normal();
    s_override_root = ResolveOverrideRoot(config.ModOverrideDirectory);

    if (s_enable_save_rename)
    {
        PatchWideSaveExtensionInModule(injector);
    }

    if (config.EnableDs2ShadowResolutionPatches)
    {
        PatchDs2ShadowMapResolution(injector, config);
    }

    HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
    s_original_nt_set_information_thread = ntdll != nullptr
        ? reinterpret_cast<nt_set_information_thread_p>(
            GetProcAddress(ntdll, "NtSetInformationThread"))
        : nullptr;
    if (s_original_nt_set_information_thread == nullptr)
    {
        Error("Failed to find NtSetInformationThread for DS2 hide-thread bypass.");
        return false;
    }

    DetourTransactionBegin();
    DetourUpdateThread(GetCurrentThread());
    DetourAttach(&(PVOID&)s_original_nt_set_information_thread, NtSetInformationThreadHook);
    LONG thread_hide_result = DetourTransactionCommit();
    if (thread_hide_result != NO_ERROR)
    {
        Error("Failed to attach DS2 hide-thread bypass: %ld", thread_hide_result);
        return false;
    }
    s_thread_hide_hook_attached = true;

    if (s_enable_file_overrides)
    {
        if (!std::filesystem::exists(s_override_root))
        {
            Error("DS2 mod override directory does not exist: %s", NarrowString(s_override_root.wstring()).c_str());
            return false;
        }

        Log("DS2 mod override directory: %s", NarrowString(s_override_root.wstring()).c_str());
    }

    if (s_enable_file_overrides)
    {
        std::vector<Injector::AOBByte> archive_pattern = {
            uint8_t(0x41), uint8_t(0x54), uint8_t(0x41), uint8_t(0x56), uint8_t(0x41), uint8_t(0x57), uint8_t(0x48), uint8_t(0x83),
            uint8_t(0xec), uint8_t(0x40), uint8_t(0x48), uint8_t(0xc7), uint8_t(0x44), uint8_t(0x24), uint8_t(0x20), uint8_t(0xfe),
            uint8_t(0xff), uint8_t(0xff), uint8_t(0xff), uint8_t(0x48), uint8_t(0x89), uint8_t(0x5c), uint8_t(0x24), uint8_t(0x60),
            uint8_t(0x48), uint8_t(0x89), uint8_t(0x6c), uint8_t(0x24), uint8_t(0x68), uint8_t(0x48), uint8_t(0x89), uint8_t(0x74),
            uint8_t(0x24), uint8_t(0x70), uint8_t(0x48), uint8_t(0x89), uint8_t(0x7c), uint8_t(0x24), uint8_t(0x78), uint8_t(0x48),
        };

        std::vector<intptr_t> archive_matches = injector.SearchAOB(archive_pattern);
        if (archive_matches.empty())
        {
            Error("Failed to find DS2 virtual archive path function.");
            return false;
        }
        if (archive_matches.size() > 1)
        {
            Log("DS2 virtual archive path pattern matched %zu sites; using first at 0x%p to match ModEngine.",
                archive_matches.size(), reinterpret_cast<void*>(archive_matches[0]));
        }

        s_original_virtual_to_archive_path =
            reinterpret_cast<virtual_to_archive_path_sotfs_p>(archive_matches[0]);

        DetourTransactionBegin();
        DetourUpdateThread(GetCurrentThread());
        DetourAttach(&(PVOID&)s_original_virtual_to_archive_path, VirtualToArchivePathHook);
        LONG result = DetourTransactionCommit();
        if (result != NO_ERROR)
        {
            Error("Failed to attach DS2 virtual archive path hook: %ld", result);
            return false;
        }

        s_archive_hook_attached = true;
    }

    if (s_enable_file_overrides || s_enable_save_rename)
    {
        DetourTransactionBegin();
        DetourUpdateThread(GetCurrentThread());
        DetourAttach(&(PVOID&)s_original_create_file, CreateFileHook);
        LONG result = DetourTransactionCommit();
        if (result != NO_ERROR)
        {
            Error("Failed to attach DS2 CreateFileW hook: %ld", result);
            return false;
        }

        s_create_file_hook_attached = true;
    }

    return true;
}

void DS2_ModFileOverrideHook::Uninstall()
{
    if (s_thread_hide_hook_attached)
    {
        DetourTransactionBegin();
        DetourUpdateThread(GetCurrentThread());
        DetourDetach(&(PVOID&)s_original_nt_set_information_thread, NtSetInformationThreadHook);
        DetourTransactionCommit();
        s_thread_hide_hook_attached = false;
    }

    if (s_archive_hook_attached)
    {
        DetourTransactionBegin();
        DetourUpdateThread(GetCurrentThread());
        DetourDetach(&(PVOID&)s_original_virtual_to_archive_path, VirtualToArchivePathHook);
        DetourTransactionCommit();
        s_archive_hook_attached = false;
    }

    if (s_create_file_hook_attached)
    {
        DetourTransactionBegin();
        DetourUpdateThread(GetCurrentThread());
        DetourDetach(&(PVOID&)s_original_create_file, CreateFileHook);
        DetourTransactionCommit();
        s_create_file_hook_attached = false;
    }
}

const char* DS2_ModFileOverrideHook::GetName()
{
    return "DS2 Mod File Override";
}
