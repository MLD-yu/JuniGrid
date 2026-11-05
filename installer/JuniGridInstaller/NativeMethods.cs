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

    // ---- 进程父链（只读，用 Toolhelp 快照；不需要提权） ----

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>这些进程里有没有哪个叫 <paramref name="names"/> 的祖先进程。
    /// 用来回答"游戏是不是我们拉起来的"——整树杀只会碰到后代，所以判据必须是父链，
    /// 不能是"游戏在不在跑"（Steam 拉起的游戏在跑也不该拦安装）。</summary>
    public static bool AnyNamedProcessDescendsFrom(IReadOnlyCollection<int> ancestorPids, params string[] names)
    {
        if (ancestorPids.Count == 0) return false;
        var parents = new Dictionary<int, int>();
        var match = new List<int>();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return false;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>(), szExeFile = "" };
            if (!Process32FirstW(snap, ref e)) return false;
            do
            {
                var pid = (int)e.th32ProcessID;
                parents[pid] = (int)e.th32ParentProcessID;
                if (names.Any(n => n.Equals(e.szExeFile, StringComparison.OrdinalIgnoreCase)))
                    match.Add(pid);
            }
            while (Process32NextW(snap, ref e));
        }
        finally { CloseHandle(snap); }

        foreach (var pid in match)
        {
            var cur = pid;
            // 父链最多走 64 层，防止数据异常时死循环（也不存在这么深的链）
            for (var depth = 0; depth < 64 && parents.TryGetValue(cur, out var parent) && parent > 0; depth++)
            {
                if (ancestorPids.Contains(parent)) return true;
                cur = parent;
            }
        }
        return false;
    }

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
