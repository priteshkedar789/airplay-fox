using AirplayFox.Protocol;
using NAudio.Wave;

namespace AirplayFox;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--cli")) return Cli(args).GetAwaiter().GetResult();

        using var mutex = new Mutex(true, "AirplayFox.SingleInstance", out var first);
        if (!first) return 0;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
        return 0;
    }

    /// <summary>Headless run for testing: AirplayFox.exe --cli [--device Bedroom] [--latency auto|SECONDS e.g. 0.25]
    /// [--seconds 30] [--dump sent.wav]  (--dump records exactly the audio sent, for glitch analysis).</summary>
    static async Task<int> Cli(string[] args)
    {
        Log.Echo = true;
        string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

        var settings = new Settings();
        var lat = Arg("--latency") ?? "auto";
        if (double.TryParse(lat, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sec))
            settings.FixedLatencyMs = (int)Math.Round(sec * 1000);
        var name = Arg("--device") ?? "Bedroom";
        var seconds = int.Parse(Arg("--seconds") ?? "30");
        var dump = Arg("--dump");

        using var discovery = new Discovery();
        AirplayDevice? device = null;
        discovery.DeviceFound += d => { if (d.Name.Contains(name, StringComparison.OrdinalIgnoreCase)) device = d; };
        discovery.Start();
        for (var i = 0; i < 100 && device is null; i++) await Task.Delay(100);
        if (device is null) { Log.Warn($"no speaker matching '{name}'"); return 1; }

        MemoryStream? recorded = dump != null ? new MemoryStream() : null;
        using var sup = new Supervisor(settings);
        if (recorded != null) sup.Tap = pkt => { lock (recorded) recorded.Write(pkt, 0, pkt.Length); };
        sup.Start(device);
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        var stats = sup.Stats?.ToString() ?? "(no stats)";
        var control = sup.LastControl;
        sup.Stop();
        Log.Info($"final: state-at-end pump: {stats}");
        if (control != null) Log.Info($"retransmit: requests={control.RetransmitRequests} packets={control.RetransmitPacketsRequested} sent={control.RetransmitPacketsSent} missed={control.RetransmitPacketsMissed}");

        if (recorded != null)
        {
            using var w = new WaveFileWriter(dump!, new WaveFormat(44100, 16, 2));
            lock (recorded) w.Write(recorded.ToArray(), 0, (int)recorded.Length);
            Log.Info($"wrote {dump}");
        }
        return 0;
    }
}
