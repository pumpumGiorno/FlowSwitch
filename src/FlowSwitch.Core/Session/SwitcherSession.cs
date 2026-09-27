using FlowSwitch.Core.Model;
using FlowSwitch.Core.Search;
using FlowSwitch.Core.Settings;

namespace FlowSwitch.Core.Session;

public enum SessionMode
{
    /// <summary>Alt+Tab: closes and activates when Alt is released.</summary>
    Standard,
    /// <summary>Ctrl+Alt+Tab / preview: stays open until Enter, click or Escape.</summary>
    Sticky,
    /// <summary>Alt+`: only the windows of the current app.</summary>
    SameApp,
}

public enum SessionStage
{
    Compact,
    Expanded,
}

public enum SessionOutcome
{
    Open,
    Committed,
    Cancelled,
}

public sealed record SessionOptions
{
    public SessionMode Mode { get; init; } = SessionMode.Standard;
    public bool Reverse { get; init; }
    public GroupingMode Grouping { get; init; } = GroupingMode.Auto;
    public bool WrapAround { get; init; } = true;
    public bool SearchEnabled { get; init; } = true;
    public float ExpandDelay { get; init; } = 0.45f;
    /// <summary>Dwell on a group before its satellites unfold automatically.</summary>
    public float GroupDwell { get; init; } = 0.7f;
}

public abstract record SessionEffect;
public sealed record ActivateWindowEffect(WindowInfo Window) : SessionEffect;
public sealed record CloseWindowEffect(WindowInfo Window) : SessionEffect;
public sealed record TogglePinEffect(WindowInfo Window) : SessionEffect;
public sealed record DismissEffect(bool Committed) : SessionEffect;
public sealed record SwitchDesktopEffect(int DesktopIndex) : SessionEffect;

/// <summary>
/// The logic of one switcher invocation: which entries exist, which is selected, search, groups,
/// and what should happen when the user commits. Pure and deterministic — all rendering and
/// animation state lives elsewhere and observes <see cref="LayoutVersion"/> / <see cref="SelectionTarget"/>.
/// </summary>
public sealed class SwitcherSession
{
    private readonly Queue<SessionEffect> _effects = new();
    private readonly WindowInfo? _foregroundAtStart;
    private IReadOnlyList<WindowInfo> _windows;
    private List<SwitcherEntry> _allEntries = new();
    private List<SwitcherEntry> _visible = new();
    private float _groupDwell;

    public SwitcherSession(IReadOnlyList<WindowInfo> mruWindows, SessionOptions options)
    {
        Options = options;
        _windows = mruWindows;
        _foregroundAtStart = mruWindows.Count > 0 ? mruWindows[0] : null;
        if (options.Mode == SessionMode.Sticky) Stage = SessionStage.Expanded;
        Rebuild();

        int initial = _visible.Count <= 1 ? 0 : options.Reverse ? _visible.Count - 1 : 1;
        SelectionTarget = initial;
    }

    public SessionOptions Options { get; }
    public SessionMode Mode => Options.Mode;
    public SessionStage Stage { get; private set; } = SessionStage.Compact;
    public SessionOutcome Outcome { get; private set; } = SessionOutcome.Open;
    public float Elapsed { get; private set; }
    public string Query { get; private set; } = string.Empty;
    public bool IsSearching => Query.Length > 0;

    /// <summary>Entries currently shown (after grouping / search), in orbit order.</summary>
    public IReadOnlyList<SwitcherEntry> Entries => _visible;

    /// <summary>Incremented whenever <see cref="Entries"/> changes (animation layer re-lays out).</summary>
    public int LayoutVersion { get; private set; }

    /// <summary>Unwrapped selection index; the orbital rotor chases this value.</summary>
    public long SelectionTarget { get; private set; }

    public int SelectedIndex => _visible.Count == 0 ? -1 : (int)(((SelectionTarget % _visible.Count) + _visible.Count) % _visible.Count);

    public SwitcherEntry? Selected => SelectedIndex >= 0 ? _visible[SelectedIndex] : null;

    /// <summary>The group whose satellites are unfolded, if any.</summary>
    public SwitcherEntry? ExpandedGroup { get; private set; }

    /// <summary>The window that will be activated on commit.</summary>
    public WindowInfo? Target => Selected?.Primary;

