using EasyTerminal.Core.Terminal;
using Windows.UI;

namespace EasyTerminal.Controls;

public sealed class ColorScheme
{
    public required string Name { get; init; }
    public required Color Background { get; init; }
    public required Color Foreground { get; init; }
    public required Color Cursor { get; init; }
    public required Color Selection { get; init; }
    public required Color[] Ansi { get; init; }

    private Color[]? _palette;

    /// <summary>The 256-color palette: the scheme's 16 colors, then the standard cube and gray ramp.</summary>
    public Color[] Palette => _palette ??= BuildPalette();

    private Color[] BuildPalette()
    {
        var palette = new Color[256];
        Ansi.CopyTo(palette, 0);
        int[] levels = { 0, 95, 135, 175, 215, 255 };
        for (int i = 0; i < 216; i++)
            palette[16 + i] = Color.FromArgb(255, (byte)levels[i / 36], (byte)levels[i / 6 % 6], (byte)levels[i % 6]);
        for (int i = 0; i < 24; i++)
        {
            byte gray = (byte)(8 + i * 10);
            palette[232 + i] = Color.FromArgb(255, gray, gray, gray);
        }
        return palette;
    }

    public Color Resolve(int color, bool isForeground, TerminalBuffer buffer)
    {
        if (color == TermColor.Default) return isForeground ? Foreground : Background;
        if (TermColor.IsPalette(color))
        {
            int index = color & 0xFF;
            int custom = buffer.ResolvePalette(index);
            if (custom != 0) return FromRgb(custom);
            return Palette[index];
        }
        return FromRgb(color);
    }

