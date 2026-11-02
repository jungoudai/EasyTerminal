using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using EasyTerminal.Core.Mosh;
using EasyTerminal.Core.Terminal;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace EasyTerminal.Tests;

public class MoshTests
{
    [Fact]
    public void OcbMatchesRfc7253Vector()
    {
        var key = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");
        var nonce = Convert.FromHexString("BBAA99887766554433221100");
        using var ocb = new AesOcb(key);
        Assert.Equal("785407BFFFC8AD9EDCC5520AC9111EE6", Convert.ToHexString(ocb.Encrypt(nonce, ReadOnlySpan<byte>.Empty)));
    }

    [Fact]
    public void OcbInteroperatesWithBouncyCastleAtEverySize()
    {
        var key = RandomNumberGenerator.GetBytes(16);
        using var ocb = new AesOcb(key);
        var random = new Random(7);
        for (int size = 0; size < 200; size++)
        {
            var nonce = new byte[12];
            var plain = new byte[size];
            random.NextBytes(nonce);
            random.NextBytes(plain);

            var reference = new OcbBlockCipher(new AesEngine(), new AesEngine());
            reference.Init(true, new AeadParameters(new KeyParameter(key), 128, nonce));
            var expected = new byte[reference.GetOutputSize(plain.Length)];
            int written = reference.ProcessBytes(plain, 0, plain.Length, expected, 0);
            reference.DoFinal(expected, written);

            var actual = ocb.Encrypt(nonce, plain);
            Assert.Equal(expected, actual);
            Assert.Equal(plain, ocb.Decrypt(nonce, actual));
        }
    }

    [Fact]
    public void OcbRejectsTampering()
    {
        using var ocb = new AesOcb(new byte[16]);
        var nonce = new byte[12];
        var sealedBytes = ocb.Encrypt(nonce, Encoding.ASCII.GetBytes("secret"));
        sealedBytes[0] ^= 1;
        Assert.Null(ocb.Decrypt(nonce, sealedBytes));
    }

    [Fact]
    public void ParsesConnectLineAmongNoise()
    {
        var parsed = MoshBootstrap.ParseConnectLine("\r\nMOSH CONNECT 60001 AbCdEfGhIjKlMnOpQrStUv\r\n\nmosh-server (mosh 1.4.0)\n");
        Assert.Equal((60001, "AbCdEfGhIjKlMnOpQrStUv"), parsed);
        Assert.Null(MoshBootstrap.ParseConnectLine("sh: mosh-server: not found"));
    }

    [Fact]
    public void WireFormatMatchesMoshFieldLayout()
    {
        // Instruction{version=2, old=1, new=2, ack=3, throwaway=1} with Mosh's tag numbers.
        var bytes = new MoshInstruction { OldNum = 1, NewNum = 2, AckNum = 3, ThrowawayNum = 1 }.Serialize();
        Assert.Equal(new byte[] { 0x08, 2, 0x10, 1, 0x18, 2, 0x20, 3, 0x28, 1 }, bytes);

        // UserMessage{instruction{[keystroke]{keys="a"}}}: field 1 > extension 2 > field 4.
        var user = MoshMessages.SerializeUser(new[] { new KeystrokeEvent(new[] { (byte)'a' }) });
        Assert.Equal(new byte[] { 0x0A, 5, 0x12, 3, 0x22, 1, (byte)'a' }, user);

        // Resize: extension 3 with width=5, height=6 field numbers.
        var resize = MoshMessages.SerializeUser(new[] { new UserResizeEvent(80, 24) });
        Assert.Equal(new byte[] { 0x0A, 6, 0x1A, 4, 0x28, 80, 0x30, 24 }, resize);
    }

