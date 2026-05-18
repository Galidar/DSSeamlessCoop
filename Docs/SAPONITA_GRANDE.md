# Saponita Blanca (White Sign Soapstone) — referencia técnica

Documento dedicado a la saponita **NORMAL/GRANDE** (White Sign Soapstone)
de DS2 SOTFS. Para la saponita pequeña ver `SAPONITA_PEQUENA.md`.
Para el catálogo general de limitaciones del sistema online ver
`SAPONITA_LIMITATIONS.md`.

Última actualización: 2026-05-18. Datos verificados contra wikis
oficiales (Fextralife DS2, Dark Souls Wiki Fandom, darksouls2.wiki.gg).

---

## Identidad

- **Nombre EN**: White Sign Soapstone
- **Nombre ES**: Saponita Blanca
- **Tipo**: Item online (multiplayer item)
- **Función**: Coloca una señal blanca cooperativa. Otros jugadores
  pueden invocarte para ayudarles en su mundo como phantom blanco.
- **Item ID hex (referencia CE community)**: `0x03B280B0`
  (cheat-engine table value — formato item slot, no item ID directo)
- **Re-usable**: Sí, infinitas veces

---

## Mecánica de duración (timer)

### Valores oficiales

| Estado | Tiempo |
|---|---|
| Base | **1 hora 6 minutos 40 segundos** (= 4000 segundos = ~66.67 min) |
| Con Name-engraved Ring (ambos jugadores con mismo dios) | **1 hora 40 minutos** (= 6000 segundos = +50%) |

### Cómo se reduce

- **Cada kill** (host o phantom) **reduce el timer**
- Enemigos más fuertes restan más tiempo
- Boss kill → **session ENDS INMEDIATAMENTE** con recompensa
- Si el host pasa por un fog wall sin boss vivo → todos los phantoms
  son enviados a casa sin recompensa

### Dónde vive en memoria (engine)

- **Timer activo**: probablemente en `phantom_mgr + 0xXXX` (offset
  pendiente de identificar — el +0x218 que ya tenemos mapped es el
  de la **pequeña** según verificación de valor 500.0)
- **Max constant 4000.0f**: pendiente — buscar en CE con float scan
  para 4000.0 o el 6000.0 (con Name-engraved Ring activo)
- **Decrement-per-kill**: hook somewhere en el enemy-death event
  que llama a un "subtract kill weight from timer" function

---

## Restricciones de área (donde NO funciona)

La saponita normal **NO funciona en áreas donde el boss ya fue
derrotado**. Es la limitación más fuerte del item.

Esta es **la diferencia clave** con la saponita pequeña: la pequeña
SÍ funciona en áreas con boss muerto.

Áreas no-coop por diseño (ambas saponitas):
- **Majula** (hub)
- **Ordeal's End** (Royal Rat Authority boss area en Doors of Pharros) —
  summoning normal está deshabilitado por design (es zona covenant)
