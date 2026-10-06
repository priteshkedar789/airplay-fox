using AirplayFox;
using AirplayFox.Audio;
using Xunit;

public class RingTests
{
    [Fact]
    public void Preserves_order_across_wraparound()
    {
        var ring = new PcmRing(8);
        var next = 0f;
        var expect = 0f;
        for (var round = 0; round < 20; round++)
        {
            var w = new float[5 * 2];
            for (var i = 0; i < w.Length; i++) w[i] = next++;
            ring.Write(w);
            var r = new float[5 * 2];
            Assert.Equal(5, ring.Read(r));
            foreach (var v in r) Assert.Equal(expect++, v);
        }
    }

    [Fact]
    public void Overflow_drops_oldest_and_counts()
    {
        var ring = new PcmRing(4);
        ring.Write(new float[] { 1, 1, 2, 2, 3, 3 });
        ring.Write(new float[] { 4, 4, 5, 5, 6, 6 });          // 6 frames into capacity 4
        Assert.Equal(4, ring.FramesAvailable);
        Assert.Equal(2, ring.FramesOverwritten);
        var r = new float[8];
        ring.Read(r);
        Assert.Equal(new float[] { 3, 3, 4, 4, 5, 5, 6, 6 }, r);
    }

    [Fact]
    public void Read_never_returns_more_than_stored()
    {
        var ring = new PcmRing(16);
        ring.Write(new float[6]);
        Assert.Equal(3, ring.Read(new float[20]));
        Assert.Equal(0, ring.Read(new float[20]));
    }
}

public class DriftTests
{
    [Theory]
    [InlineData(5000, 5000, 1000, 0)]    // on target
    [InlineData(5900, 5000, 1000, 0)]    // inside deadband
    [InlineData(6100, 5000, 1000, +1)]   // too full: drop
    [InlineData(3900, 5000, 1000, -1)]   // too empty: repeat
    public void Holds_inside_deadband_and_corrects_outside(int fill, int target, int band, int expected) =>
        Assert.Equal(expected, DriftRegulator.Adjust(fill, target, band));
}

public class TunerTests
{
    [Fact]
    public void Calm_link_gets_the_floor_and_worse_links_get_more()
    {
        var calm = LatencyTuner.Decide(Enumerable.Repeat(5.0, 40).ToArray(), 500, 0, 0);
        var jittery = LatencyTuner.Decide(Enumerable.Repeat(5.0, 39).Append(300.0).ToArray(), 500, 0, 0);
        var lossy = LatencyTuner.Decide(Enumerable.Repeat(5.0, 40).ToArray(), 500, 100, 8);
        Assert.Equal(LatencyTuner.MinLatencyMs, calm.LatencyMs);
        Assert.True(jittery.LatencyMs > calm.LatencyMs);
        Assert.True(lossy.LatencyMs > calm.LatencyMs);
    }

    [Fact]
    public void Real_world_probe_of_5_percent_loss_stays_above_the_old_failing_650ms()
    {
        // Measured on the target HomePod: rtt p50 33 ms / max 83 ms / loss 5.2% / run 1 produced scratching at 650 ms.
        var r = LatencyTuner.Decide(new double[] { 33, 33, 33, 40, 83 }, 501, 26, 1);
        Assert.True(r.LatencyMs > 650, r.ToString());
    }

    [Fact]
    public void No_data_or_absurd_data_is_safe()
    {
        Assert.Equal(1500, LatencyTuner.Decide(Array.Empty<double>(), 500, 0, 0).LatencyMs);
        Assert.Equal(LatencyTuner.MaxLatencyMs, LatencyTuner.Decide(new[] { 60000.0 }, 500, 500, 500).LatencyMs);
    }
}

public class ConverterTests
{
    /// <summary>A pure tone at 48 kHz, delivered in uneven bursts like WASAPI does, must come out as one continuous tone
    /// at 44.1 kHz: no zeros spliced in, no steps. Measured with a linear-recurrence predictor (exact for a sine).</summary>
    [Fact]
    public void Bursty_48k_tone_stays_continuous_after_resampling()
    {
        const int srcRate = 48000;
        const double w48 = 2 * Math.PI * 1000 / srcRate;
        var ring = new PcmRing(44100 * 5);
        var conv = new RateConverter(srcRate, ring);
        var rng = new Random(7);
        long pos = 0;
        while (pos < srcRate * 3)
        {
            var frames = rng.Next(60, 900);                 // uneven delivery
            var buf = new float[frames * 2];
            for (var i = 0; i < frames; i++) { var v = (float)(0.5 * Math.Sin(w48 * (pos + i))); buf[i * 2] = v; buf[i * 2 + 1] = v; }
            pos += frames;
            conv.Push(buf);
        }

        var all = new float[ring.FramesAvailable * 2];
        var n = ring.Read(all);
        Assert.True(n > 44100 * 2, $"only {n} frames came out");
        var x = Enumerable.Range(2000, n - 4000).Select(i => (double)all[i * 2]).ToArray(); // skip filter warm-up/tail
        var w44 = 2 * Math.PI * 1000 / 44100;
        double worst = 0;
        for (var i = 2; i < x.Length; i++) worst = Math.Max(worst, Math.Abs(x[i] - (2 * Math.Cos(w44) * x[i - 1] - x[i - 2])));
        Assert.True(worst < 0.01, $"discontinuity of {worst:F4} (full scale 0.5)");
    }
}

public class WireFormatTests
{
    [Fact]
    public void Samples_go_out_big_endian_like_pyatv()
    {
        // 0x1234 as a little-endian WAV sample is 34 12; RAOP wants 12 34.
        var le = new byte[] { 0x34, 0x12, 0xFF, 0x7F, 0x00, 0x80, 0x01, 0x00 };
        var wire = new byte[le.Length];
        PacketPump.ToWire(le, wire);
        Assert.Equal(new byte[] { 0x12, 0x34, 0x7F, 0xFF, 0x80, 0x00, 0x00, 0x01 }, wire);
    }

    [Fact]
    public void A_small_negative_sample_does_not_become_a_huge_one()
    {
        // -2 LE = FE FF. Read back as big-endian it must still be -2 (the old bug read it as -257).
        var wire = new byte[2];
        PacketPump.ToWire(new byte[] { 0xFE, 0xFF }, wire);
        Assert.Equal(-2, (short)((wire[0] << 8) | wire[1]));
    }
}
