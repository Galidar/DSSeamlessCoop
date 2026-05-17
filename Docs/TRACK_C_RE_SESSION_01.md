# Track C RE Session 01 — DarkSoulsII.exe live RE

Date: 2026-05-17
Process: DarkSoulsII.exe PID 19616, base 0x7FF7F3780000, size 30,892,032 bytes
Tooling: Cheat Engine 7.5 portable + bonfire MCP bridge at autorun

## Resolved addresses

| Name | Address | How resolved |
|---|---|---|
| GameManagerImp anchor AOB | `0x7FF7F3B75219` | `48 8B 05 ?? ?? ?? ?? 48 8B 58 38 48 85 DB 74 ?? F6` (1 match) |
| GameManagerImp global ptr | `0x7FF7F4D948F0` | RIP-relative disp32 from anchor (`anchor+7 + 0x0121F6D0`) |
| GameManagerImp instance | `0x7FF4ADAF0260` | `*(gm_global)` |
| intermediate object | `0x7FF4B5EF91D0` | `*(gm_imp + 0x18)` |
| Local **PlayerCtrl** | `0x7FF4B1189A40` | `*(intermediate + 0x50)`; **RTTI class = `PlayerCtrl`** ✓ |
| Position `(px,py,pz,w)` | PlayerCtrl + 0x90 | `(0.263, 4.88, -19.48, 1.0)` — `w=1.0` confirms homogeneous-position marker |

## PlayerCtrl sub-module pointer map (selected)

Read 1 KB at PlayerCtrl. Pointer-shaped qwords:

| Offset | Pointer value | Notes |
|---|---|---|
| +0x000 | `0x7FF7F4864BB8` | **vtable for `PlayerCtrl`** (exe range, only 1 instance found in heap) |
| +0x018 | `0x7FF4B1265BB0` | sub-object (1 KB, mostly zero/timer values) |
| +0x020 | `0x7FF4B06757E0` | shared world ptr (repeated at +0x138) |
| +0x0B0..+0x100 | 10 ptrs in `0x7FF4B118C/D/F/119xxxx` | First sub-module cluster |
| +0x118 | `0x7FF4B118D7C0` | **6 weapon-shaped uint32s at +0x00..+0x14** (see findings below) |
| +0x228..+0x268 | 10 ptrs in `0x7FF4B126Xxx` | Second cluster — all reference shared block `0x7FF4B126F028` |
| +0x378..+0x3F0 | More clusters in `0x7FF4B118/0x126Xxx` | Unprobed |

## Candidate equipment data

### Sub-module at `0x7FF4B118D050` (PlayerCtrl + 0xC0) — REJECTED as equipment

Two consecutive uint32s at `+0x13C / +0x140`:
- +0x13C = `101000`
- +0x140 = `101060`

Cross-reference against `Paramdex/DS2S/Names/`:
- `PlayAreaParam.txt: 101060 [Forest of Fallen Giants]`
- `MapObjectWhiteDoorParam.txt: 101000 [Forest of Fallen Giants]`

→ this sub-module is the **world / map zone state**, NOT equipment.

### Shared data block at `0x7FF4B126F028` — STRONG equipment candidate

Reached from 4 different sub-pointers (`0x7FF4B126EE60+0x1C8`,
`0x7FF4B126EEF0+0x138`, `0x7FF4B126EF10+0x118`,
`0x7FF4B126EF30+0xF8`). 10 consecutive uint32 values:

| Slot index | Value (dec) | Value (hex) |
|---|---|---|
| 0 | 393221    | 0x060005 |
| 1 | 983045    | 0x0F0005 |
| 2 | 589834    | 0x09000A |
| 3 | 262167    | 0x040017 |
| 4 | 327686    | 0x050006 |
| 5 | 327686    | 0x050006 |
| 6 | 655375    | 0x0A000F |
| 7 | 1507337   | 0x170009 |
| 8 | 524292    | 0x080004 |
| 9 | 393217    | 0x060001 |

Pattern: `0x???000??` — each value is structured as `<u16 high><u16 low>`, suggestive of (instance_index, item_handle) packed pairs.

