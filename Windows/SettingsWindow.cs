using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

// Settings window: everything is shown inside the app, because opening files in another program
// (Notepad, Explorer) fails from this elevated process on some Windows 11 machines.
partial class AutoLangSwitcherContext
{
    Form settingsForm;
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
        form.Size = new Size(560, 440);
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
        list.Columns.Add("Program", 260);
        list.Columns.Add("Language", 220);
        Button remove = MakeButton("Remove");
        Button removeAll = MakeButton("Remove all");
        remove.Enabled = false;
        list.SelectedIndexChanged += delegate { remove.Enabled = list.SelectedItems.Count > 0; };
        Action fillApps = delegate
        {
            list.BeginUpdate();
            list.Items.Clear();
            foreach (KeyValuePair<string, IntPtr> e in map)
                list.Items.Add(new ListViewItem(new string[] { e.Key, Win32.LayoutName(e.Value) }));
            list.EndUpdate();
            remove.Enabled = false;
        };
        remove.Click += delegate
        {
            foreach (ListViewItem item in list.SelectedItems) map.Remove(item.Text);
            SaveNow();
            fillApps();
        };
        removeAll.Click += delegate { ForgetAll(); fillApps(); };
        TabPage appsTab = MakeTab("Apps", list, MakeButtonBar(remove, removeAll));

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

        CheckBox smartBox = new CheckBox();
        smartBox.Text = "Smart Keyboard Auto Switch: fix words typed in the wrong keyboard language";
        smartBox.AutoSize = true;
        smartBox.Margin = new Padding(3, 16, 3, 0);
        smartBox.Checked = smartOn;
        Label smartNote = new Label();
        smartNote.AutoSize = true;
        smartNote.Margin = new Padding(20, 0, 3, 3);
        smartNote.ForeColor = SystemColors.GrayText;
        smartNote.Text = "Reads your typing to check each finished word. Nothing is saved or sent.";
        RadioButton suggestRadio = new RadioButton();
        suggestRadio.Text = "Only suggest (press Pause/Break to fix the word)";
        suggestRadio.AutoSize = true;
        suggestRadio.Margin = new Padding(20, 3, 3, 0);
        suggestRadio.Checked = !smartAuto;
        RadioButton autoRadio = new RadioButton();
        autoRadio.Text = "Fix automatically";
        autoRadio.AutoSize = true;
        autoRadio.Margin = new Padding(20, 0, 3, 3);
        autoRadio.Checked = smartAuto;
        suggestRadio.Enabled = autoRadio.Enabled = smartOn;
        EventHandler smartChanged = delegate
        {
            suggestRadio.Enabled = autoRadio.Enabled = smartBox.Checked;
            SetSmart(smartBox.Checked, autoRadio.Checked);
        };
        smartBox.CheckedChanged += smartChanged;
        autoRadio.CheckedChanged += smartChanged;   // fires for both radios, since one turns the other off
        general.Controls.Add(smartBox);
        general.Controls.Add(smartNote);
        general.Controls.Add(suggestRadio);
        general.Controls.Add(autoRadio);
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
        TextBox help = MakeTextView();
        help.WordWrap = true;
        help.ScrollBars = ScrollBars.Vertical;
        help.Font = SystemFonts.MessageBoxFont;
        help.Text = HelpText.Replace("\r\n", "\n").Replace("\n", "\r\n");   // source may be checked out with LF or CRLF
        TabPage helpTab = MakeTab("Help", help, null);

        tabs.TabPages.AddRange(new TabPage[] { appsTab, generalTab, fileTab, logTab, helpTab });
        // Reload a tab each time it is shown, so it is never out of date.
        tabs.SelectedIndexChanged += delegate
        {
            if (tabs.SelectedTab == appsTab) fillApps();
            else if (tabs.SelectedTab == fileTab) fillFile();
            else if (tabs.SelectedTab == logTab) fillLog();
        };
        fillApps();

        form.Controls.Add(tabs);
        form.FormClosed += delegate { settingsForm = null; pauseBox = null; };
        settingsForm = form;
        form.Show();
        form.Activate();
    }

    const string HelpText =
@"WHAT IT DOES
AutoLangSwitcher remembers the keyboard language you last used in each program and switches back to it when you return to that program. For example: Hebrew in WhatsApp, English in your code editor.

HOW TO USE IT
There is nothing to set up. Just use your computer as usual:
  - Switch the language in a program (Alt+Shift or Win+Space). AutoLangSwitcher remembers it.
  - When you come back to that program, even after a restart, its language comes back by itself.
  - To change a program's language, just switch it again. The new choice replaces the old one.

It remembers by program, not by window or tab: all browser tabs share one language.

HOW IT WORKS
  1. Windows tells AutoLangSwitcher each time a different program comes to the front.
  2. Before leaving a program, it notes the language that program was using.
  3. For the program now in front, it looks up the saved language and asks Windows to switch to it.
  4. About a second later it checks that the switch worked, and asks again if it didn't. Some programs, such as WhatsApp, need the second request.
  5. While you work, it checks the language a few times a second, so a change you make is remembered right away.

It never reads what you type, and it does not fix text typed in the wrong language.

THE TABS
  Apps: the programs it remembers. Select some and click Remove to forget them.
  General: pause it, start it with Windows, and see whether it runs as administrator.
  Settings file: the saved list, as stored on disk.
  Log: what it has been doing. Blue lines are switches you made, green lines are switches it made, red lines are problems.

SMART KEYBOARD AUTO SWITCH (optional, off unless you turn it on)
Turn it on in the General tab. When you finish a word with Space, AutoLangSwitcher checks whether you typed it in the wrong keyboard language, for example 'akuo' when you meant the Hebrew word for hello. If the same keys make a real word in another installed keyboard language:
  - 'Only suggest' shows a notice. Press Pause/Break within 10 seconds (before typing anything else) to retype the word in the right language.
  - 'Fix automatically' retypes it right away.
Either way, the keyboard switches to that language, and it is remembered for the program.
Short words that are real in both languages (like 'do', which is the Hebrew word for 'also' on the Hebrew keys) are decided by the words around them: up to 3 earlier words are changed together with a clearly wrong one, unless a word before them shows you meant the language you typed.
Changed a word you didn't want changed? Press Pause/Break right away (before typing anything else) to put it back. That word is then left alone until AutoLangSwitcher restarts.
It checks words with the spelling dictionaries built into Windows, and with letter statistics for English, Hebrew, Russian, Ukrainian, Arabic, Greek, French, German, Italian and Spanish. Only your installed keyboard languages are used. Words that aren't in any dictionary, like names, are left alone.
To do this it has to read your typing. The letters of the current word are kept in memory only until you finish it; nothing is saved, logged or sent. Password boxes are skipped where Windows can tell they are password boxes.

ADMINISTRATOR RIGHTS
AutoLangSwitcher runs as administrator so it can also switch programs that run as administrator. If it runs without admin rights, those programs keep their language. Use 'Restart as administrator' on the General tab.

TIPS
  - If a program keeps getting the wrong language, open the Log tab right after it happens, click Copy all, and paste it into an issue on GitHub:
    https://github.com/AniDavid/AutoLangSwitcher/issues
  - Pause it from the tray menu if you want to switch languages by hand for a while.
  - While Caps Lock is on, the tray keyboard turns orange and shows a capital A.";

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
