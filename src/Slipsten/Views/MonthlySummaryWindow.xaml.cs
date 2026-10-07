using Microsoft.UI.Xaml;
using Slipsten.TimeTracking;

namespace Slipsten.Views;

public partial class MonthlySummaryWindow : Window
{
    private readonly Func<int, int, List<DailySummary>> _summaryProvider;
    private int _year;
    private int _month;

    public MonthlySummaryWindow(TimeTracker tracker)
        : this(tracker.GetMonthlySummary)
    {
    }

    public MonthlySummaryWindow(Func<int, int, List<DailySummary>> summaryProvider)
    {
        InitializeComponent();
        _summaryProvider = summaryProvider;
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

        var rows = _summaryProvider(_year, _month)
            .Select(d => new SummaryRow(d))
            .ToList();

        SummaryList.ItemsSource = rows;

        var totalRounded = rows.Aggregate(TimeSpan.Zero, (a, r) => a + r.Source.TotalRounded);
        var ordinary = rows.Aggregate(TimeSpan.Zero, (a, r) => a + r.Source.Ordinary);
        var doublePay = rows.Aggregate(TimeSpan.Zero, (a, r) => a + r.Source.DoublePay);
        var workDays = SwedishWorkCalendar.GetWorkingDayCount(_year, _month);
        var workMonth = TimeSpan.FromHours(workDays * 8);

        TotalLabel.Text = $"Rounded: {FormatFooterHours(totalRounded)} ({FormatPercentage(totalRounded, workMonth)})  |  " +
                          $"1x: {FormatFooterHours(ordinary)}  |  " +
                          $"2x: {FormatFooterHours(doublePay)}  |  " +
                          FormatPrediction(totalRounded, workDays, workMonth);
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

    private string FormatPrediction(TimeSpan totalRounded, int workDays, TimeSpan workMonth)
    {
        var elapsedWorkDays = SwedishWorkCalendar.GetElapsedWorkingDayCount(
            _year,
            _month,
            DateTime.Today);

        if (elapsedWorkDays == 0)
            return "Predicted: —";

        var projected = TimeSpan.FromMinutes(
            totalRounded.TotalMinutes / elapsedWorkDays * workDays);
        return $"Predicted: {FormatFooterHours(projected)} ({FormatPercentage(projected, workMonth)})";
    }

    private static string FormatFooterHours(TimeSpan time) =>
        time.Minutes == 0
            ? $"{(int)time.TotalHours}h"
            : $"{(int)time.TotalHours}h {time.Minutes:D2}m";

    private static string FormatPercentage(TimeSpan value, TimeSpan target)
    {
        if (target == TimeSpan.Zero)
            return "0%";

        return $"{Math.Round(value.TotalMinutes / target.TotalMinutes * 100, MidpointRounding.AwayFromZero):0}%";
    }
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
