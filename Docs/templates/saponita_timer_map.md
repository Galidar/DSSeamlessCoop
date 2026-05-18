# Saponita / phantom session state map (live 2026-05-17)

Live CE forensic session, DS2 PID 7072, brother just disconnected from
slot 1. Mapped phantom_mgr's full state including the saponita session
timer.

## Resolution chain (cross-launch)

```
ds2_base                = GetModuleHandleW("DarkSoulsII.exe")
gm_global_addr          = ds2_base + 0x16148F0   (gm pointer slot in .data)
gm                      = *(gm_global_addr)
world_mgr               = *(gm + 0x18)
slot_mgr                = *(gm + 0x650)
phantom_mgr_holder_addr = ds2_base + 0x1616CF8   (NOT 0x16616CF8 — earlier doc wrong)
phantom_mgr_holder      = *(phantom_mgr_holder_addr)
phantom_mgr             = *(phantom_mgr_holder + 0x20)
```

This-session live values:
- ds2_base = `0x7FF76F4F0000`
- gm = `0x7FF4CFCE0260`
- world_mgr = `0x7FF4D80E91D0`
- slot_mgr = `0x7FF4D2865160`
- phantom_mgr = `0x7FF4C2CE9940`

## phantom_mgr field map (verified live)

| Offset | Type | Live value | Purpose |
|---|---|---|---|
| `+0x000` | qword | `0x028120C1C190` | low arena pointer (Steam alloc) |
| `+0x008` | u64 | `0x0000000100000001` | two-u32 flags pair, both 1 |
| `+0x010` | u32 | **2** | **phantom_count (mirror of world_mgr+0x301)** |
| `+0x018` | qword | vftable in DS2 module | self-vtable |
| `+0x168` | u32 | 1 | active flag (?) |
| `+0x178` | float (hi half of qword) | -1.000 | sentinel uninitialized |
| `+0x188` | float | -1.000 | sentinel |
| `+0x190` | u32 (hi half) | 2 | another counter = 2 |
| `+0x198` | float | **10.000** | warning threshold (?) — pair with +0x19C |
| `+0x19C` | float | **10.017** | same value, second instance |
| `+0x1A0` | float | 0.900 | interval / ratio |
| `+0x1E8` | qword | **address of last summoned phantom PlayerCtrl** | persistent reference (still points at brother's slot 1 PlayerCtrl even after he disconnected) |
| `+0x1F0` | two-u32 | 2, 2 | duplicate counters |
| `+0x1F8` | two-u32 | 2, 1 | mixed counters |
| `+0x208` | u32 (lo half) | 0x4EE8 (20200) | session ID or seed |
| `+0x218` | **float** | **SAPONITA SESSION TIMER** (countdown 1.0/sec, clamps at 0) | the prize field |
| `+0x220` | float | 0.500 | tick interval (likely) |
| `+0x230` | float | **500.000** | MAX TIMER VALUE (8 min 20 sec) |
| `+0x5C0..+0x2500` | array | 8 × 0x640 byte queue entries | phantom spawn queue |

## Behavior of `+0x218` (the saponita timer)

Live experiments:

1. **Initial state**: timer = 149.5475 (mid-countdown), decrementing at ~1.0/sec.
2. **Write 9999.0**: accepted, no clamping. Engine doesn't reject extreme values.
3. **Write -50.0**: accepted, value stays negative. No engine guard.
4. **Write 0.0**: accepted. Timer continues decrementing for ~1 frame to -0.02, then STOPS.
5. **Write 500.0 after timer hit 0**: accepted but no decrement happens. The engine has a
   separate "should_decrement" flag that flips OFF after the first time the timer hits 0
   in a session. Re-writing the timer value doesn't re-arm the decrement logic.
6. **Repeated writes of 500.0 every 500ms while no phantom is active**: timer stays at
   500.000 indefinitely (no decrement because no active session).

### Implication for "saponita freeze mode"

While a phantom IS active in a slot, the engine ticks +0x218 down at 1.0/sec. To freeze
the saponita timer (= infinite co-op duration), we can:
- Detours-hook the function that DECREMENTS +0x218, OR
- Set up a background thread that writes 500.0 to +0x218 every N ms while a phantom is
  active

The latter is trivial (one Detours-free helper inside the Injector). No engine validation
to bypass — the writes go through cleanly.

### Implication for "force phantom departure"

Setting +0x218 = 0 does NOT immediately trigger phantom return-home. The visual effect is
governed by a SEPARATE function (probably reads the timer AND a state flag). The timer
hitting 0 is a NECESSARY but not SUFFICIENT condition. To force departure we'd need to
also flip the state flag OR call the engine's departure function directly.

## Behavior of `+0x230` (the MAX value)

This is 500.000 — appears to be a static config value (not modified during gameplay).
The "saponita session duration" in DS2 SOTFS is approximately 8 min 20 sec; matches.

Engine likely uses +0x230 as the value to RESET +0x218 to whenever a new phantom joins.
Modifying +0x230 to e.g. 60.0 might shorten next session's duration. Modifying to 9999.0
might extend it. (Untested — no active phantom to verify.)

## Saponita session queue (phantom_mgr + 0x5C0)

8 entries × 0x640 bytes. Each entry is a STRUCTURED RECORD (not compact-0x39 protobuf):

| Offset within entry | Type | Purpose |
|---|---|---|
| `+0x40..+0x4B` | vec3 floats | spawn position (X, Y, Z) |
| `+0x70..+0x7F` | 4 u32 | rotation/quat (likely) |
| `+0x80` | u32 | type field (`0xE` = sentinel "no entry") |
| `+0x9C` | u32 | passed to FUN_140338a50 (animation init?) |
| `+0x1CC..+0x1D7` | vec3 floats | secondary position (target?) |
| `+0x270` | byte | character level (< 0x14 cap) |
| `+0x271` | byte | state (used by various branches in FUN_14051CE20) |
| `+0x272..+0x27B` | 10 bytes | weapon levels (iterated for FUN_14038be20) |
| `+0x27C..+0x28F` | 10 u16 | weapon stats (FUN_14038be00) |
| `+0x290..+0x29A` | 11 bytes | armor IDs (FUN_14038aca0) |
| `+0x2B8..+0x2BF` | qword | gesture / animation state |
| `+0x2C0` | u32 | unknown counter |
| `+0x2C4..` | data | passed to FUN_14016eed0 (some validation) |
| `+0x2D8` | int | HP base (clamp range vs PlayerCtrl+0x16C/+0x170) |
| `+0x630` | byte | status (cleared post-dispatch by FUN_14051DBB0 memset) |
| `+0x631` | byte | ready flag (engine checks != 0 to consider entry pending) |

Per-tick dispatcher: `FUN_14051DBB0(phantom_mgr)` walks entries, for each ready one
calls `FUN_14051CE20(phantom_mgr, entry)` which forwards to `FUN_1403572A0` →
`FUN_1403572E0` (the actual PlayerCtrl allocator).

After successful dispatch, the engine memsets entry+0x40..+0x630 (0x5F0 bytes) to zero.

## For Phase 4d (Eye Orb engine spawn without saponita sign)

We now have a fully-mapped path to drive an engine phantom spawn:

1. Resolve phantom_mgr via the chain above.
2. Find a free queue entry (where `+0x80 == 0xE` or `+0x631 == 0`).
3. Build a structured record using the live source data (slot 1 PlayerCtrl OR
   a saved template from `Docs/templates/live_slot1_*.bin`):
   - Position from peer SHM (or local + offset)
   - Equipment IDs from saved template
   - Level/HP from template
   - Status bytes set to "ready"
4. Write the record to the chosen queue slot.
5. Optionally write +0x218 = 500.0 to reset timer.
6. Next tick, FUN_14051DBB0 picks it up and dispatches.

No need to replicate compact-0x39 protobuf — the queue holds the decoded structured form
directly. This is significantly simpler than the original Phase 4d design assumed.
