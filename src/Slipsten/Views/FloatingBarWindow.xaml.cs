using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Slipsten.AzureDevOps;
using Slipsten.Data;
using Slipsten.GitHub;
using Slipsten.Settings;
using Slipsten.TimeTracking;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace Slipsten.Views;

public partial class FloatingBarWindow : Window
{
    private const int BarWidth = 180;
    private const int BarTopOffset = 50;
    private const int BarRightOffset = 50;
    private readonly TimeTracker _tracker;
    private readonly GitHubMonitor _gitHubMonitor;
    private readonly AzureDevOpsMonitor _azureDevOpsMonitor;
    private readonly Action _openMainWindow;
    private readonly Action _openSummaryWindow;
    private readonly Action _openSettingsWindow;
    private readonly Action _openGitHubSettingsWindow;
    private readonly Action _openAzureDevOpsSettingsWindow;
    private readonly Action _exitApp;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherQueueTimer _refreshTimer;
    private bool _isCleaningUp;
    private PointInt32? _lastKnownPosition;

    public FloatingBarWindow(
        TimeTracker tracker,
        GitHubMonitor gitHubMonitor,
        AzureDevOpsMonitor azureDevOpsMonitor,
        Action openMainWindow,
        Action openSummaryWindow,
        Action openSettingsWindow,
        Action openGitHubSettingsWindow,
        Action openAzureDevOpsSettingsWindow,
        Action exitApp,
        PointInt32? initialPosition)
    {
        InitializeComponent();
        _tracker = tracker;
        _gitHubMonitor = gitHubMonitor;
        _azureDevOpsMonitor = azureDevOpsMonitor;
        _openMainWindow = openMainWindow;
        _openSummaryWindow = openSummaryWindow;
        _openSettingsWindow = openSettingsWindow;
        _openGitHubSettingsWindow = openGitHubSettingsWindow;
        _openAzureDevOpsSettingsWindow = openAzureDevOpsSettingsWindow;
        _exitApp = exitApp;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        WindowHelper.SetAppIcon(this);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(BarWidth, 32));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = false;
            presenter.SetBorderAndTitleBar(true, false);
        }
        AppWindow.Changed += OnAppWindowChanged;

        Position(initialPosition);

        _tracker.StateChanged += OnTrackerStateChanged;
        _gitHubMonitor.CiStatusChanged += OnCiStatusChanged;
        _gitHubMonitor.PullRequestsChanged += OnPullRequestsChanged;
        _azureDevOpsMonitor.WorkItemsChanged += OnAzureDevOpsWorkItemsChanged;

        AzureDevOpsFlyout.Opening += (_, _) => BuildAzureDevOpsFlyout();
        GitHubFlyout.Opening += (_, _) => BuildGitHubFlyout();

        _refreshTimer = _dispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromSeconds(30);
        _refreshTimer.Tick += (_, _) => SafeUpdateUI();
        _refreshTimer.Start();

        SafeUpdateUI();
        UpdateInboxBadges();
    }

    private void Position(PointInt32? desiredPosition)
    {
        var displayArea = DisplayArea.GetFromWindowId(
            AppWindow.Id, DisplayAreaFallback.Nearest);
        var workArea = displayArea.WorkArea;

        if (desiredPosition.HasValue)
        {
            var x = desiredPosition.Value.X;
            var y = desiredPosition.Value.Y;
            var maxX = workArea.X + Math.Max(0, workArea.Width - BarWidth);
            var maxY = workArea.Y + Math.Max(0, workArea.Height - 32);
            x = Math.Clamp(x, workArea.X, maxX);
            y = Math.Clamp(y, workArea.Y, maxY);
            AppWindow.Move(new PointInt32(x, y));
            _lastKnownPosition = new PointInt32(x, y);
            return;
        }

        var topRight = new PointInt32(
            workArea.Width - BarWidth - BarRightOffset + workArea.X,
            workArea.Y + BarTopOffset);
        AppWindow.Move(topRight);
        _lastKnownPosition = topRight;
    }

    public PointInt32? GetCurrentPosition()
    {
        try
        {
            var position = AppWindow.Position;
            _lastKnownPosition = position;
            return position;
        }
        catch
        {
            return _lastKnownPosition;
        }
    }

    private void OnTrackerStateChanged(object? sender, EventArgs e)
    {
        if (_isCleaningUp)
            return;

        _dispatcherQueue.TryEnqueue(SafeUpdateUI);
    }

    private void OnCiStatusChanged(object? sender, EventArgs e)
    {
        if (_isCleaningUp)
            return;

        _dispatcherQueue.TryEnqueue(UpdateInboxBadges);
    }

    private void OnPullRequestsChanged(object? sender, EventArgs e)
    {
        if (_isCleaningUp)
            return;

        _dispatcherQueue.TryEnqueue(UpdateInboxBadges);
    }

    private void OnAzureDevOpsWorkItemsChanged(object? sender, EventArgs e)
    {
        if (_isCleaningUp)
            return;

        _dispatcherQueue.TryEnqueue(UpdateInboxBadges);
    }

    private void SafeUpdateUI()
    {
        if (_isCleaningUp)
            return;

        try { UpdateUI(); }
        catch (Exception ex) { CrashLogger.Log("FloatingBar.UpdateUI", ex); }
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_isCleaningUp || sender.Presenter is not OverlappedPresenter presenter)
            return;

        if (presenter.State == OverlappedPresenterState.Maximized)
        {
            presenter.Restore();
            sender.Resize(new SizeInt32(BarWidth, 32));

            var currentPosition = GetCurrentPosition();
            if (currentPosition.HasValue)
                Position(currentPosition);
        }
    }

    private void UpdateUI()
    {
        if (_isCleaningUp)
            return;

        StartStopIcon.Glyph = _tracker.IsRunning ? "\uE71A" : "\uE768";
        var total = _tracker.TodayTotal();
        TotalTimeText.Text = $"{(int)total.TotalHours}:{total.Minutes:D2}";
    }

    private void UpdateInboxBadges()
    {
        if (_isCleaningUp)
            return;

        try
        {
            var activeWorkItemCount = _azureDevOpsMonitor.ActiveWorkItems.Count;
            if (activeWorkItemCount > 0)
            {
                AzureDevOpsBadge.Visibility = Visibility.Visible;
                AzureDevOpsCountText.Text = activeWorkItemCount > 99 ? "99+" : activeWorkItemCount.ToString();
            }
            else
            {
                AzureDevOpsBadge.Visibility = Visibility.Collapsed;
            }

            var watchedWorkflowCount = _gitHubMonitor.WorkflowStatuses.Count;
            var ciFailureCount = _gitHubMonitor.WorkflowStatuses.Count(wf => wf.Status == CiStatus.Red);
            if (ciFailureCount > 0)
            {
                CiBadge.Visibility = Visibility.Visible;
                CiBadge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.Tomato);
                CiCountText.Text = ciFailureCount > 99 ? "99+" : ciFailureCount.ToString();
            }
            else if (watchedWorkflowCount > 0 && _gitHubMonitor.WorkflowStatuses.All(wf => wf.Status == CiStatus.Green))
            {
                CiBadge.Visibility = Visibility.Visible;
                CiBadge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.ForestGreen);
                CiCountText.Text = watchedWorkflowCount > 99 ? "99+" : watchedWorkflowCount.ToString();
            }
            else
            {
                CiBadge.Visibility = Visibility.Collapsed;
            }

            var prCount = _gitHubMonitor.PullRequestStatuses.Count;
            if (prCount > 0)
            {
                PrBadge.Visibility = Visibility.Visible;
                PrCountText.Text = prCount > 99 ? "99+" : prCount.ToString();
            }
            else
            {
                PrBadge.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex) { CrashLogger.Log("FloatingBar.UpdateInboxBadges", ex); }
    }

    private void AzureDevOps_Click(object sender, RoutedEventArgs e)
    {
        // Flyout opens via Button.Flyout; BuildAzureDevOpsFlyout runs on Opening event
    }

    private void GitHub_Click(object sender, RoutedEventArgs e)
    {
        // Flyout opens via Button.Flyout; BuildGitHubFlyout runs on Opening event
    }

    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        // Flyout opens via Button.Flyout
    }

    private void BuildGitHubFlyout()
    {
        try
        {
            GitHubFlyoutPanel.Children.Clear();
            AddFlyoutCommandRow(GitHubFlyoutPanel, "GitHub", GitHubRefresh_Click, GitHubFlyoutSettings_Click);
            AddFlyoutDivider(GitHubFlyoutPanel);
            AddFlyoutHeader(GitHubFlyoutPanel, "Pull requests");
            var prs = _gitHubMonitor.PullRequestStatuses
                .GroupBy(pr => pr.Repository, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (prs.Count == 0)
            {
                AddFlyoutText(GitHubFlyoutPanel, "No matching open PRs");
            }
            else
            {
                AddRepositoryGroupsToPanel(
                    GitHubFlyoutPanel,
                    prs.Select(group => (group.Key, group.OrderByDescending(pr => pr.Number).Cast<object>().ToList())).ToList(),
                    entry => CreateGitHubRow((PullRequestStatusInfo)entry));
            }

            AddFlyoutDivider(GitHubFlyoutPanel);
            AddFlyoutHeader(GitHubFlyoutPanel, "CI");

            var workflows = _gitHubMonitor.WorkflowStatuses
                .GroupBy(wf => wf.Repository, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (workflows.Count == 0)
            {
                AddFlyoutText(GitHubFlyoutPanel, "No watched workflows");
            }
            else
            {
                AddRepositoryGroupsToPanel(
                    GitHubFlyoutPanel,
                    workflows.Select(group => (group.Key, group.OrderBy(item => item.WorkflowName).Cast<object>().ToList())).ToList(),
                    entry => CreateGitHubRow((WorkflowStatusInfo)entry));
            }
        }
        catch (Exception ex) { CrashLogger.Log("FloatingBar.BuildGitHubFlyout", ex); }
    }

    private void BuildAzureDevOpsFlyout()
    {
        try
        {
            AzureDevOpsFlyoutPanel.Children.Clear();
            AddFlyoutCommandRow(AzureDevOpsFlyoutPanel, "Azure DevOps", AzureDevOpsRefresh_Click, AzureDevOpsFlyoutSettings_Click);
            AddFlyoutDivider(AzureDevOpsFlyoutPanel);

            if (!_azureDevOpsMonitor.ActiveWorkItems.Any())
            {
                var text = string.IsNullOrWhiteSpace(_azureDevOpsMonitor.LastError)
                    ? "No matching in-progress work items"
                    : $"Unavailable: {_azureDevOpsMonitor.LastError}";
                AddFlyoutText(AzureDevOpsFlyoutPanel, text);
                return;
            }

            var grouped = _azureDevOpsMonitor.ActiveWorkItems
                .GroupBy(item => ClassifyAzureDevOpsWorkItemType(item.WorkItemType))
                .OrderBy(group => GetAzureDevOpsWorkItemTypeOrder(group.Key))
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (var groupIndex = 0; groupIndex < grouped.Count; groupIndex++)
            {
                var group = grouped[groupIndex];
                AddFlyoutHeader(AzureDevOpsFlyoutPanel, GetAzureDevOpsWorkItemTypeHeader(group.Key));

                foreach (var item in group.OrderBy(workItem => workItem.Title, StringComparer.OrdinalIgnoreCase))
                    AzureDevOpsFlyoutPanel.Children.Add(CreateAzureDevOpsRow(item));

                if (groupIndex < grouped.Count - 1)
                {
                    AddFlyoutDivider(AzureDevOpsFlyoutPanel);
                }
            }
        }
        catch (Exception ex) { CrashLogger.Log("FloatingBar.BuildAzureDevOpsFlyout", ex); }
    }

    private void AddFlyoutCommandRow(
        Panel panel,
        string title,
        RoutedEventHandler refreshHandler,
        RoutedEventHandler settingsHandler)
    {
        var row = new Grid
        {
            MinWidth = 280,
            Margin = new Thickness(0, 0, 0, 2)
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleText = new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Opacity = 0.75,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleText, 0);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        actions.Children.Add(CreateFlyoutIconButton(Symbol.Refresh, "Refresh now", refreshHandler));
        actions.Children.Add(CreateFlyoutIconButton(Symbol.Setting, "Open settings", settingsHandler));
        Grid.SetColumn(actions, 1);

        row.Children.Add(titleText);
        row.Children.Add(actions);
        panel.Children.Add(row);
    }

    private static Button CreateFlyoutIconButton(Symbol symbol, string toolTip, RoutedEventHandler clickHandler)
    {
        var button = new Button
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2),
            MinWidth = 0,
            MinHeight = 0,
            Content = new FontIcon
            {
                FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
                Glyph = symbol switch
                {
                    Symbol.Refresh => "\uE72C",
                    Symbol.Setting => "\uE713",
                    _ => "\uE10F"
                },
                FontSize = 15
            }
        };
        button.Click += clickHandler;
        ToolTipService.SetToolTip(button, toolTip);
        return button;
    }

    private async void GitHubRefresh_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _gitHubMonitor.RefreshNowAsync();
            UpdateInboxBadges();
            BuildGitHubFlyout();
        }
        catch (Exception ex)
        {
            CrashLogger.Log("FloatingBar.GitHubRefresh", ex);
        }
    }

    private async void AzureDevOpsRefresh_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _azureDevOpsMonitor.RefreshNowAsync();
            UpdateInboxBadges();
            BuildAzureDevOpsFlyout();
        }
        catch (Exception ex)
        {
            CrashLogger.Log("FloatingBar.AzureDevOpsRefresh", ex);
        }
    }

    private void GitHubFlyoutSettings_Click(object sender, RoutedEventArgs e)
    {
        _openGitHubSettingsWindow();
    }

    private void AzureDevOpsFlyoutSettings_Click(object sender, RoutedEventArgs e)
    {
        _openAzureDevOpsSettingsWindow();
    }

    private static void AddFlyoutHeader(Panel panel, string text) =>
        panel.Children.Add(new TextBlock
        {
            Text = text,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Opacity = 0.75,
            Margin = new Thickness(0, 4, 0, 2)
        });

    private static void AddFlyoutText(Panel panel, string text) =>
        panel.Children.Add(new TextBlock
        {
            Text = text,
            Opacity = 0.75,
            Margin = new Thickness(0, 2, 0, 2)
        });

    private static void AddFlyoutDivider(Panel panel) =>
        panel.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(0, 4, 0, 4),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.Gray),
            Opacity = 0.25
        });

    private static void AddRepositoryGroupsToPanel(
        Panel panel,
        List<(string Repository, List<object> Items)> groups,
        Func<object, FrameworkElement> createItem)
    {
        for (var index = 0; index < groups.Count; index++)
        {
            var group = groups[index];
            var repoName = group.Repository.Split('/').LastOrDefault() ?? group.Repository;
            AddFlyoutHeader(panel, repoName);

            foreach (var item in group.Items)
                panel.Children.Add(createItem(item));

            if (index < groups.Count - 1)
                AddFlyoutDivider(panel);
        }
    }

    private FrameworkElement CreateAzureDevOpsRow(AzureDevOpsWorkItemInfo workItem)
    {
        var title = string.IsNullOrWhiteSpace(workItem.State)
            ? workItem.Title
            : $"{workItem.Title} ({workItem.State})";

        var row = new Grid
        {
            Margin = new Thickness(0, 0, 0, 1)
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var stateIcon = CreateAzureDevOpsStateSymbol(workItem.State);
        stateIcon.Margin = new Thickness(0, 0, 6, 0);
        Grid.SetColumn(stateIcon, 0);

        var openButton = new Button
        {
            Content = title,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 6, 4, 6),
            MinWidth = 0
        };
        if (!string.IsNullOrWhiteSpace(workItem.Url))
        {
            var url = workItem.Url;
            openButton.Click += async (_, _) =>
            {
                await OpenExternalUrlAsync(url, "FloatingBar.OpenAzureDevOpsWorkItem");
            };
        }
        else
        {
            openButton.IsEnabled = false;
        }
        Grid.SetColumn(openButton, 1);

        var copyButton = new Button
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 6, 0, 6),
            MinWidth = 0,
            MinHeight = 0,
            Content = new FontIcon
            {
                FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
                Glyph = "\uE8C8",
                FontSize = 12
            }
        };
        ToolTipService.SetToolTip(copyButton, "Copy item number");
        var idText = workItem.Id.ToString();
        copyButton.Click += (_, _) => CopyToClipboard(idText, "FloatingBar.CopyAzureDevOpsWorkItemId");
        Grid.SetColumn(copyButton, 2);

        row.Children.Add(stateIcon);
        row.Children.Add(openButton);
        row.Children.Add(copyButton);

        return row;
    }

    private FrameworkElement CreateGitHubRow(PullRequestStatusInfo pr)
    {
        var button = new Button
        {
            Content = pr.Title,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 6, 0, 6),
            MinWidth = 0
        };

        if (!string.IsNullOrWhiteSpace(pr.Url))
        {
            var url = pr.Url;
            button.Click += async (_, _) => await OpenExternalUrlAsync(url, "FloatingBar.OpenPullRequestUrl");
        }
        else
        {
            button.IsEnabled = false;
        }

        return button;
    }

    private FrameworkElement CreateGitHubRow(WorkflowStatusInfo workflow)
    {
        var navigationUrl = !string.IsNullOrWhiteSpace(workflow.RunUrl)
            ? workflow.RunUrl
            : $"https://github.com/{workflow.Repository}/actions/workflows/{workflow.WorkflowFileName}";

        var row = new Grid { Margin = new Thickness(0, 0, 0, 1) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = (SymbolIcon)CreateWorkflowStatusIcon(workflow.Status);
        icon.Margin = new Thickness(0, 0, 6, 0);
        Grid.SetColumn(icon, 0);

        var text = workflow.Status == CiStatus.Unknown
            ? $"{workflow.WorkflowName} (no recent completed run)"
            : workflow.WorkflowName;
        var button = new Button
        {
            Content = text,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 6, 0, 6),
            MinWidth = 0
        };
        if (!string.IsNullOrWhiteSpace(navigationUrl))
        {
            var url = navigationUrl;
            button.Click += async (_, _) => await OpenExternalUrlAsync(url, "FloatingBar.OpenRunUrl");
        }
        else
        {
            button.IsEnabled = false;
        }
        Grid.SetColumn(button, 1);

        row.Children.Add(icon);
        row.Children.Add(button);
        return row;
    }

    private static IconElement CreateWorkflowStatusIcon(CiStatus status)
    {
        var symbol = status switch
        {
            CiStatus.Green => Symbol.Accept,
            CiStatus.Red => Symbol.Cancel,
            _ => Symbol.Help
        };

        var color = status switch
        {
            CiStatus.Green => Colors.ForestGreen,
            CiStatus.Red => Colors.Tomato,
            _ => Colors.Gray
        };

        return new SymbolIcon(symbol)
        {
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(color)
        };
    }

    private static string ClassifyAzureDevOpsWorkItemType(string? workItemType)
    {
        var normalized = workItemType?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return "other";

        if (normalized.Contains("feature", StringComparison.OrdinalIgnoreCase))
            return "feature";
        if (normalized.Equals("pbi", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("product backlog item", StringComparison.OrdinalIgnoreCase))
        {
            return "pbi";
        }
        if (normalized.Contains("task", StringComparison.OrdinalIgnoreCase))
            return "task";

        return "other";
    }

    private static int GetAzureDevOpsWorkItemTypeOrder(string workItemTypeKey) =>
        workItemTypeKey switch
        {
            "feature" => 0,
            "pbi" => 1,
            "task" => 2,
            _ => 3
        };

    private static string GetAzureDevOpsWorkItemTypeHeader(string workItemTypeKey) =>
        workItemTypeKey switch
        {
            "feature" => "Features",
            "pbi" => "PBIs",
            "task" => "Tasks",
            _ => "Other"
        };

    private static SymbolIcon CreateAzureDevOpsStateSymbol(string? state)
    {
        var normalized = state?.Trim() ?? string.Empty;
        var symbol = Symbol.Help;
        var color = Colors.Gray;

        if (normalized.Contains("closed", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("done", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("resolved", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("completed", StringComparison.OrdinalIgnoreCase))
        {
            symbol = Symbol.Accept;
            color = Colors.ForestGreen;
        }
        else if (normalized.Contains("blocked", StringComparison.OrdinalIgnoreCase) ||
                 normalized.Contains("hold", StringComparison.OrdinalIgnoreCase))
        {
            symbol = Symbol.Cancel;
            color = Colors.Tomato;
        }
        else if (normalized.Contains("active", StringComparison.OrdinalIgnoreCase) ||
                 normalized.Contains("progress", StringComparison.OrdinalIgnoreCase) ||
                 normalized.Contains("committed", StringComparison.OrdinalIgnoreCase))
        {
            symbol = Symbol.Clock;
            color = Colors.DarkOrange;
        }
        else if (normalized.Contains("new", StringComparison.OrdinalIgnoreCase) ||
                 normalized.Contains("to do", StringComparison.OrdinalIgnoreCase) ||
                 normalized.Equals("todo", StringComparison.OrdinalIgnoreCase))
        {
            symbol = Symbol.Page;
            color = Colors.DeepSkyBlue;
        }

        return new SymbolIcon(symbol)
        {
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(color)
        };
    }

    private static void CopyToClipboard(string text, string logContext)
    {
        try
        {
            var dataPackage = new DataPackage();
            dataPackage.SetText(text);
            Clipboard.SetContent(dataPackage);
            Clipboard.Flush();
        }
        catch (Exception ex)
        {
            CrashLogger.Log(logContext, ex);
        }
    }

    private static async Task OpenExternalUrlAsync(string url, string logContext)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                throw new InvalidOperationException($"Invalid URL: {url}");

            await Windows.System.Launcher.LaunchUriAsync(uri);
        }
        catch (Exception ex)
        {
            CrashLogger.Log(logContext, ex);
        }
    }

    private void StartStop_Click(object sender, RoutedEventArgs e)
    {
        if (_tracker.IsRunning)
            _tracker.Stop(string.Empty);
        else
            _tracker.Start(WorkType.Ordinary);
    }

    private void Time_Click(object sender, RoutedEventArgs e)
    {
        _openMainWindow();
    }

    private void TodaysTime_Click(object sender, RoutedEventArgs e)
    {
        _openMainWindow();
    }

    private void MonthSummary_Click(object sender, RoutedEventArgs e)
    {
        _openSummaryWindow();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        _openSettingsWindow();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        _exitApp();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        AppWindow.Hide();
    }

    public void ShowBar()
    {
        AppWindow.Show();
        Activate();
    }

    public void Cleanup()
    {
        if (_isCleaningUp)
            return;

        _isCleaningUp = true;
        _tracker.StateChanged -= OnTrackerStateChanged;
        _gitHubMonitor.CiStatusChanged -= OnCiStatusChanged;
        _gitHubMonitor.PullRequestsChanged -= OnPullRequestsChanged;
        _azureDevOpsMonitor.WorkItemsChanged -= OnAzureDevOpsWorkItemsChanged;
        AppWindow.Changed -= OnAppWindowChanged;
        _refreshTimer.Stop();
    }
}
