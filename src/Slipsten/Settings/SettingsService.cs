using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Slipsten.Settings;

public class SettingsService
{
    private static readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private readonly string _path;

    public SettingsService()
    {
        var settingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Slipsten");
        Directory.CreateDirectory(settingsDirectory);

        _path = Path.Combine(settingsDirectory, "slipsten.json");
        TryMigrateLegacySettingsFile();
    }

    public AppSettings Load()
    {
        if (!File.Exists(_path))
        {
            var defaults = new AppSettings();
            Save(defaults);
            return defaults;
        }

        var json = File.ReadAllText(_path);
        var settings = JsonSerializer.Deserialize<AppSettings>(json, _options) ?? new AppSettings();
        MigrateGitHubSettings(settings);
        NormalizeGitHubSettings(settings);
        NormalizeAzureDevOpsSettings(settings);
        return settings;
    }

    public void Save(AppSettings settings)
    {
        NormalizeGitHubSettings(settings);
        NormalizeAzureDevOpsSettings(settings);
        var json = JsonSerializer.Serialize(settings, _options);
        File.WriteAllText(_path, json);
    }

    private static void MigrateGitHubSettings(AppSettings settings)
    {
        if (settings.GitHub.RepositoryWatches.Count > 0)
            return;

        var byRepo = new Dictionary<string, GitHubRepositoryWatchSettings>(StringComparer.OrdinalIgnoreCase);

        GitHubRepositoryWatchSettings GetOrCreate(string repository)
        {
            if (!byRepo.TryGetValue(repository, out var watch))
            {
                watch = new GitHubRepositoryWatchSettings { Repository = repository };
                byRepo[repository] = watch;
            }

            return watch;
        }

        foreach (var repository in settings.GitHub.WatchedRepositories.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            var watch = GetOrCreate(repository.Trim());
            watch.WatchMyPullRequests = true;
            watch.WatchReviewRequestsForMe = true;
        }

        foreach (var monitor in settings.GitHub.CiMonitors.Where(m => !string.IsNullOrWhiteSpace(m.Repository)))
        {
            var watch = GetOrCreate(monitor.Repository.Trim());
            watch.WatchCiFailures = true;

            foreach (var workflow in monitor.Workflows.Where(w => !string.IsNullOrWhiteSpace(w)))
            {
                var fileName = Path.GetFileName(workflow.Trim());
                if (watch.Workflows.Any(w => string.Equals(w.WorkflowFileName, fileName, StringComparison.OrdinalIgnoreCase)))
                    continue;

                watch.Workflows.Add(new GitHubWorkflowSelection
                {
                    WorkflowName = Path.GetFileNameWithoutExtension(fileName),
                    WorkflowFileName = fileName
                });
            }
        }

        settings.GitHub.RepositoryWatches = byRepo.Values
            .OrderBy(w => w.Repository, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void NormalizeGitHubSettings(AppSettings settings)
    {
        if (settings.GitHub.PullRequestPollIntervalSeconds <= 0)
            settings.GitHub.PullRequestPollIntervalSeconds = settings.GitHub.PollIntervalSeconds > 0
                ? settings.GitHub.PollIntervalSeconds
                : 120;

        if (settings.GitHub.CiPollIntervalSeconds <= 0)
            settings.GitHub.CiPollIntervalSeconds = settings.GitHub.PollIntervalSeconds > 0
                ? Math.Max(settings.GitHub.PollIntervalSeconds, 300)
                : 600;

        settings.GitHub.RepositoryWatches = settings.GitHub.RepositoryWatches
            .Where(w => !string.IsNullOrWhiteSpace(w.Repository))
            .GroupBy(w => w.Repository.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var watch = g.First();
                watch.Repository = watch.Repository.Trim();
                watch.WatchMyPullRequests = watch.WatchMyPullRequests ||
                    watch.WatchTeamPullRequests ||
                    watch.WatchReviewRequestsForMe ||
                    watch.WatchReviewRequestsForTeam;
                watch.WatchTeamPullRequests = false;
                watch.WatchReviewRequestsForMe = false;
                watch.WatchReviewRequestsForTeam = false;
                watch.Teams = watch.Teams
                    .Where(t => t.TeamId > 0)
                    .GroupBy(t => t.TeamId)
                    .Select(tg => tg.First())
                    .OrderBy(t => t.TeamName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                watch.Workflows = watch.Workflows
                    .Where(wf => !string.IsNullOrWhiteSpace(wf.WorkflowFileName))
                    .GroupBy(wf => wf.WorkflowFileName.Trim(), StringComparer.OrdinalIgnoreCase)
                    .Select(wfg =>
                    {
                        var workflow = wfg.First();
                        workflow.WorkflowFileName = workflow.WorkflowFileName.Trim();
                        workflow.WorkflowName = string.IsNullOrWhiteSpace(workflow.WorkflowName)
                            ? Path.GetFileNameWithoutExtension(workflow.WorkflowFileName)
                            : workflow.WorkflowName.Trim();
                        return workflow;
                    })
                    .OrderBy(wf => wf.WorkflowName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return watch;
            })
            .Where(w => w.HasAnyWatchEnabled)
            .OrderBy(w => w.Repository, StringComparer.OrdinalIgnoreCase)
            .ToList();

        settings.GitHub.WatchedRepositories = [];
        settings.GitHub.CiMonitors = [];
    }

    private static void NormalizeAzureDevOpsSettings(AppSettings settings)
    {
        settings.AzureDevOps.OrganizationUrl = settings.AzureDevOps.OrganizationUrl.Trim();
        settings.AzureDevOps.Project = settings.AzureDevOps.Project.Trim();
        settings.AzureDevOps.PollIntervalSeconds = settings.AzureDevOps.PollIntervalSeconds <= 0
            ? 180
            : settings.AzureDevOps.PollIntervalSeconds;
        settings.AzureDevOps.InProgressStates = settings.AzureDevOps.InProgressStates
            .Where(state => !string.IsNullOrWhiteSpace(state))
            .Select(state => state.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        settings.AzureDevOps.WorkItemTypes = settings.AzureDevOps.WorkItemTypes
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .Select(type => type.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (settings.AzureDevOps.InProgressStates.Count == 0)
        {
            settings.AzureDevOps.InProgressStates = ["New", "Committed", "To Do", "In Progress"];
        }
        else if (settings.AzureDevOps.InProgressStates.Count == 2 &&
                 settings.AzureDevOps.InProgressStates.Contains("Active", StringComparer.OrdinalIgnoreCase) &&
                 settings.AzureDevOps.InProgressStates.Contains("In Progress", StringComparer.OrdinalIgnoreCase))
        {
            settings.AzureDevOps.InProgressStates = ["New", "Committed", "To Do", "In Progress"];
        }

        if (settings.AzureDevOps.WorkItemTypes.Count == 0)
            settings.AzureDevOps.WorkItemTypes = ["Product Backlog Item", "Task", "Bug"];
    }

    private void TryMigrateLegacySettingsFile()
    {
        var legacyPath = Path.Combine(AppContext.BaseDirectory, "slipsten.json");
        if (File.Exists(_path) || !File.Exists(legacyPath))
            return;

        File.Copy(legacyPath, _path);
    }
}
