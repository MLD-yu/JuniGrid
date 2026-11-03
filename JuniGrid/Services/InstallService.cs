using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace JuniGrid.Services;

/// <summary>
/// Handles nxm:// links handed over by Windows (via the single-instance
/// named pipe, or startup args), downloads the file and installs it into
/// Mods/. Works for FREE Nexus accounts: the key+expires inside the nxm
/// link come from the user clicking "Mod Manager Download" on the website.
/// </summary>
public sealed partial class InstallService
{
    private readonly ConfigService _cfg;
    private readonly NexusService _nexus;
    private readonly ModService _mods;
    private readonly TaskCenterService _center;
    private readonly UpdateService _updater;
    private readonly GameService _game;
    private readonly PageRefreshService _pageRefresh;
    private readonly NexusWebDownloadService _webDownload;

    public InstallService(ConfigService cfg, NexusService nexus, ModService mods,
        TaskCenterService center, UpdateService updater,
        GameService game, PageRefreshService pageRefresh,
        NexusWebDownloadService webDownload)
    {
        _cfg = cfg;
        _nexus = nexus;
        _mods = mods;
        _center = center;
        _updater = updater;
        _game = game;
        _pageRefresh = pageRefresh;
        _webDownload = webDownload;
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
    // 计数跟着锁走：锁是 static（全局一把），排队数也必须是 static，否则多实例会各自少报
    private static int _installQueue;   // 正在排队等写入的安装数（进度提示用，近似值）

    internal static async Task EnterInstallGateAsync(Action<string, double?, double?> Step,
        double pct, CancellationToken ct)
    {
        var pos = Interlocked.Increment(ref _installQueue);
        try
        {
            if (pos > 1)
                Step(LocService.Tf("排队等待写入 Mods…（前面还有 {0} 个安装）", pos - 1), pct, null);
            // 必须带 ct。不带 token 的 WaitAsync 谁都取消不掉它 —— 暂停的那一跑会一直排在门外，
            // 于是「前面还有 N 个安装」里的 N 有一多半是自己上一跑留下的僵尸（实测 2026-10-07
            // 09:22:07 那 39 秒：新一跑排在同一行任务暂停前的旧跑后面，白等）。
            await _installGate.WaitAsync(ct);
        }
        finally
        {
            Interlocked.Decrement(ref _installQueue);
        }
    }

    internal static void ExitInstallGate() => _installGate.Release();

    /// <summary>这一跑还能不能把半截 zip 当垃圾删掉 —— 下载完才发现被取消的那道闸、
    /// 以及 OCE 收尾用的都是它。
    /// <para>两种情况必须留着包：<b>暂停</b>（下一跑要靠它续传）、<b>被取代</b>
    /// （新一跑正拿同一个路径在下载/解压，删了就是它的事）。</para>
    /// <para>旧写法只看 <c>task.Status</c>，那是<b>行级</b>的、会被新一跑改回 running ——
    /// 于是 2026-10-07 09:22:46 一个暂停了 40 秒的僵尸照旧走"用户取消了，清半截包"，
    /// 把新一跑正要装的包删了，真错误「这个包要盖在…上」被假错误「Could not find file」盖掉。</para>
    /// </summary>
    internal static bool MayDiscardHalfPack(TaskItem task, CancellationToken ct)
        => task.Status != "paused" && !IsStaleRun(task, ct);

    /// <summary>这一跑还是不是这条任务行的【当前】那一跑。「继续」会换一个全新的 CTS
    /// （<see cref="TaskItem.ResetCts"/>），所以对不上号的都是被取代的旧跑。
    /// <para>旧跑一律不许再碰共享的东西：半截 zip 见 <see cref="MayDiscardHalfPack"/>，
    /// 行状态见各 worker 拿到安装锁后的那道 <c>IsStaleRun</c> 闸。</para>
    /// </summary>
    internal static bool IsStaleRun(TaskItem task, CancellationToken ct) => ct != task.Cts.Token;

    /// <summary>
    /// v1.09.0 收尾兜底：<b>OperationCanceledException ≠ 用户点了取消</b>。
    /// <para>
    /// HttpClient 的超时（Http.Timeout=15s / SlowHttp=45s）抛的是 TaskCanceledException，
    /// 而它<b>继承自 OperationCanceledException</b> —— 于是"网络超时"会一路穿过所有
    /// <c>catch (OperationCanceledException)</c>，落进"已取消"分支。而那些分支旧写法
    /// <b>只 return、不 Finish</b>：任务行就永远停在 running，界面最后一行进度永远转圈。
    /// </para>
    /// <para>
    /// 实测（2026-10-05 02:34，一键装依赖卡 45%「正在获取 Scale Up Unofficial 的文件信息…」）：
    /// 请求 15s 超时 → 工人 <b>02:34:55 就已经退出</b> → 之后进程零 TCP 连接。
    /// 所以"卡住时 netstat 全 0"不是"卡在进程内等待"，而是<b>人已经走了、留下一行僵尸 running</b>。
    /// 重启后 RestoreStatus 把 running 兜成 failed，所以下次开机看着又"正常"。
    /// </para>
    /// <para>
    /// 判据：真被取消/移除时 <c>task.Cts.IsCancellationRequested</c> 为 true；
    /// 超时是"没人要求取消，只是请求自己断了"，该落failed 而不该静默退出。
    /// </para>
    /// </summary>
    /// <returns>处理了"伪装成取消的超时"则返回给调用方带错误信息的字符串；否则 null，调用方按原语义返回「已取消/已暂停」。</returns>
    internal string? FinishIfTimeout(TaskItem task, Exception ex, CancellationToken ct)
    {
        if (IsStaleRun(task, ct)) return null;             // 被「继续」取代的旧跑：行归新一跑管
        if (task.Status != "running") return null;         // 已 paused/已落终态，别覆盖
        if (!_center.Contains(task)) return null;          // 用户已移除该行：静默，不弹幽灵 toast
        if (task.Cts.IsCancellationRequested) return null; // 真·用户取消：保持静默
        var msg = LocService.Tr("网络请求超时或被中断，本步已中止") + "：" + ex.Message;
        _center.Finish(task, false, msg);
        return msg;
    }

    // ------------------------------------------------------------------
    // 直接安装（免弹内置浏览器）
    // ------------------------------------------------------------------
    // 在 ModDetail / 榜单页点「安装」时，后台向 Nexus 请求一次性下载
    // 链接并流式下载安装，全程不出启动器。免费账户限速约 1MB/s；
    // 命中 403（该 mod 强制 Premium）时的出路见 <see cref="PremiumNeedsWebFallback"/>。
    // ------------------------------------------------------------------

    /// <summary>免费账号撞 Premium 的统一出口：打开该 mod 的文件页，并回一句给调用方的话。
    /// <para>为什么开页面写在服务里而不是调用方：这条出口有三个入口会走到（详情页那颗
    /// 「安装」、任务面板的「↻ 重试」、重启后按 NexusId 重新发起），后两个拿不到 worker 的
    /// 返回值 —— 以前打开动作只挂在详情页那一个调用方上，于是任务面板重试时界面写着
    /// 「已改为网页方式」而浏览器一次都没弹（2026-10-07 09:34 实机，mod 20581）。</para>
    /// <para>v1.2.10：URL 带上 file_id —— 用户点的是哪个文件就落到哪个文件，
    /// 而不是每次都让他自己在文件列表里重找一遍。</para>
    /// <para><paramref name="open"/> 由调用点给（生产传 <c>UpdateService.OpenUrl</c>），
    /// 为的是这条出口能在不开真浏览器的情况下被验证。</para>
    /// </summary>
    internal static string PremiumNeedsWebFallback(int modId, long fileId, Action<string> open, string? reason = null)
    {
        // Nexus 文件页 —— 免费账号直链被 403 时把用户领到这里点「Mod Manager Download」，
        // nxm 接管链路会自己把包装进 Mods。
        open($"https://www.nexusmods.com/stardewvalley/mods/{modId}?tab=files&file_id={fileId}");
        var msg = LocService.Tr("premium：应用内自动下载没成功（已打开文件页，可在页上点 Mod Manager Download）");
        return string.IsNullOrWhiteSpace(reason) ? msg : msg + "（" + reason + "）";
    }

    /// <summary>
    /// 【更新 / 依赖】也要走的一条取链通道：API 直链 → 免费账号 403 时自动转应用内隐藏窗口。
    /// <para>为什么单独抽出来：一键直装（<see cref="InstallModDirectAsync"/>）里内联了同样的
    /// 两段逻辑，但「批量更新」「单条更新」「装缺失依赖」这三条路以前各自判断
    /// <c>NeedsPremium</c> 就直接放弃 —— 于是同一个 mod，点「安装」能在应用内下完，
    /// 点「更新」却跳外部浏览器，用户看到的是两套行为。</para>
    /// <para>返回的 <see cref="NexusDownloadResult.NeedsPremium"/> 只有在【两条路都不通】
    /// 时才为 true，调用方据此决定退不退回打开网页。</para>
    /// </summary>
    public async Task<NexusDownloadResult> ResolveDownloadUrlAsync(
        int modId, long fileId,
        Action<string, double?, double?>? report = null,
        CancellationToken ct = default,
        TaskItem? task = null)
    {
        var dl = await _nexus.GetDownloadUrlAsync(modId, fileId, ct, patient: true);
        if (dl.Url is not null) return dl;
        if (!dl.NeedsPremium) return dl;   // 网络/其他错误：别去开窗口，原样交给调用方

        report?.Invoke(LocService.Tr("正在通过应用内页面获取下载（不需要操作）…"), null, null);
        // 应用内取链最长 100 秒（要等一个被节流过的倒计时周期），这期间没有任何进度可报。
        // 不登记豁免的话，僵尸看门狗 60 秒就会把这条任务判死 ——
        // 2026-11-04 实测：两条更新都卡在「正在从 Nexus 页面获取下载链接…」被看门狗杀掉。
        using var longRun = task is null ? null : (IDisposable)_center.BeginLongRunning(task);
        var auto = await _webDownload.TryGetDirectUrlAsync(modId, fileId, report, ct);
        return auto.Url is not null
            ? NexusDownloadResult.Ok(auto.Url)
            : new NexusDownloadResult(null, auto.Error ?? dl.Error, true);
    }

    /// <summary>下载缓存里已经有这个 (mod, 文件) 的完整压缩包了吗？有就返回它的路径。
    /// <para>三条下载通道各用各的文件名（<c>direct-</c> 一键直装、<c>nxm-</c> 网页 nxm:// 回流、
    /// <c>dep-</c> 依赖），认的是【文件身份】不是【当初从哪条通道来】：实测 #11209 当初从
    /// nxm:// 下的（<c>nxm-11209-52234.zip</c>），重启后界面上的「重试」按 modId 重新发起走的是
    /// 直装通道 —— 只看 <c>direct-</c> 那一个名字就永远等不到本机已有的那份，而免费账号的
    /// API 直链必然 403，这条任务就再也重试不动了。</para></summary>
    private static string? CachedPack(int modId, long fileId)
    {
        foreach (var prefix in new[] { "direct", "nxm", "dep" })
        {
            var p = Path.Combine(StoragePaths.DownloadsDir, $"{prefix}-{modId}-{fileId}.zip");
            if (ModService.ArchiveReadable(p)) return p;
        }
        return null;
    }

    /// <summary>
    /// 一键直装：后台下载并安装指定 mod 的最新 MAIN 文件。
    /// 返回 null 表示成功；否则返回错误消息（含 "premium" 关键字表示需要 Premium ——
    /// 这种情况本方法已经自己打开过文件页了，调用方只负责把话显示出来，别再开一次）。
    /// 进度接入任务中心：右下角出现任务，/tasks 页能看到下载百分比/速度/每步详情。
    /// </summary>
    /// <param name="fileId">指定要装的文件；null = 最新 MAIN（详情页那颗「安装」的老行为）。
    /// <para>详情页的「文件」列表用它装【指定那一条】：同一个 mod 常常同时挂 CP 主包、
    /// AT 素材包、旧版本好几个文件，只按「最新 MAIN」挑会让玩家永远装不到自己要的变体
    /// （实测 mod 1915 就有 CP/AT 两条并行发布）。</para></param>
    /// <param name="fileLabel">拿不到 files.json 时的兜底显示名（任务标题用）。</param>
    public async Task<string?> InstallModDirectAsync(int modId, long? fileId = null, string? fileLabel = null)
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
        // 带号入场：这个 mod 上一次失败/暂停的那一行就地重开，而不是在任务管理里
        // 再排一条 —— 同一次下载点两下多出两行是错的。
        var task = _center.StartOrRestart($"下载并安装 {taskTitle}", "install", modId);

        // v1.1.7：整段工作可重入 —— 暂停后继续会重新挂 worker；半截包由 ResumableDownload 续传
        async Task<string?> RunAsync(CancellationToken ct)
        {
            void Step(string msg, double? pct = null, double? speed = null) =>
                _center.Report(task, msg, pct, speed);

            Interlocked.Increment(ref _busyCount);
            string? zipPath = null;   // v1.1.3：取消时清理半截包用（catch 里拿不到 try 内的局部量）
            // v1.2.10：应用内自动下载（免费账号）的两块状态 —— catch 里要判断
            // 「这次是从隐藏窗口拿的链接吗」，失败时才好退回打开文件页那条老路。
            long mainFileId = 0;
            var usedWebChannel = false;
            try
            {
                Step(LocService.Tr("正在获取文件信息…"), 2);
                // 指定文件（详情页文件列表）→ 按 fileId 精确取；否则还是「最新 MAIN」。
                var file = fileId is long wantFileId
                    ? await _nexus.GetFileByIdAsync(modId, wantFileId, patient: true, ct: ct)
                    : await _nexus.GetLatestMainFileAsync(modId, patient: true, ct: ct);
                ct.ThrowIfCancellationRequested();
                // files.json 里没有这一条（文件已下架 / 接口抖动）也别直接判死：
                // 带着 fileId 继续，直链那一步会给出确切原因，比「找不到可下载的文件」有用。
                if (file is null && fileId is long missingId)
                    file = new NexusFileInfo(missingId, fileLabel ?? LocService.Tr("所选文件"), "", "");
                if (file is null) { _center.Finish(task, false, LocService.Tr("找不到可下载的文件")); return LocService.Tr("找不到可下载的文件"); }
                mainFileId = file.FileId;
                // 只有 MAIN 才记「已装文件快照」（见下面 RecordInstall 那段的说明）
                var isMainFile = string.IsNullOrEmpty(file.Category)
                    || file.Category.Equals("MAIN", StringComparison.OrdinalIgnoreCase);

                var zip = Path.Combine(StoragePaths.DownloadsDir,
                    $"direct-{modId}-{file.FileId}.zip");
                zipPath = zip;
                Directory.CreateDirectory(Path.GetDirectoryName(zip)!);

                // 缓存里已经有【这一个文件】了 ⇒ 直链和下载两步都跳过。文件名按 fileId 取，
                // 作者换新文件 fileId 就变，所以复用的只会是同一个文件；而"重试"要的正是
                // 把上次那一步重跑一遍，不是重新下载。实测 2026-10-08：#11209 的 4.5 MB
                // 04:37 就完整下好了，界面上点「重试」却卡在「需要 Nexus Premium 会员」——
                // 免费账号的 API 直链必然 403，于是这条任务永远重试不动。
                // 复用时刻意不改 zipPath：那是"移除任务时清掉半截包"的目标，本机已有的完整包不该被它删掉。
                var pack = zip;
                var cached = CachedPack(modId, file.FileId);
                if (cached is not null)
                {
                    pack = cached;
                    Step(LocService.Tf("正在安装（用本机已下载的 {0}）…", file.Name), 8);
                }
                else
                {
                    Step(LocService.Tr("正在获取下载地址…"), 5);
                    // v1.3.13：一次取回这个文件的全部 CDN 候选（旧实现拿第一个就 return，
                    // 首选节点抽风时整条下载只能从头重试）
                    var dl = await _nexus.GetDownloadUrlsAsync(modId, file.FileId, ct, patient: true);
                    ct.ThrowIfCancellationRequested();
                    if (dl.NeedsPremium)
                    {
                        // v1.2.10：免费账号的直链必然 403。先走应用内的隐藏窗口
                        // （自动按下网页上那颗 Slow download，把一次性直链取回来），
                        // 拿不到才退回「打开系统浏览器」的老路 —— 这条通道不改变
                        // 任何后续逻辑，直链到手后与 Premium 用户走完全相同的下载/安装。
                        Step(LocService.Tr("正在通过应用内页面获取下载（不需要操作）…"), 6, null);
                        // 同 ResolveDownloadUrlAsync：取链这段最长 100 秒零进度，登记豁免别让看门狗误杀
                        using var webRun = _center.BeginLongRunning(task);
                        var auto = await _webDownload.TryGetDirectUrlAsync(modId, file.FileId,
                            (msg, pct, spd) => Step(msg, pct, spd), ct);
                        ct.ThrowIfCancellationRequested();
                        if (auto.Url is null)
                        {
                            _center.Finish(task, false, LocService.Tr("应用内没取到直链，已改为打开网页"));
                            return PremiumNeedsWebFallback(modId, file.FileId, UpdateService.OpenUrl, auto.Error);
                        }
                        usedWebChannel = true;
                        dl = NexusDownloadResult.Ok(auto.Url);
                    }
                    if (dl.Url is null)
                    { _center.Finish(task, false, LocService.Tr("获取下载地址失败：") + (dl.Error ?? "未知错误")); return LocService.Tr("获取下载地址失败：") + (dl.Error ?? "未知错误"); }

                    var progress = new Progress<NexusDownloadProgress>(p =>
                        Step(p.Message, p.Percent, p.SpeedMBps));
                    Step(LocService.Tf("正在下载 {0}…", file.Name), 8, 0);
                    await _nexus.DownloadFileAsync(dl.Url, zip, progress, ct, dl.Mirrors);

                    // v1.3.13：md5 抽查。只有 N 网 files.json 给了值才比；
                    // 不符先重下一次（仅一次），两次都不符只记警告、不拦安装 ——
                    // 校验是为了少装坏包，不是为了把原本能装的包拦下来。
                    var md5Second = DownloadIntegrity.Md5Verdict.Skipped;
                    if (DownloadIntegrity.CheckMd5(zip, file.Md5) == DownloadIntegrity.Md5Verdict.Mismatch)
                    {
                        Step(LocService.Tr("文件校验值不符，重新下载一次…"), 8, 0);
                        try { File.Delete(zip); } catch { }
                        await _nexus.DownloadFileAsync(dl.Url, zip, progress, ct, dl.Mirrors);
                        md5Second = DownloadIntegrity.CheckMd5(zip, file.Md5);
                    }

                    // 下完先按文件头验一次：取链那几层偶尔会把网页静态资源当直链
                    // （2026-11-04 04:43 实机：avatars.nexusmods.com 的 100×100 头像被 Chromium
                    // 标成 application/octet-stream，两条更新都下回一张 jpg，最后报「压缩包损坏」）。
                    // 在下载落盘这一步就拦下来：删坏包 + 报准确原因，别让用户猜。
                    var packFmt = ModService.DetectArchiveFormat(zip);
                    if (packFmt is "jpeg" or "gif" or "webp" or "html" or "unknown" or "empty")
                    {
                        try { File.Delete(zip); } catch { }
                        var why = packFmt switch
                        {
                            "jpeg" or "gif" or "webp" => LocService.Tr("下载到的是图片而不是压缩包：取到的直链其实是网页上的静态资源"),
                            "html" => LocService.Tr("下载到的是网页而不是压缩包（可能被网络拦截）"),
                            _ => LocService.Tr("下载到的文件不是可识别的压缩包"),
                        };
                        var bad = why + LocService.Tr("，已清理坏包，请重试");
                        _center.Finish(task, false, bad);
                        return bad;
                    }
                    if (md5Second == DownloadIntegrity.Md5Verdict.Mismatch)
                        Step(LocService.Tr("注意：文件与 Nexus 记录的 md5 不一致（可能官方重打包过），继续安装"), null, null);
                    if (ct.IsCancellationRequested)   // 下完才发现被移除/暂停 → 别装了
                    {
                        if (MayDiscardHalfPack(task, ct))
                        { try { File.Delete(zip); } catch { } }
                        throw new OperationCanceledException(ct);
                    }
                }

                // v1.3.4：安装段 —— 写入 Mods 全局串行（下载阶段已完成，多任务在此依次写入）
                // v1.1.8：InstallNew 是同步重活（解压/肖像校验/CP 转换，实测可达 50s+）。
                // 若 await 之后续体落在 Blazor UI 线程，整个界面会冻住、任务胶囊停在
                // 最后一帧进度（「91% · 3.1 M/s」假死）。强制丢到线程池执行。
                string? err;
                string? modName = null;
                var notes = new List<string>();
                // 豁免覆盖【等安装锁 + 解压 + 扫描】整段：InstallNew 实测可达 50s+，装几百 MB 的
                // 合集包会到几分钟，加上等_lock 和后续 Scan，这期间一行进度都不报。
                // 不登记豁免的话，僵尸看门狗（60s 宽限）会把正在正常安装的任务误杀。
                using var longRun = _center.BeginLongRunning(task);
                await EnterInstallGateAsync(Step, 95, ct);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    Step(LocService.Tr("正在安装到 Mods…"), 95);
                    // v1.6.5：Portraiture 素材包（无 manifest+PNG）在 ModService 内直接转换成
                    // CP 肖像包，不再依赖/安装 Portraiture 框架。包名用任务标题（可能被翻译
                    // 服务机翻，仅作目录名/展示，无碍功能）。
                    var packReqs = await PackRequirementsIfManifestlessAsync(pack, modId);
                    err = await Task.Run(() => _mods.InstallNew(cfg.GamePath, pack, out modName, modId,
                        portraiturePackName: taskTitle, cfg: _cfg, installNotes: notes,
                        packRequirements: packReqs), ct);
                }
                finally { ExitInstallGate(); }
                // 被「继续」取代的那一跑到此为止：它可能真把包装进去了，但行状态、记账、
                // 提示都交给当前那一跑去写 —— 两跑装的是同一个 zip，结果一致，而僵尸先落终态
                // 会让界面在新一跑还在跑时闪一条假失败。
                if (IsStaleRun(task, ct)) return "已暂停";
                if (err is not null)
                { _center.Finish(task, false, LocService.Tr("安装失败：") + err); return LocService.Tr("安装失败：") + err; }

                // v0.69.0：记录「最后下载日期」（详情页标题下 + 文件页签绿色✓ 用）
                cfg.ModLastDownload[modId.ToString()] = DateTime.Now.ToString("yyyy-MM-dd");
                cfg.ModFileLastDownload[file.FileId.ToString()] = DateTime.Now.ToString("yyyy-MM-dd");
                cfg.ModInstalledFile[modId.ToString()] = file.FileId.ToString();
                // 记 N 网 MAIN 安装快照（只记账，不动作者的 manifest —— 见 RecordInstall 的说明：
                // 那个字段的读者是 SMAPI，抄进单段号会让整个 mod 不加载）。
                // 「N 网文件 1 / 包内 Version 0.0.1」那种永远显示可更新，由 ShouldShowUpdate
                // 的「单段 vs 多段 = 不同编号体系」那道闸处理，认领则靠 fileId，都不需要改安装物。
                // v1.3.5：从文件列表装的【非 MAIN】文件（CP/AT 素材包、旧版本）不记账 ——
                // 记了就等于宣称「这个 mod 装的是这份可选文件」，而更新检查拿它跟远程 MAIN
                // 的 fileId 比，会让这一行永远挂着 ⇧ 更新。
                if (isMainFile && (!string.IsNullOrWhiteSpace(modName) || modId > 0))
                {
                    var folderGuess = modName ?? "";
                    // Scan 是磁盘全量扫描，同样别落在 UI 线程
                    var scanned = await Task.Run(() => _mods.Scan(cfg.GamePath), ct);
                    var entry = NexusUpdateTruth.FindInstalledEntry(scanned, modId, folderGuess);
                    if (entry is not null)
                    {
                        entry.NexusModId ??= modId;
                        NexusUpdateTruth.RecordInstall(cfg, entry, modId, file.FileId, file.Version ?? "");
                    }
                    else
                    {
                        NexusUpdateTruth.RecordInstallByModId(cfg, folderGuess.Length > 0 ? folderGuess : modId.ToString(),
                            modId, file.FileId, file.Version ?? "");
                    }
                }
                _cfg.Save(cfg);
                // 覆盖包搬过文件时 notes 里有一条去向（汉化文件放根目录不会生效）——
                // 不说的话用户查不到 zh.json 到底落在哪。
                var done = $"安装完成：{modName ?? "新 Mod"}"
                    + (notes.Count > 0 ? " · " + string.Join(" · ", notes) : "");
                _center.Finish(task, true, done);
                Notify();
                // P0-3：直装可能落入肖像包（含 Portraiture→CP 转换）→ 立绘页重扫
                NotifyPortraits();
                return null;
            }
            catch (OperationCanceledException ex)
            {
                if (!MayDiscardHalfPack(task, ct)) return "已暂停";   // 包留着：暂停=续传，被取代=新一跑在用
                // v1.1.3：用户移除了任务 → 清半截包，静默退出（任务条目已不在列表）
                try { if (zipPath is not null && File.Exists(zipPath)) File.Delete(zipPath); } catch { }
                return FinishIfTimeout(task, ex, ct) ?? "已取消";
            }
            catch (Exception ex)
            {
                // v1.2.10：应用内通道拿回来的直链如果下不动（拦到的是 N 网中间页、
                // 一次性签名过期、CDN 拒绝无浏览器上下文的请求），别把用户堵在一条
                // 他自己没法处理的消息上 —— 直接退回「打开文件页」那条老路，
                // nxm 回流仍然能把包接上。
                if (usedWebChannel && !ct.IsCancellationRequested && mainFileId > 0)
                {
                    _center.Finish(task, false, LocService.Tr("应用内下载失败，已改为网页方式"));
                    return PremiumNeedsWebFallback(modId, mainFileId, UpdateService.OpenUrl, ex.Message);
                }
                _center.Finish(task, false, ex.Message);
                Notify();
                return ex.Message;
            }
            finally
            {
                Interlocked.Decrement(ref _busyCount);
                // 失败不摘 worker —— 「重试」要用它，条目在 Remove 时回收
                if (task.Status == "done")
                    _center.UnregisterResume(task);
            }
        }

