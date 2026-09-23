using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace JuniGrid.Services;

/// <summary>
/// v1.4.3：历史版本下载 —— 用 DepotDownloader 按 Manifest ID 拉历史包，
/// 再落到游戏目录（保留 Mods/）。官方分支走 Steam 客户端；这里补官方分支之外的旧版。
/// 工具本体装在程序安装目录 tools\DepotDownloader（插件内容，不进缓存/LocalAppData）。
/// </summary>
public sealed class DepotDownloaderService
{
    // 设置页会在后台预热授权组件；合并预热与用户点击，避免重复下载/解压。
    private readonly SemaphoreSlim _depotEnsureGate = new(1, 1);

    /// <summary>
    /// 同一个 Steam 账号允许最多 3 路并发会话。
    /// ⚠ 更正（2026-09-21 19:1x 实测）：先前"两路必互踢"的结论**是错的** —— 那时两个 DD 实例
    /// 用的是同一个默认 LogonID，Steam 把它们当成同一个客户端，后登录的顶掉前一个
    /// （症状就是两边都报「Lost connection to Steam」）。给每路发一个唯一 -loginid 之后
    /// 重测：两路并行 2 分多钟，两边零掉线，其中一路落了 464 MB。
    /// 上限留 3 而不是放开：一是国内到内容服务器本来就时通时不通（日志里成片的
    /// 「Connection timeout downloading chunk」），多路只会互相抢同一条带宽；
    /// 二是扫码授权那条路还要留位置。
    /// </summary>
    private const int MaxSteamSessions = 3;
    private static readonly SemaphoreSlim SessionGate = new(MaxSteamSessions, MaxSteamSessions);
    private static int _sessionHeld;

    /// <summary>是否已有 Steam 会话在飞（正在下载，或正在等人扫码）。</summary>
    public static bool SteamSessionBusy => Volatile.Read(ref _sessionHeld) > 0;

    /// <summary>当前在飞的会话数（测试台与诊断用）。</summary>
    public static int SteamSessionCount => Volatile.Read(ref _sessionHeld);

    private static int _loginIdSeed = Random.Shared.Next(1000, 999_999) * 1000;

    /// <summary>DepotDownloader 的 -loginid：同时跑多个实例时必须各不相同，
    /// 否则 Steam 认为是同一个客户端重复登录，会把先起的那一路顶掉。</summary>
    public static int NextLoginId() => Interlocked.Increment(ref _loginIdSeed);

    /// <summary>占一路 Steam 会话，Dispose 时归还。拿不到就在这儿等。
    /// ⚠ 这把闸不可重入：调用链上**只允许最底层这一处拿**，上层要排队状态就从
    /// <see cref="Progress.Queued"/> 读，别自己再拿一遍 —— 拿两遍会自己等自己，
    /// 第一路永远卡死、后面全部跟着排不上（45 个版本一起 0% 那次就是这么来的）。</summary>
    public static async Task<IDisposable> HoldSteamSessionAsync(CancellationToken ct = default)
    {
        await SessionGate.WaitAsync(ct);
        Interlocked.Increment(ref _sessionHeld);
        return new SessionSlot();
    }

    private sealed class SessionSlot : IDisposable
    {
        public void Dispose()
        {
            Interlocked.Decrement(ref _sessionHeld);
            try { SessionGate.Release(); } catch (SemaphoreFullException) { }
        }
    }

    // P0-1：写游戏目录（ApplyStaging）全局互斥 —— 弹窗切换与后台下载完成后的 apply
    // 都会打同一 GamePath；无此锁时可并发清目录/拷本体/换 Mods，导致文件撕裂。
    private static readonly SemaphoreSlim ApplyGate = new(1, 1);
    private static int _applyActive;

    /// <summary>是否有版本 apply 正在写游戏目录（UI 用来禁用其它版本操作）。</summary>
    public static bool IsApplyInProgress => Volatile.Read(ref _applyActive) > 0;

    /// <summary>页面挂在游戏 Mods 上的 FileSystemWatcher 的持有者登记。
    /// 切换期间要处理两件事，缺一不可：
    /// ① 停事件 —— watcher 一收到事件就排 RescanAndRestate → 后台重扫 + CleanupDuplicateCopies
    ///    （会搬目录），等于同进程另一条线程在我们搬 Mods 的同时也在动 Mods —— 12:35 那次改名
    ///    重试 3 次全报「被另一进程使用」指的就是 Mods 下的 .junigrid_trash。
    /// ② 真释放句柄 —— ⚠ 只把 EnableRaisingEvents 设成 false **不会**关掉目录句柄。
    ///    旧注释在这里断言过「watcher 开着也能改名」，但 2026-09-23 真机日志连续三次切换
    ///    Mods 改名全败在 ACCESS_DENIED、且文件级占用探测恒为空（= 钉住它的是目录句柄不是文件），
    ///    与该断言直接矛盾。所以现在切换期间是 Dispose，切完由页面重建。
    ///    这样即便元凶另有其人，日志里那句「自家 Mods 监听已释放」也能把它排除掉 —— 否则永远判不出来。</summary>
    public sealed class ModWatcherSlot
    {
        /// <summary>页面自己 Dispose 掉 watcher 并置 null（释放目录句柄）。</summary>
        public required Action Release { get; init; }
        /// <summary>切换结束后页面重建 watcher。</summary>
        public required Action Reacquire { get; init; }
        /// <summary>此刻是否还持有活的 watcher 句柄（只为写日志取证，别拿它做逻辑判断）。</summary>
        public required Func<bool> IsLive { get; init; }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ModWatcherSlot, byte>
        ModWatchers = new();

    public static void RegisterModWatcher(ModWatcherSlot slot) => ModWatchers[slot] = 1;
    public static void UnregisterModWatcher(ModWatcherSlot slot) => ModWatchers.TryRemove(slot, out _);

    /// <summary>on=false：停事件并 Dispose 句柄；on=true：让页面重建。
    /// 保留这个名字是因为调用点成对出现在 try/finally 两端，语义就是「切换期间别让监听活着」。</summary>
    public static void SetModWatchersEnabled(bool on)
    {
        foreach (var slot in ModWatchers.Keys)
        {
            try { if (on) slot.Reacquire(); else slot.Release(); }
            catch (Exception ex) { AppLog.Warn("DepotDownloader", $"Mods 监听{(on ? "重建" : "释放")}失败: {ex.Message}"); }
        }
    }

    /// <summary>改名失败时写进日志的取证行：自家监听此刻是活着还是已释放。</summary>
    private static string WatcherState()
    {
        var slots = ModWatchers.Keys.ToList();
        if (slots.Count == 0) return "无登记（Mods 页没打开过或已销毁）";
        var live = slots.Count(s => { try { return s.IsLive(); } catch { return false; } });
        return live == 0 ? $"已释放（登记 {slots.Count} 个，活句柄 0）"
                         : $"⚠ 仍有 {live}/{slots.Count} 个活句柄未释放";
    }

    /// <summary>
    /// 阶段耗时打点。此前整条切换链路没有任何计时（两个服务里 grep 不到 Stopwatch），
    /// 「哪一步几秒」只能靠日志行时间戳相减倒推，而没有日志输出的阶段完全是黑箱 ——
    /// 实测一次切换约 25 秒，开头 7 秒一行日志都没有，无法归因。
    /// Lap 记录上一段耗时并重新起表；Report 在 finally 里调，抛异常时也能看出停在哪一段。
    /// </summary>
    private sealed class PhaseTimer
    {
        private readonly List<string> _parts = new();
        private readonly Stopwatch _total = Stopwatch.StartNew();
        private Stopwatch _lap = Stopwatch.StartNew();

        public void Lap(string name)
        {
            _parts.Add($"{name} {_lap.ElapsedMilliseconds}ms");
            _lap = Stopwatch.StartNew();
        }

        public void Report(string source, string head)
        {
            Lap("收尾");
            AppLog.Info(source, $"{head}总 {_total.ElapsedMilliseconds}ms —— " + string.Join(" | ", _parts));
        }
    }

    private static void ApplyStagingExclusive(string staging, string gamePath,
        IProgress<Progress>? progress, string? currentGameVersion)
    {
        var t = new PhaseTimer();
        ApplyGate.Wait();
        t.Lap("等闸门");
        Interlocked.Increment(ref _applyActive);
        PortraitSkinService.SuspendReads();   // 立绘扫描/预热是 Mods 目录的大读者，先让它别开新任务
        SetModWatchersEnabled(false);         // Mods 页的 FileSystemWatcher 是常驻句柄，不关它改名必败
        t.Lap("挂起读者与监听");
        try
        {
            // 兜底：先确认这份缓存有没有资格铺进游戏目录，再动手。
            // 原先锚点校验只跑在 ApplyStaging 之后 —— 那时 Mods 已清空、本体已重写，
            // 抛错等于把用户留在半切换状态（实测：同版本 apply 清掉 3 个 mod 之后才报「缓存不完整」）。
            CheckStagingAnchors(staging);
            t.Lap("锚点校验");
            EnsureDiskSpace(staging, gamePath);
            t.Lap("磁盘空间");
            // 有人在往 Mods 里复制 mod 时切版本，那批文件会被算成「源版本的 Mods」一起归档 ——
            // 必须趁游戏目录还没被碰之前拦下来。Task.Run 包一层：这里可能是 UI 线程，静默等待不能睡在 UI 上。
            Task.Run(() => EnsureModsQuiescent(gamePath, progress)).GetAwaiter().GetResult();
            t.Lap("等 Mods 停笔");
            // 已经起跑的那一轮扫描/预热收手之后再动目录：文件被自家打开着，Directory.Move
            // 就会失败并退化成整棵拷贝（实测一次切换 4.3 秒，两趟全走拷贝）。
            PortraitSkinService.WaitUntilIdle(800);   // 最多等 0.8 秒：等不到就照旧走拷贝
            t.Lap("等立绘收手");
            // 存档只有一个目录、所有版本共用，而它会被单向升级（见 SaveVersionService）——
            // 趁游戏目录还没被碰，先把上次收起来的档放回、再给「切过去就回不去」的档留底。
            PrepareSavesFor(staging, progress);
            t.Lap("存档准备");
            ApplyStaging(staging, gamePath, progress, currentGameVersion);
            t.Lap("应用本体与 Mods");
            // 应用完整性：缺关键 Content 时进档会在 NPC 构造里 NRE 闪退（1.0 实测）
            ValidateAppliedGame(gamePath, staging);
            t.Lap("应用后校验");
            // 记下"这个目录现在是被我们铺成哪个发行号的"：XNA 时代（1.0–1.4）的 exe
            // 自报 1.0.61xx，光读文件会把 1.2.x 认成游戏 1.0（判无适配 SMAPI、Mods 不归档）。
            UpdateService.RecordDeployedVersion(gamePath, DeployedLabelOf(staging), ReadManifestMeta(staging));
            t.Lap("记部署版本");
            // XNA 运行库补装原来只挂在 VersionDownloadService 的下载通道上，弹窗
            // 「用本地缓存切换」根本不经过那里 → 1.0–1.4 在新电脑上照样起不来。
            // 下沉到这里 = 所有 apply 路径（弹窗 / 下载 / 后台 / 测试台）统一覆盖。
            // 用 Task.Run 包一层：这里可能是 UI 线程，直接 .Result 会因 SynchronizationContext 死锁。
            Task.Run(() => EnsureXnaForAppliedGameAsync(gamePath, progress)).GetAwaiter().GetResult();
            t.Lap("XNA 补装");
        }
        finally
        {
            SetModWatchersEnabled(true);
            PortraitSkinService.ResumeReads();
            Interlocked.Decrement(ref _applyActive);
            try { ApplyGate.Release(); } catch (SemaphoreFullException) { }
            // 抛异常时也报：少了哪几段就说明停在哪一段，比只看一行错误文本好定位
            t.Report("DepotDownloader", $"版本切换阶段耗时（{Path.GetFileName(staging)}）：");
        }
    }

    /// <summary>
    /// 切换前后的存档两步：放回上次暂存的 → 给「会被新版本升上去」的留底。
    /// 切换前后的存档：放回上次收起来的 → 留底 → **把这个版本读不了的收起**（游戏列表只显示读得了的）。
    /// 整段尽力而为：存档这一步失败绝不拦下切换 —— 本体切过去比留底重要，失败只落日志。
    /// </summary>
    private static void PrepareSavesFor(string staging, IProgress<Progress>? progress)
    {
        void Log(string m) => AppLog.Warn("存档", m);
        try
        {
            var target = DeployedLabelOf(staging);
            // 差集放回：只放回这个版本读得了的。读不了的留在抽屉原地
            var back = SaveVersionService.RestoreHidden(Log, target);
            // 不做升级前留底：用高版本存盘后回不去，后果用户自担
            var hidden = SaveVersionService.HideUnreadable(target, staging, Log);
            // 不上屏：收起是常态动作、对玩家无操作价值，弹一行只是噪音（用户 2026-09-23 明确要求去掉）。
            // 每份的「已收起」和这条汇总都进日志，要查的人去日志看。
            if (hidden > 0)
                AppLog.Info("存档", $"本版读不了的 {hidden} 份存档已收起（仅日志，不上屏）");
            if (back > 0)
                progress?.Report(new Progress($"放回了之前收起来的 {back} 份存档", null));
        }
        catch (Exception ex)
        {
            AppLog.Warn("存档", "切换前的存档处理没做完（不影响本次切换）：" + ex.Message);
        }
    }

    /// <summary>
    /// 切换前确认 Mods 目录已经「停笔」。往 Mods 里复制/解压 mod 的半路切版本，那批文件会
    /// 被算成「源版本的 Mods」一起归档，或者在归档后才落盘 —— 落进「新版本」的 Mods 里。
    /// 实测两次同一事故：09:41 与 14:41 从 1.6.15 切 1.0，14:41 那次 Explorer 正在复制
    /// 13321 个文件，1.6.15 的 Mods 只搬走 18 项，剩下的全写进了 1.0 的空 Mods（1.0 无 SMAPI，
    /// 永不加载，下次切换还会被当成 1.0 的东西处理）。
    /// 所以先等它静默（最多约 15 秒），还在变就拒绝动手 —— 这时游戏目录一点没碰，用户把复制等完即可。
    /// </summary>
    private static void EnsureModsQuiescent(string gamePath, IProgress<Progress>? progress)
    {
        var mods = Path.Combine(gamePath, "Mods");
        if (!Directory.Exists(mods)) return;

        var last = ModsSnapshot(mods);
        for (var round = 0; round < 12; round++)
        {
            // ⚠ 这个间隔本身就是判定依据（"两拍之间没动静"= 没人写了），不是可以随手压的
            // 启动开销：一批批复制时（一次扔十个 mod 文件夹、中间手停半秒），0.4 秒正好会撞在
            // 两个文件夹之间的空档上 ⇒ 误判成"已经停了"，那批还没落完的文件就会被算进错的版本。
            Thread.Sleep(400);   // 两拍间隔是判定依据；从 1.2s 压到 0.4s，切换不再白等
            var now = ModsSnapshot(mods);
            if (now == last) return;          // 1.2 秒内毫无变化 → 认为已经写完
            if (round == 0)
                progress?.Report(new Progress("Mods 目录正在被写入，等复制结束后再切换…", null));
            last = now;
        }
        AppLog.Warn("DepotDownloader", "Mods 目录持续变化，已中止切换（避免把在途文件错记到别的版本）");
        throw new DepotException("Mods 目录一直在变化（像是正在复制或解压 mod）。" +
            "现在切换版本会把那批还没落完的文件错记到别的版本名下，切回来就找不到了。" +
            "请等复制完成（或取消它）后再切换。");
    }

    /// <summary>
    /// Mods 目录指纹：条目数 + 总字节 + 全树最新写入时间 + 顶层条目名。
    /// 写时间进指纹很关键：往已有 mod 文件夹里补文件不会新增顶层条目，只看名字会漏判「已停笔」。
    /// 只读元数据、不读内容；13k 文件量级一次约 0.2 秒，比等复制本身便宜得多。
    /// </summary>
    private static string ModsSnapshot(string mods)
    {
        try
        {
            long bytes = 0;
            var entries = 0;
            var newest = DateTime.MinValue;
            var top = new List<string>();
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            var rootFull = Path.GetFullPath(mods).TrimEnd(Path.DirectorySeparatorChar);
            foreach (var p in Directory.EnumerateFileSystemEntries(mods, "*", opts))
            {
                entries++;
                try
                {
                    if (Directory.Exists(p))
                    {
                        var di = new DirectoryInfo(p);
                        bytes += di.Name.Length;   // 目录无长度，只计结构变化
                        var parent = Path.GetDirectoryName(di.FullName);
                        if (parent is not null && string.Equals(
                                Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar),
                                rootFull, StringComparison.OrdinalIgnoreCase))
                            top.Add(di.Name);
                        if (di.LastWriteTime > newest) newest = di.LastWriteTime;
                    }
                    else
                    {
                        var fi = new FileInfo(p);
                        bytes += fi.Length;
                        if (fi.LastWriteTime > newest) newest = fi.LastWriteTime;
                    }
                }
                catch { }
            }
            top.Sort(StringComparer.OrdinalIgnoreCase);
            return $"{entries}:{bytes}:{newest:HHmmss.fff}:{string.Join(",", top)}";
        }
        catch { return ""; }   // 读不动（权限/占用）→ 当成「无变化」，不因此卡住正常切换
    }

    /// <summary>
    /// 切换收尾的「在途文件归位」：闸门与切换之间总有窗口（复制暂停 1.2 秒会被判成静默，
    /// 之后又恢复）。这里以「换进来时那份快照的顶层条目」为基线，把多出来的东西挪回
    /// 源版本的抽屉，而不是留在新版本名下。两遍（间隔 1.5 秒）覆盖恢复写入的下一批。
    /// </summary>
    private static void ReclaimInFlightMods(string gameMods, HashSet<string> allowed,
        string? sinkDir, string? sourceVersionLabel, IProgress<Progress>? progress)
    {
        if (sinkDir is null || !Directory.Exists(gameMods)) return;
        var total = 0;
        for (var pass = 0; pass < 2 && total < 400; pass++)
        {
            if (pass > 0) Thread.Sleep(1500);
            List<(string Name, string Path)> foreign;
            try
            {
                foreign = Directory.EnumerateFileSystemEntries(gameMods)
                    .Select(p => (Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar)), p))
                    .Where(e => !allowed.Contains(e.Item1))
                    .ToList();
            }
            catch { return; }
            if (foreign.Count == 0) break;

