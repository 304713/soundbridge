using System.Windows;
using System.Windows.Controls;
using SoundBridge.Infrastructure;

namespace SoundBridge.Views;

/// <summary>
/// 自定义定时时长。整页就一个输入框，所以不弹 MessageBox 做校验——错在哪当场写在旁边。
/// </summary>
public partial class TimerWindow : Window
{
    /// <summary>能填的范围。10 小时已经远超「睡前定个时」的用途，再大不如不连。</summary>
    public const int MinMinutes = 1;
    public const int MaxMinutes = 600;

    private TimerWindow(int suggested)
    {
        InitializeComponent();

        WindowChromeHelper.ApplyDarkTitleBar(this);
        WindowChromeHelper.ApplyAppIcon(this);

        MinutesInput.Text = Math.Clamp(suggested, MinMinutes, MaxMinutes).ToString();
        Loaded += (_, _) =>
        {
            MinutesInput.Focus();
            MinutesInput.SelectAll();
        };
    }

    /// <summary>弹出对话框拿一个分钟数；用户取消返回 null。</summary>
    public static int? Ask(Window? owner, int suggested)
    {
        var window = new TimerWindow(suggested);
        if (owner is not null && owner.IsVisible) window.Owner = owner;

        return window.ShowDialog() == true ? window._result : null;
    }

    private int? _result;

    private void OnMinutesChanged(object sender, TextChangedEventArgs e)
    {
        // 只留数字：粘贴进来「20分钟」这种也别报错，直接取数字部分
        var digits = new string(MinutesInput.Text.Where(char.IsDigit).ToArray());
        if (digits != MinutesInput.Text)
        {
            var caret = MinutesInput.CaretIndex;
            MinutesInput.Text = digits;
            MinutesInput.CaretIndex = Math.Min(caret, digits.Length);
        }

        var ok = int.TryParse(MinutesInput.Text, out var value)
                 && value >= MinMinutes && value <= MaxMinutes;

        ErrorText.Text = MinutesInput.Text.Length == 0
            ? string.Empty
            : ok ? string.Empty : $"{MinMinutes}–{MaxMinutes}";

        OkButton.IsEnabled = ok;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(MinutesInput.Text, out var value) || value < MinMinutes || value > MaxMinutes)
        {
            ErrorText.Text = $"{MinMinutes}–{MaxMinutes}";
            return;
        }

        _result = value;
        DialogResult = true;
    }
}
