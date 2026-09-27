using System.Numerics;
using FlowSwitch.Core.Animation;
using FlowSwitch.Core.Color;
using FlowSwitch.Core.Layout;
using FlowSwitch.Core.Scene;
using FlowSwitch.Core.Session;
using FlowSwitch.Core.Settings;

namespace FlowSwitch.Core.Tests;

public class SceneTests
{
    private sealed class Resources : ISceneResources
    {
        public bool HasBackdrop => true;
        public PreviewInfo GetPreview(long windowHandle) => new(true, new Vector4(0, 0, 1, 1), true, 1.6f);
        public bool HasIcon(string appId) => true;
    }

    private static (SwitcherSession Session, SwitcherAnimator Animator, SceneInput Input, ILayoutEngine Engine) Setup(int windows, SwitcherMode mode = SwitcherMode.SolarSystem)
    {
        var settings = new FlowSwitchSettings();
        var session = new SwitcherSession(TestData.Windows(windows), new SessionOptions { Grouping = GroupingMode.Off });
        var motion = MotionProfile.Resolve(settings.Animation, settings.Solar, false);
        var animator = new SwitcherAnimator();
        animator.Begin(session, motion);
        var ctx = new LayoutContext { Space = DesignSpace.For(new Vector2(2560, 1440)), Motion = motion, Solar = settings.Solar, Mode = mode };
        var input = new SceneInput
        {
            Session = session,
            Animator = animator,
            Layout = ctx,
            Palette = ScenePalette.Resolve(settings.Appearance, settings.Solar, mode),
            Quality = QualityProfile.High,
            Resources = new Resources(),
        };
        return (session, animator, input, LayoutFactory.Create(mode));
    }

    private static void Run(SwitcherSession s, SwitcherAnimator a, SceneInput input, ILayoutEngine engine, float seconds)
    {
        for (float t = 0; t < seconds; t += 1f / 60f)
        {
            s.Tick(1f / 60f);
            a.Update(1f / 60f, s, engine, input.Layout, input.Resources, input.Palette, null);
        }
    }

    [Fact]
    public void Frame_starts_with_the_backdrop_and_contains_cards_and_text()
    {
        var (s, a, input, engine) = Setup(6);
        Run(s, a, input, engine, 0.8f);
        var list = new SceneComposer().Compose(input);
        Assert.Equal(ShaderKind.Backdrop, list.Commands[0].Shader);
        Assert.Equal(6, list.Commands.ToArray().Count(c => c.Shader == ShaderKind.Card));
        Assert.Contains(list.Commands.ToArray(), c => c.Texture.Kind == TextureKind.Text && c.Texture.Text!.Text == "Discord");
    }

    [Fact]
    public void Clicking_the_centre_hits_the_selected_card()
    {
        var (s, a, input, engine) = Setup(6);
        Run(s, a, input, engine, 0.8f);
        var composer = new SceneComposer();
        composer.Compose(input);
        var hit = composer.HitTest(a.Layout.Anchor);
        Assert.Equal(HitKind.Card, hit.Kind);
        Assert.Equal(s.Selected!.Key, hit.Key);
    }

    [Fact]
    public void Exit_animation_finishes_quickly()
    {
        var (s, a, input, engine) = Setup(6);
        Run(s, a, input, engine, 0.5f);
        s.Commit();
        a.BeginExit(true, s.Selected!.Key, new Vector4(100, 100, 1600, 1000));
        Run(s, a, input, engine, 0.3f);
        Assert.True(a.IsFinished);
    }

    [Fact]
    public void Ambient_light_follows_the_selected_app_colour()
    {
        var (s, a, input, engine) = Setup(4);
        foreach (var e in s.Entries) e.Primary.Accent = ColorF.FromHex("#5865F2");
        s.Entries[2].Primary.Accent = ColorF.FromHex("#1ED760");
        Run(s, a, input, engine, 0.5f);
        s.Move(1);
        Run(s, a, input, engine, 0.6f);
        var ambient = OkLab.ToSrgb(a.AmbientLab.Value);
        Assert.True(ambient.G > ambient.B, "ambient light should have turned green");
    }

    [Fact]
    public void Removing_a_window_fades_its_card_out()
    {
        var windows = TestData.Windows(5);
        var (_, a, input, engine) = Setup(5);
        var s = new SwitcherSession(windows, new SessionOptions { Grouping = GroupingMode.Off });
        input.Session = s;
        a.Begin(s, input.Layout.Motion);
        Run(s, a, input, engine, 0.5f);
        windows.RemoveAt(3);
        s.UpdateWindows(windows);
        Run(s, a, input, engine, 1f / 60f);
        Assert.Single(a.RemovedCards);
        Run(s, a, input, engine, 0.6f);
        Assert.Empty(a.RemovedCards);
        Assert.Equal(4, a.Cards.Count);
    }

    [Fact]
    public void Projection_is_identity_for_flat_cards()
    {
        var frame = new FrameConstants { Camera = new Vector4(0, 2000, 1280, 720) };
        var p = Projection.Project(new Vector2(30, -20), new Vector4(500, 400, 1, 1), Vector4.Zero, frame);
        Assert.Equal(530f, p.X, 3);
        Assert.Equal(380f, p.Y, 3);
    }

    [Fact]
    public void Text_placement_resolves_anchor_to_rectangle()
    {
        var cmd = new DrawCommand { Shader = ShaderKind.Sprite };
        cmd.P[4] = new Vector4(100, 50, 0.5f, 1f);
        cmd.P[7] = new Vector4(0, 0, TextPlacement.TextFlag, 1f);
        Assert.True(TextPlacement.IsText(cmd));
        TextPlacement.Resolve(ref cmd, new Vector2(80, 20));
        Assert.Equal(new Vector4(60, 30, 140, 50), cmd.P[4]);
        Assert.False(TextPlacement.IsText(cmd));
    }

    [Theory]
    [InlineData(SwitcherMode.Carousel)]
    [InlineData(SwitcherMode.Grid)]
    [InlineData(SwitcherMode.CoverFlow)]
    [InlineData(SwitcherMode.OrbitMinimal)]
    public void Every_mode_composes_a_frame(SwitcherMode mode)
    {
        var (s, a, input, engine) = Setup(7, mode);
        Run(s, a, input, engine, 0.6f);
        var list = new SceneComposer().Compose(input);
        Assert.True(list.Count > 10);
        foreach (var cmd in list.Commands)
            for (int i = 0; i < 12; i++)
                Assert.True(float.IsFinite(cmd.P[i].X) && float.IsFinite(cmd.P[i].Y) && float.IsFinite(cmd.P[i].Z) && float.IsFinite(cmd.P[i].W));
    }
}
