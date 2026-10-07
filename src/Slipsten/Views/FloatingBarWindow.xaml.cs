using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Slipsten.CliWidgets;
using Slipsten.Data;
using Slipsten.Settings;
using Slipsten.TimeTracking;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace Slipsten.Views;

public partial class FloatingBarWindow : Window
{
    private const int BarHeightPx = 32;
    private const int BarChromeWidthPx = 136; // start/stop + time + menu + padding
    private const int BarWidgetWidthPx = 34;  // per widget button
    private const int BarTopOffset = 50;
    private const int BarRightOffset = 50;
    private readonly TimeTracker _tracker;
    private readonly Action _openMainWindow;
    private readonly Action _openSummaryWindow;
    private readonly Action _openSettingsWindow;
    private readonly Action _exitApp;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherQueueTimer _refreshTimer;
    private readonly CliWidgetHost? _widgetHost;
    private readonly Dictionary<string, (Button button, Border circle, TextBlock text, string tooltip)> _widgetButtons = [];
    private bool _isCleaningUp;
    private PointInt32? _lastKnownPosition;

    public FloatingBarWindow(
        TimeTracker tracker,
        Action openMainWindow,
        Action openSummaryWindow,
        Action openSettingsWindow,
        Action exitApp,
        PointInt32? initialPosition,
        CliWidgetHost? widgetHost = null)
    {
        InitializeComponent();
        _tracker = tracker;
        _openMainWindow = openMainWindow;
        _openSummaryWindow = openSummaryWindow;
        _openSettingsWindow = openSettingsWindow;
        _exitApp = exitApp;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _widgetHost = widgetHost;

        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        WindowHelper.SetAppIcon(this);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(ComputeBarWidth(), BarHeightPx));
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

        if (_widgetHost != null)
        {
            _widgetHost.WidgetStateChanged += OnWidgetStateChanged;
            InitializeWidgets();
        }

        _refreshTimer = _dispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromSeconds(30);
        _refreshTimer.Tick += (_, _) => SafeUpdateUI();
        _refreshTimer.Start();

