namespace FlowSwitch.Core.Settings;

/// <summary>Quick choices for the two card sizes (Appearance → Window size).</summary>
public enum CardSizePreset
{
    Compact,
    Default,
    Large,
    Huge,
    /// <summary>The sliders were moved away from every preset.</summary>
    Custom,
}

public static class CardSizePresets
{
    public static IReadOnlyList<CardSizePreset> All { get; } =
        new[] { CardSizePreset.Compact, CardSizePreset.Default, CardSizePreset.Large, CardSizePreset.Huge };

    /// <summary>Selected and orbit size of a preset (Custom returns the defaults).</summary>
    public static (float Selected, float Orbit) Values(CardSizePreset preset) => preset switch
    {
        CardSizePreset.Compact => (0.85f, 0.7f),
        CardSizePreset.Large => (1.25f, 0.9f),
        CardSizePreset.Huge => (1.5f, 1.05f),
        _ => (1f, 1f),
    };

    /// <summary>The preset these sizes correspond to, or <see cref="CardSizePreset.Custom"/>.</summary>
    public static CardSizePreset Match(float selected, float orbit)
    {
        foreach (var preset in All)
        {
            var (s, o) = Values(preset);
            if (MathF.Abs(s - selected) < 0.005f && MathF.Abs(o - orbit) < 0.005f) return preset;
        }
        return CardSizePreset.Custom;
    }

    public static void Apply(AppearanceSettings appearance, CardSizePreset preset)
    {
        if (preset == CardSizePreset.Custom) return;
        (appearance.SelectedCardSize, appearance.OrbitCardSize) = Values(preset);
    }
}
