# Saponita Desbloqueada — Pipeline de 8 pasos (status + research plan)

**Última actualización:** 2026-05-19 04:14 LOCAL (08:14 UTC)
**Branch:** `experiment/saponita-desbloqueada-dev`
**Versión activa:** v2.9.26-experimental
**Test válido más reciente:** session UUID `06088692-...`, host PID 24592, peer PID 19024

---

## Resumen ejecutivo

El flujo completo "uso item → hermano aparece como personaje real en mi mundo" se
descompone en 8 pasos. **Los primeros 6 funcionan end-to-end** después del fix
v2.9.26. **Faltan los pasos 7 y 8.** Lo bonito: la **saponita pequeña vanilla
(62040000) ya hace los pasos 7+8 perfectamente** porque DS2 implementó eso desde
día 1. Estrategia: extraer la receta de los pasos 7+8 desde la pequeña y
aplicarla a la Desbloqueada sin tocar el cliente de DS2 ni server-side beyond
DS3OS.

---

## Tabla maestra de estado

| # | Paso | Status | Evidencia / mecanismo |
|---|---|---|---|
| 1 | **Trigger de session.create cuando se usa el item** | ✅ v2.9.26 | `bonfire.custom_item_action` fire @08:12:05Z desde `InventorySelectedActionExecuteHook`, gated por `action_execute_caller=true` + `!pending_bonfire_action`. |
| 2 | **Emisión del LAN beacon `saponita_direct`** | ✅ | `StartHostBroadcast: session=7faa2aa821fb43e0a8ad7ab5a80b8e9c endpoint=192.168.68.54:50031` en `lan_beacon.debug.log`. Implementado por `Ds2LanBeacon.StartHostBroadcast` (BonfireService). |
| 3 | **Auto-accept del peer (sin prompt visible)** | ✅ Codex v2.9.25 | `TryAutoAcceptLanInviteForRuntime` (Ds2NativeSessionCoordinator.cs:869) aplica `invite.accepted` action directamente. Log del hermano: `Manually added peer: 192.168.68.54:50031` @08:12:05.39Z. |
| 4 | **Relaunch del DS2 del peer apuntando al server privado** | ✅ Codex v2.9.22 | `ScheduleDirectInviteRelaunch` (Ds2NativeSessionCoordinator.cs:1064) cierra DS2 del peer, fetcha public key del host vía `MasterServer.GetPublicKeyAsync`, relanza DS2 con host server args. Confirmado por switch de session log a `..._19024.events.jsonl`. |
| 5 | **Pose+chardata bidireccional via UDP/SHM** | ✅ | `Ds2NativePoseBridge` arranca host-side (`TryStartPoseBridgeAsHost`) y guest-side (`TryStartPoseBridgeAsGuest`). En 45s post-relaunch: 476 broadcasts, 469 received, 5 chardata snapshots intercambiados. |
| 6 | **Render del peer en el mundo del host (overlay HKMP)** | ✅ Phase 4d | `DS2_RenderHook` consume el SHM pose table y dibuja peer cube/quad geometry via D3D11. 2966× `render.set_peer_poses` aplicados en sesión 24592. Esto es lo que el usuario percibe como "casi convocado en mi mundo" — es el cubo overlay, no un PlayerCtrl real. |
| 7 | **Phantom spawn ENGINE-real (PlayerCtrl en host world)** | ❌ **PENDIENTE** | Cero `phantom_spawner.observed`, cero `player_ctrl.ctor_observed` post-saponita. El cubo overlay simula visualmente, pero el engine de DS2 no ve al peer como un phantom legítimo (no se puede atacar, no genera colisión, no participa en combate). |
| 8 | **DS2 del peer estable durante toda la sesión** | ❌ **PENDIENTE** | Su DS2 se sale después de ~45s+ de post-relaunch. Hipótesis activas: public-key mismatch con private server, anti-cheat kick, o vanilla DS2 cerrando sesión porque no encuentra "mundo válido al que unirse" sin un saponita-sign concreto. |

---

## Lo que sabemos de la saponita pequeña vanilla (que necesitamos replicar)

**La saponita pequeña 62040000 ya completa los pasos 7+8 perfectamente** cuando los
dos jugadores están en el mismo server privado. Vanilla DS2 implementó toda la
secuencia hace una década y nuestro DS3OS-derived server la soporta server-side.

### Lo que YA está mapeado de saponita pequeña (ver `SAPONITA_PEQUENA.md`)

- **Timer**: `phantom_mgr + 0x218` (float), MAX `phantom_mgr + 0x230` = 500.0f.
  Nuestra Saponita Desbloqueada **ya** lo freeza a 99999.0f (paso compartido).
