/*
 * Lifecycle of the local Server.exe child process.
 *
 * Synchronous and minimal — the caller decides whether to push it onto a
 * background task. We track a single instance and also detect orphaned
 * Server.exe processes that originate from our install directory.
 */

using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace Bonfire.Service.Modules;

public static class ServerProcess
{
    private static readonly object Lock = new();
    private static Process? _tracked;

    public sealed record Status(bool Running, int? Pid, DateTime? StartedAt);

    public static Status QueryStatus()
    {
        lock (Lock)
        {
            if (_tracked is not null)
            {
                try
                {
                    if (_tracked.HasExited) _tracked = null;
                }
                catch { _tracked = null; }
            }

            if (_tracked is null)
            {
                foreach (var p in Process.GetProcessesByName("Server"))
                {
                    try
                    {
                        var path = p.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(path) &&
                            string.Equals(Path.GetFullPath(path),
                                Path.GetFullPath(Paths.ServerExecutable),
                                StringComparison.OrdinalIgnoreCase))
                        {
                            return new Status(true, p.Id, SafeStartTime(p));
                        }
                    }
                    catch { /* permission or 32/64 bit mismatch — skip */ }
                }
                return new Status(false, null, null);
            }

            return new Status(true, _tracked.Id, SafeStartTime(_tracked));
        }
    }

    public static bool Start(out string? error)
    {
        error = null;
        lock (Lock)
        {
            var existing = QueryStatus();
            if (existing.Running)
            {
                error = $"Server is already running (PID {existing.Pid}).";
                return false;
            }

            if (!File.Exists(Paths.ServerExecutable))
            {
                error = $"Server.exe not found at {Paths.ServerExecutable}. Install the server first.";
                return false;
            }

            if (FindPortConflict(out var conflict))
            {
                error = conflict;
                return false;
            }

            try
            {
                // Server.exe uses printf which is fully-buffered when stdout
                // is redirected to a pipe (output sits in a 4 KB buffer until
                // it's full or the process exits). It also calls
                // OutputDebugStringA on every log line — that broadcasts
                // through the OS DBWIN_BUFFER mechanism, which we listen on
                // separately (DebugListener) and write to server.log.
                //
                // So: leave the console window visible (user can see logs in
                // real time, matches the original Loader's behaviour) AND
                // tap OutputDebugString for our own log file.
                var psi = new ProcessStartInfo
                {
                    FileName = Paths.ServerExecutable,
                    WorkingDirectory = Paths.ServerDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = false, // keep server console visible
                };
                _tracked = Process.Start(psi);
                if (_tracked is null)
                {
                    error = "Process.Start returned null.";
                    return false;
                }
                DebugListener.Start(_tracked.Id,
                    Path.Combine(Paths.InstallRoot, "server.log"));

                // Server crashes immediately if Steam isn't running or
                // steam_appid.txt is missing — surface that quickly.
                Thread.Sleep(500);
                if (_tracked.HasExited)
                {
                    error = $"Server exited immediately with code {_tracked.ExitCode}. " +
                            "Check that Steam is running and steam_appid.txt is present.";
                    _tracked = null;
                    DebugListener.Stop();
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                _tracked = null;
                return false;
            }
        }
    }

    public static void Stop()
    {
        lock (Lock)
        {
            DebugListener.Stop();
            if (_tracked is not null)
            {
                try
                {
                    if (!_tracked.HasExited)
                    {
                        try { _tracked.CloseMainWindow(); } catch { }
                        if (!_tracked.WaitForExit(2000)) _tracked.Kill();
                    }
                }
                catch { }
                _tracked = null;
            }

            // Also clean up any orphan Server.exe from our install dir.
            try
            {
                foreach (var p in Process.GetProcessesByName("Server"))
                {
                    try
                    {
                        var path = p.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(path) &&
                            string.Equals(Path.GetFullPath(path),
                                Path.GetFullPath(Paths.ServerExecutable),
                                StringComparison.OrdinalIgnoreCase))
                        {
                            try { p.Kill(); } catch { }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    private static DateTime? SafeStartTime(Process p)
    {
        try { return p.StartTime; } catch { return null; }
    }

    private static bool FindPortConflict(out string? error)
    {
        error = null;

        try
        {
            var tcpListeners = IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Select(e => e.Port)
                .ToHashSet();
            var udpListeners = IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveUdpListeners()
                .Select(e => e.Port)
                .ToHashSet();

            var ports = ReadConfiguredPorts();
            var conflicts = new List<string>();

            if (tcpListeners.Contains(ports.AuthServerPort))
                conflicts.Add($"auth TCP {ports.AuthServerPort}");
            if (tcpListeners.Contains(ports.LoginServerPort))
                conflicts.Add($"login TCP {ports.LoginServerPort}");
            if (tcpListeners.Contains(ports.GameServerPort))
                conflicts.Add($"game TCP {ports.GameServerPort}");
            if (udpListeners.Contains(ports.GameServerPort))
                conflicts.Add($"game UDP {ports.GameServerPort}");
            if (tcpListeners.Contains(ports.WebUiServerPort))
                conflicts.Add($"WebUI TCP {ports.WebUiServerPort}");

            if (conflicts.Count == 0)
                return false;

            error =
                "Bonfire server ports are already in use (" +
                string.Join(", ", conflicts.Distinct()) +
                "). Close other Bonfire/Server.exe instances before lighting this fire.";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed record ConfiguredPorts(
        int AuthServerPort,
        int LoginServerPort,
        int GameServerPort,
        int WebUiServerPort);

    private static ConfiguredPorts ReadConfiguredPorts()
    {
        var cfg = ServerConfig.Load(Paths.ConfigFile);
        var text = File.Exists(Paths.ConfigFile)
            ? File.ReadAllText(Paths.ConfigFile)
            : "";

        return new ConfiguredPorts(
            AuthServerPort: ReadInt(text, "AuthServerPort", 50000),
            LoginServerPort: cfg.LoginServerPort > 0 ? cfg.LoginServerPort : 50050,
            GameServerPort: ReadInt(text, "GameServerPort", 50010),
            WebUiServerPort: ReadInt(text, "WebUIServerPort", 50005));
    }

    private static int ReadInt(string text, string key, int fallback)
    {
        if (string.IsNullOrEmpty(text))
            return fallback;

        var match = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(?<v>-?\\d+)");
        return match.Success && int.TryParse(match.Groups["v"].Value, out var value) && value > 0
            ? value
            : fallback;
    }
}
