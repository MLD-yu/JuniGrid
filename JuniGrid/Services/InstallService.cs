using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace JuniGrid.Services;

/// <summary>
/// Handles nxm:// links handed over by Windows (via the single-instance
/// named pipe, or startup args), downloads the file and installs it into
/// Mods/. Works for FREE Nexus accounts: the key+expires inside the nxm
/// link come from the user clicking "Mod Manager Download" on the website.
/// </summary>
public sealed class InstallService
{
    private readonly ConfigService _cfg;
    private readonly NexusService _nexus;
    private readonly ModService _mods;
    private readonly TaskCenterService _center;
    private readonly UpdateService _updater;
    private readonly GameService _game;
    private readonly PageRefreshService _pageRefresh;

    public InstallService(ConfigService cfg, NexusService nexus, ModService mods,
        TaskCenterService center, UpdateService updater,
        GameService game, PageRefreshService pageRefresh)
    {
        _cfg = cfg;
        _nexus = nexus;
        _mods = mods;
        _center = center;
        _updater = updater;
        _game = game;
        _pageRefresh = pageRefresh;
    }

    public event Action? OnChanged;

    // v1.1.6：RecentStatus 列表已删 —— 只写不读的死状态（全仓库无任何读取方），
    // OnChanged 事件本身仍被 Mods.razor 使用，保留。

    // v1.3.4：并行安装 —— 下载互不干扰可并存（不同文件各下各的）；只有【写入 Mods
    // 目录的安装段】必须串行（同名目录/回收站/解压覆盖互斥），用 _installGate 排队。
    // Busy 改为活动任务计数：有任何任务在跑就 true（UI 禁用按钮的语义不变），
    // 不再用于入口拒绝 —— 点多个 mod 的安装会全部开始下载，依次写入。
    private int _busyCount;
    public bool Busy => Volatile.Read(ref _busyCount) > 0;

    private static readonly SemaphoreSlim _installGate = new(1, 1);
    private int _installQueue;   // 正在排队等写入的安装数（进度提示用，近似值）

    private async Task EnterInstallGateAsync(Action<string, double?, double?> Step, double pct)
    {
        var pos = Interlocked.Increment(ref _installQueue);
        try
        {
            if (pos > 1)
                Step(LocService.Tf("排队等待写入 Mods…（前面还有 {0} 个安装）", pos - 1), pct, null);
            await _installGate.WaitAsync();
        }
        finally
        {
            Interlocked.Decrement(ref _installQueue);
        }
    }

    private void ExitInstallGate() => _installGate.Release();

    // ------------------------------------------------------------------
    // 直接安装（免弹内置浏览器）
    // ------------------------------------------------------------------
    // 在 ModDetail / 榜单页点「安装」时，后台向 Nexus 请求一次性下载
    // 链接并流式下载安装，全程不出启动器。免费账户限速约 1MB/s；
    // 返回 403（该 mod 强制 Premium）时由调用方回落为打开内置浏览器。
    // ------------------------------------------------------------------

    /// <summary>
    /// 一键直装：后台下载并安装指定 mod 的最新 MAIN 文件。
    /// 返回 null 表示成功；否则返回错误消息（含 "premium" 关键字表示需要 Premium）。
    /// 进度接入任务中心：右下角出现任务，/tasks 页能看到下载百分比/速度/每步详情。
    /// </summary>
    public async Task<string?> InstallModDirectAsync(int modId)
    {
        var cfg = _cfg.Current;
        if (!NexusService.IsAuthenticated)
            return LocService.Tr("还没登录 Nexus —— 先到「Nexus」页登录");
        if (string.IsNullOrWhiteSpace(cfg.GamePath))
            return LocService.Tr("还没设置游戏目录 —— 先到「设置」页选择");

        // v1.3.1：Nexus mod 2400 = SMAPI —— 它的 Nexus 包是官方安装器而非 mod
        //（zip 里没有 manifest.json），普通安装管线必报"没有 manifest.json"。
        // 转调 SMAPI 专属通道：GitHub 官方安装包静默安装（与首页 SMAPI 更新同管线）。
        if (modId == SmapiNexusId)
            return await InstallSmapiFromNexusEntryAsync();

        var taskTitle = await ResolveModTitleAsync(modId, null);
        var task = _center.Start($"下载并安装 {taskTitle}", "install");

        // v1.1.7：整段工作可重入 —— 暂停后继续会重新挂 worker；半截包由 ResumableDownload 续传
        async Task<string?> RunAsync(CancellationToken ct)
        {
            void Step(string msg, double? pct = null, double? speed = null) =>
                _center.Report(task, msg, pct, speed);

            Interlocked.Increment(ref _busyCount);
            string? zipPath = null;   // v1.1.3：取消时清理半截包用（catch 里拿不到 try 内的局部量）
            try
            {
                Step(LocService.Tr("正在获取文件信息…"), 2);
                var file = await _nexus.GetLatestMainFileAsync(modId);
                ct.ThrowIfCancellationRequested();
                if (file is null) { _center.Finish(task, false, LocService.Tr("找不到可下载的文件")); return LocService.Tr("找不到可下载的文件"); }

                Step(LocService.Tr("正在获取下载地址…"), 5);
                var dl = await _nexus.GetDownloadUrlAsync(modId, file.FileId);
                ct.ThrowIfCancellationRequested();
                if (dl.NeedsPremium)
                { _center.Finish(task, false, LocService.Tr("需要 Nexus Premium 会员，已改为网页方式")); return LocService.Tr("premium：这个 mod 的直链下载需要 Nexus Premium 会员"); }
                if (dl.Url is null)
                { _center.Finish(task, false, LocService.Tr("获取下载地址失败：") + (dl.Error ?? "未知错误")); return LocService.Tr("获取下载地址失败：") + (dl.Error ?? "未知错误"); }

                var zip = Path.Combine(StoragePaths.DownloadsDir,
                    $"direct-{modId}-{file.FileId}.zip");
                zipPath = zip;
                Directory.CreateDirectory(Path.GetDirectoryName(zip)!);

                var progress = new Progress<NexusDownloadProgress>(p =>
                    Step(p.Message, p.Percent, p.SpeedMBps));
                Step(LocService.Tf("正在下载 {0}…", file.Name), 8, 0);
                await _nexus.DownloadFileAsync(dl.Url, zip, progress, ct);
                if (ct.IsCancellationRequested)   // 下完才发现被移除/暂停 → 别装了
                {
                    if (task.Status != "paused")
                    { try { File.Delete(zip); } catch { } }
                    throw new OperationCanceledException(ct);
                }

                // v1.3.4：安装段 —— 写入 Mods 全局串行（下载阶段已完成，多任务在此依次写入）
                // v1.1.8：InstallNew 是同步重活（解压/肖像校验/CP 转换，实测可达 50s+）。
                // 若 await 之后续体落在 Blazor UI 线程，整个界面会冻住、任务胶囊停在
                // 最后一帧进度（「91% · 3.1 M/s」假死）。强制丢到线程池执行。
                string? err;
                string? modName = null;
                await EnterInstallGateAsync(Step, 95);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    Step(LocService.Tr("正在安装到 Mods…"), 95);
                    // v1.6.5：Portraiture 素材包（无 manifest+PNG）在 ModService 内直接转换成
                    // CP 肖像包，不再依赖/安装 Portraiture 框架。包名用任务标题（可能被翻译
                    // 服务机翻，仅作目录名/展示，无碍功能）。
                    err = await Task.Run(() => _mods.InstallNew(cfg.GamePath, zip, out modName, modId,
                        portraiturePackName: taskTitle, cfg: _cfg), ct);
                }
                finally { ExitInstallGate(); }
                if (err is not null)
                { _center.Finish(task, false, LocService.Tr("安装失败：") + err); return LocService.Tr("安装失败：") + err; }

                // v0.69.0：记录「最后下载日期」（详情页标题下 + 文件页签绿色✓ 用）
                cfg.ModLastDownload[modId.ToString()] = DateTime.Now.ToString("yyyy-MM-dd");
                cfg.ModFileLastDownload[file.FileId.ToString()] = DateTime.Now.ToString("yyyy-MM-dd");
                // 记 N 网 MAIN 安装快照 + 回写 manifest.Version —— 装完即粘住，避免
                // 「N 网文件 1 / 包内 Version 0.0.1」导致永远显示可更新
                if (!string.IsNullOrWhiteSpace(modName) || modId > 0)
                {
                    var folderGuess = modName ?? "";
                    // Scan 是磁盘全量扫描，同样别落在 UI 线程
                    var scanned = await Task.Run(() => _mods.Scan(cfg.GamePath), ct);
                    var entry = NexusUpdateTruth.FindInstalledEntry(scanned, modId, folderGuess);
                    if (entry is not null)
                    {
                        entry.NexusModId ??= modId;
                        NexusUpdateTruth.RecordInstall(cfg, entry, modId, file.FileId, file.Version ?? "", cfg.GamePath ?? "");
                    }
                    else
                    {
                        NexusUpdateTruth.RecordInstallByModId(cfg, cfg.GamePath ?? "", folderGuess.Length > 0 ? folderGuess : modId.ToString(),
                            modId, file.FileId, file.Version ?? "");
                    }
                }
                _cfg.Save(cfg);
                var done = $"安装完成：{modName ?? "新 Mod"}";
                _center.Finish(task, true, done);
                Notify();
                // P0-3：直装可能落入肖像包（含 Portraiture→CP 转换）→ 立绘页重扫
                NotifyPortraits();
                return null;
            }
            catch (OperationCanceledException)
            {
                if (task.Status == "paused")
                {
                    // 半截包留给继续时续传
                    return "已暂停";
                }
                // v1.1.3：用户移除了任务 → 清半截包，静默退出（任务条目已不在列表）
                try { if (zipPath is not null && File.Exists(zipPath)) File.Delete(zipPath); } catch { }
                return "已取消";
            }
            catch (Exception ex)
            {
                _center.Finish(task, false, ex.Message);
                Notify();
                return ex.Message;
            }
            finally
            {
                Interlocked.Decrement(ref _busyCount);
                if (task.Status is "done" or "failed")
                    _center.UnregisterResume(task);
            }
        }

