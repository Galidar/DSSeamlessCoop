# Saponita Peq Nativa — DEEP RE de DS2 SOTFS

**Fecha:** 2026-05-19  
**Binary:** `C:\Program Files (x86)\Steam\steamapps\common\Dark Souls II Scholar of the First Sin\Game\DarkSoulsII.exe`  
**Size:** 28,200,992 bytes  
**Tool:** Ghidra 12.0.4 headless decompile (en `Docs/ghidra-out/DarkSoulsII-out/`)

## Resumen Ejecutivo

Investigación exhaustiva del flujo nativo de **saponita pequeña vanilla** en DS2 SOTFS, motivada por necesidad de entender por qué nuestro `PushRequestSummonSign` sintético (v2.9.27–31) hace que el cliente del peer cierre la conexión 7 segundos después del push.

**Hallazgo crítico:** los campos protobuf `player_struct` en `RequestCreateSign` vs `RequestSummonSign` son **estructuras binarias DISTINTAS** (`NetSvrSummonSignAppData` vs `NetSvrSummonSignSessionAppData`). Nuestro código server-side guarda `AppData` (al crear el sign) y lo envía como si fuera `SessionAppData` en el push → mismatch → cliente del peer rechaza el push.

---

## 1. Arquitectura cliente-side

### 1.1. Clases principales (todas confirmadas en el binario)

| Clase | Función |
|---|---|
| `NetSvrSummonSignInterface` | Fachada pública (cliente llama estos métodos) |
| `NetSvrSummonSignManager` | Manager singleton |
| `NetSvrSummonSignPushNotifyBuffer` | Buffer para pushes server→client |
| `NetSvrSummonSignSummonJob` | Job que ejecuta el summon recibido |
| `NetSvrCreateSummonSignJob` | Job para enviar RequestCreateSign |
| `NetSvrGetSummonSignListJob` | Job para enviar RequestGetSignList |
| `NetSvrRejectSummonSignJob` | Job para enviar RequestRejectSign |
| `NetSvrRemoveSummonSignJob` | Job para enviar RequestRemoveSign |
| `NetSvrUpdateSummonSignJob` | Job para enviar RequestUpdateSign |
| `NetSummonSlotCtrl` | Maneja un slot phantom individual |
| `NetSummonSlotAreaManager` | Lifecycle area-scoped |
| `NetSummonAcceptMultiplayCtrl` | Controla "te aceptan invocar" |
| `NetSummonJoinMultiplayCtrl` | Controla "te uniste como guest" |
| `NetSummonPacketCtrl` | Frame packets summon-specific |
| `ActiveSignManager` | Tracking de signs activos |
| `FeSceneSummonSignWindow` | UI nativa "te están convocando?" |
| `Frpg2SignImpl` | Implementación protocolo Frpg2 sign |
| `Frpg2MirrorKnightSignImpl` | Variante Mirror Knight |

### 1.2. NetSvrSummonSignInterface — métodos públicos

```cpp
class INetSvrSummonSignInterface {
    virtual INetSvrJob* CreateSummonSign(
        unsigned int area_id,
        const Frpg2Sv::CellAddress& cell,
        const Frpg2Sv::MatchingParameter& match_param,
        Frpg2Sv::Frpg2SignType sign_type,
        const NetSvrSummonSignAppData& app_data,        // ← STORED in sign
        unsigned int* out_sign_id
    );

    virtual INetSvrJob* SummonSummonSign(
        unsigned int area_id,
        const Frpg2Sv::CellAddress& cell,
        Frpg2Sv::SignInfo sign_info,
        const NetSvrSummonSignSessionAppData& session_app_data  // ← SENT per-summon
    );

    virtual INetSvrJob* GetSummonSignList(
        unsigned int area_id,
        const Frpg2Vector<SignCellGetInfo>& cells,
        unsigned int max_signs,
        const Frpg2Sv::MatchingParameter& match_param,
        bool flag1, bool flag2,
        Frpg2Vector<SignInfo>* out_signs,
        Frpg2Vector<SignData>* out_data
    );

    virtual INetSvrJob* RejectSummonSign(...);
    virtual INetSvrJob* RemoveSummonSign(...);
    virtual INetSvrJob* UpdateSummonSign(...);
};
```