            foreach (var (name, path) in foreign)
            {
                var dest = Path.Combine(sinkDir, name);
                // TryRelocateDirectory 会先删掉已存在的 dest —— 抽屉里同名条目可能是
                // 这次复制的前半截，也可能是源版本原有的 mod，绝不能覆盖，改带后缀并存。
                if (Directory.Exists(dest) || File.Exists(dest))
                    dest = Path.Combine(sinkDir, name + "-inflight" + DateTime.Now.ToString("HHmmss"));
                try
                {
                    if (Directory.Exists(path))
                    {
                        if (MoveDirectoryVerified(path, dest, null, null, "归位") == DirMove.Failed)
                        {
                            // 没归位成功就别把它记成"已允许"，下一轮还会再试一次
                            AppLog.Warn("DepotDownloader", $"在途 mod「{name}」归位核对不通过，留在原地");
                            continue;
                        }
                    }
                    else if (File.Exists(path))
                    {
                        Directory.CreateDirectory(sinkDir);
                        if (SameVolume(path, dest)) Directory.Move(path, dest);
                        else { File.Copy(path, dest, true); File.Delete(path); }
                    }
                    allowed.Add(name);
                    total++;
                }
                catch (Exception ex)
                {
                    AppLog.Warn("DepotDownloader", $"在途 mod「{name}」归位失败: {ex.Message}");
                }
            }
        }
        if (total > 0)
        {
            AppLog.Warn("DepotDownloader",
                $"切换过程中有 {total} 项 mod 在复制队列里后到，已挪回 {sourceVersionLabel ?? "?"} 的抽屉：{sinkDir}");
            progress?.Report(new Progress(
                $"检测到 {total} 项 mod 是在切换瞬间才落盘的，已挪回 {sourceVersionLabel ?? "上一个"} 版本的 Mods 抽屉；" +
                "请确认那个复制任务已经结束后再重新切换。", null));
            try { WriteSizeCache(Path.GetDirectoryName(sinkDir)!); } catch { }
        }
    }


    private static async Task EnsureXnaForAppliedGameAsync(string gamePath, IProgress<Progress>? progress)
    {
        try
        {
            var ver = TryReadGameVersion(gamePath);
            if (!XnaRedistService.GameNeedsXna(ver) || XnaRedistService.IsInstalled()) return;
            progress?.Report(new Progress("这台机器缺 XNA 4.0 运行库（1.0–1.4 必需），正在安装…", null));
            var err = await XnaRedistService.EnsureInstalledAsync(
                (msg, pct) => progress?.Report(new Progress(msg, pct is null ? null : (int?)Math.Round(pct.Value))));
            if (err is not null)
            {
                progress?.Report(new Progress("XNA 运行库安装失败：" + err, null));
                AppLog.Warn("DepotDownloader", "补装 XNA 失败: " + err);
            }
        }
        catch (Exception ex) { AppLog.Warn("DepotDownloader", "补装 XNA 异常: " + ex.Message); }
    }

    /// <summary>动手前确认目标盘装得下：② 是「先删本体、再写本体」，空间不够就是删得掉
    /// 写不回，现场比不切换更糟。体积按写入时记录的 size cache 估，估不出就跳过
    /// （不因为算不出来而挡住正常切换），另留 256 MB 余量给临时文件。</summary>
    private static void EnsureDiskSpace(string staging, string gamePath)
    {
        long need = ReadSizeCache(staging);
        if (need <= 0) return;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(gamePath));
            if (string.IsNullOrEmpty(root)) return;
            var free = new DriveInfo(root).AvailableFreeSpace;
            if (free >= need + 256L * 1024 * 1024) return;
            throw new DepotException(
                $"{root} 剩余 {free / 1024 / 1024} MB，不够写入这版本体（约 {need / 1024 / 1024} MB + 256 MB 余量）。" +
                "已中止切换，游戏目录未被改动。请清理磁盘，或在设置里把缓存目录换到空间充足的盘后重试。");
        }
        catch (DepotException) { throw; }
        catch { /* 拿不到盘符等异常不阻塞正常切换 */ }
    }

    /// <summary>缓存锚点校验，动手前跑（也在校验末尾再跑一遍）。检查项按游戏版本区分 ——
    /// NPCDispositions.xnb 是 1.0–1.5 的数据；1.6+ 已重构 Content，官方包里没有该文件，
    /// 不能拿来判「缓存不完整」。</summary>
    private static void CheckStagingAnchors(string staging)
    {
        var is16Plus = IsGameVersion16OrNewer(TryReadGameVersion(staging));
        var anchors = new List<string>
        {
            "Stardew Valley.exe",
            Path.Combine("Content", "Characters", "Abigail.xnb"),
            Path.Combine("Content", "Portraits", "Abigail.xnb"),
            // 1.0–1.5：NPC 配置表；1.6+：用 Data 下 Achievements 代替（该文件两代都有/1.6 必有）
            Path.Combine("Content", "Data", is16Plus ? "Achievements.xnb" : "NPCDispositions.xnb"),
        };

        var cacheMiss = anchors.Where(r => !File.Exists(Path.Combine(staging, r))).ToList();
        if (cacheMiss.Count > 0)
            throw new DepotException(
                $"版本缓存不完整（{staging}），缺少：{string.Join("、", cacheMiss)}。" +
                "请在版本管理里删除该版本本地缓存后重新下载。");
    }

    /// <summary>应用后校验完整性，缺失就抛错，避免用户进档才闪退。两道：
    /// ① 锚点文件对着 staging 测 —— 残缺缓存铺进目录之后，②是拿 staging 当基准的、测不出来；
    /// ② staging↔游戏目录全量清单比对 —— 只抽查 Abigail 一个角色会漏掉其它 NPC 资源，
    /// 而任一 Characters/Portraits 缺失都会在 NPC 构造里 NRE（1.0 实测：新建能进、再进档闪退）。</summary>
    private static void ValidateAppliedGame(string gamePath, string staging)
    {
        // 只查锚点。全量清单要各扫一遍上万个小文件，是「直接应用」后半段卡顿主因。
        CheckStagingAnchors(staging);
        CheckStagingAnchors(gamePath);
    }

    /// <summary>本体文件清单（相对路径集合）。跳过口径与 CopyGameBodyParallel 的排除项一致，
    /// 免得把 Mods/SMAPI 的正常差异算成本体缺失。单个目录读失败只跳过该层，不整体放弃校验。</summary>
    private static HashSet<string> EnumerateBodyFiles(string root)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return result;
        var dirs = new Stack<string>();
        dirs.Push(root);
        while (dirs.Count > 0)
        {
            var dir = dirs.Pop();
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                    if (!IsNonBodyDir(Path.GetFileName(sub))) dirs.Push(sub);

                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    var name = Path.GetFileName(file);
                    // 与 CopyGameBodyParallel 的排除项保持一致：这些是我们自己写的元数据，
                    // 不铺进游戏目录，也就不能算成「本体缺失」（.junigrid-version 漏掉时
                    // 每次切换都会假报「切换后游戏目录缺少 1 个文件」）。
                    if (name.Equals(ManifestMetaName, StringComparison.OrdinalIgnoreCase)
                        || name.Equals(".junigrid-size", StringComparison.OrdinalIgnoreCase)
                        || name.Equals(".junigrid-version", StringComparison.OrdinalIgnoreCase)
                        || name.Equals(CompleteMarkName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    result.Add(Path.GetRelativePath(root, file));
                }
            }
            catch { }
        }
        return result;
    }

    private static bool IsNonBodyDir(string name)
        => name.Equals("Mods", StringComparison.OrdinalIgnoreCase)
           || name.Equals("smapi", StringComparison.OrdinalIgnoreCase)
           || name.Equals("smapi-internal", StringComparison.OrdinalIgnoreCase)
           || name.Equals(".DepotDownloader", StringComparison.OrdinalIgnoreCase)
           || name.Equals("junigrid_trash", StringComparison.OrdinalIgnoreCase)
           || name.Equals(".junigrid_trash", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("smapi-installer", StringComparison.OrdinalIgnoreCase);

    private static bool IsGameVersion16OrNewer(string? ver)
    {
        if (string.IsNullOrWhiteSpace(ver)) return false;
        var parts = ver.Split('.');
        var maj = parts.Length > 0 && int.TryParse(parts[0], out var ma) ? ma : 0;
        var min = parts.Length > 1 && int.TryParse(
            new string(parts[1].TakeWhile(char.IsDigit).ToArray()), out var mi) ? mi : 0;
        return maj > 1 || (maj == 1 && min >= 6);
    }

    [DllImport("Kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateHardLink(string newFileName, string? existingFileName, IntPtr securityAttributes);

    /// <summary>
    /// 同盘硬链接：本体 Content 等只读文件在 staging↔游戏目录之间零拷贝。
    /// 不同盘/失败则返回 false，回落 File.Copy。Mods 不要用硬链接（游戏会写 config，会连带改缓存）。
    /// </summary>
    private static bool TryHardLink(string src, string dest)
    {
        try
        {
            if (!SameVolume(src, dest)) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (File.Exists(dest)) File.Delete(dest);
            return CreateHardLink(dest, src, IntPtr.Zero);
        }
        catch { return false; }
    }

    private static void CopyGameFileFast(string src, string dest)
    {
        if (TryHardLink(src, dest)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        try
        {
            if (File.Exists(dest)
                && new FileInfo(src).Length == new FileInfo(dest).Length
                && File.GetLastWriteTimeUtc(src) == File.GetLastWriteTimeUtc(dest))
                return;
        }
        catch { }
        File.Copy(src, dest, overwrite: true);
    }

    private static int IoParallelism => Math.Max(4, Environment.ProcessorCount);

    private static bool SameVolume(string a, string b)
    {
        try
        {
            var ra = Path.GetPathRoot(Path.GetFullPath(a));
            var rb = Path.GetPathRoot(Path.GetFullPath(b));
            return !string.IsNullOrEmpty(ra)
                   && string.Equals(ra, rb, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>改名的结果。LastError/Attempts 要带出来：日志里只写「改名失败」判不出是瞬时占用
    /// 还是结构性占用，而这两者的正确处置完全相反（前者值得等，后者等再久也是白等）。</summary>
    private sealed record RelocateOutcome(bool Moved, int Attempts, Exception? LastError);

    /// <summary>同盘目录整棵改名（近似瞬时）；跨盘/占用返回 Moved=false，调用方回落并行拷贝。
    /// ⚠ 退避口径改过（2026-09-23）：原来是 5 次尝试、睡 100+200+500+1000 = 1.8 秒，
    /// 理由是「占用多半是瞬时的」。真机日志否掉了这个前提 —— 连续三次切换里 Mods 与
    /// .junigrid_trash 的改名**每一次都失败**，1.8 秒从来没等到过成功；而搬一个 0 字节的
    /// .junigrid_trash 也要 3.3 秒，全是退避与探测的空转。
    /// 现在压到 3 次尝试、共 300ms：真瞬时（Defender 扫一下、缩略图刚读完）仍然救得回来，
    /// 结构性占用则快速转拷贝。回落路径本身是核对过的，语义仍是「移动」，所以少等不等于少做。
    /// 另外 dest 的整棵删除移出了重试循环 —— 原来每次尝试前都删一遍，等于把同一棵树删 5 次。</summary>
    private static RelocateOutcome TryRelocateDirectory(string src, string dest)
    {
        if (!Directory.Exists(src) || !SameVolume(src, dest)) return new RelocateOutcome(false, 0, null);
        var parent = Path.GetDirectoryName(Path.GetFullPath(dest));
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        Exception? last = null;
        var attempt = 0;
        try { if (Directory.Exists(dest)) Directory.Delete(dest, true); }
        catch (Exception ex) { last = ex; }
        for (; attempt < 3; attempt++)
        {
            try
            {
                Directory.Move(src, dest);
                return new RelocateOutcome(true, attempt + 1, null);
            }
            catch (Exception ex)
            {
                last = ex;
                if (attempt < 2) try { Thread.Sleep(new int[] { 100, 200 }[attempt]); } catch { }
            }
        }
        AppLog.Warn("DepotDownloader",
            $"改名失败（{attempt} 次尝试）{src} → {dest}：{DescribeIoError(last)}，回落拷贝");
        return new RelocateOutcome(false, attempt, last);
    }

    /// <summary>把异常压成一行可判读的证据：类型 + HRESULT + 原文。
    /// 0x80070005 = ACCESS_DENIED（目录句柄没给 FILE_SHARE_DELETE，或是某进程的当前目录），
    /// 0x80070020 = SHARING_VIOLATION（文件正被人打开）。两者的处置不同，混成一句「被占用」就判不了。</summary>
    private static string DescribeIoError(Exception? ex)
    {
        if (ex is null) return "(无异常)";
        var hr = ex.HResult;
        var tag = hr switch
        {
            unchecked((int)0x80070005) => "ACCESS_DENIED",
            unchecked((int)0x80070020) => "SHARING_VIOLATION",
            unchecked((int)0x800700B7) => "ALREADY_EXISTS",
            unchecked((int)0x80070091) => "DIR_NOT_EMPTY",
            _ => "0x" + hr.ToString("X8"),
        };
        return $"{ex.GetType().Name}/{tag}: {ex.Message}";
    }


    /// <summary>只读探测：Mods 顶层哪些条目此刻"别人正在用"。
    /// 判据 = 以 FileShare.None 打开一个文件，打得开就说明没人用；打不开（IOException /
    /// 共享冲突）就点名。每个顶层条目最多探 12 个文件就够定位，113 项也就几百次打开、
    /// 几十毫秒，而且只在改名已经失败时才跑。**不写、不改、不删任何东西。**
    /// ⚠ 已知盲区：它只探**文件**句柄。改名失败的常见原因是有人握着**目录本身**的句柄
    /// （FileSystemWatcher、某进程把它当当前目录），那种情况这里永远探不到，
    /// 日志就恒为「(没探到具体项)」—— 真机三次切换全是这个结果。所以判「谁钉住了 Mods」
    /// 不能只看这行，要配合下面那句「我们自己的监听句柄已释放/未释放」一起读。</summary>
    private static string LockedEntries(string dir)
    {
        var hit = new List<string>();
        try
        {
            foreach (var top in Directory.EnumerateFileSystemEntries(dir))
            {
                var name = Path.GetFileName(top.TrimEnd(Path.DirectorySeparatorChar));
                if (string.IsNullOrEmpty(name)) continue;
                bool locked;
                try
                {
                    if (Directory.Exists(top))
                    {
                        locked = false;
                        foreach (var f in Directory.EnumerateFiles(top, "*", SearchOption.AllDirectories).Take(12))
                            if (IsInUse(f)) { locked = true; break; }
                    }
                    else locked = IsInUse(top);
                }
                catch { locked = false; }
                if (!locked) continue;
                hit.Add(name);
                if (hit.Count >= 6) break;
            }
        }
        catch { }
        return hit.Count == 0 ? "(文件级没探到 → 多半是目录句柄，见上面的 ACCESS_DENIED/SHARING_VIOLATION)"
                              : string.Join("、", hit);
    }

    private static bool IsInUse(string file)
    {
        try
        {
            using var fs = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }          // 共享冲突 / 正被人删
        catch (UnauthorizedAccessException) { return false; }   // 只是权限，不算占用
        catch { return false; }
    }

    internal enum DirMove { Renamed, Copied, Failed }

    /// <summary>
    /// 把 src 整棵「移动」到 dest：同盘改名优先，改名失败才拷贝 —— 但拷贝也必须兑现移动的语义。
    /// 旧写法拷完不动源，于是源原地留着变成第二份"权威副本"：实测 2026-09-19 23:27 恢复走拷贝后，
    /// 游戏 Mods 与 1.6.15 抽屉各存一份 113 项（≈661 MB 双份，用户视角＝分不清哪个算数）；
    /// 更坏的是拷一半被打断（关应用/磁盘满/Defender 挂锁），下次切换会拿这份残缺的
    /// 去顶掉抽屉里那份完整的 —— mod 就这么没了。
    /// 所以：拷贝后逐文件核对，全等才删源；核对不过就保留源、把残缺的 dest 改名挪开并报出来。
    /// graveDir 非空时，先把「dest 里有、src 里没有」的顶层条目挪进去 —— 改名快路径会连整个 dest
    /// 一起删掉，而那些条目往往是上一批还没被认领走的 mod，不能跟着蒸发。
    /// </summary>
    internal static DirMove MoveDirectoryVerified(string src, string dest, string? graveDir,
        IProgress<Progress>? progress, string label, string? quarantineRoot = null)
    {
        if (!Directory.Exists(src)) return DirMove.Renamed;   // 没东西可搬，等同于「已不在源侧」

        var t = new PhaseTimer();
        if (graveDir is not null && Directory.Exists(dest))
            MoveAsideForeignEntries(dest, src, graveDir);
        t.Lap("让位");

        var reloc = TryRelocateDirectory(src, dest);
        t.Lap("改名");
        if (reloc.Moved)
        {
            t.Report("DepotDownloader", $"{label}：改名成功（{src}）");
            return DirMove.Renamed;
        }

        // 转复印之前点一次名：整棵挪不动，一定是 Mods 里某个东西正被人打开着。
        // 异常文本只给一个路径，说不清是谁；把"打不开的顶层条目"列出来才判得出来是
        // 杀软、资源管理器窗口，还是我们自己的线程。
        // 附带报出监听句柄状态：三次真机切换的改名全败在 ACCESS_DENIED，而文件级探测恒为空，
        // 说明钉住它的是**目录**句柄 —— 我们自己的 Mods watcher 是首要嫌疑，把它的状态一起记下来
        // 才能判是不是它（只关 EnableRaisingEvents 不释放句柄，必须 Dispose 才算真放手）。
        AppLog.Warn("DepotDownloader",
            $"{label}：改名不通（{DescribeIoError(reloc.LastError)}，{reloc.Attempts} 次尝试全败 = 非瞬时占用；" +
            $"自家 Mods 监听 {WatcherState()}），文件占用探测 → {LockedEntries(src)}");

        try { if (Directory.Exists(dest)) Directory.Delete(dest, true); } catch { }
        try
        {
            ParallelCopyDirectory(src, dest, skipTrash: true, progress, label);
        }
        catch (Exception ex)
        {
            // 拷贝中途抛错（磁盘满/源被占用）不能让它冒到调用方 —— 调用方那层 catch 只记一行日志
            // 就继续往下清游戏目录，等于拿一份没拷完的东西去顶现役 Mods。交给下面的核对判 Failed。
            AppLog.Warn("DepotDownloader", $"{label}：拷贝中断（{ex.Message}），转入核对");
        }
        t.Lap("拷贝");

        var bad = CountCopyMismatches(src, dest);
        t.Lap("核对");
        if (bad == 0)
        {
            DeleteTreeParallel(src);
            // 源壳子删不掉多半是瞬时句柄：短等一轮再试。⚠ 别把退避拉太长 —— 占用方是
            // 页面 watcher 这类"不会自己松手"的东西时，长退避只是白等（实测一次多花 8 秒）。
            // 原来是 200+400+800 三轮共 1.4 秒，真机日志显示这 1.4 秒每次都在白等
            // （搬 0 字节的 .junigrid_trash 也照睡），压到一轮 200ms：残留壳子本来就被容忍
            // （下面返回 Copied 并记日志），少等不影响正确性。
            if (Directory.Exists(src))
            {
                try { Directory.Delete(src); } catch { }
                if (Directory.Exists(src))
                {
                    Thread.Sleep(200);
                    DeleteTreeParallel(src);
                }
            }
            t.Lap("删源");
            if (!Directory.Exists(src) || !SourceHasFiles(src))
            {
                // 源已消失，或只剩删不掉的空目录壳子 —— 内容完整落在 dest，移动语义兑现。
                if (Directory.Exists(src))
                    AppLog.Warn("DepotDownloader", $"{label}：拷贝并核对通过，但源空壳删不干净（占用），残留 {src}");
                t.Report("DepotDownloader", Directory.Exists(src)
                    ? $"{label}：走拷贝，源空壳残留"
                    : $"{label}：走拷贝（改名不通）");
                return DirMove.Copied;
            }
            // 源里还有真文件（被占用删不掉，常见于硬链接后源名仍被锁）：宁可双份，
            // 也绝不把「源还站着」报成 Copied —— 调用方会拿 Copied 去清游戏目录，
            // 等于用一份没兑现的移动去顶现役 Mods（D14c 钉的就是这个）。
            AppLog.Warn("DepotDownloader",
                $"{label}：拷贝后源仍有文件（占用删不掉），移动未兑现，保留源 {src} 并报 Failed");
            progress?.Report(new Progress(
                $"{label}：移动未完成 —— 源目录里仍有文件被占用删不掉，已保留 {src}。请关闭占用后重试", null));
            t.Report("DepotDownloader", $"{label}：源残留文件，判 Failed");
            return DirMove.Failed;
        }

        // 残缺副本挪哪去：默认挪到 dest 同级。但当 dest 在**现役存档目录**里时，同级 = 游戏读档菜单
        // 会把这个「-incomplete-」壳当成一份可玩存档列出来（实测 2026-09-22 13:35 一次失败拷贝就这么
        // 在 Saves 里留下两份 39M/21M 的完整档，之后每次切换还跟着往返）。所以调用方对存档这类
        // dest 传 quarantineRoot，把残缺副本挪出 Saves，落到备份区的 _incomplete 里，读档菜单就干净了。
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string aside;
        if (quarantineRoot is not null)
        {
            var qdir = Path.Combine(quarantineRoot, "_incomplete");
            aside = Path.Combine(qdir, Path.GetFileName(dest) + "-incomplete-" + stamp);
            try { Directory.CreateDirectory(qdir); } catch { }
        }
        else
        {
            aside = dest + "-incomplete-" + stamp;
        }
        try { Directory.Move(dest, aside); }
        catch { aside = "(挪不开:" + dest + ")"; }
        AppLog.Warn("DepotDownloader",
            $"{label}：拷贝后核对有 {bad} 项不一致，源保留在 {src}，残缺副本已挪到 {aside}");
        progress?.Report(new Progress(
            $"{label}：拷贝后核对有 {bad} 项不一致，已保留原目录 {src}，残缺副本挪到 {aside} —— 请腾出磁盘/关闭占用后重试", null));
        t.Report("DepotDownloader", $"{label}：核对不通过（{bad} 项）");
        return DirMove.Failed;
    }

    /// <summary>把 dest 里「src 没有」的顶层条目挪进 graveDir（改名快路径会连 dest 一起删）。</summary>
    private static void MoveAsideForeignEntries(string dest, string src, string graveDir)
    {
        try
        {
            var srcNames = new HashSet<string>(
                Directory.EnumerateFileSystemEntries(src).Select(Path.GetFileName!),
                StringComparer.OrdinalIgnoreCase);
            var moved = new List<string>();
            foreach (var e in Directory.EnumerateFileSystemEntries(dest))
            {
                var nm = Path.GetFileName(e);
                if (srcNames.Contains(nm)) continue;
                try
                {
                    Directory.CreateDirectory(graveDir);
                    var to = Path.Combine(graveDir, nm);
                    if (Directory.Exists(to) || File.Exists(to))
                        to = Path.Combine(graveDir, nm + "-dup" + DateTime.Now.ToString("HHmmss"));
                    Directory.Move(e, to);
                    moved.Add(nm);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("DepotDownloader", $"dest 独有条目「{nm}」挪进隔离区失败: {ex.Message}");
                }
            }
            if (moved.Count > 0)
                AppLog.Warn("DepotDownloader",
                    $"{dest} 里有 {moved.Count} 项不属于本次移动的源，已挪进 {graveDir}：" + string.Join("、", moved));
        }
        catch { }
    }

    /// <summary>源树里还有没有真文件（空目录壳子不算）—— 决定「源删不干净」是容忍还是判 Failed。</summary>
    private static bool SourceHasFiles(string src)
    {
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var f in Directory.EnumerateFiles(src, "*", opts))
            {
                if (Path.GetRelativePath(src, f).Contains("junigrid_trash", StringComparison.OrdinalIgnoreCase))
                    continue;
                return true;
            }
        }
        catch { return true; }   // 连源都数不动 → 当还有文件处理（宁可 Failed）
        return false;
    }

    /// <summary>源里有多少文件在目标缺失或大小不符（回收站不计，与拷贝口径一致）。</summary>
    private static int CountCopyMismatches(string src, string dest)
    {
        int bad = 0;
        try
        {
            // 不用 IgnoreInaccessible：被锁/无权限的源文件必须计入不一致，
            // 否则拷贝跳过它、核对也跳过它，会假报「核对通过」。
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false };
            foreach (var f in Directory.EnumerateFiles(src, "*", opts))
            {
                var rel = Path.GetRelativePath(src, f);
                if (rel.Contains("junigrid_trash", StringComparison.OrdinalIgnoreCase)) continue;
                long want;
                try { want = new FileInfo(f).Length; }
                catch { bad++; continue; }
                var to = Path.Combine(dest, rel);
                try
                {
                    if (!File.Exists(to) || new FileInfo(to).Length != want) bad++;
                }
                catch { bad++; }
            }
        }
        catch { return 1; }   // 连源都数不动 → 当核对不过处理（宁可保留源）
        return bad;
    }

    /// <summary>src 和 dest 顶层名字几乎对不上 = 不是「把当前版本存回抽屉」的合法快照
    /// （半截恢复 / 切换竞态 / 放错目录）。这时 Replace 会把抽屉里那份完整的毁掉或扫进孤儿区。
    /// dest 不足 5 项、或看不清 → 不拦（沿用原行为）。</summary>
    private static bool LooksUnlikeSnapshot(string src, string dest)
    {
        try
        {
            if (!Directory.Exists(dest)) return false;
            static IEnumerable<string> RealNames(string dir) =>
                Directory.EnumerateFileSystemEntries(dir)
                    .Select(Path.GetFileName!)
                    .Where(n => !n.StartsWith('.')
                        && !n.Equals("junigrid_trash", StringComparison.OrdinalIgnoreCase));
            var destNames = RealNames(dest).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (destNames.Count < 5) return false;
            var srcNames = RealNames(src).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var overlap = destNames.Count(srcNames.Contains);
            // 对上不到 1/4 → 不像快照
            return overlap * 4 < destNames.Count;
        }
        catch { return false; }
    }

    /// <summary>Mods 目录里除回收站外还有没有真东西（决定要不要生成一个批次目录）。</summary>
    private static bool HasMovableEntries(string mods)
    {
        try
        {
            foreach (var e in Directory.EnumerateFileSystemEntries(mods))
            {
                var nm = Path.GetFileName(e);
                if (nm.Equals("junigrid_trash", StringComparison.OrdinalIgnoreCase)
                    || nm.Equals(".junigrid_trash", StringComparison.OrdinalIgnoreCase)) continue;
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>并行删整棵树：先删文件再删目录，比 Directory.Delete(true) 的串行递归快很多。</summary>
    private static void DeleteTreeParallel(string root)
    {
        if (!Directory.Exists(root)) return;
        try
        {
            var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList();
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = IoParallelism }, f =>
            {
                try { File.Delete(f); } catch { }
            });
        }
        catch { }

        try
        {
            var dirs = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).ToList();
            dirs.Sort((a, b) => b.Length.CompareTo(a.Length));
            foreach (var d in dirs)
            {
                try { Directory.Delete(d, recursive: false); } catch { try { Directory.Delete(d, true); } catch { } }
            }
        }
        catch { }

        try { Directory.Delete(root, true); } catch { }
    }

    /// <summary>清空目录内容（保留目录本身），并行删；回收站子目录可跳过。</summary>
    private static void ClearDirectoryParallel(string dir)
    {
        if (!Directory.Exists(dir)) return;
        List<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir).ToList(); }
        catch { return; }
        foreach (var e in entries)
        {
            var name = Path.GetFileName(e);
            if (name.Equals("junigrid_trash", StringComparison.OrdinalIgnoreCase)
                || name.Equals(".junigrid_trash", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                if (Directory.Exists(e)) DeleteTreeParallel(e);
                else File.Delete(e);
            }
            catch { }
        }
    }

    /// <summary>robocopy /E /MT：Windows 上大量小文件的最快稳妥拷贝；0–7 为成功。</summary>
    private static bool TryRobocopy(string src, string dest, string extraArgs = "")
    {
        try
        {
            if (!Directory.Exists(src)) return false;
            var exe = Path.Combine(Environment.SystemDirectory, "robocopy.exe");
            if (!File.Exists(exe)) return false;
            Directory.CreateDirectory(dest);
            var args = $"\"{src}\" \"{dest}\" /E /MT:{Math.Min(32, IoParallelism)} /R:1 /W:1 " +
                       "/NFL /NDL /NJH /NS /NC /NP /XD junigrid_trash .junigrid_trash" +
                       (string.IsNullOrWhiteSpace(extraArgs) ? "" : " " + extraArgs);
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(20 * 60 * 1000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            return p.ExitCode is >= 0 and < 8;
        }
        catch { return false; }
    }

    /// <summary>把 staging 目录的总字节数写入 .junigrid-size，列表页免全量扫描。</summary>
    private static void WriteSizeCache(string stagingDir)
    {
        try
        {
            if (!Directory.Exists(stagingDir)) return;
            long size = 0;
            foreach (var f in Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(f).Equals(".junigrid-size", StringComparison.OrdinalIgnoreCase)) continue;
                try { size += new FileInfo(f).Length; } catch { }
            }
            File.WriteAllText(Path.Combine(stagingDir, ".junigrid-size"), size.ToString());
        }
        catch { }
    }

    private static long ReadSizeCache(string stagingDir)
    {
        try
        {
            var p = Path.Combine(stagingDir, ".junigrid-size");
            if (File.Exists(p) && long.TryParse(File.ReadAllText(p).Trim(), out var v) && v > 0)
                return v;
        }
        catch { }
        return 0;
    }

    internal static string StagingRoot
    {
        get
        {
            var root = StoragePaths.DepotStagingDir;
            try
            {
                // 旧路径（LocalAppData）→ 统一缓存目录，首次访问时搬迁
                var legacy = StoragePaths.LegacyDepotStagingDir;
                if (!string.Equals(root, legacy, StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(legacy)
                    && !Directory.Exists(root))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(root)!);
                    Directory.Move(legacy, root);
                    AppLog.Warn("DepotDownloader", "版本缓存已迁到统一缓存目录: " + root);
                }
                else if (Directory.Exists(legacy) && Directory.Exists(root)
                         && !Directory.EnumerateFileSystemEntries(legacy).Any())
                {
                    try { Directory.Delete(legacy); } catch { }
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("DepotDownloader", "版本缓存迁移失败: " + ex.Message);
            }
            return root;
        }
    }

    /// <param name="Queued">true = 在等前面那路 Steam 会话；false = 已经轮到本人；
    /// null = 这条进度与排队无关（上层据此清掉排队标记）。</param>
    public sealed record Progress(string State, int? Percent, string? Line = null, bool? Queued = null);

    public sealed class DepotException(string message) : Exception(message);

    public DepotDownloaderService() { }

    /// <summary>一条可下载的历史版本。ManifestId 为空 = 列表展示但需用户从 SteamDB 补 ID。</summary>
    public sealed record KnownVersion(
        string Label,
        string ManifestId,
        string? Version = null)
    {
        public bool HasManifest => !string.IsNullOrWhiteSpace(ManifestId);
        // 对齐玩家动力：下拉里主要显示版本号
        public string DisplayName => !string.IsNullOrEmpty(Version) ? Version : Label;
    }

    // 星露谷 Windows depot 413151 —— 从新到旧。Manifest 按 SteamDB depot 历史 + Wiki 发布日对齐：
    // https://stardewvalleywiki.com/Version_History （2026-09 对照补全 1.01–1.3.32 等 14 个缺失版本，
    // 并修正 1.1 / 1.2.26 / 1.3.27 / 1.3.28 / 1.3.33 / 1.5.1 六处日期错位的映射）。
    // 不收录：compatibility / monogame64bit / beta 分支构建，以及 Wiki 无版本号的同日中间构建
    // （如 1.6.4 的 4-19 热修、2016-02-29 一天 9 个构建）。1.07a 只新增了 Mac/Linux 支持，
    // Windows 内容与 1.07 完全相同、无独立 manifest，故不单列。
    private static readonly KnownVersion[] KnownStardewWindows =
    [
        new("正式版", "4278718763097142923", "1.6.15"),
        new("上一正式版", "1364246008775303529", "1.6.14"),
        new("历史版", "1289391404978285152", "1.6.13"),
        new("历史版", "7276192310056789702", "1.6.12"),
        new("历史版", "6985985228734128541", "1.6.11"),
        new("历史版", "3240670252057385108", "1.6.10"),
        new("历史版", "2836558896332251757", "1.6.9"),
        new("经典版", "1777024427851858279", "1.6.8"),
        new("历史版", "8241037280344201140", "1.6.7"),
        new("历史版", "1065152683462704684", "1.6.6"),
        new("历史版", "8895407084082948264", "1.6.5"),
        new("历史版", "3118431248827251876", "1.6.4"),
        new("历史版", "3423907493306588851", "1.6.3"),
        new("历史版", "7202829112632541182", "1.6.2"),
        new("历史版", "6093927695464368045", "1.6.1"),
        new("1.6 首发", "5012590689708703589", "1.6"),
        new("经典版", "5609262347030774375", "1.5.6"),
        new("历史版", "4397694255132373486", "1.5.5"),
        new("稳定旧版", "7802000804251603756", "1.5.4"),
        new("历史版", "4121989135652425382", "1.5.3"),
        new("历史版", "5396049550535566677", "1.5.2"),
        new("历史版", "4812928243273622870", "1.5.1"),
        new("历史版", "4487511898025325586", "1.5"),
        new("1.4 末版", "6307986820908740561", "1.4.5"),
        new("历史版", "7978993718867933207", "1.4.4"),
        new("历史版", "7258148177702857381", "1.4.3"),
        new("历史版", "8519233901628247204", "1.4.2"),
        new("历史版", "7149289726988606001", "1.4.1"),
        // 注意：这条就是星露谷 1.4 正式版（发布日 2019-11-26，与 Wiki 一致）。
        // 它的文件内部版本号(FIleVersion)是 1.3.7269 —— 1.4 全系列都没改内部版本号，
        // 不要因为内部版本号像 1.3 就把它当成 1.3 晚期（实测踩坑）。
        new("1.4 正式版", "2373680906867811602", "1.4"),
        new("1.3 末版", "7951557878765234474", "1.3.36"),
        new("历史版", "3086333938055749962", "1.3.33"),
        new("历史版", "684527103824506785", "1.3.32"),
        new("历史版", "6256862244170871577", "1.3.28"),
        new("1.3 首发（联机）", "3920107848374752907", "1.3.27"),
        new("1.2 末版", "5793210319202900873", "1.2.33"),
        new("历史版", "1612680387557367797", "1.2.32"),
        new("历史版", "1627658297112725452", "1.2.31"),
        new("历史版", "7102179406389569056", "1.2.30"),
        new("历史版", "3293327472846622645", "1.2.29"),
        new("新增六语言", "3227482562029885606", "1.2.26"),
        new("1.1 修补", "7487215307508292747", "1.11"),
        new("1.1", "2981425245818393618", "1.1"),
        new("历史版", "3230210310573566485", "1.07"),
        new("历史版", "3150973666024140120", "1.06"),
        new("历史版", "7430847287787667997", "1.051b"),
        new("历史版", "4973097789996737182", "1.051"),
        new("历史版", "285564730673029890", "1.05"),
        new("历史版", "7765407486833584479", "1.04"),
        new("历史版", "4594775194614467491", "1.03"),
        new("历史版", "8462477710223862747", "1.02"),
        new("历史版", "6198424258489893658", "1.01"),
        new("首发版", "8507975696251774427", "1.0"),
    ];

    public IReadOnlyList<KnownVersion> GetKnownVersions(string appId)
        => appId == "413150" ? KnownStardewWindows : Array.Empty<KnownVersion>();

    /// <summary>该 manifest 是否就是目录里最新的官方正式版（第一条）。部署它 = 与 Steam
    /// 当前最新版完全一致，appmanifest 只读锁毫无收益，只会把用户在 Steam 里换分支/
    /// 更新的写清单操作卡成「磁盘写入错误」。</summary>
    public static bool IsLatestOfficialManifest(string appId, string? manifestId)
    {
        if (appId != "413150" || string.IsNullOrWhiteSpace(manifestId)) return false;
        return string.Equals(KnownStardewWindows[0].ManifestId, manifestId, StringComparison.Ordinal);
    }

    /// <summary>
    /// 把 Steam 的拼接式分支名换成正常点号版本，让「按数字段比大小」这条路走得通。
    ///
    /// 为什么必须换：Steam 那边 1.0.1 这个分支叫 "1.01"、1.1.1 叫 "1.11"、1.0.5.1 叫 "1.051"
    /// （表里 1.11 的显示名就是「1.1 修补」）。不换算的话 1.11 被当成「1.11 版」，比 1.2 到 1.10 全都大
    /// —— 于是「用 1.11 启动」时 1.2–1.6.15 的档全被判成比它旧、读得了，一份都不收，
    /// 玩家点哪个都是进档闪退。实测 2026-09-22 有 10 个标签踩这条
    /// （1.01/1.02/1.03/1.04/1.05/1.051/1.051b/1.06/1.07/1.11），正是玩家报的「1.0.X 到 1.1.X 读不进存档」。
    ///
    /// 表里其余标签（1.2.26 起）本身就是正常点号写法，原样返回；认不出的字符串也原样返回
    /// （存档里写的 gameVersion 都是正常点号，只有表没收的如 1.5.7 会走这条路）。
    /// 1.051b 按表里的次序（在 1.051 之后、1.06 之前）给成 1.0.5.2。
    /// </summary>
    public static string? NormalizeVersionLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return label;
        return BranchVersions.TryGetValue(label.Trim(), out var canon) ? canon : label;
    }

    private static readonly Dictionary<string, string> BranchVersions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1.01"] = "1.0.1", ["1.02"] = "1.0.2", ["1.03"] = "1.0.3", ["1.04"] = "1.0.4",
        ["1.05"] = "1.0.5", ["1.051"] = "1.0.5.1", ["1.051b"] = "1.0.5.2",
        ["1.06"] = "1.0.6", ["1.07"] = "1.0.7", ["1.11"] = "1.1.1",
    };

    /// <summary>
    /// 版本表下标 = 发行时间序（0 = 最新）。标签能在表里认出来时，**只比下标**，
    /// 不要再拿 Steam 分支名当版本号做数字段比较 —— 那条路会得出 1.11 &gt; 1.6。
    /// 认不出的标签才落回 <see cref="NormalizeVersionLabel"/> + 数字段。
    /// </summary>
    public static bool TryGetVersionRank(string? label, out int rank)
    {
        rank = -1;
        if (string.IsNullOrWhiteSpace(label)) return false;
        foreach (var c in TableCandidates(label!))
        {
            for (var i = 0; i < KnownStardewWindows.Length; i++)
            {
                if (string.Equals(KnownStardewWindows[i].Version, c, StringComparison.OrdinalIgnoreCase))
                {
                    rank = i;
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>一个标签可能对应的表内写法：原样、拼接名换算后、去掉尾部 .0 后。</summary>
    private static IEnumerable<string> TableCandidates(string label)
    {
        var raw = label.Trim();
        yield return raw;
        var n = NormalizeVersionLabel(raw);
        if (!string.IsNullOrWhiteSpace(n)) yield return n!.Trim();
        var s = StripTrailingZeros(raw);
        if (!string.Equals(s, raw, StringComparison.OrdinalIgnoreCase)) yield return s;
        var ns = NormalizeVersionLabel(s);
        if (!string.IsNullOrWhiteSpace(ns) && !string.Equals(ns, s, StringComparison.OrdinalIgnoreCase))
            yield return ns!.Trim();
    }

    private static string StripTrailingZeros(string v)
    {
        var parts = v.Split('.');
        var len = parts.Length;
        while (len > 1 && parts[len - 1].TrimStart('0') is "" or "0" or "00")
            len--;
        return string.Join('.', parts.Take(len));
    }

    /// <summary>比较两个版本标签。&lt;0 = a 更旧。两边都能进表时用表序；否则换算后按数字段比。绝不抛。</summary>
    public static int CompareVersionLabels(string? a, string? b)
    {
        if (TryGetVersionRank(a, out var ra) && TryGetVersionRank(b, out var rb))
            return rb.CompareTo(ra);   // 下标越小越新：a 更新 → 返回正
        var x = DottedSegments(NormalizeVersionLabel(a));
        var y = DottedSegments(NormalizeVersionLabel(b));
        for (var i = 0; i < x.Length; i++)
        {
            var c = x[i].CompareTo(y[i]);
            if (c != 0) return c;
        }
        return 0;
    }

    private static long[] DottedSegments(string? v)
    {
        var r = new long[4];
        var parts = (v ?? "").Split('.', '-', ' ', '_');
        for (var i = 0; i < r.Length && i < parts.Length; i++)
            if (long.TryParse(parts[i], out var n)) r[i] = n;
        return r;
    }

    /// <summary>合并内置 + 自定义；自定义按 Version 覆盖同名内置（补上缺失的 Manifest ID）。</summary>
    public List<KnownVersion> GetMergedVersions(string appId, IEnumerable<CustomHistoricalVersion>? custom)
    {
        var list = GetKnownVersions(appId).ToList();
        if (custom is null) return list;
        foreach (var c in custom)
        {
            if (string.IsNullOrWhiteSpace(c.ManifestId)) continue;
            var id = c.ManifestId.Trim();
            var ver = string.IsNullOrWhiteSpace(c.Version) ? null : c.Version.Trim();
            // 同版本号且内置缺 ID → 补上
            var idx = ver is null ? -1 : list.FindIndex(v => v.Version == ver && !v.HasManifest);
            if (idx >= 0)
            {
                var old = list[idx];
                list[idx] = old with
                {
                    ManifestId = id,
                    Label = string.IsNullOrWhiteSpace(c.Label) ? old.Label : c.Label.Trim(),
                };
                continue;
            }
            if (list.Any(v => v.ManifestId == id)) continue;
            list.Add(new KnownVersion(
                string.IsNullOrWhiteSpace(c.Label) ? "自定义" : c.Label.Trim(),
                id,
                ver));
        }
        return list;
    }

    public sealed record StagedPackage(string Label, string ManifestId, string Path, long SizeBytes, DateTime At, bool Complete);

    /// <summary>版本包的 Mods 抽屉里现存多少个条目（顶层计，回收站不算）。
    /// 返回 -1 = 数不动（占用/权限）：界面要写"数量读不出来"，绝不能写"0 个"——
    /// "0 个 mod"会被读成"放心删"，那是最坏的失败方向。</summary>
    public static int CountPackMods(string stagingDir)
    {
        try
        {
            var mods = Path.Combine(stagingDir, "Mods");
            if (!Directory.Exists(mods)) return 0;
            var n = 0;
            foreach (var e in Directory.EnumerateFileSystemEntries(mods))
            {
                var nm = Path.GetFileName(e);
                if (nm.Equals("junigrid_trash", StringComparison.OrdinalIgnoreCase)
                    || nm.Equals(".junigrid_trash", StringComparison.OrdinalIgnoreCase)) continue;
                n++;
            }
            return n;
        }
        catch { return -1; }
    }

    /// <summary>删版本包的确认正文。两个删除入口（版本管理弹窗、设置页存储）都调这一个函数 ——
    /// 那个文件夹里除了本体还躺着【该版本的 Mods 抽屉】，而 DeleteStagedPackage 是
    /// Directory.Delete(dir, true) 递归硬删、不进回收站；只说"下次要重下"会让人以为
    /// 大不了重下，实际是把这一版的 mod 一起删掉（2026-09-19 实测抽屉里躺着 113 项）。</summary>
    public static string DeletePackConfirmText(StagedPackage pkg)
    {
        var size = pkg.SizeBytes > 0 ? $"约 {ResumableDownload.FormatBytes(pkg.SizeBytes)}，" : "";
        var mods = CountPackMods(pkg.Path);
        if (mods > 0)
            return $"{pkg.Label} 的本地缓存{size}里面还存着这个版本的 {mods} 个 mod 文件夹" +
                   "（切走时被移进来的）。删了会一起没，不进回收站、无法撤销 —— " +
                   "本体下次能重下，mod 要重装。";
        if (mods < 0)
            return $"{pkg.Label} 的本地缓存{size}删除后不进回收站、无法撤销。" +
                   "这个版本的 Mods 抽屉现在读不出数量，里面可能正存着该版本的 mod，请先自行确认。";
        return $"{pkg.Label} 的本地缓存{size}删除后不进回收站、无法撤销；" +
               "下次切回该版本要重新下载这么多。";
    }

    private const string ManifestMetaName = ".junigrid-manifest";

    /// <summary>「这一版真的下完了」的标记。只有下载进程退出码 0（或导入/归档确认过完整）才写。
    /// 为什么需要：分片超时留下的半截包能骗过锚点校验（锚点只有 4 个文件），
    /// 切过去进档时才在 NPC 构造里空引用闪退。</summary>
    private const string CompleteMarkName = ".junigrid-complete";

    private static void TryWriteCompleteMark(string stagingDir)
    {
        try { File.WriteAllText(Path.Combine(stagingDir, CompleteMarkName), DateTime.Now.ToString("O")); }
        catch (Exception ex) { AppLog.Warn("DepotDownloader", "写入下载完成标记失败: " + ex.Message); }
    }

    /// <summary>这个目录有没有「已下完」的凭据。</summary>
    public static bool IsStagedPackageComplete(string stagingDir)
    {
        try { return File.Exists(Path.Combine(stagingDir, CompleteMarkName)); }
        catch { return false; }
    }

    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name.Trim())
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        var s = sb.ToString().Trim();
        return string.IsNullOrEmpty(s) ? "unknown" : s;
    }

    /// <summary>缓存目录名用版本号（如 1.5.4）；无版本号时退回 Manifest ID。</summary>
    private static string StagingDirFor(string appId, string depotId, string? version, string manifestId)
    {
        var key = !string.IsNullOrWhiteSpace(version) ? SanitizeFolderName(version!) : manifestId;
        return Path.Combine(StagingRoot, $"{appId}-{depotId}-{key}");
    }

    /// <summary>这个缓存包对应哪个发行号：优先按 manifest 查内置版本表（表里的 DisplayName
    /// 就是发行号），查不到退回目录名里那段版本。⚠ 不能用包内 exe 的文件版本 ——
    /// XNA 时代的 1.2.x 自报 1.0.61xx。</summary>
    private static string? DeployedLabelOf(string stagingDir)
    {
        var name = Path.GetFileName(stagingDir.TrimEnd(Path.DirectorySeparatorChar, '/'));
        var parts = name.Split('-');
        var folderKey = parts.Length >= 3 ? string.Join('-', parts.Skip(2)) : name;
        var manifest = ReadManifestMeta(stagingDir);
        var known = string.IsNullOrWhiteSpace(manifest)
            ? null : KnownStardewWindows.FirstOrDefault(k => k.ManifestId == manifest);
        return !string.IsNullOrWhiteSpace(known?.DisplayName) ? known!.DisplayName
             : !string.IsNullOrWhiteSpace(folderKey) ? folderKey : null;
    }

    private static void WriteManifestMeta(string stagingDir, string manifestId)    {
        try { File.WriteAllText(Path.Combine(stagingDir, ManifestMetaName), manifestId); } catch { }
    }

    private static string? ReadManifestMeta(string stagingDir)
    {
        try
        {
            var p = Path.Combine(stagingDir, ManifestMetaName);
            return File.Exists(p) ? File.ReadAllText(p).Trim() : null;
        }
        catch { return null; }
    }

    private static string? FindStagingDir(string manifestId)
    {
        if (!Directory.Exists(StagingRoot)) return null;
        foreach (var dir in Directory.EnumerateDirectories(StagingRoot))
        {
            var meta = ReadManifestMeta(dir);
            if (meta == manifestId) return dir;
            var name = Path.GetFileName(dir);
            if (name.EndsWith("-" + manifestId, StringComparison.Ordinal)) return dir;
        }
        return null;
    }

    public List<StagedPackage> ListStagedPackages()
    {
        var result = new List<StagedPackage>();
        try
        {
            if (!Directory.Exists(StagingRoot)) return result;
            foreach (var dir in Directory.EnumerateDirectories(StagingRoot))
            {
                var name = Path.GetFileName(dir);
                // SMAPI 共享池不是游戏版本包，不进列表
                if (name.StartsWith("_", StringComparison.Ordinal)) continue;

                var manifest = ReadManifestMeta(dir);
                if (!HasGameBinary(dir))
                {
                    var empty = !Directory.EnumerateFileSystemEntries(dir).Any();
                    if (empty && Directory.GetLastWriteTime(dir) < DateTime.Now.AddMinutes(-30))
                    {
                        try { Directory.Delete(dir, true); } catch { }
                    }
                    continue;
                }

                string label;
                // 展示名优先：内置表 / 文件夹名。**不要**在这里 TryReadGameVersion ——
                // 52 个包各读一遍 exe FileVersion，打开版本列表要卡很久（「未定位」半天）。
                string? realVer = null;
                if (!string.IsNullOrEmpty(manifest))
                {
                    var known = KnownStardewWindows.FirstOrDefault(k => k.ManifestId == manifest);
                    var parts = name.Split('-');
                    var folderKey = parts.Length >= 3 ? string.Join('-', parts.Skip(2)) : name;
                    if (known is not null) label = known.DisplayName;
                    else if (!string.IsNullOrEmpty(folderKey) && folderKey.Any(char.IsDigit) && !folderKey.All(char.IsDigit))
                        label = folderKey;
                    else label = folderKey;
                }
                else
                {
                    var parts = name.Split('-');
                    if (parts.Length < 3) continue;
                    manifest = parts[^1];
                    if (!manifest.All(char.IsDigit)) continue;
                    label = parts.Length >= 3 ? string.Join('-', parts.Skip(2)) : manifest;
                }

                // 大小只读缓存；没有就当 0，**绝不**为列表页全量扫盘
                long size = ReadSizeCache(dir);
                if (size <= 0) size = 0;
                result.Add(new StagedPackage(label, manifest!, dir, size, Directory.GetLastWriteTime(dir),
                    IsStagedPackageComplete(dir)));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("DepotDownloader", "列出暂存包失败: " + ex.Message);
        }
        return result.OrderByDescending(p => p.At).ToList();
    }

    /// <summary>从暂存包读真实游戏版本（exe/dll FileVersion，如 1.3.7269.37809 → 1.3.7269）。</summary>
    private static string? TryReadGameVersion(string dir)
    {
        foreach (var name in new[] { "Stardew Valley.dll", "Stardew Valley.exe" })
        {
            try
            {
                var file = Path.Combine(dir, name);
                if (!File.Exists(file))
                {
                    var hit = Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault();
                    if (hit is null) continue;
                    file = hit;
                }
                var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(file);
                // FileVersion 优先：真实本体的 ProductVersion 是「1.6.15, , 24356, 」这种带逗号
                // 的形式，切 3 段会得到「1.6.15, , 24356,」—— 拿它去查 staging 目录、
                // 生成 _mods-orphan 目录名都对不上（UpdateService.ReadLocalGameVersion
                // 一直用 FileVersion，两边口径必须一致）。
                var raw = fvi.FileVersion ?? fvi.ProductVersion;
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var parts = raw.Split('.');
                return parts.Length >= 3 ? string.Join('.', parts.Take(3)) : raw;
            }
            catch { }
        }
        return null;
    }

    /// <summary>版本号转安全目录名（ProductVersion 里可能带 ", , 24356," 这类尾巴）。</summary>
    private static string SafeDirName(string raw)
        => string.Join("", raw.Split(Path.GetInvalidFileNameChars())).Trim();

    public bool DeleteStagedPackage(string manifestId)
    {
        try
        {
            var dir = FindStagingDir(manifestId);
            if (dir is null) return true;
            Directory.Delete(dir, true);
            AppLog.Warn("DepotDownloader", "已删除暂存包 manifest=" + manifestId);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("DepotDownloader", "删除暂存包失败: " + ex.Message);
            return false;
        }
    }

    /// <summary>认领一个「从别的机器 / U盘 / 网盘拷来的版本包」：校验本体 → 认出版本 → 补写 manifest
    /// 元数据 → 必要时搬进统一缓存目录。之后在版本管理里点它就能<b>离线</b>切换，完全不连 Steam。
    /// 治的是「CM 在国内连不上 → 历史版本永远下不来」这条死路：包只要在本地，就有路可走。</summary>
    public string ImportLocalPackage(string dir, string appId = "413150", string depotId = "413151")
    {
        if (string.IsNullOrWhiteSpace(dir)) throw new DepotException("没选目录。");
        var src = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        var root = Path.GetFullPath(StagingRoot);
        if (string.Equals(src, root, StringComparison.OrdinalIgnoreCase))
            throw new DepotException("请选某个版本文件夹（里面有 Stardew Valley.dll / .exe），不是缓存目录本身。");
        if (!Directory.Exists(src)) throw new DepotException("目录不存在：" + src);
        if (!HasGameBinary(src))
            throw new DepotException("这个目录里没有找到《Stardew Valley.dll》或《Stardew Valley.exe》，不像是游戏版本包。");

        var parts = Path.GetFileName(src).Split('-');
        var manifestId = parts.Length >= 3 && parts[^1].Length > 0 && parts[^1].All(char.IsDigit)
            ? parts[^1] : null;
        var ver = TryReadGameVersion(src);
        if (manifestId is null)
        {
            var known = KnownStardewWindows.FirstOrDefault(k =>
                !string.IsNullOrEmpty(k.Version) && string.Equals(k.Version, ver, StringComparison.OrdinalIgnoreCase));
            if (known is null)
                throw new DepotException((ver is null ? "读不出游戏版本号" : $"认不出 {ver} 对应的 manifest 号") +
                    $"。请把文件夹改名为 {appId}-{depotId}-<manifest 号> 再导入（manifest 号在版本管理里能看到）。");
            manifestId = known.ManifestId;
        }

        Directory.CreateDirectory(root);
        var target = Path.Combine(root,
            $"{appId}-{depotId}-{(string.IsNullOrWhiteSpace(ver) ? manifestId : SanitizeFolderName(ver!))}");
        if (!string.Equals(src, target, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(target))
                throw new DepotException("缓存里已经有同名版本包，先在版本管理里删掉它再导入：\n" + target);
            try { Directory.Move(src, target); }
            catch (IOException)
            {
                throw new DepotException("跨盘移动做不到。请把这个文件夹整个复制到：\n" + target + "\n复制完再点一次导入。");
            }
        }
        WriteManifestMeta(target, manifestId);
        try { File.Delete(Path.Combine(target, ".junigrid-size")); } catch { }   // 大小让它重算，别报旧的
        var label = ver ?? manifestId;
        // 导入前已经校验过本体在盘上；用户从别的机器/网盘拷来的整包按"完整"认。
        TryWriteCompleteMark(target);
        AppLog.Warn("DepotDownloader", $"离线导入版本包：{target} manifest={manifestId} label={label}");
        return label;
    }

    /// <summary>游戏本体文件：1.6 起是 Stardew Valley.dll（MonoGame），1.5.x 及更早是 XNA 的 Stardew Valley.exe。</summary>
    private static bool HasGameBinary(string dir)
        => File.Exists(Path.Combine(dir, "Stardew Valley.dll"))
           || Directory.EnumerateFiles(dir, "Stardew Valley.dll", SearchOption.AllDirectories).Any()
           || File.Exists(Path.Combine(dir, "Stardew Valley.exe"))
           || Directory.EnumerateFiles(dir, "Stardew Valley.exe", SearchOption.AllDirectories).Any();

    /// <summary>这个目录能不能当「同一个 manifest 的半截包」接着下？只认同一个 manifest
    /// 且已经有本体文件的目录 —— 目录名可能带版本号也可能带 manifest，判据不能靠名字。
    /// 判 false 就清空重下（换版本的包、或只剩个空壳时不能拿旧内容去续传）。</summary>
    /// <summary>
    /// 清空重下之前，先把包里的 Mods 抽屉保住。
    /// 这条路径以前是一句静默的 Directory.Delete(staging, true)：换 manifest 重下（Steam 更新了、
    /// 或那半截包认不出来）会把整个包连抽屉一起删掉，而抽屉里可能就是用户唯一的 mod 本体 ——
    /// 2026-09-21 18:30 那次 1.6.15 重下就把 113 个 mod 文件夹这么删了，日志里一个字都没留。
    /// 现在整棵挪进 _mods-orphan\&lt;版本&gt;\Mods-&lt;时间戳&gt;，切回该版本时由孤儿归位逻辑认领回去。
    /// 挪不动就直接中止这次重下：宁可下载失败，也不能把 mod 删掉。
    /// </summary>
    internal static void PreserveStagedModsBeforeWipe(string staging)
    {
        var drawer = Path.Combine(staging, "Mods");
        if (!HasMovableEntries(drawer)) return;
        var label = DeployedLabelOf(staging) ?? Path.GetFileName(staging.TrimEnd(Path.DirectorySeparatorChar));
        var dest = Path.Combine(StagingRoot, "_mods-orphan", SafeDirName(label),
            "Mods-" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        var n = 0;
        try { n = Directory.EnumerateFileSystemEntries(drawer)
                .Count(e => !Path.GetFileName(e).StartsWith(".", StringComparison.Ordinal)); } catch { }
        if (MoveDirectoryVerified(drawer, dest, null, null, "重下前保住 Mods 抽屉") == DirMove.Failed)
            throw new InvalidOperationException(
                $"这个版本缓存里还有 {n} 个 mod 文件夹，但挪不动（多半是正被占用）—— 这次重下已中止，" +
                "mod 一个都没删。请关掉正在读写 Mods 的程序（资源管理器 / 杀软 / 游戏）后重试。");
        AppLog.Warn("DepotDownloader",
            $"重下前把该版本缓存里的 {n} 个 mod 文件夹挪进孤儿区暂存：{dest}（切回 {label} 会自动认领回去）");
    }

    public static bool CanResumeStagedPackage(string stagingDir, string manifestId)
    {
        try
        {
            return Directory.Exists(stagingDir) && HasGameBinary(stagingDir)
                && string.Equals(ReadManifestMeta(stagingDir)?.Trim(), manifestId, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    /// <summary>这个 manifest 是不是已经有一份完整可切换的缓存包（只看，不应用）。</summary>
    public bool IsStagedComplete(string manifestId)
    {
        var staging = FindStagingDir(manifestId);
        return staging is not null && Directory.Exists(staging) && HasGameBinary(staging)
            && IsStagedPackageComplete(staging);   // 半截包不算「有完整缓存」
    }

    public bool TryApplyStaged(string gamePath, string appId, string depotId, string manifestId,
        IProgress<Progress>? progress = null, string? currentGameVersion = null)
    {
        var staging = FindStagingDir(manifestId) ?? Path.Combine(StagingRoot, $"{appId}-{depotId}-{manifestId}");
        if (!Directory.Exists(staging)) return false;
        if (!HasGameBinary(staging)) return false;
        // 没确认下完的包不铺进游戏目录：缺角色贴图时进档会直接空引用闪退。
        if (!IsStagedPackageComplete(staging)) return false;

        progress?.Report(new Progress("使用本地缓存，直接应用…", null));
        ApplyStagingExclusive(staging, gamePath, progress, currentGameVersion);
        return true;
    }


    /// <summary>
    /// v1.5：切换版本时 Mods / SMAPI 按版本隔离。
    /// ① 当前游戏目录里的 Mods/SMAPI 存进「当前版本」staging + SMAPI 共享池
    /// ② 用目标 staging 覆盖游戏本体（仍跳过 Mods）
    /// ③ 用目标 staging 的 Mods 替换游戏 Mods（无则建空目录）
    /// ④ 恢复 SMAPI：目标 staging 有则用之；否则从共享池按兼容桶恢复（切版本免重装）
    /// </summary>
    /// <summary>把当前游戏目录整个收成「当前版本」的缓存包：同盘改名 → 补 manifest 元数据 →
    /// 让包自含 SMAPI。返回包路径；不该做或做不成时返回 null，调用方按老路走（② 删掉旧本体）。</summary>
    private static string? TryAdoptCurrentBody(string gamePath, string targetStaging,
        string currentGameVersion, IProgress<Progress>? progress)
    {
        try
        {
            // 包名前缀抄目标包（413150-413151-…）：ApplyStaging 手上没有 appId/depotId，
            // 而这两者本来就来自同一份 Steam 配置，抄现成的最不容易错。
            var targetName = Path.GetFileName(targetStaging.TrimEnd(Path.DirectorySeparatorChar, '/'));
            var seg = targetName.Split('-');
            var prefix = seg.Length >= 3 ? seg[0] + "-" + seg[1] : "413150-413151";
            var parts = prefix.Split('-');
            var pkg = Path.Combine(StagingRoot, prefix + "-" + SanitizeFolderName(currentGameVersion));
            if (Directory.Exists(pkg)) return null;

            // Steam 清单要在移动之前找：包落在缓存盘，从那儿向上溯找不到 steamapps。
            var manifest = SteamService.ReadInstalledDepotManifest(
                SteamService.FindAppManifest(gamePath, parts[0]), parts[1]);

            progress?.Report(new Progress($"正在把当前本体收进版本 {currentGameVersion} 的缓存…", null));
            Directory.Move(gamePath, pkg);

            if (!string.IsNullOrWhiteSpace(manifest)) WriteManifestMeta(pkg, manifest!);
            // 包自含 SMAPI（v1.6.8 口径）：④ 恢复时优先用包内这份，共享池只兜底。
            if (IsCompleteSmapiSnapshot(pkg) && !IsCompleteSmapiSnapshot(Path.Combine(pkg, "smapi")))
                CopySmapiFiles(pkg, Path.Combine(pkg, "smapi"));
            WriteSizeCache(pkg);
            // 归档来的这份就是现役本体，天然完整（缺东西的话游戏本身就跑不起来）。
            TryWriteCompleteMark(pkg);
            AppLog.Warn("DepotDownloader",
                $"当前本体已收进版本包 {pkg}（manifest={manifest ?? "未知"}）—— 切回该版本不必再回 Steam 校验");
            return pkg;
        }
        catch (Exception ex)
        {
            // 跨盘、被 Steam/资源管理器占用、权限不足都会撞到这里。移动失败时游戏目录原样未动，
            // 所以退回老行为（② 删旧本体），不因为"存不下"就不让人切换。
            AppLog.Warn("DepotDownloader",
                $"当前本体未能收进版本包（{ex.Message}）—— 游戏目录未动，本次切换按旧行为覆盖");
            return null;
        }
    }

    private static void ApplyStaging(string staging, string gamePath, IProgress<Progress>? progress,
        string? currentGameVersion)
    {
        var gameMods = Path.Combine(gamePath, "Mods");
        var targetMods = Path.Combine(staging, "Mods");
        var targetSmapi = Path.Combine(staging, "smapi");
        // 同盘 Mods 用改名迁移：整棵 Move 近似瞬时，避免 GB 级全量拷贝；
        // 改名失败会回落"拷贝 + 核对 + 删源"，语义仍然是移动，所以只记一个落点。
        string? modsMovedTo = null;
        // ① 的移动没通过核对 → 记下原因，①/①a 结束后立刻中止切换（游戏目录还没被碰过）
        string? modsMoveError = null;
        // ③ 结束时「游戏 Mods 顶层应当只有这些名字」的基线，用于回收在途复制后落的文件
        var allowedLiveMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // ⓪ 归档出来的包：② 万一失败时要把它报给用户（旧版本整份还在里面）
        string? adoptedPkg = null;

        PortraitSkinService.KeepSuspended();   // 每进一个阶段给挂起续期（见 PortraitSkinService）
        var curStaging = FindStagingByVersionLabel(currentGameVersion);
        var gameModsHasEntries = Directory.Exists(gameMods)
            && Directory.EnumerateFileSystemEntries(gameMods).Any();
        // 兜底：目标版本 == 当前版本时，「游戏里的 Mods」本身就是「这个版本的 Mods」——
        // ①/①a 会刻意跳过搬运（目录不能搬给自己），③ 若照旧清空再铺快照，用户在 Steam
        // 官方目录里手装的 mod 就原地消失且零备份（沙箱实测：3 个 → 0 个）。
        var sameVersionApply = curStaging is not null
            && string.Equals(Path.GetFullPath(curStaging), Path.GetFullPath(staging),
                StringComparison.OrdinalIgnoreCase);

        // ⓪ 当前版本还没有包 → 把整个游戏目录改名成它的包（同盘改名≈瞬时，且要么全成要么不动）。
        // 官方原版用户第一次切走就是这个场景：以前 ② 直接把旧本体删掉，想回官方最新版只能
        // 回 Steam 校验（用户的原话是"不应该上来就把用户下载的内容自动缓存起来吗"）。
        // 只在「这个版本可以有 SMAPI」时整目录收 —— Mods 跟着一起进包才是它自己的 Mods；
        // 无 SMAPI 的版本（1.0/1.1）仍走 ① 的 _no-smapi 路径，免得把上一版本残留写进 1.0 的抽屉。
        if (!sameVersionApply && curStaging is null
            && !string.IsNullOrWhiteSpace(currentGameVersion)
            && UpdateService.RecommendSmapiTag(currentGameVersion) is not null
            && Directory.Exists(gamePath))
        {
            var adopted = TryAdoptCurrentBody(gamePath, staging, currentGameVersion!, progress);
            if (adopted is not null)
            {
                curStaging = adopted;
                adoptedPkg = adopted;
                Directory.CreateDirectory(gamePath);   // ②/③ 接下来按"空目录"往里写目标版本
            }
        }

        // ① 当前 Mods → 当前版本的 staging（按真实版本号找目录）。
        // 1.0/1.1 等无适配 SMAPI 的版本：不把上一版本残留写进该版本缓存，
        // 否则切回 1.0 会「恢复」根本不属于它的 1.6 Mods。
        try
        {
            var canUseMods = UpdateService.RecommendSmapiTag(currentGameVersion) is not null;
            var hasGameMods = gameModsHasEntries;

            if (hasGameMods && !canUseMods)
            {
                if (!HasMovableEntries(gameMods))
                {
                    // 只剩回收站就别建批次目录：2026-09-19 23:27 留下的 Mods-20260919_232740
                    // 是个 0 项空桶（源里只有 .junigrid_trash，被 skipTrash 跳掉）
                    AppLog.Warn("DepotDownloader",
                        $"当前版本无 SMAPI（{currentGameVersion}），Mods 里只剩回收站目录，不生成空批次");
                }
                else
                {
                    // 带时间戳：这个位置从不自动放回游戏，若固定用同一个路径，下一次切换的
                    // 移动会先把上一批整棵删掉（用户 2026-09-19 问的「备份是否被其它备份污染」
                    // 在这里成立）。每批各留一份，由存储清理页回收。
                    var leftover = Path.Combine(StagingRoot, "_mods-orphan", "_no-smapi",
                        SafeDirName(currentGameVersion ?? "unknown"),
                        "Mods-" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                    if (MoveDirectoryVerified(gameMods, leftover, null, progress, "移出 Mods") == DirMove.Failed)
                        modsMoveError =
                            $"当前版本（{currentGameVersion}）没有适配的 SMAPI，必须先把 Mods 移出游戏目录，" +
                            "但移动没通过核对 —— 已保留原目录未动。请清理磁盘空间、或关闭正在占用 Mods 的程序后重试。";
                    else modsMovedTo = leftover;
                    AppLog.Warn("DepotDownloader",
                        $"当前版本无 SMAPI（{currentGameVersion}），Mods 已移出游戏目录到 {leftover}，不写入该版本缓存");
                }
            }
            else if (HasMovableEntries(gameMods)   // 必须有真 mod：只剩回收站时若照旧存入，
                                                   // MoveAsideForeignEntries 会把抽屉里 112 个真 mod
                                                   // 当「不属于本次移动」全扫进 _copyleft（09-23 事故）
                && curStaging is not null
                && !string.Equals(Path.GetFullPath(curStaging), Path.GetFullPath(staging), StringComparison.OrdinalIgnoreCase))
            {
                var destMods = Path.Combine(curStaging, "Mods");
                // 兜底：源和抽屉对不上（半截/竞态）→ 跳过存入，抽屉原封不动
                if (LooksUnlikeSnapshot(gameMods, destMods))
                {
                    AppLog.Warn("DepotDownloader",
                        $"存入 Mods 跳过：当前 Mods 与 {currentGameVersion} 抽屉几乎对不上（疑似半截/竞态），抽屉未动");
                    progress?.Report(new Progress(
                        $"当前 Mods 与版本 {currentGameVersion} 的缓存对不上，已跳过存入（缓存里的 mod 未动）", null));
                }
                else
                {
                progress?.Report(new Progress($"正在把当前 Mods 存入版本 {currentGameVersion}…", null));
                // 抽屉里可能躺着上一批还没被认领走的条目（改名快路径会连整个 dest 删掉）→ 先挪隔离区
                var grave = Path.Combine(StagingRoot, "_mods-orphan", "_copyleft",
                    SafeDirName(currentGameVersion ?? "unknown") + "-" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                if (MoveDirectoryVerified(gameMods, destMods, grave, progress, "存入 Mods") == DirMove.Failed)
                    modsMoveError =
                        $"把当前 Mods 存入版本 {currentGameVersion} 的缓存时核对不通过 —— 已保留游戏目录里的 Mods 未动。" +
                        "请清理磁盘空间或关闭占用 Mods 的程序后重试；实在不行先到 Mods 页手动备份。";
                else
                {
                    modsMovedTo = destMods;
                    AppLog.Warn("DepotDownloader", $"Mods 已移入 {curStaging}");
                    WriteSizeCache(curStaging);
                }
                }
            }
        }
        catch (Exception ex) { AppLog.Warn("DepotDownloader", "存当前 Mods 失败: " + ex.Message); }

        // ① 的移动没兑现（拷贝后核对不过，源被完整保留）→ 现在就停。后面 ②/③ 一定会清游戏目录，
        // 让一份没核对过的副本去顶现役 Mods，正是"mod 自己没了"的成因。此时游戏目录一个字节没动。
        if (modsMoveError is not null)
        {
            AppLog.Warn("DepotDownloader", modsMoveError);
            throw new DepotException(modsMoveError);
        }

        // ①a 有对应版本但还没 staging（或官方目录）时，Mods 没地方收 —— 存进孤儿备份
        try
        {
            var canUseMods = UpdateService.RecommendSmapiTag(currentGameVersion) is not null;
            if (canUseMods
                && Directory.Exists(gameMods) && HasMovableEntries(gameMods)
                && FindStagingByVersionLabel(currentGameVersion) is null)
            {
                var orphanMods = Path.Combine(StagingRoot, "_mods-orphan",
                    SafeDirName(currentGameVersion ?? "unknown"), "Mods");
                var grave = Path.Combine(StagingRoot, "_mods-orphan", "_copyleft",
                    SafeDirName(currentGameVersion ?? "unknown") + "-" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                if (MoveDirectoryVerified(gameMods, orphanMods, grave, progress, "备份 Mods") == DirMove.Failed)
                {
                    AppLog.Warn("DepotDownloader", "Mods 移进孤儿备份时核对不通过，已保留游戏目录未动");
                    throw new DepotException(
                        $"把 Mods 移进孤儿备份 {orphanMods} 时核对不通过 —— 已保留游戏目录里的 Mods 未动，" +
                        "请清理磁盘空间或关闭占用 Mods 的程序后重试。");
                }
                modsMovedTo = orphanMods;
                AppLog.Warn("DepotDownloader", $"Mods 已移入孤儿备份 {orphanMods}");
            }
        }
        catch (DepotException) { throw; }
        catch (Exception ex) { AppLog.Warn("DepotDownloader", "存孤儿 Mods 失败: " + ex.Message); }

        // ①d 兜底：②/③ 接下来一定会清掉游戏 Mods。若 ①/①a 什么都没落地
        // （搬迁与拷贝双双抛错时只留下一行「存当前 Mods 失败」就继续往下），这份 Mods 就真没了。
        // 清盘前最后确认一次：还在游戏里、又没有任何已落地的备份 → 保底拷一份；
        // 连保底都失败 → 中止切换，别动游戏目录。
        // 判据用 HasMovableEntries 而不是 gameModsHasEntries：只剩回收站时 ①/①a 刻意不生成批次，
        // 这里若还算「有条目」，保底就拷出一个空目录 → 判"备份目录为空" → 把正常切换拦死。
        if (!sameVersionApply && modsMovedTo is null && HasMovableEntries(gameMods))
        {
            var safety = Path.Combine(StagingRoot, "_mods-orphan", "_safety",
                SafeDirName(currentGameVersion ?? "unknown") + "-" + DateTime.Now.ToString("yyyyMMdd_HHmmss"), "Mods");
            string? safetyErr = null;
            try
            {
                ParallelCopyDirectory(gameMods, safety, skipTrash: true, progress, "保底备份 Mods");
                if (!Directory.Exists(safety) || !Directory.EnumerateFileSystemEntries(safety).Any())
                    safetyErr = "备份目录为空";
            }
            catch (Exception ex) { safetyErr = ex.Message; }

            if (safetyErr is not null)
                throw new DepotException(
                    $"当前 Mods 无法备份到 {safety}（{safetyErr}），已中止切换 —— 游戏目录未被改动。" +
                    "请检查缓存所在磁盘的剩余空间与写入权限后重试。");
            modsMovedTo = safety;
            AppLog.Warn("DepotDownloader", $"Mods 保底快照已写入 {safety}");
        }

        // ①b 清盘前把当前 SMAPI 打进共享池 + 当前版本包。
        // v1.6.8：版本包自包含 —— <staging>\smapi 与 ④ 的 targetSmapi 对应，
        // 恢复时优先用包内这份，共享池降级为兜底（用户诉求：每个版本的缓存内容
        // 都收在自己文件夹里，公共区只留真正跨版本的东西）。
        try { SaveSmapiToPool(gamePath, currentGameVersion); } catch { }
        try { SeedSmapiSampleMods(gamePath); } catch { }   // 趁 Mods 还是当前这套，先收 SMAPI 示例 mod
        try
        {
            if (curStaging is not null && IsCompleteSmapiSnapshot(gamePath)
                && !IsCompleteSmapiSnapshot(Path.Combine(curStaging, "smapi")))
            {
                var pkgSmapi = Path.Combine(curStaging, "smapi");
                try { if (Directory.Exists(pkgSmapi)) Directory.Delete(pkgSmapi, true); } catch { }
                CopySmapiFiles(gamePath, pkgSmapi);
                AppLog.Warn("DepotDownloader", $"SMAPI 已存入版本包 {pkgSmapi}");
            }
        }
        catch (Exception ex) { AppLog.Warn("DepotDownloader", "存 SMAPI 到版本包失败: " + ex.Message); }

        // ①c userdata 备份（节流 + 只留最近几份）
        try { BackupSteamUserData("413150"); } catch (Exception ex)
        {
            AppLog.Warn("DepotDownloader", "备份 Steam userdata 失败: " + ex.Message);
        }

        PortraitSkinService.KeepSuspended();   // 每进一个阶段给挂起续期（见 PortraitSkinService）
        // ② 清理并写入本体。失败时若 Mods 已改名迁出，先迁回。
        try
        {
            progress?.Report(new Progress("正在清理旧游戏文件…", null));
            // 整棵改名挪走（同盘≈瞬时）—— 以前逐文件 Delete Content 上万个小文件是切换最慢的一步。
            // 后台再慢慢删挪走的那棵，不挡用户。
            var oldBody = Path.Combine(gamePath, ".junigrid-old-body");
            try { if (Directory.Exists(oldBody)) DeleteTreeParallel(oldBody); } catch { }
            Directory.CreateDirectory(oldBody);
            foreach (var entry in Directory.EnumerateFileSystemEntries(gamePath))
            {
                var name = Path.GetFileName(entry);
                if (name.Equals("Mods", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals(".junigrid-old-body", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var dest = Path.Combine(oldBody, name);
                    if (Directory.Exists(entry)) Directory.Move(entry, dest);
                    else File.Move(entry, dest);
                }
                catch (Exception ex)
                {
                    // 改名失败再删，语义仍是清掉旧文件
                    try
                    {
                        if (Directory.Exists(entry)) DeleteTreeParallel(entry);
                        else File.Delete(entry);
                    }
                    catch (Exception ex2)
                    {
                        throw new DepotException($"无法删除 {name}（文件可能被 Steam/游戏占用，请先退出后重试）：{ex2.Message}");
                    }
                    _ = ex;
                }
            }
            _ = Task.Run(() =>
            {
                try { if (Directory.Exists(oldBody)) DeleteTreeParallel(oldBody); } catch { }
            });

            progress?.Report(new Progress("正在写入目标版本文件…", null));
            CopyGameBodyParallel(staging, gamePath, progress);
        }
        catch
        {
            if (modsMovedTo is not null && !Directory.Exists(gameMods))
            {
                try { TryRelocateDirectory(modsMovedTo, gameMods); } catch { }
            }
            if (adoptedPkg is not null)
                AppLog.Warn("DepotDownloader",
                    $"切换中断：旧版本整份保存在 {adoptedPkg}，在版本管理里点那个版本号就能原样放回");
            throw;
        }

        PortraitSkinService.KeepSuspended();   // 每进一个阶段给挂起续期（见 PortraitSkinService）
        // ③ 恢复目标版本的 Mods：同盘优先从 staging 整棵改名进来
        progress?.Report(new Progress("正在恢复该版本的 Mods…", null));
        try
        {
            // 「该版本能不能有 Mods」必须提到分支之前判：无适配 SMAPI 的版本（1.0/1.1…）
            // 即使 staging 里躺着一份 Mods（历史上被误存进来的），也绝不能铺回游戏目录 ——
            // 否则 1.0 会显示/加载 1.6 的 mod（实测首页 143 个）。
            var targetVer = TryReadGameVersion(staging);
            var targetCanUseMods = UpdateService.RecommendSmapiTag(targetVer) is not null;

            var targetHasMods = targetCanUseMods
                && Directory.Exists(targetMods)
                && Directory.EnumerateFileSystemEntries(targetMods).Any();
            if (sameVersionApply && gameModsHasEntries)
            {
                // 原地保留：游戏里的就是这版本的，现有条目全都算「合法」
                if (Directory.Exists(gameMods))
                    foreach (var n in Directory.EnumerateFileSystemEntries(gameMods))
                        allowedLiveMods.Add(Path.GetFileName(n));
                AppLog.Warn("DepotDownloader",
                    $"目标与当前版本一致（{currentGameVersion}），Mods 保持游戏目录现状：不清空、也不用快照覆盖（{targetMods} 原样留着）");
            }
            else if (targetHasMods)
            {
                foreach (var n in Directory.EnumerateFileSystemEntries(targetMods))
                    allowedLiveMods.Add(Path.GetFileName(n));
                if (Directory.Exists(gameMods)) ClearDirectoryParallel(gameMods);
                if (Directory.Exists(gameMods))
                {
                    try { Directory.Delete(gameMods, true); } catch { }
                }
                if (MoveDirectoryVerified(targetMods, gameMods, null, progress, "恢复 Mods") == DirMove.Failed)
                {
                    // 到这里本体和 SMAPI 已经换成目标版本了，中止只会更糟 —— 说清楚就好：
                    // 残缺的落点被挪开，源（该版本抽屉）完整保留，mod 一个没丢
                    AppLog.Warn("DepotDownloader", $"恢复 Mods 核对不通过，源完整保留在 {targetMods}");
                    progress?.Report(new Progress(
                        $"该版本的 Mods 没能完整恢复到游戏目录（源仍完整保留在 {targetMods}）—— " +
                        "请清理磁盘空间或关闭占用 Mods 的程序后，重新切换一次这个版本。", null));
                }
                else WriteSizeCache(staging);
            }
            else
            {
                // 目标版本没有可用 Mods → 游戏目录必须先清空。
                // 同盘「改名迁移」失败时会走拷贝（源目录还在），若这里只 CreateDirectory
                // 不清盘，上一版本的 Mods 会原样留在 1.0 这类空版本上（实测 143 个）。
                if (Directory.Exists(gameMods))
                {
                    ClearDirectoryParallel(gameMods);
                    try { Directory.Delete(gameMods, true); } catch { }
                }
                Directory.CreateDirectory(gameMods);

                if (!targetCanUseMods)
                {
                    AppLog.Warn("DepotDownloader",
                        $"目标版本无 SMAPI（{targetVer ?? "?"}），Mods 保持为空");
                    if (Directory.Exists(targetMods)
                        && Directory.EnumerateFileSystemEntries(targetMods).Any())
                        AppLog.Warn("DepotDownloader",
                            $"该版本缓存里有历史误存的 Mods（{targetMods}），已忽略未恢复");
                }
                else
                {
                    var orphanMods = FindOrphanModsDir(targetVer);
                    if (orphanMods is not null)
                    {
                        foreach (var n in Directory.EnumerateFileSystemEntries(orphanMods))
                            allowedLiveMods.Add(Path.GetFileName(n));
                        if (MoveDirectoryVerified(orphanMods, gameMods, null, progress, "恢复 Mods") == DirMove.Failed)
                        {
                            AppLog.Warn("DepotDownloader", $"从孤儿备份恢复 Mods 核对不通过，源保留在 {orphanMods}");
                            progress?.Report(new Progress(
                                $"孤儿区的 Mods 没能完整恢复（源仍保留在 {orphanMods}）—— 请清理磁盘后重新切换一次。", null));
                        }
                        else AppLog.Warn("DepotDownloader", $"Mods 已从孤儿备份恢复 {orphanMods}");
                    }
                    else
                    {
                        AppLog.Warn("DepotDownloader",
                            $"目标版本无缓存 Mods，游戏 Mods 已清空（game={targetVer ?? "?"}）");

                        // unknown 黑洞兜底：currentGameVersion 读不出来时 Mods 被收进
                        // _mods-orphan 下的 unknown 目录，而恢复是按目标版本号查的 → 永远查不到
                        // （2026-09-18 08:31 实测发生过）。这里不自动并回 —— 自动并回正是当年
                        // 1.0 长出 114 个 1.6 mod 的成因；只把它报到界面上让人自己决定。
                        try
                        {
                            var blind = Path.Combine(StagingRoot, "_mods-orphan", "unknown", "Mods");
                            if (Directory.Exists(blind))
                            {
                                var pending = Directory.EnumerateFileSystemEntries(blind).Count();
                                if (pending > 0)
                                {
                                    progress?.Report(new Progress(
                                        $"另有 {pending} 项 mod 被收在 unknown 区（当时读不出版本号），未自动放回：{blind}", null));
                                    AppLog.Warn("DepotDownloader",
                                        $"unknown 孤儿区有 {pending} 项待认领：{blind}");
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
        }
        catch (Exception ex) { AppLog.Warn("DepotDownloader", "恢复 Mods 失败: " + ex.Message); }

        // ③z 在途复制的收尾回收：闸门只保证「按下切换的那一刻起 1.2 秒没动静」，
        // 复制暂停后又恢复就会把文件落进「新版本」的 Mods。必须放在 ④ 之前 ——
        // SMAPI 快照自带 Mods\ConsoleCommands 这类条目，④ 之后再比基线会把它们当成外来文件抢走。
        if (!sameVersionApply)
        {
            try
            {
                ReclaimInFlightMods(gameMods, allowedLiveMods,
                    modsMovedTo, currentGameVersion, progress);
            }
            catch (Exception ex) { AppLog.Warn("DepotDownloader", "在途 Mods 回收失败: " + ex.Message); }
        }


        // 移动语义没完全兑现时（改名失败 → 拷贝 → 源壳子被占用删不掉）屏幕上是没有痕迹的：
        // 抽屉里已经有一份完整的，游戏目录里还躺着删不掉的旧目录。必须报出来，
        // 否则下一次切换会拿它当"这个版本新装的 mod"再归档一遍。
        var stuck = Directory.Exists(gameMods)
            ? Directory.EnumerateFileSystemEntries(gameMods)
                .Select(e => Path.GetFileName(e.TrimEnd(Path.DirectorySeparatorChar)))
                .Where(n => !n.Equals(".junigrid_trash", StringComparison.OrdinalIgnoreCase)
                            && !allowedLiveMods.Contains(n))
                .ToList()
            : new List<string>();
        if (stuck.Count > 0)
        {
            AppLog.Warn("DepotDownloader",
                $"切换完成，但游戏 Mods 里还有 {stuck.Count} 项没被认领：" + string.Join("、", stuck));
            // 不上屏：这只是"原地留了个删不掉的壳"，不影响生效；要查就查上面那行日志。
        }

        // ④ 恢复 SMAPI —— 必须跟目标游戏版本走：
        //    1.6+ 才恢复 modern 池；1.2–1.5 恢复对应 legacy 桶；
        //    1.0/1.1 无适配 SMAPI，绝不能把 1.6 的 4.x 装回去。
        var targetGameVer = TryReadGameVersion(staging);
        var smapiTag = UpdateService.RecommendSmapiTag(targetGameVer);
        var restoredFrom = (string?)null;
        if (smapiTag is not null)
        {
            if (Directory.Exists(targetSmapi))
            {
                if (!IsCompleteSmapiSnapshot(targetSmapi))
                {
                    // 旧版逻辑攒下的残缺缓存（裸 exe）—— 清掉，让位给共享池/切换后补装
                    try { Directory.Delete(targetSmapi, true); } catch { }
                }
                else
                {
                    progress?.Report(new Progress("正在恢复该版本的 SMAPI…", null));
                    try
                    {
                        // 走 ParallelCopyDirectory：同盘时硬链接、跨盘时 robocopy /MT，
                        // 都比原来逐文件串行 File.Copy 快，且与「直接应用本体/共享池」同一条口径。
                        ParallelCopyDirectory(targetSmapi, gamePath, skipTrash: false, progress, "恢复 SMAPI");
                        restoredFrom = "版本缓存";
                    }
                    catch (Exception ex) { AppLog.Warn("DepotDownloader", "恢复 SMAPI 失败: " + ex.Message); }
                }
            }
            if (restoredFrom is null && RestoreSmapiFromPool(gamePath, targetGameVer))
            {
                progress?.Report(new Progress("正在放回本机已有的 SMAPI（不用重新下载）…", null));
                restoredFrom = "共享池";
            }
        }

        if (restoredFrom is not null)
        {
            AppLog.Warn("DepotDownloader", $"SMAPI 已恢复（来源：{restoredFrom}，游戏 {targetGameVer}）");
            // 示例 mod 在 Mods/ 里，不跟 SMAPI exe 走 —— 切完补上，避免「能跑 SMAPI 却没有 Console Commands」
            EnsureSmapiSampleMods(gamePath);
            // 立绘覆盖也是写在当前 Mods 里的，换版本后重新生成
            Task.Run(() =>
            {
                try
                {
                    var portraits = PortraitSkinService.Live;
                    if (portraits is not null)
                    {
                        var scan = portraits.Scan(gamePath);
                        portraits.SyncToDisk(gamePath, scan);
                        AppLog.Warn("DepotDownloader", "已按当前 Mods 重新生成立绘覆盖包");
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warn("DepotDownloader", "切版本后重建立绘覆盖失败: " + ex.Message);
                }
            });
        }
        else if (smapiTag is null)
            AppLog.Warn("DepotDownloader", $"游戏 {targetGameVer} 无适配 SMAPI，切换后不装加载器");
        else
        {
            // 该版本有适配 SMAPI，但版本缓存和共享池都没能给出来，而 ② 已经把原来那份删了。
            // 静默过去的后果是用户点「启动」才发现（实测两次「未检测到 SMAPI」）→ 当场说。
            progress?.Report(new Progress(
                $"该版本的 SMAPI 未能恢复（缓存与共享池均不可用），请到 Mods 页重装 SMAPI 再启动", null));
            AppLog.Error("DepotDownloader",
                $"游戏 {targetGameVer} 应有 SMAPI（{smapiTag}）但版本缓存与共享池都没恢复出来，需重装 SMAPI");
        }
    }

    /// <summary>
    /// 并行拷贝整棵目录树。Mods 包动辄上千文件，串行递归 File.Copy 是切版本主要卡点。
    /// skipTrash：跳过 Mods 回收站（可达数 GB，切版本不需要跟着搬）。
    /// </summary>
    private static void ParallelCopyDirectory(string src, string dest, bool skipTrash,
        IProgress<Progress>? progress = null, string label = "复制")
    {
        if (!Directory.Exists(src)) return;
        Directory.CreateDirectory(dest);

        var sameVol = string.Equals(Path.GetPathRoot(Path.GetFullPath(src)),
            Path.GetPathRoot(Path.GetFullPath(dest)), StringComparison.OrdinalIgnoreCase);
        // 同盘优先硬链接：Mods 整包可达 GB 级，字节拷贝是「直接应用」阶段最慢的一步
        if (sameVol)
        {
            // 目录一次性建好 + 硬链接并行。旧写法每个文件串行做 CreateDirectory(父)+File.Exists+
            // File.Delete+CreateHardLink 四次系统调用，上千文件累积实测 8965ms（2026-09-23 03:25 存入 Mods），
            // 名为 Parallel 实则全串行。改成：目录一遍建完；每文件只试硬链接，失败(多为 dest 已存在)才删了重试。
            try
            {
                foreach (var d in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
                {
                    var relD = Path.GetRelativePath(src, d);
                    if (skipTrash && relD.Contains("junigrid_trash", StringComparison.OrdinalIgnoreCase)) continue;
                    Directory.CreateDirectory(Path.Combine(dest, relD));
                }
            }
            catch { }

            long linked = 0;
            var srcFiles = Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories)
                .Where(f => !(skipTrash && Path.GetRelativePath(src, f)
                    .Contains("junigrid_trash", StringComparison.OrdinalIgnoreCase)))
                .ToList();
            Parallel.ForEach(srcFiles, f =>
            {
                var to = Path.Combine(dest, Path.GetRelativePath(src, f));
                try
                {
                    if (CreateHardLink(to, f, IntPtr.Zero)) { Interlocked.Increment(ref linked); return; }
                    try { File.Delete(to); } catch { }
                    if (CreateHardLink(to, f, IntPtr.Zero)) { Interlocked.Increment(ref linked); return; }
                    File.Copy(f, to, overwrite: true);
                }
                catch
                {
                    try { File.Copy(f, to, overwrite: true); } catch { }
                }
            });
            if (linked > 0)
            {
                progress?.Report(new Progress($"{label}… 100%（硬链接）", 100));
                return;
            }
        }

        // robocopy /MT 对上千小文件明显快于托管 File.Copy
        if (TryRobocopy(src, dest))
        {
            progress?.Report(new Progress($"{label}… 100%", 100));
            return;
        }

        var roots = new Queue<string>();
        roots.Enqueue(src);
        var files = new List<string>();
        while (roots.Count > 0)
        {
            var dir = roots.Dequeue();
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(d);
                if (skipTrash && (name.Equals("junigrid_trash", StringComparison.OrdinalIgnoreCase)
                               || name.Equals(".junigrid_trash", StringComparison.OrdinalIgnoreCase)))
                    continue;
                roots.Enqueue(d);
            }
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var rel = Path.GetRelativePath(src, f);
                if (skipTrash && (rel.Contains("junigrid_trash", StringComparison.OrdinalIgnoreCase)
                               || rel.Contains(".junigrid_trash", StringComparison.OrdinalIgnoreCase)))
                    continue;
                files.Add(f);
            }
        }

        long done = 0;
        var total = files.Count;
        if (total == 0) return;

        var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            var targetDir = Path.GetDirectoryName(Path.Combine(dest, Path.GetRelativePath(src, f)));
            if (!string.IsNullOrEmpty(targetDir)) parents.Add(targetDir);
        }
        foreach (var d in parents) Directory.CreateDirectory(d);

        var step = Math.Max(1, total / 8);
        long nextReport = step;
        var reportGate = new object();

        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = IoParallelism }, file =>
        {
            var rel = Path.GetRelativePath(src, file);
            var target = Path.Combine(dest, rel);
            File.Copy(file, target, overwrite: true);
            var n = Interlocked.Increment(ref done);
            var shouldReport = false;
            lock (reportGate)
            {
                if (n == total || n >= nextReport)
                {
                    nextReport = n + step;
                    shouldReport = true;
                }
            }
            if (shouldReport)
                progress?.Report(new Progress($"{label}… {(int)(n * 100.0 / total)}%", (int)(n * 100.0 / total)));
        });
    }

    private static void CopyGameBodyParallel(string staging, string gamePath, IProgress<Progress>? progress)
    {
        var stagingFull = Path.GetFullPath(staging);
        var gameFull = Path.GetFullPath(gamePath);
        var sameVolume = string.Equals(
            Path.GetPathRoot(stagingFull), Path.GetPathRoot(gameFull),
            StringComparison.OrdinalIgnoreCase);

        var files = new List<string>();
        foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(staging, file);
            if (rel.StartsWith(".DepotDownloader", StringComparison.OrdinalIgnoreCase)) continue;
            if (rel.Equals(ManifestMetaName, StringComparison.OrdinalIgnoreCase)) continue;
            if (Path.GetFileName(rel).Equals(".junigrid-size", StringComparison.OrdinalIgnoreCase)) continue;
            if (Path.GetFileName(rel).Equals(".junigrid-version", StringComparison.OrdinalIgnoreCase)) continue;
            if (Path.GetFileName(rel).Equals(CompleteMarkName, StringComparison.OrdinalIgnoreCase)) continue;
            if (rel.StartsWith("Mods" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith("Mods/", StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith("smapi" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith("smapi/", StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith("smapi-installer", StringComparison.OrdinalIgnoreCase))
                continue;
            files.Add(file);
        }

        // 同盘优先硬链接：游戏本体只读使用，链接≈瞬时，切版本从「拷几百 MB」变成「建目录项」。
        // 链接失败（跨盘/权限/不支持）再整树 robocopy，最后才逐文件拷贝。
        if (sameVolume)
        {
            long linked = 0, failed = 0;
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = IoParallelism }, file =>
            {
                var rel = Path.GetRelativePath(staging, file);
                var target = Path.Combine(gamePath, rel);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (File.Exists(target)) File.Delete(target);
                    if (CreateHardLink(target, file, IntPtr.Zero))
                        Interlocked.Increment(ref linked);
                    else
                    {
                        File.Copy(file, target, overwrite: true);
                        Interlocked.Increment(ref failed);
                    }
                }
                catch
                {
                    try { File.Copy(file, Path.Combine(gamePath, rel), overwrite: true); Interlocked.Increment(ref failed); }
                    catch { Interlocked.Increment(ref failed); }
                }
            });
            progress?.Report(new Progress(
                failed == 0
                    ? $"正在写入目标版本文件… 100%（硬链接 {linked} 个，几乎不占额外空间）"
                    : $"正在写入目标版本文件… 100%（链接 {linked} / 拷贝 {failed}）",
                100));
            return;
        }

        if (TryRobocopy(staging, gamePath,
                "/XD Mods smapi smapi-installer .DepotDownloader /XF .junigrid-manifest .junigrid-size .junigrid-version .junigrid-complete"))
        {
            progress?.Report(new Progress("正在写入目标版本文件… 100%", 100));
            return;
        }

        long done = 0;
        var total = files.Count;
        var gate = new object();
        var errors = new List<Exception>();
        var step = Math.Max(1, total / 10);
        long nextReport = step;

        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = IoParallelism }, file =>
        {
            try
            {
                var rel = Path.GetRelativePath(staging, file);
                var dest = Path.Combine(gamePath, rel);
                CopyGameFileFast(file, dest);
            }
            catch (Exception ex)
            {
                lock (gate) errors.Add(ex);
            }
            var n = Interlocked.Increment(ref done);
            var shouldReport = false;
            lock (gate)
            {
                if (n == total || n >= nextReport)
                {
                    nextReport = n + step;
                    shouldReport = true;
                }
            }
            if (shouldReport)
                progress?.Report(new Progress($"正在写入目标版本文件… {(int)(n * 100.0 / total)}%", (int)(n * 100.0 / total)));
        });

        if (errors.Count > 0)
            throw new DepotException($"写入游戏文件失败（{errors.Count} 个）：{errors[0].Message}");
    }

    /// <summary>按真实版本号找 staging 目录（文件夹名后缀或包内 FileVersion）。</summary>
    /// <summary>找该版本的孤儿 Mods 目录。历史上这些目录是按 4 段 FileVersion 命名的
    /// （实测 _mods-orphan 下躺着 1.6.15.24356 / 1.2.6338.29417 / 1.3.7269.37809 三个），
    /// 而恢复按 3 段查 → 永远对不上，mod 一去不回。先按精确名查，查不到再认领
    /// 「同一版本 + 更长构建号」的目录：前缀带点，所以 1.6.15 不会误吞 1.6.150.x，
    /// 更不会把别的版本的 mod 并进来（那正是当年 1.0 长出 114 个 1.6 mod 的成因）。</summary>
    private static string? FindOrphanModsDir(string? targetVer)
    {
        var root = Path.Combine(StagingRoot, "_mods-orphan");
        if (string.IsNullOrWhiteSpace(targetVer) || !Directory.Exists(root)) return null;
        string? NonEmpty(string name)
        {
            var d = Path.Combine(root, name, "Mods");
            return Directory.Exists(d) && Directory.EnumerateFileSystemEntries(d).Any() ? d : null;
        }
        var key = SafeDirName(targetVer);
        var exact = NonEmpty(key);
        if (exact is not null) return exact;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var nm = Path.GetFileName(dir);
            if (!nm.StartsWith(key + ".", StringComparison.OrdinalIgnoreCase)) continue;
            var hit = NonEmpty(nm);
            if (hit is not null) return hit;
        }
        // v1.4.7：_copyleft\<ver>-<时间戳> 批次 —— MoveAsideForeignEntries 清出来的
        // 「抽屉里多出来的」整批躺在这里（mod 直接在批次目录下，没有 Mods 子层），
        // 旧逻辑只认 _mods-orphan\<ver>\Mods，这批切回版本永远回不来。
        var left = Path.Combine(root, "_copyleft");
        if (Directory.Exists(left))
        {
            foreach (var b in Directory.EnumerateDirectories(left))
            {
                var nm = Path.GetFileName(b);
                // 1.6.15-20260923_192820 / 1.6.15.24356-… 都算这一版的
                if (!nm.StartsWith(key + "-", StringComparison.OrdinalIgnoreCase)
                    && !nm.StartsWith(key + ".", StringComparison.OrdinalIgnoreCase)) continue;
                if (Directory.EnumerateFileSystemEntries(b).Any()) return b;
            }
        }
        return null;
    }

    /// <summary>SMAPI 自带的基件 mod（不是用户装的 mod，孤儿区里出现即可回收）。
    /// 各版本 SMAPI 带的基件不完全一样（4.x 只有 ConsoleCommands/SaveBackup，2.x/3.x 还有
    /// ErrorHandler/DebugMode），所以名单放宽：多列几个不会误伤 —— 用户 mod 的文件夹名
    /// 撞不上这些 UniqueID 风格的名字。</summary>
    internal static readonly HashSet<string> SmapiBaseMods = new(StringComparer.OrdinalIgnoreCase)
    {
        "ConsoleCommands", "SaveBackup", "ErrorHandler", "DebugMode", "UpdateChecks", "MultiplayerFix",
    };

    /// <summary>mod 的 manifest.MinimumApiVersion 是 <b>SMAPI</b> 版本（不是游戏版本）：
    /// 4.x 配游戏 1.6+、3.x 配 1.4–1.5、2.x 配 1.3。取不出来返回 0（判不了）。</summary>
    internal static int SmapiMajorOfModFolder(string modDir)
    {
        try
        {
            var mf = Path.Combine(modDir, "manifest.json");
            if (!File.Exists(mf)) return 0;
            var j = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(mf));
            var raw = j["MinimumApiVersion"]?.ToString() ?? j["MinimumApiVersionForSMAPI"]?.ToString();
            if (string.IsNullOrWhiteSpace(raw)) return 0;
            return int.TryParse(raw.Split('.')[0].Trim(), out var major) ? major : 0;
        }
        catch { return 0; }
    }

    /// <summary>游戏版本 → SMAPI 大版本档（与 SmapiPoolBucket 同一口径，但这里只要数字档）。</summary>
    internal static int SmapiMajorOfGameVersion(string? gameVersion)
    {
        var tag = UpdateService.RecommendSmapiTag(gameVersion);
        if (tag is null) return 0;                       // 1.0/1.1 这类压根没有适配 SMAPI
        if (tag == UpdateService.SmapiLatest) return 4;
        return int.TryParse(tag.Split('.')[0].Trim(), out var major) ? major : 0;
    }

    /// <summary>
    /// 孤儿区（_mods-orphan）自动归位 —— 它本该只收"归属失败"的东西，攒着不管就是死空间。
    /// 逐个 mod 判（整批一起判会被一条拖累）：
    /// ① SMAPI 自带基件 → 不是用户内容，移进游戏卸载回收站；
    /// ② 按 manifest 的 SMAPI 档找<b>唯一</b>候选版本抽屉：抽屉里已有同名 = 重复 → 回收站；
    ///    没有同名 → 搬进该抽屉（归位）；
    /// ③ 候选为 0 个或多个 → 留在收件箱，绝不猜（当年"1.0 长出 114 个 1.6 mod"就是猜出来的）。
    /// 全程只移动不删除，每步写日志。返回一行摘要（无事可做返回 null）。
    /// </summary>
    public string? RehomeOrphanMods(string gamePath)
    {
        var root = Path.Combine(StagingRoot, "_mods-orphan");
        if (!Directory.Exists(root)) return null;

        // 候选抽屉按 SMAPI 档分组（同一档有多个版本时该档整体算"歧义"）
        var drawersByMajor = new Dictionary<int, List<string>>();   // major → 抽屉 Mods 路径列表
        foreach (var pkg in ListStagedPackages())
        {
            var major = SmapiMajorOfGameVersion(pkg.Label);
            if (major == 0) continue;
            if (!drawersByMajor.TryGetValue(major, out var lst))
                drawersByMajor[major] = lst = new List<string>();
            lst.Add(Path.Combine(pkg.Path, "Mods"));
        }
        return RehomeOrphanMods(gamePath, drawersByMajor);
    }

    /// <summary>抽屉表单独传进来：判定逻辑（唯一候选才动、重复/自带件进回收站、判不出就留）
    /// 要能脱离真实版本包单测，否则只能靠真下几个 GB 的版本包才能验。</summary>
    public static string? RehomeOrphanMods(string gamePath, Dictionary<int, List<string>> drawersByMajor)
    {
        var root = Path.Combine(StagingRoot, "_mods-orphan");
        if (!Directory.Exists(root)) return null;

        int rehomed = 0, trashed = 0, left = 0;
        var trash = string.IsNullOrWhiteSpace(gamePath) ? null : StoragePaths.GameTrashDir(gamePath);
        var gameMods = string.IsNullOrWhiteSpace(gamePath) ? null : Path.Combine(gamePath, "Mods");

        foreach (var modsRoot in EnumerateOrphanModsRoots(root))
        {
            List<string> entries;
            try { entries = Directory.GetDirectories(modsRoot).ToList(); } catch { continue; }
            foreach (var mod in entries)
            {
                var name = Path.GetFileName(mod);
                if (name.StartsWith('.')) continue;
                if (SmapiBaseMods.Contains(name)) { if (Trash(mod, trash)) trashed++; else left++; continue; }

                var major = SmapiMajorOfModFolder(mod);
                if (major == 0 || !drawersByMajor.TryGetValue(major, out var drawers) || drawers.Count != 1)
                { left++; continue; }                       // 判不了 / 没有或不止一个候选 → 不猜

                var dst = Path.Combine(drawers[0], name);
                var liveTwin = gameMods is not null && Directory.Exists(Path.Combine(gameMods, name));
                if (Directory.Exists(dst) || liveTwin)
                { if (Trash(mod, trash)) trashed++; else left++; continue; }   // 别处已有同名 = 重复

                try
                {
                    Directory.CreateDirectory(drawers[0]);
                    if (StorageService.TryMoveTree(mod, dst)) rehomed++;
                    else left++;
                }
                catch { left++; }
            }
            PruneEmptyOrphanDirs(modsRoot);
        }
        PruneEmptyOrphanDirs(root);

        if (rehomed == 0 && trashed == 0) return null;
        var line = $"孤儿 Mods 归位：搬回版本抽屉 {rehomed} 个，重复/自带件移入回收站 {trashed} 个，判不出归属留下 {left} 个";
        AppLog.Warn("DepotDownloader", line);
        return line;
    }

    /// <summary>孤儿区里所有"Mods 清单目录"（Mods 或 Mods-&lt;时间戳&gt;，含 _no-smapi/&lt;版本&gt; 这类两层嵌套）。</summary>
    private static List<string> EnumerateOrphanModsRoots(string root)
    {
        var found = new List<string>();
        void Walk(string dir, int depth)
        {
            string[] subs;
            try { subs = Directory.GetDirectories(dir); } catch { return; }
            foreach (var s in subs)
            {
                var n = Path.GetFileName(s);
                if (n.Equals("Mods", StringComparison.OrdinalIgnoreCase)
                    || n.StartsWith("Mods-", StringComparison.OrdinalIgnoreCase)) { found.Add(s); continue; }
                if (depth < 3) Walk(s, depth + 1);
            }
        }
        Walk(root, 0);
        return found;
    }

    private static bool Trash(string modDir, string? trash)
    {
        if (trash is null) return false;
        try
        {
            Directory.CreateDirectory(trash);
            var dst = Path.Combine(trash, Path.GetFileName(modDir)
                + "-orphan-" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            return StorageService.TryMoveTree(modDir, dst);
        }
        catch { return false; }
    }

    /// <summary>批次目录空了就摘掉（保留根，下次切换还要往里写）。</summary>
    private static void PruneEmptyOrphanDirs(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var sub in Directory.GetDirectories(dir)
                         .OrderByDescending(d => d.Length))   // 先深的，父目录才空得出来
                PruneEmptyOrphanDirs(sub);
            if (!Directory.EnumerateFileSystemEntries(dir).Any()
                && !string.Equals(dir, Path.Combine(StagingRoot, "_mods-orphan"), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(dir);
        }
        catch { }
    }

    /// <summary>某个游戏版本对应的缓存包目录（「版本抽屉」就建在它下面）。本机没这个版本的包 = null。</summary>
    public static string? StagingDirForVersion(string? version) => FindStagingByVersionLabel(version);

    private static string? FindStagingByVersionLabel(string? version)
    {
        if (string.IsNullOrWhiteSpace(version) || !Directory.Exists(StagingRoot)) return null;
        var v = version.Trim();
        foreach (var dir in Directory.EnumerateDirectories(StagingRoot))
        {
            if (Path.GetFileName(dir).StartsWith("_", StringComparison.Ordinal)) continue; // 共享池等内部目录
            var name = Path.GetFileName(dir);
            if (name.EndsWith("-" + v, StringComparison.OrdinalIgnoreCase)) return dir;
            var real = TryReadGameVersion(dir);
            if (real is not null && string.Equals(real, v, StringComparison.OrdinalIgnoreCase)) return dir;
        }
        return null;
    }

    // ─── SMAPI 共享池：按「游戏版本 ↔ SMAPI 兼容」分桶，切版本只恢复对应桶 ───
    // modern     = 游戏 1.6+（SMAPI 4.x / latest）
    // legacy-3   = 游戏 1.4–1.5（SMAPI 3.x）
    // legacy-2   = 游戏 1.2.30+ / 1.3（SMAPI 2.x）
    // unsupported= 游戏 1.0/1.1、1.2.29-、1.3.26-：没有可靠一键 SMAPI

    private static string SmapiPoolRoot => Path.Combine(StagingRoot, "_smapi-pool");

    private static string SmapiPoolBucket(string? gameVersion)
    {
        var tag = UpdateService.RecommendSmapiTag(gameVersion);
        if (tag is null) return "unsupported";
        if (tag == UpdateService.SmapiLatest) return "modern"; // 1.6+
        return "legacy-" + tag.Split('.')[0];                  // 2.x / 3.x
    }

    private static bool HasSmapiInstalled(string root)
        => File.Exists(Path.Combine(root, "StardewModdingAPI.exe"));

    /// <summary>SMAPI 安装器自带的示例 mod（在 Mods/ 里，不跟 exe 走）—— 切版本后要补上。</summary>
    private static readonly string[] SmapiSampleMods = ["ConsoleCommands", "SaveBackup"];

    private static string SmapiSamplesRoot => Path.Combine(SmapiPoolRoot, "_samples");

    /// <summary>把当前 Mods 里的 SMAPI 示例 mod 收进共享种子（安装 SMAPI / 缓存时调用）。</summary>
    public static void SeedSmapiSampleMods(string gamePath)
    {
        var mods = Path.Combine(gamePath, "Mods");
        if (!Directory.Exists(mods)) return;
        foreach (var name in SmapiSampleMods)
        {
            var src = Path.Combine(mods, name);
            if (!Directory.Exists(src)) continue;
            var dest = Path.Combine(SmapiSamplesRoot, name);
            try
            {
                Directory.CreateDirectory(SmapiSamplesRoot);
                if (Directory.Exists(dest)) Directory.Delete(dest, true);
                if (!TryRobocopy(src, dest))
                    ParallelCopyDirectory(src, dest, skipTrash: false);
            }
            catch (Exception ex)
            {
                AppLog.Warn("DepotDownloader", $"缓存 SMAPI 示例 mod「{name}」失败: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 切完版本后：SMAPI 在、但 Mods 里缺示例 mod 时，从共享种子补上。
    /// 没有种子里就从各版本包 Mods 里认领一份（首次切换的过渡）。
    /// </summary>
    public static void EnsureSmapiSampleMods(string gamePath)
    {
        if (!HasSmapiInstalled(gamePath)) return;
        SeedSmapiSampleMods(gamePath);   // 已有就先入种
        var mods = Path.Combine(gamePath, "Mods");
        Directory.CreateDirectory(mods);
        foreach (var name in SmapiSampleMods)
        {
            var dst = Path.Combine(mods, name);
            if (Directory.Exists(dst) && Directory.EnumerateFileSystemEntries(dst).Any()) continue;
            var src = Path.Combine(SmapiSamplesRoot, name);
            if (!Directory.Exists(src) || !Directory.EnumerateFileSystemEntries(src).Any())
                src = FindSampleModInStaging(name) ?? "";
            if (src.Length == 0 || !Directory.Exists(src)) continue;
            try
            {
                if (Directory.Exists(dst)) Directory.Delete(dst, true);
                if (!TryRobocopy(src, dst))
                    ParallelCopyDirectory(src, dst, skipTrash: false);
                AppLog.Warn("DepotDownloader", $"已补上 SMAPI 自带示例 mod：{name}");
            }
            catch (Exception ex)
            {
                AppLog.Warn("DepotDownloader", $"补 SMAPI 示例 mod「{name}」失败: {ex.Message}");
            }
        }
    }

    private static string? FindSampleModInStaging(string name)
    {
        try
        {
            if (!Directory.Exists(StagingRoot)) return null;
            foreach (var dir in Directory.EnumerateDirectories(StagingRoot))
            {
                if (Path.GetFileName(dir).StartsWith("_", StringComparison.Ordinal)) continue;
                var m = Path.Combine(dir, "Mods", name);
                if (Directory.Exists(m) && Directory.EnumerateFileSystemEntries(m).Any()) return m;
            }
        }
        catch { }
        return null;
    }

    private static void CopySmapiFiles(string fromRoot, string toRoot)
    {
        Directory.CreateDirectory(toRoot);
        var files = new List<string>();
        try { files.AddRange(Directory.EnumerateFiles(fromRoot, "StardewModdingAPI*")); } catch { }
        foreach (var n in new[] { "Newtonsoft.Json.dll", "Mono.Cecil.dll", "steam_appid.txt" })
        {
            var src = Path.Combine(fromRoot, n);
            if (File.Exists(src)) files.Add(src);
        }
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = IoParallelism }, f =>
        {
            try { File.Copy(f, Path.Combine(toRoot, Path.GetFileName(f)), overwrite: true); } catch { }
        });
        var smapiInternal = Path.Combine(fromRoot, "smapi-internal");
        if (Directory.Exists(smapiInternal))
        {
            var destInternal = Path.Combine(toRoot, "smapi-internal");
            if (!TryRobocopy(smapiInternal, destInternal))
                ParallelCopyDirectory(smapiInternal, destInternal, skipTrash: false);
        }
    }

    /// <summary>
    /// SMAPI 快照是否完整可用：exe 在，依赖也得在 —— 2.x 的 Newtonsoft.Json.dll 在顶层，
    /// 3.x/4.x 收在 smapi-internal/。旧版收集只留 StardewModdingAPI*，恢复出的裸 exe
    /// 一启动就崩，用这条识别并淘汰残缺快照。
    /// </summary>
    private static bool IsCompleteSmapiSnapshot(string dir)
        => HasSmapiInstalled(dir)
           && (Directory.Exists(Path.Combine(dir, "smapi-internal"))
               || File.Exists(Path.Combine(dir, "Newtonsoft.Json.dll")));

    // 共享池桶里的标记文件：第一行 = 存入时该游戏版本对应的 SMAPI tag（"2.5"/"3.7.3"/"latest"）。
    // 桶按大版本共享（1.2.30 与 1.3.36 同桶），恢复时要求 tag 与目标版本该用的完全一致。
    private const string SmapiPoolMetaName = "_junigrid-smapi.meta";

    private static void WriteSmapiPoolMeta(string bucketDir, string? gameVersion)
    {
        try
        {
            var tag = UpdateService.RecommendSmapiTag(gameVersion) ?? "unknown";
            File.WriteAllText(Path.Combine(bucketDir, SmapiPoolMetaName), tag + "\n" + (gameVersion ?? ""));
        }
        catch { }
    }

    private static string? ReadSmapiPoolTag(string bucketDir)
    {
        try
        {
            var path = Path.Combine(bucketDir, SmapiPoolMetaName);
            if (!File.Exists(path)) return null;
            var first = File.ReadLines(path).FirstOrDefault()?.Trim();
            return string.IsNullOrWhiteSpace(first) ? null : first;
        }
        catch { return null; }
    }

    /// <summary>把游戏目录里的 SMAPI 产物存进共享池（按当前游戏版本的兼容桶）。
    /// 1.0/1.1 无适配 SMAPI，不入池（避免把 1.6 的 4.x 误存成「1.0 用的」）。</summary>
    public static bool SaveSmapiToPool(string gamePath, string? gameVersion)
    {
        // 残缺快照（如切版本中间态的裸 exe）不入池，避免污染桶。
        // 但 ② 随后会把整个本体删掉 —— 用户手装的那一份不能跟着蒸发，
        // 兜底留一份 as-found（不参与桶选择，只供找回）。
        if (!IsCompleteSmapiSnapshot(gamePath))
        {
            if (HasSmapiInstalled(gamePath)) ArchivePartialSmapi(gamePath, gameVersion);
            return false;
        }
        var bucket = SmapiPoolBucket(gameVersion);
        if (bucket == "unsupported") return false;
        var dest = Path.Combine(SmapiPoolRoot, bucket);
        var want = UpdateService.RecommendSmapiTag(gameVersion);
        var have = ReadSmapiPoolTag(dest);
        if (have is not null && IsCompleteSmapiSnapshot(dest)
            && !string.IsNullOrWhiteSpace(want)
            && string.Equals(have, want, StringComparison.OrdinalIgnoreCase))
        {
            // 桶 tag 对 modern 恒为 "latest"，同桶里池内那份完全可能是另一个（更旧的）小版本。
            // 无条件早退等于：② 先把用户手上这份删掉，再从池里铺旧的 —— 用户那份就此消失。
            // 所以只有「确实是同一份构建」才走快路径。
            if (SameSmapiBuild(gamePath, dest)) return true;
            AppLog.Warn("DepotDownloader",
                $"共享池 {bucket} 与游戏目录里的 SMAPI 不是同一份构建，改用游戏目录这份");
        }
        try { if (Directory.Exists(dest)) Directory.Delete(dest, true); } catch { }
        CopySmapiFiles(gamePath, dest);
        WriteSmapiPoolMeta(dest, gameVersion);
        AppLog.Warn("DepotDownloader", $"SMAPI 已写入共享池 {dest}");
        return true;
    }

    /// <summary>两份 SMAPI 是否同一构建：比 StardewModdingAPI.exe 的字节数与 FileVersion。
    /// 任一读不到都按「不同」处理（宁可多覆盖一次桶，也不能把用户那份弄丢）。</summary>
    private static bool SameSmapiBuild(string fromRoot, string toRoot)
    {
        try
        {
            var a = Path.Combine(fromRoot, "StardewModdingAPI.exe");
            var b = Path.Combine(toRoot, "StardewModdingAPI.exe");
            if (!File.Exists(a) || !File.Exists(b)) return false;
            var fa = new FileInfo(a); var fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;
            return (System.Diagnostics.FileVersionInfo.GetVersionInfo(a).FileVersion ?? "")
                == (System.Diagnostics.FileVersionInfo.GetVersionInfo(b).FileVersion ?? "");
        }
        catch { return false; }
    }

    /// <summary>清盘前把「不成套但确实装了」的 SMAPI 原样留档到
    /// _smapi-pool/as-found/&lt;版本&gt;-&lt;时间戳&gt;。不参与桶选择，仅供人工找回。</summary>
    private static void ArchivePartialSmapi(string gamePath, string? gameVersion)
    {
        try
        {
            var dest = Path.Combine(SmapiPoolRoot, "as-found",
                SafeDirName(gameVersion ?? "unknown") + "-" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            CopySmapiFiles(gamePath, dest);
            AppLog.Warn("DepotDownloader",
                $"游戏目录里的 SMAPI 不成套，已原样留档到 {dest}（清盘后可从这里找回，或在 Mods 页重装）");
        }
        catch (Exception ex) { AppLog.Warn("DepotDownloader", "留档残缺 SMAPI 失败: " + ex.Message); }
    }

    /// <summary>从共享池恢复 SMAPI 到游戏目录。桶缺失/残缺/tag 不匹配则 false。</summary>
    public static bool RestoreSmapiFromPool(string gamePath, string? gameVersion)
    {
        var bucket = SmapiPoolBucket(gameVersion);
        if (bucket == "unsupported") return false;
        var src = Path.Combine(SmapiPoolRoot, bucket);
        // 无 meta = 旧版逻辑攒的快照（多半是裸 exe 残缺品）→ 清掉不信任
        var tag = ReadSmapiPoolTag(src);
        if (tag is null || !IsCompleteSmapiSnapshot(src))
        {
            try { if (Directory.Exists(src)) Directory.Delete(src, true); } catch { }
            return false;
        }
        // 桶按大版本共享：桶里的 SMAPI 必须正是目标游戏版本该用的 tag，否则宁可走重装
        var want = UpdateService.RecommendSmapiTag(gameVersion);
        if (string.IsNullOrWhiteSpace(want) || !string.Equals(tag, want, StringComparison.OrdinalIgnoreCase))
            return false;
        CopySmapiFiles(src, gamePath);
        AppLog.Warn("DepotDownloader", $"SMAPI 已从共享池恢复（bucket={bucket}）");
        return true;
    }


    /// <summary>
    /// 安装/更新 SMAPI 成功后：写进当前版本 staging + 共享池，切回官方/其它历史版都免重装。
    /// </summary>
    public static void CacheSmapiForCurrentGame(string gamePath)
    {
        try
        {
            var ver = TryReadGameVersion(gamePath);
            // 共享池（官方最新版可能没有对应 staging，必须走这里）
            SaveSmapiToPool(gamePath, ver);
            SeedSmapiSampleMods(gamePath);

            var staging = FindStagingByVersionLabel(ver);
            if (staging is null) return;
            var dest = Path.Combine(staging, "smapi");
            try { if (Directory.Exists(dest)) Directory.Delete(dest, true); } catch { }
            CopySmapiFiles(gamePath, dest);
            AppLog.Warn("DepotDownloader", $"SMAPI 已缓存到版本 {ver}：{dest}");
        }
        catch (Exception ex) { AppLog.Warn("DepotDownloader", "缓存 SMAPI 失败: " + ex.Message); }
    }

    /// <summary>SMAPI 安装包的复用缓存路径：认得出该版本且有抽屉 → 存进<b>该版本抽屉</b>的
    /// smapi-installer（跟着版本走）；否则退到<b>通用安装包缓存下的 keep 子目录</b>
    /// （只用官方最新版、从没下过历史版本的用户也免重下 40 MB）。
    /// ⚠ 兜底不能再放回 depot-staging\smapi-cache：那是界面上看不见的第三个"安装包放哪儿"，
    /// 而 keep 就在存储页「SMAPI 安装包缓存」那一行的统计与清理范围内，空间随时可回收。</summary>
    public static string GetSmapiInstallerCacheDir(string? gameVersion)
    {
        var staging = FindStagingByVersionLabel(gameVersion);
        return staging is not null ? Path.Combine(staging, "smapi-installer")
            : Path.Combine(StoragePaths.SmapiInstallerDir, "keep");
    }

    /// <summary>
    /// 备份当前 Steam 账号下该游戏的 userdata（413150）到 JuniGrid 缓存。
    /// 成就记录在 Steam 服务器，这里只是本地快照；切换旧版本体后 Steamworks
    /// 行为可能变化，留一份便于出问题时人工比对/恢复。
    /// </summary>
    private static void BackupSteamUserData(string appId)
    {
        try
        {
            var backupRoot = Path.Combine(StoragePaths.LocalAppDataDir, "userdata-backup", appId);
            if (Directory.Exists(backupRoot))
            {
                DateTime latest = DateTime.MinValue;
                foreach (var d in Directory.EnumerateDirectories(backupRoot))
                {
                    var ts = Directory.GetLastWriteTimeUtc(d);
                    if (ts > latest) latest = ts;
                }
                PruneUserDataBackups(backupRoot, keep: 3);
                if (DateTime.UtcNow - latest < TimeSpan.FromHours(20)) return;
            }

            string? steamRoot = null;
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                var p = key?.GetValue("SteamPath") as string;
                if (!string.IsNullOrWhiteSpace(p)) steamRoot = p.Replace('/', Path.DirectorySeparatorChar);
            }
            catch { }
            if (steamRoot is null || !Directory.Exists(steamRoot)) return;

            var udRoot = Path.Combine(steamRoot, "userdata");
            if (!Directory.Exists(udRoot)) return;
            string? bestSid = null;
            var bestTs = DateTime.MinValue;
            foreach (var sidDir in Directory.EnumerateDirectories(udRoot))
            {
                var appDir = Path.Combine(sidDir, appId);
                if (!Directory.Exists(appDir)) continue;
                var ts = Directory.GetLastWriteTime(appDir);
                if (ts > bestTs) { bestTs = ts; bestSid = sidDir; }
            }
            if (bestSid is null) return;

            var dest = Path.Combine(backupRoot, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(dest);
            ParallelCopyDirectory(Path.Combine(bestSid, appId), dest, skipTrash: true);
            PruneUserDataBackups(backupRoot, keep: 3);
            AppLog.Warn("DepotDownloader", $"已备份 Steam userdata → {dest}");
        }
        catch (Exception ex)
        {
            AppLog.Warn("DepotDownloader", "userdata 备份异常: " + ex.Message);
        }
    }

    private static void PruneUserDataBackups(string backupRoot, int keep)
    {
        try
        {
            var dirs = Directory.EnumerateDirectories(backupRoot)
                .OrderByDescending(d => Directory.GetLastWriteTimeUtc(d))
                .ToList();
            for (var i = keep; i < dirs.Count; i++)
            {
                try { Directory.Delete(dirs[i], true); } catch { }
            }
        }
        catch { }
    }


    // ------------------------------------------------------------------
    // v1.5：DepotDownloader 引擎 —— 扫码登录 + 令牌免密下载
    // （早期 steamcmd 通道因登录提示符对管道极不友好已移除）
    // 首次 -qr 扫码（Steam 手机 App 确认），令牌由它自行持久化；之后 -username
    // + -remember-password 静默复用，全程无密码。
    // ------------------------------------------------------------------

    private static readonly HttpClient DepotHttp = new() { Timeout = TimeSpan.FromMinutes(10) };

    static DepotDownloaderService()
    {
        // .NET Core 起 GBK（936）等代码页编码默认不可用，必须注册 CodePages 提供程序
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>从 DD 输出里提取账号名（多种成功文案都试）。</summary>
    private static string? TryParseAccount(string trimmed, string? current)
    {
        if (!string.IsNullOrEmpty(current)) return current;
        var am = Regex.Match(trimmed, "Logging in user '(.+?)'");
        if (am.Success) return am.Groups[1].Value;
        // 3.x 扫码成功：Success! Next time you can login with -username xxx -remember-password
        am = Regex.Match(trimmed, @"-username\s+(\S+)");
        if (am.Success && !am.Groups[1].Value.StartsWith('-')) return am.Groups[1].Value;
        am = Regex.Match(trimmed, @"Logged in(?: as| via)?\s+'?([A-Za-z0-9_]+)'?", RegexOptions.IgnoreCase);
        if (am.Success) return am.Groups[1].Value;
        return current;
    }

    private static bool LooksLikeLoginSuccess(string trimmed)
    {
        return trimmed.Contains("Success!", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Next time you can login with", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Done!", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Got depot key", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("licenses for account", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeNetworkFailure(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return text.Contains("Unable to get steam3 credentials", StringComparison.OrdinalIgnoreCase)
            || text.Contains("InitializeSteam failed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Connection to Steam failed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Trying again", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Connection timeout downloading depot", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Timeout", StringComparison.OrdinalIgnoreCase)
            || text.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || text.Contains("SocketException", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Socket closed", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>失败该怎么说 —— 按**实际断在哪一段**分，不再一律甩锅 CM。
    /// 实测踩过：登录一路成功（"授权成功""已连上 Steam"都打了），断的是 CDN 分片，
    /// 旧文案却回一句"CM 握手失败 + 三条 Clash 建议"，把人往错的方向带。</summary>
    private static string NetworkHint(string? tail = null)
    {
        var t = tail ?? "";
        // "Failed to find any server with chunk …" 也算 CDN 段：登录/授权那时都已经成功了，
        // 而且判定只看最后几十行（前面几千行 Validating 会把它挤掉），漏了这一条就会甩锅 CM。
        if (t.Contains("downloading chunk", StringComparison.OrdinalIgnoreCase)
            || t.Contains("depot manifest", StringComparison.OrdinalIgnoreCase)
            || t.Contains("Failed to find any server", StringComparison.OrdinalIgnoreCase)
            || t.Contains("sending the request", StringComparison.OrdinalIgnoreCase))
            return "登录是通的，断在从 Steam 内容服务器（CDN）拉数据：换手机热点或开加速器重试即可，改 Clash 的 CM 分流没用。";
        return "连不上 Steam 中继（CM）：Clash 让 steamcontent / steampowered 走 DIRECT，或关 TUN / 开加速器后重试。";
    }

    /// <summary>工具装在程序安装目录（插件内容，跟 JuniGrid 走，不进缓存）。</summary>
    private static string DepotDownloaderDir =>
        Path.Combine(AppContext.BaseDirectory, "tools", "DepotDownloader");
    private static string DepotDownloaderExe => Path.Combine(DepotDownloaderDir, "DepotDownloader.exe");
    private static string LegacyDepotDownloaderDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JuniGrid", "DepotDownloader");

    // framework 包约 2.5MB zip / 解压约 8MB，需本机有 .NET 9+（SMAPI/多数玩家已有）；
    // 自包含包约 32MB zip / 解压约 76MB，无运行时也能跑。优先 framework。
    private const string DepotDownloaderFrameworkUrl =
        "https://github.com/SteamRE/DepotDownloader/releases/download/DepotDownloader_3.4.0/DepotDownloader-framework.zip";
    private const string DepotDownloaderStandaloneUrl =
        "https://github.com/SteamRE/DepotDownloader/releases/download/DepotDownloader_3.4.0/DepotDownloader-windows-x64.zip";

    /// <summary>本机是否有 .NET 9+ 运行时（framework 版 DD 的最低要求）。</summary>
    private static bool HasCompatibleDotnetRuntime()
    {
        try
        {
            var roots = new List<string>();
            var env = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrWhiteSpace(env)) roots.Add(env);
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"));
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet"));

            foreach (var root in roots)
            {
                var shared = Path.Combine(root, "shared", "Microsoft.NETCore.App");
                if (!Directory.Exists(shared)) continue;
                foreach (var dir in Directory.EnumerateDirectories(shared))
                {
                    var name = Path.GetFileName(dir);
                    var dot = name.IndexOf('.');
                    if (dot <= 0) continue;
                    if (int.TryParse(name.AsSpan(0, dot), out var major) && major >= 9) return true;
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 确保 DepotDownloader 就位（framework 约 3MB；无 .NET 9+ 时回退自包含约 32MB）。
    /// 装在安装目录 tools\DepotDownloader；旧 LocalAppData 副本首次访问时搬迁。
    /// </summary>
    public async Task EnsureDepotDownloaderAsync(IProgress<string>? log = null, CancellationToken ct = default)
    {
        await _depotEnsureGate.WaitAsync(ct);
        try
        {
        // 旧位置 → 安装目录
        try
        {
            if (!File.Exists(DepotDownloaderExe)
                && Directory.Exists(LegacyDepotDownloaderDir)
                && !Directory.Exists(DepotDownloaderDir))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DepotDownloaderDir)!);
                if (StorageService.TryMoveTree(LegacyDepotDownloaderDir, DepotDownloaderDir))
                    AppLog.Warn("DepotDownloader", "工具已迁到安装目录: " + DepotDownloaderDir);
            }
        }
        catch (Exception ex) { AppLog.Warn("DepotDownloader", "工具迁移失败: " + ex.Message); }

        if (File.Exists(DepotDownloaderExe)) return;

        try { Directory.CreateDirectory(DepotDownloaderDir); }
        catch (Exception ex)
        {
            throw new DepotException(
                $"无法在安装目录写入工具组件（{DepotDownloaderDir}）：{ex.Message}。" +
                "若是 Program Files 请用管理员运行，或把 JuniGrid 装到有写权限的目录");
        }

        var useFramework = HasCompatibleDotnetRuntime();
        var url = useFramework ? DepotDownloaderFrameworkUrl : DepotDownloaderStandaloneUrl;
        log?.Report(useFramework
            ? "正在下载登录组件（GitHub 官方精简包，约 3MB）…"
            : "正在下载登录组件（GitHub 官方完整包，约 32MB，本机缺少 .NET 9+ 运行时）…");

        var zip = Path.Combine(Path.GetTempPath(), "junigrid-depotdownloader.zip");
        // v1.6.2：直连 GitHub 失败自动切镜像（ghfast.top / gh-proxy / ghproxy）——
        // 国内裸机首次切版本不再卡死在组件下载上
        Exception? lastErr = null;
        var downloaded = false;
        foreach (var candidate in UpdateService.GithubUrls(url))
        {
            try
            {
                using var resp = await DepotHttp.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                await using (var fs = File.Create(zip))
                {
                    await resp.Content.CopyToAsync(fs, ct);
                }
                // 镜像可能拿 200 状态回 HTML 错误页 —— 校验 PK 文件头，不是 zip 视为该候选失败
                using (var head = File.OpenRead(zip))
                {
                    Span<byte> b = stackalloc byte[2];
                    if (head.Read(b) < 2 || b[0] != (byte)'P' || b[1] != (byte)'K')
                        throw new InvalidOperationException("候选源返回的不是 zip（可能是错误页）");
                }
                downloaded = true;
                break;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastErr = ex;
                try { File.Delete(zip); } catch { }
            }
        }
        if (!downloaded)
            throw new DepotException($"登录组件下载失败（直连与镜像均不通）：{lastErr?.Message}");
        ZipFile.ExtractToDirectory(zip, DepotDownloaderDir, overwriteFiles: true);
        try { File.Delete(zip); } catch { }
        AppLog.Warn("DepotDownloader", $"DepotDownloader 就绪（{(useFramework ? "framework" : "standalone")}）：" + DepotDownloaderExe);
        }
        finally
        {
            _depotEnsureGate.Release();
        }
    }

    private const char BlockChar = (char)0x2588; // █
    // DD 不同版本可能用 ■ ▀ ▄ 等，一并认作「黑模块」
    private static bool IsQrDarkChar(char c) =>
        c is '█' or '▀' or '▄' or '■' or '▓' or '▒' or '●' or '#' or '@';

    private static bool LooksLikeQrLine(string line)
    {
        if (line.Length < 20) return false;
        var dark = 0;
        foreach (var c in line)
            if (IsQrDarkChar(c)) dark++;
            else if (!char.IsWhiteSpace(c)) return false;
        return dark >= 8;
    }

    private static string UrlToDataUri(string url)
    {
        using var gen = new QRCoder.QRCodeGenerator();
        using var data = gen.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.M);
        using var png = new QRCoder.PngByteQRCode(data);
        var bytes = png.GetGraphic(12);
        return "data:image/png;base64," + Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// DepotDownloader 通道：扫码（首次）或本机令牌（之后）登录并下载历史版本到暂存目录，
    /// 完成后直接替换游戏文件（保留 Mods/）。onDataUri 回调给出二维码 PNG 的 data URI（标准库生成，保证可扫）；
    /// 返回登录的 Steam 账号名（回填 UI 用）。
    /// </summary>
    public async Task<string> QrDownloadAndApplyAsync(
        string gamePath, string appId, string depotId, string manifestId, string? versionLabel,
        bool tokenAvailable, string? knownAccount,
        Func<string, Task> onDataUri,
        IProgress<Progress>? progress = null, CancellationToken ct = default,
        bool applyToGame = true)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
            throw new DepotException("游戏目录无效，请先在设置里定位游戏");
        if (string.IsNullOrWhiteSpace(manifestId) || !manifestId.All(char.IsDigit))
            throw new DepotException("Manifest ID 无效（应为一串数字）");

        // 只下载模式：本地已经有完整包就到此为止，不碰游戏目录
        if (!applyToGame && IsStagedComplete(manifestId))
        {
            progress?.Report(new Progress("本地已有完整缓存", 100));
            return knownAccount ?? "";
        }
        // 本地已有完整包 → 直接应用，不下载、不删缓存、不连 Steam
        if (applyToGame && TryApplyStaged(gamePath, appId, depotId, manifestId, progress, TryReadGameVersion(gamePath)))
        {
            progress?.Report(new Progress("已切换到历史版本（本地缓存）", 100));
            AppLog.Warn("DepotDownloader", $"历史版本从缓存应用：manifest={manifestId}");
            return knownAccount ?? "";
        }

        // 真要连 Steam 了才占会话：前面两条本地缓存快路径不排队。排队状态由这里报出去，
        // 上层只照着它标「等待中」—— 上层自己再拿一遍这把闸会死锁（见 HoldSteamSessionAsync）。
        // 判据是"槽位满了"而不是"有人在用"：3 路并行时第 2、3 路不算排队。
        if (SteamSessionCount >= MaxSteamSessions)
            progress?.Report(new Progress("排队中，等前面的版本下完", null, Queued: true));
        using var session = await HoldSteamSessionAsync(ct);
        // 每一轮尝试都从 0% 起报：下载器按文件列表重走一遍，留着上一轮的旧数字会让人以为"又卡在同一段"
        progress?.Report(new Progress("正在启动下载…", 0, Queued: false));

        await EnsureDepotDownloaderAsync(new Progress<string>(msg => progress?.Report(new Progress(msg, null))), ct);

        var staging = StagingDirFor(appId, depotId, versionLabel, manifestId);
        // 半截包不再一删了之。删了就等于每次重试都从 0 开始 —— 而国内到 Steam 内容服务器
        // 恰恰是**尾部 chunk 成片超时**（日志里成片的 Connection timeout downloading chunk），
        // 于是永远卡在同一段。留着让它接着下，并带 -verify-all：DD 会预分配整个包，
        // 半截文件"大小看着是对的"，只按大小判会把残缺当完成。
        var resuming = CanResumeStagedPackage(staging, manifestId);
        if (!resuming)
        {
            PreserveStagedModsBeforeWipe(staging);   // 抽屉里可能是用户唯一的 mod 本体，先挪走再清
            try
            {
                if (Directory.Exists(staging))
                {
                    AppLog.Warn("DepotDownloader",
                        $"这个缓存包不能续传（manifest 对不上或没有本体），清空重下：{staging}");
                    Directory.Delete(staging, true);
                }
            }
            catch { }
        }
        Directory.CreateDirectory(staging);
        WriteManifestMeta(staging, manifestId);
        if (resuming)
            progress?.Report(new Progress("接着上次没下完的部分继续（先校验已下的文件）…", 0));

        var account = knownAccount;
        var qr = new List<string>();
        var failure = default(string);
        var qrEmitted = false;
        string? challengeUrl = null;
        // v1.4.6：DepotDownloader 拉 manifest 会无限 "Connection timeout ... Retrying."
        // （输出一直有行 → 静默看门狗永远不触发）。连超 N 次就掐掉，别让用户对着 90% 干等。
        var manifestTimeouts = 0;
        var manifestGiveUp = false;

        var onLine = new Action<string>(line =>
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) return;
            AppLog.Warn("DepotDownloader", "DD: " + trimmed);

            // 优先：日志里若直接有 s.team / 登录 URL，用 URL 生成标准码（比 ASCII 稳）
            var um = Regex.Match(trimmed, "https://s\\.team/q/[^\\s'\"`]+");
            if (um.Success && challengeUrl != um.Value)
            {
                challengeUrl = um.Value;
                try
                {
                    var uri = UrlToDataUri(challengeUrl);
                    qrEmitted = true;
                    _ = onDataUri(uri);
                }
                catch (Exception ex) { AppLog.Warn("DepotDownloader", "URL 生成二维码失败: " + ex.Message); }
            }

            // QR 行禁止 Trim：行尾空格是白模块
            if (LooksLikeQrLine(line)) { qr.Add(line.TrimEnd('\r')); return; }
            if (qr.Count >= 27)
            {
                var snapshot = qr.ToArray();
                qr.Clear();
                if (!qrEmitted)
                {
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var (dataUri, url) = QrAsciiToDataUriAndUrl(snapshot);
                            qrEmitted = true;
                            _ = onDataUri(dataUri);
                            AppLog.Warn("DepotDownloader", $"ASCII 二维码已生成 rows={snapshot.Length} url={url}");
                        }
                        catch (Exception ex) { AppLog.Warn("DepotDownloader", "ASCII 二维码解析失败: " + ex.Message); }
                    }, ct);
                }
            }
            else if (qr.Count > 0 && qr.Count < 27 && !LooksLikeQrLine(line))
            {
                // 二维码中间夹了非码行：保留已收集行，等凑齐；仅在明显中断时清空
                if (trimmed.Contains("ERROR", StringComparison.OrdinalIgnoreCase)) qr.Clear();
            }

            account = TryParseAccount(trimmed, account);
            var pct = Regex.Match(trimmed, @"\((\d+) / (\d+)\)");
            // manifest/chunk 连接超时：DD 自己每 10 秒重试一次，界面上必须看见
            if (trimmed.Contains("Connection timeout downloading depot", StringComparison.OrdinalIgnoreCase))
            {
                manifestTimeouts++;
                progress?.Report(new Progress(
                    manifestTimeouts >= 6
                        ? "Steam CDN 连不上（版本清单超时），准备结束任务…"
                        : $"Steam CDN 超时，正在重试（{manifestTimeouts}/6）…",
                    90, trimmed));
                if (manifestTimeouts >= 6)
                {
                    manifestGiveUp = true;
                    failure = "Steam CDN 连接超时（版本清单一直拉不下来）";
                }
            }
            else if (pct.Success || trimmed.Contains("Downloading chunk", StringComparison.OrdinalIgnoreCase)
                     || trimmed.StartsWith("Got depot key", StringComparison.OrdinalIgnoreCase))
                manifestTimeouts = 0;

            if (LooksLikeLoginSuccess(trimmed) && !string.IsNullOrWhiteSpace(account))
            {
                // 扫码确认成功 —— 立刻让 UI 知道（进程可能还要下 manifest 才退出）
                progress?.Report(new Progress("Steam 授权成功，正在完成…", 90));
            }
            if (pct.Success && long.TryParse(pct.Groups[1].Value, out var done) && long.TryParse(pct.Groups[2].Value, out var total) && total > 0)
                progress?.Report(new Progress("正在下载版本文件…", (int)Math.Min(99, done * 100 / total), trimmed));
            else if (trimmed.Contains("Use the Steam Mobile App", StringComparison.OrdinalIgnoreCase))
                progress?.Report(new Progress("等待手机扫码确认…（若下方无图请稍候或点重试）", null));
            else if (qrEmitted)
                progress?.Report(new Progress("等待手机扫码确认…", null));
            else if (trimmed.Contains("Logging in user", StringComparison.OrdinalIgnoreCase))
                progress?.Report(new Progress(trimmed.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                    ? "登录被 Steam 拒绝（令牌可能已失效，请重新扫码）" : "正在登录 Steam 账号…", null, trimmed));
            else if (trimmed.StartsWith("Downloading depot", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.Contains("Depot download", StringComparison.OrdinalIgnoreCase))
                progress?.Report(new Progress("正在下载版本文件…", null, trimmed));
            else if (!LooksLikeQrLine(line))
            {
                var zh = EngineLineToChinese(trimmed);
                if (zh is not null) progress?.Report(new Progress(zh, null, trimmed));
            }

            if (trimmed.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("FAILED login", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("TwoFactorCodeMismatch", StringComparison.OrdinalIgnoreCase))
                failure = trimmed;
        });

        // 已扫码授权 → 优先本机令牌；无账号名时退回 -qr
        var useToken = tokenAvailable && !string.IsNullOrWhiteSpace(knownAccount);
        progress?.Report(new Progress(useToken ? "正在用本机授权连接 Steam…" : "正在连接 Steam…", null));
        var args = $"-app {appId} -depot {depotId} -manifest {manifestId} -dir \"{staging}\" -remember-password -max-downloads 16{(resuming ? " -verify-all" : "")} -loginid {NextLoginId()}";
        args += useToken ? $" -username {knownAccount!.Trim()}" : " -qr";

        // 重开一路前要清掉「已出过码」的状态：ASCII 分支的 if (!qrEmitted) 会拦住第二张码
        void ResetQrState() { qrEmitted = false; qr.Clear(); challengeUrl = null; failure = null; }

        var (exitCode, tail) = await RunWithNetworkRetryAsync(args, line =>
        {
            onLine(line);
            if (line.Contains("Use the Steam Mobile App", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("STEAM GUARD", StringComparison.OrdinalIgnoreCase))
                SilenceTimeoutMs = 300000;
        }, progress, ct, kickoffDeadlineMs: KickoffDeadlineMs, onBeforeRetry: ResetQrState,
            stopWhen: () => manifestGiveUp);

        var joinedFail = string.Join("\n", tail);
        if (useToken && exitCode != 0 && !HasGameBinary(staging)
            && (joinedFail.Contains("password", StringComparison.OrdinalIgnoreCase)
                || (failure is not null && failure.Contains("password", StringComparison.OrdinalIgnoreCase))))
        {
            AppLog.Warn("DepotDownloader", "令牌路径失败，自动回退扫码");
            progress?.Report(new Progress("本机授权失效，改用扫码…", null));
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
            Directory.CreateDirectory(staging);
            WriteManifestMeta(staging, manifestId);
            ResetQrState();
            useToken = false;
            args = $"-app {appId} -depot {depotId} -manifest {manifestId} -dir \"{staging}\" -remember-password -max-downloads 16{(resuming ? " -verify-all" : "")} -loginid {NextLoginId()} -qr";
            (exitCode, tail) = await RunWithNetworkRetryAsync(args, onLine, progress, ct,
                onBeforeRetry: ResetQrState);
        }

        if (exitCode != 0 || !HasGameBinary(staging))
        {
            var hint = failure ?? (tail.Count > 0 ? tail[^1] : "");
            var all = string.Join("\n", tail);
            if (string.IsNullOrWhiteSpace(account))
                foreach (var t in tail) { account = TryParseAccount(t, account); if (!string.IsNullOrWhiteSpace(account)) break; }

            // 账号没有这款游戏 / 无下载许可 —— 不是网络问题，必须单独说清楚
            if (all.Contains("is not available from this account", StringComparison.OrdinalIgnoreCase)
                || all.Contains("No license", StringComparison.OrdinalIgnoreCase)
                || all.Contains("not owned", StringComparison.OrdinalIgnoreCase))
            {
                var who = string.IsNullOrWhiteSpace(account) ? knownAccount : account;
                throw new DepotException(
                    $"当前 Steam 账号{(string.IsNullOrWhiteSpace(who) ? "" : $"（{who}）")}没有《星露谷物语》的下载许可，" +
                    "无法下载历史版本。请改用拥有本体的账号在设置里重新扫码授权。");
            }
            if (tail.Any(l => l.Contains("password", StringComparison.OrdinalIgnoreCase)) && !useToken)
                throw new DepotException("本机登录令牌已失效，请重新点击「扫码登录并下载」扫一次码");
            if (LooksLikeNetworkFailure(hint) || LooksLikeNetworkFailure(all))
            {
                // 屏幕上只留一句人话 + 一句对策；引擎原文（很长、带分片哈希）进日志备查
                AppLog.Warn("DepotDownloader", $"下载未完成（退出码 {exitCode}）引擎原文：{all}");
                throw new DepotException($"下载未完成：{NetworkHint(all)}");
            }
            throw new DepotException(
                $"扫码下载未完成（退出码 {exitCode}）。{(hint.Length > 0 ? "最后输出：" + hint + " " : "")}" +
                "常见原因：网络超时或二维码过期，可重试");
        }

        // 到这里才是"这一版真的下完了"：退出码 0 且本体在盘上。
        // 记这个标记是因为：分片超时会留下一个「看起来完整」的半截包（锚点文件都在，
        // 缺的是别的角色的贴图），切过去进档时 NPC 构造直接空引用闪退
        // （09-22 00:04 WER：NullReferenceException at StardewValley.NPC..ctor ← loadForNewGame）。
        TryWriteCompleteMark(staging);

        if (string.IsNullOrWhiteSpace(account))
            foreach (var t in tail) { account = TryParseAccount(t, account); if (!string.IsNullOrWhiteSpace(account)) break; }

        if (!applyToGame)
        {
            // 文件已经躺在缓存抽屉里（下载阶段就写进 staging 了），切换由用户点版本号时再做
            progress?.Report(new Progress($"已下载完成，点版本号即可切换到 {versionLabel ?? manifestId}", 100));
            return account ?? knownAccount ?? "";
        }
        progress?.Report(new Progress("正在替换游戏文件…", null));
        await Task.Run(() => ApplyStagingExclusive(staging, gamePath, progress, TryReadGameVersion(gamePath)), ct);
        progress?.Report(new Progress("已切换到历史版本", 100));
        AppLog.Warn("DepotDownloader", $"DD 通道历史版本已应用：manifest={manifestId} → {gamePath}");
        return account ?? knownAccount ?? "";
    }

    /// <summary>一路 DD 从启动到「出码 / 登录成功 / 开始下载」的等待上限。实测出码是双峰的：
    /// 要成就是 2–5 秒，不成就是 DD 自己那 10 轮 CM 退避烧满 ~64 秒 —— 到点没进展就重开一路
    /// （新进程等于重新抽一次 CM 列表）。测试台会临时调小它来演这条。</summary>
    internal static int KickoffDeadlineMs = 20000;

    /// <summary>DepotDownloader 的英文状态行 → 给人看的中文。返回 null = 这句不占主提示位
    /// （宁可留着上一句中文，也不要让一行英文把「二维码已失效，正在重新获取（第 2/3 次）…」顶掉）。
    /// 原文每一行都以 DD-qr: 前缀进了 juni-grid.log，所以不显示 ≠ 查不到。
    /// 口径收在这一个函数：两条扫码通道（只登录 / 下载并应用）共用。</summary>
    private static string? EngineLineToChinese(string line)
    {
        if (line.Contains("Connection to Steam failed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Trying again", StringComparison.OrdinalIgnoreCase))
            return "正在连接 Steam 服务器…（连不上会自动重开一路）";
        if (line.Contains("Lost connection", StringComparison.OrdinalIgnoreCase))
            return "与 Steam 服务器的连接断了，正在重连…";
        if (line.Contains("InitializeSteam failed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Unable to get steam3 credentials", StringComparison.OrdinalIgnoreCase))
            return "Steam 客户端初始化没成功，正在重开一路…";
        if (line.Contains("Got AppInfo", StringComparison.OrdinalIgnoreCase))
            return "已连上 Steam，正在读取版本信息…";
        if (line.Contains("Processing depot", StringComparison.OrdinalIgnoreCase)
            || DepotProgressRx.IsMatch(line))
            return "正在下载 depot…";
        if (line.Contains("Logging in", StringComparison.OrdinalIgnoreCase))
            return "正在登录 Steam…";
        return null;
    }

    private static readonly Regex DepotProgressRx = new(@"\(\d+ / \d+\)", RegexOptions.Compiled);

    private static int SilenceTimeoutMs = 75000;   // 二维码出现后调大到 300 秒（等待用户扫码）

    /// <summary>「立即重开」的信号：出码截止是 20 秒，用户不该干等 —— 置位后当前这一路
    /// 会在 1 秒内被掐掉并马上开下一路，且这次重开不占用重试次数（是用户主动要的，不是失败）。
    /// 带时间戳只认 10 秒内的请求：没人轮询时留下的标志，会把之后某次正常下载掐掉 —— 那是更难查的事故。</summary>
    private static long _kickAt;
    public static void RequestKickNow() => Volatile.Write(ref _kickAt, Environment.TickCount64);
    private static bool TakeKick()
    {
        var at = Interlocked.Exchange(ref _kickAt, 0);
        return at != 0 && Environment.TickCount64 - at < 10_000;
    }

    /// <summary>仅扫码授权：登录并保存本机令牌（-manifest-only，不下载游戏文件）。返回账号名。
    /// onAsciiLines 给矢量码用的原始码行，onStale 在「这张码作废、正在重取」时回调。</summary>
    public async Task<string> QrLoginOnlyAsync(Func<string, Task> onDataUri,
        IProgress<Progress>? progress = null, CancellationToken ct = default,
        Func<IReadOnlyList<string>, Task>? onAsciiLines = null,
        Action? onStale = null)
    {
        // 与下载共用同一把会话闸：下载在飞时硬起第二路会把它顶掉（见 SessionGate 注释）
        using var session = await HoldSteamSessionAsync(ct);
        await EnsureDepotDownloaderAsync(new Progress<string>(msg => progress?.Report(new Progress(msg, null))), ct);
        SilenceTimeoutMs = 300000;

        string? account = null;
        var qr = new List<string>();
        var failure = default(string);
        var qrEmitted = false;
        var authorized = false;      // DD 打出 Success! 的那一刻（票据已落盘，见 RunDepotDownloaderAsync）
        string? challengeUrl = null;

        void EmitQr(string uri, string? url, IReadOnlyList<string>? ascii)
        {
            qrEmitted = true;
            if (url is not null) challengeUrl = url;
            _ = onDataUri(uri);
            if (ascii is not null && onAsciiLines is not null) _ = onAsciiLines(ascii);
        }

        // DD 每 ~22 秒换一张 challenge，而一张码的 29 行是在十几毫秒内连着吐完的。
        // 以前要等「下一行非码文」才结算，而那下一行往往正是「The QR code has changed」——
        // 于是界面永远停在上一张，用户扫的是已经作废的码，手机只报「加载二维码失败」
        // （实测 07:25:04 那张码到 07:25:26 换码时才推给界面）。现在按「最后一行后再无新行」结算。
        var qrLock = new object();
        var qrGen = 0;
        var qrDecoding = false;

        void FlushQr()
        {
            string[] snapshot;
            lock (qrLock)
            {
                if (qr.Count < 27 || qrDecoding) return;   // 半截码不推，扫不出更坑
                snapshot = qr.ToArray();
                qr.Clear();
                qrDecoding = true;
            }
            _ = Task.Run(() =>
            {
                try
                {
                    var (dataUri, url) = QrAsciiToDataUriAndUrl(snapshot);
                    EmitQr(dataUri, url, snapshot);
                    AppLog.Warn("DepotDownloader", $"ASCII 二维码已生成 rows={snapshot.Length} url={url}");
                }
                catch (Exception ex)
                {
                    AppLog.Warn("DepotDownloader", "ASCII 二维码解析失败: " + ex.Message);
                    var u = challengeUrl;
                    if (u is not null) { try { EmitQr(UrlToDataUri(u), u, snapshot); } catch { } }
                }
                finally { lock (qrLock) qrDecoding = false; }
            }, CancellationToken.None);
        }

        void ScheduleFlush()
        {
            int gen;
            lock (qrLock) gen = ++qrGen;
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(500, ct); } catch { return; }
                lock (qrLock) { if (gen != qrGen) return; }   // 期间又来了新行：交给最后那行的定时器
                FlushQr();
            }, CancellationToken.None);
        }

        var onLine = new Action<string>(line =>
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) return;
            AppLog.Warn("DepotDownloader", "DD-qr: " + trimmed);

            var um = Regex.Match(trimmed, "https://s\\.team/q/[^\\s'\"`]+");
            if (um.Success && challengeUrl != um.Value)
            {
                challengeUrl = um.Value;
                try
                {
                    EmitQr(UrlToDataUri(challengeUrl), challengeUrl, null);
                }
                catch (Exception ex) { AppLog.Warn("DepotDownloader", "URL 生成二维码失败: " + ex.Message); }
            }

            // QR 行禁止 Trim：行尾空格是白模块
            if (LooksLikeQrLine(line))
            {
                lock (qrLock) qr.Add(line.TrimEnd('\r'));
                ScheduleFlush();
                return;
            }

            // 非码文来了 ⇒ 这张码必定画完了，立刻结算（比 500ms 静默那条快）
            FlushQr();
            if (trimmed.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                lock (qrLock) qr.Clear();     // 半截码 + 报错：丢掉，别把残码推给人扫

            account = TryParseAccount(trimmed, account);
            if (LooksLikeLoginSuccess(trimmed) && !string.IsNullOrWhiteSpace(account))
            {
                authorized = true;
                progress?.Report(new Progress("Steam 授权成功，正在收尾…", 90));
            }

            // 码出来之后 Steam 中继断了 → 挑战立即失效，必须让用户重开
            if (qrEmitted && (
                    trimmed.Contains("Lost connection", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Contains("InitializeSteam failed", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Contains("Unable to get steam3 credentials", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Contains("Connection to Steam failed", StringComparison.OrdinalIgnoreCase)))
            {
                failure = "Steam 连接已中断，当前二维码已失效，请关闭后重新扫码";
                progress?.Report(new Progress(failure, null, trimmed));
                return;
            }

            if (trimmed.Contains("Use the Steam Mobile App", StringComparison.OrdinalIgnoreCase))
                progress?.Report(new Progress("等待手机扫码确认…（正在生成二维码）", null));
            else if (qrEmitted && !LooksLikeLoginSuccess(trimmed))
                progress?.Report(new Progress("等待手机扫码确认…", null));
            else if (trimmed.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.Contains("FAILED login", StringComparison.OrdinalIgnoreCase))
            {
                failure = trimmed;
                progress?.Report(new Progress(trimmed.Length > 80 ? trimmed[..80] + "…" : trimmed, null, trimmed));
            }
            else if (!LooksLikeQrLine(line))
            {
                var zh = EngineLineToChinese(trimmed);
                if (zh is not null) progress?.Report(new Progress(zh, null, trimmed));
            }
        });

        progress?.Report(new Progress("正在连接 Steam 中继…", null));
        // 每路到点没出码就重开（新进程等于重新抽一次 CM 列表）。实测出码是双峰的 ——
        // 要成就是 2–5 秒，不成就是 DD 自己 10 轮退避烧满 60 秒，所以截止不会误杀
        // 「本来会成」的那一路。成功率约 1/3 → P(至少一路出码) ≈ 1-(2/3)^3 ≈ 70%，
        // 最坏 3×20+退避 ≈ 80 秒（旧配置 2×64 ≈ 130 秒）。
        var (code, tail) = await RunWithNetworkRetryAsync(
            $"-app 413150 -qr -remember-password -manifest-only -max-downloads 4 -loginid {NextLoginId()}",
            onLine, progress, ct, maxAttempts: 3, kickoffDeadlineMs: KickoffDeadlineMs,
            stopWhen: () => authorized,
            onBeforeRetry: () =>
            {
                // 死码必须撤干净：界面上留着旧码就是让人去扫一张废的。
                // （出码本身已不再被 qrEmitted 拦，但那张码归属的进程已经退了，仍要清缓冲。）
                onStale?.Invoke();
                qrEmitted = false;
                lock (qrLock) { qr.Clear(); qrDecoding = false; }
                challengeUrl = null;
                failure = null;
            });

        // 兜底：从输出尾部再捞一次账号
        if (string.IsNullOrWhiteSpace(account))
            foreach (var t in tail)
            {
                account = TryParseAccount(t, account);
                if (!string.IsNullOrWhiteSpace(account)) break;
            }

        var joinedTail = string.Join("\n", tail);
        var success = !string.IsNullOrWhiteSpace(account)
                      || LooksLikeLoginSuccess(joinedTail)
                      || code == 0 && !LooksLikeNetworkFailure(joinedTail);

        if (!success)
        {
            // 内部造的截止行（「启动器 N 秒内未拿到二维码…」）不是失败原因，别抖给用户
            var hint = failure ?? (tail.LastOrDefault(t => !t.Contains("启动器 ")) ?? "");
            if (LooksLikeNetworkFailure(hint) || LooksLikeNetworkFailure(joinedTail))
            {
                AppLog.Warn("DepotDownloader", "扫码 CM 握手失败，DD 原始输出尾部：" + joinedTail);
                // 出过码 ⇒ 真话是「码死了」而不是「连不上所以没码」，两者处置完全不同：
                // 前者重取一张并尽快扫就行，后者要先解决网络。实测 03:45:20 出码、
                // 03:45:35 掉线，那时甩"CM 握手已重试 3 次"给用户，人只会对着死码继续扫。
                if (qrEmitted)
                    throw new DepotException(
                        "二维码已失效 —— 出码后 Steam 连接就断了，这张码扫了手机只会报「加载二维码失败」。" +
                        "请等界面刷出新码后再扫；多次刷新都失败再按下面的网络建议处理。");
                throw new DepotException(
                    "连不上 Steam 服务器（CM 握手已重试 3 次）。" +
                    "常见解法：Clash 里把 steampowered.com / steamcontent.com 设为 DIRECT；" +
                    "或改开 TUN / 全局模式；或换手机热点再试。详细原因见日志。");
            }
            if (!qrEmitted)
                throw new DepotException(
                    $"未拿到可扫描的二维码（退出码 {code}）。{hint} " +
                    "请把 %AppData%\\JuniGrid\\juni-grid.log 里最近的 DD-qr 行发来排查。");
            throw new DepotException(
                string.IsNullOrWhiteSpace(failure) || !failure.Contains("失效")
                    ? $"扫码授权未完成（退出码 {code}）。{hint} 常见原因：二维码过期、Steam 中继断开，请重新扫码"
                    : failure);
        }

        progress?.Report(new Progress("授权成功", 100));
        AppLog.Warn("DepotDownloader", $"扫码授权完成 account={account ?? "?"} exit={code}");
        return account ?? "";
    }

    /// <summary>
    /// 把 DepotDownloader 输出的 ASCII 二维码还原成原始 URL，再用 QRCoder 生成标准 PNG data URI。
    /// </summary>
    /// <summary>
    /// 把 DepotDownloader 输出的 ASCII 二维码还原成原始 URL，再用 QRCoder 生成标准 PNG data URI。
    /// 返回 (dataUri, 解码出的 URL)。
    /// </summary>
    private static (string DataUri, string Url) QrAsciiToDataUriAndUrl(IReadOnlyList<string> lines)
    {
        // 各行等长化（含行尾空白模块）；每模块 1 或 2 字符
        var maxLen = 0;
        foreach (var l in lines) maxLen = Math.Max(maxLen, l.Length);
        if (maxLen < 20) throw new DepotException("二维码行过短，无法解析");

        // 29×29 的 QR 模块宽约 58 字符（2 字符/模块）或 29（1 字符/模块）
        var twoCharPerCell = maxLen >= lines.Count * 1.5;
        var cells = twoCharPerCell ? (maxLen + 1) / 2 : maxLen;
        var rgb = new byte[lines.Count * cells * 3];
        for (var r = 0; r < lines.Count; r++)
        {
            var l = lines[r].PadRight(maxLen);
            for (var c = 0; c < cells; c++)
            {
                bool black;
                if (twoCharPerCell)
                {
                    var i0 = c * 2;
                    black = IsQrDarkChar(l[i0]) || (i0 + 1 < l.Length && IsQrDarkChar(l[i0 + 1]));
                }
                else black = IsQrDarkChar(l[Math.Min(c, l.Length - 1)]);
                var i = (r * cells + c) * 3;
                rgb[i] = rgb[i + 1] = rgb[i + 2] = black ? (byte)0 : (byte)255;
            }
        }
        var src = new ZXing.RGBLuminanceSource(rgb, cells, lines.Count);
        var reader = new ZXing.QrCode.QRCodeReader();
        var result = reader.decode(new ZXing.BinaryBitmap(new ZXing.Common.HybridBinarizer(src)))
                   ?? throw new DepotException($"二维码内容解析失败（{lines.Count}行×{cells}格，行宽{maxLen}）");
        return (UrlToDataUri(result.Text), result.Text);
    }

    /// <summary>
    /// CM 握手在国内经常秒败。网络类失败自动重试（间隔 2s/4s…）。
    /// 出过码后进程又被掉线杀掉，那张 challenge 已作废 ⇒ 额外重取一次新码（onBeforeRetry
    /// 先让调用方把死码从界面上撤掉、并放开重新出码的开关）。
    /// </summary>
    private async Task<(int ExitCode, List<string> Tail)> RunWithNetworkRetryAsync(
        string args, Action<string> onLine, IProgress<Progress>? progress, CancellationToken ct,
        int maxAttempts = 5, int kickoffDeadlineMs = 0, Action? onBeforeRetry = null,
        Func<bool>? stopWhen = null)
    {
        List<string> lastTail = new();
        int lastCode = 1;
        var afterDeadQr = false;
        var deadQrRetries = 0;
        var manualKicks = 0;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (attempt > 1)
            {
                // 用户主动踢的这一次不算进重试次数（他不是失败方，不该因为点了按钮而更早放弃）；
                // 用完就清零，否则后面每一轮都会被当成"手动重开"而跳过退避与计数
                var manual = manualKicks > 0;
                manualKicks = 0;
                if (manual) maxAttempts++;
                // 上一张码已随死掉的进程作废：先把它从界面上撤掉，再等重试间隔
                if (afterDeadQr) onBeforeRetry?.Invoke();
                var delay = manual ? 0 : Math.Min(15000, 2000 * attempt);
                progress?.Report(new Progress(manual
                    ? "按你的要求重开一路连接…"
                    : afterDeadQr
                    ? $"二维码已失效，正在重新获取（第 {attempt}/{maxAttempts} 次）…"
                    : $"连不上 Steam 服务器，重开一路再试（第 {attempt}/{maxAttempts} 次）…", 0));
                await Task.Delay(delay, ct);
            }
            afterDeadQr = false;
            var gotQr = false;
            var kickedOffAny = false;
            var wrapped = new Action<string>(line =>
            {
                if (line.Contains("Use the Steam Mobile App", StringComparison.OrdinalIgnoreCase) ||
                    line.IndexOf(BlockChar) >= 0)
                    gotQr = kickedOffAny = true;
                // 登录已过 / 已在枚举 / 已在下载 ⇒ 这一路活着，别再按「卡住」掐它
                else if (LooksLikeLoginSuccess(line) ||
                         line.Contains("Got AppInfo", StringComparison.OrdinalIgnoreCase) ||
                         line.Contains("Processing depot", StringComparison.OrdinalIgnoreCase) ||
                         DepotProgressRx.IsMatch(line))
                    kickedOffAny = true;
                onLine(line);
            });
            var (code, tail) = await RunDepotDownloaderAsync(args, wrapped, ct, () => kickedOffAny,
                kickoffDeadlineMs, stopWhen, onKick: () => manualKicks++);
            lastCode = code;
            lastTail = tail;
            // 授权已经成了（stopWhen 是我们自己掐的进程）→ 别再按「失败」重开一路
            if (stopWhen?.Invoke() == true) return (code, tail);
            // 正常退出、或不是网络类的失败（账号/权限/令牌错）→ 不再重试
            var joined = string.Join("\n", tail);
            var netLoss = LooksLikeNetworkFailure(joined) ||
                          LooksLikeNetworkFailure(tail.Count > 0 ? tail[^1] : "");
            if (code == 0 || !netLoss) return (code, tail);
            // 出过码却仍被 CM 掉线杀掉 ⇒ 那张 challenge 已作废，扫它手机必报「加载二维码失败」。
            // 以前这里 gotQr 就无条件收手，用户只能对着死码 + 一句"连不上"自己关窗重开。
            // 只额外给一次：「用户点了拒绝」在 DD 输出里和掉线没区分度，多刷码会骚扰人。
            if (gotQr)
            {
                if (deadQrRetries >= 1) return (code, tail);
                deadQrRetries++;
                afterDeadQr = true;
            }
            if (attempt == maxAttempts) return (code, tail);
        }
        return (lastCode, lastTail);
    }

    /// <summary>跑一次 DepotDownloader，逐行回调输出；75 秒无输出判定无响应（扫码等待期自动放宽）。
    /// 整体放线程池执行 —— 内部 TryTake 带超时会阻塞线程，绝不能跑在 Blazor UI 线程上（实测整界面冻结）。</summary>
    private static Task<(int ExitCode, List<string> Tail)> RunDepotDownloaderAsync(
        string args, Action<string> onLine, CancellationToken ct,
        Func<bool>? kickedOff = null, int kickoffDeadlineMs = 0, Func<bool>? stopWhen = null,
        Action? onKick = null)
    {
        return Task.Run(async () =>
        {
            var psi = new ProcessStartInfo
            {
                FileName = DepotDownloaderExe,
                Arguments = args,
                WorkingDirectory = DepotDownloaderDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // 实测：DepotDownloader 重定向输出使用系统 GBK（936）编码
                StandardOutputEncoding = Encoding.GetEncoding(936),
                StandardErrorEncoding = Encoding.GetEncoding(936),
            };

            using var proc = new Process { StartInfo = psi };
            proc.Start();
            var tail = new List<string>();
            var bc = new BlockingCollection<string>(new ConcurrentQueue<string>());
            ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });

            var pumpOut = PumpDepotAsync(proc.StandardOutput, bc, ct);
            var pumpErr = PumpDepotAsync(proc.StandardError, bc, ct);
            _ = Task.WhenAll(pumpOut, pumpErr).ContinueWith(_ => bc.CompleteAdding(), TaskScheduler.Default);

            var startedAt = Environment.TickCount64;
            var lastLineAt = Environment.TickCount64;
            // 有出码截止或有「拿到授权就收工」的判据时按秒轮询（否则维持原来的单次长阻塞）
            var takeMs = kickoffDeadlineMs > 0 || stopWhen is not null
                ? Math.Min(SilenceTimeoutMs, 1000) : SilenceTimeoutMs;
            var aborted = false;
            var stopAt = 0L;
            try
            {
                while (true)
                {
                    // 用户按了「立即重开」：1 秒内掐掉这一路，别让他等满 20 秒出码截止。
                    // 只在「还在等第一张码」时生效 —— 已经出码或已经在下载时掐进程会打断正经进度，
                    // 而那张码本来每 22 秒自动换一张，不需要手动踢。TakeKick 放最后，
                    // 前面任一条件不成立时不会把这次请求消费掉。
                    if (kickoffDeadlineMs > 0 && !(kickedOff?.Invoke() ?? false) && TakeKick())
                    {
                        aborted = true;
                        try { onKick?.Invoke(); } catch { }
                        try { proc.Kill(entireProcessTree: true); } catch { }
                        break;
                    }
                    // 授权一旦成功，DD 还要自己去枚举 license/appinfo/depot key（实测 4 秒；
                    // 卡住时能一路拖到 300 秒静默上限 —— 07:25:48 那次就是这么把弹窗挂死的）。
                    // 免密票据在打印「Next time you can login with -remember-password」时已落盘，
                    // 所以再等 2.5 秒收尾就掐进程，别让人对着不动的弹窗干等。
                    if (stopAt == 0 && stopWhen?.Invoke() == true) stopAt = Environment.TickCount64 + 2500;
                    if (stopAt != 0 && Environment.TickCount64 >= stopAt)
                    { try { proc.Kill(entireProcessTree: true); } catch { } break; }
                    if (!bc.TryTake(out var line, takeMs))
                    {
                        if (bc.IsCompleted) break;
                        var now = Environment.TickCount64;
                        // 实测 DD 连 Steam CM 单次成功率约 1/3，而它自己那 10 轮退避要烧掉 60 秒；
                        // 到点还没二维码就掐掉重开 —— 新进程等于重新抽一次 CM 列表，比干等退避快得多。
                        if (kickoffDeadlineMs > 0 && !(kickedOff?.Invoke() ?? false)
                            && now - startedAt >= kickoffDeadlineMs)
                        { aborted = true; try { proc.Kill(entireProcessTree: true); } catch { } break; }
                        if (kickoffDeadlineMs > 0)
                        {
                            if (now - lastLineAt < SilenceTimeoutMs) continue;
                            try { proc.Kill(entireProcessTree: true); } catch { }
                            ct.ThrowIfCancellationRequested();
                            throw new DepotException("连接 Steam 长时间无响应（可能令牌失效或网络断开）。请重试；建议 Clash 开 TUN 模式");
                        }
                        try { proc.Kill(entireProcessTree: true); } catch { }
                        ct.ThrowIfCancellationRequested();
                        throw new DepotException("连接 Steam 长时间无响应（可能令牌失效或网络断开）。请重试；建议 Clash 开 TUN 模式");
                    }
                    lastLineAt = Environment.TickCount64;
                    if (line.Length == 0) continue;
                    lock (tail) { tail.Add(line); if (tail.Count > 40) tail.RemoveAt(0); }
                    try { onLine(line); } catch { }
                }
            }
            catch (OperationCanceledException) { }

            await Task.WhenAll(pumpOut, pumpErr);
            try { await proc.WaitForExitAsync(CancellationToken.None); } catch { }
            ct.ThrowIfCancellationRequested();
            if (aborted)
            {
                // 造一条 DD 自己的失败措辞，让外层「网络类失败」判定认出它并立刻重开一路
                lock (tail) tail.Add("Connection to Steam failed (启动器 "
                    + (kickoffDeadlineMs / 1000) + " 秒内没等到出码或登录成功，重开一次)");
                return (-1, tail);
            }
            lock (tail) return (proc.ExitCode, tail);
        }, ct);
    }

    /// <summary>按行泵：换行/回车都视为行分隔（DepotDownloader 的进度条用 \r 刷新）。</summary>
    private static async Task PumpDepotAsync(StreamReader reader, BlockingCollection<string> bc, CancellationToken ct)
    {
        try
        {
            var buf = new StringBuilder();
            var chunk = new char[1024];
            while (true)
            {
                var n = 0;
                try { n = await reader.ReadAsync(chunk, ct); }
                catch { break; }
                if (n <= 0) break;
                for (var i = 0; i < n; i++)
                {
                    var c = chunk[i];
                    if (c == '\n' || c == '\r')
                    {
                        if (buf.Length > 0) { bc.Add(buf.ToString()); buf.Clear(); }
                    }
                    else buf.Append(c);
                }
            }
            if (buf.Length > 0) bc.Add(buf.ToString());
        }
        catch { }
        finally { bc.CompleteAdding(); }
    }
}
