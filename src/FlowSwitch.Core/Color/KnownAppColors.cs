namespace FlowSwitch.Core.Color;

/// <summary>
/// Hand-tuned brand-derived glow colours for popular apps. These win over icon extraction because
/// brand identity is not always the dominant icon colour (e.g. Chrome, Steam, Slack).
/// </summary>
public static class KnownAppColors
{
    private static readonly Dictionary<string, string> ByExecutable = new(StringComparer.OrdinalIgnoreCase)
    {
        ["discord"] = "#5865F2",
        ["discordptb"] = "#5865F2",
        ["discordcanary"] = "#5865F2",
        ["spotify"] = "#1ED760",
        ["telegram"] = "#2AABEE",
        ["ayugram"] = "#2AABEE",
        ["chrome"] = "#5B8DEF",
        ["msedge"] = "#2FA8E8",
        ["firefox"] = "#FF7A3D",
        ["brave"] = "#FB6A3B",
        ["opera"] = "#FF3B4B",
        ["vivaldi"] = "#EF3939",
        ["arc"] = "#FF5A7A",
        ["steam"] = "#2F6FB7",
        ["steamwebhelper"] = "#2F6FB7",
        ["code"] = "#23A9F2",
        ["code - insiders"] = "#24BFA5",
        ["cursor"] = "#8FA2C7",
        ["devenv"] = "#9A6BD6",
        ["rider64"] = "#C94FF0",
        ["idea64"] = "#FE4D6E",
        ["pycharm64"] = "#21D789",
        ["webstorm64"] = "#07C3F2",
        ["clion64"] = "#22D88F",
        ["photoshop"] = "#31A8FF",
        ["illustrator"] = "#FF9A00",
        ["afterfx"] = "#9999FF",
        ["adobe premiere pro"] = "#9999FF",
        ["indesign"] = "#FF3366",
        ["lightroom"] = "#31A8FF",
        ["explorer"] = "#F2C14E",
        ["slack"] = "#C0479E",
        ["ms-teams"] = "#7B83EB",
        ["teams"] = "#7B83EB",
        ["outlook"] = "#1D8CE0",
        ["olk"] = "#1D8CE0",
        ["winword"] = "#3C8CE7",
        ["excel"] = "#21A366",
        ["powerpnt"] = "#E0663F",
        ["onenote"] = "#9A45D6",
        ["notion"] = "#B9BCC4",
        ["obsidian"] = "#8B5CF6",
        ["figma"] = "#A259FF",
        ["windowsterminal"] = "#5A9BFF",
        ["wt"] = "#5A9BFF",
        ["cmd"] = "#9AA4B2",
        ["powershell"] = "#3A7BDB",
        ["pwsh"] = "#3A7BDB",
        ["notepad"] = "#4AA3DF",
        ["notepad++"] = "#8BC34A",
        ["whatsapp"] = "#25D366",
        ["zoom"] = "#2D8CFF",
        ["vlc"] = "#FF8C1A",
        ["obs64"] = "#8C8CFF",
        ["blender"] = "#F5792A",
        ["battle.net"] = "#148EFF",
        ["skype"] = "#00AFF0",
        ["signal"] = "#3A76F0",
        ["thunderbird"] = "#2E90FF",
        ["mspaint"] = "#F7B32B",
        ["1password"] = "#1A8CFF",
        ["postman"] = "#FF6C37",
        ["githubdesktop"] = "#8F6CD8",
        ["gitkraken"] = "#179287",
        ["docker desktop"] = "#1D91E6",
        ["unity"] = "#A8B3C4",
        ["unity hub"] = "#A8B3C4",
        ["epicgameslauncher"] = "#A8B3C4",
        ["gimp-2.10"] = "#9C8E6E",
        ["krita"] = "#3BABFF",
        ["audacity"] = "#1B6FE0",
        ["foobar2000"] = "#B9BCC4",
        ["mpc-hc64"] = "#B9BCC4",
        ["qbittorrent"] = "#3A8DDE",
        ["yandex"] = "#FC3F1D",
        ["browser"] = "#FC3F1D",
        ["viber"] = "#7360F2",
        ["systemsettings"] = "#6C8EBF",
        ["taskmgr"] = "#3DA5E0",
        ["mstsc"] = "#2F8DE4",
    };

    /// <summary>Package-family fragments for Store apps (matched against the AppUserModelID).</summary>
    private static readonly (string Fragment, string Hex)[] ByAumidFragment =
    {
        ("WindowsCalculator", "#6E7FCB"),
        ("WindowsTerminal", "#5A9BFF"),
        ("SpotifyAB", "#1ED760"),
        ("Telegram", "#2AABEE"),
        ("WhatsApp", "#25D366"),
        ("Windows.Photos", "#3A96DD"),
        ("ZuneMusic", "#E0508A"),
        ("ZuneVideo", "#4C7CF3"),
        ("windows.immersivecontrolpanel", "#6C8EBF"),
        ("MicrosoftStickyNotes", "#F7D046"),
        ("Paint", "#F7B32B"),
        ("Notepad", "#4AA3DF"),
        ("Todos", "#3E7BF2"),
        ("OutlookForWindows", "#1D8CE0"),
        ("MSTeams", "#7B83EB"),
        ("Clipchamp", "#6D5DE8"),
        ("XboxApp", "#3DBB3D"),
        ("GamingApp", "#3DBB3D"),
    };

    public static bool TryGet(string? executableName, string? appUserModelId, out ColorF color)
    {
        if (!string.IsNullOrEmpty(appUserModelId))
        {
            foreach (var (fragment, hex) in ByAumidFragment)
            {
                if (appUserModelId.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                {
                    color = ColorF.FromHex(hex);
                    return true;
                }
            }
        }

        if (!string.IsNullOrEmpty(executableName))
        {
            string key = executableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? executableName[..^4]
                : executableName;
            if (ByExecutable.TryGetValue(key, out string? hex))
            {
                color = ColorF.FromHex(hex);
                return true;
            }
        }

        color = default;
        return false;
    }
}
