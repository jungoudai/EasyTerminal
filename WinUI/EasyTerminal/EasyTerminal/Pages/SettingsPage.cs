using EasyTerminal.Controls;
using EasyTerminal.Core.Models;
using EasyTerminal.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EasyTerminal.Pages;

public sealed partial class SettingsPage : UserControl, IRefreshablePage
{
    private readonly AppServices _app = AppServices.Current;
    private readonly ComboBox _shell = new() { MinWidth = 260 };
    private bool _loading = true;

    public SettingsPage()
    {
        var (root, content) = Ui.Page();
        Content = root;
        var settings = _app.Store.Data.Settings;
        content.Children.Add(Ui.Title("设置"));

        content.Children.Add(Ui.Section("外观"));
        var theme = new ComboBox { MinWidth = 260 };
        foreach (var (name, tag) in new[] { ("跟随系统", "Default"), ("浅色", "Light"), ("深色", "Dark") })
            theme.Items.Add(new ComboBoxItem { Content = name, Tag = tag });
        theme.SelectedItem = theme.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == settings.Theme);
        theme.SelectionChanged += (_, _) => Update(s => s.Theme = (string)((ComboBoxItem)theme.SelectedItem).Tag);
        content.Children.Add(Row("应用主题", theme));

        AddTerminalThemeSettings(content);

        var font = new ComboBox { MinWidth = 260, IsEditable = true, Text = settings.FontFamily };
        foreach (var name in new[] { "Cascadia Mono", "Cascadia Code", "Consolas", "JetBrains Mono", "Fira Code", "Sarasa Mono SC", "Courier New" }) font.Items.Add(name);
        font.SelectedItem = font.Items.Contains(settings.FontFamily) ? settings.FontFamily : null;
        font.TextSubmitted += (_, args) => Update(s => s.FontFamily = args.Text.Trim());
        font.SelectionChanged += (_, _) =>
        {
            if (font.SelectedItem is string name) Update(s => s.FontFamily = name);
        };
        content.Children.Add(Row("字体", font));

        var size = new NumberBox { Minimum = 8, Maximum = 32, Value = settings.FontSize, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, MinWidth = 260 };
        size.ValueChanged += (_, args) =>
        {
            if (!double.IsNaN(args.NewValue)) Update(s => s.FontSize = args.NewValue);
        };
        content.Children.Add(Row("字号（Ctrl + 加号 / 减号）", size));

        var lineHeight = new NumberBox { Minimum = 1, Maximum = 2, SmallChange = 0.05, Value = settings.LineHeight, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, MinWidth = 260 };
        lineHeight.ValueChanged += (_, args) =>
        {
            if (!double.IsNaN(args.NewValue)) Update(s => s.LineHeight = args.NewValue);
        };
        content.Children.Add(Row("行高", lineHeight));

        content.Children.Add(Row("光标闪烁", Toggle(settings.CursorBlink, v => Update(s => s.CursorBlink = v))));
        content.Children.Add(Row("粗体使用亮色", Toggle(settings.BoldIsBright, v => Update(s => s.BoldIsBright = v))));

        content.Children.Add(Ui.Section("会话"));
        _shell.SelectionChanged += (_, _) =>
        {
            if (_shell.SelectedItem is ComboBoxItem { Tag: string key }) Update(s => s.DefaultShellKey = key);
        };
        content.Children.Add(Row("默认 Shell", _shell));

        var scrollback = new NumberBox { Minimum = 0, Maximum = 200000, SmallChange = 1000, LargeChange = 10000, Value = settings.Scrollback, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, MinWidth = 260 };
        scrollback.ValueChanged += (_, args) =>
        {
            if (!double.IsNaN(args.NewValue)) Update(s => s.Scrollback = (int)args.NewValue);
        };
        content.Children.Add(Row("回滚行数（新会话生效）", scrollback));
        content.Children.Add(Row("启动时恢复上次的会话", Toggle(settings.RestoreSessions, v => Update(s => s.RestoreSessions = v))));
        content.Children.Add(Row("关闭运行中的会话前确认", Toggle(settings.ConfirmCloseRunning, v => Update(s => s.ConfirmCloseRunning = v))));

