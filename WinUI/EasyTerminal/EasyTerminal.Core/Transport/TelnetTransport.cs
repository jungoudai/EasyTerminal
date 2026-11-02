using System.Net.Sockets;
using System.Text;

namespace EasyTerminal.Core.Transport;

/// <summary>Telnet with the option negotiation a modern terminal needs: echo, binary, terminal type and window size.</summary>
public sealed class TelnetTransport : ITerminalTransport
{
    private const byte Iac = 255, Dont = 254, Do = 253, Wont = 252, Will = 251, Sb = 250, Se = 240;
    private const byte OptBinary = 0, OptEcho = 1, OptSuppressGoAhead = 3, OptTerminalType = 24, OptWindowSize = 31;

    private readonly string _host;
    private readonly int _port;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _writeLock = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private int _cols, _rows;
    private bool _windowSizeAgreed;
    private int _exited;

    public event Action<ReadOnlyMemory<byte>>? DataReceived;
    public event Action<int?>? Exited;
    public int? ProcessId => null;

    public TelnetTransport(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public void Start(int cols, int rows)
    {
        _cols = cols;
        _rows = rows;
        _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        try
        {
            _client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await _client.ConnectAsync(_host, _port, timeout.Token);
            _stream = _client.GetStream();

            var filter = new TelnetFilter();
            var buffer = new byte[16384];
            while (true)
            {
                int read = await _stream.ReadAsync(buffer, _stop.Token);
                if (read == 0) break;
                var (text, reply) = filter.Process(buffer.AsSpan(0, read));
                // The size follows the reply: a server only expects it once WILL has been said.
                if (reply.Length > 0) WriteRaw(reply);
                if (filter.WindowSizeAgreed && !_windowSizeAgreed)
                {
                    _windowSizeAgreed = true;
                    SendWindowSize();
                }
                if (text.Length > 0) DataReceived?.Invoke(text);
            }
            Finish(0);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            Finish(null);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            DataReceived?.Invoke(Encoding.UTF8.GetBytes($"\x1b[31m无法连接 {_host}:{_port}：{ex.Message}\x1b[0m\r\n"));
            Finish(1);
        }
    }

    private void Finish(int? code)
    {
        if (Interlocked.Exchange(ref _exited, 1) == 0) Exited?.Invoke(code);
    }

    private void WriteRaw(ReadOnlySpan<byte> data)
    {
        lock (_writeLock)
        {
            try
            {
                _stream?.Write(data);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // The read loop sees the same failure and reports it.
            }
        }
    }

    public void Write(ReadOnlySpan<byte> data) => WriteRaw(TelnetFilter.EscapeOutgoing(data));

    private void SendWindowSize() => WriteRaw(TelnetFilter.WindowSizeMessage(_cols, _rows));

    public void Resize(int cols, int rows)
    {
        _cols = cols;
        _rows = rows;
        if (_windowSizeAgreed) SendWindowSize();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _client?.Dispose();
    }

    /// <summary>Separates terminal data from telnet commands, keeping partial commands across reads.</summary>
    public sealed class TelnetFilter
    {
        private enum Mode { Data, Command, Option, Subnegotiation, SubnegotiationCommand }

        private readonly List<byte> _sub = new();
        private Mode _mode;
        private byte _verb;

        public bool WindowSizeAgreed { get; private set; }

        public (byte[] Text, byte[] Reply) Process(ReadOnlySpan<byte> input)
        {
            var text = new List<byte>(input.Length);
            var reply = new List<byte>();
            foreach (byte b in input)
            {
                switch (_mode)
                {
                    case Mode.Data:
                        if (b == Iac) _mode = Mode.Command;
                        else text.Add(b);
                        break;
                    case Mode.Command:
                        if (b == Iac)
                        {
                            text.Add(Iac);
                            _mode = Mode.Data;
                        }
                        else if (b is Will or Wont or Do or Dont)
                        {
                            _verb = b;
                            _mode = Mode.Option;
                        }
                        else if (b == Sb)
                        {
                            _sub.Clear();
                            _mode = Mode.Subnegotiation;
                        }
                        else
                        {
                            _mode = Mode.Data;
                        }
                        break;
                    case Mode.Option:
                        Negotiate(_verb, b, reply);
                        _mode = Mode.Data;
                        break;
                    case Mode.Subnegotiation:
                        if (b == Iac) _mode = Mode.SubnegotiationCommand;
                        else _sub.Add(b);
                        break;
                    case Mode.SubnegotiationCommand:
                        if (b == Se)
                        {
                            Subnegotiate(reply);
                            _mode = Mode.Data;
                        }
                        else
                        {
                            _sub.Add(b);
                            _mode = Mode.Subnegotiation;
                        }
                        break;
                }
            }
            return (text.ToArray(), reply.ToArray());
        }

        private void Negotiate(byte verb, byte option, List<byte> reply)
        {
            switch (verb)
            {
                case Will:
                    bool wanted = option is OptEcho or OptSuppressGoAhead or OptBinary;
                    reply.AddRange(new[] { Iac, wanted ? Do : Dont, option });
                    break;
                case Do:
                    bool offer = option is OptTerminalType or OptWindowSize or OptSuppressGoAhead or OptBinary;
                    reply.AddRange(new[] { Iac, offer ? Will : Wont, option });
                    if (option == OptWindowSize) WindowSizeAgreed = true;
                    break;
                case Wont:
                    reply.AddRange(new[] { Iac, Dont, option });
                    break;
                case Dont:
                    reply.AddRange(new[] { Iac, Wont, option });
                    if (option == OptWindowSize) WindowSizeAgreed = false;
                    break;
            }
        }

        private void Subnegotiate(List<byte> reply)
        {
            // SEND (1) for terminal type is answered with IS (0) and the name.
            if (_sub.Count >= 2 && _sub[0] == OptTerminalType && _sub[1] == 1)
            {
                reply.AddRange(new byte[] { Iac, Sb, OptTerminalType, 0 });
                reply.AddRange(Encoding.ASCII.GetBytes("xterm-256color"));
                reply.AddRange(new[] { Iac, Se });
            }
        }

        public static byte[] EscapeOutgoing(ReadOnlySpan<byte> data)
        {
            var result = new List<byte>(data.Length);
            foreach (byte b in data)
            {
                result.Add(b);
                if (b == Iac) result.Add(Iac);
            }
            return result.ToArray();
        }

        public static byte[] WindowSizeMessage(int cols, int rows)
        {
            var result = new List<byte> { Iac, Sb, OptWindowSize };
            foreach (byte b in new[] { (byte)(cols >> 8), (byte)cols, (byte)(rows >> 8), (byte)rows })
            {
                result.Add(b);
                if (b == Iac) result.Add(Iac);
            }
            result.AddRange(new[] { Iac, Se });
            return result.ToArray();
        }
    }
}