### 1.3. Frpg2Sv namespace (RPC framework de FROM SOFT)

Types confirmados:
- `Frpg2Sv::CellAddress` — struct {cell_id u64, area_id u32, ...?}
- `Frpg2Sv::SignInfo` — struct {sign_id u32, ...}
- `Frpg2Sv::Frpg2SignType` — enum (0 = WhiteSoapstone, 1 = SmallWhite?, 2 = Red, ...)
- `Frpg2Sv::Frpg2RejectCode` — enum (matches SummonErrorId server-side)
- `Frpg2Sv::MatchingParameter` — struct {sm, sl, weapon_lvl, vow, ...}
- `Frpg2Sv::Frpg2NotifyImpl` — handles incoming push notifies
- `Frpg2Sv::RPCSystem` / `RPCSystemImpl` — the RPC infrastructure

Source paths in binary:
- `N:\FRPG2_64\Source\dantelion2_steam\dist\include_utf8\dantelion2/Core/Kernel/...`

### 1.4. Estructuras AppData (las DOS distintas)

#### NetSvrSummonSignAppData
**Cuándo se envía:** con `RequestCreateSign` (al PLANTAR un sign).  
**Qué contiene:** datos del jugador para mostrar como apparition del SIGN en el mundo (HP, equipment, animation, etc.).  
**Almacenado server-side:** sí, en `Sign->PlayerStruct` (bytes opacos).

#### NetSvrSummonSignSessionAppData
**Cuándo se envía:** con `RequestSummonSign` (al ACTIVAR un sign).  
**Qué contiene:** datos del jugador para iniciar el summon session — probablemente incluye estado actual del activator (no del sign-creador).  
**Almacenado server-side:** **NO** — se pasa transparente del activador al sign-owner.  
**Format binario:** DIFERENTE de AppData (struct distinta).

**Esta es la pieza que faltaba:** nuestro server estaba mandando `AppData` cuando el peer espera `SessionAppData`.

---

## 2. RVAs Confirmados (DS2 SOTFS 1.0.32.0 Steam edition)

`exe_size`: 28,200,992 (matches `kKnownSotfsSteamExeSize`)  
Base virtual cuando staged: variable (last seen `0x7FF688C40000`)

### Functions

| Symbol | RVA | Descripción |
|---|---|---|
| `FUN_140284d10` | `0x284D10` | NetSvrJob base setup (common ctor helper) |
| `FUN_14029d6c0` | `0x29D6C0` | **`NetSvrSummonSignInterface::CreateSummonSign` factory** → returns INetSvrJob* |
| `FUN_14029d830` | `0x29D830` | **`NetSvrSummonSignInterface::SummonSummonSign` factory** → returns INetSvrJob* |
| `FUN_14028c450` | `0x28C450` | `NetSvrMirrorKnightSignInterface::CreateSummonSign` factory (Mirror Knight variant) |
| `FUN_14028c560` | `0x28C560` | `NetSvrMirrorKnightSignInterface::CreateSummonMirrorKnightSignJob` factory |
| `FUN_1402a5970` | `0x2A5970` | `NetSvrSummonSignSummonJob::ctor` — job that executes summon |
| `FUN_1402a5a20` | `0x2A5A20` | NetSvrSummonSignSummonJob method (likely execute() or callback handler) |
| `FUN_1402a4b90` | `0x2A4B90` | `NetSvrSummonSignPushNotifyBuffer::ctor` |
| `FUN_1402a4d40` | `0x2A4D40` | NetSvrSummonSignPushNotifyBuffer method (push() or process()) |
| `FUN_140833650` | `0x833650` | NetSvr generic helper (called from sign window ctor) |
| `FUN_14068d5b0` | `0x68D5B0` | NetSvrManager get_instance / allocator accessor |

### Data layout — NetSvrSummonSignPushNotifyBuffer

