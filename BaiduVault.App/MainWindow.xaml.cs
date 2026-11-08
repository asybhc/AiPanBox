using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BaiduVault.App;

public partial class MainWindow : Window
{
    private const string DummyTag = "__dummy__";
    private readonly ServiceManager _svc = new();
    private readonly ResumeStore _resume = new();
    private VaultClient? _client;
    private HelpWindow? _help;

    public MainWindow()
    {
        InitializeComponent();
        // 窗口句柄一创建就切浅色标题栏（早于显示），避免先跟随系统深色模式
        WindowTheme.ApplyDarkTitleBar(this);
        // 只做初始化，不做透明度淡入：透明期间会透出深色桌面，反而像“闪深色”
        ContentRendered += async (_, _) => await InitAsync();
    }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        if (_help is { IsVisible: true }) { _help.Activate(); return; }
        _help = new HelpWindow { Owner = this };
        _help.Closed += (_, _) => _help = null;
        _help.Show();
    }

    private async Task InitAsync()
    {
        try
        {
            if (EnvProvisioner.NeedsProvisioning())
            {
                Status("首次运行，正在自动安装运行环境（约 1～2 分钟，请稍候）…");
                await Task.Run(EnvProvisioner.Ensure);
            }
            Status("正在连接本地实例（未运行则自动拉起）…");
            await Task.Run(() => _svc.EnsureRunning());
            _client = _svc.CreateClient();
            await LoadLibrariesAsync();
            Status("就绪 — 双击文件播放");
        }
        catch (Exception ex)
        {
            Status("连接失败");
            MessageBox.Show(this, ex.Message, "AiPanBox", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task LoadLibrariesAsync()
    {
        VaultTree.Items.Clear();
        var libs = await _client!.ListAsync("/");
        foreach (var lib in libs.Where(l => l.IsDirectory))
            VaultTree.Items.Add(MakeItem(lib));
    }

    private TreeViewItem MakeItem(VaultEntry entry)
    {
        var item = new TreeViewItem
        {
            Header = entry.IsDirectory ? $"📁 {entry.Name}" : $"🎬 {entry.Name}   {FormatSize(entry.Size)}",
            Tag = entry,
            Foreground = entry.IsDirectory
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1F, 0x5F, 0xA8))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x33, 0x33, 0x33)),
        };
        if (entry.IsDirectory)
            item.Items.Add(new TreeViewItem { Header = "…", Tag = DummyTag });
        return item;
    }

    private async void Item_Expanded(object sender, RoutedEventArgs e)
    {
        // TreeViewItem.Expanded 是路由事件：sender 是 TreeView，真正的节点在 OriginalSource
        if (e.OriginalSource is not TreeViewItem item) return;
        if (item.Items.Count != 1 || item.Items[0] is not TreeViewItem first || first.Tag as string != DummyTag)
            return;
        e.Handled = true;
        item.Items.Clear();
        try
        {
            var parent = (VaultEntry)item.Tag;
            var children = await _client!.ListAsync(parent.Path);
            foreach (var child in children)
                item.Items.Add(MakeItem(child));
            if (item.Items.Count == 0)
                item.Items.Add(new TreeViewItem { Header = "（空）", IsEnabled = false });
        }
        catch (Exception ex)
        {
            item.Items.Add(new TreeViewItem { Header = "加载失败：" + ex.Message, IsEnabled = false });
        }
    }

    // 挂在 TreeView 上更可靠：以当前选中节点为准（TreeViewItem 自身的 MouseDoubleClick
    // 可能因模板内部命中处理而不触发）
    private void VaultTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (VaultTree.SelectedItem is TreeViewItem { Tag: VaultEntry { IsDirectory: false } entry })
            new PlayerWindow(entry, _svc.User, _svc.Pass, _resume) { Owner = this }.Show();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_client == null) return;
        Status("刷新中…");
        try { await LoadLibrariesAsync(); Status("就绪 — 双击文件播放"); }
        catch (Exception ex) { Status("刷新失败：" + ex.Message); }
    }

    private void Libraries_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var (u, p) = OpenListClient.LoadAdminCredentials();
            var ol = new OpenListClient(u, p);
            var mgr = new LibraryManager(ol, _svc.User, _svc.Pass);
            new LibraryWindow(ol, _svc, mgr) { Owner = this }.ShowDialog();
            // 库结构可能变化，刷新主列表
            if (_client != null) _ = LoadLibrariesAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "库管理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Accounts_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var (u, p) = OpenListClient.LoadAdminCredentials();
            var ol = new OpenListClient(u, p);
            var mgr = new LibraryManager(ol, _svc.User, _svc.Pass);
            new AccountWindow(ol, _svc, mgr) { Owner = this }.ShowDialog();
            if (_client != null) _ = LoadLibrariesAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "账号管理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            new SettingsWindow(_svc) { Owner = this }.ShowDialog();
            if (_client != null) _ = LoadLibrariesAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "设置", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Upload_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var win = new UploadWindow(_svc) { Owner = this };
            win.ShowDialog();
            if (_client != null) _ = LoadLibrariesAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "上传", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Status(string text) => StatusText.Text = text;

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"({bytes / (double)(1L << 30):F1} GB)",
        >= 1L << 20 => $"({bytes / (double)(1L << 20):F1} MB)",
        >= 1L << 10 => $"({bytes / (double)(1L << 10):F0} KB)",
        _ => $"({bytes} B)",
    };
}
