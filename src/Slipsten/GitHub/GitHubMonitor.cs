using Octokit;
using Slipsten.Data;
using Slipsten.Settings;

namespace Slipsten.GitHub;

public class GitHubMonitor : IDisposable
{
    private System.Timers.Timer? _timer;
    private GitHubClient? _client;
    private AppSettings _settings;
    private bool _isUserActive = true;
    private string? _currentUserLogin;
    private DateTime _lastPullRequestPollUtc = DateTime.MinValue;
    private DateTime _lastCiPollUtc = DateTime.MinValue;
    private readonly TimeSpan _workflowCacheTtl = TimeSpan.FromHours(1);
    private readonly Dictionary<string, CacheEntry<IReadOnlyList<GitHubWorkflowSelection>>> _workflowCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _statusLock = new();
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private List<WorkflowStatusInfo> _workflowStatuses = [];
    private List<PullRequestStatusInfo> _pullRequestStatuses = [];

    public event EventHandler? CiStatusChanged;
    public event EventHandler? PullRequestsChanged;

    public IReadOnlyList<WorkflowStatusInfo> WorkflowStatuses
    {
        get { lock (_statusLock) return _workflowStatuses.ToList(); }
    }

    public IReadOnlyList<PullRequestStatusInfo> PullRequestStatuses
    {
        get { lock (_statusLock) return _pullRequestStatuses.ToList(); }
    }

    public CiStatus OverallStatus
    {
        get
        {
            lock (_statusLock)
            {
                if (_workflowStatuses.Count == 0) return CiStatus.Unknown;
                if (_workflowStatuses.Any(s => s.Status == CiStatus.Red)) return CiStatus.Red;
                if (_workflowStatuses.All(s => s.Status == CiStatus.Green)) return CiStatus.Green;
                return CiStatus.Unknown;
            }
        }
    }

    public GitHubMonitor(AppSettings settings)
    {
        _settings = settings;
    }

    public void Configure(AppSettings settings)
    {
        _settings = settings;
        _client = string.IsNullOrWhiteSpace(settings.GitHub.PersonalAccessToken)
            ? null
            : new GitHubClient(new ProductHeaderValue("Slipsten"))
            {
                Credentials = new Credentials(settings.GitHub.PersonalAccessToken)
            };

        _currentUserLogin = null;
        _workflowCache.Clear();

        if (_timer != null)
            _timer.Interval = GetTimerInterval().TotalMilliseconds;
    }

    public void Start()
    {
        Configure(_settings);

        try { if (_isUserActive) _ = PollAsync(); }
        catch (Exception ex) { CrashLogger.Log("GitHubMonitor.InitialPoll", ex); }

        _timer = new System.Timers.Timer(GetTimerInterval().TotalMilliseconds);
        _timer.Elapsed += async (_, _) =>
        {
            try { await PollAsync(); }
            catch (Exception ex) { CrashLogger.Log("GitHubMonitor.Poll", ex); }
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
            catch (Exception ex) { CrashLogger.Log("GitHubMonitor.ResumePoll", ex); }
        }
        else
        {
            _timer.Stop();
        }
    }

    public Task RefreshNowAsync() => PollAsync(forcePullRequests: true, forceCi: true, queueIfBusy: true);

