using System.Buffers.Binary;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace AirplayFox.Protocol.Crypto;

/// <summary>
/// ChaCha20-Poly1305 wrapper matching pyatv's Chacha20Cipher: a 12-byte nonce
/// built from an 8-byte little-endian counter left-padded with 4 zero bytes
/// (or an explicit literal nonce for the pairing TLV exchanges), tag appended
/// to the ciphertext.
///
/// Uses BouncyCastle instead of System.Security.Cryptography.ChaCha20Poly1305:
/// the BCL version requires OS-level CNG support that's absent on older
/// Windows 10 builds (throws PlatformNotSupportedException there).
/// </summary>
public sealed class Chacha20Cipher
{
    private const int NonceLength = 12;
    private const int MacBits = 128;

    private readonly byte[] _outKey;
    private readonly byte[] _inKey;
    private ulong _outCounter;
    private ulong _inCounter;

    public Chacha20Cipher(byte[] outKey, byte[] inKey)
    {
        _outKey = outKey;
        _inKey = inKey;
    }

    private static byte[] PadNonce(byte[] nonce)
    {
        if (nonce.Length == NonceLength) return nonce;
        var padded = new byte[NonceLength];
        Buffer.BlockCopy(nonce, 0, padded, NonceLength - nonce.Length, nonce.Length);
        return padded;
    }

    private static byte[] CounterNonce(ulong counter)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, counter);
        return bytes;
    }

    public byte[] OutNoncePeek() => PadNonce(CounterNonce(_outCounter));
    public byte[] InNoncePeek() => PadNonce(CounterNonce(_inCounter));

    public byte[] Encrypt(byte[] data, byte[]? nonce = null, byte[]? aad = null)
    {
        byte[] n;
        if (nonce is null)
        {
            n = PadNonce(CounterNonce(_outCounter));
            _outCounter++;
        }
        else
        {
            n = PadNonce(nonce);
        }

        var cipher = new ChaCha20Poly1305();
        cipher.Init(true, new AeadParameters(new KeyParameter(_outKey), MacBits, n, aad));
        var output = new byte[cipher.GetOutputSize(data.Length)];
        var len = cipher.ProcessBytes(data, 0, data.Length, output, 0);
        len += cipher.DoFinal(output, len);
        if (len != output.Length) Array.Resize(ref output, len);
        return output;
    }

    public byte[] Decrypt(byte[] data, byte[]? nonce = null, byte[]? aad = null)
    {
        byte[] n;
        if (nonce is null)
        {
            n = PadNonce(CounterNonce(_inCounter));
            _inCounter++;
        }
        else
        {
            n = PadNonce(nonce);
        }

        var cipher = new ChaCha20Poly1305();
        cipher.Init(false, new AeadParameters(new KeyParameter(_inKey), MacBits, n, aad));
        var output = new byte[cipher.GetOutputSize(data.Length)];
        var len = cipher.ProcessBytes(data, 0, data.Length, output, 0);
        len += cipher.DoFinal(output, len);
        if (len != output.Length) Array.Resize(ref output, len);
        return output;
    }
}
