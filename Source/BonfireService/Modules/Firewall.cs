/*
 * Windows Firewall rules for the DS3OS server.
 *
 * Ports come straight from the bundled config.json defaults. Modifying
 * firewall rules requires admin, so the helper relaunches a temp .bat
 * containing the full ruleset via ShellExecute "runas" — single UAC
 * prompt for all rules instead of one per command.
 */

using System.Diagnostics;
using System.Text;

namespace Bonfire.Service.Modules;

public static class Firewall
{
    public const string RulePrefix = "DS3OS";

    public static readonly int[] TcpPorts = { 50000, 50010, 50050, 50020 };
    public static readonly int[] UdpPorts = { 50000, 50010, 50050, 50020 };
    public const int WebUiTcpPort = 50005;
    public const string GameRangeTcp = "50060-50200";
    public const string GameRangeUdp = "50060-50200";
    // Phase 4d (HKMP overlay): pose-bridge UDP listens here on both
    // host and guest PCs. Must be reachable from the peer's WAN
    // address — opens a hole the same way the existing DS3OS rules
    // do. Cleaned up by RemoveRulesElevated() along with everything
    // else if the user uninstalls.
    public const int PoseBridgeUdpPort = 50031;

    public static readonly string[] RuleNames =
    {
        RulePrefix + " Server TCP",
        RulePrefix + " Server UDP",
        RulePrefix + " WebUI",
        RulePrefix + " GameRange TCP",
        RulePrefix + " GameRange UDP",
        RulePrefix + " Server.exe",
        RulePrefix + " Loader.exe",
        RulePrefix + " PoseBridge UDP",
    };

    public sealed record RuleStatus(string Name, bool Present);

    public static RuleStatus[] QueryRules()
    {
        var existing = ListExistingRuleNames();
        return RuleNames
            .Select(n => new RuleStatus(n, existing.Contains(n, StringComparer.OrdinalIgnoreCase)))
            .ToArray();
    }

    public static bool AllRulesInstalled() => QueryRules().All(r => r.Present);

    public static bool ApplyRulesElevated(string serverExePath, string loaderExePath) =>
        RunBatchElevated(BuildApplyScript(serverExePath, loaderExePath));

    public static bool RemoveRulesElevated() => RunBatchElevated(BuildRemoveScript());

    // ---------- internals ----------

    private static string BuildApplyScript(string serverExePath, string loaderExePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        AppendDelete(sb);

        var tcp = string.Join(",", TcpPorts);
        var udp = string.Join(",", UdpPorts);

        sb.AppendLine($"netsh advfirewall firewall add rule name=\"{RulePrefix} Server TCP\" dir=in action=allow protocol=TCP localport={tcp}");
        sb.AppendLine($"netsh advfirewall firewall add rule name=\"{RulePrefix} Server UDP\" dir=in action=allow protocol=UDP localport={udp}");
        sb.AppendLine($"netsh advfirewall firewall add rule name=\"{RulePrefix} WebUI\" dir=in action=allow protocol=TCP localport={WebUiTcpPort}");
        sb.AppendLine($"netsh advfirewall firewall add rule name=\"{RulePrefix} GameRange TCP\" dir=in action=allow protocol=TCP localport={GameRangeTcp}");
        sb.AppendLine($"netsh advfirewall firewall add rule name=\"{RulePrefix} GameRange UDP\" dir=in action=allow protocol=UDP localport={GameRangeUdp}");
        // Phase 4d HKMP overlay UDP backbone.
        sb.AppendLine($"netsh advfirewall firewall add rule name=\"{RulePrefix} PoseBridge UDP\" dir=in action=allow protocol=UDP localport={PoseBridgeUdpPort}");

        if (!string.IsNullOrEmpty(serverExePath))
            sb.AppendLine($"netsh advfirewall firewall add rule name=\"{RulePrefix} Server.exe\" dir=in action=allow program=\"{serverExePath}\" enable=yes");
        if (!string.IsNullOrEmpty(loaderExePath))
            sb.AppendLine($"netsh advfirewall firewall add rule name=\"{RulePrefix} Loader.exe\" dir=in action=allow program=\"{loaderExePath}\" enable=yes");

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
        foreach (var n in RuleNames)
            sb.AppendLine($"netsh advfirewall firewall delete rule name=\"{n}\" >nul 2>&1");
    }

    private static bool RunBatchElevated(string scriptContents)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(),
            "bonfire-firewall-" + Guid.NewGuid().ToString("N") + ".bat");
        try
        {
            File.WriteAllText(scriptPath, scriptContents);

            var psi = new ProcessStartInfo
            {
                FileName = scriptPath,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.WaitForExit();
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }

    private static List<string> ListExistingRuleNames()
    {
        var names = new List<string>();
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
            using var proc = Process.Start(psi);
            if (proc is null) return names;

            string? line;
            while ((line = proc.StandardOutput.ReadLine()) != null)
            {
                var idx = line.IndexOf(':');
                if (idx <= 0) continue;
                var left = line[..idx].Trim();
                if (!left.Contains("Name", StringComparison.OrdinalIgnoreCase) &&
                    !left.Contains("Nombre", StringComparison.OrdinalIgnoreCase) &&
                    !left.Contains("Regel", StringComparison.OrdinalIgnoreCase))
                    continue;

                var right = line[(idx + 1)..].Trim();
                if (right.StartsWith(RulePrefix, StringComparison.OrdinalIgnoreCase))
                    names.Add(right);
            }
            proc.WaitForExit();
        }
        catch { }
        return names;
    }
}
