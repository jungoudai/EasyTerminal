using EasyTerminal.Core.Models;
using EasyTerminal.Core.Snippets;
using EasyTerminal.Core.Terminal;
using EasyTerminal.Sessions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EasyTerminal.Services;

/// <summary>Fills in a snippet's placeholders and delivers it to terminals or devices.</summary>
public static class SnippetRunner
{
    /// <summary>Asks for the snippet's placeholders; null when the user cancels.</summary>
    public static async Task<string?> ResolveAsync(Snippet snippet)
    {
        var variables = SnippetTemplate.Variables(snippet.Body);
        if (variables.Count == 0) return snippet.Body;

        var boxes = variables.ToDictionary(v => v, v => new TextBox { Header = v });
        var panel = new StackPanel { Spacing = 8, MinWidth = 380 };
        foreach (var box in boxes.Values) panel.Children.Add(box);
        var dialog = new ContentDialog { Title = snippet.Name, Content = panel, PrimaryButtonText = "继续", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        dialog.Opened += (_, _) => boxes.Values.First().Focus(FocusState.Programmatic);
        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return null;
        return SnippetTemplate.Fill(snippet.Body, boxes.ToDictionary(p => p.Key, p => p.Value.Text));
    }

    public static async Task SendAsync(Snippet snippet, IEnumerable<SessionTab> tabs)
    {
        var targets = tabs.Where(t => t.Terminal?.State == SessionState.Running).ToList();
        if (targets.Count == 0)
        {
            await Dialogs.AlertAsync("没有运行中的终端", "请先打开一个终端会话。");
            return;
        }
        if (await ResolveAsync(snippet) is not { } text) return;
        foreach (var tab in targets) SendText(tab, text, snippet.RunImmediately);
        MarkUsed(snippet);
    }

    private static void SendText(SessionTab tab, string text, bool run)
    {
        var session = tab.Terminal!;
        session.Buffer.ScrollToBottom();
        session.Write(InputEncoder.PrepareForPaste(text.TrimEnd('\r', '\n'), session.Buffer.BracketedPaste) + (run ? "\r" : ""));
    }

    public static void MarkUsed(Snippet snippet)
    {
        snippet.LastUsed = DateTimeOffset.Now;
        AppServices.Current.Store.Save();
    }

    /// <summary>Runs the snippet on several devices at once in the background and shows what each printed.</summary>
    public static async Task RunOnDevicesAsync(Snippet snippet)
    {
        var app = AppServices.Current;
        var devices = app.Store.Data.Devices.Where(d => d.Connections.Any(c => c.Protocol == ConnectionProtocol.Ssh)).ToList();
        if (devices.Count == 0)
        {
            await Dialogs.AlertAsync("没有可用设备", "需要至少一台带 SSH 连接的设备。");
            return;
        }

        var boxes = devices.Select(d => new CheckBox { Content = $"{d.Name}（{d.Host}）", Tag = d }).ToList();
        var list = new StackPanel { MinWidth = 420 };
        foreach (var box in boxes) list.Children.Add(box);
        var picker = new ContentDialog
        {
            Title = $"在哪些设备上运行“{snippet.Name}”？",
            Content = new ScrollViewer { Content = list, MaxHeight = 360 },
            PrimaryButtonText = "运行",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await Dialogs.ShowAsync(picker) != ContentDialogResult.Primary) return;
        var chosen = boxes.Where(b => b.IsChecked == true).Select(b => (Device)b.Tag).ToList();
        if (chosen.Count == 0) return;
        if (await ResolveAsync(snippet) is not { } text) return;
        MarkUsed(snippet);

        var output = new StackPanel { Spacing = 12, MinWidth = 560 };
        var rows = chosen.ToDictionary(d => d, d =>
        {
            var body = new TextBlock { Text = "正在运行…", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono"), FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            output.Children.Add(Ui.Strong(d.Name));
            output.Children.Add(body);
            return body;
        });
        var results = new ContentDialog { Title = snippet.Name, Content = new ScrollViewer { Content = output, MaxHeight = 480 }, CloseButtonText = "关闭" };
        var shown = Dialogs.ShowAsync(results);

        await Task.WhenAll(chosen.Select(async device =>
        {
            try
            {
                var result = await Task.Run(() => app.Ssh.RunRemoteAsync(device, null, text, TimeSpan.FromMinutes(5)));
                string combined = (result.Output + result.Error).TrimEnd();
                rows[device].Text = (combined.Length > 0 ? combined : "（没有输出）") + (result.ExitCode != 0 ? $"\n退出代码 {result.ExitCode}" : "");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                rows[device].Text = "失败：" + ex.Message;
            }
        }));
        await shown;
    }
}
