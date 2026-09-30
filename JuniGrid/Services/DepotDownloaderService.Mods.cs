using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace JuniGrid.Services;

public sealed partial class DepotDownloaderService
{
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
                progress?.Report(new Progress(LocService.Tr("Mods 目录正在被写入，等复制结束后再切换…"), null));
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

}
