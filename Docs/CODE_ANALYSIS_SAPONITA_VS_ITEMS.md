# Análisis profundo de código — Saponita vs Items Custom

Tras leer **el código real** del Injector + Bonfire + decompiled DS2,
esta es la diferencia exacta entre el path saponita (que renderiza
personaje REAL) y el path items custom Bonfire (que renderiza solo
CUBO VISUAL).

Last updated: 2026-05-18. Verificado contra:
- `Source/Injector/Hooks/DarkSouls2/DS2_NativeRuntimeHook.cpp`
- `Source/Injector/Hooks/DarkSouls2/DS2_RenderHook.cpp`
- `Source/Injector/Hooks/DarkSouls2/DS2_PoseShm.cpp`
- `Source/BonfireService/Modules/Ds2NativePoseBridge.cs`
- `Docs/ghidra-out/DarkSoulsII-out/decompiled.c`

---

## Verdad operacional: el cubo es PURAMENTE VISUAL

Tu observación es **exacta**: hoy el cubo aparece donde está tu
hermano pero el engine **NO SABE QUE TU HERMANO EXISTE**. Solo es
una geometría renderizada en pantalla. No hay PlayerCtrl en el
slot pool, no hay hitbox, no hay animación de mesh, no hay
colisión. Es decoración.

Saponita vanilla, en cambio, **inserta un PlayerCtrl REAL en el
slot pool** del engine. El engine lo trata exactamente como
cualquier otro personaje: anima, colisiona, recibe daño, etc.

---

## Path saponita vanilla (real engine spawn)

### Pipeline completo

```
[server vanilla]
   ↓ envía paquete compact-0x39 (~500 bytes character data)
   ↓
[DS2 network receiver]
   ↓ decodifica paquete → struct record
   ↓
[engine writes to queue]
   ↓ phantom_mgr + 0x5C0 + N*0x640 entry populated
   ↓ entry+0x631 ← 1 (ready flag)
   ↓
[FUN_14051C940 per-frame tick]
   ↓ calls FUN_14051DBB0(phantom_mgr)
   ↓
[FUN_14051DBB0 queue dispatcher]
   ↓ walks queue, finds ready entry
   ↓ calls FUN_14051CE20(phantom_mgr, entry)
   ↓
[FUN_14051CE20 per-entry processor]
   ↓ reads structured fields from entry +0x40..+0x2D8
   ↓ builds local stack struct (~0x60 bytes)
   ↓ calls FUN_1403572A0(world_mgr, &local_struct)
   ↓
[FUN_1403572A0 wrapper]
   ↓ calls FUN_1403572E0(world_mgr, &out, &local_struct)
   ↓
[FUN_1403572E0 PHANTOM SPAWNER (the real allocator)]
   ↓ FUN_140833320(0x4A0, 0x10)  → allocate 0x4A0 bytes for PlayerCtrl
   ↓ FUN_14037EBE0(pc, mgr_rec, world, idx)  → PlayerCtrl ctor
   ↓ slot record at slot_mgr + 0x5D0 + N*0xA90 gets activated
   ↓ slot record +0xC8 = new_PlayerCtrl
   ↓ slot record +0x0E0 = 0xEB (active flag)
   ↓
[engine each frame]
   ↓ DS2 renderer iterates all active slots
   ↓ for each PlayerCtrl: render mesh + apply animation + hitbox + collision
   ↓
[VISIBLE]: real character body on screen, can fight, can be attacked
```

**Key code reference (Ghidra)**: `decompiled.c` lines 1146960+
(FUN_14051CE20), 348002+ (FUN_1401A1650), 748797+ (FUN_1403572E0)

---

## Path items custom Bonfire (VISUAL only)

### Pipeline completo

