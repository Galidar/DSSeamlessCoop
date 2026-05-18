# Saponita vs Bonfire Custom Items — Objetivo final y gap analysis

Documento que clarifica el **objetivo final** de los items custom de
Bonfire: ser equivalentes a saponitas **completamente desbloqueadas**
con flujo de uso más simple y sin las molestias del sistema de signos.

Last updated: 2026-05-18. Este doc es la **fuente de verdad** sobre
qué hace cada sistema hoy y qué falta para cerrar el gap.

---

## Visión en una frase

> Los items custom de Bonfire deberían comportarse como una saponita
> que: invoca instantáneamente (sin colocar signo), funciona en
> CUALQUIER zona (incluso Majula), muestra al otro jugador como su
> personaje REAL (no como cubo de debug, no como phantom dorado), y
> elimina TODAS las limitaciones de phantom (Estus full, items, cofres,
> bonfires, combate, daño 100%, sin timer, sin enviado-a-casa).

---

## Tabla comparativa exhaustiva

### Workflow / flujo de uso

| Aspecto | **Saponita Vanilla** (grande o pequeña) | **Items Custom Bonfire** (Blessed Eye Orb + Crystal Eye Orb) |
|---|---|---|
| Iniciar sesión | Colocar signo en suelo | Click en item → sesión activa inmediato |
| Espera | Otro jugador debe encontrar el signo | Cero espera — invitación directa |
| Conexión | Clickear signo → loading → spawn | Otro usa joiner item → loading → spawn |
| Cancelar | Recoger sign o moverse | Click leave item (Abyssal Eye Orb) |
| **Frustración** | ⚠ ALTA: signo perdido, signs no visibles, SM mismatch, etc. | ✅ NINGUNA: directo, sin friction |

### Zonas donde funciona

| Zona | Saponita | Items Custom |
|---|---|---|
| Majula (hub) | ❌ NO | ✅ SÍ |
| Things Betwixt | ❌ NO (?) | ✅ SÍ |
| Forest of Fallen Giants | ✅ pre-boss / ❌ post-boss (grande) | ✅ siempre |
| Dragon Aerie / Shrine | ⚠ parcial | ✅ siempre |
| **Memorias** (Memory of...) | ⚠ reglas especiales | ✅ siempre |
| **Ordeal's End** (Royal Rat Authority) | ❌ NO (covenant restriction) | ✅ siempre |
| **Drangleic Castle** pre-Vendrick | ❌ restricted | ✅ siempre |
| DLC arenas | ❌ NO | ✅ siempre |
| Cualquier zona post-boss | ❌ NO (grande) / ✅ SÍ (pequeña) | ✅ siempre |

**Items custom YA ganan claramente** en restricciones de zona — esto
es uno de los wins más grandes de Bonfire-coop. Ya funcional hoy.

### Matchmaking / discoverability

| Aspecto | Saponita Vanilla | Items Custom |
|---|---|---|
| Soul Memory range | Estricto | ✅ N/A — no SM matching |
| Soul Level range | Estricto | ✅ N/A |
| Name-engraved Ring | Requerido para co-op privado | ✅ N/A — Bonfire-coop es siempre privado por session_id |
| Random matchmaking | Sí (puede aparecer cualquiera) | ✅ NO — solo tu hermano (o quien tenga tu session id) |
| Visibilidad del signo | Solo para players en tier SM | ✅ N/A — no usa signs |
| Tiempo para encontrar match | Minutos / impredecible | ✅ Inmediato |

**Items custom YA ganan completo**. Esto es lo que hace Bonfire-coop
fundamentalmente superior.

### Renderizado del otro jugador (LO QUE TE FALTA)

