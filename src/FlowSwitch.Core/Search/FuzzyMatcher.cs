using System.Globalization;
using System.Text;
using FlowSwitch.Core.Model;

namespace FlowSwitch.Core.Search;

/// <summary>
/// Type-to-filter matching over app name, window title and executable name.
/// Ranked: exact → prefix → word start → acronym → substring → subsequence.
/// Also forgives a wrong keyboard layout ("вшыс" finds Discord).
/// </summary>
public static class FuzzyMatcher
{
    private const string RussianKeys = "йцукенгшщзхъфывапролджэячсмитьбюё";
    private const string LatinKeys = "qwertyuiop[]asdfghjkl;'zxcvbnm,.`";

    public static float Score(string query, WindowInfo window)
    {
        string q = Normalize(query);
        if (q.Length == 0) return 1f;

        float best = ScoreTokens(q, window);
        string swapped = SwapLayout(q);
        if (swapped != q) best = MathF.Max(best, ScoreTokens(swapped, window) * 0.92f);
        return best;
    }

    private static float ScoreTokens(string q, WindowInfo w)
    {
        string app = Normalize(w.App.DisplayName);
        string title = Normalize(w.Title);
        string exe = Normalize(Path.GetFileNameWithoutExtension(w.App.ExecutableName));

        var tokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return 1f;

        float total = 0f;
        foreach (string token in tokens)
        {
            float s = MathF.Max(ScoreField(token, app) * 1.0f,
                      MathF.Max(ScoreField(token, exe) * 0.9f, ScoreField(token, title) * 0.85f));
            if (s <= 0f) return 0f; // every token must match somewhere
            total += s;
        }
        // Whole-query bonus when the full phrase appears in one field.
        if (tokens.Length > 1 && (app.Contains(q) || title.Contains(q))) total += 0.2f * tokens.Length;
        return Math.Min(1f, total / tokens.Length);
    }

    public static float ScoreField(string q, string field)
    {
        if (field.Length == 0 || q.Length == 0) return 0f;
        if (field == q) return 1f;
        if (field.StartsWith(q, StringComparison.Ordinal)) return 0.9f + 0.05f * q.Length / field.Length;

        // Word-start match: "code" in "visual studio code", "tube" in "youtube"? (no — word start only)
        int idx = 0;
        while ((idx = field.IndexOf(q, idx, StringComparison.Ordinal)) >= 0)
        {
            if (idx == 0 || IsSeparator(field[idx - 1])) return 0.8f;
            idx++;
        }

        if (MatchesAcronym(q, field)) return 0.72f;
        if (field.Contains(q, StringComparison.Ordinal)) return 0.62f;

        // Subsequence: all characters in order; rewarded for compactness.
        int fi = 0, first = -1, last = -1;
        foreach (char c in q)
        {
            fi = field.IndexOf(c, fi);
            if (fi < 0) return 0f;
            if (first < 0) first = fi;
            last = fi;
            fi++;
        }
        if (q.Length < 2) return 0f;
        float span = last - first + 1;
        return 0.3f + 0.2f * (q.Length / span);
    }

    private static bool MatchesAcronym(string q, string field)
    {
        if (q.Length < 2) return false;
        int qi = 0;
        for (int i = 0; i < field.Length && qi < q.Length; i++)
        {
            bool wordStart = i == 0 || IsSeparator(field[i - 1]);
            if (wordStart && field[i] == q[qi]) qi++;
        }
        return qi == q.Length;
    }

    private static bool IsSeparator(char c) => c is ' ' or '-' or '_' or '.' or '—' or '–' or '|' or '/' or '\\' or ':' or '(' or '[';

    public static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        string decomposed = s.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            // Keep Cyrillic "й" intact after decomposition would split it into и + breve.
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                if (c == '̆' && sb.Length > 0 && sb[^1] == 'и') { sb[^1] = 'й'; }
                continue;
            }
            sb.Append(c == 'ё' ? 'е' : c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Re-types the query as if it was entered on the other keyboard layout (RU ↔ EN).</summary>
    public static string SwapLayout(string q)
    {
        var sb = new StringBuilder(q.Length);
        bool changed = false;
        foreach (char c in q)
        {
            int r = RussianKeys.IndexOf(c);
            if (r >= 0) { sb.Append(LatinKeys[r]); changed = true; continue; }
            sb.Append(c);
        }
        return changed ? sb.ToString() : q;
    }
}
