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

### 3. **Equipment slot array** — `PlayerCtrl + 0xE0` sub-module + 0x37C..+0x534

**FULLY DECODED** — every value cross-references cleanly against
`Paramdex/DS2S/Names/{WeaponParam,ArmorParam,ItemParam}.txt`. The
earlier confusion was because I was checking the wrong Paramdex
range. Real DS2 armor IDs start at 11,000,000 (e.g.
`11001100 = "1001 [Body], Head"`), not at 6-digit values.

Stride: 20 bytes per slot. Slot layout (per 20-byte entry):
```
+0x00  u32 item_id          (param ID — direct, NOT a handle!)
+0x04  u32 flag/count       (= 1 for valid)
+0x08  u32 unknown          (often 1 or 0)
+0x0C  u32 unknown          (often 1)
+0x10  f32 weight/value     (item weight in DS2 units)
```

Slot map (verified live with both local + phantom):

| Slot offset | Slot name | Local | Phantom |
|---|---|---|---|
| +0x37C | R1 (right hand 1) | 11220000 = Silver Eagle Kite Shield | 11420000 = Hollow Soldier Shield |
| +0x390 | R2 (right hand 2) | 3800000 = Sorcerer's Staff | 2400000 = Club |
| +0x3A4 | R3 (right hand 3) | 3400000 = Fists (empty) | 3400000 = Fists |
| +0x3B8 | L1 (left hand 1) | 1220000 = Longsword | 3400000 = Fists |
| +0x3CC | L2 (left hand 2) | 3400000 = Fists | 3400000 = Fists |
| +0x3E0 | L3 (left hand 3) | 3400000 = Fists | 3400000 = Fists |
| +0x3F4 | Head | 11001100 = placeholder helm | 17440100 = Standard Helm |
| +0x408 | Chest | 12180101 = Black Hollow Mage Robe | 13300101 = Old Knight Armor |
| +0x41C | Arms | 11001102 = placeholder gauntlets | 13300102 = Old Knight Gauntlets |
| +0x430 | Feet | 11100103 = Imported Trousers | 17440103 = Hard Leather Boots |
| +0x444..+0x4A8 | Ammo (arrows + bolts), 6 slots | EMPTY × 6 | EMPTY × 6 |
| +0x4BC | Ring 1 | 40230000 = Stone Ring | 40160000 = Ring of Blades |
| +0x4D0 | Ring 2 | EMPTY | 40370001 = Covetous Silver Serpent Ring+1 |
| +0x4E4 | Ring 3 | EMPTY | 40020000 = Chloranthy Ring |
| +0x4F8 | Ring 4 | EMPTY | 40530000 = Ring of Thorns |
| +0x50C | Quickbar 1 | **62061000 = bonfire_blessed_eye_orb** ✓ | 60155000 = Estus Flask |
| +0x520 | Quickbar 2 | **62061001 = bonfire_crystal_eye_orb** ✓ | **62061000 = bonfire_blessed_eye_orb** (used to summon!) ✓ |
| +0x534 | Quickbar 3 | 62030000 = White Sign Soapstone | (not measured) |

**The custom Bonfire orbs (62061000/62061001) appear at +0x50C/+0x520**
— that's the smoking gun that this IS the equipment+quickbar array
(we know the user has those because they used them to summon).

"Fists" (3400000) is the canonical empty-hand value, not a missing
slot. Same for armor: every armor slot HAS to be filled (DS2 doesn't
allow naked, except via specific "Hood" / "Trousers" placeholder
armors).

Total: 21 slots × 20 bytes = 420 bytes equipment+quickbar block.

### 4. Frame-changing state array — `PlayerCtrl + 0x268` sub-module + 0x1C8

10 packed `(u16 high, u16 low)` values that **change frame-to-frame**.
Not equipment (confirmed — actual equipment is at +0xE0 sub +0x37C).
Likely buff timers / active-effect IDs.

### 4. Runtime state / buff timers — `PlayerCtrl + 0x268` sub-module + 0x1C8

10 packed `(u16 high, u16 low)` values that **change frame-to-frame**.
Not equipment. Likely buff timers / active-effect IDs.

## Additional fields discovered (session-01 extended)

### HP triple — `PlayerCtrl + 0x168 / 0x170 / 0x174`

| Offset | Field | Local | Phantom |
|---|---|---|---|
| +0x168 | current HP (u32) | 811 | 1224 |
| +0x170 | max HP w/ buffs (u32) | 821 | 1304 |
| +0x174 | base max HP (u32) | 822 | 1305 |

The (max − current) gap is the hollow penalty / recent damage taken.
Base max HP is what the character would have at full unhollow with
no ring/buff bonuses; max HP w/ buffs is the effective ceiling.

### Equip load — `PlayerCtrl + 0x1AC..+0x1C0` (4 floats)

| Offset | Field | Local | Phantom |
|---|---|---|---|
| +0x1AC | max equip load (f32) | 920.0 | 1000.0 |
| +0x1B4 | max equip load duplicate | 920.0 | 1000.0 |
| +0x1B8 | current equipped weight | 501.8 | 578.5 |
| +0x1C0 | current equipped weight duplicate | 501.8 | 578.5 |

Useful as a sanity check that the character data we mirrored is the
right character (weight should match summed equipment weights).

### Animation phase — `PlayerCtrl + 0xC0 sub-module + 0x2B8` (and mirrors)

