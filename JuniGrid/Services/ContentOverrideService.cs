using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace JuniGrid.Services;

/// <summary>
/// 纯净版覆盖：把"整张替图"型的裸包（自带 XNB，或 PNG 由我们编成 XNB）直接写进游戏本体的
/// <c>Content\</c>，不需要 SMAPI、不需要 Content Patcher。
///
/// 与 <see cref="ModService"/> 那条覆盖包链的分工判据是【文件落到谁的目录里】：
/// 落到 Content 的走这里，落到别人 mod 目录的走覆盖包，两边都不是的照旧转 CP 包。
///
/// 这条能力天然比 CP 窄，界面文案不许超过这里写的范围：
///  · 只能整张换表 —— CP 的 EditImage 能只改贴图的一小块（FromArea/ToArea），这里做不到；
///  · 只能换纹理与字体 —— Data/Maps 那类资产没有对应编码器，改不了单个键。
///    字体（Fonts/SmallFont 那种 SpriteFont）能整份替换：它解不出纹理，但 XNB 是自描述容器，
///    游戏按包里那份表自己的字号/行距渲染，所以我们不必读懂它，只要原样写进去。
///
/// 【尺寸要求拆成宽高两条】（2026-10-05 改）：原代码在这里写着"尺寸必须逐像素相等，否则会画成
/// 大红脸"，据此把 13 张 _Beach 立绘判成不适配 —— 那条结论太粗，但改成"都不拦"同样错。分开看：
///   · 高度不等 ⇒ 可以照装。XNB 自描述，游戏按固定行距从顶上往下切格，源表矮了只是下面几格
///     取不到东西（那个表情画不出），高了只是多出的几格没人去索引。没有一个源像素换过行。
///   · 宽度不等 ⇒ 必须拦。一行里的列数变了，第 N 格直接落到别人的像素上 —— 那是画错人，不是少帧。
///   · 包里只交 PNG 的那一张 ⇒ 不收。这条路是往本体 Content 里换 .xnb，作者只给 PNG 就是
///     交给 CP 加载的形状（2026-10-10 定，见 <c>Plan</c> 的 png 分支：以前在这儿重编，
///     实测把效果图文件夹当成了立绘的"版本"）。
/// 旁证与它的边界：CP 的 EditImage(PatchMode=Replace) 从不比对源与目标的尺寸，用户 Mods 里
/// [CP] Portrait Anime Mods OhoDavi 的 assets/Elliott_Beach.png 是 128×128 而本体那张是 128×320。
/// 但这只证明【CP 不拦】，不证明【游戏画对】—— 那 3 个缺失表情在游戏里究竟长什么样，仍未开机验证。
///
/// 【立绘与走路表可按宽度分开】（同日实测）：101 张 Portraits/* 恒 128 宽、
/// 125 张 Characters/* 恒 64 宽，226 张无例外。所以裸包里一个同名文件属于哪张表，
/// 宽度就能定 —— 不必再靠"尺寸完全相等"才能认出目标，格数不同的旧图照样装得上。
///
/// 【同名候选要按"能不能整张换"筛】（同日实测）：一个角色名在本体里能撞出 5 份资产，
/// 其中 Characters/schedules/*、Strings/schedules/*、Characters/Dialogue/* 是
/// DictionaryReader&lt;String,String&gt; 的纯文本（角色行程与对白）—— 我们没有编码器，换不了。
/// 它们解不出纹理，早先与字体一起被 area==0 吸进候选，撑成"同名歧义"，
/// 害得 26 个角色装不上，而包里那张 128×320 跟本体 Portraits/Abigail 的 128×320 一模一样。
/// 判据见 <see cref="IsSwappable"/>：只认 Texture2DReader 与 SpriteFontReader 两个类型标记，
/// 【不能】用 area&gt;0 —— 文本表也能算出正的面积。
///
/// 原版字节与包副本放在游戏目录的 <c>JuniGrid\</c> 子文件夹里（与 Mods 同级，写不动才回落
/// AppData）——【绝不在 Content 里留任何多余文件】：Content 里躺一个
/// <c>Abigail.xnb.junigrid_backup</c> 会被 Steam「验证完整性」判成多余文件，而那次验证会把
/// 我们所有覆盖静默还原掉。原版仓库丢了也不要紧 —— 它随时能从 Steam 拿回来，
/// 这跟宿主 mod 里那份唯一的 zh.json 不是一回事，所以那边的就地备份规矩不搬过来。
/// </summary>
public static partial class ContentOverrideService
{
    public static string ContentRoot(string gamePath) => Path.Combine(gamePath, "Content");

