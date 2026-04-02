using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Slipsten.Data;

namespace Slipsten.Views;

public partial class ImportWindow : Window
{
    private const int WindowWidth = 570;
    private const int WindowHeight = 320;
    private readonly SlipstenContext _context;
    private List<TimeEntry>? _importedEntries;

    public ImportWindow(SlipstenContext context)
    {
        InitializeComponent();
        _context = context;

        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(WindowWidth, WindowHeight));
        WindowHelper.SetAppIcon(this);
        CenterOnScreen();
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

    private async void Load_Click(object sender, RoutedEventArgs e)
    {
        var path = BackupPathBox.Text.Trim();
        if (string.IsNullOrEmpty(path))
        {
            PreviewText.Text = "Please enter a backup path";
            return;
        }
        
        await LoadPreview(path);
    }

    private async Task LoadPreview(string backupPath)
    {
        try
        {
            PreviewText.Text = "Loading...";
            ImportButton.IsEnabled = false;

            _importedEntries = await GrindstoneImporter.ImportFromBackup(backupPath);

            var totalHours = _importedEntries.Sum(e => e.Duration.TotalHours);
            PreviewText.Text = $"Found {_importedEntries.Count} time slices\n" +
                              $"Total hours: {totalHours:F1}\n" +
                              $"Date range: {_importedEntries.Min(e => e.StartTime):yyyy-MM-dd} to {_importedEntries.Max(e => e.StartTime):yyyy-MM-dd}";
            ImportButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            PreviewText.Text = $"Error: {ex.Message}";
            ImportButton.IsEnabled = false;
            _importedEntries = null;
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_importedEntries == null) return;

        try
        {
            ImportButton.IsEnabled = false;
            PreviewText.Text = "Importing...";

            await GrindstoneImporter.SaveEntries(_context, _importedEntries);

            PreviewText.Text = $"✓ Imported {_importedEntries.Count} time slices";
            await Task.Delay(1500);
            Close();
        }
        catch (Exception ex)
        {
            PreviewText.Text = $"Error importing: {ex.Message}";
            ImportButton.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
