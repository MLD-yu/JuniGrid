using System.IO;

namespace JuniGrid.Services;

/// <summary>缓存与存储页的一个分类行：显示路径 + 占用 + 可否清理/迁移。
/// Tip 是悬浮「?」里的通俗解释（面向不了解 terminology 的用户）。</summary>
public sealed record StorageCategory(
    string Id,
    string Name,
    string Note,
    string DisplayPath,
    string[] SizeRoots,     // 统计占用的根（文件或目录混合）
    string[] CleanRoots,    // 清理时删除内容的根
    bool Cleanable,
    bool Movable,
    string Tip = "");

/// <summary>
/// v0.2.1：缓存与存储管理 —— 各类缓存占用统计、单项/一键清理、统一缓存目录更改与迁移。
/// 统计在后台算（可能几秒），结果经 OnStats 通知 UI；清理走任务中心（kind=cleanup），
/// 逐文件删除、被占用的跳过不中断。
/// </summary>
public sealed class StorageService
{
    private readonly ConfigService _cfg;
    private readonly TaskCenterService _center;

    public StorageService(ConfigService cfg, TaskCenterService center)
    {
        _cfg = cfg;
        _center = center;
    }

    /// <summary>某项占用计算完成/刷新后触发（可能后台线程，UI 订阅方自行调度）。</summary>
    public event Action? OnStats;

    private readonly object _gate = new();
    private readonly Dictionary<string, long> _sizes = new();     // id → 字节；缺失 = 未计算
    private readonly HashSet<string> _computing = new(StringComparer.Ordinal);
    private DateTime _lastRefreshUtc = DateTime.MinValue;

    public bool IsComputing(string id) { lock (_gate) return _computing.Contains(id); }
    public long GetSize(string id) { lock (_gate) return _sizes.TryGetValue(id, out var v) ? v : -1; }

    /// <summary>可清理各项的已知占用合计（未算出的项不计入）。</summary>
    public long TotalKnownBytes
    {
        get { lock (_gate) return _sizes.Where(kv => kv.Key != "data" && kv.Value > 0).Sum(kv => kv.Value); }
    }

    /// <summary>分类清单（每次现建：备份清理范围、游戏回收站都依赖当前配置）。</summary>
    public List<StorageCategory> GetCategories()
    {
        var list = new List<StorageCategory>();

        list.Add(new("downloads", "下载与安装临时", "下载 zip 与解压临时文件",
            StoragePaths.DownloadsDir,
            new[] { StoragePaths.DownloadsDir }, new[] { StoragePaths.DownloadsDir },
            Cleanable: true, Movable: true,
            Tip: "从 Nexus 或 GitHub 下载 mod 时的压缩包和解压中间产物 安装完成后就没用了 可放心清理"));

        list.Add(new("smapi", "SMAPI 安装包缓存", "安装器下载与解压产物",
            StoragePaths.SmapiInstallerDir,
            new[] { StoragePaths.SmapiInstallerDir }, new[] { StoragePaths.SmapiInstallerDir },
            Cleanable: true, Movable: true,
            Tip: "安装或更新 SMAPI 时下载的官方安装包和解压文件 装完就没用了 可放心清理"));

        // 只统计/清理 HTTP 与着色器缓存子目录（目录名对照本机 EBWebView 实测结构）——
        // Cookie/LocalStorage 在其它子目录，登录态不受影响
        var wv2Root = StoragePaths.WebView2Dir;
        var wv2Caches = new[]
        {
            Path.Combine(wv2Root, "EBWebView", "Default", "Cache"),
            Path.Combine(wv2Root, "EBWebView", "Default", "Code Cache"),
            Path.Combine(wv2Root, "EBWebView", "Default", "GPUCache"),
            Path.Combine(wv2Root, "EBWebView", "Default", "DawnGraphiteCache"),
            Path.Combine(wv2Root, "EBWebView", "Default", "DawnWebGPUCache"),
            Path.Combine(wv2Root, "EBWebView", "GrShaderCache"),
            Path.Combine(wv2Root, "EBWebView", "ShaderCache"),
        };
        list.Add(new("wv2", "WebView2 网络缓存", "网页与图片缓存（保留登录态）", wv2Root,
            wv2Caches, wv2Caches, Cleanable: true, Movable: true,
            Tip: "整个界面就是一套网页组件 加载 mod 封面等图片时留下的网络缓存 清理不影响 Nexus 登录状态 更改缓存位置后 重启应用生效"));

        // 更新前的 Mods 安全快照：清理 = 保留最近一次，其余全删（防呆：目录名是时间戳）
        var backupRoot = StoragePaths.ModsBackupDir;
        var oldSnapshots = SafeDirs(backupRoot).OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase).Skip(1).ToArray();
        list.Add(new("backup", "更新前 Mods 备份", "SMAPI 更新前的安全快照（清理保留最近一次）", backupRoot,
            new[] { backupRoot }, oldSnapshots, Cleanable: oldSnapshots.Length > 0, Movable: true,
            Tip: "更新 SMAPI 前自动把全部 mod 备份一份 更新失败可用它恢复 清理只删较旧的备份 最近一次永远保留"));

