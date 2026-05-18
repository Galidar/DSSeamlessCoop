# Saponita Blanca Pequeña (Small White Sign Soapstone) — referencia técnica

Documento dedicado a la saponita **PEQUEÑA** (Small White Sign Soapstone)
de DS2 SOTFS. Para la saponita normal/grande ver `SAPONITA_GRANDE.md`.
Para el catálogo general de limitaciones del sistema online ver
`SAPONITA_LIMITATIONS.md`.

Última actualización: 2026-05-18. Datos verificados contra wikis
oficiales **Y verificados live en CE memory probe** durante sesión
con hermano (2026-05-17).

---

## Identidad

- **Nombre EN**: Small White Sign Soapstone
- **Nombre ES**: Saponita Blanca Pequeña
- **Tipo**: Item online (multiplayer item)
- **Función**: Coloca señal blanca cooperativa para sesiones cortas.
  El jugador invocado aparece como **"shade" (sombra blanca)** en
  vez del phantom blanco completo de la saponita grande.
- **Re-usable**: Sí, infinitas veces
- **Notable**: **funciona en áreas con boss ya derrotado** —
  diferencia clave con la grande

---

## Mecánica de duración (timer) — **VERIFIED LIVE**

### Valores oficiales (wiki)

| Estado | Tiempo |
|---|---|
| Base | **8 minutos 20 segundos** (= **500 segundos** exactos) |
| Con Name-engraved Ring (ambos jugadores con mismo dios) | **12 minutos 30 segundos** (= 750 segundos = +50%) |

### Cómo se reduce

- **Cada kill** (host o phantom) **reduce el timer** (idéntico
  comportamiento a la grande)
- Enemigos más fuertes restan más tiempo
- **Matar a un phantom rojo summon NO reduce el timer** (excepción)
- **Cracked red eye invaders SÍ causan reducción significativa**
- Boss kill → session ENDS INMEDIATAMENTE con recompensa

### Visual del phantom según timer

A medida que el timer baja, el phantom **se vuelve progresivamente
más oscuro**. Indicadores de tiempo crítico:
- **2 minutos restantes**: dimming notable
- **1 minuto restante**: muy oscuro

Este efecto NO existe en la saponita grande.

### Dónde vive en memoria (engine) — **MAPPED LIVE**

| Field | Address (live session) | RVA / offset |
|---|---|---|
| **Timer activo** | `phantom_mgr + 0x218` | float, countdown 1.0/sec |
| **Timer MAX constant** | `phantom_mgr + 0x230` | float = 500.0f |
| **Phantom count mirror** | `phantom_mgr + 0x010` | u32 |
| **Last summoned PlayerCtrl** | `phantom_mgr + 0x1E8` | qword |

Resolution de `phantom_mgr`:
```
ds2_base + 0x1616CF8           = phantom_mgr_holder_addr  (.data slot)
*(holder_addr)                  = holder_value           (= sub-manager pointer)
*(holder_value + 0x20)          = phantom_mgr            (= la struct con el timer)
```

### Comportamiento de write — **VERIFIED LIVE**

- Engine **NO valida** writes al timer:
  - Write `9999.0f` → acepta, queda en 9999
  - Write `-50.0f` → acepta, queda en -50
  - Write `0.0f` → decrementa a -0.02 luego clamps
  - Write `500.0f` → si timer activo, sigue decrementando
- Existe un **flag separado `should_decrement`** que se apaga cuando
  el timer llega a 0 en una sesión. Una vez apagado, escribir el
  timer NO reinicia el conteo. Solo se reinicia con NUEVA invocación.

### Live values observed (2026-05-17 session)

| Time | Value | Note |
|---|---|---|
| Hermano active in slot 1 | (no captured directly) | timer decrementing |
| Hermano just left | 149.5475 | Primera lectura |
| +5s | 144.2821 | Decreased by 5.27 (1.05/sec ≈ 1.0/sec) |
| Write 9999 → instant | 9999.00 | No clamp |
| Write -50 → instant | -50.00 | No clamp |
| Write 0 → +1 frame | -0.02 | One-frame overshoot, then frozen |
| Restored to 500 (post-zero) | 500.00 | Stays 500 forever (flag OFF) |

---

