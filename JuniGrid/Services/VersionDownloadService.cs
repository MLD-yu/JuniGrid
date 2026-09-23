using System.Collections.Concurrent;
using System.IO;
using System.Text.RegularExpressions;

namespace JuniGrid.Services;

/// <summary>
/// v1.4.5：游戏版本后台下载 —— 挂进任务中心，可暂停/继续/取消。
/// 登录只在设置页完成；这里只用本机已保存的 DepotDownloader token。
/// </summary>
public sealed class VersionDownloadService
{
    private readonly ConfigService _cfg;
    private readonly DepotDownloaderService _depot;
    private readonly TaskCenterService _center;
    private readonly UpdateService _updater;
    private readonly PageRefreshService _pageRefresh;

    private readonly ConcurrentDictionary<Guid, Func<CancellationToken, Task>> _resume = new();

    /// <summary>排队中的任务（等前一路下完）。会话闸本身在 DepotDownloaderService —— 这把闸
    /// 不可重入，这里只照它报出来的 <c>Queued</c> 标记状态，绝不自己再拿一遍。</summary>
    private readonly ConcurrentDictionary<Guid, byte> _queued = new();

    /// <summary>这个任务是不是还没轮到（在排队，不是在下载）。</summary>
    public bool IsQueued(TaskItem t) => _queued.ContainsKey(t.Id);

    public VersionDownloadService(ConfigService cfg, DepotDownloaderService depot, TaskCenterService center,
        UpdateService updater, PageRefreshService pageRefresh)
    {
        _cfg = cfg;
        _depot = depot;
        _center = center;
        _updater = updater;
        _pageRefresh = pageRefresh;
    }

    public bool IsAuthorized => _cfg.Current.DepotQrLoggedIn
        && !string.IsNullOrWhiteSpace(_cfg.Current.SteamCmdAccount);


    /// <summary>把某个历史版本丢进任务中心下载（不阻塞 UI）。
    /// applyToGame=false ⇒ 只把文件拉进缓存抽屉，**绝不碰游戏目录**；下完之后由用户在
    /// 版本列表里点版本号才切换。默认就是"只下载"，切版本必须是另一个明确动作。</summary>
    public TaskItem StartDownload(string label, string manifestId, string? versionLabel, bool applyToGame = false)
    {
        var cfg = _cfg.Current;
        if (!IsAuthorized)
            throw new DepotDownloaderService.DepotException("请先在「设置」里完成 Steam 扫码授权");

        // 同一 manifest 已有运行/暂停任务 → 不重复开。按标题认（进度行早就不带 manifest 了）
        var existing = _center.Snapshot().FirstOrDefault(t =>
            t.Kind == "gameversion" && t.Status is "running" or "paused"
            && t.Title.Contains(manifestId, StringComparison.Ordinal));
        if (existing is not null) return existing;

        var task = _center.Start($"下载游戏 {label} ({manifestId})", "gameversion");
        _center.Report(task, "准备中…", 0);

        var work = async (CancellationToken ct) =>
        {
            try
            {
                // 本地完整缓存 → 直接成功
                var curVer = _updater.GetGameVersion(cfg.GamePath);
                if (!applyToGame && _depot.IsStagedComplete(manifestId))
                {
                    _center.Report(task, "本地已有完整缓存", 100);
                    _center.Finish(task, true, $"已下载 {label}，点版本号即可切换");
                    return;
                }
                if (applyToGame && _depot.TryApplyStaged(cfg.GamePath, cfg.SteamAppId, "413151", manifestId, null, currentGameVersion: curVer))
                {
                    LockIfNeeded(cfg, manifestId);
                    cfg.LastHistoricalManifest = manifestId;
                    cfg.LastHistoricalLabel = label;
                    cfg.LastHistoricalInternalVersion = _updater.GetGameVersion(cfg.GamePath) ?? "";
                    _cfg.Save(cfg);
                    _center.Report(task, "本地已有完整缓存", 100);
                    await EnsureSmapiAfterSwitchAsync(task);
                    await EnsureXnaAfterSwitchAsync(task);
                    _center.Finish(task, true, $"已就绪 {label}（本地缓存）");
                    BroadcastPagesAfterSwitch();
                    return;
                }

                // 排队：同一账号只能一路 Steam 会话，闸只在最底层持有，这里照 Queued 标状态
                var progress = new Progress<DepotDownloaderService.Progress>(p =>
                {
                    if (p.Queued == true) _queued.TryAdd(task.Id, 0);
                    else if (p.Queued == false) _queued.TryRemove(task.Id, out _);
                    _center.Report(task, p.State, p.Percent);
                });

                var account = cfg.SteamCmdAccount;
                await _depot.QrDownloadAndApplyAsync(
                    cfg.GamePath, cfg.SteamAppId, "413151",
                    manifestId, versionLabel,
                    tokenAvailable: true, knownAccount: account,
                    onDataUri: _ => Task.CompletedTask,
                    progress, ct, applyToGame: applyToGame);

                if (!applyToGame)
                {
                    // 只下载：不写"最近切换"、不补 SMAPI/XNA、不刷页面 —— 游戏目录没被碰过
                    _center.Finish(task, true, $"已下载 {label}，点版本号即可切换");
                    return;
                }

                // 下载即已应用 → 同步"最近切换"记录，UI 不再显示旧版本号
                LockIfNeeded(cfg, manifestId);
                cfg.LastHistoricalManifest = manifestId;
                cfg.LastHistoricalLabel = label;
                cfg.LastHistoricalInternalVersion = _updater.GetGameVersion(cfg.GamePath) ?? "";
                _cfg.Save(cfg);
                await EnsureSmapiAfterSwitchAsync(task);
                await EnsureXnaAfterSwitchAsync(task);

                _center.Finish(task, true, $"已下载 {label}，可在「已下载」里切换");
                BroadcastPagesAfterSwitch();
            }
            catch (OperationCanceledException)
            {
                if (task.Status == "paused")
                    _center.Report(task, "已暂停", task.Percent);
                else
                {
                    try { _depot.DeleteStagedPackage(manifestId); } catch { }
                    _center.Finish(task, false, "已取消");
                }
            }
            catch (Exception ex)
            {
                _center.Finish(task, false, ex.Message);
            }
            finally { _queued.TryRemove(task.Id, out _); }   // 别让行上留着「等待中」
        };

        _resume[task.Id] = work;
        _ = Task.Run(() => work(task.Cts.Token), CancellationToken.None);
        return task;
    }