        var logFiles = new[]
        {
            Path.Combine(StoragePaths.AppDataDir, "juni-grid.log"),
            Path.Combine(StoragePaths.AppDataDir, "juni-grid.log.old"),
            Path.Combine(StoragePaths.AppDataDir, "startup.log"),
            Path.Combine(StoragePaths.AppDataDir, "crash.log"),
        };
        list.Add(new("logs", "日志文件", "运行与崩溃日志", StoragePaths.AppDataDir,
            logFiles, logFiles, Cleanable: true, Movable: false,
            Tip: "程序运行和崩溃时记录的文字日志 只用于排查问题 清理不影响任何功能"));

        var gp = _cfg.Current.GamePath;
        if (!string.IsNullOrWhiteSpace(gp))
        {
            var trash = StoragePaths.GameTrashDir(gp);
            list.Add(new("trash", "游戏卸载回收站", "卸载 mod 时的回收站", trash,
                new[] { trash }, new[] { trash }, Cleanable: true, Movable: false,
                Tip: "卸载 mod 时文件先移到这里而不是直接删除 清空后那些 mod 才真正消失 且无法恢复 请确认不再需要"));
        }

        var dataFiles = new[]
        {
            Path.Combine(StoragePaths.AppDataDir, "junigrid.config.json"),
            Path.Combine(StoragePaths.AppDataDir, "tasks.json"),
        };
        list.Add(new("data", "设置与任务数据", "配置与任务记录（固定·不可清理）", StoragePaths.AppDataDir,
            dataFiles, Array.Empty<string>(), Cleanable: false, Movable: false,
            Tip: "你的设置 Nexus API Key mod 存档列表和下载任务记录 属于个人数据 程序永远不会自动清理它"));

