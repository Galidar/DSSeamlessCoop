/*
 * Downloads the latest DSSeamlessCoop_V*.zip release from the fork's GitHub repo
 * and extracts it into the install root. Progress is reported via a
 * callback so the RPC layer can forward `download.progress` notifications
 * to the Flutter UI.
 */

using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Bonfire.Service.Modules;

public sealed class ReleaseDownloader
{
    public const string Repo = "Galidar/DSSeamlessCoop";
    public const string AssetNamePrefix = "DSSeamlessCoop_V";
    public const string LegacyAssetName = "windows.zip";

    public sealed record ReleaseInfo(
        string TagName,
        string ReleaseUrl,
        string AssetName,
        string AssetUrl,
        long AssetSize);

    public static async Task<ReleaseInfo?> QueryLatestAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Bonfire/0.1");

            // /releases (list) instead of /releases/latest — the
            // 'latest' endpoint hard-excludes prereleases per
            // GitHub's API contract, which means anything tagged
            // experimental/beta/rc never reaches an updater calling
            // it. The list endpoint returns up to 30 most-recent
            // releases including prereleases (drafts are excluded
            // anyway for unauthenticated requests). We then pick the
            // highest by SemVer, so prereleases of a newer version
            // beat plain releases of an older version and a plain
            // release beats a prerelease of the same numeric version.
            //
            // This costs ~30 KB more bandwidth per check vs the old
            // single-release endpoint — negligible for an update
            // probe that runs once per Bonfire launch.
            var json = await http.GetStringAsync(
                $"https://api.github.com/repos/{Repo}/releases?per_page=30", ct);

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            ReleaseInfo? best = null;
            AppUpdater.ParsedVersion bestVer = default;
            bool haveBest = false;

            foreach (var release in doc.RootElement.EnumerateArray())
            {
                // Skip drafts defensively — they shouldn't be in the
                // unauthenticated response but the property exists.
                if (release.TryGetProperty("draft", out var draftEl) &&
                    draftEl.ValueKind == JsonValueKind.True)
                {
                    continue;
                }

                var candidate = ExtractCandidate(release);
                if (candidate is null) continue;

                var ver = AppUpdater.ParsedVersion.Parse(
                    candidate.TagName.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                        ? candidate.TagName[1..]
                        : candidate.TagName);
                if (!haveBest || ver.CompareTo(bestVer) > 0)
                {
                    best = candidate;
                    bestVer = ver;
                    haveBest = true;
                }
            }

            return best;
        }
        catch
        {
            return null;
        }
    }

    private static ReleaseInfo? ExtractCandidate(JsonElement release)
    {
        var tag = release.TryGetProperty("tag_name", out var tagEl)
            ? tagEl.GetString() ?? ""
            : "";
        if (string.IsNullOrEmpty(tag)) return null;
        var releaseUrl = release.TryGetProperty("html_url", out var htmlUrlEl)
            ? htmlUrlEl.GetString() ?? ""
            : "";
        if (!release.TryGetProperty("assets", out var assets) ||
            assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameEl)
                ? nameEl.GetString()
                : null;
            if (!IsReleaseAssetName(name)) continue;

            var url = asset.TryGetProperty("browser_download_url", out var urlEl)
                ? urlEl.GetString() ?? ""
                : "";
            if (string.IsNullOrEmpty(url)) continue;
            var size = asset.TryGetProperty("size", out var sizeEl) &&
                       sizeEl.TryGetInt64(out var sizeVal)
                ? sizeVal
                : 0L;

            return new ReleaseInfo(tag, releaseUrl, name ?? "", url, size);
        }
        return null;
    }

    private static bool IsReleaseAssetName(string? name)
    {
        return string.Equals(name, LegacyAssetName, StringComparison.Ordinal) ||
               (!string.IsNullOrEmpty(name) &&
                name.StartsWith(AssetNamePrefix, StringComparison.Ordinal) &&
                name.EndsWith(".zip", StringComparison.Ordinal));
    }

    public static async Task<bool> DownloadAsync(
        string url,
        string destPath,
        Action<long, long?> onProgress,
        CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Bonfire/0.1");

            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            long? total = resp.Content.Headers.ContentLength;
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(destPath);

            var buffer = new byte[81920];
            long received = 0;
            int read;
            var nextReport = DateTime.UtcNow;
            while ((read = await src.ReadAsync(buffer.AsMemory(), ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                if (DateTime.UtcNow >= nextReport)
                {
                    onProgress(received, total);
                    nextReport = DateTime.UtcNow.AddMilliseconds(150);
                }
            }
            onProgress(received, total);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool Extract(string zipPath, string destDir)
    {
        try
        {
            Directory.CreateDirectory(destDir);
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                var fullDest = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
                if (!fullDest.StartsWith(Path.GetFullPath(destDir), StringComparison.OrdinalIgnoreCase))
                    continue; // zip-slip guard

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(fullDest);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(fullDest)!);
                entry.ExtractToFile(fullDest, true);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

}