| Aspecto | Saponita Vanilla | Items Custom HOY | **Items Custom OBJETIVO** |
|---|---|---|---|
| Apariencia del otro | Phantom dorado/shade translúcido | **❌ Cubo humanoide de colores (HKMP debug overlay)** | ✅ Personaje real con su mesh, equipamiento, animaciones |
| Tu propia apariencia (cuando sos phantom) | Dorada/translúcida | N/A (no estás "phantom") | ✅ Human normal |
| Equipo visible | Real (armas, armadura) | ❌ Cubo no muestra equipo | ✅ Real (Armas, armadura, etc.) |
| Animaciones (caminar, atacar, gestos) | Reales sincronizadas | ❌ Cubo no anima | ✅ Reales sincronizadas |
| Cuerpo físico (colisiones) | Real | ❌ Cubo sin colisión | ✅ Real |

**Gap principal — esto es lo que estamos persiguiendo con Phase 4d.**

### Combate (LO QUE TE FALTA)

| Aspecto | Saponita Vanilla | Items Custom HOY | **Items Custom OBJETIVO** |
|---|---|---|---|
| El otro puede atacar enemigos? | ✅ Sí | ❌ NO (cubo no tiene weapon hitbox) | ✅ Sí |
| Vos podés atacar lo que el otro daña? | ✅ Sí | N/A — sin coordinación de hitboxes | ✅ Sí |
| Daño escalado | ~80% (phantom penalty) | N/A | ✅ **100% sin penalty** |
| Friendly fire | Limited | N/A | Configurable |
| Targeting (lock-on a enemigos juntos) | ✅ | ❌ | ✅ |

### Limitaciones del phantom (LO QUE QUEREMOS REMOVER COMPLETAMENTE)

| Limitación | Saponita | Items Custom HOY | **Items Custom OBJETIVO** |
|---|---|---|---|
| Estus heal slower | ⚠ Sí | ✅ NO (no sos phantom) | ✅ Estus normal |
| Estus blocked con red phantom (pequeña) | ⚠ Sí | ✅ NO | ✅ Nunca bloqueado |
| No items/chests/levers | ⚠ Sí | ✅ NO (no sos phantom) | ✅ Acceso normal |
| No bonfires | ⚠ Sí | ✅ NO | ✅ Acceso normal |
| No NPC interaction | ⚠ Sí | ✅ NO | ✅ Acceso normal |
| Damage scaling reducido | ⚠ Sí | ✅ NO | ✅ 100% damage |
| No Item Box / Storage | ⚠ Sí | ✅ NO | ✅ Acceso normal |
| Apariencia fantasmal dorada | ⚠ Sí | ✅ NO (no aparece — solo cubo) | ✅ Human visible |

**Items custom YA ganan en limitaciones funcionales** — pero no
cuentan porque actualmente no hay ningún personaje renderizado
(solo cubo). Cuando logremos render real, estas ventajas se
materializan.

### Timer / duración de sesión

| Aspecto | Saponita Grande | Saponita Pequeña | Items Custom HOY | **Items Custom OBJETIVO** |
|---|---|---|---|---|
| Duración base | 4000s (66m40s) | 500s (8m20s) | ✅ Sin límite (?) | ✅ Sin límite |
| Con NER | 6000s | 750s | N/A | N/A |
| Decrement con kills | ⚠ Sí | ⚠ Sí | ✅ No | ✅ No |
| Boss kill = session end | ⚠ Sí | ⚠ Sí | ✅ No | ✅ No |
| Host death = session end | ⚠ Sí | ⚠ Sí | ✅ No | ✅ No |
| Homeward Bone returns phantom | ⚠ Sí | ⚠ Sí | ✅ No | ✅ No |
| Phantom darkens visualmente | NO | ⚠ Sí | ✅ N/A | ✅ N/A |

**Items custom YA ganan totalmente** en timers/triggers. Ningún timer
artificial limita la sesión Bonfire-coop. Esta es otra ventaja
estructural enorme.

### Fog walls (muros de niebla)

| Aspecto | Saponita | Items Custom HOY | **Items Custom OBJETIVO** |
|---|---|---|---|
| Muros generados al activar co-op | ⚠ Sí | ❓ Desconocido (no testeado con render real) | ✅ Suprimidos |
| Movilidad entre áreas | Restringida al zone del summon | ✅ Libre (cubo se mueve donde sea) | ✅ Libre |
| Host enters fog wall = phantoms home | ⚠ Sí | ✅ No | ✅ No |

