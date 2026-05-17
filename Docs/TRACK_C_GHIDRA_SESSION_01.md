# Track C Ghidra Session 01 — DarkSoulsII.exe static analysis

Date: 2026-05-17
Binary: `C:\Program Files (x86)\Steam\steamapps\common\Dark Souls II Scholar of the First Sin\Game\DarkSoulsII.exe`
Size: 27 MB (28,200,992 bytes per `kKnownSotfsSteamExeSize` in the Injector)
Tool: Ghidra 12.0.4 headless analyzer via the `ghidra-decompile-dll` skill
Output: `Docs/ghidra-out/DarkSoulsII/{decompiled.c, report.md, metadata.json, headless.log}`

This session takes the place of the live CE breakpoint approach
which we proved (the hard way) triggers DS2's FROM anti-cheat. Static
analysis on the disk binary carries zero anti-cheat risk and lets us
identify call sites + offsets without the game even being running.

## Goal

Identify the network character-data path so we can hook it from the
Injector:

1. **PlayerCtrl constructor** — what function allocates a PlayerCtrl
   and writes the vtable at +0x00?
2. **Phantom spawner from net data** — the function the engine calls
   when a peer's char_data arrives over the wire and a phantom slot
   needs to be populated. This is the hook target for Phase 2B.
3. **Animation phase writer** — the function that updates the float
   at `*(PlayerCtrl + 0xC0) + 0x2B8` every tick. Once we have its
   disassembly we can read off the `animation_id` u32 (the value we
   couldn't find via live BPs without triggering AC).
4. **Vanilla matchmaking checks** — 4-phantom cap, Soul Memory
   matching, level-range gate, fog-gate despawn. These are the
   conditionals we may want to NOP/patch to let our overlay show
   peers that vanilla would reject.

## Known landmarks (from live RE Session 01)

These give Ghidra fixed reference points to anchor cross-references
against:

| Symbol | RVA (module-relative) | Notes |
|---|---|---|
| `gm_imp_global` AOB anchor | `.text + 0x3F5219` | `48 8B 05 ?? ?? ?? ?? 48 8B 58 38 48 85 DB 74 ?? F6` — load `gm` global into RAX, then dereference +0x38 |
| `gm_imp_global` address | `.data + ?` | resolved at runtime via the anchor's RIP-relative disp32; in last session = module + 0x16148F0 |
| PlayerCtrl vtable | `.rdata + ~0x10E4BB8` | observed at runtime as `0x7FF7F4864BB8` with module base `0x7FF7F3780000`; RVA = `0x10E4BB8` |

## PlayerCtrl struct landmarks (from Track C RE)

These offsets help identify the right function in decompiled C by
looking for explicit `+0xNN` constants:

| PlayerCtrl offset | Field | Use for finding |
|---|---|---|
| +0x90/+0x94/+0x98 | position x/y/z (f32) | Anything that reads world pos |
| +0x9C | w = 1.0 sentinel | Sanity-check pattern |
| +0x118 | name sub-pointer (UTF-16) | Player_/NetworkPlayer_ name init |
| +0x168/+0x170/+0x174 | HP triple (cur/max-buff/base) | Damage / heal / spawn |
| +0x1AC..+0x1C0 | equip load floats | Equip changes |
| +0xC0 (sub-pointer) | world/zone module | Area transitions |
| +0xC0 sub + 0x13C/+0x140 | PlayArea IDs | Map zone change handler |
| +0xC0 sub + 0x2B8 | animation phase f32 | **Animation update** ← high-value target |
| +0xC0 sub + 0x2E0 | game time counter f32 | Anything time-related |
| +0xE0 (sub-pointer) | equipment module | Equip change handler |
| +0xE0 sub + 0x37C | 22-slot equipment array, stride 20 | **Char_data deserializer** ← high-value target |

## Analysis status

**[2026-05-17 — initial run]** Ghidra headless launched in background
on the 27 MB binary. Expected duration 20-60 min depending on
machine and how many functions need decompilation. Log streaming to
`Docs/ghidra-out/decompile.log`.

Will be populated below as findings come in.

## Findings (live, populated as analysis progresses)

### 0. String mining — done in 30 s, before Ghidra even finished

A pre-Ghidra string dump on the 27 MB binary surfaced the complete
FromSoft network architecture in plaintext (via embedded protobuf
descriptors + RTTI class names + source-file path artifacts):

**Confirmed architecture (matches Tim Leonard's DS3 reversing):**

- DS2 uses **protobuf** for character data over the wire — same as DS3.
- The wire-format namespace is `Frpg2RequestMessage`.
- The character-data namespace is `Frpg2PlayerData`.
- Steam matchmaking is called directly via the public Steamworks
  API (`SteamMatchmaking()->CreateLobby/JoinLobby/LeaveLobby`).
- P2P transport uses an internal wrapper called `Nauru`
  (`P2PConnection`, `TCPConnection`, `TCPConnectionManager`).

**🎯 Wire messages to target (Frpg2RequestMessage.*):**

| Message | Use |
|---|---|
| `PlayerCharacterData` | THE char_data envelope. Find its deserializer. |
| `RequestUpdatePlayerCharacter` | Client → server: push my char update. |
| `RequestUpdatePlayerCharacterResponse` | Server's ack. |
| `RequestUpdatePlayerStatus` | Lighter-weight status updates (HP, soul memory). |
| `PushRequestSummonSign` | **Server → client: accept this summon.** Likely the path that allocates a phantom PlayerCtrl. |
| `PushRequestVisit` | Server → client: accept this invader/visitor. (Same path that the DS3 PaleTongue CVE-2022-24126 abused.) |
| `PushRequestRejectSign` | Reject pushed by server when matching fails. |
| `MatchingParameter` | Soul Memory / level / build params used by the matchmaker. |

**🎯 PlayerData sub-messages (Frpg2PlayerData.*):**

| Sub-message | Contents |
|---|---|
| `AllStatus` | Composite of every status sub-record. |
| `LevelStatus` | Soul Level, Soul Memory, soul/exp counters. |
| `PhysicalStatus` | HP triple, stamina, durability — maps to PlayerCtrl +0x168/+0x170/+0x174. |
| **`EquipmentInfo`** | **THE 22-slot equipment array** we mapped at PlayerCtrl + 0xE0 sub + 0x37C. Wire-format mirror. |
| `WeaponStatus` | Per-weapon durability / infusion. |
| `ArmorStatus` | Per-armor durability. |
| `ItemUsingInfo` | Quickbar / consumable state. |
| `PhantomTypeCount` | Bookkeeping for the 4-phantom cap and per-color quotas. |
| `PlayerStatus` | High-level player state (covenant, world type). Has a `Phantom_leave_at` field — suggests the despawn-timer logic lives here. |
| `PlayerLocation` | Current zone + position triplet. |
| `ServerSideStatus` | Server-side accounting. |
| `StatsInfo` | Stat block — has `Bonfire_levels` field. |
| `Vector` | Generic float[3]. |

**🎯 Engine classes to find vtables for (RTTI dump):**

These RTTI class names are EXPLICIT in the binary. Once Ghidra is
done, each one has a vtable and an enumerable set of methods — we
just need to grep `decompiled.c` for the class name + walk the
member functions.

- `PlayerCtrl` — the player character class (we already know this from CE).
- `NetSummonAcceptMultiplayCtrl` — controls accepting an incoming summon.
- `NetSummonJoinMultiplayCtrl` — controls joining as the guest.
- `NetSummonPacketCtrl` — handles summon-specific packet framing.
- `NetSummonSlotCtrl` — **manages an individual phantom slot.** Strong candidate for the slot-allocator we want to call.
- `NetSummonSlotAreaManager` — area-scoped slot lifecycle manager.
- `NetSvrSummonSignInterface` — server-facing summon-sign API.
- `INetSvrSummonSignReceiver` — receiver interface for sign notifications.
- `NetSvrSummonSignManager` — sign-system manager.
- `NetSvrSummonSignPushNotifyBuffer` — buffers server pushes.
- `NetSvrSummonSignSummonJob` — the job that performs the summon.
- `NetSvrMirrorKnightSignInterface` — parallel system for Mirror Knight (multi-host) signs.
- `NetworkPlayerLockTargetCtrl` — target-lock controller specifically for network players.
- `ChrAsmCtrl` — character-assembly (equipment) controller.
- `PlayerData@DLNRD` / `SteamPlayerData@DLNRD` — Steam-bound player data wrappers.

**Source-file path artifacts found in the binary:**

- `..\..\Source\Network\Server\SummonSign\NetSvrSummonSignUtil.cpp` — summon sign system
- `..\..\Source\Network\Server\BreakIn\NetSvrBreakInManager.cpp` — invasion
- `..\..\Source\Network\Server\Duel\NetSvrDuelManager.cpp` — duel arena
- `..\..\source\playerdata\PlayerDataManager.cpp` — player data manager
- `..\..\source\playerdata\steam\SteamOnlineIDDataManager.cpp` — Steam ID manager
- `..\..\source\matching\P2PConnection.cpp` — P2P stack
- `..\..\source\matching\NRSessionSearchResult.cpp` — search result struct
- `..\..\source\matching\SessionLight.cpp` — lightweight session
- `..\..\Game\Common\src\Frpg2PlayerData.pb.cc` — protobuf char data
- `..\..\Game\Common\src\Frpg2RequestMessage.pb.cc` — protobuf wire format

### 1. Confirmed addresses (post-rebase)

*Pending Ghidra completion.*

### 2. Phantom spawner candidate

**Hypothesis (string-driven, awaiting decompiled confirmation):**

The flow when a saponita summon succeeds is approximately:

```
matchmaking server  →  PushRequestSummonSign  →  client
  client deserializes → Frpg2Sign callback fires (CallbackType@Frpg2Sign)
    → NetSummonAcceptMultiplayCtrl::OnAccept (or similar)
       → NetSummonSlotAreaManager allocates a slot
          → NetSummonSlotCtrl::Init (or similar) populates the slot
             from PlayerCharacterData
             → PlayerCtrl constructor + Frpg2PlayerData.EquipmentInfo
               deserializer fills the 22-slot equipment array
```

The hook target is whichever method on `NetSummonSlotCtrl` does
the actual char_data → PlayerCtrl population. If we can call that
function ourselves with our peer's data (bypassing the server-side
matchmaking) the engine will allocate + render a phantom for us
without vanilla restrictions.

### 3. Animation phase writer / anim_id field

*Pending. Search the decompiled output for the constant `0x2B8`
in the context of a write through a PlayerCtrl + 0xC0 sub-pointer
chain.*

### 4. Matchmaking checks to NOP for unrestricted overlay co-op

**Likely targets:**

- `Frpg2PlayerData.PhantomTypeCount` reads → the 4-phantom cap.
- `MatchingParameter` field checks → Soul Memory / level matching.
- `PlayerStatus.Phantom_leave_at` → the fog-gate despawn timer.
- `NetSummonAcceptMultiplayCtrl::CanAccept` (or similar) — likely
  the single function that says "yes/no" to an incoming push.

*Specific RVAs pending Ghidra completion.*

## Next session plan (after this analysis lands)

1. **Pick one candidate per target** based on the decompiled C.
2. **Verify with CE `read_memory`** — read the function's first
   instructions at runtime and confirm they match what Ghidra
   decompiled (proves we have the right binary + ASLR base).
3. **Design the hook** — most likely Detours-style trampoline from
   the Injector. We already have Detours wired (used for
   RestAtBonfire hook today).
4. **Implement the hook in a feature-flagged C++ file** so it can
   be disabled at runtime if it crashes the game.
5. **Stage release as v3.0.0-experimental.alpha** for opt-in
   testing with the brother PC.
