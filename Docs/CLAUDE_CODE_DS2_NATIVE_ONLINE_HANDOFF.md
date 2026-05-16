# Claude Code Handoff - DS2 Native Bonfire Online

Last updated: 2026-05-15

This handoff is for Claude Code continuing Diux's Dark Souls II Scholar of the
First Sin private online experiment in `DSSeamlessCoop`.

## Short Version

Diux wants a DS2 Seamless-style online system, but not just the vanilla DS2
soapstone/private-server flow. The current direction is a Bonfire-owned native
runtime: DS2 gets custom command items, the injected runtime intercepts item
use, BonfireService receives those actions, and the service/UI/server layer
turns them into a private online control plane.

Do not restart from the old item/shop-only DS2 mod. That was an earlier
overhaul approach. The current work is the native runtime branch.

## Main Repository

```text
C:\Users\Diux\Desktop\DSSeamlessCoop
```

Current branch:

```text
experiment/ds2-native-runtime-custom-items
```

Latest pushed commit at handoff time:

```text
83325be0592a09b17aee68bc591f0179c8110f9d
Add DS2 runtime session bridge and native prompts
```

Remote:

```text
origin https://github.com/Galidar/DSSeamlessCoop.git
```

Recommended first commands:

```powershell
cd C:\Users\Diux\Desktop\DSSeamlessCoop
git status --short --branch
git log -3 --oneline --decorate
```

## Critical Codex Skill Memory

Read this first. It contains the latest successful DS2 custom item/runtime
recipe and the mistakes that must not be repeated.

```text
C:\Users\Diux\.codex\skills\ds2-native-coop\SKILL.md
C:\Users\Diux\.codex\skills\ds2-native-coop\references\ds2-bonfire-runtime-custom-items.md
C:\Users\Diux\.codex\skills\ds2-native-coop\references\native-patch-map.md
C:\Users\Diux\.codex\skills\ds2-native-coop\references\server-workflow.md
C:\Users\Diux\.codex\skills\ds2-native-coop\references\dsseamlesscoop-runtime-map.md
C:\Users\Diux\.codex\skills\ds2-native-coop\references\ds1-seamless-lessons-for-ds2.md
```

Run the read-only inspector:

```powershell
python C:\Users\Diux\.codex\skills\ds2-native-coop\scripts\inspect_ds2_native.py
```

## Other Skills Used Or Relevant

These are the skills that were used, referenced, or are directly useful for
continuing this project.

```text
C:\Users\Diux\.codex\skills\ds2-native-coop\SKILL.md
C:\Users\Diux\.codex\skills\ds2-overhaul-lab\SKILL.md
C:\Users\Diux\.codex\skills\ds2-bonfire-overhaul-integration\SKILL.md
C:\Users\Diux\.codex\skills\ds1-bonfire-seamless-integration\SKILL.md
C:\Users\Diux\.codex\skills\ds3-bonfire-seamless-integration\SKILL.md
C:\Users\Diux\.codex\skills\fromsoft-mod-analysis\SKILL.md
C:\Users\Diux\.codex\skills\ghidra-decompile-dll\SKILL.md
C:\Users\Diux\.codex\skills\native-dll-analysis\SKILL.md
C:\Users\Diux\.codex\skills\cheatengine\SKILL.md
C:\Users\Diux\.codex\skills\ds2-sotfs-cheat-table\SKILL.md
C:\Users\Diux\.codex\skills\ds-modengine\SKILL.md
C:\Users\Diux\.codex\skills\me2-modengine2\SKILL.md
C:\Users\Diux\.codex\skills\uxm-unpack\SKILL.md
C:\Users\Diux\.codex\skills\yabber\SKILL.md
C:\Users\Diux\.codex\skills\x64dbg-expert\SKILL.md
C:\Users\Diux\.codex\skills\magicmida-themida\SKILL.md
C:\Users\Diux\.codex\skills\codex-agent-pack\SKILL.md
C:\Users\Diux\.codex\skills\public-agent-library\SKILL.md
C:\Users\Diux\.codex\skills\.system\skill-creator\SKILL.md
C:\Users\Diux\.codex\plugins\cache\openai-curated\github\b8edb371\skills\yeet\SKILL.md
```

