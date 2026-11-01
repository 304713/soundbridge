using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace SoundBridge.Infrastructure;

/// <summary>
/// 让窗口标题栏跟着深色主题走。
/// WPF 只管客户区，非客户区由系统按「Windows 模式」的颜色画 ——
/// 系统是浅色时，深色界面顶上就会顶一条白条。
/// </summary>
internal static class WindowChromeHelper
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private const int DwmwaCaptionColor = 35;   // Win11 22000+
    private const int DwmwaTextColor = 36;      // Win11 22000+

    /// <summary>窗口底色 #0E1013，DWM 要的是 COLORREF（0x00BBGGRR）。</summary>
    private const int CaptionColorRef = 0x0013100E;
    private const int TextColorRef = 0x00FFFFFF;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    public static void ApplyDarkTitleBar(Window window)
    {
        window.SourceInitialized += (_, _) => Apply(window);
    }

    /// <summary>
    /// 给窗口装品牌图标。不设的话，标题栏左上角是 WPF 的空白默认图标，
    /// 主窗口有图标、说明窗口没有，一眼就能看出是「临时窗口」。
    /// </summary>
    public static void ApplyAppIcon(Window window)
    {
        if (window.Icon is not null) return;

        try
        {
            using var icon = AppIconFactory.CreateAppIcon(64);
            window.Icon = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        catch (Exception ex)
        {
            Log.Warn($"设置窗口图标失败: {ex.Message}");
        }
    }

    private static void Apply(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;

            // 两条路一起走：19/20 让标题栏按钮变白，35/36 直接把颜色钉死。
            // Win11 上只设 20 有时不生效（系统是浅色模式时尤甚），补上 35 才稳。
            var enabled = 1;
            var dark = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
            if (dark != 0)
                dark = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeLegacy, ref enabled, sizeof(int));

            var caption = CaptionColorRef;
            var captionResult = DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref caption, sizeof(int));

            var text = TextColorRef;
            var textResult = DwmSetWindowAttribute(handle, DwmwaTextColor, ref text, sizeof(int));

            Log.Info($"深色标题栏: dark=0x{dark:X8} caption=0x{captionResult:X8} text=0x{textResult:X8}");

            // 光改属性标题栏不会自己重画，得让系统重新算一次非客户区
            SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }
        catch (Exception ex)
        {
            Log.Warn($"设置深色标题栏失败: {ex.Message}");
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);
}
