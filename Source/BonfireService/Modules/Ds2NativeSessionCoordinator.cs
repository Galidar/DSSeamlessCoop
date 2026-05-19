using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using Bonfire.Service.Rpc;

namespace Bonfire.Service.Modules;

/// <summary>
/// Watches the DS2 injected runtime action log and turns in-game Bonfire item
/// commands into service-side session control. This is intentionally a small
/// control plane: the game emits item actions, BonfireService owns server
/// lifecycle and UI notifications.
/// </summary>
public static class Ds2NativeSessionCoordinator
{
    private static readonly object Lock = new();
    private static readonly Dictionary<string, int> ProcessedLineCounts =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, SessionMemory> Sessions =
        new(StringComparer.OrdinalIgnoreCase);
    private static CancellationTokenSource? _cts;
    private static Task? _worker;
    private static RpcServer? _server;
    private static readonly HashSet<string> MutedLanInviteSessionIds =
        new(StringComparer.OrdinalIgnoreCase);
    private static string _lastPushedLanInviteSessionId = "";
    private static DateTime _lastPushedLanInviteUtc = DateTime.MinValue;

    private static string Root => Path.Combine(Paths.InstallRoot, "Runtime", "DS2Native");

    public static void Start(RpcServer server)
    {
        lock (Lock)
        {
            if (_worker is not null)
                return;

            _server = server;
            _cts = new CancellationTokenSource();
            PrimeExistingActionLogs();
            // Proactively populate WebUI credentials in config.json so the
            // live-manifest push can authenticate against Server.exe's
            // /settings endpoint. Server.exe only auto-generates these on
            // first boot of a non-default shard; a single-profile install
            // would otherwise stay unauthenticated forever and live-push
            // would fall back to the disk-stamp + stale-flag path on every
            // item use. The new credentials take effect on the NEXT
            // Server.exe boot — already-running servers keep the empty
            // credentials they cached at startup.
            try { Ds2NativeWebUIPush.EnsureCredentialsInConfig(); } catch { }
            _worker = Task.Run(() => WorkerLoopAsync(_cts.Token));
        }
    }

    public static void Stop()
    {
        lock (Lock)
        {
            try { _cts?.Cancel(); } catch { }
            _cts = null;
            _worker = null;
            _server = null;
        }
    }

    private static void PrimeExistingActionLogs()
    {
        try
        {
            if (!Directory.Exists(Root))
                return;

            foreach (var path in Directory.EnumerateFiles(Root, "*.actions.jsonl"))
            {
                ProcessedLineCounts[path] = ReadSharedLines(path).Count;
            }
        }
        catch
        {
        }
    }

    private static async Task WorkerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                ProcessActionLogs(ct);
                // Phase 4d follow-up (v2.8.3): keep the pose bridge
                // running whenever DS2 is alive and the injector is
                // emitting resolved poses, even if the user never
                // used the in-game host/guest orb. Role is decided
                // from whether a JoinTarget is armed.
                TryAutoStartPoseBridgeFromHeartbeat();
                TryPushLanInvitePromptToRuntime();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                await NotifyAsync("ds2_runtime.session_error", new JsonObject
                {
                    ["time_utc"] = DateTime.UtcNow.ToString("O"),
                    ["error"] = ex.Message,
                });
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static void ProcessActionLogs(CancellationToken ct)
    {
        if (!Directory.Exists(Root))
            return;

        foreach (var path in Directory.EnumerateFiles(Root, "*.actions.jsonl"))
        {
            ct.ThrowIfCancellationRequested();

            var lines = ReadSharedLines(path);
            var previousCount = ProcessedLineCounts.TryGetValue(path, out var count)
                ? count
                : 0;
            if (previousCount > lines.Count)
                previousCount = 0;

            for (var index = previousCount; index < lines.Count; index++)
            {
                var line = lines[index];
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                ProcessActionLine(path, line, index + 1);
            }

            ProcessedLineCounts[path] = lines.Count;
        }
    }

    private static List<string> ReadSharedLines(string path)
    {
        var lines = new List<string>();
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    private static void ProcessActionLine(string actionLog, string line, int lineNumber)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(line);
        }
        catch
        {
            return;
        }

        if (node is not JsonObject action)
            return;

        var command = GetString(action, "command");
        if (string.IsNullOrWhiteSpace(command))
            return;

        var sessionId = GetString(action, "session_id");
        if (string.IsNullOrWhiteSpace(sessionId))
            sessionId = SessionIdFromActionLog(actionLog);

