using AirplayFox.Protocol;
using AirplayFox.Protocol.Rtsp;

namespace AirplayFox;

/// <param name="LatencyMs">Receiver playout delay (RaopAudioContext.Latency): the main lag knob.</param>
/// <param name="InitialVolumePercent">Set before audio starts, like pyatv (33). Null leaves the speaker alone.</param>
/// <param name="KeepAlive">False only for short probes that drive RTSP themselves.</param>
public sealed record SessionOptions(int LatencyMs, double? InitialVolumePercent = 33.0, bool KeepAlive = true);

/// <summary>
/// One AirPlay 2 / RAOP session to one speaker: connect, HAP transient pairing, RTSP SETUP/RECORD, keep-alive.
/// Wire behaviour mirrors pyatv (and was validated against a HomePod); only the lifecycle is new: a
/// <see cref="Died"/> event so a supervisor can reconnect, and serialised RTSP so UI volume changes can't
/// collide with the keep-alive.
/// </summary>
public sealed class AirplaySession : IDisposable
{
    private const string ControlSalt = "Control-Salt";
    private const string ControlWriteInfo = "Control-Write-Encryption-Key";
    private const string ControlReadInfo = "Control-Read-Encryption-Key";
    private const string EventsSalt = "Events-Salt";
    private const string EventsWriteInfo = "Events-Write-Encryption-Key";
    private const string EventsReadInfo = "Events-Read-Encryption-Key";

    private static readonly TimeSpan FeedbackInterval = TimeSpan.FromSeconds(2);
    private const int FeedbackStrikes = 3; // consecutive failures before the session counts as dead

    private readonly RtspConnection _connection = new();
    private RtspSession? _rtsp;
    private HapTransientPairing? _pairing;
    private RaopTimingServer? _timingServer;
    private EventChannel? _eventChannel;
    private RaopAudioContext? _context;
    private CancellationTokenSource? _feedbackCts;
    private int _died;

    public RaopControlClient? ControlClient { get; private set; }
    public RaopAudioSender? Sender { get; private set; }
    public string RemoteIp => _connection.RemoteIp;

    /// <summary>Raised once when the speaker stops answering (reason in the argument).</summary>
    public event Action<string>? Died;

    public async Task<RaopAudioSender> StartAsync(AirplayDevice device, SessionOptions options)
    {
        var latencyFrames = RaopAudioContext.FramesFromMs(options.LatencyMs);

        await _connection.ConnectAsync(device.Address.ToString(), device.Port);
        _rtsp = new RtspSession(_connection);
        _pairing = new HapTransientPairing(_connection);
        await _pairing.PairAsync();
        var (outKey, inKey) = _pairing.EncryptionKeys(ControlSalt, ControlWriteInfo, ControlReadInfo);
        _connection.Session.Enable(outKey, inKey);

        _timingServer = await RaopTimingServer.StartAsync(_connection.LocalIp);
        var eventPort = await SetupBaseAsync(_timingServer.Port);

        // The event channel's key pair is deliberately swapped vs the control channel (confirmed against pyatv's
        // airplayv2.py), and its messages must each be answered with 200 OK or the speaker drops the session.
        var (eventsOutKey, eventsInKey) = _pairing.EncryptionKeys(EventsSalt, EventsReadInfo, EventsWriteInfo);
        _eventChannel = await EventChannel.ConnectAsync(RemoteIp, eventPort, eventsOutKey, eventsInKey);

        ControlClient = new RaopControlClient(_connection.LocalIp);
        var (sharedSecret, dataPort, controlPort) = await SetupAudioStreamAsync(ControlClient.Port, latencyFrames);

        _context = new RaopAudioContext { Ssrc = (uint)_rtsp.SessionId };
        _context.Reset(latencyFrames);
        Sender = new RaopAudioSender(RemoteIp, dataPort, _context, sharedSecret);
        ControlClient.Start(RemoteIp, controlPort, _context, Sender);

        await _rtsp.Record();
        await _rtsp.Flush(_rtsp.SessionId, _context.RtpSeq, _context.RtpTime);

        if (options.InitialVolumePercent is { } vol) await SetVolumeAsync(vol);
        if (options.KeepAlive) StartFeedbackLoop();
        return Sender;
    }

    private void StartFeedbackLoop()
    {
        _feedbackCts = new CancellationTokenSource();
        var token = _feedbackCts.Token;
        var rtsp = _rtsp!;
        _ = Task.Run(async () =>
        {
            var strikes = 0;
            while (!token.IsCancellationRequested)
            {
                try { await rtsp.Feedback(); strikes = 0; }
                catch (Exception ex)
                {
                    Log.Warn($"feedback failed ({++strikes}/{FeedbackStrikes}): {ex.Message}");
                    if (strikes >= FeedbackStrikes) { RaiseDied(ex.Message); return; }
                }
                try { await Task.Delay(FeedbackInterval, token); }
                catch (TaskCanceledException) { return; }
            }
        }, token);
    }

