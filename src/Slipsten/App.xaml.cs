using Microsoft.EntityFrameworkCore;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Slipsten.AzureDevOps;
using Slipsten.CliWidgets;
using Slipsten.Data;
using Slipsten.GitHub;
using Slipsten.Notifications;
using Slipsten.Settings;
using Slipsten.TimeTracking;
using Slipsten.Views;
using Windows.Graphics;

namespace Slipsten;

public partial class App : Application
{
    private SlipstenContext? _db;
    private TimeTracker? _tracker;
    private IdleDetector? _idleDetector;
    private PhoneAlertService? _phoneAlertService;
    private GitHubMonitor? _gitHubMonitor;
    private AzureDevOpsMonitor? _azureDevOpsMonitor;
    private CliWidgetHost? _widgetHost;
    private SettingsService? _settingsService;
    private MainWindow? _mainWindow;
    private FloatingBarWindow? _floatingBar;
    private DispatcherQueue? _dispatcherQueue;
    private DispatcherQueueTimer? _phoneAlertTimer;
    private Window? _sentinel;
    private bool _isExiting;

    private DateTime _idleStartedAt;
    private WorkPromptWindow? _activePrompt;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        UnhandledException += (_, e) =>
        {
            CrashLogger.Log("App.UnhandledException", e.Exception);
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                CrashLogger.Log("AppDomain.UnhandledException", ex);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLogger.Log("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        try
        {
            Boot();
        }
        catch (Exception ex)
        {
            CrashLogger.Log("App.Boot", ex);
            var errorWindow = new Window { Title = "Slipsten — Startup Error" };
            errorWindow.Content = new Microsoft.UI.Xaml.Controls.TextBlock
            {
                Text = $"Slipsten failed to start:\n\n{ex}",
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                Margin = new Thickness(20)
            };
            errorWindow.Activate();
        }
    }

    private void Boot()
    {
        // Keep a hidden window alive so the process doesn't exit when visible windows close
        _sentinel = new Window { Title = "Slipsten" };
        WindowHelper.SetAppIcon(_sentinel);
        _sentinel.AppWindow.Hide();

        _settingsService = new SettingsService();
        var settings = _settingsService.Load();

        if (!string.IsNullOrWhiteSpace(settings.DatabasePath))
            SlipstenContext.ConfiguredPath = settings.DatabasePath;

        _db = new SlipstenContext();
        _db.Database.Migrate();

        RegisterStartupWithWindows();

        _tracker = new TimeTracker();
        _phoneAlertService = new PhoneAlertService(_tracker, _settingsService);
        _gitHubMonitor = new GitHubMonitor(settings);
        _azureDevOpsMonitor = new AzureDevOpsMonitor(settings);

        _tracker.StateChanged += (_, _) =>
        {
            try { PersistTrackingState(); }
            catch (Exception ex) { CrashLogger.Log("App.PersistTrackingState", ex); }
        };

        // If the user was actively tracking when the app last closed/crashed,
        // check how long ago and ask about the gap
        if (settings.WasTracking)
        {
            var lastEntry = _db.TimeEntries
                .OrderByDescending(e => e.Id)
                .FirstOrDefault();
            var lastEndTime = lastEntry?.EndTime ?? settings.LastCloseTime;

            if (lastEndTime.HasValue)
            {
                var gap = DateTime.Now - lastEndTime.Value;
                if (gap.TotalMinutes >= 1)
                {
                    ShowIdleReturnPrompt(gap);
                }
                else
                {
                    _tracker.ResumeLastEntry();
                }
            }
            else
            {
                _tracker.Start(WorkType.Ordinary);
            }
        }
        else
        {
            DateTime? lastKnown = settings.LastCloseTime;
            var lastEntry = _db.TimeEntries
                .OrderByDescending(e => e.Id)
                .FirstOrDefault();
            if (lastEntry?.EndTime > lastKnown)
                lastKnown = lastEntry.EndTime;

            if (lastKnown.HasValue)
            {
                var elapsed = DateTime.Now - lastKnown.Value;
                if (elapsed.TotalMinutes >= 1)
                {
                    ShowIdleReturnPrompt(elapsed);
                }
                else
                {
                    ShowStartPrompt();
                }
            }
            else
            {
                ShowStartPrompt();
            }
        }

        _idleDetector = new IdleDetector(settings.IdleThresholdSeconds);

        _idleDetector.WentIdle += (_, _) =>
        {
            try
            {
                _idleStartedAt = DateTime.Now - _idleDetector.GetIdleTime();
                _tracker.PauseAt(_idleStartedAt);
                _gitHubMonitor?.SetUserActive(false);
                _azureDevOpsMonitor?.SetUserActive(false);
                _phoneAlertTimer?.Stop();
            }
            catch (Exception ex) { CrashLogger.Log("App.WentIdle", ex); }
        };

        _idleDetector.Returned += (_, _) =>
        {
            try
            {
                var idleTime = DateTime.Now - _idleStartedAt;
                _gitHubMonitor?.SetUserActive(true);
                _azureDevOpsMonitor?.SetUserActive(true);
                _phoneAlertTimer?.Start();
                QueuePhoneAlertCheck();
                _dispatcherQueue!.TryEnqueue(() => ShowIdleReturnPrompt(idleTime));
            }
            catch (Exception ex) { CrashLogger.Log("App.Returned", ex); }
        };

        // Load CLI widget host
        var widgetsConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Slipsten", "widgets.json");
        try
        {
            _widgetHost = CliWidgetHost.LoadFrom(widgetsConfigPath);
        }
        catch (Exception ex)
        {
            CrashLogger.Log("App.LoadCliWidgetHost", ex);
            _widgetHost = null;
        }

        _floatingBar = new FloatingBarWindow(
            _tracker!,
            OpenMainWindow,
            OpenMonthlySummaryWindow,
            OpenSettingsWindow,
            ExitApp,
            GetSavedFloatingBarPosition(settings),
            _widgetHost);
        _floatingBar.Activate();

        _phoneAlertTimer = _dispatcherQueue!.CreateTimer();
        _phoneAlertTimer.Interval = TimeSpan.FromHours(1);
        _phoneAlertTimer.Tick += (_, _) => QueuePhoneAlertCheck();
        _phoneAlertTimer.Start();

        _idleDetector.Start();
        _gitHubMonitor.Start();
        _azureDevOpsMonitor.Start();
        _widgetHost?.StartAll();
        QueuePhoneAlertCheck();
    }

    private void QueuePhoneAlertCheck()
    {
        try { _ = CheckPhoneAlertsAsync(); }
        catch (Exception ex) { CrashLogger.Log("App.QueuePhoneAlertCheck", ex); }
    }

    private async Task CheckPhoneAlertsAsync()
    {
        if (_phoneAlertService == null)
            return;

        try
        {
            await _phoneAlertService.CheckAndSendMonthlyReminderAsync();
        }
        catch (Exception ex)
        {
            CrashLogger.Log("App.CheckPhoneAlertsAsync", ex);
        }
    }

    private void ShowStartPrompt()
    {
        if (_activePrompt != null) return;

        var prompt = new WorkPromptWindow(
            "You're not timing anything, want to start?", PromptMode.Start);
        _activePrompt = prompt;
        prompt.Closed += (_, _) =>
        {
            _activePrompt = null;
            if (prompt.Result == PromptResult.Start)
                _tracker!.Start(WorkType.Ordinary);
        };
        prompt.Activate();
    }

    private void ShowIdleReturnPrompt(TimeSpan awayTime)
    {
        if (_activePrompt != null)
        {
            // Update the existing prompt with the current away time and bring it to front
            _activePrompt.UpdateMessage(
                $"You're back! What do you want to do with the {FormatTime(awayTime)} that you were gone?");
            _activePrompt.Activate();
            return;
        }

        var prompt = new WorkPromptWindow(
            $"You're back! What do you want to do with the {FormatTime(awayTime)} that you were gone?",
            PromptMode.IdleReturn);
        _activePrompt = prompt;
        prompt.Closed += (_, _) =>
        {
            _activePrompt = null;
            if (prompt.Result == PromptResult.KeepContinue)
            {
                // Re-open the last entry so the idle gap is included in the same slice
                _tracker!.ResumeLastEntry();
            }
            else if (prompt.Result == PromptResult.Discard)
            {
                // Start a fresh slice from now
                _tracker!.Start(WorkType.Ordinary);
            }
        };
        prompt.Activate();
    }

    private void OpenMainWindow()
    {
        if (_mainWindow != null)
        {
            _mainWindow.Activate();
            return;
        }
        _mainWindow = new MainWindow(
            _db!,
            _tracker!);
        _mainWindow.Closed += (_, _) => _mainWindow = null;
        _mainWindow.Activate();
    }

    private void OpenMonthlySummaryWindow()
    {
        var win = new MonthlySummaryWindow(_tracker!);
        win.Activate();
    }

    private void OpenSettingsWindow()
    {
        var win = new SettingsWindow(_settingsService!, _db!, () =>
        {
            var updated = _settingsService!.Load();
            _idleDetector!.UpdateThreshold(updated.IdleThresholdSeconds);
            _gitHubMonitor!.Configure(updated);
            _azureDevOpsMonitor!.Configure(updated);
        });
        win.Closed += (_, _) =>
        {
            var updated = _settingsService!.Load();
            _idleDetector!.UpdateThreshold(updated.IdleThresholdSeconds);
            _gitHubMonitor!.Configure(updated);
            _azureDevOpsMonitor!.Configure(updated);
        };
        win.Activate();
    }

    private void OpenGitHubSettingsWindow()
    {
        var window = new GitHubSettingsWindow(_settingsService!);
        window.Closed += (_, _) =>
        {
            var updated = _settingsService!.Load();
            _gitHubMonitor!.Configure(updated);
        };
        window.Activate();
    }

    private void OpenAzureDevOpsSettingsWindow()
    {
        var window = new AzureDevOpsSettingsWindow(_settingsService!);
        window.Closed += (_, _) =>
        {
            var updated = _settingsService!.Load();
            _azureDevOpsMonitor!.Configure(updated);
        };
        window.Activate();
    }

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1
            ? $"{(int)t.TotalHours}h {t.Minutes:D2}m"
            : $"{t.Minutes}m";

    private static PointInt32? GetSavedFloatingBarPosition(AppSettings settings)
    {
        if (!settings.FloatingBarX.HasValue || !settings.FloatingBarY.HasValue)
            return null;

        return new PointInt32(settings.FloatingBarX.Value, settings.FloatingBarY.Value);
    }

    private void PersistTrackingState()
    {
        if (_settingsService == null) return;
        var settings = _settingsService.Load();
        settings.WasTracking = _tracker?.IsRunning ?? false;
        _settingsService.Save(settings);
    }

    private static void RegisterStartupWithWindows()
    {
        if (IsPackaged())
            return;

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
            key?.SetValue("Slipsten", $"\"{Environment.ProcessPath}\"");
        }
        catch (Exception ex)
        {
            CrashLogger.Log("App.RegisterStartupWithWindows", ex);
        }
    }

    private static bool IsPackaged()
    {
        try
        {
            _ = Windows.ApplicationModel.Package.Current;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void ExitApp()
    {
        if (_isExiting)
            return;

        _isExiting = true;
        var wasTracking = _tracker?.IsRunning ?? false;

        _phoneAlertTimer?.Stop();
        _idleDetector?.Dispose();
        _floatingBar?.Cleanup();
        _gitHubMonitor?.Dispose();
        _azureDevOpsMonitor?.Dispose();
        _widgetHost?.StopAll();
        _widgetHost?.Dispose();

        _tracker?.Stop(string.Empty);
        _tracker?.Dispose();

        if (_settingsService != null)
        {
            var settings = _settingsService.Load();
            var floatingBarPosition = _floatingBar?.GetCurrentPosition();
            settings.FloatingBarX = floatingBarPosition?.X;
            settings.FloatingBarY = floatingBarPosition?.Y;
            settings.LastCloseTime = DateTime.Now;
            settings.WasTracking = wasTracking;
            _settingsService.Save(settings);
        }

        _phoneAlertService?.Dispose();
        _db?.Dispose();
        _sentinel?.Close();
        Exit();
    }
}