        var memory = GetSession(sessionId);
        var state = ApplyAction(memory, action, command, actionLog, lineNumber);
        WriteServiceState(sessionId, state);
        _ = NotifyAsync("ds2_runtime.session", state);
    }

    private static JsonObject ApplyAction(
        SessionMemory memory,
        JsonObject action,
        string command,
        string actionLog,
        int lineNumber)
    {
        var now = DateTime.UtcNow;
        memory.LastCommand = command;
        memory.LastActionUtc = now;
        memory.ActionCount++;

        var itemId = GetInt(action, "item_id");
        var runtimeName = GetString(action, "runtime_name");
        var data = action["data"] as JsonObject;
        var messageEn = GetString(data, "message_en");
        var messageEs = GetString(data, "message_es");
        var behaviorPhase = GetString(data, "behavior_phase");
        var onlineIntent = GetString(data, "online_intent");

        var serverEffect = new JsonObject();
        switch (command)
        {
            case "session.create":
                memory.SessionOpen = true;
                memory.Mode = "host";
                memory.Stage = "service_host_online";
                memory.OnlineIntent = "cooperate_host";
                // Stamp the manifest into config.json BEFORE Start() so a
                // fresh Server.exe boot reads the new advertisement instead
                // of whatever ServerDescription was on disk before this
                // session opened.
                var preStartStamp = StampManifestIfChanged(memory);
                serverEffect = EnsureLocalServerRunning(memory);
                serverEffect["manifest_pre_start"] = preStartStamp;
                // Phase 4d (HKMP overlay): start the pose bridge in
                // host mode — listen on UDP 50031, no initial peers.
                // Guests' first packet auto-registers their IP, so we
                // don't need a separate signalling channel.
                serverEffect["pose_bridge_started"] = TryStartPoseBridgeAsHost();
                break;

            case "session.join":
                memory.SessionOpen = true;
                memory.Mode = "guest";
                memory.Stage = "service_guest_armed";
                memory.OnlineIntent = "cooperate_join";
                serverEffect["status"] = "guest_link_armed";
                serverEffect["note"] =
                    "Join requests are armed in Bonfire state; connect to a Bonfire host before launch for the current DS2 network stack.";
                // Phase 4d: start the pose bridge in guest mode —
                // initial peer is the host's IP from the armed join
                // target. Once the guest sends a packet to the host,
                // the host auto-discovers the guest's IP via the
                // listener and the link is bidirectional.
                serverEffect["pose_bridge_started"] = TryStartPoseBridgeAsGuest();
                break;

            case "invite.accepted":
                memory.SessionOpen = true;
                memory.Mode = "guest";
                memory.Stage = "service_direct_invite_accepted";
                memory.OnlineIntent = "cooperate_join";
                serverEffect = ArmJoinTargetFromInviteAction(data, memory.SessionId);
                serverEffect["pose_bridge_started"] = TryStartPoseBridgeAsGuest();
                serverEffect["network_note"] =
                    "Invite accepted in DS2. Bonfire will relaunch DS2 directly into the host session without a launcher accept button.";
                break;

            case "invite.dismissed":
                memory.SessionOpen = false;
                memory.Mode = "solo";
                memory.Stage = "service_direct_invite_dismissed";
                memory.OnlineIntent = "cooperate_invite_dismiss";
                MuteLanInviteSession(GetString(data, "invite_session_id"));
                serverEffect["status"] = "invite_dismissed_in_game";
                break;

            case "session.invade":
                memory.SessionOpen = true;
                memory.Mode = "invader";
                memory.Stage = "service_private_invasion_armed";
                memory.OnlineIntent = "invade_private_world";
                serverEffect["status"] = "private_invasion_armed";
                serverEffect["server_running"] = ServerProcess.QueryStatus().Running;
                break;

            case "session.leave":
                memory.SessionOpen = false;
                memory.Mode = "solo";
                memory.Stage = "service_session_closed";
                memory.OnlineIntent = "close_session";
                serverEffect = CloseRuntimeStartedServer(memory);
                // Phase 4d: tear down the pose bridge so no more
                // UDP packets fly after the session is closed.
                try { Ds2NativePoseBridge.Stop(); } catch { }
                serverEffect["pose_bridge_stopped"] = true;
                // Plan v3 Track A: stop advertising on the LAN so
                // guests stop seeing the session in their beacon
                // cache. Listener stays up so we can still pick up
                // other hosts' beacons after closing our own.
                try { Ds2LanBeacon.StopHostBroadcast(); } catch { }
                serverEffect["lan_beacon_stopped"] = true;
                break;

            case "rules.cycle":
                memory.Stage = "service_rules_updated";
                memory.OnlineIntent = "set_rules";
                memory.RulePreset = GetString(data?["rules"] as JsonObject, "preset_name");
                serverEffect["status"] = "rules_recorded";
                break;

            case "invasions.taunt":
                memory.Stage = "service_invasion_beacon";
                memory.OnlineIntent = "open_invaders";
                memory.TauntCount++;
                serverEffect["status"] = "invasion_beacon_recorded";
                serverEffect["taunt_count"] = memory.TauntCount;
                break;

            case "world.infection":
                memory.Stage = "service_world_mutation";
                memory.OnlineIntent = "mutate_world";
                memory.InfectionCount++;
                serverEffect["status"] = "world_mutation_recorded";
                serverEffect["infection_count"] = memory.InfectionCount;
                break;

            case "curse.accrue":
                memory.Stage = "service_curse_pressure";
                memory.OnlineIntent = "escalate_curse";
                memory.CurseCount++;
                serverEffect["status"] = "curse_pressure_recorded";
                serverEffect["curse_count"] = memory.CurseCount;
                break;

            case "world.recover":
                memory.Stage = "service_world_recovery";
                memory.OnlineIntent = "recover_world";
                memory.RecoveryCount++;
                serverEffect["status"] = "recovery_recorded";
                serverEffect["recovery_count"] = memory.RecoveryCount;
                break;

            default:
                memory.Stage = string.IsNullOrWhiteSpace(behaviorPhase)
                    ? "service_action_seen"
                    : behaviorPhase;
                memory.OnlineIntent = string.IsNullOrWhiteSpace(onlineIntent)
                    ? "none"
                    : onlineIntent;
                serverEffect["status"] = "recorded";
                break;
        }

        // Final stamp captures any post-switch state into the advertised
        // manifest. For session.create this is usually a no-op (the pre-Start
        // stamp covered it); for the other verbs it's the primary stamp call.
        var finalStamp = StampManifestIfChanged(memory);
        serverEffect["manifest_stamp"] = finalStamp;

        var serverStatus = ServerProcess.QueryStatus();
        var cfg = ServerConfig.Load(Paths.ConfigFile);
        var advertisedManifestNode = string.IsNullOrEmpty(memory.LastStampedManifestJson)
            ? null
            : Ds2NativeSession.TryParseManifestJson(memory.LastStampedManifestJson)?.ToJson();
        var state = new JsonObject
        {
            ["time_utc"] = now.ToString("O"),
            ["session_id"] = memory.SessionId,
            ["session_open"] = memory.SessionOpen,
            ["session_mode"] = memory.Mode,
            ["service_stage"] = memory.Stage,
            ["online_intent"] = memory.OnlineIntent,
            ["last_command"] = memory.LastCommand,
            ["last_item_id"] = itemId,
            ["last_runtime_name"] = runtimeName,
            ["last_message_en"] = messageEn,
            ["last_message_es"] = messageEs,
            ["action_count"] = memory.ActionCount,
            ["rule_preset"] = memory.RulePreset,
            ["recovery_count"] = memory.RecoveryCount,
            ["taunt_count"] = memory.TauntCount,
            ["infection_count"] = memory.InfectionCount,
            ["curse_count"] = memory.CurseCount,
            ["action_log"] = actionLog,
            ["action_line"] = lineNumber,
            ["service_state_file"] = ServiceStatePath(memory.SessionId),
            ["server_running"] = serverStatus.Running,
            ["server_pid"] = serverStatus.Pid,
            ["server_started_by_runtime"] = memory.StartedServer,
            ["server_name"] = cfg.ServerName,
            ["server_game_type"] = cfg.GameType,
            ["login_port"] = cfg.LoginServerPort,
            ["private_hostname"] = cfg.ServerPrivateHostname,
            ["public_hostname"] = cfg.ServerHostname,
            ["advertise_enabled"] = cfg.Advertise,
            ["advertised_manifest"] = advertisedManifestNode,
            ["advertised_manifest_json"] = memory.LastStampedManifestJson,
            ["advertised_equality_key"] = memory.LastStampedEqualityKey,
            ["advertised_stamped_at"] = memory.LastStampedUtc?.ToString("O"),
            ["advertised_session_id"] = memory.LastStampedManifestJson.Length > 0
                ? memory.SessionId
                : "",
            ["manifest_stale_pending_restart"] = memory.ManifestStalePendingRestart,
            ["advertise_heartbeat_seconds"] = Ds2NativeSession.HeartbeatPropagationSeconds,
            ["effect"] = serverEffect,
        };

        return state;
    }

    private static JsonObject EnsureLocalServerRunning(SessionMemory memory)
    {
        var effect = new JsonObject();
        if (!Paths.ServerInstalled)
        {
            effect["status"] = "server_missing";
            effect["error"] = $"Server.exe not found at {Paths.ServerExecutable}";
            return effect;
        }

        var status = ServerProcess.QueryStatus();
        if (status.Running)
        {
            effect["status"] = "server_already_running";
            effect["pid"] = status.Pid;
            return effect;
        }

        var cfg = ServerConfig.Load(Paths.ConfigFile);
        if (!string.Equals(cfg.GameType, "DarkSouls2", StringComparison.OrdinalIgnoreCase) &&
            Paths.ConfigExists)
        {
            cfg.GameType = "DarkSouls2";
            cfg.SaveOver(Paths.ConfigFile);
        }

        try
        {
            if (cfg.RelayEnabled)
            {
                RelayTunnel.StartAsync(cfg, Paths.ConfigFile, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
            else
            {
                RelayTunnel.Stop();
            }

            if (!ServerProcess.Start(out var error))
            {
                RelayTunnel.Stop();
                effect["status"] = "server_start_failed";
                effect["error"] = error ?? "unknown server start failure";
                return effect;
            }
        }
        catch (Exception ex)
        {
            RelayTunnel.Stop();
            effect["status"] = "server_start_failed";
            effect["error"] = ex.Message;
            return effect;
        }

        status = ServerProcess.QueryStatus();
        memory.StartedServer = status.Running;
        effect["status"] = status.Running ? "server_started" : "server_start_unknown";
        effect["pid"] = status.Pid;
        return effect;
    }

    private static JsonObject CloseRuntimeStartedServer(SessionMemory memory)
    {
        var effect = new JsonObject();
        if (!memory.StartedServer)
        {
            effect["status"] = "session_closed_server_left_running";
            effect["reason"] = "server was not started by the in-game runtime item";
            return effect;
        }

        ServerProcess.Stop();
        RelayTunnel.Stop();
        memory.StartedServer = false;
        effect["status"] = "runtime_started_server_stopped";
        return effect;
    }

    /// <summary>
    /// Writes the current SessionMemory state into <c>config.json</c> as a DS2
    /// Native Session manifest embedded in <c>ServerDescription</c>. Forces
    /// <c>Advertise=true</c> while the session is in host mode and clears the
    /// manifest line entirely once the session closes. Returns a JSON envelope
    /// describing the result and whether the running Server.exe needs a restart
    /// before the new manifest reaches the master server.
    /// </summary>
    /// <remarks>
    /// Server.exe loads its in-memory <c>RuntimeConfig</c> once at boot and
    /// re-broadcasts <c>ServerName</c>/<c>ServerDescription</c> on a heartbeat
    /// (~30s). Mutating the on-disk config while the server runs does NOT
    /// retroactively change what's advertised — hence the staleness flag.
    /// </remarks>
    private static JsonObject StampManifestIfChanged(SessionMemory memory)
    {
        var envelope = new JsonObject();
        var equalityKey = ManifestEqualityKey(memory);
        envelope["equality_key"] = equalityKey;

        if (!Paths.ConfigExists)
        {
            envelope["status"] = "config_missing";
            envelope["note"] = "Server.exe must run once to generate config.json before the manifest can be advertised.";
            return envelope;
        }

        try
        {
            var cfg = ServerConfig.Load(Paths.ConfigFile);
            var shouldClear = !memory.SessionOpen &&
                string.Equals(memory.Mode, "solo", StringComparison.OrdinalIgnoreCase);
            var wantAdvertise = !shouldClear &&
                string.Equals(memory.Mode, "host", StringComparison.OrdinalIgnoreCase);

            string newDescription;
            string newManifestJson;
            if (shouldClear)
            {
                newDescription = Ds2NativeSession.StripManifestLine(cfg.ServerDescription);
                newManifestJson = "";
            }
            else
            {
                var manifest = BuildManifestForMemory(memory);
                newDescription = Ds2NativeSession.EmbedInDescription(cfg.ServerDescription, manifest);
                newManifestJson = manifest.ToCompactJson();
            }

            var descriptionUnchanged = string.Equals(
                cfg.ServerDescription ?? "", newDescription, StringComparison.Ordinal);
            var advertiseUnchanged = !wantAdvertise || cfg.Advertise;
            var equalityUnchanged = string.Equals(
                memory.LastStampedEqualityKey, equalityKey, StringComparison.Ordinal);

            if (descriptionUnchanged && advertiseUnchanged && equalityUnchanged)
            {
                envelope["status"] = "unchanged";
                envelope["manifest_json"] = memory.LastStampedManifestJson;
                envelope["pending_restart"] = memory.ManifestStalePendingRestart;
                return envelope;
            }

            cfg.ServerDescription = newDescription;
            if (wantAdvertise)
                cfg.Advertise = true;

            if (!cfg.SaveOver(Paths.ConfigFile))
            {
                envelope["status"] = "save_failed";
                return envelope;
            }

            memory.LastStampedEqualityKey = equalityKey;
            memory.LastStampedManifestJson = newManifestJson;
            memory.LastStampedUtc = DateTime.UtcNow;

            // If Server.exe is already running, the manifest we just wrote
            // won't be picked up until it restarts — UNLESS we can push it
            // live via Server.exe's WebUI /settings endpoint. The push closes
            // the staleness gap so peer Bonfires see fresh manifest data on
            // the next master heartbeat (~30s), no Server.exe restart needed.
            var serverStatus = ServerProcess.QueryStatus();
            var stampedAfterBoot = serverStatus.Running &&
                serverStatus.StartedAt.HasValue &&
                memory.LastStampedUtc > serverStatus.StartedAt.Value.ToUniversalTime();

            Ds2NativeWebUIPush.PushResult? pushResult = null;
            if (stampedAfterBoot)
            {
                try
                {
                    pushResult = Ds2NativeWebUIPush.PushAsync(cfg, cfg.WebUIServerPort)
                        .GetAwaiter()
                        .GetResult();
                }
                catch (Exception pushEx)
                {
                    pushResult = new Ds2NativeWebUIPush.PushResult(
                        Success: false,
                        ServerReachable: false,
                        Status: "push_threw",
                        ErrorMessage: pushEx.Message);
                }
            }

            // Stale only if Server.exe is running, our stamp post-dates its
            // boot, AND the live push failed/was-skipped. If the push went
            // through, Server.exe's in-memory ServerDescription matches disk
            // and the next master heartbeat will broadcast it.
            memory.ManifestStalePendingRestart = stampedAfterBoot &&
                (pushResult is null || !pushResult.Success);

            envelope["status"] = shouldClear ? "manifest_cleared" : "manifest_stamped";
            envelope["manifest_json"] = newManifestJson;
            envelope["pending_restart"] = memory.ManifestStalePendingRestart;
            envelope["server_started_at"] = serverStatus.StartedAt?.ToUniversalTime().ToString("O");
            if (pushResult is not null)
            {
                var pushNode = new JsonObject
                {
                    ["success"] = pushResult.Success,
                    ["server_reachable"] = pushResult.ServerReachable,
                    ["status"] = pushResult.Status,
                };
                if (!string.IsNullOrEmpty(pushResult.ErrorMessage))
                {
                    pushNode["error"] = pushResult.ErrorMessage;
                }
                envelope["live_push"] = pushNode;
            }
            return envelope;
        }
        catch (Exception ex)
        {
            envelope["status"] = "stamp_failed";
            envelope["error"] = ex.Message;
            return envelope;
        }
    }

    private static Ds2NativeSession.Manifest BuildManifestForMemory(SessionMemory memory)
    {
        return new Ds2NativeSession.Manifest(
            SessionId: memory.SessionId,
            Mode: memory.Mode,
            Stage: memory.Stage,
            Intent: memory.OnlineIntent,
            RulePreset: memory.RulePreset,
            TauntCount: memory.TauntCount,
            InfectionCount: memory.InfectionCount,
            CurseCount: memory.CurseCount,
            RecoveryCount: memory.RecoveryCount,
            RuntimeVersion: Ds2NativeSession.CurrentRuntimeVersion,
            TimestampUtc: DateTime.UtcNow);
    }

    private static string ManifestEqualityKey(SessionMemory memory)
    {
        return string.Join("|",
            memory.SessionId,
            memory.Mode,
            memory.Stage,
            memory.OnlineIntent,
            memory.RulePreset,
            memory.TauntCount.ToString(),
            memory.InfectionCount.ToString(),
            memory.CurseCount.ToString(),
            memory.RecoveryCount.ToString(),
            memory.SessionOpen ? "1" : "0");
    }

    private static SessionMemory GetSession(string sessionId)
    {
        lock (Lock)
        {
            if (!Sessions.TryGetValue(sessionId, out var memory))
            {
                memory = new SessionMemory(sessionId);
                Sessions[sessionId] = memory;
            }

            return memory;
        }
    }

    private static void WriteServiceState(string sessionId, JsonObject state)
    {
        try
        {
            var path = ServiceStatePath(sessionId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, state.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
            }));
        }
        catch
        {
        }
    }

    private static string ServiceStatePath(string sessionId) =>
        Path.Combine(Root, $"{SanitizeSessionId(sessionId)}.service_state.json");

    private static string SessionIdFromActionLog(string actionLog)
    {
        var fileName = Path.GetFileName(actionLog);
        return fileName.EndsWith(".actions.jsonl", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".actions.jsonl".Length]
            : Path.GetFileNameWithoutExtension(fileName);
    }

    private static string SanitizeSessionId(string value)
    {
        var chars = value.Select(ch =>
            char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '_').ToArray();
        return chars.Length == 0 ? "ds2" : new string(chars);
    }

    private static string GetString(JsonNode? node, string key)
    {
        try
        {
            return node?[key]?.GetValue<string>() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static int GetInt(JsonNode? node, string key)
    {
        try
        {
            return node?[key]?.GetValue<int>() ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static Task NotifyAsync(string method, JsonObject payload)
    {
        var server = _server;
        if (server is null)
            return Task.CompletedTask;

        try
        {
            return server.NotifyAsync(method, payload);
        }
        catch
        {
            return Task.CompletedTask;
        }
    }

    // ── Phase 4d (2026-05-16): pose-bridge lifecycle integration ────
    //
    // The HKMP-style overlay needs a UDP backbone alongside each
    // bonfire-co-op session. We start it when the session opens and
    // tear it down when the session closes, so it's invisible to the
    // user — no env vars, no extra .bat launchers, just click host /
    // click join and the cubes start flying between PCs.
    //
    // Default port is 50031; both ends MUST use the same port for
    // auto-discovery to work (the listener registers
    // src_ip:LOCAL_PORT, not the ephemeral src port the peer sent
    // from). If we ever need per-session port rotation, we'd derive
    // it from the session_id hash; for now a fixed port keeps the
    // firewall rule simple.
    private const int kDefaultPoseBridgePort = 50031;

    private static JsonObject TryStartPoseBridgeAsHost()
    {
        var result = new JsonObject
        {
            ["role"] = "host",
            ["port"] = kDefaultPoseBridgePort,
        };
        try
        {
            // Host doesn't know the guest's IP yet — it'll be learned
            // by auto-discovery on the listener once the guest's
            // first packet arrives.
            var started = Ds2NativePoseBridge.EnsureStarted(
                kDefaultPoseBridgePort, Array.Empty<string>());
            result["started"] = started;
            result["already_running"] = !started;
        }
        catch (Exception ex)
        {
            result["started"] = false;
            result["error"] = ex.Message;
        }

        // Plan v3 Track A: start broadcasting a LAN beacon so any
        // guest on the same subnet can auto-arm a JoinTarget by
        // using the Crystal Eye Orb — no Bonfire UI clicks.
        var beaconResult = TryStartLanBeacon();
        result["lan_beacon"] = beaconResult;
        return result;
    }

    private static JsonObject TryStartLanBeacon()
    {
        var info = new JsonObject();
        try
        {
            var privateIp = Network.GetPrivateIp() ?? "127.0.0.1";
            var poseEndpoint = $"{privateIp}:{kDefaultPoseBridgePort}";
            var serverConfig = ServerConfig.Load(Paths.ConfigFile);
            var sessionName = string.IsNullOrWhiteSpace(serverConfig.ServerName)
                ? "Bonfire DS2 fire"
                : serverConfig.ServerName;
            static bool IsLoopbackHost(string? value)
            {
                if (string.IsNullOrWhiteSpace(value)) return true;
                var v = value.Trim();
                return v.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                       v.Equals("::1", StringComparison.OrdinalIgnoreCase) ||
                       v.StartsWith("127.", StringComparison.Ordinal);
            }
            var cfgHostname = serverConfig.ServerHostname;
            var cfgPrivateHostname = serverConfig.ServerPrivateHostname;
            var hostname = IsLoopbackHost(cfgHostname)
                ? privateIp
                : cfgHostname;
            var privateHostname = IsLoopbackHost(cfgPrivateHostname)
                ? privateIp
                : cfgPrivateHostname;
            var loginPort = serverConfig.LoginServerPort > 0
                ? serverConfig.LoginServerPort
                : 50050;
            var sessionId = Guid.NewGuid().ToString("N");
            // Cheap stable sender id from machine name — good enough
            // to deduplicate the host's own beacon when it loops back.
            var senderId = (long)Environment.MachineName.GetHashCode()
                           ^ ((long)Environment.UserName.GetHashCode() << 32);
            Ds2LanBeacon.EnsureListenerRunning();
            Ds2LanBeacon.StartHostBroadcast(
                sessionId,
                poseEndpoint,
                sessionName,
                senderId,
                serverConfig.ServerId,
                hostname,
                privateHostname,
                loginPort,
                serverConfig.GameType,
                !string.IsNullOrWhiteSpace(serverConfig.Password),
                "saponita_direct");
            info["broadcasting"] = true;
            info["pose_endpoint"] = poseEndpoint;
            info["hostname"] = hostname;
            info["private_hostname"] = privateHostname;
            info["login_port"] = loginPort;
            info["server_id"] = serverConfig.ServerId;
            info["session_id"] = sessionId;
            info["session_name"] = sessionName;
        }
        catch (Exception ex)
        {
            info["broadcasting"] = false;
            info["error"] = ex.Message;
        }
        return info;
    }

    private static void TryPushLanInvitePromptToRuntime()
    {
        try
        {
            Ds2LanBeacon.EnsureListenerRunning();
            var status = Ds2NativeRuntimeBridge.GetStatus();
            if (!status.Installed || !status.Active ||
                string.IsNullOrWhiteSpace(status.SessionId))
            {
                return;
            }

            var beacon = Ds2LanBeacon.PickStrongest();
            if (beacon is null)
                return;

            if (!string.Equals(beacon.GameType, "DarkSouls2",
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(beacon.InviteKind, "saponita_direct",
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            lock (Lock)
            {
                if (MutedLanInviteSessionIds.Contains(beacon.SessionId))
                    return;

                var now = DateTime.UtcNow;
                if (string.Equals(_lastPushedLanInviteSessionId, beacon.SessionId,
                        StringComparison.OrdinalIgnoreCase) &&
                    now - _lastPushedLanInviteUtc < TimeSpan.FromSeconds(8))
                {
                    return;
                }

                _lastPushedLanInviteSessionId = beacon.SessionId;
                _lastPushedLanInviteUtc = now;
            }

            var payload = BeaconToInvitePayload(beacon);
            Ds2NativeRuntimeBridge.SendCommand(
                status.SessionId,
                "invite.received",
                payload);

            _ = NotifyAsync("ds2_runtime.in_game_invite_seen", new JsonObject
            {
                ["time_utc"] = DateTime.UtcNow.ToString("O"),
                ["runtime_session_id"] = status.SessionId,
                ["invite"] = BeaconToInvitePayload(beacon),
            });
        }
        catch (Exception ex)
        {
            _ = NotifyAsync("ds2_runtime.in_game_invite_error", new JsonObject
            {
                ["time_utc"] = DateTime.UtcNow.ToString("O"),
                ["error"] = ex.Message,
            });
        }
    }

    private static JsonObject BeaconToInvitePayload(Ds2LanBeacon.CachedBeacon beacon)
    {
        return new JsonObject
        {
            ["session_id"] = beacon.SessionId,
            ["host_name"] = beacon.HostName,
            ["server_id"] = beacon.ServerId,
            ["hostname"] = beacon.Hostname,
            ["private_hostname"] = beacon.PrivateHostname,
            ["login_port"] = beacon.LoginPort,
            ["game_type"] = beacon.GameType,
            ["password_required"] = beacon.PasswordRequired,
            ["invite_kind"] = beacon.InviteKind,
            ["host_endpoint"] = beacon.HostEndpoint,
            ["source_ip"] = beacon.SourceIp.ToString(),
            ["seen_count"] = beacon.SeenCount,
            ["age_ms"] = (DateTime.UtcNow - beacon.LastSeenUtc).TotalMilliseconds,
        };
    }

    private static JsonObject ArmJoinTargetFromInviteAction(
        JsonObject? data,
        string runtimeSessionId)
    {
        var effect = new JsonObject();
        var inviteSessionId = GetString(data, "invite_session_id");
        var beacon = string.IsNullOrWhiteSpace(inviteSessionId)
            ? null
            : Ds2LanBeacon.GetRecentBeacons()
                .FirstOrDefault(b => string.Equals(
                    b.SessionId,
                    inviteSessionId,
                    StringComparison.OrdinalIgnoreCase));

        var hostName = FirstNonEmpty(
            GetString(data, "invite_host_name"),
            beacon?.HostName,
            "Bonfire DS2 fire");
        var serverId = FirstNonEmpty(GetString(data, "server_id"), beacon?.ServerId);
        var privateHost = FirstNonEmpty(
            GetString(data, "private_hostname"),
            beacon?.PrivateHostname,
            beacon?.SourceIp.ToString());
        var hostname = FirstNonEmpty(GetString(data, "hostname"), beacon?.Hostname, privateHost);
        var port = GetInt(data, "login_port");
        if (port <= 0 && beacon is not null)
            port = beacon.LoginPort;
        if (port <= 0)
            port = 50050;

        var target = new Ds2NativeJoinTarget.JoinTarget(
            ServerId: serverId,
            ServerName: hostName,
            Hostname: hostname,
            PrivateHostname: privateHost,
            Port: port,
            Password: "",
            GameType: FirstNonEmpty(beacon?.GameType, "DarkSouls2"),
            SessionId: inviteSessionId,
            SessionMode: "direct_invite_guest",
            ArmedAtUtc: DateTime.UtcNow);
        Ds2NativeJoinTarget.Set(target);
        MuteLanInviteSession(inviteSessionId);

        effect["status"] = "join_target_armed_from_in_game_prompt";
        effect["target"] = Ds2NativeJoinTarget.ToJson(target);
        effect["beacon_cache_hit"] = beacon is not null;
        effect["source"] = "ds2_in_game_invite_prompt";
        if (string.IsNullOrWhiteSpace(serverId))
        {
            effect["warning"] =
                "Invite did not include a server id; pose bridge can still arm, but DS2 private-server reconnect may need a fresh host beacon.";
        }
        else
        {
            effect["auto_relaunch_scheduled"] = true;
            ScheduleDirectInviteRelaunch(runtimeSessionId, target);
        }
        return effect;
    }

    private static void ScheduleDirectInviteRelaunch(
        string runtimeSessionId,
        Ds2NativeJoinTarget.JoinTarget target)
    {
        _ = Task.Run(async () =>
        {
            var result = new JsonObject
            {
                ["time_utc"] = DateTime.UtcNow.ToString("O"),
                ["runtime_session_id"] = runtimeSessionId,
                ["target"] = Ds2NativeJoinTarget.ToJson(target),
            };

            try
            {
                try
                {
                    Ds2NativeRuntimeBridge.SendCommand(
                        runtimeSessionId,
                        "invite.relaunching",
                        new JsonObject
                        {
                            ["host_name"] = target.ServerName,
                            ["server_id"] = target.ServerId,
                        });
                }
                catch (Exception ex)
                {
                    result["runtime_notice_error"] = ex.Message;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(1600));

                result["closed_previous_process"] =
                    CloseRuntimeProcess(runtimeSessionId);

                if (!Loader.SteamUtils.IsSteamRunningAndLoggedIn())
                {
                    throw new Exception("Steam is not running or not logged in.");
                }

                var settings = GameSettings.Load();
                var exePath = settings.PathFor("DarkSouls2");
                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                {
                    throw new Exception(
                        "No valid Dark Souls II executable is configured.");
                }

                var publicKey = (await MasterServer.GetPublicKeyAsync(
                        target.ServerId,
                        target.Password,
                        CancellationToken.None))
                    ?.Replace("\r\n", "\n");
                if (string.IsNullOrWhiteSpace(publicKey))
                {
                    throw new Exception(
                        "Could not fetch the host server public key. " +
                        "If the host profile is passworded, in-game LAN accept " +
                        "currently needs that host to be unsealed.");
                }

                var wan = await Network.GetPublicIpAsync(CancellationToken.None) ?? "";
                var lan = Network.GetPrivateIp() ?? "";
                var injectorPath =
                    Path.Combine(Paths.InstallRoot, "Loader", "Injector.dll");
                if (!File.Exists(injectorPath))
                    injectorPath = Path.Combine(Paths.ServiceDirectory, "Injector.dll");

                var launchRequest = new Bonfire.Service.Game.LaunchRequest(
                    ExePath: exePath,
                    ServerId: target.ServerId,
                    ServerName: target.ServerName,
                    Hostname: target.Hostname,
                    PrivateHostname: target.PrivateHostname,
                    Port: target.Port,
                    PublicKey: publicKey,
                    GameType: target.GameType,
                    EnableSeparateSaves: settings.UseSeparateSaves,
                    Ds2OverhaulPath: settings.Ds2OverhaulPath,
                    EnableDs1Seamless: settings.EnableDs1Seamless,
                    Ds1SeamlessPath: settings.Ds1SeamlessPath,
                    EnableDs3Seamless: settings.EnableDs3Seamless,
                    Ds3SeamlessPath: settings.Ds3SeamlessPath);

                var launch = Bonfire.Service.Game.GameLauncher.Launch(
                    launchRequest,
                    wan,
                    lan,
                    injectorPath);
                result["ok"] = launch.Ok;
                result["pid"] = launch.Pid;
                result["message"] = launch.Message;
                if (!launch.Ok)
                    throw new Exception(launch.Message);
            }
            catch (Exception ex)
            {
                result["ok"] = false;
                result["error"] = ex.Message;
            }

            await NotifyAsync("ds2_runtime.direct_invite_relaunch", result);
        });
    }

    private static JsonObject CloseRuntimeProcess(string runtimeSessionId)
    {
        var result = new JsonObject
        {
            ["session_id"] = runtimeSessionId,
        };

        var pid = TryParseRuntimePid(runtimeSessionId);
        result["pid"] = pid;
        if (pid <= 0)
        {
            result["status"] = "pid_not_found";
            return result;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                result["status"] = "already_exited";
                return result;
            }

            if (process.CloseMainWindow())
            {
                result["close_main_window"] = true;
                if (process.WaitForExit(5000))
                {
                    result["status"] = "closed_gracefully";
                    return result;
                }
            }

            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
            result["status"] = "killed";
        }
        catch (Exception ex)
        {
            result["status"] = "close_failed";
            result["error"] = ex.Message;
        }
        return result;
    }

    private static int TryParseRuntimePid(string runtimeSessionId)
    {
        if (string.IsNullOrWhiteSpace(runtimeSessionId))
            return 0;
        var idx = runtimeSessionId.LastIndexOf('_');
        if (idx < 0 || idx >= runtimeSessionId.Length - 1)
            return 0;
        return int.TryParse(runtimeSessionId.AsSpan(idx + 1), out var pid)
            ? pid
            : 0;
    }

    private static void MuteLanInviteSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        lock (Lock)
        {
            MutedLanInviteSessionIds.Add(sessionId);
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }
        return "";
    }

    // v2.8.3: called every worker tick (~1 Hz). Watches the latest
    // events.jsonl; if a resolved player.live_transform has been
    // written in the last few seconds AND the pose bridge isn't
    // already running, start it. Role is chosen from whether the
    // brother has armed a JoinTarget (guest) or not (host) — same
    // logic the orb-driven session.create / session.join handlers
    // use, just without the in-game item requirement.
    //
    // Removes the trap where vanilla DS2 + Bonfire UI alone weren't
    // enough to spin up the overlay: the user previously had to
    // know about the Blessed/Crystal Eye Orb items, find them in
    // their inventory, and use them in the right order. This
    // restores the "click host / click join, launch DS2, see cubes"
    // flow.
    private static DateTime _lastPoseAutoStartCheck = DateTime.MinValue;
    private static long _lastPoseAutoStartEventOffset = -1;

    private static void TryAutoStartPoseBridgeFromHeartbeat()
    {
        if (Ds2NativePoseBridge.IsRunning)
        {
            // v2.8.5: even if the bridge is already running, the user
            // may have armed the JoinTarget AFTER the bridge auto-
            // started (e.g. legacy code path where the bridge started
            // host-mode first, then "Join" was clicked). Make sure
            // the configured peer list reflects the current
            // JoinTarget. Idempotent — AddPeer no-ops if already in.
            TryReconcilePeersWithJoinTarget();
            return;
        }

        // Throttle: at most one auto-start attempt per 2 s.
        var now = DateTime.UtcNow;
        if (now - _lastPoseAutoStartCheck < TimeSpan.FromSeconds(2)) return;
        _lastPoseAutoStartCheck = now;

        // v2.8.4: gate purely on DS2 process existence. We no longer
        // require resolved player.live_transform events because the
        // bridge now reads pose directly from DS2 memory via
        // Ds2MemoryReader, bypassing the Injector's worker thread
        // (which sometimes hangs on pre-world chain-walk failures).
        // Events.jsonl freshness only matters for the fallback path.
        try
        {
            if (!System.Diagnostics.Process.GetProcessesByName("DarkSoulsII").Any())
                return;
        }
        catch { return; }

        // Decide role from JoinTarget. Wrap each call in try/catch
        // so a malformed disk file can't break the heartbeat loop.
        try
        {
            var target = Ds2NativeJoinTarget.Get();
            var peers = BuildPeerEndpointsFromTarget(target);
            Ds2NativePoseBridge.EnsureStarted(kDefaultPoseBridgePort, peers);
        }
        catch { /* best-effort */ }
    }

    // v2.8.5: build the initial peer-endpoint list from a JoinTarget,
    // including BOTH the master-listing Hostname (WAN address) and
    // PrivateHostname (LAN address) when both look usable. The bridge
    // broadcasts to all configured peers — packets to whichever is
    // actually reachable land at the destination's listener; the
    // other path just silently drops. This unblocks same-LAN co-op
    // (where WAN-routed packets require port-forwarding the user
    // hasn't set up) without breaking different-network co-op (where
    // only WAN works).
    private static IReadOnlyList<string> BuildPeerEndpointsFromTarget(
        Ds2NativeJoinTarget.JoinTarget? target)
    {
        var peers = new List<string>();
        if (target == null) return peers;

        void Maybe(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            // Reject obviously-useless values.
            if (raw == "0.0.0.0" || raw == "127.0.0.1" || raw == "::1") return;
            var candidate = $"{raw}:{kDefaultPoseBridgePort}";
            if (!peers.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                peers.Add(candidate);
        }
        Maybe(target.Hostname);
        Maybe(target.PrivateHostname);
        return peers;
    }

    // v2.8.5: when the bridge is already running but the JoinTarget
    // arming happens later (e.g. user clicks "Join" after the bridge
    // auto-started in host-mode on DS2 launch), inject the JoinTarget
    // peers without restarting. Ds2NativePoseBridge.AddPeer is a
    // public no-op-on-duplicate helper we added in Phase 4d.
    private static void TryReconcilePeersWithJoinTarget()
    {
        try
        {
            var target = Ds2NativeJoinTarget.Get();
            if (target == null) return;
            foreach (var peer in BuildPeerEndpointsFromTarget(target))
            {
                Ds2NativePoseBridge.AddPeer(peer);
            }
        }
        catch { /* best-effort */ }
    }

    private static string? FindLatestEventLog()
    {
        try
        {
            if (!Directory.Exists(Root)) return null;
            return Directory.EnumerateFiles(Root, "*.events.jsonl")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    // Tail the events file and look for a recent
    // player.live_transform with resolved=true. Cheap — we read
    // only the last ~16 KB so this scales fine even if the file is
    // many MB.
    private static bool HasRecentResolvedPose(string path, DateTime now)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            const int kTailBytes = 16384;
            long start = Math.Max(0, stream.Length - kTailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            string? line;
            string? newest = null;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.IndexOf("\"player.live_transform\"", StringComparison.Ordinal) < 0)
                    continue;
                if (line.IndexOf("\"resolved\":true", StringComparison.Ordinal) < 0)
                    continue;
                newest = line; // keep last one
            }
            if (newest is null) return false;
            // Crude time extraction — avoid full JSON parse cost.
            // Format: "time_utc":"2026-05-17T00:06:18Z"
            const string key = "\"time_utc\":\"";
            var idx = newest.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return false;
            var end = newest.IndexOf('"', idx + key.Length);
            if (end < 0) return false;
            var ts = newest.Substring(idx + key.Length, end - (idx + key.Length));
            if (!DateTime.TryParse(ts, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal |
                    System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var poseUtc))
            {
                return false;
            }
            return (now - poseUtc) < TimeSpan.FromSeconds(15);
        }
        catch { return false; }
    }

    private static JsonObject TryStartPoseBridgeAsGuest()
    {
        var result = new JsonObject
        {
            ["role"] = "guest",
            ["port"] = kDefaultPoseBridgePort,
        };
        try
        {
            // Make sure the listener has had a chance to populate the
            // beacon cache. Idempotent.
            Ds2LanBeacon.EnsureListenerRunning();

            var target = Ds2NativeJoinTarget.Get();
            // BuildPeerEndpointsFromTarget returns IReadOnlyList; copy
            // into a List so the beacon-fallback path can append to it.
            var peers = new List<string>(BuildPeerEndpointsFromTarget(target));

            // Plan v3 Track A: if the user fired the Crystal Eye Orb
            // without first arming a JoinTarget from the Bonfire UI,
            // try to auto-arm one from the freshest LAN beacon. This
            // makes the in-game flow item-driven end-to-end on local
            // networks (no UI clicks required).
            string? beaconNote = null;
            if (peers.Count == 0)
            {
                var beacon = Ds2LanBeacon.PickStrongest();
                if (beacon is not null)
                {
                    // Prefer the source IP of the packet over the
                    // host's self-reported endpoint string — the
                    // packet's source is what's actually routable
                    // back to the host (handles a host with multiple
                    // NICs or a misadvertised endpoint).
                    var hostPort = ExtractPort(beacon.HostEndpoint, kDefaultPoseBridgePort);
                    var resolved = $"{beacon.SourceIp}:{hostPort}";
                    peers.Add(resolved);
                    result["lan_beacon"] = new JsonObject
                    {
                        ["used"] = true,
                        ["session_id"] = beacon.SessionId,
                        ["host_endpoint"] = beacon.HostEndpoint,
                        ["source_ip"] = beacon.SourceIp.ToString(),
                        ["resolved"] = resolved,
                        ["seen_count"] = beacon.SeenCount,
                        ["age_ms"] = (DateTime.UtcNow - beacon.LastSeenUtc).TotalMilliseconds,
                    };
                    beaconNote = $"auto-armed from LAN beacon {beacon.SessionId} ({resolved})";
                }
            }

            if (peers.Count == 0)
            {
                result["started"] = false;
                result["error"] = "no armed join target and no LAN beacon visible — guest has nothing to point the bridge at";
                result["lan_beacon"] = new JsonObject { ["used"] = false };
                return result;
            }
            result["peers"] = new JsonArray(peers.Select(p => (JsonNode?)p).ToArray());

            var started = Ds2NativePoseBridge.EnsureStarted(
                kDefaultPoseBridgePort, peers);
            if (!started)
            {
                // Already running — reconcile so any newly-resolved
                // peer endpoints (e.g. LAN address that wasn't in the
                // original list) get folded in.
                foreach (var p in peers) Ds2NativePoseBridge.AddPeer(p);
                result["already_running"] = true;
            }
            result["started"] = true;
            if (beaconNote is not null) result["note"] = beaconNote;
        }
        catch (Exception ex)
        {
            result["started"] = false;
            result["error"] = ex.Message;
        }
        return result;
    }

    /// Best-effort port extractor for an "ip:port" endpoint string.
    /// Falls back to <paramref name="fallback"/> on any parse failure
    /// so a malformed beacon can never crash the auto-join path.
    private static int ExtractPort(string endpoint, int fallback)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return fallback;
        var idx = endpoint.LastIndexOf(':');
        if (idx <= 0 || idx >= endpoint.Length - 1) return fallback;
        return int.TryParse(endpoint.AsSpan(idx + 1), out var p) && p > 0 && p < 65536
            ? p
            : fallback;
    }

    private sealed class SessionMemory
    {
        public SessionMemory(string sessionId)
        {
            SessionId = sessionId;
        }

        public string SessionId { get; }
        public bool SessionOpen { get; set; }
        public bool StartedServer { get; set; }
        public string Mode { get; set; } = "solo";
        public string Stage { get; set; } = "idle";
        public string OnlineIntent { get; set; } = "none";
        public string LastCommand { get; set; } = "none";
        public string RulePreset { get; set; } = "";
        public int ActionCount { get; set; }
        public int RecoveryCount { get; set; }
        public int TauntCount { get; set; }
        public int InfectionCount { get; set; }
        public int CurseCount { get; set; }
        public DateTime LastActionUtc { get; set; }

        // Advertisement bookkeeping. The "stamp" is what we last wrote to
        // <see cref="ServerConfig.ServerDescription"/>; "stale" means the
        // running Server.exe still has an older manifest in memory because it
        // only re-reads config.json at boot.
        public string LastStampedEqualityKey { get; set; } = "";
        public string LastStampedManifestJson { get; set; } = "";
        public DateTime? LastStampedUtc { get; set; }
        public bool ManifestStalePendingRestart { get; set; }
    }
}
