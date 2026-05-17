# HKMP-Style Overlay — Research Baseline (2026-05-15 → 2026-05-16)

Captured during the live 2-PC LAN test with my brother on the
`experiment/ds2-native-runtime-custom-items` branch (v2.7.1-experimental).
This document is the empirical ground truth I built up while two real
DS2 clients were summoned together via white sign soapstone. It is the
starting point for "Long-Term Vision: HKMP-Style Overlay" in
`DS2NativeRuntime.md` and `dsseamlesscoop-runtime-map.md`.

## Status snapshot (2026-05-16, post-Phase-3)

| Phase | Status | Commit | Outcome                                          |
|-------|--------|--------|--------------------------------------------------|
| 1 — render-hook chain (CreateDevice → Factory → Present) | DONE  | earlier | per-frame callback inside DS2's render thread    |
| 2 — screen-space overlay quad + full pipeline save/restore | DONE  | `3a0ae6c` | magenta line/banner drawn each frame, lighting engine unaffected |
| 3 — world-space VP capture + 3D cube anchor             | DONE  | `806f6ad` (v14) → `979ddfc` (v15) | live VP read from DS2 memory, 3D cube sits on host's feet, world-anchored, rotates with camera |
| 4a — multi-actor rendering (cubes for N peers)          | DONE  | `76f0943` (v16) | host magenta cube + ghost cyan cube at host+5m, validated visually + telemetry (2 cubes/frame) |
| 4b — IPC for peer-pose table (commands.jsonl handler)   | DONE  | `39a6c77` (v17) + `afac6e1` (v18) | append `render.set_peer_poses` lines and renderer draws / clears in <100ms |
| 4c — UDP backbone (BonfireService bridge)               | DONE  | `4629897` (v0) + `24c8d3e` (v1) | loopback test passed in-game — ghost cube follows host with ~150ms round-trip lag |
| 4d — LAN test with second PC                             | DONE   | `f580f29` (v2.8.5) end-of-chain | both PCs see each other's cubes in DS2 (host + brother on same LAN, 2026-05-16) |
| 5 — replace cubes with character meshes                  | TODO   | —       | port the FLVER reader (or use a placeholder humanoid) so peers look like players |
| 6 — animation sync                                       | TODO   | —       | probe ChrIns+0x200..0x600 for the anim_id u32; broadcast alongside pose |

## 1. What the saponita (white sign soapstone) actually does

The brother appeared in the host's world over LAN. The server.log
captured the full handshake — every part of it goes through Server.exe
*as a matchmaker only*. The actual gameplay state (movement, animation,
combat) is NOT relayed by Server.exe.

### 1.1 Server.exe roles (what it does)

From `Source/Server.DarkSouls2/Server/GameService/GameManagers/Signs/DS2_SignManager.cpp`
and `DS2_PlayerDataManager.cpp`:

- `RequestCreateSign` — guest uploads a `player_struct` blob + the
  matching parameters (soul_level + soul_memory).
- `RequestGetSignList` — host polls for nearby signs that match its
  Soul Memory tier; server returns sign IDs + matching params +
  `player_struct` per sign.
