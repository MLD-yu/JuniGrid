using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using SharpCompress.Archives;      // v1.6.3：rar/7z 托管解压（WriteToFile 扩展在此命名空间）
using SharpCompress.Common;

namespace JuniGrid.Services;

/// <summary>
/// Scans the Mods/ folder, parses each mod's manifest.json (including
/// Nexus UpdateKeys so mods can be update-checked), and performs
/// install / update / enable / disable / uninstall operations.
/// </summary>
public sealed partial class ModService
{
    /// <summary>解压 GBK 条目名的包要按 codepage 936 解（见 <c>ExtractToTemp</c>），而 .NET 的
    /// 非 ASCII 代码页是<b>可选包</b>，不注册就抛「No data is available for encoding 936」。
    /// 原来这句注册只写在 <see cref="DepotDownloaderService"/> 里，等于"mod 能不能装"取决于
    /// 那个服务有没有被先实例化过 —— 换构造顺序、换入口、或在别处直接用 ModService，
    /// 整条安装链就会以这句话报错。谁用谁注册。</summary>
    static ModService() => System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

    // ------------------------------------------------------------------
    // Scan
    // ------------------------------------------------------------------
    /// <summary>v1.1.4：原始扫描 —— 只枚举磁盘产出条目，不做任何判重。
    /// 物理多副本清理必须基于这份原始结果（Scan 的 UID 判重会把散装副本
    /// "藏"掉，清理就再也看不到它们了）。</summary>
    private List<ModEntry> ScanRaw(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return new List<ModEntry>();
        var modsDir = Path.Combine(gamePath, "Mods");
        if (!Directory.Exists(modsDir)) return new List<ModEntry>();

        // v1.09：.junigrid_trash 改为常驻回收站 —— 勾选"移入mod回收站"删除的 mod
        // 会留在这里等用户手动还原/清理，扫描启动时不再自动清空（只跳过不扫描）。
        // 清理入口收敛到 设置 → 存储 → 游戏卸载回收站。

        // v0.72.6：先物化目录列表 —— 批量启禁时目录在惰性枚举途中被改名（X ↔ .X），
        // 枚举器会直接抛 DirectoryNotFoundException 炸穿整个 Rescan（2026-08-29 错误墙根因之一）
        List<string> dirs;
        try { dirs = Directory.EnumerateDirectories(modsDir).ToList(); }
        catch (DirectoryNotFoundException) { return new List<ModEntry>(); }

        var results = new List<ModEntry>();
        var hidOn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hidOff = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dirs)
        {
            // v0.72.6：单个目录在扫描瞬间被改名/删除属合法竞态 —— 局部容错跳过该项，
            // 绝不让它中断整次扫描；IO/权限异常单独记录但不改变原有扫描结果语义（不整吞）
            try
            {
                // v0.52.0：回收站目录不参与扫描
                if (string.Equals(Path.GetFileName(dir), ".junigrid_trash", StringComparison.OrdinalIgnoreCase))
                    continue;
                // 老版转换留下的裸素材留底（当时留在 Mods\ 里）：不是 mod，也没有清单，
                // 列出来只会是一行让人误会的「无清单」。新留底已改放缓存根的 mods-backup。
                if (Path.GetFileName(dir).EndsWith("-raw-backup", StringComparison.OrdinalIgnoreCase))
                    continue;
                // v1.6.8：空壳目录清理 —— 实体文件已不存在的目录（版本切换/卸载残留），是
                // "无清单幽灵条目"的来源。零用户内容，直接清除并记日志，不再作为 mod 列出。
                // v1.7.37 放宽：原来要求"整个目录只剩 .junigrid.json 一个条目"，于是删掉
                // Ridgeside Village 的捆绑子包后，顶层还剩 安装标记 + changelog.txt 两样，
                // 谁都不满足 ⇒ 列表里留下一行「无清单」还带个能点的开关（用户实测截图）。
                // 现在认的是"有我们的安装标记 + 下面没有任何子目录 + 没有 manifest.json
                // + 没有 dll"，剩下的只可能是作者留的说明文件。
                // ⚠ 不能只看"没有 manifest"就删：Portraiture 的素材包（Portraits/<名字>/*.png）
                // 本来就没有 manifest.json，它靠【子目录】区分于空壳，所以子目录一条不能少。
                if (Directory.Exists(dir))
                {
                    var shellEntries = Directory.GetFileSystemEntries(dir);
                    bool hasMarker = shellEntries.Any(e =>
                        Path.GetFileName(e).Equals(".junigrid.json", StringComparison.OrdinalIgnoreCase));
                    bool noSubDir = !shellEntries.Any(Directory.Exists);
                    bool noPayload = !File.Exists(Path.Combine(dir, "manifest.json"))
                        && !Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories).Any();
                    if (hasMarker && noSubDir && noPayload)
                    {
                        var left = shellEntries.Select(Path.GetFileName);
                        AppLog.Warn("Mods", $"[空壳清理] {Path.GetFileName(dir)}（仅剩安装标记与说明文件："
                            + string.Join("/", left.Where(n => !n.Equals(".junigrid.json", StringComparison.OrdinalIgnoreCase)))
                            + "）已移除");
                        try { Directory.Delete(dir, true); }
                        catch (Exception ex)
                        { AppLog.Warn("Mods", $"空壳清理失败: {ex.Message}"); }
                        continue;
                    }
                }
                // v1.1.4：完全空目录直接跳过 —— 捆绑包子包删空后的顶层空壳不产生
                // 孤儿条目、不进列表（Uninstall 已顺手删壳，这里兜住手删等其它来源）
                bool isEmpty;
                try { isEmpty = !Directory.EnumerateFileSystemEntries(dir).Any(); }
                catch { isEmpty = false; }
                if (isEmpty) continue;
                // . 开头的文件夹是"禁用标记目录"（禁用时改名 .X 产生）。
                // 不能跳过：必须把它收进来、标成 Disabled，UI 才能显示“已禁用”并可重新启用。
                // （v0.42.0 曾用 continue 跳过，导致禁用的 mod 直接从列表消失、再也启用不了 —— 已回退）
                var manifest = Path.Combine(dir, "manifest.json");
                if (!File.Exists(manifest))
                {
                    var nested = Directory
                        .EnumerateFiles(dir, "manifest.json", SearchOption.AllDirectories)
                        .OrderBy(f => f.Length)
                        .ToList();
                    // 一个文件夹里可能装了多个子 mod（Content Pack 分包很常见），
                    // 每个 nested manifest 都当成一个 mod 收进来
                    if (nested.Count == 0)
                    {
                        // 连一层 manifest 都没有 → 用文件夹名兜底显示，避免整个 mod 消失
                        results.Add(OrphanEntry(modsDir, dir));
                        continue;
                    }
                    foreach (var nm in nested)
                    {
                        var e = BuildModEntry(modsDir, dir, nm);
                        if (e is not null) { results.Add(e); continue; }
                        // v1.06.4：manifest 存在但是空文件/解析失败 → 也必须兜底收进来。
                        // 之前直接丢弃会让整个包从列表隐身：「全部禁用」碰不到它（文件夹不加
                        // 点前缀），SMAPI 却照样扫，日志里刷一屏 Skipped mods（East Scarp
                        // REMASTERED 等四个大包整包 manifest 为 0 字节的根因）。
                        results.Add(OrphanEntry(modsDir, Path.GetDirectoryName(nm)!,
                            "⚠ manifest.json 为空或无法解析（建议重装该 mod）"));
                    }
                    continue;
                }

                var folderName = Path.GetFileName(dir);
                var entry = BuildModEntry(modsDir, dir, manifest, folderName);
                if (entry is not null)
                {
                    results.Add(entry);
                    // 顶层自带 manifest 的捆绑包：Scan 只为顶层出一条目，下一层子包的 UniqueID
                    // 因此不在列表里 —— 但 SMAPI 照样加载它们，依赖它们的 mod 不能算"缺依赖"。
                    // 这里只把 UID 记进隐藏子集（不产生行、不动磁盘），实测多走一层约 70ms。
                    CollectHiddenChildUids(dir, entry.UniqueID, folderName.StartsWith('.'), hidOn, hidOff);
                }
                else
                {
                    // manifest 为空 / 无法解析 → 用文件夹兜底，标记为不可识别，别让 mod 消失
                    results.Add(OrphanEntry(modsDir, dir));
                }
            }
            catch (DirectoryNotFoundException) { continue; }
            catch (IOException ioe) { AppLog.Warn("Mods", "扫描跳过(IO): " + Path.GetFileName(dir) + " - " + ioe.Message); continue; }
            catch (UnauthorizedAccessException) { AppLog.Warn("Mods", "扫描跳过(无权限): " + Path.GetFileName(dir)); continue; }
        }
        HiddenChildUids = (hidOn, hidOff);
        return results;
    }

    /// <summary>本次扫描发现的「不列进列表的子包 UniqueID」（启用中 / 随顶层一起被禁用）。
    /// 只在 Scan/ScanRaw 跑过后有意义 —— 列表侧的「已装 UID 集合」要并进来它才不会误报缺依赖。</summary>
    public (HashSet<string> Enabled, HashSet<string> Disabled) HiddenChildUids { get; private set; }
        = (new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>顶层自带 manifest 的包，往下再看一层把子包 UID 收进隐藏集合。
    /// 只读 UID、不建条目；子包与顶层同 UID（包内又塞了一份）时不收，免得把判重逻辑绕晕。</summary>
    private static void CollectHiddenChildUids(string topDir, string? ownUid, bool topDisabled,
        HashSet<string> on, HashSet<string> off)
    {
        foreach (var sub in Directory.EnumerateDirectories(topDir))
        {
            var name = Path.GetFileName(sub);
            if (name.StartsWith('.') || name.Equals("assets", StringComparison.OrdinalIgnoreCase)) continue;
            var mf = Path.Combine(sub, "manifest.json");
            if (!File.Exists(mf)) continue;
            string uid;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(mf));
                uid = doc.RootElement.TryGetProperty("UniqueID", out var u) ? (u.GetString() ?? "").Trim() : "";
            }
            catch { continue; }   // 子目录里的坏 manifest 由 BuildModEntry/兜底那套说话，这里不重复报
            if (string.IsNullOrWhiteSpace(uid)) continue;
            if (!string.IsNullOrWhiteSpace(ownUid) && uid.Equals(ownUid, StringComparison.OrdinalIgnoreCase)) continue;
            (topDisabled ? off : on).Add(uid);
        }
    }

    public IReadOnlyList<ModEntry> Scan(string gamePath)
    {
        var results = ScanRaw(gamePath);
        // v1.1.5：回收站若已存在，扫描时即补隐藏属性与运行期保护锁（不存在则不
        // 主动创建——没有回收站就无需保护，首次卸载/暂存时才由 EnsureTrashReady 建）。
        try
        {
            var trash = StoragePaths.GameTrashDir(gamePath);
            if (Directory.Exists(trash)) ProtectTrash(trash);
        }
        catch { }
        // v1.08：UniqueID 判重 —— 同一个 mod 的禁用副本（.X）与启用副本（X）并存时
        // 只显示一份（常见于：旧副本被禁用后又重新下载/重装了新副本）。规则：优先保留
        // 启用的那份；同为启用/禁用则保留版本号高的。被隐藏的副本留在磁盘不动，不删文件。
        var byUid = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<ModEntry>();
        foreach (var e in results)
        {
            var uid = e.UniqueID?.Trim();
            if (string.IsNullOrWhiteSpace(uid)) { ordered.Add(e); continue; }
            if (byUid.TryGetValue(uid, out var prev))
            {
                ModEntry keep = prev, drop = e;
                var prevBetter = !prev.Disabled && e.Disabled;
                var dropBetter = prev.Disabled && !e.Disabled;
                if (dropBetter) { keep = e; drop = prev; }
                else if (!prevBetter && !dropBetter)
                {
                    var vp = Version.TryParse((prev.Version ?? "").TrimStart('v', 'V'), out var a) ? a : null;
                    var ve = Version.TryParse((e.Version ?? "").TrimStart('v', 'V'), out var b) ? b : null;
                    if (ve is not null && (vp is null || ve > vp)) { keep = e; drop = prev; }
                }
                // v1.1.4：重写去重落地 —— 旧写法 ordered.Remove(drop)+Add(keep) 有两个 bug：
                // keep==prev 时把已在列表的 prev 又 Add 一遍（@key 崩溃墙源头）；按位替换
                // 时 Remove 先行会让 IndexOf(prev) 变 -1（三副本换保场景越界崩溃）。
                // 现在语义：输家不入列；赢家如果是新条目，就地在列表里顶掉旧条目。
                if (ReferenceEquals(keep, e))
                {
                    var idx = ordered.IndexOf(prev);
                    if (idx >= 0) ordered[idx] = e;
                    else ordered.Add(e);
                }
                // keep==prev：e 直接不入列，列表不动
                byUid[uid] = keep;   // 锚点跟进到当前胜者，后续副本与胜者比较
                AppLog.Warn("Mods", $"[判重] UniqueID {uid} 存在多份：显示 {keep.Folder}，隐藏 {drop.Folder}");
            }
            else
            {
                byUid[uid] = e;
                ordered.Add(e);
            }
        }
        // v1.1.2：Folder 兜底判重 —— UniqueID 为空的条目（Content Pack 子包、manifest
        // 解析失败的 OrphanEntry）不参与上面按 Uid 的判重，竞态/重装场景下可能产生
        // 同一相对路径两条。列表 UI 用 Folder 当 @key，重复会直接炸整页渲染。
        var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduped = new List<ModEntry>(ordered.Count);
        foreach (var e in ordered)
        {
            if (seenFolders.Add(e.Folder)) deduped.Add(e);
            else AppLog.Warn("Mods", $"[判重] Folder {e.Folder} 重复，已隐藏多余条目");
        }
        return deduped;
    }

    /// <summary>
    /// v1.1.5：同 UniqueID 多副本的物理清理 —— 子包级语义（对齐 SMAPI：SMAPI 只跳过
    /// 重复子包，不会端走整个捆绑包）。必须基于原始扫描（ScanRaw）：Scan 的 UID 判重
    /// 会先一步把散装副本从列表藏掉。
    /// 规则（与 fable-5.1 共同敲定）：
    ///   1.1 只处理启用条目（.X 禁用副本 SMAPI 本来就跳过，无冲突）；
    ///   1.2 同一 UID 跨多个顶层文件夹时，保留"子包最多的顶层"为赢家，输家顶层里
    ///       **只有命中 UID 的子包目录**移入回收站（保留层级），unique 子包一律不动；
    ///   1.3 赢家内部的同 UID 重复子包不动（SMAPI 自己会报冲突，让用户自行处理）；
    ///   1.4 平局 tie-break：子包数 → 主 manifest 版本（顶层根 manifest，其次字典序
    ///       第一个子包的版本，都解析不出视为最旧）→ 顶层名字母序；
    ///   1.5 禁用条目不参与分组，自然不会被动。
    /// 旧版"输家顶层整体移入"规则会把与赢家共享任一公共子包 UID 的其他捆绑包整个
    /// 端走（含 unique 子包）——跨捆绑包共享公共库/内容包场景实测误删。
    /// 返回移入回收站的目录数。调用方随后需要重扫。
    /// </summary>
    /// <summary>v1.1.8：*.tmp 清扫的闸门 —— 那是一次整棵 Mods 树的递归遍历
    /// （实测 12500 个文件 / 358ms，占掉一次重扫的七成）。它清的是"进程被硬杀时
    /// 原子写 manifest 留下的半截临时文件"，一个进程开头扫一次就够；每次启禁用、
    /// 每次文件事件都再遍历一遍，就是把磁盘白烧在用户最等结果的那一下上。</summary>
    private static int sTmpSwept;

    public int CleanupDuplicateCopies(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return 0;
        // v1.1.5：顺带清扫孤儿 .tmp —— 原子写 manifest 的临时文件在进程被硬杀时
        // 会残留（finally 来不及执行）。正规 mod 不会携带 .tmp 文件，24 小时阈值
        // 兜底绝不误删"正在写一半"的临时文件。
        if (Interlocked.Exchange(ref sTmpSwept, 1) == 0)
            try
            {
                var tmpCutoff = DateTime.UtcNow.AddHours(-24);
                foreach (var tmp in Directory.EnumerateFiles(Path.Combine(gamePath, "Mods"), "*.tmp", SearchOption.AllDirectories))
                    if (File.GetLastWriteTimeUtc(tmp) < tmpCutoff)
                    { try { File.Delete(tmp); } catch { } }
            }
            catch { }
        var raw = ScanRaw(gamePath);
        var removed = 0;
        var groups = raw
            .Where(m => !m.Disabled && !string.IsNullOrWhiteSpace(m.UniqueID))
            .GroupBy(m => m.UniqueID.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(x => x.Folder.Split('/')[0])
                         .Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);
        foreach (var g in groups)
        {
            var tops = g.GroupBy(x => x.Folder.Split('/')[0], StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(t => t.Count())
                        .ThenByDescending(t => MainVersionOf(gamePath, t))
                        .ThenBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
                        .ToList();
            if (tops.Count < 2) continue;
            var keep = tops[0].Key;
            foreach (var top in tops.Skip(1))
            {
                foreach (var loser in top)
                {
                    var dir = Path.Combine(gamePath, "Mods", loser.Folder.Replace('/', Path.DirectorySeparatorChar));
                    if (!Directory.Exists(dir)) continue;
                    try
                    {
                        var grave = loser.Folder.Contains('/')
                            ? StageSubpackageToTrash(gamePath, loser.Folder)
                            : StageExistingToTrash(gamePath, dir);
                        removed++;
                        AppLog.Warn("Mods", $"[判重清理] 子包 {loser.Folder} 与顶层 {keep} 的子包 UID 重复，已移入回收站：{Path.GetFileName(grave)}");
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn("Mods", $"[判重清理] {loser.Folder} 移入回收站失败（可能被占用），保留原样: {ex.Message}");
                    }
                }
                // 2.2 输家顶层被清空才删壳；部分残留（README/config/共享资源）保守保留并记日志
                TryRemoveEmptyTopShell(gamePath, top.Key);
                var topDir = Path.Combine(gamePath, "Mods", top.Key);
                if (Directory.Exists(topDir) && Directory.EnumerateFileSystemEntries(topDir).Any())
                    AppLog.Warn("Mods", $"[判重清理] 顶层 {top.Key} 的重复子包已清理，壳目录保留（含 "
                        + Directory.GetFileSystemEntries(topDir, "*", SearchOption.AllDirectories).Length
                        + " 个文件/子目录），可自行决定去留");
            }
        }
        return removed;
    }

    /// <summary>1.4 平局用的"主 manifest 版本"：顶层根目录有 manifest.json 用它；否则取
    /// 字典序第一个子包条目的版本；都解析不出返回 null（排序时视为最旧）。</summary>
    private static Version? MainVersionOf(string gamePath, IGrouping<string, ModEntry> top)
    {
        try
        {
            var rootMf = Path.Combine(gamePath, "Mods", top.Key, "manifest.json");
            if (File.Exists(rootMf))
            {
                var text = ReadManifestText(rootMf);
                using var doc = System.Text.Json.JsonDocument.Parse(text, ManifestJson);
                if (doc.RootElement.TryGetProperty("Version", out var v)
                    && v.ValueKind == System.Text.Json.JsonValueKind.String
                    && Version.TryParse((v.GetString() ?? "").TrimStart('v', 'V'), out var ver))
                    return ver;
                return null;
            }
            var firstFolder = top.Select(x => x.Folder)
                                 .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                                 .First();
            var entry = top.First(x => x.Folder == firstFolder);
            return Version.TryParse((entry.Version ?? "").TrimStart('v', 'V'), out var v2) ? v2 : null;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------
    // Enable / disable / uninstall
    // ------------------------------------------------------------------
    /// <summary>Disabling = prefixing the folder with a dot (SMAPI skips those).
    /// v1.1.5：子包级启禁 —— "Top/Sub" 只改末段（Top/Sub ↔ Top/.Sub）。SMAPI 跳过
    /// 任意层级的 . 前缀目录（与顶层 .X 同一约定），Scan 的 Disabled 判定本来就包含
    /// Contains("/.")。独立 mod（无子路径）行为不变：整层改名，不会劈目录、不会残留空壳。
    /// v1.7.1：启用时把被禁用的祖先目录一并解开 —— 父级 ".SVE" 下的子包末段本来就没点，
    /// 只改末段等于空操作（toast 报成功、列表仍显示已禁用）。只有批量「选中全部→启用」
    /// 走的是顶层改名才碰巧成功。禁用只点当前段，不动祖先。</summary>
    public string? SetDisabled(string gamePath, string folderName, bool disabled)
    {
        try
        {
            var modsDir = Path.Combine(gamePath, "Mods");
            // v1.1.5：拆分父目录与末段 —— 子包（"Top/Sub"）只改末段，独立 mod 整层改名
            var parts = folderName.Replace('\\', '/').Split('/');
            var name = parts[^1];
            var parent = parts.Length == 1
                ? modsDir
                : Path.Combine(modsDir, string.Join(Path.DirectorySeparatorChar, parts, 0, parts.Length - 1));
            var src = Path.Combine(parent, name);
            if (!Directory.Exists(src) && !disabled && !name.StartsWith('.'))
            {
                // 启用容错：调用方传的是不带点的旧名，但磁盘上实际是 .X
                var alt = Path.Combine(parent, "." + name);
                if (Directory.Exists(alt)) { name = "." + name; src = alt; }
            }
            if (!Directory.Exists(src)) return LocService.Tr("找不到 Mod 文件夹");

            // 启用时先解开被禁用的祖先（.Stardew Valley Expanded/X → Stardew Valley Expanded/X）。
            // 逐段从顶往下改名；任一祖先失败则中止，避免半开状态让 SMAPI 仍整棵跳过。
            if (!disabled)
            {
                for (var i = 0; i < parts.Length - 1; i++)
                {
                    if (!parts[i].StartsWith('.')) continue;
                    var ancParent = i == 0
                        ? modsDir
                        : Path.Combine(modsDir, string.Join(Path.DirectorySeparatorChar, parts, 0, i));
                    var ancSrc = Path.Combine(ancParent, parts[i]);
                    var ancDest = Path.Combine(ancParent, parts[i].TrimStart('.'));
                    if (!Directory.Exists(ancSrc)) continue;
                    if (Directory.Exists(ancDest))
                    {
                        // 目标已存在 = 启用态副本与禁用态并存，旧的挪回收站再改名
                        var trash = EnsureTrashReady(gamePath);
                        var grave = Path.Combine(trash,
                            parts[i].TrimStart('.') + "-" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                        Directory.Move(ancDest, grave);
                        AppLog.Warn("Mods", $"[判重清理] 祖先重复副本 {ancDest} 已移入回收站");
                    }
                    for (var attempt = 1; ; attempt++)
                    {
                        try { Directory.Move(ancSrc, ancDest); break; }
                        catch (IOException) when (attempt < 4) { Thread.Sleep(150 * attempt); }
                        catch (UnauthorizedAccessException) when (attempt < 4) { Thread.Sleep(150 * attempt); }
                    }
                    parts[i] = parts[i].TrimStart('.');
                    AppLog.Info("Mods", $"[祖先启用] {ancSrc} → {ancDest}");
                }
                // 祖先改名后父路径变了，重算 src
                parent = parts.Length == 1
                    ? modsDir
                    : Path.Combine(modsDir, string.Join(Path.DirectorySeparatorChar, parts, 0, parts.Length - 1));
                src = Path.Combine(parent, name);
                if (!Directory.Exists(src) && !name.StartsWith('.'))
                {
                    var alt = Path.Combine(parent, "." + name);
                    if (Directory.Exists(alt)) { name = "." + name; src = alt; }
                }
                if (!Directory.Exists(src)) return LocService.Tr("找不到 Mod 文件夹");
            }

            var targetName = disabled
                ? (name.StartsWith('.') ? name : "." + name)
                : name.TrimStart('.');

            if (targetName == name) return null;

            var dest = Path.Combine(parent, targetName);
            if (Directory.Exists(dest))
            {
                // v1.08：目标已存在 = 同一 mod 的重复副本（如禁用的 .X 与新装的 X 并存）。
                // 把旧的重复副本挪进 .junigrid_trash（不真删，可找回），再完成本次改名。
                // v1.1.5：子包路径保留捆绑包层级（与 Uninstall/子包级清理一致）。
                string grave;
                if (parts.Length > 1)
                {
                    var rel = string.Join('/',
                        parts.Take(parts.Length - 1).Concat(new[] { targetName.TrimStart('.') }));
                    grave = StageSubpackageToTrash(gamePath, rel);
                }
                else
                {
                    var trash = EnsureTrashReady(gamePath);
                    grave = Path.Combine(trash,
                        targetName.TrimStart('.') + "-" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                    Directory.Move(dest, grave);
                }
                AppLog.Warn("Mods", $"[判重清理] 重复副本 {targetName} 已移入回收站（{Path.GetFileName(grave)}）");
            }
            // v0.44.0：src 和 dest 都在同一个 Mods 目录下，Directory.Move 是纯元数据
            // 重命名（瞬时，不复制内容）。原 MoveDirectorySafe 遇占用会走"复制+删源"，
            // 大 mod 要搬几百 MB → 批量启禁巨慢的根因。改用瞬时改名，占用时让 Windows
            // 直接报错，由调用方提示。
            // v1.1.4：瞬时占用自动重试 —— 列表行的封面图是 WebView2 直接从 mod 文件夹
            // 加载的，点启禁的瞬间句柄可能还没释放，改名会偶发 IOException（多分包
            // Downtown-Zuzu-main 启禁报"正被占用"的根因）。这种锁是毫秒级的，短重试即过。
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    Directory.Move(src, dest);
                    return null;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(150 * attempt);
                }
                catch (UnauthorizedAccessException) when (attempt < 4)
                {
                    Thread.Sleep(150 * attempt);
                }
            }
        }
        catch (Exception ex)
        {
            // v0.51.0：文件被占用时给中文提示，不再显示英文异常原文
            if (ex is IOException or UnauthorizedAccessException)
                return $"「{folderName}」正被占用，请先退出相关程序再{(disabled ? "禁用" : "启用")}";
            return ex.Message;
        }
    }

    /// <param name="toTrash">true = 移入 Mods/.junigrid_trash 常驻回收站（可手动还原）；
    /// false = 沿用旧"原子化"流程：先进回收站验证可删，再彻底删除。</param>
    public string? Uninstall(string gamePath, string folderName, bool toTrash = false, ConfigService? cfg = null)
    {
        try
        {
            // 禁用包磁盘名末段带 . 前缀（立绘页记录的是规范化无点名）—— 与 SetDisabled 的启用容错同款
            var modsDir = Path.Combine(gamePath, "Mods");
            var parts = folderName.Replace('\\', '/').Split('/');
            var parent = parts.Length == 1
                ? modsDir
                : Path.Combine(modsDir, string.Join(Path.DirectorySeparatorChar, parts, 0, parts.Length - 1));
            var dir = Path.Combine(parent, parts[^1]);
            if (!Directory.Exists(dir))
            {
                var alt = Path.Combine(parent, "." + parts[^1]);
                if (!Directory.Exists(alt)) return LocService.Tr("找不到 Mod 文件夹");
                dir = alt;
            }
            var trash = EnsureTrashReady(gamePath);
            var baseName = folderName.Replace('/', '_');
            string staging;
            if (toTrash)
            {
                // v1.09：回收站条目带时间戳 —— 删了"1"再装"1"再删，两份都保留互不覆盖；
                // 同一秒重名（批量删除同名 mod 理论上可能）再追加序号兜底
                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                // v1.1.4：捆绑包子包进回收站保留原始层级 —— Mods 里是
                // "Downtown-Zuzu-main/[BL] X"，回收站里也是 Downtown-Zuzu-main/[BL] X，
                // 同一捆绑包的子包聚在同一个顶层文件夹下，手动还原 = 把顶层拖回 Mods
                if (folderName.Contains('/'))
                {
                    staging = Path.Combine(trash, folderName.Replace('/', Path.DirectorySeparatorChar));
                    if (Directory.Exists(staging))
                        staging = staging + "_" + stamp;
                    for (var n = 2; Directory.Exists(staging); n++)
                        staging = staging + "_" + n;
                    Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
                }
                else
                {
                    staging = Path.Combine(trash, baseName + "_" + stamp);
                    for (var n = 2; Directory.Exists(staging); n++)
                        staging = Path.Combine(trash, $"{baseName}_{stamp}_{n}");
                }
            }
            else
            {
                staging = Path.Combine(trash, baseName + "_" + Guid.NewGuid().ToString("N")[..8]);
            }
            try
            {
                Directory.Move(dir, staging);   // 同盘瞬时改名，占用时这里直接抛异常
            }
            catch (Exception ex)
            {
                // 占用 → 还原（如果 staging 已部分移走就移回去）
                if (Directory.Exists(staging) && !Directory.Exists(dir))
                    try { Directory.Move(staging, dir); } catch { }
                if (ex is IOException or UnauthorizedAccessException)
                    return $"「{folderName}」正被占用，请先退出相关程序再删除";
                return ex.Message;
            }
            if (!toTrash)
            {
                // 移成功 → 从回收站彻底删
                try { Directory.Delete(staging, recursive: true); }
                catch (Exception ex) { AppLog.Warn("ModService", "回收站清理失败: " + ex.Message); }
                // v1.1.5：不再删除空的 .junigrid_trash 目录本体 —— v0.52.0 删空壳是
                // 为了避免 Explorer 里出现可见空目录，如今回收站带 Hidden|System 属性
                //（EnsureTrashReady）默认不可见，且运行期保护句柄会让删除必然失败。
            }
            // v1.1.4：多分包捆绑包删空后清掉顶层空壳 —— 卸载的是子包路径（如
            // "Downtown-Zuzu-main/[CC] X"），删完若顶层文件夹已空，把壳一并清掉，
            // 否则 Mods 里会留一个无 manifest 的孤儿空目录（SMAPI 也会报它没法加载）
            if (folderName.Contains('/'))
                TryRemoveEmptyTopShell(gamePath, folderName.Split('/')[0]);
            // 宿主都没了，覆盖记录留着只会变成"找不到宿主"的死账 + AppData 里永远清不掉的副本
            if (cfg is not null) PurgeOverlaysOf(folderName, cfg);
            return null;
        }
        catch (Exception ex)
        {
            // v0.47.0：文件被占用（如 Stardrop.exe 正在运行）时给人看得懂的提示
            if (ex is UnauthorizedAccessException or IOException)
                return $"「{folderName}」正被占用，请先退出相关程序再删除";
            return ex.Message;
        }
    }

    /// <summary>v1.1.4：子包卸载后清理顶层空壳。仅当顶层文件夹里已经没有任何
    /// 文件/子目录（真空）时才删；删不动（被占用）静默放弃，下次再清。</summary>
    private static void TryRemoveEmptyTopShell(string gamePath, string top)
    {
        try
        {
            var topDir = Path.Combine(gamePath, "Mods", top);
            if (!Directory.Exists(topDir)) return;
            if (Directory.EnumerateFileSystemEntries(topDir).Any()) return;
            Directory.Delete(topDir);
            AppLog.Warn("Mods", $"[空壳清理] 捆绑包子包已删空，顶层空目录 {top} 一并移除");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Mods", $"[空壳清理] {top} 清理失败（无碍，下次再清）: {ex.Message}");
        }
    }
}
