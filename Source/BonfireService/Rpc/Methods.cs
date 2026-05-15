/*
 * RPC method registry. Each handler returns a JsonNode that becomes the
 * `result` of the response. Throw an Exception to surface an error.
 *
 * Conventions:
 *   - method names are dot-namespaced: <module>.<verb>
 *   - all params and results are snake_case (the Flutter side decodes
 *     into Dart camelCase via codegen / manual mapping)
 */

using System.Text.Json.Nodes;
using Bonfire.Service.Modules;
using Bonfire.Service.Util;

namespace Bonfire.Service.Rpc;

public static class Methods
{
    public static void Register(RpcServer server)
    {
        // ----- ping -----
        server.Register("ping", async (_, _) =>
        {
            await Task.Yield();
            return new JsonObject
            {
                ["pong"] = true,
                ["version"] = Bonfire.Service.Program.ServiceVersion,
            };
        });

        // ----- app updates -----
        server.Register("app.update_status", async (_, ct) =>
        {
            var status = await AppUpdater.QueryStatusAsync(ct);
            return status is null ? null : UpdateStatusJson(status);
        });

        server.Register("app.apply_update", async (@params, ct) =>
        {
            var uiPid = @params.GetInt("ui_pid") ?? 0;
            var status = await AppUpdater.StageAndLaunchAsync(uiPid, (recv, total) =>
            {
                _ = server.NotifyAsync("app.update_progress", new JsonObject
                {
                    ["phase"] = "downloading",
                    ["bytes_received"] = recv,
                    ["bytes_total"] = total,
                });
            }, ct);

            var result = UpdateStatusJson(status);
            result["restart_required"] = status.UpdateAvailable;
            return result;
        });

        // ----- network -----
        server.Register("network.detect_ips", async (_, ct) =>
        {
            var wan = await Network.GetPublicIpAsync(ct);
            var lan = Network.GetPrivateIp();
            return new JsonObject
            {
                ["wan"] = wan,
                ["lan"] = lan,
            };
        });

        static JsonObject UpdateStatusJson(AppUpdater.UpdateStatus status)
        {
            return new JsonObject
            {
                ["current_version"] = status.CurrentVersion,
                ["latest_version"] = status.LatestVersion,
                ["latest_tag"] = status.LatestTag,
                ["release_url"] = status.ReleaseUrl,
                ["asset_name"] = status.AssetName,
                ["asset_url"] = status.AssetUrl,
                ["asset_size"] = status.AssetSize,
                ["update_available"] = status.UpdateAvailable,
            };
        }

        // ----- firewall -----
        server.Register("firewall.status", async (_, _) =>
        {
            await Task.Yield();
            var rules = Firewall.QueryRules();
            return new JsonObject
            {
                ["all_installed"] = rules.All(r => r.Present),
                ["rules"] = new JsonArray(
                    rules.Select(r => (JsonNode?)new JsonObject
                    {
                        ["name"] = r.Name,
                        ["present"] = r.Present,
                    }).ToArray()),
            };
        });

        server.Register("firewall.apply", async (_, _) =>
        {
            // Run on a worker so the RPC loop isn't blocked while the user
            // approves UAC.
            var serverExe = Paths.ServerExecutable;
            // Loader.exe lives next to the service in packaged builds.
            var loaderExe = Path.Combine(Paths.InstallRoot, "Bonfire.exe");
            var ok = await Task.Run(() => Firewall.ApplyRulesElevated(serverExe, loaderExe));
            return new JsonObject { ["success"] = ok };
        });

        server.Register("firewall.remove", async (_, _) =>
        {
            var ok = await Task.Run(() => Firewall.RemoveRulesElevated());
            return new JsonObject { ["success"] = ok };
        });

        // ----- DS2 native runtime -----
        server.Register("ds2_runtime.status", async (@params, _) =>
        {
            await Task.Yield();
            var sessionId = @params.GetString("session_id");
            return Ds2RuntimeStatusJson(Ds2NativeRuntimeBridge.GetStatus(sessionId));
        });

        server.Register("ds2_runtime.command", async (@params, _) =>
        {
            await Task.Yield();
            var sessionId = @params.GetString("session_id");
            var command = @params.GetString("command")
                ?? throw new Exception("Missing DS2 native runtime command.");
            var payload = @params?["payload"];
            return Ds2RuntimeStatusJson(
                Ds2NativeRuntimeBridge.SendCommand(sessionId, command, payload));
        });

        static JsonObject Ds2RuntimeStatusJson(Ds2NativeRuntimeBridge.RuntimeStatus status)
        {
            return new JsonObject
            {
                ["installed"] = status.Installed,
                ["active"] = status.Active,
                ["session_id"] = status.SessionId,
                ["event_log"] = status.EventLog,
                ["command_inbox"] = status.CommandInbox,
                ["action_log"] = status.ActionLog,
                ["message_log"] = status.MessageLog,
                ["state_file"] = status.StateFile,
                ["last_event"] = status.LastEvent,
                ["last_action"] = status.LastAction,
                ["last_message"] = status.LastMessage,
                ["runtime_state"] = ParseOptionalJson(status.StateJson),
                ["last_write_utc"] = status.LastWriteUtc?.ToString("O"),
            };
        }

        static JsonNode? ParseOptionalJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                return JsonNode.Parse(json);
            }
            catch
            {
                return null;
            }
        }

        // ----- server install / lifecycle -----
        server.Register("server.is_installed", async (_, _) =>
        {
            await Task.Yield();
            return new JsonObject
            {
                ["installed"] = Paths.ServerInstalled,
                ["server_directory"] = Paths.ServerDirectory,
                ["install_root"] = Paths.InstallRoot,
            };
        });

        server.Register("server.fetch_latest_release", async (_, ct) =>
        {
            var info = await ReleaseDownloader.QueryLatestAsync(ct);
            if (info is null) return null;
            return new JsonObject
            {
                ["tag"] = info.TagName,
                ["url"] = info.AssetUrl,
                ["size"] = info.AssetSize,
            };
        });

        server.Register("server.install", async (@params, ct) =>
        {
            var url = @params.GetString("url");
            if (string.IsNullOrEmpty(url))
            {
                var info = await ReleaseDownloader.QueryLatestAsync(ct);
                url = info?.AssetUrl;
                if (string.IsNullOrEmpty(url))
                    throw new Exception("Could not resolve the latest release.");
            }

            // Stop any running server so we can overwrite files.
            ServerProcess.Stop();
            RelayTunnel.Stop();

            var zipPath = Path.Combine(Paths.InstallRoot, "_release.zip");
            var ok = await ReleaseDownloader.DownloadAsync(url, zipPath, (recv, total) =>
            {
                _ = server.NotifyAsync("download.progress", new JsonObject
                {
                    ["bytes_received"] = recv,
                    ["bytes_total"] = total,
                });
            }, ct);
            if (!ok)
                throw new Exception("Download failed.");

            var extracted = await Task.Run(() => ReleaseDownloader.Extract(zipPath, Paths.InstallRoot));
            try { File.Delete(zipPath); } catch { }
            if (!extracted)
                throw new Exception("Extraction failed.");

            return new JsonObject { ["success"] = true };
        });

        server.Register("server.start", async (_, ct) =>
        {
            await Task.Yield();
            // Make sure config exists; if not, prime it by running once briefly.
            if (!Paths.ConfigExists)
            {
                if (!ServerProcess.Start(out var startErr))
                    throw new Exception(startErr ?? "Could not generate default config.");
                for (int i = 0; i < 30 && !Paths.ConfigExists; i++)
                    await Task.Delay(150);
                ServerProcess.Stop();
                RelayTunnel.Stop();
            }

            var cfg = ServerConfig.Load(Paths.ConfigFile);
            if (cfg.RelayEnabled)
            {
                await RelayTunnel.StartAsync(cfg, Paths.ConfigFile, ct);
            }
            else
            {
                RelayTunnel.Stop();
            }

            if (!ServerProcess.Start(out var err))
            {
                RelayTunnel.Stop();
                throw new Exception(err ?? "Failed to start server.");
            }

            var status = ServerProcess.QueryStatus();
            var relay = RelayTunnel.QueryStatus();
            return new JsonObject
            {
                ["pid"] = status.Pid,
                ["started_at"] = status.StartedAt?.ToString("o"),
                ["relay_running"] = relay.Running,
                ["relay_hostname"] = relay.PublicHostname,
                ["relay_login_port"] = relay.LoginPort,
            };
        });

        server.Register("server.stop", async (_, _) =>
        {
            await Task.Run(ServerProcess.Stop);
            RelayTunnel.Stop();
            return new JsonObject { ["success"] = true };
        });

        server.Register("server.status", async (_, _) =>
        {
            await Task.Yield();
            var st = ServerProcess.QueryStatus();
            var relay = RelayTunnel.QueryStatus();
            return new JsonObject
            {
                ["running"] = st.Running,
                ["pid"] = st.Pid,
                ["started_at"] = st.StartedAt?.ToString("o"),
                ["relay_running"] = relay.Running,
                ["relay_hostname"] = relay.PublicHostname,
                ["relay_login_port"] = relay.LoginPort,
            };
        });

        // ----- server config (config.json) -----
        server.Register("server.config_get", async (_, _) =>
        {
            await Task.Yield();
            var cfg = ServerConfig.Load(Paths.ConfigFile);
            return new JsonObject
            {
                ["server_name"] = cfg.ServerName,
                ["server_description"] = cfg.ServerDescription,
                ["password"] = cfg.Password,
                ["game_type"] = cfg.GameType,
                ["server_hostname"] = cfg.ServerHostname,
                ["server_private_hostname"] = cfg.ServerPrivateHostname,
                ["relay_enabled"] = cfg.RelayEnabled,
                ["relay_public_hostname"] = cfg.RelayPublicHostname,
                ["relay_control_host"] = cfg.RelayControlHost,
                ["relay_control_port"] = cfg.RelayControlPort,
                ["relay_control_token"] = cfg.RelayControlToken,
                ["relay_login_port"] = cfg.RelayLoginServerPort,
                ["relay_auth_port"] = cfg.RelayAuthServerPort,
                ["relay_game_port"] = cfg.RelayGameServerPort,
                ["advertise"] = cfg.Advertise,
                ["webui_username"] = cfg.WebUIServerUsername,
                ["webui_password"] = cfg.WebUIServerPassword,
                ["exists"] = Paths.ConfigExists,
            };
        });

        server.Register("server.config_set", async (@params, _) =>
        {
            await Task.Yield();
            // If config.json doesn't exist yet, prime it by running Server.exe once.
            if (!Paths.ConfigExists)
            {
                if (!ServerProcess.Start(out var startErr))
                    throw new Exception(startErr ?? "Could not generate default config.");
                for (int i = 0; i < 30 && !Paths.ConfigExists; i++)
                    await Task.Delay(150);
                ServerProcess.Stop();
            }

            var current = ServerConfig.Load(Paths.ConfigFile);
            var cfg = new ServerConfig
            {
                ServerName = @params.GetString("server_name") ?? current.ServerName,
                ServerDescription = @params.GetString("server_description") ?? current.ServerDescription,
                Password = @params.GetString("password") ?? current.Password,
                GameType = @params.GetString("game_type") ?? current.GameType,
                ServerHostname = @params.GetString("server_hostname") ?? current.ServerHostname,
                ServerPrivateHostname = @params.GetString("server_private_hostname") ?? current.ServerPrivateHostname,
                RelayEnabled = @params.GetBool("relay_enabled") ?? current.RelayEnabled,
                RelayPublicHostname = current.RelayPublicHostname,
                RelayControlHost = @params.GetString("relay_control_host") ?? current.RelayControlHost,
                RelayControlPort = @params.GetInt("relay_control_port") ?? current.RelayControlPort,
                RelayControlToken = @params.GetString("relay_control_token") ?? current.RelayControlToken,
                RelayLoginServerPort = current.RelayLoginServerPort,
                RelayAuthServerPort = current.RelayAuthServerPort,
                RelayGameServerPort = current.RelayGameServerPort,
                Advertise = @params.GetBool("advertise") ?? current.Advertise,
                WebUIServerUsername = @params.GetString("webui_username") ?? current.WebUIServerUsername,
                WebUIServerPassword = @params.GetString("webui_password") ?? current.WebUIServerPassword,
            };
            if (!cfg.SaveOver(Paths.ConfigFile))
                throw new Exception($"Could not write config.json at {Paths.ConfigFile}");

            // The profile's display name IS the server's ServerName — keep
            // them in sync so the bonfire's name stays correct when the
            // profile is inactive (UI falls back to profile meta in that
            // case). Same for game type.
            try
            {
                var activeId = Profiles.GetActiveId();
                if (!string.IsNullOrEmpty(activeId))
                {
                    Profiles.Rename(activeId, cfg.ServerName);
                    if (!string.IsNullOrEmpty(cfg.GameType))
                        Profiles.SetGameType(activeId, cfg.GameType);
                }
            }
            catch { /* best effort */ }

            return new JsonObject { ["success"] = true };
        });

        // ----- server uninstall / reset -----
        server.Register("server.uninstall", async (@params, _) =>
        {
            // Kill the running server first so file locks release.
            ServerProcess.Stop();
            RelayTunnel.Stop();
            await Task.Delay(300);

            var keepConfig = @params.GetBool("keep_config") ?? false;
            try
            {
                if (Directory.Exists(Paths.ServerDirectory))
                {
                    if (keepConfig && File.Exists(Paths.ConfigFile))
                    {
                        // Move config to a backup before nuking the folder.
                        var backup = Path.Combine(Paths.InstallRoot,
                            "config.backup.json");
                        File.Copy(Paths.ConfigFile, backup, true);
                    }
                    Directory.Delete(Paths.ServerDirectory, recursive: true);
                }
                return new JsonObject { ["success"] = true };
            }
            catch (Exception ex)
            {
                throw new Exception("Could not delete Server folder: " + ex.Message);
            }
        });

        server.Register("server.reset_config", async (_, _) =>
        {
            // Stop server, delete config.json so the next start regenerates
            // the default. Also wipes the keypair (regenerated on next launch).
            ServerProcess.Stop();
            RelayTunnel.Stop();
            await Task.Delay(300);
            try
            {
                if (File.Exists(Paths.ConfigFile)) File.Delete(Paths.ConfigFile);
                var saved = Path.GetDirectoryName(Paths.ConfigFile);
                if (saved is not null)
                {
                    foreach (var f in new[] { "private.key", "public.key", "database.sqlite" })
                    {
                        var p = Path.Combine(saved, f);
                        if (File.Exists(p))
                        {
                            try { File.Delete(p); } catch { }
                        }
                    }
                }
                return new JsonObject { ["success"] = true };
            }
            catch (Exception ex)
            {
                throw new Exception("Could not reset config: " + ex.Message);
            }
        });

        // ----- profiles (multi-server vault) -----
        server.Register("profiles.list", async (_, _) =>
        {
            await Task.Yield();
            var list = Profiles.List();
            var activeId = Profiles.GetActiveId();
            return new JsonObject
            {
                ["active_id"] = activeId,
                ["profiles"] = new JsonArray(list.Select(p => (JsonNode?)new JsonObject
                {
                    ["id"] = p.Id,
                    ["name"] = p.Name,
                    ["game_type"] = p.GameType,
                    ["created_at"] = p.CreatedAt,
                    ["is_active"] = p.Id == activeId,
                }).ToArray()),
            };
        });

        server.Register("profiles.create", async (@params, _) =>
        {
            await Task.Yield();
            var name = @params.GetString("name") ?? "New bonfire";
            var gameType = @params.GetString("game_type") ?? "DarkSouls2";
            var meta = Profiles.Create(name, gameType);
            return new JsonObject
            {
                ["id"] = meta.Id,
                ["name"] = meta.Name,
                ["game_type"] = meta.GameType,
            };
        });

        server.Register("profiles.rename", async (@params, _) =>
        {
            await Task.Yield();
            var id = @params.GetString("id") ?? throw new Exception("id required");
            var name = @params.GetString("name") ?? throw new Exception("name required");
            Profiles.Rename(id, name);
            return new JsonObject { ["success"] = true };
        });

        server.Register("profiles.set_game_type", async (@params, _) =>
        {
            await Task.Yield();
            var id = @params.GetString("id") ?? throw new Exception("id required");
            var gt = @params.GetString("game_type") ?? throw new Exception("game_type required");
            Profiles.SetGameType(id, gt);
            return new JsonObject { ["success"] = true };
        });

        server.Register("profiles.delete", async (@params, _) =>
        {
            var id = @params.GetString("id") ?? throw new Exception("id required");
            // If deleting the active profile, stop server first to release file locks.
            if (Profiles.GetActiveId() == id)
            {
                ServerProcess.Stop();
                RelayTunnel.Stop();
                await Task.Delay(300);
            }
            Profiles.Delete(id);
            return new JsonObject { ["success"] = true };
        });

        server.Register("profiles.activate", async (@params, _) =>
        {
            var id = @params.GetString("id") ?? throw new Exception("id required");
            // Save the current live state into whatever profile is active so
            // edits made between activations don't get lost.
            var current = Profiles.GetActiveId();
            ServerProcess.Stop();
            RelayTunnel.Stop();
            await Task.Delay(300);
            if (!string.IsNullOrEmpty(current) && current != id)
            {
                try { Profiles.SaveActiveStateTo(current); } catch { }
            }
            Profiles.Activate(id);
            return new JsonObject { ["success"] = true };
        });

        // ----- master server (public list) -----
        server.Register("servers.list", async (@params, ct) =>
        {
            var gameTypeFilter = @params.GetString("game_type"); // optional
            var list = await MasterServer.ListServersAsync(ct);
            if (list is null)
            {
                return new JsonObject
                {
                    ["available"] = false,
                    ["servers"] = new JsonArray(),
                };
            }

            var filtered = gameTypeFilter is null
                ? list
                : list.Where(s => string.Equals(s.GameType, gameTypeFilter, StringComparison.OrdinalIgnoreCase)).ToList();

            return new JsonObject
            {
                ["available"] = true,
                ["servers"] = new JsonArray(filtered.Select(s => (JsonNode?)new JsonObject
                {
                    ["id"] = s.Id,
                    ["name"] = s.Name,
                    ["description"] = s.Description,
                    ["game_type"] = s.GameType,
                    ["hostname"] = s.Hostname,
                    ["private_hostname"] = s.PrivateHostname,
                    ["ip_address"] = s.IpAddress,
                    ["port"] = s.Port,
                    ["player_count"] = s.PlayerCount,
                    ["password_required"] = s.PasswordRequired,
                    ["allow_sharding"] = s.AllowSharding,
                    ["is_shard"] = s.IsShard,
                    ["is_relayed"] = s.IsRelayed,
                    ["mods_whitelist"] = s.ModsWhiteList,
                    ["mods_blacklist"] = s.ModsBlackList,
                    ["mods_required"] = s.ModsRequiredList,
                    ["web_address"] = s.WebAddress,
                }).ToArray()),
            };
        });

        server.Register("servers.public_key", async (@params, ct) =>
        {
            var id = @params.GetString("id") ?? throw new Exception("id required");
            var password = @params.GetString("password") ?? "";
            var key = await MasterServer.GetPublicKeyAsync(id, password, ct);
            return new JsonObject { ["public_key"] = key };
        });

        // ----- game (settings + launch) -----
        server.Register("game.get_settings", async (_, _) =>
        {
            await Task.Yield();
            var s = GameSettings.Load();
            // "Valid" means: exists on disk AND its hash is in BuildConfig
            // (i.e. the patcher knows where to put the server info). The
            // original Loader's launch button used the same combined check.
            static bool IsRecognised(string path) =>
                !string.IsNullOrEmpty(path) && File.Exists(path) &&
                Loader.BuildConfig.ExeLoadConfiguration.ContainsKey(
                    Loader.ExeUtils.GetExeSimpleHash(path));
            return new JsonObject
            {
                ["ds1_exe_path"] = s.Ds1ExePath,
                ["ds2_exe_path"] = s.Ds2ExePath,
                ["ds3_exe_path"] = s.Ds3ExePath,
                ["ds1_exe_valid"] = IsRecognised(s.Ds1ExePath),
                ["ds2_exe_valid"] = IsRecognised(s.Ds2ExePath),
                ["ds3_exe_valid"] = IsRecognised(s.Ds3ExePath),
                ["ds1_seamless_path"] = s.Ds1SeamlessPath,
                ["ds1_seamless_enabled"] = s.EnableDs1Seamless,
                ["ds1_seamless_valid"] = string.IsNullOrWhiteSpace(s.Ds1SeamlessPath)
                    ? Bonfire.Service.Modules.Ds1SeamlessPayloadResolver.Resolve("", s.Ds1ExePath) is not null
                    : Bonfire.Service.Modules.Ds1SeamlessPayloadResolver.IsValidPath(s.Ds1SeamlessPath),
                ["ds2_overhaul_path"] = s.Ds2OverhaulPath,
                ["ds2_overhaul_valid"] = string.IsNullOrWhiteSpace(s.Ds2OverhaulPath) ||
                    !string.IsNullOrEmpty(Loader.Ds2ModEngineSettings.Resolve(
                        s.Ds2ExePath, "", "DarkSouls2", s.Ds2OverhaulPath).ModOverrideDirectory),
                ["ds3_seamless_path"] = s.Ds3SeamlessPath,
                ["ds3_seamless_enabled"] = s.EnableDs3Seamless,
                ["ds3_seamless_valid"] = string.IsNullOrWhiteSpace(s.Ds3SeamlessPath)
                    ? Bonfire.Service.Modules.Ds3SeamlessPayloadResolver.Resolve("", s.Ds3ExePath) is not null
                    : Bonfire.Service.Modules.Ds3SeamlessPayloadResolver.IsValidPath(s.Ds3SeamlessPath),
                ["use_separate_saves"] = s.UseSeparateSaves,
            };
        });

        server.Register("game.set_exe_path", async (@params, _) =>
        {
            await Task.Yield();
            var gameType = @params.GetString("game_type") ?? "";
            var path = @params.GetString("path") ?? "";
            if (!File.Exists(path))
                throw new Exception("File does not exist: " + path);

            var s = GameSettings.Load();
            if (string.Equals(gameType, "DarkSouls1", StringComparison.OrdinalIgnoreCase))
                s.Ds1ExePath = path;
            else if (string.Equals(gameType, "DarkSouls3", StringComparison.OrdinalIgnoreCase))
                s.Ds3ExePath = path;
            else
                s.Ds2ExePath = path;
            s.Save();

            // Sanity check: hash known?
            var hash = Loader.ExeUtils.GetExeSimpleHash(path);
            var known = Loader.BuildConfig.ExeLoadConfiguration.ContainsKey(hash);
            return new JsonObject
            {
                ["ok"] = true,
                ["path"] = path,
                ["recognised"] = known,
                ["hash"] = hash,
            };
        });

        server.Register("game.set_ds2_overhaul_path", async (@params, _) =>
        {
            await Task.Yield();
            var path = @params.GetString("path") ?? "";
            if (!string.IsNullOrWhiteSpace(path))
            {
                var resolved = Loader.Ds2ModEngineSettings.Resolve(
                    "", "", "DarkSouls2", path);
                if (string.IsNullOrEmpty(resolved.ModOverrideDirectory))
                    throw new Exception("Not a valid DS2 overhaul directory: " + path);
                path = resolved.ModOverrideDirectory;
            }

            var s = GameSettings.Load();
            s.Ds2OverhaulPath = path;
            s.Save();
            return new JsonObject
            {
                ["ok"] = true,
                ["path"] = path,
            };
        });

        server.Register("game.set_ds1_seamless_path", async (@params, _) =>
        {
            await Task.Yield();
            var path = @params.GetString("path") ?? "";
            if (!string.IsNullOrWhiteSpace(path) &&
                !Bonfire.Service.Modules.Ds1SeamlessPayloadResolver.IsValidPath(path))
            {
                throw new Exception("Not a valid DS1 Seamless payload directory: " + path);
            }

            var s = GameSettings.Load();
            s.Ds1SeamlessPath = path;
            s.Save();
            return new JsonObject
            {
                ["ok"] = true,
                ["path"] = path,
            };
        });

        server.Register("game.set_ds1_seamless_enabled", async (@params, _) =>
        {
            await Task.Yield();
            var s = GameSettings.Load();
            s.EnableDs1Seamless = @params.GetBool("enabled") ?? true;
            s.Save();
            return new JsonObject
            {
                ["ok"] = true,
                ["enabled"] = s.EnableDs1Seamless,
            };
        });

        server.Register("game.set_ds3_seamless_path", async (@params, _) =>
        {
            await Task.Yield();
            var path = @params.GetString("path") ?? "";
            if (!string.IsNullOrWhiteSpace(path) &&
                !Bonfire.Service.Modules.Ds3SeamlessPayloadResolver.IsValidPath(path))
            {
                throw new Exception("Not a valid DS3 Seamless payload directory: " + path);
            }

            var s = GameSettings.Load();
            s.Ds3SeamlessPath = path;
            s.Save();
            return new JsonObject
            {
                ["ok"] = true,
                ["path"] = path,
            };
        });

        server.Register("game.set_ds3_seamless_enabled", async (@params, _) =>
        {
            await Task.Yield();
            var s = GameSettings.Load();
            s.EnableDs3Seamless = @params.GetBool("enabled") ?? true;
            s.Save();
            return new JsonObject
            {
                ["ok"] = true,
                ["enabled"] = s.EnableDs3Seamless,
            };
        });

        server.Register("game.steam_status", async (_, _) =>
        {
            await Task.Yield();
            // The upstream Loader only exposes a combined "running and logged
            // in" check — that's also the only state where launching works,
            // so we surface just one boolean.
            return new JsonObject
            {
                ["ok"] = Loader.SteamUtils.IsSteamRunningAndLoggedIn(),
            };
        });

        server.Register("game.launch", async (@params, ct) =>
        {
            // Required: server_id (master server lookup), game_type, password (maybe)
            var serverId = @params.GetString("server_id")
                           ?? throw new Exception("server_id required");
            var password = @params.GetString("password") ?? "";
            var loopback = @params.GetBool("loopback") ?? false; // for "my server" launches

            // Pull server metadata from master server.
            var list = await MasterServer.ListServersAsync(ct);
            var serverEntry = list?.FirstOrDefault(s => s.Id == serverId);
            if (serverEntry is null)
                throw new Exception("Server not found in master server list.");

            // Fetch public key. Normalise line endings (LF only) — same
            // reason as in game.launch_local: the C++ injector's
            // DS3_ReplaceServerAddressHook only matches at a fixed
            // byte-length (426 for 2048-bit LF). Master HTTP normally
            // returns LF, but be defensive in case a future master
            // implementation echoes whatever the server sent (CRLF).
            var publicKey = (await MasterServer.GetPublicKeyAsync(serverId, password, ct))
                ?.Replace("\r\n", "\n");
            if (string.IsNullOrEmpty(publicKey))
                throw new Exception("Master server refused to give the public key (wrong password?).");

            // Game settings.
            var settings = GameSettings.Load();
            var exePath = settings.PathFor(serverEntry.GameType);
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                throw new Exception(
                    $"No valid {serverEntry.GameType} game executable configured. " +
                    "Set the path in Game Settings.");

            // Steam check — game won't auth without it.
            if (!Loader.SteamUtils.IsSteamRunningAndLoggedIn())
                throw new Exception("Steam isn't running or you're not logged in.");

            // Detect machine IPs (used for LAN/loopback resolution).
            var wan = await Network.GetPublicIpAsync(ct) ?? "";
            var lan = Network.GetPrivateIp() ?? "";

            // Find Injector.dll. In packaged builds it lives in <root>/Loader/.
            var injectorPath = Path.Combine(Paths.InstallRoot, "Loader", "Injector.dll");
            if (!File.Exists(injectorPath))
            {
                // Fallback: same dir as service.
                injectorPath = Path.Combine(Paths.ServiceDirectory, "Injector.dll");
            }

            var req = new Bonfire.Service.Game.LaunchRequest(
                ExePath: exePath,
                ServerId: serverId,
                ServerName: serverEntry.Name,
                Hostname: serverEntry.Hostname,
                PrivateHostname: serverEntry.PrivateHostname,
                Port: serverEntry.Port,
                PublicKey: publicKey,
                GameType: serverEntry.GameType,
                EnableSeparateSaves: settings.UseSeparateSaves,
                Ds2OverhaulPath: settings.Ds2OverhaulPath,
                EnableDs1Seamless: settings.EnableDs1Seamless,
                Ds1SeamlessPath: settings.Ds1SeamlessPath,
                EnableDs3Seamless: settings.EnableDs3Seamless,
                Ds3SeamlessPath: settings.Ds3SeamlessPath);

            // Heavy P/Invoke — push it onto a worker.
            var result = await Task.Run(() => Bonfire.Service.Game.GameLauncher.Launch(
                req, wan, lan, injectorPath));

            if (!result.Ok)
                throw new Exception(result.Message);

            return new JsonObject
            {
                ["ok"] = true,
                ["pid"] = result.Pid,
                ["server_name"] = serverEntry.Name,
            };
        });

        // ----- game.launch_local — for player's own profiles -----
        server.Register("game.launch_local", async (@params, ct) =>
        {
            var profileId = @params.GetString("profile_id")
                            ?? throw new Exception("profile_id required");

            var meta = Profiles.List().FirstOrDefault(p => p.Id == profileId)
                       ?? throw new Exception("Profile not found.");
            var isDs1 = string.Equals(
                meta.GameType,
                "DarkSouls1",
                StringComparison.OrdinalIgnoreCase);

            // Game settings.
            var settings = GameSettings.Load();
            var exePath = settings.PathFor(meta.GameType);
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                throw new Exception(
                    $"No valid {meta.GameType} game executable configured. " +
                    "Set the path in Game Settings.");

            if (!Loader.SteamUtils.IsSteamRunningAndLoggedIn())
                throw new Exception("Steam isn't running or you're not logged in.");

            // Activate this profile if it isn't already (saves current state,
            // copies new files into Server/Saved/default).
            //
            // We also re-activate when the active profile IS this one but
            // the live config drifted away from the profile metadata
            // (e.g. Server.exe wrote its compile-time defaults on first
            // start and stamped GameType=DarkSouls3 even though the meta
            // says DarkSouls2). When that happens Server.exe is running in
            // the wrong protocol and the game gets a "server unavailable"
            // back from the auth flow. Force a clean re-activation +
            // restart so the running Server matches the profile.
            var currentActive = Profiles.GetActiveId();
            var liveCfg0 = ServerConfig.Load(Paths.ConfigFile);
            var gameTypeMatches = string.Equals(
                liveCfg0.GameType, meta.GameType,
                StringComparison.OrdinalIgnoreCase);
            if (currentActive != profileId || !gameTypeMatches)
            {
                ServerProcess.Stop();
                RelayTunnel.Stop();
                await Task.Delay(300);
                if (!string.IsNullOrEmpty(currentActive) && currentActive != profileId)
                {
                    try { Profiles.SaveActiveStateTo(currentActive); } catch { }
                }
                Profiles.Activate(profileId);
            }

            // Make sure the server is running (it's the source of truth for
            // its public key — generated on first start if missing).
            // DS1 uses Server.exe as Bonfire's coordinator around DS1SeamlessCoop,
            // so restart it on local launches to avoid stale invisible state.
            if (isDs1 && ServerProcess.QueryStatus().Running)
            {
                ServerProcess.Stop();
                RelayTunnel.Stop();
                await Task.Delay(300, ct);
            }

            if (!ServerProcess.QueryStatus().Running)
            {
                var preStartCfg = ServerConfig.Load(Paths.ConfigFile);
                if (preStartCfg.RelayEnabled)
                {
                    await RelayTunnel.StartAsync(preStartCfg, Paths.ConfigFile, ct);
                }
                else
                {
                    RelayTunnel.Stop();
                }

                if (!ServerProcess.Start(out var startErr, forceShellConsole: isDs1))
                {
                    RelayTunnel.Stop();
                    throw new Exception(startErr ?? "Could not start local server.");
                }
            }

            // Wait for the public key to appear (up to 5s) — Server.exe
            // writes it during startup if it doesn't exist yet.
            var pubKeyPath = Path.Combine(Paths.SavedDirectory, "public.key");
            for (int i = 0; i < 50 && !File.Exists(pubKeyPath); i++)
                await Task.Delay(100, ct);
            if (!File.Exists(pubKeyPath))
                throw new Exception("Server's public key did not appear within 5s.");

            // CRITICAL: normalise line endings to LF only.
            //
            // Server.exe writes public.key via OpenSSL's PEM_write_bio which
            // produces \r\n line endings on Windows (writes ASCII text mode
            // → 434 bytes for a 2048-bit RSA key). The C++ injector's
            // DS3_ReplaceServerAddressHook only triggers when the inserted
            // PEM block size matches a specific length (426 bytes for the
            // retail LF-encoded key) — see DS3_ReplaceServerAddressHook.cpp,
            // k_key_length. With CRLF the size is 434 → mismatch → the
            // server-address replacement NEVER FIRES, the game encrypts its
            // handshake with the retail public key, and the local server
            // can't decrypt it — manifests as DS3 "Error al iniciar sesión"
            // / DS2 equivalent.
            //
            // The public-server flow goes through the master HTTP API which
            // happens to return LF-only because of how Node serialises the
            // string, which is why public-server launches worked but local
            // ones didn't. Fix: collapse \r\n → \n unconditionally.
            var publicKey = (await File.ReadAllTextAsync(pubKeyPath, ct))
                .Replace("\r\n", "\n");

            // Snapshot the freshly generated keypair back into the profile so
            // future activations have it ready.
            try { Profiles.SaveActiveStateTo(profileId); } catch { }

            // Read the live config to know which port etc to connect to.
            // Use the config's LoginServerPort rather than hardcoding 50050,
            // so an admin who shifted the port (or a sharded deployment)
            // still works. Loader passes Config.Port from the master entry
            // for the same reason.
            var cfg = ServerConfig.Load(Paths.ConfigFile);
            var port = cfg.LoginServerPort > 0 ? cfg.LoginServerPort : 50050;

            // Detect IPs — used to decide loopback vs LAN vs WAN.
            var wan = await Network.GetPublicIpAsync(ct) ?? "";
            var lan = Network.GetPrivateIp() ?? "";

            var injectorPath = Path.Combine(Paths.InstallRoot, "Loader", "Injector.dll");
            if (!File.Exists(injectorPath))
                injectorPath = Path.Combine(Paths.ServiceDirectory, "Injector.dll");

            var req = new Bonfire.Service.Game.LaunchRequest(
                ExePath: exePath,
                ServerId: !string.IsNullOrWhiteSpace(cfg.ServerId) ? cfg.ServerId : profileId,
                ServerName: meta.Name,
                Hostname: !string.IsNullOrEmpty(cfg.ServerHostname) ? cfg.ServerHostname : (wan.Length > 0 ? wan : "127.0.0.1"),
                PrivateHostname: !string.IsNullOrEmpty(cfg.ServerPrivateHostname) ? cfg.ServerPrivateHostname : (lan.Length > 0 ? lan : "127.0.0.1"),
                Port: port,
                PublicKey: publicKey,
                GameType: meta.GameType,
                EnableSeparateSaves: settings.UseSeparateSaves,
                Ds2OverhaulPath: settings.Ds2OverhaulPath,
                EnableDs1Seamless: settings.EnableDs1Seamless,
                Ds1SeamlessPath: settings.Ds1SeamlessPath,
                EnableDs3Seamless: settings.EnableDs3Seamless,
                Ds3SeamlessPath: settings.Ds3SeamlessPath);

            var result = await Task.Run(() => Bonfire.Service.Game.GameLauncher.Launch(
                req, wan, lan, injectorPath));

            if (!result.Ok)
                throw new Exception(result.Message);

            return new JsonObject
            {
                ["ok"] = true,
                ["pid"] = result.Pid,
                ["server_name"] = meta.Name,
            };
        });

        // ----- shutdown -----
        server.Register("service.shutdown", async (_, _) =>
        {
            await Task.Yield();
            // Best-effort: stop server child first.
            ServerProcess.Stop();
            RelayTunnel.Stop();
            // Actual exit happens after the response is written.
            _ = Task.Run(async () =>
            {
                await Task.Delay(100);
                Environment.Exit(0);
            });
            return new JsonObject { ["bye"] = true };
        });
    }
}
