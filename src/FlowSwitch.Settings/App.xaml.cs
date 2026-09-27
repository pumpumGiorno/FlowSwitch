using System.Threading;
using System.Windows;
using System.Windows.Threading;
using FlowSwitch.Core.Diagnostics;
using FlowSwitch.Core.Ipc;
using FlowSwitch.Core.Settings;
using FlowSwitch.Settings.Services;

namespace FlowSwitch.Settings;

public partial class App : Application
{
    private const string ActivateEventName = @"Local\FlowSwitch.Settings.Activate";
    private const string OnboardingEventName = @"Local\FlowSwitch.Settings.Onboarding";

    private Mutex? _instance;
    private EventWaitHandle? _activate;
    private EventWaitHandle? _onboarding;
    private RegisteredWaitHandle? _activateWait;
    private RegisteredWaitHandle? _onboardingWait;
    private SettingsModel? _model;
    private MainWindow? _main;
    private OnboardingWindow? _onboardingWindow;

    public SettingsModel Model => _model ?? throw new InvalidOperationException("Settings are not loaded yet.");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool onboarding = e.Args.Any(a => string.Equals(a, "--onboarding", StringComparison.OrdinalIgnoreCase));

        _instance = new Mutex(initiallyOwned: true, IpcProtocol.SettingsInstanceMutex, out bool created);
        if (!created)
        {
            // Already open: bring that window forward instead of opening a second one.
            SignalExisting(onboarding ? OnboardingEventName : ActivateEventName);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        _model = new SettingsModel(new SettingsStore(SettingsStore.DefaultDirectory));

        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _onboarding = new EventWaitHandle(false, EventResetMode.AutoReset, OnboardingEventName);
        _activateWait = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => Dispatcher.BeginInvoke(ShowMain), null, -1, false);
        _onboardingWait = ThreadPool.RegisterWaitForSingleObject(_onboarding, (_, _) => Dispatcher.BeginInvoke(ShowOnboarding), null, -1, false);

        if (onboarding) ShowOnboarding();
        else ShowMain();
    }

    public void ShowMain()
    {
        if (_main is null)
        {
            _main = new MainWindow(Model);
            _main.Closed += (_, _) =>
            {
                _main = null;
                ShutdownIfIdle();
            };
            _main.Show();
        }
        BringToFront(_main);
    }

    public void ShowOnboarding()
    {
        if (_onboardingWindow is null)
        {
            _onboardingWindow = new OnboardingWindow(Model);
            _onboardingWindow.Closed += (_, _) =>
            {
                var openSettings = _onboardingWindow?.OpenSettingsOnClose == true;
                _onboardingWindow = null;
                if (openSettings) ShowMain();
                else ShutdownIfIdle();
            };
            _onboardingWindow.Show();
        }
        BringToFront(_onboardingWindow);
    }

    private void ShutdownIfIdle()
    {
        if (_main is null && _onboardingWindow is null) Shutdown();
    }

    private static void BringToFront(Window window)
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (hwnd != 0) NativeMethods.SetForegroundWindow(hwnd);
    }

    private static void SignalExisting(string name)
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(name);
            handle.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The other instance is still starting up; it will show its own window.
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Settings: unhandled exception", e.Exception);
        MessageBox.Show(e.Exception.Message, "FlowSwitch Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _model?.Dispose();
        _activateWait?.Unregister(null);
        _onboardingWait?.Unregister(null);
        _activate?.Dispose();
        _onboarding?.Dispose();
        if (_instance is not null)
        {
            try { _instance.ReleaseMutex(); } catch (ApplicationException) { }
            _instance.Dispose();
        }
        base.OnExit(e);
    }
}
