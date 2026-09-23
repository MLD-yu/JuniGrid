using System.IO;
using System.Text.RegularExpressions;

namespace JuniGrid.Services;

/// <summary>
/// 存档的「单向升级」保护 + 择档/聚焦启动。
///
/// 星露谷只有一个存档目录（%APPDATA%\StardewValley\Saves），所有游戏版本共用一份，
/// 而存档是被单向写上去的：一份档只要被高版本存过一次盘，低版本再点它就是进档闪退
/// （实测 28 次：1.0 加载被 1.6.8 写过的档，栈全是 NPC..ctor ← Game1.loadForNewGame）。
/// 游戏自己的存档列表不分版本，所以玩家从界面上看不出哪一行读得了。
///
/// 设计（列表始终完整，不再按版本隐藏）：
/// ① 每次往更高版本切之前，先把「这次会被升级」的档整份复制进缓存留底。想回旧版本继续玩时，
///    把留底放回 Saves，那份档在旧版本又读得懂 —— 存档本体从不删，只做复制或改名移走。
///    这一步只读 Saves、往缓存里写，开着云存档也安全。
/// ② 启动器「用这份档玩」自动切到能读它的版本，并用 <see cref="FocusHideExcept"/> 聚焦：
///    游戏里只显示选中的那一份，老版本列表分不清高版本档、点错行闪退的问题从根上没了。
///    玩完 <see cref="RestoreHidden"/> 放回，云眼里的 Saves 始终是完整的。
///
/// 判「读不了」只认档里写着的 &lt;gameVersion&gt;：1.0–1.2 的存档没有这个字段，判不准就一律不动它。
/// </summary>
public static class SaveVersionService
{
    /// <summary>Saves 下一个目录 = 一份存档槽位（联机时同一槽位有多个成员文件）。</summary>
    public sealed record Slot(string Name, string Dir, string? FarmerName, string? FarmName,
        string? GameVersion, long SizeBytes, DateTime WrittenAt, bool MetaComplete = true);

    /// <summary>一份留底。<see cref="At"/> 是留底时间，<see cref="SourceWritten"/> 用来判「内容没变过」。</summary>
    public sealed record Stamp(string Dir, string SaveName, DateTime At, DateTime SourceWritten,
        long SizeBytes, string Note);


    /// <summary>测试沙箱替换点（与 StoragePaths.CacheRoot 同用法）；null = 真实存档目录。</summary>
    public static string? SavesDirOverride { get; set; }

    /// <summary>留底落在版本缓存根下面，存储清理页那一列表才看得到它、才能回收它。</summary>
    public static string BackupRoot => Path.Combine(DepotDownloaderService.StagingRoot, "_saves-backup");

    private const int KeepStamps = 3;
    private const string StampMeta = ".jg-src";
    private const string HiddenDirName = "Saves-hidden";
    /// <summary>名字里带这四个字的留底是「退回旧档时被换下来的现有档」，不参与自动回收。</summary>
    private const string ReplacedMark = "换下来的";

    /// <summary>存档目录。整个目录不存在（从没玩过）时返回 null，所有操作自动变成空动作。</summary>
    public static string? SavesDir()
    {
        // 覆盖路径同样要求目录真的存在：否则「没有存档」这件事在沙箱里和真机上表现不一致，
        // 收档/放回会往一个凭空的目录里搬（2026-09-22 沙箱 S16 就是这么抓出来的）。
        if (!string.IsNullOrWhiteSpace(SavesDirOverride))
            return Directory.Exists(SavesDirOverride) ? SavesDirOverride : null;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StardewValley", "Saves");
        return Directory.Exists(dir) ? dir : null;
    }

    // ─────────────── 版本口径（单一出口：界面和切换闸门都从这里判） ───────────────

    /// <summary>
    /// &lt;0 = a 更旧。认不出的段按 0 处理，绝不抛。
    /// 口径见 <see cref="DepotDownloaderService.CompareVersionLabels"/>：两边都在版本表里时用表序，
    /// 不拿 Steam 分支名当版本号做数字段比较（那条路会得出 1.11 &gt; 1.6）。
    /// </summary>
    public static int CompareVersions(string? a, string? b)
        => DepotDownloaderService.CompareVersionLabels(a, b);

    /// <summary>
    /// version 读不读得了这份档。
    /// - 写着更高 gameVersion 的 → 读不了（单向升级，1.0 点 1.6 档必 NPC..ctor 闪退）。
    /// - 没有 SaveGameInfo 的残缺档 → **不给旧版本开**（实测：海岛_447615700 只剩档体，
    ///   被当成「无版本=1.0 可读」后在 1.0 里闪退）。至少 1.3+ 再试。
    /// - 1.2 及更早的老格式（有 SaveGameInfo、无 gameVersion）→ 一律当读得了。
    /// </summary>
    public static bool ReadableBy(Slot s, string? version)
    {
        if (version is null) return true;
        if (!s.MetaComplete)
            return CompareVersions(version, "1.3") >= 0;
        if (s.GameVersion is null) return true;
        return CompareVersions(s.GameVersion, version) <= 0;
    }

    /// <summary>这份档会不会被 version 存一次盘就回不去（= 切过去之前该留底）。</summary>
    public static bool UpgradedBy(Slot s, string? version)
        => version is not null && (s.GameVersion is null || CompareVersions(s.GameVersion, version) < 0);

    // ─────────────── 启动前那道闸门用的口径（只读，不动任何文件） ───────────────

    /// <summary>当前存档目录里 version 读不了的那几份。</summary>
    public static List<Slot> UnreadableBy(string? version)
        => Scan().Where(s => !ReadableBy(s, version)).ToList();

