namespace AirplayFox.Audio;

/// <summary>
/// Thread-safe FIFO of interleaved stereo float frames between the capture thread (bursty, device clock)
/// and the packet pump (steady, wall clock). Decoupling them is the point: a late WASAPI chunk no longer
/// stalls or corrupts the outgoing stream, the ring's fill level absorbs it.
/// </summary>
public sealed class PcmRing
{
    private const int Ch = 2;
    private readonly float[] _buf;
    private readonly object _gate = new();
    private int _head;   // next frame to read
    private int _count;  // frames stored

    public long FramesOverwritten;  // oldest audio discarded because the pump was not keeping up

    public PcmRing(int capacityFrames) => _buf = new float[capacityFrames * Ch];

    public int Capacity => _buf.Length / Ch;
    public int FramesAvailable { get { lock (_gate) return _count; } }

    public void Write(ReadOnlySpan<float> interleaved)
    {
        lock (_gate)
        {
            var frames = interleaved.Length / Ch;
            if (frames > Capacity) { interleaved = interleaved[((frames - Capacity) * Ch)..]; frames = Capacity; }
            var overflow = _count + frames - Capacity;
            if (overflow > 0) { _head = (_head + overflow) % Capacity; _count -= overflow; FramesOverwritten += overflow; }

            var tail = (_head + _count) % Capacity;
            var firstFrames = Math.Min(frames, Capacity - tail);
            interleaved[..(firstFrames * Ch)].CopyTo(_buf.AsSpan(tail * Ch));
            interleaved[(firstFrames * Ch)..].CopyTo(_buf.AsSpan(0));
            _count += frames;
        }
    }

    /// <summary>Reads up to dest.Length/2 frames; returns frames actually read.</summary>
    public int Read(Span<float> dest)
    {
        lock (_gate)
        {
            var frames = Math.Min(dest.Length / Ch, _count);
            var firstFrames = Math.Min(frames, Capacity - _head);
            _buf.AsSpan(_head * Ch, firstFrames * Ch).CopyTo(dest);
            _buf.AsSpan(0, (frames - firstFrames) * Ch).CopyTo(dest[(firstFrames * Ch)..]);
            _head = (_head + frames) % Capacity;
            _count -= frames;
            return frames;
        }
    }

    public void Clear() { lock (_gate) { _head = 0; _count = 0; } }
}
