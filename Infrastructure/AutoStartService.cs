using Microsoft.Win32;
using Windows.ApplicationModel;

namespace SoundBridge.Infrastructure;

/// <summary>
/// 开机自启。
/// 免安装版写注册表 Run 键；MSIX 版必须用清单里声明的 startupTask，
/// 因为打包应用的注册表写入会被重定向到沙盒，真的 Run 键不会生效。
/// </summary>
public static class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SoundBridge";
    private const string StartupTaskId = "SoundBridgeStartup";

    public static bool IsEnabled()
    {
        if (PackageIdentity.IsPackaged) return GetStartupTask() is { State: StartupTaskState.Enabled };

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
        if (PackageIdentity.IsPackaged)
        {
            _ = SetStartupTaskAsync(enabled);
            return;
        }

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

    private static StartupTask? GetStartupTask()
    {
        try
        {
            // 放到线程池上同步等待，避免在 UI 线程上死等 WinRT 回调
            return Task.Run(async () => await StartupTask.GetAsync(StartupTaskId))
                       .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Warn($"读取启动任务失败: {ex.Message}");
            return null;
        }
    }

    private static async Task SetStartupTaskAsync(bool enabled)
    {
        try
        {
            var task = await StartupTask.GetAsync(StartupTaskId);

            if (enabled)
            {
                var state = await task.RequestEnableAsync();
                Log.Info(state switch
                {
                    StartupTaskState.Enabled => "已开启开机自启（启动任务）",
                    StartupTaskState.DisabledByUser =>
                        "开机自启被系统的「启动应用」设置关掉了，需要用户在任务管理器里打开",
                    StartupTaskState.DisabledByPolicy => "组织策略禁止了开机自启",
                    _ => $"开启开机自启未生效，当前状态: {state}"
                });
            }
            else
            {
                task.Disable();
                Log.Info("已关闭开机自启（启动任务）");
            }
        }
        catch (Exception ex)
        {
            Log.Error("设置启动任务失败", ex);
        }
    }
}
