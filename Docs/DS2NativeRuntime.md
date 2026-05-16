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
- Commands are JSONL lines appended to the command inbox. Recognized commands
  are `ping`, `session.create`, `session.join`, `session.invade`,
  `session.leave`, `session.reconnect`, `rules.cycle`, `invasions.taunt`,
  `world.infection`, `curse.accrue`, `world.recover`, `inventory.probe`,
  `player.sync.request`, and `world.sync.request`.
- BonfireService exposes RPC helpers:
  `ds2_runtime.status` reads the latest runtime heartbeat and
  `ds2_runtime.command` appends a command envelope to the inbox.
- BonfireService also starts a `Ds2NativeSessionCoordinator` background watcher.
  It tails `<session>.actions.jsonl`, writes `<session>.service_state.json`,
  and emits `ds2_runtime.session` UI notifications whenever an in-game custom
  item changes the service-side session state.

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

- `62061000`: `Blessed Eye Orb` / `Orbe del Ojo Bendecido`.
- `62061001`: `Crystal Eye Orb` / `Orbe del Ojo de Cristal`.
- `62061002`: `Chaos Eye Orb` / `Orbe del Ojo del Caos`.
- `62061003`: `Abyssal Eye Orb` / `Orbe del Ojo Abisal`.
- `62061004`: `Ominous Tome` / `Tomo Ominoso`.
- `62061005`: `Dried Fingers` / `Dedos Secos`.
- `62061006`: `Cursed Pendant` / `Colgante Maldito`.
- `62061007`: `Crimson Blossom` / `Flor Carmesi`.
- `62061008`: `Parchment of Deliverance` / `Pergamino de la Liberacion`.

The current release-safe icon pass deliberately uses DS2 vanilla icon assets
rather than redistributing DS1/DS3 menu art. The rows are still Bonfire-owned
custom items, but their `Icon ID` points to existing DS2 visuals:

- `62061000` / Blessed Eye Orb -> icon `53600000` (`Eye of the Priestess`).
- `62061001` / Crystal Eye Orb -> icon `62050000` (`Cracked Blue Eye Orb`).
- `62061002` / Chaos Eye Orb -> icon `62060000` (`Cracked Red Eye Orb`).
- `62061003` / Abyssal Eye Orb -> icon `51000000` (`Crushed Eye Orb`).
- `62061004` / Ominous Tome -> icon `60527000` (`Bonfire Ascetic`).
- `62061005` / Dried Fingers -> icon `62000000` (`Dried Fingers`).
- `62061006` / Cursed Pendant -> icon `60370000` (`Silver Talisman`).
- `62061007` / Crimson Blossom -> icon `60310000` (`Green Blossom`).
- `62061008` / Parchment of Deliverance -> icon `60355000` (`Aged Feather`).

DS1/DS3 Seamless-style items can be custom by ID, text, and runtime behavior
while still referencing icon art from the base game. Do not ship copied DS1/DS3
menu textures in DS2 releases; use DS2 vanilla icons or original Bonfire-owned
art instead.

These rows port the DS1 Seamless item contract into DS2-owned row IDs. DS2 still
needs a real `ItemParam` row to render an inventory/HUD object, but the custom
IDs, FMG identity, runtime messages, and behavior are Bonfire-owned. The first
prototype borrowed eye-orb/effigy shells, which made DS2 keep treating some
items as vanilla invasion or utility items. The current pass uses `Bone of
Order` only as a safe field template, then gives every Bonfire command item its
own native identity:

```text
Effect ID          same as visible Bonfire row ID, 62061000..62061008
Item Usage ID      same as visible Bonfire row ID, 62061000..62061008
Item Use Animation 1700
Item State         13
```

Only the safe use fields are inherited from `Bone of Order`; the native
`speffect_id`, `item_usage_id`, visible ID, names, descriptions, and Bonfire
command mapping are Bonfire-owned per item. The injector suppresses the vanilla
continuation after the Bonfire action fires, so the shell exists only to make
DS2 expose a usable/quickslot item in every zone while Bonfire owns the meaning.

`DS2BonfireRuntime` currently includes:

- `enc_regulation.bnd.dcx`
- a clean vanilla `Param\*.param` set extracted from the installed SOTFS
  `enc_regulation.bnd.dcx`, with only `ItemParam.param`,
  `ItemUsageParam.param`, and `ItemUseCheckDialogParam.param` overlaid for the
  Bonfire custom rows
