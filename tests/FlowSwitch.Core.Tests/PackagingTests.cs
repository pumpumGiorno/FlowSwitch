using System.Text;
using System.Text.RegularExpressions;

namespace FlowSwitch.Core.Tests;

/// <summary>Guards for problems that only show up on a real Windows install, never in a Linux build.</summary>
public class PackagingTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FlowSwitch.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
    }

    [Fact]
    public void Shader_source_is_ascii()
    {
        // d3dcompiler_47 only ever saw the first N bytes (N = character count) of a UTF-8 source
        // with non-ASCII comments and failed with "unexpected end of file". Keep it plain ASCII.
        string path = RepoFile("src", "FlowSwitch", "Shaders", "FlowSwitch.hlsl");
        byte[] bytes = File.ReadAllBytes(path);
        int offending = Array.FindIndex(bytes, b => b > 0x7F);
        Assert.True(offending < 0, $"Non-ASCII byte at offset {offending} of FlowSwitch.hlsl: " +
            (offending >= 0 ? Encoding.UTF8.GetString(bytes, Math.Max(0, offending - 20), Math.Min(40, bytes.Length - Math.Max(0, offending - 20))) : string.Empty));
    }

    [Fact]
    public void Host_and_settings_share_one_windows_target_framework()
    {
        // Both apps are published into one folder; different Windows SDK projections overwrite
        // each other's Microsoft.Windows.SDK.NET.dll (that broke every WinRT type in the host).
        string host = File.ReadAllText(RepoFile("src", "FlowSwitch", "FlowSwitch.csproj"));
        string settings = File.ReadAllText(RepoFile("src", "FlowSwitch.Settings", "FlowSwitch.Settings.csproj"));
        var tfm = new Regex("<TargetFramework>([^<]+)</TargetFramework>");
        Assert.Equal(tfm.Match(host).Groups[1].Value, tfm.Match(settings).Groups[1].Value);
    }
}
