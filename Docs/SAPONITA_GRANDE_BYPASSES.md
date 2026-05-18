# Saponita Grande — Tabla de bypass status

Por cada limitación vanilla de la **White Sign Soapstone** (saponita
normal/grande): qué hace, dónde vive en el engine, y si tenemos
bypass implementado, parcial, o pendiente.

Companion a `Docs/SAPONITA_GRANDE.md` (referencia técnica completa).

Última actualización: 2026-05-18.

Leyenda:
- ✅ **Rota** — bypass funcionando hoy
- 🟢 **Trivial** — código pendiente, approach claro
- 🟡 **Pistas** — RE necesario antes de codear (1-4h estimado)
- 🔴 **No mapeada** — requiere RE significativa (4h+) o impossible cliente-side

---

## A. Restricciones de matchmaking

| Limitación | Status | Notas |
|---|---|---|
| **Soul Memory matching range** | ✅ Rota | Bonfire-coop no usa server vanilla. Queue-write injection (Phase 4d) bypassa completamente. |
| **Soul Level matching** | ✅ Rota | Igual que SM — el server vanilla está fuera del path Bonfire. |
| **Sign tier system** | ✅ Rota | Bonfire no coloca signos reales — inyectamos directo a la queue. |
| **Name-engraved Ring same-god requirement** | ✅ Rota | Si ambos clientes son Bonfire-coop con el mismo `bonfire_coop_session_id`, son partners automáticamente. |

---

## B. Restricciones de área

| Limitación | Status | Notas |
|---|---|---|
| **No funciona post-boss-kill** ⚠ EXCLUSIVA grande | 🟢 Trivial via queue-write | Es la diferencia más importante con la pequeña. Bypass: Phase 4d (write directo a queue) salta el sign-listing check que valida "boss alive en este área". |
| **Majula no permite saponita** | 🟢 Trivial via queue-write | Idem |
| **DLC arenas / Ordeal's End** | 🟢 Trivial via queue-write | Idem — el área-restriction está en sign-placement, no en spawn-execution |
| **Memorias (Memory of...) reglas especiales** | 🟡 Pistas | Necesita verificar que el spawn no requiera flag de "in memory" en el host. RE estimado 1h. |

---

## C. Phantom appearance & abilities (igual que pequeña)

Estas limitaciones son IDÉNTICAS entre las dos saponitas porque
derivan del mismo flag `is_phantom` en char_data.

| Limitación | Status | Notas |
|---|---|---|
| **Shader phantom dorado/translúcido** | 🟡 Pistas | Necesita encontrar el shader-selector en DS2_RenderHook path. Approach: NOP el `if (is_phantom) use_phantom_material` site. ~2-4h RE. |
| **Estus heal más lento** | 🟡 Pistas | EstusUseFn lee `is_phantom`. NOP la division/multiplier. ~1-2h. |
| **No items / chests / levers** | 🟡 Pistas | InteractionGate lee `is_phantom`. Múltiples sites pero todos derivados del MISMO flag → un solo NOP del flag desbloquea todos. ~2-3h. |
| **No bonfires** | 🟡 Pistas | BonfireUseFn lee `is_phantom`. NOP. ~1h. |
| **Damage scaling ~80%** | 🟡 Pistas | DamageCalc multiplier. NOP. ~1h. |
| **No interaction con NPCs** | 🟡 Pistas | NPCDialogFn check is_phantom. NOP. ~1h. |
| **No Item Box / Storage** | 🟡 Pistas | StorageOpenFn check is_phantom. NOP. ~30min. |

**KEY**: si encontramos el `is_phantom` flag y lo forzamos a 0 (o lo
NOP-eamos en la PlayerCtrl), TODAS estas limitaciones caen
simultáneamente. Es un solo punto de ataque. **Esto es la prioridad
1 después del timer**.

---

## D. Session limits

| Limitación | Status | Notas |
|---|---|---|
| **Timer 4000s (66m40s)** | 🟡 Pistas | El offset del timer GRANDE no está mapped aún (sí el de la pequeña a +0x218). Necesita sesión live con phantom grande activo, scan float que decrementa 1/sec con MAX=4000.0f cerca. ~30min de RE live. |
| **Timer 6000s con Name-engraved Ring** | 🟡 Misma búsqueda | Mismo field, max es 6000 si NER equipado |
| **Each kill reduces timer** | 🟡 Pistas | El call al "subtract kill weight from timer" se hace en enemy-death event. Hook ahí para NOP. ~1h una vez que tenemos el timer field. |
| **Boss kill = session ends immediately** ⚠ EXCLUSIVA grande | 🟡 Pistas | Boss-death event llama a "cleanup all phantoms". NOP el cleanup trigger. ~1h. **Nota**: en la pequeña esto NO termina la sesión inmediatamente (otra diferencia clave). |
| **Phantom dies → home** | 🟡 Pistas | State-machine case 2 (leaving) on HP=0. NOP transition o force HP=max. ~2h. |
| **Host dies → all phantoms home** | 🟡 Pistas | Host-death broadcast. NOP el broadcast or NOP receivers. ~2h. |
| **Host enters fog wall without boss → phantoms home no reward** | 🟡 Pistas | Fog-wall-traversal check. NOP. ~1h. |
| **Homeward Bone use → phantom returns** | 🟢 Comportamiento voluntario | No requiere fix — el phantom literalmente eligió usar el item. Si quisiéramos bloquearlo: NOP HomewardBone use if is_phantom. ~30min. |

