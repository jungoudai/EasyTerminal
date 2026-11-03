using System.Text;
using EasyTerminal.Core.Terminal;
using EasyTerminal.Services;
using EasyTerminal.Sessions;
using Microsoft.Extensions.AI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace EasyTerminal.Assistant;

/// <summary>Chat with the built-in agent, drawn with ordinary WinUI controls.</summary>
public sealed class AgentPane : Grid
{
    private static readonly FontFamily MonoFont = new("Cascadia Mono");

    private readonly AppServices _app = AppServices.Current;
    private readonly AgentChat _chat;
    private readonly Func<SessionTab?> _activeTab;
    private readonly Action _openSettings;
    private readonly ScrollViewer _scroll = new() { HorizontalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly StackPanel _transcript = new() { Spacing = 12, Padding = new Thickness(12, 4, 12, 12) };
    private readonly TextBox _input = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MaxHeight = 180,
        PlaceholderText = "让 Agent 帮你查看终端、执行命令…（Enter 发送，Shift+Enter 换行）",
    };
    private readonly Button _send;
    private readonly Button _stop;
    private readonly InfoBar _setup = new() { Severity = InfoBarSeverity.Informational, IsClosable = false, Title = "配置模型后即可使用" };
    private readonly Dictionary<string, TextBlock> _toolCalls = new();
    private FrameworkElement? _welcome;
    private CancellationTokenSource? _running;

    public AgentPane(SessionManager sessions, Func<SessionTab?> activeTab, Action openSettings)
    {
        _activeTab = activeTab;
        _openSettings = openSettings;
        _chat = new AgentChat(new AgentTools(sessions, activeTab));
        _chat.Tools.ApproveAsync = AskApprovalAsync;

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _setup.Margin = new Thickness(10, 0, 10, 4);
        _setup.ActionButton = Ui.Button("打开设置", null, openSettings);
        Children.Add(_setup);

        _scroll.Content = _transcript;
        SetRow(_scroll, 1);
        Children.Add(_scroll);

        _send = Ui.Button("发送", "", () => _ = SendAsync(), accent: true);
        _stop = Ui.Button("停止", Ui.GlyphStop, () => _running?.Cancel());
        _stop.Visibility = Visibility.Collapsed;
        var actions = Ui.Columns("auto", "*", "auto", "auto");
        actions.Children.Add(Ui.IconButton(Ui.GlyphAdd, "新对话", NewConversation).InColumn(0));
        actions.Children.Add(Ui.IconButton("", "模型设置", openSettings).InColumn(2));
        var sendSlot = new Grid();
        sendSlot.Children.Add(_send);
        sendSlot.Children.Add(_stop);
        actions.Children.Add(sendSlot.InColumn(3));

        var composer = new StackPanel { Spacing = 6, Padding = new Thickness(10, 6, 10, 10) };
        composer.Children.Add(_input);
        composer.Children.Add(actions);
        SetRow(composer, 2);
        Children.Add(composer);

        _input.PreviewKeyDown += OnInputKeyDown;
        _app.SettingsChanged += UpdateSetupState;
        UpdateSetupState();
        ShowWelcome();
    }

    public void FocusInput() => _input.Focus(FocusState.Programmatic);

    /// <summary>Puts text into the box, such as a selection from a terminal, ready to be asked about.</summary>
    public void Prefill(string text)
    {
        _input.Text = text;
        _input.SelectionStart = text.Length;
        FocusInput();
    }

    private void UpdateSetupState()
    {
        var settings = _app.Store.Data.Settings.Agent;
        string? problem = AgentChat.ConfigurationProblem(settings, _app.Store.Secrets.Get(settings.ApiKeySecretId));
        _setup.Message = problem ?? "";
        _setup.IsOpen = problem != null;
    }

    private void NewConversation()
    {
        _running?.Cancel();
        _chat.Reset();
        _toolCalls.Clear();
        _transcript.Children.Clear();
        ShowWelcome();
        FocusInput();
    }

