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
/// v1.3.14：可中断、可报进度的删除器。
/// <para>
/// <b>为什么要有这个类：</b>原来删除走 <see cref="ModService.Uninstall"/>，那是一个同步方法，
/// 而它的调用点是 Blazor 的 <c>@onclick</c> —— 于是 <c>Directory.Move</c> +
/// <c>Directory.Delete(recursive: true)</c> 全在渲染线程上跑。一个几百 MB、上万文件的
/// 大包能把这台机器上的整个界面钉死十几秒，期间动画停、点击没反应、连关闭按钮都点不动。
/// </para>
/// <para>
/// <b>语义（刻意与 Uninstall 保持一致，不许漂）：</b>
/// <list type="number">
/// <item>先在同卷内 <c>Move</c> 到 <c>.junigrid_trash</c>（瞬时改名，保留「占用就失败」的判定）；</item>
/// <item>勾了「移入回收站」到此为止，一个字节都不删；</item>
/// <item>没勾则逐个文件删，边删边报进度，随时可取消；</item>
/// <item><b>取消</b>：立即停手，把还没删的整棵子树<b>移回原位</b>。已经删掉的文件回不来，
/// 所以文案必须说实话（"已取消，剩余已还原"），不能装作什么都没发生。
/// 万一移不回去（目标又被占了），就把残留留在回收站并明确告知 —— 东西还在，能手动还原。</item>
/// <item>全部删完才调 <see cref="ModService.AfterUninstallCleanup"/> 做空壳与覆盖记录清理。</item>
/// </list>
/// </para>
/// </summary>
public static class ModDeleteService
{
    /// <summary>进度回调节流：90ms 一次。删除一个上万文件的包时，不节流就是上万次
    /// <c>InvokeAsync</c> —— 那本身就是另一种卡死。</summary>
    private const int TickMs = 90;

    public static Task<DeleteOutcome> DeleteAsync(
        string gamePath, IReadOnlyList<string> folders, bool toTrash,
        ConfigService? cfg, IProgress<DeleteTick>? progress, CancellationToken ct)
    {
        var snapshot = folders.ToArray();
        // 整个删除过程都在线程池里跑；取消只靠 ct，不靠 Task 的取消令牌
        //（否则取消会把 Task 直接判为 Canceled，调用方拿不到"删到哪一步了"）
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
        var doneBytes = 0L;
        var totalBytes = 0L;

        // ── 阶段 1：统计总量 ────────────────────────────────────────────
        // 不统计的话进度条只能一直停在 0，用户分不清"在删"还是"卡住了"。
        // 勾了回收站就不用统计 —— 那一步只是改名，快到不需要百分比。
        if (!toTrash)
        {
            progress?.Report(new DeleteTick(LocService.Tr("正在统计文件…"), null, 0, 0));
            foreach (var f in folders)
            {
                if (ct.IsCancellationRequested) return new(0, 0, true, false, null);   // 一个字节都没动，干净退出
                var d = ResolveModDir(gamePath, f);
                if (d is null) continue;
                totalBytes += MeasureBytes(d, ct);
            }
            if (ct.IsCancellationRequested) return new(0, 0, true, false, null);
        }

        // ── 阶段 2：逐个搬运 + 删除 ─────────────────────────────────────
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

            try
            {
                Directory.Move(dir, staging);   // 同盘瞬时改名，占用时这里直接抛
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

            if (toTrash)
            {
                ok++;
                progress?.Report(new DeleteTick(
                    LocService.Tf("正在移入回收站 {0}", name),
                    i * 100.0 / n, doneBytes, totalBytes));
                ModService.AfterUninstallCleanup(gamePath, folder, cfg);
                continue;
            }

            // ── 物理删除：边删边报，随时可停 ──
            var res = DeleteTree(staging, dir, name, totalBytes, i, n, progress, ct, ref doneBytes);
            if (res.Canceled)
            {
                canceled = true;
                leftInTrash |= res.LeftInTrash;
                break;                      // 这条没删完，不算成功也不算失败
            }
            if (!res.Clean)
            {
                failed++;
                firstError ??= res.Error;
                leftInTrash = true;         // 残留还在回收站里，别让用户以为删干净了
                continue;
            }
            ok++;
            ModService.AfterUninstallCleanup(gamePath, folder, cfg);
        }

        if (!canceled && !toTrash)
            progress?.Report(new DeleteTick(LocService.Tr("正在删除…"), 100, doneBytes, totalBytes));

        return new DeleteOutcome(ok, failed, canceled, leftInTrash, firstError);
    }

