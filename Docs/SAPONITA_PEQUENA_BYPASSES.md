# Saponita Pequeña — Tabla de bypass status

Por cada limitación vanilla de la **Small White Sign Soapstone**
(saponita pequeña): qué hace, dónde vive en el engine, y si tenemos
bypass implementado, parcial, o pendiente.

Companion a `Docs/SAPONITA_PEQUENA.md` (referencia técnica completa).

Última actualización: 2026-05-18. Esta tabla refleja TODO lo
verificado live (sesión CE 2026-05-17 con hermano) + research wiki.

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
| **Soul Level matching** | ✅ Rota | Igual que SM. |
| **Sign tier system** | ✅ Rota | Bonfire no coloca signos reales. |
| **Name-engraved Ring same-god requirement** | ✅ Rota | Si ambos clientes son Bonfire-coop, son partners automáticamente. |
| **SM range "más amplio hacia abajo"** (característica de la pequeña) | ✅ Bypass total | Bonfire ignora SM completamente. |

---

## B. Restricciones de área

| Limitación | Status | Notas |
|---|---|---|
| **Funciona post-boss** ⚠ característica exclusiva de la pequeña | ✅ **YA es comportamiento default** | La pequeña NO tiene esta restricción incluso vanilla. No requiere bypass. |
| **Majula no permite saponita** | 🟢 Trivial via queue-write | Phase 4d bypassa el sign-placement check. |
| **Ordeal's End / DLC arenas** | 🟢 Trivial via queue-write | Idem. |

---

## C. Phantom appearance & abilities (igual que grande, mismo flag is_phantom)

| Limitación | Status | Notas |
|---|---|---|
| **Shader shade más translúcido que phantom blanco** | 🟡 Pistas | Misma estrategia que grande pero el shader-selector aplica una variante "shade" (más alpha bajo). NOP el material-selector. ~2-4h RE. |
| **Phantom darkens progresivamente con el timer** ⚠ EXCLUSIVO de pequeña | 🟡 Pistas | Hay un mapping `timer / max_timer → alpha_scale` en el render path. NOP el lookup → alpha constante = sin darkening. ~1-2h RE. **NOTA**: NO existe en la grande. |
| **Estus heal más lento** | 🟡 Pistas | EstusUseFn lee `is_phantom`. Mismo NOP que la grande. ~1-2h. |
| **Estus BLOQUEADO si hay phantom rojo activo** ⚠ EXCLUSIVO de pequeña | 🟡 Pistas | EstusUseFn lee TANTO `is_phantom` (=shade) Y `red_phantom_in_world`. Una de dos: NOP el is_phantom check (resuelve también el blockeo), OR NOP el red-phantom-presence check. ~1h. |
| **No items / chests / levers** | 🟡 Pistas | InteractionGate is_phantom. Mismo NOP. ~2-3h (múltiples sites, todos derivados del MISMO flag). |
| **No bonfires** | 🟡 Pistas | BonfireUseFn check is_phantom. NOP. ~1h. |
| **Damage scaling ~80%** | 🟡 Pistas | DamageCalc multiplier on is_phantom. NOP. ~1h. |
| **No NPC interaction** | 🟡 Pistas | NPCDialogFn check. NOP. ~1h. |
| **No Item Box / Storage** | 🟡 Pistas | StorageOpenFn check. NOP. ~30min. |

**KEY**: forzar `is_phantom = false` en char_data desbloquea casi todo
de C en un solo write. Misma estrategia que con la grande.

---

## D. Session limits

| Limitación | Status | Notas |
|---|---|---|
| **Timer 500s (8m20s)** | ✅ **MAPPED + verified writable** | `phantom_mgr + 0x218` (float). Engine zero validation. Pendiente: command bus action `saponita_pequena.timer.freeze` en v2.9.16. |
| **Timer 750s con Name-engraved Ring** | ✅ MAPPED | Mismo field `+0x218`, max es 750 si NER equipado. **Engine valida internamente** el equip — nosotros escribimos cualquier valor. |
| **MAX constant 500.0f en +0x230** | ✅ MAPPED + writable | Cambiar este field afecta la próxima invocación. Útil para que NEW summons inicien con valor custom. |
| **Each kill reduces timer** | 🟢 Pistas | El call al "subtract kill weight" se hace en enemy-death event. Same hook que para la grande. ~1h. |
| **Cracked red orb invader kill = reduction significativa** | 🟢 Mismo hook | Misma función, distinto multiplier según tipo de víctima. NOP el subtract entirely. |
| **Phantom rojo summon kill NO reduce timer** | ✅ Comportamiento ya correcto | No requiere fix — es excepción positiva del vanilla. |
| **Boss kill = session ends immediately** | 🟡 Pistas | Boss-death event llama a phantom-cleanup. NOP. ~1h. (Mismo trigger que la grande.) |
| **Phantom dies → home** | 🟡 Pistas | State machine case 2 on HP=0. NOP transition O write HP=max loop. ~2h. |
| **Host dies → all shades home** | 🟡 Pistas | Host-death broadcast. NOP. ~2h. |
| **Host enters fog wall → shade home (sin reward)** | 🟡 Pistas | Fog-wall traversal check. NOP. ~1h. |
| **Homeward Bone use → return** | 🟢 Voluntario | No requiere fix automático. Si quisiéramos bloquearlo: NOP. |

