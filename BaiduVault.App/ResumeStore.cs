using System.IO;
using System.Text.Json;

namespace BaiduVault.App;

/// <summary>
/// 断点续播记录：JSON 落 %LOCALAPPDATA%\BaiduVault.App\resume.json。
/// 键 = 完整 vault 路径(含库名前缀)+大小+mtime —— 多库天然不串，文件被替换后旧键自动失效。
/// </summary>
public sealed class ResumeStore
{
    private sealed class ResumeEntry
    {
        public long PositionMs { get; set; }
        public DateTimeOffset Updated { get; set; }
    }

    private readonly string _file;
    private readonly Dictionary<string, ResumeEntry> _map;
    private readonly object _gate = new();

    public ResumeStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BaiduVault.App");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "resume.json");
        try
        {
            _map = File.Exists(_file)
                ? JsonSerializer.Deserialize<Dictionary<string, ResumeEntry>>(File.ReadAllText(_file)) ?? new()
                : new();
        }
        catch { _map = new(); }
    }

    /// <summary>Path 已含库名（/baidu/... 或 /demo2/...），所以同名文件在不同库不会串键。</summary>
    public static string MakeKey(VaultEntry e) =>
        $"{e.Path}|{e.Size}|{e.LastModified.UtcTicks}";

    public long Get(string key)
    {
        lock (_gate) return _map.TryGetValue(key, out var v) ? v.PositionMs : 0;
    }

    public void Set(string key, long positionMs)
    {
        lock (_gate)
        {
            _map[key] = new ResumeEntry { PositionMs = positionMs, Updated = DateTimeOffset.UtcNow };
            Save();
        }
    }

    public void Remove(string key)
    {
        lock (_gate)
        {
            if (_map.Remove(key)) Save();
        }
    }

    private void Save() => File.WriteAllText(_file, JsonSerializer.Serialize(_map));
}
