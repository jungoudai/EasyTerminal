using System.Text;

namespace EasyTerminal.Core.Transport;

/// <summary>
/// Lets a session start at once while slow preparation, such as probing addresses, runs in the
/// background and reports into the terminal. Input typed meanwhile is delivered once the real transport is up.
/// </summary>
public sealed class DeferredTransport : ITerminalTransport
{
    private readonly Func<Action<string>, CancellationToken, Task<ITerminalTransport>> _factory;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _lock = new();
    private readonly List<byte[]> _pendingInput = new();
    private ITerminalTransport? _inner;
    private int _cols, _rows;
    private int _exited;

    public event Action<ReadOnlyMemory<byte>>? DataReceived;
    public event Action<int?>? Exited;
    public int? ProcessId => _inner?.ProcessId;

    public DeferredTransport(Func<Action<string>, CancellationToken, Task<ITerminalTransport>> factory) => _factory = factory;

    public void Start(int cols, int rows)
    {
        _cols = cols;
        _rows = rows;
        _ = Task.Run(RunAsync);
    }

    private void Print(string text) => DataReceived?.Invoke(Encoding.UTF8.GetBytes(text));

    private async Task RunAsync()
    {
        try
        {
            var inner = await _factory(line => Print($"\x1b[2m{line}\x1b[0m\r\n"), _stop.Token);
            inner.DataReceived += data => DataReceived?.Invoke(data);
            inner.Exited += RaiseExited;
            int cols, rows;
            lock (_lock)
            {
                if (_stop.IsCancellationRequested)
                {
                    inner.Dispose();
                    return;
                }
                _inner = inner;
                cols = _cols;
                rows = _rows;
            }
            inner.Start(cols, rows);
            lock (_lock)
            {
                foreach (var input in _pendingInput) inner.Write(input);
                _pendingInput.Clear();
            }
        }
        catch (OperationCanceledException)
        {
            RaiseExited(null);
        }
        catch (Exception ex)
        {
            Print($"\x1b[31m无法连接：{ex.Message}\x1b[0m\r\n");
            RaiseExited(1);
        }
    }

    private void RaiseExited(int? code)
    {
        if (Interlocked.Exchange(ref _exited, 1) == 0) Exited?.Invoke(code);
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            if (_inner != null && _pendingInput.Count == 0) _inner.Write(data);
            else _pendingInput.Add(data.ToArray());
        }
    }

    public void Resize(int cols, int rows)
    {
        lock (_lock)
        {
            _cols = cols;
            _rows = rows;
            _inner?.Resize(cols, rows);
        }
    }

    public void Dispose()
    {
        ITerminalTransport? inner;
        lock (_lock)
        {
            _stop.Cancel();
            inner = _inner;
        }
        inner?.Dispose();
    }
}
