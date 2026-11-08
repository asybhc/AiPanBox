using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BaiduVault.App;

/// <summary>
/// OpenList 管理 API 封装（账号/存储由 OpenList 管理，不再由脚本生成）。
/// 仅本地 127.0.0.1:5244，JWT 鉴权；401 时自动重试登录。
/// 不在任何输出/日志中打印 token 或密码。
/// </summary>
public sealed class OpenListClient
{
    public const int Port = 5244;
    private static readonly string Base = $"http://127.0.0.1:{Port}";

    private readonly string _user;
    private readonly string _pass;
    private readonly HttpClient _http = new();
    private string? _token;

    public OpenListClient(string user, string pass)
    {
        _user = user;
        _pass = pass;
    }

    public static (string User, string Pass) LoadAdminCredentials()
    {
        var file = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BaiduVault", "keys", "openlist-admin.txt");
        if (!File.Exists(file))
            throw new InvalidOperationException($"未找到网盘服务的管理凭据：{file}");
        string u = "", p = "";
        foreach (var raw in File.ReadAllLines(file))
        {
            var line = raw.Trim();
            if (line.StartsWith("用户:", StringComparison.Ordinal)) u = line["用户:".Length..].Trim();
            if (line.StartsWith("密码:", StringComparison.Ordinal)) p = line["密码:".Length..].Trim();
        }
        if (u.Length == 0 || p.Length == 0)
            throw new InvalidOperationException($"网盘服务的管理凭据格式异常：{file}");
        return (u, p);
    }

    public async Task LoginAsync()
    {
        var body = JsonContent.Create(new { username = _user, password = _pass });
        using var resp = await _http.PostAsync($"{Base}/api/auth/login", body);
        resp.EnsureSuccessStatusCode();
        var j = await resp.Content.ReadFromJsonAsync<OlResp<OlToken>>();
        _token = j?.Data?.Token ?? throw new InvalidOperationException("网盘服务登录响应中没有访问令牌");
    }

    private HttpRequestMessage NewReq(HttpMethod m, string path, HttpContent? content = null)
    {
        var req = new HttpRequestMessage(m, $"{Base}{path}");
        if (_token != null) req.Headers.Add("Authorization", _token);
        if (content != null) req.Content = content;
        return req;
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage req, bool reloginOn401 = true)
    {
        using var resp = await _http.SendAsync(req);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized && reloginOn401)
        {
            await LoginAsync();
            req = CloneRequest(req);
            using var r2 = await _http.SendAsync(req);
            return await Read<T>(r2);
        }
        return await Read<T>(resp);
    }

    private static async Task<T> Read<T>(HttpResponseMessage resp)
    {
        resp.EnsureSuccessStatusCode();
        var j = await resp.Content.ReadFromJsonAsync<OlResp<T>>();
        if (j?.Code != 200)
            throw new InvalidOperationException($"网盘服务接口返回错误：代码 {j?.Code}，信息 {j?.Message}");
        return j.Data!;
    }

    private HttpRequestMessage CloneRequest(HttpRequestMessage req)
    {
        var clone = new HttpRequestMessage(req.Method, req.RequestUri);
        if (req.Content != null)
        {
            var bytes = req.Content.ReadAsByteArrayAsync().Result;
            clone.Content = new ByteArrayContent(bytes);
            foreach (var h in req.Content.Headers) clone.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        clone.Headers.Add("Authorization", _token!);
        return clone;
    }

    // ---------- 存储 ----------

    public Task<OlStorageList> ListStoragesAsync() =>
        SendAsync<OlStorageList>(NewReq(HttpMethod.Get, "/api/admin/storage/list"));

    public Task CreateStorageAsync(OlStorage s) =>
        SendAsync<JsonElement>(NewReq(HttpMethod.Post, "/api/admin/storage/create",
            JsonContent.Create(s, options: _jsonOpt)));

    public Task UpdateStorageAsync(OlStorage s) =>
        SendAsync<JsonElement>(NewReq(HttpMethod.Post, "/api/admin/storage/update",
            JsonContent.Create(s, options: _jsonOpt)));

    public Task DeleteStorageAsync(int id) =>
        SendAsync<JsonElement>(NewReq(HttpMethod.Post, $"/api/admin/storage/delete?id={id}"));

    // ---------- 驱动 ----------

    public Task<List<OlDriverListItem>> ListDriversAsync() =>
        SendAsync<List<OlDriverListItem>>(NewReq(HttpMethod.Get, "/api/admin/driver/list"));

    public Task<OlDriverInfo> GetDriverInfoAsync(string driver) =>
        SendAsync<OlDriverInfo>(NewReq(HttpMethod.Get, $"/api/admin/driver/info?driver={Uri.EscapeDataString(driver)}"));

    private static readonly JsonSerializerOptions _jsonOpt = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}

// ---------------- 模型 ----------------

public sealed class OlResp<T>
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("data")] public T? Data { get; set; }
}

public sealed class OlToken { [JsonPropertyName("token")] public string Token { get; set; } = ""; }

public sealed class OlStorageList
{
    [JsonPropertyName("content")] public List<OlStorage> Content { get; set; } = new();
}

public sealed class OlStorage
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("mount_path")] public string MountPath { get; set; } = "";
    [JsonPropertyName("order")] public int Order { get; set; }
    [JsonPropertyName("driver")] public string Driver { get; set; } = "";
    [JsonPropertyName("cache_expiration")] public int CacheExpiration { get; set; } = 30;
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("addition")] public string Addition { get; set; } = "";
    [JsonPropertyName("remark")] public string Remark { get; set; } = "";
    [JsonPropertyName("disabled")] public bool Disabled { get; set; }
    [JsonPropertyName("order_by")] public string OrderBy { get; set; } = "";
    [JsonPropertyName("order_direction")] public string OrderDirection { get; set; } = "";
    [JsonPropertyName("extract_folder")] public string ExtractFolder { get; set; } = "";
    [JsonPropertyName("web_proxy")] public bool WebProxy { get; set; }
    [JsonPropertyName("webdav_policy")] public string WebDavPolicy { get; set; } = "native_proxy";
    [JsonPropertyName("down_proxy_url")] public string DownProxyUrl { get; set; } = "";
    [JsonPropertyName("disable_proxy_sign")] public bool DisableProxySign { get; set; }
    [JsonPropertyName("disable_index")] public bool DisableIndex { get; set; }
    [JsonPropertyName("enable_sign")] public bool EnableSign { get; set; }
    [JsonPropertyName("seed_policy")] public string SeedPolicy { get; set; } = "inherit";
}

public sealed class OlDriverListItem
{
    [JsonPropertyName("driver")] public string Driver { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public sealed class OlDriverInfo
{
    [JsonPropertyName("common")] public List<OlField> Common { get; set; } = new();
    [JsonPropertyName("additional")] public List<OlField> Additional { get; set; } = new();
    [JsonPropertyName("config")] public OlDriverConfig Config { get; set; } = new();
}

public sealed class OlField
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = ""; // string/number/text/select/bool
    [JsonPropertyName("default")] public string Default { get; set; } = "";
    [JsonPropertyName("options")] public string Options { get; set; } = ""; // comma-separated for select
    [JsonPropertyName("required")] public bool Required { get; set; }
    [JsonPropertyName("help")] public string Help { get; set; } = "";

    public List<string> OptionList => string.IsNullOrEmpty(Options)
        ? new() : Options.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

public sealed class OlDriverConfig
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("alert")] public string Alert { get; set; } = "";
}
