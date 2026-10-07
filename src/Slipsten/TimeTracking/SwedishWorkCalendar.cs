namespace Slipsten.TimeTracking;

public static class SwedishWorkCalendar
{
    public static int GetWorkingDayCount(int year, int month) =>
        CountWorkingDays(year, month, new DateTime(year, month, 1).AddMonths(1).AddDays(-1));

    public static int GetElapsedWorkingDayCount(int year, int month, DateTime date)
    {
        var firstDay = new DateTime(year, month, 1);
        if (date.Date < firstDay)
            return 0;

        var lastDay = firstDay.AddMonths(1).AddDays(-1);
        return CountWorkingDays(year, month, date.Date > lastDay ? lastDay : date.Date);
    }

    public static bool IsWorkingDay(DateTime date) =>
        date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday &&
        !GetPublicHolidays(date.Year).Contains(date.Date);

    private static int CountWorkingDays(int year, int month, DateTime through)
    {
        var count = 0;

        for (var day = new DateTime(year, month, 1); day <= through; day = day.AddDays(1))
        {
            if (IsWorkingDay(day))
                count++;
        }

        return count;
    }

    private static HashSet<DateTime> GetPublicHolidays(int year)
    {
        var easterSunday = GetEasterSunday(year);

        return
        [
            new DateTime(year, 1, 1),
            new DateTime(year, 1, 6),
            easterSunday.AddDays(-2),
            easterSunday.AddDays(1),
            new DateTime(year, 5, 1),
            easterSunday.AddDays(39),
            new DateTime(year, 6, 6),
            GetMidsummerDay(year),
            GetAllSaintsDay(year),
            new DateTime(year, 12, 25),
            new DateTime(year, 12, 26)
        ];
    }

    private static DateTime GetMidsummerDay(int year)
    {
        var date = new DateTime(year, 6, 20);
        while (date.DayOfWeek != DayOfWeek.Saturday)
            date = date.AddDays(1);

        return date;
    }

    private static DateTime GetAllSaintsDay(int year)
    {
        var date = new DateTime(year, 10, 31);
        while (date.DayOfWeek != DayOfWeek.Saturday)
            date = date.AddDays(1);

        return date;
    }

    private static DateTime GetEasterSunday(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = (19 * a + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + 2 * e + 2 * i - h - k) % 7;
        var m = (a + 11 * h + 22 * l) / 451;
        var month = (h + l - 7 * m + 114) / 31;
        var day = (h + l - 7 * m + 114) % 31 + 1;

        return new DateTime(year, month, day);
    }
}
