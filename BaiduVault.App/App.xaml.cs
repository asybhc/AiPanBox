using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace BaiduVault.App;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\AiPanBox.SingleInstance";
    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    private static Mutex? _instanceMutex;

    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BaiduVault.App");

    protected override void OnStartup(StartupEventArgs e)
    {
        // 单实例：已有实例在运行时，直接把它的窗口叫到前台并退出本次启动。
        _instanceMutex = new Mutex(true, InstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            ActivateExistingInstance();
            Shutdown();
            return;
        }
        // 诊断开关：某些多显卡/虚拟显示器环境下 D3D 路径失效会让 WPF 窗口全白，
        // 设置 BAIDUVAULT_SOFTWARE_RENDER=1 或 settings.json 的 SoftwareRendering=true 可强制软件渲染。
        if (Environment.GetEnvironmentVariable("BAIDUVAULT_SOFTWARE_RENDER") == "1" || AppSettings.Current.SoftwareRendering)
        {
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        }
        // 浅色主题：整体白底黑字，启动时即使先出一帧也不会有明暗跳变
        ModernWpf.ThemeManager.Current.ApplicationTheme = ModernWpf.ApplicationTheme.Light;
        // 所有窗口（含后续新建）加载时统一把系统标题栏切成浅色
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
        try
        {
            LibVlcBootstrapper.Prepare();
        }
        catch (Exception ex)
        {
            try
            {
                Directory.CreateDirectory(LogDir);
                File.WriteAllText(Path.Combine(LogDir, "libvlc.log"), DateTime.Now.ToString("o") + Environment.NewLine + ex);
            }
            catch { }
        }
        base.OnStartup(e);
    }

    private static void ActivateExistingInstance()
    {
        try
        {
            var self = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(self.ProcessName))
            {
                if (p.Id == self.Id) continue;
                var h = p.MainWindowHandle;
                if (h == IntPtr.Zero) continue;
                ShowWindow(h, SwRestore);
                BringWindowToTop(h);
                SetForegroundWindow(h);
                return;
            }
        }
        catch { /* 激活失败不影响本进程退出 */ }
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

        try { _instanceMutex?.Dispose(); } catch { }
        base.OnExit(e);
    }
}
