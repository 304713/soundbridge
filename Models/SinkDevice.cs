using SoundBridge.Infrastructure;

namespace SoundBridge.Models;

public sealed class SinkDevice : ObservableObject
{
    private bool _isActive;

    public SinkDevice(string id, string name, bool paired = true)
    {
        Id = id;
        Name = string.IsNullOrWhiteSpace(name) ? "未命名设备" : name;
        IsPaired = paired;
    }

    /// <summary>WinRT 设备 Id，用于创建音频接收连接。</summary>
    public string Id { get; }

    public string Name { get; }

    public bool IsPaired { get; }

    /// <summary>当前是否正在接收该设备的音频。</summary>
    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }
}
