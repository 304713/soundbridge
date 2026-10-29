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

    /// <summary>用户指定的播放设备 ID；null 表示不干预系统默认设置。</summary>
    public string? OutputDeviceId { get; set; }
    public string? OutputDeviceName { get; set; }
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
