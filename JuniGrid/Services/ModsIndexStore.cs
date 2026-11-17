using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace JuniGrid.Services;

/// <summary>
/// 一个游戏版本那一份「mod 索引」：分类归属、N 网安装账、备注、封面/官网分类缓存。
/// <para>为什么要按版本分开：这些表的键都是 <b>Mods 里的文件夹名</b>，而 <c>Mods\</c> 是按版本
/// 整包隔离的（切版本时整棵树换掉）。同一个 <c>ZoomLevel</c> 在 1.6.14 和 1.6.15 里可能是不同版本、
/// 甚至不同 mod 的同名文件夹 —— 记在一本全局账上，两个版本就会互相覆盖：
/// 切走之后这一版看着全是「未分类」，切回来又把这版新做的分类顶掉。</para>
/// <para>所以这些表跟着版本走，一版一本账。<b>分类词汇本身也在这本账里</b>（
/// <see cref="ModsIndex.CategoryOrder"/> 顺序、<see cref="ModsIndex.CategoryIcons"/> 图标）——
/// 原先只分归属、词汇留全局，结果是「切过去一排 0 个的空分类」（v1.6.13 改正）。</para>
/// <para><b>刻意不收进来的两张表：</b><c>ModNexusInstalls</c>（键是 UniqueID 与文件夹混用）和
/// <c>ModInstalledFile</c>（键是 N 网 modId）—— 它们的键天然跨版本不变。放进按版本的账里，
/// 切过去就查不到「这一版已经装过」，列表会把「更新」画成「安装」、点一下重下一遍。
/// 判据是<b>键的性质</b>：键是文件夹名（随树换就失效）的进来，键是 mod 身份的留在全局。</para>
/// </summary>
internal sealed class ModsIndex
{
    /// <summary>这份账属于哪个游戏版本（如 <c>1.6.15</c>）。</summary>
    public string Version { get; set; } = "";
    /// <summary>上次写盘时间，纯排障用。</summary>
    public string SavedAt { get; set; } = "";

    /// <summary>文件夹（顶层目录或独立 mod）→ 自定义分类名。</summary>
    public Dictionary<string, string> Categories { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>这一版分类词汇表的显示顺序（末尾 = 屏上最上；<c>""</c> 是「未分类」的图标槽，
    /// 拖动/排序把它也当一个普通成员）。
    /// <para><b>为什么词汇也按版本记</b>（v1.6.13，2026-11-16 用户反馈）：原先只有归属按版本分，
    /// 名字顺序与图标留在全局 —— 于是在 1.6.15 里建了 7 个测试分类，切到 1.5.6 后那一版一个成员
    /// 都没有，屏幕上照样立着 7 排「0 个」的空段头（空分类必须画段头才能接拖拽，v1.3.78）。
    /// 名字是<b>用户给某一版那几个 mod 起的名字</b>，跟归属一样该跟着树走。</para></summary>
    public List<string>? CategoryOrder { get; set; }
    /// <summary>分类名 → 段头图标，与 <see cref="CategoryOrder"/> 同一本账。
    /// <c>null</c>（整个属性缺席）= 词汇还没分账时写的旧账本，挂载时按归属播种一次。</summary>
    public Dictionary<string, string>? CategoryIcons { get; set; }
    public Dictionary<string, string> OfficialCategoryNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>文件夹 → 用户备注名。</summary>
    public Dictionary<string, string> Remarks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>文件夹 → 改备注前的原清单 Name。</summary>
    public Dictionary<string, string> OriginalNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>文件夹 → 封面 URL（纯缓存，跟着版本分开只是为了让列表不串图）。</summary>
    public Dictionary<string, string> Covers { get; set; } = new();
    /// <summary>文件夹 → N 网官方分类名（缓存）。</summary>
    public Dictionary<string, string> NexusCategories { get; set; } = new();
    /// <summary>查过官网但没拿到分类的文件夹（缓存，免得每进一次列表就重查）。</summary>
    public List<string> CategoryTried { get; set; } = new();

    public int Total => Categories.Count + Remarks.Count + OriginalNames.Count
        + Covers.Count + NexusCategories.Count + CategoryTried.Count;
}

/// <summary>
/// 按游戏版本分账的 mod 索引存储（v1.6.2）。
/// <para><b>存在哪儿、为什么：</b>权威副本在 <c>%APPDATA%\JuniGrid\versions\&lt;版本&gt;\mods-index.json</c>
/// （测试舱里跟着 <c>JUNIGRID_CONFIG_DIR</c> 走）。<b>没有</b>直接住进 <c>depot-staging\&lt;版本&gt;\</c>：
/// 那是下载缓存，存储清理页和用户的「清缓存」会整目录删掉 —— 用户手建的分类不该跟着缓存一起没。
/// 但版本文件夹确实该带上一份：切走时那棵树会被移进版本包，账本跟着走才不会人树两分，
/// 所以每次落盘**也**往版本文件夹里写一份 <c>.junigrid\mods-index.json</c>（读的时候先读 app data，
/// 没有才回退到这份副本）。</para>
/// <para><b>怎么挂载：</b>进程里只有「当前版本」这一份账是活的 —— 启动时 <see cref="Attach"/> 把它
/// 灌进 <see cref="JuniGridConfig"/> 那几个字典，切版本后 <see cref="SyncIfVersionChanged"/> 先把旧版本
/// 存下来再换成新版本的。这样所有读写这些字典的既有代码（分类弹窗、安装、更新比对、封面缓存）
/// 一行都不用改，语义自动就变成「按版本」了。</para>
/// </summary>
internal static class ModsIndexStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        // 中文不转 \uXXXX：这份文件是给人看/排障的（分类名、备注都是中文），
        // 转义之后 grep 一个分类名都搜不出来。本地文件，没有 HTML 注入面。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>版本文件夹里的副本名（放在点开头的子目录里：SMAPI 只扫 Mods，看不见它）。</summary>
    private const string MirrorRel = ".junigrid/mods-index.json";

