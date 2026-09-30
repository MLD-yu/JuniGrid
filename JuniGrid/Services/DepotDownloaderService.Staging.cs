using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace JuniGrid.Services;

public sealed partial class DepotDownloaderService
{
    internal static string StagingRoot
    {
        get
        {
            var root = StoragePaths.DepotStagingDir;
            try
            {
                // 旧路径（LocalAppData）→ 统一缓存目录，首次访问时搬迁
                var legacy = StoragePaths.LegacyDepotStagingDir;
                if (!string.Equals(root, legacy, StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(legacy)
                    && !Directory.Exists(root))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(root)!);
                    Directory.Move(legacy, root);
                    AppLog.Warn("DepotDownloader", "版本缓存已迁到统一缓存目录: " + root);
                }
                else if (Directory.Exists(legacy) && Directory.Exists(root)
                         && !Directory.EnumerateFileSystemEntries(legacy).Any())
                {
                    try { Directory.Delete(legacy); } catch { }
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("DepotDownloader", "版本缓存迁移失败: " + ex.Message);
            }
            return root;
        }
    }

    /// <summary>一条可下载的历史版本。ManifestId 为空 = 列表展示但需用户从 SteamDB 补 ID。</summary>
    public sealed record KnownVersion(
        string Label,
        string ManifestId,
        string? Version = null)
    {
        public bool HasManifest => !string.IsNullOrWhiteSpace(ManifestId);
        // 对齐玩家动力：下拉里主要显示版本号
        public string DisplayName => !string.IsNullOrEmpty(Version) ? Version : Label;
    }

    // 星露谷 Windows depot 413151 —— 从新到旧。Manifest 按 SteamDB depot 历史 + Wiki 发布日对齐：
    // https://stardewvalleywiki.com/Version_History （2026-09 对照补全 1.01–1.3.32 等 14 个缺失版本，
    // 并修正 1.1 / 1.2.26 / 1.3.27 / 1.3.28 / 1.3.33 / 1.5.1 六处日期错位的映射）。
    // 不收录：compatibility / monogame64bit / beta 分支构建，以及 Wiki 无版本号的同日中间构建
    // （如 1.6.4 的 4-19 热修、2016-02-29 一天 9 个构建）。1.07a 只新增了 Mac/Linux 支持，
    // Windows 内容与 1.07 完全相同、无独立 manifest，故不单列。
    private static readonly KnownVersion[] KnownStardewWindows =
    [
        new("正式版", "4278718763097142923", "1.6.15"),
        new("上一正式版", "1364246008775303529", "1.6.14"),
        new("历史版", "1289391404978285152", "1.6.13"),
        new("历史版", "7276192310056789702", "1.6.12"),
        new("历史版", "6985985228734128541", "1.6.11"),
        new("历史版", "3240670252057385108", "1.6.10"),
        new("历史版", "2836558896332251757", "1.6.9"),
        new("经典版", "1777024427851858279", "1.6.8"),
        new("历史版", "8241037280344201140", "1.6.7"),
        new("历史版", "1065152683462704684", "1.6.6"),
        new("历史版", "8895407084082948264", "1.6.5"),
        new("历史版", "3118431248827251876", "1.6.4"),
        new("历史版", "3423907493306588851", "1.6.3"),
        new("历史版", "7202829112632541182", "1.6.2"),
        new("历史版", "6093927695464368045", "1.6.1"),
        new("1.6 首发", "5012590689708703589", "1.6"),
        new("经典版", "5609262347030774375", "1.5.6"),
        new("历史版", "4397694255132373486", "1.5.5"),
        new("稳定旧版", "7802000804251603756", "1.5.4"),
        new("历史版", "4121989135652425382", "1.5.3"),
        new("历史版", "5396049550535566677", "1.5.2"),
        new("历史版", "4812928243273622870", "1.5.1"),
        new("历史版", "4487511898025325586", "1.5"),
        new("1.4 末版", "6307986820908740561", "1.4.5"),
        new("历史版", "7978993718867933207", "1.4.4"),
        new("历史版", "7258148177702857381", "1.4.3"),
        new("历史版", "8519233901628247204", "1.4.2"),
        new("历史版", "7149289726988606001", "1.4.1"),
        // 注意：这条就是星露谷 1.4 正式版（发布日 2019-11-26，与 Wiki 一致）。
        // 它的文件内部版本号(FIleVersion)是 1.3.7269 —— 1.4 全系列都没改内部版本号，
        // 不要因为内部版本号像 1.3 就把它当成 1.3 晚期（实测踩坑）。
        new("1.4 正式版", "2373680906867811602", "1.4"),
        new("1.3 末版", "7951557878765234474", "1.3.36"),
        new("历史版", "3086333938055749962", "1.3.33"),
        new("历史版", "684527103824506785", "1.3.32"),
        new("历史版", "6256862244170871577", "1.3.28"),
        new("1.3 首发（联机）", "3920107848374752907", "1.3.27"),
        new("1.2 末版", "5793210319202900873", "1.2.33"),
        new("历史版", "1612680387557367797", "1.2.32"),
        new("历史版", "1627658297112725452", "1.2.31"),
        new("历史版", "7102179406389569056", "1.2.30"),
        new("历史版", "3293327472846622645", "1.2.29"),
        new("新增六语言", "3227482562029885606", "1.2.26"),
        new("1.1 修补", "7487215307508292747", "1.11"),
        new("1.1", "2981425245818393618", "1.1"),
        new("历史版", "3230210310573566485", "1.07"),
        new("历史版", "3150973666024140120", "1.06"),
        new("历史版", "7430847287787667997", "1.051b"),
        new("历史版", "4973097789996737182", "1.051"),
        new("历史版", "285564730673029890", "1.05"),
        new("历史版", "7765407486833584479", "1.04"),
        new("历史版", "4594775194614467491", "1.03"),
        new("历史版", "8462477710223862747", "1.02"),
        new("历史版", "6198424258489893658", "1.01"),
        new("首发版", "8507975696251774427", "1.0"),
    ];

