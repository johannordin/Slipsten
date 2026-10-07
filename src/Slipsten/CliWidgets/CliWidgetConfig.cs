namespace Slipsten.CliWidgets;

public class CliWidgetsConfig
{
    public List<CliWidgetDefinition> CliWidgets { get; set; } = [];
    public Dictionary<string, string> Variables { get; set; } = [];
}

public class CliWidgetDefinition
{
    public string Id { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Tooltip { get; set; } = "";
    public int RefreshSeconds { get; set; } = 60;
    public string Command { get; set; } = "";
    public BadgeDefinition? Badge { get; set; }
    public FlyoutDefinition? Flyout { get; set; }
}

public class BadgeDefinition
{
    // Retained only to provide an explicit migration error for legacy configurations.
    public string Command { get; set; } = "";
    public string Text { get; set; } = "";
    public string Source { get; set; } = "array.length";
    public string Color { get; set; } = "";
    public string TextColor { get; set; } = "";
    public string Position { get; set; } = "bottom-right";
    public string ShowWhen { get; set; } = "> 0";
}

public class FlyoutDefinition
{
    // Retained only to provide an explicit migration error for legacy configurations.
    public string Command { get; set; } = "";
    public string DisplayTemplate { get; set; } = "";
    public string UrlTemplate { get; set; } = "";
    public string GroupBy { get; set; } = "";

    // Legacy keys are retained for explicit error reporting during migration.
    public string ItemFormat { get; set; } = "";
    public string ItemUrl { get; set; } = "";
    public string EmptyMessage { get; set; } = "Nothing to show";
}