        SafeUpdateUI();
    }

    private void Position(PointInt32? desiredPosition)
    {
        var displayArea = DisplayArea.GetFromWindowId(
            AppWindow.Id, DisplayAreaFallback.Nearest);
        var workArea = displayArea.WorkArea;

        var barWidth = AppWindow.Size.Width;
        if (desiredPosition.HasValue)
        {
            var x = desiredPosition.Value.X;
            var y = desiredPosition.Value.Y;
            var maxX = workArea.X + Math.Max(0, workArea.Width - barWidth);
            var maxY = workArea.Y + Math.Max(0, workArea.Height - BarHeightPx);
            x = Math.Clamp(x, workArea.X, maxX);
            y = Math.Clamp(y, workArea.Y, maxY);
            AppWindow.Move(new PointInt32(x, y));
            _lastKnownPosition = new PointInt32(x, y);
            return;
        }

        var topRight = new PointInt32(
            workArea.Width - barWidth - BarRightOffset + workArea.X,
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
            sender.Resize(new SizeInt32(ComputeBarWidth(), BarHeightPx));

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

    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        // Flyout opens via Button.Flyout
    }

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

    private int ComputeBarWidth()
    {
        var widgetCount = _widgetHost?.Widgets.Count ?? 0;
        return BarChromeWidthPx + widgetCount * BarWidgetWidthPx;
    }

    private void InitializeWidgets()
    {
        if (_widgetHost == null)
            return;

        WidgetsPanel.Items.Clear();
        _widgetButtons.Clear();

        foreach (var widget in _widgetHost.Widgets)
        {
            var button = CreateWidgetButton(widget);
            WidgetsPanel.Items.Add(button);

            var runner = _widgetHost.GetRunner(widget.Id);
            if (runner != null)
            {
                UpdateWidgetVisual(widget.Id, runner.BadgeCount, runner.HasError, runner.LastError);
            }
            else
            {
                UpdateWidgetVisual(widget.Id, 0, hasError: false, errorMessage: null);
            }
        }

        AppWindow.Resize(new SizeInt32(ComputeBarWidth(), BarHeightPx));
    }

    private Button CreateWidgetButton(CliWidgetDefinition widget)
    {
        var circle = new Border
        {
            Background = new SolidColorBrush(ParseColor(widget.Badge?.Color ?? "#0078d4")),
            CornerRadius = new CornerRadius(10),
            Height = 20,
            MinWidth = 20,
            Padding = new Thickness(5, 0, 5, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var text = new TextBlock
        {
            FontSize = 9,
            Foreground = new SolidColorBrush(Colors.White),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Text = "0"
        };

        circle.Child = text;

        var button = new Button
        {
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            Content = circle
        };

        ToolTipService.SetToolTip(button, widget.Tooltip);

        // Store references
        _widgetButtons[widget.Id] = (button, circle, text, widget.Tooltip);

        // Create flyout
        if (widget.Flyout != null)
        {
            var flyout = new Flyout { ShouldConstrainToRootBounds = false };
            var flyoutPanel = new StackPanel { Spacing = 2, Padding = new Thickness(6), MinWidth = 280 };
            flyout.Content = flyoutPanel;
            
            flyout.Opening += async (_, _) => await BuildWidgetFlyout(widget.Id, flyoutPanel, widget.Flyout);
            button.Flyout = flyout;
            button.Click += (_, _) => { }; // Trigger flyout
        }

        return button;
    }

    private async Task BuildWidgetFlyout(string widgetId, StackPanel panel, FlyoutDefinition flyoutDef)
    {
        panel.Children.Clear();

        var runner = _widgetHost?.GetRunner(widgetId);
        if (runner == null)
            return;

        try
        {
            var items = await runner.GetFlyoutItemsAsync();

            if (items.Count == 0)
            {
                AddFlyoutText(panel, flyoutDef.EmptyMessage);
            }
            else
            {
                foreach (var item in items)
                {
                    var button = new Button
                    {
                        Content = item.DisplayText,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Left,
                        Background = new SolidColorBrush(Colors.Transparent),
                        BorderThickness = new Thickness(0),
                        Padding = new Thickness(0, 6, 0, 6),
                        MinWidth = 0
                    };

                    if (!string.IsNullOrWhiteSpace(item.Url))
                    {
                        var url = item.Url;
                        button.Click += async (_, _) => await OpenExternalUrlAsync(url, "CliWidget.OpenUrl");
                    }
                    else
                    {
                        button.IsEnabled = false;
                    }

                    panel.Children.Add(button);
                }
            }
        }
        catch (Exception ex)
        {
            CrashLogger.Log($"FloatingBar.BuildWidgetFlyout.{widgetId}", ex);
            AddFlyoutText(panel, ex.Message);
        }
    }

    private void OnWidgetStateChanged(object? sender, string widgetId)
    {
        if (_isCleaningUp)
            return;

        _dispatcherQueue.TryEnqueue(() =>
        {
            var runner = _widgetHost?.GetRunner(widgetId);
            if (runner != null)
            {
                UpdateWidgetVisual(widgetId, runner.BadgeCount, runner.HasError, runner.LastError);
            }
        });
    }

    private void UpdateWidgetVisual(string widgetId, int count, bool hasError, string? errorMessage)
    {
        if (!_widgetButtons.TryGetValue(widgetId, out var controls))
            return;

        if (hasError)
        {
            controls.text.Text = "!";
            ToolTipService.SetToolTip(
                controls.button,
                string.IsNullOrWhiteSpace(errorMessage)
                    ? $"{controls.tooltip} (error)"
                    : $"{controls.tooltip}\nError: {errorMessage}");
        }
        else
        {
            controls.text.Text = count > 99 ? "99+" : Math.Max(0, count).ToString();
            ToolTipService.SetToolTip(controls.button, controls.tooltip);
        }
    }

    private static Windows.UI.Color ParseColor(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6)
        {
            var r = Convert.ToByte(hex.Substring(0, 2), 16);
            var g = Convert.ToByte(hex.Substring(2, 2), 16);
            var b = Convert.ToByte(hex.Substring(4, 2), 16);
            return Windows.UI.Color.FromArgb(255, r, g, b);
        }
        return Colors.DodgerBlue;
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
        
        if (_widgetHost != null)
        {
            _widgetHost.WidgetStateChanged -= OnWidgetStateChanged;
        }
        
        AppWindow.Changed -= OnAppWindowChanged;
        _refreshTimer.Stop();
    }
}
