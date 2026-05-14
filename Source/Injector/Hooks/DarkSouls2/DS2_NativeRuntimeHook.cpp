/*
 * Dark Souls 3 - Open Server
 * Copyright (C) 2021 Tim Leonard
 *
 * This program is free software; licensed under the MIT license.
 * You should have received a copy of the license along with this program.
 * If not, see <https://opensource.org/licenses/MIT>.
 */

#include "Injector/Hooks/DarkSouls2/DS2_NativeRuntimeHook.h"
#include "Injector/Injector/Injector.h"
#include "Shared/Core/Utils/Logging.h"
#include "Shared/Core/Utils/Strings.h"
#include "ThirdParty/detours/src/detours.h"
#include "ThirdParty/nlohmann/json.hpp"

#include <Windows.h>

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <ctime>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <memory>
#include <mutex>
#include <sstream>
#include <string>
#include <system_error>
#include <vector>

namespace
{
    constexpr uintmax_t kKnownSotfsSteamExeSize = 28200992;
    constexpr uintptr_t kRestAtBonfireRva = 0x17DC40;
    constexpr uintptr_t kItemGiveRva = 0x1AC3D0;
    constexpr uintptr_t kItemStructConvertRva = 0x05D950;
    constexpr uintptr_t kItemPopupDisplayRva = 0x501080;
    constexpr uintptr_t kInventoryFirstHopOffset = 0xA8;
    constexpr uintptr_t kInventoryHopOffset = 0x10;
    constexpr uintptr_t kItemInventoryBagListOffset = 0x10;
    constexpr uintptr_t kItemDisplayManagerOffset = 0x22E0;
    constexpr uintptr_t kInventoryBagEntriesOffset = 0xF0;
    constexpr int32_t kInventoryBagEntryCount = 3840;
    constexpr size_t kMaxItemGiveBatchCount = 8;
    constexpr uintptr_t kInventoryAdjustQuantityVtableSlotOffset = 0x30;
    constexpr uint8_t kGameManagerImpPattern[] = {
        0x48, 0x8B, 0x05, 0x00, 0x00, 0x00, 0x00,
        0x48, 0x8B, 0x58, 0x38,
        0x48, 0x85, 0xDB,
        0x74, 0x00,
        0xF6,
    };
    constexpr char kGameManagerImpMask[] = "xxx????xxxxxxxx?x";
    LONG s_worker_started = 0;
    LONG s_bonfire_rest_observer_state = 0;
    LONG s_inventory_observer_state = 0;
    LONG s_inventory_adjust_event_count = 0;
    std::mutex s_event_log_mutex;

    struct RuntimeWorkerConfig
    {
        std::string SessionId;
        std::filesystem::path EventLog;
        std::filesystem::path CommandInbox;
        std::string ServerName;
        std::string ServerHostname;
        int ServerPort = 0;
        bool SeparateSaves = false;
        bool FileOverrides = false;
        std::string SaveFileExtension;
        uintmax_t ExeSize = 0;
        bool ExeMatchesKnownBaseline = false;
        uintptr_t GameBaseAddress = 0;
        size_t GameImageSize = 0;
        uintptr_t GameManagerImpGlobalAddress = 0;
    };

    using InventoryAdjustQuantityFn =
        int64_t(__fastcall*)(void* inventory, int32_t item_id, int32_t amount, int32_t unk3, int32_t unk4);
    using RestAtBonfireFn = void(__fastcall*)(void* bonfire_context, int32_t bonfire_id);
    using ItemGiveFn = void(__fastcall*)(void* inventory_bag_list, void* item_spawn_list, int32_t item_count);
    using ItemStructConvertFn = void(__fastcall*)(void* display_stack, void* item_spawn_list, int32_t item_count, int32_t show_popup);
    using ItemPopupDisplayFn = void(__fastcall*)(void* item_display_manager, void* display_stack);

    RestAtBonfireFn s_original_rest_at_bonfire = nullptr;
    InventoryAdjustQuantityFn s_original_inventory_adjust_quantity = nullptr;
    void** s_inventory_adjust_quantity_slot = nullptr;
    RuntimeWorkerConfig s_active_runtime_config;
    bool s_active_runtime_config_ready = false;

#pragma pack(push, 1)
    struct Ds2ItemGiveEntry
    {
        int32_t Reserved = 0;
        int32_t ItemId = 0;
        float Durability = 0.0f;
        int16_t Quantity = 0;
        uint8_t Upgrade = 0;
        uint8_t Gem = 0;
    };
#pragma pack(pop)

    static_assert(sizeof(Ds2ItemGiveEntry) == 16, "DS2 item grant entry must stay 16 bytes.");

    struct RuntimeGrantItem
    {
        int32_t ItemId;
        int16_t Quantity;
        const char* RuntimeName;
    };

    constexpr RuntimeGrantItem kBonfireRuntimeItems[] = {
        { 60360001, 1, "bonfire_liberation_scroll" },
        { 62060001, 1, "bonfire_abyssal_eye_orb" },
        { 62060002, 1, "bonfire_ominous_tome" },
    };

    const RuntimeGrantItem* FindBonfireRuntimeItem(int32_t item_id)
    {
        for (const RuntimeGrantItem& item : kBonfireRuntimeItems)
        {
            if (item.ItemId == item_id)
            {
                return &item;
            }
        }

        return nullptr;
    }

    struct ItemGiveContext
    {
        void* InventoryBagList = nullptr;
        void* ItemDisplayManager = nullptr;
        uintptr_t InventoryBagEntries = 0;
        uintptr_t GameManager = 0;
        uintptr_t InventoryOwner = 0;
    };

