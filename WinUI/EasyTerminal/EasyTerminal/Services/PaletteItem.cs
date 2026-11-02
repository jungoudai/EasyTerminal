using EasyTerminal.Core.Palette;

namespace EasyTerminal.Services;

/// <summary>One row of the command palette.</summary>
public sealed class PaletteItem
{
    public required string Glyph { get; init; }
    public required string Title { get; init; }
    public string Detail { get; init; } = "";
    public bool HasDetail => Detail.Length > 0;
    public required string Category { get; init; }
    public required Action Run { get; init; }
    /// <summary>Commands are what a leading ">" narrows the list to, as in VS Code.</summary>
    public bool IsCommand { get; init; }

    public string SearchText => $"{Title} {Detail} {Category}";

    /// <summary>Filters and ranks items; the sort is stable, so ties keep sessions and recents first.</summary>
    public static List<PaletteItem> Filter(IEnumerable<PaletteItem> items, string query)
    {
        bool commandsOnly = query.StartsWith('>');
        string terms = commandsOnly ? query[1..].Trim() : query.Trim();
        var pool = commandsOnly ? items.Where(i => i.IsCommand) : items;
        return pool
            .Select(item => (Item: item, Score: FuzzyMatcher.Score(terms, item.SearchText)))
            .Where(x => x.Score != null)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Item)
            .Take(50)
            .ToList();
    }
}
