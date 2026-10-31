using System.Runtime.InteropServices;

namespace WinKeyGuard;

// Passive foreground-change listener. Maps the focused window's PID to an exe name via a process
// snapshot, so it never calls OpenProcess on the focused application (e.g. the game).
// Must be started on the UI thread (the WinForms message loop delivers the callbacks).
sealed class FocusMonitor : IDisposable
{
    public event Action<string?>? ForegroundExeChanged;

    Native.WinEventProc? _proc;   // field keeps the delegate alive while the hook is installed
    IntPtr _hook;

    public void Start()
    {
        _proc = OnEvent;
        _hook = Native.SetWinEventHook(
            Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _proc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);

        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException("SetWinEventHook failed.");
    }

    public string? CurrentExe() => ExeForWindow(Native.GetForegroundWindow());

    void OnEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero) return;
        var exe = ExeForWindow(hwnd);
        if (exe != null) ForegroundExeChanged?.Invoke(exe);
    }

    static string? ExeForWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        return Native.ExeNameFromPid(pid);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }

    static class Native
    {
        public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        const uint TH32CS_SNAPPROCESS = 0x00000002;

        public delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public UIntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("user32.dll")]
        public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32 entry);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        public static string? ExeNameFromPid(uint pid)
        {
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return null;

            try
            {
                var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
                if (!Process32FirstW(snap, ref entry)) return null;

                do
                {
                    if (entry.th32ProcessID == pid) return entry.szExeFile;
                }
                while (Process32NextW(snap, ref entry));
            }
            finally
            {
                CloseHandle(snap);
            }

            return null;
        }
    }
}