    std::filesystem::path GetDs2ExecutablePath()
    {
        HMODULE module = GetModuleHandleW(L"DarkSoulsII.exe");
        if (module == nullptr)
        {
            return {};
        }

        wchar_t path[MAX_PATH] = {};
        if (GetModuleFileNameW(module, path, static_cast<DWORD>(std::size(path))) == 0)
        {
            return {};
        }

        return std::filesystem::path(path);
    }

    const char* BoolText(bool value)
    {
        return value ? "true" : "false";
    }

    std::string UtcNowIso8601()
    {
        auto now = std::chrono::system_clock::now();
        std::time_t now_time = std::chrono::system_clock::to_time_t(now);

        std::tm utc = {};
        gmtime_s(&utc, &now_time);

        char buffer[32] = {};
        std::strftime(buffer, sizeof(buffer), "%Y-%m-%dT%H:%M:%SZ", &utc);
        return buffer;
    }

    std::string SanitizeSessionId(std::string value)
    {
        for (char& ch : value)
        {
            const bool allowed =
                (ch >= 'a' && ch <= 'z') ||
                (ch >= 'A' && ch <= 'Z') ||
                (ch >= '0' && ch <= '9') ||
                ch == '-' ||
                ch == '_';
            if (!allowed)
            {
                ch = '_';
            }
        }

        return value.empty() ? "ds2" : value;
    }

    std::filesystem::path FallbackRuntimePath(const char* filename)
    {
        return std::filesystem::current_path() / "DS2NativeRuntime" / filename;
    }

    std::filesystem::path ResolveRuntimePath(const std::string& configured_path, const char* fallback_filename)
    {
        if (configured_path.empty())
        {
            return FallbackRuntimePath(fallback_filename);
        }

        return std::filesystem::path(configured_path).lexically_normal();
    }

    void EnsureParentDirectory(const std::filesystem::path& path)
    {
        std::error_code error;
        const std::filesystem::path parent = path.parent_path();
        if (!parent.empty())
        {
            std::filesystem::create_directories(parent, error);
        }
    }

    std::string HexPointer(uintptr_t value)
    {
        std::ostringstream stream;
        stream << "0x" << std::hex << std::uppercase << value;
        return stream.str();
    }

    template <typename T>
    bool TryReadValue(uintptr_t address, T& value)
    {
        value = {};
        if (address == 0)
        {
            return false;
        }

        bool success = false;
        __try
        {
            value = *reinterpret_cast<const T*>(address);
            success = true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            value = {};
            success = false;
        }

        return success;
    }

    bool TryReadPointer(uintptr_t address, uintptr_t& value)
    {
        return TryReadValue(address, value) && value != 0;
    }

    void AddPointerProbeStep(
        nlohmann::json& steps,
        const char* name,
        uintptr_t address,
        bool success,
        uintptr_t value)
    {
        nlohmann::json step;
        step["name"] = name;
        step["address"] = HexPointer(address);
        step["success"] = success;
        step["value"] = HexPointer(value);
        steps.push_back(step);
    }

    size_t GetModuleImageSize(HMODULE module)
    {
        if (module == nullptr)
        {
            return 0;
        }

        const auto base = reinterpret_cast<uintptr_t>(module);
        const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
        if (dos->e_magic != IMAGE_DOS_SIGNATURE)
        {
            return 0;
        }

        const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS*>(base + dos->e_lfanew);
        if (nt->Signature != IMAGE_NT_SIGNATURE)
        {
            return 0;
        }

        return nt->OptionalHeader.SizeOfImage;
    }

    bool IsReadablePage(DWORD protect)
    {
        if ((protect & PAGE_GUARD) != 0 ||
            (protect & PAGE_NOACCESS) != 0)
        {
            return false;
        }

        const DWORD base_protect = protect & 0xFF;
        return base_protect == PAGE_READONLY ||
            base_protect == PAGE_READWRITE ||
            base_protect == PAGE_WRITECOPY ||
            base_protect == PAGE_EXECUTE_READ ||
            base_protect == PAGE_EXECUTE_READWRITE ||
            base_protect == PAGE_EXECUTE_WRITECOPY;
    }

    bool PatternMatches(uintptr_t address)
    {
        for (size_t i = 0; i < std::size(kGameManagerImpPattern); ++i)
        {
            if (kGameManagerImpMask[i] == 'x' &&
                *reinterpret_cast<const uint8_t*>(address + i) != kGameManagerImpPattern[i])
            {
                return false;
            }
        }

        return true;
    }

    uintptr_t ResolveGameManagerImpGlobalAddress(uintptr_t module_base, size_t module_size)
    {
        if (module_base == 0 || module_size < std::size(kGameManagerImpPattern))
        {
            return 0;
        }

        const uintptr_t module_end = module_base + module_size;
        uintptr_t cursor = module_base;
        while (cursor < module_end)
        {
            MEMORY_BASIC_INFORMATION info = {};
            if (VirtualQuery(reinterpret_cast<const void*>(cursor), &info, sizeof(info)) == 0)
            {
                break;
            }

            const uintptr_t region_base = reinterpret_cast<uintptr_t>(info.BaseAddress);
            const uintptr_t region_end = std::min(
                region_base + info.RegionSize,
                module_end);
            if (info.State == MEM_COMMIT && IsReadablePage(info.Protect))
            {
                const uintptr_t start = std::max(region_base, module_base);
                if (region_end <= start ||
                    region_end - start < std::size(kGameManagerImpPattern))
                {
                    cursor = region_end > cursor ? region_end : cursor + 0x1000;
                    continue;
                }

                const uintptr_t last = region_end - std::size(kGameManagerImpPattern);
                for (uintptr_t address = start; address <= last; ++address)
                {
                    if (!PatternMatches(address))
                    {
                        continue;
                    }

                    const int32_t displacement =
                        *reinterpret_cast<const int32_t*>(address + 3);
                    return address + 7 + displacement;
                }
            }

            cursor = region_end > cursor ? region_end : cursor + 0x1000;
        }

        return 0;
    }

