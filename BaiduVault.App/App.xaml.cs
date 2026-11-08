using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace BaiduVault.App;

public partial class App : Application
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BaiduVault.App");

    protected override void OnStartup(StartupEventArgs e)
    {
        // 诊断开关：某些多显卡/虚拟显示器环境下 D3D 路径失效会让 WPF 窗口全白，
        // 设置 BAIDUVAULT_SOFTWARE_RENDER=1 或 settings.json 的 SoftwareRendering=true 可强制软件渲染。
        if (Environment.GetEnvironmentVariable("BAIDUVAULT_SOFTWARE_RENDER") == "1" || AppSettings.Current.SoftwareRendering)
        {
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        }
        // ModernWpfUI 深色主题：控件外观由库统一提供，跟随系统配色
        ModernWpf.ThemeManager.Current.ApplicationTheme = ModernWpf.ApplicationTheme.Dark;
        // 所有窗口（含后续新建）加载时统一把系统标题栏切成深色
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window window) WindowTheme.ApplyDarkTitleBar(window);
            }));
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                Directory.CreateDirectory(LogDir);
                File.WriteAllText(Path.Combine(LogDir, "crash.log"),
                    DateTime.Now.ToString("o") + Environment.NewLine + args.Exception.ToString());
            }
            catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try
            {
                Directory.CreateDirectory(LogDir);
                File.WriteAllText(Path.Combine(LogDir, "crash.log"),
                    DateTime.Now.ToString("o") + Environment.NewLine + args.ExceptionObject?.ToString());
            }
            catch { }
        };
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (AppSettings.Current.ClearCacheOnExit)
            {
                // VFS 缓存中 cache\vfs\vault\ 是解密后的明文（已实测：与原文件 SHA256 一致）。
                // 退出时清空该目录，避免明文残留。
                var vaultCache = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BaiduVault", "cache", "vfs", "vault");
                if (Directory.Exists(vaultCache))
                {
                    foreach (var f in Directory.EnumerateFiles(vaultCache, "*", SearchOption.AllDirectories))
                    {
                        try { File.Delete(f); } catch { /* 占用的文件跳过 */ }
                    }
                    foreach (var d in Directory.EnumerateDirectories(vaultCache, "*", SearchOption.AllDirectories)
                                 .OrderByDescending(p => p.Length))
                    {
                        try { Directory.Delete(d, true); } catch { }
                    }
                }
            }
        }
        catch { /* 清理失败不阻塞退出 */ }

        base.OnExit(e);
    }
}
