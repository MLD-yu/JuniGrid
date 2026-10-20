using System.Collections.Concurrent;
using System.IO;

namespace JuniGrid.Services;

public static partial class ContentOverrideService
{
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
}