```
+0x00  vftable
+0x08  allocator pointer (param_2)
+0x10  u8 flag (init 0)
+0x18-0x48  some inline struct (FUN_14084b400 init)
+0x50  Vector<PushItem>{begin, end, capacity, allocator}   // 0x20 bytes
       items are 0x110 (272) bytes each ← THE PUSH QUEUE
+0x70  Vector<ptr>{begin, end, capacity, allocator}        // 0x20 bytes
       items are 8 bytes each ← probably callback pointers
+0x90  Vector<ptr>{begin, end, capacity, allocator}        // 0x20 bytes
       items are 8 bytes each ← probably another callback list
+0xB0  end
```

### Data layout — NetSvrSummonSignSummonJob

From `FUN_1402a5970` ctor:
```
+0x00  vftable (NetSvrSummonSignSummonJob::vftable)
+0x08  vftable (duplicate ptr for vftable param 1?)
+0x10-0x27  inherited NetSvrJob base (FUN_14027a930 init)
+0x28  param_2[0] (8 bytes — sign_info.sign_id maybe + extras?)
+0x30  param_2[1] (8 bytes)
+0x38  param_2[2] (1 byte) — sign type
+0x3C  param_3[0] u32 — cell area u32?
+0x40  param_3[1] u32 — cell_id low?
+0x44  param_3[2] u32 — cell_id high?
+0x48  param_3[3] u32
+0x4C  param_3[4] u32
+0x50  param_3[5] u32
+0x58  param_4 (8 bytes — SessionAppData pointer)
+0x60  param_5 (8 bytes — context)
```

---

## 3. Protocolo Frpg2 Message Types

| Hex | Name | Direction | Use |
|---|---|---|---|
| `0x0397` | RequestCreateSign | C→S | Place a sign in the world |
| `0x0398` | RequestSummonSign | C→S | Activate someone's sign (summon them) |
| `0x0399` | RequestRemoveSign | C→S | Remove your own placed sign |
| `0x039A` | RequestGetSignList | C→S | Poll for nearby signs |
| `0x039B` | **PushRequestSummonSign** | **S→C** | Notify sign owner "you are being summoned" |
| `0x039C` | PushRequestRejectSign | S→C | Notify sign owner "summon rejected" |
| `0x039D` | PushRequestRemoveSign | S→C | Notify aware players "sign removed" |
| `0x039E` | RequestRejectSign | C→S | Decline a summon |
| `0x039F` | RequestUpdateSign | C→S | Update sign state |

(Verified from `Source/Server.DarkSouls2/Server/Streams/DS2_Frpg2ReliableUdpMessageTypes.inc`)

---

## 4. Protobuf field maps (verified)

### RequestCreateSign (placement)
```protobuf
message RequestCreateSign {
    required uint32 online_area_id = 1;
    required MatchingParameter matching_parameter = 2;
    required bytes player_struct = 3;           // NetSvrSummonSignAppData serialized
    required uint32 cell_id = 4;
    required uint32 sign_type = 5;
}
```

### RequestSummonSign (activation by host)
```protobuf
message RequestSummonSign {
    required int64 online_area_id = 1;
    required SignInfo sign_info = 2;
    required bytes player_struct = 3;           // NetSvrSummonSignSessionAppData serialized
    required int64 cell_id = 4;
}
```

### PushRequestSummonSign (server → sign owner)
```protobuf
message PushRequestSummonSign {
    required PushMessageId push_message_id = 1;
    required int64 player_id = 2;               // Summoner's player_id
    required int64 sign_id = 3;                  // The sign being activated
    required bytes player_struct = 4;           // **SessionAppData** (not AppData)
    required string player_steam_id = 5;        // Summoner's steam id
}
```

**Verified:** vanilla `Handle_RequestSummonSign` copies `Request->player_struct()` (= the SessionAppData from RequestSummonSign) directly into `PushMessage.player_struct`. Server NEVER substitutes the AppData stored in `Sign->PlayerStruct`.

---

## 5. Item IDs (sign-related)

