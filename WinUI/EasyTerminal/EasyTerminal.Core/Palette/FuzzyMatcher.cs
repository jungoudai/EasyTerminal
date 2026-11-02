namespace EasyTerminal.Core.Palette;

/// <summary>Command-palette matching: every space-separated term must appear as a subsequence.</summary>
public static class FuzzyMatcher
{
    /// <summary>Returns a score where higher is better, or null when the query does not match.</summary>
    public static int? Score(string query, string text)
    {
        int total = 0;
        foreach (var term in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (ScoreTerm(term, text) is not { } score) return null;
            total += score;
        }
        return total;
    }

    private static int? ScoreTerm(string term, string text)
    {
        int exact = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (exact >= 0)
        {
            bool atWordStart = exact == 0 || !char.IsLetterOrDigit(text[exact - 1]);
            return 1000 + term.Length * 10 + (exact == 0 ? 500 : 0) + (atWordStart ? 200 : 0) - exact;
        }

        int score = 0, position = 0, run = 0;
        foreach (char c in term)
        {
            int found = -1;
            for (int i = position; i < text.Length; i++)
            {
                if (char.ToLowerInvariant(text[i]) == char.ToLowerInvariant(c))
                {
                    found = i;
                    break;
                }
            }
            if (found < 0) return null;
            run = found == position && position > 0 ? run + 1 : 0;
            bool wordStart = found == 0 || !char.IsLetterOrDigit(text[found - 1]);
            score += 1 + run * 3 + (wordStart ? 8 : 0);
            position = found + 1;
        }
        return score;
    }
}