        _center.RegisterResume(task, ct => Task.Run(() => RunAsync(ct), CancellationToken.None));
        return await Task.Run(() => RunAsync(task.Cts.Token));
    }

    /// <summary>SMAPI 在 Nexus 上的 mod ID（nexusmods.com/stardewvalley/mods/2400）。</summary>
    public const int SmapiNexusId = 2400;

    /// <summary>
    /// v1.3.2：nxm 回流装 SMAPI —— 用户在 Nexus 文件页点了某个具体文件（如旧文件
    /// SMAPI 4.5.1）的「模组管理器下载」。尊重用户的选择：按 fileId 从 Nexus 下载
    /// 【那个版本】的官方安装器包（Nexus 的 SMAPI zip 就是官方安装器，结构与 GitHub
    /// release 相同），再走 RunSmapiInstallerAsync 同款的静默安装流程。
    /// 不再无视用户选择强拉 GitHub 最新版。
    /// </summary>
    private async Task InstallSmapiFromNxmAsync(
        TaskItem task, JuniGridConfig cfg,
        int modId, int fileId, string? key, string? exp,
        Action<string, double?, double?> Step, CancellationToken ct = default)
    {
        // v1.3.4：Busy 由调用方（HandleNxmLinkAsync）管理 —— 旧版这里重复置位/复位
        // 会让外层 finally 提前解锁。
        string? zipPath = null;
        try
        {
            Step(LocService.Tf("正在获取下载地址（SMAPI 文件 #{0}）…", fileId), 8, null);
            var dl = await _nexus.GetNxmDownloadUrlAsync(modId, fileId, key, exp);
            ct.ThrowIfCancellationRequested();
            if (dl.Url is null)
            {
                var msg = dl.Error ?? "获取下载地址失败";
                _center.Finish(task, false, msg);
                Notify();
                return;
            }

            zipPath = Path.Combine(StoragePaths.DownloadsDir, $"nxm-{modId}-{fileId}.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
            var dlProgress = new Progress<NexusDownloadProgress>(
                p => Step(p.Message, p.Percent, p.SpeedMBps));
            Step(LocService.Tr("正在下载 SMAPI 安装器（你选择的版本）…"), 10, 0);
            await _nexus.DownloadFileAsync(dl.Url, zipPath, dlProgress, ct);
            if (ct.IsCancellationRequested)
            {
                if (task.Status != "paused")
                { try { File.Delete(zipPath); } catch { } }
                throw new OperationCanceledException(ct);
            }

            Step(LocService.Tr("正在静默安装 SMAPI…"), 90, null);
            var progress = new Progress<UpdateService.InstallProgress>(
                p => Step(p.Message, p.Percent, p.SpeedMBps));
            var err = await _updater.InstallSmapiZipAsync(zipPath, cfg.GamePath, progress);
            if (err is not null)
            { _center.Finish(task, false, err); Notify(); return; }

            cfg.ModLastDownload[modId.ToString()] = DateTime.Now.ToString("yyyy-MM-dd");
            cfg.ModFileLastDownload[fileId.ToString()] = DateTime.Now.ToString("yyyy-MM-dd");
            _cfg.Save(cfg);
            var done = "安装完成：SMAPI（你选择的版本）";
            _center.Finish(task, true, done);
            Notify();
        }
        catch (OperationCanceledException)
        {
            if (task.Status == "paused") return;   // 半截包留给继续
            try { if (zipPath is not null && File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            _center.Finish(task, false, LocService.Tr("已取消"));
        }
        catch (Exception ex)
        {
            _center.Finish(task, false, ex.Message);
            Notify();
        }
    }

    /// <summary>
    /// v1.3.1：从 Nexus 页的 SMAPI 条目点「安装」→ 走 SMAPI 专属通道。
    /// 不碰 Nexus 的 zip（那是安装器不是 mod），直接用首页 SMAPI 更新的同一条管线：
    /// CheckSmapiAsync 拿 GitHub 官方安装包地址 → RunSmapiInstallerAsync 静默安装。
    /// </summary>
    private async Task<string?> InstallSmapiFromNexusEntryAsync()
    {
        var cfg = _cfg.Current;
        var task = _center.Start("下载并安装 SMAPI - Stardew Modding API", "install");

        async Task<string?> RunAsync(CancellationToken ct)
        {
            void Step(string msg, double? pct = null, double? speed = null) =>
                _center.Report(task, msg, pct, speed);

            Interlocked.Increment(ref _busyCount);
            try
            {
                Step(LocService.Tr("正在获取适配当前游戏的 SMAPI…"), 5);
                // v1.1.8：按当前游戏版本取对应 release（1.6+ = latest 4.x；1.4/1.5 等 = 历史 tag）
                var info = await _updater.CheckSmapiForGameAsync(
                    _game.ProbeSmapiVersion(cfg.GamePath),
                    _updater.GetGameVersion(cfg.GamePath), force: true);
                ct.ThrowIfCancellationRequested();
                if (info.InstallerZipUrl is null)
                {
                    var msg = info.Error ?? "没找到 SMAPI 安装包下载地址（GitHub 访问失败？稍后再试）";
                    _center.Finish(task, false, msg);
                    return msg;
                }
                if (info.HasUpdate is false && info.InstalledParsed)
                    Step(LocService.Tr("本地 SMAPI 已是最新，按重装执行…"), 8);

                // v1.3.4：SMAPI 安装含几百 MB 的 Mods 备份与游戏目录写入，全程持安装锁
                await EnterInstallGateAsync(Step, 8);
                string? err;
                try
                {
                    ct.ThrowIfCancellationRequested();
                    // RunSmapiInstallerAsync 的进度（含备份/下载/解压/安装阶段）桥接到任务中心
                    var progress = new Progress<UpdateService.InstallProgress>(
                        p => Step(p.Message, p.Percent, p.SpeedMBps));
                    err = await _updater.RunSmapiInstallerAsync(info, cfg.GamePath, progress, ct);
                }
                finally { ExitInstallGate(); }
                if (ct.IsCancellationRequested && task.Status == "paused")
                    return "已暂停";
                if (err is not null)
                { _center.Finish(task, false, LocService.Tr("安装失败：") + err); return LocService.Tr("安装失败：") + err; }

                var done = $"安装完成：SMAPI {info.LatestVersion ?? ""}".TrimEnd();
                _center.Finish(task, true, done);
                Notify();
                return null;
            }
            catch (OperationCanceledException)
            {
                return task.Status == "paused" ? "已暂停" : "已取消";
            }
            catch (Exception ex)
            {
                _center.Finish(task, false, ex.Message);
                Notify();
                return ex.Message;
            }
            finally
            {
                Interlocked.Decrement(ref _busyCount);
                if (task.Status is "done" or "failed")
                    _center.UnregisterResume(task);
            }
        }

        _center.RegisterResume(task, ct => Task.Run(() => RunAsync(ct), CancellationToken.None));
        return await Task.Run(() => RunAsync(task.Cts.Token));
    }

    public async Task HandleNxmLinkAsync(string link)
    {
        var task = _center.Start("网页一键安装（Nexus）", "install");

        // v1.1.7：整段可重入 —— 暂停后继续重新挂 worker；半截包续传
        async Task RunAsync(CancellationToken ct)
        {
            string? zipPath = null;   // v1.1.3：取消清理用

            void Step(string msg, double? pct = null, double? speed = null) =>
                _center.Report(task, msg, pct, speed);

            Interlocked.Increment(ref _busyCount);
            try
            {
                Step(LocService.Tr("解析收到的 Nexus 下载链接…"), 2);
                if (!TryParseNxm(link, out var modId, out var fileId, out var key, out var exp))
                {
                    _center.Finish(task, false, LocService.Tr("无法解析链接"));
                    Notify();
                    return;
                }

                var cfg = _cfg.Current;
                if (!NexusService.IsAuthenticated)
                {
                    _center.Finish(task, false, LocService.Tr("还没登录 Nexus —— 先到「Nexus」页登录"));
                    Notify();
                    return;
                }
                if (string.IsNullOrWhiteSpace(cfg.GamePath))
                {
                    _center.Finish(task, false, LocService.Tr("还没设置游戏目录"));
                    Notify();
                    return;
                }

                // 解析出 modId 后把任务标题补上 mod 名称，方便在 /tasks 页识别
                task.Title = LocService.Tr("下载并安装 ") + await ResolveModTitleAsync(modId, null);
                ct.ThrowIfCancellationRequested();

                // v1.3.1：SMAPI（mod 2400）网页回流同样转专属通道 —— Nexus 包是安装器
                // 不是 mod，普通管线必失败。
                // v1.3.2：带上 fileId —— 用户在 Nexus 选的就是他要的版本（如旧文件 4.5.1），
                // 旧版无视选择强拉 GitHub 最新（选 4.5.1 装成 4.5.2）。
                if (modId == SmapiNexusId)
                {
                    await InstallSmapiFromNxmAsync(task, cfg, modId, (int)fileId, key, exp, Step, ct);
                    return;
                }

                Step(LocService.Tf("正在获取下载地址（Mod #{0}）…", modId), 8);
                // download_link.json 必须带认证头 —— 现为 OAuth Bearer（SendApiAsync 默认挂）；
                // 另附 nxm 链接里的一次性 key/expires 作 query 参数。二者缺一 → 401。
                var dl = await _nexus.GetNxmDownloadUrlAsync(modId, fileId, key, exp);
                ct.ThrowIfCancellationRequested();
                if (dl.Url is null)
                {
                    _center.Finish(task, false, dl.Error ?? LocService.Tr("获取下载地址失败"));
                    Notify();
                    return;
                }

                var zip = Path.Combine(StoragePaths.DownloadsDir, $"nxm-{modId}-{fileId}.zip");
                zipPath = zip;
                Directory.CreateDirectory(Path.GetDirectoryName(zip)!);

                var progress = new Progress<NexusDownloadProgress>(p =>
                    Step(p.Message, p.Percent, p.SpeedMBps));
                Step(LocService.Tr("正在下载…"), 12, 0);
                await _nexus.DownloadFileAsync(dl.Url, zip, progress, ct);
                if (ct.IsCancellationRequested)   // 下完才发现被移除/暂停 → 别装了
                {
                    if (task.Status != "paused")
                    { try { File.Delete(zip); } catch { } }
                    throw new OperationCanceledException(ct);
                }

                // v1.3.4：安装段 —— 写入 Mods 全局串行
                string? nxmErr;
                string? modName = null;
                await EnterInstallGateAsync(Step, 95);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    Step(LocService.Tr("正在安装到 Mods…"), 95);
                    var nxmTitle = task.Title.StartsWith("下载并安装 ") ? task.Title["下载并安装 ".Length..] : null;
                    // v1.6.5：Portraiture 素材包直接转换成 CP 肖像包（不再装框架）
                    // v1.1.8：同直装路径，InstallNew 丢线程池，避免冻住 UI
                    nxmErr = await Task.Run(() => _mods.InstallNew(cfg.GamePath, zip, out modName, modId,
                        portraiturePackName: nxmTitle, cfg: _cfg), ct);
                }
                finally { ExitInstallGate(); }
                if (nxmErr is null)
                {
                    _center.Finish(task, true, $"安装完成：{modName ?? "新 Mod"}");
                    Notify();
                    // P0-3：nxm 接管安装同样可能带肖像包
                    NotifyPortraits();

                    // 兜底：.nxm 一键安装也必须写下「已拥有这个 fileId」。缺了它，
                    // 作者没 bump manifest.Version 时（实测 mod 1839：包内 1.6.4 / N 网文件 1.6.7、
                    // mod 27100：包内 0.0.1 / N 网文件版本 "1"）ShouldShowUpdate 只能拿两个
                    // 不同命名空间的版本号比大小 → 更新提示永远消不掉。与 InstallFromNexus 同款写法。
                    try
                    {
                        cfg.ModLastDownload[modId.ToString()] = DateTime.Now.ToString("yyyy-MM-dd");
                        cfg.ModFileLastDownload[fileId.ToString()] = DateTime.Now.ToString("yyyy-MM-dd");
                        // 版本号必须一起记：留空 ⇒ 快道（smapi.io 只给版本号、不给 fileId）那条路
                        // 再没有任何判据能认出「这一版我已经装过了」，⇧ 装上就消不掉
                        var nxmVer = (await _nexus.GetFileByIdAsync(modId, fileId))?.Version ?? "";
                        var guess = modName ?? "";
                        var scanned = await Task.Run(() => _mods.Scan(cfg.GamePath), ct);
                        var entry = NexusUpdateTruth.FindInstalledEntry(scanned, modId, guess);
                        if (entry is not null)
                        {
                            entry.NexusModId ??= modId;
                            NexusUpdateTruth.RecordInstall(cfg, entry, modId, fileId, nxmVer, cfg.GamePath ?? "");
                        }
                        else
                            NexusUpdateTruth.RecordInstallByModId(cfg, cfg.GamePath ?? "",
                                guess.Length > 0 ? guess : modId.ToString(), modId, fileId, nxmVer);
                        _cfg.Save(cfg);
                    }
                    catch (Exception rex) { AppLog.Warn("Install", ".nxm 安装后写下载/安装记录失败: " + rex.Message); }
                }
                else
                {
                    _center.Finish(task, false, LocService.Tr("安装失败：") + nxmErr);
                    Notify();
                }
            }
            catch (OperationCanceledException)
            {
                if (task.Status == "paused") return;   // 半截包留给继续
                try { if (zipPath is not null && File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            }
            catch (Exception ex)
            {
                _center.Finish(task, false, ex.Message);
                Notify();
            }
            finally
            {
                Interlocked.Decrement(ref _busyCount);
                if (task.Status is "done" or "failed")
                    _center.UnregisterResume(task);
            }
        }

        _center.RegisterResume(task, ct => Task.Run(() => RunAsync(ct), CancellationToken.None));
        await Task.Run(() => RunAsync(task.Cts.Token));
    }

    private void Notify() => OnChanged?.Invoke();

    /// <summary>P0-3：磁盘上肖像包/框架变化后，让已打开的立绘页重扫（未打开则无订阅、无操作）。</summary>
    private void NotifyPortraits()
    {
        try { _pageRefresh.Request("portraits"); }
        catch { /* 刷新信号失败不影响安装结果 */ }
    }

    // ------------------------------------------------------------------
    // v1.2.0：一键安装缺失依赖
    // ------------------------------------------------------------------
    // 依赖以 SMAPI UniqueID（manifest Dependencies / ContentPackFor 宿主）声明，
    // Nexus 只认 modId —— 中间靠「官网搜索（免 key GraphQL，按下载数排序）→
    // 下载后校验 zip 内 manifest UniqueID」这道防错闭环打通：搜错候选最多浪费
    // 一次下载，绝不会装错 mod。传递闭包自动跟进：装上的依赖自己又缺什么，接着装。
    // SMAPI 本体是加载器不是普通 mod，单独提示不自动装。
    // Nexus 免费账户没有 API 直下权限（403）→ 命中一次后余下依赖全部转「需手动」
    // 并附 Nexus 页面链接，不再浪费 API 请求额度。
    // ------------------------------------------------------------------

    public sealed record DependencyRunItem(string Uid, string Status, string? Name, int? ModId, string? Detail);

    /// <summary>
    /// 一键安装缺失依赖。onlyUids 为 null = 全部缺失依赖；传入具体 UID 列表 =
    /// 只装这批（以及它们装上后新暴露的传递依赖）。preResolved = 确认弹窗已经
    /// 搜索解析过的 UID → modId（零搜索开销直接用，校验照做）。返回 null 表示
    /// 流程跑完（个别依赖可能转手动/失败）。
    /// </summary>
    public async Task<string?> InstallMissingDependenciesAsync(IReadOnlyList<string>? onlyUids = null,
        IReadOnlyDictionary<string, int>? preResolved = null)
    {
        if (Busy) return LocService.Tr("上一个安装还没完成，等它结束再试");
        var cfg = _cfg.Current;
        if (!NexusService.IsAuthenticated)
            return LocService.Tr("还没登录 Nexus —— 先到「Nexus」页登录");
        if (string.IsNullOrWhiteSpace(cfg.GamePath))
            return LocService.Tr("还没设置游戏目录 —— 先到「设置」页选择");

        var task = _center.Start(
            onlyUids is null ? "一键安装缺失依赖" : $"安装缺失依赖（{onlyUids.Count} 项）", "install");
        void Step(string msg, double? pct = null, double? speed = null) => _center.Report(task, msg, pct, speed);

        // v1.3.4：批量装依赖全程持安装锁（逐项下载+写入都是完整的安装会话），
        // 与其它 mod 安装互斥排队。
        Interlocked.Increment(ref _busyCount);
        var outcomes = new List<DependencyRunItem>();
        var toOpen = new List<string>();   // v1.2.2：需手动的依赖页面，结束时批量在浏览器打开
        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var premiumBlocked = false;
        string? zipPath = null;
        try
        {
            await EnterInstallGateAsync(Step, 2);
            try
            {
                // onlyUids 只约束首轮（用户指定的那批）；装上的依赖自己又缺的传递依赖照样跟进
                var allowedFirstRound = onlyUids is null ? null : new HashSet<string>(onlyUids, StringComparer.OrdinalIgnoreCase);
                var firstRound = true;

                while (true)
                {
                    task.Cts.Token.ThrowIfCancellationRequested();
                    // 每轮重扫磁盘 —— 上一轮装上的依赖可能自带新的缺失依赖（传递闭包）
                    var mods = _mods.Scan(cfg.GamePath);
                    var installedUids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var m in mods)
                        if (!string.IsNullOrWhiteSpace(m.UniqueID))
                            installedUids.Add(m.UniqueID.Trim());

                    var batch = new List<string>();
                    foreach (var m in mods)
                    {
                        foreach (var d in m.Dependencies.Concat(m.ContentPackIds))
                        {
                            var dep = d?.Trim();
                            if (string.IsNullOrWhiteSpace(dep)
                                || installedUids.Contains(dep)
                                || processed.Contains(dep)) continue;
                            if (firstRound && allowedFirstRound is not null && !allowedFirstRound.Contains(dep)) continue;
                            if (!batch.Contains(dep, StringComparer.OrdinalIgnoreCase)) batch.Add(dep);
                        }
                    }
                firstRound = false;
                if (batch.Count == 0) break;

                var total = processed.Count + batch.Count;
                // 每轮重取一次已装 UID → 名字/N网id 的映射：本轮刚装上的依赖下一轮就该认得，
                // 而且「同包子模块」判定必须有这张表（子模块在 Nexus 上没有独立页面可搜）。
                var installedMap = new Dictionary<string, (string Name, int? NexusId)>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in _mods.Scan(cfg.GamePath))
                    if (!string.IsNullOrWhiteSpace(e.UniqueID))
                        installedMap[e.UniqueID.Trim()] = (string.IsNullOrWhiteSpace(e.Name) ? e.Folder : e.Name, e.NexusModId);

                foreach (var dep in batch)
                {
                    task.Cts.Token.ThrowIfCancellationRequested();
                    processed.Add(dep);
                    var idx = processed.Count;
                    Step(LocService.Tf("({0}/{1}) 正在处理 {2}…", idx, total, dep), Math.Round(idx * 90.0 / (total + 1), 1));

                    // ① 加载器本体不是 mod，自动装不进去
                    if (dep.Equals("SMAPI", StringComparison.OrdinalIgnoreCase))
                    {
                        outcomes.Add(new DependencyRunItem(dep, "framework", "SMAPI", null,
                            "SMAPI 是 mod 加载器本体，无法作为普通 mod 安装 —— 请用 SMAPI 安装器（启动器首页可检查/更新 SMAPI）"));
                        continue;
                    }

                    // ①b 同包子模块：它不是独立 mod，Nexus 上没有它的页面（实测 shurmash 名下只有
                    // 2 个 mod，没有 LewdDew_Valley_helper）。发搜索等于白费一次请求，再把人领到
                    // 一个不存在的页面上 —— 该领他去的是宿主 mod 的页面。
                    if (ModService.BundledParentOf(dep, installedMap.Keys) is string parentUid
                        && installedMap.TryGetValue(parentUid, out var parent))
                    {
                        outcomes.Add(new DependencyRunItem(dep, "manual", parent.Name, parent.NexusId,
                            LocService.Tf("「{0}」是「{1}」包内的子模块，Nexus 上没有它的独立页面 —— 重装或更新「{1}」就会带上它。", dep, parent.Name)));
                        if (parent.NexusId is int pid)
                            toOpen.Add($"https://www.nexusmods.com/stardewvalley/mods/{pid}");
                        Step(LocService.Tf("⏭ {0}：「{1}」包内子模块，随宿主一起装", dep, parent.Name));
                        continue;
                    }

                    // ② 候选解析（v1.2.3 惰性化）：已知候选（弹窗预解析 + 配置缓存）零搜索
                    // 直接用，全部校验落空才走官网搜索降级 —— 重复安装/连续装依赖省掉整轮
                    // GraphQL 请求。premiumBlocked 快速通道只需要一个候选 id 生成手动链接。
                    List<NexusModListEntry> cands;
                    if (premiumBlocked)
                    {
                        var face = await FirstCandidateAsync(cfg, dep, preResolved);
                        if (face is null)
                        {
                            Step(LocService.Tf("✗ {0}：Nexus 搜索不到匹配的 mod，已打开搜索页转人工", dep));
                            AddUnresolvedOutcome(dep, toOpen, outcomes);
                            continue;
                        }
                        Step(LocService.Tf("⏭ {0}：免费账户不能 API 直下，已打开网页转手动", dep));
                        outcomes.Add(new DependencyRunItem(dep, "manual", face.Name, face.Id,
                            dep.Equals(ModService.PortraitureFrameworkUid, StringComparison.OrdinalIgnoreCase)
                                ? "Portraiture 框架需手动下载 —— 点「Mod Manager Download」后启动器自动接管；装完请到立绘页确认素材包"
                                : "已在浏览器打开此页面 —— 点「Mod Manager Download」，启动器会自动接管下载安装（Nexus 免费账户限制）"));
                        toOpen.Add($"https://www.nexusmods.com/stardewvalley/mods/{face.Id}");
                        continue;
                    }

                    cands = await KnownCandidatesAsync(cfg, dep, preResolved);
                    var searched = false;
                    string? lastErr = null;
                    var ok = false;
                    while (true)
                    {
                        // ③ 逐候选：下载 → 校验 UniqueID → 安装（每批最多试 3 个，免费限速下省带宽）
                        foreach (var c in cands.Take(3))
                        {
                            task.Cts.Token.ThrowIfCancellationRequested();
                            Step(LocService.Tf("({0}/{1}) 正在获取 {2} 的文件信息…", idx, total, c.Name));
                            var file = await _nexus.GetLatestMainFileAsync(c.Id);
                            if (file is null) { lastErr = LocService.Tr("找不到可下载的文件"); continue; }

                            var dl = await _nexus.GetDownloadUrlAsync(c.Id, file.FileId);
                            if (dl.NeedsPremium)
                            {
                                // 换候选也一样 —— 账号级限制：本次余下依赖全部转手动
                                premiumBlocked = true;
                                lastErr = "premium";
                                break;
                            }
                            if (dl.Url is null) { lastErr = dl.Error ?? "获取下载地址失败"; continue; }

                            zipPath = Path.Combine(StoragePaths.DownloadsDir, $"dep-{c.Id}-{file.FileId}.zip");
                            Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
                            try
                            {
                                var progress = new Progress<NexusDownloadProgress>(p => Step(p.Message, p.Percent, p.SpeedMBps));
                                Step(LocService.Tf("({0}/{1}) 正在下载 {2}…", idx, total, c.Name), null, 0);
                                await _nexus.DownloadFileAsync(dl.Url, zipPath, progress, task.Cts.Token);
                                if (task.Cts.IsCancellationRequested)
                                    return task.Status == "paused" ? "已暂停" : "已取消";

                                Step(LocService.Tf("({0}/{1}) 正在安装 {2}…", idx, total, c.Name), Math.Round(idx * 95.0 / (total + 1), 1));
                                string? modName = null;
                                var err = await Task.Run(() => _mods.InstallNew(cfg.GamePath, zipPath, out modName, c.Id,
                                    requireUniqueId: dep, cfg: _cfg), task.Cts.Token);
                                if (err == ModService.UidMismatchError)
                                { lastErr = LocService.Tf("候选「{0}」不含 {1}，换下一个", c.Name, dep); continue; }
                                if (err is not null) { lastErr = err; continue; }

                                // 成功：记住解析结果 + 与直装同款的收尾（更新队列/最后下载日期/刷新列表）
                                cfg.DependencyNexusIds[dep] = c.Id;
                                cfg.ModLastDownload[c.Id.ToString()] = DateTime.Now.ToString("yyyy-MM-dd");
                                cfg.ModFileLastDownload[file.FileId.ToString()] = DateTime.Now.ToString("yyyy-MM-dd");
                                _cfg.Save(cfg);
                                outcomes.Add(new DependencyRunItem(dep, "installed", modName ?? c.Name, c.Id,
                                    dep.Equals(ModService.PortraitureFrameworkUid, StringComparison.OrdinalIgnoreCase)
                                        ? "Portraiture 框架已装 —— 供声明依赖它的 mod 使用；启动器转换的 CP 肖像包不依赖它，游戏内按 P 切换高清模式"
                                        : null));
                                Step(LocService.Tf("✓ 已安装 {0}（{1}）", modName ?? c.Name, dep));
                                Notify();
                                // P0-3：装上的依赖可能带肖像内容/框架 → 立绘页重扫
                                NotifyPortraits();
                                ok = true;
                                break;
                            }
                            finally
                            {
                                // 半截包/装完的 zip 都不留（下载可续传缓存的意义不大，依赖包普遍很小）
                                try { if (zipPath is not null && File.Exists(zipPath)) File.Delete(zipPath); } catch { }
                                zipPath = null;
                            }
                        }
                        // 已知候选全落空 → 官网搜索降级（每依赖只搜一轮，搜完仍不行就认命）
                        if (ok || premiumBlocked || searched) break;
                        searched = true;
                        Step(cands.Count > 0
                            ? LocService.Tf("({0}/{1}) 已知候选不匹配，正在搜索 {2}…", idx, total, dep)
                            : $"({idx}/{total}) 正在搜索 {dep}…");
                        cands = await SearchDependencyCandidatesAsync(dep);
                        if (cands.Count == 0) break;
                    }

                if (ok) continue;
                if (premiumBlocked)
                {
                    var face = cands.Count > 0 ? cands[0] : null;
                    outcomes.Add(new DependencyRunItem(dep, "manual", face?.Name, face?.Id,
                        "已在浏览器打开此页面 —— 点「Mod Manager Download」，启动器会自动接管下载安装（Nexus 免费账户限制）"));
                    Step(LocService.Tf("⏭ {0}：Nexus 直下需要 Premium，已打开网页转手动", dep));
                    if (face is not null) toOpen.Add($"https://www.nexusmods.com/stardewvalley/mods/{face.Id}");
                }
                    else if (searched && cands.Count == 0)
                    {
                        Step(LocService.Tf("✗ {0}：Nexus 搜索不到匹配的 mod，已打开搜索页转人工", dep));
                        AddUnresolvedOutcome(dep, toOpen, outcomes);
                    }
                    else
                    {
                        var face = cands.Count > 0 ? cands[0] : null;
                        outcomes.Add(new DependencyRunItem(dep, "failed", face?.Name, face?.Id,
                            lastErr ?? "下载/安装失败"));
                        Step($"✗ {dep}：{lastErr}");
                    }
                }
                if (processed.Count > 200) break;   // 防御：异常清单不至于无限跑
            }

            var done = outcomes.Count(o => o.Status == "installed");
            var manualC = outcomes.Count(o => o.Status is "manual" or "framework");
            var unresolvedC = outcomes.Count(o => o.Status == "unresolved");
            var failedC = outcomes.Count(o => o.Status == "failed");
            var sb = new StringBuilder();
            if (done > 0) sb.Append($"已安装 {done} 个依赖");
            if (manualC > 0) sb.Append(sb.Length > 0 ? $"；{manualC} 个需手动" : $"{manualC} 个需手动");
            if (unresolvedC > 0) sb.Append(sb.Length > 0 ? $"；{unresolvedC} 个未找到" : $"{unresolvedC} 个未找到");
            if (failedC > 0) sb.Append(sb.Length > 0 ? $"；{failedC} 个失败" : $"{failedC} 个失败");
            var summary = sb.Length == 0 ? "没有需要安装的缺失依赖" : sb.ToString();
            // v1.2.3：只有 unresolved/failed 才算任务失败 —— "需手动"（免费账户开网页）
            // 是规划内的正常出路，按成功收尾，末行摘要不显示成红叉。
            _center.Finish(task, failedC == 0 && unresolvedC == 0, summary);

            // v1.2.2：免费账户/搜不到的依赖 —— 结束时把页面批量在默认浏览器打开，
            // 用户逐个点「Mod Manager Download」，nxm 接管链路自动下载安装进 Mods
            //（免费下载的一次性 key 只能由网页点击产生，无法在应用里直接替用户完成）。
            // 上限 12 个防标签页风暴。SMAPI 本体不开页面（它有独立安装器）。
            // v1.2.3：不再往任务日志写"已在浏览器打开…"提示行（浏览器弹窗本身就是告知）。
            if (toOpen.Count > 0)
            {
                foreach (var url in toOpen.Take(12))
                {
                    try { UpdateService.OpenUrl(url); } catch { }
                }
            }
            Notify();
            // P0-3：本轮装上过依赖（含 Portraiture）→ 立绘页一次性重扫
            if (outcomes.Any(o => o.Status == "installed"
                    || o.Uid.Equals(ModService.PortraitureFrameworkUid, StringComparison.OrdinalIgnoreCase)))
                NotifyPortraits();
            return null;
            }
            finally { ExitInstallGate(); }
        }
        catch (OperationCanceledException)
        {
            try { if (zipPath is not null && File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            return task.Status == "paused" ? "已暂停" : "已取消";
        }
        catch (Exception ex)
        {
            _center.Finish(task, false, LocService.Tr("一键装依赖失败：") + ex.Message);
            return ex.Message;
        }
        finally
        {
            Interlocked.Decrement(ref _busyCount);
        }
    }

    /// <summary>确认弹窗的展示信息：UID → (mod 名, Nexus modId, 封面)。
    /// 免 key GraphQL 的命中要过 <see cref="AuthorAffinity"/> 这道闸才敢拿出来当"这个依赖是谁"
    /// —— 实际安装仍以下载后的 UniqueID 校验为准，但展示错了会把人直接带去别人的 mod 页。</summary>
    public sealed record DependencyDisplayInfo(string Uid, string? Name, int? ModId, string? CoverUrl);

    /// <summary>UID 的作者段（"ChaseXavier.Miku" → "ChaseXavier"）；没有点号或太短（"a.b"）返回 null。</summary>
    private static string? UidAuthorSegment(string uid)
    {
        var dot = uid.IndexOf('.');
        if (dot <= 0) return null;
        var seg = new string(uid[..dot].Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return seg.Length >= 2 ? seg : null;
    }

    /// <summary>某条搜索结果与这个 UID 的"作者亲和度"：
    /// 3=作者段与上传者完全相同 · 2=一方包含另一方（Wildflour ↔ Wildflourmods）·
    /// 1=作者段是上传者的首字母缩写（CF ↔ CyanFire）· 0=对不上。
    /// 为什么必须看作者：搜 "Miku" 按下载数排第一是 lucasedu11 的「Miku skin for Abigail」(4291)，
    /// 而 ChaseXavier.Miku 真正对应的是排第三的「CP_Miku NPC」(4373)。旧写法直接取第一个命中，
    /// 于是弹窗把人指向别人的 mod，用户照装之后「缺少依赖」依旧（实测 2026-09-26）。</summary>
    internal static int AuthorAffinity(string uid, string? uploader)
    {
        var a = UidAuthorSegment(uid);
        if (a is null) return 0;                       // UID 没有作者段 → 无从判，交给名字
        var u = new string((uploader ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (u.Length == 0) return 0;
        if (u == a) return 3;
        if (u.Contains(a) || a.Contains(u)) return 2;
        // 首字母缩写：上传者的驼峰词首字母串（CyanFire → "cf"）
        var initials = new string((uploader ?? "").Where(char.IsUpper).ToArray()).ToLowerInvariant();
        if (initials.Length >= 2 && initials == a) return 1;
        return 0;
    }

    /// <summary>按作者亲和度从高到低排候选（同分保持原有的下载数顺序），供展示与下载共用。</summary>
    internal static List<NexusModListEntry> RankByAuthor(string uid, IEnumerable<NexusModListEntry> hits)
        => hits.OrderByDescending(h => AuthorAffinity(uid, h.Uploader)).ToList();


    /// <summary>v1.2.3：展示信息进程内缓存 —— 同会话反复打开弹窗不再重搜（mod 名/封面基本不变）。</summary>
    private static readonly ConcurrentDictionary<string, DependencyDisplayInfo> DisplayCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>解析单个依赖 UID 的展示信息：进程内缓存 → 配置持久缓存 → 免 key 搜索。
    /// <b>只有作者段能对上（<see cref="AuthorAffinity"/> &gt; 0）的命中才拿出来当"这个依赖是谁"</b>；
    /// 对不上就返回 null（弹窗退回显示裸 UID，不再给出可点的错误页面），并顺手把
    /// 老版本按"下载数第一"存下来的错答案从配置里清掉（自愈，不靠用户手动删配置）。</summary>
    public async Task<DependencyDisplayInfo?> ResolveDependencyDisplayAsync(string uid)
    {
        if (DisplayCache.TryGetValue(uid, out var cached)) return cached;
        // v1.2.4：配置持久缓存 —— 跨会话同步出结果，重启后弹窗首开不再等搜索
        var cfg = _cfg.Current;
        var staleSaved = false;
        if (cfg.DependencyDisplays.TryGetValue(uid, out var saved))
        {
            if (AuthorAffinity(uid, saved.Uploader) > 0)
            {
                var fromSaved = new DependencyDisplayInfo(uid,
                    saved.Name.Length > 0 ? saved.Name : null,
                    saved.ModId > 0 ? saved.ModId : null,
                    saved.CoverUrl.Length > 0 ? saved.CoverUrl : null);
                DisplayCache[uid] = fromSaved;
                return fromSaved;
            }
            staleSaved = true;   // 缓存里没有上传者、或上传者与 UID 作者段对不上 → 重新解析
        }
        try
        {
            var ranked = RankByAuthor(uid, await SearchDependencyCandidatesAsync(uid));
            var top = ranked.FirstOrDefault(h => h.Id > 0 && AuthorAffinity(uid, h.Uploader) > 0);
            if (top is null)
            {
                if (staleSaved) DropStaleDisplay(cfg, uid);
                return null;
            }
            var info = new DependencyDisplayInfo(uid, top.Name, top.Id,
                string.IsNullOrWhiteSpace(top.ThumbnailUrl) ? top.PictureUrl : top.ThumbnailUrl);
            DisplayCache[uid] = info;
            // 回写持久缓存（连上传者一起存，下次会话能直接判它可不可信），写失败不影响本次展示
            try
            {
                cfg.DependencyDisplays[uid] = new DependencyDisplayEntry
                {
                    Name = top.Name ?? "",
                    ModId = top.Id,
                    CoverUrl = info.CoverUrl ?? "",
                    Uploader = top.Uploader ?? "",
                };
                _cfg.Save(cfg);
            }
            catch { }
            return info;
        }
        catch
        {
            if (staleSaved) DropStaleDisplay(cfg, uid);
            return null;
        }
    }

    /// <summary>清掉一条不可信的持久缓存（旧规则按下载数存进来的错答案，如 ChaseXavier.Miku → 4291）。</summary>
    private void DropStaleDisplay(JuniGridConfig cfg, string uid)
    {
        if (!cfg.DependencyDisplays.Remove(uid)) return;
        try { _cfg.Save(cfg); } catch { }
        AppLog.Info("Install", $"[依赖解析] 丢弃错认的缓存 {uid}（旧规则按下载数取首个命中）");
    }

    /// <summary>已知候选（v1.2.3：弹窗预解析 + 配置缓存）：零搜索开销直接用。只有第一个
    /// 候选解析一次显示名（过程日志可读），其余以 "Mod #id" 兜底；校验仍在下载后执行，
    /// 候选错了也装不进 Mods。</summary>
    private async Task<List<NexusModListEntry>> KnownCandidatesAsync(
        JuniGridConfig cfg, string uid, IReadOnlyDictionary<string, int>? preResolved)
    {
        var ids = new List<int>();
        var seen = new HashSet<int>();
        if (preResolved is not null && preResolved.TryGetValue(uid, out var pre) && pre > 0 && seen.Add(pre))
            ids.Add(pre);
        if (cfg.DependencyNexusIds.TryGetValue(uid, out var known) && known > 0 && seen.Add(known))
            ids.Add(known);
        if (ids.Count == 0) return new List<NexusModListEntry>();
        var firstName = "";
        try
        {
            var info = await _nexus.GetModAsync(ids[0]);
            if (!string.IsNullOrWhiteSpace(info?.Name)) firstName = info!.Name;
        }
        catch { }
        return ids.Select(id => new NexusModListEntry(
            id, id == ids[0] && firstName.Length > 0 ? firstName : $"Mod #{id}", "", "", 0, "")).ToList();
    }

    /// <summary>官网搜索候选（v1.2.3：已知候选全落空时的降级通道。免 key GraphQL，
    /// 按下载数排序）。候选只是"疑似"，真正放行靠下载后校验 manifest UniqueID。</summary>
    private async Task<List<NexusModListEntry>> SearchDependencyCandidatesAsync(string uid)
    {
        var list = new List<NexusModListEntry>();
        var seen = new HashSet<int>();
        foreach (var term in UidSearchTerms(uid))
        {
            List<NexusModListEntry>? hits = null;
            try
            {
                // forceIncludeAdult：这是"能不能解析到依赖"，不是"给用户看什么列表"。
                // 带上展示偏好的话，不开「显示成人内容」的人永远装不上成人 mod 的依赖
                // （实测搜 "LewdDew" 带过滤 0 条、不带 12 条）。
                hits = await _nexus.BrowseModsAsync("downloads", 0, 8, "stardewvalley",
                    searchText: term, forceIncludeAdult: true);
            }
            catch { }
            if (hits is null) continue;
            foreach (var h in hits)
                if (h.Id > 0 && seen.Add(h.Id)) list.Add(h);
            if (list.Count >= 12) break;   // 候选上限，防跑飞
        }
        // 作者段能对上的候选排前面：下载数高≠就是它（搜 "Miku" 下载第一是别人的皮肤包）
        return RankByAuthor(uid, list);
    }

    /// <summary>premiumBlocked 快速通道专用：只找一个候选 id 生成手动链接（known → 搜索）。
    /// 搜索结果一个都对不上作者段时宁可返回 null（转"未找到"开搜索页人工确认），
    /// 也不能把人领到下载数最高的那个同名 mod 页面上。</summary>
    private async Task<NexusModListEntry?> FirstCandidateAsync(
        JuniGridConfig cfg, string uid, IReadOnlyDictionary<string, int>? preResolved)
    {
        var known = await KnownCandidatesAsync(cfg, uid, preResolved);
        if (known.Count > 0) return known[0];
        var hits = await SearchDependencyCandidatesAsync(uid);
        return hits.FirstOrDefault(h => AuthorAffinity(uid, h.Uploader) > 0);
    }

    /// <summary>unresolved 收尾：结果项 + Nexus 站内搜索页链接（转人工确认，结束时统一开浏览器）。</summary>
    private void AddUnresolvedOutcome(string dep, List<string> toOpen, List<DependencyRunItem> outcomes)
    {
        toOpen.Add("https://www.nexusmods.com/stardewvalley/search/?gsearch="
            + Uri.EscapeDataString(UidSearchTerms(dep).FirstOrDefault() ?? dep)
            + "&gsearchtype=mods");
        outcomes.Add(new DependencyRunItem(dep, "unresolved", null, null,
            "自动搜索不到这个依赖 —— 已打开 Nexus 搜索页，请人工确认后点 Mod Manager Download（启动器会自动接管）"));
    }

    /// <summary>UID → 搜索词：取 UniqueID 末段（作者段搜索噪音太大），驼峰拆词优先
    /// （"Pathoschild.ContentPatcher" → "Content Patcher" → "ContentPatcher" → 全 UID）。</summary>
    private static IEnumerable<string> UidSearchTerms(string uid)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dot = uid.IndexOf('.');
        if (dot > 0 && dot < uid.Length - 1)
        {
            var namePart = uid[(dot + 1)..].Trim();
            if (namePart.Length > 0)
            {
                var camel = CamelSplit(namePart);
                if (camel.Length > 0 && seen.Add(camel)) yield return camel;
                if (seen.Add(namePart)) yield return namePart;
            }
            if (seen.Add(uid)) yield return uid;
        }
        else if (seen.Add(uid)) yield return uid;
    }

    /// <summary>驼峰拆词："BroadcastAPI" → "Broadcast API"（小写→大写与"大写后跟小写"处断开）。</summary>
    private static string CamelSplit(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (i > 0 && char.IsUpper(c)
                && (char.IsLower(s[i - 1])
                    || (i + 1 < s.Length && char.IsLower(s[i + 1]) && !char.IsWhiteSpace(s[i - 1]))))
                sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }


    /// <summary>
    /// 解析出可读的 mod 名称用于任务标题（/tasks 页需要显示"在下载哪个 mod"）。
    /// 网络请求拿不到名称时回退到传入的 fallback 或 "Mod #{id}"。
    /// </summary>
    private async Task<string> ResolveModTitleAsync(int modId, string? fallbackName)
    {
        try
        {
            var info = await _nexus.GetModAsync(modId);
            if (!string.IsNullOrWhiteSpace(info?.Name)) return info.Name;
        }
        catch (Exception __ex) { AppLog.Warn("InstallService", __ex.Message); }
        return string.IsNullOrWhiteSpace(fallbackName) ? $"Mod #{modId}" : fallbackName;
    }

    /// <summary>nxm://stardewvalley/mods/1234/files/5678?key=…&amp;expires=…</summary>
    private static bool TryParseNxm(
        string link, out int modId, out long fileId, out string key, out string expires)
    {
        modId = 0; fileId = 0; key = ""; expires = "";
        try
        {
            var uri = new Uri(link);
            var seg = uri.AbsolutePath.Trim('/').Split('/');
            var mi = Array.IndexOf(seg, "mods");
            var fi = Array.IndexOf(seg, "files");
            if (mi < 0 || fi < 0 || mi + 1 >= seg.Length || fi + 1 >= seg.Length)
                return false;

            modId = int.Parse(seg[mi + 1]);
            fileId = long.Parse(seg[fi + 1]);

            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length != 2) continue;
                if (kv[0] == "key") key = Uri.UnescapeDataString(kv[1]);
                if (kv[0] == "expires") expires = kv[1];
            }
            return key.Length > 0 && expires.Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
