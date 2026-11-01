using System.Runtime.InteropServices;
using SoundBridge.Infrastructure;

namespace SoundBridge.Services;

/// <summary>一个可以被选中的播放设备。</summary>
public sealed class AudioOutput
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool IsDefault { get; init; }
}

/// <summary>
/// 列出 / 切换系统默认播放设备。
/// 之所以要自己管这件事：Windows 在默认设备消失后不会自动回落到别的设备，
/// 声音会一直往一个不存在的端点送，表现就是「连上了却没声音」。
/// </summary>
public static class AudioOutputService
{
    private const int ERender = 0;
    private const int EConsole = 0;
    private const int EMultimedia = 1;

    /// <summary>列出当前可用的播放设备（只列已启用的）。</summary>
    public static List<AudioOutput> GetOutputs()
    {
        var result = new List<AudioOutput>();

        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

            string? defaultId = null;
            if (enumerator.GetDefaultAudioEndpoint(ERender, EMultimedia, out var def) == 0 && def is not null)
            {
                if (def.GetId(out var id) == 0) defaultId = id;
                Marshal.ReleaseComObject(def);
            }

            if (enumerator.EnumAudioEndpoints(ERender, 0x1 /*ACTIVE*/, out var collection) != 0)
            {
                Marshal.ReleaseComObject(enumerator);
                return result;
            }

            collection.GetCount(out var count);
            for (var i = 0; i < count; i++)
            {
                if (collection.Item(i, out var device) != 0) continue;

                device.GetId(out var deviceId);
                device.OpenPropertyStore(0, out var store);
                var name = ReadFriendlyName(store);

                if (!string.IsNullOrWhiteSpace(name))
                {
                    result.Add(new AudioOutput
                    {
                        Id = deviceId,
                        Name = name,
                        IsDefault = string.Equals(deviceId, defaultId, StringComparison.OrdinalIgnoreCase)
                    });
                }

                Marshal.ReleaseComObject(store);
                Marshal.ReleaseComObject(device);
            }

            Marshal.ReleaseComObject(collection);
            Marshal.ReleaseComObject(enumerator);
        }
        catch (Exception ex)
        {
            Log.Error("枚举播放设备失败", ex);
        }

