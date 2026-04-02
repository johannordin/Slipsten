using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Slipsten.Data;
using Slipsten.Settings;

namespace Slipsten.AzureDevOps;

public class AzureDevOpsMonitor : IDisposable
{
    private System.Timers.Timer? _timer;
    private AppSettings _settings;
    private bool _isUserActive = true;
    private bool _isDisposed;
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private string? _azCommandPath;

    private readonly object _statusLock = new();
    private List<AzureDevOpsWorkItemInfo> _activeWorkItems = [];
    private string? _lastError;

    public event EventHandler? WorkItemsChanged;

    public IReadOnlyList<AzureDevOpsWorkItemInfo> ActiveWorkItems
    {
        get { lock (_statusLock) return _activeWorkItems.ToList(); }
    }

    public string? LastError
    {
        get { lock (_statusLock) return _lastError; }
    }

    public AzureDevOpsMonitor(AppSettings settings)
    {
        _settings = settings;
    }

    public void Configure(AppSettings settings)
    {
        _settings = settings;
        if (_timer != null)
            _timer.Interval = GetTimerInterval().TotalMilliseconds;
    }

    public void Start()
    {
        Configure(_settings);

        try { if (_isUserActive) _ = PollAsync(); }
        catch (Exception ex) { CrashLogger.Log("AzureDevOpsMonitor.InitialPoll", ex); }

        _timer = new System.Timers.Timer(GetTimerInterval().TotalMilliseconds);
        _timer.Elapsed += async (_, _) =>
        {
            try { await PollAsync(); }
            catch (Exception ex) { CrashLogger.Log("AzureDevOpsMonitor.Poll", ex); }
        };
        _timer.Start();
    }

    public void SetUserActive(bool isActive)
    {
        _isUserActive = isActive;

        if (_timer == null)
            return;

        if (isActive)
        {
            _timer.Start();

            try { _ = PollAsync(); }
            catch (Exception ex) { CrashLogger.Log("AzureDevOpsMonitor.ResumePoll", ex); }
        }
        else
        {
            _timer.Stop();
        }
    }

    public Task RefreshNowAsync() => PollAsync(queueIfBusy: true);

    private async Task PollAsync(bool queueIfBusy = false)
    {
        if (_isDisposed || !_isUserActive)
            return;

        if (queueIfBusy)
        {
            await _pollLock.WaitAsync();
        }
        else if (!await _pollLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            if (!_settings.AzureDevOps.Enabled)
            {
                UpdateStatuses([], null);
                return;
            }

            var context = await ResolveContextAsync();
            var queryResultJson = await RunAzAsync(BuildQueryArguments(context));
            var ids = ParseWorkItemIds(queryResultJson);

            var workItems = new List<AzureDevOpsWorkItemInfo>();
            foreach (var id in ids)
            {
                var detailsJson = await RunAzAsync(BuildWorkItemShowArguments(context, id));
                var item = ParseWorkItem(detailsJson, context.OrganizationUrl, context.Project);
                if (item != null)
                    workItems.Add(item);
            }

            workItems = workItems
                .OrderBy(item => item.WorkItemType, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Id)
                .ToList();

            UpdateStatuses(workItems, null);
        }
        catch (Exception ex)
        {
            CrashLogger.Log("AzureDevOpsMonitor.Query", ex);
            UpdateStatuses([], ex.Message);
        }
        finally
        {
            _pollLock.Release();
        }
    }

    private void UpdateStatuses(List<AzureDevOpsWorkItemInfo> workItems, string? error)
    {
        lock (_statusLock)
        {
            _activeWorkItems = workItems;
            _lastError = error;
        }

        WorkItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    private TimeSpan GetTimerInterval()
    {
        var seconds = _settings.AzureDevOps.PollIntervalSeconds <= 0
            ? 180
            : _settings.AzureDevOps.PollIntervalSeconds;
        return TimeSpan.FromSeconds(seconds);
    }

    private async Task<AzureDevOpsContext> ResolveContextAsync()
    {
        var defaults = await LoadCliDefaultsAsync();
        return new AzureDevOpsContext(
            string.IsNullOrWhiteSpace(_settings.AzureDevOps.OrganizationUrl)
                ? defaults.OrganizationUrl
                : _settings.AzureDevOps.OrganizationUrl.Trim(),
            string.IsNullOrWhiteSpace(_settings.AzureDevOps.Project)
                ? defaults.Project
                : _settings.AzureDevOps.Project.Trim());
    }

    private async Task<AzureDevOpsContext> LoadCliDefaultsAsync()
    {
        try
        {
            var output = await RunAzAsync(["devops", "configure", "--list"]);
            var organization = string.Empty;
            var project = string.Empty;

            foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                if (line.StartsWith("organization =", StringComparison.OrdinalIgnoreCase))
                    organization = line["organization =".Length..].Trim();
                else if (line.StartsWith("project =", StringComparison.OrdinalIgnoreCase))
                    project = line["project =".Length..].Trim();
            }

            return new AzureDevOpsContext(organization, project);
        }
        catch (Exception ex)
        {
            CrashLogger.Log("AzureDevOpsMonitor.LoadCliDefaults", ex);
            return new AzureDevOpsContext(string.Empty, string.Empty);
        }
    }

