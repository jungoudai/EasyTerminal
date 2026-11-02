using System.Diagnostics;
using System.Text;
using EasyTerminal.Core.Terminal;
using Xunit.Abstractions;

namespace EasyTerminal.Tests;

public class TerminalBufferTests(ITestOutputHelper output)
{
    private static string Row(TerminalBuffer t, int row)
    {
        var sb = new StringBuilder();
        foreach (var cell in t.ViewportLine(row).Cells)
        {
            if ((cell.Flags & CellFlags.WideTail) != 0) continue;
            ClusterTable.Append(sb, cell.Codepoint);
        }
        return sb.ToString().TrimEnd();
    }

    [Fact]
    public void PrintsAndWraps()
    {
        var t = new TerminalBuffer(5, 3);
        t.Write("helloworld");
        Assert.Equal("hello", Row(t, 0));
        Assert.Equal("world", Row(t, 1));
        Assert.True(t.ViewportLine(0).Wrapped);
        Assert.Equal(4, t.CursorCol); // Pending wrap keeps the cursor on the last column.
    }

    [Fact]
    public void Utf8AcrossChunkBoundaries()
    {
        var t = new TerminalBuffer(20, 3);
        var bytes = Encoding.UTF8.GetBytes("中文é");
        foreach (var b in bytes) t.Write(new[] { b });
        Assert.Equal("中文é", Row(t, 0));
        Assert.Equal(5, t.CursorCol);
    }

    [Fact]
    public void WideCharacterWrapsWholeAtLineEnd()
    {
        var t = new TerminalBuffer(3, 2);
        t.Write("ab中");
        Assert.Equal("ab", Row(t, 0));
        Assert.Equal("中", Row(t, 1));
    }

    [Fact]
    public void OverwritingHalfAWideCharacterBlanksTheOtherHalf()
    {
        var t = new TerminalBuffer(10, 2);
        t.Write("中\x1b[1;2Hx");
        Assert.Equal(" x", Row(t, 0));
    }

    [Fact]
    public void CombiningMarkAttachesToPreviousCell()
    {
        var t = new TerminalBuffer(10, 2);
        t.Write("éx");
        Assert.Equal("éx", Row(t, 0));
        Assert.Equal(2, t.CursorCol);
    }

    [Fact]
    public void SplitEscapeSequenceAcrossWrites()
    {
        var t = new TerminalBuffer(10, 5);
        t.Write("\x1b[");
        t.Write("3;");
        t.Write("4Hx");
        Assert.Equal("   x", Row(t, 2));
    }

    [Fact]
    public void SgrParsesSemicolonAndColonColors()
    {
        var t = new TerminalBuffer(10, 2);
        t.Write("\x1b[1;38;2;1;2;3;48;5;9mA\x1b[0;38:2::4:5:6mB\x1b[0mC");
        var cells = t.ViewportLine(0).Cells;
        Assert.Equal(TermColor.Rgb(1, 2, 3), cells[0].Foreground);
        Assert.Equal(TermColor.Palette(9), cells[0].Background);
        Assert.True((cells[0].Flags & CellFlags.Bold) != 0);
        Assert.Equal(TermColor.Rgb(4, 5, 6), cells[1].Foreground);
        Assert.Equal(0, cells[2].Foreground);
    }

    [Fact]
    public void ScrollbackKeepsOldLinesAndCapsAtLimit()
    {
        var t = new TerminalBuffer(10, 3, 5);
        for (int i = 0; i < 20; i++) t.Write($"line{i}\r\n");
        Assert.Equal(5, t.ScrollbackLines);
        Assert.Equal("line19", Row(t, 1));
        t.ScrollViewport(5);
        Assert.Equal("line13", Row(t, 0));
    }

    [Fact]
    public void ScrollRegionDoesNotTouchScrollback()
    {
        var t = new TerminalBuffer(10, 5, 100);
        t.Write("top\r\n\x1b[2;4r\x1b[2;1Ha\r\nb\r\nc\r\nd\r\ne");
        Assert.Equal("top", Row(t, 0));
        Assert.Equal(0, t.ScrollbackLines);
        Assert.Equal("c", Row(t, 1));
        Assert.Equal("e", Row(t, 3));
    }