- **Phantom count mirror**: `phantom_mgr + 0x010` (u32).
- **Spawn queue**: `phantom_mgr + 0x5C0`, 8 entries × 0x640 bytes.
- **PlayerCtrl ctor pipeline**:
  - `FUN_14051DBB0` dispatcher (per-tick)
  - → `FUN_14051CE20` (queue consumer)
  - → `FUN_1403572A0` / `FUN_1403572E0`
  - → `FUN_14037EBE0` PlayerCtrl ctor
- **Item params**: UseAnim=850, UseID=2410, Effect=62040000.

### Lo que falta extraer (para pasos 7+8)

| Dato | Por qué importa | Cómo extraerlo |
|---|---|---|
| **Server-side: protocolo de "place sign" / "request summon"** | Para saber qué mensaje envía DS2 al server cuando plantas saponita | Capturar tráfico TCP entre DS2-peer y nuestro Server.exe durante sesión vanilla saponita pequeña exitosa. El server tiene `SummonSign` struct + `BeingSummonedByPlayerId`. |
| **Cliente: estado del peer al recibir "te están convocando"** | Para saber qué estado DS2 espera/setea antes de spawnear el PlayerCtrl | CE/log: leer `phantom_mgr` queue entry del HOST justo después de que peer plante saponita pequeña. Cuál de los 8 slots se ocupa, qué fields se setean. |
| **Cliente: secuencia para que el HOST acepte el sign** | Para saber qué prompt/animation/función del engine corre cuando el host activa una saponita en el suelo | RE de la función de "interact con summon sign". Probable RVA conocido o derivable. |
| **Auth/connection sequence completo (peer→server)** | Para no crashear DS2 del peer en paso 8 | Comparar logs server-side entre conexión vanilla saponita (que SÍ se mantiene) vs conexión Saponita Desbloqueada post-relaunch (que se cae). |

---

## Plan de research (orden recomendado)

### Etapa A — Reproducir saponita pequeña vanilla con full instrumentación

Objetivo: tener una grabación end-to-end de qué hace DS2 + Server cuando un
saponita pequeña vanilla resulta en un summon exitoso entre los dos jugadores.

1. Reiniciar ambos clientes en v2.9.26 (mismo build que el test exitoso de hoy).
2. Player B planta saponita pequeña (62040000) vanilla en el mundo.
3. Player A (host) activa el sign visible en su mundo.
4. **Capturar simultáneamente:**
   - Eventos JSONL de los DOS clientes
   - `pose_bridge.debug.log` de los DOS clientes
   - **Server output stdout** completo (filtra por player ids)
   - **Tráfico TCP capturado** entre cliente A ↔ server y cliente B ↔ server
     (puerto 50050, frpg2 protocol — probablemente protobuf cifrado pero
     ya tenemos el AES key en BonfireService)
5. Confirmar que phantom_spawner.observed + player_ctrl.ctor_observed disparan
   en el host con arg4=1 (phantom mode).

### Etapa B — Mapear el mensaje server-side de "summon request"

Objetivo: identificar exactamente qué mensaje envía el server al host del peer
cuando un summon sign vanilla se activa.

1. En el tráfico capturado en Etapa A, identificar el packet que viaja del
   server al host después de que A activa el sign.
2. Buscar en `Source/Server/Server/...` la implementación que despacha ese
   packet (probable: `Server.cpp` o un message handler dentro de
   `GameService/`).
3. Replicar la misma secuencia desde el server pero disparada por nuestro
   evento `session.create` (en vez de "place sign + match").

### Etapa C — Modificar Saponita Desbloqueada para usar la misma ruta

Dos opciones (A más conservadora, B más profunda):

**Opción A — Server-driven (preferida, no toca cliente)**:
1. Cuando BonfireService recibe `session.create` del host:
   - **Si el peer ya está conectado al mismo private server**: enviar al server
     un comando admin/extension que cree una `SummonSign` virtual con
     `OwnerPlayerId=peer`, `BeingSummonedByPlayerId=host`. El server entonces
     dispara la misma secuencia que un sign-activate vanilla.
2. Eliminar `ScheduleDirectInviteRelaunch` cuando el peer ya está conectado.
3. Si el peer no está conectado, fallback al relaunch actual.

**Opción B — Client-driven (más invasivo)**:
1. Replicar el queue-write en `phantom_mgr+0x5C0` con TODOS los campos
   correctamente populados desde chardata SHM (no solo pos/level/HP como hizo
   el v1 fallido en v2.9.16). Requiere:
   - Mapear los ~496 bytes intermedios entre `+0x80` (type) y `+0x270` (level)
     que actualmente quedan en cero (probablemente equip/spell ids, animation
     init state, etc.)
   - Leer chardata del peer via Ds2PoseShm + serializar al formato exacto del
     queue entry vanilla

