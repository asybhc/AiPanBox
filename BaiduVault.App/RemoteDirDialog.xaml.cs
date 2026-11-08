using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace BaiduVault.App;

/// <summary>浏览目标库的远端目录树（经 rclone serve webdav，路径已解密可见）。</summary>
public partial class RemoteDirDialog : Window
{
    private static readonly string Root =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BaiduVault");
    private static readonly string RcloneExe = Path.Combine(Root, "bin", "rclone.exe");
    private static readonly string ConfPath = Path.Combine(Root, "rclone.conf");

    private readonly VaultClient _client;
    private readonly string _lib;
    private string _path = "/";

    private sealed class DirItem
    {
        public string Name { get; init; } = "";
        public string Path { get; init; } = "";
        public override string ToString() => "📁 " + Name;
    }

    public string? SelectedPath { get; private set; }

    public RemoteDirDialog(ServiceManager svc, string lib, string startPath = "/")
    {
        InitializeComponent();
        _lib = lib;
        _client = svc.CreateClient();
        Title = $"选择远端目录 - 库 {lib}";
        LibText.Text = $"目标库：{lib}";
        _path = Normalize(startPath);
        Loaded += async (_, _) => await LoadAsync();
    }

    private static string Normalize(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return "/";
        p = p.Trim();
        if (!p.StartsWith('/')) p = "/" + p;
        if (p.Length > 1) p = p.TrimEnd('/');
        return p;
    }

    private string FullPath() => _lib + (_path == "/" ? "/" : _path + "/");

    private async Task LoadAsync()
    {
        DirList.Items.Clear();
        LoadingText.Text = "加载中…";
        PathText.Text = $"当前：{_lib}:{_path}";
        try
        {
            var entries = await _client.ListAsync(FullPath());
            foreach (var e in entries.Where(x => x.IsDirectory))
            {
                DirList.Items.Add(new DirItem
                {
                    Name = e.Name,
                    Path = Normalize(e.Path.Replace("/" + _lib, "", StringComparison.OrdinalIgnoreCase)),
                });
            }
            DirCountText.Text = $"共 {DirList.Items.Count} 个子目录";
            LoadingText.Text = "";
        }
        catch (Exception ex)
        {
            LoadingText.Text = "加载失败：" + ex.Message;
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_path == "/") return;
        var idx = _path.LastIndexOf('/');
        _path = idx <= 0 ? "/" : _path[..idx];
        await LoadAsync();
    }

    private async void DirList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DirList.SelectedItem is DirItem item)
        {
            _path = item.Path;
            await LoadAsync();
        }
    }

    private void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptInput("新建远端文件夹", "输入文件夹名（不能含 /）：");
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim().Trim('/');
        if (name.Length == 0) return;
        var dest = $"{_lib}_chunk512:{(_path == "/" ? "/" : _path + "/")}{name}";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = RcloneExe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--config"); psi.ArgumentList.Add(ConfPath);
            psi.ArgumentList.Add("mkdir"); psi.ArgumentList.Add(dest);
            using var p = Process.Start(psi)!;
            p.WaitForExit(20000);
            if (p.ExitCode != 0)
                throw new InvalidOperationException(p.StandardError.ReadToEnd());
            _ = LoadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "新建失败：" + ex.Message, "远端目录", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Choose_Click(object sender, RoutedEventArgs e)
    {
        SelectedPath = _path;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static string? PromptInput(string title, string prompt)
    {
        var dlg = new Window
        {
            Title = title, Width = 420, Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.White,
            Foreground = System.Windows.Media.Brushes.Black,
        };
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var tb = new TextBox();
        System.Windows.Automation.AutomationProperties.SetAutomationId(tb, "PromptInputBox");
        panel.Children.Add(tb);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "确定", Width = 70, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "取消", Width = 70 };
        btns.Children.Add(ok); btns.Children.Add(cancel);
        panel.Children.Add(btns);
        dlg.Content = panel;
        ok.Click += (_, _) => { dlg.DialogResult = true; dlg.Close(); };
        cancel.Click += (_, _) => { dlg.DialogResult = false; dlg.Close(); };
        return dlg.ShowDialog() == true ? tb.Text : null;
    }
}
