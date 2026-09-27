using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FlowSwitch.Settings.Controls;
using FlowSwitch.Settings.Pages;
using FlowSwitch.Settings.Services;
using static FlowSwitch.Settings.Services.NativeMethods;

namespace FlowSwitch.Settings;

public partial class MainWindow : Window
{
    private readonly SettingsModel _model;
    private readonly Dictionary<RadioButton, Func<FrameworkElement>> _pages;
    private readonly Dictionary<RadioButton, FrameworkElement> _cache = new();
    private readonly DispatcherTimer _statusTimer;
    private RadioButton? _current;
    private bool _maxPressed;

    public MainWindow(SettingsModel model)
    {
        _model = model;
        InitializeComponent();
        DataContext = model;

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? "Settings" : $"Version {version.Major}.{version.Minor}.{version.Build}";

        _pages = new Dictionary<RadioButton, Func<FrameworkElement>>
        {
            [NavGeneral] = () => new GeneralPage(),
            [NavAppearance] = () => new AppearancePage(),
            [NavSolar] = () => new SolarSystemPage(),
            [NavAnimations] = () => new AnimationsPage(),
            [NavPerformance] = () => new PerformancePage(),
            [NavHotkeys] = () => new HotkeysPage(),
            [NavAdvanced] = () => new AdvancedPage(),
            [NavAbout] = () => new AboutPage(),
        };
        foreach (var nav in _pages.Keys) nav.Checked += (s, _) => Navigate((RadioButton)s!);

        // Any edit in any page: save shortly after (no Apply button anywhere).
        AddHandler(RangeBase.ValueChangedEvent, new RoutedPropertyChangedEventHandler<double>((_, _) => _model.MarkDirty()));
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(OnToggle));
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(OnToggle));
        AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => _model.MarkDirty()));
        AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => _model.MarkDirty()));

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _model.Changed += (_, _) => UpdateStatus();

        Loaded += (_, _) =>
        {
            NavGeneral.IsChecked = true;
            UpdateStatus();
            _statusTimer.Start();
        };
        Closed += (_, _) =>
        {
            _statusTimer.Stop();
            _model.SaveNow();
        };
        StateChanged += (_, _) => OnStateChanged();
    }

    // ───────────────────────────── navigation ─────────────────────────────

    public void Navigate(RadioButton nav)
    {
        if (_current == nav) return;
        bool first = _current is null;
        _current = nav;
        nav.IsChecked = true;

        if (!_cache.TryGetValue(nav, out var page))
        {
            page = _pages[nav]();
            _cache[nav] = page;
        }
        PageHost.Content = page;
        PageScroller.ScrollToTop();

        // Arrive: fade in and settle upward by a few pixels.
        var offset = new TranslateTransform(0, first ? 0 : 14);
        page.RenderTransform = offset;
        Motion.Animate(page, OpacityProperty, 1, 0);
        if (!first)
        {
            page.Opacity = 0;
            Motion.Animate(page, OpacityProperty, 1, 240, Motion.EaseOut);
            Motion.Animate(offset, TranslateTransform.YProperty, 0, 320, Motion.Glide);
        }
        MoveIndicator(nav, animate: !first);
    }

    private void MoveIndicator(RadioButton nav, bool animate)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!nav.IsLoaded) return;
            var p = nav.TranslatePoint(new Point(0, 0), NavPanel);
            double y = p.Y + (nav.ActualHeight - NavIndicator.Height) / 2;
            NavIndicator.Opacity = 1;
            Motion.Animate(NavIndicatorOffset, TranslateTransform.YProperty, y, animate ? 340 : 0, Motion.Glide);
            if (animate && Motion.Enabled)
            {
                // Stretch mid-flight, like a drop of light sliding between entries.
                var stretch = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(340) };
                stretch.KeyFrames.Add(new EasingDoubleKeyFrame(1.9, KeyTime.FromPercent(0.35), Motion.EaseOut));
                stretch.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0), Motion.Glide));
                NavIndicatorScale.BeginAnimation(ScaleTransform.ScaleYProperty, stretch);
            }
        });
    }

    private void OnToggle(object sender, RoutedEventArgs e)
    {
        // Navigation radio buttons are not settings.
        if (e.OriginalSource is RadioButton { GroupName: "Nav" }) return;
        _model.MarkDirty();
    }

    // ───────────────────────────── status ─────────────────────────────

    private void UpdateStatus()
    {
        bool running = FlowSwitchHost.IsRunning;
        var general = _model.Settings.General;
        if (!running)
        {
            StatusDot.Fill = (Brush)FindResource("TextTertiaryBrush");
            StatusText.Text = "Not running";
            StatusDetail.Text = "Windows' Alt + Tab is active.";
            TryText.Text = "Start FlowSwitch";
        }
        else if (!general.Enabled)
        {
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0x6B));
            StatusText.Text = "Turned off";
            StatusDetail.Text = "Windows' Alt + Tab is active.";
            TryText.Text = "Try it";
        }
        else
        {
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x97));
            StatusText.Text = "Running";
            StatusDetail.Text = "Alt + Tab is handled by FlowSwitch.";
            TryText.Text = "Try it";
        }
    }

    private void OnTryClick(object sender, RoutedEventArgs e)
    {
        _model.SaveNow();
        if (!FlowSwitchHost.IsRunning)
        {
            if (!FlowSwitchHost.Start())
                MessageBox.Show(this, "FlowSwitch.exe was not found next to the Settings app.", "FlowSwitch", MessageBoxButton.OK, MessageBoxImage.Information);
            Dispatcher.BeginInvoke(DispatcherPriority.Background, UpdateStatus);
            return;
        }
        FlowSwitchHost.ShowPreview();
    }

    // ───────────────────────────── window chrome ─────────────────────────────

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        nint hwnd = new WindowInteropHelper(this).Handle;
        SetDwmInt(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
        if (IsWindows11) SetDwmInt(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND);

        if (SupportsMica && HwndSource.FromHwnd(hwnd) is { CompositionTarget: not null } source)
        {
            // Let DWM's Mica show through: transparent WPF background over the glass frame.
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            Background = Brushes.Transparent;
            if (!SetDwmInt(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_MAINWINDOW))
            {
                Background = (Brush)FindResource("FallbackBackdropBrush");
            }
        }
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        OnStateChanged();
    }

    private void OnStateChanged()
    {
        bool max = WindowState == WindowState.Maximized;
        MaximizeButton.Content = max ? "" : "";
        MaximizeButton.ToolTip = max ? "Restore" : "Maximize";
        // A maximised chromeless window extends past the screen by the resize border.
        var t = SystemParameters.WindowResizeBorderThickness;
        Root.Margin = max ? new Thickness(t.Left + 4, t.Top + 4, t.Right + 4, t.Bottom + 4) : new Thickness(0);
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>Reports the maximise button as HTMAXBUTTON so Windows 11 shows Snap Layouts on hover.</summary>
    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                if (IsOverMaximize(lParam))
                {
                    SetMaximizeHot(true);
                    handled = true;
                    return HTMAXBUTTON;
                }
                SetMaximizeHot(false);
                break;
            case WM_NCMOUSELEAVE:
                SetMaximizeHot(false);
                _maxPressed = false;
                break;
            case WM_NCLBUTTONDOWN when wParam == HTMAXBUTTON:
                _maxPressed = true;
                handled = true;
                return 0;
            case WM_NCLBUTTONUP when wParam == HTMAXBUTTON:
                if (_maxPressed) ToggleMaximize();
                _maxPressed = false;
                handled = true;
                return 0;
        }
        return 0;
    }

    private bool IsOverMaximize(nint lParam)
    {
        if (!MaximizeButton.IsVisible) return false;
        int x = unchecked((short)(lParam & 0xFFFF));
        int y = unchecked((short)((lParam >> 16) & 0xFFFF));
        try
        {
            var p = MaximizeButton.PointFromScreen(new Point(x, y));
            return p.X >= 0 && p.Y >= 0 && p.X < MaximizeButton.ActualWidth && p.Y < MaximizeButton.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void SetMaximizeHot(bool hot)
    {
        object? tag = hot ? "hot" : null;
        if (!Equals(MaximizeButton.Tag, tag)) MaximizeButton.Tag = tag;
    }
}
