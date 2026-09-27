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
        SetCurrentProcessExplicitAppUserModelID("FlowSwitch.App");

        string localData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlowSwitch");
        var log = new FileLogSink(Path.Combine(localData, "logs"));
        Log.SetSink(log);

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
                if (!args.Contains("--startup") && !waitForPrevious) SignalRunningInstance(IpcProtocol.OpenSettings);
                log.Dispose();
                return 0;
            }
        }

        var store = new SettingsStore(SettingsStore.DefaultDirectory);
        using var settings = new SettingsService(store);
        Log.MinimumLevel = settings.Current.Advanced.LogLevel;
        var crash = new CrashGuard(store, log);
        crash.Install();

        Log.Info($"FlowSwitch {typeof(Program).Assembly.GetName().Version} starting on {Environment.OSVersion}.");
        try
        {
            using var app = new FlowSwitchApp(args, settings, crash, localData);
            return app.Run();
        }
        catch (Exception ex)
        {
            crash.OnFatal(ex, "startup");
            return 1;
        }
        finally
        {
            Log.Info("FlowSwitch exiting.");
            log.Dispose();
        }
    }

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