    nlohmann::json ProbeInventoryRuntime(const RuntimeWorkerConfig& config)
    {
        nlohmann::json payload;
        payload["pattern"] = "BobTable.GameManagerImp.inventory_vtable_slot_0x30";
        payload["game_base"] = HexPointer(config.GameBaseAddress);
        payload["game_image_size"] = config.GameImageSize;
        payload["game_manager_imp_global"] =
            HexPointer(config.GameManagerImpGlobalAddress);
        payload["adjust_quantity_vtable_slot_offset"] =
            HexPointer(kInventoryAdjustQuantityVtableSlotOffset);
        payload["exe_matches_known_baseline"] = config.ExeMatchesKnownBaseline;
        payload["observer_state"] =
            s_inventory_observer_state == 2 ? "armed" : "probe_only";
        payload["observer_original_target"] =
            HexPointer(reinterpret_cast<uintptr_t>(s_original_inventory_adjust_quantity));

        nlohmann::json steps = nlohmann::json::array();
        if (config.GameBaseAddress == 0)
        {
            payload["resolved"] = false;
            payload["reason"] = "DarkSoulsII.exe module base was not available";
            payload["steps"] = steps;
            return payload;
        }
        if (config.GameManagerImpGlobalAddress == 0)
        {
            payload["resolved"] = false;
            payload["reason"] = "GameManagerImp AOB was not found";
            payload["steps"] = steps;
            return payload;
        }

        auto read_step = [&steps](
            const char* name,
            uintptr_t address,
            uintptr_t& value) -> bool
        {
            const bool success = TryReadPointer(address, value);
            AddPointerProbeStep(steps, name, address, success, value);
            return success;
        };

        uintptr_t inventory_root = 0;
        uintptr_t inventory_owner = 0;
        uintptr_t inventory_node_a = 0;
        uintptr_t inventory_node_b = 0;
        uintptr_t inventory_object = 0;
        uintptr_t inventory_vtable = 0;
        uintptr_t adjust_quantity_target = 0;

        if (!read_step("GameManagerImp", config.GameManagerImpGlobalAddress, inventory_root) ||
            !read_step("game_manager+0xA8", inventory_root + kInventoryFirstHopOffset, inventory_owner) ||
            !read_step("owner+0x10", inventory_owner + kInventoryHopOffset, inventory_node_a) ||
            !read_step("node_a+0x10", inventory_node_a + kInventoryHopOffset, inventory_node_b) ||
            !read_step("node_b+0x10", inventory_node_b + kInventoryHopOffset, inventory_object) ||
            !read_step("inventory.vtable", inventory_object, inventory_vtable) ||
            !read_step(
                "vtable+0x30",
                inventory_vtable + kInventoryAdjustQuantityVtableSlotOffset,
                adjust_quantity_target))
        {
            payload["resolved"] = false;
            payload["reason"] = "inventory pointer chain is not ready yet";
            payload["steps"] = steps;
            return payload;
        }

        payload["resolved"] = true;
        payload["inventory_object"] = HexPointer(inventory_object);
        payload["inventory_vtable"] = HexPointer(inventory_vtable);
        payload["adjust_quantity_slot"] =
            HexPointer(inventory_vtable + kInventoryAdjustQuantityVtableSlotOffset);
        payload["adjust_quantity_target"] = HexPointer(adjust_quantity_target);
        payload["ready_for_observer"] = config.ExeMatchesKnownBaseline;
        payload["steps"] = steps;
        return payload;
    }

    void AppendRuntimeEvent(
        const RuntimeWorkerConfig& config,
        const char* event_name,
        const nlohmann::json& data = nlohmann::json::object())
    {
        std::lock_guard<std::mutex> guard(s_event_log_mutex);
        EnsureParentDirectory(config.EventLog);

        nlohmann::json event;
        event["time_utc"] = UtcNowIso8601();
        event["runtime"] = "ds2_native";
        event["session_id"] = config.SessionId;
        event["event"] = event_name;
        event["data"] = data;

        std::ofstream stream(config.EventLog, std::ios::out | std::ios::app | std::ios::binary);
        if (!stream)
        {
            return;
        }

        stream << event.dump() << "\n";
    }

    RuntimeWorkerConfig GetActiveRuntimeConfig(const char* fallback_filename)
    {
        RuntimeWorkerConfig config;
        if (s_active_runtime_config_ready)
        {
            config = s_active_runtime_config;
        }
        else
        {
            config.SessionId = "ds2";
            config.EventLog = std::filesystem::current_path() /
                "DS2NativeRuntime" /
                fallback_filename;
            config.GameBaseAddress =
                reinterpret_cast<uintptr_t>(GetModuleHandleW(L"DarkSoulsII.exe"));
        }

        return config;
    }