    /// <summary>按版本分目录的根。</summary>
    public static string Root => Path.Combine(ConfigService.ConfigDir, "versions");

    /// <summary>当前挂在 <see cref="JuniGridConfig"/> 上的版本；null = 认不出版本，
    /// 本模块整体不动作（行为与分账之前完全一致）。</summary>
    public static string? ActiveVersion { get; private set; }

    private static bool _mounted;
    private static string? _lastJson;                 // 上次写出去的内容 —— 一样就不重复写
    private static List<string> _mirrorDirs = new();  // 这个版本的「版本文件夹」

    // 分账那一刻的全局词汇快照。**必须在第一次 Mount 之前抓**：Mount 一次之后，配置里
    // ModUserCategoryOrder / ModCategoryIcon 就变成「当前版本」的镜像了，再拿它当播种源
    // 会把别的版本的名字和图标带丢（1.5.6 上先挂一次，1.6.15 的头像图标就没了）。
    private static List<string> _legacyOrder = new();
    private static Dictionary<string, string> _legacyIcons = new(StringComparer.OrdinalIgnoreCase);
    private static bool _legacyCaptured;

    public static string FileFor(string version) => Path.Combine(Root, SafeName(version), "mods-index.json");

    // ── 挂载 ─────────────────────────────────────────────────────────

    /// <summary>只给测试舱：清掉挂载状态，让下一个用例从零挂一次。</summary>
    internal static void Detach()
    {
        ActiveVersion = null;
        _mounted = false;
        _lastJson = null;
        _mirrorDirs = new();
        _legacyOrder = new();
        _legacyIcons = new(StringComparer.OrdinalIgnoreCase);
        _legacyCaptured = false;
    }

    /// <summary>启动时挂载当前版本的账本。幂等，失败不影响配置加载。</summary>
    internal static void Attach(JuniGridConfig cfg)
    {
        try
        {
            CaptureLegacyVocabulary(cfg);
            // 升级路径：盘上可能有好几本「词汇还没分账」的旧账本，**必须趁这份全局词汇
            // 还在手里时一次补齐**。只补当前挂载那本是不够的 —— 挂上去之后配置里那两个
            // 字段就变成当前版本的镜像了，等用户切到别的版本再补，拿到的是空名单
            // （2026-11-16 实机：1.5.6 先挂一次，1.6.15 的 Krobus/Emily 头像就没了）。
            var seededSnaps = SeedExistingLedgers(cfg);
            Mount(cfg, DetectVersion(cfg, null), seededSnaps);
        }
        catch (Exception ex) { AppLog.Warn("ModsIndex", "挂载 mod 索引失败（沿用全局旧账）：" + ex.Message); }
    }

