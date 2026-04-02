using System.IO;
using Microsoft.EntityFrameworkCore;

namespace Slipsten.Data;

public class SlipstenContext : DbContext
{
    public static string ConfiguredPath { get; set; } = string.Empty;

    public DbSet<TimeEntry> TimeEntries => Set<TimeEntry>();
    public DbSet<NotifiedItem> NotifiedItems => Set<NotifiedItem>();

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        string dbPath;
        if (!string.IsNullOrWhiteSpace(ConfiguredPath))
        {
            dbPath = ConfiguredPath;
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        }
        else
        {
            dbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Slipsten", "slipsten.db");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        }
        options.UseSqlite($"Data Source={dbPath}");
    }

    public static string GetDefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Slipsten", "slipsten.db");

    public static string GetResolvedPath() =>
        string.IsNullOrWhiteSpace(ConfiguredPath) ? GetDefaultPath() : ConfiguredPath;
}
