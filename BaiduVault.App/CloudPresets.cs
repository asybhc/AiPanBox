namespace BaiduVault.App;

/// <summary>
/// 13 家网盘的驱动预设：显示名 → OpenList 驱动 key、必填字段、授权引导、OAuth 配置。
/// 驱动 key 与字段名来自运行实例 /api/admin/driver/info 实测（见《多网盘接入指引.md》）。
/// OAuth 自动换取仅百度为项目实测；其余标注「未实测」，失败时以平台/OpenList 令牌工具为准。
/// </summary>
public sealed class PresetOAuth
{
    /// <summary>授权页模板，{clientId} 会被替换为字段 client_id / ClientID 的值。</summary>
    public string AuthorizeUrl { get; init; } = "";
    public string TokenUrl { get; init; } = "";
    public string RedirectUri { get; init; } = "oob";
    public bool Verified { get; init; }
    public string TokenFormat { get; init; } = "form"; // form | json
    public string Note { get; init; } = "";
}

public sealed class CloudPreset
{
    public string Key { get; init; } = "";
    public string Display { get; init; } = "";
    public string Driver { get; init; } = "";
    /// <summary>稳定档位：稳 / 中 / 折腾</summary>
    public string Tier { get; init; } = "中";
    public string Prefix { get; init; } = "";
    public string Summary { get; init; } = "";
    public string[] Steps { get; init; } = Array.Empty<string>();
    public string[] CommonFailures { get; init; } = Array.Empty<string>();
    public Dictionary<string, string> FieldLabels { get; init; } = new();
    public string PlatformUrl { get; init; } = "";
    public PresetOAuth? OAuth { get; init; }
    /// <summary>chunker 分片大小；蓝奏免费版单文件约 100MB，必须调小。</summary>
    public string ChunkSize { get; init; } = "512M";

    /// <summary>用于下拉展示：名称（驱动）· 档位</summary>
    public string ComboText => $"{Display}  [{Driver}]  {Tier}";
}

