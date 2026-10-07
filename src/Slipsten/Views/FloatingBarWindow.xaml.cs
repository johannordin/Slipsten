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
using System.Numerics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;

namespace Slipsten.Views;

public partial class FloatingBarWindow : Window
{
    private const int BarHeightPx = 32;
    private const int InitialBarWidthPx = 100;
    private const int BarTopOffset = 50;
    private const int BarRightOffset = 50;
    private readonly TimeTracker? _tracker;
    private readonly Action _openMainWindow;
    private readonly Action _openSummaryWindow;
    private readonly Action _openSettingsWindow;
    private readonly Action _exitApp;
    private readonly string? _widgetsConfigPath;
    private readonly TimeSpan? _previewTotal;
    private readonly bool _previewIsRunning;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherQueueTimer _refreshTimer;
    private readonly CliWidgetHost? _widgetHost;
    private readonly Dictionary<string, (Button button, Border circle, TextBlock text, string tooltip, string badgeTextTemplate)> _widgetButtons = [];
    private bool _isCleaningUp;
    private int _barWidthPx;
    private PointInt32? _lastKnownPosition;

    public FloatingBarWindow(
        TimeTracker? tracker,
        Action openMainWindow,
        Action openSummaryWindow,
        Action openSettingsWindow,
        Action exitApp,
        PointInt32? initialPosition,
        CliWidgetHost? widgetHost = null,
        string? widgetsConfigPath = null,
        TimeSpan? previewTotal = null,
        bool previewIsRunning = false)
    {
        InitializeComponent();
        _tracker = tracker;
        _openMainWindow = openMainWindow;
        _openSummaryWindow = openSummaryWindow;
        _openSettingsWindow = openSettingsWindow;
        _exitApp = exitApp;
        _widgetsConfigPath = widgetsConfigPath;
        _previewTotal = previewTotal;
        _previewIsRunning = previewIsRunning;
        TotalTimeText.Translation = new Vector3(0, -1, 0);
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _widgetHost = widgetHost;

        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        WindowHelper.SetAppIcon(this);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(InitialBarWidthPx, BarHeightPx));
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

        if (_tracker != null)
            _tracker.StateChanged += OnTrackerStateChanged;

        if (_widgetHost != null)
        {
            _widgetHost.WidgetStateChanged += OnWidgetStateChanged;
            _widgetHost.ConfigurationReloaded += OnWidgetsConfigurationReloaded;
            InitializeWidgets();
        }

        LayoutRoot.Loaded += (_, _) => ResizeToContent();
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
            sender.Resize(new SizeInt32(
                _barWidthPx == 0 ? InitialBarWidthPx : _barWidthPx,
                BarHeightPx));

