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
public static class ContentOverrideService
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

    // ───────────────────── 老位置（C 盘配置目录）迁移 ─────────────────────

    private static readonly ConcurrentDictionary<string, byte> _migrated = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>把老位置（ConfigDir/content-overrides、content-originals）的库存搬进游戏侧新根。
    /// 幂等 + 每个根一次：进素材包页 / 安装 / 启动都会调，只有第一次真搬。
    /// 跨盘搬 = 先整树复制、逐个数过文件数再删老目录 —— 不验数就删等于拿还原能力赌拷贝没断。</summary>
    public static void MigrateLegacyStore(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return;
        var root = StoreRoot(gamePath);
        if (!_migrated.TryAdd(root, 0)) return;
        try
        {
            MoveTree(Path.Combine(ConfigService.ConfigDir, LegacyOverridesDir), DerivedRoot(gamePath));
            MoveTree(Path.Combine(ConfigService.ConfigDir, LegacyOriginalsDir), OriginalsRoot(gamePath));
            // 派生缓存从 asset-packs/.store 挪到与它同级的 content-derived：
            // 用户会点开 asset-packs 翻自己的包，那里不该躺着哈希目录
            MoveTree(Path.Combine(PacksRoot(gamePath), LegacyStoreDir), DerivedRoot(gamePath));
            VersionizeOriginals(gamePath);
        }
        catch (Exception ex) { AppLog.Warn("ContentOverride", "老缓存搬家失败（下次启动再试）：" + ex.Message); }
    }

    /// <summary>把「按资产一层」的老原版仓库收进当前版本那一层。老条目就是在当时那一版下抄来的，
    /// 归到现役版本这一层是唯一说得通的落点 —— 但只在【还没分过层】时这么归，
    /// 所以拿一个标记文件当分过层的凭据：<c>content-originals/Portraits/x.orig.xnb</c> 与
    /// <c>content-originals/1.6.15/x.orig.xnb</c> 光看深度分不出来，靠猜迟早把两版混一层。
    /// 半途崩了标记不会落下次，重跑时已经在版本层里的条目会被跳过 —— 幂等。</summary>
    private static void VersionizeOriginals(string gamePath)
    {
        var root = OriginalsRoot(gamePath);
        var mark = Path.Combine(root, VersionedMark);
        if (!Directory.Exists(root) || File.Exists(mark)) return;
        var ver = OriginalVersionSegment(gamePath);
        // 认不出版本 ⇒ 不猜该归到哪一层。宁可让老条目原地躺着（还原会点名失败），
        // 也不能把 1.6 抄来的原版标成 1.5 —— 标错是静默的，失败是响的。
        if (ver == UnknownVersion) return;
        var dst = Path.Combine(root, ver);
        foreach (var e in Directory.EnumerateFileSystemEntries(root))
        {
            var name = Path.GetFileName(e);
            if (name.Equals(ver, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(VersionedMark, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(dst);
            Directory.Move(e, Path.Combine(dst, name));
        }
        File.WriteAllText(mark, ver);
    }

    /// <summary>老账没有版本戳时按证据回填：<b>原版实际躺在哪一层</b>。
    /// 一条账的原版在 <c>content-originals/&lt;某版&gt;/</c> 下唯一命中，就说明这条账是那一版抄的；
    /// 当前版本那一版优先（它就是现在这副本体，之后要继续用）。
    /// 多版都命中又都不像当前版 ⇒ 说不清，留空，界面把它归进「版本待确认」，绝不猜一个。
    /// 只在缺戳时动；一次都没缺就一个字节都不写。</summary>
    public static int BackfillVersions(ConfigService cfg, string gamePath)
    {
        var need = cfg.Current.ContentOverrides.Values.Where(r => r.Version.Length == 0).ToList();
        var root = OriginalsRoot(gamePath);
        if (need.Count == 0 || !Directory.Exists(root)) return 0;
        string[] layers;
        try { layers = Directory.GetDirectories(root); }
        catch { return 0; }
        var current = OriginalVersionSegment(gamePath);
        var changed = 0;
        foreach (var r in need)
        {
            var rel = r.Asset.Replace('/', Path.DirectorySeparatorChar) + ".orig.xnb";
            var hits = layers
                .Where(l => File.Exists(Path.Combine(l, rel)))
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();
            if (hits.Count == 0) continue;
            var pick = hits.FirstOrDefault(n => string.Equals(n!, current, StringComparison.OrdinalIgnoreCase))
                       ?? (hits.Count == 1 ? hits[0] : null);
            if (pick is null) continue;
            r.Version = pick!;
            changed++;
        }
        if (changed > 0)
        {
            cfg.Save(cfg.Current);
            AppLog.Warn("ContentOverride", $"[版本回填] {changed} 条老账按原版所在层补上了版本戳");
        }
        return changed;
    }

    /// <summary>丢掉没有记录引用的副本目录（<c>content-derived/&lt;12位哈希&gt;/</c>）。
    /// 每条记录写盘时都会拿一个新的随机 id，旧 id 只在"同一条记录被覆盖"那一刻删；
    /// 配置被换过、或删记录的路径没走到 DropStore，目录就永远留在盘上
    /// （2026-10-07 实测：盘上 221 个、账上只引用 182 个）。
    /// <para><b>算术护栏</b>：账上一条记录都没有、或有引用的 id 在盘上找不到 —— 这两种情况都说明
    /// "引用集"本身不可信，此时删任何目录都可能是在删刚装好的包。2026-10-02 就是按"没引用就删"
    /// 清 AppData，把 268 个活目录送走了，所以宁可不清。</para>
    /// <para>10 分钟内写过的目录一律豁免：安装是"先写副本、后落账"，中间那几秒它天然无主。</para></summary>
    public static int PruneUnreferencedStores(ConfigService cfg, string gamePath)
    {
        var root = DerivedRoot(gamePath);
        if (!Directory.Exists(root)) return 0;
        var refs = cfg.Current.ContentOverrides.Values
            .Where(r => r.StoreId.Length > 0).Select(r => r.StoreId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] dirs;
        try { dirs = Directory.GetDirectories(root); }
        catch { return 0; }
        var have = dirs.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // 引用集必须是盘上目录集的非空子集，否则连"谁活着"都说不清 —— 这时一个都不删。
        if (refs.Count == 0 || !have.IsSupersetOf(refs))
        {
            if (dirs.Length > refs.Count)
                AppLog.Warn("ContentOverride",
                    $"[副本清理] 跳过：账上引用 {refs.Count} 个、盘上 {dirs.Length} 个，两者对不上时不动盘");
            return 0;
        }
        var fresh = DateTime.UtcNow.AddMinutes(-10);
        var gone = 0;
        foreach (var d in dirs)
        {
            if (refs.Contains(Path.GetFileName(d))) continue;
            try
            {
                if (Directory.GetLastWriteTimeUtc(d) > fresh) continue;
                Directory.Delete(d, true);
                gone++;
            }
            catch (Exception ex) { AppLog.Warn("ContentOverride", "[副本清理] " + ex.Message); }
        }
        if (gone > 0) AppLog.Warn("ContentOverride", $"[副本清理] 丢掉 {gone} 个无主副本目录");
        return gone;
    }

    private static void MoveTree(string src, string dst)
    {
        if (!Directory.Exists(src)) return;
        var files = Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories).ToList();
        foreach (var f in files)
        {
            var rel = Path.GetRelativePath(src, f);
            var to = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to, overwrite: true);
        }
        // 数对得上才删老目录；对不上就留着（下次再搬），绝不两头落空
        var copied = Directory.Exists(dst)
            ? Directory.EnumerateFiles(dst, "*", SearchOption.AllDirectories).Count() : 0;
        if (copied >= files.Count) Directory.Delete(src, true);
    }

    // ───────────────────── 原包备份（打开文件夹的目标）─────────────────────

    /// <summary>把安装用的解包目录原样存进 asset-packs 下，返回落点的文件夹名（记进账）。
    /// 结构与原包逐字节一致 —— 人打开看到的就是当初下载的那个 mod，而不是我们编出来的 xnb 堆。
    /// 同名撞车时追加 -2/-3：两个包撞名是常事，挤进一个文件夹就分不清谁是谁了。
    /// 失败（盘满/权限）只记日志返回 ""——原包备份是锦上添花，不该让安装本身失败。</summary>
    public static string PreserveOriginalPack(string gamePath, string srcDir, string packName, string slot,
        ConfigService cfg)
    {
        try
        {
            var baseName = Sanitize(packName);
            if (baseName.Length == 0) baseName = "pack";
            var root = PacksRoot(gamePath);
            var name = baseName;
            for (var i = 2; Directory.Exists(Path.Combine(root, name)); i++)
            {
                // 同一个槽位重装 = 同名的自己人：先清掉旧的再落新的（内容就是该更新）
                var owner = cfg.Current.ContentOverrides.Values
                    .FirstOrDefault(v => v.PackDir.Equals(name, StringComparison.OrdinalIgnoreCase))?.Slot;
                if (owner is not null && owner.Equals(slot, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Delete(Path.Combine(root, name), true);
                    break;
                }
                name = baseName + "-" + i;
            }
            var dst = Path.Combine(root, name);
            CopyTree(srcDir, dst);
            return name;
        }
        catch (Exception ex)
        {
            AppLog.Warn("ContentOverride", "原包备份失败（不影响安装结果）：" + ex.Message);
            return "";
        }
    }

    private static void CopyTree(string src, string dst)
    {
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(dst, Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to, overwrite: true);
        }
    }

    // ───────────────────── 账外覆盖：本体被"账上没有的东西"盖着 ─────────────────────

    /// <summary>账外那张图的暂存处。挪开之前必须先把它留一份 —— 这台机器上往往【只有这一份】
    /// （实测 2026-10-09：6 张兽化立绘在 <c>content-derived</c> 与 <c>asset-packs</c> 里
    /// 逐字节搜不到），直接写回留底等于把它销毁，而他以后可能还想要。</summary>
    public static string ForeignRoot(string gamePath) => Path.Combine(StoreRoot(gamePath), "content-foreign");

    /// <summary>按版本分层存，与 <see cref="OriginalPath(string,string,string)"/> 同一个理由：
    /// 不同本体上的图不能混在一层。</summary>
    public static string ForeignPath(string gamePath, string asset, string version)
        => Path.Combine(ForeignRoot(gamePath), Sanitize(version),
            asset.Replace('/', Path.DirectorySeparatorChar) + ".xnb");

    /// <summary>一张对不上留底的图。<paramref name="Moved"/> = false：本体上此刻是那张对不上的图；
    /// = true：这张曾被挪开过（本体上是我们记下的那份），那份来路不明的图存在
    /// <c>content-foreign</c> 里 —— 界面上仍然列出来，但<b>不给任何写盘动作</b>：
    /// 我们手上最好的证据只是"动手前盘上的样子"，它本身可能就是别人盖上去的。</summary>
    public sealed record StrayOverride(string Asset, string Version, string CurrentFile,
        string OriginalFile, string ForeignFile, bool Moved, long OriginalSize,
        DateTime OriginalCapturedAt);

    /// <summary>点名"有留底、账上却没这条记录、现值又不是我们记下的那份"的资产。
    ///
    /// <para>为什么需要它（2026-10-09 实机）：他装过一个兽化内容包，删掉之后 6 张村民立绘
    /// 仍被盖着 —— 而整页的数据起点是 <c>ContentOverrides</c>，那 6 张一条记录都没有，
    /// 于是「全部还原」按下去一张都不碰。用户看到的现象就是"还原是坏的"。
    /// 这一栏只负责让它现形，恢复原版交给 Steam（我们不揽网络那条链路）。</para>
    ///
    /// <para>三条判据各挡一种瞎报：① <b>必须有留底</b> —— 没留底＝我们从来没动过这张图，
    /// 手里没有任何"它该长什么样"的依据，报出来就是猜；② <b>账上有记录的一律不报</b> ——
    /// 那是既有状态机（<see cref="StateOf"/>）的地盘，重复报会两边打架；
    /// ③ <b>只看当前版本那一层</b> —— 别的版本的留底连"我们记下了什么"都说不清当前本体。</para></summary>
    public static List<StrayOverride> Strays(ConfigService cfg, string gamePath)
    {
        var list = new List<StrayOverride>();
        if (string.IsNullOrWhiteSpace(gamePath)) return list;
        var ver = OriginalVersionSegment(gamePath);
        if (ver == UnknownVersion) return list;          // 认不出版本 ⇒ 不分层对账，也不猜一层
        var layer = Path.Combine(OriginalsRoot(gamePath), ver);
        if (!Directory.Exists(layer)) return list;
        var tracked = new HashSet<string>(
            cfg.Current.ContentOverrides.Values.Select(v => v.Asset), StringComparer.OrdinalIgnoreCase);
        foreach (var orig in Directory.EnumerateFiles(layer, "*.orig.xnb", SearchOption.AllDirectories))
        {
            var asset = Path.GetRelativePath(layer, orig)
                .Replace('\\', '/')[..^".orig.xnb".Length];
            if (tracked.Contains(asset)) continue;
            var cur = AssetFullPath(gamePath, asset);
            var foreign = ForeignPath(gamePath, asset, ver);
            long origLen = new FileInfo(orig).Length;
            var captured = File.GetCreationTime(orig);
            if (File.Exists(cur) && !SameBytes(cur, orig, origLen))
                list.Add(new StrayOverride(asset, ver, cur, orig, foreign, false, origLen, captured));
            else if (File.Exists(foreign))
                list.Add(new StrayOverride(asset, ver, cur, orig, foreign, true, origLen, captured));
        }
        return list.OrderBy(x => x.Asset, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>先比长度再算哈希：一屏几百张时长度就能判掉绝大多数，别为省一次比较而多算几百次 SHA。</summary>
    private static bool SameBytes(string a, string b, long knownLenOfB)
    {
        try
        {
            var fi = new FileInfo(a);
            return fi.Length == knownLenOfB && Sha256(a) == Sha256(b);
        }
        catch { return false; }
    }

    public static List<ContentOverrideRecord> Records(ConfigService cfg)
        => cfg.Current.ContentOverrides.Values
            .OrderBy(r => r.PackName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Asset, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>界面用的一行 = 一个包（一个包可能盖了几十张图，但开关是整包的）。
    /// 分组只看 Slot，不看 PackName —— 两个不同的包重名是常事，重名会把两包并成一行，
    /// 那一行的开关就变成"同时操作两个没关系的包"。
    /// <paramref name="VanillaCount"/> = 账上开着、盘上却已经是原版的那几张（被 Steam 验证
    /// 或游戏更新还原掉了），与 <paramref name="DriftedCount"/>（外来字节顶掉/文件没了）分两报。</summary>
    public sealed record PackGroup(string Slot, string PackName, int? NexusModId,
        List<ContentOverrideRecord> Items, bool AnyOn, int DriftedCount, int LocaleRiskCount,
        int VanillaCount, List<ContentOverrideRecord> ImageItems)
    {
        /// <summary>显示名与封面取自记录（由界面从 N 网补齐后写回），没补之前就是包目录名那种丑名字。</summary>
        public string PackTitle => Items.Count > 0 ? Items[0].PackTitle : "";
        public string PackCover => Items.Count > 0 ? Items[0].PackCover : "";
        /// <summary>这个包没装上的文件数与头几个原因（安装当时算好留在账上，见 ContentOverrideRecord）。</summary>
        public int Skipped => Items.Count > 0 ? Items[0].SkippedCount : 0;
        public string SkippedNote => Items.Count > 0 ? Items[0].SkippedNote : "";
        /// <summary>装上了但尺寸与本体不同的条目（不是失败，只是表情帧数与原版不同）。</summary>
        public int Resized => Items.Count > 0 ? Items[0].ResizedCount : 0;
        public string ResizedNote => Items.Count > 0 ? Items[0].ResizedNote : "";
        /// <summary>N 网分类名（素材包页按它分组）；空 = 还没补到或这包没有页号。</summary>
        public string Category => Items.Count > 0 ? Items[0].CategoryName : "";
        /// <summary>这个包是按哪个版本编的（界面按它分列）。一个包里出现混合版本（部分行还没按
        /// 当前本体重编）时留空 ⇒ 归进「版本待确认」，不替它挑一个。</summary>
        public string StampedVersion { get; } = Items.Select(r => r.Version)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
            ? Items.Select(r => r.Version).FirstOrDefault() ?? "" : "";
    }

    /// <summary>按包汇总账本。<see cref="PackGroup.Items"/> 是这个包的<b>全部账</b>，
    /// <c>ImageItems</c> 是界面上<b>列得出来的行</c>（只留图像，同一张图的语言副本并成一条）。
    /// <para>为什么必须分成两份：整包开关与删除都按 slot 重读账本来覆盖全部文件
    ///（<see cref="SetPackEnabled"/> / <see cref="DeletePack"/>）。把文本表和语言副本从界面上
    /// 藏起来，绝不能顺手把它们也从"关掉这个包"的动作里拿掉 —— 那就变成界面没这个包、
    /// 本体里还躺着一堆没人认领的覆盖。</para></summary>
    public static List<PackGroup> Groups(string gamePath, ConfigService cfg)
        => cfg.Current.ContentOverrides.Values
            .GroupBy(r => r.Slot, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var items = g.OrderBy(r => r.Asset, StringComparer.OrdinalIgnoreCase).ToList();
                return new PackGroup(
                    g.Key,
                    g.First().PackName,
                    g.First().NexusModId,
                    items,
                    g.Any(r => r.Enabled),
                    g.Count(r => Drifted(gamePath, r)),
                    g.Count(r => LocaleSiblings(gamePath, r.Asset).Count > 0),
                    g.Count(r => StateOf(gamePath, r) == OverrideState.AlreadyVanilla),
                    ImageRows(gamePath, items));
            })
            .OrderBy(x => x.PackName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>整包开或关。关 = 这个包盖过的每张图都退回原版；开 = 每张图重新盖回（含修复漂移）。
    /// <para>账上记的版本不是当前本体那一版时，打开走的是【重编】而不是回放：
    /// 副本里那份 xnb 是按上一个版本的行距/帧数编的，直接盖进新版本本体不会报错，
    /// 只会画错 —— 所以先拿原包备份对当前本体重跑一遍判定（一次解析覆盖整包，
    /// 逐行重编会把同一个包解 101 遍），重编完的行由那条路直接写盘并置为启用。</para></summary>
    public static string? SetPackEnabled(ConfigService cfg, string gamePath, string slot, bool enabled)
    {
        var mine = cfg.Current.ContentOverrides.Values
            .Where(r => r.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Asset, StringComparer.OrdinalIgnoreCase).ToList();
        var stale = enabled ? StaleRows(gamePath, mine) : new List<ContentOverrideRecord>();
        string? err = null;
        if (stale.Count > 0)
            err = ForVersion(RebuildForCurrentVersion(cfg, gamePath, slot,
                stale.Select(r => r.Asset).ToHashSet(StringComparer.OrdinalIgnoreCase))) ?? err;
        foreach (var r in mine)
        {
            if (stale.Contains(r)) continue;          // 重编那条路已经把它写完并置为启用了
            if (enabled || r.Enabled)                 // 关的时候别去动本来就关着的行（见 SetEnabled 那条理由）
            {
                var one = SetEnabled(cfg, gamePath, KeyOf(r.Asset, r.Slot), enabled);
                if (one is not null) err = one;
            }
        }
        return err;
    }

    /// <summary>账上版本与当前本体不一致的那些行。版本空白（老账）算一致 —— 猜不出属于哪版就不要为它重编。</summary>
    private static List<ContentOverrideRecord> StaleRows(string gamePath, List<ContentOverrideRecord> rows)
    {
        var current = OriginalVersionSegment(gamePath);
        return rows.Where(r => r.Version.Length > 0
                               && !r.Version.Equals(current, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>把 <paramref name="assets"/> 这些行按<b>当前</b>本体重编并盖上，返回要报给用户的话（null = 全部成功）。
    /// 原包备份没了 / 当前版本一张都判不上 / 那几张还盖着我们的图，都是拦得下来的诚实失败：
    /// 绝不退回"直接回放旧字节"，那是把上一个版本编出来的表画进当前本体。
    /// <para>这里<b>不加</b>"该包需按当前游戏版本重新编码"那句帽子 —— 这个函数也被【副本丢了】
    /// 那条路调用（见 <see cref="SetEnabled"/>），那条跟版本一点关系都没有，套上这句就是撒谎。
    /// 只有真是版本变了的调用方才配说这话。</para></summary>
    private static string? RebuildForCurrentVersion(ConfigService cfg, string gamePath, string slot,
        HashSet<string> assets)
    {
        var rj = Rejudge(cfg, gamePath, slot, true, assets);
        if (rj.Error is not null) return rj.Error;
        var left = cfg.Current.ContentOverrides.Values
            .Count(r => r.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase)
                        && assets.Contains(r.Asset) && !r.Enabled);
        if (left > 0)
            return LocService.Tf("当前版本有 {0} 个文件无法安装，已保持原版", left);
        return null;
    }

    /// <summary>"按当前版本重编"这句帽子只有【真是版本变了】的调用方才配戴。</summary>
    private static string? ForVersion(string? err)
        => err is null ? null : LocService.Tf("该包需按当前游戏版本重新编码，但{0}", err);

    // ───────────────────── 安装 ─────────────────────

    /// <summary>只解析、不写盘：包里每个文件落到哪张原版图、要写成什么字节。
    /// 「这包是不是给游戏本体的」这个判断与下面真正落盘用的是【同一份】结果 ——
    /// 另起一个分类器迟早会和执行分叉，到那时候要么该装的没装，要么装了的不该装。</summary>
    public static PlanResult Prepare(string gamePath, string srcDir)
        => Directory.Exists(ContentRoot(gamePath)) ? Plan(gamePath, srcDir) : new PlanResult();

    /// <summary>把 Prepare 的结果落盘。返回 null = 成功；返回文案 = 一个字节都没写。
    /// <paramref name="srcDir"/> = 解包出来的原包目录：装完原样备份到游戏目录的 asset-packs 下
    /// （打开文件夹看到的就是当初下载的那个 mod），传 null 则跳过备份。</summary>
    public static string? Commit(string gamePath, PlanResult plan, string packName, int? nexusModId,
        ConfigService cfg, out string? modName, string? srcDir = null)
    {
        modName = packName;
        var slot = SlotOf(packName, nexusModId);
        // 这一趟写盘属于哪一个版本 —— 原版层与记录上的版本戳必须出自同一次解析，
        // 不然中途版本变了就会出现「账说自己在 1.6.15，原版却存进了 1.6.16 那层」。
        var version = OriginalVersionSegment(gamePath);
        MigrateLegacyStore(gamePath);
        if (!Directory.Exists(ContentRoot(gamePath)))
            return LocService.Tr("游戏目录中没有 Content 文件夹，请先确认游戏本体已安装");

        if (plan.Items.Count == 0)
            return plan.Rejects.Count > 0
                ? LocService.Tr("这些文件没有一个能与游戏本体的图像对应：") + string.Join("；", plan.Rejects.Take(6))
                : LocService.Tr("包内未找到 .xnb 或 .png 图像文件");

        // ① 先把原版全部收进仓库。这一步失败游戏本体一个字节没动。
        var freshlyStored = new List<string>();
        try
        {
            foreach (var asset in plan.Assets)
            {
                var dst = AssetFullPath(gamePath, asset);
                if (!File.Exists(dst) || File.Exists(OriginalPath(gamePath, asset))) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(OriginalPath(gamePath, asset))!);
                File.Copy(dst, OriginalPath(gamePath, asset), overwrite: true);
                freshlyStored.Add(asset);
            }
        }
        catch (Exception ex)
        {
            foreach (var a in freshlyStored) { try { File.Delete(OriginalPath(gamePath, a)); } catch { } }
            AppLog.Warn("ContentOverride", "[纯净版] 存不下原版：" + ex.Message);
            return LocService.Tr("无法保存原版文件，操作已中止，游戏本体未作任何修改：") + ex.Message;
        }

        // ② 写 Content。中途失败就整单退回原版 —— 半截覆盖玩家看到的是"立绘一半新一半旧"。
        var written = new List<string>();
        try
        {
            foreach (var it in plan.Items)
            {
                var dst = AssetFullPath(gamePath, it.Asset);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.WriteAllBytes(dst, it.Bytes);
                written.Add(it.Asset);
            }
        }
        catch (Exception ex)
        {
            foreach (var a in written)
            {
                try
                {
                    if (File.Exists(OriginalPath(gamePath, a)))
                        File.Copy(OriginalPath(gamePath, a), AssetFullPath(gamePath, a), overwrite: true);
                    else File.Delete(AssetFullPath(gamePath, a));
                }
                catch (Exception backEx) { AppLog.Warn("ContentOverride", backEx.Message); }
            }
            AppLog.Warn("ContentOverride", "[纯净版] 写入中断，已退回原版：" + ex.Message);
            return LocService.Tr("写入中断，已全部还原为原版：") + ex.Message;
        }

        // ②.4 从盘上认回来的账，slot 只能用包名（那时没有页号）。他现在重新下载一次
        //      ⇒ 这次带页号，slot 变成 "N<页号>"。旧账与旧备份目录必须一起让位，否则：
        //      界面上同一个包出现两张卡（一张开一张关），磁盘上多出一份「包名-2」的备份目录
        //      （PreserveOriginalPack 见同名目录的主人不是这个 slot，就会往后缀上躲）。
        var nameSlot = Sanitize(packName);
        if (!slot.Equals(nameSlot, StringComparison.OrdinalIgnoreCase)
            && cfg.Current.ContentOverrides.Values.Any(v =>
                v.Slot.Equals(nameSlot, StringComparison.OrdinalIgnoreCase)
                && v.PackName.Equals(packName, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var kv in cfg.Current.ContentOverrides
                         .Where(kv => kv.Value.Slot.Equals(nameSlot, StringComparison.OrdinalIgnoreCase))
                         .Select(kv => kv.Key).ToList())
                cfg.Current.ContentOverrides.Remove(kv);
            DropPackBackup(gamePath, cfg, nameSlot, nameSlot);
            AppLog.Warn("ContentOverride",
                $"[重新绑定页号] 「{packName}」的无页号旧账已让位（slot {nameSlot} → {slot}）");
        }

        // ②.5 原包备份：盘上已经对了，再把解包目录原样收进 asset-packs。
        // 放在动账之前 —— 账上的 PackDir 要指着它；备份失败（""）不挡安装。
        var packDir = srcDir is not null && Directory.Exists(srcDir)
            ? PreserveOriginalPack(gamePath, srcDir, packName, slot, cfg)
            : "";

        // ③ 盘已经对了，才动账。账本只在这里被改，所以①②的失败路径不需要任何回滚账本的代码。
        foreach (var it in plan.Items)
        {
            var key = KeyOf(it.Asset, slot);
            // 同一个资产上原先在位的另一份：文件已经被我们盖掉了，账转成"关着"，
            // 副本留着，切回去就能重新生效（一个文件只装得下一张表）。
            foreach (var other in cfg.Current.ContentOverrides.Values.Where(o =>
                         o.Enabled && o.Asset.Equals(it.Asset, StringComparison.OrdinalIgnoreCase)
                         && !KeyOf(o.Asset, o.Slot).Equals(key, StringComparison.OrdinalIgnoreCase)).ToList())
                other.Enabled = false;
            var storeId = StoreCopy(gamePath, it.Bytes, it.Asset);
            if (cfg.Current.ContentOverrides.TryGetValue(key, out var prev) && prev.StoreId.Length > 0
                && prev.StoreId != storeId) DropStore(gamePath, prev.StoreId);
            cfg.Current.ContentOverrides[key] = new ContentOverrideRecord
            {
                Asset = it.Asset, Slot = slot, PackName = packName, NexusModId = nexusModId,
                OriginalSha256 = File.Exists(OriginalPath(gamePath, it.Asset)) ? Sha256(OriginalPath(gamePath, it.Asset)) : "",
                AppliedSha256 = Sha256Of(it.Bytes), StoreId = storeId, Enabled = true,
                Encoded = it.Encoded, PackTitle = packName, PackDir = packDir, Version = version,
                // 装成功 ≠ 全装上了。没装上的那几个数与名字要留在账上，界面上才说得出"为什么
                // 那几个角色没变"；只写进日志的话，等人来问的时候日志早就滚走了。
                SkippedCount = plan.Rejects.Count,
                SkippedNote = string.Join("\n", plan.Rejects.Take(8)),
                ResizedCount = plan.Resized.Count,
                ResizedNote = string.Join("\n", plan.Resized.Take(8)),
                // 同一张目标的多个配色：默认那版记在 VariantLabel，其余留在 Variants 里给人换。
                Variants = plan.VariantLabels.TryGetValue(it.Asset, out var vs) ? vs : new(),
                VariantLabel = it.Label,
                VariantFacts = plan.VariantItems.TryGetValue(it.Asset, out var vi)
                    ? vi.Select(v => new ContentOverrideVariant(v.Label, v.Note)).ToList() : new(),
            };
        }
        cfg.Save(cfg.Current);
        AppLog.Warn("ContentOverride",
            $"[纯净版] {packName}：写进 Content {plan.Items.Count} 张，丢弃 {plan.Rejects.Count} 个文件"
            + (plan.Resized.Count > 0 ? $"，其中 {plan.Resized.Count} 张尺寸与本体不同（已照装）" : ""));
        return null;
    }

    /// <summary>本次进程里已经试过的原包目录（键 = 游戏目录|目录名）。
    /// 认不回来的包（真漂移）每次都重算一遍没有意义：解析一个 91 张图的包是秒级，
    /// 而内容包页的 <c>Reload</c> 会被按钮反复调用。</summary>
    private static readonly HashSet<string> _rebuildTried = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>盘上有没有"账上完全没记着"的原包目录 —— <see cref="RebuildFromDisk"/> 的廉价前置。
    /// 只做目录名与账本 <c>PackDir</c>/<c>PackName</c> 的集合比较，一个文件都不解析；
    /// 正常情况（账实一致）这里直接返回 false，页面按原路走。</summary>
    public static bool NeedsRebuild(ConfigService cfg, string gamePath)
    {
        var root = PacksRoot(gamePath);
        if (!Directory.Exists(root)) return false;
        var accounted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in cfg.Current.ContentOverrides.Values)
        {
            if (r.PackDir.Length > 0) accounted.Add(r.PackDir);
            if (r.PackName.Length > 0) accounted.Add(r.PackName);
        }
        try
        {
            foreach (var d in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(d);
                if (name.StartsWith('.')) continue;
                if (accounted.Contains(name)) continue;
                if (_rebuildTried.Contains(gamePath + "|" + name)) continue;
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>下载缓存里【zip 条目清单】→ 页号（zip 名就写着 <c>nxm-&lt;页号&gt;-&lt;文件号&gt;.zip</c>）。
    /// 同一份清单对应多个不同页号时那个页号也留着（调用方看到"不唯一"就不认）。</summary>
    private static Dictionary<string, List<int>> CacheZipPages()
    {
        var map = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        try
        {
            var dir = StoragePaths.DownloadsDir;
            if (!Directory.Exists(dir)) return map;
            foreach (var z in Directory.EnumerateFiles(dir, "*.zip"))
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    Path.GetFileName(z), @"^(?:nxm|direct|dep)-(\d+)-");
                if (!m.Success || !int.TryParse(m.Groups[1].Value, out var page) || page <= 0) continue;
                if (ZipEntrySets(z) is not { } variants) continue;
                foreach (var sig in variants)
                {
                    if (!map.TryGetValue(sig, out var list)) map[sig] = list = new List<int>();
                    if (!list.Contains(page)) list.Add(page);
                }
            }
        }
        catch (Exception ex) { AppLog.Warn("ContentOverride", "[页号回填] 扫下载缓存失败：" + ex.Message); }
        return map;
    }

    /// <summary>一个压缩包的条目清单签名，两种写法都给：压缩包带公共根目录时，
    /// 原包备份里那一层可能被剥掉也可能保留（取决于当初解包到哪一层）。
    /// 读不出清单（坏档、空包、加密 rar）⇒ null，调用方跳过，绝不猜。</summary>
    private static List<string>? ZipEntrySets(string zipPath)
    {
        try
        {
            if (EntryNames(zipPath) is not { } entries) return null;
            var names = entries
                .Where(p => !p.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase)
                            && !Path.GetFileName(p).Equals(".DS_Store", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.TrimStart('/').ToLowerInvariant())
                .Where(p => p.Length > 0)
                .Distinct().ToList();
            if (names.Count == 0) return null;
            var raw = string.Join("\n", names.OrderBy(s => s, StringComparer.Ordinal));
            var first = names[0].Split('/')[0];
            var outp = new List<string> { raw };
            if (names.All(n => n.Contains('/') && n.Split('/')[0] == first))
                outp.Add(string.Join("\n", names.Select(n => n[(first.Length + 1)..])
                    .OrderBy(s => s, StringComparer.Ordinal)));
            return outp;
        }
        catch { return null; }
    }

    /// <summary>取压缩包条目名清单。<b>按文件头认格式，不信扩展名</b> —— 安装器把所有落盘名一律写成
    /// <c>.zip</c>（实机 2026-10-08：缓存里 193 个"zip"有 14 个实为 RAR/7z）。
    /// <para>zip 的条目名按 GBK 解，与 <c>ModService.ExtractToDirectory</c> 同一套：中文作者压的包大量用
    /// GBK 存条目名且不置 UTF-8 标志位，用默认 UTF-8 读会把「[xnb]三色-Tricolor」读成一串乱码 ⇒
    /// 与原包目录的清单【看起来】不相等 ⇒ 页号白白认不回来。</para>
    /// <para>rar/7z 走 SharpCompress，与安装解压用的是同一套。<b>安装能吃的格式，认页号时也必须能吃</b>：
    /// 否则从 RAR 装的内容包永远接不回详情页 —— 实机同一轮里剩下"找不到"的 4 个包
    /// （Better Artifact Digspots / Galarian Ponyta / Starry Sky / Yuriyuri's Hairstyles）
    /// 清单逐条躺在 nxm-774、nxm-4618、nxm-1310、nxm-2393 这四个 RAR 里。</para></summary>
    private static List<string>? EntryNames(string zipPath)
    {
        switch (ModService.DetectArchiveFormat(zipPath))
        {
            case "zip":
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                using var z = System.IO.Compression.ZipFile.Open(zipPath,
                    System.IO.Compression.ZipArchiveMode.Read, System.Text.Encoding.GetEncoding(936));
                return z.Entries
                    .Where(e => e.Length > 0 || !e.FullName.EndsWith('/'))
                    .Select(e => e.FullName.Replace('\\', '/')).ToList();
            }
            case "rar":
            {
                using var a = SharpCompress.Archives.Rar.RarArchive.OpenArchive(zipPath);
                return a.Entries.Where(e => !e.IsDirectory)
                    .Select(e => (e.Key ?? "").Replace('\\', '/')).ToList();
            }
            case "7z":
            {
                using var a = SharpCompress.Archives.SevenZip.SevenZipArchive.OpenArchive(zipPath);
                return a.Entries.Where(e => !e.IsDirectory)
                    .Select(e => (e.Key ?? "").Replace('\\', '/')).ToList();
            }
            default: return null;   // 裸 xnb / 裸 png / html 错误页 / 坏档：没有"清单"可谈
        }
    }

    /// <summary>原包目录的文件清单签名（与 <see cref="ZipEntrySets"/> 同一套规范化）。</summary>
    private static string PackDirSignature(string packDir)
        => string.Join("\n", Directory.EnumerateFiles(packDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(packDir, f).Replace('\\', '/').ToLowerInvariant())
            .Where(p => !p.StartsWith("__macosx/") && Path.GetFileName(p) != ".ds_store")
            .OrderBy(s => s, StringComparer.Ordinal));

    /// <summary>盘上的页号回填：原包目录的文件清单与缓存里某个 zip <b>全等</b>，且该清单只指向
    /// <b>一个</b>页号，才认。实测 2026-10-08：12 个从盘上认回来的包里 5 个这样找回了页号。
    /// <para>为什么判据必须这么窄：松一档（"包里每个文件都能在某个压缩包里找到"）时，
    /// 只有一个文件的包会同时命中 3 个页号（实测 Yuriyuri's Hairstyles → 20581/1083/…），
    /// 而写错页号比没页号更糟 —— 卡片上「访问 mod 详情」会把人领到别人的 mod 页，
    /// 封面与分类也会跟着错到别人身上。</para></summary>
    private static int? PageIdForPack(Dictionary<string, List<int>> zipPages, string packDir)
    {
        try
        {
            if (zipPages.TryGetValue(PackDirSignature(packDir), out var pages) && pages.Count == 1)
                return pages[0];
        }
        catch { }
        return null;
    }

    /// <summary>给账上<b>没有页号</b>的记录补页号 —— 判据与 <see cref="PageIdForPack"/> 完全同源
    /// （下载缓存里某个 zip 的条目清单与原包目录<b>全等</b>，且该清单只指向<b>一个</b>页号）。
    ///
    /// <para>为什么单独要这一步：从盘上认回来的账只有包名，而详情页、封面、分类全都挂在页号上
    /// （<c>EnsureMetaAsync</c> 只给带页号的记录补元数据）。用户看到的就是"包和 N 网分离了"：
    /// 卡片是字母块、12 个包全挤在「未分类」、点「访问 mod 详情」只有一句"并非从 Nexus 安装"。</para>
    ///
    /// <para>补上页号的同时把 slot 从"包名"改写成"N&lt;页号&gt;"，与正常安装的记录同一个形状 ——
    /// 不改的话他以后重新下载这个包，会按新 slot 再写一套账，界面上同一个包两张卡。</para></summary>
    public static int BindPageIds(ConfigService cfg, string gamePath)
    {
        var slots = cfg.Current.ContentOverrides.Values
            .Where(v => v.NexusModId is null && v.PackDir.Length > 0)
            .Select(v => v.Slot).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (slots.Count == 0) return 0;

        var zipPages = CacheZipPages();
        if (zipPages.Count == 0) return 0;
        int bound = 0;
        foreach (var slot in slots)
        {
            var rows = cfg.Current.ContentOverrides.Values
                .Where(v => v.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase)).ToList();
            if (rows.Count == 0) continue;
            var dir = PackOriginalDir(gamePath, rows[0].PackDir);
            if (!Directory.Exists(dir)) continue;
            if (PageIdForPack(zipPages, dir) is not int page) continue;
            RekeySlot(cfg, slot, SlotOf(rows[0].PackName, page), page);
            bound++;
        }
        if (bound > 0)
        {
            cfg.Save(cfg.Current);
            AppLog.Warn("ContentOverride", $"[页号回填] {bound} 个包按下载缓存认回页号，详情页/封面/分类已可回填");
        }
        return bound;
    }

    /// <summary>把一个包的所有记录从旧 slot 搬到新 slot，并顺手清掉封面与分类
    /// （那两样是按页号去 N 网取的，换页号后必须重取，不能留着旧的）。</summary>
    private static void RekeySlot(ConfigService cfg, string oldSlot, string newSlot, int page)
    {
        if (oldSlot.Equals(newSlot, StringComparison.OrdinalIgnoreCase)) return;
        var moving = cfg.Current.ContentOverrides
            .Where(kv => kv.Value.Slot.Equals(oldSlot, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var kv in moving) cfg.Current.ContentOverrides.Remove(kv.Key);
        foreach (var kv in moving)
        {
            kv.Value.Slot = newSlot;
            kv.Value.NexusModId = page;
            kv.Value.PackCover = "";
            kv.Value.CategoryName = "";
            cfg.Current.ContentOverrides[KeyOf(kv.Value.Asset, newSlot)] = kv.Value;
        }
    }

    /// <summary>盘上有、账上没有的包被认回来的结果。</summary>
    /// <param name="Packs">认回来的包数。</param>
    /// <param name="Records">写回（或试算出）的资产条数。</param>
    /// <param name="Applied">判为【正在生效】的条数（现值 == 我们那份）。</param>
    /// <param name="Drifted">现值既不是我们那份也不是原版 ⇒ 一条都没认的资产数。</param>
    /// <param name="Already">账上本来就有记录、因此没动的包数。</param>
    /// <param name="Unresolved">一个目标都没认回来的包，带原因。</param>
    public sealed record RebuildReport(int Packs, int Records, int Applied, int Drifted, int Already,
        List<string> Unresolved);

    /// <summary>以 <c>asset-packs\&lt;目录&gt;</c> 为起点，把丢掉的账认回来。
    ///
    /// <para>为什么需要它：全仓原来<b>没有一条从盘到账的路</b>。页面、开关、还原、
    /// <see cref="AuditOrphans"/> 的起点都是 <see cref="JuniGridConfig.ContentOverrides"/>，
    /// 盘只被用作"这条账的包还活着吗"的证据（<c>AuditOrphans</c> 里那句
    /// <c>foreach (var d in EnumerateDirectories(PacksRoot))</c> 只往一个集合里塞名字）。
    /// 账一旦为空，那 13 个原包目录就再也没有第二个人看它们一眼 —— 而游戏本体里
    /// 147 个文件仍然被它们盖着（2026-10-08 实机：配置被一个模型里没有这个键的旧安装副本
    /// 整份 <c>Save</c> 覆盖，账清了、盘没动）。</para>
    ///
    /// <para><b>三方对得上才认</b>，每条资产单独判，判不动的就跳过（绝不猜）：
    /// ① 拿 <see cref="Prepare"/> 对原包目录重跑一遍解析，算出它应当替换哪些资产、字节是什么；
    /// ② 现值 == 我们算出来的那份 ⇒ 生效中；现值 == <c>content-originals</c> 那一层里的原版 ⇒ 关着；
    /// 现值是<b>同包的另一个配色</b>（用户换过色）或<b>别的包那份副本</b> ⇒ 也算关着，
    /// 这两种都不是漂移，判成漂移会让整包回不来；
    /// ③ 副本按【字节】在 <c>content-derived</c> 里找现成的（<see cref="StoreCopy"/> 用的是随机 id，
    /// 不认字节的活就会把盘上那批变成孤儿）。
    /// 三张都不是 ⇒ 这条不写，算进 <see cref="RebuildReport.Drifted"/>。</para>
    ///
    /// <para>⚠ <b>绝不从 Content 抄原版</b>：包还盖在盘上时抄到的就是【我们自己那张】，
    /// 之后还原会把我们的图当原版退回去（<see cref="Rejudge"/> 里同一条纪律）。
    /// 原版只从 <c>content-originals/&lt;版本&gt;/</c> 读，读不到就按"我们新增的文件"处理。</para>
    ///
    /// <para><paramref name="dryRun"/> 只算不写（连副本都不另落）—— 先看表再决定，用于实机核对。</para></summary>
    public static RebuildReport RebuildFromDisk(ConfigService cfg, string gamePath, bool dryRun)
    {
        var root = PacksRoot(gamePath);
        if (!Directory.Exists(root)) return new RebuildReport(0, 0, 0, 0, 0, new());

        // 账上记着的包一律不动：这条路只补"账上完全没有、盘上却躺着原包"的那一批。
        var accounted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in cfg.Current.ContentOverrides.Values)
        {
            if (r.PackDir.Length > 0) accounted.Add(r.PackDir);
            if (r.PackName.Length > 0) accounted.Add(r.PackName);
        }

        string[] dirs;
        try { dirs = Directory.GetDirectories(root); }
        catch { return new RebuildReport(0, 0, 0, 0, accounted.Count, new()); }

        var stores = StoreIndexByBytes(gamePath);
        var layers = OriginalLayers(gamePath);
        var zipPages = CacheZipPages();          // 页号只可能从下载缓存的 zip 名里认回来
        var pending = new List<(string Key, ContentOverrideRecord Rec)>();
        var unresolved = new List<string>();
        int packs = 0, applied = 0, drifted = 0, already = 0;

        foreach (var d in dirs)
        {
            var name = Path.GetFileName(d);
            if (name.StartsWith('.')) continue;                 // .store 这类内部层不是包
            if (accounted.Contains(name)) { already++; continue; }
            _rebuildTried.Add(gamePath + "|" + name);           // 本进程只试一次（认不回来也一样）

            PlanResult plan;
            try { plan = Prepare(gamePath, d); }
            catch (Exception ex) { unresolved.Add($"{name}：解析失败 {ex.Message}"); continue; }
            if (plan.Items.Count == 0)
            {
                unresolved.Add($"{name}：包内没有一个能与本体图像对应的文件");
                continue;
            }

            var page = PageIdForPack(zipPages, d);
            var slot = SlotOf(name, page);
            var wrote = 0;
            foreach (var it in plan.Items)
            {
                var live = AssetFullPath(gamePath, it.Asset);
                var cur = File.Exists(live) ? Sha256(live) : "";
                var mine = Sha256Of(it.Bytes);
                var (origHash, origVer) = ReadOriginal(gamePath, it.Asset, layers);
                var storeId = stores.TryGetValue((it.Asset.ToLowerInvariant(), mine), out var sid) ? sid : "";
                var inPlace = it;                       // 在位的那一版：默认是它，换过配色就不是

                bool on;
                if (cur.Length > 0 && cur == mine) on = true;
                else if (cur.Length > 0 && plan.VariantItems.TryGetValue(it.Asset, out var cands)
                         && cands.FirstOrDefault(c => Sha256Of(c.Bytes) == cur) is { } hit)
                {
                    // 用户在这个包里换过配色：在位的是【同包的另一版】，不是漂移。
                    // 认它，否则这个包整包被判"对不上"，换过配色的包一个都回不来。
                    inPlace = hit; mine = cur; on = true;
                    storeId = stores.TryGetValue((it.Asset.ToLowerInvariant(), cur), out var s2) ? s2 : "";
                }
                else if (origHash.Length > 0 && cur == origHash) on = false;
                else if (origHash.Length == 0 && cur.Length == 0 && storeId.Length > 0) on = false;
                else if (cur.Length > 0 && stores.ContainsKey((it.Asset.ToLowerInvariant(), cur)))
                    on = false;                         // 现值是【别的包】那份副本 ⇒ 这一个是关着的
                else
                {
                    drifted++;                          // 三张都不是 ⇒ 不认，也别猜
                    continue;
                }

                if (on) applied++;
                // 试算一个字节都不许写：StoreCopy 会往 content-derived 落新目录
                if (on && !dryRun && storeId.Length == 0) storeId = StoreCopy(gamePath, inPlace.Bytes, inPlace.Asset);
                pending.Add((KeyOf(it.Asset, slot), new ContentOverrideRecord
                {
                    Asset = it.Asset, Slot = slot, PackName = name, PackTitle = name, PackDir = name,
                    NexusModId = page,
                    OriginalSha256 = origHash, AppliedSha256 = mine, StoreId = storeId, Enabled = on,
                    Encoded = inPlace.Encoded,
                    Version = origVer.Length > 0 ? origVer : OriginalVersionSegment(gamePath),
                    SkippedCount = plan.Rejects.Count, SkippedNote = string.Join("\n", plan.Rejects.Take(8)),
                    ResizedCount = plan.Resized.Count, ResizedNote = string.Join("\n", plan.Resized.Take(8)),
                    Variants = plan.VariantLabels.TryGetValue(it.Asset, out var vs) ? vs : new(),
                    VariantLabel = inPlace.Label,
                    VariantFacts = plan.VariantItems.TryGetValue(it.Asset, out var vi)
                        ? vi.Select(v => new ContentOverrideVariant(v.Label, v.Note)).ToList() : new(),
                }));
                wrote++;
            }
            if (wrote > 0) packs++;
            else unresolved.Add($"{name}：{plan.Items.Count} 个目标一条都对不上（现值既不是我们那份也不是原版）");
        }

        if (!dryRun && pending.Count > 0)
        {
            foreach (var (key, rec) in pending) cfg.Current.ContentOverrides[key] = rec;
            cfg.Save(cfg.Current);
        }
        AppLog.Warn("ContentOverride",
            $"[账本重建{(dryRun ? "·试算" : "")}] 认回 {packs} 个包 / {pending.Count} 条（生效中 {applied}），"
            + $"对不上 {drifted} 条，账上已有 {already} 个包未动");
        return new RebuildReport(packs, pending.Count, applied, drifted, already, unresolved);
    }

    /// <summary><c>content-originals</c> 下面实际存在的版本层（没有先后，逐个试）。</summary>
    private static List<string> OriginalLayers(string gamePath)
    {
        var list = new List<string>();
        try
        {
            if (Directory.Exists(OriginalsRoot(gamePath)))
                list.AddRange(Directory.GetDirectories(OriginalsRoot(gamePath)).Select(Path.GetFileName)!);
        }
        catch { }
        return list;
    }

    /// <summary>按留底读某张资产的原版哈希，以及它在<b>哪一版</b>那一层。都没有 ⇒ ("", "")。</summary>
    private static (string Hash, string Version) ReadOriginal(string gamePath, string asset, List<string> layers)
    {
        foreach (var v in layers)
        {
            var p = OriginalPath(gamePath, asset, v);
            if (File.Exists(p)) return (Sha256(p), v);
        }
        return ("", "");
    }

    /// <summary>盘上现成的派生副本，按【资产 + 字节哈希】索引 —— 重建时拿它认回 <c>StoreId</c>，
    /// 而不是再写一份新的（<see cref="StoreCopy"/> 用随机 id，另写就等于把盘上那批变成孤儿）。</summary>
    private static Dictionary<(string Asset, string Hash), string> StoreIndexByBytes(string gamePath)
    {
        var map = new Dictionary<(string, string), string>();
        var root = DerivedRoot(gamePath);
        if (!Directory.Exists(root)) return map;
        foreach (var dir in Directory.GetDirectories(root))
        {
            var id = Path.GetFileName(dir);
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(dir, f).Replace('\\', '/');
                if (!rel.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase)) continue;
                try { map[(rel[..^4].ToLowerInvariant(), Sha256(f))] = id; } catch { }
            }
        }
        return map;
    }

    /// <summary>重判一个已装的包的结果。</summary>
    /// <param name="NewlyInstalled">这次【新装上】的资产数（原先被判不适配，现在能装了）。</param>
    /// <param name="StillRejected">仍然不适配的条数 —— 这些是真不适配，账上照旧留着。</param>
    /// <param name="Repaired">只改了账（分类从"不适配"改成"尺寸不同"），没动盘上的字节。</param>
    public sealed record RejudgeResult(int NewlyInstalled, int StillRejected, int Repaired, string? Error);

    /// <summary>
    /// 用<b>当前的</b>判定逻辑重判一个已装的包，并把能装上的补进去。
    ///
    /// 为什么需要它：界面上那些"不适配"是<b>安装当时</b>算好存在
    /// <see cref="ContentOverrideRecord.SkippedNote"/> 里的历史快照 ——
    /// 判定逻辑改对了，那些字不会自己变。实测用户账上仍挂着 25 条 2026-10-05 之前的误报
    /// （3 个字体 + 4 张尺寸不同 + 6 个 _Beach/_Winter 路由错的），不清掉就一直误导人。
    ///
    /// 走原包备份（<c>asset-packs/&lt;packDir&gt;</c>）重跑 <see cref="Plan"/>，不必重新下载；
    /// 备份不在了就只能如实说"没法重判"，绝不猜。
    ///
    /// <para><paramref name="rewriteForCurrentVersion"/> 是给「换了游戏版本再打开开关」用的：
    /// 默认那条路【不动】账上已有的资产（只补新能装上的），而版本一切换，账上那些行恰恰就是
    /// 要按当前本体<b>重编</b>的 —— 它们记的是上一个版本编出来的字节。开了这个参数才会把
    /// 每一条都重新编码、重新写盘、并把 <see cref="ContentOverrideRecord.Version"/> 换成当前版本。</para>
    ///
    /// <para>纪律与 <see cref="Commit"/> 一致：<b>先写盘、后动账</b>。
    /// 写盘失败 ⇒ 老账原样留着（宁可继续显示旧的误报，也不能显示"已修复"而盘上没动）。</para>
    /// </summary>
    public static RejudgeResult Rejudge(ConfigService cfg, string gamePath, string slot,
        bool rewriteForCurrentVersion = false, HashSet<string>? onlyAssets = null)
    {
        var rows = cfg.Current.ContentOverrides.Values
            .Where(r => r.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase)).ToList();
        if (rows.Count == 0)
            return new RejudgeResult(0, 0, 0, LocService.Tr("该包没有覆盖记录，无法重新检查"));

        var version = OriginalVersionSegment(gamePath);
        // 重编要先抄"当前版本的原版"当底，所以【这次要重算的那几张】里只要有一张盘上还盖着我们
        // 上次贴的那份，抄到的就是自己的图 —— 之后还原会把我们的图当原版退回去。
        // 判据必须逐张看盘，不能问"这个包里有没有别的行开着"：实机 2026-10-09 的包
        // 「No More Bowlegs」6 张里 Abigail 一张开着、另外 5 张盘上早就是原版，旧写法拿那一张
        // 把 5 张全锁死，而且它对那 5 张说的理由（"仍处于应用状态"）是假的。
        // 只在重编这条路算（要哈希文件）；进页那种普通重判不进这里。
        if (rewriteForCurrentVersion)
        {
            var stillOurs = rows
                .Where(r => onlyAssets is null || onlyAssets.Contains(r.Asset, StringComparer.OrdinalIgnoreCase))
                .Where(r => DiskState(gamePath, r) == OverrideState.InPlace)
                .Select(r => r.Asset).ToList();
            if (stillOurs.Count > 0)
                return new RejudgeResult(0, 0, 0, stillOurs.Count <= 3
                    ? LocService.Tf("这几张还盖着我们上次贴进去的图（{0}），先把它们关掉再开。",
                        string.Join("、", stillOurs))
                    : LocService.Tf("有 {0} 张图还盖着我们上次贴进去的那份（{1} 等），先把它们关掉再开。",
                        stillOurs.Count, string.Join("、", stillOurs.Take(3))));
        }

        var any = rows[0];
        var packDir = any.PackDir;
        if (string.IsNullOrEmpty(packDir))
            return new RejudgeResult(0, 0, 0, LocService.Tr("该包未保留原包备份，无法按当前本体重新解析 —— 请重新安装"));
        var srcDir = PackOriginalDir(gamePath, packDir);

        // 账上记的目录名可能已经和盘上对不上了 —— 实测 2026-10-05：账里写着
        // "Starry Sky Interface…-2"，而 asset-packs 下只有不带后缀那一份
        // （PreserveOriginalPack 撞名时会加 -2，之后目录又被清理/重命名过）。
        // 这种情况不该把重判堵死：按包名去掉 -N 后缀再找一次，找到了就用它。
        if (!Directory.Exists(srcDir))
        {
            var trimmed = TrimDupSuffix(packDir);
            if (trimmed is not null)
            {
                var alt = PackOriginalDir(gamePath, trimmed);
                if (Directory.Exists(alt)) { srcDir = alt; packDir = trimmed; }
            }
        }
        if (!Directory.Exists(srcDir))
            return new RejudgeResult(0, 0, 0, LocService.Tr("原包备份已不在磁盘上，无法按当前本体重新解析 —— 请重新安装"));

        // 账上已经生效的那些资产 —— 重判要拿它们跟新计划对照，不能自己跟自己比。
        var onDisk = rows.Select(r => r.Asset).ToHashSet(StringComparer.OrdinalIgnoreCase);
        PlanResult plan;
        try { plan = Prepare(gamePath, srcDir); }
        catch (Exception ex)
        {
            AppLog.Warn("ContentOverride", "[重判] 解析失败：" + ex.Message);
            return new RejudgeResult(0, 0, 0, LocService.Tr("重新检查时解析原包失败：") + ex.Message);
        }
        if (plan.Items.Count == 0)
            return new RejudgeResult(0, plan.Rejects.Count, 0, LocService.Tr("原包当前没有任何文件可以安装，已保持原样"));

        // 玩家换过配色的那些行：重新解析给的默认版永远是【排序第一版】，照它写下去就会把人
        // 选的黑色悄悄换回三色。所以先按账上的 VariantLabel 把这一版从候选里挑回来。
        for (var i = 0; i < plan.Items.Count; i++)
        {
            var it = plan.Items[i];
            if (!plan.VariantItems.TryGetValue(it.Asset, out var cands) || cands.Count < 2) continue;
            if (!cfg.Current.ContentOverrides.TryGetValue(KeyOf(it.Asset, slot), out var old)) continue;
            if (old.VariantLabel.Length == 0) continue;
            var chosen = cands.FirstOrDefault(c => string.Equals(c.Label, old.VariantLabel, StringComparison.OrdinalIgnoreCase));
            if (chosen is not null) plan.Items[i] = chosen;
        }

        // ① 先存原版 —— 与 Commit 同理：我们即将动 Content，得先留退路。
        var fresh = new List<string>();
        try
        {
            foreach (var it in plan.Items)
            {
                var alreadyStored = File.Exists(OriginalPath(gamePath, it.Asset));
                if (!File.Exists(AssetFullPath(gamePath, it.Asset)) || alreadyStored) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(OriginalPath(gamePath, it.Asset))!);
                File.Copy(AssetFullPath(gamePath, it.Asset), OriginalPath(gamePath, it.Asset), overwrite: true);
                fresh.Add(it.Asset);
            }
        }
        catch (Exception ex)
        {
            foreach (var a in fresh) { try { File.Delete(OriginalPath(gamePath, a)); } catch { } }
            AppLog.Warn("ContentOverride", "[重判] 存不下原版：" + ex.Message);
            return new RejudgeResult(0, 0, 0, LocService.Tr("无法保存原版文件，操作已中止，游戏本体未作任何修改：") + ex.Message);
        }

        // ② 写 Content。默认只写"账上还没有的" —— 已经在位的那些不重复写，
        //    免得把用户手动关掉又打开的顺序搅乱（那属于 SetEnabled 的地盘）。
        //    重编那条路相反：账上那些行【就是】要按当前本体重新编一遍的。
        var newly = rewriteForCurrentVersion
            ? (onlyAssets is null ? plan.Items : plan.Items.Where(it => onlyAssets.Contains(it.Asset)).ToList())
            : plan.Items.Where(it => !onDisk.Contains(it.Asset)).ToList();
        var written = new List<string>();
        try
        {
            foreach (var it in newly)
            {
                var dst = AssetFullPath(gamePath, it.Asset);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.WriteAllBytes(dst, it.Bytes);
                written.Add(it.Asset);
            }
        }
        catch (Exception ex)
        {
            foreach (var a in written)
            {
                try
                {
                    if (File.Exists(OriginalPath(gamePath, a)))
                        File.Copy(OriginalPath(gamePath, a), AssetFullPath(gamePath, a), overwrite: true);
                    else File.Delete(AssetFullPath(gamePath, a));
                }
                catch (Exception backEx) { AppLog.Warn("ContentOverride", backEx.Message); }
            }
            AppLog.Warn("ContentOverride", "[重判] 写入中断，已退回原版：" + ex.Message);
            return new RejudgeResult(0, 0, 0, LocService.Tr("写入中断，已全部还原为原版：") + ex.Message);
        }

        // ③ 盘上对了，才动账。
        var repaired = 0;
        foreach (var it in newly)
        {
            var key = KeyOf(it.Asset, slot);
            foreach (var other in cfg.Current.ContentOverrides.Values.Where(o =>
                         o.Enabled && o.Asset.Equals(it.Asset, StringComparison.OrdinalIgnoreCase)
                         && !KeyOf(o.Asset, o.Slot).Equals(key, StringComparison.OrdinalIgnoreCase)).ToList())
                other.Enabled = false;
            var storeId = StoreCopy(gamePath, it.Bytes, it.Asset);
            // 已有行就地改，不 new 一个顶掉：顶掉会把 PackCover / CategoryName 这些
            // 界面懒补回来的字段擦成空，界面上就变成"封面没了 / 包掉回未分类"。
            if (!cfg.Current.ContentOverrides.TryGetValue(key, out var rec))
            {
                rec = new ContentOverrideRecord { Asset = it.Asset, Slot = slot };
                cfg.Current.ContentOverrides[key] = rec;
            }
            else if (rec.StoreId.Length > 0 && rec.StoreId != storeId)
                DropStore(gamePath, rec.StoreId);
            rec.PackName = any.PackName;
            rec.NexusModId = any.NexusModId;
            rec.OriginalSha256 = File.Exists(OriginalPath(gamePath, it.Asset))
                ? Sha256(OriginalPath(gamePath, it.Asset)) : "";
            rec.AppliedSha256 = Sha256Of(it.Bytes);
            rec.StoreId = storeId;
            rec.Enabled = true;
            rec.Encoded = it.Encoded;
            rec.PackTitle = any.PackTitle;
            rec.PackDir = packDir;
            rec.Version = version;
            rec.AppliedAtUtc = DateTime.UtcNow;
            rec.SkippedCount = plan.Rejects.Count;
            rec.SkippedNote = string.Join("\n", plan.Rejects.Take(8));
            rec.ResizedCount = plan.Resized.Count;
            rec.ResizedNote = string.Join("\n", plan.Resized.Take(8));
            // 配色清单跟着这次解析的结果走：原包被人改动过时，界面上不该还列着已经没了的那版。
            rec.Variants = plan.VariantLabels.TryGetValue(it.Asset, out var vl) ? vl : new();
            rec.VariantLabel = it.Label;
            rec.VariantFacts = plan.VariantItems.TryGetValue(it.Asset, out var vi2)
                ? vi2.Select(v => new ContentOverrideVariant(v.Label, v.Note)).ToList() : new();
            repaired++;
        }

        // ④ 已经在位的那些行：盘上的字节没动，但账上的"不适配/尺寸不同"分类要跟着新判定更新。
        //    不更新的话：同一个包里有的行写着"不适配"、有的写着"已运用"，界面上自相矛盾。
        var newSkip = string.Join("\n", plan.Rejects.Take(8));
        var newResized = string.Join("\n", plan.Resized.Take(8));
        var dirChanged = false;
        foreach (var r in rows)
        {
            // 上面按原名回退找到了实际目录 ⇒ 把纠正后的名字写回账。
            // 不写回的话下次重判还会撞同一个错（账指着一个不存在的目录）。
            if (!r.PackDir.Equals(packDir, StringComparison.OrdinalIgnoreCase)) { r.PackDir = packDir; dirChanged = true; }
            // 版本清单也照这次的解析结果刷新：它是【安装当时】的快照，判据一变（比如 2026-10-10
            // 起 PNG 不再算候选）账上就会继续列着包里已经没有的那几版 —— 界面上那些"角色预览 / xnb"
            // 配色 chip 就是这么留下的，点它还会去找一个不存在的候选。
            var newVariants = plan.VariantLabels.TryGetValue(r.Asset, out var vl)
                ? vl : new List<string>();
            var newFacts = plan.VariantItems.TryGetValue(r.Asset, out var vi)
                ? vi.Select(v => new ContentOverrideVariant(v.Label, v.Note)).ToList()
                : new List<ContentOverrideVariant>();
            if (r.SkippedNote == newSkip && r.ResizedNote == newResized
                && r.SkippedCount == plan.Rejects.Count && r.ResizedCount == plan.Resized.Count
                && r.Variants.SequenceEqual(newVariants)) continue;
            r.SkippedCount = plan.Rejects.Count;
            r.SkippedNote = newSkip;
            r.ResizedCount = plan.Resized.Count;
            r.ResizedNote = newResized;
            r.Variants = newVariants;
            r.VariantFacts = newFacts;
            // VariantLabel 故意不动：它记的是"现在贴着的那一份"是哪一版。
            // 那份候选可能已经不在清单里（效果图就是这种）—— 这时候一颗 chip 都不高亮才是实话，
            // 而玩家点清单里剩下的那一版，正好把它换成还在的。
        }

        cfg.Save(cfg.Current);
        AppLog.Warn("ContentOverride",
            $"[重判] {any.PackName}：新装上 {repaired} 张，分类已刷新"
            + (dirChanged ? $"，原包目录名已纠正为 {packDir}" : "")
            + $"，仍不适配 {plan.Rejects.Count} 个");
        return new RejudgeResult(repaired, plan.Rejects.Count, rows.Count, null);
    }

    /// <summary>
    /// 把一条记录换成同包里另一个配色（<see cref="ContentOverrideRecord.Variants"/> 里列的那些）。
    ///
    /// <para>为什么账上不存 N 份字节：一个文件只装得下一张表，"换配色"本质就是**重新盖一次**；
    /// 而候选图本来就在 <see cref="ContentOverrideRecord.PackDir"/> 那份原包备份里，
    /// 重新解析一遍既能按<b>当前</b>本体重编（与换版本重编同一套），也免得每个配色都留一份副本占盘。</para>
    ///
    /// <para>纪律与 <see cref="Commit"/>/<see cref="Rejudge"/> 一致：<b>先写盘、后动账</b>。
    /// 记录是关着的（没在位）⇒ 只挑好新那份、把副本与标签换上，等下次打开就是它，不碰 Content。</para>
    /// </summary>
    public static string? SwitchVariant(ConfigService cfg, string gamePath, string asset, string slot, string label)
    {
        var key = KeyOf(asset, slot);
        if (!cfg.Current.ContentOverrides.TryGetValue(key, out var r)) return LocService.Tr("未找到该记录");
        if (r.Variants.Count < 2) return null;                                  // 单版本包，没得换
        if (string.Equals(r.VariantLabel, label, StringComparison.Ordinal)) return null;   // 已经是这一版

        var srcDir = ResolvePackBackup(gamePath, r);
        if (srcDir is null)
            return string.IsNullOrEmpty(r.PackDir)
                ? LocService.Tr("该包未保留原包备份，换配色需要原包 —— 请重新安装一次")
                : LocService.Tr("原包备份已不在磁盘上，换配色需要原包 —— 请重新安装一次");

        PlanResult plan;
        try { plan = Prepare(gamePath, srcDir); }
        catch (Exception ex)
        {
            AppLog.Warn("ContentOverride", "[换配色] 解析原包失败：" + ex.Message);
            return LocService.Tr("换配色时解析原包失败：") + ex.Message;
        }
        var pick = plan.VariantItems.TryGetValue(asset, out var cands)
            ? cands.FirstOrDefault(c => string.Equals(c.Label, label, StringComparison.OrdinalIgnoreCase))
            : null;
        if (pick is null)
            return LocService.Tf("原包里已找不到配色「{0}」—— 这个包可能被改动过，重新安装一次再换", label);

        // 在位的那条要盖 Content：盘上现在这份必须是我们放的，别人（另一 mod / 手动改）动过就别盲写。
        if (r.Enabled && ForeignOnDisk(gamePath, r))
            return LocService.Tr("该图像当前既非本程序写入也非原版（可能被其他 mod 或手动修改），已停止换配色");

        var dst = AssetFullPath(gamePath, asset);
        if (r.Enabled)
        {
            try { File.WriteAllBytes(dst, pick.Bytes); }
            catch (Exception ex)
            {
                AppLog.Warn("ContentOverride", "[换配色] 写入失败：" + ex.Message);
                return LocService.Tr("写入中断，已全部还原为原版：") + ex.Message;
            }
        }
        var prevStore = r.StoreId;
        r.StoreId = StoreCopy(gamePath, pick.Bytes, asset);
        if (prevStore.Length > 0 && prevStore != r.StoreId) DropStore(gamePath, prevStore);
        r.AppliedSha256 = Sha256Of(pick.Bytes);
        r.Encoded = pick.Encoded;
        r.VariantLabel = pick.Label.Length > 0 ? pick.Label : label;
        if (plan.VariantItems.TryGetValue(asset, out var facts))
            r.VariantFacts = facts.Select(v => new ContentOverrideVariant(v.Label, v.Note)).ToList();
        r.Version = OriginalVersionSegment(gamePath);
        if (r.Enabled) r.AppliedAtUtc = DateTime.UtcNow;
        // 候选清单跟着这次解析的结果刷新（原包被人动过时，界面上不该还列着没了的那版）
        if (plan.VariantLabels.TryGetValue(asset, out var fresh)) r.Variants = fresh;
        cfg.Save(cfg.Current);
        AppLog.Warn("ContentOverride", $"[换配色] {asset} ← {r.VariantLabel}（{(r.Enabled ? "已写入本体" : "包当前关着，下次打开生效")}）");
        return null;
    }

    /// <summary>这个包的这张图，字节实际在哪儿：<b>派生副本 → 原包备份 → 没有</b>。
    /// <para>为什么必须两处都找：从盘上认回来的账（<see cref="RebuildFromDisk"/>）只给"真在位"的那些
    /// 编派生副本，判成"关着"的那几条 <c>StoreId</c> 是空的 —— 可它的图一直好好躺在
    /// <c>asset-packs/&lt;包&gt;/</c> 里。预览只认 <c>StoreId</c> 的话，就会把"盘上明明有"
    /// 报成"该副本文件已不在磁盘上"（实机 2026-10-09：No More Bowlegs 的 Characters/Abigail 就这样
    /// 被谎报了一次，而写入那条路 <see cref="ResolvePackBackup"/> 本来就会回退到原包备份）。</para></summary>
    public static string? PackImageSource(string gamePath, ContentOverrideRecord r)
    {
        if (r.StoreId.Length > 0)
        {
            var copy = StoreFile(gamePath, r.StoreId, r.Asset);
            if (File.Exists(copy)) return copy;
        }
        var dir = ResolvePackBackup(gamePath, r);
        if (dir is null) return null;
        var direct = Path.Combine(dir, r.Asset.Replace('/', Path.DirectorySeparatorChar) + ".xnb");
        if (File.Exists(direct) && BytesMatch(direct, r.AppliedSha256)) return direct;
        // 备份是平铺的（包里那张图就躺在根上，"它盖哪张原版图"是 Prepare 按内容认出来的）⇒ 按同名候选找，
        // 但每一个都要拿账上那份字节验一次：只认文件名的话，一个包里 Portraits/Abigail 与
        // Characters/Abigail 会互相拿错图 —— 拿错图比不画更糟。
        //（实机 2026-10-09：No More Bowlegs 的备份就是 6 个平铺文件，直接拼资产路径必然找不到。）
        var name = Path.GetFileName(r.Asset) + ".xnb";
        try
        {
            foreach (var cand in Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories))
                if (BytesMatch(cand, r.AppliedSha256)) return cand;
        }
        catch { return null; }
        return null;
    }

    /// <summary>字节对得上才算这张是它。账上没写期望值的老行（AppliedSha256 空）放行。</summary>
    private static bool BytesMatch(string file, string wantSha)
        => wantSha.Length == 0
           || string.Equals(Sha256(file), wantSha, StringComparison.OrdinalIgnoreCase);

    /// <summary>账上那份原包备份的实际目录；没了就按去掉 <c>-2</c> 后缀的名字回退找一次。
    /// 找不到返回 null（<see cref="Rejudge"/> 与 <see cref="SwitchVariant"/> 共用这套说法）。</summary>
    private static string? ResolvePackBackup(string gamePath, ContentOverrideRecord r)
    {
        if (string.IsNullOrEmpty(r.PackDir)) return null;
        var srcDir = PackOriginalDir(gamePath, r.PackDir);
        if (Directory.Exists(srcDir)) return srcDir;
        var trimmed = TrimDupSuffix(r.PackDir);
        if (trimmed is not null)
        {
            var alt = PackOriginalDir(gamePath, trimmed);
            if (Directory.Exists(alt)) return alt;
        }
        return null;
    }

    /// <summary>去掉目录名末尾的 <c>-2</c>、<c>-3</c> 这类撞名后缀；没有就返回 null。
    /// 用来在"账上记的目录没了"时按原名回退去找（见 <see cref="Rejudge"/>）。</summary>
    private static string? TrimDupSuffix(string dirName)
    {
        var i = dirName.LastIndexOf('-');
        if (i <= 0 || i == dirName.Length - 1) return null;
        return int.TryParse(dirName.AsSpan(i + 1), out _) && dirName.AsSpan(i + 1).Length <= 3
            ? dirName[..i]
            : null;
    }

    /// <summary>账上所有还挂着"不适配"的包（界面上给"重判并修复"用）。</summary>
    public static List<(string Key, string PackName, int Skipped)> SlotsNeedingRejudge(ConfigService cfg)
        => cfg.Current.ContentOverrides.Values
            .Where(r => r.SkippedCount > 0)
            .GroupBy(r => r.Slot, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Key: g.Key, PackName: g.First().PackName, Skipped: g.Max(r => r.SkippedCount)))
            .OrderByDescending(x => x.Skipped)
            .ToList();

    /// <param name="Label">同一张目标的多个配色里，这一版叫什么（取包内文件夹名）。
    /// 单版本包留空，界面因此不会凭空多一条配色栏。</param>
    /// <param name="Note">这一版自己的说明（尺寸与本体不同时那句）。界面上"按配色分行"要用它，
    /// 不能再从拼好的整串备注里倒推是哪个配色 —— 4 个配色的文件名全都叫 hairstyles.png。</param>
    public sealed record PlanItem(string Asset, byte[] Bytes, bool Encoded, string Source,
        string Label = "", string Note = "");

    /// <summary>包内这张图所在的"版本文件夹"名，剥掉作者标编码方式的前缀（<c>[xnb]三色-Tricolor</c> ⇒ <c>三色-Tricolor</c>）。
    /// 根目录下的文件没有文件夹 ⇒ 空串，调用方回落到文件名。</summary>
    internal static string VariantFolderOf(string rel)
    {
        var dir = Path.GetDirectoryName(rel)?.Replace('\\', '/') ?? "";
        var seg = dir.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        return Regex.Replace(seg, @"^[\[（(]\s*[xX][nN][bB]\s*[\]）)]\s*", "").Trim();
    }

    public sealed class PlanResult
    {
        public List<PlanItem> Items { get; } = new();
        /// <summary>被跳过的文件，带【包内路径】与【推出来的资产】——收尾要拿这两个数认"同包的源图"。
        /// 界面与日志读的是 <see cref="Rejects"/> 那份纯文案表。</summary>
        public List<(string Rel, string Asset, string Msg)> RejectRows { get; } = new();
        /// <summary>包内某个文件 → 它自己那句"尺寸与本体不同"的说明。分组配色时按这个取备注。</summary>
        public Dictionary<string, string> FileNotes { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>跳过原因（逐条人话）。同包源图那类已在 Plan 收尾被摘掉，不算"不可用"。</summary>
        public List<string> Rejects => RejectRows.Select(r => r.Msg).ToList();
        public void AddReject(string rel, string? asset, string msg) => RejectRows.Add((rel, asset ?? "", msg));
        /// <summary>装上了、但尺寸与本体那张不同的条目。游戏按格位切帧，格位不变就
        /// 不会画错人，所以这类一律放行 —— 只是提醒玩家"这角色的表情比原版少几帧"，
        /// 不能再混进 Rejects 里说成"不适配"，那会让整张表被扣上一枚不可用的章。</summary>
        public List<string> Resized { get; } = new();
        /// <summary>一个能用的都没有 ⇒ 调用方不该接管这个包（交回原来的路由）。</summary>
        public bool Installable => Items.Count > 0;
        /// <summary>有多少个文件【认出了本体里的某张原版图】。
        /// 与 Installable 分开计数是为了区分两种"没的装"：
        /// 一种是这包根本不是给本体的（0，交回原路），另一种是给本体但编不成
        ///（&gt;0，必须就地拒绝并点名，不许悄悄退回转换器）。
        /// 高度不同不再计入这一类 —— 它照装，只是进 <see cref="Resized"/>；
        /// 宽度不同仍然算这一类，因为那会横向串格，必须点名拒绝。</summary>
        public int TargetsSeen;
        /// <summary>撞到同一张目标的多个配色（含被选为默认的那一个），键 = 资产。
        /// 只有两版以上才有条目 ⇒ 界面拿它决定要不要出配色栏。</summary>
        public Dictionary<string, List<string>> VariantLabels { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>上面那些标签对应的候选项本体（含默认版）。换配色就是从这张表里取另一版重盖一次，
        /// 所以候选的字节必须留着 —— 只留默认那版的话，"换配色"就没东西可换了。</summary>
        public Dictionary<string, List<PlanItem>> VariantItems { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Assets => Items.Select(i => i.Asset).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>逐文件推目标并算好要写的字节。整个阶段不碰游戏目录。</summary>
    private static PlanResult Plan(string gamePath, string srcDir)
    {
        var res = new PlanResult();
        var stemIndex = BuildStemIndex(ContentRoot(gamePath), out var variantBases);
        // 已装 mod 在 content.json 里声明的资产名：整趟只取一次。逐文件取 = 每个候选重扫一遍
        // Mods（实测 0.18 秒/次），256 张图的包光这一步就是几分钟。
        var modDeclared = ModService.DeclaredAssetNames(gamePath);
        foreach (var f in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(f);
            var isXnb = ext.Equals(".xnb", StringComparison.OrdinalIgnoreCase);
            if (!isXnb && !ext.Equals(".png", StringComparison.OrdinalIgnoreCase)) continue;
            var rel = Path.GetRelativePath(srcDir, f).Replace('\\', '/');
            var name = Path.GetFileName(rel);
            var asset = ResolveTarget(gamePath, rel, f, stemIndex, variantBases, modDeclared, out var why, out var sawVanillaName);
            if (sawVanillaName) res.TargetsSeen++;     // 认得出是本体某张原版图，哪怕这张装不成
            if (asset is null) { res.AddReject(rel, null, why!); continue; }
            var targetPath = AssetFullPath(gamePath, asset);
            if (isXnb)
            {
                byte[] bytes;
                try { bytes = File.ReadAllBytes(f); }
                catch (Exception ex) { AppLog.Warn("ContentOverride", ex.Message); continue; }
                var bad = XnbCodec.Validate(bytes);
                if (bad is not null) { res.AddReject(rel, asset, name + "：" + bad); continue; }

                // 目标本体件解不出纹理【有两种完全不同的原因】，必须分开判，不能一律当字体：
                //  · 字体（SpriteFont）：自描述容器，字号与行距都记在表里，整份替换成立
                //    （CP 的 EditImage 对字体也是整份替换）⇒ 收，且不做尺寸闸门；
                //    画面就是它，素材页还能把字形图集解出来画成卡。
                //  · 文字/数据表（DictionaryReader<String,String>：对话、事件脚本、日程、
                //    字符串表、礼物喜好…）：不是画面，装上去不改变任何像素 ⇒ 【不收】。
                // 旧写法在这里只看"解不解得出纹理"，于是整个第二类被当成字体一路放行 ——
                // 实测 1083 那个 furry 包因此往本体塞了 1236 份文字表，而包里那份是【1.6 之前】
                // 编的：Strings/BigCraftables 比本体少 14 条串、没有 BigChest_Name（大箱子是 1.6
                // 加的）、指环措辞还是 1.5.6 的 "re-roll trinkets"。整份盖上去=把本体较新的
                // 文本表退回旧版，缺键的条目在游戏里显示成空白或原样键名 —— 那才是真的画错。
                var theirs = XnbDecoder.TryLocate(targetPath);
                if (theirs is null)
                {
                    if (!XnbCodec.IsFontAsset(targetPath))
                    {
                        res.AddReject(rel, asset, LocService.Tf("{0}：{1} 在本体里不是图像也不是字体，是文字/数据表，不安装",
                            name, asset));
                        continue;
                    }
                    res.Items.Add(new PlanItem(asset, bytes, false, rel));
                    continue;
                }

                // 目标能解出纹理：这才是"整张换表"那种情况。
                // 高度不等【可以照装】：XNB 自描述，换进去的就是那张表自己的宽高，而游戏按固定
                // 行距从顶上往下切格 —— 源表矮了则下面几格取不到东西（那个表情画不出），高了则
                // 多出的几格游戏根本不会去索引。两种都只是"格数不对"，没有一个源像素换过行。
                //
                // 宽度不等要分两种，一种放行、一种拦（实测取证，别再一刀切）：
                //  · 源 64 宽 + 目标 128 宽立绘 ⇒ 放行，丢掉的第 2 列是【眨眼帧】。
                //    逐像素比对本体四张 128×64 立绘的两列：Grandpa 差异 95/4096、MrQi 56、
                //    Gil 783、Wizard 187，差异区域全部落在眼部（Grandpa x∈[12,51] y∈[23,45]）
                //    ⇒ 第 2 列是眨眼，不是第二套表情。装上去少眨一次眼，脸是对的。
                //    本体里本就有 6 张 64×64 立绘（AnsweringMachine / Bouncer / Dwarf /
                //    Gunther），证明游戏本来就处理得了单列立绘。
                //    —— 以前这里一律拒绝，是把"少眨一次眼"当成了"画错人"，过严。
                //  · 其余宽度不等 ⇒ 必须拦：一行里的列数变了，第 N 格直接落到别人的像素上
                //    ⇒ 横向串格，那不是"少几帧"，是画错人。
                var mine = XnbDecoder.TryLocate(bytes);
                if (mine is not null && mine.Width != theirs.Width
                    && !IsSingleFramePortraitFill(mine.Width, mine.Height, theirs.Width, theirs.Height, asset))
                {
                    res.AddReject(rel, asset, LocService.Tf("{0}：此文件 {1}×{2}，游戏 {3} 为 {4}×{5} —— 宽度不一致，整表替换会导致横向错位",
                        name, mine.Width, mine.Height, asset, theirs.Width, theirs.Height));
                    continue;
                }
                if (mine is not null && mine.Height != theirs.Height)
                    res.Resized.Add(LocService.Tf("{0}：此文件 {1}×{2}，游戏 {3} 为 {4}×{5} —— 按原样整体替换，{6}",
                        name, mine.Width, mine.Height, asset, theirs.Width, theirs.Height,
                        mine.Height < theirs.Height ? "本体多出来的那几格是空的" : "超出本体的那几格游戏取不到"));
                if (mine is not null && IsSingleFramePortraitFill(mine.Width, mine.Height, theirs.Width, theirs.Height, asset))
                    res.Resized.Add(LocService.Tf("{0}：此文件 {1}×{2}，游戏 {3} 为 {4}×{5} —— 按原样整体替换，{6}",
                        name, mine.Width, mine.Height, asset, theirs.Width, theirs.Height,
                        "本体右边那列是眨眼帧，这份只有单帧，角色少眨一次眼"));
                if (res.Resized.Count > 0) res.FileNotes[rel] = res.Resized[^1];   // 这份的尺寸说明归到自己名下
                res.Items.Add(new PlanItem(asset, bytes, false, rel));
            }
            else
            {
                // PNG 不当候选：往本体 Content 里贴图这条路只收游戏认得的 .xnb。
                // 作者只交 PNG 的那一张不是"失败的图"，是【没打算走这条路】—— 实测 Villagers
                // Anime Portrait（N97）：`xnb/` 里 46 张 xnb 才是给本体的，`Character Previews/`
                // 里 45 张 png 是给人看的效果图，而旧写法按【文件夹名排序】取第一版，
                // "Character Previews" 排在 "xnb" 前面 ⇒ 账上 20 条是从效果图重编出来的，
                // 其中 Portraits/Alex 当时就在位 —— 游戏里盖着的确实是效果图（用户 2026-10-10 抓到）。
                // 整包一张 xnb 都没有的那种更早就进不了这条路（ModService.OverrideChannelTakes 的
                // anyXnb 硬前提），这儿只管混装包里那些只有 PNG 的张。
                res.AddReject(rel, asset, LocService.Tf("{0}：只有 PNG，内容包只收 XNB", name));
            }
        }
        // 一个包里多张图撞到同一个资产：以前【整组都不装】（"作者没写清的事我们不能替他编"）。
        // 实测这条把一类正常的包全挡死了 —— Yomi's Golden Princess Hairstyle 就是
        // 同一张 Characters/Farmer/hairstyles 的 4 个配色（三色/可染白/金/黑），各带 png+xnb，
        // 8 个文件一起被拒，玩家一个都用不上。现在按【包内所在文件夹】分版本：
        // 候选只有 .xnb（PNG 在上面那步就不收了），所以"同一文件夹里 png 与 xnb 是同一份画的
        // 两种编码"这件事已经不会发生 —— 分版本只看【装了哪几个文件夹的 xnb】。
        // 不同文件夹才是不同版本，默认取按名排序的第一版，其余记进 VariantLabels，
        // 让玩家在对象卡的版本行里换（SwitchVariant）。
        var byTarget = res.Items.GroupBy(i => i.Asset, StringComparer.OrdinalIgnoreCase).ToList();
        res.Items.Clear();
        foreach (var g in byTarget)
        {
            if (g.Count() == 1) { res.Items.Add(g.First()); continue; }
            var versions = g.GroupBy(i => VariantFolderOf(i.Source), StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => x.OrderBy(y => y.Source, StringComparer.Ordinal).First())
                .ToList();
            if (versions.Count == 1) { res.Items.Add(versions[0]); continue; }
            var labeled = versions.Select(v => v with
            {
                Label = VariantFolderOf(v.Source).Length > 0
                    ? VariantFolderOf(v.Source)
                    : Path.GetFileNameWithoutExtension(v.Source),
                Note = res.FileNotes.GetValueOrDefault(v.Source, "")
            }).ToList();
            res.Items.Add(labeled[0]);
            res.VariantLabels[g.Key] = labeled.Select(v => v.Label).ToList();
            res.VariantItems[g.Key] = labeled;
        }
        // 同一份画的另一种编码（作者常把 PNG 源图与 XNB 并排放：Cursors.xnb + cursors.png）：
        // 装上的那份已经把它代表了，再列一条"不可用/无法识别"就是纯误导 —— 它不是失败，
        // 是没用的第二份编码。只摘"同资产已装上、且文件名只差扩展名"这一种，
        // 其它跳过原因（真不适配、真撞车）照旧逐条点名。
        res.RejectRows.RemoveAll(x => x.Asset.Length > 0 && res.Items.Any(i =>
            i.Asset.Equals(x.Asset, StringComparison.OrdinalIgnoreCase)
            && Path.GetFileNameWithoutExtension(i.Source)
                .Equals(Path.GetFileNameWithoutExtension(x.Rel), StringComparison.OrdinalIgnoreCase)));
        return res;
    }

    // ───────────────────── 开关 / 删除 / 还原 ─────────────────────

    /// <summary>开或关一条。
    /// 「关→关」必须什么都不做：关掉一条已经关着的记录若走还原，会把同一个资产上【在位的那份兄弟】
    /// 当成自己的原版退回游戏目录（覆盖包那条踩过的坑）。
    /// 「开→开」反而要重新盖一次：盘上那份可能已经被 Steam 验证完整性、或游戏更新换回原版了，
    /// 而账还写着"开着" —— 这时候再点一下就是修复入口，不该要求人先关再开。</summary>
    public static string? SetEnabled(ConfigService cfg, string gamePath, string key, bool enabled)
    {
        if (!cfg.Current.ContentOverrides.TryGetValue(key, out var r)) return LocService.Tr("未找到该记录");
        if (!enabled && !r.Enabled) return null;
        if (enabled)
        {
            // 单行开关也要认版本：回放旧字节 = 把上一个版本编的表画进当前本体（见 SetPackEnabled）。
            var one = StaleRows(gamePath, new List<ContentOverrideRecord> { r });
            if (one.Count > 0)
                return ForVersion(RebuildForCurrentVersion(cfg, gamePath, r.Slot,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { r.Asset }));
        }
        try
        {
            if (enabled)
            {
                var copy = StoreFile(gamePath, r.StoreId, r.Asset);
                if (r.StoreId.Length == 0 || !File.Exists(copy))
                    // 从盘上认回来的账只给"真在位"的那几条编副本（见 RebuildFromDisk），所以"副本没了"
                    // 不等于"包没了"——原包备份往往好端端躺着。先按当前本体重编一次再放弃，
                    // 否则用户看到的是"要点启用却被告知必须重装"，而东西就在 asset-packs 里。
                    return RebuildForCurrentVersion(cfg, gamePath, r.Slot,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { r.Asset });
                var dst = AssetFullPath(gamePath, r.Asset);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(copy, dst, overwrite: true);
                foreach (var other in cfg.Current.ContentOverrides.Values.Where(o =>
                             o.Enabled && !ReferenceEquals(o, r)
                             && o.Asset.Equals(r.Asset, StringComparison.OrdinalIgnoreCase)).ToList())
                    other.Enabled = false;
                r.Enabled = true;
            }
            else
            {
                // 盘上那张既不是我们放的、也不是原版 ⇒ 动它之前先停下：盖回原版看起来是"还原成功"，
                // 实际抹掉的是第三方那张，而且账上没有任何地方说过它存在过。
                if (ForeignOnDisk(gamePath, r))
                    return LocService.Tr("该图像当前既非本程序写入也非原版（可能被其他 mod 或手动修改），已停止还原");
                if (!RevertAsset(gamePath, r)) return LocService.Tr("还原失败，游戏本体未作改动，可重试");
                r.Enabled = false;
            }
            cfg.Save(cfg.Current);
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Warn("ContentOverride", ex.Message);
            return LocService.Tr("操作失败：") + ex.Message;
        }
    }

    /// <summary>把一张退回原版；原本没有这张图时删掉我们新增的文件。r 还没落账时传 null。
    /// <paramref name="version"/> 传记录自己那一版 —— 切过版本之后原版躺在旧层里，
    /// 只查当前层会读不到，然后把旧覆盖当成"我们新增的文件"删掉。</summary>
    private static bool RevertAsset(string gamePath, string asset, string? appliedSha = null,
        string? version = null)
    {
        try
        {
            var dst = AssetFullPath(gamePath, asset);
            var orig = OriginalPath(gamePath, asset, version);
            if (File.Exists(orig))
            {
                File.Copy(orig, dst, overwrite: true);
                return true;
            }
            // 没有原版仓库：要么这本来就是我们新增的文件（删），要么动不了（报出来，别装成功）
            if (File.Exists(dst) && appliedSha is not null && Sha256(dst) == appliedSha) File.Delete(dst);
            return !File.Exists(dst) || appliedSha is null;
        }
        catch (Exception ex)
        {
            AppLog.Warn("ContentOverride", ex.Message);
            return false;
        }
    }
    private static bool RevertAsset(string gamePath, ContentOverrideRecord r)
        => RevertAsset(gamePath, r.Asset, r.AppliedSha256, r.Version);

    /// <summary>丢掉一张的原版仓库：只删它自己，再逐层回收空目录。
    /// 老写法是「递归删它的父目录」，而 <c>content-originals/&lt;版本&gt;/Portraits/</c> 里躺着的是
    /// 别的角色的原版 —— 删掉就再也回不去，而账上那些记录还以为自己在位，还原会直接失败。</summary>
    private static void DropOriginal(string gamePath, string asset, string? version = null)
    {
        var orig = OriginalPath(gamePath, asset, version);
        if (!File.Exists(orig)) return;
        File.Delete(orig);
        var stop = OriginalsRoot(gamePath) + Path.DirectorySeparatorChar;
        for (var dir = Path.GetDirectoryName(orig);
             dir is not null && dir.StartsWith(stop, StringComparison.OrdinalIgnoreCase)
             && !Directory.EnumerateFileSystemEntries(dir).Any();
             dir = Path.GetDirectoryName(dir))
            Directory.Delete(dir);
    }

    /// <summary>删一条记录。只有这个资产上的【最后一条】记录被删时才还原并丢掉原版仓库 ——
    /// 早删会把还活在盘上的兄弟那份内容当"原版"退回去。
    /// <para>盘上那张是别人放的（<see cref="ForeignOnDisk"/>）时【一条都不动】：这时删账会得到
    /// "界面上没这个包了、本体里却躺着一张没人认领的图"，正是他报过的那类看不见的事实。
    /// 返回要说给用户的话，null = 删干净了。</para></summary>
    public static string? Delete(ConfigService cfg, string gamePath, string key)
    {
        if (!cfg.Current.ContentOverrides.TryGetValue(key, out var r)) return null;
        var held = cfg.Current.ContentOverrides.Values
            .Where(o => o.Asset.Equals(r.Asset, StringComparison.OrdinalIgnoreCase)
                        && ForeignOnDisk(gamePath, o)).ToList();
        if (held.Count > 0)
            return LocService.Tf("有 {0} 个图像文件当前既非本程序写入也非原版（可能被其他 mod 或手动修改），已停止删除，记录与文件均保留", held.Count);
        var siblings = cfg.Current.ContentOverrides.Values
            .Count(o => o.Asset.Equals(r.Asset, StringComparison.OrdinalIgnoreCase));
        if (siblings <= 1)
        {
            if (r.Enabled) RevertAsset(gamePath, r);
            try { DropOriginal(gamePath, r.Asset, r.Version); }
            catch (Exception ex) { AppLog.Warn("ContentOverride", ex.Message); }
        }
        cfg.Current.ContentOverrides.Remove(key);
        if (r.StoreId.Length > 0) DropStore(gamePath, r.StoreId);
        cfg.Save(cfg.Current);
        return null;
    }

    /// <summary>删掉一个包：【整包一起删或整包不删】。逐张删的话，拦下一张时其它张已经没了，
    /// 用户看到的是"包被删了一半"，比删不掉更难解释。
    /// 返回要报给用户的话（null = 全删干净）。</summary>
    public static string? DeletePack(ConfigService cfg, string gamePath, string slot)
    {
        var mine = cfg.Current.ContentOverrides.Values
            .Where(r => r.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase)).ToList();
        var held = mine.Count(r => ForeignOnDisk(gamePath, r));
        if (held > 0)
            return LocService.Tf("有 {0} 个图像文件当前既非本程序写入也非原版（可能被其他 mod 或手动修改），已停止删除，记录与文件均保留", held);
        // 先记下原包备份的目录名：下面的循环会把账清空，清完就再也查不到这个包当初落在哪个文件夹了
        var packDir = mine.Select(r => r.PackDir).FirstOrDefault(d => d.Length > 0) ?? "";
        foreach (var r in mine) Delete(cfg, gamePath, KeyOf(r.Asset, r.Slot));
        DropPackBackup(gamePath, cfg, slot, packDir);
        return null;
    }

    /// <summary>删除一个包时，把它在 <c>asset-packs</c> 下的原包备份一起清掉。
    ///
    /// <para>以前只清账、不动这个目录，而全仓唯一会删 asset-packs 的代码是
    /// <see cref="PreserveOriginalPack"/> 里"重装同名包先清旧目录"那一条 —— 它靠【账上还有记录
    /// 指着这个目录名】认人。删除恰好把账清空，于是这个目录从此无人认领：
    /// 实测 2026-10-07 删掉 1554 张表那个包之后，210MB 的原包备份还躺在 asset-packs 里，
    /// 用户开文件夹看见"删掉的包还在"。</para>
    ///
    /// <para>只删这个 slot 自己记过的那个目录名；删前再确认没有别的 slot 还指着它
    ///（撞名会落 -2，理论上不会共用，但老账 PackDir 为空时我们本来也不知道目录是谁的，
    /// 那种情况不动手）。删失败只记日志 —— 账已经清了，备份留着比让删除整体报错强。</para></summary>
    private static void DropPackBackup(string gamePath, ConfigService cfg, string slot, string packDir)
    {
        if (packDir.Length == 0) return;
        if (cfg.Current.ContentOverrides.Values.Any(r =>
                !r.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase)
                && r.PackDir.Equals(packDir, StringComparison.OrdinalIgnoreCase))) return;
        var dir = PackOriginalDir(gamePath, packDir);
        if (!Directory.Exists(dir)) return;
        try
        {
            Directory.Delete(dir, true);
            AppLog.Warn("ContentOverride", $"[删除] 原包备份已清：asset-packs/{packDir}");
        }
        catch (Exception ex) { AppLog.Warn("ContentOverride", "原包备份删除失败（账已清，目录留着）：" + ex.Message); }
    }

    /// <summary>全部退回原版并清账。切游戏版本之前必须先跑它 —— 否则改过的 Content 会被
    /// 当成"那个版本的原版"整目录收进版本缓存（TryAdoptCurrentBody 是 Directory.Move）。
    /// <para><paramref name="HeldBack"/> = 因为盘上那张是别人放的而【没敢动】的张数。
    /// 这些行留着 Enabled，界面上继续标红，等人去看是什么盖的 —— 静默跳过比少还原一张更糟。</para></summary>
    public static (int Reverted, int HeldBack) RevertAll(ConfigService cfg, string gamePath)
    {
        var n = 0;
        var held = 0;
        foreach (var r in cfg.Current.ContentOverrides.Values.Where(r => r.Enabled).ToList())
        {
            if (ForeignOnDisk(gamePath, r)) { held++; continue; }
            if (RevertAsset(gamePath, r)) { r.Enabled = false; n++; }
        }
        cfg.Save(cfg.Current);
        if (held > 0) AppLog.Warn("ContentOverride", $"[全部还原] {held} 张盘上已是别人放的图，未覆盖");
        return (n, held);
    }

    // ───────────────────── 盘账一致性 ─────────────────────

    /// <summary>一条记录在当前本体上的实际状态。分开「早就被还原」与「被外来字节顶掉」是必要的：
    /// 前者最常见的原因是 Steam 验证了完整性或游戏更新换了本体，那是【已经回到原版】，
    /// 界面上报红色「已不在位」等于把一件已经做完了的事说成故障（用户 2026-10-07 指出过这类文案）。</summary>
    public enum OverrideState { Off, InPlace, AlreadyVanilla, Foreign, Gone }

    /// <summary>只看盘：这张图现在是谁的。<b>不问账上的开关</b> —— 需要判"底干不干净"的地方
    /// 要的就是这个；<see cref="StateOf"/> 在它之上多问一句"账上说开着吗"，那一问会把
    /// "账说关着、盘上还盖着我们的图"看成 Off。</summary>
    public static OverrideState DiskState(string gamePath, ContentOverrideRecord r)
    {
        var dst = AssetFullPath(gamePath, r.Asset);
        if (!File.Exists(dst)) return OverrideState.Gone;
        var now = Sha256(dst);
        if (now == r.AppliedSha256) return OverrideState.InPlace;
        if (r.OriginalSha256.Length > 0 && now == r.OriginalSha256) return OverrideState.AlreadyVanilla;
        var orig = OriginalPath(gamePath, r.Asset, r.Version);
        if (File.Exists(orig) && now == Sha256(orig)) return OverrideState.AlreadyVanilla;
        return OverrideState.Foreign;
    }

    public static OverrideState StateOf(string gamePath, ContentOverrideRecord r)
        => r.Enabled ? DiskState(gamePath, r) : OverrideState.Off;

    /// <summary>账上开着、盘上却不是那份字节，而且那份【既不是我们放的也不是原版】（= 别人或手动动过），
    /// 或者文件干脆没了。Steam 验证还原成原版那种不算，见 <see cref="StateOf"/>。</summary>
    public static bool Drifted(string gamePath, ContentOverrideRecord r)
        => StateOf(gamePath, r) is OverrideState.Foreign or OverrideState.Gone;

    /// <summary>还原前的一道闸：盘上那张既不是我们放上去的、也不是原版 ⇒ 别拿原版去盖它。
    /// 盖下去看着"还原成功"，实际抹掉的是第三方那张图，而且没有任何地方说过它存在过。
    /// 只有【开着】的行才谈得上：关着的行盘上本来就该是原版或别人的，还原是空操作。</summary>
    public static bool ForeignOnDisk(string gamePath, ContentOverrideRecord r)
        => StateOf(gamePath, r) == OverrideState.Foreign;

    /// <summary>一条"包已经不在了、但它对本体的覆盖还生效"的对账结果。
    /// 四个计数互斥、相加 = EnabledCount。</summary>
    public sealed record OrphanOverrideGroup(string Slot, string PackName, string PackDir,
        int EnabledCount, int SafeToRevert, int AlreadyReverted, int DriftedCount, int AddedOnly)
    {
        /// <summary>能安全还原的条数（现值 == 我们放上去那份）。界面只在它 &gt; 0 时才该给还原入口。</summary>
        public bool Revertible => SafeToRevert > 0;
    }

    /// <summary>孤儿对账：包已经不在了（Mods 下的 mod 目录、或 asset-packs 里的原包副本都没了），
    /// 但它写进游戏本体的覆盖【不会自己撤回】。
    ///
    /// <para>为什么会有这个洞：写盘的入口只有 <see cref="SetPackEnabled"/> 与 <see cref="DeletePack"/>，
    /// 而它俩的唯一调用点在素材页（<c>Packs.razor</c>）。Mods 页的删除/移入回收站走 <c>ModService</c>，
    /// 根本不碰 <c>contentOverrides</c>。2026-10-06 实测：一条 101 张 <c>Portraits/*</c> 的账，
    /// 源包 10-04 02:58 就被移进 <c>Mods\.junigrid_trash\</c>，覆盖在本体里躺了两天 ——
    /// 期间 SMAPI 每次启动报 <c>Some of the game's content files were modified or corrupted</c>，
    /// 而且它压住肖像页的一切选择（用户在 UI 里怎么设都看不到变化）。</para>
    ///
    /// <para>⚠ <b>存活判定不能用 <c>asset-packs/&lt;packDir&gt;</c></b>：那是【原包备份】，
    /// 按设计一直躺着（用途是"关掉再打开不必重新下载"）。拿它当存活证据，本案永远判不出来。
    /// 只认 <c>Mods</c> 树里带标记文件（<c>manifest.json</c> / <c>.junigrid.json</c>）的目录 ——
    /// 实测一级子目录里躺着 <c>assets</c>/<c>i18n</c> 这类通用名，只比目录名会误判。
    /// 带点号的【禁用态算活着】：那是用户在 Mods 页的明确决定，覆盖留着符合预期，不是孤儿。</para>
    ///
    /// <para>这一版【只点名、不写盘】：判定一旦误伤，自动还原会把用户故意留下的画风静默改掉。
    /// 逐条分四档，其中 <c>DriftedCount</c>（现值既不是我们那份也不是原版 = 别人或 Steam 动过）
    /// 与 <c>AddedOnly</c>（原版本来没这张，还原 = 删文件）永远不该被自动写。</para>
    ///
    /// <para><c>packDir</c> 与 <c>packName</c> 都为空的记录（老账）判不了 ⇒ 不报，别猜。</para>
    /// </summary>
    public static List<OrphanOverrideGroup> AuditOrphans(string gamePath, ConfigService cfg)
    {
        var live = LivePackNames(gamePath);
        // ⚠ 本体覆盖的包【不住在 Mods\】：Commit 把原包整份收进 <游戏>/JuniGrid/asset-packs/<名字>，
        //   「打开文件夹」打开的是它，还原时读原件也是从它读（见 PackOriginalDir）——
        //   所以它就是这类包的存活证据。只查 Mods 的后果实测过（2026-10-07 沙盒端到端）：
        //   刚装完两分钟的包立刻被判「包已删除，本体覆盖仍在生效」。
        try
        {
            if (Directory.Exists(PacksRoot(gamePath)))
                foreach (var d in Directory.EnumerateDirectories(PacksRoot(gamePath)))
                    live.Add(Path.GetFileName(d));
        }
        catch (Exception ex) { AppLog.Warn("ContentOverride", "asset-packs 存活枚举失败: " + ex.Message); }
        var outp = new List<OrphanOverrideGroup>();
        foreach (var g in cfg.Current.ContentOverrides.Values
                     .GroupBy(r => r.Slot, StringComparer.OrdinalIgnoreCase))
        {
            var on = g.Where(r => r.Enabled).ToList();
            if (on.Count == 0) continue;
            var dir = on.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.PackDir))?.PackDir ?? "";
            var name = on.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.PackName))?.PackName ?? "";
            if (dir.Length == 0 && name.Length == 0) continue;      // 判不了就不点名
            if (live.Contains(dir) || (name.Length > 0 && live.Contains(name))) continue;

            int safe = 0, already = 0, drift = 0, added = 0;
            foreach (var r in on)
            {
                if (r.OriginalSha256.Length == 0) { added++; continue; }   // 原版没这张 ⇒ 还原=删文件
                var dst = AssetFullPath(gamePath, r.Asset);
                if (!File.Exists(dst)) { drift++; continue; }
                var now = Sha256(dst);
                if (now == r.AppliedSha256) safe++;
                else if (now == r.OriginalSha256) already++;                // 账没跟上，盘上已经是原版
                else drift++;                                                 // 别人放的 ⇒ 只报不覆盖
            }
            outp.Add(new OrphanOverrideGroup(g.Key, name, dir, on.Count, safe, already, drift, added));
        }
        return outp.OrderBy(x => x.PackName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Mods 树里【真的装着包】的目录名与 manifest 里的 Name。
    /// 深度限 3（SVE 的包在 <c>Stardew Valley Expanded/[CP] …</c> 这种两层下面），
    /// 跳过点号开头的目录本身 —— 但把它的名字去掉点号后计入（禁用态仍算"在"）。</summary>
    private static HashSet<string> LivePackNames(string gamePath)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mods = Path.Combine(gamePath, "Mods");
        if (!Directory.Exists(mods)) return set;
        void Probe(string dir, int depth)
        {
            string[] subs;
            try { subs = Directory.GetDirectories(dir); } catch { return; }
            foreach (var d in subs)
            {
                var leaf = Path.GetFileName(d);
                if (leaf.Equals(".junigrid_trash", StringComparison.OrdinalIgnoreCase)) continue;
                var bare = leaf.TrimStart('.');
                var marker = false;
                try
                {
                    marker = File.Exists(Path.Combine(d, "manifest.json"))
                             || File.Exists(Path.Combine(d, ".junigrid.json"));
                }
                catch { }
                if (marker)
                {
                    set.Add(bare);
                    try
                    {
                        var nm = Newtonsoft.Json.Linq.JObject.Parse(
                            File.ReadAllText(Path.Combine(d, "manifest.json")))["Name"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(nm)) set.Add(nm!);
                    }
                    catch { }
                }
                if (depth < 3) Probe(d, depth + 1);
            }
        }
        Probe(mods, 1);
        return set;
    }

    /// <summary>这张原版图有没有语言变体（<c>Cursors.zh-CN.xnb</c> 那种）。有 ⇒ 只换基名那张，
    /// 中文客户端可能仍然读到变体，效果看不见。我们【不】替作者去盖变体：变体存在的唯一理由
    /// 就是那张图上有本地化文字，盖掉会把中文界面拆了。所以把它作为提醒原样报出来。</summary>
    public static List<string> LocaleSiblings(string gamePath, string asset)
    {
        var list = new List<string>();
        try
        {
            var full = AssetFullPath(gamePath, asset);
            var dir = Path.GetDirectoryName(full);
            var stem = Path.GetFileNameWithoutExtension(full);
            if (dir is null || !Directory.Exists(dir)) return list;
            foreach (var f in Directory.EnumerateFiles(dir, stem + ".*.xnb"))
            {
                var m = LocaleTail.Match(Path.GetFileNameWithoutExtension(f));
                if (m.Success && !list.Contains(m.Value[1..])) list.Add(m.Value[1..]);
            }
        }
        catch (Exception ex) { AppLog.Warn("ContentOverride", ex.Message); }
        return list;
    }

    // ───────────────────── 界面上一行 = 一张图 ─────────────────────

    /// <summary>资产名末尾的语言后缀（<c>Data/Boots.zh-CN</c> 的那截）。星露谷把同一份资产按语言
    /// 各存一个文件，游戏按当前语言优先读带后缀那份 —— 所以一个包要处处生效就得把 11 种语言
    /// 全带上（实测 Nexus 1083 那包 1554 个文件里 1217 个就是这么来的）。
    /// <para>这个式子原先在 <see cref="LocaleSiblings"/> 与 <see cref="BuildStemIndex"/> 里各写一遍，
    /// 收成一处：界面并行、候选表排除变体、写盘展开副本必须是【同一个】"什么算变体"的答案。</para></summary>
    private static readonly Regex LocaleTail = new(@"\.[a-z]{2}-[A-Z]{2}$", RegexOptions.Compiled);

    /// <summary>去掉语言后缀 = 这张图本来的名字；不带后缀的原样返回。</summary>
    public static string LocaleBase(string asset) => LocaleTail.Replace(asset, "");

    /// <summary>把包内的账收成界面行：先剔掉不是图的（对话表 / 事件脚本 / 日程 / 字符串表 ——
    /// 判据就用安装时那道 <see cref="IsSwappable"/>，不另造一个"看起来像图"的说法），
    /// 再把同一张图的语言副本并成一条。基名那条不在包里时由某个副本代表，
    /// 一条图都不会凭空从列表里消失。</summary>
    public static List<ContentOverrideRecord> ImageRows(string gamePath,
        IEnumerable<ContentOverrideRecord> items)
    {
        var picked = new List<ContentOverrideRecord>();
        foreach (var g in items.Where(r => IsSwappable(gamePath, r.Asset))
                     .GroupBy(r => LocaleBase(r.Asset), StringComparer.OrdinalIgnoreCase))
        {
            // 代表优先挑【开着】的那一条（基名在前）：基名被单独关掉、语言副本还盖着的时候，
            // 拿基名当代表会让「按对象」那张卡报成"没应用"，而游戏里早就换图了。
            picked.Add(g.FirstOrDefault(r => r.Enabled
                         && r.Asset.Equals(g.Key, StringComparison.OrdinalIgnoreCase))
                       ?? g.FirstOrDefault(r => r.Enabled)
                       ?? g.FirstOrDefault(r => r.Asset.Equals(g.Key, StringComparison.OrdinalIgnoreCase))
                       ?? g.First());
        }
        return picked.OrderBy(r => r.Asset, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>这个包里与 <paramref name="asset"/> 属于【同一张图】的全部资产名：基名本身
    /// 加它在账上的所有语言副本。界面并成一行，写盘就必须一起写 —— 只盖基名那张的话，
    /// 中文客户端读到的还是另一份，症状是"这个包关掉了却还有半包没还原"。</summary>
    public static List<string> SlotSiblings(ConfigService cfg, string slot, string asset)
    {
        var baseName = LocaleBase(asset);
        return cfg.Current.ContentOverrides.Values
            .Where(r => r.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase)
                        && LocaleBase(r.Asset).Equals(baseName, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Asset)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>给「按对象」视图用的 <c>take</c> 谓词：这条账在界面上是不是【一行】。
    /// 一次算好代表集合再查表，不在谓词里重扫账本 —— 谓词会被每个视图调用好几遍。</summary>
    public static Func<ContentOverrideRecord, bool> ShownRowPredicate(string gamePath, ConfigService cfg)
    {
        var reps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in cfg.Current.ContentOverrides.Values
                     .GroupBy(r => r.Slot, StringComparer.OrdinalIgnoreCase))
            foreach (var r in ImageRows(gamePath, g))
                reps.Add(KeyOf(r.Asset, r.Slot));
        return r => reps.Contains(KeyOf(r.Asset, r.Slot));
    }

    /// <summary>界面上的一行 = 一张图的全部文件（基名 + 它在这个包里的所有语言副本），
    /// 所以这一行的状态必须按【整组】算。
    /// <para>为什么不能只看基名那一条：实测 1083 那包的 <c>LooseSprites/Cursors</c> 基名被单独
    /// 关掉过，而 11 份语言副本还盖在游戏本体上 —— 游戏里已经是 fur 鼠标，只看基名却会报成
    /// "这一行没应用"。并成一行之后，行说的得是这 12 个文件的共同事实。</para>
    /// <para><see cref="StateOf"/> 对关着的行返回 <see cref="OverrideState.Off"/>，
    /// 所以外来字节 / 已还原只会从【开着】的那些文件里报出来。</para></summary>
    public static OverrideState ImageGroupState(string gamePath, ConfigService cfg,
        string slot, string asset)
    {
        var st = OverrideState.Off;
        foreach (var f in SlotSiblings(cfg, slot, asset))
        {
            if (!cfg.Current.ContentOverrides.TryGetValue(KeyOf(f, slot), out var r)) continue;
            var one = StateOf(gamePath, r);
            if (one is OverrideState.Foreign or OverrideState.Gone) return one;   // 失效最要紧，先报
            if (one == OverrideState.InPlace) st = OverrideState.InPlace;
            else if (one == OverrideState.AlreadyVanilla && st != OverrideState.InPlace) st = one;
        }
        return st;
    }

    // ───────────────────── 目标解析 ─────────────────────

    /// <summary>某个本体资产的纹理宽高，按需精确解析并记忆化。解不出来返回 (0,0)。
    ///
    /// 为什么不建全量宽高索引：<see cref="BuildStemIndex"/> 要扫 3550 个 xnb，
    /// TryTextureArea 只读 14 字节头，很快；但真宽高要解压（TryLocate），
    /// 全量跑 4 秒以上 —— 这正是立绘页当初专门加跳过闸门躲的开销。
    /// 而【候选】每次只有 2–5 个（同一个基名），单独解这几个并缓存即可，
    /// 同一个资产在包里出现几十次也只解一次。
    ///
    /// 超过 24MB 的 xnb 直接放弃（那必然是 4096² 级别的超大图），免得为一个
    /// 判定结果解压几十 MB —— 判不出来就退回面积筛选，与以前一样不会更糟。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int w, int h)>
        _sizeCache = new(StringComparer.OrdinalIgnoreCase);

    private static (int w, int h) TextureSizeOf(string gamePath, string asset)
    {
        var key = gamePath + "|" + asset;
        return _sizeCache.GetOrAdd(key, _ =>
        {
            try
            {
                var f = AssetFullPath(gamePath, asset);
                if (!File.Exists(f) || new FileInfo(f).Length > 24L * 1024 * 1024) return (0, 0);
                var lay = XnbDecoder.TryLocate(f);
                return lay is null ? (0, 0) : (lay.Width, lay.Height);
            }
            catch { return (0, 0); }
        });
    }

    /// <summary>
    /// 基名 → Content 里的候选资产，连同只读 14 字节头算出的像素面积。
    /// 这一步【只读头不解压】，扫 3550 个资产约 1.7 秒（实测）——
    /// 立绘页当初专门加过跳过闸门躲的就是解压那几秒，别在这里把它请回来。
    /// 能不能整张替换由 <see cref="IsSwappable"/> 按需判 + 缓存，不塞进这张表。
    /// </summary>
    private static Dictionary<string, List<(string asset, int area)>> BuildStemIndex(
        string contentRoot, out HashSet<string> variantBases)
    {
        var idx = new Dictionary<string, List<(string, int)>>(StringComparer.OrdinalIgnoreCase);
        // 语言变体不进候选表：换基名那张才是正路，动变体等于替作者决定中文界面长什么样。
        // 但【基名本身存在】这件事要记下来 —— 包里若是 SmallFont.zh-CN 而本体只有那张中文变体，
        // 查不到候选就说"本体没有叫 SmallFont 的图像"是误报（本体明明就在那儿躺着）。
        // 在这里顺手记，ResolveTarget 是每个包内文件调一次，不能让它扫 3550 个键。
        variantBases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(contentRoot, "*.xnb", SearchOption.AllDirectories))
        {
            var asset = Path.GetRelativePath(contentRoot, f).Replace('\\', '/');
            asset = asset[..^".xnb".Length];
            var stem = Path.GetFileName(asset);
            if (LocaleTail.IsMatch(stem))
            {
                variantBases.Add(Path.GetFileNameWithoutExtension(stem));
                continue;
            }
            // area==0 = 解不出纹理但文件确实在本体里（字体 SpriteFont 就是这一类）。
            // 原来 `area is null ⇒ continue` 把它们全丢了，于是字体包一律报
            // "游戏本体里没有叫 SmallFont 的图像" —— 那是误报，文件明明就在那儿。
            var area = XnbCodec.TryTextureArea(f) ?? 0;
            if (!idx.TryGetValue(stem, out var l)) idx[stem] = l = new List<(string, int)>();
            l.Add((asset, area));
        }
        return idx;
    }

    /// <summary>能不能整张替换的判定结果，记住是因为它要解压内容区。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool>
        _swapCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 这份本体资产我们【能不能整张换掉】—— 有 Texture2DReader（纹理）或
    /// SpriteFontReader（字体）才算数，两者都是自描述容器，整份写进去游戏就按新表渲染。
    /// <para>素材页"这一行算不算一张图"用的也是它（见 <see cref="ImageRows"/>）：
    /// 装进来的闸门和列出来的闸门必须是同一个答案，否则会出现"装上了但界面上找不到"。</para>
    ///
    /// 判据【绝对不能】写 <c>area &gt; 0</c>：排程表与对白是 DictionaryReader&lt;String,String&gt;，
    /// 它的 bodyLen-177 照样可能是正的 4 倍数 —— 实测 Maru 的排程表算出 area=636、
    /// Strings 排程表 155、Penny 的对话表 3177，于是 area&gt;0 会把这堆纯文本当成
    /// "能换的纹理"顶进候选，Maru / Penny 就又变成"本体里有 2 张同名的，分不清该换哪一张"。
    ///
    /// 为什么按需判而不是建索引时全判：判"是不是纹理"得解压内容区，
    /// 3550 个资产全跑一遍要 4 秒以上（只读头全扫只要 1.7 秒），而 Plan 每装一个包跑一次。
    /// 真正要问的只有【同名候选】2–5 个 —— 一个包里绝大多数基名唯一，
    /// 唯一候选在 ResolveTarget 里直接返回，走都走不到这里。
    /// </summary>
    public static bool IsSwappable(string gamePath, string asset)
        => _swapCache.GetOrAdd(gamePath + "|" + asset, _ =>
        {
            try
            {
                // 目录级的快速排除：这些目录按内容分类就是文本/数据表
                // （实测 Characters/schedules/*、Characters/Dialogue/*、Strings/* 全是
                // DictionaryReader<String,String>），解内容区也只是白花时间。
                var a = asset.Replace('\\', '/');
                if (a.Contains("/schedules/") || a.Contains("/Dialogue/")
                    || a.StartsWith("Strings/", StringComparison.OrdinalIgnoreCase)) return false;

                var f = AssetFullPath(gamePath, asset);
                if (!File.Exists(f)) return false;
                return XnbCodec.IsTextureAsset(f) || XnbCodec.IsFontAsset(f);
            }
            catch { return false; }
        });

    /// <summary>
    /// 包内一个文件 → Content 资产。两级梯子，推不出来就【不猜】：
    /// ① 包自己声明的目标（<c>LooseSprites.png/Cursors.png</c>、<c>Starry Sky/LooseSprites/Cursors.xnb</c>）；
    /// ② 扁平单文件：基名唯一，或按像素面积筛到唯一 —— 实测 Nexus 1839 那 102 个文件只靠基名
    ///    有 101 个撞车（Abigail 同时在 Characters/、Portraits/、Dialogue/、schedules/ 里），
    ///    加上面积才剩 87 个唯一。
    /// 仍撞车的（立绘表和走路表都是 128×64 的 MrQi）只能点名问人：猜错的代价是游戏里
    /// 某个角色长成别人的脸，而玩家会以为是包的锅。
    /// <paramref name="sawVanillaName"/> = 这个名字在本体里【确实是一张原版图】（哪怕尺寸不对）。
    /// 调用方靠它区分"根本不是给本体的包"（交回原路）和"给本体但画的是别的版本"（就地拒绝并点名）——
    /// 只数装成功的会漏掉后者，而后者正是当年"13 个文件转出 5 条假立绘"的入口。
    /// </summary>
    private static string? ResolveTarget(string gamePath, string relInPack, string file,
        Dictionary<string, List<(string asset, int area)>> stemIndex, HashSet<string> variantBases,
        HashSet<string> modDeclared, out string? why, out bool sawVanillaName)
    {
        why = null; sawVanillaName = false;
        var declared = ModService.ResolveAssetTarget(gamePath, relInPack, modDeclared);
        if (declared is not null && File.Exists(AssetFullPath(gamePath, declared)))
        {
            sawVanillaName = true;
            return declared;
        }

        var stem = Path.GetFileNameWithoutExtension(relInPack);
        if (!stemIndex.TryGetValue(stem, out var all) || all.Count == 0)
        {
            // 先分清两种"查不到"，它们要给的结论完全不同：
            //  · 带语言变体后缀的（SmallFont.zh-CN）—— 本体【有】那张，只是被候选表排除了。
            //    无条件报"本体没有"是误报：用户会以为包坏了，而本体那张其实正躺在 Content 里。
            //  · 真的没有（实测 Dobson / tinyFontBorder / DialogBoxGreen：全库 3550 个资产搜不到，
            //    且游戏 DLL 的字符串堆里连这个名字都没有 —— 是游戏自己不认识它）—— 这才是实话。
            var bare = Path.GetFileNameWithoutExtension(stem);
            why = variantBases.Contains(bare)
                ? LocService.Tf("{0}：本体中对应的是 {1}；本程序不替换语言变体 —— 请替换 {1} 的原版",
                    Path.GetFileName(relInPack), bare)
                : LocService.Tf("{0}：游戏本体中不存在名为 {1} 的图像", Path.GetFileName(relInPack), stem);
            return null;
        }
        // 【剔掉换不了的】同名候选里混着排程表与对白（实测 Characters/schedules/Abigail、
        // Characters/Dialogue/Abigail、Strings/schedules/Abigail 全是 DictionaryReader<String,String>）。
        // 我们没有这类资产的编码器，整份塞进去只会把角色的行程与对白写坏，所以它们不进候选：
        // 早先只按 area==0 挑（把"算不出纹理的"一律当字体），每个角色凭空多出 3 个"同名"，
        // 于是 Abigail/Harvey/George… 26 个角色集体报"分不清该换哪一张"，
        // 而包里那张 128×320 跟本体 Portraits/Abigail 的 128×320 根本一模一样。
        //
        // 只有【多个候选】才需要判；单个候选直接返回（它要么装得上，要么 Plan 会给文案）。
        var cands = all.Count == 1 ? all : all.Where(c => IsSwappable(gamePath, c.asset)).ToList();
        if (cands.Count == 0)
        {
            why = LocService.Tf("{0}：本体中名为 {1} 的文件属于日程表/对话等文本，无法替换",
                Path.GetFileName(relInPack), stem);
            return null;
        }
        sawVanillaName = true;      // 名字对得上原版图，尺寸或歧义的问题在下面报
        var isPng = Path.GetExtension(file).Equals(".png", StringComparison.OrdinalIgnoreCase);
        if (cands.Count == 1 && isPng)
            return cands[0].asset;      // PNG 装不上，但认得出是哪张 ⇒ 拒绝文案与"同版另一编码"去重都要用它

        // 目标是字体（解不出纹理，但类型表里有 SpriteFontReader）⇒ 没有面积可比，
        // 可【名字对得上】本身就是最强的一条证据：字体表是自描述容器（内部记着自己的
        // 字号/行距/字形贴图），整份替换后游戏按新表渲染，不需要我们读懂它，
        // 也不存在"格位对不上"的问题。必须在这里返回 ——
        // 再往下走，.xnb 字体会在读尺寸那步拿到 null 被误判成"读不出尺寸"。
        if (!isPng)
        {
            // 用 TextureSizeOf 而不是 area==0：这里要问的是"解不解得出纹理"，
            // 而 area==0 也可能是别的原因（数据类资产），两者不再混为一谈。
            var byName = cands.Where(c => TextureSizeOf(gamePath, c.asset).w == 0).ToList();
            if (byName.Count == 1) return byName[0].asset;
            if (byName.Count > 1)
            {
                why = LocService.Tf("{0}：本体中有 {1} 张同名的 {2}，无法确定替换对象",
                    Path.GetFileName(relInPack), byName.Count, stem);
                return null;
            }
        }

        // 包里这张图的宽高读一次，既拿来筛候选也拿来写拒绝文案（两处各自再解一遍是浪费，
        // 而且两边万一读得不一致，文案就会说假话）。
        (int w, int h)? srcDims = isPng
            ? PixelKit.DecodePng(file) is { } t ? (t.Width, t.Height) : null
            : XnbDecoder.TryLocate(file) is { } l ? (l.Width, l.Height) : null;
        if (srcDims is null)
        {
            why = LocService.Tf("{0}：无法读取该图像尺寸", Path.GetFileName(relInPack));
            return null;
        }
        // 【宽度定案】立绘 128 宽为主、走路表 64 宽（实测本体 Portraits/ 是 128 宽 95 张 +
        // 64 宽 6 张；Characters/ 绝大多数 64 宽），所以裸包里一个 <Name>.xnb 属于哪张表，
        // 宽度基本就能定。放在面积筛选【之前】：Characters/ 与 Portraits/ 同名是常态
        // （Abigail 两边都有），而包里的图跟其中任意一张对不上也是常事（作者按旧格数画的），
        // 先用宽度定下是哪一类表，才不会走进"哪个都对不上"的死胡同。
        if (!isPng)
        {
            // 宽高都对上：最理想，直接定。
            var exact = cands
                .Where(c => TextureSizeOf(gamePath, c.asset) == (srcDims.Value.w, srcDims.Value.h))
                .ToList();
            if (exact.Count == 1) return exact[0].asset;

            // 宽度对上但高度不同 = 确定是同一类表（立绘 vs 走路表），只是格数不同。
            // 这种情况以前直接判"不适配"，现在交回 Plan 照装 —— 游戏用 SpriteBatch
            // 的源矩形按固定格位切帧、目标矩形另外缩放，少几帧表情不会串格。
            var sameWidth = cands
                .Where(c => srcDims.Value.w > 0 && TextureSizeOf(gamePath, c.asset).w == srcDims.Value.w)
                .ToList();
            if (sameWidth.Count == 1) return sameWidth[0].asset;

            // 源是 64×64 单帧、目标是 128×64 立绘 ⇒ 也是"同一张脸，只是少一列眨眼"。
            // 实测：本体四张 128×64 立绘（Grandpa/Gil/MrQi/Wizard）的第 2 列与第 1 列
            // 逐像素只差 56~783/4096，差异区域全部落在眼部 ⇒ 第 2 列是眨眼帧。
            // 以前这里匹配不到（同宽要求 64==128），一路掉到下面报"几张对不上"，
            // 把"少眨一次眼"误判成了"这不是给本体的包"。放行后交给 Plan 的宽度闸门，
            // 那里有同一个判据（IsSingleFramePortraitFill）兜底。
            var singleFrameFill = cands
                .Where(c => IsSingleFramePortraitFill(srcDims.Value.w, srcDims.Value.h,
                             TextureSizeOf(gamePath, c.asset).w, TextureSizeOf(gamePath, c.asset).h, c.asset))
                .ToList();
            if (singleFrameFill.Count == 1) return singleFrameFill[0].asset;
        }

        var hit = cands.Where(c => c.area == srcDims.Value.w * srcDims.Value.h).ToList();
        if (hit.Count == 1) return hit[0].asset;
        // 面积一张都没对上、但本体里这个名字只有一张图：仍然交回去，
        // 让下面那道尺寸闸门用「64×64 对 64×128」说话 —— 报"这张 4096 格"是要人自己开计算器，
        // 而装不上的人想知道的是"差在哪"，不是"差多少个数"。
        if (hit.Count == 0 && cands.Count == 1) return cands[0].asset;
        // 报"没有这个尺寸"必须把【每个候选各是多大的】一起列出来。
        // 原话是"多半是给别的版本画的"，实测是误导：Grandpa 包内 64×64 与本体
        // Portraits/Grandpa 128×64 讲的是同一个人（本体那张是两个 64 格并排的两帧），
        // 只是包里那张少一帧 —— 不是"别的版本"。只报尺寸数字，人只能自己猜差在哪。
        why = hit.Count == 0
            ? LocService.Tf("{0}：此文件 {1}×{2}，与本体中名为 {3} 的数张均不匹配（{4}）",
                Path.GetFileName(relInPack), srcDims.Value.w, srcDims.Value.h, stem,
                string.Join("、", cands.Select(c =>
                {
                    var d = TextureSizeOf(gamePath, c.asset);
                    return d.w == 0 ? c.asset : LocService.Tf("{0} 为 {1}×{2}", c.asset, d.w, d.h);
                })))
            : LocService.Tf("{0}：本体中有 {1} 张同名同尺寸的原版图像（{2}），无法确定替换对象",
                Path.GetFileName(relInPack), hit.Count, string.Join("、", hit.Select(h => h.asset)));
        return null;
    }

    /// <summary>
    /// "单帧立绘补列"放行：源 64 宽、目标 128 宽，且目标是立绘。
    ///
    /// 为什么只认这一个组合 —— 结论来自本体资产实测，不是对美术手法的猜测：
    /// 本体 <c>Content/Portraits/</c> 是 128 宽 95 张 + 64 宽 6 张；
    /// 128×64 的只有 Grandpa / Gil / MrQi / Wizard 四张，把它们的两列逐像素比对：
    /// Grandpa 差异 95/4096、MrQi 56、Gil 783、Wizard 187，且差异区域全部落在眼部
    /// （Grandpa 是 x∈[12,51] y∈[23,45]，正是眼睛的位置）⇒ 第 2 列是<b>眨眼帧</b>，
    /// 不是第二套表情、也不是第二个人。所以拿 64 宽单帧换进去，丢的是眨眼，脸是对的。
    /// 而本体里本就有 6 张 64×64 立绘（AnsweringMachine / Bouncer / Dwarf / Gunther），
    /// 证明游戏本来就渲染得了单列立绘。
    ///
    /// 高度也要求一致（都 64）：只允许"少一列眨眼"，不允许同时少行 ——
    /// 少行会丢掉整排表情，那是另一回事，仍该由下面的高度分支去照装/拒绝。
    /// 其余宽度不等一律仍走整单拒绝，那是真串格。
    /// </summary>
    private static bool IsSingleFramePortraitFill(
        int mineW, int mineH, int theirsW, int theirsH, string asset)
        => mineW == 64 && mineH == 64 && theirsW == 128 && theirsH == 64
           && asset.StartsWith("Portraits/", StringComparison.OrdinalIgnoreCase);

    private static string StoreCopy(string gamePath, byte[] bytes, string asset)
    {
        var storeId = Guid.NewGuid().ToString("N")[..12];
        try
        {
            var keep = StoreFile(gamePath, storeId, asset);
            Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
            File.WriteAllBytes(keep, bytes);
            return storeId;
        }
        catch (Exception ex)
        {
            AppLog.Warn("ContentOverride", "[纯净版] 副本保存失败，之后将无法重新开启：" + ex.Message);
            return "";
        }
    }

    private static void DropStore(string gamePath, string storeId)
    {
        try
        {
            var dir = StoreDir(gamePath, storeId);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch (Exception ex) { AppLog.Warn("ContentOverride", ex.Message); }
    }

    private static string Sha256(string file)
    {
        try { return Sha256Of(File.ReadAllBytes(file)); }
        catch (Exception ex) { AppLog.Warn("ContentOverride", ex.Message); return ""; }
    }
    private static string Sha256Of(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
