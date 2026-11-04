using System.Collections.Concurrent;
using System.IO;

namespace JuniGrid.Services;

/// <summary>一次删除进度回调。<see cref="Percent"/> 为 null 表示还在统计、总量未知（UI 走不确定态）。</summary>
public sealed record DeleteTick(string Message, double? Percent, long DoneBytes, long TotalBytes);

/// <summary>一批删除的结果。</summary>
public sealed record DeleteOutcome(
    int Deleted, int Failed, bool Canceled, bool LeftInTrash, string? FirstError)
{
    public bool AnyFailed => Failed > 0;
}

/// <summary>
/// 可中断、可报进度的删除器。
/// <para>
/// <b>为什么要有这个类：</b>原来删除走 <see cref="ModService.Uninstall"/>，那是一个同步方法，
/// 而它的调用点是 Blazor 的 <c>@onclick</c> —— 于是 <c>Directory.Move</c> +
/// <c>Directory.Delete(recursive: true)</c> 全在渲染线程上跑，一删就是十几秒的整界面冻结。
/// </para>
/// <para>
/// <b>v1.3.19 起分两段（这是关键）：</b>
/// <list type="number">
/// <item><b>前台（毫秒级）</b>：只把目录 <c>Move</c> 进 <c>.junigrid_trash</c>。同盘改名不碰文件内容，
/// 一万个文件也是一瞬间 —— 调用方拿到的"已删除"是真的已经不在 Mods 里了。</item>
/// <item><b>后台（慢，但用户看不见）</b>：真正的逐文件删除交给单线程清理队列。
/// 实测 <c>File.Delete</c> 在本机会卡在原生调用里（杀软实时扫描），几万个文件要按分钟算；
/// 把它挡在用户面前，就变成"删完了转圈还在转"。</item>
/// </list>
/// </para>
/// <para>
/// <b>语义（刻意与 Uninstall 保持一致，不许漂）：</b>
/// <list type="number">
/// <item>先在同卷内 <c>Move</c> 到 <c>.junigrid_trash</c>（瞬时改名，保留「占用就失败」的判定）；</item>
/// <item>勾了「移入回收站」到此为止，一个字节都不删，也不进清理队列；</item>
/// <item>没勾则 Move 完就返回成功，目录随即进后台清理队列（逐文件删，失败留残骸并记日志）；</item>
/// <item>全部搬完才调 <see cref="ModService.AfterUninstallCleanup"/> 做空壳与覆盖记录清理。</item>
/// </list>
/// </para>
/// </summary>
public static class ModDeleteService
{
    /// <summary>前台搬运阶段是否在进行。watcher 的自动重扫看这个 —— 搬家的那一瞬扫盘，
    /// 会把半消失的目录当成"新发现"或"已删除"，白跑一轮。</summary>
    public static volatile bool Busy;

    /// <summary>待清理的回收站目录（Move 完就进来，由一个固定线程慢慢删）。</summary>
    private static readonly ConcurrentQueue<string> _purgeQueue = new();
    private static int _purgeWorkerRunning;

    /// <summary>当前队列里还剩多少个待清理目录（给设置页/日志看，不做 UI 阻塞）。</summary>
    public static int PendingPurgeCount => _purgeQueue.Count;

    public static Task<DeleteOutcome> DeleteAsync(
        string gamePath, IReadOnlyList<string> folders, bool toTrash,
        ConfigService? cfg, IProgress<DeleteTick>? progress, CancellationToken ct)
    {
        var snapshot = folders.ToArray();
        // 整个搬运过程都在线程池里跑；取消只靠 ct，不靠 Task 的取消令牌
        //（否则取消会把 Task 直接判为 Canceled，调用方拿不到"搬了几条"）
        return Task.Run(() => DeleteCore(gamePath, snapshot, toTrash, cfg, progress, ct), CancellationToken.None);
    }

