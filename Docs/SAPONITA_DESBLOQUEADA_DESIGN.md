# Saponita Desbloqueada — Item custom = clon de saponita pequeña con TODAS las limitaciones desactivadas

Diseño arquitectural completo del **objetivo final**: crear un item
custom que sea efectivamente la **saponita pequeña con TODO
desbloqueado**.

Last updated: 2026-05-18.

> **Idea original del usuario**: "podrías duplicar la saponita
> pequeña convertirla exactamente en un item custom en donde
> desbloquearás exactamente todas las limitaciones del juego incluso
> las que descubriste comportamientos etc desbloqueando exactamente
> todo"

Esto es **superior** al approach Phase 4d original porque:
- ✅ No reinventamos el sistema de spawn — usamos el del engine que YA funciona
- ✅ No necesitamos sintetizar queue entries byte-a-byte
- ✅ El engine maneja sub-allocations, animaciones, hitboxes automáticamente
- ✅ Cada limitación se trata individualmente como hook independiente
- ✅ Modular: cada bypass puede activarse/desactivarse desde UI

---

## Arquitectura "Clone & Unlock"

### Concepto en 3 líneas

1. El usuario tiene un **item custom Bonfire** que internamente
   dispara la maquinaria de **saponita pequeña** vanilla.
2. Nuestros hooks interceptan **cada limitación** y la bypassan
   antes que se aplique al phantom resultante.
3. El engine, sin saber, spawnea un PlayerCtrl real con todas las
   capacidades de un host — pero con la apariencia visual de tu
   hermano.

### Flujo de uso (user-visible)

```
[Vos abrís inventario]
  ↓
[Click "Saponita Desbloqueada" (item 62061000 = Blessed Eye Orb redefinido)]
  ↓
[Engine + Bonfire hooks colaboran]
  ↓
[Tu hermano aparece INSTANTÁNEAMENTE en tu mundo]
  ↓ como human normal (no dorado/fantasmal)
  ↓ con sus armas, armadura, animaciones reales
  ↓ puede recoger items, abrir cofres, usar bonfires
  ↓ daño 100%, Estus full
  ↓ sin timer (sesión continúa indefinidamente)
  ↓ sin que muerte de boss/host/phantom termine la sesión
  ↓ en CUALQUIER zona (incluso Majula, post-boss)
```

### Flujo técnico (engine-level)

```
[InventoryUseItemHook fires - existing hook]
  ↓ item_id = 62061000
  ↓ HandleBonfireRuntimeItemUse(command="session.create")
  ↓
[NEW: SaponitaDesbloqueada_Trigger() — Phase 4d-clone]
  ↓ resolve phantom_mgr + slot_mgr
  ↓ build "synthetic saponita pequena event"
  ↓ inject into queue at phantom_mgr+0x5C0+N*0x640
  ↓ data = clone of slot_1 template OR Bonfire SHM peer data
  ↓ set entry+0x631 = 1 (ready flag)
  ↓
[engine per-tick FUN_14051DBB0 picks up next frame]
  ↓ engine doesn't know we wrote this — looks identical to network packet
  ↓ FUN_14051CE20 dispatches → FUN_1403572A0 → FUN_1403572E0
  ↓ engine ALLOCATES PlayerCtrl in slot 1-5
  ↓ engine calls FUN_14037EBE0 (PlayerCtrl ctor)
  ↓
[NEW: PostSpawnHook (Detours on FUN_14037EBE0 exit OR observer pattern)]
  ↓ identify the just-spawned PlayerCtrl
  ↓ apply ALL bypasses to that PlayerCtrl:
  ↓   - is_phantom flag = 0     (appearance + limitations)
  ↓   - timer = 99999.0          (no expiration)
  ↓   - skip server validation   (already happened — we wrote queue ourselves)
  ↓
[engine renders normally]
  ↓ PlayerCtrl tiene flags de "host" en vez de "phantom"
  ↓ engine no aplica shader dorado
  ↓ engine no aplica scaling penalty
  ↓ engine no bloquea acciones
  ↓
[VISIBLE]: tu hermano como cuerpo real, sin limitaciones, sin timer
```

---

## Plan de implementación — 7 hooks específicos

### Hook 1: Item-use trigger (ya existe, expandir)

**File**: `Source/Injector/Hooks/DarkSouls2/DS2_NativeRuntimeHook.cpp`
**Location**: `HandleBonfireRuntimeItemUse`, dentro de `command == "session.create"`

**Cambio**:

```cpp
else if (command == "session.create")
{
    s_session_open = true;
    s_session_mode = "host";
    s_last_session_request = "create";
    payload["action"] = "session_create_requested";

    // ⭐ NUEVO: trigger saponita-desbloqueada spawn
    int target_slot = -1;
    bool spawn_ok = SaponitaDesbloqueada_Trigger(&target_slot);
    payload["saponita_desbloqueada_spawn"] = spawn_ok;
    payload["target_slot"] = target_slot;
}
```

### Hook 2: SaponitaDesbloqueada_Trigger (NUEVO)

Lo que construye y mete la entrada en la queue del engine:

```cpp
bool SaponitaDesbloqueada_Trigger(int* out_slot)
{
    auto pm = DS2_TryResolvePhantomMgr();
    if (!pm) return false;
    
    __try {
        // 1. Find a free queue entry
        uintptr_t entry = 0;
        for (int i = 0; i < 8; ++i) {
            uintptr_t e = pm + 0x5C0 + i * 0x640;
            uint32_t type = *(uint32_t*)(e + 0x80);
            uint8_t ready = *(uint8_t*)(e + 0x631);
            if (type == 0xE || ready == 0) { entry = e; break; }
        }
        if (!entry) return false;
        
        // 2. Zero entry (avoid stale data tripping checks)
        ZeroMemory((void*)entry, 0x640);
        
        // 3. Populate structured fields from Bonfire SHM peer data
        auto peer = Ds2PeerShm::GetClosestActivePeer();  // existing API
        if (!peer.valid) return false;
        
        *(float*)(entry + 0x40) = peer.x;
        *(float*)(entry + 0x44) = peer.y;
        *(float*)(entry + 0x48) = peer.z;
        
        // Rotation (identity quaternion or peer's yaw)
        *(uint32_t*)(entry + 0x70) = 0;
        *(uint32_t*)(entry + 0x74) = 0;
        *(uint32_t*)(entry + 0x78) = 0;
        *(uint32_t*)(entry + 0x7C) = 0x3F800000;  // 1.0f w component
        
        // Type field — saponita-pequena equivalent. 0x12 = small white,
        // 0x13 = ghost. NOT 0xE (sentinel). 
        *(uint32_t*)(entry + 0x80) = 0x12;
        
        // Level (clamp 0..20)
        *(uint8_t*)(entry + 0x270) = (uint8_t)std::min(peer.level, 20);
        
        // Weapon levels (10 bytes from peer SHM)
        memcpy((void*)(entry + 0x272), peer.weapon_levels, 10);
        
        // Weapon stats (10 u16 = 20 bytes)
        memcpy((void*)(entry + 0x27C), peer.weapon_stats, 20);
        
        // Armor IDs (11 bytes)
        memcpy((void*)(entry + 0x290), peer.armor_ids, 11);
        
        // HP base
        *(int32_t*)(entry + 0x2D8) = peer.hp_max;
        
        // 4. Pre-bypass timer (so engine doesn't immediately expire)
        *(float*)(pm + 0x218) = 99999.0f;
        // Also crank up MAX so any future reset doesn't shrink it
        *(float*)(pm + 0x230) = 99999.0f;
        
        // 5. Set ready flag LAST (engine picks up next tick)
        *(uint8_t*)(entry + 0x631) = 1;
        
        // Register that we triggered a spawn — PostSpawnHook will
        // apply remaining bypasses when the PlayerCtrl appears
        s_saponita_desbloqueada_pending_spawn = true;
        s_saponita_desbloqueada_target_peer = peer.id;
        
        if (out_slot) *out_slot = -1;  // will be filled by PostSpawnHook
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
```

### Hook 3: PostSpawnHook (Detours on FUN_14037EBE0)

Cuando el engine acaba de construir el PlayerCtrl, aplicamos
**TODAS las bypasses** al instante:

```cpp
// El observer ya existe (kPlayerCtrlCtorRva = 0x37EBE0).
// Extender PlayerCtrlCtorHook para aplicar bypasses post-ctor.

void* __fastcall PlayerCtrlCtorHook(
    void* this_ptr, void* arg2, void* arg3, void* arg4)
{
    // ... existing logging ...
    
    PlayerCtrlCtorFn original = s_original_player_ctrl_ctor;
    void* ret = nullptr;
    if (original != nullptr) {
        ret = original(this_ptr, arg2, arg3, arg4);
    }
    
    // ⭐ NUEVO: si esta spawn fue triggered por nuestra saponita,
    // apply bypasses al PlayerCtrl recién construido.
    if (s_saponita_desbloqueada_pending_spawn.load()) {
        s_saponita_desbloqueada_pending_spawn.store(false);
        ApplySaponitaDesbloqueadaBypasses(this_ptr);
    }
    
    return ret;
}

void ApplySaponitaDesbloqueadaBypasses(void* player_ctrl_ptr)
{
    if (!player_ctrl_ptr) return;
    auto pc = (uint8_t*)player_ctrl_ptr;
    
    __try {
        // BYPASS 1: is_phantom flag → 0
        // Location TBD via RE — likely byte at PlayerCtrl + ~0x4X
        // or in intermediate sub-struct at +0x18 chain
        // For now we know intermediate exists at +0x18; the flag is
        // somewhere in its first 0x100 bytes
        auto intermediate = *(uintptr_t*)(pc + 0x18);
        if (intermediate) {
            // Hypothetical offset — verify live
            *(uint8_t*)(intermediate + 0x?? /* is_phantom */) = 0;
        }
        
        // BYPASS 2: Force "team type" = host (so engine treats peer
        // as same team — no PvP damage, no phantom limitations)
        // Per design doc: PlayerCtrl + 0x50 holds team type packed
        // Set to local-host equivalent
        // *(uint32_t*)(pc + 0x50) = 0x14010000;  // host-team
        
        // BYPASS 3: ChrIns flags (sub-pointers at +0xB0..+0x118)
        // These hold "can_use_bonfire", "can_pickup_items" etc.
        // Walk and patch each.
        // TBD via RE.
        
        // BYPASS 4: Set HP to max (no death-induced return)
        // PlayerCtrl + 0x168 = current HP, +0x174 = max HP
        // Make sure current = max
        int32_t hp_max = *(int32_t*)(pc + 0x174);
        *(int32_t*)(pc + 0x168) = hp_max;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        // Silent — partial bypass still better than crash
    }
}
```

### Hook 4: Timer keep-alive loop

En el worker thread, mantener el timer al máximo:

```cpp
// Existing worker loop ~line 4900
while (true) {
    command_offset = PollCommandInbox(*config, command_offset);
    
    // ⭐ NUEVO: si saponita desbloqueada está activa, mantener timer
    if (s_saponita_desbloqueada_active.load()) {
        DS2_SaponitaPequenaTimer_Write(99999.0f);
    }
    
    // ... rest of loop ...
}
```

### Hook 5: Boss-death cleanup bypass

Cuando el engine detecta "boss murió → enviar phantoms a casa",
NOP el cleanup trigger.

**Necesita RE**: encontrar el call site dentro de FUN_1401A1580
(case 3 = dead/cleanup in state machine). NOP la transición.

```cpp
// Future: Detours hook on FUN_1401A1580 or its caller
// On entry, check if the slot being cleaned up was spawned by us.
// If yes, return early without doing the cleanup.
```

### Hook 6: Fog wall suppression

**Necesita RE**: encontrar la función que spawnea fog walls al
detectar `phantom_count > 0`. NOP el call.

```cpp
// Future: AOB scan for the function that reads
// world_mgr+0x301 phantom_count and spawns fog walls.
// Likely uses a PlayAreaParam lookup. NOP the call site.
```

### Hook 7: Slot cap bypass

NOP el `cmp byte, 6` que limita a 6 slots máximo. Permite spawn
de N>6 phantoms (no necesario para single-brother pero útil para
parties grandes).

```cpp
// In Phase 2B.2E (already documented):
// AOB scan for the cmp instruction inside FUN_1401A0DC0 and
// FUN_1403572E0. Replace 'cmp byte, 6' with 'cmp byte, FF'.
```

---

## Tabla de implementación: hooks vs limitaciones