            var currentPosition = GetCurrentPosition();
            if (currentPosition.HasValue)
                Position(currentPosition);
        }
    }

    private void UpdateUI()
    {
        if (_isCleaningUp)
            return;

        var isRunning = _previewTotal.HasValue ? _previewIsRunning : _tracker?.IsRunning == true;
        StartStopIcon.Glyph = isRunning ? "\uE71A" : "\uE768";
        var total = _previewTotal ?? _tracker?.TodayTotal() ?? TimeSpan.Zero;
        TotalTimeText.Text = $"{(int)total.TotalHours}:{total.Minutes:D2}";
        ResizeToContent();
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
        if (_tracker == null)
            return;

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

    private async void EditWidgets_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_widgetsConfigPath))
                throw new InvalidOperationException("Widgets configuration path is unavailable.");

            var configFile = await StorageFile.GetFileFromPathAsync(_widgetsConfigPath);
            if (!await Windows.System.Launcher.LaunchFileAsync(configFile))
                throw new InvalidOperationException($"No application is associated with '{_widgetsConfigPath}'.");
        }
        catch (Exception ex)
        {
            CrashLogger.Log("FloatingBar.EditWidgets", ex);
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        _exitApp();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        AppWindow.Hide();
    }

    private void ResizeToContent()
    {
        LayoutRoot.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var scale = Content.XamlRoot?.RasterizationScale ?? 1;
        var width = Math.Max(1, (int)Math.Ceiling(LayoutRoot.DesiredSize.Width * scale));

        if (width == _barWidthPx)
            return;

        _barWidthPx = width;
        AppWindow.Resize(new SizeInt32(width, BarHeightPx));
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

        ResizeToContent();
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
        text.Translation = new Vector3(0, -1, 0);

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
        _widgetButtons[widget.Id] = (
            button,
            circle,
            text,
            widget.Tooltip,
            widget.Badge?.Text ?? string.Empty);

        // Create flyout
        if (widget.Flyout != null)
        {
            var flyout = new Flyout { ShouldConstrainToRootBounds = false };
            var flyoutPanel = new StackPanel
            {
                Width = 360,
                Spacing = 0,
                Padding = new Thickness(8)
            };
            flyout.Content = flyoutPanel;
            
            flyout.Opening += async (_, _) => await BuildWidgetFlyout(
                widget.Id,
                widget.Tooltip,
                flyoutPanel,
                widget.Flyout);
            button.Flyout = flyout;
            button.Click += (_, _) => { }; // Trigger flyout
        }

        return button;
    }

    private async Task BuildWidgetFlyout(
        string widgetId,
        string widgetTooltip,
        StackPanel panel,
        FlyoutDefinition flyoutDef)
    {
        panel.Children.Clear();
        panel.Children.Add(new TextBlock
        {
            Text = widgetTooltip,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(4, 2, 4, 6)
        });

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
                var itemsPanel = new StackPanel { Spacing = 2 };
                foreach (var item in items)
                {
                    var itemText = new TextBlock
                    {
                        Text = item.DisplayText,
                        TextWrapping = TextWrapping.WrapWholeWords,
                        TextTrimming = TextTrimming.WordEllipsis,
                        MaxLines = 2,
                        VerticalAlignment = VerticalAlignment.Center
                    };

                    var row = new Grid();
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.Children.Add(itemText);

                    var openIcon = new SymbolIcon
                    {
                        Symbol = Symbol.OpenFile,
                        Opacity = 0.7,
                        Margin = new Thickness(8, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(openIcon, 1);
                    row.Children.Add(openIcon);

                    var button = new Button
                    {
                        Content = row,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Background = new SolidColorBrush(Colors.Transparent),
                        BorderThickness = new Thickness(0),
                        Padding = new Thickness(8, 7, 8, 7),
                        CornerRadius = new CornerRadius(5),
                        MinWidth = 0
                    };

                    if (!string.IsNullOrWhiteSpace(item.Url))
                    {
                        var url = item.Url;
                        ToolTipService.SetToolTip(button, $"Open {item.DisplayText}");
                        button.Click += async (_, _) => await OpenExternalUrlAsync(url, "CliWidget.OpenUrl");
                    }
                    else
                    {
                        button.IsEnabled = false;
                    }

                    itemsPanel.Children.Add(button);
                }

                panel.Children.Add(new ScrollViewer
                {
                    Content = itemsPanel,
                    MaxHeight = 360,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollMode = ScrollMode.Auto
                });
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

    private void OnWidgetsConfigurationReloaded(object? sender, EventArgs e)
    {
        if (_isCleaningUp)
            return;

        _dispatcherQueue.TryEnqueue(() =>
        {
            InitializeWidgets();
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
            var countText = count > 99 ? "99+" : Math.Max(0, count).ToString();
            controls.text.Text = FormatBadgeText(controls.badgeTextTemplate, countText);
            ToolTipService.SetToolTip(controls.button, controls.tooltip);
        }
    }

    private static string FormatBadgeText(string template, string count)
    {
        if (string.IsNullOrWhiteSpace(template))
            return count;

        if (template.Contains("{array.length}", StringComparison.Ordinal))
            return template.Replace("{array.length}", count, StringComparison.Ordinal);

        if (template.Contains("{value}", StringComparison.Ordinal))
            return template.Replace("{value}", count, StringComparison.Ordinal);

        return template.Contains("{count}", StringComparison.Ordinal)
            ? template.Replace("{count}", count, StringComparison.Ordinal)
            : $"{template.Trim()} {count}";
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
        if (_tracker != null)
            _tracker.StateChanged -= OnTrackerStateChanged;
        
        if (_widgetHost != null)
        {
            _widgetHost.WidgetStateChanged -= OnWidgetStateChanged;
            _widgetHost.ConfigurationReloaded -= OnWidgetsConfigurationReloaded;
        }
        
        AppWindow.Changed -= OnAppWindowChanged;
        _refreshTimer.Stop();
    }
}
