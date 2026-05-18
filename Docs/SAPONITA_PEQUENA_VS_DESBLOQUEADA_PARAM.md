# Saponita Pequeña vs Saponita Desbloqueada — comparativa PARAM completa

**Versión analizada:** v2.9.19 (post-icon patch)
**Generado:** sesión live de testing, sign placed in-world

## 1. ItemParam (84 bytes/row)

Diff fila 62040000 (Small WSS) vs 62061000 (Saponita Desbloq):

| Offset | Field | Saponita Pequeña | Saponita Desbloq | ¿Importa? |
|---|---|---|---|---|
| 0x00 | Icon ID | 62040000 | **62045000** | ✅ Post-v2.9.19 → ícono rojo |
| 0x04 | Effect ID | 62040000 | 62061000 | ⚠️ Custom (puede afectar VFX de uso) |
| 0x08 | Unk08 | 1103 | 0 | ⚠️ Desconocido, valor distinto |
| 0x10 | Unk10 | 0 | 1132 | ⚠️ Desconocido, valor distinto |
| 0x2C | Sort ID | 1001 | 1000 | ❌ Cosmético (posición en inventario) |
| 0x40 | **Item Use Animation** | **850** | **1700** | ⚠️ Anim al usar: saponita-place vs custom |
| 0x44 | **Item Usage ID** | **2410** | **62061000** | ⚠️ Lógica de uso: saponita-summon vs custom |
| 0x4A | Max Held | 1 | 1 | ✅ Iguales |
| 0x4D | Consumable flag | 2 (Non-Consumable) | 2 | ✅ Iguales — NO se gasta del inventario |
| 0x4F | Item Type | 8 (Good) | 8 | ✅ Iguales |
| 0x52 | Item State | 13 (Estus-class) | 13 | ✅ Iguales — sobrevive NG+ |

**Coincidencias clave** (lo que hace que el engine lo trate como item online válido):
- ItemType = 8 (Good/consumable category)
- Consumable flag = 2 (Non-Consumable — el item no se consume del inventario, idéntico a saponita)
- Item State = 13 (clase Estus, persiste entre NG+)
- Max Held = 1

**Divergencias por resolver** (si querés behavior 1:1 con saponita):
- UseAnim 1700 → **850** (pondría la animación de "plantar saponita" al usarla)
- UseID 62061000 → **2410** (haría que el engine ejecute la lógica de saponita-summon — pero esto COLISIONARÍA con nuestro hook custom, ver §3)
- Unk08/Unk10/Effect — origen desconocido, no esencial

## 2. ItemUsageParam (8 bytes/row, 64 rows totales)

| Row | Item | Unk00 | u04 | u05 | u06 | u07 |
|---|---|---|---|---|---|---|
| **2410** | Saponita Pequeña + Roja | -1 | 7 | **24** | **0** | 0 |
| **62061000** | Saponita Desbloqueada | -1 | 7 | **180** | **1** | 0 |
| 62061001-008 | (slots custom legacy) | -1 | 7 | 180 | 1 | 0 |
| 2550 | (referencia con 180,1) | -1 | 7 | 180 | 1 | 0 |
| 2630 | (referencia con 180,1) | -1 | 7 | 180 | 1 | 0 |

**Hallazgo:** la fila custom 62061000 fue clonada desde un template **180,1** (probablemente 2550 o 2630), no desde la fila 2410 de saponita. Las flags u05/u06 son distintas. Sin docs del campo no sé qué controla cada bit, pero la observación es que **no es una clonación exacta de saponita**.

u04=7 sí coincide (categoría "consumable" general). Solo row 2120 (Eye Orb) tiene u04=1, así que estamos en la categoría correcta.

## 3. Flujo de "USE" actual — por qué el hook custom funciona aún con UseID distinto

Cuando el player USA el item del inventario:

