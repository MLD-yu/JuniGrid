using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JuniGrid.Services;

/// <summary>
/// 物品图鉴清单：把游戏本体的 <c>Content/Data/Objects</c>（id + 内部名）与
/// <c>Content/Strings/Objects.zh-CN</c>（本地化名）拼成「id → 中文名」的完整列表。
///
/// 为什么不在代码里写死一张表：原版光 <c>Data/Objects</c> 就有 700 多件东西，
/// 硬编码的清单永远是残缺的（第一版只写了 70 件，用户第一句话就是"物品不全"）。
/// 两个 xnb 都在游戏目录里、都带 mtime，所以这里<b>运行时现读</b>：
/// 游戏升级加新物品、装汉化补丁改名，图标列表自动跟着变，不用发版。
///
/// 解析方式：XNB 的 <c>Dictionary&lt;string, ObjectData&gt;</c> 值端用的是反射 reader
/// （字段顺序随游戏版本变），完整反序列化要跟着星露谷的字段表一起改，脆得没法维护。
/// 这里改用<b>定点扫描</b>：字典每一项都长这样 ——
/// <c>02 | len | "388" | ?? | 02 | len | "Wood"</c>（02 = StringReader 的 typeId），
/// 只认这个模式，取 id 与内部名；认不出的项直接跳过（最多少几件冷门物品，
/// 绝不会把价格、数量之类的数字误当成 id 混进来）。
///
/// 本地化名的键规则（1.6 实测）：<c>内部名去空格 + "_Name"</c>，例如
/// <c>Prismatic Shard → PrismaticShard_Name</c>、<c>Wood → Wood_Name</c>。
/// </summary>
public static class ItemCatalogService
{
    /// <summary>一件物品。<see cref="Id"/> 同时是物品图鉴表里的格号；
    /// <see cref="Key"/> 是游戏内部的英文名（同名变体去重、排错都靠它）；
    /// <see cref="Type"/> 是 <c>ObjectData.Type</c>（Basic / Litter / Quest …），
    /// 用来把"只能在世界里存在"的条目挡在清单外。</summary>
    public sealed record Item(int Id, string Name, string Key, string Type = "");

    /// <summary>解析规则变了就升这个号，磁盘缓存整份作废。</summary>
    /// <summary>缓存格式版本。解析规则一改就要跟着升，否则老用户读到的是按旧规则算出来的清单
    /// （v4：工具改用 menu 格 + 按族去重 + 补中文名，v3 的缓存里那些错图/英文键会被一直用下去）。</summary>
    private const string CacheVersion = "v4";

    private static readonly object Gate = new();
    private static List<Item>? _items;
    private static string? _itemsKey;
    private static bool _loading;

    /// <summary>全量清单（按 id 升序）。首次调用会解析一遍 xnb（约 100ms），
    /// 之后命中内存/磁盘缓存。解析失败返回空表 —— 调用方回退精选清单。</summary>
    public static IReadOnlyList<Item> Items(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return Array.Empty<Item>();
        var key = KeyOf(gamePath);
        lock (Gate) { if (_items is not null && _itemsKey == key) return _items; }

        var loaded = ReadDisk(key) ?? Parse(gamePath);
        lock (Gate) { _items = loaded; _itemsKey = key; }
        if (loaded.Count > 0) WriteDisk(key, loaded);
        return loaded;
    }

    /// <summary>后台预热：打开图标选择器前先排上，别让第一次点「物品」卡一下。</summary>
    public static void Prewarm(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return;
        lock (Gate) { if (_loading || (_items is not null && _itemsKey == KeyOf(gamePath))) return; _loading = true; }
        _ = Task.Run(() =>
        {
            try { Items(gamePath); }
            catch (Exception ex) { AppLog.Warn("ItemCatalog", ex.Message); }
            finally { lock (Gate) _loading = false; }
        });
    }


    // ───────────────────────── 工具 / 武器 ─────────────────────────

    /// <summary>一件可以用作分类图标的"非物品"条目。<paramref name="Spec"/> 直接就是图标规格。</summary>
    public sealed record IconEntry(string Spec, string Name);

