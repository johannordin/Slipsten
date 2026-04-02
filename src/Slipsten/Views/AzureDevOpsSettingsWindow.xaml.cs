using Microsoft.UI.Xaml;
using Slipsten.Settings;

namespace Slipsten.Views;

public partial class AzureDevOpsSettingsWindow : Window
{
    private const int WindowWidth = 560;
    private const int WindowHeight = 430;
    private readonly SettingsService _settingsService;
    private AppSettings _settings;

    public AzureDevOpsSettingsWindow(SettingsService settingsService)
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

        Populate();
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

    private void Populate()
    {
        AzureDevOpsEnabledCheck.IsChecked = _settings.AzureDevOps.Enabled;
        AzureDevOpsOrganizationBox.Text = _settings.AzureDevOps.OrganizationUrl;
        AzureDevOpsProjectBox.Text = _settings.AzureDevOps.Project;
        AzureDevOpsStatesBox.Text = string.Join(", ", _settings.AzureDevOps.InProgressStates);
        AzureDevOpsWorkItemTypesBox.Text = string.Join(", ", _settings.AzureDevOps.WorkItemTypes);
        AzureDevOpsPollIntervalBox.Text = _settings.AzureDevOps.PollIntervalSeconds.ToString();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var enabledText = _settings.AzureDevOps.Enabled ? "Enabled." : "Disabled.";
        var orgText = string.IsNullOrWhiteSpace(_settings.AzureDevOps.OrganizationUrl)
            ? "Uses az default organization."
            : $"Org: {_settings.AzureDevOps.OrganizationUrl}";
        var projectText = string.IsNullOrWhiteSpace(_settings.AzureDevOps.Project)
            ? "Uses az default project."
            : $"Project: {_settings.AzureDevOps.Project}";
        var states = _settings.AzureDevOps.InProgressStates.Count == 0
            ? "New, Committed, To Do, In Progress"
            : string.Join(", ", _settings.AzureDevOps.InProgressStates);
        var types = _settings.AzureDevOps.WorkItemTypes.Count == 0
            ? "Product Backlog Item, Task, Bug"
            : string.Join(", ", _settings.AzureDevOps.WorkItemTypes);

        AzureDevOpsSummaryText.Text = $"{enabledText} {orgText} {projectText} States: {states}. Types: {types}.";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.AzureDevOps.Enabled = AzureDevOpsEnabledCheck.IsChecked == true;
        _settings.AzureDevOps.OrganizationUrl = AzureDevOpsOrganizationBox.Text.Trim();
        _settings.AzureDevOps.Project = AzureDevOpsProjectBox.Text.Trim();
        _settings.AzureDevOps.InProgressStates = AzureDevOpsStatesBox.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        _settings.AzureDevOps.WorkItemTypes = AzureDevOpsWorkItemTypesBox.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (int.TryParse(AzureDevOpsPollIntervalBox.Text, out var pollInterval) && pollInterval > 0)
            _settings.AzureDevOps.PollIntervalSeconds = pollInterval;

        _settingsService.Save(_settings);
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