    /// <summary>Called by the owner when sending throws, so a dead socket is noticed in ~ms rather than after 3 missed pings.</summary>
    public void RaiseDied(string reason)
    {
        if (Interlocked.Exchange(ref _died, 1) == 0) Died?.Invoke(reason);
    }

    /// <summary>Receiver volume, 0-100 (maps to -30..0 dBFS like pyatv; 0 = mute).</summary>
    public async Task SetVolumeAsync(double percent)
    {
        if (_rtsp is null) throw new InvalidOperationException("not connected");
        var dbfs = percent <= 0.0 ? -144.0 : percent * 30.0 / 100.0 - 30.0;
        await _rtsp.SetParameter("volume", dbfs.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>RTSP round trip in ms; only valid while the keep-alive loop is off (probes).</summary>
    public async Task<double> PingAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await _rtsp!.Feedback();
        return sw.Elapsed.TotalMilliseconds;
    }

    public async Task StopAsync()
    {
        _feedbackCts?.Cancel();
        if (_rtsp != null)
            try { await _rtsp.Teardown().WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* best effort */ }
    }

    private async Task<int> SetupBaseAsync(int timingServerPort)
    {
        var resp = await _rtsp!.Setup(new Dictionary<string, object>
        {
            ["deviceID"] = "AA:BB:CC:DD:EE:FF",
            ["sessionUUID"] = Guid.NewGuid().ToString().ToUpperInvariant(),
            ["timingPort"] = timingServerPort,
            ["timingProtocol"] = "NTP",
            ["isMultiSelectAirPlay"] = true,
            ["groupContainsGroupLeader"] = false,
            ["macAddress"] = "AA:BB:CC:DD:EE:FF",
            ["model"] = "iPhone14,3",
            ["name"] = "AirplayFox",
            ["osBuildVersion"] = "20F66",
            ["osName"] = "iPhone OS",
            ["osVersion"] = "16.5",
            ["senderSupportsRelay"] = false,
            ["sourceVersion"] = "690.7.1",
            ["statsCollectionEnabled"] = false,
        });
        if (resp.Code != 200) throw new InvalidOperationException($"base SETUP failed: {resp.Code} {resp.Message}");
        var body = Plist.Decode(resp.Body);
        return body.TryGetValue("eventPort", out var p) ? Convert.ToInt32(p) : 0;
    }

    private async Task<(byte[] SharedSecret, int DataPort, int ControlPort)> SetupAudioStreamAsync(int localControlPort, int latencyFrames)
    {
        // The key only has to match what we send in "shk"; deriving it from the session keys keeps it fresh per session.
        var (sharedSecret, _) = _pairing!.EncryptionKeys(EventsSalt, EventsWriteInfo, EventsReadInfo);

        var resp = await _rtsp!.Setup(new Dictionary<string, object>
        {
            ["streams"] = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["audioFormat"] = 0x800, // PCM 44.1k/16/2
                    ["audioMode"] = "default",
                    ["controlPort"] = localControlPort,
                    ["ct"] = 1,
                    ["isMedia"] = true,
                    // We stamp RTP times with this delay, so advertise exactly it; keep the max generous.
                    ["latencyMax"] = Math.Max(latencyFrames, 88200),
                    ["latencyMin"] = latencyFrames,
                    ["shk"] = sharedSecret,
                    ["spf"] = RaopAudioContext.FramesPerPacket,
                    ["sr"] = RaopAudioContext.SampleRate,
                    ["type"] = 0x60,
                    ["supportsDynamicStreamID"] = false,
                    ["streamConnectionID"] = (long)_rtsp.SessionId,
                },
            },
        });
        if (resp.Code != 200) throw new InvalidOperationException($"audio SETUP failed: {resp.Code} {resp.Message}");

        var stream = (Dictionary<string, object>)((List<object>)Plist.Decode(resp.Body)["streams"])[0];
        return (sharedSecret, Convert.ToInt32(stream["dataPort"]), Convert.ToInt32(stream["controlPort"]));
    }

    public void Dispose()
    {
        _feedbackCts?.Cancel();
        _feedbackCts?.Dispose();
        Sender?.Dispose();
        ControlClient?.Dispose();
        _eventChannel?.Dispose();
        _timingServer?.Dispose();
        _connection.Dispose();
    }
}