    /// <summary>P0-1：后台下载完成若已 apply 到游戏目录，Mods/立绘数据随之失效，广播已打开页面重扫。</summary>
    private void BroadcastPagesAfterSwitch()
    {
        try
        {
            _pageRefresh.Request("mods");
            _pageRefresh.Request("portraits");
            _pageRefresh.Request("version");
        }
        catch (Exception ex) { AppLog.Warn("VersionDownload", "广播页面刷新失败: " + ex.Message); }
    }

    /// <summary>历史降级应用成功后同步 appmanifest 只读锁，防止 Steam 自动更新覆盖。</summary>
    private void LockIfNeeded(JuniGridConfig cfg, string manifestId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(cfg.GamePath)) return;
            if (DepotDownloaderService.IsLatestOfficialManifest(cfg.SteamAppId, manifestId)) return;
            var mf = SteamService.FindAppManifest(cfg.GamePath, cfg.SteamAppId);
            if (mf is null) return;
            var (ok, _) = SteamService.SetGameVersionLock(mf, true);
            if (ok) cfg.LockGameVersion = true;
        }
        catch (Exception ex) { AppLog.Warn("VersionDownload", "切换后同步版本锁失败: " + ex.Message); }
    }

    /// <summary>
    /// v1.6.1：切完本体后 SMAPI 必须跟版本走 —— 该版本没恢复出加载器（首次切到 /
    /// 旧残缺缓存被淘汰）且存在适配 tag 时，静默补装一次对应的 SMAPI。装完
    /// UpdateService 会把产物写进该版本 staging + 共享池，之后来回切换免下载。
    /// 失败不阻塞切版本，只在任务里提示（可到 Mod 页一键安装）。
    /// </summary>
    private async Task EnsureSmapiAfterSwitchAsync(TaskItem task)
    {
        try
        {
            var cfg = _cfg.Current;
            var gamePath = cfg.GamePath;
            if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath)) return;
            if (File.Exists(Path.Combine(gamePath, "StardewModdingAPI.exe"))) return;   // 版本缓存/共享池已恢复

            var gameVer = _updater.GetGameVersion(gamePath);
            var tag = UpdateService.RecommendSmapiTag(gameVer);
            if (tag is null) return;   // 1.0/1.1、1.2.29- 等无适配版本，不装

            _center.Report(task, $"正在补装 SMAPI {tag}（适配游戏 {gameVer}）…", 99);
            var info = await _updater.CheckSmapiForGameAsync(null, gameVer);
            if (info.Error is not null || info.InstallerZipUrl is null)
            {
                _center.Report(task, "SMAPI 未能自动补装：" + (info.Error ?? "没找到安装包地址") + "，可到 Mod 页一键安装", 99);
                return;
            }
            var err = await _updater.RunSmapiInstallerAsync(info, gamePath,
                new Progress<UpdateService.InstallProgress>(p =>
                    _center.Report(task, p.Message, p.Percent)),
                CancellationToken.None);
            _center.Report(task, err is null
                ? $"SMAPI {info.LatestVersion ?? tag} 已随游戏版本就位"
                : "SMAPI 未能自动补装：" + err + "，可到 Mod 页一键安装", 99);
        }
        catch (Exception ex)
        {
            AppLog.Warn("VersionDownload", "切换后补装 SMAPI 失败: " + ex.Message);
        }
    }

    /// <summary>
    /// v1.6.2：切到 XNA 时代版本（1.0–1.4）时确保系统装了 XNA 运行库 —— 新电脑没有它，
    /// SMAPI 能起、游戏本体必崩（Microsoft.Xna.Framework 找不到）。1.5+ 换 MonoGame、
    /// 运行库随游戏走，不用管。下载校验安装全在 XnaRedistService；失败不阻塞切版本。
    /// </summary>
    private async Task EnsureXnaAfterSwitchAsync(TaskItem task)
    {
        try
        {
            var gamePath = _cfg.Current.GamePath;
            if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath)) return;

            var gameVer = _updater.GetGameVersion(gamePath);
            if (!XnaRedistService.GameNeedsXna(gameVer) || XnaRedistService.IsInstalled()) return;

            _center.Report(task, "系统缺少 XNA 运行库（老版本游戏依赖），正在安装…", 99);
            var err = await XnaRedistService.EnsureInstalledAsync(
                (m, p) => _center.Report(task, m, p));
            _center.Report(task, err ?? "XNA 运行库已就绪，老版本游戏可以启动了", 99);
        }
        catch (Exception ex)
        {
            AppLog.Warn("VersionDownload", "切换后安装 XNA 失败: " + ex.Message);
        }
    }

    public void Pause(TaskItem t)
    {
        if (t.Status != "running") return;
        _center.Pause(t);
    }

    public void Resume(TaskItem t)
    {
        if (t.Status != "paused") return;
        if (_resume.TryGetValue(t.Id, out var work))
        {
            _center.ResumeMark(t);
            _ = Task.Run(() => work(t.Cts.Token), CancellationToken.None);
            return;
        }

        // 重启后从 tasks.json 恢复的任务，worker 委托随上一个进程一起没了 —— 原先点「继续」
        // 静默无反应（TaskCenterService.Resume 找不到 worker 直接 return false）。
        // 任务标题里带着 manifest，按它重新起一路，并把这条僵尸行摘掉。
        var m = Regex.Match(t.Title, @"\((\d+)\)\s*$");
        if (!m.Success)
        {
            _center.Finish(t, false, "认不出这个任务要下哪个版本，回版本列表重新点「下载」");
            return;
        }
        var manifestId = m.Groups[1].Value;
        var cfg = _cfg.Current;
        var kv = _depot.GetMergedVersions(cfg.SteamAppId, cfg.CustomHistoricalVersions)
            .FirstOrDefault(k => string.Equals(k.ManifestId, manifestId, StringComparison.Ordinal));
        if (kv is null)
        {
            _center.Finish(t, false, "这个版本已不在列表里，无法继续");
            return;
        }
        if (!IsAuthorized)
        {
            _center.Finish(t, false, "请先在「设置」里完成 Steam 扫码授权，再点「下载」");
            return;
        }
        _center.Remove(t);
        try
        {
            var again = StartDownload(kv.DisplayName, manifestId, kv.Version);
            // 半截包保留：底层 QrDownloadAndApplyAsync 认出「这个目录就是这个 manifest 的」时
            // 不清空，改用 -verify-all 让 DepotDownloader 校验后续传（尾部 chunk 超时是国内常态，
            // 每次从 0 开始就永远下不完）。
            _center.Report(again, "接着上次没下完的部分继续", again.Percent);
        }
        catch (Exception ex) { AppLog.Warn("VersionDownload", "续传重开失败: " + ex.Message); }
    }
}
