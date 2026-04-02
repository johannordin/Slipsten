using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Slipsten.Data;
using Slipsten.GitHub;
using Slipsten.Settings;

namespace Slipsten.Views;

public partial class GitHubSettingsWindow : Window
{
    private const int WindowWidth = 750;
    private const int WindowHeight = 500;
    private readonly SettingsService _settingsService;
    private readonly GitHubDiscoveryService _discoveryService = new();
    private AppSettings _settings;
    private List<SelectableWorkflowItem> _allWorkflowItems = [];

    public GitHubSettingsWindow(SettingsService settingsService)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _settings = settingsService.Load();

        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(WindowWidth, WindowHeight));
        WindowHelper.SetAppIcon(this);
        CenterOnScreen();

        PatBox.Text = _settings.GitHub.PersonalAccessToken;
        PrPollIntervalBox.Text = _settings.GitHub.PullRequestPollIntervalSeconds.ToString();
        CiPollIntervalBox.Text = _settings.GitHub.CiPollIntervalSeconds.ToString();
        StatusText.Text = "Refresh repositories to load private repositories from GitHub.";
        UpdateSelectionUi(false);

        _ = RefreshRepositoriesAsync();
    }

    private void CenterOnScreen()
    {
        var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
            AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
        var workArea = displayArea.WorkArea;
        AppWindow.Move(new Windows.Graphics.PointInt32(
            (workArea.Width - WindowWidth) / 2 + workArea.X,
            (workArea.Height - WindowHeight) / 2 + workArea.Y));
    }

    private async Task RefreshRepositoriesAsync()
    {
        SaveAccessSettingsToModel();
        StatusText.Text = "Loading private repositories...";

        if (string.IsNullOrWhiteSpace(_settings.GitHub.PersonalAccessToken))
        {
            StatusText.Text = "Add a GitHub token first.";
            RepoList.ItemsSource = null;
            return;
        }

        try
        {
            var repositories = (await _discoveryService.GetAccessiblePrivateRepositoriesAsync(_settings)).ToList();
            var knownRepositories = _settings.GitHub.RepositoryWatches
                .Select(w => w.Repository)
                .Where(r => repositories.All(repo => !string.Equals(repo.FullName, r, StringComparison.OrdinalIgnoreCase)))
                .Select(r => new GitHubRepositoryCatalogItem(0, r, true));

            RepoList.ItemsSource = repositories
                .Concat(knownRepositories)
                .GroupBy(r => r.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var repo = g.First();
                    return new GitHubRepoListItem
                    {
                        FullName = repo.FullName,
                        DisplayName = repo.IsConfigured ? $"{repo.FullName}  [configured]" : repo.FullName
                    };
                })
                .OrderBy(r => r.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            StatusText.Text = "Select a repository to configure watches.";
        }
        catch (Exception ex)
        {
            CrashLogger.Log("GitHubSettingsWindow.RefreshRepositories", ex);
            StatusText.Text = $"Could not load repositories: {ex.Message}";
        }
    }

    private async Task LoadSelectedRepositoryAsync()
    {
        if (RepoList.SelectedItem is not GitHubRepoListItem selected)
        {
            UpdateSelectionUi(false);
            SelectedRepoText.Text = string.Empty;
            SelectedRepoHint.Text = string.Empty;
            WorkflowList.ItemsSource = null;
            WorkflowFilterBox.Text = string.Empty;
            _allWorkflowItems = [];
            SetWatchCheckboxes(new GitHubRepositoryWatchSettings());
            return;
        }

        var existing = _settings.GitHub.RepositoryWatches
            .FirstOrDefault(w => string.Equals(w.Repository, selected.FullName, StringComparison.OrdinalIgnoreCase))
            ?? new GitHubRepositoryWatchSettings { Repository = selected.FullName };

        UpdateSelectionUi(true);
        SelectedRepoText.Text = selected.FullName;
        SelectedRepoHint.Text = existing.HasAnyWatchEnabled
            ? "Existing watch configuration loaded."
            : "No watch configuration saved yet for this repository.";
        SetWatchCheckboxes(existing);

        try
        {
            var workflows = await _discoveryService.GetWorkflowsAsync(_settings.GitHub.PersonalAccessToken, selected.FullName);
            _allWorkflowItems = workflows
                .Select(workflow => new SelectableWorkflowItem
                {
                    FileName = workflow.FileName,
                    Name = workflow.Name,
                    DisplayName = $"{workflow.Name} ({workflow.FileName})",
                    IsSelected = existing.Workflows.Any(w => string.Equals(w.WorkflowFileName, workflow.FileName, StringComparison.OrdinalIgnoreCase))
                })
                .ToList();
            ApplyWorkflowFilter();
        }
        catch (Exception ex)
        {
            CrashLogger.Log("GitHubSettingsWindow.LoadWorkflows", ex);
            _allWorkflowItems = [];
            WorkflowList.ItemsSource = _allWorkflowItems;
            StatusText.Text = $"Workflows could not be loaded for {selected.FullName}: {ex.Message}";
        }
    }

    private void SetWatchCheckboxes(GitHubRepositoryWatchSettings settings)
    {
        WatchMyPrsCheck.IsChecked = settings.WatchMyPullRequests;
        WatchCiFailuresCheck.IsChecked = settings.WatchCiFailures;
    }

    private void UpdateSelectionUi(bool hasSelection)
    {
        NoRepoSelectedText.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
        RepoDetailsPanel.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        SaveRepoWatchButton.IsEnabled = hasSelection;
        RemoveRepoWatchButton.IsEnabled = hasSelection;
    }

    private void SaveAccessSettingsToModel()
    {
        _settings.GitHub.PersonalAccessToken = PatBox.Text.Trim();
        if (int.TryParse(PrPollIntervalBox.Text, out var prPoll) && prPoll > 0)
            _settings.GitHub.PullRequestPollIntervalSeconds = prPoll;
        if (int.TryParse(CiPollIntervalBox.Text, out var ciPoll) && ciPoll > 0)
            _settings.GitHub.CiPollIntervalSeconds = ciPoll;
    }

    private async void SaveAccess_Click(object sender, RoutedEventArgs e)
    {
        SaveAccessSettingsToModel();
        _settingsService.Save(_settings);
        StatusText.Text = "GitHub access settings saved.";
        await RefreshRepositoriesAsync();
    }

    private async void RefreshRepos_Click(object sender, RoutedEventArgs e)
    {
        await RefreshRepositoriesAsync();
    }

    private async void RepoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await LoadSelectedRepositoryAsync();
    }

    private void SaveRepoWatch_Click(object sender, RoutedEventArgs e)
    {
        if (RepoList.SelectedItem is not GitHubRepoListItem selected)
        {
            StatusText.Text = "Select a repository first.";
            return;
        }

        SaveAccessSettingsToModel();

        var updated = new GitHubRepositoryWatchSettings
        {
            Repository = selected.FullName,
            WatchMyPullRequests = WatchMyPrsCheck.IsChecked == true,
            WatchCiFailures = WatchCiFailuresCheck.IsChecked == true,
            Workflows = (WorkflowList.ItemsSource as IEnumerable<SelectableWorkflowItem> ?? [])
                .Where(w => w.IsSelected)
                .Select(w => new GitHubWorkflowSelection
                {
                    WorkflowName = w.Name,
                    WorkflowFileName = w.FileName
                })
                .ToList()
        };

        _settings.GitHub.RepositoryWatches.RemoveAll(w => string.Equals(w.Repository, selected.FullName, StringComparison.OrdinalIgnoreCase));
        if (updated.HasAnyWatchEnabled)
            _settings.GitHub.RepositoryWatches.Add(updated);

        _settingsService.Save(_settings);
        StatusText.Text = $"Saved watch setup for {selected.FullName}.";
        _ = RefreshRepositoriesAsync();
    }

    private void RemoveRepoWatch_Click(object sender, RoutedEventArgs e)
    {
        if (RepoList.SelectedItem is not GitHubRepoListItem selected)
        {
            StatusText.Text = "Select a repository first.";
            return;
        }

        SaveAccessSettingsToModel();
        _settings.GitHub.RepositoryWatches.RemoveAll(w => string.Equals(w.Repository, selected.FullName, StringComparison.OrdinalIgnoreCase));
        _settingsService.Save(_settings);
        SetWatchCheckboxes(new GitHubRepositoryWatchSettings());
        WorkflowList.ItemsSource = new List<SelectableWorkflowItem>();
        UpdateSelectionUi(false);
        RepoList.SelectedItem = null;
        StatusText.Text = $"Removed watch setup for {selected.FullName}.";
        _ = RefreshRepositoriesAsync();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void WorkflowFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyWorkflowFilter();
    }

    private void ApplyWorkflowFilter()
    {
        var filter = WorkflowFilterBox.Text?.Trim() ?? string.Empty;
        WorkflowList.ItemsSource = string.IsNullOrWhiteSpace(filter)
            ? _allWorkflowItems
            : _allWorkflowItems
                .Where(w =>
                    w.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    w.FileName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    w.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
    }
}

public sealed class GitHubRepoListItem
{
    public string FullName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
}

public sealed class SelectableWorkflowItem
{
    public string Name { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool IsSelected { get; set; }
}
