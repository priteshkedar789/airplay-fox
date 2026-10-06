using System.Diagnostics;
using AirplayFox.Protocol;

namespace AirplayFox;

/// <summary>What a probe measured and the playout latency chosen from it.</summary>
public sealed record TuneResult(int LatencyMs, double RttP50Ms, double RttMaxMs, double LossPercent, int MaxLostRun, int PacketsSent)
{
    public override string ToString() =>
        $"latency={LatencyMs}ms (rtt p50={RttP50Ms:F0}ms max={RttMaxMs:F0}ms, loss={LossPercent:F1}%, worst run={MaxLostRun} pkts, probe={PacketsSent} pkts)";
}

/// <summary>
/// Picks the lowest playout latency the current Wi-Fi link can sustain.
///
/// Latency is the speaker's jitter buffer. A lost packet is rescued only if the speaker notices, asks again and our
/// resend lands before that sample's play time, so the buffer must cover (gap detection + round trip) for the worst
/// case on this link plus the longest run of lost packets.
///
/// The probe is a short throwaway session streaming inaudible silence at the real packet rate while sampling the RTSP
/// round trip; the speaker's retransmit requests are the loss measurement. Probe loss ran about half of what the real
/// stream later saw (measured: 5% vs 12%), so loss is scaled up before it is used.
/// </summary>
public static class LatencyTuner
{
    public const int MinLatencyMs = 350;
    public const int MaxLatencyMs = 2000;
    private const int ProbeLatencyMs = 1000;   // generous so the probe itself never glitches into the measurement
    private const int StepMs = 50;
    private const double LossScale = 2.0;      // probe underestimates real-stream loss
    private const double Margin = 1.25;
    private const double PacketMs = 1000.0 * RaopAudioContext.FramesPerPacket / RaopAudioContext.SampleRate;

    public static async Task<TuneResult> ProbeAsync(AirplayDevice device, TimeSpan probe)
    {
        using var session = new AirplaySession();
        var sender = await session.StartAsync(device, new SessionOptions(ProbeLatencyMs, InitialVolumePercent: null, KeepAlive: false));
        var control = session.ControlClient!;

        var ring = new Audio.PcmRing(RaopAudioContext.SampleRate); // stays empty: the pump sends silence
        using var pump = new Audio.PacketPump(ring, pcm => sender.SendPacket(pcm), standingFillMs: 100);
        pump.Start();

        // RTT samples, strictly sequential (one RTSP request at a time; keep-alive is off for probes).
        var rtts = new List<double>();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < probe)
        {
            try { rtts.Add(await session.PingAsync()); }
            catch (Exception ex) { Log.Warn($"tuner ping failed: {ex.Message}"); break; }
            await Task.Delay(100);
        }
        var sent = (int)pump.Stats.Packets;
        await Task.Delay(300); // let late retransmit requests for the tail arrive
        pump.Dispose();
        await session.StopAsync();
        return Decide(rtts, sent, control.RetransmitPacketsRequested, control.MaxRetransmitRun);
    }

    /// <summary>Pure decision rule (unit-tested without hardware).</summary>
    public static TuneResult Decide(IReadOnlyList<double> rttsMs, int packetsSent, long lostPackets, int maxLostRun)
    {
        if (rttsMs.Count == 0) // no measurement: stay on the proven setting rather than guess low
            return new TuneResult(RaopAudioContext.DefaultLatencyMs, 0, 0, 0, 0, packetsSent);

        var sorted = rttsMs.OrderBy(x => x).ToArray();
        var p50 = sorted[sorted.Length / 2];
        var max = sorted[^1];
        var lossPct = packetsSent > 0 ? 100.0 * lostPackets / packetsSent : 0;
        var effLoss = lossPct * LossScale;

        // Gap noticed on the next packet, request half an RTT out, resend half an RTT back; two attempts of the worst RTT.
        var need = 150 + 2 * (PacketMs + max) + maxLostRun * PacketMs;
        if (effLoss > 5) need += 200;
        if (effLoss > 15) need += 300;
        if (effLoss > 30) need += 500;
        need *= Margin;

        var ms = (int)(Math.Ceiling(need / StepMs) * StepMs);
        return new TuneResult(Math.Clamp(ms, MinLatencyMs, MaxLatencyMs), p50, max, lossPct, maxLostRun, packetsSent);
    }

    /// <summary>Probe, falling back to the default if the probe cannot run.</summary>
    public static async Task<int> TuneOrDefaultAsync(AirplayDevice device)
    {
        try
        {
            var result = await ProbeAsync(device, TimeSpan.FromSeconds(4));
            Log.Info($"tuner: {result}");
            return result.LatencyMs;
        }
        catch (Exception ex)
        {
            Log.Warn($"tuner probe failed, using {RaopAudioContext.DefaultLatencyMs} ms: {ex.Message}");
            return RaopAudioContext.DefaultLatencyMs;
        }
    }
}
