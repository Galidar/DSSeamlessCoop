/*
 * Dark Souls - Open Server (Galidar fork)
 *
 * Resolves filesystem paths to the local Server.exe install relative to the
 * Loader executable. The expected layout is the one produced by
 * Tools/generate_package_windows.bat:
 *
 *   <root>/
 *     Loader/
 *       Loader.exe                <- Application.ExecutablePath
 *     Server/
 *       Server.exe
 *       Saved/default/config.json
 *       steam_appid.txt
 *
 * For dev builds where Loader.exe is run from Bin\x64_release\, the Server
 * folder is also resolved relative to the loader directory.
 */

using System;
using System.IO;
using System.Windows.Forms;

namespace Loader.LocalServer
{
    public static class LocalServerPaths
    {
        public static string LoaderDirectory =>
            Path.GetDirectoryName(Application.ExecutablePath) ?? Environment.CurrentDirectory;

        /// <summary>
        /// Root of the install — sibling of Loader/ and Server/ in packaged builds.
        /// </summary>
        public static string InstallRoot
        {
            get
            {
                var dir = LoaderDirectory;
                // Packaged: <root>/Loader/Loader.exe → <root>
                var parent = Path.GetFullPath(Path.Combine(dir, ".."));
                if (Directory.Exists(Path.Combine(parent, "Server")))
                {
                    return parent;
                }
                // Dev build: Loader.exe sits next to Server.exe
                return dir;
            }
        }

        public static string ServerDirectory => Path.Combine(InstallRoot, "Server");
        public static string ServerExecutable => Path.Combine(ServerDirectory, "Server.exe");
        public static string SteamAppIdFile => Path.Combine(ServerDirectory, "steam_appid.txt");
        public static string SavedDirectory => Path.Combine(ServerDirectory, "Saved", "default");
        public static string ConfigFile => Path.Combine(SavedDirectory, "config.json");
        public static string PrivateKeyFile => Path.Combine(SavedDirectory, "private.key");
        public static string PublicKeyFile => Path.Combine(SavedDirectory, "public.key");

        public static bool ServerInstalled => File.Exists(ServerExecutable);
        public static bool ConfigExists => File.Exists(ConfigFile);
    }
}