    bool ResolveItemGiveContext(
        const RuntimeWorkerConfig& config,
        ItemGiveContext& context,
        nlohmann::json& payload)
    {
        nlohmann::json steps = nlohmann::json::array();
        auto read_step = [&steps](
            const char* name,
            uintptr_t address,
            uintptr_t& value) -> bool
        {
            const bool success = TryReadPointer(address, value);
            AddPointerProbeStep(steps, name, address, success, value);
            return success;
        };

        uintptr_t game_manager = 0;
        uintptr_t inventory_owner = 0;
        uintptr_t inventory_bag_list = 0;
        uintptr_t item_display_manager = 0;
        if (!read_step("GameManagerImp", config.GameManagerImpGlobalAddress, game_manager) ||
            !read_step("game_manager+0xA8", game_manager + kInventoryFirstHopOffset, inventory_owner) ||
            !read_step("inventory_owner+0x10", inventory_owner + kItemInventoryBagListOffset, inventory_bag_list) ||
            !read_step("game_manager+0x22E0", game_manager + kItemDisplayManagerOffset, item_display_manager))
        {
            payload["resolved"] = false;
            payload["reason"] = "item give pointer chain is not ready";
            payload["steps"] = steps;
            return false;
        }

        context.GameManager = game_manager;
        context.InventoryOwner = inventory_owner;
        context.InventoryBagList = reinterpret_cast<void*>(inventory_bag_list);
        context.ItemDisplayManager = reinterpret_cast<void*>(item_display_manager);
        context.InventoryBagEntries = inventory_bag_list + kInventoryBagEntriesOffset;

        payload["resolved"] = true;
        payload["game_manager"] = HexPointer(game_manager);
        payload["inventory_owner"] = HexPointer(inventory_owner);
        payload["inventory_bag_list"] = HexPointer(inventory_bag_list);
        payload["inventory_bag_entries"] = HexPointer(context.InventoryBagEntries);
        payload["item_display_manager"] = HexPointer(item_display_manager);
        payload["steps"] = steps;
        return true;
    }

    bool BuildMissingBonfireItemBatch(
        const ItemGiveContext& context,
        std::vector<Ds2ItemGiveEntry>& entries,
        nlohmann::json& payload)
    {
        int32_t quantities[std::size(kBonfireRuntimeItems)] = {};
        bool found[std::size(kBonfireRuntimeItems)] = {};
        bool has_empty_slot = false;

        for (int32_t position = 0; position < kInventoryBagEntryCount; ++position)
        {
            const uintptr_t item =
                context.InventoryBagEntries +
                (static_cast<uintptr_t>(position) * sizeof(Ds2ItemGiveEntry));

            int32_t item_id = 0;
            if (!TryReadValue(item, item_id))
            {
                payload["inventory_readable"] = false;
                payload["failed_slot"] = position;
                payload["failed_address"] = HexPointer(item);
                return false;
            }

            if (item_id == 0)
            {
                has_empty_slot = true;
                continue;
            }

            for (size_t runtime_index = 0; runtime_index < std::size(kBonfireRuntimeItems); ++runtime_index)
            {
                if (item_id != kBonfireRuntimeItems[runtime_index].ItemId)
                {
                    continue;
                }

                int32_t quantity = 0;
                if (!TryReadValue(item + 0x8, quantity))
                {
                    payload["inventory_readable"] = false;
                    payload["failed_slot"] = position;
                    payload["failed_address"] = HexPointer(item + 0x8);
                    return false;
                }

                found[runtime_index] = true;
                quantities[runtime_index] = std::max(quantities[runtime_index], quantity);
            }
        }

        payload["inventory_readable"] = true;
        payload["has_empty_slot"] = has_empty_slot;

        nlohmann::json checked_items = nlohmann::json::array();
        for (size_t runtime_index = 0; runtime_index < std::size(kBonfireRuntimeItems); ++runtime_index)
        {
            const RuntimeGrantItem& item = kBonfireRuntimeItems[runtime_index];
            const int32_t held_quantity = found[runtime_index] ? quantities[runtime_index] : 0;
            const int32_t missing_quantity =
                std::max<int32_t>(0, static_cast<int32_t>(item.Quantity) - held_quantity);

            nlohmann::json item_payload;
            item_payload["runtime_name"] = item.RuntimeName;
            item_payload["item_id"] = item.ItemId;
            item_payload["item_id_hex"] = HexPointer(static_cast<uint32_t>(item.ItemId));
            item_payload["held_quantity"] = held_quantity;
            item_payload["target_quantity"] = item.Quantity;
            item_payload["missing_quantity"] = missing_quantity;
            checked_items.push_back(item_payload);

            if (missing_quantity <= 0)
            {
                continue;
            }

            Ds2ItemGiveEntry entry;
            entry.ItemId = item.ItemId;
            entry.Quantity = static_cast<int16_t>(missing_quantity);
            entries.push_back(entry);
        }

        payload["items_checked"] = checked_items;
        payload["missing_count"] = entries.size();
        if (!entries.empty() && !has_empty_slot)
        {
            payload["reason"] = "inventory is full";
            return false;
        }

        return true;
    }

    bool InvokeItemGiveFunctions(
        uintptr_t game_base,
        const ItemGiveContext& context,
        Ds2ItemGiveEntry* entries,
        int32_t entry_count)
    {
        if (game_base == 0 ||
            context.InventoryBagList == nullptr ||
            context.ItemDisplayManager == nullptr ||
            entries == nullptr ||
            entry_count <= 0)
        {
            return false;
        }

        bool success = false;
        __try
        {
            auto item_give =
                reinterpret_cast<ItemGiveFn>(game_base + kItemGiveRva);
            auto item_struct_convert =
                reinterpret_cast<ItemStructConvertFn>(game_base + kItemStructConvertRva);
            auto item_popup_display =
                reinterpret_cast<ItemPopupDisplayFn>(game_base + kItemPopupDisplayRva);

            alignas(16) uint8_t display_stack[168] = {};
            item_give(context.InventoryBagList, entries, entry_count);
            item_struct_convert(display_stack, entries, entry_count, 1);
            item_popup_display(context.ItemDisplayManager, display_stack);
            success = true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            success = false;
        }

        return success;
    }

