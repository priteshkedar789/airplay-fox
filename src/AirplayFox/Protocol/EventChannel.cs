using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using AirplayFox.Protocol.Crypto;

namespace AirplayFox.Protocol;

/// <summary>
/// The AirPlay2 "event" side channel: a second TCP connection, HAP-framed
/// with its own key pair, that the receiver expects to exist alongside the
/// main RTSP connection.
///
/// HomePod pushes unsolicited HTTP-style requests down this channel and
/// expects a "200 OK" reply to each one - a liveness/ack contract distinct
/// from the main RTSP connection's periodic /feedback POST. Mirrors pyatv's
/// EventChannel.handle_received (protocols/airplay/channels.py) exactly,
/// including its comment: "Send a positive response to satisfy the other
/// end of the channel". Originally this class only decrypted and discarded
/// incoming bytes; measured live, HomePod would then tear down the entire
/// session (not just this channel) roughly 30-45s in, even with the RTSP
/// connection's own /feedback keepalive succeeding the whole time - i.e.
/// this channel has its own, separate liveness requirement.
/// </summary>
public sealed class EventChannel : IDisposable
{
    private static readonly Regex RequestLine = new(@"^([A-Z_]+) (\S+) (\S+)/(\S+)$", RegexOptions.Compiled);

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly HapSession _session = new();
    private readonly CancellationTokenSource _cts = new();
    private byte[] _buffer = Array.Empty<byte>();

    private EventChannel(TcpClient tcp, byte[] outKey, byte[] inKey)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _session.Enable(outKey, inKey);
        _ = Task.Run(() => DrainLoop(_cts.Token));
    }

    public static async Task<EventChannel> ConnectAsync(string host, int port, byte[] outKey, byte[] inKey, int retries = 5)
    {
        while (true)
        {
            try
            {
                var tcp = new TcpClient();
                await tcp.ConnectAsync(host, port);
                return new EventChannel(tcp, outKey, inKey);
            }
            catch (SocketException) when (retries > 0)
            {
                retries--;
                await Task.Delay(1000);
            }
        }
    }

    private async Task DrainLoop(CancellationToken ct)
    {
        var buf = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var n = await _stream.ReadAsync(buf, ct);
                if (n == 0)
                {
                    Log.Debug("[EventChannel] remote closed the event channel");
                    break;
                }

                var decrypted = _session.Decrypt(buf[..n]);
                if (decrypted.Length == 0) continue;

                var combined = new byte[_buffer.Length + decrypted.Length];
                Buffer.BlockCopy(_buffer, 0, combined, 0, _buffer.Length);
                Buffer.BlockCopy(decrypted, 0, combined, _buffer.Length, decrypted.Length);
                _buffer = combined;

                await HandleReceivedAsync(ct);
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // expected: connection torn down deliberately during our own teardown
        }
        catch (Exception ex)
        {
            // Previously filtered on `!_tcp.Connected`, which is unreliable
            // (.Connected often still reports true right after the remote
            // closes) - a real failure here could die silently inside this
            // fire-and-forget Task.Run with zero trace.
            Log.Debug($"[EventChannel] DrainLoop failed: {ex}");
        }
    }

    /// <summary>Parses as many complete HTTP-style requests as are currently
    /// buffered and replies "200 OK" to each, mirroring pyatv's
    /// EventChannel.handle_received. Leaves any trailing partial message in
    /// _buffer for the next read.</summary>
    private async Task HandleReceivedAsync(CancellationToken ct)
    {
        while (_buffer.Length > 0)
        {
            var separator = IndexOfCrlfCrlf(_buffer);
            if (separator < 0) break; // incomplete message - wait for more data

            var headerBytes = _buffer.AsSpan(0, separator).ToArray();
            var headerText = Encoding.UTF8.GetString(headerBytes);
            var lines = headerText.Split("\r\n");

            var match = RequestLine.Match(lines[0]);
            if (!match.Success)
            {
                Log.Debug($"[EventChannel] unparseable request line: {lines[0]}");
                _buffer = Array.Empty<byte>(); // can't safely resync a malformed stream - drop it
                return;
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 1; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                var colon = lines[i].IndexOf(':');
                if (colon < 0) continue;
                headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
            }

            var contentLength = headers.TryGetValue("Content-Length", out var cl) ? int.Parse(cl) : 0;
            var bodyStart = separator + 4; // past the \r\n\r\n
            if (_buffer.Length - bodyStart < contentLength) break; // body not fully buffered yet

            var consumed = bodyStart + contentLength;
            var protocol = match.Groups[3].Value;
            var version = match.Groups[4].Value;

            var responseHeaders = new List<(string Key, string Value)> { ("Content-Length", "0"), ("Audio-Latency", "0") };
            if (headers.TryGetValue("Server", out var server)) responseHeaders.Add(("Server", server));
            if (headers.TryGetValue("CSeq", out var cseq)) responseHeaders.Add(("CSeq", cseq));

            var response = new StringBuilder();
            response.Append($"{protocol}/{version} 200 OK\r\n");
            if (!headers.ContainsKey("Server")) response.Append("Server: AirplayFox\r\n");
            foreach (var (key, value) in responseHeaders) response.Append($"{key}: {value}\r\n");
            response.Append("\r\n");

            var encrypted = _session.Encrypt(Encoding.UTF8.GetBytes(response.ToString()));
            await _stream.WriteAsync(encrypted, ct);

            _buffer = _buffer[consumed..];
        }
    }

    private static int IndexOfCrlfCrlf(byte[] data)
    {
        for (var i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                return i;
        }
        return -1;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _stream.Dispose();
        _tcp.Dispose();
        _cts.Dispose();
    }
}
