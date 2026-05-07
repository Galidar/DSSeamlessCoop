/*
 * Multi-server (multi-profile) support.
 *
 * Layout on disk:
 *
 *   <root>/
 *     Server/
 *       Server.exe                       (the binary — one copy, shared)
 *       Saved/
 *         default/                       (this is the LIVE/active profile —
 *           config.json                   what Server.exe reads on launch)
 *           private.key
 *           public.key
 *           database.sqlite
 *
 *     BonfireProfiles/                   (Bonfire's profile vault)
 *       <profile-id>/
 *         meta.json                      ({ name, gameType, createdAt })
 *         config.json
 *         private.key
 *         public.key
 *         database.sqlite                (optional — copied if present)
 *       <other-profile-id>/
 *         …
 *       active.txt                       (id of currently active profile)
 *
 * "Activating" a profile means: stop the server, copy its files into
 * Server/Saved/default/, write its id to active.txt, start the server again
 * (caller decides whether to start). Only one profile is "live" at a time
 * because Server.exe binds fixed ports.
 *
 * On first run, if Server/Saved/default/config.json exists but no profile
 * vault exists yet, we migrate the existing config into a "default" profile.
 */

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bonfire.Service.Modules;

public sealed class ProfileMeta
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("game_type")] public string GameType { get; set; } = "DarkSouls2";
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";
}

public static class Profiles
{
    public static string Vault =>
        Path.Combine(Paths.InstallRoot, "BonfireProfiles");

    public static string ActiveMarkerFile =>
        Path.Combine(Vault, "active.txt");

    public static string ProfileDir(string id) =>
        Path.Combine(Vault, SafeId(id));

    public static string ProfileMetaFile(string id) =>
        Path.Combine(ProfileDir(id), "meta.json");

    /// <summary>
    /// Returns every profile in the vault, sorted by created_at.
    /// Auto-migrates the legacy single-server install on first call.
    /// </summary>
    public static List<ProfileMeta> List()
    {
        EnsureMigrated();
        var list = new List<ProfileMeta>();
        if (!Directory.Exists(Vault)) return list;
        foreach (var dir in Directory.EnumerateDirectories(Vault))
        {
            var meta = Path.Combine(dir, "meta.json");
            if (!File.Exists(meta)) continue;
            try
            {
                var m = JsonSerializer.Deserialize<ProfileMeta>(File.ReadAllText(meta));
                if (m is not null) list.Add(m);
            }
            catch { }
        }
        list.Sort((a, b) => string.CompareOrdinal(a.CreatedAt, b.CreatedAt));
        return list;
    }

    public static string? GetActiveId()
    {
        EnsureMigrated();
        try
        {
            if (!File.Exists(ActiveMarkerFile)) return null;
            var id = File.ReadAllText(ActiveMarkerFile).Trim();
            return string.IsNullOrEmpty(id) ? null : id;
        }
        catch { return null; }
    }

    public static ProfileMeta Create(string name, string gameType)
    {
        EnsureMigrated();
        Directory.CreateDirectory(Vault);
        var id = NewId(name);
        var dir = ProfileDir(id);
        Directory.CreateDirectory(dir);
        var meta = new ProfileMeta
        {
            Id = id,
            Name = name,
            GameType = gameType,
            CreatedAt = DateTime.UtcNow.ToString("o"),
        };
        WriteMeta(meta);

        // Seed config.json so the profile is self-sufficient — the
        // ServerName and GameType the user chose at creation must end up
        // in Saved/default when this profile activates. If we leave it
        // unset, Server.exe writes its compile-time defaults
        // (ServerName="Dark Souls Server", GameType="DarkSouls3") even
        // when the user explicitly picked DS2.
        try
        {
            var profileCfg = Path.Combine(dir, "config.json");
            if (File.Exists(Paths.ConfigFile))
            {
                // Clone full schema from the running default so we keep
                // matchmaking parameters / anti-cheat thresholds / etc.
                File.Copy(Paths.ConfigFile, profileCfg, overwrite: true);
                PatchConfigFields(profileCfg, name, gameType);
            }
            else
            {
                // No live config to clone (server not yet installed, or
                // user wiped it). Write a minimal stub — Server.exe will
                // fill in the rest on first start (RuntimeConfig has
                // sensible defaults for every field) and Save() the
                // expanded version back.
                File.WriteAllText(profileCfg, MinimalConfigJson(name, gameType));
            }
        }
        catch { /* non-fatal */ }
        return meta;
    }

