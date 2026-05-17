# Track C Phase 2B.2 — Engine-cooperative phantom spawn design

Date: 2026-05-17
Status: **design complete; implementation ready**
Predecessors:
- `TRACK_C_RE_SESSION_01.md` — live CE RE (gm chain, PlayerCtrl, char_data)
- `TRACK_C_GHIDRA_SESSION_01.md` — Ghidra static analysis (function RVAs)
- v2.9.6 + v2.9.7 — hook observers; captured 6 ctor hits + slot pool
- v2.9.9 — gated experimental hooks behind `BONFIRE_DS2_RE_HOOKS=1`

## Goal

Make a peer's character appear as a **full, engine-rendered phantom**
in the host's world — with real mesh, real armor, real animations
— **without** going through DS2's vanilla matchmaking. Our existing
P2P pipe already ships the peer's `Frpg2PlayerData`-equivalent
char_data; this design says how to feed it to the engine's own
spawner so DS2 renders a phantom for us.

## The spawn chain (fully reverse-engineered)

```
                 (caller in network msg handler — line 347603)
                                │
                                ▼
                  FUN_1401A1650(struct* req)            RVA 0x1A1650  ← 1-arg entry ⭐
                  reads req->+0x29 (phantom_type flag)
                  builds the inner local_348 request from req
                                │
                                ▼
                  FUN_1403572A0(world_mgr, req2)        RVA 0x3572A0  ← clean wrapper
                  manages the output buffer on stack
                                │
                                ▼
                  FUN_1403572E0(world_mgr, out, req2)   RVA 0x3572E0  ← THE phantom spawner
                  literal string "NetworkPlayer_%06u"
                  allocates 0x4A0 bytes via FUN_140833320
                                │
                                ▼
                  FUN_14037EBE0(slot, mgr_rec, world, idx) RVA 0x37EBE0  ← PlayerCtrl ctor
                  sets vtable, zeros tail fields
```

After `FUN_1401A1650` returns, the spawned `PlayerCtrl*` is at
`*(req + 0x10)`. If null, the spawn failed (engine refused — would
go through the error path `FUN_1401A0C70`).

## Global anchors

| Anchor | Use |
|---|---|
| **`DAT_1416148F0`** | The "GameManager singleton" global. Multiple sub-managers reachable from here at fixed offsets. AOB to be added in Phase 2B.2A implementation. |
| `*(DAT_1416148F0 + 0x18)` | **`world_mgr`** — the `param_1` we pass into the spawn chain. Same in every caller. |
| `*(DAT_1416148F0 + 0x650)` | Slot-manager array base; slot `i` at `+ 0x5D0 + i × 0xA90`. |
| `*(DAT_1416148F0 + 0xA8)` | Another sub-manager (used by FUN_140357920). |

The slot manager array's first 6 entries are the phantom slots
documented in `TRACK_C_RE_SESSION_01.md` (slots 0=local, 1=phantom,
2–5=empty pre-allocated 1MB each).

## The request struct (param_1 of FUN_1401A1650) — FULLY MAPPED

**Update (Phase 2B.2B complete)**: walked every `param_1 + 0xXX`
access inside FUN_1401A1650 and the upstream dispatcher
`FUN_1401A0D20` (RVA 0x1A0D20). The struct is much smaller than
feared — only 7 fields touched by the spawn entry.

**Outer wrapper struct** (~0x30 bytes — call it `Ds2PhantomRequest`):

| Offset | Width | Direction | Meaning |
|---|---|---|---|
| `+0x08` | `int` | **IN/OUT** state machine: `0=spawn`, `1=alive`, `2=leaving`, `3=dead`. Set 0 to drive a fresh spawn. Engine flips to 1 on success. |
| **`+0x10`** | `PlayerCtrl*` | **OUT** | Spawned phantom pointer. Read this after the call. Null = spawn refused. |
| `+0x18` | u32 | reset to 0 by `FUN_1401A1650` | internal state |
| `+0x1C` | u32 | reset to 0xFFFFFFFF | error/slot sentinel |
| **`+0x20`** | `void*` | **IN — REQUIRED non-zero** | Pointer to the inner request struct (see below). |
| `+0x28` | u8 | reset to 0 | internal flag |
| **`+0x29`** | u8 | **IN** | Phantom type: 0=NetworkPlayer/white, 1=GhostPlayer/red. Drives `0x12+flag` → 0x12 or 0x13 in the inner spawn struct, which selects the wide-string name format (`L"NetworkPlayer_%06u"` vs `L"GhostPlayer_%06u"`). |

