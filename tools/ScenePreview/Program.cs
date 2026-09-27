using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using FlowSwitch.Core.Animation;
using FlowSwitch.Core.Color;
using FlowSwitch.Core.Layout;
using FlowSwitch.Core.Model;
using FlowSwitch.Core.Scene;
using FlowSwitch.Core.Session;
using FlowSwitch.Core.Settings;

// Usage: ScenePreview --out <dir> [--width 2560 --height 1440 --fps 60]
// Writes <scenario>.json for every scenario below.

var options = ParseArgs(args);
string outDir = options.GetValueOrDefault("out", "tools/web-preview/generated");
int width = int.Parse(options.GetValueOrDefault("width", "2560"), CultureInfo.InvariantCulture);
int height = int.Parse(options.GetValueOrDefault("height", "1440"), CultureInfo.InvariantCulture);
int fps = int.Parse(options.GetValueOrDefault("fps", "60"), CultureInfo.InvariantCulture);
string? only = options.GetValueOrDefault("scenario");
Directory.CreateDirectory(outDir);

var scenarios = new List<Scenario>
{
    new("hero", SwitcherMode.SolarSystem, 9, Captures(0.9f), Timeline()),
    new("compact", SwitcherMode.SolarSystem, 6, Captures(0.32f), Timeline()),
    new("reveal", SwitcherMode.SolarSystem, 9, Range(0f, 0.5f, fps), Timeline()),
    new("rotate", SwitcherMode.SolarSystem, 9, Range(0.95f, 1.6f, fps), Timeline((1.0f, "tab"))),
    new("burst", SwitcherMode.SolarSystem, 9, Range(0.95f, 2.1f, fps), Timeline((1.0f, "tab"), (1.08f, "tab"), (1.16f, "tab"), (1.24f, "tab"))),
    new("reverse", SwitcherMode.SolarSystem, 9, Range(0.95f, 1.6f, fps), Timeline((1.0f, "shift-tab"))),
    new("many", SwitcherMode.SolarSystem, 16, Captures(0.9f), Timeline()),
    new("two", SwitcherMode.SolarSystem, 2, Captures(0.9f), Timeline()),
    new("search", SwitcherMode.SolarSystem, 9, Captures(0.7f, 1.3f), Timeline((0.8f, "type:d"), (0.86f, "type:i"), (0.92f, "type:s"))),
    new("group", SwitcherMode.SolarSystem, 12, Captures(0.9f, 1.6f, 2.4f), Timeline((1.0f, "tab"), (1.8f, "expand"))),
    new("three", SwitcherMode.SolarSystem, 3, Captures(0.9f), Timeline()),
    new("five", SwitcherMode.SolarSystem, 5, Captures(0.9f), Timeline()),
    new("six", SwitcherMode.SolarSystem, 6, Captures(0.9f), Timeline()),
    new("ten", SwitcherMode.SolarSystem, 10, Captures(0.9f), Timeline()),
    new("twelve", SwitcherMode.SolarSystem, 12, Captures(0.9f), Timeline()),
    new("twenty", SwitcherMode.SolarSystem, 20, Captures(0.9f), Timeline()),
    new("exit", SwitcherMode.SolarSystem, 9, Range(0.9f, 1.15f, fps), Timeline((0.9f, "commit"))),
    new("minimal", SwitcherMode.OrbitMinimal, 9, Captures(0.9f), Timeline()),
    new("carousel", SwitcherMode.Carousel, 9, Captures(0.9f), Timeline()),
    new("grid", SwitcherMode.Grid, 9, Captures(0.9f), Timeline()),
    new("coverflow", SwitcherMode.CoverFlow, 9, Captures(0.9f), Timeline()),
};

foreach (var scenario in scenarios)
{
    if (only is not null && scenario.Name != only) continue;
    var json = Run(scenario, width, height, fps);
    File.WriteAllText(Path.Combine(outDir, scenario.Name + ".json"), json);
    Console.WriteLine($"{scenario.Name}: {scenario.Captures.Count} frame(s)");
}

