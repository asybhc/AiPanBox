using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Win32;

namespace BaiduVault.App;

public sealed class UploadTask : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public string Source { get; init; } = "";
    public string Dest { get; init; } = "";
    public bool IsDir { get; init; }
    public long Size { get; init; }

    private string _status = "待上传";
    private double _progress;
    private string _speed = "";
    private string _verify = "";
    private string _result = "";

    public string Status { get => _status; set => Set(ref _status, value, nameof(Status)); }
    public double Progress
    {
        get => _progress;
        set { Set(ref _progress, value, nameof(Progress)); OnPropertyChanged(nameof(ProgressText)); }
    }
    public string Speed { get => _speed; set => Set(ref _speed, value, nameof(Speed)); }
    public string Verify { get => _verify; set => Set(ref _verify, value, nameof(Verify)); }
    public string Result { get => _result; set => Set(ref _result, value, nameof(Result)); }

    public string SizeText => UploadWindow.FormatSize(Size);
    public string ProgressText => Status == "已完成" ? "100%" : $"{Math.Round(_progress)}%";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    private void Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public partial class UploadWindow : Window
{
    private readonly ServiceManager _svc;
    private readonly List<string> _sources = new();
    private readonly ObservableCollection<UploadTask> _queue = new();
    private Process? _proc;
    private CancellationTokenSource? _queueCts;
    private CancellationTokenSource? _taskCts;
    private bool _running;
    private bool _paused;
    private string? _lib;

    private static readonly string Root =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BaiduVault");
    private static readonly string RcloneExe = Path.Combine(Root, "bin", "rclone.exe");
    private static readonly string ConfPath = Path.Combine(Root, "rclone.conf");

    public UploadWindow(ServiceManager svc)
    {
        InitializeComponent();
        _svc = svc;
        QueueGrid.ItemsSource = _queue;
        ConcurrencyBox.Text = AppSettings.Current.UploadConcurrency.ToString();
        RetriesBox.Text = AppSettings.Current.UploadRetries.ToString();
        SkipExistingBox.IsChecked = AppSettings.Current.UploadSkipExisting;
        VerifyBox.IsChecked = AppSettings.Current.UploadVerify;
        Loaded += async (_, _) => await LoadLibrariesAsync();
    }

    private async Task LoadLibrariesAsync()
    {
        try
        {
            var (u, p) = OpenListClient.LoadAdminCredentials();
            var ol = new OpenListClient(u, p);
            var mgr = new LibraryManager(ol, _svc.User, _svc.Pass);
            var libs = await mgr.ListLibrariesAsync();
            TargetLibBox.Items.Clear();
            foreach (var lib in libs.Where(l => l.Mounted))
                TargetLibBox.Items.Add(lib.Name);
            if (TargetLibBox.Items.Count > 0) TargetLibBox.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "加载库列表失败：" + ex.Message, "上传", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void TargetLibBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (TargetLibBox.SelectedItem is not string lib) return;
        _lib = lib;
        var conc = AppSettings.Current.LibraryConcurrency.TryGetValue(lib, out var v)
            ? v : AppSettings.Current.UploadConcurrency;
        ConcurrencyBox.Text = conc.ToString();
    }

    // ---------- 源文件选择 ----------

    private void PickFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Multiselect = true, Title = "选择要上传的文件" };
        if (dlg.ShowDialog(this) == true) AddSources(dlg.FileNames);
    }

    private void PickFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择要上传的文件夹" };
        if (dlg.ShowDialog(this) == true)
            AddSources(new[] { dlg.FolderName });
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
            AddSources(files);
    }

    private void AddSources(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (!_sources.Contains(p, StringComparer.OrdinalIgnoreCase))
            {
                _sources.Add(p);
                SourceList.Items.Add(p);
            }
        }
        UpdateEstimate();
    }

    private void ClearSources_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        _sources.Clear();
        SourceList.Items.Clear();
        _queue.Clear();
        UpdateEstimate();
        QueueText.Text = "";
    }

    private void UpdateEstimate()
    {
        long total = 0;
        int fileCount = 0;
        foreach (var s in _sources)
        {
            try
            {
                if (Directory.Exists(s))
                {
                    foreach (var f in Directory.EnumerateFiles(s, "*", SearchOption.AllDirectories))
                    { total += new FileInfo(f).Length; fileCount++; }
                }
                else if (File.Exists(s))
                { total += new FileInfo(s).Length; fileCount++; }
            }
            catch { }
        }
        EstimateText.Text = fileCount > 0
            ? $"共 {fileCount} 个文件，约 {FormatSize(total)}"
            : "";
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (_lib == null)
        {
            MessageBox.Show(this, "请先选择目标库", "上传", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var dlg = new RemoteDirDialog(_svc, _lib, TargetPrefixBox.Text) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.SelectedPath != null)
            TargetPrefixBox.Text = dlg.SelectedPath;
    }

    // ---------- 队列 ----------

    private void BuildQueue()
    {
        _queue.Clear();
        var prefix = NormalizePrefix(TargetPrefixBox.Text);
        foreach (var src in _sources)
        {
            var isDir = Directory.Exists(src);
            var name = Path.GetFileName(src.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(name)) name = "upload";
            long size = 0;
            try
            {
                if (isDir)
                    foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                        size += new FileInfo(f).Length;
                else if (File.Exists(src)) size = new FileInfo(src).Length;
            }
            catch { }
            var dest = $"{_lib}_chunk512:{JoinRemote(prefix, name)}";
            _queue.Add(new UploadTask
            {
                Name = name,
                Source = src,
                Dest = dest,
                IsDir = isDir,
                Size = size,
            });
        }
        QueueText.Text = $"队列 {_queue.Count} 项";
    }

    private static string NormalizePrefix(string text)
    {
        var p = text.Trim();
        if (p.Length == 0) p = "/";
        if (!p.StartsWith('/')) p = "/" + p;
        return p;
    }

    private static string JoinRemote(string prefix, string name) =>
        prefix.EndsWith('/') ? prefix + name : prefix + "/" + name;

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_sources.Count == 0)
        { MessageBox.Show(this, "请先选择要上传的文件或文件夹", "上传", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (TargetLibBox.SelectedItem is not string lib)
        { MessageBox.Show(this, "请选择目标库", "上传", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (!int.TryParse(ConcurrencyBox.Text.Trim(), out var conc) || conc < 1) conc = 4;
        if (!int.TryParse(RetriesBox.Text.Trim(), out var retries) || retries < 0) retries = 3;
        _lib = lib;

        AppSettings.Current.UploadConcurrency = conc;
        AppSettings.Current.UploadRetries = retries;
        AppSettings.Current.UploadSkipExisting = SkipExistingBox.IsChecked == true;
        AppSettings.Current.UploadVerify = VerifyBox.IsChecked == true;
        AppSettings.Current.LibraryConcurrency[lib] = conc;
        AppSettings.Current.Save();

        BuildQueue();
        LogBox.Clear();
        _paused = false;
        _queueCts = new CancellationTokenSource();
        await RunQueueAsync();
    }

    private async Task RunQueueAsync()
    {
        if (_running) return;
        _running = true;
        UpdateButtons();
        var conc = int.TryParse(ConcurrencyBox.Text.Trim(), out var c0) ? Math.Max(1, c0) : 4;
        var retries = int.TryParse(RetriesBox.Text.Trim(), out var r0) ? Math.Max(0, r0) : 3;
        var skip = SkipExistingBox.IsChecked == true;
        var verify = VerifyBox.IsChecked == true;
        var succeeded = 0;
        var failed = 0;

        try
        {
            var i = 0;
            while (i < _queue.Count)
            {
                if (_queueCts!.IsCancellationRequested)
                {
                    if (_queue[i].Status == "待上传") _queue[i].Status = "已取消";
                    i++;
                    continue;
                }
                var task = _queue[i];
                if (task.Status == "已完成") { i++; continue; }

                while (_paused && !_queueCts.IsCancellationRequested)
                {
                    task.Status = "已暂停";
                    await Task.Delay(300);
                }
                if (_queueCts.IsCancellationRequested)
                {
                    if (task.Status == "待上传") task.Status = "已取消";
                    i++;
                    continue;
                }

                QueueText.Text = $"队列 {i + 1}/{_queue.Count}";
                AppendLog($"[{i + 1}/{_queue.Count}] {task.Source}  →  {task.Dest}");
                var completed = await RunTaskAsync(task, conc, retries, skip);

                if (task.Status == "已暂停") continue;      // 点「继续」后重跑同一任务
                if (task.Status == "已取消") { failed++; i++; continue; }
                if (completed && verify) await VerifyAsync(task);
                if (task.Status == "已完成") succeeded++; else failed++;
                i++;
            }

            if (succeeded > 0)
            {
                try
                {
                    await ForgetVfsAsync();
                    AppendLog("已刷新播放缓存索引，新上传的文件可立即播放。");
                }
                catch (Exception ex)
                {
                    AppendLog("刷新播放缓存索引失败（不影响已上传文件）：" + ex.Message);
                }
            }
        }
        finally
        {
            _running = false;
            _queueCts?.Dispose();
            _queueCts = null;
            UpdateButtons();
        }

        var msg = $"队列结束：成功 {succeeded}，失败/取消 {failed}。";
        AppendLog(msg);
        QueueText.Text = msg;
        if (failed > 0)
            MessageBox.Show(this, msg + "\n\n失败项可点「重试失败」重新上传（已完成的文件会自动跳过）。",
                "上传", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async Task<bool> RunTaskAsync(UploadTask task, int conc, int retries, bool skip)
    {
        if (_paused) { task.Status = "已暂停"; return false; }
        task.Status = "上传中";
        task.Progress = 0;
        task.Speed = "";
        task.Verify = "";
        task.Result = "";

        var args = new List<string> { "--config", ConfPath };
        if (task.IsDir) { args.Add("copy"); args.Add(task.Source); args.Add(task.Dest); }
        else { args.Add("copyto"); args.Add(task.Source); args.Add(task.Dest); }
        if (skip) args.Add("--ignore-existing");
        args.AddRange(new[]
        {
            "--stats", "1s", "--stats-one-line", "--stats-log-level", "NOTICE",
            "--transfers", conc.ToString(),
            "--retries", retries.ToString(),
            "--low-level-retries", "3",
            "-v",
        });

        _taskCts = new CancellationTokenSource();
        var ct = _taskCts.Token;
        var err = new StringBuilder();
        var ok = await RunRcloneAsync(args, ct, line =>
        {
            ParseStatsLine(task, line);
            if (!IsStatsLine(line)) err.AppendLine(line);
        });

        if (ok)
        {
            task.Status = "已完成";
            task.Progress = 100;
        }
        else if (_paused)
        {
            task.Status = "已暂停";
        }
        else if (ct.IsCancellationRequested)
        {
            task.Status = "已取消";
        }
        else
        {
            task.Status = "失败";
            var tail = err.ToString().Trim();
            task.Result = tail.Length > 300 ? tail[^300..] : tail;
        }
        _taskCts?.Dispose();
        _taskCts = null;
        return ok;
    }

    private async Task<bool> RunRcloneAsync(List<string> args, CancellationToken ct, Action<string>? onLine)
    {
        var psi = new ProcessStartInfo
        {
            FileName = RcloneExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _proc = proc;
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) Dispatcher.BeginInvoke(() => onLine?.Invoke(e.Data));
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) Dispatcher.BeginInvoke(() => onLine?.Invoke(e.Data));
        };

        try
        {
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            using var reg = ct.Register(() => { try { proc.Kill(true); } catch { } });
            await Task.Run(() => proc.WaitForExit());
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            AppendLog("启动上传程序失败：" + ex.Message);
            return false;
        }
        finally
        {
            try { proc.Dispose(); } catch { }
            if (_proc == proc) _proc = null;
        }
    }

    // rclone 1.6x 的 --stats-one-line 输出形如：
    //   NOTICE:   34.500 MiB / 200.000 MiB, 17%, 6.800 MiB/s, ETA 24s
    // 旧版带 "Transferred:" 前缀，这里同时兼容两种写法。
    private static readonly Regex StatsRegex = new(
        @"(?:Transferred:)?\s*[\d.]+\s*[A-Za-z]+\s*/\s*[\d.]+\s*[A-Za-z]+,\s*(\d+)%", RegexOptions.Compiled);
    private static readonly Regex SpeedRegex = new(
        @"([\d.]+(?:\s*[kMGTP]?i?B)?/s)", RegexOptions.Compiled);

    private static bool IsStatsLine(string line) =>
        (line.Contains("NOTICE:") || line.Contains("Transferred:"))
        && line.Contains("%") && line.Contains(" / ");

    private static void ParseStatsLine(UploadTask task, string line)
    {
        if (!IsStatsLine(line)) return;
        var m = StatsRegex.Match(line);
        if (m.Success && double.TryParse(m.Groups[1].Value, out var pct))
            task.Progress = Math.Min(100, Math.Max(0, pct));
        var sm = SpeedRegex.Match(line);
        if (sm.Success) task.Speed = sm.Groups[1].Value;
    }

    // ---------- 校验 ----------

    private async Task VerifyAsync(UploadTask task)
    {
        task.Verify = "校验中…";
        try
        {
            var remoteSize = task.IsDir
                ? await GetRemoteDirSizeAsync(task.Dest)
                : await GetRemoteFileSizeAsync(task.Dest);
            if (remoteSize < 0)
            {
                task.Verify = "无法读取远端大小（跳过）";
                return;
            }
            if (remoteSize != task.Size)
            {
                task.Verify = $"大小不一致：本地 {FormatSize(task.Size)} / 远端 {FormatSize(remoteSize)}";
                task.Status = "失败";
                return;
            }
            if (task.IsDir)
            {
                task.Verify = $"大小一致（{FormatSize(remoteSize)}）";
                return;
            }

            var (hashOk, remoteHash) = await TryRemoteSha256Async(task.Dest);
            if (!hashOk || remoteHash == null)
            {
                task.Verify = "大小一致（远端不支持哈希）";
                return;
            }
            var localHash = await Task.Run(() => Sha256File(task.Source));
            task.Verify = string.Equals(localHash, remoteHash, StringComparison.OrdinalIgnoreCase)
                ? "哈希一致（SHA-256）"
                : "哈希不一致";
            if (!string.Equals(localHash, remoteHash, StringComparison.OrdinalIgnoreCase))
                task.Status = "失败";
        }
        catch (Exception ex)
        {
            task.Verify = "校验失败：" + ex.Message;
        }
    }

    private async Task<long> GetRemoteFileSizeAsync(string dest)
    {
        var (code, stdout, _) = await RunCaptureAsync(new List<string>
        {
            "--config", ConfPath, "lsjson", dest, "--stat", "--no-mimetype",
        });
        if (code != 0) return -1;
        using var doc = JsonDocument.Parse(stdout);
        var s = doc.RootElement.TryGetProperty("Size", out var v) ? v.ToString() : "0";
        return long.TryParse(s, out var n) ? n : -1;
    }

    private async Task<long> GetRemoteDirSizeAsync(string dest)
    {
        var (code, stdout, _) = await RunCaptureAsync(new List<string>
        {
            "--config", ConfPath, "size", dest, "--json",
        });
        if (code != 0) return -1;
        using var doc = JsonDocument.Parse(stdout);
        var b = doc.RootElement.TryGetProperty("bytes", out var v) ? v.ToString() : "0";
        return long.TryParse(b, out var n) ? n : -1;
    }

    private async Task<(bool Ok, string? Hash)> TryRemoteSha256Async(string dest)
    {
        var (code, stdout, _) = await RunCaptureAsync(new List<string>
        {
            "--config", ConfPath, "hashsum", "SHA-256", dest,
        });
        if (code != 0) return (false, null);
        var m = Regex.Match(stdout, "\\b([0-9a-fA-F]{64})\\b");
        return m.Success ? (true, m.Groups[1].Value) : (false, null);
    }

    private static string Sha256File(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        var hash = sha.ComputeHash(fs);
        return Convert.ToHexString(hash);
    }

    private async Task<(int Code, string Stdout, string Stderr)> RunCaptureAsync(List<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = RcloneExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        await Task.Run(() => p.WaitForExit());
        return (p.ExitCode, await outTask, await errTask);
    }

    // ---------- 控制 ----------

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (!_running) return;
        _paused = true;
        ClearRunningSpeed();
        AppendLog("已暂停（当前任务中断，点「继续」从该任务重跑；已完成的文件不会重传）。");
        try { _taskCts?.Cancel(); } catch { }
        try { _proc?.Kill(true); } catch { }
        UpdateButtons();
    }

    private void Resume_Click(object sender, RoutedEventArgs e)
    {
        if (!_running) return;
        _paused = false;
        AppendLog("继续上传…");
        UpdateButtons();
    }

    private void CancelTask_Click(object sender, RoutedEventArgs e)
    {
        ClearRunningSpeed();
        AppendLog("已取消当前任务。");
        try { _taskCts?.Cancel(); } catch { }
        try { _proc?.Kill(true); } catch { }
    }

    private void CancelAll_Click(object sender, RoutedEventArgs e)
    {
        _paused = false;
        try { _queueCts?.Cancel(); } catch { }
        try { _taskCts?.Cancel(); } catch { }
        try { _proc?.Kill(true); } catch { }
        AppendLog("已取消全部剩余任务。");
        UpdateButtons();
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        var retry = _queue.Where(t => t.Status is "失败" or "已取消" or "已暂停").ToList();
        if (retry.Count == 0)
        {
            MessageBox.Show(this, "没有可重试的任务。", "重试", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        foreach (var t in retry)
        {
            t.Status = "待上传";
            t.Progress = 0;
            t.Speed = "";
            t.Verify = "";
            t.Result = "";
        }
        _paused = false;
        _queueCts = new CancellationTokenSource();
        await RunQueueAsync();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            var r = MessageBox.Show(this, "上传仍在进行，确定要停止并关闭吗？",
                "上传", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (r != MessageBoxResult.OK) return;
            try { _queueCts?.Cancel(); } catch { }
            try { _taskCts?.Cancel(); } catch { }
            try { _proc?.Kill(true); } catch { }
        }
        Close();
    }

    private void UpdateButtons()
    {
        StartBtn.IsEnabled = !_running;
        PauseBtn.IsEnabled = _running && !_paused;
        ResumeBtn.IsEnabled = _running && _paused;
        CancelTaskBtn.IsEnabled = _running;
        CancelAllBtn.IsEnabled = _running;
        RetryBtn.IsEnabled = !_running && _queue.Any(t => t.Status is "失败" or "已取消" or "已暂停");
    }

    /// <summary>中断后清掉残留速度，避免用户看到“已取消/已暂停”却还显示旧速率。</summary>
    private void ClearRunningSpeed()
    {
        foreach (var t in _queue.Where(t => t.Status == "上传中")) t.Speed = "";
    }

    private async Task ForgetVfsAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var resp = await http.PostAsync("http://127.0.0.1:5572/vfs/forget",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        resp.EnsureSuccessStatusCode();
    }

    private void AppendLog(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F0} KB",
        _ => $"{bytes} B",
    };
}