    /// <summary>
    /// Two-field stub config.json — just enough for Server.exe to load
    /// without falling back to its own DarkSouls3 / "Dark Souls Server"
    /// defaults. Server's RuntimeConfig::Load merges the rest from struct
    /// defaults and writes the full document back via SaveConfig().
    /// </summary>
    private static string MinimalConfigJson(string serverName, string gameType)
    {
        var n = (serverName ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        var g = (gameType ?? "DarkSouls3").Replace("\\", "\\\\").Replace("\"", "\\\"");
        return "{\n" +
               "    \"ServerName\": \"" + n + "\",\n" +
               "    \"GameType\": \"" + g + "\"\n" +
               "}\n";
    }

    public static void Rename(string id, string newName)
    {
        var meta = ReadMeta(id) ?? throw new Exception("Profile not found.");
        meta.Name = newName;
        WriteMeta(meta);

        // Keep config.json in sync so the rename takes effect on the next
        // server start. We don't restart Server.exe here — the user will
        // notice on next launch, which matches how the original Loader
        // handled config edits.
        try
        {
            var profileCfg = Path.Combine(ProfileDir(id), "config.json");
            if (File.Exists(profileCfg)) PatchConfigFields(profileCfg, newName, null);
            if (GetActiveId() == id && File.Exists(Paths.ConfigFile))
                PatchConfigFields(Paths.ConfigFile, newName, null);
        }
        catch { /* best effort */ }
    }

    public static void SetGameType(string id, string gameType)
    {
        var meta = ReadMeta(id) ?? throw new Exception("Profile not found.");
        meta.GameType = gameType;
        WriteMeta(meta);

        try
        {
            var profileCfg = Path.Combine(ProfileDir(id), "config.json");
            if (File.Exists(profileCfg)) PatchConfigFields(profileCfg, null, gameType);
            if (GetActiveId() == id && File.Exists(Paths.ConfigFile))
                PatchConfigFields(Paths.ConfigFile, null, gameType);
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Updates ServerName and/or GameType in a config.json without disturbing
    /// any other field. Mirrors the regex-based replace strategy in
    /// ServerConfig so users who hand-edit other fields don't lose their work.
    /// Pass null to skip a field.
    /// </summary>
    private static void PatchConfigFields(string configPath, string? serverName, string? gameType)
    {
        if (!File.Exists(configPath)) return;
        var text = File.ReadAllText(configPath);
        if (serverName is not null) text = ReplaceJsonString(text, "ServerName", serverName);
        if (gameType is not null)   text = ReplaceJsonString(text, "GameType",   gameType);
        File.WriteAllText(configPath, text);
    }

    private static string? ReadStringField(string configPath, string key)
    {
        if (!File.Exists(configPath)) return null;
        var text = File.ReadAllText(configPath);
        var m = System.Text.RegularExpressions.Regex.Match(
            text,
            "\"" + System.Text.RegularExpressions.Regex.Escape(key) +
            "\"\\s*:\\s*\"(?<v>(?:\\\\.|[^\"\\\\])*)\"");
        return m.Success
            ? System.Text.RegularExpressions.Regex.Unescape(m.Groups["v"].Value)
            : null;
    }

    private static string ReplaceJsonString(string text, string key, string value)
    {
        var pattern = "\"" + System.Text.RegularExpressions.Regex.Escape(key) +
                      "\"\\s*:\\s*\"(?:\\\\.|[^\"\\\\])*\"";
        var escaped = (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        var replacement = "\"" + key + "\": \"" + escaped + "\"";
        return System.Text.RegularExpressions.Regex.IsMatch(text, pattern)
            ? System.Text.RegularExpressions.Regex.Replace(text, pattern, replacement)
            : text;
    }

    public static void Delete(string id)
    {
        var dir = ProfileDir(id);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        if (GetActiveId() == id)
        {
            try { File.Delete(ActiveMarkerFile); } catch { }
        }
    }

    /// <summary>
    /// Snapshots the live Server/Saved/default/ files into the given
    /// profile (so changes made via the WebUI or directly on disk get
    /// captured before we swap to a different profile).
    /// </summary>
    public static void SaveActiveStateTo(string id)
    {
        var dir = ProfileDir(id);
        Directory.CreateDirectory(dir);
        CopyIfExists(Paths.ConfigFile, Path.Combine(dir, "config.json"));
        CopyIfExists(Path.Combine(Paths.SavedDirectory, "private.key"),
            Path.Combine(dir, "private.key"));
        CopyIfExists(Path.Combine(Paths.SavedDirectory, "public.key"),
            Path.Combine(dir, "public.key"));
        CopyIfExists(Path.Combine(Paths.SavedDirectory, "database.sqlite"),
            Path.Combine(dir, "database.sqlite"));
    }

    /// <summary>
    /// Copies the profile's files into Server/Saved/default/ so the next
    /// Server.exe launch picks them up. Caller is responsible for stopping
    /// the running server first and starting it after.
    ///
    /// We CLEAR Saved/default first so a fresh profile doesn't inherit
    /// the previous one's config / keys / database. The previous profile's
    /// state must already have been saved to its own folder by the caller
    /// (RPC profiles.activate / game.launch_local handle this).
    /// </summary>
    public static void Activate(string id)
    {
        var dir = ProfileDir(id);
        if (!Directory.Exists(dir))
            throw new Exception("Profile not found.");

        Directory.CreateDirectory(Paths.SavedDirectory);

        // Clean slate. Without this, an empty new profile would be activated
        // on top of the previous profile's files (Server.exe would read the
        // wrong config.json, the keypair would be wrong, etc.).
        foreach (var f in new[]
        {
            "config.json", "private.key", "public.key",
            "database.sqlite", "database.sqlite-journal",
            "last_activity.time"
        })
        {
            var p = Path.Combine(Paths.SavedDirectory, f);
            if (File.Exists(p))
            {
                try { File.Delete(p); } catch { }
            }
        }

        CopyIfExists(Path.Combine(dir, "config.json"), Paths.ConfigFile);
        CopyIfExists(Path.Combine(dir, "private.key"),
            Path.Combine(Paths.SavedDirectory, "private.key"));
        CopyIfExists(Path.Combine(dir, "public.key"),
            Path.Combine(Paths.SavedDirectory, "public.key"));
        CopyIfExists(Path.Combine(dir, "database.sqlite"),
            Path.Combine(Paths.SavedDirectory, "database.sqlite"));

        // GameType belt-and-suspenders: Server.exe's C++ default is
        // DarkSouls3 — if we leave Saved/default with no GameType field,
        // a DS2 profile would boot Server.exe in DS3 mode and the game
        // would get "server unavailable". So force GameType from the
        // profile metadata (user picks the game at profile creation, never
        // changes it after).
        //
        // ServerName, on the other hand, IS user-editable via Configure —
        // we must NOT overwrite it from meta on every activation, or the
        // user's renames would be silently reverted. Pull it the other
        // way: if the live config has an explicit ServerName, sync it back
        // into the meta so the row label and Configure field both follow
        // the user's choice. This makes config.ServerName the source of
        // truth, with meta.Name as a cached mirror.
        //
        // If config.json doesn't exist at all (profile created before
        // Server.exe was installed), seed a stub so Server.exe doesn't
        // fall back to its compile-time defaults.
        var meta = ReadMeta(id);
        if (meta is not null)
        {
            if (File.Exists(Paths.ConfigFile))
            {
                PatchConfigFields(Paths.ConfigFile, null, meta.GameType);
                var liveName = ReadStringField(Paths.ConfigFile, "ServerName");
                if (!string.IsNullOrEmpty(liveName) && liveName != meta.Name)
                {
                    meta.Name = liveName;
                    WriteMeta(meta);
                }
            }
            else
            {
                File.WriteAllText(Paths.ConfigFile,
                    MinimalConfigJson(meta.Name, meta.GameType));
            }
        }

        File.WriteAllText(ActiveMarkerFile, id);
    }

    // ---------- internals ----------

    private static ProfileMeta? ReadMeta(string id)
    {
        var path = ProfileMetaFile(id);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<ProfileMeta>(File.ReadAllText(path));
        }
        catch { return null; }
    }

    private static void WriteMeta(ProfileMeta meta)
    {
        Directory.CreateDirectory(ProfileDir(meta.Id));
        File.WriteAllText(ProfileMetaFile(meta.Id),
            JsonSerializer.Serialize(meta,
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string NewId(string seed)
    {
        var basePart = SafeId(string.IsNullOrEmpty(seed) ? "profile" : seed);
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString("x");
        return basePart + "-" + stamp;
    }

    /// <summary>
    /// Strip characters that aren't safe as a folder name. Lowercase ASCII,
    /// digits, hyphen and underscore only.
    /// </summary>
    private static string SafeId(string raw)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in raw.ToLowerInvariant())
        {
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
                c == '-' || c == '_')
                sb.Append(c);
            else if (c == ' ')
                sb.Append('-');
        }
        if (sb.Length == 0) sb.Append("profile");
        return sb.ToString();
    }

    private static void CopyIfExists(string src, string dst)
    {
        if (!File.Exists(src)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Copy(src, dst, true);
    }

    /// <summary>
    /// First-run migration: if there's a config.json in Server/Saved/default
    /// but no BonfireProfiles vault, create a "default" profile and copy the
    /// existing files into it. Mark it as active.
    /// </summary>
    private static bool _migrationChecked;
    public static void EnsureMigrated()
    {
        if (_migrationChecked) return;
        _migrationChecked = true;
        try
        {
            if (Directory.Exists(Vault) &&
                Directory.EnumerateFiles(Vault, "*", SearchOption.AllDirectories).Any())
            {
                return; // vault already populated
            }
            if (!Paths.ConfigExists) return; // nothing to migrate

            // Bootstrap the vault with the current install as "default".
            var liveCfg = ServerConfig.Load(Paths.ConfigFile);
            var meta = Create(
                name: string.IsNullOrEmpty(liveCfg.ServerName)
                    ? "Default"
                    : liveCfg.ServerName,
                gameType: string.IsNullOrEmpty(liveCfg.GameType)
                    ? "DarkSouls2"
                    : liveCfg.GameType);
            SaveActiveStateTo(meta.Id);
            File.WriteAllText(ActiveMarkerFile, meta.Id);
        }
        catch { /* best effort */ }
    }
}
