using System.Diagnostics;
using System.Runtime.InteropServices;

namespace JuniGrid.Services;

/// <summary>
/// v0.2.1：内存管理 —— 系统/自身/WebView2/游戏进程内存快照、工作集压缩（自身+游戏）、
/// 定时与阈值自动压缩、系统级待机内存页释放（需管理员，经提权子实例执行）。
/// 后台循环常驻（随 DI 解析启动）：每 5s 推一次快照并检查自动压缩条件，
/// 不依赖设置页是否打开。OnSnapshot 可能在后台线程触发，UI 订阅方自行调度。
/// </summary>
public sealed class MemoryService
{
    private readonly ConfigService _cfg;
    public MemoryService(ConfigService cfg)
    {
        _cfg = cfg;
        _ = LoopAsync();
    }

    public event Action<MemorySnapshot>? OnSnapshot;

    /// <summary>内存快照。各进程数字均为【私有工作集】口径，与任务管理器「内存(活动)」一致。</summary>
    public sealed record MemorySnapshot(
        long SysTotalBytes,
        long SysAvailBytes,
        long AppWorkingSet,
        long AppGcBytes,
        long WebViewWorkingSet,
        int WebViewProcCount,
        long GameWorkingSet,
        bool GameRunning)
    {
        public double SysPercent => SysTotalBytes <= 0
            ? 0
            : (SysTotalBytes - SysAvailBytes) * 100.0 / SysTotalBytes;
    }

    public MemorySnapshot GetSnapshot() => Build();

    private DateTime _lastTrimUtc = DateTime.UtcNow;   // 启动后先观察一个周期，不立刻触发
    private readonly object _trimGate = new();