    private static DeleteOutcome DeleteCore(
        string gamePath, string[] folders, bool toTrash,
        ConfigService? cfg, IProgress<DeleteTick>? progress, CancellationToken ct)
    {
        var n = folders.Length;
        var ok = 0;
        var failed = 0;
        string? firstError = null;
        var canceled = false;
        var leftInTrash = false;

        Busy = true;
        try
        {
            var i = 0;
            foreach (var folder in folders)
            {
                i++;
                if (ct.IsCancellationRequested) { canceled = true; break; }

                var dir = ResolveModDir(gamePath, folder);
                if (dir is null)
                {
                    failed++;
                    firstError ??= LocService.Tr("找不到 Mod 文件夹");
                    continue;
                }

                var name = folder.Contains('/') ? folder.Split('/')[^1] : folder;
                string staging;
                try { staging = PlanStaging(gamePath, folder, toTrash); }
                catch (Exception ex)
                {
                    failed++;
                    firstError ??= ex.Message;
                    continue;
                }

                // 唯一的"真实动作"：同盘改名。占用时这里直接抛（文件被别的程序打开着）。
                try
                {
                    Directory.Move(dir, staging);
                }
                catch (Exception ex)
                {
                    if (Directory.Exists(staging) && !Directory.Exists(dir))
                        try { Directory.Move(staging, dir); } catch { }
                    failed++;
                    firstError ??= ex is IOException or UnauthorizedAccessException
                        ? LocService.Tf("「{0}」正被占用，请先退出相关程序再删除", folder)
                        : ex.Message;
                    continue;
                }

                ok++;
                progress?.Report(new DeleteTick(
                    toTrash ? LocService.Tf("正在移入回收站 {0}", name) : LocService.Tf("正在移除 {0}", name),
                    i * 100.0 / n, 0, 0));

                if (!toTrash)
                {
                    // Move 之后就不归前台管了：后台逐文件删，慢多久用户都看不见。
                    // 清不干净也只是残骸留在回收站（日志有记录），不会回到 Mods。
                    EnqueuePurge(staging);
                    leftInTrash = true;
                }

                ModService.AfterUninstallCleanup(gamePath, folder, cfg);
            }
        }
        finally
        {
            Busy = false;
        }

        return new DeleteOutcome(ok, failed, canceled, leftInTrash, firstError);
    }

    // ══════════════════════════════════════════════════════════════════
    // 后台清理：单线程、串行、静默。
    // 为什么不并行：这些删除本身是磁盘+杀软瓶颈，开多线程只会让磁盘更乱、
    // 还可能与安装解压抢 IO；用户也感知不到快慢，串行最稳。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>把一个已经搬进回收站的目录排队清理。幂等：同一路径不重复入队。</summary>
    public static void EnqueuePurge(string staging)
    {
        if (string.IsNullOrWhiteSpace(staging)) return;
        _purgeQueue.Enqueue(staging);
        StartPurgeWorker();
    }

    private static void StartPurgeWorker()
    {
        if (Interlocked.CompareExchange(ref _purgeWorkerRunning, 1, 0) == 1) return;
        _ = Task.Run(() =>
        {
            try
            {
                while (_purgeQueue.TryDequeue(out var dir))
                {
                    try { PurgeTree(dir); }
                    catch (Exception ex) { AppLog.Warn("Delete", $"后台清理 {dir} 失败：{ex.Message}"); }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _purgeWorkerRunning, 0);
                // 收尾竞态：刚置 0 的瞬间可能又有新项入队，这里补捞一次
                if (!_purgeQueue.IsEmpty) StartPurgeWorker();
            }
        });
    }

    /// <summary>真正把目录删空。逐文件删（保留"被占用就跳过"的容忍度），最后清空壳。</summary>
    private static void PurgeTree(string staging)
    {
        if (!Directory.Exists(Lp(staging))) return;

        List<string> files;
        try { files = Directory.EnumerateFiles(Lp(staging), "*", SearchOption.AllDirectories).ToList(); }
        catch (Exception ex) { AppLog.Warn("Delete", $"枚举 {staging} 失败：{ex.Message}"); return; }

        var stuck = 0;
        foreach (var file in files)
        {
            try { File.Delete(file); }
            catch { stuck++; }   // 单个文件被占用不影响其余的，最后统一报一次
        }

        try { Directory.Delete(Lp(staging), recursive: true); }
        catch (Exception ex)
        {
            AppLog.Warn("Delete", $"清理 {Path.GetFileName(staging)} 未完成（{stuck} 个文件被占用）：{ex.Message}");
        }
    }

