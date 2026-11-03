using System.ClientModel;
using System.ComponentModel;
using System.Text;
using Anthropic;
using EasyTerminal.Core.Models;
using EasyTerminal.Core.Terminal;
using EasyTerminal.Services;
using EasyTerminal.Sessions;
using Microsoft.Extensions.AI;
using OpenAI;

namespace EasyTerminal.Assistant;

/// <summary>
/// The built-in agent: a chat with a model that can look at and type into the app's terminals.
/// Microsoft.Extensions.AI runs the tool-calling loop; the provider is any <see cref="IChatClient"/>.
/// </summary>
public sealed class AgentChat
{
    private const string SystemPrompt = """
        你是 EasyTerminal 内置的终端助手，运行在用户的 Windows 电脑上。EasyTerminal 管理本机 Shell、WSL、Docker 以及通过 SSH/Mosh 连接的远程设备。
        你可以用工具查看打开的会话、读取终端里的内容、在终端里执行命令、查看已保存的设备和代码片段。
        工作方式：
        - 回答与终端有关的问题前，先用 read_terminal 看清楚当前输出，不要猜测。
        - 需要执行命令时用 run_command。每条命令都可能需要用户确认；被拒绝时不要换个写法重试同一件事，改为说明原因或给出替代方案。
        - 删除数据、重启服务、修改系统配置等有风险的操作，先说明影响再执行。
        - 注意目标会话的系统（Linux、Windows cmd/PowerShell 等），给出匹配的命令。
        - 回答简洁，用用户使用的语言。命令和代码放在 Markdown 代码块里。
        """;

    private readonly List<ChatMessage> _history = new();
    private IChatClient? _client;
    private string _clientKey = "";

    public AgentTools Tools { get; }

    public AgentChat(AgentTools tools)
    {
        Tools = tools;
        Reset();
    }

    public void Reset()
    {
        _history.Clear();
        _history.Add(new ChatMessage(ChatRole.System, SystemPrompt));
        Tools.AllowAllThisConversation = false;
    }

    /// <summary>Null when the agent can be used, otherwise what still needs setting up.</summary>
    public static string? ConfigurationProblem(AgentSettings settings, string? apiKey)
    {
        if (settings.Model.Trim().Length == 0) return "还没有填写模型名称。";
        if (!Uri.TryCreate(settings.Endpoint.Trim(), UriKind.Absolute, out _) && !(settings.Provider == AgentProvider.Anthropic && settings.Endpoint.Trim().Length == 0))
            return "API 地址无效。";
        bool local = Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var uri) && uri.IsLoopback;
        if (string.IsNullOrEmpty(apiKey) && !local) return "还没有填写 API 密钥。";
        return null;
    }

    private static IChatClient CreateProviderClient(AgentSettings settings, string? apiKey)
    {
        if (settings.Provider == AgentProvider.Anthropic)
        {
            var anthropic = settings.Endpoint.Trim().Length > 0
                ? new AnthropicClient { ApiKey = apiKey ?? "", BaseUrl = settings.Endpoint.Trim() }
                : new AnthropicClient { ApiKey = apiKey ?? "" };
            return anthropic.AsIChatClient(settings.Model.Trim(), 8192);
        }
        // Local servers such as Ollama ignore the key, but the client insists on one.
        var options = new OpenAIClientOptions { Endpoint = new Uri(settings.Endpoint.Trim()) };
        return new OpenAI.Chat.ChatClient(settings.Model.Trim(), new ApiKeyCredential(string.IsNullOrEmpty(apiKey) ? "none" : apiKey), options).AsIChatClient();
    }

    /// <summary>One tiny request, so the settings page can tell whether the address, model and key work.</summary>
    public static async Task<string> TestAsync(AgentSettings settings, string? apiKey, CancellationToken cancel)
    {
        if (ConfigurationProblem(settings, apiKey) is { } problem) return problem;
        using var client = CreateProviderClient(settings, apiKey);
        var response = await client.GetResponseAsync("Reply with the single word: ok", new ChatOptions { MaxOutputTokens = 16 }, cancel);
        return "连接成功，模型回复：" + response.Text.Trim();
    }

    private IChatClient Client(AgentSettings settings, string? apiKey)
    {
        string key = $"{settings.Provider}|{settings.Endpoint}|{settings.Model}|{apiKey}";
        if (_client != null && key == _clientKey) return _client;
        _client?.Dispose();
        _client = new ChatClientBuilder(CreateProviderClient(settings, apiKey))
            .UseFunctionInvocation(configure: f =>
            {
                f.AllowConcurrentInvocation = false;
                f.MaximumIterationsPerRequest = 24;
            })
            .Build();
        _clientKey = key;
        return _client;
    }

    /// <summary>Sends a message and streams the reply, tool calls and their results included.</summary>
    public async IAsyncEnumerable<ChatResponseUpdate> SendAsync(string text, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancel)
    {
        var app = AppServices.Current;
        var settings = app.Store.Data.Settings.Agent;
        var client = Client(settings, app.Store.Secrets.Get(settings.ApiKeySecretId));
        _history.Add(new ChatMessage(ChatRole.User, text));

        var options = new ChatOptions { Tools = Tools.All, ToolMode = ChatToolMode.Auto };
        var received = new List<ChatResponseUpdate>();
        try
        {
            await foreach (var update in client.GetStreamingResponseAsync(_history, options, cancel))
            {
                received.Add(update);
                yield return update;
            }
        }
        finally
        {
            // Keep whatever arrived, even when stopped, so the next question has the context.
            if (received.Count > 0) _history.AddMessages(received);
            else _history.RemoveAt(_history.Count - 1);
        }
    }
}