- `RequestSummonSign` — host activates a specific sign; server validates
  the binary `player_struct` (the DS2 NRSSR sanitizer is a no-op stub —
  see `DS2_NRSSRSanitizer.h:94`, the DS2 format hasn't been deciphered)
  and pushes `PushRequestSummonSign` to the originating guest with the
  host's `player_struct`.
- `RequestUpdatePlayerStatus` — every 300s (5 min default, clamped
  60–50000 by `player_status_send_delay`) each connected DS2 client
  uploads its **entire** `AllStatus` blob: position, area, name,
  covenant, soul_memory, equipment, stats, etc.

### 1.2 The `player_struct` blob

Defined as `required bytes player_struct = …` in every sign/breakin
protobuf message. It is NOT a free-form blob — it's the game's internal
**NRSessionSearchResult** struct with these constants (from
`DS2_NRSSRSanitizer.h`):

```text
SIGNATURE        = 0x5652584E
VERSION_NUMBER   = 0x8405
SESSION_DATA_SIZE = 8 bytes   ← Steam lobby ID (CSteamID)
HOST_ONLINE_ID_SIZE = 8 bytes ← CSteamID of the host
MAX_PROP_WSTR_SIZE  = 1024 chars
MAX_NAME_WSTR_SIZE  = 256 chars
```

So `player_struct` carries the **Steam lobby ID** that lets the two
clients find each other over Steam P2P after matchmaking. Once both
clients have that 8-byte lobby ID, the gameplay traffic flows over
Steam Networking, not over our server.

### 1.3 What this means in plain terms

**Server.exe is a matchmaker + low-rate state sync + Steam-lobby
broker. The actual coop session runs as a Steam P2P relay between the
two paired DS2 clients.** This matches Hollow Knight's setup: a small
broker establishes peer connections, then players talk peer-to-peer.

The 2-PC LAN test confirmed this end-to-end:

- 22:58:12 — brother authenticated, Game Service redirected him to our
  private IP because we're on the same /24 subnet
  (server.log:22:58:12.668).
- 22:59:34 onward — brother and host both lit the same bonfire (10655
  Cardinal Tower) two minutes apart, proving they're in the same
  instance.
- `2 players | 1 servers` reported continuously for 15+ min without a
  hiccup.

## 2. PlayerData protobuf schema (sync surface that already exists)

From `Protobuf/DarkSouls2/DS2_Frpg2PlayerData.proto`:

```proto
message AllStatus {
    optional PlayerLocation player_location  = 1;
    optional PlayerStatus   player_status    = 2;
    optional ItemUsingInfo  item_using_info  = 3;
    optional StatsInfo      stats_info       = 4;
    optional LevelStatus    attributes       = 5;
    optional PhysicalStatus physical_status  = 6;
    optional WeaponStatus   weapon_status    = 7;
    optional ArmorStatus    armor_status     = 8;
    optional ServerSideStatus server_side_status = 9;
    optional EquipmentInfo  equipment_info   = 10;
}

message PlayerLocation {
    optional uint32 online_area_id           = 1;
    optional uint32 cell_id                  = 2;
    optional uint32 online_activity_area_id  = 3;
    optional Vector position                 = 4;  // ← x/y/z float
    optional float  unknown_5                = 5;
}

message Vector { required float x = 1; required float y = 2; required float z = 3; }

message PhysicalStatus {
    optional uint32 health = 1;
    optional uint32 stamina = 2;
    optional float  equip_load = 3;
    // ... +20 more
    optional float  poise = 24;
}
```

**Key takeaway:** position (Vec3) is already serialized on the wire, but
at 1/300 Hz. To do HKMP-style overlay we don't need to invent the
schema — we need to:

1. Massively crank the send rate (≥30 Hz).
2. Add rotation + animation_state + per-bone if we want full visual
   fidelity.
3. Server.exe needs to *broadcast* received states to other clients in
   the same `online_area_id` (currently it only stores them for
   matchmaking lookups).
4. Client needs a "fake actor spawn" hook to render N received states
   as visible characters.

## 3. Live pointer chain in DS2 (from inventory.probe event)

Captured at 2026-05-16T03:18:29Z while the user was in-game (PID 6500):

```text
DarkSoulsII.exe base       = 0x7FF70CE20000   (image size 30,892,032)
GameManagerImp static slot = 0x7FF70E4348F0
  → +0x00 = 0x7FF4366B0260  (GameManagerImp object)
    → +0xA8 = 0x7FF43EAB65A0  (owner)
      → +0x10 = 0x7FF43EB0CF60  (node_a)
        → +0x10 = 0x7FF43EB0D020  (node_b)
          → +0x10 = 0x7FF43EB1D180  (inventory object)
            inventory.vtable = 0x7FF70DEE4408
              vtable+0x30 = AdjustQuantity   → 0x7FFBFB689FF0
              vtable+0x38 = UseItem          → 0x7FFBFB68BBB0  (hooked → 0x7FF70CFD3830)
              vtable+0x70 = GetSelected      → 0x7FFBFB68BA20  (hooked → 0x7FF70CFD19A0)
```

The signature scan that resolves `GameManagerImp` (from
`DS2_NativeRuntimeHook.cpp:68`):

```text
48 8B 05 ?? ?? ?? ??    mov rax, [rip+disp32]
48 8B 58 38             mov rbx, [rax+0x38]
48 85 DB                test rbx, rbx
74 ??                   jz short
F6 …                    test ...
```

So the inventory tree is reachable as
`GameManagerImp → +0xA8 → +0x10 → +0x10 → +0x10 → inventory`.

The **player world entity** lives off a different child of
`GameManagerImp` (not 0xA8). We need to enumerate `GameManagerImp +
0x00…0x200` for pointers that lead to character/transform-like data.
Concrete TODO in milestone 1.

## 4. Known DS2 SOTFS 1.0.3.0 anchors

From `DS2_NativeRuntimeHook.cpp:36–67`:

| RVA       | Symbol                                            |
|-----------|---------------------------------------------------|
| `0x17DC40`| RestAtBonfire callsite                             |
| `0x2D3B20`| EyeOrbUse validation                               |
| `0x1AC3D0`| ItemGive                                           |
| `0x05D950`| ItemStructConvert                                  |
| `0x501080`| ItemPopupDisplay                                   |
| `0x1B19D0`| InventorySelectedItemCategory                      |
| `0x500C40`| InventorySelectedActionExecute                     |
| `0x32FF15`| Selected-action candidate caller return RVA        |
| `0x32FF24`| Selected-action execute caller return RVA          |
| `0x1B281C`| Inventory entry enumeration return RVA             |

Binary identity (SHA256 = `0045931B8914504531B7864A9488D396DC50CBAF524964016E1D69C3D1173131`,
size 28,200,992 bytes) — confirmed live.

## 5. Three architecture paths for HKMP-style overlay

| Path | Approach | Latency | Engineering cost | Risk |
|------|----------|---------|------------------|------|
| **A. Server-relayed fast lane** | Define new protobuf `PushPlayerStateFast` (pos+rot+anim+ts); Server.exe broadcasts to all clients with matching `online_area_id`. Hook DS2 to send at 30Hz + receive into a fake-actor queue. | ~50-100ms (client→server→client) | Medium — uses existing protobuf transport | Low — additive |
| **B. Steam P2P tee** | Hook DS2's Steam P2P send/recv at the API layer (`SteamNetworking::SendP2PPacket`); mirror packets through Bonfire's transport for N-way replication. | ~20-50ms LAN | High — Steam API hooking is dense | Medium — risks anticheat-like signals |
| **C. Client-side fake actor spawn** | Memory-write phantom slots into DS2 to spawn ghosts driven by external feed; bypass the 4-phantom-slot cap if possible. | ~immediate (local render) | Very high — heaviest game-side mod | High — phantom cap may be hard-coded |

Recommendation: start with **Path A** because it reuses the protocol
DS2 already speaks. Path A milestone 1 = "local player transform read"
+ "server-side echo back to self over the new fast-lane message" — once
that round-trip works at 30Hz, we already have HKMP-grade infra; only
fake-actor rendering remains.

## 6. Milestone 1 — Local player transform read

Two parallel tracks. Either alone gives partial answers; together they
cross-validate.

### Track 1 — Server-side protobuf telemetry (no DS2 restart)

Add a debug switch to `DS2_PlayerDataManager::Handle_RequestUpdatePlayerStatus`
that dumps the decoded `AllStatus` to a JSONL log next to `server.log`.
This gives us — **right now, from any active session** — the actual
in-game positions for both connected players, with no offset discovery
required.

Pros:
- Zero DS2-side code, no restart, no risk to current 2-PC test.
- Confirms the Vec3 magnitudes and coordinate system (does DS2 use
  meters? game units? what's the Y axis?).
- Tells us how often the client *actually* sends (vs the 300s configured
  default — the client may push extra updates on area transitions).

Cons:
- 300s is far too slow for sync; tells us the schema but not high-rate
  behavior.

### Track 2 — Client-side memory probe (requires injector rebuild)

Extend `DS2_NativeRuntimeHook.cpp` with a new `player.transform_probe`
event that:

1. Reuses the existing `GameManagerImp` resolver.
2. Iterates `GameManagerImp + 0x00..0x200` at 8-byte stride.
3. For each value that looks like a heap pointer (`0x7FF400000000..
   0x7FF600000000` range, confirmed against `VirtualQuery`), dereferences
   it and inspects offsets 0x60..0x120 for sequences that look like a
   transform (3 consecutive `float`s with a reasonable position range —
   DS2 world positions are typically in the [-1000, +1000] range).
4. Once candidates are found, logs them at the 30Hz heartbeat tick so we
   can confirm they update when the player moves.

Pros:
- 60Hz-capable.
- Direct ground truth — bypasses any send-rate clamps.
- Foundation for the eventual fake-actor spawn (we'll need the same
  ChrIns layout to write phantom state).

Cons:
- Requires a Bonfire/Injector rebuild + DS2 restart (kills the current
  test session).
- Offset discovery is iterative; expect a few rebuilds before the right
  candidate locks in.

### Suggested sequence

1. Finish current 2-PC session (no rush).
2. Land Track 1 (server-side telemetry) first — additive, low risk, gives
   us the protobuf wire values for verification before we touch
   memory.
3. Land Track 2 (memory probe) on the next branch session — the
   server-side numbers from Track 1 become the "answer key" against
   which the memory candidates are validated.
4. When both agree, document the resolved ChrIns transform offset(s) in
   a new `dsseamlesscoop-player-sync-map.md` reference file (per the
   Codex skill memory note).

## 7. Reference repos (HKMP architecture map)

```text
C:\Users\Diux\Desktop\HKMP-master\HKMP-master
C:\Users\Diux\Desktop\HKMP-Entity-Sync-master\HKMP-Entity-Sync-master
```

The architectural mapping HKMP→DS2 is documented in
`Docs/DS2NativeRuntime.md` under "Long-Term Vision: HKMP-Style Overlay".
Key parallels:

- HKMP `ServerNetManager` ⇔ our Server.exe `DS2_PlayerDataManager`
- HKMP `ClientNetManager` ⇔ DS2 `RequestUpdatePlayerStatus` send path
- HKMP `ClientPlayer.PlayerData` ⇔ DS2 `AllStatus`
- HKMP fake-player GameObject spawn ⇔ DS2 ChrIns phantom slot (open
  question — does the engine support >4 phantom slots without a deep
  patch?)

## 8. Live evidence captured during this research window

- `server.log` mtime 23:13:58 — 2 players continuously connected for
  15+ minutes, no errors.
- `Runtime/DS2Native/906e2a2d-…_6500.events.jsonl` — 434 events of
  `runtime.heartbeat`, `inventory.probe` (with full pointer chain),
  `bonfire.custom_item_use`, `bonfire.rest`. The hook is alive and
  reporting cleanly every ~2s.
- `Runtime/DS2Native/…_6500.service_state.json` — manifest is being
  live-pushed to Server.exe and propagated to master every 30s, no
  staleness reported.
- Brother's Steam ID `01100001516773ce` (Player 2) entered our session
  at `2026-05-15T22:58:12.721Z` via LAN private-IP redirect.
- Both players progressed through Forest of Fallen Giants (lit
  bonfires 2650 Fire Keepers' Dwelling, 4650 Far Fire, 10670
  Crestfallen's Retreat, 10655 Cardinal Tower).

This evidence is the baseline against which any future overlay code is
validated.

## 9. Track 1 results — server-side telemetry deployed and live (2026-05-15 23:43)

Patch landed in `DS2_PlayerDataManager::Handle_RequestUpdatePlayerStatus`
(decode the merged `AllStatus` + emit one `PLAYER_STATUS_JSON {…}` line
per packet via `LogS`). Rebuilt `Server.exe` against
`intermediate/vs2022/Source/Server/Server.vcxproj` in 11s, copied to
`Source/bonfire/build/.../Release/Server/Server.exe`. Brother reconnected
via Travel-to-this-fire after the restart and we captured 50+ samples
across ~7.5 min of active coop play.

### 9.1 Position evidence (both players, sequential)

Diux (host, `011000011773f8ab`, SL 23):

| Hora UTC      | px       | py     | pz       | yaw (rad) | sat_bf |
|---------------|----------|--------|----------|-----------|--------|
| 23:47:00      | 102.61   | 0.66   | -243.94  | -2.64     | 0      |
| 23:47:23      | 91.90    | -8.30  | -226.99  | -1.45     | 0      |
| 23:47:28      | 92.92    | -8.30  | -209.53  | 0.00      | 0      |
| 23:47:49      | 79.43    | 1.62   | -184.50  | 0.30      | **1**  |
| 23:54:12      | 70.28    | 1.62   | -189.76  | -2.36     | 0      |
| 23:54:34      | 67.20    | 1.88   | -210.35  | 2.80      | 0      |

Evil (guest brother, `01100001516773ce`, SL 16):

| Hora UTC      | px       | py     | pz       | yaw (rad) |
|---------------|----------|--------|----------|-----------|
| 23:49:38      | 78.41    | 1.62   | -184.87  | 0.87      |
| 23:50:54      | 78.04    | 1.62   | -179.99  | -0.06     |
| 23:52:31      | 58.79    | 1.56   | -187.50  | -2.85     |
| 23:53:04      | 79.01    | 1.62   | -183.44  | 1.25      | (sat_bf=1 — sat at host's bonfire) |
| 23:54:31      | 65.29    | 1.68   | -215.66  | -2.58     |
| 23:54:37      | 67.86    | 2.24   | -206.65  | 1.64      |

### 9.2 Confirmed conclusions

- **Coordinate system**: Y is altitude. X and Z are horizontal axes. Range
  for this area [-300, +300] ish.
- **Unit**: ~meters. Diux moved from (102, -243) to (67, -210) in
  ~7.5 min — a real-world walking pace fits.
- **yaw lives in `PlayerLocation.unknown_5`** (proto field 5). Range
  [-π, +π] in radians. Renamed mentally as `yaw`. Already on the wire.
- **`cell_id` is a world grid index**, not a stable ID:
  - Top byte = X bucket (0xFD/0xFE = adjacent buckets at px ≈ 101)
  - Byte 2 = Z bucket (0xC0/0x00/0x40/0x80 step by 0x40 every ~10 pz units)
  - Bytes 3-4 = sub-cell / altitude refinement
  - Used by DS2 for chunk streaming, not directly useful for overlay sync.
- **Send rate observed**: 5-10s between updates while moving, 20-60s when
  idle. **Faster than the documented 60s clamp minimum** — the client
  pushes opportunistically on movement, area change, bonfire interactions.
  This is the slow lane.
- **Steam P2P is the fast lane**: nothing in
  `DS2_Frpg2ReliableUdpMessageTypes.inc` carries movement/animation data
  in real time. The 4 covered domains (PlayerData, Signs, BreakIn, Visitor,
  Misc, Ranking, QuickMatch) are all 1Hz-or-slower request/response.
  Inter-player phantom sync is therefore traveling over Steam Networking
  (`SendP2PPacket` etc.) carrying NRSessionSearchResult-keyed sessions,
  invisible to our Server.exe.
- **Both players co-locate after summon**: brother first appeared at
  (78.41, 1.62, -184.87) while host was at (79.43, 1.62, -184.50). The
  saponita drops him within ~1.5 units of host — the host's bonfire
  altar — confirming summon == teleport to host's current position.

### 9.3 Architecture implication for the overlay

The Steam P2P discovery rules out Path A (server fast lane) as a *practical*
HKMP overlay path. If we cranked `player_status_send_delay` to the 60s
minimum the data rate is still 1/60 Hz at most — useless for visible
phantom movement.

Path B (Steam P2P tee) is technically viable but extremely invasive:
hook `SteamNetworking::SendP2PPacket` / `ReadP2PPacket` in DS2's process,
mirror packets to additional clients. Steam may treat this as anti-cheat
divergence and Steam-banhammer the account. Not recommended.

**Path C (client-side memory probe + fake-actor spawn) is the clear
winner.** With Track 1 ground truth (px, py, pz, yaw at 1/5-10 Hz), we
can validate any candidate transform we find in DS2's process memory at
60Hz. Then we drive *N* fake-actor phantoms locally from sync packets
sent over our OWN transport (Bonfire-relayed UDP, not Steam P2P).

## 10. Track 2 prep — memory probe insertion plan

When the user is done with the current coop session and we can restart
DS2, the next patch lands in `DS2_NativeRuntimeHook.cpp`:

1. Reuse the existing `kGameManagerImpPattern` signature scan to resolve
   the global `GameManagerImp` pointer (already proven working — see
   `inventory.probe` event at every ~30s).
2. Add a `WorldTransformProbe` worker that, on each runtime heartbeat
   (every ~2s), enumerates `GameManagerImp + 0x00 .. 0x200` in 8-byte
   stride, and for each pointer-valued slot dereferences and inspects
   offsets 0x60 .. 0x300 in the target object for **float quads** matching:

   ```text
   [px, py, pz, yaw_or_unknown]
   where px ∈ [-1000, +1000]  and  pz ∈ [-1000, +1000]
   and    py ∈ [-100, +100]
   and    yaw or 4th float ∈ [-π, +π]
   ```

3. For each candidate quad, emit a `player.transform_probe_candidate`
   event with the resolved chain (GameManagerImp + ?? → +?? → +??) and
   the values read.

4. The user walks around in-game. We then grep events.jsonl for the
   candidate whose values track the PLAYER_STATUS_JSON entries from
   server.log within ±0.5 units. That's the offset chain.

5. Once locked, write it to a new reference doc
   `dsseamlesscoop-player-sync-map.md` and lift it into a permanent
   `kPlayerTransformChain` constant in the hook.

This is purely additive instrumentation — no behavior change, no
gameplay risk. Trigger condition for deploying it: brother is OK with a
~1-minute DS2/Server restart cycle, just like the Track 1 deployment.

## 11. Track 2 results — player transform chain LOCKED (2026-05-16 00:20)

Patch landed in `DS2_NativeRuntimeHook.cpp`:
`EmitWorldTransformProbe` runs every 3rd heartbeat (~6s) and scans
GameManagerImp at two pointer hops for float quads matching the
PLAYER_STATUS_JSON ground-truth envelope. v2 of the probe (after the
first single-hop iteration missed) added the second hop and tightened
`kMinHeapPtr` to `0x00007FF000000000`.

### 11.1 The locked offset chain

```text
DarkSoulsII.exe + AOB(GameManagerImp_global)   →   GameManagerImp object
GameManagerImp + 0x18                          →   intermediate (entity manager?)
intermediate  + 0x50                           →   player ChrIns (host's avatar)
player_ChrIns + 0x90 (float[4])                →   px, py, pz, w=1.0   ← position (homogeneous)
player_ChrIns + 0xA0 (float[4])                →   px, py, pz, w=0.0   ← mirror or velocity vector
```

### 11.2 Validation — 164 probes over 16 minutes survive:

- **Idle at hoguera**: 30+ consecutive probes returning the exact same
  (76.30, 1.62, -183.05) — chain is stable when the player is still.
- **Walking, sprinting, rolling**: smooth float deltas in the 1-6
  unit-per-tick range, byte-exactly matching the wire-side
  PLAYER_STATUS_JSON snapshots (which fire every ~20s and contain
  identical coordinate values when sampled).
- **Bonfire warp #1** (00:26:40): position jumped from (78, 1.62, -184)
  to (10, 5.92, -16) — entirely different cell — and the chain still
  pointed at the live transform. No staleness.
- **Area transition #2** (00:27:54): pz jumped from -184 to +291 (a
  470-unit teleport to a brand-new area, probably Heide's Tower or
  Cathedral of Blue zone). Chain held.
- **Bonfire warp back** (00:36:12): jumped from (-6, -67, 430) (likely
  The Gutter at py=-67) back to (78, 1.62, -183) at Cardinal Tower in
  one tick. Chain held.
- **Vertical fall** (00:30:17): py descended -17 → -50 → -67 over 12s
  during a fall. Chain held the entire arc.

The chain is **invariant across area transitions, bonfire warps, and
vertical drops**. This rules out ChrIns reallocation on world cell
streaming; the player's ChrIns is allocated once at session start and
re-purposed (transform written in place) on warp.

### 11.3 Wire-side vs memory-side rate gap

The wire `PLAYER_STATUS_JSON` channel delivers ~1 sample / 20s when the
player is moving. The memory `world.transform_probe` at 1/6s gives 3-4x
denser samples. With a higher probe cadence (every 33ms ≈ 30Hz) this
chain unlocks HKMP-grade overlay sync:

```text
Wire    @ 00:23:21  (76.30, 1.62, -183.05)
Probe   @ 00:23:24  (76.30, 1.62, -183.05)  ← exact match, idle
Probe   @ 00:23:30  (76.30, 1.62, -183.05)  ← exact match, idle
Probe   @ 00:23:37  (72.67, 1.62, -184.26)  ← mid-walk capture
Wire    @ 00:23:39  (69.19, 2.26, -179.96)  ← 2s after probe, slightly ahead
```

Memory side is leading the wire by ~2-5s in moving samples — exactly
what we want: ground truth at native frame rate, not network-throttled.

### 11.4 Other chains discovered

Static "likely_player" candidates that may be the brother's phantom or
local NPCs (rendered locally with stale/interpolated data):

| Chain | Samples | Position | Notes |
|---|---|---|---|
| `gm+0x18 → +0x80 → [obj] + 0x90` (multiple obj_ptrs) | 50+ | various py=11, py=-9, py=1.62 | Likely streaming-radius entities |
| `gm+0x18 → +0x88 → [0x...AB700] + 0x90` | 25 | (69.19, -9.20, -174.82) | Static — possibly a corpse or NPC |
| `gm+0x20 → +0x1A0` | 2 | moved 6.25u in (82,3.7,-183)→(76,3.5,-181) | Different branch — may be guest phantom |

The `gm+0x20 → +0x1A0` chain is intriguing — different gm offset (0x20
vs 0x18), different inner offset (0x1A0 vs 0x90), and showed a real
6.25-unit movement. Worth a follow-up scan to confirm it's the guest's
ChrIns/phantom.

### 11.5 Outstanding gaps

1. **Yaw in memory** not yet located. The `+0x9C` w-component is constant
   at 1.0 (homogeneous coord), `+0xAC` is constant at 0.0. The yaw float
   that PLAYER_STATUS_JSON reports as `loc_u5` is probably at +0x60..+0x88
   relative to the same ChrIns. A targeted scan around the player object
   is the next probe iteration.
2. **Guest's transform chain** still uncertain. The `gm+0x20` candidate
   needs validation across more samples.
3. **Animation state** (idle / walk / attack frame) not yet probed. Will
   live near the transform in a per-frame state struct.

## 12. Track 3 plan — HKMP overlay milestone 2 (synthesis)

With the host's transform chain locked, the next concrete deliverable is:

1. **Add a `RuntimeWorkerConfig::PlayerTransformChainLocked` cached
   pointer triplet** so the probe doesn't need to AOB-scan on every tick.
2. **Add a `player.live_transform` event** that simply reads the cached
   chain and emits (px, py, pz) every heartbeat. Bandwidth-cheap. 60Hz
   doable.
3. **Add a Bonfire-side network broadcast**: BonfireService listens to
   `player.live_transform`, batches into UDP packets, sends to peer
   Bonfires via the existing BonfireService↔Server.exe transport. We
   are NOT using Steam P2P — we're routing through our own
   BonfireService backbone, which we control entirely.
4. **Add fake-actor spawn hook on receiver**: when a peer's transform
   packet arrives, write it into a fake ChrIns slot in the local DS2
   process (we need to learn how DS2 allocates phantom slots first —
   probably by intercepting the existing summon-sign flow and forging a
   "spectator" sign).
5. **Validate with N=2 players visible to each other simultaneously,
   moving independently, not going through Steam P2P**.

Milestones (5) is the actual HKMP-style overlay. Milestones (1)-(4)
together are achievable in 2-3 more focused work sessions.

## 15. Track 5 — Camino 2 Phase 1 LOCKED (2026-05-16 02:44)

After confirming that Camino 1 (synthetic non-phantom ChrIns synthesis)
would inherit all of DS2's vanilla phantom restrictions (4-player cap,
fog-gate despawn, Soul Memory matching, Steam P2P dependency, etc.),
we committed to **Camino 2 — direct DXGI render-pipeline hook**. The
goal is to make DS2 draw peer-player meshes from our own data stream,
bypassing the vanilla character/phantom subsystem entirely.

### 15.1 Why Camino 1 was rejected

Even if we built a synthetic ChrIns and inserted it into the world chr
manager, it would still be constrained by:

- Phantom slot cap (host + 3 phantoms vanilla)
- Despawn at fog gates, boss rooms, area transitions, host death
- Soul Memory tier / invadability rules
- White/red phantom team flags
- Soapstone-based init (every "summon" needs a sign ritual)
- Steam P2P expectations for sync (vanilla DS2 phantoms get their
  transform from Steam Networking, not from our network)

The whole point of the custom items 62061000-62061008 + BNS sentinel
manifest is to bypass vanilla mechanics. The render layer has to be
external too.

### 15.2 Phase 1 — D3D11 render hook installed (the v8c iteration)

Three failed iterations preceded the working hook:

- **v7** (2026-05-16 02:18): dummy IDXGISwapChain creation triggered
  the "Darksouls Lighting Engine" mod's `scene_rt_handler.cpp:284`
  assertion `_swapchain == swapchain`. Lighting engine tracks the
  first swap chain it sees and asserts on every Present — our dummy
  polluted that tracking.
- **v8a**: rollback — disabled the hook entirely, kept the rest.
- **v8b** (2026-05-16 02:30): switched to hooking the d3d11.dll
  export `D3D11CreateDeviceAndSwapChain` via GetProcAddress + Detours,
  no dummy needed. Coexisted with lighting engine cleanly but **never
  fired** because dumpbin on `DarkSoulsII.exe` showed DS2 imports
  `D3D11CreateDevice` (NOT the AndSwapChain variant).
- **v8c** (2026-05-16 02:38): hooks `D3D11CreateDevice` (the actual
  export DS2 uses), walks the returned device → `IDXGIDevice` →
  `IDXGIAdapter` → `IDXGIFactory`, VMT-hooks `IDXGIFactory::
  CreateSwapChain` (vtable index 10), then VMT-hooks the returned
  swap chain's `Present` (vtable index 8). **Locked.**

### 15.3 v8c evidence

164 seconds of in-game play with the brother summoned, lighting engine
active:

```text
render_create_device_hooked:  True
render_factory_hooked:        True
render_present_hooked:        True
render_hook_installed:        True
render_frame_count: 0 → 9141 (Δ 9141 frames in 164.0s = 55.7 FPS)
```

No assertion. No black screen. No phantom restrictions. We are now
splicing into DS2's render thread at every frame, alongside the
community lighting engine, with both subsystems oblivious to each
other.

### 15.4 The cascade hook architecture

```text
DS2 process attach
  └─ Injector.dll loads
       └─ DS2_RenderHook::Install spawns InstallThread

InstallThread (polls 500ms × 60)
  ├─ GetProcAddress(d3d11.dll, "D3D11CreateDevice")
  ├─ DetourAttach → HookedCreateDevice
  └─ s_create_device_hooked = true

DS2 renderer init
  └─ Calls D3D11CreateDevice
       └─ Our HookedCreateDevice
            ├─ Forwards to original (real d3d11)
            │    └─ Original calls into dxgi.dll (lighting engine wrapper)
            ├─ device->QueryInterface(IDXGIDevice)
            ├─ dxgi_device->GetAdapter
            ├─ adapter->GetParent(IDXGIFactory)
            ├─ VMT-hook factory.CreateSwapChain (vtable[10])
            └─ s_factory_hooked = true

DS2 calls factory->CreateSwapChain
  └─ Our HookedFactoryCreateSwapChain
       ├─ Forwards to original (lighting engine wrapper)
       ├─ Wrapper creates real swap chain + its own tracking
       ├─ VMT-hook swap.Present (vtable[8])
       ├─ s_present_hooked = true
       └─ s_installed = true

Every rendered frame (~55-60Hz)
  └─ DS2 calls swap.Present
       └─ Our HookedPresent
            ├─ s_frame_count++
            ├─ (Phase 2 draw splicing — currently no-op)
            └─ Forwards to original
                 └─ Lighting engine's scene_rt_handler runs OK
                    (sees the same wrapper IDXGISwapChain it tracked)
```

### 15.5 What's unlocked

With Phase 1 we have the per-frame callback inside DS2's render thread
+ access to the swap chain pointer + (via the cascade) access to the
`ID3D11Device` and `ID3D11DeviceContext`. That's everything D3D11
needs to splice draw calls.

Concrete next-step targets:

- **Phase 2** — draw a solid-color quad in a screen-space corner of
  the swap chain. Visual proof of overlay. ~1 rebuild.
- **Phase 3** — locate DS2's view-projection matrix in memory, draw a
  world-space placeholder (cube/capsule) at a fixed world position.
  ~2 rebuilds + memory-search session.
- **Phase 4** — UDP transport between BonfireService instances; pose
  packets at 30Hz. ~3 rebuilds (BonfireService C# side + Injector
  reader).
- **Phase 5** — wire peer pose data → world-space placeholder
  position. First moment players see "each other" via OUR overlay
  (NOT the vanilla phantom system). ~1 rebuild.
- **Phase 6** — real character mesh (FLVER parsing or simplified
  humanoid placeholder).

### 15.6 Files modified for Phase 1

- `Source/Injector/Hooks/DarkSouls2/DS2_RenderHook.h` (new)
- `Source/Injector/Hooks/DarkSouls2/DS2_RenderHook.cpp` (new, v8c)
- `Source/Injector/CMakeLists.txt` (added new sources)
- `Source/Injector/Injector/Injector.cpp` (registered DS2_RenderHook)
- `Source/Injector/Hooks/DarkSouls2/DS2_NativeRuntimeHook.cpp`
  (heartbeat now surfaces 4 per-stage render-hook flags + frame
  counter)

## 13. Track 3 v3 results — yaw + HP locked (2026-05-16 00:53)

Track 3 v3 ships `EmitPlayerLiveTransform` running every heartbeat
(~2s). It uses the locked chain `gm+0x18 → +0x50` to read the host's
ChrIns directly and dumps:

- `yaw_candidates`: floats in [-π, +π] excluding ~0.0/~1.0/-1.0 from
  `ChrIns + 0x00..0x200`.
- `int_candidates`: uint32 in [1, 50000] from the same range.

### 13.1 Yaw formula locked

Two pairs of offsets oscillate in [-0.988, +0.988] — that's the
sin/cos range, NOT yaw-in-radians. The values form the 2x2 rotation
sub-matrix of a standard 4x4 transform:

```text
yaw_radians = atan2( memory[ChrIns + 0x80],     // sin(yaw)
                     memory[ChrIns + 0x60] )    // cos(yaw)
```

Validated against wire `PlayerLocation.unknown_5` (renamed mentally as
`yaw_radians`) with **Δ=0.000 on every same-instant pair** across 109
probe samples. Example matches:

```text
wire @ 00:49:42  loc_u5 = -0.686    probe atan2 = -0.686   Δ=0.000 ✓
wire @ 00:52:09  loc_u5 =  1.736    probe atan2 =  1.736   Δ=0.000 ✓
```

Non-zero deltas in other samples (max Δ=5.6) are explained by the wire
sampling at ~1/20s while the player kept rotating in the intervening
seconds — the probe captures the live state, the wire captures a
snapshot from 2-15s prior.

### 13.2 Full transform matrix layout

```text
        col0           col1   col2          col3
row0  [ cos(yaw)        ?    -sin(yaw)       ?       ]   ChrIns + 0x60..0x6C
row1  [ ?               ?     ?              ?       ]   ChrIns + 0x70..0x7C
row2  [ sin(yaw)        ?     cos(yaw)       ?       ]   ChrIns + 0x80..0x8C
row3  [ px              py    pz             1.0     ]   ChrIns + 0x90..0x9C
```

Standard right-handed column-major OpenGL-style 4x4. DS2 is Y-axis-only
for player rotation; pitch/roll cells in row 1 are near-identity.

### 13.3 HP locked at +0x168 / +0x170

Wire-side `PhysicalStatus.health` = 807; in-memory uint32 at ChrIns +
0x168 = 807 (exact match), and ChrIns + 0x170 = 807 (mirror), and
ChrIns + 0x174 = 808 (likely max_hp / target_hp / regen ceiling).

### 13.4 Other identified fields

```text
ChrIns + 0x008  uint32  alive/combat flag (oscillates 1↔2)
ChrIns + 0x128  uint32  13 (likely vigor stat)
ChrIns + 0x130  uint32  15 (likely endurance stat)
ChrIns + 0x148  uint32  33 (wire SL=32 → memory = SL + 1, off-by-one)
```

### 13.5 Still missing (next iteration)

- **Stamina**: wire stam=92. Not found in 0x00..0x200 range. Probably
  ChrIns + 0x200+.
- **Soul Memory**: wire sm=37051. Not in scan range either.
- **Equip Load**: wire equip=24.8 (float). Need to add a float-in-[0,
  100] candidate pass.
- **Brother's ChrIns**: still a hypothesis (`gm+0x20 → +0x1A0` saw
  movement). Needs a sibling-offset probe to confirm.

### 13.6 The "live read path" — 68 bytes per player per tick

With Track 3 v3 we can read everything an HKMP-style overlay needs in
a single pointer chain walk + 64-byte block read:

```c
// One-time AOB-resolved at session start:
uintptr_t gm_imp_global = AOB_match;            // RIP-relative resolve

// Every overlay tick (30Hz target):
uintptr_t gm_imp = *(uintptr_t*)gm_imp_global;
uintptr_t intermediate = *(uintptr_t*)(gm_imp + 0x18);
uintptr_t chrins = *(uintptr_t*)(intermediate + 0x50);

// Read the 4x4 transform (16 floats = 64 bytes):
float xform[16];
memcpy(xform, (void*)(chrins + 0x60), 64);

// Decode:
float yaw = atan2f(xform[8], xform[0]);  // atan2(sin@0x80, cos@0x60)
float px  = xform[12];                    // ChrIns + 0x90
float py  = xform[13];                    // ChrIns + 0x94
float pz  = xform[14];                    // ChrIns + 0x98

uint32_t hp = *(uint32_t*)(chrins + 0x168);
```

**Total wire bandwidth per player at 30Hz: 68 bytes × 30 = 2 KB/s.**
Trivial. The Bonfire↔BonfireService UDP backbone can carry this for
8 simultaneous players at <16 KB/s — that's HKMP-grade sync done on a
LAN without a sweat.

## 13b. Camera & View-Projection chain (2026-05-16, Phase 3 v14)

After ~10 rebuilds of cbuffer-snoop attempts (Phase 3 v6→v13) returned
zero usable VP candidates — DS2 SOTFS composes `V × P` in the shader
instead of storing the combined matrix — we switched to **reading the
camera state directly from DS2's memory** via interactive Cheat Engine
exploration. This worked on the first walk and gives us a stable,
live VP without any GPU sync.

### The chain (anchored on the existing `gm_imp_global` AOB)

```
gm_imp_global  resolved via AOB
   48 8B 05 ?? ?? ?? ?? 48 8B 58 38 48 85 DB 74 ?? F6
   (already done by DS2_NativeRuntimeHook; address published via
   DS2_NativeRuntimeHook_GetGameManagerImpAddress())

   *gm_imp_global      = gm
   *(gm + 0xD0)        = p1   (== player ChrIns; also gm+0x18→+0x50)
   *(p1 + 0xE8)        = p2
   *(p2 + 0x18)        = p3
   *(p3 + 0x28)        = camCfg
```

### `camCfg` field map (verified live)

| Offset  | Type        | Meaning                                          |
|---------|-------------|--------------------------------------------------|
| `+0x4E0` | `float[16]` | **camera-to-world transform** (row-major)        |
|         |             | row 0 = right axis (rx,ry,rz,0)                  |
|         |             | row 1 = up axis    (ux,uy,uz,0)                  |
|         |             | row 2 = forward    (fx,fy,fz,0)                  |
|         |             | row 3 = eye world position (ex,ey,ez,1)          |
| `+0x520` | `float[16]` | **D3D LH perspective projection** (row-major)    |
|         |             | m[0][0] = 1/(aspect·tan(fovY/2))                 |
|         |             | m[1][1] = 1/tan(fovY/2)                          |
|         |             | m[2][2] = far/(far−near) ≈ 1.0                   |
|         |             | m[2][3] = 1.0                                    |
|         |             | m[3][2] = −near·m[2][2] ≈ −0.1                   |
|         |             | m[3][3] = 0                                      |
| `+0x3D4` | `float`    | aspect ratio (1.778 = 16:9)                      |
| `+0x3D8` | `float`    | near plane (0.1)                                 |
| `+0x3DC` | `float`    | far plane (5000)                                 |
| `+0xD04` | `float`    | camFovY radians (0.7679 ≈ 44° vanilla)           |
| `+0xD10` | `float`    | camera target Y offset above feet (chrOY = 1.42) |
| `+0xD18` | `float`    | camera distance from target (3.6)                |

### Verification math (live snapshot)

At a sample frame:
- Player position: `(-32.88, 6.60, -11.22)` (chr+0x90)
- Camera eye:     `(-31.07, 8.17, -14.32)` (camCfg+0x4E0 row 3)
- Camera target = player + (0, chrOY, 0) = `(-32.88, 8.02, -11.22)`
- `target − eye` = `(-1.82, -0.15, 3.10)`, ‖.‖ = **3.6000 ✓** (= cfg_dist)
- Forward axis (camCfg+0x4E0 row 2) = `(-0.5045, -0.0431, 0.8624)`
- Normalized `(target − eye)` = `(-0.506, -0.042, 0.861)` ✓ matches row 2

### View-Projection construction

For an orthonormal rotation `R` + translation `t = eye`, the inverse
(world → view) is `R^T | -R^T·eye`. So in C++ the view matrix is:

```c
float view[16] = {
    rx,  ux,  fx,  0,
    ry,  uy,  fy,  0,
    rz,  uz,  fz,  0,
    -(eye·right), -(eye·up), -(eye·forward), 1
};
```

Then `VP = view × proj` via standard row-major matrix multiply.
The result is uploaded row-major to the HLSL cbuffer; the shader uses
`mul(VP, float4(world_pos, 1.0))`. Under HLSL's default column-major
matrix interpretation of cbuffer bytes, this is equivalent to
`clip_col = VP^T · world_col`, which equals
`(world_row · VP)^T = clip_col`. ✓

### Discovery method (Cheat Engine bridge)

1. `AOBScan("48 8B 05 ?? ?? ?? ?? 48 8B 58 38 48 85 DB 74 ?? F6", "+W")`
   resolves `gm_imp_global`.
2. Walked `gm → ... → camCfg` by reading qword pointers.
3. Searched `camCfg` for **4×4 orthonormal sub-matrices** with
   translation within ~5 units of the player. Only one matched in the
   first 0x1000 bytes: `camCfg + 0x4E0`.
4. Searched for **projection-like matrices** (`m[2][3]=1, m[3][3]=0`):
   24 candidates appeared, of which `camCfg + 0x520` has the diagonal
   `(1.3922, 2.4751, 1.0, 1.0)` that decomposes cleanly into
   `yScale = 1/tan(0.7679/2) = 2.4751` and `xScale = yScale/1.778 = 1.3922`.

The full session is captured in `commit history (Phase 3 v14)` and in
the heartbeat events the runtime emits with the new
`render_live_vp_*` fields.

## 13c. Phase 3 closure (2026-05-16, v15)

Confirmed visually in-game: a magenta 3D cube of 6 distinguishable
faces sits on the host's feet, stays anchored when the character
moves, and rotates correctly when the camera orbits. The geometry
follows the host through level transitions and the cube faces are
clearly visible from any camera angle Souls allows.

This closes Phase 3: **we can now draw arbitrary world-space
geometry inside DS2's swap chain, anchored to any world-space
coordinate, with the same projection DS2 uses for its own geometry**.
Everything past this point is "what do we draw" rather than "can we
draw at all".

The architecture supporting future iterations:
- `DS2_RenderHook::DrawOverlay` already saves & restores every D3D11
  immediate-context state it touches (OM/RS/IA/Shaders/VS_CB0), so
  adding more draws inside this hook is purely additive — won't
  disturb the rage-vitamins Lighting Engine post-passes.
- `TryReadCameraVP()` builds the per-frame VP in <1µs. We can call it
  once and feed the same VP to N draws without re-walking the chain.
- The HLSL cbuffer is dynamically mapped per draw (WRITE_DISCARD),
  so we can rewrite `anchor + scale + color + per-actor rotation`
  per cube without allocating new buffers.

## 13d. Phase 4d closure (2026-05-16, v2.8.0 → v2.8.5)

LAN test with Diux + brother on the same subnet. Goal: each side
sees a cube placeholder at the other's world position. Took six
incremental releases to land cleanly because every layer of the
stack had at least one corner-case bug that only surfaces with two
PCs talking to each other.

The release chain, with what each fixed:

| Tag | Headline |
|-----|----------|
| v2.8.0 | Phase 4d wired: bridge auto-starts on session.create / session.join from the in-game orb. First LAN attempt revealed the auto-update was broken because /releases/latest excludes prereleases. |
| v2.8.1 | AppUpdater switched to /releases?per_page=30 — clients see prerelease updates too. |
| v2.8.2 | UI version label fell back to GitHub-derived currentVersion (showed "vunknown" until the probe succeeded). Now reads from the local ping RPC unconditionally. |
| v2.8.3 | Pose bridge auto-starts from heartbeat whenever DarkSoulsII.exe is alive, no in-game orb required. Removed the trap of "host clicked Light, never used the orb, bridge never woke up". |
| v2.8.4 | Injector worker thread sometimes hangs on a chain-walk SEH in pre-world loading — events.jsonl freezes, the bridge that tailed it for pose source went silent. Added Ds2MemoryReader: AOB-scans DarkSoulsII.exe and walks the chain via ReadProcessMemory from the BonfireService side, completely bypassing the Injector worker. Pose now reliable on the broadcast side. |
| v2.8.5 | Peers were configured from master-listed Hostname (WAN) only; same-LAN co-op routed packets all the way out to the public internet and got dropped by the host's router on the return path. Now peers list includes BOTH Hostname (WAN, e.g. 190.114.43.66) and PrivateHostname (LAN, e.g. 192.168.68.54). LAN traffic stays on the switch. |

Two non-code issues fixed during the test:

1. **Windows firewall rule for UDP 50031.** Added to `Firewall.cs`
   in v2.8.0 but the rule re-application path only runs at install
   time. Incremental updates left the rule missing on both PCs.
   Manual `netsh advfirewall firewall add rule name="DS3OS PoseBridge
   UDP" dir=in action=allow protocol=UDP localport=50031` unblocked
   inbound packets. Future work: have BonfireService check and
   re-apply on startup.

2. **Hung Injector worker thread.** Surfaces if the runtime worker's
   chain-walk SEH catches during DS2's pre-world loading phase —
   the thread enters a state where it stops emitting heartbeats AND
   stops polling commands.jsonl, even though the render hook (in a
   different thread) keeps drawing the host magenta cube. The fix
   tonight was empirical: tell the user to End Task DarkSoulsII.exe
   from Task Manager and re-launch from Bonfire. A fresh DS2
   process gets a fresh worker. Real fix is to (a) figure out the
   exact code path that causes the hang and patch it, or (b) move
   commands.jsonl polling into the render hook thread which is
   independently alive. Filed for a future session.

End-state telemetry from the working LAN test:
- bridge: `peers=1 broadcast=7543 received=6164` (after ~13 min)
- 732 `command.applied` events with `peer_count=1` on the host side
- Injector log: `drew 2 cubes` on both PCs (own magenta + peer color)

## 14. Next concrete milestones

### Track A — multi-actor rendering (next rebuild)

Extend `DrawOverlay` to iterate over a peer-pose array and emit one
cube per entry. The cube shader already accepts an `anchor` cbuffer
field; widening the cbuffer to also carry per-instance rotation and
color lets each peer render with its own facing + tint. Initial
implementation hard-codes a ghost cube at `host_pos + (5, 0, 0)` to
validate the pipeline scales to N draws without breaking the lighting
engine; subsequent commits wire the array to the network input.

### Track B — Track 4 live broadcast

Cache the locked `gm → +0x18 → +0x50 → ChrIns` chain inside
`RuntimeWorkerConfig`. Add a `player.pose` runtime event at 30Hz with
the 64-byte payload defined in §13. Write a small UDP broadcaster in
BonfireService that publishes the pose stream to peer Bonfires on the
LAN, and a corresponding receiver that drops poses into the Track-A
peer-pose array.

### Track C — Track 6 animation sync

Probe `ChrIns+0x200..0x600` for the `animation_id` field (a u32 that
indexes a global animation event table). Once we sync animation_id
alongside pose, the cube placeholders can be replaced with actual
character models that play the right anim per frame.

### Track D — Track 5 fake-actor spawn (research)

Figure out how DS2 allocates phantom slots from `RequestSummonSign`
push messages. If we can forge a "synthetic summon" with our own
`player_struct` blob keyed to a fake CSteamID, we get a renderable
phantom slot we own. Drive it from the Track 4 pose stream and the
cube renderer becomes redundant — peer players appear as proper DS2
phantoms with no overlay needed.

The branch choice between (A+B+C) and D is genuine: A+B+C is a
fully custom overlay (HKMP-pure), D rides DS2's native phantom
system. The overlay path is more work but bypasses every limitation
of vanilla phantoms (4-player cap, fog-gate despawn, hostility
constraints). D is less work but inherits those limits.

