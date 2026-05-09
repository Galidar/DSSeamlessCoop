/*
 * Headless replica of MainForm.PerformLaunch from the original C# Loader.
 *
 * Steps performed (in order, abort on failure):
 *   1. Resolve the connection hostname for the server (LAN vs WAN vs loopback)
 *   2. Hash the game .exe to look up the matching DarkSoulsLoadConfig
 *   3. Write steam_appid.txt next to the .exe so Steam SDK initialises
 *   4. CreateProcess the game (no suspended state — the C# Loader doesn't either)
 *   5. If the build config wants the injector, allocate memory in the child,
 *      write the path to Injector.dll, and CreateRemoteThread into LoadLibraryW
 *   6. Otherwise, encrypt the server info block and WriteProcessMemory it to
 *      the patch address (with retries — Steam stub unpacks asynchronously)
 *
 * No WinForms / MessageBox; all errors come back as a [LaunchResult] string.
 */

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

using Bonfire.Service.Modules;
using Loader; // re-using the Loader-namespace utility classes (linked via csproj)

namespace Bonfire.Service.Game;

public sealed record LaunchRequest(
    string ExePath,
    string ServerId,
    string ServerName,
    string Hostname,
    string PrivateHostname,
    int Port,
    string PublicKey,
    string GameType,
    bool EnableSeparateSaves,
    string Ds2OverhaulPath,
    bool EnableDs3Seamless,
    string Ds3SeamlessPath);

public sealed record LaunchResult(bool Ok, string Message, int? Pid);

public static class GameLauncher
{
    private sealed record Ds3SeamlessLaunchPlan(
        string DllPath,
        string? LauncherPath);

    public static LaunchResult Launch(LaunchRequest req,
        string machinePublicIp, string machinePrivateIp,
        string injectorDllPath)
    {
        // ── 1. Sanity ──────────────────────────────────────────────
        if (string.IsNullOrEmpty(req.PublicKey))
            return new(false, "Server's public key is missing.", null);
        if (!File.Exists(req.ExePath))
            return new(false, $"Game executable not found: {req.ExePath}", null);

        // ── 2. Resolve which hostname to actually connect to ──────
        var connectionHostname = ResolveConnectIp(req, machinePublicIp, machinePrivateIp);

        // ── 3. Look up the build config for this exact .exe hash ──
        if (!BuildConfig.ExeLoadConfiguration.TryGetValue(
                ExeUtils.GetExeSimpleHash(req.ExePath), out var loadCfg))
        {
            return new(false,
                "This game executable isn't a recognised version. " +
                "BuildConfig has no patching offsets for it.", null);
        }

        // ── 4. Steam app ID file (next to the .exe) ───────────────
        var exeDir = Path.GetDirectoryName(req.ExePath)!;
        try
        {
            File.WriteAllText(Path.Combine(exeDir, "steam_appid.txt"),
                loadCfg.SteamAppId.ToString());
        }
        catch (Exception ex)
        {
            return new(false, $"Could not write steam_appid.txt: {ex.Message}", null);
        }

        var ds3SeamlessPlan = PrepareDs3Seamless(req, out var ds3PrepareErr);
        if (!string.IsNullOrEmpty(ds3PrepareErr))
            return new(false, ds3PrepareErr, null);

        var launchExe = ds3SeamlessPlan?.LauncherPath ?? req.ExePath;
        var launchedViaDs3SeamlessLauncher =
            ds3SeamlessPlan?.LauncherPath is not null;

        // ── 5. CreateProcess ───────────────────────────────────────
        //
        // Plain ZERO_FLAG, exactly like the original Loader's PerformLaunch
        // (MainForm.cs:649). Earlier we tried CREATE_BREAKAWAY_FROM_JOB on
        // the theory that BonfireService's job-object membership was
        // breaking Steam IPC, but that flag actually severs the game from
        // the parent process tree in a way Steam's overlay/auth machinery
        // doesn't like (manifests as: login handshake passes, auth socket
        // opens then immediately drops without sending RequestHandshake).
        // Match Loader exactly.
        var startup = new STARTUPINFO();
        var pi = new PROCESS_INFORMATION();
        var launchStartedAt = DateTime.Now;
        var ok = WinAPI.CreateProcess(
            null!, $"\"{launchExe}\"",
            IntPtr.Zero, IntPtr.Zero, false,
            ProcessCreationFlags.ZERO_FLAG,
            IntPtr.Zero, exeDir,
            ref startup, out pi);
        if (!ok)
            return new(false,
                $"CreateProcess failed (GetLastError={Marshal.GetLastWin32Error()}).", null);

        var targetPi = pi;
        if (launchedViaDs3SeamlessLauncher)
        {
            if (!WaitForGameProcess(req.ExePath, launchStartedAt, out targetPi, out var waitErr))
                return new(false, waitErr, (int)pi.dwProcessId);
        }

        return Inject(
            targetPi,
            loadCfg,
            injectorDllPath,
            req,
            connectionHostname,
            ds3SeamlessPlan,
            ds3SeamlessAlreadyLoaded: launchedViaDs3SeamlessLauncher);
    }

