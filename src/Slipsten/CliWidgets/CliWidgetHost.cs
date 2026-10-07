using System.Text.Json;
using Slipsten.Data;

namespace Slipsten.CliWidgets;

public class CliWidgetHost : IDisposable
{
    private readonly object _sync = new();
    private readonly string _configPath;
    private readonly List<CliWidgetRunner> _runners = [];
    private readonly FileSystemWatcher _configWatcher;
    private readonly Timer _reloadTimer;
    private CliWidgetsConfig _config;
    private bool _isStarted;
    private bool _disposed;

    public event EventHandler<string>? WidgetStateChanged;
    public event EventHandler? ConfigurationReloaded;

    public IReadOnlyList<CliWidgetDefinition> Widgets
    {
        get
        {
            lock (_sync)
                return _config.CliWidgets.ToArray();
        }
    }

    private CliWidgetHost(string configPath, CliWidgetsConfig config)
    {
        _configPath = Path.GetFullPath(configPath);
        _config = config;
        _runners.AddRange(CreateRunners(config));

        var directory = Path.GetDirectoryName(_configPath) ?? AppContext.BaseDirectory;
        _reloadTimer = new Timer(_ => ReloadConfiguration(), null, Timeout.Infinite, Timeout.Infinite);
        _configWatcher = new FileSystemWatcher(directory, Path.GetFileName(_configPath))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _configWatcher.Changed += OnConfigFileChanged;
        _configWatcher.Created += OnConfigFileChanged;
        _configWatcher.Deleted += OnConfigFileChanged;
        _configWatcher.Renamed += OnConfigFileRenamed;
    }

    public static CliWidgetHost LoadFrom(string configPath)
    {
        var config = LoadConfig(configPath);
        return new CliWidgetHost(configPath, config);
    }

    public static CliWidgetHost CreatePreview(CliWidgetsConfig config) =>
        new(Path.Combine(Path.GetTempPath(), $"Slipsten-preview-{Guid.NewGuid():N}.json"), config);

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
        lock (_sync)
            return _runners.FirstOrDefault(r => r.WidgetId == widgetId);
    }

    public void StartAll()
    {
        List<CliWidgetRunner> runners;
        lock (_sync)
        {
            if (_disposed)
                return;

            _isStarted = true;
            runners = [.. _runners];
        }

        foreach (var runner in runners)
            runner.Start();
    }

    public void StopAll()
    {
        List<CliWidgetRunner> runners;
        lock (_sync)
        {
            _isStarted = false;
            runners = [.. _runners];
        }

        foreach (var runner in runners)
            runner.Stop();
    }

    private List<CliWidgetRunner> CreateRunners(CliWidgetsConfig config)
    {
        var runners = new List<CliWidgetRunner>();
        foreach (var widget in config.CliWidgets)
        {
            var runner = new CliWidgetRunner(widget, config.Variables);
            runner.StateChanged += OnRunnerStateChanged;
            runners.Add(runner);
        }

        return runners;
    }

    private void OnRunnerStateChanged(object? sender, EventArgs e)
    {
        if (sender is CliWidgetRunner runner)
            WidgetStateChanged?.Invoke(this, runner.WidgetId);
    }

    private void OnConfigFileChanged(object sender, FileSystemEventArgs e) => ScheduleReload();

    private void OnConfigFileRenamed(object sender, RenamedEventArgs e) => ScheduleReload();

    private void ScheduleReload()
    {
        lock (_sync)
        {
            if (!_disposed)
                _reloadTimer.Change(TimeSpan.FromMilliseconds(350), Timeout.InfiniteTimeSpan);
        }
    }

    private void ReloadConfiguration()
    {
        try
        {
            var config = LoadConfig(_configPath);
            var newRunners = CreateRunners(config);
            List<CliWidgetRunner> oldRunners;
            bool shouldStart;

            lock (_sync)
            {
                if (_disposed)
                {
                    foreach (var runner in newRunners)
                        runner.Dispose();

                    return;
                }

                oldRunners = [.. _runners];
                _runners.Clear();
                _runners.AddRange(newRunners);
                _config = config;
                shouldStart = _isStarted;
            }

            if (shouldStart)
            {
                foreach (var runner in newRunners)
                    runner.Start();
            }

            foreach (var runner in oldRunners)
                runner.Dispose();

            ConfigurationReloaded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            CrashLogger.Log("CliWidgetHost.ReloadConfiguration", ex);
        }
    }

    public void Dispose()
    {
        List<CliWidgetRunner> runners;
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            runners = [.. _runners];
            _runners.Clear();
        }

        _configWatcher.Dispose();
        _reloadTimer.Dispose();

        foreach (var runner in runners)
        {
            runner.Stop();
            runner.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
