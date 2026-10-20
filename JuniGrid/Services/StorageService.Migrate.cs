using System.IO;

namespace JuniGrid.Services;

public sealed partial class StorageService
{
    public async Task<string> MigrateCacheRootAsync(string? newRoot)
    {
        newRoot = string.IsNullOrWhiteSpace(newRoot) ? null : Path.GetFullPath(newRoot.Trim());

        // 先验落点再动盘：一旦 CreateDirectory + 写配置，用户就以为设好了
        var reject = CacheRootRejection(newRoot, AppContext.BaseDirectory, _cfg.Current.GamePath);
        if (reject is not null) return reject;

        if (newRoot is not null) Directory.CreateDirectory(newRoot);

        // 保存前先抓旧位置（SyncStoragePaths 会把解析结果切到新根）
        var oldDownloads = StoragePaths.DownloadsDir;
        var oldSmapi = StoragePaths.SmapiInstallerDir;
        var oldBackup = StoragePaths.ModsBackupDir;
        var oldWv2 = StoragePaths.WebView2Dir;
        var oldSelfUpdate = StoragePaths.SelfUpdateDir;
        var oldVerCache = StoragePaths.DepotStagingDir;
        var oldThumbs = StoragePaths.InCache("portrait-covers");

        if (SelfUpdateService.CacheBusy)
            return LocService.Tr("自更新安装包正在下载，稍后再更改缓存目录");

        var cfg = _cfg.Current;
        cfg.CacheRoot = newRoot;
        _cfg.Save(cfg);

        var task = _center.Start(newRoot is null ? "恢复默认缓存位置" : "迁移缓存目录", "cleanup");
        _center.Report(task, newRoot is null ? LocService.Tr("正在恢复默认位置…") : LocService.Tf("目标：{0}", newRoot), 5);
        return await Task.Run(() =>
        {
            // v1.09.0：这个委托原先【裸奔】—— 任何异常都会逸出 Task.Run，
            // 而 Finish 只写在最后一行 ⇒ 任务永远停在 running（和 InstallService
            // 那四处理由同源：异常没人收尾）。这里整体裹进 try/catch/finally，
            // 失败一律落 failed + 带原因，running 的行绝不留给用户去猜。
            try
            {
                long moved = 0, skipped = 0;
                var pairs = new (string oldDir, string target)[]
                {
                    (oldDownloads, StoragePaths.DownloadsDir),
                    (oldSmapi, StoragePaths.SmapiInstallerDir),
                    (oldBackup, StoragePaths.ModsBackupDir),
                    (oldSelfUpdate, StoragePaths.SelfUpdateDir),
                    (oldVerCache, StoragePaths.DepotStagingDir),
                    // 素材缩略图缓存跟着走 —— 不迁的话换根后素材包页整页空白重建
                    (oldThumbs, StoragePaths.InCache("portrait-covers")),
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
                    _center.Report(task, LocService.Tr("WebView2 数据将在重启后自动迁移"), 90);
                }

                var msg = newRoot is null
                    ? "已恢复默认位置" + wv2Note
                    : $"迁移完成：挪入 {ResumableDownload.FormatBytes(moved)}" +
                      (skipped > 0 ? $"，{ResumableDownload.FormatBytes(skipped)} 正在使用留在原目录" : "") + wv2Note;
                _center.Finish(task, true, msg);
                AppLog.Warn("Storage", $"缓存目录迁移到 {newRoot ?? "<默认>"}：挪入 {moved}，跳过 {skipped}");
                // 缩略图虚拟主机跟着新缓存根重新映射，否则短 URL 全 404
                try { PackThumbService.RemapThumbHost?.Invoke(); } catch { }
                lock (_gate) _sizes.Clear();
                RefreshAll(force: true);
                return msg;
            }
            catch (Exception ex)
            {
            // 落终态，绝不让这行永远 running（用户会以为还在跑）
            _center.Finish(task, false, LocService.Tr("迁移缓存目录失败：") + ex.Message);
            AppLog.Warn("Storage", "缓存目录迁移失败：" + ex.Message);
            return LocService.Tr("迁移缓存目录失败：") + ex.Message;
            }
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
                ModService.CopyDirectoryContents(srcDir, dstDir);
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

    /// <summary>v1.2.7 一次性搬家：把历史默认位置（<c>%TEMP%\JuniGrid</c>）里的可迁移项
    /// 挪进当前的默认缓存落点。
    /// <para>为什么必须存在：默认落点从临时目录改成安装目录，等于改了「配置里那一栏空着」的含义。
    /// 存量「从没设过缓存目录」的人，东西全都躺在临时目录里；不搬的话新默认一上线，
    /// 应用就去新位置找一个空壳 —— Nexus 登录态、SMAPI 更新前的 Mods 备份、几百 MB 版本缓存
    /// 当场"消失"（这正是 <c>InstallMigrate</c> 注释里警告的那个失效形态）。</para>
    /// <para>只在用户<b>没设过</b>统一缓存目录时动手：设过的人自己的位置一个字节都不碰。
    /// webview2 搬不动时返回它的旧路径，由调用方记进 <c>PendingWebView2MoveFrom</c>，
    /// 本会话继续用旧目录 —— 绝不允许"静默切到空的新目录"。
    /// 新位置已经有同名非空目录时也不搬（两处都有 ⇒ 留给用户在存储页自己清，不替他决定）。</para>
    /// <param name="cacheRoot">配置里那一栏的原值（非空 = 用户自己指过，直接跳过）。</param>
    /// <param name="webview2Stuck">返回搬不动的 webview2 旧路径；没有则 null。</param>
    /// <param name="moved">实际搬走的项数。</param>
    /// <param name="sourceRoot">来源根，默认历史默认位置（%TEMP%\JuniGrid）；只给夹具注入临时目录。</param>
    /// <param name="targetRoot">目标根，默认当前缓存落点；只给夹具注入临时目录。</param>
    internal static void MigrateFromLegacyTempRoot(string? cacheRoot,
        out string? webview2Stuck, out int moved,
        string? sourceRoot = null, string? targetRoot = null)
    {
        webview2Stuck = null;
        moved = 0;
        if (!string.IsNullOrWhiteSpace(cacheRoot)) return;                 // 用户自己指过位置：一个字节都不动
        var srcRoot = sourceRoot ?? StoragePaths.TempRoot;
        var dstRoot = targetRoot ?? StoragePaths.DefaultCacheRoot;
        if (!Directory.Exists(srcRoot)) return;                            // 没住过那个位置

        // 旧位置里有啥就搬啥 —— 不查任何名单。名单是第三份手抄表，应用以后新加一个缓存目录而
        // 这里没跟上，那一类就会静默留在旧位置（M10 只比对安装器那两张，钉不住这里）。
        string[] legacyEntries;
        try { legacyEntries = Directory.GetFileSystemEntries(srcRoot); }
        catch (Exception ex) { AppLog.Warn("Storage", "[缓存搬家] 旧位置读不出来：" + ex.Message); return; }

        static bool TryMoveFile(string src, string dst)
        {
            try { File.Move(src, dst); return true; }
            catch
            {
                try { File.Copy(src, dst, true); File.Delete(src); return true; }
                catch { try { File.Delete(dst); } catch { } return false; }
            }
        }

        foreach (var src in legacyEntries)
        {
            var name = Path.GetFileName(src);
            var dst = Path.Combine(dstRoot, name);
            var isDir = Directory.Exists(src);
            try
            {
                if (isDir && Directory.Exists(dst))
                {
                    if (Directory.EnumerateFileSystemEntries(dst).Any())
                    {
                        // 两处都有：用新位置那份（应用现在认的就是它），旧位置留给存储页统计+自行清理。
                        // 这里不能顺手合并 —— 两边都可能有用户没备份过的东西，替他选哪边活下来不是搬家该做的事。
                        AppLog.Warn("Storage", $"[缓存搬家] 新旧两处都有 {name}，两边都不动（可在存储页清理旧位置）");
                        continue;
                    }
                    Directory.Delete(dst, recursive: false);               // 空壳：挪开让路，同盘 Move 才不撞
                }
                else if (!isDir && File.Exists(dst))
                {
                    AppLog.Warn("Storage", $"[缓存搬家] 新旧两处都有散装文件 {name}，两边都不动");
                    continue;
                }
                Directory.CreateDirectory(dstRoot);
                if (isDir ? TryMoveTree(src, dst) : TryMoveFile(src, dst)) { moved++; continue; }
                AppLog.Warn("Storage", $"[缓存搬家] {name} 占用中，留在原处下次启动再搬：{src}");
            }
            catch (Exception ex)
            {
                AppLog.Warn("Storage", $"[缓存搬家] {name} 没搬动：{ex.Message}");
            }
            if (isDir && name == "webview2") webview2Stuck = src;          // 只有搬不成才当它卡住
        }

        try
        {
            if (Directory.Exists(srcRoot) && !Directory.EnumerateFileSystemEntries(srcRoot).Any())
                Directory.Delete(srcRoot);                                 // 只删确实空了的壳
        }
        catch { }
    }
}
