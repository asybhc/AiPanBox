using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BaiduVault.App;

/// <summary>
/// 应用设置，持久化到 %LOCALAPPDATA%\BaiduVault.App\settings.json。
/// 端口/缓存改动需重启 rclone serve 生效；硬解/字幕在打开播放器时读取。
/// </summary>
public sealed class AppSettings
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BaiduVault.App");
    private static readonly string SettingsPath = System.IO.Path.Combine(Dir, "settings.json");

    private static AppSettings? _instance;
    public static AppSettings Current => _instance ??= Load();

    public int OpenListPort { get; set; } = 5244;
    public int WebDavPort { get; set; } = 8088;
    public string CacheMaxSize { get; set; } = "10G";
    public bool HardwareDecoding { get; set; } = true;
    public string SubtitleEncoding { get; set; } = "GB18030";
    public bool AutoStart { get; set; }
    public bool ClearCacheOnExit { get; set; }
    public int UploadConcurrency { get; set; } = 4;
    public int UploadRetries { get; set; } = 3;
    public bool UploadSkipExisting { get; set; } = true;
    public bool UploadVerify { get; set; } = true;
    /// <summary>按库记忆的上传并发数（库名 → transfers）。</summary>
    public Dictionary<string, int> LibraryConcurrency { get; set; } = new();
    public string AudioOutputDevice { get; set; } = "";
    public string DefaultPlayBehavior { get; set; } = "resume"; // resume | start
    /// <summary>强制软件渲染：显卡/虚拟显示器异常时 WPF 窗口可能出现全白，设 true 可绕过 D3D。</summary>
    public bool SoftwareRendering { get; set; }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var s = JsonSerializer.Deserialize<AppSettings>(json);
                if (s != null) return s;
            }
        }
        catch { /* 损坏则用默认值 */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            });
            File.WriteAllText(SettingsPath, json);
        }
        catch { /* 保存失败不阻塞 */ }
    }

    public static void Reload() => _instance = Load();
}
