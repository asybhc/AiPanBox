using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace BaiduVault.App;

public partial class SettingsWindow : Window
{
    private readonly ServiceManager _svc;
    private readonly AppSettings _snap; // 打开时的快照，用于判断是否需要重启

    public SettingsWindow(ServiceManager svc)
    {
        InitializeComponent();
        _svc = svc;
        _snap = new AppSettings
        {
            OpenListPort = AppSettings.Current.OpenListPort,
            WebDavPort = AppSettings.Current.WebDavPort,
            CacheMaxSize = AppSettings.Current.CacheMaxSize,
            HardwareDecoding = AppSettings.Current.HardwareDecoding,
            SubtitleEncoding = AppSettings.Current.SubtitleEncoding,
            AutoStart = AppSettings.Current.AutoStart,
            ClearCacheOnExit = AppSettings.Current.ClearCacheOnExit,
            UploadConcurrency = AppSettings.Current.UploadConcurrency,
            AudioOutputDevice = AppSettings.Current.AudioOutputDevice,
            DefaultPlayBehavior = AppSettings.Current.DefaultPlayBehavior,
        };
        LoadControls();
        Loaded += (_, _) => RefreshLogs();
    }

    private static readonly string Root =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BaiduVault");

    private void LoadControls()
    {
        OpenListPortBox.Text = AppSettings.Current.OpenListPort.ToString();
        WebDavPortBox.Text = AppSettings.Current.WebDavPort.ToString();
        CacheMaxSizeBox.Text = AppSettings.Current.CacheMaxSize;
        HardwareDecodingBox.IsChecked = AppSettings.Current.HardwareDecoding;
        SubtitleEncodingBox.Text = AppSettings.Current.SubtitleEncoding;
        ClearCacheOnExitBox.IsChecked = AppSettings.Current.ClearCacheOnExit;
        AutoStartBox.IsChecked = AppSettings.Current.AutoStart;
        UploadConcurrencyBox.Text = AppSettings.Current.UploadConcurrency.ToString();
        DefaultPlayBehaviorBox.SelectedIndex = AppSettings.Current.DefaultPlayBehavior == "start" ? 1 : 0;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // 校验
        if (!int.TryParse(OpenListPortBox.Text.Trim(), out var olPort) || olPort < 1 || olPort > 65535)
        { MessageBox.Show(this, "网盘服务端口必须是 1-65535 的整数", "设置", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (!int.TryParse(WebDavPortBox.Text.Trim(), out var wdPort) || wdPort < 1 || wdPort > 65535)
        { MessageBox.Show(this, "播放服务端口必须是 1-65535 的整数", "设置", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (string.IsNullOrWhiteSpace(CacheMaxSizeBox.Text))
        { MessageBox.Show(this, "缓存上限不能为空", "设置", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (!int.TryParse(UploadConcurrencyBox.Text.Trim(), out var conc) || conc < 1 || conc > 64)
        { MessageBox.Show(this, "上传并发数必须是 1-64 的整数", "设置", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

        var subEnc = string.IsNullOrWhiteSpace(SubtitleEncodingBox.Text) ? "GB18030" : SubtitleEncodingBox.Text.Trim();
        var playBehavior = DefaultPlayBehaviorBox.SelectedItem is ComboBoxItem cbi && (string)cbi.Tag == "start" ? "start" : "resume";

        var changed =
            olPort != _snap.OpenListPort ||
            wdPort != _snap.WebDavPort ||
            CacheMaxSizeBox.Text.Trim() != _snap.CacheMaxSize;

        AppSettings.Current.OpenListPort = olPort;
        AppSettings.Current.WebDavPort = wdPort;
        AppSettings.Current.CacheMaxSize = CacheMaxSizeBox.Text.Trim();
        AppSettings.Current.HardwareDecoding = HardwareDecodingBox.IsChecked == true;
        AppSettings.Current.SubtitleEncoding = subEnc;
        AppSettings.Current.ClearCacheOnExit = ClearCacheOnExitBox.IsChecked == true;
        AppSettings.Current.AutoStart = AutoStartBox.IsChecked == true;
        AppSettings.Current.UploadConcurrency = conc;
        AppSettings.Current.DefaultPlayBehavior = playBehavior;
        AppSettings.Current.Save();

        // 开机自启 → HKCU Run
        ApplyAutoStart(AutoStartBox.IsChecked == true);

        // 端口/缓存变更 → 重启服务
        if (changed)
        {
            try
            {
                if (olPort != _snap.OpenListPort) _svc.RestartOpenList();
                if (wdPort != _snap.WebDavPort || CacheMaxSizeBox.Text.Trim() != _snap.CacheMaxSize)
                    _svc.RestartServe();
                MessageBox.Show(this, "保存成功，相关服务已重启。", "设置", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存成功，但重启服务失败：" + ex.Message, "设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        else
        {
            MessageBox.Show(this, "保存成功。", "设置", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ApplyAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            var exePath = Process.GetCurrentProcess().MainModule?.FileName
                          ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (enable)
                key.SetValue("BaiduVault.App", $"\"{exePath}\"");
            else
                key.DeleteValue("BaiduVault.App", false);
        }
        catch { /* 注册表写入失败不阻塞 */ }
    }

    // ---------- 密钥备份 ----------

    private void ExportBackup_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "导出密钥备份包",
            Filter = "ZIP 备份包 (*.zip)|*.zip",
            FileName = $"baiduvault-backup-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dlg.ShowDialog(this) != true) return;
        var pwd = PasswordPrompt.Show(this, "设置备份包密码",
            "为备份包设置密码（AES-256 加密打包）。\n恢复时必须输入该密码，请务必牢记。", confirm: true);
        if (pwd == null) return;
        try
        {
            LibraryManager.ExportBackup(dlg.FileName, pwd);
            MessageBox.Show(this, $"已导出到：\n{dlg.FileName}\n\n备份包已用你设置的密码加密（AES-256）。丢密码 = 无法恢复，请妥善保管。",
                "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "导出失败：" + ex.Message, "设置", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ImportBackup_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "导入密钥备份包",
            Filter = "ZIP 备份包 (*.zip)|*.zip",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        string? pwd = null;
        if (LibraryManager.IsBackupEncrypted(dlg.FileName))
        {
            pwd = PasswordPrompt.Show(this, "输入备份包密码", "该备份包已加密，请输入导出时设置的密码：", confirm: false);
            if (pwd == null) return;
        }

        List<string> libs;
        try
        {
            libs = LibraryManager.PreviewBackup(dlg.FileName, pwd);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "读取备份包失败：" + ex.Message, "设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var msg = new StringBuilder();
        msg.AppendLine("即将导入的备份包包含以下库：");
        msg.AppendLine(string.Join("、", libs.Count > 0 ? libs : new[] { "(无)" }));
        msg.AppendLine();
        msg.AppendLine("⚠ 警告：导入将覆盖当前的同步配置和全部密钥，并会重启播放服务。");
        msg.AppendLine("当前库的密钥会被替换为备份包中的密钥。此操作不可撤销（已自动备份当前状态到 backup-library-mgr）。");
        msg.AppendLine();
        msg.AppendLine("是否继续？");

        if (MessageBox.Show(this, msg.ToString(), "确认导入", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            var (u, p) = OpenListClient.LoadAdminCredentials();
            var ol = new OpenListClient(u, p);
            var mgr = new LibraryManager(ol, _svc.User, _svc.Pass);
            LibraryManager.ImportBackup(dlg.FileName, _svc, pwd);
            MessageBox.Show(this, "导入成功，服务已重启。请返回主窗口刷新库列表。",
                "导入成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "导入失败：" + ex.Message, "设置", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------- 日志 ----------

    private void RefreshLogs_Click(object sender, RoutedEventArgs e) => RefreshLogs();

    private void RefreshLogs()
    {
        var sb = new StringBuilder();
        AppendLog(sb, Path.Combine(Root, "logs", "rclone-webdav.log"), "rclone-webdav.log");
        AppendLog(sb, Path.Combine(Root, "logs", "openlist-out.log"), "openlist-out.log");
        AppendLog(sb, Path.Combine(Root, "logs", "openlist-err.log"), "openlist-err.log");
        var openlistLog = Path.Combine(Root, "openlist-data", "log", "log.log");
        if (File.Exists(openlistLog))
            AppendLog(sb, openlistLog, "openlist-data/log/log.log");
        else
        {
            // 尝试 openlist-data/log 下的其他日志
            var logDir = Path.Combine(Root, "openlist-data", "log");
            if (Directory.Exists(logDir))
            {
                foreach (var f in Directory.GetFiles(logDir, "*.log").OrderByDescending(f => f).Take(1))
                    AppendLog(sb, f, "openlist log");
            }
        }
        LogBox.Text = sb.ToString();
        LogBox.ScrollToEnd();
    }

    private static void AppendLog(StringBuilder sb, string path, string label)
    {
        sb.AppendLine($"===== {label} =====");
        if (!File.Exists(path)) { sb.AppendLine("(文件不存在)"); sb.AppendLine(); return; }
        try
        {
            var lines = File.ReadAllLines(path);
            var tail = lines.Skip(Math.Max(0, lines.Length - 80));
            foreach (var l in tail) sb.AppendLine(l);
        }
        catch (Exception ex) { sb.AppendLine($"(读取失败: {ex.Message})"); }
        sb.AppendLine();
    }

    private void OpenLogDir_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(Root, "logs");
        if (!Directory.Exists(dir)) dir = Root;
        try { Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true }); }
        catch { }
    }
}
