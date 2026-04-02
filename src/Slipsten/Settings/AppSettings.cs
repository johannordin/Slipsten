namespace Slipsten.Settings;

public class GitHubTeamSelection
{
    public long TeamId { get; set; }
    public string Organization { get; set; } = string.Empty;
    public string TeamSlug { get; set; } = string.Empty;
    public string TeamName { get; set; } = string.Empty;
}

public class GitHubWorkflowSelection
{
    public string WorkflowName { get; set; } = string.Empty;
    public string WorkflowFileName { get; set; } = string.Empty;
}

public class GitHubRepositoryWatchSettings
{
    public string Repository { get; set; } = string.Empty;
    public bool WatchMyPullRequests { get; set; }
    public bool WatchTeamPullRequests { get; set; }
    public bool WatchReviewRequestsForMe { get; set; }
    public bool WatchReviewRequestsForTeam { get; set; }
    public bool WatchCiFailures { get; set; }
    public List<GitHubTeamSelection> Teams { get; set; } = [];
    public List<GitHubWorkflowSelection> Workflows { get; set; } = [];

    public bool HasAnyWatchEnabled =>
        WatchMyPullRequests ||
        WatchCiFailures;
}

public class PhoneAlertSettings
{
    public bool Enabled { get; set; }
    public string Provider { get; set; } = "ntfy";
    public string NtfyTopic { get; set; } = string.Empty;
    public string NtfyServerUrl { get; set; } = "https://ntfy.sh";
    public string NtfyAccessToken { get; set; } = string.Empty;
    public string LastReminderMonth { get; set; } = string.Empty;
}

public class CiMonitorEntry
{
    public string Repository { get; set; } = string.Empty;
    public List<string> Workflows { get; set; } = [];
}

public class GitHubSettings
{
    public string PersonalAccessToken { get; set; } = string.Empty;
    public List<GitHubRepositoryWatchSettings> RepositoryWatches { get; set; } = [];
    public int PullRequestPollIntervalSeconds { get; set; } = 120;
    public int CiPollIntervalSeconds { get; set; } = 600;

    // Legacy settings kept for migration from earlier versions.
    public List<string> WatchedRepositories { get; set; } = [];
    public List<CiMonitorEntry> CiMonitors { get; set; } = [];
    public int PollIntervalSeconds { get; set; } = 120;
}

public class AzureDevOpsSettings
{
    public bool Enabled { get; set; }
    public string OrganizationUrl { get; set; } = string.Empty;
    public string Project { get; set; } = string.Empty;
    public List<string> InProgressStates { get; set; } = ["New", "Committed", "To Do", "In Progress"];
    public List<string> WorkItemTypes { get; set; } = ["Product Backlog Item", "Task", "Bug"];
    public int PollIntervalSeconds { get; set; } = 180;
}

public class AppSettings
{
    public int IdleThresholdSeconds { get; set; } = 60;
    public string DatabasePath { get; set; } = string.Empty;
    public GitHubSettings GitHub { get; set; } = new();
    public AzureDevOpsSettings AzureDevOps { get; set; } = new();
    public PhoneAlertSettings PhoneAlerts { get; set; } = new();
    public int? FloatingBarX { get; set; }
    public int? FloatingBarY { get; set; }
    public DateTime? LastCloseTime { get; set; }
    public bool WasTracking { get; set; }
}