| Limitación de saponita pequeña | Hook que la rompe | LOC estimado | Esfuerzo |
|---|---|---|---|
| **Workflow saponita (signo + clickear + esperar)** | Hook 1 (item-use ya existe) | 5 | ✅ Ya hecho |
| **Server matchmaking SM/SL** | Hook 2 (queue-write directo) | 30 | 30min |
| **Restricciones de área (Majula, post-boss)** | Hook 2 (queue-write bypassa sign-listing) | 0 | Automático |
| **Soul Memory matching ranges** | Hook 2 (no usamos server vanilla) | 0 | Automático |
| **Name-engraved Ring same-god** | Hook 2 (irrelevante) | 0 | Automático |
| **Timer 500s decrement** | Hook 4 (keep-alive loop) | 5 | 5min |
| **Each kill reduces timer** | Hook 4 (escribimos timer cada N ms) | 0 | Cubierto por #4 |
| **Boss kill = session end** | Hook 5 (NOP cleanup) | 30 | 1-2h RE |
| **Phantom dies → home** | Hook 3 (HP=max + state machine override) | 15 | 1h |
| **Host dies → all phantoms home** | Hook 3 + 5 | 20 | 1h |
| **Phantom appearance (dim shade)** | Hook 3 (`is_phantom = 0`) | 5 | 30min después de mapear flag |
| **Estus heal slow / blocked** | Hook 3 (mismo `is_phantom` NOP) | 0 | Cubierto por #3 |
| **No items / chests / levers** | Hook 3 (mismo flag) | 0 | Cubierto |
| **No bonfires** | Hook 3 (mismo flag) | 0 | Cubierto |
| **Damage scaling ~80%** | Hook 3 (mismo flag) | 0 | Cubierto |
| **No NPC interaction** | Hook 3 (mismo flag) | 0 | Cubierto |
| **No Storage box** | Hook 3 (mismo flag) | 0 | Cubierto |
| **Phantom darkens visualmente** | Hook 4 (timer always 99999 → never darkens) | 0 | Cubierto por #4 |
| **Fog walls de área** | Hook 6 (NOP fog spawn) | 30 | 4-6h RE + 1h código |
| **Cap 4 phantoms** | Hook 7 (`cmp 6` NOP) | 20 | 1h |

**Total esfuerzo**: ~150 LOC del lado código, **5-9 horas** de
trabajo incluyendo RE para Hook 3 (mapear flag), Hook 5 (cleanup
trigger), Hook 6 (fog walls).

---

## Hooks que YA existen y reutilizamos

| Hook existing | Función | Uso para saponita desbloqueada |
|---|---|---|
| `InventoryUseItemHook` (kInventoryUseItem) | Detecta uso de items custom | Hook 1 (trigger) |
| `PlayerCtrlCtorHook` (kPlayerCtrlCtorRva) | Observa cada PlayerCtrl ctor | Hook 3 (post-spawn bypasses) |
| `SpawnEntryHook` (kSpawnEntryRva) | Captura saponita entries | Diagnóstico opcional |
| `PhantomSpawnerHook` (kPhantomSpawnerRva) | Captura FUN_1403572E0 | Diagnóstico opcional |
| `QueueDispatchHook` (kQueueDispatchRva) | Captura FUN_14051CE20 | Diagnóstico opcional |
| `DS2_RenderHook_SetPeerPoses` | Publica peer poses | Sigue funcionando (cube fallback) |
| `PeerIsCoveredByActivePhantomSlot` (v2.9.12) | Suprime cubo si engine cubre | ✅ Auto-supresión cuando spawn ok |
| `DS2_TryResolvePhantomMgr` (planned) | Resolver phantom_mgr | Hook 2 (queue write) |

---

## Lo que el usuario ve después de implementarlo

### Pre-uso:
- Inventario muestra "Saponita Desbloqueada" (rename Blessed Eye Orb)
- Tu hermano YA está en tu mundo como cubo (cube overlay v2.9.12)

### Acto de uso:
- Click "Saponita Desbloqueada"
- Mensaje en chat: "Bonfire: sesión iniciada"
- **Próximo frame (~16ms)**: cubo desaparece, **tu hermano aparece como human real** al lado tuyo
- Sus armas visibles, animaciones funcionan, podés pegarle (si friendly fire on) o pegarle juntos a enemigos
- Pueden recoger items separadamente
- Pueden abrir cofres separadamente
- Pueden activar bonfires separadamente
- Sin timer en pantalla, sin warning de "1 minuto restante"
- Si vos morís, no le mandás home — vos respawn en bonfire, él sigue ahí
- Si él muere, no es enviado home — respawn cerca tuyo
- Pueden matar bosses juntos — sesión continúa después
- Pueden moverse a otra área — no hay fog wall lock

### Para tu hermano (mismo Bonfire mod activo):
- Su perspectiva idéntica
- Te ve a vos como human real, no como phantom
- Mismas libertades

---

## Nombre del item custom

El item 62061000 actualmente se llama "Blessed Eye Orb" (con descripción
"create a Bonfire co-op session").

**Sugerencias para renaming en `kBonfireRuntimeItems`**:

| Option | RuntimeName | ActionLabel | Vibe |
|---|---|---|---|
| A | `bonfire_unlocked_soapstone` | "saponita desbloqueada — sesión sin límites" | Descriptivo |
| B | `bonfire_eternal_soapstone` | "saponita eterna" | Poético |
| C | `bonfire_freedom_soapstone` | "saponita libre" | Marketing |
| D | `bonfire_brother_soapstone` | "saponita de hermandad" | Tu caso específico |

