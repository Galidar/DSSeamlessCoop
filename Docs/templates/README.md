# slot1 template — live engine-rendered phantom snapshot

Captured 2026-05-17, during a real user session online + summoned-as-phantom
in the same world as user's brother. Brother (`NetworkPlayer_000100`) was
the active vanilla phantom in slot 1 of DS2's slot pool, fully engine-
rendered as a real body.

## Why this exists

Phase 2B.2's end goal is to spawn engine-rendered phantoms for Bonfire-coop
peers WITHOUT vanilla saponita matchmaking. To do that, we need to know
what an engine-rendered phantom looks like in memory — every field of every
sub-structure. This template captures that ground truth.

## Source addresses (process-specific, included for context only)

| File | Source address | Size | What |
|---|---|---|---|
| `slot1_record.bin` | `0x7FF4C40D61C0` | 0xA90 | Full slot manager record |
| `slot1_playerctrl.bin` | `0x7FF4C4D69E60` | 0x4A0 | The PlayerCtrl itself |
| `slot1_intermediate.bin` | `0x7FF4C4E45F10` | 0x400 | PlayerCtrl+0x18 sub-struct |
| `slot1_sub_E0.bin` | `0x7FF4C4E46800` | 0x800 | Equipment manager parent (intermediate+0xE0) |
| `slot1_sub_B0_weapon_mgr.bin` | `0x7FF4C4D6CA80` | 0x400 | PlayerCtrl+0xB0 sub |
| `slot1_sub_B8_weapon_mgr2.bin` | `0x7FF4C4D6CBA0` | 0x400 | PlayerCtrl+0xB8 sub |
| `slot1_sub_C0_zonemgr.bin` | `0x7FF4C4D6D3A0` | 0x400 | PlayerCtrl+0xC0 sub |
| `slot1_sub_C8_zonemgr2.bin` | `0x7FF4C4D6D4B0` | 0x400 | PlayerCtrl+0xC8 sub |
| `slot1_sub_D0_chr_data.bin` | `0x7FF4C4D6D5D0` | 0x400 | PlayerCtrl+0xD0 sub |
| `slot1_sub_D8_chr_data2.bin` | `0x7FF4C4D6D600` | 0x400 | PlayerCtrl+0xD8 sub |
| `slot1_sub_E0_equipmgr_parent.bin` | `0x7FF4C4D6A350` | 0x400 | PlayerCtrl+0xE0 sub |
| `slot1_sub_E8_unk.bin` | `0x7FF4C4D6A310` | 0x400 | PlayerCtrl+0xE8 sub |
| `slot1_sub_F0_unk.bin` | `0x7FF4C4D6D630` | 0x400 | PlayerCtrl+0xF0 sub |
| `slot1_sub_F8_unk.bin` | `0x7FF4C4D71960` | 0x400 | PlayerCtrl+0xF8 sub |
| `slot1_sub_100_unk.bin` | `0x7FF4C4D712F0` | 0x400 | PlayerCtrl+0x100 sub |
| `slot1_sub_118_name_ptr.bin` | `0x7FF4C4D6DB10` | 0x400 | Name+misc sub |

Total: ~19 KB of live engine-rendered phantom state.

## Identity

- Brother's in-engine name: **`NetworkPlayer_000100`** (UTF-16, at sub_118+0)
- Local player name: `Player_000100`
- Brother's level: 20, HP 1304/1305
- Brother's equipment array (live read, stride 0x14, intermediate+0xE0 sub +0x37C):
  - RH1: 11420000  RH2: 2400000  RH3: 3400000
  - LH1-3: 3400000 / 3400000 / 3400000
  - Arrows/bolts 6-9: 17440100 / 13300101 / 13300102 / 17440103
  - Armor 12-15: all -1 (unarmored)
  - Rings 16-19: 40420000 / 40370001 / 40020000 / 40530000
  - Custom 20-21: 60155000 / **62061000 (Blessed Eye Orb — Bonfire)**

## How to use

These templates are NOT directly replayable (they contain heap pointers
that are session-specific). They're REFERENCES for understanding what the
engine populates when it spawns a phantom.

To implement Phase 2B.2C (synthetic spawn):

1. Open `slot1_playerctrl.bin` in a hex viewer.
2. At each offset where the live diff (vs local) showed a "sub-pointer"
   (+0xB0, +0xB8, +0xC0, +0xC8, +0xD0, +0xD8, +0xE0, +0xE8, +0xF0, +0xF8,
   +0x100, +0x118), the engine allocated a fresh sub-struct via
   `FUN_140833320` and wired it in.
3. Match the byte patterns at each sub-struct against the corresponding
   `slot1_sub_*.bin` file to understand the engine's init pattern.
4. The synthesis code allocates fresh sub-structs and patches scalar
   fields (position, HP, equipment IDs, name) from the Bonfire-coop SHM
   peer data.

## The 19 KB does NOT include the compact-format inbound buffer

The buffer that ORIGINATED this spawn (DS2's internal compact `0x39`
serialization format, ~500 bytes, allocated by FUN_140833320 and freed
right after FUN_1401A1650 returned) is no longer in memory. To capture
it for a future session:

1. Set env var `BONFIRE_DS2_SPAWN_CAPTURE=1` before launching DS2 via
   Bonfire.
2. Trigger one inbound summon (let brother summon you, or place your
   sign and have someone summon you).
3. v2.9.11's `SpawnEntryHook` will dump the compact buffer to
   `Runtime/DS2Native/<session>.spawn-captures/<utc>_<hit>_inner.bin`.
4. Commit that file to this `Docs/templates/` directory alongside the
   slot1_* files. That gives us the COMPLETE template: both the input
   (compact buffer) and the output (slot state).

With both inbound buffer + output state captured, Phase 2B.2C synthesis
code has zero ambiguity left.