        return list;
    }

    /// <summary>刷新全部占用（30 秒内已刷过则跳过，除非 force）。不关心结果的地方用。</summary>
    public void RefreshAll(bool force = false) => _ = RefreshAllAsync(force);

    /// <summary>刷新全部占用并等待完成 —— 全部项都算出占用返回 true，任一项算失败返回 false
    /// （供「刷新占用」按钮弹 toast 上报成功/失败）。</summary>
    public async Task<bool> RefreshAllAsync(bool force = false)
    {
        if (!force && DateTime.UtcNow - _lastRefreshUtc < TimeSpan.FromSeconds(30)) return true;
        _lastRefreshUtc = DateTime.UtcNow;
        var results = await Task.WhenAll(GetCategories().Select(ComputeAsync)).ConfigureAwait(false);
        return results.All(ok => ok);
    }

    private async Task<bool> ComputeAsync(StorageCategory c)
    {
        lock (_gate) { if (!_computing.Add(c.Id)) return true; }   // 已在算，视为进行中即成功
        try
        {
            var roots = c.SizeRoots;
            var bytes = await Task.Run(() =>
            {
                long sum = 0;
                foreach (var root in roots) sum += DirSize(root);
                return sum;
            }).ConfigureAwait(false);
            lock (_gate) _sizes[c.Id] = bytes;
            OnStats?.Invoke();
            return true;
        }
        catch { return false; }
        finally
        {
            lock (_gate) _computing.Remove(c.Id);
        }
    }

    private static long DirSize(string root)
    {
        try
        {
            if (File.Exists(root)) return new FileInfo(root).Length;
            if (!Directory.Exists(root)) return 0;
            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            long sum = 0;
            foreach (var f in Directory.EnumerateFiles(root, "*", opts))
            {
                try { sum += new FileInfo(f).Length; } catch { }
            }
            return sum;
        }
        catch { return 0; }
    }

    /// <summary>
    /// 清理一个分类。走任务中心报进度；逐文件删除，被占用的跳过。
    /// 返回给 toast 的结果一句话。下载/安装类目录有任务进行中时拒绝清理（会删掉正在用的 zip）。
    /// </summary>
    public Task<string> CleanCategoryAsync(string id)
    {
        var c = GetCategories().FirstOrDefault(x => x.Id == id);
        if (c is null || !c.Cleanable) return Task.FromResult("这一项不可清理");
        if ((c.Id == "downloads" || c.Id == "smapi") && _center.RunningCount > 0)
            return Task.FromResult("有下载/安装任务进行中，结束后再清理");

        var task = _center.Start("清理：" + c.Name, "cleanup");
        _center.Report(task, "开始清理…", 3);

        var roots = c.CleanRoots;
        var pruneDirs = roots.Any(Directory.Exists);   // 目录根才需要收尾空壳
        return Task.Run(() =>
        {
            long freed = 0, skipped = 0;
            var files = CollectFiles(roots);
            for (var i = 0; i < files.Count; i++)
            {
                try
                {
                    var len = new FileInfo(files[i]).Length;
                    File.Delete(files[i]);
                    freed += len;
                }
                catch { skipped++; }   // 占用中/权限不足：跳过，不中断
                if (i % 50 == 0 || i == files.Count - 1)
                    _center.Report(task,
                        $"已清理 {ResumableDownload.FormatBytes(freed)}（跳过占用中 {skipped} 个）",
                        3 + 92.0 * (i + 1) / Math.Max(1, files.Count));
            }
            if (pruneDirs)
                foreach (var root in roots) PruneEmptyDirs(root);

            var msg = files.Count == 0
                ? "这里已经很干净了"
                : $"清理完成：释放 {ResumableDownload.FormatBytes(freed)}" +
                  (skipped > 0 ? $"，跳过占用中的 {skipped} 个文件" : "");
            _center.Finish(task, true, msg);
            AppLog.Warn("Storage", $"清理[{c.Id}] 释放 {freed} 字节，跳过 {skipped}");
            lock (_gate) _sizes[c.Id] = DirSizeSum(roots);
            OnStats?.Invoke();
            return msg;
        });
    }

    private static long DirSizeSum(string[] roots) => roots.Sum(DirSize);

    private static List<string> CollectFiles(IEnumerable<string> roots)
    {
        var files = new List<string>();
        foreach (var root in roots)
        {
            try
            {
                if (File.Exists(root)) { files.Add(root); continue; }
                if (!Directory.Exists(root)) continue;
                files.AddRange(Directory.EnumerateFiles(root, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true
                }));
            }
            catch { }
        }
        return files;
    }

    /// <summary>清完后把空目录壳一并摘掉（保留根目录本身，服务还要往里写）。</summary>
    private static void PruneEmptyDirs(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            var dirs = Directory.EnumerateDirectories(root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = 0
            }).OrderByDescending(d => d.Length);   // 先删最深的，父目录才空得出来
            foreach (var dir in dirs)
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>列出目录下的一级子目录全路径（不存在/不可读返回空）。</summary>
    private static List<string> SafeDirs(string dir)
    {
        try
        {
            return Directory.Exists(dir)
                ? Directory.GetDirectories(dir).ToList()
                : new List<string>();
        }
        catch { return new List<string>(); }
    }

    /// <summary>
    /// 更改统一缓存目录（newDir = null 表示恢复默认位置）：下载/安装临时、SMAPI 安装包、
    /// Mods 备份三处立即切换并现场搬迁现有内容；WebView2 数据正被占用，记入
    /// PendingWebView2MoveFrom，由 MainWindow 在下次启动（WebView2 初始化之前）自动搬迁。
    /// </summary>
    public async Task<string> MigrateCacheRootAsync(string? newRoot)
    {
        newRoot = string.IsNullOrWhiteSpace(newRoot) ? null : Path.GetFullPath(newRoot.Trim());
        if (newRoot is not null) Directory.CreateDirectory(newRoot);

        // 保存前先抓旧位置（SyncStoragePaths 会把解析结果切到新根）
        var oldDownloads = StoragePaths.DownloadsDir;
        var oldSmapi = StoragePaths.SmapiInstallerDir;
        var oldBackup = StoragePaths.ModsBackupDir;
        var oldWv2 = StoragePaths.WebView2Dir;

        var cfg = _cfg.Current;
        cfg.CacheRoot = newRoot;
        _cfg.Save(cfg);

        var task = _center.Start(newRoot is null ? "恢复默认缓存位置" : "迁移缓存目录", "cleanup");
        _center.Report(task, newRoot is null ? "正在恢复默认位置…" : $"目标：{newRoot}", 5);
        return await Task.Run(() =>
        {
            long moved = 0, skipped = 0;
            var pairs = new (string oldDir, string target)[]
            {
                (oldDownloads, StoragePaths.DownloadsDir),
                (oldSmapi, StoragePaths.SmapiInstallerDir),
                (oldBackup, StoragePaths.ModsBackupDir),
            };
            var movedNotes = new List<string>();
            foreach (var (oldDir, target) in pairs)
            {
                if (string.Equals(Path.GetFullPath(oldDir), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)
                    || !Directory.Exists(oldDir))
                    continue;
                Directory.CreateDirectory(target);
                var m0 = moved; var s0 = skipped;
                MoveInto(oldDir, target, ref moved, ref skipped);
                if (moved + skipped > m0 + s0)
                {
                    movedNotes.Add($"{Path.GetFileName(oldDir)} → {Path.GetFileName(target)}");
                    // 旧目录搬空了就顺手删掉空壳（被占用的文件留在里面则保留）
                    try
                    {
                        if (Directory.Exists(oldDir) && !Directory.EnumerateFileSystemEntries(oldDir).Any())
                            Directory.Delete(oldDir);
                    }
                    catch { }
                }
            }

            // WebView2 正被本进程占用 → 记遗留迁移，MainWindow 下次启动（WebView2 初始化前）自动搬
            var wv2Note = "";
            if (!string.Equals(Path.GetFullPath(oldWv2), Path.GetFullPath(StoragePaths.WebView2Dir), StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(oldWv2))
            {
                cfg.PendingWebView2MoveFrom = oldWv2;
                _cfg.Save(cfg);
                wv2Note = "；WebView2 数据将在重启应用后自动迁移";
                _center.Report(task, "WebView2 数据将在重启后自动迁移", 90);
            }

            var msg = newRoot is null
                ? "已恢复默认位置" + wv2Note
                : $"迁移完成：挪入 {ResumableDownload.FormatBytes(moved)}" +
                  (skipped > 0 ? $"，{ResumableDownload.FormatBytes(skipped)} 正在使用留在原目录" : "") + wv2Note;
            _center.Finish(task, true, msg);
            AppLog.Warn("Storage", $"缓存目录迁移到 {newRoot ?? "<默认>"}：挪入 {moved}，跳过 {skipped}");
            lock (_gate) _sizes.Clear();
            RefreshAll(force: true);
            return msg;
        }).ConfigureAwait(false);
    }

    /// <summary>把 src 里的所有内容挪进 dstDir（逐项尝试 Move，失败复制+删源，仍失败计 skipped）。</summary>
    private static void MoveInto(string src, string dstDir, ref long moved, ref long skipped)
    {
        try
        {
            if (!Directory.Exists(src)) return;
            Directory.CreateDirectory(dstDir);
            foreach (var srcDir in Directory.GetDirectories(src))
            {
                var dst = Path.Combine(dstDir, Path.GetFileName(srcDir));
                var len = DirSize(srcDir);
                if (TryMoveTree(srcDir, dst)) moved += len; else skipped += len;
            }
            foreach (var srcFile in Directory.GetFiles(src))
            {
                var dst = Path.Combine(dstDir, Path.GetFileName(srcFile));
                var len = new FileInfo(srcFile).Length;
                try
                {
                    File.Move(srcFile, dst, overwrite: true);
                    moved += len;
                }
                catch
                {
                    try
                    {
                        File.Copy(srcFile, dst, overwrite: true);
                        File.Delete(srcFile);
                        moved += len;
                    }
                    catch { skipped += len; }
                }
            }
        }
        catch (Exception ex) { AppLog.Warn("Storage", "迁移目录失败: " + ex.Message); }
    }

    /// <summary>整树挪动：同盘直接 Move；跨盘/占用时逐文件复制+删源，部分文件占用算失败（整树留在原地）。
    /// 供 MainWindow 启动时执行 WebView2 数据的遗留迁移。</summary>
    public static bool TryMoveTree(string srcDir, string dstDir)
    {
        try
        {
            Directory.Move(srcDir, dstDir);
            return true;
        }
        catch
        {
            try
            {
                CopyTree(srcDir, dstDir);
                Directory.Delete(srcDir, recursive: true);
                return true;
            }
            catch
            {
                try { if (Directory.Exists(dstDir)) Directory.Delete(dstDir, recursive: true); } catch { }
                return false;
            }
        }
    }

    private static void CopyTree(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.GetDirectories(src))
            CopyTree(d, Path.Combine(dst, Path.GetFileName(d)));
    }
}