**Why this is likely equipment**: DS2 character slot count is exactly 10
when counting `head + chest + hands + legs + (right1, right2, right3) + (left1, left2, left3) = 4+6 = 10`.

**Why these aren't raw param IDs**: DS2 weapons start at 1,000,000 and
Paramdex confirms `1000000 = Dagger`. None of these values fall in the
weapon-param range. Instead they appear to be **inventory instance
handles** — DS2 stores each owned item as a separate instance (so two
identical Daggers have different handle values), and the equipment
slots reference those handle indices, not the raw param IDs.

A handle → param lookup happens through the inventory module, which
should be reachable from another PlayerCtrl sub-pointer (TODO: probe
the +0x378..+0x3F0 cluster).

### Sub-module at `0x7FF4B118D7C0` (PlayerCtrl + 0x118) — possibly weapon slots

First 6 uint32s:
- +0x00 = 7,077,968  (0x6C0610)
- +0x04 = 7,929,953  (0x791C21)
- +0x08 = 7,471,205  (0x71FE65)
- +0x0C = 3,145,823  (0x30005F)
- +0x10 = 3,145,776  (0x300030)
- +0x14 = 3,145,777  (0x300031)

The bottom 3 are consecutive (`0x30005F, 0x300030, 0x300031`) — could
be 3 stack handles of the same item type. The top 3 are larger.

## Next-session test plan

1. **Slot mapping test**: user removes their helmet in-game.
   Re-read `0x7FF4B126F028` and see which u32 changed. That
   pinpoints `head_armor_slot` index.
2. **Repeat** for chest, hands, legs to map all 4 armor slots.
3. **Repeat** for right-hand weapons (drop one, see which u32
   resets to 0xFFFFFFFF or shifts).
4. **Find the inventory module**: probe PlayerCtrl + 0x378..+0x3F0
   cluster. The inventory module should have a hash table mapping
   handles → param IDs.
5. **Verify on brother**: once brother is summoned as phantom in
   user's world, find his PlayerCtrl via heap scan (currently only
   1 PlayerCtrl vtable instance — need to confirm phantoms use the
   same class or a derived one).

## Phantom PlayerCtrl discovered (brother summoned via vanilla saponita)

Brother summoned successfully once he leveled to bypass DS2's vanilla
±10-level matchmaking. His phantom PlayerCtrl sits next to ours in the
same `intermediate` parent object:

| Name | Address | How resolved |
|---|---|---|
| Phantom PlayerCtrl | `0x7FF4B1309E60` | `*(intermediate + 0x58)` (sibling slot of local at +0x50) |
| Phantom RTTI | `PlayerCtrl` | identical class, identical struct layout ✓ |
| Phantom position | `(69.0, 1.7, -195.7)` | offset +0x90 (same as local) — different world location |

Struct symmetry: **every sub-module pointer at +0x18, +0xB0..+0x100,
+0x118, +0x228..+0x268** points to a DIFFERENT per-character
allocation on the phantom side, but at the SAME offsets relative to
PlayerCtrl. Layout is identical — only the values inside differ.

## Per-character data localisations

### 1. Player name string — `PlayerCtrl + 0x118` (first 0x40 bytes)

UTF-16 LE. Local string starts with `'P' (0x50)`, phantom with
`'N' (0x4E)`:

- Local: `"Player_0001"` (or similar; bytes 50 00 6C 00 61 00 79 00 ...)
- Phantom: `"NetworkPlayer_0010"` (bytes 4E 00 65 00 74 00 77 00 6F 00 ...)

This is the **cleanest runtime discriminator** — single u16 read at
PlayerCtrl + 0x118 tells us instantly whether this ChrIns is local
or a network phantom.

### 2. Current map zone — `PlayerCtrl + 0xC0` sub-module + 0x13C / + 0x140

- Local has `101000` / `101020` → `PlayAreaParam: [Forest of Fallen Giants]`
  (cross-referenced via Paramdex/DS2S/Names/PlayAreaParam.txt)
- Phantom has `0 / 0` → the phantom's "current zone" field stays null
  on the host side because the phantom is rendered IN the host's world,
  it doesn't need its own zone state loaded.

