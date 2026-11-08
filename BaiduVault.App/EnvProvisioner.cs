using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace BaiduVault.App;

/// <summary>
/// 首次运行自动补齐运行环境：内置的 rclone / openlist 解压到 %LOCALAPPDATA%\BaiduVault，
/// 再跑一遍部署脚本生成密钥、配置与本地服务。已是完整环境时什么也不做。
/// </summary>
internal static class EnvProvisioner
{
    private static readonly string Root =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BaiduVault");

    public static bool NeedsProvisioning()
    {
        var bin = Path.Combine(Root, "bin");
        return !File.Exists(Path.Combine(bin, "rclone.exe"))
               || !File.Exists(Path.Combine(bin, "openlist.exe"))
               || !File.Exists(Path.Combine(Root, "rclone.conf"))
               || !File.Exists(Path.Combine(Root, "keys", "webdav-credentials.txt"));
    }

    public static void Ensure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Path.Combine(Root, "logs"));

        var bin = Path.Combine(Root, "bin");
        var script = Path.Combine(Root, "setup-local.ps1");

        if (!File.Exists(Path.Combine(bin, "rclone.exe"))
            || !File.Exists(Path.Combine(bin, "openlist.exe"))
            || !File.Exists(script))
        {
            ExtractEnv();
        }

        if (File.Exists(Path.Combine(Root, "rclone.conf"))
            && File.Exists(Path.Combine(Root, "keys", "webdav-credentials.txt"))
            && File.Exists(Path.Combine(bin, "rclone.exe"))
            && File.Exists(Path.Combine(bin, "openlist.exe")))
        {
            return;
        }

        RunSetup(script);
    }

    private static void ExtractEnv()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("env.zip")
                           ?? throw new InvalidOperationException("缺少内置运行环境资源");
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(Root, entry.FullName));
            if (!target.StartsWith(Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = File.Create(target);
            input.CopyTo(output);
        }
    }

    private static void RunSetup(string script)
    {
        var log = Path.Combine(Root, "logs", "auto-setup.log");
        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -DemoLibraries demo")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException("无法启动环境安装进程");
        using var writer = new StreamWriter(log, false);
        var reader = process.StandardOutput.BaseStream;
        var buffer = new byte[4096];
        var deadline = DateTime.Now.AddMinutes(4);
        int n;
        while ((n = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            writer.Write(System.Text.Encoding.UTF8.GetString(buffer, 0, n));
            writer.Flush();
            if (DateTime.Now > deadline) break;
        }
        process.WaitForExit(60_000);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"运行环境安装失败（退出码 {process.ExitCode}）。详情见：{log}");
        }
    }
}