        _center.RegisterResume(task, ct => Task.Run(() => RunAsync(ct), CancellationToken.None));
        return await Task.Run(() => RunAsync(task.Cts.Token));
    }

    /// <summary>
    /// 详情页「文件」列表里的「仅下载」：把选中的那一条存到下载缓存目录，**不装进 Mods**。
    /// <para>为什么单独一条：有些文件不是独立 mod（散 xnb 素材、旧版本备份、给别的管理器用的
    /// 补丁），装进去反而多一行垃圾；但用户又确实想拿到文件本身。</para>
    /// <para>取链与直装完全同一条（Premium 直链 → 免费账号应用内隐藏窗口），下完在资源管理器里定位。</para>
    /// </summary>
    public async Task<string?> DownloadFileOnlyAsync(int modId, long fileId, string fileName)
    {
        if (!NexusService.IsAuthenticated)
            return LocService.Tr("还没登录 Nexus —— 先到「Nexus」页登录");

        var safe = SafeFileName(fileName);
        if (!safe.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) safe += ".zip";
        var task = _center.Start($"下载文件 {safe}", "download");

        async Task<string?> RunAsync(CancellationToken ct)
        {
            void Step(string msg, double? pct = null, double? speed = null) =>
                _center.Report(task, msg, pct, speed);
            Interlocked.Increment(ref _busyCount);
            try
            {
                Step(LocService.Tr("正在获取下载地址…"), 3);
                var dl = await ResolveDownloadUrlAsync(modId, fileId,
                    (msg, pct, spd) => Step(msg, pct, spd), ct, task);
                ct.ThrowIfCancellationRequested();
                if (dl.Url is null)
                {
                    _center.Finish(task, false, dl.NeedsPremium
                        ? LocService.Tr("应用内没能取到下载链接（可能需要登录 Nexus 网页版）")
                        : LocService.Tr("获取下载地址失败：") + dl.Error);
                    return dl.Error;
                }

                Directory.CreateDirectory(StoragePaths.DownloadsDir);
                var dest = Path.Combine(StoragePaths.DownloadsDir, safe);
                // 同名文件不覆盖：加 (2)(3) 后缀，免得用户下了两版旧文件只剩一份
                dest = UniquifyPath(dest);
                Step(LocService.Tf("正在下载 {0}…", safe), 8, 0);
                var progress = new Progress<NexusDownloadProgress>(p => Step(p.Message, p.Percent, p.SpeedMBps));
                await _nexus.DownloadFileAsync(dl.Url, dest, progress, ct, dl.Mirrors);
                ct.ThrowIfCancellationRequested();

                _center.Finish(task, true, LocService.Tf("已下载到 {0}", dest));
                try
                {
                    // 落到文件夹并选中该文件（windowsVerbatimArguments 那套在 ProcessStartInfo 里
                    // 靠参数串自己拼引号，路径带空格也不会断）
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{dest}\"",
                        UseShellExecute = true,
                    });
                }
                catch { /* 定位失败不影响已下好的文件 */ }
                return null;
            }
            catch (OperationCanceledException)
            {
                _center.Finish(task, false, LocService.Tr("已取消"));
                return "已取消";
            }
            catch (Exception ex)
            {
                _center.Finish(task, false, ex.Message);
                return ex.Message;
            }
            finally { Interlocked.Decrement(ref _busyCount); }
        }

        return await Task.Run(() => RunAsync(task.Cts.Token));
    }

    /// <summary>文件名清洗：去掉 Windows 非法字符和结尾的点/空格（NTFS 会吃掉尾点，
    /// 之后 File.Exists 就永远找不到自己刚写的那个文件）。</summary>
    private static string SafeFileName(string? name)
    {
        var s = string.IsNullOrWhiteSpace(name) ? "nexus-file" : name.Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        s = s.TrimEnd('.', ' ');
        return s.Length == 0 ? "nexus-file" : s;
    }

    private static string UniquifyPath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return path;
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
            await _nexus.DownloadFileAsync(dl.Url, zipPath, dlProgress, ct, dl.Mirrors);
            if (ct.IsCancellationRequested)
            {
                if (MayDiscardHalfPack(task, ct))
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
            cfg.ModInstalledFile[modId.ToString()] = fileId.ToString();
            _cfg.Save(cfg);
            var done = "安装完成：SMAPI（你选择的版本）";
            _center.Finish(task, true, done);
            Notify();
        }
        catch (OperationCanceledException)
        {
            if (!MayDiscardHalfPack(task, ct)) return;   // 包留着：暂停=续传，被取代=新一跑在用
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
        var task = _center.StartOrRestart("下载并安装 SMAPI - Stardew Modding API", "install", SmapiNexusId);

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
                await EnterInstallGateAsync(Step, 8, ct);
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
                if (ct.IsCancellationRequested
                    && (task.Status == "paused" || IsStaleRun(task, ct)))
                    return "已暂停";
                if (err is not null)
                { _center.Finish(task, false, LocService.Tr("安装失败：") + err); return LocService.Tr("安装失败：") + err; }

                var done = $"安装完成：SMAPI {info.LatestVersion ?? ""}".TrimEnd();
                _center.Finish(task, true, done);
                Notify();
                return null;
            }
            catch (OperationCanceledException ex)
            {
                return FinishIfTimeout(task, ex, ct)
                    ?? (task.Status == "paused" || IsStaleRun(task, ct) ? "已暂停" : "已取消");
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
                // 失败不摘 worker —— 「重试」要用它，条目在 Remove 时回收
                if (task.Status == "done")
                    _center.UnregisterResume(task);
            }
        }

        _center.RegisterResume(task, ct => Task.Run(() => RunAsync(ct), CancellationToken.None));
        return await Task.Run(() => RunAsync(task.Cts.Token));
    }

    public async Task HandleNxmLinkAsync(string link)
    {
        // ── 第一件事：这条 nxm 是不是我们自己那个隐藏窗口撂出来的？ ──
        // N 网文件页必须带 &nmm=1 才会渲染下载面板，而那个面板会顺手触发一次 nxm://；
        // Windows 又把链接转回本进程。旧版不分青红皂白就开一行「下载并安装 X」，
        // 结果同一个 mod 在任务管理里出现「更新 Mod「X」」+「下载并安装 X」两行
        // （2026-11-04 用户反馈）。这里先认领：正在等直链的那个窗口就是它主人，
        // 把解析出来的真直链塞回去即可，不再新建任务。
        if (TryParseNxm(link, out var pendingMod, out var pendingFile, out var pendingKey, out var pendingExp)
            && NexusWebDownloadService.HasPending(pendingMod, pendingFile))
        {
            try
            {
                var hand = await _nexus.GetNxmDownloadUrlAsync(pendingMod, pendingFile, pendingKey, pendingExp);
                if (NexusWebDownloadService.TryDeliver(pendingMod, pendingFile, hand.Url))
                {
                    AppLog.Info("Install", $"nxm 回流已并入正在进行的任务 mod={pendingMod} file={pendingFile}");
                    Notify();
                    return;
                }
            }
            catch (Exception ex)
            {
                // 认领失败不能把整条回流堵死 —— 落到下面照旧走独立安装
                AppLog.Warn("Install", "nxm 回流合流失败：" + ex.Message);
            }
        }

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
                task.NexusId = modId;   // 号先落，再取标题 —— 标题取失败也不该丢掉重发的凭据
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
                await _nexus.DownloadFileAsync(dl.Url, zip, progress, ct, dl.Mirrors);
                if (ct.IsCancellationRequested)   // 下完才发现被移除/暂停 → 别装了
                {
                    if (MayDiscardHalfPack(task, ct))
                    { try { File.Delete(zip); } catch { } }
                    throw new OperationCanceledException(ct);
                }

                // v1.3.4：安装段 —— 写入 Mods 全局串行
                string? nxmErr;
                string? modName = null;
                var nxmNotes = new List<string>();
                // 同直装：等锁 + InstallNew + 后续 Scan 整段不报进度，豁免看门狗
                using var nxmLongRun = _center.BeginLongRunning(task);
                await EnterInstallGateAsync(Step, 95, ct);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    Step(LocService.Tr("正在安装到 Mods…"), 95);
                    var nxmTitle = task.Title.StartsWith("下载并安装 ") ? task.Title["下载并安装 ".Length..] : null;
                    // v1.6.5：Portraiture 素材包直接转换成 CP 肖像包（不再装框架）
                    // v1.1.8：同直装路径，InstallNew 丢线程池，避免冻住 UI
                    var nxmPackReqs = await PackRequirementsIfManifestlessAsync(zip, modId);
                    nxmErr = await Task.Run(() => _mods.InstallNew(cfg.GamePath, zip, out modName, modId,
                        portraiturePackName: nxmTitle, cfg: _cfg, installNotes: nxmNotes,
                        packRequirements: nxmPackReqs), ct);
                }
                finally { ExitInstallGate(); }
                if (IsStaleRun(task, ct)) return;   // 行归当前那一跑写
                if (nxmErr is null)
                {
                    _center.Finish(task, true, $"安装完成：{modName ?? "新 Mod"}"
                        + (nxmNotes.Count > 0 ? " · " + string.Join(" · ", nxmNotes) : ""));
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
                        cfg.ModInstalledFile[modId.ToString()] = fileId.ToString();
                        // 版本号必须一起记：留空 ⇒ 快道（smapi.io 只给版本号、不给 fileId）那条路
                        // 再没有任何判据能认出「这一版我已经装过了」，⇧ 装上就消不掉
                        var nxmVer = (await _nexus.GetFileByIdAsync(modId, fileId, ct: ct))?.Version ?? "";
                        var guess = modName ?? "";
                        var scanned = await Task.Run(() => _mods.Scan(cfg.GamePath), ct);
                        var entry = NexusUpdateTruth.FindInstalledEntry(scanned, modId, guess);
                        if (entry is not null)
                        {
                            entry.NexusModId ??= modId;
                            NexusUpdateTruth.RecordInstall(cfg, entry, modId, fileId, nxmVer);
                        }
                        else
                            NexusUpdateTruth.RecordInstallByModId(cfg,
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
            catch (OperationCanceledException ex)
            {
                if (!MayDiscardHalfPack(task, ct)) return;   // 包留着：暂停=续传，被取代=新一跑在用
                try { if (zipPath is not null && File.Exists(zipPath)) File.Delete(zipPath); } catch { }
                FinishIfTimeout(task, ex, ct);
            }
            catch (Exception ex)
            {
                _center.Finish(task, false, ex.Message);
                Notify();
            }
            finally
            {
                Interlocked.Decrement(ref _busyCount);
                // 失败不摘 worker —— 「重试」要用它，条目在 Remove 时回收
                if (task.Status == "done")
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

    /// <summary>没有 manifest.json 的包（汉化补丁 / 覆盖型文件包）在 ModService 里靠<b>包内路径</b>
    /// 认宿主；整包只有一个裸 <c>zh.json</c> 时连那层路径都没有（实测 41314），此时唯一说得出
    /// 宿主是谁的地方，是作者在这个包自己页面上登记的那条前置。
    /// <para>所以：只在【整包没有 manifest.json】时才发这一次免 key 请求，正常 mod 一次都不发；
    /// "有没有 manifest"交给 ModService.ArchiveHasManifest 按魔数分派 —— 用 ZipFile 直接 peek
    /// 会漏掉"扩展名叫 .zip、内容其实是 RAR"的包（实测 31795「Grandpa's Farm 中文翻译」）。</para></summary>
    private async Task<IReadOnlyList<NexusRequirement>?> PackRequirementsIfManifestlessAsync(
        string zipPath, int? modId)
    {
        if (modId is not > 0) return null;
        if (ModService.ArchiveHasManifest(zipPath)) return null;
        try { return await _nexus.GetModRequirementsAsync(modId.Value); }
        catch (Exception ex) { AppLog.Warn("Install", $"读 #{modId} 页面登记的前置失败：{ex.Message}"); return null; }
    }

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
        var runCt = task.Cts.Token;   // 进门时领的身份牌，收尾时用它判「这一跑还在位吗」（见 IsStaleRun）
        void Step(string msg, double? pct = null, double? speed = null) => _center.Report(task, msg, pct, speed);

        // v1.3.4：批量装依赖全程持安装锁（逐项下载+写入都是完整的安装会话），
        // 与其它 mod 安装互斥排队。
        Interlocked.Increment(ref _busyCount);
        var outcomes = new List<DependencyRunItem>();
        var toOpen = new List<string>();   // v1.2.2：需手动的依赖页面，结束时批量在浏览器打开
        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 依赖 UID → 声明它的父包 Nexus 页面号（跨轮累积：传递依赖在第二轮才露头时，
        // 第一轮记下的父包仍然用得上）。
        var parentsByDep = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        // 同一个依赖被几个父包各自要求到哪一版（依赖方 manifest 的 MinimumVersion 原样）。
        // 「这一版在不在某一页」只能靠它判 —— 官网要求表可能整页不存在（见 FindVersionProviderAsync）。
        var needsByDep = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var premiumBlocked = false;
        string? zipPath = null;
        try
        {
            await EnterInstallGateAsync(Step, 2, runCt);
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
                    // 已装 UID → 名字/N网id/版本。版本这一列是给下面的「够不够新」判据用的。
                    // 每轮重扫：本轮刚装上的依赖，下一轮就该在这张表里认得；
                    // 「同包子模块」判定也必须有它（子模块在 Nexus 上没有独立页面可搜）。
                    var installedMap = new Dictionary<string, (string Name, int? NexusId, string Version)>(StringComparer.OrdinalIgnoreCase);
                    foreach (var m in mods)
                        if (!string.IsNullOrWhiteSpace(m.UniqueID))
                            installedMap[m.UniqueID.Trim()] = (string.IsNullOrWhiteSpace(m.Name) ? m.Folder : m.Name,
                                m.NexusModId, m.Version ?? "");

                    var batch = new List<string>();
                    // 已装版本表：判「装着但太旧」用（与 installedMap 同一份数据）
                    var installedVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in installedMap) installedVersions[kv.Key] = kv.Value.Version;
                    foreach (var m in mods)
                    {
                        // 该装/该换的依赖由 DependencyRules 一处判定 —— 必需依赖缺了就装、太旧也换；
                        // 可选依赖只在「装着但太旧」时才算（没装是作者允许的状态）。
                        // 实机 2026-10-29：弹窗按 UI 那份清单列出了 GMCM，而这里只遍历必需依赖，
                        // 任务跑了个空 —— 点了「开始安装」什么也没发生。
                        foreach (var dep0 in DependencyRules.DepsToProcess(m, installedVersions))
                        {
                            var dep = dep0.Trim();
                            if (string.IsNullOrWhiteSpace(dep) || processed.Contains(dep)) continue;
                            if (firstRound && allowedFirstRound is not null && !allowedFirstRound.Contains(dep)) continue;
                            // 记下父包要求的那一版（逐字留着，不折成 ≥ 门槛）—— 后面靠它认
                            // 「哪一页的文件表里真有这一版」。
                            if (m.DependencyMinVersions.TryGetValue(dep, out var needVer)
                                && !string.IsNullOrWhiteSpace(needVer))
                            {
                                if (!needsByDep.TryGetValue(dep, out var nl))
                                    needsByDep[dep] = nl = new List<string>();
                                if (!nl.Contains(needVer.Trim(), StringComparer.OrdinalIgnoreCase))
                                    nl.Add(needVer.Trim());
                            }
                            // 记下"这个依赖是谁的前置"：官网的 Requirements 表挂在父包身上，
                            // 拿父包页面号去问它，比按 UID 拆词全站搜索准得多。
                            if (m.NexusModId is > 0)
                            {
                                if (!parentsByDep.TryGetValue(dep, out var par))
                                    parentsByDep[dep] = par = new List<int>();
                                if (!par.Contains(m.NexusModId.Value)) par.Add(m.NexusModId.Value);
                            }
                            if (!batch.Contains(dep, StringComparer.OrdinalIgnoreCase)) batch.Add(dep);
                        }
                    }
                firstRound = false;
                if (batch.Count == 0) break;

                var total = processed.Count + batch.Count;

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
                    parentsByDep.TryGetValue(dep, out var parents);

                    // ── 版本证据闸（实机 2026-10-12）──
                    // 本机装着这个依赖、却仍低于某个父包的要求时，「它自己来自哪一页」不再是答案：
                    // 那一页可能根本给不出要求的版本（完美手册要 0.6.4-unofficial-mushymato.0，
                    // focustense 的 28870 整页最高 0.6.1 —— 而我们一遍遍把用户领回那一页，红字永远消不掉）。
                    // 只有「文件版本表里逐字含有要求串」的那一页才算数；一页都没有 ⇒ 不给任何页面号。
                    int? provider = null;
                    string providerName = "";
                    var needs = needsByDep.TryGetValue(dep, out var needList) ? needList : new List<string>();
                    if (installedMap.TryGetValue(dep, out var installedDep) && needs.Count > 0)
                    {
                        var hit = await FindDependencyProviderAsync(
                            cfg, dep, needs, installedDep.NexusId, parents);
                        if (hit is (int hid, string hname)) { provider = hid; providerName = hname; }
                    }
                    if (provider is null && needs.Count > 0 && installedMap.ContainsKey(dep))
                    {
                        var needLabel = string.Join("、", needs);
                        Step(LocService.Tf("✗ {0}：N 网候选页都没有 {1} 这一版", dep, needLabel));
                        outcomes.Add(new DependencyRunItem(dep, "manual", dep, null,
                            LocService.Tf("要求 {0}，本机 {1}；候选页的文件版本表里没有逐字等于该要求的版本"
                                + " —— 这一版可能只在 GitHub 等站外发布，不再指向任何 N 网页面",
                                needLabel, installedDep.Version)));
                        continue;
                    }

                    if (premiumBlocked)
                    {
                        if (provider is int provId)
                        {
                            Step(LocService.Tf("⏭ {0}：应用内没能自动下载，已打开有 {1} 那一版的页面",
                                dep, string.Join("、", needs)));
                            outcomes.Add(new DependencyRunItem(dep, "manual", providerName, provId,
                                "已在浏览器打开这一版真正所在的页面 —— 点「Mod Manager Download」，启动器会自动接管下载（应用内通道未取到链接）"));
                            toOpen.Add($"https://www.nexusmods.com/stardewvalley/mods/{provId}");
                            continue;
                        }
                        var face = await FirstCandidateAsync(cfg, dep, preResolved, parents);
                        if (face is null)
                        {
                            Step(LocService.Tf("✗ {0}：Nexus 搜索不到匹配的 mod，已打开搜索页转人工", dep));
                            AddUnresolvedOutcome(dep, toOpen, outcomes);
                            continue;
                        }
                        Step(LocService.Tf("⏭ {0}：应用内没能自动下载，已打开网页转手动", dep));
                        outcomes.Add(new DependencyRunItem(dep, "manual", face.Name, face.Id,
                            dep.Equals(ModService.PortraitureFrameworkUid, StringComparison.OrdinalIgnoreCase)
                                ? "Portraiture 框架需手动下载 —— 点「Mod Manager Download」后启动器自动接管；装完请到立绘页确认素材包"
                                : "已在浏览器打开此页面 —— 点「Mod Manager Download」，启动器会自动接管下载安装（应用内通道未取到链接）"));
                        toOpen.Add($"https://www.nexusmods.com/stardewvalley/mods/{face.Id}");
                        continue;
                    }

                    cands = await KnownCandidatesAsync(cfg, dep, preResolved,
                        installedMap.TryGetValue(dep, out var ownDep) ? ownDep.NexusId : null, parents);
                    // 有版本证据的那一页排最前；本机那份来自的页只当兜底。
                    if (provider is int firstId)
                        cands = cands.Where(c => c.Id != firstId)
                            .Prepend(new NexusModListEntry(firstId, providerName, "", "", 0, ""))
                            .ToList();
                    var searched = false;
                    // 这一轮搜索有没有真的问出结果：没答上（断网/限流）不能说成"搜不到"
                    var searchAnswered = false;
                    string? lastErr = null;
                    var ok = false;
                    while (true)
                    {
                        // ③ 逐候选：下载 → 校验 UniqueID → 安装（每批最多试 3 个，免费限速下省带宽）
                        foreach (var c in cands.Take(3))
                        {
                            task.Cts.Token.ThrowIfCancellationRequested();
                            Step(LocService.Tf("({0}/{1}) 正在获取 {2} 的文件信息…", idx, total, c.Name));
                            var file = await _nexus.GetLatestMainFileAsync(c.Id, patient: true, ct: task.Cts.Token);
                            if (file is null) { lastErr = LocService.Tr("找不到可下载的文件"); continue; }

                            // v1.3.5：免费账号也先走应用内隐藏窗口。以前这里一见 NeedsPremium
                            // 就把 premiumBlocked 打开 —— 余下每个依赖都转手动、收尾时分批开
                            // 十几个标签页。同一套机制，「安装」能在应用内下完，「装依赖」却要
                            // 跳浏览器，用户看到的是两副面孔。
                            var dl = await ResolveDownloadUrlAsync(c.Id, file.FileId,
                                (msg, pct, spd) => Step(msg, pct, spd), task.Cts.Token, task);
                            if (dl.NeedsPremium)
                            {
                                // 隐藏窗口也没拿到（未登录 / 验证过不去）→ 账号级限制，
                                // 余下依赖全部转手动，不再逐个去开窗口白等。
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
                                await _nexus.DownloadFileAsync(dl.Url, zipPath, progress, task.Cts.Token, dl.Mirrors);
                                if (task.Cts.IsCancellationRequested)
                                    return task.Status == "paused" ? "已暂停" : "已取消";

                                Step(LocService.Tf("({0}/{1}) 正在安装 {2}…", idx, total, c.Name), Math.Round(idx * 95.0 / (total + 1), 1));
                                string? modName = null;
                                // 解压 + 肖像校验 + CP 转换，期间不报任何进度 ⇒ 豁免看门狗，
                                // 否则装大包时会被误判成"工人已死"（实测这段可达 50s+）。
                                using var depLongRun = _center.BeginLongRunning(task);
                                var err = await Task.Run(() => _mods.InstallNew(cfg.GamePath, zipPath, out modName, c.Id,
                                    requireUniqueId: dep, cfg: _cfg), task.Cts.Token);
                                if (err == ModService.UidMismatchError)
                                { lastErr = LocService.Tf("候选「{0}」不含 {1}，换下一个", c.Name, dep); continue; }
                                if (err is not null) { lastErr = err; continue; }

                                // 成功：记住解析结果 + 与直装同款的收尾（更新队列/最后下载日期/刷新列表）
                                cfg.DependencyNexusIds[dep] = c.Id;
                                cfg.ModLastDownload[c.Id.ToString()] = DateTime.Now.ToString("yyyy-MM-dd");
                                cfg.ModFileLastDownload[file.FileId.ToString()] = DateTime.Now.ToString("yyyy-MM-dd");
                                cfg.ModInstalledFile[c.Id.ToString()] = file.FileId.ToString();
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
                        var srch = await SearchDependencyCandidatesAsync(dep);
                        cands = srch.Hits;
                        searchAnswered = srch.Answered;
                        if (cands.Count == 0) break;
                    }

                if (ok) continue;
                if (premiumBlocked)
                {
                    var face = cands.Count > 0 ? cands[0] : null;
                    outcomes.Add(new DependencyRunItem(dep, "manual", face?.Name, face?.Id,
                        "已在浏览器打开此页面 —— 点「Mod Manager Download」，启动器会自动接管下载安装（应用内通道未取到链接）"));
                    Step(LocService.Tf("⏭ {0}：Nexus 直下需要 Premium，已打开网页转手动", dep));
                    if (face is not null) toOpen.Add($"https://www.nexusmods.com/stardewvalley/mods/{face.Id}");
                }
                    else if (searched && cands.Count == 0 && searchAnswered)
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
        catch (OperationCanceledException ex)
        {
            try { if (zipPath is not null && File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            return FinishIfTimeout(task, ex, runCt)
                ?? (task.Status == "paused" ? "已暂停" : "已取消");
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
    /// 免 key GraphQL 的命中要过 <see cref="SearchHitIdentifies"/>（作者段＋名字段双闸）
    /// 才敢拿出来当"这个依赖是谁"
    /// —— 实际安装仍以下载后的 UniqueID 校验为准，但展示错了会把人直接带去别人的 mod 页。</summary>
    public sealed record DependencyDisplayInfo(string Uid, string? Name, int? ModId, string? CoverUrl);

    /// <summary>unresolved 收尾：结果项 + Nexus 站内搜索页链接（转人工确认，结束时统一开浏览器）。</summary>
    private void AddUnresolvedOutcome(string dep, List<string> toOpen, List<DependencyRunItem> outcomes)
    {
        toOpen.Add("https://www.nexusmods.com/stardewvalley/search/?gsearch="
            + Uri.EscapeDataString(UidSearchTerms(dep).FirstOrDefault() ?? dep)
            + "&gsearchtype=mods");
        outcomes.Add(new DependencyRunItem(dep, "unresolved", null, null,
            "自动搜索不到这个依赖 —— 已打开 Nexus 搜索页，请人工确认后点 Mod Manager Download（启动器会自动接管）"));
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
