namespace AirplayFox.Protocol.Rtsp;

/// <summary>
/// RTSP-level session state on top of a connected RtspConnection: CSeq
/// counter, session id, and the standard header set pyatv sends on every
/// exchange. Mirrors pyatv's support/rtsp.py RtspSession.
/// </summary>
public sealed class RtspSession
{
    private const string UserAgent = "AirPlay/550.10";

    public RtspConnection Connection { get; }
    public int SessionId { get; } = Random.Shared.Next();
    public string DacpId { get; } = Random.Shared.NextInt64().ToString("X");
    public uint ActiveRemote { get; } = (uint)Random.Shared.Next();
    private int _cseq;
    // One request in flight at a time: the keep-alive loop, volume changes from the UI and the
    // tuner's pings all share this connection, and its framing is strictly request/response.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RtspSession(RtspConnection connection) => Connection = connection;

    private string Uri => $"rtsp://{Connection.LocalIp}/{SessionId}";

    public async Task<RtspResponse> Exchange(
        string method,
        string? uri = null,
        string? contentType = null,
        IDictionary<string, string>? headers = null,
        byte[]? body = null,
        string protocol = "RTSP/1.0")
    {
        await _gate.WaitAsync();
        try
        {
        var cseq = _cseq++;
        var hdrs = new Dictionary<string, string>
        {
            ["CSeq"] = cseq.ToString(),
            ["DACP-ID"] = DacpId,
            ["Active-Remote"] = ActiveRemote.ToString(),
            ["Client-Instance"] = DacpId,
        };
        if (headers != null)
            foreach (var (k, v) in headers) hdrs[k] = v;

        return await Connection.SendAsync(method, uri ?? Uri, protocol, UserAgent, contentType, hdrs, body);
        }
        finally { _gate.Release(); }
    }

    public Task<RtspResponse> Setup(IDictionary<string, object> body) =>
        Exchange("SETUP", body: Plist.Encode(body), contentType: "application/x-apple-binary-plist");

    public Task<RtspResponse> Record() => Exchange("RECORD");

    /// <summary>Keep-alive ping HomePod expects during an active RAOP session
    /// ("Start keep-alive task to ensure connection is not closed by remote
    /// device" - pyatv's stream_client.py, sent every 2s via its
    /// AirPlayV2._feedback_task_loop). Missing entirely was traced to
    /// HomePod silently stopping playback ~30-45s into every live-streaming
    /// session while packets kept flowing normally at the network level.</summary>
    public Task<RtspResponse> Feedback() => Exchange("POST", uri: "/feedback");

    public Task<RtspResponse> Flush(int rtspSessionId, int rtpSeq, long rtpTime) =>
        Exchange("FLUSH", headers: new Dictionary<string, string>
        {
            ["Range"] = "npt=0-",
            ["Session"] = rtspSessionId.ToString(),
            ["RTP-Info"] = $"seq={rtpSeq};rtptime={rtpTime}",
        });

    public Task<RtspResponse> SetParameter(string parameter, string value) =>
        Exchange("SET_PARAMETER", contentType: "text/parameters",
            body: System.Text.Encoding.UTF8.GetBytes($"{parameter}: {value}"));

    public Task<RtspResponse> Teardown() =>
        Exchange("TEARDOWN", headers: new Dictionary<string, string> { ["Session"] = SessionId.ToString() });
}
