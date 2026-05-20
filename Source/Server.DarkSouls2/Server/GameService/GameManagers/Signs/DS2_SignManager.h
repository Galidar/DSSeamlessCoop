/*
 * Dark Souls 3 - Open Server
 * Copyright (C) 2021 Tim Leonard
 *
 * This program is free software; licensed under the MIT license.
 * You should have received a copy of the license along with this program.
 * If not, see <https://opensource.org/licenses/MIT>.
 */

#pragma once

#include "Server/GameService/GameManager.h"
#include "Server/GameService/Utils/OnlineAreaPool.h"
#include "Server/Database/DatabaseTypes.h"
#include "Server.DarkSouls2/Protobuf/DS2_Protobufs.h"
#include "Server.DarkSouls2/Server/GameService/Utils/DS2_GameIds.h"
#include "Server.DarkSouls2/Server/GameService/Utils/DS2_CellAndAreaId.h"

#include <unordered_map>
#include <vector>

struct Frpg2ReliableUdpMessage;
class Server;
class GameService;

// Handles all client requests to do with matchmaking
// Placing and retrieving summon signs.

class DS2_SignManager
    : public GameManager
{
public:    
    DS2_SignManager(Server* InServerInstance, GameService* InGameServiceInstance);

    virtual MessageHandleResult OnMessageRecieved(GameClient* Client, const Frpg2ReliableUdpMessage& Message) override;

    virtual std::string GetName() override;
    virtual void Poll() override;

    virtual void OnLostPlayer(GameClient* Client) override;

    size_t GetLiveCount() { return LiveCache.GetTotalEntries(); }

    // v2.9.27 — Saponita Desbloqueada admin summon path.
    // Called every tick from Poll() — checks for a JSON inbox file
    // written by BonfireService when session.create fires, and
    // dispatches a synthetic PushRequestSummonSign to the target peer
    // so the engine spawns him in the summoner's world WITHOUT
    // requiring a real sign-place + activate sequence. Cached
    // player_struct from the target's most recent placed sign is
    // used (or fallback to summoner's own latest sign data).
    void ProcessAdminSummonInbox();

protected:
    bool CanMatchWith(const DS2_Frpg2RequestMessage::MatchingParameter& Client, const DS2_Frpg2RequestMessage::MatchingParameter& Match, uint32_t SignType);

    void RemoveSignAndNotifyAware(const std::shared_ptr<SummonSign>& Sign);

    MessageHandleResult Handle_RequestGetSignList(GameClient* Client, const Frpg2ReliableUdpMessage& Message);
    MessageHandleResult Handle_RequestCreateSign(GameClient* Client, const Frpg2ReliableUdpMessage& Message);
    MessageHandleResult Handle_RequestRemoveSign(GameClient* Client, const Frpg2ReliableUdpMessage& Message);
    MessageHandleResult Handle_RequestUpdateSign(GameClient* Client, const Frpg2ReliableUdpMessage& Message);
    MessageHandleResult Handle_RequestSummonSign(GameClient* Client, const Frpg2ReliableUdpMessage& Message);
    MessageHandleResult Handle_RequestRejectSign(GameClient* Client, const Frpg2ReliableUdpMessage& Message);
    MessageHandleResult Handle_RequestGetRightMatchingArea(GameClient* Client, const Frpg2ReliableUdpMessage& Message);

private:
    Server* ServerInstance;
    GameService* GameServiceInstance;

    OnlineAreaPool<DS2_CellAndAreaId, SummonSign> LiveCache;

    uint32_t NextSignId = 1000;

    // v2.9.32 Opción C — cache la SessionAppData real (bytes que mando el
    // activator en RequestSummonSign) keyed por player_id del activator.
    // Cuando ProcessAdminSummonInbox dispara, usa esta SessionAppData
    // cacheada en vez de Sign->PlayerStruct (que es AppData formato
    // distinto y causa peer disconnect).
    std::unordered_map<uint32_t, std::vector<uint8_t>> CachedSessionAppData;

};