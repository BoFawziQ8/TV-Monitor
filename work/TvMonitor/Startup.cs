using Microsoft.Win32;

// Optional autostart: one per-user Run entry that starts this copy in tray mode.
static class Startup
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TV Monitor";

    internal static bool Enabled
    {
        get
        {
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(KeyPath);
                return run?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { return false; }
        }
    }

    internal static void Set(bool enabled)
    {
        using var run = Registry.CurrentUser.CreateSubKey(KeyPath, true);
        if (enabled) run.SetValue(ValueName, Command);
        else run.DeleteValue(ValueName, false);
    }

    // Keep the entry pointing at this copy after the app folder moves.
    internal static void Refresh()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(KeyPath, true);
            if (run?.GetValue(ValueName) is string value && value != Command) run.SetValue(ValueName, Command);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
    }

    private static string Command => "\"" + Environment.ProcessPath + "\" --tray";
}
