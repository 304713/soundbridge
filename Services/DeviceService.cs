using System.Text.RegularExpressions;
using SoundBridge.Infrastructure;
using SoundBridge.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Media.Audio;

namespace SoundBridge.Services;

/// <summary>这台电脑上蓝牙无线电的状态。</summary>
public enum BluetoothState
{
    /// <summary>读不出来（没有蓝牙硬件，或者系统不让我们查）。</summary>
    Unknown,

    On,

    /// <summary>开关被关掉了，或者开了飞行模式。</summary>
    Off,

    /// <summary>适配器在设备管理器里被禁用。</summary>
    Disabled
}

/// <summary>
/// 通过系统蓝牙栈枚举「能作为音频接收端」的已配对设备。
/// </summary>
public sealed class DeviceService
{
    /// <summary>
    /// 蓝牙开关状态。用 <see cref="Radio"/> 而不是 <see cref="BluetoothAdapter.GetDefaultAsync"/>：
    /// 实测适配器存在但无线电被关掉时，后者照样返回一个对象，区分不出来。
    /// </summary>
    private BluetoothState _radioState = BluetoothState.Unknown;
    private long _radioCheckedAt;
    private static readonly TimeSpan RadioCacheFor = TimeSpan.FromSeconds(5);

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

    /// <summary>
    /// 蓝牙开关现在是开着还是关着。
    /// 关掉蓝牙时，系统的筛选器会一台设备都返回不了，界面上看起来和「适配器不支持接收」
    /// 一模一样——这两件事的解法完全相反，所以必须分开告诉用户。
    /// </summary>
    public async Task<BluetoothState> GetBluetoothStateAsync()
    {
        var now = Environment.TickCount64;

        // 空列表时这个方法会跟着 3 秒一次的轮询被调用，5 秒内不再重复问系统
        if (_radioState != BluetoothState.Unknown && now - _radioCheckedAt < RadioCacheFor.TotalMilliseconds)
            return _radioState;

        _radioCheckedAt = now;
        _radioState = await ReadBluetoothStateAsync();
        return _radioState;
    }

    private static async Task<BluetoothState> ReadBluetoothStateAsync()
    {
        try
        {
            var radios = await Radio.GetRadiosAsync();
            var found = false;
            var state = BluetoothState.Unknown;

            foreach (var radio in radios)
            {
                if (radio.Kind != RadioKind.Bluetooth) continue;

                found = true;
                if (radio.State == RadioState.On) return BluetoothState.On;

                state = radio.State == RadioState.Disabled
                    ? BluetoothState.Disabled
                    : BluetoothState.Off;
            }

            return found ? state : BluetoothState.Unknown;
        }
        catch (Exception ex)
        {
            Log.Warn($"查询蓝牙开关状态失败: {ex.Message}");
            return BluetoothState.Unknown;
        }
    }
}
