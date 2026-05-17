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

**`inner+0x08` deep dive — FULLY MAPPED (Phase 2B.2B-continued)**

After more aggressive Ghidra reading we now have the complete inner
struct format. **It is NOT a protoc-generated MessageLite**. It is
DS2's own compact in-engine snapshot format, and it doubles as the
wire format (no separate serialization step).

```
inner_data layout (call it Ds2PlayerSnapshot):
  +0x00 (u16)   magic = 0x39                ← FUN_1401A2F60 verifies
  +0x02 (u16)   primary_count               ← FUN_1401A2680 multiplier
  +0x04 (u32)   sequence_id (DAT_14160E274++)
  +0x08 (u16)   secondary_count (or zero)
  +0x0A (u16)   padding/flags
  +0x0C..+0x1EF fixed sub-record area (0x1E4 bytes)  ← FUN_1401A2800 copies
  +0x1F0 (u16)  packed[0]: hi-4 = entries, lo-12 = length
  +0x1F2 (u16)  packed[0]: offset to data start
  +0x1F4 ...    more u32 entries (primary_count × 4 bytes total)
  +(0x1F0 + offset + count×4) ...  variable-length entries:
      each entry: (u16 hdr where (hdr & 0x3F) = tag, (hdr >> 6) = length)
                  followed by payload bytes
```

**The builder** is `FUN_1401A29C0(longlong* sources_triple, u16* dst,
u32 dst_size)` — writes magic 0x39 + the rest from THREE source
pointers (`sources_triple[0..2]`, each a sub-manager pointing at
in-engine player data).

**Size computation**: `FUN_1401A2B00(sources_triple)` returns
`0x1F0 + sources[1][2]*4 + sources[2][2]`.

**Encoder usage in DS2** (line ~346155 of `decompiled.c`):
```c
size = FUN_1401A2B00(triple_ptr);
buf  = alloc(size, 0x10);
zero(buf, size);
FUN_1401A29C0(triple_ptr, buf, size);   // build
sink->vtbl[0x18](sink, buf, size);      // send
free(buf);
```

**Decoder** (the receive side): incoming bytes go through
`FUN_1401A2F60` which validates magic 0x39 and traverses the entries
to compute expected size. The bytes are then assigned to
`inner+0x08` directly — **no deserialization step**. The wire format
IS the in-memory format.

### Critical implication — Path C-easiest is INVALIDATED

The protoc-generated `AllStatus` (`DS2_Frpg2PlayerData::AllStatus`)
does NOT have the +0x1F0/+0x1F2/+0x02 layout. Its fields are at
totally different offsets (vtable, _unknown_fields_ string,
_has_bits_, then pointers). **The repo's protobuf source is
server-side scaffolding from the Saponita-server era — it does
not reflect what DS2.exe holds in memory.**

We cannot just feed an `AllStatus*` to `inner+0x08`. The DS2 engine
does NOT translate from `AllStatus` to its compact format anywhere
on the receive path; the compact format is what's transmitted.

### Revised path landscape

## Revised path landscape (post-finding)

### Path A — synthesize the snapshot from scratch (medium)

Hand-build the compact snapshot in C. We now know the layout
(+0x00 magic, +0x02 primary_count, +0x0C..+0x1EF fixed area,
+0x1F0 packed entries). The 0x1E4-byte fixed area is the hard
part — we need to know which tag goes at which offset and how
DS2 indexes them. Estimated 4-6 h to fully reverse the field
mapping, then ~1 h to ship synthesis code.

### Path B — capture & replay (still viable)

1. Hook `FUN_1401A1650` entry (read-only); on every invocation
   snapshot the whole outer wrapper + inner buffer (size = walk
   from magic until the variable-length region ends).
2. Save as a template. **Patch the player_id at +0x14** plus
   any deltas we want changed.
3. Allocate fresh request + inner, copy template, mutate, drive
   `FUN_1401A1650`.

Requires one real summon (vanilla saponita OR brother joining
via the existing seamless-coop server, both fine). Lowest RE
cost. Concrete deliverable: a binary template file in
`Runtime/DS2Native/spawn-captures/`.

