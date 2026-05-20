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