    public IReadOnlyList<KnownVersion> GetKnownVersions(string appId)
        => appId == "413150" ? KnownStardewWindows : Array.Empty<KnownVersion>();

    /// <summary>该 manifest 是否就是目录里最新的官方正式版（第一条）。部署它 = 与 Steam
    /// 当前最新版完全一致，appmanifest 只读锁毫无收益，只会把用户在 Steam 里换分支/
    /// 更新的写清单操作卡成「磁盘写入错误」。</summary>
    public static bool IsLatestOfficialManifest(string appId, string? manifestId)
    {
        if (appId != "413150" || string.IsNullOrWhiteSpace(manifestId)) return false;
        return string.Equals(KnownStardewWindows[0].ManifestId, manifestId, StringComparison.Ordinal);
    }

    /// <summary>
    /// 把 Steam 的拼接式分支名换成正常点号版本，让「按数字段比大小」这条路走得通。
    ///
    /// 为什么必须换：Steam 那边 1.0.1 这个分支叫 "1.01"、1.1.1 叫 "1.11"、1.0.5.1 叫 "1.051"
    /// （表里 1.11 的显示名就是「1.1 修补」）。不换算的话 1.11 被当成「1.11 版」，比 1.2 到 1.10 全都大
    /// —— 于是「用 1.11 启动」时 1.2–1.6.15 的档全被判成比它旧、读得了，一份都不收，
    /// 玩家点哪个都是进档闪退。实测 2026-09-22 有 10 个标签踩这条
    /// （1.01/1.02/1.03/1.04/1.05/1.051/1.051b/1.06/1.07/1.11），正是玩家报的「1.0.X 到 1.1.X 读不进存档」。
    ///
    /// 表里其余标签（1.2.26 起）本身就是正常点号写法，原样返回；认不出的字符串也原样返回
    /// （存档里写的 gameVersion 都是正常点号，只有表没收的如 1.5.7 会走这条路）。
    /// 1.051b 按表里的次序（在 1.051 之后、1.06 之前）给成 1.0.5.2。
    /// </summary>
    public static string? NormalizeVersionLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return label;
        return BranchVersions.TryGetValue(label.Trim(), out var canon) ? canon : label;
    }

    private static readonly Dictionary<string, string> BranchVersions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1.01"] = "1.0.1", ["1.02"] = "1.0.2", ["1.03"] = "1.0.3", ["1.04"] = "1.0.4",
        ["1.05"] = "1.0.5", ["1.051"] = "1.0.5.1", ["1.051b"] = "1.0.5.2",
        ["1.06"] = "1.0.6", ["1.07"] = "1.0.7", ["1.11"] = "1.1.1",
    };

    /// <summary>
    /// 版本表下标 = 发行时间序（0 = 最新）。标签能在表里认出来时，**只比下标**，
    /// 不要再拿 Steam 分支名当版本号做数字段比较 —— 那条路会得出 1.11 &gt; 1.6。
    /// 认不出的标签才落回 <see cref="NormalizeVersionLabel"/> + 数字段。
    /// </summary>
    public static bool TryGetVersionRank(string? label, out int rank)
    {
        rank = -1;
        if (string.IsNullOrWhiteSpace(label)) return false;
        foreach (var c in TableCandidates(label!))
        {
            for (var i = 0; i < KnownStardewWindows.Length; i++)
            {
                if (string.Equals(KnownStardewWindows[i].Version, c, StringComparison.OrdinalIgnoreCase))
                {
                    rank = i;
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>一个标签可能对应的表内写法：原样、拼接名换算后、去掉尾部 .0 后。</summary>
    private static IEnumerable<string> TableCandidates(string label)
    {
        var raw = label.Trim();
        yield return raw;
        var n = NormalizeVersionLabel(raw);
        if (!string.IsNullOrWhiteSpace(n)) yield return n!.Trim();
        var s = StripTrailingZeros(raw);
        if (!string.Equals(s, raw, StringComparison.OrdinalIgnoreCase)) yield return s;
        var ns = NormalizeVersionLabel(s);
        if (!string.IsNullOrWhiteSpace(ns) && !string.Equals(ns, s, StringComparison.OrdinalIgnoreCase))
            yield return ns!.Trim();
    }

    private static string StripTrailingZeros(string v)
    {
        var parts = v.Split('.');
        var len = parts.Length;
        while (len > 1 && parts[len - 1].TrimStart('0') is "" or "0" or "00")
            len--;
        return string.Join('.', parts.Take(len));
    }

    /// <summary>比较两个版本标签。&lt;0 = a 更旧。两边都能进表时用表序；否则换算后按数字段比。绝不抛。</summary>
    public static int CompareVersionLabels(string? a, string? b)
    {
        if (TryGetVersionRank(a, out var ra) && TryGetVersionRank(b, out var rb))
            return rb.CompareTo(ra);   // 下标越小越新：a 更新 → 返回正
        var x = DottedSegments(NormalizeVersionLabel(a));
        var y = DottedSegments(NormalizeVersionLabel(b));
        for (var i = 0; i < x.Length; i++)
        {
            var c = x[i].CompareTo(y[i]);
            if (c != 0) return c;
        }
        return 0;
    }

    private static long[] DottedSegments(string? v)
    {
        var r = new long[4];
        var parts = (v ?? "").Split('.', '-', ' ', '_');
        for (var i = 0; i < r.Length && i < parts.Length; i++)
            if (long.TryParse(parts[i], out var n)) r[i] = n;
        return r;
    }

    /// <summary>合并内置 + 自定义；自定义按 Version 覆盖同名内置（补上缺失的 Manifest ID）。</summary>
    public List<KnownVersion> GetMergedVersions(string appId, IEnumerable<CustomHistoricalVersion>? custom)
    {
        var list = GetKnownVersions(appId).ToList();
        if (custom is null) return list;
        foreach (var c in custom)
        {
            if (string.IsNullOrWhiteSpace(c.ManifestId)) continue;
            var id = c.ManifestId.Trim();
            var ver = string.IsNullOrWhiteSpace(c.Version) ? null : c.Version.Trim();
            // 同版本号且内置缺 ID → 补上
            var idx = ver is null ? -1 : list.FindIndex(v => v.Version == ver && !v.HasManifest);
            if (idx >= 0)
            {
                var old = list[idx];
                list[idx] = old with
                {
                    ManifestId = id,
                    Label = string.IsNullOrWhiteSpace(c.Label) ? old.Label : c.Label.Trim(),
                };
                continue;
            }
            if (list.Any(v => v.ManifestId == id)) continue;
            list.Add(new KnownVersion(
                string.IsNullOrWhiteSpace(c.Label) ? "自定义" : c.Label.Trim(),
                id,
                ver));
        }
        return list;
    }

    public sealed record StagedPackage(string Label, string ManifestId, string Path, long SizeBytes, DateTime At, bool Complete);

    /// <summary>版本包的 Mods 抽屉里现存多少个条目（顶层计，回收站不算）。
    /// 返回 -1 = 数不动（占用/权限）：界面要写"数量读不出来"，绝不能写"0 个"——
    /// "0 个 mod"会被读成"放心删"，那是最坏的失败方向。</summary>
    public static int CountPackMods(string stagingDir)
    {
        try
        {
            var mods = Path.Combine(stagingDir, "Mods");
            if (!Directory.Exists(mods)) return 0;
            var n = 0;
            foreach (var e in Directory.EnumerateFileSystemEntries(mods))
            {
                var nm = Path.GetFileName(e);
                if (nm.Equals("junigrid_trash", StringComparison.OrdinalIgnoreCase)
                    || nm.Equals(".junigrid_trash", StringComparison.OrdinalIgnoreCase)) continue;
                n++;
            }
            return n;
        }
        catch { return -1; }
    }

    /// <summary>删版本包的确认正文。两个删除入口（版本管理弹窗、设置页存储）都调这一个函数 ——
    /// 那个文件夹里除了本体还躺着【该版本的 Mods 抽屉】，而 DeleteStagedPackage 是
    /// Directory.Delete(dir, true) 递归硬删、不进回收站；只说"下次要重下"会让人以为
    /// 大不了重下，实际是把这一版的 mod 一起删掉（2026-09-19 实测抽屉里躺着 113 项）。</summary>
    public static string DeletePackConfirmText(StagedPackage pkg)
    {
        var size = pkg.SizeBytes > 0 ? $"约 {ResumableDownload.FormatBytes(pkg.SizeBytes)}，" : "";
        var mods = CountPackMods(pkg.Path);
        if (mods > 0)
            return $"{pkg.Label} 的本地缓存{size}里面还存着这个版本的 {mods} 个 mod 文件夹" +
                   "（切走时被移进来的）。删了会一起没，不进回收站、无法撤销 —— " +
                   "本体下次能重下，mod 要重装。";
        if (mods < 0)
            return $"{pkg.Label} 的本地缓存{size}删除后不进回收站、无法撤销。" +
                   "这个版本的 Mods 抽屉现在读不出数量，里面可能正存着该版本的 mod，请先自行确认。";
        return $"{pkg.Label} 的本地缓存{size}删除后不进回收站、无法撤销；" +
               "下次切回该版本要重新下载这么多。";
    }

    private const string ManifestMetaName = ".junigrid-manifest";

    /// <summary>「这一版真的下完了」的标记。只有下载进程退出码 0（或导入/归档确认过完整）才写。
    /// 为什么需要：分片超时留下的半截包能骗过锚点校验（锚点只有 4 个文件），
    /// 切过去进档时才在 NPC 构造里空引用闪退。</summary>
    private const string CompleteMarkName = ".junigrid-complete";

    private static void TryWriteCompleteMark(string stagingDir)
    {
        try { File.WriteAllText(Path.Combine(stagingDir, CompleteMarkName), DateTime.Now.ToString("O")); }
        catch (Exception ex) { AppLog.Warn("DepotDownloader", "写入下载完成标记失败: " + ex.Message); }
    }

    /// <summary>这个目录有没有「已下完」的凭据。</summary>
    public static bool IsStagedPackageComplete(string stagingDir)
    {
        try { return File.Exists(Path.Combine(stagingDir, CompleteMarkName)); }
        catch { return false; }
    }

    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name.Trim())
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        var s = sb.ToString().Trim();
        return string.IsNullOrEmpty(s) ? "unknown" : s;
    }

    /// <summary>缓存目录名用版本号（如 1.5.4）；无版本号时退回 Manifest ID。</summary>
    private static string StagingDirFor(string appId, string depotId, string? version, string manifestId)
    {
        var key = !string.IsNullOrWhiteSpace(version) ? SanitizeFolderName(version!) : manifestId;
        return Path.Combine(StagingRoot, $"{appId}-{depotId}-{key}");
    }

    /// <summary>这个缓存包对应哪个发行号：优先按 manifest 查内置版本表（表里的 DisplayName
    /// 就是发行号），查不到退回目录名里那段版本。⚠ 不能用包内 exe 的文件版本 ——
    /// XNA 时代的 1.2.x 自报 1.0.61xx。</summary>
    private static string? DeployedLabelOf(string stagingDir)
    {
        var name = Path.GetFileName(stagingDir.TrimEnd(Path.DirectorySeparatorChar, '/'));
        var parts = name.Split('-');
        var folderKey = parts.Length >= 3 ? string.Join('-', parts.Skip(2)) : name;
        var manifest = ReadManifestMeta(stagingDir);
        var known = string.IsNullOrWhiteSpace(manifest)
            ? null : KnownStardewWindows.FirstOrDefault(k => k.ManifestId == manifest);
        return !string.IsNullOrWhiteSpace(known?.DisplayName) ? known!.DisplayName
             : !string.IsNullOrWhiteSpace(folderKey) ? folderKey : null;
    }

    private static void WriteManifestMeta(string stagingDir, string manifestId)    {
        try { File.WriteAllText(Path.Combine(stagingDir, ManifestMetaName), manifestId); } catch { }
    }

    private static string? ReadManifestMeta(string stagingDir)
    {
        try
        {
            var p = Path.Combine(stagingDir, ManifestMetaName);
            return File.Exists(p) ? File.ReadAllText(p).Trim() : null;
        }
        catch { return null; }
    }

    private static string? FindStagingDir(string manifestId)
    {
        if (!Directory.Exists(StagingRoot)) return null;
        foreach (var dir in Directory.EnumerateDirectories(StagingRoot))
        {
            var meta = ReadManifestMeta(dir);
            if (meta == manifestId) return dir;
            var name = Path.GetFileName(dir);
            if (name.EndsWith("-" + manifestId, StringComparison.Ordinal)) return dir;
        }
        return null;
    }

    public List<StagedPackage> ListStagedPackages()
    {
        var result = new List<StagedPackage>();
        try
        {
            if (!Directory.Exists(StagingRoot)) return result;
            foreach (var dir in Directory.EnumerateDirectories(StagingRoot))
            {
                var name = Path.GetFileName(dir);
                // SMAPI 共享池不是游戏版本包，不进列表
                if (name.StartsWith("_", StringComparison.Ordinal)) continue;

                var manifest = ReadManifestMeta(dir);
                if (!HasGameBinary(dir))
                {
                    var empty = !Directory.EnumerateFileSystemEntries(dir).Any();
                    if (empty && Directory.GetLastWriteTime(dir) < DateTime.Now.AddMinutes(-30))
                    {
                        try { Directory.Delete(dir, true); } catch { }
                    }
                    continue;
                }

                string label;
                // 展示名优先：内置表 / 文件夹名。**不要**在这里 TryReadGameVersion ——
                // 52 个包各读一遍 exe FileVersion，打开版本列表要卡很久（「未定位」半天）。
                if (!string.IsNullOrEmpty(manifest))
                {
                    var known = KnownStardewWindows.FirstOrDefault(k => k.ManifestId == manifest);
                    var parts = name.Split('-');
                    var folderKey = parts.Length >= 3 ? string.Join('-', parts.Skip(2)) : name;
                    if (known is not null) label = known.DisplayName;
                    else label = folderKey;
                }
                else
                {
                    var parts = name.Split('-');
                    if (parts.Length < 3) continue;
                    manifest = parts[^1];
                    if (!manifest.All(char.IsDigit)) continue;
                    label = parts.Length >= 3 ? string.Join('-', parts.Skip(2)) : manifest;
                }

                // 大小只读缓存；没有就当 0，**绝不**为列表页全量扫盘
                long size = ReadSizeCache(dir);
                if (size <= 0) size = 0;
                result.Add(new StagedPackage(label, manifest!, dir, size, Directory.GetLastWriteTime(dir),
                    IsStagedPackageComplete(dir)));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("DepotDownloader", "列出暂存包失败: " + ex.Message);
        }
        return result.OrderByDescending(p => p.At).ToList();
    }

    /// <summary>从暂存包读真实游戏版本（exe/dll FileVersion，如 1.3.7269.37809 → 1.3.7269）。</summary>
    private static string? TryReadGameVersion(string dir)
    {
        foreach (var name in new[] { "Stardew Valley.dll", "Stardew Valley.exe" })
        {
            try
            {
                var file = Path.Combine(dir, name);
                if (!File.Exists(file))
                {
                    var hit = Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault();
                    if (hit is null) continue;
                    file = hit;
                }
                var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(file);
                // FileVersion 优先：真实本体的 ProductVersion 是「1.6.15, , 24356, 」这种带逗号
                // 的形式，切 3 段会得到「1.6.15, , 24356,」—— 拿它去查 staging 目录、
                // 生成 _mods-orphan 目录名都对不上（UpdateService.ReadLocalGameVersion
                // 一直用 FileVersion，两边口径必须一致）。
                var raw = fvi.FileVersion ?? fvi.ProductVersion;
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var parts = raw.Split('.');
                return parts.Length >= 3 ? string.Join('.', parts.Take(3)) : raw;
            }
            catch { }
        }
        return null;
    }

    /// <summary>版本号转安全目录名（ProductVersion 里可能带 ", , 24356," 这类尾巴）。</summary>
    private static string SafeDirName(string raw)
        => string.Join("", raw.Split(Path.GetInvalidFileNameChars())).Trim();

    public bool DeleteStagedPackage(string manifestId)
    {
        try
        {
            var dir = FindStagingDir(manifestId);
            if (dir is null) return true;
            Directory.Delete(dir, true);
            AppLog.Warn("DepotDownloader", "已删除暂存包 manifest=" + manifestId);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("DepotDownloader", "删除暂存包失败: " + ex.Message);
            return false;
        }
    }

    /// <summary>认领一个「从别的机器 / U盘 / 网盘拷来的版本包」：校验本体 → 认出版本 → 补写 manifest
    /// 元数据 → 必要时搬进统一缓存目录。之后在版本管理里点它就能<b>离线</b>切换，完全不连 Steam。
    /// 治的是「CM 在国内连不上 → 历史版本永远下不来」这条死路：包只要在本地，就有路可走。</summary>
    public string ImportLocalPackage(string dir, string appId = "413150", string depotId = "413151")
    {
        if (string.IsNullOrWhiteSpace(dir)) throw new DepotException("没选目录。");
        var src = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        var root = Path.GetFullPath(StagingRoot);
        if (string.Equals(src, root, StringComparison.OrdinalIgnoreCase))
            throw new DepotException("请选某个版本文件夹（里面有 Stardew Valley.dll / .exe），不是缓存目录本身。");
        if (!Directory.Exists(src)) throw new DepotException("目录不存在：" + src);
        if (!HasGameBinary(src))
            throw new DepotException("这个目录里没有找到《Stardew Valley.dll》或《Stardew Valley.exe》，不像是游戏版本包。");

        var parts = Path.GetFileName(src).Split('-');
        var manifestId = parts.Length >= 3 && parts[^1].Length > 0 && parts[^1].All(char.IsDigit)
            ? parts[^1] : null;
        var ver = TryReadGameVersion(src);
        if (manifestId is null)
        {
            var known = KnownStardewWindows.FirstOrDefault(k =>
                !string.IsNullOrEmpty(k.Version) && string.Equals(k.Version, ver, StringComparison.OrdinalIgnoreCase));
            if (known is null)
                throw new DepotException((ver is null ? "读不出游戏版本号" : $"认不出 {ver} 对应的 manifest 号") +
                    $"。请把文件夹改名为 {appId}-{depotId}-<manifest 号> 再导入（manifest 号在版本管理里能看到）。");
            manifestId = known.ManifestId;
        }

        Directory.CreateDirectory(root);
        var target = Path.Combine(root,
            $"{appId}-{depotId}-{(string.IsNullOrWhiteSpace(ver) ? manifestId : SanitizeFolderName(ver!))}");
        if (!string.Equals(src, target, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(target))
                throw new DepotException("缓存里已经有同名版本包，先在版本管理里删掉它再导入：\n" + target);
            try { Directory.Move(src, target); }
            catch (IOException)
            {
                throw new DepotException("跨盘移动做不到。请把这个文件夹整个复制到：\n" + target + "\n复制完再点一次导入。");
            }
        }
        WriteManifestMeta(target, manifestId);
        try { File.Delete(Path.Combine(target, ".junigrid-size")); } catch { }   // 大小让它重算，别报旧的
        var label = ver ?? manifestId;
        // 导入前已经校验过本体在盘上；用户从别的机器/网盘拷来的整包按"完整"认。
        TryWriteCompleteMark(target);
        AppLog.Warn("DepotDownloader", $"离线导入版本包：{target} manifest={manifestId} label={label}");
        return label;
    }

    /// <summary>游戏本体文件：1.6 起是 Stardew Valley.dll（MonoGame），1.5.x 及更早是 XNA 的 Stardew Valley.exe。</summary>
    private static bool HasGameBinary(string dir)
        => File.Exists(Path.Combine(dir, "Stardew Valley.dll"))
           || Directory.EnumerateFiles(dir, "Stardew Valley.dll", SearchOption.AllDirectories).Any()
           || File.Exists(Path.Combine(dir, "Stardew Valley.exe"))
           || Directory.EnumerateFiles(dir, "Stardew Valley.exe", SearchOption.AllDirectories).Any();

    /// <summary>这个目录能不能当「同一个 manifest 的半截包」接着下？只认同一个 manifest
    /// 且已经有本体文件的目录 —— 目录名可能带版本号也可能带 manifest，判据不能靠名字。
    /// 判 false 就清空重下（换版本的包、或只剩个空壳时不能拿旧内容去续传）。</summary>
    public static bool CanResumeStagedPackage(string stagingDir, string manifestId)
    {
        try
        {
            return Directory.Exists(stagingDir) && HasGameBinary(stagingDir)
                && string.Equals(ReadManifestMeta(stagingDir)?.Trim(), manifestId, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    /// <summary>某个游戏版本对应的缓存包目录（「版本抽屉」就建在它下面）。本机没这个版本的包 = null。</summary>
    public static string? StagingDirForVersion(string? version) => FindStagingByVersionLabel(version);

    /// <summary>按真实版本号找 staging 目录（文件夹名后缀或包内 FileVersion）。</summary>
    private static string? FindStagingByVersionLabel(string? version)
    {
        if (string.IsNullOrWhiteSpace(version) || !Directory.Exists(StagingRoot)) return null;
        var v = version.Trim();
        foreach (var dir in Directory.EnumerateDirectories(StagingRoot))
        {
            if (Path.GetFileName(dir).StartsWith("_", StringComparison.Ordinal)) continue; // 共享池等内部目录
            var name = Path.GetFileName(dir);
            if (name.EndsWith("-" + v, StringComparison.OrdinalIgnoreCase)) return dir;
            var real = TryReadGameVersion(dir);
            if (real is not null && string.Equals(real, v, StringComparison.OrdinalIgnoreCase)) return dir;
        }
        return null;
    }

}
