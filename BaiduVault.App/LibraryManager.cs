using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BaiduVault.App;

/// <summary>库生命周期：OpenList 存储 + rclone crypt/chunker + vault combine + 密钥文件。</summary>
public sealed class LibraryManager
{
    private static readonly string Root =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BaiduVault");
    private static readonly string ConfPath = Path.Combine(Root, "rclone.conf");
    private static readonly string KeysPath = Path.Combine(Root, "keys", "rclone-keys.txt");
    private static readonly string RcloneExe = Path.Combine(Root, "bin", "rclone.exe");
    private static readonly string BackupDir = Path.Combine(Root, "backup-library-mgr");

    private readonly OpenListClient _ol;
    private readonly string _webdavUser;
    private readonly string _webdavPass;

    public LibraryManager(OpenListClient ol, string webdavUser, string webdavPass)
    {
        _ol = ol;
        _webdavUser = webdavUser;
        _webdavPass = webdavPass;
    }

    public enum LibStatus { Normal, Expired, ServiceError, Unknown }

    public sealed record LibraryInfo(
        string Name,        // 库名（挂载路径去前缀 /）
        string DisplayName, // OpenList remark，空则用 Name
        string Driver,
        string MountPath,
        int StorageId,      // OpenList 存储 id（删除账号用）
        string Addition,    // OpenList 存储的授权字段 JSON（只做展示，不含明文口令）
        bool HasCryptKey,
        bool Mounted,       // rclone.conf 中存在 crypt+chunker 且在 vault upstream 中
        LibStatus Status,
        string? StatusDetail,
        string? Advice);

    public async Task<List<LibraryInfo>> ListLibrariesAsync()
    {
        await _ol.LoginAsync();
        var storages = await _ol.ListStoragesAsync();
        var cfg = RcloneConfig.Load(ConfPath);
        var upstreams = cfg.GetVaultUpstreams();
        var keys = LoadKeys();

        var result = new List<LibraryInfo>();
        foreach (var s in storages.Content)
        {
            var name = s.MountPath.TrimStart('/');
            if (string.IsNullOrEmpty(name)) continue;
            var mounted = upstreams.ContainsKey(name) && cfg.HasSection($"{name}_crypt") && cfg.HasSection($"{name}_chunk512");
            var hasKey = keys.ContainsKey(name);
            var (status, detail, advice) = await ProbeAsync(name, s.Status, mounted);
            result.Add(new LibraryInfo(
                name,
                string.IsNullOrEmpty(s.Remark) ? name : s.Remark,
                s.Driver,
                s.MountPath,
                s.Id,
                s.Addition,
                hasKey,
                mounted,
                status,
                detail,
                advice));
        }
        return result;
    }

