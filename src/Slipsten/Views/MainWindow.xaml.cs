using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Slipsten.Data;
using Slipsten.TimeTracking;

namespace Slipsten.Views;

public partial class MainWindow : Window
{
    private readonly SlipstenContext _db;
    private readonly TimeTracker _tracker;
    private readonly DispatcherQueue _dispatcherQueue;
    private DateTime _currentDate = DateTime.Today;

    public MainWindow(SlipstenContext db, TimeTracker tracker)
    {
        InitializeComponent();
        _db = db;
        _tracker = tracker;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(620, 500));
        WindowHelper.SetAppIcon(this);
        CenterOnScreen();

        _tracker.StateChanged += OnTrackerStateChanged;
        Closed += OnWindowClosed;

        UpdateStartStopButton();
        LoadEntries();
    }

    private void CenterOnScreen()
    {
        var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
            AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
        var workArea = displayArea.WorkArea;
        AppWindow.Move(new Windows.Graphics.PointInt32(
            (workArea.Width - 620) / 2 + workArea.X,
            (workArea.Height - 500) / 2 + workArea.Y));
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _tracker.StateChanged -= OnTrackerStateChanged;
    }

    private void OnTrackerStateChanged(object? sender, EventArgs e)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            UpdateStartStopButton();
            LoadEntries();
        });
    }

    private void UpdateStartStopButton()
    {
        StartStopButton.Content = _tracker.IsRunning ? "■ Stop" : "▶ Start";
    }

    private void StartStop_Click(object sender, RoutedEventArgs e)
    {
        if (_tracker.IsRunning)
            _tracker.Stop(string.Empty);
        else
            _tracker.Start(WorkType.Ordinary);
    }

    private void LoadEntries()
    {
        DateLabel.Text = _currentDate.ToString("dddd, d MMMM yyyy");
        _db.ChangeTracker.Clear();

        var entries = _db.TimeEntries
            .Where(e => e.StartTime.Date == _currentDate)
            .OrderBy(e => e.StartTime)
            .ToList()
            .Select(e => new EntryRow(e))
            .ToList();

        EntryList.ItemsSource = entries;
        EditPanel.Visibility = Visibility.Collapsed;

        var total = entries.Aggregate(TimeSpan.Zero, (a, r) => a + r.Entry.Duration);
        DayTotal.Text = $"Total: {(int)total.TotalHours}h {total.Minutes:D2}m";
    }

    private void PrevDay_Click(object sender, RoutedEventArgs e)
    {
        _currentDate = _currentDate.AddDays(-1);
        LoadEntries();
    }

    private void NextDay_Click(object sender, RoutedEventArgs e)
    {
        _currentDate = _currentDate.AddDays(1);
        LoadEntries();
    }

    private void Today_Click(object sender, RoutedEventArgs e)
    {
        _currentDate = DateTime.Today;
        LoadEntries();
    }

    private void DoublePay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.DataContext is EntryRow row)
        {
            row.Entry.Type = cb.IsChecked == true ? WorkType.DoublePay : WorkType.Ordinary;
            _db.SaveChanges();
        }
    }

    private void EntryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EntryList.SelectedItem is EntryRow row)
        {
            StartBox.Text = row.Entry.StartTime.ToString("HH:mm");
            EndBox.Text = row.Entry.EndTime?.ToString("HH:mm") ?? "";
            EditPanel.Visibility = Visibility.Visible;
        }
        else
        {
            EditPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void SaveEdit_Click(object sender, RoutedEventArgs e)
    {
        if (EntryList.SelectedItem is not EntryRow row) return;

        if (TimeSpan.TryParse(StartBox.Text, out var startTime))
            row.Entry.StartTime = _currentDate + startTime;
        if (!string.IsNullOrWhiteSpace(EndBox.Text) && TimeSpan.TryParse(EndBox.Text, out var endTime))
            row.Entry.EndTime = _currentDate + endTime;

        _db.SaveChanges();
        LoadEntries();
    }
    private async void DeleteEntry_Click(object sender, RoutedEventArgs e)
    {
        if (EntryList.SelectedItem is not EntryRow row) return;

        var dialog = new ContentDialog
        {
            Title = "Delete time slice?",
            Content = $"{row.Start} – {row.End} ({row.Duration})",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _db.TimeEntries.Remove(row.Entry);
            _db.SaveChanges();
            LoadEntries();
        }
    }
}

public class EntryRow
{
    public TimeEntry Entry { get; }

    public EntryRow(TimeEntry entry) => Entry = entry;

    public string Start => Entry.StartTime.ToString("HH:mm");
    public string End => Entry.EndTime?.ToString("HH:mm") ?? "running…";
    public string Duration
    {
        get
        {
            var d = Entry.Duration;
            return $"{(int)d.TotalHours}h {d.Minutes:D2}m";
        }
    }
    public bool IsDoublePay => Entry.Type == WorkType.DoublePay;
    public string Description => Entry.Description;
}