    void GrantBonfireRuntimeItems(
        const RuntimeWorkerConfig& config,
        void* bonfire_context,
        int32_t bonfire_id,
        const char* reason)
    {
        nlohmann::json payload;
        payload["reason"] = reason;
        payload["bonfire_context"] =
            HexPointer(reinterpret_cast<uintptr_t>(bonfire_context));
        payload["bonfire_id"] = bonfire_id;
        payload["game_base"] = HexPointer(config.GameBaseAddress);
        payload["item_give"] = HexPointer(config.GameBaseAddress + kItemGiveRva);
        payload["item_struct_convert"] =
            HexPointer(config.GameBaseAddress + kItemStructConvertRva);
        payload["item_popup_display"] =
            HexPointer(config.GameBaseAddress + kItemPopupDisplayRva);
        payload["visible_items_are_placeholders"] = false;

        if (!config.ExeMatchesKnownBaseline)
        {
            payload["granted"] = false;
            payload["reason"] = "unsupported executable baseline";
            AppendRuntimeEvent(config, "bonfire.item_grant_failed", payload);
            return;
        }

        ItemGiveContext item_context;
        if (!ResolveItemGiveContext(config, item_context, payload))
        {
            payload["granted"] = false;
            AppendRuntimeEvent(config, "bonfire.item_grant_failed", payload);
            return;
        }

        std::vector<Ds2ItemGiveEntry> missing_items;
        missing_items.reserve(std::size(kBonfireRuntimeItems));
        if (!BuildMissingBonfireItemBatch(item_context, missing_items, payload))
        {
            payload["granted"] = false;
            AppendRuntimeEvent(config, "bonfire.item_grant_failed", payload);
            return;
        }

        if (missing_items.empty())
        {
            payload["granted"] = false;
            payload["reason"] = "all bonfire runtime items are already present";
            AppendRuntimeEvent(config, "bonfire.item_grant_skipped", payload);
            return;
        }

        alignas(16) uint8_t item_give_buffer[
            32 + (sizeof(Ds2ItemGiveEntry) * kMaxItemGiveBatchCount)] = {};
        auto* item_entries =
            reinterpret_cast<Ds2ItemGiveEntry*>(item_give_buffer + 32);
        const size_t copy_count =
            std::min(missing_items.size(), kMaxItemGiveBatchCount);
        for (size_t index = 0; index < copy_count; ++index)
        {
            item_entries[index] = missing_items[index];
        }

        const bool success = InvokeItemGiveFunctions(
            config.GameBaseAddress,
            item_context,
            item_entries,
            static_cast<int32_t>(copy_count));

        payload["granted"] = success;
        payload["granted_count"] = copy_count;
        AppendRuntimeEvent(
            config,
            success ? "bonfire.item_grant_result" : "bonfire.item_grant_failed",
            payload);
    }

    bool ResolveInventoryAdjustQuantitySlot(
        const RuntimeWorkerConfig& config,
        uintptr_t& inventory_object,
        uintptr_t& inventory_vtable,
        uintptr_t& adjust_quantity_slot,
        uintptr_t& adjust_quantity_target)
    {
        uintptr_t inventory_root = 0;
        uintptr_t inventory_owner = 0;
        uintptr_t inventory_node_a = 0;
        uintptr_t inventory_node_b = 0;

        if (!TryReadPointer(config.GameManagerImpGlobalAddress, inventory_root) ||
            !TryReadPointer(inventory_root + kInventoryFirstHopOffset, inventory_owner) ||
            !TryReadPointer(inventory_owner + kInventoryHopOffset, inventory_node_a) ||
            !TryReadPointer(inventory_node_a + kInventoryHopOffset, inventory_node_b) ||
            !TryReadPointer(inventory_node_b + kInventoryHopOffset, inventory_object) ||
            !TryReadPointer(inventory_object, inventory_vtable))
        {
            return false;
        }

        adjust_quantity_slot =
            inventory_vtable + kInventoryAdjustQuantityVtableSlotOffset;
        return TryReadPointer(adjust_quantity_slot, adjust_quantity_target);
    }

    void LogInventoryAdjustQuantity(
        void* inventory,
        int32_t item_id,
        int32_t amount,
        int32_t forwarded_amount,
        int32_t unk3,
        int32_t unk4)
    {
        const LONG event_index = InterlockedIncrement(&s_inventory_adjust_event_count);
        const RuntimeGrantItem* runtime_item = FindBonfireRuntimeItem(item_id);
        const bool known_online_item =
            item_id == 62050000 ||
            item_id == 62060000 ||
            runtime_item != nullptr;
        if (event_index > 256 && !known_online_item)
        {
            return;
        }

        RuntimeWorkerConfig config;
        if (s_active_runtime_config_ready)
        {
            config = s_active_runtime_config;
        }
        else
        {
            config.SessionId = "ds2";
            config.EventLog = std::filesystem::current_path() /
                "DS2NativeRuntime" /
                "inventory_adjust.events.jsonl";
        }

        nlohmann::json payload;
        payload["event_index"] = event_index;
        payload["inventory"] = HexPointer(reinterpret_cast<uintptr_t>(inventory));
        payload["item_id"] = item_id;
        payload["item_id_hex"] = HexPointer(static_cast<uint32_t>(item_id));
        payload["amount"] = amount;
        payload["forwarded_amount"] = forwarded_amount;
        payload["unk3"] = unk3;
        payload["unk4"] = unk4;
        payload["known_online_item"] = known_online_item;
        payload["bonfire_runtime_item"] = runtime_item != nullptr;
        if (runtime_item != nullptr)
        {
            payload["runtime_name"] = runtime_item->RuntimeName;
            payload["runtime_consumption_suppressed"] = amount != forwarded_amount;
        }
        payload["observer_state"] = "armed";
        payload["original_target"] =
            HexPointer(reinterpret_cast<uintptr_t>(s_original_inventory_adjust_quantity));

        AppendRuntimeEvent(config, "inventory.adjust_quantity", payload);

        if (runtime_item != nullptr && amount != 0)
        {
            AppendRuntimeEvent(config, "bonfire.custom_item_use", payload);
        }
    }

