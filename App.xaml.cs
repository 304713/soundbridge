using System.Threading;
using SoundBridge.Infrastructure;
using SoundBridge.Services;
using SoundBridge.ViewModels;
using SoundBridge.Views;

namespace SoundBridge;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\SoundBridge.SingleInstance";
    private const string SignalName = @"Local\SoundBridge.ShowWindow";

    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;
    private TrayService? _tray;
    private MainViewModel? _viewModel;
    private MainWindow? _window;

    public static bool IsExiting { get; private set; }

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        var startHidden = e.Args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));

        _mutex = new Mutex(true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // 已有实例在跑：叫它把窗口亮出来，然后退出
            try { EventWaitHandle.OpenExisting(SignalName).Set(); } catch { }
            Shutdown();
            return;
        }

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
        var listener = new Thread(() =>
        {
            while (true)
            {
                _showSignal.WaitOne();
                Dispatcher.InvokeAsync(ShowMainWindow);
            }
        })
        {
            IsBackground = true,
            Name = "SoundBridge.SingleInstanceListener"
        };
        listener.Start();

        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("UI 线程未处理异常", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("后台未处理异常", args.ExceptionObject as Exception);

        _viewModel = new MainViewModel();
        _window = new MainWindow { DataContext = _viewModel };
        MainWindow = _window;

        _tray = new TrayService(_viewModel, ShowMainWindow, ExitApplication);

        // 托盘模式（开机自启）下窗口不会 Show，MainWindow.Loaded 永远不触发。
        // 所以初始化在这里主动拉起，否则自启就只是挂了个图标，扫设备、自连全不会发生。
        _ = StartupAsync();

        if (startHidden)
        {
            Log.Info("以托盘模式启动");
        }
        else
        {
            _window.Show();
        }
    }

    private async System.Threading.Tasks.Task StartupAsync()
    {
        try
        {
            if (_viewModel is not null) await _viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            Log.Error("启动初始化失败", ex);
        }
    }

    public void ShowMainWindow()
    {
        if (_window is null) return;

        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == System.Windows.WindowState.Minimized)
            _window.WindowState = System.Windows.WindowState.Normal;

        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
    }

    public void ExitApplication()
    {
        IsExiting = true;
        _tray?.Dispose();
        _tray = null;
        _viewModel?.Dispose();
        _viewModel = null;
        Shutdown();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _tray?.Dispose();
        _viewModel?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
