using NAudio.CoreAudioApi;

namespace AirplayFox;

/// <summary>Mutes the PC's default output while streaming so only the speaker is heard. Remembers a marker file so
/// a crash can't leave the PC muted: the next start restores it.</summary>
public static class LocalMute
{
    private static string Marker => Path.Combine(Log.Dir, "pc-muted.flag");

    public static void Engage()
    {
        try
        {
            using var e = new MMDeviceEnumerator();
            using var d = e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (d.AudioEndpointVolume.Mute) return; // already muted by the user: leave it, don't claim it
            d.AudioEndpointVolume.Mute = true;
            File.WriteAllText(Marker, d.ID);
        }
        catch (Exception ex) { Log.Warn($"mute failed: {ex.Message}"); }
    }

    public static void Restore()
    {
        try
        {
            if (!File.Exists(Marker)) return;
            var id = File.ReadAllText(Marker);
            File.Delete(Marker);
            using var e = new MMDeviceEnumerator();
            using var d = e.GetDevice(id);
            d.AudioEndpointVolume.Mute = false;
        }
        catch (Exception ex) { Log.Warn($"unmute failed: {ex.Message}"); }
    }
}