/// <summary>What the agent can do in the app. Commands go through <see cref="ApproveAsync"/> first.</summary>
public sealed class AgentTools
{
    private readonly SessionManager _sessions;
    private readonly Func<SessionTab?> _activeTab;
    private readonly Dictionary<SessionTab, int> _ids = new();
    private int _nextId = 1;

    /// <summary>Asks the user whether a command may run in a session.</summary>
    public Func<SessionTab, string, Task<bool>>? ApproveAsync { get; set; }
    public bool AllowAllThisConversation { get; set; }
    public IList<AITool> All { get; }

    public AgentTools(SessionManager sessions, Func<SessionTab?> activeTab)
    {
        _sessions = sessions;
        _activeTab = activeTab;
        All = new List<AITool>
        {
            AIFunctionFactory.Create(ListSessions, "list_sessions", "列出 EasyTerminal 中打开的会话（id、标题、类型、设备、状态，以及哪个在前台）。"),
            AIFunctionFactory.Create(ReadTerminal, "read_terminal", "读取某个终端会话最近的输出文本（包括回滚区）。不指定 session_id 时读取前台会话。"),
            AIFunctionFactory.Create(RunCommand, "run_command", "在终端会话中输入一条命令并回车，等待输出稳定后返回新增的输出。不指定 session_id 时在前台会话中运行。交互式程序（如 vim、top）不适合用它。"),
            AIFunctionFactory.Create(ListDevices, "list_devices", "列出已保存的设备：名称、地址、用户、系统和可用的连接。"),
            AIFunctionFactory.Create(OpenSession, "open_session", "打开设备上的一个终端连接（SSH、Mosh 等），返回新会话的 id。"),
            AIFunctionFactory.Create(ListSnippets, "list_snippets", "列出用户保存的代码片段（名称、说明、内容）。"),
        };
    }

    private int IdOf(SessionTab tab)
    {
        if (!_ids.TryGetValue(tab, out int id)) _ids[tab] = id = _nextId++;
        return id;
    }

