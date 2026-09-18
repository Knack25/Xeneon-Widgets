using System.Drawing;
using System.Security;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PlannerEdge.Helper.Hosting;

public static class TrayIconTheme
{
    public static bool UseLightForeground(int? lightTheme, bool highContrast, Color background) =>
        highContrast ? background.GetBrightness() < 0.5f : lightTheme != 1;

    public static bool IsLightForeground()
    {
        int? theme = null;
        try
        {
            theme = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", null) as int?;
        }
        catch (Exception error) when (error is SecurityException or UnauthorizedAccessException or IOException) { }
        return UseLightForeground(theme, SystemInformation.HighContrast, SystemColors.Window);
    }
}
