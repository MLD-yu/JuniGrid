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

    /// <summary>v1.7.7：同一个内容包里的多画风（包用 CP 配置 token 在 assets/&lt;画风&gt;/ 之间
    /// 选一套，Donut's 实测 12 个角色有 2~3 套画）。非 null = 这条卡只代表该包的那一个画风目录，
    /// 界面上单独一张卡、单独可选；null = 整包一条（旧语义，绝大多数包都是这样）。</summary>
    public string? Variant { get; init; }

    /// <summary>v1.7.7：选中这条画风卡时要往源包 config.json 写的那个配置键名（如 AlesiaPortrait）。
    /// 值形态的键写在 ConfigKeys 里（"AlesiaPortrait=Dawn"），这里只留裸键名给排查用。</summary>
    public string? VariantConfigKey { get; init; }

    /// <summary>v1.7.10：画风卡显示在卡名上的那一截。绝大多数情况就等于 <see cref="Variant"/>
    ///（目录段 token 包：画风名本身就是目录名）。例外是"一个布尔开关的两条互斥分支"
    ///（Donut's 的 Wizard：Variant 是要写回 config 的 "true"/"false"，
    /// 而给用户看的是那张脸所在的母目录名 KlevLovins / fifadog —— 写 "· true" 没人看得懂）。</summary>
    public string? VariantLabel { get; init; }

    /// <summary>v1.7.10：这条是不是"包当前生效的那一档"（作者 ConfigSchema 的 Default，
    /// 或 config.json 里已经写好的值）。卡片列表会按包名重排，"默认档排在第一条"这个顺序
    /// 活不下来 ⇒ 用户从没在界面上选过画风时，落盘要靠这个标记认该钉哪一张，
    /// 否则会钉成字母序第一套、而游戏里显示的是作者默认那套（= 半生效）。</summary>
    public bool IsCurrentVariant { get; init; }

    /// <summary>官方「默认」行专用：这一行的图实际来自哪个扩展包（SVE 把法师整表换成马格努斯，
    /// 默认行画的就是马格努斯）。null = 真是游戏原皮。右侧精灵窗借默认行的身体时必须点名，
    /// 否则看上去就是"这张皮肤配了别家 mod 的身体"（实测法师选 RRR 补丁）。</summary>
    public string? DefaultArtPack { get; init; }

    /// <summary>卡名尾巴上那一截（显示用）。</summary>
    public string DisplayVariant => VariantLabel ?? Variant;

    /// <summary>v1.7.29：这张卡的图是 <b>HD 肖像通道</b>的高清表（游戏里由 Portraiture 的 HDP
    /// 模式 / HD Portraits 本体画，走 <c>Mods/HDPortraits/&lt;角色&gt;</c> 那条数据资产，
    /// <b>完全不读</b> <c>Portraits/&lt;角色&gt;</c>）。值 = 单格边长（表宽 ÷ 2，Dacar 是 256）。
    /// null = 普通立绘。落盘必须换一条路：把那条数据资产的 Portrait 指回本包自己的高清资产，
    /// 而不是拿 512 宽的表去做 <c>EditImage Replace</c>（CP 会因超出目标尺寸整条拒绝）。</summary>
    public int? HdCell { get; init; }
}

