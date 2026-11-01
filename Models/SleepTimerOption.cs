namespace SoundBridge.Models;

/// <summary>
/// 定时关闭的时长选项。<see cref="Minutes"/> 为 0 表示不启用。
/// </summary>
public sealed class SleepTimerOption
{
    public int Minutes { get; init; }

    public string Label { get; init; } = string.Empty;

    /// <summary>「自定义…」那一项：选中它本身不开始计时，而是先问用户要多久。</summary>
    public bool IsCustom { get; init; }

    /// <summary>用户自己填过的时长，选完会作为一个普通选项留在下拉框里。</summary>
    public bool IsUserDefined { get; init; }

    public static SleepTimerOption Off { get; } = new() { Minutes = 0, Label = "关闭" };

    public static SleepTimerOption Custom { get; } = new() { Minutes = 0, Label = "自定义…", IsCustom = true };
}
