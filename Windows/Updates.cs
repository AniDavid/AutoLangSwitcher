using System;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

// Check for updates: asks GitHub for the latest release's version. Nothing about the user is sent.
partial class AutoLangSwitcherContext
{
    const string ReleasesPage = "https://github.com/AniDavid/AutoLangSwitcher/releases/latest";
    const string LatestApi = "https://api.github.com/repos/AniDavid/AutoLangSwitcher/releases/latest";

    bool autoUpdateCheck = true;
    string newVersion;            // a newer release, once found
    bool updateBalloon;           // the balloon on screen is the update one
    ToolStripMenuItem updateItem;
    readonly Timer updateTimer = new Timer();

    void StartUpdateChecks()
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(SettingsKey))
                if (k != null) autoUpdateCheck = Convert.ToInt32(k.GetValue("CheckForUpdates", 1)) == 1;
        }
        catch (Exception ex) { Log("could not read the update setting: " + ex.Message); }

        tray.BalloonTipClicked += delegate { if (updateBalloon) OpenReleasesPage(); };
        tray.BalloonTipClosed += delegate { updateBalloon = false; };

        // First check a minute after start (the network may not be up yet at login), then once a day.
        updateTimer.Interval = 60 * 1000;
        updateTimer.Tick += delegate
        {
            updateTimer.Interval = 24 * 60 * 60 * 1000;
            if (autoUpdateCheck && newVersion == null) CheckForUpdates(false);
        };
        updateTimer.Start();
    }

    void SetAutoUpdateCheck(bool on)
    {
        autoUpdateCheck = on;
        try
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(SettingsKey))
                k.SetValue("CheckForUpdates", on ? 1 : 0, RegistryValueKind.DWord);
        }
        catch (Exception ex) { Log("could not save the update setting: " + ex.Message); }
    }

    // Tray menu item: checks now, or opens the download page once a newer version is known.
    void UpdateMenuClicked()
    {
        if (newVersion != null) OpenReleasesPage();
        else CheckForUpdates(true);
    }

    void CheckForUpdates(bool manual)
    {
        Task.Run(() => FetchLatestVersion()).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                string why = t.Exception.GetBaseException().Message;
                Log("update check failed: " + why);
                if (manual)
                    MessageBox.Show("Could not check for updates:\n" + why, Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Version current = typeof(AutoLangSwitcherContext).Assembly.GetName().Version;
            Version latest = new Version(t.Result);
            if (!IsNewer(latest, current))
            {
                Log("update check: " + current.ToString(3) + " is the latest version");
                if (manual)
                    MessageBox.Show("You have the latest version (" + current.ToString(3) + ").", Title,
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            newVersion = t.Result;
            updateItem.Text = "Download version " + newVersion + "...";
            Log("update check: version " + newVersion + " is available");
            if (manual)
            {
                if (MessageBox.Show("Version " + newVersion + " is available (you have " + current.ToString(3) +
                    ").\n\nOpen the download page?", Title, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    OpenReleasesPage();
            }
            else
            {
                updateBalloon = true;
                tray.ShowBalloonTip(15000, Title, "Version " + newVersion + " is available. Click here to download it.", ToolTipIcon.Info);
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    static string FetchLatestVersion()
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        using (WebClient wc = new WebClient())
        {
            wc.Headers[HttpRequestHeader.UserAgent] = "AutoLangSwitcher";   // GitHub refuses requests without one
            wc.Encoding = Encoding.UTF8;
            string v = VersionOfRelease(wc.DownloadString(LatestApi));
            if (v == null) throw new Exception("GitHub's reply has no version number");
            return v;
        }
    }

    // "tag_name": "v1.2.1" -> "1.2.1"
    static string VersionOfRelease(string json)
    {
        Match m = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?(\\d+(\\.\\d+){1,3})\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    // Compares the first three parts only: the exe's version is 1.2.1.0, the tag is 1.2.1.
    static bool IsNewer(Version latest, Version current)
    {
        return new Version(latest.Major, latest.Minor, Math.Max(latest.Build, 0)).CompareTo(
            new Version(current.Major, current.Minor, Math.Max(current.Build, 0))) > 0;
    }

    void OpenReleasesPage()
    {
        try { Process.Start(ReleasesPage); }
        catch (Exception ex) { Log("could not open the download page: " + ex.Message); }
    }

    static void UpdateSelfTest(Action<bool, string> check)
    {
        check(VersionOfRelease("{\"url\":\"x\",\"tag_name\": \"v1.2.1\",\"name\":\"1.2.1\"}") == "1.2.1", "update check reads 'v1.2.1' from GitHub's reply");
        check(VersionOfRelease("{\"tag_name\":\"nightly\"}") == null, "update check ignores a tag that isn't a version");
        check(IsNewer(new Version("1.2.2"), new Version("1.2.1.0")), "1.2.2 is newer than 1.2.1.0");
        check(IsNewer(new Version("1.10.0"), new Version("1.9.0.0")), "1.10.0 is newer than 1.9.0.0");
        check(!IsNewer(new Version("1.2.1"), new Version("1.2.1.0")), "1.2.1 is not newer than 1.2.1.0");
    }
}
