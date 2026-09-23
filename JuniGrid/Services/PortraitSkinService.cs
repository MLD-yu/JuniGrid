using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace JuniGrid.Services;

/// <summary>弹窗里的一个皮肤选项（v2：单列表，每个角色只有一个生效选择）。
/// IsVanilla=官方皮肤行（不可删）；IsNative=mod 默认外观行（娘家包，不可删）。</summary>
public sealed record PortraitSkinOption(
    string PackFolder,      // 相对 Mods/ 的包路径（唯一键，子包带 /；官方行为空串）
    string PackName,        // 显示名（官方行=「默认」、娘家行=「mod 默认外观」、其余=包名）
    bool IsPortraiture,     // Portraiture 素材包（启用/禁用归框架管，启动器不碰）
    bool IsVanilla,         // 官方皮肤行（置顶、不可删、悬停提示「默认」）
    bool IsNative,          // 娘家包（mod 自带角色的默认外观行）
    bool HasSprite,         // 该包同时整表替换精灵图（悬停提示附注「含精灵图」）
    string? SourceFile,     // 缩略图来源（绝对路径，png/xnb；null = 无图占位）
    string? SpriteFile,     // 对应的精灵图来源（走动小人整表；null = 无）
    string[] ConfigKeys)    // 选中该角色时要写 true 的 config.json 开关
{
    public bool Deletable => !IsVanilla && !IsNative;

    /// <summary>画面与这一条完全相同（解码后逐像素一致）的其它包名。肖像页只留一条，
    /// 其余收进这里，避免同一个 Krobus 出现两张一模一样的卡。</summary>
    public IReadOnlyList<string> Dupes { get; init; } = Array.Empty<string>();

    /// <summary>整张逐像素差异 ≤5% 的"近似同款"（作者重导了一遍、字节不同但画几乎一样）。
    /// 与 Dupes 分开记：界面上要能说出这条是"完全相同"还是"几乎相同"。</summary>
    public IReadOnlyList<string> NearDupes { get; init; } = Array.Empty<string>();
}

/// <summary>网格里的一个角色卡片。</summary>
public sealed record PortraitCharacter(
    string Id,
    string DisplayName,
    bool IsVanilla,
    PortraitSkinOption? Vanilla,                        // 官方皮肤行（仅原版角色/马）
    PortraitSkinOption? Native,                         // mod 默认外观行（仅 mod 角色）
    IReadOnlyList<PortraitSkinOption> Skins,            // 可选皮肤（官方/默认行之外）
    string? NativePackFolder)                           // 娘家包（SyncToDisk 永不禁用）
{
    /// <summary>弹窗列表：默认行置顶 + 皮肤（v2 单列表）。</summary>
    [Newtonsoft.Json.JsonIgnore]
    public IEnumerable<PortraitSkinOption> AllOptions
    {
        get
        {
            if (Vanilla is not null) yield return Vanilla;
            if (Native is not null) yield return Native;
            foreach (var s in Skins) yield return s;
        }
    }

    /// <summary>这张卡管着的真实 NPC 条目（同脸别名合并后 &gt;1）。写配置/写覆盖包都按这个
    /// 列表逐个落，游戏里那几份数据才会一起换脸。</summary>
    public IReadOnlyList<string> Members { get; init; } = Array.Empty<string>();

    /// <summary>被并进别的卡的别名卡自己：指向本尊 id。这类卡不上屏（游戏里那份数据仍然
    /// 单独写盘），只在扫描结果里留着给写盘链路解析。</summary>
    public string? AliasOf { get; init; }

    /// <summary>页签归属：本尊那张卡可能同时挂在「原版」和某个 mod 页签下。</summary>
    public IReadOnlyList<string> GroupKeys { get; init; } = Array.Empty<string>();

    [Newtonsoft.Json.JsonIgnore]
    public bool Hidden => !string.IsNullOrEmpty(AliasOf);

    /// <summary>生效的归属键（未合并时就是自己那一个）。</summary>
    [Newtonsoft.Json.JsonIgnore]
    public IReadOnlyList<string> TabKeys => GroupKeys.Count > 0 ? GroupKeys : new[] { GroupKey };

    /// <summary>页签分组键：优先娘家包；娘家检测没中的（如苏琪）落到第一个皮肤的来源包。</summary>
    [Newtonsoft.Json.JsonIgnore]
    public string GroupKey => NativePackFolder ?? Skins.FirstOrDefault()?.PackFolder ?? "mod";
}

public sealed class PortraitScanResult
{
    public List<PortraitCharacter> Characters { get; set; } = new();
    /// <summary>娘家包目录集合（相对 Mods/）—— SyncToDisk 禁用的豁免名单。</summary>
    public HashSet<string> NativePacks { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>娘家包目录 → manifest Name（立绘页按 mod 分组页签的显示名）。</summary>
    public Dictionary<string, string> NativePackNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>(包目录, 角色) → config 开关 —— SyncToDisk 写 true/false 用。</summary>
    /// <summary>(包目录, 角色) → config 开关 —— SyncToDisk 写 true/false 用。
    /// JSON 序列化（扫描快照）用字符串键版本 —— ValueTuple 键 Newtonsoft 写得出读不回。</summary>
    [Newtonsoft.Json.JsonIgnore]
    public Dictionary<(string Pack, string Char), string[]> ConfigKeys { get; set; } = new();

    /// <summary>ConfigKeys 的快照序列化形态（键 = "pack␟char"）。</summary>
    public Dictionary<string, string[]>? ConfigKeysFlat
    {
        get
        {
            if (ConfigKeys.Count == 0) return null;
            var d = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var kv in ConfigKeys)
                d[kv.Key.Pack + '\u241F' + kv.Key.Char] = kv.Value;
            return d;
        }
        set
        {
            if (value is null) return;
            var d = new Dictionary<(string, string), string[]>();
            foreach (var kv in value)
            {
                var sep = kv.Key.LastIndexOf('\u241F');
                if (sep <= 0) continue;
                d[(kv.Key[..sep], kv.Key[(sep + 1)..])] = kv.Value;
            }
            ConfigKeys = d;
        }
    }
    /// <summary>Portraiture 素材包根目录（相对 Mods/，如 "Portraiture/Portraits"）；未装框架为 null。</summary>
    public string? PortraitureRoot { get; set; }
    /// <summary>(包 Folder, 资产类别, 基础角色 id, 变体 id, 文件) —— 各包给角色的变体资产
    /// （季节/差分等，资产名如 Wizard_Spring）。覆盖包选中皮肤时整族钉住。</summary>
    public List<(string Pack, string Kind, string BaseId, string VariantId, string File)> VariantAssets { get; init; } = new();
    /// <summary>角色 id → Data/Characters 的 DisplayName（汉化包写的名字，i18n 已代换）。
    /// 显示名优先级：这里的中文名 &gt; 内置对照表 &gt; 英文名 &gt; id。</summary>
    public Dictionary<string, string> CharDisplayNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>v1.3.9：过滤/合并诊断（角色 → 原因），立绘页诊断面板直接展示。</summary>
    public List<(string Id, string Reason)> Diagnostics { get; set; } = new();
    /// <summary>走过 NoPortraits 占位剔除的角色（诊断文案用）。</summary>
    [Newtonsoft.Json.JsonIgnore]
    public HashSet<string> NoPortraitIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>走过空白立绘剔除的角色（诊断文案用）。</summary>
    [Newtonsoft.Json.JsonIgnore]
    public HashSet<string> BlankPortraitIds { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 立绘页核心服务：扫描 Mods 把 Content Patcher 皮肤包按角色归类、识别 Portraiture 素材包与
/// 娘家包、生成像素风缩略图（磁盘缓存）、并把选择单向同步到磁盘（启禁 mod + 写 config 开关）。
/// v2 规格：每角色只有一个选择；选中某皮肤 = 大头照+精灵图一起切（包里没精灵图就回退原版）。
/// </summary>
public sealed class PortraitSkinService
{

    /// <summary>JuniGrid 自带的立绘覆盖包（Mods/~JuniGrid Portrait Overrides）：~ 前缀让它
    /// 按 SMAPI 字母序【最后加载】—— 同优先级的补丁按加载顺序生效，最后加载 = 用户在
    /// 肖像页的选择稳赢其它启用中的肖像包（Nyapu/SVE/[CP]… 无论排在哪都以选择为准）。
    /// 以最高优先级
    /// Load 被选中角色的 Portraits/&lt;id&gt; / Characters/&lt;id&gt;，压过其它启用包的同名补丁 ——
    /// 换肤不再启停任何 mod（Childhood Sweetheart 这类功能 mod 的对话/事件不受影响）。</summary>
    public const string OverrideFolder = "~JuniGrid Portrait Overrides";
    /// <summary>历史遗留的无 `~` 覆盖包目录名（旧版启动器）；SMAPI 会加载它，幽灵 FromFile 会拖垮绘制循环。</summary>
    public const string LegacyOverrideFolder = "JuniGrid Portrait Overrides";
    public const string OverrideUid = "JuniGrid.PortraitOverrides";

    private readonly ModService _mods;
    private readonly ConfigService _cfg;

    public PortraitSkinService(ModService mods, ConfigService cfg)
    {
        _mods = mods;
        _cfg = cfg;
        Live = this;
    }

    /// <summary>供版本切换等静态路径调用（DI 单例在构造时挂到这里）。</summary>
    public static PortraitSkinService? Live { get; private set; }

    // ══════════════════════ 内置清单（常驻） ══════════════════════

    /// <summary>原版村民中文名。⚠ "是否原版角色"的判定（影响娘家包语义）以这张表的键为准；
    /// SVE 等展示名绝不能混进来 —— 展示字典与原版判定混用是 v1 的翻车点。</summary>
    private static readonly Dictionary<string, string> VanillaNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Abigail"] = "阿比盖尔", ["Alex"] = "亚历克斯", ["Caroline"] = "卡罗琳", ["Clint"] = "克林特",
        ["Demetrius"] = "德米特里厄斯", ["Dwarf"] = "矮人", ["Elliott"] = "艾利欧特", ["Emily"] = "艾米丽",
        ["Evelyn"] = "艾芙琳", ["George"] = "乔治", ["Gil"] = "吉尔", ["Gus"] = "格斯",
        ["Haley"] = "海莉", ["Harvey"] = "哈维", ["Jas"] = "贾斯", ["Jodi"] = "乔迪",
        ["Kent"] = "肯特", ["Krobus"] = "科罗布斯", ["Leah"] = "莉亚", ["Leo"] = "雷欧",
        ["Lewis"] = "刘易斯", ["Linus"] = "莱纳斯", ["Marnie"] = "玛妮", ["Marlon"] = "马龙",
        ["Maru"] = "玛鲁", ["MrQi"] = "齐先生", ["Morris"] = "莫里斯", ["Pam"] = "潘姆",
        ["Penny"] = "潘妮", ["Pierre"] = "皮埃尔", ["Robin"] = "罗宾", ["Sam"] = "山姆",
        ["Sandy"] = "桑迪", ["Sebastian"] = "塞巴斯蒂安", ["Shane"] = "谢恩", ["Vincent"] = "文森特",
        ["Willy"] = "威利", ["Wizard"] = "法师", ["Gunther"] = "冈瑟",
    };

    /// <summary>SVE 等常见 mod 角色中文名（只做展示，与原版判定无关）。</summary>
    private static readonly Dictionary<string, string> ModNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sophia"] = "苏菲亚", ["Victor"] = "维克托", ["Andy"] = "安迪", ["Claire"] = "克莱尔",
        ["Lance"] = "兰斯", ["Olivia"] = "奥利维亚", ["Susan"] = "苏珊", ["Apples"] = "阿普尔斯",
        ["Scarlett"] = "斯嘉丽", ["Suki"] = "苏琪", ["June"] = "朱恩", ["Morgan"] = "摩根",
        ["Martin"] = "马丁", ["Blair"] = "布莱尔",
    };

    /// <summary>贴图资产别名：游戏内容目录里的真实文件名与角色 id 不同时在此映射（1.6 实测：
    /// Leo 的贴图注册为 ParrotBoy，Portraits 目录根本没有 Leo.xnb）。</summary>
    private static IEnumerable<string> VanillaAssetAliases(string id)
    {
        if (id.Equals("Leo", StringComparison.OrdinalIgnoreCase))
            yield return "ParrotBoy";
        yield return id;
    }

    /// <summary>资产别名反查表：mod 的 CP Target 常沿用原版资产名（Nyapu/OhoDavi 写
    /// Portraits/ParrotBoy，从不出现 Portraits/Leo）。扫描归并与覆盖包写盘都必须
    /// 经过这里，否则雷欧看不到这些皮肤，换肤也打不中游戏真实资产。</summary>
    private static readonly Dictionary<string, string> AssetAliasToCharId =
        new(StringComparer.OrdinalIgnoreCase) { ["ParrotBoy"] = "Leo" };

    /// <summary>资产名 → 角色 id（无别名时原样返回）。</summary>
    private static string CanonCharId(string assetName) =>
        AssetAliasToCharId.TryGetValue(assetName, out var id) ? id : assetName;

    /// <summary>角色 id → 游戏内容资产名（无别名时原样返回）。覆盖包 Target 必须用它。</summary>
    private static string GameAssetId(string charId)
    {
        foreach (var (alias, id) in AssetAliasToCharId)
            if (id.Equals(charId, StringComparison.OrdinalIgnoreCase)) return alias;
        return charId;
    }

    // ══════════════════════ 扫描 ══════════════════════

    private sealed class PackScan
    {
        public string Folder = "";
        public string Name = "";
        /// <summary>v1.3.4：内容包 UniqueID —— 代换 CP 原生 {{ModId}} token 用
        ///（Downtown Zuzu 的角色/立绘全部以 {{ModId}}_Name 形式注册）。</summary>
        public string Uid = "";
        /// <summary>磁盘上的真实目录（禁用包带 . 前缀）。文件解析必须用它 ——
        /// Folder 是规范化无点名，对禁用包拼出来的路径不存在（Axylvoo 空卡实测）。</summary>
        public string RawDir = "";
        /// <summary>角色 id → 整表立绘来源文件（缩略图候选，按出现序）。</summary>
        public Dictionary<string, List<string>> PortraitFiles = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>整表替换精灵图的角色 id（判断"含精灵图"）。</summary>
        public HashSet<string> SpriteChars = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>角色 id → 整表精灵图来源文件（弹窗右侧预览用）。</summary>
        public Dictionary<string, List<string>> SpriteFiles = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>角色 id → 该包给此角色打补丁时挂的 When 布尔键（config 开关）。</summary>
        public Dictionary<string, HashSet<string>> WhenKeys = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SchemaKeys = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>变体资产：资产名是已知角色 id 的下划线变体（如 Wizard_Spring → Wizard）。
        /// 这类 Load 的 Target 是变体本体（Portraits/Wizard_Spring），常配 1.6 Appearance 按季节
        /// 挂载 —— Baechu 法师的变体 Load 被"装 SVE 不生效"门槛挡掉后，Appearance 全部指向
        /// 缺失资产，立绘空白（实机）。覆盖包需要整族钉住。</summary>
        public List<(string Kind, string BaseId, string VariantId, string File)> AssetVariants = new();
        /// <summary>包 config.json 的当前值（懒加载）。FromFile 里的 {{配置键}} 靠它代换 ——
        /// Elle's Cuter Horses 的马皮肤是 assets/Horse/{{Horse Skin}}.png，文件名取决于
        /// 玩家在 GMCM 里选的马皮肤（config.json 的当前值）。</summary>
        public Dictionary<string, string>? ConfigValues;
    }

    /// <summary>扫描 Mods 全目录，产出角色卡片数据。纯磁盘读取，可在后台线程调用。
    /// v1.3.9：扫描快照 —— 结果序列化到 LocalAppData，键 = Mods 目录签名（顶层目录数 +
    /// 各包 manifest/content.json/config.json 的 mtime 指纹）。签名未变 → 直接反序列化
    /// 秒回（低配机 + 大型 mod 阵容从 4 秒降到 0.1 秒）；变了才真正重扫并覆写快照。</summary>
    public PortraitScanResult Scan(string gamePath)
    {
        using var lease = ReadLease();
        var cached = TryLoadScanCache(gamePath, out var sig);
        if (cached is not null)
        {
            RememberScannedCharIds(gamePath, cached.Characters.Select(c => c.Id));
            return cached;
        }

        var result = new PortraitScanResult();
        if (string.IsNullOrWhiteSpace(gamePath)) return result;
        var modsDir = Path.Combine(gamePath, "Mods");
        // 没有 Mods 也要往下走：原版角色来自 VanillaNames 表，跟 mod 无关。以前这里直接返回空，
        // 于是 Steam 刚重装完（Mods 目录还不存在）的那一会儿整页 0 角色，界面还提示
        // "确认游戏目录指向的是星露谷本体" —— 目录是对的，是扫描自己提前退了。
        var hasModsDir = Directory.Exists(modsDir);

        // ── 1. 枚举所有 manifest：CP 内容包 + Portraiture 框架 ──
        // 分层枚举：跳过回收站（里面常有整棵 Mods 备份，误识别会让 Portraiture 根指向回收站），
        // 每个顶层目录限深 —— 子包 manifest 至多三四层，更深处都是素材文件
        var packs = new List<PackScan>();
        string? portraitureRootDir = null;
        List<string> manifests = new();
        try
        {
            // 与 ModService.ScanRaw 同款枚举（SearchOption.AllDirectories）——
            // ⚠ 不要换 EnumerationOptions：其一 RecurseSubdirectories 默认 false（不显式设 true
            // 只扫顶层一层，子包全漏）；其二 IgnoreInaccessible 会把瞬时占用静默吞成
            // "该目录 0 个 manifest"，实机偶发整个 SVE 消失且无任何日志。ScanRaw 在生产
            // 环境验证过，每目录失败时记日志便于诊断。
            foreach (var top in hasModsDir ? Directory.EnumerateDirectories(modsDir) : Array.Empty<string>())
            {
                if (Path.GetFileName(top).StartsWith(".junigrid_trash", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    manifests.AddRange(Directory.GetFiles(top, "manifest.json", SearchOption.AllDirectories));
                }
                catch (Exception ex)
                { AppLog.Warn("Portraits", $"枚举 {Path.GetFileName(top)} 失败: {ex.Message}"); }
            }
        }
        catch (Exception ex)
        { AppLog.Warn("Portraits", "枚举 manifest 失败: " + ex.Message); return result; }

        foreach (var mf in manifests)
        {
            try
            {
                JObject man;
                using (var sr = new StreamReader(mf))
                    man = JObject.Parse(sr.ReadToEnd());
                var cpf = man["ContentPackFor"]?["UniqueID"]?.ToString();
                var uid = man["UniqueID"]?.ToString();
                var dir = Path.GetDirectoryName(mf)!;
                // 覆盖包是本服务自己生成/维护的，绝不能被扫成角色的皮肤来源（自己覆盖自己）
                if (string.Equals(uid, OverrideUid, StringComparison.OrdinalIgnoreCase))
                    continue;
                // v1.6.8：禁用的 mod（顶层目录带 . 前缀）不参与肖像页 —— SMAPI 没加载它，
                // 它的肖像/扩展 NPC 在游戏里本来就不生效，列出来只会污染页面。
                // 重新启用后自动回到肖像页；指向它的历史选择由 PackExistsAnywhere 保留。
                var relTop = Path.GetRelativePath(modsDir, dir)
                    .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar })[0];
                if (relTop.StartsWith('.'))   // 点号无大小写之分
                    continue;
                // ⚠ 规范化为无点路径：禁用包的文件夹带 . 前缀（.[CP] xx），原样记录会让
                // 选择值/后续启禁/config 写入全部用带点路径（启用改名后路径失效）。
                // 禁用状态只影响 Disabled 标志，不影响身份。
                var rel = string.Join('/',
                    Path.GetRelativePath(modsDir, dir).Replace('\\', '/')
                        .Split('/').Select(seg => seg.TrimStart('.')));

                if (string.Equals(uid, ModService.PortraitureFrameworkUid, StringComparison.OrdinalIgnoreCase))
                {
                    portraitureRootDir = Path.Combine(dir, "Portraits");
                    continue;
                }
                if (!string.Equals(cpf, "Pathoschild.ContentPatcher", StringComparison.OrdinalIgnoreCase))
                    continue;

                packs.Add(new PackScan
                {
                    Folder = rel,
                    Name = man["Name"]?.ToString() is { Length: > 0 } n ? n : Path.GetFileName(dir),
                    RawDir = dir,
                    Uid = uid ?? "",
                });
            }
            catch { /* 单个 manifest 损坏不影响整体 */ }
        }

