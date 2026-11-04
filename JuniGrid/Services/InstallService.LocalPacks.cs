using System.IO;

namespace JuniGrid.Services;

/// <summary>
/// 本地 mod 压缩包的入口判据 + 安装管线。
/// <para>与 Nexus 三条通道（直装 / .nxm / 依赖直装）共用同一个
/// <see cref="ModService.InstallNew"/> 与同一把 <see cref="EnterInstallGateAsync"/> 写入锁 ——
/// 本地包没有任何特殊形状，缺的只是一条"把磁盘路径递进去"的通路。</para>
/// <para>本文件只做<b>粗筛</b>：扩展名 + 存在性。真正的格式识别（魔数、rar 冒充 zip、
/// 7z）由 <see cref="ModService.InstallNew"/> 内部完成，这里刻意不打开文件 ——
/// 粗筛要在 WPF 的 Drop 事件里同步跑完，不能为了看一眼魔数去 IO 几十个文件。</para>
/// </summary>
public sealed partial class InstallService
{
    /// <summary>本地包认的三种扩展名。与 <c>ModService.DetectArchiveFormat</c> 的覆盖面一致。</summary>
    private static readonly string[] PackExtensions = { ".zip", ".7z", ".rar" };

    /// <summary>扩展名是不是压缩包。<b>纯函数</b>，不碰磁盘 —— 单测不必造临时文件。
    /// <para>用 <see cref="Path.GetExtension"/> 而不是 <c>EndsWith</c>：
    /// 后者会把 <c>foo.zip.txt</c> 判成压缩包。</para></summary>
    internal static bool HasPackExtension(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = Path.GetExtension(path);
        if (ext.Length == 0) return false;
        foreach (var e in PackExtensions)
            if (string.Equals(ext, e, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>这是一个能交给 <see cref="ModService.InstallNew"/> 的本地包：
    /// 扩展名对、文件真的在、且不是目录（同名目录 + 扩展名的情况在 Windows 上不存在，
    /// 但用户可能拖一个叫 <c>xxx.zip</c> 的文件夹进来）。</summary>
    internal static bool IsLocalPackPath(string? path)
    {
        if (!HasPackExtension(path)) return false;
        try
        {
            if (Directory.Exists(path)) return false;
            return File.Exists(path);
        }
        catch (Exception ex) { AppLog.Warn("Install", "检查本地包路径失败：" + ex.Message); return false; }
    }

    /// <summary>过滤 + 去重。同一路径拖两次只装一次（Windows 路径大小写不敏感）。</summary>
    internal static IReadOnlyList<string> FilterLocalPacks(IEnumerable<string>? paths)
    {
        if (paths is null) return Array.Empty<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>();
        foreach (var p in paths)
            if (IsLocalPackPath(p) && seen.Add(p)) kept.Add(p);
        return kept;
    }

    /// <summary>
    /// 把一批本地压缩包装进 <c>Mods/</c>。返回成功安装的个数。
    /// <para>每个包在任务中心开一条 <c>kind="install"</c> 的行（与 Nexus 直装同款），
    /// 逐个串行写入 —— <see cref="_installGate"/> 本来就是全局串行，但显式串行还能保证
    /// 任务中心里的顺序与用户拖入顺序一致。</para>
    /// <para>失败<b>不中断</b>：某个包没有 manifest / 覆盖包认不出宿主 / UID 撞车，
    /// 记下原因继续装下一个。失败原因写在各任务行的终态消息里。</para>
    /// <para>全部结束后<b>只发一次</b> <see cref="Notify"/>：Mods 页的
    /// <c>OnInstallerChanged</c> 收到就会 <c>RescanAndRestate</c>（清缓存 + 全盘扫描 +
    /// 重新判定更新）。这里再补一条 <c>PageRefresh.Request("mods")</c> 就是第二遍全盘扫，
    /// 白烧 IO。</para>
    /// </summary>
    public async Task<int> InstallLocalPacksAsync(IReadOnlyList<string> paths)
    {
        var cfg = _cfg.Current;
        if (string.IsNullOrWhiteSpace(cfg.GamePath)) return 0;

        var packs = FilterLocalPacks(paths);
        if (packs.Count == 0) return 0;

        var ok = 0;
        var failed = 0;
        var anyInstalled = false;

        foreach (var pack in packs)
        {
            var fileName = Path.GetFileName(pack);
            var task = _center.Start(LocService.Tf("安装本地包：{0}", fileName), "install");
            Interlocked.Increment(ref _busyCount);
            string? modName = null;
            var notes = new List<string>();
            try
            {
                void Step(string msg, double? pct = null, double? speed = null) =>
                    _center.Report(task, msg, pct, speed);

                // 本地包没有下载段：一上手就是"装"，进度直接满格 + 「安装中」（同 Nexus 直装）
                task.Installing = true;
                Step(LocService.Tr("正在安装到 Mods…"), 100);

                // InstallNew 是同步重活（解压 / 肖像校验 / CP 转换，实测可达 50s+）。
                // 豁免覆盖【等锁 + 解压 + 扫描】整段，否则僵尸看门狗（60s 宽限）会把
                // 正在正常安装的任务误杀。
                using var longRun = _center.BeginLongRunning(task);
                string? err;
                await EnterInstallGateAsync(Step, 100, CancellationToken.None);
                try
                {
                    // cfg: _cfg 必须传 —— 不传就走"记不了账宁可不装"那条分支，
                    // 覆盖型包会被直接拒绝。portraiturePackName 用包名（立绘包转 CP 包时
                    // 用来命名目录，与 Nexus 链路传任务标题同理）。
                    // packRequirements 不传：那是从 N 网页面拿的登记前置，本地包没有这个来源。
                    var packName = Path.GetFileNameWithoutExtension(pack);
                    err = await Task.Run(() => _mods.InstallNew(
                        cfg.GamePath, pack, out modName,
                        nexusModId: null,
                        portraiturePackName: packName,
                        cfg: _cfg,
                        installNotes: notes,
                        progress: msg => _center.Report(task, msg),
                        ct: CancellationToken.None));
                }
                finally { ExitInstallGate(); task.Installing = false; }

                if (err is null)
                {
                    ok++;
                    anyInstalled = true;
                    _center.Finish(task, true, LocService.Tf("安装完成：{0}", modName ?? fileName)
                        + (notes.Count > 0 ? " · " + string.Join(" · ", notes) : ""));
                }
                else
                {
                    failed++;
                    _center.Finish(task, false, LocService.Tr("安装失败：") + err);
                }
            }
            catch (OperationCanceledException)
            {
                failed++;
                _center.Finish(task, false, LocService.Tr("安装已取消"));
            }
            catch (Exception ex)
            {
                failed++;
                AppLog.Warn("Install", $"本地包装失败 {fileName}：{ex.Message}");
                _center.Finish(task, false, ex.Message);
            }
            finally
            {
                Interlocked.Decrement(ref _busyCount);
            }
        }

        if (anyInstalled)
        {
            Notify();                 // Mods 页据此重扫（不要额外发 PageRefresh，见方法注释）
            NotifyPortraits();        // 本地包可能落进立绘素材目录，Mods 页重扫碰不到立绘缓存
        }
        return ok;
    }
}