    /// <summary>
    /// 这批「读不了的档」的指纹。闸门用它判「同一状态只拦一次」：档的集合没变就说明玩家已经看过
    /// 说明了，不该每次启动都拦；集合变了（新增了档、或换了版本）则重新拦一次。
    /// </summary>
    public static string UnreadableFingerprint(string? version, List<Slot> bad)
        => (version ?? "-") + "|" + string.Join(",",
            bad.Select(s => s.Name).OrderBy(x => x, StringComparer.Ordinal));

    /// <summary>弹窗里那段话：说清后果 + 两条出路（切版本 / 仍然启动）。</summary>
    public static string DescribeUnreadableShort(string? version, List<Slot> bad)
    {
        var need = bad.Select(s => s.GameVersion)
            .Where(v => v is not null)
            .OrderBy(v => v, Comparer<string?>.Create((a, b) => CompareVersions(a, b)))
            .LastOrDefault() ?? "更高的版本";
        return $"当前游戏版本 {version ?? "未知"} 读不了存档列表里的 {bad.Count} 份档（它们是 {need} 或更高版本存过盘的）。\n\n"
             + "这个版本的存档列表会把它们显示得和你自己的档一模一样（农场主名、农场名、日期全是空白或同一个值），"
             + "点错一行就是进档闪退到桌面。\n\n"
             + $"要玩那 {bad.Count} 份档：在「版本管理」里切到 {need} 或更新的版本。\n"
             + $"只想在 {version ?? "这个版本"} 里玩自己的档：点「仍然启动」，进去之后认准目录名再点。";
    }

