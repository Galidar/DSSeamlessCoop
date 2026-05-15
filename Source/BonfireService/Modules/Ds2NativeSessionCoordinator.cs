using System.Text.Json;
using System.Text.Json.Nodes;
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
                serverEffect = EnsureLocalServerRunning(memory);
                break;

            case "session.join":
                memory.SessionOpen = true;
                memory.Mode = "guest";
                memory.Stage = "service_guest_armed";
                memory.OnlineIntent = "cooperate_join";
                serverEffect["status"] = "guest_link_armed";
                serverEffect["note"] =
                    "Join requests are armed in Bonfire state; connect to a Bonfire host before launch for the current DS2 network stack.";
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

        var serverStatus = ServerProcess.QueryStatus();
        var cfg = ServerConfig.Load(Paths.ConfigFile);
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
    }
}
