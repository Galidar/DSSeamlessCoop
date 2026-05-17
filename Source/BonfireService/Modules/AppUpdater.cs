using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bonfire.Service.Modules;

public static class AppUpdater
{
    public const string ChannelStable = "stable";
    public const string ChannelExperimental = "experimental";

    private static readonly object ChannelLock = new();
    private static string _channel = ChannelExperimental;
    private static bool _channelLoadedFromDisk;

    private static string ChannelFilePath =>
        Path.Combine(Paths.InstallRoot, "Runtime", "AppUpdater", "channel.json");

    public sealed record UpdateStatus(
        string CurrentVersion,
        string LatestVersion,
        string LatestTag,
        string ReleaseUrl,
        string AssetName,
        string AssetUrl,
        long AssetSize,
        bool UpdateAvailable,
        string Channel);

    /// <summary>
    /// Returns the currently-active update channel ("stable" or
    /// "experimental"). Lazily loads the persisted preference on first
    /// call so a Bonfire restart preserves the user's selection.
    /// </summary>
    public static string GetChannel()
    {
        lock (ChannelLock)
        {
            EnsureChannelLoaded();
            return _channel;
        }
    }

    /// <summary>
    /// Normalises <paramref name="raw"/> to "stable" or "experimental",
    /// updates the in-memory channel, and persists to disk best-effort.
    /// Returns the resolved channel.
    /// </summary>
    public static string SetChannel(string raw)
    {
        var resolved = NormalizeChannel(raw);
        lock (ChannelLock)
        {
            EnsureChannelLoaded();
            _channel = resolved;
            PersistChannel(resolved);
        }
        return resolved;
    }

    /// <summary>
    /// Probe GitHub for the latest release applicable to
    /// <paramref name="channel"/>. Pass <c>null</c> or empty to use the
    /// persisted preference (typically what the Flutter UI does on its
    /// silent-boot probe before it has read the channel back).
    /// </summary>
    public static async Task<UpdateStatus?> QueryStatusAsync(
        string? channel = null,
        CancellationToken ct = default)
    {
        var resolved = string.IsNullOrWhiteSpace(channel)
            ? GetChannel()
            : NormalizeChannel(channel);

        var info = await ReleaseDownloader.QueryLatestAsync(resolved, ct);
        if (info is null)
            return null;

        var current = Bonfire.Service.Program.ServiceVersion;
        var latest = CleanVersion(info.TagName);
        return new UpdateStatus(
            CurrentVersion: current,
            LatestVersion: latest,
            LatestTag: info.TagName,
            ReleaseUrl: info.ReleaseUrl,
            AssetName: info.AssetName,
            AssetUrl: info.AssetUrl,
            AssetSize: info.AssetSize,
            UpdateAvailable: CompareVersions(latest, current) > 0,
            Channel: resolved);
    }

    private static string NormalizeChannel(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ChannelExperimental;
        var trimmed = raw.Trim().ToLowerInvariant();
        return trimmed switch
        {
            "stable" or "release" or "latest" => ChannelStable,
            "experimental" or "pre" or "prerelease" or "beta" or "rc" => ChannelExperimental,
            _ => ChannelExperimental,
        };
    }

    private static void EnsureChannelLoaded()
    {
        if (_channelLoadedFromDisk) return;
        _channelLoadedFromDisk = true;
        try
        {
            if (!File.Exists(ChannelFilePath)) return;
            var raw = File.ReadAllText(ChannelFilePath);
            var node = JsonNode.Parse(raw) as JsonObject;
            var stored = node?["channel"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(stored))
                _channel = NormalizeChannel(stored);
        }
        catch
        {
            // Best effort — fall back to the default channel.
        }
    }

