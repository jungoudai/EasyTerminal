using System.Buffers.Binary;
using System.IO.Compression;

namespace EasyTerminal.Core.Mosh;

/// <summary>Just enough protobuf for Mosh's three messages: varints and length-delimited fields.</summary>
internal ref struct ProtoReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _position = 0;

    public bool End => _position >= _data.Length;

    public ulong ReadVarint()
    {
        ulong value = 0;
        int shift = 0;
        while (true)
        {
            if (_position >= _data.Length || shift > 63) throw new InvalidDataException("Bad varint");
            byte b = _data[_position++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
            shift += 7;
        }
    }

    public (int Field, int WireType) ReadTag()
    {
        ulong tag = ReadVarint();
        return ((int)(tag >> 3), (int)(tag & 7));
    }

    public ReadOnlySpan<byte> ReadBytes()
    {
        int length = checked((int)ReadVarint());
        if (length < 0 || _position + length > _data.Length) throw new InvalidDataException("Bad length");
        var slice = _data.Slice(_position, length);
        _position += length;
        return slice;
    }

    public void Skip(int wireType)
    {
        switch (wireType)
        {
            case 0: ReadVarint(); break;
            case 1: _position += 8; break;
            case 2: ReadBytes(); break;
            case 5: _position += 4; break;
            default: throw new InvalidDataException("Unknown wire type");
        }
    }
}

internal sealed class ProtoWriter
{
    private readonly MemoryStream _stream = new();

    public void WriteVarint(int field, ulong value)
    {
        WriteRawVarint((ulong)(field << 3));
        WriteRawVarint(value);
    }

    public void WriteBytes(int field, ReadOnlySpan<byte> value)
    {
        WriteRawVarint((ulong)((field << 3) | 2));
        WriteRawVarint((ulong)value.Length);
        _stream.Write(value);
    }

    private void WriteRawVarint(ulong value)
    {
        while (value >= 0x80)
        {
            _stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        _stream.WriteByte((byte)value);
    }

    public byte[] ToArray() => _stream.ToArray();
}

/// <summary>TransportBuffers.Instruction, the state-sync envelope.</summary>
public sealed class MoshInstruction
{
    public const uint ProtocolVersion = 2;

    public uint Version { get; set; } = ProtocolVersion;
    public ulong OldNum { get; set; }
    public ulong NewNum { get; set; }
    public ulong AckNum { get; set; }
    public ulong ThrowawayNum { get; set; }
    public byte[] Diff { get; set; } = Array.Empty<byte>();

    public byte[] Serialize()
    {
        var writer = new ProtoWriter();
        writer.WriteVarint(1, Version);
        writer.WriteVarint(2, OldNum);
        writer.WriteVarint(3, NewNum);
        writer.WriteVarint(4, AckNum);
        writer.WriteVarint(5, ThrowawayNum);
        if (Diff.Length > 0) writer.WriteBytes(6, Diff);
        return writer.ToArray();
    }

    public static MoshInstruction Parse(ReadOnlySpan<byte> data)
    {
        var instruction = new MoshInstruction();
        var reader = new ProtoReader(data);
        while (!reader.End)
        {
            var (field, wire) = reader.ReadTag();
            switch (field)
            {
                case 1: instruction.Version = (uint)reader.ReadVarint(); break;
                case 2: instruction.OldNum = reader.ReadVarint(); break;
                case 3: instruction.NewNum = reader.ReadVarint(); break;
                case 4: instruction.AckNum = reader.ReadVarint(); break;
                case 5: instruction.ThrowawayNum = reader.ReadVarint(); break;
                case 6: instruction.Diff = reader.ReadBytes().ToArray(); break;
                default: reader.Skip(wire); break;
            }
        }
        return instruction;
    }
}

public abstract record HostEvent;
public sealed record HostBytesEvent(byte[] Data) : HostEvent;
public sealed record HostResizeEvent(int Width, int Height) : HostEvent;

public abstract record UserEvent;
public sealed record KeystrokeEvent(byte[] Keys) : UserEvent;
public sealed record UserResizeEvent(int Width, int Height) : UserEvent;

/// <summary>ClientBuffers.UserMessage and HostBuffers.HostMessage bodies carried in Instruction.diff.</summary>
public static class MoshMessages
{
    private const int InstructionField = 1;
    private const int KeystrokeExtension = 2;
    private const int ResizeExtension = 3;
    private const int HostBytesExtension = 2;
    private const int KeysField = 4;
    private const int HostStringField = 4;
    private const int WidthField = 5;
    private const int HeightField = 6;

