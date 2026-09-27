using System.Numerics;
using FlowSwitch.Core.Animation;
using FlowSwitch.Core.Settings;

namespace FlowSwitch.Core.Layout;

/// <summary>A calm, familiar grid. The highlight glides between cells with the same rotor physics.</summary>
public sealed class GridLayout : ILayoutEngine
{
    private const float AreaWidth = 1400f;
    private const float AreaHeight = 640f;
    private const float AreaTop = 120f;
    private const float Gap = 34f;
    private const float LabelSpace = 34f;
    private const float MaxCardWidth = 420f;

    public SwitcherMode Mode => SwitcherMode.Grid;

    public bool Wraps => true;

    public void Compute(LayoutContext ctx, ReadOnlySpan<LayoutItem> items, LayoutResult result)
    {
        int n = items.Length;
        result.Reset(n);
        if (n == 0) return;
        var sp = ctx.Space;
        var motion = ctx.Motion;
        Vector2 parallax = ctx.Parallax * motion.ParallaxAmount;

        // Pick the column count that gives the largest cards.
        int bestCols = 1;
        float bestW = 0f;
        for (int cols = 1; cols <= n; cols++)
        {
            int rows = (n + cols - 1) / cols;
            float cellW = (AreaWidth - (cols - 1) * Gap) / cols;
            float cellH = (AreaHeight - (rows - 1) * Gap) / rows - LabelSpace;
            if (cellH <= 20f) continue;
            float w = MathF.Min(MathF.Min(cellW, cellH * 1.6f), MaxCardWidth * ctx.CardSize);
            if (w > bestW + 0.5f) { bestW = w; bestCols = cols; }
        }

        int columns = bestCols;
        int rowCount = (n + columns - 1) / columns;
        float cellWidth = bestW;
        float cellHeight = bestW / 1.6f;
        float gridH = rowCount * (cellHeight + LabelSpace) + (rowCount - 1) * Gap;
        float top = AreaTop + (AreaHeight - gridH) * 0.5f;

        result.Anchor = sp.ToPx(new Vector2(DesignSpace.Width * 0.5f, top + gridH * 0.5f));
        result.FocusBottom = sp.ToPx(new Vector2(0, top + gridH)).Y;

        for (int i = 0; i < n; i++)
        {
            ref CardPose pose = ref result.Poses[i];
            int row = i / columns, col = i % columns;
            int inRow = row == rowCount - 1 ? n - row * columns : columns;
            float rowWidth = inRow * cellWidth + (inRow - 1) * Gap;
            float left = (DesignSpace.Width - rowWidth) * 0.5f;
            var cellCenter = new Vector2(left + col * (cellWidth + Gap) + cellWidth * 0.5f,
                                         top + row * (cellHeight + LabelSpace + Gap) + cellHeight * 0.5f);

            double d = n == 1 ? 0 : OrbitalRotor.WrapOffset(i - ctx.RotorPosition, n);
            float focus = 1f - Easing.Smootherstep(Math.Min(1f, (float)Math.Abs(d)));
            Vector2 box = LayoutMath.Fit(items[i].Aspect, cellWidth, cellHeight, 1.0f, 2.2f);
            float grow = 1f + 0.08f * focus * motion.ScaleIntensity;

            pose.Center = sp.ToPx(cellCenter - parallax * (6f + 4f * focus));
            pose.PreviewSize = box * sp.Scale * grow;
            pose.Scale = cellWidth / (SolarSystemLayout.CenterBoxWidth * ctx.CardSize) * grow;
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