- `menu\text\english\*.fmg`
- `menu\text\spanish\itemname.fmg`,
  `menu\text\spanish\simpleexplanation.fmg`, and
  `menu\text\spanish\detailedexplanation.fmg`
- `menu\text\neutralspanish\itemname.fmg`,
  `menu\text\neutralspanish\simpleexplanation.fmg`, and
  `menu\text\neutralspanish\detailedexplanation.fmg`

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

The native DS2 use-confirmation band comes from `ItemUseCheckDialogParam`, not
from `ItemParam` itself. `Bone of Order` is item row `62020000` and uses
`ItemUseCheckDialogParam` row `62020000 -> common.fmg 30000000`, which is the
vanilla `Use %s?` style prompt. Bonfire custom rows `62061000..62061008` clone
that same dialog row so the game shows a normal localized DS2 confirmation
prompt before Bonfire's runtime-owned action runs.

## Custom Item Functions

The custom item runtime now treats item use as an explicit event source instead
of relying only on inventory quantity changes. Live Frida/x64dbg-style tracing
showed the custom entries keep the Bonfire item id at entry `+0x14`, while DS2
uses a native/placeholder behavior id at entry `+0x18`:

- `62061000` / Blessed Eye Orb -> native use id `62061000`
- `62061001` / Crystal Eye Orb -> native use id `62061001`
- `62061002` / Chaos Eye Orb -> native use id `62061002`
- `62061003` / Abyssal Eye Orb -> native use id `62061003`
- `62061004` / Ominous Tome -> native use id `62061004`
- `62061005` / Dried Fingers -> native use id `62061005`
- `62061006` / Cursed Pendant -> native use id `62061006`
- `62061007` / Crimson Blossom -> native use id `62061007`
- `62061008` / Parchment of Deliverance -> native use id `62061008`

The runtime wraps the inventory selected-entry vtable slot `+0x70`. That slot
is noisy: DS2 also calls it while enumerating visible inventory entries from
return RVA `0x1B281C`. The runtime therefore only caches calls from the
selected-entry callers around `0x1B19DA`, `0x1B19FF`, and `0x1B1A6F`, then
stores the highlighted Bonfire custom item and its native use id for a short
selection window.

The validated action trigger is `DarkSoulsII.exe+0x1B19D0`, wrapped as
`InventorySelectedItemCategoryHook`. When its caller return RVA is `0x32FF15`,
DS2 is acting on the selected usable item. The hook resolves the recent
`+0x70` selected-entry cache back to the Bonfire custom item, emits
`inventory.selected_action_candidate`, and calls `HandleBonfireRuntimeItemUse`.
The next vanilla continuation callsite is `DarkSoulsII.exe+0x500C40`, returning
to `0x32FF24`; `InventorySelectedActionExecuteHook` watches that call and skips
the native item action after the Bonfire runtime has already handled the custom
item. The hook now also honors a short post-action Bonfire suppression window,
so category drift in the next DS2 call cannot sneak a vanilla continuation
behind a handled Bonfire command. This is intentionally enabled for all Bonfire
runtime items so the visible custom rows behave as Bonfire-owned command
artifacts instead of continuing into their cloned/native placeholder behavior.
This path was live-tested on 2026-05-14 with quickslot/inventory use for the
first three custom-item prototypes, then expanded to the DS1 Seamless-style
nine-item set. DS2's use-validation helper at
`DarkSoulsII.exe+0x2D3B20` and the inventory quantity-adjust vtable slot remain
backup/diagnostic hooks for native placeholder ids and possible consumption
events. For Bonfire custom native IDs the validation hook returns true even if
the original DS2 helper does not recognize the new `speffect_id`; the log keeps
both `native_validation_result` and the forwarded `validation_result`. Every
matching use writes a normal runtime event plus a
Bonfire-readable sibling action log, message log, and state snapshot beside the
event log:

```text
Runtime\DS2Native\<session>.events.jsonl
Runtime\DS2Native\<session>.actions.jsonl
Runtime\DS2Native\<session>.messages.jsonl
Runtime\DS2Native\<session>.state.json
```