| ID | Item | Sign type used | Animation |
|---|---|---|---|
| `62020000` | White Sign Soapstone (grande) | 0 (WhiteSoapstone) | 850 (place sign) |
| `62030000` | (small variation?) | 0 (WhiteSoapstone) | 850 |
| `62040000` | **Small White Sign Soapstone (saponita peq)** | 1 (SmallWhiteSoapstone) | 850 |
| `62045000` | Red Sign Soapstone (saponita roja) | 2 (RedSoapstone) | 850 |
| `62050000` | (Bell keepers?) | ? | ? |
| `62060000` | Dragon Eye? | ? | ? |
| `62061000` | **Saponita Desbloqueada (custom Bonfire)** | — | 1700 (custom anim) |

### Item Usage IDs (ItemUsageParam refs)
- Row `2410` — saponita summon (WSS + RSS share this)
- Row `62061000` — custom row hueco (placeholder, no real logic)

---

## 6. Por qué nuestros v2.9.27–v2.9.31 fallan — ROOT CAUSE confirmado

```
Vanilla flow:
  Host activate sign:
    NetSvrSummonSignInterface::SummonSummonSign(area, cell, sign_info, SessionAppData)
                                                                       ^^^^^^^^^^^^^^
                                                                       CLIENT generates fresh
    → builds RequestSummonSign protobuf
    → MessageStream sends to server
  Server Handle_RequestSummonSign:
    → constructs PushMessage
    → PushMessage.set_player_struct(Request->player_struct() ...)   // ← copies SessionAppData
    → sends to sign owner
  Sign owner receives push:
    → NetSvrSummonSignPushNotifyBuffer queues it
    → NetSvrSummonSignSummonJob deserializes player_struct as SessionAppData ← format MATCHES
    → validates → spawns phantom
```

```
Nuestro v2.9.27–31 flow:
  Host uses Saponita Desbloqueada (no triggers SummonSummonSign on client)
  BonfireService writes admin_summon_inbox.json
  Server ProcessAdminSummonInbox:
    → constructs PushMessage
    → PushMessage.set_player_struct(SummonerSign->PlayerStruct ...) // ← AppData (from Create)
    → sends to peer
  Peer receives push:
    → NetSvrSummonSignPushNotifyBuffer queues it
    → NetSvrSummonSignSummonJob deserializes player_struct as SessionAppData
       BUT the bytes are AppData format ← MISMATCH
    → deserialization fails or produces garbage state
    → some internal validation closes message stream
    → server logs 'Connection closed... Disconnecting client as message stream closed'
    → BonfireService relaunch path fires too (until v2.9.29 fixed that) reconecta peer
```

---

## 7. Caminos viables para v2.9.32+

### Opción A: Client-side inject SummonSummonSign (RECOMENDADO)

**Cómo:** Diux's Injector intercepta el uso de Saponita Desbloqueada y llama `NetSvrSummonSignInterface::SummonSummonSign` directamente al function pointer en `FUN_14029d830`.