**Inner data struct** (`*(outer + 0x20)`):

| Offset | Width | Direction | Meaning |
|---|---|---|---|
| `+0x08` | qword | IN | Pointer to peer identity / character data. Read by 3 sibling validators (`FUN_1401A2680/2740/2800`) that fill local stack buffers later passed into `FUN_1401A0E40`. **The qword points at a Frpg2-style protobuf-deserialized struct** (see below). |
| `+0x14` | u32 | IN | **player_id** — the integer that ends up formatted into `NetworkPlayer_%06u` (= our `000100` etc). |

**`inner+0x08` deep dive** (`FUN_1401A2680` body):

```c
void FUN_1401A2680(u32* out_16bytes, void* protobuf_data) {
  if (protobuf_data == 0) return;
  uVar8 = *(u16*)(protobuf_data + 0x1F0) >> 0xC;   // upper 4 bits = entry count
  puVar6 = protobuf_data + 0x1F0 + *(u16*)(protobuf_data + 0x1F2)
                              + *(u16*)(protobuf_data + 0x02) * 4;
  // Iterates packed entries; on tag (uVar1 & 0x3F) == 0, extracts 16 bytes
}
```

This is **classic Frpg2 protobuf in-memory encoding**:
- `+0x02`: a u16 length/offset
- `+0x1F0`: u16 with a packed (count, flags) header
- `+0x1F2`: u16 offset
- Packed entries follow with (tag,length,payload) shape

**The struct is ~0x200+ bytes** and corresponds to the in-memory
form of `Frpg2RequestMessage::PlayerCharacterData` (the same
message ID we saw advertised at line 2723817 of decompiled.c).
Synthesizing from scratch requires understanding the Frpg2
encoding. There are three more pragmatic paths:

## Three paths for Phase 2B.2C inner-data synthesis

### Path A — full synthesis from scratch (hardest)

Build the ~0x200-byte protobuf-encoded struct in C from our SHM
peer data. Requires reverse-engineering every field tag + offset
that the 3 validators consume. Estimated 4-8 h of additional RE.

### Path B — capture & replay (recommended next step) ⭐

1. Add a one-shot **capture hook** at the entry to
   `FUN_1401A1650`. When the engine spawns a real phantom
   (vanilla saponita), we **snapshot the entire
   Ds2PhantomRequest + its inner struct + the protobuf blob**
   into a file. ~10 KB total per capture.
2. Replay the captured snapshot for our peer: copy the byte-
   pattern into Injector-allocated memory, **patch just the
   delta fields** (player_id at inner+0x14, equipment IDs
   inside the protobuf at known offsets we already mapped in
   RE Session 01).
3. Call `FUN_1401A1650(replayed_request)`.

This trades one constraint (need one vanilla summon ever, to
capture a template) for a massive reduction in RE complexity.

### Path C — hook the upstream deserializer

Find the function that **takes a `PlayerCharacterData`
protobuf bytestream and builds the internal struct**. That
function exists (the network handler uses it to build inner
from the wire format). Call it with our SHM peer's serialized
data. Cleanest architecturally but requires finding +
understanding that deserializer — likely 2-4h of RE.

## Recommendation

**Path B for the first working prototype**. Once we have a
template captured, we can spawn brother any time without
saponita matching. After it's working visually, do Path C as
the production-grade path so we're not dependent on a captured
template (the protobuf layout could change between game patches).

## State machine — `FUN_1401A0D20` dispatcher