    public bool TryDequeueEffect(out SessionEffect effect) => _effects.TryDequeue(out effect!);

    // ───────────────────────────── navigation ─────────────────────────────

    public void Move(int delta)
    {
        if (Outcome != SessionOutcome.Open || _visible.Count == 0 || delta == 0) return;
        CollapseGroup();
        if (!Options.WrapAround)
        {
            int idx = Math.Clamp(SelectedIndex + delta, 0, _visible.Count - 1);
            SelectionTarget += idx - SelectedIndex;
        }
        else
        {
            SelectionTarget += delta;
        }
        _groupDwell = 0f;
    }

    public void Select(int index)
    {
        if (Outcome != SessionOutcome.Open || index < 0 || index >= _visible.Count) return;
        int delta = index - SelectedIndex;
        if (delta == 0) return;
        // Take the short way around the ring.
        int n = _visible.Count;
        if (Options.WrapAround && Math.Abs(delta) > n / 2) delta -= Math.Sign(delta) * n;
        CollapseGroup();
        SelectionTarget += delta;
        _groupDwell = 0f;
    }

    public void SelectKey(string key)
    {
        int i = _visible.FindIndex(e => e.Key == key);
        if (i >= 0) Select(i);
    }

    // ───────────────────────────── groups ─────────────────────────────

    public void ExpandSelectedGroup()
    {
        if (Selected is { IsGroup: true } g && ExpandedGroup != g)
        {
            ExpandedGroup = g;
            Stage = SessionStage.Expanded;
        }
    }

    public void CollapseGroup()
    {
        if (ExpandedGroup is null) return;
        ExpandedGroup.SubIndex = 0;
        ExpandedGroup = null;
    }

    /// <summary>Cycles satellites of the selected group (Alt+` / ↑↓). Expands the group first.</summary>
    public bool CycleWithinGroup(int delta)
    {
        if (Selected is not { IsGroup: true } g) return false;
        if (ExpandedGroup != g)
        {
            ExpandSelectedGroup();
            if (delta > 0) g.SubIndex = Math.Min(1, g.Windows.Count - 1);
            return true;
        }
        int n = g.Windows.Count;
        g.SubIndex = ((g.SubIndex + delta) % n + n) % n;
        return true;
    }

    public void SelectSatellite(string entryKey, int subIndex)
    {
        int i = _visible.FindIndex(e => e.Key == entryKey);
        if (i < 0) return;
        Select(i);
        var g = _visible[i];
        if (!g.IsGroup) return;
        ExpandedGroup = g;
        g.SubIndex = Math.Clamp(subIndex, 0, g.Windows.Count - 1);
    }

    // ───────────────────────────── search ─────────────────────────────

    public void AppendQuery(string text)
    {
        if (!Options.SearchEnabled || Outcome != SessionOutcome.Open || string.IsNullOrEmpty(text)) return;
        SetQuery(Query + text);
    }

    public void Backspace()
    {
        if (Query.Length == 0) return;
        SetQuery(Query[..^1]);
    }

    public void ClearQuery() => SetQuery(string.Empty);

    public void SetQuery(string query)
    {
        if (query == Query) return;
        Query = query;
        Stage = SessionStage.Expanded;
        CollapseGroup();
        Rebuild();
        SelectionTarget = _visible.Count > 1 && Query.Length == 0 && !Options.Reverse ? 1 : 0;
    }

    // ───────────────────────────── actions ─────────────────────────────

    public void Commit()
    {
        if (Outcome != SessionOutcome.Open) return;
        Outcome = SessionOutcome.Committed;
        var target = Target;
        if (target is not null && (target != _foregroundAtStart || target.IsMinimized))
            _effects.Enqueue(new ActivateWindowEffect(target));
        _effects.Enqueue(new DismissEffect(true));
    }

    /// <summary>Commit a specific entry (mouse click).</summary>
    public void CommitEntry(string key, int subIndex = -1)
    {
        int i = _visible.FindIndex(e => e.Key == key);
        if (i < 0) return;
        if (subIndex >= 0) SelectSatellite(key, subIndex);
        else Select(i);
        Commit();
    }

    public void Cancel()
    {
        if (Outcome != SessionOutcome.Open) return;
        Outcome = SessionOutcome.Cancelled;
        _effects.Enqueue(new DismissEffect(false));
    }

