using FlowSwitch.Core.Settings;

namespace FlowSwitch.Core.Model;

/// <summary>
/// One object in the switcher: a single window, or an app "planet" whose extra windows orbit it
/// as satellites.
/// </summary>
public sealed class SwitcherEntry
{
    private readonly WindowInfo[] _windows;

    public SwitcherEntry(IEnumerable<WindowInfo> windows, bool forceGroup = false)
    {
        _windows = windows.ToArray();
        if (_windows.Length == 0) throw new ArgumentException("An entry needs at least one window.", nameof(windows));
        IsGroup = forceGroup || _windows.Length > 1;
        Key = IsGroup ? $"g:{_windows[0].App.Id}" : $"w:{_windows[0].Handle:X}";
    }

    /// <summary>Stable identity used to carry animation state across list changes.</summary>
    public string Key { get; }

    public bool IsGroup { get; }

    /// <summary>Windows in MRU order. The first one is the "planet".</summary>
    public IReadOnlyList<WindowInfo> Windows => _windows;

    /// <summary>Selected satellite when the group is expanded.</summary>
    public int SubIndex { get; set; }

    public WindowInfo Primary => _windows[Math.Clamp(SubIndex, 0, _windows.Length - 1)];

    public WindowInfo Planet => _windows[0];

    public AppIdentity App => _windows[0].App;

    public override string ToString() => IsGroup ? $"{App.DisplayName} ×{_windows.Length}" : Primary.ToString();
}

/// <summary>Turns an MRU window list into switcher entries (pinning + grouping).</summary>
public static class EntryBuilder
{
    /// <summary>Number of leading MRU windows that are never grouped (keeps quick Alt+Tab muscle memory intact).</summary>
    public const int ProtectedCount = 2;

    public static List<SwitcherEntry> Build(IReadOnlyList<WindowInfo> mru, GroupingMode grouping)
    {
        var ordered = ApplyPins(mru);
        var entries = new List<SwitcherEntry>(ordered.Count);
        if (grouping == GroupingMode.Off || ordered.Count <= ProtectedCount + 1)
        {
            foreach (var w in ordered) entries.Add(new SwitcherEntry(new[] { w }));
            return entries;
        }

        int threshold = grouping == GroupingMode.Always ? 2 : 3;
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = ProtectedCount; i < ordered.Count; i++)
        {
            if (ordered[i].IsPinned) continue;
            counts[ordered[i].App.Id] = counts.GetValueOrDefault(ordered[i].App.Id) + 1;
        }

        var groups = new Dictionary<string, List<WindowInfo>>(StringComparer.OrdinalIgnoreCase);
        var placeholders = new List<(int Index, string AppId)>();
        for (int i = 0; i < ordered.Count; i++)
        {
            var w = ordered[i];
            bool groupable = i >= ProtectedCount && !w.IsPinned && counts.GetValueOrDefault(w.App.Id) >= threshold;
            if (!groupable)
            {
                entries.Add(new SwitcherEntry(new[] { w }));
                continue;
            }
            if (!groups.TryGetValue(w.App.Id, out var list))
            {
                list = new List<WindowInfo>();
                groups[w.App.Id] = list;
                placeholders.Add((entries.Count, w.App.Id));
                entries.Add(null!); // filled below, at the position of the app's most recent window
            }
            list.Add(w);
        }

        foreach (var (index, appId) in placeholders)
            entries[index] = new SwitcherEntry(groups[appId], forceGroup: true);
        return entries;
    }

    /// <summary>
    /// Pinned windows move up right after the two most recent windows, so they are always one or
    /// two Tab presses away without disturbing the "Alt+Tab goes back" behaviour.
    /// </summary>
    public static List<WindowInfo> ApplyPins(IReadOnlyList<WindowInfo> mru)
    {
        var result = new List<WindowInfo>(mru.Count);
        var pinned = new List<WindowInfo>();
        var rest = new List<WindowInfo>();
        for (int i = 0; i < mru.Count; i++)
        {
            if (i < ProtectedCount) result.Add(mru[i]);
            else if (mru[i].IsPinned) pinned.Add(mru[i]);
            else rest.Add(mru[i]);
        }
        result.AddRange(pinned);
        result.AddRange(rest);
        return result;
    }
}
