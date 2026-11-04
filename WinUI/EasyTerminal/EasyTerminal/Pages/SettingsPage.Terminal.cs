using EasyTerminal.Controls;
using EasyTerminal.Core.Models;
using EasyTerminal.Core.Terminal;
using EasyTerminal.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EasyTerminal.Pages;

/// <summary>Terminal color schemes, custom and imported schemes, background opacity and the window material.</summary>
public sealed partial class SettingsPage
{
    private readonly ComboBox _schemeBox = new() { MinWidth = 220 };
    private readonly Slider _opacity = new() { Minimum = 0, Maximum = 100, StepFrequency = 5, SmallChange = 5, Width = 260 };
    private readonly Border _preview = new() { CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 10, 12, 10), BorderThickness = new Thickness(1) };

    /// <summary>
    /// Switches scheme. Picking a Mica scheme makes the background transparent so the material shows; leaving one
    /// for an ordinary scheme makes it opaque again, unless the user chose an opacity of their own in between.
    /// </summary>
    public static void ChooseScheme(AppSettings settings, string name)
    {
        bool wasMica = ColorScheme.IsMicaName(settings.ColorScheme);
        bool nowMica = ColorScheme.IsMicaName(name);
        if (nowMica && !wasMica && settings.TerminalOpacity == 100) settings.TerminalOpacity = 0;
        else if (!nowMica && wasMica && settings.TerminalOpacity == 0) settings.TerminalOpacity = 100;
        settings.ColorScheme = name;
    }

    private void AddTerminalThemeSettings(StackPanel content)
    {
        var settings = _app.Store.Data.Settings;

        FillSchemes();
        _schemeBox.SelectionChanged += (_, _) =>
        {
            if (_loading || _schemeBox.SelectedItem is not string name || name == _app.Store.Data.Settings.ColorScheme) return;
            Update(s => ChooseScheme(s, name));
            _loading = true;
            _opacity.Value = _app.Store.Data.Settings.TerminalOpacity;
            _loading = false;
            ShowPreview();
        };
        var schemeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        schemeRow.Children.Add(_schemeBox);
        schemeRow.Children.Add(Ui.IconButton(Ui.GlyphEdit, "编辑当前配色（另存为自定义配色）", () => _ = EditSchemeAsync(false)));
        schemeRow.Children.Add(Ui.MenuButton(Ui.GlyphMore, "更多", menu =>
        {
            menu.Items.Add(Ui.MenuItem("导入 Windows Terminal 配色…", Ui.GlyphDownload, () => _ = EditSchemeAsync(true)));
            menu.Items.Add(Ui.MenuItem("复制当前配色 JSON", Ui.GlyphCopy, CopySchemeJson));
            if (ColorScheme.Find(_app.Store.Data.Settings.ColorScheme).IsCustom)
                menu.Items.Add(Ui.MenuItem("删除此自定义配色", Ui.GlyphDelete, () => _ = DeleteSchemeAsync()));
        }));
        content.Children.Add(Row("终端配色", schemeRow));

        _opacity.Value = settings.TerminalOpacity;
        _opacity.ValueChanged += (_, args) =>
        {
            if (_loading) return;
            Update(s => s.TerminalOpacity = (int)Math.Round(args.NewValue));
            ShowPreview();
        };
        content.Children.Add(Row("终端背景不透明度", _opacity));

        var backdrop = new ComboBox { MinWidth = 260 };
        foreach (var (name, tag) in new[] { ("Mica", "Mica"), ("Mica Alt（更深的色调）", "MicaAlt"), ("亚克力（透出桌面）", "Acrylic") })
            backdrop.Items.Add(new ComboBoxItem { Content = name, Tag = tag });
        backdrop.SelectedItem = backdrop.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == settings.WindowBackdrop) ?? backdrop.Items[0];
        backdrop.SelectionChanged += (_, _) =>
        {
            if (backdrop.SelectedItem is ComboBoxItem { Tag: string tag }) Update(s => s.WindowBackdrop = tag);
        };
        content.Children.Add(Row("窗口背景材质", backdrop));

        var previewCard = new StackPanel { Spacing = 8 };
        previewCard.Children.Add(_preview);
        previewCard.Children.Add(Ui.Secondary("不透明度低于 100% 时，终端会透出窗口的 Mica 或亚克力。选 Mica 配色会自动设为完全透明；也可以导入 Windows Terminal 配色，或编辑任一配色另存为自定义配色。", true));
        content.Children.Add(Ui.Card(previewCard, 12));
        ActualThemeChanged += (_, _) => ShowPreview();
        ShowPreview();
    }

    private void FillSchemes()
    {
        bool wasLoading = _loading;
        _loading = true;
        _schemeBox.Items.Clear();
        foreach (var name in ColorScheme.Names) _schemeBox.Items.Add(name);
        string current = _app.Store.Data.Settings.ColorScheme;
        _schemeBox.SelectedItem = _schemeBox.Items.Contains(current) ? current : ColorScheme.All[0].Name;
        _loading = wasLoading;
    }

    /// <summary>A sample of the scheme as the terminal will draw it, including the opacity over this page's background.</summary>
    private void ShowPreview()
    {
        var settings = _app.Store.Data.Settings;
        var scheme = ColorScheme.Find(settings.ColorScheme, ActualTheme == ElementTheme.Dark);
        byte alpha = (byte)Math.Round(Math.Clamp(settings.TerminalOpacity, 0, 100) * 2.55);
        _preview.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, scheme.Background.R, scheme.Background.G, scheme.Background.B));
        _preview.BorderBrush = Ui.Brush("CardStrokeColorDefaultBrush");

        var panel = new StackPanel { Spacing = 8 };
        var font = new FontFamily(settings.FontFamily);
        var prompt = new TextBlock { FontFamily = font, FontSize = settings.FontSize };
        prompt.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "user@host", Foreground = new SolidColorBrush(scheme.Ansi[2]) });
        prompt.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = ":", Foreground = new SolidColorBrush(scheme.Foreground) });
        prompt.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "~/project", Foreground = new SolidColorBrush(scheme.Ansi[4]) });
        prompt.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "$ ls --color", Foreground = new SolidColorBrush(scheme.Foreground) });
        panel.Children.Add(prompt);
        var listing = new TextBlock { FontFamily = font, FontSize = settings.FontSize };
        foreach (var (text, color) in new[] { ("src  ", 12), ("build.sh  ", 10), ("README.md  ", 7), ("error.log", 9) })
            listing.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = text, Foreground = new SolidColorBrush(scheme.Ansi[color]) });
        panel.Children.Add(listing);

        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var color in scheme.Ansi)
            swatches.Children.Add(new Border { Width = 18, Height = 14, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(color) });
        panel.Children.Add(swatches);
        _preview.Child = panel;
    }

    private void CopySchemeJson()
    {
        var scheme = ColorScheme.Find(_app.Store.Data.Settings.ColorScheme, ActualTheme == ElementTheme.Dark);
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(scheme.ToData().ToWindowsTerminalJson());
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    /// <summary>
    /// Edits the current scheme as Windows Terminal JSON, or imports pasted or loaded JSON. Saving never changes
    /// a built-in scheme: an edited built-in becomes a custom scheme named after it.
    /// </summary>
    private async Task EditSchemeAsync(bool import)
    {
        var settings = _app.Store.Data.Settings;
        var current = ColorScheme.Find(settings.ColorScheme, ActualTheme == ElementTheme.Dark);
        var editor = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            Height = 380,
            Width = 520,
            Text = import ? "" : current.ToData().ToWindowsTerminalJson(),
            PlaceholderText = "粘贴 Windows Terminal 配色 JSON：单个配色、配色数组，或含 \"schemes\" 的整个 settings.json",
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
        var help = Ui.Secondary(import
            ? "可以从 windowsterminalthemes.dev 复制，或点“从文件读取”。导入的配色会出现在配色列表里。"
            : "修改颜色（#RRGGBB）后保存。内置配色不会被改动，保存后会生成一份自定义配色。", true);
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(help);
        panel.Children.Add(editor);

        var dialog = new ContentDialog
        {
            Title = import ? "导入配色" : "编辑配色",
            Content = panel,
            PrimaryButtonText = "保存",
            SecondaryButtonText = "从文件读取…",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.SecondaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            var deferral = args.GetDeferral();
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                picker.FileTypeFilter.Add(".json");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.WindowHandle);
                if (await picker.PickSingleFileAsync() is { } file) editor.Text = await File.ReadAllTextAsync(file.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                help.Text = "无法读取文件：" + ex.Message;
            }
            finally
            {
                deferral.Complete();
            }
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                SaveSchemes(TerminalSchemeData.ParseWindowsTerminal(editor.Text), current);
            }
            catch (FormatException ex)
            {
                help.Text = ex.Message;
                args.Cancel = true;
            }
        };
        await Dialogs.ShowAsync(dialog);
    }

    private void SaveSchemes(List<TerminalSchemeData> schemes, ColorScheme edited)
    {
        var settings = _app.Store.Data.Settings;
        var builtIn = ColorScheme.All.Select(s => s.Name).Concat(new[] { ColorScheme.MicaFollow, ColorScheme.MicaDark.Name, ColorScheme.MicaLight.Name }).ToHashSet();
        string? first = null;
        foreach (var scheme in schemes)
        {
            if (builtIn.Contains(scheme.Name)) scheme.Name += "（自定义）";
            settings.CustomSchemes.RemoveAll(s => s.Name == scheme.Name);
            settings.CustomSchemes.Add(scheme);
            first ??= scheme.Name;
        }
        _app.LoadCustomSchemes();
        // Editing a Mica scheme keeps its transparency; any other scheme keeps the opacity the user had.
        if (first != null)
        {
            if (edited.IsMica) settings.ColorScheme = first;
            else ChooseScheme(settings, first);
        }
        _app.NotifySettingsChanged();
        FillSchemes();
        _loading = true;
        _opacity.Value = settings.TerminalOpacity;
        _loading = false;
        ShowPreview();
    }

    private async Task DeleteSchemeAsync()
    {
        var settings = _app.Store.Data.Settings;
        string name = settings.ColorScheme;
        if (!await Dialogs.ConfirmAsync($"删除配色“{name}”？", "终端会改用默认配色。", "删除", true)) return;
        settings.CustomSchemes.RemoveAll(s => s.Name == name);
        _app.LoadCustomSchemes();
        settings.ColorScheme = ColorScheme.All[0].Name;
        _app.NotifySettingsChanged();
        FillSchemes();
        ShowPreview();
    }
}
