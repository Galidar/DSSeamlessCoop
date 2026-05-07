/*
 * Dark Souls - Open Server (Galidar fork)
 *
 * Manages the lifecycle of the local Server.exe child process:
 *   - Start (with proper working directory)
 *   - Stop (graceful first, then forced)
 *   - Status (running / not running / since-when)
 *
 * The class is intentionally small and synchronous. The wizard runs the
 * start/stop calls on a worker task so the UI thread stays responsive.
 */

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Loader.LocalServer
{
    public static class LocalServerProcess
    {
        private static readonly object Lock = new object();
        private static Process Tracked;

        public class Status
        {
            public bool Running;
            public int Pid;
            public DateTime? StartedAt;
        }

        public static Status QueryStatus()
        {
            lock (Lock)
            {
                // Did our tracked process die?
                if (Tracked != null)
                {
                    try
                    {
                        if (Tracked.HasExited)
                        {
                            Tracked = null;
                        }
                    }
                    catch
                    {
                        Tracked = null;
                    }
                }

                // No tracked process — check if Server.exe is running anyway.
                if (Tracked == null)
                {
                    var procs = Process.GetProcessesByName("Server");
                    foreach (var p in procs)
                    {
                        try
                        {
                            // Match the one in our install directory.
                            var path = p.MainModule?.FileName;
                            if (!string.IsNullOrEmpty(path) &&
                                string.Equals(Path.GetFullPath(path),
                                              Path.GetFullPath(LocalServerPaths.ServerExecutable),
                                              StringComparison.OrdinalIgnoreCase))
                            {
                                return new Status
                                {
                                    Running = true,
                                    Pid = p.Id,
                                    StartedAt = SafeStartTime(p),
                                };
                            }
                        }
                        catch { /* probably a 32-bit/permission issue, skip */ }
                    }

                    return new Status { Running = false };
                }

                return new Status
                {
                    Running = true,
                    Pid = Tracked.Id,
                    StartedAt = SafeStartTime(Tracked),
                };
            }
        }

        /// <summary>
        /// Launches Server.exe from the install directory. Returns true if
        /// the process was successfully started.
        /// </summary>
        public static bool Start(out string error)
        {
            error = null;
            lock (Lock)
            {
                var existing = QueryStatus();
                if (existing.Running)
                {
                    error = "Server is already running (PID " + existing.Pid + ").";
                    return false;
                }

                var exe = LocalServerPaths.ServerExecutable;
                if (!File.Exists(exe))
                {
                    error = "Server.exe not found at " + exe + ". Run the Setup download step first.";
                    return false;
                }

                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = exe,
                        WorkingDirectory = LocalServerPaths.ServerDirectory,
                        UseShellExecute = false,
                        CreateNoWindow = false, // keep server console visible
                    };
                    Tracked = Process.Start(psi);
                    if (Tracked == null)
                    {
                        error = "Process.Start returned null.";
                        return false;
                    }

                    // Server.exe initialises Steam SDK on a worker thread; if
                    // it fails immediately it'll exit within a couple seconds.
                    // Give it a brief moment so callers can react.
                    Thread.Sleep(500);
                    if (Tracked.HasExited)
                    {
                        error = "Server exited immediately with code " + Tracked.ExitCode +
                                ". Check that Steam is running and steam_appid.txt is present.";
                        Tracked = null;
                        return false;
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    Tracked = null;
                    return false;
                }
            }
        }

        public static void Stop()
        {
            lock (Lock)
            {
                // Kill tracked process if any
                if (Tracked != null)
                {
                    try
                    {
                        if (!Tracked.HasExited)
                        {
                            try { Tracked.CloseMainWindow(); } catch { }
                            if (!Tracked.WaitForExit(2000))
                            {
                                Tracked.Kill();
                            }
                        }
                    }
                    catch { }
                    Tracked = null;
                }

                // Also clean up any orphaned Server.exe from our install dir.
                try
                {
                    var procs = Process.GetProcessesByName("Server");
                    foreach (var p in procs)
                    {
                        try
                        {
                            var path = p.MainModule?.FileName;
                            if (!string.IsNullOrEmpty(path) &&
                                string.Equals(Path.GetFullPath(path),
                                              Path.GetFullPath(LocalServerPaths.ServerExecutable),
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
    }
}
