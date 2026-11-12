using System.IO;

namespace JuniGrid.Services;

/// <summary>
/// 分类图标：把「一个分类该画哪张图」翻译成一张能直接进 &lt;img&gt; 的 URL。
///
/// 图标全部取自<b>游戏本体素材</b>（不联网、不打包图片）：
///  · <c>chicken</c> —— 默认图标：白鸡第一帧（<c>Content/Animals/White Chicken</c>），
///    也就是 Steam 库里星露谷那只鸡；方图，22px 的框里一眼能认出来；
///  · <c>portrait:Abigail</c> —— 角色大头照（<c>Content/Portraits/Abigail</c>）；
///  · <c>item:72</c> —— 物品图鉴里的一格（<c>Content/Maps/springobjects</c> 第 72 格）。
///
/// 解码 / 裁剪 / 缓存 / 虚拟主机全走 <see cref="PackThumbService"/> —— 它已经在素材包页做过
/// 同一件事（XNB → PNG、磁盘缓存、短 URL 通道），这里只负责"取哪张表、取哪一格"。
/// 生成的图是<b>满幅</b>的（不补白边），显示端用 <c>object-fit: contain</c> 装进方框，
/// 所以宽高比不同的图（物品是正方形、立绘是 64×64）都不会被切掉一块。
/// </summary>
public static class CategoryIconService
{
    /// <summary>没设置过图标时的默认值：白鸡（= Steam 库图标那只）。</summary>
    public const string DefaultSpec = "chicken";

    /// <summary>用户自己上传的图片：规格是 <c>custom:&lt;id&gt;</c>，id 是 8 位十六进制。
    /// 文件存在 <see cref="StoragePaths.CustomIconDir"/>，短 URL 走独立虚拟主机
    /// （<see cref="CustomHost"/>，见 MainWindow.OnThumbWebResourceRequested）。</summary>
    public const string CustomPrefix = "custom:";
    public const string CustomHost = "https://junigrid-icons";
    /// <summary>自制图标的 PNG 尺寸。显示框只有 22~36px，96 已经留了高分屏余量。</summary>
    public const int CustomSize = 96;
    private const string CustomNameIndex = "custom-icons.json";

    /// <summary>原版物品图鉴表（24 列 × 16px）。</summary>
    private const string ObjectsAsset = "Maps/springobjects";
    /// <summary>工具与武器各有自己的图集和数据表，跟物品不是同一张（见 ItemCatalogService.Tools/Weapons）。</summary>
    private const string ToolsAsset = "TileSheets/tools";
    private const string WeaponsAsset = "TileSheets/weapons";
    /// <summary>动物行走图：16×16 一格、每行 4 格。第一格就是站着的那张。</summary>
    private const string ChickenAsset = "Animals/White Chicken";

    /// <summary>图标规格是否可用（空 = 用默认的白鸡）。</summary>
    public static string NormalizeSpec(string? spec)
        => string.IsNullOrWhiteSpace(spec) ? DefaultSpec : spec.Trim();

