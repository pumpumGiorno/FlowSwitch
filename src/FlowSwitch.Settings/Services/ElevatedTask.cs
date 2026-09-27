using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;

namespace FlowSwitch.Settings.Services;

/// <summary>
/// "Run with administrator rights": a scheduled task that starts FlowSwitch elevated at sign-in.
/// A task (rather than an elevated Run entry) is the only way to start elevated without a UAC
/// prompt on every sign-in. Creating or removing it needs one UAC confirmation.
/// </summary>
public static class ElevatedTask
{
    public const string TaskName = "FlowSwitch (administrator)";

    public static bool Create(string exePath)
    {
        string user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts FlowSwitch with administrator rights so Alt+Tab also works over elevated windows.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{Escape(user)}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
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
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{Escape(exePath)}</Command>
                  <Arguments>--startup</Arguments>
                  <WorkingDirectory>{Escape(Path.GetDirectoryName(exePath) ?? string.Empty)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
        string file = Path.Combine(Path.GetTempPath(), $"FlowSwitch-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode);
            return RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F", elevate: true);
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public static bool Delete() => RunSchtasks($"/Delete /TN \"{TaskName}\" /F", elevate: true) || !Exists();

    public static bool Exists() => RunSchtasks($"/Query /TN \"{TaskName}\"", elevate: false);

    /// <summary>Starts the task now (no prompt: the task already carries the elevation).</summary>
    public static bool RunNow() => RunSchtasks($"/Run /TN \"{TaskName}\"", elevate: false);

    private static bool RunSchtasks(string arguments, bool elevate)
    {
        var info = new ProcessStartInfo("schtasks.exe", arguments)
        {
            UseShellExecute = elevate,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        if (elevate) info.Verb = "runas";
        try
        {
            using var process = Process.Start(info);
            if (process is null) return false;
            process.WaitForExit(20000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            // The UAC prompt was declined.
            return false;
        }
    }

    private static string Escape(string text) => SecurityElement.Escape(text) ?? string.Empty;
}
