using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace JuniGridInstaller;

internal static class NativeMethods
{
    private const int SW_RESTORE = 9;
    private const uint FLASHW_CAPTION = 1;
    private const uint FLASHW_TRAY = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out bool pvAttribute, int cbAttribute);

    private const uint GW_OWNER = 4;
    private const int DWMWA_CLOAKED = 14;

    /// <summary>窗口有没有真的在屏幕上给用户看：可见、且没被 DWM 披走（虚拟桌面/远程会话里
    /// 的窗口会 IsWindowVisible=True 却根本看不见，2026-10-17 实测 SessionId≠1 那类）。
    /// 拿它当判据，宁可走弹窗也不能把用户领到一个看不见的窗口上。</summary>
    private static bool IsActuallyShown(IntPtr hWnd)
    {
        if (!IsWindowVisible(hWnd)) return false;
        try
        {
            if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out var cloaked, sizeof(bool)) == 0 && cloaked)
                return false;
        }
        catch { /* 拿不到就退回 IsWindowVisible */ }
        return true;
    }

    /// <summary>pid 那个进程里「标题逐字等于安装窗口标题」的顶层窗口：无 owner，
    /// 要么正在显示要么被最小化。判据收紧到这个程度，是因为 IME/TSF 那类帧窗口
    /// （标题也非空、也是顶层无 owner）会被当成安装窗口，选中它们＝又一次静默。</summary>
    public static IntPtr FindInstallerWindowOfProcess(int pid, string title)
    {
        var hit = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var owner);
            if (owner != (uint)pid) return true;
            if (GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;         // 有 owner 的是子/属主窗口
            var len = GetWindowTextLength(h);
            if (len <= 0 || len != title.Length) return true;               // 先比长度，少一次取串
            var sb = new System.Text.StringBuilder(len + 2);
            if (GetWindowText(h, sb, sb.Capacity) <= 0) return true;
            if (sb.ToString() != title) return true;
            if (!IsActuallyShown(h) && !IsIconic(h)) return true;            // 既没在屏上也没最小化 ⇒ 不是它
            hit = h;
            return false;
        }, IntPtr.Zero);
        return hit;
    }

    /// <summary>窗口有没有摆到用户面前：没被最小化（前台是不是它，只用来写日志）。</summary>
    public static bool IsOnScreen(IntPtr hWnd)
        => hWnd != IntPtr.Zero && !IsIconic(hWnd);

    public static bool IsForeground(IntPtr hWnd)
        => hWnd != IntPtr.Zero && GetForegroundWindow() == hWnd;

    /// <summary>把别的进程的窗口还原并带到前面。后台进程抢不到前台是常态，
    /// 那就退回闪任务栏图标 —— 至少用户看得见「那儿有一个」。</summary>
    public static void RestoreAndRaise(IntPtr hWnd)
    {
        ShowWindowAsync(hWnd, SW_RESTORE);
        SwitchToThisWindow(hWnd, true);
        if (GetForegroundWindow() != hWnd)
        {
            var info = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                hwnd = hWnd,
                dwFlags = FLASHW_CAPTION | FLASHW_TRAY,
                uCount = 1,
                dwTimeout = 0,
            };
            FlashWindowEx(ref info);
        }
    }

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