    private async Task LoopAsync()
    {
        while (true)
        {
            try
            {
                var snap = Build();
                OnSnapshot?.Invoke(snap);

                var c = _cfg.Current;
                var now = DateTime.UtcNow;
                var sinceTrim = now - _lastTrimUtc;
                var cooldown = TimeSpan.FromMinutes(Math.Max(5, c.MemTimerMinutes));
                var thresholdHit = c.MemThresholdEnabled && snap.SysPercent >= Math.Max(50, c.MemThresholdPercent)
                                   && sinceTrim >= TimeSpan.FromMinutes(2);   // 阈值触发至少间隔 2 分钟，避免连续狂压
                var timerHit = c.MemTimerEnabled && sinceTrim >= cooldown;
                if (thresholdHit || timerHit)
                {
                    lock (_trimGate)
                    {
                        // 双检：拿锁后时间已满足才压，杜绝并发双触发
                        if (now - _lastTrimUtc >= (thresholdHit ? TimeSpan.FromMinutes(2) : cooldown))
                        {
                            _lastTrimUtc = now;
                            var (b, a) = CompressSelf();
                            AppLog.Warn("Memory",
                                $"自动压缩：{ResumableDownload.FormatBytes(b)} → {ResumableDownload.FormatBytes(a)}" +
                                $"（{(thresholdHit ? "阈值" : "定时")}触发，系统 {snap.SysPercent:F0}%）");
                        }
                    }
                }
            }
            catch { /* 快照/压缩的任何异常都不允许带崩循环 */ }
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
    }

    /// <summary>压缩自身：全代阻塞压缩 GC + 工作集整体换出。返回 (压缩前, 压缩后) 工作集。</summary>
    public (long before, long after) CompressSelf()
    {
        var before = Environment.WorkingSet;
        GC.WaitForPendingFinalizers();
        for (var gen = 2; gen >= 0; gen--)
            GC.Collect(gen, GCCollectionMode.Forced, blocking: true, compacting: true);
        TrimWorkingSet();
        var after = Environment.WorkingSet;
        return (before, after);
    }

    /// <summary>只把自身工作集换出（不做 GC，无暂停感）。托管堆本来就小，工作集大头是
    /// 运行时/JIT/框架映像 —— 换出后系统按需自动换回。用于启动完成后与最小化时的温和瘦身。</summary>
    public static void TrimWorkingSet() =>
        SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));

    /// <summary>清理游戏进程工作集（SMAPI 与 Steam 两种模式都在探测范围内）。返回处理到的进程数。</summary>
    public int TrimGameWorkingSet()
    {
        var n = 0;
        foreach (var name in new[] { "StardewModdingAPI", "Stardew Valley" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    try { if (EmptyWorkingSet(p.Handle)) n++; } catch { /* 已退出/权限不足 */ }
                }
            }
        }
        return n;
    }

    /// <summary>
    /// 压缩 WebView2 子进程工作集。渲染进程是独立沙箱，进不去做 GC；
    /// 但 EmptyWorkingSet 可以把它们的工作集整体换出（系统按需自动换回，
    /// UI 不受影响，回来后首次滚动/交互可能略顿）。返回 (进程数, 压缩前后合计工作集)。
    /// </summary>
    public (int count, long before, long after) TrimWebView2()
    {
        var byPid = ProcTable();
        long before = 0, after = 0;
        var n = 0;
        foreach (var r in byPid.Values)
        {
            if (!r.ImageName.Equals("msedgewebview2.exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (!InTreeOf(byPid, r.Pid)) continue;
            try
            {
                using var p = Process.GetProcessById((int)r.Pid);
                before += p.WorkingSet64;
                if (EmptyWorkingSet(p.Handle)) n++;
                p.Refresh();
                after += p.WorkingSet64;
            }
            catch { /* 已退出/权限不足 */ }
        }
        return (n, before, after);
    }

    // ------------------------------------------------------------------
    // 快照
    // ------------------------------------------------------------------

    private sealed record ProcRow(long Pid, long ParentPid, long PrivateWs, string ImageName);

    private static MemorySnapshot Build()
    {
        long total = 0, avail = 0;
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref ms)) { total = (long)ms.ullTotalPhys; avail = (long)ms.ullAvailPhys; }

        var byPid = ProcTable();
        var mine = (long)Environment.ProcessId;
        long appWs = 0, wvWs = 0, gameWs = 0;
        var wvCount = 0;
        var gameRunning = false;

        foreach (var r in byPid.Values)
        {
            var name = r.ImageName;
            if (name.Equals("msedgewebview2.exe", StringComparison.OrdinalIgnoreCase))
            {
                if (InTreeOf(byPid, r.Pid)) { wvWs += r.PrivateWs; wvCount++; }
            }
            else if (name.Equals("stardewmoddingapi.exe", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("stardew valley.exe", StringComparison.OrdinalIgnoreCase))
            {
                gameWs += r.PrivateWs;
                gameRunning = true;
            }
            else if (r.Pid == mine)
            {
                appWs = r.PrivateWs;
            }
        }

        return new MemorySnapshot(total, avail, appWs, GC.GetTotalMemory(forceFullCollection: false),
            wvWs, wvCount, gameWs, gameRunning);
    }

    /// <summary>pid 是否挂在【本进程】之下（沿父进程链向上找，链条可穿过其它 webview 进程）。</summary>
    private static bool InTreeOf(Dictionary<long, ProcRow> byPid, long pid)
    {
        var mine = (long)Environment.ProcessId;
        var cur = pid;
        for (var hop = 0; hop < 16 && byPid.TryGetValue(cur, out var row); hop++)
        {
            if (row.ParentPid == mine) return true;
            cur = row.ParentPid;
        }
        return false;
    }

    /// <summary>
    /// 全量进程表（pid → 父pid/私有工作集/映像名），一次 NtQuerySystemInformation 拿齐。
    /// 统一口径用【私有工作集】—— 与任务管理器「内存(活动)」同列。
    /// 此前用完整工作集（WorkingSet64）会把六个 Chromium 进程共享的映像各算一遍，
    /// 才出现卡片 425MB / 任务管理器 145MB 的差距。
    /// </summary>
    private static Dictionary<long, ProcRow> ProcTable()
    {
        var byPid = new Dictionary<long, ProcRow>(96);
        var len = 0x40000;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buf = Marshal.AllocHGlobal(len);
            try
            {
                var status = NtQuerySystemInformation(SystemProcessInformation, buf, len, out var needed);
                if (status != 0)
                {
                    // 只有「缓冲区不够大」值得扩容重试，其余错误直接放弃
                    if (status != unchecked((int)0xC0000004)) break;
                    len = needed > len ? needed + 65536 : len * 2;
                    continue;
                }

                var cur = buf;
                while (true)
                {
                    // x64 SYSTEM_PROCESS_INFORMATION 固定字段偏移（自 Vista 起稳定）：
                    // +8 WorkingSetPrivateSize（私有工作集，字节）
                    // +56 UNICODE_STRING.Length / +64 .Buffer（映像名）
                    // +80 UniqueProcessId / +88 InheritedFromUniqueProcessId
                    var pid = Marshal.ReadInt64(cur, 80);
                    var parent = Marshal.ReadInt64(cur, 88);
                    var privateWs = Marshal.ReadInt64(cur, 8);
                    var nameLen = Marshal.ReadInt16(cur, 56);
                    var nameBuf = Marshal.ReadInt64(cur, 64);
                    var name = nameLen > 0 && nameBuf != 0
                        ? Marshal.PtrToStringUni(new IntPtr(nameBuf), nameLen / 2) ?? ""
                        : "";
                    byPid[pid] = new ProcRow(pid, parent, privateWs, name);

                    var next = Marshal.ReadInt32(cur, 0);   // NextEntryOffset，0 = 最后一条
                    if (next <= 0 || next > 0x100000) break; // 上限防脏数据死循环
                    cur = IntPtr.Add(cur, next);
                }
                return byPid;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        return byPid;
    }

    private const int SystemProcessInformation = 5;

    // ------------------------------------------------------------------
    // Win32
    // ------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr process);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int infoClass, IntPtr buffer, int length, out int returnLength);
}
