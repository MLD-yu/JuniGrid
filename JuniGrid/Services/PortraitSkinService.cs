using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace JuniGrid.Services;


/// <summary>
/// 立绘页核心服务：扫描 Mods 把 Content Patcher 皮肤包按角色归类、识别 Portraiture 素材包与
/// 娘家包、生成像素风缩略图（磁盘缓存）、并把选择单向同步到磁盘（启禁 mod + 写 config 开关）。
/// v2 规格：每角色只有一个选择；选中某皮肤 = 大头照+精灵图一起切（包里没精灵图就回退原版）。
/// </summary>
public sealed partial class PortraitSkinService
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
    /// <summary>v1.7.28：HD Portraits 本体的 UniqueID —— 与 Portraiture 并列为
    /// <c>Mods/HDPortraits/*</c> 通道的两个渲染端。</summary>
    public const string HdPortraitsUid = "tlitookilakin.HDPortraits";
    /// <summary>覆盖包的落盘语义版本 —— 改了"钉什么/不钉什么"的规则就 +1。启动自检见到
    /// 版本不符会全量重建：否则按旧规则钉进去的条目会一直留在游戏里、永远不自愈
    ///（v1.7.12 = 2：只换脸的皮肤不再借原版/娘家的走路表去钉 Characters，
    /// 实测法师那条旧 Characters/Wizard 光靠"进肖像页"是不会消失的；
    /// v1.7.13 = 3：皮肤没精灵时改按前置依赖包的身体钉，老包里没有这些条目；
    /// v1.7.14 = 4：冬天室内/室外两条一起钉（旧覆盖包只钉了一张，另一地点掉图）——
    /// 老用户不必重选，启动自检按新语义全量重建即补上第二条；
    /// v1.7.16 = 5：锁定且【包自身无走路图】时按 Dependencies 去前置包借身子（未锁定早就会，锁定漏了 →
    /// Donut's Olivia 锁定后身子掉回 SVE 红裙）。只在自身无身子时触发，自带身子的包（艾米丽）不受影响；
    /// v1.7.17 = 6：场合资产加【尺寸闸】—— 拿别的场合/别的季的图顶进 Portraits/卡罗琳_夏季 这类资产时，
    /// 比本尊小就不钉（游戏按固定行距取格会越界 ⇒ 说话框空白、人整个隐身）。实机一次扫出 76 条这种钉法；
    /// v1.7.18 = 7：裸图→CP 转换器的命名规则修好（场合图补 NPC 前缀、Caroline_Vanilla 归基准），
    /// 老覆盖包里钉着转换包那些游戏里不存在的资产名（Characters/Caroline_Vanilla），要重建才换得掉；
    /// v1.7.19 = 8：CP 原生 `PatchMode: "Overlay"` 的叠加层（Seasonal Cute Characters 的鼻子图）
    /// 不再被当整张走路表登记 ⇒ 老覆盖包里那 6 张只有 8 行有像素的 Emily/Victor 冬天图必须重建才掉得掉；
    /// v1.7.20 = 9：「基资产 + When:{Season}」写法的包（Caroline Overhaul 等）现在逐季钉 base+When，
    /// 老覆盖包里那条无条件的 Portraits/卡罗琳 把四季压成同一张，必须重建才换得掉；
    /// v1.7.20b = 10：分季表改成【包自己声明的优先】—— 老写法"只在按文件名找不到时才兜底"会被
    /// 一张"四季都指向同一文件"的退化表挡住（卡罗琳实测：四条钉的是同一张图）；
    /// v1.7.20c = 11：分季钉的落盘文件名改双下划线（Caroline__spring.png）—— 单下划线和
    /// 变体资产那条写的 Caroline_Spring.png 在 Windows 上是同一个物理文件，变体在后 ⇒ 四季钉图
    /// 被覆盖成基础图（卡罗琳实测四条 MD5 全一样）；
    /// v1.7.22 = 12：覆盖包补丁优先级 "Late + 10" → "Late + 100" —— 我们自己早期版本转出的裸图包
    /// （Female Wizard - Sprite And Portrait）每条也写 Late + 10，并列时谁赢只看加载顺序，
    /// 实机法师四季各锁一包却始终显示那包的脸。老覆盖包要重建才换得掉优先级；
    /// v1.7.23 = 13：变体资产（Portraits/Caroline_Winter_Outdoor 这类）改按【包声明的分季表】逐季钉 ——
    /// 旧写法只按文件名猜季节，卡罗琳 Overhaul 的四季文件叫 Spring.png（不带角色名）⇒ 猜不出来 ⇒
    /// 五张变体全钉成同一张，而游戏走 1.6 Appearance 读的就是这些变体资产 ⇒ 冬天仍显示春装；
    /// v1.7.24 = 14：全局档是 Portraiture 素材时【不再跳过整个角色】—— 按季指定的 CP 包照样要钉。
    /// 旧写法一句 continue 让艾米丽四季各选一包却一条补丁都没有（2026-09-28 全量对账查出）；
    /// v1.7.26 = 15：立绘场合资产的替身比本尊小时【补白到本尊尺寸再钉】，不再整条跳过。
    /// 旧写法的后果：那条场合资产没人钉 ⇒ 游戏在那个场合读别家的图，而覆盖包是 Late+100 的整张替换，
    /// 用户看到的就是"我选了 A，游戏里四季全是 B"（实机法师：Portraits/Wizard_Spring…Winter 四条
    /// 全被尺寸闸丢掉 ⇒ 永远显示 Donut 的女法师脸，怎么换都没用，2026-09-28 用户截图）；
    /// v1.7.27 = 16：① 基资产立绘也补满底图（旧写法我方 128×256 盖不住 RomRas 的 128×320 ⇒
    /// 第 5 行永远是它的脸，"无论选什么都蹦出那张脸"）；② 按季那条落盘文件名改双下划线
    /// （单下划线和场合资产的 Caroline_Summer.png 在 Windows 上是同一个物理文件，两条补丁互相
    /// 顶掉 = 卡罗琳夏季钉成春季那张）；③ 全量重建后清掉 assets 里没被任何补丁引用的旧图
    ///（实测积了 505 张，排查时会被误当成"我们钉了这张"）；
    /// v1.7.28 = 17：<b>接管 HD 肖像通道</b> —— 别的包写了 <c>Mods/HDPortraits/&lt;角色&gt;</c>、
    /// 且我们确实钉了 <c>Portraits/&lt;角色&gt;</c> 时，追加一条 <c>EditData</c> 把那条数据资产的
    /// <c>Portrait</c> 指回 <c>Portraits/&lt;角色&gt;</c>、<c>Size</c> 改成我们那张图的格宽。
    /// 不接管的话渲染端（Portraiture 的 HDP 模式 / HD Portraits 本体）照旧画别家的高清脸，
    /// 用户在肖像页选什么都不生效（2026-09-29 实机：法师永远是 [CP] Dacar Rasmodia Portraits）；
    /// v1.7.29 = 18：<b>身体链补最后一档</b> —— 皮肤自己没走路图、整条前置依赖链（含祖先前置）
    /// 也没有时，改钉【默认行】的精灵（原版角色取不到默认行再回落原版 xnb），不再"什么都不钉"。
    /// 旧语义让游戏里留下第三家 mod 配的身体，而弹窗预览一直画的是默认行 ⇒ 界面/落盘/游戏各说一套
    ///（2026-09-29 全量对账：阿比盖尔等 25 格「脸○身—」，用户明确要的是「自己→前置→默认」）；
    /// v1.7.29 = 19：选中 <b>HD 通道</b>的包（只写 Mods/HDPortraits/&lt;角色&gt;、不碰 Portraits/ 那种）
    /// 时改钉 <c>EditData → Portrait 指回本包自己的高清资产</c>，并且【不钉】<c>Portraits/&lt;角色&gt;</c>
    /// —— 那张 512 宽的表当普通立绘钉，CP 会以 "target area extends past the right edge" 整条拒绝。</summary>
    /// v1.7.30 = 20：<b>走路表也上尺寸闸</b> —— 选的包走路表比游戏底图矮时【不钉身子】。
    /// 以前只有"变体资产"那条循环有闸，基资产/按季两条没有 ⇒ 钉一张 64×192 到 64×480 的底图上，
    /// 下面 288 行仍是别人家 mod 的身子（2026-09-29 实机：选了 Seasonal Baechu，法师脸对、小人还是 SVE 的）。
    /// 不能靠补高救：游戏按「纹理高 ÷ 4」算行高，补高会让每一帧都裁错。</summary>
    /// v1.7.31 = 21：那道闸补全剩下四个出口 —— 身体链（前置档 + 默认档）、锁定档、按季档、
    /// PinOverrideAsset 的四季表。20 只闸住了"皮肤自己那张"，链上捞回来的、锁定用的、按季用的
    /// 仍是裸的 ⇒ 同一个人换个入口又矮一次（2026-09-29 对账点名：马格努斯 64×192←前置包、
    /// 奥莉薇亚 64×416←锁定、Haley/卡罗琳/索菲亚 64×{384,224,416}←按季）。</summary>
    /// v1.7.32 = 22：<b>21 那套"矮 ⇒ 当作没身子"整条作废</b>（用户 2026-09-29 判为需求错误：
    /// 换来换去不是他选的那包的身子）。实机双补丁同资产探针证明 CP 先跑所有 Load、再跑 EditImage
    /// 且 Priority 不能跨阶段压制，CP 也没有改小画布的字段 ⇒ 走路表矮于画布时唯一能保住"我选的包"
    /// 的做法是【纵向拉伸铺满画布】（CopyBody），场合资产也不再因矮被丢
    ///（法师冬天读 Characters/Magnus_Winter，旧判据整条不钉 ⇒ 永远是 SCC-SVE 的紫发女巫）。
    /// ⚠ 其中"纵向拉伸"当天就被实机推翻（拉成一个长头），CopyBody 已改回原样拷 —— 见
    /// PortraitSkinService.Override.cs 里 CopyBody 的说明；本版本号保留 22 这个号不回收。</summary>
    /// v1.7.33 = 23：<b>场合资产登记加后缀白名单</b> —— RecordVariant 原先只要"本体名 + 下划线 +
    /// 任意词"就当成 1.6 的场合差分，于是把别人包里的剧情状态图也一起钉了（本机实测 88 条越界，
    /// 78 条是 zLewdDewValley 的 Abigail_LewDew / _LewDewExtra / Jas_Collar…，另有
    /// Clint_Magician / Krobus_Trenchcoat / Governor_walking，全都没有 Appearance 引用）。
    /// ⇒ 旧包里那些"把别人过场立绘换成用户选的脸"的补丁必须整体重建掉，光改判据不自愈。</summary>
    /// v1.7.34 = 24：<b>合并卡的「默认」在所有 id 上必须是同一张脸</b> —— 旧版只锁了有原版资产的那一侧，
    /// 于是法师卡（Wizard + SVE 的 Magnus）钉出来是分裂的：Portraits/Wizard*=原版 d40f98a609b1，
    /// Portraits/Magnus*=SVE 的 f6f735633f6c（走路表同样 d7a5db8f0a9b vs 789e2e96e37c）。
    /// 游戏读的是 Portraits/Magnus ⇒ 用户选男法师、进游戏是女巫（2026-09-30 实测）。
    /// ⇒ 旧包里那些"按【娘家行】钉进去的成员资产"不会自愈，必须整体重建。</summary>
    /// v1.7.35 = 25：<b>默认行的立绘必须纵向铺满底图</b> —— CopyPortraitFull 用 PngSize 量自己那张图，
    /// 而 PngSize 只读 PNG 头，原版那侧是 .xnb ⇒ 量出 0×0 ⇒ "比底图矮就平铺补满"整段被跳过 ⇒
    /// 默认行永远只钉一格。用 CP 的 `patch export "Portraits/Wizard"` 导出游戏真正在用的资产实测：
    /// 底图 128×1024（16 行表情帧），只有第 0 行是我们的原版脸，第 1–15 行仍是 OhoDavi 的女巫
    /// ⇒ 台词取到哪一行就露哪张脸（用户 2026-09-30 报"我选男法师，进游戏两句话变三次脸"）。
    /// ⇒ 旧包里那些只有一格高的默认行钉图必须重建，光改判据不自愈。</summary>
    public const int OverrideFormat = 25;

    /// <summary>覆盖包补丁的优先级。⚠ 必须【严格高于】任何可能被它覆盖的包（包括我们自己
    /// 转出来的 JuniGrid.PortraitPack.* 老包写的 "Late + 10"）—— 同优先级并列时 CP 由加载顺序
    /// 决定胜负，用户在界面上选的那张就会随机输掉。</summary>
    public const string OverridePriority = "Late + 100";
    /// <summary>版本号写在覆盖包根目录的这个文件里，不塞进 content.json ——
    /// 那份文件的 schema 是 CP 的，往里加未知根字段可能被它当错误报出来。</summary>
    public const string OverrideFormatFile = "junigrid-override-format.txt";

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

    /// <summary>这个名字是不是一个已知 NPC 的贴图资产名。裸图包（Portraiture 那类）的文件只叫
    /// Spring / Aerobics，NPC 名要靠这条判定找回来 —— 见 ModService 转换包时的场合前缀补全。</summary>
    public static bool IsKnownNpcAsset(string? stem) =>
        stem is { Length: > 0 } && (VanillaNames.ContainsKey(stem) || ModNames.ContainsKey(stem));

    /// <summary>外观变体后缀（大小写不敏感）：季节 + 场景变装 + 节日差分 + 职业换装。
    /// 语义是"这是某个角色的另一种外观，不是另一个角色"。Scan 里用它把同一角色的多张图归并成
    /// 一张卡（用户要「主要人物的主要肖像」，不要节日/泳装/工作服刷屏）；ModService 转换裸图包时
    /// 也用它当补 NPC 前缀的闸门 —— 只有整串后缀都在这张表里的裸名（Spring、Winter_Indoor、
    /// Aerobics）才是"丢了角色名的变体"，Bear/AnsweringMachine 这种是它们自己的资产名，
    /// 挂到某个角色名下就是把好包改坏（B43d 实测）。
    /// 表里的词按实机素材收：Wedding 是 Caroline (Overhaul) 那个包教会我们的 —— 漏收它就既
    /// 归并不了、转换时也补不上前缀，自愈会判"认不出角色的新目标"而整包不动（2026-09-28 实测）。</summary>
    public static readonly HashSet<string> AppearanceSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Beach", "Spring", "Summer", "Fall", "Winter", "Hospital",
        "Indoor", "Outdoor", "Makeup",
        "FlowerDance", "SpiritsEve", "Spiritseve", "EggF", "Fair", "Jellies", "Luau",
        "IceF", "IceFestival", "WinterStar", "DesertFestival", "Theater", "Joja", "JojaMart",
        "Aerobics", "Work", "Doctor", "Cosplay", "Event", "Vendor", "Older",
        "Wedding",
    };

    /// <summary>SVE 等常见 mod 角色中文名（只做展示，与原版判定无关）。</summary>
    private static readonly Dictionary<string, string> ModNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sophia"] = "苏菲亚", ["Victor"] = "维克托", ["Andy"] = "安迪", ["Claire"] = "克莱尔",
        ["Lance"] = "兰斯", ["Olivia"] = "奥利维亚", ["Susan"] = "苏珊", ["Apples"] = "阿普尔斯",
        ["Scarlett"] = "斯嘉丽", ["Suki"] = "苏琪", ["June"] = "朱恩", ["Morgan"] = "摩根",
        ["Martin"] = "马丁", ["Blair"] = "布莱尔",
        // SVE 主要角色（有完整剧情/对话）
        ["Isaac"] = "艾萨克", ["Jadu"] = "贾杜", ["Jolyne"] = "乔琳",
        ["Camilla"] = "卡蜜拉", ["Alesia"] = "阿莱西娅", ["Magnus"] = "法师",
    };

    /// <summary>贴图资产别名：游戏内容目录里的真实文件名与角色 id 不同时在此映射（1.6 实测：
    /// Leo 的贴图注册为 ParrotBoy，Portraits 目录根本没有 Leo.xnb）。</summary>
    private static IEnumerable<string> VanillaAssetAliases(string id)
    {
        if (id.Equals("Leo", StringComparison.OrdinalIgnoreCase))
            yield return "ParrotBoy";
        if (id.Equals("Gil", StringComparison.OrdinalIgnoreCase))
            yield return "GilSprite";
        yield return id;
    }

    /// <summary>资产别名反查表：mod 的 CP Target 常沿用原版资产名（Nyapu/OhoDavi 写
    /// Portraits/ParrotBoy，从不出现 Portraits/Leo）。扫描归并与覆盖包写盘都必须
    /// 经过这里，否则雷欧看不到这些皮肤，换肤也打不中游戏真实资产。</summary>
    private static readonly Dictionary<string, string> AssetAliasToCharId =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ParrotBoy"] = "Leo",
            // 吉尔 1.6 本体贴图叫 GilSprite（无 Characters/Gil.xnb），SVE/SCC 也是这个资产名。
            // 不归到 Gil 的话，吉尔卡的精灵预览会落到 FindCharacterAsset 的错误回退上（实测乱图）。
            ["GilSprite"] = "Gil",
        };

    /// <summary>资产名 → 角色 id（无别名时原样返回）。</summary>
    private static string CanonCharId(string assetName) =>
        AssetAliasToCharId.TryGetValue(assetName, out var id) ? id : assetName;

    /// <summary>
    /// 查素材时该试的所有名字：本名 + 游戏资产别名（Leo/ParrotBoy）+ 同义 NPC 名
    /// （Wizard/Magnus）。性转包常把精灵图只挂在 Characters/Magnus、立绘挂
    /// Portraits/Wizard，按单键查会得出「这包没有精灵图」→ 预览回落男巫师（实测）。
    /// </summary>
    private static IEnumerable<string> AssetLookupIds(string id)
    {
        yield return id;
        foreach (var (alias, cid) in AssetAliasToCharId)
            if (cid.Equals(id, StringComparison.OrdinalIgnoreCase))
                yield return alias;
        if (id.Equals("Wizard", StringComparison.OrdinalIgnoreCase)) yield return "Magnus";
        else if (id.Equals("Magnus", StringComparison.OrdinalIgnoreCase)) yield return "Wizard";
        else if (id.Equals("Leo", StringComparison.OrdinalIgnoreCase)) yield return "ParrotBoy";
        else if (id.Equals("Gil", StringComparison.OrdinalIgnoreCase)) yield return "GilSprite";
    }

    /// <summary>角色 id → 游戏内容资产名（无别名时原样返回）。覆盖包 Target 必须用它。</summary>
    private static string GameAssetId(string charId)
    {
        foreach (var (alias, id) in AssetAliasToCharId)
            if (id.Equals(charId, StringComparison.OrdinalIgnoreCase)) return alias;
        return charId;
    }

    /// <summary>转换包自愈每进程只跑一轮（闸门是包里的 junigrid-convert.txt，跑完就都最新了）。
    /// gamePath 还没就位时不烧掉这一轮 —— 配置比扫描晚到的话，自愈会永远错过。</summary>
    private static int _healConvertedRan;
    private static void HealConvertedPacksOnce(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return;
        if (Interlocked.Exchange(ref _healConvertedRan, 1) == 1) return;
        try { ModService.HealConvertedPortraitPacks(gamePath); }
        catch (Exception ex) { AppLog.Warn("Portraits", "[转换包自愈] 未执行：" + ex.Message); }
    }

    /// <summary>扫描 Mods 全目录，产出角色卡片数据。纯磁盘读取，可在后台线程调用。
    /// v1.3.9：扫描快照 —— 结果序列化到 LocalAppData，键 = Mods 目录签名。
    /// v1.7.2：进程内再加一层内存快照 —— 从立绘页切走再切回时直接命中，
    /// 不再闪骨架、不再反序列化几百 KB 的 JSON。</summary>
    public PortraitScanResult Scan(string gamePath)
    {
        // 转换包自愈：必须排在所有快照之前 —— 它会改写 content.json，签名（含 content.json 的
        // mtime）跟着变，旧快照自然作废。放在后面就会出现"这轮命中旧快照、下轮才重扫"的错位。
        HealConvertedPacksOnce(gamePath);

        // 内存快照：同一次启动内 Mods 签名没变就直接用
        var mem = TryGetMemoryScan(gamePath);
        if (mem is not null) return mem;

        using var lease = ReadLease();
        var cached = TryLoadScanCache(gamePath, out var sig);
        if (cached is not null)
        {
            RememberScannedCharIds(gamePath, cached.Characters.Select(c => c.Id));
            StoreMemoryScan(gamePath, cached, sig);
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
        // v1.7.28：HD 肖像通道的渲染端装了没 —— 没装的话 Mods/HDPortraits/* 只是没人读的数据资产
        var hdRendererInstalled = false;
        List<string> manifests = new();
        var installedUids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirByUid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
                // v1.7.10：记下"装了哪些 mod、各在哪个目录" —— CP 的 HasMod / 跨包 config
                // 条件要用（Ohodavi's for Rasmodia 的画风 token 就挂在 HasMod 上）。
                // 禁用包上面已经 continue 掉了：SMAPI 没加载它，HasMod 本来就该是 false。
                if (!string.IsNullOrWhiteSpace(uid))
                {
                    installedUids.Add(uid!);
                    dirByUid[uid!] = dir;
                    // v1.7.28：谁会把 Mods/HDPortraits/<角色> 真的画到屏幕上。
                    // Portraiture 自带一套 HDP 实现（config.json 的 active="HDP"，本服务自己也会写它），
                    // HD Portraits 本体是另一条来源。两个都没有 ⇒ 那条通道是死的，不必接管。
                    if (string.Equals(uid, ModService.PortraitureFrameworkUid, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(uid, HdPortraitsUid, StringComparison.OrdinalIgnoreCase))
                        hdRendererInstalled = true;
                }
                // ⚠ 规范化为无点路径：禁用包的文件夹带 . 前缀（.[CP] xx），原样记录会让
                // 选择值/后续启禁/config 写入全部用带点路径（启用改名后路径失效）。
                // 禁用状态只影响 Disabled 标志，不影响身份。
                var rel = string.Join('/',
                    Path.GetRelativePath(modsDir, dir).Replace('\\', '/')
                        .Split('/').Select(seg => seg.TrimStart('.')));

                // v1.7.13：记下「谁依赖谁」—— 皮肤自己没走路表时，身体按前置依赖包解析
                // （用户拍板的链：自己的精灵 → 前置包的精灵 → 默认）。禁用包上面已经
                // continue 掉了，所以这里记下来的前置一律是游戏里真会加载的包。
                if (!string.IsNullOrWhiteSpace(uid))
                    result.PackFolderByUid[uid!] = rel;
                if (man["Dependencies"] is JArray depArr && depArr.Count > 0)
                {
                    var depUids = new List<string>();
                    foreach (var d in depArr)
                    {
                        var du = d?["UniqueID"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(du)) depUids.Add(du!);
                    }
                    if (depUids.Count > 0) result.PackDeps[rel] = depUids;
                }

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
                nativeCandidates, nativeOwners, displayNames, new List<string>(), gated: false, visited, depth: 0,
                new ModIndex(installedUids, dirByUid));
        }
        foreach (var kv in displayNames) result.CharDisplayNames[kv.Key] = kv.Value;

        // v1.7.10：判不了的条件 / 解不出的 token 汇总出来点名（每包一行，样例截前 4 条）。
        // 静默跳过 = 用户报"某个包的肖像没显示"时我们连方向都没有。
        foreach (var p in packs)
        {
            if (p.UnknownConds.Count == 0) continue;
            foreach (var c in p.UnknownConds) result.UnknownConditions.Add((p.Folder, c));
            AppLog.Info("Portraits", $"[条件判不了] {p.Folder} 共 {p.UnknownConds.Count} 类：" +
                string.Join(" ｜ ", p.UnknownConds.Take(4)));
        }

        // 变体资产汇入扫描结果（覆盖包整族钉住用）
        foreach (var p in packs)
            foreach (var v in p.AssetVariants)
                result.VariantAssets.Add((p.Folder, v.Kind, v.BaseId, v.VariantId, v.File));

        // 「基资产 + When:{Season}」的分季映射同样汇入（覆盖包按文件名找不到季节图时兜底）
        foreach (var p in packs)
            foreach (var (asset, seasons) in p.BaseSeasonPatches)
                foreach (var (season, file) in seasons)
                    result.BaseSeasonPatches.Add((p.Folder, asset, season, file));

        // v1.7.28：HD 肖像通道（Mods/HDPortraits/<角色>）汇入。这类包以前被"目标命名空间不认识"
        // 整条静默丢弃 ⇒ 它照旧画走对话框大头照，而肖像页没有它、日志也不点名（Dacar 实测：
        // 用户报"法师怎么换都是那张脸、那张脸在肖像页里根本找不到"）。现在既接管也点名。
        result.HdPortraitRendererInstalled = hdRendererInstalled;
        foreach (var p in packs)
            foreach (var h in p.HdEntries)
                if (!result.HdPortraitEntries.Any(e =>
                        string.Equals(e.DataAsset, h.DataAsset, StringComparison.OrdinalIgnoreCase)))
                    result.HdPortraitEntries.Add((p.Folder, h.DataAsset, h.Npc));
        // v1.7.29：把 HD 通道拼成【可以当皮肤选】的条目。必须放到这里 —— 单包解析阶段看不见
        // 别的包，而 Dacar 那张高清脸的文件名挂着 {{Spiderbuttons.CMCT/Dynamic: <别人家UID>,<token>}}
        //（值由 Romanceable Rasmodia 的 DynamicTokens 按"装没装 SVE"决定）。只有全部包解析完、
        // 把被引用包算好的 TokenNow 按原样键回填进本包，代换那一步才解得开。
        var packByUid = new Dictionary<string, PackScan>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in packs)
            if (!string.IsNullOrWhiteSpace(p.Uid) && !packByUid.ContainsKey(p.Uid)) packByUid[p.Uid] = p;
        foreach (var p in packs)
            foreach (var need in p.NeededCrossTokens)
            {
                // 键形如 "Spiderbuttons.CMCT/Dynamic: <UID>,<token>"：冒号后到第一个逗号是 UID
                var head = need.IndexOf(':');
                var comma = need.IndexOf(',', head + 1);
                if (head <= 0 || comma <= head + 1) continue;
                if (packByUid.TryGetValue(need[(head + 1)..comma].Trim(), out var other)
                    && other.TokenNow.TryGetValue(need[(comma + 1)..].Trim(), out var xval)
                    && xval.Length > 0)
                    p.TokenNow[need] = xval;
            }
        if (hdRendererInstalled)
            foreach (var p in packs)
            {
                var hdPackDir = Path.Combine(modsDir, p.Folder);
                foreach (var h in p.HdEntries)
                {
                    // 那条数据资产的 Portrait 指向哪个资产：包自己在 EditData 里写明；
                    // 没写明的按"同名私有资产"兜一条，再解不开就放弃（绝不猜文件名）。
                    var ptr = p.HdPortraitPointers.TryGetValue(h.DataAsset, out var ptrRaw)
                        ? ptrRaw : "Mods/" + h.Npc;
                    ptr = ptr.Replace("{{TargetWithoutPath}}", h.Npc, StringComparison.OrdinalIgnoreCase);
                    if (ptr.Contains("{{")) continue;
                    // 同一资产常有多条 Load（互斥的 Include 子文件各钉一条）⇒ 按补丁顺序取
                    // 第一条【真解得出文件】的；字典式"后者覆盖前者"会拿到没生效那一支的图
                    //（Dacar 的 portraits.json / RRRRportraits.json 实测撞在同一个资产名上）。
                    string? hdAbs = null;
                    foreach (var (lt, tpl) in p.ModsPrivateLoads)
                    {
                        if (!string.Equals(lt, ptr, StringComparison.OrdinalIgnoreCase)) continue;
                        var rel = ResolveFromFileTokens(tpl, "Portraits", h.Npc, p);
                        var abs = rel is null ? null : SafeFull(hdPackDir, rel);
                        if (abs is null || !File.Exists(abs) || !LooksLikePortrait(abs)) continue;
                        hdAbs = abs;
                        break;
                    }
                    if (hdAbs is null) continue;
                    PngSize(hdAbs, out var hdW, out _);
                    // 128 宽就是普通立绘通道的尺寸，不该按 HD 那条路钉（会绕过 Portraits/ 资产）
                    if (hdW <= 128) continue;
                    result.HdSkins.Add((p.Folder, h.Npc, h.DataAsset, ptr, hdAbs, hdW / 2));
                }
            }
        foreach (var g in result.HdPortraitEntries
                     .GroupBy(e => e.Npc, StringComparer.OrdinalIgnoreCase))
        {
            var owners = string.Join("、",
                g.Select(e => e.Pack).Distinct(StringComparer.OrdinalIgnoreCase));
            AppLog.Warn("Portraits", $"[HD肖像通道] {g.Key} 的大头照由 {owners} 走 Mods/HDPortraits 提供" +
                (hdRendererInstalled ? "（渲染端已装 ⇒ 覆盖包接管这条通道）" : "（本机没装渲染端 ⇒ 这条通道不生效）"));
            if (hdRendererInstalled)
                result.Diagnostics.Add((g.Key,
                    $"对话框大头照走 HD 肖像通道（Mods/HDPortraits/{g.Key}），来源包：{owners}。" +
                    "这条通道绕开 Portraits/ 资产，已由覆盖包接管 —— 本页选的皮肤会一并生效。"));
        }

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
        // ⚠ 原版名单必须以游戏自己的 Content/Portraits 为准：VanillaNames 只收了"可交友"的
        // 那 39 个，而 Grandpa/Bear/Birdie/Bouncer/Fizz/Governor/Henchman/SafariGuy 这些
        // 原版就有肖像、OhoDavi/Nyapu/stardewvalley anime mods 还给它们画了整套画 —— 名单不含
        // 它们 ⇒ 这些包的画"永远不显示"（实测：45 个缺口里占 39 个）。
        // ⚠ 但"有肖像"不等于"是角色"：AnsweringMachine（电话机）等无生命物体已在 VanillaPortraitIds
        // 里按 NonPersonPortraits 剔除，最终 allCharIds 再 ExceptWith 一次（画风包会把它塞回来）。
        // ⚠ 先过别名归一（ParrotBoy → Leo）：不然资产名会被当成又一个原版角色，
        // 给雷欧多开一张卡（实测）。
        var vanillaPortraitIds = new HashSet<string>(
            VanillaPortraitIds(gamePath).Select(CanonCharId), StringComparer.OrdinalIgnoreCase);
        foreach (var v in vanillaPortraitIds) canonIds.Add(v);
        canonIds.UnionWith(nativeCandidates);
        var rawIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in packs)
        {
            foreach (var id in p.PortraitFiles.Keys) rawIds.Add(id);
            foreach (var id in p.SpriteFiles.Keys) rawIds.Add(id);
            foreach (var id in p.SpriteChars) rawIds.Add(id);
        }

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
                if (!AppearanceSuffixes.Contains(suffix)) break;   // 非外观后缀 → 不归并
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
        // vanillaPortraitIds 已按别名归一（Leo 而不是 ParrotBoy），直接并进来
        foreach (var v in vanillaPortraitIds) allCharIds.Add(v);
        foreach (var p in packs)
        {
            foreach (var id in p.PortraitFiles.Keys) allCharIds.Add(id);
            foreach (var id in p.WhenKeys.Keys) allCharIds.Add(id);
        }
        foreach (var id in portraitureSources.Keys) allCharIds.Add(id);
        allCharIds.UnionWith(rawIds);
        // 硬闸门：非角色资产（电话机等无生命物体）即使被某个画风包画了也不出卡。上面 packs /
        // portraiture / rawIds 那几行会把它塞回候选，所以只过滤 VanillaPortraitIds 不够，必须打在最终集合上。
        allCharIds.ExceptWith(NonPersonPortraits);

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
            // 原版判定同上：游戏自带这张肖像的就是原版角色，不是"mod 数据条目"
            var isVanilla = VanillaNames.ContainsKey(id) || vanillaPortraitIds.Contains(id);

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
                // 用户 2026-09-29 拍板：「默认」行永远锁死原版。实测 47 个原版角色里有 28 个的
                // 默认卡指向 mod 文件（LewDew 系列、SVE 的法师/冈瑟/马龙、把图改名成
                // Portrait.png / Sprite.png 的大改包）⇒ 下一个 mod 就把"默认"顶掉了。
                // 现在：有原版资产的一侧【绝不】被扩展包覆盖，mod 的图只作为皮肤卡出现。
                // ⚠ 只有原版根本没有那一侧时才允许回落：吉尔 1.6 的贴图不叫
                //   Content/Characters/Gil.xnb（连 GilSprite.xnb 都没有），不回落就是一张空白默认卡。
                var defName = (string?)null; var defPortrait = VanillaPortraitXnb(gamePath, id);
                var defSprite = VanillaSpriteXnb(gamePath, id);
                foreach (var p in packs)
                {
                    // 纯短路：两侧都齐了就少绕几圈。真正的锁是下面两处 `is null` 逐侧守卫
                    // —— 实测把这个 break 整行撤掉用例一条都不会变红（M21），
                    // 而撤掉逐侧守卫 B57b 立刻红成「默认行的脸=mod 图」（M22）。
                    if (defPortrait is not null && defSprite is not null) break;
                    if (!expansionFolders.Contains(p.Folder)) continue;
                    // 逐侧守卫①（实测承重，见上）：原版有这张脸就一个字都不看 mod 的。
                    // 精确名优先（Portraits/<id>）；SVE 给马龙/冈瑟的新立绘挂在
                    // MarlonFay / GuntherSilvian 这类"前缀+剧情名"资产下（1.6 Appearance
                    // 引用），主名 Portraits/<id> 反而没 Load —— 前缀匹配兜住
                    if (defPortrait is null)
                    {
                        if (!p.PortraitFiles.TryGetValue(id, out var pf) || pf is null || pf.Count == 0)
                        {
                            // 剩余部分必须以大写字母开头（复合词：MarlonFay/GuntherSilvian），
                            // 否则会把 Jasper（以 Jas 开头的另一个 NPC）错当贾斯的默认像（实测）
                            var altKey = p.PortraitFiles.Keys.FirstOrDefault(k =>
                                k.StartsWith(id, StringComparison.OrdinalIgnoreCase) && k.Length > id.Length
                                && char.IsUpper(k[id.Length]));
                            if (altKey is not null) pf = p.PortraitFiles[altKey];
                        }
                        if (pf is { Count: > 0 })
                        {
                            var f = PickSource(id, pf);
                            if (f is not null) { defPortrait = f; defName = p.Name; }
                        }
                    }
                    if (defPortrait is null) continue;
                    // 逐侧守卫②：走路表同理，只在原版缺表时才收扩展包那张（吉尔/桑迪形状）。
                    var sprBefore = defSprite;
                    if (defSprite is null && p.SpriteFiles.TryGetValue(id, out var sf))
                        defSprite = PickSource(id, sf) ?? defSprite;
                    if (defSprite != sprBefore) defName = p.Name;
                    if (defSprite is null)
                    {
                        var altSKey = p.SpriteFiles.Keys.FirstOrDefault(k =>
                            k.StartsWith(id, StringComparison.OrdinalIgnoreCase) && k.Length > id.Length
                            && char.IsUpper(k[id.Length]));
                        if (altSKey is not null && PickSource(id, p.SpriteFiles[altSKey]) is { } altSpr)
                        {
                            defSprite = altSpr;
                            defName = p.Name;
                        }
                    }
                }
                // 变体资产路径：SVE 给马龙/冈瑟的新立绘挂在 Portraits/MarlonFay、
                // Portraits/GuntherSilvian 这类变体资产名下（1.6 Appearance 引用），
                // 主名 Portraits/<id> 反而没 Load —— 从变体登记里补（同样只补缺的那一侧）
                if (defPortrait is null)
                {
                    var v = result.VariantAssets.FirstOrDefault(va =>
                        expansionFolders.Contains(va.Pack)
                        && string.Equals(va.BaseId, id, StringComparison.OrdinalIgnoreCase)
                        && va.Kind == "Portraits");
                    if (v.Pack is not null)
                    {
                        defPortrait = v.File;
                        var vs = result.VariantAssets.FirstOrDefault(va =>
                            expansionFolders.Contains(va.Pack)
                            && string.Equals(va.BaseId, id, StringComparison.OrdinalIgnoreCase)
                            && va.Kind == "Characters");
                        if (vs.Pack is not null && defSprite is null)
                        {
                            defSprite = vs.File;
                            defName = packs.FirstOrDefault(x => string.Equals(x.Folder, vs.Pack,
                                StringComparison.OrdinalIgnoreCase))?.Name;
                        }
                    }
                }
                vanillaRow = new PortraitSkinOption("", "默认", IsPortraiture: false, IsVanilla: true,
                    IsNative: false, HasSprite: defSprite is not null, defPortrait,
                    defSprite, Array.Empty<string>())
                { DefaultArtPack = defName };
            }
            else
            {
                // 硬同义词（SameNpcPairs：SVE 把法师整个换成另一个 id「Magnus」）——这个 id
                // 在 Content\ 里没有对应资产，于是它压根没有【默认行】，落盘时走的是娘家行
                // =SVE 那张女巫脸；而游戏读的偏偏就是 Portraits/Magnus ⇒ 卡片显示原版男法师、
                // 进游戏是女巫（2026-09-30 实测：钉进去的字节 Wizard*=d40f98a609b1 原版，
                // Magnus*=f6f735633f6c SVE）。同一个人的另一侧有原版 ⇒ 默认行就用那张。
                foreach (var (pa, pb) in SameNpcPairs)
                {
                    var twin = id.Equals(pa, StringComparison.OrdinalIgnoreCase) ? pb
                        : id.Equals(pb, StringComparison.OrdinalIgnoreCase) ? pa : null;
                    if (twin is null) continue;
                    var tPortrait = VanillaPortraitXnb(gamePath, twin);
                    var tSprite = VanillaSpriteXnb(gamePath, twin);
                    if (tPortrait is null && tSprite is null) break;
                    vanillaRow = new PortraitSkinOption("", "默认", IsPortraiture: false,
                        IsVanilla: true, IsNative: false, HasSprite: tSprite is not null,
                        tPortrait, tSprite, Array.Empty<string>());
                    break;
                }
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
                // 别名键兜底：Wizard/Magnus 同义，包可能只在某一侧挂立绘/精灵图
                if (files is null || files.Count == 0)
                {
                    foreach (var alt in AssetLookupIds(id))
                    {
                        if (alt.Equals(id, StringComparison.OrdinalIgnoreCase)) continue;
                        if (p.PortraitFiles.TryGetValue(alt, out var altPf) && altPf is { Count: > 0 })
                        { files = altPf; break; }
                    }
                }
                if (spriteFiles is null || spriteFiles.Count == 0)
                {
                    foreach (var alt in AssetLookupIds(id))
                    {
                        if (alt.Equals(id, StringComparison.OrdinalIgnoreCase)) continue;
                        if (p.SpriteFiles.TryGetValue(alt, out var altSf) && altSf is { Count: > 0 })
                        { spriteFiles = altSf; break; }
                    }
                }
                // 包必须真的给这个角色注册过立绘（Load 过 Portraits/<id>）才算皮肤 ——
                // 否则每个内容包都会变成每个角色的"皮肤"（实机用户反馈：满屏不相关皮肤）
                if (files is null) continue;

                // 季节服装包常把动态资源放在 assets/Portraits/Emily/Emily_Spring.png
                // 这类路径依赖运行时 token，静态解析不到时仍从实际素材目录取一张代表图。
                var packRoot = p.RawDir is { Length: > 0 } rawDir
                    ? rawDir
                    : Path.Combine(modsDir, p.Folder.Replace('/', Path.DirectorySeparatorChar));
                // v1.7.3：类别校验 —— 误登记进 PortraitFiles 的精灵表（64×192）绝不能当
                // 头像缩略图（整表缩进小方格 = 一格小人图集）；反过来头像也不能当精灵。
                string? PickPortrait(List<string>? list)
                {
                    // 包自己声明了「基资产按季换立绘」（同一条 Target=Portraits/<id>、按
                    // When:{Season} 分支）时，封面用它声明的春季那张。这种包的 base 文件是
                    // 作者画的"原版服装"占位（[CP] Caroline Overhaul：立绘仍是裸的），
                    // 拿 base 当封面用户看到的就是裸体（2026-09-28 实测）。
                    // ⚠ 只认这条声明，不按文件名里的季节提权 —— 季节差分资产
                    //（Portraits/Juliet_Winter）是"某个场合的脸"，不是默认像（同日实测）。
                    if (list is not null)
                    {
                        foreach (var ak in AssetLookupIds(id))
                        {
                            if (!p.BaseSeasonPatches.TryGetValue("Portraits/" + ak, out var dsm)) continue;
                            foreach (var sn in CoverSeasonOrder)
                                if (dsm.TryGetValue(sn, out var df) && !IsNoPortraitsSource(df)
                                    && LooksLikePortrait(df)) return df;
                            break;
                        }
                    }
                    var best = PickSource(id, list);
                    if (best is not null && !IsNoPortraitsSource(best) && LooksLikePortrait(best)) return best;
                    if (list is not null)
                        foreach (var f in list)
                            if (!IsNoPortraitsSource(f) && LooksLikePortrait(f)) return f;
                    return FindCharacterAsset(packRoot, "Portraits", id);
                }
                string? PickSprite(List<string>? list)
                {
                    var best = PickSource(id, list);
                    if (best is not null && !IsNoSpritesSource(best) && !IsNonWalkSprite(best) && LooksLikeSprite(best)) return best;
                    if (list is not null)
                        foreach (var f in list)
                            if (!IsNoSpritesSource(f) && !IsNonWalkSprite(f) && LooksLikeSprite(f)) return f;
                    return FindCharacterAsset(packRoot, "Characters", id);
                }
                var src = PickPortrait(files);
                // 解析不出立绘文件的皮肤直接不列（未知 mod 写法的系统性兜底）——
                // 空卡不能选（切换会被阻止）、缩略图永远是空占位、删除键还会误删整个包
                var schemaKeys = p.SchemaKeys.Where(k => k.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0);
                var keys = (whenKeys ?? Enumerable.Empty<string>()).Union(schemaKeys).Distinct().ToArray();

                var spriteSrc = PickSprite(spriteFiles);
                // v1.7.9：作者**声明**过画风（ConfigSchema + DynamicTokens）的娘家包拆成每画风一卡。
                // [CP] Miku Mod Plus 就是这种：Miku 是它带的新 NPC，五套画风全在这一个包里，
                // 不拆就整个角色只剩一张卡。
                // ⚠ 不看 src 是否为空：令牌能按当前 config 解析出单张（config.json 记了
                // VanillaPortraitChange=SD）时 src 非空，若还要求 src is null，用户一旦有过
                // config 就把五套画风压回一张（实测 09-27 只出 1 卡）。声明信号（DeclaredStyleAssets
                // = ConfigSchema.AllowValues × DynamicTokens 且每档真有立绘文件）本身足够精确。
                // ⚠ 但不许扩成「src 解析不出来就拆」：那样会把事件演员与季节变体
                //（Axel/Bear/Magnus_Fall/Jas_Cum/Suki_IceFestival…）整批放进立绘页
                //（实测 09-26 被用户退回，上屏卡 64 → 103）。
                if (isNativeFor && p.DeclaredStyleAssets.Contains(id)
                    && StyleVariants(p, id) is { Count: >= 2 } styles0)
                {
                    var token0 = styles0[0].Token;
                    foreach (var st0 in styles0)
                    {
                        var vSpr0 = st0.Sprite ?? spriteSrc;
                        if (IsFakeWalkSheet(vSpr0, st0.Portrait, id)) vSpr0 = null;
                        var card = new PortraitSkinOption(p.Folder, $"{p.Name}{VariantNameSep}{st0.Style}",
                            IsPortraiture: false, IsVanilla: false, IsNative: false,
                            // 「含精灵图」只认手里真有整表文件的那条。SpriteChars 是"这个包碰过
                            // 这个角色的 Characters"，ToArea 局部补丁（RRR 补丁的花舞节差分）也在里面，
                            // 拿它当角标 = 卡片宣称有身体、右侧却只能借别人的（实测法师）。
                            HasSprite: vSpr0 is not null,
                            st0.Portrait, vSpr0, StyleKeys(keys, token0, st0.Style))
                        {
                            Variant = st0.Style,
                            VariantConfigKey = token0,
                            IsCurrentVariant = string.Equals(st0.Style, styles0[0].Style,
                                StringComparison.OrdinalIgnoreCase),
                        };
                        // 第一档（= ConfigSchema 的 Default，游戏当前显示的那套）当「mod 默认外观」行：
                        // 这样角色能过「mod 角色必须有娘家行」那道门，不必为了 Miku 去放宽它
                        //（放宽过一次，结果事件演员与季节变体整批上屏，实测被用户退回）。
                        if (nativeRow is null)
                        {
                            nativeRow = card with { IsNative = true, PackName = card.PackName + " 默认外观" };
                            nativePack = p.Folder;
                            result.NativePacks.Add(p.Folder);
                            result.NativePackNames[p.Folder] = p.Name;
                        }
                        else skins.Add(card);
                    }
                    configKeys[(p.Folder, id)] = keys
                        .Where(k => !k.Equals(token0, StringComparison.OrdinalIgnoreCase))
                        .Concat(styles0.Select(st0 => $"{token0}={st0.Style}")).ToArray();
                    continue;
                }

                // v1.7.8：挑出来的"精灵"其实是这个角色的单格立绘（实测 Suki 与立绘同一文件、
                // Gunther/Marlon/Claire/Martin/Krobus 是 64×64 单格）→ 当这张皮肤没有精灵处理，
                // 于是走 v1.7.4 那条回落：原皮精灵 → 原皮也没有就空白（右侧不再摆一张四不像）。
                if (IsFakeWalkSheet(spriteSrc, src, id)) spriteSrc = null;

                // v1.7.7：同一个包内多画风 → 一个画风一张卡（用户拍板）。
                // 只有 ≥2 个画风目录才拆；0/1 个候选时下面那条老路径原样走，产出的选项
                // 逐字段与改动前一致 —— 别的包不许因为这次改动漂移（硬要求）。
                // 娘家行不拆：它是「mod 默认外观」那一行，不可选不可删，拆成三行没有语义。
                // v1.7.10：画风有两个来源 —— ① 目录段上的 {{配置键}}（Donut's 的 Alesia）；
                // ② 同一个布尔开关的互斥分支（Donut's 的 Wizard）。①优先，②只在①没有时兜上。
                var styles = StyleVariants(p, id);
                var fromBranch = false;
                if (styles is null) { styles = BranchVariants(p, id); fromBranch = true; }
                if (!isNativeFor && styles is { Count: >= 2 })
                {
                    var token = styles[0].Token;
                    foreach (var s in styles)
                    {
                        // ⚠ 分支卡**不许**沿用包级兜底那张精灵：法师就是被这条兜底缝坏的 ——
                        // fifa 分支本来没有身体，兜底会把 klev 的身体塞给它 = 游戏里不存在的组合。
                        var vSprite = fromBranch ? s.Sprite : (s.Sprite ?? spriteSrc);
                        if (IsFakeWalkSheet(vSprite, s.Portrait, id)) vSprite = null;   // v1.7.8：单格立绘不算走路表
                        // 分支画风要写回 config 的是开关的值（true/false），给用户看的那一截
                        // 用作者放图的母目录名（= 画师名）；①那类目录名本来就等于值，不动。
                        var label = s.Style;
                        if (fromBranch && s.Portrait is { Length: > 0 } br)
                            label = Path.GetFileName(Path.GetDirectoryName(br)) is { Length: > 0 } d ? d : label;
                        skins.Add(new PortraitSkinOption(p.Folder, $"{p.Name}{VariantNameSep}{label}",
                            IsPortraiture: false, IsVanilla: false, IsNative: false,
                            // 分支卡的"含精灵图"角标只说自己那条分支，且不借包级 SpriteChars
                            HasSprite: vSprite is not null,
                            s.Portrait, vSprite, StyleKeys(keys, token, s.Style))
                        {
                            Variant = s.Style,
                            VariantLabel = label == s.Style ? null : label,
                            VariantConfigKey = token,
                            // 登记时已把"作者默认/config 当前那一档"挪到第一条（卡片列表随后按
                            // 包名重排，顺序活不下来，所以把结论记在条上给 MatchOption 用）
                            IsCurrentVariant = string.Equals(s.Style, styles[0].Style,
                                StringComparison.OrdinalIgnoreCase),
                        });
                    }
                    // 写源包 config.json 用的键：裸 token 换成「每个画风一条 Key=Value」，
                    // 具体写哪一个由 PortraitSkinVariants 决定（见 WritePackConfig）
                    var merged = keys.Where(k => !k.Equals(token, StringComparison.OrdinalIgnoreCase))
                        .Concat(styles.Select(s => $"{token}={s.Style}")).ToArray();
                    configKeys[(p.Folder, id)] = merged;
                    continue;
                }

                // 解析不出立绘文件的皮肤直接不列（未知 mod 写法的系统性兜底）——
                // 空卡不能选（切换会被阻止）、缩略图永远是空占位、删除键还会误删整个包
                if (src is null) continue;

                var opt = new PortraitSkinOption(p.Folder, p.Name, IsPortraiture: false, IsVanilla: false,
                    IsNative: isNativeFor, HasSprite: spriteSrc is not null,
                    src, spriteSrc, keys);
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

            // v1.7.29：HD 肖像通道的包（只写 Mods/HDPortraits/<角色>、不碰 Portraits/ 的那种，
            // 例：[CP] Dacar Rasmodia Portraits）也上屏当皮肤选。旧扫描把这类目标整条丢掉 ⇒
            // 那张脸在肖像页里根本找不到，用户只能看着"无论选什么都还是它"（2026-09-29 实测报障）。
            var hdIds = AssetLookupIds(id).ToList();
            foreach (var h in result.HdSkins)
            {
                if (!hdIds.Any(k => string.Equals(k, h.Npc, StringComparison.OrdinalIgnoreCase))) continue;
                if (skins.Any(s => string.Equals(s.PackFolder, h.Pack, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(s.SourceFile, h.File, StringComparison.OrdinalIgnoreCase))) continue;
                var hdPackName = packs.FirstOrDefault(x => string.Equals(x.Folder, h.Pack,
                    StringComparison.OrdinalIgnoreCase))?.Name ?? h.Pack;
                skins.Add(new PortraitSkinOption(h.Pack, hdPackName, IsPortraiture: false, IsVanilla: false,
                    IsNative: false, HasSprite: false, h.File, null, Array.Empty<string>())
                { HdCell = h.Cell });
            }

            // 吉尔这类原版角色在 1.6 本体没有 Characters/<id>.xnb（贴图叫 GilSprite，
            // 由内容包 Load 后按别名归到角色名下）—— 原版行没图时取包里登记的精灵表，
            // 默认行也能预览行走贴图。别名键（GilSprite）也要查一遍。
            if (vanillaRow is not null && !LooksLikeSprite(vanillaRow.SpriteFile))
            {
                var aliasKeys = new List<string> { id };
                foreach (var (alias, cid) in AssetAliasToCharId)
                    if (cid.Equals(id, StringComparison.OrdinalIgnoreCase))
                        aliasKeys.Add(alias);
                var packSprite = packs
                    .Select(p =>
                    {
                        foreach (var k in aliasKeys)
                            if (p.SpriteFiles.TryGetValue(k, out var sf))
                            {
                                var f = PickSource(k, sf);
                                if (f is not null && LooksLikeSprite(f)) return f;
                            }
                        return null;
                    })
                    .FirstOrDefault(f => f is not null);
                // 兜底：原版 Content/Characters/<id>.xnb（含 GilSprite 别名）
                packSprite ??= VanillaSpriteXnb(gamePath, id);
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

            // v1.7.6：只藏「完全没有立绘来源」的空壳（测试沙箱/坏包）。有立绘就上页，
            // 没精灵不再整卡杀掉（Suki 这类 NoSprites 剧情 NPC 立绘能换）——精灵预览
            // 在 UI 侧回落默认皮肤。
            var anyPortrait = vanillaRow?.SourceFile is not null
                || nativeRow?.SourceFile is not null
                || skins.Any(s => s.SourceFile is not null);
            if (!anyPortrait) continue;

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
                // E0 硬同义词：游戏里同一个 NPC 的不同 id（SVE 把法师登记为 Magnus）。
                // 不用走佐证 —— 同名但构图不同的重绘会让 E4 误判成两个角色（法师卡拆成两张）。
                var e0 = KnownSameNpc(c.Id, winner.Id);
                // 门槛与 VariantGroups 的③号分组信号保持一致（原来是 4，把 Sam 这类三位字母
                // 本体挡掉了 ⇒ 分组成功、佐证却拒并，SamLewd 单独挂在页面上）。
                var e1 = winner.Id.Length >= 3
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
                if (!e0 && !e1 && !e3 && !e4)
                {
                    AppLog.Warn("Portraits",
                        $"[同名保留] {c.Id} 与 {winner.Id} 同名「{c.DisplayName}」但无变体佐证（id/娘家/立绘均不符），各自保留");
                    continue;
                }
                AppLog.Warn("Portraits",
                    $"[变体并入] {c.Id} → {winner.Id}「{winner.DisplayName}」佐证: " +
                    $"{(e0 ? "同义词 " : "")}{(e1 ? "id包含 " : "")}{(e3 ? "同娘家 " : "")}{(e4 ? "立绘相似" : "")}");
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
                    else
                    {
                        // 同包娘家行：旧的没精灵、新的有 → 升级
                        var nidx = bag.FindIndex(x =>
                            string.Equals(x.PackFolder, folder, StringComparison.OrdinalIgnoreCase));
                        if (nidx >= 0 && bag[nidx].SpriteFile is null && c.Native.SpriteFile is not null)
                        {
                            var pn2 = result.NativePackNames.TryGetValue(folder ?? "",
                                out var npn2) ? npn2 : c.Native.PackName;
                            bag[nidx] = c.Native with
                            {
                                IsNative = false,
                                PackName = $"{pn2}（{c.Id}）"
                            };
                        }
                    }
                }
                foreach (var s in c.Skins)
                {
                    if (string.IsNullOrWhiteSpace(s.PackFolder)) continue;
                    if (!have.Add(s.PackFolder))
                    {
                        // 同包已有一条：若旧的没精灵图、新的有，升级成带精灵的那条
                        //（Wizard 侧只有大头照、Magnus 侧有精灵图时，旧逻辑会丢掉精灵 → 男巫师回退）
                        var idx = bag.FindIndex(x =>
                            string.Equals(x.PackFolder, s.PackFolder, StringComparison.OrdinalIgnoreCase));
                        if (idx >= 0 && bag[idx].SpriteFile is null && s.SpriteFile is not null)
                            bag[idx] = s;
                        continue;
                    }
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

        // v1.3.9：与默认像同文件的皮肤行折叠（用户："这些怎么还没消失"）。
        // 抽成 FoldSkinsAgainstDefault 单独跑（用例 B22 直接盯着它）。
        // v1.7.15：跨包哈希去重已删除，这里只剩"同文件路径"那一道折叠。
        FoldSkinsAgainstDefault(characters, result.Diagnostics);

        // 季节皮肤分配校验（对全部角色，不限于发生过折叠的）：指向已不存在/空包名的
        // 条目回落全局选择，否则界面无卡可高亮、游戏里永远显示那个失效的包。
        {
            // ⚠ 只有"扫的就是用户配置里那个游戏目录"才允许动他的按季分配。
            // 触发背景（2026-09-28）：跑测试台后用户 5 个角色（Sophia/Victor/Wizard/Magnus/Emily）
            // 的四季分配被清空、日志留下"[季节清理] … 已不在扫描结果"。那条清理的判据是
            // "角色不在本次扫描结果里"，而沙箱目录的扫描结果当然没有这些角色 ⇒ 只要有一次
            // 非用户目录的扫描走到这里，用户配置就被清。具体是哪一次跑的链子没能复现出来
            // （加/去掉这道闸门，测试台整套跑完配置都完好），所以这条是【防御性闸门】，
            // 不是已证实的根因修复 —— 别把它当"bug 已定位"。
            static string NormPath(string? p) => string.IsNullOrWhiteSpace(p) ? ""
                : Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var isConfiguredGame = NormPath(gamePath).Length > 0
                && string.Equals(NormPath(gamePath), NormPath(_cfg.Current.GamePath),
                    StringComparison.OrdinalIgnoreCase);
            var ss0 = isConfiguredGame ? _cfg.Current.PortraitSeasonSkins : null;
            if (ss0 is { Count: > 0 })
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
        StoreMemoryScan(gamePath, result, sig);
        return result;
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

    /// <summary>名字上看就不是走路表的素材（节日换装/鼻头叠加/沙滩差分…）。
    /// Suki_DesertFestival.png 这种 64×64 节日图被当精灵会让预览区出现奇怪的小图
    ///（用户要求：没有真走路图就别显示）。</summary>
    public static bool IsNonWalkSprite(string? file)
    {
        if (string.IsNullOrWhiteSpace(file)) return false;
        var stem = Path.GetFileNameWithoutExtension(file.Replace('\\', '/'));
        foreach (var t in new[]
                 {
                     "FlowerDance", "SpiritsEve", "Spiritseve", "EggF", "Fair", "Jellies", "Luau",
                     "DesertFestival", "IceFestival", "Winter_IceF", "Winter_WinterStar",
                     "Nose", "Overlay", "Makeup", "Chair_Overlay",
                 })
        {
            if (stem.Contains(t, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
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

    /// <summary>封面取"包声明的分季像"时的季节优先序：春季最接近角色的默认形象。</summary>
    private static readonly string[] CoverSeasonOrder = { "spring", "summer", "fall", "winter" };

    /// <summary>从整表来源列表里挑默认文件：文件名与角色 id 完全一致的最优先（游戏默认帧），
    /// 沙滩/泳装这类外观变体排最后 —— 防止补丁登记顺序把沙滩照顶成默认（Sunberry 实测）。
    /// ⚠ 这里只看文件名形态，"包按季换基资产"那种真信号走 <see cref="PackScan.BaseSeasonPatches"/>，
    /// 见 <c>PickPortrait</c>。</summary>
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
                // 门面要挑"最像默认脸"的那张。事件/场景/节日换装（健身服、医院、Joja、精灵祭、
                // 花火舞、冬日内外…）是"某个场合的脸"，绝不是角色默认像 —— 重罚垫底。
                // 否则像 [CP] Childhood Sweetheart Caroline 这种"只有变体、没有 base 立绘"的整修包，
                // 会因文件列表第一条是 Caroline_Aerobics（蓝色紧身衣像泳装）而被当成封面（用户实测）。
                + (IsEventVariant(fn) ? 700 : 0)
                // 普通季节差分只按"春最接近默认"排个相对次序（罚分 0/4/6/8），⚠ 不许再加分压过
                // base：文件名带季节就提权曾把 East Scarp 的 Juliet 封面弄成 Juliet_Winter.png
                //（它的默认像是 assets/Portraits/Juliet/Juliet_base.png，冬季像是**另一个资产**
                // Portraits/Juliet_Winter，靠 1.6 Appearance 按季挂上）—— 同一天实测被用户一眼看穿。
                // "基资产本身按季换图"（[CP] Caroline Overhaul 的 Portraits/Caroline + When:{Season}）
                // 那种包不靠文件名，走包声明的分季表：见 PickPortrait。
                + SeasonPenalty(fn)
                // NyapuPortraits/AlternativeTextures 是 mod 自带的「可选画风」（门控加载），
                // 不是默认像 —— 有正规文件时垫底（Juliet/Eloise 默认像被可选画风顶替，实测）。
                // ⚠ 罚分必须压得住上面那条「文件名==角色 id」的 -1000：这类可选画风目录里的
                // 文件常就叫 Juliet.png，800 的旧罚分被 -1000 抵掉 ⇒ Juliet 封面又变成画风图
                //（assets/NyapuPortraits/Juliet.png，而作者 config 里 NyapuPortraits=false，
                // 真默认像是 assets/Portraits/Juliet/Juliet_base.png —— 2026-09-28 实测）。
                // 留出口：全表只剩这一张时它照样能被选中（没有别的可比）。
                + (norm.Contains("/NyapuPortraits/", StringComparison.OrdinalIgnoreCase)
                   || norm.Contains("/AlternativeTextures/", StringComparison.OrdinalIgnoreCase) ? 2000 : 0);
            if (score < bestScore) { bestScore = score; best = f; }
        }
        return best;
    }

    /// <summary>"某个场合才穿"的换装/节日/场景差分后缀 —— 不能当角色门面。与
    /// <see cref="AppearanceSuffixes"/> 语义一致，但那份判的是"整串后缀"（用于归并/补前缀），
    /// 这里是子串匹配、且多收 swim/trenchcoat，专给代表图选取用（覆盖旧代码只罚 beach/swim 的窄判据）。</summary>
    private static readonly string[] EventVariantTokens =
    { "beach", "swim", "aerobics", "hospital", "doctor", "work", "joja", "cosplay",
      "event", "vendor", "older", "makeup", "theater", "spiriteve", "flowerdance",
      "eggf", "luau", "icef", "winterstar", "desertfestival", "fair", "jellies",
      "trenchcoat", "indoor", "outdoor" };

    private static bool IsEventVariant(string fn)
        => EventVariantTokens.Any(t => fn.Contains(t, StringComparison.OrdinalIgnoreCase));

    /// <summary>普通季节差分的相对罚分：春=默认像 → 0，其余依次垫高。非季节名返回 0
    /// （事件换装已在 IsEventVariant 里重罚，这里不重复计）。</summary>
    private static int SeasonPenalty(string fn)
        => fn.EndsWith("spring", StringComparison.OrdinalIgnoreCase) ? 0
         : fn.EndsWith("summer", StringComparison.OrdinalIgnoreCase) ? 4
         : fn.EndsWith("fall", StringComparison.OrdinalIgnoreCase) ? 6
         : fn.EndsWith("winter", StringComparison.OrdinalIgnoreCase) ? 8
         : 0;

    /// <summary>图片看起来是走路精灵表还是头像？
    /// 精灵：窄条（≤64 宽）且有行走帧高度，或 16×32 单帧；
    /// 头像：宽 ≥64 且不是窄条 —— 128×320/128×640 这种多表情立绘表也算头像。
    /// ⚠ 不能用「高 ≥ 2×宽」一刀切当精灵 —— 会把 128×320 季节立绘全拒掉（角色整页消失）。</summary>
    private static void PngSize(string? path, out int w, out int h)
    {
        w = h = 0;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[24];
            if (fs.Read(buf, 0, 24) < 24) return;
            if (buf[0] != 0x89 || buf[1] != (byte)'P') return;
            w = (buf[16] << 24) | (buf[17] << 16) | (buf[18] << 8) | buf[19];
            h = (buf[20] << 24) | (buf[21] << 16) | (buf[22] << 8) | buf[23];
        }
        catch { }
    }

    /// <summary>图片尺寸，PNG 与原版 .xnb 都量得出。
    /// ⚠ 判"我们的图够不够盖住底图"必须用这个而不是 PngSize —— 后者只读 PNG 头，
    /// 原版那一侧（.xnb）会返回 0×0，于是"矮就纵向平铺补满"整段被跳过（B65 实测）。</summary>
    private static void ImageSize(string? path, out int w, out int h)
    {
        PngSize(path, out w, out h);
        if (w > 0 && h > 0) return;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            var tex = XnbDecoder.TryDecode(path);
            if (tex is not null) { w = tex.Width; h = tex.Height; }
        }
        catch { }
    }

    /// <summary>v1.7.8：这张"精灵"其实是被当成走路表的**单格立绘**吗？
    /// 1.6 村民走路表是 64 宽 × 高 ≥128（4 方向 × 32 行）；64×64 摆不出走路动画。
    /// 判据用"和这个角色的立绘一模一样"而不是纯尺寸 —— 马/宠物的表本来就是 64×64，
    /// 按尺寸一刀切会把它们误杀（实测 211 张皮肤卡里 10 张中招：Suki 的精灵与立绘是
    /// 同一个文件，Gunther/Marlon/Claire/Martin/Krobus 的"精灵"是 64×64 单格头像）。
    /// 判成真 ⇒ 这张皮肤按"没有精灵"处理 ⇒ 走 v1.7.4 那条回落：原皮精灵 → 原皮也没有就空白。</summary>
    private static bool IsFakeWalkSheet(string? sprite, string? portrait, string charId)
    {
        if (string.IsNullOrWhiteSpace(sprite) || sprite.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase))
            return false;
        // 马/宠物的走路表本来就是 64×64（每帧 16 像素，Elle's Cuter Horses 那类），
        // 按尺寸一刀切会把它们误杀 ⇒ 这几个 id 不参与判定。
        if (charId.Contains("horse", StringComparison.OrdinalIgnoreCase)
            || charId.Equals("cat", StringComparison.OrdinalIgnoreCase)
            || charId.Equals("dog", StringComparison.OrdinalIgnoreCase)
            || charId.Equals("pet", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(portrait)) return false;
        if (string.Equals(sprite, portrait, StringComparison.OrdinalIgnoreCase)) return true;
        PngSize(sprite, out var sw, out var sh);
        PngSize(portrait, out var pw, out var ph);
        if (sw <= 0 || sh <= 0) return false;
        // 1.6 村民表 = 64 宽 × 高 ≥128（4 方向 × 每行 32）；不足 4 行摆不出走路动画，
        // 那是一格头像（实测 Krobus_Trenchcoat / Claire_Vendor / Martin_Vendor 都这样被捞走）
        if (sh < 128) return true;
        return sw == pw && sh == ph && sh <= 64;
    }

    public static bool LooksLikeSprite(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        if (path.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase)) return true;
        PngSize(path, out var w, out var h);
        if (w <= 0 || h <= 0) return false;
        // 宽 ≥96 一律不是走路表（头像 128×320/640）
        if (w >= 96) return false;
        // 64×192/480 走路表；16×32、64×64 单帧/小表
        return w <= 64 && h >= 32;
    }

    private static bool LooksLikePortrait(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        if (path.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase)) return true;
        PngSize(path, out var w, out var h);
        if (w <= 0 || h <= 0) return false;
        // 高窄走路表（64×192/480）不是头像
        if (w <= 64 && h > w) return false;
        return w >= 64 && h >= 64;
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

    // ══════════════════════ 选择 → 磁盘同步 ══════════════════════

    // ══════════════════════ v1.7.7 画风（同包多画风）══════════════════════

    /// <summary>逐季画风的落盘键分隔符（角色 id 里不会出现 ␟，与季节串同一套约定）。</summary>
    private const char VariantSep = '␟';

    /// <summary>该角色当前生效的画风目录名：season 非空时读「角色 id␟季节」那条
    ///（右键按季指定的画风各存各的），否则读全局那条。没有 = null（整包一条）。</summary>
    private static string? SkinVariant(JuniGridConfig cfg, string charId, string? season = null) =>
        cfg.PortraitSkinVariants.TryGetValue(
            season is { Length: > 0 } ? charId + VariantSep + season : charId, out var v)
        && v.Length > 0 ? v : null;

    /// <summary>界面用：某个角色（可选某一季）当前记的画风目录名。</summary>
    public string? SelectedVariant(string charId, string? season = null) =>
        SkinVariant(_cfg.Current, charId, season);

    /// <summary>整张卡（可能是合并卡）生效的画风：SelectSkin 会把画风落到每个成员的键上，
    /// 所以任一成员有记录就算这张卡选了那个画风。season 非空时读该季的独立记录。</summary>
    private static string? GetMemberVariant(JuniGridConfig cfg, PortraitCharacter ch, string? season)
    {
        var ids = ch.Members.Count > 0 ? (IEnumerable<string>)ch.Members : new[] { ch.Id };
        foreach (var id in ids)
            if (SkinVariant(cfg, id, season) is { Length: > 0 } v) return v;
        return null;
    }

    /// <summary>v1.7.7：画风卡的名字尾巴（"包名 · 画风目录"）。抽出来是因为「整包」口径的
    /// 展示（批量应用那一栏）必须把它剥掉，否则一个包的批量行会写着某个画风的名字。</summary>
    public const string VariantNameSep = " · ";

    /// <summary>这条卡的显示名去掉画风尾巴 = 整包名。</summary>
    public static string PackNameWithoutVariant(PortraitSkinOption o) =>
        o.DisplayVariant is { Length: > 0 } v
        && o.PackName.EndsWith(VariantNameSep + v, StringComparison.Ordinal)
            ? o.PackName[..^(v.Length + VariantNameSep.Length)]
            : o.PackName;

    /// <summary>逐季画风的键挂在 "角色 id␟季节" 下；季节分配整条清掉时它就成了孤儿 ——
    /// 下次按同季指定时会被旧画风抢先命中（实测推演），所以两处 Remove 都要跟着摘。</summary>
    private static void DropSeasonVariants(JuniGridConfig cfg, string charId)
    {
        foreach (var k in cfg.PortraitSkinVariants.Keys
                     .Where(x => x.StartsWith(charId + VariantSep, StringComparison.OrdinalIgnoreCase))
                     .ToList())
            cfg.PortraitSkinVariants.Remove(k);
    }

    /// <summary>按「包 + 画风」取那张卡。variant 为空时取该包第一条 —— 与旧代码的
    /// FirstOrDefault(PackFolder) 逐字等价，所以没拆画风的包行为完全不变。
    /// variant 非空但包里已经没有这个画风（作者更新后删了那个目录）⇒ 回落第一条，
    /// **不清用户配置**：那是不可逆数据，目录回来选择就该复活（同 StalePack 的做法），
    /// 只在日志留一行痕 —— 界面上看不出"选的和用的不是同一张"时这是唯一线索。</summary>
    private static PortraitSkinOption? MatchOption(IEnumerable<PortraitSkinOption> opts,
        string? packFolder, string? variant, string charId = "")
    {
        PortraitSkinOption? first = null;
        foreach (var o in opts)
        {
            if (o.IsVanilla || !string.Equals(o.PackFolder ?? "", packFolder ?? "",
                    StringComparison.OrdinalIgnoreCase)) continue;
            first ??= o;
            if (variant is { Length: > 0 } && string.Equals(o.Variant, variant,
                    StringComparison.OrdinalIgnoreCase)) return o;
        }
        if (variant is { Length: > 0 })
        {
            AppLog.Warn("Portraits",
                $"[画风失效] {charId} 选的画风「{variant}」在 {packFolder} 里已经找不到，" +
                $"本次按「{first?.Variant ?? "整包"}」落盘（配置不动，画风目录回来就复活）");
            return first;
        }
        // 没记画风 = 用户从没在界面上选过 ⇒ 该钉"包当前生效的那一档"（作者 Default / config 现值），
        // 不是字母序第一条：卡片列表按包名重排过，first 完全可能是另一套画（实测 Romanceable
        // Rasmodia 的 Original 排在 CreepyKat's 后面 —— 钉错就是界面与游戏各显示一套）。
        if (first is { IsCurrentVariant: false })
            foreach (var o in opts)
                if (!o.IsVanilla && o.IsCurrentVariant
                    && string.Equals(o.PackFolder ?? "", packFolder ?? "", StringComparison.OrdinalIgnoreCase))
                    return o;
        return first;
    }

    /// <summary>选择皮肤（packFolder=null = 回官方/mod 默认）。写配置并同步磁盘。
    /// 变更提示由 UI 负责（「下次启动游戏生效」）。锁定中的角色拒绝切换。
    /// v1.7.1：只增量更新该角色的覆盖包条目（不再全量重建），换肤从秒级降到亚秒级。
    /// v1.7.7：variant = 同包多画风时选中的那个画风目录名（null = 整包一条/清除记录）。</summary>
    public void SelectSkin(string gamePath, PortraitScanResult scan, string charId, string? packFolder,
        string? variant = null)
    {
        var cfg = _cfg.Current;
        // v1.6.8：左键 = 整体统一 —— 切换全局选择时清除该角色的逐季分配（右键设置的
        // 四季各不同）。旧语义两者叠加：逐季钉入压过全局选择，用户点左键"没反应"。
        // 合并卡（同一个人的几份 NPC 数据）逐成员落配置：游戏里那几个条目才会一起换脸。
        foreach (var id in MemberIds(scan, charId))
        {
            // 用户点皮肤卡 = 明确要换这张，锁定状态要让路（旧写法在这里把选择改回锁定快照
            // 然后 continue：界面高亮了、配置没动、游戏里也没变，实机被报成"单击没用"）。
            // 只删锁定标记，其余按未锁定路径一样落配置 —— 弹窗右上锁头会跟着变回未锁。
            cfg.PortraitLocks.Remove(id);
            cfg.PortraitSeasonSkins.Remove(id);
            DropSeasonVariants(cfg, id);   // v1.7.7：季节串清了，挂在季节键上的画风也要一起摘
            cfg.PortraitTrueVanilla.Remove(id);
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
            // v1.7.7：画风单独一本（PortraitSkins 的值格式不许动，老配置要能原样读）
            if (variant is { Length: > 0 }) cfg.PortraitSkinVariants[id] = variant;
            else cfg.PortraitSkinVariants.Remove(id);
        }
        SyncToDisk(gamePath, scan, MemberIds(scan, charId));
        _cfg.Save(cfg);
    }

    // ══════════════════════ v1.7 锁定 / 一键恢复 / 一键应用 ══════════════════════

    /// <summary>解析锁定条目（损坏 JSON 返回 null，当作未锁定）。</summary>
    public static PortraitLockInfo? ParseLock(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var j = Newtonsoft.Json.Linq.JObject.Parse(raw);
            return new PortraitLockInfo(
                j["pack"]?.ToString() ?? "",
                j["pin"]?.ToString() is { Length: > 0 } p ? p : null,
                j["season"]?.ToString() is { Length: > 0 } s ? s : null);
        }
        catch { return null; }
    }

    public static string SerializeLock(PortraitLockInfo info) =>
        new Newtonsoft.Json.Linq.JObject(
            new Newtonsoft.Json.Linq.JProperty("pack", info.PackFolder ?? ""),
            new Newtonsoft.Json.Linq.JProperty("pin", info.PinFile ?? ""),
            new Newtonsoft.Json.Linq.JProperty("season", info.Season ?? "")
        ).ToString(Newtonsoft.Json.Formatting.None);

    /// <summary>该 NPC 条目是否处于锁定（合并卡任一成员锁定即视为锁定 —— 写盘按成员走）。</summary>
    public static bool IsLocked(JuniGridConfig cfg, string charId) =>
        cfg.PortraitLocks.ContainsKey(charId);

    /// <summary>取锁定信息；未锁定或解析失败返回 null。</summary>
    public static PortraitLockInfo? GetLock(JuniGridConfig cfg, string charId) =>
        cfg.PortraitLocks.TryGetValue(charId, out var raw) ? ParseLock(raw) : null;

    /// <summary>
    /// 锁定/解锁当前生效肖像。locked=true 时把「当前生效选项 + 当前预览季的那张图」
    /// 钉死：游戏内四季不再轮换。解锁只删锁定标记，保留皮肤选择。
    /// </summary>
    public void SetLocked(string gamePath, PortraitScanResult scan, string charId, bool locked,
        PortraitSkinOption? opt, string? pinFile, string? season)
    {
        var cfg = _cfg.Current;
        foreach (var id in MemberIds(scan, charId))
        {
            if (locked)
            {
                var pack = opt?.PackFolder ?? "";
                // 锁定瞬间清掉逐季分配，否则 WriteOverridePack 仍会按季钉变体
                cfg.PortraitSeasonSkins.Remove(id);
                DropSeasonVariants(cfg, id);
                cfg.PortraitLocks[id] = SerializeLock(new PortraitLockInfo(pack, pinFile, season));
                // v1.7.7：锁的就是某张画风卡时把画风一起钉住 —— 解锁后这条选择要能原样复活
                if (opt?.Variant is { Length: > 0 } v0) cfg.PortraitSkinVariants[id] = v0;
                else cfg.PortraitSkinVariants.Remove(id);
                // 锁到默认行时同时进原版默认名单（覆盖包要压住其它启用包）
                if (pack.Length == 0)
                {
                    if (!cfg.PortraitVanillaDefaults.Contains(id)) cfg.PortraitVanillaDefaults.Add(id);
                    cfg.PortraitSkins.Remove(id);
                }
                else
                {
                    cfg.PortraitSkins[id] = pack;
                    cfg.PortraitVanillaDefaults.Remove(id);
                }
            }
            else
            {
                cfg.PortraitLocks.Remove(id);
            }
        }
        SyncToDisk(gamePath, scan, MemberIds(scan, charId));
        _cfg.Save(cfg);
    }

    /// <summary>
    /// 一键恢复默认：清掉全部皮肤选择 / 季节分配 / 锁定；原版角色强制钉【原版 xnb】
    ///（冈瑟/马龙/法师这类 SVE 也重绘了默认的，统一回原版而不是扩展包默认像）。
    /// 范围是全局字典 —— 用户点「一键」就是要所有角色，不止当前扫描到的那批。
    /// </summary>
    public void RestoreAllDefaults(string gamePath, PortraitScanResult scan)
    {
        var cfg = _cfg.Current;
        cfg.PortraitSkins.Clear();
        cfg.PortraitSeasonSkins.Clear();
        cfg.PortraitSkinVariants.Clear();   // v1.7.7：画风键与 PortraitSkins 同级，整本一起清
        cfg.PortraitLocks.Clear();
        cfg.PortraitTrueVanilla.Clear();
        cfg.PortraitVanillaDefaults.Clear();
        foreach (var ch in scan.Characters)
        {
            if (ch.Hidden) continue;
            if (!ch.IsVanilla) continue;
            foreach (var id in MemberIds(scan, ch.Id))
            {
                if (!cfg.PortraitVanillaDefaults.Contains(id)) cfg.PortraitVanillaDefaults.Add(id);
                if (!cfg.PortraitTrueVanilla.Contains(id)) cfg.PortraitTrueVanilla.Add(id);
            }
        }
        SyncToDisk(gamePath, scan);
        _cfg.Save(cfg);
    }

    /// <summary>
    /// 一键应用：把指定皮肤包套到「所有拥有该包皮肤选项」的 NPC 上。
    /// 已锁定的 NPC 跳过（整卡禁用，不允许被批量改写）。返回 (成功数, 跳过锁定数)。
    /// 批量写配置、单次 SyncToDisk —— 逐角色 SelectSkin 会触发 N 次全量覆盖包重建。
    /// </summary>
    public (int Applied, int SkippedLocked) ApplyPackToAll(
        string gamePath, PortraitScanResult scan, string packFolder)
    {
        var cfg = _cfg.Current;
        var applied = 0;
        var skipped = 0;
        foreach (var ch in scan.Characters)
        {
            if (ch.Hidden) continue;
            // 该角色是否有这个包的可选皮肤（默认/娘家行不算「有这个包的皮肤」——
            // 批量应用的是第三方肖像包，不是把所有人打回默认）
            var has = ch.Skins.Any(s =>
                string.Equals(s.PackFolder ?? "", packFolder, StringComparison.OrdinalIgnoreCase)
                && s.SourceFile is not null);
            if (!has) continue;
            var members = MemberIds(scan, ch.Id);
            if (members.Any(id => IsLocked(cfg, id)))
            {
                skipped++;
                continue;
            }
            foreach (var id in members)
            {
                cfg.PortraitSeasonSkins.Remove(id);
                cfg.PortraitTrueVanilla.Remove(id);
                cfg.PortraitSkins[id] = packFolder;
                cfg.PortraitVanillaDefaults.Remove(id);
                // v1.7.7：批量应用一次给的是整包，不指定画风 ⇒ 清掉记录 = 用该包第一条
                //（画风目录里的当前 config 值那一套），与覆盖包的解析口径保持一致
                DropSeasonVariants(cfg, id);
                cfg.PortraitSkinVariants.Remove(id);
            }
            applied++;
        }
        if (applied > 0)
        {
            SyncToDisk(gamePath, scan);
            _cfg.Save(cfg);
        }
        return (applied, skipped);
    }

    /// <summary>列出可一键应用的皮肤包（有 ≥1 个角色可选皮肤的包，按显示名排序）。
    /// SampleSource = 该包给某角色的一张立绘，列表封面用。</summary>
    public static List<(string PackFolder, string PackName, int CharCount, string? SampleSource)> ListAppliablePacks(
        PortraitScanResult scan)
    {
        var map = new Dictionary<string, (string Name, HashSet<string> Chars, string? Sample)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var ch in scan.Characters)
        {
            if (ch.Hidden) continue;
            foreach (var s in ch.Skins)
            {
                if (string.IsNullOrWhiteSpace(s.PackFolder) || s.SourceFile is null) continue;
                if (!map.TryGetValue(s.PackFolder, out var e))
                    // v1.7.7：画风卡的名字带 " · 画风" 尾巴，这一栏是「整包批量应用」，必须显整包名
                    map[s.PackFolder] = e = (PackNameWithoutVariant(s),
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase), s.SourceFile);
                e.Chars.Add(ch.Id);
                if (e.Sample is null) e = (e.Name, e.Chars, s.SourceFile);
            }
        }
        return map
            .Select(kv => (kv.Key, kv.Value.Name, kv.Value.Chars.Count, kv.Value.Sample))
            .OrderByDescending(x => x.Item3)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
            // v1.7.29：选中的皮肤里有【走 HD 通道的包】（只写 Mods/HDPortraits/<角色>、不碰
            // Portraits/ 那种，例：[CP] Dacar Rasmodia Portraits）时，HDP 必须开着 —— 通道一关，
            // 游戏就退回读 Portraits/<角色>，而那张我们故意不钉（512 宽的表当普通立绘钉会被 CP
            // 整条拒绝）⇒ 脸直接掉回别家 mod。
            var hdChosen = scan.HdSkins.Any(h => _cfg.Current.PortraitSkins.Values
                .Any(v => string.Equals(v, h.Pack, StringComparison.OrdinalIgnoreCase)));

            JObject root;
            using (var sr = new StreamReader(configPath))
                root = JObject.Parse(sr.ReadToEnd());
            var cur = root["active"]?.ToString();

            // ⚠ v1.7.29 起【素材包不再是打开 HDP 的理由】—— 这是原来那个真正的 bug：
            // active="HDP" 开的是 Portraiture 的「HD Portraits 桥接模块」，不是"用我选的这个素材包"。
            // 旧写法让"给艾米丽点一下素材包"顺手把全局 HD 通道打开，于是任何写了
            // Mods/HDPortraits/<角色> 的 NPC 都被那个包接管（实测：法师的脸变成 [CP] Dacar 那张，
            // 而且因为通道是艾米丽的选择打开的，从法师页面上根本看不出是谁干的）。
            // 素材包现在由 ModService.AutoConvertLoosePortraits 转成 CP 包、覆盖包直接钉，
            // Portraiture 退回它该待的位置：只当别的 mod 的前置依赖。
            string? want = hdChosen ? "HDP" : cur == "HDP" ? "Vanilla" : null;
            // want==null = 这个值是用户/别的 mod 自己设的（素材包名、预设名…），我们不猜、不覆盖。
            // 这条同时保住了 v1.6.6 的护栏：框架只是别人家的依赖时不许硬切成 Vanilla。
            if (want is null || string.Equals(cur, want, StringComparison.OrdinalIgnoreCase)) return;
            root["active"] = want;
            var tmp = configPath + ".junigrid.tmp";
            // 同样是记账写入：别让 Portraiture 在 Mods 列表里乱跳
            KeepDirTime(fwDir, () =>            {
                File.WriteAllText(tmp, root.ToString(Newtonsoft.Json.Formatting.Indented));
                File.Move(tmp, configPath, true);
            });
            AppLog.Warn("Portraits", $"Portraiture 框架模式已切换为 {want}");
        }
        catch (Exception ex)
        { AppLog.Warn("Portraits", "同步 Portraiture 模式失败: " + ex.Message); }
    }

    /// <summary>配置→磁盘单向同步（幂等）：生成/更新 JuniGrid 覆盖包（最高优先级
    /// EditImage 被选中角色的立绘/精灵表），**不再启停任何 mod** ——
    /// "没选中的包禁用"会连功能性 mod 一起废掉（Childhood Sweetheart 还有对话/事件，
    /// 实机用户反馈）。mod 本体的启停完全归 Mods 页管。
    /// onlyIds 非空时只增量重建这些 NPC 的条目（换肤热路径）；null = 全量。</summary>
    public void SyncToDisk(string gamePath, PortraitScanResult scan, IReadOnlyList<string>? onlyIds = null)
    {
        // 覆盖包被禁用（Mods 页那个开关 = 目录改名 .前缀）⇒ 用户要的就是"肖像页整体不生效、
        // 游戏按各 mod 自己的图"。这里只跳过落盘：配置里的选择照记，重新启用后下一次同步补齐。
        // ⚠ 连别的包的 config.json 开关也不碰 —— 那同样是"改 mod 自己的决定"。
        if (OverridePackDisabled(gamePath))
        {
            AppLog.Info("Portraits", "[覆盖包] 已被禁用（Mods 页）⇒ 本次同步整体跳过");
            return;
        }
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
        WriteOverridePack(gamePath, scan, skins, cfg.PortraitVanillaDefaults, onlyIds);

        // v1.7.29：Portraiture 的 HDP 模式必须在这里一起对账 —— 以前只在四个"用户动作"方法里
        // 各调一次，启动自检 / 进肖像页那条对账路径（只走 SyncToDisk）从不校正。于是模式会漂走：
        // 用户把艾米丽从 Portraiture 素材包换成 CP 包 ⇒ active 被写回 Vanilla，而法师选的是走
        // HD 通道的 Dacar ⇒ 通道不画、我们又不钉 Portraits/ ⇒ 游戏里掉回别家 mod 的脸。
        SyncPortraitureActive(gamePath, scan);

        // 选中包的 per-char config 开关照写（config.json 是用户设置，不算改 mod 内容）。
        // 增量模式下只碰该角色相关的包，避免每次换肤遍历全部 ConfigKeys。
        var modsDir = Path.Combine(gamePath, "Mods");
        var seasonEnabled = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (chId, raw) in cfg.PortraitSeasonSkins)
        {
            if (onlyIds is not null && !onlyIds.Contains(chId, StringComparer.OrdinalIgnoreCase)) continue;
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
        IEnumerable<string> packCandidates = skins
            .Where(kv => onlyIds is null || onlyIds.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
            .Select(kv => kv.Value)
            .Concat(seasonEnabled.Keys)
            .Where(f => f.Length > 0 && !f.StartsWith("Portraiture/", StringComparison.OrdinalIgnoreCase));
        // 全量时仍要处理所有选中包（批量应用/恢复默认）；增量时只处理本次角色用到的包
        if (onlyIds is null)
            packCandidates = skins.Values.Concat(seasonEnabled.Keys)
                .Where(f => f.Length > 0 && !f.StartsWith("Portraiture/", StringComparison.OrdinalIgnoreCase));
        var selectedPacks = packCandidates
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
            WritePackConfig(dir, pack, scan, enabledChars: enabled,
                variants: _cfg.Current.PortraitSkinVariants);
        }
    }

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

}

