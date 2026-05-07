/*
 * Resolves filesystem paths to the Server install relative to the service
 * binary. The Flutter UI expects the standard packaged layout:
 *
 *   <root>/
 *     Bonfire.exe                  (the Flutter app)
 *     BonfireService.exe           (this service)
 *     Server/
 *       Server.exe
 *       Saved/default/config.json
 */

namespace Bonfire.Service.Modules;

public static class Paths
{
    public static string ServiceDirectory =>
        Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory)
            ?? Environment.CurrentDirectory;

    public static string InstallRoot
    {
        get
        {
            var dir = ServiceDirectory;
            // Packaged: <root>/BonfireService.exe (sibling of Server/).
            if (Directory.Exists(Path.Combine(dir, "Server")))
                return dir;
            // Dev fallback: walk up one level (some build outputs nest one deeper).
            var parent = Path.GetFullPath(Path.Combine(dir, ".."));
            if (Directory.Exists(Path.Combine(parent, "Server")))
                return parent;
            return dir;
        }
    }

    public static string ServerDirectory => Path.Combine(InstallRoot, "Server");
    public static string ServerExecutable => Path.Combine(ServerDirectory, "Server.exe");
    public static string SteamAppIdFile => Path.Combine(ServerDirectory, "steam_appid.txt");
    public static string SavedDirectory => Path.Combine(ServerDirectory, "Saved", "default");
    public static string ConfigFile => Path.Combine(SavedDirectory, "config.json");

    public static bool ServerInstalled => File.Exists(ServerExecutable);
    public static bool ConfigExists => File.Exists(ConfigFile);
}
