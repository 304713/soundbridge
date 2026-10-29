using SoundBridge.Infrastructure;
using SoundBridge.Models;
using Windows.Devices.Enumeration;
using Windows.Media.Audio;

namespace SoundBridge.Services;

/// <summary>
/// 通过系统蓝牙栈枚举「能作为音频接收端」的已配对设备。
/// </summary>
public sealed class DeviceService
{
    public async Task<IReadOnlyList<SinkDevice>> GetSinkDevicesAsync()
    {
        var result = new List<SinkDevice>();

        try
        {
            var selector = AudioPlaybackConnection.GetDeviceSelector();
            Log.Info($"枚举设备，筛选器: {selector}");

            var infos = await DeviceInformation.FindAllAsync(selector);
            Log.Info($"系统返回 {infos.Count} 个候选设备");

            foreach (var info in infos)
            {
                if (string.IsNullOrWhiteSpace(info.Name)) continue;

                var paired = true;
                try { paired = info.Pairing?.IsPaired ?? true; } catch { }

                result.Add(new SinkDevice(info.Id, info.Name, paired));
            }
        }
        catch (Exception ex)
        {
            Log.Error("枚举设备失败", ex);
        }

        return result
            .OrderBy(d => d.Name, StringComparer.CurrentCulture)
            .ToList();
    }
}
