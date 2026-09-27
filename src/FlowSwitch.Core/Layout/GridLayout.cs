using System.Numerics;
using FlowSwitch.Core.Animation;
using FlowSwitch.Core.Settings;

namespace FlowSwitch.Core.Layout;

/// <summary>A calm, familiar grid. The highlight glides between cells with the same rotor physics.</summary>
/// <remarks>
/// Card sizes: the orbit size scales the grid (its area and largest cell), the selected size sets
/// how far the highlighted cell lifts out of it. Gaps widen with that lift, so the highlighted
/// card never covers its neighbours or their labels.
/// </remarks>
public sealed class GridLayout : ILayoutEngine
{
    private const float AreaWidth = 1400f;
    private const float AreaHeight = 640f;
    private const float AreaTop = 120f;
    /// <summary>Kept free above and below the grid for the search field, desktops and hints.</summary>
    private const float ChromeSpace = 100f;
    private const float Gap = 34f;
    private const float LabelSpace = 34f;
    private const float MaxCardWidth = 420f;
    /// <summary>Lift of the selected cell at the default sizes (before the animation's scale intensity).</summary>
    private const float BaseLift = 0.08f;

    public SwitcherMode Mode => SwitcherMode.Grid;

    public bool Wraps => true;

    public void Compute(LayoutContext ctx, ReadOnlySpan<LayoutItem> items, LayoutResult result)
    {
        int n = items.Length;
        result.Reset(n);
        if (n == 0) return;
        var sp = ctx.Space;
        var motion = ctx.Motion;
        var size = ctx.CardSize;
        Vector2 parallax = ctx.Parallax * motion.ParallaxAmount;

        // A selected size above the orbit size lifts the highlighted cell further out of the grid.
        float lift = BaseLift * motion.ScaleIntensity + 0.6f * Math.Clamp(size.Selected / MathF.Max(size.Orbit, 0.1f) - 1f, 0f, 0.8f);

        // The grid area scales with the orbit size, up to the room the monitor has around the chrome.
        var min = sp.VisibleMin;
        var max = sp.VisibleMax;
        float areaScale = Math.Clamp(size.Orbit, 0.5f, 1.5f);
        float areaWidth = MathF.Min(AreaWidth * areaScale, max.X - min.X - 120f);
        float areaHeight = MathF.Min(AreaHeight * areaScale, max.Y - min.Y - 2f * ChromeSpace);
        float areaTop = MathF.Max(min.Y + ChromeSpace, AreaTop + (AreaHeight - areaHeight) * 0.5f);
        float margin = MathF.Min(areaTop - min.Y, max.Y - areaTop - areaHeight) - 40f;

        // Pick the column count that gives the largest cards; gaps make room for the glass frames
        // and the lifted cell.
        int bestCols = 1;
        float bestW = 0f, bestGap = Gap, bestLift = lift;
        for (int cols = 1; cols <= n; cols++)
        {
            int rows = (n + cols - 1) / cols;
            float gap = Gap, w = 0f, cellLift = lift;
            for (int pass = 0; pass < 3; pass++)
            {
                float cellW = (areaWidth - (cols - 1) * gap) / cols;
                float cellH = (areaHeight - (rows - 1) * gap) / rows - LabelSpace;
                if (cellH <= 20f) { w = 0f; break; }
                w = MathF.Min(MathF.Min(cellW, cellH * 1.6f), MaxCardWidth * size.Orbit);
                // The lifted cell stays inside the margins around the grid area.
                cellLift = MathF.Max(0f, MathF.Min(lift, MathF.Min(180f / w, 2f * margin / (w / 1.6f))));
                float pad = SolarSystemLayout.CardPadding * w / SolarSystemLayout.CenterBoxWidth;
                gap = MathF.Max(Gap, w * cellLift * 0.5f + pad * (2f + cellLift) + 4f);
            }
            if (w > bestW + 0.5f) { bestW = w; bestCols = cols; bestGap = gap; bestLift = cellLift; }
        }

        int columns = bestCols;
        int rowCount = (n + columns - 1) / columns;
        float cellWidth = bestW;
        float cellHeight = bestW / 1.6f;
        float spacing = bestGap;
        float gridH = rowCount * (cellHeight + LabelSpace) + (rowCount - 1) * spacing;
        float top = areaTop + (areaHeight - gridH) * 0.5f;

        result.Anchor = sp.ToPx(new Vector2(DesignSpace.Width * 0.5f, top + gridH * 0.5f));
        result.FocusBottom = sp.ToPx(new Vector2(0, top + gridH)).Y;
        result.Topology = columns;

        for (int i = 0; i < n; i++)
        {
            ref CardPose pose = ref result.Poses[i];
            int row = i / columns, col = i % columns;
            int inRow = row == rowCount - 1 ? n - row * columns : columns;
            float rowWidth = inRow * cellWidth + (inRow - 1) * spacing;
            float left = (DesignSpace.Width - rowWidth) * 0.5f;
            var cellCenter = new Vector2(left + col * (cellWidth + spacing) + cellWidth * 0.5f,
                                         top + row * (cellHeight + LabelSpace + spacing) + cellHeight * 0.5f);

            double d = n == 1 ? 0 : OrbitalRotor.WrapOffset(i - ctx.RotorPosition, n);
            float focus = 1f - Easing.Smootherstep(Math.Min(1f, (float)Math.Abs(d)));
            Vector2 box = LayoutMath.Fit(items[i].Aspect, cellWidth, cellHeight, 1.0f, 2.2f);
            float grow = 1f + bestLift * focus;

            pose.Center = sp.ToPx(cellCenter - parallax * (6f + 4f * focus));
            pose.PreviewSize = box * sp.Scale * grow;
            pose.Scale = cellWidth / SolarSystemLayout.CenterBoxWidth * grow;
            pose.TypeScale = pose.Scale * CardSizing.TypeFactor(size.At(focus));
            pose.Opacity = 1f;
            pose.Brightness = Easing.Lerp(0.7f, 1f, focus);
            pose.Blur = 0f;
            pose.Depth = 1f - focus;
            pose.Focus = focus;
            pose.Glow = Easing.Lerp(0.18f, 1f, focus);
            pose.InfoAlpha = 0f;
            pose.LabelAlpha = 1f;
            pose.SortKey = focus;
        }
    }
}
