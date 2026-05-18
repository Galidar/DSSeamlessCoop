# SAPONITA — DS2 Co-op Summon System Reference

Authoritative reference for everything we know about DS2 SOTFS's
saponita (white sign soapstone) summon system. Includes exact memory
addresses (RVA + live), code locations to modify, behavior notes, and
ready-to-paste C++ snippets.

This document supersedes the partial info scattered across
`TRACK_C_PHASE_2B2_DESIGN.md`, `TRACK_C_RE_SESSION_01.md`, and
`templates/saponita_timer_map.md`. Last live-verified 2026-05-18.

---

## TL;DR — what we can do RIGHT NOW

1. **Freeze the saponita session timer** (= infinite co-op duration).
   Write `500.0f` to `phantom_mgr + 0x218` every ~500 ms while a
   phantom is active. Engine has zero guards.

2. **Cut a session short**. Write `0.0f` to `phantom_mgr + 0x218`.
   Engine stops decrementing. (Does NOT auto-trigger return-home;
   that's a separate state flag we haven't bypassed yet.)

3. **Engine-spawn a phantom without saponita sign**. Build a 0x640-
   byte structured record at a free queue slot
   (`phantom_mgr + 0x5C0 + N*0x640`), set `+0x631 = 1` (ready flag).
   Engine's per-tick dispatcher `FUN_14051DBB0` picks it up and
   calls `FUN_14051CE20` → `FUN_1403572A0` → `FUN_1403572E0` (the
   real allocator with `L"NetworkPlayer_%06u"` string). NO compact-
    0x39 protobuf needed — the queue holds the DECODED structured
   form directly.

---

## 1. Pointer resolution chain (cross-launch, ASLR-safe)

All addresses are resolved at runtime from `ds2_base` (= module
handle of `DarkSoulsII.exe`).

```
ds2_base                  = (uintptr_t)GetModuleHandleW(L"DarkSoulsII.exe")

# Game manager (existing — already used by Bonfire)
gm_global_addr            = ds2_base + 0x16148F0   # .data slot
gm                        = *(uintptr_t*)(gm_global_addr)

# World / phantom slot pool (existing)
world_mgr                 = *(uintptr_t*)(gm + 0x18)
slot_mgr                  = *(uintptr_t*)(gm + 0x650)

# Phantom manager (NEW — discovered 2026-05-18)
phantom_mgr_holder_addr   = ds2_base + 0x1616CF8   # .data slot
                                                    # ⚠ NOT 0x16616CF8
                                                    #   (earlier doc was wrong)
phantom_mgr_holder        = *(uintptr_t*)(phantom_mgr_holder_addr)
phantom_mgr               = *(uintptr_t*)(phantom_mgr_holder + 0x20)
```

Existing AOB anchors (`Source/Injector/Hooks/DarkSouls2/DS2_NativeRuntimeHook.cpp`):

- `kSpawnEntryRva = 0x1A1650` — FUN_1401A1650, host-side spawn entry
- `kPhantomSpawnerRva = 0x3572E0` — FUN_1403572E0, the actual allocator
- `kQueueDispatchRva = 0x51CE20` — FUN_14051CE20, per-entry dispatcher
- `kPlayerCtrlCtorRva = 0x37EBE0` — FUN_14037EBE0, PlayerCtrl ctor

New RVAs to add for saponita features:

```cpp
// Saponita session timer + max constant + decrement-flag region
constexpr uintptr_t kPhantomMgrHolderRva = 0x1616CF8;   // .data slot
constexpr uintptr_t kPhantomMgrSubOffset = 0x20;        // holder + 0x20 = phantom_mgr
constexpr uintptr_t kSaponitaTimerOffset = 0x218;       // float countdown
constexpr uintptr_t kSaponitaTimerMaxOff = 0x230;       // float max constant (500.0)
constexpr uintptr_t kPhantomCountOffset  = 0x010;       // u32 phantom count mirror

// Phantom-spawn queue layout
constexpr uintptr_t kQueueBaseOffset     = 0x5C0;       // queue start within phantom_mgr
constexpr size_t    kQueueEntryStride    = 0x640;       // bytes per entry
constexpr size_t    kQueueEntryCount     = 8;           // up to 8 pending spawns
```

