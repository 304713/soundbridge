using System.IO;
using System.Text.Json;
using SoundBridge.Infrastructure;

namespace SoundBridge.Services;

public sealed class AppSettings
{
    public bool AutoStart { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool AutoReconnect { get; set; } = true;
    public bool AutoConnectOnStart { get; set; }
    public string? LastDeviceName { get; set; }

    /// <summary>是否已经看过首次使用说明。</summary>
    public bool HasSeenWelcome { get; set; }

    /// <summary>用户指定的播放设备 ID；null 表示不干预系统默认设置。</summary>
    public string? OutputDeviceId { get; set; }
    public string? OutputDeviceName { get; set; }

    /// <summary>连接时把系统音量压到上限以内。默认开启，属于安全措施而非体验功能。</summary>
    public bool VolumeLimitEnabled { get; set; } = true;

    /// <summary>音量上限（百分比）。</summary>
    public int VolumeCapPercent { get; set; } = 80;

    /// <summary>
    /// 定时到点后是否响铃。默认关：这个功能多半是在睡前用的，
    /// 半夜突然来一声音效只会把人吵醒。
    /// </summary>
    public bool SleepTimerChime { get; set; }

    /// <summary>上次在「自定义…」里填的分钟数，下次打开对话框直接带出来。</summary>
    public int CustomSleepMinutes { get; set; } = 20;
}

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
    };

    public AppSettings Current { get; private set; } = new();

    public void Load()
    {
        try
        {
            var file = AppPaths.SettingsFile;
            if (!File.Exists(file)) return;

            var json = File.ReadAllText(file);
            Current = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Error("读取设置失败，使用默认值", ex);
            Current = new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(Current, Options));
        }
        catch (Exception ex)
        {
            Log.Error("保存设置失败", ex);
        }
    }
}