    private void ShowWelcome()
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(Ui.Strong("终端 Agent"));
        panel.Children.Add(Ui.Secondary("它能读取终端输出、在你确认后执行命令、打开设备连接。试试：", true));
        foreach (var prompt in new[] { "解释前台终端最后的报错，并给出修复办法", "看看这台机器的磁盘和内存占用", "列出我打开的会话和保存的设备" })
        {
            var button = new HyperlinkButton { Content = prompt, Padding = new Thickness(0, 2, 0, 2) };
            button.Click += (_, _) =>
            {
                _input.Text = prompt;
                _ = SendAsync();
            };
            panel.Children.Add(button);
        }
        _transcript.Children.Add(panel);
        _welcome = panel;
    }

    private void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        if (shift.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        e.Handled = true;
        if (_running == null) _ = SendAsync();
    }

    private async Task SendAsync()
    {
        string text = _input.Text.Trim();
        if (text.Length == 0 || _running != null) return;
        UpdateSetupState();
        if (_setup.IsOpen)
        {
            _openSettings();
            return;
        }

        if (_welcome != null) _transcript.Children.Remove(_welcome);
        _welcome = null;
        _input.Text = "";
        AddUserMessage(text);

        var running = new CancellationTokenSource();
        _running = running;
        _send.Visibility = Visibility.Collapsed;
        _stop.Visibility = Visibility.Visible;

        var reply = new StringBuilder();
        StackPanel? bubble = null;
        TextBlock? live = null;
        var thinking = Ui.Secondary("思考中…");
        _transcript.Children.Add(thinking);
        ScrollToEnd();
        long lastPaint = 0;

        void FinishBubble()
        {
            if (bubble == null) return;
            bubble.Children.Clear();
            foreach (var block in Markdown.Render(reply.ToString(), SendToTerminal)) bubble.Children.Add(block);
            bubble = null;
            live = null;
            reply.Clear();
        }

        try
        {
            await foreach (var update in _chat.SendAsync(text, running.Token))
            {
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case TextContent { Text.Length: > 0 } part:
                            if (bubble == null)
                            {
                                bubble = new StackPanel { Spacing = 6 };
                                live = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
                                bubble.Children.Add(live);
                                _transcript.Children.Insert(_transcript.Children.IndexOf(thinking), bubble);
                            }
                            reply.Append(part.Text);
                            // Repainting on every token is wasted work on a fast model.
                            if (Environment.TickCount64 - lastPaint > 40)
                            {
                                live!.Text = reply.ToString();
                                lastPaint = Environment.TickCount64;
                                ScrollToEnd();
                            }
                            break;
                        case FunctionCallContent call:
                            FinishBubble();
                            _transcript.Children.Insert(_transcript.Children.IndexOf(thinking), ToolCallView(call));
                            ScrollToEnd();
                            break;
                        case FunctionResultContent result when _toolCalls.TryGetValue(result.CallId, out var view):
                            string output = result.Result?.ToString() ?? result.Exception?.Message ?? "";
                            view.Text = output.Length > 4000 ? output[..4000] + "\n…" : output;
                            break;
                    }
                }
            }
            FinishBubble();
        }
        catch (OperationCanceledException)
        {
            if (live != null) reply.Append("\n\n（已停止）");
            FinishBubble();
        }
        catch (Exception ex)
        {
            FinishBubble();
            _transcript.Children.Add(new InfoBar { Severity = InfoBarSeverity.Error, IsOpen = true, IsClosable = true, Title = "请求失败", Message = ex.Message });
        }
        finally
        {
            _transcript.Children.Remove(thinking);
            _running = null;
            running.Dispose();
            _send.Visibility = Visibility.Visible;
            _stop.Visibility = Visibility.Collapsed;
            ScrollToEnd();
        }
    }

    private void AddUserMessage(string text)
    {
        var bubble = new Border
        {
            Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            Padding = new Thickness(12, 8, 12, 8),
            CornerRadius = new CornerRadius(8),
            Background = Ui.Brush("SubtleFillColorSecondaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(40, 0, 0, 0),
        };
        _transcript.Children.Add(bubble);
    }

    private FrameworkElement ToolCallView(FunctionCallContent call)
    {
        string summary = call.Name switch
        {
            "run_command" => "运行 " + Arg(call, "command"),
            "read_terminal" => "读取终端输出",
            "list_sessions" => "查看打开的会话",
            "list_devices" => "查看已保存的设备",
            "open_session" => "打开 " + Arg(call, "device"),
            "list_snippets" => "查看代码片段",
            _ => call.Name,
        };
        var output = new TextBlock { FontFamily = MonoFont, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Text = "…" };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(Ui.Icon(call.Name == "run_command" ? Ui.GlyphTerminal : "", 12));
        header.Children.Add(new TextBlock { Text = summary, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 320 });
        var box = new Expander
        {
            Header = header,
            Content = new ScrollViewer { Content = output, MaxHeight = 240 },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 8, 12, 8),
            MinHeight = 0,
        };
        _toolCalls[call.CallId] = output;
        return box;
    }

    private static string Arg(FunctionCallContent call, string name) =>
        call.Arguments != null && call.Arguments.TryGetValue(name, out var value) ? value?.ToString() ?? "" : "";

    /// <summary>A card in the conversation that holds the command until the user decides.</summary>
    private Task<bool> AskApprovalAsync(SessionTab tab, string command)
    {
        var decision = new TaskCompletionSource<bool>();
        var card = new StackPanel { Spacing = 8 };
        card.Children.Add(Ui.Secondary($"Agent 想在“{tab.Title}”中运行：", true));
        card.Children.Add(new TextBlock { Text = command, FontFamily = MonoFont, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var border = Ui.Card(card, 12);
        border.BorderBrush = Ui.Brush("AccentFillColorDefaultBrush");

        void Decide(bool run, string label)
        {
            card.Children.Remove(buttons);
            card.Children.Add(Ui.Secondary(label));
            border.BorderBrush = Ui.Brush("CardStrokeColorDefaultBrush");
            decision.TrySetResult(run);
        }

        buttons.Children.Add(Ui.Button("运行", Ui.GlyphPlay, () => Decide(true, "已允许"), accent: true));
        buttons.Children.Add(Ui.Button("本次对话都允许", null, () =>
        {
            _chat.Tools.AllowAllThisConversation = true;
            Decide(true, "已允许本次对话中的所有命令");
        }));
        buttons.Children.Add(Ui.Button("拒绝", null, () => Decide(false, "已拒绝")));
        card.Children.Add(buttons);

        // Insert above the "thinking" line so the order reads top to bottom.
        int at = Math.Max(0, _transcript.Children.Count - 1);
        _transcript.Children.Insert(at, border);
        ScrollToEnd();
        _running?.Token.Register(() => decision.TrySetResult(false));
        return decision.Task;
    }

    private void SendToTerminal(string code)
    {
        if (_activeTab() is not { Terminal: { State: SessionState.Running } terminal } tab)
        {
            _ = Dialogs.AlertAsync("没有正在运行的终端", "先打开一个终端会话。");
            return;
        }
        // Pasted, not run: the user reviews it at the prompt and presses Enter.
        terminal.Buffer.ScrollToBottom();
        terminal.Write(InputEncoder.PrepareForPaste(code.TrimEnd('\r', '\n'), terminal.Buffer.BracketedPaste));
        tab.View?.FocusTerminal();
    }

    private void ScrollToEnd()
    {
        // Wait for layout so the new height is known.
        _app.Dispatcher.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _scroll.ChangeView(null, _scroll.ScrollableHeight, null, true));
    }
}