    private async Task PollAsync(bool forcePullRequests = false, bool forceCi = false, bool queueIfBusy = false)
    {
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
        if (_client == null || !_isUserActive) return;

        var configuredRepositories = _settings.GitHub.RepositoryWatches
            .Where(w => w.HasAnyWatchEnabled)
            .ToList();

        var nowUtc = DateTime.UtcNow;
        var shouldPollPullRequests = forcePullRequests ||
            nowUtc - _lastPullRequestPollUtc >= TimeSpan.FromSeconds(_settings.GitHub.PullRequestPollIntervalSeconds);
        var shouldPollCi = forceCi ||
            nowUtc - _lastCiPollUtc >= TimeSpan.FromSeconds(_settings.GitHub.CiPollIntervalSeconds);

        if (configuredRepositories.Count == 0)
        {
            lock (_statusLock)
            {
                _pullRequestStatuses = [];
                _workflowStatuses = [];
            }

            RaisePullRequestsChanged();
            RaiseCiStatusChanged();
            return;
        }

        if (string.IsNullOrWhiteSpace(_currentUserLogin))
        {
            var currentUser = await _client.User.Current();
            _currentUserLogin = currentUser.Login;
        }

        if (shouldPollPullRequests)
        {
            var newPullRequestStatuses = await SearchMyPullRequestsAsync(configuredRepositories, _currentUserLogin!);

            lock (_statusLock)
            {
                _pullRequestStatuses = newPullRequestStatuses
                    .OrderBy(pr => pr.Repository, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(pr => pr.Number)
                    .ToList();
            }

            _lastPullRequestPollUtc = nowUtc;
            RaisePullRequestsChanged();
        }

        if (shouldPollCi)
        {
            await PollCiStatusAsync(configuredRepositories);
            _lastCiPollUtc = nowUtc;
        }
        }
        finally
        {
            _pollLock.Release();
        }
    }

    private async Task<List<PullRequestStatusInfo>> SearchMyPullRequestsAsync(
        List<GitHubRepositoryWatchSettings> configuredRepositories,
        string currentUserLogin)
    {
        var repositoriesWithPrWatch = configuredRepositories
            .Where(w => w.WatchMyPullRequests)
            .Select(w => w.Repository)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (repositoriesWithPrWatch.Count == 0)
            return [];

        try
        {
            var results = new List<PullRequestStatusInfo>();
            var seenPullRequests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var searchQueries = new[]
            {
                $"is:pr is:open author:{currentUserLogin} archived:false",
                $"is:pr is:open author:Copilot assignee:{currentUserLogin} archived:false"
            };

            foreach (var query in searchQueries)
            {
                var search = await _client!.Search.SearchIssues(new SearchIssuesRequest(query)
                {
                    PerPage = 100
                });

                foreach (var issue in search.Items)
                {
                    var repository = TryGetRepositoryFromUrl(issue.PullRequest?.HtmlUrl ?? issue.HtmlUrl);
                    if (string.IsNullOrWhiteSpace(repository) ||
                        !repositoriesWithPrWatch.Contains(repository))
                    {
                        continue;
                    }

                    if (!seenPullRequests.Add($"{repository}#{issue.Number}"))
                    {
                        continue;
                    }

                    try
                    {
                        results.Add(new PullRequestStatusInfo(
                            repository,
                            issue.Number,
                            issue.Title,
                            issue.PullRequest?.HtmlUrl ?? issue.HtmlUrl,
                            issue.User?.Login ?? string.Empty));
                    }
                    catch (Exception ex)
                    {
                        CrashLogger.Log($"GitHubMonitor.LoadPullRequest({repository}#{issue.Number})", ex);
                    }
                }
            }

            return results;
        }
        catch (Exception ex)
        {
            CrashLogger.Log("GitHubMonitor.SearchMyPullRequests", ex);
            return [];
        }
    }

    private async Task PollCiStatusAsync(List<GitHubRepositoryWatchSettings> configuredRepositories)
    {
        if (_client == null)
            return;

        var newStatuses = new List<WorkflowStatusInfo>();

        foreach (var repositoryWatch in configuredRepositories.Where(w => w.WatchCiFailures))
        {
            if (!GitHubDiscoveryService.TrySplitRepository(repositoryWatch.Repository, out var owner, out var repo))
                continue;

            try
            {
                if (repositoryWatch.Workflows.Count == 0)
                {
                    var workflows = await GetWorkflowsForRepositoryAsync(owner, repo, repositoryWatch.Repository);
                    foreach (var workflow in workflows)
                    {
                        newStatuses.Add(await GetLatestRunStatusAsync(
                            owner,
                            repo,
                            workflow.WorkflowName,
                            workflow.WorkflowFileName));
                    }
                }
                else
                {
                    foreach (var workflow in repositoryWatch.Workflows)
                    {
                        newStatuses.Add(await GetLatestRunStatusAsync(
                            owner,
                            repo,
                            workflow.WorkflowName,
                            workflow.WorkflowFileName));
                    }
                }
            }
            catch (Exception ex)
            {
                CrashLogger.Log($"GitHubMonitor.PollCi({repositoryWatch.Repository})", ex);
            }
        }

        lock (_statusLock) _workflowStatuses = newStatuses;
        RaiseCiStatusChanged();
    }

