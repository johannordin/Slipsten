using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
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
    private readonly InputNonClientPointerSource _nonClientPointerSource;
    private readonly Dictionary<string, (Button button, TextBlock text, string tooltip, string badgeTextTemplate)> _widgetButtons = [];
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
        _nonClientPointerSource = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
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
        ContentGrid.SizeChanged += (_, _) => UpdateTitleBarInputRegions();
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
        ContentPanel.InvalidateMeasure();
        ContentPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desiredSize = ContentPanel.DesiredSize;

        var scale = Content.XamlRoot?.RasterizationScale ?? 1;
        var xamlRootWidth = Content.XamlRoot?.Size.Width ?? desiredSize.Width;
        var frameWidth = Math.Max(0, (AppWindow.Size.Width / scale) - xamlRootWidth);
        var width = Math.Max(
            1,
            (int)Math.Ceiling((desiredSize.Width + frameWidth) * scale));

        if (width == _barWidthPx)
            return;

        var position = AppWindow.Position;
        var rightEdge = position.X + AppWindow.Size.Width;
        _barWidthPx = width;
        AppWindow.Resize(new SizeInt32(width, BarHeightPx));
        Position(new PointInt32(rightEdge - width, position.Y));
    }

    private void UpdateTitleBarInputRegions()
    {
        var xamlRoot = Content.XamlRoot;
        if (xamlRoot == null || ContentGrid.ActualWidth <= 0 || ContentGrid.ActualHeight <= 0)
            return;

        var scale = xamlRoot.RasterizationScale;
        var origin = ContentGrid.TransformToVisual(null).TransformPoint(new Point(0, 0));
        var region = new RectInt32(
            (int)Math.Floor(origin.X * scale),
            (int)Math.Floor(origin.Y * scale),
            (int)Math.Ceiling(ContentGrid.ActualWidth * scale),
            (int)Math.Ceiling(ContentGrid.ActualHeight * scale));

        _nonClientPointerSource.SetRegionRects(NonClientRegionKind.Passthrough, [region]);
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
        var text = new TextBlock
        {
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Text = "0"
        };
        text.Translation = new Vector3(0, -1, 0);

        var backgroundColor = widget.Badge?.Color?.Trim() ?? string.Empty;
        var textColor = widget.Badge?.TextColor?.Trim() ?? string.Empty;
        var hasBackground = !string.IsNullOrWhiteSpace(backgroundColor);
        if (!string.IsNullOrWhiteSpace(textColor))
            text.Foreground = new SolidColorBrush(ParseColor(textColor));

        var button = new Button
        {
            VerticalAlignment = VerticalAlignment.Center,
            Background = hasBackground
                ? new SolidColorBrush(ParseColor(backgroundColor))
                : new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = hasBackground ? new CornerRadius(4) : new CornerRadius(0),
            Padding = hasBackground ? new Thickness(6, 1, 6, 1) : new Thickness(2, 1, 2, 1),
            MinWidth = 0,
            MinHeight = 0,
            UseSystemFocusVisuals = false,
            Content = text
        };
        ToolTipService.SetToolTip(button, widget.Tooltip);

        // Store references
        _widgetButtons[widget.Id] = (
            button,
            text,
            widget.Tooltip,
            widget.Badge?.Text ?? string.Empty);

        // Create flyout
        if (widget.Flyout != null)
        {
            var flyout = new Flyout
            {
                ShouldConstrainToRootBounds = false,
                FlyoutPresenterStyle = new Style(typeof(FlyoutPresenter))
                {
                    Setters =
                    {
                        new Setter(FlyoutPresenter.PaddingProperty, new Thickness(0))
                    }
                }
            };
            var flyoutPanel = new StackPanel
            {
                Width = 360,
                Spacing = 0,
                Padding = new Thickness(6)
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
            Margin = new Thickness(2, 0, 2, 4)
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
                var itemsPanel = new StackPanel
                {
                    Width = 348,
                    Spacing = 1
                };
                var groupedItems = items.GroupBy(item => item.Group ?? string.Empty);
                var showGroupHeaders = !string.IsNullOrWhiteSpace(flyoutDef.GroupBy);

                foreach (var group in groupedItems)
                {
                    if (showGroupHeaders)
                    {
                        itemsPanel.Children.Add(new TextBlock
                        {
                            Text = group.Key,
                            FontSize = 12,
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                            Opacity = 0.7,
                            Margin = new Thickness(6, itemsPanel.Children.Count == 0 ? 0 : 6, 6, 2)
                        });
                    }

                    foreach (var item in group)
                    {
                        var itemText = new TextBlock
                        {
                            Text = item.DisplayText,
                            TextWrapping = TextWrapping.WrapWholeWords,
                            TextTrimming = TextTrimming.WordEllipsis,
                            MaxLines = 2,
                            VerticalAlignment = VerticalAlignment.Center
                        };

                        var button = new Button
                        {
                            Content = itemText,
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            HorizontalContentAlignment = HorizontalAlignment.Stretch,
                            Background = new SolidColorBrush(Colors.Transparent),
                            BorderThickness = new Thickness(0),
                            Padding = new Thickness(6, 4, 6, 4),
                            CornerRadius = new CornerRadius(4),
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
                }

                panel.Children.Add(new ScrollViewer
                {
                    Content = itemsPanel,
                    MaxHeight = 360,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    HorizontalScrollMode = ScrollMode.Disabled,
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

        ResizeToContent();
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