- **Memorias** (Memory of...) — reglas especiales por DLC
- **DLC arena zones** (Old Iron King's prison etc.)

---

## Soul Memory matchmaking

La saponita normal tiene un **range de Soul Memory más estrecho**
que la pequeña — encontrar partner es más difícil si SM no matchea
estrictamente.

| Rango aprox. | Saponita Grande | Saponita Pequeña |
|---|---|---|
| Soul Memory match window | Más estricto | Más amplio |
| Para arriba | igual | igual |
| Para abajo | menos rango | **más rango hacia abajo** |

Documentación exhaustiva de tiers en `SAPONITA_LIMITATIONS.md`.

---

## Limitaciones del phantom invocado

Cuando alguien te invoca via su saponita normal (vos sos el phantom):

1. **Apariencia**: tu personaje se ve como **phantom blanco dorado**
   (shader translúcido aplicado a vista propia + del host). El host
   se ve a sí mismo como human normal.
2. **Estus Flask**: funciona pero **heal más lento** (no halved como
   en DS1, pero degradado).
3. **Items**:
   - No podés recoger items
   - No podés abrir cofres
   - No podés activar palancas / puertas / elevators
   - No podés interactuar con NPCs
4. **Bonfires**: no podés activarlos ni descansar
5. **Daño**: scaling reducido vs vanilla (~80% en algunos checks)
6. **Storage box**: no acceso a Item Box

Estas limitaciones son IDÉNTICAS a la saponita pequeña (es la misma
mecánica de phantom).

---

## Recompensas al completar

| Recompensa | Condición |
|---|---|
| **1 Token of Fidelity** | Default para todos los phantoms blancos |
| **1 Sunlight Medal** | Si el phantom es Heir of the Sun covenant |
| **25% de las souls** del boss matado por el host | Phantom gana esto cuando se completa boss |
| **Free body form** (revives a human) | Bonus al completar |
| **Item durability** restored | Bonus al completar |
| **Spell uses** restored | Bonus al completar |
| **Health** restored al máximo | Bonus al completar |

---

## Diferencias clave vs Saponita Pequeña

| Aspecto | Grande | Pequeña |
|---|---|---|
| Timer base | **4000s** (66m40s) | 500s (8m20s) |
| Timer con NER | 6000s | 750s |
| Funciona post-boss? | **NO** | SÍ |
| SM matching range | estrecho | más amplio (sobre todo hacia abajo) |
| Reward | Token of Fidelity + Sunlight Medal | Smooth & Silky Stone |
| Sesión termina en boss kill | SÍ inmediatamente | SÍ inmediatamente |
| Phantom darkens visualmente | NO | **SÍ** (cuando timer baja, se pone más oscuro a 2min y 1min restantes) |
| Estus si hay phantom rojo | OK | **bloqueado** |

---

## Cómo encontrar el timer de la saponita grande en memoria (TODO)

El timer de la pequeña ya está mapped (`phantom_mgr + 0x218 = 500.0`).
El de la grande debería ser un float separado en algún lado.

**Strategy para encontrarlo (sesión live futura):**

1. Host tiene phantom grande activo (no pequeña).
2. Wait ~30 segundos.
3. Read phantom_mgr full range, find any float that **decrements at
   ~1.0/sec** AND tiene un MAX al lado de **4000.0f** o **6000.0f**.
4. Verify con write: cambiar a otro valor → ver si se mantiene y
   sigue tickeando.

**Candidatos posibles offset (basado en proximity al +0x218 pequeña):**
- `phantom_mgr + 0xXXX` (cerca de +0x218 pero no exactamente)
- O en un sub-manager separado (puede haber `summon_mgr` distinto
  para "session co-op" vs "session shade")

**Cuando lo encontremos**, la mecánica de write será IDÉNTICA a la
pequeña: ninguna validación, freeze writing repetidamente, etc.

---

## Implementación en código (cuando se mapeé)

Una vez identifiquemos el offset del timer grande, añadir en
`Source/Injector/Hooks/DarkSouls2/DS2_NativeRuntimeHook.cpp`:

```cpp
// Saponita GRANDE (White Sign Soapstone) timer
constexpr uintptr_t kSaponitaGrandeTimerOffset = 0xXXX;  // TODO: find
constexpr float     kSaponitaGrandeTimerMax    = 4000.0f;
constexpr float     kSaponitaGrandeTimerMaxNER = 6000.0f;

float DS2_SaponitaGrandeTimer_Read() {
    auto pm = DS2_TryResolvePhantomMgr();
    if (!pm) return 0.0f;
    __try { return *(float*)(pm + kSaponitaGrandeTimerOffset); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0.0f; }
}

bool DS2_SaponitaGrandeTimer_Write(float v) {
    auto pm = DS2_TryResolvePhantomMgr();
    if (!pm) return false;
    __try { *(float*)(pm + kSaponitaGrandeTimerOffset) = v; return true; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
```

Command bus actions:
```
{"command":"saponita_grande.timer.freeze"}        -> write 4000 (or 6000 if NER)
{"command":"saponita_grande.timer.kill"}          -> write 0
{"command":"saponita_grande.timer.set","value":N} -> write N
{"command":"saponita_grande.timer.freeze_auto"}   -> background loop writing 4000 every 500ms
```

Para bypass de los OTROS limitations (apariencia phantom, items, estus,
etc.) ver `SAPONITA_GRANDE_BYPASSES.md` con tabla concreta de qué se
rompe y qué falta.

---

## Referencias

- [Fextralife: White Sign Soapstone](https://darksouls2.wiki.fextralife.com/White+Sign+Soapstone)
- [Fextralife: Online matchmaking](https://darksouls2.wiki.fextralife.com/Online)
- [Fextralife: Cooperative Gameplay](https://darksouls.fandom.com/wiki/Cooperative_Gameplay_(Dark_Souls_II))
- [Fextralife: Phantoms](https://darksouls2.wiki.fextralife.com/Phantoms)
- [Atvaark DS2 Cheat Engine Guide](https://gist.github.com/Atvaark/f308e1d8e00e07106452)
- Internal: `Docs/SAPONITA_LIMITATIONS.md`, `Docs/SAPONITA_PEQUENA.md`