        content.Children.Add(Row($"记录会话日志到 {AppServices.SessionLogDirectory}（新会话生效，可能包含敏感输出）", Toggle(settings.LogSessions, v => Update(s => s.LogSessions = v))));

        content.Children.Add(Ui.Section("工具"));
        var assistantUrl = new TextBox { MinWidth = 260, Text = settings.AssistantUrl };
        assistantUrl.LostFocus += (_, _) =>
        {
            // Only web addresses: this page runs in a browser with saved sign-ins.
            if (Uri.TryCreate(assistantUrl.Text.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http") Update(s => s.AssistantUrl = uri.ToString());
            else assistantUrl.Text = _app.Store.Data.Settings.AssistantUrl;
        };
        content.Children.Add(Row("AI 助手网址（网页模式）", assistantUrl));
        content.Children.Add(Row("扫描设备时查询地区（向 ipinfo.io 或 ipapi.co 发送设备的公网地址）", Toggle(settings.RegionLookup, v => Update(s => s.RegionLookup = v))));

        AddAgentSettings(content);
        AddTailscaleSettings(content);

        content.Children.Add(Ui.Section("配置"));
        var transfer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        transfer.Children.Add(Ui.Button("导出", Ui.GlyphUpload, () => _ = ExportAsync()));
        transfer.Children.Add(Ui.Button("导入", Ui.GlyphDownload, () => _ = ImportAsync()));
        content.Children.Add(Row("设备、片段和端口转发（不含密码和令牌）", transfer));

        _app.ScanChanged += Refresh;
        Refresh();
        _loading = false;
    }

    private static readonly (string Name, AgentProvider Provider, string Endpoint, string Model)[] AgentPresets =
    {
        ("DeepSeek", AgentProvider.OpenAICompatible, "https://api.deepseek.com/v1", "deepseek-chat"),
        ("通义千问", AgentProvider.OpenAICompatible, "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus"),
        ("Kimi", AgentProvider.OpenAICompatible, "https://api.moonshot.cn/v1", "kimi-latest"),
        ("OpenAI", AgentProvider.OpenAICompatible, "https://api.openai.com/v1", "gpt-5-mini"),
        ("Claude", AgentProvider.Anthropic, "https://api.anthropic.com", "claude-sonnet-5-5"),
        ("Ollama（本机）", AgentProvider.OpenAICompatible, "http://localhost:11434/v1", "qwen3"),
    };

    private void AddAgentSettings(StackPanel content)
    {
        var agent = _app.Store.Data.Settings.Agent;
        content.Children.Add(Ui.Section("AI Agent"));

        var provider = new ComboBox { MinWidth = 260 };
        provider.Items.Add(new ComboBoxItem { Content = "OpenAI 兼容接口", Tag = AgentProvider.OpenAICompatible });
        provider.Items.Add(new ComboBoxItem { Content = "Anthropic", Tag = AgentProvider.Anthropic });
        var endpoint = new TextBox { MinWidth = 260, Text = agent.Endpoint };
        var model = new TextBox { MinWidth = 260, Text = agent.Model };
        var key = new PasswordBox { MinWidth = 260, PlaceholderText = agent.ApiKeySecretId != null ? "已保存（输入新的以替换）" : "sk-…" };

        void ShowValues()
        {
            provider.SelectedItem = provider.Items.Cast<ComboBoxItem>().First(i => (AgentProvider)i.Tag == agent.Provider);
            endpoint.Text = agent.Endpoint;
            model.Text = agent.Model;
        }
        ShowValues();

        var preset = new ComboBox { MinWidth = 260, PlaceholderText = "选择服务商快速填写" };
        foreach (var item in AgentPresets) preset.Items.Add(item.Name);
        preset.SelectionChanged += (_, _) =>
        {
            if (preset.SelectedIndex < 0) return;
            var chosen = AgentPresets[preset.SelectedIndex];
            Update(s =>
            {
                s.Agent.Provider = chosen.Provider;
                s.Agent.Endpoint = chosen.Endpoint;
                s.Agent.Model = chosen.Model;
            });
            ShowValues();
        };
        content.Children.Add(Row("服务商", preset));

        provider.SelectionChanged += (_, _) =>
        {
            if (provider.SelectedItem is ComboBoxItem { Tag: AgentProvider value }) Update(s => s.Agent.Provider = value);
        };
        content.Children.Add(Row("接口类型（DeepSeek、通义千问、Kimi、OpenAI、Ollama 等都用 OpenAI 兼容接口）", provider));

        endpoint.LostFocus += (_, _) =>
        {
            string text = endpoint.Text.Trim();
            if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http") Update(s => s.Agent.Endpoint = text);
            else endpoint.Text = agent.Endpoint;
        };
        content.Children.Add(Row("API 地址", endpoint));

        model.LostFocus += (_, _) =>
        {
            if (model.Text.Trim().Length > 0) Update(s => s.Agent.Model = model.Text.Trim());
            else model.Text = agent.Model;
        };
        content.Children.Add(Row("模型", model));

        key.LostFocus += (_, _) =>
        {
            if (key.Password.Length == 0) return;
            // Kept encrypted in the vault, like device passwords; settings only hold its id.
            string id = _app.Store.Secrets.Set(agent.ApiKeySecretId, key.Password.Trim());
            key.Password = "";
            key.PlaceholderText = "已保存（输入新的以替换）";
            Update(s => s.Agent.ApiKeySecretId = id);
        };
        content.Children.Add(Row("API 密钥（加密保存在本机）", key));

        content.Children.Add(Row("允许 Agent 直接执行命令（不逐条确认）", Toggle(agent.AutoApproveCommands, v => Update(s => s.Agent.AutoApproveCommands = v))));

        var result = Ui.Secondary("", true);
        var test = Ui.Button("测试连接", Ui.GlyphPlay, () => { });
        test.Click += async (_, _) =>
        {
            test.IsEnabled = false;
            result.Text = "正在连接…";
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                result.Text = await Assistant.AgentChat.TestAsync(agent, _app.Store.Secrets.Get(agent.ApiKeySecretId), timeout.Token);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                result.Text = "连接失败：" + ex.Message;
            }
            finally
            {
                test.IsEnabled = true;
            }
        };
        var testRow = new StackPanel { Spacing = 6 };
        testRow.Children.Add(test);
        testRow.Children.Add(result);
        content.Children.Add(Ui.Card(testRow, 12));
    }

