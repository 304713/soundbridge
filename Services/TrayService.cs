using System.ComponentModel;
using System.Drawing;
using SoundBridge.Infrastructure;
using SoundBridge.ViewModels;

namespace SoundBridge.Services;

/// <summary>
/// 托盘图标 + 右键菜单，把界面上的三件事（连接 / 自启 / 退出）搬到托盘里。
/// 图标和消息在 <see cref="TrayIcon"/>，这里只回答「菜单上该有哪些项、现在是什么状态」。
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly MainViewModel _viewModel;
    private readonly Action _showWindow;
    private readonly Action _exit;
    private readonly TrayIcon _tray;

    /// <summary>缓存托盘图标：状态文字随时会变，图标只在连接状态真的变了才重画。</summary>
    private Icon? _currentIcon;
    private bool? _iconConnected;

    public TrayService(MainViewModel viewModel, Action showWindow, Action exit)
    {
        _viewModel = viewModel;
        _showWindow = showWindow;
        _exit = exit;

        _tray = new TrayIcon(showWindow, ShowContextMenu);

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
            or nameof(MainViewModel.SleepTimerTrayText)
            or nameof(MainViewModel.AutoStart))
        {
            UpdateVisual();
        }
    }

    /// <summary>
    /// 每次右键都现读一遍视图模型。菜单是临时建的，不用跟着状态变化去维护——
    /// 也就不会出现「托盘菜单里的连接按钮还是禁用状态」这种过时显示。
    /// </summary>
    private void ShowContextMenu()
    {
        var toggleText = _viewModel.IsReconnecting
            ? "停止重连"
            : _viewModel.IsConnected ? "断开连接" : "连接";

        var canToggle = _viewModel.IsReconnecting || _viewModel.IsConnected || _viewModel.SelectedDevice is not null;

        _tray.ShowMenu(new[]
        {
            new TrayMenuEntry("打开主界面", _showWindow, IsDefault: true),
            TrayMenuEntry.Separator,
            new TrayMenuEntry(toggleText, () => _viewModel.PrimaryCommand.Execute(null), canToggle),
            TrayMenuEntry.Separator,
            new TrayMenuEntry("开机自启", () => _viewModel.AutoStart = !_viewModel.AutoStart, Checked: _viewModel.AutoStart),
            TrayMenuEntry.Separator,
            new TrayMenuEntry("退出", _exit)
        });
    }

    private void UpdateVisual()
    {
        try
        {
            var connected = _viewModel.IsConnected;
            if (_currentIcon is null || _iconConnected != connected)
            {
                _currentIcon = AppIconFactory.CreateTrayIcon(connected);
                _iconConnected = connected;
            }

            _tray.Update(_currentIcon, BuildTooltip());
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

    public void Dispose()
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _tray.Dispose();
    }
}
