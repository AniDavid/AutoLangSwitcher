// AutoLangSwitcher (formerly KeyboardMemory) - remembers the keyboard layout (input language)
// you last used in each application and restores it when that application gets focus again.
//
// Build: run build.bat (uses the C# compiler that ships with Windows / .NET Framework 4.x),
//        or open AutoLangSwitcher.sln (builds both net48 and net10.0-windows).
// Data:  %APPDATA%\AutoLangSwitcher\layouts.txt (read from %APPDATA%\KeyboardMemory until the first save)
//
// Written for the C# 5 compiler in .NET Framework 4.x (no newer syntax on purpose).

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Threading;
using System.Reflection;
using System.Windows.Forms;

// The one place the version is set: shown in About, and the installer reads it from the exe.
[assembly: AssemblyVersion("1.2.0")]
[assembly: AssemblyTitle("AutoLangSwitcher")]

static class Program
{
    const string MutexName = "AutoLangSwitcher_SingleInstance_7F3A";
    const string LegacyMutexName = "KeyboardMemory_SingleInstance_7F3A";   // older KeyboardMemory.exe

    [STAThread]
    static void Main(string[] args)
    {
        bool restarted = HasArg(args, "--restarted");    // relaunched by "Restart as administrator"
        bool noElevate = HasArg(args, "--no-elevate");   // never show the admin prompt

        // Checks Smart Keyboard Auto Switch; run with the output piped, e.g. AutoLangSwitcher.exe --selftest | more
        if (HasArg(args, "--selftest"))
        {
            Environment.Exit(AutoLangSwitcherContext.SmartSelfTest(Console.Out));
            return;
        }

        // Two copies (or the old KeyboardMemory.exe and this one) would fight over the layout.
        if (IsAlreadyRunning(LegacyMutexName) || (!restarted && IsAlreadyRunning(MutexName)))
        {
            ShowAlreadyRunning();
            return;
        }

        // Ask for admin rights so layouts can also be switched in apps running as administrator.
        // If the user says No in the UAC prompt, keep running without admin rights.
        if (!Elevation.IsElevated && !noElevate && Elevation.RelaunchElevated(restarted ? "--restarted" : ""))
            return;

        bool createdNew;
        using (Mutex mutex = new Mutex(true, MutexName, out createdNew))
        {
            if (!createdNew && restarted)
            {
                // The previous instance is shutting down; wait for it.
                try { createdNew = mutex.WaitOne(10000); }
                catch (AbandonedMutexException) { createdNew = true; }
            }
            if (!createdNew)
            {
                ShowAlreadyRunning();
                return;
            }
            Application.EnableVisualStyles();
            Application.Run(new AutoLangSwitcherContext());
        }
    }

    static void ShowAlreadyRunning()
    {
        MessageBox.Show("AutoLangSwitcher (or the older Keyboard Memory) is already running - see the tray icon.",
            "AutoLangSwitcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    static bool IsAlreadyRunning(string mutexName)
    {
        try { using (Mutex.OpenExisting(mutexName)) { } return true; }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        catch (UnauthorizedAccessException) { return true; }   // exists, owned by an admin instance
        catch { return false; }
    }

    static bool HasArg(string[] args, string name)
    {
        foreach (string a in args) if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

static class Elevation
{
    // Cannot change while the process runs, so compute it once.
    public static readonly bool IsElevated = CheckElevated();

    static bool CheckElevated()
    {
        using (WindowsIdentity id = WindowsIdentity.GetCurrent())
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    // Starts a new copy with the UAC prompt. Returns false if the user declined.
    public static bool RelaunchElevated(string args)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath, args);
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            using (Process.Start(psi)) { }
            return true;
        }
        catch (Win32Exception) { return false; }   // "No" in the UAC prompt
    }
}
