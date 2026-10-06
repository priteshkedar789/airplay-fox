using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using AirplayFox.Protocol;

namespace AirplayFox.Audio;

/// <summary>
/// WASAPI loopback of the default output device, delivered as 44.1 kHz stereo float into a <see cref="PcmRing"/>.
///
/// Polls the capture client directly from one thread (NAudio's event-driven WasapiLoopbackCapture was seen to go
/// silent after ~40 s). Whole chunks only: the resampler is fed from a non-padding buffer and is read only when
/// enough real samples exist for a full chunk, so a late WASAPI delivery never injects zeros. When Windows
/// switches the default output device the session restarts on the new one.
/// </summary>
public sealed class LoopbackCapture : IDisposable
{
    private const int IdleResetMs = 50;        // nothing delivered this long: drop the few ms stuck in the resampler

    [DllImport("avrt.dll", CharSet = CharSet.Unicode)] private static extern IntPtr AvSetMmThreadCharacteristicsW(string task, ref int index);
    [DllImport("avrt.dll")] private static extern bool AvRevertMmThreadCharacteristics(IntPtr h);

    private readonly PcmRing _ring;
    private readonly Thread _thread;
    private volatile bool _stop;

    public long FramesCaptured;
    public int Restarts;
    public string DeviceName { get; private set; } = "";
    /// <summary>Raised when capture cannot run (no output device / unsupported format); it keeps retrying.</summary>
    public event Action<string>? Problem;

    public LoopbackCapture(PcmRing ring)
    {
        _ring = ring;
        _thread = new Thread(Run) { IsBackground = true, Name = "AirplayFox.Capture" };
    }

    public void Start() => _thread.Start();

    private void Run()
    {
        var index = 0;
        var mmcss = AvSetMmThreadCharacteristicsW("Pro Audio", ref index);
        while (!_stop)
        {
            try { Session(); }
            catch (Exception ex) when (!_stop)
            {
                Restarts++;
                Log.Warn($"capture session ended: {ex.Message}");
                Problem?.Invoke(ex.Message);
                Thread.Sleep(500);
            }
        }
        if (mmcss != IntPtr.Zero) AvRevertMmThreadCharacteristics(mmcss);
    }

    private void Session()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        DeviceName = device.FriendlyName;
        var client = device.AudioClient;
        var fmt = client.MixFormat;
        var std = fmt is WaveFormatExtensible ext ? ext.ToStandardWaveFormat() : fmt;
        if (std.Encoding != WaveFormatEncoding.IeeeFloat)
            throw new NotSupportedException($"output mix format {fmt} is not float");

        var srcRate = fmt.SampleRate;
        var srcCh = fmt.Channels;
        client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.Loopback, 2_000_000 /* 200 ms */, 0, fmt, Guid.Empty);
        var capture = client.AudioCaptureClient;
        client.Start();
        Log.Info($"capture started on '{DeviceName}' ({srcRate} Hz, {srcCh} ch)");

        var converter = new RateConverter(srcRate, _ring);
        var lastDataMs = Environment.TickCount64;
        var lastDeviceCheck = Environment.TickCount64;

        try
        {
            while (!_stop)
            {
                var gotData = false;
                while (capture.GetNextPacketSize() > 0)
                {
                    var ptr = capture.GetBuffer(out var frames, out var flags);
                    if (frames > 0)
                    {
                        var stereo = new float[frames * 2];
                        if ((flags & AudioClientBufferFlags.Silent) == 0)
                        {
                            var raw = new float[frames * srcCh];
                            Marshal.Copy(ptr, raw, 0, raw.Length);
                            for (var f = 0; f < frames; f++)
                            {
                                stereo[f * 2] = raw[f * srcCh];
                                stereo[f * 2 + 1] = srcCh > 1 ? raw[f * srcCh + 1] : raw[f * srcCh];
                            }
                        }
                        FramesCaptured += frames;
                        converter.Push(stereo);
                        gotData = true;
                    }
                    capture.ReleaseBuffer(frames);
                }

                var now = Environment.TickCount64;
                if (gotData) lastDataMs = now;
                else if (now - lastDataMs > IdleResetMs) converter.DropPending();

                if (now - lastDeviceCheck > 1000)
                {
                    lastDeviceCheck = now;
                    using var current = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    if (current.ID != device.ID) { Log.Info("default output changed, restarting capture"); return; }
                }

                Thread.Sleep(4);
            }
        }
        finally { try { client.Stop(); } catch { } }
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(2));
    }
}