---

## 2. The Saponita Session Timer — `phantom_mgr + 0x218`

### Behavior verified live

- **Type**: `float` (single precision)
- **Initial value when phantom joins**: `500.0` (= 8 min 20 sec, matches
  DS2 SOTFS vanilla co-op duration)
- **Tick rate**: decrements at exactly `1.0/sec` while a phantom is
  active in any slot
- **Clamp**: engine stops decrementing once value reaches 0 (observed
  `-0.02` minimum due to one-frame overshoot)
- **Write validation**: NONE. Engine accepts any float value:
    - `9999.0f` stays 9999.0
    - `-50.0f` stays -50.0
    - `0.0f` stays 0 (decrements ~1 frame then stops)
    - `500.0f` resumes ticking normally (if a phantom is active)
- **Separate `should_decrement` flag**: once the timer first hits 0
  in a session, the engine flips an internal flag OFF and stops
  decrementing. Re-writing the timer to e.g. 500 does NOT re-arm.
  Decrement only restarts on NEXT phantom summon event.

### Live values observed (2026-05-18, brother present then left)

| Time | Value | Note |
|---|---|---|
| Brother active | (presumably 500.0 → decrementing) | Not captured directly |
| Brother just left | 149.5475 | First read |
| +5 sec | 144.2821 | Decreased by 5.27 (1.05/sec — matches frame jitter around 1.0) |
| After write 9999 | 9999.00 | Accepted, no clamp |
| After write -50 | -50.00 | Accepted, no clamp |
| After write 0 | 0.00 → -0.02 | One-frame overshoot, then stopped |
| Restored to 500 (post-zero) | 500.00 | Stays at 500 forever (decrement flag now OFF) |

### Field neighbors

| Offset | Type | Live | Probable meaning |
|---|---|---|---|
| `+0x010` | u32 | 2 | phantom count (mirror of `world_mgr + 0x301`) |
| `+0x198` | float | 10.000 | warning/threshold constant (unverified) |
| `+0x19C` | float | 10.017 | paired with +0x198 |
| `+0x1A0` | float | 0.900 | interval/ratio |
| `+0x1E8` | qword | live PlayerCtrl* | last summoned phantom (persists post-disconnect) |
| `+0x218` | **float** | 149→0 | **THE TIMER** |
| `+0x220` | float | 0.500 | tick interval (likely) |
| `+0x230` | float | **500.000** | **MAX TIMER VALUE** |

---

## 3. Phantom-spawn queue — `phantom_mgr + 0x5C0`

### Layout

8 entries, 0x640 bytes each (total queue size `0x3200` bytes spanning
`+0x5C0..+0x37C0`). Each entry is a STRUCTURED RECORD — NOT compact-
0x39 protobuf. The compact-0x39 format only exists briefly on the
network receive path; by the time data lands in the queue it's been
DECODED into this structured form.

### Per-entry field map

| Offset (within entry) | Type | Purpose |
|---|---|---|
| `+0x40..+0x4B` | `vec3 float` | spawn position (X, Y, Z) |
| `+0x70..+0x7F` | 4 × u32 | rotation / quaternion |
| `+0x80` | u32 | type field. `0xE` = "no entry sentinel". Real phantoms have type < 1000 (range observed: 1-100s). |
| `+0x9C` | u32 | passed to FUN_140338a50 (animation init) |
| `+0x1CC..+0x1D7` | vec3 float | secondary position (target?) |
| `+0x270` | u8 | character level (engine clamps ≤ 0x14 = 20) |
| `+0x271` | u8 | state byte (consumed by FUN_14051CE20 branches) |
| `+0x272..+0x27B` | 10 bytes | weapon levels (iterated → FUN_14038be20) |
| `+0x27C..+0x28F` | 10 × u16 | weapon stats (iterated → FUN_14038be00) |
| `+0x290..+0x29A` | 11 bytes | armor IDs (FUN_14038aca0) |
| `+0x2B8..+0x2BF` | qword | gesture / animation state |
| `+0x2C0` | u32 | unknown counter |
| `+0x2C4..+0x2D7` | data | passed to FUN_14016eed0 (validation) |
| `+0x2D8` | int | HP base (clamped vs PlayerCtrl +0x16C/+0x170 in dispatcher) |
| `+0x630` | u8 | status (memset cleared post-dispatch by FUN_14051DBB0) |
| `+0x631` | u8 | **ready flag — engine processes entry when != 0** |