    /// <summary>
    /// 工具清单（<c>Data/Tools</c> + <c>TileSheets/tools</c>）。
    /// <para>工具<b>不在</b> <c>Data/Objects</c> 里，图集也是独立的一张 —— 所以物品页签原来
    /// 一件工具都看不到（用户反馈「为什么物品里面没有工具」）。</para>
    /// <para>解析仍走"定点扫描"而不是完整反序列化：这两张表的值端字段顺序随游戏版本会变。
    /// 只认两样在文件里都<b>按记录顺序</b>出现的东西 —— 名字的本地化 token
    /// （<c>[LocalizedText Strings\Tools:Xxx_Name]</c>）和贴图资源名（<c>TileSheets\tools</c>）——
    /// 按顺序一一配对。工具的 SpriteIndex 是紧跟贴图名之后的<b>第三个 int</b>
    /// （实测 Axe=0、CopperAxe=1、SteelAxe=2… 逐级 +1）。</para></summary>
    public static List<IconEntry> Tools(string gamePath)
    {
        var list = new List<IconEntry>();
        try
        {
            var body = ReadDataBody(gamePath, "Data/Tools");
            if (body is null) return list;
            var loc = ReadLocalizedMap(gamePath, "Strings/Tools");
            var nameKeys = FindLocalizedKeys(body, @"Strings[\\/]Tools:(\w+)_Name\]");
            var textures = FindStringEnds(body, "TileSheets/tools");
            var n = Math.Min(nameKeys.Count, textures.Count);

            // 一条记录在文件里是这样（1.6.19 实测，token 级）：
            //   Key | ClassName | 英文名 | int | int | [LocalizedText ..._Name] | [Description]
            //   | TileSheets\tools | heldIdx | menuIdx | upgradeLevel | (T)上一级 | 0
            //
            // 该画的是 menuIdx（菜单/工具栏那张图）；为 -1 时退回 heldIdx
            // （鱼竿就是这种：held 8/9/10/11，menu -1）。
            //
            // 2026-11-12 修：以前把 **upgradeLevel**（0/1/2/3/4）当格子号用，于是所有「铱级」
            // 工具都画成第 4 格 —— 界面上表现为斧头/镐/锄/喷壶的铱级是同一张图，一堆重复
            // （用户反馈「为什么这么多重复的，而且还不是手里能拿的」）。
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < n; i++)
            {
                var key = nameKeys[i];                                   // Axe / Axe_Copper / FishingRod_Bamboo
                var family = key.Split('_')[0];
                // 垃圾桶在 Data/Tools 里，但它不是拿在手里的工具；
                // 灯笼的 menu 格（74）在 TileSheets/tools 里只有一小块紫色残片，画出来认不出是什么。
                // 这两样放进图标选择器只会让人困惑，直接不列。
                if (family.Equals("TrashCan", StringComparison.OrdinalIgnoreCase)
                    || family.Equals("Lantern", StringComparison.OrdinalIgnoreCase)) continue;
                // 同族只留第一档（斧头 5 个等级在我这里长得几乎一样，全列出来就是「重复」）。
                // 鱼竿例外：每根都不一样，而且都能真的拿到。
                var isRod = family.Equals("FishingRod", StringComparison.OrdinalIgnoreCase);
                if (!isRod && !seen.Add(family)) continue;

                var held = ReadIntAt(body, textures[i]);
                var menu = ReadIntAt(body, textures[i] + 4);
                var sprite = menu is >= 0 ? menu!.Value : held ?? -1;
                if (sprite is < 0 or > 4096) continue;

                var shown = LookupName(loc, key) ?? key;
                list.Add(new IconEntry("tool:" + sprite, shown));
            }
        }
        catch (Exception ex) { AppLog.Warn("ItemCatalog", "工具清单解析失败: " + ex.Message); }
        return list;
    }

    /// <summary>
    /// 武器清单（<c>Data/Weapons</c> + <c>TileSheets/weapons</c>）。
    /// <para>武器同样不在 <c>Data/Objects</c> 里。图集 <c>TileSheets/weapons</c> 的格子号
    /// 就是武器在 <c>Data/Weapons</c> 里的 id（0 起连续），所以按出现顺序编号即可。</para></summary>
    public static List<IconEntry> Weapons(string gamePath)
    {
        var list = new List<IconEntry>();
        try
        {
            var body = ReadDataBody(gamePath, "Data/Weapons");
            if (body is null) return list;
            var loc = ReadLocalizedMap(gamePath, "Strings/Weapons");
            var nameKeys = FindLocalizedKeys(body, @"Strings[\\/]Weapons:(\w+)_Name\]");
            for (var i = 0; i < nameKeys.Count; i++)
            {
                var shown = LookupName(loc, nameKeys[i]) ?? nameKeys[i];
                list.Add(new IconEntry("weapon:" + i, shown));
            }
        }
        catch (Exception ex) { AppLog.Warn("ItemCatalog", "武器清单解析失败: " + ex.Message); }
        return list;
    }

    private static byte[]? ReadDataBody(string gamePath, string asset)
    {
        var p = ContentOverrideService.AssetFullPath(gamePath, asset);
        return File.Exists(p) ? XnbDecoder.ReadContentRegion(File.ReadAllBytes(p)) : null;
    }

    /// <summary>把 <c>Strings/Tools</c> 这类「key → 本地化文本」表读成字典（优先 zh-CN）。</summary>
    private static Dictionary<string, string> ReadLocalizedMap(string gamePath, string asset)
    {
        foreach (var name in new[] { asset + ".zh-CN", asset })
        {
            var p = ContentOverrideService.AssetFullPath(gamePath, name);
            if (!File.Exists(p)) continue;
            var dict = ReadStringDictionary(XnbDecoder.ReadContentRegion(File.ReadAllBytes(p)));
            if (dict.Count > 0) return dict;
        }
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static string? Lookup(Dictionary<string, string> map, string key)
        => map.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    /// <summary>按本地化 key 取显示名。<c>Strings/Tools</c> 与 <c>Strings/Weapons</c> 里的键都带
    /// <c>_Name</c> 后缀（<c>Axe_Name</c>），而 <c>Data/*</c> 里的 token 只给到 <c>Axe</c> ——
    /// 只查一次原名会永远落空，界面上就一直是英文键（用户反馈「这些武器怎么没中文翻译」）。</summary>
    private static string? LookupName(Dictionary<string, string> map, string key)
        => Lookup(map, key) ?? Lookup(map, key + "_Name");

    /// <summary>按出现顺序抓出所有匹配的本地化 key（如 <c>Axe_Copper</c>）。</summary>
    private static List<string> FindLocalizedKeys(byte[] body, string pattern)
    {
        var keys = new List<string>();
        var re = new Regex(pattern);
        foreach (var (_, text) in ScanStrings(body))
        {
            var m = re.Match(text);
            if (m.Success) keys.Add(m.Groups[1].Value);
        }
        return keys;
    }

    /// <summary>按出现顺序抓出所有「等于这个资源名」的字符串，返回它们在 body 里<b>结束</b>的偏移。
    /// 资源名里的路径分隔符两种写法都认（XNB 里混用 <c>\</c> 和 <c>/</c>）。</summary>
    private static List<int> FindStringEnds(byte[] body, string asset)
    {
        var norm = asset.Replace('\\', '/');
        var ends = new List<int>();
        foreach (var (end, text) in ScanStrings(body))
            if (text.Replace('\\', '/').Equals(norm, StringComparison.OrdinalIgnoreCase)) ends.Add(end);
        return ends;
    }

    /// <summary>扫 body 里所有 <c>0x02 + 长度 + UTF8</c> 形态的字符串（XNB 的 StringReader 格式）。
    /// 返回 (字符串结束偏移, 文本)。长度超过 1 字节编码的直接跳过 —— 说明踩错位了。</summary>
    private static IEnumerable<(int End, string Text)> ScanStrings(byte[] body)
    {
        for (var i = 0; i + 2 < body.Length; i++)
        {
            if (body[i] != 0x02) continue;
            var len = body[i + 1];
            if (len == 0 || len > 120) continue;
            var start = i + 2;
            if (start + len > body.Length) continue;
            var ok = true;
            for (var k = 0; k < len; k++)
            {
                var c = body[start + k];
                if (c < 0x20 || c >= 0x7f) { ok = false; break; }
            }
            if (!ok) continue;
            yield return (start + len, Encoding.UTF8.GetString(body, start, len));
        }
    }

    private static int? ReadIntAt(byte[] body, int offset)
        => offset >= 0 && offset + 4 <= body.Length ? BitConverter.ToInt32(body, offset) : null;

    private static List<Item> Parse(string gamePath)
    {
        var list = new List<Item>();
        try
        {
            var objectsPath = ContentOverrideService.AssetFullPath(gamePath, "Data/Objects");
            if (!File.Exists(objectsPath)) return list;
            var pairs = ScanObjectKeys(XnbDecoder.ReadContentRegion(File.ReadAllBytes(objectsPath)));
            if (pairs.Count == 0) return list;

            var names = ReadLocalizedNames(gamePath);
            foreach (var (id, name, type) in pairs)
            {
                // 只留"能捡起来放进背包"的东西 —— 用户的原话。两类要挡掉：
                //  · Litter：地上的石头 / 杂草 / 树枝。捡的时候会化成别的东西（石头 → 石头道具、
                //    杂草 → 纤维），自己从来不进背包，图集里那格也多半是地形贴图；
                //  · asdf：秘密纸条 / 遗失之书 / 神器挖掘点 / 墓碑底座 / 日志残页。都是"世界里的
                //    一次性读物或痕迹"，不是可持有的物品（1.6 给它们打了这个占位类型）。
                if (IsWorldOnly(type)) continue;
                var shown = LocalizedName(names, name, id) ?? name;
                list.Add(new Item(id, shown, name, type));
            }
            list.Sort((a, b) => a.Id.CompareTo(b.Id));
        }
        catch (Exception ex) { AppLog.Warn("ItemCatalog", "解析失败: " + ex.Message); }
        return list;
    }

    /// <summary>两类"进不了背包"的条目（见 Parse 里的说明）。</summary>
    private static bool IsWorldOnly(string type)
        => type.Equals("Litter", StringComparison.OrdinalIgnoreCase)
        || type.Equals("asdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>扫 <c>Dictionary&lt;string, ObjectData&gt;</c>：取 id、Name 与 Type。
    /// ObjectData 的头几个字段固定是 Name / DisplayName / Description / Type 四个字符串，
    /// 后面才是价格、贴图那些 —— 只读这四段，别的一概不碰（字段表随版本变，读多了就脆）。</summary>
    private static List<(int Id, string Name, string Type)> ScanObjectKeys(byte[]? body)
    {
        var result = new List<(int, string, string)>();
        if (body is null) return result;
        var seen = new HashSet<int>();
        for (var i = 0; i + 6 < body.Length; i++)
        {
            if (body[i] != 0x02) continue;              // StringReader 的 typeId
            var len = body[i + 1];
            if (len is 0 or > 4) continue;              // 物品 id 最长 4 位
            if (i + 2 + len + 3 >= body.Length) continue;

            var digits = 0;
            for (var k = 0; k < len; k++)
            {
                var c = body[i + 2 + k];
                if (c is < (byte)'0' or > (byte)'9') { digits = -1; break; }
                digits = digits * 10 + (c - '0');
            }
            if (digits < 0) continue;

            // 键后面紧跟 1 个字节就是 ObjectData 的 reader 索引，再接 Name 字段的 "02 + 长度 + 内容"。
            // 只认这 1 个字节的位移：放宽成"最多隔 3 字节"会让后面的 DisplayName / Description
            // 甚至下一项的键顶上来，把别的物品名安到这件物品头上（实测 id 30 被叫成木材）。
            var r = i + 2 + len + 1;
            if (!TryReadAscii(body, ref r, out var name)) continue;
            if (name.Length < 2 || !Regex.IsMatch(name, @"^[A-Za-z][A-Za-z0-9 _'\-\.]*$")) continue;
            // 跳过 DisplayName、Description，第四段才是 Type
            if (!TryReadAscii(body, ref r, out _)) continue;
            if (!TryReadAscii(body, ref r, out _)) continue;
            if (!TryReadAscii(body, ref r, out var type)) continue;

            if (seen.Add(digits)) result.Add((digits, name, type));
        }
        return result;
    }

    /// <summary>读一段 XNB 字符串（0x02 + 7bit 长度 + UTF8）。这几段字段都很短，
    /// 长度超过 1 字节编码的直接放弃 —— 说明已经踩错位了。</summary>
    private static bool TryReadAscii(byte[] body, ref int pos, out string text)
    {
        text = "";
        if (pos + 2 > body.Length || body[pos] != 0x02) return false;
        var len = body[pos + 1];
        if (len > 127) return false;
        pos += 2;
        if (pos + len > body.Length) return false;
        text = Encoding.UTF8.GetString(body, pos, len);
        pos += len;
        return true;
    }

    /// <summary>读 <c>Content/Strings/Objects[.zh-CN]</c> 的平铺字符串字典。
    /// 中文版缺失（或键不够）时用英文版兜底。</summary>
    private static Dictionary<string, string> ReadLocalizedNames(string gamePath)
    {
        foreach (var name in new[] { "Strings/Objects.zh-CN", "Strings/Objects" })
        {
            var path = ContentOverrideService.AssetFullPath(gamePath, name);
            if (!File.Exists(path)) continue;
            var dict = ReadStringDictionary(XnbDecoder.ReadContentRegion(File.ReadAllBytes(path)));
            if (dict.Count > 0) return dict;
        }
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ReadStringDictionary(byte[]? body)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (body is null) return dict;
        try
        {
            var pos = 0;
            var readerCount = Read7Bit(body, ref pos);
            for (var i = 0; i < readerCount && i < 64; i++)
            {
                _ = ReadString(body, ref pos);
                pos += 4;                                 // 每个 reader 后面跟一个版本号 int32
            }
            _ = Read7Bit(body, ref pos);                  // shared resource count
            _ = Read7Bit(body, ref pos);                  // 根对象的 reader 索引
            var count = BitConverter.ToInt32(body, pos);
            pos += 4;
            if (count is <= 0 or > 100_000) return dict;
            for (var i = 0; i < count; i++)
            {
                pos += 1;                                 // key 的 typeId
                var k = ReadString(body, ref pos);
                pos += 1;                                 // value 的 typeId
                var v = ReadString(body, ref pos);
                if (v.Length > 0) dict[k] = v;
            }
        }
        catch (Exception ex) { AppLog.Warn("ItemCatalog", "读字符串表失败: " + ex.Message); }
        return dict;
    }

    /// <summary>内部名 → 本地化名。键的三种可能写法都试一遍（游戏各版本略有差异）。</summary>
    private static string? LocalizedName(Dictionary<string, string> dict, string name, int id)
    {
        if (dict.Count == 0) return null;
        var compact = name.Replace(" ", "").Replace("-", "");
        foreach (var key in new[] { name + "_Name", compact + "_Name", id + "_Name" })
            if (dict.TryGetValue(key, out var v) && v.Length > 0) return v;
        return null;
    }

    private static int Read7Bit(byte[] data, ref int pos)
    {
        int result = 0, shift = 0;
        while (true)
        {
            var b = data[pos++];
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift > 28) throw new InvalidDataException("7bit 溢出");
        }
    }

    private static string ReadString(byte[] data, ref int pos)
    {
        var len = Read7Bit(data, ref pos);
        if (len < 0 || pos + len > data.Length) throw new InvalidDataException("字符串越界");
        var s = Encoding.UTF8.GetString(data, pos, len);
        pos += len;
        return s;
    }

    // ───────────────────────── 缓存 ─────────────────────────

    private static string? _cachePath;

    private static string CachePath => _cachePath ??= StoragePaths.InCache("item-catalog.json");

    /// <summary>缓存键：两个 xnb 的 mtime+长度 —— 游戏一升级、汉化一改就自动重解析。</summary>
    private static string KeyOf(string gamePath)
    {
        static string Stat(string path)
        {
            try { var fi = new FileInfo(path); return fi.Exists ? $"{fi.LastWriteTimeUtc:yyyyMMddHHmmss}-{fi.Length}" : "none"; }
            catch { return "err"; }
        }
        var objects = ContentOverrideService.AssetFullPath(gamePath, "Data/Objects");
        var strings = ContentOverrideService.AssetFullPath(gamePath, "Strings/Objects.zh-CN");
        return $"{CacheVersion}|{Stat(objects)}|{Stat(strings)}";
    }

    private sealed class CacheFile
    {
        public string Key { get; set; } = "";
        public List<ItemDto> Items { get; set; } = new();
    }

    private sealed class ItemDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Key { get; set; } = "";
        public string Type { get; set; } = "";
    }

    private static List<Item>? ReadDisk(string key)
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            var data = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(CachePath));
            if (data is null || data.Key != key || data.Items.Count == 0) return null;
            return data.Items.Select(i => new Item(i.Id, i.Name, i.Key, i.Type)).ToList();
        }
        catch { return null; }
    }

    private static void WriteDisk(string key, List<Item> items)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            var data = new CacheFile
            {
                Key = key,
                Items = items.Select(i => new ItemDto { Id = i.Id, Name = i.Name, Key = i.Key, Type = i.Type }).ToList(),
            };
            File.WriteAllText(CachePath, JsonSerializer.Serialize(data));
        }
        catch (Exception ex) { AppLog.Warn("ItemCatalog", "写缓存失败: " + ex.Message); }
    }
}
