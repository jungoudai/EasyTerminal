using System.IO.Ports;
using System.Text;

namespace EasyTerminal.Core.Transport;

/// <summary>A serial line, 8N1 without flow control, which is what console ports nearly always use.</summary>
public sealed class SerialTransport : ITerminalTransport
{
    private readonly string _portName;
    private readonly int _baudRate;
    private readonly CancellationTokenSource _stop = new();
    private SerialPort? _port;
    private int _exited;

    public event Action<ReadOnlyMemory<byte>>? DataReceived;
    public event Action<int?>? Exited;
    public int? ProcessId => null;

    public SerialTransport(string portName, int baudRate)
    {
        _portName = portName;
        _baudRate = baudRate;
    }

    public static string[] AvailablePorts() => SerialPort.GetPortNames().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();

    public void Start(int cols, int rows) => _ = Task.Run(RunAsync);

    private async Task RunAsync()
    {
        try
        {
            _port = new SerialPort(_portName, _baudRate, Parity.None, 8, StopBits.One) { Handshake = Handshake.None, DtrEnable = true, RtsEnable = true };
            _port.Open();
            DataReceived?.Invoke(Encoding.UTF8.GetBytes($"\x1b[2m已打开 {_portName}，{_baudRate} 波特\x1b[0m\r\n"));
            var buffer = new byte[4096];
            while (true)
            {
                int read = await _port.BaseStream.ReadAsync(buffer, _stop.Token);
                if (read == 0) break;
                DataReceived?.Invoke(buffer.AsMemory(0, read));
            }
            Finish(0);
        }
        catch (OperationCanceledException)
        {
            Finish(null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            if (_stop.IsCancellationRequested)
            {
                Finish(null);
                return;
            }
            string reason = ex is UnauthorizedAccessException ? "端口被其他程序占用" : ex.Message;
            DataReceived?.Invoke(Encoding.UTF8.GetBytes($"\x1b[31m无法使用 {_portName}：{reason}\x1b[0m\r\n"));
            Finish(1);
        }
    }

    private void Finish(int? code)
    {
        if (Interlocked.Exchange(ref _exited, 1) == 0) Exited?.Invoke(code);
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        try
        {
            if (_port is { IsOpen: true } port) port.BaseStream.Write(data);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // The read loop sees the same failure and reports it.
        }
    }

    public void Resize(int cols, int rows)
    {
    }

    public void Dispose()
    {
        _stop.Cancel();
        _port?.Dispose();
    }
}