    private static LaunchResult Inject(PROCESS_INFORMATION pi,
        DarkSoulsLoadConfig loadCfg, string injectorDllPath,
        LaunchRequest req, string connectionHostname,
        Ds3SeamlessLaunchPlan? ds3SeamlessPlan,
        bool ds3SeamlessAlreadyLoaded)
    {

        try
        {
            // ── 6. Inject DLL or patch memory ────────────────────
            if (loadCfg.UseInjector)
            {
                if (!InjectDll(pi, injectorDllPath, req, connectionHostname,
                        out var injectErr))
                {
                    return new(false, injectErr, (int)pi.dwProcessId);
                }
            }
            else
            {
                if (!PatchMemory(pi, loadCfg, connectionHostname, req.PublicKey,
                        out var patchErr))
                {
                    return new(false, patchErr, (int)pi.dwProcessId);
                }
            }

            if (ds3SeamlessPlan is not null && !ds3SeamlessAlreadyLoaded)
            {
                if (!InjectDs3Seamless(pi, ds3SeamlessPlan, out var ds3SeamlessErr))
                {
                    return new(false, ds3SeamlessErr, (int)pi.dwProcessId);
                }
            }

            return new(true, "Launched.", (int)pi.dwProcessId);
        }
        catch (Exception ex)
        {
            return new(false, $"Exception during patch/inject: {ex.Message}",
                (int)pi.dwProcessId);
        }
    }

    /// <summary>
    /// Same heuristic as the original Loader: if the server's WAN IP matches
    /// our WAN IP, we're behind the same NAT; prefer LAN or loopback.
    /// </summary>
    private static string ResolveConnectIp(
        LaunchRequest req, string machinePublicIp, string machinePrivateIp)
    {
        var hostnameIp = NetUtils.HostnameToIPv4(req.Hostname);
        var privateIp = NetUtils.HostnameToIPv4(req.PrivateHostname);

        if (!string.IsNullOrEmpty(hostnameIp) &&
            hostnameIp == machinePublicIp)
        {
            // Behind same NAT.
            if (!string.IsNullOrEmpty(privateIp) &&
                privateIp == machinePrivateIp)
            {
                return "127.0.0.1";
            }
            return string.IsNullOrEmpty(req.PrivateHostname)
                ? req.Hostname
                : req.PrivateHostname;
        }
        return req.Hostname;
    }

    private static bool InjectDll(
        PROCESS_INFORMATION pi, string injectorPath, LaunchRequest req,
        string connectionHostname, out string error)
    {
        error = "";
        if (!File.Exists(injectorPath))
        {
            error = $"Injector.dll not found at {injectorPath}";
            return false;
        }

        // Write the injector config file the DLL will read on attach.
        var configPath = Path.Combine(Path.GetDirectoryName(injectorPath)!, "Injector.config");
        var ds2ModEngine = Ds2ModEngineSettings.Resolve(
            req.ExePath, injectorPath, req.GameType, req.Ds2OverhaulPath);
        var ds2LightingEngineActive = IsDs2LightingEngineInstalled(req);
        var injectCfg = new InjectionConfig
        {
            ServerName = req.ServerName,
            ServerPublicKey = req.PublicKey,
            ServerHostname = connectionHostname,
            ServerPort = req.Port,
            ServerGameType = req.GameType,
            EnableSeperateSaveFiles =
                !Ds3SeamlessOwnsSaveHook(req) &&
                (req.EnableSeparateSaves || ds2ModEngine.UseAlternateSaveFile),
            EnableModFileOverrides = ds2ModEngine.EnableModFileOverrides,
            ModOverrideDirectory = ds2ModEngine.ModOverrideDirectory,
            CacheModFilePaths = ds2ModEngine.CacheModFilePaths,
            SaveFileExtension = ds2ModEngine.UseAlternateSaveFile ? ".sl3" : ".ds3os",
            EnableDs2ShadowResolutionPatches =
                ds2ModEngine.EnableShadowResolutionPatches && !ds2LightingEngineActive,
            Ds2DirectionalShadowResolution = ds2ModEngine.DirectionalShadowResolution,
            Ds2DynamicAtlasShadowResolution = ds2ModEngine.DynamicAtlasShadowResolution,
            Ds2DynamicPointShadowResolution = ds2ModEngine.DynamicPointShadowResolution,
            Ds2DynamicSpotShadowResolution = ds2ModEngine.DynamicSpotShadowResolution,
        };
        File.WriteAllText(configPath, injectCfg.ToJson());

        return LoadLibraryIntoProcess(pi, injectorPath, "Injector.dll", out error);
    }