    public static string AssetFullPath(string gamePath, string asset)
        => Path.Combine(ContentRoot(gamePath), asset.Replace('/', Path.DirectorySeparatorChar) + ".xnb");

    // ───────────────────── 存储位置 ─────────────────────
    //
    // 覆盖素材（原版仓库 / 包的副本 / 原包备份）放在【游戏目录里的 JuniGrid 文件夹】，
    // 与 Mods 同级 —— 这些东西改的是游戏本体，跟游戏躺在一起才讲得通，也不占 C 盘。
    // 游戏装在 Program Files 这类写不动的位置时回落到配置目录（记日志，功能不断）。

    /// <summary>老位置（C 盘配置目录）里的两个目录名 —— 迁移与清理只认这两个名字。</summary>
    private const string LegacyOverridesDir = "content-overrides";
    private const string LegacyOriginalsDir = "content-originals";
    /// <summary>派生缓存（写进 Content 那一份的副本）现在的目录名，与 asset-packs 同级。</summary>
    private const string DerivedDir = "content-derived";
    /// <summary>派生缓存上一版待过的地方（<c>asset-packs/.store</c>）—— 只用于搬家。</summary>
    private const string LegacyStoreDir = ".store";
    /// <summary>原版仓库「已按版本分层」的凭据（见 <see cref="VersionizeOriginals"/>）。</summary>
    private const string VersionedMark = ".versioned";
    /// <summary>认不出游戏版本时原版仓库落的那一层 —— 不拿它当版本号用，只做落点。</summary>
    private const string UnknownVersion = "_unknown";

    private static readonly ConcurrentDictionary<string, bool> _rootWritable = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>游戏目录里那个共享存储文件夹的名字。版本切换要靠它认出「这一份不属于本体」，
    /// 所以两处（这里和 DepotDownloaderService）必须同一个字面量。</summary>
    public const string SharedStoreFolder = "JuniGrid";

    /// <summary>共享存储在游戏目录里的【位置本身】—— 与 <see cref="StoreRoot"/> 不同，
    /// 这里不判断可写性、也不跟随 AppData 回落：写不进去的时候那儿根本没有这个文件夹。</summary>
    public static string SharedStoreDir(string gamePath) => Path.Combine(gamePath, SharedStoreFolder);

    /// <summary>游戏侧存储根。写不动游戏目录时回落 <c>ConfigDir/game-store</c>（探测结果按根缓存，
    /// 不每次 stat）。</summary>
    public static string StoreRoot(string gamePath)
    {
        if (!string.IsNullOrWhiteSpace(gamePath))
        {
            var root = SharedStoreDir(gamePath);
            var ok = _rootWritable.GetOrAdd(root, static r =>
            {
                try
                {
                    Directory.CreateDirectory(r);
                    var probe = Path.Combine(r, ".write-probe");
                    File.WriteAllText(probe, "");
                    File.Delete(probe);
                    return true;
                }
                catch (Exception ex)
                {
                    AppLog.Warn("ContentOverride", "游戏目录写不进去，覆盖素材改存配置目录：" + ex.Message);
                    return false;
                }
            });
            if (ok) return root;
        }
        return Path.Combine(ConfigService.ConfigDir, "game-store");
    }

