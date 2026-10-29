using System.Windows;
using SoundBridge.Infrastructure;

namespace SoundBridge.Views;

public partial class WelcomeWindow : Window
{
    public WelcomeWindow(bool firstRun)
    {
        InitializeComponent();

        WindowChromeHelper.ApplyDarkTitleBar(this);

        if (!firstRun)
        {
            OkButton.Content = "关闭";
            FooterHint.Text = "随时可以关掉，不影响正在进行的连接。";
        }
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        try
        {
            DialogResult = true;   // 模态打开时这一句会自动关闭窗口
        }
        catch (InvalidOperationException)
        {
            // 非模态调用时 DialogResult 不可用，走下面的 Close
        }

        Close();
    }
}
