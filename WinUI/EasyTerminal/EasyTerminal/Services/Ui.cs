using EasyTerminal.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EasyTerminal.Services;

/// <summary>Small builders that keep the code-defined pages short and visually consistent.</summary>
public static class Ui
{
    public const string GlyphHome = "";
    public const string GlyphDevices = "";
    public const string GlyphComputer = "";
    public const string GlyphServer = "";
    public const string GlyphTerminal = "";
    public const string GlyphFolder = "";
    public const string GlyphFile = "";
    public const string GlyphTransfer = "";
    public const string GlyphNetwork = "";
    public const string GlyphSessions = "";
    public const string GlyphKey = "";
    public const string GlyphRefresh = "";
    public const string GlyphAdd = "";
    public const string GlyphEdit = "";
    public const string GlyphDelete = "";
    public const string GlyphPlay = "";
    public const string GlyphStop = "";
    public const string GlyphCopy = "";
    public const string GlyphContainer = "";
    public const string GlyphVm = "";
    public const string GlyphLinux = "";
    public const string GlyphUp = "";
    public const string GlyphDownload = "";
    public const string GlyphUpload = "";
    public const string GlyphClose = "";
    public const string GlyphStar = "";
    public const string GlyphStarFilled = "";
    public const string GlyphSearch = "";

    public const string GlyphRemoteDesktop = "";
    public const string GlyphOpenExternal = "";
    public const string GlyphSnippet = "";
    public const string GlyphChart = "";
    public const string GlyphAssistant = "";
    public const string GlyphForward = "";
    public const string GlyphResources = "";
    public const string GlyphGlobe = "";
    public const string GlyphSerial = "";
    public const string GlyphAccept = "";
    public const string GlyphSplit = "";
    public const string GlyphBroadcast = "";
    public const string GlyphMore = "";
    public const string GlyphRename = "";
    public const string GlyphLog = "";

    public static string GlyphFor(ConnectionProtocol protocol) => protocol switch
    {
        ConnectionProtocol.Ssh => GlyphTerminal,
        ConnectionProtocol.Sftp => GlyphTransfer,
        ConnectionProtocol.Mosh => GlyphNetwork,
        ConnectionProtocol.Rdp => GlyphRemoteDesktop,
        ConnectionProtocol.Telnet => GlyphGlobe,
        ConnectionProtocol.Serial => GlyphSerial,
        ConnectionProtocol.Docker => GlyphContainer,
        ConnectionProtocol.Proxmox => GlyphVm,
        ConnectionProtocol.Monitor => GlyphChart,
        ConnectionProtocol.Tools => "",
        ConnectionProtocol.Network => "",
        _ => GlyphSessions,
    };

    public static string GlyphFor(LocalKind kind) => kind switch
    {
        LocalKind.Wsl => GlyphLinux,
        LocalKind.Docker => GlyphContainer,
        LocalKind.Multipass => GlyphVm,
        _ => GlyphTerminal,
    };

    public static string Name(ConnectionProtocol protocol) => protocol switch
    {
        ConnectionProtocol.Ssh => "SSH",
        ConnectionProtocol.Sftp => "SFTP",
        ConnectionProtocol.Mosh => "Mosh",
        ConnectionProtocol.Tmux => "tmux",
        ConnectionProtocol.Rdp => "RDP",
        ConnectionProtocol.Telnet => "Telnet",
        ConnectionProtocol.Serial => "串口",
        ConnectionProtocol.Docker => "Docker",
        ConnectionProtocol.Proxmox => "Proxmox",
        ConnectionProtocol.Monitor => "状态",
        ConnectionProtocol.Tools => "工具",
        ConnectionProtocol.Network => "网络检测",
        _ => "herdr",
    };

    public static string Name(LocalKind kind) => kind switch
    {
        LocalKind.Wsl => "WSL",
        LocalKind.Docker => "Docker",
        LocalKind.Multipass => "Multipass",
        _ => "Shell",
    };

    public static FontIcon Icon(string glyph, double size = 16) => new() { Glyph = glyph, FontSize = size };

    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    public static TextBlock Title(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["TitleTextBlockStyle"] };

