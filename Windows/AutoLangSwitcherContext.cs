using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

// Tray app: watches which app has focus, remembers its layout and restores it on return.
partial class AutoLangSwitcherContext : ApplicationContext
{
    const string Title = "AutoLangSwitcher";
    static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    static readonly string DataDir = Path.Combine(AppData, "AutoLangSwitcher");
    static readonly string DataFile = Path.Combine(DataDir, "layouts.txt");
    // Where the app kept its data when it was called KeyboardMemory.
    static readonly string LegacyDataFile = Path.Combine(AppData, @"KeyboardMemory\layouts.txt");

    // Windows that are only "passing through" (taskbar, Alt+Tab switcher, menus).
    // Focus moving to these does not count as switching application.
    static readonly HashSet<string> IgnoredClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "MultitaskingViewFrame", "TaskSwitcherWnd",
        "ForegroundStaging", "XamlExplorerHostIslandWindow", "#32768", "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland", "Windows.UI.Core.CoreWindow", "WorkerW", "Progman"
    };

    // ---------------------------------------------------------------- State
    // exe -> layout. Sorted so the settings file and the menu list apps alphabetically.
    readonly SortedDictionary<string, IntPtr> map = new SortedDictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
    readonly List<string> logLines = new List<string>();
    readonly Win32.WinEventDelegate hookProc;   // kept in a field so the GC does not collect it
    readonly IntPtr hook;
    readonly Timer pollTimer = new Timer();
    readonly Timer saveTimer = new Timer();
    readonly NotifyIcon tray = new NotifyIcon();
    readonly ToolStripMenuItem pauseItem;
    readonly uint ownPid = (uint)Process.GetCurrentProcess().Id;

    IntPtr curHwnd = IntPtr.Zero;
    string curExe = null;
    int ignoreUntil = 0;   // Environment.TickCount after which recording resumes
    IntPtr expected = IntPtr.Zero;      // layout an automatic switch asked for, checked by Poll
    int retries = 0;                    // re-requests already made for the current switch
    IntPtr failedLayout = IntPtr.Zero;  // layout left behind by a failed switch; not the user's choice
    bool dirty = false;
    bool paused = false;
    bool capsOn = false;
    readonly Icon normalIcon = MakeIcon(false), capsIcon = MakeIcon(true);

    public AutoLangSwitcherContext()
    {
        Load();

        // Older versions (KeyboardMemory) started via the Run key or their own task, pointing at the
        // old exe. Replace that with this app's scheduled task (needs admin).
        if (Elevation.IsElevated && Startup.HasLegacy)
            Log("move startup to scheduled task: exit code " + Startup.Enable());

        // Tray icon and menu
        pauseItem = new ToolStripMenuItem("Pause", null, delegate { SetPaused(!paused); });

        ContextMenuStrip menu = new ContextMenuStrip();
        if (!Elevation.IsElevated)
        {
            menu.Items.Add(new ToolStripMenuItem("Restart as administrator", null, delegate { RestartAsAdmin(); }));
            menu.Items.Add(new ToolStripSeparator());
        }
        ToolStripMenuItem settingsItem = new ToolStripMenuItem("Settings...", null, delegate { ShowSettings(); });
        settingsItem.Font = new Font(settingsItem.Font, FontStyle.Bold);   // default action, same as double-click
        menu.Items.Add(settingsItem);
        menu.Items.Add(pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        updateItem = new ToolStripMenuItem("Check for updates...", null, delegate { UpdateMenuClicked(); });
        menu.Items.Add(updateItem);
        menu.Items.Add(new ToolStripMenuItem("About " + Title, null, delegate { ShowAbout(); }));
        menu.Items.Add(new ToolStripMenuItem("Exit", null, delegate { ExitThread(); }));

        tray.Icon = normalIcon;
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += delegate { ShowSettings(); };
        tray.Visible = true;
        UpdateTooltip();

        // Watch focus changes
        hookProc = OnWinEvent;
        hook = Win32.SetWinEventHook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            hookProc, 0, 0, Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);
        Log("started " + (Elevation.IsElevated ? "as administrator" : "WITHOUT admin rights") + ", " + map.Count +
            " app(s) loaded, focus hook " + (hook != IntPtr.Zero ? "OK" : "FAILED"));

        // Poll the active app's layout so changes (Alt+Shift, Win+Space) are remembered
        pollTimer.Interval = 300;
        pollTimer.Tick += delegate { UpdateCapsLock(); Poll(); };
        pollTimer.Start();

        saveTimer.Interval = 5000;
        saveTimer.Tick += delegate { if (dirty) Save(); };
        saveTimer.Start();

        OnForeground(Win32.GetForegroundWindow());
        LoadSmartSettings();
        StartUpdateChecks();
    }

    protected override void ExitThreadCore()
    {
        if (hook != IntPtr.Zero) Win32.UnhookWinEvent(hook);
        StopSmart();
        pollTimer.Stop();
        saveTimer.Stop();
        updateTimer.Stop();
        if (dirty) Save();
        if (settingsForm != null) settingsForm.Close();
        tray.Visible = false;
        tray.Dispose();
        base.ExitThreadCore();
    }

    // ---------------------------------------------------------------- Core logic
    void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild,
        uint dwEventThread, uint dwmsEventTime)
    {
        try { OnForeground(hwnd); } catch { /* never let a hook callback crash the app */ }
    }

    void OnForeground(IntPtr hwnd)
    {
        hwnd = Win32.RootOf(hwnd);
        if (hwnd == IntPtr.Zero || hwnd == curHwnd || IsIgnored(hwnd)) return;
        string exe = Win32.ResolveExe(hwnd);

        // explorer.exe also draws the taskbar, its language menu and other pop-ups. Only File Explorer
        // windows count as an app; otherwise picking a language from the taskbar would be undone.
        string cls = Win32.ClassNameOf(hwnd);
        if (exe == "explorer.exe" && cls != "CabinetWClass")
        {
            Log("focus explorer.exe (" + cls + "): part of Windows, ignored");
            return;
        }

        // 1. Remember what the app we are leaving was using.
        if (!paused && curExe != null && Win32.IsWindow(curHwnd) && Elapsed(ignoreUntil))
            Record(curExe, Win32.LayoutOf(curHwnd));

        // 2. Switch to the new app.
        curHwnd = hwnd;
        curExe = exe;
        expected = IntPtr.Zero;
        failedLayout = IntPtr.Zero;
        if (exe == null || paused) return;

        IntPtr wanted;
        if (!map.TryGetValue(exe, out wanted))
        {
            Log("focus " + exe + ": not remembered yet");
            return;
        }
        if (!Win32.IsInstalled(wanted))
        {
            Log("focus " + exe + ": saved layout " + Win32.LayoutName(wanted) + " is not installed, skipping");
            return;
        }
        // Always ask for the layout. With Windows' default "one input language for all apps"
        // mode, a background app's thread still reports its old layout, so comparing
        // against it is unreliable and would skip needed switches.
        int error;
        if (!Win32.RequestLayout(hwnd, wanted, out error))
            Log("  PostMessage failed (error " + error + ") - app may be running as administrator");
        Log("auto switch " + exe + ": " + Win32.LayoutName(Win32.LayoutOf(hwnd)) + " -> " + Win32.LayoutName(wanted));
        // Give the app a moment to switch before we start recording again,
        // otherwise we could record the old layout for this app.
        ignoreUntil = Environment.TickCount + 800;
        expected = wanted;
        retries = 0;
    }

    void Poll()
    {
        if (paused || curExe == null || !Win32.IsWindow(curHwnd) || !Elapsed(ignoreUntil)) return;
        // Skip while something else is in front, e.g. the taskbar or Alt+Tab.
        if (Win32.RootOf(Win32.GetForegroundWindow()) != curHwnd) return;
        IntPtr now = Win32.LayoutOf(curHwnd);
        if (expected != IntPtr.Zero)
        {
            if (now != expected && retries < 2)
            {
                // In Windows' "one input language for all apps" mode a newly focused app can report the
                // wanted layout (stale), so the first request is a no-op and Windows then applies the
                // previous app's layout. Asking again now is a real change and sticks.
                retries++;
                int error;
                Win32.RequestLayout(curHwnd, expected, out error);
                Log("auto switch retry " + retries + " " + curExe + ": " + Win32.LayoutName(now) + " -> " + Win32.LayoutName(expected));
                ignoreUntil = Environment.TickCount + 500;
                return;
            }
            // The app ignored the automatic switch: keep the remembered layout instead of
            // mistaking the unchanged one for the user's choice.
            if (now != expected)
            {
                Log("auto switch FAILED " + curExe + ": still " + Win32.LayoutName(now) + ", keeping " + Win32.LayoutName(expected));
                failedLayout = now;
            }
            expected = IntPtr.Zero;
        }
        Record(curExe, now);
    }

    void Record(string exe, IntPtr hkl)
    {
        if (exe == null || hkl == IntPtr.Zero || hkl == failedLayout) return;
        IntPtr old;
        bool known = map.TryGetValue(exe, out old);
        if (known && old == hkl) return;
        map[exe] = hkl;
        dirty = true;
        // Automatic switches set the remembered layout, so any other change came from the user.
        Log((known ? "manual switch " + exe + ": " + Win32.LayoutName(old) + " -> " : "remember new app " + exe + ": ") +
            Win32.LayoutName(hkl) + " (" + hkl.ToInt64().ToString("X") + ")");
        UpdateTooltip();
    }

    bool IsIgnored(IntPtr hwnd)
    {
        return Win32.ProcessIdOf(hwnd) == ownPid || IgnoredClasses.Contains(Win32.ClassNameOf(hwnd));
    }

    static bool Elapsed(int tick)
    {
        return unchecked(Environment.TickCount - tick) >= 0;
    }

    // ---------------------------------------------------------------- Debug log
    void Log(string msg)
    {
        logLines.Add(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg);
        if (logLines.Count > 500) logLines.RemoveRange(0, logLines.Count - 500);
    }

    // ---------------------------------------------------------------- Persistence
    // File format: "program.exe = hex-layout-id ; comment" per line, ';' starts a comment.
    void Load()
    {
        try
        {
            // Until this app saves its own file, read the one KeyboardMemory left behind.
            // The old file is never changed, so nothing is lost if anything below fails.
            string source = File.Exists(DataFile) ? DataFile : LegacyDataFile;
            if (!File.Exists(source)) return;
            if (source == LegacyDataFile) Log("loading settings from " + LegacyDataFile);
            foreach (string raw in File.ReadAllLines(source, Encoding.UTF8))
            {
                string line = raw;
                int semi = line.IndexOf(';');
                if (semi >= 0) line = line.Substring(0, semi);
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string exe = line.Substring(0, eq).Trim();
                string hex = line.Substring(eq + 1).Trim();
                long v;
                if (exe.Length > 0 && long.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v))
                    map[exe] = new IntPtr(v);
            }
        }
        catch { }
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("; AutoLangSwitcher - last keyboard layout per application.");
            sb.AppendLine("; Format: program.exe = layout-id ; language. Delete a line to forget an app.");
            sb.AppendLine("; Exit AutoLangSwitcher before editing this file by hand.");
            foreach (KeyValuePair<string, IntPtr> e in map)
                sb.AppendLine(e.Key + " = " + e.Value.ToInt64().ToString("X") + " ; " + Win32.LayoutName(e.Value));
            File.WriteAllText(DataFile, sb.ToString(), Encoding.UTF8);
            dirty = false;
        }
        catch { }
    }

    // Saves right away after a change the user made from the menu.
    void SaveNow()
    {
        dirty = true;
        Save();
        UpdateTooltip();
    }

    // ---------------------------------------------------------------- Tray UI
    void ForgetAll()
    {
        if (MessageBox.Show("Forget the remembered keyboard layout for all apps?", Title,
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        map.Clear();
        SaveNow();
    }

    void SetPaused(bool value)
    {
        paused = value;
        pauseItem.Checked = value;
        if (pauseBox != null) pauseBox.Checked = value;
        UpdateTooltip();
    }

    // Caps Lock indicator: orange keyboard in the tray while it is on.
    void UpdateCapsLock()
    {
        bool on = Control.IsKeyLocked(Keys.CapsLock);
        if (on == capsOn) return;
        capsOn = on;
        tray.Icon = on ? capsIcon : normalIcon;
        UpdateTooltip();
    }

    void UpdateTooltip()
    {
        string t = (capsOn ? "CAPS LOCK ON - " : "") + Title +(Elevation.IsElevated ? " (admin)" : " (not admin)") +
            (paused ? " (paused)" : "") + " - " + map.Count + " app(s)";
        tray.Text = t.Length > 63 ? t.Substring(0, 63) : t;   // NotifyIcon limit
    }

    // Edit the credit lines here (the Mac version has the same text in AutoLangSwitcher.swift).
    void ShowAbout()
    {
        MessageBox.Show(
            Title + " " + typeof(AutoLangSwitcherContext).Assembly.GetName().Version.ToString(3) + "\n\n" +
            "Remembers the keyboard language you use in each program and switches back to it when you return.\n\n" +
            "Created by AniDavid\n" +
            "Built with the help of Claude (Anthropic)",
            "About " + Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void RestartAsAdmin()
    {
        if (dirty) Save();
        if (Elevation.RelaunchElevated("--restarted")) ExitThread();
    }

    // Returns the state re-read from Windows after the change, so the caller shows what really happened.
    bool SetStartup(bool enable)
    {
        if (!enable)
        {
            Startup.Disable();
        }
        else if (!Elevation.IsElevated)
        {
            if (MessageBox.Show("Starting with Windows as administrator needs admin rights.\n\nRestart AutoLangSwitcher as administrator now?",
                Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                RestartAsAdmin();
        }
        else
        {
            int code = Startup.Enable();
            Log("create startup task: exit code " + code);
            if (code != 0)
                MessageBox.Show("Could not create the startup task (see the Log tab in Settings).", Title,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        return Startup.IsEnabled;
    }

    // Blue keyboard with a green "restore" arrow badge. Drawn at the real tray size so it stays sharp
    // (shortcut: on net48 the app is not DPI-aware, so this is 16px and Windows scales it on high-DPI screens).
    static Icon MakeIcon(bool caps)
    {
        int size = SystemInformation.SmallIconSize.Width;
        using (Bitmap bmp = new Bitmap(size, size))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                g.ScaleTransform(size / 64f, size / 64f);   // designed on a 64x64 grid

                // Keyboard body with two rows of keys and a space bar
                RectangleF body = new RectangleF(2, 12, 56, 38);
                using (GraphicsPath p = RoundRect(body, 9))
                using (LinearGradientBrush b = new LinearGradientBrush(body,
                    caps ? Color.FromArgb(255, 170, 40) : Color.FromArgb(66, 150, 255),
                    caps ? Color.FromArgb(220, 110, 0) : Color.FromArgb(24, 92, 210), 90f))
                    g.FillPath(b, p);
                if (caps)
                {
                    // A capital "A" instead of the keys, so the state doesn't depend on seeing the color.
                    using (GraphicsPath letter = new GraphicsPath())
                    using (StringFormat sf = new StringFormat())
                    using (FontFamily font = new FontFamily("Arial"))
                    using (SolidBrush w = new SolidBrush(Color.White))
                    {
                        sf.Alignment = StringAlignment.Center;
                        sf.LineAlignment = StringAlignment.Center;
                        letter.AddString("A", font, (int)FontStyle.Bold, 42, new RectangleF(2, 10, 44, 44), sf);
                        g.FillPath(w, letter);
                    }
                }
                else using (SolidBrush w = new SolidBrush(Color.White))
                {
                    for (int row = 0; row < 2; row++)
                        for (int col = 0; col < 4; col++)
                            FillRound(g, w, new RectangleF(8 + col * 11, 18 + row * 10, 7, 6), 1.5f);
                    FillRound(g, w, new RectangleF(14, 39, 24, 6), 1.5f);
                }

                // Badge: green disc with a white ring, bottom right
                PointF c = new PointF(48, 48);
                const float r = 14;
                using (SolidBrush ring = new SolidBrush(Color.White))
                    g.FillEllipse(ring, c.X - r, c.Y - r, 2 * r, 2 * r);
                using (SolidBrush fill = new SolidBrush(Color.FromArgb(34, 170, 80)))
                    g.FillEllipse(fill, c.X - r + 2.5f, c.Y - r + 2.5f, 2 * r - 5, 2 * r - 5);

                // Clockwise arrow from 300deg round to 180deg, with its head at the end
                const float ar = 6.5f, start = 300, sweep = 240;
                using (Pen pen = new Pen(Color.White, 3.2f))
                {
                    pen.StartCap = LineCap.Round;
                    g.DrawArc(pen, c.X - ar, c.Y - ar, 2 * ar, 2 * ar, start, sweep);
                }
                double a = (start + sweep) * Math.PI / 180;
                float cos = (float)Math.Cos(a), sin = (float)Math.Sin(a);
                PointF end = new PointF(c.X + ar * cos, c.Y + ar * sin);
                const float len = 5.5f, half = 4.2f;   // head length along the clockwise tangent (-sin, cos)
                using (SolidBrush w = new SolidBrush(Color.White))
                    g.FillPolygon(w, new PointF[] {
                        new PointF(end.X - sin * len, end.Y + cos * len),
                        new PointF(end.X + cos * half, end.Y + sin * half),
                        new PointF(end.X - cos * half, end.Y - sin * half) });
            }
            // One icon handle for the lifetime of the app; Windows frees it on exit.
            return Icon.FromHandle(bmp.GetHicon());
        }
    }

    static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        GraphicsPath p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static void FillRound(Graphics g, Brush b, RectangleF r, float radius)
    {
        using (GraphicsPath p = RoundRect(r, radius)) g.FillPath(b, p);
    }
}