    int64_t __fastcall InventoryAdjustQuantityHook(
        void* inventory,
        int32_t item_id,
        int32_t amount,
        int32_t unk3,
        int32_t unk4)
    {
        const RuntimeGrantItem* runtime_item = FindBonfireRuntimeItem(item_id);
        const int32_t forwarded_amount =
            runtime_item != nullptr && amount != 0 ? 0 : amount;
        LogInventoryAdjustQuantity(
            inventory,
            item_id,
            amount,
            forwarded_amount,
            unk3,
            unk4);

        InventoryAdjustQuantityFn original = s_original_inventory_adjust_quantity;
        if (original == nullptr)
        {
            return 0;
        }

        return original(inventory, item_id, forwarded_amount, unk3, unk4);
    }

    void __fastcall RestAtBonfireHook(void* bonfire_context, int32_t bonfire_id)
    {
        RuntimeWorkerConfig config =
            GetActiveRuntimeConfig("bonfire_rest.events.jsonl");

        nlohmann::json payload;
        payload["bonfire_context"] =
            HexPointer(reinterpret_cast<uintptr_t>(bonfire_context));
        payload["bonfire_id"] = bonfire_id;
        payload["original_target"] =
            HexPointer(reinterpret_cast<uintptr_t>(s_original_rest_at_bonfire));
        AppendRuntimeEvent(config, "bonfire.rest", payload);

        RestAtBonfireFn original = s_original_rest_at_bonfire;
        if (original != nullptr)
        {
            original(bonfire_context, bonfire_id);
        }

        GrantBonfireRuntimeItems(
            config,
            bonfire_context,
            bonfire_id,
            "rest_at_bonfire");
    }

    bool TryArmBonfireRestObserver(const RuntimeWorkerConfig& config)
    {
        if (!config.ExeMatchesKnownBaseline)
        {
            return false;
        }

        if (config.GameBaseAddress == 0)
        {
            return false;
        }

        if (InterlockedCompareExchange(&s_bonfire_rest_observer_state, 1, 0) != 0)
        {
            return s_bonfire_rest_observer_state == 2;
        }

        s_original_rest_at_bonfire =
            reinterpret_cast<RestAtBonfireFn>(config.GameBaseAddress + kRestAtBonfireRva);

        DetourTransactionBegin();
        DetourUpdateThread(GetCurrentThread());
        DetourAttach(&(PVOID&)s_original_rest_at_bonfire, RestAtBonfireHook);
        const LONG result = DetourTransactionCommit();

        nlohmann::json payload;
        payload["rest_at_bonfire"] =
            HexPointer(config.GameBaseAddress + kRestAtBonfireRva);
        payload["hook_target"] =
            HexPointer(reinterpret_cast<uintptr_t>(&RestAtBonfireHook));
        payload["detour_result"] = result;

        if (result != NO_ERROR)
        {
            AppendRuntimeEvent(config, "bonfire.rest_observer_arm_failed", payload);
            s_original_rest_at_bonfire = nullptr;
            InterlockedExchange(&s_bonfire_rest_observer_state, 0);
            return false;
        }

        payload["original_target"] =
            HexPointer(reinterpret_cast<uintptr_t>(s_original_rest_at_bonfire));
        AppendRuntimeEvent(config, "bonfire.rest_observer_armed", payload);
        InterlockedExchange(&s_bonfire_rest_observer_state, 2);
        return true;
    }

    bool TryArmInventoryObserver(const RuntimeWorkerConfig& config)
    {
        if (!config.ExeMatchesKnownBaseline)
        {
            return false;
        }

        if (InterlockedCompareExchange(&s_inventory_observer_state, 1, 0) != 0)
        {
            return s_inventory_observer_state == 2;
        }

        uintptr_t inventory_object = 0;
        uintptr_t inventory_vtable = 0;
        uintptr_t adjust_quantity_slot = 0;
        uintptr_t adjust_quantity_target = 0;
        if (!ResolveInventoryAdjustQuantitySlot(
            config,
            inventory_object,
            inventory_vtable,
            adjust_quantity_slot,
            adjust_quantity_target))
        {
            InterlockedExchange(&s_inventory_observer_state, 0);
            return false;
        }

        const auto hook_target =
            reinterpret_cast<uintptr_t>(&InventoryAdjustQuantityHook);
        if (adjust_quantity_target == hook_target)
        {
            InterlockedExchange(&s_inventory_observer_state, 2);
            return true;
        }

        DWORD old_protect = 0;
        auto* slot = reinterpret_cast<void**>(adjust_quantity_slot);
        if (!VirtualProtect(
            slot,
            sizeof(void*),
            PAGE_EXECUTE_READWRITE,
            &old_protect))
        {
            nlohmann::json payload;
            payload["slot"] = HexPointer(adjust_quantity_slot);
            payload["error"] = GetLastError();
            AppendRuntimeEvent(config, "inventory.observer_arm_failed", payload);
            InterlockedExchange(&s_inventory_observer_state, 0);
            return false;
        }

        s_original_inventory_adjust_quantity =
            reinterpret_cast<InventoryAdjustQuantityFn>(adjust_quantity_target);
        s_inventory_adjust_quantity_slot = slot;
        *slot = reinterpret_cast<void*>(&InventoryAdjustQuantityHook);

        DWORD ignored = 0;
        VirtualProtect(slot, sizeof(void*), old_protect, &ignored);
        FlushInstructionCache(GetCurrentProcess(), slot, sizeof(void*));

        nlohmann::json payload;
        payload["inventory_object"] = HexPointer(inventory_object);
        payload["inventory_vtable"] = HexPointer(inventory_vtable);
        payload["slot"] = HexPointer(adjust_quantity_slot);
        payload["original_target"] = HexPointer(adjust_quantity_target);
        payload["hook_target"] = HexPointer(hook_target);
        AppendRuntimeEvent(config, "inventory.observer_armed", payload);

        InterlockedExchange(&s_inventory_observer_state, 2);
        return true;
    }

