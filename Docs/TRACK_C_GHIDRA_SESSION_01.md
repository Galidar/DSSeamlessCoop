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

### 1. Confirmed addresses (Ghidra-decompiled)

Build info baked into the binary path artifacts:
**`N:\FRPG2_64\Source\dantelion2_steam\...`** — FromSoft's build root.
"Dantelion 2" is their internal cross-platform engine layer; the `_steam`
suffix means this is the Steam-shipped binary.

| Symbol | RVA | Confirmation |
|---|---|---|
| `PlayerCtrl::ctor` (small init) | `0x14037EBE0` | sets `*this = PlayerCtrl::vftable`, inits +0x480/+0x488/+0x490/+0x498 to zero |
| `PlayerCtrl::dtor_partial` | `0x14037EC30` | called when freeing without vtable swap |
| `PlayerCtrl::dtor_full` | `0x14037EC60` | restores vtable then `free()` |
| **`PlayerCtrl::Spawn` (allocator + init)** | `0x140355930` | **bigger function — does the 0x4A0-byte alloc, calls ctor with 4 args, handles slot iteration, wires up the world-loaded ChrIns** ⭐⭐⭐ |
| `NetSummonSlotCtrl::ctor` | `0x1402D1CB0` | sets vftable, inits sentinel fields to 0xFFFFFFFF |
| `NetSummonSlotAreaManager::ctor` | (callee of slot alloc) | sets vftable + zeros aux pointers |
| `NetSummonSlotAreaManager::AllocateSlot` | `0x1402C0810` | **allocates 0x230 bytes, calls slot ctor, sets up two 0x18/0x10-byte aux structs with `0xe` (=14) initial values** ⭐⭐⭐ |
| `NetSvrCreateSummonSignJob::ctor` | `0x14029D6C0` | wired into the matchmaking server signal path |
| `NetSvrGetSummonSignListJob::ctor` | `0x14029D760` | parallel — "give me the sign list" |
| `Frpg2SignImpl::vftable_init` | `0x14152AC0` (approx) | line 1386072 in decompiled.c — registers Frpg2Sign callback handlers |

**Constants from the spawner that pin down DS2's online slot model:**

| Constant | Meaning |
|---|---|
| **PlayerCtrl struct size = `0x4A0` (1184 bytes)** | Confirmed at line 747842 of decompiled.c: `FUN_140833320(0x4A0, 0x10, plVar10)` |
| **NetSummonSlotCtrl struct size = `0x230` (560 bytes)** | Confirmed at line 614696 of decompiled.c |
| **MAX phantom slots = 6** (`uVar14 < 6` gate) | Inside `FUN_140355930` at the PlayerCtrl ctor call site. This is the famous "4 phantom cap" from DS2 lore — internally the engine reserves 6, with the vanilla matchmaking filling at most 4. |
| **Slot manager stride = `0xA90` (2704 bytes)** | `*(DAT_1416148f0 + 0x650) + 0x5d0 + slot_index * 0xA90` — each slot has a 2704-byte management record |
| **Global slot-manager pointer at `0x1416148F0`** | `DAT_1416148F0` in Ghidra. This is the static address that holds (or transitively reaches) the per-slot manager array. **NEW gm-class anchor for Phase 2B** — write a separate AOB to resolve this on every launch. |
| Aux struct init value `0xE` (=14) | Three fields set to 14 in `NetSummonSlotAreaManager::AllocateSlot`. Likely a per-slot "type cap" or visible-cap. |
| `ChrNullOperator::vftable` | The "empty slot" marker pattern. A slot whose char operator vtable equals this is empty. |

### 2. Phantom spawner candidate

**🎯 CONFIRMED:** `FUN_140355930` (RVA `0x140355930`) is the
PlayerCtrl spawn function. Its body shows the canonical FromSoft
phantom-slot allocation pattern:

```c
// Excerpt from FUN_140355930 around the PlayerCtrl ctor call site:
plVar10 = (longlong *)(param_1 + 0x2d0);
do {
  uVar14 = (uint)plVar15;             // slot index
  if (*plVar10 == 0) {                 // slot is empty
    if (uVar14 < 6 &&                 // SIX-SLOT CAP — bypass this for unlimited phantoms
        (plVar10 = (longlong *)
                   (*(longlong *)(DAT_1416148f0 + 0x650) + 0x5d0 +
                   (longlong)(int)uVar14 * 0xa90),  // walks into slot manager array
         plVar10 != 0)) {
      // virtual call on slot manager to get the allocation pool
      plVar10 = (**(code **)(*plVar10 + 8))();
      if (plVar10 != 0) {
        lVar6 = FUN_140833320(0x4a0, 0x10, plVar10);   // Allocate 0x4A0 bytes
        if (lVar6 != 0) {
          plVar4 = FUN_14037ebe0(lVar6, plVar10, local_190, plVar15);
          //              ^^^^^^^^   PlayerCtrl::ctor with 4 args:
          //                          (storage_ptr, alloc_pool, world_ctx, slot_index)
        }
        // ... wires up child ChrIns, body operator, ...
        local_140 = FUN_140833320(0x30, 8, plVar10);   // 0x30-byte sub-struct
        if (local_140 != 0) {
          FUN_14031b2a0(local_140, plVar4);            // bind sub-struct to PlayerCtrl
          *local_140 = ChrNullOperator::vftable;
        }
        // ... 0x270-byte sub-struct (probably the character operator) ...
      }
    }
  }
} while (...);
```

**Hook target — three options ranked:**