## Restricciones de área (donde NO funciona)

La saponita pequeña **SÍ funciona en áreas con boss derrotado** —
ESTO ES LA CARACTERÍSTICA ÚNICA que la distingue de la grande.

Áreas donde TAMPOCO funciona (compartido con grande):
- **Majula** (hub)
- **Ordeal's End** (Royal Rat Authority en Doors of Pharros)
- **Memorias** (Memory of...)
- Ciertas DLC arenas

---

## Soul Memory matchmaking

La saponita pequeña tiene un **range de Soul Memory más amplio**
que la grande — más fácil emparejar:

| Direction | Saponita Pequeña | Saponita Grande |
|---|---|---|
| Hacia arriba | igual range que grande | (referencia) |
| **Hacia abajo** | **más amplio (más permisivo)** | menos rango |

Por eso la pequeña está pensada para "co-op con personajes
inferiores en SM" (típicamente: tu amigo recién empieza, vos ya
estás avanzado).

---

## Limitaciones del phantom (shade) invocado

Cuando alguien te invoca via su saponita pequeña (vos sos shade):

1. **Apariencia**: tu personaje se ve como **shade — sombra blanca
   más translúcida que el phantom normal**, oscurecimiento progresivo
   con el timer.
2. **Estus Flask**:
   - Funciona pero **heal más lento**
   - **BLOQUEADO completamente si hay phantom rojo en el mundo**
     (diferencia con la grande)
3. **Items**:
   - No podés recoger items
   - No podés abrir cofres
   - No podés activar palancas / puertas / elevators
   - No podés interactuar con NPCs
4. **Bonfires**: no podés activarlos
5. **Daño**: scaling reducido (similar a grande)
6. **Storage box**: no acceso

---

## Recompensas al completar

| Recompensa | Condición |
|---|---|
| **1 Smooth & Silky Stone** | Default (= reward distinto al de la grande) |
| **1 Sunlight Medal** | Si el shade es Heir of the Sun covenant |
| **Health restored** | Al máximo |
| **Item durability** restored | Bonus al completar |
| **Spell uses** restored | Bonus al completar |
| **Free body form** (revives a human) | Bonus al completar |

**Diferencia clave**: la grande da **Token of Fidelity** (más valioso
para upgrade del White Sign), la pequeña da **Smooth & Silky Stone**
(item randomizable con los trade crows en Things Betwixt).

---

## Diferencias clave vs Saponita Grande

(Resumen — ver tabla completa en `SAPONITA_GRANDE.md`)

| Aspecto | Pequeña | Grande |
|---|---|---|
| Timer base | **500s** (8m20s) | 4000s (66m40s) |
| Timer con NER | 750s | 6000s |
| **Funciona post-boss?** | **SÍ** | NO |
| SM matching hacia abajo | más amplio | estrecho |
| Reward | Smooth & Silky Stone | Token of Fidelity + (Sunlight) |
| Phantom darkens con timer | **SÍ** (a 2min y 1min) | NO |
| Estus si hay phantom rojo | **bloqueado** | OK (heal slow) |

---

## Estado actual del bypass (sumario)

Para tabla detallada ver `SAPONITA_PEQUENA_BYPASSES.md`.

| Limitación | Status |
|---|---|
| Timer 500s | ✅ MAPPED + writable (`phantom_mgr+0x218`) |
| Decrement-per-kill | 🟡 mismo timer field, podemos resetear |
| Boss kill ends session | 🟡 bypasseable via queue-write |
| SM matching | ✅ bypasseado en Bonfire-coop (no usa server vanilla) |
| Phantom appearance (dim/dark) | 🟡 mismo flag is_phantom — NOP shader-selector |
| Estus blocked with red | 🟡 NOP red-phantom-presence check |
| No items/chests/levers | 🟡 NOP is_phantom interaction gates |
| Area restrictions (Majula etc) | ✅ Bonfire bypassea via queue-write |
| Phantom darkening visual | 🟡 NOP timer→shader-alpha mapping |

---

## Snippets de código listos (cuando se implemente v2.9.16)

### Resolver helper (compartido con grande)

