/*
 * Persistent app settings — only the bits Bonfire owns (game .exe paths,
 * "use separate saves" toggle). Lives in %APPDATA%\Bonfire\settings.json.
 */

using System.Text.Json;

namespace Bonfire.Service.Modules;

public sealed class GameSettings
{
    public string Ds2ExePath { get; set; } = "";
    public string Ds3ExePath { get; set; } = "";
    public string Ds2OverhaulPath { get; set; } = "";
    public bool UseSeparateSaves { get; set; } = true;

    public static string SettingsPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Bonfire");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    public static GameSettings Load()
    {
        try
        {
            var path = SettingsPath;
            if (!File.Exists(path)) return AutoDetect();
            var text = File.ReadAllText(path);
            return JsonSerializer.Deserialize<GameSettings>(text) ?? AutoDetect();
        }
        catch
        {
            return AutoDetect();
        }
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(SettingsPath,
                JsonSerializer.Serialize(this,
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Looks at the standard Steam install paths and the registry to find
    /// each Souls game's .exe, so the user typically doesn't have to point
    /// the file picker at it manually.
    /// </summary>
    public static GameSettings AutoDetect()
    {
        var s = new GameSettings();
        try
        {
            var ds2 = Loader.SteamUtils.GetGameInstallPath(
                "Dark Souls II Scholar of the First Sin");
            if (!string.IsNullOrEmpty(ds2))
            {
                var p = Path.Combine(ds2, "Game", "DarkSoulsII.exe");
                if (File.Exists(p)) s.Ds2ExePath = p;
            }
        }
        catch { }
        try
        {
            var ds3 = Loader.SteamUtils.GetGameInstallPath("DARK SOULS III");
            if (!string.IsNullOrEmpty(ds3))
            {
                var p = Path.Combine(ds3, "Game", "DarkSoulsIII.exe");
                if (File.Exists(p)) s.Ds3ExePath = p;
            }
        }
        catch { }
        return s;
    }

    public string PathFor(string gameType)
    {
        return string.Equals(gameType, "DarkSouls3", StringComparison.OrdinalIgnoreCase)
            ? Ds3ExePath
            : Ds2ExePath;
    }
}