Use `ds2-native-coop` for native/runtime/server work. Use
`ds2-overhaul-lab` only for data/Param/FMG/package-style DS2 work. Use
`fromsoft-mod-analysis`, `ghidra-decompile-dll`, `native-dll-analysis`,
`x64dbg-expert`, and `cheatengine` when you need new address discovery or live
validation.

## Important Local Sources And Tools

```text
C:\Users\Diux\Desktop\Ds2NativeCoop
C:\Users\Diux\Desktop\DSSeamlessCoop
C:\Users\Diux\Desktop\DS Loader-1196-Latest-1738303226
C:\Users\Diux\Desktop\ghidra-decompile-dll
C:\Users\Diux\Desktop\cheatengine-mcp-bridge-main
C:\Users\Diux\Desktop\DarkSoulsScripting-DSR-x64
C:\Users\Diux\Desktop\SeamlessCoopExtension-main\SeamlessCoopExtension-main
```

Temporary/research tools that existed during this work:

```text
C:\Users\Diux\Desktop\DSSeamlessCoop\Temp\Ds2RegTool
C:\Users\Diux\Desktop\DSSeamlessCoop\Temp\external_repos\Paramdex
```

Do not assume temp tools are part of the release. They are for investigation,
Param dumps, row cloning, and verification.

## Safety And Scope Rules

- Use private servers only. Do not route patched/injected clients to FromSoft
  official servers.
- Do not bypass ownership checks or Steam tickets.
- Do not patch `DarkSoulsII.exe`, `regulation.bnd.dcx`, or saves in place
  without explicit user approval and a backup.
- Verify DS2 SOTFS Steam version before trusting native offsets.
- Keep research tools and distributable release artifacts separate.
- Preserve notices/licenses for DS3OS/DSSeamlessCoop-derived code.

## Current Architecture

Current flow:

```text
DS2 custom item use
  -> Source\Injector\Hooks\DarkSouls2\DS2_NativeRuntimeHook.cpp
  -> Runtime\DS2Native\<session>.actions.jsonl
  -> Source\BonfireService\Modules\Ds2NativeSessionCoordinator.cs
  -> Runtime\DS2Native\<session>.service_state.json
  -> JSON-RPC notification ds2_runtime.session
  -> Flutter UI banner in Bonfire
```

Important repo files:

```text
Docs\DS2NativeRuntime.md
Docs\CLAUDE_CODE_DS2_NATIVE_ONLINE_HANDOFF.md
Resources\Loader\DS2BonfireRuntime\Param\ItemParam.param
Resources\Loader\DS2BonfireRuntime\Param\ItemUsageParam.param
Resources\Loader\DS2BonfireRuntime\Param\ItemUseCheckDialogParam.param
Source\Injector\Hooks\DarkSouls2\DS2_NativeRuntimeHook.cpp
Source\BonfireService\Modules\Ds2NativeRuntimeBridge.cs
Source\BonfireService\Modules\Ds2NativeSessionCoordinator.cs
Source\BonfireService\Program.cs
Source\BonfireService\Rpc\Methods.cs
Source\bonfire\lib\state\app_state.dart
Source\bonfire\lib\screens\home_screen.dart
```

Packaged test runtime path:

```text
C:\Users\Diux\Desktop\DSSeamlessCoop\Source\bonfire\build\windows\x64\runner\Release
```

Runtime logs appear under:

```text
C:\Users\Diux\Desktop\DSSeamlessCoop\Source\bonfire\build\windows\x64\runner\Release\Runtime\DS2Native
```

## Current Custom DS2 Runtime Items

The current DS2 custom item IDs are:

```text
62061000 Blessed Eye Orb / Orbe del Ojo Bendecido        -> session.create
62061001 Crystal Eye Orb / Orbe del Ojo de Cristal       -> session.join
62061002 Chaos Eye Orb / Orbe del Ojo del Caos           -> session.invade
62061003 Abyssal Eye Orb / Orbe del Ojo Abisal           -> session.leave
62061004 Ominous Tome / Tomo Ominoso                     -> rules.cycle
62061005 Dried Fingers / Dedos Secos                     -> invasions.taunt
62061006 Cursed Pendant / Colgante Maldito               -> world.infection
62061007 Crimson Blossom / Flor Carmesi                  -> curse.accrue
62061008 Parchment of Deliverance / Pergamino de la Liberacion -> world.recover
```

They use DS2 vanilla icons but custom IDs, custom text, and Bonfire-owned
runtime behavior. This matches the important lesson from DS1/DS3 Seamless:
items can be "custom" by ID/text/runtime behavior while still using base-game
icons.

Do not copy DS1/DS3 item rows or textures directly into DS2. DS2 needs real
DS2-compatible item rows to draw and use inventory objects.

## What Is Already Proven In Game

Diux confirmed:

- The custom items are granted from a bonfire, not at new-game startup.
- The items appear in inventory/HUD/quickslot.
- The items can be used from inventory and quickslot.
- Use animations and particles play.
- Native DS2 confirmation prompts work after adding `ItemUseCheckDialogParam`
  rows.
- The items emit runtime actions after the direct selected-entry probe fix.
- The UI can show Bonfire-side state through the DS2 runtime banner.

The latest code also suppresses native continuation after Bonfire runtime item
actions so custom items do not fall through into old red/blue eye orb or effigy
behavior.

## Item Data Lessons That Matter

Do not reintroduce the old mistake:

- The original DS2 mod delivered items at startup through
  `PlayerStatusItemParam`.
- Another old path sold items through `ShopLineupParam`.
- The current native runtime must not depend on either of those.

Current package rule:

```text
PlayerStatusItemParam.param: no 62061000..62061008 grants
ShopLineupParam.param:       no 62061000..62061008 shop entries
ItemParam.param:             contains the nine Bonfire item rows
ItemUsageParam.param:        contains custom usage support
ItemUseCheckDialogParam.param: rows 62061000..62061008 clone Bone of Order prompt
```

Native confirmation prompt:

```text
Bone of Order item row: 62020000
ItemUseCheckDialogParam source row: 62020000
Prompt FMG id: 30000000
Expected custom rows: 62061000..62061008
Expected fields: Unk00=1, Unk04=30000000, Unk08=0, Unk0C=0, Unk10=0
```

Important correction:

```text
60360000 is Darksign, not Bone of Order.
```

## Native Hook Details

Main hook file:

```text
Source\Injector\Hooks\DarkSouls2\DS2_NativeRuntimeHook.cpp
```

Known important native points:

```text
DarkSoulsII.exe+0x1B19D0  selected item category/action candidate
Return RVA 0x32FF15       highlighted usable item path
DarkSoulsII.exe+0x500C40  next native continuation after selected-category call
Return RVA 0x32FF24       continuation path to suppress after Bonfire action
DarkSoulsII.exe+0x2D3B20  item use validation helper, useful diagnostic path
```

Selected inventory entry details:

```text
vtable slot +0x70 gives selected inventory entry
entry +0x14 = visible custom item id
entry +0x18 = native use item id
entry +0x20 roughly quantity
```

The latest fix added `ResolveCurrentSelectedRuntimeItem(...)` so the hook can
probe the selected item directly during the `0x1B19D0` action candidate path
when the recent selected-entry cache is missing.

Useful runtime events:

```text
inventory.selected_runtime_item
inventory.selected_runtime_item_direct_probe
inventory.selected_action_candidate
bonfire.custom_item_use
bonfire.custom_item_action
item_use.validation
inventory.selected_action_execute
```

