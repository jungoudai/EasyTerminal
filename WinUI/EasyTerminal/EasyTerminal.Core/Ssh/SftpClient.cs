using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;

namespace EasyTerminal.Core.Ssh;

public sealed record SftpEntry(string Name, string FullPath, bool IsDirectory, bool IsSymlink, long Size, DateTimeOffset? Modified, uint Permissions)
{
    public string PermissionText
    {
        get
        {
            var sb = new StringBuilder(IsDirectory ? "d" : IsSymlink ? "l" : "-");
            for (int shift = 6; shift >= 0; shift -= 3)
            {
                uint bits = Permissions >> shift;
                sb.Append((bits & 4) != 0 ? 'r' : '-').Append((bits & 2) != 0 ? 'w' : '-').Append((bits & 1) != 0 ? 'x' : '-');
            }
            return sb.ToString();
        }
    }
}

public sealed class SftpException(int code, string message) : IOException(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// SFTP v3 client over any byte stream, normally the stdio of <c>ssh -s sftp</c> so it inherits every
/// authentication and routing feature of OpenSSH. Requests are pipelined for throughput.
/// </summary>
public sealed class SftpClient : IDisposable
{
    private const byte FxpInit = 1, FxpVersion = 2, FxpOpen = 3, FxpClose = 4, FxpRead = 5, FxpWrite = 6, FxpLstat = 7, FxpSetstat = 9,
        FxpOpendir = 11, FxpReaddir = 12, FxpRemove = 13, FxpMkdir = 14, FxpRmdir = 15, FxpRealpath = 16, FxpStat = 17, FxpRename = 18,
        FxpStatus = 101, FxpHandle = 102, FxpData = 103, FxpName = 104, FxpAttrs = 105;

    private const uint OpenRead = 1, OpenWrite = 2, OpenCreate = 8, OpenTruncate = 16;
    private const int ChunkSize = 32 * 1024;
    private const int Pipeline = 16;

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<Packet>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly TaskCompletionSource<uint> _version = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private uint _nextId;
    private Exception? _failure;
    private readonly Action? _onDispose;

    private readonly record struct Packet(byte Type, byte[] Body);

    public SftpClient(Stream input, Stream output, Action? onDispose = null)
    {
        _input = input;
        _output = output;
        _onDispose = onDispose;
    }

    public async Task InitializeAsync(CancellationToken cancellation = default)
    {
        _ = Task.Run(ReadLoop);
        var init = new PacketWriter(FxpInit);
        init.Write((uint)3);
        await SendAsync(init);
        await _version.Task.WaitAsync(cancellation);
    }

    private async Task ReadLoop()
    {
        var header = new byte[4];
        try
        {
            while (true)
            {
                await _input.ReadExactlyAsync(header);
                int length = BinaryPrimitives.ReadInt32BigEndian(header);
                if (length is < 1 or > 16 * 1024 * 1024) throw new IOException("SFTP 服务器返回了无效的数据包");
                var body = new byte[length];
                await _input.ReadExactlyAsync(body);

                byte type = body[0];
                if (type == FxpVersion)
                {
                    _version.TrySetResult(BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(1)));
                    continue;
                }
                uint id = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(1));
                if (_pending.TryRemove(id, out var source))
                    source.TrySetResult(new Packet(type, body));
            }
        }
        catch (Exception ex)
        {
            _failure = ex is EndOfStreamException ? new IOException("SFTP 连接已断开") : ex;
            _version.TrySetException(_failure);
            foreach (var key in _pending.Keys)
                if (_pending.TryRemove(key, out var source)) source.TrySetException(_failure);
        }
    }

    private async Task SendAsync(PacketWriter packet)
    {
        await _writeLock.WaitAsync();
        try
        {
            await _output.WriteAsync(packet.Finish());
            await _output.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<Packet> RequestAsync(byte type, Action<PacketWriter> build)
    {
        if (_failure != null) throw _failure;
        uint id = Interlocked.Increment(ref _nextId);
        var source = new TaskCompletionSource<Packet>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = source;
        var packet = new PacketWriter(type);
        packet.Write(id);
        build(packet);
        await SendAsync(packet);
        return await source.Task;
    }

    private static void ThrowIfError(Packet packet)
    {
        if (packet.Type != FxpStatus) throw new IOException($"SFTP 响应类型异常 ({packet.Type})");
        var reader = new PacketReader(packet.Body, 5);
        uint code = reader.ReadUInt32();
        if (code != 0) throw new SftpException((int)code, StatusMessage(code, reader));
    }

    private static string StatusMessage(uint code, PacketReader reader)
    {
        string text = code switch
        {
            2 => "文件不存在",
            3 => "没有权限",
            4 => "操作失败",
            8 => "服务器不支持此操作",
            _ => "SFTP 错误",
        };
        try
        {
            string detail = reader.ReadString();
            if (detail.Length > 0) text += ": " + detail;
        }
        catch (IOException)
        {
        }
        return text;
    }

    private static byte[] ExpectHandle(Packet packet)
    {
        if (packet.Type == FxpHandle) return new PacketReader(packet.Body, 5).ReadBytes();
        ThrowIfError(packet);
        throw new IOException("SFTP 没有返回句柄");
    }

    public async Task<string> RealPathAsync(string path)
    {
        var reply = await RequestAsync(FxpRealpath, p => p.Write(path));
        if (reply.Type != FxpName)
        {
            ThrowIfError(reply);
            throw new IOException("SFTP 没有返回路径");
        }
        var reader = new PacketReader(reply.Body, 5);
        reader.ReadUInt32();
        return reader.ReadString();
    }

    public async Task<IReadOnlyList<SftpEntry>> ListAsync(string path)
    {
        var handle = ExpectHandle(await RequestAsync(FxpOpendir, p => p.Write(path)));
        var entries = new List<SftpEntry>();
        try
        {
            while (true)
            {
                var reply = await RequestAsync(FxpReaddir, p => p.WriteBytes(handle));
                if (reply.Type == FxpStatus)
                {
                    var status = new PacketReader(reply.Body, 5);
                    uint code = status.ReadUInt32();
                    if (code == 1) break;
                    throw new SftpException((int)code, StatusMessage(code, status));
                }
                if (reply.Type != FxpName) throw new IOException("SFTP 目录响应异常");
                var reader = new PacketReader(reply.Body, 5);
                uint count = reader.ReadUInt32();
                for (uint i = 0; i < count; i++)
                {
                    string name = reader.ReadString();
                    reader.ReadString();
                    var attrs = reader.ReadAttributes();
                    if (name is "." or "..") continue;
                    string full = path.EndsWith('/') ? path + name : path + "/" + name;
                    entries.Add(attrs.ToEntry(name, full));
                }
            }
        }
        finally
        {
            await CloseHandleQuietly(handle);
        }

        // A symlink to a directory should browse like one, so resolve those targets.
        for (int i = 0; i < entries.Count; i++)
        {
            if (!entries[i].IsSymlink) continue;
            try
            {
                var target = await StatAsync(entries[i].FullPath);
                entries[i] = entries[i] with { IsDirectory = target.IsDirectory };
            }
            catch (IOException)
            {
                // Dangling links stay listed as plain links.
            }
        }
        return entries;
    }

    public async Task<SftpEntry> StatAsync(string path)
    {
        var reply = await RequestAsync(FxpStat, p => p.Write(path));
        if (reply.Type != FxpAttrs)
        {
            ThrowIfError(reply);
            throw new IOException("SFTP 没有返回属性");
        }
        return new PacketReader(reply.Body, 5).ReadAttributes().ToEntry(Path.GetFileName(path), path);
    }

    public async Task MakeDirectoryAsync(string path) =>
        ThrowIfError(await RequestAsync(FxpMkdir, p => { p.Write(path); p.Write((uint)0); }));

    public async Task RemoveFileAsync(string path) => ThrowIfError(await RequestAsync(FxpRemove, p => p.Write(path)));

    public async Task RemoveDirectoryAsync(string path) => ThrowIfError(await RequestAsync(FxpRmdir, p => p.Write(path)));

    public async Task RenameAsync(string from, string to) =>
        ThrowIfError(await RequestAsync(FxpRename, p => { p.Write(from); p.Write(to); }));

    public async Task SetPermissionsAsync(string path, uint permissions) =>
        ThrowIfError(await RequestAsync(FxpSetstat, p => { p.Write(path); p.Write((uint)4); p.Write(permissions); }));

    public async Task RemoveRecursiveAsync(SftpEntry entry)
    {
        if (!entry.IsDirectory || entry.IsSymlink)
        {
            await RemoveFileAsync(entry.FullPath);
            return;
        }
        foreach (var child in await ListAsync(entry.FullPath)) await RemoveRecursiveAsync(child);
        await RemoveDirectoryAsync(entry.FullPath);
    }

    public async Task DownloadAsync(string remotePath, Stream destination, long expectedSize, IProgress<long>? progress = null, CancellationToken cancellation = default)
    {
        var handle = ExpectHandle(await RequestAsync(FxpOpen, p => { p.Write(remotePath); p.Write(OpenRead); p.Write((uint)0); }));
        try
        {
            long offset = 0;
            long written = 0;
            var inFlight = new Queue<(long Offset, Task<Packet> Reply)>();
            bool eof = false;
            while (!eof || inFlight.Count > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                while (!eof && inFlight.Count < Pipeline)
                {
                    long at = offset;
                    inFlight.Enqueue((at, RequestAsync(FxpRead, p => { p.WriteBytes(handle); p.Write((ulong)at); p.Write((uint)ChunkSize); })));
                    offset += ChunkSize;
                    // Past the known size the next read will hit EOF, so stop speculating.
                    if (expectedSize >= 0 && offset >= expectedSize + ChunkSize) break;
                }
                if (inFlight.Count == 0) break;

                var (position, task) = inFlight.Dequeue();
                var reply = await task;
                if (reply.Type == FxpStatus)
                {
                    var status = new PacketReader(reply.Body, 5);
                    uint code = status.ReadUInt32();
                    if (code != 1) throw new SftpException((int)code, StatusMessage(code, status));
                    eof = true;
                    continue;
                }
                if (eof) continue;
                var data = new PacketReader(reply.Body, 5).ReadSegment();
                if (position != written) throw new IOException("SFTP 读取结果不连续");
                await destination.WriteAsync(data, cancellation);
                written += data.Count;
                progress?.Report(written);

                // A short read means the server may return less than asked; re-request from the true offset.
                if (data.Count < ChunkSize)
                {
                    while (inFlight.Count > 0) await inFlight.Dequeue().Reply;
                    offset = written;
                }
            }
        }
        finally
        {
            await CloseHandleQuietly(handle);
        }
    }

    public async Task UploadAsync(Stream source, string remotePath, IProgress<long>? progress = null, CancellationToken cancellation = default)
    {
        var handle = ExpectHandle(await RequestAsync(FxpOpen, p => { p.Write(remotePath); p.Write(OpenWrite | OpenCreate | OpenTruncate); p.Write((uint)0); }));
        try
        {
            long offset = 0;
            var inFlight = new Queue<Task<Packet>>();
            var buffer = new byte[ChunkSize];
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                int read = await source.ReadAtLeastAsync(buffer, buffer.Length, false, cancellation);
                if (read == 0) break;
                var chunk = buffer.AsSpan(0, read).ToArray();
                long at = offset;
                inFlight.Enqueue(RequestAsync(FxpWrite, p => { p.WriteBytes(handle); p.Write((ulong)at); p.WriteBytes(chunk); }));
                offset += read;
                while (inFlight.Count >= Pipeline) ThrowIfError(await inFlight.Dequeue());
                progress?.Report(offset);
            }
            while (inFlight.Count > 0) ThrowIfError(await inFlight.Dequeue());
        }
        finally
        {
            await CloseHandleQuietly(handle);
        }
    }

    private async Task CloseHandleQuietly(byte[] handle)
    {
        try
        {
            await RequestAsync(FxpClose, p => p.WriteBytes(handle));
        }
        catch (IOException)
        {
        }
    }

    public void Dispose()
    {
        try
        {
            _output.Dispose();
        }
        catch (IOException)
        {
        }
        _onDispose?.Invoke();
    }

    private sealed class PacketWriter
    {
        private readonly MemoryStream _stream = new();

        public PacketWriter(byte type)
        {
            _stream.Write(stackalloc byte[4]);
            _stream.WriteByte(type);
        }

        public void Write(uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            _stream.Write(bytes);
        }

        public void Write(ulong value)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
            _stream.Write(bytes);
        }

        public void Write(string value) => WriteBytes(Encoding.UTF8.GetBytes(value));

        public void WriteBytes(byte[] value)
        {
            Write((uint)value.Length);
            _stream.Write(value);
        }

        public byte[] Finish()
        {
            var data = _stream.ToArray();
            BinaryPrimitives.WriteInt32BigEndian(data, data.Length - 4);
            return data;
        }
    }

    private readonly record struct Attributes(long Size, uint Permissions, uint ModifiedTime, bool HasTime)
    {
        public SftpEntry ToEntry(string name, string fullPath)
        {
            uint type = Permissions & 0xF000;
            return new SftpEntry(name, fullPath, type == 0x4000, type == 0xA000, Size,
                HasTime ? DateTimeOffset.FromUnixTimeSeconds(ModifiedTime) : null, Permissions & 0xFFF);
        }
    }

    private sealed class PacketReader(byte[] data, int position)
    {
        private int _position = position;

        public uint ReadUInt32()
        {
            if (_position + 4 > data.Length) throw new IOException("SFTP 数据包被截断");
            uint value = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(_position));
            _position += 4;
            return value;
        }

        public ulong ReadUInt64()
        {
            if (_position + 8 > data.Length) throw new IOException("SFTP 数据包被截断");
            ulong value = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(_position));
            _position += 8;
            return value;
        }

        public ArraySegment<byte> ReadSegment()
        {
            int length = (int)ReadUInt32();
            if (length < 0 || _position + length > data.Length) throw new IOException("SFTP 数据包被截断");
            var segment = new ArraySegment<byte>(data, _position, length);
            _position += length;
            return segment;
        }

        public byte[] ReadBytes() => ReadSegment().ToArray();

        public string ReadString()
        {
            var segment = ReadSegment();
            return Encoding.UTF8.GetString(segment.Array!, segment.Offset, segment.Count);
        }

        public Attributes ReadAttributes()
        {
            uint flags = ReadUInt32();
            long size = 0;
            uint permissions = 0, modified = 0;
            bool hasTime = false;
            if ((flags & 1) != 0) size = (long)ReadUInt64();
            if ((flags & 2) != 0)
            {
                ReadUInt32();
                ReadUInt32();
            }
            if ((flags & 4) != 0) permissions = ReadUInt32();
            if ((flags & 8) != 0)
            {
                ReadUInt32();
                modified = ReadUInt32();
                hasTime = true;
            }
            if ((flags & 0x80000000) != 0)
            {
                uint count = ReadUInt32();
                for (uint i = 0; i < count; i++)
                {
                    ReadSegment();
                    ReadSegment();
                }
            }
            return new Attributes(size, permissions, modified, hasTime);
        }
    }
}
