using System.ComponentModel;
using System.Drawing;
using SoundBridge.Infrastructure;
using SoundBridge.ViewModels;
using WinForms = System.Windows.Forms;

namespace SoundBridge.Services;

public sealed class TrayService : IDisposable
{
    private readonly MainViewModel _viewModel;
    private readonly Action _showWindow;
    private readonly Action _exit;
    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly WinForms.ToolStripMenuItem _toggleItem;
    private readonly WinForms.ToolStripMenuItem _autoStartItem;
    private Icon? _icon;

    public TrayService(MainViewModel viewModel, Action showWindow, Action exit)
    {
        _viewModel = viewModel;
        _showWindow = showWindow;
        _exit = exit;

        _toggleItem = new WinForms.ToolStripMenuItem("连接", null, (_, _) => _viewModel.PrimaryCommand.Execute(null));
        _autoStartItem = new WinForms.ToolStripMenuItem("开机自启", null, (_, _) => _viewModel.AutoStart = !_viewModel.AutoStart);

        var menu = new WinForms.ContextMenuStrip { ShowImageMargin = false };
        var openItem = new WinForms.ToolStripMenuItem("打开主界面", null, (_, _) => _showWindow())
        {
            Font = new Font(menu.Font, FontStyle.Bold)
        };
        menu.Items.Add(openItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(new WinForms.ToolStripMenuItem("退出", null, (_, _) => _exit()));

        _icon = AppIconFactory.CreateTrayIcon(false);
        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = _icon,
            Text = "声桥",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => _showWindow();

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateVisual();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsConnected)
            or nameof(MainViewModel.IsReconnecting)
            or nameof(MainViewModel.IsBusy)
            or nameof(MainViewModel.SelectedDevice)
            or nameof(MainViewModel.StatusText)
            or nameof(MainViewModel.ActionText)
            or nameof(MainViewModel.SleepTimerTrayText)
            or nameof(MainViewModel.AutoStart))
        {
            UpdateVisual();
        }
    }

    private void UpdateVisual()
    {
        try
        {
            var connected = _viewModel.IsConnected;
            var next = AppIconFactory.CreateTrayIcon(connected);

            _notifyIcon.Icon = next;
            _icon?.Dispose();
            _icon = next;

            _notifyIcon.Text = Truncate(BuildTooltip(), 63);
            _toggleItem.Text = _viewModel.IsReconnecting
                ? "停止重连"
                : _viewModel.IsConnected ? "断开连接" : "连接";
            _toggleItem.Enabled = _viewModel.IsReconnecting
                                  || _viewModel.IsConnected
                                  || _viewModel.SelectedDevice is not null;
            _autoStartItem.Checked = _viewModel.AutoStart;
        }
        catch (Exception ex)
        {
            Log.Error("更新托盘状态失败", ex);
        }
    }

    /// <summary>托盘提示：连接状态后面挂上定时关闭的剩余时间，缩到托盘时也能看见。</summary>
    private string BuildTooltip()
    {
        var timer = _viewModel.SleepTimerTrayText;
        return timer.Length == 0
            ? $"声桥 · {_viewModel.StatusText}"
            : $"声桥 · {_viewModel.StatusText} · {timer}";
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    public void Dispose()
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon?.Dispose();
    }
}