```
[user clicks Blessed Eye Orb / Crystal Eye Orb]
   ↓ DS2 calls Inventory.UseItem(item_id = 62061000)
   ↓
[InventoryUseItemHook fires]
   ↓ DS2_NativeRuntimeHook.cpp line 2370
   ↓ FindBonfireRuntimeItem(62061000) → matches Blessed Eye Orb
   ↓ LogInventoryUseItem(...)
   ↓ HandleBonfireRuntimeItemUse(...)
   ↓
[HandleBonfireRuntimeItemUse line 1581]
   ↓ Determine command from item: "session.create" / "session.join" / etc.
   ↓ Set state flags:
   ↓   s_session_open = true
   ↓   s_session_mode = "host" | "guest"
   ↓ Log to events.jsonl
   ↓ Suppress vanilla continuation (= don't actually use the eye orb)
   ↓
[BonfireService picks up the state via heartbeat / events.jsonl]
   ↓ Sends "render.set_peer_poses" command to Injector worker
   ↓
[Injector worker thread @ ~4516]
   ↓ Processes command "render.set_peer_poses"
   ↓ Calls DS2_RenderHook_SetPeerPoses(poses, count)
   ↓
[DS2_RenderHook_SetPeerPoses line 1843]
   ↓ Writes to s_peer_table[16]  (POD struct, 32 bytes each)
   ↓   position[3], yaw_radians, color[3], valid
   ↓
[DrawOverlay every frame, line 1101+]
   ↓ For each valid s_peer_table[i]:
   ↓   PeerIsCoveredByActivePhantomSlot(pos)?  → if yes, skip (v2.9.12)
   ↓   else: append to draws[]
   ↓ For each draws[i]:
   ↓   Map cbuffer with cube transform + color
   ↓   s_d3d_context->Draw(72, 0)   // 72 verts = torso cube + head cube
   ↓
[VISIBLE]: humanoid cube of colors at peer position
```

**Key code reference**:
- `DS2_NativeRuntimeHook.cpp` line 340 (kBonfireRuntimeItems table)
- `DS2_NativeRuntimeHook.cpp` line 1581 (HandleBonfireRuntimeItemUse)
- `DS2_NativeRuntimeHook.cpp` line 4516+ (worker command bus)
- `DS2_RenderHook.cpp` line 951 (DrawOverlay)
- `DS2_RenderHook.cpp` line 1843 (SetPeerPoses)

### Lo que NUNCA pasa con items custom hoy

❌ No se llama a `FUN_1401A1650` (saponita-entry)
❌ No se escribe a `phantom_mgr + 0x5C0` queue
❌ No se llama a `FUN_1403572E0` (phantom spawner)
❌ No se llama a `FUN_14037EBE0` (PlayerCtrl ctor)
❌ No se asigna PlayerCtrl en slots 1-5 del slot pool
❌ No se activa slot record markers (+0x0A0/+0x0C0/+0x0E0)
❌ No hay hitbox, collision, animation skeleton para el peer

**Consecuencia**: el engine vive en su mundo "solo" — vos sos el
único PlayerCtrl real en slot 0. Los cubos son sprites/quads
dibujados sobre el framebuffer al final, AJENOS al engine. Como
un overlay HUD.

---

## Comparación lado a lado

### Estado del engine

| Aspecto | Saponita | Items Custom HOY |
|---|---|---|
| `slot_mgr + 0x5D0 + 1*0xA90 + 0xC8` (slot 1 PC*) | live PlayerCtrl | nullptr/dangling |
| `slot_mgr + 0x5D0 + 1*0xA90 + 0x0E0` (active flag) | `0xEB` | `0x0` |
| `world_mgr + 0x301` (phantom count) | 1+ | 0 |
| `phantom_mgr + 0x010` (phantom count mirror) | 1+ | 0 |
| `phantom_mgr + 0x1E8` (last summoned) | live PlayerCtrl | 0 |
| `phantom_mgr + 0x218` (timer) | decrementing 1.0/sec | 0 (never started) |
| Active rendered meshes | host + phantom | host only |
| Active animation contexts | host + phantom | host only |
| Active physics bodies | host + phantom | host only |
| Active hitboxes | host + phantom | host only |

### Visualización

| Aspecto | Saponita | Items Custom HOY |
|---|---|---|
| Mesh | real character (FLVER) | quad geometry (cube) |
| Vertices | thousands per character | 72 (36 torso + 36 head) |
| Texture | character textures + armor | flat color |
| Animation | full skeleton, ~200 bones | static, position-only |
| Render pass | character pass (deferred lighting) | overlay pass (post-everything) |
| Shader | character shader (PBR + skin) | minimal vertex+pixel shader |
| Depth test | normal | partially disabled (overlay) |

### Hitboxes / colisión

| Aspecto | Saponita | Items Custom HOY |
|---|---|---|
| Player attacks hit peer? | sí | no — cube no tiene hitbox |
| Peer attacks hit you? | sí (potential FF/PvP) | imposible — cube no tiene weapon |
| Collision detection | sí (engine physics) | no (cube atraviesa walls) |
| Push apart on overlap | sí | no |
| Targeting (lock-on) | sí | no |

