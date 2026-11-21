using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Windows.Forms;

// Settings window: everything is shown inside the app, because opening files in another program
// (Notepad, Explorer) fails from this elevated process on some Windows 11 machines.
partial class AutoLangSwitcherContext
{
    Form settingsForm;
    TabControl settingsTabs;

    // Tray menu > Help: the Settings window, on the Help tab.
    void ShowHelp()
    {
        ShowSettings();
        foreach (TabPage page in settingsTabs.TabPages)
            if (page.Text == "Help") settingsTabs.SelectedTab = page;
    }

    // Apps tab: the menu for the selected programs (right-click, double-click, or the menu key).
    ContextMenuStrip AppMenu(List<string> exes, Action done)
    {
        ContextMenuStrip menu = new ContextMenuStrip();
        foreach (bool lockIt in new bool[] { true, false })
        {
            ToolStripMenuItem sub = new ToolStripMenuItem(lockIt ? "Lock to (always this language)" : "Start in (this language when opened)");
            foreach (IntPtr hkl in Win32.Layouts())
            {
                LangRule r = new LangRule();
                r.Lock = lockIt;
                r.Hkl = hkl;
                sub.DropDownItems.Add(Win32.LayoutName(hkl), null, delegate { SetRule(exes, r); done(); });
            }
            menu.Items.Add(sub);
        }
        menu.Items.Add("No rule (remember the last language)", null, delegate { SetRule(exes, null); done(); });
        menu.Items.Add(new ToolStripSeparator());
        bool allOff = exes.TrueForAll(delegate(string exe) { return offApps.Contains(exe); });
        menu.Items.Add(allOff ? "Turn smart switch on here" : "Turn smart switch off here", null,
            delegate { SetAppsOff(exes, !allOff); done(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exes.Count == 1 ? "Forget this program" : "Forget these programs", null, delegate
        {
            List<string> ruled = new List<string>();
            foreach (string exe in exes)
            {
                map.Remove(exe);
                if (rules.ContainsKey(exe)) ruled.Add(exe);
            }
            if (ruled.Count > 0) SetRule(ruled, null);
            SaveNow();
            done();
        });
        return menu;
    }

    CheckBox pauseBox;   // kept in sync with the tray's Pause item by SetPaused

    void ShowSettings()
    {
        if (settingsForm != null)
        {
            if (settingsForm.WindowState == FormWindowState.Minimized) settingsForm.WindowState = FormWindowState.Normal;
            settingsForm.Activate();
            return;
        }

        Form form = new Form();
        form.Text = Title + " Settings";
        form.Icon = tray.Icon;
        form.StartPosition = FormStartPosition.CenterScreen;
        form.Size = new Size(640, 440);
        form.MinimumSize = new Size(420, 320);

        TabControl tabs = new TabControl();
        tabs.Dock = DockStyle.Fill;

        // ---- Apps
        ListView list = new ListView();
        list.Dock = DockStyle.Fill;
        list.View = View.Details;
        list.FullRowSelect = true;
        list.MultiSelect = true;
        list.HideSelection = false;
        list.ShowItemToolTips = true;   // the exe name, over the program's name
        list.Columns.Add("Program", 170);
        list.Columns.Add("Language", 140);
        list.Columns.Add("Rule", 175);
        list.Columns.Add("Smart switch", 85);
        Button removeAll = MakeButton("Remove all");
        Action fillApps = delegate
        {
            list.BeginUpdate();
            list.Items.Clear();
            // Remembered programs, plus locked ones that never got a remembered language, by name.
            List<string> exes = new List<string>(map.Keys);
            foreach (string exe in rules.Keys) if (!map.ContainsKey(exe)) exes.Add(exe);
            Dictionary<string, string> names = new Dictionary<string, string>();   // looked up once each
            foreach (string exe in exes) names[exe] = DisplayName(exe);
            exes.Sort(delegate(string x, string y) { return string.Compare(names[x], names[y], StringComparison.CurrentCultureIgnoreCase); });
            foreach (string exe in exes)
            {
                IntPtr hkl;
                ListViewItem item = new ListViewItem(new string[] {
                    names[exe], map.TryGetValue(exe, out hkl) ? Win32.LayoutName(hkl) : "", RuleText(exe),
                    !smartOn ? "" : offApps.Contains(exe) ? "Off" : "On" });
                item.Tag = exe;
                item.ToolTipText = exe;
                list.Items.Add(item);
            }
            list.EndUpdate();
        };
        // Right-click (or double-click, or the menu key) a program for its menu; it applies to all selected programs.
        Action<Point> showMenu = delegate(Point at)
        {
            List<string> exes = new List<string>();
            foreach (ListViewItem item in list.SelectedItems) exes.Add((string)item.Tag);
            if (exes.Count > 0) AppMenu(exes, fillApps).Show(list, at);
        };
        list.MouseUp += delegate(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            ListViewItem hit = list.GetItemAt(e.X, e.Y);
            if (hit != null && !hit.Selected)
            {
                list.SelectedItems.Clear();
                hit.Selected = true;
            }
            showMenu(e.Location);
        };
        list.MouseDoubleClick += delegate(object sender, MouseEventArgs e) { showMenu(e.Location); };
        list.KeyDown += delegate(object sender, KeyEventArgs e)
        {
            if (list.SelectedItems.Count == 0) return;
            if (e.KeyCode == Keys.Apps || (e.KeyCode == Keys.F10 && e.Shift))
            {
                Rectangle r = list.SelectedItems[0].Bounds;
                showMenu(new Point(r.Left + 20, r.Bottom));
                e.Handled = true;
            }
        };
        removeAll.Click += delegate { ForgetAll(); fillApps(); };
        Label appsHint = new Label();
        appsHint.AutoSize = true;
        appsHint.ForeColor = SystemColors.GrayText;
        appsHint.Margin = new Padding(12, 8, 3, 3);
        appsHint.Text = "Right-click a program to lock its language, set the one it opens in,\nturn smart switch off or on, or forget it.";
        TabPage appsTab = MakeTab("Apps", list, MakeButtonBar(removeAll));
        appsTab.Controls[1].Controls.Add(appsHint);   // the hint sits next to the button

        // ---- General
        pauseBox = new CheckBox();
        pauseBox.Text = "Pause (stop switching languages for now)";
        pauseBox.AutoSize = true;
        pauseBox.AutoCheck = false;   // the state is set by SetPaused, so tray and window agree
        pauseBox.Checked = paused;
        pauseBox.Click += delegate { SetPaused(!paused); };

        CheckBox startupBox = new CheckBox();
        startupBox.Text = "Start with Windows";
        startupBox.AutoSize = true;
        startupBox.AutoCheck = false;   // shows what Windows reports after the change, not the click
        startupBox.Checked = Startup.IsEnabled;
        startupBox.Click += delegate
        {
            form.Cursor = Cursors.WaitCursor;
            startupBox.Checked = SetStartup(!startupBox.Checked);
            form.Cursor = Cursors.Default;
        };

        FlowLayoutPanel general = new FlowLayoutPanel();
        general.Dock = DockStyle.Fill;
        general.FlowDirection = FlowDirection.TopDown;
        general.WrapContents = false;
        general.AutoScroll = true;
        general.Controls.Add(pauseBox);
        general.Controls.Add(startupBox);

        CheckBox updatesBox = new CheckBox();
        updatesBox.Text = "Check for updates once a day";
        updatesBox.AutoSize = true;
        updatesBox.Checked = autoUpdateCheck;
        updatesBox.CheckedChanged += delegate { SetAutoUpdateCheck(updatesBox.Checked); };
        general.Controls.Add(updatesBox);

        CheckBox smartBox = new CheckBox();
        smartBox.Text = "Smart Keyboard Auto Switch: fix words typed in the wrong keyboard language";
        smartBox.AutoSize = true;
        smartBox.Margin = new Padding(3, 16, 3, 0);
        smartBox.Checked = smartOn;
        Label smartNote = new Label();
        smartNote.AutoSize = true;
        smartNote.Margin = new Padding(20, 0, 3, 3);
        smartNote.ForeColor = SystemColors.GrayText;
        smartNote.Text = "Reads your typing to check each finished word. Nothing is saved or sent, except the list below.";
        RadioButton suggestRadio = new RadioButton();
        suggestRadio.Text = "Only suggest (press the fix key to fix the word)";
        suggestRadio.AutoSize = true;
        suggestRadio.Margin = new Padding(20, 3, 3, 0);
        suggestRadio.Checked = !smartAuto;
        RadioButton autoRadio = new RadioButton();
        autoRadio.Text = "Fix automatically";
        autoRadio.AutoSize = true;
        autoRadio.Margin = new Padding(20, 0, 3, 3);
        autoRadio.Checked = smartAuto;
        FlowLayoutPanel keyRow = new FlowLayoutPanel();
        keyRow.AutoSize = true;
        keyRow.Margin = new Padding(17, 3, 3, 0);
        Label keyLabel = new Label();
        keyLabel.Text = "Fix key (also undoes a fix, or changes the last word on request):";
        keyLabel.AutoSize = true;
        keyLabel.Margin = new Padding(3, 6, 3, 0);
        ComboBox keyBox = new ComboBox();
        keyBox.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (FixKey k in FixKeys) keyBox.Items.Add(k.Name);
        keyBox.SelectedIndex = fixKey;
        keyRow.Controls.Add(keyLabel);
        keyRow.Controls.Add(keyBox);
        Label keepLabel = new Label();
        keepLabel.AutoSize = true;
        keepLabel.Margin = new Padding(20, 8, 3, 0);
        keepLabel.Text = "Never change these words (as they appear when typed; words you undo are added here):";
        TextBox keepBox = new TextBox();
        keepBox.Multiline = true;
        keepBox.ScrollBars = ScrollBars.Vertical;
        keepBox.Size = new Size(320, 70);
        keepBox.Margin = new Padding(20, 3, 3, 3);
        keepBox.Text = KeepWordsText();
        // The words the box was filled with: an undo while the window is open adds words that the box
        // doesn't show, and saving the box must not drop them.
        HashSet<string> keepShown = new HashSet<string>(keepWords, StringComparer.OrdinalIgnoreCase);
        EventHandler saveKeep = delegate
        {
            MergeKeepWords(keepBox.Text, keepShown);
            keepShown = new HashSet<string>(keepWords, StringComparer.OrdinalIgnoreCase);
            keepBox.Text = KeepWordsText();
        };
        keepBox.Leave += saveKeep;
        form.FormClosing += delegate { MergeKeepWords(keepBox.Text, keepShown); };
        Action enable = delegate
        {
            suggestRadio.Enabled = autoRadio.Enabled = keyBox.Enabled = keepBox.Enabled = smartBox.Checked;
        };
        enable();
        EventHandler smartChanged = delegate
        {
            enable();
            SetSmart(smartBox.Checked, autoRadio.Checked, keyBox.SelectedIndex);
        };
        smartBox.CheckedChanged += smartChanged;
        autoRadio.CheckedChanged += smartChanged;   // fires for both radios, since one turns the other off
        keyBox.SelectedIndexChanged += smartChanged;
        general.Controls.Add(smartBox);
        general.Controls.Add(smartNote);
        general.Controls.Add(suggestRadio);
        general.Controls.Add(autoRadio);
        general.Controls.Add(keyRow);
        general.Controls.Add(keepLabel);
        general.Controls.Add(keepBox);
        Label status = new Label();
        status.AutoSize = true;
        status.Margin = new Padding(3, 16, 3, 3);
        status.Text = Elevation.IsElevated
            ? "Running as administrator."
            : "Not running as administrator: programs that run as administrator can't be switched.";
        general.Controls.Add(status);
        if (!Elevation.IsElevated)
        {
            Button restart = MakeButton("Restart as administrator");
            restart.Click += delegate { RestartAsAdmin(); };
            general.Controls.Add(restart);
        }
        TabPage generalTab = MakeTab("General", general, null);

        // ---- Smart switch off: programs where smart switch leaves the typing alone
        TextBox offBox = new TextBox();
        offBox.Multiline = true;
        offBox.ScrollBars = ScrollBars.Vertical;
        offBox.Dock = DockStyle.Fill;
        offBox.Text = OffAppsText();
        Label offLabel = new Label();
        offLabel.Dock = DockStyle.Top;
        offLabel.Height = 48;
        offLabel.Text = "Smart Keyboard Auto Switch stays off in these programs (exe names, one per line). " +
            "Code editors and terminals are here to start with: keys there can be commands, and auto-complete changes the text.";
        Panel offPanel = new Panel();
        offPanel.Dock = DockStyle.Fill;
        offPanel.Controls.Add(offBox);     // Fill is added first so it docks last
        offPanel.Controls.Add(offLabel);
        Action saveOff = delegate
        {
            SetOffApps(offBox.Text, true);
            offBox.Text = OffAppsText();
        };
        Button saveOffButton = MakeButton("Save");
        saveOffButton.Click += delegate { saveOff(); };
        // Only when edited: the Apps tab may have changed the list since the box was filled.
        offBox.Leave += delegate { if (offBox.Modified) saveOff(); };
        form.FormClosing += delegate { if (offBox.Modified) SetOffApps(offBox.Text, true); };
        TabPage offTab = MakeTab("Smart switch off", offPanel, MakeButtonBar(saveOffButton));

        // ---- Settings file
        TextBox fileBox = MakeTextView();
        Label pathLabel = new Label();
        pathLabel.Dock = DockStyle.Top;
        pathLabel.AutoEllipsis = true;
        pathLabel.Height = 22;
        pathLabel.Text = DataFile;
        Action fillFile = delegate
        {
            Save();   // so the file matches what the app has in memory
            try { fileBox.Text = File.Exists(DataFile) ? File.ReadAllText(DataFile, Encoding.UTF8) : "(no apps remembered yet)"; }
            catch (Exception ex) { fileBox.Text = "Could not read the file: " + ex.Message; }
        };
        Button refreshFile = MakeButton("Refresh");
        refreshFile.Click += delegate { fillFile(); };
        TabPage fileTab = MakeTab("Settings file", fileBox, MakeButtonBar(refreshFile));
        fileTab.Controls.Add(pathLabel);   // added last so it docks first, above the text

        // ---- Log
        RichTextBox logBox = new RichTextBox();
        logBox.Dock = DockStyle.Fill;
        logBox.ReadOnly = true;
        logBox.WordWrap = false;
        logBox.BackColor = SystemColors.Window;
        logBox.Font = new Font(FontFamily.GenericMonospace, 9f);
        Action fillLog = delegate
        {
            logBox.Clear();
            foreach (string line in logLines)
            {
                logBox.SelectionColor = LogColor(line);
                logBox.AppendText(line + "\n");
            }
            logBox.SelectionStart = logBox.TextLength;
            logBox.ScrollToCaret();
        };
        Button refreshLog = MakeButton("Refresh");
        refreshLog.Click += delegate { fillLog(); };
        Button copyLog = MakeButton("Copy all");
        copyLog.Click += delegate { if (logBox.TextLength > 0) Clipboard.SetText(logBox.Text); };
        Button clearLog = MakeButton("Clear");
        clearLog.Click += delegate { logLines.Clear(); fillLog(); };
        Label legend = new Label();
        legend.AutoSize = true;
        legend.Margin = new Padding(12, 8, 3, 3);
        legend.Text = "Blue: you switched   Green: app switched   Purple: smart switch   Gray: focus   Red: problem";
        TabPage logTab = MakeTab("Log", logBox, MakeButtonBar(refreshLog, copyLog, clearLog));
        logTab.Controls[1].Controls.Add(legend);   // the legend sits next to the buttons

        // ---- Help
        WebBrowser help = new WebBrowser();
        help.Dock = DockStyle.Fill;
        help.AllowWebBrowserDrop = false;
        help.IsWebBrowserContextMenuEnabled = false;
        help.ScriptErrorsSuppressed = true;
        help.WebBrowserShortcutsEnabled = true;   // Ctrl+C and Ctrl+A still work
        help.DocumentText = WithHelpImages(HelpHtml.Replace("{FIXKEY}", WebUtility.HtmlEncode(FixKeyName)));
        // Web links open in the default browser, not inside the tab.
        help.Navigating += delegate(object sender, WebBrowserNavigatingEventArgs e)
        {
            if (e.Url.Scheme != "http" && e.Url.Scheme != "https") return;
            e.Cancel = true;
            try { Process.Start(e.Url.ToString()); }
            catch (Exception ex) { Log("could not open " + e.Url + ": " + ex.Message); }
        };
        TabPage helpTab = MakeTab("Help", help, null);

        tabs.TabPages.AddRange(new TabPage[] { appsTab, generalTab, offTab, fileTab, logTab, helpTab });
        // Reload a tab each time it is shown, so it is never out of date.
        tabs.SelectedIndexChanged += delegate
        {
            if (tabs.SelectedTab == appsTab) fillApps();
            else if (tabs.SelectedTab == offTab) offBox.Text = OffAppsText();
            else if (tabs.SelectedTab == fileTab) fillFile();
            else if (tabs.SelectedTab == logTab) fillLog();
        };
        fillApps();

        form.Controls.Add(tabs);
        form.FormClosed += delegate { settingsForm = null; settingsTabs = null; pauseBox = null; };
        settingsForm = form;
        settingsTabs = tabs;
        // On a scaled screen (125%, 150%...) the text is already bigger; make the window and columns grow with it.
        float scale = form.DeviceDpi / 96f;
        if (scale > 1.01f)
        {
            form.MinimumSize = new Size((int)(form.MinimumSize.Width * scale), (int)(form.MinimumSize.Height * scale));
            form.Scale(new SizeF(scale, scale));
            foreach (ColumnHeader c in list.Columns) c.Width = (int)(c.Width * scale);
        }
        form.Show();
        form.Activate();
    }

    // The Help tab, shown in a WebBrowser control (Internet Explorer engine, so plain CSS only).
    // {FIXKEY} is replaced with the chosen fix key. Hebrew is written as entities to keep this file ASCII.
    const string HelpHtml = @"<!DOCTYPE html>
<html><head><meta charset='utf-8'><meta http-equiv='X-UA-Compatible' content='IE=edge'>
<style>
body { font-family: 'Segoe UI', sans-serif; font-size: 10pt; color: #222; margin: 14px 18px; line-height: 1.45; }
h1 { font-size: 15pt; font-weight: 600; margin: 0 0 4px; color: #1a5fb4; }
h2 { font-size: 11.5pt; font-weight: 600; margin: 22px 0 6px; padding-bottom: 3px; border-bottom: 1px solid #ddd; color: #1a5fb4; }
p { margin: 6px 0; }
ul, ol { margin: 6px 0; padding-left: 22px; }
li { margin: 3px 0; }
kbd { font-family: 'Segoe UI', sans-serif; font-size: 9pt; padding: 1px 5px; border: 1px solid #bbb; border-bottom-width: 2px; border-radius: 3px; background: #f6f6f6; white-space: nowrap; }
code { font-family: Consolas, monospace; font-size: 9.5pt; background: #f2f2f2; padding: 0 3px; white-space: nowrap; }
.he { font-family: 'Segoe UI', Arial, sans-serif; font-weight: 600; direction: rtl; unicode-bidi: embed; }
.lead { color: #555; margin-top: 0; }
.note { background: #eef5fd; border-left: 3px solid #1a5fb4; padding: 7px 10px; margin: 10px 0; }
.warn { background: #fff6e5; border-left: 3px solid #e0a000; padding: 7px 10px; margin: 10px 0; }
table { border-collapse: collapse; margin: 6px 0; width: 100%; }
td { padding: 5px 8px; border-bottom: 1px solid #eee; vertical-align: top; }
td.k { font-weight: 600; white-space: nowrap; width: 1%; }
.ex { color: #555; }
.blue { color: #1a5fb4; font-weight: 600; } .green { color: #2e8b3d; font-weight: 600; }
.purple { color: #8a3ab9; font-weight: 600; } .gray { color: #777; font-weight: 600; } .red { color: #c01c28; font-weight: 600; }
a { color: #1a5fb4; }
.shot { display: block; max-width: 100%; margin: 10px 0 4px; border: 1px solid #ccc; box-shadow: 0 1px 4px rgba(0,0,0,.15); }
</style></head><body>

<h1>AutoLangSwitcher</h1>
<p class='lead'>Remembers the keyboard language you last used in each program, and switches back to it when you return.
For example: Hebrew in WhatsApp, English in your code editor.</p>

<h2>How to use it</h2>
<p>There is nothing to set up. Just use your computer as usual:</p>
<ul>
<li>Switch the language in a program (<kbd>Alt</kbd>+<kbd>Shift</kbd> or <kbd>Win</kbd>+<kbd>Space</kbd>). AutoLangSwitcher remembers it.</li>
<li>When you come back to that program, even after a restart, its language comes back by itself.</li>
<li>To change a program's language, just switch it again. The new choice replaces the old one.</li>
</ul>
<div class='note'>It remembers by <b>program</b>, not by window or tab: all browser tabs share one language.</div>

<h2>The tray menu</h2>
{IMG:tray}
<p>Right-click the keyboard icon in the tray (next to the clock): <b>Settings...</b> opens this window (double-clicking the icon does too),
<b>Help</b> opens it on this page, <b>Pause</b> stops switching for a while, and <b>Check for updates...</b> looks for a newer version.</p>

<h2>The tabs</h2>
{IMG:apps}
<table>
<tr><td class='k'>Apps</td><td>The programs it remembers and their languages (point at a name to see its file name).
<b>Right-click</b> a program (or double-click it) to lock its language or choose the one it opens in (below), turn Smart Keyboard
Auto Switch off or on there, or forget it. Select several with <kbd>Ctrl</kbd>+click to change them together.</td></tr>
<tr><td class='k'>General</td><td>Pause, start with Windows, check for updates, Smart Keyboard Auto Switch, and whether it runs as administrator.</td></tr>
<tr><td class='k'>Smart switch off</td><td>The programs where Smart Keyboard Auto Switch stays off, one exe name per line.</td></tr>
<tr><td class='k'>Settings file</td><td>The saved list, as stored on disk.</td></tr>
<tr><td class='k'>Log</td><td>What it has been doing:
<span class='blue'>blue</span> switches you made, <span class='green'>green</span> switches it made,
<span class='purple'>purple</span> smart switch, <span class='gray'>gray</span> focus changes, <span class='red'>red</span> problems.</td></tr>
</table>

<h2>Language rules: lock, or start in a language</h2>
<p>Normally the last language you used in a program wins. To give a program a fixed language instead, right-click it on the
<b>Apps</b> tab and choose:</p>
<table>
<tr><td class='k'>Lock to</td><td>The program gets this language <b>every time</b> it comes to the front. You can still switch while you're in it,
but that switch isn't remembered: leave and come back, and it's back to the locked language.
<span class='ex'>Example: lock VS Code to English, so a Hebrew comment doesn't make it Hebrew next time.</span></td></tr>
<tr><td class='k'>Start in</td><td>The program gets this language <b>when it's opened</b> after being closed. While it runs, your switches are
remembered as usual. <span class='ex'>Example: WhatsApp always opens in Hebrew, even if you last used it in English.</span></td></tr>
<tr><td class='k'>No rule</td><td>Back to normal: the last language you used.</td></tr>
</table>
{IMG:rules}
<p>The <b>Rule</b> column on the Apps tab shows each program's rule. A program already running when AutoLangSwitcher starts
gets its start language the first time you switch to it.</p>

<h2>Smart Keyboard Auto Switch</h2>
<p class='lead'>Optional, and off unless you turn it on in the General tab.</p>
{IMG:general}
<p>Typed <code>akuo</code> when you meant <span class='he'>&#x5E9;&#x5DC;&#x5D5;&#x5DD;</span>? When you finish a word (with <kbd>Space</kbd>,
punctuation like <code>, . ? !</code>, or <kbd>Enter</kbd>), AutoLangSwitcher checks whether the same keys make a real word in another
of your keyboard languages. If they do:</p>
<ul>
<li><b>Only suggest</b> shows a notice. Press <kbd>{FIXKEY}</kbd> within 10 seconds, before typing anything else, to retype the word.</li>
<li><b>Fix automatically</b> retypes it right away. A word ended with <kbd>Enter</kbd> is fixed before the Enter goes through,
so a chat message is sent already fixed. <kbd>Shift</kbd>+<kbd>Enter</kbd> (a new line in chat apps) is left alone.</li>
</ul>
<p>Either way the keyboard switches to that language, and it is remembered for the program.</p>

<p><b>The fix key</b> (<kbd>{FIXKEY}</kbd>, you can choose another on the General tab) does three things:</p>
<ul>
<li>Fixes a suggested word.</li>
<li>Right after a fix you didn't want, puts the word back. That word then goes on the <b>Never change these words</b> list.</li>
<li>Right after a word that looks right but isn't, like <code>tbh</code> when you meant <span class='he'>&#x5D0;&#x5E0;&#x5D9;</span>, changes it to
your other keyboard language. Press it again to put it back.</li>
</ul>

<p><b>Good to know</b></p>
<ul>
<li>Short words that are real in both languages, like <code>do</code> (<span class='he'>&#x5D2;&#x5DD;</span> on the Hebrew keys), are decided
by the words around them: <span class='ex'><code>do akuo</code> becomes <span class='he'>&#x5D2;&#x5DD; &#x5E9;&#x5DC;&#x5D5;&#x5DD;</span>, while
<code>I went to akuo</code> keeps its English <code>to</code>.</span></li>
<li>Typed <code>hELLO</code> because Caps Lock was on by mistake? It is fixed to <code>Hello</code> and Caps Lock is turned off.</li>
<li>Words that aren't in any dictionary, like names, are left alone. It uses the spelling dictionaries built into Windows,
plus letter statistics for English, Hebrew, Russian, Ukrainian, Arabic, Greek, French, German, Italian and Spanish.</li>
<li>It stays off in code editors and terminals (VS Code, Visual Studio, JetBrains IDEs, Windows Terminal, PowerShell, cmd and others),
where keys can be commands and auto-complete changes the text. Change that on the <b>Smart switch off</b> tab, or by right-clicking a program on the <b>Apps</b> tab.</li>
</ul>
{IMG:off}
<div class='note'><b>Privacy:</b> to do this it has to read your typing. The letters of the current word stay in memory only until you finish it:
nothing is logged or sent, and only the never-change list is saved. Password boxes are skipped where Windows can tell they are
password boxes. While the feature is off, your typing isn't read at all.</div>

<h2>How it works</h2>
<ol>
<li>Windows tells AutoLangSwitcher each time a different program comes to the front.</li>
<li>Before leaving a program, it notes the language that program was using.</li>
<li>For the program now in front, it looks up the saved language and asks Windows to switch to it.</li>
<li>About a second later it checks that the switch worked, and asks again if it didn't. Some programs, such as WhatsApp, need the second request.</li>
<li>While you work, it checks the language a few times a second, so a change you make is remembered right away.</li>
</ol>

<h2>Administrator rights</h2>
<p>AutoLangSwitcher runs as administrator so it can also switch programs that run as administrator. Without admin rights, those programs
keep their language: use <b>Restart as administrator</b> on the General tab.</p>

<h2>Tips</h2>
<ul>
<li>A program keeps getting the wrong language? Open the Log tab right after it happens, click <b>Copy all</b>, and paste it into
<a href='https://github.com/AniDavid/AutoLangSwitcher/issues'>an issue on GitHub</a>.</li>
<li><b>Pause</b> it from the tray menu to switch languages by hand for a while.</li>
<li>While Caps Lock is on, the tray keyboard turns orange and shows a capital <b>A</b>.</li>
<li><b>Check for updates...</b> in the tray menu asks GitHub for the latest version. It also checks once a day by itself, unless you untick
that on the General tab. Only the version number is fetched; nothing is sent.</li>
</ul>
</body></html>";

    // A tab with the main control filling it and an optional button bar along the bottom.
    static TabPage MakeTab(string text, Control main, Control buttons)
    {
        TabPage tab = new TabPage(text);
        tab.Padding = new Padding(8);
        tab.UseVisualStyleBackColor = true;
        tab.Controls.Add(main);                          // Fill is added first so it docks last
        if (buttons != null) tab.Controls.Add(buttons);
        return tab;
    }

    // Colors follow the wording used by Log() in AutoLangSwitcherContext.cs.
    static Color LogColor(string line)
    {
        if (line.Contains("FAILED") || line.Contains("failed") || line.Contains("not installed")) return Color.Firebrick;
        if (line.Contains("  manual switch ") || line.Contains("  remember ")) return Color.RoyalBlue;
        if (line.Contains("  auto switch ")) return Color.ForestGreen;
        if (line.Contains("  focus ")) return Color.Gray;
        if (line.Contains("  smart switch ")) return Color.DarkMagenta;
        return SystemColors.WindowText;
    }

    static FlowLayoutPanel MakeButtonBar(params Button[] buttons)
    {
        FlowLayoutPanel bar = new FlowLayoutPanel();
        bar.Dock = DockStyle.Bottom;
        bar.AutoSize = true;
        bar.Padding = new Padding(0, 6, 0, 0);
        bar.Controls.AddRange(buttons);
        return bar;
    }

    static Button MakeButton(string text)
    {
        Button b = new Button();
        b.Text = text;
        b.AutoSize = true;
        b.UseVisualStyleBackColor = true;
        return b;
    }

    static TextBox MakeTextView()
    {
        TextBox t = new TextBox();
        t.Dock = DockStyle.Fill;
        t.Multiline = true;
        t.ReadOnly = true;
        t.WordWrap = false;
        t.ScrollBars = ScrollBars.Both;
        t.BackColor = SystemColors.Window;
        t.Font = new Font(FontFamily.GenericMonospace, 9f);
        return t;
    }
}