    private async Task<WorkflowStatusInfo> GetLatestRunStatusAsync(
        string owner,
        string repo,
        string workflowName,
        string workflowFileName)
    {
        var fullRepo = $"{owner}/{repo}";

        try
        {
            var runs = await _client!.Actions.Workflows.Runs.ListByWorkflow(
                owner,
                repo,
                workflowFileName,
                new WorkflowRunsRequest { Branch = "main", Status = CheckRunStatusFilter.Completed },
                new ApiOptions { PageSize = 1, PageCount = 1 });

            if (runs.WorkflowRuns.Count == 0)
            {
                runs = await _client.Actions.Workflows.Runs.ListByWorkflow(
                    owner,
                    repo,
                    workflowFileName,
                    new WorkflowRunsRequest { Branch = "master", Status = CheckRunStatusFilter.Completed },
                    new ApiOptions { PageSize = 1, PageCount = 1 });
            }

            if (runs.WorkflowRuns.Count == 0)
                return new(fullRepo, workflowName, workflowFileName, CiStatus.Unknown, "", DateTime.Now);

            var latest = runs.WorkflowRuns[0];
            var completedAt = latest.UpdatedAt.UtcDateTime;
            var status = CiStatus.Unknown;

            if (latest.Conclusion?.Value == WorkflowRunConclusion.Success)
            {
                status = CiStatus.Green;
            }
            else if (latest.Conclusion?.Value == WorkflowRunConclusion.Failure)
            {
                status = completedAt >= DateTime.UtcNow.AddMonths(-1)
                    ? CiStatus.Red
                    : CiStatus.Unknown;
            }

            return new(fullRepo, workflowName, workflowFileName, status, latest.HtmlUrl, DateTime.Now);
        }
        catch
        {
            return new(fullRepo, workflowName, workflowFileName, CiStatus.Unknown, "", DateTime.Now);
        }
    }

    private async Task<IReadOnlyList<GitHubWorkflowSelection>> GetWorkflowsForRepositoryAsync(string owner, string repo, string repositoryKey)
    {
        if (_workflowCache.TryGetValue(repositoryKey, out var cachedWorkflows) &&
            DateTime.UtcNow - cachedWorkflows.CachedAtUtc <= _workflowCacheTtl)
        {
            return cachedWorkflows.Value;
        }

        var workflows = await _client!.Actions.Workflows.List(owner, repo);
        var selections = workflows.Workflows
            .Select(w => new GitHubWorkflowSelection
            {
                WorkflowName = string.IsNullOrWhiteSpace(w.Name) ? Path.GetFileNameWithoutExtension(w.Path) : w.Name,
                WorkflowFileName = Path.GetFileName(w.Path)
            })
            .ToList();

        _workflowCache[repositoryKey] = new CacheEntry<IReadOnlyList<GitHubWorkflowSelection>>(selections, DateTime.UtcNow);
        return selections;
    }

    private static string? TryGetRepositoryFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            return null;

        return $"{segments[0]}/{segments[1]}";
    }

    private void RaisePullRequestsChanged()
    {
        try { PullRequestsChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { CrashLogger.Log("GitHubMonitor.PullRequestsChanged", ex); }
    }

    private void RaiseCiStatusChanged()
    {
        try { CiStatusChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { CrashLogger.Log("GitHubMonitor.CiStatusChanged", ex); }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _pollLock.Dispose();
    }

    private TimeSpan GetTimerInterval()
    {
        var pr = Math.Max(30, _settings.GitHub.PullRequestPollIntervalSeconds);
        var ci = Math.Max(30, _settings.GitHub.CiPollIntervalSeconds);
        return TimeSpan.FromSeconds(Math.Min(pr, ci));
    }

    private sealed record CacheEntry<T>(T Value, DateTime CachedAtUtc);
}
