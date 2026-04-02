using System.IO;

namespace Slipsten.Data;

public static class CrashLogger
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Slipsten", "slipsten.log");

    public static void Log(string context, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{context}] {ex}\n\n";
            File.AppendAllText(LogPath, entry);

            // Keep log file under 1 MB
            var info = new FileInfo(LogPath);
            if (info.Exists && info.Length > 1_000_000)
            {
                var lines = File.ReadAllLines(LogPath);
                File.WriteAllLines(LogPath, lines.Skip(lines.Length / 2));
            }
        }
        catch { /* logging must never crash the app */ }
    }
}
