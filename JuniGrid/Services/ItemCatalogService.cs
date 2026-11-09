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
    private const string CacheVersion = "v3";

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

    // ───────────────────────── 解析 ─────────────────────────

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