    private async Task ExportAsync()
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = "EasyTerminal 配置" };
        picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.WindowHandle);
        if (await picker.PickSaveFileAsync() is not { } file) return;
        try
        {
            await File.WriteAllTextAsync(file.Path, _app.Store.Export());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.AlertAsync("无法导出", ex.Message);
        }
    }

    private async Task ImportAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".json");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.WindowHandle);
        if (await picker.PickSingleFileAsync() is not { } file) return;
        try
        {
            int added = _app.Store.Import(await File.ReadAllTextAsync(file.Path));
            _app.NotifyDevicesChanged();
            await Dialogs.AlertAsync("导入完成", added > 0 ? $"添加了 {added} 项。已有的项目保持不变。" : "没有新内容，文件里的项目都已存在。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            await Dialogs.AlertAsync("无法导入", ex.Message);
        }
    }

    public void Refresh()
    {
        bool wasLoading = _loading;
        _loading = true;
        _shell.Items.Clear();
        foreach (var kind in new[] { LocalKind.Shell, LocalKind.Wsl })
            foreach (var target in _app.LocalTargets(kind))
                _shell.Items.Add(new ComboBoxItem { Content = target.Kind == LocalKind.Wsl ? "WSL: " + target.Name : target.Name, Tag = target.StableKey });
        _shell.SelectedItem = _shell.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _app.DefaultShell.StableKey);
        _loading = wasLoading;
    }

    private void Update(Action<AppSettings> change)
    {
        if (_loading) return;
        change(_app.Store.Data.Settings);
        _app.NotifySettingsChanged();
    }

    private static ToggleSwitch Toggle(bool value, Action<bool> changed)
    {
        var toggle = new ToggleSwitch { IsOn = value, OnContent = "开", OffContent = "关" };
        toggle.Toggled += (_, _) => changed(toggle.IsOn);
        return toggle;
    }

    private static FrameworkElement Row(string label, FrameworkElement control)
    {
        var row = Ui.Columns("*", "auto");
        var text = Ui.Body(label);
        text.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(text.InColumn(0));
        row.Children.Add(control.InColumn(1));
        return Ui.Card(row, 12);
    }
}
