using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using AirplayFox.Protocol.Crypto;

namespace AirplayFox.Protocol.Rtsp;

public sealed record RtspResponse(int Code, string Message, IReadOnlyDictionary<string, string> Headers, byte[] Body);

/// <summary>
/// Raw RTSP/HTTP-ish request/response connection matching pyatv's wire format
/// (support/http.py): "METHOD uri PROTOCOL\r\nHeader: value\r\n...\r\n\r\n" +
/// optional body, framed by Content-Length. Requests are sent and awaited
/// strictly sequentially, so no CSeq-keyed dispatch table is needed.
/// Once <see cref="Session"/> is enabled (after pair-setup), all writes/reads
/// are transparently wrapped in HAP 1024-byte-frame encryption.
/// </summary>
public sealed class RtspConnection : IDisposable
{
    private static readonly byte[] HeaderSep = "\r\n\r\n"u8.ToArray();
    private static readonly Regex StatusLineRegex = new(@"^[^/]+/[0-9.]+ ([0-9]+) (.*)$");

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private byte[] _recvBuffer = Array.Empty<byte>();

    public HapSession Session { get; } = new();
    public string LocalIp { get; private set; } = "";
    public string RemoteIp { get; private set; } = "";

    private static string Unmap(System.Net.IPAddress addr) =>
        (addr.IsIPv4MappedToIPv6 ? addr.MapToIPv4() : addr).ToString();

    public async Task ConnectAsync(string host, int port)
    {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(host, port);
        _stream = _tcp.GetStream();
        LocalIp = Unmap(((System.Net.IPEndPoint)_tcp.Client.LocalEndPoint!).Address);
        RemoteIp = Unmap(((System.Net.IPEndPoint)_tcp.Client.RemoteEndPoint!).Address);
    }

    public async Task<RtspResponse> SendAsync(
        string method,
        string uri,
        string protocol = "HTTP/1.1",
        string? userAgent = null,
        string? contentType = null,
        IEnumerable<KeyValuePair<string, string>>? headers = null,
        byte[]? body = null)
    {
        if (_stream is null) throw new InvalidOperationException("not connected");

        var msg = new StringBuilder();
        msg.Append($"{method} {uri} {protocol}");
        if (userAgent != null) msg.Append($"\r\nUser-Agent: {userAgent}");
        if (contentType != null) msg.Append($"\r\nContent-Type: {contentType}");
        if (body is { Length: > 0 }) msg.Append($"\r\nContent-Length: {body.Length}");
        if (headers != null)
            foreach (var (key, value) in headers)
                msg.Append($"\r\n{key}: {value}");
        msg.Append("\r\n\r\n");

        var head = Encoding.UTF8.GetBytes(msg.ToString());
        var output = body is { Length: > 0 } ? head.Concat(body).ToArray() : head;
        var toSend = Session.Encrypt(output);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await _stream.WriteAsync(toSend);
        var response = await ReadResponseAsync();
        sw.Stop();
        if (sw.Elapsed.TotalMilliseconds > 150 || response.Code != 200)
            Log.Debug($"rtsp {method} {uri} -> {response.Code} in {sw.Elapsed.TotalMilliseconds:F0}ms");

        return response;
    }

    private async Task<RtspResponse> ReadResponseAsync()
    {
        while (true)
        {
            var parsed = TryParse();
            if (parsed != null) return parsed;

            var buf = new byte[8192];
            var n = await _stream!.ReadAsync(buf);
            if (n == 0) throw new IOException("connection closed by remote");

            var decrypted = Session.Decrypt(buf[..n]);
            if (decrypted.Length > 0)
                _recvBuffer = _recvBuffer.Concat(decrypted).ToArray();
        }
    }

    private RtspResponse? TryParse()
    {
        var sep = IndexOf(_recvBuffer, HeaderSep);
        if (sep < 0) return null;

        var headerText = Encoding.UTF8.GetString(_recvBuffer, 0, sep);
        var afterHeaders = _recvBuffer[(sep + HeaderSep.Length)..];

        var lines = headerText.Split("\r\n");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var idx = line.IndexOf(':');
            if (idx < 0) continue;
            headers[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }

        var contentLength = headers.TryGetValue("Content-Length", out var cl) ? int.Parse(cl) : 0;
        if (afterHeaders.Length < contentLength) return null; // wait for more data

        var bodyBytes = afterHeaders[..contentLength];
        _recvBuffer = afterHeaders[contentLength..];

        var match = StatusLineRegex.Match(lines[0]);
        var code = match.Success ? int.Parse(match.Groups[1].Value) : 0;
        var message = match.Success ? match.Groups[2].Value : "";

        return new RtspResponse(code, message, headers, bodyBytes);
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        if (haystack.Length < needle.Length) return -1;
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
    }
}
