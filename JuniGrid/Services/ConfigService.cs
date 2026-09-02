using System.IO;
using System.Text.Json;

namespace JuniGrid.Services;

/// <summary>
/// Persistent user config stored as JSON under %APPDATA%/JuniGrid/.
/// </summary>
public sealed class ConfigService
{
    private static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JuniGrid");
    private static readonly string ConfigPath = Path.Combine(ConfigDir, "junigrid.config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public JuniGridConfig Current { get; private set; } = new();

    public ConfigService()
    {
        Load();
        // v0.72.6：进程退出前把防抖队列里未落盘的修改同步写盘 —— 防抖窗口内的最后修改不丢
        System.AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
    }

        public void Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var loaded = JsonSerializer.Deserialize<JuniGridConfig>(
                        File.ReadAllText(ConfigPath), JsonOpts);
                    if (loaded is not null) Current = loaded;
                }
            }
            catch
            {
                Current = new JuniGridConfig();
            }
            SyncAdultFilter();
            SyncStoragePaths();
        }

    /// <summary>把「过滤成人内容 / 只显示成人内容」两个互斥开关同步到 NexusService 的静态查询开关
    /// （浏览 GraphQL 是否加 adult 过滤条件）。</summary>
    private void SyncAdultFilter()
    {
        NexusService.OnlyAdultContent = Current.OnlyAdultContent;
        NexusService.IncludeAdultContent = !Current.OnlyAdultContent && !Current.FilterAdultContent;
    }

        /// <summary>v0.2.1：把统一缓存目录同步到 StoragePaths 静态入口 —— 各服务取路径零改动即时生效。</summary>
        private void SyncStoragePaths() =>
            StoragePaths.CacheRoot = string.IsNullOrWhiteSpace(Current.CacheRoot) ? null : Current.CacheRoot;

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
            _debounceCts?.Cancel();
            _debounceCts = new System.Threading.CancellationTokenSource();
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

    /// <summary>tmp + 原子替换 + 异步重试（不占锁、不卡 UI 线程）。</summary>
    private static async System.Threading.Tasks.Task<bool> WriteAtomicAsync(string json)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                var tmp = ConfigPath + ".tmp";
                await File.WriteAllTextAsync(tmp, json).ConfigureAwait(false);
                File.Move(tmp, ConfigPath, true);   // 原子替换：写一半崩溃也不会截断旧配置
                return true;
            }
            catch (IOException) when (attempt < 4)
            { await System.Threading.Tasks.Task.Delay(40 * attempt).ConfigureAwait(false); }
            catch (Exception ex)
            { AppLog.Error("Config", $"配置保存失败(尝试 {attempt} 次): " + ex.Message); return false; }
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
                try
                {
                    Directory.CreateDirectory(ConfigDir);
                    var tmp = ConfigPath + ".tmp";
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, ConfigPath, true);
                    System.Threading.Volatile.Write(ref _savedVersion, v);
                    return;
                }
                catch (IOException) when (attempt < 4) { System.Threading.Thread.Sleep(40 * attempt); }
                catch (Exception ex) { AppLog.Error("Config", "退出 Flush 写盘失败: " + ex.Message); return; }
            }
        }
        catch (Exception ex) { AppLog.Error("Config", "Flush 异常: " + ex.Message); }
    }
}

/// <summary>v0.46.0：mod 存档（仿 Stardrop Profile）—— 记录该存档下启用哪些 mod（按 UniqueID）。</summary>
public sealed class ModProfile
{
    public string Name { get; set; } = "";
    public List<string> EnabledModUids { get; set; } = new();
}

public sealed class JuniGridConfig
{
    public string GamePath { get; set; } = "";
    public string LaunchMode { get; set; } = "smapi";   // "smapi" | "steam"
    public string SteamAppId { get; set; } = "413150";
    public string ActiveShaderPreset { get; set; } = "balanced";
    public string NexusApiKey { get; set; } = "";

    // Launch history
    public string? LastLaunchTime { get; set; }          // ISO-8601
    public string? LastLaunchMode { get; set; }
    public int TotalLaunchCount { get; set; }

    // Nexus 封面缓存：mod 文件夹名 → 封面图 URL（检查更新时顺手存，列表秒开）
    public Dictionary<string, string> ModCovers { get; set; } = new();

    /// <summary>用户给 mod 起的备注名：mod 文件夹名 → 备注（列表里显示成 “备注(原名)”）。</summary>
    public Dictionary<string, string> ModRemarks { get; set; } = new();

    /// <summary>v1.01.0：Nexus 页搜索历史（对照官网 Recent Searches，最多 10 条，新词排前）。</summary>
    public List<string> NexusSearchHistory { get; set; } = new();

    /// <summary>
    /// 过滤色情（成人）内容开关。默认开启 —— Nexus 浏览/搜索一律排除成人内容；
    /// 关闭时设置页要求输入出生年月日验证年满 18 周岁（仅本地校验，不联网比对）。
    /// </summary>
    public bool FilterAdultContent { get; set; } = true;
    /// <summary>「只显示成人内容」开关，与 FilterAdultContent 互斥（两者最多一个开启，可同时关闭）。默认关闭。</summary>
    public bool OnlyAdultContent { get; set; } = false;
    /// <summary>
    /// Nexus 一键安装（免弹浏览器、后台直接下载并装进 Mods）。默认开启；
    /// 关闭后详情页的「安装」按钮改为打开内置浏览器兜底。
    /// </summary>
    public bool EnableOneClickInstall { get; set; } = true;

    /// <summary>Nexus 登录后缓存的用户信息（来自 /v1/users/validate.json）。</summary>
    public string NexusUserName { get; set; } = "";
    public string NexusUserEmail { get; set; } = "";
    public string NexusProfileUrl { get; set; } = "";
    public bool   NexusIsPremium { get; set; }
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

    /// <summary>v0.46.0：mod 存档列表（"默认" 为内置存档，不可删除）。</summary>
    public List<ModProfile> ModProfiles { get; set; } = new();
    /// <summary>当前激活的存档名。</summary>
    public string ActiveProfile { get; set; } = "默认";

    /// <summary>Nexus 官方分类表（category_id → 英文名），运行时带 API Key 拉取一次并缓存。</summary>
    public Dictionary<int, string> NexusCategories { get; set; } = new();
    /// <summary>mod 文件夹 → 官网分类英文名（检查更新/补封面时顺手缓存，与 ModCovers 同生命周期）。</summary>
    public Dictionary<string, string> ModCategories { get; set; } = new();

    /// <summary>
    /// vNext：更新检查指纹缓存 —— Nexus modId → (updatedAt 指纹, 上次精查到的最新 MAIN 文件版本, 精查时间)。
    /// 进 Mod 页先跑一次免 key 的 GraphQL 批量指纹比对：updatedAt 没变的 mod 直接复用缓存版本号
    /// （文件列表没变，结果不会过期），只有指纹变化/缓存缺失的才逐个 files.json 精查并回写本缓存。
    /// 持久化到配置里，重启应用后依然命中 —— 常规进页的检查从 N 个请求塌缩到 ~N/50 个。
    /// </summary>
    public Dictionary<int, ModUpdateFingerprintEntry> ModUpdateFingerprints { get; set; } = new();

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
