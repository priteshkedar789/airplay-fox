using AirplayFox.Protocol.Crypto;

namespace AirplayFox.Protocol.Rtsp;

/// <summary>
/// AirPlay2 HAP "transient" pairing (pyatv's AirPlayHapTransientPairVerifyProcedure):
/// an SRP-6a exchange with the well-known PIN 3939 over two /pair-setup POSTs.
/// No PIN entry, no long-term identity - the shared secret is derived fresh
/// every connection. After this, EncryptionKeys() derives session keys for
/// the main RTSP connection and for side channels (event channel, RTP audio).
/// </summary>
public sealed class HapTransientPairing
{
    private static readonly IReadOnlyDictionary<string, string> Headers = new Dictionary<string, string>
    {
        ["User-Agent"] = "AirPlay/320.20",
        ["Connection"] = "keep-alive",
        ["X-Apple-HKP"] = "4",
    };

    private readonly RtspConnection _rtsp;
    private readonly SrpTransientClient _srp = new();

    public HapTransientPairing(RtspConnection rtsp) => _rtsp = rtsp;

    public async Task PairAsync()
    {
        await _rtsp.SendAsync("POST", "/pair-pin-start", headers: Headers);

        var m1 = Tlv8.Write(new Dictionary<int, byte[]>
        {
            [TlvValue.Method] = new byte[] { 0x00 },
            [TlvValue.SeqNo] = new byte[] { 0x01 },
            [TlvValue.Flags] = new byte[] { 0x10 }, // TransientPairing
        });
        var m2Resp = await _rtsp.SendAsync("POST", "/pair-setup", contentType: "application/octet-stream", headers: Headers, body: m1);
        var m2 = Tlv8.Read(m2Resp.Body);

        var salt = m2[TlvValue.Salt];
        var serverPublic = m2[TlvValue.PublicKey];

        var (clientPublic, proof) = _srp.ComputeProof(serverPublic, salt);

        var m3 = Tlv8.Write(new Dictionary<int, byte[]>
        {
            [TlvValue.SeqNo] = new byte[] { 0x03 },
            [TlvValue.PublicKey] = clientPublic,
            [TlvValue.Proof] = proof,
        });
        await _rtsp.SendAsync("POST", "/pair-setup", contentType: "application/octet-stream", headers: Headers, body: m3);
    }

    /// <summary>Derives (outputKey, inputKey) for a given channel salt/info, matching
    /// pyatv's PairVerifyProcedure.encryption_keys().</summary>
    public (byte[] OutputKey, byte[] InputKey) EncryptionKeys(string salt, string outputInfo, string inputInfo) =>
        (_srp.DeriveKey(salt, outputInfo), _srp.DeriveKey(salt, inputInfo));
}
