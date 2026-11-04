using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EasyTerminal.Core.Terminal;

/// <summary>
/// A terminal color scheme as plain data: colors are "#RRGGBB". Reads and writes the Windows Terminal
/// scheme format, so schemes from windowsterminalthemes.dev or a settings.json can be pasted in.
/// </summary>
public sealed class TerminalSchemeData
{
    public static readonly string[] AnsiNames =
    {
        "black", "red", "green", "yellow", "blue", "purple", "cyan", "white",
        "brightBlack", "brightRed", "brightGreen", "brightYellow", "brightBlue", "brightPurple", "brightCyan", "brightWhite",
    };

    public string Name { get; set; } = "";
    public string Background { get; set; } = "#0C0C0C";
    public string Foreground { get; set; } = "#CCCCCC";
    public string Cursor { get; set; } = "#FFFFFF";
    public string Selection { get; set; } = "#444444";
    /// <summary>The sixteen ANSI colors in the usual order: black to white, then their bright forms.</summary>
    public List<string> Ansi { get; set; } = new();

    /// <summary>
    /// Reads one scheme object, an array of them, or an object with a "schemes" array (a whole settings.json).
    /// Missing colors fall back to Campbell's. Throws <see cref="FormatException"/> when nothing usable is found.
    /// </summary>
    public static List<TerminalSchemeData> ParseWindowsTerminal(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            throw new FormatException("不是有效的 JSON：" + ex.Message, ex);
        }

        IEnumerable<JsonNode?> candidates = root switch
        {
            JsonArray array => array,
            JsonObject obj when obj["schemes"] is JsonArray schemes => schemes,
            JsonObject obj => new[] { obj },
            _ => Array.Empty<JsonNode?>(),
        };

        var result = new List<TerminalSchemeData>();
        foreach (var node in candidates.OfType<JsonObject>())
        {
            string? Get(string key) => node[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            if (Get("background") == null && Get("foreground") == null) continue;
            var campbell = Campbell();
            var scheme = new TerminalSchemeData
            {
                Name = (Get("name") ?? "").Trim(),
                Background = Normalize(Get("background")) ?? campbell.Background,
                Foreground = Normalize(Get("foreground")) ?? campbell.Foreground,
                Cursor = Normalize(Get("cursorColor")) ?? Normalize(Get("foreground")) ?? campbell.Cursor,
                Selection = Normalize(Get("selectionBackground")) ?? campbell.Selection,
            };
            for (int i = 0; i < AnsiNames.Length; i++) scheme.Ansi.Add(Normalize(Get(AnsiNames[i])) ?? campbell.Ansi[i]);
            if (scheme.Name.Length == 0) scheme.Name = "导入的配色";
            result.Add(scheme);
        }
        if (result.Count == 0) throw new FormatException("没有找到配色。需要包含 background、foreground 和 16 种颜色的 Windows Terminal 配色。");
        return result;
    }

    public string ToWindowsTerminalJson()
    {
        var obj = new JsonObject
        {
            ["name"] = Name,
            ["background"] = Background,
            ["foreground"] = Foreground,
            ["cursorColor"] = Cursor,
            ["selectionBackground"] = Selection,
        };
        for (int i = 0; i < AnsiNames.Length; i++) obj[AnsiNames[i]] = i < Ansi.Count ? Ansi[i] : Campbell().Ansi[i];
        return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>"#RGB", "#RRGGBB" or "#RRGGBBAA" (alpha dropped) as "#RRGGBB"; null when it is not a color.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string hex = value.Trim().TrimStart('#');
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => new string(c, 2)));
        if (hex.Length == 8) hex = hex[..6];
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)) return null;
        return "#" + hex.ToUpperInvariant();
    }

    /// <summary>The color as 0xRRGGBB; black when unreadable.</summary>
    public static int ToRgb(string value) =>
        Normalize(value) is { } hex ? int.Parse(hex[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : 0;

    public static TerminalSchemeData Campbell() => new()
    {
        Name = "Campbell",
        Background = "#0C0C0C",
        Foreground = "#CCCCCC",
        Cursor = "#FFFFFF",
        Selection = "#444444",
        Ansi = new List<string>
        {
            "#0C0C0C", "#C50F1F", "#13A10E", "#C19C00", "#0037DA", "#881798", "#3A96DD", "#CCCCCC",
            "#767676", "#E74856", "#16C60C", "#F9F1A5", "#3B78FF", "#B4009E", "#61D6D6", "#F2F2F2",
        },
    };
}
