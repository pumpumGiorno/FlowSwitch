using FlowSwitch.Core.Color;
using FlowSwitch.Core.Model;

namespace FlowSwitch.Core.Tests;

internal static class TestData
{
    private static readonly (string App, string Exe, string Title)[] Apps =
    {
        ("Visual Studio Code", "Code.exe", "SceneComposer.cs — FlowSwitch"),
        ("Discord", "Discord.exe", "#design"),
        ("Google Chrome", "chrome.exe", "YouTube — Google Chrome"),
        ("Spotify", "Spotify.exe", "Spotify Premium"),
        ("Telegram", "Telegram.exe", "Telegram"),
        ("File Explorer", "explorer.exe", "Downloads"),
        ("Figma", "Figma.exe", "Orbit exploration"),
        ("Windows Terminal", "WindowsTerminal.exe", "pwsh"),
    };

    public static List<WindowInfo> Windows(int count, Func<int, string>? exe = null)
    {
        var list = new List<WindowInfo>();
        for (int i = 0; i < count; i++)
        {
            var (app, e, title) = Apps[i % Apps.Length];
            string exeName = exe?.Invoke(i) ?? e;
            list.Add(new WindowInfo
            {
                Handle = 0x1000 + i,
                App = new AppIdentity(exeName.ToLowerInvariant(), exe is null ? app : exeName, exeName),
                Title = $"{title} {i}",
                Bounds = new RectI(0, 0, 1600, 1000),
                Accent = ColorF.NeutralAccent,
            });
        }
        return list;
    }
}