        return result;
    }

    /// <summary>把默认播放设备切成指定设备（播放 + 多媒体两个角色，不动通讯设备）。</summary>
    public static bool TrySetDefault(string deviceId, out string error)
    {
        error = string.Empty;
        try
        {
            var policy = (IPolicyConfig)new CPolicyConfigClient();
            foreach (var role in new[] { EConsole, EMultimedia })
            {
                var hr = policy.SetDefaultEndpoint(deviceId, role);
                if (hr != 0)
                {
                    error = $"系统拒绝切换（0x{hr:X8}）";
                    Log.Warn($"切换默认播放设备失败，角色 {role}，HRESULT 0x{hr:X8}");
                    Marshal.ReleaseComObject(policy);
                    return false;
                }
            }

            Marshal.ReleaseComObject(policy);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Error("切换默认播放设备异常", ex);
            return false;
        }
    }

    /// <summary>读某台播放设备的当前音量（0~1）。</summary>
    public static bool TryGetVolume(string deviceId, out float scalar, out string error)
    {
        var value = 0f;
        var ok = WithVolume(deviceId,
            v => v.GetMasterVolumeLevelScalar(out value),
            out error);
        scalar = value;
        return ok;
    }

    /// <summary>直接设置音量（0~1）。只在明确需要时用，平时走 TryCapVolume。</summary>
    public static bool TrySetVolume(string deviceId, float scalar, out string error)
    {
        var level = Math.Clamp(scalar, 0f, 1f);
        return WithVolume(deviceId, v =>
        {
            var context = Guid.Empty;
            return v.SetMasterVolumeLevelScalar(level, ref context);
        }, out error);
    }

    /// <summary>
    /// 把指定设备的音量压到上限以内。已经低于上限就不动，避免无谓干扰。
    /// 返回 true 表示确实调低了；before 是调整前的音量（0~1）。
    /// </summary>
    public static bool TryCapVolume(string deviceId, int capPercent, out double before, out string error)
    {
        before = 0;
        error = string.Empty;

        var cap = Math.Clamp(capPercent, 1, 100) / 100f;

        if (!TryGetVolume(deviceId, out var current, out error)) return false;

        before = current;
        if (current <= cap + 0.0001f) return false;   // 没超上限，不折腾

        if (!TrySetVolume(deviceId, cap, out error))
        {
            error = string.IsNullOrEmpty(error) ? "设置音量失败" : error;
            return false;
        }

        return true;
    }

    /// <summary>打开设备的音量接口执行一个操作，返回 HRESULT == 0 是否成立。</summary>
    private static bool WithVolume(string deviceId, Func<IAudioEndpointVolume, int> action, out string error)
    {
        error = string.Empty;
        const int ClsCtxAll = 23;

        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        object? activated = null;

        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDevice(deviceId, out device) != 0 || device is null)
            {
                error = "找不到该播放设备";
                return false;
            }

            var iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out activated) != 0 || activated is null)
            {
                error = "无法访问该设备的音量控制";
                return false;
            }

            var volume = (IAudioEndpointVolume)activated;
            if (action(volume) != 0)
            {
                error = "音量操作失败";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Error("音量操作异常", ex);
            return false;
        }
        finally
        {
            if (activated is not null && Marshal.IsComObject(activated)) Marshal.ReleaseComObject(activated);
            if (device is not null) Marshal.ReleaseComObject(device);
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
        }
    }

    private static string? ReadFriendlyName(IPropertyStore store)
    {
        var key = new PROPERTYKEY
        {
            fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
            pid = 14
        };

        if (store.GetValue(ref key, out var value) != 0) return null;
        if (value.vt != 31 /*VT_LPWSTR*/) return null;
        return Marshal.PtrToStringUni(value.pointerValue);
    }
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject { }

[ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
internal class CPolicyConfigClient { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    int GetDevice(string id, out IMMDevice device);
    int RegisterEndpointNotificationCallback(IntPtr client);
    int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    int GetCount(out int count);
    int Item(int index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    int OpenPropertyStore(int access, out IPropertyStore properties);
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    int GetState(out int state);
}

[ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    int GetCount(out int count);
    int GetAt(int index, out PROPERTYKEY key);
    int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
    int Commit();
}

[ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr format);
    int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, bool isDefault, out IntPtr format);
    int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpoint, IntPtr mix);
    int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, bool isDefault, out long def, out long min);
    int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr period);
    int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr mode);
    int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
    int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref PROPERTYKEY key, out PROPVARIANT value);
    int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref PROPERTYKEY key, ref PROPVARIANT value);
    int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
    int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, bool visible);
}

/// <summary>
/// 设备音量控制。方法顺序必须与 vtable 一致——COM 按序号调用，漏一个就会调错函数。
/// </summary>
[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    int RegisterControlChangeNotify(IntPtr notify);
    int UnregisterControlChangeNotify(IntPtr notify);
    int GetChannelCount(out uint count);
    int SetMasterVolumeLevel(float level, ref Guid context);
    int SetMasterVolumeLevelScalar(float level, ref Guid context);
    int GetMasterVolumeLevel(out float level);
    int GetMasterVolumeLevelScalar(out float level);
    int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
    int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
    int GetChannelVolumeLevel(uint channel, out float level);
    int GetChannelVolumeLevelScalar(uint channel, out float level);
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    int GetVolumeStepInfo(out uint step, out uint stepCount);
    int VolumeStepUp(ref Guid context);
    int VolumeStepDown(ref Guid context);
    int QueryHardwareSupport(out uint mask);
    int GetVolumeRange(out float min, out float max, out float increment);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROPERTYKEY
{
    public Guid fmtid;
    public int pid;
}

[StructLayout(LayoutKind.Explicit)]
internal struct PROPVARIANT
{
    [FieldOffset(0)] public ushort vt;
    [FieldOffset(8)] public IntPtr pointerValue;
}
