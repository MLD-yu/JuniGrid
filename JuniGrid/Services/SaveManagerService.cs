using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace JuniGrid.Services;

/// <summary>
/// 农夫的「长什么样」。全部来自存档里的 SaveGameInfo，渲染交给 <see cref="FarmerAvatarService"/>。
/// <para>1.6 起衬衫/裤子/帽子的样式与染色在 shirtItem/pantsItem/hat 物件里
/// （旧版裸 <c>shirt</c>/<c>pants</c> 整数恒为 -1），这里解析成显示值，老存档走旧字段退路。</para>
/// </summary>
public sealed record SaveLook(
    bool Male,
    int Skin,
    int Hair,
    int Shoes,
    int ShirtIndex,
    string? ShirtId,
    (int R, int G, int B) ShirtColor,
    bool ShirtPrismatic,
    int PantsIndex,
    (int R, int G, int B) PantsColor,
    bool PantsPrismatic,
    (int R, int G, int B) HairColor,
    bool HairPrismatic,
    (int R, int G, int B) EyeColor,
    int Accessory,
    int HatIndex,
    int HatHairDrawType,
    bool HatIgnoreHairOffset,
    bool HatPrismatic);

/// <summary>首页「存档管理」一行。金额/时长都是存档自己记的，不另算。</summary>
public sealed record SaveEntry(
    string DirName,
    string Dir,
    string? FarmerName,
    string? FarmName,
    string? GameVersion,
    long SizeBytes,
    DateTime WrittenAt,
    TimeSpan Played,
    long Money,
    bool MetaComplete,
    SaveLook? Look);

/// <summary>
/// 首页存档管理：扫描 <c>%APPDATA%\StardewValley\Saves</c>、导入 / 导出单份档。
/// <para>和 <see cref="SaveVersionService"/> 的分工：那边管「版本闸门 / 收档放回」，
/// 是启动链路上的安全设施；这边只管「给人看 + 搬进搬出」，不参与任何启动判定 ——
/// 首页出问题绝不能影响进游戏。</para>
/// </summary>
public static class SaveManagerService
{
    private static readonly Regex ColorBlock = new(
        @"<(?<tag>hairstyleColor|pantsColor|shirtColor|newEyeColor)>\s*<B>(?<b>\d+)</B>\s*<G>(?<g>\d+)</G>\s*<R>(?<r>\d+)</R>",
        RegexOptions.Compiled);

    private static readonly Regex SimpleTag = new(
        @"<(?<tag>name|farmName|gameVersion|millisecondsPlayed|money|Gender|skin|hair|shoes|shirt|pants|accessory|prismaticHair)>([^<]*)",
        RegexOptions.Compiled);

    private static readonly Regex ItemBlock = new(
        @"<(?<tag>shirtItem|pantsItem|hat)>(?<body>.*?)</\k<tag>>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>扫全部存档，按「最后写入时间」从新到旧。目录不存在就是空列表。</summary>
    public static List<SaveEntry> Scan()
    {
        var list = new List<SaveEntry>();
        var root = SaveVersionService.SavesDir();
        if (root is null) return list;
        string[] dirs;
        try { dirs = Directory.GetDirectories(root); } catch { return list; }
        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            // 本家拷贝残壳（*-incomplete-<时间戳>）不算一份档：只有索引件，点进去必闪退
            if (SaveVersionService.IsIncompleteShell(name)) continue;
            var e = Read(dir);
            if (e is not null) list.Add(e);
        }
        list.Sort((a, b) => b.WrittenAt.CompareTo(a.WrittenAt));
        return list;
    }