    /// <summary>原版仓库的根。</summary>
    public static string OriginalsRoot(string gamePath)
        => Path.Combine(StoreRoot(gamePath), LegacyOriginalsDir);

    /// <summary>原版仓库的「哪一版」这一层。替图本身与版本无关（包和副本因此全版本共用），
    /// 但【原版长什么样】是版本属性 —— 1.5 的 JunimoNote 和 1.6 的不是同一张图。
    /// 少了这一层，存储一共享，<see cref="Commit"/> 里「已经有原版就不覆盖」就会让第二个版本
    /// 永远存不进自己的原版，之后还原 = 把第一个版本的原版画进第二个版本的本体。
    /// 认不出版本时落到 <c>_unknown</c>：宁可不认识，也不替它编一个版本号出来。</summary>
    public static string OriginalVersionSegment(string gamePath)
    {
        var v = UpdateService.ResolveCurrentVersion(gamePath);
        return string.IsNullOrWhiteSpace(v) ? UnknownVersion : Sanitize(v);
    }

    /// <summary>原版仓库。按「版本 + 资产」存一份，全局唯一 —— 第二个包绝不覆盖它，
    /// 否则"原版"就变成了第一个包的内容，还原会还原出 mod 的图。</summary>
    public static string OriginalPath(string gamePath, string asset)
        => OriginalPath(gamePath, asset, null);

    /// <summary>指定落在哪一版那一层。<paramref name="version"/> 为空 = 当前本体那一版
    /// （写盘路径都用这个，因为「现在动这张图」时该抄的就是现本体）；
    /// 还原/对账要按<b>记录自己那一版</b>读，否则切过版本之后会去空的那一层找原件，
    /// 把「还原不了」误报成「这张是我们新增的、该删」。</summary>
    public static string OriginalPath(string gamePath, string asset, string? version)
        => Path.Combine(OriginalsRoot(gamePath),
            string.IsNullOrWhiteSpace(version) ? OriginalVersionSegment(gamePath) : Sanitize(version),
            asset.Replace('/', Path.DirectorySeparatorChar) + ".orig.xnb");

    /// <summary>素材包原包备份目录的根（用户可见：每个包一个文件夹，结构与原包一致）。</summary>
    public static string PacksRoot(string gamePath)
        => Path.Combine(StoreRoot(gamePath), "asset-packs");

    /// <summary>一个包的原包备份文件夹（打开文件夹的目标；结构与原包逐字节一致）。</summary>
    public static string PackOriginalDir(string gamePath, string packDir)
        => Path.Combine(PacksRoot(gamePath), packDir);

    /// <summary>派生缓存的根：我们写进 Content 那一份的副本。与 <c>asset-packs</c> 【同级】，
    /// 不塞在它下面 —— asset-packs 是用户会点开翻的地方（里面每个文件夹就是一个原包），
    /// 而这里躺的是 12 位哈希，看着就像残留垃圾。实测代价：2026-10-04 他嫌"包里存的怎么是编号"，
    /// 直接把 <c>asset-packs/.store</c> 删进了回收站，6 条记录当场变成只能看不能用的死账。</summary>
    public static string DerivedRoot(string gamePath) => Path.Combine(StoreRoot(gamePath), DerivedDir);

    /// <summary>我们写进去那份的副本：开关关掉再打开时不必重新下载，PNG 包也不必重编一遍。</summary>
    public static string StoreDir(string gamePath, string storeId)
        => Path.Combine(DerivedRoot(gamePath), storeId);

    public static string StoreFile(string gamePath, string storeId, string asset)
        => Path.Combine(StoreDir(gamePath, storeId), asset.Replace('/', Path.DirectorySeparatorChar) + ".xnb");

    public static string KeyOf(string asset, string slot) => asset + "|" + slot;
    public static string SlotOf(string packName, int? nexusModId)
        => nexusModId is > 0 ? "N" + nexusModId : Sanitize(packName);

    public static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Trim();
    }
}
