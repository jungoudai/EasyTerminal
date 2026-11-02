using System.Text.RegularExpressions;

namespace EasyTerminal.Core.Snippets;

/// <summary>{{name}} placeholders in a snippet, which are filled in each time it runs.</summary>
public static partial class SnippetTemplate
{
    [GeneratedRegex(@"\{\{\s*([\p{L}\p{N}_-]+)\s*\}\}")]
    private static partial Regex Placeholder();

    public static IReadOnlyList<string> Variables(string body) =>
        Placeholder().Matches(body).Select(m => m.Groups[1].Value).Distinct().ToList();

    public static string Fill(string body, IReadOnlyDictionary<string, string> values) =>
        Placeholder().Replace(body, m => values.TryGetValue(m.Groups[1].Value, out var value) ? value : m.Value);

    /// <summary>A remote command that runs the snippet at login and then carries on into the user's shell.</summary>
    public static string StartupCommand(string body) => body.Replace("\r\n", "\n").TrimEnd() + "\nexec \"${SHELL:-sh}\" -l";
}
