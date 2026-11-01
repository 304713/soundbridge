using System.Windows;
using SoundBridge.Infrastructure;

namespace SoundBridge.Views;

public partial class PrivacyWindow : Window
{
    public PrivacyWindow()
    {
        InitializeComponent();
        WindowChromeHelper.ApplyDarkTitleBar(this);
        WindowChromeHelper.ApplyAppIcon(this);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
