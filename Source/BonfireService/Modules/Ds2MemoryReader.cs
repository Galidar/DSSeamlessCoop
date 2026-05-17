/*
 * Ds2MemoryReader — direct ReadProcessMemory pose source for the
 * pose bridge.
 *
 * The runtime worker thread inside the Injector hangs on a chain walk
 * exception during DS2's pre-world loading phase (see v2.8.3 test
 * notes), which freezes events.jsonl forever in that DS2 session.
 * The render hook keeps working because it runs in a different
 * thread, so the magenta host cube still renders — but the pose
 * bridge that depends on events.jsonl for a broadcast source goes
 * silent and no peer packets cross the wire.
 *
 * This reader bypasses the worker entirely: it opens DS2's process,
 * AOB-scans for the gm_imp_global anchor (same pattern the Injector
 * uses) once per process boot, caches the resolved address, and on
 * every call walks the chain to chr+0x90 to extract the live player
 * world position. The bridge's WatcherLoop uses this as its source
 * instead of tailing events.jsonl.
 *
 * Chain (verified via Cheat Engine on 2026-05-16, identical to the
 * Injector's TryReadHostPosition):
 *
 *   AOB pattern   48 8B 05 ?? ?? ?? ?? 48 8B 58 38 48 85 DB 74 ?? F6
 *   site + 7 + sign-extend(disp32) = gm_imp_global
 *   gm     = *gm_imp_global
 *   inter  = *(gm + 0x18)
 *   chr    = *(inter + 0x50)
 *   px,py,pz = floats at chr+0x90, +0x94, +0x98
 */

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Bonfire.Service.Modules;

