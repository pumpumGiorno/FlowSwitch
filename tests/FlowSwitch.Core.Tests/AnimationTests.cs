using FlowSwitch.Core.Animation;
using FlowSwitch.Core.Settings;

namespace FlowSwitch.Core.Tests;

public class AnimationTests
{
    [Theory]
    [InlineData(1f / 240f)]
    [InlineData(1f / 60f)]
    [InlineData(0.05f)]
    public void Spring_converges_for_any_frame_time(float dt)
    {
        var spring = new Spring(0f) { Target = 1f };
        var spec = new SpringSpec(0.3f, 0.86f);
        for (float t = 0; t < 2f; t += dt) spring.Step(dt, spec);
        Assert.Equal(1f, spring.Value, 3);
    }

    [Fact]
    public void Spring_is_frame_rate_independent()
    {
        var spec = new SpringSpec(0.3f, 0.9f);
        var a = new Spring(0f) { Target = 1f };
        var b = new Spring(0f) { Target = 1f };
        for (int i = 0; i < 24; i++) a.Step(1f / 240f, spec);
        for (int i = 0; i < 6; i++) b.Step(1f / 60f, spec);
        Assert.Equal(a.Value, b.Value, 4);
    }

    [Fact]
    public void Critically_damped_spring_never_overshoots()
    {
        var spring = new Spring(0f) { Target = 1f };
        var spec = new SpringSpec(0.2f, 1f);
        for (int i = 0; i < 400; i++)
        {
            spring.Step(1f / 120f, spec);
            Assert.True(spring.Value <= 1.0001f);
        }
    }

    [Fact]
    public void CubicBezier_hits_endpoints_and_is_monotonic_for_standard_curves()
    {
        foreach (var curve in new[] { Easing.Enter, Easing.Exit, Easing.Standard, Easing.Soft })
        {
            Assert.Equal(0f, curve.Evaluate(0f), 4);
            Assert.Equal(1f, curve.Evaluate(1f), 4);
            float prev = 0f;
            for (float x = 0.01f; x <= 1f; x += 0.01f)
            {
                float y = curve.Evaluate(x);
                Assert.True(y >= prev - 1e-4f, $"curve decreased at {x}");
                prev = y;
            }
        }
    }

    [Fact]
    public void Rotor_settles_on_target()
    {
        var rotor = new OrbitalRotor();
        rotor.Reset(1);
        rotor.Advance(1);
        var spec = MotionProfile.Smooth.Rotor;
        for (int i = 0; i < 240; i++) rotor.Step(1f / 120f, spec);
        Assert.True(rotor.IsSettled);
        Assert.Equal(2.0, rotor.Position, 3);
    }

    [Fact]
    public void Rotor_burst_accelerates_and_never_lags_more_than_max()
    {
        var spec = MotionProfile.Smooth.Rotor;
        var single = new OrbitalRotor();
        single.Reset(0);
        single.Advance(1);
        var burst = new OrbitalRotor();
        burst.Reset(0);
        burst.Advance(4);

        single.Step(1f / 60f, spec);
        burst.Step(1f / 60f, spec);
        Assert.True(burst.Velocity > single.Velocity * 1.5, "a backlog of presses should spin the system faster");
        Assert.True(burst.Lag <= burst.MaxLag + 1e-9);
    }

    [Theory]
    [InlineData(0.4, 5, 0.4)]
    [InlineData(4.6, 5, -0.4)]
    [InlineData(-2.6, 5, 2.4)]
    [InlineData(3.0, 6, 3.0)]
    [InlineData(-3.0, 6, 3.0)]
    public void WrapOffset_maps_into_half_open_range(double d, int count, double expected)
    {
        Assert.Equal(expected, OrbitalRotor.WrapOffset(d, count), 6);
    }

    [Fact]
    public void Reduced_motion_profile_is_used_when_system_prefers_it()
    {
        var profile = MotionProfile.Resolve(new AnimationSettings(), new SolarSystemSettings(), systemPrefersReducedMotion: true);
        Assert.True(profile.Reduced);
        Assert.Equal(0f, profile.ParallaxAmount);
        Assert.Equal(0f, profile.IdleAmount);
    }

    [Fact]
    public void Every_preset_keeps_transitions_responsive()
    {
        foreach (var preset in Enum.GetValues<AnimationPreset>())
        {
            var p = MotionProfile.Resolve(new AnimationSettings { Preset = preset }, new SolarSystemSettings(), false);
            Assert.True(p.Rotor.Response <= 0.46f, $"{preset} rotor too slow");
            Assert.True(p.RevealDuration <= 0.32f, $"{preset} reveal too slow");
            Assert.True(p.ExitDuration <= 0.26f, $"{preset} exit too slow");
        }
    }
}
