using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace RioUninstaller.Services;

/// <summary>
/// Avoids the UAC prompt on every launch: a scheduled task (registered once, elevated) starts the app
/// with admin rights on demand. Falls back to a normal UAC prompt if the task isn't there.
/// </summary>
public static class Elevation
{
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);

    public static bool IsAdmin
    {
        get
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }
    }

    static string ExePath => Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";

    static string TaskName
    {
        get
        {
            var u = new string((Environment.UserName ?? "user").Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-').ToArray());
            return "RioUninstaller-" + (u.Length == 0 ? "user" : u);
        }
    }

    static (int code, string output) Run(string file, string args, int timeoutMs = 15000)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p == null) return (-1, "");
            var outTask = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return (-1, ""); }
            return (p.ExitCode, outTask.Result);
        }
        catch { return (-1, ""); }
    }

    /// <summary>True if our task exists and points at this exe.</summary>
    public static bool TaskIsValid()
    {
        var (code, xml) = Run("schtasks.exe", $"/Query /TN \"{TaskName}\" /XML");
        if (code != 0 || string.IsNullOrEmpty(xml)) return false;
        return xml.Contains(SecurityElement.Escape(ExePath), StringComparison.OrdinalIgnoreCase)
            || xml.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Non-admin start: ask the scheduled task to launch us elevated (no UAC).</summary>
    public static bool TryRunTask()
    {
        if (!TaskIsValid()) return false;
        try { AllowSetForegroundWindow(-1); } catch { }
        return Run("schtasks.exe", $"/Run /TN \"{TaskName}\"").code == 0;
    }

    public static bool RelaunchAsAdmin()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ExePath, "--elevated") { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch { return false; }   // user declined
    }

    public static void EnsureTask()
    {
        try { if (!TaskIsValid()) RegisterTask(); } catch { }
    }

    public static void RegisterTask()
    {
        if (!IsAdmin) return;
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? "";
        string xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>Starts Rio Uninstaller with admin rights without a UAC prompt.</Description>
  </RegistrationInfo>
  <Principals>
    <Principal id=""Author"">
      <UserId>{SecurityElement.Escape(sid)}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>4</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>""{SecurityElement.Escape(ExePath)}""</Command>
      <Arguments>--elevated</Arguments>
    </Exec>
  </Actions>
</Task>";
        var tmp = Path.Combine(Path.GetTempPath(), "rio-task-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            Run("schtasks.exe", $"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F");
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    public static void UnregisterTask() => Run("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F");
}
