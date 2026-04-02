using System.IO;
using System.Text.Json;

namespace Slipsten.Data;

public class GrindstoneImporter
{
    public static async Task<List<TimeEntry>> ImportFromBackup(string backupPath)
    {
        var entries = new List<TimeEntry>();
        
        if (string.IsNullOrWhiteSpace(backupPath))
            throw new ArgumentException("Backup path cannot be empty");
        
        // Normalize path - trim quotes and whitespace
        backupPath = backupPath.Trim().Trim('"').Trim();
        
        // Check if path exists as-is first
        bool pathExists = Directory.Exists(backupPath) || File.Exists(backupPath);
        
        if (!pathExists)
        {
            throw new DirectoryNotFoundException(
                $"Path does not exist: {backupPath}\n\n" +
                $"Please check:\n" +
                $"1. The path is correct\n" +
                $"2. The path is accessible\n" +
                $"3. If copying from file explorer, paste the full path");
        }
        
        string jsonFile;
        
        // If it's a file path, use it directly
        if (File.Exists(backupPath) && (backupPath.EndsWith(".gsjdb") || backupPath.EndsWith(".json")))
        {
            jsonFile = backupPath;
        }
        else if (Directory.Exists(backupPath))
        {
            // Find latest .gsjdb file in the directory
            var files = Directory.GetFiles(backupPath, "*.gsjdb");
            if (files.Length == 0)
                throw new FileNotFoundException($"No .gsjdb file found in: {backupPath}");
            
            // Get the most recent backup
            jsonFile = files.OrderByDescending(f => f).First();
        }
        else
        {
            throw new FileNotFoundException($"Invalid path: {backupPath}");
        }

        try
        {
            var json = await File.ReadAllTextAsync(jsonFile);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("f", out var f))
                throw new InvalidOperationException("Invalid gsjdb format: missing 'f' property");

            // Build task lookup for WorkType mapping (o: "Dubbel" → DoublePay)
            var taskTypes = new Dictionary<string, WorkType>();
            if (f.TryGetProperty("t", out var tasks) && tasks.ValueKind == JsonValueKind.Array)
            {
                foreach (var task in tasks.EnumerateArray())
                {
                    var id = task.TryGetProperty("i", out var idEl) ? idEl.GetString() : null;
                    if (id == null) continue;

                    var workType = WorkType.Ordinary;
                    if (task.TryGetProperty("o", out var oEl) &&
                        string.Equals(oEl.GetString(), "Dubbel", StringComparison.OrdinalIgnoreCase))
                    {
                        workType = WorkType.DoublePay;
                    }
                    taskTypes[id] = workType;
                }
            }

            // Import all time records from f.r
            if (f.TryGetProperty("r", out var records) && records.ValueKind == JsonValueKind.Array)
            {
                foreach (var record in records.EnumerateArray())
                {
                    if (!record.TryGetProperty("s", out var sEl) ||
                        !record.TryGetProperty("e", out var eEl))
                        continue;

                    if (!DateTime.TryParse(sEl.GetString(), null,
                            System.Globalization.DateTimeStyles.RoundtripKind, out var startTime) ||
                        !DateTime.TryParse(eEl.GetString(), null,
                            System.Globalization.DateTimeStyles.RoundtripKind, out var endTime))
                        continue;

                    if (endTime <= startTime) continue;

                    var taskId = record.TryGetProperty("t", out var tEl) ? tEl.GetString() : null;
                    var workType = taskId != null && taskTypes.TryGetValue(taskId, out var wt)
                        ? wt
                        : WorkType.Ordinary;

                    entries.Add(new TimeEntry
                    {
                        StartTime = startTime.ToLocalTime(),
                        EndTime = endTime.ToLocalTime(),
                        Type = workType
                    });
                }
            }

            if (entries.Count == 0)
                throw new InvalidOperationException("No time entries found in the backup file");

            return entries;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to parse backup file as JSON: {ex.Message}", ex);
        }
    }

    public static async Task SaveEntries(SlipstenContext context, List<TimeEntry> entries)
    {
        foreach (var entry in entries)
        {
            context.TimeEntries.Add(entry);
        }
        await context.SaveChangesAsync();
    }
}