    /// <summary>
    /// 三态归因：OpenList 存储状态（/api/admin/storage/list）→ 库级 WebDAV PROPFIND → rclone serve PROPFIND。
    /// 区分「凭证过期」（需重登）与「服务异常」（需重启服务），避免误导用户去重登。
    /// </summary>
    private async Task<(LibStatus, string?, string?)> ProbeAsync(string name, string olStatus, bool mounted)
    {
        if (!mounted) return (LibStatus.Unknown, "未挂载（仅网盘服务存储存在）", "点「编辑/重登」顶部无入口时，先在库管理页重新添加该库以生成加密密钥。");
        if (!ServiceManager.IsPortOpen(AppSettings.Current.OpenListPort))
            return (LibStatus.ServiceError, $"网盘服务未运行（端口 {AppSettings.Current.OpenListPort} 未监听）", "点「重启服务」；若无效，关闭应用后重新打开以拉起网盘服务。");
        if (!ServiceManager.IsPortOpen(AppSettings.Current.WebDavPort))
            return (LibStatus.ServiceError, $"播放服务未运行（端口 {AppSettings.Current.WebDavPort} 未监听）", "点「重启服务」重新拉起播放服务。");

        // 1) OpenList 侧：该库的驱动是否还能列目录（最能归因凭证问题）
        var (olCode, olErr) = await ProbeDavAsync(
            $"http://127.0.0.1:{AppSettings.Current.OpenListPort}/dav/{Uri.EscapeDataString(name)}/",
            openListSide: true);
        if (olCode is 401 or 403)
            return (LibStatus.Expired,
                $"网盘服务拒绝访问（HTTP {olCode}）；驱动状态={StatusText(olStatus)}",
                "点「编辑/重登」，按引导重新填写授权字段（登录凭证/令牌/账密）。");
        if (olCode == 0 || olCode >= 500)
        {
            var (s, d, a) = ClassifyOpenListStatus(olStatus, olErr);
            return (s, d, a);
        }

        // 2) rclone serve 侧：投递链是否可达（库已挂载时应能 PROPFIND 到根）
        var (svCode, svErr) = await ProbeDavAsync(
            $"http://127.0.0.1:{AppSettings.Current.WebDavPort}/{Uri.EscapeDataString(name)}/",
            openListSide: false);
        if (svCode == 207) return (LibStatus.Normal, null, null);
        if (svCode is 401 or 403)
            return (LibStatus.ServiceError, $"播放服务拒绝访问（HTTP {svCode}）", "检查密钥目录下的播放凭证是否与启动参数一致，然后重启服务。");
        if (svCode == 0 || svCode >= 500)
            return (LibStatus.ServiceError, $"播放服务返回异常（{(svCode == 0 ? svErr : "HTTP " + svCode)}）", "点「重启服务」；仍失败请查看日志目录里的播放服务日志。");
        return (LibStatus.ServiceError, $"HTTP {svCode}", "点「重启服务」；仍失败请查看日志。");
    }