Each action also carries a Bonfire-owned behavior contract:
`message_key`, English/Spanish message text, `behavior_phase`,
`online_intent`, `native_shell_use_only=true`, and
`vanilla_continuation_policy=suppress_after_bonfire_action`. The current state
snapshot mirrors the last command/message/stage and points to the message log,
so Bonfire UI/server code can react without asking DS2 to run the vanilla item
meaning.

Current action map:

- `62061000` / `Blessed Eye Orb`: emits `session.create`.
- `62061001` / `Crystal Eye Orb`: emits `session.join`.
- `62061002` / `Chaos Eye Orb`: emits `session.invade`.
- `62061003` / `Abyssal Eye Orb`: emits `session.leave`.
- `62061004` / `Ominous Tome`: emits `rules.cycle`.
- `62061005` / `Dried Fingers`: emits `invasions.taunt`.
- `62061006` / `Cursed Pendant`: emits `world.infection`.
- `62061007` / `Crimson Blossom`: emits `curse.accrue`.
- `62061008` / `Parchment of Deliverance`: emits `world.recover`, refreshes
  the inventory probe, and re-runs the missing Bonfire item grant path as a
  lightweight runtime repair.

The legacy prototype IDs `60360001`, `62060001`, and `62060002` remain
recognized so old test saves do not fall back into vanilla behavior, but they
are no longer granted at bonfire rest.

This is the first functional layer for the custom items: the game-side item use
now produces deterministic runtime commands/state instead of only diagnostic
logs. The inventory quantity-adjust observer remains as a reusable backup hook,
but live testing showed custom use may validate through the native placeholder
id instead of the visible custom id. The selected-entry cache is what makes the
mapping precise and avoids treating a normal vanilla eye orb as a Bonfire
command item. The deeper multiplayer effects can bind to the same action/state
files in later milestones.

Historical 2026-05-14 validation before the per-item custom shell migration:

- `Tomo Ominoso` selected entry `+0x14=62060002`, `+0x18=62050000`; `0x1B19D0`
  returned category `15` from caller `0x32FF15`; action `rules.cycle` advanced
  rules through `open_coop` and `challenge`.
- `Orbe del Ojo Abisal` selected entry `+0x14=62060001`, `+0x18=62060000`;
  `0x1B19D0` returned category `14`; action `session.leave` wrote
  `session_leave_requested`. The later `0x500C40` action-execute hook is now
  expected to suppress the native red-eye invasion search/no-world continuation
  for this item.
- `Pergamino de la Liberacion` selected entry `+0x14=60360001`,
  `+0x18=60151000`; `0x1B19D0` returned category `13`; action
  `world.recover` refreshed the probe and re-ran the missing-item grant repair.

Current rows `62061000` through `62061008` should instead resolve their
selected-entry native shell as the same custom ID at both `+0x14` and `+0x18`.
If live logs show `62020000`, `62050000`, `62060000`, or `60151000` for one of
the nine current IDs, the loaded Param/DLL is stale or the row was accidentally
rebuilt from a vanilla prototype again.

`ds2_runtime.status` also reports `action_log`, `state_file`, `last_action`,
`service_state_file`, `last_action`, parsed `runtime_state`, and parsed
`service_state` so Bonfire UI/service code can surface item effects without
scraping raw files.

## Service-Side Session Coordinator

`BonfireService` watches every `Runtime\DS2Native\*.actions.jsonl` file after
startup. Existing action logs are treated as history; new action lines are
processed into a service-owned state snapshot:

```text
Runtime\DS2Native\<session>.service_state.json
```

Current service behavior:

- `session.create` marks the DS2 runtime as host mode and starts the local
  `Server.exe` if it is not already running. If the server was already open,
  the action records `server_already_running` and leaves it alone.
- `session.join` marks guest mode and records that a Bonfire host link is armed.
  The current DS2 network stack still needs the game to be launched against the
  target Bonfire server; this is the first control-plane state, not yet a
  mid-game server retarget.
- `session.invade` records private invasion intent for later matching work.
- `session.leave` returns the service state to solo mode. It only stops
  `Server.exe` if that server was started by the in-game runtime item, avoiding
  accidental teardown of a manually launched Bonfire session.