    public static TextBlock Section(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, 12, 0, 2) };

    public static TextBlock Secondary(string text, bool wrap = false) => new()
    {
        Text = text,
        Foreground = Brush("TextFillColorSecondaryBrush"),
        FontSize = 12,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    public static TextBlock Body(string text) => new() { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };

    public static Border Card(UIElement child, double padding = 14) => new()
    {
        Child = child,
        Padding = new Thickness(padding),
        CornerRadius = new CornerRadius(8),
        BorderThickness = new Thickness(1),
        Background = Brush("CardBackgroundFillColorDefaultBrush"),
        BorderBrush = Brush("CardStrokeColorDefaultBrush"),
    };

    public static Button Button(string text, string? glyph, Action onClick, bool accent = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (glyph != null) content.Children.Add(Icon(glyph, 14));
        content.Children.Add(new TextBlock { Text = text });
        var button = new Button { Content = content };
        if (accent) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        button.Click += (_, _) => onClick();
        return button;
    }

    public static Button IconButton(string glyph, string tooltip, Action onClick)
    {
        var button = new Button { Content = Icon(glyph, 14), Padding = new Thickness(8, 6, 8, 6), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
        ToolTipService.SetToolTip(button, tooltip);
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>A small labeled button for a card. Entries in <paramref name="menu"/> appear on right-click.</summary>
    public static Button Chip(string glyph, string text, string tooltip, Action onClick, params MenuFlyoutItem[] menu)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(Icon(glyph, 12));
        content.Children.Add(new TextBlock { Text = text, FontSize = 12, MaxWidth = 180, TextTrimming = TextTrimming.CharacterEllipsis });
        var button = new Button { Content = content, Padding = new Thickness(8, 4, 8, 4) };
        ToolTipService.SetToolTip(button, tooltip);
        button.Click += (_, _) => onClick();
        if (menu.Length > 0)
        {
            var flyout = new MenuFlyout();
            foreach (var item in menu) flyout.Items.Add(item);
            button.ContextFlyout = flyout;
        }
        return button;
    }

    public static Grid Columns(params string[] widths)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        foreach (var width in widths)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = width switch
                {
                    "*" => new GridLength(1, GridUnitType.Star),
                    "auto" => GridLength.Auto,
                    // Weighted stars such as "2*" share the remaining width in proportion.
                    _ when width.EndsWith('*') => new GridLength(double.Parse(width[..^1], System.Globalization.CultureInfo.InvariantCulture), GridUnitType.Star),
                    _ => new GridLength(double.Parse(width, System.Globalization.CultureInfo.InvariantCulture)),
                },
            });
        }
        return grid;
    }

    public static T InColumn<T>(this T element, int column) where T : FrameworkElement
    {
        Grid.SetColumn(element, column);
        return element;
    }

    /// <summary>Page shell with a scrollable, width-limited content column.</summary>
    public static (ScrollViewer Root, StackPanel Content) Page()
    {
        var content = new StackPanel { Spacing = 8, Padding = new Thickness(32, 24, 32, 32), MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Stretch };
        return (new ScrollViewer { Content = content, HorizontalScrollMode = ScrollMode.Disabled }, content);
    }

    public static TextBlock Strong(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis };

    public static MenuFlyoutItem MenuItem(string text, string glyph, Action onClick)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = Icon(glyph) };
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>An icon button that opens a menu built when it is clicked, so the entries are always current.</summary>
    public static Button MenuButton(string glyph, string tooltip, Action<MenuFlyout> build)
    {
        Button button = null!;
        button = IconButton(glyph, tooltip, () =>
        {
            var menu = new MenuFlyout();
            build(menu);
            menu.ShowAt(button);
        });
        return button;
    }

    /// <summary>The machine at a glance: system, size, load and where it is.</summary>
    public static string DescribeFacts(DeviceFacts facts)
    {
        var parts = new List<string>();
        if (facts.Os.Length > 0) parts.Add(facts.Os);
        else if (facts.Kernel.Length > 0) parts.Add(facts.Kernel);
        if (facts.Architecture.Length > 0) parts.Add(facts.Architecture);
        if (facts.Virtualization.Length > 0) parts.Add(facts.Virtualization);
        if (facts.CpuCount > 0 || facts.MemoryTotalKb > 0)
            parts.Add($"{facts.CpuCount} 核 {facts.MemoryTotalKb / 1024.0 / 1024.0:0.#} GB");
        parts.Add($"负载 {facts.Load1:0.##} / {facts.Load5:0.##} / {facts.Load15:0.##}");
        if (facts.UptimeSeconds > 0) parts.Add("已运行 " + FormatDuration(facts.UptimeSeconds));
        return string.Join(" · ", parts);
    }

    public static string DescribeLocation(DeviceFacts facts)
    {
        string country = facts.CountryCode;
        try
        {
            if (country.Length == 2) country = new System.Globalization.RegionInfo(country).DisplayName;
        }
        catch (ArgumentException)
        {
            // An unknown code is still more useful than nothing.
        }
        var place = new List<string> { country };
        if (facts.Region.Length > 0 && facts.Region != facts.City) place.Add(facts.Region);
        if (facts.City.Length > 0) place.Add(facts.City);
        string text = string.Join(" ", place.Where(p => p.Length > 0));
        if (facts.PublicIp.Length > 0) text += (text.Length > 0 ? " · " : "") + facts.PublicIp;
        if (facts.Network.Length > 0) text += " · " + facts.Network;
        return text;
    }

    public static string FormatDuration(long seconds)
    {
        if (seconds < 3600) return $"{seconds / 60} 分钟";
        if (seconds < 86400) return $"{seconds / 3600} 小时";
        return $"{seconds / 86400} 天";
    }

    /// <summary>A bundled picture such as a system logo or a flag, or null when the file isn't there.</summary>
    public static Image? Asset(string folder, string name, double width, double height)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", folder, name + ".svg");
        if (!File.Exists(path)) return null;
        return new Image
        {
            Source = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(new Uri(path)) { RasterizePixelWidth = width * 2, RasterizePixelHeight = height * 2 },
            Width = width,
            Height = height,
            Stretch = Stretch.Uniform,
        };
    }

    public static string RelativeTime(DateTimeOffset time)
    {
        var span = DateTimeOffset.Now - time;
        if (span.TotalMinutes < 1) return "刚刚";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} 分钟前";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} 小时前";
        return $"{(int)span.TotalDays} 天前";
    }

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}
