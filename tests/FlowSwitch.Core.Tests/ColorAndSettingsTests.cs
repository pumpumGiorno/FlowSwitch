using System.Numerics;
using FlowSwitch.Core.Color;
using FlowSwitch.Core.Settings;

namespace FlowSwitch.Core.Tests;

public class ColorAndSettingsTests
{
    [Theory]
    [InlineData("#5865F2")]
    [InlineData("#1ED760")]
    [InlineData("#F2C14E")]
    [InlineData("#000000")]
    [InlineData("#FFFFFF")]
    public void Oklab_roundtrips(string hex)
    {
        var c = ColorF.FromHex(hex);
        var back = OkLab.ToSrgb(OkLab.FromSrgb(c));
        Assert.Equal(c.R, back.R, 3);
        Assert.Equal(c.G, back.G, 3);
        Assert.Equal(c.B, back.B, 3);
    }

    [Fact]
    public void Accent_extraction_finds_the_logo_colour_on_a_grey_background()
    {
        const int size = 64;
        var px = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int i = (y * size + x) * 4;
            bool logo = Vector2.Distance(new Vector2(x, y), new Vector2(32, 32)) < 12;
            (byte r, byte g, byte b) = logo ? ((byte)30, (byte)215, (byte)96) : ((byte)120, (byte)120, (byte)124);
            px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255;
        }
        var accent = AccentExtractor.Extract(px, size, size, size * 4, premultiplied: true);
        Assert.NotNull(accent);
        Assert.True(accent!.Value.G > accent.Value.R && accent.Value.G > accent.Value.B);
    }

    [Fact]
    public void Monochrome_icons_have_no_accent()
    {
        var px = Enumerable.Repeat((byte)200, 32 * 32 * 4).ToArray();
        Assert.Null(AccentExtractor.Extract(px, 32, 32, 32 * 4, premultiplied: true));
    }

    [Fact]
    public void Glow_normalisation_keeps_hue_and_tames_extremes()
    {
        var neon = AccentExtractor.NormalizeForGlow(ColorF.FromHex("#00FF00"));
        var (l, c, _) = OkLab.ToLch(OkLab.FromSrgb(neon));
        Assert.InRange(l, 0.63f, 0.81f);
        Assert.InRange(c, 0.05f, 0.175f);
        Assert.True(neon.G > neon.R && neon.G > neon.B);
    }

    [Fact]
    public void Known_apps_have_brand_colours()
    {
        Assert.True(KnownAppColors.TryGet("Discord.exe", null, out var discord));
        Assert.Equal("#5865F2", discord.ToHex());
        Assert.True(KnownAppColors.TryGet("x.exe", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", out _));
        Assert.False(KnownAppColors.TryGet("unknown-tool.exe", null, out _));
    }

    [Fact]
    public void Settings_roundtrip_through_json()
    {
        var s = new FlowSwitchSettings();
        s.General.Mode = SwitcherMode.CoverFlow;
        s.Appearance.GlowIntensity = 0.33f;
        s.Advanced.PassthroughProcesses.Add("game.exe");
        var back = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(s));
        Assert.Equal(SwitcherMode.CoverFlow, back.General.Mode);
        Assert.Equal(0.33f, back.Appearance.GlowIntensity, 4);
        Assert.Contains("game.exe", back.Advanced.PassthroughProcesses);
        Assert.Contains("\"coverFlow\"", SettingsSerializer.Serialize(s), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Hand_edited_out_of_range_values_are_clamped()
    {
        var s = SettingsSerializer.Deserialize("{ \"appearance\": { \"glowIntensity\": 7, \"cardCornerRadius\": -3 }, \"general\": { \"revealDelayMs\": 99999 } }");
        Assert.Equal(1f, s.Appearance.GlowIntensity);
        Assert.Equal(6f, s.Appearance.CardCornerRadius);
        Assert.Equal(400, s.General.RevealDelayMs);
    }

    [Fact]
    public void Corrupt_settings_file_falls_back_to_defaults()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fs-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{ this is not json");
            var store = new SettingsStore(dir);
            var s = store.Load();
            Assert.Equal(SwitcherMode.SolarSystem, s.General.Mode);
            Assert.True(File.Exists(Path.Combine(dir, "settings.json.corrupt")));

            s.General.Mode = SwitcherMode.Grid;
            store.Save(s);
            Assert.Equal(SwitcherMode.Grid, store.Load().General.Mode);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData(PowerState.AC, QualityPreset.High)]
    [InlineData(PowerState.Battery, QualityPreset.Balanced)]
    [InlineData(PowerState.BatterySaver, QualityPreset.BatterySaver)]
    public void Auto_quality_follows_power_state(PowerState power, QualityPreset expected)
    {
        Assert.Equal(expected, QualityProfile.Resolve(new PerformanceSettings(), power).Preset);
    }

    [Fact]
    public void Battery_saver_uses_static_previews_and_no_particles()
    {
        var q = QualityProfile.BatterySaver;
        Assert.Equal(0f, q.NearPreviewFps);
        Assert.Equal(0f, q.FarPreviewFps);
        Assert.Equal(0, q.ParticleCount);
        Assert.Equal(0f, q.BackdropFps);
    }
}