## BonfireService State

Coordinator:

```text
Source\BonfireService\Modules\Ds2NativeSessionCoordinator.cs
```

Current behavior:

```text
session.create     host mode, start/keep local DS2 private Server.exe
session.join       guest mode armed
session.invade     private invasion intent recorded
session.leave      solo/closed; stops only server started by runtime item
rules.cycle        record rule preset
invasions.taunt    record invasion beacon counter
world.infection    record world mutation counter
curse.accrue       record curse pressure counter
world.recover      record recovery counter
```

Status RPC:

```text
ds2_runtime.status
```

Notifications:

```text
ds2_runtime.session
ds2_runtime.session_error
```

State files:

```text
<session>.events.jsonl
<session>.actions.jsonl
<session>.messages.jsonl
<session>.state.json
<session>.service_state.json
```

## Build And Validation Commands

Service build:

```powershell
dotnet build Source\BonfireService\BonfireService.csproj -c Release -r win-x64
```

Service publish:

```powershell
dotnet publish Source\BonfireService\BonfireService.csproj -c Release -r win-x64 --self-contained true
```

Flutter build:

```powershell
cd C:\Users\Diux\Desktop\DSSeamlessCoop\Source\bonfire
flutter build windows --release
flutter analyze
```

Injector build:

```powershell
& 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe' /m /p:Configuration=Release /p:Platform=x64 intermediate\vs2022\Source\Injector\Injector.vcxproj
```

Verify service version from packaged build:

```powershell
C:\Users\Diux\Desktop\DSSeamlessCoop\Source\bonfire\build\windows\x64\runner\Release\BonfireService.exe --version
```

Expected at this handoff:

```text
Bonfire.Service 2.6.0
```

Verify native prompt rows:

```powershell
dotnet run --project C:\Users\Diux\Desktop\DSSeamlessCoop\Temp\Ds2RegTool\Ds2RegTool.csproj -- dump-param `
  C:\Users\Diux\Desktop\DSSeamlessCoop\Resources\Loader\DS2BonfireRuntime\Param\ItemUseCheckDialogParam.param `
  C:\Users\Diux\Desktop\DSSeamlessCoop\Temp\external_repos\Paramdex\DS2S\Defs\ITEM_USE_CHECK_DIALOG_PARAM.xml `
  62061000 62061001 62061002 62061003 62061004 62061005 62061006 62061007 62061008
```

Copy prompt Param to packaged runtime before live testing:

```powershell
Copy-Item `
  C:\Users\Diux\Desktop\DSSeamlessCoop\Resources\Loader\DS2BonfireRuntime\Param\ItemUseCheckDialogParam.param `
  C:\Users\Diux\Desktop\DSSeamlessCoop\Source\bonfire\build\windows\x64\runner\Release\Loader\DS2BonfireRuntime\Param\ItemUseCheckDialogParam.param `
  -Force
```

Launch Bonfire test build:

```powershell
Start-Process -FilePath C:\Users\Diux\Desktop\DSSeamlessCoop\Source\bonfire\build\windows\x64\runner\Release\bonfire.exe
```

## What Claude Should Do Next

The next work is not "add more item rows". The item layer is now good enough to
be the control plane. The next work is making those commands produce real
private online behavior.

Recommended next phase:

1. Keep the current nine custom items and their action contract stable.
2. Make `session.create` create a host session that Bonfire can advertise and
   that another Bonfire client can target before DS2 launch.
3. Make `session.join` bind the local DS2 runtime to a selected Bonfire host
   rather than only recording "guest armed".
4. Reuse the existing DSSeamlessCoop/DS3OS private server path for DS2 server
   redirect, RSA/public key replacement, and login/matching where possible.
5. Add a real "world/session identity" protocol in BonfireService so both
   players share a session id and the DS2 runtime can know who is host/guest.