    private SessionTab? Find(int? id)
    {
        foreach (var gone in _ids.Keys.Where(t => !_sessions.Tabs.Contains(t)).ToList()) _ids.Remove(gone);
        if (id is null or 0) return _activeTab();
        return _sessions.Tabs.FirstOrDefault(t => IdOf(t) == id);
    }

    private static Task<T> OnUi<T>(Func<Task<T>> work)
    {
        var dispatcher = AppServices.Current.Dispatcher;
        if (dispatcher.HasThreadAccess) return work();
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.TryEnqueue(async () =>
        {
            try
            {
                result.TrySetResult(await work());
            }
            catch (Exception ex)
            {
                result.TrySetException(ex);
            }
        });
        return result.Task;
    }

    private Task<string> ListSessions() => OnUi(() =>
    {
        var active = _activeTab();
        var sb = new StringBuilder();
        foreach (var tab in _sessions.Tabs)
        {
            string kind = tab.Terminal != null ? "终端" : tab.Sftp != null ? "SFTP" : tab.Rdp != null ? "远程桌面" : tab.Web != null ? "网页" : "面板";
            string where = tab.IsLocal ? "本机 " + tab.LocalTarget!.Name : tab.Device?.Name ?? "";
            sb.AppendLine($"id={IdOf(tab)} 标题={tab.Title} 类型={kind} 位置={where} 状态={tab.StatusText.ReplaceLineEndings(" ")}{(tab == active ? " [前台]" : "")}");
        }
        return Task.FromResult(sb.Length == 0 ? "没有打开的会话。" : sb.ToString());
    });

    private Task<string> ReadTerminal(
        [Description("会话 id，来自 list_sessions；省略表示前台会话")] int? session_id = null,
        [Description("读取最后多少行，默认 80，最多 400")] int lines = 80) => OnUi(() =>
    {
        if (Find(session_id) is not { Terminal: { } terminal } tab) return Task.FromResult("找不到这个终端会话。用 list_sessions 查看可用的会话。");
        return Task.FromResult($"[{tab.Title}]\n" + Tail(terminal.Buffer, Math.Clamp(lines, 1, 400)));
    });

    private Task<string> RunCommand(
        [Description("要执行的命令，一行")] string command,
        [Description("会话 id，来自 list_sessions；省略表示前台会话")] int? session_id = null,
        [Description("最长等待秒数，默认 20，最多 120")] int timeout_seconds = 20) => OnUi(async () =>
    {
        if (Find(session_id) is not { Terminal: { } terminal } tab) return "找不到这个终端会话。用 list_sessions 查看可用的会话。";
        if (terminal.State != SessionState.Running) return $"会话 {tab.Title} 没有在运行（{tab.StatusText}）。";
        command = command.Trim().ReplaceLineEndings(" ");
        if (command.Length == 0) return "命令是空的。";
        if (!AllowAllThisConversation && !AppServices.Current.Store.Data.Settings.Agent.AutoApproveCommands)
        {
            if (ApproveAsync == null || !await ApproveAsync(tab, command)) return "用户拒绝执行这条命令。";
        }

        var buffer = terminal.Buffer;
        long start;
        lock (buffer.SyncRoot)
        {
            buffer.ScrollToBottom();
            start = buffer.AbsoluteViewportRow(buffer.CursorRow);
        }

        long lastOutput = Environment.TickCount64;
        void OnUpdated() => Interlocked.Exchange(ref lastOutput, Environment.TickCount64);
        terminal.Updated += OnUpdated;
        try
        {
            terminal.Write(InputEncoder.PrepareForPaste(command, buffer.BracketedPaste) + "\r");
            long began = Environment.TickCount64;
            long limit = Math.Clamp(timeout_seconds, 1, 120) * 1000L;
            bool finished = false;
            while (Environment.TickCount64 - began < limit)
            {
                await Task.Delay(200);
                // Quiet for a while after at least some time has passed: the command is done or waiting.
                if (Environment.TickCount64 - began > 600 && Environment.TickCount64 - Interlocked.Read(ref lastOutput) > 1200)
                {
                    finished = true;
                    break;
                }
            }
            string output = Since(buffer, start);
            return (finished ? "" : $"[{timeout_seconds} 秒后仍在输出，以下是目前的内容，命令可能还在运行]\n") + output;
        }
        finally
        {
            terminal.Updated -= OnUpdated;
        }
    });

