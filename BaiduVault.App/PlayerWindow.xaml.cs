using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;

namespace BaiduVault.App;

public partial class PlayerWindow : Window
{
    private readonly LibVLC _lib;
    private readonly MediaPlayer _mp;
    private readonly Media _media;
    private readonly ResumeStore _resume;
    private readonly string _resumeKey;
    private readonly long _resumeMs;
    private readonly DispatcherTimer _saveTimer;
    private bool _seeking;
    private bool _spuApplying;
    private long _lengthMs;

    public PlayerWindow(VaultEntry entry, string user, string pass, ResumeStore resume)
    {
        InitializeComponent();
        Title = $"{entry.Name} — AiPanBox";
        _spuApplying = true; // 阻止 SelectionChanged 在 _mp 就绪前触发
        SpuBox.Items.Add(new SpuItem(-1, "关"));
        SpuBox.SelectedIndex = 0;
        _spuApplying = false;
        _resume = resume;
        _resumeKey = ResumeStore.MakeKey(entry);
        _resumeMs = resume.Get(_resumeKey);

        // 调试钩子：%LOCALAPPDATA%\BaiduVault.App\debug-opts.txt 存在时按行追加参数
        //   -- 开头的行 → 全局 libvlc 参数；: 开头的行 → 媒体级选项
        var dbgFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BaiduVault.App", "debug-opts.txt");
        var dbgLines = File.Exists(dbgFile) ? File.ReadAllLines(dbgFile) : Array.Empty<string>();
        var globalOpts = dbgLines.Where(l => l.TrimStart().StartsWith("--")).ToArray();
        var mediaOpts = dbgLines.Where(l => l.TrimStart().StartsWith(':')).ToList();

        // 字幕编码：默认 GB18030（兼容 GBK），可在设置页改
        var subEnc = string.IsNullOrWhiteSpace(AppSettings.Current.SubtitleEncoding)
            ? "GB18030" : AppSettings.Current.SubtitleEncoding.Trim();
        // 硬解：走正式配置 AppSettings.HardwareDecoding；关闭时传 :avcodec-hw=none
        // （不能用 MediaPlayer.EnableHardwareDecoding=true，它会在 Play 时强制 :avcodec-hw=any 覆盖）
        if (!AppSettings.Current.HardwareDecoding)
            mediaOpts.Insert(0, ":avcodec-hw=none");

        _lib = new LibVLC(new[] { $"--subsdec-encoding={subEnc}" }.Concat(globalOpts).ToArray());
        _mp = new MediaPlayer(_lib);

        // 凭据嵌进 URL userinfo（preemptive Basic，最稳；http-user/http-pwd 媒体选项实测不生效）
        var host = new Uri($"http://127.0.0.1:{AppSettings.Current.WebDavPort}");
        var url = $"{host.Scheme}://{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(pass)}@{host.Authority}" +
                  VaultClient.EscapePath(entry.Path);
        _media = new Media(_lib, url, FromType.FromLocation, mediaOpts.ToArray());

        _mp.Playing += (_, _) => Dispatcher.BeginInvoke(OnPlaying);
        // 字幕轨通过 ESAdded 事件增量收集；不要在 Playing 里同步查 SpuDescription——
        // 播放启动瞬间 vout 正在等 UI 线程，UI 线程再同步调 libvlc 会形成交叉锁（首帧后卡死）
        _mp.ESAdded += (_, e) =>
        {
            if (e.Type != TrackType.Text) return;
            Dispatcher.BeginInvoke(() =>
            {
                _spuApplying = true;
                try
                {
                    if (SpuBox.Items.Count == 0) SpuBox.Items.Add(new SpuItem(-1, "关"));
                    SpuBox.Items.Add(new SpuItem(e.Id, $"字幕轨 {e.Id}"));
                }
                finally { _spuApplying = false; }
            });
        };
        _mp.LengthChanged += (_, e) => Dispatcher.BeginInvoke(() =>
        {
            _lengthMs = e.Length;
            SeekBar.Maximum = Math.Max(1, e.Length);
            UpdateTimeText(_mp.Time);
        });
        _mp.TimeChanged += (_, e) => Dispatcher.BeginInvoke(() =>
        {
            if (!_seeking) SeekBar.Value = e.Time;
            UpdateTimeText(e.Time);
        });
        _mp.EndReached += (_, _) => Dispatcher.BeginInvoke(() => _resume.Remove(_resumeKey));

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _saveTimer.Tick += (_, _) => SaveResume();
    }

    private void OnPlaying()
    {
        if (_resumeMs > 10_000)
        {
            _mp.Time = _resumeMs;
            UpdateTimeText(_resumeMs);
        }
        _saveTimer.Start();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        VideoView.MediaPlayer = _mp;
        _mp.Play(_media);
    }

    // ---------- 字幕 ----------
    // 轨道列表由 ESAdded 事件填充（见构造函数），这里只处理切换与外挂加载

    private void SpuBox_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_spuApplying || _mp == null || SpuBox.SelectedItem is not SpuItem item) return;
        _mp.SetSpu(item.Id);
    }

    private void LoadSub_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择外挂字幕（GBK/GB18030 编码已兼容）",
            Filter = "字幕文件 (*.srt;*.ass;*.ssa;*.vtt)|*.srt;*.ass;*.ssa;*.vtt|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        var ok = _mp.AddSlave(MediaSlaveType.Subtitle, new Uri(dlg.FileName).AbsoluteUri, true);
        if (!ok)
            MessageBox.Show(this, "字幕加载失败。", "AiPanBox", MessageBoxButton.OK, MessageBoxImage.Warning);
        // 新字幕轨会通过 ESAdded 自动进入下拉框
    }

    private sealed record SpuItem(int Id, string Name)
    {
        public override string ToString() => Name;
    }

    // ---------- 播放控制 ----------

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void TogglePlay()
    {
        if (_mp.IsPlaying) { _mp.Pause(); PlayPauseBtn.Content = "播放"; }
        else { _mp.Play(); PlayPauseBtn.Content = "暂停"; }
    }

    private void SeekBar_Down(object sender, MouseButtonEventArgs e) => _seeking = true;

    private void SeekBar_Up(object sender, MouseButtonEventArgs e)
    {
        _mp.Time = (long)SeekBar.Value;
        UpdateTimeText(_mp.Time);
        _seeking = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); e.Handled = true; break;
            case Key.Left: _mp.Time = Math.Max(0, _mp.Time - 5_000); e.Handled = true; break;
            case Key.Right: _mp.Time = Math.Min(_lengthMs, _mp.Time + 5_000); e.Handled = true; break;
        }
    }

    // ---------- 断点续播 ----------

    private void SaveResume()
    {
        var t = _mp.Time;
        if (t <= 10_000 || _lengthMs <= 0) return;
        if (t >= _lengthMs * 0.95) { _resume.Remove(_resumeKey); return; } // 看完不留档
        _resume.Set(_resumeKey, t);
    }

    // ---------- 收尾 ----------

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _saveTimer.Stop();
        SaveResume();
        _mp.Stop();
        _mp.Dispose();
        _media.Dispose();
        _lib.Dispose();
        base.OnClosed(e);
    }

    private void UpdateTimeText(long ms) =>
        TimeText.Text = $"{Fmt(ms)} / {Fmt(_lengthMs)}";

    private static string Fmt(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.Hours > 0 ? $"{t.Hours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes:D2}:{t.Seconds:D2}";
    }
}
