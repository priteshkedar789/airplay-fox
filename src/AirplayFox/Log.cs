namespace AirplayFox;

/// <summary>Tiny file logger: %APPDATA%\AirplayFox\airplayfox.log, truncated at start when over 1 MB.
/// Protocol code logs at Debug; the UI's "Open log" shows it.</summary>
public static class Log
{
    private static readonly object Gate = new();
    public static string Dir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AirplayFox");
    public static string FilePath { get; } = Path.Combine(Dir, "airplayfox.log");
    public static bool Echo { get; set; } // also print to console (CLI mode)

    static Log()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1_000_000) File.Delete(FilePath);
        }
        catch { /* logging must never break the app */ }
    }

    public static void Debug(string msg) => Write("DBG", msg);
    public static void Info(string msg) => Write("INF", msg);
    public static void Warn(string msg) => Write("WRN", msg);

    private static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {level} {msg}";
        if (Echo) Console.WriteLine(line);
        lock (Gate)
        {
            try { File.AppendAllText(FilePath, line + Environment.NewLine); } catch { }
        }
    }
}
