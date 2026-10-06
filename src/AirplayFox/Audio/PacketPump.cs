using System.Diagnostics;
using System.Runtime.InteropServices;
using AirplayFox.Protocol;

namespace AirplayFox.Audio;

public sealed class PumpStats
{
    public long Packets, SilencePackets, Underruns, FramesDropped, FramesRepeated, Stalls;
    public double MaxLateMs;
    public override string ToString() =>
        $"packets={Packets} silence={SilencePackets} underruns={Underruns} drift(drop/repeat)={FramesDropped}/{FramesRepeated} stalls={Stalls} maxLate={MaxLateMs:F1}ms";
}

/// <summary>
/// Turns the ring into a steady stream of 352-frame RTP packets, one every 7.98 ms of wall-clock time.
///
/// Two states: Prefill sends silence until the ring holds a "standing fill" (a small cushion that rides out
/// bursty capture), Run sends real audio and trims drift. If the ring ever runs dry it falls back to Prefill
/// instead of sending partial packets, so a late chunk can never splice zeros into the middle of a signal.
/// While the PC is silent (WASAPI loopback delivers nothing) it simply keeps sending silence, which also keeps
/// the speaker's timeline and keep-alive happy.
/// </summary>
public sealed class PacketPump : IDisposable
{
    private const int Frames = RaopAudioContext.FramesPerPacket;
    private const double StallRebaselineMs = 100;

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);
    [DllImport("avrt.dll", CharSet = CharSet.Unicode)] private static extern IntPtr AvSetMmThreadCharacteristicsW(string task, ref int index);
    [DllImport("avrt.dll")] private static extern bool AvRevertMmThreadCharacteristics(IntPtr h);

    private readonly PcmRing _ring;
    private readonly Action<byte[]> _send;
    private readonly int _targetFrames;
    private readonly int _deadbandFrames;
    private readonly Thread _thread;
    private volatile bool _stop;
    private bool _failed;

    public PumpStats Stats { get; } = new();
    /// <summary>Called with every PCM16 packet we send (testing/recording only).</summary>
    public Action<byte[]>? Tap { get; set; }
    /// <summary>Raised once if sending throws (socket gone): the session is dead.</summary>
    public event Action<Exception>? Failed;

    public PacketPump(PcmRing ring, Action<byte[]> sendPacket, int standingFillMs)
    {
        _ring = ring;
        _send = sendPacket;
        _targetFrames = Math.Max(Frames * 2, RaopAudioContext.FramesFromMs(standingFillMs));
        _deadbandFrames = RaopAudioContext.FramesFromMs(30);
        _thread = new Thread(Run) { IsBackground = true, Name = "AirplayFox.Pump", Priority = ThreadPriority.Highest };
    }

    public void Start() => _thread.Start();

    private void Run()
    {
        timeBeginPeriod(1);
        var index = 0;
        var mmcss = AvSetMmThreadCharacteristicsW("Pro Audio", ref index);
        try { Loop(); }
        finally
        {
            if (mmcss != IntPtr.Zero) AvRevertMmThreadCharacteristics(mmcss);
            timeEndPeriod(1);
        }
    }

    private void Loop()
    {
        var floats = new float[(Frames + 1) * 2];
        var pcm = new byte[Frames * 4];
        var wire = new byte[Frames * 4];
        var silence = new byte[Frames * 4];
        var filling = true;
        var sw = Stopwatch.StartNew();
        var ticksPerPacket = Stopwatch.Frequency * Frames / (double)RaopAudioContext.SampleRate;
        long n = 0;
        double baseTicks = 0;

        while (!_stop)
        {
            // Wait for this packet's absolute deadline: coarse sleep, then spin the last ~1 ms.
            var deadline = baseTicks + n * ticksPerPacket;
            var remainingMs = (deadline - sw.ElapsedTicks) * 1000.0 / Stopwatch.Frequency;
            if (remainingMs > 2) Thread.Sleep((int)remainingMs - 1);
            while (sw.ElapsedTicks < deadline && !_stop) Thread.SpinWait(30);
            var lateMs = (sw.ElapsedTicks - deadline) * 1000.0 / Stopwatch.Frequency;
            if (lateMs > Stats.MaxLateMs) Stats.MaxLateMs = lateMs;
            if (lateMs > StallRebaselineMs) // suspend/resume or a long stall: don't burst to "catch up"
            {
                Stats.Stalls++;
                baseTicks = sw.ElapsedTicks;
                n = 0;
            }
            n++;

            var fill = _ring.FramesAvailable;
            if (filling && fill >= _targetFrames) filling = false;
            if (!filling && fill < Frames) { filling = true; Stats.Underruns++; }

            byte[] packet;
            if (filling)
            {
                packet = silence;
                Stats.SilencePackets++;
            }
            else
            {
                var adj = DriftRegulator.Adjust(fill, _targetFrames, _deadbandFrames);
                // Consume one frame more (drop it) or one less (repeat the last) than we emit.
                var want = adj > 0 ? Frames + 1 : adj < 0 ? Frames - 1 : Frames;
                var got = _ring.Read(floats.AsSpan(0, want * 2));
                if (adj > 0 && got == Frames + 1) { got = Frames; Stats.FramesDropped++; }
                else if (adj < 0 && got == Frames - 1) Stats.FramesRepeated++;
                // Short read (repeat case): fill the tail with the last real frame.
                for (var f = got; f < Frames; f++)
                {
                    floats[f * 2] = floats[(got - 1) * 2];
                    floats[f * 2 + 1] = floats[(got - 1) * 2 + 1];
                }
                ToPcm16(floats, pcm);
                packet = pcm;
            }

            try
            {
                Tap?.Invoke(packet);            // little-endian PCM, i.e. a normal WAV
                ToWire(packet, wire);
                _send(wire);
                Stats.Packets++;
            }
            catch (Exception ex)
            {
                if (!_failed) { _failed = true; Failed?.Invoke(ex); }
                return;
            }
        }
    }

    /// <summary>RAOP carries 16-bit PCM big-endian (pyatv byte-swaps for the same reason). Sending the
    /// little-endian bytes made the speaker read every sample byte-swapped: ~+48 dB gain on quiet signals and
    /// scrambled, scratchy audio on loud ones.</summary>
    public static void ToWire(ReadOnlySpan<byte> littleEndian, Span<byte> bigEndian)
    {
        for (var i = 0; i + 1 < littleEndian.Length; i += 2)
        {
            bigEndian[i] = littleEndian[i + 1];
            bigEndian[i + 1] = littleEndian[i];
        }
    }

    private static void ToPcm16(float[] src, byte[] dst)
    {
        for (int i = 0, o = 0; i < Frames * 2; i++, o += 2)
        {
            var v = (short)Math.Clamp(MathF.Round(src[i] * 32767f), -32768f, 32767f);
            dst[o] = (byte)v;
            dst[o + 1] = (byte)(v >> 8);
        }
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread.IsAlive && Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(2));
    }
}