    private static Color FromRgb(int rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private static Color Hex(uint rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private static Color[] Colors(params uint[] values) => values.Select(Hex).ToArray();

    public static readonly IReadOnlyList<ColorScheme> All = new[]
    {
        new ColorScheme
        {
            Name = "One Half Dark",
            Background = Hex(0x1B1F27), Foreground = Hex(0xDCDFE4), Cursor = Hex(0xA3B3CC), Selection = Hex(0x3E4757),
            Ansi = Colors(0x282C34, 0xE06C75, 0x98C379, 0xE5C07B, 0x61AFEF, 0xC678DD, 0x56B6C2, 0xDCDFE4,
                          0x5A6374, 0xE06C75, 0x98C379, 0xE5C07B, 0x61AFEF, 0xC678DD, 0x56B6C2, 0xFFFFFF),
        },
        new ColorScheme
        {
            Name = "Campbell",
            Background = Hex(0x0C0C0C), Foreground = Hex(0xCCCCCC), Cursor = Hex(0xFFFFFF), Selection = Hex(0x444444),
            Ansi = Colors(0x0C0C0C, 0xC50F1F, 0x13A10E, 0xC19C00, 0x0037DA, 0x881798, 0x3A96DD, 0xCCCCCC,
                          0x767676, 0xE74856, 0x16C60C, 0xF9F1A5, 0x3B78FF, 0xB4009E, 0x61D6D6, 0xF2F2F2),
        },
        new ColorScheme
        {
            Name = "Tokyo Night",
            Background = Hex(0x1A1B26), Foreground = Hex(0xC0CAF5), Cursor = Hex(0xC0CAF5), Selection = Hex(0x33467C),
            Ansi = Colors(0x15161E, 0xF7768E, 0x9ECE6A, 0xE0AF68, 0x7AA2F7, 0xBB9AF7, 0x7DCFFF, 0xA9B1D6,
                          0x414868, 0xF7768E, 0x9ECE6A, 0xE0AF68, 0x7AA2F7, 0xBB9AF7, 0x7DCFFF, 0xC0CAF5),
        },
        new ColorScheme
        {
            Name = "Catppuccin Mocha",
            Background = Hex(0x1E1E2E), Foreground = Hex(0xCDD6F4), Cursor = Hex(0xF5E0DC), Selection = Hex(0x45475A),
            Ansi = Colors(0x45475A, 0xF38BA8, 0xA6E3A1, 0xF9E2AF, 0x89B4FA, 0xF5C2E7, 0x94E2D5, 0xBAC2DE,
                          0x585B70, 0xF38BA8, 0xA6E3A1, 0xF9E2AF, 0x89B4FA, 0xF5C2E7, 0x94E2D5, 0xA6ADC8),
        },
        new ColorScheme
        {
            Name = "Solarized Light",
            Background = Hex(0xFDF6E3), Foreground = Hex(0x586E75), Cursor = Hex(0x586E75), Selection = Hex(0xEEE8D5),
            Ansi = Colors(0x073642, 0xDC322F, 0x859900, 0xB58900, 0x268BD2, 0xD33682, 0x2AA198, 0xEEE8D5,
                          0x002B36, 0xCB4B16, 0x586E75, 0x657B83, 0x839496, 0x6C71C4, 0x93A1A1, 0xFDF6E3),
        },
    };

    // ---- Mica ----------------------------------------------------------------------------------

    public const string MicaFollow = "Mica（跟随主题）";

    /// <summary>Colors for a terminal that lets the window's Mica show through; the background is only a tint.</summary>
    public static readonly ColorScheme MicaDark = new()
    {
        Name = "Mica 深色",
        Background = Hex(0x202020), Foreground = Hex(0xE4E4E4), Cursor = Hex(0xFFFFFF), Selection = Hex(0x3D5A80),
        Ansi = Colors(0x2B2B2B, 0xFF6B6B, 0x7FD88F, 0xF2C66D, 0x6CB6FF, 0xD69CFF, 0x5FD7E5, 0xD8D8D8,
                      0x7A7A7A, 0xFF8787, 0x98E8A6, 0xFFD98A, 0x8CC8FF, 0xE2B4FF, 0x8AE6F0, 0xFFFFFF),
        IsMica = true,
    };

    public static readonly ColorScheme MicaLight = new()
    {
        Name = "Mica 浅色",
        Background = Hex(0xF3F3F3), Foreground = Hex(0x1F1F1F), Cursor = Hex(0x1F1F1F), Selection = Hex(0xBBD6F7),
        Ansi = Colors(0x383A42, 0xC4314B, 0x2E8B3E, 0x9A6700, 0x0060C0, 0x8A2BB8, 0x0A7E8C, 0x7A7A7A,
                      0x5C5F66, 0xD9405A, 0x3A9E4C, 0xB37A00, 0x1A75D1, 0xA040CC, 0x1590A0, 0x9A9A9A),
        IsMica = true,
    };

    /// <summary>True for schemes meant to be shown with a transparent background over Mica.</summary>
    public bool IsMica { get; init; }

    /// <summary>True for schemes the user imported or edited.</summary>
    public bool IsCustom { get; init; }

    /// <summary>Schemes from settings; replaced whenever the user imports, edits or deletes one.</summary>
    public static IReadOnlyList<ColorScheme> Custom { get; set; } = Array.Empty<ColorScheme>();

    /// <summary>Every scheme name the settings and palette offer, built-in first.</summary>
    public static IEnumerable<string> Names =>
        All.Select(s => s.Name).Concat(new[] { MicaFollow, MicaDark.Name, MicaLight.Name }).Concat(Custom.Select(s => s.Name));

    public static bool IsMicaName(string name) => name == MicaFollow || name == MicaDark.Name || name == MicaLight.Name;

    /// <summary>The scheme for a name; "follow theme" picks the Mica variant matching the app's theme.</summary>
    public static ColorScheme Find(string name, bool darkTheme = true)
    {
        if (name == MicaFollow) return darkTheme ? MicaDark : MicaLight;
        if (name == MicaDark.Name) return MicaDark;
        if (name == MicaLight.Name) return MicaLight;
        return Custom.FirstOrDefault(s => s.Name == name) ?? All.FirstOrDefault(s => s.Name == name) ?? All[0];
    }

    public static ColorScheme FromData(TerminalSchemeData data)
    {
        static Color Of(string value) => FromRgb(TerminalSchemeData.ToRgb(value));
        var ansi = new Color[16];
        for (int i = 0; i < 16; i++) ansi[i] = i < data.Ansi.Count ? Of(data.Ansi[i]) : All[1].Ansi[i];
        return new ColorScheme
        {
            Name = data.Name,
            Background = Of(data.Background),
            Foreground = Of(data.Foreground),
            Cursor = Of(data.Cursor),
            Selection = Of(data.Selection),
            Ansi = ansi,
            IsCustom = true,
        };
    }

    public TerminalSchemeData ToData()
    {
        static string Of(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        return new TerminalSchemeData
        {
            Name = Name,
            Background = Of(Background),
            Foreground = Of(Foreground),
            Cursor = Of(Cursor),
            Selection = Of(Selection),
            Ansi = Ansi.Select(Of).ToList(),
        };
    }
}