```c
void FUN_1401A0D20(Ds2PhantomRequest* req) {
    switch (req->state /* +0x08 */) {
    case 0: if (req->inner /* +0x20 */ != 0
                && FUN_1401A0DC0(req))     // validate
                FUN_1401A1650(req);          // SPAWN (sets state→1 on success)
            break;
    case 1: FUN_1401A1C30(req);              // alive — tick / sync
            break;
    case 2: FUN_1401A1CB0(req);              // leaving — start despawn
            break;
    case 3: FUN_1401A1580(req); ...          // dead — cleanup
            break;
    }
}
```

So the **complete lifecycle in one struct**: allocate a
`Ds2PhantomRequest`, set state=0, populate inner, call the
dispatcher (or skip the pre-validate and call `FUN_1401A1650`
directly). Engine handles all 4 lifecycle phases.

## Implementation update (Phase 2B.2A shipped, 2B.2B partial)

### ✅ Phase 2B.2A — shipped in v2.9.10 (commit 17e570f)

- `Ds2MemoryReader.TryReadWorldMgr()` reads `*(gm_imp_global + 0x18)`
- `bridge.status` exposes `spawn_chain.world_mgr_ptr` + the
  spawn-chain RVAs.

### ✅ Phase 2B.2B (this update) — outer wrapper fully mapped

- 7 outer-struct fields + state machine documented above.
- Inner struct partially mapped: `+0x08` data ptr, `+0x14` player_id.
- Inner `+0x08` shape still pending — needs reading
  `FUN_1401A2680/2740/2800` validator bodies.

### 🚧 Phase 2B.2C — synthetic spawn

Pre-conditions before 2B.2C can attempt a live spawn:

1. **Finish inner `+0x08` mapping** (RE the 3 validators).
2. **Decide on player_id allocation policy** — engine seems to
   want unique u32 IDs per peer; we can derive deterministically
   from our SHM `sender_id` (low 24 bits + offset).
3. **Decide on call mechanism** — option (a) call FUN_1401A1650
   directly (skip pre-validate; faster but skips a safety net),
   option (b) call FUN_1401A0D20 with state=0 (lets the engine's
   own validator run). **Recommend (b) first** — if engine
   refuses, we learn safely.

Then 2B.2C itself:

1. Build a Ds2PhantomRequest + Ds2InnerRequest on the heap from
   the Injector side.
2. Resolve world_mgr (already done via 2B.2A).
3. Call the dispatcher. If state flips to 1, read `+0x10` for
   the PlayerCtrl ptr.
4. Verify in CE: RTTI on the new pointer = "PlayerCtrl", and
   crucially, **the character appears in-game**.

### 🚧 Phase 2B.2D — wire SHM

Same as before — replace hardcoded values with peer SHM lookups.

### 🚧 Phase 2B.2E — bypass slot cap (only if we hit it)

## Implementation plan (Phase 2B.2)

### Phase 2B.2A — anchor + handle plumbing

1. **AOB for `DAT_1416148F0`** — find a stable byte signature near
   a code site that loads it. Add it next to the existing
   `gm_imp_global` AOB in `Ds2MemoryReader.cs` + the Injector.
2. **Runtime resolver** — `Ds2MemoryReader.TryReadGameMgrGlobal()`
   walks the AOB once per process, caches.
3. **Read sanity test** — confirm `*(DAT_1416148F0 + 0x18)` and
   `*(DAT_1416148F0 + 0x650)` resolve to the same kind of
   pointers we saw in v2.9.7 logs across multiple launches.

No code is hooked in 2B.2A. Pure-read only.

### Phase 2B.2B — request struct map

1. **Static-RE** every `param_1 + 0xXX` access inside
   `FUN_1401A1650` (RVA 0x1A1650). Output: a complete C struct
   `Ds2SpawnRequest` describing every field we need to populate.
2. **Cross-check** against the second caller `FUN_14051CE20`
   (RVA 0x51CE20) — different code path, same downstream call,
   shows which fields are essential vs optional.
3. **Document field-by-field** mapping `SHM char_data ↔
   Ds2SpawnRequest`.

### Phase 2B.2C — synthetic spawn (no SHM yet)

1. **Allocate** a `Ds2SpawnRequest` on the Injector heap.
2. **Populate** with hardcoded test data (e.g. player_id `123456`,
   position == local player + offset, default armor IDs).