Useful for: knowing which area the local player is in (we already
have this via Plan v3 Track A discovery, but this is a runtime confirm).

### 3. Per-character data array — `PlayerCtrl + 0xE0` sub-module + 0x3E0..+0x440

20-byte-stride array of `(u32 id, u32 flag=1, f32 value)` entries.
Local and phantom have DIFFERENT entries → confirms per-character
allocation.

Example entries:
| Offset | Local | Phantom |
|---|---|---|
| +0x3F4 | 11001100 (10.0) | 17440100 (65.0) |
| +0x408 | 12180101 (30.0) | 13300101 (65.0) |
| +0x41C | 11001102 (48.33) | 13300102 (20.0) |
| +0x430 | 11100103 (33.33) | 17440103 (20.0) |

**NOT confirmed as equipment IDs**. The phantom's value `133001`
cross-references to `EnemyParam: Bonewheel Skeleton`. The other
values (174401, 110011, 121801, ...) don't appear in any DS2S
param. Best guess: this is a **bestiary / encounter tracking
array** or **active VFX list** per-character. Equipment is
elsewhere — likely in the unprobed `+0x378..+0x3F0` cluster or
behind the `PlayerCtrl + 0x18` sub-pointer.

### 4. Runtime state / buff timers — `PlayerCtrl + 0x268` sub-module + 0x1C8

10 packed `(u16 high, u16 low)` values that **change frame-to-frame**.
Not equipment. Likely buff timers / active-effect IDs.

## Static addresses (for the BonfireService Ds2CharDataReader)

Once we confirm offsets, these are the AOB anchors / pointer-chain
specs to encode in `Ds2CharDataReader`:

```
gm-anchor-AOB:   48 8B 05 ?? ?? ?? ?? 48 8B 58 38 48 85 DB 74 ?? F6
gm-global-ptr:   anchor + 7 + *(int32*)(anchor + 3)    [RIP-relative]
gm-instance:     *(gm-global-ptr)
intermediate:    *(gm-instance + 0x18)
PlayerCtrl_local:    *(intermediate + 0x50)
PlayerCtrl_phantom1: *(intermediate + 0x58)   // and +0x60..? for more slots

// Locale-able fields confirmed:
chr_name_utf16:  *(PlayerCtrl + 0x118)            // first u16 == 'P' (local) / 'N' (network)
position_xyz:    PlayerCtrl + 0x90                // float[3]
position_w:      PlayerCtrl + 0x9C                // f32 == 1.0 (homogeneous marker)
zone_primary:    *(PlayerCtrl + 0xC0) + 0x13C     // u32 PlayAreaParam ID
zone_secondary:  *(PlayerCtrl + 0xC0) + 0x140     // u32 PlayAreaParam ID

// Candidate arrays (semantics TBD — needs equip/unequip test):
per_char_array1: *(PlayerCtrl + 0xE0) + 0x3E0     // stride 20: (u32,u32,f32,_,_)
state_array:     *(PlayerCtrl + 0x268) + 0x1C8    // packed (u16,u16) — frame-changing
```

## What's next

1. **In-game equip/unequip test** (needs user) — remove helmet,
   re-scan all sub-modules for the u32 that changed. That
   pinpoints the actual equipment slot module.

2. **Probe unexplored sub-pointer clusters**:
   - `PlayerCtrl + 0x18` (differs per character, 1 KB, mostly zeros)
   - `PlayerCtrl + 0x378..+0x3F0` (not yet read)
   - Sub-pointers OF sub-modules (deeper drill)

3. **Static analysis on DarkSoulsII.exe** to generate stable AOB
   anchors for each offset above. Module base randomises per
   launch (we landed at `0x7FF7F3780000` this session); the
   `gm-anchor-AOB` is the only fully stable anchor. Every other
   address is computed at runtime from that.

4. **Animation state hunt** (orthogonal to equipment): find the
   animation controller sub-module. Per Omni's research it lives
   inside ChrIns / PlayerCtrl in DS3/Sekiro at a known offset.
   For DS2 we'd search the unprobed sub-pointers for fields with
   a u32 animation ID in range [1, 5000] and a nearby f32 frame
   index in [0, 5.0].
