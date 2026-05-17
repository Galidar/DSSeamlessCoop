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

## The request struct (param_1 of FUN_1401A1650)

This is the struct we have to allocate + populate to drive the
spawn. From the decompiled body, the fields actually consumed are:

| Offset | Type | Use | Source from our SHM |
|---|---|---|---|
| `+0x10` | `void*` | **OUTPUT — spawned PlayerCtrl ptr** | n/a, we read it after the call |
| `+0x29` | `u8` | Phantom-type flag (controls NetworkPlayer vs GhostPlayer name + 0x12/0x13 inner-type) | always 0 for white phantom |
| (TBD) | varies | position/rotation, player_id, equipment hash, ... | from our SHM peer entry |

**The full struct layout still needs more reading** of FUN_1401A1650's
body before we can populate it accurately. The unmapped fields are
read at offsets like `+0x10`, `+0x29`, and via deep indirections.
Next-session work: walk every `param_1 + 0xXX` deref inside
FUN_1401A1650 and document.

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
