namespace AirplayFox.Protocol.Crypto;

/// <summary>
/// HAP wire-level encryption: 1024-byte plaintext frames, each prefixed with
/// a 2-byte little-endian length and authenticated with that length as AAD.
/// Transparent pass-through until Enable() is called.
/// </summary>
public sealed class HapSession
{
    private const int FrameLength = 1024;
    private const int AuthTagLength = 16;

    private Chacha20Cipher? _cipher;
    private byte[] _pending = Array.Empty<byte>();

    public void Enable(byte[] outKey, byte[] inKey) => _cipher = new Chacha20Cipher(outKey, inKey);

    public byte[] Encrypt(byte[] data)
    {
        if (_cipher is null) return data;

        using var output = new MemoryStream();
        var offset = 0;
        while (offset < data.Length)
        {
            var chunkLen = Math.Min(FrameLength, data.Length - offset);
            var frame = new byte[chunkLen];
            Buffer.BlockCopy(data, offset, frame, 0, chunkLen);
            offset += chunkLen;

            var length = new byte[2];
            length[0] = (byte)(chunkLen & 0xFF);
            length[1] = (byte)((chunkLen >> 8) & 0xFF);

            var encrypted = _cipher.Encrypt(frame, aad: length);
            output.Write(length);
            output.Write(encrypted);
        }
        return output.ToArray();
    }

    public byte[] Decrypt(byte[] data)
    {
        if (_cipher is null) return data;

        var buf = new byte[_pending.Length + data.Length];
        Buffer.BlockCopy(_pending, 0, buf, 0, _pending.Length);
        Buffer.BlockCopy(data, 0, buf, _pending.Length, data.Length);

        using var output = new MemoryStream();
        var pos = 0;
        while (pos < buf.Length)
        {
            if (buf.Length - pos < 2) break;
            var plainLen = buf[pos] | (buf[pos + 1] << 8);
            var blockLen = plainLen + AuthTagLength;
            if (buf.Length - pos < 2 + blockLen) break;

            var length = new byte[] { buf[pos], buf[pos + 1] };
            var block = new byte[blockLen];
            Buffer.BlockCopy(buf, pos + 2, block, 0, blockLen);

            output.Write(_cipher.Decrypt(block, aad: length));
            pos += 2 + blockLen;
        }

        _pending = buf[pos..];
        return output.ToArray();
    }
}
