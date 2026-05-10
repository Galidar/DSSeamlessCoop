/*
 * Reads/writes Server/Saved/default/config.json. Only touches the fields
 * the UI manages — other entries are preserved verbatim through a
 * regex-based replace so users who hand-edit matchmaking parameters don't
 * lose their work.
 */

using System.Text.RegularExpressions;

namespace Bonfire.Service.Modules;

public sealed class ServerConfig
{
    public string ServerId { get; set; } = "";
    public string ServerName { get; set; } = "My DS3OS Server";
    public string ServerDescription { get; set; } = "A custom Dark Souls server.";
    public string Password { get; set; } = "";
    public string GameType { get; set; } = "DarkSouls2";
    public string ServerHostname { get; set; } = "";
    public string ServerPrivateHostname { get; set; } = "";
    public bool RelayEnabled { get; set; } = false;
    public string RelayPublicHostname { get; set; } = "";
    public string RelayControlHost { get; set; } = "";
    public int RelayControlPort { get; set; } = 50030;
    public string RelayControlToken { get; set; } = "";
    public int RelayLoginServerPort { get; set; } = 0;
    public int RelayAuthServerPort { get; set; } = 0;
    public int RelayGameServerPort { get; set; } = 0;
    public bool Advertise { get; set; } = true;
    public string WebUIServerUsername { get; set; } = "";
    public string WebUIServerPassword { get; set; } = "";
    /// <summary>
    /// Login server port — what we tell the injector to redirect the game's
    /// retail address to. Server.exe binds this same port via its own
    /// LoginServerPort config field. Default 50050 matches DS3OS upstream.
    /// </summary>
    public int LoginServerPort { get; set; } = 50050;

    public static ServerConfig Load(string path)
    {
        var cfg = new ServerConfig();
        if (!File.Exists(path)) return cfg;

        var text = File.ReadAllText(path);
        cfg.ServerId              = ReadString(text, "ServerId")              ?? cfg.ServerId;
        cfg.ServerName            = ReadString(text, "ServerName")            ?? cfg.ServerName;
        cfg.ServerDescription     = ReadString(text, "ServerDescription")     ?? cfg.ServerDescription;
        cfg.Password              = ReadString(text, "Password")              ?? cfg.Password;
        cfg.GameType              = ReadString(text, "GameType")              ?? cfg.GameType;
        cfg.ServerHostname        = ReadString(text, "ServerHostname")        ?? cfg.ServerHostname;
        cfg.ServerPrivateHostname = ReadString(text, "ServerPrivateHostname") ?? cfg.ServerPrivateHostname;
        cfg.RelayEnabled          = ReadBool(text,   "RelayEnabled")          ?? cfg.RelayEnabled;
        cfg.RelayPublicHostname   = ReadString(text, "RelayPublicHostname")   ?? cfg.RelayPublicHostname;
        cfg.RelayControlHost      = ReadString(text, "RelayControlHost")      ?? cfg.RelayControlHost;
        cfg.RelayControlPort      = ReadInt(text,    "RelayControlPort")      ?? cfg.RelayControlPort;
        cfg.RelayControlToken     = ReadString(text, "RelayControlToken")     ?? cfg.RelayControlToken;
        cfg.RelayLoginServerPort  = ReadInt(text,    "RelayLoginServerPort")  ?? cfg.RelayLoginServerPort;
        cfg.RelayAuthServerPort   = ReadInt(text,    "RelayAuthServerPort")   ?? cfg.RelayAuthServerPort;
        cfg.RelayGameServerPort   = ReadInt(text,    "RelayGameServerPort")   ?? cfg.RelayGameServerPort;
        cfg.Advertise             = ReadBool(text,   "Advertise")             ?? cfg.Advertise;
        cfg.WebUIServerUsername   = ReadString(text, "WebUIServerUsername")   ?? cfg.WebUIServerUsername;
        cfg.WebUIServerPassword   = ReadString(text, "WebUIServerPassword")   ?? cfg.WebUIServerPassword;
        cfg.LoginServerPort       = ReadInt(text,    "LoginServerPort")       ?? cfg.LoginServerPort;
        return cfg;
    }

