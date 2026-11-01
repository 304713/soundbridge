namespace SoundBridge.Models;

/// <summary>
/// 定时关闭的时长选项。<see cref="Minutes"/> 为 0 表示不启用。
/// </summary>
public sealed class SleepTimerOption
{
    public int Minutes { get; init; }

    public string Label { get; init; } = string.Empty;

    public static SleepTimerOption Off { get; } = new() { Minutes = 0, Label = "关闭" };
}
