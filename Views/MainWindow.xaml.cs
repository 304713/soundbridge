using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using SoundBridge.Infrastructure;
using SoundBridge.ViewModels;

namespace SoundBridge.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        WindowChromeHelper.ApplyDarkTitleBar(this);
        Loaded += OnLoaded;
        Closing += OnClosing;

        try
        {
            // 任务栏 / 窗口图标固定用品牌蓝，不跟连接状态走（否则会一直显示未连接的灰）。
            using var icon = AppIconFactory.CreateAppIcon(64);
            Icon = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
        }
        catch (Exception ex)
        {
            Log.Warn($"设置窗口图标失败: {ex.Message}");
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            await viewModel.InitializeAsync();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (App.IsExiting) return;

        if (DataContext is MainViewModel { MinimizeToTray: true })
        {
            e.Cancel = true;
            Hide();
        }
    }
}