    private sealed record TreeResult(bool Clean, bool Canceled, bool LeftInTrash, string? Error);

    /// <summary>把一个已经搬进回收站的目录删空。返回它是否删干净 / 是否被取消。</summary>
    private static TreeResult DeleteTree(
        string staging, string origin, string name,
        long totalBytes, int index, int count,
        IProgress<DeleteTick>? progress, CancellationToken ct, ref long doneBytes)
    {
        var lastTick = 0L;
        string? err = null;

        List<string> files;
        try { files = Directory.EnumerateFiles(Lp(staging), "*", SearchOption.AllDirectories).ToList(); }
        catch (Exception ex) { return new TreeResult(false, false, true, ex.Message); }

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested)
            {
                // 停手 + 把剩下的挪回去。已经删掉的回不来 —— 这一点由调用方的文案说明。
                var moved = TryMoveBack(staging, origin);
                return new TreeResult(false, true, !moved, err);
            }

            long len = 0;
            try { len = new FileInfo(file).Length; } catch { }
            try
            {
                File.Delete(file);
                doneBytes += len;
            }
            catch (Exception ex)
            {
                // 单个文件被占用不该让整批停下来：继续删别的，最后统一报
                err ??= ex is UnauthorizedAccessException or IOException
                    ? LocService.Tf("「{0}」里有文件正被占用", name)
                    : ex.Message;
                continue;
            }

            var now = Environment.TickCount64;
            if (now - lastTick >= TickMs)
            {
                lastTick = now;
                var inner = totalBytes > 0 ? Math.Min(1.0, (double)doneBytes / totalBytes) : 0;
                var overall = Math.Min(99.5, (index - 1 + inner) * 100.0 / count);
                progress?.Report(new DeleteTick(
                    LocService.Tf("正在删除 {0}", name), overall, doneBytes, totalBytes));
            }
        }

        // 文件都清了，剩下的是空目录壳 —— 这时整棵递归删是毫秒级，不用再分批
        try { Directory.Delete(Lp(staging), recursive: true); }
        catch (Exception ex)
        {
            err ??= ex is UnauthorizedAccessException or IOException
                ? LocService.Tf("「{0}」里有文件正被占用", name)
                : ex.Message;
            return new TreeResult(false, false, true, err);
        }

        return new TreeResult(err is null, false, err is not null, err);
    }

    /// <summary>取消时把还没删的子树搬回原处。成功才算"干净取消"。</summary>
    private static bool TryMoveBack(string staging, string origin)
    {
        try
        {
            if (!Directory.Exists(staging)) return true;             // 已经空了，等于没东西要还
            if (!Directory.Exists(origin)) { Directory.Move(staging, origin); return true; }
            return false;                                            // 原位竟被占了：留在回收站
        }
        catch (Exception ex)
        {
            AppLog.Warn("Delete", $"取消删除时无法把 {staging} 移回 {origin}：{ex.Message}");
            return false;
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

    /// <summary>目录里所有文件的字节总和（用于进度分母）。枚举失败就当 0 —— 进度条退回不确定态，
    /// 但删除照常进行。</summary>
    private static long MeasureBytes(string dir, CancellationToken ct)
    {
        long sum = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(Lp(dir), "*", SearchOption.AllDirectories))
            {
                if (ct.IsCancellationRequested) break;
                try { sum += new FileInfo(f).Length; } catch { }
            }
        }
        catch { }
        return sum;
    }

    /// <summary>加长路径前缀 —— 有的 mod 里嵌着层层嵌套的素材目录，裸路径会撞 260 上限。</summary>
    private static string Lp(string p) =>
        p.StartsWith(@"\\?\", StringComparison.Ordinal) ? p
        : p.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + p[2..]
        : @"\\?\" + p;
}
