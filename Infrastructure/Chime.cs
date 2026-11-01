using System.IO;
using System.Runtime.InteropServices;

namespace SoundBridge.Infrastructure;

/// <summary>
/// 定时到点的那一声。
///
/// 优先走系统声音方案里的「提示音」（SystemAsterisk）：用户在声音设置里换过音效、
/// 或者把系统声音整体关掉了，都算他的选择，照办。只有当这一项被设成「无」的时候
/// 才退回系统自带的「叮」一声——用户既然勾了「到点响铃」，就不应该一点声音都没有。
/// </summary>
public static class Chime
{
    private const uint SND_ASYNC = 0x0001;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_ALIAS = 0x00010000;
    private const uint SND_FILENAME = 0x00020000;

    /// <summary>退回用的 WAV 不存在的话，记一次就不再反复查磁盘。</summary>
    private static bool _fallbackUnavailable;

    public static void Play()
    {
        try
        {
            // SND_ASYNC：立刻返回，不挡住到点之后的收尾动作
            if (PlaySound("SystemAsterisk", IntPtr.Zero, SND_ALIAS | SND_ASYNC | SND_NODEFAULT))
                return;

            if (_fallbackUnavailable)
            {
                Log.Warn("提示音没响：系统声音方案里没有可用提示音");
                return;
            }

            var ding = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Windows Ding.wav");

            if (!File.Exists(ding))
            {
                _fallbackUnavailable = true;
                Log.Warn($"提示音没响：既没有系统提示音，也找不到 {ding}");
                return;
            }

            if (PlaySound(ding, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_NODEFAULT))
            {
                Log.Info("系统提示音没配，改用自带的「叮」一声");
                return;
            }

            Log.Warn("提示音没响：系统拒绝了播放请求（音频设备可能刚被切走）");
        }
        catch (Exception ex)
        {
            Log.Warn($"提示音播放失败: {ex.Message}");
        }
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PlaySound(string sound, IntPtr module, uint flags);
}
