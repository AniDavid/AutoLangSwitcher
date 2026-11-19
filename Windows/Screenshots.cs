using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

// Pictures of the Settings tabs for the Help tab, and the code that shows them.
// AutoLangSwitcher.exe --screenshots <folder> saves them as help-apps.png, help-general.png and help-off.png,
// using example programs (not yours) and saving nothing. Copy them to Windows\Help to put them in the exe.
partial class AutoLangSwitcherContext
{
    static readonly string[] HelpShots = { "apps", "general", "off" };   // the first three tabs, in order

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
        smartOn = true;   // so the General tab shows its options enabled
        tray.Icon = normalIcon;

        ShowSettings();
        Form form = settingsForm;
        TabControl tabs = null;
        foreach (Control c in form.Controls) if (c is TabControl) tabs = (TabControl)c;
        Directory.CreateDirectory(dir);
        for (int i = 0; i < HelpShots.Length; i++)
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
                    c.Save(Path.Combine(dir, "help-" + HelpShots[i] + ".png"), ImageFormat.Png);
            }
        }
        // The form is left open (the process exits next), so closing it can't save anything.
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