    /// <summary>读一份存档。空目录（Steam 云留下的壳）返回 null，与版本闸门同口径。</summary>
    public static SaveEntry? Read(string dir)
    {
        long bytes = 0;
        var newest = DateTime.MinValue;
        var any = false;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var fi = new FileInfo(f);
                bytes += fi.Length;
                any = true;
                if (fi.LastWriteTime > newest) newest = fi.LastWriteTime;
            }
        }
        catch { return null; }
        if (!any) return null;

        string txt = "";
        var metaOk = false;
        try
        {
            var info = Path.Combine(dir, "SaveGameInfo");
            if (File.Exists(info)) { txt = File.ReadAllText(info); metaOk = true; }
        }
        catch { /* 读不出来就只显示目录名和大小 */ }

        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        if (txt.Length > 0)
            foreach (Match m in SimpleTag.Matches(txt))
                tags.TryAdd(m.Groups["tag"].Value, m.Groups[1].Value.Trim());

        var colors = new Dictionary<string, (int R, int G, int B)>(StringComparer.Ordinal);
        if (txt.Length > 0)
            foreach (Match m in ColorBlock.Matches(txt))
                colors.TryAdd(m.Groups["tag"].Value, (Num(m.Groups["r"].Value), Num(m.Groups["g"].Value), Num(m.Groups["b"].Value)));

        var male = !tags.TryGetValue("Gender", out var g)
                   || !(g.Equals("Female", StringComparison.OrdinalIgnoreCase) || g == "1");

        long played = 0;
        if (tags.TryGetValue("millisecondsPlayed", out var ms)) long.TryParse(ms, out played);
        long money = 0;
        if (tags.TryGetValue("money", out var mo)) long.TryParse(mo, out money);

        var items = new Dictionary<string, string>(StringComparer.Ordinal);
        if (txt.Length > 0)
            foreach (Match m in ItemBlock.Matches(txt))
                items.TryAdd(m.Groups["tag"].Value, m.Groups["body"].Value);

        var shirtItem = ItemOf(items, "shirtItem");
        var pantsItem = ItemOf(items, "pantsItem");
        var hatItem = ItemOf(items, "hat");

        var legacyShirt = IntOf(tags, "shirt", -1);
        var legacyPants = IntOf(tags, "pants", -1);

        var look = new SaveLook(
            male,
            IntOf(tags, "skin", 0),
            IntOf(tags, "hair", 0),
            IntOf(tags, "shoes", 12),
            shirtItem?.Index ?? (legacyShirt >= 0 ? legacyShirt : male ? 209 : 41),
            shirtItem?.Id ?? (legacyShirt >= 0 ? (1000 + legacyShirt).ToString() : null),
            shirtItem?.Color ?? (colors.TryGetValue("shirtColor", out var sc) ? sc : (255, 255, 255)),
            shirtItem?.Prismatic ?? false,
            pantsItem?.Index ?? (legacyPants >= 0 ? legacyPants : 14),
            pantsItem?.Color ?? (colors.TryGetValue("pantsColor", out var pc) ? pc : (255, 255, 255)),
            pantsItem?.Prismatic ?? false,
            colors.TryGetValue("hairstyleColor", out var hc) ? hc : (90, 60, 40),
            tags.TryGetValue("prismaticHair", out var ph) && ph == "true",
            colors.TryGetValue("newEyeColor", out var ec) ? ec : (70, 70, 90),
            IntOf(tags, "accessory", -1),
            hatItem?.Index ?? -1,
            hatItem?.HairDrawType ?? 0,
            hatItem?.IgnoreHairOffset ?? false,
            hatItem?.Prismatic ?? false);

        tags.TryGetValue("name", out var farmer);
        tags.TryGetValue("farmName", out var farm);
        tags.TryGetValue("gameVersion", out var ver);

        return new SaveEntry(
            Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, '/')),
            dir,
            string.IsNullOrWhiteSpace(farmer) ? null : farmer,
            string.IsNullOrWhiteSpace(farm) ? null : farm,
            string.IsNullOrWhiteSpace(ver) ? null : ver,
            bytes, newest, TimeSpan.FromMilliseconds(played), money, metaOk, metaOk ? look : null);
    }

    private static int Num(string s) => int.TryParse(s, out var v) ? Math.Clamp(v, 0, 255) : 0;

    private sealed record ClothingInfo(
        string? Id, int Index, (int R, int G, int B) Color, bool Prismatic,
        int HairDrawType, bool IgnoreHairOffset);

    /// <summary>解一件穿着物件（shirtItem/pantsItem/hat）的显示字段。缺块返回 null。</summary>
    private static ClothingInfo? ItemOf(Dictionary<string, string> items, string tag)
    {
        if (!items.TryGetValue(tag, out var body)) return null;
        string? Field(string name)
        {
            var m = Regex.Match(body, "<" + name + ">([^<]*)");
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }
        var id = Field("itemId");
        int index;
        if (tag == "hat")
            index = int.TryParse(Field("parentSheetIndex"), out var pi) ? pi
                  : int.TryParse(id, out var hi) ? hi : -1;
        else
            index = int.TryParse(Field("indexInTileSheet"), out var si) ? si : -1;
        var color = (255, 255, 255);
        var cm = Regex.Match(body, @"<clothesColor>\s*<B>(\d+)</B>\s*<G>(\d+)</G>\s*<R>(\d+)</R>");
        if (cm.Success) color = (Num(cm.Groups[3].Value), Num(cm.Groups[2].Value), Num(cm.Groups[1].Value));
        return new ClothingInfo(
            id, index, color,
            Field("isPrismatic") == "true",
            int.TryParse(Field("hairDrawType"), out var hd) ? hd : 0,
            Field("ignoreHairstyleOffset") == "true");
    }

    private static int IntOf(Dictionary<string, string> tags, string key, int fallback)
        => tags.TryGetValue(key, out var s) && int.TryParse(s, out var v) ? v : fallback;

    // ─────────────── 导出 ───────────────

    /// <summary>
    /// 把一份档打成 zip。<b>zip 内保留存档目录名这一层</b> —— 导入方（以及玩家自己解压）
    /// 才能一眼看出这是哪份档；只摊平文件的话同目录解压必然互相覆盖。
    /// </summary>
    public static (bool Ok, string Message) Export(SaveEntry e, string destZip)
    {
        try
        {
            if (!Directory.Exists(e.Dir)) return (false, LocService.Tr("存档目录不存在"));
            var tmp = destZip + ".part";
            if (File.Exists(tmp)) File.Delete(tmp);
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                var files = Directory.EnumerateFiles(e.Dir, "*", SearchOption.AllDirectories).ToList();
                foreach (var f in files)
                {
                    var rel = Path.GetRelativePath(e.Dir, f).Replace('\\', '/');
                    var entry = zip.CreateEntry(e.DirName + "/" + rel, CompressionLevel.Optimal);
                    // 把原文件的修改时间写进 zip 条目：导入端靠它还原时间戳（否则「最近游玩」会乱）
                    try { entry.LastWriteTime = File.GetLastWriteTime(f); } catch { /* 超出 zip 可表示范围就留默认 */ }
                    using var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var dst = entry.Open();
                    src.CopyTo(dst);
                }
            }
            if (File.Exists(destZip)) File.Delete(destZip);
            File.Move(tmp, destZip);
            return (true, LocService.Tr("已导出") + "：" + Path.GetFileName(destZip));
        }
        catch (Exception ex)
        {
            AppLog.Warn("存档", $"导出 {e.DirName} 失败：{ex.Message}");
            return (false, LocService.Tr("导出失败") + "：" + ex.Message);
        }
    }

    // ─────────────── 导入 ───────────────

    /// <summary>
    /// 导入一份压缩包（zip / rar / 7z，按文件头认，不看扩展名）。要求包里**至少有一个 SaveGameInfo**
    /// （也接受直接把文件摊在根上的包）；撞名时改成「名字 (导入 时间)」，绝不覆盖现有档 —— 存档是不可再生数据。
    /// <para>不走 <c>ZipFile.ExtractToDirectory</c>：它不保留条目时间戳，解出来全是「现在」，
    /// 玩家导回来的档会在首页「最近游玩」里假性置顶，游戏读档菜单的排序也跟着乱。
    /// 这里逐条写入并 <c>SetLastWriteTime</c> 还原。</para>
    /// </summary>
    public static (bool Ok, string Message, string? DirName) Import(string zipPath)
    {
        var root = SaveVersionService.SavesDir();
        if (root is null) return (false, LocService.Tr("找不到存档目录（先启动一次游戏）"), null);
        var fmt = ModService.DetectArchiveFormat(zipPath);
        if (fmt != "zip" && fmt != "rar" && fmt != "7z")
            return (false, LocService.Tr("不支持的压缩包格式（zip / rar / 7z）"), null);
        var (files, keepAlive) = ReadArchive(zipPath, fmt);
        try
        {
            // 档根 = 含 SaveGameInfo 的最浅那一层目录
            var info = files
                .Where(e => string.Equals(e.Name, "SaveGameInfo", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.Full.Count(c => c == '/'))
                .FirstOrDefault();
            if (info is null)
                return (false, LocService.Tr("这个压缩包里没有找到存档（缺 SaveGameInfo）"), null);

            var prefix = info.Full[..(info.Full.Length - info.Name.Length)];
            var wantName = prefix.Length == 0
                ? Path.GetFileNameWithoutExtension(zipPath)          // 摊平的包：用压缩包名
                : prefix.TrimEnd('/').Split('/')[^1];
            if (string.IsNullOrWhiteSpace(wantName)) wantName = Path.GetFileNameWithoutExtension(zipPath);

            var dest = Path.Combine(root, SafeName(wantName));
            if (Directory.Exists(dest))
                dest = Path.Combine(root, SafeName(wantName) + " (导入 " + DateTime.Now.ToString("MMdd-HHmm") + ")");
            Directory.CreateDirectory(dest);

            var copied = 0;
            foreach (var e in files)
            {
                if (!e.Full.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var rel = e.Full[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
                if (rel.Length == 0) continue;
                var target = Path.Combine(dest, rel);
                var parent = Path.GetDirectoryName(target);
                if (parent is not null) Directory.CreateDirectory(parent);
                e.Extract(target);
                if (e.Mtime is { } mt)
                {
                    try { File.SetLastWriteTime(target, mt.LocalDateTime); } catch { }
                }
                copied++;
            }
            if (copied == 0)
            {
                try { Directory.Delete(dest, true); } catch { }
                return (false, LocService.Tr("这个压缩包里没有找到存档（缺 SaveGameInfo）"), null);
            }

            AppLog.Info("存档", $"已导入 {Path.GetFileName(zipPath)} → {Path.GetFileName(dest)}（{copied} 个文件）");
            return (true, LocService.Tr("已导入") + "：" + Path.GetFileName(dest), Path.GetFileName(dest));
        }
        catch (Exception ex)
        {
            AppLog.Warn("存档", $"导入 {Path.GetFileName(zipPath)} 失败：{ex.Message}");
            return (false, LocService.Tr("导入失败") + "：" + ex.Message, null);
        }
        finally { keepAlive.Dispose(); }
    }

    private sealed record ArchEntry(string Full, string Name, DateTimeOffset? Mtime, Action<string> Extract);

    /// <summary>把压缩包条目表读平成统一列表（路径统一 '/' 分隔）。zip 走内置库，rar/7z 走 SharpCompress。
    /// 返回的 KeepAlive 必须活到所有 Extract 跑完 —— 条目流依赖打开的归档句柄。</summary>
    private static (List<ArchEntry> Files, IDisposable KeepAlive) ReadArchive(string path, string fmt)
    {
        var list = new List<ArchEntry>();
        switch (fmt)
        {
            case "zip":
            {
                var zip = System.IO.Compression.ZipFile.OpenRead(path);
                foreach (var e in zip.Entries)
                {
                    if (e.Name.Length == 0 || e.FullName.EndsWith("/")) continue;
                    var ex = e;
                    list.Add(new ArchEntry(ex.FullName.Replace('\\', '/'), ex.Name,
                        ex.LastWriteTime, t => ex.ExtractToFile(t, true)));
                }
                return (list, zip);
            }
            case "rar":
            {
                var a = SharpCompress.Archives.Rar.RarArchive.OpenArchive(path);
                foreach (var e in a.Entries)
                {
                    if (e.IsDirectory || string.IsNullOrEmpty(e.Key)) continue;
                    var ex = e;
                    // 不走 WriteToFile 扩展：实机 RAR5 下它抛 IndexOutOfRange，OpenEntryStream 逐条复制是好的
                    list.Add(new ArchEntry(ex.Key.Replace('\\', '/'), Path.GetFileName(ex.Key),
                        ex.LastModifiedTime, t => { using var s = ex.OpenEntryStream(); using var fs = File.Create(t); s.CopyTo(fs); }));
                }
                return (list, a);
            }
            case "7z":
            {
                var a = SharpCompress.Archives.SevenZip.SevenZipArchive.OpenArchive(path);
                foreach (var e in a.Entries)
                {
                    if (e.IsDirectory || string.IsNullOrEmpty(e.Key)) continue;
                    var ex = e;
                    list.Add(new ArchEntry(ex.Key.Replace('\\', '/'), Path.GetFileName(ex.Key),
                        ex.LastModifiedTime, t => { using var s = ex.OpenEntryStream(); using var fs = File.Create(t); s.CopyTo(fs); }));
                }
                return (list, a);
            }
            default:
                return (list, new NoopDispose());
        }
    }

    private sealed class NoopDispose : IDisposable
    {
        public void Dispose() { }
    }

    /// <summary>目录名消毒：Windows 不允许的字符换成下划线，顺带掐掉尾部空格/点。</summary>
    private static string SafeName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (s.Length == 0) s = "导入的存档";
        return s.Length > 80 ? s[..80] : s;
    }
}
