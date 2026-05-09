namespace Bonfire.Service.Modules;

public sealed record Ds3SeamlessPayload(
    string SourceRoot,
    string SourceDll,
    string? SourceLauncher,
    bool IsBundled);

public static class Ds3SeamlessPayloadResolver
{
    public const string RootDirectoryName = "SeamlessCoop";
    public const string DllName = "ds3sc.dll";
    public const string LauncherName = "ds3sc_launcher.exe";

    public static Ds3SeamlessPayload? Resolve(string configuredPath, string ds3ExePath)
    {
        foreach (var path in CandidatePaths(configuredPath, ds3ExePath))
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
        string ds3ExePath,
        string sessionPassword,
        out string injectedDll,
        out string? launcherExe,
        out string error)
    {
        injectedDll = "";
        launcherExe = null;
        error = "";

        var payload = Resolve(configuredPath, ds3ExePath);
        if (payload is null)
        {
            error = string.IsNullOrWhiteSpace(configuredPath)
                ? "DS3 Seamless payload is not bundled."
                : "DS3 Seamless payload path is invalid: " + configuredPath;
            return false;
        }

        var gameDir = Path.GetDirectoryName(ds3ExePath);
        if (string.IsNullOrEmpty(gameDir))
        {
            error = "Could not resolve Dark Souls III game directory.";
            return false;
        }

        var targetRoot = Path.Combine(gameDir, RootDirectoryName);
        var targetDll = Path.Combine(targetRoot, DllName);
        var targetLauncher = Path.Combine(gameDir, LauncherName);

        try
        {
            if (!SamePath(payload.SourceRoot, targetRoot))
            {
                CopyDirectory(payload.SourceRoot, targetRoot);
            }

            if (!string.IsNullOrEmpty(payload.SourceLauncher))
            {
                CopyFileIfDifferent(payload.SourceLauncher, targetLauncher);
            }

            EnsureCrashDumpDirectories(targetRoot);
            WriteSessionPassword(targetRoot, sessionPassword);
        }
        catch (Exception ex)
        {
            error = "Could not stage DS3 Seamless payload: " + ex.Message;
            return false;
        }

        if (!File.Exists(targetDll))
        {
            error = "DS3 Seamless payload was staged but ds3sc.dll is missing.";
            return false;
        }

        injectedDll = targetDll;
        launcherExe = File.Exists(targetLauncher) ? targetLauncher : null;
        return true;
    }

    private static void EnsureCrashDumpDirectories(string targetRoot)
    {
        Directory.CreateDirectory(Path.Combine(targetRoot, "crashdumps", "attachments"));
        Directory.CreateDirectory(Path.Combine(targetRoot, "crashdumps", "reports"));
    }

    private static void WriteSessionPassword(string targetRoot, string sessionPassword)
    {
        var settingsPath = Path.Combine(targetRoot, "ds3sc_settings.ini");
        var lines = File.Exists(settingsPath)
            ? File.ReadAllLines(settingsPath).ToList()
            : new List<string>();

        var inPasswordSection = false;
        var wrotePassword = false;

        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
            {
                if (inPasswordSection && !wrotePassword)
                {
                    lines.Insert(i, "cooppassword = " + sessionPassword);
                    wrotePassword = true;
                    i++;
                }

                inPasswordSection = string.Equals(
                    trimmed, "[PASSWORD]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (inPasswordSection &&
                trimmed.StartsWith("cooppassword", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = "cooppassword = " + sessionPassword;
                wrotePassword = true;
            }
        }

        if (!wrotePassword)
        {
            if (lines.Count > 0 && lines[^1].Length > 0)
                lines.Add("");
            if (!inPasswordSection)
                lines.Add("[PASSWORD]");
            lines.Add("cooppassword = " + sessionPassword);
        }

        Directory.CreateDirectory(targetRoot);
        File.WriteAllLines(settingsPath, lines);
    }

    private static IEnumerable<string> CandidatePaths(string configuredPath, string ds3ExePath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            yield return configuredPath;

        yield return Path.Combine(Paths.InstallRoot, "Loader", RootDirectoryName);
        yield return Path.Combine(Paths.ServiceDirectory, "Loader", RootDirectoryName);

        var gameDir = Path.GetDirectoryName(ds3ExePath);
        if (!string.IsNullOrEmpty(gameDir))
            yield return Path.Combine(gameDir, RootDirectoryName);
    }

    private static Ds3SeamlessPayload? ResolveOne(string path)
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
        string? launcher = null;

        if (File.Exists(path) &&
            string.Equals(Path.GetFileName(path), DllName, StringComparison.OrdinalIgnoreCase))
        {
            root = Path.GetDirectoryName(path);
        }
        else if (Directory.Exists(path))
        {
            var directDll = Path.Combine(path, DllName);
            var nestedRoot = Path.Combine(path, RootDirectoryName);
            var nestedDll = Path.Combine(nestedRoot, DllName);

            if (File.Exists(directDll))
            {
                root = path;
            }
            else if (File.Exists(nestedDll))
            {
                root = nestedRoot;
                var packageLauncher = Path.Combine(path, LauncherName);
                if (File.Exists(packageLauncher))
                    launcher = packageLauncher;
            }
        }

        if (root is null)
            return null;

        var dll = Path.Combine(root, DllName);
        if (!File.Exists(dll))
            return null;

        launcher ??= ResolveLauncher(root);
        var bundledRoot = Path.Combine(Paths.InstallRoot, "Loader", RootDirectoryName);
        return new Ds3SeamlessPayload(
            root,
            dll,
            launcher,
            SamePath(root, bundledRoot));
    }

    private static string? ResolveLauncher(string root)
    {
        var parent = Path.GetDirectoryName(root);
        if (!string.IsNullOrEmpty(parent))
        {
            var sibling = Path.Combine(parent, LauncherName);
            if (File.Exists(sibling))
                return sibling;
        }

        var loaderSibling = Path.Combine(Paths.InstallRoot, "Loader", LauncherName);
        if (File.Exists(loaderSibling))
            return loaderSibling;

        return null;
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