**Items custom YA ganan** porque no usan la maquinaria de fog-wall
spawn de saponita. **Si introducimos engine-spawn (Phase 4d), debemos
verificar que NO active fog walls.** Si las activa, NOP el spawn.

### Caps de phantoms simultáneos

| Aspecto | Saponita | Items Custom HOY | **Items Custom OBJETIVO** |
|---|---|---|---|
| Max phantoms cooperativos | 3-4 vanilla | ✅ Ilimitado (cubos, sin slot pool) | ✅ Hasta 6 (slot pool físico) o ilimitado vía cap-bypass |

### Recompensas

| Aspecto | Saponita Grande | Saponita Pequeña | Items Custom |
|---|---|---|---|
| Token of Fidelity | ✅ | ❌ | ❌ (irrelevante — no usás saponita) |
| Sunlight Medal (covenant) | ✅ Heir of the Sun | ✅ Heir of the Sun | ❌ |
| Smooth & Silky Stone | ❌ | ✅ | ❌ |
| Souls del boss (25%) | ✅ | ✅ | N/A |
| Health/durability/spells restored | ✅ | ✅ | N/A — no aplica |

Items custom **no dan estas recompensas** porque no son parte del
sistema vanilla. Esto está bien — el objetivo es co-op libre, no
farming.

---

## ¿Qué tienen los items custom HOY que las saponitas no?

✅ **Workflow instantáneo** — sin colocar signos
✅ **Zonas universales** — funciona en Majula, post-boss, etc.
✅ **Privacy automática** — solo tu session
✅ **Sin timer artificial** — sesión continúa indefinidamente
✅ **Sin death triggers** — morir no envía al phantom a casa
✅ **Sin boss-kill end** — matar boss no termina sesión
✅ **Apariencia natural** — no hay shader phantom dorado aplicado a vos
✅ **Estus full / items / bonfires / chests** — no estás marcado como phantom
✅ **Damage 100%** — sin scaling penalty

---

## ¿Qué les FALTA a los items custom para ser "saponitas unlocked"?

❌ **Render del otro jugador como cuerpo real** (no cubo)
   - Status: Phase 4d, queue-write spawn pendiente v2.9.16
   - Pieza crítica para acercarnos a saponita

❌ **Hitboxes / colisión / combate compartido**
   - Status: depende del engine-spawn — una vez Phase 4d funcione,
     el engine maneja hitboxes automático (el phantom es real PlayerCtrl
     con todos sus sub-systems activos)

❌ **Sincronización de animaciones reales** (no solo position)
   - Status: el PoseShm de Bonfire ya envía animation_state, pero el
     cubo no lo aplica. Una vez engine-spawn, las animaciones llegan
     automático via el state-machine del PlayerCtrl

---

## Gap → roadmap concreto

### Lo que falta del lado **render**

| Gap | Cómo cerrarlo | Esfuerzo |
|---|---|---|
| Cubo → personaje real | Phase 4d queue-write spawn (`SAPONITA_PEQUENA.md` snippets ready) | v2.9.16 — ~3-4h código |
| Phantom golden shader (que NO queremos aplicar) | Forzar `is_phantom = false` en el spawned PlayerCtrl tras inject | ~30min después de tener Phase 4d |
| Sub-pointers (intermediate, equipment) | Engine los allocará al spawnear via FUN_14037EBE0 | Automático |

### Lo que falta del lado **combate**

| Gap | Cómo cerrarlo | Esfuerzo |
|---|---|---|
| Combat hitboxes | Automático una vez engine-spawn funciona | 0 |
| Lock-on / targeting | Automático — PlayerCtrl es real, engine lo trata como peer | 0 |
| Damage sync | Bonfire-coop ya tiene infraestructura SHM. Plumbing pendiente para attack events | ~2h después |

### Lo que YA está