    private async Task<(int Code, string? Error)> ProbeDavAsync(string url, bool openListSide)
    {
        string user, pass;
        if (openListSide) (user, pass) = OpenListClient.LoadAdminCredentials();
        else (user, pass) = (_webdavUser, _webdavPass);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), url);
            req.Headers.Add("Authorization", "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes($"{user}:{pass}")));
            req.Headers.Add("Depth", "0");
            using var resp = await http.SendAsync(req);
            return ((int)resp.StatusCode, null);
        }
        catch (Exception ex)
        {
            return (0, ex.Message);
        }
    }

    private static (LibStatus, string?, string?) ClassifyOpenListStatus(string olStatus, string? transportError)
    {
        if (transportError != null && transportError.Length > 0)
            return (LibStatus.ServiceError, "网盘服务不可达：" + transportError, "点「重启服务」，或重新打开应用拉起网盘服务。");
        var text = olStatus ?? "";
        if (text.Equals("work", StringComparison.OrdinalIgnoreCase))
            return (LibStatus.Normal, null, null);
        var k = text.ToLowerInvariant();
        var credentialLike = new[]
        {
            "token", "cookie", "refresh", "access", "auth", "授权", "凭证", "登录", "失效",
            "expired", "expire", "invalid", "unauthorized", "401", "403", "验证码",
        }.Any(k.Contains);
        if (credentialLike)
            return (LibStatus.Expired, $"网盘服务驱动状态：{StatusText(text)}", "点「编辑/重登」，重新填写授权字段；登录凭证/验证码类驱动掉线属网盘风控。");
        return (LibStatus.ServiceError, $"网盘服务驱动状态：{StatusText(text)}", "点「重启服务」；仍失败请查看网盘服务日志。");
    }

    /// <summary>把网盘服务返回的英文驱动状态翻成中文，便于用户判断。</summary>
    private static string StatusText(string status)
    {
        return (status ?? "").Trim().ToLowerInvariant() switch
        {
            "" or "none" => "未配置",
            "work" => "正常",
            "init" => "初始化中",
            "expired" => "凭证过期",
            "invalid" => "凭证无效",
            "unauthorized" => "未授权",
            "disabled" => "已停用",
            _ => status!,
        };
    }

    /// <summary>该库名是否已生成 crypt 密钥（即真正挂载过的库）。</summary>
    public bool HasCryptKey(string name) => LoadKeys().ContainsKey(name);

    /// <summary>添加库：OpenList 建存储 → 生成 crypt 密钥 → 写 rclone.conf → 更新 vault → 写密钥 → 重启 serve → 连接测试。</summary>
    public async Task AddLibraryAsync(string name, string driver, Dictionary<string, object> addition,
        string remark, ServiceManager svc, string chunkSize = "512M")
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("库名不能为空");
        await _ol.LoginAsync();
        Backup();

        // 1. OpenList 建存储
        var storage = new OlStorage
        {
            MountPath = "/" + name,
            Driver = driver,
            Remark = remark,
            Addition = JsonSerializer.Serialize(addition),
            WebDavPolicy = "native_proxy",
            SeedPolicy = "inherit",
        };
        var createdStorage = false;
        try
        {
            await _ol.CreateStorageAsync(storage).ConfigureAwait(false);
            createdStorage = true;
        }
        catch
        {
            // OpenList 有时会「报错但存储已落库」（如驱动初始化失败），先清掉同名存储
            try { await DeleteStorageByNameAsync(name).ConfigureAwait(false); } catch { }
            throw;
        }
        var configTouched = false;
        try
        {
            // 2. 生成 crypt 密钥
            var (pass1, pass2) = GenerateCryptPasswords();
            var obs1 = Obscure(pass1);
            var obs2 = Obscure(pass2);

            // 3. 写 rclone.conf
            var cfg = RcloneConfig.Load(ConfPath);
            if (cfg.HasSection($"{name}_crypt") || cfg.HasSection($"{name}_chunk512"))
                throw new InvalidOperationException($"同步配置里已有 {name} 的相关设置，请先移出挂载");

            configTouched = true;
            cfg.SetSection($"{name}_crypt", new Dictionary<string, string>
            {
                ["type"] = "crypt",
                ["remote"] = $"opl:/{name}/secret_vault",
                ["filename_encryption"] = "standard",
                ["directory_name_encryption"] = "true",
                ["suffix"] = "none",
                ["password"] = obs1,
                ["password2"] = obs2,
            });
            cfg.SetSection($"{name}_chunk512", new Dictionary<string, string>
            {
                ["type"] = "chunker",
                ["remote"] = $"{name}_crypt:",
                ["chunk_size"] = chunkSize,
                ["name_format"] = "*.rcc##",
            });
            var up = cfg.GetVaultUpstreams();
            up[name] = $"{name}_chunk512:";
            cfg.SetVaultUpstreams(up);
            cfg.Save();

            // 4. 写密钥
            AppendKeys(name, pass1, pass2, obs1, obs2);

            // 5. 重启 serve 让新库生效
            svc.RestartServe();

            // 6. 连接测试（失败则完整回滚，避免留下半配置）
            var (status, detail, advice) = await ProbeAsync(name, "work", true);
            if (status != LibStatus.Normal)
                throw new InvalidOperationException(
                    $"库 {name} 连接测试失败：{detail ?? "未知"}。{advice ?? ""}");
        }
        catch
        {
            // 回滚：删 crypt 段 + vault 条目 + 密钥 + OpenList 存储，再重启 serve
            try
            {
                if (configTouched)
                {
                    var cfg = RcloneConfig.Load(ConfPath);
                    cfg.RemoveSection($"{name}_crypt");
                    cfg.RemoveSection($"{name}_chunk512");
                    var up = cfg.GetVaultUpstreams();
                    up.Remove(name);
                    cfg.SetVaultUpstreams(up);
                    cfg.Save();
                }
                RemoveKeys(name);
                if (createdStorage)
                {
                    var list = await _ol.ListStoragesAsync();
                    var s = list.Content.FirstOrDefault(x => x.MountPath.TrimEnd('/') == "/" + name);
                    if (s != null) await _ol.DeleteStorageAsync(s.Id);
                }
                svc.RestartServe();
            }
            catch { /* 回滚尽力而为，原始异常继续抛出 */ }
            throw;
        }
    }

    /// <summary>编辑/重登：只更新 OpenList 存储的驱动参数与备注，不动 crypt 密钥与挂载。</summary>
    public async Task UpdateStorageAsync(string name, string driver,
        Dictionary<string, object> addition, string remark)
    {
        await _ol.LoginAsync();
        var list = await _ol.ListStoragesAsync();
        var s = list.Content.FirstOrDefault(x => x.MountPath.TrimEnd('/') == "/" + name)
                ?? throw new InvalidOperationException($"网盘服务中未找到库 {name}");
        s.Driver = driver;
        s.Remark = remark;
        s.Addition = JsonSerializer.Serialize(addition);
        s.MountPath = "/" + name;
        await _ol.UpdateStorageAsync(s);
    }

    private async Task DeleteStorageByNameAsync(string name)
    {
        var list = await _ol.ListStoragesAsync();
        var s = list.Content.FirstOrDefault(x => x.MountPath.TrimEnd('/') == "/" + name);
        if (s != null) await _ol.DeleteStorageAsync(s.Id);
    }

    public async Task RenameLibraryAsync(string oldName, string newName, ServiceManager svc)
    {
        if (oldName == newName) return;
        await _ol.LoginAsync();
        Backup();

        // OpenList 改挂载路径
        var storages = await _ol.ListStoragesAsync();
        var s = storages.Content.FirstOrDefault(x => x.MountPath == "/" + oldName)
                ?? throw new InvalidOperationException($"网盘服务中未找到库 {oldName}");
        s.MountPath = "/" + newName;
        await _ol.UpdateStorageAsync(s);

        // rclone.conf: 重命名段 + remote 引用 + vault upstream
        var cfg = RcloneConfig.Load(ConfPath);
        cfg.RenameSection($"{oldName}_crypt", $"{newName}_crypt");
        cfg.RenameSection($"{oldName}_chunk512", $"{newName}_chunk512");
        var cryptRemote = cfg.Get($"{newName}_crypt", "remote");
        if (cryptRemote != null) cfg.SetSection($"{newName}_crypt", RebuildCryptKv(cfg, $"{newName}_crypt", newName));
        var chunkerRemote = cfg.Get($"{newName}_chunk512", "remote");
        if (chunkerRemote != null)
        {
            // 重写 chunker 段以修正 remote
            cfg.SetSection($"{newName}_chunk512", new Dictionary<string, string>
            {
                ["type"] = "chunker",
                ["remote"] = $"{newName}_crypt:",
                ["chunk_size"] = "512M",
                ["name_format"] = "*.rcc##",
            });
        }
        var up = cfg.GetVaultUpstreams();
        if (up.Remove(oldName, out var remote)) up[newName] = remote.Replace(oldName, newName);
        cfg.SetVaultUpstreams(up);
        cfg.Save();

        // keys：重命名前缀
        RenameKeys(oldName, newName);

        svc.RestartServe();
    }

    private static Dictionary<string, string> RebuildCryptKv(RcloneConfig cfg, string section, string libName)
    {
        // 保留原 crypt 段的键值，仅修正 remote
        var kv = new Dictionary<string, string>();
        foreach (var line in new[] { "type", "remote", "filename_encryption", "directory_name_encryption", "suffix", "password", "password2" })
        {
            var v = cfg.Get(section, line);
            if (v != null) kv[line] = line == "remote" ? $"opl:/{libName}/secret_vault" : v;
        }
        return kv;
    }

    /// <summary>仅移出挂载（保留 OpenList 存储与密钥）。</summary>
    public void RemoveMount(string name, ServiceManager svc)
    {
        Backup();
        var cfg = RcloneConfig.Load(ConfPath);
        cfg.RemoveSection($"{name}_crypt");
        cfg.RemoveSection($"{name}_chunk512");
        var up = cfg.GetVaultUpstreams();
        up.Remove(name);
        cfg.SetVaultUpstreams(up);
        cfg.Save();
        svc.RestartServe();
    }

    /// <summary>彻底删除：移出挂载 + 删 OpenList 存储 + 删密钥段。</summary>
    public async Task DeleteLibraryAsync(string name, ServiceManager svc) =>
        await DeleteAccountAsync(name, svc, deleteKeys: true);

    /// <summary>删除账号：移出挂载 + 删 OpenList 登录凭证（存储）；deleteKeys 决定是否删掉加密密钥。</summary>
    public async Task DeleteAccountAsync(string name, ServiceManager svc, bool deleteKeys)
    {
        RemoveMount(name, svc);
        await _ol.LoginAsync();
        var storages = await _ol.ListStoragesAsync();
        var s = storages.Content.FirstOrDefault(x => x.MountPath == "/" + name);
        if (s != null) await _ol.DeleteStorageAsync(s.Id);
        if (deleteKeys) RemoveKeys(name);
    }

    // ---------- 密钥 ----------

    private static Dictionary<string, (string O1, string O2)> LoadKeys()
    {
        var dict = new Dictionary<string, (string O1, string O2)>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(KeysPath)) return dict;
        string? current = null;
        string? o1 = null, o2 = null;
        foreach (var raw in File.ReadAllLines(KeysPath))
        {
            var line = raw.Trim();
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^#\s*===\s*库:\s*(\S+)");
            if (m.Success)
            {
                if (current != null && o1 != null) dict[current] = (o1, o2 ?? "");
                current = m.Groups[1].Value; o1 = o2 = null;
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq]; var val = line[(eq + 1)..];
            string lib;
            if (key == "CRYPT_PASS_OBSCURED") lib = "baidu";
            else if (key.EndsWith("_CRYPT_PASS_OBSCURED", StringComparison.Ordinal))
                lib = key[..^"_CRYPT_PASS_OBSCURED".Length];
            else if (key == "CRYPT_PASS2_OBSCURED") lib = "baidu";
            else if (key.EndsWith("_CRYPT_PASS2_OBSCURED", StringComparison.Ordinal))
                lib = key[..^"_CRYPT_PASS2_OBSCURED".Length];
            else continue;
            if (key.EndsWith("PASS2_OBSCURED", StringComparison.Ordinal))
                dict[lib] = (dict.TryGetValue(lib, out var t) ? t.O1 : "", val);
            else
                dict[lib] = (val, dict.TryGetValue(lib, out var t2) ? t2.O2 : "");
        }
        if (current != null && o1 != null) dict[current] = (o1, o2 ?? "");
        return dict;
    }

    private static void AppendKeys(string lib, string p1, string p2, string o1, string o2)
    {
        var prefix = lib.Equals("baidu", StringComparison.OrdinalIgnoreCase) ? "" : lib.ToUpperInvariant() + "_";
        var sb = new StringBuilder();
        if (File.Exists(KeysPath) && new FileInfo(KeysPath).Length > 0)
            sb.AppendLine();
        sb.AppendLine($"# === 库: {lib} ===");
        sb.AppendLine($"{prefix}CRYPT_PASS={p1}");
        sb.AppendLine($"{prefix}CRYPT_PASS2={p2}");
        sb.AppendLine($"{prefix}CRYPT_PASS_OBSCURED={o1}");
        sb.AppendLine($"{prefix}CRYPT_PASS2_OBSCURED={o2}");
        File.AppendAllText(KeysPath, sb.ToString());
    }

    private static void RemoveKeys(string lib)
    {
        if (!File.Exists(KeysPath)) return;
        var lines = File.ReadAllLines(KeysPath).ToList();
        var prefix = lib.Equals("baidu", StringComparison.OrdinalIgnoreCase) ? "" : lib.ToUpperInvariant() + "_";
        var outLines = new List<string>();
        var inSection = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            var m = System.Text.RegularExpressions.Regex.Match(t, @"^#\s*===\s*库:\s*(\S+)");
            if (m.Success)
            {
                inSection = m.Groups[1].Value.Equals(lib, StringComparison.OrdinalIgnoreCase);
                if (inSection) continue;
            }
            if (inSection && (t.StartsWith(prefix + "CRYPT_PASS") || t.StartsWith("# " + prefix) || t.Length == 0))
                continue;
            outLines.Add(lines[i]);
        }
        File.WriteAllLines(KeysPath, outLines);
    }

    private static void RenameKeys(string oldLib, string newLib)
    {
        if (!File.Exists(KeysPath)) return;
        var content = File.ReadAllText(KeysPath);
        content = content.Replace($"# === 库: {oldLib}", $"# === 库: {newLib}");
        var oldP = oldLib.Equals("baidu", StringComparison.OrdinalIgnoreCase) ? "" : oldLib.ToUpperInvariant() + "_";
        var newP = newLib.Equals("baidu", StringComparison.OrdinalIgnoreCase) ? "" : newLib.ToUpperInvariant() + "_";
        content = content.Replace(oldP + "CRYPT_PASS", newP + "CRYPT_PASS");
        File.WriteAllText(KeysPath, content);
    }

    // ---------- 备份包加密（APBX1 格式：魔数 + 盐 + Nonce + GCM 标签 + AES-256-GCM 密文） ----------

    private static readonly byte[] BackupMagic = "APBX1"u8.ToArray();
    private const int Pbkdf2Iterations = 200_000;

    /// <summary>判断备份包是否为加密包（读文件头魔数）。</summary>
    public static bool IsBackupEncrypted(string zipPath)
    {
        using var fs = File.OpenRead(zipPath);
        var head = new byte[BackupMagic.Length];
        if (fs.Read(head, 0, head.Length) != head.Length) return false;
        return head.SequenceEqual(BackupMagic);
    }

    /// <summary>导出密钥备份包（含 rclone.conf + keys 目录）。password 非空时整体 AES-256-GCM 加密。</summary>
    public static void ExportBackup(string zipPath, string? password)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);

        byte[] plain;
        using (var mem = new MemoryStream())
        {
            using (var zip = new ZipArchive(mem, ZipArchiveMode.Create, true))
            {
                zip.CreateEntryFromFile(ConfPath, "rclone.conf");
                foreach (var f in Directory.GetFiles(Path.Combine(Root, "keys")))
                    zip.CreateEntryFromFile(f, "keys/" + Path.GetFileName(f));
            }
            plain = mem.ToArray();
        }

        if (string.IsNullOrEmpty(password))
        {
            File.WriteAllBytes(zipPath, plain);
            return;
        }
        File.WriteAllBytes(zipPath, EncryptBackup(plain, password));
    }

    private static byte[] EncryptBackup(byte[] plain, string password)
    {
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
            aes.Encrypt(nonce, plain, cipher, tag);
        // 魔数(5) + 盐(16) + Nonce(12) + 标签(16) + 密文
        using var outp = new MemoryStream();
        outp.Write(BackupMagic);
        outp.Write(salt);
        outp.Write(nonce);
        outp.Write(tag);
        outp.Write(cipher);
        return outp.ToArray();
    }

    private static byte[] DecryptBackup(byte[] data, string password)
    {
        var headLen = BackupMagic.Length + 16 + 12 + 16;
        if (data.Length <= headLen || !data.AsSpan(0, BackupMagic.Length).SequenceEqual(BackupMagic))
            throw new InvalidOperationException("不是有效的加密备份包");
        var salt = data.AsSpan(BackupMagic.Length, 16);
        var nonce = data.AsSpan(BackupMagic.Length + 16, 12);
        var tag = data.AsSpan(BackupMagic.Length + 28, 16);
        var cipher = data.AsSpan(headLen);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        var plain = new byte[cipher.Length];
        try
        {
            using (var aes = new AesGcm(key, 16))
                aes.Decrypt(nonce, cipher, tag, plain);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("密码错误或备份包已损坏");
        }
        return plain;
    }

    /// <summary>把备份包（明文 zip 或加密包）解出到临时目录。</summary>
    private static void ExtractBackup(string zipPath, string? password, string tmp)
    {
        Directory.CreateDirectory(tmp);
        if (IsBackupEncrypted(zipPath))
        {
            if (string.IsNullOrEmpty(password))
                throw new InvalidOperationException("该备份包已加密，需要输入密码");
            var plain = DecryptBackup(File.ReadAllBytes(zipPath), password);
            var inner = Path.Combine(tmp, "backup.zip");
            File.WriteAllBytes(inner, plain);
            ZipFile.ExtractToDirectory(inner, tmp);
            File.Delete(inner);
        }
        else
        {
            ZipFile.ExtractToDirectory(zipPath, tmp);
        }
    }

    /// <summary>预览备份包内容：返回库名列表（从 rclone.conf 的 vault upstreams 解析）。</summary>
    public static List<string> PreviewBackup(string zipPath, string? password = null)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "baiduvault-import-preview-" + Guid.NewGuid().ToString("N"));
        try
        {
            ExtractBackup(zipPath, password, tmp);
            var confFile = Path.Combine(tmp, "rclone.conf");
            if (!File.Exists(confFile))
                throw new InvalidOperationException("备份包内没有同步配置文件");
            var cfg = RcloneConfig.Load(confFile);
            return cfg.GetVaultUpstreams().Keys.ToList();
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    /// <summary>导入备份包：覆盖 rclone.conf + keys 目录，然后重启 serve。
    /// 警告：会覆盖当前所有库的密钥与配置。调用前应让用户确认。</summary>
    public static void ImportBackup(string zipPath, ServiceManager svc, string? password = null)
    {
        Backup(); // 先备份当前状态到 backup-library-mgr
        var tmp = Path.Combine(Path.GetTempPath(), "baiduvault-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            ExtractBackup(zipPath, password, tmp);

            var confFile = Path.Combine(tmp, "rclone.conf");
            var keysDir = Path.Combine(tmp, "keys");
            if (!File.Exists(confFile))
                throw new InvalidOperationException("备份包内没有同步配置文件");

            // 覆盖 rclone.conf
            File.Copy(confFile, ConfPath, true);

            // 覆盖 keys 目录
            var realKeysDir = Path.Combine(Root, "keys");
            Directory.CreateDirectory(realKeysDir);
            if (Directory.Exists(keysDir))
            {
                foreach (var f in Directory.GetFiles(realKeysDir))
                    try { File.Delete(f); } catch { }
                foreach (var f in Directory.GetFiles(keysDir))
                    File.Copy(f, Path.Combine(realKeysDir, Path.GetFileName(f)), true);
            }

            svc.RestartServe();
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    // ---------- 工具 ----------

    private static (string, string) GenerateCryptPasswords()
    {
        var rnd = RandomNumberGenerator();
        return (RandomString(rnd, 32), RandomString(rnd, 32));
    }

    private static System.Security.Cryptography.RandomNumberGenerator RandomNumberGenerator()
        => System.Security.Cryptography.RandomNumberGenerator.Create();

    private static string RandomString(System.Security.Cryptography.RandomNumberGenerator rng, int len)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789+/";
        var bytes = new byte[len];
        rng.GetBytes(bytes);
        var sb = new StringBuilder(len);
        foreach (var b in bytes) sb.Append(chars[b % chars.Length]);
        return sb.ToString();
    }

    private static string Obscure(string plain)
    {
        var psi = new ProcessStartInfo
        {
            FileName = RcloneExe,
            Arguments = $"obscure \"{plain}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var outp = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        if (p.ExitCode != 0 || string.IsNullOrEmpty(outp))
            throw new InvalidOperationException($"生成加密密钥失败：{p.StandardError.ReadToEnd()}");
        return outp;
    }

    private static void Backup()
    {
        try
        {
            Directory.CreateDirectory(BackupDir);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            if (File.Exists(ConfPath)) File.Copy(ConfPath, Path.Combine(BackupDir, $"rclone.conf.{stamp}.bak"), true);
            if (File.Exists(KeysPath)) File.Copy(KeysPath, Path.Combine(BackupDir, $"rclone-keys.txt.{stamp}.bak"), true);
        }
        catch { /* 备份失败不阻塞主流程，由 save 前的内存态兜底 */ }
    }
}