    public void CloseSelected()
    {
        if (Outcome == SessionOutcome.Open && Target is { } w) _effects.Enqueue(new CloseWindowEffect(w));
    }

    public void CloseEntry(string key)
    {
        var entry = _visible.Find(e => e.Key == key);
        if (entry is not null && Outcome == SessionOutcome.Open) _effects.Enqueue(new CloseWindowEffect(entry.Primary));
    }

    public void TogglePinSelected()
    {
        if (Outcome == SessionOutcome.Open && Target is { } w) _effects.Enqueue(new TogglePinEffect(w));
    }

    public void TogglePin(string key)
    {
        var entry = _visible.Find(e => e.Key == key);
        if (entry is not null && Outcome == SessionOutcome.Open) _effects.Enqueue(new TogglePinEffect(entry.Primary));
    }

    public void SwitchDesktop(int index)
    {
        if (Outcome == SessionOutcome.Open) _effects.Enqueue(new SwitchDesktopEffect(index));
    }

    // ───────────────────────────── time & data ─────────────────────────────

    public void Tick(float dt)
    {
        if (Outcome != SessionOutcome.Open) return;
        Elapsed += dt;
        if (Stage == SessionStage.Compact && Elapsed >= Options.ExpandDelay) Stage = SessionStage.Expanded;

        if (Selected is { IsGroup: true } && ExpandedGroup is null && Stage == SessionStage.Expanded)
        {
            _groupDwell += dt;
            if (_groupDwell >= Options.GroupDwell) ExpandSelectedGroup();
        }
        else
        {
            _groupDwell = 0f;
        }
    }

    /// <summary>Forces the expanded stage (e.g. the mouse moved over the overlay).</summary>
    public void Expand() => Stage = SessionStage.Expanded;

    /// <summary>Applies a fresh window list (a window opened, closed or changed) while keeping the selection.</summary>
    public void UpdateWindows(IReadOnlyList<WindowInfo> mruWindows)
    {
        if (Outcome != SessionOutcome.Open) return;
        string? selectedKey = Selected?.Key;
        long selectedHandle = Selected?.Primary.Handle ?? 0;
        int oldIndex = SelectedIndex;
        _windows = mruWindows;
        Rebuild();

        int newIndex = selectedKey is null ? -1 : _visible.FindIndex(e => e.Key == selectedKey);
        if (newIndex < 0 && selectedHandle != 0)
            newIndex = _visible.FindIndex(e => e.Windows.Any(w => w.Handle == selectedHandle));
        if (newIndex < 0) newIndex = Math.Clamp(oldIndex, 0, Math.Max(0, _visible.Count - 1));
        SelectionTarget = newIndex;
        if (ExpandedGroup is not null && !_visible.Contains(ExpandedGroup)) ExpandedGroup = null;
    }

    private void Rebuild()
    {
        IReadOnlyList<WindowInfo> source = _windows;
        if (Options.Mode == SessionMode.SameApp && _foregroundAtStart is not null)
        {
            string appId = _foregroundAtStart.App.Id;
            source = _windows.Where(w => string.Equals(w.App.Id, appId, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (Query.Length == 0)
        {
            var grouping = Options.Mode == SessionMode.SameApp ? GroupingMode.Off : Options.Grouping;
            _allEntries = EntryBuilder.Build(source, grouping);
            _visible = _allEntries;
        }
        else
        {
            // Search flattens groups: every window competes on its own.
            _visible = source
                .Select((w, mruIndex) => (w, mruIndex, score: FuzzyMatcher.Score(Query, w)))
                .Where(x => x.score > 0f)
                .OrderByDescending(x => x.score)
                .ThenBy(x => x.mruIndex)
                .Select(x => new SwitcherEntry(new[] { x.w }))
                .ToList();
        }

        // Preserve group expansion across rebuilds when the same group still exists.
        if (ExpandedGroup is not null)
        {
            var match = _visible.Find(e => e.Key == ExpandedGroup.Key);
            if (match is not null) match.SubIndex = Math.Min(ExpandedGroup.SubIndex, match.Windows.Count - 1);
            ExpandedGroup = match;
        }
        LayoutVersion++;
    }
}
