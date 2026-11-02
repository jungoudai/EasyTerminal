using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using EasyTerminal.Core.Terminal;
using EasyTerminal.Core.Transport;

namespace EasyTerminal.Core.Mosh;

/// <summary>Implemented by transports that deliver whole screens instead of a byte stream.</summary>
public interface IFramebufferTransport
{
    event Action<TerminalBuffer>? FrameReady;
}

public sealed record MoshEndpoint(string Host, int Port, string Base64Key);

public static partial class MoshBootstrap
{
    [GeneratedRegex(@"MOSH CONNECT (\d+) ([A-Za-z0-9/+]{22})")]
    private static partial Regex ConnectLine();

    public static (int Port, string Key)? ParseConnectLine(string output)
    {
        var match = ConnectLine().Match(output);
        return match.Success ? (int.Parse(match.Groups[1].Value), match.Groups[2].Value) : null;
    }

    // -s binds to the address the SSH connection arrived on; output is merged so errors are visible.
    public const string ServerCommand = "mosh-server new -s -c 256 -l LANG=en_US.UTF-8 2>&1";
}

/// <summary>
/// Native client for Mosh's State Synchronization Protocol. The server diffs against the newest state we
/// acknowledged, so every recent framebuffer is kept until it says they can be thrown away.
/// </summary>
public sealed class MoshTransport : ITerminalTransport, IFramebufferTransport
{
    private const int RetransmitMilliseconds = 250;
    private const int AckDelayMilliseconds = 100;
    private const int HeartbeatMilliseconds = 3000;
    private const int PortHopMilliseconds = 10000;
    private const ulong ShutdownState = ulong.MaxValue;

    private readonly Func<CancellationToken, Task<MoshEndpoint>> _bootstrap;
    private readonly object _lock = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SortedList<ulong, TerminalBuffer> _states = new();
    private readonly Dictionary<ushort, byte[]> _fragments = new();

    // Outgoing state: a flat event log; a state number is just a position in it.
    private readonly List<UserEvent> _events = new();
    private readonly SortedList<ulong, int> _sentStates = new() { [0] = 0 };
    private ulong _ackedRemote;
    private ulong _nextState = 1;
    private ulong _nextFragmentId;
    private ulong _sequence;
    private ulong _fragmentAssemblyId = ulong.MaxValue;
    private int _fragmentTotal = -1;
    private long _lastSendTicks;
    private long _lastHeardTicks;
    private bool _ackPending;
    private long _ackDueTicks;
    private ushort _peerTimestamp = 0xFFFF;
    private long _peerTimestampTicks;

    private AesOcb? _cipher;
    private Socket? _socket;
    private EndPoint? _remote;
    private int _cols, _rows;
    private int _exitRaised;

    public event Action<ReadOnlyMemory<byte>>? DataReceived;
    public event Action<int?>? Exited;
    public event Action<TerminalBuffer>? FrameReady;
    public int? ProcessId => null;

    /// <summary>Raised when the link has been silent long enough that the user should know.</summary>
    public event Action<TimeSpan?>? LinkStatusChanged;

    public MoshTransport(Func<CancellationToken, Task<MoshEndpoint>> bootstrap) => _bootstrap = bootstrap;

    public void Start(int cols, int rows)
    {
        _cols = cols;
        _rows = rows;
        _states[0] = new TerminalBuffer(cols, rows, 0);
        lock (_lock) _events.Add(new UserResizeEvent(cols, rows));
        _ = Task.Run(RunAsync);
    }

    private void Message(string text) => DataReceived?.Invoke(Encoding.UTF8.GetBytes(text));

    private async Task RunAsync()
    {
        try
        {
            Message("正在通过 SSH 启动 mosh-server…\r\n");
            var endpoint = await _bootstrap(_stop.Token);
            var addresses = await Dns.GetHostAddressesAsync(endpoint.Host, _stop.Token);
            var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.First();
            _remote = new IPEndPoint(address, endpoint.Port);
            _cipher = new AesOcb(Convert.FromBase64String(endpoint.Base64Key + "=="));
            Message($"\x1b[2J\x1b[HMosh 已连接 {address}:{endpoint.Port}，等待服务器画面…\r\n");
            OpenSocket();

            _lastHeardTicks = Environment.TickCount64;
            await Task.WhenAll(Task.Run(ReceiveLoop), Task.Run(TickLoop));
            Finish(0);
        }
        catch (OperationCanceledException)
        {
            Finish(0);
        }
        catch (Exception ex)
        {
            Message($"\r\n\x1b[31mMosh 连接失败：{ex.Message}\x1b[0m\r\n");
            Finish(1);
        }
    }

