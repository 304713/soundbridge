using Microsoft.Win32;

namespace SoundBridge.Infrastructure;

public static class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SoundBridge";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key is null) return;

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return;
                key.SetValue(ValueName, $"\"{exe}\" --tray");
                Log.Info("已开启开机自启");
            }
            else
            {
                key.DeleteValue(ValueName, false);
                Log.Info("已关闭开机自启");
            }
        }
        catch (Exception ex)
        {
            Log.Error("设置开机自启失败", ex);
        }
    }
}
