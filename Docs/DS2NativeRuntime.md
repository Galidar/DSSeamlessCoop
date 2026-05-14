# DS2 Native Runtime Rebuild

This document is the starting point for the new Dark Souls II SOTFS seamless
runtime. The old DS2SeamlessCoop loose-data package has been removed so the
project can move toward a native method instead of relying on edited maps,
shops, or vanilla multiplayer items. A tiny Bonfire-owned data layer remains
only for custom item rows/icons/text while the runtime owns the behavior.

## Goal

Build a Bonfire-native DS2 runtime that can eventually provide seamless-style
co-op behavior through injected DS2 hooks and Bonfire's private-server stack:
custom session commands, comfortable connect/reconnect flow, shared world
state, and custom utility items controlled by our runtime rather than by the
vanilla DS2 online item rules.

The design target is the same player outcome as the DS1/DS3 seamless style:
one host world, multiple players staying together through normal play, less
manual setup, and no dependency on official servers.

## Baseline After Cleanup

- `Resources\Loader\DS2SeamlessCoop\` is removed from the repository.
- `Resources\Loader\modengine.ini` keeps DS2 file overrides disabled by
  default.
- Bonfire no longer auto-detects stale `Loader\DS2SeamlessCoop` or
  `ds2multoverhaul` folders.
- Bonfire auto-loads only `Resources\Loader\DS2BonfireRuntime\`, the
  self-contained native runtime item/text layer for the custom command items.
- The updater removes stale DS2 loose-data folders from existing installs before
  copying a new release over them.
- DS2 still uses the private server, server keys, injected server-address hook,
  and `.sl3` separate saves.
- External overhaul data remains possible only through an explicit user path or
  `DS2_OVERHAUL_DIR`.

## Command/Event Bridge

The first native runtime feature is an injected DS2 command bridge:

- Bonfire writes runtime paths into `Injector.config`:
  `EnableDs2NativeRuntime`, `Ds2NativeRuntimeSessionId`,
  `Ds2NativeRuntimeEventLog`, and `Ds2NativeRuntimeCommandInbox`.
- BonfireService launch creates files under `Runtime\DS2Native\`.
- `DS2_NativeRuntimeHook` starts a lightweight worker thread in the game
  process.
- The runtime appends JSONL events such as `runtime.started`,
  `runtime.heartbeat`, `inventory.probe`, `command.received`, and
  `command.pong`.
- Commands are JSONL lines appended to the command inbox. Initial recognized
  commands are `ping`, `session.create`, `session.join`, `session.leave`,
  `session.reconnect`, `inventory.probe`, `player.sync.request`, and
  `world.sync.request`.
- BonfireService exposes RPC helpers:
  `ds2_runtime.status` reads the latest runtime heartbeat and
  `ds2_runtime.command` appends a command envelope to the inbox.

At this stage commands are acknowledged and logged only. Gameplay handlers are
not armed yet; this deliberately proves the control channel before mutating
items, flags, or world state.

`inventory.probe` validates the Bob table / Blue Acolyte-style DS2 inventory
chain without patching it. The runtime resolves `GameManagerImp` with the same
AOB pattern used by Bob's Cheat Engine table, then walks:

- `GameManagerImp`
- `+0xA8 -> +0x10 -> +0x10 -> +0x10`
- inventory vtable slot `+0x30`

The event records every pointer hop and the candidate quantity-adjust function.
When the probe resolves on the supported SOTFS executable, the runtime wraps the
candidate vtable slot and emits `inventory.observer_armed`. The wrapper logs
`inventory.adjust_quantity` events and forwards the original call unchanged.
This is still observational: it does not alter item IDs, quantities, or
consumption rules.

## Bonfire Item Grant Path

The runtime now hooks `RestAtBonfire` at `DarkSoulsII.exe+0x17DC40`. The call
shape was confirmed from Boblord's SOTFS Cheat Engine table:

- `rcx = [[[GameManagerImp]+70]+58]`
- `edx = bonfireId`
- Majula test bonfire id: `4650`

On a supported executable the injected runtime emits
`bonfire.rest_observer_armed`. Every rest emits `bonfire.rest`, then the runtime
uses DS2's own item grant functions:

- `ItemGive`: `DarkSoulsII.exe+0x1AC3D0`
- `ItemStructConvert`: `DarkSoulsII.exe+0x05D950`
- `ItemPopupDisplay`: `DarkSoulsII.exe+0x501080`

The grant context follows the Bob table chain:

- `GameManagerImp`
- inventory bag list: `[[[GameManagerImp]+A8]+10]`
- item display manager: `[[GameManagerImp]+22E0]`
- inventory bag entries: `inventoryBagList + 0xF0`, 3840 entries, 16 bytes each

The 16-byte item grant entry is:

- `+0x00`: reserved / zero
- `+0x04`: item id
- `+0x08`: durability float
- `+0x0C`: quantity int16
- `+0x0E`: upgrade byte
- `+0x0F`: gem byte

Current Bonfire runtime items are Bonfire-owned custom rows loaded from the
minimal `DS2BonfireRuntime` layer:

- `60360001`: `Liberation Scroll` / `Pergamino de la Liberacion`.
- `62060001`: `Abyssal Eye Orb` / `Orbe del Ojo Abisal`.
- `62060002`: `Ominous Tome` / `Tomo Ominoso`.

The rows are still based on known-safe DS2 item layouts, but their visible FMG
identity is Bonfire-owned. Current experimental usage IDs are `2140` for the
scroll slot and `2120` for the two command-artifact slots so native item-use
paths can be traced and then intercepted by the runtime.

`DS2BonfireRuntime` currently includes:

- `enc_regulation.bnd.dcx`
- a clean vanilla `Param\*.param` set extracted from the installed SOTFS
  `enc_regulation.bnd.dcx`, with only `ItemParam.param` and
  `ItemUsageParam.param` overlaid for the Bonfire custom rows
- `menu\text\english\*.fmg`
- `menu\text\spanish\itemname.fmg`,
  `menu\text\spanish\simpleexplanation.fmg`, and
  `menu\text\spanish\detailedexplanation.fmg`

Important: do not strip this package down to only `ItemParam.param` and
`ItemUsageParam.param`. The small `enc_regulation.bnd.dcx` expects its companion
loose `Param` set; launching with only the two item params lets the runtime
start but can make DS2 close during data loading.

Equally important: do not copy `PlayerStatusItemParam.param` or
`ShopLineupParam.param` from the old DS2SeamlessCoop/overhaul package into this
runtime layer. Those files are vanilla in `DS2BonfireRuntime`; otherwise the
old data path grants the items at new-character/startup time or sells them in
shops, bypassing the native bonfire-rest grant path.

The visible names/descriptions are intentionally Bonfire-specific so the
inventory no longer presents the items as native Human Effigy / Cracked Eye Orb
clones.

## Runtime Milestones

1. Add a DS2 runtime diagnostics layer inside `Source\Injector\Hooks\DarkSouls2`.
   It should confirm the executable version and active DS2 runtime settings
   before any gameplay hook is armed. Initial file:
   `DS2_NativeRuntimeHook.cpp`.
2. Add the Bonfire-to-injected-runtime command/event bridge. Initial event and
   command files are under `Runtime\DS2Native\`.
3. Find and wrap one stable item-use path. `inventory.probe` validates the
   chain, then `inventory.observer_armed` wraps the quantity-adjust slot to log
   item adjustments while preserving vanilla consumption.
4. Introduce Bonfire-owned DS2 command items. The first pass grants custom rows
   from `DS2BonfireRuntime` at bonfire rest while the runtime owns the behavior.
5. Add a bonfire-rest grant path for those command items, learned from the DS1
   investigation but implemented with DS2 addresses and DS2-safe guards. Initial
   hook and item grant logging are in `DS2_NativeRuntimeHook.cpp`.
6. Move session actions out of vanilla DS2 matchmaking rules: create, join,
   leave, reconnect, and host migration candidates should be controlled by the
   runtime and Bonfire state.
7. Synchronize shared world state in stages: player transforms first, then
   enemies, objects, boss progress, flags, and area reset behavior.

## Safety Rules

- Keep DS2 retail saves separate from Bonfire private-server saves.
- Do not re-enable broad loose map/shop overrides as the default path. The only
  auto-loaded data layer should be the minimal Bonfire runtime item/text layer.
- Bind offsets to the supported Steam DS2 SOTFS executable and fail closed on
  unknown versions.
- Prefer small hooks with observable logs over large invisible patches.
- Preserve the current DS2 private-server bridge while the new runtime is built.

## Current Concrete Work Item

The current code milestone is to run DS2 through Bonfire with
`DS2BonfireRuntime` active, rest at a bonfire, and inspect
`Runtime\DS2Native\*.events.jsonl` for `bonfire.rest_observer_armed`,
`bonfire.rest`, and `bonfire.item_grant_result`. A successful grant means the
runtime can add visible Bonfire-owned custom items from DS2's internal item-give
path without touching map pickups or the old overhaul package.

Live validation on 2026-05-14:

- `Injector.config` generated `EnableModFileOverrides=true` and
  `ModOverrideDirectory=...\Loader\DS2BonfireRuntime`.
- Runtime session `85701f21-22cb-40ae-83bc-bd821fa533b0_35712` started with
  `file_overrides:true`.
- Resting at bonfire id `2650` emitted `bonfire.rest`.
- The item helper resolved `GameManagerImp`, inventory bag list, item display
  manager, and bag entries.
- The helper emitted `bonfire.item_grant_skipped` because all three custom item
  IDs were already present at quantity `1`: `60360001`, `62060001`,
  `62060002`.
- The user confirmed the items were directly present in inventory. No native
  pickup popup appeared during this run because the helper skipped `ItemGive`
  and `ItemPopupDisplay` once it detected all three items were already owned.

Follow-up correction on 2026-05-14:

- That skipped-grant validation exposed an important mistake: the temporary
  `DS2BonfireRuntime\Param` set had been copied from the old DS2SeamlessCoop
  package, including its modified `PlayerStatusItemParam.param`.
- `DS2BonfireRuntime\Param` was rebuilt from the installed game's vanilla
  regulation extraction. Only `ItemParam.param` and `ItemUsageParam.param`
  remain custom-overlaid.
- Verification: the runtime `PlayerStatusItemParam.param` and
  `ShopLineupParam.param` now hash-match the vanilla extraction and contain no
  `60360001`, `62060001`, or `62060002` entries. New characters should not
  receive the Bonfire custom items until the native rest hook grants them.
