using EasyTerminal.Controls;
using EasyTerminal.Core.Models;
using EasyTerminal.Core.Ssh;
using EasyTerminal.Pages;
using EasyTerminal.Services;
using EasyTerminal.Sessions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace EasyTerminal;

/// <summary>The title-bar search box, which doubles as a VS Code style command palette.</summary>
public sealed partial class MainWindow
{
    private List<PaletteItem> _paletteResults = new();
    private bool _paletteHidesRemoteDesktop;

    public void OpenCommandPalette(string query = "")
    {
        Palette.Text = query;
        UpdatePalette();
        Palette.Focus(FocusState.Programmatic);
        Palette.IsSuggestionListOpen = true;
    }

    public void CloseCommandPalette()
    {
        Palette.Text = "";
        Palette.IsSuggestionListOpen = false;
    }

    private void UpdatePalette()
    {
        _paletteResults = PaletteItem.Filter(BuildPaletteItems(Palette.Text.Trim()), Palette.Text.Trim());
        Palette.ItemsSource = _paletteResults;
    }

    private void OnPaletteGotFocus(object sender, RoutedEventArgs e)
    {
        UpdatePalette();
        Palette.IsSuggestionListOpen = true;
        SetPaletteHidesRemoteDesktop(true);
    }

    private void OnPaletteLostFocus(object sender, RoutedEventArgs e) => SetPaletteHidesRemoteDesktop(false);

    /// <summary>A remote desktop is a window above XAML and would cover the suggestion list.</summary>
    private void SetPaletteHidesRemoteDesktop(bool hide)
    {
        if (_paletteHidesRemoteDesktop == hide) return;
        _paletteHidesRemoteDesktop = hide;
        RdpPane.Suppress(hide);
    }

