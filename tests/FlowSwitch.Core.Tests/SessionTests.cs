using FlowSwitch.Core.Model;
using FlowSwitch.Core.Search;
using FlowSwitch.Core.Session;
using FlowSwitch.Core.Settings;

namespace FlowSwitch.Core.Tests;

public class SessionTests
{
    private static SwitcherSession Session(int windows, SessionOptions? options = null) =>
        new(TestData.Windows(windows), options ?? new SessionOptions { Grouping = GroupingMode.Off });

    [Fact]
    public void Alt_tab_selects_the_previous_window_like_windows_does()
    {
        var s = Session(5);
        Assert.Equal(1, s.SelectedIndex);
    }

    [Fact]
    public void Alt_shift_tab_selects_the_last_window()
    {
        var s = Session(5, new SessionOptions { Reverse = true, Grouping = GroupingMode.Off });
        Assert.Equal(4, s.SelectedIndex);
    }

    [Fact]
    public void Single_window_selects_itself()
    {
        Assert.Equal(0, Session(1).SelectedIndex);
    }

    [Fact]
    public void Tab_wraps_around_and_keeps_unwrapped_direction()
    {
        var s = Session(3);
        s.Move(1);
        s.Move(1);
        Assert.Equal(0, s.SelectedIndex);
        Assert.Equal(3, s.SelectionTarget);
    }

    [Fact]
    public void Commit_activates_selection_then_dismisses()
    {
        var s = Session(4);
        s.Move(1);
        s.Commit();
        Assert.True(s.TryDequeueEffect(out var first));
        var activate = Assert.IsType<ActivateWindowEffect>(first);
        Assert.Equal(0x1002, activate.Window.Handle);
        Assert.True(s.TryDequeueEffect(out var second));
        Assert.True(Assert.IsType<DismissEffect>(second).Committed);
    }

    [Fact]
    public void Cancel_does_not_activate_anything()
    {
        var s = Session(4);
        s.Cancel();
        Assert.True(s.TryDequeueEffect(out var effect));
        Assert.False(Assert.IsType<DismissEffect>(effect).Committed);
        Assert.False(s.TryDequeueEffect(out _));
    }

    [Fact]
    public void Committing_the_current_window_does_nothing_but_close()
    {
        var s = Session(3);
        s.Select(0);
        s.Commit();
        Assert.True(s.TryDequeueEffect(out var effect));
        Assert.IsType<DismissEffect>(effect);
    }

    [Fact]
    public void Typing_filters_and_selects_best_match()
    {
        var s = Session(8);
        s.AppendQuery("disc");
        Assert.Single(s.Entries);
        Assert.Equal("Discord", s.Entries[0].App.DisplayName);
        Assert.Equal(0, s.SelectedIndex);
        Assert.Equal(SessionStage.Expanded, s.Stage);
        s.Backspace();
        s.Backspace();
        s.Backspace();
        s.Backspace();
        Assert.Equal(8, s.Entries.Count);
    }

    [Fact]
    public void Wrong_keyboard_layout_still_finds_the_app()
    {
        var s = Session(8);
        s.AppendQuery("вшыс"); // "disc" typed on a Russian layout
        Assert.Contains(s.Entries, e => e.App.DisplayName == "Discord");
    }

    [Fact]
    public void Stage_expands_after_delay()
    {
        var s = Session(4, new SessionOptions { ExpandDelay = 0.3f });
        s.Tick(0.2f);
        Assert.Equal(SessionStage.Compact, s.Stage);
        s.Tick(0.2f);
        Assert.Equal(SessionStage.Expanded, s.Stage);
    }

    [Fact]
    public void Window_closing_keeps_selection_on_same_window()
    {
        var windows = TestData.Windows(5);
        var s = new SwitcherSession(windows, new SessionOptions { Grouping = GroupingMode.Off });
        s.Move(2); // index 3
        long selected = s.Target!.Handle;
        windows.RemoveAt(1);
        s.UpdateWindows(windows);
        Assert.Equal(selected, s.Target!.Handle);
    }

    [Fact]
    public void Groups_protect_the_two_most_recent_windows()
    {
        var windows = TestData.Windows(6, i => "chrome.exe");
        var entries = EntryBuilder.Build(windows, GroupingMode.Auto);
        Assert.Equal(3, entries.Count);
        Assert.False(entries[0].IsGroup);
        Assert.False(entries[1].IsGroup);
        Assert.True(entries[2].IsGroup);
        Assert.Equal(4, entries[2].Windows.Count);
    }

    [Fact]
    public void Group_satellite_can_be_selected_and_committed()
    {
        var windows = TestData.Windows(6, i => "chrome.exe");
        var s = new SwitcherSession(windows, new SessionOptions { Grouping = GroupingMode.Auto });
        s.Select(2);
        Assert.True(s.CycleWithinGroup(1));
        Assert.True(s.CycleWithinGroup(1));
        Assert.Equal(windows[4].Handle, s.Target!.Handle);
        s.Commit();
        Assert.True(s.TryDequeueEffect(out var effect));
        Assert.Equal(windows[4].Handle, Assert.IsType<ActivateWindowEffect>(effect).Window.Handle);
    }

    [Fact]
    public void Pinned_windows_move_up_after_the_protected_pair()
    {
        var windows = TestData.Windows(6);
        windows[5].IsPinned = true;
        var ordered = EntryBuilder.ApplyPins(windows);
        Assert.Equal(windows[5].Handle, ordered[2].Handle);
        Assert.Equal(windows[0].Handle, ordered[0].Handle);
    }

    [Fact]
    public void Same_app_mode_only_contains_the_foreground_app()
    {
        var windows = TestData.Windows(6, i => i % 2 == 0 ? "code.exe" : "chrome.exe");
        var s = new SwitcherSession(windows, new SessionOptions { Mode = SessionMode.SameApp });
        Assert.All(s.Entries, e => Assert.Equal("code.exe", e.App.ExecutableName));
        Assert.Equal(3, s.Entries.Count);
    }

    [Theory]
    [InlineData("vsc", "Visual Studio Code")]
    [InlineData("code", "Visual Studio Code")]
    [InlineData("tele", "Telegram")]
    [InlineData("youtube", "Google Chrome")]
    public void Fuzzy_matcher_finds_by_app_title_and_acronym(string query, string expectedApp)
    {
        var windows = TestData.Windows(8);
        var best = windows.OrderByDescending(w => FuzzyMatcher.Score(query, w)).First();
        Assert.Equal(expectedApp, best.App.DisplayName);
        Assert.True(FuzzyMatcher.Score(query, best) > 0.5f);
    }

    [Fact]
    public void Fuzzy_matcher_rejects_unrelated_queries()
    {
        var w = TestData.Windows(1)[0];
        Assert.Equal(0f, FuzzyMatcher.Score("zzq", w));
    }
}
