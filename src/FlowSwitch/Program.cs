using FlowSwitch.Core.Diagnostics;
using FlowSwitch.Core.Ipc;
using FlowSwitch.Core.Settings;
using FlowSwitch.Diagnostics;
using FlowSwitch.Services;
using static FlowSwitch.Interop.Win32;

namespace FlowSwitch;

internal static class Program
{
    /// <summary>
    /// Arguments: <c>--startup</c> (launched by Windows at sign-in), <c>--recover</c> (restarted
    /// after a crash), <c>--restart</c> (tray → Restart). Anything else opens Settings if
    /// FlowSwitch is already running.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        // The log comes first, before anything that could fail, so even a crash in the first
        // second leaves a trace in %LOCALAPPDATA%\FlowSwitch\Logs\flowswitch.log.
        var log = new RollingFileLog(FlowSwitchPaths.HostLog);
        Log.SetSink(log);
        Log.MinimumLevel = LogLevel.Info;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            log.WriteNow($"Unhandled exception (terminating: {e.IsTerminating}): {e.ExceptionObject}");
            log.Flush(TimeSpan.FromSeconds(1));
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => log.Flush(TimeSpan.FromSeconds(1));

        string version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "?";
        Log.Info("────────────────────────────────────────────────────────────");
        Log.Info($"FlowSwitch {version} starting.");
        foreach (string line in SystemDescription.StartupBanner(args)) Log.Info(line);
        Log.Info($"Log file: {log.FilePath}");

        try
        {
            SetCurrentProcessExplicitAppUserModelID("FlowSwitch.App");
            return RunInstance(args, log);
        }
        catch (Exception ex)
        {
            log.WriteNow($"FlowSwitch failed before it could start: {ex}");
            return 1;
        }
        finally
        {
            Log.Info("FlowSwitch exiting.");
            log.Dispose();
        }
    }

    private static int RunInstance(string[] args, RollingFileLog log)
    {
        // A relaunch may start while the old instance is still shutting down: wait briefly.
        bool waitForPrevious = args.Contains("--restart") || args.Contains("--recover");
        using var mutex = new Mutex(true, IpcProtocol.InstanceMutex, out bool createdNew);
        if (!createdNew)
        {
            bool acquired = false;
            if (waitForPrevious)
            {
                try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(8)); }
                catch (AbandonedMutexException) { acquired = true; }
            }
            if (!acquired)
            {
                Log.Info("Another FlowSwitch instance is already running — " +
                         (args.Contains("--startup") || waitForPrevious ? "exiting." : "asking it to open Settings and exiting."));
                if (!args.Contains("--startup") && !waitForPrevious) SignalRunningInstance(IpcProtocol.OpenSettings);
                return 0;
            }
        }

        var store = new SettingsStore(SettingsStore.DefaultDirectory);
        using var settings = new SettingsService(store);
        var current = settings.Current;
        Log.MinimumLevel = EffectiveLogLevel(current);
        Log.Info($"Settings loaded from {store.SettingsPath}{(File.Exists(store.SettingsPath) ? string.Empty : " (file missing — defaults)")}: " +
                 $"enabled {current.General.Enabled}, mode {current.General.Mode}, Alt+Tab {current.Hotkeys.AltTab}, " +
                 $"onboarding done {current.General.OnboardingCompleted}, run elevated {current.Advanced.RunElevated}, log level {current.Advanced.LogLevel}.");
        var crash = new CrashGuard(store, log);
        crash.Install();

        try
        {
            using var app = new FlowSwitchApp(args, settings, crash);
            return app.Run();
        }
        catch (Exception ex)
        {
            crash.OnFatal(ex, "startup");
            return 1;
        }
    }

    /// <summary>While diagnostic logging is on (the default for now) everything from Info up is written.</summary>
    public static LogLevel EffectiveLogLevel(FlowSwitchSettings settings) =>
        settings.Advanced.DiagnosticLogging && settings.Advanced.LogLevel > LogLevel.Info ? LogLevel.Info : settings.Advanced.LogLevel;

    private static void SignalRunningInstance(int command)
    {
        nint window = FindWindowW(IpcProtocol.HostWindowClass, null);
        if (window == 0) return;
        uint message = RegisterWindowMessageW(IpcProtocol.CommandMessage);
        GetWindowThreadProcessId(window, out uint pid);
        AllowSetForegroundWindow(pid);
        PostMessageW(window, message, (nuint)command, 0);
    }
}
