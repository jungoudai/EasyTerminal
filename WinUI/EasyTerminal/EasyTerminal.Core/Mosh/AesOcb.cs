using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace EasyTerminal.Core.Mosh;

/// <summary>AES-128-OCB3 (RFC 7253) with a 128-bit tag and no associated data, as used by Mosh's SSP.</summary>
public sealed class AesOcb : IDisposable
{
    public const int TagSize = 16;
    public const int NonceSize = 12;
    private const int BlockSize = 16;

    private readonly Aes _aes;
    private readonly byte[] _lStar = new byte[BlockSize];
    private readonly byte[] _lDollar = new byte[BlockSize];
    private readonly byte[][] _l = new byte[32][];

    public AesOcb(ReadOnlySpan<byte> key)
    {
        _aes = Aes.Create();
        _aes.Key = key.ToArray();
        _aes.EncryptEcb(new byte[BlockSize], _lStar, PaddingMode.None);
        Double(_lStar, _lDollar);
        _l[0] = new byte[BlockSize];
        Double(_lDollar, _l[0]);
        for (int i = 1; i < _l.Length; i++)
        {
            _l[i] = new byte[BlockSize];
            Double(_l[i - 1], _l[i]);
        }
    }

    private static void Double(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int carry = input[0] >> 7;
        for (int i = 0; i < BlockSize - 1; i++) output[i] = (byte)((input[i] << 1) | (input[i + 1] >> 7));
        output[BlockSize - 1] = (byte)((input[BlockSize - 1] << 1) ^ (carry * 0x87));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Xor(Span<byte> target, ReadOnlySpan<byte> other)
    {
        for (int i = 0; i < BlockSize; i++) target[i] ^= other[i];
    }

    private void InitialOffset(ReadOnlySpan<byte> nonce, Span<byte> offset)
    {
        Span<byte> full = stackalloc byte[BlockSize];
        // num2str(TAGLEN mod 128, 7) is zero for a 128-bit tag, then zeros, a 1 bit and the nonce.
        full[BlockSize - nonce.Length - 1] = 1;
        nonce.CopyTo(full[(BlockSize - nonce.Length)..]);
        int bottom = full[BlockSize - 1] & 0x3F;
        full[BlockSize - 1] &= 0xC0;

        Span<byte> ktop = stackalloc byte[BlockSize];
        _aes.EncryptEcb(full, ktop, PaddingMode.None);
        Span<byte> stretch = stackalloc byte[24];
        ktop.CopyTo(stretch);
        for (int i = 0; i < 8; i++) stretch[BlockSize + i] = (byte)(ktop[i] ^ ktop[i + 1]);

        int byteShift = bottom / 8;
        int bitShift = bottom % 8;
        for (int i = 0; i < BlockSize; i++)
        {
            int high = stretch[i + byteShift] << bitShift;
            int low = bitShift == 0 ? 0 : stretch[i + byteShift + 1] >> (8 - bitShift);
            offset[i] = (byte)(high | low);
        }
    }

    public byte[] Encrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext)
    {
        var output = new byte[plaintext.Length + TagSize];
        Span<byte> offset = stackalloc byte[BlockSize];
        Span<byte> checksum = stackalloc byte[BlockSize];
        Span<byte> block = stackalloc byte[BlockSize];
        InitialOffset(nonce, offset);

        int blocks = plaintext.Length / BlockSize;
        for (int i = 1; i <= blocks; i++)
        {
            Xor(offset, _l[BitOperations.TrailingZeroCount(i)]);
            var input = plaintext.Slice((i - 1) * BlockSize, BlockSize);
            Xor(checksum, input);
            input.CopyTo(block);
            Xor(block, offset);
            var target = output.AsSpan((i - 1) * BlockSize, BlockSize);
            _aes.EncryptEcb(block, target, PaddingMode.None);
            Xor(target, offset);
        }

        int remaining = plaintext.Length % BlockSize;
        if (remaining > 0)
        {
            Xor(offset, _lStar);
            Span<byte> pad = stackalloc byte[BlockSize];
            _aes.EncryptEcb(offset, pad, PaddingMode.None);
            var tail = plaintext[(blocks * BlockSize)..];
            for (int i = 0; i < remaining; i++)
            {
                output[blocks * BlockSize + i] = (byte)(tail[i] ^ pad[i]);
                checksum[i] ^= tail[i];
            }
            checksum[remaining] ^= 0x80;
        }

        Span<byte> tag = stackalloc byte[BlockSize];
        checksum.CopyTo(block);
        Xor(block, offset);
        Xor(block, _lDollar);
        _aes.EncryptEcb(block, tag, PaddingMode.None);
        tag.CopyTo(output.AsSpan(plaintext.Length));
        return output;
    }

    /// <summary>Returns the plaintext, or null when authentication fails.</summary>
    public byte[]? Decrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext)
    {
        if (ciphertext.Length < TagSize) return null;
        int length = ciphertext.Length - TagSize;
        var output = new byte[length];
        Span<byte> offset = stackalloc byte[BlockSize];
        Span<byte> checksum = stackalloc byte[BlockSize];
        Span<byte> block = stackalloc byte[BlockSize];
        InitialOffset(nonce, offset);

        int blocks = length / BlockSize;
        for (int i = 1; i <= blocks; i++)
        {
            Xor(offset, _l[BitOperations.TrailingZeroCount(i)]);
            ciphertext.Slice((i - 1) * BlockSize, BlockSize).CopyTo(block);
            Xor(block, offset);
            var target = output.AsSpan((i - 1) * BlockSize, BlockSize);
            _aes.DecryptEcb(block, target, PaddingMode.None);
            Xor(target, offset);
            Xor(checksum, target);
        }

        int remaining = length % BlockSize;
        if (remaining > 0)
        {
            Xor(offset, _lStar);
            Span<byte> pad = stackalloc byte[BlockSize];
            _aes.EncryptEcb(offset, pad, PaddingMode.None);
            var tail = ciphertext.Slice(blocks * BlockSize, remaining);
            for (int i = 0; i < remaining; i++)
            {
                byte plain = (byte)(tail[i] ^ pad[i]);
                output[blocks * BlockSize + i] = plain;
                checksum[i] ^= plain;
            }
            checksum[remaining] ^= 0x80;
        }

        Span<byte> tag = stackalloc byte[BlockSize];
        checksum.CopyTo(block);
        Xor(block, offset);
        Xor(block, _lDollar);
        _aes.EncryptEcb(block, tag, PaddingMode.None);
        return CryptographicOperations.FixedTimeEquals(tag, ciphertext[length..]) ? output : null;
    }

    public void Dispose() => _aes.Dispose();
}
