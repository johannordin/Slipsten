using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Slipsten.Settings;
using Slipsten.Data;

namespace Slipsten.Views;

public partial class SettingsWindow : Window
{
    private const int WindowWidth = 750;
    private const int WindowHeight = 500;
    private readonly SettingsService _settingsService;
    private readonly SlipstenContext _context;
    private readonly Action? _settingsChanged;
    private AppSettings _settings;

    public SettingsWindow(SettingsService settingsService, SlipstenContext context, Action? settingsChanged = null)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _context = context;
        _settingsChanged = settingsChanged;
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
        DbPathBox.Text = _settings.DatabasePath;
        UpdateResolvedPath();
        IdleThresholdBox.Text = _settings.IdleThresholdSeconds.ToString();
        PhoneAlertsEnabledCheck.IsChecked = _settings.PhoneAlerts.Enabled;
        PhoneAlertProviderBox.Text = _settings.PhoneAlerts.Provider;
        NtfyTopicBox.Text = _settings.PhoneAlerts.NtfyTopic;
        NtfyServerUrlBox.Text = _settings.PhoneAlerts.NtfyServerUrl;
        NtfyAccessTokenBox.Text = _settings.PhoneAlerts.NtfyAccessToken;
    }

    private void UpdateResolvedPath()
    {
        var path = string.IsNullOrWhiteSpace(DbPathBox.Text)
            ? SlipstenContext.GetDefaultPath()
            : DbPathBox.Text.Trim();
        ResolvedPathText.Text = path;
    }

    private async void BrowseDbPath_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker();
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
        picker.FileTypeChoices.Add("SQLite Database", [".db"]);
        picker.SuggestedFileName = "slipsten";

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        if (file != null)
        {
            DbPathBox.Text = file.Path;
            UpdateResolvedPath();
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(IdleThresholdBox.Text, out var idle))
            _settings.IdleThresholdSeconds = idle;
        _settings.PhoneAlerts.Enabled = PhoneAlertsEnabledCheck.IsChecked == true;
        _settings.PhoneAlerts.Provider = string.IsNullOrWhiteSpace(PhoneAlertProviderBox.Text)
            ? "ntfy"
            : PhoneAlertProviderBox.Text.Trim();
        _settings.PhoneAlerts.NtfyTopic = NtfyTopicBox.Text.Trim();
        _settings.PhoneAlerts.NtfyServerUrl = string.IsNullOrWhiteSpace(NtfyServerUrlBox.Text)
            ? "https://ntfy.sh"
            : NtfyServerUrlBox.Text.Trim();
        _settings.PhoneAlerts.NtfyAccessToken = NtfyAccessTokenBox.Text.Trim();

        var newDbPath = DbPathBox.Text.Trim();
        var dbPathChanged = newDbPath != _settings.DatabasePath;
        _settings.DatabasePath = newDbPath;

        _settingsService.Save(_settings);
        _settingsChanged?.Invoke();

        if (dbPathChanged)
        {
            var dialog = new ContentDialog
            {
                Title = "Restart required",
                Content = "The database path has changed. Please restart Slipsten for this to take effect.",
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot
            };
            await dialog.ShowAsync();
        }

        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var importWindow = new ImportWindow(_context);
        importWindow.Activate();
    }
}
