namespace Bonfire.Service.Modules;

public sealed record Ds1SeamlessPayload(
    string SourceRoot,
    string SourceDll,
    bool IsBundled);

public sealed record Ds1BonfireCoordinator(
    string ServerId,
    string ServerName,
    string Hostname,
    string PrivateHostname,
    int Port);

public static class Ds1SeamlessPayloadResolver
{
    public const string BundledRootDirectoryName = "DS1SeamlessCoop";
    public const string RuntimeRootDirectoryName = "SeamlessCoop";
    public const string DllName = "ds1sc.dll";
    public const string LauncherName = "ds1sc_launcher.exe";
    private const string LegacyBundledRootDirectoryName = "Ds1SeamlessCoop";
    private const string LegacyLauncherSha256 =
        "448A819B43A3DDF85761F3D3348696E8457EB48DBCC2A7C467D571DBA64B8F74";
    private static readonly string[] BundledRootDirectoryNames =
    {
        BundledRootDirectoryName,
        LegacyBundledRootDirectoryName,
    };

    public static Ds1SeamlessPayload? Resolve(string configuredPath, string ds1ExePath)
    {
        foreach (var path in CandidatePaths(configuredPath, ds1ExePath))
        {
            var payload = ResolveOne(path);
            if (payload is not null)
                return payload;
        }
        return null;
    }

    public static bool IsValidPath(string path) => ResolveOne(path) is not null;

    public static bool TryPrepareForGame(
        string configuredPath,
        string ds1ExePath,
        string sessionPassword,
        Ds1BonfireCoordinator coordinator,
        out string injectedDll,
        out string error)
    {
        injectedDll = "";
        error = "";

        var payload = Resolve(configuredPath, ds1ExePath);
        if (payload is null)
        {
            error = string.IsNullOrWhiteSpace(configuredPath)
                ? "DS1 Seamless payload is not bundled."
                : "DS1 Seamless payload path is invalid: " + configuredPath;
            return false;
        }

        var gameDir = Path.GetDirectoryName(ds1ExePath);
        if (string.IsNullOrEmpty(gameDir))
        {
            error = "Could not resolve Dark Souls Remastered game directory.";
            return false;
        }

        var targetRoot = Path.Combine(gameDir, RuntimeRootDirectoryName);
        var targetDll = Path.Combine(targetRoot, DllName);
        var targetLauncher = Path.Combine(gameDir, LauncherName);

        try
        {
            if (!SamePath(payload.SourceRoot, targetRoot))
            {
                CopyDirectory(payload.SourceRoot, targetRoot);
            }

            RemoveLegacyStagedLauncher(targetLauncher);
            EnsureCrashDumpDirectories(targetRoot);
            WriteRuntimeSettings(targetRoot, sessionPassword);
            WriteCoordinatorFile(targetRoot, coordinator);
        }
        catch (Exception ex)
        {
            error = "Could not stage DS1 Seamless payload: " + ex.Message;
            return false;
        }

        if (!File.Exists(targetDll))
        {
            error = "DS1 Seamless payload was staged but ds1sc.dll is missing.";
            return false;
        }

        injectedDll = targetDll;
        return true;
    }

    private static void RemoveLegacyStagedLauncher(string targetLauncher)
    {
        if (!File.Exists(targetLauncher))
            return;

        using var stream = File.OpenRead(targetLauncher);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        if (string.Equals(hash, LegacyLauncherSha256, StringComparison.OrdinalIgnoreCase))
            File.Delete(targetLauncher);
    }

    private static void EnsureCrashDumpDirectories(string targetRoot)
    {
        Directory.CreateDirectory(Path.Combine(targetRoot, "crashdumps", "attachments"));
        Directory.CreateDirectory(Path.Combine(targetRoot, "crashdumps", "reports"));
    }

    private static void WriteRuntimeSettings(
        string targetRoot,
        string sessionPassword)
    {
        var settingsPath = Path.Combine(targetRoot, "ds1sc_settings.ini");
        var lines = File.Exists(settingsPath)
            ? File.ReadAllLines(settingsPath).ToList()
            : new List<string>();

        UpsertIniValue(lines, "PASSWORD", "cooppassword", sessionPassword);

        // Keep DS1 sessions scoped to the Bonfire fire. The DS1 runtime still
        // owns gameplay networking today, but Bonfire should not enable
        // unrelated serverless world events for a coordinated fire.
        UpsertIniValue(lines, "GAMEPLAY", "serverless_features", "0");

        Directory.CreateDirectory(targetRoot);
        File.WriteAllLines(settingsPath, lines);
    }

    private static void WriteCoordinatorFile(
        string targetRoot,
        Ds1BonfireCoordinator coordinator)
    {
        var coordinatorPath = Path.Combine(targetRoot, "bonfire_coordinator.ini");
        var lines = new[]
        {
            "[BONFIRE]",
            "coordinator_enabled = 1",
            "coordinator_protocol = bonfire-ds1-v1",
            "server_id = " + IniValue(coordinator.ServerId),
            "server_name = " + IniValue(coordinator.ServerName),
            "server_hostname = " + IniValue(coordinator.Hostname),
            "server_private_hostname = " + IniValue(coordinator.PrivateHostname),
            "server_port = " + coordinator.Port,
        };

        Directory.CreateDirectory(targetRoot);
        File.WriteAllLines(coordinatorPath, lines);
    }