    [Fact]
    public void FragmentHeaderSetsFinalBit()
    {
        var serialized = MoshPacket.SerializeFragment(new MoshFragment(9, 3, true, new byte[] { 0xAA }));
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0, 0, 9, 0x80, 3, 0xAA }, serialized);
    }

    /// <summary>A minimal SSP server, enough to run the client through state sync, loss and out-of-order diffs.</summary>
    private sealed class LoopbackServer : IDisposable
    {
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly AesOcb _cipher;
        private readonly CancellationTokenSource _stop = new();
        private readonly object _lock = new();
        private ulong _sequence;
        private ulong _fragmentId;
        private IPEndPoint? _client;
        private ulong _clientState;
        private ulong _lastAck;
        private readonly Dictionary<ulong, int> _clientEvents = new() { [0] = 0 };

        public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        public string Key { get; }
        public List<UserEvent> Received { get; } = new();
        public TaskCompletionSource FirstPacket { get; } = new();
        public ulong LastAck { get { lock (_lock) return _lastAck; } }
        public bool DropFromClient { get; set; }

        public LoopbackServer()
        {
            var key = RandomNumberGenerator.GetBytes(16);
            Key = Convert.ToBase64String(key).TrimEnd('=');
            _cipher = new AesOcb(key);
            _ = Task.Run(Loop);
        }

        private async Task Loop()
        {
            while (!_stop.IsCancellationRequested)
            {
                UdpReceiveResult packet;
                try
                {
                    packet = await _udp.ReceiveAsync(_stop.Token);
                }
                catch (Exception)
                {
                    return;
                }
                if (DropFromClient) continue;
                if (!MoshPacket.TryOpen(_cipher, packet.Buffer, false, out _, out _, out var payload)) continue;
                var fragment = MoshPacket.ParseFragment(payload);
                var instruction = MoshInstruction.Parse(MoshPacket.Decompress(fragment.Contents));
                lock (_lock)
                {
                    _client = packet.RemoteEndPoint;
                    _lastAck = Math.Max(_lastAck, instruction.AckNum);
                    if (_clientEvents.TryGetValue(instruction.OldNum, out int baseCount) && !_clientEvents.ContainsKey(instruction.NewNum))
                    {
                        var events = MoshMessages.ParseUser(instruction.Diff);
                        // The diff restates everything since the base state; keep only what is new.
                        var fresh = events.Skip(Received.Count - baseCount).ToList();
                        Received.AddRange(fresh);
                        _clientEvents[instruction.NewNum] = Received.Count;
                        _clientState = Math.Max(_clientState, instruction.NewNum);
                    }
                }
                FirstPacket.TrySetResult();
            }
        }

        public void SendFrame(ulong oldNum, ulong newNum, string text, ulong throwaway = 0)
        {
            ulong ack;
            IPEndPoint client;
            lock (_lock)
            {
                ack = _clientState;
                client = _client!;
            }
            var instruction = new MoshInstruction
            {
                OldNum = oldNum,
                NewNum = newNum,
                AckNum = ack,
                ThrowawayNum = throwaway,
                Diff = text.Length == 0 ? Array.Empty<byte>() : MoshMessages.SerializeHost(new[] { new HostBytesEvent(Encoding.UTF8.GetBytes(text)) }),
            };
            var compressed = MoshPacket.Compress(instruction.Serialize());
            foreach (var fragment in MoshPacket.Fragment(_fragmentId++, compressed))
            {
                var datagram = MoshPacket.Seal(_cipher, _sequence++, true, 1, 0xFFFF, MoshPacket.SerializeFragment(fragment));
                _udp.Send(datagram, datagram.Length, client);
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _udp.Dispose();
            _cipher.Dispose();
        }
    }

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("Condition was not met");
            await Task.Delay(10);
        }
    }

    private static string RowText(TerminalBuffer t, int row)
    {
        lock (t.SyncRoot)
        {
            var sb = new StringBuilder();
            foreach (var cell in t.ViewportLine(row).Cells) ClusterTable.Append(sb, cell.Codepoint);
            return sb.ToString().TrimEnd();
        }
    }

    [Fact]
    public async Task SyncsStateInBothDirectionsAndHandlesOutOfOrderDiffs()
    {
        using var server = new LoopbackServer();
        using var transport = new MoshTransport(_ => Task.FromResult(new MoshEndpoint("127.0.0.1", server.Port, server.Key)));
        var display = new TerminalBuffer(40, 10, 0);
        transport.FrameReady += frame => { lock (frame.SyncRoot) display.CopyFrom(frame); };
        transport.Start(40, 10);

        await server.FirstPacket.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // The initial resize must arrive first; mosh-server waits for it before running the shell.
        await WaitFor(() => { lock (server.Received) return server.Received.Count > 0; });
        Assert.Equal(new UserResizeEvent(40, 10), server.Received[0]);

        transport.Write("ls\r"u8);
        await WaitFor(() => server.Received.OfType<KeystrokeEvent>().Any());
        Assert.Equal("ls\r", Encoding.ASCII.GetString(server.Received.OfType<KeystrokeEvent>().First().Keys));

        server.SendFrame(0, 1, "hello");
        await WaitFor(() => RowText(display, 0) == "hello");
        await WaitFor(() => server.LastAck >= 1);

        // State 3 is built from state 1 even though state 2 arrives later; 2 must then be ignored as stale.
        server.SendFrame(1, 3, "\r\nthree");
        await WaitFor(() => RowText(display, 1) == "three");
        server.SendFrame(1, 2, "\r\ntwo");
        await Task.Delay(100);
        Assert.Equal("three", RowText(display, 1));

        // A retransmitted state must not be applied twice.
        server.SendFrame(1, 3, "\r\nthree");
        await Task.Delay(100);
        Assert.Equal("", RowText(display, 2));
    }

    [Fact]
    public async Task RetransmitsKeystrokesUntilAcknowledged()
    {
        using var server = new LoopbackServer();
        using var transport = new MoshTransport(_ => Task.FromResult(new MoshEndpoint("127.0.0.1", server.Port, server.Key)));
        transport.Start(20, 5);
        await server.FirstPacket.Task.WaitAsync(TimeSpan.FromSeconds(5));

        server.DropFromClient = true;
        transport.Write("x"u8);
        await Task.Delay(400);
        server.DropFromClient = false;
        await WaitFor(() => server.Received.OfType<KeystrokeEvent>().Any());
    }

    [Fact]
    public async Task IgnoresForgedPackets()
    {
        using var server = new LoopbackServer();
        using var transport = new MoshTransport(_ => Task.FromResult(new MoshEndpoint("127.0.0.1", server.Port, server.Key)));
        int frames = 0;
        transport.FrameReady += _ => Interlocked.Increment(ref frames);
        transport.Start(20, 5);
        await server.FirstPacket.Task.WaitAsync(TimeSpan.FromSeconds(5));
        server.SendFrame(0, 1, "ok");
        await WaitFor(() => Volatile.Read(ref frames) == 1);
    }

    [Fact]
    public async Task ReportsBootstrapFailureInTerminal()
    {
        using var transport = new MoshTransport(_ => throw new IOException("mosh-server not found"));
        var text = new StringBuilder();
        var exited = new TaskCompletionSource<int?>();
        transport.DataReceived += d => { lock (text) text.Append(Encoding.UTF8.GetString(d.Span)); };
        transport.Exited += code => exited.TrySetResult(code);
        transport.Start(20, 5);
        Assert.Equal(1, await exited.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("mosh-server not found", text.ToString());
    }
}
