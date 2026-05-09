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
}