    private static int? ReadInt(string text, string key)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            text, "\"" + System.Text.RegularExpressions.Regex.Escape(key) +
                  "\"\\s*:\\s*(?<v>-?\\d+)");
        return m.Success && int.TryParse(m.Groups["v"].Value, out var v) ? v : null;
    }

    public bool SaveOver(string path)
    {
        if (!File.Exists(path)) return false;

        var text = File.ReadAllText(path);
        text = ReplaceString(text, "ServerName",            ServerName);
        text = ReplaceString(text, "ServerDescription",     ServerDescription);
        text = ReplaceString(text, "Password",              Password);
        text = ReplaceString(text, "GameType",              GameType);
        text = ReplaceString(text, "ServerHostname",        ServerHostname);
        text = ReplaceString(text, "ServerPrivateHostname", ServerPrivateHostname);
        text = UpsertBool(text,   "RelayEnabled",          RelayEnabled);
        text = UpsertString(text, "RelayPublicHostname",   RelayPublicHostname);
        text = UpsertString(text, "RelayControlHost",      RelayControlHost);
        text = UpsertInt(text,    "RelayControlPort",      RelayControlPort);
        text = UpsertString(text, "RelayControlToken",     RelayControlToken);
        text = UpsertInt(text,    "RelayLoginServerPort",  RelayLoginServerPort);
        text = UpsertInt(text,    "RelayAuthServerPort",   RelayAuthServerPort);
        text = UpsertInt(text,    "RelayGameServerPort",   RelayGameServerPort);
        text = ReplaceBool(text,   "Advertise",             Advertise);
        text = ReplaceString(text, "WebUIServerUsername",   WebUIServerUsername);
        text = ReplaceString(text, "WebUIServerPassword",   WebUIServerPassword);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return true;
    }

    private static string? ReadString(string text, string key)
    {
        var m = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"(?<v>(?:\\\\.|[^\"\\\\])*)\"");
        return m.Success ? Regex.Unescape(m.Groups["v"].Value) : null;
    }

    private static bool? ReadBool(string text, string key)
    {
        var m = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(?<v>true|false)");
        if (!m.Success) return null;
        return m.Groups["v"].Value == "true";
    }

    private static string ReplaceString(string text, string key, string value)
    {
        var pattern = "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"(?:\\\\.|[^\"\\\\])*\"";
        var escaped = (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        var replacement = "\"" + key + "\": \"" + escaped + "\"";
        return Regex.IsMatch(text, pattern)
            ? Regex.Replace(text, pattern, replacement)
            : text;
    }

    private static string ReplaceBool(string text, string key, bool value)
    {
        var pattern = "\"" + Regex.Escape(key) + "\"\\s*:\\s*(?:true|false)";
        var replacement = "\"" + key + "\": " + (value ? "true" : "false");
        return Regex.IsMatch(text, pattern)
            ? Regex.Replace(text, pattern, replacement)
            : text;
    }

    private static string UpsertString(string text, string key, string value)
    {
        return Regex.IsMatch(text, "\"" + Regex.Escape(key) + "\"\\s*:")
            ? ReplaceString(text, key, value)
            : InsertBeforeFinalBrace(text, "\"" + key + "\": \"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"");
    }

    private static string UpsertBool(string text, string key, bool value)
    {
        return Regex.IsMatch(text, "\"" + Regex.Escape(key) + "\"\\s*:")
            ? ReplaceBool(text, key, value)
            : InsertBeforeFinalBrace(text, "\"" + key + "\": " + (value ? "true" : "false"));
    }

    private static string UpsertInt(string text, string key, int value)
    {
        var pattern = "\"" + Regex.Escape(key) + "\"\\s*:\\s*-?\\d+";
        var replacement = "\"" + key + "\": " + value;
        return Regex.IsMatch(text, pattern)
            ? Regex.Replace(text, pattern, replacement)
            : InsertBeforeFinalBrace(text, replacement);
    }

    private static string InsertBeforeFinalBrace(string text, string property)
    {
        var idx = text.LastIndexOf('}');
        if (idx < 0)
            return "{\n    " + property + "\n}\n";

        var before = text[..idx].TrimEnd();
        var after = text[idx..];
        var needsComma = before.Length > 0 && !before.EndsWith("{") && !before.EndsWith(",");
        return before + (needsComma ? "," : "") + "\n    " + property + "\n" + after;
    }
}
