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
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    public static void ApplyDarkTitleBar(Window window)
    {
        // EnsureHandle：在窗口真正显示之前就拿到句柄，保证首次绘制前已切浅色
        var handle = new WindowInteropHelper(window).EnsureHandle();
        if (handle == IntPtr.Zero) return;
        int enabled = 0;
        if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeOld, ref enabled, sizeof(int));
        // 明确指定标题栏与文字颜色，避免跟随系统深色模式先黑后白
        int caption = 0x00FFFFFF; // COLORREF: 0x00BBGGRR，白色
        int text = 0x00000000;    // 黑色
        int border = 0x00DDDDDD;  // 浅灰边框
        DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref caption, sizeof(int));
        DwmSetWindowAttribute(handle, DwmwaTextColor, ref text, sizeof(int));
        DwmSetWindowAttribute(handle, DwmwaBorderColor, ref border, sizeof(int));
    }
}
