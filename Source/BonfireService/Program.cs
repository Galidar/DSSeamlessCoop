/*
 * Bonfire — Dark Souls Open Server companion (Galidar fork)
 *
 * Headless C# service. Speaks JSON-RPC 2.0 over stdio so the Flutter UI
 * can drive every operation (network detection, firewall, server lifecycle,
 * game patching/injection, master server queries) without owning any
 * Win32-specific code itself.
 *
 * One request per stdin line. One response per stdout line. Notifications
 * (download progress, server logs) are pushed as their own line whenever
 * the server has something to say.
 */

using System;
using System.Reflection;
using System.Threading.Tasks;
using Bonfire.Service.Modules;
using Bonfire.Service.Rpc;

namespace Bonfire.Service;

public static class Program
{
    public static string ServiceVersion { get; } = ResolveServiceVersion();

    public static async Task<int> Main(string[] args)
    {
        // --version is a CLI affordance the Flutter app uses to verify it
        // spawned a compatible service binary before opening the JSON-RPC
        // session.
        if (args.Length >= 1 && args[0] == "--version")
        {
            Console.WriteLine($"Bonfire.Service {ServiceVersion}");
            return 0;
        }

        var server = new RpcServer();
        Methods.Register(server);
        Ds2NativeSessionCoordinator.Start(server);

        // Plan v3 Track A: bring the LAN-discovery listener up at
        // boot so a guest's Bonfire already has a cache of host
        // beacons by the time the Crystal Eye Orb fires in-game.
        // No-op on networks where multicast is filtered — the orb
        // path still falls back to the master-list/UI flow.
        try { Ds2LanBeacon.EnsureListenerRunning(); } catch { }

        // Phase 4c bootstrap: if the env vars are set we auto-start
        // the pose bridge without waiting for the Flutter UI to RPC
        // it. Useful for the single-PC loopback test (see
        // HKMP_OVERLAY_RESEARCH.md §14 Track B) and for LAN
        // deployments where the user wants the bridge live from
        // service startup. Format:
        //
        //   BONFIRE_POSE_BRIDGE_PORT=50031
        //   BONFIRE_POSE_BRIDGE_PEERS=127.0.0.1:50031        (loopback)
        //   BONFIRE_POSE_BRIDGE_PEERS=192.168.1.5:50031,...  (LAN)
        TryAutoStartPoseBridge();

        try
        {
            await server.RunAsync();
            return 0;
        }
        finally
        {
            Ds2NativeSessionCoordinator.Stop();
            try { Ds2NativePoseBridge.Stop(); } catch { }
            try { Ds2LanBeacon.ShutdownAll(); } catch { }
        }
    }

    private static void TryAutoStartPoseBridge()
    {
        var peersEnv = Environment.GetEnvironmentVariable("BONFIRE_POSE_BRIDGE_PEERS");
        if (string.IsNullOrWhiteSpace(peersEnv)) return;

        var portEnv = Environment.GetEnvironmentVariable("BONFIRE_POSE_BRIDGE_PORT");
        if (!int.TryParse(portEnv, out var port) || port <= 0 || port > 65535)
        {
            port = 50031;
        }

        var peers = peersEnv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        try { Ds2NativePoseBridge.Start(port, peers); }
        catch { /* env-var-driven startup is best-effort */ }
    }

    private static string ResolveServiceVersion()
    {
        var assembly = typeof(Program).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            // The SDK appends the git SHA as SemVer build metadata in local builds.
            return informationalVersion.Split('+', 2)[0];
        }

        return assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
