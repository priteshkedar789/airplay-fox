using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using AirplayFox.Protocol;

namespace AirplayFox.Audio;

/// <summary>
/// Device-rate stereo float in, 44.1 kHz stereo float out into a <see cref="PcmRing"/>.
/// Whole chunks only: the resampler is read only when enough real input exists for a full output chunk, and the
/// staging buffer never pads with silence, so a late input burst cannot splice zeros into the signal.
/// </summary>
public sealed class RateConverter
{
    private const int ChunkOutFrames = 256;
    private const int LookaheadFrames = 256; // resampler filter taps

    private readonly int _srcRate;
    private readonly PcmRing _ring;
    private readonly BufferedWaveProvider? _staging;
    private readonly WdlResamplingSampleProvider? _resampler;
    private readonly int _needBytes;
    private readonly float[] _chunk = new float[ChunkOutFrames * 2];

    public RateConverter(int sourceRate, PcmRing ring)
    {
        _srcRate = sourceRate;
        _ring = ring;
        if (sourceRate == RaopAudioContext.SampleRate) return; // exact passthrough, no resampling at all

        _staging = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(sourceRate, 2))
        {
            ReadFully = false,
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(1),
        };
        _resampler = new WdlResamplingSampleProvider(_staging.ToSampleProvider(), RaopAudioContext.SampleRate);
        _needBytes = (int)Math.Ceiling(ChunkOutFrames * (double)sourceRate / RaopAudioContext.SampleRate + LookaheadFrames) * 2 * sizeof(float);
    }

    public void Push(float[] stereo)
    {
        if (_staging is null) { _ring.Write(stereo); return; }

        var bytes = new byte[stereo.Length * sizeof(float)];
        Buffer.BlockCopy(stereo, 0, bytes, 0, bytes.Length);
        _staging.AddSamples(bytes, 0, bytes.Length);
        while (_staging.BufferedBytes >= _needBytes)
        {
            var n = _resampler!.Read(_chunk, 0, _chunk.Length);
            if (n == 0) break;
            _ring.Write(_chunk.AsSpan(0, n));
        }
    }

    /// <summary>Nothing is playing: discard the few ms parked in the resampler so they don't replay stale at the next start.</summary>
    public void DropPending() => _staging?.ClearBuffer();
}
