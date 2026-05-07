/*
 * Downloads the latest release windows.zip from the fork's GitHub repo
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
    public const string AssetName = "windows.zip";

    public sealed record ReleaseInfo(string TagName, string AssetUrl, long AssetSize);

    public static async Task<ReleaseInfo?> QueryLatestAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Bonfire/0.1");
            var json = await http.GetStringAsync(
                $"https://api.github.com/repos/{Repo}/releases/latest", ct);

            // Proper JSON parse — earlier versions used a regex on the raw
            // payload, which broke as soon as we hit assets with nested
            // {uploader: {...}} objects (regex couldn't handle nesting).
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagEl)
                ? tagEl.GetString() ?? ""
                : "";

            if (!root.TryGetProperty("assets", out var assets) ||
                assets.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameEl)
                    ? nameEl.GetString()
                    : null;
                if (!string.Equals(name, AssetName, StringComparison.Ordinal))
                    continue;

                var url = asset.TryGetProperty("browser_download_url", out var urlEl)
                    ? urlEl.GetString() ?? ""
                    : "";
                var size = asset.TryGetProperty("size", out var sizeEl) &&
                           sizeEl.TryGetInt64(out var sizeVal)
                    ? sizeVal
                    : 0L;

                return string.IsNullOrEmpty(url)
                    ? null
                    : new ReleaseInfo(tag, url, size);
            }

            return null;
        }
        catch
        {
            return null;
        }
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