    private static void PersistChannel(string channel)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ChannelFilePath)!);
            var node = new JsonObject { ["channel"] = channel };
            File.WriteAllText(
                ChannelFilePath,
                node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best effort — the in-memory value is authoritative.
        }
    }

    public static async Task<UpdateStatus> StageAndLaunchAsync(
        int uiProcessId,
        Action<long, long?> onProgress,
        CancellationToken ct = default)
    {
        // Apply respects the currently-pinned channel — if the user is
        // on "stable", we install the latest non-prerelease even when an
        // experimental tag is newer.
        var status = await QueryStatusAsync(channel: null, ct)
            ?? throw new InvalidOperationException("Could not resolve the latest release.");

        if (!status.UpdateAvailable)
            return status;

        var workRoot = Path.Combine(Path.GetTempPath(), "BonfireUpdater", status.LatestTag);
        var zipPath = Path.Combine(workRoot, status.AssetName);
        var stagingRoot = Path.Combine(workRoot, "staging");
        var scriptPath = Path.Combine(workRoot, "apply-update.ps1");

        Directory.CreateDirectory(workRoot);
        if (Directory.Exists(stagingRoot))
            Directory.Delete(stagingRoot, recursive: true);

        var downloaded = await ReleaseDownloader.DownloadAsync(
            status.AssetUrl,
            zipPath,
            onProgress,
            ct);
        if (!downloaded)
            throw new InvalidOperationException("Download failed.");

        ZipFile.ExtractToDirectory(zipPath, stagingRoot, overwriteFiles: true);

        var sourceRoot = ResolveReleaseRoot(stagingRoot);
        if (!File.Exists(Path.Combine(sourceRoot, "Bonfire.exe")) ||
            !File.Exists(Path.Combine(sourceRoot, "BonfireService.exe")))
        {
            throw new InvalidOperationException("Downloaded release does not contain Bonfire.exe and BonfireService.exe.");
        }

        File.WriteAllText(
            scriptPath,
            BuildUpdaterScript(
                uiProcessId,
                Environment.ProcessId,
                sourceRoot,
                Paths.InstallRoot,
                Path.Combine(Paths.InstallRoot, "Bonfire.exe"),
                workRoot),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File {QuoteArg(scriptPath)}",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        Process.Start(psi);

        return status;
    }

    private static string ResolveReleaseRoot(string stagingRoot)
    {
        var directBonfire = Path.Combine(stagingRoot, "Bonfire.exe");
        if (File.Exists(directBonfire))
            return stagingRoot;

        var childRoots = Directory.EnumerateDirectories(stagingRoot).ToList();
        foreach (var child in childRoots)
        {
            if (File.Exists(Path.Combine(child, "Bonfire.exe")))
                return child;
        }

        return stagingRoot;
    }

    private static string BuildUpdaterScript(
        int uiProcessId,
        int serviceProcessId,
        string sourceRoot,
        string installRoot,
        string bonfireExe,
        string workRoot)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("$ProgressPreference = 'SilentlyContinue'");
        sb.AppendLine($"$uiPid = {uiProcessId.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"$servicePid = {serviceProcessId.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"$source = {PsString(sourceRoot)}");
        sb.AppendLine($"$dest = {PsString(installRoot)}");
        sb.AppendLine($"$bonfire = {PsString(bonfireExe)}");
        sb.AppendLine($"$workRoot = {PsString(workRoot)}");
        sb.AppendLine(@"
function Wait-BonfireProcess([int] $processId) {
  if ($processId -le 0) { return }
  try {
    $p = Get-Process -Id $processId -ErrorAction Stop
    Wait-Process -Id $processId -Timeout 60 -ErrorAction Stop
  } catch {
  }
}

Wait-BonfireProcess $uiPid
Wait-BonfireProcess $servicePid
Start-Sleep -Milliseconds 400

New-Item -ItemType Directory -Force -Path $dest | Out-Null
$legacyDs2Payloads = @(
  (Join-Path $dest 'Loader\DS2SeamlessCoop'),
  (Join-Path $dest 'Loader\ds2multoverhaul')
)
foreach ($legacyDs2Payload in $legacyDs2Payloads) {
  if (Test-Path -LiteralPath $legacyDs2Payload) {
    Remove-Item -LiteralPath $legacyDs2Payload -Recurse -Force
  }
}
Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $dest -Recurse -Force

try {
  Start-Process -FilePath $bonfire -WorkingDirectory $dest
} catch {
}

Start-Sleep -Seconds 2
try {
  Remove-Item -LiteralPath $workRoot -Recurse -Force
} catch {
}
");
        return sb.ToString();
    }

    private static string CleanVersion(string tag)
    {
        var version = tag.Trim();
        return version.StartsWith("v", StringComparison.OrdinalIgnoreCase)
            ? version[1..]
            : version;
    }

    private static int CompareVersions(string left, string right)
    {
        var l = ParsedVersion.Parse(left);
        var r = ParsedVersion.Parse(right);
        return l.CompareTo(r);
    }

    private static string PsString(string value)
    {
        return "'" + value.Replace("'", "''") + "'";
    }

    private static string QuoteArg(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    // Made public so ReleaseDownloader can sort its list of candidate
    // releases by the same comparator AppUpdater uses to detect
    // "update available". Keeps the SemVer ordering authoritative in
    // one place — prerelease tags lose to plain releases of the same
    // numeric version (per SemVer 2.0), and two prereleases compare
    // alphabetically on their suffix.
    public readonly record struct ParsedVersion(int Major, int Minor, int Patch, int Build, string Prerelease)
        : IComparable<ParsedVersion>
    {
        public static ParsedVersion Parse(string value)
        {
            value = value.Trim();
            var noBuild = value.Split('+', 2)[0];
            var prerelease = "";
            var numeric = noBuild;
            var dash = noBuild.IndexOf('-');
            if (dash >= 0)
            {
                prerelease = noBuild[(dash + 1)..];
                numeric = noBuild[..dash];
            }

            var parts = numeric.Split('.');
            return new ParsedVersion(
                ReadPart(parts, 0),
                ReadPart(parts, 1),
                ReadPart(parts, 2),
                ReadPart(parts, 3),
                prerelease);
        }

        public int CompareTo(ParsedVersion other)
        {
            var major = Major.CompareTo(other.Major);
            if (major != 0) return major;
            var minor = Minor.CompareTo(other.Minor);
            if (minor != 0) return minor;
            var patch = Patch.CompareTo(other.Patch);
            if (patch != 0) return patch;
            var build = Build.CompareTo(other.Build);
            if (build != 0) return build;

            if (Prerelease.Length == 0 && other.Prerelease.Length > 0)
                return 1;
            if (Prerelease.Length > 0 && other.Prerelease.Length == 0)
                return -1;
            return string.Compare(Prerelease, other.Prerelease, StringComparison.OrdinalIgnoreCase);
        }

        private static int ReadPart(string[] parts, int index)
        {
            return index < parts.Length &&
                   int.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : 0;
        }
    }
}
