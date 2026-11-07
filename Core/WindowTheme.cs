using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace NetBoost.Core;

/// <summary>
/// 把系统标题栏染成与应用一致的深色，避免白色标题栏和深色界面割裂。
/// 依赖 DWM 属性：Win11 支持完整配色，Win10 1903+ 至少支持深色模式。
/// </summary>
public static class WindowTheme
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeLegacy = 19;
    private const int DwmBorderColor = 34;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void ApplyDarkCaption(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        Set(handle, DwmUseImmersiveDarkMode, 1);
        Set(handle, DwmUseImmersiveDarkModeLegacy, 1);

        // 与应用背景一致的标题栏，达到「一体化」效果
        Set(handle, DwmCaptionColor, ToColorRef(0x0B, 0x0E, 0x14));
        Set(handle, DwmTextColor, ToColorRef(0xF2, 0xF5, 0xFC));
        Set(handle, DwmBorderColor, ToColorRef(0x0B, 0x0E, 0x14));
    }

    /// <summary>COLORREF 是 0x00BBGGRR 布局，注意与 RGB 相反。</summary>
    private static int ToColorRef(int r, int g, int b) => (b << 16) | (g << 8) | r;

    private static void Set(IntPtr hwnd, int attribute, int value)
    {
        try
        {
            var raw = value;
            DwmSetWindowAttribute(hwnd, attribute, ref raw, sizeof(int));
        }
        catch
        {
            // 旧系统不支持该属性，忽略即可
        }
    }
}
