using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FlowSwitch.Core.Ipc;
using FlowSwitch.Settings.Controls;
using FlowSwitch.Settings.Services;
using static FlowSwitch.Settings.Services.NativeMethods;

namespace FlowSwitch.Settings;

/// <summary>The first-run tour: Welcome → Solar System → Make it yours → Ready.</summary>
public partial class OnboardingWindow : Window
{
    private readonly SettingsModel _model;
    private readonly FrameworkElement[] _screens;
    private readonly List<Border> _dots = new();
    private int _index;
    private bool _finished;

    public OnboardingWindow(SettingsModel model)
    {
        _model = model;
        InitializeComponent();
        DataContext = model;

        _screens = new FrameworkElement[] { WelcomeScreen, SolarScreen, CustomizeScreen, ReadyScreen };
        foreach (var _ in _screens)
        {
            var dot = new Border
            {
                Height = 6, Width = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 8, 0),
                Background = (Brush)FindResource("TextTertiaryBrush"),
            };
            _dots.Add(dot);
            Dots.Children.Add(dot);
        }

        // Edits made here go to settings.json like everywhere else.
        AddHandler(System.Windows.Controls.Primitives.RangeBase.ValueChangedEvent,
            new RoutedPropertyChangedEventHandler<double>((_, _) => _model.MarkDirty()));
        AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => _model.MarkDirty()));
        AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => _model.MarkDirty()));

        Loaded += (_, _) =>
        {
            Show(0, direction: 0);
            StartIdleMotion();
        };
        Closing += (_, _) => Complete();
    }

    /// <summary>True when the user chose "Open Settings" on the last screen.</summary>
    public bool OpenSettingsOnClose { get; private set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        nint hwnd = new WindowInteropHelper(this).Handle;
        SetDwmInt(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
        if (IsWindows11) SetDwmInt(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND);
        if (SupportsMica && HwndSource.FromHwnd(hwnd) is { CompositionTarget: not null } source)
        {
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            Background = Brushes.Transparent;
            if (!SetDwmInt(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_MAINWINDOW))
                Background = (Brush)FindResource("FallbackBackdropBrush");
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.OriginalSource is Slider) return;
        switch (e.Key)
        {
            case Key.Right:
                Next();
                e.Handled = true;
                break;
            case Key.Left when _index > 0:
                Show(_index - 1, direction: -1);
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    private void OnNext(object sender, RoutedEventArgs e) => Next();

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_index > 0) Show(_index - 1, direction: -1);
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        OpenSettingsOnClose = true;
        Finish();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void Next()
    {
        if (_index < _screens.Length - 1) Show(_index + 1, direction: 1);
        else Finish();
    }

    private void Finish()
    {
        _finished = true;
        Close();
    }

    /// <summary>Marks the tour as seen (even when closed early, so it does not reappear on every start).</summary>
    private void Complete()
    {
        _model.Settings.General.OnboardingCompleted = true;
        _model.SaveNow();
        if (_finished) FlowSwitchHost.Send(IpcProtocol.OnboardingFinished);
    }

    private void Show(int index, int direction)
    {
        var from = _screens[_index];
        var to = _screens[index];
        _index = index;

        if (from != to && direction != 0)
        {
            // Outgoing screen drifts away and fades; the new one arrives from the other side.
            var outOffset = new TranslateTransform();
            from.RenderTransform = outOffset;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(Motion.Enabled ? 180 : 0)) { EasingFunction = Motion.EaseOut };
            fade.Completed += (_, _) =>
            {
                if (_screens[_index] != from) from.Visibility = Visibility.Collapsed;
            };
            from.BeginAnimation(OpacityProperty, fade);
            Motion.Animate(outOffset, TranslateTransform.XProperty, -36 * direction, 220, Motion.EaseOut);
        }
        else if (from != to)
        {
            from.Visibility = Visibility.Collapsed;
        }

        to.Visibility = Visibility.Visible;
        var inOffset = new TranslateTransform(direction == 0 ? 0 : 48 * direction, direction == 0 ? 12 : 0);
        to.RenderTransform = inOffset;
        to.BeginAnimation(OpacityProperty, null);
        to.Opacity = 0;
        Motion.Animate(to, OpacityProperty, 1, direction == 0 ? 520 : 320, Motion.EaseOut);
        Motion.Animate(inOffset, TranslateTransform.XProperty, 0, 420, Motion.Glide);
        Motion.Animate(inOffset, TranslateTransform.YProperty, 0, 520, Motion.Glide);

        if (to == SolarScreen) SolarPreview.Replay();
        if (to == CustomizeScreen) CustomPreview.Replay();

        UpdateFooter();
    }

    private void UpdateFooter()
    {
        var accent = (Brush)FindResource("AccentGradientBrush");
        var idle = (Brush)FindResource("TextTertiaryBrush");
        for (int i = 0; i < _dots.Count; i++)
        {
            bool active = i == _index;
            _dots[i].Background = active ? accent : idle;
            Motion.Animate(_dots[i], WidthProperty, active ? 22 : 6, 260, Motion.Glide);
        }
        BackButton.Visibility = _index == 0 ? Visibility.Hidden : Visibility.Visible;
        bool last = _index == _screens.Length - 1;
        SettingsButton.Visibility = last ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = _index switch
        {
            0 => "Get started",
            _ when last => "Done",
            _ => "Next",
        };
    }

    /// <summary>The logo breathes and the keycaps glow in turn — slow enough to be felt rather than seen.</summary>
    private void StartIdleMotion()
    {
        if (!Motion.Enabled) return;
        var breathe = new DoubleAnimation(1.0, 1.025, TimeSpan.FromSeconds(2.3))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        WelcomeLogoScale.BeginAnimation(ScaleTransform.ScaleXProperty, breathe);
        WelcomeLogoScale.BeginAnimation(ScaleTransform.ScaleYProperty, breathe);

        AltGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, Pulse(0));
        TabGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, Pulse(0.6));
    }

    private static DoubleAnimationUsingKeyFrames Pulse(double beginSeconds)
    {
        var pulse = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(2.4),
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = TimeSpan.FromSeconds(beginSeconds),
        };
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(0.2, KeyTime.FromPercent(0)));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(0.85, KeyTime.FromPercent(0.25), new SineEase { EasingMode = EasingMode.EaseOut }));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(0.2, KeyTime.FromPercent(0.7), new SineEase { EasingMode = EasingMode.EaseInOut }));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(0.2, KeyTime.FromPercent(1)));
        return pulse;
    }
}