- `rules.cycle`, `invasions.taunt`, `world.infection`, `curse.accrue`, and
  `world.recover` update counters/state in `service_state.json` for the next
  multiplayer behavior layer.

Every processed action also sends an unsolicited JSON-RPC notification named
`ds2_runtime.session`; the Flutter UI can subscribe through `RpcClient` without
polling.

The command inbox accepts the same stateful verbs (`session.create`,
`session.join`, `session.invade`, `session.leave`, `session.reconnect`,
`rules.cycle`, `invasions.taunt`, `world.infection`, `curse.accrue`,
`world.recover`, and `runtime.state`) so Bonfire UI controls and in-game custom
items share one runtime state machine.

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

DS1-style item set update on 2026-05-14:

- `ItemParam.param` now contains the nine DS1 Seamless-style Bonfire IDs
  `62061000` through `62061008`.
- The old prototype IDs are still recognized by the runtime but are marked
  `GrantAtBonfire=false`, so future rests deliver the DS1-style set instead.
- FMG text was patched in English, Spanish, and neutral Spanish for the nine
  current names and descriptions.

## Session Advertisement Layer (2026-05-15)

Once item-action emission was stable end-to-end, the next layer wired the
service-side session state into the master server so a peer Bonfire client can
discover the session before launch.

Module: `Source\BonfireService\Modules\Ds2NativeSession.cs`.

Protocol shape — the coordinator embeds a single-line manifest at the bottom of
`ServerConfig.ServerDescription`, behind a versioned sentinel:

```text
<user description>\n%%BNS-DS2-V1%%{"session_id":"...","mode":"host","stage":"service_host_online","intent":"cooperate_host","rules":"challenge","taunt":0,"infection":0,"curse":0,"recovery":0,"runtime_ver":1,"ts":"2026-05-15T..."}
```

`Server.exe` re-publishes `ServerName`/`ServerDescription` to the master server
every `Config.AdvertiseHearbeatTime` (default 30 s) via
`Server::PollServerAdvertisement`, so other Bonfire instances reading
`MasterServer.ListServersAsync` see the manifest verbatim. Any client that
imports `Ds2NativeSession` can parse it with
`Ds2NativeSession.TryParseFromDescription(...)`.

Stamping rules in `Ds2NativeSessionCoordinator.StampManifestIfChanged`:

- Builds a manifest from the current `SessionMemory` (mode, stage, intent,
  rule preset, counters, runtime version, timestamp).
- Embeds it via `EmbedInDescription`, preserving the user's free-text
  description above the sentinel line. On `session.leave` returning to solo,
  the sentinel line is stripped entirely so the master listing reads as plain
  again.
- Forces `cfg.Advertise = true` while in host mode.
- Dedupes by an `equality_key` derived from the same fields (excluding the
  timestamp) so timestamp-only refreshes do not trigger redundant writes.
- For `session.create`, the stamp runs **before** `EnsureLocalServerRunning`
  so a fresh `Server.exe` boot reads the manifest. For every other verb the
  stamp runs after the switch and is flagged
  `manifest_stale_pending_restart=true` since the running server keeps its
  boot-loaded config in memory.

The service-state JSON now exposes:

```text
advertised_manifest               parsed JsonObject of the stamped manifest
advertised_manifest_json          compact JSON string (sentinel payload)
advertised_equality_key           dedup key
advertised_stamped_at             ISO-8601 of last successful stamp
advertised_session_id             public session id (= runtime session id)
manifest_stale_pending_restart    true iff Server.exe boot predates the stamp
advertise_heartbeat_seconds       30 (informational, references Server.exe)
advertise_enabled                 mirror of cfg.Advertise after stamp
```

The Flutter UI in `Source\bonfire\lib\state\app_state.dart` strips the sentinel
from descriptions before display (`_stripBnsSentinel`) so the manifest is
invisible to humans but parseable by peer Bonfire clients.

## Native Runtime Hardening (2026-05-15)

While validating the advertisement layer end-to-end the following native
runtime issues were caught and resolved:

- `ResolveLastSelectedRuntimeItem`, `ResolveCurrentSelectedRuntimeItem`, and
  `InventorySelectedItemEntryHook` all rejected a Bonfire row when the live
  inventory entry's `+0x18 native_use_item_id` did not match the in-code
  `RuntimeGrantItem::NativeUseItemId`. Codex validated action emission against
  the legacy 62060001/62060002/60360001 rows whose in-code `NativeUseItemId`
  matched their prototype shells; the migration to 62061000..62061008 with
  self-referential IDs in the code table preceded the corresponding
  `ItemParam.param` rebuild, so live `+0x18` still reported vanilla shells
  (e.g. `62050000` for Crystal Eye Orb) and the resolution chain returned
  `nullptr`. The visible `item_id` at `+0x14` is now treated as authoritative
  for Bonfire-owned rows; the `native_id_matches` boolean is preserved in
  diagnostic events but no longer gates resolution. This aligns with the
  existing tolerance already present in `ItemUseValidationHook`.
- `RuntimeGrantItem::SuppressVanillaContinuation` is now `false` for the nine
  62061000..62061008 rows (kept `true` for the three legacy rows). The
  suppression at `DarkSoulsII.exe+0x500C40` was originally added to block the
  vanilla red-eye invasion search after using Abyssal Eye Orb's prototype
  (NativeUseItemId 62060000); the new rows inherit `Item Use Animation = 1700`
  / `Item State = 13` from the Bone of Order template (item id 62020000),
  which has no invasion-search side effects, so suppressing vanilla
  continuation served only to interrupt DS2's quickslot use-animation pipeline.
  Quickslot now plays the full vanilla animation while
  `HandleBonfireRuntimeItemUse` still emits the Bonfire-owned action.
- `ServerConfig.SaveOver` previously escaped only `\\` and `"` when writing
  values, so the literal newline inside the manifest line corrupted
  `config.json`; `nlohmann::json` in `Server.exe` then logged
  `Failed to load configuration file` and started with compile-time defaults,
  breaking the auth handshake. `ReplaceString`, `UpsertString`, and
  `ReadString` now go through `JsonEncodeString` / `JsonDecodeString`
  helpers that handle `\\` `"` `\n` `\r` `\t` `\b` `\f` `/` for both
  directions, preserving round-trip integrity for any free-form field.

Live validation 2026-05-15:

- Steam authenticated through the local `Server.exe` after the JSON escape
  fix (`Steam id 011000011773f8ab has logged in as player 1`).
- `actions.jsonl` emitted for every Bonfire item from both inventory and
  quickslot use paths.
- `config.json` carries a JSON-valid sentinel after each state change
  (`"ServerDescription": "A custom Dark Souls server.\n%%BNS-DS2-V1%%{...}"`
  with escaped `\n`).
- The Flutter bonfire list renders descriptions stripped of the sentinel
  line.

## DS2 Native Sessions Browser (2026-05-15)

The advertisement layer publishes the manifest to master; the browser
layer surfaces it back in the UI so peers can discover and target it.

Flutter side (`Source\bonfire\lib\state\app_state.dart` +
`Source\bonfire\lib\screens\home_screen.dart`):

- `Ds2NativeSessionManifest.tryParseFromDescription(String)` mirrors
  the C# parser. Each `PublicServer` carries an optional
  `bnsManifest` field populated from the raw description before
  `_stripBnsSentinel` cleans it for display.
- `_Ds2NativeSessionsSection` renders entries with non-null
  `bnsManifest` filtered to the DS2 tab. Each row shows:
  - Icon (host = fireplace, guest = person_add_alt, invader = globe)
  - Name + clean description
  - Mode chip (HOST / GUEST / INVADER / fallback) in palette-mapped
    color (accent / secondary / warn)
  - Rule preset chip
  - Non-zero counter chips (taunt / infection / curse / recovery)
  - `session <session_id>` subtitle line
  - Player count + Join button
- BNS entries are hidden from the generic `PUBLIC BONFIRES` section
  to keep the demarcation clean.

## Join Target Arming (2026-05-15)

Module: `Source\BonfireService\Modules\Ds2NativeJoinTarget.cs`.

Single-shot armed-target slot consumed by `game.launch_local`:

- `Ds2NativeJoinTarget.Set(JoinTarget)` stores the target with a
  best-effort sidecar at `Runtime\DS2Native\join_target.json` so a
  BonfireService restart between arming and launching does not lose
  the user's selection.
- `Consume()` reads-and-clears atomically, used at the top of
  `game.launch_local`.