### 🎯 Path D — engine self-serialization (recommended) ⭐⭐⭐

**The DS2 engine has a function that builds the snapshot from a
live source: `FUN_1401A29C0(triple_ptr*, buf, size)`.** Our local
PlayerCtrl is alive in memory. The source-triple for the local
player exists too (somewhere reachable from GameManagerImp). We
can:

1. Resolve the **local player's source triple** (`longlong[3]`).
   This is the same triple passed into FUN_1401A29C0 every time
   the local game wants to advertise its character to peers.
2. Call `FUN_1401A2B00(triple)` → size.
3. Allocate `size` bytes (via DS2's own allocator,
   `FUN_140833320`).
4. Call `FUN_1401A29C0(triple, buf, size)` → buf now holds a
   valid serialized local-player snapshot.
5. Patch `+0x14` of buf (the player_id field would be inside the
   variable region — TBD which tag).
6. Allocate `Ds2PhantomRequest` outer wrapper + inner struct;
   set `inner+0x08 = buf`, set the other fields per the wrapper
   map.
7. Call `FUN_1401A1650(wrapper)`.

This **uses DS2's own serializer** so we're guaranteed-correct
format — no synthesis errors possible. First spawn would be a
"clone" of the local player; once that works, Path D-extended
becomes synthesizing different sources from SHM (4-6 h after
the clone-spawn proof of concept).

