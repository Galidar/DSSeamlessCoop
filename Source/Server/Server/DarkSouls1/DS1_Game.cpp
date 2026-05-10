/*
 * Bonfire
 * Copyright (C) 2026 Galidar
 *
 * This program is free software; licensed under the MIT license.
 * You should have received a copy of the license along with this program.
 * If not, see <https://opensource.org/licenses/MIT>.
 */

#include "Server/DarkSouls1/DS1_Game.h"

namespace
{
    class DS1_PlayerState : public PlayerState
    {
    public:
        uint32_t GetCurrentAreaId() override { return 0; }
        bool IsInGame() override { return false; }
        size_t GetSoulCount() override { return 0; }
        size_t GetSoulMemory() override { return 0; }
        size_t GetDeathCount() override { return 0; }
        size_t GetMultiplayerSessionCount() override { return 0; }
        double GetPlayTime() override { return 0.0; }
        std::string GetConvenantStatusDescription() override { return ""; }
        std::string GetStatusDescription() override { return "DS1 Bonfire coordinator active"; }
    };
}

bool DS1_Game::Protobuf_To_ReliableUdpMessageType(
    google::protobuf::MessageLite*,
    Frpg2ReliableUdpMessageType&)
{
    return false;
}

bool DS1_Game::ReliableUdpMessageType_To_Protobuf(
    Frpg2ReliableUdpMessageType,
    bool,
    std::shared_ptr<google::protobuf::MessageLite>&)
{
    return false;
}

bool DS1_Game::ReliableUdpMessageType_Expects_Response(
    Frpg2ReliableUdpMessageType)
{
    return false;
}

void DS1_Game::RegisterGameManagers(GameService&)
{
    // DS1 support currently uses Server.exe as Bonfire's coordinator while the
    // DS1 Seamless runtime owns gameplay networking. DS1 clients do not speak
    // the DSOS protobuf protocol yet, so there are no gameplay managers here.
}

std::unique_ptr<PlayerState> DS1_Game::CreatePlayerState()
{
    return std::make_unique<DS1_PlayerState>();
}

std::string DS1_Game::GetAreaName(uint32_t)
{
    return "Lordran";
}

std::string DS1_Game::GetBossDiscordThumbnailUrl(uint32_t)
{
    return "";
}

void DS1_Game::GetStatistics(
    GameService&,
    std::unordered_map<std::string, std::string>& Stats)
{
    Stats["Mode"] = "Dark Souls I Bonfire coordinator";
    Stats["Runtime"] = "DS1SeamlessCoop";
    Stats["GameplayNetwork"] = "Steam P2P";
    Stats["Coordinator"] = "Server.exe";
}

void DS1_Game::SendManagementMessage(
    Frpg2ReliableUdpMessageStream&,
    const std::string&)
{
}
