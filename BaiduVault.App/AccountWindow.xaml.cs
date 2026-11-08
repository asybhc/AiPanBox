using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Text.Json;

namespace BaiduVault.App;

/// <summary>账号管理：列出所有已登录的网盘账号（OpenList 存储），支持编辑凭证、移出挂载、删除账号。</summary>
public partial class AccountWindow : Window
{
    private sealed class AccountRow
    {
        public string Name { get; init; } = "";
        public string Remark { get; init; } = "";
        public string Driver { get; init; } = "";
        public string StatusText { get; init; } = "";
        public string Credentials { get; init; } = "";
        public string KeyText { get; init; } = "";
        public string MountText { get; init; } = "";
        public string Detail { get; init; } = "";
        public LibraryManager.LibraryInfo Info { get; init; } = null!;
    }

    private readonly OpenListClient _ol;
    private readonly ServiceManager _svc;
    private readonly LibraryManager _mgr;

    public AccountWindow(OpenListClient ol, ServiceManager svc, LibraryManager mgr)
    {
        InitializeComponent();
        _ol = ol;
        _svc = svc;
        _mgr = mgr;
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        StatusText.Text = "读取中…";
        try
        {
            var libs = await _mgr.ListLibrariesAsync();
            var rows = libs.Select(lib => new AccountRow
            {
                Name = lib.Name,
                Remark = lib.DisplayName == lib.Name ? "" : lib.DisplayName,
                Driver = CloudPresets.DriverDisplay(lib.Driver),
                StatusText = StatusLabel(lib.Status),
                Credentials = CredentialSummary(lib.Driver, lib.Addition),
                KeyText = lib.HasCryptKey ? "有" : "无",
                MountText = lib.Mounted ? "已挂" : "未挂",
                Detail = AdviceFor(lib),
                Info = lib,
            }).ToList();
            AccountGrid.ItemsSource = rows;

            var mounted = libs.Count(x => x.Mounted);
            HintText.Text = $"共 {libs.Count} 个账号，其中 {mounted} 个已挂载。每个账号对应一个加密库；" +
                            "删除账号会移除登录凭证与该库的挂载，加密密钥可选择保留。双击一行可编辑凭证。";
            StatusText.Text = $"已加载 {libs.Count} 个账号";
        }
        catch (Exception ex)
        {
            StatusText.Text = "加载失败";
            MessageBox.Show(this, "读取账号列表失败：" + ex.Message, "账号管理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string StatusLabel(LibraryManager.LibStatus status) => status switch
    {
        LibraryManager.LibStatus.Normal => "正常",
        LibraryManager.LibStatus.Expired => "凭证过期",
        LibraryManager.LibStatus.ServiceError => "服务异常",
        _ => "未知",
    };

    private static string AdviceFor(LibraryManager.LibraryInfo lib)
    {
        if (!lib.Mounted) return "未挂载（库不可用）";
        return lib.Status switch
        {
            LibraryManager.LibStatus.Expired => "凭证已失效，请编辑凭证重新登录",
            LibraryManager.LibStatus.ServiceError => "网盘服务异常，请重启服务",
            LibraryManager.LibStatus.Unknown => "状态无法探测",
            _ => "",
        };
    }

    /// <summary>把存储的授权 JSON 转成「字段中文名」清单，只显示已填写的字段，不展示具体值。</summary>
    private static string CredentialSummary(string driver, string addition)
    {
        if (string.IsNullOrWhiteSpace(addition)) return "（无授权字段）";
        var preset = CloudPresets.ByDriver(driver);
        try
        {
            using var doc = JsonDocument.Parse(addition);
            var labels = new List<string>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var value = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? "",
                    JsonValueKind.Number => prop.Value.ToString(),
                    JsonValueKind.True => "true",
                    _ => "",
                };
                if (value.Trim().Length == 0) continue;
                labels.Add(CloudPresets.LabelFor(preset, prop.Name));
            }
            return labels.Count == 0 ? "（无授权字段）" : string.Join("、", labels);
        }
        catch (JsonException)
        {
            return "（授权字段无法解析）";
        }
    }

    private AccountRow? Selected()
    {
        if (AccountGrid.SelectedItem is AccountRow row) return row;
        MessageBox.Show(this, "请先在列表中选择一个账号。", "账号管理", MessageBoxButton.OK, MessageBoxImage.Information);
        return null;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _svc.RestartServe();
            StatusText.Text = "播放服务已重启";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "重启服务失败：" + ex.Message, "账号管理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AccountGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => EditSelected();

    private void Edit_Click(object sender, RoutedEventArgs e) => EditSelected();

    private void EditSelected()
    {
        var row = Selected();
        if (row == null) return;
        var dlg = new AddLibraryWindow(_ol, _svc, _mgr, row.Info.Name) { Owner = this };
        if (dlg.ShowDialog() == true) _ = ReloadAsync();
    }

    private async void Unmount_Click(object sender, RoutedEventArgs e)
    {
        var row = Selected();
        if (row == null) return;
        if (!row.Info.Mounted)
        {
            MessageBox.Show(this, "该账号尚未挂载，无需移出。", "账号管理", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var answer = MessageBox.Show(this,
            $"将「{row.Name}」移出挂载（加密与分片设置会从同步配置中移除，登录凭证和密钥保留）。\n\n之后可用「编辑凭证」重新挂载。继续？",
            "移出挂载", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            await _mgr.RemoveMountAsync(row.Name, _svc);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "移出挂载失败：" + ex.Message, "账号管理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var row = Selected();
        if (row == null) return;

        var (ok, deleteKeys) = ConfirmDelete(row);
        if (!ok) return;

        try
        {
            await _mgr.DeleteAccountAsync(row.Name, _svc, deleteKeys);
            await ReloadAsync();
            MessageBox.Show(this,
                $"账号「{row.Name}」已删除" + (deleteKeys ? "（含加密密钥）。" : "，加密密钥已保留。"),
                "账号管理", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "删除账号失败：" + ex.Message, "账号管理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>删除确认对话框：返回 (是否继续, 是否同时删除密钥)。</summary>
    private (bool Ok, bool DeleteKeys) ConfirmDelete(AccountRow row)
    {
        var dlg = new Window
        {
            Title = "删除账号", Width = 520, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
            Background = Brushes.White,
            Foreground = Brushes.Black, ResizeMode = ResizeMode.NoResize,
        };
        var panel = new StackPanel { Margin = new Thickness(18, 16, 18, 14) };
        panel.Children.Add(new TextBlock
        {
            Text = $"即将删除账号「{row.Name}」（{row.Driver}）：\n\n" +
                   "1. 从同步配置中移出该库的加密与分片挂载\n" +
                   "2. 删除网盘服务中保存的登录凭证（该账号将不再登录）\n\n" +
                   "这会立即影响正在进行的上传/播放，请先停止相关操作。",
            TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)), LineHeight = 22,
        });

        var keepKey = new CheckBox
        {
            Content = "同时删除该账号的加密密钥（不可恢复，已有密文将永久无法解密）",
            Margin = new Thickness(0, 14, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B)),
            IsEnabled = row.Info.HasCryptKey,
        };
        panel.Children.Add(keepKey);
        panel.Children.Add(new TextBlock
        {
            Text = row.Info.HasCryptKey
                ? "不勾选则只删除登录凭证，密钥保留，日后用同一账号重新添加时仍可解密已有文件。"
                : "该账号没有可删除的密钥。",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 6, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "取消", Width = 82, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var confirm = new Button { Content = "删除账号", Width = 96 };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        panel.Children.Add(buttons);
        dlg.Content = panel;

        bool ok = false;
        cancel.Click += (_, _) => dlg.Close();
        confirm.Click += (_, _) => { ok = true; dlg.Close(); };
        dlg.ShowDialog();
        return (ok, ok && keepKey.IsChecked == true);
    }
}