    private void OnPaletteTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) UpdatePalette();
    }

    private void OnPaletteSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        // Enter without moving the selection runs the best match.
        var item = args.ChosenSuggestion as PaletteItem ?? _paletteResults.FirstOrDefault();
        if (item == null) return;
        CloseCommandPalette();
        item.Run();
        if (_activeTab != null) _app.Dispatcher.TryEnqueue(() => _activeTab?.View?.FocusTerminal());
    }

    private void OnPalettePreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        e.Handled = true;
        CloseCommandPalette();
        if (_activeTab?.View != null) _activeTab.View.FocusTerminal();
        else RootGrid.Focus(FocusState.Programmatic);
    }

    private void OnPaletteAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        OpenCommandPalette();
    }

    private IEnumerable<PaletteItem> BuildPaletteItems(string query)
    {
        // A typed address is offered first so the box can also connect straight away.
        if (query.Contains('@') && DevicesPage.TryParseAddress(query, out var user, out var host, out var port))
        {
            yield return new PaletteItem
            {
                Glyph = Ui.GlyphTerminal,
                Title = $"连接到 {query}",
                Detail = "通过 SSH 连接，并保存为设备",
                Category = "连接",
                Run = () => _sessions.QuickConnect(user, host, port),
            };
        }

        foreach (var tab in _sessions.Tabs)
        {
            var captured = tab;
            yield return new PaletteItem
            {
                Glyph = tab.Glyph,
                Title = tab.Title,
                Detail = (tab.Device?.Name ?? "本机") + " · " + tab.StatusText,
                Category = "切换会话",
                Run = () => ActivateTab(captured),
            };
        }

        foreach (var item in SnippetItems()) yield return item;
        foreach (var item in NewSessionItems()) yield return item;
        foreach (var item in CommandItems()) yield return item;
    }

    private IEnumerable<PaletteItem> SnippetItems()
    {
        // Only offered when there is a terminal in front to receive it.
        if (_activeTab is not { Terminal: not null } target) yield break;
        foreach (var snippet in _app.Store.Data.Snippets.OrderByDescending(s => s.LastUsed))
        {
            yield return new PaletteItem
            {
                Glyph = Ui.GlyphSnippet,
                Title = snippet.Name,
                Detail = snippet.Body.ReplaceLineEndings(" ⏎ "),
                Category = "运行片段",
                Run = () => _ = SnippetRunner.SendAsync(snippet, new[] { target }),
            };
        }
    }

    private IEnumerable<PaletteItem> NewSessionItems()
    {
        foreach (var device in _app.Store.Data.Devices)
        {
            foreach (var connection in device.Connections.Where(device.Shows).OrderByDescending(c => c.LastUsed))
            {
                yield return new PaletteItem
                {
                    Glyph = Ui.GlyphFor(connection.Protocol),
                    Title = $"{device.Name} · {connection.Name}",
                    Detail = $"{Ui.Name(connection.Protocol)} · {device.Host}",
                    Category = "新建会话",
                    Run = () => _ = _sessions.OpenConnectionAsync(device, connection),
                };
            }

            if (!_app.Probes.TryGetValue(device.Id, out var probe)) continue;
            foreach (var session in probe.Sessions)
            {
                yield return new PaletteItem
                {
                    Glyph = Ui.GlyphSessions,
                    Title = $"{device.Name} · {session.Name}",
                    Detail = $"{(session.Kind == RemoteSessionKind.Tmux ? "tmux" : "herdr")} · {session.Detail}",
                    Category = "新建会话",
                    Run = () => _ = _sessions.OpenRemoteSessionAsync(device, session),
                };
            }
            foreach (var container in probe.Containers)
            {
                yield return new PaletteItem
                {
                    Glyph = Ui.GlyphContainer,
                    Title = $"{device.Name} · {container.Name}",
                    Detail = "Docker · " + container.Detail,
                    Category = "新建会话",
                    Run = () => _ = _sessions.OpenRemoteContainerAsync(device, container),
                };
            }
        }

        foreach (var kind in new[] { LocalKind.Shell, LocalKind.Wsl, LocalKind.Docker, LocalKind.Multipass })
        {
            foreach (var target in _app.LocalTargets(kind))
            {
                yield return new PaletteItem
                {
                    Glyph = Ui.GlyphFor(kind),
                    Title = "本机 · " + target.Name,
                    Detail = Ui.Name(kind) + (kind == LocalKind.Shell ? "" : " · " + target.Detail),
                    Category = "新建会话",
                    Run = () => _ = _sessions.OpenLocalAsync(target),
                };
            }
        }
    }

    private IEnumerable<PaletteItem> CommandItems()
    {
        PaletteItem Command(string glyph, string title, Action run, string detail = "") =>
            new() { Glyph = glyph, Title = title, Detail = detail, Category = "命令", IsCommand = true, Run = run };

        if (_activeTab is { } active)
        {
            yield return Command(Ui.GlyphRefresh, "重新连接当前会话", active.Restart, active.Title);
            yield return Command(Ui.GlyphCopy, "复制当前会话", () => _sessions.Duplicate(active), active.Title);
            yield return Command(Ui.GlyphClose, "关闭当前会话", () => _ = CloseTabAsync(active), active.Title);
            if (active.View is { } view) yield return Command(Ui.GlyphSearch, "在当前终端中查找", () => (active.Content as TerminalPane)?.ShowFind(), "Ctrl+Shift+F");
            if (_splitRight != null) yield return Command(Ui.GlyphSplit, "取消分屏", CloseSplit);
            else if (_sessions.Tabs.Count > 1) yield return Command(Ui.GlyphSplit, "分屏显示当前会话", () => ShowSplit(active), active.Title);
        }

        yield return Command(Ui.GlyphBroadcast, _sessions.IsBroadcasting ? "关闭广播输入" : "开启广播输入", () => _sessions.SetBroadcasting(!_sessions.IsBroadcasting), "向所有运行中的终端发送相同的键盘输入");
        yield return Command(Ui.GlyphAssistant, "打开或关闭 AI 助手", ToggleAssistant);
        yield return Command(Ui.GlyphHome, "打开主页", () => Navigate("home"));
        yield return Command(Ui.GlyphDevices, "打开设备", () => Navigate("devices"));
        yield return Command(Ui.GlyphSnippet, "打开代码片段", () => Navigate("snippets"));
        yield return Command(Ui.GlyphAdd, "新建代码片段", () =>
        {
            Navigate("snippets");
            _ = ((SnippetsPage)_pages["snippets"]).AddAsync();
        });
        yield return Command(Ui.GlyphForward, "打开端口转发", () => Navigate("forwards"));
        yield return Command(Ui.GlyphKey, "打开密钥", () => Navigate("keys"));
        yield return Command(Ui.GlyphEdit, "打开设置", () => Navigate("settings"));
        yield return Command(Ui.GlyphAdd, "添加设备", () =>
        {
            Navigate("devices");
            _ = ((DevicesPage)_pages["devices"]).AddDeviceAsync();
        });
        yield return Command(Ui.GlyphKey, "生成密钥", () =>
        {
            Navigate("keys");
            _ = ((KeysPage)_pages["keys"]).GenerateAsync();
        }, "硬件安全密钥或软件密钥");
        yield return Command(Ui.GlyphRefresh, "重新扫描本地环境", () => _ = _app.ScanLocalAsync(), "WSL、Docker、Multipass");

        foreach (var device in _app.Store.Data.Devices)
        {
            yield return Command(Ui.GlyphRefresh, $"扫描 {device.Name} 的会话和容器", () => _ = ProbeFromPaletteAsync(device), "tmux、herdr、Docker");
            yield return Command(Ui.GlyphEdit, $"编辑设备 {device.Name}", () => _ = EditDeviceAsync(device));
        }

        foreach (var scheme in ColorScheme.All)
        {
            yield return Command(Ui.GlyphEdit, "终端配色：" + scheme.Name, () =>
            {
                _app.Store.Data.Settings.ColorScheme = scheme.Name;
                _app.NotifySettingsChanged();
            });
        }
        foreach (var (name, tag) in new[] { ("跟随系统", "Default"), ("浅色", "Light"), ("深色", "Dark") })
        {
            yield return Command(Ui.GlyphEdit, "应用主题：" + name, () =>
            {
                _app.Store.Data.Settings.Theme = tag;
                _app.NotifySettingsChanged();
            });
        }
    }

    private async Task ProbeFromPaletteAsync(Device device)
    {
        try
        {
            var result = await _app.ProbeAsync(device);
            ShowNotice(device.Name, $"发现 {result.Sessions.Count} 个会话、{result.Containers.Count} 个容器", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowNotice($"无法扫描 {device.Name}", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task EditDeviceAsync(Device device)
    {
        if (await DeviceDialog.ShowAsync(device) != null) _app.NotifyDevicesChanged();
    }
}
