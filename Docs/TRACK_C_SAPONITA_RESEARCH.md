# Track C — How saponita generates a phantom (research dump)

External research conducted by the user via Claude.ai (web) about the
architecture of the Dark Souls summon/phantom rendering pipeline.
Lands in our project corpus so Track C work has the same starting
point even after context windows turn over.

The original prompt asked: **how does the saponita generate a
phantom? specifically the mesh — how can the other player's mesh be
seen?**

---

## Core architectural answer

**The mesh never travels over the network.** It would be infeasible:
each armor model is several MB, textures more, and shipping it all in
real time would melt any connection.

What actually happens:

1. **All assets are on both machines already.** When you installed
   Dark Souls, your copy includes *every* armor mesh, weapon mesh,
   haircut, body, every texture, every animation in the game. The
   other player's copy is content-identical. Nothing about the other
   player's character is exclusive to them.

2. **What gets transmitted is "character data"** — a small packet
   (a few KB) that is essentially a recipe for how to assemble the
   character. Inside there are:
   - Character editor parameters (face sliders, hair colour, skin
     tone, body proportions) packed into a compact binary struct.
   - Equipment IDs (`"armor ID 1234567, weapon ID 567890"` — not
     the armor itself, just the number that identifies it in the
     game's database).
   - Level, stats relevant to matchmaking, name, selected gesture.

3. **The receiving client reconstructs the phantom locally.** It
   receives those data and says "ok, render a character with face X,
   wearing armor ID 1234567, wielding weapon ID 567890". It looks
   those meshes up in *its own files*, loads them, mounts them on
   the character skeleton, applies the facial/body parameters, and
   tints with the phantom shader (translucent white, gold, red, ...)
   per summon type.

4. **During the active session**, only **state** is transmitted
   continuously:
   - Position XYZ + rotation
   - Which animation is playing and at what frame
     ("I am at frame 12 of the greatsword swing")
   - HP, stamina, active buffs
   - Discrete events (used Estus, applied resin)

   **The animations themselves are local** — what's synced is
   *which* animation, not the animation data.

This explains several common game phenomena:

- Modded weapons/armor with IDs that don't exist in the official
  game can't be seen by other players — they receive an ID that
  doesn't resolve in their installation, so the character appears
  naked or with garbage.
- The famous DS3 / Elden Ring RCE exploits worked because the
  client *parsed* received character data and had vulnerabilities in
  how it deserialised certain fields. An attacker shipped malformed
  character data that overwrote memory on the target.
- In DS2/DS3 there were simpler exploits where corrupt character
  data crashed whoever tried to render it.
- When matchmaking servers go down, pre-loaded ghost phantoms still
  show up because their data was already on disk.

The "transmit state, not geometry" approach is standard in
multiplayer everywhere. Counter-Strike, WoW, Fortnite, and pretty
much any online game work the same way. What differs in Souls is
that the connection is direct peer-to-peer instead of going through
an authoritative server.

---

## Confirming sources

### P2P model
The oldest Dark Souls 1 technical guide describes it explicitly:
the game connects to GFWL for auth, then the app starts adding
other players' IPs to a "P2P IP pool". Community tools (DSCfix,
DSCM, Wulf's `DaS-PC-MPChan`) work exactly on that P2P pool
mechanism, not on a central gameplay server.

### Character data + equipment IDs
Documented at the protocol level thanks to Tim Leonard's reverse-
engineering work on DS3. Players are synced via protobuf messages.
There's a `RequestUpdatePlayerCharacter` message containing a
`character_id` and a `character_data` blob — a serialised block
describing equipment/appearance/items, which the server persists
even when the player is offline. Separately there's `PlayerStatus`
with fields like `soul_level`, `covenant`, `hp`, `max_hp`,
`base_max_hp`, `embered`, `souls`, `soul_memory`, `archetype`,
`played_areas`, `world_type`, `can_summon_for_*` — exactly the
"continuous state" mentioned above.

### Numeric IDs
katalash's `dstools` repo confirms models live in `chr/` and `obj/`
folders, where each character is identified by a unique ID in the
format `cXXXX`. Weapons work the same way with decimal IDs — for
example the Club has base ID `8000000`, and upgrades add +1 up to
+99, while infusions are multiples of 100. So a network message
that says "weapon 8000527" translates to "Crystal Club +27" on the
receiver's installation.

### Character struct in memory
**Omni's reverse-engineering work found a struct called
`SprjChrDataModule` ("character data module")**, a structure that
appears in multiple FromSoft games including Sekiro and seems to
contain all the vital character data. This is the RAM
representation of what gets serialised across the network.

### RCE clarification
The famous `PaleTongue` (CVE-2022-24126) was actually delivered
via the matchmaking server — for the PoC the message used was
`PushRequestVisit` sent via `RequestSendMessageToPlayers`. This is
the most potent version of the exploit because the target client
parses the vulnerable data immediately on reception in any
situation, even at the main menu. Contrary to popular belief, this
is *NOT* a peer-to-peer networking exploit — it's tied to the
matchmaking server.

But — and this is key — beyond CVE-2022-24126 there were also P2P
vulnerabilities. The DS3 patch 1.15.1 fixed both CVE-2022-24125 and
CVE-2022-24126 plus a wide variety of potential security
vulnerabilities in the P2P networking (OOB reads/writes), and all
known exploits that could corrupt other players' saves. So the
broader idea — the client parses data sent by another machine and
that was a massive attack surface — is correct, but the famous RCE
specifically entered through the server channel, not the direct
peer-to-peer connection.

The exploitation mechanism, technically: two instructions redirect
execution to the memory address the attacker wrote at offset `0x40`
of the data buffer, achieving arbitrary code redirection. From
there the attacker builds a redirection chain that copies its
payload into a suitable region and executes it, using the buffer
as if it were a vtable. So they were treating attacker-controlled
data as if it were a legitimate C++ vtable, which allows jumping
to arbitrary code.

---

## Repos / reading worth chasing

- **`tremwil/ds3-nrssr-rce`** — PoC and full technical writeup of
  the DS3 RCE. Shows the exact memory layout of the character data
  buffer (offset 0x40 vtable pointer, etc.) — directly useful for
  understanding the binary format.
- **Tim Leonard blog (`timleonard.uk`)**, "Reverse Engineering
  Dark Souls 3 Networking" — 6 parts, full protocol documented
  with every protobuf message.
- **`katalash/dstools`** — file format library.
  `soulsmodding.wikidot.com` covers the same ground.
- **`Wulf2k/DaS-PC-MPChan`** — DS1 P2P connection viewer code.
- **LukeYui Seamless Co-op for Elden Ring** — the closest existing
  implementation to "drop the vanilla multiplayer system, replace
  with our own". Reference for the architectural approach Track C
  is trying to take.

---

## What this means for our Track C plan

This research validates and sharpens the original framing:

- **We don't need to capture or transmit raw mesh data.** Both
  PCs have the assets. We need to capture the **character data
  blob** + **runtime state** (animation id, frame, position,
  rotation, hp, stamina) and ship those over the existing
  Plan v3 Track B pose pipe.
- **Locate `SprjChrDataModule`** in DS2 SOTFS memory. Each ChrIns
  carries one. The struct contains equipment IDs, character
  editor params, animation state. We already walk
  `gm + 0x18 + 0x50 + 0x90` to the local player's position; the
  data module is at a sibling offset on the same ChrIns.
- **Two render paths** to evaluate:
  1. **Engine-cooperative** (the ideal): find the internal DS2
     function that spawns a phantom from incoming character data,
     hook it, and feed our peer's data through it. The engine
     does the asset lookup, skinning, animation playback for us.
     Then override the phantom-limit checks (4-cap, fog-gate
     despawn, Soul Memory matching, etc.) so the rendered
     character has no vanilla constraints — it's our overlay's
     puppet, just using the game's renderer.
  2. **Standalone overlay** (fallback): keep our D3D11 overlay
     pipeline. Extend the cube primitive to draw a simple
     skinned silhouette using the game's loaded mesh buffers (we
     can `Map`/copy the vertex buffer for the relevant
     `cXXXX.flv` after the game loads it on its own ChrIns).
     Skip animations or play a fixed idle pose. Coarser visual
     but doesn't need any function hooks.

