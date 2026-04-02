namespace Slipsten.Data;

public class NotifiedItem
{
    public int Id { get; set; }
    /// <summary>Unique key: e.g. "pr:owner/repo:123" or "workflow:owner/repo:456"</summary>
    public string Key { get; set; } = string.Empty;
    public DateTime NotifiedAt { get; set; }
}
