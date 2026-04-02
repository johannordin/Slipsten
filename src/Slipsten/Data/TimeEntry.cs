namespace Slipsten.Data;

public enum WorkType
{
    Ordinary = 0,
    DoublePay = 1
}

public class TimeEntry
{
    public int Id { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string Description { get; set; } = string.Empty;
    public WorkType Type { get; set; }

    public TimeSpan Duration => (EndTime ?? DateTime.Now) - StartTime;
}