Path 1 is what the user wants ("steal the mesh data the
saponita causes to load"). Path 2 is the safer interim if
Path 1 needs more RE time than expected.

---

## Concrete next session

1. **CE session** while two PCs are connected in vanilla DS2
   co-op:
   - User-side: locate the local player's ChrIns via the known
     `gm + 0x18 + 0x50` chain.
   - At `+0x90` we have the position. Scan sibling offsets for a
     pointer to a struct that contains:
     - 4-byte equipment IDs at known offsets (cross-reference
       Paramdex `EquipParamWeapon.csv` to confirm).
     - Character editor blob (~80-200 bytes).
     - Animation state (current animation id u32, current frame
       float).
   - On the brother's side (with him as the saponita-summoned
     phantom), find the **phantom** ChrIns and verify the same
     struct layout. Both peers' character data should look
     symmetrical.

2. **Confirm `SprjChrDataModule`** via RTTI lookup (CE skill
   `get_rtti_classname` on the candidate pointer). If the class
   name shows `SprjChrDataModule` we've matched Omni's struct.

3. **Static analysis** — open DarkSoulsII.exe in Ghidra
   (or use the AOB-based search from our skill), find references
   to the `SprjChrDataModule` vtable, locate the constructor /
   "load from net data" path. That's the engine-cooperative
   render-path entry point.

4. **Write `Ds2CharDataReader`** (BonfireService) that mirrors
   the local player's struct over our existing UDP pipe. Add
   a `character_data` field to the wire format. The brother's
   BonfireService publishes the received character_data into a
   new shared section `Local\BonfireDS2CharDataV1`.

5. **Injector consumes** that section on its render thread.
   Phase 1 — log the equipment IDs to confirm round-trip. Phase
   2 — call the engine-cooperative spawn function with the peer
   data and draw the result at the overlay anchor.

Tag target: **v3.0.0-experimental** when the cube becomes a
recognisable peer character.
