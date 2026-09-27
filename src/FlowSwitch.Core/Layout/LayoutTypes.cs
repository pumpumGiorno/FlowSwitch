using System.Numerics;
using FlowSwitch.Core.Animation;
using FlowSwitch.Core.Settings;

namespace FlowSwitch.Core.Layout;

/// <summary>
/// Maps the 1600×900 design space every layout is authored in onto the actual monitor, so the
/// composition keeps its proportions on any resolution or DPI.
/// </summary>
public readonly record struct DesignSpace(Vector2 Viewport, float Scale, Vector2 Origin)
{
    public const float Width = 1600f;
    public const float Height = 900f;

    public static DesignSpace For(Vector2 viewport)
    {
        float s = MathF.Min(viewport.X / Width, viewport.Y / Height);
        var origin = (viewport - new Vector2(Width, Height) * s) * 0.5f;
        return new DesignSpace(viewport, s, origin);
    }

    public Vector2 ToPx(Vector2 design) => Origin + design * Scale;
    public Vector2 ToPx(float x, float y) => Origin + new Vector2(x, y) * Scale;
    public float Px(float designUnits) => designUnits * Scale;
    public Vector2 ToDesign(Vector2 px) => (px - Origin) / Scale;
}

/// <summary>Where and how a card is drawn this frame. All lengths are in pixels.</summary>
public struct CardPose
{
    /// <summary>Centre of the preview area.</summary>
    public Vector2 Center;
    /// <summary>Size of the live preview area (the card's glass frame surrounds it).</summary>
    public Vector2 PreviewSize;
    /// <summary>Size relative to the fully focused centre card (drives padding, radius, text).</summary>
    public float Scale;
    public float Opacity;
    /// <summary>0 = nearest to the viewer, 1 = farthest.</summary>
    public float Depth;
    /// <summary>1 = the selected card at rest in the centre.</summary>
    public float Focus;
    /// <summary>Depth-of-field blur, 0..1.</summary>
    public float Blur;
    public float Brightness;
    public float Yaw;
    public float Pitch;
    /// <summary>Distance behind the screen plane (px) for true 3D layouts.</summary>
    public float Z;
    public float Glow;
    /// <summary>Opacity of the info strip (icon, app name, window title) inside the card.</summary>
    public float InfoAlpha;
    /// <summary>Opacity of the small name label under non-selected cards (expanded stage).</summary>
    public float LabelAlpha;
    /// <summary>Opacity of a floor reflection (Cover Flow).</summary>
    public float Reflection;
    /// <summary>Painter's order: lower draws first.</summary>
    public float SortKey;

    public static CardPose Lerp(in CardPose a, in CardPose b, float t)
    {
        return new CardPose
        {
            Center = Vector2.Lerp(a.Center, b.Center, t),
            PreviewSize = Vector2.Lerp(a.PreviewSize, b.PreviewSize, t),
            Scale = Easing.Lerp(a.Scale, b.Scale, t),
            Opacity = Easing.Lerp(a.Opacity, b.Opacity, t),
            Depth = Easing.Lerp(a.Depth, b.Depth, t),
            Focus = Easing.Lerp(a.Focus, b.Focus, t),
            Blur = Easing.Lerp(a.Blur, b.Blur, t),
            Brightness = Easing.Lerp(a.Brightness, b.Brightness, t),
            Yaw = Easing.Lerp(a.Yaw, b.Yaw, t),
            Pitch = Easing.Lerp(a.Pitch, b.Pitch, t),
            Z = Easing.Lerp(a.Z, b.Z, t),
            Glow = Easing.Lerp(a.Glow, b.Glow, t),
            InfoAlpha = Easing.Lerp(a.InfoAlpha, b.InfoAlpha, t),
            LabelAlpha = Easing.Lerp(a.LabelAlpha, b.LabelAlpha, t),
            Reflection = Easing.Lerp(a.Reflection, b.Reflection, t),
            SortKey = Easing.Lerp(a.SortKey, b.SortKey, t),
        };
    }
}

/// <summary>Per-entry information a layout needs.</summary>
public readonly record struct LayoutItem(float Aspect, bool IsGroup, int GroupCount);

/// <summary>A decorative orbit line.</summary>
public readonly record struct OrbitRing(Vector2 Center, Vector2 Radii, float Roll, float Alpha, int Index);

public sealed class LayoutContext
{
    public DesignSpace Space;
    public double RotorPosition;
    public double RotorVelocity;
    /// <summary>0 = compact stage, 1 = expanded stage.</summary>
    public float Expansion;
    /// <summary>Seconds since the overlay opened (idle motion).</summary>
    public float Time;
    /// <summary>Smoothed pointer position relative to the centre, −1..1 per axis.</summary>
    public Vector2 Parallax;
    public float CardSize = 1f;
    public SolarSystemSettings Solar = new();
    public MotionProfile Motion = MotionProfile.Smooth;
    public SwitcherMode Mode;
}

public sealed class LayoutResult
{
    public CardPose[] Poses = Array.Empty<CardPose>();
    public readonly List<OrbitRing> Rings = new();
    /// <summary>Centre of the composition in pixels (ambient light anchor).</summary>
    public Vector2 Anchor;
    /// <summary>Bottom edge (px) of the focused card including its info strip — where satellites unfold.</summary>
    public float FocusBottom;

    public void Reset(int count)
    {
        if (Poses.Length != count) Poses = new CardPose[count];
        Rings.Clear();
    }
}

public interface ILayoutEngine
{
    SwitcherMode Mode { get; }

    /// <summary>
    /// True for ring-like layouts where going past the last item continues to the first.
    /// False for rows (Carousel, Cover Flow): the rotor then moves directly between indices.
    /// </summary>
    bool Wraps { get; }

    void Compute(LayoutContext context, ReadOnlySpan<LayoutItem> items, LayoutResult result);
}

public static class LayoutMath
{
    /// <summary>Fits a card of the given aspect into a box. Aspect is clamped to sane card shapes.</summary>
    public static Vector2 Fit(float aspect, float boxWidth, float boxHeight, float minAspect = 1.0f, float maxAspect = 2.2f)
    {
        aspect = Math.Clamp(float.IsFinite(aspect) ? aspect : 1.6f, minAspect, maxAspect);
        return aspect > boxWidth / boxHeight
            ? new Vector2(boxWidth, boxWidth / aspect)
            : new Vector2(boxHeight * aspect, boxHeight);
    }

    public static Vector2 Rotate(Vector2 v, float radians)
    {
        float c = MathF.Cos(radians), s = MathF.Sin(radians);
        return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    public static float Deg(float degrees) => degrees * (MathF.PI / 180f);
}