    /// <summary>规格 → 图片 URL。素材还没生成好时返回 null（已排入后台，完成会触发
    /// <see cref="PackThumbService.Changed"/>，页面收到后重画一次就有了）。</summary>
    public static string? Url(string? spec, string gamePath)
    {
        var s = NormalizeSpec(spec);

        // 自制图标跟游戏目录无关（是用户上传的 PNG），所以放在 gamePath 判空之前 ——
        // 换了机器/还没设游戏目录时，自制图标照样要能显示出来。
        if (IsCustom(s)) return CustomUrl(s[CustomPrefix.Length..]);

        if (string.IsNullOrWhiteSpace(gamePath)) return null;

        if (s.Equals(DefaultSpec, StringComparison.OrdinalIgnoreCase)) return ChickenUrl(gamePath);

        if (s.StartsWith("portrait:", StringComparison.OrdinalIgnoreCase))
        {
            var name = s["portrait:".Length..].Trim();
            if (name.Length == 0) return ChickenUrl(gamePath);
            var path = ContentOverrideService.AssetFullPath(gamePath, "Portraits/" + name);
            return File.Exists(path) ? PackThumbService.Get(path, PackThumbService.Kind.Portrait) : ChickenUrl(gamePath);
        }

        if (s.StartsWith("item:", StringComparison.OrdinalIgnoreCase))
        {
            // 64px 输出对 22px 的显示框已经绰绰有余（还留了高分屏的余量），
            // 再大只是让缓存目录里多几 MB 没用的像素
            if (!int.TryParse(s["item:".Length..].Trim(), out var id) || id < 0) return ChickenUrl(gamePath);
            var sheet = ContentOverrideService.AssetFullPath(gamePath, ObjectsAsset);
            return File.Exists(sheet) ? PackThumbService.GetTile(sheet, id, 64) : ChickenUrl(gamePath);
        }

        // 工具 / 武器：格子号就是数据表里那条记录的 SpriteIndex / id，
        // 列数按图集实际宽度算（这两张图比物品图窄，写死 24 会错位）。
        if (s.StartsWith("tool:", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("weapon:", StringComparison.OrdinalIgnoreCase))
        {
            var isTool = s.StartsWith("tool:", StringComparison.OrdinalIgnoreCase);
            var rest = isTool ? s["tool:".Length..].Trim() : s["weapon:".Length..].Trim();
            if (!int.TryParse(rest, out var idx) || idx < 0) return ChickenUrl(gamePath);
            var sheet = ContentOverrideService.AssetFullPath(gamePath, isTool ? ToolsAsset : WeaponsAsset);
            if (!File.Exists(sheet)) return ChickenUrl(gamePath);
            return PackThumbService.GetFrame(sheet, idx, PackThumbService.SheetCols(sheet), cell: 16, outSize: 64);
        }

        if (s.StartsWith("animal:", StringComparison.OrdinalIgnoreCase))
        {
            var name = s["animal:".Length..].Trim();
            if (name.Length == 0 || name.Contains('/') || name.Contains('\\')) return ChickenUrl(gamePath);
            var path = ContentOverrideService.AssetFullPath(gamePath, "Animals/" + name);
            return File.Exists(path)
                ? PackThumbService.GetFrame(path, 0, cols: 4, cell: 16, outSize: 64)
                : ChickenUrl(gamePath);
        }

        return ChickenUrl(gamePath);
    }

    // ───────────────────────── 自定义图片（用户上传） ─────────────────────────

    /// <summary>这个规格是不是「用户上传的图」。</summary>
    public static bool IsCustom(string? spec)
        => spec is not null && spec.StartsWith(CustomPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>id 只允许十六进制 —— 它直接变成文件名，不校验就能用 <c>../</c> 写到别的目录去。</summary>
    public static bool IsValidCustomId(string? id)
    {
        if (id is null || id.Length is < 6 or > 32) return false;
        foreach (var c in id)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    /// <summary>自制图标的落盘路径（id 非法时 null）。</summary>
    public static string? CustomFile(string id)
        => IsValidCustomId(id) ? Path.Combine(StoragePaths.CustomIconDir, "jgc-" + id.ToLowerInvariant() + ".png") : null;

    /// <summary>自制图标 → 可进 &lt;img&gt; 的 URL。文件不在了返回 null（调用方会退回白鸡）。</summary>
    public static string? CustomUrl(string id)
    {
        var path = CustomFile(id);
        if (path is null || !File.Exists(path)) return null;
        // 短 URL 通道没挂上（单元测试 / 早期启动）时退回 data URI，跟 PackThumbService 一个策略
        if (PackThumbService.ThumbHostBase is not null) return CustomHost + "/" + Path.GetFileName(path);
        try { return "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(path)); }
        catch { return null; }
    }

    /// <summary>存一张自制图标。返回值就是写进配置的规格（<c>custom:&lt;id&gt;</c>）；
    /// 内容不是 PNG 或写盘失败时返回 null。</summary>
    public static string? SaveCustom(byte[] png, string displayName)
    {
        if (png is null || png.Length < 8) return null;
        // PNG 魔数：8 字节。前端给的是 canvas.toDataURL，正常就是 PNG；这里挡一下
        // 免得半截数据/别的格式躺进图标目录（那目录是直接喂给 &lt;img&gt; 的）
        ReadOnlySpan<byte> sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (!png.AsSpan(0, 8).SequenceEqual(sig)) return null;
        if (png.Length > 4 * 1024 * 1024) return null;

        var id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        var path = CustomFile(id);
        if (path is null) return null;
        try
        {
            Directory.CreateDirectory(StoragePaths.CustomIconDir);
            File.WriteAllBytes(path, png);
        }
        catch { return null; }

        // 名字只用于悬浮提示（文件本身是哈希名）。存一份索引，读不到就当「自定义图片」
        try
        {
            var index = LoadCustomIndex();
            index.RemoveAll(x => x.Id == id);
            index.Add(new CustomIndexEntry(id, Trim(displayName), DateTime.UtcNow));
            WriteCustomIndex(index);
        }
        catch { /* 名字存不下不影响图标本身可用 */ }

        return CustomPrefix + id;
    }

    /// <summary>删掉一张自制图标（配置里还引用着的由调用方自己清）。</summary>
    public static void DeleteCustom(string spec)
    {
        if (!IsCustom(spec)) return;
        var id = spec[CustomPrefix.Length..];
        var path = CustomFile(id);
        try { if (path is not null && File.Exists(path)) File.Delete(path); } catch { }
        try
        {
            var index = LoadCustomIndex();
            index.RemoveAll(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            WriteCustomIndex(index);
        }
        catch { }
    }

    /// <summary>已上传的自制图标，最近上传的排最前。</summary>
    public static List<IconOption> CustomIcons()
    {
        var list = new List<IconOption>();
        try
        {
            var dir = StoragePaths.CustomIconDir;
            if (!Directory.Exists(dir)) return list;
            var index = LoadCustomIndex();
            var rows = new List<(DateTime At, IconOption Opt)>();
            foreach (var f in Directory.EnumerateFiles(dir, "jgc-*.png"))
            {
                var name = Path.GetFileNameWithoutExtension(f);   // jgc-<id>
                if (name.Length <= 4) continue;
                var id = name[4..];
                if (!IsValidCustomId(id)) continue;
                var entry = index.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                var at = entry?.At ?? File.GetLastWriteTimeUtc(f);
                rows.Add((at, new IconOption(CustomPrefix + id,
                    string.IsNullOrWhiteSpace(entry?.Name) ? "自定义图片" : entry!.Name!,
                    "自定义")));
            }
            // 最近传的排最前（id 是随机的，按 id 排等于乱序）
            rows.Sort((a, b) => b.At.CompareTo(a.At));
            list.AddRange(rows.Select(r => r.Opt));
        }
        catch { }
        return list;
    }

    private sealed class CustomIndexEntry
    {
        public string Id { get; set; } = "";
        public string? Name { get; set; }
        public DateTime At { get; set; }
        public CustomIndexEntry() { }
        public CustomIndexEntry(string id, string name, DateTime at) { Id = id; Name = name; At = at; }
    }

    private static List<CustomIndexEntry> LoadCustomIndex()
    {
        try
        {
            var p = Path.Combine(StoragePaths.CustomIconDir, CustomNameIndex);
            if (!File.Exists(p)) return [];
            return System.Text.Json.JsonSerializer.Deserialize<List<CustomIndexEntry>>(File.ReadAllText(p)) ?? [];
        }
        catch { return []; }
    }

    private static void WriteCustomIndex(List<CustomIndexEntry> list)
    {
        var p = Path.Combine(StoragePaths.CustomIconDir, CustomNameIndex);
        Directory.CreateDirectory(StoragePaths.CustomIconDir);
        File.WriteAllText(p, System.Text.Json.JsonSerializer.Serialize(list));
    }

    private static string Trim(string? s)
    {
        var t = (s ?? "").Trim();
        if (t.Length == 0) return "自定义图片";
        return t.Length > 40 ? t[..40] : t;
    }

    private static string? ChickenUrl(string gamePath)
    {
        var path = ContentOverrideService.AssetFullPath(gamePath, ChickenAsset);
        return File.Exists(path)
            ? PackThumbService.GetFrame(path, 0, cols: 4, cell: 16, outSize: 64)
            : null;
    }

    // ───────────────────────── 选择器数据 ─────────────────────────

    /// <summary>一个可选项。<see cref="Spec"/> 就是要写进配置的值。</summary>
    public sealed record IconOption(string Spec, string Name, string Group);

    /// <summary>立绘表里有、但**不是村里人**的那几张：答录机（台机器）、熊、小鸟（动物）、
    /// 保镖、菲兹（节日演出里的临时角色）。它们当分类图标既认不出也用不上，
    /// 2026-11-09 按用户要求从清单里剔除 —— 素材还在，只是不再列出来。</summary>
    private static readonly HashSet<string> HiddenPortraits = new(StringComparer.OrdinalIgnoreCase)
    {
        "AnsweringMachine", "Bear", "Birdie", "Bouncer", "Fizz",
    };

    /// <summary>角色大头照（读游戏 <c>Content/Portraits</c> 目录）。同一个人只留默认形象 ——
    /// <c>Abigail_Beach</c> / <c>Abigail_Winter</c> 是同一张脸的另一套衣服，列表里出现三次
    /// 只会让人找不到。基名不存在的才单列。</summary>
    public static List<IconOption> Portraits(string gamePath)
    {
        var list = new List<IconOption>();
        try
        {
            var dir = Path.Combine(ContentOverrideService.ContentRoot(gamePath), "Portraits");
            if (!Directory.Exists(dir)) return list;
            var names = Directory.EnumerateFiles(dir, "*.xnb")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();
            var set = new HashSet<string>(names!, StringComparer.OrdinalIgnoreCase);
            foreach (var n in names!.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (HiddenPortraits.Contains(n)) continue;         // 非村民（道具/动物/演出角色）
                var cut = n.IndexOf('_');
                if (cut > 0 && set.Contains(n[..cut])) continue;   // 变体（_Beach / _Winter / _FlowerDance…）
                list.Add(new IconOption("portrait:" + n, PortraitName(n), "角色"));
            }
        }
        catch (Exception ex) { AppLog.Warn("CatIcon", "读 Portraits 失败: " + ex.Message); }
        return list;
    }

    /// <summary>常见角色的中文名（认图之外还给个能读的字）。没登记的原样显示英文名。</summary>
    private static string PortraitName(string name) => name switch
    {
        "Abigail" => "阿比盖尔",
        "Alex" => "亚历克斯",
        "Caroline" => "卡洛琳",
        "Clint" => "克林特",
        "Demetrius" => "德米特里",
        "Dwarf" => "矮人",
        "Elliott" => "艾利欧特",
        "Emily" => "艾米丽",
        "Evelyn" => "艾芙琳",
        "George" => "乔治",
        "Gil" => "吉尔",
        "Gunther" => "冈瑟",
        "Gus" => "古斯",
        "Haley" => "海莉",
        "Harvey" => "哈维",
        "Jas" => "贾斯",
        "Jodi" => "乔迪",
        "Kent" => "肯特",
        "Krobus" => "科罗布斯",
        "Leah" => "莉亚",
        "Leo" => "雷欧",
        "Lewis" => "刘易斯",
        "Linus" => "莱纳斯",
        "Marlon" => "马龙",
        "Marnie" => "玛妮",
        "Maru" => "玛鲁",
        "Morris" => "莫里斯",
        "Pam" => "潘姆",
        "Penny" => "潘妮",
        "Pierre" => "皮埃尔",
        "Robin" => "罗宾",
        "Sam" => "山姆",
        "Sandy" => "桑迪",
        "Sebastian" => "塞巴斯蒂安",
        "Shane" => "谢恩",
        "Vincent" => "文森特",
        "Willy" => "威利",
        "Wizard" => "法师",
        // 特殊立绘（过场/事件里才出现的那些）—— 名字来自文件名，这里给个能读的说法：
        // 不翻的话列表里会冒出一排 "AnsweringMachine / SafariGuy"，看着像素材抓错了。
        "AnsweringMachine" => "答录机",
        "Bear" => "熊",
        "Birdie" => "小鸟",
        "Bouncer" => "保镖",
        "Fizz" => "菲兹",
        "Governor" => "州长",
        "Grandpa" => "爷爷",
        "Henchman" => "手下",
        "MrQi" => "齐先生",
        "ParrotBoy" => "雷欧",
        "SafariGuy" => "探险家",
        _ => name,
    };

    /// <summary>物品清单：<b>全量</b>取自游戏本体的 <c>Data/Objects</c> + <c>Strings/Objects</c>
    /// （700 多件，含本地化名），见 <see cref="ItemCatalogService"/>。
    /// 游戏目录没配好或解析失败时退回 <see cref="FallbackItems"/> 那一小撮常用物品。</summary>
    public static List<IconOption> Items(string gamePath)
    {
        var list = new List<IconOption>();
        // 工具、武器先摆最前面。它们在 Data/Objects 之外（各有独立的数据表和图集），
        // 而且各自只有几十件 —— 跟在 700 多件物品后面会被「首批 140 件」的分批截断挡住，
        // 用户滚很久都翻不到，等于没有（用户反馈「物品里面没有工具」）。
        if (!string.IsNullOrWhiteSpace(gamePath))
        {
            foreach (var e in ItemCatalogService.Tools(gamePath))
                list.Add(new IconOption(e.Spec, e.Name, "工具"));
            foreach (var e in ItemCatalogService.Weapons(gamePath))
                list.Add(new IconOption(e.Spec, e.Name, "武器"));
        }
        // 同名变体只留 id 最小的那件：原版光是叫「Stone」的 id 就有 48 个
        // （矿洞各层石头的不同配色），全铺出来能把前几屏刷满，还挑不出区别。
        // 按<b>内部英文名</b>去重而不是中文名 —— "Large Egg / Large Brown Egg" 汉化后
        // 都叫「大蛋」，但它们确实是两件东西，不能合。
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sheet = ContentOverrideService.AssetFullPath(gamePath, ObjectsAsset);
        foreach (var it in ItemCatalogService.Items(gamePath))
        {
            // 图集里全透明的那几格（1.6 遗留的废弃条目）不列出来 —— 选了也是空白；
            // 灰色虚线空框那种占位图同理，看着像图、其实什么都不是
            if (!PackThumbService.HasVisiblePixels(sheet, it.Id)) continue;
            if (PackThumbService.IsPlaceholderCell(sheet, it.Id)) continue;
            if (!seen.Add(it.Key)) continue;
            list.Add(new IconOption("item:" + it.Id, it.Name, "物品"));
        }
        return list.Count > 0 ? list : FallbackItems.ToList();
    }

    /// <summary>兜底清单（游戏目录不可用时的常用物品）。</summary>
    private static readonly IReadOnlyList<IconOption> FallbackItems = new List<IconOption>
    {
        // 基础材料
        new("item:388", "木材", "材料"),
        new("item:390", "石头", "材料"),
        new("item:771", "纤维", "材料"),
        new("item:709", "硬木", "材料"),
        new("item:92", "树液", "材料"),
        new("item:380", "铁矿石", "材料"),
        new("item:382", "煤炭", "材料"),
        new("item:338", "精炼石英", "材料"),
        new("item:428", "布料", "材料"),
        new("item:766", "史莱姆", "材料"),
        new("item:767", "蝙蝠翅膀", "材料"),
        new("item:768", "太阳精华", "材料"),
        new("item:769", "虚空精华", "材料"),
        new("item:684", "虫肉", "材料"),
        new("item:178", "干草", "材料"),
        new("item:430", "松露", "材料"),

        // 矿石与金属锭
        new("item:378", "铜矿石", "矿石"),
        new("item:384", "金矿石", "矿石"),
        new("item:386", "铱矿石", "矿石"),
        new("item:334", "铜锭", "矿石"),
        new("item:335", "铁锭", "矿石"),
        new("item:336", "金锭", "矿石"),
        new("item:337", "铱锭", "矿石"),

        // 宝石与矿物
        new("item:60", "绿宝石", "宝石"),
        new("item:62", "海蓝宝石", "宝石"),
        new("item:64", "红宝石", "宝石"),
        new("item:66", "紫水晶", "宝石"),
        new("item:68", "黄玉", "宝石"),
        new("item:70", "翡翠", "宝石"),
        new("item:72", "钻石", "宝石"),
        new("item:74", "五彩碎片", "宝石"),
        new("item:80", "石英", "宝石"),
        new("item:82", "火石英", "宝石"),
        new("item:84", "冰霜之泪", "宝石"),
        new("item:86", "地晶", "宝石"),

        // 牧场
        new("item:176", "蛋", "牧场"),
        new("item:180", "棕色蛋", "牧场"),
        new("item:182", "大蛋", "牧场"),
        new("item:184", "牛奶", "牧场"),
        new("item:186", "大牛奶", "牧场"),
        new("item:436", "羊奶", "牧场"),
        new("item:438", "大山羊奶", "牧场"),

        // 工匠品
        new("item:424", "奶酪", "工匠品"),
        new("item:426", "山羊奶酪", "工匠品"),
        new("item:306", "蛋黄酱", "工匠品"),
        new("item:307", "鸭蛋黄酱", "工匠品"),
        new("item:346", "啤酒", "工匠品"),
        new("item:303", "淡啤酒", "工匠品"),
        new("item:348", "果酒", "工匠品"),
        new("item:340", "蜂蜜", "工匠品"),
        new("item:459", "蜂蜜酒", "工匠品"),

        // 觅食
        new("item:16", "野山葵", "觅食"),
        new("item:18", "水仙", "觅食"),
        new("item:20", "韭葱", "觅食"),
        new("item:22", "蒲公英", "觅食"),
        new("item:396", "香料浆果", "觅食"),
        new("item:398", "葡萄", "觅食"),
        new("item:402", "甜豌豆", "觅食"),
        new("item:404", "野李", "觅食"),
        new("item:406", "榛子", "觅食"),
        new("item:408", "黑莓", "觅食"),
        new("item:410", "冬根", "觅食"),
        new("item:412", "水晶果", "觅食"),
        new("item:414", "雪山药", "觅食"),
        new("item:420", "红蘑菇", "觅食"),
        new("item:422", "紫蘑菇", "觅食"),

        // 特别
        new("item:434", "星之果实", "特别"),
        new("item:279", "魔法糖冰棍", "特别"),
        new("item:773", "生命药水", "特别"),
        new("item:349", "能量滋补水", "特别"),
    };
}