6. Decide how to represent the other player in DS2:
   - First practical path: use existing DS2 private server/matching managers,
     bend them with Bonfire-owned session rules, and avoid official servers.
   - Experimental path: build a fake-world overlay where BonfireService syncs
     position/action state and DS2 displays a controlled actor/ghost/proxy.
     This is harder and requires live memory work.
7. Attach item functions to actual multiplayer state:
   - Blessed Eye Orb: create host lobby/server/session.
   - Crystal Eye Orb: join selected private session.
   - Chaos Eye Orb: request private invasion into available Bonfire session.
   - Abyssal Eye Orb: leave/disband current Bonfire session.
   - Ominous Tome: sync rules to all clients.
   - Dried Fingers: allow more invaders/guests.
   - Cursed Pendant: apply shared world mutation state.
   - Crimson Blossom: increase shared challenge/curse state.
   - Parchment of Deliverance: recover ally/session/world state.
8. Add UI in Bonfire to show available DS2 native sessions and bind the
   selected target to `session.join`.
9. Keep validating every native assumption with DS2 live logs and, when needed,
   Cheat Engine/Ghidra/x64dbg.

## Known Gaps

- `session.join` currently records guest/link armed state; it does not yet
  mid-game retarget DS2 to a host.
- `session.invade` currently records private invasion intent; matching behavior
  is not fully implemented.
- The service-side session state is real, but the multiplayer gameplay layer is
  still the next build target.
- Native DS2 actor/ghost/proxy spawning for a fully fake online overlay is not
  solved yet.
- Current UI banner is a status surface, not a complete DS2 session browser.

## Debug Checklist For Claude

If custom items appear at new game startup:

```text
Check PlayerStatusItemParam.param. The custom item IDs should not be there.
```

If custom items appear in a shop:

```text
Check ShopLineupParam.param. The custom item IDs should not be there.
```

If items use but do not show native DS2 prompt:

```text
Dump ItemUseCheckDialogParam.param for 62061000..62061008.
They should clone 62020000 / common.fmg 30000000.
Copy the patched Param into the packaged runtime and relaunch DS2.
```

If item use animates but no action log appears:

```text
Check Runtime\DS2Native\*.events.jsonl for:
inventory.selected_runtime_item_direct_probe
inventory.selected_action_candidate
bonfire.custom_item_action
```

If DS2 shows vanilla invasion/search UI after using a Bonfire item:

```text
Inspect the 0x500C40 suppression path in DS2_NativeRuntimeHook.cpp.
The runtime should suppress native continuation after Bonfire custom actions.
```

If Bonfire UI does not update:

```text
Check BonfireService is running.
Check <session>.actions.jsonl has new lines.
Check <session>.service_state.json exists.
Check ds2_runtime.status returns service_state.
Check Source\bonfire\lib\state\app_state.dart notification handling.
```

## Commit Style Diux Likes

Before committing:

```powershell
git status --short --branch
git diff --stat
git diff --cached --check
```

Use detailed commit bodies with:

```text
Observed diagnostic
Root cause
Fix
Validation
Version changes
Reload/operator note
AI-Agent: Codex GPT-5.5 (xhigh reasoning)
Co-Authored-By: Codex <noreply@openai.com>
```

Stage only intended files. Do not commit generated build output unless the
release process explicitly needs it.

## Human Context

Diux is trying to escape the limits of vanilla DS2 multiplayer items and build
something closer in spirit to Yui-style DS1/DS3 Seamless: custom command items,
private sessions, and eventually a more seamless/fake online layer that feels
like both players are in a shared world.

The user is comfortable with deep reverse engineering, Ghidra, Cheat Engine,
x64dbg, DS3OS, ModEngine, Param editing, and live game tests. Be direct, keep
the work moving, and preserve discoveries in skills/docs so progress is never
lost.

## Claude Continuation Log

### 2026-05-15 — Session advertisement layer + native runtime hardening

Closed items from `What Claude Should Do Next` and follow-ups discovered while
validating live:

1. **Session advertisement (handoff step 2):** new
   `Source\BonfireService\Modules\Ds2NativeSession.cs` defines the
   `%%BNS-DS2-V1%%`-sentinel manifest embedded in `ServerConfig.ServerDescription`.
   `Ds2NativeSessionCoordinator` now stamps the manifest after every state
   change (`session.create` runs the stamp before `EnsureLocalServerRunning`
   so a fresh `Server.exe` boot inherits it; other verbs stamp post-switch
   and mark `manifest_stale_pending_restart=true`). Service state exposes
   `advertised_manifest`, `advertised_equality_key`, `advertised_stamped_at`,
   `manifest_stale_pending_restart`, `advertise_heartbeat_seconds`,
   `advertise_enabled`. `Server.exe` re-publishes the new
   `ServerDescription` to the master server on its 30s heartbeat.

2. **Native runtime regressions caught:**
   - `DS2_NativeRuntimeHook.cpp` rejected Bonfire rows when
     `+0x18 native_use_item_id` did not match the in-code
     `RuntimeGrantItem::NativeUseItemId`. The 9-item migration to
     `62061000..62061008` updated the in-code table to self-referential IDs
     but the shipping `ItemParam.param` still carries the prototype shells
     (live `+0x18 == 62050000` for Crystal Eye Orb etc.). The three rejection
     sites (`ResolveLastSelectedRuntimeItem`,
     `ResolveCurrentSelectedRuntimeItem`,
     `InventorySelectedItemEntryHook`) now treat the visible `+0x14 item_id`
     as authoritative; `native_id_matches` survives as a diagnostic field.
   - `SuppressVanillaContinuation` flipped to `false` for the nine
     `62061000..62061008` rows (legacy rows keep `true`). The Bone of Order
     template the new rows inherit has no invasion-search side effect, so
     suppressing vanilla continuation at `0x500C40` only broke the DS2
     quickslot use-animation pipeline. Quickslot now plays the full vanilla
     animation while `HandleBonfireRuntimeItemUse` still emits the
     Bonfire-owned action.

3. **JSON escape bug in `ServerConfig.SaveOver`:** the regex-based
   `ReplaceString` only escaped `\\` and `"`, so the manifest's literal `\n`
   between the user description and the sentinel produced an unescaped
   newline inside a JSON string. `nlohmann::json` in `Server.exe` rejected
   the file (`Failed to load configuration file`), `Server.exe` came up on
   compile-time defaults, and DS2 dropped to `Error al iniciar sesion`. New
   `JsonEncodeString`/`JsonDecodeString` helpers in `ServerConfig.cs` handle
   `\\ " \n \r \t \b \f /` in both directions and feed
   `ReplaceString`/`UpsertString`/`ReadString`.

4. **Flutter UI cleanup:** `Source\bonfire\lib\state\app_state.dart` strips
   the `%%BNS-DS2-V1%%` line from `ServerConfig.description` and
   `PublicServer.description` before they reach the UI, so the manifest is
   invisible to humans but still parseable by peer Bonfire clients reading
   `MasterServer.ListServersAsync`.

5. **Tooling:** Flutter SDK 3.41.9 stable was installed at
   `C:\Users\Diux\flutter` (git clone --depth 1). The
   `windows-elevated-kill` personal skill is documented and exercised via
   `Start-Process powershell.exe -Verb RunAs -Wait` with batched PID kill;
   the false-negative exit-code-1 from `-Verb RunAs -Wait` requires a
   `Get-Process -Id` verification step.

Live validation 2026-05-15:

- Steam auth succeeds end-to-end (`Steam id 011000011773f8ab has logged in
  as player 1`).
- All nine items (`62061000..62061008`) emit their mapped command from
  both inventory and quickslot.
- `service_state.json` flips between `session_mode=host` and
  `session_mode=guest` on Blessed/Crystal Eye Orb use.
