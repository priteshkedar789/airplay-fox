using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using AirplayFox.Protocol.Crypto;

namespace AirplayFox.Protocol;

/// <summary>
/// Sends one RTP audio packet at a time over the UDP data channel, ChaCha20
/// encrypted with the shared secret negotiated in the second SETUP call.
/// Mirrors pyatv's AirPlayV2.send_audio_packet + StreamClient._send_packet.
/// Pacing is the caller's responsibility - for live capture, feeding frames
/// from the WASAPI callback as they arrive already paces this correctly, so
/// there's no wall-clock sleep/catch-up loop here (unlike pyatv, which also
/// has to pace file playback with no natural real-time source).
///
/// Keeps a backlog of recently sent packets so RaopControlClient can
/// service the receiver's retransmit requests for packets lost in transit
/// (real Wi-Fi loss, not a bug - this is why RAOP has a retransmit channel
/// at all). Without this, every lost UDP packet was an audible click with
/// no recovery, which is what made extended live streams sound consistently
/// scratchy while a 3-second tone rarely hit it.
/// </summary>
public sealed class RaopAudioSender : IDisposable
{
    private const int BacklogCapacity = 1024; // ~8s of packets at 44100Hz/352fpp - far more than any real retransmit window needs

    private readonly UdpClient _udp;
    private readonly IPEndPoint _remote;
    private readonly RaopAudioContext _context;
    private readonly Chacha20Cipher _cipher;
    private readonly ConcurrentDictionary<ushort, byte[]> _backlog = new();
    private readonly ConcurrentQueue<ushort> _backlogOrder = new();

    public RaopAudioSender(string remoteIp, int remotePort, RaopAudioContext context, byte[] sharedSecret)
    {
        _udp = new UdpClient();
        _remote = new IPEndPoint(IPAddress.Parse(remoteIp), remotePort);
        _context = context;
        _cipher = new Chacha20Cipher(sharedSecret, sharedSecret);
    }

    public bool TryGetBacklogPacket(ushort seqno, out byte[] packet) => _backlog.TryGetValue(seqno, out packet!);

    private bool _firstPacket = true;

    /// <summary>Sends exactly one packet worth of PCM frames (must be RaopAudioContext.PacketSize bytes,
    /// 16-bit stereo interleaved, zero-padded by the caller if short).</summary>
    public void SendPacket(ReadOnlySpan<byte> pcmFrame)
    {
        if (pcmFrame.Length != RaopAudioContext.PacketSize)
            throw new ArgumentException($"expected exactly {RaopAudioContext.PacketSize} bytes, got {pcmFrame.Length}");

        var seqno = _context.RtpSeq;
        var header = new byte[12];
        header[0] = 0x80;
        header[1] = (byte)(_firstPacket ? 0xE0 : 0x60);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2, 2), seqno);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)_context.RtpTime);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8, 4), _context.Ssrc);
        _firstPacket = false;

        var nonce = _cipher.OutNoncePeek();
        var aad = header.AsSpan(4, 8).ToArray();
        var encrypted = _cipher.Encrypt(pcmFrame.ToArray(), aad: aad);

        var packet = new byte[header.Length + encrypted.Length + 8];
        Buffer.BlockCopy(header, 0, packet, 0, header.Length);
        Buffer.BlockCopy(encrypted, 0, packet, header.Length, encrypted.Length);
        Buffer.BlockCopy(nonce, 4, packet, header.Length + encrypted.Length, 8); // last 8 bytes of the 12-byte nonce

        _udp.Send(packet, packet.Length, _remote);

        _backlog[seqno] = packet;
        _backlogOrder.Enqueue(seqno);
        while (_backlogOrder.Count > BacklogCapacity && _backlogOrder.TryDequeue(out var oldest))
            _backlog.TryRemove(oldest, out _);

        _context.Advance(RaopAudioContext.FramesPerPacket);
    }

    public void Dispose() => _udp.Dispose();
}