1. **Hook `FUN_140355930` directly** and inject our peer's
   `PlayerCharacterData` into a free slot. NOPing the `uVar14 < 6`
   check unblocks more than 6 phantoms simultaneously (if needed
   for cross-network co-op with many peers).
2. **Re-implement the body** in our Injector — manually allocate
   0x4A0 bytes via `FUN_140833320`, call `FUN_14037EBE0` ourselves,
   wire up the slot manager pointer at `DAT_1416148F0 + 0x650`.
   This bypasses the engine's matchmaking-driven path entirely and
   gives us full control over when/where a phantom appears.
3. **Hook the message handler** for
   `Frpg2RequestMessage::PushRequestSummonSign` and synthesize a
   valid push from our SHM-received peer data. Lets the engine's
   own pipeline do the work, just with us as the "server". Cleanest
   if the deserializer's checks are tractable.

### 3. Animation phase writer / anim_id field

**Partial finding (not yet confirmed as the local-player anim writer):**

Line 187396: `FUN_1400E5310` (RVA `0x1400E5310`) writes a u32 to
`+0x2B8` of its `param_1`, where `param_1` was passed as
`*caller_param_1 + 0x940`. The write is preceded by a virtual call
on the same struct's `+0x2B0` (a sub-controller vtable):

```c
plVar1 = (longlong *)(param_1 + 0x2b0);
// ... store position floats at +0x2D8..+0x2F0 ...
uVar4 = (**(code **)(*plVar1 + 8))(plVar1);   // virtual call returns u32
*(undefined4 *)(param_1 + 0x2b8) = uVar4;     // store it at +0x2B8 ← anim_id?
```

The `*caller + 0x940` parameter origin suggests this is the
**`+0x940` offset of some OTHER struct**, not necessarily our
`*(PlayerCtrl + 0xC0) + 0x2B8` chain. To confirm, we'd need to
walk in CE: get `*(PlayerCtrl + 0xC0)` at runtime, see if its
parent is reachable at `+0x940` from a different struct, and
match call-site context.

**Verdict: deferred.** Not blocking for Phase 2B — the
engine-cooperative phantom render gets animations *for free* once
we feed PlayerCharacterData through `FUN_140355930` or the
recreated allocator. anim_id only matters if we ever do
manual-mesh-render fallback.

### 4. Matchmaking checks to NOP for unrestricted overlay co-op

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

**Identified (specific function-body audits pending Session 02):**

- **The hard cap** — `uVar14 < 6` literal inside `FUN_140355930`
  at the PlayerCtrl ctor call site. Patching this `cmp ..., 6`
  to `cmp ..., N` widens the slot count engine-wide. Or patch
  the conditional jump to unconditional so any-index slots get
  allocated.
- **`Frpg2PlayerData.PhantomTypeCount`** protobuf message —
  bookkeeping for per-color phantom quotas (white/red/etc).
  Likely read inside `NetSummonAcceptMultiplayCtrl::CanAccept`
  paths (vtable methods of the class at lines 611250 / 611377).
- **`Frpg2Sv::MatchingParameter`** field reads — Soul Memory /
  level / build comparison happens here. The struct is passed
  by reference into `NetSvrSummonSignInterface::CreateSummonSign`
  (we have that signature now), and the server checks fail when
  the params don't match.
- **`PlayerStatus.Phantom_leave_at`** field — the fog-gate /
  area-transition despawn timer. Mentioned in the protobuf
  descriptor strings; the consumer reads it before deciding
  to despawn a phantom.

**For Phase 2B (v3.0.0), we DON'T necessarily need to NOP any
of these** — if we go with hook approach #2 (re-implement the
allocator body in our Injector), we never call into the vanilla
matchmaking pipeline, so its checks never fire. The NOP approach
is for hook #1 (in-place patch) or hook #3 (synthesize a server
push) where the engine's checks still run.

## Demangled C++ symbols recovered

Ghidra preserved the FromSoft RTTI/PDB symbols on some functions.
These are the C++ class/method signatures we now have verbatim:

```cpp
NetSvrSummonSignInterface::CreateSummonSign(
    unsigned int playerId,
    const Frpg2Sv::CellAddress& cell,
    const Frpg2Sv::MatchingParameter& match,
    Frpg2Sv::Frpg2SignType signType,
    const NetSvrSummonSignAppData& appData,
    unsigned int* outSignId
) -> INetSvrJob*

NetSvrSummonSignInterface::GetSummonSignList(
    unsigned int playerId,
    const Frpg2Vector<SignCellGetInfo>& cells,
    unsigned int,
    const Frpg2Sv::MatchingParameter& match,
    bool, bool,
    Frpg2Vector<Frpg2Sv::SignInfo>* outSigns,
    Frpg2Vector<Frpg2ClientLib::SignData>* outData
) -> INetSvrJob*

NetSvrMirrorKnightSignInterface::CreateSummonSign(...)  // parallel for Mirror Knight
```

Plus the vtable references:
- `PlayerCtrl::vftable` (at `.rdata` near line 781675)
- `NetSummonSlotCtrl::vftable`, `NetSummonAcceptMultiplayCtrl::vftable`,
  `NetSummonJoinMultiplayCtrl::vftable`, `NetSummonSlotAreaManager::vftable`,
  `NetSummonPacketCtrl::vftable`, `NetSvrSummonSignManager::vftable`
- `Frpg2RequestMessage::PlayerCharacterData::vftable`
- `Frpg2RequestMessage::RequestUpdatePlayerCharacter::vftable`
- `Frpg2RequestMessage::PushRequestSummonSign::vftable`
- `Frpg2PlayerData::EquipmentInfo::vftable`
- `ChrNullOperator::vftable` (empty-slot marker)

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
