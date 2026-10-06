namespace AirplayFox.Protocol;

/// <summary>NTP time helpers matching pyatv's protocols/raop/timing.py.</summary>
public static class Ntp
{
    private const long NtpEpochOffset = 0x83AA7E80; // seconds between 1900-01-01 and 1970-01-01

    public static ulong Now()
    {
        var nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
        var seconds = nowUs / 1_000_000;
        var frac = nowUs - seconds * 1_000_000;
        return (ulong)(seconds + NtpEpochOffset) << 32 | (ulong)((frac << 32) / 1_000_000);
    }

    public static (uint Seconds, uint Frac) ToParts(ulong ntp) => ((uint)(ntp >> 32), (uint)(ntp & 0xFFFFFFFF));

    /// <summary>Converts NTP time to a sample-rate timestamp (protocols/raop/timing.py's ntp2ts).
    /// Stays in ulong until the final shift-down to avoid overflowing long on the
    /// intermediate (ntp>>16)*rate product, which briefly approaches 2^64.</summary>
    public static long ToTimestamp(ulong ntp, int rate) => (long)(((ntp >> 16) * (ulong)rate) >> 16);

    /// <summary>Converts a sample-rate timestamp to NTP time (ts2ntp). Same overflow caveat as above.</summary>
    public static ulong FromTimestamp(long timestamp, int rate) =>
        ((ulong)timestamp << 16) / (ulong)rate << 16;
}
