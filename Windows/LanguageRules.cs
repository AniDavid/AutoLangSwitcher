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
partial class AutoLangSwitcherContext
{
    static readonly string RulesFile = Path.Combine(DataDir, "language-rules.txt");

    class LangRule
    {
        public bool Lock;    // false: start in
        public IntPtr Hkl;
    }

    readonly Dictionary<string, LangRule> rules = new Dictionary<string, LangRule>(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, uint> lastPid = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

    // The layout a program should get when it comes to the front; IntPtr.Zero for none.
    static IntPtr Wanted(LangRule rule, bool launched, IntPtr remembered)
    {
        if (rule != null && (rule.Lock || launched)) return rule.Hkl;
        return remembered;
    }

    bool IsLocked(string exe)
    {
        LangRule r;
        return exe != null && rules.TryGetValue(exe, out r) && r.Lock;
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

    static bool IsRunning(uint pid)
    {
        Process p;
        try { p = Process.GetProcessById((int)pid); }
        catch (ArgumentException) { return false; }
        try { return !p.HasExited; }
        catch { return true; }   // no access (runs as administrator): it exists
        finally { p.Dispose(); }
    }

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
        LangRule lockHe = new LangRule(), startHe = new LangRule();
        lockHe.Lock = true;
        lockHe.Hkl = startHe.Hkl = he;
        check(Wanted(lockHe, false, en) == he, "a locked program gets its language even when another is remembered");
        check(Wanted(startHe, true, en) == he, "a 'start in' program gets its language when opened");
        check(Wanted(startHe, false, en) == en, "a 'start in' program keeps the remembered language while it runs");
        check(Wanted(null, true, en) == en && Wanted(null, false, IntPtr.Zero) == IntPtr.Zero, "no rule: the remembered language, as before");
        string exe;
        LangRule back;
        check(ParseRule(RuleLine("WhatsApp.exe", lockHe), out exe, out back) && exe == "WhatsApp.exe" && back.Lock && back.Hkl == he,
            "a rule line is read back as written");
        check(!ParseRule("x.exe = sometimes 409", out exe, out back), "a rule line with an unknown word is ignored");
    }
}