### Etapa D — Resolver paso 8 (estabilidad DS2 del peer)

Hipótesis a verificar EN ORDEN:

1. **Public-key mismatch**: el `MasterServer.GetPublicKeyAsync` fetchea la key
   correcta? Logear el value en BonfireService antes del relaunch.
2. **Server auth rechaza al guest**: capturar el handshake TCP post-relaunch
   entre peer y server; ¿hay un `AuthService` reject?
3. **DS2 anti-cheat detecta el relaunch como suspicious**: la flag de
   "private server" en `modengine.ini` está bien? Comparar config entre la
   primera conexión exitosa (sin saponita) y la post-relaunch.
4. **Vanilla DS2 cierra sesión porque "no hay mundo válido"**: si DS2 conecta
   al server pero el server no le envía sign info ni summon request, DS2
   puede terminar la sesión sola por timeout. Esto se resuelve si Etapa C
   Opción A se completa (server envía summon request inmediatamente).

---

## Archivos clave para inspección

- **Injector hooks**: `Source/Injector/Hooks/DarkSouls2/DS2_NativeRuntimeHook.cpp`
- **BonfireService coordinator**: `Source/BonfireService/Modules/Ds2NativeSessionCoordinator.cs`
- **LAN beacon**: `Source/BonfireService/Modules/Ds2LanBeacon.cs`
- **Pose bridge**: `Source/BonfireService/Modules/Ds2NativePoseBridge.cs` (asumido)
- **Server core**: `Source/Server/Server/Server.cpp`, `Source/Server/Server/GameService/GameService.cpp`
- **Server config**: `Source/Server/Config/RuntimeConfig.h` (campos `SummonSign*`, `DisableCoopAutoSummon`)
- **Runtime JSONL (host)**: `C:/DSSeamlessCoop/Runtime/DS2Native/<sid>_<pid>.events.jsonl`
- **Saponita peq research previa**: `Docs/SAPONITA_PEQUENA.md`, `Docs/CODE_ANALYSIS_SAPONITA_VS_ITEMS.md`

---

## Próximos commits sugeridos (orden)

1. **`v2.9.27: Etapa A — instrument vanilla saponita pequena flow`**:
   añadir hooks adicionales para capturar phantom_spawner + player_ctrl.ctor
   events con full payload cuando item_id==62040000 (saponita peq vanilla).
2. **`v2.9.28: Etapa B — server-side summon-request RPC handler`**:
   añadir endpoint admin al server que acepte `summon(peer_player_id,
   host_player_id)` y dispare la secuencia vanilla server-side.
3. **`v2.9.29: Etapa C — saponita_desbloqueada usa server RPC + skip relaunch`**:
   cuando ambos peers ya están conectados al mismo private server,
   BonfireService llama el RPC y omite el `ScheduleDirectInviteRelaunch`.

Cada commit con cuerpo Observed / Root cause / Fix / Validation / Operator note
(formato Codex).

---

## Log de avances (cronológico)

| Fecha | Versión | Cambio | Resultado |
|---|---|---|---|
| 2026-05-17 | v2.9.16 | SaponitaDesbloqueada_Trigger v1 (queue-write placeholder) | Crash/slowdown — datos incompletos |
| 2026-05-17 | v2.9.17 | tabla colapsada a 1 item, cubo overlay off | Estable, pero sin convocación |
| 2026-05-18 | v2.9.18 | FMG rename "Saponita Desbloqueada" + descripción | Texto correcto in-game |
| 2026-05-18 | v2.9.19 | ItemParam Icon ID → 62045000 (rojo) | Icono OK |
| 2026-05-18 | v2.9.20 | memory.peek.phantom_mgr diagnostic (revertido) | — |
| 2026-05-18 | v2.9.21 | pivot a saponita peq vanilla (revertido) | — |
| 2026-05-18 | v2.9.22 | Codex: LAN beacon + relaunch flow + native UI | "te están convocando" loading screen apareció en peer |
| 2026-05-19 | v2.9.23-24 | Codex: native confirmation prompt UTF-16 | Prompt nativo correcto |
| 2026-05-19 | v2.9.25 | Codex: auto-accept silent + active-session guards | Trigger broken (regresion) |
| **2026-05-19** | **v2.9.26** | **Restaurar trigger desde action_execute path** | **✅ Pasos 1-6 working end-to-end** |
| TBD | v2.9.27+ | Etapas A → C → D | Pasos 7+8 |
