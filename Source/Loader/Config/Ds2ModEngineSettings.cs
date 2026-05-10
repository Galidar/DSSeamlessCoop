/*
 * Dark Souls II ModEngine-style data package detection.
 *
 * This is shared by the WinForms Loader and BonfireService so both launch paths
 * generate the same injector config for DS2 overhaul packages.
 */

using System;
using System.Collections.Generic;
using System.IO;

#nullable enable

namespace Loader
{
    public sealed class Ds2ModEngineSettings
    {
        private const string OverrideDirectoryName = "DS2SeamlessCoop";
        private const string LegacyOverrideDirectoryName = "ds2multoverhaul";

        public bool EnableModFileOverrides { get; init; }
        public string ModOverrideDirectory { get; init; } = "";
        public bool CacheModFilePaths { get; init; } = true;
        public bool UseAlternateSaveFile { get; init; }
        public bool EnableShadowResolutionPatches { get; init; }
        public int DirectionalShadowResolution { get; init; } = 2048;
        public int DynamicAtlasShadowResolution { get; init; } = 1024;
        public int DynamicPointShadowResolution { get; init; } = 256;
        public int DynamicSpotShadowResolution { get; init; } = 512;

        public static Ds2ModEngineSettings Resolve(
            string exePath, string injectorPath, string gameType, string? explicitOverridePath = null)
        {
            if (!string.Equals(gameType, "DarkSouls2", StringComparison.OrdinalIgnoreCase))
                return new Ds2ModEngineSettings();

            var exeDir = string.IsNullOrWhiteSpace(exePath)
                ? ""
                : Path.GetDirectoryName(exePath) ?? "";
            var injectorDir = string.IsNullOrWhiteSpace(injectorPath)
                ? ""
                : Path.GetDirectoryName(injectorPath) ?? "";
            var envOverride = Environment.GetEnvironmentVariable("DS2_OVERHAUL_DIR");

            var candidates = new List<string?>
            {
                explicitOverridePath,
                envOverride,
            };
            if (!string.IsNullOrEmpty(injectorDir))
            {
                candidates.Add(Path.Combine(injectorDir, OverrideDirectoryName));
                candidates.Add(Path.Combine(injectorDir, LegacyOverrideDirectoryName));
            }
            if (!string.IsNullOrEmpty(exeDir))
            {
                candidates.Add(Path.Combine(exeDir, OverrideDirectoryName));
                candidates.Add(Path.Combine(exeDir, LegacyOverrideDirectoryName));
            }

            foreach (var baseDir in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            })
            {
                if (!Directory.Exists(baseDir))
                    continue;

                foreach (var releaseDir in Directory.EnumerateDirectories(
                             baseDir, "DS2*Multiplayer Overhaul - Version 1.0.4a*"))
                {
                    candidates.Add(Path.Combine(releaseDir, OverrideDirectoryName));
                    candidates.Add(Path.Combine(releaseDir, LegacyOverrideDirectoryName));
                }
            }

            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                    continue;

                var fullPath = ResolveDs2OverrideRoot(candidate);
                if (string.IsNullOrEmpty(fullPath))
                    continue;

                var releaseRoot = Directory.GetParent(fullPath)?.FullName ?? "";
                var iniPath = Path.Combine(releaseRoot, "modengine.ini");
                var useOverrideDirectory = ReadIniBool(
                    iniPath, "files", "useModOverrideDirectory", true);
                var configuredOverrideRoot = ResolveModEngineOverrideRoot(
                    releaseRoot, ReadIniValue(iniPath, "files", "modOverrideDirectory"));
                if (!string.IsNullOrEmpty(configuredOverrideRoot))
                    fullPath = configuredOverrideRoot;

                return new Ds2ModEngineSettings
                {
                    EnableModFileOverrides = useOverrideDirectory,
                    ModOverrideDirectory = useOverrideDirectory ? fullPath : "",
                    CacheModFilePaths = ReadIniBool(iniPath, "files", "cacheFilePaths", true),
                    UseAlternateSaveFile = ReadIniBool(
                        iniPath, "savefile", "useAlternateSaveFile", true),
                    EnableShadowResolutionPatches = true,
                    DirectionalShadowResolution = ReadIniInt(
                        iniPath, "rendering", "directionalShadowResolution", 4096) / 2,
                    DynamicAtlasShadowResolution = ReadIniInt(
                        iniPath, "rendering", "dynamicAtlasShadowResolution", 2048) / 2,
                    DynamicPointShadowResolution = ReadIniInt(
                        iniPath, "rendering", "dynamicPointShadowResolution", 512) / 2,
                    DynamicSpotShadowResolution = ReadIniInt(
                        iniPath, "rendering", "dynamicSpotShadowResolution", 1024) / 2,
                };
            }

            return new Ds2ModEngineSettings();
        }

        private static string ResolveModEngineOverrideRoot(string releaseRoot, string? configuredPath)
        {
            if (string.IsNullOrWhiteSpace(configuredPath))
                return "";

            configuredPath = configuredPath.Trim().Trim('"');
            var rootedLikeModEngine = configuredPath.StartsWith("\\") || configuredPath.StartsWith("/");
            var candidate = rootedLikeModEngine
                ? Path.Combine(releaseRoot, configuredPath.TrimStart('\\', '/'))
                : Path.Combine(releaseRoot, configuredPath);

            return ResolveDs2OverrideRoot(candidate);
        }

        private static string ResolveDs2OverrideRoot(string candidate)
        {
            var fullPath = Path.GetFullPath(candidate);
            if (IsDs2OverrideRoot(fullPath))
                return fullPath;

            var nested = Path.Combine(fullPath, OverrideDirectoryName);
            if (IsDs2OverrideRoot(nested))
                return nested;

            var legacyNested = Path.Combine(fullPath, LegacyOverrideDirectoryName);
            return IsDs2OverrideRoot(legacyNested) ? legacyNested : "";
        }

        private static bool IsDs2OverrideRoot(string path)
        {
            return Directory.Exists(path) &&
                (Directory.Exists(Path.Combine(path, "Param")) ||
                 Directory.Exists(Path.Combine(path, "map")) ||
                 Directory.Exists(Path.Combine(path, "menu")) ||
                 File.Exists(Path.Combine(path, "enc_regulation.bnd.dcx")));
        }

        private static bool ReadIniBool(string iniPath, string section, string key, bool defaultValue)
        {
            var value = ReadIniValue(iniPath, section, key);
            if (string.IsNullOrWhiteSpace(value))
                return defaultValue;

            value = value.Trim().Trim('"');
            if (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase))
                return true;
            if (value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase))
                return false;

            return defaultValue;
        }

        private static int ReadIniInt(string iniPath, string section, string key, int defaultValue)
        {
            var value = ReadIniValue(iniPath, section, key);
            return int.TryParse(value, out var result) ? result : defaultValue;
        }

        private static string? ReadIniValue(string iniPath, string section, string key)
        {
            if (!File.Exists(iniPath))
                return null;

            var currentSection = "";
            foreach (var rawLine in File.ReadLines(iniPath))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#"))
                    continue;

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    currentSection = line[1..^1].Trim();
                    continue;
                }

                if (!currentSection.Equals(section, StringComparison.OrdinalIgnoreCase))
                    continue;

                var separator = line.IndexOf('=');
                if (separator < 0)
                    continue;

                var lineKey = line[..separator].Trim();
                if (lineKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                    return line[(separator + 1)..].Trim();
            }

            return null;
        }
    }
}
