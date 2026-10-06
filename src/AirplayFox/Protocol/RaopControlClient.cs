using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace AirplayFox.Protocol;

/// <summary>
/// Sends the periodic SyncPacket that lets the receiver lock its playback
/// clock to ours (~1/sec), and services the receiver's retransmit requests
/// for RTP audio packets lost in transit. Mirrors pyatv's ControlClient.
/// </summary>
public sealed class RaopControlClient : IDisposable
{
    private readonly UdpClient _udp;
    private RaopAudioContext? _context;
    private RaopAudioSender? _sender;
    private IPEndPoint? _remote;
    private readonly CancellationTokenSource _cts = new();

    public long RetransmitRequests;
    public long RetransmitPacketsRequested;
    public long RetransmitPacketsSent;
    public long RetransmitPacketsMissed;
    /// <summary>Longest single request for consecutive lost packets (a burst of loss, not scattered drops).</summary>
    public int MaxRetransmitRun;

    /// <summary>Binds the local control socket immediately so its port can be
    /// included in the audio-stream SETUP request; call Start() once the
    /// receiver's control port is known from the SETUP response.</summary>
    public RaopControlClient(string localIp) => _udp = new UdpClient(new IPEndPoint(IPAddress.Parse(localIp), 0));

    public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

    public void Start(string remoteIp, int remoteControlPort, RaopAudioContext context, RaopAudioSender sender)
    {
        _context = context;
        _sender = sender;
        _remote = new IPEndPoint(IPAddress.Parse(remoteIp), remoteControlPort);
        _ = Task.Run(() => SyncLoop(_cts.Token));
        _ = Task.Run(() => ReceiveLoop(_cts.Token));
    }

    /// <summary>Handles retransmit requests (marker 0x55): the receiver asks for a
    /// run of RTP audio seqnos it never got (real Wi-Fi packet loss - without this,
    /// every lost packet is a permanent click with no recovery). Mirrors pyatv's
    /// ControlClient.datagram_received / _retransmit_lost_packets exactly, including
    /// its from-scratch response framing (0x80 0xD6 + original seqno + original packet).</summary>
    private async Task ReceiveLoop(CancellationToken ct)
    {
        var sender = _sender!;
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await _udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }

            var data = result.Buffer;
            if (data.Length < 8) continue;
            var actualType = (byte)(data[1] & 0x7F);
            if (actualType != 0x55) continue;

            var lostSeqno = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4, 2));
            var lostPackets = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(6, 2));
            RetransmitRequests++;
            RetransmitPacketsRequested += lostPackets;
            if (lostPackets > MaxRetransmitRun) MaxRetransmitRun = lostPackets;

            for (var i = 0; i < lostPackets; i++)
            {
                var seqno = (ushort)(lostSeqno + i);
                if (!sender.TryGetBacklogPacket(seqno, out var packet)) { RetransmitPacketsMissed++; continue; }

                var resp = new byte[4 + packet.Length];
                resp[0] = 0x80;
                resp[1] = 0xD6;
                resp[2] = packet[2]; // original seqno, copied straight from the packet header
                resp[3] = packet[3];
                Buffer.BlockCopy(packet, 0, resp, 4, packet.Length);

                try { await _udp.SendAsync(resp, result.RemoteEndPoint, ct); RetransmitPacketsSent++; }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task SyncLoop(CancellationToken ct)
    {
        var context = _context!;
        var remote = _remote!;
        var firstPacket = true;

        // "last sync" time is deliberately a stale snapshot taken before the
        // previous iteration's sleep - NOT recomputed fresh each time. It
        // represents when the last sync happened, distinct from "now" (the
        // fresh rtptime below). Collapsing the two to the same instant (an
        // earlier bug here) gave the receiver two timestamps ~0 apart every
        // packet, which it apparently read as a clock-rate anomaly and
        // audibly corrected for - a click roughly once per second, matching
        // this loop's cadence exactly. Mirrors pyatv's ControlClient._sync_task.
        var lastSyncTime = Ntp.FromTimestamp(context.HeadTs, RaopAudioContext.SampleRate);

        while (!ct.IsCancellationRequested)
        {
            var (sec, frac) = Ntp.ToParts(lastSyncTime);

            var packet = new byte[20];
            packet[0] = (byte)(firstPacket ? 0x90 : 0x80);
            packet[1] = 0xD4;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), 0x0007);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), (uint)(context.RtpTime - context.Latency));
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8, 4), sec);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(12, 4), frac);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16, 4), (uint)context.RtpTime);
            firstPacket = false;

            try { await _udp.SendAsync(packet, remote, ct); }
            catch (OperationCanceledException) { break; }

            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { break; }

            lastSyncTime = Ntp.FromTimestamp(context.HeadTs, RaopAudioContext.SampleRate);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp.Dispose();
        _cts.Dispose();
    }
}
