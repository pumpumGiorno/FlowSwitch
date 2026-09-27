using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using FlowSwitch.Core.Diagnostics;
using FlowSwitch.Core.Ipc;

namespace FlowSwitch.Settings.Services;

/// <summary>Result of <see cref="FlowSwitchHost.EnsureRunningAsync"/>.</summary>
public sealed record HostStartResult(bool Running, string Message);

/// <summary>
/// Talks to the resident FlowSwitch process: finds its hidden window, asks for its real health
/// (<see cref="IpcProtocol.QueryStatus"/>), posts commands and starts it when it is missing.
/// </summary>
public static class FlowSwitchHost
{
    private static uint _message;
    private static readonly SemaphoreSlim StartGate = new(1, 1);

    /// <summary>Why the last start attempt failed (shown until the host runs).</summary>
    public static string? LastStartError { get; private set; }

    public static bool IsRunning => Find() != 0;

    private static uint Message => _message != 0 ? _message : _message = NativeMethods.RegisterWindowMessageW(IpcProtocol.CommandMessage);

    public static bool Send(int command, nint lParam = 0)
    {
        nint hwnd = Find();
        if (hwnd == 0) return false;
        return NativeMethods.PostMessageW(hwnd, Message, command, lParam);
    }

    /// <summary>The host's live health, or null when it is not running or does not answer within 1 s.</summary>
    public static HostStatus? QueryStatus()
    {
        nint hwnd = Find();
        if (hwnd == 0) return null;
        nint ok = NativeMethods.SendMessageTimeoutW(hwnd, Message, IpcProtocol.QueryStatus, 0,
            NativeMethods.SMTO_ABORTIFHUNG | NativeMethods.SMTO_BLOCK, 1000, out nint result);
        if (ok == 0) return null;
        var status = (HostStatus)(long)result;
        return status.HasFlag(HostStatus.Valid) ? status : null;
    }

    /// <summary>
    /// Makes sure FlowSwitch.exe runs: starts it if needed and waits until it answers over IPC.
    /// Never claims success without an answer from the resident process.
    /// </summary>
    public static async Task<HostStartResult> EnsureRunningAsync(string reason, TimeSpan? timeout = null)
    {
        await StartGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (QueryStatus() is not null)
            {
                LastStartError = null;
                return new HostStartResult(true, "FlowSwitch is running");
            }

            string? exe = FindExecutable();
            if (exe is null)
            {
                LastStartError = $"FlowSwitch.exe was not found next to the Settings app ({AppContext.BaseDirectory}).";
                Log.Error($"Cannot start FlowSwitch ({reason}): {LastStartError}");
                return new HostStartResult(false, LastStartError);
            }

            Process? process;
            try
            {
                Log.Info($"Starting {exe} ({reason}).");
                process = Process.Start(new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(exe)!,
                });
            }
            catch (Win32Exception ex)
            {
                LastStartError = $"Windows refused to start FlowSwitch.exe: {ex.Message} (error {ex.NativeErrorCode}).";
                Log.Error($"Starting FlowSwitch failed: {LastStartError}");
                return new HostStartResult(false, LastStartError);
            }
            catch (Exception ex)
            {
                LastStartError = $"Starting FlowSwitch.exe failed: {ex.Message}";
                Log.Error("Starting FlowSwitch failed", ex);
                return new HostStartResult(false, LastStartError);
            }

            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(12));
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(150).ConfigureAwait(false);
                if (QueryStatus() is not null)
                {
                    LastStartError = null;
                    Log.Info("FlowSwitch started and answered over IPC.");
                    return new HostStartResult(true, "FlowSwitch is running");
                }
                if (process is { HasExited: true })
                {
                    // Another instance may have won the race; it answers soon or not at all.
                    if (IsRunning) continue;
                    LastStartError = $"FlowSwitch.exe exited right after starting (exit code {process.ExitCode}). " +
                                     $"Details: {FlowSwitchPaths.HostLog}";
                    Log.Error(LastStartError);
                    return new HostStartResult(false, LastStartError);
                }
            }
            LastStartError = "FlowSwitch.exe was started but did not answer within 12 s. " +
                             $"Details: {FlowSwitchPaths.HostLog}";
            Log.Error(LastStartError);
            return new HostStartResult(false, LastStartError);
        }
        finally
        {
            StartGate.Release();
        }
    }

    /// <summary>Asks FlowSwitch to restart itself (or starts it) and waits for it to answer.</summary>
    public static async Task<HostStartResult> RestartAsync()
    {
        if (Send(IpcProtocol.Restart))
        {
            Log.Info("Restart requested.");
            // The old instance exits, the new one takes over the window class.
            await Task.Delay(1200).ConfigureAwait(false);
        }
        return await EnsureRunningAsync("restart").ConfigureAwait(false);
    }

    /// <summary>Opens the switcher in preview mode; starts FlowSwitch first if needed.</summary>
    public static async Task<HostStartResult> ShowPreviewAsync() => await SendStartingAsync(IpcProtocol.ShowPreview, "preview").ConfigureAwait(false);

    /// <summary>Shows the overlay with the real windows for a few seconds, without Alt+Tab.</summary>
    public static async Task<HostStartResult> TestOverlayAsync() => await SendStartingAsync(IpcProtocol.TestOverlay, "test overlay").ConfigureAwait(false);

    private static async Task<HostStartResult> SendStartingAsync(int command, string reason)
    {
        var start = await EnsureRunningAsync(reason).ConfigureAwait(false);
        if (!start.Running) return start;
        return Send(command) ? start : new HostStartResult(false, "FlowSwitch did not accept the command.");
    }

    /// <summary>Runs the host's self-test and returns its report (or an explanation why there is none).</summary>
    public static async Task<string> RunDiagnosticsAsync()
    {
        var start = await EnsureRunningAsync("diagnostics").ConfigureAwait(false);
        if (!start.Running)
        {
            return "FlowSwitch Diagnostics\n\nHost process ............... FAIL\n    " + start.Message +
                   $"\n\nThe resident app is not running, so Alt+Tab is handled by Windows.\nLog: {FlowSwitchPaths.HostLog}";
        }
        int request = Random.Shared.Next(1, int.MaxValue);
        if (!Send(IpcProtocol.RunDiagnostics, request)) return "FlowSwitch did not accept the diagnostics request.";
        string path = FlowSwitchPaths.DiagnosticsReport;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(250).ConfigureAwait(false);
            try
            {
                if (!File.Exists(path)) continue;
                string text = File.ReadAllText(path);
                string header = $"Request: {request}";
                if (text.StartsWith(header, StringComparison.Ordinal)) return text[header.Length..].TrimStart('\r', '\n');
            }
            catch (IOException)
            {
                // Being written right now.
            }
        }
        return $"FlowSwitch did not finish the self-test within 30 s. See {FlowSwitchPaths.HostLog}.";
    }

    public static string? FindExecutable()
    {
        string local = Path.Combine(AppContext.BaseDirectory, "FlowSwitch.exe");
        if (File.Exists(local)) return local;

        // Development layout: src/FlowSwitch.Settings/bin/<cfg>/<tfm>/ → src/FlowSwitch/bin/<cfg>/<tfm>/
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            string? configuration = dir.Parent?.Name;
            var src = dir.Parent?.Parent?.Parent?.Parent;
            if (src is null || configuration is null) return null;
            string bin = Path.Combine(src.FullName, "FlowSwitch", "bin", configuration);
            if (!Directory.Exists(bin)) return null;
            return Directory.GetFiles(bin, "FlowSwitch.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static nint Find() => NativeMethods.FindWindowW(IpcProtocol.HostWindowClass, null);
}
