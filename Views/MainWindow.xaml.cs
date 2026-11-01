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

        // 任务栏 / 窗口图标固定用品牌蓝，不跟连接状态走（否则会一直显示未连接的灰）。
        WindowChromeHelper.ApplyAppIcon(this);

        Loaded += OnLoaded;
        Closing += OnClosing;
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