| Feature | Status |
|---|---|
| Workflow instantáneo | ✅ Today |
| Zone-universal | ✅ Today |
| Privacy by session | ✅ Today |
| Cube overlay | ✅ Today (DS2_RenderHook Phase 4a) |
| Cube suppression cuando phantom engine activo | ✅ v2.9.12 |
| Position sync | ✅ Today (DS2_PoseShm) |
| Cross-launch resolver (gm chain) | ✅ Today |

---

## Mapping de items custom → equivalente saponita

| Bonfire Item | Item ID | Command | Equivalente Saponita | Status objetivo |
|---|---|---|---|---|
| **Blessed Eye Orb** | 62061000 | `session.create` | Colocar saponita (host invocable) | Crear sesión + ofrecer slot phantom (Phase 4d) |
| **Crystal Eye Orb** | 62061001 | `session.join` | Clickear signo (guest se invoca al host) | Invocar al host engine-cooperativo |
| Chaos Eye Orb | 62061002 | `session.invade` | Red Sign Soapstone (invasor) | PvP equivalent |
| Abyssal Eye Orb | 62061003 | `session.leave` | Recoger sign / Homeward Bone | Termina sesión |
| Ominous Tome | 62061004 | `rules.cycle` | N/A | Cambia reglas Bonfire |
| Dried Fingers | 62061005 | `invasions.taunt` | Bell Keeper's Seal | Open invasions |
| Cursed Pendant | 62061006 | `world.infection` | Bonfire Ascetic | World modifier |
| Crimson Blossom | 62061007 | `curse.accrue` | N/A | Curse stack |

**Items críticos para tu visión**: Blessed Eye Orb (host) + Crystal
Eye Orb (joiner). Estos son los que necesitan Phase 4d.

---

## Diferencias filosóficas

### Saponita vanilla = "FromSoft model"
- Diseñado con **frustración** intencionalmente (signos pueden ser
  raros, SM mismatch común, time pressure)
- **Encuentros aleatorios** son una mecánica social
- **Limitaciones del phantom** son un balance de poder ("ayudás pero
  no podés progresar tu propio mundo")
- **Death penalty** es parte del fun (riesgo)

### Items custom Bonfire = "Player-friendly co-op"
- **Cero friction**: directo, instantáneo, privado
- **Sin penalidades artificiales** — co-op debería ser "jugar con tu
  amigo", no "tolerar penalty por hacerlo"
- **Apariencia y mecánicas idénticas a solo**: el peer es un peer real,
  no un visitante traslúcido

---

## TL;DR (para tu hermano)

> "Estamos haciendo un sistema que reemplaza saponita completamente.
> Usás un item custom (Blessed Eye Orb), tu hermano usa otro (Crystal
> Eye Orb), aparece al lado tuyo como si fuera él normal — su
> personaje, sus armas, todo. Pueden pelear, recoger items, abrir
> cofres, usar bonfires, todo. Sin timer, sin que un boss termine la
> sesión, sin enviarte a casa al morir. En cualquier zona del juego,
> incluso Majula. Y sin tener que esperar a que vea tu signo o
> matchear con Soul Memory — instantáneo."

**Estado actual:**
- ✅ Workflow + zonas universales + privacy + sin timer = ya funcionan
- ❌ Render real del otro jugador = falta Phase 4d (en construcción)
- Resto de limitaciones phantom = ya no aplican (no estás "phantom"
  en este sistema, sos peer Bonfire-coop)

---

## Referencias

- `Docs/SAPONITA_GRANDE.md` — referencia técnica saponita normal
- `Docs/SAPONITA_PEQUENA.md` — referencia técnica saponita pequeña
- `Docs/SAPONITA_GRANDE_BYPASSES.md` — bypass status grande
- `Docs/SAPONITA_PEQUENA_BYPASSES.md` — bypass status pequeña
- `Docs/SAPONITA_LIMITATIONS.md` — catálogo general de limitaciones
- `Docs/TRACK_C_PHASE_2B2_DESIGN.md` — engine-spawn design (Phase 4d)
