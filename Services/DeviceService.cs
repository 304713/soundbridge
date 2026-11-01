using System.Text.RegularExpressions;
using SoundBridge.Infrastructure;
using SoundBridge.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Media.Audio;

namespace SoundBridge.Services;

/// <summary>
/// 通过系统蓝牙栈枚举「能作为音频接收端」的已配对设备。
/// </summary>
public sealed class DeviceService
{
    /// <summary>A2DP 接收端设备 ID 里带着手机蓝牙地址，形如 ...&amp;0&amp;0123456789AB_C00000000#</summary>
    private static readonly Regex AddressInId = new(@"&([0-9A-Fa-f]{12})_", RegexOptions.Compiled);

    /// <summary>筛选器写一次就够：这东西每 3 秒枚举一次，每次都记等于往日志里灌水。</summary>
    private bool _selectorLogged;

    /// <summary>上一次的候选数量，只在数量变化时记一行。</summary>
    private int _lastCount = -1;

    public async Task<IReadOnlyList<SinkDevice>> GetSinkDevicesAsync()
    {
        var result = new List<SinkDevice>();

        try
        {
            var selector = AudioPlaybackConnection.GetDeviceSelector();
            if (!_selectorLogged)
            {
                Log.Info($"枚举设备，筛选器: {selector}");
                _selectorLogged = true;
            }

            var infos = await DeviceInformation.FindAllAsync(selector);
            if (infos.Count != _lastCount)
            {
                Log.Info($"系统返回 {infos.Count} 个候选设备");
                _lastCount = infos.Count;
            }

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

    /// <summary>
    /// 手机现在还在不在线。用来区分两件事：音频流抖了一下（链路还在，可以悄悄接回去），
    /// 还是用户/手机那边真的把连接断了（再连就是跟人对着干）。
    /// 返回 null 表示查不出来，交给调用方按「不确定」处理。
    /// </summary>
    public async Task<bool?> IsLinkConnectedAsync(string deviceId)
    {
        try
        {
            var match = AddressInId.Match(deviceId);
            if (!match.Success) return null;

            var address = Convert.ToUInt64(match.Groups[1].Value, 16);
            using var device = await BluetoothDevice.FromBluetoothAddressAsync(address);
            if (device is null) return null;

            return device.ConnectionStatus == BluetoothConnectionStatus.Connected;
        }
        catch (Exception ex)
        {
            Log.Warn($"查询蓝牙链路状态失败: {ex.Message}");
            return null;
        }
    }
}