### Dispatch flow

```
FUN_14051C940(phantom_mgr, dt)              # per-frame tick
  └ FUN_14051DBB0(phantom_mgr)              # queue dispatcher
      └ for each entry where (status @+0x631 != 0)
          and not sentinel (+0x80 != 0xE):
            FUN_14051CE20(phantom_mgr, entry)   # per-entry processor
              ├ reads structured fields from entry
              ├ builds small local "summary" struct (~0x60 bytes)
              └ FUN_1403572A0(world_mgr, &summary)
                  └ FUN_1403572E0(world_mgr, &out_pctrl, &summary)
                      └ FUN_14037EBE0(slot, mgr_rec, world, idx)
                        # PlayerCtrl ctor at RVA 0x37EBE0
              after success: memset entry+0x40..+0x630 = 0 (0x5F0 bytes)
```

### To inject a synthetic phantom (Phase 4d)

1. Resolve `phantom_mgr`.
2. Walk queue entries 0..7, find one where `*(u8*)(entry + 0x631) == 0`
   AND `*(u32*)(entry + 0x80) == 0xE` (sentinel). Both conditions =
   safe slot to write.
3. Populate the structured fields above with source data (live slot 1
   PlayerCtrl, saved template, or peer SHM data).
4. Set `*(u8*)(entry + 0x631) = 1` (ready) LAST.
5. Engine dispatches on next tick. PlayerCtrl appears in a free
   slot 1-5.

Optional: write `*(float*)(phantom_mgr + 0x218) = 500.0` to ensure
timer starts fresh.

---

## 4. Slot pool — where the spawned PlayerCtrl ends up

Already documented in `TRACK_C_PHASE_2B2_DESIGN.md` but consolidated
here for the saponita context.

```
slot_mgr      = *(uintptr_t*)(gm + 0x650)
slot_rec_N    = slot_mgr + 0x5D0 + N * 0xA90       # N in 0..5
playerctrl_N  = *(uintptr_t*)(slot_rec_N + 0xC8)
```

| Slot | Role |
|---|---|
| 0 | Local player (you) |
| 1 | First summoned phantom |
| 2-5 | Additional phantom slots (pre-allocated 1 MB each, empty by default) |

A slot is "active" when:
- `*(uintptr_t*)(playerctrl_N) >= 0x7FF7_0000_0000` AND `<= 0x7FF8_0000_0000`
  (vtable points into DS2.exe module range)

A slot is "empty" when:
- The vtable pointer at `*(playerctrl_N)` points back into `slot_mgr`
  scratch memory (~`0x7FF4_xxxx_xxxx` heap range)

### Slot record activation markers

When a slot transitions from empty → active, these fields change:

| Offset | Empty | Active | Purpose |
|---|---|---|---|
| `+0x0A0` | 0x1025 | 0x1023 | flags |
| `+0x0C0` | 0x100000 (1 MB free) | ~0x801E0 (~526 KB used) | size used |
| `+0x0E0` | 0x0 | 0xEB / 0xDE / similar | refcount / active flag |
| `+0x0C8` | dangling alloc | live PlayerCtrl* | the slot's PlayerCtrl pointer |

---

## 5. Where to add the code

### A. Saponita timer manipulation (smallest, safest feature)

