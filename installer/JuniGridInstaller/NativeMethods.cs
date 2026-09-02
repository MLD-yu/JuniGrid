using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace JuniGridInstaller;

internal static class NativeMethods
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Win11 上给无边框窗口加系统圆角；Win10 不支持则静默忽略。</summary>
    public static void TryRoundCorners(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            int pref = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref pref, sizeof(int));
        }
        catch
        {
            // 旧系统没有该 API，直角即可
        }
    }
}
