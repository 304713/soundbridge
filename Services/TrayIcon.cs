using System.Drawing;
using System.Runtime.InteropServices;

namespace SoundBridge.Services;

/// <summary>托盘菜单里的一项。Text 为 null 表示分隔线。</summary>
public sealed record TrayMenuEntry(string? Text, Action? Click = null, bool Enabled = true, bool Checked = false, bool IsDefault = false)
{
    public static TrayMenuEntry Separator { get; } = new(null!, Click: null);
}

/// <summary>
/// 托盘图标。直接调 Shell_NotifyIcon，不依赖 WinForms。
///
/// 为什么不用 WPF 的 ContextMenu：它需要一个 WPF 视觉树里的落点，而多开一个 WPF 窗口
/// 实测要多吃 17～37 MB 私有内存（每个 HwndSource 都带自己的一套渲染表面）——
/// 为去掉 WinForms 而背上这个，等于白折腾。所以窗口和菜单都用最原始的 Win32：
/// 一个不可见的窗口收消息，一个原生弹出菜单，开销以 KB 计。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_APP = 0x8000;
    private const int CallbackMessage = WM_APP + 0x20;

    private const int WM_CONTEXTMENU = 0x007B;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_NULL = 0x0000;

    private const uint NIM_ADD = 0;
    private const uint NIM_MODIFY = 1;
    private const uint NIM_DELETE = 2;

    private const uint NIF_MESSAGE = 0x01;
    private const uint NIF_ICON = 0x02;
    private const uint NIF_TIP = 0x04;
    private const uint NIF_SHOWTIP = 0x80;

    private const uint MF_STRING = 0x0000;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint MF_GRAYED = 0x0001;
    private const uint MF_CHECKED = 0x0008;
    private const uint MF_DEFAULT = 0x1000;

    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_NONOTIFY = 0x0080;

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>资源管理器重启后会广播这条消息，收到就得把图标重新挂上去。</summary>
    private static readonly uint TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    private readonly Action _activate;
    private readonly Action _showContextMenu;
    private readonly IntPtr _hwnd;

    /// <summary>窗口过程委托必须一直是活的，交给 GC 回收了回调就会跳进野指针。</summary>
    private readonly WndProcDelegate _wndProc;

    private Icon? _icon;
    private string _tip = "声桥";
    private bool _added;
    private bool _disposed;

    public TrayIcon(Action activate, Action showContextMenu)
    {
        _activate = activate;
        _showContextMenu = showContextMenu;
        _wndProc = WindowProc;
        _hwnd = CreateHostWindow();

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("创建托盘消息窗口失败");
    }

    /// <summary>换图标和提示文字。传进来的 Icon 由这里接管，替换时负责释放。</summary>
    public void Update(Icon icon, string tip)
    {
        if (_disposed) return;

        _tip = Truncate(tip, 127);

        var previous = _icon;
        _icon = icon;

        if (!_added) Add();
        else if (!Modify()) Add();

        if (!ReferenceEquals(previous, icon)) previous?.Dispose();
    }

    /// <summary>在鼠标位置弹出菜单并执行选中项。菜单在调用期间是模态的。</summary>
    public void ShowMenu(IReadOnlyList<TrayMenuEntry> entries)
    {
        if (_disposed) return;

        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        var actions = new List<Action?>(entries.Count);

        try
        {
            foreach (var entry in entries)
            {
                if (entry.Text is null)
                {
                    AppendMenu(menu, MF_SEPARATOR, IntPtr.Zero, null);
                    continue;
                }

                var flags = MF_STRING;
                if (!entry.Enabled) flags |= MF_GRAYED;
                if (entry.Checked) flags |= MF_CHECKED;
                if (entry.IsDefault) flags |= MF_DEFAULT;

                // ID 从 1 开始，和 actions 的下标一一对应
                var id = (IntPtr)(actions.Count + 1);
                AppendMenu(menu, flags, id, entry.Text);
                actions.Add(entry.Click);
            }

            GetCursorPos(out var point);

            // 原生菜单的规矩：弹出前必须让宿主窗口成为前台窗口，否则点菜单外面它不会消失。
            // 而 SetForegroundWindow 对「不属于当前前台线程」的调用是会被系统忽略的，
            // 所以先临时挂到前台线程上，设完再断开——这一步是这整套代码里唯一不能省的。
            var previous = GetForegroundWindow();
            ForceForeground();

            var command = (int)TrackPopupMenu(menu,
                TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY, point.X, point.Y, 0, _hwnd, IntPtr.Zero);

            // 让菜单窗口收到一条消息，收尾释放捕获
            PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            // 焦点还回去：不然关掉菜单后用户原来那个窗口会一直是一副「非激活」的灰样子
            if (previous != IntPtr.Zero && previous != _hwnd) SetForegroundWindow(previous);

            if (command > 0 && command <= actions.Count) actions[command - 1]?.Invoke();
        }
        catch (Exception ex)
        {
            Infrastructure.Log.Error("弹出托盘菜单失败", ex);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    /// <summary>借前台线程的手，把「设前台」这件事办成。</summary>
    private void ForceForeground()
    {
        var foreground = GetForegroundWindow();
        var foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
        var thisThread = GetCurrentThreadId();
        var attached = false;

        try
        {
            if (foregroundThread != 0 && foregroundThread != thisThread)
                attached = AttachThreadInput(thisThread, foregroundThread, true);

            SetForegroundWindow(_hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(thisThread, foregroundThread, false);
        }
    }

    private IntPtr CreateHostWindow()
    {
        var instance = GetModuleHandle(null);

        // 每个实例注册自己的窗口类：类名带 GUID，委托就和这个实例绑死，
        // 不会出现「第二个实例复用了第一个实例的回调」这种诡异问题
        var name = $"SoundBridgeTrayHost.{Guid.NewGuid():N}";
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = instance,
            lpszClassName = name
        };

        if (RegisterClassEx(ref wc) == 0)
        {
            Infrastructure.Log.Warn("注册托盘消息窗口类失败");
            return IntPtr.Zero;
        }

        // 屏幕外的一个 1×1 空窗口：看不见、不占任务栏、不进 Alt+Tab，只用来收消息。
        // 必须是「显示状态」（带 WS_VISIBLE），否则 SetForegroundWindow 不认它，
        // 菜单就会变成点哪儿都不消失的那种。
        return CreateWindowEx(WS_EX_TOOLWINDOW, name, string.Empty, WS_POPUP | WS_VISIBLE,
            -32000, -32000, 1, 1, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
    }

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (_disposed) return DefWindowProc(hwnd, message, wParam, lParam);

        if (message == TaskbarCreated)
        {
            _added = false;
            Add();
            return IntPtr.Zero;
        }

        if (message != CallbackMessage) return DefWindowProc(hwnd, message, wParam, lParam);

        // 新版协议把鼠标消息塞在 lParam 的低 16 位，老版直接放整个 lParam；取低位两种都能用
        switch ((int)((long)lParam & 0xFFFF))
        {
            case WM_LBUTTONDBLCLK:
                _activate();
                break;

            case WM_RBUTTONUP:
            case WM_CONTEXTMENU:
                _showContextMenu();
                break;

            case WM_LBUTTONUP:
                // 单击不做事：和以前一样，只有双击才把主界面叫出来
                break;
        }

        return IntPtr.Zero;
    }

    private void Add()
    {
        try
        {
            _added = Shell(NIM_ADD);
            if (!_added) Infrastructure.Log.Warn("托盘图标添加失败（资源管理器没准备好？稍后重试）");
        }
        catch (Exception ex)
        {
            Infrastructure.Log.Error("托盘图标添加异常", ex);
        }
    }

    private bool Modify()
    {
        try
        {
            return Shell(NIM_MODIFY);
        }
        catch (Exception ex)
        {
            Infrastructure.Log.Error("托盘图标更新异常", ex);
            return false;
        }
    }

    private bool Shell(uint action)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon?.Handle ?? IntPtr.Zero,
            szTip = _tip,
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };

        return Shell_NotifyIcon(action, ref data);
    }

    private static string Truncate(string text, int max)
        => string.IsNullOrEmpty(text) ? "声桥" : text.Length <= max ? text : text[..max];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_added) Shell(NIM_DELETE);
        }
        catch
        {
            // 图标没了就算了，退出流程不能卡在这
        }

        _added = false;

        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);

        _icon?.Dispose();
        _icon = null;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATAW data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, IntPtr itemId, string? item);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attachThread, uint attachTo, bool attach);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y,
        int reserved, IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
}
