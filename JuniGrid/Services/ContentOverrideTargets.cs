using System.IO;

namespace JuniGrid.Services;

/// <summary>
/// 素材包页「按对象」视图的分组模型：把 ContentOverrides 的逐资产账重组为【人认识的对象】——
/// 一个 NPC（立绘 + 行走图并成一张卡）、一匹马、一栋房子、一棵树……
/// 网格上一个格子 = 这里的一个 <see cref="TargetGroup"/>，点开弹窗按【版本】（哪个包画的）
/// 逐个应用，与立绘页"一个角色一张卡、点卡换肤"的交互同构。
///
/// 应用粒度落到 <see cref="ContentOverrideService.SetEnabled"/> 的逐资产开关上：
/// 同一个资产同时只能有一个版本在位（一张表装不下两张图），切换时兄弟版本自动转关 ——
/// 那套规矩已经经过"整包开关/删除/还原"三条路验证，这里只借道，不另造轮子。
/// </summary>
public static class ContentOverrideTargets
{
    /// <summary>一个可覆盖对象。Key：人物 = <c>P|名字</c>，其余 = <c>A|资产路径</c>。</summary>
    public sealed record TargetGroup(string Key, string Name, string Category, string Sub,
        List<ContentOverrideRecord> Records)
    {
        /// <summary>对象名下的资产（立绘排前、行走图其次，其余按路径排）—— 预览与批量应用都按这个序。</summary>
        public List<string> Assets { get; } = Records.Select(r => r.Asset)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a.StartsWith("Portraits/", StringComparison.OrdinalIgnoreCase) ? 0
                : a.StartsWith("Characters/", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>此刻有版本在位（至少一张图盖着）。</summary>
        public bool AnyOn => Records.Any(r => r.Enabled);

        /// <summary>在位的版本属于哪个包（取第一张在位图的包；一个对象正常只会有一个包的部分图在位）。</summary>
        public ContentOverrideRecord? Active => Records.FirstOrDefault(r => r.Enabled);

        /// <summary>可选版本数（不含"原版"）。</summary>
        public int VersionCount => Records.Select(r => r.Slot)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();

        /// <summary>按版本分组的记录（弹窗逐行列出版本用），按包名排。</summary>
        public IEnumerable<IGrouping<string, ContentOverrideRecord>> Versions
            => Records.GroupBy(r => r.Slot, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => DisplayPack(g.First()), StringComparer.OrdinalIgnoreCase);

        /// <summary>人物组（立绘 + 行走图并成的那张卡）。非人物组一张图一个对象。</summary>
        public bool IsPerson => Key.StartsWith("P|", StringComparison.Ordinal);

        /// <summary>这个对象名下有几个形象（默认 / 冬季 / 沙滩…）。1 = 弹窗里不必出形象 chips。</summary>
        public int VariantCount => Variants.Count;

        /// <summary>组内的形象清单，默认最前。电话机、Bear 这种只有一张的组就只剩「默认」一项。</summary>
        public List<string> Variants { get; } = Records.Select(r => VariantOf(r.Asset))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(VariantOrder).ToList();

        /// <summary>某个形象名下的资产（脸 + 身子一起算一个形象）。</summary>
        public List<string> AssetsOf(string variant) => Assets
            .Where(a => VariantOf(a).Equals(variant, StringComparison.OrdinalIgnoreCase)).ToList();

        /// <summary>某个形象下【有副本可换】的版本（包）—— 弹窗切形象时列表要跟着缩，
        /// 不然会出现"点了没反应"的行：那个包压根没画这个形象。</summary>
        public IEnumerable<IGrouping<string, ContentOverrideRecord>> VersionsOf(string variant)
        {
            var hit = AssetsOf(variant);
            return Versions.Where(g => g.Any(r => hit.Contains(r.Asset, StringComparer.OrdinalIgnoreCase)))
                .OrderBy(g => DisplayPack(g.First()), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>这个形象此刻在位的是哪个包（null = 原版）。</summary>
        public ContentOverrideRecord? ActiveOf(string variant)
        {
            var hit = AssetsOf(variant);
            return Records.FirstOrDefault(r => r.Enabled
                && hit.Contains(r.Asset, StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <summary>包显示名：N 网补到的标题优先，没补到用安装时的包名。</summary>
    public static string DisplayPack(ContentOverrideRecord r)
        => string.IsNullOrWhiteSpace(r.PackTitle) ? r.PackName : r.PackTitle;

    /// <summary>这个资产是"人物"的一部分（立绘或行走图）—— 两者按名字并成同一张卡。</summary>
    public static bool IsPerson(string asset)
        => asset.StartsWith("Portraits/", StringComparison.OrdinalIgnoreCase)
        || asset.StartsWith("Characters/", StringComparison.OrdinalIgnoreCase);

    private static string Leaf(string asset)
        => asset[(asset.LastIndexOf('/') + 1)..];

    /// <summary>形象轴 —— 判据是【原版自己怎么命名】，不是我们发明的分类。
    /// 实测 <c>Content\Portraits</c> 101 张、<c>Content\Characters</c> 872 张，
    /// 后缀只有 _Winter(30/31)、_Beach(19/21)、_JojaMart(2/2)、_Hospital(1/1)、_Trenchcoat(1/1)，
    /// 而 <c>_Spring</c> / <c>_Summer</c> / <c>_Fall</c> 一张都没有 —— 春到秋用的就是无后缀那张默认图。
    /// 所以界面只列真存在的形象，不做春夏秋冬四个假 chip（立绘页那边照旧，
    /// 因为 CP 的 <c>Season</c> token 允许作者真交出四张不同的图）。</summary>
    private static readonly (string Suffix, string Label)[] VariantSuffixes =
    {
        ("_Winter", "冬季"),
        ("_Beach", "沙滩"),
        ("_JojaMart", "JojaMart 打工"),
        ("_Hospital", "医院上班"),
        ("_Trenchcoat", "风衣"),
    };
    public const string DefaultVariant = "默认";

    /// <summary>去掉形象后缀得到本名。只剥一次已知后缀 ——
    /// <c>Assorted_Fishermen_Winter</c> 的根是 <c>Assorted_Fishermen</c>，不是 <c>Assorted</c>。</summary>
    public static string PersonRoot(string leaf)
    {
        foreach (var (suffix, _) in VariantSuffixes)
            if (leaf.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return leaf[..^suffix.Length];
        return leaf;
    }

    /// <summary>这张图属于哪个形象（无后缀 = 默认）。非人物资产也返回默认。</summary>
    public static string VariantOf(string asset)
    {
        var leaf = Leaf(asset);
        foreach (var (suffix, label) in VariantSuffixes)
            if (leaf.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return label;
        return DefaultVariant;
    }

    /// <summary>形象在界面上的次序：默认最前，其余按上面那张表的次序（= 原版里的常见程度）。</summary>
    public static int VariantOrder(string variant)
        => variant == DefaultVariant ? -1 : Array.FindIndex(VariantSuffixes,
            v => v.Label.Equals(variant, StringComparison.OrdinalIgnoreCase));

    /// <summary>对象分类（界面 chips 与分区标题用）。返回稳定的中文键，页面再过 Loc.T。</summary>
    public static string CategoryOf(string asset)
    {
        var top = asset.Split('/')[0];
        return top.ToLowerInvariant() switch
        {
            "portraits" or "characters" => "人物",
            "animals" => "动物",
            "buildings" => "建筑",
            "terrainfeatures" => "树木植被",
            // Minigames/*（TitleButtons 那张标题界面图集就是这一档）和 LooseSprites 是同一类东西：
            // 一堆不相关的 UI 小图拼在一张表上。以前映射里没有它，「标题界面」就被丢进「其他」，
            // 而它明明该跟界面杂图摆一起（用户 2026-10-07 问出来的）。
            "loosesprites" or "minigames" => "界面杂图",
            "tilesheets" => "地图图块",
            "maps" => "地图贴图",
            "fonts" => "字体",
            _ => "其他",
        };
    }

    /// <summary>分类在界面上的排序（人物最前）。</summary>
    public static int CategoryOrder(string category) => category switch
    {
        "人物" => 0,
        "动物" => 1,
        "建筑" => 2,
        "树木植被" => 3,
        "界面杂图" => 4,
        "地图图块" => 5,
        "地图贴图" => 6,
        "字体" => 7,
        _ => 8,
    };

    /// <summary>常见资产的叫法；没收录的用文件名原样（NPC id、Cursors_2 这类人不看也知道是啥）。</summary>
    private static readonly Dictionary<string, string> FriendlyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Animals/Horse"] = "马",
        ["Animals/Dog"] = "狗",
        ["Animals/Cat"] = "猫",
        ["Buildings/houses"] = "农舍房屋",
        ["LooseSprites/Cursors"] = "光标与界面",
        ["LooseSprites/daybg"] = "白天背景",
        ["LooseSprites/nightbg"] = "夜晚背景",
        ["Minigames/TitleButtons"] = "标题界面",
    };

    /// <summary>把全部覆盖账重组为对象列表（界面每次 Reload 重算 —— 账是唯一真相，不另存副本）。</summary>
    /// <summary>把账上的覆盖按【对象】并组。
    /// <paramref name="take"/> 是给界面的版本桶用的：按包与按对象必须是同一批账，
    /// 否则切到「其它版本」时一边换了列表、另一边还摆着上一版的卡。</summary>
    public static List<TargetGroup> Build(ConfigService cfg, Func<ContentOverrideRecord, bool>? take = null)
    {
        var byKey = new Dictionary<string, List<ContentOverrideRecord>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in cfg.Current.ContentOverrides.Values)
        {
            if (take is not null && !take(r)) continue;
            // 人物按【本名】并组：Abigail / Abigail_Winter / Abigail_Beach 与它们的行走图
            // 是同一个人的不同形象，不是三个对象。老写法用整叶名当键，一个角色被拆成 3 张卡。
            // 非人物按【去掉语言后缀的资产路径】并组：两个包盖同一张图，一个交出
            // LooseSprites/Cursors、另一个只交出 Cursors.de-DE，那还是一张卡
            // （实测 1083 与 13113 就是这样被拆成两张的）。
            var key = IsPerson(r.Asset) ? "P|" + PersonRoot(Leaf(r.Asset))
                                        : "A|" + ContentOverrideService.LocaleBase(r.Asset);
            if (!byKey.TryGetValue(key, out var list)) byKey[key] = list = new List<ContentOverrideRecord>();
            list.Add(r);
        }
        var result = new List<TargetGroup>(byKey.Count);
        foreach (var (key, records) in byKey)
        {
            var first = records[0];
            var person = key.StartsWith("P|", StringComparison.Ordinal);
            // 名字与副标题都按【去掉语言后缀】那张资产给：卡面上写 Cursors，
            // 而不是恰好被当代表的那份 Cursors.de-DE。
            var assetKey = ContentOverrideService.LocaleBase(first.Asset);
            var name = person ? key[2..]
                : FriendlyNames.TryGetValue(assetKey, out var nice) ? nice : Leaf(assetKey);
            // 副标题：非人物对象给完整资产路径（"TerrainFeatures/tree1" 这种才知道盖的是哪张）；
            // 人物给成员资产的自然描述（立绘 / 立绘+行走图）。
            string sub;
            if (person)
            {
                var hasP = records.Any(r => r.Asset.StartsWith("Portraits/", StringComparison.OrdinalIgnoreCase));
                var hasC = records.Any(r => r.Asset.StartsWith("Characters/", StringComparison.OrdinalIgnoreCase));
                sub = hasP && hasC ? "立绘 + 行走图" : hasP ? "立绘" : "行走图";
            }
            else sub = assetKey;
            result.Add(new TargetGroup(key, name, CategoryOf(first.Asset), sub, records));
        }
        return result
            .OrderBy(t => CategoryOrder(t.Category))
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ───────────────────── 缩略图源 ─────────────────────

    /// <summary>一个资产当前该画哪张图：有版本在位 → 那个版本的副本；没有 → 原版仓库（或游戏现文件）。
    /// 副本丢失返回 null（界面画占位，总比画错图强）。</summary>
    public static string? ThumbSource(string gamePath, List<ContentOverrideRecord> records, string asset)
    {
        var on = records.FirstOrDefault(r => r.Enabled
            && r.Asset.Equals(asset, StringComparison.OrdinalIgnoreCase));
        if (on is not null)
        {
            var copy = ContentOverrideService.StoreFile(gamePath, on.StoreId, on.Asset);
            return File.Exists(copy) ? copy : null;
        }
        return VanillaSource(gamePath, asset);
    }

    /// <summary>原版那张图：优先原版仓库（内容保证是没盖过的），没有仓库（新增的图）退回游戏现文件。</summary>
    public static string? VanillaSource(string gamePath, string asset)
    {
        var orig = ContentOverrideService.OriginalPath(gamePath, asset);
        if (File.Exists(orig)) return orig;
        var live = ContentOverrideService.AssetFullPath(gamePath, asset);
        return File.Exists(live) ? live : null;
    }

    /// <summary>一条安装备注说的是哪张资产。备注长这样：
    /// <c>Maru_Beach.xnb：此文件 128×320，游戏 Portraits/Maru_Beach 为 128×128 —— …</c>
    /// <para>必须带边界判：<c>Portraits/Maru</c> 是 <c>Portraits/Maru_Beach</c> 的前缀，
    /// 直接 Contains 会让"沙滩"那行被"默认形象"认领走 —— 于是缩略图、点开放大、
    /// 状态字全都指向另一张图（实测 Maru/Leah 的 _Beach 行就是这样错的）。</para></summary>
    public static ContentOverrideRecord? RecordForNote(
        IEnumerable<ContentOverrideRecord> items, string name, string reason)
    {
        foreach (var r in items)
            if (MentionsAsset(reason, r.Asset)) return r;
        var stem = Path.GetFileNameWithoutExtension(name);
        return stem.Length == 0 ? null : items.FirstOrDefault(r =>
            r.Asset[(r.Asset.LastIndexOf('/') + 1)..].Equals(stem, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>text 里是否【整词】出现了 asset（后面不能紧跟字母数字或 _ / ，否则那是更长的资产名）。</summary>
    private static bool MentionsAsset(string text, string asset)
    {
        if (asset.Length == 0) return false;
        for (var i = text.IndexOf(asset, StringComparison.OrdinalIgnoreCase);
             i >= 0; i = text.IndexOf(asset, i + 1, StringComparison.OrdinalIgnoreCase))
        {
            var after = i + asset.Length;
            if (after >= text.Length || !(char.IsLetterOrDigit(text[after]) || text[after] is '_' or '/'))
                return true;
        }
        return false;
    }

    /// <summary>某个版本（包）在这张资产上的图源（它的副本）；没盖这张或副本丢了返回 null。</summary>
    public static string? VersionSource(string gamePath, List<ContentOverrideRecord> slotRecords, string asset)
    {
        var r = slotRecords.FirstOrDefault(x => x.Asset.Equals(asset, StringComparison.OrdinalIgnoreCase));
        if (r is null || r.StoreId.Length == 0) return null;
        var copy = ContentOverrideService.StoreFile(gamePath, r.StoreId, r.Asset);
        return File.Exists(copy) ? copy : null;
    }

    /// <summary>对象卡片的预览源：按资产序（立绘→行走图→其他）取第一张有图的。</summary>
    public static (string Path, PackThumbService.Kind Kind)? CardThumbSource(string gamePath, TargetGroup t)
    {
        foreach (var asset in t.Assets)
        {
            var src = ThumbSource(gamePath, t.Records, asset);
            if (src is not null) return (src, PackThumbService.KindOf(asset));
        }
        return null;
    }
}
