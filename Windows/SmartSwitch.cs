using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// Smart Keyboard Auto Switch: notices a word typed with the wrong keyboard language (e.g. "akuo" when
// Hebrew was meant) and suggests the fix or makes it. Off by default; while it is off no keyboard hook
// is installed. Typed letters stay in memory until the word ends: never saved, never logged.
partial class AutoLangSwitcherContext
{
    const string SettingsKey = @"Software\AutoLangSwitcher";
    const int MinLetters = 1;
    const uint VK_BACK = 0x08, VK_PAUSE = 0x13, VK_SPACE = 0x20;

    const int LookBack = 3;   // earlier words of the same run that a fix may also change
    const int No = 0, Ambiguous = 1, Clear = 2;   // is a word typed in the wrong language?

    struct Stroke { public uint Vk, Sc; public bool Shift, AltGr, Caps; }
    struct TypedWord { public Stroke[] Keys; public IntPtr Layout; }

    bool smartOn, smartAuto;
    Win32.LowLevelProc keyProc, mouseProc;   // kept in fields so the GC does not collect them
    IntPtr keyHook = IntPtr.Zero, mouseHook = IntPtr.Zero;
    SynchronizationContext ui;
    readonly List<Stroke> word = new List<Stroke>();
    // The words before it, separated by single spaces with nothing else typed in between ("a run").
    readonly List<TypedWord> history = new List<TypedWord>();
    IntPtr wordHwnd = IntPtr.Zero;
    int keyCount = 0;   // key presses so far; a change means the user typed on after a word ended

    // The fix for the last word, waiting for Pause/Break (suggest mode), or the undo of a fix just made.
    string fixText, fixBack;
    IntPtr fixHwnd, fixLayout, fixBackLayout;
    int fixErase, fixUntil;
    bool fixIsUndo;
    // Words whose fix the user undid: left alone until the app restarts (memory only).
    readonly HashSet<string> keepWords = new HashSet<string>();