public static class Ds2MemoryReader
{
    // ── P/Invoke ─────────────────────────────────────────────────────
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        IntPtr handle, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr h);

    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;

    // AOB pattern bytes + wildcard mask. Same one the Injector uses.
    private static readonly byte[] Pattern = new byte[] {
        0x48, 0x8B, 0x05, 0x00, 0x00, 0x00, 0x00,
        0x48, 0x8B, 0x58, 0x38, 0x48, 0x85, 0xDB, 0x74, 0x00, 0xF6
    };
    private static readonly bool[] Wildcard = new bool[] {
        false, false, false, true, true, true, true,
        false, false, false, false, false, false, false, false, true, false
    };

    // ── State (cached per DS2 process) ───────────────────────────────
    private static readonly object Lock = new();
    private static int _ds2Pid;
    private static IntPtr _processHandle = IntPtr.Zero;
    private static IntPtr _gmImpGlobalAddr = IntPtr.Zero;

    // ── Public API ───────────────────────────────────────────────────

    /// <summary>
    /// Try to read the host player's world position from DS2 memory.
    /// Returns true on success; false if DS2 isn't running, the AOB
    /// hasn't been resolved yet, or any chain pointer is null (which
    /// is normal during loading screens / area transitions).
    /// </summary>
    public static bool TryReadHostPose(out float px, out float py, out float pz)
    {
        px = py = pz = 0;

        var ds2 = Process.GetProcessesByName("DarkSoulsII").FirstOrDefault();
        if (ds2 == null) return false;

        lock (Lock)
        {
            // Re-attach if the DS2 process changed under us (e.g. user
            // killed + restarted DS2 without restarting Bonfire).
            if (_ds2Pid != ds2.Id)
            {
                ReleaseLocked();
                if (!AttachLocked(ds2)) return false;
            }

            if (_gmImpGlobalAddr == IntPtr.Zero)
            {
                // First call for this DS2 process — scan the main
                // module for the AOB pattern. ~28 MB scan, runs once.
                if (!ResolveGmImpGlobalLocked(ds2)) return false;
            }

            // Walk the chain.
            if (!TryReadUInt64(_gmImpGlobalAddr, out var gm) || gm == 0) return false;
            if (!TryReadUInt64((IntPtr)((long)gm + 0x18), out var inter) || inter == 0) return false;
            if (!TryReadUInt64((IntPtr)((long)inter + 0x50), out var chr) || chr == 0) return false;
            if (!TryReadFloat3((IntPtr)((long)chr + 0x90), out px, out py, out pz)) return false;

            // Loose sanity check — DS2 worlds typically fit in
            // |coord| < 10_000. Garbage reads (NaN, ±∞, or huge
            // magnitudes) get rejected so we don't broadcast a bogus
            // peer position.
            if (!IsSane(px) || !IsSane(py) || !IsSane(pz)) return false;
            return true;
        }
    }

    /// <summary>True if a DS2 process is currently attached + scanned.</summary>
    public static bool IsAttached
    {
        get { lock (Lock) { return _processHandle != IntPtr.Zero && _gmImpGlobalAddr != IntPtr.Zero; } }
    }

    /// <summary>Drop the cached handle/scan result (e.g. on shutdown).</summary>
    public static void Release()
    {
        lock (Lock) { ReleaseLocked(); }
    }

    // ── Internals ────────────────────────────────────────────────────

    private static void ReleaseLocked()
    {
        if (_processHandle != IntPtr.Zero)
        {
            try { CloseHandle(_processHandle); } catch { }
        }
        _processHandle = IntPtr.Zero;
        _ds2Pid = 0;
        _gmImpGlobalAddr = IntPtr.Zero;
    }

    private static bool AttachLocked(Process ds2)
    {
        try
        {
            _processHandle = OpenProcess(
                PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, ds2.Id);
            if (_processHandle == IntPtr.Zero) return false;
            _ds2Pid = ds2.Id;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool ResolveGmImpGlobalLocked(Process ds2)
    {
        try
        {
            // ProcessMainModule path is the DarkSoulsII.exe mapping.
            // BaseAddress + size delimit the .text/.rdata/.data region
            // we scan.
            var mm = ds2.MainModule;
            if (mm == null) return false;
            var baseAddr = mm.BaseAddress;
            var size = mm.ModuleMemorySize;
            if (baseAddr == IntPtr.Zero || size <= 0) return false;

            // Read the whole module into a managed byte[]. ~28 MB on
            // DS2 — single allocation, then linear scan.
            var buf = new byte[size];
            if (!ReadProcessMemory(_processHandle, baseAddr, buf,
                    (IntPtr)size, out _))
            {
                return false;
            }

            // AOB scan. Pattern is 17 bytes with two wildcards. A
            // naive linear scan is plenty fast (a few hundred ms).
            int hit = FindPattern(buf, Pattern, Wildcard);
            if (hit < 0) return false;

            // rip-relative 32-bit displacement starts at byte 3 of the
            // pattern. site = base + hit; gm_imp_global = site + 7 + disp.
            int disp =
                buf[hit + 3] |
                (buf[hit + 4] << 8) |
                (buf[hit + 5] << 16) |
                (buf[hit + 6] << 24);
            long resolved = (long)baseAddr + hit + 7L + disp;
            _gmImpGlobalAddr = (IntPtr)resolved;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int FindPattern(byte[] hay, byte[] needle, bool[] wild)
    {
        int n = hay.Length;
        int m = needle.Length;
        int last = n - m;
        for (int i = 0; i <= last; ++i)
        {
            bool ok = true;
            for (int j = 0; j < m; ++j)
            {
                if (!wild[j] && hay[i + j] != needle[j]) { ok = false; break; }
            }
            if (ok) return i;
        }
        return -1;
    }

    private static bool TryReadUInt64(IntPtr addr, out ulong value)
    {
        value = 0;
        var buf = new byte[8];
        if (!ReadProcessMemory(_processHandle, addr, buf, (IntPtr)8, out var read) ||
            (int)read != 8)
        {
            return false;
        }
        value = BitConverter.ToUInt64(buf, 0);
        return true;
    }

    private static bool TryReadFloat3(IntPtr addr, out float a, out float b, out float c)
    {
        a = b = c = 0;
        var buf = new byte[12];
        if (!ReadProcessMemory(_processHandle, addr, buf, (IntPtr)12, out var read) ||
            (int)read != 12)
        {
            return false;
        }
        a = BitConverter.ToSingle(buf, 0);
        b = BitConverter.ToSingle(buf, 4);
        c = BitConverter.ToSingle(buf, 8);
        return true;
    }

    private static bool IsSane(float v)
    {
        if (float.IsNaN(v) || float.IsInfinity(v)) return false;
        return Math.Abs(v) < 10000f;
    }
}
