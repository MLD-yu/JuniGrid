using System.IO;
using System.Text.Json;

namespace JuniGrid.Services;

/// <summary>
/// Persistent user config stored as JSON under %APPDATA%/JuniGrid/.
/// </summary>
public sealed class ConfigService
{
    private static readonly string ConfigDir = StoragePaths.AppDataDir;
    private static readonly string ConfigPath = Path.Combine(ConfigDir, "junigrid.config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // 必须：旧配置可能是 PascalCase。只开 CamelCase 时 Deserialize 会静默丢字段
        //（ModCovers/ModProfiles 变空 → 封面全没、合集消失），随后 Save 再把空配置写回盘。
        PropertyNameCaseInsensitive = true
    };

    public JuniGridConfig Current { get; private set; } = new();

    public ConfigService()
    {
        Load();
        // v0.72.6：进程退出前把防抖队列里未落盘的修改同步写盘 —— 防抖窗口内的最后修改不丢
        System.AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
    }

        private static string ConfigBackupPath => ConfigPath + ".bak";

        public void Load()
        {
            var restoredFromBak = false;
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var loaded = JsonSerializer.Deserialize<JuniGridConfig>(
                        File.ReadAllText(ConfigPath), JsonOpts);
                    if (loaded is not null)
                    {
                        Current = loaded;
                        // 只在「读进来的不是空壳」时覆盖 .bak —— 防止用清零后的配置顶掉好备份
                        if (Current.ModCovers.Count + Current.ModProfiles.Count + Current.ModRemarks.Count > 0
                            || Current.TotalLaunchCount > 0
                            || !string.IsNullOrEmpty(Current.GamePath))
                        {
                            try { File.Copy(ConfigPath, ConfigBackupPath, overwrite: true); } catch { }
                        }
                    }
                }
                else if (File.Exists(ConfigBackupPath))
                {
                    // 主文件没了：从 .bak 救（与 playtime 同款防呆）
                    var loaded = JsonSerializer.Deserialize<JuniGridConfig>(
                        File.ReadAllText(ConfigBackupPath), JsonOpts);
                    if (loaded is not null)
                    {
                        Current = loaded;
                        restoredFromBak = true;
                        AppLog.Warn("Config", "junigrid.config.json 缺失，已从 .bak 恢复");
                        try { AtomicFile.WriteAllText(ConfigPath, JsonSerializer.Serialize(Current, JsonOpts)); } catch { }
                    }
                }
            }
            catch
            {
                // v1.1.2：损坏现场先留档 —— 之后再试 .bak，最后才退回默认
                try
                {
                    if (File.Exists(ConfigPath))
                    {
                        var backup = ConfigPath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                        File.Copy(ConfigPath, backup, true);
                        AppLog.Error("Config", "配置文件解析失败，已备份为 " + Path.GetFileName(backup));
                    }
                }
                catch { }
                Current = new JuniGridConfig();
                try
                {
                    if (File.Exists(ConfigBackupPath))
                    {
                        var bak = JsonSerializer.Deserialize<JuniGridConfig>(
                            File.ReadAllText(ConfigBackupPath), JsonOpts);
                        if (bak is not null)
                        {
                            Current = bak;
                            restoredFromBak = true;
                            AppLog.Warn("Config", "已从 .bak 恢复配置（主文件损坏）");
                        }
                    }
                }
                catch { /* .bak 也不行才用默认 */ }
            }
            SyncAdultFilter();
            SyncStoragePaths();
            // v1.1.5：首次使用日期只补写一次（老用户从本次升级后开始起算）
            // 仅当「本来就没有配置」或「从备份恢复出的旧配置缺该字段」时补写；
            // 解析失败后的全新默认对象不许在这里立刻 Save 盖掉现场
            if (Current.FirstRunDate is null)
            {
                Current.FirstRunDate = DateTime.Now.ToString("O");
                if (!File.Exists(ConfigPath) || restoredFromBak || ConfigLooksIntact())
                    Save(Current);
            }
        }

        /// <summary>主配置存在且能解析 = 现场完好，允许补写 FirstRunDate。</summary>
        private static bool ConfigLooksIntact()
        {
            try
            {
                return File.Exists(ConfigPath)
                    && JsonSerializer.Deserialize<JuniGridConfig>(File.ReadAllText(ConfigPath), JsonOpts) is not null;
            }
            catch { return false; }
        }

    /// <summary>把「显示成人内容」单一开关同步到 NexusService 的静态查询开关
    /// （浏览 GraphQL 是否加 adult 过滤条件）。开关实际发生变化时递增 NexusService.AdultFilterVersion，
    /// Nexus 页据此判断手里的浏览快照是不是旧过滤条件拉的、要不要弃用重拉。</summary>
    private void SyncAdultFilter()
    {
        var include = Current.ShowAdultContent;
        if (NexusService.IncludeAdultContent != include)
            NexusService.BumpAdultFilterVersion();
        NexusService.IncludeAdultContent = include;
    }

        /// <summary>v0.2.1：把统一缓存目录同步到 StoragePaths 静态入口 —— 各服务取路径零改动即时生效。</summary>
        private void SyncStoragePaths()
        {
            StoragePaths.CacheRoot = string.IsNullOrWhiteSpace(Current.CacheRoot) ? null : Current.CacheRoot;
        }

    // v0.72.6：持久化协调器 —— Save() 不再每次全量写盘，改为 dirty 标记 + 250ms 防抖合并 +
    // 版本号快照 + 单写者后台落盘 + tmp 原子替换 + 异步重试。批量 63 个 mod 的 100+ 次
    // 保存请求合并为一次真实磁盘写入（"批量操作慢"的持久化侧根因）。
    // 保留 v0.72.5 的正确语义：串行写、tmp+原子替换、失败重试、不炸 UnobservedTaskException。
    private int _dirtyVersion;              // 每次 Save() +1
    private int _savedVersion;              // 已落盘的版本
    private int _saveRunning;               // 单写者闸门（0/1）
    private readonly object _schedGate = new();   // 只护调度状态，绝不包 I/O
    private System.Threading.CancellationTokenSource? _debounceCts;
    private const int DebounceMs = 250;

    /// <summary>统一保存入口：更新 Current + 打脏标记 + 调度合并写盘。立即返回，不阻塞调用线程。
    /// 现有全部调用点（含 4 处同步调用）无需改动 —— 最终持久化语义由防抖+退出 Flush 保证。</summary>
    /// <summary>v0.2.2：任意保存后触发的轻量通知（如 TaskDock 监听 ShowTaskDock 即时显隐）。
    /// 可能在后台线程触发，订阅方需自行调度回 UI 线程。</summary>
    public static event Action? Saved;

    public void Save(JuniGridConfig cfg)
    {
        Current = cfg;
        SyncAdultFilter();
        SyncStoragePaths();
        Saved?.Invoke();
        System.Threading.Interlocked.Increment(ref _dirtyVersion);
        lock (_schedGate)
        {
            // v1.1.6：旧 CTS 取消后立刻释放 —— 之前只 Cancel 不 Dispose，
            // 批量操作一次 100+ 次保存请求就抛弃 100+ 个 CTS 给 finalizer
            var old = _debounceCts;
            _debounceCts = new System.Threading.CancellationTokenSource();
            try { old?.Cancel(); old?.Dispose(); } catch { }
            var token = _debounceCts.Token;
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try { await System.Threading.Tasks.Task.Delay(DebounceMs, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }   // 被更新的一次保存请求合并掉
                try { await SaveLoopAsync().ConfigureAwait(false); }
                catch (Exception ex) { AppLog.Error("Config", "后台保存循环异常(已捕获,防 UnobservedTaskException): " + ex.Message); }
            });
        }
    }

    /// <summary>写盘循环：取版本快照 → 序列化（撞上并发修改则重取快照）→ tmp+原子替换 →
    /// 写完后若 dirty 版本已前进（保存期间有新修改）立即再写一轮 —— 绝不用旧快照覆盖新状态；
    /// 失败保持 dirty 稍后重试，不静默当成功。</summary>
    private async System.Threading.Tasks.Task SaveLoopAsync()
    {
        if (System.Threading.Interlocked.Exchange(ref _saveRunning, 1) == 1) return;  // 已有写盘循环在跑
        try
        {
            while (true)
            {
                var v = System.Threading.Volatile.Read(ref _dirtyVersion);
                if (v <= _savedVersion) return;

                var cfg = Current;
                string? json = null;
                for (var k = 0; k < 6; k++)
                {
                    try { json = JsonSerializer.Serialize(cfg, JsonOpts); break; }
                    catch (InvalidOperationException)
                    { await System.Threading.Tasks.Task.Delay(25).ConfigureAwait(false); }  // 序列化撞上并发改字典 → 重取快照
                }
                if (json is null)
                { AppLog.Error("Config", "配置序列化连续失败（并发修改过频），保持 dirty 等待下次保存"); return; }

                if (await WriteAtomicAsync(json).ConfigureAwait(false))
                    System.Threading.Volatile.Write(ref _savedVersion, v);
                else
                { await System.Threading.Tasks.Task.Delay(800).ConfigureAwait(false); continue; }  // 失败保 dirty，稍后重试
                // 回到循环顶部重查 dirtyVersion —— 写盘期间的新修改会触发下一轮
            }
        }
        finally { System.Threading.Volatile.Write(ref _saveRunning, 0); }
    }

    /// <summary>tmp + 原子替换 + 异步重试（不占锁、不卡 UI 线程）。
    /// v1.1.6：tmp 带随机后缀 —— 固定 ".tmp" 在后台写盘循环与退出 Flush 撞上同一窗口时
    /// 会互撞（一个把 tmp Move 走，另一个 WriteAllText/Move 抛异常）。</summary>
    private static async System.Threading.Tasks.Task<bool> WriteAtomicAsync(string json)
    {
        for (var attempt = 1; ; attempt++)
        {
            var tmp = $"{ConfigPath}.{Guid.NewGuid().ToString("N")[..8]}.tmp";
            try
            {
                Directory.CreateDirectory(ConfigDir);
                await File.WriteAllTextAsync(tmp, json).ConfigureAwait(false);
                File.Move(tmp, ConfigPath, true);   // 原子替换：写一半崩溃也不会截断旧配置
                return true;
            }
            catch (Exception ex) when (attempt < 4 && ex is IOException or UnauthorizedAccessException)
            { try { File.Delete(tmp); } catch { } await System.Threading.Tasks.Task.Delay(40 * attempt).ConfigureAwait(false); }
            catch (Exception ex)
            { try { File.Delete(tmp); } catch { } AppLog.Error("Config", $"配置保存失败(尝试 {attempt} 次): " + ex.Message); return false; }
        }
    }

    /// <summary>退出兜底（ProcessExit 调用）：取消防抖，若有未落盘修改则同步写盘。
    /// 保证应用退出时最后一次配置修改不丢。</summary>
    public void Flush()
    {
        try
        {
            lock (_schedGate) { _debounceCts?.Cancel(); }
            var v = System.Threading.Volatile.Read(ref _dirtyVersion);
            if (v <= _savedVersion) return;
            var cfg = Current;
            string? json = null;
            for (var k = 0; k < 6; k++)
            {
                try { json = JsonSerializer.Serialize(cfg, JsonOpts); break; }
                catch (InvalidOperationException) { System.Threading.Thread.Sleep(20); }
            }
            if (json is null) { AppLog.Error("Config", "退出 Flush 序列化失败"); return; }
            for (var attempt = 1; ; attempt++)
            {
                var tmp = $"{ConfigPath}.{Guid.NewGuid().ToString("N")[..8]}.tmp";
                try
                {
                    Directory.CreateDirectory(ConfigDir);
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, ConfigPath, true);
                    System.Threading.Volatile.Write(ref _savedVersion, v);
                    return;
                }
                catch (IOException) when (attempt < 4) { try { File.Delete(tmp); } catch { } System.Threading.Thread.Sleep(40 * attempt); }
                catch (Exception ex) { try { File.Delete(tmp); } catch { } AppLog.Error("Config", "退出 Flush 写盘失败: " + ex.Message); return; }
            }
        }
        catch (Exception ex) { AppLog.Error("Config", "Flush 异常: " + ex.Message); }
    }
}

