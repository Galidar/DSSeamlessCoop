/*
 * Captures Win32 OutputDebugStringA messages via the DBWIN_BUFFER shared
 * memory + DBWIN_BUFFER_READY/DBWIN_DATA_READY event pair (the same
 * mechanism DebugView uses). Server.exe calls OutputDebugStringA on every
 * Log/Warning/Error line, so listening on this gives us its full log
 * stream even with the console redirected — no ConPTY needed.
 *
 * The buffer is system-global and one-reader-only: if another tool (e.g.
 * Visual Studio debugger, DebugView) is already listening, our event
 * waits will fail and we silently log the start banner only. That's fine
 * for our purposes (best-effort diagnostic).
 */

using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace Bonfire.Service.Modules;

internal static class DebugListener
{
    private const string MapName = "DBWIN_BUFFER";
    private const string ReadyEventName = "DBWIN_BUFFER_READY";
    private const string DataReadyEventName = "DBWIN_DATA_READY";
    private const int MapSize = 4096;

    private static CancellationTokenSource? _cts;
    private static Thread? _thread;
    private static StreamWriter? _writer;
    private static int _filterPid;

    public static void Start(int filterPid, string logPath)
    {
        Stop();
        _filterPid = filterPid;
        try
        {
            _writer = new StreamWriter(logPath, append: false) { AutoFlush = true };
            _writer.WriteLine($"=== Server.exe PID={filterPid} started at {DateTime.Now:HH:mm:ss.fff} ===");
        }
        catch { _writer = null; }

        _cts = new CancellationTokenSource();
        _thread = new Thread(() => Run(_cts.Token))
        {
            IsBackground = true,
            Name = "DebugListener"
        };
        _thread.Start();
    }

    public static void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        try { _thread?.Join(500); } catch { }
        _thread = null;
        try
        {
            _writer?.WriteLine($"=== Listener stopped at {DateTime.Now:HH:mm:ss.fff} ===");
            _writer?.Dispose();
        }
        catch { }
        _writer = null;
    }

    private static void Run(CancellationToken ct)
    {
        // ACL that allows Everyone (Server.exe runs in our admin token, but
        // OutputDebugStringA needs the events accessible regardless of
        // process integrity level — copy DebugView's behaviour).
        var sd = new SECURITY_DESCRIPTOR();
        if (!InitializeSecurityDescriptor(ref sd, 1) ||
            !SetSecurityDescriptorDacl(ref sd, true, IntPtr.Zero, false))
        {
            return;
        }
        var sa = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            bInheritHandle = false
        };
        IntPtr sdPtr = Marshal.AllocHGlobal(Marshal.SizeOf(sd));
        Marshal.StructureToPtr(sd, sdPtr, false);
        sa.lpSecurityDescriptor = sdPtr;

        IntPtr ready = IntPtr.Zero, dataReady = IntPtr.Zero;
        MemoryMappedFile? mmf = null;
        try
        {
            ready = CreateEventW(ref sa, false, false, ReadyEventName);
            dataReady = CreateEventW(ref sa, false, false, DataReadyEventName);
            if (ready == IntPtr.Zero || dataReady == IntPtr.Zero) return;

            try
            {
                mmf = MemoryMappedFile.CreateOrOpen(MapName, MapSize, MemoryMappedFileAccess.ReadWrite);
            }
            catch { return; }

            using var view = mmf.CreateViewAccessor(0, MapSize, MemoryMappedFileAccess.Read);

            // Tell the OS we're ready to receive.
            SetEvent(ready);

            while (!ct.IsCancellationRequested)
            {
                uint waitRes = WaitForSingleObject(dataReady, 250);
                if (waitRes != 0) // 0 = WAIT_OBJECT_0 (signalled)
                {
                    continue; // timeout — just loop
                }

                // Buffer layout: [4 bytes PID][message text up to 4092 bytes, null-terminated ANSI]
                int pid = view.ReadInt32(0);
                var bytes = new byte[MapSize - 4];
                view.ReadArray(4, bytes, 0, bytes.Length);
                int len = Array.IndexOf<byte>(bytes, 0);
                if (len < 0) len = bytes.Length;
                var msg = Encoding.Default.GetString(bytes, 0, len).TrimEnd('\r', '\n');

                if (pid == _filterPid && _writer is not null && msg.Length > 0)
                {
                    try { _writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}"); } catch { }
                }

                // Signal we're ready for the next message.
                SetEvent(ready);
            }
        }
        catch { /* swallow */ }
        finally
        {
            if (ready != IntPtr.Zero) CloseHandle(ready);
            if (dataReady != IntPtr.Zero) CloseHandle(dataReady);
            mmf?.Dispose();
            Marshal.FreeHGlobal(sdPtr);
        }
    }

    // ---------- Win32 P/Invoke ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_DESCRIPTOR
    {
        public byte revision;
        public byte size;
        public ushort control;
        public IntPtr owner;
        public IntPtr group;
        public IntPtr sacl;
        public IntPtr dacl;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool InitializeSecurityDescriptor(ref SECURITY_DESCRIPTOR sd, uint revision);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetSecurityDescriptorDacl(ref SECURITY_DESCRIPTOR sd,
        bool bDaclPresent, IntPtr pDacl, bool bDaclDefaulted);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEventW(ref SECURITY_ATTRIBUTES sa,
        bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint ms);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
