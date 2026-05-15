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
#include <intrin.h>
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
    constexpr uintptr_t kEyeOrbUseValidationRva = 0x2D3B20;
    constexpr uintptr_t kItemGiveRva = 0x1AC3D0;
    constexpr uintptr_t kItemStructConvertRva = 0x05D950;
    constexpr uintptr_t kItemPopupDisplayRva = 0x501080;
    constexpr uintptr_t kInventoryFirstHopOffset = 0xA8;
    constexpr uintptr_t kInventoryHopOffset = 0x10;
    constexpr uintptr_t kItemInventoryBagListOffset = 0x10;
    constexpr uintptr_t kItemDisplayManagerOffset = 0x22E0;
    constexpr uintptr_t kInventoryBagEntriesOffset = 0xF0;
    constexpr int32_t kInventoryBagEntryCount = 3840;
    constexpr size_t kMaxItemGiveBatchCount = 16;
    constexpr uintptr_t kInventoryAdjustQuantityVtableSlotOffset = 0x30;
    constexpr uintptr_t kInventoryUseItemVtableSlotOffset = 0x38;
    constexpr uintptr_t kInventoryGetItemEntryVtableSlotOffset = 0x08;
    constexpr uintptr_t kInventorySelectedItemEntryVtableSlotOffset = 0x70;
    constexpr uintptr_t kInventoryItemEntryItemIdOffset = 0x14;
    constexpr uintptr_t kInventoryItemEntryNativeUseItemIdOffset = 0x18;
    constexpr uintptr_t kInventoryItemEntryQuantityOffset = 0x20;
    constexpr uintptr_t kInventoryItemEntryFlagsOffset = 0x1F;
    constexpr uintptr_t kInventorySelectedEntryReturnRvaA = 0x1B19DA;
    constexpr uintptr_t kInventorySelectedEntryReturnRvaB = 0x1B19FF;
    constexpr uintptr_t kInventorySelectedEntryReturnRvaC = 0x1B1A6F;
    constexpr uintptr_t kInventorySelectedItemCategoryRva = 0x1B19D0;
    constexpr uintptr_t kInventorySelectedActionCandidateReturnRva = 0x32FF15;
    constexpr uintptr_t kInventorySelectedActionExecuteRva = 0x500C40;
    constexpr uintptr_t kInventorySelectedActionExecuteReturnRva = 0x32FF24;
    constexpr uintptr_t kInventoryEntryEnumerationReturnRva = 0x1B281C;
    constexpr uint64_t kRuntimeItemActionDebounceMs = 750;
    constexpr uint64_t kRuntimeItemVanillaSuppressWindowMs = 3000;
    constexpr uint64_t kRuntimeItemSelectionWindowMs = 15000;
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
    LONG s_item_use_validation_observer_state = 0;
    LONG s_inventory_selected_item_category_observer_state = 0;
    LONG s_inventory_selected_action_execute_observer_state = 0;
    LONG s_inventory_observer_state = 0;
    LONG s_inventory_adjust_event_count = 0;
    LONG s_inventory_use_event_count = 0;
    LONG s_inventory_selected_event_count = 0;
    LONG s_inventory_selected_action_event_count = 0;
    LONG s_inventory_selected_action_execute_event_count = 0;
    LONG s_item_use_validation_event_count = 0;
    std::mutex s_event_log_mutex;
    std::mutex s_runtime_state_mutex;
    std::mutex s_inventory_selection_mutex;
    uint64_t s_runtime_action_sequence = 0;
    int32_t s_last_runtime_action_item_id = 0;
    uint64_t s_last_runtime_action_tick = 0;
    int32_t s_pending_runtime_suppression_item_id = 0;
    uint64_t s_pending_runtime_suppression_tick = 0;
    uint64_t s_pending_runtime_suppression_sequence = 0;
    int32_t s_last_selected_runtime_item_id = 0;
    int32_t s_last_selected_native_use_item_id = 0;
    uintptr_t s_last_selected_entry_address = 0;
    uint64_t s_last_selected_tick = 0;
    bool s_session_open = false;
    std::string s_session_mode = "solo";
    std::string s_last_session_request = "none";
    std::string s_last_runtime_command = "none";
    std::string s_last_runtime_message_key = "none";
    std::string s_last_runtime_message_en;
    std::string s_last_runtime_message_es;
    std::string s_runtime_stage = "idle";
    std::string s_online_intent = "none";
    int32_t s_rule_preset_index = 0;
    uint32_t s_recovery_request_count = 0;
    uint32_t s_invasion_request_count = 0;
    uint32_t s_taunt_request_count = 0;
    uint32_t s_infection_request_count = 0;
    uint32_t s_curse_sigil_count = 0;

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
    using InventoryGetItemEntryFn =
        void* (__fastcall*)(void* inventory, int32_t item_slot);
    using InventoryUseItemFn =
        int64_t(__fastcall*)(void* inventory, int32_t item_slot, int32_t action_arg);
    using InventorySelectedItemEntryFn =
        void* (__fastcall*)(void* inventory);
    using InventorySelectedItemCategoryFn =
        int32_t(__fastcall*)(void* inventory);
    using InventorySelectedActionExecuteFn =
        void(__fastcall*)(void* action_manager, int32_t selected_category, int32_t action_flag);
    using ItemUseValidationFn = bool(__fastcall*)(int32_t item_id);
    using RestAtBonfireFn = void(__fastcall*)(void* bonfire_context, int32_t bonfire_id);
    using ItemGiveFn = void(__fastcall*)(void* inventory_bag_list, void* item_spawn_list, int32_t item_count);
    using ItemStructConvertFn = void(__fastcall*)(void* display_stack, void* item_spawn_list, int32_t item_count, int32_t show_popup);
    using ItemPopupDisplayFn = void(__fastcall*)(void* item_display_manager, void* display_stack);

    RestAtBonfireFn s_original_rest_at_bonfire = nullptr;
    ItemUseValidationFn s_original_item_use_validation = nullptr;
    InventoryAdjustQuantityFn s_original_inventory_adjust_quantity = nullptr;
    InventoryUseItemFn s_original_inventory_use_item = nullptr;
    InventorySelectedItemEntryFn s_original_inventory_selected_item_entry = nullptr;
    InventorySelectedItemCategoryFn s_original_inventory_selected_item_category = nullptr;
    InventorySelectedActionExecuteFn s_original_inventory_selected_action_execute = nullptr;
    void** s_inventory_adjust_quantity_slot = nullptr;
    void** s_inventory_use_item_slot = nullptr;
    void** s_inventory_selected_item_entry_slot = nullptr;
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
        int32_t NativeUseItemId;
        int16_t Quantity;
        bool GrantAtBonfire;
        const char* RuntimeName;
        const char* ActionCommand;
        const char* ActionLabel;
        int32_t ActionCategory;
        bool SuppressVanillaContinuation;
    };

    constexpr RuntimeGrantItem kBonfireRuntimeItems[] = {
        {
            62061000,
            62061000,
            1,
            true,
            "bonfire_blessed_eye_orb",
            "session.create",
            "create a Bonfire co-op session",
            15,
            true,
        },
        {
            62061001,
            62061001,
            1,
            true,
            "bonfire_crystal_eye_orb",
            "session.join",
            "join a Bonfire co-op session",
            15,
            true,
        },
        {
            62061002,
            62061002,
            1,
            true,
            "bonfire_chaos_eye_orb",
            "session.invade",
            "invade a Bonfire co-op session",
            14,
            true,
        },
        {
            62061003,
            62061003,
            1,
            true,
            "bonfire_abyssal_eye_orb",
            "session.leave",
            "leave or disband the current Bonfire session",
            14,
            true,
        },
        {
            62061004,
            62061004,
            1,
            true,
            "bonfire_ominous_tome",
            "rules.cycle",
            "cycle Bonfire runtime rules",
            15,
            true,
        },
        {
            62061005,
            62061005,
            1,
            true,
            "bonfire_dried_fingers",
            "invasions.taunt",
            "invite invaders into the Bonfire world",
            13,
            true,
        },
        {
            62061006,
            62061006,
            1,
            true,
            "bonfire_cursed_pendant",
            "world.infection",
            "apply a Bonfire world disaster request",
            13,
            true,
        },
        {
            62061007,
            62061007,
            1,
            true,
            "bonfire_crimson_blossom",
            "curse.accrue",
            "accrue a Bonfire curse sigil",
            13,
            true,
        },
        {
            62061008,
            62061008,
            1,
            true,
            "bonfire_deliverance_parchment",
            "world.recover",
            "revive allies and repair Bonfire runtime items",
            13,
            true,
        },
        {
            60360001,
            60151000,
            1,
            false,
            "bonfire_legacy_liberation_scroll",
            "world.recover",
            "legacy DS2 test item; kept recognized but no longer granted",
            13,
            true,
        },
        {
            62060001,
            62060000,
            1,
            false,
            "bonfire_legacy_abyssal_eye_orb",
            "session.leave",
            "legacy DS2 test item; kept recognized but no longer granted",
            14,
            true,
        },
        {
            62060002,
            62050000,
            1,
            false,
            "bonfire_legacy_ominous_tome",
            "rules.cycle",
            "legacy DS2 test item; kept recognized but no longer granted",
            15,
            true,
        },
    };

    struct RuntimeBehaviorDescriptor
    {
        const char* MessageKey;
        const char* MessageEnglish;
        const char* MessageSpanish;
        const char* BehaviorPhase;
        const char* OnlineIntent;
    };

    RuntimeBehaviorDescriptor RuntimeBehaviorForCommand(
        const std::string& command)
    {
        if (command == "session.create")
        {
            return {
                "session.create.armed",
                "DEBUG Bonfire: host-link item used. Waiting for a private peer handshake.",
                "DEBUG Bonfire: item de enlace anfitrion usado. Esperando enlace privado con otro jugador.",
                "host_bootstrap",
                "cooperate_host",
            };
        }

        if (command == "session.join")
        {
            return {
                "session.join.armed",
                "DEBUG Bonfire: guest-link item used. The runtime will request a private host link.",
                "DEBUG Bonfire: item de enlace invitado usado. El runtime pedira enlace privado con anfitrion.",
                "guest_handshake",
                "cooperate_join",
            };
        }

        if (command == "session.invade")
        {
            return {
                "session.invade.queued",
                "DEBUG Bonfire: invasion-link item used. No vanilla world search will be used.",
                "DEBUG Bonfire: item de invasion privada usado. No se usara la busqueda vanilla de mundos.",
                "invasion_match",
                "invade_private_world",
            };
        }

        if (command == "session.leave")
        {
            return {
                "session.leave.closed",
                "DEBUG Bonfire: leave-link item used. Runtime returned to solo state.",
                "DEBUG Bonfire: item de salida usado. El runtime regreso a estado solitario.",
                "session_teardown",
                "close_session",
            };
        }

        if (command == "rules.cycle")
        {
            return {
                "rules.cycle.changed",
                "DEBUG Bonfire: rules item used. The next sync will export the world contract.",
                "DEBUG Bonfire: item de reglas usado. La proxima sincronizacion exportara el contrato del mundo.",
                "rule_negotiation",
                "set_rules",
            };
        }

        if (command == "invasions.taunt")
        {
            return {
                "invasions.taunt.raised",
                "DEBUG Bonfire: invader beacon item used. Private invaders may be matched by Bonfire.",
                "DEBUG Bonfire: item baliza de invasores usado. Bonfire puede emparejar invasores privados.",
                "invasion_beacon",
                "open_invaders",
            };
        }

        if (command == "world.infection")
        {
            return {
                "world.infection.queued",
                "DEBUG Bonfire: world mutation item used. The private runtime owns the effect.",
                "DEBUG Bonfire: item de mutacion del mundo usado. El runtime privado controla el efecto.",
                "world_disaster",
                "mutate_world",
            };
        }

        if (command == "curse.accrue")
        {
            return {
                "curse.accrue.marked",
                "DEBUG Bonfire: curse pressure item used. The counter is stored by Bonfire.",
                "DEBUG Bonfire: item de presion maldita usado. Bonfire guarda el contador.",
                "curse_pressure",
                "escalate_curse",
            };
        }

        if (command == "world.recover")
        {
            return {
                "world.recover.requested",
                "DEBUG Bonfire: recovery item used. Allies and runtime items will be repaired by Bonfire.",
                "DEBUG Bonfire: item de recuperacion usado. Bonfire reparara aliados e items del runtime.",
                "world_recovery",
                "recover_world",
            };
        }

        if (command == "session.reconnect")
        {
            return {
                "session.reconnect.requested",
                "DEBUG Bonfire: reconnect command used. The private link will be rebuilt.",
                "DEBUG Bonfire: comando de reconexion usado. El enlace privado se reconstruira.",
                "session_reconnect",
                "reconnect_session",
            };
        }

        return {
            "runtime.state.updated",
            "DEBUG Bonfire: runtime state updated.",
            "DEBUG Bonfire: estado del runtime actualizado.",
            "runtime_state",
            "state_update",
        };
    }

    void AddRuntimeBehaviorPayload(
        nlohmann::json& payload,
        const RuntimeBehaviorDescriptor& behavior)
    {
        payload["message_key"] = behavior.MessageKey;
        payload["message_en"] = behavior.MessageEnglish;
        payload["message_es"] = behavior.MessageSpanish;
        payload["behavior_phase"] = behavior.BehaviorPhase;
        payload["online_intent"] = behavior.OnlineIntent;
        payload["bonfire_owned_behavior"] = true;
        payload["native_shell_use_only"] = true;
        payload["vanilla_continuation_policy"] =
            "suppress_after_bonfire_action";
    }

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

    const RuntimeGrantItem* FindBonfireRuntimeItemByNativeUseItem(
        int32_t native_use_item_id)
    {
        for (const RuntimeGrantItem& item : kBonfireRuntimeItems)
        {
            if (item.NativeUseItemId == native_use_item_id)
            {
                return &item;
            }
        }

        return nullptr;
    }

    struct RuntimeSelectionMatch
    {
        bool Matched = false;
        int32_t RuntimeItemId = 0;
        int32_t NativeUseItemId = 0;
        uintptr_t EntryAddress = 0;
        uint64_t AgeMs = 0;
    };

    struct ItemGiveContext
    {
        void* InventoryBagList = nullptr;
        void* ItemDisplayManager = nullptr;
        uintptr_t InventoryBagEntries = 0;
        uintptr_t GameManager = 0;
        uintptr_t InventoryOwner = 0;
    };

    void EmitInventoryProbeAndMaybeArm(const RuntimeWorkerConfig& config);
    void GrantBonfireRuntimeItems(
        const RuntimeWorkerConfig& config,
        void* bonfire_context,
        int32_t bonfire_id,
        const char* reason);

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
        payload["pattern"] = "BobTable.GameManagerImp.inventory_vtable_slots_0x30_0x38_0x70";
        payload["game_base"] = HexPointer(config.GameBaseAddress);
        payload["game_image_size"] = config.GameImageSize;
        payload["game_manager_imp_global"] =
            HexPointer(config.GameManagerImpGlobalAddress);
        payload["adjust_quantity_vtable_slot_offset"] =
            HexPointer(kInventoryAdjustQuantityVtableSlotOffset);
        payload["use_item_vtable_slot_offset"] =
            HexPointer(kInventoryUseItemVtableSlotOffset);
        payload["selected_item_vtable_slot_offset"] =
            HexPointer(kInventorySelectedItemEntryVtableSlotOffset);
        payload["exe_matches_known_baseline"] = config.ExeMatchesKnownBaseline;
        payload["observer_state"] =
            s_inventory_observer_state == 2 ? "armed" : "probe_only";
        payload["observer_original_target"] =
            HexPointer(reinterpret_cast<uintptr_t>(s_original_inventory_adjust_quantity));
        payload["use_observer_original_target"] =
            HexPointer(reinterpret_cast<uintptr_t>(s_original_inventory_use_item));
        payload["selected_observer_original_target"] =
            HexPointer(reinterpret_cast<uintptr_t>(s_original_inventory_selected_item_entry));

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
        uintptr_t use_item_target = 0;
        uintptr_t selected_item_target = 0;

        if (!read_step("GameManagerImp", config.GameManagerImpGlobalAddress, inventory_root) ||
            !read_step("game_manager+0xA8", inventory_root + kInventoryFirstHopOffset, inventory_owner) ||
            !read_step("owner+0x10", inventory_owner + kInventoryHopOffset, inventory_node_a) ||
            !read_step("node_a+0x10", inventory_node_a + kInventoryHopOffset, inventory_node_b) ||
            !read_step("node_b+0x10", inventory_node_b + kInventoryHopOffset, inventory_object) ||
            !read_step("inventory.vtable", inventory_object, inventory_vtable) ||
            !read_step(
                "vtable+0x30",
                inventory_vtable + kInventoryAdjustQuantityVtableSlotOffset,
                adjust_quantity_target) ||
            !read_step(
                "vtable+0x38",
                inventory_vtable + kInventoryUseItemVtableSlotOffset,
                use_item_target) ||
            !read_step(
                "vtable+0x70",
                inventory_vtable + kInventorySelectedItemEntryVtableSlotOffset,
                selected_item_target))
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
        payload["use_item_slot"] =
            HexPointer(inventory_vtable + kInventoryUseItemVtableSlotOffset);
        payload["use_item_target"] = HexPointer(use_item_target);
        payload["selected_item_slot"] =
            HexPointer(inventory_vtable + kInventorySelectedItemEntryVtableSlotOffset);
        payload["selected_item_target"] = HexPointer(selected_item_target);
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

    std::filesystem::path RuntimeSiblingPath(
        const RuntimeWorkerConfig& config,
        const char* suffix)
    {
        std::filesystem::path event_log = config.EventLog;
        if (event_log.empty())
        {
            return std::filesystem::current_path() /
                "DS2NativeRuntime" /
                (std::string("ds2_native") + suffix);
        }

        std::string filename = event_log.filename().string();
        const std::string events_suffix = ".events.jsonl";
        if (filename.size() >= events_suffix.size() &&
            filename.compare(
                filename.size() - events_suffix.size(),
                events_suffix.size(),
                events_suffix) == 0)
        {
            filename =
                filename.substr(0, filename.size() - events_suffix.size()) +
                suffix;
        }
        else
        {
            filename += suffix;
        }

        return event_log.parent_path() / filename;
    }

    void AppendRuntimeAction(
        const RuntimeWorkerConfig& config,
        const RuntimeGrantItem& item,
        const char* command,
        const nlohmann::json& data)
    {
        std::lock_guard<std::mutex> guard(s_event_log_mutex);
        const std::filesystem::path action_log =
            RuntimeSiblingPath(config, ".actions.jsonl");
        EnsureParentDirectory(action_log);

        nlohmann::json action;
        action["time_utc"] = UtcNowIso8601();
        action["runtime"] = "ds2_native";
        action["session_id"] = config.SessionId;
        action["source"] = "custom_item";
        action["item_id"] = item.ItemId;
        action["item_id_hex"] = HexPointer(static_cast<uint32_t>(item.ItemId));
        action["runtime_name"] = item.RuntimeName;
        action["command"] = command;
        action["data"] = data;

        std::ofstream stream(action_log, std::ios::out | std::ios::app | std::ios::binary);
        if (!stream)
        {
            return;
        }

        stream << action.dump() << "\n";
    }

    void AppendRuntimeMessage(
        const RuntimeWorkerConfig& config,
        const RuntimeGrantItem& item,
        const char* command,
        const nlohmann::json& data)
    {
        std::lock_guard<std::mutex> guard(s_event_log_mutex);
        const std::filesystem::path message_log =
            RuntimeSiblingPath(config, ".messages.jsonl");
        EnsureParentDirectory(message_log);

        nlohmann::json message;
        message["time_utc"] = UtcNowIso8601();
        message["runtime"] = "ds2_native";
        message["session_id"] = config.SessionId;
        message["source"] = "custom_item";
        message["item_id"] = item.ItemId;
        message["item_id_hex"] = HexPointer(static_cast<uint32_t>(item.ItemId));
        message["runtime_name"] = item.RuntimeName;
        message["command"] = command;
        message["message_key"] = data.value("message_key", std::string());
        message["message_en"] = data.value("message_en", std::string());
        message["message_es"] = data.value("message_es", std::string());
        message["behavior_phase"] =
            data.value("behavior_phase", std::string());
        message["online_intent"] =
            data.value("online_intent", std::string());

        std::ofstream stream(
            message_log,
            std::ios::out | std::ios::app | std::ios::binary);
        if (!stream)
        {
            return;
        }

        stream << message.dump() << "\n";
    }

    const char* RuntimeRulePresetName(int32_t index)
    {
        switch (index)
        {
        case 0:
            return "balanced";
        case 1:
            return "open_coop";
        case 2:
            return "challenge";
        default:
            return "balanced";
        }
    }

    nlohmann::json RuntimeRulePayload(int32_t index)
    {
        nlohmann::json rules;
        rules["preset_index"] = index;
        rules["preset_name"] = RuntimeRulePresetName(index);
        rules["friendly_fire"] = index == 2;
        rules["invasions_enabled"] = index == 2;
        rules["boss_barriers_removed"] = index != 0;
        rules["shared_progression_target"] = true;
        rules["enemy_scaling"] = index == 2 ? "challenge" : "standard";
        return rules;
    }

    nlohmann::json BuildRuntimeStatePayloadNoLock(
        const RuntimeWorkerConfig& config)
    {
        nlohmann::json state;
        state["session_open"] = s_session_open;
        state["session_mode"] = s_session_mode;
        state["last_session_request"] = s_last_session_request;
        state["last_runtime_command"] = s_last_runtime_command;
        state["last_runtime_item_id"] = s_last_runtime_action_item_id;
        state["last_runtime_item_id_hex"] =
            HexPointer(static_cast<uint32_t>(s_last_runtime_action_item_id));
        state["last_runtime_message_key"] = s_last_runtime_message_key;
        state["last_runtime_message_en"] = s_last_runtime_message_en;
        state["last_runtime_message_es"] = s_last_runtime_message_es;
        state["runtime_stage"] = s_runtime_stage;
        state["online_intent"] = s_online_intent;
        state["rule_preset_index"] = s_rule_preset_index;
        state["rule_preset"] = RuntimeRulePresetName(s_rule_preset_index);
        state["rules"] = RuntimeRulePayload(s_rule_preset_index);
        state["recovery_request_count"] = s_recovery_request_count;
        state["invasion_request_count"] = s_invasion_request_count;
        state["taunt_request_count"] = s_taunt_request_count;
        state["infection_request_count"] = s_infection_request_count;
        state["curse_sigil_count"] = s_curse_sigil_count;
        state["runtime_action_sequence"] = s_runtime_action_sequence;
        state["pending_vanilla_suppression_item_id"] =
            s_pending_runtime_suppression_item_id;
        state["pending_vanilla_suppression_sequence"] =
            s_pending_runtime_suppression_sequence;
        state["private_online_contract"] = {
            {"owner", "bonfire_ds2_native_runtime"},
            {"item_behavior", "custom"},
            {"native_shell_use_only", true},
            {"vanilla_continuation", "suppressed_after_custom_action"},
        };
        state["actions_log"] =
            RuntimeSiblingPath(config, ".actions.jsonl").string();
        state["messages_log"] =
            RuntimeSiblingPath(config, ".messages.jsonl").string();
        state["state_file"] =
            RuntimeSiblingPath(config, ".state.json").string();
        return state;
    }

    void WriteRuntimeStateSnapshot(
        const RuntimeWorkerConfig& config,
        const nlohmann::json& state)
    {
        const std::filesystem::path state_path =
            RuntimeSiblingPath(config, ".state.json");
        EnsureParentDirectory(state_path);

        std::ofstream stream(
            state_path,
            std::ios::out | std::ios::trunc | std::ios::binary);
        if (!stream)
        {
            return;
        }

        stream << state.dump(2) << "\n";
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
            if (!item.GrantAtBonfire)
            {
                continue;
            }

            const int32_t held_quantity = found[runtime_index] ? quantities[runtime_index] : 0;
            const int32_t missing_quantity =
                std::max<int32_t>(0, static_cast<int32_t>(item.Quantity) - held_quantity);

            nlohmann::json item_payload;
            item_payload["runtime_name"] = item.RuntimeName;
            item_payload["item_id"] = item.ItemId;
            item_payload["item_id_hex"] = HexPointer(static_cast<uint32_t>(item.ItemId));
            item_payload["grant_at_bonfire"] = item.GrantAtBonfire;
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

    void HandleBonfireRuntimeItemUse(
        const RuntimeWorkerConfig& config,
        const RuntimeGrantItem& item,
        int32_t amount,
        int32_t forwarded_amount,
        const char* source = nullptr,
        int32_t item_slot = -1,
        int32_t action_arg = 0)
    {
        const std::string command =
            item.ActionCommand != nullptr ? item.ActionCommand : "";
        const RuntimeBehaviorDescriptor behavior =
            RuntimeBehaviorForCommand(command);
        nlohmann::json payload;
        payload["item_id"] = item.ItemId;
        payload["item_id_hex"] = HexPointer(static_cast<uint32_t>(item.ItemId));
        payload["runtime_name"] = item.RuntimeName;
        payload["command"] = command;
        payload["label"] = item.ActionLabel;
        payload["action_category"] = item.ActionCategory;
        payload["grant_at_bonfire"] = item.GrantAtBonfire;
        payload["suppress_vanilla_continuation"] =
            item.SuppressVanillaContinuation;
        payload["amount"] = amount;
        payload["forwarded_amount"] = forwarded_amount;
        payload["consumption_suppressed"] = amount != forwarded_amount;
        AddRuntimeBehaviorPayload(payload, behavior);
        if (source != nullptr)
        {
            payload["source"] = source;
        }
        if (item_slot >= 0)
        {
            payload["item_slot"] = item_slot;
            payload["action_arg"] = action_arg;
        }

        const uint64_t now_tick = GetTickCount64();
        bool suppressed_duplicate = false;
        {
            std::lock_guard<std::mutex> guard(s_runtime_state_mutex);
            const std::string command =
                item.ActionCommand != nullptr ? item.ActionCommand : "";
            suppressed_duplicate =
                s_last_runtime_action_item_id == item.ItemId &&
                now_tick >= s_last_runtime_action_tick &&
                now_tick - s_last_runtime_action_tick < kRuntimeItemActionDebounceMs;

            if (!suppressed_duplicate)
            {
                s_last_runtime_action_item_id = item.ItemId;
                s_last_runtime_action_tick = now_tick;
                s_pending_runtime_suppression_item_id = item.ItemId;
                s_pending_runtime_suppression_tick = now_tick;
                payload["action_sequence"] = ++s_runtime_action_sequence;
                s_pending_runtime_suppression_sequence =
                    s_runtime_action_sequence;
                s_last_runtime_command = command;
                s_last_runtime_message_key = behavior.MessageKey;
                s_last_runtime_message_en = behavior.MessageEnglish;
                s_last_runtime_message_es = behavior.MessageSpanish;
                s_runtime_stage = behavior.BehaviorPhase;
                s_online_intent = behavior.OnlineIntent;
                payload["pending_vanilla_suppression_ms"] =
                    kRuntimeItemVanillaSuppressWindowMs;

                if (command == "session.leave")
                {
                    s_session_open = false;
                    s_session_mode = "solo";
                    s_last_session_request = "leave";
                    payload["action"] = "session_leave_requested";
                }
                else if (command == "session.create")
                {
                    s_session_open = true;
                    s_session_mode = "host";
                    s_last_session_request = "create";
                    payload["action"] = "session_create_requested";
                }
                else if (command == "session.join")
                {
                    s_session_open = true;
                    s_session_mode = "guest";
                    s_last_session_request = "join";
                    payload["action"] = "session_join_requested";
                }
                else if (command == "session.invade")
                {
                    s_session_open = true;
                    s_session_mode = "invader";
                    s_last_session_request = "invade";
                    ++s_invasion_request_count;
                    payload["action"] = "session_invade_requested";
                    payload["invasion_request_count"] =
                        s_invasion_request_count;
                }
                else if (command == "rules.cycle")
                {
                    s_rule_preset_index = (s_rule_preset_index + 1) % 3;
                    payload["action"] = "rules_cycled";
                    payload["rule_preset"] =
                        RuntimeRulePresetName(s_rule_preset_index);
                    payload["rules"] = RuntimeRulePayload(s_rule_preset_index);
                }
                else if (command == "invasions.taunt")
                {
                    ++s_taunt_request_count;
                    payload["action"] = "invasions_taunt_requested";
                    payload["taunt_request_count"] = s_taunt_request_count;
                }
                else if (command == "world.infection")
                {
                    ++s_infection_request_count;
                    payload["action"] = "world_infection_requested";
                    payload["infection_request_count"] =
                        s_infection_request_count;
                }
                else if (command == "curse.accrue")
                {
                    ++s_curse_sigil_count;
                    payload["action"] = "curse_sigil_accrued";
                    payload["curse_sigil_count"] = s_curse_sigil_count;
                }
                else if (command == "world.recover")
                {
                    ++s_recovery_request_count;
                    payload["action"] = "world_recovery_requested";
                    payload["recovery_request_count"] =
                        s_recovery_request_count;
                }
                else
                {
                    payload["action"] = "unknown_runtime_item";
                }

                nlohmann::json state =
                    BuildRuntimeStatePayloadNoLock(config);
                payload["state"] = state;
                WriteRuntimeStateSnapshot(config, state);
            }
        }

        if (suppressed_duplicate)
        {
            payload["suppressed"] = true;
            payload["reason"] = "duplicate item-use event inside debounce window";
            AppendRuntimeEvent(config, "bonfire.custom_item_action_suppressed", payload);
            return;
        }

        AppendRuntimeAction(config, item, command.c_str(), payload);
        AppendRuntimeMessage(config, item, command.c_str(), payload);
        AppendRuntimeEvent(config, "bonfire.custom_item_action", payload);

        if (command == "world.recover")
        {
            EmitInventoryProbeAndMaybeArm(config);
            GrantBonfireRuntimeItems(
                config,
                nullptr,
                -1,
                "liberation_scroll_recovery");
        }
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

    bool ResolveInventoryObserverSlots(
        const RuntimeWorkerConfig& config,
        uintptr_t& inventory_object,
        uintptr_t& inventory_vtable,
        uintptr_t& adjust_quantity_slot,
        uintptr_t& adjust_quantity_target,
        uintptr_t& use_item_slot,
        uintptr_t& use_item_target,
        uintptr_t& selected_item_slot,
        uintptr_t& selected_item_target)
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
        use_item_slot =
            inventory_vtable + kInventoryUseItemVtableSlotOffset;
        selected_item_slot =
            inventory_vtable + kInventorySelectedItemEntryVtableSlotOffset;
        return TryReadPointer(adjust_quantity_slot, adjust_quantity_target) &&
            TryReadPointer(use_item_slot, use_item_target) &&
            TryReadPointer(selected_item_slot, selected_item_target);
    }

    void LogInventoryAdjustQuantity(
        void* inventory,
        int32_t item_id,
        int32_t amount,
        int32_t forwarded_amount,
        int32_t unk3,
        int32_t unk4,
        const RuntimeGrantItem* resolved_runtime_item = nullptr,
        const RuntimeSelectionMatch* resolved_selection_match = nullptr)
    {
        const LONG event_index = InterlockedIncrement(&s_inventory_adjust_event_count);
        const RuntimeGrantItem* runtime_item = resolved_runtime_item;
        if (runtime_item == nullptr)
        {
            runtime_item = FindBonfireRuntimeItem(item_id);
        }
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
        payload["resolved_from_selected_placeholder"] =
            resolved_selection_match != nullptr &&
            resolved_selection_match->Matched;
        if (runtime_item != nullptr)
        {
            payload["runtime_item_id"] = runtime_item->ItemId;
            payload["runtime_item_id_hex"] =
                HexPointer(static_cast<uint32_t>(runtime_item->ItemId));
            payload["native_use_item_id"] = runtime_item->NativeUseItemId;
            payload["native_use_item_id_hex"] =
                HexPointer(static_cast<uint32_t>(runtime_item->NativeUseItemId));
            payload["runtime_name"] = runtime_item->RuntimeName;
            payload["runtime_consumption_suppressed"] = amount != forwarded_amount;
        }
        if (resolved_selection_match != nullptr &&
            resolved_selection_match->Matched)
        {
            payload["selected_entry"] =
                HexPointer(resolved_selection_match->EntryAddress);
            payload["selected_age_ms"] = resolved_selection_match->AgeMs;
        }
        payload["observer_state"] = "armed";
        payload["original_target"] =
            HexPointer(reinterpret_cast<uintptr_t>(s_original_inventory_adjust_quantity));

        AppendRuntimeEvent(config, "inventory.adjust_quantity", payload);

        if (runtime_item != nullptr && amount != 0)
        {
            AppendRuntimeEvent(config, "bonfire.custom_item_use", payload);
            HandleBonfireRuntimeItemUse(
                config,
                *runtime_item,
                amount,
                forwarded_amount,
                "inventory_adjust_quantity");
        }
    }

    void* ResolveInventoryItemEntry(void* inventory, int32_t item_slot)
    {
        if (inventory == nullptr)
        {
            return nullptr;
        }

        uintptr_t inventory_vtable = 0;
        uintptr_t get_entry_target = 0;
        if (!TryReadPointer(reinterpret_cast<uintptr_t>(inventory), inventory_vtable) ||
            !TryReadPointer(
                inventory_vtable + kInventoryGetItemEntryVtableSlotOffset,
                get_entry_target))
        {
            return nullptr;
        }

        auto get_entry =
            reinterpret_cast<InventoryGetItemEntryFn>(get_entry_target);
        void* entry = nullptr;
        __try
        {
            entry = get_entry(inventory, item_slot);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            entry = nullptr;
        }

        return entry;
    }

    int32_t ReadInventoryItemEntryId(void* entry)
    {
        int32_t item_id = 0;
        TryReadValue(
            reinterpret_cast<uintptr_t>(entry) + kInventoryItemEntryItemIdOffset,
            item_id);
        return item_id;
    }

    int32_t ReadInventoryItemEntryNativeUseId(void* entry)
    {
        int32_t item_id = 0;
        TryReadValue(
            reinterpret_cast<uintptr_t>(entry) + kInventoryItemEntryNativeUseItemIdOffset,
            item_id);
        return item_id;
    }

    const RuntimeGrantItem* ResolveSelectedRuntimeItemFromNativeUse(
        int32_t native_use_item_id,
        RuntimeSelectionMatch& match)
    {
        const uint64_t now_tick = GetTickCount64();
        int32_t selected_runtime_item_id = 0;
        int32_t selected_native_use_item_id = 0;
        uintptr_t selected_entry_address = 0;
        uint64_t selected_tick = 0;

        {
            std::lock_guard<std::mutex> guard(s_inventory_selection_mutex);
            selected_runtime_item_id = s_last_selected_runtime_item_id;
            selected_native_use_item_id = s_last_selected_native_use_item_id;
            selected_entry_address = s_last_selected_entry_address;
            selected_tick = s_last_selected_tick;
        }

        if (selected_runtime_item_id == 0 ||
            selected_native_use_item_id != native_use_item_id ||
            selected_tick == 0 ||
            now_tick < selected_tick ||
            now_tick - selected_tick > kRuntimeItemSelectionWindowMs)
        {
            return nullptr;
        }

        const RuntimeGrantItem* runtime_item =
            FindBonfireRuntimeItem(selected_runtime_item_id);
        if (runtime_item == nullptr ||
            runtime_item->NativeUseItemId != native_use_item_id)
        {
            return nullptr;
        }

        match.Matched = true;
        match.RuntimeItemId = selected_runtime_item_id;
        match.NativeUseItemId = native_use_item_id;
        match.EntryAddress = selected_entry_address;
        match.AgeMs = now_tick - selected_tick;
        return runtime_item;
    }

    const RuntimeGrantItem* ResolveLastSelectedRuntimeItem(
        RuntimeSelectionMatch& match)
    {
        const uint64_t now_tick = GetTickCount64();
        int32_t selected_runtime_item_id = 0;
        int32_t selected_native_use_item_id = 0;
        uintptr_t selected_entry_address = 0;
        uint64_t selected_tick = 0;

        {
            std::lock_guard<std::mutex> guard(s_inventory_selection_mutex);
            selected_runtime_item_id = s_last_selected_runtime_item_id;
            selected_native_use_item_id = s_last_selected_native_use_item_id;
            selected_entry_address = s_last_selected_entry_address;
            selected_tick = s_last_selected_tick;
        }

        if (selected_runtime_item_id == 0 ||
            selected_tick == 0 ||
            now_tick < selected_tick ||
            now_tick - selected_tick > kRuntimeItemSelectionWindowMs)
        {
            return nullptr;
        }

        const RuntimeGrantItem* runtime_item =
            FindBonfireRuntimeItem(selected_runtime_item_id);
        if (runtime_item == nullptr ||
            runtime_item->NativeUseItemId != selected_native_use_item_id)
        {
            return nullptr;
        }

        match.Matched = true;
        match.RuntimeItemId = selected_runtime_item_id;
        match.NativeUseItemId = selected_native_use_item_id;
        match.EntryAddress = selected_entry_address;
        match.AgeMs = now_tick - selected_tick;
        return runtime_item;
    }

    bool IsInventorySelectedEntryCaller(
        const RuntimeWorkerConfig& config,
        uintptr_t return_address,
        uintptr_t& return_rva)
    {
        return_rva = 0;
        if (config.GameBaseAddress == 0 ||
            return_address < config.GameBaseAddress)
        {
            return false;
        }

        return_rva = return_address - config.GameBaseAddress;
        if (return_rva == kInventoryEntryEnumerationReturnRva)
        {
            return false;
        }

        return return_rva == kInventorySelectedEntryReturnRvaA ||
            return_rva == kInventorySelectedEntryReturnRvaB ||
            return_rva == kInventorySelectedEntryReturnRvaC;
    }

    void RememberSelectedRuntimeItem(
        const RuntimeWorkerConfig& config,
        void* inventory,
        void* entry,
        const RuntimeGrantItem& item,
        int32_t native_use_item_id,
        uintptr_t return_rva)
    {
        const uintptr_t entry_address = reinterpret_cast<uintptr_t>(entry);
        const uint64_t now_tick = GetTickCount64();
        bool changed = false;

        {
            std::lock_guard<std::mutex> guard(s_inventory_selection_mutex);
            changed =
                s_last_selected_runtime_item_id != item.ItemId ||
                s_last_selected_native_use_item_id != native_use_item_id ||
                s_last_selected_entry_address != entry_address;
            s_last_selected_runtime_item_id = item.ItemId;
            s_last_selected_native_use_item_id = native_use_item_id;
            s_last_selected_entry_address = entry_address;
            s_last_selected_tick = now_tick;
        }

        if (!changed)
        {
            return;
        }

        const LONG event_index =
            InterlockedIncrement(&s_inventory_selected_event_count);
        nlohmann::json payload;
        payload["event_index"] = event_index;
        payload["inventory"] = HexPointer(reinterpret_cast<uintptr_t>(inventory));
        payload["entry"] = HexPointer(entry_address);
        payload["item_id"] = item.ItemId;
        payload["item_id_hex"] = HexPointer(static_cast<uint32_t>(item.ItemId));
        payload["native_use_item_id"] = native_use_item_id;
        payload["native_use_item_id_hex"] =
            HexPointer(static_cast<uint32_t>(native_use_item_id));
        payload["runtime_name"] = item.RuntimeName;
        payload["source"] = "inventory_selected_entry_vtable_0x70";
        payload["return_rva"] = HexPointer(return_rva);
        payload["observer_state"] = "armed";
        payload["original_target"] = HexPointer(
            reinterpret_cast<uintptr_t>(s_original_inventory_selected_item_entry));
        AppendRuntimeEvent(config, "inventory.selected_runtime_item", payload);
    }

    void LogInventoryUseItem(
        void* inventory,
        int32_t item_slot,
        int32_t action_arg,
        void* entry,
        int32_t item_id)
    {
        const LONG event_index = InterlockedIncrement(&s_inventory_use_event_count);
        RuntimeSelectionMatch selection_match;
        const RuntimeGrantItem* runtime_item = FindBonfireRuntimeItem(item_id);
        if (runtime_item == nullptr)
        {
            runtime_item =
                ResolveSelectedRuntimeItemFromNativeUse(item_id, selection_match);
        }
        if (event_index > 256 && runtime_item == nullptr)
        {
            return;
        }

        RuntimeWorkerConfig config =
            GetActiveRuntimeConfig("inventory_use.events.jsonl");

        uint16_t quantity = 0;
        uint8_t flags = 0;
        if (entry != nullptr)
        {
            TryReadValue(
                reinterpret_cast<uintptr_t>(entry) + kInventoryItemEntryQuantityOffset,
                quantity);
            TryReadValue(
                reinterpret_cast<uintptr_t>(entry) + kInventoryItemEntryFlagsOffset,
                flags);
        }

        nlohmann::json payload;
        payload["event_index"] = event_index;
        payload["inventory"] = HexPointer(reinterpret_cast<uintptr_t>(inventory));
        payload["item_slot"] = item_slot;
        payload["action_arg"] = action_arg;
        payload["entry"] = HexPointer(reinterpret_cast<uintptr_t>(entry));
        payload["item_id"] = item_id;
        payload["item_id_hex"] = HexPointer(static_cast<uint32_t>(item_id));
        payload["entry_quantity"] = quantity;
        payload["entry_flags"] = flags;
        payload["bonfire_runtime_item"] = runtime_item != nullptr;
        payload["resolved_from_selected_placeholder"] = selection_match.Matched;
        payload["observer_state"] = "armed";
        payload["source"] = "inventory_use_vtable_0x38";
        payload["original_target"] =
            HexPointer(reinterpret_cast<uintptr_t>(s_original_inventory_use_item));
        if (runtime_item != nullptr)
        {
            payload["runtime_item_id"] = runtime_item->ItemId;
            payload["runtime_item_id_hex"] =
                HexPointer(static_cast<uint32_t>(runtime_item->ItemId));
            payload["native_use_item_id"] = runtime_item->NativeUseItemId;
            payload["native_use_item_id_hex"] =
                HexPointer(static_cast<uint32_t>(runtime_item->NativeUseItemId));
            payload["runtime_name"] = runtime_item->RuntimeName;
        }
        if (selection_match.Matched)
        {
            payload["selected_entry"] =
                HexPointer(selection_match.EntryAddress);
            payload["selected_age_ms"] = selection_match.AgeMs;
        }

        AppendRuntimeEvent(config, "inventory.use_item_candidate", payload);

        if (runtime_item != nullptr)
        {
            AppendRuntimeEvent(config, "bonfire.custom_item_use", payload);
            HandleBonfireRuntimeItemUse(
                config,
                *runtime_item,
                1,
                1,
                "inventory_use_vtable_0x38",
                item_slot,
                action_arg);
        }
    }

    int64_t __fastcall InventoryAdjustQuantityHook(
        void* inventory,
        int32_t item_id,
        int32_t amount,
        int32_t unk3,
        int32_t unk4)
    {
        RuntimeSelectionMatch selection_match;
        const RuntimeGrantItem* runtime_item = FindBonfireRuntimeItem(item_id);
        if (runtime_item == nullptr)
        {
            runtime_item =
                ResolveSelectedRuntimeItemFromNativeUse(item_id, selection_match);
        }
        const int32_t forwarded_amount =
            runtime_item != nullptr && amount != 0 ? 0 : amount;
        LogInventoryAdjustQuantity(
            inventory,
            item_id,
            amount,
            forwarded_amount,
            unk3,
            unk4,
            runtime_item,
            &selection_match);

        InventoryAdjustQuantityFn original = s_original_inventory_adjust_quantity;
        if (original == nullptr)
        {
            return 0;
        }

        return original(inventory, item_id, forwarded_amount, unk3, unk4);
    }

    int64_t __fastcall InventoryUseItemHook(
        void* inventory,
        int32_t item_slot,
        int32_t action_arg)
    {
        void* entry = ResolveInventoryItemEntry(inventory, item_slot);
        const int32_t item_id = ReadInventoryItemEntryId(entry);
        LogInventoryUseItem(
            inventory,
            item_slot,
            action_arg,
            entry,
            item_id);

        InventoryUseItemFn original = s_original_inventory_use_item;
        if (original == nullptr)
        {
            return 0;
        }

        return original(inventory, item_slot, action_arg);
    }

    void* __fastcall InventorySelectedItemEntryHook(void* inventory)
    {
        InventorySelectedItemEntryFn original =
            s_original_inventory_selected_item_entry;
        void* entry = nullptr;
        if (original != nullptr)
        {
            entry = original(inventory);
        }

        if (entry == nullptr)
        {
            return entry;
        }

        const int32_t item_id = ReadInventoryItemEntryId(entry);
        const RuntimeGrantItem* runtime_item = FindBonfireRuntimeItem(item_id);
        if (runtime_item == nullptr)
        {
            return entry;
        }

        const RuntimeWorkerConfig config =
            GetActiveRuntimeConfig("inventory_selected.events.jsonl");
        uintptr_t return_rva = 0;
        const uintptr_t return_address =
            reinterpret_cast<uintptr_t>(_ReturnAddress());
        if (!IsInventorySelectedEntryCaller(config, return_address, return_rva))
        {
            return entry;
        }

        const int32_t native_use_item_id =
            ReadInventoryItemEntryNativeUseId(entry);
        if (native_use_item_id != runtime_item->NativeUseItemId)
        {
            return entry;
        }

        RememberSelectedRuntimeItem(
            config,
            inventory,
            entry,
            *runtime_item,
            native_use_item_id,
            return_rva);
        return entry;
    }

    int32_t __fastcall InventorySelectedItemCategoryHook(void* inventory)
    {
        InventorySelectedItemCategoryFn original =
            s_original_inventory_selected_item_category;
        int32_t result = -1;
        if (original != nullptr)
        {
            result = original(inventory);
        }

        RuntimeWorkerConfig config =
            GetActiveRuntimeConfig("inventory_selected_action.events.jsonl");
        uintptr_t return_rva = 0;
        const uintptr_t return_address =
            reinterpret_cast<uintptr_t>(_ReturnAddress());
        if (config.GameBaseAddress != 0 &&
            return_address >= config.GameBaseAddress)
        {
            return_rva = return_address - config.GameBaseAddress;
        }

        RuntimeSelectionMatch selection_match;
        const RuntimeGrantItem* runtime_item =
            ResolveLastSelectedRuntimeItem(selection_match);
        const bool action_candidate =
            return_rva == kInventorySelectedActionCandidateReturnRva;

        const LONG event_index =
            InterlockedIncrement(&s_inventory_selected_action_event_count);
        if (event_index <= 256 || runtime_item != nullptr || action_candidate)
        {
            nlohmann::json payload;
            payload["event_index"] = event_index;
            payload["inventory"] =
                HexPointer(reinterpret_cast<uintptr_t>(inventory));
            payload["source"] = "inventory_selected_category_0x1B19D0";
            payload["category_result"] = result;
            payload["return_rva"] = HexPointer(return_rva);
            payload["action_candidate_caller"] = action_candidate;
            payload["bonfire_runtime_item"] = runtime_item != nullptr;
            payload["resolved_from_last_selection"] =
                selection_match.Matched;
            payload["observer_state"] = "armed";
            payload["original_target"] = HexPointer(
                reinterpret_cast<uintptr_t>(
                    s_original_inventory_selected_item_category));
            if (runtime_item != nullptr)
            {
                payload["runtime_item_id"] = runtime_item->ItemId;
                payload["runtime_item_id_hex"] =
                    HexPointer(static_cast<uint32_t>(runtime_item->ItemId));
                payload["native_use_item_id"] =
                    runtime_item->NativeUseItemId;
                payload["native_use_item_id_hex"] =
                    HexPointer(
                        static_cast<uint32_t>(
                            runtime_item->NativeUseItemId));
                payload["runtime_name"] = runtime_item->RuntimeName;
                payload["command"] = runtime_item->ActionCommand;
            }
            if (selection_match.Matched)
            {
                payload["selected_entry"] =
                    HexPointer(selection_match.EntryAddress);
                payload["selected_age_ms"] = selection_match.AgeMs;
            }

            AppendRuntimeEvent(
                config,
                "inventory.selected_action_candidate",
                payload);

            if (runtime_item != nullptr &&
                action_candidate &&
                result >= 0)
            {
                AppendRuntimeEvent(
                    config,
                    "bonfire.custom_item_use",
                    payload);
                HandleBonfireRuntimeItemUse(
                    config,
                    *runtime_item,
                    1,
                    1,
                    "inventory_selected_category_0x1B19D0",
                    -1,
                    result);
            }
        }

        return result;
    }

    void __fastcall InventorySelectedActionExecuteHook(
        void* action_manager,
        int32_t selected_category,
        int32_t action_flag)
    {
        RuntimeWorkerConfig config =
            GetActiveRuntimeConfig("inventory_selected_action_execute.events.jsonl");
        uintptr_t return_rva = 0;
        const uintptr_t return_address =
            reinterpret_cast<uintptr_t>(_ReturnAddress());
        if (config.GameBaseAddress != 0 &&
            return_address >= config.GameBaseAddress)
        {
            return_rva = return_address - config.GameBaseAddress;
        }

        RuntimeSelectionMatch selection_match;
        const RuntimeGrantItem* runtime_item =
            ResolveLastSelectedRuntimeItem(selection_match);
        const bool action_execute_caller =
            return_rva == kInventorySelectedActionExecuteReturnRva;
        const bool category_matches =
            runtime_item != nullptr &&
            selected_category == runtime_item->ActionCategory;
        const uint64_t now_tick = GetTickCount64();
        bool pending_bonfire_action = false;
        uint64_t pending_bonfire_action_age_ms = 0;
        uint64_t pending_bonfire_action_sequence = 0;
        if (runtime_item != nullptr)
        {
            std::lock_guard<std::mutex> guard(s_runtime_state_mutex);
            pending_bonfire_action =
                s_pending_runtime_suppression_item_id == runtime_item->ItemId &&
                now_tick >= s_pending_runtime_suppression_tick &&
                now_tick - s_pending_runtime_suppression_tick <
                    kRuntimeItemVanillaSuppressWindowMs;
            if (pending_bonfire_action)
            {
                pending_bonfire_action_age_ms =
                    now_tick - s_pending_runtime_suppression_tick;
                pending_bonfire_action_sequence =
                    s_pending_runtime_suppression_sequence;
            }
        }
        const bool suppress_vanilla =
            action_execute_caller &&
            runtime_item != nullptr &&
            (category_matches || pending_bonfire_action) &&
            runtime_item->SuppressVanillaContinuation;

        const LONG event_index =
            InterlockedIncrement(
                &s_inventory_selected_action_execute_event_count);
        if (event_index <= 256 ||
            runtime_item != nullptr ||
            action_execute_caller)
        {
            nlohmann::json payload;
            payload["event_index"] = event_index;
            payload["action_manager"] =
                HexPointer(reinterpret_cast<uintptr_t>(action_manager));
            payload["source"] = "inventory_selected_action_execute_0x500C40";
            payload["selected_category"] = selected_category;
            payload["action_flag"] = action_flag;
            payload["return_rva"] = HexPointer(return_rva);
            payload["action_execute_caller"] = action_execute_caller;
            payload["bonfire_runtime_item"] = runtime_item != nullptr;
            payload["resolved_from_last_selection"] =
                selection_match.Matched;
            payload["category_matches"] = category_matches;
            payload["pending_bonfire_action"] = pending_bonfire_action;
            payload["pending_bonfire_action_age_ms"] =
                pending_bonfire_action_age_ms;
            payload["pending_bonfire_action_sequence"] =
                pending_bonfire_action_sequence;
            payload["vanilla_continuation_suppressed"] =
                suppress_vanilla;
            payload["observer_state"] = "armed";
            payload["original_target"] = HexPointer(
                reinterpret_cast<uintptr_t>(
                    s_original_inventory_selected_action_execute));
            if (runtime_item != nullptr)
            {
                payload["runtime_item_id"] = runtime_item->ItemId;
                payload["runtime_item_id_hex"] =
                    HexPointer(static_cast<uint32_t>(runtime_item->ItemId));
                payload["native_use_item_id"] =
                    runtime_item->NativeUseItemId;
                payload["native_use_item_id_hex"] =
                    HexPointer(
                        static_cast<uint32_t>(
                            runtime_item->NativeUseItemId));
                payload["runtime_name"] = runtime_item->RuntimeName;
                payload["command"] = runtime_item->ActionCommand;
                payload["expected_category"] =
                    runtime_item->ActionCategory;
                payload["suppress_vanilla_continuation"] =
                    runtime_item->SuppressVanillaContinuation;
            }
            if (selection_match.Matched)
            {
                payload["selected_entry"] =
                    HexPointer(selection_match.EntryAddress);
                payload["selected_age_ms"] = selection_match.AgeMs;
            }

            AppendRuntimeEvent(
                config,
                suppress_vanilla ?
                    "inventory.selected_action_execute_suppressed" :
                    "inventory.selected_action_execute",
                payload);
        }

        if (suppress_vanilla)
        {
            return;
        }

        InventorySelectedActionExecuteFn original =
            s_original_inventory_selected_action_execute;
        if (original != nullptr)
        {
            original(action_manager, selected_category, action_flag);
        }
    }

    void LogItemUseValidation(
        int32_t item_id,
        bool native_validation_result,
        bool forwarded_validation_result)
    {
        const LONG event_index =
            InterlockedIncrement(&s_item_use_validation_event_count);
        RuntimeSelectionMatch selection_match;
        const RuntimeGrantItem* runtime_item = FindBonfireRuntimeItem(item_id);
        if (runtime_item == nullptr)
        {
            runtime_item =
                ResolveSelectedRuntimeItemFromNativeUse(item_id, selection_match);
        }
        const RuntimeGrantItem* placeholder_runtime_item =
            FindBonfireRuntimeItemByNativeUseItem(item_id);
        const bool known_online_item =
            item_id == 62050000 ||
            item_id == 62060000 ||
            placeholder_runtime_item != nullptr ||
            runtime_item != nullptr;
        if (event_index > 256 && !known_online_item)
        {
            return;
        }

        RuntimeWorkerConfig config =
            GetActiveRuntimeConfig("item_use_validation.events.jsonl");

        nlohmann::json payload;
        payload["event_index"] = event_index;
        payload["source"] = "item_use_validation_0x2D3B20";
        payload["validation_rva"] = HexPointer(kEyeOrbUseValidationRva);
        payload["item_id"] = item_id;
        payload["item_id_hex"] = HexPointer(static_cast<uint32_t>(item_id));
        payload["native_validation_result"] = native_validation_result;
        payload["validation_result"] = forwarded_validation_result;
        payload["bonfire_validation_override"] =
            forwarded_validation_result != native_validation_result;
        payload["known_online_item"] = known_online_item;
        payload["bonfire_runtime_item"] = runtime_item != nullptr;
        payload["placeholder_belongs_to_runtime_item"] =
            placeholder_runtime_item != nullptr;
        payload["resolved_from_selected_placeholder"] = selection_match.Matched;
        payload["observer_state"] = "armed";
        payload["original_target"] =
            HexPointer(reinterpret_cast<uintptr_t>(s_original_item_use_validation));
        if (runtime_item != nullptr)
        {
            payload["runtime_item_id"] = runtime_item->ItemId;
            payload["runtime_item_id_hex"] =
                HexPointer(static_cast<uint32_t>(runtime_item->ItemId));
            payload["native_use_item_id"] = runtime_item->NativeUseItemId;
            payload["native_use_item_id_hex"] =
                HexPointer(static_cast<uint32_t>(runtime_item->NativeUseItemId));
            payload["runtime_name"] = runtime_item->RuntimeName;
            payload["command"] = runtime_item->ActionCommand;
        }
        if (selection_match.Matched)
        {
            payload["selected_entry"] =
                HexPointer(selection_match.EntryAddress);
            payload["selected_age_ms"] = selection_match.AgeMs;
        }

        AppendRuntimeEvent(config, "item_use.validation", payload);

        if (runtime_item == nullptr)
        {
            return;
        }

        AppendRuntimeEvent(config, "bonfire.custom_item_use", payload);
        if (!forwarded_validation_result)
        {
            payload["reason"] = "native validation returned false";
            AppendRuntimeEvent(
                config,
                "bonfire.custom_item_action_skipped",
                payload);
            return;
        }

        HandleBonfireRuntimeItemUse(
            config,
            *runtime_item,
            1,
            1,
            "item_use_validation_0x2D3B20",
            -1,
            forwarded_validation_result ? 1 : 0);
    }

    bool __fastcall ItemUseValidationHook(int32_t item_id)
    {
        ItemUseValidationFn original = s_original_item_use_validation;
        bool native_result = true;
        if (original != nullptr)
        {
            native_result = original(item_id);
        }

        RuntimeSelectionMatch selection_match;
        const RuntimeGrantItem* runtime_item = FindBonfireRuntimeItem(item_id);
        if (runtime_item == nullptr)
        {
            runtime_item =
                ResolveSelectedRuntimeItemFromNativeUse(item_id, selection_match);
        }
        const RuntimeGrantItem* placeholder_runtime_item =
            FindBonfireRuntimeItemByNativeUseItem(item_id);
        const bool bonfire_runtime_item =
            runtime_item != nullptr || placeholder_runtime_item != nullptr;
        const bool forwarded_result = native_result || bonfire_runtime_item;

        LogItemUseValidation(item_id, native_result, forwarded_result);
        return forwarded_result;
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

    bool TryArmItemUseValidationObserver(const RuntimeWorkerConfig& config)
    {
        if (!config.ExeMatchesKnownBaseline)
        {
            return false;
        }

        if (config.GameBaseAddress == 0)
        {
            return false;
        }

        if (InterlockedCompareExchange(
            &s_item_use_validation_observer_state,
            1,
            0) != 0)
        {
            return s_item_use_validation_observer_state == 2;
        }

        s_original_item_use_validation =
            reinterpret_cast<ItemUseValidationFn>(
                config.GameBaseAddress + kEyeOrbUseValidationRva);

        DetourTransactionBegin();
        DetourUpdateThread(GetCurrentThread());
        DetourAttach(
            &(PVOID&)s_original_item_use_validation,
            ItemUseValidationHook);
        const LONG result = DetourTransactionCommit();

        nlohmann::json payload;
        payload["item_use_validation"] =
            HexPointer(config.GameBaseAddress + kEyeOrbUseValidationRva);
        payload["hook_target"] =
            HexPointer(reinterpret_cast<uintptr_t>(&ItemUseValidationHook));
        payload["detour_result"] = result;

        if (result != NO_ERROR)
        {
            AppendRuntimeEvent(
                config,
                "item_use.validation_observer_arm_failed",
                payload);
            s_original_item_use_validation = nullptr;
            InterlockedExchange(&s_item_use_validation_observer_state, 0);
            return false;
        }

        payload["original_target"] =
            HexPointer(reinterpret_cast<uintptr_t>(s_original_item_use_validation));
        AppendRuntimeEvent(
            config,
            "item_use.validation_observer_armed",
            payload);
        InterlockedExchange(&s_item_use_validation_observer_state, 2);
        return true;
    }

    bool TryArmInventorySelectedItemCategoryObserver(
        const RuntimeWorkerConfig& config)
    {
        if (!config.ExeMatchesKnownBaseline)
        {
            return false;
        }

        if (config.GameBaseAddress == 0)
        {
            return false;
        }

        if (InterlockedCompareExchange(
            &s_inventory_selected_item_category_observer_state,
            1,
            0) != 0)
        {
            return
                s_inventory_selected_item_category_observer_state == 2;
        }

        s_original_inventory_selected_item_category =
            reinterpret_cast<InventorySelectedItemCategoryFn>(
                config.GameBaseAddress + kInventorySelectedItemCategoryRva);

        DetourTransactionBegin();
        DetourUpdateThread(GetCurrentThread());
        DetourAttach(
            &(PVOID&)s_original_inventory_selected_item_category,
            InventorySelectedItemCategoryHook);
        const LONG result = DetourTransactionCommit();

        nlohmann::json payload;
        payload["selected_item_category"] =
            HexPointer(
                config.GameBaseAddress +
                kInventorySelectedItemCategoryRva);
        payload["hook_target"] =
            HexPointer(
                reinterpret_cast<uintptr_t>(
                    &InventorySelectedItemCategoryHook));
        payload["detour_result"] = result;

        if (result != NO_ERROR)
        {
            AppendRuntimeEvent(
                config,
                "inventory.selected_action_observer_arm_failed",
                payload);
            s_original_inventory_selected_item_category = nullptr;
            InterlockedExchange(
                &s_inventory_selected_item_category_observer_state,
                0);
            return false;
        }

        payload["original_target"] =
            HexPointer(
                reinterpret_cast<uintptr_t>(
                    s_original_inventory_selected_item_category));
        payload["action_candidate_return_rva"] =
            HexPointer(kInventorySelectedActionCandidateReturnRva);
        AppendRuntimeEvent(
            config,
            "inventory.selected_action_observer_armed",
            payload);
        InterlockedExchange(
            &s_inventory_selected_item_category_observer_state,
            2);
        return true;
    }

    bool TryArmInventorySelectedActionExecuteObserver(
        const RuntimeWorkerConfig& config)
    {
        if (!config.ExeMatchesKnownBaseline)
        {
            return false;
        }

        if (config.GameBaseAddress == 0)
        {
            return false;
        }

        if (InterlockedCompareExchange(
            &s_inventory_selected_action_execute_observer_state,
            1,
            0) != 0)
        {
            return
                s_inventory_selected_action_execute_observer_state == 2;
        }

        s_original_inventory_selected_action_execute =
            reinterpret_cast<InventorySelectedActionExecuteFn>(
                config.GameBaseAddress + kInventorySelectedActionExecuteRva);

        DetourTransactionBegin();
        DetourUpdateThread(GetCurrentThread());
        DetourAttach(
            &(PVOID&)s_original_inventory_selected_action_execute,
            InventorySelectedActionExecuteHook);
        const LONG result = DetourTransactionCommit();

        nlohmann::json payload;
        payload["selected_action_execute"] =
            HexPointer(
                config.GameBaseAddress +
                kInventorySelectedActionExecuteRva);
        payload["hook_target"] =
            HexPointer(
                reinterpret_cast<uintptr_t>(
                    &InventorySelectedActionExecuteHook));
        payload["detour_result"] = result;

        if (result != NO_ERROR)
        {
            AppendRuntimeEvent(
                config,
                "inventory.selected_action_execute_observer_arm_failed",
                payload);
            s_original_inventory_selected_action_execute = nullptr;
            InterlockedExchange(
                &s_inventory_selected_action_execute_observer_state,
                0);
            return false;
        }

        payload["original_target"] =
            HexPointer(
                reinterpret_cast<uintptr_t>(
                    s_original_inventory_selected_action_execute));
        payload["action_execute_return_rva"] =
            HexPointer(kInventorySelectedActionExecuteReturnRva);
        AppendRuntimeEvent(
            config,
            "inventory.selected_action_execute_observer_armed",
            payload);
        InterlockedExchange(
            &s_inventory_selected_action_execute_observer_state,
            2);
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
        uintptr_t use_item_slot = 0;
        uintptr_t use_item_target = 0;
        uintptr_t selected_item_slot = 0;
        uintptr_t selected_item_target = 0;
        if (!ResolveInventoryObserverSlots(
            config,
            inventory_object,
            inventory_vtable,
            adjust_quantity_slot,
            adjust_quantity_target,
            use_item_slot,
            use_item_target,
            selected_item_slot,
            selected_item_target))
        {
            InterlockedExchange(&s_inventory_observer_state, 0);
            return false;
        }

        const auto adjust_hook_target =
            reinterpret_cast<uintptr_t>(&InventoryAdjustQuantityHook);
        const auto use_hook_target =
            reinterpret_cast<uintptr_t>(&InventoryUseItemHook);
        const auto selected_hook_target =
            reinterpret_cast<uintptr_t>(&InventorySelectedItemEntryHook);
        if (adjust_quantity_target == adjust_hook_target &&
            use_item_target == use_hook_target &&
            selected_item_target == selected_hook_target)
        {
            InterlockedExchange(&s_inventory_observer_state, 2);
            return true;
        }

        DWORD old_protect = 0;
        auto* adjust_slot = reinterpret_cast<void**>(adjust_quantity_slot);
        auto* use_slot = reinterpret_cast<void**>(use_item_slot);
        auto* selected_slot = reinterpret_cast<void**>(selected_item_slot);
        const uintptr_t first_slot =
            std::min({adjust_quantity_slot, use_item_slot, selected_item_slot});
        const uintptr_t last_slot =
            std::max({adjust_quantity_slot, use_item_slot, selected_item_slot});
        const size_t protect_size =
            (last_slot - first_slot) + sizeof(void*);
        if (!VirtualProtect(
            reinterpret_cast<void*>(first_slot),
            protect_size,
            PAGE_EXECUTE_READWRITE,
            &old_protect))
        {
            nlohmann::json payload;
            payload["adjust_quantity_slot"] = HexPointer(adjust_quantity_slot);
            payload["use_item_slot"] = HexPointer(use_item_slot);
            payload["selected_item_slot"] = HexPointer(selected_item_slot);
            payload["error"] = GetLastError();
            AppendRuntimeEvent(config, "inventory.observer_arm_failed", payload);
            InterlockedExchange(&s_inventory_observer_state, 0);
            return false;
        }

        if (adjust_quantity_target != adjust_hook_target)
        {
            s_original_inventory_adjust_quantity =
                reinterpret_cast<InventoryAdjustQuantityFn>(adjust_quantity_target);
            s_inventory_adjust_quantity_slot = adjust_slot;
            *adjust_slot = reinterpret_cast<void*>(&InventoryAdjustQuantityHook);
        }

        if (use_item_target != use_hook_target)
        {
            s_original_inventory_use_item =
                reinterpret_cast<InventoryUseItemFn>(use_item_target);
            s_inventory_use_item_slot = use_slot;
            *use_slot = reinterpret_cast<void*>(&InventoryUseItemHook);
        }

        if (selected_item_target != selected_hook_target)
        {
            s_original_inventory_selected_item_entry =
                reinterpret_cast<InventorySelectedItemEntryFn>(selected_item_target);
            s_inventory_selected_item_entry_slot = selected_slot;
            *selected_slot = reinterpret_cast<void*>(&InventorySelectedItemEntryHook);
        }

        DWORD ignored = 0;
        VirtualProtect(
            reinterpret_cast<void*>(first_slot),
            protect_size,
            old_protect,
            &ignored);
        FlushInstructionCache(
            GetCurrentProcess(),
            reinterpret_cast<void*>(first_slot),
            protect_size);

        nlohmann::json payload;
        payload["inventory_object"] = HexPointer(inventory_object);
        payload["inventory_vtable"] = HexPointer(inventory_vtable);
        payload["adjust_quantity_slot"] = HexPointer(adjust_quantity_slot);
        payload["adjust_quantity_original_target"] = HexPointer(adjust_quantity_target);
        payload["adjust_quantity_hook_target"] = HexPointer(adjust_hook_target);
        payload["use_item_slot"] = HexPointer(use_item_slot);
        payload["use_item_original_target"] = HexPointer(use_item_target);
        payload["use_item_hook_target"] = HexPointer(use_hook_target);
        payload["selected_item_slot"] = HexPointer(selected_item_slot);
        payload["selected_item_original_target"] = HexPointer(selected_item_target);
        payload["selected_item_hook_target"] = HexPointer(selected_hook_target);
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
            command == "runtime.state" ||
            command == "session.create" ||
            command == "session.join" ||
            command == "session.invade" ||
            command == "session.leave" ||
            command == "session.reconnect" ||
            command == "rules.cycle" ||
            command == "invasions.taunt" ||
            command == "world.infection" ||
            command == "curse.accrue" ||
            command == "world.recover" ||
            command == "inventory.probe" ||
            command == "player.sync.request" ||
            command == "world.sync.request";
    }

    void ApplyRuntimeCommandState(
        const RuntimeWorkerConfig& config,
        const std::string& command,
        nlohmann::json& payload)
    {
        bool run_recovery = false;
        const RuntimeBehaviorDescriptor behavior =
            RuntimeBehaviorForCommand(command);
        AddRuntimeBehaviorPayload(payload, behavior);
        {
            std::lock_guard<std::mutex> guard(s_runtime_state_mutex);
            payload["applied"] = true;
            s_last_runtime_command = command;
            s_last_runtime_message_key = behavior.MessageKey;
            s_last_runtime_message_en = behavior.MessageEnglish;
            s_last_runtime_message_es = behavior.MessageSpanish;
            s_runtime_stage = behavior.BehaviorPhase;
            s_online_intent = behavior.OnlineIntent;

            if (command == "session.leave")
            {
                s_session_open = false;
                s_session_mode = "solo";
                s_last_session_request = "leave";
                payload["action"] = "session_leave_requested";
            }
            else if (command == "session.create")
            {
                s_session_open = true;
                s_session_mode = "host";
                s_last_session_request = "create";
                payload["action"] = "session_create_requested";
            }
            else if (command == "session.join")
            {
                s_session_open = true;
                s_session_mode = "guest";
                s_last_session_request = "join";
                payload["action"] = "session_join_requested";
            }
            else if (command == "session.invade")
            {
                s_session_open = true;
                s_session_mode = "invader";
                s_last_session_request = "invade";
                ++s_invasion_request_count;
                payload["action"] = "session_invade_requested";
                payload["invasion_request_count"] =
                    s_invasion_request_count;
            }
            else if (command == "session.reconnect")
            {
                s_last_session_request = "reconnect";
                payload["action"] = "session_reconnect_requested";
            }
            else if (command == "rules.cycle")
            {
                s_rule_preset_index = (s_rule_preset_index + 1) % 3;
                payload["action"] = "rules_cycled";
                payload["rule_preset"] =
                    RuntimeRulePresetName(s_rule_preset_index);
                payload["rules"] = RuntimeRulePayload(s_rule_preset_index);
            }
            else if (command == "invasions.taunt")
            {
                ++s_taunt_request_count;
                payload["action"] = "invasions_taunt_requested";
                payload["taunt_request_count"] = s_taunt_request_count;
            }
            else if (command == "world.infection")
            {
                ++s_infection_request_count;
                payload["action"] = "world_infection_requested";
                payload["infection_request_count"] =
                    s_infection_request_count;
            }
            else if (command == "curse.accrue")
            {
                ++s_curse_sigil_count;
                payload["action"] = "curse_sigil_accrued";
                payload["curse_sigil_count"] = s_curse_sigil_count;
            }
            else if (command == "world.recover")
            {
                ++s_recovery_request_count;
                run_recovery = true;
                payload["action"] = "world_recovery_requested";
                payload["recovery_request_count"] =
                    s_recovery_request_count;
            }
            else if (command == "runtime.state")
            {
                payload["action"] = "state_requested";
            }
            else
            {
                payload["applied"] = false;
                payload["action"] = "not_stateful";
            }

            nlohmann::json state = BuildRuntimeStatePayloadNoLock(config);
            payload["state"] = state;
            WriteRuntimeStateSnapshot(config, state);
        }

        if (run_recovery)
        {
            EmitInventoryProbeAndMaybeArm(config);
            GrantBonfireRuntimeItems(
                config,
                nullptr,
                -1,
                "runtime_command_recovery");
        }
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

            if (command == "runtime.state" ||
                command == "session.create" ||
                command == "session.join" ||
                command == "session.invade" ||
                command == "session.leave" ||
                command == "session.reconnect" ||
                command == "rules.cycle" ||
                command == "invasions.taunt" ||
                command == "world.infection" ||
                command == "curse.accrue" ||
                command == "world.recover")
            {
                ApplyRuntimeCommandState(config, command, payload);
                AppendRuntimeEvent(config, "command.applied", payload);
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
        {
            std::lock_guard<std::mutex> guard(s_runtime_state_mutex);
            nlohmann::json state = BuildRuntimeStatePayloadNoLock(*config);
            WriteRuntimeStateSnapshot(*config, state);
            AppendRuntimeEvent(*config, "runtime.state", state);
        }
        TryArmBonfireRestObserver(*config);
        TryArmItemUseValidationObserver(*config);
        TryArmInventorySelectedItemCategoryObserver(*config);
        TryArmInventorySelectedActionExecuteObserver(*config);
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
