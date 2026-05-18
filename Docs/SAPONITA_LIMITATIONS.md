# SAPONITA — Limitaciones vanilla y posibles bypasses

Catálogo de TODAS las restricciones que DS2 SOTFS impone sobre el
sistema saponita (co-op summon), con notas técnicas sobre dónde
vivir cada limitación en memoria/código y un veredicto honesto:
"sí puedo romperla" / "tengo pistas" / "no sé aún".

Companion a `Docs/SAPONITA.md` (mapa de estado live + código).

Last updated 2026-05-18.

---

## Las dos saponitas de DS2 SOTFS

DS2 tiene DOS items principales de invocación cooperativa:

| Item | Item ID | Función vanilla |
|---|---|---|
| **White Sign Soapstone** ("Saponita Blanca") | `60155000` | Versión clásica. Coloca señal blanca, los hosts pueden invocarte como phantom de cooperación. Reusable infinito. |
| **Small White Sign Soapstone** ("Saponita Blanca Pequeña") | `60165000` (aprox.) | Versión "scaling" — el phantom se nivela al host. Pensada para new game / niveles bajos. Reusable. |

Otros items de la misma familia que comparten infraestructura:

| Item | ID aprox. | Función |
|---|---|---|
| Red Sign Soapstone (saponita roja) | `60175000` | Te ofreces para PvP/invasión amistosa |
| Cracked Red Eye Orb | `60185000` | Invasion offensive |
| Dragon Eye | — | Arena de duelos (Aldia / Coliseo) |
| Bell Keeper's Seal | — | Covenant auto-summon |
| Bonfire Ascetic | — | (no es saponita pero también modifica el world state) |

Todos estos comparten gran parte de la maquinaria: cola de spawn
en `phantom_mgr + 0x5C0`, slot pool en `slot_mgr + 0x5D0`, timer en
`phantom_mgr + 0x218`. Las diferencias entre ellos son flags en el
queue entry y validaciones server-side.

---

## Limitaciones vanilla — catálogo completo

### A. Matchmaking restrictions

#### A1. Soul Memory matching

- **Qué hace**: DS2 SOTFS usa "Soul Memory" (= total de souls ganadas
  jamás, no se reduce al morir) como criterio principal de
  matchmaking. Hosts y phantoms tienen que estar dentro de un rango
  de SM (usualmente ±10% más una ventana fija).
- **Donde vive (engine)**: el check se hace SERVER-SIDE en
  matchmaking vanilla. Cliente envía su SM en el packet de signo.
  Soul Memory del jugador local está en `PlayerCtrl` (sub-struct
  attainable vía intermediate). Mejor anchor: cualquier read del
  campo SM en la cadena de char_data.
- **Veredicto**:
  - ✅ **Romper en Bonfire-coop**: trivial — Bonfire-coop NO usa
    matchmaking vanilla. Cuando inyectamos a un phantom directamente
    en la queue (`phantom_mgr + 0x5C0`), saltamos toda la fase
    server-side. Soul Memory deja de importar.
  - ⚠️ **Romper en vanilla**: imposible cliente-side limpio (el
    server compara). Solo posible con un servidor custom (que es
    lo que hace Bonfire de hecho).

#### A2. Soul Level (SL) ranges

- **Qué hace**: además de SM, DS2 también considera Soul Level con
  un range tipo Souls clásico (±10).
- **Donde vive**: server-side. SL está en char_data como uno de los
  campos del LevelStatus (proto). PlayerCtrl +0x128 = level (lo
  documentamos earlier).
- **Veredicto**: igual que A1 — bypasseable en Bonfire-coop por
  inyección directa en queue.

#### A3. Phantom slot cap (4 phantoms / 6 slots)

- **Qué hace**: DS2 SOTFS permite hasta 4 phantoms cooperativos
  (white) en el mundo del host. Hay 6 slots totales en el slot pool
  (slot 0 = local host, slot 1-5 = phantoms).