/// <summary>v0.46.0：mod 存档（仿 Stardrop Profile）—— 记录该存档下启用哪些 mod（按 UniqueID）。
/// vNext：按游戏版本隔离 —— 切换版本会整包替换 Mods/，启用清单不能跨版本共用。</summary>
public sealed class ModProfile
{
    public string Name { get; set; } = "";
    public List<string> EnabledModUids { get; set; } = new();
    /// <summary>所属游戏版本（展示号，如 1.6.15 / 1.4 / 1.0）。空 = 旧配置，首次读取时迁到当前版本。</summary>
    public string GameVersion { get; set; } = "";
}

public sealed class JuniGridConfig
{
    public string GamePath { get; set; } = "";
    public string LaunchMode { get; set; } = "smapi";   // "smapi" | "steam"
    public string SteamAppId { get; set; } = "413150";
    public string NexusApiKey { get; set; } = "";

    // Launch history
    public string? LastLaunchTime { get; set; }          // ISO-8601
    public int TotalLaunchCount { get; set; }

    /// <summary>v1.1.5：首次使用 JuniGrid 的日期（ISO-8601，首次保存配置时补写一次）。
    /// 首页游玩热力图的年份列表从这里起算到今年。</summary>
    public string? FirstRunDate { get; set; }

    // Nexus 封面缓存：mod 文件夹名 → 封面图 URL（检查更新时顺手存，列表秒开）
    public Dictionary<string, string> ModCovers { get; set; } = new();

