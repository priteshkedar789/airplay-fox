using System.Net;
using Makaretu.Dns;

namespace AirplayFox.Protocol;

public sealed record AirplayDevice(string Name, string Host, int Port, IPAddress Address)
{
    // Used as a stable dictionary key / tray menu identity.
    public string Id => $"{Name}|{Address}|{Port}";
}

/// <summary>
/// Bonjour/mDNS discovery of _raop._tcp AirPlay speakers. Mirrors pyatv's
/// raop_service_handler: instance name is "&lt;hex-id&gt;@Device Name._raop._tcp.local.".
/// </summary>
public sealed class Discovery : IDisposable
{
    private const string ServiceType = "_raop._tcp";

    private readonly MulticastService _mdns = new();
    private readonly ServiceDiscovery _sd;
    private readonly Dictionary<string, AirplayDevice> _found = new();
    private readonly System.Threading.Timer _requery;

    public event Action<AirplayDevice>? DeviceFound;

    public IReadOnlyCollection<AirplayDevice> Devices
    {
        get { lock (_found) { return _found.Values.ToList(); } }
    }

    public Discovery()
    {
        _sd = new ServiceDiscovery(_mdns);
        _mdns.AnswerReceived += OnAnswer;
        // mDNS responses to a query only arrive once per burst; re-query
        // periodically to catch devices that power on/join later.
        _requery = new System.Threading.Timer(_ => Query(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        _mdns.Start();
        Query();
        _requery.Change(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
    }

    private void Query() => _sd.QueryServiceInstances(ServiceType);

    private void OnAnswer(object? sender, MessageEventArgs e)
    {
        var records = e.Message.Answers.Concat(e.Message.AdditionalRecords).ToList();

        // A response to our _raop._tcp query commonly bundles SRV/A records
        // for *other* services HomePod advertises on the same host
        // (_companion-link._tcp, _airplay._tcp [video/mirroring, not RAOP
        // audio], _hap._tcp, quic) as additional records in the same
        // packet - a normal mDNS optimization, not something specific to
        // our query. Without this filter every one of those showed up in
        // the Devices menu as if it were a real AirPlay-audio target;
        // selecting one would tear down a working session to "connect" to
        // a service that was never a RAOP endpoint at all.
        const string suffix = "." + ServiceType + ".local";
        foreach (var srv in records.OfType<SRVRecord>())
        {
            if (!srv.Name.ToString().TrimEnd('.').EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                continue;

            var addr = records
                .OfType<ARecord>()
                .FirstOrDefault(a => DomainName.Equals(a.Name, srv.Target))
                ?.Address;
            if (addr is null) continue;

            var display = DisplayName(srv.Name.ToString());
            var device = new AirplayDevice(display, srv.Target.ToString().TrimEnd('.'), srv.Port, addr);

            lock (_found) { _found[device.Id] = device; }
            DeviceFound?.Invoke(device);
        }
    }

    private static string DisplayName(string instanceName)
    {
        var name = instanceName.TrimEnd('.');
        var at = name.IndexOf('@');
        if (at >= 0) name = name[(at + 1)..];
        const string suffix = "._raop._tcp.local";
        if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            name = name[..^suffix.Length];
        return name;
    }

    public void Dispose()
    {
        _requery.Dispose();
        _mdns.Stop();
        _mdns.Dispose();
    }
}