    private void OpenSocket()
    {
        var socket = new Socket(_remote!.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        socket.Connect(_remote);
        var previous = Interlocked.Exchange(ref _socket, socket);
        previous?.Dispose();
    }

    private async Task ReceiveLoop()
    {
        var buffer = new byte[65536];
        while (!_stop.IsCancellationRequested)
        {
            var socket = _socket!;
            int received;
            try
            {
                received = await socket.ReceiveAsync(buffer, SocketFlags.None, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                continue;
            }
            catch (SocketException)
            {
                // ICMP unreachable while roaming: wait for the next hop rather than giving up.
                await Task.Delay(200, _stop.Token).ContinueWith(_ => { }, TaskScheduler.Default);
                continue;
            }
            HandleDatagram(buffer.AsSpan(0, received));
        }
    }

    private void HandleDatagram(ReadOnlySpan<byte> datagram)
    {
        if (!MoshPacket.TryOpen(_cipher!, datagram, true, out _, out ushort timestamp, out var payload)) return;

        MoshInstruction? instruction = null;
        try
        {
            var fragment = MoshPacket.ParseFragment(payload);
            lock (_lock)
            {
                _lastHeardTicks = Environment.TickCount64;
                _peerTimestamp = timestamp;
                _peerTimestampTicks = _lastHeardTicks;
                var assembled = AddFragment(fragment);
                if (assembled != null) instruction = MoshInstruction.Parse(MoshPacket.Decompress(assembled));
            }
        }
        catch (InvalidDataException)
        {
            return;
        }

        LinkStatusChanged?.Invoke(null);
        if (instruction != null) ApplyInstruction(instruction);
    }

    private byte[]? AddFragment(MoshFragment fragment)
    {
        if (fragment.Id < _fragmentAssemblyId && _fragmentAssemblyId != ulong.MaxValue) return null;
        if (fragment.Id != _fragmentAssemblyId)
        {
            _fragmentAssemblyId = fragment.Id;
            _fragments.Clear();
            _fragmentTotal = -1;
        }
        _fragments[fragment.Number] = fragment.Contents;
        if (fragment.Final) _fragmentTotal = fragment.Number + 1;
        if (_fragmentTotal < 0 || _fragments.Count != _fragmentTotal) return null;

        using var output = new MemoryStream();
        for (ushort i = 0; i < _fragmentTotal; i++) output.Write(_fragments[i]);
        _fragments.Clear();
        _fragmentAssemblyId++;
        return output.ToArray();
    }

    private void ApplyInstruction(MoshInstruction instruction)
    {
        if (instruction.Version != MoshInstruction.ProtocolVersion)
        {
            Message("\r\n\x1b[31mMosh 协议版本不兼容。\x1b[0m\r\n");
            _stop.Cancel();
            return;
        }

        TerminalBuffer? latest = null;
        lock (_lock)
        {
            // Everything up to the acknowledged state is now known to the server.
            for (int i = _sentStates.Count - 1; i >= 0; i--)
            {
                if (_sentStates.Keys[i] < instruction.AckNum && _sentStates.Count > 1) _sentStates.RemoveAt(i);
            }

            if (instruction.NewNum == ShutdownState)
            {
                _stop.Cancel();
                return;
            }
            if (_states.ContainsKey(instruction.NewNum))
            {
                if (instruction.Diff.Length > 0) ScheduleAck();
                return;
            }
            if (!_states.TryGetValue(instruction.OldNum, out var reference)) return;

            var state = reference.Clone();
            try
            {
                foreach (var hostEvent in MoshMessages.ParseHost(instruction.Diff))
                {
                    switch (hostEvent)
                    {
                        case HostBytesEvent bytes: state.Write(bytes.Data); break;
                        case HostResizeEvent resize when resize.Width > 0 && resize.Height > 0: state.Resize(resize.Width, resize.Height); break;
                    }
                }
            }
            catch (InvalidDataException)
            {
                return;
            }

            _states[instruction.NewNum] = state;
            while (_states.Count > 1 && _states.Keys[0] < instruction.ThrowawayNum) _states.RemoveAt(0);
            // A server that stops acknowledging must not grow memory without bound.
            while (_states.Count > 64) _states.RemoveAt(0);

            _ackedRemote = _states.Keys[^1];
            // Acknowledging an empty ack would make both sides ping-pong forever.
            if (instruction.Diff.Length > 0) ScheduleAck();
            if (instruction.NewNum == _ackedRemote) latest = state;
        }
        if (latest != null) FrameReady?.Invoke(latest);
    }

    private void ScheduleAck()
    {
        if (_ackPending) return;
        _ackPending = true;
        _ackDueTicks = Environment.TickCount64 + AckDelayMilliseconds;
    }

    private async Task TickLoop()
    {
        long lastHop = Environment.TickCount64;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(50, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            long now = Environment.TickCount64;
            long sinceHeard = now - Volatile.Read(ref _lastHeardTicks);
            if (sinceHeard > PortHopMilliseconds && now - lastHop > PortHopMilliseconds)
            {
                // A new source port lets the session survive NAT rebinding and network changes.
                lastHop = now;
                OpenSocket();
                LinkStatusChanged?.Invoke(TimeSpan.FromMilliseconds(sinceHeard));
            }

            bool send;
            lock (_lock)
            {
                bool unsent = _events.Count > _sentStates.Values[^1];
                // Only keystrokes and resizes are retransmitted; a lost pure ack is covered by the next one.
                bool unacked = _sentStates.Values[^1] > _sentStates.Values[0];
                send = unsent
                    || (unacked && now - _lastSendTicks >= RetransmitMilliseconds)
                    || (_ackPending && now >= _ackDueTicks)
                    || now - _lastSendTicks >= HeartbeatMilliseconds;
            }
            if (send) SendState();
        }
    }

    private void SendState()
    {
        byte[] datagrams;
        var packets = new List<byte[]>();
        lock (_lock)
        {
            int current = _events.Count;
            ulong newNum;
            if (current > _sentStates.Values[^1] || _sentStates.Count == 1)
            {
                // A pure ack or heartbeat still takes a fresh number, like Mosh's send_empty_ack.
                newNum = _nextState++;
                _sentStates[newNum] = current;
            }
            else
            {
                newNum = _sentStates.Keys[^1];
            }

            ulong oldNum = _sentStates.Keys[0];
            int from = _sentStates.Values[0];
            var diff = from < current ? MoshMessages.SerializeUser(_events.Skip(from).Take(current - from)) : Array.Empty<byte>();

            var instruction = new MoshInstruction
            {
                OldNum = oldNum,
                NewNum = newNum,
                AckNum = _ackedRemote,
                ThrowawayNum = oldNum,
                Diff = diff,
            };
            var compressed = MoshPacket.Compress(instruction.Serialize());
            ushort now = (ushort)(Environment.TickCount64 & 0xFFFF);
            // Echo the peer's clock adjusted for how long we held it, so it can measure RTT.
            ushort reply = _peerTimestamp == 0xFFFF ? (ushort)0xFFFF : (ushort)(_peerTimestamp + (Environment.TickCount64 - _peerTimestampTicks));
            foreach (var fragment in MoshPacket.Fragment(_nextFragmentId++, compressed))
                packets.Add(MoshPacket.Seal(_cipher!, _sequence++, false, now, reply, MoshPacket.SerializeFragment(fragment)));

            _ackPending = false;
            _lastSendTicks = Environment.TickCount64;
            CompactEvents();
        }

        foreach (var packet in packets)
        {
            datagrams = packet;
            try
            {
                _socket?.Send(datagrams);
            }
            catch (SocketException)
            {
                // Retransmitted on the next tick once the network returns.
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void CompactEvents()
    {
        int acknowledged = _sentStates.Values[0];
        if (acknowledged < 1024) return;
        _events.RemoveRange(0, acknowledged);
        for (int i = 0; i < _sentStates.Count; i++) _sentStates.SetValueAtIndex(i, _sentStates.Values[i] - acknowledged);
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        var keys = data.ToArray();
        lock (_lock) _events.Add(new KeystrokeEvent(keys));
    }

    public void Resize(int cols, int rows)
    {
        _cols = cols;
        _rows = rows;
        lock (_lock) _events.Add(new UserResizeEvent(cols, rows));
    }

    private void Finish(int code)
    {
        if (Interlocked.Exchange(ref _exitRaised, 1) == 0) Exited?.Invoke(code);
    }

    public void Dispose()
    {
        if (_cipher != null && _remote != null && !_stop.IsCancellationRequested)
        {
            // Best effort: tell the server to end now rather than linger until its idle timeout.
            try
            {
                var instruction = new MoshInstruction { OldNum = _sentStates.Keys[0], NewNum = ShutdownState, AckNum = _ackedRemote, ThrowawayNum = _sentStates.Keys[0] };
                var compressed = MoshPacket.Compress(instruction.Serialize());
                foreach (var fragment in MoshPacket.Fragment(_nextFragmentId++, compressed))
                    _socket?.Send(MoshPacket.Seal(_cipher, _sequence++, false, (ushort)Environment.TickCount64, 0xFFFF, MoshPacket.SerializeFragment(fragment)));
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
            }
        }
        _stop.Cancel();
        _socket?.Dispose();
        Finish(0);
    }
}
