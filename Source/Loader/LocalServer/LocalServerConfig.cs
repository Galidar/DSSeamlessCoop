/*
 * Dark Souls - Open Server (Galidar fork)
 *
 * Reads/writes Server/Saved/default/config.json. We don't model every field —
 * only the ones the wizard needs to manage. Other fields are preserved through
 * a string-based replace so we never accidentally drop or corrupt server-side
 * settings the user may have hand-edited.
 */

using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Loader.LocalServer
{
    public class LocalServerConfig
    {
        public string ServerName { get; set; } = "My DS3OS Server";
        public string ServerDescription { get; set; } = "A custom Dark Souls server.";
        public string Password { get; set; } = "";
        public string GameType { get; set; } = "DarkSouls2";
        public string ServerHostname { get; set; } = "";        // WAN
        public string ServerPrivateHostname { get; set; } = ""; // LAN
        public bool Advertise { get; set; } = true;

        /// <summary>
        /// Reads only the fields we care about. Returns defaults if the file
        /// doesn't exist yet.
        /// </summary>
        public static LocalServerConfig Load(string path)
        {
            var cfg = new LocalServerConfig();
            if (!File.Exists(path))
            {
                return cfg;
            }

            var text = File.ReadAllText(path);
            cfg.ServerName            = ReadString(text, "ServerName")            ?? cfg.ServerName;
            cfg.ServerDescription     = ReadString(text, "ServerDescription")     ?? cfg.ServerDescription;
            cfg.Password              = ReadString(text, "Password")              ?? cfg.Password;
            cfg.GameType              = ReadString(text, "GameType")              ?? cfg.GameType;
            cfg.ServerHostname        = ReadString(text, "ServerHostname")        ?? cfg.ServerHostname;
            cfg.ServerPrivateHostname = ReadString(text, "ServerPrivateHostname") ?? cfg.ServerPrivateHostname;
            cfg.Advertise             = ReadBool(text, "Advertise") ?? cfg.Advertise;
            return cfg;
        }

        /// <summary>
        /// Updates only the fields we manage in an existing config.json template,
        /// preserving formatting and other entries. If no template exists,
        /// returns false — caller should make sure the server has run once
        /// (which generates a default) or copy the bundled template.
        /// </summary>
        public bool SaveOver(string path)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var text = File.ReadAllText(path);
            text = ReplaceString(text, "ServerName",            ServerName);
            text = ReplaceString(text, "ServerDescription",     ServerDescription);
            text = ReplaceString(text, "Password",              Password);
            text = ReplaceString(text, "GameType",              GameType);
            text = ReplaceString(text, "ServerHostname",        ServerHostname);
            text = ReplaceString(text, "ServerPrivateHostname", ServerPrivateHostname);
            text = ReplaceBool(text,   "Advertise",             Advertise);

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
            return true;
        }

        // ---- helpers ----

        private static string ReadString(string text, string key)
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
}