```cpp
constexpr uintptr_t kPhantomMgrHolderRva = 0x1616CF8;
constexpr uintptr_t kPhantomMgrSubOffset = 0x20;
constexpr uintptr_t kSaponitaPequenaTimerOffset = 0x218;
constexpr uintptr_t kSaponitaPequenaTimerMaxOff = 0x230;
constexpr float     kSaponitaPequenaTimerDefault = 500.0f;

uintptr_t DS2_TryResolvePhantomMgr()
{
    auto ds2_base =
        (uintptr_t)GetModuleHandleW(L"DarkSoulsII.exe");
    if (ds2_base == 0) return 0;
    __try {
        auto holder = *(uintptr_t*)(ds2_base + kPhantomMgrHolderRva);
        if (holder == 0) return 0;
        return *(uintptr_t*)(holder + kPhantomMgrSubOffset);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}
```

### Timer manipulation

```cpp
float DS2_SaponitaPequenaTimer_Read() {
    auto pm = DS2_TryResolvePhantomMgr();
    if (!pm) return 0.0f;
    __try { return *(float*)(pm + kSaponitaPequenaTimerOffset); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0.0f; }
}

bool DS2_SaponitaPequenaTimer_Write(float v) {
    auto pm = DS2_TryResolvePhantomMgr();
    if (!pm) return false;
    __try {
        *(float*)(pm + kSaponitaPequenaTimerOffset) = v;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// Optional: modify MAX. Engine likely uses this to reset on new summon.
bool DS2_SaponitaPequenaTimer_WriteMax(float v) {
    auto pm = DS2_TryResolvePhantomMgr();
    if (!pm) return false;
    __try {
        *(float*)(pm + kSaponitaPequenaTimerMaxOff) = v;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
```

### Command bus actions

```cpp
if (command == "saponita_pequena.timer.freeze") {
    bool ok = DS2_SaponitaPequenaTimer_Write(500.0f);
    payload["timer_set_to"] = 500.0f;
    payload["ok"] = ok;
    AppendRuntimeEvent(config, "saponita_pequena.timer.freeze", payload);
    return;
}
if (command == "saponita_pequena.timer.kill") {
    bool ok = DS2_SaponitaPequenaTimer_Write(0.0f);
    payload["ok"] = ok;
    AppendRuntimeEvent(config, "saponita_pequena.timer.kill", payload);
    return;
}
if (command == "saponita_pequena.timer.set") {
    float v = 500.0f;
    if (data.contains("value") && data["value"].is_number())
        v = data["value"].get<float>();
    bool ok = DS2_SaponitaPequenaTimer_Write(v);
    payload["timer_set_to"] = v;
    payload["ok"] = ok;
    AppendRuntimeEvent(config, "saponita_pequena.timer.set", payload);
    return;
}
```

### Auto-freeze loop

```cpp
std::atomic<bool> s_saponita_pequena_freeze_auto{false};

// In worker thread loop (every ~500 ms):
if (s_saponita_pequena_freeze_auto.load()) {
    DS2_SaponitaPequenaTimer_Write(kSaponitaPequenaTimerDefault);
}

// Command:
if (command == "saponita_pequena.timer.freeze_auto") {
    bool enable = data.contains("enable") ?
        data["enable"].get<bool>() : true;
    s_saponita_pequena_freeze_auto.store(enable);
    payload["freeze_auto"] = enable;
    AppendRuntimeEvent(config, "saponita_pequena.timer.freeze_auto", payload);
    return;
}
```

---

## Referencias

- [Fextralife: Small White Sign Soapstone](https://darksouls2.wiki.fextralife.com/Small+White+Sign+Soapstone)
- [Darksouls2 Wikidot: Small White Sign Soapstone](http://darksouls2.wikidot.com/small-white-sign-soapstone)
- [Steam Community: Soapstone differences](https://steamcommunity.com/app/335300/discussions/0/133258092249739365/)
- [GameFAQs: Small vs regular Soapstone](https://gamefaqs.gamespot.com/boards/693331-dark-souls-ii/68784247)
- Internal: `Docs/SAPONITA_LIMITATIONS.md`, `Docs/SAPONITA_GRANDE.md`,
  `Docs/templates/saponita_timer_map.md`
- Live forensic dumps: `Docs/templates/live_slot1_*.bin`,
  `queue_entry_5/6/7.bin`