1. **DS2 engine** lee `ItemParam.row[62061000].ItemUsageID` = 62061000
2. Llama el vtable slot `Inventory::UseItem` (slot offset 0x38)
3. **Nuestro hook** `InventoryUseItemHook` (en [DS2_NativeRuntimeHook.cpp:2563](Source/Injector/Hooks/DarkSouls2/DS2_NativeRuntimeHook.cpp:2563)) intercepta y *siempre llama el original*
4. **Antes**, en el flujo de selección, `InventorySelectedItemCategoryHook` ya detectó `ItemId == 62061000` y llamó `HandleBonfireRuntimeItemUse`
5. `HandleBonfireRuntimeItemUse` con `command = "session.create"` invoca [`SaponitaDesbloqueada_Trigger`](Source/Injector/Hooks/DarkSouls2/DS2_NativeRuntimeHook.cpp:657)
6. `SaponitaDesbloqueada_Trigger` escribe entry en queue `phantom_mgr+0x5C0` → engine spawnea phantom

Como UseID = 62061000 (custom) y no hay row 62061000 en ItemUsageParam con lógica vanilla pesada, el original `Inventory::UseItem` probablemente no hace nada visible. Eso es **lo que queremos**: el hook spawnea, vanilla no interfiere.

**Si cambiáramos UseID a 2410** (saponita vanilla):
- ✅ El engine reproduciría la animación + comportamiento real de saponita-pequeña-place-sign
- ❌ Pero también abriría el menú de signs vanilla, o intentaría conectar al server de matchmaking vanilla, lo cual podría:
  - Pedir conexión a Online Network (servers cerrados desde 2025 — fallaría)
  - Cancelar el uso del item si no hay conexión
  - Conflictar con nuestro spawn-queue write

**Recomendación:** dejar UseID = 62061000 (como está). Cambiar solo UseAnim 1700 → 850 si querés que el player haga el gesto visual de "plantar saponita" al usar el item.

## 4. Lo que NO está clonado todavía (subsistemas a revisar)

| Subsistema | Saponita Pequeña | Saponita Desbloq | Notas |
|---|---|---|---|
| ChrMultiplayParam | row 2 (Small WSS slot) | ?  | Define cantidad de phantoms permitidos por saponita |
| OnlineEventParam | activa al place-sign | no | Trigger del network sync |
| PlayerCommonParam | cooldown + limits | ? | Anti-spam + zona limits |
| Timer phantom_mgr+0x218 | 500.0f decrementa | 99999.0f (freezeado por Injector) | ✅ Ya manejado |
| Queue phantom_mgr+0x5C0 | write at sign-activation | write at item-use | ✅ Funciona via SaponitaDesbloqueada_Trigger |

Estos son los siguientes a investigar **live con CE** mientras el sign está plantado.

## 5. Próximos pasos para v2.9.20+

**Cambios de bajo riesgo (cosméticos pero importantes):**
- [ ] `ItemParam[62061000].ItemUseAnimation` 1700 → **850** (gesto de saponita al usar)
- [ ] `ItemUsageParam[62061000].u05` 180 → **24** (clone exact de saponita row 2410)
- [ ] `ItemUsageParam[62061000].u06` 1 → **0** (idem)

**Hallazgos esperados con CE (sign placed, pre-summon):**
- `phantom_mgr+0x218` debería estar decrementando lentamente (timer de saponita activo)
- `phantom_mgr+0x5C0` queue debería estar vacía (nadie ha invocado todavía)
- Buscar estructura del "sign placement" — ubicación + relación host
- Buscar `host_is_summon_candidate` flag (¿algún byte cerca del PlayerCtrl?)

**Hallazgos esperados con CE (hermano summoneando — durante el spawn):**
- queue entry aparece en +0x5C0 con datos del peer
- `phantom_count` incrementa
- nuevo PlayerCtrl en slot pool (slot_mgr+0x5D0+N*0xA90)

Documentar todo en `Docs/SAPONITA_LIVE_STATE_DUMP.md` cuando CE bridge esté cargado.
