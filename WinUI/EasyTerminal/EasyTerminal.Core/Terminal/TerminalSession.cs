using EasyTerminal.Core.Mosh;
using EasyTerminal.Core.Transport;

namespace EasyTerminal.Core.Terminal;

public enum SessionState { Starting, Running, Exited, Failed }

/// <summary>Couples a transport to a terminal buffer and survives reconnects with the same buffer.</summary>
public sealed class TerminalSession : IDisposable
{
    private readonly Func<ITerminalTransport> _transportFactory;
    private ITerminalTransport? _transport;
    private int _cols = 120, _rows = 30;
    private readonly object _logLock = new();
    private FileStream? _log;

    public TerminalBuffer Buffer { get; }
    public SessionState State { get; private set; } = SessionState.Starting;
    public int? ExitCode { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public int? ProcessId => _transport?.ProcessId;

    /// <summary>Raised on any thread when new output is ready to draw.</summary>
    public event Action? Updated;
    public event Action? StateChanged;

    public TerminalSession(Func<ITerminalTransport> transportFactory, int scrollback)
    {
        _transportFactory = transportFactory;
        Buffer = new TerminalBuffer(_cols, _rows, scrollback);
        Buffer.Response += bytes => _transport?.Write(bytes);
    }

    public void Start(int cols, int rows)
    {
        _cols = cols;
        _rows = rows;
        Buffer.Resize(cols, rows);
        Connect();
    }

    /// <summary>Tears down the current transport and connects again, keeping scrollback.</summary>
    public void Restart()
    {
        DetachTransport();
        if (State != SessionState.Starting) Buffer.Write("\r\n\x1b[2m--- 正在重新连接 ---\x1b[0m\r\n");
        Connect();
    }

    private void Connect()
    {
        State = SessionState.Starting;
        ExitCode = null;
        Error = null;
        StartedAt = DateTimeOffset.Now;
        StateChanged?.Invoke();
        ITerminalTransport? transport = null;
        try
        {
            transport = _transportFactory();
            _transport = transport;
            transport.DataReceived += OnData;
            transport.Exited += OnExited;
            if (transport is IFramebufferTransport frames) frames.FrameReady += OnFrame;
            transport.Start(_cols, _rows);
            State = SessionState.Running;
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            State = SessionState.Failed;
            Buffer.Write($"\x1b[31m无法启动会话：{ex.Message}\x1b[0m\r\n");
            if (transport != null) DetachTransport();
            StateChanged?.Invoke();
            Updated?.Invoke();
        }
    }

    private void DetachTransport()
    {
        var transport = Interlocked.Exchange(ref _transport, null);
        if (transport == null) return;
        transport.DataReceived -= OnData;
        transport.Exited -= OnExited;
        if (transport is IFramebufferTransport frames) frames.FrameReady -= OnFrame;
        transport.Dispose();
    }

    /// <summary>Records everything the session prints to a file, control sequences included, so <c>cat</c> replays it.</summary>
    public void StartLog(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _log = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
    }

    private void OnData(ReadOnlyMemory<byte> data)
    {
        Buffer.Write(data.Span);
        try
        {
            lock (_logLock) _log?.Write(data.Span);
        }
        catch (IOException)
        {
            // A full disk must not take the session down with it.
        }
        Updated?.Invoke();
    }

    private void OnFrame(TerminalBuffer frame)
    {
        lock (frame.SyncRoot) Buffer.CopyFrom(frame);
        Updated?.Invoke();
    }

    private void OnExited(int? code)
    {
        ExitCode = code;
        State = SessionState.Exited;
        StateChanged?.Invoke();
        Updated?.Invoke();
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (State is SessionState.Exited or SessionState.Failed) return;
        _transport?.Write(data);
    }

    public void Write(string text) => Write(System.Text.Encoding.UTF8.GetBytes(text));

    public void Resize(int cols, int rows)
    {
        if (cols == _cols && rows == _rows) return;
        _cols = cols;
        _rows = rows;
        Buffer.Resize(cols, rows);
        _transport?.Resize(cols, rows);
        Updated?.Invoke();
    }

    public void Dispose()
    {
        DetachTransport();
        lock (_logLock)
        {
            _log?.Dispose();
            _log = null;
        }
    }
}
