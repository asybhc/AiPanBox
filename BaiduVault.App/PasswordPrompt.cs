using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BaiduVault.App;

/// <summary>备份包密码输入对话框。confirm=true 时要求两次输入一致。返回 null 表示用户取消。</summary>
internal static class PasswordPrompt
{
    public static string? Show(Window owner, string title, string prompt, bool confirm)
    {
        var dlg = new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1F)),
            Foreground = Brushes.Gainsboro,
        };
        var panel = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });

        var pwd = new PasswordBox();
        System.Windows.Automation.AutomationProperties.SetAutomationId(pwd, "PwdBox");
        panel.Children.Add(pwd);

        PasswordBox? pwd2 = null;
        if (confirm)
        {
            panel.Children.Add(new TextBlock { Text = "再次输入密码：", Margin = new Thickness(0, 10, 0, 4) });
            pwd2 = new PasswordBox();
            System.Windows.Automation.AutomationProperties.SetAutomationId(pwd2, "PwdConfirmBox");
            panel.Children.Add(pwd2);
        }

        var hint = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x7B, 0x7B)), Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        panel.Children.Add(hint);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var ok = new Button { Content = "确定", Width = 76, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "取消", Width = 76 };
        btns.Children.Add(ok);
        btns.Children.Add(cancel);
        panel.Children.Add(btns);
        dlg.Content = panel;

        string? result = null;
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrEmpty(pwd.Password))
            {
                hint.Text = "密码不能为空";
                hint.Visibility = Visibility.Visible;
                return;
            }
            if (confirm && pwd.Password != pwd2!.Password)
            {
                hint.Text = "两次输入的密码不一致";
                hint.Visibility = Visibility.Visible;
                return;
            }
            result = pwd.Password;
            dlg.DialogResult = true;
            dlg.Close();
        };
        cancel.Click += (_, _) => { dlg.DialogResult = false; dlg.Close(); };
        dlg.Loaded += (_, _) => pwd.Focus();
        return dlg.ShowDialog() == true ? result : null;
    }
}
