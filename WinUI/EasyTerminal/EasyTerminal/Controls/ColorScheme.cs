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

    public static ColorScheme Find(string name) => All.FirstOrDefault(s => s.Name == name) ?? All[0];
}