Argumentos:
- area_id: del world manager actual
- cell: de Wally's sign en LiveCache (server lo da o Diux's client lo conoce via GetSummonSignList)
- sign_info: { sign_id = Wally's, ... }
- SessionAppData: el cliente DS2 lo construye internamente como parte del execute() del job

**Ventaja:** ejecuta el path vanilla EXACTO. SessionAppData generada por el binary del cliente con todos los fields correctos.

**Reto:** necesitamos saber:
1. Cómo obtener el puntero a NetSvrSummonSignInterface (singleton accessor)
2. Cómo construir Frpg2Sv::CellAddress + SignInfo
3. Function signature exacta del call (calling convention, alignment)

**Pre-requisito:** Wally debe tener un sign válido cacheado server-side (mismo que ahora).

### Opción B: Server-side fake the full RequestSummonSign

**Cómo:** server simula que Diux mandó un RequestSummonSign con player_struct (= SessionAppData) construido sintéticamente.

**Reto:** SessionAppData es opaco — no sabemos cómo construirlo sin un cliente. Probable fallo.

### Opción C: Sniff SessionAppData de una activación vanilla previa

**Cómo:** la PRIMERA vez que Diux activa un sign vanilla, server cachea su SessionAppData. Saponita Desbloqueada subsiguiente reutiliza esa SessionAppData cacheada.

**Ventaja:** sin RE invasivo del cliente.  
**Reto:** SessionAppData puede contener estado time-varying (pose, HP en ese momento) que se vuelve inválido al reutilizarlo. El cliente del peer puede rechazar SessionAppData stale.

### Opción D: Aceptar el relaunch path como definitivo

Conservar el flow v2.9.27 (LAN beacon → peer auto-accept → relaunch). Funciona. Solo es "no in-place".

---

## 7-bis. PIPELINE COMPLETO recuperado vía Ghidra (sesión 2)

Reverseé el flujo end-to-end de cómo el cliente DS2 envía un summon:

### `FUN_1402a5b40` — alto nivel (1 arg `param_1` = context)

```c
void FUN_1402a5b40(longlong context)  // context = some Frpg2 manager
{
    sign_mgr = *(context + 0x60) + 0x30;     // = NetSvrSummonSignInterface
    area_id  = **(int**)(context + 0x58);    // current area
    cell     = FUN_1402a9c90(...) / FUN_1402a9f80(...);  // build CellAddress
    sign_info = build from local sign list   // 8 bytes by value
    
    char session_buf[168];                    // SessionAppData = 168 bytes
    FUN_140a3efc0(session_buf);              // ctor / init
    
    gm = *DAT_141616cf8;                     // gm global (RVA 0x1616CF8) — YA TENEMOS ESTO
    FUN_140520000(gm, session_buf);          // FILL SessionAppData from current player state
    
    FUN_14029e700(sign_mgr, area_id, &cell, sign_info, session_buf);
    //           ^^^^^^^^                              ^^^^^^^^^^^
    //           SummonSummonSign wrapper             our buffer
    
    FUN_140a3f0b0(session_buf);              // dtor
}
```

### `FUN_14029e700` — wrapper público (5 args)

```c
LONGLONG FUN_14029e700(
    self,                                    // NetSvrSummonSignInterface*
    u32 area_id,                            // param_2
    u32* cell_addr,                         // param_3 (CellAddress)
    u64 sign_info,                          // param_4 (SignInfo, by value)
    NetSvrSummonSignSessionAppData* session // param_5 (on stack arg)
);
```

Internamente:
```c
job = alloc(0x58);                           // 88 bytes for job
FUN_14029d830(job);                          // factory init (vftable etc.)
FUN_140284f80(self+0x18, job);               // enqueue in NetSvrManager
job[0x28] = area_id;                         // store area
job[0x2C] = cell;                            // store cell  
job[0x30] = sign_info;                       // store sign info
FUN_1402a6cf0(session, job + 0x38);          // COPY SessionAppData into job
return job;
```

### `FUN_140520000` — SessionAppData filler (CRÍTICO)

```c
void FUN_140520000(longlong gm, longlong buf)
{
    FUN_140a408b0(gm + 0x10, buf, 0);        // fill core fields from GM+0x10
    *(u64*)(buf + 0xA8) = *(u64*)(gm + 0x78); // copy 2 fields from GM
    *(u64*)(buf + 0xB0) = *(u64*)(gm + 0x80);
}
```

Esta función toma el GameManagerImp y el buffer de 168 bytes, y POPULA todos los campos correctos del SessionAppData. Si llamamos esto desde el Injector, **obtenemos la SessionAppData CORRECTA** para el jugador local.

### RVAs adicionales descubiertos

| Function | RVA | Purpose |
|---|---|---|
| `FUN_1402a5b40` | `0x2A5B40` | High-level summon trigger (1-arg) |
| `FUN_14029e700` | `0x29E700` | SummonSummonSign 5-arg wrapper |
| `FUN_140520000` | `0x520000` | SessionAppData filler from GM |
| `FUN_140a3efc0` | `0xA3EFC0` | SessionAppData ctor |
| `FUN_140a3f0b0` | `0xA3F0B0` | SessionAppData dtor |
| `FUN_140a408b0` | `0xA408B0` | Inner field copier (called by filler) |
| `FUN_140284f80` | `0x284F80` | NetSvrManager job enqueue |
| `FUN_140833320` | `0x833320` | Memory allocator (used by job factories) |
| `FUN_1402aa380` | `0x2AA380` | Heap accessor for SignManager allocations |
| `FUN_1402a9c90` | `0x2A9C90` | CellAddress builder |
| `FUN_1402a9f80` | `0x2A9F80` | CellAddress / SignInfo builder helper |
| `FUN_1402a6e40` | `0x2A6E40` | Area validation ("is online area") |

---

## 8. Implementación práctica de Opción A (recipe)

Con todo lo recuperado, el Injector puede emular el flujo así:

```cpp
// In DS2_NativeRuntimeHook.cpp, when Saponita Desbloqueada is used:

uintptr_t base = game_base;  // DarkSoulsII.exe base addr

// 1. Resolve gm_imp via existing kPhantomMgrHolderRva (0x1616CF8)
uintptr_t* dat_141616cf8 = (uintptr_t*)(base + 0x1616CF8);
uintptr_t gm = *dat_141616cf8;
if (!gm) return false;

// 2. Allocate 168 bytes for SessionAppData (stack OK, or heap if needed)
alignas(8) uint8_t session_app_data[168] = {0};

// 3. Init via FUN_140a3efc0(buffer)
auto init_fn = (void(*)(void*))(base + 0xA3EFC0);
init_fn(session_app_data);

// 4. Fill via FUN_140520000(gm, buffer)
auto fill_fn = (void(*)(uintptr_t, void*))(base + 0x520000);
fill_fn(gm, session_app_data);

// 5. Build CellAddress + SignInfo — REQUIRES finding layouts
//    For CellAddress: probably {u32 area_id, u32 cell_id, ...}
//    For SignInfo: 8 bytes by value — likely {u32 sign_id, u32 type+flags}
//    Both can be probed by reading what NetSvrSummonSummonSignJob stores at +0x28,+0x2C,+0x30

uintptr_t target_sign_id = <wally's sign_id from server beacon>;
uintptr_t target_area_id = 0x9A4D830;  // 10100000 dec = our test area
uint64_t sign_info = target_sign_id;   // simplest case, just the id (may need more fields)
uint32_t cell_addr[3] = { target_area_id, target_cell_id, 0 };

// 6. Get NetSvrSummonSignInterface singleton
//    UNKNOWN — need to find. Probably global via path like:
//       *(some_global) -> +0x60 -> +0x30 = SignManager
//    Or via accessor function (TBD: search for "SummonSign.*GetInstance")

uintptr_t sign_mgr = ?;  // singleton lookup

// 7. Call FUN_14029e700(sign_mgr, area_id, &cell, sign_info, session_app_data)
auto summon_fn = (uintptr_t(*)(uintptr_t, uint32_t, uint32_t*, uint64_t, void*))(base + 0x29E700);
uintptr_t job_handle = summon_fn(sign_mgr, target_area_id, cell_addr, sign_info, session_app_data);

// 8. Dtor via FUN_140a3f0b0(buffer)
auto dtor_fn = (void(*)(void*))(base + 0xA3F0B0);
dtor_fn(session_app_data);

// Done — DS2 client now sent a real RequestSummonSign through its native MessageStream.
// Server processes vanilla → sends valid PushRequestSummonSign to peer → peer accepts.
```

### Unknowns críticos antes de poder buildar v2.9.32 Opción A

1. **`NetSvrSummonSignInterface` singleton pointer**: encontrar globalmente. Posibles paths:
   - Buscar funciones con "GetSummonSignManager" o similar
   - Trazar quién llama a `FUN_1402a5b40` y ver de dónde sale `param_1`
   - Probar todos los DAT_ globals que apunten a algo cuyo +0x60+0x30 sea un vftable de NetSvrSummonSignInterface

2. **`Frpg2Sv::SignInfo` layout exacto**: ¿solo sign_id (4 bytes) o tiene type/state/flags adicionales? El job store at +0x30 sugiere 8 bytes pero puede haber padding.

3. **`Frpg2Sv::CellAddress` layout**: probablemente `{area_id u32, cell_id u32}` por su uso pero hay que confirmar.

4. **Cómo obtener el `sign_id` de Wally desde el cliente de Diux**: el cliente conoce los signs cercanos via RequestGetSignList polling. Si Diux NO se ha movido cerca del sign de Wally, su cliente NO conoce el sign_id local. Una opción: BonfireService recibe el sign_id del server (via outbox) y se lo pasa al Injector.

### Camino más simple aún (Opción A-bis): hijack FUN_1402a5b40

Si `FUN_1402a5b40` puede ser llamada con un context que apunte a globals que ya tenemos, podemos invocarla directamente sin construir args manualmente. Necesita más RE.

---

## 9. Status realista para terminar la Saponita Desbloqueada

Después de TODO este análisis, las realidades:

| Opción | Effort | Probabilidad de éxito | Notes |
|---|---|---|---|
| A — client-side inject SummonSummonSign | 4-8 hrs (multi-sesión) | Alta si resolvemos los 4 unknowns | Camino "correcto" — usa código vanilla del cliente |
| C — server cache SessionAppData de activación vanilla previa | 30 min | Media (SessionAppData puede ser time-varying) | Quick experiment, fallback aceptable |
| D — pulir relaunch path como solución final | 1 hr | Alta (ya funciona) | "Buena" UX pero no in-place — DS2 del peer se cierra y vuelve |

**Recomendación honesta**: dado que ya invertimos 10+ versiones en este problema, y A requiere RE más profundo, **lo más sano es:**
1. Implementar C como experimento rápido (validar SessionAppData hypothesis)
2. Si C funciona → win
3. Si C no funciona → ir a D (pulir relaunch) — paso 7-8 aceptables-con-relaunch
4. Dejar A documentado para sesión futura con más tiempo

---

## 8. Lo que NO conocemos todavía (TODO para próximas sesiones)

1. **`NetSvrSummonSignInterface` singleton accessor RVA** — para llamar SummonSummonSign client-side necesitamos el puntero a la interface.
2. **`Frpg2Sv::CellAddress` layout exacto** — para construir correctamente.
3. **`Frpg2Sv::SignInfo` layout exacto** — sólo conocemos sign_id (4 bytes), pero hay más fields.
4. **Validation logic in `NetSvrSummonSignSummonJob::execute()`** — la función exacta que valida el push y decide aceptar/rechazar. Encontrar `FUN_1402a5a20` decompilación.
5. **Cómo se construye SessionAppData en el cliente** — qué fields incluye, qué orden, qué encoding (probable es protobuf interno).
6. **Where the message handler dispatches PushRequestSummonSign on the client** — busca cross-refs a `PushRequestSummonSign::vftable` en handler functions.

---

## 9. Test diagnóstico recomendado antes de v2.9.32

Para confirmar la hipótesis del AppData vs SessionAppData mismatch:

1. **Test A — vanilla con ambos signs**: Wally planta saponita peq, Diux planta saponita peq, Diux activa el de Wally por la UI vanilla. Si esto funciona (Wally aparece como phantom en Diux), confirma que tener dos signs en mismo cell NO es el problema.

2. **Test B — vanilla con sólo Wally sign**: Wally planta saponita peq, Diux NO planta. Diux activa el de Wally. Si funciona, vanilla no requiere que activator tenga sign propio (esperado).

3. **Test C — vanilla activacion + then Saponita Desbloqueada**: hacer una activación vanilla exitosa primero (para cachear SessionAppData server-side al recibir Request->player_struct), luego usar Saponita Desbloqueada en el mismo zone session. Si server-side guardáramos la SessionAppData de Test C y la reutilizáramos, podría funcionar el push synthetic.

Si Test C funciona con SessionAppData cacheada, **Opción C es viable**.

---

## Referencias

- `Source/Server.DarkSouls2/Server/GameService/GameManagers/Signs/DS2_SignManager.cpp` (handlers server-side)
- `Source/Server.DarkSouls2/Server/Streams/DS2_Frpg2ReliableUdpMessageTypes.inc` (message types)
- `Protobuf/DarkSouls2/DS2_Frpg2RequestMessage.proto` (protobuf definitions)
- `Docs/ghidra-out/DarkSoulsII-out/decompiled.c` (116 MB decompiled DS2 binary)
- `Docs/TRACK_C_GHIDRA_SESSION_01.md` (previous Ghidra session notes)
- `Docs/TRACK_C_SAPONITA_RESEARCH.md` (saponita protocol notes)