- `Get()` and `Clear()` round out the API for the read-only inspector
  and user cancel.

RPC surface added to `Source\BonfireService\Rpc\Methods.cs`:

- `ds2_runtime.set_join_target {server_id, password?}` — looks up the
  server in `MasterServer.ListServersAsync`, validates
  `GameType==DarkSouls2`, validates the description carries the BNS
  sentinel via `Ds2NativeSession.TryParseFromDescription`, snapshots
  the entry and stores it via `Ds2NativeJoinTarget.Set`. Returns
  `{armed:true, target:{...}}`.
- `ds2_runtime.clear_join_target` — drops the armed target on user
  cancel.
- `ds2_runtime.get_join_target` — read-only inspector for the UI to
  render the armed-target banner.

`game.launch_local` consults `Ds2NativeJoinTarget.Consume()` right
after the Steam validation. When non-null:

1. Validates target `GameType` matches the launching profile.
2. Fetches the peer's public key via
   `MasterServer.GetPublicKeyAsync` (same path as `game.launch`
   uses for public servers), normalises CRLF→LF to satisfy
   `DS3_ReplaceServerAddressHook.k_key_length`.
3. Builds a `LaunchRequest` with target's hostname/port/public_key
   instead of the local profile's, leaves the local `Server.exe`
   running untouched (the guest might still be hosting their own
   session), spawns DS2 + injects, returns
   `{ok, pid, server_name, join_target, mode:"joined_peer_session"}`.

Flutter UI side:

- `AppState.ds2JoinTarget` carries the parsed snapshot via
  `refreshDs2JoinTarget()` (added to `refreshAll`) so persisted
  arming survives service restarts.
- `armDs2JoinTarget(serverId, password?)` / `clearDs2JoinTarget()`
  wrap the RPCs.
- `_Ds2NativeSessionRow` Join button is now live: clicking arms,
  re-clicking the now-`Cancel` button clears, the matching row gets
  a `TARGET ARMED` chip + `_HoverableRow.selected: true` highlight.
- `_Ds2JoinTargetBanner` renders between the runtime banner and
  `MY BONFIRES` whenever `ds2JoinTarget` is non-null, showing host
  name + host:port + session mode/id + a Cancel button so the
  arming can be backed out without scrolling to the BNS browser.

## Live Manifest Propagation (2026-05-15)

`Source\BonfireService\Modules\Ds2NativeWebUIPush.cs` closes the
`manifest_stale_pending_restart` gap left by the initial advertise
layer.

Problem the gap created: Server.exe loads `RuntimeConfig` once at
boot and re-broadcasts the in-memory `ServerName`/`ServerDescription`
on the 30 s heartbeat. Any in-game manifest mutation (mode change,
counter increment, rule cycle) was written to `config.json` on disk
but Server.exe never picked it back up — peers stayed on the snapshot
their host had loaded at boot.

Fix: every successful `StampManifestIfChanged` write now POSTs the
new `serverName`/`serverDescription` to Server.exe's WebUI
`/settings` endpoint (`Source\Server\Server\WebUIService\Handlers\
SettingsHandler.cpp`). That endpoint mutates Server.exe's in-memory
`Config` in place and calls `SaveConfig()`, so the next 30 s
heartbeat broadcasts the fresh manifest — no Server.exe restart
needed.

Authentication is the two-step WebUI handshake:

1. `POST /auth {username, password}` → `{token}`. Credentials come
   from `cfg.WebUIServerUsername` / `cfg.WebUIServerPassword`.
2. `POST /settings` + header `Auth-Token: <token>` with body
   `{serverName, serverDescription}`.

Server.exe normally only auto-generates the WebUI credentials on the
first boot of a non-default shard (`Server::Initialize` guarded on
`!IsDefaultServer()`); a single-profile install would otherwise stay
permanently unauthenticated. To fix this proactively,
`Ds2NativeWebUIPush.EnsureCredentialsInConfig()` runs from
`Ds2NativeSessionCoordinator.Start()` and populates `bonfire-<hex>`
/ `<guid hex>` values for any empty pair before Server.exe is next
spawned.

`StampManifestIfChanged` now decides staleness as:

```text
stampedAfterBoot = serverRunning && stamp.utc > server.StartedAt.utc
stale = stampedAfterBoot && (pushResult is null OR !pushResult.Success)
```

