using System.Globalization;
using Slipsten.Settings;
using Slipsten.TimeTracking;

namespace Slipsten.Notifications;

public sealed class PhoneAlertService : IDisposable
{
    private readonly TimeTracker _tracker;
    private readonly SettingsService _settingsService;
    private readonly HttpClient _httpClient;

    public PhoneAlertService(TimeTracker tracker, SettingsService settingsService)
    {
        _tracker = tracker;
        _settingsService = settingsService;
        _httpClient = new HttpClient();
    }

    public async Task CheckAndSendMonthlyReminderAsync()
    {
        var settings = _settingsService.Load();
        var alerts = settings.PhoneAlerts;
        var now = DateTime.Now;

        if (!alerts.Enabled ||
            !string.Equals(alerts.Provider, "ntfy", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(alerts.NtfyTopic))
        {
            return;
        }

        var lastDayOfMonth = DateTime.DaysInMonth(now.Year, now.Month);
        if (now.Day != lastDayOfMonth || now.Hour < 12)
            return;

        var targetMonth = new DateTime(now.Year, now.Month, 1);
        var monthKey = targetMonth.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        if (string.Equals(alerts.LastReminderMonth, monthKey, StringComparison.Ordinal))
            return;

        var total = _tracker.GetMonthlySummary(targetMonth.Year, targetMonth.Month)
            .Aggregate(TimeSpan.Zero, (acc, day) => acc + day.Total);

        var title = "Slipsten reminder";
        var body = $"{targetMonth:MMMM} total: {FormatHours(total)}. Time to report it.";

        await SendNtfyAsync(alerts, title, body);

        settings.PhoneAlerts.LastReminderMonth = monthKey;
        _settingsService.Save(settings);
    }

    private async Task SendNtfyAsync(PhoneAlertSettings settings, string title, string message)
    {
        var serverUrl = settings.NtfyServerUrl.Trim().TrimEnd('/');
        var topic = settings.NtfyTopic.Trim().Trim('/');

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{serverUrl}/{topic}")
        {
            Content = new StringContent(message)
        };

        request.Headers.TryAddWithoutValidation("Title", title);
        request.Headers.TryAddWithoutValidation("Priority", "default");
        request.Headers.TryAddWithoutValidation("Tags", "calendar,alarm_clock");

        if (!string.IsNullOrWhiteSpace(settings.NtfyAccessToken))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.NtfyAccessToken.Trim());

        using var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private static string FormatHours(TimeSpan time) =>
        time.TotalHours >= 1
            ? $"{(int)time.TotalHours}h {time.Minutes:D2}m"
            : $"{time.Minutes}m";

    public void Dispose() => _httpClient.Dispose();
}
