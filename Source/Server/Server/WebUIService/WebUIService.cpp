/*
 * Dark Souls 3 - Open Server
 * Copyright (C) 2021 Tim Leonard
 *
 * This program is free software; licensed under the MIT license.
 * You should have received a copy of the license along with this program.
 * If not, see <https://opensource.org/licenses/MIT>.
 */

#include "Server/WebUIService/WebUIService.h"

#include "Server/WebUIService/Handlers/AuthHandler.h"
#include "Server/WebUIService/Handlers/PlayersHandler.h"
#include "Server/WebUIService/Handlers/StatisticsHandler.h"
#include "Server/WebUIService/Handlers/SettingsHandler.h"
#include "Server/WebUIService/Handlers/DebugStatisticsHandler.h"
#include "Server/WebUIService/Handlers/MessageHandler.h"
#include "Server/WebUIService/Handlers/BansHandler.h"
#include "Server/WebUIService/Handlers/ShardingHandler.h"

#include "Server/Server.h"
#include "Shared/Core/Utils/Logging.h"
#include "Shared/Core/Utils/Strings.h"
#include "Shared/Core/Utils/Random.h"
#include "Shared/Core/Utils/DebugObjects.h"

#include "Config/BuildConfig.h"
#include "Config/RuntimeConfig.h"

WebUIService::WebUIService(Server* OwningServer)
    : ServerInstance(OwningServer)
{
    Handlers.push_back(std::make_shared<AuthHandler>(this));
    Handlers.push_back(std::make_shared<PlayersHandler>(this));
    Handlers.push_back(std::make_shared<StatisticsHandler>(this));
    Handlers.push_back(std::make_shared<SettingsHandler>(this));
    Handlers.push_back(std::make_shared<MessageHandler>(this));
    Handlers.push_back(std::make_shared<BansHandler>(this));
    Handlers.push_back(std::make_shared<DebugStatisticsHandler>(this));
    Handlers.push_back(std::make_shared<ShardingHandler>(this));
}

WebUIService::~WebUIService()
{
}

bool WebUIService::Init()
{
    int Port = ServerInstance->GetConfig().WebUIServerPort;

    if (mg_init_library(MG_FEATURES_FILES) == 0)
    {
        ErrorS("WebUI", "Failed to initialize civitweb library.");
        return false;
    }

    std::filesystem::path StaticPath = std::filesystem::current_path() / "../../Source/WebUI/Static";
    if (!std::filesystem::exists(StaticPath))
    {
        StaticPath = std::filesystem::current_path() / "WebUI/Static";
    }
    if (!std::filesystem::exists(StaticPath))
    {
        ErrorS("WebUI", "Failed to find webui file-serving directory.");
        return false;
    }

    std::vector<std::string> Options;
    Options.push_back("document_root");
    Options.push_back(StaticPath.string());
    Options.push_back("listening_ports");
    Options.push_back(StringFormat("%i", Port));
    Options.push_back("num_threads");
    Options.push_back("5");
    // Disable browser caching of WebUI assets. The HTML and JS get
    // updated whenever the server is, and an admin re-opening the WebUI
    // after an upgrade should NOT see a stale page from cache. Set
    // static_file_max_age=0 (civetweb sends Cache-Control: max-age=0)
    // and additionally send a no-store header for full belt-and-braces
    // — Chrome will revalidate on every reload.
    Options.push_back("static_file_max_age");
    Options.push_back("0");
    Options.push_back("additional_header");
    Options.push_back("Cache-Control: no-store, no-cache, must-revalidate");

    // Force charset=utf-8 on text MIME types so non-ASCII characters in
    // index.html / main.js (em-dashes, accented strings, etc.) render
    // correctly. Without this civetweb sends Content-Type without a
    // charset parameter and browsers fall back to Latin-1 / Windows-
    // 1252, turning U+2014 into "â€\"" mojibake.
    Options.push_back("extra_mime_types");
    Options.push_back(
        ".html=text/html; charset=utf-8,"
        ".htm=text/html; charset=utf-8,"
        ".js=application/javascript; charset=utf-8,"
        ".css=text/css; charset=utf-8,"
        ".json=application/json; charset=utf-8");

    try
    {
        WebServer = std::make_shared<CivetServer>(Options);
    }
    catch (CivetException&)
    {
        ErrorS("WebUI", "Failed to create civet webserver.");
        return false;
    }

    for (auto Handler : Handlers)
    {
        Handler->Register(WebServer.get());
    }

    Log("WebUI service is now listening at http://localhost:%i/", Port);

    return true;
}

bool WebUIService::Term()
{
    if (!mg_exit_library())
    {
        return false;
    }

    return true;
}

void WebUIService::Poll()
{
    DebugTimerScope Scope(Debug::WebUIService_PollTime);

    ClearExpiredTokens();
    GatherData();
}

std::string WebUIService::GetName()
{
    return "WebUI";
}

bool WebUIService::CheckAuthToken(const std::string& Token)
{
    std::scoped_lock lock(StateMutex);

    return AuthTokens.find(Token) != AuthTokens.end();
}

std::string WebUIService::AddAuthToken()
{
    std::scoped_lock lock(StateMutex);

    std::vector<uint8_t> Bytes;
    Bytes.resize(64);
    FillRandomBytes(Bytes.data(), (int)Bytes.size());

    AuthToken NewToken;
    NewToken.Token = BytesToHex(Bytes);
    NewToken.ExpireTime = GetSeconds() + BuildConfig::WEBUI_AUTH_TIMEOUT;
    AuthTokens[NewToken.Token] = NewToken;

    return NewToken.Token;
}

bool WebUIService::IsAuthenticated(const mg_connection* Connection)
{
    std::scoped_lock lock(StateMutex);

    const char* AuthToken = mg_get_header(Connection, "Auth-Token");
    if (AuthToken == nullptr)
    {
        return false;
    }

    return CheckAuthToken(AuthToken);
}

void WebUIService::ClearExpiredTokens()
{
    std::scoped_lock lock(StateMutex);

    double Time = GetSeconds();
    for (auto iter = AuthTokens.begin(); iter != AuthTokens.end(); /* empty */)
    {
        if (Time > iter->second.ExpireTime)
        {
            iter = AuthTokens.erase(iter);
        }
        else
        {
            iter++;
        }
    }
}

void WebUIService::GatherData()
{
    for (auto Handler : Handlers)
    {
        if (Handler->NeedsDataGather())
        {
            Handler->GatherData();
        }
    }    
}
