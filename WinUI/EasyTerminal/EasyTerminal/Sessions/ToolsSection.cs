using EasyTerminal.Core.Models;
using EasyTerminal.Core.Monitoring;
using EasyTerminal.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EasyTerminal.Sessions;

/// <summary>The tools part of a device's status: monitoring and diagnostic programs it has, and the ones it could install.</summary>
public sealed class ToolsSection : StackPanel, IDisposable
{
    private readonly AppServices _app = AppServices.Current;
    private readonly SessionManager _sessions;
    private readonly Device _device;
    private readonly StackPanel _body = new() { Spacing = 8 };
    private readonly Button _scan;

    public ToolsSection(SessionManager sessions, Device device)
    {
        _sessions = sessions;
        _device = device;
        Spacing = 8;

        var header = Ui.Columns("*", "auto");
        var title = Ui.Section("工具");
        title.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(title.InColumn(0));
        // Installing happens in a terminal, so the list only learns about it from a new scan.
        _scan = Ui.Button("重新扫描", Ui.GlyphRefresh, () => _ = ScanAsync());
        header.Children.Add(_scan.InColumn(1));
        Children.Add(header);
        Children.Add(_body);

        _app.DevicesChanged += Refresh;
        Refresh();
    }

    private async Task ScanAsync()
    {
        try
        {
            await _app.ProbeAsync(_device);
        }
        catch (Exception ex)
        {
            _body.Children.Insert(0, new InfoBar { Severity = InfoBarSeverity.Error, IsOpen = true, IsClosable = true, Title = "无法扫描", Message = ex.Message });
        }
    }

    private void Refresh()
    {
        _scan.IsEnabled = !_app.ProbingDevices.Contains(_device.Id);
        _body.Children.Clear();
        var facts = _device.Facts;
        if (facts == null)
        {
            _body.Children.Add(Ui.Secondary("扫描设备后会列出它安装的监测和诊断工具。"));
            return;
        }

        var grid = new Controls.CardGridPanel { MinCardWidth = 260, Spacing = 8 };
        foreach (var tool in ToolCatalog.All.Where(t => facts.Tools.Contains(t.Name)))
            grid.Children.Add(BuildTool(tool, facts));
        if (grid.Children.Count > 0) _body.Children.Add(grid);
        else _body.Children.Add(Ui.Secondary("没有发现常用的监测工具。"));

        // Only what this host's package manager can install, so every button here can work.
        var installable = facts.PackageManager.Length > 0
            ? ToolCatalog.All.Where(t => !facts.Tools.Contains(t.Name) && ToolCatalog.InstallCommand(t, facts.PackageManager, facts.IsRoot) != null).ToList()
            : new List<Tool>();
        if (installable.Count == 0) return;
        var list = new StackPanel { Spacing = 6 };
        foreach (var tool in installable)
        {
            var row = Ui.Columns("*", "auto");
            var text = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            text.Children.Add(Ui.Body(tool.Name));
            var description = Ui.Secondary($"{ToolCatalog.CategoryName(tool.Category)} · {tool.Description}");
            description.VerticalAlignment = VerticalAlignment.Center;
            text.Children.Add(description);
            row.Children.Add(text.InColumn(0));
            row.Children.Add(Ui.Button("安装", Ui.GlyphDownload, () => _ = InstallAsync(tool, facts)).InColumn(1));
            list.Children.Add(row);
        }
        _body.Children.Add(new Expander
        {
            Header = $"可以安装的工具（{installable.Count}，使用 {facts.PackageManager}）",
            Content = list,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        });
    }

    private FrameworkElement BuildTool(Tool tool, DeviceFacts facts)
    {
        var row = Ui.Columns("*", "auto");
        var text = new StackPanel();
        text.Children.Add(Ui.Strong(tool.Name));
        string detail = $"{ToolCatalog.CategoryName(tool.Category)} · {tool.Description}" + (tool.NeedsRoot && !facts.IsRoot ? " · 需要 sudo" : "");
        var description = Ui.Secondary(detail);
        ToolTipService.SetToolTip(description, detail);
        text.Children.Add(description);
        row.Children.Add(text.InColumn(0));
        var open = Ui.Button("打开", Ui.GlyphPlay, () => _ = LaunchAsync(tool, facts));
        open.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(open.InColumn(1));
        return Ui.Card(row, 10);
    }

    private async Task LaunchAsync(Tool tool, DeviceFacts facts)
    {
        string target = "";
        if (tool.NeedsTarget)
        {
            if (await Dialogs.PromptTextAsync(tool.Name, "目标地址", tool.Name is "mtr" or "traceroute" ? "1.1.1.1" : "", "开始") is not { Length: > 0 } entered) return;
            target = entered;
        }
        _sessions.OpenRemoteCommand(_device, target.Length > 0 ? $"{tool.Name} {target}" : tool.Name, Ui.GlyphFor(ConnectionProtocol.Tools), ToolCatalog.LaunchCommand(tool, target, facts.IsRoot));
    }

    private async Task InstallAsync(Tool tool, DeviceFacts facts)
    {
        if (ToolCatalog.InstallCommand(tool, facts.PackageManager, facts.IsRoot) is not { } command) return;
        if (!await Dialogs.ConfirmAsync($"在 {_device.Name} 上安装 {tool.Name}？", $"会打开一个终端，用 {facts.PackageManager} 安装软件包 {tool.Package}{(facts.IsRoot ? "" : "，可能需要输入 sudo 密码")}。装好后点“重新扫描”。", "安装")) return;
        _sessions.OpenRemoteCommand(_device, "安装 " + tool.Name, Ui.GlyphDownload, command);
    }

    public void Dispose() => _app.DevicesChanged -= Refresh;
}