So the flag is only `true` when Server.exe is running, our stamp
genuinely post-dates its boot, AND the live push didn't go through.
The push outcome is surfaced under
`effect.manifest_stamp.live_push = {success, server_reachable, status, error?}`
in the service-state JSON so the UI can show granular diagnostics.

Live validation 2026-05-15:

- After redeploy and a single Bonfire item use,
  `service_state.json` showed `manifest_stale_pending_restart: false`
  and `live_push: {success:true, server_reachable:true,
  status:"pushed"}`.
- Master `/api/v1/servers` returned our `DS2 Native Probe` entry
  with the **current** `session_id` (matching the live DS2 PID) and
  `mode: guest` (matching the user's last Crystal Eye Orb use),
  timestamped at the exact moment of the item use — not the
  Server.exe boot snapshot.

## Long-Term Vision: HKMP-Style Overlay

The current stack (advertise / browser / join target / live push)
gives two Bonfire instances a shared `Server.exe` and a shared
session identity. What it does **not** yet do is make the players
visible to each other inside DS2's world the way Yui's DS3 Seamless
or Hollow Knight Multiplayer (HKMP — `C:\Users\Diux\Desktop\HKMP-master`
and `C:\Users\Diux\Desktop\HKMP-Entity-Sync-master`) do. That is
this project's eventual goal.

HKMP's architecture, applied to DS2 by analogy:

| HKMP layer | DS2 equivalent (target) |
| --- | --- |
| Injected `.dll` mod via Hollow Knight Modding API | Existing `Loader\Injector.dll` (DS2_NativeRuntimeHook.cpp + new sync hooks) |
| Each player owns their world independently (bosses, NPCs, doors are per-client) | Same — each DS2 keeps its own progression. The shared layer is overlay-only. |
| Real-time UDP/TCP packet stream of position, animation state, skin id, team | New `Source\Server.DarkSouls2\Server\GameService\GameManagers\PlayerSync\` manager + UDP fanout on the existing game port (50010), or a new sync port. Position + rotation + animation state at 20–30 Hz. |
| Server-side broadcast of each client's state to all peers | DS3OS `GameService` already manages connected clients; an additional message type for `PlayerSync` payloads would slot in alongside the existing protobuf message types. |
| Client-side spawn of "fake player" actor for each peer | This is the heavy lift. Each DS2 client needs to spawn a controllable actor (probably reusing the NPC phantom skeleton from summon sign placement) and drive its transform/animation from the received peer state every frame. Hook target: the chr/player update loop near the existing `0x17DC40` rest hook. |
| Entity sync (enemies, bosses, world state) — the separate `HKMP-Entity-Sync` repo | The "experimental path" called out in the original handoff. Higher complexity, must come after player sync is stable. |

Concrete first milestone for the overlay layer:

1. **Local player state read.** Identify the DS2 in-process pointer
   chain to the local player's transform (position, rotation,
   current animation state id). Validate live via Cheat Engine /
   x64dbg, like the existing `BobTable.GameManagerImp...0x70`
   pattern that drives the selected-entry cache. Document in a new
   `dsseamlesscoop-player-sync-map.md` reference.
2. **Outbound packet emission.** Add a runtime thread in
   `DS2_NativeRuntimeHook.cpp` that samples the local player state
   at 20–30 Hz and pushes JSONL / binary packets through a new
   sibling file (e.g. `<session>.player.jsonl`) or directly over
   UDP. Start with file-based so it's debuggable, switch to network
   once stable.
3. **Inbound packet ingest.** Bonfire-side coordinator reads peer
   packets from Server.exe and forwards to the local injector via
   the existing command inbox channel.
4. **Fake player spawn.** Hook DS2's NPC spawn path to inject a
   controllable actor at the received peer position. The actor's
   animation state is driven by the received payload each frame.
5. **Iterate** on packet rate, smoothing, animation coverage,
   damage handling (off by default, like HKMP's PvP toggle).

Everything before this milestone — the BNS infrastructure built
through 2026-05-15 — is the **control plane** that tells two
Bonfires they are sharing a session and lets them establish a
trusted server-mediated link. The overlay layer is the
**data plane** that actually makes their characters visible to
each other inside DS2. The control plane is now done; the data
plane is the next major project.
