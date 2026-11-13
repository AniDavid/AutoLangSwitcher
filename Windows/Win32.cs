using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// Win32 declarations and the small helpers built directly on them.
static class Win32
{
    public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);
    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    const uint GA_ROOT = 2;
    const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hWinEventHook);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint idThread);
    [DllImport("user32.dll")] static extern int GetKeyboardLayoutList(int nBuff, [Out] IntPtr[] lpList);
    [DllImport("user32.dll", SetLastError = true)] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO info);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int maxCount);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr hProcess, uint flags, StringBuilder exeName, ref uint size);

    // The top-level window that owns hwnd (or hwnd itself).
    public static IntPtr RootOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        IntPtr root = GetAncestor(hwnd, GA_ROOT);
        return root != IntPtr.Zero ? root : hwnd;
    }

    public static uint ProcessIdOf(IntPtr hwnd)
    {
        uint pid;
        GetWindowThreadProcessId(hwnd, out pid);
        return pid;
    }

    public static string ClassNameOf(IntPtr hwnd)
    {
        StringBuilder sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static IntPtr LayoutOf(IntPtr hwnd)
    {
        uint pid;
        uint tid = GetWindowThreadProcessId(hwnd, out pid);
        return tid == 0 ? IntPtr.Zero : GetKeyboardLayout(tid);
    }

    public static bool IsInstalled(IntPtr hkl)
    {
        int n = GetKeyboardLayoutList(0, null);
        if (n <= 0) return false;
        IntPtr[] list = new IntPtr[n];
        GetKeyboardLayoutList(n, list);
        return Array.IndexOf(list, hkl) >= 0;
    }

    // Asks the window to switch layout. Sends the request to the top-level window (the standard
    // target) and, if different, also to the control that has keyboard focus.
    // Returns false (with the Win32 error) if the top-level window refused the message.
    public static bool RequestLayout(IntPtr hwnd, IntPtr hkl, out int error)
    {
        bool ok = PostMessage(hwnd, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, hkl);
        error = ok ? 0 : Marshal.GetLastWin32Error();
        uint pid;
        uint tid = GetWindowThreadProcessId(hwnd, out pid);
        GUITHREADINFO gi = new GUITHREADINFO();
        gi.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
        if (GetGUIThreadInfo(tid, ref gi) && gi.hwndFocus != IntPtr.Zero && gi.hwndFocus != hwnd)
            PostMessage(gi.hwndFocus, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, hkl);
        return ok;
    }

    // Lower-case exe file name of the app that owns the window, or null.
    public static string ResolveExe(IntPtr hwnd)
    {
        uint pid = ProcessIdOf(hwnd);
        string exe = ExeName(pid);

        // Store apps (Calculator, Mail, ...) all live inside ApplicationFrameHost.exe;
        // look at the child window to find the real app.
        if (exe != null && exe.Equals("applicationframehost.exe", StringComparison.OrdinalIgnoreCase))
        {
            uint realPid = 0;
            EnumChildWindows(hwnd, delegate(IntPtr child, IntPtr l)
            {
                uint p = ProcessIdOf(child);
                if (p != 0 && p != pid) { realPid = p; return false; }
                return true;
            }, IntPtr.Zero);
            string real = ExeName(realPid);
            if (real != null) exe = real;
        }
        return exe;
    }

    static string ExeName(uint pid)
    {
        if (pid == 0) return null;
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            StringBuilder sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            if (!QueryFullProcessImageName(h, 0, sb, ref size)) return null;
            return Path.GetFileName(sb.ToString()).ToLowerInvariant();
        }
        finally { CloseHandle(h); }
    }

    public static string LayoutName(IntPtr hkl)
    {
        int lang = (int)(hkl.ToInt64() & 0xFFFF);
        try { return CultureInfo.GetCultureInfo(lang).DisplayName; }
        catch { return "0x" + lang.ToString("X4"); }
    }
}