- **Donde vive (engine)**: el check `slot_index < 6` está dentro de
  `FUN_1403572E0` (el allocator). Pre-check en `FUN_1401A0DC0` lee
  `*(char *)(world_mgr + 0x301) < 0x06`.
  - RVA `FUN_1401A0DC0` = `0x1A0DC0`
  - Instrucción específica: `cmp byte ptr [rax+301], 6` seguido de
    `jl ...` o equivalente.
- **Veredicto**:
  - ✅ **Romper**: NOP-patch los dos `cmp ..., 6` (uno en
    FUN_1401A0DC0, otro en FUN_1403572E0) o un Detours hook que
    fuerce el byte a 0 cuando se lea. Riesgo bajo. Documentado en
    `TRACK_C_PHASE_2B2_DESIGN.md` Phase 2B.2E.
  - **Pendiente**: no implementado en código aún. Trivial (~20 LOC
    + AOB scan para encontrar el `cmp 6` bytes).

#### A4. Area restrictions (zonas donde saponita no funciona)

- **Qué hace**: ciertas zonas tienen el flag "no co-op signs":
  - **Majula** (hub) — sin signos para preservar el lobby
  - **Dragon Aerie / Dragon Shrine** (algunos rincones)
  - **Memory of...** zonas (las "Memorias" del Giant Lord etc.)
    tienen reglas especiales
  - **DLC arenas** (Old Iron King's prison-bunker zones, etc.)
  - **Aldia's Keep antes de Vendrick's audience** (lore-locked)
  - **Post-game** zones tras matar al boss del área
- **Donde vive**: PlayAreaParam (en regulation.bin) tiene un campo
  per-zone `disableCoop` (o equivalente). El engine lee el current
  zone ID (de PlayerCtrl + chain) y rechaza la colocación del sign
  si el flag está set.
- **Veredicto**:
  - 🟡 **Romper en cliente**: posible vía Smithbox / regulation
    edit — sería un mod separado al regulation.bin. NO breakeable
    desde el Injector sin tocar archivos.
  - ✅ **Romper en Bonfire**: si Bonfire injecta directo en la queue
    (Phase 4d), el spawn ocurre sin pasar por el sign-placement
    check. La zona ya no importa.

#### A5. Boss-defeated state

- **Qué hace**: una vez muerto el boss del área, los signs ya no
  aparecen en ese área (el "área está despejada").
- **Donde vive**: event flag por área + check en sign-listing.
- **Veredicto**:
  - ✅ Bonfire-coop bypass: idem A4 — inyectar directamente sin pasar
    por sign-listing.

---

### B. Phantom appearance & abilities

#### B1. Phantom "ghost" appearance (la limitación que más te molesta)

- **Qué hace vanilla**:
  - Si **vos** ponés saponita y otro te invoca → **tu personaje se
    convierte en phantom**: aparece **traslúcido/dorado-fantasmal**
    para vos mismo, aplica el shader de phantom blanco/dorado,
    pierde colisión con NPCs amistosos, **tus armas hacen menos daño**
    (~80% damage scaling de phantom), Estus a la mitad (5 charges →
    2 o 3), no podés activar bonfires, no podés abrir cofres, no
    podés activar puertas/elevators/levers, no podés recoger items.
  - El **host (tu hermano) sí te ve como fantasma dorado**.
  - **Tu hermano sigue siendo human form** desde su propia
    perspectiva, y vos lo ves a él como human (no como phantom)
    desde la tuya.
- **Donde vive en engine**:
  - **El `phantom_type` byte**: en el queue entry está en el rango
    `+0x29` (per Ghidra signature de `FUN_1401A1650` request struct)
    o equivalente. En `PlayerCtrl`, hay flags que indican
    "renderear como phantom" — el render hook DS2_RenderHook lo lee
    indirectamente.
  - El **valor del phantom_type** controla el formato wide-string
    name (`L"NetworkPlayer_%06u"` vs `L"GhostPlayer_%06u"` vs etc.)
    y simultáneamente el material/shader que se aplica al render.
  - Los **damage scaling** y **Estus halved** están en
    chr_data sub-structs — flags como `is_phantom`, `is_white_phantom`
    en intermediate +0xE0 region.
- **Veredicto**:
  - 🟡 **Romper APARIENCIA**: posible — write `phantom_type = 0` (=
    "host" / "local") en el PlayerCtrl phantom para que el render
    hook use el material de human en vez del dorado. La mecánica
    network sigue siendo "phantom" pero visualmente es human.
    - **Riesgo**: cambiar el byte podría confundir otros sistemas
      (mejor que tener cubos, igual). Necesita testing live.
  - 🟡 **Romper LIMITACIONES**: posible flag-by-flag. Hay flags
    bool en chr_data como `cannot_open_chest`, `cannot_use_bonfire`,
    `estus_halved`. Cada uno es un write de 1 byte.
  - ❌ **Romper ENTERAMENTE para ambos lados (cliente que invoca Y
    el invocado)**: server-side. Bonfire bypassea el server, así que
    podemos hacer que VOS no veas a tu hermano como phantom — pero
    no podés cambiar cómo tu hermano se ve a sí mismo desde su
    cliente sin que él también corra el mismo mod.
- **Para tu caso**:
  > "si ubiera una forma que los 2 mantuvieramos nuestra apariencia
  > real no fantasma sin limitaciones eso seria genial"
  
  Esto requiere que AMBOS clientes corran el mismo Bonfire con la
  misma versión de Injector y los mismos parches activos. Si los dos
  tienen Bonfire v2.9.X+ con phantom-appearance bypass:
  - Cada uno hookea su propio render para mostrar al otro como human
  - Cada uno se ve a sí mismo como human (no aplicar phantom shader)
  - Si limpiamos las flags `is_phantom` en char_data antes de que
    el render shader las lea, ambos se ven normales
  - Las **limitaciones funcionales** (Estus halved, etc.) son
    fuerzas LOCALES del cliente phantom — se pueden NOP-patch.
  
  Veredicto pragmático: **POSIBLE con ambos corriendo el mod**.
  Requiere identificar el flag `is_phantom` en char_data y el shader-
  selector en render path. Probablemente 1-2 sesiones de RE.

#### B2. Phantom can't use bonfires

- **Donde vive**: flag en chr_data + check en bonfire-use path
  (`FUN_14017DC40`-ish, near `RestAtBonfire`).
- **Veredicto**: 🟡 NOP el check del flag. ~10 LOC.

#### B3. Phantom Estus halved

- **Donde vive**: max-estus stat es modificado por el `is_phantom`
  flag en stat-recalc path. Likely lives in intermediate +0xE0 sub.
- **Veredicto**: 🟡 forzar el flag a 0 → estus full. O patch el
  multiplier.

#### B4. Phantom can't pick up items / open chests / activate levers

- **Donde vive**: cada acción interaction tiene un check
  `if (player.is_phantom) reject`. Múltiples sites.
- **Veredicto**: 🟡 todos derivan del mismo `is_phantom` flag.
  NOP-patch ese flag → todos los checks pasan automáticamente.

#### B5. Phantom damage scaling (~80%)

- **Donde vive**: damage calc reads `is_phantom` and applies
  multiplier. AtkParam_Pc or similar.
- **Veredicto**: 🟡 mismo flag. Mismo fix.

---

### C. Session limits

#### C1. Session timer (500.0 segundos = 8 min 20 seg)

- **Qué hace**: tras N segundos el phantom es enviado a casa.
- **Donde vive**: `phantom_mgr + 0x218` (float). Documentado en
  `SAPONITA.md` sección 2.
- **Veredicto**:
  - ✅ **Romper**: ya verificado live. Write `500.0` cada 500ms
    al `+0x218` mientras phantom activo. Engine zero validation.
  - **Pendiente código**: comando `saponita.timer.freeze_auto` en
    v2.9.16. Snippet listo en `SAPONITA.md` sección 5B.

#### C2. Phantom dies → sent home

- **Qué hace**: si el phantom muere, vuelve a su mundo.
- **Donde vive**: muerte trigger en HP=0 transition; el "send home"
  llama a la state-machine de phantom transition (FUN_1401A0D20
  case 2 = "leaving"). Si HP > 0 el case 2 no se activa.
- **Veredicto**:
  - 🟡 **Romper "morir = volver"**: posible. NOP-patch el
    state-transition que setea state=2 al detectar HP=0. O hookear
    el HP-reaches-zero callback. Complejo (la state machine se
    integra con muchos sistemas).
  - Más simple: write HP=max constantly mientras el phantom esté
    activo — `slot1_PlayerCtrl + 0x168 = max_hp`. Effectively god
    mode for the phantom.

#### C3. Host dies → all phantoms sent home

- **Donde vive**: world_mgr tracking of host HP + on-death trigger.
- **Veredicto**: 🟡 NOP el trigger o write HP_host = max al
  detectar host-death event.

#### C4. Boss killed → phantoms sent home

- **Donde vive**: boss event flag set → triggers phantom cleanup
  via FUN_1401A1580 (case 3 dead/cleanup).
- **Veredicto**: 🟡 NOP el boss-killed cleanup trigger. Likely
  inside FUN_1401A1580 or its caller.

#### C5. Phantom uses Homeward Bone → returns

- **Veredicto**: ✅ no-op por nuestra parte — comportamiento
  voluntario. Si querés bloquearlo: NOP el item-use validation
  para HomewardBone si player is phantom.

---

### D. Fog walls (los muros de niebla que mencionaste)

- **Qué hace vanilla**: cuando un phantom entra al mundo del host,
  el engine genera **muros de niebla en las salidas del área**
  para impedir que el phantom abandone esa zona. Los muros
  desaparecen cuando el phantom es enviado a casa.
- **Donde vive**:
  - El spawn de muros está manejado por el sistema de
    PlayAreaParam + sub-managers. Cada zona tiene N "fog wall
    spawn points" definidos.
  - El check trigger es "is_co_op_active" → activate fog walls.
  - El engine puede ser GM-singleton + algún sub-mgr para
    "active fog walls" list.
- **Veredicto**:
  - 🟡 **Romper**: medio-complejo. Dos approaches:
    - **A**: Buscar el spawn-fog-walls function via cadena
      "co_op_phantom_count > 0 → spawn". NOP-patch el call.
    - **B**: Encontrar la lista de muros activos + flag de
      visibilidad. Forzar visibility=false / collide=false.
  - **Pendiente RE**: no encontrado aún. Likely en FUN_14018xxxx
    range (zona/area code). String search por "fog" / "FogWall"
    en decompile.c daría sites.

---

### E. Item drop restrictions

- **Qué hace**: phantoms y hosts no pueden tradear items vía drop.
  Anti-cheat protection.
- **Donde vive**: ItemDrop callback checks `is_phantom` y el flag
  de session. Server-side adicional.
- **Veredicto**:
  - 🟡 **Romper localmente**: posible — NOP el check. Pero el
    server también valida → drops podrían sincronizar mal.
  - ✅ **En Bonfire-coop**: si ambos corren Bonfire con el bypass,
    las drops via la SHM peer infrastructure work. Esto ya es
    funcional.

---

### F. Sign visibility / placement restrictions

#### F1. Signs disappear after N minutes if not summoned

- **Donde vive**: cada signo tiene su propio timer (separate from
  C1 session timer). Server-side.
- **Veredicto**: ✅ bypasseado en Bonfire (no usamos signs reales).

#### F2. Sign placement requires unburnt human / specific items

- **Qué hace vanilla**: tenés que estar en forma humana para colocar
  saponita normal. Saponita pequeña funciona también en hollow form.
- **Donde vive**: item-use validation gate en `FUN_140xxxxxx`.
  Check de "is_human" flag.
- **Veredicto**: ✅ Bonfire ya patchea esto via los items custom
  (Blessed Eye Orb etc. funcionan en hollow).

#### F3. Only one sign at a time per player

- **Veredicto**: ✅ Bonfire no usa signos vanilla, irrelevante.

---

### G. Network / packet validation

#### G1. Soapstone item ID must be valid

- **Donde vive**: item validation lista en
  `Source/Server.DarkSouls2/Server/GameService/Utils/Ids/ItemId.inc`
  y server logic.
- **Veredicto**: ✅ Bonfire usa items custom (62061000+) que ya
  bypasean.

#### G2. Steam P2P token validation

- **Qué hace**: el server emite tokens que validan packets.
- **Donde vive**: Steam SDK + Frpg2 protocol.
- **Veredicto**: ❌ no necesitamos romper esto — Bonfire usa su
  propia infraestructura UDP/Steam P2P + SHM, no el server vanilla.

---

## Resumen ejecutivo — lo que puedes romper YA vs lo que falta

### ✅ ROTAS (verified live o trivial de hacer)

| Limitación | Cómo se rompe | Status |
|---|---|---|
| Session timer (500s) | Write `phantom_mgr+0x218 = 500` cada 500ms | Verified live; código pendiente para v2.9.16 |
| Matchmaking SM/SL | Bonfire-coop bypassea el server vanilla | Ya funcionando |
| Item-use restriction (hollow form) | Items custom Bonfire (62061000+) | Ya funcionando |
| Area restrictions (sign placement) | Queue-write injection sin sign | Ya documentado, falta v2.9.16 |
| Boss-defeated state | Queue-write injection sin sign | Idem |
| One-sign-at-a-time | Bonfire no usa signs vanilla | N/A |

### 🟡 BREAKEABLES (tengo pistas suficientes, falta código)

| Limitación | Approach | Esfuerzo |
|---|---|---|
| **Phantom appearance (golden ghost)** | Write `phantom_type` = 0 / NOP shader-select hook | 2-4h RE para encontrar shader site + 1h código |
| **Phantom can't use bonfires** | NOP item-use validation for is_phantom check | 1-2h |
| **Phantom Estus halved** | NOP stat-recalc multiplier for is_phantom | 1-2h |
| **Phantom can't pick up items / open chests** | NOP each interaction check (or unified `is_phantom` flag = 0) | 2-3h (multiple sites) |
| **Phantom damage scaling (~80%)** | NOP damage multiplier for is_phantom | 1h |
| **Phantom dies → sent home** | NOP state=2 trigger on HP=0 OR write HP=max loop | 2h |
| **Host dies → all phantoms home** | NOP host-death trigger broadcasting | 2h |
| **Boss killed → phantoms home** | NOP boss-killed cleanup | 1h |
| **Phantom slot cap (6 = 4 phantoms)** | NOP two `cmp ..., 6` instructions | 1h (Phase 2B.2E ya documentado) |
| **Fog walls on co-op** | Find spawn-fog-wall function, NOP the trigger | 4-6h RE + 1h código |

### ❌ NO BREAKEABLES (sin custom server)

| Limitación | Por qué no |
|---|---|
| Vanilla DS2 server SM matching | Hard server-side. Pero Bonfire-coop tiene su propio server, así que en práctica esto SÍ se rompe via el path Bonfire. |
| Anti-cheat soft bans | FromSoft tracks anomalies. Mejor jugar en Bonfire-coop privado donde no hay server FromSoft involucrado. |

---

## Para tu caso específico (vos + tu hermano, ambos human, sin limitaciones)

**Lo que necesitás:**

1. ✅ Spawn engine-cooperativo sin saponita (Phase 4d queue-write — pendiente v2.9.16)
2. 🟡 Phantom appearance bypass (= "que tu hermano no se vea dorado y vos no te veas dorado")
3. 🟡 Phantom limitations bypass (estus, bonfires, items, damage)
4. ✅ Timer infinito (saponita.timer.freeze_auto — pendiente v2.9.16)
5. 🟡 Fog walls suprimidas (= movilidad total entre áreas)
6. 🟡 Phantom-dies-go-home bypass (= si morís no te mandan a casa)

**Requisitos prácticos:**
- AMBOS corren Bonfire con el mismo Injector
- AMBOS tienen activado el "engine-phantom mode" (cuando se materialice en v2.9.16+)
- AMBOS tienen los bypasses activados en Bonfire UI

**Setup ideal completo (v2.9.20+ futuro):**
- Bonfire UI tiene checkbox "co-op libre" que activa:
  - saponita.timer.freeze_auto (infinite session)
  - phantom-appearance bypass (todos human)
  - phantom-limitations bypass (estus full, bonfires OK, items OK)
  - fog-walls suppression (movilidad libre)
  - phantom-death bypass (no return on death)

Esto es ESCALABLE — cada bypass es un módulo independiente que se
puede activar/desactivar en config. La infraestructura ya existe en
Bonfire (`RuntimeConfig` + Injector hooks).

---

## Roadmap propuesto (orden de prioridad)

1. **v2.9.16** — implementar lo que ya está descubierto y documented:
   - Saponita timer freeze/kill/set/freeze_auto commands
   - Queue-write spawn skeleton (basic, brother-clone version)
   - Bug fix RVA `0x1616CF8`
   
2. **v2.9.17** — phantom appearance bypass
   - RE para encontrar shader-selector / phantom flag
   - Hook que fuerce render-as-human
   
3. **v2.9.18** — phantom limitations bypass (single bundle)
   - NOP los checks de is_phantom para: bonfires, items, chests,
     levers, damage scaling, Estus halved
   
4. **v2.9.19** — fog walls + slot cap
   - Find fog-wall spawn function, NOP it conditionally
   - NOP `cmp ..., 6` for unlimited phantom slots
   
5. **v2.9.20** — bundle "Bonfire freeform co-op"
   - Toggle único que activa todo lo anterior
   - UI checkbox + heartbeat exposes status

---

## Diferencia entre las dos saponitas (técnica)

Vanilla DS2 SOTFS tiene mecánicas distintas para las dos saponitas
que comparten infraestructura pero divergen en checks:

| Mecánica | Saponita Blanca | Saponita Blanca Pequeña |
|---|---|---|
| Item ID | 60155000 | 60165000 |
| **SM matching window** | Tight | Wider (más permisivo) |
| **Level scaling** | No (phantom mantiene su SL real) | **Sí — phantom es scaled al host SL** |
| **Phantom HP/damage** | Native stats | Scaled stats |
| **Time limit** | 500s (= +0x218 max) | 500s (igual) |
| **Boss kill = home** | Sí | Sí |
| **Estus** | Halved | Halved |
| **Use restrictions** | Same as host (no bonfire, no chest) | Same |

El **level scaling** es la diferencia clave. Vive en el char_data
serializer — cuando construye el queue entry para una saponita
pequeña, modifica los stats antes de enviar.

**Para tu visión**: ambas saponitas convergen al mismo queue-write
spawn cuando hacemos Phase 4d. La diferencia visible al usuario
sería simplemente "estás scalado" vs "no estás scalado", que se
podría exponer como un toggle más en Bonfire.

---

## Referencias y fuentes

- DS2 SOTFS internal RE: `Docs/SAPONITA.md` (master), `TRACK_C_PHASE_2B2_DESIGN.md`, `TRACK_C_RE_SESSION_01.md`
- Ghidra decompiled output: `Docs/ghidra-out/DarkSoulsII-out/decompiled.c`
- Live forensic dumps: `Docs/templates/*.bin`
- DS2 wiki sources (general behavior): fextralife DS2 wiki, Fextra
  "Co-op" article, DS2 multiplayer rules article
- DS3OS / Bonfire fork heritage: gives us the Frpg2 protocol shape
- Player community knowledge: r/DarkSouls2 mechanical posts re:
  fog walls + soul memory matchmaking
