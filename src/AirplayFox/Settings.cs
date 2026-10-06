using System.Text.Json;

namespace AirplayFox;

/// <summary>Persisted user choices (%APPDATA%\AirplayFox\settings.json).</summary>
public sealed class Settings
{
    public string? LastDevice { get; set; }
    /// <summary>Receiver playout latency in seconds (0.00 - 4.00); null = Auto (measure the network at connect).</summary>
    public double? LatencySeconds { get; set; }
    public const double MaxLatencySeconds = 4.0;
    public double VolumePercent { get; set; } = 33;
    /// <summary>Mute this PC's own speakers while streaming (avoids hearing the delayed echo). Off by default:
    /// on some audio drivers muting also silences the loopback tap.</summary>
    public bool MuteLocal { get; set; }
    /// <summary>Test/CLI override: use exactly this receiver latency instead of the preset.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public int? FixedLatencyMs { get; set; }

    private static string PathOnDisk => System.IO.Path.Combine(Log.Dir, "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(PathOnDisk)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try { File.WriteAllText(PathOnDisk, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { Log.Warn($"could not save settings: {ex.Message}"); }
    }

    /// <summary>(receiver latency ms or null = tune, standing-fill ms). The standing fill is the small cushion the
    /// pump keeps against bursty capture; it shrinks with the latency so low settings stay low (total delay is about
    /// latency + fill).</summary>
    public (int? LatencyMs, int FillMs) Resolve()
    {
        if (FixedLatencyMs is { } fixedMs) return (fixedMs, FillFor(fixedMs));
        if (LatencySeconds is { } sec)
        {
            var ms = (int)Math.Round(Math.Clamp(sec, 0, MaxLatencySeconds) * 1000);
            return (ms, FillFor(ms));
        }
        return (null, 100);
    }

    public static int FillFor(int latencyMs) => Math.Clamp(latencyMs / 4, 16, 100);
}
