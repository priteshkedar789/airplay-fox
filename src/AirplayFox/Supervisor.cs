using AirplayFox.Audio;
using AirplayFox.Protocol;

namespace AirplayFox;

public enum LinkState { Idle, Tuning, Connecting, Streaming, Reconnecting }

/// <summary>
/// Owns one speaker connection for its whole life: tune, connect, stream, and when the speaker stops answering
/// (Wi-Fi blip, speaker reboot) tear down, back off and reconnect on its own, re-tuning latency each time.
/// </summary>
public sealed class Supervisor : IDisposable
{
    private readonly Settings _settings;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile AirplaySession? _session;

    public LinkState State { get; private set; } = LinkState.Idle;
    public string Detail { get; private set; } = "";
    public AirplayDevice? Device { get; private set; }
    public PumpStats? Stats { get; private set; }
    public int CurrentLatencyMs { get; private set; }
    /// <summary>Retransmit counters of the most recent session (kept after it ends, for diagnostics).</summary>
    public RaopControlClient? LastControl { get; private set; }
    /// <summary>Set before Start to record every packet sent (tests).</summary>
    public Action<byte[]>? Tap { get; set; }
    public event Action? Changed;

    public Supervisor(Settings settings) => _settings = settings;

    public void Start(AirplayDevice device)
    {
        Stop();
        Device = device;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(device, _cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(6)); } catch { /* cancelled */ }
        _cts?.Dispose();
        _cts = null;
        _loop = null;
        if (State != LinkState.Idle) Set(LinkState.Idle, "");
        Device = null;
    }

    public async Task SetVolumeAsync(double percent)
    {
        var s = _session;
        if (s != null) try { await s.SetVolumeAsync(percent); } catch (Exception ex) { Log.Warn($"volume: {ex.Message}"); }
    }

    private void Set(LinkState state, string detail)
    {
        State = state;
        Detail = detail;
        Log.Info($"state: {state} {detail}");
        Changed?.Invoke();
    }

    private async Task RunAsync(AirplayDevice device, CancellationToken ct)
    {
        var backoffS = 1;
        while (!ct.IsCancellationRequested)
        {
            AirplaySession? session = null;
            LoopbackCapture? capture = null;
            PacketPump? pump = null;
            var muted = false;
            try
            {
                var (fixedMs, fillMs) = _settings.Resolve();
                int latencyMs;
                if (fixedMs is { } f) latencyMs = f;
                else
                {
                    Set(LinkState.Tuning, "measuring the network");
                    latencyMs = await LatencyTuner.TuneOrDefaultAsync(device);
                }
                ct.ThrowIfCancellationRequested();
                CurrentLatencyMs = latencyMs;

                Set(LinkState.Connecting, $"{device.Name}, {latencyMs} ms");
                session = new AirplaySession();
                var sender = await session.StartAsync(device, new SessionOptions(latencyMs, _settings.VolumePercent));
                var died = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                session.Died += reason => died.TrySetResult(reason);
                _session = session;
                LastControl = session.ControlClient;

                var ring = new PcmRing(RaopAudioContext.SampleRate / 2);
                capture = new LoopbackCapture(ring);
                pump = new PacketPump(ring, pcm => sender.SendPacket(pcm), fillMs) { Tap = Tap };
                pump.Failed += ex => session.RaiseDied(ex.Message);
                Stats = pump.Stats;
                capture.Start();
                pump.Start();
                if (_settings.MuteLocal) { LocalMute.Engage(); muted = true; }

                backoffS = 1;
                Set(LinkState.Streaming, $"{device.Name}, {latencyMs} ms");
                var finished = await Task.WhenAny(died.Task, Task.Delay(Timeout.Infinite, ct));
                if (finished == died.Task) Log.Warn($"session died: {await died.Task}");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Warn($"connection failed: {ex.Message}"); }
            finally
            {
                _session = null;
                pump?.Dispose();
                capture?.Dispose();
                if (muted) LocalMute.Restore();
                if (session != null)
                {
                    await session.StopAsync();
                    session.Dispose();
                }
                if (pump != null) Log.Info($"pump: {pump.Stats}");
            }

            if (ct.IsCancellationRequested) break;
            Set(LinkState.Reconnecting, $"retrying in {backoffS}s");
            try { await Task.Delay(TimeSpan.FromSeconds(backoffS), ct); } catch (OperationCanceledException) { break; }
            backoffS = Math.Min(backoffS * 2, 15);
        }
    }

    public void Dispose() => Stop();
}