    public static byte[] SerializeUser(IEnumerable<UserEvent> events)
    {
        var message = new ProtoWriter();
        foreach (var userEvent in events)
        {
            var body = new ProtoWriter();
            var instruction = new ProtoWriter();
            switch (userEvent)
            {
                case KeystrokeEvent key:
                    body.WriteBytes(KeysField, key.Keys);
                    instruction.WriteBytes(KeystrokeExtension, body.ToArray());
                    break;
                case UserResizeEvent resize:
                    body.WriteVarint(WidthField, (ulong)resize.Width);
                    body.WriteVarint(HeightField, (ulong)resize.Height);
                    instruction.WriteBytes(ResizeExtension, body.ToArray());
                    break;
            }
            message.WriteBytes(InstructionField, instruction.ToArray());
        }
        return message.ToArray();
    }

    public static IReadOnlyList<HostEvent> ParseHost(ReadOnlySpan<byte> data)
    {
        var events = new List<HostEvent>();
        var message = new ProtoReader(data);
        while (!message.End)
        {
            var (field, wire) = message.ReadTag();
            if (field != InstructionField || wire != 2)
            {
                message.Skip(wire);
                continue;
            }
            var instruction = new ProtoReader(message.ReadBytes());
            while (!instruction.End)
            {
                var (extension, extensionWire) = instruction.ReadTag();
                if (extension == HostBytesExtension && extensionWire == 2)
                {
                    var body = new ProtoReader(instruction.ReadBytes());
                    while (!body.End)
                    {
                        var (f, w) = body.ReadTag();
                        if (f == HostStringField && w == 2) events.Add(new HostBytesEvent(body.ReadBytes().ToArray()));
                        else body.Skip(w);
                    }
                }
                else if (extension == ResizeExtension && extensionWire == 2)
                {
                    var body = new ProtoReader(instruction.ReadBytes());
                    int width = 0, height = 0;
                    while (!body.End)
                    {
                        var (f, w) = body.ReadTag();
                        if (f == WidthField) width = (int)body.ReadVarint();
                        else if (f == HeightField) height = (int)body.ReadVarint();
                        else body.Skip(w);
                    }
                    events.Add(new HostResizeEvent(width, height));
                }
                else
                {
                    instruction.Skip(extensionWire);
                }
            }
        }
        return events;
    }

    // Test support: the mirror images, so a loopback server can speak the same wire format.
    public static byte[] SerializeHost(IEnumerable<HostEvent> events)
    {
        var message = new ProtoWriter();
        foreach (var hostEvent in events)
        {
            var body = new ProtoWriter();
            var instruction = new ProtoWriter();
            switch (hostEvent)
            {
                case HostBytesEvent bytes:
                    body.WriteBytes(HostStringField, bytes.Data);
                    instruction.WriteBytes(HostBytesExtension, body.ToArray());
                    break;
                case HostResizeEvent resize:
                    body.WriteVarint(WidthField, (ulong)resize.Width);
                    body.WriteVarint(HeightField, (ulong)resize.Height);
                    instruction.WriteBytes(ResizeExtension, body.ToArray());
                    break;
            }
            message.WriteBytes(InstructionField, instruction.ToArray());
        }
        return message.ToArray();
    }