### Animaciones / sincronización

| Aspecto | Saponita | Items Custom HOY |
|---|---|---|
| Walking animation | engine plays based on velocity | cube static |
| Attack animation | engine plays from weapon hitbox events | cube static |
| Gesture animations | engine plays from peer-sent gesture id | cube static |
| Equipment changes | engine swaps mesh dynamically | cube static color |
| Health → death animation | engine plays ragdoll | cube static |

---

## El gap específico: queue-write

### Lo que FALTA implementar

En `HandleBonfireRuntimeItemUse` cuando `command == "session.create"`
o `"session.join"`, AGREGAR un step adicional:

```cpp
// PSEUDO-CODE — Phase 4d que nos falta
else if (command == "session.create")
{
    s_session_open = true;
    s_session_mode = "host";
    payload["action"] = "session_create_requested";
    
    // ⭐ NUEVO: spawn engine-cooperativo del peer SHM más cercano
    auto peer = Ds2PeerShm::TryGetClosestPeer();
    if (peer.valid) {
        int target_slot = -1;
        bool ok = DS2_TryEngineSpawnFromQueue(peer, &target_slot);
        payload["engine_spawn_attempted"] = ok;
        payload["target_slot"] = target_slot;
    }
}
```

`DS2_TryEngineSpawnFromQueue` es la función que hace lo que vanilla
saponita hace internamente:

```cpp
bool DS2_TryEngineSpawnFromQueue(const PeerData& peer, int* out_slot)
{
    auto pm = DS2_TryResolvePhantomMgr();
    if (!pm) return false;
    
    __try {
        // Find free queue entry
        uintptr_t entry = 0;
        for (int i = 0; i < 8; ++i) {
            uintptr_t e = pm + 0x5C0 + i * 0x640;
            uint32_t type = *(uint32_t*)(e + 0x80);
            uint8_t ready = *(uint8_t*)(e + 0x631);
            if (type == 0xE || ready == 0) { entry = e; break; }
        }
        if (!entry) return false;
        
        // Zero entry first
        ZeroMemory((void*)entry, 0x640);
        
        // Populate structured fields with peer data
        *(float*)(entry + 0x40) = peer.x;
        *(float*)(entry + 0x44) = peer.y;
        *(float*)(entry + 0x48) = peer.z;
        *(uint32_t*)(entry + 0x80) = 0x12;  // phantom type
        *(uint8_t*)(entry + 0x270) = peer.level;
        // ... weapon, armor, etc. from peer SHM
        
        // Reset timer (so engine ticks fresh 500s)
        *(float*)(pm + 0x218) = 500.0f;
        
        // Set ready flag LAST (engine picks it up next tick)
        *(uint8_t*)(entry + 0x631) = 1;
        
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
```

Engine's FUN_14051DBB0 (per-tick) picks up the entry, dispatches it
via the same chain saponita uses, **PlayerCtrl gets allocated in
slot 1-5, real character renders**.

---

## Inventario actual de items custom vs su mapping ideal

| Item ID | Item Name (Bonfire) | Command actual | Acción HOY | **Acción IDEAL Phase 4d** |
|---|---|---|---|---|
| 62061000 | Blessed Eye Orb | `session.create` | flag `host_bootstrap`, peer table populated → cubo aparece | **`host_bootstrap` + queue-write para cada peer SHM → PlayerCtrls reales aparecen** |
| 62061001 | Crystal Eye Orb | `session.join` | flag `guest_handshake`, registers as guest | `guest_handshake` + announce-to-host via SHM. Engine spawn ocurre en lado del host con el queue-write. |
| 62061002 | Chaos Eye Orb | `session.invade` | invasor flag | queue-write con phantom_type = "red" (0x13) |
| 62061003 | Abyssal Eye Orb | `session.leave` | leave flag | + clear queue entry + force return en lado peer |
| 62061004-7 | Otros | misc actions | misc flags | no requieren engine-spawn |

**Items críticos para tu visión**: 62061000 (Blessed) + 62061001 (Crystal).
Los otros son features secundarios.

---

## Files a tocar para cerrar el gap

### Nuevos archivos

Ninguno necesario. Todo el código va en archivos existentes.

### Archivos a modificar

**`Source/Injector/Hooks/DarkSouls2/DS2_NativeRuntimeHook.cpp`**:

1. **Top del archivo (~línea 80)**: agregar constantes:
   ```cpp
   constexpr uintptr_t kPhantomMgrHolderRva = 0x1616CF8;
   constexpr uintptr_t kPhantomMgrSubOffset = 0x20;
   constexpr uintptr_t kQueueBaseOffset = 0x5C0;
   constexpr size_t kQueueEntryStride = 0x640;
   constexpr uintptr_t kQueueEntryTypeField = 0x80;
   constexpr uintptr_t kQueueEntryReadyFlag = 0x631;
   constexpr uintptr_t kQueueEntryPositionOff = 0x40;
   constexpr uintptr_t kSaponitaTimerOffset = 0x218;
   ```

2. **Section helpers (~línea 500-700)**: agregar:
   - `DS2_TryResolvePhantomMgr()` 
   - `DS2_TryEngineSpawnFromQueue(PeerData)`
   - `DS2_SaponitaTimer_Read/Write()` + saponita timer commands

3. **HandleBonfireRuntimeItemUse (~línea 1581)**: en los casos
   `session.create` y `session.join`, agregar el call a
   `DS2_TryEngineSpawnFromQueue` con los peer poses actuales.

4. **Command bus dispatcher (~línea 4993-5008)**: agregar comandos
   manuales:
   - `phantom.engine_spawn` — dispara queue-write manualmente
   - `saponita_pequena.timer.freeze` etc.

### Total esfuerzo estimado

- Saponita timer commands: ~50 LOC, 1h
- `DS2_TryResolvePhantomMgr`: ~20 LOC, 30min
- `DS2_TryEngineSpawnFromQueue` (con todos los structured fields):
  ~150 LOC, 3-4h (incluye RE de los campos que aún no mapeé como
  el +0x80 type valid values, +0x272 weapon levels semantics)
- Wire to item actions: ~30 LOC, 30min
- Build + test + iterate (1-2 ciclos): 2h

**Total**: ~6-8h para v2.9.16 + v2.9.17 con todo funcionando.

---

## Diagnóstico crítico para tu hermano

Si tu hermano dice "yo te veo como cubo" eso confirma:

1. **Su Bonfire está corriendo** ✅
2. **El SHM peer table está sync** ✅
3. **DS2_RenderHook está hookeado y Draws** ✅
4. **El gap es solo Phase 4d** ⚠

Si ambos vieran "no aparezco para nada" significaría que el SHM
peer table falla. Pero como ven el cubo, el SHM funciona perfecto.
El problema es UNICAMENTE el render — cubo vs personaje real.

Y eso es exactamente lo que cierra Phase 4d: reemplaza el cubo
overlay con un PlayerCtrl engine-rendered.

---

## TL;DR

**Hoy**:
```
Item custom → state flag → BonfireService → peer table → 
DS2_RenderHook DrawOverlay → CUBO geometry shader
```

**Mañana (Phase 4d)**:
```
Item custom → state flag → DS2_TryEngineSpawnFromQueue →
phantom_mgr + 0x5C0 queue entry written → 
FUN_14051DBB0 picks up → FUN_14051CE20 → FUN_1403572A0 →
FUN_1403572E0 → FUN_14037EBE0 → real PlayerCtrl in slot →
DS2 character renderer draws PERSONAJE REAL con armor+anims+hitbox
```

**El cubo deja de dibujarse automáticamente** porque v2.9.12
cube-suppression detecta el slot active a < 1.6m del peer pose y
skip emit. Win-win: real body aparece, cubo desaparece.

**Vos lo describiste perfecto**: "ese cubo que aparece es totalmente
visual actualmente". Confirmado en código — es solo overlay sin
nada en el engine debajo.

---

## Referencias

- `Source/Injector/Hooks/DarkSouls2/DS2_NativeRuntimeHook.cpp` — items + command bus
- `Source/Injector/Hooks/DarkSouls2/DS2_RenderHook.cpp` — cube overlay + DrawOverlay
- `Source/Injector/Hooks/DarkSouls2/DS2_PoseShm.cpp` — peer pose IPC
- `Source/BonfireService/Modules/Ds2NativePoseBridge.cs` — pose publisher
- `Docs/SAPONITA_GRANDE.md`, `Docs/SAPONITA_PEQUENA.md` — saponita tech refs
- `Docs/SAPONITA_VS_BONFIRE_ITEMS.md` — comparativa de objetivos
- `Docs/TRACK_C_PHASE_2B2_DESIGN.md` — engine-spawn design
- `Docs/templates/saponita_timer_map.md` — live RE notes
