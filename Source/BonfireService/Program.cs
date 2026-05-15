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
        try
        {
            await server.RunAsync();
            return 0;
        }
        finally
        {
            Ds2NativeSessionCoordinator.Stop();
        }
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