    private Task<string> ListDevices() => OnUi(() =>
    {
        var sb = new StringBuilder();
        foreach (var device in AppServices.Current.Store.Data.Devices)
        {
            sb.Append($"名称={device.Name} 地址={device.Host}:{device.Port} 用户={device.User}");
            if (device.Facts is { } facts) sb.Append(" 系统=" + Ui.DescribeFacts(facts));
            sb.Append(" 连接=" + string.Join("、", device.Connections.Where(device.Shows).Select(c => $"{c.Name}({Ui.Name(c.Protocol)})")));
            sb.AppendLine();
        }
        return Task.FromResult(sb.Length == 0 ? "还没有保存的设备。" : sb.ToString());
    });

    private Task<string> OpenSession(
        [Description("设备名称")] string device,
        [Description("连接名称；省略时使用第一个 SSH 连接")] string? connection = null) => OnUi(async () =>
    {
        var target = AppServices.Current.Store.Data.Devices.FirstOrDefault(d => d.Name.Equals(device, StringComparison.OrdinalIgnoreCase));
        if (target == null) return "找不到这个设备。用 list_devices 查看已保存的设备。";
        var chosen = connection is { Length: > 0 }
            ? target.Connections.FirstOrDefault(c => c.Name.Equals(connection, StringComparison.OrdinalIgnoreCase))
            : target.Connections.FirstOrDefault(c => c.Protocol == ConnectionProtocol.Ssh);
        if (chosen == null) return "这个设备上没有匹配的连接。";
        int before = _sessions.Tabs.Count;
        await _sessions.OpenConnectionAsync(target, chosen);
        if (_sessions.Tabs.Count == before) return "没有打开新的会话。";
        var tab = _sessions.Tabs[^1];
        // Give the login a moment so the first read shows a prompt.
        for (int i = 0; i < 25 && tab.Terminal?.State == SessionState.Starting; i++) await Task.Delay(200);
        await Task.Delay(800);
        return $"已打开 id={IdOf(tab)} 标题={tab.Title} 状态={tab.StatusText.ReplaceLineEndings(" ")}";
    });

    private Task<string> ListSnippets() => OnUi(() =>
    {
        var sb = new StringBuilder();
        foreach (var snippet in AppServices.Current.Store.Data.Snippets)
            sb.AppendLine($"名称={snippet.Name} 说明={snippet.Description}\n{snippet.Body}\n");
        return Task.FromResult(sb.Length == 0 ? "还没有代码片段。" : sb.ToString());
    });

    // ---- Terminal text -------------------------------------------------------------------

    private const int MaxChars = 12000;

    private static long LastRow(TerminalBuffer buffer) => buffer.AbsoluteViewportRow(buffer.Rows - 1) + buffer.ScrollOffset;

    public static string Tail(TerminalBuffer buffer, int lines)
    {
        lock (buffer.SyncRoot)
        {
            long last = LastRow(buffer);
            return Clean(buffer.GetText(last - lines + 1, 0, last, buffer.Cols - 1));
        }
    }

    private static string Since(TerminalBuffer buffer, long start)
    {
        lock (buffer.SyncRoot) return Clean(buffer.GetText(start, 0, LastRow(buffer), buffer.Cols - 1));
    }

    private static string Clean(string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n').Select(l => l.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        string result = string.Join('\n', lines);
        return result.Length > MaxChars ? "…（前面的内容已省略）\n" + result[^MaxChars..] : result;
    }
}
