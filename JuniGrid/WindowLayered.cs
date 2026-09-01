using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;

namespace JuniGrid;

/// <summary>
/// 用 Win32 WS_EX_LAYERED + SetLayeredWindowAttributes 做 DWM 合成器层的透明控制。
///
/// 为什么不用 WPF 的 Window.Opacity：
/// 当 WindowStyle=None + AllowsTransparency=False + WindowChrome 组合时，
/// WPF Opacity 属性对 DWM 合成不生效一小段时间（走 GDI 兜底路径），
/// 表现为 Show()/最小化恢复瞬间闪一帧黑框（就是 v0.20 里图一那个鬼影）。
///
/// WS_EX_LAYERED 是 DWM 合成器层的属性，一旦置位，窗口的每一帧都由
/// DWM 按 alpha 值合成，物理上不可能露出未绘制的底色。
/// </summary>
internal static class WindowLayered
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const uint LWA_ALPHA = 0x00000002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    /// <summary>把窗口设为 layered 并置 alpha=0（完全透明）。必须在窗口 HWND 已建立后调用。</summary>
    public static void MakeLayeredInvisible(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) hwnd = new WindowInteropHelper(w).EnsureHandle();
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_LAYERED);
        SetLayeredWindowAttributes(hwnd, 0, 0, LWA_ALPHA);
    }

    /// <summary>直接把 alpha 设为某个值（0..255）。</summary>
    public static void SetAlpha(Window w, byte alpha)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
    }

    /// <summary>把 alpha 用 DoubleAnimation 平滑动画到 255（不透明）。</summary>
    public static void FadeInToOpaque(Window w, int durationMs = 260)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;

        // 用 DispatcherTimer 手动步进 alpha —— DoubleAnimation 绑不到 Win32 属性
        var start = DateTime.UtcNow;
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)   // ~60fps
        };
        timer.Tick += (_, _) =>
        {
            var t = (DateTime.UtcNow - start).TotalMilliseconds / durationMs;
            if (t >= 1) { SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA); timer.Stop(); RemoveLayered(w); return; }
            // CubicEase EaseOut：1 - (1-t)^3
            var eased = 1 - Math.Pow(1 - t, 3);
            byte a = (byte)Math.Clamp(Math.Round(eased * 255), 0, 255);
            SetLayeredWindowAttributes(hwnd, 0, a, LWA_ALPHA);
        };
        timer.Start();
    }

    /// <summary>动画完成后移除 WS_EX_LAYERED —— 不移除会长期走 DWM 逐帧合成，浪费 GPU。</summary>
    private static void RemoveLayered(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex & ~WS_EX_LAYERED);
    }
}
