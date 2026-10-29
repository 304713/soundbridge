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