3. **Resolve** `*(DAT_1416148F0 + 0x18)` to get `world_mgr`.
4. **Call** `((SpawnEntryFn)(base + 0x1A1650))(&request)`.
5. **Read** `request.spawned_ptr` (= `+0x10`) — should be a real
   `PlayerCtrl*` if successful.
6. **Verify in CE**: RTTI on the new pointer → "PlayerCtrl",
   pre-state mostly zero, vtable set, and crucially **the
   character appears in-game** at the synthesized position.

This is the moment of truth — if the engine renders a phantom from
our hardcoded data, we've cleared the entire technical hurdle.

### Phase 2B.2D — wire the SHM data through

1. Replace the hardcoded request data with values pulled from the
   peer's char_data SHM (`Local\BonfireDS2CharDataV1`).
2. Spawn one phantom per peer in `peerCharData[]`.
3. Tear down the phantom when the peer's SHM entry expires (TTL).
4. Update the phantom's position each tick from the peer's pose
   (write to the spawned PlayerCtrl's `+0x90`/`+0x94`/`+0x98`
   floats — we already know that offset from RE Session 01).

### Phase 2B.2E — bypass vanilla limits

The matchmaking restrictions (Soul Memory matching, level range,
4-phantom cap) live in the **packet handler that calls
`FUN_1401A1650`** (line 347603 caller chain). By calling
`FUN_1401A1650` directly we skip those checks entirely. No NOP
patches needed.

The internal `slot_index < 6` check inside `FUN_1403572E0` is the
only remaining cap. If we hit it (which would only happen with
≥5 simultaneous peers), we'd need to widen it via a Detours hook
on the `cmp ..., 6` instruction inside `FUN_1403572E0`. Not
urgent — the user's near-term use case is one peer (the brother).

## What NOT to do

- **Don't hook `FUN_140355930`** — v2.9.6 proved it's not on the
  per-phantom hot path.
- **Don't hook the PlayerCtrl ctor as a producer** — too small,
  too low-level; the caller's post-ctor init is what makes the
  slot usable.
- **Don't try to inject fake `PushRequestSummonSign` protobuf
  messages** — that path goes through Steam P2P + matchmaking
  validation; far more friction than calling `FUN_1401A1650`
  directly.
- **Don't add more Detours hooks while observers are armed** —
  v2.9.7 regression showed that piling on Detours hooks can break
  the ItemUseValidation arm. Phase 2B.2 should AVOID adding new
  hooks during the spawn implementation; ideally we just **call**
  `FUN_1401A1650` from the Injector with no detour at all.

## Anti-cheat considerations

- **`FUN_1401A1650` is a public function inside DS2's .text**.
  Calling it from inside DS2's own process (which is what the
  Injector does — it runs as a DLL injected into DS2) is
  indistinguishable from a normal engine call to anti-cheat,
  because there's no debug register usage, no memory write to a
  protected region, no foreign code crossing trust boundaries.
- The call uses `world_mgr = *(DAT_1416148F0 + 0x18)` — exactly
  the same source the engine uses. AC can't tell our call apart
  from a "regular" engine call.
- **No HW BPs**. Just runtime `read_memory` (already known to be
  AC-clean from our existing pose-bridge work) + a single
  function call.

## Estimated implementation effort

| Phase | Hours | Risk |
|---|---|---|
| 2B.2A (anchor) | ~1h | low — same AOB pattern as existing gm |
| 2B.2B (struct map) | ~2-3h | medium — Ghidra reading |
| 2B.2C (synth spawn) | ~2-3h | **HIGH** — first time calling engine code from injector, can crash DS2 if struct is wrong. Mitigate: only test offline (no brother summoned). |
| 2B.2D (SHM wiring) | ~1-2h | low — mechanical translation |
| 2B.2E (cap bypass) | ~30min | low (one cmp patch) — only if needed |

**Total ~7-10 h** of focused work for the full Phase 2B.2 across
2-3 sessions. The 2B.2C step is where most risk lives; we'll
checkpoint hard there.
