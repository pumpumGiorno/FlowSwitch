using FlowSwitch.Core.Color;

namespace FlowSwitch.Core.Model;

/// <summary>Integer rectangle in screen pixels.</summary>
public readonly record struct RectI(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public float CenterX => X + Width * 0.5f;
    public float CenterY => Y + Height * 0.5f;
}

/// <summary>Who a window belongs to, as the user understands it ("Google Chrome", "Discord").</summary>
public sealed record AppIdentity(
    string Id,
    string DisplayName,
    string ExecutableName,
    string? ExecutablePath = null,
    string? AppUserModelId = null)
{
    public static AppIdentity Unknown { get; } = new("unknown", "Application", "unknown");
}

/// <summary>
/// A switchable top-level window. Instances are owned by the window tracker and updated in place
/// (title, state) while the switcher is open.
/// </summary>
public sealed class WindowInfo
{
    public required long Handle { get; init; }
    public required AppIdentity App { get; init; }
    public string Title { get; set; } = string.Empty;
    public bool IsMinimized { get; set; }
    public bool IsHung { get; set; }
    public bool IsPinned { get; set; }
    public bool IsOnCurrentDesktop { get; set; } = true;
    public Guid DesktopId { get; set; }
    public bool IsElevated { get; set; }
    /// <summary>Visible frame bounds (restored bounds for minimized windows).</summary>
    public RectI Bounds { get; set; }
    /// <summary>Width / height of the window frame.</summary>
    public float AspectRatio => Bounds.IsEmpty ? 16f / 10f : Math.Clamp(Bounds.Width / (float)Bounds.Height, 0.4f, 4f);
    /// <summary>Glow colour for the app (brand table → icon extraction → neutral).</summary>
    public ColorF Accent { get; set; } = ColorF.NeutralAccent;
    /// <summary>Monotonic activation stamp for MRU ordering (0 = never observed).</summary>
    public long LastActivated { get; set; }

    /// <summary>Secondary line under the app name; hides titles that just repeat the app name.</summary>
    public string Subtitle
    {
        get
        {
            string title = Title.Trim();
            if (title.Length == 0 || string.Equals(title, App.DisplayName, StringComparison.OrdinalIgnoreCase)) return string.Empty;
            return title;
        }
    }

    public override string ToString() => $"0x{Handle:X} {App.DisplayName} — {Title}";
}