public static class CloudPresets
{
    public static readonly CloudPreset[] All =
    {
        new()
        {
            Key = "local",
            Display = "本地目录（演示/排障）",
            Driver = "Local",
            Tier = "稳",
            Prefix = "demo",
            Summary = "把一个本地文件夹作为库（不加密上云，仅用于演示或排障）。",
            Steps = new[]
            {
                "在磁盘上新建一个空文件夹（例如 D:\\演示库）",
                "在下方「本地文件夹绝对路径」填该文件夹的完整路径",
                "点「连接测试」→「完成」",
            },
            CommonFailures = new[] { "路径不存在或无权限：确认文件夹已创建、当前用户可写" },
            FieldLabels = new() { ["root_folder_path"] = "本地文件夹绝对路径" },
        },
        new()
        {
            Key = "ali",
            Display = "阿里云盘",
            Driver = "AliyundriveOpen",
            Tier = "稳",
            Prefix = "ali",
            Summary = "官方开放平台授权登录，刷新令牌自动续期，可长期无人值守。",
            PlatformUrl = "https://open.alipan.com/console/developer",
            Steps = new[]
            {
                "打开阿里云盘开放平台，创建应用，拿到应用 ID 与应用密钥（有开发资质时）",
                "点下方「打开授权页」，用阿里云盘 App 登录并授权",
                "把回调页显示的授权码粘贴到「授权码」框，点「用授权码换取令牌」",
                "没有开发资质时：改用本地网盘服务文档页的刷新令牌工具扫码，拿到刷新令牌后直接填第 4 步字段",
                "「根目录 ID」填 root（表示根目录，也可填网盘内某个目录 ID）",
            },
            CommonFailures = new[]
            {
                "刷新令牌单次使用后会轮换，若手动复制过旧令牌会失效",
                "开放平台第三方流量有权益包限制，超限会限速（未实测）",
            },
            FieldLabels = new()
            {
                ["refresh_token"] = "刷新令牌（务必离线备份）",
                ["root_folder_id"] = "根目录 ID（默认填 root）",
                ["client_id"] = "应用 ID（自建应用时填）",
                ["client_secret"] = "应用密钥（自建应用时填）",
                ["alipan_type"] = "账号类型（默认 resource）",
                ["remove_way"] = "删除方式（默认即可）",
                ["drive_type"] = "盘类型（默认 resource）",
            },
            OAuth = new PresetOAuth
            {
                AuthorizeUrl = "https://openapi.alipan.com/oauth/authorize?client_id={clientId}&redirect_uri=https%3A%2F%2Fopenapi.alipan.com%2Foauth%2Fcallback&scope=user%3Abase%2Cfile%3Aall%3Aread%2Cfile%3Aall%3Awrite&response_type=code",
                TokenUrl = "https://openapi.alipan.com/oauth/access_token",
                RedirectUri = "https://openapi.alipan.com/oauth/callback",
                TokenFormat = "json",
                Verified = false,
                Note = "阿里云盘开放平台授权登录（未实测）：若换取失败，请用本地网盘服务的刷新令牌工具扫码获取。",
            },
        },
        new()
        {
            Key = "115",
            Display = "115 网盘",
            Driver = "115 Open",
            Tier = "稳",
            Prefix = "115",
            Summary = "115 开放平台授权登录（访问令牌与刷新令牌均必填），自动续期。",
            PlatformUrl = "https://open.115.com",
            Steps = new[]
            {
                "打开 115 开放平台，申请应用，走授权码流程",
                "拿到访问令牌与刷新令牌后分别填入下方两个字段",
                "「根目录 ID」填 0（根目录）或具体目录 ID",
            },
            CommonFailures = new[]
            {
                "访问令牌与刷新令牌必须成对填写，缺一个会报授权失败",
                "令牌过期后需重新走一次开放平台授权（本项目未实测自动续期）",
            },
            FieldLabels = new()
            {
                ["root_folder_id"] = "根目录 ID（默认 0）",
                ["access_token"] = "访问令牌",
                ["refresh_token"] = "刷新令牌",
            },
        },
        new()
        {
            Key = "123",
            Display = "123 云盘",
            Driver = "123 Open",
            Tier = "稳",
            Prefix = "123",
            Summary = "123 开放平台授权登录，刷新令牌自动续期。",
            PlatformUrl = "https://www.123pan.com/developer",
            Steps = new[]
            {
                "打开 123 云盘开放平台，创建应用，拿到应用 ID 与应用密钥",
                "按平台文档走授权流程，换取访问令牌与刷新令牌",
                "四个令牌字段全部填入（缺一不可）",
                "「根目录 ID」填 0（根目录）",
            },
            CommonFailures = new[]
            {
                "123 开放平台上传有并发/频率限制，失败可把上传线程数调小",
                "访问令牌过期后由刷新令牌自动续（未实测）",
            },
            FieldLabels = new()
            {
                ["ClientID"] = "应用 ID",
                ["ClientSecret"] = "应用密钥",
                ["AccessToken"] = "访问令牌",
                ["RefreshToken"] = "刷新令牌（务必离线备份）",
                ["root_folder_id"] = "根目录 ID（默认 0）",
            },
        },
        new()
        {
            Key = "quark",
            Display = "夸克网盘",
            Driver = "Quark",
            Tier = "折腾",
            Prefix = "quark",
            Summary = "用浏览器登录凭证登录。风控较敏感，掉线后需重新获取。",
            PlatformUrl = "https://pan.quark.cn",
            Steps = new[]
            {
                "浏览器登录夸克网盘网页版",
                "按 F12 打开开发者工具 → 网络 → 刷新页面 → 选任一请求 → 复制请求头里的整段登录凭证",
                "粘贴到下方登录凭证字段",
                "「根目录 ID」填 0（根目录）",
                "有开发资质可改用 QuarkOpen；电视端可改用 QuarkTV（登录令牌寿命更长）",
            },
            CommonFailures = new[]
            {
                "频繁操作可能触发滑块验证，登录凭证随即失效",
                "登录凭证里缺少必要字段（如 __pus / __puus）会直接报未登录",
            },
            FieldLabels = new()
            {
                ["cookie"] = "浏览器登录凭证（整段复制）",
                ["root_folder_id"] = "根目录 ID（默认 0）",
                ["use_transcoding_address"] = "转码地址（默认 false）",
            },
        },
        new()
        {
            Key = "baidu",
            Display = "百度网盘",
            Driver = "BaiduNetdisk",
            Tier = "稳",
            Prefix = "baidu",
            Summary = "开发者应用密钥（应用 ID 与密钥，即 AppKey 与 SecretKey）+ 授权码换取刷新令牌，本地网盘服务自动续期（项目实测）。",
            PlatformUrl = "https://pan.baidu.com/union/console/applist",
            Steps = new[]
            {
                "打开百度网盘开放平台「应用列表」，创建「软件应用」，回调地址填 oob（固定值，表示手动复制授权码）",
                "记下应用 ID 与应用密钥（即 AppKey 与 SecretKey），填到下方",
                "点下方「打开百度授权页」，登录百度账号并授权（授权范围：basic 与 netdisk）",
                "浏览器跳到回调页（oob）后会显示授权码，粘贴到「授权码」框",
                "点「用授权码换取令牌」，刷新令牌会自动填入；请立刻离线备份",
                "「根目录路径」填 /apps/{你的应用名}",
            },
            CommonFailures = new[]
            {
                "回调地址必须填 oob，否则授权页会直接报错",
                "刷新令牌只能用一次；重新授权会作废旧令牌",
                "第三方接口上传限速约 4.65 兆字节/秒、加密文件名上限 119 字符（项目实测）",
            },
            FieldLabels = new()
            {
                ["client_id"] = "应用 ID（即 AppKey）",
                ["client_secret"] = "应用密钥（即 SecretKey）",
                ["refresh_token"] = "刷新令牌（授权码换取）",
                ["root_folder_path"] = "根目录路径（/apps/你的应用名）",
                ["custom_crack_ua"] = "自定义浏览器标识（默认即可）",
            },
            OAuth = new PresetOAuth
            {
                AuthorizeUrl = "https://openapi.baidu.com/oauth/2.0/authorize?response_type=code&client_id={clientId}&redirect_uri=oob&scope=basic%2Cnetdisk&display=popup",
                TokenUrl = "https://openapi.baidu.com/oauth/2.0/token",
                RedirectUri = "oob",
                TokenFormat = "form",
                Verified = true,
                Note = "百度授权码模式（项目实测）：在授权页登录后，回调页（oob）会显示授权码，在此处换取刷新令牌。",
            },
        },
        new()
        {
            Key = "189",
            Display = "天翼云盘",
            Driver = "189CloudPC",
            Tier = "折腾",
            Prefix = "189",
            Summary = "账密 + 图形验证码/短信验证码登录；可能触发风控，需人工过验证码。",
            PlatformUrl = "https://cloud.189.cn",
            Steps = new[]
            {
                "登录方式选「账密」（默认）",
                "填手机号与密码",
                "如果登录时弹出验证码：把图形验证码填到「图形验证码」字段，或按短信流程填「短信验证码」字段",
                "「根目录 ID」填 -11（个人云根目录）或具体目录 ID",
                "云类型选「个人云」或「家庭云」",
            },
            CommonFailures = new[]
            {
                "验证码过期需立刻重试；风控触发后要等一段时间",
                "家庭云需要额外填「家庭云 ID」",
            },
            FieldLabels = new()
            {
                ["login_type"] = "登录方式（账密 / 扫码）",
                ["username"] = "手机号",
                ["password"] = "密码",
                ["validate_code"] = "图形验证码（登录提示时填）",
                ["sms_code"] = "短信验证码（可选）",
                ["root_folder_id"] = "根目录 ID（个人云默认 -11）",
                ["type"] = "云类型（个人云 / 家庭云）",
            },
        },
        new()
        {
            Key = "139",
            Display = "移动云盘",
            Driver = "139Yun",
            Tier = "折腾",
            Prefix = "139",
            Summary = "账密 + 短信验证码，或直接粘贴抓包得到的授权字符串。",
            PlatformUrl = "https://yun.139.com",
            Steps = new[]
            {
                "推荐：填手机号与密码，本地网盘服务会走短信验证，把收到的验证码填到「短信验证码」字段",
                "或从移动云盘应用/网页抓包，把授权字符串整段粘贴进来",
                "「根目录 ID」留空或填具体目录 ID（默认根目录）",
                "云类型选「个人云」或「家庭云」",
            },
            CommonFailures = new[]
            {
                "短信验证码有时效，超时需重新发送",
                "授权字符串过期后需重新抓包",
            },
            FieldLabels = new()
            {
                ["authorization"] = "授权字符串（抓包所得，优先）",
                ["username"] = "手机号",
                ["password"] = "密码",
                ["sms_code"] = "短信验证码",
                ["root_folder_id"] = "根目录 ID（可留空）",
                ["type"] = "云类型（个人云 / 家庭云）",
            },
        },
        new()
        {
            Key = "uc",
            Display = "UC 网盘",
            Driver = "UC",
            Tier = "折腾",
            Prefix = "uc",
            Summary = "用浏览器登录凭证登录，与夸克同源实现；电视端 UCTV 更稳。",
            PlatformUrl = "https://drive.uc.cn",
            Steps = new[]
            {
                "浏览器登录 UC 网盘网页版",
                "按 F12 打开开发者工具 → 网络 → 刷新 → 复制任一请求的整段登录凭证",
                "粘贴到登录凭证字段，「根目录 ID」填 0",
                "登录令牌长寿方案：改用 UCTV（需要刷新令牌与查询令牌，抓包获取）",
            },
            CommonFailures = new[]
            {
                "登录凭证被风控掉线的表现：列目录报错或为空",
                "登录凭证与夸克互不通用，别混填",
            },
            FieldLabels = new()
            {
                ["cookie"] = "浏览器登录凭证（整段复制）",
                ["root_folder_id"] = "根目录 ID（默认 0）",
                ["use_transcoding_address"] = "转码地址（默认 false）",
            },
        },
        new()
        {
            Key = "lanzou",
            Display = "蓝奏云",
            Driver = "Lanzou",
            Tier = "折腾",
            Prefix = "lanzou",
            Summary = "账密或浏览器登录凭证；免费版单文件约 100 兆字节，本应用会把分片调到 90 兆。",
            PlatformUrl = "https://pc.woozooo.com",
            Steps = new[]
            {
                "模式选「账密」",
                "填账号与密码",
                "「根目录 ID」填 -1（根目录）",
                "服务地址与浏览器标识保持默认",
                "已按该库限制把分片大小设为 90 兆（添加库时自动写入）",
            },
            CommonFailures = new[]
            {
                "免费账号单文件上限约 100 兆字节，超过会失败（未实测）",
                "分享链接模式（shareUrl）与账密模式不能混用",
                "该驱动无复制实现，库内复制不可用",
            },
            FieldLabels = new()
            {
                ["type"] = "模式（账密 / 分享）",
                ["account"] = "账号",
                ["password"] = "密码",
                ["root_folder_id"] = "根目录 ID（默认 -1）",
            },
            ChunkSize = "90M",
        },
        new()
        {
            Key = "ilanzou",
            Display = "蓝奏优享",
            Driver = "ILanZou",
            Tier = "中",
            Prefix = "ilanzou",
            Summary = "账号、密码、登录 IP 全必填（IP 为登录环境参数）。",
            PlatformUrl = "https://www.ilanzou.com",
            Steps = new[]
            {
                "填账号与密码",
                "IP 填你当前登录优享版时使用的公网 IP（不确定就留空先试）",
                "「根目录 ID」填 -1（根目录）",
            },
            CommonFailures = new[]
            {
                "IP 与登录环境不一致可能直接拒绝",
                "优享版单文件上限高于免费版，但仍建议分片不超过 200 兆",
            },
            FieldLabels = new()
            {
                ["username"] = "账号",
                ["password"] = "密码",
                ["ip"] = "登录 IP（环境绑定参数）",
                ["root_folder_id"] = "根目录 ID（默认 -1）",
            },
        },
        new()
        {
            Key = "guangya",
            Display = "光鸭云盘",
            Driver = "GuangYaPan",
            Tier = "中",
            Prefix = "guangya",
            Summary = "应用 ID + 手机短信验证流程，换取访问令牌与刷新令牌。",
            PlatformUrl = "https://www.guangyapan.com",
            Steps = new[]
            {
                "应用 ID 填光鸭开放平台申请的应用 ID",
                "手机号填你的手机号",
                "发送验证码：在「发送验证码」字段填 true（即可触发），或按本地网盘服务页面提示发起",
                "把收到的验证码填到「短信验证码」字段，验证流程 ID 按返回填写",
                "登录成功后访问令牌、刷新令牌、设备 ID、设备指纹由驱动自动回写",
            },
            CommonFailures = new[]
            {
                "短信验证码有效期短，需重新发送",
                "设备指纹缺失可能需要重新走一次短信登录",
            },
            FieldLabels = new()
            {
                ["client_id"] = "应用 ID（必填）",
                ["phone_number"] = "手机号",
                ["send_code"] = "发送验证码（填 true 触发）",
                ["verify_code"] = "短信验证码",
                ["verification_id"] = "验证流程 ID",
                ["refresh_token"] = "刷新令牌（登录后自动回写）",
            },
        },
        new()
        {
            Key = "wopan",
            Display = "联通云盘",
            Driver = "WoPan",
            Tier = "中",
            Prefix = "wopan",
            Summary = "以刷新令牌（可选访问令牌）为主，自动续期；家庭云需填家庭云 ID。",
            PlatformUrl = "https://pan.wo.cn",
            Steps = new[]
            {
                "从联通云盘应用/网页抓包获得刷新令牌（以及访问令牌）",
                "填入刷新令牌；访问令牌有时可留空",
                "「根目录 ID」留空（默认根目录）",
                "家庭云再填「家庭云 ID」",
            },
            CommonFailures = new[]
            {
                "刷新令牌过期需重新抓包",
                "注意区分联通云盘（WoPan）与腾讯微云（WeiYun），不要选错驱动",
            },
            FieldLabels = new()
            {
                ["refresh_token"] = "刷新令牌（抓包所得）",
                ["access_token"] = "访问令牌（可选）",
                ["root_folder_id"] = "根目录 ID（可留空）",
                ["family_id"] = "家庭云 ID（家庭云才填）",
            },
        },
        new()
        {
            Key = "thunder",
            Display = "迅雷云盘",
            Driver = "ThunderX",
            Tier = "折腾",
            Prefix = "thunder",
            Summary = "账密登录，可能要求人工过验证码（验证码令牌）。",
            PlatformUrl = "https://pan.xunlei.com",
            Steps = new[]
            {
                "填账号与密码",
                "根目录 ID 留空（默认根目录）",
                "若提示验证码：按本地网盘服务页面提示完成，把验证码令牌填入",
            },
            CommonFailures = new[]
            {
                "触发风控后需人工过验证码，无法完全无人值守",
                "不要选「专家（Expert）」系列变体（需要抓包算法参数）",
            },
            FieldLabels = new()
            {
                ["username"] = "账号（手机号）",
                ["password"] = "密码",
                ["captcha_token"] = "验证码令牌（触发时填）",
                ["root_folder_id"] = "根目录 ID（可留空）",
            },
        },
    };

