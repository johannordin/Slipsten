using Microsoft.UI.Xaml;
using Slipsten.TimeTracking;

namespace Slipsten.Views;

public partial class MonthlySummaryWindow : Window
{
    private readonly TimeTracker _tracker;
    private int _year;
    private int _month;

    public MonthlySummaryWindow(TimeTracker tracker)
    {
        InitializeComponent();
        _tracker = tracker;
        _year = DateTime.Today.Year;
        _month = DateTime.Today.Month;

        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 480));
        WindowHelper.SetAppIcon(this);
        CenterOnScreen();

        Refresh();
    }

    private void CenterOnScreen()
    {
        var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
            AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
        var workArea = displayArea.WorkArea;
        AppWindow.Move(new Windows.Graphics.PointInt32(
            (workArea.Width - 560) / 2 + workArea.X,
            (workArea.Height - 480) / 2 + workArea.Y));
    }

    private void Refresh()
    {
        MonthLabel.Text = new DateTime(_year, _month, 1).ToString("MMMM yyyy");

        var rows = _tracker.GetMonthlySummary(_year, _month)
            .Select(d => new SummaryRow(d))
            .ToList();

        SummaryList.ItemsSource = rows;

        var totalRounded = rows.Aggregate(TimeSpan.Zero, (a, r) => a + r.Source.TotalRounded);
        TotalLabel.Text = $"Total (rounded): {FormatHours(totalRounded)}  |  " +
                          $"Ordinary: {FormatHours(rows.Aggregate(TimeSpan.Zero, (a, r) => a + r.Source.Ordinary))}  |  " +
                          $"Double Pay: {FormatHours(rows.Aggregate(TimeSpan.Zero, (a, r) => a + r.Source.DoublePay))}";
    }

    private void PrevMonth_Click(object sender, RoutedEventArgs e)
    {
        var d = new DateTime(_year, _month, 1).AddMonths(-1);
        _year = d.Year; _month = d.Month;
        Refresh();
    }

    private void NextMonth_Click(object sender, RoutedEventArgs e)
    {
        var d = new DateTime(_year, _month, 1).AddMonths(1);
        _year = d.Year; _month = d.Month;
        Refresh();
    }

    private static string FormatHours(TimeSpan t) =>
        $"{(int)t.TotalHours}h {t.Minutes:D2}m";
}

public class SummaryRow
{
    public DailySummary Source { get; }
    public DateTime Date => Source.Date;
    public string DateFormatted => Source.Date.ToString("ddd dd MMM");
    public string OrdinaryFormatted => FormatHours(Source.Ordinary);
    public string DoublePayFormatted => FormatHours(Source.DoublePay);
    public string TotalFormatted => FormatHours(Source.Total);
    public string TotalRoundedFormatted => FormatHours(Source.TotalRounded);

    public SummaryRow(DailySummary source) => Source = source;

    private static string FormatHours(TimeSpan t) =>
        t == TimeSpan.Zero ? "—" : $"{(int)t.TotalHours}h {t.Minutes:D2}m";
}