---

## E. Fog walls

| Limitación | Status | Notas |
|---|---|---|
| **Muros de niebla en bordes de área al haber phantom activo** | 🟡 Pistas | Fog-wall spawn function call on co_op_count > 0. NOP el call. RE estimado 4-6h (no mapeada aún, buscar via string "FogWall" o trace upwards desde renderer fog material). |
| **Host enters fog wall = phantom returns** | 🟡 (mismo que D7) | Mismo hook que el "phantoms home no reward" — mismo NOP cubre ambos. |

---

## F. Item drops / inter-player trading

| Limitación | Status | Notas |
|---|---|---|
| **No drops phantom ↔ host** | ✅ Bonfire bypass | Si ambos corren Bonfire, la SHM peer infrastructure permite drops via Bonfire bridge (no via vanilla packet). Ya funciona. |
| **Anti-cheat detection en server vanilla** | ⚠ N/A | Bonfire no usa server FromSoft, no nos importa. |

---

## G. Sign management

| Limitación | Status | Notas |
|---|---|---|
| **Sign desaparece al salir del área** | ✅ Bonfire bypass | No usamos signs reales — queue-write injection sin sign. |
| **Sign desaparece al entrar fog wall del host** | ✅ Idem |
| **Solo 1 sign activo por jugador** | ✅ Idem |
| **Sign requiere human form para colocar** | ✅ Idem |

---

## H. Caps de phantoms simultáneos

| Limitación | Status | Notas |
|---|---|---|
| **Cap 4 phantoms** (cooperative, vanilla) | 🟢 Trivial via NOP-patch | Ya documentado en `TRACK_C_PHASE_2B2_DESIGN.md` Phase 2B.2E. El check `cmp byte ptr [ax+301], 6` está dentro de FUN_1401A0DC0 y FUN_1403572E0. NOP-patch ambos → 6+ phantoms posibles. ~1h. |
| **Slot pool físico = 6** | 🟡 Pistas | El slot pool tiene 6 slots pre-asignados (slots 0-5). Para exceder físicamente: extender el slot_mgr array. Más complejo, no necesario para nuestro caso single-brother. |

---

## I. Comparación con saponita pequeña (para tu visión "ambos human sin limites")

Si usás saponita **grande** para que tu hermano te invoque o vos a él:

| Aspecto | Default vanilla | Con Bonfire bypasses completos (v2.9.20 hipotético) |
|---|---|---|
| Apariencia phantom | dorado/translúcido | **human normal** ✓ |
| Estus | heal slow | **heal full** ✓ |
| Items / chests | bloqueado | **OK** ✓ |
| Bonfires | bloqueado | **OK** ✓ |
| Damage scaling | ~80% | **100%** ✓ |
| Timer | 4000s decrementing | **infinito** ✓ |
| Boss kill = end | sí | **no, sesión continúa** ✓ |
| Fog walls de área | restringen movimiento | **no, libre** ✓ |
| Phantom dies = home | sí | **no, sin penalty** ✓ |
| Host dies = home | sí | **no** ✓ |

Resultado deseado: **co-op libre sin limitaciones** con la saponita
grande. **Posible con AMBOS corriendo Bonfire y los bypasses activos.**

---

## Roadmap específico para saponita grande

Priorizado por value/esfuerzo:

1. **🥇 Encontrar timer offset (4000s/6000s)** — necesita sesión live
   con phantom grande activo. ~30min. **UNLOCK**: timer freeze/extend
   commands.

2. **🥈 Bypass del flag `is_phantom`** — un solo NOP desbloquea
   apariencia + estus + items + bonfires + damage simultáneamente.
   ~2-4h.

3. **🥉 NOP del boss-kill-cleanup trigger** — exclusivo de saponita
   grande (la pequeña no tiene esta penalty). Permite continuar
   co-op post-boss. ~1h.

4. **NOP fog-wall spawn** — libre movimiento. ~4-6h RE + 1h código.

5. **Cap 4 → ilimitado** — el `cmp 6` NOP patch. ~1h.

6. **Bundle "Bonfire freeform co-op"** — UI toggle único que activa
   todo. ~30min de wiring después de tener todo lo anterior.

---

## Referencias

- `Docs/SAPONITA_GRANDE.md` — referencia técnica completa
- `Docs/SAPONITA_LIMITATIONS.md` — catálogo general (compartido con
  la pequeña)
- `Docs/SAPONITA_PEQUENA_BYPASSES.md` — companion para la pequeña
- [Fextralife: White Sign Soapstone](https://darksouls2.wiki.fextralife.com/White+Sign+Soapstone)
- [Fextralife: Cooperative Gameplay](https://darksouls2.wiki.fextralife.com/Co-op)