    /// <summary>把 <see cref="Root"/> 下所有「旧格式」（没有 <c>categoryOrder</c>）的账本按归属补一次词汇，
    /// 顺手刷新版本文件夹里的副本。返回算好的快照表（没有要补的就返回 null，让 Mount 自己算）。</summary>
    private static IReadOnlyList<(string Version, string ModsRoot)>? SeedExistingLedgers(JuniGridConfig cfg)
    {
        try
        {
            if (!Directory.Exists(Root)) return null;
            var pending = new List<(string Ver, ModsIndex Idx, string Path)>();
            foreach (var dir in Directory.EnumerateDirectories(Root))
            {
                var path = Path.Combine(dir, "mods-index.json");
                var idx = ReadFile(path);
                if (idx is null || idx.CategoryOrder is not null) continue;
                SeedVocabulary(idx, _legacyOrder, _legacyIcons);
                idx.SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                var ver = idx.Version is { Length: > 0 } v ? v : Path.GetFileName(dir);
                pending.Add((ver, idx, path));
            }
            if (pending.Count == 0) return null;

            var snaps = ModService.VersionSnapshots(cfg.GamePath);
            foreach (var (ver, idx, path) in pending)
            {
                try
                {
                    var json = JsonSerializer.Serialize(idx, JsonOpts);
                    AtomicFile.WriteAllText(path, json);
                    foreach (var d in MirrorDirs(ver, snaps, cfg.GamePath)) WriteMirror(d, json);
                    AppLog.Info("ModsIndex",
                        $"{ver} 的账本是旧格式，启动时按归属补齐分类词汇：{idx.CategoryOrder?.Count ?? 0} 个名字");
                }
                catch (Exception ex) { AppLog.Warn("ModsIndex", $"补齐 {ver} 的分类词汇失败：{ex.Message}"); }
            }
            return snaps;
        }
        catch (Exception ex)
        {
            AppLog.Warn("ModsIndex", "启动时补分类词汇失败（留给挂载那一步）：" + ex.Message);
            return null;
        }
    }

    private static void CaptureLegacyVocabulary(JuniGridConfig cfg)
    {
        if (_legacyCaptured) return;
        try { _legacyOrder = new List<string>(cfg.ModUserCategoryOrder); }
        catch (InvalidOperationException) { _legacyOrder = new(); }
        try { _legacyIcons = new Dictionary<string, string>(cfg.ModCategoryIcon, StringComparer.OrdinalIgnoreCase); }
        catch (InvalidOperationException) { _legacyIcons = new(StringComparer.OrdinalIgnoreCase); }
        _legacyCaptured = true;
    }

    /// <summary>版本变了就换一本账：先把旧的存下来，再挂新的。返回是否换过。
    /// <para>调用点：切版本成功后、Mods 页重扫时（Steam 在外头更新了本体也会被这条接住）。</para></summary>
    internal static bool SyncIfVersionChanged(JuniGridConfig cfg)
        => SyncIfVersionChanged(cfg, null, null);