    // ---------------------------------------------------------------- Settings
    void LoadSmartSettings()
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(SettingsKey))
                if (k != null)
                {
                    smartOn = Convert.ToInt32(k.GetValue("SmartSwitch", 0)) == 1;
                    smartAuto = Convert.ToInt32(k.GetValue("SmartSwitchAuto", 0)) == 1;
                }
        }
        catch { }
        tray.BalloonTipClicked += delegate { ApplyFix(); };
        if (smartOn) StartSmart();
    }

    void SetSmart(bool on, bool auto)
    {
        smartAuto = auto;
        if (on != smartOn)
        {
            smartOn = on;
            if (on) StartSmart(); else StopSmart();
        }
        try
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(SettingsKey))
            {
                k.SetValue("SmartSwitch", smartOn ? 1 : 0, RegistryValueKind.DWord);
                k.SetValue("SmartSwitchAuto", smartAuto ? 1 : 0, RegistryValueKind.DWord);
            }
        }
        catch (Exception ex) { Log("could not save the Smart Keyboard Auto Switch setting: " + ex.Message); }
    }

    void StartSmart()
    {
        if (keyHook != IntPtr.Zero) return;
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        keyProc = OnKeyHook;
        keyHook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, keyProc, Win32.GetModuleHandle(null), 0);
        mouseProc = OnMouseHook;
        mouseHook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, mouseProc, Win32.GetModuleHandle(null), 0);
        Log(keyHook != IntPtr.Zero ? "smart switch on" :
            "smart switch FAILED to start (error " + Marshal.GetLastWin32Error() + ")");
    }

    void StopSmart()
    {
        if (keyHook == IntPtr.Zero) return;
        Win32.UnhookWindowsHookEx(keyHook);
        keyHook = IntPtr.Zero;
        if (mouseHook != IntPtr.Zero) Win32.UnhookWindowsHookEx(mouseHook);
        mouseHook = IntPtr.Zero;
        word.Clear();
        history.Clear();
        fixText = null;
        Log("smart switch off");
    }

    // ---------------------------------------------------------------- Typing
    IntPtr OnKeyHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            int msg = wParam.ToInt32();
            if (nCode >= 0 && (msg == 0x100 || msg == 0x104))   // WM_KEYDOWN, WM_SYSKEYDOWN
            {
                Win32.KBDLLHOOKSTRUCT k = (Win32.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Win32.KBDLLHOOKSTRUCT));
                if ((k.flags & Win32.LLKHF_INJECTED) == 0 && OnKey(k.vkCode, k.scanCode)) return (IntPtr)1;
            }
        }
        catch { /* never let a hook callback crash the app */ }
        return Win32.CallNextHookEx(keyHook, nCode, wParam, lParam);
    }

    // A click in the window being typed in may move the cursor: the typed words are no longer just
    // before it, so forget them and any pending fix (which would erase the wrong text). Clicks elsewhere,
    // like on the suggestion balloon, keep the fix.
    IntPtr OnMouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            int msg = wParam.ToInt32();
            if (nCode >= 0 && (msg == 0x201 || msg == 0x204 || msg == 0x207) &&   // left, right, middle button down
                Win32.RootAt(Marshal.ReadInt32(lParam, 0), Marshal.ReadInt32(lParam, 4)) == Win32.RootOf(Win32.GetForegroundWindow()))
            {
                keyCount++;   // also cancels a word check still waiting to run
                fixText = null;
                EndRun();
            }
        }
        catch { /* never let a hook callback crash the app */ }
        return Win32.CallNextHookEx(mouseHook, nCode, wParam, lParam);
    }

    // Returns true to swallow the key: only Pause/Break when it applies a suggested fix.
    bool OnKey(uint vk, uint sc)
    {
        if (vk == VK_PAUSE && fixText != null)
        {
            ui.Post(delegate { ApplyFix(); }, null);
            return true;
        }
        if (IsModifier(vk)) return false;
        keyCount++;
        fixText = null;   // typing on: the suggestion no longer applies
        IntPtr fg = Win32.GetForegroundWindow();
        bool altGr = Win32.IsDown(0xA5);   // right Alt, which Windows also reports as Ctrl+Alt
        if (paused || Win32.IsDown(0x5B) || Win32.IsDown(0x5C) || (!altGr && (Win32.IsDown(0x11) || Win32.IsDown(0x12))))
        {
            EndRun();   // paused, or a shortcut: not part of a word
            return false;
        }
        if (fg != wordHwnd)
        {
            EndRun();
            wordHwnd = fg;
        }
        if (vk == VK_BACK)
        {
            if (word.Count > 0) word.RemoveAt(word.Count - 1);
            else history.Clear();   // editing an earlier word: the run no longer matches the screen
            return false;
        }
        if (vk == VK_SPACE)
        {
            if (word.Count < MinLetters)
            {
                EndRun();   // two spaces in a row
                return false;
            }
            TypedWord w = new TypedWord();
            w.Keys = word.ToArray();
            w.Layout = Win32.LayoutOf(fg);
            TypedWord[] before = history.ToArray();
            int count = keyCount;
            ui.Post(delegate { CheckWord(w, before, fg, count); }, null);
            history.Add(w);
            if (history.Count > LookBack) history.RemoveAt(0);
            word.Clear();
            return false;
        }
        Stroke s = new Stroke();
        s.Vk = vk;
        s.Sc = sc;
        s.Shift = Win32.IsDown(0x10);
        s.AltGr = altGr;
        s.Caps = Control.IsKeyLocked(Keys.CapsLock);
        // A key belongs to the word if it is a letter in any installed layout: in English the
        // Hebrew letter tav is the comma key, for example.
        bool letter = false;
        foreach (IntPtr hkl in Win32.Layouts())
            if (char.IsLetter(CharOf(s, hkl))) { letter = true; break; }
        if (letter) word.Add(s); else EndRun();   // Enter, Tab, arrows, digits... end it without a check
        return false;
    }

    void EndRun()
    {
        word.Clear();
        history.Clear();
    }

    static bool IsModifier(uint vk)
    {
        return vk == 0x10 || vk == 0x11 || vk == 0x12 || (vk >= 0xA0 && vk <= 0xA5) ||
            vk == 0x5B || vk == 0x5C || vk == 0x14;   // Shift, Ctrl, Alt, Win, Caps Lock
    }

    // Runs on the UI thread after a word ended with Space, so the hook itself stays fast.
    // `before`: the earlier words of the same run, oldest first.
    void CheckWord(TypedWord current, TypedWord[] before, IntPtr hwnd, int count)
    {
        if (!smartOn || count != keyCount || Win32.GetForegroundWindow() != hwnd || Win32.IsPasswordBox(hwnd)) return;
        IntPtr layout = current.Layout;
        string lang = LangOf(layout);
        // The end of the run, current word last. A word typed with another layout, or one whose fix
        // the user undid, cuts it.
        List<Stroke[]> run = new List<Stroke[]>();
        List<string> typedWords = new List<string>();
        List<TypedWord> all = new List<TypedWord>(before);
        all.Add(current);
        foreach (TypedWord w in all)
        {
            string t = w.Layout == layout ? TextOf(w.Keys, layout) : null;
            if (t == null || keepWords.Contains(t)) { run.Clear(); typedWords.Clear(); continue; }
            run.Add(w.Keys);
            typedWords.Add(t);
        }
        if (run.Count == 0) return;   // the current word itself can't be checked
        string[] typed = typedWords.ToArray();
        foreach (IntPtr other in Win32.Layouts())
        {
            string olang = LangOf(other);
            if (olang == lang) continue;
            string[] meant = new string[run.Count];
            for (int i = 0; i < run.Count; i++) meant[i] = TextOf(run[i], other);
            int first = FixStart(typed, lang, meant, olang);
            if (first < 0) continue;

            string typedRun = string.Join(" ", typed, first, typed.Length - first);
            string meantRun = string.Join(" ", meant, first, meant.Length - first);
            int words = typed.Length - first;
            Log("smart switch " + curExe + ": last " + (words == 1 ? "word" : words + " words") + " looked like " +
                Win32.LayoutName(other) + (smartAuto ? ", fixed" : ", suggested (Pause/Break)"));
            fixText = meantRun + " ";
            fixBack = typedRun + " ";
            fixErase = typedRun.Length + 1;   // the words and the space after them
            fixHwnd = hwnd;
            fixLayout = other;
            fixBackLayout = layout;
            fixIsUndo = false;
            fixUntil = Environment.TickCount + 10000;
            if (smartAuto) ApplyFix();
            else tray.ShowBalloonTip(10000, Title, "Typed in the wrong language? Press Pause/Break to change the last " +
                (words == 1 ? "word" : words + " words") + " to " + Win32.LayoutName(other) + ".", ToolTipIcon.Info);
            return;
        }
    }

    void ApplyFix()
    {
        string text = fixText;
        fixText = null;
        if (text == null || Elapsed(fixUntil) || Win32.GetForegroundWindow() != fixHwnd) return;
        int error;
        Win32.RequestLayout(fixHwnd, fixLayout, out error);   // Poll then remembers it like a manual switch
        Win32.Retype(fixErase, text);
        history.Clear();   // the screen now differs from what was typed
        if (fixIsUndo)
        {
            foreach (string w in text.TrimEnd().Split(' ')) keepWords.Add(w);
            Log("smart switch " + curExe + ": fix undone, those words are left alone from now on");
            return;
        }
        // Pause/Break again, before typing anything else, puts the word back.
        fixIsUndo = true;
        fixErase = text.Length;
        fixText = fixBack;
        IntPtr fixedLayout = fixLayout;
        fixLayout = fixBackLayout;
        fixBackLayout = fixedLayout;
        fixUntil = Environment.TickCount + 10000;
    }

    // ---------------------------------------------------------------- Deciding
    // A run of words, current word last, as typed and as the same keys give in another layout.
    // Returns the index of the first word to change, or -1 to leave the run alone.
    //  - A word that is gibberish here but a real word there ("akuo" / shalom) is Clear.
    //  - A real word in both languages ("do" / gam) is Ambiguous: it is changed only together with a
    //    Clear word next to it, and not when a word that is real only here comes before it
    //    ("I went to akuo": "to" stays English, like "went").
    static int FixStart(string[] typed, string lang, string[] meant, string otherLang)
    {
        int last = typed.Length - 1;
        int verdict = Judge(typed[last], lang, meant[last], otherLang);
        if (verdict == No) return -1;
        int first = last, firstClear = verdict == Clear ? last : -1;
        bool stoppedByWord = false;
        for (int i = last - 1; i >= 0; i--)
        {
            int v = Judge(typed[i], lang, meant[i], otherLang);
            if (v == No) { stoppedByWord = true; break; }
            first = i;
            if (v == Clear) firstClear = i;
        }
        if (firstClear < 0) return -1;   // only ambiguous words: no evidence
        return stoppedByWord ? firstClear : first;
    }

    static bool IsWrongLanguage(string typed, string lang, string meant, string otherLang)
    {
        return Judge(typed, lang, meant, otherLang) == Clear;
    }

    // typed: the word as it came out; meant: the same keys in another layout.
    static int Judge(string typed, string lang, string meant, string otherLang)
    {
        if (typed == null || meant == null) return No;
        foreach (char c in meant) if (!char.IsLetter(c)) return No;
        string bare = typed.Trim(',', '.', ';', '\'', '"', '/', '[', ']', '`');
        if (IsWord(otherLang, meant) != true) return No;
        bool? here = IsWord(lang, bare);
        if (here == true) return Ambiguous;
        if (here == null) return No;
        // Second opinion: a typed word that still looks normal for its language is probably a name
        // or slang missing from the dictionary, so leave it alone.
        // (Not for 1-2 letters, where letter statistics say little.)
        string a = TwoLetter(lang), b = TwoLetter(otherLang);
        bool looksNormal = bare.Length >= 3 && LetterStats.Has(a) && LetterStats.Has(b) &&
            LetterStats.Margin(b, meant) >= LetterStats.Margin(a, bare);
        return looksNormal ? No : Clear;
    }

    // Acronyms and chat abbreviations people type in lower case, and "i". Windows' dictionary doesn't know
    // most of them, and letter statistics can't tell them from gibberish ("cmd" looks no more English than "akt").
    static readonly HashSet<string> Abbreviations = new HashSet<string>(
        ("i ok js ts ai ui ux vs tv pc os ip id pm am db hr pr qa eu uk " +
         "api sdk cli gui url uri http https html css json xml yaml sql cmd exe dll msi pdf docx xlsx pptx csv txt " +
         "zip usb ssd hdd cpu gpu ram rom ios wifi vpn dns tcp udp ssh ftp smtp git npm nuget sudo bash apt dev prod " +
         "ceo cto cfo faq diy asap btw fyi imo imho idk lol omg thx pls plz brb afk tbh etc aka").Split(' '));

    // Real 1-2 letter words, for languages whose dictionary accepts almost any one or two letters
    // (Windows' Hebrew dictionary accepts single letters). Longer words go to the dictionary.
    static readonly Dictionary<string, HashSet<string>> ShortWords = new Dictionary<string, HashSet<string>>
    {
        { "en", new HashSet<string>(("a i ad ah am an as at be by do eh ex go ha he hi hm if in is it me mr ms my no of oh " +
            "ok on or ox pi so to uh um up us we ya ye yo").Split(' ')) },
        { "he", new HashSet<string>("\u05E9\u05DC \u05D0\u05EA \u05D6\u05D4 \u05D6\u05D5 \u05DC\u05D0 \u05DB\u05DF \u05DE\u05D4 \u05DE\u05D9 \u05E2\u05DC \u05D0\u05DD \u05D2\u05DD \u05E8\u05E7 \u05E2\u05DD \u05D9\u05E9 \u05D0\u05D6 \u05D3\u05D9 \u05D4\u05DD \u05D4\u05DF \u05DC\u05D9 \u05DC\u05D5 \u05DC\u05DA \u05DC\u05D4 \u05D1\u05D5 \u05D1\u05D4 \u05D1\u05D9 \u05E4\u05D4 \u05E9\u05DD \u05DB\u05DC \u05D0\u05DA \u05DB\u05D9 \u05E0\u05D0 \u05D0\u05DC \u05D0\u05E3 \u05E2\u05D3 \u05D0\u05D9 \u05D0\u05D5 \u05D1\u05DF \u05D1\u05EA \u05D0\u05D1 \u05D0\u05D7 \u05D9\u05D3 \u05E4\u05D9 \u05D7\u05D9 \u05E7\u05D5 \u05E6\u05D5 \u05D2\u05DF \u05D3\u05D2 \u05D7\u05D2 \u05E2\u05E5 \u05D2\u05D1 \u05D7\u05DD \u05E7\u05E8 \u05E8\u05E2 \u05E2\u05D6 \u05D0\u05E9 \u05D9\u05DD \u05D7\u05DF \u05D2\u05DC \u05E8\u05D1 \u05D4\u05E8 \u05DB\u05E3 \u05D3\u05E3 \u05E1\u05DC \u05D2\u05E8 \u05DE\u05E1 \u05EA\u05DF \u05E7\u05D7 \u05E9\u05D1 \u05E8\u05D3 \u05E6\u05D0 \u05D1\u05D0 \u05D6\u05D6 \u05E0\u05E2 \u05DB\u05DA \u05D0\u05D4 \u05D3\u05DD \u05E9\u05DF \u05DC\u05D1 \u05E2\u05D8 \u05E6\u05D1 \u05EA\u05D4 \u05E1\u05D1".Split(' ')) },
    };

    // True or false, or null when there is neither a Windows dictionary nor letter statistics.
    static bool? IsWord(string lang, string w)
    {
        if (w.Length == 0) return false;
        foreach (char c in w) if (!char.IsLetter(c)) return false;
        if (Abbreviations.Contains(w.ToLowerInvariant())) return true;
        HashSet<string> shortWords;
        if (ShortWords.TryGetValue(TwoLetter(lang), out shortWords) && w.Length <= MaxLength(shortWords))
            return shortWords.Contains(w.ToLowerInvariant());
        bool? dict = Speller.Check(lang, w);
        // Dictionaries list acronyms and names only with their capitals: "API", "London".
        // Not for 1-2 letters: almost every pair of capitals is some code ("AK" is Alaska).
        if (dict == false && w.Length >= 3 && Speller.KnownWithOtherCase(lang, w)) dict = true;
        if (dict != null) return dict;
        string two = TwoLetter(lang);
        if (LetterStats.Has(two)) return LetterStats.Margin(two, w) <= 0;
        return null;
    }

    static int MaxLength(HashSet<string> words)
    {
        int max = 0;
        foreach (string w in words) max = Math.Max(max, w.Length);
        return max;
    }

    static char CharOf(Stroke s, IntPtr hkl)
    {
        return Win32.CharOf(s.Vk, s.Sc, s.Shift, s.AltGr, s.Caps, hkl);
    }

    static string TextOf(Stroke[] keys, IntPtr hkl)
    {
        char[] text = new char[keys.Length];
        for (int i = 0; i < keys.Length; i++)
            if ((text[i] = CharOf(keys[i], hkl)) == '\0') return null;
        return new string(text);
    }

    static string LangOf(IntPtr hkl)
    {
        try { return CultureInfo.GetCultureInfo((int)(hkl.ToInt64() & 0xFFFF)).Name; }
        catch { return ""; }
    }

    static string TwoLetter(string lang)
    {
        int dash = lang.IndexOf('-');
        return dash > 0 ? lang.Substring(0, dash) : lang;
    }

    // ---------------------------------------------------------------- Self-test (AutoLangSwitcher.exe --selftest)
    public static int SmartSelfTest(TextWriter o)
    {
        int fails = 0;
        Action<bool, string> check = delegate(bool ok, string what)
        {
            o.WriteLine((ok ? "ok    " : "FAIL  ") + what);
            if (!ok) fails++;
        };

        string shalom = new string(new char[] { (char)0x5E9, (char)0x5DC, (char)0x5D5, (char)0x5DD });
        if (Speller.Check("en-US", "hello") != null)
        {
            check(Speller.Check("en-US", "hello") == true, "English dictionary knows 'hello'");
            check(Speller.Check("en-US", "akuo") == false, "English dictionary rejects 'akuo'");
        }
        if (Speller.Check("he-IL", shalom) != null)
            check(Speller.Check("he-IL", shalom) == true, "Hebrew dictionary knows shalom");

        foreach (string lang in LetterStats.Languages)
        {
            int good = 0;
            string[] samples = LetterStats.Samples(lang);
            foreach (string w in samples) if (LetterStats.Margin(lang, w) <= 0) good++;
            check(good >= samples.Length * 9 / 10, lang + " letter statistics accept " + good + "/" + samples.Length + " common words");
        }

        IntPtr en = FindLayout("en"), he = FindLayout("he");
        if (en == IntPtr.Zero || he == IntPtr.Zero)
        {
            o.WriteLine("skip  English/Hebrew typing tests: both keyboard layouts must be installed");
            return fails;
        }
        Stroke[] akuo = KeysOf("akuo", en);
        check(TextOf(akuo, he) == shalom, "keys a-k-u-o on the Hebrew layout give shalom");
        check(IsWrongLanguage("akuo", LangOf(en), shalom, LangOf(he)), "'akuo' is detected as Hebrew typed on English");
        check(IsWrongLanguage(TextOf(KeysOf("i", en), he), LangOf(he), "i", LangOf(en)), "'i' typed on the Hebrew keyboard is caught");
        foreach (string w in new string[] { "hello", "api", "london", "json", "cmd" })
            check(!IsWrongLanguage(w, LangOf(en), TextOf(KeysOf(w, en), he), LangOf(he)), "'" + w + "' is left alone");
        // Runs of words: { words as meant in Hebrew/English, expected first word to change (-1: none) }
        RunCheck(check, en, he, "do akuo", 0, "'do akuo': 'do' (gam) is changed together with shalom");
        RunCheck(check, en, he, "went to akuo", 2, "'went to akuo': 'to' stays English like 'went'");
        RunCheck(check, en, he, "akuo do", 0, "'akuo do': ambiguous 'do' after shalom is changed with it");
        RunCheck(check, en, he, "to do so", -1, "'to do so': all ambiguous, left alone");
        RunCheck(check, en, he, "hello world", -1, "'hello world' is left alone");
        CrossCheck(o, check, "he", he, en);
        CrossCheck(o, check, "en", en, he);
        return fails;
    }

    // `text` is typed on `typedHkl`; FixStart must say which word the fix starts at.
    static void RunCheck(Action<bool, string> check, IntPtr typedHkl, IntPtr otherHkl, string text, int expected, string what)
    {
        string[] typed = text.Split(' ');
        string[] meant = new string[typed.Length];
        for (int i = 0; i < typed.Length; i++) meant[i] = TextOf(KeysOf(typed[i], typedHkl), otherHkl);
        check(FixStart(typed, LangOf(typedHkl), meant, LangOf(otherHkl)) == expected, what);
    }

    // Common words of `lang` typed with the other layout must be detected.
    static void CrossCheck(TextWriter o, Action<bool, string> check, string lang, IntPtr meantHkl, IntPtr typedHkl)
    {
        int found = 0, total = 0;
        foreach (string w in LetterStats.Samples(lang))
        {
            Stroke[] keys = KeysOf(w, meantHkl);
            if (keys == null) continue;
            total++;
            if (IsWrongLanguage(TextOf(keys, typedHkl), LangOf(typedHkl), w, LangOf(meantHkl))) found++;
        }
        check(found >= total * 7 / 10, lang + " words typed on the wrong layout detected: " + found + "/" + total);
    }

    static IntPtr FindLayout(string twoLetter)
    {
        foreach (IntPtr hkl in Win32.Layouts())
            if (TwoLetter(LangOf(hkl)) == twoLetter) return hkl;
        return IntPtr.Zero;
    }

    static Stroke[] KeysOf(string text, IntPtr hkl)
    {
        Stroke[] keys = new Stroke[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            short r = Win32.VkKeyScanEx(text[i], hkl);
            if (r == -1) return null;
            keys[i].Vk = (uint)(r & 0xFF);
            keys[i].Sc = Win32.MapVirtualKeyEx(keys[i].Vk, 0, hkl);
            keys[i].Shift = (r & 0x100) != 0;
            keys[i].AltGr = (r & 0x600) == 0x600;
        }
        return keys;
    }
}