    /// <summary>用户给 mod 起的备注名：mod 文件夹名 → 备注（列表里显示成 “备注(原名)”）。</summary>
    /// <summary>v1.1.2：mod 文件夹 → 备注名。列表显示成「备注(原名)」，并同步进 mod 的
    /// manifest.json（游戏内 GMCM 标题读的就是它）。</summary>
    /// <summary>
    /// v1.6.8：启动器从 N 网装过的 MAIN 文件记录（UniqueID/Folder → fileId+文件版本）。
    /// 更新比对的权威口径是「N 网 MAIN 文件」而不是 manifest.Version ——
    /// 作者上传新文件却忘改包内 Version（Haley 恒 0.0.1、N 网文件 1）时，
    /// 只比 manifest 会永远报「可更新」，用户反复点更新都装不掉，误以为启动器坏了。
    /// 安装成功后记下 fileId/文件版本，并把该版本回写进本地 manifest，装完即粘住。
    /// </summary>
    public Dictionary<string, NexusInstallRecord> ModNexusInstalls { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> ModRemarks { get; set; } = new();
    /// <summary>v1.1.2：mod 文件夹 → 该 mod 清单里的原始 Name。备注同步进 manifest 前先存档，
    /// 取消备注时用它还原，避免原名丢失。</summary>
    public Dictionary<string, string> ModOriginalNames { get; set; } = new();

    /// <summary>v1.01.0：Nexus 页搜索历史（对照官网 Recent Searches，最多 10 条，新词排前）。</summary>
    public List<string> NexusSearchHistory { get; set; } = new();

    /// <summary>
    /// 「显示成人内容」开关。默认关闭 —— Nexus 浏览/搜索一律显式排除成人内容；
    /// 开启后应用不加任何成人条件：列表随登录用户 Nexus 账号上的成人内容设置执行（服务端强制），
    /// 应用永不覆盖账号偏好（Nexus AUP 要求）。无年龄验证（早期版本的出生年月验证已移除）。
    /// 老配置里的 FilterAdultContent/OnlyAdultContent 已废弃，反序列化时自然忽略。
    /// </summary>
    public bool ShowAdultContent { get; set; } = false;
    /// <summary>
    /// Nexus 一键安装（免弹浏览器、后台直接下载并装进 Mods）。默认开启；
    /// 关闭后详情页的「安装」按钮改为打开内置浏览器兜底。
    /// </summary>
    public bool EnableOneClickInstall { get; set; } = true;

    /// <summary>
    /// v1.2.4：锁定游戏版本 —— 把 Steam 的 appmanifest_413150.acf 设为只读，
    /// Steam 客户端（含 steam:// 启动前的强制更新）写不进版本清单，本体停留在当前版本，
    /// 想玩旧版 mod 的玩家不再被 Steam 强升。想更新游戏时关掉本开关即可恢复。
    /// 非 Steam 版（GOG 等）找不到清单文件，开关自动禁用。
    /// </summary>
    public bool LockGameVersion { get; set; } = false;

    /// <summary>
    /// v1.4：版本相关 Steam 账号名 —— 只记住 Steam 账号名方便下次回填；
    /// 密码与验证码每次输入、不落盘。
    /// </summary>
    public string SteamCmdAccount { get; set; } = "";

    /// <summary>v1.4.1：最近一次经 DepotDownloader 装上的历史 Manifest ID（空 = 当前走官方分支）。</summary>
    public string LastHistoricalManifest { get; set; } = "";

    /// <summary>v1.4.1：历史版本展示名（如 "1.5.4 · 1.5.4"），配合 LastHistoricalManifest 回填 UI。</summary>
    public string LastHistoricalLabel { get; set; } = "";

    /// <summary>
    /// v1.4.5：上一次应用的历史版本对应的"文件内部版本号"（星露谷 1.4 → 1.3.7269 这种）。
    /// 当前文件内部版本号与其一致时，UI 可放心用 LastHistoricalLabel 作为展示版本号。
    /// </summary>
    public string LastHistoricalInternalVersion { get; set; } = "";

    /// <summary>
    /// v1.5：DepotDownloader 扫码登录 —— 手机扫码确认过一次后置 true，
    /// 刷新令牌由 DepotDownloader 自行持久化在本机；之后下载走令牌免密。
    /// 令牌失效（约几个月/改密码）时由 UI 重置为 false 重新扫码。
    /// </summary>
    public bool DepotQrLoggedIn { get; set; } = false;

    /// <summary>
    /// v1.4.2：用户自定义历史版本条目 —— 从 SteamDB 复制的任意 Manifest ID。
    /// 与内置清单合并展示；可删除。按需下载，不预拉全量。
    /// </summary>
    public List<CustomHistoricalVersion> CustomHistoricalVersions { get; set; } = new();

    /// <summary>Nexus 登录后缓存的用户信息（来自 /v1/users/validate.json）。</summary>
    public string NexusUserName { get; set; } = "";
    public string NexusProfileUrl { get; set; } = "";
    public bool   NexusIsPremium { get; set; }

    /// <summary>v1.1.1：界面主题（"light" | "dark"）。标题栏开关切换并落盘；
    /// 启动时 TitleBar 用它对齐前端（localStorage 为防闪白的同步快路径）。
    /// v1.1.2：默认改为 dark（用户主用暗色观察界面）。</summary>
    public string Theme { get; set; } = "dark";

    /// <summary>v1.3.9：每季节独立皮肤 —— 角色 id → "spring:包␟summer:包␟fall:包␟winter:包"
    ///（缺季 = 该季用全局选择）。覆盖包按季钉变体资产。</summary>
    public Dictionary<string, string> PortraitSeasonSkins { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>v0.69.0：modId → 最后一次从该 mod 下载文件的日期（yyyy-MM-dd）。本地安装/更新时记录，并与 N 网下载历史合并。</summary>
    public Dictionary<string, string> ModLastDownload { get; set; } = new();
    /// <summary>v0.69.0：fileId → 该文件的下载日期（仅本机经系统内下载过的）。</summary>
    public Dictionary<string, string> ModFileLastDownload { get; set; } = new();

    /// <summary>v0.68.2：设置页「自动安装更新」开关（仅 Nexus Premium 会员可开启）。
    /// 开启后进入 Mod 页检测到更新不再弹询问窗，直接在系统内自动安装。</summary>
    public bool EnableAutoInstall { get; set; } = false;

    /// <summary>v0.2.2：任务管理悬浮窗常驻开关。开启常驻显示；关闭后仅在下载任务运行时显示。</summary>
    public bool ShowTaskDock { get; set; } = false;
    public string NexusAvatarDataUri { get; set; } = "";

    /// <summary>累计游玩时间（分钟）。LauncherService 在游戏进程退出时累加。</summary>
    public long TotalPlayMinutes { get; set; }

    /// <summary>v0.46.0：mod 存档列表（"默认" 为内置存档，不可删除）。按 GameVersion 分组使用。</summary>
    public List<ModProfile> ModProfiles { get; set; } = new();
    /// <summary>当前激活的存档名（旧字段，仅作迁移兜底）。</summary>
    public string ActiveProfile { get; set; } = "默认";
    /// <summary>各游戏版本的当前存档名（展示号 → 存档名）。切版本后互不干扰。</summary>
    public Dictionary<string, string> ActiveProfileByGame { get; set; } = new();

    /// <summary>Nexus 官方分类表（category_id → 英文名），运行时带 API Key 拉取一次并缓存。</summary>
    public Dictionary<int, string> NexusCategories { get; set; } = new();
    /// <summary>mod 文件夹 → 官网分类英文名（检查更新/补封面时顺手缓存，与 ModCovers 同生命周期）。</summary>
    public Dictionary<string, string> ModCategories { get; set; } = new();

    /// <summary>vNext：更新检查指纹缓存 —— Nexus modId → (updatedAt 指纹, 上次精查到的最新 MAIN 文件版本, 精查时间)。
    /// 进 Mod 页先跑一次免 key 的 GraphQL 批量指纹比对：updatedAt 没变的 mod 直接复用缓存版本号
    /// （文件列表没变，结果不会过期），只有指纹变化/缓存缺失的才逐个 files.json 精查并回写本缓存。
    /// 持久化到配置里，重启应用后依然命中 —— 常规进页的检查从 N 个请求塌缩到 ~N/50 个。
    /// </summary>
    public Dictionary<int, ModUpdateFingerprintEntry> ModUpdateFingerprints { get; set; } = new();

    /// <summary>v1.2.0：一键装依赖的解析缓存 —— SMAPI UniqueID → Nexus modId。
    /// 搜索命中并经 manifest UniqueID 校验装成功后记录，下次一键装依赖直接命中，
    /// 不再重复搜索。校验仍在每次下载后执行，缓存错了也装不进 Mods。</summary>
    public Dictionary<string, int> DependencyNexusIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>v1.2.4：缺失依赖弹窗的展示缓存 —— UniqueID → (modId, 名称, 封面 URL)。
    /// 弹窗打开时先查此缓存同步出结果（封面本体由 WebView2 直连远程 URL），
    /// 未命中才做免 key 搜索并回写。只用于展示 —— 安装仍以下载后的 UniqueID 校验为准。</summary>
    public Dictionary<string, DependencyDisplayEntry> DependencyDisplays { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>v1.3.0 立绘页：每个角色当前生效的皮肤 —— 角色 id → 包 Folder（相对 Mods/）。
    /// v2 规格：只有这一个字典（选中某皮肤 = 大头照+精灵图一起切换）；键为角色 id 或 "Horse"。</summary>
    public Dictionary<string, string> PortraitSkins { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>v1.4 覆盖包机制：用户显式点了「默认」的角色 id。覆盖包会把这些角色的
    /// 原版立绘拷进去以最高优先级 Load，压过其它启用包的同名补丁；不在名单里的角色
    /// = 从未碰过，游戏里按 mod 自己的默认走。</summary>
    public List<string> PortraitVanillaDefaults { get; set; } = new();

    /// <summary>
    /// v1.7 肖像锁定：角色 id → 锁定条目（JSON 字符串，见 PortraitLockInfo）。
    /// 锁定后 UI 整卡禁用，游戏内该 NPC 四季一律显示锁定时选中的那一张图
    /// （不再跟季节变体轮换）。合并卡按成员 id 各写一条。
    /// </summary>
    public Dictionary<string, string> PortraitLocks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// v1.7「一键恢复默认」：强制走【原版 xnb】的角色 id（冈瑟/马龙/法师这类
    /// 原版有默认、SVE 也重绘了默认的，统一回原版而不是扩展包默认像）。
    /// 用户之后再手动选皮肤时由 SelectSkin 移出本名单。
    /// </summary>
    public List<string> PortraitTrueVanilla { get; set; } = new();

    /// <summary>首页统计缓存：上次探测到的游戏版本 / SMAPI / Mod 数。
    /// 冷启动先画缓存再后台对齐，避免「0 / 未安装 / 空白」假象。</summary>
    public string CachedGameVersion { get; set; } = "";
    public string CachedSmapiVersion { get; set; } = "";
    public int CachedModCount { get; set; }
    public int CachedDisabledModCount { get; set; }

    /// <summary>v0.2.1：统一缓存目录（null = 各类缓存走历史默认位置）。
    /// 设置后下载/安装临时、SMAPI 安装包、WebView2 数据、Mods 备份都迁到该目录下的子目录。</summary>
    public string? CacheRoot { get; set; }

    /// <summary>v0.2.2：WebView2 数据目录迁移遗留标记 —— 更改缓存目录时 WebView2 正被占用无法立即搬，
    /// 记下旧位置，下次启动（WebView2 初始化之前）自动搬迁后清空。</summary>
    public string? PendingWebView2MoveFrom { get; set; }

    /// <summary>v0.2.1：内存管理 —— 定时自动压缩开关与间隔（分钟）。</summary>
    public bool MemTimerEnabled { get; set; } = false;
    public int MemTimerMinutes { get; set; } = 30;

    /// <summary>v0.2.1：内存管理 —— 系统内存占用达到阈值(%)时自动压缩。</summary>
    public bool MemThresholdEnabled { get; set; } = false;
    public int MemThresholdPercent { get; set; } = 80;

    /// <summary>v1.1.2：全局自动翻译开关。开启后任何页面出现的外文内容实时翻成中文；
    /// 日志行首 [时间 级别 来源] 前缀永远保留原文 —— 分类筛选/着色都靠它。
    /// 默认关闭（国内网络到翻译引擎不稳定，避免默认体验时好时坏）。</summary>
    public bool TranslationEnabled { get; set; } = false;


}

/// <summary>v1.4.2：用户从 SteamDB 手动收录的历史版本。</summary>
public sealed class CustomHistoricalVersion
{
    public string Label { get; set; } = "";
    public string ManifestId { get; set; } = "";
    public string? Version { get; set; }
    public string? Note { get; set; }
}

/// <summary>vNext：单条更新检查指纹。UpdatedAt 与 GraphQL 批量结果逐字比对；
/// LatestFileVersion 只会写「files.json 精查成功」的结果（与安装源同一权威口径）；
/// CheckedAtUtc 给缓存兜底有效期（24h，防 updatedAt 假设之外的极端情况长期滞留）。</summary>
public sealed class ModUpdateFingerprintEntry
{
    public string UpdatedAt { get; set; } = "";
    public string LatestFileVersion { get; set; } = "";
    public DateTime CheckedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>v1.2.4：缺失依赖弹窗的单条展示信息（免 key 搜索的首个命中，仅展示用）。</summary>
public sealed class DependencyDisplayEntry
{
    public string Name { get; set; } = "";
    public int ModId { get; set; }
    public string CoverUrl { get; set; } = "";
}

/// <summary>启动器从 Nexus 安装过的 MAIN 文件快照（更新粘住判定用）。</summary>
public sealed class NexusInstallRecord
{
    public int NexusModId { get; set; }
    public long FileId { get; set; }
    /// <summary>N 网 MAIN 文件上的版本号（可能与包内 manifest.Version 不一致）。</summary>
    public string RemoteVersion { get; set; } = "";
    public DateTime InstalledAtUtc { get; set; } = DateTime.UtcNow;
}
