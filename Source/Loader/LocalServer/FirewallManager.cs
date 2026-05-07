/*
 * Dark Souls - Open Server (Galidar fork)
 *
 * Wraps `netsh advfirewall firewall ...` to install/remove the rules required
 * to run a local DS3OS server. Modifying firewall rules requires admin rights,
 * so the helper relaunches netsh through ShellExecute with verb "runas" — the
 * UAC prompt appears, the user approves, and the elevated child process
 * applies the rules.
 *
 * The full ruleset is run through a single batch file we generate on the fly
 * so the user only sees one UAC prompt instead of seven.
 */

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Loader.LocalServer
{
    public static class FirewallManager
    {
        public const string RulePrefix = "DS3OS";

        // Ports DS3OS listens on. Keep in sync with config.json defaults.
        public static readonly int[] TcpPorts = new[] { 50000, 50010, 50050, 50020 };
        public static readonly int[] UdpPorts = new[] { 50000, 50010, 50050, 50020 };
        public const int WebUiTcpPort = 50005;
        public const string GameRangeTcp = "50060-50200";
        public const string GameRangeUdp = "50060-50200";

        public class Rule
        {
            public string Name;
            public bool Present;
        }

        /// <summary>
        /// Returns the firewall rules we manage and whether each one is
        /// currently installed.
        /// </summary>
        public static Rule[] QueryRules()
        {
            var names = new[]
            {
                RulePrefix + " Server TCP",
                RulePrefix + " Server UDP",
                RulePrefix + " WebUI",
                RulePrefix + " GameRange TCP",
                RulePrefix + " GameRange UDP",
                RulePrefix + " Server.exe",
                RulePrefix + " Loader.exe",
            };

            var existing = ListExistingRuleNames();
            return names.Select(n => new Rule
            {
                Name = n,
                Present = existing.Contains(n, StringComparer.OrdinalIgnoreCase),
            }).ToArray();
        }

        public static bool AllRulesInstalled()
        {
            var rules = QueryRules();
            foreach (var r in rules)
            {
                if (!r.Present) return false;
            }
            return true;
        }

        /// <summary>
        /// Builds a temporary .bat file with all delete + add commands and
        /// runs it elevated via ShellExecute("runas"). Returns true if the
        /// elevated process exited with code 0, false on UAC denial or error.
        /// </summary>
        public static bool ApplyRulesElevated(string serverExePath, string loaderExePath)
        {
            var script = BuildApplyScript(serverExePath, loaderExePath);
            return RunBatchElevated(script);
        }

        public static bool RemoveRulesElevated()
        {
            var script = BuildRemoveScript();
            return RunBatchElevated(script);
        }

        // ---------- internals ----------

        private static string BuildApplyScript(string serverExePath, string loaderExePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            AppendDelete(sb);

            // Ports — joined comma list for single rule each.
            var tcp = string.Join(",", TcpPorts);
            var udp = string.Join(",", UdpPorts);

            sb.AppendLine("netsh advfirewall firewall add rule name=\"" + RulePrefix + " Server TCP\" dir=in action=allow protocol=TCP localport=" + tcp);
            sb.AppendLine("netsh advfirewall firewall add rule name=\"" + RulePrefix + " Server UDP\" dir=in action=allow protocol=UDP localport=" + udp);
            sb.AppendLine("netsh advfirewall firewall add rule name=\"" + RulePrefix + " WebUI\" dir=in action=allow protocol=TCP localport=" + WebUiTcpPort);
            sb.AppendLine("netsh advfirewall firewall add rule name=\"" + RulePrefix + " GameRange TCP\" dir=in action=allow protocol=TCP localport=" + GameRangeTcp);
            sb.AppendLine("netsh advfirewall firewall add rule name=\"" + RulePrefix + " GameRange UDP\" dir=in action=allow protocol=UDP localport=" + GameRangeUdp);

            if (!string.IsNullOrEmpty(serverExePath))
            {
                sb.AppendLine("netsh advfirewall firewall add rule name=\"" + RulePrefix + " Server.exe\" dir=in action=allow program=\"" + serverExePath + "\" enable=yes");
            }
            if (!string.IsNullOrEmpty(loaderExePath))
            {
                sb.AppendLine("netsh advfirewall firewall add rule name=\"" + RulePrefix + " Loader.exe\" dir=in action=allow program=\"" + loaderExePath + "\" enable=yes");
            }

            // Exit cleanly so ShellExecute reports success.
            sb.AppendLine("exit /b 0");
            return sb.ToString();
        }

        private static string BuildRemoveScript()
        {
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            AppendDelete(sb);
            sb.AppendLine("exit /b 0");
            return sb.ToString();
        }

        private static void AppendDelete(StringBuilder sb)
        {
            string[] names = {
                RulePrefix + " Server TCP",
                RulePrefix + " Server UDP",
                RulePrefix + " WebUI",
                RulePrefix + " GameRange TCP",
                RulePrefix + " GameRange UDP",
                RulePrefix + " Server.exe",
                RulePrefix + " Loader.exe",
            };
            foreach (var n in names)
            {
                sb.AppendLine("netsh advfirewall firewall delete rule name=\"" + n + "\" >nul 2>&1");
            }
        }

        private static bool RunBatchElevated(string scriptContents)
        {
            string scriptPath = Path.Combine(Path.GetTempPath(),
                "ds3os-firewall-" + Guid.NewGuid().ToString("N") + ".bat");
            try
            {
                File.WriteAllText(scriptPath, scriptContents);

                var psi = new ProcessStartInfo
                {
                    FileName = scriptPath,
                    UseShellExecute = true,
                    Verb = "runas",      // triggers UAC
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                };
                using (var proc = Process.Start(psi))
                {
                    if (proc == null) return false;
                    proc.WaitForExit();
                    return proc.ExitCode == 0;
                }
            }
            catch
            {
                // UAC denied or other error
                return false;
            }
            finally
            {
                try { File.Delete(scriptPath); } catch { }
            }
        }

        private static System.Collections.Generic.List<string> ListExistingRuleNames()
        {
            var names = new System.Collections.Generic.List<string>();
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = "advfirewall firewall show rule name=all",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                using (var proc = Process.Start(psi))
                {
                    string line;
                    while ((line = proc.StandardOutput.ReadLine()) != null)
                    {
                        // Both English ("Rule Name:") and Spanish ("Nombre de regla:")
                        // localizations include the literal name on the right of the colon.
                        var idx = line.IndexOf(':');
                        if (idx <= 0) continue;
                        var left = line.Substring(0, idx).Trim();
                        if (!left.Contains("Name", StringComparison.OrdinalIgnoreCase) &&
                            !left.Contains("Nombre", StringComparison.OrdinalIgnoreCase) &&
                            !left.Contains("Regel", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        var right = line.Substring(idx + 1).Trim();
                        if (right.StartsWith(RulePrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            names.Add(right);
                        }
                    }
                    proc.WaitForExit();
                }
            }
            catch { }
            return names;
        }
    }
}
