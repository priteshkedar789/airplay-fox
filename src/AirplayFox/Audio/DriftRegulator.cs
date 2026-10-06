namespace AirplayFox.Audio;

/// <summary>
/// The PC's audio clock and the speaker's clock never agree exactly (typically 20-100 ppm). Left alone the
/// ring either slowly drains (underruns, clicks) or slowly fills (latency creeps up). Holding the fill level
/// near a target by shedding or repeating a single frame per packet (at most ~2800 ppm of authority, inaudible
/// at that size) keeps both away. A deadband stops it hunting on the normal burst jitter of capture.
/// </summary>
public static class DriftRegulator
{
    /// <returns>+1 = drop one frame (ring too full), -1 = repeat one frame (ring too empty), 0 = leave alone.</returns>
    public static int Adjust(int fillFrames, int targetFrames, int deadbandFrames) =>
        fillFrames > targetFrames + deadbandFrames ? +1
        : fillFrames < targetFrames - deadbandFrames ? -1
        : 0;
}
