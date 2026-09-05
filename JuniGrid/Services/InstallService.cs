using System.IO;
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

    /// <summary>Newest-first status feed, shown on the Mods page.</summary>
    public List<string> RecentStatus { get; } = new();

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
            var err = _mods.InstallNew(cfg.GamePath, zip, out var modName);
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
            var err = _mods.InstallNew(cfg.GamePath, zip, out var modName);
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

    private void Notify(string msg)
    {
        RecentStatus.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {msg}");
        if (RecentStatus.Count > 20)
            RecentStatus.RemoveAt(RecentStatus.Count - 1);
        OnChanged?.Invoke();
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