        // Portraiture 素材包（无 manifest，PNG 文件名 = 角色 id）
        var portraiturePacks = new List<(string Folder, string Name, string Dir)>();
        if (portraitureRootDir is not null && Directory.Exists(portraitureRootDir))
        {
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(portraitureRootDir))
                {
                    var name = Path.GetFileName(sub);
                    if (name.StartsWith('.')) continue;
                    portraiturePacks.Add(("Portraiture/Portraits/" + name, name, sub));
                }
            }
            catch { }
        }

        // ── 2. 解析每个 CP 包的 content.json（含 Include 递归展开）──
        var nativeCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // Data/Characters 出现过的 mod 角色 id
        var nativeOwners = new Dictionary<(string Pack, string Char), int>();           // (包, 角色) → 娘家 rank（0 真娘家）
        var displayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // Data/Characters DisplayName（汉化中文名）
        foreach (var p in packs)
        {
            // visited：每个子 content.json 只解析一次 —— Include 链有菱形/循环引用时
            // 不加会指数爆炸（Downtown Zuzu 实测 89 秒 → 加了毫秒级）
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // 文件解析用真实磁盘目录（禁用包带 . 前缀）；规范化 Folder 拼出的路径对禁用包不存在
            var packRoot = p.RawDir is { Length: > 0 } raw
                ? raw
                : Path.Combine(modsDir, p.Folder.Replace('/', Path.DirectorySeparatorChar));
            ParseContentPack(p, packRoot, Path.Combine(packRoot, "content.json"),
                nativeCandidates, nativeOwners, displayNames, new List<string>(), gated: false, visited, depth: 0);
        }
        foreach (var kv in displayNames) result.CharDisplayNames[kv.Key] = kv.Value;

        // 变体资产汇入扫描结果（覆盖包整族钉住用）
        foreach (var p in packs)
            foreach (var v in p.AssetVariants)
                result.VariantAssets.Add((p.Folder, v.Kind, v.BaseId, v.VariantId, v.File));

        // 每个角色只认一个娘家包：rank 最小者（真娘家优先于 compat 门控改动），平局取先出现
        var bestNative = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ((pack, charId), rank) in nativeOwners)
        {
            if (bestNative.TryGetValue(charId, out var cur))
            {
                var curRank = nativeOwners[(cur, charId)];
                if (rank > curRank || (rank == curRank && string.CompareOrdinal(pack, cur) >= 0)) continue;
            }
            bestNative[charId] = pack;
        }

        // ── 3. Portraiture 素材包按 PNG 文件名归类 ──
        var portraitureSources = new Dictionary<string, List<(string Pack, string Name, string File)>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, name, dir) in portraiturePacks)
        {
            try
            {
                foreach (var png in Directory.GetFiles(dir, "*.png", SearchOption.AllDirectories))
                {
                    var id = Path.GetFileNameWithoutExtension(png);
                    if (string.IsNullOrWhiteSpace(id) || id.Contains('*') || id.Contains('/')) continue;
                    if (!portraitureSources.TryGetValue(id, out var list))
                        portraitureSources[id] = list = new();
                    list.Add((folder, name, png));
                }
            }
            catch { }
        }

        // ── 4. 变体归并：Abigail_Beach/_summer/_Hospital → 主角色 id。
        //    权威集合 = 原版 + 马 + Data/Characters 候选；剥后缀命中权威集合才算归并，
        //    剥不动的裸 id（mod 自带角色，只以立绘 patch 形式存在）保留自身。
        //    ⚠ 权威集合绝不能混入包里的原始变体名 —— 否则 "Harvey_Aerobics" 会先命中
        //    自身、永远剥不到 "Harvey"（v1 调试时踩过的自匹配坑）。
        //    ⚠ 只剥「外观变体」后缀（季节/沙滩/医院）：Suki_IceFestival、ApplesSad、
        //    ClaireJoja 这类事件/节日 id 资产彼此独立，必须保持独立（§2.1 实测结论）。
        var canonIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in VanillaNames.Keys) canonIds.Add(k);
        canonIds.Add("Horse");
        canonIds.UnionWith(nativeCandidates);
        var rawIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in packs)
        {
            foreach (var id in p.PortraitFiles.Keys) rawIds.Add(id);
            foreach (var id in p.SpriteFiles.Keys) rawIds.Add(id);
            foreach (var id in p.SpriteChars) rawIds.Add(id);
        }
        // 外观变体后缀（大小写不敏感）：季节 + 场景变装
        var appearanceSuffixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Beach", "Spring", "Summer", "Fall", "Winter", "Hospital" };

        string? ResolveVariant(string name)
        {
            // 原版资产别名优先：Nyapu/OhoDavi 的 Target 是 Portraits/ParrotBoy，
            // 必须归到角色 id Leo，否则雷欧永远只有空白默认行（实测）
            if (AssetAliasToCharId.TryGetValue(name, out var aliased)) return aliased;
            var hit = canonIds.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
            var n = name;
            while (true)
            {
                var cut = n.LastIndexOf('_');
                if (cut <= 0) break;
                var suffix = n[(cut + 1)..];
                if (!appearanceSuffixes.Contains(suffix)) break;   // 非外观后缀 → 不归并
                n = n[..cut];
                if (AssetAliasToCharId.TryGetValue(n, out var aliased2)) return aliased2;
                hit = canonIds.FirstOrDefault(k => k.Equals(n, StringComparison.OrdinalIgnoreCase));
                if (hit is not null) return hit;
            }
            // 裸 mod id（如 SCC 兼容的 SVE 角色、Portraiture 素材包 id）：原样保留
            var raw = rawIds.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
            return raw;
        }

        foreach (var p in packs)
        {
            p.PortraitFiles = MergeByResolved(p.PortraitFiles, ResolveVariant);
            p.SpriteFiles = MergeByResolved(p.SpriteFiles, ResolveVariant);
            p.SpriteChars = new HashSet<string>(
                p.SpriteChars.Select(ResolveVariant).Where(r => r is not null).Select(r => r!),
                StringComparer.OrdinalIgnoreCase);
            p.WhenKeys = p.WhenKeys
                .Select(kv => (Id: ResolveVariant(kv.Key), Keys: kv.Value))
                .Where(x => x.Id is not null)
                .GroupBy(x => x.Id!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => new HashSet<string>(
                    g.SelectMany(x => x.Keys), StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);
        }

        // ── 5. 组装角色卡片 ──
        var allCharIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in VanillaNames.Keys) allCharIds.Add(k);
        allCharIds.Add("Horse");
        foreach (var p in packs)
        {
            foreach (var id in p.PortraitFiles.Keys) allCharIds.Add(id);
            foreach (var id in p.WhenKeys.Keys) allCharIds.Add(id);
        }
        foreach (var id in portraitureSources.Keys) allCharIds.Add(id);
        allCharIds.UnionWith(rawIds);

        var configKeys = new Dictionary<(string, string), string[]>();
        var characters = new List<PortraitCharacter>();

        // v1.3.9：扩展包识别 —— 给 ≥3 个 mod NPC 当娘家的包（SVE/RSV/East Scarp 这类
        // 内容扩展）。它们常给原版 NPC 也画了新默认立绘（马龙/法师/冈瑟…与原版几乎
        // 同构图的重绘，用户实测反馈"默认肖像直接用 SVE 的"）。
        var expansionFolders = nativeOwners
            .Where(kv => kv.Value == 0 && !VanillaNames.ContainsKey(kv.Key.Char))
            .GroupBy(kv => kv.Key.Pack)
            .Where(g => g.Count() >= 3)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var id in allCharIds)
        {
            var isHorse = id.Equals("Horse", StringComparison.OrdinalIgnoreCase);
            // 马不放立绘页（用户要求：只要 NPC）—— 马没有"对话立绘"，换肤就是整体贴图，
            // 走 mod 自己的配置（如 Elle's Cuter Horses 的 config.json）
            if (isHorse) continue;
            var isVanilla = VanillaNames.ContainsKey(id);

            // mod 角色必须有整表立绘来源，否则是事件临时演员/纯数据条目 → 不显示
            // v1.3.4：有娘家注册却无立绘来源 = 立绘链路断裂（Include/子文件解析问题），
            // 记诊断日志 —— 无娘家的事件临时演员静默跳过。
            var hasPortraitSkin = packs.Any(p => p.PortraitFiles.ContainsKey(id))
                || portraitureSources.ContainsKey(id);
            if (!isVanilla && !hasPortraitSkin)
            {
                if (bestNative.TryGetValue(id, out var bn0) && !string.IsNullOrEmpty(bn0))
                {
                    result.Diagnostics.Add((id, $"娘家 {bn0}：有 NPC 注册但没有任何立绘来源（可能只有精灵或写法未识别），未显示"));
                    AppLog.Warn("Portraits", $"[过滤诊断] {id}（娘家 {bn0}）无任何立绘来源，未显示");
                }
                continue;
            }

            // 官方皮肤行（原版角色专属；官方皮肤不可删，悬停提示「默认」）。
            // v1.3.9：扩展包增强的默认像 —— SVE 这类扩展给马龙/法师/冈瑟等原版 NPC
            // 画了与原版几乎一样构图的新版立绘且游戏里本来就被它覆盖（Load 生效），
            // 默认行直接用扩展包的文件（预览=游戏实际）；卸载扩展包后扫描自然回落原版 xnb。
            PortraitSkinOption? vanillaRow = null;
            if (isVanilla)
            {
                var defP = (string?)null; var defPortrait = VanillaPortraitXnb(gamePath, id);
                var defSprite = VanillaSpriteXnb(gamePath, id);
                foreach (var p in packs)
                {
                    if (!expansionFolders.Contains(p.Folder)) continue;
                    // 精确名优先（Portraits/<id>）；SVE 给马龙/冈瑟的新立绘挂在
                    // MarlonFay / GuntherSilvian 这类"前缀+剧情名"资产下（1.6 Appearance
                    // 引用），主名 Portraits/<id> 反而没 Load —— 前缀匹配兜住
                    if (!p.PortraitFiles.TryGetValue(id, out var pf) || pf is null || pf.Count == 0)
                    {
                        // 剩余部分必须以大写字母开头（复合词：MarlonFay/GuntherSilvian），
                        // 否则会把 Jasper（以 Jas 开头的另一个 NPC）错当贾斯的默认像（实测）
                        var altKey = p.PortraitFiles.Keys.FirstOrDefault(k =>
                            k.StartsWith(id, StringComparison.OrdinalIgnoreCase) && k.Length > id.Length
                            && char.IsUpper(k[id.Length]));
                        if (altKey is null) continue;
                        pf = p.PortraitFiles[altKey];
                    }
                    if (pf is null || pf.Count == 0) continue;
                    var f = PickSource(id, pf);
                    if (f is null) continue;
                    defP = p.Folder;
                    defPortrait = f;
                    defSprite = p.SpriteFiles.TryGetValue(id, out var sf)
                        ? PickSource(id, sf) ?? defSprite : defSprite;
                    if (defSprite is null || defSprite.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase))
                    {
                        var altSKey = p.SpriteFiles.Keys.FirstOrDefault(k =>
                            k.StartsWith(id, StringComparison.OrdinalIgnoreCase) && k.Length > id.Length
                            && char.IsUpper(k[id.Length]));
                        if (altSKey is not null)
                            defSprite = PickSource(id, p.SpriteFiles[altSKey]) ?? defSprite;
                    }
                    break;
                }
                // 变体资产路径：SVE 给马龙/冈瑟的新立绘挂在 Portraits/MarlonFay、
                // Portraits/GuntherSilvian 这类变体资产名下（1.6 Appearance 引用），
                // 主名 Portraits/<id> 反而没 Load —— 从变体登记里补
                if (defPortrait == VanillaPortraitXnb(gamePath, id))
                {
                    var v = result.VariantAssets.FirstOrDefault(va =>
                        expansionFolders.Contains(va.Pack)
                        && string.Equals(va.BaseId, id, StringComparison.OrdinalIgnoreCase)
                        && va.Kind == "Portraits");
                    if (v.Pack is not null)
                    {
                        defP = v.Pack;
                        defPortrait = v.File;
                        var vs = result.VariantAssets.FirstOrDefault(va =>
                            expansionFolders.Contains(va.Pack)
                            && string.Equals(va.BaseId, id, StringComparison.OrdinalIgnoreCase)
                            && va.Kind == "Characters");
                        if (vs.Pack is not null) defSprite = vs.File;
                    }
                }
                vanillaRow = new PortraitSkinOption("", "默认", IsPortraiture: false, IsVanilla: true,
                    IsNative: false, HasSprite: defSprite is not null, defPortrait,
                    defSprite, Array.Empty<string>());
            }

            var skins = new List<PortraitSkinOption>();
            PortraitSkinOption? nativeRow = null;
            string? nativePack = null;

            foreach (var p in packs)
            {
                // 该包是否为此角色的娘家包（Data/Characters 出现过，且是该角色的最优候选）——
                // 仅 mod 角色生效：给原版角色的 Data/Characters 兼容改动不算娘家
                bestNative.TryGetValue(id, out var nativeOwner);
                var isNativeFor = !isVanilla &&
                                  string.Equals(nativeOwner, p.Folder, StringComparison.OrdinalIgnoreCase);
                p.PortraitFiles.TryGetValue(id, out var files);
                p.SpriteFiles.TryGetValue(id, out var spriteFiles);
                p.WhenKeys.TryGetValue(id, out var whenKeys);
                // 包必须真的给这个角色注册过立绘（Load 过 Portraits/<id>）才算皮肤 ——
                // 否则每个内容包都会变成每个角色的"皮肤"（实机用户反馈：满屏不相关皮肤）
                if (files is null) continue;

                // 季节服装包常把动态资源放在 assets/Portraits/Emily/Emily_Spring.png
                // 这类路径依赖运行时 token，静态解析不到时仍从实际素材目录取一张代表图。
                var packRoot = p.RawDir is { Length: > 0 } rawDir
                    ? rawDir
                    : Path.Combine(modsDir, p.Folder.Replace('/', Path.DirectorySeparatorChar));
                var src = PickSource(id, files) ?? FindCharacterAsset(packRoot, "Portraits", id);
                // 解析不出立绘文件的皮肤直接不列（未知 mod 写法的系统性兜底）——
                // 空卡不能选（切换会被阻止）、缩略图永远是空占位、删除键还会误删整个包
                if (src is null) continue;

                var schemaKeys = p.SchemaKeys.Where(k => k.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0);
                var keys = (whenKeys ?? Enumerable.Empty<string>()).Union(schemaKeys).Distinct().ToArray();

                var opt = new PortraitSkinOption(p.Folder, p.Name, IsPortraiture: false, IsVanilla: false,
                    IsNative: isNativeFor, HasSprite: p.SpriteChars.Contains(id), src,
                    PickSource(id, spriteFiles) ?? FindCharacterAsset(packRoot, "Characters", id), keys);
                configKeys[(p.Folder, id)] = keys;

                if (isNativeFor)
                {
                    // 娘家包 = mod 自带角色的「mod 默认外观」行：不算皮肤、角标不计数、不可删
                    nativeRow = opt with { PackName = $"{opt.PackName} 默认外观", IsNative = true };
                    nativePack = p.Folder;
                    result.NativePacks.Add(p.Folder);
                    result.NativePackNames[p.Folder] = p.Name;
                }
                else
                {
                    skins.Add(opt);
                }
            }

            foreach (var (folder, name, file) in
                     portraitureSources.TryGetValue(id, out var ps) ? ps : new List<(string, string, string)>())
            {
                skins.Add(new PortraitSkinOption(folder, name, IsPortraiture: true, IsVanilla: false,
                    IsNative: false, HasSprite: false, file, null, Array.Empty<string>()));
            }

            // 吉尔这类原版角色在 1.6 本体没有 Characters/<id>.xnb（贴图叫 GilSprite，
            // 由内容包 Load 后按别名归到角色名下）—— 原版行没图时取包里登记的精灵表，
            // 默认行也能预览行走贴图
            if (vanillaRow is not null && vanillaRow.SpriteFile is null)
            {
                var packSprite = packs
                    .Select(p => p.SpriteFiles.TryGetValue(id, out var sf) ? PickSource(id, sf) : null)
                    .FirstOrDefault(f => f is not null);
                if (packSprite is not null)
                    vanillaRow = vanillaRow with { SpriteFile = packSprite, HasSprite = true };
            }

            // 立绘来源全是 NoPortraits 占位（故意不画脸）→ 剔除；单包 NoPortraits → 该包剔除
            if (nativeRow?.SourceFile is not null && IsNoPortraitsSource(nativeRow.SourceFile))
            {
                result.NoPortraitIds.Add(id);
                continue;
            }
            skins.RemoveAll(s => s.SourceFile is not null && IsNoPortraitsSource(s.SourceFile));

            // v1.3.3b：空白立绘剔除 —— 全透明占位图（East Scarp 朱尼莫玉：游戏里根本
            // 没有对话立绘，png 是 21KB 的全透明占位）显示出来就是一块空框。
            if (nativeRow?.SourceFile is not null && IsBlankPortrait(nativeRow.SourceFile))
            {
                result.BlankPortraitIds.Add(id);
                nativeRow = null;
            }
            skins.RemoveAll(s => s.SourceFile is not null && IsBlankPortrait(s.SourceFile));

            // mod 角色：只显示内置常见名单里的角色（SVE 常驻阵容，对应交接文档的内置清单）。
            // 其余是事件临时演员/节日变体/特殊演员（Brianna、SVE_Henchman、MarlonFay、
            // Suki_IceFestival…）——立绘只有娘家包自己一份，玩家没有"换皮肤"的意义，而且
            // 不少资产是精灵表/残片，显示出来就是错的（用户实测反馈：没必要显示+显示不正确）
            // v1.3.3：有娘家注册（Data/Characters）的 mod 新角色放行 —— East Scarp 这类
            // "新增 NPC"mod 的角色各有 Data/Characters 注册 + 完整立绘/精灵，是被 474 白名单
            // 整族误杀的正式 NPC（临时演员没有 Data/Characters 注册，依然被滤）。
            // v1.3.4：有娘家注册却被滤的角色记诊断日志 —— 立绘页"少角色"时日志直接给出
            // 是哪道门滤掉了谁（比阿特丽斯/埃洛伊丝时代无从排查的教训）。
            // v1.3.9：诊断文案细分三种情况 —— 功能性 NPC 没画立绘（最常见）/ 故意空脸
            // 占位 / 立绘全透明，不再笼统说"无效"。
            if (!isVanilla && !ModNames.ContainsKey(id) && nativeRow is null)
            {
                if (bestNative.TryGetValue(id, out var bn) && !string.IsNullOrEmpty(bn))
                {
                    var reason =
                        result.NoPortraitIds.Contains(id) ? "mod 没有给这个 NPC 画对话立绘（功能性 NPC：任务人/事件路人/动物，游戏里看不到它的头像）——立绘页只展示能换装的村民，未显示" :
                        result.BlankPortraitIds.Contains(id) ? "立绘文件是全透明占位图（mod 故意不画），未显示" :
                        "mod 未提供可用的立绘来源（或来源文件缺失），未显示";
                    result.Diagnostics.Add((id, reason));
                    AppLog.Warn("Portraits", $"[过滤诊断] {id}（娘家 {bn}）无有效立绘行，未显示");
                }
                continue;
            }

            // v1.3.3b：动物伙伴不进立绘页 —— East Scarp 的 Menagerie 宠物（快乐史莱姆、
            // 女武神狗、鸭鸭）：立绘+精灵是动物动画形制（整表多帧，一只狗贴成两只），
            // 与人形村民换装预览完全不同形，显示出来就是错的。立绘页定位是村民换装。
            if (!isVanilla && IsMenagerieAnimal(id, packs))
            {
                AppLog.Warn("Portraits", $"[过滤诊断] {id}（动物伙伴）按 Menagerie 目录排除");
                result.Diagnostics.Add((id, "动物伙伴（宠物/坐骑形制），按 Menagerie 目录排除"));
                continue;
            }

            // 兜底：内置角色的娘家注册（Data/Characters）形式千奇百怪（苏琪是事件代码
            // 注册的，解析不到）。没解析到娘家时把第一个皮肤升格为「mod 默认外观」——
            // ⚠ 绝不让角色的默认立绘以"可删除皮肤"的形态出现：用户实测点了苏琪那个
            // 唯一皮肤的 ✕，把整个 SVE 都删进回收站了
            if (!isVanilla && nativeRow is null && skins.Count > 0)
            {
                var first = skins[0];
                nativeRow = first with { PackName = $"{first.PackName} 默认外观", IsNative = true };
                nativePack = first.PackFolder;
                result.NativePacks.Add(first.PackFolder);
                result.NativePackNames[first.PackFolder] = first.PackName;
                skins.RemoveAt(0);
            }

            // mod 角色的皮肤若全因解析失败被跳过（上面 src null → continue），
            // 这里会是零选项的空壳角色 → 整个不显示
            if (!isVanilla && nativeRow is null && skins.Count == 0) continue;

            // 没有任何行走贴图来源的角色整个不上（用户最终规则：有精灵图就显示精灵图，
            // 没精灵图的人物直接不上 —— 对所有角色生效；v1.3.6 放宽放过一次，
            // SVE 苏琪（占位图）立刻混进来，用户两次点名，别再放宽）。
            if (vanillaRow?.SpriteFile is null && nativeRow?.SpriteFile is null
                && skins.All(s => s.SpriteFile is null)) continue;

            // 显示名优先级：扫描抓到的中文名（汉化包 Data/Characters DisplayName，i18n 已
            // 代换）> 内置对照表（原版 / SVE 常驻阵容）> 扫描到的英文名 > 裸 id。
            // 内置表只是兜底 —— 真名以用户装的汉化为准（用户实测：装了汉化却只有几个
            // 角色显示中文，因为旧逻辑只查内置表）。
            result.CharDisplayNames.TryGetValue(id, out var scannedName);
            var display = HasCjk(scannedName) ? scannedName!
                : VanillaNames.TryGetValue(id, out var vn) ? vn
                : ModNames.TryGetValue(id, out var mn) ? mn
                : !string.IsNullOrWhiteSpace(scannedName) ? scannedName!
                : id;

            skins.Sort((a, b) => string.Compare(a.PackName, b.PackName, StringComparison.OrdinalIgnoreCase));
            characters.Add(new PortraitCharacter(id, display, isVanilla, vanillaRow, nativeRow, skins, nativePack));
        }

        // 排序：原版村民（按中文名）→ mod 角色
        characters.Sort((a, b) =>
        {
            int Rank(PortraitCharacter c) => c.IsVanilla ? 0 : 2;
            var r = Rank(a) - Rank(b);
            if (r != 0) return r;
            return string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCulture);
        });

        // v1.3.8：变体 NPC 识别与并入（用户拍板："变体应该算作 NPC 的新皮肤"）。
        // 分组信号见 VariantGroups：① 汉化把剧情变体译成与本体同名（SVE GuntherSilvian→
        // 「冈瑟」）② 没装汉化时的同脸别名（默认立绘同一文件 + id 互为前缀）。
        // 组内保留排序最前者（原版优先）为本体，其余角色的默认外观与自有皮肤并入
        // 本体的皮肤列表（见下方佐证裁决），本体那张卡同时管这几个 NPC 条目（Members）。
        string? PackOf(PortraitCharacter c) => c.Native?.PackFolder ?? c.Skins.FirstOrDefault()?.PackFolder;

        var dupOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var mergeInto = new Dictionary<string, List<PortraitSkinOption>>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in VariantGroups(characters))
        {
            PortraitCharacter? winner = null;
            // 同包去重账本：本体已有该包的皮肤/娘家时，变体同包的东西不再重复并入
            //（实测：马龙的 MarlonFay 变体自带 SCCC 皮肤，与本体已有的 SCCC 皮肤
            // 几乎同图，页面上出现两张「Seasonal Cute Characters SVE」）
            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in g)
            {
                if (winner is null)
                {
                    winner = c;
                    if (winner.Native?.PackFolder is { Length: > 0 } wf) have.Add(wf);
                    foreach (var s in winner.Skins)
                        if (!string.IsNullOrWhiteSpace(s.PackFolder)) have.Add(s.PackFolder);
                    continue;
                }
                // v1.3.8 佐证裁决（用户："怎么更准确判断变体"）：同名为必要条件，
                // 还须至少一条身份佐证 —— E1 id 包含（GuntherSilvian⊃Gunther、
                // MorrisTod⊃Morris、ScarlettFake⊃Scarlett、HighlandsDwarf⊃Dwarf、
                // MarlonFay⊃Marlon）/ E3 同娘家包 / E4 默认立绘 dHash 相似
                //（64 位汉明距 ≤12，同一构图的重绘）。全无佐证 = 可能只是两个真不同
                // NPC 恰好同名 → 各自保留并记日志，不并入。
                var e1 = winner.Id.Length >= 4
                    && (c.Id.StartsWith(winner.Id, StringComparison.OrdinalIgnoreCase)
                        || c.Id.EndsWith(winner.Id, StringComparison.OrdinalIgnoreCase));
                var wPack = PackOf(winner); var cPack = PackOf(c);
                var e3 = wPack is not null && string.Equals(wPack, cPack, StringComparison.OrdinalIgnoreCase);
                var e4 = false;
                if (!e3)
                {
                    // E4：跨皮肤最小汉明距 —— 本体任一来源 vs 变体任一来源两两比对取最小，
                    // 变体换了默认图也能靠其它皮肤兜住（v1.3.9：原来只比默认一张）
                    var wSrcs = new[] { winner.Vanilla?.SourceFile, winner.Native?.SourceFile }
                        .Concat(winner.Skins.Select(s => s.SourceFile))
                        .Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f!).Distinct().ToList();
                    var cSrcs = new[] { c.Vanilla?.SourceFile, c.Native?.SourceFile }
                        .Concat(c.Skins.Select(s => s.SourceFile))
                        .Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f!).Distinct().ToList();
                    if (wSrcs.Count > 0 && cSrcs.Count > 0)
                    {
                        foreach (var wf in wSrcs.Take(5))
                        {
                            var hw = DHash(wf);
                            if (hw is null) continue;
                            foreach (var cf in cSrcs.Take(5))
                            {
                                var hc = DHash(cf);
                                if (hc is null) continue;
                                if (Hamming(hw.Value, hc.Value) <= 12) { e4 = true; break; }
                            }
                            if (e4) break;
                        }
                    }
                }
                if (!e1 && !e3 && !e4)
                {
                    AppLog.Warn("Portraits",
                        $"[同名保留] {c.Id} 与 {winner.Id} 同名「{c.DisplayName}」但无变体佐证（id/娘家/立绘均不符），各自保留");
                    continue;
                }
                AppLog.Warn("Portraits",
                    $"[变体并入] {c.Id} → {winner.Id}「{winner.DisplayName}」佐证: " +
                    $"{(e1 ? "id包含 " : "")}{(e3 ? "同娘家 " : "")}{(e4 ? "立绘相似" : "")}");
                dupOf[c.Id] = winner.Id;
                var bag = mergeInto.TryGetValue(winner.Id, out var b)
                    ? b : mergeInto[winner.Id] = new();
                if (c.Native?.SourceFile is not null)
                {
                    var folder = c.Native.PackFolder;
                    if (string.IsNullOrWhiteSpace(folder) || have.Add(folder))
                    {
                        var pn = result.NativePackNames.TryGetValue(folder ?? "",
                            out var npn) ? npn : c.Native.PackName;
                        bag.Add(c.Native with
                        {
                            IsNative = false,
                            PackName = $"{pn}（{c.Id}）"
                        });
                    }
                }
                foreach (var s in c.Skins)
                {
                    if (string.IsNullOrWhiteSpace(s.PackFolder) || !have.Add(s.PackFolder)) continue;
                    bag.Add(s);
                }
            }
        }
        if (dupOf.Count > 0)
        {
            // ⚠ 变体卡只是不上屏，**绝不能从扫描结果里删掉**（v1.3.8 是 RemoveAll）：
            // 覆盖包、季节配置、缩略图全按真实 NPC 条目逐个解析，删掉就等于游戏里
            // 那份数据没人管 —— 而它和同一个人共用一张脸，本体换装时它必须跟着换。
            var dupKeys = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < characters.Count; i++)
            {
                if (!dupOf.TryGetValue(characters[i].Id, out var own)) continue;
                if (!dupKeys.TryGetValue(own, out var lst)) dupKeys[own] = lst = new();
                lst.AddRange(characters[i].TabKeys);
                characters[i] = characters[i] with { AliasOf = own };
            }
            foreach (var w in mergeInto)
            {
                var idx = characters.FindIndex(c => c.Id.Equals(w.Key, StringComparison.OrdinalIgnoreCase));
                if (idx < 0) continue;
                var win = characters[idx];
                var skins = win.Skins.Concat(w.Value).ToList();
                var members = (win.Members.Count > 0 ? win.Members.ToList() : new List<string> { win.Id });
                if (dupKeys.TryGetValue(w.Key, out _))
                    members.AddRange(dupOf.Where(kv => kv.Value.Equals(w.Key, StringComparison.OrdinalIgnoreCase))
                        .Select(kv => kv.Key));
                // 页签归属：本尊是原版角色时，它自己那张不占 mod 页签（否则"有 SCCC 皮肤"
                // 就把冈瑟挂进 SCCC 页签），只有被并进来的 mod 变体那几份数据才带来 mod 页签。
                var ownKeys = win.IsVanilla ? Array.Empty<string>() : win.TabKeys;
                var keys = ownKeys.Concat(dupKeys.TryGetValue(w.Key, out var dk) ? dk : Array.Empty<string>())
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (keys.Count == 0) keys.Add("mod");
                characters[idx] = win with { Skins = skins, Members = members, GroupKeys = keys };
                // ⚠ 绝不能把被并入包从 NativePacks 移除 —— 那会连 SVE 这类扩展包本体的
                // 娘家保护一起丢掉（SyncToDisk 禁用豁免名单），导致 SVE 被误禁 +
                // 整体替换候选混入扩展包（两个 bug 同源，实测抓过）
            }
            foreach (var dup in dupOf)
            {
                AppLog.Warn("Portraits", $"[过滤诊断] {dup.Key}（同名变体）并入 {dup.Value} 作皮肤");
                result.Diagnostics.Add((dup.Key, "同名变体：已并入本体作为可选皮肤"));
            }
        }

        // v1.3.9：视觉重复皮肤折叠（用户："这些怎么还没消失"）+ 与默认像同文件的字面重复。
        // 抽成 FoldSkinsAgainstDefault 单独跑（用例 B22 直接盯着它）。
        FoldSkinsAgainstDefault(characters,
            id => _cfg.Current.PortraitSkins.TryGetValue(id, out var sp) ? sp : null,
            result.Diagnostics);

        // 季节皮肤分配校验（对全部角色，不限于发生过折叠的）：指向已不存在/空包名的
        // 条目回落全局选择，否则界面无卡可高亮、游戏里永远显示那个失效的包。
        {
            var ss0 = _cfg.Current.PortraitSeasonSkins;
            if (ss0.Count > 0)
            {
                var byId = characters.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
                var changedAny = false;
                foreach (var chId in ss0.Keys.ToList())
                {
                    if (!byId.TryGetValue(chId, out var chX))
                    {
                        // 角色本身已不在扫描结果里（卸载/改名）—— 整条作废
                        ss0.Remove(chId);
                        changedAny = true;
                        AppLog.Warn("Portraits", $"[季节清理] {chId} 已不在扫描结果，季节分配已清除");
                        continue;
                    }
                    var ssRaw0 = ss0[chId];
                    var parts0 = new List<string>();
                    var changed0 = false;
                    foreach (var seg0 in ssRaw0.Split('␟'))
                    {
                        var i0 = seg0.IndexOf(':');
                        if (i0 <= 0) { changed0 = true; continue; }
                        var sPack0 = seg0[(i0 + 1)..];
                        // 空包名 = 显式固定到默认/原版行（右键「默认」），合法保留。
                        // 非空则必须还能在选项里找到，否则丢弃。
                        var alive0 = sPack0.Length == 0 || chX.AllOptions.Any(o =>
                            string.Equals(o.PackFolder ?? "", sPack0, StringComparison.OrdinalIgnoreCase));
                        if (alive0) parts0.Add(seg0); else changed0 = true;
                    }
                    if (changed0)
                    {
                        if (parts0.Count == 0) ss0.Remove(chId);
                        else ss0[chId] = string.Join('␟', parts0);
                        changedAny = true;
                        AppLog.Warn("Portraits", $"[季节清理] {chId} 指向失效/空包名的季节分配已回落全局选择");
                    }
                }
                if (changedAny) _cfg.Save(_cfg.Current);
            }
        }

        foreach (var (key, keys) in configKeys) result.ConfigKeys[key] = keys;
        // 选择指向已消失的包 ⇒ 游戏里其实是默认脸。屏幕上不弹提示（一看就知道换回默认了），
        // 但留一行日志：用户报"我明明换过脸"时，这是唯一能查到的痕迹。
        foreach (var ch in characters)
            foreach (var id in (ch.Members.Count > 0 ? ch.Members : new[] { ch.Id }))
            {
                if (!_cfg.Current.PortraitSkins.TryGetValue(id, out var want)) continue;
                if (StalePack(ch.AllOptions, want) is { } gone)
                    AppLog.Warn("Portraits", $"[选择失效] {id} → {gone} 已不在 Mods，游戏里显示默认脸");
            }
        result.PortraitureRoot = portraitureRootDir is null
            ? null : Path.GetRelativePath(modsDir, portraitureRootDir).Replace('\\', '/');
        result.Characters = characters;
        RememberScannedCharIds(gamePath, characters.Select(c => c.Id));
        SaveProbePersist();   // 本轮新判定统一落盘一次
        SaveScanCache(gamePath, result, sig);
        return result;
    }

    /// <summary>
    /// 版本切换期间挂起立绘读盘。我们自己就是 Mods 目录最大的读者（扫描要解码图片判透明/
    /// 比画面，缩略图预热要逐张读），Windows 上目录里有文件被打开时 Directory.Move 会失败 ⇒
    /// 切换退化成整棵拷贝（实测 4.3 秒那次就是被自家扫描挡住，113 项白拷一遍）。
    /// 切换开始置 true、结束置 false；扫描与缩略图在挂起期间排队等待，不开新的读盘任务。
    /// </summary>
    public static volatile bool Suspended;

    private static int _readers;
    private static readonly Timer ResumeTimer = new(_ => Suspended = false, null, Timeout.Infinite, Timeout.Infinite);

    /// <summary>切换开始：挂起读盘，并兜底 30 秒后自动恢复 —— 切换线程被强杀或崩在半路时，
    /// 不能把立绘页永久锁死（那看起来就像"扫描坏了"）。</summary>
    public static void SuspendReads()
    {
        Suspended = true;
        KeepSuspended();
    }

    /// <summary>给挂起续期。⚠ 没有这一步，30 秒兜底会在一次长切换（拷 700 MB 完全可能超过
    /// 30 秒）中途把 Suspended 放开 ⇒ 立绘扫描醒过来重新读 Mods，正搬着的目录又被占住 ——
    /// 我们这次要修的正是"自己抢自己"。切换每走完一个阶段调一次；线程真死了才会自动放行。</summary>
    public static void KeepSuspended() => ResumeTimer.Change(30000, Timeout.Infinite);

    public static void ResumeReads()
    {
        ResumeTimer.Change(Timeout.Infinite, Timeout.Infinite);
        Suspended = false;
    }

    /// <summary>等立绘这边正在跑的扫描/缩略图收手（切换前调用）。超时也放行 —— 改名还有重试兜底。</summary>
    public static void WaitUntilIdle(int timeoutMs = 8000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Volatile.Read(ref _readers) > 0 && Environment.TickCount64 < until) Thread.Sleep(100);
    }

    /// <summary>一次读盘（扫描或缩略图生成）的租约：挂起时排队等，计数供切换侧等空闲。</summary>
    private static IDisposable ReadLease()
    {
        var until = Environment.TickCount64 + 20000;
        while (Suspended && Environment.TickCount64 < until) Thread.Sleep(150);
        Interlocked.Increment(ref _readers);
        return new Lease();
    }

    private sealed class Lease : IDisposable
    {
        private int _alive = 1;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _alive, 0) == 0) return;
            Interlocked.Decrement(ref _readers);
        }
    }

    // ══════════════════ v1.3.9 扫描快照缓存 ══════════════════

    private static string ScanCachePath =>
        Path.Combine(StoragePaths.AppDataDir, "portrait-scan-cache.json");

    /// <summary>Content\Portraits\*.xnb 的角色名前缀（Abigail_Winter → Abigail），按游戏目录缓存。
    /// 用来判「这张裸图是不是给某个已知角色的」。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, HashSet<string>>
        VanillaIdsCache = new(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> VanillaPortraitIds(string gamePath)
        => VanillaIdsCache.GetOrAdd(gamePath, gp =>
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var dir = Path.Combine(gp, "Content", "Portraits");
                if (!Directory.Exists(dir)) return set;
                foreach (var f in Directory.EnumerateFiles(dir, "*.xnb"))
                {
                    var nm = Path.GetFileNameWithoutExtension(f);
                    var cut = nm.IndexOf('_');
                    set.Add(cut > 0 ? nm[..cut] : nm);
                }
            }
            catch { }
            return set;
        });

    /// <summary>上一次扫描认出的角色 id（含 mod 新增角色，如 SVE 的 Andy / ScarlettFake）。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, HashSet<string>>
        ScannedCharIdsCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>扫描跑完（或快照命中）就把角色表留在内存里，供裸素材包判定用。</summary>
    private static void RememberScannedCharIds(string gamePath, IEnumerable<string> ids)
    {
        var set = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        if (set.Count > 0) ScannedCharIdsCache[gamePath] = set;
    }

    /// <summary>原版角色 ∪ 上次扫描认出的角色。mod 新增角色只有扫描过才认得：
    /// 内存里没有就顺手读一次落盘快照（只认得"这个目录该不该出现转换按钮"，
    /// 认不出就当不该动它 —— 多认几个名字只会让裸素材包更容易被认出来，
    /// 不会把别人的包误判成肖像包，那一道靠"整棵子树没有 manifest.json"挡）。</summary>
    private static HashSet<string> KnownPortraitIds(string gamePath)
    {
        var ids = new HashSet<string>(VanillaPortraitIds(gamePath), StringComparer.OrdinalIgnoreCase);
        if (!ScannedCharIdsCache.TryGetValue(gamePath, out var scanned))
            ScannedCharIdsCache[gamePath] = scanned = LoadScannedCharIds(gamePath);
        foreach (var id in scanned) ids.Add(id);
        return ids;
    }

    private static HashSet<string> LoadScannedCharIds(string gamePath)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(ScanCachePath)) return set;
            var j = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(ScanCachePath));
            if (!string.Equals(j["game"]?.ToString(), gamePath, StringComparison.OrdinalIgnoreCase)) return set;
            if (j["scan"]?["Characters"] is Newtonsoft.Json.Linq.JArray arr)
                foreach (var t in arr)
                    if (t["Id"]?.ToString() is { Length: > 0 } id)
                        set.Add(id);
        }
        catch { }
        return set;
    }

    /// <summary>
    /// 这个目录是不是「手工放进 Mods\ 的裸肖像素材包」。三条全满足才算，宁可漏判不误判：
    /// ① 整棵子树里没有任何 manifest.json —— 有就说明是别人的包或捆绑包，SVE、Downtown Zuzu
    ///    里面也躺着 Emily.png，就是靠这条挡住；
    /// ② 至少一张 .png/.xnb 的文件名前缀对得上角色名（Emily.png、Abigail_Spring.png），
    ///    角色表 = 原版 + 上次扫描认出的 mod 新增角色（SVE 的 Andy 等）；
    /// ③ 不是回收站 / 隐藏目录 / 我们自己的留底与覆盖包。
    /// </summary>
    public static bool LooksLikeLoosePortraitFolder(string gamePath, string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;
            var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (name.StartsWith('.') || name.StartsWith("~", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.EndsWith("-raw-backup", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.Equals("Portraiture", StringComparison.OrdinalIgnoreCase)) return false;
            if (Directory.EnumerateFiles(dir, "manifest.json", SearchOption.AllDirectories).Any()) return false;
            var ids = KnownPortraitIds(gamePath);
            if (ids.Count == 0) return false;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(f);
                if (!ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
                    && !ext.Equals(".xnb", StringComparison.OrdinalIgnoreCase)) continue;
                var nm = Path.GetFileNameWithoutExtension(f);
                var cut = nm.IndexOf('_');
                if (ids.Contains(cut > 0 ? nm[..cut] : nm)) return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>配置里给这个角色选的包已经不在本次扫描的候选里（包被卸载、或目录被人手工搬走）
    /// 时返回那个消失的包名，否则 null。不清配置 —— 那是不可逆的用户数据，包装回来选择就该复活；
    /// 界面上只把"现在其实是默认脸"说清楚，否则用户只会以为"我明明换过了"。</summary>
    public static string? StalePack(IEnumerable<PortraitSkinOption> options, string? want)
    {
        if (string.IsNullOrWhiteSpace(want)) return null;
        return options.Any(o => string.Equals(o.PackFolder ?? "", want, StringComparison.OrdinalIgnoreCase))
            ? null : want;
    }

    /// <summary>
    /// 同脸别名 NPC 表（SVE 的 ScarlettFake ↔ Scarlett 等）：别名 → 本名。
    /// 这些是游戏里真有其人的独立 NPC（有自己的对话与日程），只是被同一个包套了同一张脸 ——
    /// 不能折叠成一条，但要标注，否则看起来像我们把同一个角色列了两遍、去重漏了。
    /// 判据两条都要满足：① 默认立绘来自同一个文件；② 名字互为前缀（Scarlett → ScarlettFake）。
    /// 只满足①的是两张真的不同角色共用了素材（比如同款通用脸），标成别名反而误导。
    /// </summary>
    public static Dictionary<string, string> AliasMap(IReadOnlyList<PortraitCharacter> chars)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byFile = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var ch in chars)
        {
            if (ch.Hidden) continue;   // 已经并过的卡不再参与（MergeAliasCards 幂等）
            var f = ch.Native?.SourceFile ?? ch.Vanilla?.SourceFile ?? ch.Skins.FirstOrDefault()?.SourceFile;
            if (string.IsNullOrWhiteSpace(f)) continue;
            if (!byFile.TryGetValue(f, out var lst)) byFile[f] = lst = new List<string>();
            lst.Add(ch.Id);
        }
        foreach (var ids in byFile.Values)
        {
            if (ids.Count < 2) continue;
            var ordered = ids.OrderBy(x => x.Length).ToList();
            var main = ordered[0];
            foreach (var other in ordered.Skip(1))
                if (other.StartsWith(main, StringComparison.OrdinalIgnoreCase)) map[other] = main;
        }
        return map;
    }

    /// <summary>
    /// 变体分组：两条信号取并集 —— ① DisplayName 相同（v1.3.8 旧口径，只有装了汉化才命中）；
    /// ② 同脸别名（默认立绘同一个文件 + id 互为前缀，与语言无关）。
    /// ②是必须的：SVE 给剧情多开的条目（ScarlettFake / GuntherSilvian / MorrisTod /
    /// MarlonFay）DisplayName 写的就是本尊的 i18n 键，没装汉化时屏幕上一个是中文一个是英文，
    /// 旧口径永远不命中 ⇒ 同一个人列成两张卡（用户实测）。
    /// 组内保持传入顺序（排序后原版在前 ⇒ 本尊当天然赢家）。
    /// </summary>
    private static List<List<PortraitCharacter>> VariantGroups(List<PortraitCharacter> chars)
    {
        var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in chars) parent[c.Id] = c.Id;
        string Find(string id)
        {
            while (parent[id] != id) { parent[id] = parent[parent[id]]!; id = parent[id]!; }
            return id;
        }
        void Union(string a, string b)
        {
            var ra = Find(a); var rb = Find(b);
            if (ra != rb) parent[rb] = ra;
        }
        foreach (var g in chars.GroupBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            // 占位名（"???" / "？？？"这类无字母数字的名字）不合并 —— 那是 mod 故意的
            // 神秘 NPC 命名（RSV 的 RelicSpirit 与 TreehouseGirl 实测：两个不同的角色，
            // 游戏里就显示 ???），并非翻译撞车（用户指正）。
            if (!g.Key.Any(char.IsLetterOrDigit)) continue;
            var head = g.First().Id;
            foreach (var c in g.Skip(1)) Union(head, c.Id);
        }
        foreach (var (aid, mid) in AliasMap(chars)) Union(mid, aid);

        var groups = new List<List<PortraitCharacter>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in chars)
        {
            if (!seen.Add(Find(c.Id))) continue;
            var g = chars.Where(x => string.Equals(Find(x.Id), Find(c.Id), StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (g.Count > 1) groups.Add(g);
        }
        return groups;
    }

    /// <summary>
    /// Mods 目录签名 = ①顶层条目名 ②全部 manifest/content.json/config.json 的
    /// (相对路径, mtime, size) ③全部 .png/.xnb 素材的 (相对路径, size)。
    /// ②③ 都只 stat 不读内容，一次目录遍历拿全。
    /// 为什么必须有 ①③：立绘页认的皮肤有两个来源 —— CP 包（有 json）和 Portraiture 素材包
    /// （<c>Portraiture/Portraits/名字/Emily.png</c>，一个 json 都没有）。旧签名只看 json，
    /// 于是手放/手删/手改这类纯 PNG 素材包完全不动签名，立绘页一直吃旧快照
    ///（2026-09-19 实测：把 TP's Emily Portrait 搬进搬出，界面长时间不变）。
    /// 素材只取 size 不取 mtime：换 mtime 不代表换画，而换画必然改字节数（除非逐字节相同，
    /// 那本来就该命中缓存）。scan-algo 版本号跟扫描语义走，算法变更必须作废旧快照。</summary>
    private static string ModsSignature(string modsDir)
    {
        try
        {
            var parts = new List<string> { "scan-algo:assets-v3" };
            foreach (var e in Directory.EnumerateFileSystemEntries(modsDir))
                parts.Add("T:" + Path.GetFileName(e));
            foreach (var f in new DirectoryInfo(modsDir).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (f.FullName.Contains(".junigrid_trash", StringComparison.OrdinalIgnoreCase)
                    || f.FullName.Contains("junigrid_trash", StringComparison.OrdinalIgnoreCase)) continue;
                var rel = Path.GetRelativePath(modsDir, f.FullName);
                var name = f.Name;
                if (name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("content.json", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("config.json", StringComparison.OrdinalIgnoreCase))
                {
                    // 结构文件带 mtime：config.json 被改写（哪怕字节数没变）就是角色开关变了
                    parts.Add($"J:{rel}|{f.LastWriteTimeUtc.Ticks}|{f.Length}");
                }
                else if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                      || name.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add($"A:{rel}|{f.Length}");
                }
            }
            parts.Sort(StringComparer.Ordinal);
            using var sha = System.Security.Cryptography.SHA1.Create();
            return Convert.ToHexString(sha.ComputeHash(
                System.Text.Encoding.UTF8.GetBytes(string.Join("\n", parts))));
        }
        catch { return ""; }
    }

    /// <summary>尝试读快照：签名一致才命中。损坏/版本不符静默返回 null 走重扫。</summary>
    private PortraitScanResult? TryLoadScanCache(string gamePath, out string sig)
    {
        sig = string.IsNullOrWhiteSpace(gamePath) ? "" : ModsSignature(Path.Combine(gamePath, "Mods"));
        if (sig.Length == 0) return null;
        try
        {
            if (!File.Exists(ScanCachePath)) return null;
            var j = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(ScanCachePath));
            // 必须连目录一起认：签名只覆盖 Mods 那棵树，两个不同目录算出同一个签名是可能的
            // （测试台跑沙箱扫描就会把真实目录的快照顶掉 —— 实测立绘页因此整页空）。
            var cachedGame = j["game"]?.ToString();
            if (!string.IsNullOrWhiteSpace(cachedGame)
                && !string.Equals(Path.GetFullPath(cachedGame).TrimEnd('\\', '/'),
                    Path.GetFullPath(gamePath).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                return null;
            if (string.Equals(j["sig"]?.ToString(), sig, StringComparison.Ordinal)
                && j["scan"] is not null)
            {
                var scan = j["scan"]!.ToObject<PortraitScanResult>(
                    new Newtonsoft.Json.JsonSerializer
                    {
                        ContractResolver = new Newtonsoft.Json.Serialization.DefaultContractResolver(),
                        TypeNameHandling = Newtonsoft.Json.TypeNameHandling.Auto,
                        ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore,
                    });
                if (scan is { Characters.Count: > 0 })
                {
                    AppLog.Warn("Portraits", "扫描快照命中（签名一致），跳过重扫");
                    return scan;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Portraits", "扫描快照读取失败（走重扫）: " + ex.Message);
        }
        return null;
    }

    /// <summary>写快照（扫描完成后调用）。失败静默 —— 缓存永远不能拖垮主流程。</summary>
    private static void SaveScanCache(string gamePath, PortraitScanResult scan, string sig)
    {
        try
        {
            Directory.CreateDirectory(StoragePaths.AppDataDir);
            var obj = new Newtonsoft.Json.Linq.JObject
            {
                ["sig"] = sig,
                ["game"] = gamePath,
                ["time"] = DateTime.Now.ToString("O"),
                ["scan"] = Newtonsoft.Json.Linq.JToken.FromObject(scan)
            };
            File.WriteAllText(ScanCachePath, obj.ToString(Newtonsoft.Json.Formatting.None));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Portraits", "扫描快照写入失败: " + ex.Message);
        }
    }

    /// <summary>变体名归并到主 id 后的字典重构。</summary>
    private static Dictionary<string, List<string>> MergeByResolved(
        Dictionary<string, List<string>> raw, Func<string, string?> resolve)
    {
        var merged = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, files) in raw)
        {
            var r = resolve(id);
            if (r is null) continue;
            if (!merged.TryGetValue(r, out var list)) merged[r] = list = new();
            foreach (var f in files)
                if (!list.Contains(f, StringComparer.OrdinalIgnoreCase)) list.Add(f);
        }
        return merged;
    }

    private static bool IsNoPortraitsSource(string file) =>
        file.Replace('\\', '/').Contains("/NoPortraits/", StringComparison.OrdinalIgnoreCase);

    /// <summary>NoSprites 占位（纯色块，角色没有实际行走贴图）—— 与 NoPortraits 同理过滤。</summary>
    private static bool IsNoSpritesSource(string file)
    {
        var norm = file.Replace('\\', '/');
        // SDS 用 Empty.png 当占位精灵（Load 后再 EditImage 叠真图），与 NoSprites 同理剔除
        if (norm.EndsWith("/Empty.png", StringComparison.OrdinalIgnoreCase)) return true;
        return norm.Contains("/NoSprites/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// v1.3.3b：立绘有效性检测 —— 全透明占位（游戏里根本没有对话立绘，显示出来
    /// 一块空框）判为无效。
    /// v1.3.4：回退"非方形→无效"判定 —— 用户的自定义肖像存在非方形尺寸（半身像/
    /// 宽幅构图），一刀切会误杀用户添加的肖像（实测反馈：新肖像全消失）。只保留
    /// 全透明这一无争议的无效形态。
    /// 解码失败按有效处理（宁多显示不误杀）。
    /// v1.6.8：判定结果进持久探针缓存（portrait-probe-cache.json），冷扫描不再
    /// 全量重复解码。
    /// </summary>
    private static bool IsBlankPortrait(string file)
    {
        var key = ProbeKey("blank", file);
        if (ProbePersist.TryGetValue(key, out var cached)) return cached;
        var verdict = ProbeBlank(file);
        ProbePersist[key] = verdict;
        return verdict;
    }

    private static bool ProbeBlank(string file)
    {
        try
        {
            var tex = PixelKit.DecodePng(file);
            if (tex is null) return false;
            var px = tex.PixelsRgba;
            for (var i = 3; i < px.Length; i += 4)   // RGBA：每 4 字节第 4 位是 A
                if (px[i] > 16) return false;        // 存在可见像素 → 有效立绘
            return true;                              // 全透明 → 无效
        }
        catch { return false; }
    }

    // ── v1.6.8：探针判定持久缓存（跨进程）──
    // ⚠ 键必须带探针种类前缀：IsOpaqueImage（true=实心肖像）与 IsBlankPortrait
    // （true=空白废图）语义相反，共用一个键会互相污染 —— 谁先探测谁定调，
    // 另一个读到反向结论：实心肖像被当"空白"删掉 / 真叠加被当"整表"放进页面（实测）。
    private static readonly ConcurrentDictionary<string, bool> ProbePersist = LoadProbePersist();

    private static string ProbePersistPath =>
        Path.Combine(StoragePaths.AppDataDir, "portrait-probe-cache.json");

    private static string ProbeKey(string kind, string file)
    {
        var fi = new FileInfo(file);
        return $"{kind}|{file}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}";
    }

    private static ConcurrentDictionary<string, bool> LoadProbePersist()
    {
        try
        {
            var p = ProbePersistPath;
            if (!File.Exists(p)) return new();
            var j = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(p));
            var d = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in j)
            {
                // 旧格式（无种类前缀）的键语义不明，直接丢弃
                if (kv.Value?.Type != Newtonsoft.Json.Linq.JTokenType.Boolean) continue;
                if (!kv.Key.StartsWith("opaque|", StringComparison.Ordinal)
                    && !kv.Key.StartsWith("blank|", StringComparison.Ordinal)) continue;
                d[kv.Key] = (bool)kv.Value!;
            }
            return d;
        }
        catch { return new(); }
    }

    /// <summary>写探针持久缓存。v1.6.8：只在本扫描收尾调用一次 —— 每判定一张就全量
    /// 重写 JSON 会产生几百次同步写盘，比解码本身还慢（实测回归）。</summary>
    public static void SaveProbePersist()
    {
        try
        {
            var obj = new Newtonsoft.Json.Linq.JObject();
            foreach (var kv in ProbePersist)
                obj[kv.Key] = kv.Value;
            File.WriteAllText(ProbePersistPath, obj.ToString(Newtonsoft.Json.Formatting.None));
        }
        catch { }
    }

    /// <summary>
    /// v1.3.3b：动物伙伴不进立绘页 —— East Scarp 的 Menagerie 宠物系统（快乐史莱姆、
    /// 女武神狗、鸭鸭等）：立绘+精灵表是动物动画形制（多帧/整表），与人形村民换装
    /// 预览完全不同形，显示出来就是"精灵对不上/整表乱贴"。立绘页定位是村民换装。
    /// 判定：该角色的立绘/精灵来源文件都在 /Menagerie/ 目录下（East Scarp 宠物园
    /// 子系统的统一目录约定）。
    /// </summary>
    private static bool IsMenagerieAnimal(string id, IEnumerable<PackScan> packs)
    {
        List<string>? pFiles = null, sFiles = null;
        foreach (var p in packs)
        {
            if (p.PortraitFiles.TryGetValue(id, out var pf))
                pFiles = (pFiles ?? new()).Concat(pf).ToList();
            if (p.SpriteFiles.TryGetValue(id, out var sf))
                sFiles = (sFiles ?? new()).Concat(sf).ToList();
        }
        var all = (pFiles ?? new()).Concat(sFiles ?? new()).ToList();
        if (all.Count == 0) return false;   // 没有来源信息不判（交给其它门槛）
        // v1.3.9：精灵在 Menagerie 下即判宠物（HappySlime：立绘在 NyapuPortraits、
        // 精灵在 Menagerie，旧判定要求两者都在 → 漏网，精灵预览变成花坛）
        if (sFiles is not null && sFiles.Any(f => f.Replace('\\', '/').Contains("/Menagerie/", StringComparison.OrdinalIgnoreCase)))
            return true;
        return all.All(f => f.Replace('\\', '/').Contains("/Menagerie/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>从整表来源列表里挑默认文件：文件名与角色 id 完全一致的最优先（游戏默认帧），
    /// 沙滩/泳装这类外观变体排最后 —— 防止补丁登记顺序把沙滩照顶成默认（Sunberry 实测）。</summary>
    private static string? PickSource(string? id, List<string>? files)
    {
        if (files is null) return null;
        string? best = null;
        var bestScore = int.MaxValue;
        for (var idx = 0; idx < files.Count; idx++)
        {
            var f = files[idx];
            if (IsNoPortraitsSource(f) || IsNoSpritesSource(f)) continue;
            var fn = Path.GetFileNameWithoutExtension(f);
            var norm = f.Replace('\\', '/');
            var score = idx
                + (id is not null && fn.Equals(id, StringComparison.OrdinalIgnoreCase) ? -1000 : 0)
                + (fn.Contains("beach", StringComparison.OrdinalIgnoreCase)
                   || fn.Contains("swim", StringComparison.OrdinalIgnoreCase) ? 500 : 0)
                // NyapuPortraits/AlternativeTextures 是 mod 自带的「可选画风」（门控加载），
                // 不是默认像 —— 有正规文件时垫底（Juliet/Eloise 默认像被可选画风顶替，实测）
                + (norm.Contains("/NyapuPortraits/", StringComparison.OrdinalIgnoreCase)
                   || norm.Contains("/AlternativeTextures/", StringComparison.OrdinalIgnoreCase) ? 800 : 0);
            if (score < bestScore) { bestScore = score; best = f; }
        }
        return best;
    }

    /// <summary>为动态季节资源找一张稳定的代表图。
    /// 常见结构是 assets/Portraits/&lt;角色&gt;/&lt;角色&gt;_Spring.png；只在 content.json
    /// 无法静态解析出来源时调用，避免把普通包的非目标素材误当作肖像。</summary>
    private static string? FindCharacterAsset(string packRoot, string assetKind, string charId)
    {
        try
        {
            var characterDir = Path.Combine(packRoot, "assets", assetKind, charId);
            if (Directory.Exists(characterDir))
            {
                return Directory.EnumerateFiles(characterDir, "*.png", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
            }

            var assetDir = Path.Combine(packRoot, "assets", assetKind);
            if (!Directory.Exists(assetDir)) return null;
            return Directory.EnumerateFiles(assetDir, charId + "_*.png", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private static string? VanillaPortraitXnb(string gamePath, string id)
    {
        return VanillaAssetXnb(gamePath, "Portraits", id);
    }

    /// <summary>原版精灵表（走动小人）：Content/Characters/&lt;id&gt;.xnb。</summary>
    private static string? VanillaSpriteXnb(string gamePath, string id)
    {
        return VanillaAssetXnb(gamePath, "Characters", id);
    }

    private static string? VanillaAssetXnb(string gamePath, string dir, string id)
    {
        foreach (var alias in VanillaAssetAliases(id))
        {
            var p = Path.Combine(gamePath, "Content", dir, alias + ".xnb");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    // ══════════════════════ content.json 解析 ══════════════════════

    /// <summary>解析一个 content 文件（主 content.json 或 Include 链上的任意子 json）。
    /// inheritedWhenKeys：Include 处挂的 When 布尔键要遗传给子 patch（SCC 的
    /// SlightlyCuterAbigail 这类开关写在 Include 上，不遗传则 config 开关永远写不中）。
    /// gated：本 patch 是否处于 When 门控之下 —— 影响"娘家包"判定权重（compat 包对
    /// 别家 mod 角色的 Data/Characters 兼容改动是门控的，真娘家是无条件的）。</summary>
    private void ParseContentPack(PackScan pack, string packRoot, string contentFile,
        HashSet<string> nativeCandidates, Dictionary<(string Pack, string Char), int> nativeOwners,
        Dictionary<string, string> displayNames,
        List<string> inheritedWhenKeys, bool gated, HashSet<string> visited, int depth)
    {
        if (depth > 6) return;
        if (!File.Exists(contentFile)) return;
        if (!visited.Add(Path.GetFullPath(contentFile))) return;
        // ⚠ CP 语义：FromFile（含 Include 的）永远相对内容包根目录（content.json 所在），
        // 不是当前子 json 所在目录
        var packDir = packRoot;
        pack.ConfigValues ??= LoadConfigValues(packRoot);
        JObject root;
        try
        {
            using var sr = new StreamReader(contentFile);
            // CP 生态的 content.json 常带 // 注释与尾逗号（East Scarp 实测）。
            // 2026-09-20 对本机 186 个 manifest/content 逐个跑严格 JObject.Parse：0 失败，
            // 且 "值,\n// 注释\n}" 这一形状 Newtonsoft 13.0.3 直接吃下 —— 早先"不容忍尾逗号"
            // 的结论不成立，故去掉了当时的清洗步骤。解析失败仍会让整包在 catch 里静默消失。
            root = JObject.Parse(sr.ReadToEnd());
        }
        catch (Exception ex)
        {
            // v1.3.3：解析失败必须留痕 —— 此前这里是纯 catch { return; }，
            // 整个包静默消失没有任何日志，用户只能看到"立绘页少了角色"无从排查。
            AppLog.Warn("Portraits", $"content 解析失败 {Path.GetFileName(contentFile)}: {ex.Message}");
            return;
        }

        // ConfigSchema：社区惯例布尔键含角色名（ReplaceAbigail）
        if (root["ConfigSchema"] is JObject schema)
            foreach (var k in schema.Properties())
                pack.SchemaKeys.Add(k.Name);

        if (root["Changes"] is not JArray changes) return;
        foreach (var c in changes.OfType<JObject>())
        {
            var action = c["Action"]?.ToString() ?? "Load";

            // When 里的布尔字面量 = config 开关（Include 的会遗传给子 patch）
            var whenKeys = new List<string>(inheritedWhenKeys);
            if (c["When"] is JObject when)
                foreach (var kv in when.Properties())
                    // 带操作符的是条件表达式（"HasMod |contains=xxx": true），不是 config 开关
                    if (kv.Value?.Type == JTokenType.Boolean
                        && !kv.Name.Contains('|') && !kv.Name.Contains(' '))
                        whenKeys.Add(kv.Name);
            var selfGated = gated || whenKeys.Count > 0;

            // Include：大包（SVE）主 content.json 只写 Include，真 patch 在子文件里
            if (action.Equals("Include", StringComparison.OrdinalIgnoreCase))
            {
                var inc = c["FromFile"]?.ToString();
                if (string.IsNullOrWhiteSpace(inc) || inc.Contains("{{")) continue;
                foreach (var part in inc.Split(','))
                {
                    var pat = part.Trim();
                    if (pat.Length == 0) continue;
                    IEnumerable<string> matches;
                    try
                    {
                        var full = Path.Combine(packDir, pat.Replace('/', Path.DirectorySeparatorChar));
                        if (pat.Contains('*') || pat.Contains('?'))
                            matches = Directory.GetFiles(Path.GetDirectoryName(full) ?? packDir,
                                Path.GetFileName(full), SearchOption.TopDirectoryOnly);
                        else
                            matches = File.Exists(full) ? new[] { full } : Array.Empty<string>();
                    }
                    catch { continue; }
                    foreach (var m in matches)
                    {
                        var subPack = new PackScan { Folder = pack.Folder, Name = pack.Name, Uid = pack.Uid };
                        ParseContentPack(subPack, packRoot, m, nativeCandidates, nativeOwners,
                            displayNames, whenKeys, selfGated, visited, depth + 1);
                        Absorb(pack, subPack);
                    }
                }
                continue;
            }

            var fromFile = c["FromFile"]?.ToString();
            if (string.IsNullOrWhiteSpace(fromFile)) fromFile = null;

            var targets = c["Target"] switch
            {
                JArray arr => arr.Select(t => t?.ToString() ?? ""),
                null => Array.Empty<string>(),
                JToken t => (t.ToString() ?? "").Split(','),
            };

            foreach (var targetRaw in targets)
            {
                var target = targetRaw.Trim();
                // v1.3.4：{{ModId}}_ 前缀剥离 —— 角色数据键与立绘资产名归到同一裸名 id
                //（Downtown Zuzu：数据键 {{ModId}}_Callum / 立绘 Portraits/Callum）。
                if (pack.Uid.Length > 0)
                    target = target.Replace("{{ModId}}_", "", StringComparison.OrdinalIgnoreCase)
                                   .Replace("{{ModId}}", "", StringComparison.OrdinalIgnoreCase);
                if (target.Length == 0 || target.Contains("{{") ||
                    target.Contains('*') || target.Contains('?'))
                    continue;

                var slash = target.IndexOf('/');
                if (slash <= 0) continue;
                var prefix = target[..slash];
                var tail = target[(slash + 1)..];
                if (tail.Length == 0 || tail.Contains('*') || tail.Contains('?')) continue;
                // v1.3.6：1.6 子路径资产目标（Sunberry Village："Portraits/AichaSBV/AichaSBV"）——
                // 旧代码见第二个 '/' 直接丢弃，基础立绘/精灵全没登记，只有泳装这类无子路径
                // 变体被吸收成默认（用户实测：桑贝里村满屏泳装 + 20 个 NPC 只剩 5 个）。
                // 归属：首段 = 角色名；token 代换仍传完整 tail（"Assets/{{Target}}.png" 靠它拼路径）。
                string name;
                if (tail.Contains('/'))
                {
                    var segs = tail.Split('/');
                    if (segs.Any(s => s.Length == 0)) continue;
                    name = segs[0];
                }
                else name = tail;

                if (prefix.Equals("Data", StringComparison.OrdinalIgnoreCase) &&
                    (name.Equals("Characters", StringComparison.OrdinalIgnoreCase) ||
                     name.StartsWith("Characters/", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("NPCDispositions", StringComparison.OrdinalIgnoreCase) ||
                     name.StartsWith("NPCDispositions/", StringComparison.OrdinalIgnoreCase)))
                {
                    // 娘家包判定：包里出现 Data/Characters/<id> 或 Data/NPCDispositions/<id>
                    // 字面量，或 Entries 键 / Records 键 / Fields 第 2 元素（SVE 的写法）→
                    // 该包是该角色的娘家包候选。⚠ NPCDispositions 是 1.6 旧式注册 ——
                    // Adventurer's Guild Expanded 只写它、全程不碰 Data/Characters，不认的话
                    // 它的 NPC（Daisy/Daniel/Gabriel/Silly/Zinnia）全被"无娘家"门槛滤掉（实测）。
                    // 无条件 patch 记 rank 0（真娘家）；When 门控的 compat 改动记 rank 1，
                    // 有真娘家时让位（SCC 对 SVE 角色的兼容改动不该抢 SVE 的娘家身份）。
                    // ⚠ 这里是 EditData，不能被下面的 Load-only 门拦掉（SVE 全靠它注册角色）
                    var rank = selfGated ? 1 : 0;
                    if (name.StartsWith("Characters/", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("NPCDispositions/", StringComparison.OrdinalIgnoreCase))
                    {
                        var cid = name.StartsWith("Characters/", StringComparison.OrdinalIgnoreCase)
                            ? name["Characters/".Length..]
                            : name["NPCDispositions/".Length..];
                        if (cid.Length > 0 && !cid.Contains('/'))
                            RememberNative(nativeOwners, pack.Folder, cid, rank);
                    }
                    else
                    {
                        var before = nativeCandidates.Count;
                        CollectDataCharacterIds(c, nativeCandidates, pack, displayNames, packRoot);
                        foreach (var cid in nativeCandidates.Skip(before))
                            RememberNative(nativeOwners, pack.Folder, cid, rank);
                    }
                    continue;
                }

                string aspect;
                if (prefix.Equals("Portraits", StringComparison.OrdinalIgnoreCase)) aspect = "portrait";
                else if (prefix.Equals("Characters", StringComparison.OrdinalIgnoreCase)) aspect = "sprite";
                else if (prefix.Equals("Animals", StringComparison.OrdinalIgnoreCase)) aspect = "animal";
                else continue;

                if (aspect == "sprite")
                {
                    // 精灵表来源：整表 Load，或「整张不透明替换」的 EditImage
                    //（SDS 的精灵就是 EditImage 分年覆盖：Rane.png 当 Year=1、Rane1.png
                    // 之后 —— 只认 Load 会把 Empty.png 占位当精灵，弹窗右侧空白，实测）。
                    // FromArea/ToArea 局部改动与 Overlay 声明不算。
                    var sprIsLoad = action.Equals("Load", StringComparison.OrdinalIgnoreCase);
                    var sprIsEdit = action.Equals("EditImage", StringComparison.OrdinalIgnoreCase);
                    if ((sprIsLoad || sprIsEdit)
                        && c["FromArea"] is null && c["ToArea"] is null && fromFile is not null
                        && !(sprIsEdit && string.Equals(c["Overlay"]?.ToString(), "true", StringComparison.OrdinalIgnoreCase)))
                    {
                        var concrete = ResolveFromFileTokens(fromFile, prefix, tail, pack);
                        if (concrete is not null)
                        {
                            string abs = "";
                            try
                            {
                                abs = Path.GetFullPath(Path.Combine(packDir,
                                    concrete.Replace('/', Path.DirectorySeparatorChar)));
                            }
                            catch { }
                            if (abs.Length > 0 && File.Exists(abs) && !IsNoSpritesSource(abs)
                                && (!sprIsEdit || IsOpaqueImage(abs)))
                            {
                                if (!pack.SpriteFiles.TryGetValue(name, out var slist))
                                    pack.SpriteFiles[name] = slist = new();
                                if (!slist.Contains(abs, StringComparer.OrdinalIgnoreCase)) slist.Add(abs);
                                RecordVariant(pack, "Characters", name, abs);
                            }
                        }
                    }
                    pack.SpriteChars.Add(name);
                    if (whenKeys.Count > 0) RememberWhenKeys(pack, name, whenKeys);
                    continue;
                }

                // portrait / animal：整表 Load 或「整张不透明替换」的 EditImage 才算换肤。
                // ⚠ EditImage 缺省是 Overlay 叠加 —— 对话粉底、电视节目、地图包之类对全角色
                // 立绘的装饰性叠加全是这个形态，直接算皮肤会满屏不相关皮肤（实机用户反馈）；
                // 但 Nyapu 这类肖像美化包也用无参 EditImage 整张替换（实测 95 个目标一张补丁，
                // 用户反馈"下载的肖像不显示"）。鉴别信号：替换图不透明像素占比 ≥90%
                //（装饰叠加图大量透明）。
                var isLoad = action.Equals("Load", StringComparison.OrdinalIgnoreCase);
                var isEdit = action.Equals("EditImage", StringComparison.OrdinalIgnoreCase);
                if (!isLoad && !isEdit) continue;
                // FromArea/ToArea = 局部改图（节日表情差分等），同样不是换肤
                if (c["FromArea"] is not null || c["ToArea"] is not null) continue;
                // 明确声明 Overlay:true → 作者自己承认是装饰叠加
                if (isEdit && string.Equals(c["Overlay"]?.ToString(), "true", StringComparison.OrdinalIgnoreCase))
                    continue;

                // 动态 FromFile 先试代换常见 token（SCC 这类大包整表用
                // {{TargetPathOnly}}/{{TargetWithoutPath}} 拼路径），代换不出就只让角色出现
                //（缩略图走占位/回退）
                if (fromFile is not null)
                {
                    var concrete = ResolveFromFileTokens(fromFile, prefix, tail, pack);
                    if (concrete is not null)
                    {
                        string abs = "";
                        try
                        {
                            abs = Path.GetFullPath(Path.Combine(packDir,
                                concrete.Replace('/', Path.DirectorySeparatorChar)));
                        }
                        catch { }
                        // v1.6.6：FromFile 指向的文件不存在 → 静默跳过会让整包"消失"无从排查
                        //（转换包 FromFile 写错时实测）。留一条痕，与 [EditImage跳过] 同级。
                        if (abs.Length == 0 || !File.Exists(abs))
                            AppLog.Warn("Portraits",
                                $"[FromFile缺失] {pack.Folder}: {name} ← {concrete}（文件不存在，忽略该条）");
                        if (abs.Length > 0 && File.Exists(abs))
                        {
                            // EditImage 还须「整张不透明替换」才算换肤；装饰叠加直接跳过，
                            // 也不登记 PortraitFiles 键（避免装饰包把角色凭空带上页）。
                            // 跳过时记日志 —— 肖像美化包被误判时用户报"包不显示"有迹可查。
                            if (isEdit && !IsOpaqueImage(abs))
                            {
                                AppLog.Warn("Portraits",
                                    $"[EditImage跳过] {name} ← {Path.GetFileName(abs)}（弱透明叠加，非整张替换）");
                                continue;
                            }
                            if (!pack.PortraitFiles.TryGetValue(name, out var list))
                                pack.PortraitFiles[name] = list = new();
                            if (!list.Contains(abs, StringComparer.OrdinalIgnoreCase)) list.Add(abs);
                            RecordVariant(pack, "Portraits", name, abs);
                        }
                    }
                }
                if (isLoad && !pack.PortraitFiles.ContainsKey(name))
                    pack.PortraitFiles[name] = new();
                if (whenKeys.Count > 0) RememberWhenKeys(pack, name, whenKeys);
            }
        }
    }

    private static void RememberWhenKeys(PackScan pack, string charId, List<string> keys)
    {
        if (!pack.WhenKeys.TryGetValue(charId, out var set))
            pack.WhenKeys[charId] = set = new(StringComparer.OrdinalIgnoreCase);
        set.UnionWith(keys);
    }

    /// <summary>「整张替换图」判定：抽样统计 alpha≥200 的实心像素占比 ≥35%。立绘天然带
    /// 透明背景（脸周围），不透明占比只有 6-7 成（Nyapu 实测 64-72%），不能用不透明率；
    /// 但真替换图的美术像素全是实心的（alpha=255），而装饰性叠加（腮红/色调层）几乎全是
    /// 弱透明像素、实心占比≈0。v1.6.8：结果进持久探针缓存 —— 扫描期间同一文件只解码
    /// 一次，跨进程文件未变也直接取历史判定。</summary>
    private static bool IsOpaqueImage(string path)
    {
        // v1.6.8：探针判定持久缓存 —— 冷扫描（安装/切版本后首次进肖像页）原本要全量
        // 解码几百张 PNG 做 透明度/空白 判定，是进页卡顿的大头。文件没变（路径+修改时间
        // +大小）直接取历史判定，只有新/变文件才解码。键带 "opaque" 种类前缀（见 ProbeKey 注释）。
        var key = ProbeKey("opaque", path);
        if (ProbePersist.TryGetValue(key, out var cached)) return cached;
        var verdict = ProbeOpaque(path);
        ProbePersist[key] = verdict;
        return verdict;
    }

    private static bool ProbeOpaque(string path)
    {
        try
        {
            var tex = PixelKit.DecodePng(path);
            if (tex is null) return false;
            long total = 0, solid = 0;
            for (var y = 0; y < tex.Height; y += 2)
                for (var x = 0; x < tex.Width; x += 2)
                {
                    total++;
                    if (tex.PixelsRgba[(y * tex.Width + x) * 4 + 3] >= 200) solid++;
                }
            return total > 0 && solid * 100 >= total * 35;
        }
        catch { return false; }
    }

    /// <summary>资产名是已知角色 id 的下划线变体（Wizard_Spring → Wizard）时记为变体资产。
    /// VariantId 保持游戏原资产名（ParrotBoy_Winter），BaseId 归到角色 id（Leo）——
    /// 覆盖包按 VariantId 写 Target 才能打中 1.6 Appearance 引用的真实资产。</summary>
    private static void RecordVariant(PackScan pack, string kind, string assetName, string file)
    {
        var us = assetName.IndexOf('_');
        if (us <= 0) return;
        var baseId = CanonCharId(assetName[..us]);
        if (!VanillaNames.ContainsKey(baseId) && !ModNames.ContainsKey(baseId)) return;
        if (!pack.AssetVariants.Any(v => v.Kind == kind && v.VariantId == assetName
                && string.Equals(v.File, file, StringComparison.OrdinalIgnoreCase)))
            pack.AssetVariants.Add((kind, baseId, assetName, file));
    }

    /// <summary>区域内可见像素占比 ≥5% 视为有内容（全透明/近空白返回 false）。</summary>
    private static bool RegionHasPixels(DecodedTexture tex, int x, int y, int w, int h)
    {
        var px = tex.PixelsRgba;
        var vis = 0;
        var threshold = Math.Max(1, w * h / 20);
        for (var yy = y; yy < y + h && yy < tex.Height; yy++)
            for (var xx = x; xx < x + w && xx < tex.Width; xx++)
                if (px[(yy * tex.Width + xx) * 4 + 3] > 16 && ++vis >= threshold) return true;
        return false;
    }

    /// <summary>代换 FromFile 里可静态确定的 CP token（目标已知时是确定值）。
    /// v1.3.4：补上 {{Target}}（完整目标资产名）—— Ridgeside Village 的全部默认立绘
    /// 都写的是 Assets/{{Target}}.png（"Portraits/Aguar" → "Assets/Portraits/Aguar.png"），
    /// 旧版不支持导致默认立绘代换失败记成空，角色唯一来源落到 Beach 差分上
    ///（整村 NPC 显示成泳装）。只处理 Target / TargetPathOnly / TargetWithoutPath /
    /// TargetName 四个；其余 token（季节、config 值…）代换不了返回 null。
    /// 代换结果不再含 {{ }} 才算成功。</summary>
    private static string? ResolveFromFileTokens(string fromFile, string targetPrefix, string targetName,
        PackScan? pack = null)
    {
        var fullTarget = targetPrefix + "/" + targetName;
        // 子路径目标（"AichaSBV/AichaSBV"）的 {{TargetName}} 取末段 —— 与 CP 语义一致
        var lastName = targetName.Contains('/')
            ? targetName[(targetName.LastIndexOf('/') + 1)..] : targetName;
        var s = fromFile
            .Replace("{{TargetPathOnly}}", targetPrefix, StringComparison.OrdinalIgnoreCase)
            .Replace("{{TargetWithoutPath}}", targetName, StringComparison.OrdinalIgnoreCase)
            .Replace("{{TargetName}}", lastName, StringComparison.OrdinalIgnoreCase)
            .Replace("{{Target}}", fullTarget, StringComparison.OrdinalIgnoreCase);
        // v1.3.4：{{ModId}}_ 前缀剥离（与数据键同规则）
        s = s.Replace("{{ModId}}_", "", StringComparison.OrdinalIgnoreCase);
        // {{配置键}}：按包 config.json 的当前值代换（Elle's Cuter Horses 的
        // assets/Horse/{{Horse Skin}}.png → assets/Horse/PintoSilver.png）。
        // 代换不掉的（缺 config 或键不存在）保持原样 → 上层判 null 走不可用卡
        if (s.Contains("{{") && pack?.ConfigValues is { } cfg)
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\{\{\s*([^{}|]+?)\s*\}\}",
                m => cfg.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
        return s.Contains("{{") ? null : s;
    }

    /// <summary>读包 config.json 的当前值（键 → 字符串）。解析失败返回 null，不影响扫描。</summary>
    private static Dictionary<string, string>? LoadConfigValues(string packRoot)
    {
        try
        {
            var p = Path.Combine(packRoot, "config.json");
            if (!File.Exists(p)) return null;
            using var sr = new StreamReader(p);
            if (JObject.Parse(sr.ReadToEnd()) is not JObject o) return null;
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in o.Properties())
                if (kv.Value is not null
                    && kv.Value.Type is not (JTokenType.Object or JTokenType.Array))
                    d[kv.Name] = kv.Value.ToString();
            return d;
        }
        catch { return null; }
    }

    /// <summary>娘家关系记最小 rank：0 = 无条件 Data/Characters patch（真娘家），1 = When 门控的
    /// compat 改动。组装阶段每个角色只认 rank 最小的那个候选包。</summary>
    private static void RememberNative(Dictionary<(string Pack, string Char), int> owners,
        string pack, string charId, int rank)
    {
        if (owners.TryGetValue((pack, charId), out var cur) && cur <= rank) return;
        owners[(pack, charId)] = rank;
    }

    private void Absorb(PackScan into, PackScan from)
    {
        foreach (var (id, files) in from.PortraitFiles)
        {
            if (!into.PortraitFiles.TryGetValue(id, out var list)) into.PortraitFiles[id] = list = new();
            foreach (var f in files)
                if (!list.Contains(f, StringComparer.OrdinalIgnoreCase)) list.Add(f);
        }
        into.SpriteChars.UnionWith(from.SpriteChars);
        foreach (var (id, files) in from.SpriteFiles)
        {
            if (!into.SpriteFiles.TryGetValue(id, out var slist)) into.SpriteFiles[id] = slist = new();
            foreach (var f in files)
                if (!slist.Contains(f, StringComparer.OrdinalIgnoreCase)) slist.Add(f);
        }
        foreach (var (id, keys) in from.WhenKeys)
        {
            if (!into.WhenKeys.TryGetValue(id, out var set)) into.WhenKeys[id] = set = new(StringComparer.OrdinalIgnoreCase);
            set.UnionWith(keys);
        }
        into.SchemaKeys.UnionWith(from.SchemaKeys);
        // 变体资产也是 Include 子文件里登记的（Baechu 的 Code/Wizard.json），不搬就全丢
        foreach (var v in from.AssetVariants)
            if (!into.AssetVariants.Contains(v))
                into.AssetVariants.Add(v);
    }

    /// <summary>Target=="Data/Characters" 的 EditData：Entries 键名 / Records 键 / Fields 第 2 元素
    /// 都是 mod 自带角色的 id 候选（SVE 的写法）。字段名噪音靠「最终必须有立绘」过滤。
    /// 同时抓取 Entries/Records 值与 Fields 覆盖里的 DisplayName —— 汉化包把中文名写在这个
    /// 字段（直接中文或 {{i18n:key}} token，用包内 i18n/zh.json 代换），供立绘页显示。</summary>
    private static void CollectDataCharacterIds(JObject change, HashSet<string> into,
        PackScan? pack, Dictionary<string, string> names, string? packRoot)
    {
        void AddCandidate(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            // v1.3.4：{{ModId}}_ 前缀剥离 —— Downtown Zuzu（SpaceCore 自注册 NPC）的
            // 角色键是 {{ModId}}_Name，而它的立绘 Target 是裸名 Portraits/Name；
            // 两边必须归到同一个 id 才能配对（代换成完整 uid 反而对不上）。
            s = s.Replace("{{ModId}}_", "", StringComparison.OrdinalIgnoreCase)
                 .Replace("{{ModId}}", "", StringComparison.OrdinalIgnoreCase);
            if (s.Contains(' ') || s.Contains('/') || s.Length > 40) return;
            into.Add(s);
        }

        void AddName(string? idRaw, JToken? v)
        {
            if (idRaw is null || v is null) return;
            var raw = v.Type == JTokenType.String
                ? v.ToString()
                : (v is JObject jo ? jo["DisplayName"]?.ToString() : null);
            if (string.IsNullOrWhiteSpace(raw)) return;
            var id = idRaw.Replace("{{ModId}}_", "", StringComparison.OrdinalIgnoreCase)
                          .Replace("{{ModId}}", "", StringComparison.OrdinalIgnoreCase);
            if (id.Length == 0 || id.Contains('/') || id.Length > 40) return;
            string? resolved = raw;
            var m = System.Text.RegularExpressions.Regex.Match(
                raw, @"\{\{\s*i18n:([^{}|]+?)\s*\}\}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var i18n = packRoot is null ? null : LoadPackI18n(packRoot);
                resolved = i18n is not null && i18n.TryGetValue(m.Groups[1].Value.Trim(), out var zh) ? zh : null;
            }
            else if (raw.Contains("{{")) return;   // 其它代换不了的 token（config 等）
            if (string.IsNullOrWhiteSpace(resolved)) return;
            resolved = resolved.Trim();
            if (resolved.Length == 0 || resolved.Length > 40) return;
            names[id] = resolved;
        }

        if (change["Entries"] is JObject entries)
            foreach (var k in entries.Properties())
            {
                AddCandidate(k.Name);
                AddName(k.Name, k.Value);
            }
        if (change["Records"] is JArray records)
            foreach (var r in records)
            {
                if (r is JObject ro)
                {
                    var rid = ro["Key"]?.ToString() ?? ro["Id"]?.ToString();
                    AddCandidate(rid);
                    AddName(rid, ro);
                }
                else if (r is JArray ra && ra.Count > 0) AddCandidate(ra[0]?.ToString());
            }
        if (change["Fields"] is JArray fields)
            foreach (var f in fields)
            {
                if (f is not JArray fa || fa.Count < 2) continue;
                // 1.6 模型格式的 Fields: [角色id, "DisplayName", 中文名] —— 第 2 元素是
                // 字段名而非 id（旧斜串格式的 Fields [行号, id, …] 走原 AddCandidate 逻辑）
                if (string.Equals(fa[1]?.ToString(), "DisplayName", StringComparison.OrdinalIgnoreCase)
                    && fa.Count > 2)
                {
                    AddName(fa[0]?.ToString(), fa[2]);
                    continue;
                }
                AddCandidate(fa[1]?.ToString());
            }
    }

    /// <summary>读包内 i18n 中文词典（zh.json → zh-Hans.json → zh-CN.json），供
    /// DisplayName 的 {{i18n:key}} 代换。结果按包根缓存 —— 同一包扫描期只读一次。</summary>
    private static readonly ConcurrentDictionary<string, Dictionary<string, string>?> I18nCache = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string>? LoadPackI18n(string packRoot)
    {
        return I18nCache.GetOrAdd(packRoot, root =>
        {
            try
            {
                var dir = Path.Combine(root, "i18n");
                if (!Directory.Exists(dir)) return null;
                foreach (var name in new[] { "zh.json", "zh-Hans.json", "zh-CN.json" })
                {
                    var f = Path.Combine(dir, name);
                    if (!File.Exists(f)) continue;
                    using var sr = new StreamReader(f);
                    if (JObject.Parse(sr.ReadToEnd()) is not JObject o) continue;
                    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in o.Properties())
                        if (kv.Value is not null && kv.Value.Type == JTokenType.String)
                            d[kv.Name] = kv.Value.ToString();
                    if (d.Count > 0) return d;
                }
            }
            catch { }
            return null;
        });
    }

    /// <summary>字符串是否含 CJK 汉字（判断 DisplayName 是不是真中文名）。</summary>
    private static bool HasCjk(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        foreach (var ch in s)
            if (ch >= 0x4E00 && ch <= 0x9FFF) return true;
        return false;
    }

    /// <summary>默认立绘的 dHash（差异哈希，64 位）：整图按 9×8 网格采样灰度，
    /// 相邻列比较得 64 位。用于变体判定 —— 同一 NPC 的重绘立绘构图相同，哈希
    /// 汉明距很小；不同 NPC 的立绘构图不同，距离大。结果按文件缓存。</summary>
    /// <summary>解码成 RGBA 后取 SHA1（含宽高）：PNG 与 XNB 画的是同一张图也算同一个。
    /// 解不动返回 null（不参与去重，宁可多一张卡也不能把不同的画并掉）。进程内缓存。</summary>
    private static readonly ConcurrentDictionary<string, string?> ArtHashCache = new(StringComparer.OrdinalIgnoreCase);

    private static string? ArtHash(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return ArtHashCache.GetOrAdd(path, p =>
        {
            try
            {
                DecodedTexture? tex = null;
                if (p.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase)) tex = XnbDecoder.TryDecode(p);
                if (tex is null && File.Exists(p)) tex = PixelKit.DecodePng(p);
                if (tex is null) return null;
                using var sha = System.Security.Cryptography.SHA1.Create();
                var head = BitConverter.GetBytes(tex.Width)
                    .Concat(BitConverter.GetBytes(tex.Height)).ToArray();
                sha.TransformBlock(head, 0, head.Length, null, 0);
                sha.TransformFinalBlock(tex.PixelsRgba, 0, tex.PixelsRgba.Length);
                return Convert.ToHexString(sha.Hash!);
            }
            catch { return null; }
        });
    }

    private static bool SameConfigKeys(string[] a, string[] b)
        => a.Length == b.Length && a.OrderBy(x => x, StringComparer.Ordinal)
               .SequenceEqual(b.OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal);

    // 立绘 + 精灵表两张都一致才算同一份画面：只比立绘会把「同立绘、不同走动小人」的包错并
    private static string? ArtKey(PortraitSkinOption o)
    {
        var a = ArtHash(o.SourceFile);
        var b = ArtHash(o.SpriteFile);
        if (a is null && b is null) return null;
        return (a ?? "-") + "|" + (b ?? "-");
    }

    /// <summary>
    /// 每张卡的两道折叠：① 与「默认像」同一个文件的皮肤行（扩展包增强默认后，
    /// 被并进来的变体默认行常常就是同一个文件 —— 冈瑟/马龙实测）；
    /// ② 跨包「逐像素一致」的同画面合并（作者 CP 版 + 我们转换的素材包 + 手放进
    /// Portraiture 的同一批画），立绘与精灵表两张都一致才并。
    /// ⚠ 不做 dHash 感知折叠：v1.3.9b 那种只看春季格子的判据会误杀季节包（Emily 的
    /// SCCC 被误折，用户实测），跨包"几乎一样"留给用户自己选。
    /// </summary>
    public static void FoldSkinsAgainstDefault(List<PortraitCharacter> characters,
        Func<string, string?> selectedPack, List<(string, string)>? diagnostics = null)
    {
        for (var ci = 0; ci < characters.Count; ci++)
        {
            var ch0 = characters[ci];
            if (ch0.Skins.Count == 0) continue;
            var anchorSrc = ch0.Vanilla?.SourceFile ?? ch0.Native?.SourceFile;
            var keptS = new List<PortraitSkinOption>();
            foreach (var s in ch0.Skins)
            {
                if (string.IsNullOrWhiteSpace(s.SourceFile)) { keptS.Add(s); continue; }
                if (string.Equals(s.SourceFile, anchorSrc, StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics?.Add((ch0.Id, $"皮肤「{s.PackName}」与默认像同文件，已折叠"));
                    AppLog.Warn("Portraits", $"[视觉折叠] {ch0.Id} 皮肤「{s.PackName}」与默认像同文件，折叠");
                    continue;
                }
                keptS.Add(s);
            }
            var sameArt = new List<string>();
            var deduped = DedupeByIdenticalArt(keptS, selectedPack(ch0.Id), sameArt);
            if (sameArt.Count > 0)
            {
                diagnostics?.Add((ch0.Id, $"同画面合并 {sameArt.Count} 条：" + string.Join("、", sameArt)));
                AppLog.Warn("Portraits", $"[同画面合并] {ch0.Id} 折叠 {sameArt.Count} 条：" + string.Join("、", sameArt));
            }
            // ⚠ 比较基准必须是**折叠前**的 Skins.Count：旧代码写成 `deduped.Count != keptS.Count`，
            // 于是"只折叠掉一条、第二轮没再动"时两个数相等 ⇒ 折叠结果整个丢掉，
            // 日志明明打了 [视觉折叠] 而卡上那张重复格子还在（冈瑟 5 格，用户："一模一样还不给去重"）。
            // 同理必须把 ch0 换成折叠后的实例再写回 —— 后续季节清理等判断要用新行集。
            if (deduped.Count != ch0.Skins.Count)
                characters[ci] = ch0 with { Skins = deduped };
        }
    }

    /// <summary>把画面逐像素相同的多个包并成一条，其余记进 Dupes。
    /// 用户当前选中的那份永远当幸存者 —— 换掉会让选中框消失，还可能被配置自愈清掉选择。</summary>
    private static List<PortraitSkinOption> DedupeByIdenticalArt(
        List<PortraitSkinOption> skins, string? selectedPack, List<string>? dropped = null)
    {
        if (skins.Count < 2) return skins;
        var slots = new List<(PortraitSkinOption Opt, List<string> Dupes, List<string> Near)>();
        var byKey = new Dictionary<string, int>(StringComparer.Ordinal);
        dropped ??= new List<string>();
        foreach (var s in skins)
        {
            var key = ArtKey(s);
            if (key is null) { slots.Add((s, new(), new())); continue; }
            // 同画面但 config.json 开关不同 ⇒ 不算可互换：选中时要写的键不一样，
            // 并掉就等于让用户再也开不了那个包的按角色开关。
            if (byKey.TryGetValue(key, out var same) && !SameConfigKeys(slots[same].Opt.ConfigKeys, s.ConfigKeys))
                key = key + "#" + s.PackFolder;
            if (!byKey.TryGetValue(key, out var at))
            {
                byKey[key] = slots.Count;
                slots.Add((s, new(), new()));
                continue;
            }
            var cur = slots[at];
            var keepNew = string.Equals(s.PackFolder, selectedPack, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(cur.Opt.PackFolder, selectedPack, StringComparison.OrdinalIgnoreCase);
            if (keepNew)
            {
                cur.Dupes.Add(cur.Opt.PackName);
                slots[at] = (s, cur.Dupes, cur.Near);
            }
            else cur.Dupes.Add(s.PackName);
            dropped.Add($"{s.PackName}（与 {slots[at].Opt.PackName} 同画面）");
        }

        // 第二遍「近似合并」：整张逐像素差异 ≤5% 才并（作者把同一张画重导了一遍、字节不同）。
        // 刻意不用感知哈希 —— v1.3.9b 那种只看春季格子的 dHash 会误杀季节包（Emily 的 SCCC 实测被
        // 折掉）；季节包四格画面不一样，整张比下来差异远超 5%，这条不会重演那次事故。
        var pxCache = new Dictionary<string, DecodedTexture?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < slots.Count; i++)
        {
            for (var j = slots.Count - 1; j > i; j--)
            {
                var a = slots[i];
                var b = slots[j];
                if (!SameConfigKeys(a.Opt.ConfigKeys, b.Opt.ConfigKeys)) continue;
                if (!NearSameArt(a.Opt, b.Opt, pxCache)) continue;
                // 用户当前选中的那份永远当幸存者 —— 换掉会让选中框消失，还可能被配置自愈清掉选择
                var aSel = string.Equals(a.Opt.PackFolder, selectedPack, StringComparison.OrdinalIgnoreCase);
                var bSel = string.Equals(b.Opt.PackFolder, selectedPack, StringComparison.OrdinalIgnoreCase);
                if (bSel && !aSel) (a, b) = (b, a);
                a.Near.AddRange(b.Near);
                a.Near.Add(b.Opt.PackName);
                a.Dupes.AddRange(b.Dupes);
                slots[i] = (a.Opt, a.Dupes, a.Near);
                slots.RemoveAt(j);
                dropped.Add($"{b.Opt.PackName}（与 {a.Opt.PackName} 近似同款）");
            }
        }

        return slots.Select(t => t.Dupes.Count > 0 || t.Near.Count > 0
            ? t.Opt with { Dupes = t.Dupes, NearDupes = t.Near } : t.Opt).ToList();
    }

    /// <summary>近似判据的阈值：整张画里允许 ≤5% 的像素不同。</summary>
    private const int NearArtMaxDiffPercent = 5;

    private static DecodedTexture? DecodeForCompare(string? path, Dictionary<string, DecodedTexture?> cache)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (cache.TryGetValue(path, out var hit)) return hit;
        DecodedTexture? tex = null;
        try
        {
            if (path.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase)) tex = XnbDecoder.TryDecode(path);
            if (tex is null && File.Exists(path)) tex = PixelKit.DecodePng(path);
        }
        catch { tex = null; }
        cache[path] = tex;
        return tex;
    }

    /// <summary>立绘与精灵表两张都近似才算同一份画面；只有一方带精灵表 ⇒ 不算（那是两回事）。</summary>
    private static bool NearSameArt(PortraitSkinOption a, PortraitSkinOption b,
        Dictionary<string, DecodedTexture?> cache)
    {
        if (!NearSamePlane(a.SourceFile, b.SourceFile, cache)) return false;
        if ((a.SpriteFile is null) ^ (b.SpriteFile is null)) return false;
        return a.SpriteFile is null || NearSamePlane(a.SpriteFile, b.SpriteFile, cache);
    }

    /// <summary>整张逐像素比对。尺寸不同直接否 —— 不做缩放对齐，缩放会把真不同的画判成相同。</summary>
    private static bool NearSamePlane(string? pa, string? pb, Dictionary<string, DecodedTexture?> cache)
    {
        var ta = DecodeForCompare(pa, cache);
        var tb = DecodeForCompare(pb, cache);
        if (ta is null || tb is null) return false;
        if (ta.Width != tb.Width || ta.Height != tb.Height) return false;
        if (ta.PixelsRgba.Length != tb.PixelsRgba.Length) return false;
        var diff = 0;
        for (var i = 0; i < ta.PixelsRgba.Length; i += 4)
        {
            if (ta.PixelsRgba[i] != tb.PixelsRgba[i] || ta.PixelsRgba[i + 1] != tb.PixelsRgba[i + 1]
                || ta.PixelsRgba[i + 2] != tb.PixelsRgba[i + 2] || ta.PixelsRgba[i + 3] != tb.PixelsRgba[i + 3])
                diff++;
        }
        var total = ta.PixelsRgba.Length / 4;
        return total > 0 && diff * 100 <= (long)total * NearArtMaxDiffPercent;
    }

    private static readonly ConcurrentDictionary<string, long?> DHashCache = new(StringComparer.OrdinalIgnoreCase);
    private static long? DHash(string path)
    {
        return DHashCache.GetOrAdd(path, p =>
        {
            try
            {
                DecodedTexture? tex = null;
                if (p.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase)) tex = XnbDecoder.TryDecode(p);
                if (tex is null && File.Exists(p)) tex = PixelKit.DecodePng(p);
                if (tex is null) return null;
                const int W = 9, H = 8;
                var gray = new double[W * H];
                for (var gy = 0; gy < H; gy++)
                    for (var gx = 0; gx < W; gx++)
                    {
                        var sx = Math.Min(tex.Width - 1, gx * tex.Width / W);
                        var sy = Math.Min(tex.Height - 1, gy * tex.Height / H);
                        var o = (sy * tex.Width + sx) * 4;
                        // 立绘表透明底按黑算：alpha 低的像素灰度记 0
                        gray[gy * W + gx] = tex.PixelsRgba[o + 3] > 16
                            ? tex.PixelsRgba[o] * 0.299 + tex.PixelsRgba[o + 1] * 0.587
                              + tex.PixelsRgba[o + 2] * 0.114
                            : 0;
                    }
                long hash = 0;
                var bit = 0;
                for (var y = 0; y < H; y++)
                    for (var x = 0; x < W - 1; x++, bit++)
                        if (gray[y * W + x] > gray[y * W + x + 1]) hash |= 1L << bit;
                return hash;
            }
            catch { return null; }
        });
    }

    /// <summary>两个 64 位哈希的汉明距（不同位数）。</summary>
    private static int Hamming(long a, long b)
        => System.Numerics.BitOperations.PopCount((ulong)(a ^ b));

    // ══════════════════════ 选择 → 磁盘同步 ══════════════════════

    /// <summary>选择皮肤（packFolder=null = 回官方/mod 默认）。写配置并同步磁盘。
    /// 变更提示由 UI 负责（「下次启动游戏生效」）。</summary>
    public void SelectSkin(string gamePath, PortraitScanResult scan, string charId, string? packFolder)
    {
        var cfg = _cfg.Current;
        // v1.6.8：左键 = 整体统一 —— 切换全局选择时清除该角色的逐季分配（右键设置的
        // 四季各不同）。旧语义两者叠加：逐季钉入压过全局选择，用户点左键"没反应"。
        // 合并卡（同一个人的几份 NPC 数据）逐成员落配置：游戏里那几个条目才会一起换脸。
        foreach (var id in MemberIds(scan, charId))
        {
            cfg.PortraitSeasonSkins.Remove(id);
            if (packFolder is null)
            {
                cfg.PortraitSkins.Remove(id);
                // 显式回默认：记入名单 —— 覆盖包会拷一份原版立绘压过其它启用包的同名 Load
                //（不记的话启用的 mod 包会赢，游戏里回不到原版）
                if (!cfg.PortraitVanillaDefaults.Contains(id)) cfg.PortraitVanillaDefaults.Add(id);
            }
            else
            {
                cfg.PortraitSkins[id] = packFolder;
                cfg.PortraitVanillaDefaults.Remove(id);
            }
        }
        SyncToDisk(gamePath, scan);
        SyncPortraitureActive(gamePath, scan);
        _cfg.Save(cfg);
    }

    /// <summary>这张卡管着的真实 NPC 条目 id（未合并时就是它自己）。传进来的若是被并入的
    /// 别名卡（页面不会显示它，但清理失效选择那条路会拿它的 id 调进来），一律换成整组成员。</summary>
    private static IReadOnlyList<string> MemberIds(PortraitScanResult scan, string charId)
    {
        var ch = scan.Characters.FirstOrDefault(c =>
            string.Equals(c.Id, charId, StringComparison.OrdinalIgnoreCase)
            || c.Members.Contains(charId, StringComparer.OrdinalIgnoreCase));
        if (ch is null) return new[] { charId };
        if (ch.Members.Count > 0) return ch.Members;
        if (ch.AliasOf is { Length: > 0 } own)
        {
            var win = scan.Characters.FirstOrDefault(c =>
                string.Equals(c.Id, own, StringComparison.OrdinalIgnoreCase));
            if (win is not null && win.Members.Count > 0) return win.Members;
            return new[] { own, ch.Id };
        }
        return new[] { ch.Id };
    }

    /// <summary>同步 Portraiture 框架的 HD 模式开关（config.json 的 "active" 字段）。
    /// Portraiture 素材包的生效状态归框架管（游戏内按 P 切换并被框架记住），但那样
    /// 「选默认」就永远还原不了原版立绘 —— 用户实测反馈。现约定：只要本页有任一角色
    /// 选中 Portraiture 素材包 → 框架切 HDP（高清模式）；一个都没有 → 还原 Vanilla。
    /// 注意 Portraiture 的模式是全局的（它没有按角色开关），多包混合时以 HD 优先。
    /// 只在用户于本页改选择时写入，平时绝不碰框架配置。</summary>
    private void SyncPortraitureActive(string gamePath, PortraitScanResult scan)
    {
        try
        {
            if (scan.PortraitureRoot is null) return;
            var rootDir = Path.Combine(gamePath, "Mods",
                scan.PortraitureRoot.Replace('/', Path.DirectorySeparatorChar));
            var fwDir = Path.GetDirectoryName(rootDir.TrimEnd(Path.DirectorySeparatorChar));
            if (fwDir is null) return;
            var configPath = Path.Combine(fwDir, "config.json");
            if (!File.Exists(configPath)) return;

            // v1.6.6：框架也可能是其它 mod 的【依赖】（如 Seven Deadly Sins 靠它的 API
            // 注册肖像，包在扩展自己的文件夹里）—— 托管目录一个包都没有时，绝不能把
            // HD 模式硬切成 Vanilla，否则依赖 mod 的肖像功能被关死。只有用户真的在
            // 托管目录放了素材包，本页才有仲裁权（选中 → HDP，全不选 → Vanilla）。
            if (!Directory.Exists(rootDir) || !Directory.EnumerateDirectories(rootDir).Any())
                return;

            var want = _cfg.Current.PortraitSkins.Values
                .Any(v => v.StartsWith("Portraiture/", StringComparison.OrdinalIgnoreCase))
                ? "HDP" : "Vanilla";

            JObject root;
            using (var sr = new StreamReader(configPath))
                root = JObject.Parse(sr.ReadToEnd());
            if (string.Equals(root["active"]?.ToString(), want, StringComparison.OrdinalIgnoreCase))
                return;
            root["active"] = want;
            var tmp = configPath + ".junigrid.tmp";
            File.WriteAllText(tmp, root.ToString(Newtonsoft.Json.Formatting.Indented));
            File.Move(tmp, configPath, true);
            AppLog.Warn("Portraits", $"Portraiture 框架模式已切换为 {want}");
        }
        catch (Exception ex)
        { AppLog.Warn("Portraits", "同步 Portraiture 模式失败: " + ex.Message); }
    }

    /// <summary>配置→磁盘单向同步（幂等）：生成/更新 JuniGrid 覆盖包（最高优先级
    /// Load+EditImage 被选中角色的立绘/精灵表），**不再启停任何 mod** ——
    /// "没选中的包禁用"会连功能性 mod 一起废掉（Childhood Sweetheart 还有对话/事件，
    /// 实机用户反馈）。mod 本体的启停完全归 Mods 页管。</summary>
    public void SyncToDisk(string gamePath, PortraitScanResult scan)
    {
        var cfg = _cfg.Current;
        var skins = cfg.PortraitSkins;

        // 迁移旧数据：规范化之前存入的带点【值】（包路径 .[CP] xx → [CP] xx）
        foreach (var k in skins.Keys.ToList())
        {
            var v = skins[k];
            var canon = string.Join('/', v.Split('/').Select(seg => seg.TrimStart('.')));
            if (canon != v) skins[k] = canon;
        }

        // ── 覆盖包：换肤的唯一落盘手段 ──
        WriteOverridePack(gamePath, scan, skins, cfg.PortraitVanillaDefaults);

        // 选中包的 per-char config 开关照写（config.json 是用户设置，不算改 mod 内容）。
        // v1.3.9c：只右键固定了季节、没有全局选中包的角色也要把对应包开关打开 ——
        // Baechu 这类靠 ConfigSchema 按角色 Include 的季节包，开关 false 时
        // Appearance/季节资产整段不生效，覆盖包钉了 Emily_Spring 也没人引用（Emily 实测）。
        var modsDir = Path.Combine(gamePath, "Mods");
        var seasonEnabled = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (chId, raw) in cfg.PortraitSeasonSkins)
        {
            foreach (var seg in raw.Split('␟'))
            {
                var i = seg.IndexOf(':');
                if (i <= 0) continue;
                var pack = seg[(i + 1)..];
                if (pack.Length == 0 || pack.StartsWith("Portraiture/", StringComparison.OrdinalIgnoreCase)) continue;
                if (!seasonEnabled.TryGetValue(pack, out var set))
                    seasonEnabled[pack] = set = new(StringComparer.OrdinalIgnoreCase);
                set.Add(chId);
            }
        }
        var selectedPacks = skins.Values.Concat(seasonEnabled.Keys)
            .Where(f => f.Length > 0 && !f.StartsWith("Portraiture/", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pack in selectedPacks)
        {
            var dir = ResolvePackDir(modsDir, pack);
            if (dir is null) continue;
            var enabled = skins.Where(kv => string.Equals(kv.Value, pack, StringComparison.OrdinalIgnoreCase))
                     .Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (seasonEnabled.TryGetValue(pack, out var seasonChars))
                enabled.UnionWith(seasonChars);
            WritePackConfig(dir, pack, scan, enabledChars: enabled);
        }
    }

    // ══════════════════════ 立绘覆盖包 ══════════════════════

    /// <summary>生成/更新覆盖包：对每个「显式选择」的角色，把对应立绘（和精灵表）拷进
    /// assets 并以最高优先级 Load+EditImage 压过其它启用包的同名补丁（含 Nyapu 这类
    /// 无参 EditImage 整表替换）；显式回默认的角色拷原版立绘。拷贝失败的项直接跳过 ——
    /// 绝不写引用不存在文件的补丁（CP 会报错）。</summary>
    private void WriteOverridePack(string gamePath, PortraitScanResult scan,
        Dictionary<string, string> skins, List<string> vanillaDefaults)
    {
        try
        {
            var modsDir = Path.Combine(gamePath, "Mods");
            var root = Path.Combine(modsDir, OverrideFolder);
            var assets = Path.Combine(root, "assets");
            Directory.CreateDirectory(assets);

            // ── 清理历史遗留的无 `~` / 点前缀覆盖包 ──
            // 旧版目录仍会被 SMAPI 加载；content.json 引用已删 PNG 时 CP 把资产置 null，
            // 绘制循环 ContentLoadException 直接崩（SMAPI-crash.txt 实测）。
            foreach (var legacyName in new[] { LegacyOverrideFolder, "." + LegacyOverrideFolder })
            {
                var legacy = Path.Combine(modsDir, legacyName);
                if (!Directory.Exists(legacy)) continue;
                try
                {
                    ModService.StageExistingToTrash(gamePath, legacy);
                    AppLog.Warn("Portraits", $"[覆盖包] 已回收历史遗留目录 {legacyName}");
                }
                catch (Exception lex)
                {
                    AppLog.Warn("Portraits", $"[覆盖包] 清理遗留目录失败 {legacyName}: {lex.Message}");
                }
            }

            // manifest 只写一次
            var mf = Path.Combine(root, "manifest.json");
            if (!File.Exists(mf))
                File.WriteAllText(mf,
                    "{\"Name\":\"JuniGrid Portrait Overrides\",\"Author\":\"JuniGrid\",\"Version\":\"1.0.0\"," +
                    "\"Description\":\"Portrait overrides managed by JuniGrid. Regenerated automatically.\"," +
                    "\"UniqueID\":\"" + OverrideUid + "\"," +
                    "\"ContentPackFor\":{\"UniqueID\":\"Pathoschild.ContentPatcher\"}}");

            // v1.3.9b：不再「整目录删除重建」—— 游戏运行时 SMAPI/CP 占着覆盖包文件句柄，
            // Directory.Delete 直接抛 IOException 被 catch 吞掉，覆盖包从此再也不更新，
            // 用户当天所有换肤全部无效（实测大坑）。改为：逐文件覆盖写入（游戏运行时
            // 大多数文件可覆盖），删除残留文件失败只记日志不影响其它文件。
            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<string, byte[]> WriteFile = (dest, bytes) =>
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.WriteAllBytes(dest, bytes);
                    expected.Add(Path.GetFullPath(dest).ToUpperInvariant());
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Portraits", $"[覆盖包] 写入 {Path.GetFileName(dest)} 失败（游戏占用？）: {ex.Message}");
                }
            };

            var changes = new List<object>();
            // v1.6.8：目标去重 —— 同一资产多条补丁在部分 CP 版本会触发 Exclusive 冲突
            //（两条 Load 抢同一素材 = 都不生效，Emily_Summer #1/#2 实测）。
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ch in scan.Characters)
            {
                string? sel = null, portraitSrc = null, spriteSrc = null;
                PortraitSkinOption? selectedOpt = null;
                Dictionary<string, string>? selectedSeasonFiles = null;
                var vanillaDefault = false;
                if (skins.TryGetValue(ch.Id, out var s)
                    && !s.StartsWith("Portraiture/", StringComparison.OrdinalIgnoreCase))
                {
                    sel = s;
                    var opt = ch.AllOptions.FirstOrDefault(o =>
                        !o.IsVanilla && string.Equals(o.PackFolder, sel, StringComparison.OrdinalIgnoreCase));
                    if (opt is null && ch.AliasOf is { Length: > 0 } ownId)
                    {
                        // 合并卡的成员自己没这个包的行（那张皮肤是本尊独有的）：借本尊那一行。
                        // 不借就等于 SelectSkin 把选择写进了它名下却解析不出文件 ⇒
                        // 游戏里本尊换脸、替身没换 = 半生效。
                        opt = scan.Characters.FirstOrDefault(c => string.Equals(
                                c.Id, ownId, StringComparison.OrdinalIgnoreCase))
                            ?.AllOptions.FirstOrDefault(o =>
                                !o.IsVanilla && string.Equals(o.PackFolder, sel, StringComparison.OrdinalIgnoreCase));
                    }
                    if (opt is not null)
                    {
                        selectedOpt = opt;
                        portraitSrc = opt.SourceFile;
                        spriteSrc = opt.SpriteFile;
                        // v1.6.8：按角色 id 收集季节文件（支持每角色文件夹布局与冬季
                        // Indoor/Outdoor 拆分 —— Baechu 形态）
                        selectedSeasonFiles = GetSeasonFilesForChar(opt.SourceFile, ch.Id);
                        // v1.3.9b：季节包的 SourceFile 常是「4×2 四季总表」（Baechu 的
                        // Emily.png），钉进覆盖包后游戏里四季都显示总表第一帧 —— 和弹窗
                        // 卡片显示的春装图对不上，被用户误认成别的包（实测）。基础像改用
                        // 卡片显示的那张（春季文件），冬季另有 Emily_Winter 变体钉住。
                        if (GetSeasonFiles(opt.SourceFile).TryGetValue("spring", out var springP))
                            portraitSrc = springP;
                    }
                }
                else if (vanillaDefaults.Contains(ch.Id, StringComparer.OrdinalIgnoreCase))
                {
                    // 显式回默认：拷「默认行」当前来源 —— 原版角色若被扩展包增强
                    // （马龙/法师/冈瑟…），默认行指向扩展包文件（=游戏实际显示）；
                    // 卸载扩展包后重新扫描，默认行回落原版 xnb，恢复原版肖像。
                    // mod 角色拷娘家默认外观。
                    vanillaDefault = true;
                    portraitSrc = ch.Vanilla?.SourceFile
                        ?? (ch.IsVanilla ? VanillaPortraitXnb(gamePath, ch.Id) : ch.Native?.SourceFile);
                    spriteSrc = ch.Vanilla?.SpriteFile
                        ?? (ch.IsVanilla ? VanillaSpriteXnb(gamePath, ch.Id) : ch.Native?.SpriteFile);
                }
                if (portraitSrc is null) continue;

                // v1.6.8：季节包（来源文件同目录/角色文件夹里有 <id>_<季>.png 变体，如
                // Seasonal Baechu）逐季钉入 + Season 条件 —— 静态钉"春季文件"会让游戏里
                // 四季都显示同一张图（实测：Baechu 冬季=春季）。非季节包维持单文件钉入。
                // 游戏真实资产名（Leo → ParrotBoy）—— Target 必须用它，用角色 id 会打空
                var assetId = GameAssetId(ch.Id);
                if (selectedOpt is not null
                    && selectedSeasonFiles is { Count: > 0 })
                {
                    PinOverrideAsset(changes, written, assets, "Portraits", assetId,
                        selectedOpt.SourceFile, selectedSeasonFiles);
                    if (spriteSrc is not null)
                        PinOverrideAsset(changes, written, assets, "Characters", assetId,
                            spriteSrc, GetSeasonFilesForChar(spriteSrc, ch.Id));
                }
                else if (written.Add("Portraits/" + assetId)
                         && CopyAsPng(portraitSrc, Path.Combine(assets, "Portraits", assetId + ".png")))
                {
                    // v1.6.8：只写 EditImage+Replace，不再 Load —— 部分版本 CP 里 Load 是
                    // Exclusive 语义：与源包的 Load 抢同一素材时【两个都不生效】（Zinnia/
                    // RelicSpirit 实测同归于尽）。EditImage 可叠加、后加载者赢，本包 ~ 前缀
                    // 排最后加载 → 用户选择稳赢且永不冲突。
                    changes.Add(new { Action = "EditImage", Target = "Portraits/" + assetId,
                        FromFile = "assets/Portraits/" + assetId + ".png", PatchMode = "Replace" });
                    if (spriteSrc is not null && CopyAsPng(spriteSrc,
                            Path.Combine(assets, "Characters", assetId + ".png"))
                            && written.Add("Characters/" + assetId))
                    {
                        changes.Add(new { Action = "EditImage", Target = "Characters/" + assetId,
                            FromFile = "assets/Characters/" + assetId + ".png", PatchMode = "Replace" });
                    }
                }

                // 变体资产整族钉住（季节外观等）：Baechu 法师走 1.6 Appearance 引用
                // Portraits/Wizard_Spring 等变体资产，而它的变体 Load 被"装 SVE 不生效"
                // 门槛挡掉 → 不补齐，游戏按季节解析外观就指向缺失 → 立绘空白（实机）。
                // 选中皮肤 → 用该包登记的变体文件；显式默认 → 全部钉到原版，不让引用悬空
                var variants = sel is not null
                    ? scan.VariantAssets
                        .Where(v => string.Equals(v.Pack, sel, StringComparison.OrdinalIgnoreCase)
                                    && string.Equals(v.BaseId, ch.Id, StringComparison.OrdinalIgnoreCase))
                        .Select(v => (v.Kind, v.VariantId, File: (string?)v.File))
                    : vanillaDefault
                        ? scan.VariantAssets
                            .Where(v => string.Equals(v.BaseId, ch.Id, StringComparison.OrdinalIgnoreCase))
                            .GroupBy(v => (v.Kind, v.VariantId))
                            // 显式默认：季节变体（Emily_Winter / Emily_Winter_Indoor）必须钉
                            // 该季的原版文件，不能整族钉成基础肖像 —— Baechu 的 Appearance
                            // 会引用 Emily_Winter_Indoor，钉成春装后冬天就永远不换冬衣（Emily 实测）
                            .Select(g => (g.Key.Kind, g.Key.VariantId,
                                File: ResolveSeasonalVariantFile(
                                    g.Key.Kind == "Portraits" ? portraitSrc : spriteSrc, g.Key.VariantId)))
                    : Enumerable.Empty<(string Kind, string VariantId, string? File)>();
                foreach (var v in variants)
                {
                    if (v.File is null) continue;
                    if (written.Add(v.Kind + "/" + v.VariantId)
                        && CopyAsPng(v.File, Path.Combine(assets, v.Kind, v.VariantId + ".png")))
                    {
                        changes.Add(new { Action = "EditImage", Target = v.Kind + "/" + v.VariantId,
                            FromFile = $"assets/{v.Kind}/{v.VariantId}.png", PatchMode = "Replace" });
                    }
                }

                // v1.3.9：每季节独立皮肤 —— 用户给某季指定了包 → 把该季变体资产钉成
                // 那个包的对应文件（借 scan.VariantAssets 拿到 mod 用的确切变体资产名）。
                if (_cfg.Current.PortraitSeasonSkins.TryGetValue(ch.Id, out var ssRaw))
                {
                    var ss = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var seg in ssRaw.Split('␟'))
                    {
                        var i = seg.IndexOf(':');
                        if (i > 0) ss[seg[..i]] = seg[(i + 1)..];
                    }
                    foreach (var (season, pack) in ss)
                    {
                        if (string.Equals(pack, sel, StringComparison.OrdinalIgnoreCase)) continue;   // 与全局一致，无需钉
                        var cap = char.ToUpperInvariant(season[0]) + season[1..].ToLowerInvariant();
                        // pack 为空串 = 原版默认行（用户可把某一季固定回原版样）
                        var opt = pack is { Length: 0 }
                            ? ch.Vanilla ?? ch.Native
                            : ch.AllOptions.FirstOrDefault(o =>
                                string.Equals(o.PackFolder, pack, StringComparison.OrdinalIgnoreCase));
                        if (opt?.SourceFile is null) continue;
                        var seasonP = GetSeasonFiles(opt.SourceFile).TryGetValue(season, out var sp)
                            ? sp : opt.SourceFile;
                        var seasonS = opt.SpriteFile is not null
                            ? GetSeasonFiles(opt.SpriteFile).TryGetValue(season, out var ss2) ? ss2 : opt.SpriteFile
                            : null;
                        // 覆盖包按变体资产名落盘（Portraits/<id>_<Spring> 等），与 mod 的挂载对齐。
                        // 冬变体还有 Emily_Winter_Indoor/_Outdoor（Baechu Appearance 引用），
                        // 只 EndsWith("Winter") 会漏掉 → 冬天仍显示非冬装（Emily 实测）
                        foreach (var v in scan.VariantAssets)
                        {
                            if (!string.Equals(v.BaseId, ch.Id, StringComparison.OrdinalIgnoreCase)) continue;
                            if (!v.VariantId.EndsWith(cap, StringComparison.OrdinalIgnoreCase)
                                && !v.VariantId.Contains("_" + cap, StringComparison.OrdinalIgnoreCase))
                                continue;
                            var file = v.Kind == "Portraits" ? seasonP : seasonS;
                            if (file is null) continue;
                            if (written.Add(v.Kind + "/" + v.VariantId)
                                && CopyAsPng(file, Path.Combine(assets, v.Kind, v.VariantId + ".png")))
                            {
                                changes.Add(new { Action = "EditImage", Target = v.Kind + "/" + v.VariantId,
                                    FromFile = $"assets/{v.Kind}/{v.VariantId}.png", PatchMode = "Replace" });
                            }
                        }
                    }
                }
            }

            var contentArr = new JArray();
            var skippedGhosts = 0;
            foreach (var c in changes)
            {
                var jo = JObject.FromObject(c);
                var from = jo["FromFile"]?.ToString();
                if (string.IsNullOrWhiteSpace(from))
                { skippedGhosts++; continue; }
                var abs = Path.Combine(root, from.Replace('/', Path.DirectorySeparatorChar));
                // 终检：绝不写出引用不存在 PNG 的补丁 —— CP 加载失败会把资产置 null，
                // SMAPI 再拦 null，游戏绘制循环 ContentLoadException → 整局崩溃。
                if (!File.Exists(abs))
                {
                    skippedGhosts++;
                    AppLog.Warn("Portraits", $"[覆盖包] 跳过幽灵补丁 {jo["Target"]} ← {from}（文件不存在）");
                    continue;
                }
                contentArr.Add(jo);
            }
            var content = new JObject(
                new JProperty("Format", "2.0.0"),
                new JProperty("Changes", contentArr));
            // 先写临时文件再替换，避免写一半留下半截 content.json
            var contentPath = Path.Combine(root, "content.json");
            var tmpContent = contentPath + ".junigrid.tmp";
            File.WriteAllText(tmpContent, content.ToString(Newtonsoft.Json.Formatting.Indented));
            File.Move(tmpContent, contentPath, true);
            AppLog.Warn("Portraits", $"[覆盖包] 已重建：{contentArr.Count} 条补丁" +
                (skippedGhosts > 0 ? $"（跳过 {skippedGhosts} 条幽灵引用）" : ""));

            // 覆盖包自身必须启用（用户在 Mods 页禁了它换肤就全体失效 —— 拉回来）
            _mods.SetDisabled(gamePath, OverrideFolder, false);
        }
        catch (Exception ex)
        { AppLog.Warn("Portraits", "覆盖包更新失败: " + ex.Message); }
    }


    /// <summary>把立绘/精灵表源拷成覆盖包里的 PNG（xnb 先解码）。失败返回 false，调用方跳过该项。</summary>
    /// <summary>
    /// v1.6.8：把「基础文件 + 季节变体」钉进覆盖包。有季节变体（&lt;id&gt;_&lt;季&gt;.png）时
    /// 逐季 EditImage + Season 条件（基础图只兜底未被季节图覆盖的季节）；没有季节
    /// 变体时维持 Load+EditImage 单文件钉入。季节包（Baechu 等）由此实现
    /// 游戏内四季正确轮换 —— 静态钉一张图会让四季都显示同一张（实测）。
    /// assetId = 游戏真实资产名（Leo → ParrotBoy），Target 与 FromFile 都用它。
    /// </summary>
    private static void PinOverrideAsset(List<object> changes, HashSet<string> written,
        string assets, string assetKind, string assetId,
        string? baseSrc, Dictionary<string, string>? seasonFiles)
    {
        if (!written.Add(assetKind + "/" + assetId)) return;   // 同目标已钉（Exclusive 冲突防护）
        var target = assetKind + "/" + assetId;
        var seasons = new[] { "spring", "summer", "fall", "winter" };

        if (seasonFiles is { Count: > 0 })
        {
            var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (season, sFile) in seasonFiles)
            {
                var dest = Path.Combine(assets, assetKind, $"{assetId}_{season}.png");
                if (!CopyAsPng(sFile, dest)) continue;
                covered.Add(season);
                changes.Add(new
                {
                    Action = "EditImage",
                    Target = target,
                    FromFile = $"assets/{assetKind}/{assetId}_{season}.png",
                    PatchMode = "Replace",
                    When = new Dictionary<string, string> { ["Season"] = season },

                });
            }
            if (baseSrc is not null)
            {
                var rest = string.Join(", ", seasons.Where(s => !covered.Contains(s)));
                if (rest.Length == 0) return;
                var bDest = Path.Combine(assets, assetKind, assetId + ".png");
                if (!CopyAsPng(baseSrc, bDest)) return;
                changes.Add(new
                {
                    Action = "EditImage",
                    Target = target,
                    FromFile = $"assets/{assetKind}/{assetId}.png",
                    PatchMode = "Replace",
                    When = new Dictionary<string, string> { ["Season"] = rest },

                });
            }
            return;
        }

        if (baseSrc is null) return;
        var d = Path.Combine(assets, assetKind, assetId + ".png");
        if (!CopyAsPng(baseSrc, d)) return;
        changes.Add(new { Action = "EditImage", Target = target,
            FromFile = $"assets/{assetKind}/{assetId}.png", PatchMode = "Replace" });
    }

    private static bool CopyAsPng(string? src, string dest)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(src) || !File.Exists(src)) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            // v1.6.8：目标与源一致（大小+修改时间）→ 跳过复制。切换皮肤时全量重钉
            // 几十张图，绝大多数没变 —— 跳过让重复切换近乎零 IO。
            if (File.Exists(dest)
                && new FileInfo(src).Length == new FileInfo(dest).Length
                && File.GetLastWriteTimeUtc(src) == File.GetLastWriteTimeUtc(dest))
                return true;
            if (src.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase))
            {
                var tex = XnbDecoder.TryDecode(src);
                if (tex is null) return false;
                File.WriteAllBytes(dest,
                    PixelKit.CropScalePng(tex, 0, 0, tex.Width, tex.Height, tex.Width, tex.Height));
                File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(src));
                return true;
            }
            try
            {
                File.Copy(src, dest, overwrite: true);
                File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(src));   // 供下次跳过判定
            }
            catch (IOException) when (File.Exists(dest))
            {
                // 目标被游戏进程锁住 → 读源字节后覆盖写（多数情况下可成功）
                File.WriteAllBytes(dest, File.ReadAllBytes(src));
                File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(src));
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>解析包在磁盘上的真实目录：优先规范化无点名，回退末段带 . 前缀的禁用名。</summary>
    private static string? ResolvePackDir(string modsDir, string pack)
    {
        var parts = pack.Replace('\\', '/').Split('/');
        var parent = parts.Length == 1
            ? modsDir
            : Path.Combine(modsDir, string.Join(Path.DirectorySeparatorChar, parts, 0, parts.Length - 1));
        var clean = Path.Combine(parent, parts[^1]);
        if (Directory.Exists(clean)) return clean;
        var alt = Path.Combine(parent, "." + parts[^1]);
        return Directory.Exists(alt) ? alt : null;
    }

    /// <summary>写包的 config.json：本包被选中角色的开关 → true；包内其它角色的开关 → false
    /// （社区惯例布尔键含角色名，如 ReplaceAbigail）。原子替换，失败只记日志不炸页面。</summary>
    private static void WritePackConfig(string packDir, string pack, PortraitScanResult scan,
        HashSet<string> enabledChars)
    {
        try
        {
            var path = Path.Combine(packDir, "config.json");
            JObject root;
            if (File.Exists(path))
            {
                using var sr = new StreamReader(path);
                root = JObject.Parse(sr.ReadToEnd());
            }
            else root = new JObject();

            var changed = false;
            foreach (var (key, chars) in scan.ConfigKeys)
            {
                if (!string.Equals(key.Pack, pack, StringComparison.OrdinalIgnoreCase)) continue;
                var want = enabledChars.Contains(key.Char);
                foreach (var k in chars)
                {
                    if (root[k] is { Type: JTokenType.Boolean } tok && (bool)tok == want) continue;
                    root[k] = want;
                    changed = true;
                }
            }
            if (!changed) return;
            var tmp = path + ".junigrid.tmp";
            File.WriteAllText(tmp, root.ToString(Newtonsoft.Json.Formatting.Indented));
            File.Move(tmp, path, true);
        }
        catch (Exception ex)
        { AppLog.Warn("Portraits", $"写 {pack}/config.json 失败: {ex.Message}"); }
    }

    // ══════════════════════ 缩略图（磁盘缓存 + data URI） ══════════════════════

    private const string CacheVersion = "v7";   // 裁剪规则变了必须升版本

    private static string CacheDir => StoragePaths.InCache("portrait-covers");

    private readonly ConcurrentDictionary<string, string?> _memory = new();
    private readonly ConcurrentDictionary<string, byte> _generating = new();
    private readonly ConcurrentQueue<string> _memoryOrder = new();
    private const int MemoryCap = 1024;

    /// <summary>有新缩略图生成完成时触发（页面订阅后刷新渲染，防抖在服务内做）。</summary>
    public event Action? Changed;
    private int _notifyPending;

    private void NotifyChanged()
    {
        if (Interlocked.Exchange(ref _notifyPending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(400);
            Interlocked.Exchange(ref _notifyPending, 0);
            try { Changed?.Invoke(); } catch { }
        });
    }

    /// <summary>缓存键 = 版本 + 种类 + 源路径 + mtime + size（源文件更新自动失效）。
    /// ⚠ 不含"卡片/弹窗"这类范围——同一来源文件渲染结果相同，必须共享缓存，
    /// 否则打开弹窗时每个格子都要重新生成，先出一片占位图（实机用户反馈）。</summary>
    private static string CoverKey(ThumbKind kind, string? src)
    {
        string srcPart;
        try
        {
            srcPart = src is null ? "none"
                : $"{File.GetLastWriteTimeUtc(src):yyyyMMddHHmmss}-{new FileInfo(src).Length}";
        }
        catch { srcPart = "err"; }
        return $"{CacheVersion}|{kind}|{srcPart}";
    }

    /// <summary>角色卡片封面 = 当前生效大头照（选中包 → 原版 xnb → 娘家包）。未就绪返回 null。</summary>
    public string? GetCharacterCover(string gamePath, PortraitCharacter ch, string? selectedPackFolder)
    {
        string? src = null;
        if (selectedPackFolder is not null)
        {
            var opt = ch.AllOptions.FirstOrDefault(o =>
                string.Equals(o.PackFolder, selectedPackFolder, StringComparison.OrdinalIgnoreCase) && !o.IsVanilla);
            if (opt is not null) src = opt.SourceFile;
        }
        if (src is null && ch.IsVanilla) src = VanillaPortraitXnb(gamePath, ch.Id);
        if (src is null) src = ch.Native?.SourceFile;
        // 娘家检测没中（注册形式没解析到）但有皮肤来源的 mod 角色：用皮肤来源兜底
        if (src is null) src = ch.Skins.Select(s => s.SourceFile).FirstOrDefault(s => s is not null);
        if (src is null) return null;
        return RequestThumb(CoverKey(ThumbKind.Portrait, src), ThumbKind.Portrait, src);
    }

    /// <summary>弹窗右侧的精灵图预览（整张表，等比缩到 ≤720）。</summary>
    public string? GetSpriteThumb(PortraitSkinOption skin) =>
        skin.SpriteFile is null
            ? null
            : RequestThumb(CoverKey(ThumbKind.Sprite, skin.SpriteFile), ThumbKind.Sprite, skin.SpriteFile);

    /// <summary>读取包目录的 .junigrid.json 里的 Nexus mod id（mod 名点击进详情页用）。</summary>
    public static int? GetNexusModId(string gamePath, string? packFolder)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || string.IsNullOrWhiteSpace(packFolder)) return null;
        try
        {
            var packDir = Path.Combine(gamePath, "Mods",
                packFolder.Replace('/', Path.DirectorySeparatorChar));
            // ① 安装边车（应用内安装时写入的 nexusModId）
            var sidecar = Path.Combine(packDir, ".junigrid.json");
            if (File.Exists(sidecar))
            {
                var j = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(sidecar));
                if (j["nexusModId"]?.ToObject<int?>() is int sid) return sid;
            }
            // ② v1.6.8：manifest UpdateKeys —— 外部安装的包（如 Nyapu 变体）靠它关联
            // Nexus，之前只查边车会让这类包在肖像页永远打不开详情页（实测）
            var mf = Path.Combine(packDir, "manifest.json");
            if (File.Exists(mf))
            {
                var root = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(mf));
                if (root["UpdateKeys"] is Newtonsoft.Json.Linq.JArray arr)
                    foreach (var uk in arr)
                    {
                        var s = uk?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)uk! : "";
                        if (!s.StartsWith("Nexus:", StringComparison.OrdinalIgnoreCase)) continue;
                        var idPart = s[6..].Split('@')[0].Trim();
                        if (int.TryParse(idPart, out var id)) return id;
                    }
            }
        }
        catch { }
        return null;
    }

    /// <summary>设置某季节的独立皮肤（packFolder=null 清除该季，回全局选择）。写盘重建覆盖包。</summary>
    public void SetSeasonSkin(string gamePath, PortraitScanResult scan, string charId, string season, string? packFolder)
    {
        // 与 SelectSkin 同理：合并卡的一季选择要落到这个人的全部 NPC 条目上
        foreach (var id in MemberIds(scan, charId))
        {
            var cur = _cfg.Current.PortraitSeasonSkins.TryGetValue(id, out var v) ? v : "";
            var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var seg in cur.Split('␟'))
            {
                var i = seg.IndexOf(':');
                if (i > 0) parts[seg[..i]] = seg[(i + 1)..];
            }
            if (packFolder is null) parts.Remove(season);
            else parts[season] = packFolder;
            if (parts.Count == 0) _cfg.Current.PortraitSeasonSkins.Remove(id);
            else _cfg.Current.PortraitSeasonSkins[id] = string.Join("␟", parts.Select(kv => kv.Key + ":" + kv.Value));
        }
        _cfg.Save(_cfg.Current);
        SyncToDisk(gamePath, scan);
    }

    /// <summary>解析角色的每季皮肤表（␟ 分隔串 → 季节→包）。
    /// 空包名 = 显式固定到默认/原版行（PackFolder 空串）—— 右键「默认」卡写入的
    /// "winter:" 必须保留，否则冬天永远回不到原版冬装（Emily 实测）。</summary>
    public static Dictionary<string, string> GetSeasonSkins(JuniGridConfig cfg, string charId)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (cfg.PortraitSeasonSkins.TryGetValue(charId, out var v))
            foreach (var seg in v.Split('␟'))
            {
                var i = seg.IndexOf(':');
                if (i > 0) d[seg[..i]] = seg[(i + 1)..];
            }
        return d;
    }

    /// <summary>弹窗皮肤格缩略图（v2：全部用大头照）。</summary>
    public string? GetSkinThumb(PortraitCharacter ch, PortraitSkinOption skin) =>
        skin.SourceFile is null
            ? null
            : RequestThumb(CoverKey(ThumbKind.Portrait, skin.SourceFile), ThumbKind.Portrait, skin.SourceFile);

    /// <summary>任意路径的立绘缩略图（弹窗季节预览用，与皮肤格同一生成管线/缓存）。
    /// sync=true：缓存未命中时当场生成再返回 —— 弹窗里的图必须立即出现，
    /// 不允许"先空白占位、后台慢慢补"（用户实测换季闪空白）。单张仅几十毫秒。</summary>
    public string? GetPortraitThumbByPath(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? null
            : RequestThumb(CoverKey(ThumbKind.Portrait, path), ThumbKind.Portrait, path, sync: true);

    /// <summary>任意路径的精灵表缩略图（弹窗季节预览用，同步）。</summary>
    public string? GetSpriteThumbByPath(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? null
            : RequestThumb(CoverKey(ThumbKind.Sprite, path), ThumbKind.Sprite, path, sync: true);

    /// <summary>弹窗打开时预生成该角色全部皮肤的四季变体缩略图（后台、去重）——
    /// 用户点季节按钮时全部零等待。</summary>
    public void PrewarmSeasonThumbs(PortraitCharacter ch)
    {
        foreach (var opt in ch.AllOptions)
        {
            // v1.6.8：按角色 id 收集季节文件（每角色文件夹布局/冬季 Indoor 拆分）
            foreach (var f in GetSeasonFilesForChar(opt.SourceFile, ch.Id).Values)
                _ = RequestThumb(CoverKey(ThumbKind.Portrait, f), ThumbKind.Portrait, f);
            foreach (var f in GetSeasonFilesForChar(opt.SpriteFile, ch.Id).Values)
                _ = RequestThumb(CoverKey(ThumbKind.Sprite, f), ThumbKind.Sprite, f);
        }
    }

    /// <summary>变体资产名含季节段时，从基准文件旁取该季源；否则退回基准文件。
    /// 用于显式默认：Emily_Winter_Indoor → Content/Portraits/Emily_Winter.xnb。</summary>
    private static string? ResolveSeasonalVariantFile(string? baseFile, string variantId)
    {
        if (string.IsNullOrWhiteSpace(baseFile)) return baseFile;
        var seasons = GetSeasonFiles(baseFile);
        foreach (var (key, token) in new[]
                 { ("winter", "_Winter"), ("spring", "_Spring"), ("summer", "_Summer"), ("fall", "_Fall") })
        {
            if (variantId.Contains(token, StringComparison.OrdinalIgnoreCase)
                && seasons.TryGetValue(key, out var f))
                return f;
        }
        return baseFile;
    }

    /// <summary>探测立绘/精灵来源文件的春夏秋冬变体：同目录 <基准名>_spring/_summer/
    /// _fall/_winter.png（大小写不敏感）。覆盖两类实测布局 —— SCCC 的
    /// assets/Portraits/Sophia_Spring.png 与 Sunberry 的 assets/Portraits/&lt;id&gt;/&lt;id&gt;_spring.png，
    /// 都是「同目录 + _季节后缀」。来源文件本身是变体（Sophia_Spring）时先剥回基准名。
    /// 没有的季节无键 —— UI 切换到缺失季节时保持当前图（用户要求）。</summary>
    public static Dictionary<string, string> GetSeasonFiles(string? sourceFile)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(sourceFile)) return result;
        try
        {
            var dir = Path.GetDirectoryName(sourceFile);
            if (dir is null || !Directory.Exists(dir)) return result;
            var stem = Path.GetFileNameWithoutExtension(sourceFile);
            foreach (var suffix in new[] { "_Spring", "_Summer", "_Fall", "_Winter" })
                if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                { stem = stem[..^suffix.Length]; break; }
            foreach (var (key, suffix) in new[]
                     { ("spring", "_Spring"), ("summer", "_Summer"), ("fall", "_Fall"), ("winter", "_Winter") })
            {
                // mod 布局：同目录 png（Sunberry：assets/Portraits/<id>/<id>_spring.png；
                // SCCC 子文件夹：assets/Portraits/Sophia/Sophia_Spring.png）
                var p = Path.Combine(dir, stem + suffix + ".png");
                if (File.Exists(p)) { result[key] = p; continue; }
                var sub = Path.Combine(dir, stem, stem + suffix + ".png");
                if (File.Exists(sub)) { result[key] = sub; continue; }
                // 原版布局：Content/Portraits|Characters/<id>_Winter.xnb —— 原版 1.6 给
                // 30 个村民只做了冬季变体（用户实测：原版艾米丽点冬季没反应）
                var xnb = Path.Combine(dir, stem + suffix + ".xnb");
                if (File.Exists(xnb)) result[key] = xnb;
            }
            // v1.6.8：每角色文件夹布局（Baechu：assets/Portraits/<id>/<id>_<季>[_场景].png）
            // 的冬季拆成了 Indoor/Outdoor 两张、没有 <id>_Winter.png —— 补上：
            // 冬季优先 Indoor（对话多数在室内），Outdoor 由场景 Appearance 走变体资产。
            if (sourceFile is not null && !result.ContainsKey("winter"))
            {
                var ownDir = Path.GetDirectoryName(sourceFile);
                if (ownDir is not null
                    && Path.GetFileName(ownDir).Equals(stem, StringComparison.OrdinalIgnoreCase))
                {
                    var indoor = Path.Combine(ownDir, stem + "_Winter_Indoor.png");
                    var outdoor = Path.Combine(ownDir, stem + "_Winter_Outdoor.png");
                    if (File.Exists(indoor)) result["winter"] = indoor;
                    else if (File.Exists(outdoor)) result["winter"] = outdoor;
                }
            }
        }
        catch { }
        return result;
    }

    /// <summary>
    /// v1.6.8：按「角色 id + 来源文件」收集季节文件 —— 在 GetSeasonFiles（同目录兄弟
    /// 文件）之上，支持共享目录里以 &lt;角色id&gt;_&lt;季&gt; 命名的文件（不限所在目录名），
    /// 以及冬季 Indoor/Outdoor 拆分（Baechu/Childhood Sweetheart 两种布局实测）。
    /// 肖像页弹窗季节预览与覆盖包逐季钉入都走这里，保证预览与游戏内一致。
    /// 角色 id 与资产别名（Leo/ParrotBoy）都扫一遍 —— Nyapu 的季节图叫 ParrotBoy_Winter。
    /// </summary>
    public static Dictionary<string, string> GetSeasonFilesForChar(string? sourceFile, string charId)
    {
        var result = GetSeasonFiles(sourceFile);
        try
        {
            if (string.IsNullOrWhiteSpace(sourceFile) || string.IsNullOrWhiteSpace(charId)) return result;
            var dir = Path.GetDirectoryName(sourceFile);
            if (dir is null || !Directory.Exists(dir)) return result;

            var idCandidates = VanillaAssetAliases(charId).ToList();
            foreach (var (key, suffix) in new[]
                     { ("spring", "_Spring"), ("summer", "_Summer"), ("fall", "_Fall"), ("winter", "_Winter") })
            {
                if (result.ContainsKey(key)) continue;
                foreach (var id in idCandidates)
                {
                    var p = Path.Combine(dir, id + suffix + ".png");
                    if (File.Exists(p)) { result[key] = p; break; }
                    var p2 = Path.Combine(dir, id + suffix + ".xnb");
                    if (File.Exists(p2)) { result[key] = p2; break; }
                }
            }
            // 冬季 Indoor/Outdoor 拆分（Baechu/Childhood Sweetheart 布局）：
            // 优先 Indoor（对话多数在室内），Outdoor 由场景 Appearance 走变体资产
            if (!result.ContainsKey("winter"))
            {
                foreach (var id in idCandidates)
                {
                    var indoor = Path.Combine(dir, id + "_Winter_Indoor.png");
                    var outdoor = Path.Combine(dir, id + "_Winter_Outdoor.png");
                    if (File.Exists(indoor)) { result["winter"] = indoor; break; }
                    if (File.Exists(outdoor)) { result["winter"] = outdoor; break; }
                }
            }
        }
        catch { }
        return result;
    }

    /// <summary>预热：把扫描结果里所有角色的全部皮肤缩略图生成一遍（后台、去重）。
    /// 页面扫描完成后调用一次，打开弹窗时图已就绪。</summary>
    public void PrewarmThumbs(string gamePath, PortraitScanResult scan)
    {
        foreach (var ch in scan.Characters)
        {
            if (ch.Hidden) continue;   // 别名卡不上屏，缩略图没人看
            if (ch.IsVanilla)
            {
                var coverSrc = ch.Vanilla?.SourceFile;
                if (coverSrc is not null)
                    _ = RequestThumb(CoverKey(ThumbKind.Portrait, coverSrc), ThumbKind.Portrait, coverSrc);
            }
            foreach (var opt in ch.AllOptions)
            {
                _ = GetSkinThumb(ch, opt);
                _ = GetSpriteThumb(opt);
            }
            // 角色当前生效的封面（选中包/娘家包来源可能与任何格子不同）
            _ = GetCharacterCover(gamePath, ch, null);
        }
    }

    private string? RequestThumb(string key, ThumbKind kind, string src, bool sync = false)
    {
        if (_memory.TryGetValue(key, out var cached)) return cached;
        // v1.3.4：生成并发限 4 路 —— 算法升级（alg2）后的全量重建是几百张图的
        // 解码+逐帧分析+编码，无限制地 Task.Run 会占满线程池，把整个应用的
        // UI 调度一起拖卡（用户实测"刚开始什么都不显示，过一会儿才好"）。
        if (_generating.TryAdd(key, 0))
        {
            if (sync)
            {
                // 同步路径（弹窗按需取图）：绕开后台队列当场生成。单张几十毫秒，
                // 不占 _thumbGate 名额（避免撞上页面级预热洪峰时把渲染线程卡死）。
                try
                {
                    var uri = GenerateThumbDataUri(key, kind, src);
                    // v1.6.8：失败不写负缓存 —— 瞬时失败（切版本文件占用等）被永久记住
                    // 会让卡片永远空白（实测）。下次渲染自动重试，_generating 去重防惊群。
                    if (uri is not null) Remember(key, uri);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Portraits", "缩略图生成失败: " + ex.Message);
                }
                finally { _generating.TryRemove(key, out _); }
            }
            else
            {
                _thumbGate.Wait();
                try
                {
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            // 后台预热让路给版本切换（同步路径不让：那会冻住渲染线程，
                            // 单张几十毫秒的读盘由切换侧的改名重试兜住）
                            using var lease = ReadLease();
                            var uri = GenerateThumbDataUri(key, kind, src);
                            // v1.6.8：失败不写负缓存（同上），下次预热自动重试
                            if (uri is not null)
                            {
                                Remember(key, uri);
                                NotifyChanged();
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLog.Warn("Portraits", "缩略图生成失败: " + ex.Message);
                        }
                        finally
                        {
                            _generating.TryRemove(key, out _);
                            _thumbGate.Release();
                        }
                    });
                }
                catch
                {
                    _generating.TryRemove(key, out _);
                    _thumbGate.Release();
                    throw;
                }
            }
        }
        return _memory.TryGetValue(key, out var v) ? v : null;
    }

    /// <summary>缩略图生成并发闸（4 路）—— 解码/编码都是 CPU 密集，防线程池饥饿。</summary>
    private static readonly SemaphoreSlim _thumbGate = new(4, 4);

    private void Remember(string key, string? uri)
    {
        _memory[key] = uri;
        _memoryOrder.Enqueue(key);
        while (_memory.Count > MemoryCap && _memoryOrder.TryDequeue(out var old))
            _memory.TryRemove(old, out _);
    }

    private enum ThumbKind { Portrait, Sprite }

    private string? GenerateThumbDataUri(string cacheKey, ThumbKind kind, string src)
    {
        Directory.CreateDirectory(CacheDir);
        // 磁盘缓存文件名 = 键哈希（键已含版本+mtime+size，天然失效正确；哈希也避免
        // 包 Folder 里的 / 出现在文件路径里 —— v1 踩过的坑）。v1.3.4：预览算法升级
        //（空白帧扫描回退）—— 键追加了算法版本号，旧空白缓存图不会命中。
        var cacheFile = Path.Combine(CacheDir, Sha1(cacheKey + "|alg3") + ".png");
        byte[] png;
        if (File.Exists(cacheFile))
        {
            png = File.ReadAllBytes(cacheFile);
        }
        else
        {
            DecodedTexture? tex = null;
            if (src.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase))
                tex = XnbDecoder.TryDecode(src);
            if (tex is null && File.Exists(src))
                tex = PixelKit.DecodePng(src);
            if (tex is null) return null;

            int sx, sy, sw, sh;
            if (kind == ThumbKind.Sprite)
            {
                // 精灵图预览：只取正面站立那一帧（原版布局 4 列，帧宽 = 宽/4，
                // 标准帧 16×32；矮个角色 16×24 —— 矮人/科罗布斯）。整张表全是
                // 各方向行走帧，全显示没有意义（用户反馈）
                // 宽 < 64 装不下 4 列 16px 帧 → 整图就是一帧（SVE 的 GilSprite 16×32 单帧）
                if (tex.Width >= 64)
                {
                    var fw = Math.Max(1, tex.Width / 4);
                    var fh = 2 * fw;
                    if (tex.Height % fh != 0 && tex.Height % (fw * 3 / 2) == 0)
                        fh = fw * 3 / 2;
                    (sx, sy, sw, sh) = (0, 0, fw, fh);
                    // v1.3.4：mod 自定动画表的帧布局未必是 4 列 —— 左上帧可能是空白
                    //（JosephineK/Gwen 弹窗右侧空白）。逐帧找第一个有可见像素的帧；
                    // 全空回退整图缩放。
                    if (!RegionHasPixels(tex, sx, sy, sw, sh))
                    {
                        var found = false;
                        for (var fy = 0; !found && fy + fh <= tex.Height; fy += fh)
                            for (var fx = 0; !found && fx + fw <= tex.Width; fx += fw)
                                if (RegionHasPixels(tex, fx, fy, fw, fh))
                                { (sx, sy) = (fx, fy); found = true; }
                        if (!found) (sx, sy, sw, sh) = (0, 0, tex.Width, tex.Height);
                    }
                }
                else
                {
                    (sx, sy, sw, sh) = (0, 0, tex.Width, tex.Height);
                }
            }
            else
            {
                // 立绘表是 2 列布局（游戏按 宽/2 的方形帧解读，首帧在左上）：
                // 原版 128 宽（64 帧）与 Portraiture/HD 高清表（256/512 宽，帧 128/256）
                // 一律裁左上首帧 —— 与游戏内实际显示完全一致。
                // v1.3.5：v1.3.4 对非 128 宽「整图缩放」是矫枉过正 —— Seven Deadly Sins
                // 全部立绘是 512×512~512×3072 的 2 列高清表，整图贴出来就是一屏碎脸图集；
                // 像素探针证实 Emily/SDS 各文件 (0,0,宽/2,宽/2) 全部有内容。
                // 首帧空白 → 逐帧找第一个有内容的帧；全空回退整图。
                var fw = tex.Width / 2;
                var isSheet = fw >= 64 && tex.Width % 128 == 0 && tex.Height % fw == 0;
                if (isSheet)
                {
                    (sx, sy, sw, sh) = (0, 0, fw, fw);
                    if (!RegionHasPixels(tex, sx, sy, sw, sh))
                    {
                        var found = false;
                        for (var fy = 0; !found && fy + fw <= tex.Height; fy += fw)
                            for (var fx = 0; !found && fx + fw <= tex.Width; fx += fw)
                                if (RegionHasPixels(tex, fx, fy, fw, fw))
                                { (sx, sy) = (fx, fy); found = true; }
                        if (!found) (sx, sy, sw, sh) = (0, 0, tex.Width, tex.Height);
                    }
                }
                else
                {
                    (sx, sy, sw, sh) = (0, 0, tex.Width, tex.Height);
                }
            }

            // 最近邻整数倍缩放（像素风：绝不平滑插值）。立绘/马缩到 ≤128；
            // 精灵帧很小，改为整数倍放大到 ~144 宽（16×32 → 144×288）
            int dw, dh;
            if (kind == ThumbKind.Sprite)
            {
                // 精灵帧很小 → 整数倍「放大」（⚠ dw/dh 是乘法；除法会把 16×32 缩成 1×3 黑块）
                var up = Math.Max(1, Math.Min(144 / Math.Max(1, sw), 512 / Math.Max(1, sh)));
                (dw, dh) = (sw * up, sh * up);
            }
            else
            {
                var down = Math.Max(1, Math.Max(
                    (int)Math.Ceiling(sw / (double)128),
                    (int)Math.Ceiling(sh / (double)128)));
                (dw, dh) = (Math.Max(1, sw / down), Math.Max(1, sh / down));
            }
            png = PixelKit.CropScalePng(tex, sx, sy, sw, sh, dw, dh);
            try { File.WriteAllBytes(cacheFile, png); } catch { }
        }
        return "data:image/png;base64," + Convert.ToBase64String(png);
    }

    private static string Sha1(string s)
    {
        using var sha = SHA1.Create();
        return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s)));
    }
}