    [Fact]
    public void AltScreenRestoresPrimaryAndCursor()
    {
        var t = new TerminalBuffer(10, 3);
        t.Write("shell");
        t.Write("\x1b[?1049h");
        Assert.True(t.AltScreenActive);
        Assert.Equal("", Row(t, 0));
        t.Write("vim");
        t.Write("\x1b[?1049l");
        Assert.False(t.AltScreenActive);
        Assert.Equal("shell", Row(t, 0));
        Assert.Equal(5, t.CursorCol);
    }

    [Fact]
    public void InsertDeleteAndEraseCharacters()
    {
        var t = new TerminalBuffer(10, 2);
        t.Write("abcdef\x1b[1;2H\x1b[2@");
        Assert.Equal("a  bcdef", Row(t, 0));
        t.Write("\x1b[2P");
        Assert.Equal("abcdef", Row(t, 0));
        t.Write("\x1b[1;1H\x1b[2X");
        Assert.Equal("  cdef", Row(t, 0));
        t.Write("\x1b[1;4H\x1b[K");
        Assert.Equal("  c", Row(t, 0));
    }

    [Fact]
    public void InsertAndDeleteLines()
    {
        var t = new TerminalBuffer(5, 4);
        t.Write("a\r\nb\r\nc\r\nd");
        t.Write("\x1b[2;1H\x1b[L");
        Assert.Equal("", Row(t, 1));
        Assert.Equal("b", Row(t, 2));
        Assert.Equal("c", Row(t, 3));
        t.Write("\x1b[M");
        Assert.Equal("b", Row(t, 1));
        Assert.Equal("", Row(t, 3));
    }

    [Fact]
    public void DecSpecialGraphicsMapsLineDrawing()
    {
        var t = new TerminalBuffer(10, 2);
        t.Write("\x1b(0lqk\x1b(Bx");
        Assert.Equal("┌─┐x", Row(t, 0));
    }

    [Fact]
    public void ReportsCursorPositionAndAttributes()
    {
        var t = new TerminalBuffer(10, 5);
        var replies = new List<string>();
        t.Response += b => replies.Add(Encoding.ASCII.GetString(b));
        t.Write("\x1b[3;4H\x1b[6n\x1b[c");
        Assert.Equal("\x1b[3;4R", replies[0]);
        Assert.StartsWith("\x1b[?62", replies[1]);
    }

    [Fact]
    public void OscTitleSupportsBothTerminators()
    {
        var t = new TerminalBuffer(10, 2);
        t.Write("\x1b]0;一\x07");
        Assert.Equal("一", t.Title);
        t.Write("\x1b]2;two\x1b\\after");
        Assert.Equal("two", t.Title);
        Assert.Equal("after", Row(t, 0));
    }

    [Fact]
    public void ReflowRewrapsAndKeepsCursorOnItsText()
    {
        var t = new TerminalBuffer(10, 4, 100);
        t.Write("0123456789abcde");
        t.Resize(20, 4);
        Assert.Equal("0123456789abcde", Row(t, 0));
        Assert.Equal(15, t.CursorCol);
        Assert.Equal(0, t.CursorRow);

        t.Resize(5, 4);
        Assert.Equal("01234", Row(t, 0));
        Assert.Equal("56789", Row(t, 1));
        Assert.Equal("abcde", Row(t, 2));
    }

    [Fact]
    public void ReflowDoesNotSplitWideCharacters()
    {
        var t = new TerminalBuffer(10, 3, 10);
        t.Write("中中中中中");
        t.Resize(5, 3);
        Assert.Equal("中中", Row(t, 0));
        Assert.Equal("中中", Row(t, 1));
        Assert.Equal("中", Row(t, 2));
    }

    [Fact]
    public void GetTextJoinsSoftWrappedRows()
    {
        var t = new TerminalBuffer(5, 3);
        t.Write("helloworld\r\nnext");
        long top = t.AbsoluteViewportRow(0);
        Assert.Equal("helloworld\r\nnext", t.GetText(top, 0, top + 2, 4));
    }

