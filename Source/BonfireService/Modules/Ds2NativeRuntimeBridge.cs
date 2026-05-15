using System.Text.Json.Nodes;

namespace Bonfire.Service.Modules;

public static class Ds2NativeRuntimeBridge
{
    public sealed record RuntimeStatus(
        bool Installed,
        bool Active,
        string SessionId,
        string EventLog,
        string CommandInbox,
        string ActionLog,
        string MessageLog,
        string StateFile,
        string ServiceStateFile,
        string LastEvent,
        string LastAction,
        string LastMessage,
        string StateJson,
        string ServiceStateJson,
        DateTime? LastWriteUtc);

    private static string Root => Path.Combine(Paths.InstallRoot, "Runtime", "DS2Native");

    public static RuntimeStatus GetStatus(string? sessionId = null)
    {
        Directory.CreateDirectory(Root);

        var eventLog = ResolveEventLog(sessionId);
        if (string.IsNullOrEmpty(eventLog))
        {
            return new RuntimeStatus(false, false, "", "", "", "", "", "", "", "", "", "", "", "", null);
        }

        var lastWrite = File.GetLastWriteTimeUtc(eventLog);
        var commandInbox = ToCommandInbox(eventLog);
        var actionLog = ToSibling(eventLog, ".actions.jsonl");
        var messageLog = ToSibling(eventLog, ".messages.jsonl");
        var stateFile = ToSibling(eventLog, ".state.json");
        var serviceStateFile = ToSibling(eventLog, ".service_state.json");
        var lastEvent = ReadLastLine(eventLog);
        var lastAction = File.Exists(actionLog) ? ReadLastLine(actionLog) : "";
        var lastMessage = File.Exists(messageLog) ? ReadLastLine(messageLog) : "";
        var stateJson = File.Exists(stateFile) ? ReadAllText(stateFile) : "";
        var serviceStateJson = File.Exists(serviceStateFile) ? ReadAllText(serviceStateFile) : "";
        var active = DateTime.UtcNow - lastWrite < TimeSpan.FromSeconds(20);

        return new RuntimeStatus(
            Installed: true,
            Active: active,
            SessionId: FromEventLog(eventLog),
            EventLog: eventLog,
            CommandInbox: commandInbox,
            ActionLog: actionLog,
            MessageLog: messageLog,
            StateFile: stateFile,
            ServiceStateFile: serviceStateFile,
            LastEvent: lastEvent,
            LastAction: lastAction,
            LastMessage: lastMessage,
            StateJson: stateJson,
            ServiceStateJson: serviceStateJson,
            LastWriteUtc: lastWrite);
    }

    public static RuntimeStatus SendCommand(string? sessionId, string command, JsonNode? payload)
    {
        if (string.IsNullOrWhiteSpace(command))
            throw new Exception("DS2 native runtime command is missing.");

        var status = GetStatus(sessionId);
        if (!status.Installed || string.IsNullOrEmpty(status.CommandInbox))
            throw new Exception("No DS2 native runtime command inbox was found.");

        Directory.CreateDirectory(Path.GetDirectoryName(status.CommandInbox)!);

        var envelope = new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString("N"),
            ["time_utc"] = DateTime.UtcNow.ToString("O"),
            ["command"] = command,
        };
        if (payload is not null)
        {
            envelope["payload"] = JsonNode.Parse(payload.ToJsonString());
        }

        File.AppendAllText(status.CommandInbox, envelope.ToJsonString() + Environment.NewLine);
        return GetStatus(status.SessionId);
    }

    private static string ResolveEventLog(string? sessionId)
    {
        if (!Directory.Exists(Root))
            return "";

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            var exact = Path.Combine(Root, SanitizeSessionId(sessionId) + ".events.jsonl");
            return File.Exists(exact) ? exact : "";
        }

        return Directory.EnumerateFiles(Root, "*.events.jsonl")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault() ?? "";
    }

    private static string ToCommandInbox(string eventLog)
    {
        var fileName = Path.GetFileName(eventLog);
        var commandName = fileName.EndsWith(".events.jsonl", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".events.jsonl".Length] + ".commands.jsonl"
            : fileName + ".commands.jsonl";
        return Path.Combine(Path.GetDirectoryName(eventLog) ?? Root, commandName);
    }

    private static string ToSibling(string eventLog, string suffix)
    {
        var fileName = Path.GetFileName(eventLog);
        var siblingName = fileName.EndsWith(".events.jsonl", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".events.jsonl".Length] + suffix
            : fileName + suffix;
        return Path.Combine(Path.GetDirectoryName(eventLog) ?? Root, siblingName);
    }

    private static string FromEventLog(string eventLog)
    {
        var fileName = Path.GetFileName(eventLog);
        return fileName.EndsWith(".events.jsonl", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".events.jsonl".Length]
            : Path.GetFileNameWithoutExtension(fileName);
    }

    private static string ReadLastLine(string path)
    {
        try
        {
            return File.ReadLines(path).LastOrDefault() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string ReadAllText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch
        {
            return "";
        }
    }

    private static string SanitizeSessionId(string value)
    {
        var chars = value.Select(ch =>
            char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '_').ToArray();
        return chars.Length == 0 ? "ds2" : new string(chars);
    }
}