**Recomendación**: A — claro técnicamente.

El item ID 62061000 NO cambia (mantener compat) — solo cambian
`RuntimeName` y `ActionLabel`.

---

## Versionado propuesto

| Versión | Scope | Esfuerzo | Status |
|---|---|---|---|
| **v2.9.16** | Hook 1 (existing) + Hook 2 (queue-write) + Hook 4 (timer keep-alive). Resultado: tu hermano spawnea como cuerpo real con phantom limitations vanilla activas. | 2-3h | Pending |
| **v2.9.17** | Hook 3 (`is_phantom` flag bypass) — RE para mapear flag + apply. Desbloquea apariencia + Estus + items + bonfires + damage en un golpe. | 3-4h | Pending |
| **v2.9.18** | Hook 5 (boss-cleanup) + HP-keep-alive — sesión no termina con muerte/boss. | 2h | Pending |
| **v2.9.19** | Hook 6 (fog walls) — movilidad libre entre áreas. | 5h | Pending |
| **v2.9.20** | Hook 7 (slot cap) + UI toggle bundle "Saponita Desbloqueada Modo Completo". | 1.5h | Pending |

**Total: ~15h** para tener TODO funcionando end-to-end con UI.

---

## Próximo paso concreto

**v2.9.16 (próxima sesión de código)**:

1. Bug fix: corregir RVA de phantom_mgr en v2.9.15 (`0x16616CF8`
   → `0x1616CF8`).
2. Implementar `DS2_TryResolvePhantomMgr()` helper.
3. Implementar `SaponitaDesbloqueada_Trigger()` con queue-write
   básico.
4. Implementar timer keep-alive en worker loop.
5. Renombrar item 62061000 a "saponita_desbloqueada".
6. Wire en `HandleBonfireRuntimeItemUse(command="session.create")`.
7. Build + stage + tag v2.9.16.

**Test esperado**: usar el item, ver a tu hermano como cuerpo real
(con shader fantasmal todavía activo — eso queda para v2.9.17).
**Si no aparece**: significa que falta algún field en la queue
entry — iterar.

---

## Riesgos y mitigaciones

### Riesgo 1: Queue entry incompleto → spawn rechazado

**Mitigación**: empezar copiando una entry capturada (los
`Docs/templates/queue_entry_*.bin` o, mejor, una que capturemos con
v2.9.14 cuando hermano se invoque). Patchear solo los campos delta
(position, identidad).

### Riesgo 2: PlayerCtrlCtorHook fires para muchos PlayerCtrls (no solo el nuestro)

**Mitigación**: usar el `s_saponita_desbloqueada_pending_spawn` flag
como gate. Después del primer ctor post-trigger, baja el flag.
Asume que el siguiente ctor es nuestro.

### Riesgo 3: Engine fields que no conocemos rechazan el spawn

**Mitigación**: empezar con campos mínimos viables, ver evento
`spawn_entry.observed` (v2.9.11 hook). Si el engine valida y
rechaza, los logs nos dirán por qué. Iterar.

### Riesgo 4: is_phantom flag location desconocida

**Mitigación**: live RE durante sesión activa. Comparar slot 0
(local human) vs slot 1 (phantom) byte por byte de la
`intermediate` struct. Diff revelará el flag.

### Riesgo 5: Engine cleanup nuestro PlayerCtrl al morir el host

**Mitigación**: HP keep-alive del host también (no solo del
phantom). Worker loop escribe `slot0_pc + 0x168 = +0x174` cada N
frames.

---

## Referencias

- `Docs/SAPONITA_PEQUENA.md` — referencia técnica
- `Docs/SAPONITA_PEQUENA_BYPASSES.md` — tabla bypass status
- `Docs/SAPONITA_VS_BONFIRE_ITEMS.md` — comparativa objetivos
- `Docs/CODE_ANALYSIS_SAPONITA_VS_ITEMS.md` — análisis profundo del gap
- `Docs/TRACK_C_PHASE_2B2_DESIGN.md` — engine spawn design
- `Docs/templates/saponita_timer_map.md` — live timer RE notes
- `Source/Injector/Hooks/DarkSouls2/DS2_NativeRuntimeHook.cpp` — items + commands
- `Source/Injector/Hooks/DarkSouls2/DS2_RenderHook.cpp` — cube + DrawOverlay
- `Source/Injector/Hooks/DarkSouls2/DS2_PoseShm.cpp` — peer pose IPC
