using System;
using System.Collections.Generic;
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
        return Array.IndexOf(Layouts(), hkl) >= 0;
    }

    public static IntPtr[] Layouts()
    {
        int n = GetKeyboardLayoutList(0, null);
        IntPtr[] list = new IntPtr[Math.Max(n, 0)];
        if (n > 0) GetKeyboardLayoutList(n, list);
        return list;
    }

    // The control that has keyboard focus in hwnd's thread, or IntPtr.Zero.
    static IntPtr FocusOf(IntPtr hwnd)
    {
        uint pid;
        uint tid = GetWindowThreadProcessId(hwnd, out pid);
        GUITHREADINFO gi = new GUITHREADINFO();
        gi.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
        return GetGUIThreadInfo(tid, ref gi) ? gi.hwndFocus : IntPtr.Zero;
    }

    // Asks the window to switch layout. Sends the request to the top-level window (the standard
    // target) and, if different, also to the control that has keyboard focus.
    // Returns false (with the Win32 error) if the top-level window refused the message.
    public static bool RequestLayout(IntPtr hwnd, IntPtr hkl, out int error)
    {
        bool ok = PostMessage(hwnd, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, hkl);
        error = ok ? 0 : Marshal.GetLastWin32Error();
        IntPtr focus = FocusOf(hwnd);
        if (focus != IntPtr.Zero && focus != hwnd)
            PostMessage(focus, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, hkl);
        return ok;
    }

    // ---------------------------------------------------------------- Smart Keyboard Auto Switch
    public delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion u; }

    public const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    public const uint LLKHF_INJECTED = 0x10;
    const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 2, KEYEVENTF_UNICODE = 4;
    const int GWL_STYLE = -16, ES_PASSWORD = 0x20;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT pt);

    // The top-level window under a screen point.
    public static IntPtr RootAt(int x, int y)
    {
        POINT pt = new POINT();
        pt.x = x;
        pt.y = y;
        return RootOf(WindowFromPoint(pt));
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern short VkKeyScanEx(char ch, IntPtr hkl);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] public static extern uint MapVirtualKeyEx(uint code, uint mapType, IntPtr hkl);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int ToUnicodeEx(uint vk, uint sc, byte[] state, [Out] StringBuilder buf, int size, uint flags, IntPtr hkl);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);

    public static bool IsDown(int vk)
    {
        return (GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    // The character a key makes in a layout, or '\0' (no character, a dead key, or more than one).
    public static char CharOf(uint vk, uint sc, bool shift, bool altGr, bool caps, IntPtr hkl)
    {
        byte[] state = new byte[256];
        if (shift) state[0x10] = 0x80;
        if (altGr) { state[0x11] = 0x80; state[0x12] = 0x80; }
        if (caps) state[0x14] = 0x01;
        // The virtual key depends on the layout it was typed with (AZERTY's A key sends VK_A where QWERTY
        // has Q), so take the key that sits at the same place in `hkl`. Numpad keys keep theirs:
        // their scan codes map to Home, End... regardless of Num Lock.
        if (sc != 0 && (vk < 0x60 || vk > 0x6F))
        {
            uint there = MapVirtualKeyEx(sc, 3 /* MAPVK_VSC_TO_VK_EX */, hkl);
            if (there != 0) vk = there;
        }
        StringBuilder sb = new StringBuilder(8);
        int n = ToUnicodeEx(vk, sc, state, sb, sb.Capacity, 4 /* leave the dead-key state alone */, hkl);
        return n == 1 ? sb[0] : '\0';
    }

    // Classic password boxes only; password fields inside browsers can't be detected this way.
    public static bool IsPasswordBox(IntPtr hwnd)
    {
        IntPtr focus = FocusOf(hwnd);
        return focus != IntPtr.Zero && (GetWindowLong(focus, GWL_STYLE) & ES_PASSWORD) != 0 &&
            ClassNameOf(focus).IndexOf("edit", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // Presses Backspace `erase` times, then types text as Unicode characters (whatever the layout).
    // Erases `erase` characters, types `text`, then presses `thenKeys` (virtual-key codes).
    public static void Retype(int erase, string text, params ushort[] thenKeys)
    {
        List<INPUT> inputs = new List<INPUT>();
        // Let go of modifiers still held (the Ctrl+Shift+Z fix key): Ctrl+Backspace would erase whole words.
        foreach (ushort m in new ushort[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C })
            if (IsDown(m)) inputs.Add(Key(m, (char)MapVirtualKey(m, 0), KEYEVENTF_KEYUP));
        for (int i = 0; i < erase; i++)
        {
            inputs.Add(Key(0x08, '\0', 0));
            inputs.Add(Key(0x08, '\0', KEYEVENTF_KEYUP));
        }
        foreach (char c in text)
        {
            inputs.Add(Key(0, c, KEYEVENTF_UNICODE));
            inputs.Add(Key(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
        }
        foreach (ushort vk in thenKeys)
        {
            char sc = (char)MapVirtualKey(vk, 0);   // some apps (terminals, remote desktop) read the scan code
            inputs.Add(Key(vk, sc, 0));
            inputs.Add(Key(vk, sc, KEYEVENTF_KEYUP));
        }
        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(INPUT)));
    }

    static INPUT Key(ushort vk, char scan, uint flags)
    {
        INPUT input = new INPUT();
        input.type = INPUT_KEYBOARD;
        input.u.ki.wVk = vk;
        input.u.ki.wScan = scan;
        input.u.ki.dwFlags = flags;
        return input;
    }

    // Lower-case exe file name of the app that owns the window, or null.
    public static string ResolveExe(IntPtr hwnd)
    {
        uint pid;
        return ResolveExe(hwnd, out pid);
    }

    // Same, and the id of that app's process (for Store apps, the app's own process, not the frame host).
    public static string ResolveExe(IntPtr hwnd, out uint appPid)
    {
        uint pid = ProcessIdOf(hwnd);
        appPid = pid;
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
            if (real != null)
            {
                exe = real;
                appPid = realPid;
            }
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