static string Run(Scenario scenario, int width, int height, int fps)
{
    var windows = FakeDesktop.Windows(scenario.WindowCount);
    var settings = new FlowSwitchSettings();
    settings.General.Mode = scenario.Mode;
    var motion = MotionProfile.Resolve(settings.Animation, settings.Solar, systemPrefersReducedMotion: false);
    var quality = QualityProfile.High;
    var palette = ScenePalette.Resolve(settings.Appearance, settings.Solar, scenario.Mode);

    var session = new SwitcherSession(windows, new SessionOptions
    {
        Grouping = scenario.WindowCount >= 12 ? GroupingMode.Auto : GroupingMode.Off,
        ExpandDelay = settings.General.ExpandDelayMs / 1000f,
    });
    var animator = new SwitcherAnimator();
    animator.Begin(session, motion);
    var engine = LayoutFactory.Create(scenario.Mode);
    var ctx = new LayoutContext
    {
        Space = DesignSpace.For(new Vector2(width, height)),
        Solar = settings.Solar,
        Motion = motion,
        Mode = scenario.Mode,
        CardSize = settings.Appearance.CardSize,
    };
    var resources = new FakeResources(windows);
    var composer = new SceneComposer();
    var input = new SceneInput
    {
        Session = session,
        Animator = animator,
        Layout = ctx,
        Palette = palette,
        Quality = quality,
        Resources = resources,
        Desktops = new[] { new DesktopInfo("Desktop 1", 9, true), new DesktopInfo("Work", 4, false), new DesktopInfo("Music", 1, false) },
        BackdropLevels = quality.BackdropBlurLevels,
    };

    var sb = new StringBuilder();
    sb.Append("{\"width\":").Append(width).Append(",\"height\":").Append(height);
    sb.Append(",\"windows\":").Append(JsonSerializer.Serialize(windows.Select(w => new
    {
        handle = w.Handle,
        app = w.App.DisplayName,
        appId = w.App.Id,
        exe = w.App.ExecutableName,
        title = w.Title,
        accent = w.Accent.ToHex(),
        aspect = w.AspectRatio,
        minimized = w.IsMinimized,
    })));
    sb.Append(",\"frames\":[");

    float dt = 1f / fps;
    float end = scenario.Captures.Max() + dt * 0.5f;
    var events = new Queue<(float Time, string Action)>(scenario.Events.OrderBy(e => e.Time));
    bool first = true;
    long frameIndex = 0;
    var captures = new SortedSet<float>(scenario.Captures);
    for (float t = 0f; t <= end; t += dt, frameIndex++)
    {
        while (events.Count > 0 && events.Peek().Time <= t)
        {
            var (_, action) = events.Dequeue();
            Apply(action, session, animator);
        }
        session.Tick(dt);
        while (session.TryDequeueEffect(out var effect))
        {
            if (effect is DismissEffect d)
            {
                var target = session.Target;
                Vector4? rect = target is null ? null : new Vector4(width * 0.18f, height * 0.12f, width * 0.64f, height * 0.72f);
                animator.BeginExit(d.Committed, session.Selected?.Key, rect);
            }
        }
        animator.Update(dt, session, engine, ctx, resources, palette, null);
        input.FrameIndex = frameIndex;

        bool capture = captures.Any(c => MathF.Abs(c - t) < dt * 0.5f);
        if (!capture) continue;
        var list = composer.Compose(input);
        if (!first) sb.Append(',');
        first = false;
        WriteFrame(sb, t, list);
    }
    sb.Append("]}");
    return sb.ToString();
}

static void Apply(string action, SwitcherSession session, SwitcherAnimator animator)
{
    switch (action)
    {
        case "tab": session.Move(+1); break;
        case "shift-tab": session.Move(-1); break;
        case "commit": session.Commit(); break;
        case "cancel": session.Cancel(); break;
        case "expand": session.CycleWithinGroup(+1); break;
        default:
            if (action.StartsWith("type:", StringComparison.Ordinal)) session.AppendQuery(action[5..]);
            break;
    }
    _ = animator;
}

static void WriteFrame(StringBuilder sb, float t, DrawList list)
{
    var f = list.Frame;
    sb.Append("{\"t\":").Append(F(t));
    sb.Append(",\"frame\":[");
    AppendVec(sb, f.Viewport); sb.Append(',');
    AppendVec(sb, f.Camera); sb.Append(',');
    AppendVec(sb, f.Backdrop); sb.Append(',');
    AppendVec(sb, f.Globals);
    sb.Append("],\"cmds\":[");
    var cmds = list.Commands;
    for (int i = 0; i < cmds.Length; i++)
    {
        var c = cmds[i];
        if (i > 0) sb.Append(',');
        sb.Append("{\"s\":").Append((int)c.Shader);
        sb.Append(",\"tk\":").Append((int)c.Texture.Kind);
        if (c.Texture.Kind == TextureKind.Preview) sb.Append(",\"th\":").Append(c.Texture.Handle);
        if (c.Texture.Key is not null) sb.Append(",\"key\":").Append(JsonSerializer.Serialize(c.Texture.Key));
        if (c.Texture.Text is { } text)
        {
            sb.Append(",\"text\":{\"text\":").Append(JsonSerializer.Serialize(text.Text))
              .Append(",\"size\":").Append(F(text.SizePx))
              .Append(",\"weight\":").Append(text.Weight)
              .Append(",\"family\":").Append((int)text.Family)
              .Append(",\"maxWidth\":").Append(F(text.MaxWidthPx)).Append('}');
        }
        sb.Append(",\"p\":[");
        for (int k = 0; k < 12; k++)
        {
            if (k > 0) sb.Append(',');
            AppendVec(sb, c.P[k]);
        }
        sb.Append("]}");
    }
    sb.Append("]}");
}