/// <summary>Just enough Markdown for chat replies: paragraphs, headings, lists, inline code and code blocks.</summary>
internal static class Markdown
{
    private static readonly FontFamily MonoFont = new("Cascadia Mono");

    public static IEnumerable<FrameworkElement> Render(string text, Action<string> sendToTerminal)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var prose = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].TrimStart().StartsWith("```"))
            {
                prose.Add(lines[i]);
                continue;
            }
            if (Prose(prose) is { } block) yield return block;
            prose.Clear();
            string language = lines[i].Trim().TrimStart('`').Trim();
            var code = new List<string>();
            for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith("```"); i++) code.Add(lines[i]);
            yield return CodeBlock(string.Join('\n', code), language, sendToTerminal);
        }
        if (Prose(prose) is { } last) yield return last;
    }

    private static FrameworkElement? Prose(List<string> lines)
    {
        while (lines.Count > 0 && lines[0].Trim().Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        if (lines.Count == 0) return null;

        var rich = new RichTextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        Paragraph? paragraph = null;
        foreach (var raw in lines)
        {
            string line = raw.TrimEnd();
            if (line.Trim().Length == 0)
            {
                paragraph = null;
                continue;
            }
            bool heading = line.StartsWith('#');
            string body = heading ? line.TrimStart('#').Trim() : line;
            string trimmed = body.TrimStart();
            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ")) body = new string(' ', body.Length - trimmed.Length) + "• " + trimmed[2..];

            if (paragraph == null || heading)
            {
                paragraph = new Paragraph { Margin = new Thickness(0, rich.Blocks.Count > 0 ? 6 : 0, 0, 0) };
                rich.Blocks.Add(paragraph);
            }
            else
            {
                paragraph.Inlines.Add(new LineBreak());
            }
            AddInlines(paragraph.Inlines, body, heading);
            if (heading) paragraph = null;
        }
        return rich;
    }

    /// <summary>`code` and **bold**; anything else is plain text.</summary>
    private static void AddInlines(InlineCollection inlines, string text, bool bold)
    {
        int i = 0;
        var plain = new StringBuilder();
        void FlushPlain()
        {
            if (plain.Length == 0) return;
            var run = new Run { Text = plain.ToString() };
            if (bold) run.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            inlines.Add(run);
            plain.Clear();
        }
        while (i < text.Length)
        {
            if (text[i] == '`' && text.IndexOf('`', i + 1) is int end and > 0)
            {
                FlushPlain();
                inlines.Add(new Run { Text = text[(i + 1)..end], FontFamily = MonoFont, Foreground = Ui.Brush("AccentTextFillColorPrimaryBrush") });
                i = end + 1;
            }
            else if (text.AsSpan(i).StartsWith("**") && text.IndexOf("**", i + 2, StringComparison.Ordinal) is int close and > 0)
            {
                FlushPlain();
                inlines.Add(new Run { Text = text[(i + 2)..close], FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                i = close + 2;
            }
            else
            {
                plain.Append(text[i++]);
            }
        }
        FlushPlain();
    }

    private static FrameworkElement CodeBlock(string code, string language, Action<string> sendToTerminal)
    {
        var header = Ui.Columns("*", "auto", "auto");
        header.Children.Add(new TextBlock { Text = language, FontSize = 12, Foreground = Ui.Brush("TextFillColorSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center }.InColumn(0));
        header.Children.Add(Ui.IconButton(Ui.GlyphCopy, "复制", () =>
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(code);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }).InColumn(1));
        header.Children.Add(Ui.IconButton(Ui.GlyphTerminal, "粘贴到前台终端（不会自动回车）", () => sendToTerminal(code)).InColumn(2));

        var body = new ScrollViewer
        {
            Content = new TextBlock { Text = code, FontFamily = MonoFont, FontSize = 12.5, IsTextSelectionEnabled = true },
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            Padding = new Thickness(0, 0, 0, 6),
        };
        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(body);
        return new Border
        {
            Child = panel,
            Padding = new Thickness(10, 2, 4, 6),
            CornerRadius = new CornerRadius(6),
            Background = Ui.Brush("SubtleFillColorSecondaryBrush"),
            BorderBrush = Ui.Brush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
        };
    }
}
