using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FlowSwitch.Settings.Controls;

/// <summary>A Fluent toggle switch. The knob glides with a short ease-out; no animation on first load.</summary>
public sealed class ToggleSwitch : CheckBox
{
    public static readonly DependencyProperty OnTextProperty =
        DependencyProperty.Register(nameof(OnText), typeof(string), typeof(ToggleSwitch), new PropertyMetadata("On"));

    public static readonly DependencyProperty OffTextProperty =
        DependencyProperty.Register(nameof(OffText), typeof(string), typeof(ToggleSwitch), new PropertyMetadata("Off"));

    private const double Travel = 20.0;
    private readonly TranslateTransform _knobOffset = new();
    private readonly ScaleTransform _knobScale = new(1, 1);
    private UIElement? _onTrack;

    public string OnText
    {
        get => (string)GetValue(OnTextProperty);
        set => SetValue(OnTextProperty, value);
    }

    public string OffText
    {
        get => (string)GetValue(OffTextProperty);
        set => SetValue(OffTextProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("PART_Knob") is FrameworkElement knob)
        {
            var group = new TransformGroup();
            group.Children.Add(_knobScale);
            group.Children.Add(_knobOffset);
            knob.RenderTransform = group;
        }
        _onTrack = GetTemplateChild("PART_OnTrack") as UIElement;
        Sync(animate: false);
    }

    protected override void OnChecked(RoutedEventArgs e)
    {
        base.OnChecked(e);
        Sync(IsLoaded);
    }

    protected override void OnUnchecked(RoutedEventArgs e)
    {
        base.OnUnchecked(e);
        Sync(IsLoaded);
    }

    protected override void OnMouseEnter(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        AnimateScale(1.17);
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        AnimateScale(1.0);
    }

    private void Sync(bool animate)
    {
        bool on = IsChecked == true;
        Motion.Animate(_knobOffset, TranslateTransform.XProperty, on ? Travel : 0.0, animate ? 200 : 0, Motion.Glide);
        if (_onTrack is not null) Motion.Animate(_onTrack, OpacityProperty, on ? 1.0 : 0.0, animate ? 160 : 0, Motion.EaseOut);
    }

    private void AnimateScale(double scale)
    {
        Motion.Animate(_knobScale, ScaleTransform.ScaleXProperty, scale, 140, Motion.EaseOut);
        Motion.Animate(_knobScale, ScaleTransform.ScaleYProperty, scale, 140, Motion.EaseOut);
    }
}

/// <summary>Shared motion helpers for the settings UI (respects the Windows "animation effects" setting).</summary>
public static class Motion
{
    public static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
    public static readonly IEasingFunction Glide = new QuinticEase { EasingMode = EasingMode.EaseOut };

    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    public static void Animate(IAnimatable target, DependencyProperty property, double to, int milliseconds, IEasingFunction? easing = null)
    {
        if (milliseconds <= 0 || !Enabled)
        {
            target.BeginAnimation(property, null);
            if (target is DependencyObject d) d.SetValue(property, to);
            return;
        }
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = easing ?? EaseOut,
            FillBehavior = FillBehavior.HoldEnd,
        };
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }
}
