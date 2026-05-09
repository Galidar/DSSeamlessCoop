/*
 * Bonfire
 * Copyright (C) 2026 Galidar
 *
 * This program is free software; licensed under the MIT license.
 * You should have received a copy of the license along with this program.
 * If not, see <https://opensource.org/licenses/MIT>.
 */

#pragma once

#include "Server/Game.h"

class DS1_Game : public Game
{
public:
    bool Protobuf_To_ReliableUdpMessageType(
        google::protobuf::MessageLite* Message,
        Frpg2ReliableUdpMessageType& Output) override;
    bool ReliableUdpMessageType_To_Protobuf(
        Frpg2ReliableUdpMessageType Type,
        bool IsResponse,
        std::shared_ptr<google::protobuf::MessageLite>& Output) override;
    bool ReliableUdpMessageType_Expects_Response(
        Frpg2ReliableUdpMessageType Type) override;

    void RegisterGameManagers(GameService& Service) override;
    std::unique_ptr<PlayerState> CreatePlayerState() override;

    std::string GetAreaName(uint32_t AreaId) override;
    std::string GetBossDiscordThumbnailUrl(uint32_t BossId) override;
    void GetStatistics(
        GameService& Service,
        std::unordered_map<std::string, std::string>& Stats) override;
    void SendManagementMessage(
        Frpg2ReliableUdpMessageStream& stream,
        const std::string& TextMessage) override;
};
