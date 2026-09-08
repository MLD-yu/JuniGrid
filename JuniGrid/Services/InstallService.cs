using System.Collections.Concurrent;
using System.IO;
using System.Text;
using Microsoft.Win32;

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
    private readonly UpdateQueueService _queue;
    private readonly TaskCenterService _center;

    public InstallService(ConfigService cfg, NexusService nexus, ModService mods,
        UpdateQueueService queue, TaskCenterService center)
    {
        _cfg = cfg;
        _nexus = nexus;
        _mods = mods;
        _queue = queue;
        _center = center;
    }

    public event Action? OnChanged;

    // v1.1.6：RecentStatus 列表已删 —— 只写不读的死状态（全仓库无任何读取方），
    // OnChanged 事件本身仍被 Mods.razor 使用，保留。

    public bool Busy { get; private set; }

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
        if (Busy) return "上一个安装还没完成，等它结束再试";

        var cfg = _cfg.Current;
        if (string.IsNullOrWhiteSpace(cfg.NexusApiKey))
            return "还没配置 Nexus API Key —— 先到「Nexus」页粘贴";
        if (string.IsNullOrWhiteSpace(cfg.GamePath))
            return "还没设置游戏目录 —— 先到「设置」页选择";

        var taskTitle = await ResolveModTitleAsync(cfg.NexusApiKey, modId, null);
        var task = _center.Start($"下载并安装 {taskTitle}", "install");

        void Step(string msg, double? pct = null, double? speed = null) =>
            _center.Report(task, msg, pct, speed);

        Busy = true;
        string? zipPath = null;   // v1.1.3：取消时清理半截包用（catch 里拿不到 try 内的局部量）
        try
        {
            Step("正在获取文件信息…", 2);
            var file = await _nexus.GetLatestMainFileAsync(cfg.NexusApiKey, modId);
            if (file is null) { _center.Finish(task, false, "找不到可下载的文件"); return "找不到可下载的文件"; }

            Step("正在获取下载地址…", 5);
            var dl = await _nexus.GetDownloadUrlAsync(cfg.NexusApiKey, modId, file.FileId);
            if (dl.NeedsPremium)
            { _center.Finish(task, false, "需要 Nexus Premium 会员，已改为网页方式"); return "premium：这个 mod 的直链下载需要 Nexus Premium 会员"; }
            if (dl.Url is null)
            { _center.Finish(task, false, "获取下载地址失败：" + (dl.Error ?? "未知错误")); return "获取下载地址失败：" + (dl.Error ?? "未知错误"); }

            var zip = Path.Combine(StoragePaths.DownloadsDir,
                $"direct-{modId}-{file.FileId}.zip");
            zipPath = zip;
            Directory.CreateDirectory(Path.GetDirectoryName(zip)!);

            var progress = new Progress<NexusDownloadProgress>(p =>
                Step(p.Message, p.Percent, p.SpeedMBps));
            Step($"正在下载 {file.Name}…", 8, 0);
            await _nexus.DownloadFileAsync(dl.Url, zip, progress, task.Cts.Token);
            if (task.Cts.IsCancellationRequested)   // 下完才发现被移除 → 别装了，清掉半截包
            { try { File.Delete(zip); } catch { } return "已取消"; }

            Step("正在安装到 Mods…", 95);
            var err = _mods.InstallNew(cfg.GamePath, zip, out var modName, modId);
            if (err is not null)
            { _center.Finish(task, false, "安装失败：" + err); return "安装失败：" + err; }

            _queue.NotifyInstalled(modId);
            // v0.69.0：记录「最后下载日期」（详情页标题下 + 文件页签绿色✓ 用）
            cfg.ModLastDownload[modId.ToString()] = DateTime.Now.ToString("yyyy-MM-dd");
            cfg.ModFileLastDownload[file.FileId.ToString()] = DateTime.Now.ToString("yyyy-MM-dd");
            _cfg.Save(cfg);
            var done = $"安装完成：{modName ?? "新 Mod"}";
            _center.Finish(task, true, done);
            Notify("✅ " + done);
            return null;
        }
        catch (OperationCanceledException)
        {
            // v1.1.3：用户移除了任务 → 清半截包，静默退出（任务条目已不在列表）
            try { if (zipPath is not null && File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            return "已取消";
        }
        catch (Exception ex)
        {
            _center.Finish(task, false, ex.Message);
            Notify("❌ " + ex.Message);
            return ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    public async Task HandleNxmLinkAsync(string link)
    {
        if (Busy)
        {
            Notify("⏳ 上一个安装还没完成，等它结束再点");
            return;
        }
        var task = _center.Start("网页一键安装（Nexus）", "install");
        string? zipPath = null;   // v1.1.3：取消清理用

        void Step(string msg, double? pct = null, double? speed = null) =>
            _center.Report(task, msg, pct, speed);

        Busy = true;
        try
        {
            Step("解析收到的 Nexus 下载链接…", 2);
            if (!TryParseNxm(link, out var modId, out var fileId, out var key, out var exp))
            {
                _center.Finish(task, false, "无法解析链接");
                Notify("❌ 无法解析链接：" + link);
                return;
            }

            var cfg = _cfg.Current;
            if (string.IsNullOrWhiteSpace(cfg.NexusApiKey))
            {
                _center.Finish(task, false, "还没配置 Nexus API Key");
                Notify("❌ 还没配置 Nexus API Key —— 先到「Nexus」页粘贴");
                return;
            }
            if (string.IsNullOrWhiteSpace(cfg.GamePath))
            {
                _center.Finish(task, false, "还没设置游戏目录");
                Notify("❌ 还没设置游戏目录 —— 先到「设置」页选择");
                return;
            }

            // 解析出 modId 后把任务标题补上 mod 名称，方便在 /tasks 页识别
            task.Title = "下载并安装 " + await ResolveModTitleAsync(cfg.NexusApiKey, modId, null);

            Step($"正在获取下载地址（Mod #{modId}）…", 8);
            // v0.62.0：恢复带 API key 头 —— Nexus 的 download_link.json 端点强制要求 apikey 头，
            // 即使 URL 里有 key/expires，没头直接 401（v0.61 把 key 去掉反而引入了这个错）。
            var dl = await _nexus.GetNxmDownloadUrlAsync(cfg.NexusApiKey, modId, fileId, key, exp);
            if (dl.Url is null)
            {
                _center.Finish(task, false, dl.Error ?? "获取下载地址失败");
                Notify("❌ " + (dl.Error ?? "获取下载地址失败"));
                _queue.NotifyFailed(modId);   // 链接过期/被拒 → 跳过，别让队列卡死
                return;
            }

            var zip = Path.Combine(StoragePaths.DownloadsDir, $"nxm-{modId}-{fileId}.zip");
            zipPath = zip;
            Directory.CreateDirectory(Path.GetDirectoryName(zip)!);

            var progress = new Progress<NexusDownloadProgress>(p =>
                Step(p.Message, p.Percent, p.SpeedMBps));
            Step("正在下载…", 12, 0);
            await _nexus.DownloadFileAsync(dl.Url, zip, progress, task.Cts.Token);
            if (task.Cts.IsCancellationRequested)   // 下完才发现被移除 → 别装了，清掉半截包
            { try { File.Delete(zip); } catch { } return; }

            Step("正在安装到 Mods…", 95);
            var err = _mods.InstallNew(cfg.GamePath, zip, out var modName, modId);
            if (err is null)
            {
                _queue.NotifyInstalled(modId);   // 更新队列：装完一个，自动前进
                _center.Finish(task, true, $"安装完成：{modName ?? "新 Mod"}");
                Notify($"✅ 安装完成：{modName ?? "新 Mod"}（已在 Mod 管理页可见）");
            }
            else
            {
                _center.Finish(task, false, "安装失败：" + err);
                Notify("❌ 安装失败：" + err);
                _queue.NotifyFailed(modId);   // 装不进去也推进队列
            }
        }
        catch (OperationCanceledException)
        {
            try { if (zipPath is not null && File.Exists(zipPath)) File.Delete(zipPath); } catch { }
        }
        catch (Exception ex)
        {
            _center.Finish(task, false, ex.Message);
            Notify("❌ " + ex.Message);
        }
        finally
        {
            Busy = false;
        }
    }

    private void Notify(string msg) => OnChanged?.Invoke();

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

    /// <summary>最近一次一键装依赖的逐项结果（Mods 页结果弹窗的数据源）。
    /// Status: installed / manual / framework / unresolved / failed。</summary>
    public IReadOnlyList<DependencyRunItem>? LastDependencyRun { get; private set; }

    public sealed record DependencyRunItem(string Uid, string Status, string? Name, int? ModId, string? Detail);

    /// <summary>
    /// 一键安装缺失依赖。onlyUids 为 null = 全部缺失依赖；传入具体 UID 列表 =
    /// 只装这批（以及它们装上后新暴露的传递依赖）。preResolved = 确认弹窗已经
    /// 搜索解析过的 UID → modId（零搜索开销直接用，校验照做）。返回 null 表示
    /// 流程跑完（个别依赖可能转手动/失败，逐项结果看 <see cref="LastDependencyRun"/>）。
    /// </summary>
    public async Task<string?> InstallMissingDependenciesAsync(IReadOnlyList<string>? onlyUids = null,
        IReadOnlyDictionary<string, int>? preResolved = null)
    {
        if (Busy) return "上一个安装还没完成，等它结束再试";
        var cfg = _cfg.Current;
        if (string.IsNullOrWhiteSpace(cfg.NexusApiKey))
            return "还没配置 Nexus API Key —— 先到「Nexus」页粘贴";
        if (string.IsNullOrWhiteSpace(cfg.GamePath))
            return "还没设置游戏目录 —— 先到「设置」页选择";

        var task = _center.Start(
            onlyUids is null ? "一键安装缺失依赖" : $"安装缺失依赖（{onlyUids.Count} 项）", "install");
        void Step(string msg, double? pct = null, double? speed = null) => _center.Report(task, msg, pct, speed);

        Busy = true;
        var outcomes = new List<DependencyRunItem>();
        var toOpen = new List<string>();   // v1.2.2：需手动的依赖页面，结束时批量在浏览器打开
        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var premiumBlocked = false;
        string? zipPath = null;
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
                foreach (var dep in batch)
                {
                    task.Cts.Token.ThrowIfCancellationRequested();
                    processed.Add(dep);
                    var idx = processed.Count;
                    Step($"({idx}/{total}) 正在处理 {dep}…", Math.Round(idx * 90.0 / (total + 1), 1));

                    // ① 加载器本体不是 mod，自动装不进去
                    if (dep.Equals("SMAPI", StringComparison.OrdinalIgnoreCase))
                    {
                        outcomes.Add(new DependencyRunItem(dep, "framework", "SMAPI", null,
                            "SMAPI 是 mod 加载器本体，无法作为普通 mod 安装 —— 请用 SMAPI 安装器（启动器首页可检查/更新 SMAPI）"));
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
                            Step($"✗ {dep}：Nexus 搜索不到匹配的 mod，已打开搜索页转人工");
                            AddUnresolvedOutcome(dep, toOpen, outcomes);
                            continue;
                        }
                        Step($"⏭ {dep}：免费账户不能 API 直下，已打开网页转手动");
                        outcomes.Add(new DependencyRunItem(dep, "manual", face.Name, face.Id,
                            "已在浏览器打开此页面 —— 点「Mod Manager Download」，启动器会自动接管下载安装（Nexus 免费账户限制）"));
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
                            Step($"({idx}/{total}) 正在获取 {c.Name} 的文件信息…");
                            var file = await _nexus.GetLatestMainFileAsync(cfg.NexusApiKey, c.Id);
                            if (file is null) { lastErr = "找不到可下载的文件"; continue; }

                            var dl = await _nexus.GetDownloadUrlAsync(cfg.NexusApiKey, c.Id, file.FileId);
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
                                Step($"({idx}/{total}) 正在下载 {c.Name}…", null, 0);
                                await _nexus.DownloadFileAsync(dl.Url, zipPath, progress, task.Cts.Token);
                                if (task.Cts.IsCancellationRequested) return "已取消";

                                Step($"({idx}/{total}) 正在安装 {c.Name}…", Math.Round(idx * 95.0 / (total + 1), 1));
                                var err = _mods.InstallNew(cfg.GamePath, zipPath, out var modName, c.Id,
                                    requireUniqueId: dep);
                                if (err == ModService.UidMismatchError)
                                { lastErr = $"候选「{c.Name}」不含 {dep}，换下一个"; continue; }
                                if (err is not null) { lastErr = err; continue; }

                                // 成功：记住解析结果 + 与直装同款的收尾（更新队列/最后下载日期/刷新列表）
                                cfg.DependencyNexusIds[dep] = c.Id;
                                cfg.ModLastDownload[c.Id.ToString()] = DateTime.Now.ToString("yyyy-MM-dd");
                                cfg.ModFileLastDownload[file.FileId.ToString()] = DateTime.Now.ToString("yyyy-MM-dd");
                                _cfg.Save(cfg);
                                _queue.NotifyInstalled(c.Id);
                                outcomes.Add(new DependencyRunItem(dep, "installed", modName ?? c.Name, c.Id, null));
                                Step($"✓ 已安装 {modName ?? c.Name}（{dep}）");
                                Notify("");
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
                            ? $"({idx}/{total}) 已知候选不匹配，正在搜索 {dep}…"
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
                    Step($"⏭ {dep}：Nexus 直下需要 Premium，已打开网页转手动");
                    if (face is not null) toOpen.Add($"https://www.nexusmods.com/stardewvalley/mods/{face.Id}");
                }
                    else if (searched && cands.Count == 0)
                    {
                        Step($"✗ {dep}：Nexus 搜索不到匹配的 mod，已打开搜索页转人工");
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

            LastDependencyRun = outcomes;
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
            Notify("");
            return null;
        }
        catch (OperationCanceledException)
        {
            try { if (zipPath is not null && File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            return "已取消";
        }
        catch (Exception ex)
        {
            LastDependencyRun = outcomes;
            _center.Finish(task, false, "一键装依赖失败：" + ex.Message);
            return ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>确认弹窗的展示信息：UID → (mod 名, Nexus modId, 封面)。
    /// 免 key GraphQL 按下载数取首个命中，仅用于展示 —— 实际安装仍以下载后的
    /// UniqueID 校验为准，这里搜错顶多显示错封面，不会装错 mod。</summary>
    public sealed record DependencyDisplayInfo(string Uid, string? Name, int? ModId, string? CoverUrl);

    /// <summary>v1.2.3：展示信息进程内缓存 —— 同会话反复打开弹窗不再重搜（mod 名/封面基本不变）。</summary>
    private static readonly ConcurrentDictionary<string, DependencyDisplayInfo> DisplayCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>解析单个依赖 UID 的展示信息：进程内缓存 → 配置持久缓存 → 免 key 搜索（命中后两处都回写）。
    /// 搜索失败/无命中返回 null（调用方退回显示 UID）。</summary>
    public async Task<DependencyDisplayInfo?> ResolveDependencyDisplayAsync(string uid)
    {
        if (DisplayCache.TryGetValue(uid, out var cached)) return cached;
        // v1.2.4：配置持久缓存 —— 跨会话同步出结果，重启后弹窗首开不再等搜索
        var cfg = _cfg.Current;
        if (cfg.DependencyDisplays.TryGetValue(uid, out var saved))
        {
            var fromSaved = new DependencyDisplayInfo(uid,
                saved.Name.Length > 0 ? saved.Name : null,
                saved.ModId > 0 ? saved.ModId : null,
                saved.CoverUrl.Length > 0 ? saved.CoverUrl : null);
            DisplayCache[uid] = fromSaved;
            return fromSaved;
        }
        try
        {
            var term = UidSearchTerms(uid).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(term)) return null;
            var hits = await _nexus.BrowseModsAsync("downloads", 0, 5, "stardewvalley", searchText: term);
            var top = hits?.FirstOrDefault(h => h.Id > 0);
            if (top is null) return null;
            var info = new DependencyDisplayInfo(uid, top.Name, top.Id,
                string.IsNullOrWhiteSpace(top.ThumbnailUrl) ? top.PictureUrl : top.ThumbnailUrl);
            DisplayCache[uid] = info;
            // 回写持久缓存，下次会话秒开；写失败不影响本次展示（大不了下次重搜）
            try
            {
                cfg.DependencyDisplays[uid] = new DependencyDisplayEntry
                {
                    Name = top.Name ?? "",
                    ModId = top.Id,
                    CoverUrl = info.CoverUrl ?? "",
                };
                _cfg.Save(cfg);
            }
            catch { }
            return info;
        }
        catch { return null; }
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
            var info = await _nexus.GetModAsync(cfg.NexusApiKey, ids[0]);
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
            try { hits = await _nexus.BrowseModsAsync("downloads", 0, 8, "stardewvalley", searchText: term); }
            catch { }
            if (hits is null) continue;
            foreach (var h in hits)
                if (h.Id > 0 && seen.Add(h.Id)) list.Add(h);
            if (list.Count >= 12) break;   // 候选上限，防跑飞
        }
        return list;
    }

    /// <summary>premiumBlocked 快速通道专用：只找一个候选 id 生成手动链接（known → 搜索）。</summary>
    private async Task<NexusModListEntry?> FirstCandidateAsync(
        JuniGridConfig cfg, string uid, IReadOnlyDictionary<string, int>? preResolved)
    {
        var known = await KnownCandidatesAsync(cfg, uid, preResolved);
        if (known.Count > 0) return known[0];
        var hits = await SearchDependencyCandidatesAsync(uid);
        return hits.FirstOrDefault();
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
    private async Task<string> ResolveModTitleAsync(string apiKey, int modId, string? fallbackName)
    {
        try
        {
            var info = await _nexus.GetModAsync(apiKey, modId);
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
