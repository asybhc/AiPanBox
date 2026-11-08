using System.IO;
using System.IO.Compression;
using System.Reflection;
using LibVLCSharp.Shared;

namespace BaiduVault.App;

/// <summary>
/// 单文件版启动时把内置的 libvlc（win-x64）释放到本地目录，再按该目录初始化 VLC。
/// 普通目录版直接使用 exe 旁边的 libvlc，无需释放。
/// </summary>
internal static class LibVlcBootstrapper
{
    private static bool _ready;

    public static void Prepare()
    {
        if (_ready) return;

        // 目录版：libvlc 就在 exe 旁边，直接用
        var beside = Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64");
        if (File.Exists(Path.Combine(beside, "libvlc.dll")))
        {
            Core.Initialize(beside);
            _ready = true;
            return;
        }

        // 单文件版：首次运行释放到本地目录
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var archDir = Path.Combine(local, "BaiduVault", "libvlc", "win-x64");
        var dll = Path.Combine(archDir, "libvlc.dll");

        if (!File.Exists(dll))
        {
            Extract(archDir);
        }

        Core.Initialize(archDir);
        _ready = true;
    }

    private static void Extract(string archDir)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("libvlc.zip")
                           ?? throw new InvalidOperationException("缺少内置 libvlc 资源");
        Directory.CreateDirectory(archDir);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(archDir, entry.FullName));
            if (!target.StartsWith(Path.GetFullPath(archDir), StringComparison.OrdinalIgnoreCase)) continue;
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
}