    void EmitInventoryProbeAndMaybeArm(const RuntimeWorkerConfig& config)
    {
        nlohmann::json probe = ProbeInventoryRuntime(config);
        AppendRuntimeEvent(config, "inventory.probe", probe);
        if (probe.value("resolved", false))
        {
            TryArmInventoryObserver(config);
        }
    }

    nlohmann::json MakeStatusPayload(const RuntimeWorkerConfig& config)
    {
        nlohmann::json payload;
        payload["server_name"] = config.ServerName;
        payload["server_hostname"] = config.ServerHostname;
        payload["server_port"] = config.ServerPort;
        payload["separate_saves"] = config.SeparateSaves;
        payload["save_file_extension"] = config.SaveFileExtension;
        payload["file_overrides"] = config.FileOverrides;
        payload["exe_size"] = config.ExeSize;
        payload["exe_matches_known_baseline"] = config.ExeMatchesKnownBaseline;
        payload["game_base"] = HexPointer(config.GameBaseAddress);
        payload["game_image_size"] = config.GameImageSize;
        payload["game_manager_imp_global"] =
            HexPointer(config.GameManagerImpGlobalAddress);
        payload["command_inbox"] = config.CommandInbox.string();
        return payload;
    }

    bool IsKnownRuntimeCommand(const std::string& command)
    {
        return command == "ping" ||
            command == "session.create" ||
            command == "session.join" ||
            command == "session.leave" ||
            command == "session.reconnect" ||
            command == "inventory.probe" ||
            command == "player.sync.request" ||
            command == "world.sync.request";
    }

    void HandleCommandLine(const RuntimeWorkerConfig& config, const std::string& line)
    {
        if (line.empty())
        {
            return;
        }

        nlohmann::json payload;
        payload["raw"] = line;

        try
        {
            nlohmann::json parsed = nlohmann::json::parse(line);
            const std::string command = parsed.value("command", "");
            payload["command"] = command;
            payload["accepted"] = IsKnownRuntimeCommand(command);

            if (command == "ping")
            {
                payload["note"] = "pong";
                AppendRuntimeEvent(config, "command.pong", payload);
                return;
            }

            if (command == "inventory.probe")
            {
                payload["note"] = "inventory probe emitted";
                AppendRuntimeEvent(config, "command.received", payload);
                EmitInventoryProbeAndMaybeArm(config);
                return;
            }

            payload["note"] = "command bus is active; gameplay handlers are not armed yet";
            AppendRuntimeEvent(config, "command.received", payload);
        }
        catch (const nlohmann::json::parse_error& error)
        {
            payload["accepted"] = false;
            payload["error"] = error.what();
            AppendRuntimeEvent(config, "command.parse_failed", payload);
        }
    }

    uintmax_t PollCommandInbox(const RuntimeWorkerConfig& config, uintmax_t read_offset)
    {
        std::error_code error;
        const uintmax_t size = std::filesystem::exists(config.CommandInbox, error)
            ? std::filesystem::file_size(config.CommandInbox, error)
            : 0;
        if (error || size == 0)
        {
            return 0;
        }
        if (size < read_offset)
        {
            read_offset = 0;
        }
        if (size == read_offset)
        {
            return read_offset;
        }

        std::ifstream stream(config.CommandInbox, std::ios::in | std::ios::binary);
        if (!stream)
        {
            return read_offset;
        }

        stream.seekg(static_cast<std::streamoff>(read_offset));
        std::string line;
        while (std::getline(stream, line))
        {
            if (!line.empty() && line.back() == '\r')
            {
                line.pop_back();
            }
            HandleCommandLine(config, line);
        }

        return size;
    }

    DWORD WINAPI RuntimeWorkerThread(void* parameter)
    {
        std::unique_ptr<RuntimeWorkerConfig> config(
            static_cast<RuntimeWorkerConfig*>(parameter));

        EnsureParentDirectory(config->CommandInbox);
        if (!std::filesystem::exists(config->CommandInbox))
        {
            std::ofstream create(config->CommandInbox, std::ios::out | std::ios::app | std::ios::binary);
        }

        AppendRuntimeEvent(*config, "runtime.started", MakeStatusPayload(*config));
        TryArmBonfireRestObserver(*config);
        EmitInventoryProbeAndMaybeArm(*config);

        uintmax_t command_offset = 0;
        uint32_t heartbeat_counter = 0;
        while (true)
        {
            command_offset = PollCommandInbox(*config, command_offset);

            if ((heartbeat_counter++ % 3) == 0)
            {
                nlohmann::json heartbeat;
                heartbeat["counter"] = heartbeat_counter;
                heartbeat["command_inbox_size"] = command_offset;
                AppendRuntimeEvent(*config, "runtime.heartbeat", heartbeat);
            }
            if ((heartbeat_counter % 15) == 0)
            {
                EmitInventoryProbeAndMaybeArm(*config);
            }

            Sleep(2000);
        }
    }
}