    private static string IniValue(string value) =>
        (value ?? "").Replace("\r", " ").Replace("\n", " ").Trim();

    private static void UpsertIniValue(
        List<string> lines,
        string section,
        string key,
        string value)
    {
        var sectionHeader = "[" + section + "]";
        var inSection = false;
        var insertAt = lines.Count;

        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
            {
                if (inSection)
                {
                    insertAt = i;
                    break;
                }

                inSection = string.Equals(trimmed, sectionHeader, StringComparison.OrdinalIgnoreCase);
                if (inSection)
                    insertAt = i + 1;
                continue;
            }

            if (inSection)
            {
                insertAt = i + 1;
                var separator = trimmed.IndexOf('=');
                var existingKey = separator >= 0
                    ? trimmed[..separator].Trim()
                    : trimmed;
                if (string.Equals(existingKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = key + " = " + value;
                    return;
                }
            }
        }

        if (!inSection)
        {
            if (lines.Count > 0 && lines[^1].Length > 0)
                lines.Add("");
            lines.Add(sectionHeader);
            lines.Add(key + " = " + value);
            return;
        }

        lines.Insert(insertAt, key + " = " + value);
    }

    private static IEnumerable<string> CandidatePaths(string configuredPath, string ds1ExePath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            yield return configuredPath;

        foreach (var bundledRootDirectoryName in BundledRootDirectoryNames)
        {
            yield return Path.Combine(Paths.InstallRoot, "Loader", bundledRootDirectoryName);
            yield return Path.Combine(Paths.ServiceDirectory, "Loader", bundledRootDirectoryName);
        }

        var gameDir = Path.GetDirectoryName(ds1ExePath);
        if (!string.IsNullOrEmpty(gameDir))
            yield return Path.Combine(gameDir, RuntimeRootDirectoryName);
    }

    private static Ds1SeamlessPayload? ResolveOne(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            path = Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch
        {
            return null;
        }

        string? root = null;
        if (File.Exists(path) &&
            string.Equals(Path.GetFileName(path), DllName, StringComparison.OrdinalIgnoreCase))
        {
            root = Path.GetDirectoryName(path);
        }
        else if (Directory.Exists(path))
        {
            var directDll = Path.Combine(path, DllName);
            var nestedRuntimeRoot = Path.Combine(path, RuntimeRootDirectoryName);
            var nestedRuntimeDll = Path.Combine(nestedRuntimeRoot, DllName);

            if (File.Exists(directDll))
            {
                root = path;
            }
            else if (File.Exists(nestedRuntimeDll))
            {
                root = nestedRuntimeRoot;
            }
            else
            {
                foreach (var bundledRootDirectoryName in BundledRootDirectoryNames)
                {
                    var nestedBundledRoot = Path.Combine(path, bundledRootDirectoryName);
                    var nestedBundledDll = Path.Combine(nestedBundledRoot, DllName);
                    if (File.Exists(nestedBundledDll))
                    {
                        root = nestedBundledRoot;
                        break;
                    }
                }
            }
        }

        if (root is null)
            return null;

        var dll = Path.Combine(root, DllName);
        if (!File.Exists(dll))
            return null;

        var bundledRoot = Path.Combine(Paths.InstallRoot, "Loader", BundledRootDirectoryName);
        var legacyBundledRoot = Path.Combine(Paths.InstallRoot, "Loader", LegacyBundledRootDirectoryName);
        return new Ds1SeamlessPayload(
            root,
            dll,
            SamePath(root, bundledRoot) || SamePath(root, legacyBundledRoot));
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var sourceSubdir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, sourceSubdir);
            Directory.CreateDirectory(Path.Combine(targetDir, relative));
        }

        foreach (var sourceFile in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, sourceFile);
            var targetFile = Path.Combine(targetDir, relative);
            CopyFileIfDifferent(sourceFile, targetFile);
        }
    }

    private static void CopyFileIfDifferent(string sourceFile, string targetFile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
        if (File.Exists(targetFile) && FilesEqual(sourceFile, targetFile))
            return;
        File.Copy(sourceFile, targetFile, true);
    }

    private static bool FilesEqual(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (leftInfo.Length != rightInfo.Length)
            return false;

        using var leftStream = File.OpenRead(left);
        using var rightStream = File.OpenRead(right);
        var leftBuffer = new byte[81920];
        var rightBuffer = new byte[81920];

        while (true)
        {
            var leftRead = leftStream.Read(leftBuffer, 0, leftBuffer.Length);
            var rightRead = rightStream.Read(rightBuffer, 0, rightBuffer.Length);
            if (leftRead != rightRead)
                return false;
            if (leftRead == 0)
                return true;

            for (var i = 0; i < leftRead; i++)
            {
                if (leftBuffer[i] != rightBuffer[i])
                    return false;
            }
        }
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