    [Fact]
    public void MalformedInputNeverThrows()
    {
        var random = new Random(1234);
        var t = new TerminalBuffer(40, 12, 50);
        var data = new byte[4096];
        for (int round = 0; round < 500; round++)
        {
            random.NextBytes(data);
            // Bias towards control bytes so sequences actually get parsed.
            for (int i = 0; i < data.Length; i += 3) data[i] = (byte)"\x1b[;?0123456789mHJKLMPr\n\r"[random.Next(24)];
            t.Write(data);
            if (round % 50 == 0) t.Resize(random.Next(1, 80), random.Next(1, 30));
        }
    }

    [Fact]
    public void ThroughputIsAtLeast100MegabytesPerSecond()
    {
        var line = Encoding.UTF8.GetBytes("\x1b[32mThe quick brown fox\x1b[0m jumps over the lazy dog 0123456789 abcdefghijklmnopqrstuvwxyz\r\n");
        var chunk = new byte[64 * 1024];
        for (int i = 0; i < chunk.Length; i++) chunk[i] = line[i % line.Length];
        var t = new TerminalBuffer(120, 40, 10000);
        for (int i = 0; i < 200; i++) t.Write(chunk); // warm up the JIT

        const int rounds = 8000;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < rounds; i++) t.Write(chunk);
        sw.Stop();
        double mbps = rounds * chunk.Length / 1048576.0 / sw.Elapsed.TotalSeconds;
        output.WriteLine($"{mbps:0} MB/s");
        Assert.True(mbps > 100, $"Only {mbps:0} MB/s");
    }
}

public class InputEncoderTests
{
    [Theory]
    [InlineData(TerminalKey.Up, KeyModifiers.None, false, "\x1b[A")]
    [InlineData(TerminalKey.Up, KeyModifiers.None, true, "\x1bOA")]
    [InlineData(TerminalKey.Left, KeyModifiers.Control, false, "\x1b[1;5D")]
    [InlineData(TerminalKey.F1, KeyModifiers.None, false, "\x1bOP")]
    [InlineData(TerminalKey.F5, KeyModifiers.Shift, false, "\x1b[15;2~")]
    [InlineData(TerminalKey.Delete, KeyModifiers.None, false, "\x1b[3~")]
    [InlineData(TerminalKey.Tab, KeyModifiers.Shift, false, "\x1b[Z")]
    [InlineData(TerminalKey.Backspace, KeyModifiers.None, false, "\x7f")]
    [InlineData(TerminalKey.Enter, KeyModifiers.None, false, "\r")]
    public void EncodesKeys(TerminalKey key, KeyModifiers mods, bool appCursor, string expected) =>
        Assert.Equal(expected, InputEncoder.EncodeKey(key, mods, appCursor));

    [Theory]
    [InlineData('c', (byte)3)]
    [InlineData('[', (byte)0x1B)]
    [InlineData(' ', (byte)0)]
    public void MapsControlCharacters(char c, byte expected) => Assert.Equal(expected, InputEncoder.ControlByte(c));

    [Fact]
    public void EncodesSgrMouse()
    {
        Assert.Equal("\x1b[<0;3;4M", Encoding.ASCII.GetString(InputEncoder.EncodeMouse(MouseEncodingMode.Sgr, MouseButton.Left, MouseAction.Press, 2, 3, KeyModifiers.None)));
        Assert.Equal("\x1b[<0;3;4m", Encoding.ASCII.GetString(InputEncoder.EncodeMouse(MouseEncodingMode.Sgr, MouseButton.Left, MouseAction.Release, 2, 3, KeyModifiers.None)));
        Assert.Equal("\x1b[<64;1;1M", Encoding.ASCII.GetString(InputEncoder.EncodeMouse(MouseEncodingMode.Sgr, MouseButton.WheelUp, MouseAction.Press, 0, 0, KeyModifiers.None)));
    }

    [Fact]
    public void BracketedPasteStripsEmbeddedEndMarker()
    {
        Assert.Equal("\x1b[200~a\rb\x1b[201~", InputEncoder.PrepareForPaste("a\r\n\x1b[201~b", true));
        Assert.Equal("a\rb", InputEncoder.PrepareForPaste("a\nb", false));
    }
}
