using System.IO;
using System.Text;

namespace BaiduVault.App;

/// <summary>
/// rclone.conf 最小读写器：保留注释与顺序，按段操作。
/// 仅支持 [section] + key = value + #注释；不重写未触及的行。
/// </summary>
public sealed class RcloneConfig
{
    private readonly List<Line> _lines = new();
    private readonly string _path;

    private sealed class Line
    {
        public string Raw { get; set; } = "";
        public string? Section { get; set; }   // 段头所在行设为段名，段内键值行也带段名
        public string? Key { get; set; }
        public string? Value { get; set; }
        public bool IsHeader => Key == null && Section != null && Raw.StartsWith('[');
    }

    private RcloneConfig(string path) { _path = path; }

    public static RcloneConfig Load(string path)
    {
        var cfg = new RcloneConfig(path);
        string? current = null;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = new Line { Raw = raw };
            var t = raw.TrimStart();
            if (t.Length == 0 || t.StartsWith('#') || t.StartsWith(';'))
            {
                line.Section = current;
                cfg._lines.Add(line);
                continue;
            }
            if (t.StartsWith('['))
            {
                var end = t.IndexOf(']');
                if (end > 1)
                {
                    current = t[1..end].Trim();
                    line.Section = current;
                }
                cfg._lines.Add(line);
                continue;
            }
            var eq = t.IndexOf('=');
            if (eq > 0)
            {
                line.Section = current;
                line.Key = t[..eq].Trim();
                line.Value = t[(eq + 1)..].Trim();
            }
            cfg._lines.Add(line);
        }
        return cfg;
    }

    public void Save()
    {
        var sb = new StringBuilder();
        foreach (var l in _lines)
        {
            if (l.Key != null) sb.AppendLine($"{l.Key} = {l.Value}");
            else sb.AppendLine(l.Raw);
        }
        // 去掉文件末尾多余空行，保持单换行结尾
        File.WriteAllText(_path, sb.ToString());
    }

    /// <summary>读取 [vault] 的 upstreams，返回 {库名: remote:} 顺序字典。</summary>
    public Dictionary<string, string> GetVaultUpstreams()
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        var line = _lines.FirstOrDefault(l => l.Section == "vault" && l.Key == "upstreams");
        if (line?.Value == null) return dict;
        foreach (var part in line.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0) dict[part[..eq]] = part[(eq + 1)..];
        }
        return dict;
    }

    public void SetVaultUpstreams(Dictionary<string, string> upstreams)
    {
        var line = _lines.FirstOrDefault(l => l.Section == "vault" && l.Key == "upstreams");
        var value = string.Join(' ', upstreams.Select(kv => $"{kv.Key}={kv.Value}"));
        if (line != null) line.Value = value;
        else
        {
            // 若无 vault 段则追加
            _lines.Add(new Line { Raw = "[vault]", Section = "vault" });
            _lines.Add(new Line { Section = "vault", Key = "upstreams", Value = value });
        }
    }

    public bool HasSection(string name) => _lines.Any(l => l.Section == name && l.IsHeader);

    /// <summary>替换（或追加）指定段内容（header + 一组 key=value）。段内原有非键值行（注释）保留在段尾。</summary>
    public void SetSection(string name, IEnumerable<KeyValuePair<string, string>> kv)
    {
        // 删除旧段（段头到下一个段头之间）
        var start = _lines.FindIndex(l => l.Section == name && l.IsHeader);
        if (start >= 0)
        {
            var end = _lines.Count;
            for (var i = start + 1; i < _lines.Count; i++)
            {
                if (_lines[i].IsHeader) { end = i; break; }
            }
            _lines.RemoveRange(start, end - start);
        }
        // 追加新段
        var idx = _lines.Count;
        _lines.Add(new Line { Raw = $"[{name}]", Section = name });
        foreach (var (k, v) in kv)
            _lines.Add(new Line { Section = name, Key = k, Value = v });
    }

    public void RemoveSection(string name)
    {
        var start = _lines.FindIndex(l => l.Section == name && l.IsHeader);
        if (start < 0) return;
        var end = _lines.Count;
        for (var i = start + 1; i < _lines.Count; i++)
        {
            if (_lines[i].IsHeader) { end = i; break; }
        }
        _lines.RemoveRange(start, end - start);
    }

    /// <summary>重命名段（包括段头与段内引用，但不处理其它段对它的 remote= 引用——由调用方处理）。</summary>
    public void RenameSection(string oldName, string newName)
    {
        foreach (var l in _lines)
        {
            if (l.IsHeader && l.Section == oldName)
            {
                l.Section = newName;
                l.Raw = $"[{newName}]";
            }
            else if (l.Section == oldName)
            {
                l.Section = newName;
            }
        }
    }

    public string? Get(string section, string key) =>
        _lines.FirstOrDefault(l => l.Section == section && l.Key == key)?.Value;
}
