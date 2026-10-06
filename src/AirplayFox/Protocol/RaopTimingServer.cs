using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace AirplayFox.Protocol;

/// <summary>
/// Responds to the speaker's NTP-style timing probes so it can lock its
/// playback clock to ours. Mirrors pyatv's raop/protocols/__init__.py
/// TimingServer: echoes the probe's send-time back as "reftime" and stamps
/// our own receive time as "recvtime"/"sendtime".
/// </summary>
public sealed class RaopTimingServer : IDisposable
{
    private const int PacketLength = 32;

    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();

    private RaopTimingServer(UdpClient udp) => _udp = udp;

    public static Task<RaopTimingServer> StartAsync(string localIp)
    {
        var udp = new UdpClient(new IPEndPoint(IPAddress.Parse(localIp), 0));
        var server = new RaopTimingServer(udp);
        _ = Task.Run(() => server.ListenLoop(server._cts.Token));
        return Task.FromResult(server);
    }

    public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await _udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }

            var data = result.Buffer;
            if (data.Length < PacketLength) continue;

            var proto = data[0];
            var sendtimeSec = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(24, 4));
            var sendtimeFrac = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(28, 4));
            var (recvSec, recvFrac) = Ntp.ToParts(Ntp.Now());

            var resp = new byte[PacketLength];
            resp[0] = proto;
            resp[1] = 0x53 | 0x80;
            BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(2, 2), 7);
            BinaryPrimitives.WriteUInt32BigEndian(resp.AsSpan(8, 4), sendtimeSec);
            BinaryPrimitives.WriteUInt32BigEndian(resp.AsSpan(12, 4), sendtimeFrac);
            BinaryPrimitives.WriteUInt32BigEndian(resp.AsSpan(16, 4), recvSec);
            BinaryPrimitives.WriteUInt32BigEndian(resp.AsSpan(20, 4), recvFrac);
            BinaryPrimitives.WriteUInt32BigEndian(resp.AsSpan(24, 4), recvSec);
            BinaryPrimitives.WriteUInt32BigEndian(resp.AsSpan(28, 4), recvFrac);

            await _udp.SendAsync(resp, result.RemoteEndPoint);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp.Dispose();
        _cts.Dispose();
    }
}
