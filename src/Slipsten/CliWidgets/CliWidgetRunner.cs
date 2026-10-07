using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Slipsten.CliWidgets;

public partial class CliWidgetRunner : IDisposable
{
    private readonly CliWidgetDefinition _definition;
    private readonly Dictionary<string, string> _variables;
    private readonly Timer? _timer;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private bool _disposed;

    public event EventHandler? StateChanged;

    public string WidgetId => _definition.Id;
    public int BadgeCount { get; private set; }
    public bool HasError { get; private set; }
    public string? LastError { get; private set; }

    public CliWidgetRunner(CliWidgetDefinition definition, Dictionary<string, string> variables)
    {
        _definition = definition;
        _variables = variables;

        if (_definition.Badge != null && _definition.RefreshSeconds > 0)
        {
            _timer = new Timer(
                async _ => await RefreshAsync(),
                null,
                Timeout.Infinite,
                Timeout.Infinite);
        }
    }

    public void Start()
    {
        if (_timer != null && _definition.RefreshSeconds > 0)
        {
            _timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(_definition.RefreshSeconds));
        }
    }

    public void Stop()
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    public async Task RefreshAsync()
    {
        if (_definition.Badge == null)
            return;

        if (!await _refreshLock.WaitAsync(0))
            return;

        try
        {
            var command = GetCommand(_definition.Badge.Command, "badge");
            var output = await RunCommandAsync(command);
            var count = ParseBadgeCount(output, _definition.Badge);

            var changed = count != BadgeCount;
            BadgeCount = count;
            HasError = false;
            LastError = null;

            if (changed)
                StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            var changed = HasError == false || BadgeCount != 0;
            HasError = true;
            LastError = ex.Message;
            BadgeCount = 0;

            if (changed)
                StateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task<IReadOnlyList<FlyoutItem>> GetFlyoutItemsAsync()
    {
        if (_definition.Flyout == null)
            return Array.Empty<FlyoutItem>();

        var displayTemplate = _definition.Flyout.DisplayTemplate?.Trim() ?? string.Empty;
        var urlTemplate = _definition.Flyout.UrlTemplate?.Trim() ?? string.Empty;
        var groupByTemplate = _definition.Flyout.GroupBy?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(displayTemplate) || string.IsNullOrWhiteSpace(urlTemplate))
        {
            var usedLegacyKeys = !string.IsNullOrWhiteSpace(_definition.Flyout.ItemFormat) ||
                                 !string.IsNullOrWhiteSpace(_definition.Flyout.ItemUrl);
            var migrationHint = usedLegacyKeys
                ? "Legacy keys 'itemFormat' and 'itemUrl' are no longer supported. Use 'displayTemplate' and 'urlTemplate'."
                : "Set both 'displayTemplate' and 'urlTemplate' on this widget flyout.";

            throw new InvalidOperationException($"Widget '{WidgetId}' has invalid flyout template configuration. {migrationHint}");
        }

        var command = GetCommand(_definition.Flyout.Command, "flyout");
        var output = await RunCommandAsync(command);
        return ParseFlyoutItems(output, displayTemplate, urlTemplate, groupByTemplate);
    }

    private string GetCommand(string sectionCommand, string sectionName)
    {
        var command = string.IsNullOrWhiteSpace(sectionCommand)
            ? _definition.Command
            : sectionCommand;

        if (string.IsNullOrWhiteSpace(command))
        {
            throw new InvalidOperationException(
                $"Widget '{WidgetId}' has no {sectionName} command. " +
                "Set 'command' on the widget or on the specific section.");
        }

        return SubstituteVariables(command);
    }

    private async Task<string> RunCommandAsync(string command)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c {command}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null)
            throw new InvalidOperationException("Failed to start process");

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            throw new TimeoutException($"Command timed out after 10 seconds: {command}");
        }

        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Command failed with exit code {process.ExitCode}: {error}");
        }

        return output;
    }

    private int ParseBadgeCount(string jsonOutput, BadgeDefinition badge)
    {
        if (string.IsNullOrWhiteSpace(jsonOutput))
            return 0;

        var source = GetBadgeSource(badge);
        var element = JsonDocument.Parse(jsonOutput).RootElement;

        if (source == "array.length")
        {
            if (element.ValueKind == JsonValueKind.Array)
                return element.GetArrayLength();
            return 0;
        }
        else if (source == "value")
        {
            if (element.ValueKind == JsonValueKind.Number)
                return Math.Max(0, element.GetInt32());
            return 0;
        }

        return 0;
    }

    private static string GetBadgeSource(BadgeDefinition badge)
    {
        var text = badge.Text ?? string.Empty;

        if (text.Contains("{array.length}", StringComparison.Ordinal))
            return "array.length";

        if (text.Contains("{value}", StringComparison.Ordinal))
            return "value";

        return badge.Source;
    }

    private List<FlyoutItem> ParseFlyoutItems(
        string jsonOutput,
        string itemFormat,
        string itemUrl,
        string groupByTemplate)
    {
        if (string.IsNullOrWhiteSpace(jsonOutput))
            return [];

        var element = JsonDocument.Parse(jsonOutput).RootElement;
        if (element.ValueKind != JsonValueKind.Array)
            return [];

        var items = new List<FlyoutItem>();
        foreach (var item in element.EnumerateArray())
        {
            var displayText = ResolveTemplate(itemFormat, item);
            var url = string.IsNullOrWhiteSpace(itemUrl) ? null : ResolveTemplate(itemUrl, item);
            var group = string.IsNullOrWhiteSpace(groupByTemplate)
                ? null
                : ResolveTemplate(groupByTemplate, item);
            items.Add(new FlyoutItem(displayText, url, group));
        }

        return items;
    }

    private string ResolveTemplate(string template, JsonElement data)
    {
        return TokenRegex().Replace(template, match =>
        {
            var propertyPath = match.Groups[1].Value;
            var value = GetNestedProperty(data, propertyPath);
            return value ?? match.Value;
        });
    }

    private string? GetNestedProperty(JsonElement element, string path)
    {
        var parts = path.Split('.');
        var current = element;

        foreach (var part in parts)
        {
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(part, out var child))
            {
                current = child;
            }
            else
            {
                return null;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => current.ToString()
        };
    }

    private string SubstituteVariables(string command)
    {
        return TokenRegex().Replace(command, match =>
        {
            var varName = match.Groups[1].Value;
            return _variables.TryGetValue(varName, out var value) ? value : match.Value;
        });
    }

    [GeneratedRegex(@"\{([^}]+)\}")]
    private static partial Regex TokenRegex();

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _timer?.Dispose();
        _refreshLock.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort to avoid orphaned command processes after timeout.
        }
    }
}

public record FlyoutItem(string DisplayText, string? Url, string? Group);