/// <summary>
/// v1.7 肖像锁定条目：锁定后游戏内该 NPC 四季一律显示 PinFile 那一张图。
/// PackFolder 空串 = 默认/原版行；PinFile 为锁定瞬间预览用的绝对路径（可能失效，
/// 失效时回落 PackFolder 的 SourceFile / 原版 xnb）。
/// </summary>
public sealed record PortraitLockInfo(string PackFolder, string? PinFile, string? Season);

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
    /// <summary>v1.7.20：作者用「同一资产名 + When:{Season}」表达四季的包，取出来的分季映射。
    /// Asset 形如 <c>Portraits/Caroline</c>。覆盖包按文件名找不到季节图时用它 —— 否则会把
    /// 一张图无条件钉满四季（用户报的"春夏秋冬都是同一张"）。</summary>
    public List<(string Pack, string Asset, string Season, string File)> BaseSeasonPatches { get; init; } = new();

    /// <summary>v1.7.10：扫描期间遇到、但我们**判不了**的 CP 条件 / 代换不出的 token
    /// （包名 + 原文）。盲区必须能自己报数：不然用户只看到"这个包的肖像没显示"，
    /// 我们也无从知道是哪一个包、卡在哪一条条件上。--audit-packs 以此汇总。</summary>
    public List<(string Pack, string Cond)> UnknownConditions { get; init; } = new();
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

    /// <summary>v1.7.13：包目录（相对 Mods/、规范化）→ manifest 声明的依赖 UniqueID（声明序）。
    /// 皮肤自己没走路表时按「前置依赖包的身体」解析用；扫描每份 manifest 本来就要解析一遍，
    /// 顺手记下来，比写盘/预览时再重读磁盘便宜。
    /// ⚠ 必须参与序列化（不能 JsonIgnore）：扫描结果会进磁盘快照，写盘路径常在快照命中时跑，
    /// 忽略就会让快照里的依赖图恒空 → 前置身体永远解析不出（实测法师身体没钉）。</summary>
    public Dictionary<string, List<string>> PackDeps { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>v1.7.13：manifest UniqueID → 包目录（规范化）。只含【启用】的包 ——
    /// 禁用的包 SMAPI 不加载，借它的身体等于钉一张游戏里根本不存在的图。
    /// ⚠ 同上，必须参与序列化，否则快照命中后为空。</summary>
    public Dictionary<string, string> PackFolderByUid { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>v1.7.28：HD 肖像通道登记 —— (包目录, HD 数据资产名, 该条对应的角色资产名)。
    /// 数据资产名形如 <c>Mods/HDPortraits/Wizard</c>，渲染端（Portraiture 的 HDP 模式 /
    /// HD Portraits 本体）按它里面的 <c>Portrait</c> 字段画对话框大头照，**完全绕开**
    /// <c>Portraits/&lt;npc&gt;</c>。这类包以前被扫描器按"目标不在 Portraits//Characters/ 命名空间"
    /// 静默丢弃 ⇒ 用户在肖像页看不到它、换任何皮肤都换不掉那张脸
    /// （2026-09-29 实机：法师永远是 [CP] Dacar Rasmodia Portraits 那张高清脸）。
    /// ⚠ 必须参与序列化：覆盖包常在快照命中时写盘，忽略就会让接管逻辑恒不触发。</summary>
    public List<(string Pack, string DataAsset, string Npc)> HdPortraitEntries { get; init; } = new();

    /// <summary>v1.7.28：本机是否装了 HD 肖像【渲染端】。没装的话 <c>Mods/HDPortraits/*</c>
    /// 只是躺在 CP 里没人读的数据资产，覆盖包不该为它写补丁（写了 CP 会报目标不存在）。</summary>
    public bool HdPortraitRendererInstalled { get; set; }

    /// <summary>v1.7.29：HD 肖像通道里【可以当皮肤选】的那些包提供的高清表 ——
    /// (包目录, 角色资产名, HD 数据资产名, 包自己的高清资产名, 表文件绝对路径, 单格边长)。
    /// 由「<c>Load Mods/HDPortraits/&lt;角色&gt;</c> + <c>EditData</c> 的 <c>Entries.Portrait</c>
    /// + <c>Load &lt;那个资产&gt; ← png</c>」三条拼出来，所以必须等所有包都解析完、
    /// 跨包动态 token 能代换之后再算。</summary>
    public List<(string Pack, string Npc, string DataAsset, string PortraitAsset, string File, int Cell)>
        HdSkins { get; init; } = new();

    /// <summary>v1.7.37：同一份立绘/走路表资产上【所有包】的声明（对手高度）。
    /// 键是规范化的资产名（<c>Characters/Wizard</c>、<c>Portraits/Wizard</c>；
    /// SVE 的 <c>Magnus</c> 与原版的 <c>Wizard</c> 并进同一键），值按包逐条列出。
    /// ⚠ 必须参与序列化：这份数据在扫描期才有，落盘/页面上常是快照命中。
    /// ⚠ 纯信息层 —— 落盘判据一行没动（v4 的 A+C 取舍由 B55 守着），只用来回答
    /// "我要钉的这张表比游戏里最高的那张矮多少行"。
    /// 每条各自的声明原文留在 <see cref="RivalSheet.Asset"/> 里（<c>Magnus_Winter</c> 与
    /// <c>Wizard_Winter</c> 并到一键后仍分得清是谁画的）。</summary>
    public Dictionary<string, List<RivalSheet>> RivalSheets { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>一条"整表换掉某份立绘/走路表"的声明（可能来自别的包，也可能是同一包的旧分支）。
/// 用 class 不用 positional record：Newtonsoft 对 record 的反序列化在 net6 上有坑，
/// 而这个字典必须能进磁盘快照（M5 就是验这条）。</summary>
public sealed class RivalSheet
{
    /// <summary>声明方包目录（相对 Mods/、无点号规范化），与 PackScan.Folder 同口径。</summary>
    public string Pack { get; set; } = "";
    /// <summary>补丁声明时的包内资产名原文（<c>Magnus_Winter</c> 这类场合资产在此留痕）。</summary>
    public string Asset { get; set; } = "";
    /// <summary>CP Action 原文（Load / EditImage …）。</summary>
    public string Action { get; set; } = "";
    /// <summary>Priority 原文（未声明为 null）。CP 的 Load 全部先于 EditImage 执行，
    /// 所以这个字段【不能】用来判胜负，只作取证。</summary>
    public string? Priority { get; set; }
    /// <summary>FromFile 原文（可能带 token）。⚠ 必须留：代换不出或文件不存在时 File 是空的，
    /// 原文是唯一还能认出"哪一条补丁"的线索（变异 M2 的用例就靠它点名）。</summary>
    public string? From { get; set; }
    /// <summary>FromFile 代换后【真实存在】的绝对路径；代换不出或文件不存在时为空串。</summary>
    public string File { get; set; } = "";
    public int W { get; set; }
    public int H { get; set; }
    /// <summary>When 原文（null = 无条件）。条件是否成立本服务判不了时不猜，原样留着。</summary>
    public string? When { get; set; }
    /// <summary>true = 这条会把【整张】资产换掉（Load，或无 FromArea/ToArea/Overlay/PatchMode:Overlay
    /// 的 EditImage）。只有 true 的条目参与 Max(H)；false 的保留供取证。</summary>
    public bool FullSheet { get; set; }
}

/// <summary>v1.7.37 立绘页角标：一个资产格（<c>Characters/Jas_Winter</c> 这种算独立一格）上
/// 「本包钉的那张覆盖到第几行 / 游戏里最高那张覆盖到第几行」。行距 32px（走路表固定）。</summary>
public sealed record BodyCoverageAsset(string Key, int OwnRows, int RivalRows);

/// <summary>同一档（我方行数一样、对手也是同几个包）的资产格并成一行显示 ——
/// 底层数据仍按资产，合并只发生在展示层（用户 2026-10-01 拍板：Jas 三条同包同档不该刷三行，
/// 而 Wizard 与 Wizard_Beach 对手不同包必须分开）。<see cref="Assets"/> 保留每一格的真实差口。</summary>
public sealed record BodyCoverageGroup(int OwnRows, string[] RivalPacks, BodyCoverageAsset[] Assets);
