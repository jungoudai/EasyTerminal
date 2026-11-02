using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace EasyTerminal.Core.Terminal;

[Flags]
public enum CellFlags : ushort
{
    None = 0,
    Bold = 1 << 0,
    Faint = 1 << 1,
    Italic = 1 << 2,
    Underline = 1 << 3,
    Blink = 1 << 4,
    Inverse = 1 << 5,
    Invisible = 1 << 6,
    Strike = 1 << 7,
    WideTail = 1 << 8,
}

/// <summary>Colors are tagged ints: 0 is the default, then 0x01_0000nn palette or 0x02_rrggbb true color.</summary>
public static class TermColor
{
    public const int Default = 0;
    public static int Palette(int index) => 0x01000000 | (index & 0xFF);
    public static int Rgb(int r, int g, int b) => 0x02000000 | ((r & 0xFF) << 16) | ((g & 0xFF) << 8) | (b & 0xFF);
    public static bool IsPalette(int c) => (c >> 24) == 1;
    public static bool IsRgb(int c) => (c >> 24) == 2;
}

[StructLayout(LayoutKind.Sequential)]
public struct Cell
{
    /// <summary>Unicode scalar, 0 for blank, or a <see cref="ClusterTable"/> id for grapheme clusters.</summary>
    public int Codepoint;
    public int Foreground;
    public int Background;
    public CellFlags Flags;

    public readonly bool IsBlank => (Codepoint == 0 || Codepoint == ' ') && Background == 0 && (Flags & (CellFlags.Inverse | CellFlags.Underline | CellFlags.Strike | CellFlags.WideTail)) == 0;
}

/// <summary>Interns multi-codepoint graphemes so a cell can stay a fixed 16 bytes.</summary>
public static class ClusterTable
{
    public const int Base = 0x200000;
    private const int MaxEntries = 1 << 16;
    private static readonly ConcurrentDictionary<string, int> Ids = new();
    private static readonly List<string> Values = new();

    public static bool IsCluster(int codepoint) => codepoint >= Base;

    public static int Intern(string cluster)
    {
        if (Ids.TryGetValue(cluster, out var id)) return id;
        lock (Values)
        {
            if (Ids.TryGetValue(cluster, out id)) return id;
            if (Values.Count >= MaxEntries) return char.ConvertToUtf32(cluster, 0);
            id = Base + Values.Count;
            Values.Add(cluster);
            Ids[cluster] = id;
            return id;
        }
    }

    public static string Get(int id)
    {
        lock (Values) return Values[id - Base];
    }

    public static string ToText(int codepoint)
    {
        if (codepoint <= 0) return " ";
        if (IsCluster(codepoint)) return Get(codepoint);
        return char.ConvertFromUtf32(codepoint);
    }

    public static void Append(StringBuilder sb, int codepoint)
    {
        if (codepoint <= 0) sb.Append(' ');
        else if (codepoint < 0x10000) sb.Append((char)codepoint);
        else sb.Append(ToText(codepoint));
    }
}

public static class CharWidth
{
    // East Asian Wide/Fullwidth plus emoji presentation blocks, sorted for binary search.
    private static readonly (int Start, int End)[] Wide =
    {
        (0x1100, 0x115F), (0x231A, 0x231B), (0x2329, 0x232A), (0x23E9, 0x23EC), (0x23F0, 0x23F0), (0x23F3, 0x23F3),
        (0x25FD, 0x25FE), (0x2614, 0x2615), (0x2648, 0x2653), (0x267F, 0x267F), (0x2693, 0x2693), (0x26A1, 0x26A1),
        (0x26AA, 0x26AB), (0x26BD, 0x26BE), (0x26C4, 0x26C5), (0x26CE, 0x26CE), (0x26D4, 0x26D4), (0x26EA, 0x26EA),
        (0x26F2, 0x26F3), (0x26F5, 0x26F5), (0x26FA, 0x26FA), (0x26FD, 0x26FD), (0x2705, 0x2705), (0x270A, 0x270B),
        (0x2728, 0x2728), (0x274C, 0x274C), (0x274E, 0x274E), (0x2753, 0x2755), (0x2757, 0x2757), (0x2795, 0x2797),
        (0x27B0, 0x27B0), (0x27BF, 0x27BF), (0x2B1B, 0x2B1C), (0x2B50, 0x2B50), (0x2B55, 0x2B55), (0x2E80, 0x303E),
        (0x3041, 0x33FF), (0x3400, 0x4DBF), (0x4E00, 0x9FFF), (0xA000, 0xA4CF), (0xA960, 0xA97F), (0xAC00, 0xD7A3),
        (0xF900, 0xFAFF), (0xFE10, 0xFE19), (0xFE30, 0xFE6F), (0xFF01, 0xFF60), (0xFFE0, 0xFFE6), (0x16FE0, 0x18AFF),
        (0x1B000, 0x1B2FF), (0x1F004, 0x1F004), (0x1F0CF, 0x1F0CF), (0x1F18E, 0x1F18E), (0x1F191, 0x1F19A),
        (0x1F1E6, 0x1F202), (0x1F210, 0x1F23B), (0x1F240, 0x1F248), (0x1F250, 0x1F251), (0x1F260, 0x1F265),
        (0x1F300, 0x1F320), (0x1F32D, 0x1F335), (0x1F337, 0x1F37C), (0x1F37E, 0x1F393), (0x1F3A0, 0x1F3CA),
        (0x1F3CF, 0x1F3D3), (0x1F3E0, 0x1F3F0), (0x1F3F4, 0x1F3F4), (0x1F3F8, 0x1F43E), (0x1F440, 0x1F440),
        (0x1F442, 0x1F4FC), (0x1F4FF, 0x1F53D), (0x1F54B, 0x1F54E), (0x1F550, 0x1F567), (0x1F57A, 0x1F57A),
        (0x1F595, 0x1F596), (0x1F5A4, 0x1F5A4), (0x1F5FB, 0x1F64F), (0x1F680, 0x1F6C5), (0x1F6CC, 0x1F6CC),
        (0x1F6D0, 0x1F6D2), (0x1F6D5, 0x1F6D7), (0x1F6DC, 0x1F6DF), (0x1F6EB, 0x1F6EC), (0x1F6F4, 0x1F6FC),
        (0x1F7E0, 0x1F7EB), (0x1F7F0, 0x1F7F0), (0x1F90C, 0x1F93A), (0x1F93C, 0x1F945), (0x1F947, 0x1F9FF),
        (0x1FA70, 0x1FAFF), (0x20000, 0x3FFFD),
    };

    /// <summary>Returns 0 for combining marks, 2 for wide characters and 1 otherwise.</summary>
    public static int Of(int cp)
    {
        if (cp < 0x300) return 1;
        if (cp is 0x200D or 0x200C or 0xFE0F or 0xFE0E) return 0;
        if (cp >= 0x1100)
        {
            int lo = 0, hi = Wide.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                var (start, end) = Wide[mid];
                if (cp < start) hi = mid - 1;
                else if (cp > end) lo = mid + 1;
                else return 2;
            }
        }
        var category = CharUnicodeInfo.GetUnicodeCategory(cp);
        return category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format ? 0 : 1;
    }
}
