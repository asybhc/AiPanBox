using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.Sockets;

namespace BaiduVault.App;

/// <summary>
/// 本地实例管家：检测 openlist(:5244) / rclone serve(:8088)，未运行则用 WMI
/// Win32_Process.Create 脱离启动（与 setup-local.ps1 的 Start-Detached 同一思路，
/// 进程不随本应用退出而被回收）。不装服务、不写注册表。
/// </summary>
public sealed class ServiceManager
{
    public int OpenListPort => AppSettings.Current.OpenListPort;
    public int WebDavPort => AppSettings.Current.WebDavPort;

    private static readonly string Root =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BaiduVault");

    public string User { get; private set; } = "";
    public string Pass { get; private set; } = "";

    /// <summary>确保 8088 可用；必要时拉起 openlist 与 rclone serve。抛异常 = 需要用户先跑 setup。</summary>
    public void EnsureRunning()
    {
        LoadCredentials();
        if (IsPortOpen(WebDavPort)) return;

        var openlist = Path.Combine(Root, "bin", "openlist.exe");
        var rclone = Path.Combine(Root, "bin", "rclone.exe");
        var conf = Path.Combine(Root, "rclone.conf");
        if (!File.Exists(openlist) || !File.Exists(rclone) || !File.Exists(conf))
            throw new InvalidOperationException(
                "未检测到已部署的本地服务（缺少程序或同步配置）。请先在 BaiduVault-local 目录运行 setup-local.ps1 完成部署。");

        if (!IsPortOpen(OpenListPort))
        {
            var pid = StartHidden(
                $"\"{openlist}\" server --data \"{Path.Combine(Root, "openlist-data")}\"", Root);
            WritePid("openlist.pid", pid);
            WaitPort(OpenListPort, 40, "网盘服务");
        }

        var cmd = BuildServeCmd();
        var servePid = StartHidden(cmd, Root);
        WritePid("rclone-serve.pid", servePid);
        WaitPort(WebDavPort, 30, "播放服务");
    }

    public VaultClient CreateClient() => new(User, Pass, WebDavPort);

    private string BuildServeCmd()
    {
        var rclone = Path.Combine(Root, "bin", "rclone.exe");
        var conf = Path.Combine(Root, "rclone.conf");
        return string.Join(' ', new[]
        {
            $"\"{rclone}\"", "--config", $"\"{conf}\"",
            "serve", "webdav", "vault:",
            "--addr", $"127.0.0.1:{WebDavPort}",
            "--user", User, "--pass", Pass,
            "--vfs-cache-mode", "full",
            "--cache-dir", $"\"{Path.Combine(Root, "cache")}\"",
            "--vfs-cache-max-size", AppSettings.Current.CacheMaxSize,
            "--vfs-cache-max-age", "48h",
            "--vfs-read-ahead", "64M", "--buffer-size", "8M",
            "--vfs-read-chunk-size", "16M", "--vfs-read-chunk-size-limit", "256M",
            "--dir-cache-time", "1m",
            "--rc", "--rc-addr", "127.0.0.1:5572", "--rc-no-auth",
            "--chunker-fail-hard",
            "--log-level", "INFO",
            "--log-file", $"\"{Path.Combine(Root, "logs", "rclone-webdav.log")}\"",
        });
    }

    /// <summary>重启 rclone serve（配置变更后调用）。先按 pid 文件停止，再拉起。</summary>
    public void RestartServe()
    {
        LoadCredentials();
        var pidFile = Path.Combine(Root, "run", "rclone-serve.pid");
        if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile), out var pid))
        {
            try { Process.GetProcessById(pid)?.Kill(); } catch { }
            try { for (var i = 0; i < 20 && IsPortOpen(WebDavPort); i++) Thread.Sleep(250); } catch { }
        }
        if (IsPortOpen(WebDavPort))
            throw new InvalidOperationException("旧的播放服务未释放端口，请稍后重试或手动结束该进程。");

        var servePid = StartHidden(BuildServeCmd(), Root);
        WritePid("rclone-serve.pid", servePid);
        WaitPort(WebDavPort, 30, "播放服务");
    }

    /// <summary>重启 openlist（端口变更后调用）。</summary>
    public void RestartOpenList()
    {
        var pidFile = Path.Combine(Root, "run", "openlist.pid");
        if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile), out var pid))
        {
            try { Process.GetProcessById(pid)?.Kill(); } catch { }
            try { for (var i = 0; i < 40 && IsPortOpen(OpenListPort); i++) Thread.Sleep(250); } catch { }
        }
        var openlist = Path.Combine(Root, "bin", "openlist.exe");
        var op = StartHidden($"\"{openlist}\" server --data \"{Path.Combine(Root, "openlist-data")}\"", Root);
        WritePid("openlist.pid", op);
        WaitPort(OpenListPort, 40, "网盘服务");
    }

    private void LoadCredentials()
    {
        var file = Path.Combine(Root, "keys", "webdav-credentials.txt");
        if (!File.Exists(file))
            throw new InvalidOperationException(
                $"未找到播放凭据 {file}。请先运行 setup-local.ps1 完成部署。");
        foreach (var line in File.ReadAllLines(file))
        {
            if (line.StartsWith("WEBDAV_USER=", StringComparison.Ordinal)) User = line["WEBDAV_USER=".Length..].Trim();
            if (line.StartsWith("WEBDAV_PASS=", StringComparison.Ordinal)) Pass = line["WEBDAV_PASS=".Length..].Trim();
        }
        if (User.Length == 0 || Pass.Length == 0)
            throw new InvalidOperationException($"凭据文件格式异常：{file}");
    }

    public static bool IsPortOpen(int port)
    {
        try
        {
            using var c = new TcpClient();
            var iar = c.BeginConnect("127.0.0.1", port, null, null);
            var ok = iar.AsyncWaitHandle.WaitOne(800);
            if (ok && c.Connected) { c.EndConnect(iar); return true; }
            return false;
        }
        catch { return false; }
    }

    private static void WaitPort(int port, int seconds, string name)
    {
        for (var i = 0; i < seconds; i++)
        {
            if (IsPortOpen(port)) return;
            Thread.Sleep(1000);
        }
        throw new InvalidOperationException($"{name} 未在端口 {port} 上监听（已等待 {seconds} 秒）。日志目录：{Path.Combine(Root, "logs")}");
    }

    private static void WritePid(string name, int pid)
    {
        try
        {
            var dir = Path.Combine(Root, "run");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name), pid.ToString());
        }
        catch { /* pid 文件只是辅助，失败不影响运行 */ }
    }

    /// <summary>WMI Win32_Process.Create：拉起脱离调用方进程树的隐藏后台进程。</summary>
    private static int StartHidden(string commandLine, string workDir)
    {
        using var startupClass = new ManagementClass("Win32_ProcessStartup");
        using var startup = startupClass.CreateInstance();
        startup["ShowWindow"] = 0; // SW_HIDE

        using var processClass = new ManagementClass("Win32_Process");
        using var inParams = processClass.GetMethodParameters("Create");
        inParams["CommandLine"] = commandLine;
        inParams["CurrentDirectory"] = workDir;
        inParams["ProcessStartupInformation"] = startup;

        using var outParams = processClass.InvokeMethod("Create", inParams, null);
        var ret = (uint)outParams["ReturnValue"];
        if (ret != 0)
            throw new InvalidOperationException($"启动失败（返回值 {ret}）：{commandLine}");
        return (int)(uint)outParams["ProcessId"];
    }
}