- `config.json` ServerDescription stamps the sentinel with valid JSON
  escaping, parsed cleanly by `Server.exe` at boot.
- Flutter bonfire list shows the clean description (no sentinel leak).

### What's next (still open)

- **Browser DS2 + Join binding (handoff steps 3, 4, 8):** add a section to
  the DS2 tab that filters `servers.list` to entries whose `description`
  contains `%%BNS-DS2-V1%%`. Parse the manifest with `Ds2NativeSession` (Dart
  port required, or surface the parsed JSON from BonfireService). Selecting
  an entry should arm the join target so the next `game.launch` redirects to
  it; the in-game Crystal Eye Orb should bind to that target instead of just
  recording `guest_link_armed`.
- **Param self-referential migration:** the `tolerant native_id` hook
  changes are a workaround for stale `ItemParam.param` rows. The doc's
  official shape (Effect ID = Item Usage ID = visible row ID) still needs
  the `Ds2RegTool` clone pass to remove the `+0x18 = 62050000` artifact.
- **Real invasion matching (step 7c):** wire `session.invade` into
  `Source\Server.DarkSouls2\Server\GameService\GameManagers\BreakIn\` so
  Chaos Eye Orb actually triggers a private-only invasion query.
- **Multiplayer effects (steps 7d-h):** rules.cycle / dried fingers /
  cursed pendant / crimson blossom / parchment counters are still
  service-side bookkeeping only.

### 2026-05-15 (continued) — Browser, join target, live push, HKMP vision

Four additional commits landed during the same session, pushing the
project from "manifest stamped to disk" through to "live state visible
to peers" and laying out the long-term overlay direction.

1. **DS2 NATIVE SESSIONS browser** (`75bd65d`). Closes handoff step 8.
   New Dart `Ds2NativeSessionManifest.tryParseFromDescription` mirrors
   the C# parser; `PublicServer.bnsManifest` is populated alongside
   the stripped display description.
   `_Ds2NativeSessionsSection` renders a dedicated section between
   `MY BONFIRES` and `PUBLIC BONFIRES` on the DS2 tab, hides the same
   entries from the generic public list, and shows mode/rules/counter
   chips + session_id per row. Join button shipped disabled with a
   "next iteration" tooltip in this commit.

2. **Join target arming, service backend** (`0e5fdcf`). Closes handoff
   step 3 (DS2 binds to a selected host before launch).
   New `Source\BonfireService\Modules\Ds2NativeJoinTarget.cs` holds a
   single-shot target with disk persistence at
   `Runtime\DS2Native\join_target.json`. Three RPC methods:
   `ds2_runtime.set_join_target` / `clear_join_target` / `get_join_target`.
   `game.launch_local` calls `Consume()` right after the Steam check;
   when armed, it fetches the peer's public key via
   `MasterServer.GetPublicKeyAsync` (same path as the public-server
   `game.launch` already used — closes handoff step 4) and builds a
   `LaunchRequest` against the target's hostname/port/key. Returns
   `mode: "joined_peer_session"` for telemetry. Local Server.exe is
   intentionally left running so a host can still advertise while
   guesting elsewhere.

3. **Join target arming, Flutter UI** (`50b3374`). Wires the Join
   button.
   `AppState.ds2JoinTarget` plus `refreshDs2JoinTarget` /
   `armDs2JoinTarget` / `clearDs2JoinTarget`. The
   `_Ds2NativeSessionRow` Join button is now live with a SnackBar
   on success/error and morphs into a Cancel button when the row's
   server matches the armed target. New `_Ds2JoinTargetBanner`
   between the runtime banner and `MY BONFIRES` so the armed state
   is visible without scrolling.
   Validated end-to-end with a self-arm: `armed_at_utc` matched the
   click, `join_target.json` materialised, the next launch returned
   `mode: "joined_peer_session"` and the sidecar was deleted
   post-`Consume()`.

4. **Live manifest propagation** (`39ba0f6` + `88bc11a`). Closes the
   `manifest_stale_pending_restart` gap left behind by 2105e86.
   New `Source\BonfireService\Modules\Ds2NativeWebUIPush.cs` does
   the two-step WebUI handshake (`POST /auth` → token → `POST
   /settings` with `Auth-Token` header) so Server.exe's in-memory
   `RuntimeConfig.ServerName`/`ServerDescription` get updated in
   place every time `StampManifestIfChanged` writes the disk. The
   next ~30 s heartbeat carries the fresh manifest to the master,
   no Server.exe restart required.
   The follow-up commit (`88bc11a`) handles the chicken-and-egg
   that `Server::Initialize` only auto-generates `WebUIServerUsername`/
   `WebUIServerPassword` on the **first** boot of a **non-default**
   shard — a single-profile install would otherwise stay
   unauthenticated forever. `Ds2NativeSessionCoordinator.Start`
   now calls `EnsureCredentialsInConfig()` so a fresh
   `bonfire-<hex>` / `<guid>` pair is populated before Server.exe
   is next spawned.
   Live validation: master `/api/v1/servers` returned our `DS2 Native
   Probe` entry with `session_id` matching the current DS2 PID
   (`..._33208`, not the boot snapshot) and `mode: guest` matching
   the user's last Crystal Eye Orb use, timestamped at the exact
   moment of that item use.

### Long-term direction (HKMP-style overlay)

The control plane is now done. The data plane — actually rendering
the other player inside DS2 so the experience matches Yui's DS3
Seamless / Hollow Knight Multiplayer — is the next major project.
Reference repos on the user's machine:

```text
C:\Users\Diux\Desktop\HKMP-master\HKMP-master
C:\Users\Diux\Desktop\HKMP-Entity-Sync-master\HKMP-Entity-Sync-master
```

HKMP works by injecting a mod DLL that samples local player state
each frame, sends it to a standalone server, and spawns "fake
player" actors driven by inbound peer state on each client. Each
player owns their world independently; only the avatars are shared.
The DS2 mapping is documented in detail in
`Docs\DS2NativeRuntime.md` under "Long-Term Vision: HKMP-Style
Overlay" with a concrete first-milestone breakdown (local-state
read → outbound packet emission → inbound ingest → fake-player
spawn → iterate on rate / smoothing / animation coverage).

The infrastructure built through 2026-05-15 (BNS sentinel, browser,
join target, live push, manifest-driven session identity) is the
session-discovery layer that the overlay layer will run on top of:
two Bonfires already agree on which `Server.exe` they share, who
is host vs guest, and what rule preset is active. The overlay layer
just adds an additional message type for player-position packets
and a new client-side actor spawn path.

### Next-session priorities

1. **Experimental release bump** — tag a `v2.6.x-experimental` (or
   v2.7.0-experimental) with the six commits above so the user's
   brother can update Bonfire on his second PC and join the live
   test. Use the existing in-app updater (`AppUpdater.cs`).
2. **Real 2-PC test** — once both Bonfires are at the same version,
   host on PC A, browse + Join from PC B, validate the join target
   actually redirects DS2 through master and both players appear
   in `server.log` as connected clients to the same `Server.exe`.
3. **session.invade real matching** — wire Chaos Eye Orb into
   `Source\Server.DarkSouls2\Server\GameService\GameManagers\
   BreakIn\` so the orb actually triggers a private-only invasion
   match. First item with a multiplayer side effect beyond
   bookkeeping counters.
4. **Param self-referential migration** — clean up the
   `+0x18 = 62050000` artifact via `Ds2RegTool`, remove the tolerant
   `native_id` workaround from `DS2_NativeRuntimeHook.cpp`.
5. **HKMP-style overlay milestone 1** — local player state read
   (position, rotation, animation state id). Live-validate with
   Cheat Engine. Document in a new
   `dsseamlesscoop-player-sync-map.md` reference.