    private static bool IsDs2LightingEngineInstalled(LaunchRequest req)
    {
        if (!string.Equals(req.GameType, "DarkSouls2", StringComparison.OrdinalIgnoreCase))
            return false;

        var gameDir = Path.GetDirectoryName(req.ExePath);
        if (string.IsNullOrEmpty(gameDir))
            return false;

        if (!File.Exists(Path.Combine(gameDir, "dxgi.dll")))
            return false;

        return
            Directory.Exists(Path.Combine(gameDir, "ds2le_atmosphere_presets")) ||
            Directory.Exists(Path.Combine(gameDir, "shader", "addon_shaders")) ||
            Directory.Exists(Path.Combine(gameDir, "shader", "addon_config")) ||
            File.Exists(Path.Combine(gameDir, "DS2LE.log"));
    }

    private static Ds3SeamlessLaunchPlan? PrepareDs3Seamless(
        LaunchRequest req, out string error)
    {
        error = "";
        if (!ShouldAttemptDs3Seamless(req))
            return null;

        if (!Ds3SeamlessPayloadResolver.TryPrepareForGame(
                req.Ds3SeamlessPath,
                req.ExePath,
                BuildDs3SeamlessPassword(req),
                out var dllPath,
                out var launcherPath,
                out var prepareErr))
        {
            if (string.IsNullOrWhiteSpace(req.Ds3SeamlessPath) &&
                prepareErr.Contains("not bundled", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            error = prepareErr;
            return null;
        }

        return new Ds3SeamlessLaunchPlan(dllPath, launcherPath);
    }

    private static bool InjectDs3Seamless(
        PROCESS_INFORMATION pi, Ds3SeamlessLaunchPlan plan, out string error)
    {
        return LoadLibraryIntoProcess(pi, plan.DllPath, "DS3 Seamless runtime", out error);
    }

    private static bool WaitForGameProcess(
        string gameExePath,
        DateTime launchStartedAt,
        out PROCESS_INFORMATION pi,
        out string error)
    {
        pi = new PROCESS_INFORMATION();
        error = "";

        var exeName = Path.GetFileNameWithoutExtension(gameExePath);
        var expectedPath = Path.GetFullPath(gameExePath);
        var minStartTime = launchStartedAt.AddSeconds(-5);

        for (var attempt = 0; attempt < 80; attempt++)
        {
            foreach (var process in Process.GetProcessesByName(exeName))
            {
                try
                {
                    if (process.StartTime < minStartTime)
                        continue;

                    var processPath = process.MainModule?.FileName;
                    if (string.IsNullOrEmpty(processPath) ||
                        !SamePath(processPath, expectedPath))
                    {
                        continue;
                    }

                    var handle = WinAPI.OpenProcess(
                        ProcessAccessFlags.All, false, process.Id);
                    if (handle == IntPtr.Zero)
                        continue;

                    pi = new PROCESS_INFORMATION
                    {
                        hProcess = handle,
                        dwProcessId = (uint)process.Id,
                    };
                    return true;
                }
                catch
                {
                    // The process can exit or deny module reads while the
                    // launcher is still starting the game. Keep polling.
                }
            }

            Thread.Sleep(250);
        }

        error = "DS3 Seamless launcher started, but DarkSoulsIII.exe did not appear.";
        return false;
    }

    private static bool LoadLibraryIntoProcess(
        PROCESS_INFORMATION pi, string dllPath, string label, out string error)
    {
        error = "";
        if (!File.Exists(dllPath))
        {
            error = $"{label} not found at {dllPath}";
            return false;
        }

        // Resolve the LoadLibraryW address we'll invoke on the remote thread.
        var kernel32 = WinAPI.GetModuleHandle("kernel32.dll");
        if (kernel32 == IntPtr.Zero)
        {
            error = $"GetModuleHandle(kernel32.dll) failed " +
                    $"(GetLastError={Marshal.GetLastWin32Error()})";
            return false;
        }
        var loadLibrary = WinAPI.GetProcAddress(kernel32, "LoadLibraryW");
        if (loadLibrary == IntPtr.Zero)
        {
            error = $"GetProcAddress(LoadLibraryW) failed " +
                    $"(GetLastError={Marshal.GetLastWin32Error()})";
            return false;
        }

        // Allocate space for the path string in the target process. Steam
        // stub unpacks the executable asynchronously, so VirtualAllocEx may
        // fail for the first few hundred ms — retry up to ~16s.
        var pathBytes = Encoding.Unicode.GetBytes(dllPath + "\0");
        var pathAddr = IntPtr.Zero;
        for (var i = 0; i < 32 && pathAddr == IntPtr.Zero; i++)
        {
            pathAddr = WinAPI.VirtualAllocEx(
                pi.hProcess, IntPtr.Zero, (uint)pathBytes.Length,
                (uint)(AllocationType.Reserve | AllocationType.Commit),
                (uint)MemoryProtection.ReadWrite);
            if (pathAddr == IntPtr.Zero) Thread.Sleep(500);
        }
        if (pathAddr == IntPtr.Zero)
        {
            error = $"{label}: VirtualAllocEx failed " +
                    $"(GetLastError={Marshal.GetLastWin32Error()})";
            return false;
        }

        if (!WinAPI.WriteProcessMemory(pi.hProcess, pathAddr,
                pathBytes, (uint)pathBytes.Length, out var written) ||
            written != pathBytes.Length)
        {
            error = $"{label}: WriteProcessMemory failed " +
                    $"(GetLastError={Marshal.GetLastWin32Error()})";
            return false;
        }

        var thread = WinAPI.CreateRemoteThread(
            pi.hProcess, IntPtr.Zero, 0, loadLibrary, pathAddr, 0, IntPtr.Zero);
        if (thread == IntPtr.Zero)
        {
            error = $"{label}: CreateRemoteThread failed " +
                    $"(GetLastError={Marshal.GetLastWin32Error()})";
            return false;
        }
        return true;
    }

    private static bool ShouldAttemptDs3Seamless(LaunchRequest req) =>
        req.EnableDs3Seamless &&
        string.Equals(req.GameType, "DarkSouls3", StringComparison.OrdinalIgnoreCase);

    private static bool Ds3SeamlessOwnsSaveHook(LaunchRequest req) =>
        ShouldAttemptDs3Seamless(req) &&
        Ds3SeamlessPayloadResolver.Resolve(req.Ds3SeamlessPath, req.ExePath) is not null;

    private static string BuildDs3SeamlessPassword(LaunchRequest req)
    {
        var seed = !string.IsNullOrWhiteSpace(req.ServerId)
            ? req.ServerId
            : $"{req.ServerName}|{req.Hostname}|{req.Port}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return "bf-" + Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool PatchMemory(
        PROCESS_INFORMATION pi, DarkSoulsLoadConfig loadCfg,
        string connectionHostname, string publicKey, out string error)
    {
        error = "";
        var dataBlock = PatchingUtils.MakeEncryptedServerInfo(
            connectionHostname, publicKey, loadCfg.Key);
        if (dataBlock is null)
        {
            error = "Failed to encode server info patch (hostname or key too long).";
            return false;
        }

        for (var i = 0; i < 32; i++)
        {
            var baseAddr = WinAPI.GetProcessModuleBaseAddress(pi.hProcess);
            var patchAddr = (IntPtr)loadCfg.ServerInfoAddress;
            if (loadCfg.UsesASLR)
                patchAddr = (IntPtr)((ulong)baseAddr + (ulong)patchAddr);

            if (WinAPI.WriteProcessMemory(pi.hProcess, patchAddr,
                    dataBlock, (uint)dataBlock.Length, out var written) &&
                written == dataBlock.Length)
            {
                return true;
            }
            Thread.Sleep(500);
        }
        error = "Failed to write server info to game memory after 32 retries.";
        return false;
    }
}
