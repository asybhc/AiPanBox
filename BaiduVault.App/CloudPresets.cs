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

    /// <summary>用于下拉展示：名称（档位）。驱动标识不展示，避免界面出现英文。</summary>
    public string ComboText => $"{Display}（{Tier}）";
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

    /// <summary>驱动标识的中文显示名（不在内置预设中时回退原标识）。</summary>
    public static string DriverDisplay(string driver) => ByDriver(driver)?.Display ?? driver;

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
        ["access_token"] = "访问令牌",
        ["AccessToken"] = "访问令牌",
        ["account"] = "账号",
        ["alipan_type"] = "账号类型",
        ["api_url_address"] = "接口地址",
        ["authorization"] = "授权字符串",
        ["baseUrl"] = "服务地址",
        ["callback_concurrency"] = "回调并发数",
        ["captcha_token"] = "验证码令牌",
        ["client_id"] = "应用 ID",
        ["client_secret"] = "应用密钥",
        ["client_sn"] = "设备序列号",
        ["client_type"] = "客户端类型",
        ["ClientID"] = "应用 ID",
        ["ClientSecret"] = "应用密钥",
        ["cloud_id"] = "家庭云 ID",
        ["cookie"] = "登录凭证",
        ["custom_cache_policies"] = "自定义缓存策略",
        ["custom_upload_part_size"] = "自定义分片大小（0 为自动）",
        ["device_id"] = "设备 ID",
        ["device_sign"] = "设备签名",
        ["DirectLink"] = "直链下载",
        ["DirectLinkPrivateKey"] = "直链私钥",
        ["DirectLinkValidDuration"] = "直链有效期（分钟）",
        ["directory_size"] = "统计目录大小",
        ["download_api"] = "下载接口",
        ["drive_type"] = "盘类型",
        ["family_id"] = "家庭云 ID",
        ["family_transfer"] = "家庭云转存",
        ["generate_torrent"] = "生成种子文件",
        ["internal_upload"] = "内网上传加速",
        ["ip"] = "IP 地址",
        ["jg_open_id"] = "推送 ID",
        ["limit_rate"] = "请求限速（次/秒）",
        ["link_id"] = "分享 ID",
        ["livp_download_format"] = "实况照片下载格式",
        ["login_type"] = "登录方式",
        ["low_bandwith_upload_mode"] = "低带宽上传模式",
        ["mail_cookies"] = "邮箱登录凭证",
        ["no_use_ocr"] = "禁用文字识别",
        ["only_list_video_file"] = "仅列出视频文件",
        ["page_size"] = "分页大小",
        ["pdf_thumbnail"] = "PDF 缩略图",
        ["phone_number"] = "手机号",
        ["proxy_range"] = "代理分片下载",
        ["rapid_upload"] = "秒传",
        ["recycle_bin_path"] = "回收站路径",
        ["refresh_token"] = "刷新令牌",
        ["RefreshToken"] = "刷新令牌",
        ["repair_file_info"] = "修复文件信息",
        ["report_real_size"] = "上报真实文件大小",
        ["root_path"] = "根路径",
        ["send_code"] = "发送验证码",
        ["share_password"] = "分享密码",
        ["shareUrl"] = "分享链接",
        ["sms_code"] = "短信验证码",
        ["sort_rule"] = "排序规则",
        ["sort_type"] = "排序类型",
        ["thumb_cache_folder"] = "缩略图缓存目录",
        ["thumb_concurrency"] = "缩略图并发数",
        ["thumbnail"] = "缩略图",
        ["upload_api"] = "上传接口地址",
        ["upload_method"] = "上传方式",
        ["upload_thread"] = "上传线程数",
        ["upload_timeout"] = "分片上传超时（秒）",
        ["UploadThread"] = "上传线程数",
        ["use_dynamic_upload_api"] = "动态获取上传接口",
        ["use_large_thumbnail"] = "使用大缩略图",
        ["use_old_stream_upload"] = "使用旧版流式上传",
        ["use_online_api"] = "使用在线接口",
        ["use_transcoding_address"] = "转码地址",
        ["use_video_url"] = "使用视频直链",
        ["user_agent"] = "浏览器标识",
        ["user_domain_id"] = "用户域 ID",
        ["user_finger"] = "设备指纹",
        ["validate_code"] = "图形验证码",
        ["verification_id"] = "验证流程 ID",
        ["verify_code"] = "验证码",
        ["mount_path"] = "挂载路径",
        ["order"] = "排序",
        ["remark"] = "备注",
        ["down_proxy_url"] = "下载代理地址",
        ["webdav_policy"] = "WebDAV 策略",
        ["seed_policy"] = "秒传策略",
    };

    /// <summary>字段的中文标签（找不到回退原字段名）。</summary>
    public static string LabelFor(CloudPreset? preset, string field) =>
        preset?.FieldLabels.TryGetValue(field, out var label) == true ? label
        : CommonLabels.TryGetValue(field, out var common) ? common
        : field;

    /// <summary>下拉选项值的中文显示（仅界面显示用，提交给网盘服务的仍是原值）。</summary>
    private static readonly Dictionary<string, string> OptionLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["302_redirect"] = "302 重定向",
        ["use_proxy_url"] = "使用代理链接",
        ["native_proxy"] = "本机代理（默认）",
        ["front"] = "解压文件夹排在前面",
        ["back"] = "解压文件夹排在后面",
        ["inherit"] = "继承全局设置",
        ["on"] = "开启",
        ["off"] = "关闭",
        ["name"] = "名称",
        ["time"] = "时间",
        ["size"] = "大小",
        ["modified"] = "修改时间",
        ["filename"] = "文件名",
        ["filesize"] = "文件大小",
        ["lastOpTime"] = "最近操作时间",
        ["none"] = "默认",
        ["file_type"] = "类型",
        ["file_name"] = "文件名",
        ["updated_at"] = "更新时间",
        ["created_at"] = "创建时间",
        ["file_size"] = "文件大小",
        ["user_utime"] = "修改时间",
        ["asc"] = "升序",
        ["desc"] = "降序",
        ["official"] = "官方接口",
        ["crack"] = "第三方加速接口",
        ["crack_video"] = "第三方加速接口（视频）",
        ["default"] = "默认",
        ["alipanTV"] = "阿里盘 TV 端",
        ["resource"] = "资源盘",
        ["backup"] = "备份盘",
        ["trash"] = "移到回收站",
        ["delete"] = "直接删除",
        ["web"] = "网页版",
        ["pc"] = "电脑客户端",
        ["jpeg"] = "JPEG 图片",
        ["mov"] = "MOV 视频",
        ["password"] = "账密登录",
        ["qrcode"] = "扫码登录",
        ["personal"] = "个人云",
        ["personal_new"] = "个人云（新）",
        ["family"] = "家庭云",
        ["group"] = "群组",
        ["share"] = "共享",
        ["account"] = "账密",
        ["cookie"] = "登录凭证",
        ["url"] = "分享链接",
        ["stream"] = "流式上传",
        ["rapid"] = "秒传",
        ["old"] = "旧版上传",
        ["name_asc"] = "名称升序",
        ["name_desc"] = "名称降序",
        ["time_asc"] = "时间升序",
        ["time_desc"] = "时间降序",
        ["size_asc"] = "大小升序",
        ["size_desc"] = "大小降序",
    };

    /// <summary>选项的中文显示（找不到回退原值）。</summary>
    public static string OptionLabel(string value) =>
        OptionLabels.TryGetValue(value, out var label) ? label : value;

    /// <summary>网盘服务返回的英文字段说明 → 中文（按本机实测的原文精确匹配）。</summary>
    private static readonly Dictionary<string, string> HelpTranslations = new(StringComparer.Ordinal)
    {
        ["The path you want to mount to, it is unique and cannot be repeated"] = "要挂载到的路径，必须唯一、不能重复",
        ["use to sort"] = "用于排序",
        ["Disable sign for Download proxy URL"] = "关闭下载代理地址的签名",
        ["Override automatic transfer-seed generation for this storage"] = "覆盖此存储的自动秒传种子生成设置",
        ["This might impact host performance"] = "可能影响主机性能",
        ["enable thumbnail"] = "启用缩略图",
        ["Generate PDF first-page thumbnails with Quick Look on macOS"] = "在 macOS 上生成 PDF 首页缩略图",
        ["Number of concurrent thumbnail generation goroutines. This controls how many thumbnails can be generated in parallel."] = "缩略图生成的并发数，控制可同时生成的缩略图数量",
        ["The position of the video thumbnail. If the value is a number (integer ot floating point), it represents the time in seconds. If the value ends with '%', it represents the percentage of the video duration."] = "视频缩略图的位置：填数字表示秒数；以 % 结尾表示视频时长的百分比",
        ["show hidden directories and files"] = "显示隐藏的目录和文件",
        ["path to recycle bin, delete permanently if empty or keep 'delete permanently'"] = "回收站路径；留空表示彻底删除",
        ["The cache expiration time for this storage"] = "此存储的缓存有效期",
        ["The cache expiration rules for this storage"] = "此存储的缓存有效期规则",
        ["Keep it empty if you don't have one"] = "没有就留空",
        ["If you enable this option, the file will be uploaded to the server first, so the progress will be incorrect"] = "开启后文件会先上传到服务器，进度显示将不准确",
        ["If you are using Aliyun ECS is located in Beijing, you can turn it on to boost the upload speed"] = "使用位于北京的阿里云服务器时可开启，以提升上传速度",
        ["Maximum active proxied downloads shared by Aliyun user"] = "阿里云用户共享的最大代理下载并发数",
        ["limit all api request rate ([limit]r/1s)"] = "限制所有接口的请求频率（次/秒）",
        ["list api per page size of 115open driver"] = "列表接口的每页数量",
        ["the threads of upload"] = "上传线程数",
        ["use direct link when download file"] = "下载文件时使用直链",
        ["private key for direct link, if URL authentication is enabled"] = "开启网址鉴权时的直链私钥",
        ["minutes, if URL authentication is enabled"] = "开启网址鉴权时的有效期（分钟）",
        ["You can watch the transcoded video and support 302 redirection"] = "可观看转码后的视频，支持 302 跳转",
        ["1<=thread<=32"] = "线程数范围 1~32",
        ["per-slice upload timeout in seconds"] = "单个分片的上传超时时间（秒）",
        ["dynamically get upload api domain, when enabled, the 'Upload API' setting will be used as a fallback if failed to get"] = "动态获取上传接口域名；获取失败时回退使用上方填写的上传接口",
        ["0 for auto"] = "填 0 表示自动",
        ["Not needed when an access token or refresh token is provided"] = "已提供访问令牌或刷新令牌时无需填写",
        ["SMS code for the second device verification, fill it in and save again when login asks for it"] = "二次设备验证的短信验证码；登录要求时填入并再次保存",
        ["To switch accounts, please clear this field"] = "如需切换账号，请清空此字段",
        ["DEVICEID cookie issued after the second device verification, keep it to avoid verifying again"] = "二次验证后签发的设备标识，保留它可避免重复验证",
        ["Device serial number captured from the official client, leave it empty if you do not have one"] = "从官方客户端抓包得到的设备序列号，没有则留空",
        ["Optional push id reported by the official client"] = "可选，官方客户端上报的推送 ID",
        ["Device fingerprint sent with login requests, generated and kept automatically when empty"] = "登录请求携带的设备指纹，留空时自动生成并保存",
        ["Generate torrent file with CAS extension after upload"] = "上传后生成种子文件",
        ["Need to enable proxy"] = "需要开启代理",
        ["Authorization can be used alone. If empty, use username + password; mail_cookies is optional and will be established/updated automatically. Existing mail_cookies can also be used alone for fast login."] = "授权字符串可单独使用；留空则用账号加密码登录；邮箱凭证可选，会自动建立并更新，已有邮箱凭证也可单独用于快速登录",
        ["Use together with password when Authorization is empty. mail_cookies may be left empty on the first login."] = "授权字符串为空时与密码配合使用；首次登录邮箱凭证可留空",
        ["Use together with username when Authorization is empty. mail_cookies may be left empty on the first login."] = "授权字符串为空时与账号配合使用；首次登录邮箱凭证可留空",
        ["Fill this only after OpenList reports that a 139 Mail SMS verification code was sent, then save the storage again."] = "仅当网盘服务提示已发送 139 邮箱短信验证码后填写，然后再次保存",
        ["Optional cookies from mail.10086.cn. Leave empty for a first username/password login; cookies created or updated by password/SMS login are persisted and reused as device context. Existing cookies may also be used alone for fast login."] = "可选，139 邮箱的登录凭证；首次账密登录留空，登录后会自动保存并复用",
        ["Multiple shares are separated by commas or new lines. Use link_id#password for password-protected shares."] = "多个分享用逗号或换行分隔；加密分享用「分享ID#密码」格式",
        ["ud_id in Cookie, fill in to show disk usage"] = "登录凭证中的 ud_id，填写后可显示网盘用量",
        ["Enable to report the real file size during upload"] = "开启后上传时上报真实文件大小",
        ["Enable to use large thumbnail for images"] = "开启后图片使用大缩略图",
        ["Enable to use old stream upload method (not support rapid upload)"] = "开启后使用旧版流式上传（不支持秒传）",
        ["Client type reported to the personal cloud API. web (9) allows uploading files up to 50 GB; pc (13) raises the single file upload limit to 500 GB."] = "上报给个人云接口的客户端类型：网页版单文件上限 50 GB，电脑客户端提升到 500 GB",
        ["about 15 days valid, ignore if shareUrl is used"] = "有效期约 15 天；使用分享链接时忽略",
        ["basic URL for file operation"] = "文件操作的基础地址",
        ["used to get the sharing page"] = "用于获取分享页面",
        ["To use webdav, you need to enable it"] = "如需使用 WebDAV 需开启此项",
        ["Full path in GuangYaPan cloud drive"] = "光鸭云盘内的完整路径",
        ["Phone number for SMS login, e.g. +86 13800000000"] = "短信登录的手机号，例如 +86 13800000000",
        ["Captcha token required by /v1/auth/verification"] = "验证接口要求的验证码令牌",
        ["Set true and save to send SMS code, it auto-resets to false after sending"] = "勾选并保存即可发送短信验证码，发送后自动复位",
        ["SMS verification code used with phone_number; fill then save to finish login"] = "与手机号配合使用的短信验证码，填入后保存完成登录",
        ["Auto-generated after sending SMS code; do not edit manually"] = "发送验证码后自动生成，请勿手动修改",
        ["Bearer access token (optional if refresh_token is provided)"] = "访问令牌（已提供刷新令牌时可选）",
        ["Refresh token for auto-login/auto-refresh"] = "用于自动登录与自动续期的刷新令牌",
        ["Client ID for GuangYaPan API, must be provided"] = "光鸭云盘接口的应用 ID，必填",
        ["Optional custom device id (32 hex chars), auto-generated when empty"] = "可选的自定义设备 ID（32 位十六进制字符），留空自动生成",
        ["Optional custom X-Device-Sign header (generated from device_id when empty)"] = "可选的自定义设备签名请求头，留空时由设备 ID 生成",
        ["Sort field used by the file list"] = "文件列表使用的排序字段",
        ["Sort direction used by the file list"] = "文件列表使用的排序方向",
        ["Provide a refresh token or an access token (at least 16 characters)"] = "提供刷新令牌或访问令牌（至少 16 个字符）",
        ["Keep it empty if you want to use your personal drive"] = "使用个人云盘请留空",
        ["Optional when a refresh token is provided; it is refreshed automatically when missing or too short"] = "已提供刷新令牌时可选；缺失或过短时自动刷新",
    };

    /// <summary>字段说明的中文（无对应翻译时返回空串，界面不再显示英文原文）。</summary>
    public static string HelpFor(string? rawHelp) =>
        string.IsNullOrWhiteSpace(rawHelp) ? ""
        : HelpTranslations.TryGetValue(rawHelp.Trim(), out var zh) ? zh
        : "";
}
