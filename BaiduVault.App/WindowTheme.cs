using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BaiduVault.App;

/// <summary>
/// 让系统标题栏跟随深色主题（Win10 1809+ / Win11）。
/// ModernWpfUI 只负责客户区控件样式，标题栏需要 DWM 属性单独设置。
/// </summary>
internal static class WindowTheme
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeOld = 19;

    public static void ApplyDarkTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        int enabled = 1;
        if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeOld, ref enabled, sizeof(int));
    }
}
