using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

// "Start with Windows". Uses a scheduled task "at log on, run with highest privileges", because
// Windows does not start admin programs from the normal Run list and would show UAC at every log on.
// Older versions (named KeyboardMemory) used the Run key and later a "KeyboardMemory" task pointing
// at the old exe; both are still detected so they can be removed or migrated.
static class Startup
{
    const string TaskName = "AutoLangSwitcher";
    const string LegacyTaskName = "KeyboardMemory";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "KeyboardMemory";

    // Each check below starts schtasks.exe, so callers should avoid repeating them.
    public static bool IsEnabled
    {
        get { return HasTask || HasLegacy; }
    }

    public static bool HasTask
    {
        get { return TaskExists(TaskName); }
    }

    // Startup entry left by the old KeyboardMemory app.
    public static bool HasLegacy
    {
        get { return TaskExists(LegacyTaskName) || RunKeyExists(); }
    }

    public static void Disable()
    {
        RunSchtasks("/delete /tn " + TaskName + " /f");
        DeleteLegacy();
    }

    // Creates the scheduled task (needs admin) and removes the old entries on success.
    // Returns the schtasks exit code: 0 on success.
    public static int Enable()
    {
        int code = CreateTask();
        if (code == 0) DeleteLegacy();
        return code;
    }

    static void DeleteLegacy()
    {
        if (TaskExists(LegacyTaskName)) RunSchtasks("/delete /tn " + LegacyTaskName + " /f");
        DeleteRunKey();
    }

    static int CreateTask()
    {
        string user;
        using (WindowsIdentity id = WindowsIdentity.GetCurrent()) user = SecurityElement.Escape(id.Name);
        string exe = SecurityElement.Escape(Application.ExecutablePath);
        string xml =
"<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
"<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
"  <RegistrationInfo><Description>AutoLangSwitcher - restores each app's keyboard layout</Description></RegistrationInfo>\r\n" +
"  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + user + "</UserId></LogonTrigger></Triggers>\r\n" +
"  <Principals><Principal id=\"Author\"><UserId>" + user + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
"  <Settings>\r\n" +
"    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
"    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
"    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
"    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
"    <Priority>7</Priority>\r\n" +
"  </Settings>\r\n" +
"  <Actions Context=\"Author\"><Exec><Command>" + exe + "</Command><Arguments>--no-elevate</Arguments></Exec></Actions>\r\n" +
"</Task>\r\n";
        string tmp = Path.Combine(Path.GetTempPath(), "AutoLangSwitcherTask.xml");
        try
        {
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            return RunSchtasks("/create /tn " + TaskName + " /xml \"" + tmp + "\" /f");
        }
        catch { return -1; }
        finally { try { File.Delete(tmp); } catch { } }
    }

    static bool TaskExists(string name)
    {
        return RunSchtasks("/query /tn " + name) == 0;
    }

    static int RunSchtasks(string args)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe", args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                return p.ExitCode;
            }
        }
        catch { return -1; }
    }

    static bool RunKeyExists()
    {
        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
            return k != null && k.GetValue(RunValue) != null;
    }

    static void DeleteRunKey()
    {
        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
            if (k != null) k.DeleteValue(RunValue, false);
    }
}