    internal static bool SyncIfVersionChanged(JuniGridConfig cfg, string? versionOverride,
        IReadOnlyList<(string Version, string ModsRoot)>? snapshots)
    {
        var ver = DetectVersion(cfg, versionOverride);
        if (ver.Length == 0) return false;
        if (_mounted && string.Equals(ActiveVersion, ver, StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            Flush(cfg);   // 旧版本先落盘（没挂载过就是空操作）
            Mount(cfg, ver, snapshots);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("ModsIndex", $"切到 {ver} 的 mod 索引失败：{ex.Message}");
            return false;
        }
    }

    private static string DetectVersion(JuniGridConfig cfg, string? versionOverride)
        => ModService.NormalizeGameVersion(
            versionOverride ?? UpdateService.ReadLocalGameVersion(cfg.GamePath));

    private static void Mount(JuniGridConfig cfg, string ver,
        IReadOnlyList<(string Version, string ModsRoot)>? snapshots)
    {
        if (ver.Length == 0)
        {
            // 认不出版本（没装游戏 / 路径没配）：不动数据。宁可让旧账继续全局，
            // 也不许在这里清空 —— 清空了等于把用户的分类一次性抹掉。
            ActiveVersion = null;
            _mounted = false;
            return;
        }
        if (_mounted && string.Equals(ActiveVersion, ver, StringComparison.OrdinalIgnoreCase)) return;

        CaptureLegacyVocabulary(cfg);
        var snaps = snapshots ?? ModService.VersionSnapshots(cfg.GamePath);
        var idx = TryLoad(ver, snaps, cfg.GamePath);
        var migrated = false;
        if (idx is null)
        {
            // 这个版本还没有自己的账本 ⇒ 配置里那份是「分账前」的全局账，按快照拆一次。
            // 只在这一步拆：拆完之后这个版本就有账本了，以后不再重拆（重拆会在
            // 「用户删了某个 mod、而别版本的快照里恰好也有它」时把账挪来挪去）。
            idx = MigrateLegacy(cfg, ver, snaps);
            migrated = true;
        }
        else if (idx.CategoryOrder is null)
        {
            // 旧格式账本（归属早就分账了、词汇还留在全局那份）：按这一版真正用到的名字播种一次。
            // 别的版本起的名字不属于这一版 —— 留着就是切过来一排「0 个」的空段头。
            SeedVocabulary(idx, _legacyOrder, _legacyIcons);
            migrated = true;      // 借这个旗子让下面立刻落盘（否则播种源下次启动已被换成这一版的）
            AppLog.Info("ModsIndex", $"{ver} 的账本是旧格式，分类词汇按归属播种：{idx.CategoryOrder.Count} 个名字");
        }
        Apply(cfg, idx);
        ActiveVersion = ver;
        _mounted = true;
        _mirrorDirs = MirrorDirs(ver, snaps, cfg.GamePath);
        _lastJson = null;
        // 发生过拆分/播种就立刻写一遍：`mine` 这份以前要等下一次存盘才落盘，而词汇的播种源
        // （配置里那份）在 Apply 之后就已经换成这一版的了 —— 不留盘，重启后播种会拿到空名单。
        if (migrated) Flush(cfg);
        AppLog.Info("ModsIndex",
            $"已挂载 {ver} 的 mod 索引{(migrated ? "（旧账按版本拆分后）" : "")}：" +
            $"分类 {idx.Categories.Count} / 词汇 {idx.CategoryOrder?.Count ?? 0} / " +
            $"备注 {idx.Remarks.Count} / 缓存 {idx.Covers.Count}+{idx.NexusCategories.Count}");
    }

    // ── 落盘 ─────────────────────────────────────────────────────────

    /// <summary>把当前内存里的账写到 <see cref="ActiveVersion"/> 名下。没挂载过就什么都不做。</summary>
    internal static void Flush(JuniGridConfig cfg)
    {
        if (ActiveVersion is not { Length: > 0 } ver) return;
        string json;
        try { json = JsonSerializer.Serialize(Capture(cfg, ver), JsonOpts); }
        catch (InvalidOperationException) { return; }   // 撞上并发改字典：这次不写，下次存盘还会来
        catch (Exception ex) { AppLog.Warn("ModsIndex", $"序列化 {ver} 的 mod 索引失败：{ex.Message}"); return; }
        if (json == _lastJson) return;
        try
        {
            AtomicFile.WriteAllText(FileFor(ver), json);
            foreach (var dir in _mirrorDirs) WriteMirror(dir, json);
            _lastJson = json;
        }
        catch (Exception ex) { AppLog.Warn("ModsIndex", $"写 {ver} 的 mod 索引失败：{ex.Message}"); }
    }

    private static ModsIndex Capture(JuniGridConfig cfg, string ver) => new()
    {
        Version = ver,
        SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        Categories = new Dictionary<string, string>(cfg.ModUserCategory, StringComparer.OrdinalIgnoreCase),
        Remarks = new Dictionary<string, string>(cfg.ModRemarks, StringComparer.OrdinalIgnoreCase),
        OriginalNames = new Dictionary<string, string>(cfg.ModOriginalNames, StringComparer.OrdinalIgnoreCase),
        Covers = new Dictionary<string, string>(cfg.ModCovers),
        NexusCategories = new Dictionary<string, string>(cfg.ModCategories),
        CategoryTried = new List<string>(cfg.ModCategoryTried),
        // 词汇表也逐版落盘 —— 配置里那两个字段只是「当前版本这一份」的镜像
        CategoryOrder = new List<string>(cfg.ModUserCategoryOrder),
        CategoryIcons = new Dictionary<string, string>(cfg.ModCategoryIcon, StringComparer.OrdinalIgnoreCase),
        OfficialCategoryNames = new Dictionary<string, string>(cfg.ModOfficialCategoryNames, StringComparer.OrdinalIgnoreCase)
    };

    /// <summary>换引用，不就地清空：渲染线程可能正拿着旧字典在遍历，
    /// <c>Clear()</c> 会抛「Collection was modified」，换指针不会。</summary>
    private static void Apply(JuniGridConfig cfg, ModsIndex idx)
    {
        cfg.ModUserCategory = new Dictionary<string, string>(idx.Categories, StringComparer.OrdinalIgnoreCase);
        cfg.ModRemarks = new Dictionary<string, string>(idx.Remarks, StringComparer.OrdinalIgnoreCase);
        cfg.ModOriginalNames = new Dictionary<string, string>(idx.OriginalNames, StringComparer.OrdinalIgnoreCase);
        cfg.ModCovers = new Dictionary<string, string>(idx.Covers);
        cfg.ModCategories = new Dictionary<string, string>(idx.NexusCategories);
        cfg.ModCategoryTried = new List<string>(idx.CategoryTried);
        // 空词汇 = 这一版没有分类：Mods 页据此不画分段、也不补空段头
        cfg.ModUserCategoryOrder = new List<string>(idx.CategoryOrder ?? new List<string>());
        cfg.ModCategoryIcon = new Dictionary<string, string>(
            idx.CategoryIcons ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        cfg.ModOfficialCategoryNames = new Dictionary<string, string>(idx.OfficialCategoryNames, StringComparer.OrdinalIgnoreCase);
    }

    // ── 读写单份账本 ──────────────────────────────────────────────────

    private static ModsIndex? TryLoad(string ver, IReadOnlyList<(string Version, string ModsRoot)> snaps,
        string? gamePath)
    {
        var idx = ReadFile(FileFor(ver));
        if (idx is not null) return idx;
        foreach (var dir in MirrorDirs(ver, snaps, gamePath))
        {
            idx = ReadFile(Path.Combine(dir, MirrorRel.Replace('/', Path.DirectorySeparatorChar)));
            if (idx is null) continue;
            AppLog.Info("ModsIndex", $"app data 里没有 {ver} 的账本，已从版本文件夹取回：{dir}");
            return idx;
        }
        return null;
    }

    private static ModsIndex? ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var idx = JsonSerializer.Deserialize<ModsIndex>(File.ReadAllText(path), JsonOpts);
            if (idx is null) return null;
            // 反序列化给的是默认 comparer 的字典，这里按各自的原始语义重建（文件夹名大小写不敏感）
            idx.Categories = new Dictionary<string, string>(idx.Categories ?? new(), StringComparer.OrdinalIgnoreCase);
            idx.Remarks = new Dictionary<string, string>(idx.Remarks ?? new(), StringComparer.OrdinalIgnoreCase);
            idx.OriginalNames = new Dictionary<string, string>(idx.OriginalNames ?? new(), StringComparer.OrdinalIgnoreCase);
            idx.Covers ??= new();
            idx.NexusCategories ??= new();
            idx.OfficialCategoryNames = new Dictionary<string, string>(idx.OfficialCategoryNames ?? new(), StringComparer.OrdinalIgnoreCase);
            idx.CategoryTried ??= new();
            return idx;
        }
        catch (Exception ex)
        {
            AppLog.Warn("ModsIndex", $"读 {path} 失败（当成没有这份账）：{ex.Message}");
            return null;
        }
    }