/// <summary>PNG 解码 / 裁剪缩放 / PNG 编码（WPF Imaging，编码解码纯后台可用）。缩放一律最近邻保像素风。</summary>
public static class PixelKit
{
    public static DecodedTexture? DecodePng(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            var bmp = System.Windows.Media.Imaging.BitmapDecoder.Create(fs,
                System.Windows.Media.Imaging.BitmapCreateOptions.None,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
            var w = bmp.PixelWidth;
            var h = bmp.PixelHeight;
            if (w is <= 0 or > 8192 || h is <= 0 or > 8192) return null;
            var conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                bmp, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            var bgra = new byte[w * h * 4];
            conv.CopyPixels(bgra, w * 4, 0);
            // BGRA → RGBA（与 XNB 解码同口径）
            for (var i = 0; i < bgra.Length; i += 4)
                (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
            return new DecodedTexture(bgra, w, h);
        }
        catch { return null; }
    }

    /// <summary>裁剪 + 最近邻缩放，输出 PNG 字节。</summary>
    public static byte[] CropScalePng(DecodedTexture tex, int sx, int sy, int sw, int sh, int dw, int dh)
    {
        var outPx = new byte[dw * dh * 4];
        for (var y = 0; y < dh; y++)
        {
            var srow = Math.Min(tex.Height - 1, sy + (int)((double)y * sh / dh));
            for (var x = 0; x < dw; x++)
            {
                var scol = Math.Min(tex.Width - 1, sx + (int)((double)x * sw / dw));
                var si = (srow * tex.Width + scol) * 4;
                var di = (y * dw + x) * 4;
                outPx[di] = tex.PixelsRgba[si];
                outPx[di + 1] = tex.PixelsRgba[si + 1];
                outPx[di + 2] = tex.PixelsRgba[si + 2];
                outPx[di + 3] = tex.PixelsRgba[si + 3];
            }
        }
        // RGBA → BGRA 交给 WPF
        for (var i = 0; i < outPx.Length; i += 4)
            (outPx[i], outPx[i + 2]) = (outPx[i + 2], outPx[i]);
        var bmp = System.Windows.Media.Imaging.BitmapSource.Create(dw, dh, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, outPx, dw * 4);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}