**Pre-condition for Path D**: find the local player's source
triple. It's reachable from the FUN_14019F520 call chain we
just mapped (`*(param_1 + 0x28) + 0x28` for some `param_1`
that's reachable from a known global). ~1-2 h of RE.

## Recommendation (revised)

**Phase 2B.2C-v1 = Path B capture** — ship a read-only capture
hook on `FUN_1401A1650` entry to harvest one real template AND
to validate the inner-struct layout map empirically. This is a
small (~150 LOC) safe addition. No new build deps. Even if we
never replay, the capture proves out the layout and produces a
concrete artifact to drive next steps.

**Phase 2B.2C-v2 = Path D clone-spawn** — once we have a real
captured template AND we've resolved the local source triple,
we have two independent ways to produce a valid inner buffer.
Cross-check them, then drive `FUN_1401A1650` with a Path D-built
buffer (engine-validated) and the wrapper from the captured
template (also engine-validated for the wrapper fields). First
spawn = local-player clone, lowest possible risk.

**Phase 2B.2C-v3 = SHM-driven Path D** — replace the local
source triple with synthesized sources reflecting the peer's
SHM char_data. Highest payoff, depends on v2 working first.

## 2026-05-17 update — Frpg2PlayerImpl discovery (Path E)

After committing Phase 2B.2C-v1 we dug one more layer and found
the **engine's live AllStatus instances**. They DO exist in
memory. This unlocks a fifth path:

### Class hierarchy reverse-engineered

```
Frpg2ClientImpl  (RVA 0x68f890 — ctor; allocated 0x788 bytes)
  +0x00   Frpg2ClientLib::Frpg2ClientImpl::vftable
  +0x08   <shared base/heap>
  +0x10   <NetSvrMessageManager-like>           (FUN_140692410)
  +0x80   <subsystem>                            (FUN_140695E40)
  +0x90   Frpg2PlayerImpl                        (FUN_140698550) ⭐
    +0x00   Frpg2ClientLib::Frpg2PlayerImpl::vftable
    +0x08   <heap base>
    +0x10   AllStatus #1 (current)               (vftable = Frpg2PlayerData::AllStatus)
    +0x90   AllStatus #2 (cached/previous)
    +0x110  RequestUpdatePlayerCharacter         (vftable = Frpg2RequestMessage::Req...)
    +0x140  <param_2 reference>
    +0x158  DLPlainLightMutex
    +0x190  FUN_1406BCEF0-built sub-struct
    +0x198  FUN_1406BCEF0-built sub-struct
    +0x1A0  u32 counter = 5
    +0x1A4  u32 interval = 0x3C (60)
    +0x1B8..+0x1B8+0x60   FUN_140833320(0xE0) allocated buffer
  +0x258  (subsystem) FUN_14069CD30
  ... more sub-managers
```

### Why this matters

DS2 maintains the local player's character data as a live
`Frpg2PlayerData::AllStatus` C++ object at
`Frpg2PlayerImpl + 0x10`. This means:

1. **Path C-easiest is RESURRECTED, with adjustments.** We do
   NOT feed an `AllStatus*` to `inner+0x08` directly (still
   invalid — that's the compact 0x39 format). But we CAN:
   - **Read** the live AllStatus's fields by walking its
     protoc-generated layout (since we have matching `.pb.h`).
   - **Write** new field values into a cloned AllStatus.
   - **Serialize** via the engine's encoder (which converts
     AllStatus → triple → compact 0x39 format) OR via the
     protoc `Serialize()` (which produces wire bytes, NOT the
     compact format — different).
2. **The triple-ptr that FUN_1401A29C0 consumes is built from
   the AllStatus instances**. The 3 ring buffers at the triple
   likely live INSIDE Frpg2PlayerImpl or one of its sub-
   managers. Each ring entry is 0x1E4 bytes — the same size as
   the fixed area of the compact format.

### Path E — Frpg2PlayerImpl-driven (most powerful)

If we can locate the live `Frpg2ClientImpl` instance:

1. RTTI-scan in CE for vtable `Frpg2ClientLib::Frpg2PlayerImpl`
   OR walk down from `Frpg2ClientImpl` (which is held in
   a global we still need to find — search for stores of
   `Frpg2ClientImpl::vftable`).
2. Read `Frpg2PlayerImpl + 0x10` → current AllStatus.
3. Use the in-repo `DS2_Frpg2PlayerData::AllStatus` class to
   parse the field values (verify by cross-checking known
   values like HP from our existing PlayerCtrl read).
4. Spawn variants:
   - **(a) Direct AllStatus clone**: copy the entire
     0x80 bytes from +0x10 to +0x90 of a new
     Frpg2PlayerImpl-shaped buffer, mutate the player_id,
     call the engine's existing "send" pipeline with that
     buffer. The engine produces the compact format
     internally and routes it. (Hardest — touches the network
     stack.)
   - **(b) Trigger engine-side serialization**: invoke
     whatever function in Frpg2PlayerImpl produces the
     compact format from the AllStatus (likely a vtable
     slot). Capture the buffer post-call. Use it as our
     phantom's inner+0x08.
   - **(c) Plain read for capture-replay**: just observe the
     AllStatus to learn the canonical field values, then use
     Path B's captured wire-format template and patch deltas.

### Searches required (next RE session)

1. **Frpg2ClientImpl storage**: find where the result of
   FUN_14068D910 is stored. Probable singleton getter is
   FUN_14068D840 or similar (returns
   `DAT_141618BC0` based on the partial match at line
   1370921). Confirm `DAT_141618BC0` holds the
   Frpg2ClientImpl pointer in CE.
2. **AllStatus → triple → compact path**: trace from the
   AllStatus instance to the ring-buffer triple that
   FUN_1401A29C0 consumes. The 3 ring buffers must be filled
   from the AllStatus somewhere; find that conversion code.
3. **RequestUpdatePlayerCharacter usage**: this is what
   actually goes over the wire to the server. Map it to the
   compact format (or confirm they're independent paths).

These can be done either statically (Ghidra) or live (CE
RTTI scan). The live approach is faster — DS2 is currently
running per Phase 1 work, and RTTI gives us instant access.

## 2026-05-17 update — Path E confirmed live in CE

Session ran with DS2 PID 20116, base `0x7FF76F4F0000`. We disassembled
`FUN_140698550` (the Frpg2PlayerImpl ctor) and extracted the
vftable directly from its first instructions:

```
0x7FF76FB88566: 48 8D 05 4B 97 A7 00   lea rax,[0x7FF770601CB8]   ; vftable
0x7FF76FB8856D: 48 89 01               mov [rcx],rax
0x7FF76FB88570: 48 89 51 08            mov [rcx+0x08],rdx         ; heap base
0x7FF76FB88574: 48 83 C1 10            add rcx,0x10
0x7FF76FB88578: E8 ...                 call FUN_1406AF5F0          ; init AllStatus + RequestUpdate
```

**Frpg2PlayerImpl::vftable = `0x7FF770601CB8`**  (RVA `0x1111CB8`).

Scanning all writable memory for qword pointers to that vftable
returned **exactly ONE hit**: `0x7FF4B456F610`. That is the
single Frpg2PlayerImpl instance (singleton inside Frpg2ClientImpl).

### Live address map (this session)

| Field | Address |
|---|---|
| Frpg2PlayerImpl | `0x7FF4B456F610` |
| AllStatus #1 (current) | `0x7FF4B456F620` |
| AllStatus #2 (snapshot/cached) | `0x7FF4B456F6A0` |
| RequestUpdatePlayerCharacter | `0x7FF4B456F720` |
| DLPlainLightMutex | `0x7FF4B456F778` |
| counter=5 / interval=60 ✓ | `0x7FF4B456F7B0` |

### AllStatus #1 sub-message map (this session)

| Field | Proto # | Address | vftable |
|---|---|---|---|
| player_location | 1 | `0x7FF4B456FDA0` | `0x7FF770607528` |
| player_status | 2 | `0x7FF4B456FE20` | |
| item_using_info | 3 | `0x7FF4B45700A0` | |
| stats_info | 4 | `0x7FF4B456FF20` | |
| level_status (attributes) | 5 | `0x7FF4B45701C0` | |
| physical_status | 6 | `0x7FF4B4570100` | `0x7FF770607838` |
| weapon_status | 7 | `0x7FF4B4570240` | |
| armor_status | 8 | `0x7FF4B45702C0` | |
| server_side_status | 9 | NULL (server-set, expected) | |
| equipment_info | 10 | `0x7FF4B4570320` | `0x7FF770607988` |

### Layout finding — multi-inheritance

DS2's MessageLite subclasses inherit from a SECOND base
(`FSProtobufExtension::ToStringSettings`) whose vftable sits at
**offset +0x10** in the derived class. Standard protoc layout is
`vtbl + _unknown_fields_ + _has_bits_ + fields`. DS2's is
`vtbl + heap_ptr + ToStringSettings_vtbl + ... + fields`. Field
offsets shift accordingly (e.g. AllStatus's first sub-message
`player_location_` is at **+0x28**, not +0x30 like vanilla protoc).

This is the SAME layout the protoc-generated `.pb.cc` in our repo
produces (the .proto has FSProtobufExtension declared as an option
modifier in the same Generated/ tree), so binary compatibility is
preserved. Reading AllStatus #1 sub-pointers in CE matched .pb.h
field ordering exactly (verified live).

### EquipmentInfo cross-check

EquipmentInfo's repeated fields landed at:
`+0x28: 0 (alignment)`
`+0x30: ptr, size=6, total=6` (consumable slots)
`+0x48: ptr, size=4, total=4` (left-hand weapons)
`+0x60: ptr, size=4, total=4` (right-hand weapons)
`+0x78: ptr, size=4, total=4` (rings)

This matches RE Session 01's mapping of DS2's equipment slot
groupings exactly.

### Implication

**Path E is unblocked.** Frpg2PlayerImpl is reachable via a single
AOB scan (qword equal to Frpg2PlayerImpl::vftable). From there we
walk +0x10 to AllStatus #1 and use the matching protoc-generated
classes in `Source/Server.DarkSouls2/Protobuf/Generated/` to
parse / mutate / serialize the player's character data.

The remaining open question: how does AllStatus → compact-format
(magic 0x39) conversion happen inside the engine? Either:
- It's in a vtable slot of AllStatus (so we call it via the live
  instance pointer), OR
- It's a separate engine function that takes AllStatus + a triple
  of ring buffers built elsewhere.

Saponita-time observation (next step) should expose this — when
DS2 fires the encoder, AllStatus #2 ought to update (snapshot of
#1), the sequence_id at `Frpg2PlayerImpl + 0x1A0` should
increment, and the wire bytes should briefly appear in heap.

## 2026-05-17 update (later) — saponita-time observation results

Live session with user actually placing a saponita sign. Snapshotted
Frpg2PlayerImpl pre- and post-placement. Result:

### AllStatus #1 vs AllStatus #2 are NOT the same thing

`AllStatus #2` is **not a snapshot of AllStatus #1**. It contains
a **different set of fields** with different content:

| Field | AllStatus #1 (current/live) | AllStatus #2 (advertised) |
|---|---|---|
| `_has_bits_` | `0x1F` (all 5 fields set) | `0x18` (only `position` + `unknown_5`) |
| `online_area_id` | 10310000 (real zone) | 0 |
| `cell_id` | 29359104 | 0 |
| `position*` | live Vector (X=3.84, Y=-18.52, Z=207.07) | different Vector (X=1.81, Y=-18.52, Z=208.15) |
| `unknown_5` | -3.06738 | 0.4781 |

The X/Z difference of ~2 units with identical Y (same ground level)
is consistent with the **saponita sign drop offset** — the sign
materializes where the player was standing when they used the
soapstone, while the player continues moving.

### Interpretation

- **AllStatus #1** = the engine's **internal authoritative snapshot
  of the local player** — full data, kept updated each tick.
- **AllStatus #2** = the **sign-placement advertisement message**.
  Minimal: only position and unknown_5. This is what travels to the
  matchmaking server when the player drops a white sign.

That contradicts the Ghidra-time assumption that #2 was a "previous
snapshot of #1." The two serve different network roles.

### Implications for phantom spawn

The PHANTOM SUMMON receive path consumes the **compact 0x39 format**
buffer (FUN_1401A1650's inner). Neither AllStatus #1 nor AllStatus
#2 is that format — both are full protoc MessageLite objects.

So for engine-cooperative phantom spawn we still need either:
- Path B: a captured inbound compact-format template (what v2.9.11's
  spawn-entry capture hook is built to do).
- Path D: find the AllStatus → compact converter (FUN_1401A29C0 reads
  a "ring-buffer triple"; that triple is built from the local
  player's live data somewhere upstream — likely the ChrIns/PlayerCtrl
  rather than the AllStatus).

AllStatus is server-side scaffolding for the local→server message
flow. The phantom spawn pipeline uses a different in-memory
representation that gets transmitted unchanged (compact 0x39).

**Revised strategy**:
- Path E (Frpg2PlayerImpl access) is useful for **monitoring**
  what the local player advertises and for understanding the
  outbound message flow, but it's NOT the bridge to phantom spawn.
- The phantom spawn pipeline still requires Path B (capture &
  replay) — v2.9.11's hook will capture a real compact-format
  template when one fires.

### What this DOES prove

1. **DS2's in-memory protoc layout matches the in-repo `.pb.h`** —
   subdivision pointers at predicted offsets, scalar fields decode
   correctly (online_area_id, position Vector with valid XYZ
   floats, has_bits mask).
2. **Frpg2PlayerImpl is a singleton** — exactly one instance in
   memory, locatable via vftable AOB scan.
3. **AllStatus #2 update is observable** — we can detect saponita
   sign drop / network advertisement events by polling AllStatus
   #2's position Vector. Could be a useful trigger for Bonfire to
   correlate against signage activity.

## 2026-05-17 update (later again) — Frpg2ClientImpl mapped, triple hunt parked

Walked back from Frpg2PlayerImpl to its parent.

### Frpg2ClientImpl ctor disassembled (RVA 0x68F890)

```
0x7FF76FB7F8AB: 48 8D 05 26 02 A8 00   lea rax,[0x7FF7705FFAD8]    ; vftable
0x7FF76FB7F8B2: 48 89 01               mov [rcx],rax
0x7FF76FB7F8B5: 48 83 C1 08            add rcx,08
0x7FF76FB7F8B9: E8 522B0000            call FUN_140692410           ; subsystem A (+0x08)
0x7FF76FB7F8BF: 48 8D 8F 80 00 00 00   lea rcx,[rdi+0x80]
0x7FF76FB7F8C6: 48 8D 57 08            lea rdx,[rdi+0x08]
0x7FF76FB7F8CA: E8 71650000            call FUN_140695E40           ; subsystem B (+0x80)
```

**Frpg2ClientImpl::vftable = `0x7FF7705FFAD8`**  (RVA `0x110FAD8`).

### Live instance

Computed live: Frpg2PlayerImpl @ ClientImpl+0x90 + we have Frpg2PlayerImpl @ `0x7FF4B456F610` →
**Frpg2ClientImpl @ `0x7FF4B456F580`**. Confirmed by reading +0x00 = vftable.

The instance is 0x788 bytes (allocated by FUN_14068D910). Key fields
verified live:

| Offset | Field | Value at live read |
|---|---|---|
| +0x000 | vftable | `0x7FF7705FFAD8` ✓ |
| +0x008 | secondary vftable | `0x7FF770600278` |
| +0x010 | subsystem A start (FUN_140692410) | heap ptr |
| +0x080 | subsystem B start (FUN_140695E40) | sub-vftable |
| +0x090 | Frpg2PlayerImpl::vftable | `0x7FF770601CB8` ✓ |
| +0x258 | subsystem C (FUN_14069CD30) | ... |
| +0x308 | subsystem F (FUN_1406A38A0) | ... |
| +0x358..+0x4B8 | 5x stream-handlers (FUN_1406A5880 with kinds 0,1,2,4,3) | ... |
| +0x510..+0x5C0 | 3x ... (FUN_1406A8520 with kinds 0,1,2) | ... |
| +0x618 | FUN_1406AA330 | ... |
| +0x6D8/+0x730 | 2x FUN_1406AC290 with kinds 0,1 | ... |

The 5 streams at +0x358..+0x4B8 are likely the per-channel
Frpg2 message queues (Player, Ghosts, BloodMessages, Bloodstains,
Signs) and align with the saponita-related code paths.

### Triple-ptr hunt result: PARKED

`FUN_1401A29C0` consumes a `longlong[3]` triple at RCX. The triple
lives at `*(N + 0x28)` for some sub-manager N reached from NetSvr
code paths. Two static callers were found:

- `FUN_14019F520` (line 346118): passes `*(M + 0x28)` as N where M
  is `*(*(caller + 0x18) + 0x20)` or `*(caller + 0x18)`.
- `FUN_1401A37D0` (line 349704): passes its own param_1 as N.
  Has ZERO direct callers in `decompiled.c` — invoked only via
  function pointer / vtable. So N is an abstract class with
  virtual encode methods.

Memory triple-pattern scan of Frpg2ClientImpl returned only
AllStatus sub-message clusters (not real ring-buffer triples).
The triple_ptr lives in a heap-allocated NetSvr sub-manager, not
directly inside Frpg2ClientImpl.

### Path forward

**Triple hunt requires live encoder hook** (capture RCX at
FUN_1401A29C0 entry on a real fire). Pure static tracing is
diminishing returns. The capture would need either:
- A DBVM watch (anti-cheat-safe) on the encoder, OR
- An indirect runtime breakpoint via a Detours hook on
  FUN_1401A29C0 entry (similar to the spawn-entry hook in v2.9.11).

For phantom spawn (the real goal), Path B remains the right
move — v2.9.11's spawn-entry capture hook will get the compact
buffer template directly when a host receives a summon, no need
to construct the triple ourselves.

### Operational utility unlocked NOW

What Path E gives Bonfire today, with zero further work:

1. **Live player coords**: read AllStatus #1 player_location → Vector → XYZ.
2. **Live zone/cell**: read AllStatus #1 player_location.online_area_id.
3. **Sign-drop detection**: poll AllStatus #2 position; X/Z change = saponita drop.
4. **Equipment changes**: AllStatus #1 equipment_info has repeated arrays of
   slot IDs at +0x30/+0x48/+0x60/+0x78 (consumables/left/right/rings).
5. **HP/level state**: AllStatus #1 physical_status, level_status sub-messages.

All of these are stable cross-launch via the vftable-anchored
location strategy (one AOB qword scan + walk).

Old Path C-easiest's premise (in-repo `AllStatus` source =
in-memory format DS2 reads) is **disproven**. The protobuf
source in `Source/Server.DarkSouls2/Protobuf/Generated/` is
**server-side** scaffolding from the original DS3OS lineage,
not what DS2.exe consumes. The DS2.exe format is more compact
and engine-specific (magic 0x39 header, packed entries).

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

### ✅ Phase 2B.2B (this update) — outer wrapper + inner format mapped

- 7 outer-struct fields + state machine documented above.
- Inner struct (`Ds2PlayerSnapshot`) FULLY mapped: magic 0x39 at
  +0x00, primary_count at +0x02, sequence at +0x04, fixed area
  +0x0C..+0x1EF (0x1E4 bytes), packed entries from +0x1F0.
- **Encoder identified**: `FUN_1401A29C0(triple_ptr, dst, size)`.
- **Size function identified**: `FUN_1401A2B00(triple_ptr)`.
- **Decoder/validator identified**: `FUN_1401A2F60(buf, size)`.
- Path C-easiest invalidated (in-repo protoc source ≠ DS2's
  in-memory format).

### 🚧 Phase 2B.2C-v1 — capture hook (next implementation step)

Implementation target for v2.9.11:

1. Add a read-only Detours hook on `FUN_1401A1650` entry, gated
   by `BONFIRE_DS2_SPAWN_CAPTURE=1` (in addition to existing
   `BONFIRE_DS2_RE_HOOKS=1`).
2. On each invocation, with SEH guard:
   - Capture outer wrapper (0x40 bytes from param_1).
   - Read `*(param_1 + 0x20)` to get inner pointer.
   - Validate magic `*(u16*)inner == 0x39`. If not, skip.
   - Use `FUN_1401A2B00` if reachable, else walk entries to
     compute total size. Cap at 64 KB.
   - Dump raw bytes to
     `Runtime/DS2Native/spawn-captures/<utc>_<seq>_outer.bin`
     and `_inner.bin`.
   - Write structured `_meta.json` with the parsed header
     fields (magic, counts, sequence, sizes) for quick diff.
3. Pass the original call through (`pTrampoline(param_1)`).
4. Auto-rotate: keep most-recent 5 captures, delete older.

This is purely observational. No spawn-triggering. Lowest
risk. Run by the user during one summon (vanilla or via
existing Bonfire server). Output validates everything we
RE'd statically.

### 🚧 Phase 2B.2C-v2 — Path D source-triple resolver

After capture proves the layout, find the local player's
source triple. We know the calling pattern (from line 346155):
`triple_ptr` lives at `*(some_manager + 0x28)`. Need to walk
back from `FUN_14019F520` callers to find which global gives
us `some_manager`.

### 🚧 Phase 2B.2C-v3 — clone-spawn test

With triple_ptr resolved + size+encode functions known:
1. Compute size = `FUN_1401A2B00(triple)`.
2. Allocate buf = `FUN_140833320(size)` (engine's allocator).
3. Build = `FUN_1401A29C0(triple, buf, size)`.
4. Build Ds2PhantomRequest wrapper with inner+0x08 = buf.
5. Drive `FUN_1401A1650(wrapper)`.
6. Read wrapper+0x10. If non-null → real PlayerCtrl spawned.

Expected outcome: a phantom that is a visual clone of the
local player. First confirmed engine-cooperative spawn.

### 🚧 Phase 2B.2D — SHM-driven Path D

After clone-spawn works:
1. Capture multiple snapshots from real summons (Path B's
   capture hook still active) to learn the field tag mapping
   (which entry encodes equipment, HP, level, etc.).
2. Build a synthesizer in the Injector that constructs a
   custom buf from SHM peer data, skipping the engine's
   triple-based encoder.
3. Per-peer phantom lifecycle (spawn on SHM entry add, despawn
   on entry expire).

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