**File**: `Source/Injector/Hooks/DarkSouls2/DS2_NativeRuntimeHook.cpp`

**Add near the top (alongside other RVA constants ~line 80):**

```cpp
// Saponita session timer + the queue manager that holds it
constexpr uintptr_t kPhantomMgrHolderRva = 0x1616CF8;
constexpr uintptr_t kPhantomMgrSubOffset = 0x20;
constexpr uintptr_t kSaponitaTimerOffset = 0x218;
constexpr uintptr_t kSaponitaTimerMaxOff = 0x230;
constexpr uintptr_t kSaponitaTimerDefault = 500.0f;
```

**Add a resolver helper (somewhere near `TryReadCameraVP` is fine):**

```cpp
// Resolves phantom_mgr via the gm chain. Returns 0 if any link is null.
uintptr_t DS2_TryResolvePhantomMgr()
{
    const uintptr_t ds2_base =
        reinterpret_cast<uintptr_t>(GetModuleHandleW(L"DarkSoulsII.exe"));
    if (ds2_base == 0) return 0;

    __try
    {
        uintptr_t holder = *reinterpret_cast<const uintptr_t*>(
            ds2_base + kPhantomMgrHolderRva);
        if (holder == 0) return 0;
        return *reinterpret_cast<const uintptr_t*>(
            holder + kPhantomMgrSubOffset);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return 0;
    }
}
```

**Add 3 helpers:**

```cpp
// Read current timer value (NaN/0 on failure).
float DS2_SaponitaTimer_Read()
{
    const uintptr_t pm = DS2_TryResolvePhantomMgr();
    if (pm == 0) return 0.0f;
    __try {
        return *reinterpret_cast<const float*>(pm + kSaponitaTimerOffset);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0.0f;
    }
}

// Write any float to the timer. No engine validation — caller's responsibility.
bool DS2_SaponitaTimer_Write(float value)
{
    const uintptr_t pm = DS2_TryResolvePhantomMgr();
    if (pm == 0) return false;
    __try {
        *reinterpret_cast<float*>(pm + kSaponitaTimerOffset) = value;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// Optional: change the MAX constant. Affects what new summons reset to.
bool DS2_SaponitaTimer_WriteMax(float value)
{
    const uintptr_t pm = DS2_TryResolvePhantomMgr();
    if (pm == 0) return false;
    __try {
        *reinterpret_cast<float*>(pm + kSaponitaTimerMaxOff) = value;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
```

**Wire to command bus (near line ~5010 where `phantom.clone` was added):**

```cpp
if (command == "saponita.timer.freeze")
{
    bool ok = DS2_SaponitaTimer_Write(500.0f);
    payload["timer_set_to"] = 500.0f;
    payload["ok"] = ok;
    AppendRuntimeEvent(config, "saponita.timer.freeze", payload);
    return;
}
if (command == "saponita.timer.kill")
{
    bool ok = DS2_SaponitaTimer_Write(0.0f);
    payload["timer_set_to"] = 0.0f;
    payload["ok"] = ok;
    AppendRuntimeEvent(config, "saponita.timer.kill", payload);
    return;
}
if (command == "saponita.timer.set")
{
    float v = 500.0f;
    if (data.contains("value") && data["value"].is_number())
        v = data["value"].get<float>();
    bool ok = DS2_SaponitaTimer_Write(v);
    payload["timer_set_to"] = v;
    payload["ok"] = ok;
    AppendRuntimeEvent(config, "saponita.timer.set", payload);
    return;
}
```

**Expose in heartbeat (where other diagnostics are dumped):**

```cpp
heartbeat["saponita_timer"] = DS2_SaponitaTimer_Read();
heartbeat["saponita_phantom_count"] = /* read u8 at world_mgr+0x301 */;
```

### B. Freeze-timer auto-loop (true infinite saponita)

Add to the runtime worker thread (near `PollCommandInbox` loop). A
simple boolean toggled by a `saponita.timer.freeze_auto` command:

```cpp
std::atomic<bool> s_saponita_freeze_auto{false};

// In the main worker loop, every ~500 ms:
if (s_saponita_freeze_auto.load()) {
    DS2_SaponitaTimer_Write(kSaponitaTimerDefault);
}

// Command handler:
if (command == "saponita.timer.freeze_auto") {
    bool enable = true;
    if (data.contains("enable") && data["enable"].is_boolean())
        enable = data["enable"].get<bool>();
    s_saponita_freeze_auto.store(enable);
    payload["freeze_auto"] = enable;
    AppendRuntimeEvent(config, "saponita.timer.freeze_auto", payload);
    return;
}
```

### C. Queue-write spawn (Phase 4d — engine-spawn without sign)

This is the big one. Approach: build a 0x640-byte queue entry filled
with structured fields from a source (slot 1 live OR saved template
`Docs/templates/live_slot1_playerctrl.bin`), write to a free queue
slot, set ready flag.

**File**: new helper in
`Source/Injector/Hooks/DarkSouls2/DS2_NativeRuntimeHook.cpp`.

Pseudo-skeleton (full implementation is a separate task — listed here
for the doc; the actual code is ~150 LOC):

```cpp
bool DS2_TryEngineSpawnFromQueue(
    const PeerData& peer,           // position, equipment IDs, etc.
    int* out_target_slot)
{
    auto pm = DS2_TryResolvePhantomMgr();
    if (!pm) return false;

    __try {
        // 1. Find a free queue entry.
        const uintptr_t queue_base = pm + 0x5C0;
        uintptr_t entry = 0;
        for (int i = 0; i < 8; ++i) {
            uintptr_t e = queue_base + i * 0x640;
            uint32_t type = *(uint32_t*)(e + 0x80);
            uint8_t ready = *(uint8_t*)(e + 0x631);
            if (type == 0xE || ready == 0) {
                entry = e;
                break;
            }
        }
        if (!entry) return false;

        // 2. Zero the entry first (avoid stale data tripping checks).
        ZeroMemory((void*)entry, 0x640);

        // 3. Populate structured fields.
        // Position
        *(float*)(entry + 0x40) = peer.x;
        *(float*)(entry + 0x44) = peer.y;
        *(float*)(entry + 0x48) = peer.z;
        // Rotation (identity quat or peer's yaw)
        *(uint32_t*)(entry + 0x70) = 0;  // ...
        // Type field — use a known valid phantom type (NOT 0xE)
        *(uint32_t*)(entry + 0x80) = 0x12;  // matches NetworkPlayer mode flag
        // Level (clamp 0..20)
        *(uint8_t*)(entry + 0x270) = peer.level;
        // Equipment iteration (10 weapon levels, 10 stats, 11 armor)
        memcpy((void*)(entry + 0x272), peer.weapon_levels, 10);
        memcpy((void*)(entry + 0x27C), peer.weapon_stats, 20);
        memcpy((void*)(entry + 0x290), peer.armor_ids, 11);
        // HP base
        *(int32_t*)(entry + 0x2D8) = peer.hp_max;

        // 4. Reset session timer (so the new session starts at 500s).
        *(float*)(pm + kSaponitaTimerOffset) = kSaponitaTimerDefault;

        // 5. Set ready flag LAST.
        *(uint8_t*)(entry + 0x631) = 1;

        // Engine's next per-tick FUN_14051DBB0 picks it up.
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
```

Wire to Eye Orb action (62061000) in the custom-item path.

---

## 6. What we DON'T yet know

- **Exact meaning of `+0x80` type field**: observed `0xE` as sentinel
  but valid spawn types untested. Educated guess: `0x12`/`0x13` =
  NetworkPlayer/GhostPlayer based on Ghidra constants found earlier
  (see `TRACK_C_PHASE_2B2_DESIGN.md` outer wrapper +0x29 phantom_type
  byte that drives the wide-string format selection).