    /// <summary>写进日志的完整版：逐份列出目录名、档内版本、农场主名。</summary>
    public static string DescribeUnreadable(string? version, List<Slot> bad)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"版本 {version ?? "未知"} 读不了的存档共 {bad.Count} 份（目录名 / 档内版本 / 农场主名）：\n");
        foreach (var s in bad.Take(20))
            sb.Append($"  {s.Name}  /  {s.GameVersion}  /  {s.FarmerName ?? "-"}\n");
        if (bad.Count > 20) sb.Append($"  …另有 {bad.Count - 20} 份\n");
        sb.Append("存档是单向升级的：被高版本存过一次盘，低版本再点它就是 NPC..ctor 里的 NullReferenceException。");
        return sb.ToString();
    }


    // ─────────────── 扫描 ───────────────

    public static List<Slot> Scan()
    {
        var list = new List<Slot>();
        var root = SavesDir();
        if (root is null) return list;
        string[] dirs;
        try { dirs = Directory.GetDirectories(root); } catch { return list; }
        foreach (var dir in dirs)
        {
            var s = ReadSlot(dir);
            if (s is not null) list.Add(s);
        }
        return list;
    }

    /// <summary>读一份存档槽位；空目录（Steam 云留下的壳）返回 null，不算一份档。</summary>
    private static Slot? ReadSlot(string dir)
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

        string? farmer = null, farm = null, ver = null;
        var metaOk = false;
        try
        {
            var info = Path.Combine(dir, "SaveGameInfo");
            if (File.Exists(info))
            {
                metaOk = true;
                var txt = File.ReadAllText(info);
                farm = Tag(txt, "farmName");
                ver = Tag(txt, "gameVersion");
                farmer = Tag(txt, "playerName") ?? Inside(txt, "player", "name") ?? Tag(txt, "name");
            }
        }
        catch { /* 名字读不出来不影响留底和挪走，只是界面上少一列 */ }

        return new Slot(Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, '/')), dir,
            farmer, farm, ver, bytes, newest, metaOk);
    }

    private static string? Tag(string xml, string tag)
    {
        var m = Regex.Match(xml, "<" + tag + ">([^<]*)");
        var v = m.Success ? m.Groups[1].Value.Trim() : "";
        return v.Length > 0 ? v : null;
    }

    private static string? Inside(string xml, string outer, string inner)
    {
        var o = Regex.Match(xml, "<" + outer + ">(.*?)</" + outer + ">", RegexOptions.Singleline);
        return o.Success ? Tag(o.Groups[1].Value, inner) : null;
    }

    // ─────────────── ① 留底 ───────────────

    /// <summary>切到 targetVersion 之前，给「会被这个版本升上去」的档各留一份底。返回新建留底份数。</summary>
    public static int BackupAboutToUpgrade(string? targetVersion, Action<string>? log = null)
    {
        if (targetVersion is null)
        {
            log?.Invoke("认不出要切去的版本号，这次不给存档留底");
            return 0;
        }
        SweepPartials();
        var made = 0;
        foreach (var s in Scan().Where(x => UpgradedBy(x, targetVersion)))
        {
            try
            {
                var root = Path.Combine(BackupRoot, s.Name);
                Directory.CreateDirectory(root);
                var stamps = StampsOfPath(root);
                // 内容没动过就不再复制一份：他一天能切十次版本，只玩一次的档才需要底
                if (stamps.Count > 0 && stamps[0].SourceWritten == s.WrittenAt
                    && stamps[0].SizeBytes == s.SizeBytes) continue;

                var dst = UniqueStampDir(root, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                var tmp = dst + ".tmp";
                CopyTree(s.Dir, tmp);
                File.WriteAllText(Path.Combine(tmp, StampMeta),
                    $"{s.SizeBytes}|{s.WrittenAt.Ticks}|切到 {targetVersion} 之前（存档内版本 " +
                    $"{s.GameVersion ?? "未记录，1.2 及更早"}）");
                Directory.Move(tmp, dst);      // 标记最后写、整棵改名顶上：半份底不会被认成底
                made++;
                PruneStamps(root, log);
                log?.Invoke($"给存档「{s.FarmName ?? s.Name}」留了底（{(double)s.SizeBytes / 1048576:F1} MB）");
            }
            catch (Exception ex)
            {
                log?.Invoke($"存档「{s.Name}」留底失败：{ex.Message}");
            }
        }
        return made;
    }

    /// <summary>某份存档的全部留底，新的在前。</summary>
    public static List<Stamp> StampsOf(string saveName) => StampsOfPath(Path.Combine(BackupRoot, saveName));

    private static List<Stamp> StampsOfPath(string saveRoot)
    {
        var list = new List<Stamp>();
        if (!Directory.Exists(saveRoot)) return list;
        try
        {
            foreach (var dir in Directory.GetDirectories(saveRoot))
            {
                if (dir.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                var meta = Path.Combine(dir, StampMeta);
                if (!File.Exists(meta)) continue;
                long size = 0; var written = DateTime.MinValue;
                try
                {
                    var parts = File.ReadAllText(meta).Split('|');
                    if (parts.Length >= 2)
                    {
                        long.TryParse(parts[0], out size);
                        long.TryParse(parts[1], out var ticks);
                        if (ticks > 0) written = new DateTime(ticks);
                    }
                }
                catch { }
                list.Add(new Stamp(dir, Path.GetFileName(saveRoot), LastWriteOf(meta),
                    written, size, NoteOf(dir)));
            }
        }
        catch { return list; }
        list.Sort((a, b) => b.At.CompareTo(a.At));
        return list;
    }

    private static DateTime LastWriteOf(string file)
    {
        try { return new FileInfo(file).LastWriteTime; } catch { return DateTime.MinValue; }
    }

    private static string NoteOf(string stampDir)
    {
        try
        {
            var parts = File.ReadAllText(Path.Combine(stampDir, StampMeta)).Split('|');
            return parts.Length > 2 ? parts[2] : "";
        }
        catch { return ""; }
    }

    private static string UniqueStampDir(string saveRoot, string stamp)
    {
        var dst = Path.Combine(saveRoot, stamp);
        var i = 2;
        while (Directory.Exists(dst)) dst = Path.Combine(saveRoot, $"{stamp}-{i++}");
        return dst;
    }

    /// <summary>
    /// 每份存档最多留 KeepStamps 份底，删最旧的。删的只是我们复制出来的副本 —— 原件一直在 Saves 里。
    /// 「换下来的」那种是玩家亲手换掉、盘上只剩一份的状态，不参与回收。
    /// </summary>
    private static void PruneStamps(string saveRoot, Action<string>? log)
    {
        var stamps = StampsOfPath(saveRoot)
            .Where(s => !Path.GetFileName(s.Dir).Contains(ReplacedMark, StringComparison.Ordinal))
            .ToList();
        foreach (var s in stamps.Skip(KeepStamps))
        {
            try
            {
                Directory.Delete(s.Dir, true);
                log?.Invoke($"清掉更旧的一份存档底：{Path.GetFileName(s.Dir)}");
            }
            catch { /* 删不动就留着，占点盘而已 */ }
        }
        SweepPartials(saveRoot);
    }

    /// <summary>复制中断留下的 &lt;时间戳&gt;.tmp 半份底，下次路过顺手收掉（不传就扫整个留底区）。</summary>
    private static void SweepPartials(string? saveRoot = null)
    {
        var roots = new List<string>();
        try
        {
            if (saveRoot is not null) roots.Add(saveRoot);
            else if (Directory.Exists(BackupRoot)) roots.AddRange(Directory.GetDirectories(BackupRoot));
        }
        catch { return; }
        foreach (var r in roots)
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(r, "*.tmp"))
                    try { Directory.Delete(dir, true); } catch { }
            }
            catch { }
        }
    }

    /// <summary>留底区里现在有哪些档、各留了几份底（设置 → 缓存与存储 的「存档升级前的留底」那一行展开用）。
    /// 按最新一份底的时间倒序。</summary>
    public static List<(string SaveName, int Stamps, DateTime Newest, string Note)> StashOverview()
    {
        var list = new List<(string SaveName, int Stamps, DateTime Newest, string Note)>();
        if (!Directory.Exists(BackupRoot)) return list;
        try
        {
            foreach (var dir in Directory.GetDirectories(BackupRoot))
            {
                var name = Path.GetFileName(dir);
                var stamps = StampsOfPath(dir);
                if (stamps.Count == 0) continue;
                list.Add((name, stamps.Count, stamps[0].At, stamps[0].Note));
            }
        }
        catch { return list; }
        list.Sort((x, y) => y.Newest.CompareTo(x.Newest));
        return list;
    }

    /// <summary>某份存档最新的那一份留底在哪个目录（「退回」退的就是它）。没有则 null。</summary>
    public static string? NewestStampDir(string saveName)
    {
        var stamps = StampsOf(saveName);
        return stamps.Count == 0 ? null : stamps[0].Dir;
    }

    /// <summary>某个版本包里现在压着几份被收走的档。启动要不要绕开 Steam 判的就是这个：
    /// 只要压着，云随时可能把它们下回来，所以每一次启动都得绕，不只是搬东西那一次。</summary>
    public static int StashedCountFor(string? pkgDir)
    {
        if (pkgDir is null) return 0;
        try
        {
            // 新 C: 抽屉 + 旧 E: 抽屉都算：排空窗口期旧抽屉里还压着的档不能漏计，
            // 否则「绕开 Steam 直启」的判据会在收敛前翻掉。
            var n = 0;
            foreach (var h in new[] { HiddenRootOf(pkgDir), LegacyHiddenRootOf(pkgDir) })
                if (Directory.Exists(h)) n += Directory.GetDirectories(h).Length;
            return n;
        }
        catch { return 0; }
    }

    /// <summary>聚焦抽屉 + 各版本抽屉里一共压着几份。聚焦启动后判「要不要绕开 Steam」用这个持久量。</summary>
    public static int StashedCountAll()
    {
        try { return EnumerateHiddenRoots().Sum(h => Directory.GetDirectories(h).Length); }
        catch { return 0; }
    }

    /// <summary>留底总占用（设置 → 缓存与存储 那一行用）。目录不存在时是 0。</summary>
    public static long BackupBytes() => Directory.Exists(BackupRoot) ? DirBytes(BackupRoot) : 0;

    /// <summary>全部留底涉及的存档槽位数，界面上「N 份存档有留底」。</summary>
    public static int BackedUpSaveCount()
    {
        if (!Directory.Exists(BackupRoot)) return 0;
        try { return Directory.GetDirectories(BackupRoot).Count(d => StampsOfPath(d).Count > 0); }
        catch { return 0; }
    }

    /// <summary>
    /// 退回：把选中的那份底放回 Saves。
    /// 现有的那份不覆盖也不删 —— 先整棵改名成一份新留底（还能再换回去），然后把旧的复制进来
    /// （复制不是搬走，留底原地留着，可以反复退回不同的时间点）。
    /// </summary>
    public static string? Restore(string saveName, string stampDir, Action<string>? log = null)
    {
        var root = SavesDir();
        if (root is null) return "还没有存档目录，退回不了";
        var saveRoot = Path.Combine(BackupRoot, saveName);
        if (!PathsUnder(stampDir, saveRoot) || !File.Exists(Path.Combine(stampDir, StampMeta)))
            return "这份留底已经不在原位置了";
        var dst = Path.Combine(root, saveName);
        try
        {
            if (Directory.Exists(dst))
            {
                var aside = UniqueStampDir(saveRoot,
                    DateTime.Now.ToString("yyyyMMdd_HHmmss") + "-" + ReplacedMark + "现有档");
                if (DepotDownloaderService.MoveDirectoryVerified(dst, aside, null, null, "换下现有存档")
                    == DepotDownloaderService.DirMove.Failed)
                    return "现有的那份挪不动，已停止（没有覆盖任何东西）";
                // 补上完成标记，这份「换下来的」才算一份认得出的留底：界面上列得出来、
                // 没有标记的半成品会被回收逻辑清掉，被换下来的档绝不能被那样清掉。
                var moved = ReadSlot(aside);
                if (moved is not null)
                    File.WriteAllText(Path.Combine(aside, StampMeta),
                        $"{moved.SizeBytes}|{moved.WrittenAt.Ticks}|退回「{saveName}」时被换下来的现有档");
                log?.Invoke($"现有的那份存档已改名为留底：{Path.GetFileName(aside)}");
            }
            CopyTree(stampDir, dst, skipMeta: true);
            log?.Invoke($"已把「{Path.GetFileName(stampDir)}」那份底放回存档「{saveName}」");
            return null;
        }
        catch (Exception ex)
        {
            log?.Invoke($"退回存档「{saveName}」失败：{ex.Message}");
            return "退回失败：" + ex.Message;
        }
    }

    private static bool PathsUnder(string child, string parent)
    {
        try
        {
            var c = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar);
            var p = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
            return c.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                   && Path.GetFileName(Path.GetDirectoryName(c)) == Path.GetFileName(p);
        }
        catch { return false; }
    }

    // ─────────────── ② 按版本挪走读不了的档 ───────────────

    /// <summary>
    /// 抽屉根。**必须和现役存档同盘**：存档在 C:（%AppData%\StardewValley\Saves），
    /// 抽屉若还在 E:（旧位置，staging 包下），收/放每一份都是跨盘字节拷贝
    /// （2026-09-23 真机：10 份 × 100~1000ms = 4.6 秒，日志里全是「0 次尝试全败→走拷贝」，
    /// 0 次尝试 = SameVolume 判否连改名都不试）。同盘后 Directory.Move 是改名 = 瞬时，这才是「瞬间切档」。
    /// 放 LocalApplicationData（不漫游、游戏不扫这个目录）；代价是 C: 多占一份抽屉体积，
    /// 换取收/放零拷贝 —— 用户 2026-09-23 明确要求瞬间，空间让位给速度。
    /// </summary>
    /// <summary>测试沙箱可覆写抽屉根（与 <see cref="SavesDirOverride"/> 同理，不落配置）。</summary>
    public static string? DrawerRootOverride { get; set; }

    public static string DrawerRoot => DrawerRootOverride ?? StoragePaths.SavesHiddenDir;

    public static string HiddenRootOf(string pkgDir) => Path.Combine(DrawerRoot,
        Path.GetFileName(pkgDir.TrimEnd(Path.DirectorySeparatorChar, '/')));

    /// <summary>旧抽屉位置（E: staging 包下）。只读：EnumerateHiddenRoots 仍会枚举它，
    /// 放回时跨盘拷一次进 Saves 后 E: 那份即空，一轮收敛到 C:；不做显式迁移、不丢数据。</summary>
    private static string LegacyHiddenRootOf(string pkgDir) => Path.Combine(pkgDir, HiddenDirName);
    private static string LegacyFocusRootOf() => Path.Combine(DepotDownloaderService.StagingRoot, FocusDirName);

    /// <summary>把各版本抽屉里收着的档放回 Saves。
    /// <paramref name="readableByVersion"/> 非空时只放回**这个版本读得了的**档，读不了的留在抽屉原地不动。
    ///
    /// 为什么要这个差集：调用方（切换前的 PrepareSavesFor、启动前的 PrepareSavesForLaunch）
    /// 都是「先全放回 → 紧接着 HideUnreadable 把读不了的收回去」。无条件全放回意味着
    /// **每一份目标版本读不了的档都必然走完一个完整往返**。而存档真身固定在 C:
    /// （%APPDATA%\StardewValley\Saves），抽屉在缓存盘 E:，跨盘 ⇒ 改名快路径直接不上
    /// （TryRelocateDirectory 第一行就因 SameVolume=false 返回），硬链接也用不了 ⇒ 全是真字节拷贝。
    /// 2026-09-23 真机实测：抽屉 436 MB / 14 份，一次切换搬出 436 MB 再搬回 436 MB，
    /// 花 14.4 秒（占整次切换约 25 秒的一半以上），而对 Saves 目录的**净改变为 0** ——
    /// 放回的那 14 份和随后收走的那 14 份名单完全一致。
    ///
    /// 稳态不变：跳过的那些档最终就该在抽屉里，改与不改结束状态相同，
    /// 所以 StashedCountAll 这个持久量不受影响（它 &gt;0 正是「绕开 Steam 直启」的判据，见 LauncherService）。
    /// 同名冲突仍无条件当场裁决 —— 那是 StashedCountAll 死结的出口，不能因为差集跳过。</summary>
    public static int RestoreHidden(Action<string>? log = null, string? readableByVersion = null)
    {
        var root = SavesDir();
        if (root is null) return 0;
        var moved = 0;
        var resurrected = 0;
        var skipped = 0;
        foreach (var hidden in EnumerateHiddenRoots())
        {
            string[] dirs;
            try { dirs = Directory.GetDirectories(hidden); } catch { continue; }
            foreach (var dir in dirs)
            {
                var name = Path.GetFileName(dir);
                var dst = Path.Combine(root, name);
                if (Directory.Exists(dst))
                {
                    // 同名档两边都有（开着 Steam 云最常见：云把它同步回来了）。
                    // 以前只记日志、抽屉那份永远不放回 —— 于是 StashedCountAll 恒 ≥1，
                    // 启动路由一直误判「暂存区压着档」，还把兼容性闸门整个跳过（真机 2026-09-22 12:03 那次
                    // 就是在 1.0 里点到了 1.6 的「镇委书记」直接 NPC..ctor）。
                    // 现在当场拆掉这个死结：更完整/更新的一份留在 Saves，另一份降级成留底，抽屉清空。
                    if (!ResolveNameConflict(dir, dst, name, log)) resurrected++;
                    continue;
                }
                if (!ShouldRestore(dir, readableByVersion)) { skipped++; continue; }
                if (DepotDownloaderService.MoveDirectoryVerified(dir, dst, null, null, "移回存档", BackupRoot)
                    != DepotDownloaderService.DirMove.Failed)
                {
                    moved++;
                    log?.Invoke($"存档「{name}」已放回存档列表");
                }
            }
            TryDeleteIfEmpty(hidden);
        }
        // 我们收走的档又出现在 Saves 里 = Steam 云把它下载回来了。这不是事故，是云开着时的常态：
        // 实测 2026-09-22 04:57:45，本地少 83 个存档文件，Steam 的 up 同步选择**下载 83、上传 2** ——
        // 「本地没有」被判成「该恢复」，不是「玩家删档」。同名冲突现已由 ResolveNameConflict 当场拆掉。
        if (resurrected > 0)
            log?.Invoke($"有 {resurrected} 份暂存档与 Saves 同名且两份都残缺/拆不动，暂留在抽屉里");
        if (skipped > 0)
            log?.Invoke($"{skipped} 份存档 {readableByVersion} 读不了，留在抽屉里没搬（搬出去也会被立刻收回来）");
        return moved;
    }

    /// <summary>这份抽屉档该不该放回。<paramref name="version"/> 为空 = 全放回（旧行为）。
    /// 认不出的（空壳、没元数据）一律放回：这类目录是 KB 级，搬它不花钱，
    /// 而把它留在抽屉里会让 StashedCountAll 平白多计一份。</summary>
    private static bool ShouldRestore(string drawerDir, string? version)
    {
        if (version is null) return true;
        var s = ReadSlot(drawerDir);
        return s is null || ReadableBy(s, version);
    }

    /// <summary>
    /// 同名冲突当场裁决：有 SaveGameInfo 的、或更新的那份留在 Saves；另一份改名成留底。
    /// 抽屉里那一侧必须清空，否则 StashedCountAll 永远 ≥1，启动路由会一直误判。
    /// 成功拆掉返回 true。
    /// </summary>
    private static bool ResolveNameConflict(string drawerDir, string savesDir, string name, Action<string>? log)
    {
        try
        {
            var a = ReadSlot(drawerDir);   // 抽屉里那份
            var b = ReadSlot(savesDir);    // Saves 里那份（多半是云刚下的）
            // Saves 只有空壳（云留下的目录、没有档体）→ 空壳无数据可保，清掉让抽屉那份回位
            if (a is not null && b is null)
            {
                try { Directory.Delete(savesDir, true); } catch { }
                if (DepotDownloaderService.MoveDirectoryVerified(drawerDir, savesDir, null, null, "同名空壳清理后回位", BackupRoot)
                    != DepotDownloaderService.DirMove.Failed)
                {
                    TryDeleteIfEmpty(Path.GetDirectoryName(drawerDir)!);
                    log?.Invoke($"存档「{name}」Saves 里只是云留下的空壳，已清掉，抽屉里那份放回列表");
                    return true;
                }
                log?.Invoke($"存档「{name}」空壳清掉后抽屉那份仍搬不动");
                return false;
            }
            // 优先：有元数据的；都有则更新的；都无元数据则更新的
            var drawerWins =
                a is not null && (b is null
                    || (a.MetaComplete && !b.MetaComplete)
                    || (a.MetaComplete == b.MetaComplete && a.WrittenAt > b.WrittenAt));
            var root = Path.Combine(BackupRoot, name);
            Directory.CreateDirectory(root);
            var loserDir = drawerWins ? savesDir : drawerDir;
            var aside = UniqueStampDir(root, DateTime.Now.ToString("yyyyMMdd_HHmmss") + "-同名冲突让位");
            if (DepotDownloaderService.MoveDirectoryVerified(loserDir, aside, null, null, "同名冲突让位")
                == DepotDownloaderService.DirMove.Failed)
            {
                log?.Invoke($"存档「{name}」两边同名且让位失败，两份都留着（抽屉仍压着，启动路由可能误判）");
                return false;
            }
            // 无论 ReadSlot 认不认得出，都补完成标记 —— 否则这份让位的会被当成半成品清掉
            var moved = ReadSlot(aside);
            File.WriteAllText(Path.Combine(aside, StampMeta),
                $"{moved?.SizeBytes ?? 0}|{(moved?.WrittenAt ?? DateTime.Now).Ticks}|与 Saves 同名冲突时被换下来的那份");
            PruneStamps(root, log);
            if (drawerWins)
            {
                // 赢家在抽屉里 → 搬回 Saves 原位（输家 Saves 那份已进留底）
                if (DepotDownloaderService.MoveDirectoryVerified(drawerDir, savesDir, null, null, "同名冲突胜方回位", BackupRoot)
                    == DepotDownloaderService.DirMove.Failed)
                {
                    log?.Invoke($"存档「{name}」同名冲突：胜方搬回 Saves 失败，暂留在抽屉");
                    return false;
                }
            }
            // !drawerWins：赢家本来就在 Saves，输家抽屉那份已进留底，无需再搬
            TryDeleteIfEmpty(Path.GetDirectoryName(drawerDir)!);
            log?.Invoke($"存档「{name}」两边同名：{(drawerWins ? "抽屉" : "Saves")}那份留在列表，" +
                        $"另一份已降级为留底（{Path.GetFileName(aside)}），抽屉已清空");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"存档「{name}」同名冲突处理失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 聚焦启动：只留 keepSaveName 在 Saves，其余挪进聚焦抽屉。返回挪走的份数。
    /// 和旧「按版本收档」同一套存放与放回（RestoreHidden 会一并放回）。
    ///
    /// 为什么需要：老版本的存档列表读不懂高版本的档（显示成「农场主 1 / 农场 1」），
    /// 游戏内点错一行就是 NPC..ctor 闪退。启动器已经替玩家选好了这份档 ——
    /// 游戏里只出现这一行，点哪都是它。启动器列表仍然是全的。
    /// </summary>
    public static int FocusHideExcept(string? keepSaveName, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(keepSaveName) || SavesDir() is null) return 0;
        var half = HalfSyncedSaves();
        if (half.Count > 0)
        {
            log?.Invoke($"存档目录里有 {half.Count} 份只剩索引件的档（云同步断在半路）—— 这种现场不搬档");
            return 0;
        }
        var hidden = FocusRootOf();
        var n = 0;
        foreach (var s in Scan())
        {
            if (string.Equals(s.Name, keepSaveName, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var dst = Path.Combine(hidden, s.Name);
                Directory.CreateDirectory(hidden);
                if (Directory.Exists(dst))
                {
                    // 云把同名档补回了 Saves、聚焦抽屉里还留着一份：让 Saves 这份进抽屉，
                    // 抽屉原来那份降级成留底（不删、不覆盖）。
                    var root = Path.Combine(BackupRoot, s.Name);
                    var aside = UniqueStampDir(root,
                        DateTime.Now.ToString("yyyyMMdd_HHmmss") + "-聚焦抽屉里原来的那份");
                    Directory.CreateDirectory(root);
                    if (DepotDownloaderService.MoveDirectoryVerified(dst, aside, null, null, "给云上刚下来的那份让位")
                        == DepotDownloaderService.DirMove.Failed)
                    {
                        log?.Invoke($"存档「{s.Name}」聚焦抽屉里那份挪不动，Saves 里这份只能留着");
                        continue;
                    }
                    File.WriteAllText(Path.Combine(aside, StampMeta),
                        $"{s.SizeBytes}|{s.WrittenAt.Ticks}|聚焦时抽屉里已有同名的一份，这是抽屉里原来那份");
                    PruneStamps(root, log);
                }
                if (DepotDownloaderService.MoveDirectoryVerified(s.Dir, dst, null, null, "聚焦启动暂存其它存档")
                    == DepotDownloaderService.DirMove.Failed)
                {
                    log?.Invoke($"存档「{s.Name}」挪不动，会留在游戏列表里");
                    continue;
                }
                n++;
                log?.Invoke($"聚焦启动：「{s.FarmName ?? s.Name}」暂存进聚焦抽屉（游戏里只显示「{keepSaveName}」）");
            }
            catch (Exception ex)
            {
                log?.Invoke($"聚焦暂存「{s.Name}」失败：{ex.Message}");
            }
        }
        return n;
    }

    public static string FocusRootOf() => Path.Combine(DrawerRoot, FocusDirName);

    private const string FocusDirName = "_saves-focus";

    /// <summary>把 targetVersion 明确读不了的档挪进抽屉（游戏列表只显示读得了的）。返回挪走的份数。
    /// 半同步的那几份跳过（别搬半份档），其余尽量收；收不动的由调用方再扫进聚焦抽屉。</summary>
    public static int HideUnreadable(string? targetVersion, string pkgDir, Action<string>? log = null)
    {
        if (targetVersion is null || SavesDir() is null) return 0;
        // 半同步现场只跳过那几份，不再整段拒绝 —— 否则一份半截档会挡住全部收起，
        // 游戏列表里照样出现 1.6 档，启动闸门又弹回来（2026-09-22 13:35 真机）。
        var half = new HashSet<string>(HalfSyncedSaves(), StringComparer.OrdinalIgnoreCase);
        // 真实版本包 → 包下 Saves-hidden；聚焦根 / 空 → 直接进 _saves-focus（不要再套一层）
        var hidden = pkgDir is null
            || string.Equals(Path.GetFileName(pkgDir.TrimEnd(Path.DirectorySeparatorChar, '/')),
                FocusDirName, StringComparison.OrdinalIgnoreCase)
            ? FocusRootOf()
            : HiddenRootOf(pkgDir);
        var n = 0;
        foreach (var s in Scan().Where(x => !ReadableBy(x, targetVersion)))
        {
            // 半同步的也收走（本就残缺，留在列表里只会被点到闪退）；云会再下完整版
            if (half.Contains(s.Name))
                log?.Invoke($"存档「{s.Name}」只剩索引件，仍收起（残档不该出现在游戏列表里）");
            try
            {
                var dst = Path.Combine(hidden, s.Name);
                Directory.CreateDirectory(hidden);
                if (Directory.Exists(dst))
                {
                    var root = Path.Combine(BackupRoot, s.Name);
                    var aside = UniqueStampDir(root,
                        DateTime.Now.ToString("yyyyMMdd_HHmmss") + "-抽屉里原来的那份");
                    Directory.CreateDirectory(root);
                    if (DepotDownloaderService.MoveDirectoryVerified(dst, aside, null, null, "给云上刚下来的那份让位")
                        == DepotDownloaderService.DirMove.Failed)
                    {
                        log?.Invoke($"存档「{s.Name}」抽屉里那份挪不动，改收进聚焦抽屉");
                        if (ForceHideToFocus(s, log)) n++;
                        continue;
                    }
                    File.WriteAllText(Path.Combine(aside, StampMeta),
                        $"{s.SizeBytes}|{s.WrittenAt.Ticks}|收档时抽屉里已有同名的一份，这是抽屉里原来那份");
                    PruneStamps(root, log);
                    log?.Invoke($"抽屉里原来那份「{s.Name}」降级为留底（{Path.GetFileName(aside)}），给刚下回来的这份让位");
                }
                if (DepotDownloaderService.MoveDirectoryVerified(s.Dir, dst, null, null, "收走读不了的存档")
                    == DepotDownloaderService.DirMove.Failed)
                {
                    log?.Invoke($"存档「{s.Name}」挪不动，改收进聚焦抽屉");
                    if (ForceHideToFocus(s, log)) n++;
                    continue;
                }
                n++;
                log?.Invoke($"存档「{s.FarmName ?? s.Name}」是 {s.GameVersion} 的档，{targetVersion} 读不了 → 已收起");
            }
            catch (Exception ex)
            {
                log?.Invoke($"收存档「{s.Name}」失败：{ex.Message}");
                if (ForceHideToFocus(s, log)) n++;
            }
        }
        TryDeleteIfEmpty(hidden);
        return n;
    }

    /// <summary>版本包抽屉收不动时的兜底：挪进聚焦抽屉（同一套放回逻辑）。</summary>
    private static bool ForceHideToFocus(Slot s, Action<string>? log)
    {
        try
        {
            var dst = Path.Combine(FocusRootOf(), s.Name);
            Directory.CreateDirectory(FocusRootOf());
            if (Directory.Exists(dst))
            {
                var root = Path.Combine(BackupRoot, s.Name);
                Directory.CreateDirectory(root);
                var aside = UniqueStampDir(root,
                    DateTime.Now.ToString("yyyyMMdd_HHmmss") + "-聚焦抽屉里原来的那份");
                if (DepotDownloaderService.MoveDirectoryVerified(dst, aside, null, null, "聚焦抽屉让位")
                    != DepotDownloaderService.DirMove.Failed)
                {
                    File.WriteAllText(Path.Combine(aside, StampMeta),
                        $"{s.SizeBytes}|{s.WrittenAt.Ticks}|聚焦抽屉里原来的那份");
                }
            }
            if (DepotDownloaderService.MoveDirectoryVerified(s.Dir, dst, null, null, "兜底收起读不了的存档")
                == DepotDownloaderService.DirMove.Failed)
            {
                log?.Invoke($"存档「{s.Name}」两处抽屉都收不动，仍留在列表里");
                return false;
            }
            log?.Invoke($"存档「{s.Name}」已兜底收进聚焦抽屉");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"兜底收起「{s.Name}」失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>现在被收在某个版本抽屉里的档（界面上要说清「不是丢了，是被收起来了」）。</summary>
    public static List<(Slot Hidden, string PkgDir)> HiddenSaves()
    {
        var list = new List<(Slot, string)>();
        foreach (var hidden in EnumerateHiddenRoots())
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(hidden))
                {
                    var s = ReadSlot(dir);
                    if (s is not null) list.Add((s, Path.GetFileName(Path.GetDirectoryName(dir)!)));
                }
            }
            catch { }
        }
        return list;
    }

    private static IEnumerable<string> EnumerateHiddenRoots()
    {
        // 新抽屉（C:，同盘改名）。DrawerRoot 下每个子目录都是一个抽屉（含聚焦抽屉）。
        if (Directory.Exists(DrawerRoot))
        {
            string[] nd;
            try { nd = Directory.GetDirectories(DrawerRoot); } catch { nd = Array.Empty<string>(); }
            foreach (var d in nd) yield return d;
        }
        // 旧抽屉（E: staging 下）只读排空：放回时跨盘拷一次进 Saves，E: 那份随即变空，
        // 一轮之后全部收敛到 C:。不显式迁移、不丢数据。
        var root = DepotDownloaderService.StagingRoot;
        if (!Directory.Exists(root)) yield break;
        var lf = LegacyFocusRootOf();
        if (Directory.Exists(lf)) yield return lf;
        string[] dirs;
        try { dirs = Directory.GetDirectories(root); } catch { yield break; }
        foreach (var d in dirs)
        {
            if (string.Equals(Path.GetFileName(d), FocusDirName, StringComparison.OrdinalIgnoreCase)) continue;
            var lh = LegacyHiddenRootOf(d);
            if (Directory.Exists(lh)) yield return lh;
        }
    }

    private static void TryDeleteIfEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch { }
    }

    // ─────────────── 云存档「下到一半」的现场 ───────────────

    /// <summary>
    /// 找出看着像存档槽位、却没有档体文件的目录 —— Steam 云做全量下载同步时会先删本地再下，
    /// 中途超时（实测 2026-09-22 02:26:35 `Download complete, result Timeout`）就留下这种半成品：
    /// 目录还在、SaveGameInfo 下来了、真正的存档文件没下来。
    /// 这时进游戏存一次盘，Steam 会判「本地比云端新」，把缺文件的本地状态传上去覆盖云端 —— 那才是真丢。
    /// 星露谷的档体文件没有扩展名（`农场名_农场ID`，联机时每个农场主一个），
    /// 而 SaveGameInfo / SaveGameInfo_old 是随档生成的索引件，不算档体。
    /// </summary>
    public static List<string> HalfSyncedSaves()
    {
        var bad = new List<string>();
        var root = SavesDir();
        if (root is null) return bad;
        string[] dirs;
        try { dirs = Directory.GetDirectories(root); } catch { return bad; }
        foreach (var dir in dirs)
        {
            string[] files;
            try { files = Directory.GetFiles(dir); } catch { continue; }
            if (files.Length == 0) continue;                 // 空壳目录（云留下的壳）不报
            var named = files.Select(f => Path.GetFileName(f)).ToList();
            var isSaveSlot = named.Any(f => f.StartsWith("SaveGameInfo", StringComparison.OrdinalIgnoreCase))
                || named.Any(IsBodyFile);
            if (!isSaveSlot) continue;                        // 不是存档槽位，别管人家的目录
            if (named.Any(IsBodyFile)) continue;              // 档体在，正常
            bad.Add(Path.GetFileName(dir));
        }
        return bad;
    }

    private static bool IsBodyFile(string fileName)
        => Path.GetExtension(fileName).Length == 0
        && !fileName.StartsWith("SaveGameInfo", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 顶部横幅那一句话：要短到不被截断，又把「再点一次就能启动」这个出口写明白 ——
    /// 硬拦到底只会把人逼去用 Steam 直接启动（那条路我们完全看不到），比放行更不可控。
    /// </summary>
    public static string DescribeHalfSyncedShort(List<string> bad)
    {
        var head = string.Join("、", bad.Take(3)) + (bad.Count > 3 ? " 等" : "");
        return "有 " + bad.Count + " 份档只剩索引件、没有存档文件（" + head + "）—— Steam 云同步断在半路，" +
            "现在存盘可能把残缺状态覆盖到云端。再点一次「启动游戏」= 你看过这句、仍要启动；" +
            "更稳的是先让 Steam 把云同步跑完。";
    }

    /// <summary>写进日志的完整版（列前 6 个 + 具体怎么办）。</summary>
    public static string DescribeHalfSynced(List<string> bad)
    {
        var head = string.Join("、", bad.Take(6)) + (bad.Count > 6 ? " 等 " + bad.Count + " 份" : "");
        return "存档目录里有 " + bad.Count + " 份档只剩索引件、没有存档文件（" + head + "）—— " +
            "这是 Steam 云存档下到一半断了的现场。现在进游戏存一次盘，Steam 会当成「本地比云端新」" +
            "把缺文件的本地状态传上去覆盖云端，那几份档就真没了。\n\n" +
            "等 Steam 把云存档下完再启动：Steam 完全退出（不是关窗口）再重开 → 点开始游戏让它重跑同步；" +
            "反复超时的话，换网络节点或让 amazonaws / steamcloud 域名走直连。";
    }

    // ─────────────── 目录工具（存档量级几 MB～几十 MB，直接拷贝） ───────────────

    private static void CopyTree(string src, string dst, bool skipMeta = false)
    {
        Directory.CreateDirectory(dst);
        // ⚠ 禁止硬链接：留底/退回是「两份互不影响的真副本」。同盘硬链接会让 Touch 现役档
        // 顺手改掉留底内容（S13 钉住：退回后正文仍是「现在的样子」）。这里必须字节拷贝。
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, f);
            if (skipMeta && rel == StampMeta) continue;
            var to = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to, true);
        }
    }

    [System.Runtime.InteropServices.DllImport("Kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string newFileName, string? existingFileName, IntPtr securityAttributes);

    public static long DirBytes(string dir)
    {
        long sum = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                try { sum += new FileInfo(f).Length; } catch { }
        }
        catch { }
        return sum;
    }
}
