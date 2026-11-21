using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

// Pictures of the Settings tabs for the Help tab, and the code that shows them.
// AutoLangSwitcher.exe --screenshots <folder> saves them as help-<name>.png, using example programs (not yours) and
// saving nothing. Copy them to Windows\Help to put them in the exe (build.bat and the .csproj embed them).
partial class AutoLangSwitcherContext
{
    static readonly string[] TabShots = { "apps", "general", "off" };   // the first three tabs, in order
    static readonly string[] HelpShots = { "apps", "general", "off", "rules", "tray" };

    public static void TakeScreenshots(string dir)
    {
        new AutoLangSwitcherContext(dir);
    }

    // Only what the Settings window needs: no hooks, timers or tray icon, and nothing is loaded or saved.
    AutoLangSwitcherContext(string dir)
    {
        IntPtr en = new IntPtr(0x04090409), he = new IntPtr(0x040D040D);
        map["WhatsApp.exe"] = he;
        map["WINWORD.EXE"] = he;
        map["OUTLOOK.EXE"] = he;
        map["chrome.exe"] = en;
        map["Code.exe"] = en;
        foreach (string exe in DefaultOffApps) offApps.Add(exe);
        LangRule startHe = new LangRule(), lockEn = new LangRule();
        startHe.Hkl = he;
        lockEn.Lock = true;
        lockEn.Hkl = en;
        rules["WhatsApp.exe"] = startHe;
        rules["Code.exe"] = lockEn;
        smartOn = true;   // so the General tab shows its options enabled
        tray.Icon = normalIcon;

        ShowSettings();
        Form form = settingsForm;
        TabControl tabs = null;
        foreach (Control c in form.Controls) if (c is TabControl) tabs = (TabControl)c;
        Directory.CreateDirectory(dir);
        for (int i = 0; i < TabShots.Length; i++)
        {
            tabs.SelectedIndex = i;
            Application.DoEvents();
            using (Bitmap b = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(b, new Rectangle(0, 0, form.Width, form.Height));
                // Keep only the inside: DrawToBitmap draws an old-style title bar.
                Point inside = form.PointToScreen(Point.Empty);
                Rectangle client = new Rectangle(inside.X - form.Left, inside.Y - form.Top, form.ClientSize.Width, form.ClientSize.Height);
                using (Bitmap c = b.Clone(client, b.PixelFormat))
                    c.Save(Path.Combine(dir, "help-" + TabShots[i] + ".png"), ImageFormat.Png);
            }
        }

        // Menus are windows of their own, so these two are taken from the screen, inside the Settings window only
        // (it is kept on top), so nothing else on the screen gets in.
        form.TopMost = true;
        form.Activate();

        // The Language rule menu, open on WhatsApp with Start in > Hebrew highlighted.
        tabs.SelectedIndex = 0;
        ListView list = (ListView)tabs.TabPages[0].Controls[0];
        foreach (ListViewItem item in list.Items) item.Selected = item.Text == "WhatsApp.exe";
        ContextMenuStrip ruleMenu = RuleMenu(new List<string>(), delegate { });
        ruleMenu.Show(list, new Point(90, 60));   // over the list, so the menus stay inside the window
        ToolStripMenuItem start = (ToolStripMenuItem)ruleMenu.Items[1];
        start.Select();
        start.ShowDropDown();
        foreach (ToolStripItem lang in start.DropDownItems) if (lang.Text.StartsWith("Hebrew")) lang.Select();
        Rectangle window = form.RectangleToScreen(form.ClientRectangle);
        SaveScreen(window, Path.Combine(dir, "help-rules.png"));
        ruleMenu.Close();

        // The tray menu (without "Restart as administrator", which only shows when not running as administrator).
        ContextMenuStrip trayMenu = MakeTrayMenu(false);
        trayMenu.Show(window.Left + 40, window.Top + 40);
        trayMenu.Items[1].Select();   // Help
        SaveScreen(trayMenu.Bounds, Path.Combine(dir, "help-tray.png"));
        trayMenu.Close();
        // The form is left open (the process exits next), so closing it can't save anything.
    }

    static void SaveScreen(Rectangle r, string file)
    {
        for (int i = 0; i < 10; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }   // let the menus draw
        using (Bitmap b = new Bitmap(r.Width, r.Height))
        {
            using (Graphics g = Graphics.FromImage(b)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
            b.Save(file, ImageFormat.Png);
        }
    }

    // Replaces {IMG:name} in the Help page with the picture embedded in the exe, or with nothing if it isn't there.
    static string WithHelpImages(string html)
    {
        foreach (string name in HelpShots)
        {
            string img = "";
            using (Stream s = typeof(AutoLangSwitcherContext).Assembly.GetManifestResourceStream("help-" + name + ".png"))
                if (s != null)
                {
                    byte[] data = new byte[s.Length];
                    s.Read(data, 0, data.Length);
                    img = "<img class='shot' alt='' src='data:image/png;base64," + Convert.ToBase64String(data) + "'>";
                }
            html = html.Replace("{IMG:" + name + "}", img);
        }
        return html;
    }
}
