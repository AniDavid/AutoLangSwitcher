using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

// Language rules, set per program on the Apps tab:
//   Lock to X:  the program gets X every time it comes to the front. Switching inside it is allowed but never remembered.
//   Start in X: the program gets X when it is opened (after being closed); while it runs, switches are remembered as usual.
// Saved in language-rules.txt, one "program.exe = lock|start layout-id ; language" per line.

class LangRule
{
    public bool Lock;    // false: start in
    public IntPtr Hkl;
}

// What each program's language should be: the remembered layouts, the rules, and which process was last seen per
// program. No windows or Windows calls here, so the self-test can play whole sequences through it.
class LanguageMemory
{
    // exe -> layout. Sorted so the settings file and the menu list apps alphabetically.
    public readonly SortedDictionary<string, IntPtr> Map = new SortedDictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, LangRule> Rules = new Dictionary<string, LangRule>(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, uint> lastPid = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
    public Func<uint, bool> IsRunning = ProcessIsRunning;   // replaced in the self-test

    // A program came to the front (pid: its process). Returns the layout it should get, or IntPtr.Zero for none;
    // why is " (locked)" or " (start language)" when a rule decided it, for the log.
    public IntPtr Focus(string exe, uint pid, out string why)
    {
        IntPtr remembered;
        Map.TryGetValue(exe, out remembered);
        LangRule rule;
        Rules.TryGetValue(exe, out rule);
        IntPtr wanted = Wanted(rule, JustLaunched(exe, pid), remembered);
        why = rule == null || (wanted == remembered && !rule.Lock) ? "" : rule.Lock ? " (locked)" : " (start language)";
        return wanted;
    }

    // The language in use in a program. Returns false if nothing changed: same as remembered, or the program is locked
    // (a switch inside a locked program is never remembered). old is the layout it had, or IntPtr.Zero if new.
    public bool Record(string exe, IntPtr hkl, out IntPtr old)
    {
        bool known = Map.TryGetValue(exe, out old);
        if (IsLocked(exe) || (known && old == hkl)) return false;
        Map[exe] = hkl;
        return true;
    }

    public bool IsLocked(string exe)
    {
        LangRule r;
        return exe != null && Rules.TryGetValue(exe, out r) && r.Lock;
    }

    public static IntPtr Wanted(LangRule rule, bool launched, IntPtr remembered)
    {
        if (rule != null && (rule.Lock || launched)) return rule.Hkl;
        return remembered;
    }

    // Was this program opened since it was last in front? The first time a program is seen counts as opened,
    // so a program already running when AutoLangSwitcher starts gets its start language once.
    bool JustLaunched(string exe, uint pid)
    {
        uint last;
        bool seen = lastPid.TryGetValue(exe, out last);
        lastPid[exe] = pid;
        if (!seen) return true;
        return last != pid && !IsRunning(last);   // another window of a copy still running isn't a new start
    }

    static bool ProcessIsRunning(uint pid)
    {
        Process p;
        try { p = Process.GetProcessById((int)pid); }
        catch (ArgumentException) { return false; }
        try { return !p.HasExited; }
        catch { return true; }   // no access (runs as administrator): it exists
        finally { p.Dispose(); }
    }
}

partial class AutoLangSwitcherContext
{
    static readonly string RulesFile = Path.Combine(DataDir, "language-rules.txt");

    readonly LanguageMemory memory = new LanguageMemory();
    SortedDictionary<string, IntPtr> map { get { return memory.Map; } }
    Dictionary<string, LangRule> rules { get { return memory.Rules; } }

    string RuleText(string exe)
    {
        LangRule r;
        if (!rules.TryGetValue(exe, out r)) return "";
        return (r.Lock ? "Locked: " : "Starts in: ") + Win32.LayoutName(r.Hkl);
    }

    // Sets (or with rule null, removes) the rule for these programs and saves.
    void SetRule(IEnumerable<string> exes, LangRule rule)
    {
        foreach (string exe in exes)
        {
            if (rule == null) rules.Remove(exe);
            else rules[exe] = rule;
            Log("language rule " + exe + ": " + (rule == null ? "none" : RuleText(exe)));
        }
        SaveRules();
    }

    void LoadRules()
    {
        try
        {
            if (!File.Exists(RulesFile)) return;
            foreach (string line in File.ReadAllLines(RulesFile, Encoding.UTF8))
            {
                string exe;
                LangRule r;
                if (ParseRule(line, out exe, out r)) rules[exe] = r;
            }
        }
        catch (Exception ex) { Log("could not read " + RulesFile + ": " + ex.Message); }
    }