bool DS2_NativeRuntimeHook::Install(Injector& injector)
{
    const RuntimeConfig& config = injector.GetConfig();

    Log("DS2 native runtime baseline active.");
    Log("DS2 runtime config: private_save=%s save_extension=%s file_overrides=%s",
        BoolText(config.EnableSeperateSaveFiles),
        config.SaveFileExtension.c_str(),
        BoolText(config.EnableModFileOverrides));

    if (!config.ModOverrideDirectory.empty())
    {
        Log("DS2 external override path: %s", config.ModOverrideDirectory.c_str());
    }

    const std::filesystem::path exe_path = GetDs2ExecutablePath();
    if (exe_path.empty())
    {
        Warning("DS2 native runtime could not resolve DarkSoulsII.exe path for diagnostics.");
        return true;
    }

    Log("DS2 executable: %s", NarrowString(exe_path.wstring()).c_str());

    std::error_code error;
    const uintmax_t exe_size = std::filesystem::file_size(exe_path, error);
    if (error)
    {
        Warning("DS2 native runtime could not read executable size: %s", error.message().c_str());
        return true;
    }

    if (exe_size != kKnownSotfsSteamExeSize)
    {
        Warning("DS2 executable size is %llu bytes; known Steam SOTFS baseline is %llu bytes.",
            static_cast<unsigned long long>(exe_size),
            static_cast<unsigned long long>(kKnownSotfsSteamExeSize));
    }
    else
    {
        Success("DS2 executable matches the known Steam SOTFS baseline.");
    }

    if (!config.EnableDs2NativeRuntime)
    {
        Log("DS2 native runtime command bridge disabled by config.");
        return true;
    }

    if (InterlockedCompareExchange(&s_worker_started, 1, 0) != 0)
    {
        return true;
    }

    auto* worker_config = new RuntimeWorkerConfig();
    worker_config->SessionId = SanitizeSessionId(config.Ds2NativeRuntimeSessionId);
    worker_config->EventLog = ResolveRuntimePath(
        config.Ds2NativeRuntimeEventLog,
        "ds2_native.events.jsonl");
    worker_config->CommandInbox = ResolveRuntimePath(
        config.Ds2NativeRuntimeCommandInbox,
        "ds2_native.commands.jsonl");
    worker_config->ServerName = config.ServerName;
    worker_config->ServerHostname = config.ServerHostname;
    worker_config->ServerPort = config.ServerPort;
    worker_config->SeparateSaves = config.EnableSeperateSaveFiles;
    worker_config->FileOverrides = config.EnableModFileOverrides;
    worker_config->SaveFileExtension = config.SaveFileExtension;
    worker_config->ExeSize = exe_size;
    worker_config->ExeMatchesKnownBaseline = exe_size == kKnownSotfsSteamExeSize;
    HMODULE game_module = GetModuleHandleW(L"DarkSoulsII.exe");
    worker_config->GameBaseAddress = reinterpret_cast<uintptr_t>(game_module);
    worker_config->GameImageSize = GetModuleImageSize(game_module);
    worker_config->GameManagerImpGlobalAddress =
        ResolveGameManagerImpGlobalAddress(
            worker_config->GameBaseAddress,
            worker_config->GameImageSize);
    s_active_runtime_config = *worker_config;
    s_active_runtime_config_ready = true;

    const std::string event_log_text = worker_config->EventLog.string();
    const std::string command_inbox_text = worker_config->CommandInbox.string();
    HANDLE thread = CreateThread(nullptr, 0, RuntimeWorkerThread, worker_config, 0, nullptr);
    if (thread == nullptr)
    {
        Error("Failed to start DS2 native runtime command bridge thread: %u", GetLastError());
        delete worker_config;
        InterlockedExchange(&s_worker_started, 0);
        return true;
    }

    CloseHandle(thread);
    Log("DS2 native runtime command bridge active: events=%s commands=%s",
        event_log_text.c_str(),
        command_inbox_text.c_str());

    return true;
}

void DS2_NativeRuntimeHook::Uninstall()
{
    if (s_original_rest_at_bonfire != nullptr)
    {
        DetourTransactionBegin();
        DetourUpdateThread(GetCurrentThread());
        DetourDetach(&(PVOID&)s_original_rest_at_bonfire, RestAtBonfireHook);
        DetourTransactionCommit();
        s_original_rest_at_bonfire = nullptr;
        InterlockedExchange(&s_bonfire_rest_observer_state, 0);
    }

    if (s_inventory_adjust_quantity_slot != nullptr &&
        s_original_inventory_adjust_quantity != nullptr)
    {
        DWORD old_protect = 0;
        if (VirtualProtect(
            s_inventory_adjust_quantity_slot,
            sizeof(void*),
            PAGE_EXECUTE_READWRITE,
            &old_protect))
        {
            *s_inventory_adjust_quantity_slot =
                reinterpret_cast<void*>(s_original_inventory_adjust_quantity);
            DWORD ignored = 0;
            VirtualProtect(
                s_inventory_adjust_quantity_slot,
                sizeof(void*),
                old_protect,
                &ignored);
            FlushInstructionCache(
                GetCurrentProcess(),
                s_inventory_adjust_quantity_slot,
                sizeof(void*));
        }
    }
}

const char* DS2_NativeRuntimeHook::GetName()
{
    return "DS2 Native Runtime";
}