- **The "should_decrement" flag location**: timer at +0x218 stops at
  0 due to a separate flag. Probably a single bit somewhere in
  `phantom_mgr` we haven't pinpointed. Finding it lets us re-arm the
  decrement post-zero (for forcing return-home).
- **What the FUN_14016eed0 validation at entry+0x2C4 checks**: if our
  synthetic queue entry fails this, dispatch will reject. Most likely
  it's checksumming the structured fields or comparing against a
  server-issued token.
- **Full mapping of weapon-stats / armor-IDs to peer SHM data**: we
  have RAW byte counts (10/20/11) but not the per-byte semantics yet.

---

## 7. Files touched by saponita-related changes (existing)

| File | Purpose |
|---|---|
| `Source/Injector/Hooks/DarkSouls2/DS2_NativeRuntimeHook.cpp` | All hooks + command bus. New helpers + commands go here. |
| `Source/Injector/Hooks/DarkSouls2/DS2_NativeRuntimeHook.h` | (no changes needed) |
| `Source/BonfireService/Modules/Ds2NativePoseBridge.cs` | Exposes RVAs in `bridge.status`. Add saponita_timer_offset etc. |
| `Source/BonfireService/Modules/Ds2MemoryReader.cs` | Has the `gm_imp_global` resolver. Optionally add a `TryReadSaponitaTimer()` mirror. |

## 8. Templates on disk

`Docs/templates/` holds live forensic dumps for reference:

| File | What | When |
|---|---|---|
| `live_slot1_playerctrl.bin` | 0x4A0 bytes — brother's PlayerCtrl JUST before disconnect | 2026-05-17 |
| `live_slot1_record.bin` | 0xA90 bytes — brother's slot manager record | 2026-05-17 |
| `slot1_playerctrl.bin` | older capture | 2026-05-17 (earlier session) |
| `queue_entry_5.bin` | 0x640 bytes — residual queue entry data | 2026-05-17 |
| `queue_entry_6.bin` | 0x640 bytes | 2026-05-17 |
| `queue_entry_7.bin` | 0x640 bytes | 2026-05-17 |
| `saponita_timer_map.md` | initial timer map notes | 2026-05-17 |
| `slot1_sub_*.bin` | sub-struct dumps (intermediate, weapon mgr, etc.) | 2026-05-17 |

## 9. Vanilla saponita reference

For comparison: DS2 SOTFS vanilla saponita summon mechanism. Anything
we synthesize must produce equivalent visible behavior.

- **Sign placement**: player drops a `Soapstone` (item ID 60155000
  for white, 60156000 for red etc.) → server stores sign with
  position + bearer's session token.
- **Host clicks sign**: server validates Soul Memory match + level
  range + phantom cap (max 4 in DS2 SOTFS) + `world_mgr+0x301`
  current count + 6. Server sends compact-0x39 wire packet to host.
- **Host receives packet**: network layer decodes compact-0x39 into
  the structured queue-entry format at `phantom_mgr+0x5C0+N*0x640`,
  sets `+0x631 = 1` ready.
- **Per-tick dispatcher** (`FUN_14051DBB0`) picks up entry, calls
  `FUN_14051CE20` → `FUN_1403572A0` → `FUN_1403572E0` (allocator).
- **Allocator** calls `FUN_140833320(0x4A0, 0x10)` to get fresh
  PlayerCtrl memory, then `FUN_14037EBE0` ctor with
  `(slot, mgr_rec, world, idx)` args.
- **Slot record activated**: flags set at +0x0A0/+0x0C0/+0x0E0;
  PlayerCtrl* written to +0xC8.
- **Timer reset**: `phantom_mgr+0x218 = 500.0` (8min20s countdown
  starts).
- **Engine renders** the new PlayerCtrl as a real character mesh
  using its equipment, animations, etc.
- **Session ends** when: host beats area boss, OR host dies, OR
  phantom dies, OR phantom uses homeward bone, OR timer expires.

Our Phase 4d goal: do step "engine renders the new PlayerCtrl" WITHOUT
the sign-placement and network round-trip. Direct queue-write does
exactly that.
