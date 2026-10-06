namespace AirplayFox.Protocol;

/// <summary>Per-session RTP/timing state, mirrors pyatv's StreamContext.</summary>
public sealed class RaopAudioContext
{
    public const int SampleRate = 44100; // fixed by RAOP, not negotiable
    public const int Channels = 2;
    public const int BytesPerChannel = 2;
    public const int FramesPerPacket = 352;

    public ushort RtpSeq { get; private set; }
    public long StartTs { get; private set; }
    public long HeadTs { get; private set; }
    /// <summary>Playout delay in frames: the receiver plays each sample this long after its
    /// RTP time. This is the lag you hear, so lower is snappier but leaves less room for
    /// retransmits to rescue lost packets. Set per session via Reset(latencyFrames).</summary>
    public long Latency { get; private set; } = FramesFromMs(DefaultLatencyMs);

    public const int DefaultLatencyMs = 1500; // the long-validated value, kept as the fallback
    public static int FramesFromMs(int ms) => (int)(ms * (long)SampleRate / 1000);
    public uint Ssrc { get; init; }

    public const int FrameSize = Channels * BytesPerChannel;
    public const int PacketSize = FramesPerPacket * FrameSize;

    /// <summary>Current RTP time including latency offset, as sent in sync packets and audio headers.</summary>
    public long RtpTime => HeadTs - (StartTs - Latency);

    public void Reset(int latencyFrames)
    {
        Latency = latencyFrames;
        Reset();
    }

    public void Reset()
    {
        RtpSeq = (ushort)Random.Shared.Next(ushort.MaxValue);
        StartTs = Ntp.ToTimestamp(Ntp.Now(), SampleRate);
        HeadTs = StartTs;
    }

    /// <summary>Call after sending a packet built from the current RtpSeq/RtpTime.</summary>
    public void Advance(int framesSent)
    {
        RtpSeq++;
        HeadTs += framesSent;
    }
}
