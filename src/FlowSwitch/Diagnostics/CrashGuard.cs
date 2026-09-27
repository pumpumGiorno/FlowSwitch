using System.Diagnostics;
using FlowSwitch.Core.Diagnostics;
using FlowSwitch.Core.Settings;
using FlowSwitch.Interop;

namespace FlowSwitch.Diagnostics;

/// <summary>
/// Crash handling and crash-loop protection.
/// </summary>
/// <remarks>
/// When FlowSwitch dies, Windows removes its keyboard hook, so native Alt+Tab keeps working no
/// matter what. This class makes sure FlowSwitch comes back (and doesn't come back forever if
/// something is fundamentally broken — after three crashes in ten minutes it starts in safe mode
/// with the hook disabled until the user re-enables it).
/// </remarks>
internal sealed class CrashGuard
{
    private static readonly TimeSpan LoopWindow = TimeSpan.FromMinutes(10);
    private const int LoopThreshold = 3;

    private readonly SettingsStore _store;
    private readonly RollingFileLog? _log;
    private int _handling;

    public CrashGuard(SettingsStore store, RollingFileLog? log)
    {
        _store = store;
        _log = log;
    }

    public bool AutoRestart { get; set; } = true;

    public void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            OnFatal(e.ExceptionObject as Exception, "unhandled exception");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        // Windows Error Reporting restarts us after crashes/hangs (for processes that ran ≥ 60 s).
        try
        {
            Win32.RegisterApplicationRestart("--recover", Win32.RESTART_NO_PATCH | Win32.RESTART_NO_REBOOT);
        }
        catch
        {
            // Not critical.
        }
    }

    /// <summary>Decides at startup whether the previous runs crashed too often.</summary>
    public bool ShouldStartInSafeMode(out string? reason)
    {
        var state = _store.LoadState();
        reason = state.SafeModeReason;
        if (state.SafeMode) return true;

        var recent = state.RecentCrashes.Where(t => DateTimeOffset.UtcNow - t < LoopWindow).ToList();
        if (recent.Count >= LoopThreshold)
        {
            state.SafeMode = true;
            state.SafeModeReason = $"FlowSwitch stopped {recent.Count} times in the last {LoopWindow.TotalMinutes:0} minutes.";
            Log.Warn($"Crash loop detected: {state.SafeModeReason} Entering safe mode.");
            reason = state.SafeModeReason;
            TrySave(state);
            return true;
        }
        return false;
    }

    public void ClearSafeMode()
    {
        var state = _store.LoadState();
        state.SafeMode = false;
        state.SafeModeReason = null;
        state.RecentCrashes.Clear();
        TrySave(state);
    }

    /// <summary>Records the failure, relaunches if allowed, and terminates the process.</summary>
    public void OnFatal(Exception? ex, string what)
    {
        if (Interlocked.Exchange(ref _handling, 1) == 1) return;
        string message = $"FlowSwitch is terminating ({what}): {ex}";
        _log?.WriteNow(message);
        Log.Error(message);

        var state = _store.LoadState();
        state.RecentCrashes.Add(DateTimeOffset.UtcNow);
        state.RecentCrashes = state.RecentCrashes.Where(t => DateTimeOffset.UtcNow - t < LoopWindow).ToList();
        TrySave(state);

        bool restart = AutoRestart && state.RecentCrashes.Count < LoopThreshold;
        _log?.WriteNow($"Crash recorded ({state.RecentCrashes.Count} in the last {LoopWindow.TotalMinutes:0} min); " +
                       (restart ? "restarting." : state.RecentCrashes.Count >= LoopThreshold ? "next start will be in SAFE MODE." : "auto-restart is off."));
        _log?.Flush(TimeSpan.FromSeconds(1));
        if (restart) Relaunch("--recover");
    }

    /// <summary>The UI thread stopped responding: restart cleanly rather than keep a frozen overlay on screen.</summary>
    public void OnHang(string details)
    {
        OnFatal(new TimeoutException(details), "hang");
        Environment.FailFast("FlowSwitch overlay stopped responding: " + details);
    }

    public static void Relaunch(string arguments)
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe is null) return;
            Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Error("Relaunch failed", ex);
        }
    }

    private void TrySave(RuntimeState state)
    {
        try
        {
            _store.SaveState(state);
        }
        catch
        {
            // ignored — never fail while handling a failure
        }
    }
}
