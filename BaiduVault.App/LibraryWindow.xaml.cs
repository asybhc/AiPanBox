using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace BaiduVault.App;

public partial class LibraryWindow : Window
{
    private readonly OpenListClient _ol;
    private readonly ServiceManager _svc;
    private readonly LibraryManager _mgr;
    private List<LibraryManager.LibraryInfo> _libs = new();
    private readonly DispatcherTimer _timer;
    private bool _loading;

    public LibraryWindow(OpenListClient ol, ServiceManager svc, LibraryManager mgr)
    {
        InitializeComponent();
        _ol = ol;
        _svc = svc;
        _mgr = mgr;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _timer.Tick += async (_, _) => await LoadAsync();
        _timer.Start();
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => _timer.Stop();
    }

    private sealed class Row
    {
        public string Name { get; init; } = "";
        public string Driver { get; init; } = "";
        public string StatusText { get; init; } = "";
        public string MountPath { get; init; } = "";
        public string KeyText { get; init; } = "";
        public string? StatusDetail { get; init; }
        public string? Advice { get; init; }
        public LibraryManager.LibStatus Status { get; init; }
    }

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        StatusText.Text = "检测中…（网盘服务存储状态 + 库目录探测）";
        try
        {
            _libs = await _mgr.ListLibrariesAsync();
            Grid.ItemsSource = _libs.Select(l => new Row
            {
                Name = l.Name,
                Driver = CloudPresets.DriverDisplay(l.Driver),
                StatusText = l.Status switch
                {
                    LibraryManager.LibStatus.Normal => "正常",
                    LibraryManager.LibStatus.Expired => "凭证过期",
                    LibraryManager.LibStatus.ServiceError => "服务异常",
                    _ => "未挂载",
                },
                MountPath = l.MountPath,
                KeyText = l.HasCryptKey ? "有" : "无",
                StatusDetail = l.StatusDetail,
                Advice = l.Advice,
                Status = l.Status,
            }).ToList();
            var bad = _libs.Count(l => l.Status != LibraryManager.LibStatus.Normal);
            StatusText.Text = bad == 0
                ? $"共 {_libs.Count} 个库，全部正常（{DateTime.Now:HH:mm:ss}）"
                : $"共 {_libs.Count} 个库，{bad} 个异常（{DateTime.Now:HH:mm:ss}）";
        }
        catch (Exception ex)
        {
            StatusText.Text = "检测失败：" + ex.Message;
        }
        finally { _loading = false; }
    }

    private LibraryManager.LibraryInfo? Selected() =>
        Grid.SelectedItem is Row r ? _libs.FirstOrDefault(l => l.Name == r.Name) : null;

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void AutoCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_timer == null) return;
        if (AutoCheck.IsChecked == true) _timer.Start(); else _timer.Stop();
    }

    private void RestartService_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = "正在重启播放服务…";
            _svc.RestartServe();
            _ = LoadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "重启失败：" + ex.Message, "重启服务", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new AddLibraryWindow(_ol, _svc, _mgr) { Owner = this };
        if (dlg.ShowDialog() == true)
            _ = LoadAsync();
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        var lib = Selected();
        if (lib == null) { MessageBox.Show(this, "请先选择一个库"); return; }
        if (lib.Status == LibraryManager.LibStatus.Expired)
            MessageBox.Show(this,
                $"库 {lib.Name} 判定为「凭证过期」：\n{lib.StatusDetail}\n\n" +
                "即将打开编辑页，当前参数已预填；只需更新授权字段（登录凭证/令牌/账密）后点「完成」。" +
                "不会更换加密密钥，已上传文件不受影响。",
                "一键重登", MessageBoxButton.OK, MessageBoxImage.Information);
        var dlg = new AddLibraryWindow(_ol, _svc, _mgr, lib.Name) { Owner = this };
        if (dlg.ShowDialog() == true)
            _ = LoadAsync();
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        var lib = Selected();
        if (lib == null) { MessageBox.Show(this, "请先选择一个库"); return; }
        var newName = PromptInput("重命名库", $"为库 {lib.Name} 输入新名称：");
        if (string.IsNullOrWhiteSpace(newName)) return;
        try
        {
            await _mgr.RenameLibraryAsync(lib.Name, newName.Trim(), _svc);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "重命名失败：" + ex.Message, "库管理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Unmount_Click(object sender, RoutedEventArgs e)
    {
        var lib = Selected();
        if (lib == null) { MessageBox.Show(this, "请先选择一个库"); return; }
        var r = MessageBox.Show(this,
            $"移出挂载将从同步配置移除 {lib.Name} 的加密与分片设置以及聚合挂载，" +
            $"但保留网盘服务存储与密钥（可重新挂载恢复）。是否继续？",
            "移出挂载", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (r != MessageBoxResult.OK) return;
        try
        {
            _mgr.RemoveMount(lib.Name, _svc);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "移出挂载失败：" + ex.Message, "库管理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var lib = Selected();
        if (lib == null) { MessageBox.Show(this, "请先选择一个库"); return; }
        var warn = $"彻底删除将同时：\n1) 移出挂载\n2) 删除网盘服务存储\n3) 删除该库的加密密钥\n\n" +
                   $"⚠ 该库已上传到网盘的密文将永久无法解密，不可恢复！\n\n确认删除库 {lib.Name}？";
        var r = MessageBox.Show(this, warn, "彻底删除", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (r != MessageBoxResult.OK) return;
        try
        {
            await _mgr.DeleteLibraryAsync(lib.Name, _svc);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "删除失败：" + ex.Message, "库管理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "导出密钥备份包（含同步配置与密钥）",
            Filter = "ZIP 压缩包 (*.zip)|*.zip",
            FileName = $"baiduvault-keys-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
        };
        if (dlg.ShowDialog(this) != true) return;
        var pwd = PasswordPrompt.Show(this, "设置备份包密码",
            "为备份包设置密码（AES-256 加密打包）。\n恢复时必须输入该密码，请务必牢记。", confirm: true);
        if (pwd == null) return;
        try
        {
            LibraryManager.ExportBackup(dlg.FileName, pwd);
            MessageBox.Show(this, "已导出：" + dlg.FileName +
                "\n\n备份包已用你设置的密码加密（AES-256）。恢复时选择该文件并输入密码即可。", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "导出失败：" + ex.Message, "库管理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string? PromptInput(string title, string prompt)
    {
        var dlg = new Window
        {
            Title = title, Width = 400, Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.White,
            Foreground = Brushes.Black,
        };
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var tb = new TextBox();
        System.Windows.Automation.AutomationProperties.SetAutomationId(tb, "PromptInputBox");
        panel.Children.Add(tb);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "保存", Width = 70, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "取消", Width = 70 };
        btns.Children.Add(ok); btns.Children.Add(cancel);
        panel.Children.Add(btns);
        dlg.Content = panel;
        ok.Click += (_, _) => { dlg.DialogResult = true; dlg.Close(); };
        cancel.Click += (_, _) => { dlg.DialogResult = false; dlg.Close(); };
        return dlg.ShowDialog() == true ? tb.Text : null;
    }
}