    private static void WriteMirror(string dir, string json)
    {
        try
        {
            var path = Path.Combine(dir, MirrorRel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            // 版本包可能被别的进程占着 / 只读：副本写不了不影响主副本
            AppLog.Info("ModsIndex", $"版本文件夹副本写不进去（{dir}）：{ex.Message}");
        }
    }

    /// <summary>这个版本的「版本文件夹」：缓存包、孤儿备份、半截包隔离区里装着同版本 Mods 的那些目录。
    /// <b>不含</b>正在用的游戏目录 —— 那是游戏本体，不许往里丢东西。</summary>
    private static List<string> MirrorDirs(string ver, IReadOnlyList<(string Version, string ModsRoot)> snaps,
        string? gamePath)
    {
        var list = new List<string>();
        var live = string.IsNullOrWhiteSpace(gamePath)
            ? null : Path.Combine(gamePath, "Mods");
        foreach (var (v, modsRoot) in snaps)
        {
            if (!string.Equals(ModService.NormalizeGameVersion(v), ver, StringComparison.OrdinalIgnoreCase)) continue;
            if (live is not null && string.Equals(Path.GetFullPath(modsRoot), Path.GetFullPath(live),
                    StringComparison.OrdinalIgnoreCase)) continue;
            var dir = Path.GetDirectoryName(modsRoot.TrimEnd(Path.DirectorySeparatorChar, '/'));
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)) list.Add(dir!);
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ── 旧账分家 ─────────────────────────────────────────────────────

    /// <summary>把分账之前那本全局账按「文件夹出现在哪个版本的 Mods 快照里」拆开：
    /// 属于当前版本的留在内存里，其余写进各自版本的账本文件。
    /// <para>判据只认磁盘（快照里有没有这个顶层目录），不靠时间戳猜。都找不到的留在当前版本 ——
    /// 宁可多留一条孤儿账，也不把用户的分类弄丢。</para></summary>
    private static ModsIndex MigrateLegacy(JuniGridConfig cfg, string curVer,
        IReadOnlyList<(string Version, string ModsRoot)> snaps)
    {
        var mine = new ModsIndex { Version = curVer, SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") };
        // 每个快照的顶层名字集合：一次枚举，别对几百条账各 stat 一遍
        var tops = snaps
            .Select(s => (s.Version, Names: TopNames(s.ModsRoot)))
            .Where(t => t.Names.Count > 0)
            .ToList();
        var buckets = new Dictionary<string, ModsIndex>(StringComparer.OrdinalIgnoreCase);

        ModsIndex Bucket(string v)
        {
            if (!buckets.TryGetValue(v, out var b))
                buckets[v] = b = new ModsIndex { Version = v, SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") };
            return b;
        }

        string OwnerOf(string key)
        {
            var top = key.Split('/', '\\')[0].Trim();
            if (top.Length == 0) return curVer;
            var bare = top.TrimStart('.');
            foreach (var (v, names) in tops)
                if (names.Contains(top) || names.Contains(bare) || names.Contains("." + bare)) return v;
            return curVer;
        }

        void Take<T>(Dictionary<string, T> src, Action<ModsIndex, string, T> put)
        {
            foreach (var (k, v) in src.ToList())
            {
                var owner = OwnerOf(k);
                put(IsSame(owner, curVer) ? mine : Bucket(owner), k, v);
            }
        }

        Take(cfg.ModUserCategory, (t, k, v) => t.Categories[k] = v);
        Take(cfg.ModRemarks, (t, k, v) => t.Remarks[k] = v);
        Take(cfg.ModOriginalNames, (t, k, v) => t.OriginalNames[k] = v);
        Take(cfg.ModCovers, (t, k, v) => t.Covers[k] = v);
        Take(cfg.ModCategories, (t, k, v) => t.NexusCategories[k] = v);
        foreach (var k in cfg.ModCategoryTried.ToList())
        {
            var owner = OwnerOf(k);
            (IsSame(owner, curVer) ? mine : Bucket(owner)).CategoryTried.Add(k);
        }

        // 词汇表没有文件夹键可拆 —— 按「这一版真正用到了哪些名字」切：归属在谁那本账里，
        // 名字（连同顺序和图标）就跟着进谁那本。三个版本都没用到的孤儿名字（用户建了又搬空、
        // 或者只是试验过的测试分类）就此留在原地不再跟着版本跑 —— 它们本来就是切版本后
        // 那一排「0 个」的来源。
        SeedVocabulary(mine, _legacyOrder, _legacyIcons);
        foreach (var b in buckets.Values) SeedVocabulary(b, _legacyOrder, _legacyIcons);

        foreach (var (v, b) in buckets)
        {
            try
            {
                var json = JsonSerializer.Serialize(b, JsonOpts);
                AtomicFile.WriteAllText(FileFor(v), json);
                foreach (var dir in MirrorDirs(v, snaps, cfg.GamePath)) WriteMirror(dir, json);
            }
            catch (Exception ex)
            {
                AppLog.Warn("ModsIndex", $"旧账拆到 {v} 时写盘失败：{ex.Message}");
            }
        }
        if (buckets.Count > 0)
            AppLog.Info("ModsIndex",
                $"旧账按版本拆分：{curVer} 留 {mine.Total} 条，" +
                $"另存 {buckets.Count} 个版本（{string.Join("、", buckets.Keys.Take(6))}）");
        return mine;
    }

    private static bool IsSame(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>给一本账播种分类词汇：只收「这本账的归属里真的出现过」的名字，顺序与图标沿用全局那份。
    /// <para>为什么按归属筛而不是整份照抄：词汇表里的名字没有任何文件夹键，看不出属于哪一版；
    /// 唯一的证据就是「这一版的某个文件夹被归进了它」。照抄整份 = 每切一版就把别的版本建的名字
    /// 全搬过来，屏幕上又立起一排 0 个的空段头。</para>
    /// <para>孤儿名字（哪一版都没有成员）在这里<del>不保留</del>—— 它们本来就不是任何一版的内容，
    /// 用户要留就重建成一个分类，要删也就此消失。</para></summary>
    private static void SeedVocabulary(ModsIndex idx, IReadOnlyList<string> legacyOrder,
        IReadOnlyDictionary<string, string> legacyIcons)
    {
        var used = idx.Categories.Values
            .SelectMany(ModCategoryUtil.Split)          // 值可能是多选拼起来的 "9,10"
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var order = new List<string>();
        foreach (var c in legacyOrder)
        {
            if (c is null) continue;
            if (c.Length == 0)
            {
                // "" = 「未分类」的图标槽，不是分类名：原样留着（拖动与排序把它当普通成员）
                if (!order.Contains("")) order.Add("");
                continue;
            }
            if (order.Contains(c, StringComparer.OrdinalIgnoreCase)) continue;
            if (used.Contains(c, StringComparer.OrdinalIgnoreCase)) order.Add(c);
        }
        foreach (var c in used)                          // 名单外的（手改过配置 / 更早的分类）补末尾
            if (!order.Contains(c, StringComparer.OrdinalIgnoreCase)) order.Add(c);
        idx.CategoryOrder = order;

        var icons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in legacyIcons)
        {
            if (string.IsNullOrWhiteSpace(v)) continue;
            if (k.Length == 0 || order.Contains(k, StringComparer.OrdinalIgnoreCase)) icons[k] = v;
        }
        idx.CategoryIcons = icons;
    }

    private static HashSet<string> TopNames(string modsRoot)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(modsRoot)) return set;
            foreach (var d in Directory.EnumerateDirectories(modsRoot)) set.Add(Path.GetFileName(d));
        }
        catch { }
        return set;
    }

    private static string SafeName(string ver)
    {
        var bad = Path.GetInvalidFileNameChars();
        var chars = ver.Trim().Select(c => bad.Contains(c) ? '_' : c).ToArray();
        var name = new string(chars);
        return name.Length == 0 ? "unknown" : name;
    }
}
