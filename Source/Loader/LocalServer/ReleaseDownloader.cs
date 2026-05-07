/*
 * Dark Souls - Open Server (Galidar fork)
 *
 * Downloads the latest release windows.zip from a public GitHub repository
 * and extracts it next to the Loader executable. Progress callbacks are
 * marshalled back to the caller so the wizard can drive a progress bar.
 */

using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Loader.LocalServer
{
    public class ReleaseDownloader
    {
        public string Repo { get; }
        public string AssetName { get; }

        public ReleaseDownloader(string repo, string assetName = "windows.zip")
        {
            Repo = repo;
            AssetName = assetName;
        }

        public class ReleaseInfo
        {
            public string TagName;
            public string AssetUrl;
            public long AssetSize;
        }

        /// <summary>
        /// Hits api.github.com/.../releases/latest and returns the URL of the
        /// requested asset. Returns null on failure.
        /// </summary>
        public ReleaseInfo QueryLatest()
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
                using (var wc = new WebClient())
                {
                    wc.Headers.Add(HttpRequestHeader.UserAgent, "DSSeamlessCoop-Loader");
                    var json = wc.DownloadString("https://api.github.com/repos/" + Repo + "/releases/latest");

                    var tag = ReadJsonString(json, "tag_name");

                    // Find the asset block matching AssetName, then read its url + size.
                    // The assets array is well-formed JSON; a regex over the asset object
                    // is good enough since names/urls/sizes are simple scalars.
                    var assetPattern = new Regex(
                        "\\{[^{}]*\"name\"\\s*:\\s*\"" + Regex.Escape(AssetName) + "\"[^{}]*\\}",
                        RegexOptions.Singleline);
                    var match = assetPattern.Match(json);
                    if (!match.Success) return null;

                    var block = match.Value;
                    var url = ReadJsonString(block, "browser_download_url");
                    var sizeStr = ReadJsonNumber(block, "size");
                    long size = 0;
                    long.TryParse(sizeStr, out size);

                    if (string.IsNullOrEmpty(url)) return null;
                    return new ReleaseInfo { TagName = tag, AssetUrl = url, AssetSize = size };
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Downloads <paramref name="url"/> to <paramref name="destZipPath"/> and
        /// reports progress (0..100) plus bytes-downloaded/total via the callback.
        /// </summary>
        public Task<bool> DownloadAsync(
            string url,
            string destZipPath,
            Action<int, long, long> onProgress,
            CancellationToken ct = default)
        {
            var tcs = new TaskCompletionSource<bool>();
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
                var wc = new WebClient();
                wc.Headers.Add(HttpRequestHeader.UserAgent, "DSSeamlessCoop-Loader");

                wc.DownloadProgressChanged += (s, e) =>
                {
                    onProgress?.Invoke(e.ProgressPercentage, e.BytesReceived, e.TotalBytesToReceive);
                };
                wc.DownloadFileCompleted += (s, e) =>
                {
                    wc.Dispose();
                    if (e.Cancelled || e.Error != null)
                    {
                        tcs.TrySetResult(false);
                        return;
                    }
                    tcs.TrySetResult(true);
                };
                if (ct.CanBeCanceled)
                {
                    ct.Register(() => { try { wc.CancelAsync(); } catch { } });
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destZipPath));
                wc.DownloadFileAsync(new Uri(url), destZipPath);
            }
            catch
            {
                tcs.TrySetResult(false);
            }
            return tcs.Task;
        }

        /// <summary>
        /// Extracts <paramref name="zipPath"/> into <paramref name="destDir"/>,
        /// overwriting any existing files. Returns true on success.
        /// </summary>
        public static bool Extract(string zipPath, string destDir)
        {
            try
            {
                Directory.CreateDirectory(destDir);
                using (var archive = ZipFile.OpenRead(zipPath))
                {
                    foreach (var entry in archive.Entries)
                    {
                        var fullDest = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
                        if (!fullDest.StartsWith(Path.GetFullPath(destDir), StringComparison.OrdinalIgnoreCase))
                        {
                            // zip-slip guard — should never trigger on our own zips
                            continue;
                        }
                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            Directory.CreateDirectory(fullDest);
                            continue;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(fullDest));
                        entry.ExtractToFile(fullDest, true);
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ---- tiny JSON helpers (no external dep) ----

        private static string ReadJsonString(string text, string key)
        {
            var m = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"(?<v>(?:\\\\.|[^\"\\\\])*)\"");
            return m.Success ? Regex.Unescape(m.Groups["v"].Value) : null;
        }

        private static string ReadJsonNumber(string text, string key)
        {
            var m = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(?<v>-?\\d+(?:\\.\\d+)?)");
            return m.Success ? m.Groups["v"].Value : null;
        }
    }
}