    /// <summary>Mods 下的实际目录（禁用包末段带 "." 前缀）。</summary>
    private static string? ResolveModDir(string gamePath, string folderName)
    {
        var modsDir = Path.Combine(gamePath, "Mods");
        var parts = folderName.Replace('\\', '/').Split('/');
        var parent = parts.Length == 1
            ? modsDir
            : Path.Combine(modsDir, string.Join(Path.DirectorySeparatorChar, parts, 0, parts.Length - 1));
        var dir = Path.Combine(parent, parts[^1]);
        if (Directory.Exists(dir)) return dir;
        var alt = Path.Combine(parent, "." + parts[^1]);
        return Directory.Exists(alt) ? alt : null;
    }

    /// <summary>回收站落点名。规则逐字沿用 <see cref="ModService.Uninstall"/>：
    /// 带时间戳（删了再装再删两份都留），子包保留原始层级（还原时整层拖回去）。</summary>
    private static string PlanStaging(string gamePath, string folderName, bool toTrash)
    {
        var trash = StoragePaths.GameTrashDir(gamePath);
        Directory.CreateDirectory(trash);
        var baseName = folderName.Replace('/', '_');

        if (!toTrash)
            return Path.Combine(trash, baseName + "_" + Guid.NewGuid().ToString("N")[..8]);

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        if (folderName.Contains('/'))
        {
            var staging = Path.Combine(trash, folderName.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(staging)) staging += "_" + stamp;
            for (var n = 2; Directory.Exists(staging); n++) staging += "_" + n;
            Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
            return staging;
        }
        var single = Path.Combine(trash, baseName + "_" + stamp);
        for (var n = 2; Directory.Exists(single); n++)
            single = Path.Combine(trash, $"{baseName}_{stamp}_{n}");
        return single;
    }

    /// <summary>启动时回收上一次会话没删完的残骸。
    /// <para>
    /// 只认 <c>toTrash: false</c> 的落点命名 <c>&lt;名字&gt;_&lt;8 位 hex&gt;</c>（见
    /// <see cref="PlanStaging"/>）。用户主动"移入回收站"的条目是
    /// <c>&lt;名字&gt;_&lt;yyyyMMdd_HHmmss&gt;</c> —— 那些是用户的东西，绝不能碰。
    /// 另外跳过 10 分钟内刚建的：可能是本次会话正在删的那一个。
    /// </para>
    /// </summary>
    public static void PurgeOrphanedStaging(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return;
        string trash;
        try
        {
            trash = StoragePaths.GameTrashDir(gamePath);
            if (!Directory.Exists(trash)) return;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Delete", "回收残留待删目录：定位回收站失败：" + ex.Message);
            return;
        }

        var cutoff = DateTime.UtcNow.AddMinutes(-10);
        var queued = 0;
        foreach (var dir in Directory.GetDirectories(trash))
        {
            if (!IsOnePassStagingName(Path.GetFileName(dir))) continue;
            try { if (Directory.GetCreationTimeUtc(dir) > cutoff) continue; } catch { }
            EnqueuePurge(dir);
            queued++;
        }
        if (queued > 0)
            AppLog.Info("Delete", $"回收上次会话遗留的待删目录 {queued} 个（后台清理）");
    }

    /// <summary>目录名是否是一次性删除落点：以 <c>_</c> + 8 位十六进制结尾。</summary>
    private static bool IsOnePassStagingName(string name)
    {
        // 至少要有 "x_" + 8 位
        if (name.Length < 10 || name[^9] != '_') return false;
        for (var i = name.Length - 8; i < name.Length; i++)
        {
            var c = name[i];
            var hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!hex) return false;
        }
        return true;
    }

    /// <summary>加长路径前缀 —— 有的 mod 里嵌着层层嵌套的素材目录，裸路径会撞 260 上限。</summary>
    private static string Lp(string p) =>
        p.StartsWith(@"\\?\", StringComparison.Ordinal) ? p
        : p.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + p[2..]
        : @"\\?\" + p;
}
