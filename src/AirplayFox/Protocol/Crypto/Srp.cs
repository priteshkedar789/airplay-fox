using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace AirplayFox.Protocol.Crypto;

/// <summary>
/// SRP-6a client, specialized for HAP transient pair-setup: username
/// "Pair-Setup", PIN "3939", RFC5054 3072-bit group, SHA-512.
/// Mirrors srptools' SRPContext/SRPClientSession math exactly (including its
/// non-standard use of minimal-length, unpadded big-endian integers for
/// public keys and proofs on the wire).
/// </summary>
public sealed class SrpTransientClient
{
    private const string Username = "Pair-Setup";
    private const string Pin = "3939";
    private const string GeneratorHex = "5";

    private static readonly BigInteger N = BEFromHex(
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7EDEE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3DC2007CB8A163BF0598DA48361C55D39A69163FA8FD24CF5F83655D23DCA3AD961C62F356208552BB9ED529077096966D670C354E4ABC9804F1746C08CA18217C32905E462E36CE3BE39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF6955817183995497CEA956AE515D2261898FA051015728E5A8AAAC42DAD33170D04507A33A85521ABDF1CBA64ECFB850458DBEF0A8AEA71575D060C7DB3970F85A6E1E4C7ABF5AE8CDB0933D71E8C94E04A25619DCEE3D2261AD2EE6BF12FFA06D98A0864D87602733EC86A64521F2B18177B200CBBE117577A615D6C770988C0BAD946E208E24FA074E5AB3143DB5BFCE0FD108E4B82D120A93AD2CAFFFFFFFFFFFFFFFF");
    private static readonly BigInteger G = new(5);
    private static readonly int PadLen = N.ToByteArray(isUnsigned: true, isBigEndian: true).Length; // 384
    private static readonly BigInteger K = BEFromHex(Convert.ToHexString(Sha512(Pad(N), Pad(G))));

    private readonly BigInteger _a;
    private readonly BigInteger _clientPublic; // A

    public byte[] SessionKey { get; private set; } = Array.Empty<byte>(); // K = H(S), raw 64 bytes

    public SrpTransientClient() : this(RandomNumberGenerator.GetBytes(32))
    {
    }

    /// <summary>Testing seam: fixes the client private exponent instead of randomizing it.</summary>
    internal SrpTransientClient(byte[] fixedPrivateBytes)
    {
        _a = BEFromBytes(fixedPrivateBytes);
        _clientPublic = BigInteger.ModPow(G, _a, N);
    }

    private static BigInteger BEFromHex(string hex) => BEFromBytes(Convert.FromHexString(hex));

    private static BigInteger BEFromBytes(byte[] bytes) => new(bytes, isUnsigned: true, isBigEndian: true);

    private static byte[] ToBE(BigInteger v) => v.ToByteArray(isUnsigned: true, isBigEndian: true);

    private static byte[] Pad(BigInteger v)
    {
        var b = ToBE(v);
        if (b.Length == PadLen) return b;
        var r = new byte[PadLen];
        Buffer.BlockCopy(b, 0, r, PadLen - b.Length, b.Length);
        return r;
    }

    private static byte[] Sha512(params byte[][] parts)
    {
        using var sha = SHA512.Create();
        foreach (var p in parts) sha.TransformBlock(p, 0, p.Length, null, 0);
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return sha.Hash!;
    }

    private static BigInteger HashInt(params byte[][] parts) => BEFromBytes(Sha512(parts));

    /// <summary>Client public key A, sent as PublicKey in the M1 pair-setup request.</summary>
    public byte[] ClientPublic => ToBE(_clientPublic);

    /// <summary>
    /// Processes the device's salt + public key (from the M2 response) and
    /// returns (clientPublic, proof) to send back as the M3 request body.
    /// </summary>
    public (byte[] PublicKey, byte[] Proof) ComputeProof(byte[] serverPublicBytes, byte[] saltBytes)
    {
        var serverPublic = BEFromBytes(serverPublicBytes); // B
        if (serverPublic % N == 0)
            throw new InvalidOperationException("invalid server public key (B mod N == 0)");

        // x = H(s | H(I:P))
        var innerHash = Sha512(Encoding.UTF8.GetBytes($"{Username}:{Pin}"));
        var x = HashInt(saltBytes, innerHash);

        // u = H(PAD(A) | PAD(B))
        var u = HashInt(Pad(_clientPublic), Pad(serverPublic));

        // S = (B - k*g^x) ^ (a + u*x) mod N
        var v = BigInteger.ModPow(G, x, N);
        var baseVal = ((serverPublic - K * v) % N + N) % N;
        var exp = _a + u * x;
        var premaster = BigInteger.ModPow(baseVal, exp, N);

        SessionKey = Sha512(ToBE(premaster));

        // M1 = H( H(N) xor H(g) | H(I) | s | A | B | K )
        var hn = Sha512(ToBE(N));
        var hg = Sha512(ToBE(G));
        var hng = new byte[hn.Length];
        for (var i = 0; i < hn.Length; i++) hng[i] = (byte)(hn[i] ^ hg[i]);
        var hi = Sha512(Encoding.UTF8.GetBytes(Username));

        var proof = Sha512(hng, hi, saltBytes, ToBE(_clientPublic), ToBE(serverPublic), SessionKey);

        return (ToBE(_clientPublic), proof);
    }

    /// <summary>Derives a 32-byte key via HKDF-SHA512 over the SRP session key.</summary>
    public byte[] DeriveKey(string salt, string info) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA512, SessionKey, 32, Encoding.UTF8.GetBytes(salt), Encoding.UTF8.GetBytes(info));
}