    public static IReadOnlyList<UserEvent> ParseUser(ReadOnlySpan<byte> data)
    {
        var events = new List<UserEvent>();
        var message = new ProtoReader(data);
        while (!message.End)
        {
            var (field, wire) = message.ReadTag();
            if (field != InstructionField || wire != 2)
            {
                message.Skip(wire);
                continue;
            }
            var instruction = new ProtoReader(message.ReadBytes());
            while (!instruction.End)
            {
                var (extension, extensionWire) = instruction.ReadTag();
                if (extensionWire != 2)
                {
                    instruction.Skip(extensionWire);
                    continue;
                }
                var body = new ProtoReader(instruction.ReadBytes());
                int width = 0, height = 0;
                while (!body.End)
                {
                    var (f, w) = body.ReadTag();
                    if (extension == KeystrokeExtension && f == KeysField) events.Add(new KeystrokeEvent(body.ReadBytes().ToArray()));
                    else if (extension == ResizeExtension && f == WidthField) width = (int)body.ReadVarint();
                    else if (extension == ResizeExtension && f == HeightField) height = (int)body.ReadVarint();
                    else body.Skip(w);
                }
                if (extension == ResizeExtension) events.Add(new UserResizeEvent(width, height));
            }
        }
        return events;
    }
}

public readonly record struct MoshFragment(ulong Id, ushort Number, bool Final, byte[] Contents);

/// <summary>The outer layers: fragmentation, zlib and the OCB-sealed datagram with its 16-bit timestamps.</summary>
public static class MoshPacket
{
    public const ulong DirectionToClient = 1UL << 63;
    public const int FragmentHeaderSize = 10;
    public const int MaxFragmentPayload = 1200;

    public static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, true)) zlib.Write(data);
        return output.ToArray();
    }

    public static byte[] Decompress(ReadOnlySpan<byte> data)
    {
        using var input = new MemoryStream(data.ToArray());
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    public static List<MoshFragment> Fragment(ulong id, ReadOnlySpan<byte> data)
    {
        var fragments = new List<MoshFragment>();
        int position = 0;
        ushort number = 0;
        do
        {
            int size = Math.Min(MaxFragmentPayload, data.Length - position);
            bool final = position + size >= data.Length;
            fragments.Add(new MoshFragment(id, number++, final, data.Slice(position, size).ToArray()));
            position += size;
        }
        while (position < data.Length);
        return fragments;
    }

    public static byte[] SerializeFragment(MoshFragment fragment)
    {
        var buffer = new byte[FragmentHeaderSize + fragment.Contents.Length];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, fragment.Id);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(8), (ushort)(fragment.Number | (fragment.Final ? 0x8000 : 0)));
        fragment.Contents.CopyTo(buffer, FragmentHeaderSize);
        return buffer;
    }

    public static MoshFragment ParseFragment(ReadOnlySpan<byte> data)
    {
        if (data.Length < FragmentHeaderSize) throw new InvalidDataException("Short fragment");
        ulong id = BinaryPrimitives.ReadUInt64BigEndian(data);
        ushort combined = BinaryPrimitives.ReadUInt16BigEndian(data[8..]);
        return new MoshFragment(id, (ushort)(combined & 0x7FFF), (combined & 0x8000) != 0, data[FragmentHeaderSize..].ToArray());
    }

    public static byte[] Seal(AesOcb cipher, ulong sequence, bool toClient, ushort timestamp, ushort timestampReply, ReadOnlySpan<byte> payload)
    {
        ulong nonceValue = sequence | (toClient ? DirectionToClient : 0);
        Span<byte> nonce = stackalloc byte[AesOcb.NonceSize];
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], nonceValue);

        var plain = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(plain, timestamp);
        BinaryPrimitives.WriteUInt16BigEndian(plain.AsSpan(2), timestampReply);
        payload.CopyTo(plain.AsSpan(4));

        var sealedBytes = cipher.Encrypt(nonce, plain);
        var datagram = new byte[8 + sealedBytes.Length];
        nonce[4..].CopyTo(datagram);
        sealedBytes.CopyTo(datagram, 8);
        return datagram;
    }

    public static bool TryOpen(AesOcb cipher, ReadOnlySpan<byte> datagram, bool expectToClient, out ulong sequence, out ushort timestamp, out byte[] payload)
    {
        sequence = 0;
        timestamp = 0;
        payload = Array.Empty<byte>();
        if (datagram.Length < 8 + AesOcb.TagSize + 4) return false;

        Span<byte> nonce = stackalloc byte[AesOcb.NonceSize];
        datagram[..8].CopyTo(nonce[4..]);
        var plain = cipher.Decrypt(nonce, datagram[8..]);
        if (plain == null || plain.Length < 4) return false;

        ulong value = BinaryPrimitives.ReadUInt64BigEndian(datagram);
        bool toClient = (value & DirectionToClient) != 0;
        if (toClient != expectToClient) return false;
        sequence = value & ~DirectionToClient;
        timestamp = BinaryPrimitives.ReadUInt16BigEndian(plain);
        payload = plain.AsSpan(4).ToArray();
        return true;
    }
}
