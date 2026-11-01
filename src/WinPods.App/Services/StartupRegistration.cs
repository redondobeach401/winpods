using Microsoft.Win32;

namespace WinPods.App.Services;

/// <summary>"Start with Windows" via HKCU\...\Run.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WinPods";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --tray");
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
