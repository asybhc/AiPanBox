using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace BaiduVault.App;

/// <summary>vault 聚合根下的一个条目（库/目录/文件）。Path 以 / 开头，第一段是库名。</summary>
public sealed class VaultEntry
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTimeOffset LastModified { get; init; }
}

/// <summary>rclone serve webdav（vault: 聚合根）的只读列目录客户端。</summary>
public sealed class VaultClient
{
    public string BaseAddress { get; }
    private static readonly XNamespace D = "DAV:";
    private readonly HttpClient _http;

    public VaultClient(string user, string pass, int port = 8088)
    {
        BaseAddress = $"http://127.0.0.1:{port}";
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
    }

    /// <summary>按路径段编码，保留 '/' 分隔（前导 / 整体编码会变成 %2F，服务端不识别）。</summary>
    public static string EscapePath(string path) =>
        string.Concat(path.Split('/').Select(Uri.EscapeDataString).Select((s, i) => i == 0 ? s : "/" + s));

    /// <summary>列出 path 的直接子项（Depth:1）。path="/" 时返回库列表。</summary>
    public async Task<List<VaultEntry>> ListAsync(string path, CancellationToken ct = default)
    {
        if (!path.StartsWith('/')) path = "/" + path;
        if (!path.EndsWith('/')) path += "/";

        const string body = """
            <?xml version="1.0" encoding="utf-8"?>
            <propfind xmlns="DAV:"><prop><displayname/><getcontentlength/><getlastmodified/><resourcetype/></prop></propfind>
            """;
        using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), BaseAddress + EscapePath(path));
        req.Headers.Add("Depth", "1");
        req.Content = new StringContent(body, Encoding.UTF8, "application/xml");
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var doc = XDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        string Norm(string p) => p.TrimEnd('/');
        var self = Norm(path);
        var result = new List<VaultEntry>();

        foreach (var response in doc.Descendants(D + "response"))
        {
            var href = response.Element(D + "href")?.Value;
            if (string.IsNullOrEmpty(href)) continue;
            var decoded = Uri.UnescapeDataString(href);
            if (string.Equals(Norm(decoded), self, StringComparison.OrdinalIgnoreCase)) continue;

            // 取状态 200 的 propstat
            var prop = response.Descendants(D + "propstat")
                .FirstOrDefault(ps => ps.Element(D + "status")?.Value.Contains(" 200") == true)
                ?.Element(D + "prop");
            if (prop == null) continue;

            var isDir = prop.Element(D + "resourcetype")?.Element(D + "collection") != null;
            long.TryParse(prop.Element(D + "getcontentlength")?.Value, out var size);
            DateTimeOffset.TryParse(prop.Element(D + "getlastmodified")?.Value,
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var mtime);

            var trimmed = Norm(decoded);
            var name = trimmed[(trimmed.LastIndexOf('/') + 1)..];
            if (name.Length == 0) continue;

            result.Add(new VaultEntry
            {
                Name = name,
                Path = trimmed,
                IsDirectory = isDir,
                Size = size,
                LastModified = mtime,
            });
        }

        return result
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
