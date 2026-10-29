using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace SoundBridge.Infrastructure;

internal static class AppIconFactory
{
    /// <summary>软件主色，与主题里的 AccentBrush 保持一致。</summary>
    public const string AccentHex = "#2F6FED";

    /// <summary>未连接态用的主题中灰。</summary>
    public const string IdleHex = "#6C757E";

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>任务栏 / 窗口图标：固定品牌蓝，不随连接状态变化。</summary>
    public static Icon CreateAppIcon(int size = 32) => Draw(AccentHex, size);

    /// <summary>托盘图标：已连接用品牌蓝，未连接用灰，靠颜色区分状态。</summary>
    public static Icon CreateTrayIcon(bool connected) => Draw(connected ? AccentHex : IdleHex, 32);

    /// <summary>圆角方块 + 音符。运行时绘制，不依赖外部资源文件。</summary>
    private static Icon Draw(string colorHex, int size)
    {
        using var bmp = Render(colorHex, size);

        var handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    /// <summary>把图标渲染成位图，供生成 .ico 或预览用。</summary>
    public static Bitmap Render(string colorHex, int size)
    {
        var background = ColorTranslator.FromHtml(colorHex);
        var unit = size / 32f;          // 以 32px 为基准等比放大
        var radius = 8f * unit;
        var inset = 1f * unit;

        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        using var path = RoundedRect(new RectangleF(inset, inset, size - inset * 2, size - inset * 2), radius);
        using var brush = new SolidBrush(background);
        g.FillPath(brush, path);

        using var font = new Font("Segoe UI Symbol", 17f * unit, FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        g.DrawString("\u266A", font, Brushes.White, new RectangleF(0f, 0f, size, size + unit), format);

        return bmp;
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
