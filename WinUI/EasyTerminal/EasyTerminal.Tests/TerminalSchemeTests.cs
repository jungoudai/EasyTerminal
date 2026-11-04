using EasyTerminal.Core.Terminal;

namespace EasyTerminal.Tests;

public class TerminalSchemeTests
{
    private const string Dracula = """
        {
            // Windows Terminal allows comments and trailing commas
            "name": "Dracula",
            "background": "#282A36",
            "foreground": "#F8F8F2",
            "cursorColor": "#F8F8F2",
            "selectionBackground": "#44475A",
            "black": "#21222C", "red": "#FF5555", "green": "#50FA7B", "yellow": "#F1FA8C",
            "blue": "#BD93F9", "purple": "#FF79C6", "cyan": "#8BE9FD", "white": "#F8F8F2",
            "brightBlack": "#6272A4", "brightRed": "#FF6E6E", "brightGreen": "#69FF94", "brightYellow": "#FFFFA5",
            "brightBlue": "#D6ACFF", "brightPurple": "#FF92DF", "brightCyan": "#A4FFFF", "brightWhite": "#FFFFFF",
        }
        """;

    [Fact]
    public void ReadsAWindowsTerminalScheme()
    {
        var scheme = Assert.Single(TerminalSchemeData.ParseWindowsTerminal(Dracula));
        Assert.Equal("Dracula", scheme.Name);
        Assert.Equal("#282A36", scheme.Background);
        Assert.Equal("#44475A", scheme.Selection);
        Assert.Equal(16, scheme.Ansi.Count);
        Assert.Equal("#FF5555", scheme.Ansi[1]);
        Assert.Equal("#FFFFFF", scheme.Ansi[15]);
    }

    [Fact]
    public void ReadsTheSchemesOfAWholeSettingsFile()
    {
        string settings = """{ "profiles": {}, "schemes": [ { "name": "A", "background": "#000", "foreground": "#fff" }, { "name": "B", "background": "#111111", "foreground": "#eeeeee" } ] }""";
        var schemes = TerminalSchemeData.ParseWindowsTerminal(settings);
        Assert.Equal(new[] { "A", "B" }, schemes.Select(s => s.Name));
        Assert.Equal("#000000", schemes[0].Background);
        // Missing colors come from Campbell, and the cursor follows the foreground.
        Assert.Equal("#FFFFFF", schemes[0].Cursor);
        Assert.Equal(TerminalSchemeData.Campbell().Ansi, schemes[0].Ansi);
    }

    [Fact]
    public void RoundTripsThroughJson()
    {
        var original = TerminalSchemeData.ParseWindowsTerminal(Dracula)[0];
        var again = Assert.Single(TerminalSchemeData.ParseWindowsTerminal(original.ToWindowsTerminalJson()));
        Assert.Equal(original.Name, again.Name);
        Assert.Equal(original.Background, again.Background);
        Assert.Equal(original.Ansi, again.Ansi);
    }

    [Theory]
    [InlineData("#abc", "#AABBCC")]
    [InlineData("12ab34", "#12AB34")]
    [InlineData("#12AB34FF", "#12AB34")]
    [InlineData("red", null)]
    [InlineData("", null)]
    public void NormalizesColors(string input, string? expected) => Assert.Equal(expected, TerminalSchemeData.Normalize(input));

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "name": "no colors" }""")]
    [InlineData("[]")]
    public void RejectsTextWithoutSchemes(string json) => Assert.Throws<FormatException>(() => TerminalSchemeData.ParseWindowsTerminal(json));
}