---

## E. Fog walls

| Limitación | Status | Notas |
|---|---|---|
| **Muros de niebla en bordes de área (shade locked al área)** | 🟡 Pistas | Mismo mecanismo que con la grande — fog-wall spawn function al detectar co_op_count > 0. NOP. ~4-6h RE + 1h código. |

---

## F. Item drops / inter-player trading

| Limitación | Status | Notas |
|---|---|---|
| **No drops shade ↔ host** | ✅ Bonfire bypass | SHM peer infrastructure permite drops Bonfire-side. Ya funciona. |

---

## G. Sign management

| Limitación | Status | Notas |
|---|---|---|
| **Sign desaparece al salir del área** | ✅ Bonfire bypass | No usamos signs reales. |
| **Sign desaparece al entrar fog wall del host** | ✅ Idem |
| **Solo 1 sign activo por jugador** | ✅ Idem |
| **Sign requiere human form para colocar** | ✅ Idem |
| **Sign NO desaparece al matar boss del área** ⚠ EXCLUSIVO pequeña | ✅ Comportamiento ya correcto | Es excepción positiva — más permisivo. |

---

## H. Caps de phantoms simultáneos

| Limitación | Status | Notas |
|---|---|---|
| **Cap 4 phantoms (cooperative)** | 🟢 Trivial via NOP-patch | Mismo cap que la grande — share check `cmp byte ptr [ax+301], 6`. ~1h. |
| **Slot pool físico = 6** | 🟡 Pistas | Mismo slot_mgr para ambas saponitas. Para >6 simultáneos: extender pool. No necesario para single-brother. |

---

## I. Diferencias visualeS del shade que se podrían replicar/bypasear

La pequeña tiene un comportamiento visual único: **darkening
progresivo a 2min y 1min restantes**. Esto puede:

- ✅ **Mantenerse** como feedback visual de "tiempo restante"
- 🟡 **Bypasearse** completamente — write `phantom_mgr + 0x218` cada
  N frames a 500.0 nunca activa el darkening trigger (porque el
  visual depende del timer value).

Si vas a bypasear el timer entirely (freeze 500.0 loop), el
darkening **NUNCA se activa** automáticamente — porque depende del
valor numérico. Eso es bono.

---

## J. Para tu visión "ambos human sin limites con saponita pequeña"

Si usás saponita **pequeña** específicamente (porque te interesa
poder summon en áreas con boss muerto):

| Aspecto | Default vanilla | Con Bonfire bypasses completos |
|---|---|---|
| Apariencia shade | dim/translúcido (con darkening) | **human normal** ✓ |
| Estus | heal slow + bloqueado con rojo | **heal full siempre** ✓ |
| Items / chests | bloqueado | **OK** ✓ |
| Bonfires | bloqueado | **OK** ✓ |
| Damage scaling | ~80% | **100%** ✓ |
| Timer | 500s decrementing + darkening | **infinito sin darkening** ✓ |
| Boss kill = end | sí | **no, sesión continúa** ✓ |
| Funciona post-boss | **sí ya** | sí (no cambia) ✓ |
| Fog walls de área | restringen movimiento | **no, libre** ✓ |
| Shade dies = home | sí | **no, sin penalty** ✓ |
| Host dies = home | sí | **no** ✓ |

**Resultado posible: co-op libre post-boss sin limitaciones** con la
saponita pequeña, AMBOS corriendo Bonfire con todos los bypasses.

---

## Roadmap específico para saponita pequeña

Priorizado por value/esfuerzo (algunos pasos compartidos con la grande):

1. **🥇 Implementar timer commands en v2.9.16** — el offset ya está
   mapped. Solo falta wiring del command bus. ~1h código + build +
   test.
   - `saponita_pequena.timer.freeze`
   - `saponita_pequena.timer.kill`
   - `saponita_pequena.timer.set`
   - `saponita_pequena.timer.freeze_auto`

2. **🥈 Bypass del flag `is_phantom`** — compartido con grande. Un
   solo NOP desbloquea apariencia + estus + items + bonfires + damage
   + darkening progresivo simultáneamente. ~2-4h.

3. **🥉 NOP fog-wall spawn** — libre movimiento. Compartido con
   grande. ~4-6h RE + 1h código.

4. **NOP boss-kill-cleanup trigger** — compartido. ~1h.

5. **Cap 4 → ilimitado** — `cmp 6` NOP patch compartido. ~1h.

6. **Bundle "Bonfire freeform co-op"** — UI toggle único. ~30min.

---

## Referencias

- `Docs/SAPONITA_PEQUENA.md` — referencia técnica completa
- `Docs/SAPONITA_GRANDE_BYPASSES.md` — companion para la grande
- `Docs/SAPONITA_LIMITATIONS.md` — catálogo general (compartido)
- [Fextralife: Small White Sign Soapstone](https://darksouls2.wiki.fextralife.com/Small+White+Sign+Soapstone)
- [Wikidot: Small White Sign Soapstone](http://darksouls2.wikidot.com/small-white-sign-soapstone)
