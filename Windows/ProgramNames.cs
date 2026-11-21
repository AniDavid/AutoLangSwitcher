using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

// Friendly program names for the Apps tab ("Microsoft Word" instead of WINWORD.EXE), from each exe's file description.
// Noted when a program comes to the front and saved in program-names.txt, so they're there when it isn't running.
partial class AutoLangSwitcherContext
{
    static readonly string NamesFile = Path.Combine(DataDir, "program-names.txt");
    readonly Dictionary<string, string> programNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    void LoadNames()
    {
        try
        {
            if (!File.Exists(NamesFile)) return;
            foreach (string line in File.ReadAllLines(NamesFile, Encoding.UTF8))
            {
                int eq = line.IndexOf('=');
                if (eq > 0 && !line.StartsWith(";")) programNames[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
        }
        catch (Exception ex) { Log("could not read " + NamesFile + ": " + ex.Message); }
    }

    // A program came to the front: remember its name if it's new.
    void NoteProgramName(string exe, uint pid)
    {
        if (programNames.ContainsKey(exe)) return;
        string name = DescriptionOf(Win32.ExePath(pid));
        if (name == null) return;
        programNames[exe] = name;
        try
        {
            Directory.CreateDirectory(DataDir);
            File.AppendAllText(NamesFile, exe + " = " + name + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception ex) { Log("could not save " + NamesFile + ": " + ex.Message); }
    }

    // The name shown on the Apps tab: the saved one, else from a running copy or the App Paths registry, else the exe name.
    string DisplayName(string exe)
    {
        string name;
        if (programNames.TryGetValue(exe, out name)) return name;
        name = DescriptionOf(RunningPath(exe)) ?? DescriptionOf(AppPath(exe));
        if (name != null) programNames[exe] = name;   // for this session; saved when the program next comes to the front
        return name ?? exe;
    }

    static string DescriptionOf(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            string d = FileVersionInfo.GetVersionInfo(path).FileDescription;
            return string.IsNullOrWhiteSpace(d) ? null : d.Trim();
        }
        catch { return null; }
    }

    static string RunningPath(string exe)
    {
        foreach (Process p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
            using (p)
            {
                string path = Win32.ExePath((uint)p.Id);
                if (path != null) return path;
            }
        return null;
    }

    static string AppPath(string exe)
    {
        foreach (RegistryKey root in new RegistryKey[] { Registry.CurrentUser, Registry.LocalMachine })
            try
            {
                using (RegistryKey k = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + exe))
                    if (k != null)
                    {
                        string path = k.GetValue(null) as string;
                        if (path != null) return path.Trim('"');
                    }
            }
            catch { }
        return null;
    }
}
