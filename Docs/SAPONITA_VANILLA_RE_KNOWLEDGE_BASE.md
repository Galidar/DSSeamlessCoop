# Saponita Vanilla — Knowledge Base de Reverse Engineering

> **PROPÓSITO**: Consolidación COMPLETA de todo lo descubierto via Ghidra/CE/source analysis sobre el sistema de saponita pequeña vanilla en DS2 SOTFS. Documento maestro de referencia para no perder contexto entre sesiones de AI/dev.
>
> **Última actualización**: 2026-05-19 (v2.9.32)
> **Binario analizado**: `DarkSoulsII.exe` 28,200,992 bytes (SOTFS Steam 1.0.32.0)
> **Fuentes**: Ghidra 12.0.4 headless decompile + Docs/ghidra-out/ + Server source (DS3OS-derived)

---

## TABLA DE CONTENIDOS

1. [Mapa de clases del Sign/Summon System](#1-mapa-de-clases)
2. [RVAs Confirmados — Master Reference](#2-rvas-confirmados)
3. [Pipeline End-to-End: Crear Sign + Activar Sign](#3-pipeline-end-to-end)
4. [Estructuras de datos clave](#4-estructuras-de-datos)
5. [Protocolo Frpg2 Reliable UDP](#5-protocolo-frpg2)
6. [Protobuf Messages](#6-protobuf-messages)
7. [Server-side handlers (DS3OS-derived)](#7-server-side-handlers)
8. [Item IDs y referencias cruzadas](#8-item-ids)
9. [Por qué falla nuestro synth push v2.9.27-32](#9-root-cause-analysis)
10. [Plan v2.9.32+ — Opción C cache + Opción A inject](#10-implementation-paths)
11. [Anti-cheat y limitaciones de RE en vivo](#11-anti-cheat)
12. [Globals + DAT_ refs útiles](#12-globals)
13. [Decompiled function bodies (snippets críticos)](#13-decompiled-snippets)

---

## 1. Mapa de clases

### Cliente-side (en DarkSoulsII.exe)

| Clase | Función | Estado RE |
|---|---|---|
| `NetSvrSummonSignInterface` | Fachada pública. Métodos: CreateSummonSign, SummonSummonSign, GetSummonSignList, RejectSummonSign, RemoveSummonSign, UpdateSummonSign | ✅ Confirmed |
| `NetSvrSummonSignManager` | Manager singleton (back-end de la interface) | ✅ vftable found |
| `NetSvrSummonSignPushNotifyBuffer` | Buffer para pushes server→client (queue de PushRequestSummonSign incoming) | ✅ ctor at FUN_1402a4b90 |
| `NetSvrSummonSignSummonJob` | Job async que EJECUTA un summon recibido | ✅ ctor at FUN_1402a5970 |
| `NetSvrCreateSummonSignJob` | Job para mandar RequestCreateSign al server | ✅ vftable |
| `NetSvrGetSummonSignListJob` | Job para mandar RequestGetSignList | ✅ vftable |
| `NetSvrRejectSummonSignJob` | Job para mandar RequestRejectSign | ✅ vftable |
| `NetSvrRemoveSummonSignJob` | Job para mandar RequestRemoveSign | ✅ vftable |
| `NetSvrUpdateSummonSignJob` | Job para mandar RequestUpdateSign | ✅ vftable |
| `NetSvrSummonJobBase` | Clase base de todos los NetSvr* jobs | ✅ vftable |
| `NetSummonSlotCtrl` | Maneja un slot phantom individual cliente-side | ✅ vftable |
| `NetSummonSlotAreaManager` | Lifecycle area-scoped de slots | ✅ vftable |
| `NetSummonAcceptMultiplayCtrl` | Controla "te aceptan invocar" (peer side) | ✅ vftable |
| `NetSummonJoinMultiplayCtrl` | Controla "te uniste como guest" (host side) | ✅ vftable |
| `NetSummonPacketCtrl` | Frame packets summon-specific | ✅ vftable |
| `ActiveSignManager` | Tracking de signs activos visibles en mundo cliente | ✅ vftable |
| `FeSceneSummonSignWindow` | UI nativa de "¿quieres usar el sign?" / "te están convocando" | ✅ vftable |
| `Frpg2SignImpl` | Implementación protocolo Frpg2 sign (lado server-comm) | ✅ vftable |
| `Frpg2MirrorKnightSignImpl` | Variante Mirror Knight | ✅ vftable |
| `AbstractNetSvrMySignManager` | Base abstracta para mi-sign manager | ✅ vftable |

### Variantes Mirror Knight (paralelas, no nos importan para saponita peq)

- `NetSvrMirrorKnightSignInterface`
- `NetSvrMirrorKnightSignSummonJob`
- `Frpg2MirrorKnightSignImpl`
- `PushRequestSummonMirrorKnightSign`
- `PushRequestRejectMirrorKnightSign`
- `PushRequestRemoveMirrorKnightSign`
- `RequestCreateMirrorKnightSign(Response)`
- `RequestGetMirrorKnightSignList(Response)`

### Protobuf message classes (en cliente y server)

- `PushRequestSummonSign`
- `PushRequestRejectSign`
- `PushRequestRemoveSign`
- `RequestCreateSign(Response)`
- `RequestGetSignList(Response)`
- `RequestRejectSign(Response)`
- `RequestRemoveSign(Response)`

---

## 2. RVAs Confirmados

### Function RVAs (Image base usually 0x140000000 in PE)

| RVA | Symbol | Signature | Verified |
|---|---|---|---|
| `0x29D6C0` | `NetSvrSummonSignInterface::CreateSummonSign` factory | `INetSvrJob*(area, &cell, &MatchingParameter, sign_type, &AppData, *out_sign_id)` | ✅ |
| `0x29D830` | `NetSvrSummonSignInterface::SummonSummonSign` factory | `INetSvrJob*(area, &cell, sign_info, &SessionAppData)` | ✅ |
| `0x29E700` | `SummonSummonSign` 5-arg wrapper | `LONGLONG(self, area_id, *cell_addr, sign_info, *session_app_data)` | ✅ |
| `0x2A5B40` | **High-level summon trigger** (`FUN_1402a5b40`) — 1 arg context | Allocates session_buf[168], inits, fills from GM, calls SummonSummonSign | ✅ |
| `0x2A5970` | `NetSvrSummonSignSummonJob::ctor` (`FUN_1402a5970`) | 5 params: self, sign_info, cell, session, ctx | ✅ |
| `0x2A5A20` | `NetSvrSummonSignSummonJob` method (likely execute() or callback) | Unknown signature, large body | 🔶 partial |
| `0x2A4B90` | `NetSvrSummonSignPushNotifyBuffer::ctor` | Init vectors at +0x50, +0x70, +0x90 | ✅ |
| `0x2A4D40` | `NetSvrSummonSignPushNotifyBuffer` method (push/process?) | Unknown signature | 🔶 partial |
| `0x520000` | **`SessionAppData` filler from GM** (`FUN_140520000`) | `void(gm, *buffer)` — populates 168-byte buffer from gm+0x10/+0x78/+0x80 | ✅ |
| `0xA3EFC0` | `SessionAppData` constructor/init (`FUN_140a3efc0`) | `void(*buffer)` — inits 168-byte buffer | ✅ |
| `0xA3F0B0` | `SessionAppData` destructor (`FUN_140a3f0b0`) | `void(*buffer)` — destroys SessionAppData | ✅ |
| `0xA408B0` | SessionAppData inner field copier | Called by FUN_140520000 with (gm+0x10, buf, 0) | ✅ |
| `0x284D10` | NetSvrJob base ctor helper | Common job setup | ✅ |
| `0x284F80` | NetSvrManager job enqueue | Enqueues an INetSvrJob into the manager queue | ✅ |
| `0x833320` | Memory allocator | `void*(size, align, allocator)` | ✅ |
| `0x68D5B0` | NetSvrManager get_instance / allocator accessor | Returns pointer to manager | ✅ |
| `0x2A9C90` | CellAddress builder (`FUN_1402a9c90`) | Builds CellAddress struct | ✅ |
| `0x2A9F80` | CellAddress / SignInfo builder helper (`FUN_1402a9f80`) | Helper for cell + sign info | ✅ |
| `0x2A6E40` | Area validation (`FUN_1402a6e40`) | `char(*ret, *area_id)` — "is online area" check | ✅ |
| `0x6A1840` | **Frpg2 message dispatcher cliente** (`FUN_1406a1840`) | Switch on msg_type; case 0x39b = PushRequestSummonSign processing | ✅ |
| `0xCB41D0` | PushRequestSummonSign protobuf init (`FUN_140cb41d0`) | Init the protobuf object for parsing | ✅ |
| `0x6B7EA0` | Protobuf ParseFromArray (`FUN_1406b7ea0`) | `char(*proto, *data, len)` — returns 1 if parse OK | ✅ |
| `0xCBBB30` | Protobuf field accessor (player_steam_id?) (`FUN_140cbbb30`) | Returns ptr to string field | ✅ |
| `0x1AC3D0` | DS2 `ItemGive` function | Used by Bonfire Injector to grant items at bonfire | ✅ (already used) |
| `0x4750B0` | DS2 `SetEventFlag` | Bonfire Injector reference | ✅ (already used) |
| `0x17DC40` | DS2 `RestAtBonfire` | Hooked by Injector | ✅ (already used) |
| `0x4FE1C0` | DS2 frontend confirmation helper (used by Codex v2.9.23 for native prompt) | Native UI confirmation dialog | ✅ |
| `0x503620` | DS2 common text lookup (used for SI/NO labels) | Returns UTF-16/wchar_t labels | ✅ |
| `0x3F5219` | gm_imp_global AOB anchor | `48 8B 05 ?? ?? ?? ?? 48 8B 58 38 48 85 DB 74 ?? F6` | ✅ |

### Data globals (RVAs)

| RVA | Symbol | Description | Verified |
|---|---|---|---|
| `0x1616CF8` | `DAT_141616cf8` / kPhantomMgrHolderRva | GM holder global — `*DAT_141616cf8` = GM_imp ptr | ✅ |
| `0x16148F0` | gm_imp_global address (older session) | Resolved via AOB anchor | ✅ partial |
| `0x10E4BB8` | PlayerCtrl vtable | RVA observed | ✅ |
| `0x1416751F8` | `DAT_1416751f8` | Some inventory-related global (used in inventory dump funcs) | 🔶 |
| `0x1415E1C50` | `DAT_1415e1c50` | Stack canary | ✅ |
| `0x1415E2920` | `DAT_141878f10` ref (in dispatcher) | Some global for default state | 🔶 |
| `0x141113C58` | `DAT_141113c58` | String constant (probably log fmt string) | 🔶 |
| `0x141113C80` | `DAT_141113c80` | Another string constant | 🔶 |
| `0x141113CB8` | `DAT_141113cb8` | Another string constant | 🔶 |

### PlayerCtrl offsets (de live RE sessions previas)

| Offset | Field | Use |
|---|---|---|
| `+0x90` | Position vec3 (px, py, pz floats) | Player position |
| `+0xC0` | Pointer to animation context | Animation phase float at +0x2B8 |
| `+0x128` | Soul Level (u32) | |
| `+0x168/+0x170/+0x174` | HP triple (current, max?, max+buffs?) | |
| `+0xE0+0x37C` | Equipment array | |

### phantom_mgr (resolved via DAT_141616cf8) offsets

| Offset | Field | Use |
|---|---|---|
| `+0x010` | Phantom count u32 | Active phantom mirror |
| `+0x020` | Sub-pointer (chain to actual phantom_mgr struct) | |
| `+0x218` | Saponita session timer (f32) | Decrements 1.0/sec, max 500.0 |
| `+0x230` | Saponita timer MAX (f32) | Normally 500.0 for saponita peq |
| `+0x5C0` | Phantom spawn queue start | 8 entries × 0x640 stride |
| `+0x5C0+N*0x640+0x40` | Queue entry position (vec3) | |
| `+0x5C0+N*0x640+0x80` | Queue entry type (u32, 0x12=NetworkPlayer) | |
| `+0x5C0+N*0x640+0x270` | Queue entry level (u8, <=20) | |
| `+0x5C0+N*0x640+0x630` | Queue entry status (u8) | |
| `+0x5C0+N*0x640+0x631` | Queue entry ready flag (u8) | |

---

## 3. Pipeline End-to-End

### A) Cliente PLANTA un sign

```
1. Player abre inventario, selecciona saponita peq (item_id 62040000)
2. Player presiona Use → ItemUsageParam row 2410 logic
3. DS2 corre animation 850 (placing sign)
4. Cliente calls NetSvrSummonSignInterface::CreateSummonSign(
       area_id,
       &CellAddress{cell_id, area_id},
       &MatchingParameter{sm, sl, weapon_lvl, vow},
       Frpg2SignType_SmallWhiteSoapstone (1),
       &NetSvrSummonSignAppData{<populated from current player state>},
       &out_sign_id
   )
5. Internally:
   - Allocates NetSvrCreateSummonSignJob (FUN_14029d6c0)
   - Job serializes args into RequestCreateSign protobuf
   - Job sends via Frpg2 MessageStream (msg_type 0x0397)
6. Server Handle_RequestCreateSign:
   - NRSSR sanity check on player_struct bytes
   - Creates SummonSign struct with NextSignId++
   - Stores PlayerStruct = AppData bytes
   - LiveCache.Add({cell, area}, sign_id, sign)
   - Client->ActiveSummonSigns.push_back(sign)
   - Sends RequestCreateSignResponse{sign_id} back to client
   - Stats: AddPlayerStatistic("Sign/TotalCreated", player_id, 1)
7. Cliente receives response, ahora sabe su nuevo sign_id
8. Sign visible en mundo del cliente como white glow
```

### B) Otro cliente VE el sign

```
1. Cliente periodicamente envia RequestGetSignList(area, cells, max, matching_params, ...)
2. Server Handle_RequestGetSignList:
   - LiveCache.Find(LocationId) returns signs in that area+cells
   - Filtra por MatchingParameter (SM/SL range, etc.)
   - Returns RequestGetSignListResponse{[SignInfo]}
3. Cliente recibe lista, dibuja los signs en su mundo (cada SignInfo tiene sign_id, position, type)
```

### C) Cliente ACTIVA un sign (= summon other player)

```
1. Activator camina hasta el sign, lo interactúa (presiona Use)
2. FeSceneSummonSignWindow muestra "¿Convocar a este compañero?" (UI nativa)
3. Player confirma "Sí"
4. Cliente trigger FUN_1402a5b40(context):
   a) sign_mgr = *(context+0x60) + 0x30      // = NetSvrSummonSignInterface
   b) area_id  = **(context+0x58)
   c) cell     = build via FUN_1402a9c90(...) and FUN_1402a9f80(...)
   d) sign_info = construct from local sign list (sign_id + flags, 8 bytes)
   e) char session_buf[168]                    // SessionAppData inline
   f) FUN_140a3efc0(session_buf)              // SessionAppData ctor
   g) gm = *DAT_141616cf8                     // GM global
   h) FUN_140520000(gm, session_buf)          // POPULATE SessionAppData from current state
   i) FUN_14029e700(sign_mgr, area_id, &cell, sign_info, session_buf)
      // = NetSvrSummonSignInterface::SummonSummonSign(...)
   j) FUN_140a3f0b0(session_buf)              // SessionAppData dtor
5. SummonSummonSign internally:
   - Allocates NetSvrSummonSummonSignJob (0x58 bytes)
   - FUN_14029d830(job) inits vftable
   - FUN_140284f80(sign_mgr+0x18, job) enqueues in NetSvrManager
   - Job stores area_id at +0x28, cell at +0x2C, sign_info at +0x30
   - FUN_1402a6cf0(session_app_data, job+0x38) copies SessionAppData into job
6. Job execute() serializes into RequestSummonSign protobuf:
   {
     online_area_id: u32,
     sign_info: SignInfo { sign_id, ... },
     player_struct: SessionAppData bytes,      // ← 168 bytes serialized
     cell_id: u32
   }
7. Cliente sends via MessageStream (msg_type 0x0398)
8. Server Handle_RequestSummonSign:
   - NRSSR sanity check
   - LiveCache.Find(LocationId, sign_id) → finds the Sign
   - Verifies Sign->BeingSummonedByPlayerId == 0
   - Constructs PushRequestSummonSign:
       push_message_id: PushID_PushRequestSummonSign
       player_id: <activator's player_id>
       player_steam_id: <activator's Steam>
       sign_id: <Sign->SignId>
       player_struct: <Request->player_struct() = SessionAppData bytes>  ← KEY: copies activator's SessionAppData
   - OriginClient (sign owner)->MessageStream->Send(&PushMessage)
   - Sign->BeingSummonedByPlayerId = activator
   - Sends RequestSummonSignResponse{} (empty ack) back to activator
```

### D) Sign owner RECIBE el push y es convocado

```
1. Cliente del sign owner recibe Frpg2 push message_type 0x039B
2. Llega a Frpg2 dispatcher: FUN_1406a1840 with param_2 = 0x39b
3. Dispatcher:
   a) local_118 = Frpg2RequestMessage::PushRequestSummonSign::vftable
   b) FUN_140cb41d0(&local_118)   // init protobuf
   c) cVar2 = FUN_1406b7ea0(&local_118, raw_bytes, raw_len)  // ParseFromArray
   d) if cVar2 != 0 (parse OK):
      - Extract player_steam_id, player_id, sign_id, player_struct
      - Build internal struct (0x90 = 144 bytes) on stack (local_c8)
      - Copy player_struct bytes into struct buffer
      - Call vtable[+0x10] of *(param_1+0x48) with (0, &struct, 0x90)
        ← This is NetSvrSummonSignPushNotifyBuffer.process_push() or similar
4. NetSvrSummonSignPushNotifyBuffer queues the push
5. On next tick, NetSvrSummonSignSummonJob picks up the push
6. Job VALIDATES (algo no documentado en detalle):
   - SignHandle exists locally for sign_id? (yes if sign was placed by this client)
   - SessionAppData bytes deserialize correctly as NetSvrSummonSignSessionAppData?
   - Other player still online?
   - Sign hasn't been revoked?
7. If valid:
   - NetSummonSlotCtrl allocates phantom slot
   - PlayerCtrl ctor runs with arg4=1 (phantom mode)
   - World transition to activator's world begins
   - "Te están convocando" loading screen
   - Player respawns as phantom in activator's world
8. If invalid:
   - Some path closes the MessageStream (observed as "Connection closed")
   - Client's DS2 may crash or just disconnect cleanly
```

---

## 4. Estructuras de datos

### NetSvrSummonSignPushNotifyBuffer (cliente-side, from FUN_1402a4b90 ctor)

```c
struct NetSvrSummonSignPushNotifyBuffer {
    void**  vftable;                   // +0x00
    void*   allocator;                 // +0x08 (param_2 of ctor)
    u8      flag_init;                 // +0x10
    u8      pad[7];                    // +0x11..+0x17
    // some inline 0x30-byte struct initialized by FUN_14084b400
    u8      inline_struct[0x30];       // +0x18..+0x47
    void*   begin_pushes;              // +0x50 (Vector<PushItem>)
    void*   end_pushes;                // +0x58
    void*   capacity_pushes;           // +0x60
    void*   alloc_pushes;              // +0x68 (= allocator)
    void*   begin_ptrs1;               // +0x70 (Vector<void*>)
    void*   end_ptrs1;                 // +0x78
    void*   capacity_ptrs1;            // +0x80
    void*   alloc_ptrs1;               // +0x88 (= allocator)
    void*   begin_ptrs2;               // +0x90 (Vector<void*>)
    void*   end_ptrs2;                 // +0x98
    void*   capacity_ptrs2;            // +0xA0
    void*   alloc_ptrs2;               // +0xA8 (= allocator)
};
```

Each "PushItem" is 0x110 bytes (272 bytes) — the queued push data.

### NetSvrSummonSignSummonJob (cliente-side, from FUN_1402a5970 ctor)

```c
struct NetSvrSummonSignSummonJob {
    void**  vftable;                   // +0x00 = NetSvrSummonSignSummonJob::vftable
    void**  vftable_dup;               // +0x08 (also vftable ptr)
    u8      inherited_base[0x18];      // +0x10..+0x27 (from NetSvrJob base, FUN_14027a930 init)
    u64     sign_info_part1;           // +0x28 = param_2[0]
    u64     sign_info_part2;           // +0x30 = param_2[1]
    u8      sign_info_byte;            // +0x38 = param_2[2]
    u8      pad1[3];                   // +0x39..+0x3B
    u32     cell_or_area1;             // +0x3C = param_3[0]
    u32     cell_or_area2;             // +0x40 = param_3[1]
    u32     cell_or_area3;             // +0x44 = param_3[2]
    u32     cell_or_area4;             // +0x48 = param_3[3]
    u32     cell_or_area5;             // +0x4C = param_3[4]
    u32     cell_or_area6;             // +0x50 = param_3[5]
    u64     session_app_data;          // +0x58 = param_4 (ptr to SessionAppData)
    u64     context;                   // +0x60 = param_5
};
```

### NetSvrSummonSignSessionAppData (168 bytes / 0xA8)

From decompiled body of FUN_1402a5b40:
- `local_d8[168]` is the stack-allocated session_app_data buffer
- After FUN_140a3efc0 init + FUN_140520000 fill, it contains:
  - Bytes 0x00..0x9F: filled by FUN_140a408b0(gm+0x10, buf, 0)
  - Bytes 0xA0..0xA7: copy of `*(gm+0x78)` (8 bytes)
  - Bytes 0xA8..0xAF: copy of `*(gm+0x80)` (8 bytes)

Total visible size: 0xB0 = 176 bytes? But buffer is 168 bytes? May be 168 + 8-byte padding for alignment.

**Format is OPAQUE — protobuf only sees it as `bytes`.** Internally it's a serialized representation of player state at the moment of activation: char model, equipment, HP, position relative to host, etc.

### Frpg2Sv::CellAddress (probable layout)

```c
struct CellAddress {
    u32 cell_id;     // +0x00
    u32 area_id;     // +0x04 (or other order)
    // possibly more fields (the param_3 array of 6 u32s suggests 24 bytes?)
};
```

Need more RE to confirm exact size + field order.

### Frpg2Sv::SignInfo (8 bytes by value)

```c
struct SignInfo {
    u32 sign_id;     // +0x00
    u32 flags;       // +0x04 (type + state?)
};
```

Passed by value (8 bytes fit in single u64 register on x64).

### NetSvrSummonSignAppData (sign-level, sent on Create)

**OPAQUE** — protobuf bytes. Format known only by client. Contains data for displaying sign in world (model, equipment for the sign apparition).

**Server stores** this in `Sign->PlayerStruct` (vector<uint8_t>).

**Crucially**: this is DIFFERENT format than SessionAppData. v2.9.27-31 mistake was using this in the push when peer expected SessionAppData.

---

## 5. Protocolo Frpg2

### Reliable UDP message types (DS2)

| Hex | Name | Direction | Use | Expect Response |
|---|---|---|---|---|
| `0x0397` | RequestCreateSign | C→S | Place a sign | Yes (RequestCreateSignResponse) |
| `0x0398` | RequestSummonSign | C→S | Activate someone's sign | Yes (RequestSummonSignResponse) |
| `0x0399` | RequestRemoveSign | C→S | Remove your own sign | Yes |
| `0x039A` | RequestGetSignList | C→S | Poll signs in area | Yes |
| `0x039B` | **PushRequestSummonSign** | **S→C** | "You are being summoned" | No (push) |
| `0x039C` | PushRequestRejectSign | S→C | "Summon rejected" | No (push) |
| `0x039D` | PushRequestRemoveSign | S→C | "Sign removed" | No (push) |
| `0x039E` | RequestRejectSign | C→S | Decline summon | Yes |
| `0x039F` | RequestUpdateSign | C→S | Update sign state | Yes |

### Frpg2ReliableUdpMessageHeader

```c
struct Frpg2ReliableUdpMessageHeader {
    Frpg2ReliableUdpMessageType msg_type;  // u32
    u32 msg_index;
    // ...
};
```

For Push messages: `msg_index = 0xFFFFFFFF` always.  
For Response messages: `msg_type = Reply`, `msg_index = ResponseTo->msg_index`.  
For Request messages: `msg_index = SentMessageCounter++`.

---

## 6. Protobuf Messages

```protobuf
message RequestCreateSign {
    required uint32 online_area_id = 1;
    required MatchingParameter matching_parameter = 2;
    required bytes player_struct = 3;     // NetSvrSummonSignAppData serialized
    required uint32 cell_id = 4;
    required uint32 sign_type = 5;        // 0=White, 1=SmallWhite, 2=Red, ...
}

message RequestCreateSignResponse {
    required uint32 sign_id = 1;
}

message RequestSummonSign {
    required int64 online_area_id = 1;
    required SignInfo sign_info = 2;
    required bytes player_struct = 3;     // NetSvrSummonSignSessionAppData serialized
    required int64 cell_id = 4;
}

message RequestSummonSignResponse {
    // empty
}

message PushRequestSummonSign {
    required PushMessageId push_message_id = 1;
    required int64 player_id = 2;         // Summoner (activator)
    required int64 sign_id = 3;
    required bytes player_struct = 4;     // SessionAppData copied from RequestSummonSign
    required string player_steam_id = 5;
}

message RequestGetSignList {
    required uint32 online_area_id = 1;
    repeated SignCellInfo search_areas = 2;
    required uint32 max_signs = 3;
    required MatchingParameter matching_parameter = 4;
    // ... more fields
}

message MatchingParameter {
    // SM range, SL range, weapon level, vow, etc.
}

message SignInfo {
    // sign_id, type, ...
}

enum PushMessageId {
    PushID_PushRequestSummonSign = ?;
    PushID_PushRequestRejectSign = ?;
    PushID_PushRequestRemoveSign = ?;
}

enum SignType {
    SignType_WhiteSoapstone = 0;       // assumed
    SignType_SmallWhiteSoapstone = 1;  // assumed
    SignType_RedSoapstone = 2;
    SignType_DragonEye = ?;
}

enum SummonErrorId {
    SummonErrorId_NoLongerBeSummonable = ?;
    SummonErrorId_SignHasDisappeared = ?;
    SummonErrorId_SignAlreadyUsed = ?;
}
```

(From `Protobuf/DarkSouls2/DS2_Frpg2RequestMessage.proto`)

---

## 7. Server-side Handlers (DS3OS-derived)

`Source/Server.DarkSouls2/Server/GameService/GameManagers/Signs/DS2_SignManager.cpp`

### Handle_RequestCreateSign (line 226)

```cpp
1. NRSSR sanity check on Request->player_struct()
2. Create SummonSign{SignId=NextSignId++, Type, PlayerId, PlayerStruct=request bytes, MatchingParameters, Location}
3. LiveCache.Add(LocationId, SignId, Sign)
4. Client->ActiveSummonSigns.push_back(Sign)
5. RequestCreateSignResponse{sign_id}
6. AddPlayerStatistic("Sign/TotalCreated", player_id, 1)
7. Discord notice (if configured)
```

### Handle_RequestSummonSign (line 358)

```cpp
1. NRSSR sanity check on Request->player_struct()
2. LiveCache.Find(LocationId, sign_id) → Sign
3. If !Sign: bSuccess = false, SummonError = SignHasDisappeared
4. If Sign->BeingSummonedByPlayerId != 0: bSuccess = false, SummonError = SignAlreadyUsed
5. If bSuccess:
   - FindClientByPlayerId(Sign->PlayerId) → OriginClient
   - Build PushRequestSummonSign{
       player_id: activator.player_id,
       player_steam_id: activator.steam_id,
       sign_id: Sign->SignId,
       player_struct: Request->player_struct()  ← Activator's SessionAppData copied
   }
   - OriginClient->MessageStream->Send(&PushMessage)
   - Sign->BeingSummonedByPlayerId = activator.player_id
6. Send RequestSummonSignResponse{} (empty ack)
7. If !bSuccess: send PushRequestRejectSign with SummonError to activator
```

### ProcessAdminSummonInbox (v2.9.27+ — our addition for Saponita Desbloqueada)

```cpp
1. Read C:/DSSeamlessCoop/admin_summon_inbox.json if exists, delete file
2. Parse JSON: request_id, mode, summoner_steam_id, target_steam_id
3. Mode "auto_pick_two_players": find 2 connected clients, lowest player_id = summoner
4. v2.9.30+: Pick TargetSign from TargetClient->ActiveSummonSigns.back() (for sign_id)
5. v2.9.32+: Pick cached SessionAppData from CachedSessionAppData[summoner_player_id] (for player_struct)
6. Construct PushRequestSummonSign with correct format
7. Send to target via target's MessageStream
8. Write outbox JSON with result
```

---

## 8. Item IDs

### Saponitas + relacionados (de Paramdex DS2S)

| ID | Item | Sign type | UseAnim | UseID | Icon |
|---|---|---|---|---|---|
| `62020000` | White Sign Soapstone (grande) | 0 | 850 | 2410 | 62020000 |
| `62030000` | (variant?) | 0 | 850 | 2410 | 62030000 |
| `62040000` | **Small White Sign Soapstone (saponita peq)** | 1 | 850 | 2410 | 62040000 |
| `62045000` | Red Sign Soapstone (saponita roja) | 2 | 850 | 2410 | 62045000 |
| `62050000` | Bell Keepers Sign? | ? | ? | ? | ? |
| `62060000` | Cracked Red Eye Orb (invasion) | — | 200 | 2120 | 62060000 |
| `62061000` | **Saponita Desbloqueada (custom Bonfire)** | — | 1700 (custom) | 62061000 (custom row) | 62045000 (post-v2.9.19) |
| `62070000` | Dragon Eye | — | ? | ? | 62070000 |

### ItemUsageParam (8 bytes/row)

| Row | Items | Unk00 | u04 | u05 | u06 | u07 | Notes |
|---|---|---|---|---|---|---|---|
| `2410` | Saponita Pequeña + Roja vanilla | -1 | 7 | 24 | 0 | 0 | The saponita summon logic |
| `2120` | Cracked Red Eye Orb | -1 | 1 | 20 | 6 | 0 | Invasion logic |
| `62061000` | Saponita Desbloqueada custom | -1 | 7 | 180 | 1 | 0 | Hueco — handled by Injector hook |

### Map areas with saponita activity (cell IDs observed)

| online_area_id | cell_id | Zone | Notes |
|---|---|---|---|
| `10100000` | `4265607170` | Majula | Tested location |
| `10100000` | `4290772987` | Majula nearby cell | Tested location |

---

## 9. Root Cause Analysis (por qué v2.9.27-31 fallaron)

### El bug

**`player_struct` en `RequestCreateSign` (creación de sign) y en `RequestSummonSign` (activación de sign) son protobuf bytes pero contienen estructuras DIFERENTES:**

- `RequestCreateSign.player_struct` = serialized `NetSvrSummonSignAppData` (formato A)
- `RequestSummonSign.player_struct` = serialized `NetSvrSummonSignSessionAppData` (formato B)
- `PushRequestSummonSign.player_struct` = `RequestSummonSign.player_struct` (= formato B)

**Server stores `formato A` en `Sign->PlayerStruct` cuando recibe RequestCreateSign.**

**v2.9.27-31 metían `formato A` en el push synthetic donde peer espera `formato B`.**

Cliente del peer recibe push → parsea protobuf (success — bytes son opacos al protobuf) → pasa al SummonJob → SummonJob intenta deserializar player_struct como SessionAppData → falla porque es AppData → algún check internal falla → cierra MessageStream → server log muestra "Connection closed... Disconnecting client as message stream closed" ~7s después del push.

### Por qué nuestros 5 intentos no resolvieron

| Versión | Intentó | Por qué falló |
|---|---|---|
| v2.9.27 | Push con TargetClient.PlayerStruct fallback a SummonerClient.PlayerStruct | Ambos eran formato AppData |
| v2.9.28 | Mismo pero host-side write | Mismo formato wrong |
| v2.9.29 | + stop LAN beacon post-push | Beacon stop OK, pero push igual mismatch formato |
| v2.9.30 | Solo TargetClient signs, sin fallback | Sigue AppData formato |
| v2.9.31 | sign_id de target + player_struct de SummonerClient.PlayerStruct | Aún era SummonerClient.AppData (formato A) |
| v2.9.32 | sign_id de target + player_struct de CachedSessionAppData[summoner] | **Cached SessionAppData es formato B real** ← debería funcionar si SessionAppData no es time-varying |

### Cómo Wally se desconectaba siempre 7s después

1. T+0s: server emite PushRequestSummonSign con player_struct[80] bytes formato A
2. T+0s: Wally's DS2 cliente parsea protobuf OK
3. T+0s..2s: push enqueued en NetSvrSummonSignPushNotifyBuffer
4. T+~2s: NetSvrSummonSignSummonJob procesa el push
5. T+~2s: SummonJob intenta deserializar player_struct como SessionAppData
6. T+~2s..5s: validation fails, cliente termina la conexión
7. T+~7s: server detecta el TCP stream closed
8. server.log: "2:Wally Connection closed... Disconnecting client as message stream closed"

---

## 10. Implementation Paths

### Opción C — Cache real SessionAppData (v2.9.32 IMPLEMENTADO)

```cpp
// In Handle_RequestSummonSign:
CachedSessionAppData[Player.GetPlayerId()] = Request->player_struct().bytes;

// In ProcessAdminSummonInbox:
const auto& bytes = CachedSessionAppData[summoner_player_id];
PushMessage.set_player_struct(bytes.data(), bytes.size());
```

**Pre-requisito**: el summoner debe haber activado al menos una saponita peq vanilla EN LA SESIÓN ACTUAL antes de usar Saponita Desbloqueada.

**Risk**: SessionAppData puede ser time-varying (HP, posición, equipment momento de activación). Reusarlo stale puede fallar otra validación cliente-side.

### Opción A — Client-side inject SummonSummonSign (FUTURO)

Diux's Injector llama directamente la función nativa:

```cpp
// (1) Resolve gm via existing DAT_141616cf8
uintptr_t gm = *(uintptr_t*)(game_base + 0x1616CF8);

// (2) Allocate SessionAppData buffer
alignas(8) uint8_t session_buf[168] = {0};

// (3) Init via FUN_140a3efc0
((void(*)(void*))(game_base + 0xA3EFC0))(session_buf);

// (4) Fill via FUN_140520000 — KEY function (uses real GM state)
((void(*)(uintptr_t, void*))(game_base + 0x520000))(gm, session_buf);

// (5) Get SignManager singleton (??? — UNKNOWN ACCESSOR)
uintptr_t sign_mgr = ???;

// (6) Build CellAddress + SignInfo (??? — UNKNOWN LAYOUTS)
uint32_t cell_addr[3] = { current_area, current_cell, 0 };  // guessed
uint64_t sign_info = wally_sign_id_from_server_outbox;  // guessed 8 bytes

// (7) Call SummonSummonSign
((uintptr_t(*)(uintptr_t, uint32_t, uint32_t*, uint64_t, void*))(game_base + 0x29E700))(
    sign_mgr, current_area, cell_addr, sign_info, session_buf);

// (8) Dtor
((void(*)(void*))(game_base + 0xA3F0B0))(session_buf);
```

**Unknowns críticos**:
1. SignManager singleton pointer (need to find global or accessor)
2. Frpg2Sv::CellAddress exact layout (24 bytes? aligned?)
3. Frpg2Sv::SignInfo exact 8-byte layout
4. How Wally's sign_id reaches Diux's Injector (via Bonfire IPC?)

**Cuando se resuelvan, la implementación es directa y usa todo código vanilla del cliente.**

### Opción D — Aceptar relaunch (v2.9.27 path)

Funciona. Peer's DS2 se reinicia ~5-7s. Después del relaunch, conectado al server del host. Saponita Desbloqueada vuelve "casi" como vanilla pero con UX peor.

---

## 11. Anti-Cheat

DS2 SOTFS tiene anti-cheat user-mode que bloquea:
- CE attach con memory reads desde shell normal (modules=0, can_read=false)
- Hardware breakpoints (triggers AC silenciosamente)
- Module enum

Workarounds probados:
- **Injector dentro del proceso**: funciona perfecto (somos parte del proceso, AC no aplica a nosotros)
- **Ghidra static analysis**: 100% safe, sin AC
- **CE con DBVM**: no probado, requiere kernel mode + setup adicional

**Recomendación**: para todo RE futuro, usar Ghidra (decompile en disco) + Injector logging (eventos JSONL). Evitar CE attach a menos que tengamos DBVM ready.

---

## 12. Globals

### DAT_141616cf8 — el gm holder global

```cpp
// At RVA 0x1616CF8 in DarkSoulsII.exe
uintptr_t* dat_141616cf8 = (uintptr_t*)(game_base + 0x1616CF8);
uintptr_t gm = *dat_141616cf8;  // GameManagerImp pointer

// From gm:
//   gm + 0x10 = chr_world  (FUN_140a408b0 source)
//   gm + 0x18 = next pointer in chain
//     (gm+0x18)+0x50 = local PlayerCtrl
//   gm + 0x78 = something copied into SessionAppData +0xA8
//   gm + 0x80 = something copied into SessionAppData +0xB0
//   gm + 0x650 = slot_mgr (PlayerCtrl slot pool)
//     slot_mgr+0x5D0+N*0xA90 = slot N record
//       slot+0xC8 = PlayerCtrl* in slot
//       slot+0xE0 = active flag
//   gm + 0x22E0 = item display manager (used by v2.9.23 native prompt)
```

### Injector hook RVAs (Bonfire's existing usage)

- `ItemGive` = `+0x1AC3D0`
- `SetEventFlag` = `+0x4750B0`
- `RestAtBonfire` = `+0x17DC40`
- `Inventory::UseItem` vtable slot offset `+0x38` (vtable slot, not RVA)
- `Inventory::SelectedItemEntry` vtable slot offset `+0x70`
- Native frontend confirm = `+0x4FE1C0`
- Native text lookup = `+0x503620`

---

## 13. Decompiled Snippets (críticos preservados)

### FUN_1402a5b40 — high-level summon trigger (CORE)

```c
void FUN_1402a5b40(longlong param_1)
{
    undefined8 uVar1;
    undefined4 uVar2;
    undefined4 *puVar3;
    undefined8 uVar4;
    // ... 168-byte buffer local_d8[168]
    
    local_18 = DAT_1415e1c50 ^ (ulonglong)auStack_198;  // stack canary
    uVar1 = *(undefined8 *)(*(longlong *)(param_1 + 0x60) + 0x30);  // sign_mgr from context
    local_168 = **(undefined4 **)(param_1 + 0x58);  // area_id?
    FUN_1402a6e40(local_128, &local_168);  // area validation
    if (local_128[0] != '\0') {
        // ... build CellAddress + SignInfo via FUN_1402a9c90 + FUN_1402a9f80
        FUN_140a3efc0(local_d8);  // SessionAppData ctor
        // ...
        if (DAT_141616cf8 != (undefined8 *)0x0) {
            uVar4 = *DAT_141616cf8;  // gm
        }
        FUN_140520000(uVar4, local_d8);  // FILL SessionAppData
        // ...
        FUN_14029e700(uVar1, uVar2, local_138, CONCAT44(local_120, local_124));  // SummonSummonSign
        FUN_140a3f0b0(local_d8);  // dtor
    }
    thunk_FUN_141b6b19f(local_18 ^ (ulonglong)auStack_198);  // stack check
    return;
}
```

### FUN_14029e700 — SummonSummonSign wrapper

```c
LONGLONG FUN_14029e700(longlong self, undefined4 area_id, undefined4* cell, undefined8 sign_info, undefined8 session_app_data)
{
    char cVar1;
    undefined8 uVar2;
    longlong lVar3;
    
    if (*(longlong *)(self + 0x18) != 0) {        // has NetSvrManager?
        cVar1 = FUN_1402d8920(area_id);            // is online area?
        if (cVar1 != '\0') {
            uVar2 = FUN_1402aa380();              // get heap
            lVar3 = FUN_140833320(0x58, 8, uVar2);  // allocate 88 bytes
            uVar2 = 0;
            if (lVar3 != 0) {
                uVar2 = FUN_14029d830(lVar3);     // init job (vftable etc.)
            }
            lVar3 = FUN_140284f80(*(undefined8 *)(self + 0x18), uVar2);  // enqueue
            if (lVar3 != 0) {
                *(undefined4 *)(lVar3 + 0x28) = area_id;
                *(undefined4 *)(lVar3 + 0x2c) = *cell;
                *(undefined8 *)(lVar3 + 0x30) = sign_info;
                cVar1 = FUN_1402a6cf0(session_app_data, lVar3 + 0x38);  // copy SessionAppData
                if (cVar1 != '\0') return lVar3;
                FUN_1402a7c80(lVar3);              // error cleanup
            }
        }
    }
    return 0;
}
```

### FUN_140520000 — SessionAppData filler (KEY)

```c
void FUN_140520000(longlong gm, longlong buf)
{
    FUN_140a408b0(gm + 0x10, buf, 0);                        // fill via inner copier
    *(undefined8 *)(buf + 0xa8) = *(undefined8 *)(gm + 0x78);  // copy field
    *(undefined8 *)(buf + 0xb0) = *(undefined8 *)(gm + 0x80);  // copy field
    return;
}
```

### FUN_1406a1840 — Frpg2 push dispatcher (cliente)

```c
void FUN_1406a1840(longlong param_1, int param_2, void* raw_data, ulong raw_len)
{
    // ... stack setup ...
    
    if (*(longlong *)(param_1 + 0x48) != 0) {       // has push processor?
        if (param_2 == 0x39b) {                      // PushRequestSummonSign
            local_110 = FUN_1406bbc90();
            local_118 = Frpg2RequestMessage::PushRequestSummonSign::vftable;
            // ... protobuf instance setup ...
            FUN_140cb41d0(&local_118);              // init protobuf
            cVar2 = FUN_1406b7ea0(&local_118, raw_data, raw_len);  // ParseFromArray
            if (cVar2 != '\0') {                    // parse OK
                // ... extract fields:
                // local_d8 / local_e0 = sign_id / player_id
                // local_e8 / local_ec / local_f0 = more fields
                // FUN_140cbbb30(&local_118, &local_150) = player_steam_id string
                // ... build 0x90 (144 byte) struct local_c8 ...
                // ... copy player_struct bytes ...
                
                // KEY CALL: vtable[+0x10] of *(param_1+0x48), called with (0, &struct, 0x90)
                (**(code **)(**(longlong **)(param_1 + 0x48) + 0x10))
                    (*(longlong **)(param_1 + 0x48), 0, local_c8, 0x90);
            }
            FUN_140c6d340(&local_118);
        }
        else if (param_2 == 0x39c) {                 // PushRequestRejectSign
            // similar pattern
        }
    }
}
```

### Vanilla server Handle_RequestSummonSign (from DS3OS-derived)

```cpp
MessageHandleResult DS2_SignManager::Handle_RequestSummonSign(GameClient* Client, ...) {
    auto* Request = (DS2_Frpg2RequestMessage::RequestSummonSign*)Message.Protobuf.get();
    
    // v2.9.32: cache the real SessionAppData for later reuse
    CachedSessionAppData[Player.GetPlayerId()] = bytes(Request->player_struct());
    
    bool bSuccess = true;
    
    if (BuildConfig::NRSSR_SANITY_CHECKS) {
        auto v = DS2_NRSSRSanitizer::ValidateEntryList(Request->player_struct().data(), ...);
        if (v != Valid) bSuccess = false;
    }
    
    DS2_CellAndAreaId LocationId = { (uint64_t)Request->cell_id(), Request->online_area_id() };
    auto Sign = LiveCache.Find(LocationId, Request->sign_info().sign_id());
    if (!Sign) { bSuccess = false; SummonError = SignHasDisappeared; }
    if (Sign && Sign->BeingSummonedByPlayerId != 0) { bSuccess = false; SummonError = SignAlreadyUsed; }
    
    if (bSuccess) {
        auto OriginClient = GameServiceInstance->FindClientByPlayerId(Sign->PlayerId);
        
        DS2_Frpg2RequestMessage::PushRequestSummonSign PushMessage;
        PushMessage.set_push_message_id(PushID_PushRequestSummonSign);
        PushMessage.set_player_id(Player.GetPlayerId());
        PushMessage.set_player_steam_id(Player.GetSteamId());
        PushMessage.set_sign_id(Sign->SignId);
        PushMessage.set_player_struct(Request->player_struct().data(), Request->player_struct().size());
        //                            ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
        //                            CRITICAL: copies SessionAppData from activator's request directly
        
        OriginClient->MessageStream->Send(&PushMessage);
        Sign->BeingSummonedByPlayerId = Player.GetPlayerId();
    }
    
    DS2_Frpg2RequestMessage::RequestSummonSignResponse Response;
    Client->MessageStream->Send(&Response, &Message);
    
    if (!bSuccess) {
        DS2_Frpg2RequestMessage::PushRequestRejectSign RejPush;
        RejPush.set_push_message_id(PushID_PushRequestRejectSign);
        RejPush.set_player_id(Player.GetPlayerId());
        RejPush.set_sign_id(Request->sign_info().sign_id());
        RejPush.set_summon_error_id(SummonError);
        Client->MessageStream->Send(&RejPush);
    }
    
    return MessageHandleResult::Handled;
}
```

---

## Glossary

- **AppData** = `NetSvrSummonSignAppData` = bytes sent with RequestCreateSign (sign-level state)
- **SessionAppData** = `NetSvrSummonSignSessionAppData` = bytes sent with RequestSummonSign (per-summon activator state, 168 bytes)
- **SignInfo** = 8-byte Frpg2Sv struct containing sign_id + flags
- **CellAddress** = Frpg2Sv struct containing cell_id + area_id
- **MatchingParameter** = Frpg2Sv struct with SM, SL, weapon level for matchmaking
- **GM / gm_imp** = GameManagerImp singleton, accessed via DAT_141616cf8
- **NetSvrJob** = base class for all client-side async network jobs
- **NetSvrManager** = singleton that owns and dispatches jobs

---

## TODO para próximas sesiones

1. ✅ Identify root cause (DONE: AppData vs SessionAppData mismatch)
2. ✅ Map all RVAs (DONE: 30+ functions, 10+ globals)
3. ✅ Implement Opción C (DONE: v2.9.32)
4. ⏳ Test v2.9.32 in-game
5. ⏳ If C fails, RE NetSvrSummonSignInterface singleton accessor
6. ⏳ If C fails, RE Frpg2Sv::CellAddress + SignInfo exact layouts
7. ⏳ If C fails, implement Opción A v2.9.33
8. ⏳ Map NetSvrSummonSignSummonJob.execute() validation logic (what exactly fails when SessionAppData is wrong format)
9. ⏳ Find what calls FUN_1402a5b40 from above (the input handler trigger)
10. ⏳ Document the SessionAppData binary layout (start by capturing bytes from real Handle_RequestSummonSign + diff'ing two consecutive activations)

---

## Referencias rápidas

- Repo path: `C:/Users/Diux/Desktop/DSSeamlessCoop/`
- Decompiled binary: `Docs/ghidra-out/DarkSoulsII-out/decompiled.c` (116MB)
- Ghidra report: `Docs/ghidra-out/DarkSoulsII-out/report.md`
- Server source: `Source/Server.DarkSouls2/Server/GameService/GameManagers/Signs/DS2_SignManager.cpp`
- Injector source: `Source/Injector/Hooks/DarkSouls2/DS2_NativeRuntimeHook.cpp`
- Previous RE notes:
  - `Docs/TRACK_C_GHIDRA_SESSION_01.md`
  - `Docs/TRACK_C_RE_SESSION_01.md`
  - `Docs/TRACK_C_SAPONITA_RESEARCH.md`
  - `Docs/SAPONITA_PEQ_NATIVE_DEEP_RE.md` (deep dive sesión 2026-05-19)
  - `Docs/SAPONITA_DESBLOQUEADA_8STEP_PIPELINE.md`
  - `Docs/SAPONITA_DESBLOQUEADA_DESIGN.md`
- Live data: `C:/DSSeamlessCoop/server.log`, `C:/DSSeamlessCoop/admin_summon_outbox.json`, `C:/DSSeamlessCoop/Runtime/DS2Native/*.jsonl`
