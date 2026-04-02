using System.IO;
using Microsoft.UI.Xaml;

namespace Slipsten.Views;

internal static class WindowHelper
{
    public static void SetAppIcon(Window window)
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "slipsten.ico");
        if (File.Exists(iconPath))
            window.AppWindow.SetIcon(iconPath);
    }
}