    public static CloudPreset? ByDriver(string driver) =>
        All.FirstOrDefault(p => string.Equals(p.Driver, driver, StringComparison.OrdinalIgnoreCase));

    /// <summary>常用驱动字段的中文标签（预设未单独定义时使用）。</summary>
    private static readonly Dictionary<string, string> CommonLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["disableindex"] = "禁用索引",
        ["disable_index"] = "禁用索引",
        ["enablesign"] = "启用签名",
        ["enable_sign"] = "启用签名",
        ["cacheexpiration"] = "缓存有效期（分钟）",
        ["cache_expiration"] = "缓存有效期（分钟）",
        ["orderby"] = "排序字段",
        ["order_by"] = "排序字段",
        ["orderdirection"] = "排序方向",
        ["order_direction"] = "排序方向",
        ["extractfolder"] = "解压目录",
        ["extract_folder"] = "解压目录",
        ["webproxy"] = "网页代理",
        ["web_proxy"] = "网页代理",
        ["disableproxysign"] = "关闭代理签名",
        ["disable_proxy_sign"] = "关闭代理签名",
        ["custom_crack_ua"] = "自定义浏览器标识",
        ["root_folder_id"] = "根目录 ID",
        ["root_folder_path"] = "根目录路径",
        ["username"] = "账号",
        ["password"] = "密码",
        ["video_thumb_pos"] = "缩略图位置",
        ["show_hidden"] = "显示隐藏文件",
        ["mkdir_perm"] = "新建文件夹权限",
    };

    /// <summary>字段的中文标签（找不到回退原字段名）。</summary>
    public static string LabelFor(CloudPreset? preset, string field) =>
        preset?.FieldLabels.TryGetValue(field, out var label) == true ? label
        : CommonLabels.TryGetValue(field, out var common) ? common
        : field;
}
