using System.Text.Json;

namespace Slipsten.CliWidgets;

public class CliWidgetHost : IDisposable
{
    private readonly List<CliWidgetRunner> _runners = [];
    private readonly CliWidgetsConfig _config;
    private bool _disposed;

    public event EventHandler<string>? WidgetStateChanged;

    public IReadOnlyList<CliWidgetDefinition> Widgets => _config.CliWidgets;

    private CliWidgetHost(CliWidgetsConfig config)
    {
        _config = config;

        foreach (var widget in config.CliWidgets)
        {
            var runner = new CliWidgetRunner(widget, config.Variables);
            runner.StateChanged += (_, _) => WidgetStateChanged?.Invoke(this, widget.Id);
            _runners.Add(runner);
        }
    }

    public static CliWidgetHost LoadFrom(string configPath)
    {
        var config = LoadConfig(configPath);
        return new CliWidgetHost(config);
    }

    private static CliWidgetsConfig LoadConfig(string configPath)
    {
        if (!File.Exists(configPath))
        {
            // Copy default config from bundle
            var defaultConfigPath = Path.Combine(AppContext.BaseDirectory, "widgets.json");
            if (File.Exists(defaultConfigPath))
            {
                var directory = Path.GetDirectoryName(configPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                File.Copy(defaultConfigPath, configPath);
            }
            else
            {
                // Return empty config if no default exists
                return new CliWidgetsConfig();
            }
        }

        var json = File.ReadAllText(configPath);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        return JsonSerializer.Deserialize<CliWidgetsConfig>(json, options) ?? new CliWidgetsConfig();
    }

    public CliWidgetRunner? GetRunner(string widgetId)
    {
        return _runners.FirstOrDefault(r => r.WidgetId == widgetId);
    }

    public void StartAll()
    {
        foreach (var runner in _runners)
            runner.Start();
    }

    public void StopAll()
    {
        foreach (var runner in _runners)
            runner.Stop();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        StopAll();

        foreach (var runner in _runners)
            runner.Dispose();

        _runners.Clear();
        GC.SuppressFinalize(this);
    }
}