static void AppendVec(StringBuilder sb, Vector4 v) =>
    sb.Append(F(v.X)).Append(',').Append(F(v.Y)).Append(',').Append(F(v.Z)).Append(',').Append(F(v.W));

static string F(float v) => float.IsFinite(v) ? v.ToString("0.#####", CultureInfo.InvariantCulture) : "0";

static List<float> Captures(params float[] times) => times.ToList();

static List<float> Range(float from, float to, int fps)
{
    var list = new List<float>();
    for (float t = from; t <= to + 1e-4f; t += 1f / fps) list.Add(MathF.Round(t * fps) / fps);
    return list;
}

static List<(float Time, string Action)> Timeline(params (float, string)[] events) => events.ToList();

static Dictionary<string, string> ParseArgs(string[] args)
{
    var d = new Dictionary<string, string>();
    for (int i = 0; i + 1 < args.Length; i += 2) d[args[i].TrimStart('-')] = args[i + 1];
    return d;
}

internal sealed record Scenario(string Name, SwitcherMode Mode, int WindowCount, List<float> Captures, List<(float Time, string Action)> Events);

internal sealed class FakeResources(IReadOnlyList<WindowInfo> windows) : ISceneResources
{
    private readonly Dictionary<long, WindowInfo> _byHandle = windows.ToDictionary(w => w.Handle);

    public bool HasBackdrop => true;

    public PreviewInfo GetPreview(long windowHandle) =>
        _byHandle.TryGetValue(windowHandle, out var w) && !w.IsMinimized
            ? new PreviewInfo(true, new Vector4(0, 0, 1, 1), true, w.AspectRatio)
            : PreviewInfo.Missing;

    public bool HasIcon(string appId) => true;
}

internal static class FakeDesktop
{
    private static readonly (string App, string Exe, string Title, int W, int H)[] Catalog =
    {
        ("Visual Studio Code", "Code.exe", "SceneComposer.cs — FlowSwitch", 1920, 1080),
        ("Discord", "Discord.exe", "#design — FlowSwitch", 1600, 1000),
        ("Google Chrome", "chrome.exe", "Orbital mechanics — YouTube — Google Chrome", 1920, 1080),
        ("Spotify", "Spotify.exe", "Spotify Premium", 1500, 950),
        ("Telegram", "Telegram.exe", "Telegram", 1280, 860),
        ("File Explorer", "explorer.exe", "Downloads", 1400, 900),
        ("Figma", "Figma.exe", "FlowSwitch — Orbit exploration", 1920, 1080),
        ("Windows Terminal", "WindowsTerminal.exe", "pwsh — FlowSwitch", 1300, 800),
        ("Steam", "steam.exe", "Steam", 1600, 1000),
        ("Photoshop", "Photoshop.exe", "Nebula_key_visual.psd @ 66%", 1920, 1080),
        ("Google Chrome", "chrome.exe", "Pull requests · FlowSwitch — Google Chrome", 1920, 1080),
        ("Google Chrome", "chrome.exe", "Windows.Graphics.Capture — Microsoft Learn", 1920, 1080),
        ("Google Chrome", "chrome.exe", "Gmail — Inbox (3)", 1920, 1080),
        ("Notepad", "notepad.exe", "ideas.txt — Notepad", 1100, 800),
        ("Microsoft Excel", "EXCEL.EXE", "Roadmap.xlsx — Excel", 1920, 1080),
        ("Obsidian", "Obsidian.exe", "Motion language — Obsidian", 1500, 950),
    };

    public static List<WindowInfo> Windows(int count)
    {
        var list = new List<WindowInfo>();
        for (int i = 0; i < count; i++)
        {
            var (app, exe, title, w, h) = Catalog[i % Catalog.Length];
            var identity = new AppIdentity(exe.ToLowerInvariant(), app, exe);
            KnownAppColors.TryGet(exe, null, out var accent);
            if (accent == default) accent = ColorF.NeutralAccent;
            list.Add(new WindowInfo
            {
                Handle = 0x10000 + i * 0x10,
                App = identity,
                Title = title,
                Bounds = new RectI(100, 100, w, h),
                Accent = AccentExtractor.NormalizeForGlow(accent),
                IsMinimized = app == "Steam",
            });
        }
        return list;
    }
}