A normalized `0..1` float that resets when a new animation starts.
Confirmed during a roll test: was at `0.973` mid-idle, jumped to
`0.330` mid-roll. Mirrored at:

  - `+0xC0 sub + 0x2B8`
  - `+0xC0 sub + 0x3F8`
  - `+0xF0 sub + 0x028`
  - `+0xF0 sub + 0x168`

The actual `animation_id` u32 (TAE ID) is **NOT yet identified** —
needs a follow-up session using CE's "find what writes to this
address" data-breakpoint on the phase float, which leads to the
animation update function from where we trace back the anim_id
register/source.

### Game time counter — multiple mirrors

A `f32` counter that advances at game-realtime rate (1.0 / second
real-time, when game is unpaused). Mirrors at:

  - `+0xC0 sub + 0x2E0`, `+0x3F8`
  - `+0xF0 sub + 0x050`, `+0x190`, `+0x3D0`
  - `+0x100 sub + 0x080`, `+0x0A0`, `+0x1C0`, `+0x1D0`, `+0x2C0`

All show the same value (e.g. `71.531s` then `73.400s` after 1.9s
real time). NOT animation-specific. Useful as "how long has this
character been alive in the current run".

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

// Confirmed fields (Session 01):

// === Identity ===
chr_name_utf16:    *(PlayerCtrl + 0x118)            // UTF-16 LE; first u16 == 'P'(0x50)=local, 'N'(0x4E)=network phantom

// === Spatial ===
position_xyz:      PlayerCtrl + 0x90                // float[3]
position_w:        PlayerCtrl + 0x9C                // f32 == 1.0 (homogeneous marker — sanity check)

// === Stats ===
hp_current:        PlayerCtrl + 0x168               // u32
hp_max_w_buffs:    PlayerCtrl + 0x170               // u32 (effective max incl. ring bonuses)
hp_max_base:       PlayerCtrl + 0x174               // u32 (base max without buffs)

equip_load_max:    PlayerCtrl + 0x1AC               // f32 (also mirrored at +0x1B4)
equip_weight_cur:  PlayerCtrl + 0x1B8               // f32 (also mirrored at +0x1C0)

// === World context ===
zone_primary:      *(PlayerCtrl + 0xC0) + 0x13C     // u32 PlayAreaParam ID (e.g. 101000 = Forest of Fallen Giants)
zone_secondary:    *(PlayerCtrl + 0xC0) + 0x140     // u32 PlayAreaParam ID (sub-zone)

// === Equipment loadout (22 slots × 20 bytes each, stride 0x14) ===
//   Per-slot layout: u32 item_id, u32 flag/count, u32, u32, f32 weight
//   item_id is the direct Paramdex ID — no decoding needed.
//   Empty hand slot = 3400000 (= "Fists"), not -1.
equip_array_base:  *(PlayerCtrl + 0xE0) + 0x37C
//   slot 0:  +0x37C  right-hand weapon 1 (R1)
//   slot 1:  +0x390  right-hand weapon 2 (R2)
//   slot 2:  +0x3A4  right-hand weapon 3 (R3)
//   slot 3:  +0x3B8  left-hand weapon 1 (L1)
//   slot 4:  +0x3CC  left-hand weapon 2 (L2)
//   slot 5:  +0x3E0  left-hand weapon 3 (L3)
//   slot 6:  +0x3F4  head armor
//   slot 7:  +0x408  chest armor
//   slot 8:  +0x41C  arms armor
//   slot 9:  +0x430  feet armor
//   slots 10–15: +0x444..+0x4A8  ammo (arrow×2 + bolt×2 + reserved×2)
//   slots 16–19: +0x4BC..+0x4F8  rings 1–4
//   slots 20–22: +0x50C..+0x534  quickbar consumables 1–3

// === Animation (partial — full anim_id field deferred to Session 02) ===
anim_phase:        *(PlayerCtrl + 0xC0) + 0x2B8     // f32 normalized 0..1 (mirrored at +0x3F8 and +0xF0 sub +0x028 / +0x168)
game_time:         *(PlayerCtrl + 0xC0) + 0x2E0     // f32 real-time seconds (many mirrors)
```

## ⚠️ Anti-cheat detection on hardware breakpoints — DO NOT RETRY

End-of-session-01 finding: setting a CE hardware **write breakpoint**
on `*(PlayerCtrl + 0xC0) + 0x2B8` (the animation phase float)
triggered DS2's FROM anti-cheat — the game booted the player back
to the main menu within seconds. The HW debug registers (DR0..DR3)
are checked by FROM's anti-cheat layer when online services are
active. Plain `read_memory` calls remain undetected (we've made
hundreds in this session); only the BP triggered the kick.

For animation hunt and any future writer-tracing work on DS2:

- **AVOID** `set_data_breakpoint` while DS2 is online with the
  brother summoned.
- **PREFER** DBVM watches (`start_dbvm_watch`) — they run in
  hypervisor ring -1, invisible to user-mode anti-cheat. The
  trade-off is the user must have the DBVM driver loaded (a
  separate one-time install).
- **OR** do it via static analysis: open DarkSoulsII.exe in Ghidra,
  find the function that writes to the phase offset
  (`*PlayerCtrl + 0xC0_sub + 0x2B8`), and read off the anim_id
  source register from the disassembly. Doesn't need the game
  running and can't possibly trigger AC.

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