    void SaveRules()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("; AutoLangSwitcher - language rules, set on the Apps tab.");
            sb.AppendLine("; program.exe = lock layout-id (always this language) or start layout-id (this language when opened)");
            foreach (KeyValuePair<string, LangRule> e in rules) sb.AppendLine(RuleLine(e.Key, e.Value));
            File.WriteAllText(RulesFile, sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex) { Log("could not save " + RulesFile + ": " + ex.Message); }
    }

    static string RuleLine(string exe, LangRule r)
    {
        return exe + " = " + (r.Lock ? "lock " : "start ") + r.Hkl.ToInt64().ToString("X") + " ; " + Win32.LayoutName(r.Hkl);
    }

    static bool ParseRule(string line, out string exe, out LangRule rule)
    {
        exe = null;
        rule = null;
        int semi = line.IndexOf(';');
        if (semi >= 0) line = line.Substring(0, semi);
        int eq = line.IndexOf('=');
        if (eq <= 0) return false;
        exe = line.Substring(0, eq).Trim();
        string[] parts = line.Substring(eq + 1).Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        long v;
        if (exe.Length == 0 || parts.Length != 2 || (parts[0] != "lock" && parts[0] != "start") ||
            !long.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v) || v == 0) return false;
        rule = new LangRule();
        rule.Lock = parts[0] == "lock";
        rule.Hkl = new IntPtr(v);
        return true;
    }

    static void RuleSelfTest(Action<bool, string> check)
    {
        IntPtr en = new IntPtr(0x04090409), he = new IntPtr(0x040D040D);
        LangRule lockEn = new LangRule(), startHe = new LangRule();
        lockEn.Lock = true;
        lockEn.Hkl = en;
        startHe.Hkl = he;

        check(LanguageMemory.Wanted(lockEn, false, he) == en, "a locked program gets its language even when another is remembered");
        check(LanguageMemory.Wanted(startHe, false, en) == en, "a 'start in' program keeps the remembered language while it runs");
        check(LanguageMemory.Wanted(null, true, en) == en, "no rule: the remembered language, as before");

        // Whole sequences, with made-up processes: running holds the process ids that are still open.
        HashSet<uint> running = new HashSet<uint>();
        LanguageMemory m = new LanguageMemory();
        m.IsRunning = delegate(uint pid) { return running.Contains(pid); };
        string why;
        IntPtr old;

        // No rule: a new program isn't switched; what it uses is remembered and comes back.
        running.Add(10);
        check(m.Focus("notepad.exe", 10, out why) == IntPtr.Zero, "sequence: a new program without a rule isn't switched");
        check(m.Record("notepad.exe", he, out old) && old == IntPtr.Zero, "sequence: its language is remembered");
        check(m.Focus("notepad.exe", 10, out why) == he && why == "", "sequence: and comes back when it returns to the front");
        check(!m.Record("notepad.exe", he, out old), "sequence: the same language again is not a change");

        // Lock: a switch inside is not remembered, and the locked language comes back.
        m.Map["code.exe"] = he;
        m.Rules["code.exe"] = lockEn;
        running.Add(20);
        check(m.Focus("code.exe", 20, out why) == en && why == " (locked)", "sequence: a locked program gets its language");
        check(!m.Record("code.exe", he, out old), "sequence: a switch inside a locked program is not remembered");
        check(m.Focus("code.exe", 20, out why) == en, "sequence: back in front, the locked language again");
        check(m.Map["code.exe"] == he, "sequence: the remembered language of a locked program is left as it was");

        // Start in: opened -> start language; switches while running are remembered; reopened -> start language again.
        m.Rules["whatsapp.exe"] = startHe;
        running.Add(30);
        check(m.Focus("whatsapp.exe", 30, out why) == he && why == " (start language)", "sequence: a 'start in' program opens in its language");
        check(m.Record("whatsapp.exe", en, out old), "sequence: a switch while it runs is remembered");
        check(m.Focus("whatsapp.exe", 30, out why) == en && why == "", "sequence: back in front while still running, the remembered language");
        running.Add(31);
        check(m.Focus("whatsapp.exe", 31, out why) == en, "sequence: a second window of a copy still running keeps the remembered language");
        running.Remove(30);
        running.Remove(31);
        running.Add(32);
        check(m.Focus("whatsapp.exe", 32, out why) == he, "sequence: closed and opened again, the start language");

        string exe;
        LangRule back;
        check(ParseRule(RuleLine("WhatsApp.exe", lockEn), out exe, out back) && exe == "WhatsApp.exe" && back.Lock && back.Hkl == en,
            "a rule line is read back as written");
        check(!ParseRule("x.exe = sometimes 409", out exe, out back), "a rule line with an unknown word is ignored");
    }
}
