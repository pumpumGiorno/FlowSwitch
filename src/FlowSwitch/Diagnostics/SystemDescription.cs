using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace FlowSwitch.Diagnostics;

/// <summary>Facts about the machine and this process, for the startup banner and the self-test.</summary>
internal static class SystemDescription
{
    /// <summary>"Windows 11 23H2, build 22631.4317" from the registry (Environment.OSVersion lacks the release name).</summary>
    public static string WindowsDisplayVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string build = key?.GetValue("CurrentBuildNumber") as string ?? Environment.OSVersion.Version.Build.ToString();
            string release = key?.GetValue("DisplayVersion") as string ?? key?.GetValue("ReleaseId") as string ?? "?";
            object? ubr = key?.GetValue("UBR");
            string product = int.TryParse(build, out int b) && b >= 22000 ? "Windows 11" : "Windows 10";
            return $"{product} {release}, build {build}{(ubr is int u ? "." + u : string.Empty)}";
        }
        catch
        {
            return Environment.OSVersion.VersionString;
        }
    }

    public static bool IsElevated
    {
        get
        {
            try
            {
                return Environment.IsPrivilegedProcess;
            }
            catch
            {
                return false;
            }
        }
    }

    public static IEnumerable<string> StartupBanner(string[] args)
    {
        yield return $"Executable: {Environment.ProcessPath} (pid {Environment.ProcessId}, {RuntimeInformation.ProcessArchitecture}, session {System.Diagnostics.Process.GetCurrentProcess().SessionId})";
        yield return $"Base directory: {AppContext.BaseDirectory}";
        yield return $"Working directory: {Environment.CurrentDirectory}";
        yield return $"Arguments: {(args.Length == 0 ? "(none)" : string.Join(' ', args))}";
        yield return $"Windows version: {WindowsDisplayVersion()} ({Environment.OSVersion.Version}, {RuntimeInformation.OSArchitecture})";
        yield return $".NET runtime: {RuntimeInformation.FrameworkDescription}, {(IsSelfContained() ? "self-contained" : "framework-dependent")}";
        yield return $"Process elevated: {(IsElevated ? "Yes" : "No")}";
        yield return $"User: {Environment.UserDomainName}\\{Environment.UserName}";
    }

    private static bool IsSelfContained() =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "coreclr.dll"));
}
