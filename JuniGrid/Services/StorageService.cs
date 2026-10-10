using System.IO;

namespace JuniGrid.Services;

/// <summary>缓存与存储页的一个分类行：显示路径 + 占用 + 可否清理/迁移。
/// Tip 是悬浮「?」里的通俗解释（面向不了解 terminology 的用户）。
/// Destructive = 删掉的东西没法靠重新下载或应用自己重建（存档/mod 备份/配置等用户内容），
/// 界面上给它一个红色「⚠」而不是普通「?」。</summary>
public sealed record StorageCategory(
    string Id,
    string Name,
    string Note,
    string DisplayPath,
    string[] SizeRoots,     // 统计占用的根（文件或目录混合）
    string[] CleanRoots,    // 清理时删除内容的根
    bool Cleanable,
    bool Movable,
    string Tip = "",
    bool Destructive = false);

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

    /// <summary>列出一个目录下的现存条目；目录不存在或读不动就给空数组 —— 存储页不能因为一个
    /// 历史位置的权限问题而整页报错。</summary>
    private static string[] SafeEntries(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetFileSystemEntries(dir) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
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

        // v1.6.12：缓存根上的历史遗留 _smapi-install.dat（早期解压 install.dat 落下的）
        // 必须挂在本项下，否则 G3 审计会把它钉成「看不见就删不掉」的无主条目。
        list.Add(new("smapi", "SMAPI 安装包缓存", "安装包 zip 与解压临时目录",
            StoragePaths.SmapiInstallerDir,
            new[] { StoragePaths.SmapiInstallerDir, StoragePaths.LegacySmapiInstallDat },
            new[] { StoragePaths.SmapiInstallerDir, StoragePaths.LegacySmapiInstallDat },
            Cleanable: true, Movable: true,
            Tip: "安装或更新 SMAPI 时下载的官方安装包和解压文件。装完工作文件会自动删；其中 keep 子目录是特意留着复用的那一份安装包（这台机器没有对应版本抽屉时靠它免重下 40 MB）。清理这里只是下次装 SMAPI 时重新下载 不影响游戏和已经装好的 SMAPI"));

        // v1.1.4：应用自更新安装包缓存 —— 正常装完即自动删，但万一自动删除失效
        //（安装被强杀/删文件抛异常等），一个包 100MB 会悄悄堆积，必须让用户看得见、清得掉
        list.Add(new("selfupdate", "自更新安装包缓存", "应用自身更新的安装包（最新版安装成功后自动删除）",
            StoragePaths.SelfUpdateDir,
            new[] { StoragePaths.SelfUpdateDir }, new[] { StoragePaths.SelfUpdateDir },
            Cleanable: true, Movable: true,
            Tip: "应用自动更新时下载的安装包 安装成功后会自动删除 但如果更新中断可能残留 这里可以手动清掉 一个安装包约 100MB 清理后下次更新会重新下载"));

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

        // 更新前的 Mods 安全快照：清理 = 保留最近一次，其余全删（防呆：目录名是时间戳）。
        // 写入侧实际放在 <root>\smapi\Mods-<时间戳>（按工具分桶），旧布局可能直接是
        // <root>\Mods-<时间戳> —— 这里两级都枚举，只认 Mods-* 目录名。
        // v1.6.8 修复：此前只枚举一级，二级布局下 oldSnapshots 恒为空 → Cleanable 恒 false，
        // 清理按钮永远不出现（6.2GB 只能看着）。
        var backupRoot = StoragePaths.ModsBackupDir;
        static bool IsModsSnapshot(string p) =>
            Path.GetFileName(p).StartsWith("Mods-", StringComparison.OrdinalIgnoreCase);
        // 快照可能在 mods-backup\smapi\Mods-*（旧布局）也可能在
        // mods-backup\smapi\<版本号>\Mods-*（v1.6.11 按版本分桶）→ 递归找，别只枚举两级。
        IEnumerable<string> FindSnapshots(string dir, int depth)
        {
            foreach (var d in SafeDirs(dir))
            {
                if (IsModsSnapshot(d)) yield return d;
                else if (depth < 3) foreach (var x in FindSnapshots(d, depth + 1)) yield return x;
            }
        }
        var snapshotDirs = FindSnapshots(backupRoot, 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var oldSnapshots = snapshotDirs.Skip(1).ToArray();
        list.Add(new("backup", "更新前 Mods 备份", "SMAPI 更新前的安全快照（清理保留最近一次）", backupRoot,
            new[] { backupRoot }, oldSnapshots, Cleanable: oldSnapshots.Length > 0, Movable: true,
            Tip: "更新 SMAPI 前自动把全部 mod 备份一份 更新失败可用它恢复 清理只删较旧的备份 最近一次永远保留"));

        // v1.4.4：游戏版本缓存 —— 历史版本本地包，跟随统一缓存目录
        var verCache = StoragePaths.DepotStagingDir;
        list.Add(new("vercache", "游戏版本缓存", "历史版本本地包", verCache,
            new[] { verCache }, new[] { verCache }, Cleanable: true, Movable: true,
            Tip: "切换游戏版本时下载的完整游戏包 切回该版本时无需重新下载 清理会删掉全部已缓存版本 而且每个版本包里还躺着那一版的 Mods 抽屉（那是你的 mod 本体，删了要重装）"));

        // v1.2.7：默认缓存落点从 %TEMP%\JuniGrid 改成安装目录后，旧位置可能还剩搬不走的副本。
        // 必须让它在界面上看得见并可以清 —— 「看不见就删不掉」是 G3 审计钉过的那类无主条目。
        // 唯一不给清的时候：PendingWebView2MoveFrom 有值 —— 那表示本会话的 WebView2 还住在旧位置，
        // 删它等于删掉正在用的登录态。
        var legacyRoots = SafeEntries(StoragePaths.TempRoot);
        if (legacyRoots.Length > 0 && string.IsNullOrWhiteSpace(_cfg.Current.PendingWebView2MoveFrom))
        {
            list.Add(new("legacytemp", "旧缓存位置", "默认位置改到安装目录后剩下的副本", StoragePaths.TempRoot,
                legacyRoots, legacyRoots, Cleanable: true, Movable: false,
                Tip: "以前这些缓存默认放在系统的临时文件夹 现在改放到程序所在目录 这里是没能搬过去的旧副本 "
                    + "应用读的是新位置那一份 清掉这里只是删重复的旧副本 个别文件正被占用时会留在原处 重启后再清"));
        }

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

        // v1.6.11：这三项原先在界面上完全没有条目 —— 实测磁盘上有 15 MB 缩略图缓存、
        // 1.2 MB 扫描/翻译缓存、以及 Steam userdata 快照没人管（审计用例 G3 就是钉这个）。
        // v1.6.12：saves-hidden（版本收起的存档抽屉，可达数百 MB）与 userdata-backup 同理。
        var pcovers = new[]
        {
            // 必须是 InCache（CacheRoot ?? DefaultCacheRoot）—— 与写缩略图的 PackThumbService
            // 同一个入口。以前这里写的是 CacheRoot ?? "" 拼接，没设统一缓存目录时拼出裸相对名
            // "portrait-covers"：统计算不到真目录（1.1 MB 缩略图凭空消失）、「打开」跳到空文件夹。
            StoragePaths.InCache("portrait-covers"),
            Path.Combine(StoragePaths.AppDataDir, "portrait-scan-cache.json"),
            Path.Combine(StoragePaths.AppDataDir, "portrait-probe-cache.json"),
            // 早期版本（CacheRoot 还没统一时）把缩略图写在 AppData / LocalAppData 下，留下的目录也得有人管
            Path.Combine(StoragePaths.AppDataDir, "portrait-covers"),
            Path.Combine(StoragePaths.LocalAppDataDir, "portrait-covers"),
            // 农夫头像小图（1KB 级）：不单开清理行，但必须进审计认领，否则 G3 钉成无主条目
            Path.Combine(StoragePaths.AppDataDir, "farmer-full.png"),
            Path.Combine(StoragePaths.AppDataDir, "farmer-head.png"),
        };
        list.Add(new("pcovers", "内容包缩略图缓存", "内容包页生成的缩略图", pcovers[0],
            pcovers, pcovers, Cleanable: true, Movable: true,
            Tip: "内容包页为了秒开把缩略图存了一份 清理只是让它下次重新生成 不影响任何 mod"));

        var appCacheFiles = new[]
        {
            Path.Combine(StoragePaths.AppDataDir, "junigrid.translate-cache.json"),
            Path.Combine(StoragePaths.AppDataDir, "farmer-full.png"),
            Path.Combine(StoragePaths.AppDataDir, "farmer-head.png"),
        };
        // farmer-*.png 已挂进 pcovers 的 SizeRoots（G3 审计要求）；translate-cache 不在清理页展示。
        _ = appCacheFiles;

        var dataFiles = new[]
        {
            Path.Combine(StoragePaths.AppDataDir, "junigrid.config.json"),
            Path.Combine(StoragePaths.AppDataDir, "tasks.json"),
            Path.Combine(StoragePaths.AppDataDir, "playtime.json"),   // 游玩时长统计，属个人数据
        };
        list.Add(new("data", "设置与任务数据", "配置与任务记录（固定·不可清理）", StoragePaths.AppDataDir,
            dataFiles, Array.Empty<string>(), Cleanable: false, Movable: false,
            Tip: "你的设置 Nexus API Key mod 存档列表和下载任务记录 属于个人数据 程序永远不会自动清理它 删了应用回到刚装好的状态，Nexus 授权也要重新扫",
            Destructive: true));

        // 切旧版本前备份的 Steam userdata（成就/云存档本地记录）。只统计不清理 —— 删了出问题没法比对。
        var steamDataDir = StoragePaths.SteamUserdataBackupDir;
        list.Add(new("steamdata", "Steam 存档快照", "切旧版本前备份的 userdata（成就/云存档本地记录）", steamDataDir,
            new[] { steamDataDir }, Array.Empty<string>(), Cleanable: false, Movable: false,
            Tip: "切换到旧游戏版本前自动备份的 Steam 本地记录 只用于出问题时人工比对 不会自动清理",
            Destructive: true));

        // 版本收起的存档抽屉：用户档被暂时从 Saves 挪进来（避免低版本点错闪退）。
        // 绝不可清理 —— 那是玩家的真存档，只是当前版本读不了才收起来的。
        // 占用还要算上人工隔离区里的存档批次：09-22 那次去重把 16 份 / 438 MB 挪进 _quarantine，
        // 这一行看不见它，玩家视角就是"存档凭空少了 438 MB"。
        var savesHidden = StoragePaths.SavesHiddenDir;
        var qzBatches = SaveVersionService.QuarantineSaveBatches();
        list.Add(new("saveshidden", "版本收起的存档", "当前游戏版本读不了、暂时从存档列表收起的档（含人工隔离批次）", savesHidden,
            new[] { savesHidden }.Concat(qzBatches).ToArray(), Array.Empty<string>(), Cleanable: false, Movable: false,
            Tip: "切到读不了它们的版本时 自动把那些档从存档列表收进这里 防止点错闪退 切回能读的版本会自动放回 千万不要手动删 这是你的存档本体 开着 Steam 云时收档会让云端在退出同步时跟着删掉这些文件 所以这里可能就是唯一一份",
            Destructive: true));

        return list;
    }

    /// <summary>把收起的存档放回 Saves：抽屉 + 人工隔离批次一起扫，走的正是切版本/启动时那条
    /// <see cref="SaveVersionService.RestoreHidden"/>，差集口径也一致（只放回当前版本读得了的）。
    /// 收进去的东西界面必须能拿回来 —— 这一行以前只有占用，没有任何出口。</summary>
    public async Task<string> RestoreHiddenSavesAsync()
    {
        var gamePath = _cfg.Current.GamePath;
        var n = await Task.Run(() => SaveVersionService.RestoreHidden(
            m => AppLog.Warn("Storage", m), UpdateService.ResolveCurrentVersion(gamePath)));
        RefreshAll(true);
        return n > 0 ? $"已放回 {n} 份存档" : "没有需要放回的存档（当前版本读得了的都在列表里）";
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

    /// <summary>整棵目录的字节数（<c>DepotDownloaderService</c> 跨盘收存本体前要用它算"装不装得下"）。</summary>
    internal static long DirSize(string root)
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
        if (c.Id == "selfupdate" && SelfUpdateService.CacheBusy)
            return Task.FromResult("自更新安装包正在下载，结束后再清理");
        return RunClean(c.Id, c.Name, c.CleanRoots);
    }

    /// <summary>depot-staging 里除版本包之外的附属目录。删的代价不同的东西**必须分行**：
    /// SMAPI 本体删了下次切版本要重装，Mod 暂存里是用户的东西（所以那条走回收站）。
    /// 人工隔离区（我排查跨版本污染时挪东西用的）不上界面。</summary>
    public sealed record StagingBucketKind(string Key, string[] Folders, string Name, string Tip, bool Danger);

    public static readonly StagingBucketKind[] StagingBucketKinds =
    {
        new("smapi", new[] { "_smapi-pool" }, "SMAPI 本体（切版本免重装）",
            "已经装好的 SMAPI 本体，按兼容档分三格：4.x 配 1.6+、3.x 配 1.4/1.5、2.x 配 1.3。切版本时直接拷进游戏目录，不用联网、不用重装。" +
            "删了不影响游戏能不能启动，只是下次切到需要它的版本时要重新装一遍 SMAPI（能连上网就会自动装回来）。",
            Danger: false),
        new("mods", new[] { "_mods-orphan" }, "没归到版本的 Mod",
            "正常情况下 mod 跟着版本走：每个版本的 mod 存在它自己的缓存抽屉里（就是上面那些版本号），切回去自动换回来。" +
            "这一行是没进抽屉的那部分 —— 源版本装不了 SMAPI（1.0 这类）被挪出来的、认不出属于哪个版本的、抽屉里上一批没被认领的。" +
            "带版本号的那几批，切回对应版本会自动放回；其余的不会，只能自己挪。" +
            "这里面可能是你的 mod 本体，所以「删除」其实是移到游戏卸载回收站，还能拿回来。",
            Danger: true),
    };

    /// <summary>界面上实际出现的附属目录（一个都不存在时不显示；大小已遍历一次目录树）。</summary>
    public static (StagingBucketKind Kind, string Path, long Bytes)[] ListStagingBuckets()
    {
        var root = StoragePaths.DepotStagingDir;
        if (!Directory.Exists(root)) return Array.Empty<(StagingBucketKind, string, long)>();
        var rows = new List<(StagingBucketKind, string, long)>();
        foreach (var k in StagingBucketKinds)
        {
            long sum = 0; string? first = null;
            foreach (var f in k.Folders)
            {
                var dir = Path.Combine(root, f);
                if (!Directory.Exists(dir)) continue;
                first ??= dir;
                sum += DirSize(dir);
            }
            if (first is not null) rows.Add((k, first, sum));
        }
        return rows.ToArray();
    }

    /// <summary>清理某个附属目录：可自愈的缓存直接删；里面是用户 mod 的那条**移到游戏卸载回收站**。</summary>
    public Task<string> CleanStagingBucketAsync(string key)
    {
        var k = StagingBucketKinds.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
        if (k is null) return Task.FromResult("这一项不在可清理范围内");
        var root = StoragePaths.DepotStagingDir;
        var dirs = k.Folders.Select(f => Path.Combine(root, f)).Where(Directory.Exists).ToArray();
        if (dirs.Length == 0) return Task.FromResult("这里已经很干净了");
        if (_center.RunningCount > 0) return Task.FromResult("有下载/安装任务进行中，结束后再清理");
        if (!k.Danger) return RunClean("vercache", k.Name, dirs);

        var gamePath = _cfg.Current.GamePath;
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
            return Task.FromResult("没定位到游戏目录，不知道往哪儿移 —— 先在设置里选好游戏位置");
        return Task.Run(() => MoveBucketsToTrash(gamePath, dirs, k.Name));
    }

    /// <summary>把暂存目录的每个批次整批挪进 <code>Mods\.junigrid_trash</code>（跟卸载 mod 同一条路，
    /// 扫描与列表都跳过它）。挪完批次目录空了就摘掉，界面那行随之消失。绝不硬删。</summary>
    private string MoveBucketsToTrash(string gamePath, string[] dirs, string title)
    {
        var task = _center.Start("移出：" + title, "cleanup");
        _center.Report(task, LocService.Tr("正在移进游戏卸载回收站…"), 5);
        try
        {
            var trash = StoragePaths.GameTrashDir(gamePath);
            Directory.CreateDirectory(trash);
            int moved = 0, skipped = 0; long bytes = 0;
            foreach (var dir in dirs)
            {
                List<string> batches;
                try { batches = Directory.GetDirectories(dir).ToList(); } catch { continue; }
                foreach (var batch in batches)
                {
                    var name = Path.GetFileName(batch);
                    var dst = Path.Combine(trash, name + "-" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                    var len = DirSize(batch);
                    if (TryMoveTree(batch, dst)) { moved++; bytes += len; }
                    else skipped++;
                    _center.Report(task, LocService.Tf("已移出 {0} 批（{1}）", moved, ResumableDownload.FormatBytes(bytes))
                        + (skipped > 0 ? $"，占用中跳过 {skipped} 批" : ""),
                        5 + 90.0 * (moved + skipped) / Math.Max(1, batches.Count + dirs.Length));
                }
                try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
            }
            var msg = moved == 0
                ? (skipped > 0 ? "都被占用，一个也没挪动" : "这里已经很干净了")
                : $"已移出 {moved} 批（{ResumableDownload.FormatBytes(bytes)}）到游戏卸载回收站"
                  + (skipped > 0 ? $"，占用中跳过 {skipped} 批" : "");
            _center.Finish(task, moved > 0, msg);
            AppLog.Warn("Storage", $"移出[{title}] moved={moved} skipped={skipped} bytes={bytes}");
            lock (_gate) _sizes["vercache"] = DirSizeSum(GetCategories()
                .FirstOrDefault(x => x.Id == "vercache")?.SizeRoots ?? dirs);
            OnStats?.Invoke();
            return msg;
        }
        catch (Exception ex)
        {
            _center.Finish(task, false, LocService.Tr("移出失败：") + ex.Message);
            return LocService.Tr("移出失败：") + ex.Message;
        }
    }

    private Task<string> RunClean(string sizeKey, string title, string[] roots)
    {
        var task = _center.Start("清理：" + title, "cleanup");
        _center.Report(task, LocService.Tr("开始清理…"), 3);

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
                        LocService.Tf("已清理 {0}（跳过占用中 {1} 个）", ResumableDownload.FormatBytes(freed), skipped),
                        3 + 92.0 * (i + 1) / Math.Max(1, files.Count));
            }
            if (pruneDirs)
                foreach (var root in roots) PruneEmptyDirs(root);

            var msg = files.Count == 0
                ? "这里已经很干净了"
                : $"清理完成：释放 {ResumableDownload.FormatBytes(freed)}" +
                  (skipped > 0 ? $"，跳过占用中的 {skipped} 个文件" : "");
            _center.Finish(task, true, msg);
            AppLog.Warn("Storage", $"清理[{sizeKey}:{title}] 释放 {freed} 字节，跳过 {skipped}");
            lock (_gate) _sizes[sizeKey] = DirSizeSum(GetCategories()
                .FirstOrDefault(x => x.Id == sizeKey)?.SizeRoots ?? roots);
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
    /// <summary>缓存根落点的三条硬拒绝（返回 null = 可以设）。
    /// <para>为什么要有这条：以前 <see cref="MigrateCacheRootAsync"/> 只 <c>CreateDirectory</c> 就接受，
    /// 用户把统一缓存目录设成安装目录或游戏目录时，<c>depot-staging</c>（游戏历史版本，一份几 GB）、
    /// <c>mods-backup</c>（SMAPI 更新前的 mod 快照）、<c>webview2</c>（登录态）会整个搬进去 ——
    /// 下次覆盖安装或走卸载器就连锅端。缓存根还必须是"专门放缓存的目录"，
    /// 否则清理按钮和卸载器都分不清哪份是它的。</para></summary>
    internal static string? CacheRootRejection(string? root, string installDir, string gameDir)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;      // 恢复默认位置，永远允许
        var full = Trim(Path.GetFullPath(root.Trim()));

        // ① 整个盘符根，或桌面/文档/下载这类用户自己的公共位置
        //    （Downloads 不在这版 SDK 的 SpecialFolder 枚举里，只能按 %USERPROFILE%\Downloads 拼）
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var drive = Path.GetPathRoot(full);
        if (drive is not null && Trim(drive).Length > 0 && Trim(drive).Equals(full, StringComparison.OrdinalIgnoreCase))
            return LocService.Tr("缓存目录不能是整个盘 —— 里面全是你自己的东西，请在盘里新建一个专门放缓存的子目录（比如 D:\\JuniGridCache）。");
        foreach (var known in new[]
        {
            profile,
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            string.IsNullOrWhiteSpace(profile) ? "" : Path.Combine(profile, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        })
        {
            if (string.IsNullOrWhiteSpace(known)) continue;
            if (Trim(Path.GetFullPath(known)).Equals(full, StringComparison.OrdinalIgnoreCase))
                return LocService.Tr("缓存目录不能设在桌面/文档/下载这类公共位置 —— 里面本来就有你自己的东西。请在里面新建一个专门放缓存的子目录（比如 D:\\JuniGridCache）。");
        }

        // ② 安装目录本身/它的子目录/它的上层：v1.2.5 起【放行】。
        //    原来的理由是「覆盖安装和卸载会连子树一起删」，这条理由现在不成立了 ——
        //    覆盖安装按 junigrid-files.txt 只回收我们写出去的文件，卸载没清单时也只做
        //    不带 /s 的 rd（目录里还剩东西就失败、整目录原样留着），两条都不再端子树。
        //    而「程序和数据住在一起」正是换目录安装要搬的那个形态：用户选了 D:\JuniGrid，
        //    他就以为版本缓存和登录态会跟着去 D:\JuniGrid，拦他反而违背直觉。
        //    （游戏目录那条仍要拦：切版本/卸载 SMAPI 的连坐风险在人家自己身上，见 ③。）

        // ③ 游戏目录同理：切版本/卸载 SMAPI 时最容易连累
        if (!string.IsNullOrWhiteSpace(gameDir) && (SameOrInside(full, gameDir) || SameOrInside(gameDir, full)))
            return LocService.Tr("缓存目录不能设在游戏目录里或它的上层 —— 那会把 mod 备份和游戏版本缓存堆进 "
                + "Stardew Valley 文件夹，切版本或卸载时一起没。请换一个专门放缓存的目录（比如 D:\\JuniGridCache）。");

        return null;
    }

    private static string Trim(string p) => p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>a 与 b 相同，或 b 在 a 这棵子树里。</summary>
    private static bool SameOrInside(string a, string b)
    {
        var x = Trim(Path.GetFullPath(a));
        var y = Trim(Path.GetFullPath(b));
        return x.Length > 0 && y.Length > 0
            && (y.Equals(x, StringComparison.OrdinalIgnoreCase)
                || y.StartsWith(x + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

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
        => ModService.CopyDirectoryContents(src, dst);

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