    private List<string> BuildQueryArguments(AzureDevOpsContext context)
    {
        var states = _settings.AzureDevOps.InProgressStates
            .Where(state => !string.IsNullOrWhiteSpace(state))
            .Select(state => state.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var workItemTypes = _settings.AzureDevOps.WorkItemTypes
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .Select(type => type.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (states.Count == 0)
            states = ["New", "Committed", "To Do", "In Progress"];
        if (workItemTypes.Count == 0)
            workItemTypes = ["Product Backlog Item", "Task", "Bug"];

        var wiqlStates = string.Join(", ", states.Select(state => $"'{EscapeWiql(state)}'"));
        var wiqlTypes = string.Join(", ", workItemTypes.Select(type => $"'{EscapeWiql(type)}'"));
        var projectClause = string.IsNullOrWhiteSpace(context.Project)
            ? string.Empty
            : $"[System.TeamProject] = '{EscapeWiql(context.Project)}' And ";
        var wiql = $"Select [System.Id] From WorkItems Where {projectClause}[System.AssignedTo] = @Me And [System.State] In ({wiqlStates}) And [System.WorkItemType] In ({wiqlTypes}) Order By [System.State], [System.ChangedDate] Desc";

        var arguments = new List<string>
        {
            "boards", "query",
            "--wiql", wiql,
            "--only-show-errors",
            "--output", "json"
        };

        AppendContextArguments(arguments, context);
        return arguments;
    }

    private static List<string> BuildWorkItemShowArguments(AzureDevOpsContext context, int id)
    {
        var arguments = new List<string>
        {
            "boards", "work-item", "show",
            "--id", id.ToString(),
            "--only-show-errors",
            "--output", "json"
        };

        AppendOrganizationArgument(arguments, context.OrganizationUrl);
        return arguments;
    }

    private static void AppendContextArguments(List<string> arguments, AzureDevOpsContext context)
    {
        AppendOrganizationArgument(arguments, context.OrganizationUrl);

        if (!string.IsNullOrWhiteSpace(context.Project))
        {
            arguments.Add("--project");
            arguments.Add(context.Project);
        }
    }

    private static void AppendOrganizationArgument(List<string> arguments, string? organizationUrl)
    {
        if (string.IsNullOrWhiteSpace(organizationUrl))
            return;

        arguments.Add("--org");
        arguments.Add(organizationUrl);
    }

    private async Task<string> RunAzAsync(IReadOnlyList<string> arguments)
    {
        var azCommandPath = ResolveAzCommandPath();

        var startInfo = new ProcessStartInfo
        {
            FileName = azCommandPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            var argsText = string.Join(" ", arguments);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(stderr)
                    ? $"az {argsText} failed with exit code {process.ExitCode}."
                    : stderr.Trim());
        }

        return stdout;
    }

    private string ResolveAzCommandPath()
    {
        if (!string.IsNullOrWhiteSpace(_azCommandPath))
            return _azCommandPath;

        var candidates = new List<string?>
        {
            FindOnPath("az.cmd"),
            FindOnPath("az.exe"),
            FindOnPath("az"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Azure CLI", "wbin", "az.cmd")
        };

        _azCommandPath = candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .FirstOrDefault(File.Exists);

        if (string.IsNullOrWhiteSpace(_azCommandPath))
        {
            throw new InvalidOperationException(
                "Azure CLI was not found. Install Azure CLI or add az.cmd to PATH before enabling Azure DevOps integration.");
        }

        return _azCommandPath;
    }

    private static string? FindOnPath(string fileName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
            return null;

        foreach (var segment in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(segment.Trim(), fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // Ignore malformed PATH entries while probing for az.
            }
        }

        return null;
    }

    private static List<int> ParseWorkItemIds(string json)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
        var root = document.RootElement;
        var workItemsElement = root.ValueKind switch
        {
            JsonValueKind.Array => root,
            JsonValueKind.Object when root.TryGetProperty("workItems", out var nestedWorkItems) &&
                                     nestedWorkItems.ValueKind == JsonValueKind.Array => nestedWorkItems,
            _ => default
        };

        if (workItemsElement.ValueKind != JsonValueKind.Array)
            return [];

        var ids = new List<int>();
        foreach (var item in workItemsElement.EnumerateArray())
        {
            if (item.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var id))
                ids.Add(id);
        }

        return ids;
    }

    private static AzureDevOpsWorkItemInfo? ParseWorkItem(string json, string fallbackOrganizationUrl, string fallbackProject)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id))
            return null;

        if (!root.TryGetProperty("fields", out var fieldsElement))
            return null;

        var title = GetFieldString(fieldsElement, "System.Title");
        var state = GetFieldString(fieldsElement, "System.State");
        var workItemType = GetFieldString(fieldsElement, "System.WorkItemType");
        var project = GetFieldString(fieldsElement, "System.TeamProject");
        if (string.IsNullOrWhiteSpace(project))
            project = fallbackProject;

        var url = BuildWorkItemUrl(fallbackOrganizationUrl, project, id);
        return new AzureDevOpsWorkItemInfo(
            id,
            string.IsNullOrWhiteSpace(title) ? $"Work item {id}" : title,
            state,
            workItemType,
            project,
            url);
    }

    private static string GetFieldString(JsonElement fieldsElement, string name)
    {
        if (!fieldsElement.TryGetProperty(name, out var value))
            return string.Empty;

        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.ToString();
    }

    private static string? BuildWorkItemUrl(string organizationUrl, string project, int id)
    {
        if (string.IsNullOrWhiteSpace(organizationUrl) || string.IsNullOrWhiteSpace(project))
            return null;

        return $"{organizationUrl.TrimEnd('/')}/{Uri.EscapeDataString(project)}/_workitems/edit/{id}";
    }

    private static string EscapeWiql(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    public void Dispose()
    {
        _isDisposed = true;
        _timer?.Stop();
        _timer?.Dispose();
    }

    private sealed record AzureDevOpsContext(string OrganizationUrl, string Project);
}
