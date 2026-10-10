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

    /// <summary>游戏进程（SMAPI/游戏本体）是否存活 —— 与 LauncherService.AnyProcess 同判据的静态版。
    /// v1.1.6：GetProcessesByName 返回的 Process 各持一个句柄，用完即释放（本属性被备注同步
    /// 等后台任务反复调用，不释放会积累 finalizer 压力）。</summary>
    private static bool IsGameProcessAlive
    {
        get
        {
            return LauncherService.IsGameProcessRunning();
        }
    }

    // ------------------------------------------------------------------
    // v1.1.2：备注同步进 manifest.json —— 游戏内 GMCM 的 mod 标题读的就是
    // manifest 的 Name 字段，把它改成备注名，游戏里就显示中文名。
    // 原名先存进配置 ModOriginalNames，取消备注时还原。只动 Name，UniqueID 等
    // 其它字段一律不碰（更新检查、依赖关系全靠 UniqueID，不受影响）。
    // ------------------------------------------------------------------

    /// <summary>把备注写入（或从）指定 mod 的 manifest.json。remark 为空 = 还原原名。
    /// 返回 null 表示成功，否则为错误描述。skipAliveCheck：批量同步时由调用方在循环外
    /// 统一判过一次存活（逐个判 = 每个 mod 两次全系统进程枚举）。</summary>
    public string? ApplyRemarkToManifest(string gamePath, string folder, string remark,
        Dictionary<string, string> originalNames, bool skipAliveCheck = false)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gamePath) || string.IsNullOrWhiteSpace(folder))
                return LocService.Tr("路径无效");
            var manifestPath = Path.Combine(gamePath, "Mods", folder.Replace('/', '\\'), "manifest.json");
            if (!File.Exists(manifestPath)) return LocService.Tr("找不到 manifest.json");
            if (!skipAliveCheck && IsGameProcessAlive) return LocService.Tr("游戏运行中，暂不能修改清单");

            // Newtonsoft 宽松解析（SMAPI manifest 允许尾随逗号/注释），再规整写回
            var text = ReadManifestText(manifestPath);
            var root = Newtonsoft.Json.Linq.JObject.Parse(text);
            var currentName = root["Name"]?.Type == Newtonsoft.Json.Linq.JTokenType.String
                ? (string?)root["Name"] : null;
            if (string.IsNullOrEmpty(currentName)) return LocService.Tr("清单缺少 Name 字段");

            if (string.IsNullOrWhiteSpace(remark))
            {
                // 还原原名
                if (!originalNames.TryGetValue(folder, out var original)) return null;   // 没改过，无需还原
                root["Name"] = original;
                originalNames.Remove(folder);
            }
            else
            {
                // 存档原名（只在首次时记，mod 更新重置清单后仍以上次存档为准）
                if (!originalNames.TryGetValue(folder, out var original))
                    originalNames[folder] = currentName!;
                // v1.1.4：把备注真正写进 Name —— 之前只存档了原名、没赋值，
                // 写回的是原内容，manifest 永远不变（GMCM 一直显示英文名的根因）
                root["Name"] = remark;
            }

            WriteManifestAtomic(manifestPath, root.ToString(Newtonsoft.Json.Formatting.Indented));
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>扫描后批量对账：所有备注过的 mod，manifest 里的 Name 与备注不一致就补写
    /// （覆盖 mod 更新后清单被重置的情况）。静默执行，失败只记日志。</summary>
    public void SyncAllRemarks(string gamePath, ConfigService cfg)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gamePath) || IsGameProcessAlive) return;
            var changed = false;
            foreach (var kv in cfg.Current.ModRemarks)
            {
                var folder = kv.Key;
                if (folder.Contains("/.")) continue;   // 跳过禁用的 mod
                var manifestPath = Path.Combine(gamePath, "Mods", folder.Replace('/', '\\'), "manifest.json");
                if (!File.Exists(manifestPath)) continue;
                try
                {
                    var root = Newtonsoft.Json.Linq.JObject.Parse(ReadManifestText(manifestPath));
                    var name = root["Name"]?.Type == Newtonsoft.Json.Linq.JTokenType.String
                        ? (string?)root["Name"] : null;
                    if (name == kv.Value)
                    {
                        // 自愈：manifest 已是备注名、但原名记录丢了（旧版本只在内存记原名、
                        // 退出前没回写配置），列表括号里的原名因此消失、取消备注也无从还原。
                        // 真名已不可考，用文件夹名兜底补记（Automate 这类文件夹=原名的能完全复原）。
                        if (!cfg.Current.ModOriginalNames.ContainsKey(folder))
                        {
                            cfg.Current.ModOriginalNames[folder] = folder.Split('/').Last();
                            changed = true;
                        }
                        continue;   // 已同步
                    }
                    var err = ApplyRemarkToManifest(gamePath, folder, kv.Value, cfg.Current.ModOriginalNames,
                        skipAliveCheck: true);   // v1.1.6：存活已在循环外判过，别每个 mod 都枚举一遍进程
                    if (err is not null) AppLog.Warn("Mods", $"[备注同步] {folder}: {err}");
                    else changed = true;   // ApplyRemarkToManifest 改了 ModOriginalNames（内存），一并落盘
                }
                catch (Exception ex) { AppLog.Warn("Mods", $"[备注同步] {folder}: {ex.Message}"); }
            }
            if (changed) cfg.Save(cfg.Current);
        }
        catch { }
    }

    // ------------------------------------------------------------------
    // v1.1.4 回归修复（BUG-2）：manifest 读写的并发与原子性。
    // Scan 后台的 ReadAllText（FileShare.Read）握住文件时，备注写入的
    // WriteAllText 申请写访问会被立即拒绝 → IOException（E10 复现根因）；
    // 且 WriteAllText 原地清空覆盖，写一半进程被杀 = manifest 损坏（E11）。
    // 修法三保险：读侧宽松共享锁 + 写侧临时文件原子替换 + 短重试。
    // ------------------------------------------------------------------

    /// <summary>宽松共享锁读 manifest：允许读的同时被重命名/替换，Scan 不再撞备注写入。</summary>
    private static string ReadManifestText(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, System.Text.Encoding.UTF8);
        return sr.ReadToEnd();
    }

    /// <summary>原子写 manifest：先写临时文件再 File.Replace 式替换（带 4 次短重试），
    /// 任何时刻磁盘上的 manifest 要么是旧的要么是新的，绝无半截。</summary>
    private static void WriteManifestAtomic(string path, string content)
    {
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, content, new System.Text.UTF8Encoding(false));
            for (var i = 0; i < 4; i++)
            {
                try
                {
                    File.Move(tempPath, path, overwrite: true);
                    return;
                }
                // v1.1.5：IOException 与 UnauthorizedAccessException 都重试 —— 杀软对
                // manifest 内存映射的瞬间 Move 会抛 UAE（"拒绝访问"），属瞬时态；
                // 真只读目标重试 4 次后照样上抛报错，E9 语义不变（多耗约 300ms）。
                catch (Exception ex) when (i < 3 && ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(50 * (i + 1));
                }
            }
        }
        finally
        {
            // 替换失败时清理临时文件，不留垃圾
            if (File.Exists(tempPath))
                try { File.Delete(tempPath); } catch { }
        }
    }

    /// <summary>剥掉 SMAPI manifest 允许的注释（// 行注释 + /* */ 块注释，如 East Scarp
    /// 的自动生成 manifest）与尾随逗号，让 System.Text.Json 的严格解析不炸。
    /// SMAPI 走 Newtonsoft 默认容忍注释能加载，这里必须对齐同样的宽容度，
    /// 否则会把好 mod 误报成"manifest.json 已损坏"。
    /// v1.3.2：internal —— 裸图转换器与包解析都要用这份清洗；CP 生态的非严格 JSON
    /// content.json 同样会遇到 CP 生态的非严格 JSON（East Scarp 尾逗号+注释组合
    /// 让 Newtonsoft 都炸），整套 CP 包解析共用这一份清洗。
    /// v1.3.3：改两遍法 —— 单遍流式在「"值", ⏎ //注释 ⏎ }」场景会漏剥尾逗号
    ///（扫空白时被注释行开头的 / 截停），East Scarp 的 NPC 子文件全是这个写法，
    /// 结果 Newtonsoft 拿着带尾逗号的文本照样炸、整个包静默消失。</summary>
    /// <summary>SMAPI 的 manifest/content 方言允许注释与尾逗号。Newtonsoft 默认就容忍，
    /// System.Text.Json 要显式开这两项 —— 不必再自己扫一遍字符串。</summary>
    internal static readonly System.Text.Json.JsonDocumentOptions ManifestJson = new()
    {
        CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // ------------------------------------------------------------------
    // Install / update from zips
    // ------------------------------------------------------------------
    /// <summary>
    /// Installs a downloaded mod UPDATE zip, replacing the existing folder.
    /// The target keeps its old name (so a ".Disabled" prefix survives).
    /// </summary>
    public string? InstallUpdate(string gamePath, string targetFolderName, string zipPath,
        out string? newVersion, string? expectedUniqueId = null)
    {
        newVersion = null;
        string? temp = null;
        try
        {
            var manifest = ExtractToTemp(zipPath, "mod-update-", out temp);
            if (manifest is null) return LocService.Tr("压缩包里没找到 manifest.json");
            var modRoot = ResolveModRoot(temp, out var allManifests);

            // 安全锁：多 Mod 共用一个 GitHub 仓库时，latest release 可能是别的 Mod。
            // 校验 UniqueID 不符就放弃，绝不能覆盖错装。
            // v1.1.4：多分包捆绑包 —— 任一子包的 UID 匹配即视为正确更新包
            //（Downtown Zuzu 这类 7 子包同发布，期望 UID 只是其中一个）。
            if (expectedUniqueId is not null)
            {
                var found = false;
                foreach (var mf in allManifests)
                {
                    try
                    {
                        // v1.3.1：校验必须用宽松选项 —— 带块注释的 manifest（East Scarp
                        // 等）裸 JsonDocument.Parse 会炸并被 catch 跳过，导致
                        // UID 明明匹配却误报"下载的包不是这个 Mod"。
                        using var check = JsonDocument.Parse(ReadManifestText(mf), ManifestJson);
                        var uid = check.RootElement.TryGetProperty("UniqueID", out var u)
                            ? u.GetString() : null;
                        if (string.Equals(uid, expectedUniqueId, StringComparison.OrdinalIgnoreCase))
                        { found = true; manifest = mf; break; }
                    }
                    catch { }
                }
                if (!found)
                    return LocService.Tr("下载的包不是这个 Mod（发布仓库里含多个 Mod），已放弃安装防止装错");
            }

            // 更新的是<b>子包行</b>（targetFolderName 形如 "顶层/子包"）时，zip 根往往是整个捆绑包。
            // 直接把 modRoot 搬进子包路径，会把整个包塞成孙子目录 —— 子包顶层不再有 manifest.json，
            // SMAPI 认不出这个 mod，实测等于把它弄坏（2026-09-30 用户报"捆绑包被拆散、用不了"）。
            // 所以只搬与被更新子包同名的那一层；作者只发一个独立 mod（zip 里没这层）时退回整包。
            var leafName = Path.GetFileName(targetFolderName.TrimEnd('/', '\\'));
            if (targetFolderName.Contains('/') && leafName.Length > 0)
            {
                var pick = Path.Combine(modRoot, leafName);
                var pickManifest = Path.Combine(pick, "manifest.json");
                if (Directory.Exists(pick) && File.Exists(pickManifest))
                {
                    modRoot = pick;
                    manifest = pickManifest;   // 版本号也要读这一层的，否则回写的是别的子包的
                }
            }

            try
            {
                using var doc = JsonDocument.Parse(ReadManifestText(manifest));
                if (doc.RootElement.TryGetProperty("Version", out var v))
                    newVersion = v.GetString();
            }
            catch (Exception __ex) { AppLog.Warn("ModService", __ex.Message); }

            var dest = Path.Combine(gamePath, "Mods", targetFolderName);
            // v1.1.1：旧版不再直接删除 —— 先整体改名移入 .junigrid_trash（同盘原子改名，
            // 绝不出现"删一半"），再装新版；安装失败时原路移回 Mods。任何中断旧版都不丢。
            string? stagedOld = null;
            if (Directory.Exists(dest))
            {
                try { stagedOld = StageExistingToTrash(gamePath, dest); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { return $"「{targetFolderName}」正被占用，无法备份旧版，已放弃更新（旧版未动）"; }
            }
            try
            {
                MoveDirectorySafe(modRoot, dest);   // 跨盘保护
            }
            catch
            {
                // 清掉可能残缺的新版半成品，再把旧版原路移回；移不回去就留在回收站（可手动还原）
                try { if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true); } catch { }
                if (stagedOld is not null) RestoreStaged(stagedOld, dest);
                throw;
            }

            TryDelete(temp);
            if (stagedOld is not null)
                AppLog.Warn("Mods", $"[更新] 旧版已移入回收站保留：{Path.GetFileName(stagedOld)}（可在设置中还原或清理）");
            return null;
        }
        catch (Exception ex)
        {
            if (temp is not null) TryDelete(temp);
            return ex.Message;
        }
    }

    /// <summary>
    /// Installs a BRAND-NEW mod zip into Mods/. Folder name comes from the
    /// zip's inner folder, or the manifest Name when files sit at zip root.
    /// A name collision replaces the old folder (acts as an update).
    /// <para>v1.2.0：requireUniqueId 非空时，zip 内必须存在 UniqueID 与之（忽略大小写）
    /// 一致的 manifest 才允许安装 —— 一键装依赖按名称搜到的候选包以此防"装错 mod"。
    /// 不匹配返回 <see cref="UidMismatchError"/> 哨兵值，调用方换下一个候选。</para>
    /// </summary>
    public const string UidMismatchError = "uniqueid-mismatch";

    /// <summary>
    /// 判断一个"缺失依赖"其实是某个<b>已装 mod 包内的子模块</b>：作者段相同，且去掉作者段后
    /// 以「已装 UID 的名字段 + 分隔符」开头（<c>shurmash.LewdDew_Valley</c> → <c>shurmash.LewdDew_Valley_helper</c>）。
    /// 这类东西在 Nexus 上没有独立页面（实测搜 shurmash 只有 2 个 mod，没有 helper），
    /// 拿它去搜等于白费一次请求，再把人领到一个不存在的页面。命中返回那个已装 UID，否则 null。
    /// 多个候选取最长前缀；末字符必须是分隔符，否则 <c>A.Mod</c> 会误命中 <c>A.ModHelper</c>。
    /// </summary>
    public static string? BundledParentOf(string missingUid, IEnumerable<string> installedUids)
    {
        if (string.IsNullOrWhiteSpace(missingUid)) return null;
        var dot = missingUid.IndexOf('.');
        if (dot <= 0 || dot >= missingUid.Length - 1) return null;
        var author = missingUid[..dot];
        var rest = missingUid[(dot + 1)..];
        string? best = null;
        foreach (var raw in installedUids)
        {
            var uid = (raw ?? "").Trim();
            if (uid.Length == 0 || uid.Equals(missingUid, StringComparison.OrdinalIgnoreCase)) continue;
            var d = uid.IndexOf('.');
            if (d <= 0 || !string.Equals(uid[..d], author, StringComparison.OrdinalIgnoreCase)) continue;
            var head = uid[(d + 1)..];
            if (head.Length == 0 || !rest.StartsWith(head, StringComparison.OrdinalIgnoreCase)) continue;
            if (rest[head.Length] is not ('_' or '-' or '.')) continue;
            if (best is null || uid.Length > best.Length) best = uid;
        }
        return best;
    }

    // ------------------------------------------------------------------
    // 覆盖型包（汉化补丁这类没有 manifest.json、只往别的 mod 上盖文件的包）
    // ------------------------------------------------------------------

    private const string OverlayBackupSuffix = ".junigrid_backup";

    /// <summary>RelPaths = 文件在<b>包里</b>的相对路径；DstPaths 非 null 时是逐文件对应的<b>宿主内</b>
    /// 落点（汉化包把 zh.json 压在包根目录时要搬进 i18n/），null = 两边同路径。</summary>
    internal sealed record OverlayPlan(string Host, List<string> RelPaths, string PackRoot, List<string>? DstPaths = null);

    internal enum OverlayFail
    {
        None,
        /// <summary>包内文件都是光秃秃一层（裸 zh.json），没说宿主是谁。</summary>
        NoHostInPack,
        /// <summary>包内摆着好几个别的 mod 的目录（跨顶层，或顶层认不出而第二层有好几家），
        /// 而<b>不是每一家都唯一对上已装的 mod</b>：有的名字本机没装着，或两个目录对上同一行。
        /// 名字都点得出来，问题在【只盖一半会让这个包半生效】，所以整包不装。</summary>
        MultiHost,
        /// <summary>认出了宿主名，但用户没装它。</summary>
        HostNotInstalled,
        /// <summary>配置项汉化包点名的字段，好几家已装 mod 都声明了 ⇒ 认不出该盖谁。</summary>
        AmbiguousConfigHost,
        /// <summary>整包只有一个裸 json、宿主由包自己页面的前置认出来了、本机也装着它，
        /// 但这份 json 的条目跟宿主 i18n 里的对不上 ⇒ 它不是这个 mod 的汉化，盖上去不会生效。
        /// 与 <see cref="NoHostInPack"/> 分开说：那句是"我们认不出"，这句是"认出来了、内容不对"。</summary>
        DeclaredHostKeyMismatch,
        /// <summary>整包只有一个翻译 json，而它的条目在本机<b>没有任何</b>已装 mod 的 i18n 里过半出现
        /// ⇒ 它对应的那个 mod 没装（实测 45719/49340 是「UI Info Suite 2 <b>Alternative</b>」的汉化，
        /// 本机装的是原版 UIInfoSuite2，298 个键只撞得上 95 个）。与 <see cref="NoHostInPack"/>
        /// 分开说：那句是"我们认不出"，这句是"认得出没装着的是谁"。</summary>
        HostKeysUnmatched,
    }

    /// <summary>一次扫描的覆盖健康度汇总：ByHost 给列表徽标，StaleKeys 给清单弹窗逐行判定。</summary>
    public sealed record OverlayReport(
        Dictionary<string, (int Total, int Stale)> ByHost,
        HashSet<string> StaleKeys);

    /// <summary>宿主目录可能是 Mods/X，也可能是被禁用的 Mods/.X（SetDisabled 用点前缀）。
    /// 记录里统一存不带点的名字，找的时候两种都试 —— 与立绘页那条点名容错同款。</summary>
    /// <summary>宿主路径归一：统一分隔符、去首尾斜杠、去掉禁用改名的首段点号。
    /// 记录键、徽标分组、卸载连带清理三处必须用同一个形状，否则同一个宿主会分裂成两个键。</summary>
    public static string NormalizeOverlayHost(string host)
    {
        var s = host.Replace('\\', '/').Trim('/');
        return s.StartsWith('.') ? s[1..] : s;
    }

    /// <summary>记录键里的来源槽位（第三段）；同一个宿主文件上可以挂多条，靠它分组。</summary>
    public static string OverlaySlotOf(string key)
    {
        var i = key.LastIndexOf('|');
        return i < 0 ? key : key[(i + 1)..];
    }

    /// <summary>宿主目录：支持「顶层」和「捆绑包/子包」两种深度。禁用只把顶层改名（X → .X），
    /// 但每一段都试一次带点写法，别名捆绑包（.Bundle/Child）里的子包也能命中。</summary>
    private static string? ResolveHostDir(string gamePath, string host)
    {
        var rel = NormalizeOverlayHost(host);
        if (rel.Length == 0) return null;
        var cur = Path.Combine(gamePath, "Mods");
        foreach (var seg in rel.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var hit = Directory.Exists(Path.Combine(cur, seg)) ? seg
                : Directory.Exists(Path.Combine(cur, "." + seg)) ? "." + seg : null;
            if (hit is null) return null;
            cur = Path.Combine(cur, hit);
        }
        return Directory.Exists(cur) ? cur : null;
    }

    /// <summary>在已安装 mod 里找<b>唯一</b>一个叶子目录名等于 leaf 的子包（形如 捆绑包/子包）。
    /// 命中 0 个或多个都不返回 —— 猜错的表现是盖到隔壁同名子包上，宁可放弃。</summary>
    private static string? FindInstalledSubpackageByLeaf(string gamePath, string leaf)
    {
        string? found = null;
        var hits = 0;
        try
        {
            foreach (var topDir in Directory.EnumerateDirectories(Path.Combine(gamePath, "Mods")))
            {
                var topName = Path.GetFileName(topDir);
                if (topName.Equals(".junigrid_trash", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var child in Directory.EnumerateDirectories(topDir))
                {
                    if (!Path.GetFileName(child).Equals(leaf, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!File.Exists(Path.Combine(child, "manifest.json"))) continue;
                    hits++;
                    found = topName.TrimStart('.') + "/" + Path.GetFileName(child);
                }
            }
        }
        catch { return null; }
        return hits == 1 ? found : null;
    }

    private static string Sha256Of(string file)
    {
        try { return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))); }
        catch { return ""; }
    }

    /// <summary>
    /// 判定一个没有 manifest.json 的包要盖到哪些已装 mod 上，交回【每个宿主一份】计划。
    /// <b>只采信包自己的内部路径</b>：所有文件共享同一个第一层目录名，那个名字就是宿主。
    /// 包名像不像某个 mod 只用来给人看，不作为落盘依据 —— 猜错的表现是"装成功了但游戏里
    /// 毫无变化"，用户自己永远查不出来，所以判定不出时返回 null 让调用方放弃安装。
    /// 第一层对不上时再往下探一层：汉化包常把真宿主再套一层
    /// （"zLewdDewValley translation/LewdDew_Valley/…"，宿主其实是捆绑包里的子包）。
    /// 还不行才按<b>拼写</b>认一次（<see cref="FindInstalledHostBySpelling"/>）：包内那层目录
    /// 允许只是把宿主的名字抄得不太一样，判据仍是【已装 mod 自己声明的目录名/显示名】且唯一命中。
    /// <para>包里摆着<b>好几个</b>别的 mod 的目录（跨顶层，或在顶层之下并列）不再一律弃权：
    /// 每个目录名都逐字对上唯一一家已装 mod 时，那是给这几个 mod 一起用的包（实机 53406
    /// 「To The Moon (中文)」就是捆绑包两个子包的汉化），<b>每家各一份计划</b>；
    /// 有一个对不上、或两个目录对上同一行 ⇒ 报 <see cref="OverlayFail.MultiHost"/>，
    /// 因为只盖一半会让这个包半生效。</para>
    /// </summary>
    internal List<OverlayPlan>? ProbeOverlay(string gamePath, string tempDir, out OverlayFail fail, out string hostGuess,
        IReadOnlyList<NexusRequirement>? packRequirements = null)
    {
        fail = OverlayFail.None;
        hostGuess = "";
        string[] files;
        try { files = Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories); }
        catch { return null; }
        if (files.Length == 0) { fail = OverlayFail.NoHostInPack; return null; }

        var packRels = files
            .Select(f => Path.GetRelativePath(tempDir, f).Replace('\\', '/')).ToList();
        // 包根目录上摊着的说明文件不参与判定，也不写进宿主：实测 11209「Stardew Valley Expanded
        // -image translation-Chinese」17 个条目里 16 个在 [CP] Stardew Valley Expanded/ 下、
        // 第 17 个就是一个裸的「说明.txt」—— 旧写法因为"一半裸在根、一半埋在目录下"整包否决。
        var judged = packRels.Where(r => !IsPackDocFile(r)).ToList();
        var bareCount = judged.Count(r => !r.Contains('/'));
        if (judged.Count == 0) { fail = OverlayFail.NoHostInPack; return null; }
        if (bareCount == judged.Count)
        {
            // 所有文件都光秃秃躺在包根目录（实测 41314 / 18080 / 44611 整包就一个裸 zh.json）——
            // 下面每一层判据都要先看"包内那层目录名"，这里根本没有那层。只剩两条证据可用：
            // 这个包页面登记的前置、以及这份翻译自己的键是谁的键。
            if (SoleTranslationJson(tempDir, judged) is string bareJson)
            {
                var bp = ProbeTranslationOnlyPack(gamePath, tempDir, bareJson, packRequirements,
                    out fail, out hostGuess);
                return bp is null ? null : new List<OverlayPlan> { bp };
            }
            fail = OverlayFail.NoHostInPack;
            return null;
        }
        if (bareCount > 0)
        {
            // 一半裸在根、一半埋在目录下：包内路径自己就矛盾，不在这里替用户猜。
            fail = OverlayFail.NoHostInPack;
            return null;
        }

        string? top = null;
        var rel = new List<string>();
        var relNested = new List<string>();
        var tops = new List<string>();     // 包内出现过的顶层目录名（>1 = 一个包里含多个 mod 的目录）
        var subs = new List<string>();     // 顶层之下那层的不同目录名（多宿主贴图包：一层一个别的 mod）
        // 逐目录名收集"它自己名下有哪些文件"，供多宿主包一家一份计划用（见 ProbeOnePlanPerHostDir）
        var filesByTop = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var filesBySub = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            var r = Path.GetRelativePath(tempDir, f).Replace('\\', '/');
            if (IsPackDocFile(r)) continue;      // 说明文件：不参与判定，也不写进宿主
            var parts = r.Split('/');
            top ??= parts[0];
            AddDistinct(tops, parts[0]);
            if (parts.Length > 2 && !IsVanillaAssetLayoutDir(parts[1])) AddDistinct(subs, parts[1]);
            rel.Add(r[(top.Length + 1)..]);
            if (parts.Length > 2) relNested.Add(string.Join('/', parts[2..]));
            (filesByTop.TryGetValue(parts[0], out var tf) ? tf : filesByTop[parts[0]] = new List<string>())
                .Add(string.Join('/', parts[1..]));
            if (parts.Length > 2)
                (filesBySub.TryGetValue(parts[1], out var sf) ? sf : filesBySub[parts[1]] = new List<string>())
                    .Add(string.Join('/', parts[2..]));
        }
        if (top is null || rel.Count == 0) { fail = OverlayFail.NoHostInPack; return null; }
        hostGuess = top;
        // 跨好几个顶层目录：先问"是不是每一家都装着"（实机 53406 那种一个包里摆着捆绑包两个
        // 子包的汉化）。全对得上就一家一份计划；有一家对不上才报多宿主，把名字报出来让人自己分开发。
        if (tops.Count > 1)
        {
            if (ProbeOnePlanPerHostDir(gamePath, tempDir, tops, filesByTop, "") is List<OverlayPlan> topPlans)
                return topPlans;
            fail = OverlayFail.MultiHost; hostGuess = JoinHostNames(tops); return null;
        }
        if (ResolveHostDir(gamePath, top) is string exactDir)
        {
            // 顶层就是【已装的那个壳】：文件分住在几个子包里时，每个子包各记一条账。
            // 记在壳上等于记在一个没有行的宿主上（徽标、开关、还原全点不到，要等下次启动的
            // RelocateBundleTopOverlays 才搬进子包），实机汉化包 10342 就是这样隐身过。
            if (SplitShellPlanByBundleChild(exactDir, top, top, rel) is List<OverlayPlan> shellPlans)
                return shellPlans;
            return new List<OverlayPlan>
            {
                WithI18nHome(tempDir, new OverlayPlan(top, rel, top), exactDir)
            };
        }
        // 顶层名字是【捆绑包里某个子包的目录名】（宿主住在 Mods/壳/子包 这一层）：ResolveHostDir
        // 只找顶层，所以 SVE 这类捆绑包的汉化/贴图包以前一律判"认不出宿主"。
        // 实测 11209 的顶层就是 [CP] Stardew Valley Expanded —— 正是那个子包的目录名。
        // 这条不是拼写猜测：目录名逐字相等、且那个子包自己有 manifest，命中多个同样不返回。
        if (FindInstalledSubpackageByLeaf(gamePath, top) is string childHost
            && ResolveHostDir(gamePath, childHost) is string childDir)
        {
            hostGuess = childHost;
            return One(WithI18nHome(tempDir, new OverlayPlan(childHost, rel, top), childDir));
        }
        // 第二层【只有一个】名字时它才可能是真宿主（汉化包把真宿主再套一层的写法）；
        // 好几个名字 = 这包本来就是给好几个 mod 用的，第二层是候选宿主清单，不是宿主。
        if (subs.Count == 1 && relNested.Count == judged.Count
            && FindInstalledSubpackageByLeaf(gamePath, subs[0]) is string nested
            && ResolveHostDir(gamePath, nested) is string nestedDir)
        {
            hostGuess = nested;
            return One(WithI18nHome(tempDir, new OverlayPlan(nested, relNested, top + "/" + subs[0]), nestedDir));
        }
        // 拼写不同 ≠ 没装：汉化作者常把 N 网页号写进包内目录名。认出来了仍然只往【已存在】的
        // 宿主目录里盖，一个候选都对不上才说「你还没装它」。
        if (FindInstalledHostBySpelling(gamePath, top) is string bySpelling
            && ResolveHostDir(gamePath, bySpelling) is string spelledDir)
        {
            hostGuess = bySpelling;
            return One(WithI18nHome(tempDir, new OverlayPlan(bySpelling, rel, top), spelledDir));
        }
        // 走到这里：顶层名字谁也对不上，而第二层摆着好几个别的 mod 的名字。那些名字是【候选宿主
        // 清单】不是宿主（见下面 ProbeOnePlanPerHostDir 那一步），所以不许拿顶层那个名字当
        // 「你还没装的前置」报出去（实测 mod 1993 的文件 35407：顶层是包自己的标题
        // 「Mod Compatibility Textures」，真宿主是第二层的 Deep Woods Obelisk / Tractor Garage，
        // 而这两个都没装 ⇒ 旧写法会凭空捏一个不存在的「前置」）。
        // 名字对不上 ≠ 不是它的汉化包：作者常把【翻译自己的名字】写成包内顶层目录 ——
        // 实测 mod 43130 整包只有 `[CP] Elle's Seasonal Buildings-CN/i18n/zh.json`，
        // 而宿主目录叫 `[CP] Elle's Seasonal Buildings`（多个 -CN），拼写键也认不下它。
        // 这里【不截名字】：截法一定会被同作者的姊妹包打脸（Spring 的界面包盖到 Fall 的
        // 界面包上，这台机器上踩过）。改看两边各自声明的内容 —— 包里 config.<字段>.name
        // 点名了要翻译哪些配置项，宿主就得在自己写的 ConfigSchema / example_config 里
        // 把这些字段全声明出来。实测这台机器：154 个已装目录里只有 1 个有任何交集，
        // 且 47/47 全覆盖，第二名 0 个 —— 分辨力够，才敢拿来落盘。
        var wantCfg = PackDeclaredConfigFields(tempDir, top, rel);
        if (wantCfg.Count > 0)
        {
            var cfgHits = FindHostsByDeclaredConfigFields(gamePath, wantCfg);
            if (cfgHits.Count == 1 && ResolveHostDir(gamePath, cfgHits[0]) is string cfgDir)
            {
                hostGuess = cfgHits[0];
                return One(WithI18nHome(tempDir, new OverlayPlan(cfgHits[0], rel, top), cfgDir));
            }
            if (cfgHits.Count > 1)
            {
                fail = OverlayFail.AmbiguousConfigHost;
                hostGuess = JoinHostNames(cfgHits);
                return null;
            }
        }
        if (subs.Count > 1)
        {
            // 第二层那几个名字【每一家都装着】⇒ 这是给这几个 mod 一起用的包，一家一份计划。
            // 实机 53406「To The Moon (中文)」：包内两个目录就是已装捆绑包 Nullnnow.ToTheMoon
            // v1.0.0 的两个子包，旧写法在这里直接弃权，把一个完整的汉化拒成"该盖哪家你决定"。
            // 有一家对不上就整包不装 —— 只盖一半会让这个包半生效，而那正是这条链最怕的静默。
            if (ProbeOnePlanPerHostDir(gamePath, tempDir, subs, filesBySub, top + "/") is List<OverlayPlan> subPlans)
                return subPlans;
            fail = OverlayFail.MultiHost; hostGuess = JoinHostNames(subs); return null;
        }
        // 包内那层目录名谁也对不上 ⇒ 最后再问一遍这个包页面登记的前置、以及这份翻译自己的键
        // （实测 24232 包内那层就是 "i18n"、49587 那层是翻译自己的标题 —— 两个都不是宿主名）。
        // 只在路径判据全落空时才走到这里，所以"顶层目录名就是宿主"那条既有的路一条都没动。
        if (SoleTranslationJson(tempDir, judged) is string transJson)
        {
            var tplan = ProbeTranslationOnlyPack(gamePath, tempDir, transJson, packRequirements,
                out var tfail, out var tguess);
            if (tplan is not null) { fail = OverlayFail.None; hostGuess = tguess; return One(tplan); }
            // 翻译包那条通道报得出【具体是谁】时（页面登记的前置），就用它的判定 ——
            // 实测 24232 包内那层是 "i18n"，旧写法会指着这个素材层名说「你还没装「i18n」」，
            // 而页面说得出真宿主是 #20781。它说不出名字时才轮到下面那道"层名不是 mod 名"的闸。
            // HostKeysUnmatched 例外：它是"键集在本机一家都没过半"这个【测出来的事实】，
            // 连最接近的一家都没有时也要用这句（否则就落到"拿包内目录名当前置"那句谎话上）。
            if (tfail is OverlayFail.HostKeysUnmatched
                || (tguess.Length > 0
                    && tfail is OverlayFail.HostNotInstalled or OverlayFail.DeclaredHostKeyMismatch))
            { fail = tfail; hostGuess = tguess; return null; }
        }
        // 顶层那个名字是原版素材层名（i18n / assets / Portraits…）时，它不是 mod 名，
        // 说「你还没装它」等于凭空捏一个不存在的 mod（实测界面就报了「要盖在「i18n」上」）。
        if (IsVanillaAssetLayoutDir(top)) { fail = OverlayFail.NoHostInPack; hostGuess = ""; return null; }
        fail = OverlayFail.HostNotInstalled;
        return null;
    }

    /// <summary>包内那层目录名里，这些是【原版 Content 的素材层名】而不是某个 mod 的目录名。
    /// 实测 30539「Alternative Portraits and Sprites for Childhood Sweetheart Caroline」整包是
    /// <c>assets/Portraits/*.png</c> + <c>assets/Sprites/*.png</c>，旧写法把第二层的
    /// "Portraits、Sprites" 当成"包里放着好几个别的 mod 的目录"报出去 —— 那是游戏自己的文件夹名。
    /// 立绘家族本来就有它自己那条转换通道，不参与"多宿主"判定。</summary>
    private static readonly string[] VanillaAssetLayoutDirs =
    {
        "portraits", "characters", "sprites", "maps", "tilesheets", "loosesprites",
        "assets", "i18n", "data", "text", "buildings", "terrainfeatures", "mines", "animals",
    };

    /// <summary>包根目录上摊着的说明文件：不是 mod 内容，既不参与"宿主是谁"的判定，
    /// 也不写进宿主目录（免得往人家 mod 里塞一个 说明.txt）。只看裸在包根的文件 ——
    /// 带目录的（Host/readme.txt）照包内路径走，本来就在宿主那一层下面。</summary>
    private static bool IsPackDocFile(string relPath)
    {
        if (relPath.Contains('/')) return false;
        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        if (ext is ".txt" or ".md" or ".pdf" or ".html" or ".rtf") return true;
        var leaf = Path.GetFileNameWithoutExtension(relPath).ToLowerInvariant();
        return leaf is "readme" or "license" or "licence" or "changelog"
            or "说明" or "介绍" or "注意" or "留言";
    }

    private static bool IsVanillaAssetLayoutDir(string name)
        => Array.IndexOf(VanillaAssetLayoutDirs, name.Trim().ToLowerInvariant()) >= 0;

    /// <summary>这个包在 N 网页面登记的前置里<b>可能当覆盖宿主</b>的那些行：
    /// 场外链接不算（没有页面号也没有本机目录）；<b>SMAPI 不算</b> —— 它是加载器，
    /// 不住在 Mods\ 里，永远不可能被"盖"（实测 24232 登记了两行：#20781 与 SMAPI，
    /// 剔掉 SMAPI 才看得出宿主只有一个）。</summary>
    private static List<NexusRequirement> HostCandidateRows(IReadOnlyList<NexusRequirement>? reqs)
        => (reqs ?? new List<NexusRequirement>())
           .Where(r => !r.External && r.ModName.Trim().Length > 0
                       && !r.ModName.Trim().StartsWith("SMAPI", StringComparison.OrdinalIgnoreCase))
           .ToList();

    /// <summary>登记的前置里，<b>本机装着且唯一对上</b>的那一条的宿主目录名。
    /// 一条都对不上或对上好几条都不给。页面号可以是 0（实测 18080 登记了名字
    /// "Crop Harvest Bubbles" 而 modId 填的是 "0"）—— 名字照样能用，只是报不出页面号。</summary>
    private string? FindInstalledRequirementHost(string gamePath, IReadOnlyList<NexusRequirement>? reqs)
        => MatchDeclaredHosts(gamePath, reqs).One;

    /// <summary>登记的前置与本机已装 mod 到底对上几个。<b>必须把"并列"和"一个都没对上"分开</b>：
    /// 前者是同名子包分不出该盖谁（实测 SVE 捆绑包里 <c>[CP] Stardew Valley Expanded</c> 与
    /// <c>Stardew Valley Expanded Code</c> 两个子包的 manifest Name 都写着 "Stardew Valley Expanded"），
    /// 后者才是"你还没装它"。把并列说成没装是第二句谎话 —— 用户会去找一个其实装着的 mod。
    /// <para><see cref="One"/> 在并列时<b>必须是 null</b>，不是"给最后一个"：两个下游都把它当
    /// 唯一的宿主用（<see cref="ProbeOverlayForDeclaredHost"/> 直接往那个目录写字、
    /// InstallNew 那句"本机也装着它"直接报名字），给错了就是盖错 mod / 指错 mod。
    /// 实测 2026-10-08 的 31795：前置写着 SVE，本机 SVE 撞出两个同名子包，界面报的是
    /// 排在后面的那个 <c>[FTM]</c> 兄弟目录。</para></summary>
    private (int Count, string? One) MatchDeclaredHosts(string gamePath, IReadOnlyList<NexusRequirement>? reqs)
    {
        string? one = null;
        var hits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in HostCandidateRows(reqs))
            foreach (var h in InstalledHostsByName(gamePath, r.ModName))
                if (hits.Add(h)) one = h;
        return (hits.Count, hits.Count == 1 ? one : null);
    }

    /// <summary>已装 mod 里名字（目录末段或 manifest 显示名）与这个前置名对得上的全部宿主。
    /// 与 <see cref="FindInstalledHostBySpelling"/> 同一套拼写键，只是不替调用方在并列时弃权。</summary>
    private List<string> InstalledHostsByName(string gamePath, string name)
    {
        var list = new List<string>();
        var key = HostSpellingKey(name);
        if (key.Length == 0) return list;
        try
        {
            foreach (var e in ScanRaw(gamePath))
            {
                var host = NormalizeOverlayHost(e.Folder);
                if (host.Length == 0) continue;
                var leaf = host.Replace('\\', '/').Split('/')[^1];
                if (!HostSpellingKey(leaf).Equals(key, StringComparison.Ordinal)
                    && !HostSpellingKey(e.Name ?? "").Equals(key, StringComparison.Ordinal)) continue;
                if (ResolveHostDir(gamePath, host) is null) continue;
                if (!list.Contains(host, StringComparer.OrdinalIgnoreCase)) list.Add(host);
            }
        }
        catch { return list; }
        return list;
    }

    /// <summary>整包只有一个翻译 json 时（见 <see cref="SoleTranslationJson"/>），页面登记的那
    /// <b>唯一</b>一条宿主候选 —— 用来把「你还没装它」说成具体是哪个 mod、哪个页面。
    /// 多于一行（说不清哪条是宿主）或一行都没有 ⇒ null。</summary>
    internal static NexusRequirement? SoleHostCandidate(IReadOnlyList<NexusRequirement>? reqs)
    {
        var rows = HostCandidateRows(reqs);
        return rows.Count == 1 ? rows[0] : null;
    }

    /// <summary>整包【只有一个翻译 json】的形状。这一批实机包全是它，但包内那层目录名五花八门：
    /// <list type="bullet">
    /// <item>41314 / 18080 / 44611 —— 裸在包根目录的 <c>zh.json</c></item>
    /// <item>24232 —— <c>i18n/zh.json</c></item>
    /// <item>49587 / 38630 —— <c>翻译自己的标题/i18n/zh.json</c></item>
    /// <item>32901 —— <c>Caroline/default.json</c>（角色名当目录名，1299 个键里 1280 个
    ///   就是宿主 i18n/default.json 的键）</item>
    /// </list>
    /// 所以<b>不看路径</b>：包内那层目录名要么是翻译自己的标题、要么是素材层名、要么是角色名，
    /// 拿它当宿主名会凭空捏一个不存在的 mod（实测界面就报过「这个包要盖在「i18n」上」）。
    /// "这是不是一份翻译"由文件自己的内容说了算 —— 顶层键 vs 各已装 mod 的 i18n 键集，
    /// 见 <see cref="ProbeTranslationOnlyPack"/>。返回包内路径；不满足"恰好一个文件、
    /// 它是 json、且不是 content.json / manifest.json"⇒ null。</summary>
    private static string? SoleTranslationJson(string tempDir, List<string> judgedRels)
    {
        if (judgedRels.Count != 1) return null;
        var r = judgedRels[0];
        var slash = r.LastIndexOf('/');
        var leaf = slash < 0 ? r : r[(slash + 1)..];
        if (!leaf.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("content.json", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)) return null;
        // 不再要求它躺在 i18n/ 下：实测 32901「Childhood Sweetheart Caroline (Chinese)」
        // 把翻译放在 <角色名>/default.json（1299 个键，与宿主 i18n/default.json 的 1285 个
        // 重合 1280 = 98.5%）。"这是不是一份翻译"由文件自己的内容说了算（顶层键 vs 宿主
        // i18n 键集，见 ProbeTranslationOnlyPack），包内那层目录名不参与判断。
        return r;
    }

    /// <summary>一个已装 mod 的 <c>i18n/*.json</c> 顶层键全集（非递归，与
    /// <see cref="WithI18nHome"/> 读的是同一处）。</summary>
    private static HashSet<string> HostI18nKeys(string hostDir)
    {
        var have = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var f in Directory.GetFiles(Path.Combine(hostDir, "i18n"), "*.json"))
                if (JsonTopKeys(f) is { } ks)
                    foreach (var k in ks) have.Add(k);
        }
        catch { }
        return have;
    }

    /// <summary>这份翻译的顶层键是否过半落在这个宿主的 i18n 键集里（六成 = 这条链既有的校准线）。</summary>
    private static bool HostI18nCovers(string hostDir, HashSet<string> packKeys)
    {
        var have = HostI18nKeys(hostDir);
        return have.Count > 0 && packKeys.Count(k => have.Contains(k)) * 100 >= packKeys.Count * 60;
    }

    /// <summary>已装 mod 里，i18n 键集过半覆盖 <paramref name="want"/> 的那些目录。
    /// 命中几家都交回调用方，这里绝不替用户挑。
    /// <para>遍历的单位必须是 <see cref="ScanRaw"/> 的行（顶层 + 捆绑包子包），不能只看
    /// <c>Mods\*</c> 一层：实机 2026-10-08 的「Grandpa''s Farm」是个壳，它的 i18n 在
    /// <c>Grandpa''s Farm/[CP] Grandpa''s Farm/i18n/</c>，只扫一层 ⇒ 键路由对整个捆绑包生态
    /// 全部失明（SVE、Grandpa''s Farm、Immersive Farm… 一个都认不出来），而名字路由
    /// （<see cref="InstalledHostsByName"/>）本来就看得到子包 —— 两条证据对"什么算宿主"
    /// 不一致，才会出现"宿主明明装着、却报判断不出宿主"。</para></summary>
    private List<string> FindHostsByI18nKeys(string gamePath, HashSet<string> want)
        => RankHostsByI18nKeys(gamePath, want)
            .Where(r => r.Matched * 100 >= want.Count * 60)
            .Select(r => r.Host).ToList();

    /// <summary>把全部已装宿主按"这个包的键在谁家 i18n 里出现得多"排名（一家都撞不上的不列）。
    /// 用来把拒绝说成【可核对的事实】：实测 45719「UI Info Suite 2 Alternative-Chinese」的 298 个键
    /// 里只有 95 个（32%）撞在本机装着的<b>原版</b> UIInfoSuite2 上 —— 说"判断不出宿主"是把已经
    /// 量出来的东西藏起来，说"它就是 UIInfoSuite2 的汉化"是撒谎，只有报出这一名和这个数是诚实的。</summary>
    private List<(string Host, int Matched)> RankHostsByI18nKeys(string gamePath, HashSet<string> want)
    {
        var rows = new List<(string, int)>();
        try
        {
            foreach (var e in ScanRaw(gamePath))
            {
                var host = NormalizeOverlayHost(e.Folder);
                if (host.Length == 0) continue;
                if (ResolveHostDir(gamePath, host) is not string dir) continue;
                var have = HostI18nKeys(dir);
                if (have.Count == 0) continue;
                var matched = want.Count(have.Contains);
                if (matched > 0) rows.Add((host, matched));
            }
        }
        catch { }
        return rows.OrderByDescending(r => r.Item2).ToList();
    }

    /// <summary>整包只有一个翻译 json ⇒ 认宿主并给出落点。两条证据按权威度排：
    /// ① <b>这个包页面登记的前置</b>里本机装着且唯一对上的那一条（作者自己写的）；
    /// ② 页面一行都没登记时，这份 json 的顶层键 vs 各已装 mod 自己 i18n 的键集，唯一命中
    ///    （实测 49587：ZoomLevel <b>100%</b>、第二名 0%）。
    /// <para><b>②不许在"页面登记了、可那条本机没装着"时兜底</b> —— 实测 24232「Crop Harvest
    /// Bubbles (Updated) 中文」登记的前置是<b>没装的</b> #20781，而它那 6 个键跟本机装着的
    /// 原版 Crop Harvest Bubbles 的 i18n 也 100% 重合（更新版的键是原版的子集）：
    /// 让键集兜底就会把"更新版"的翻译盖到原版头上，正是这条链最不该制造的那种静默。</para>
    /// 两条都不给答案时：页面只有唯一一条候选 ⇒ 报「你还没装它」并带页面号；否则 NoHostInPack。</summary>
    private OverlayPlan? ProbeTranslationOnlyPack(string gamePath, string tempDir, string jsonRel,
        IReadOnlyList<NexusRequirement>? packRequirements, out OverlayFail fail, out string hostGuess)
    {
        fail = OverlayFail.None;
        hostGuess = "";
        var rows = HostCandidateRows(packRequirements);
        var leaf = jsonRel.Split('/')[^1];
        var slash = jsonRel.LastIndexOf('/');
        var packRoot = slash < 0 ? "" : jsonRel[..slash];
        HashSet<string>? reqHostKeys = null;

        var (declaredCount, declaredOne) = MatchDeclaredHosts(gamePath, packRequirements);
        if (rows.Count > 0 && declaredCount == 0)
        {
            // 一条前置都没对上本机已装 mod ⇒ 这才是真的"你还没装它"
            fail = rows.Count == 1 ? OverlayFail.HostNotInstalled : OverlayFail.NoHostInPack;
            hostGuess = rows.Count == 1 ? rows[0].ModName.Trim() : "";
            return null;
        }
        if (declaredCount == 1 && declaredOne is string reqHost)
        {
            if (ResolveHostDir(gamePath, reqHost) is not string reqDir)
            { fail = OverlayFail.NoHostInPack; return null; }
            if (JsonTopKeys(Path.Combine(tempDir, jsonRel.Replace('/', Path.DirectorySeparatorChar)))
                    is not { Count: > 0 } declaredKeys || !HostI18nCovers(reqDir, declaredKeys))
            {
                // 名字对上了、条目对不上 ⇒ 不是它的翻译（不许落到宿主根目录当静默无效文件）
                fail = OverlayFail.DeclaredHostKeyMismatch;
                hostGuess = reqHost;
                return null;
            }
            return new OverlayPlan(reqHost, new List<string> { leaf }, packRoot,
                new List<string> { "i18n/" + leaf });
        }

        // 页面没给答案（一行都没登记，或登记的名字在本机撞出好几个同名子包分不出该盖谁）
        // ⇒ 只看这份翻译自己声明的内容：它的键是谁的 i18n 里的键。
        // 实测 31795「Grandpa's Farm 中文翻译」：登记的前置写着 SVE，而 SVE 在本机装着的是
        // 两个同名子包 —— 并列不许多数服从，交给键集去分（它的键是隔壁 Grandpa's Farm 的）。
        var keys = reqHostKeys ?? JsonTopKeys(
            Path.Combine(tempDir, jsonRel.Replace('/', Path.DirectorySeparatorChar)));
        if (keys is not { Count: > 0 })
        { fail = OverlayFail.NoHostInPack; return null; }
        var byKeys = FindHostsByI18nKeys(gamePath, keys);
        if (byKeys.Count == 1 && ResolveHostDir(gamePath, byKeys[0]) is not null)
            return new OverlayPlan(byKeys[0], new List<string> { leaf }, packRoot,
                new List<string> { "i18n/" + leaf });
        if (byKeys.Count == 0)
        {
            // 一家都没过半 ⇒ 我们其实【量出了东西】：最接近的是谁、对上了几条。
            // 报"判断不出宿主"是把这句藏起来让人自己去猜（实测 45719 就是这样被当成 bug 报上来的）。
            var rank = RankHostsByI18nKeys(gamePath, keys);
            fail = OverlayFail.HostKeysUnmatched;
            hostGuess = rank.Count > 0 ? rank[0].Host : "";
            AppLog.Info("Mods", $"这个汉化包的 {keys.Count} 个条目在本机最接近的宿主："
                + (rank.Count == 0 ? "一家都没有"
                    : string.Join(" , ", rank.Take(3).Select(r => $"{r.Host} {r.Matched}/{keys.Count}"))));
            return null;
        }
        fail = OverlayFail.NoHostInPack;
        hostGuess = "";
        return null;
    }

    /// <summary>无 manifest 的图片包，如果它自己页面登记的那<b>唯一</b>一条前置是本机装着的某个 mod，
    /// 而且包里的图片逐张都在那个 mod 的目录里有同名文件 ⇒ 它是那个 mod 的<b>覆盖包</b>，
    /// 不是"照着原版某张图画的面子包"。
    /// <para>为什么需要这一层（实机 2026-10-08，34635「DaisyNiko's Earthy Recolour - Chinese」）：
    /// 包内路径是 <c>Maps/fall_beach.png</c> 这种【原版 Content 相对路径】，而
    /// <c>Content\Maps\fall_beach.xnb</c> 真的存在 ⇒ <see cref="HasForeignAssetTarget"/> 先一步判定
    /// "该转成 CP 包盖原版"，覆盖链压根没执行。可这包 11 张图 <b>11/11</b> 在已装的
    /// <c>[CP] DaisyNiko's Earthy Recolour</c>（#5255）里有同名文件，转出来的 11 条 CP 目标
    /// 也<b>全部</b>落在 ER 自己那 98 条目标之内 —— 一个独有目标都没有。
    /// 作者登记的意图应当压过我们从路径猜出来的形状。</para></summary>
    internal OverlayPlan? ProbeOverlayForDeclaredHost(string gamePath, string tempDir,
        IReadOnlyList<NexusRequirement>? packRequirements)
    {
        if (FindInstalledRequirementHost(gamePath, packRequirements) is not string hostName) return null;
        if (ResolveHostDir(gamePath, hostName) is not string hostDir) return null;

        List<string> packRel;
        try
        {
            packRel = Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(tempDir, f).Replace('\\', '/')).ToList();
        }
        catch { return null; }
        if (packRel.Count == 0) return null;
        if (MapPackFilesOntoHost(hostDir, packRel) is not Dictionary<string, string> map) return null;

        var rel = new List<string>();
        var dst = new List<string>();
        foreach (var p in packRel) { rel.Add(p); dst.Add(map[p]); }
        return new OverlayPlan(hostName, rel, "", dst);
    }

    /// <summary>把包里的文件按【宿主目录里真实存在的同名文件】逐一对上落点，三种形状都试：
    /// 原样、加 <c>assets/</c> 前缀、去 <c>assets/</c> 前缀（各家 mod 收素材的层不一样）。
    /// 命中率<b>只算图片</b>（png / xnb）——"这是谁的素材"这件事只有素材本身能作证；
    /// 图片命中不足六成 ⇒ null（六成是这条链既有的校准线，见 <see cref="WithI18nHome"/>）。
    /// 没命中的文件（readme 之类）按包内路径原样落。</summary>
    private static Dictionary<string, string>? MapPackFilesOntoHost(string hostDir, List<string> packRel)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var images = 0;
        var hits = 0;
        foreach (var p in packRel)
        {
            var ext = Path.GetExtension(p);
            var isImage = ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
                          || ext.Equals(".xnb", StringComparison.OrdinalIgnoreCase);
            if (isImage) images++;                       // 分母要在"命中不命中"之前数，否则永远是 100%
            var found = HostPathShapes(p).FirstOrDefault(s =>
                File.Exists(Path.Combine(hostDir, s.Replace('/', Path.DirectorySeparatorChar))));
            if (found is not null)
            {
                map[p] = found;
                if (isImage) hits++;
                continue;
            }
            // 这张图宿主里没有同名文件（覆盖包顺手新增的素材）。落点仍按宿主自己的层走：
            // 取那个"父目录在宿主里已经存在"的形状，两边都不存在就原样落，不硬编一个位置。
            map[p] = isImage
                ? (HostPathShapes(p).FirstOrDefault(s => Directory.Exists(
                      Path.GetDirectoryName(Path.Combine(hostDir,
                          s.Replace('/', Path.DirectorySeparatorChar))))) ?? p)
                : p;
        }
        return images > 0 && hits * 100 >= images * 60 ? map : null;
    }

    private static IEnumerable<string> HostPathShapes(string packRelPath)
    {
        yield return packRelPath;
        if (packRelPath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
            yield return packRelPath["assets/".Length..];
        else
            yield return "assets/" + packRelPath;
    }

    /// <summary>一个包里所有 manifest 自己声明的 UniqueID（捆绑包收集全部子包）。</summary>
    private static HashSet<string> PackUniqueIds(string[] manifests)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mf in manifests)
            try
            {
                using var doc = JsonDocument.Parse(ReadManifestText(mf), ManifestJson);
                if (doc.RootElement.TryGetProperty("UniqueID", out var u)
                    && u.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(u.GetString()))
                    set.Add(u.GetString()!.Trim());
            }
            catch { }
        return set;
    }

    /// <summary><c>Mods\&lt;folderName&gt;</c>（或禁用态的 <c>Mods\.&lt;folderName&gt;</c>）里已经住着
    /// <b>另一个 mod</b> —— 它自己的 manifest 写了 UniqueID，而那个 ID 不在这一包声明的 ID 里 ——
    /// 就返回它的名字；目录不存在、读不出 manifest（空壳/无清单）、或本来就是同一个 mod 的旧版
    /// ⇒ null，照旧走"同名旧目录移进回收站"那条路。
    /// <para>没有这道闸的后果（测试舱实测 2026-10-08）：装一个 Name=HostOne / UniqueID=Some.Other
    /// 的包，已装的 HostOne（UniqueID=Host.One）会被整个移进 <c>Mods\.junigrid_trash</c> ——
    /// 两个 mod 只在文件夹名上撞车，用户看到的却是"我那个 mod 自己没了"。</para></summary>
    private static string? DifferentModInThatFolder(string gamePath, string folderName, HashSet<string> newUids)
    {
        if (newUids.Count == 0) return null;      // 这一包没写 UID ⇒ 没有可比的身份，不拦
        var modsDir = Path.Combine(gamePath, "Mods");
        foreach (var candidate in new[] { folderName, "." + folderName })
        {
            var dir = Path.Combine(modsDir, candidate);
            if (!Directory.Exists(dir)) continue;
            string[] manifests;
            try { manifests = Directory.GetFiles(dir, "manifest.json", SearchOption.AllDirectories); }
            catch { continue; }
            foreach (var mf in manifests)
            {
                try
                {
                    using var doc = JsonDocument.Parse(ReadManifestText(mf), ManifestJson);
                    if (doc.RootElement.TryGetProperty("UniqueID", out var u)
                        && u.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(u.GetString())
                        && !newUids.Contains(u.GetString()!.Trim()))
                        return doc.RootElement.TryGetProperty("Name", out var n)
                               && n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString())
                            ? n.GetString()!.Trim() : candidate;
                }
                catch { }
            }
        }
        return null;
    }

    /// <summary>包里文件所在的那层是「自己没有 manifest 的捆绑包壳」⇒ 宿主归到它真正的宿主行
    /// <c>顶层/子包</c>（Scan 不为壳顶层出行，只为带 manifest 的子包出行）。
    /// <para>不归一的症状（实机汉化包 28252 → Frontier Farm，2026-10-07）：包内路径
    /// <c>Frontier Farm/[CP] Frontier Farm/i18n/zh.json</c> 的顶层名恰好就是壳目录名，于是宿主记成了顶层；
    /// 而 Mods 页那两行都是子包，徽标按宿主形状查表 ⇒ 一个都查不到，覆盖包在界面上整个隐身，
    /// 开关、还原、删除三个入口全点不到 —— 文件其实已经正确落盘、游戏里也确实生效了。</para>
    /// 归不了返回 null（交回原判定）：顶层自己有 manifest、文件散在好几个子包、
    /// 有直属顶层的文件、或那一层子目录根本没有 manifest。</summary>
    private static List<OverlayPlan> One(OverlayPlan p) => new() { p };

    /// <summary>包里文件所在的那层是「自己没有 manifest 的捆绑包壳」⇒ 按子包拆开，<b>每个子包一条账</b>
    /// <c>顶层/子包</c>（Scan 不为壳顶层出行，只为带 manifest 的子包出行）。一个子包时它退化成原来
    /// 那份 <see cref="SplitShellPlanByBundleChild"/> 在一家时的结果，形状逐字不变。
    /// <para>不归一的症状（实机汉化包 28252 → Frontier Farm，2026-10-07）：包内路径
    /// <c>Frontier Farm/[CP] Frontier Farm/i18n/zh.json</c> 的顶层名恰好就是壳目录名，于是宿主记成了顶层；
    /// 而 Mods 页那两行都是子包，徽标按宿主形状查表 ⇒ 一个都查不到，覆盖包在界面上整个隐身，
    /// 开关、还原、删除三个入口全点不到 —— 文件其实已经正确落盘、游戏里也确实生效了。</para>
    /// <para>文件分住几个子包时（实机汉化包 10342 → SVE 的 Code 与 [CP] 两包）原来也记在壳上，
    /// 要等下次启动 <see cref="RelocateBundleTopOverlays"/> 才搬进子包行 —— 装完到重启之前那一截
    /// 同样是隐形的。这里当场拆开，就不需要等。</para>
    /// 拆不了返回 null（交回原判定）：顶层自己有 manifest、有直属顶层的文件、或某一层子目录没有 manifest。</summary>
    private static List<OverlayPlan>? SplitShellPlanByBundleChild(string topDir, string top, string packRoot,
        List<string> rel)
    {
        if (File.Exists(Path.Combine(topDir, "manifest.json"))) return null;
        var bySeg = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rel)
        {
            var i = r.IndexOf('/');
            if (i <= 0) return null;                       // 有文件直属壳这一层：拆不进任何子包
            if (!bySeg.TryGetValue(r[..i], out var g)) bySeg[r[..i]] = g = new List<string>();
            g.Add(r[(i + 1)..]);
        }
        var plans = new List<OverlayPlan>();
        foreach (var (seg, files) in bySeg)
        {
            if (!File.Exists(Path.Combine(topDir, seg, "manifest.json"))) return null;   // 那一层不是子包
            plans.Add(new OverlayPlan(top + "/" + seg, files, packRoot + "/" + seg));
        }
        return plans.Count > 0 ? plans : null;
    }

    /// <summary>包内并列摆着<b>好几个</b>别的 mod 的目录名时，逐字比对已装 mod 行：
    /// <b>每一家都唯一对上</b>才交回"一家一份计划"，否则 null（调用方报 <see cref="OverlayFail.MultiHost"/>）。
    /// <para>为什么原来一律弃权、现在敢盖：弃权那条写法顺带把【捆绑包的汉化包】也拒了 ——
    /// 实机 53406「To The Moon (中文)」包内两个目录就是已装捆绑包的两个子包，目录名逐字相同、
    /// 两个都装着，这里没有任何"该替谁决定"的余地，缺的只是"去盘上看一眼"。</para>
    /// <para>三道闸，一道都不松：① 只认<b>逐字相等</b>的行末段名（不拼写、不截断 —— 多宿主一次要落
    /// 好几家，猜错的面积比单宿主大）；② 一家都对不上才算，<b>有一家缺就整包不装</b>（只盖一半会让
    /// 这个包半生效，正是这条链最怕的静默）；③ 包里出现的目录名必须【全部】在候选清单里 ——
    /// 混着 assets/ 这类原版素材层名的包拆一半留一半，等于替用户丢了文件。</para></summary>
    private List<OverlayPlan>? ProbeOnePlanPerHostDir(string gamePath, string tempDir, List<string> dirNames,
        Dictionary<string, List<string>> filesByDir, string packRootPrefix)
    {
        if (dirNames.Count < 2 || dirNames.Count != filesByDir.Count) return null;
        var plans = new List<OverlayPlan>();
        foreach (var n in dirNames)
        {
            // 名字逐字相等 ⇒ 每家本来就是不同的一行，不需要再防"两个目录对上同一家"
            if (FindInstalledHostByExactDirName(gamePath, n) is not string host) return null;
            if (ResolveHostDir(gamePath, host) is not string hostDir) return null;
            plans.Add(WithI18nHome(tempDir, new OverlayPlan(host, filesByDir[n], packRootPrefix + n), hostDir));
        }
        return plans;
    }

    /// <summary>已装 mod 行里，<b>目录末段名逐字相等</b>的那一家。命中 0 个或多个都不返回。
    /// 行必须来自 <see cref="ScanRaw"/>（顶层 mod 与捆绑包子包都算），这样落盘的宿主一定是
    /// Mods 页有一行的宿主 —— 记在壳顶层上的账没有徽标、开关、还原任何入口。</summary>
    private string? FindInstalledHostByExactDirName(string gamePath, string dirName)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? one = null;
        try
        {
            foreach (var e in ScanRaw(gamePath))
            {
                var host = NormalizeOverlayHost(e.Folder);
                if (host.Length == 0) continue;
                if (!host.Replace('\\', '/').Split('/')[^1].Equals(dirName, StringComparison.OrdinalIgnoreCase)) continue;
                // 扫到却又不在磁盘上了 = 目录正在被改名，这份结果不能用
                if (ResolveHostDir(gamePath, host) is null) continue;
                if (found.Add(host)) one = host;
            }
        }
        catch { return null; }
        return found.Count == 1 ? one : null;
    }

    /// <summary>包里【声明】要翻译哪些配置项：它自己 i18n/*.json 里 <c>config.&lt;字段&gt;.name</c>
    /// 那些键的字段名。一个都没有 ⇒ 空集（这包不是配置项汉化，别拿这条判据套它）。</summary>
    private static HashSet<string> PackDeclaredConfigFields(string tempDir, string packRoot, List<string> relPaths)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in relPaths)
        {
            if (!r.StartsWith("i18n/", StringComparison.OrdinalIgnoreCase)
                || !r.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            if (JsonTopKeys(Path.Combine(tempDir,
                    packRoot.Replace('/', Path.DirectorySeparatorChar),
                    r.Replace('/', Path.DirectorySeparatorChar))) is not { } keys) continue;
            foreach (var k in keys)
                if (k.StartsWith("config.", StringComparison.OrdinalIgnoreCase)
                    && k.EndsWith(".name", StringComparison.OrdinalIgnoreCase)
                    && k.Length > "config..name".Length)
                    set.Add(k["config.".Length..^".name".Length]);
        }
        return set;
    }

    /// <summary>已装 mod 里，把自己声明的配置项字段名【全包含】want 的那些目录。
    /// 一家都没命中或命中好几家，都由调用方处理 —— 这里绝不替用户挑一个。</summary>
    private static List<string> FindHostsByDeclaredConfigFields(string gamePath, HashSet<string> want)
    {
        var hits = new List<string>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Path.Combine(gamePath, "Mods")))
            {
                var name = Path.GetFileName(dir);
                if (name.Equals(".junigrid_trash", StringComparison.OrdinalIgnoreCase)) continue;
                if (CoversConfigFields(dir, want)) hits.Add(NormalizeOverlayHost(name));
            }
        }
        catch { }
        return hits;
    }

    /// <summary>这个目录声明的配置项字段名：CP 的通用配置菜单就读这几处 ——
    /// manifest.json / content.json 的 <c>ConfigSchema</c>（嵌套一层），加上
    /// example_config.json / config.json 的顶层键。</summary>
    private static bool CoversConfigFields(string dir, HashSet<string> want)
    {
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in new[] { "manifest.json", "content.json" })
            try
            {
                using var doc = JsonDocument.Parse(ReadManifestText(Path.Combine(dir, file)), ManifestJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("ConfigSchema", out var cs)
                    && cs.ValueKind == JsonValueKind.Object)
                    foreach (var p in cs.EnumerateObject()) declared.Add(p.Name);
            }
            catch { }
        foreach (var file in new[] { "example_config.json", "config.json" })
            if (JsonTopKeys(Path.Combine(dir, file)) is { } keys) declared.UnionWith(keys);
        return declared.Count > 0 && want.IsSubsetOf(declared);
    }

    private static void AddDistinct(List<string> list, string name)
    {
        if (!list.Contains(name, StringComparer.OrdinalIgnoreCase)) list.Add(name);
    }

    /// <summary>把候选宿主名拼进一句提示里；超过三个只点名前三个并报总数，免得提示卡变成一堵字。</summary>
    private static string JoinHostNames(List<string> names)
        => names.Count <= 3 ? string.Join("、", names)
         : string.Join("、", names.Take(3)) + " 等 " + names.Count + " 个";

    /// <summary>包内目录名的拼写键：剥掉「N 网页号 + 分隔符」前缀与「[CP] 」这类标签，
    /// 再忽略大小写与空格/连字符/点。<c>2544 Canon-Friendly Dialogue Expansion</c> 与
    /// <c>[CP] Canon Friendly Dialogue Expansion</c> 得到同一个键。</summary>
    private static string HostSpellingKey(string s)
    {
        var n = (s ?? "").Trim();
        var sep = n.IndexOfAny(new[] { ' ', '_', '-', '.' });
        if (sep > 0 && n[..sep].All(char.IsDigit))
            n = n[(sep + 1)..].TrimStart(' ', '_', '-', '.');
        n = StripPackTag(n);
        var sb = new System.Text.StringBuilder(n.Length);
        foreach (var ch in n)
            if (!char.IsWhiteSpace(ch) && ch is not ('-' or '_' or '.')) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    /// <summary>按拼写键在<b>已装</b> mod 里找宿主：比目录名末段，也比 manifest 显示名
    /// （汉化包内那层目录常照抄显示名，而显示名和文件夹名本来就允许不同）。
    /// 命中 0 个或多个都不返回 —— 放宽的只是拼写，猜错的表现是把文件盖进隔壁 mod 里。</summary>
    private string? FindInstalledHostBySpelling(string gamePath, string packFolderName)
    {
        var key = HostSpellingKey(packFolderName);
        if (key.Length == 0) return null;
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? found = null;
        try
        {
            foreach (var e in ScanRaw(gamePath))
            {
                var host = NormalizeOverlayHost(e.Folder);
                if (host.Length == 0) continue;
                var leaf = host.Replace('\\', '/').Split('/')[^1];
                if (!HostSpellingKey(leaf).Equals(key, StringComparison.Ordinal)
                    && !HostSpellingKey(e.Name ?? "").Equals(key, StringComparison.Ordinal)) continue;
                // 扫到却又不在磁盘上了 = 目录正在被改名，这份结果不能用
                if (ResolveHostDir(gamePath, host) is null) continue;
                if (hosts.Add(host)) found = host;
            }
        }
        catch { return null; }
        return hosts.Count == 1 ? found : null;
    }

    /// <summary>包把翻译文件压在<b>包根</b>而不是 i18n/ 里时的搬家判定。实测 13273：整包只有
    /// <c>2544 Canon-Friendly Dialogue Expansion/zh.json</c> 一个文件，而 Content Patcher 与 SMAPI
    /// 只读 <c>i18n/&lt;语言&gt;.json</c>（宿主 i18n/default.json 开头那段给译者的说明、以及作者
    /// readme 都这么写）—— 按原样盖到宿主根目录，症状就是「装成功、游戏里毫无变化」，
    /// 正是这条链拒绝制造的那种静默。
    /// <para>判据不看文件名猜语言，只看两边自己声明的内容：包里裸在根目录的 json 只有【一个】、
    /// 宿主 i18n/ 里读得出键集、且这个 json 的顶层键 ≥60% 出现在那份键集里（实测 4669/4923＝94.8%）。
    /// content.json / manifest.json 是宿主根目录上有意义的名词，一律不搬。</para></summary>
    private static OverlayPlan WithI18nHome(string tempDir, OverlayPlan plan, string hostDir)
    {
        // 候选＝包里【唯一】一个裸在根目录的 json。再多一个裸 json 就说不清哪份是翻译，整包放弃搬家；
        // 旁边的 readme / license 之类不算竞争（它们照包内路径落在根目录，本来也不归谁读）。
        var bareJson = plan.RelPaths
            .Where(x => !x.Contains('/') && x.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).ToList();
        if (bareJson.Count != 1) return plan;
        var r = bareJson[0];
        if (r.Equals("content.json", StringComparison.OrdinalIgnoreCase)
            || r.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)) return plan;
        // 宿主根目录本来就有一个同名文件 = 作者就把它放这儿，不该动包的结构
        if (File.Exists(Path.Combine(hostDir, r.Replace('/', Path.DirectorySeparatorChar)))) return plan;
        if (JsonTopKeys(Path.Combine(tempDir,
                plan.PackRoot.Replace('/', Path.DirectorySeparatorChar),
                r.Replace('/', Path.DirectorySeparatorChar))) is not { Count: > 0 } packKeys) return plan;
        // 宿主 i18n/ 里一个 json 都读不出键集（没这个目录、或全是坏 json）⇒ 键重合必然为 0，
        // 下面那道比例闸就是这条证据本身，不再单列一条判不出东西的分支。
        var hostKeys = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var f in Directory.GetFiles(Path.Combine(hostDir, "i18n"), "*.json"))
                if (JsonTopKeys(f) is { } ks)
                    foreach (var k in ks) hostKeys.Add(k);
        }
        catch { return plan; }
        if (packKeys.Count(k => hostKeys.Contains(k)) * 100 < packKeys.Count * 60) return plan;
        return plan with
        {
            DstPaths = plan.RelPaths.Select(x => x == r ? "i18n/" + r : x).ToList()
        };
    }

    /// <summary>一个 json 的顶层键集合（容忍 SMAPI 的注释/尾逗号）。不是对象或读不了都算拿不到。</summary>
    private static HashSet<string>? JsonTopKeys(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(ReadManifestText(file), ManifestJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in doc.RootElement.EnumerateObject()) set.Add(p.Name);
            return set;
        }
        catch { return null; }
    }

    internal static string OverlayRefusal(OverlayFail fail, string gamePath, string hostGuess,
        NexusRequirement? soleHost = null)
    {
        // 页面上写了什么，几句拒绝都要能带上：认不出宿主 ≠ 页面上没写。实测 30539
        // 「Alternative Portraits and Sprites for Childhood Sweetheart Caroline (Overhaul)」
        // 整包是 assets/Portraits + assets/Sprites（原版素材层名，不是 mod 目录名），路径判据
        // 一条都不命中，而它页面登记的前置明写着 #16220。这句只陈述"页面登记了什么"，
        // 不替我们推断它是不是覆盖宿主 —— 一个只登记 Content Patcher 的独立 CP 包也会走到这里，
        // 那种情况前置是依赖不是宿主。
        var declared = soleHost is { } h
            ? LocService.Tf("这个包在 N 网登记的前置是「{0}」{1}。", h.ModName.Trim(),
                h.ModId > 0 ? LocService.Tf("（N 网 #{0}）", h.ModId) : "")
            : "";
        switch (fail)
        {
            case OverlayFail.HostNotInstalled:
                // 这条通道不挑内容：汉化、贴图、兼容包都走这里（判据只看包内路径），
                // 所以措辞里不许假设它是「翻译包」—— 实测 mod 1993 的文件 35407 是 256 张 PNG。
                // 「要盖在谁身上」这句有出处时优先用出处：包内那层目录名是作者随手起的，
                // 而包自己页面上登记的那条前置带页面号，人能去核对
                // （实测 34705 的包内目录叫「[CP] Seeds N' Saplings - …」，真宿主 #31185 的页面名不带 [CP]）。
                // 作者有时只登记名字不填页面号（实测 18080 的 modId 就是 0）—— 那就只报名字，
                // 不许凭空捏一个「#0」。
                var (hostName, page) = soleHost is { } s
                    ? (s.ModName.Trim(), s.ModId > 0 ? LocService.Tf("（N 网 #{0}）", s.ModId) : "")
                    : (hostGuess, "");
                return LocService.Tf("这个包要盖在「{0}」{1}上，但你还没装它。先装被覆盖的那个 mod，再装这个包。",
                    hostName, page);
            case OverlayFail.AmbiguousConfigHost:
                return LocService.Tf("这个包要翻译的配置项，好几个已装 mod 都声明了（{0}），认不出该盖谁，已放弃安装。", hostGuess);
            case OverlayFail.MultiHost:
                // 这句的原因随判据一起变了：包内几个目录名【每一家都装着】时我们已经会一家一份地盖，
                // 所以走到这里只剩一种可能 —— 有名字对不上唯一一家已装 mod。原来那句
                // 「该盖哪家我们替你决定不了」在这种机器上是谎话（实机 53406 的包内两个目录
                // 就是已装捆绑包的两个子包，我们只是没去盘上看）。
                return LocService.Tf("这个包里放着好几个别的 mod 的目录（{0}），本机没能把每一个都对上一个已装的 mod —— 只盖一半会让这个包半生效，已放弃安装。请只装你需要的那一个，或手动放进对应 mod。", hostGuess);
            case OverlayFail.DeclaredHostKeyMismatch:
                // 与「判断不出宿主」分开说：宿主认出来了、本机也装着，是这份文件的内容跟它对不上。
                return LocService.Tf("这个包登记的前置是「{0}」，本机也装着它，可这份文件里的条目跟它自己 i18n 里的对不上 —— 它不是这个 mod 的汉化，盖上去也不会生效，已放弃安装。", hostGuess);
            case OverlayFail.HostKeysUnmatched:
                // 这句只陈述量出来的事：本机没有一家【过半】用得到这些条目。最接近的那一家可以报出来
                // （它是"第二名长什么样"的证据，不是宿主 —— 措辞里不许把它说成宿主），一条都没撞上时
                // 连这个名字都不许出现。实测 45719：298 条里 95 条撞在原版 UIInfoSuite2 上（32%），
                // 而它真正对应的「UI Info Suite 2 Alternative」本机没装。
                return (hostGuess.Length > 0
                    ? LocService.Tf("这个包要翻译的条目，本机装着的 mod 里没有一家对得上 —— 最接近的一家是「{0}」，也只对得上其中一小部分。它对应的那个 mod 你还没装，先装它再装这个汉化。", hostGuess)
                    : LocService.Tr("这个包要翻译的条目，本机装着的 mod 里没有一家用得到。它对应的那个 mod 你还没装，先装它再装这个汉化。")) + declared;
            default:
                return LocService.Tf("这个包里没有 manifest.json，包内路径也没说明它是给哪个 mod 的，判断不出宿主，已放弃安装。{0}", declared);
        }
    }

    /// <summary>判断一个<b>带 manifest</b> 的包是不是「某个已安装 mod 的汉化重打包」——
    /// 作者把整个 mod 目录重发一遍、manifest 原封不动（UID 与显示名都和已装那份相同），只把内容翻译过来。
    /// 只有这种情况才允许逐文件盖到人家目录里，其余一律交回正常安装。
    /// <para>前提：同 UID 的已安装目录<b>只有一个</b>，且包里 ≥60% 的文件在宿主目录里已有同名文件
    /// （2026-09-30 LewdDew Valley 实测：汉化包 306 个文件里 305 个原包里就有）。</para>
    /// <para>在此之上三层否决，只看包自己声明的身份，<b>不猜文件名</b>（用户 2026-10-03 定的规则：
    /// 市面上的语言替换包基本都和宿主同名 ⇒ 名字不同就是另一个 mod）：</para>
    /// <list type="number">
    /// <item><b>同一个 Nexus 页面的两份文件 ⇒ 不盖。</b>那是"这一页换一份下载"。
    /// 实测 Cat Valley(39379) 的 Interface 与 Mobile Interface：路径重合 92.6%、重合里字节相同 99.3%、
    /// 两份 manifest 连显示名都一样，只有 content.json 不同。盖上去会留下"目录叫 -Mobile、
    /// 身子是 Interface"的怪状态（用户机上那条 10-02 20:30 的记录就是这样）。</item>
    /// <item><b>显示名不同 ⇒ 不盖，且拒绝安装</b>（通过 <paramref name="uidTwinHost"/> 报给调用方）。
    /// 实测 Goshcakes 把每个季节 UI 都发成同一个 UniqueID=<c>Goshcakes.customMenuEtc</c>、
    /// UpdateKeys 写的是字面量 <c>"Nexus:???"</c>（页号识别不出东西），于是 Fall UI(22253) 与
    /// Spring UI(26299) 路径重合 96% 被当成汉化包，27 个文件全盖进对方目录。
    /// 判成"另一个 mod"后也<b>不能</b>走正常安装 —— 安装链的「同 UID 判重清扫」会把已装那份整个
    /// 移进回收站（实测夹具宿主 ModS19 就这么消失了），那比覆盖更糟。</item>
    /// <item><b>同名但重合素材过半都换了 ⇒ 不盖。</b>那是接手的改版，不是汉化
    /// （实测 Fall/Spring 字节相同率只有 7.7%，真汉化包 ~99%）。</item>
    /// </list>
    /// </summary>
    private OverlayPlan? ProbeTranslatedRepackage(string gamePath, string tempDir, string modRoot,
        string manifest, int? packNexusModId, JuniGridConfig? cfg, out string? uidTwinHost)
    {
        uidTwinHost = null;
        string uid, packName;
        try
        {
            using var doc = JsonDocument.Parse(ReadManifestText(manifest), ManifestJson);
            uid = doc.RootElement.TryGetProperty("UniqueID", out var u) ? (u.GetString() ?? "").Trim() : "";
            packName = doc.RootElement.TryGetProperty("Name", out var nm) ? (nm.GetString() ?? "").Trim() : "";
        }
        catch { return null; }
        if (uid.Length == 0) return null;

        List<ModEntry> sameUid;
        try
        {
            sameUid = ScanRaw(gamePath)
                .Where(e => !string.IsNullOrWhiteSpace(e.UniqueID)
                    && e.UniqueID.Trim().Equals(uid, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch { return null; }
        var hosts = sameUid.Select(e => NormalizeOverlayHost(e.Folder)).Distinct().ToList();
        if (hosts.Count != 1) return null;              // 0 个=不是重打包；多个=目标不唯一，别猜

        var host = hosts[0];
        var hostEntry = sameUid.First(e =>
            NormalizeOverlayHost(e.Folder).Equals(host, StringComparison.OrdinalIgnoreCase));
        var hostDir = ResolveHostDir(gamePath, host);
        if (hostDir is null) return null;
        // 已经同路径 = 普通的"更新那个 mod"，交给原链路，不要改成逐文件覆盖
        if (NormalizeOverlayHost(Path.GetFileName(modRoot)).Equals(host, StringComparison.OrdinalIgnoreCase))
            return null;

        string[] files;
        try { files = Directory.GetFiles(modRoot, "*", SearchOption.AllDirectories); }
        catch { return null; }
        if (files.Length < 2) return null;              // 单文件的走普通覆盖链就够了
        var rel = files.Select(f => Path.GetRelativePath(modRoot, f).Replace('\\', '/')).ToList();
        var missing = rel.Where(r => !File.Exists(Path.Combine(hostDir,
            r.Replace('/', Path.DirectorySeparatorChar)))).ToList();
        var overlap = rel.Count - missing.Count;

        // 三层判据，只看包自己声明的身份，不再猜文件名（用户 2026-10-03 定的规则：
        // 市面上的语言替换包基本都和宿主同名 ⇒ 名字不同就是另一个 mod）。
        var samePage = packNexusModId is > 0 && (hostEntry.NexusModId == packNexusModId
            || InstalledPageOf(cfg, host) == packNexusModId);

        // ① 同一个 Nexus 页面的两份文件 ＝ "这一页换一份下载"，不是谁的汉化。交回正常安装：
        //    新那份成自己的目录、旧的同 UID 副本进 app 回收站（可恢复），不逐文件盖。
        //    实测 Cat Valley(39379) 的 Interface 与 Mobile Interface：路径重合 92.6%、重合里字节
        //    相同 99.3%、两份 manifest 连显示名都一模一样，只有 content.json 不同 ——
        //    作者就是在同一页发两个可选布局；盖上去会留下"目录叫 -Mobile、身子是 Interface"的怪状态。
        if (samePage) return null;

        // ② 显示名不同 ＝ 作者眼里就是两个 mod。实测 Goshcakes 把每个季节 UI 都发成同一个
        //    UniqueID=Goshcakes.customMenuEtc，Name 分别叫 Spring UI / Fall UI（Fall 盖掉 Spring
        //    那次就是栽在"只看 UID + 路径重合"上）。这种既不能盖（人家毁了）也不能走正常安装 ——
        //    安装链的「同 UID 判重清扫」会把已装那份整个移进回收站（实测夹具宿主 ModS19 就这么没了），
        //    那比覆盖更糟。所以报给调用方：拒绝安装，两边都不动。
        if (packName.Length > 0
            && !packName.Equals((hostEntry.Name ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
        {
            uidTwinHost = string.IsNullOrWhiteSpace(hostEntry.Name) ? host : hostEntry.Name.Trim();
            return null;
        }

        // ③ 同名同 UID、但重合素材过半都换了 ⇒ 是接手的改版，不是汉化（真汉化包素材字节不动：
        //    2026-09-30 LewdDew 实测 306 个文件里 305 个原包里就有）。别盖，交回正常安装。
        if (overlap > 0 && IdenticalOverlapRatio(modRoot, hostDir, rel) < 50) return null;

        if (overlap * 100 < files.Length * 60) return null;   // 目录结构对不上 → 它是要新增的另一个 mod
        return new OverlayPlan(host, rel, Path.GetRelativePath(tempDir, modRoot).Replace('\\', '/'));
    }

    /// <summary>重合文件里"字节完全相同"的百分比（0-100）。分母是【重合且读得到】的文件数 ——
    /// 长度不同就是"不相同"，必须计入分母（早先写成 continue 跳过，分母只剩长度恰好一样的那几个，
    /// 比率虚高到 100%，整道闸门形同没有，被 OV19 当场抓出来）。先比长度再算哈希，
    /// 是因为姊妹变体改的是图，长度几乎必然不同，这样大部分文件不用读进内存就能判掉。</summary>
    private static int IdenticalOverlapRatio(string modRoot, string hostDir, List<string> rel)
    {
        var same = 0;
        var total = 0;
        foreach (var r in rel)
        {
            var packFile = Path.Combine(modRoot, r.Replace('/', Path.DirectorySeparatorChar));
            var hostFile = Path.Combine(hostDir, r.Replace('/', Path.DirectorySeparatorChar));
            long packLen, hostLen;
            try
            {
                packLen = new FileInfo(packFile).Length;
                hostLen = new FileInfo(hostFile).Length;
            }
            catch { continue; }                          // 读不到的文件不进分母，免得把"不知道"当成"不同"
            total++;
            if (packLen != hostLen) continue;            // 长度都不同 ⇒ 确定不相同（计入分母，不算相同）
            if (packLen > 0 && Sha256Of(packFile) == Sha256Of(hostFile)) same++;
        }
        return total == 0 ? 0 : (int)(same * 100L / total);
    }

    /// <summary>这条已装目录当初是从哪个 Nexus 页面装进来的。同一个 mod 在安装记录里往往有多把键
    /// （UID、完整目录、末段目录、显示名），只有【完整目录】那把是宿主自己的身份：末段与显示名那两把
    /// 会被后装的汉化包改写（2026-09-30 LewdDew 实测：UID 与完整目录仍是原版页 7222，末段键已被改写
    /// 成汉化页 49428）。拿那两把判"同一页"就会把真翻译包也一起拦掉。</summary>
    private static int InstalledPageOf(JuniGridConfig? cfg, string host)
    {
        if (cfg is null) return 0;
        foreach (var kv in cfg.ModNexusInstalls)
            if (kv.Value.NexusModId > 0
                && NormalizeOverlayHost(kv.Key).Equals(host, StringComparison.OrdinalIgnoreCase))
                return kv.Value.NexusModId;
        return 0;
    }

    /// <summary>把覆盖包的文件写进宿主目录，并逐文件记账。
    /// 备份只在"还没有备份"时创建 —— 已有备份说明先前有别的包盖过这里，那份才是真原版，
    /// 用当前值覆盖它就会把还原目标污染成上一个覆盖版。</summary>
    private string? ApplyOverlay(string gamePath, string tempDir, OverlayPlan plan, ConfigService cfg,
        int? nexusModId, string packName, out string? modName, List<string>? notes = null)
    {
        modName = null;
        var hostDir = ResolveHostDir(gamePath, plan.Host);
        if (hostDir is null) return LocService.Tr("找不到要覆盖的 mod，已放弃安装");
        var hostRoot = Path.GetFullPath(hostDir) + Path.DirectorySeparatorChar;
        // 包内那层目录 ≠ 宿主目录（汉化包常把宿主再套一层、或宿主是捆绑包里的子包），
        // 所以取文件一律用 PackRoot，落盘才用宿主目录。
        var packRoot = Path.GetFullPath(Path.Combine(tempDir, plan.PackRoot.Replace('/', Path.DirectorySeparatorChar)));
        var c = cfg.Current;
        var written = 0;
        var skipped = 0;
        var adopted = 0;

        foreach (var (r, dst) in plan.RelPaths.Zip(plan.DstPaths ?? plan.RelPaths))
        {
            // src 用包内路径，其余（落盘、账本键、AppData 副本、开关与还原）一律用宿主内路径
            var local = dst.Replace('/', Path.DirectorySeparatorChar);
            var src = Path.Combine(packRoot, r.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(src)) continue;
            // 「已放到宿主的 X」这句只能在【真的动了那个文件】之后说。
            // 原来它写在循环开头，于是内容早已在位、一个字节都没写的包（实测 44611：
            // 宿主的 i18n/zh.json 与包里逐字节相同）也会报"已放到宿主的「i18n/zh.json」"，
            // 而 Mods 行上一个覆盖徽标都没有 —— 用户读到的是"装了"，实际是"什么都没装"。
            var moved = !dst.Equals(r, StringComparison.Ordinal);
            var dstPath = Path.GetFullPath(Path.Combine(hostDir, local));
            // 包内出现 ../ 之类的越界路径时绝不允许写到宿主外面去
            if (!dstPath.StartsWith(hostRoot, StringComparison.OrdinalIgnoreCase))
                return LocService.Tr("覆盖包里含越界路径，已放弃安装");
            // 逐字节相同的文件不重抄一遍盘：汉化重打包会把整个目录原样带上来
            // （LewdDew 那个包 306 个文件里 160 个和原版一模一样），
            // 全记进账就变成几百条毫无意义的"可还原"，还会把备份目录撑爆。
            var same = File.Exists(dstPath) && Sha256Of(src) == Sha256Of(dstPath);
            var key = OverlayKey(plan.Host, dst, nexusModId, packName);
            var bakFile = dstPath + OverlayBackupSuffix;
            if (same)
            {
                // 内容已经在位 ≠ 账还在。账没了这份汉化就成了隐形文件：界面看不见、
                // 开关和还原都没有入口，再点安装还会被上面那条规则算成"没东西可写"。
                // 所以补账。补得了的前提是原版备份还在 —— 没备份就绝不伪造 origHash，
                // 否则「删除」会把宿主自己那份文件当成"覆盖包新增的"直接删掉。
                if (OverlayLive(c, key, local) || !File.Exists(bakFile)) { skipped++; continue; }
                if (!TryAdoptOverlay(c, plan, key, dst, local, src, dstPath, Sha256Of(bakFile), nexusModId, packName))
                    return $"「{Path.GetFileName(hostDir)}」里的文件正被占用，覆盖未完成：{dst}";
                if (moved) notes?.Add(MovedNote(dst));
                adopted++;
                continue;
            }
            // 备份才是真原版：第二个包盖上来时磁盘上那份是第一个包的内容，
            // 拿它当"原版"存下来，还原时就把第一个汉化一起毁掉了。
            var origHash = File.Exists(dstPath)
                ? (File.Exists(bakFile) ? Sha256Of(bakFile) : Sha256Of(dstPath))
                : "";
            try
            {
                if (File.Exists(dstPath) && !File.Exists(bakFile)) File.Copy(dstPath, bakFile);
                Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);
                File.Copy(src, dstPath, overwrite: true);
                // 留一份包里原件到 AppData 副本目录：没有它，"关掉"就变成"卸载"，再也打不开了。
                // 放 AppDataDir 而不是缓存目录 —— 缓存是「缓存与存储」页能一键清的，清了就等于弄丢用户的包。
                var storeId = OverlayStoreId(src, local, packName);
                // 同一个宿主文件被第二个汉化包重盖时，上一条记录的副本目录就再没人引用了
                // （记录被覆盖，「删除」只认新 StoreId）→ 永久孤儿。新副本落盘成功后清掉旧的。
                DropReplacedStore(c, key, storeId);
                c.Overlays[key] = new OverlayRecord
                {
                    Host = plan.Host,
                    RelPath = dst,
                    PackName = packName,
                    NexusModId = nexusModId,
                    OriginalSha256 = origHash,
                    AppliedSha256 = Sha256Of(dstPath),
                    StoreId = storeId,
                    Enabled = true,
                };
                // 文件槽位只有一个：这份占上了，同一个文件上的其它包一律记成"关着" ——
                // 账和副本都留着，所以想换回哪一个，去卡片里拨它那颗开关就行。
                foreach (var sib in OverlaySiblings(c, c.Overlays[key], key))
                    c.Overlays[sib].Enabled = false;
                if (moved) notes?.Add(MovedNote(dst));
                written++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"「{Path.GetFileName(hostDir)}」里的文件正被占用，覆盖未完成：{dst}";
            }
        }
        // 全都逐字节相同且账都在 = 这个包本来就装好了：那是成功，不是"没有可写入的文件"
        if (written == 0 && adopted == 0 && skipped == 0) return LocService.Tr("覆盖包里没有可写入的文件");
        if (written == 0 && adopted == 0 && skipped > 0)
            notes?.Add(LocService.Tr("内容已经在位，这次一个文件都没改"));

        cfg.Save(c);
        modName = packName;
        AppLog.Info("Mods", $"[覆盖包] {packName} → {Path.GetFileName(hostDir)}，共写入 {written} 个文件"
            + (notes is { Count: > 0 } ? "（" + string.Join("；", notes) + "）" : "")
            + (adopted > 0 ? $"（另有 {adopted} 个内容早就在位、这次把账补回来了）" : "")
            + (skipped > 0 ? $"（另有 {skipped} 个与原版逐字节相同，跳过）" : ""));
        return null;
    }

    /// <summary>搬家提示。实测两种形状：包里裸在根目录的 <c>zh.json</c>（13273 / 44611），
    /// 以及压在角色名下的 <c>Caroline/default.json</c>（32901）—— 所以不能说"它在包里是根目录文件"。</summary>
    private static string MovedNote(string dst)
        => LocService.Tf("已放到宿主的「{0}」—— 按包里那个位置放不会生效", dst);

    /// <summary>这条覆盖在账上是不是"活着的"：记着、开着、副本文件还在。
    /// 副本没了就不算 —— 那样「关掉再打开」会打不开，正是要靠重装补回来的那种。</summary>
    private static bool OverlayLive(JuniGridConfig c, string key, string local)
    {
        if (!c.Overlays.TryGetValue(key, out var rec) || !rec.Enabled
            || string.IsNullOrEmpty(rec.StoreId)) return false;
        return File.Exists(Path.Combine(OverlayStoreDir(rec.StoreId), local));
    }

    /// <summary>把一份"文件已经在位但账没了"的覆盖重新记账：不重写文件（内容一样，重抄没有意义），
    /// 只补记录 + 补副本，让卡片、开关和「还原」重新有入口。</summary>
    private static bool TryAdoptOverlay(JuniGridConfig c, OverlayPlan plan, string key, string rel,
        string local, string src, string dst, string origHash, int? nexusModId, string packName)
    {
        try
        {
            var storeId = OverlayStoreId(src, local, packName);
            DropReplacedStore(c, key, storeId);
            c.Overlays[key] = new OverlayRecord
            {
                Host = plan.Host,
                RelPath = rel,
                PackName = packName,
                NexusModId = nexusModId,
                OriginalSha256 = origHash,
                AppliedSha256 = Sha256Of(dst),
                StoreId = storeId,
                Enabled = true,
            };
            foreach (var sib in OverlaySiblings(c, c.Overlays[key], key))
                c.Overlays[sib].Enabled = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>存一份包里原件到 AppData 副本目录，返回它的 storeId；存失败返回 ""（只失去"重新开启"的能力）。</summary>
    private static string OverlayStoreId(string src, string local, string packName)
    {
        var storeId = Guid.NewGuid().ToString("N")[..12];
        try
        {
            var keep = Path.Combine(OverlayStoreDir(storeId), local);
            Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
            File.Copy(src, keep, overwrite: true);
            return storeId;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Mods", "[覆盖包] 副本保存失败，之后将无法重新开启：" + ex.Message);
            return "";
        }
    }

    /// <summary>同一个槽位换了新副本，旧副本目录就再没人引用了（记录已被覆盖，「删除」只认新 StoreId）→ 删掉。</summary>
    private static void DropReplacedStore(JuniGridConfig c, string key, string storeId)
    {
        if (storeId.Length == 0 || !c.Overlays.TryGetValue(key, out var replaced)
            || replaced.StoreId.Length == 0 || replaced.StoreId.Equals(storeId, StringComparison.Ordinal)) return;
        try { Directory.Delete(OverlayStoreDir(replaced.StoreId), recursive: true); }
        catch (Exception ex)
        { AppLog.Warn("Mods", "[覆盖包] 上一个包的副本目录没清掉（只是多占点盘）: " + ex.Message); }
    }

    /// <summary>扫描时校验覆盖记录：磁盘上还是我们放上去的那一份吗？
    /// 宿主 mod 更新会把整个旧目录移进回收站再装新版，覆盖于是静默消失。
    /// 这里<b>只报不修</b>：自动重放会盖掉上游可能已经改好的官方翻译，判断错了没人知道。
    /// ByHost 给列表行的徽标用，StaleKeys 给清单弹窗逐行标红用。</summary>
    public static OverlayReport OverlayHealth(string gamePath, JuniGridConfig cfg)
    {
        var byHost = new Dictionary<string, (int Total, int Stale)>(StringComparer.OrdinalIgnoreCase);
        var stale = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 同一个宿主文件上可以有好几个汉化包各占一条账（磁盘槽位只有一个），所以按文件分组判：
        // 有人在位就只校验在位的那条，否则两份内容撞得一模一样时，关着的那条会被误报成失效。
        foreach (var grp in cfg.Overlays.GroupBy(kv => NormalizeOverlayHost(kv.Value.Host) + "|" + kv.Value.RelPath,
                     StringComparer.OrdinalIgnoreCase))
        {
            var rows = grp.ToList();
            var activeKey = rows.FirstOrDefault(kv => kv.Value.Enabled).Key;
            var hostDir = ResolveHostDir(gamePath, rows[0].Value.Host);
            var file = hostDir is null ? null
                : Path.Combine(hostDir, rows[0].Value.RelPath.Replace('/', Path.DirectorySeparatorChar));
            var hash = file is not null && File.Exists(file) ? Sha256Of(file) : null;
            foreach (var (key, rec) in rows)
            {
                var ours = hash is not null && hash == rec.AppliedSha256;
                // "正常" = 磁盘状态和开关一致：开着该是我们的文件在位，关着该是不在位
                var alive = activeKey is null ? !ours
                    : string.Equals(key, activeKey, StringComparison.OrdinalIgnoreCase) ? ours : true;
                var norm = NormalizeOverlayHost(rec.Host);
                var cur = byHost.TryGetValue(norm, out var v) ? v : (0, 0);
                byHost[norm] = (cur.Item1 + 1, alive ? cur.Item2 : cur.Item2 + 1);
                if (!alive) stale.Add(key);
            }
        }
        return new OverlayReport(byHost, stale);
    }

    /// <summary>覆盖记录的键：宿主 | 包内相对路径 | 来源槽位。
    /// 槽位用 N 网 modId（本地包退回包名），所以同一个文件能被多个汉化包各占一条记录 ——
    /// 磁盘上那个文件同一时刻只可能是其中一份，其余的账和副本都留着，开关一拨就换上去。</summary>
    public static string OverlayKey(string host, string relPath, int? nexusModId, string packName)
        => host + "|" + relPath + "|" + (nexusModId?.ToString()
            ?? (string.IsNullOrWhiteSpace(packName) ? "legacy" : packName));

    /// <summary>这个 N 网 mod 是不是正以「覆盖包」的形式装着、而且此刻真的在位。
    /// 汉化包住在别人的目录里，Mods 列表没有它那一行，所以详情页那种「Mods\ 里有没有这个文件夹」
    /// 的已装判据对它永远为空 —— 只能问覆盖账本，再拿磁盘上的实际哈希对一遍
    /// （宿主更新过一次 = 账在但文件不是我们的了，那不算已装，该让人重装）。</summary>
    public static bool OverlayAppliedForMod(string gamePath, JuniGridConfig cfg, int modId)
    {
        foreach (var rec in cfg.Overlays.Values)
        {
            if (rec.NexusModId != modId || !rec.Enabled) continue;
            var hostDir = ResolveHostDir(gamePath, rec.Host);
            if (hostDir is null) continue;
            var file = Path.Combine(hostDir, rec.RelPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(file) && Sha256Of(file) == rec.AppliedSha256) return true;
        }
        return false;
    }

    /// <summary>同一个宿主文件上的其它覆盖记录（不含自己）。文件槽位只有一个，谁在位谁算数。</summary>
    private static List<string> OverlaySiblings(JuniGridConfig c, OverlayRecord rec, string selfKey)
        => c.Overlays.Where(kv => !kv.Key.Equals(selfKey, StringComparison.OrdinalIgnoreCase)
                && NormalizeOverlayHost(kv.Value.Host).Equals(NormalizeOverlayHost(rec.Host), StringComparison.OrdinalIgnoreCase)
                && kv.Value.RelPath.Equals(rec.RelPath, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key).ToList();

    /// <summary>覆盖包文件的副本目录。开关要能来回，就得留着包里那份原件；
    /// 放 AppDataDir 而不是缓存目录 —— 缓存是「缓存与存储」页能一键清掉的，清了就等于弄丢用户的包。
    /// <para>根目录与配置认同一个 <c>JUNIGRID_CONFIG_DIR</c>：不设时仍是
    /// <c>%APPDATA%\JuniGrid\overlays</c>（逐字节同旧行为）；设了则连副本都落在临时目录里。
    /// 为什么补这道缝：这页既不在「缓存与存储」里露出、app 也不回收孤儿，
    /// 2026-10-01 几轮测试就在玩家真目录里堆出 171 个永久孤儿目录。</para></summary>
    public static string OverlayStoreDir(string storeId)
        => Path.Combine(ConfigService.ConfigDir, "overlays", storeId);

    /// <summary>把历史上记成「捆绑包壳顶层」的覆盖账改记到它真正的宿主行 <c>顶层/子包</c>
    /// （症状与判据见 <see cref="SplitShellPlanByBundleChild"/>；那条判据只在新装时生效，救不了已经落盘的账）。
    /// <para>键里带着宿主，所以键、<see cref="OverlayRecord.Host"/>、
    /// <see cref="OverlayRecord.RelPath"/> 三处一起改，AppData 那份副本也要跟着搬 ——
    /// 搬不动就整条不动：宁可留着这条看不见的账，也不能把「关掉再打开」弄坏。</para>
    /// 判据只看磁盘，改完就不再命中，所以每次启动跑一遍是空转，不需要版本戳。返回改了几条。</summary>
    public static int RelocateBundleTopOverlays(JuniGridConfig c, string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || c.Overlays.Count == 0) return 0;
        var moved = 0;
        foreach (var (key, rec) in c.Overlays.ToList())
        {
            var host = NormalizeOverlayHost(rec.Host);
            var i = rec.RelPath.IndexOf('/');
            if (host.Length == 0 || host.Contains('/') || i <= 0) continue;   // 已是子包形状、或直属顶层的文件：没有更深的行可归
            if (ResolveHostDir(gamePath, host) is not string topDir) continue; // 宿主目录都不在了：那是 Purge 的活
            if (File.Exists(Path.Combine(topDir, "manifest.json"))) continue;  // 顶层自己有行
            var seg = rec.RelPath[..i];
            if (!File.Exists(Path.Combine(topDir, seg, "manifest.json"))) continue;  // 那一层不是子包
            var newHost = host + "/" + seg;
            var newRel = rec.RelPath[(i + 1)..];
            if (!TryMoveOverlayCopy(rec, newRel, out var why))
            {
                AppLog.Warn("Mods", $"[覆盖包] 副本没搬动，这条账先按原样留着：{key}（{why}）");
                continue;
            }
            rec.Host = newHost;
            rec.RelPath = newRel;
            c.Overlays.Remove(key);
            c.Overlays[newHost + "|" + newRel + "|" + OverlaySlotOf(key)] = rec;
            moved++;
        }
        if (moved > 0)
            AppLog.Info("Mods", $"[覆盖包] {moved} 条账的宿主从「捆绑包壳顶层」改记到子包那一行");
        return moved;
    }

    /// <summary>副本跟着宿主形状搬位：开关是按「副本目录 + 宿主内路径」找那份留底的。
    /// 搬不动就整条账不动 —— 先改了账而副本还在旧路径上，「关掉再打开」就再也打不开了。</summary>
    private static bool TryMoveOverlayCopy(OverlayRecord rec, string newRel, out string? why)
    {
        why = null;
        if (rec.StoreId.Length == 0) return true;
        var sep = Path.DirectorySeparatorChar;
        var root = OverlayStoreDir(rec.StoreId);
        var oldCopy = Path.Combine(root, rec.RelPath.Replace('/', sep));
        var newCopy = Path.Combine(root, newRel.Replace('/', sep));
        if (!File.Exists(oldCopy)) return true;   // 旧位置没有：要么早就搬过，要么副本本来就没留住
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(newCopy)!);
            File.Move(oldCopy, newCopy, overwrite: true);
            return true;
        }
        catch (Exception ex) { why = ex.Message; return false; }
    }

    /// <summary>卸载宿主时清掉它名下的覆盖记录与副本目录。
    /// <b>不做还原</b>：宿主目录本身就要没了，把原版拷回去毫无意义，而副本留在 AppData 里
    /// 就再也无人引用 —— 记录也会变成一条永远"找不到宿主"的死账。</summary>
    private static void PurgeOverlaysOf(string folderName, ConfigService cfg)
    {
        var c = cfg.Current;
        if (c.Overlays.Count == 0) return;
        // 卸载顶层会连里面的子包一起没掉，所以「等于它」或「在它下面」的记录都要清；
        // 反过来只卸某个子包时，顶层和兄弟子包的覆盖不能跟着陪葬。
        var gone = NormalizeOverlayHost(folderName);
        var doomed = c.Overlays
            .Where(kv => NormalizeOverlayHost(kv.Value.Host).Equals(gone, StringComparison.OrdinalIgnoreCase)
                || NormalizeOverlayHost(kv.Value.Host).StartsWith(gone + "/", StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key).ToList();
        foreach (var key in doomed)
        {
            if (c.Overlays[key].StoreId is { Length: > 0 } sid)
                try { Directory.Delete(OverlayStoreDir(sid), recursive: true); } catch { }
            c.Overlays.Remove(key);
        }
        if (doomed.Count == 0) return;
        cfg.Save(c);
        AppLog.Info("Mods", $"[覆盖包] 宿主 {gone} 被卸载，连带清除 {doomed.Count} 条覆盖记录与副本");
    }

    /// <summary>开 / 关一条覆盖。开着 = 我们的文件在位；关掉 = 把原文件放回去，<b>但账和副本都留着</b>，
    /// 所以随时能再打开，不需要重新下载汉化包。彻底删账走 <see cref="RevertOverlay"/>。</summary>
    public string? SetOverlayEnabled(string gamePath, string key, bool enabled, ConfigService cfg, bool save = true)
    {
        var c = cfg.Current;
        if (!c.Overlays.TryGetValue(key, out var rec)) return LocService.Tr("找不到这条覆盖记录");
        var hostDir = ResolveHostDir(gamePath, rec.Host);
        if (hostDir is null) return LocService.Tf("找不到宿主 mod「{0}」", rec.Host);
        var local = rec.RelPath.Replace('/', Path.DirectorySeparatorChar);
        var dst = Path.GetFullPath(Path.Combine(hostDir, local));
        var bak = dst + OverlayBackupSuffix;
        // 状态没变就别动盘：关着的那条再"关"一次会把当前在位的另一个包的文件换成原版
        if (rec.Enabled == enabled) return null;
        try
        {
            if (enabled)
            {
                var keep = string.IsNullOrEmpty(rec.StoreId)
                    ? null : Path.Combine(OverlayStoreDir(rec.StoreId), local);
                if (keep is null || !File.Exists(keep))
                    return LocService.Tr("覆盖包的文件副本已丢失，无法重新开启。请重新安装这个覆盖包。");
                if (File.Exists(dst) && !File.Exists(bak)) File.Copy(dst, bak);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(keep, dst, overwrite: true);
                rec.AppliedSha256 = Sha256Of(dst);
                // 抢占槽位：同一个文件上其它包的记录一律记成"关着"（账和副本都留着）
                foreach (var sib in OverlaySiblings(c, rec, key)) c.Overlays[sib].Enabled = false;
            }
            else if (File.Exists(bak))
            {
                File.Copy(bak, dst, overwrite: true);   // 备份留着：下次开启时它才是"原版"
            }
            else if (File.Exists(dst) && string.IsNullOrEmpty(rec.OriginalSha256))
            {
                File.Delete(dst);                       // 这个文件本来就是覆盖包新增的
            }
            rec.Enabled = enabled;
            if (save) cfg.Save(c);
            AppLog.Info("Mods", $"[覆盖包] {(enabled ? "开启" : "关闭")} {rec.Host}/{rec.RelPath}");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return LocService.Tr("文件正被占用，操作失败");
        }
    }

    /// <summary>彻底撤销一条覆盖：还原原文件、删掉备份与副本、销账。
    /// 备份没了就不硬编 —— 宿主被整体重装过时，目录里那份新文件正是上游原版，没什么可还原的，
    /// 这时只把账销掉，并如实说明。</summary>
    public string? RevertOverlay(string gamePath, string key, ConfigService cfg, bool save = true)
    {
        var c = cfg.Current;
        if (!c.Overlays.TryGetValue(key, out var rec)) return LocService.Tr("找不到这条覆盖记录");
        var hostDir = ResolveHostDir(gamePath, rec.Host);
        var dst = hostDir is null
            ? null
            : Path.GetFullPath(Path.Combine(hostDir, rec.RelPath.Replace('/', Path.DirectorySeparatorChar)));
        var bak = dst is null ? null : dst + OverlayBackupSuffix;

        try
        {
            // 先算同一个文件上还留着谁：还剩别人，备份和槽位就还要用，不能顺手清掉
            var sibs = OverlaySiblings(c, rec, key);
            if (rec.Enabled)
            {
                if (bak is not null && File.Exists(bak))
                {
                    if (dst is not null) File.Copy(bak, dst, overwrite: true);
                    if (sibs.Count == 0) File.Delete(bak);
                }
                else if (dst is not null && rec.OriginalSha256.Length == 0 && File.Exists(dst))
                {
                    File.Delete(dst);   // 原本没这个文件，是覆盖包新增的 → 删掉就是还原
                }
            }
            else if (sibs.Count == 0 && bak is not null && File.Exists(bak))
            {
                File.Delete(bak);       // 删的是关着的那条，且这个文件上再没别的账 → 备份也没人用了
            }
            if (!string.IsNullOrEmpty(rec.StoreId))
                try { Directory.Delete(OverlayStoreDir(rec.StoreId), recursive: true); } catch { }
            c.Overlays.Remove(key);
            if (save) cfg.Save(c);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return LocService.Tr("文件正被占用，还原失败");
        }
    }

    /// <summary>整包开关：一个汉化包可能盖了上百个文件（LewdDew 那种重打包 146 个），
    /// 卡片上是一行，所以按「宿主 + 来源槽位」成组操作，只在最后落一次盘。</summary>
    public string? SetOverlayGroupEnabled(string gamePath, string host, string slot, bool enabled, ConfigService cfg)
    {
        var c = cfg.Current;
        var keys = GroupKeys(c, host, slot);
        string? err = null;
        foreach (var k in keys)
        {
            err = SetOverlayEnabled(gamePath, k, enabled, cfg, save: false);
            if (err is not null) break;
        }
        if (keys.Count > 0) cfg.Save(c);
        return err;
    }

    /// <summary>整包删除（还原 + 销账 + 删副本）。逐条走 RevertOverlay，最后统一落盘一次。</summary>
    public string? RevertOverlayGroup(string gamePath, string host, string slot, ConfigService cfg)
    {
        var c = cfg.Current;
        var keys = GroupKeys(c, host, slot);
        string? err = null;
        foreach (var k in keys)
        {
            err = RevertOverlay(gamePath, k, cfg, save: false);
            if (err is not null) break;
        }
        if (keys.Count > 0) cfg.Save(c);
        return err;
    }

    /// <summary>某个包在某个宿主名下的全部记录键（按路径排序，保证逐条操作顺序稳定）。</summary>
    private static List<string> GroupKeys(JuniGridConfig c, string host, string slot)
        => c.Overlays
            .Where(kv => NormalizeOverlayHost(kv.Value.Host).Equals(NormalizeOverlayHost(host), StringComparison.OrdinalIgnoreCase)
                && OverlaySlotOf(kv.Key).Equals(slot, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>这个包该不该走「直接写游戏本体 Content」那条通道（<see cref="InstallNew"/> 里那段判据，
    /// 抽出来是为了能在沙盒里单独验，不必造压缩包、不必真游戏目录）。
    /// <para>硬前提：<paramref name="anyXnb"/> 为假（整包一张 XNB 都没有）就一律不走 ——
    /// 纯 PNG 包是作者交给 CP 的形状，不是给本体覆盖用的。</para></summary>
    internal static bool OverrideChannelTakes(ContentOverrideService.PlanResult? plan,
        bool anyXnb, bool anyPngUnpaired, bool hasSmapi)
        => plan is not null && anyXnb
           && (plan.Installable || plan.TargetsSeen > 0 || !anyPngUnpaired || !hasSmapi);

    public string? InstallNew(string gamePath, string zipPath, out string? modName, int? nexusModId = null,
        string? requireUniqueId = null, string? portraiturePackName = null, ConfigService? cfg = null,
        List<string>? installNotes = null, IReadOnlyList<NexusRequirement>? packRequirements = null)
    {
        modName = null;
        string? temp = null;
        try
        {
            var manifest = ExtractToTemp(zipPath, "mod-install-", out temp);
            if (manifest is null)
            {
                // 依赖直装（带 requireUniqueId）的候选包没有 manifest，就是"这个包不是我们要的那个 mod"，
                // 交回哨兵值让调用方换下一个候选。绝不能退化成覆盖安装或一条把循环打断的错误文案。
                if (requireUniqueId is not null)
                {
                    TryDelete(temp);
                    return UidMismatchError;
                }
                // v1.2.8：包里带着「写明目标资产的目录」（LooseSprites.png/Cursors.png 这种约定）时，
                // 按目录名转成 CP 包 —— 这条【必须排在肖像转换之前】。旧写法是"没有清单但图 ⇒ 当立绘包"，
                // 于是一个界面包被转成 Portraits/daybg 这类游戏里根本不存在的目标
                //（实测 Starry Sky Interface：13 个文件只写出 5 条假立绘，剩下 8 个静默丢掉）。
                // 只在推出【非立绘家族】的目标时才接管，纯立绘包照旧走下面那条，UID 与立绘页连续性不变。
                var loosePackName = SanitizeFolderName(
                    string.IsNullOrWhiteSpace(portraiturePackName)
                        ? Path.GetFileNameWithoutExtension(zipPath)
                        : portraiturePackName);
                // v1.5.2：覆盖判定整条【提到转换之前】。原来它排在最后，于是只要包里的图
                // 在原版 Content 里有同名资产，就先被"烤进本体 / 转成 CP 包"截走，覆盖那条路
                // 连看都看不到。实机 34635「DaisyNiko's Earthy Recolour - Chinese」：
                // 包内顶层目录名【就是】已装的 [CP] DaisyNiko's Earthy Recolour，
                // 15 张图全在它的 assets/ 下有同名文件 —— 这是整条链里最强的一条证据，
                // 却被 Maps/fall_beach 在原版里也存在这件事压过去，转成了假 CP 包（03:29 重装修复后复现）。
                // 现在顺序是：先问"这是不是谁的覆盖包"，答案是才落盘；不是才轮到转换。
                var declaredOvPlan = ProbeOverlayForDeclaredHost(gamePath, temp!, packRequirements);
                OverlayFail ofail = OverlayFail.NoHostInPack;
                string hostGuess = "";
                var ovPlans = declaredOvPlan is not null ? One(declaredOvPlan)
                             : ProbeOverlay(gamePath, temp!, out ofail, out hostGuess, packRequirements);
                if (ovPlans is not null && cfg is not null)
                {
                    // 一个包可以盖在好几家上（捆绑包的汉化就是这种形状）：一家一份计划、逐家落盘逐家记账。
                    // 某一家被占用就停在那里 —— 已经写进去的那些条条在账上，能从对应那行的卡片里撤。
                    string? oerr = null;
                    foreach (var p in ovPlans)
                    {
                        oerr = ApplyOverlay(gamePath, temp!, p, cfg, nexusModId,
                            loosePackName, out modName, installNotes);
                        if (oerr is not null) break;
                    }
                    TryDelete(temp);
                    return oerr;
                }
                // v1.6.9：「纯净版覆盖」—— 满足下面任一条形状，就直接落游戏本体 Content：
                //  ① 包里的位图全都有同名 XNB 兄弟（含"整包只有 XNB"那种前 SMAPI 时代的包）：
                //     作者本来就是按"换掉某张原版图"画的，转成 CP 包反而多依赖一层 SMAPI，
                //     而这条路上大大小小的 bug 全出在转换那一层；
                //  ② 这台机器【没装 SMAPI】：那种机器上转出来的 CP 包根本不会生效，装它等于装个死重量。
                // 两条都还要加一道前提 ——【包里得有 XNB】。
                // 【一张 XNB 都没有的纯 PNG 包不走这条通道】（2026-10-07 定）：作者只交 PNG，
                // 意思就是"这包靠 Content Patcher 加载"，我们把它烤进游戏本体等于替作者改了发布方式；
                // 而且这个口子一开，"任何 mod 都能被我们另找一条路塞进本体"就挡不住了 ——
                // 没装 SMAPI 用不了 CP 包是事实，不是要绕过去的障碍。
                //（今天上午曾加过第 ③ 条"Prepare 认得出目标就接管"，为的是让扁平 PNG 包在装了 SMAPI
                //  的机器上也有地方去，那条已按上面的理由撤掉；判据与执行仍共用同一份 Prepare 结果。）
                ImageShapeOf(temp!, out var anyXnb, out var anyPngUnpaired);
                var cplan = cfg is not null ? ContentOverrideService.Prepare(gamePath, temp!) : null;
                if (OverrideChannelTakes(cplan, anyXnb, anyPngUnpaired, GameService.HasSmapiLoader(gamePath)))
                {
                    if (cplan.Installable)
                    {
                        // srcDir = temp：落盘前先把完整原包（结构一点不变的 mod）收进游戏目录旁，
                        // 「打开文件夹」打开的就是这份，而不是扁平的逐资产副本。
                        var cerr = ContentOverrideService.Commit(gamePath, cplan, loosePackName, nexusModId, cfg, out modName, temp);
                        TryDelete(temp);
                        if (cerr is null) modName = loosePackName;
                        return cerr;
                    }
                    // 认得出这是"冲着本体某张原版图画的包"、却一张都对不上 ⇒ 就地拒绝并点名，
                    // 绝不退给转换那条路。退回去就是当年那个 bug 的形状：13 个文件转成 5 条假立绘，
                    // 剩下的静默丢掉，而安装结果一片祥和。
                    if (cplan.TargetsSeen > 0)
                    {
                        TryDelete(temp);
                        modName = null;
                        return LocService.Tr("这个包的图对不上游戏本体当前版本的尺寸，已放弃安装，你原来装的什么都没动：")
                            + string.Join("；", cplan.Rejects.Take(6));
                    }
                }
                if (HasForeignAssetTarget(gamePath, temp!))
                {
                    var aerr = InstallLooseAssetPackAsCp(gamePath, temp!, loosePackName, nexusModId, out modName);
                    TryDelete(temp);
                    if (aerr is null) modName = loosePackName;
                    return aerr;
                }
                // 覆盖判定在转换之前就跑过了（见上面 ovPlans）：它说不是覆盖包，下面这几条
                // 转换通道才有权接管；全都没接管，就用它那次的判定解释"为什么放弃"。
                // 宿主其实认出来了、本机也装着，而包里的文件与那个 mod 自己的文件【一张都不同名】——
                // 这既不是"判断不出宿主"也不是"你还没装"，说那两句都是撒谎。
                // 实机 30539：登记前置 #16220，本机 [CP] Childhood Sweetheart Caroline 就是它，
                // 但包里 21 张叫 Portraits/Aerobics.png，宿主自己 30 张叫 Portraits/Caroline_Aerobics.png
                // —— 同名 0/21，盖上去一张都不会替换（是给另一个版本的这个 mod 画的）。
                if (cfg is not null && ovPlans is null
                    && !Directory.EnumerateFiles(temp!, "*.json", SearchOption.AllDirectories).Any()
                    && FindInstalledRequirementHost(gamePath, packRequirements) is string installedHost)
                {
                    TryDelete(temp);
                    modName = null;
                    return LocService.Tf("这个包登记的前置是「{0}」，本机也装着它 —— 可包里的文件跟那个 mod 自己的文件对不上路径，盖上去不会替换任何东西（多半是给另一个版本的它画的），已放弃安装。", installedHost);
                }
                // cfg 为 null 时连账都记不了，宁可不装（没有记录 = 不可逆的静默改动）
                TryDelete(temp);
                modName = null;
                if (cfg is null)
                    return LocService.Tr("这个压缩包没有 manifest.json，且当前流程无法为覆盖安装留下还原记录，已放弃安装。");
                return OverlayRefusal(ofail, gamePath, hostGuess, SoleHostCandidate(packRequirements));
            }
            var modRoot = ResolveModRoot(temp, out var allManifests);
            var isBundle = allManifests.Length > 1;

            // v1.2.0：依赖直装防呆 —— 候选包必须真的提供期望的 UniqueID（捆绑包任一
            // 子包命中即可，与 InstallUpdate 的 expectedUniqueId 语义一致）。
            if (requireUniqueId is not null && !ManifestsContainUid(allManifests, requireUniqueId))
            {
                TryDelete(temp);
                modName = null;
                return UidMismatchError;
            }

            // v1.1.5：与"没有 manifest 就拒绝"同一策略——manifest 是坏 JSON / 空文件 /
            // 根不是对象的也拒绝安装（否则会静默装上 SMAPI 无法加载的孤儿条目）。
            // ManifestJson 选项已容忍 SMAPI 风格的注释/尾逗号。
            foreach (var mf in allManifests)
            {
                try
                {
                    using var checkDoc = JsonDocument.Parse(ReadManifestText(mf), ManifestJson);
                    if (checkDoc.RootElement.ValueKind != JsonValueKind.Object)
                        throw new JsonException("root is not an object");
                }
                catch (Exception __checkEx)
                {
                    AppLog.Warn("ModService", "manifest 校验失败: " + __checkEx.Message);
                    return LocService.Tr("压缩包里的 manifest.json 已损坏或为空，不是完整的 mod，已放弃安装");
                }
            }

            try
            {
                using var doc = JsonDocument.Parse(ReadManifestText(manifest));
                if (doc.RootElement.TryGetProperty("Name", out var n))
                    modName = n.GetString();
            }
            catch (Exception __ex) { AppLog.Warn("ModService", __ex.Message); }

            // v1.2.4：带 manifest 的包也可能是「同一个 mod 的汉化重打包」（作者把整个目录重发一遍、
            // manifest 原封不动）。这种要按文件盖回它对应的那个已装目录，绝不能当成第二个 mod
            // 装到别处 —— 同 UniqueID 会互相判重吃掉，捆绑包结构还会被拆散。
            var prettyName = modName;
            string? uidTwin = null;
            if (cfg is not null && allManifests.Length == 1 && requireUniqueId is null
                && ProbeTranslatedRepackage(gamePath, temp!, modRoot, manifest, nexusModId, cfg.Current, out uidTwin) is { } rep)
            {
                var rerr = ApplyOverlay(gamePath, temp!, rep, cfg, nexusModId,
                    SanitizeFolderName(Path.GetFileNameWithoutExtension(zipPath)), out _);
                TryDelete(temp);
                modName = prettyName;      // 卡片标题用 manifest 里的名字，不用 zip 文件名
                return rerr;
            }
            // 撞 UID 的另一个 mod：既不覆盖也不装，原地停下。继续走正常安装会触发「同 UID 判重清扫」，
            // 把用户已装的另一个 mod 整个移进回收站 —— 那比覆盖更糟（覆盖至少还能从覆盖账里退回）。
            if (uidTwin is not null)
            {
                TryDelete(temp);
                modName = null;
                return LocService.Tf("这个 mod 与已装的「{0}」用了同一个 UniqueID，而两边素材不是同一套（不是它的汉化包）。"
                    + "同一个 UniqueID 在游戏里只能生效一个，所以已放弃安装 —— 你装的那份一个字节都没动。"
                    + "想换成这一份，先到 Mods 页把「{0}」卸载或禁用。", uidTwin);
            }

            if (isBundle)
            {
                // 捆绑包的展示名取所有子包名的共同部分。子包名都带 "[BL] "/"[CC] "
                // 这类标记前缀，剥掉之后再取公共前缀（直接按字符取会在 '[' 后一个
                // 字符就分叉，得到空串 → 退化成第一个子包名 "[BL] Downtown Zuzu"）
                var names = allManifests.Select(m => SafeManifestName(m))
                    .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList();
                var stripped = names.Select(n => StripPackTag(n).Trim()).ToList();
                var common = CommonPrefix(stripped).Trim().TrimEnd('-', '_').Trim();
                if (common.Length > 1)
                    modName = common;
                else
                {
                    // v1.1.5：子包名没有公共前缀（如 [CC] Buildings/Streets/Props）时，
                    // 回退为 zip 内的顶层目录名，而不是带标签的单个子包名。
                    // 不用 zip 文件名兜底——Nexus 直装链路的 zip 名是 direct-{id}-{fileId}。
                    var topName = Path.GetFileName(modRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (modRoot != temp && !string.IsNullOrWhiteSpace(topName))
                        modName = topName;
                }
            }

            var folderName = ChooseFolderName(Path.GetFileName(modRoot), modRoot == temp, modName, isBundle, zipPath);

            var newUids = PackUniqueIds(isBundle ? allManifests : new[] { manifest });

            // v1.5.2：同名旧目录移进回收站之前，先看它自己声明的是不是同一个 mod。
            // 原来这一步【只看文件夹名】—— 两个 UniqueID 完全不同的 mod 只要显示名撞车
            // （同一个 mod 被换作者重传、或两个作者都叫 "Cursors"），你已装那份就会被
            // 无声无息地扫进 Mods\.junigrid_trash，新那份顶上它的名字。测试舱实测：
            // 装 Name=HostOne / UniqueID=Some.Other 的包，已装的 HostOne / Host.One 整个进了回收站。
            // 这比"盖错文件"更糟，所以这里直接拒装，一个字节都不动。
            var clash = DifferentModInThatFolder(gamePath, folderName, newUids);
            if (clash is not null)
            {
                TryDelete(temp);
                modName = null;
                return LocService.Tf("这个 mod 与已装的「{0}」要用同一个文件夹名，但两边 manifest 的 UniqueID 不同 —— 它们是两个不同的 mod。已放弃安装，你装的那份一个字节都没动。", clash);
            }

            // v1.1.1：同名旧目录（启用/禁用两份）不再直接删除 —— 先移入回收站，安装失败回滚
            var dest = Path.Combine(gamePath, "Mods", folderName);
            string? stagedDest = null, stagedDisabled = null;
            if (Directory.Exists(dest))
            {
                try { stagedDest = StageExistingToTrash(gamePath, dest); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { TryDelete(temp); return $"「{folderName}」正被占用，无法备份旧版，已放弃安装（旧版未动）"; }
            }
            // v0.71.9：同名【禁用】目录（.folderName）也要清掉 —— 旧逻辑只查不带点的 dest，
            // 禁用 mod（Mods/.X）+ 新下载（Mods/X）会同时存在，扫描出来就是同一个 mod 两行。
            var destDisabled = Path.Combine(gamePath, "Mods", "." + folderName);
            if (Directory.Exists(destDisabled))
            {
                try { stagedDisabled = StageExistingToTrash(gamePath, destDisabled); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (stagedDest is not null) RestoreStaged(stagedDest, dest);
                    TryDelete(temp);
                    return $"「.{folderName}」正被占用，无法备份旧版，已放弃安装（旧版未动）";
                }
            }
            // v0.71.9：按 manifest UniqueID 兜底判重 —— 文件夹名不同但 UniqueID 相同
            // （如 ABC / ABC-1.2 / .ABC）也属于同一个 mod，一并清理，防任何形式的重复项。
            // v1.1.4：捆绑包收集全部子包的 UID —— 旧版安装器漏装产生的散装子包
            // （如 Mods 下单独的 "[BL] Downtown Zuzu"）也要一并收进回收站。
            try
            {
                if (newUids.Count > 0)
                {
                    // v1.1.5：子包级清扫（与 CleanupDuplicateCopies 的赢家语义不同——
                    // 新装的 zip 就是赢家，2.3 规则要求两套判定分开实现，不复用函数）。
                    // 基于 ScanRaw 条目遍历：天然排除 .junigrid_trash（修复"把回收站移进
                    // 回收站自身"）、天然带禁用状态；命中 UID 的旧子包目录移入回收站
                    // （保留层级），unique 子包与无关 mod 一律不动；命中目录=顶层本身
                    // （独立 mod）才整层移动。旧写法按顶层枚举+整层移动，会把与新版
                    // 捆绑包共享任一公共子包 UID 的其他捆绑包整个端走（T7 实测）。
                    foreach (var old in ScanRaw(gamePath))
                    {
                        if (old.Disabled || string.IsNullOrWhiteSpace(old.UniqueID)) continue;
                        if (!newUids.Contains(old.UniqueID.Trim())) continue;
                        // 安装目标本身（同名冲突已在上面处理），不再清扫
                        if (string.Equals(old.Folder, folderName, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(old.Folder, "." + folderName, StringComparison.OrdinalIgnoreCase))
                            continue;
                        var oldDir = Path.Combine(gamePath, "Mods", old.Folder.Replace('/', Path.DirectorySeparatorChar));
                        if (!Directory.Exists(oldDir)) continue;
                        try
                        {
                            var stagedDup = old.Folder.Contains('/')
                                ? StageSubpackageToTrash(gamePath, old.Folder)
                                : StageExistingToTrash(gamePath, oldDir);
                            AppLog.Warn("Mods", $"[判重清理] 同 UniqueID 旧副本子包 {old.Folder} 已移入回收站：{Path.GetFileName(stagedDup)}");
                        }
                        catch (Exception stageEx)
                        {
                            AppLog.Warn("Mods", $"[判重清理] {old.Folder} 移入回收站失败（可能被占用），保留原样: {stageEx.Message}");
                        }
                    }
                }
            }
            catch (Exception __ex) { AppLog.Warn("ModService", "UniqueID 判重清理失败: " + __ex.Message); }

            try
            {
                if (modRoot == temp)
                    CopyDirectoryContents(temp, dest);   // files at zip root — copy into named folder
                else
                    MoveDirectorySafe(modRoot, dest);   // 跨盘保护
            }
            catch
            {
                // v1.1.1：安装失败 → 清掉残缺半成品，把回收站里的旧版原路移回
                try { if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true); } catch { }
                if (stagedDest is not null) RestoreStaged(stagedDest, dest);
                if (stagedDisabled is not null) RestoreStaged(stagedDisabled, destDisabled);
                throw;
            }

            TryDelete(temp);
            // v1.1.2：把安装来源（Nexus modId）写进安装根的边车文件。SVE 这类包的 manifest
            // 会把 UpdateKeys 留成无效占位 "Nexus:???"，扫描侧凭 manifest 拿不到 ID，
            // 「已安装」识别与更新检查就断了 —— 边车兜底；重装/更新时随目录被新版覆盖。
            if (nexusModId is int nid)
                WriteNexusIdSidecar(dest, nid);
            return null;
        }
        catch (Exception ex)
        {
            if (temp is not null) TryDelete(temp);
            return ex.Message;
        }
    }

    // ---------------- Portraiture 框架 UID（依赖识别用；肖像转换器已移除） ----------------

    public const string PortraitureFrameworkUid = "Platonymous.Portraiture";
    private static readonly JsonSerializerOptions PackJsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string RenderPackContentJson(List<Dictionary<string, object?>> changes) =>
        JsonSerializer.Serialize(
            new Dictionary<string, object?> { ["Format"] = "2.5", ["Changes"] = changes }, PackJsonOpts);

    /// <summary>
    /// 自愈：把我们自己转出来的 CP 肖像包（UID = <c>JuniGrid.PortraitPack.*</c>）里按老规则写的
    /// content.json 按现行规则重算一遍。转换只发生在"安装那一刻"，所以规则修好之前装的包
    /// 一直是坏的 —— 用户看到的就是"改了代码为什么还是全错"（实测 Caroline Overhaul）。
    /// 图本来就在包的 assets/ 下，重算不需要原压缩包，用户也不必重装。
    /// 只在标记缺失/落后时动包；重算结果与现状一致就只补标记（不惊动扫描缓存签名）。
    /// 返回重写了 content.json 的包数。
    /// </summary>
    private static void ImageShapeOf(string dir, out bool anyXnb, out bool anyPngUnpaired)
    {
        anyXnb = false; anyPngUnpaired = false;
        try
        {
            var xnbStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string>? bitmapStems = null;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(f);
                if (ext.Equals(".xnb", StringComparison.OrdinalIgnoreCase))
                {
                    anyXnb = true;
                    xnbStems.Add(Path.GetFileNameWithoutExtension(f));
                }
                else if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
                    (bitmapStems ??= new List<string>()).Add(Path.GetFileNameWithoutExtension(f));
            }
            if (bitmapStems is null) return;
            // 一张找不到同名 XNB 的位图就足够否掉"这包只有 XNB"这个判断
            foreach (var stem in bitmapStems)
                if (!xnbStems.Contains(stem)) { anyPngUnpaired = true; break; }
        }
        catch { }
    }

    // ---------------- 安装来源边车（.junigrid.json） ----------------

    private const string SidecarFileName = ".junigrid.json";

    /// <summary>v1.1.2：通过本系统安装时明明知道 Nexus modId，落一份边车到安装根目录兜底
    /// （manifest UpdateKeys 缺失/无效时扫描侧的唯一关联来源）。写失败只影响该包的
    /// 更新识别，不当作安装失败。</summary>
    private static void WriteNexusIdSidecar(string destDir, int nexusModId)
    {
        try
        {
            var json = JsonSerializer.Serialize(
                new SidecarPayload { NexusModId = nexusModId },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            File.WriteAllText(Path.Combine(destDir, SidecarFileName), json);
        }
        catch (Exception ex)
        { AppLog.Warn("ModService", "写入安装来源边车失败: " + ex.Message); }
    }

    /// <summary>从 manifest 所在目录向上走到 Mods 根，取最近一层边车里的 nexusModId
    /// （捆绑包的所有子包继承安装根上的边车）。没有/损坏 → null。</summary>
    private static int? ReadNexusIdSidecar(string startDir, string modsDir)
    {
        try
        {
            var seps = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
            var root = Path.GetFullPath(modsDir).TrimEnd(seps);
            var dir = Path.GetFullPath(startDir).TrimEnd(seps);
            while (true)
            {
                var f = Path.Combine(dir, SidecarFileName);
                if (File.Exists(f))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(f));
                    if (doc.RootElement.ValueKind == JsonValueKind.Object
                        && doc.RootElement.TryGetProperty("nexusModId", out var v)
                        && v.TryGetInt32(out var id))
                        return id;
                }
                if (string.Equals(dir, root, StringComparison.OrdinalIgnoreCase)) return null;
                var parent = Directory.GetParent(dir)?.FullName;
                if (parent is null) return null;
                dir = parent.TrimEnd(seps);
                // 走出 Mods 根还没命中 → 没有（不读 Mods 之外的文件）
                if (!dir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(dir, root, StringComparison.OrdinalIgnoreCase))
                    return null;
            }
        }
        catch
        {
            return null;   // 边车损坏/不可读 → 当作没有
        }
    }

    private sealed class SidecarPayload
    {
        public int NexusModId { get; set; }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------
    /// <summary>Extracts zip to a unique temp dir; returns the shallowest manifest.json path.
    /// 支持「裸 XNB / 裸 PNG」肖像资源（Nexus 上无 manifest 的老包）：拷进临时目录后走 CP 转换。</summary>
    /// <summary>这个压缩包读完后，里面【有没有】manifest.json —— 决定它是不是"没有身份的包"
    /// （汉化/贴图覆盖包），也决定 InstallService 要不要为它去问一次页面登记的前置。
    /// <b>按魔数分派，不看扩展名</b>：实测 31795「Grandpa's Farm 中文翻译」下载下来叫
    /// <c>.zip</c>、头四个字节却是 <c>Rar!</c> —— 只看 zip 条目会一个都读不出来，
    /// 于是"没有 manifest"被判成"包内路径没说明宿主"，页面登记的那条前置也压根没去问。</summary>
    internal static bool ArchiveHasManifest(string zipPath)
    {
        try
        {
            switch (DetectArchiveFormat(zipPath))
            {
                case "zip":
                    using (var z = System.IO.Compression.ZipFile.OpenRead(zipPath))
                        return z.Entries.Any(e => e.Name.Length > 0
                            && e.Name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase));
                case "rar":
                    using (var a = SharpCompress.Archives.Rar.RarArchive.OpenArchive(zipPath))
                        return a.Entries.Any(e => !e.IsDirectory && ManifestLeaf(e.Key));
                    return false;
                case "7z":
                    using (var a = SharpCompress.Archives.SevenZip.SevenZipArchive.OpenArchive(zipPath))
                        return a.Entries.Any(e => !e.IsDirectory && ManifestLeaf(e.Key));
                    return false;
                default:
                    return false;   // 裸图片 / 空包 / 认不出来：本来就没有 manifest
            }
        }
        catch { return true; }   // 读不动就当它有身份，走正常安装，不去猜宿主
    }

    /// <summary>这个文件现在能不能当一个【完整的压缩包】用 —— 只为回答"下载缓存里那份还要不要再下一遍"。
    /// zip 认中央目录（半截下载没有它），rar/7z 让 SharpCompress 把条目表走一遍；
    /// 认不出格式 / 裸图片 / 一条都列不出来 ⇒ false，调用方照旧去下载，绝不拿一个坏文件去装。</summary>
    internal static bool ArchiveReadable(string zipPath)
    {
        try
        {
            if (!File.Exists(zipPath) || new FileInfo(zipPath).Length == 0) return false;
            switch (DetectArchiveFormat(zipPath))
            {
                case "zip":
                    using (var z = System.IO.Compression.ZipFile.OpenRead(zipPath))
                        return z.Entries.Count > 0;
                case "rar":
                    using (var a = SharpCompress.Archives.Rar.RarArchive.OpenArchive(zipPath))
                        return a.Entries.Any(e => !e.IsDirectory);
                case "7z":
                    using (var a = SharpCompress.Archives.SevenZip.SevenZipArchive.OpenArchive(zipPath))
                        return a.Entries.Any(e => !e.IsDirectory);
                default:
                    return false;
            }
        }
        catch { return false; }
    }

    private static bool ManifestLeaf(string? key)
        => key is not null && Path.GetFileName(key.Replace('\\', '/'))
            .Equals("manifest.json", StringComparison.OrdinalIgnoreCase);

    private static string? ExtractToTemp(string zipPath, string prefix, out string tempDir)
    {
        tempDir = Path.Combine(StoragePaths.DownloadsDir, prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        switch (DetectArchiveFormat(zipPath))
        {
            case "zip":
                // 中文作者压的包大量用 GBK 存条目名且不置 UTF-8 标志位。默认按 UTF-8 解会把
                // 非法字节换成 U+FFFD —— 症状：顶层目录变成「[CP]è¶…??」这种乱码，
                // 它还会当宿主名写进覆盖账，界面上就一直显示乱码。
                // 传 936 只在【没有 UTF-8 标志位】时生效，正常包不受影响。
                // 936 这个代码页由本类自己的静态构造注册（见 static ModService()）——
                // 以前靠 DepotDownloaderService 顺手注册，谁先跑是运气。
                ZipFile.ExtractToDirectory(zipPath, tempDir, System.Text.Encoding.GetEncoding(936));
                break;
            case "rar":
            case "7z":
                // v1.6.3：Nexus 上不少作者传 rar/7z（如 East Scarp 某文件）——
                // SharpCompress 纯托管解压（只解不压），zip 仍走 ZipFile 原路径
                ExtractManagedArchive(zipPath, tempDir);
                break;
            case "xnb":
            case "png":
                // 纯资源文件（无压缩包壳）：原样拷入临时目录，后续肖像转换管线可识别
                File.Copy(zipPath, Path.Combine(tempDir,
                    Path.GetFileName(zipPath.Length > 0 ? zipPath : "portrait" + Path.GetExtension(zipPath))),
                    overwrite: true);
                break;
            case "html":
                throw new InvalidOperationException("下载到的是网页而不是压缩包（可能被网络拦截），请稍后重试。");
            case "empty":
                throw new InvalidOperationException("压缩包是空的或不完整（下载中断），请重新下载。");
            default:
                throw new InvalidOperationException("压缩包损坏或格式未知（可能下载不完整），请重新下载。");
        }
        return Directory
            .GetFiles(tempDir, "manifest.json", SearchOption.AllDirectories)
            .OrderBy(p => p.Length)
            .FirstOrDefault();
    }

    /// <summary>压缩包/资源格式按文件头判定：zip / rar / 7z / xnb / png / html / empty / unknown。
    /// <c>internal</c>：下载缓存里"叫什么扩展名"不可信（安装器一律把落盘名写成 .zip，
    /// 实机 2026-10-08 的 193 个缓存文件里有 14 个其实是 RAR/7z），别处要认格式只能走这同一个文件头判定。</summary>
    internal static string DetectArchiveFormat(string path)
    {
        var head = new byte[8];
        int read;
        using (var s = File.OpenRead(path))
            read = s.Read(head, 0, head.Length);

        if (read >= 2 && head[0] == (byte)'P' && head[1] == (byte)'K')
            return "zip";
        if (read >= 4 && head[0] == (byte)'R' && head[1] == (byte)'a' && head[2] == (byte)'r' && head[3] == (byte)'!')
            return "rar";
        if (read >= 6 && head[0] == (byte)'7' && head[1] == (byte)'z' && head[2] == 0xBC
            && head[3] == 0xAF && head[4] == 0x27 && head[5] == 0x1C)
            return "7z";
        // 裸 XNB（老式肖像/精灵资源，Nexus 可能直接传这个文件而非 zip）
        if (read >= 3 && head[0] == (byte)'X' && head[1] == (byte)'N' && head[2] == (byte)'B')
            return "xnb";
        // 裸 PNG
        if (read >= 4 && head[0] == 0x89 && head[1] == (byte)'P' && head[2] == (byte)'N' && head[3] == (byte)'G')
            return "png";
        if (read >= 1 && head[0] == (byte)'<')
            return "html";
        if (read < 4)
            return "empty";
        return "unknown";
    }

    /// <summary>
    /// rar / 7z 托管解压（SharpCompress，纯托管 unrar 实现）。逐条守路径：
    /// 压缩包里带 ../ 或绝对路径的条目不得越出解压根（zip-slip 防御）。
    /// </summary>
    private static void ExtractManagedArchive(string path, string destDir)
    {
        var root = Path.GetFullPath(destDir);
        // SharpCompress 0.50：ArchiveFactory.Open 已移除，按格式用各自 OpenArchive
        using SharpCompress.Archives.IArchive archive = DetectArchiveFormat(path) == "rar"
            ? SharpCompress.Archives.Rar.RarArchive.OpenArchive(path)
            : SharpCompress.Archives.SevenZip.SevenZipArchive.OpenArchive(path);
        foreach (var entry in archive.Entries)
        {
            if (entry.IsDirectory) continue;
            var key = (entry.Key ?? "").Replace('\\', '/');
            if (key.Length == 0 || key.Contains("../") || Path.IsPathRooted(key))
                continue;   // 越界/可疑条目直接跳过，不写盘
            var dest = Path.GetFullPath(Path.Combine(root, key));
            if (!dest.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.WriteToFile(dest, new SharpCompress.Common.ExtractionOptions { Overwrite = true });
        }
    }

    /// <summary>
    /// v1.1.4：解压目录里确定安装根。多数 zip 只有一个 manifest → 安装其所在文件夹；
    /// 多分包捆绑包（一个 zip 内多个 manifest 子包，如 Downtown Zuzu 的
    /// [BL]/[CC]/[CP]/[DLL]/[FTM]/[MFM]/[TS]）如果只装第一个 manifest 所在子包，
    /// 其余分包会留在临时目录被删掉 —— 用户"只装上了一个 [BL]"的根因。
    /// 规则：所有 manifest 同属一个顶层目录 → 安装该顶层目录（整体）；散落多个顶层目录
    /// → 整个解压根目录作为安装内容。
    /// </summary>
    private static string ResolveModRoot(string temp, out string[] manifests)
    {
        manifests = Directory.GetFiles(temp, "manifest.json", SearchOption.AllDirectories);
        if (manifests.Length <= 1)
            return Path.GetDirectoryName(manifests.FirstOrDefault() ?? temp)!;

        var tops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mf in manifests)
            tops.Add(Path.GetRelativePath(temp, Path.GetDirectoryName(mf)!)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0]);
        return tops.Count == 1 ? Path.Combine(temp, tops.First()) : temp;
    }

    private static string? SafeManifestName(string manifestPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(ReadManifestText(manifestPath));
            return doc.RootElement.TryGetProperty("Name", out var n) ? n.GetString() : null;
        }
        catch { return null; }
    }

    /// <summary>v1.2.0：一组 manifest 里是否存在 UniqueID 与期望值（忽略大小写）一致的子包。</summary>
    private static bool ManifestsContainUid(string[] manifestPaths, string expectedUid)
    {
        foreach (var mf in manifestPaths)
        {
            try
            {
                using var doc = JsonDocument.Parse(ReadManifestText(mf), ManifestJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("UniqueID", out var u)
                    && string.Equals(u.GetString(), expectedUid, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { }
        }
        return false;
    }

    /// <summary>剥掉子包名开头的标记前缀，如 "[BL] Downtown Zuzu" → "Downtown Zuzu"。
    /// SMAPI 社区惯用 "[XX] " 标注内容包类型（[CP]/[CC]/[DLL]…）。</summary>
    private static string StripPackTag(string name)
    {
        var n = name.TrimStart();
        while (n.StartsWith("["))
        {
            var close = n.IndexOf(']');
            if (close < 0) break;
            n = n[(close + 1)..].TrimStart();
        }
        return n;
    }

    /// <summary>取一组名字的最长公共前缀（按字符），用于给捆绑包起展示名。
    /// 只有前缀在某个名字中间被截断时才去掉残缺的尾词；所有名字一致时原样返回。</summary>
    private static string CommonPrefix(List<string> names)
    {
        if (names.Count == 0) return "";
        var prefix = names[0];
        foreach (var n in names.Skip(1))
        {
            var len = Math.Min(prefix.Length, n.Length);
            var i = 0;
            while (i < len && prefix[i] == n[i]) i++;
            prefix = prefix[..i];
            if (prefix.Length == 0) break;
        }
        // 名字完全一致（或前缀恰为某个名字全长）→ 不需要裁尾
        if (prefix.Length == 0 || prefix.Length >= names.Min(x => x.Length)) return prefix;
        // 前缀截在词中间（如 "Downtown Zu"）→ 回退到最近的空格/连字符
        var cut = prefix.Length;
        while (cut > 0 && prefix[cut - 1] != ' ' && prefix[cut - 1] != '-') cut--;
        return prefix[..cut];
    }


    /// <summary>
    /// 跨盘安全的目录移动：Directory.Move 只支持同卷，跨卷会抛
    /// "Source and destination path must have identical roots"。
    /// 这里检测到不同盘符时改用"复制 + 删源"。
    /// </summary>
    private static void MoveDirectorySafe(string src, string dest)
    {
        try
        {
            Directory.Move(src, dest);
        }
        catch (IOException)
        {
            // 跨盘 / 目标目录已存在 / 句柄占用 → 用复制+删源
            Directory.CreateDirectory(dest);
            CopyDirectoryContents(src, dest);
            try { Directory.Delete(src, recursive: true); } catch (Exception __ex) { AppLog.Warn("ModService", __ex.Message); }
        }
    }
    /// <summary>递归拷贝目录内容（ModService / UpdateService / StorageService 共用实现）。
    /// skipTrash：跳过 Mods 回收站目录（junigrid_trash / .junigrid_trash，可达数 GB）——
    /// 备份场景用它，回收站留在原位不受影响。</summary>
    /// <summary>把 src 的内容并进 dest。skipExisting：目标已存在的文件一律不动 —— 用于「把更新前的
    /// 快照并回去」这类场景，目标在那之后可能已被安装器或用户改过，拿旧内容盖回去就是静默回滚。</summary>
    public static void CopyDirectoryContents(string src, string dest, bool skipTrash = false, bool skipExisting = false)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.GetFiles(src))
        {
            var target = Path.Combine(dest, Path.GetFileName(f));
            if (skipExisting && File.Exists(target)) continue;
            File.Copy(f, target, overwrite: true);
        }
        foreach (var d in Directory.GetDirectories(src))
        {
            var name = Path.GetFileName(d);
            if (skipTrash && (name.Equals("junigrid_trash", StringComparison.OrdinalIgnoreCase)
                           || name.Equals(".junigrid_trash", StringComparison.OrdinalIgnoreCase))) continue;
            CopyDirectoryContents(d, Path.Combine(dest, name), skipTrash, skipExisting);
        }
    }

    /// <summary>
    /// v1.1.1：把 Mods 下已存在的旧版目录整体改名移入 .junigrid_trash（同盘瞬时原子改名，
    /// 失败即整体失败，绝不出现"删一半"）。命名沿用 Uninstall 的时间戳+序号兜底规则。
    /// 返回 staging 路径；被占用等改名失败直接抛异常，由调用方决定放弃或回滚。
    /// </summary>
    internal static string StageExistingToTrash(string gamePath, string existingDir)
    {
        var trash = EnsureTrashReady(gamePath);
        var baseName = Path.GetFileName(existingDir).TrimStart('.');
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var staging = Path.Combine(trash, baseName + "_" + stamp);
        for (var n = 2; Directory.Exists(staging); n++)
            staging = Path.Combine(trash, $"{baseName}_{stamp}_{n}");
        Directory.Move(existingDir, staging);
        return staging;
    }

    /// <summary>子包级清理专用：把子包目录（相对路径如 "Top/Sub"）移入回收站并保留
    /// 捆绑包层级（trash/Top/Sub[_时间戳]），与 Uninstall 的子包路径约定一致 ——
    /// 手动还原时把顶层文件夹拖回 Mods 即可。</summary>
    private static string StageSubpackageToTrash(string gamePath, string folder)
    {
        var trash = EnsureTrashReady(gamePath);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var staging = Path.Combine(trash, folder.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(staging))
            staging = staging + "_" + stamp;
        for (var n = 2; Directory.Exists(staging); n++)
            staging = staging + "_" + n;
        Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
        Directory.Move(Path.Combine(gamePath, "Mods", folder.Replace('/', Path.DirectorySeparatorChar)), staging);
        return staging;
    }

    // ------------------------------------------------------------------
    // v1.1.5：回收站保护 —— 防止用户在资源管理器里误改名/误删 .junigrid_trash。
    // M1 隐藏属性：Hidden|System，资源管理器默认视图不可见（误触概率趋零）；
    // M2 运行期句柄锁：持有不含 FILE_SHARE_DELETE 的目录句柄，应用开着时外部对
    //    trash 本身的改名/删除被 Windows 直接拒绝；trash 内子目录的存入/还原
    //    不受影响（句柄只锁目录自身）。进程退出操作系统自动释放。
    // trash 被强行删掉也能自愈：下次需要时 EnsureTrashReady 自动重建空回收站。
    // ------------------------------------------------------------------

    private static readonly TrashGuard TrashGuardInstance = new();

    /// <summary>回收站统一入口：确保目录存在 + 隐藏/系统属性 + 运行期保护锁。
    /// 所有需要 touch 回收站的代码一律走这里，不要自己 Path.Combine+CreateDirectory。</summary>
    private static string EnsureTrashReady(string gamePath)
    {
        var trash = StoragePaths.GameTrashDir(gamePath);
        Directory.CreateDirectory(trash);
        ProtectTrash(trash);
        return trash;
    }

    /// <summary>给已存在的回收站补隐藏属性 + 运行期保护锁（幂等，可重复调用）。</summary>
    private static void ProtectTrash(string trash)
    {
        try
        {
            var di = new DirectoryInfo(trash);
            if ((di.Attributes & (FileAttributes.Hidden | FileAttributes.System))
                != (FileAttributes.Hidden | FileAttributes.System))
                di.Attributes |= FileAttributes.Hidden | FileAttributes.System;
        }
        catch { }
        TrashGuardInstance.Acquire(trash);
    }

    private sealed class TrashGuard
    {
        private SafeFileHandle? _handle;
        private string? _path;

        public void Acquire(string trashDir)
        {
            // v1.1.6：旧句柄指向的目录若已被外部删除重建，句柄虽未关闭但已保护不了新目录
            //（旧写法直接短路 return，重建后的回收站裸奔）—— 路径失效就释放重取
            if (_handle is { IsInvalid: false, IsClosed: false }
                && _path is not null && Directory.Exists(_path))
                return;
            Release();
            _path = trashDir;
            try
            {
                _handle = NativeMethods.CreateFile(trashDir,
                    NativeMethods.GENERIC_READ,
                    NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                    IntPtr.Zero, NativeMethods.OPEN_EXISTING,
                    NativeMethods.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
                // 打不开（如目录刚被用户删了）就静默放弃——下次 EnsureTrashReady 再试
            }
            catch { /* 保护锁绝不影响主流程 */ }
        }

        private void Release()
        {
            try { _handle?.Dispose(); } catch { }
            _handle = null;
        }
    }

    private static class NativeMethods
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint FILE_SHARE_READ = 0x1;
        public const uint FILE_SHARE_WRITE = 0x2;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess,
            uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);
    }

    /// <summary>安装失败回滚：把回收站里的旧版原路移回 Mods；移不回去就留在回收站并记日志，
    /// 用户可在设置 → 游戏卸载回收站 里手动还原。</summary>
    private static void RestoreStaged(string staging, string dest)
    {
        try { Directory.Move(staging, dest); }
        catch (Exception __ex)
        { AppLog.Warn("Mods", $"[回滚] 旧版未能移回，已保留在回收站：{Path.GetFileName(staging)} - {__ex.Message}"); }
    }

    /// <summary>定新装 mod 落进 Mods 的目录名。
    /// <para>包内目录名带 <c>U+FFFD</c> 时【不要用它】：那是 zip 条目名没设 UTF-8 标志、字节其实是 GBK，
    /// 而 .NET 按 UTF-8 硬解出来的结果（2026-10-02 实测 Cat Valley 39379 的「[CP]猫露谷物语(Interface)」，
    /// 落盘成 <c>[CP]è¶…(Interface)</c>）。U+FFFD 是不可逆的 —— 一旦写进目录名，原名再也读不回来，
    /// 界面（覆盖包面板标题、Mod 行）还会把乱码直接甩给用户看。这种改走 manifest 的 <c>Name</c>：
    /// 文件内容一直是 UTF-8，从来没坏过。
    /// <para>bundles 的 zip 名是 <c>nxm-&lt;id&gt;-&lt;fileId&gt;</c> / <c>direct-…</c>，本来就不可读，
    /// 所以名字已损坏时不要再拿它兜底，直接用 manifest Name。</para></summary>
    internal static string ChooseFolderName(string innerName, bool atZipRoot, string? manifestName,
        bool isBundle, string zipPath)
    {
        var lossy = innerName.Contains('\uFFFD', StringComparison.Ordinal);
        if (innerName.Length > 0 && !atZipRoot && !lossy) return innerName;
        var zipName = isBundle && !lossy ? Path.GetFileNameWithoutExtension(zipPath) : null;
        return SanitizeFolderName(zipName ?? manifestName ?? "NewMod");
    }

    private static string SanitizeFolderName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        var trimmed = name.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? "NewMod" : trimmed;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception __ex) { AppLog.Warn("ModService", __ex.Message); }
    }

    /// <summary>
    /// 读取 manifest 文本：剥掉星露谷 mod 常见但非严格的注释（/* */ 与 //），
    /// 空文件 / 无法读取返回 null，调用方据此用文件夹名兜底，避免整个 mod 被吞掉。
    /// </summary>
    private static string? TryReadManifestCleaned(string manifestPath)
    {
        try
        {
            var text = ReadManifestText(manifestPath);
            if (string.IsNullOrWhiteSpace(text)) return null;
            // 直接返回原文——Newtonsoft JObject.Parse 本身宽松，
            // 可接受 SMAPI manifest 的尾随逗号、行内 // 注释、/* */ 注释。
            return text;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>连一层 manifest 都没有的子目录（或空目录等），用文件夹名兜底显示。</summary>
    private static ModEntry OrphanEntry(string modsDir, string modDir, string? note = null)
    {
        var folderName = Path.GetRelativePath(modsDir, modDir).Replace('\\', '/');
        return new ModEntry
        {
            Folder = folderName,
            LastWrite = Directory.GetLastWriteTime(Path.Combine(modsDir, folderName)),
            Disabled = folderName.StartsWith('.') || folderName.Contains("/."),
            Name = Path.GetFileName(modDir.TrimEnd(Path.DirectorySeparatorChar)),
            Version = "?",
            Description = note ?? "⚠ 该文件夹没有 manifest.json",
            HasManifest = false,
        };
    }

    /// <summary>
    /// 从单个 manifest.json 构建 ModEntry。
    /// 读不到 manifest / 空 / 解析失败 → 返回 null（调用方用 OrphanEntry 兜底）。
    /// 解析 Dependencies 数组和 ContentPackFor.UniqueID 作为"依赖"（供缺失检测）。
    /// </summary>
    private static ModEntry? BuildModEntry(string modsDir, string ownerDir, string manifestPath,
        string? displayNameOverride = null)
    {
        var cleaned = TryReadManifestCleaned(manifestPath);
        if (cleaned is null)
        {
            return null;
        }

        try
        {
            // Newtonsoft 宽松解析：SMAPI manifest 允许尾随逗号/行内注释，
            // 严格 JsonDocument 会误判"无清单"。JObject.Parse 一律兼容。
            var root = Newtonsoft.Json.Linq.JObject.Parse(cleaned);

            // UpdateKeys：Nexus / GitHub
            int? nexusId = null;
            string? githubRepo = null;
            if (root["UpdateKeys"] is Newtonsoft.Json.Linq.JArray uksArr)
            {
                foreach (var uk in uksArr)
                {
                    var s = uk?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)uk! : "";
                    if (nexusId is null
                        && s.StartsWith("Nexus:", StringComparison.OrdinalIgnoreCase))
                    {
                        // 形如 "Nexus:23135@main"：去掉 @ 后的 GitHub 风格后缀再取纯数字 ID
                        var idPart = s[6..].Split('@')[0].Trim();
                        if (int.TryParse(idPart, out var id))
                            nexusId = id;
                    }
                    else if (githubRepo is null
                        && s.StartsWith("GitHub:", StringComparison.OrdinalIgnoreCase))
                    {
                        var r = s[7..].Trim().TrimEnd('/');
                        if (r.Contains('/')) githubRepo = r;
                    }
                    if (nexusId is not null && githubRepo is not null) break;
                }
            }

            // v1.1.2：UpdateKeys 没有/无效（如 SVE 1.15.11 写成 "Nexus:???"，数字位解析不出）
            // → 回退读安装边车，恢复 Nexus 关联（「已安装」徽标 + 更新检查都靠它）
            if (nexusId is null)
                nexusId = ReadNexusIdSidecar(Path.GetDirectoryName(manifestPath) ?? modsDir, modsDir);

            // Dependencies：拆成「必需」与「可选」两份。SMAPI 的 IsRequired(旧名 Required) 默认 true，
            // 显式标 false 的是可选依赖（可缺但不应报缺失）→ 单独存，别混进缺失红字。
            // MinimumVersion 也留下（按 UID 索引）：SMAPI 判的是「装了且够新」，版本不够它照样把依赖方
            // 整条 Skipped（实测 完美手册 1.0.0 因本机 StardewUI 0.6.1 < 要求的 0.6.4-unofficial-mushymato.0
            // 被跳过）。只判「装没装」的旧口径对这种情况全程静音。
            var deps = new List<string>();
            var optDeps = new List<string>();
            var depMin = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root["Dependencies"] is Newtonsoft.Json.Linq.JArray depsArr)
            {
                foreach (var item in depsArr)
                {
                    if (item is not Newtonsoft.Json.Linq.JObject io) continue;
                    var s = DepString(io, "UniqueID");
                    if (string.IsNullOrWhiteSpace(s)) continue;

                    var min = DepString(io, "MinimumVersion")?.Trim();
                    if (!string.IsNullOrEmpty(min)) depMin[s.Trim()] = min!;

                    if (DepIsRequired(io)) deps.Add(s);
                    else optDeps.Add(s);
                }
            }

            // ContentPackFor：这是"该 pack 需要宿主框架"。宿主不是用户硬装的缺失，
            // 很多包缺框架也只是动态功能缺失，不当"缺失依赖"误报 → 存单独字段，不进 deps。
            string? contentPackHost = null;
            if (root["ContentPackFor"] is Newtonsoft.Json.Linq.JObject cpfObj)
            {
                contentPackHost = cpfObj["UniqueID"]?.Type == Newtonsoft.Json.Linq.JTokenType.String
                    ? (string)cpfObj["UniqueID"]! : null;
            }

            // v0.45.0：分类标签 —— manifest 里有 EntryDll 的是代码 Mod（含 C# 逻辑），
            // 声明了 ContentPackFor 的是内容包（依附宿主框架，如 Content Patcher）。
            var hasEntryDll = root["EntryDll"]?.Type == Newtonsoft.Json.Linq.JTokenType.String;
            var category = hasEntryDll && contentPackHost is not null ? "代码 · 内容包"
                : hasEntryDll ? "代码 Mod"
                : contentPackHost is not null ? "内容包"
                : "";

            // 相对 Mods/ 的路径作为唯一 folder 标识（多级结构也标清，如 "SVE/[CP] xx"）
            var manifestDir = Path.GetDirectoryName(manifestPath) ?? ownerDir;
            var relFolder = Path.GetRelativePath(modsDir, manifestDir).Replace('\\', '/');
            var dis = relFolder.StartsWith('.') || relFolder.Contains("/.");

            var folderName = string.IsNullOrEmpty(displayNameOverride)
                ? Path.GetFileName(ownerDir.TrimEnd(Path.DirectorySeparatorChar))
                : displayNameOverride;

            return new ModEntry
            {
                Folder = relFolder,
                LastWrite = Directory.GetLastWriteTime(Path.Combine(modsDir, relFolder)),
                Disabled = dis,
                Name = root["Name"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)root["Name"]! : folderName,
                Author = root["Author"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)root["Author"]! : "Unknown",
                Version = root["Version"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)root["Version"]! : "?",
                Description = root["Description"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)root["Description"]! : "",
                // v1.08：忽略大小写读取 —— SMAPI 内置 mod 的 manifest 写的是 "UniqueId"，
                // 严格匹配会读空导致同一 mod 的禁用/启用副本无法判重（列表出现两行）。
                UniqueID = (root.GetValue("UniqueID", StringComparison.OrdinalIgnoreCase)?.Type == Newtonsoft.Json.Linq.JTokenType.String
                    ? (string)root.GetValue("UniqueID", StringComparison.OrdinalIgnoreCase)! : ""),
                NexusModId = nexusId,
                GitHubRepo = githubRepo,
                Dependencies = deps,
                OptionalDependencies = optDeps,
                DependencyMinVersions = depMin,
                ContentPackIds = contentPackHost is not null ? new List<string> { contentPackHost } : new List<string>(),
                HasManifest = true,
                Category = category,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>依赖项里的字符串字段，忽略大小写取（作者写 "uniqueId" 的也有）。</summary>
    private static string? DepString(Newtonsoft.Json.Linq.JObject dep, string key)
    {
        var tk = dep.GetValue(key, StringComparison.OrdinalIgnoreCase);
        return tk?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)tk : null;
    }

    /// <summary>这条依赖是不是必需的（SMAPI 语义：默认必需，显式标 false 才算可选）。
    /// 作者的写法很杂：真布尔 false、字符串 "false"（[CP] Miku Mod Plus 的 ChaseXavier.Miku 就是）、
    /// 数字 0 —— SMAPI 用 Newtonsoft 反序列化，这三种都会落成 false；我们原先只认布尔，
    /// 于是可选依赖被当成硬缺失，在 Mods 页刷一条红字「缺少依赖」。
    /// 读不懂的写法（"maybe"）按必需处理：宁可多提示一条，不能漏掉真缺的东西。</summary>
    private static bool DepIsRequired(Newtonsoft.Json.Linq.JObject dep)
    {
        var tk = dep.GetValue("IsRequired", StringComparison.OrdinalIgnoreCase)
                 ?? dep.GetValue("Required", StringComparison.OrdinalIgnoreCase);
        if (tk is null || tk.Type == Newtonsoft.Json.Linq.JTokenType.Null) return true;
        try { return (bool)tk; } catch { return true; }
    }
}

public sealed class ModEntry
{
    public string Folder { get; set; } = "";
    /// <summary>mod 文件夹的最后写入时间，用于"按时间排序"。</summary>
    public DateTime LastWrite { get; set; }
    public bool Disabled { get; set; }
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
    public string UniqueID { get; set; } = "";           // manifest 里的唯一标识，更新包校验用
    public int? NexusModId { get; set; }   // from manifest UpdateKeys "Nexus:<id>"
    public string? GitHubRepo { get; set; }  // from manifest UpdateKeys "GitHub:<owner>/<repo>"（免费直下）
    public List<string> Dependencies { get; set; } = new();  // 本 mod 依赖的 UniqueID（含 ContentPackFor 宿主）
    /// <summary>manifest 里作者标了 IsRequired=false 的依赖：缺了不影响用，不进「缺少依赖」红字，
    /// 只在行尾给一条可忽略的浅色提示。</summary>
    public List<string> OptionalDependencies { get; set; } = new();
    /// <summary>依赖 UID → manifest 声明的 MinimumVersion（只收写了版本的条目）。
    /// 在 SMAPI 那边「装了但版本低于它」等于没装 —— 依赖方整条被 Skipped。</summary>
    public Dictionary<string, string> DependencyMinVersions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> ContentPackIds { get; set; } = new(); // 它作为内容包时依赖的宿主 mod UniqueID（合并进 Dependencies 用于缺失检测）
    public bool HasManifest { get; set; } = true;   // false = 该文件夹没有有效 manifest（用文件夹名兜底）
    /// <summary>v0.45.0：分类标签（仿 PCL2），在列表行简介前显示。代码 Mod / 内容包 / 代码·内容包；都不是则为空。</summary>
    public string Category { get; set; } = "";

    /// <summary>
    /// JuniGrid 自己转出来的 CP 包：立绘那条是 <c>JuniGrid.PortraitPack.*</c>，
    /// 按目录名转的裸素材包（界面/小马/其它画面）是 <c>JuniGrid.AssetPack.*</c>。
    /// manifest Version 固定 1.0.0，NexusModId 只是原素材包的来源边车 ——
    /// 绝不能拿 N 网原包版本号去比本地 1.0.0，否则永远显示「可更新」。
    /// </summary>
    public bool IsConvertedPack =>
        !string.IsNullOrEmpty(UniqueID)
        && (UniqueID.StartsWith("JuniGrid.PortraitPack.", StringComparison.OrdinalIgnoreCase)
            || UniqueID.StartsWith(ModService.AssetPackUidPrefix, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// N 网更新「装得上、装完不粘」问题的判定与落盘。
/// 权威口径 = 最新 MAIN 文件的 FileId/Version，不是包内 manifest.Version。
/// </summary>
public static class NexusUpdateTruth
{
    public static string StampKey(ModEntry m) =>
        string.IsNullOrEmpty(m.UniqueID) ? m.Folder : m.UniqueID;

    public static NexusInstallRecord? GetInstallRecord(JuniGridConfig cfg, ModEntry m)
    {
        var key = StampKey(m);
        if (cfg.ModNexusInstalls.TryGetValue(key, out var r) && r is not null) return r;
        if (cfg.ModNexusInstalls.TryGetValue(m.Folder, out var r2) && r2 is not null) return r2;
        return null;
    }

    /// <summary>安装成功后：记住 N 网 fileId/文件版本；若与 manifest 不一致则回写 Version。
    /// <paramref name="downloadedModId"/> 必须是<b>真下载的那一页</b>：非官方续作的 manifest 里
    /// UpdateKeys 仍写着原版页（实测 2026-10-01 从续作页 20702 装的 Friends Forever，
    /// manifest 里写的是 Nexus:1738），拿 <c>m.NexusModId</c> 存就是把来源身份丢掉 ——
    /// 而「替代版是不是你手上这一版」正是靠这条记录来证明的。</summary>
    public static void RecordInstall(JuniGridConfig cfg, ModEntry m, int downloadedModId,
        long fileId, string remoteVersion, string gamePath)
    {
        if (string.IsNullOrWhiteSpace(remoteVersion) && fileId == 0) return;
        var rec = new NexusInstallRecord
        {
            NexusModId = downloadedModId > 0 ? downloadedModId : (m.NexusModId ?? 0),
            FileId = fileId,
            RemoteVersion = remoteVersion ?? "",
            InstalledAtUtc = DateTime.UtcNow,
        };
        var key = StampKey(m);
        cfg.ModNexusInstalls[key] = rec;
        if (!string.IsNullOrEmpty(m.Folder) && !string.Equals(m.Folder, key, StringComparison.OrdinalIgnoreCase))
            cfg.ModNexusInstalls[m.Folder] = rec;
        if (!string.IsNullOrWhiteSpace(remoteVersion))
            TrySetManifestVersion(Path.Combine(gamePath, "Mods", m.Folder), remoteVersion);
    }

    /// <summary>装完后从扫描结果里认出「刚装的那一条」。文件夹名对不上时，再按 manifest 的显示名
    /// 和路径末段各认一次 —— 传进来的猜测名是 manifest 里的 <c>Name</c>，改名安装与捆绑包子包的场合
    /// 它就不等于文件夹名；认不出来的后果是以一个永远查不中的键写进 ModNexusInstalls（记录等于白写，
    /// manifest 版本回写也落空）。三条判据按可信度排：N 网 id &gt; 文件夹全名 &gt; 显示名 / 末段。</summary>
    public static ModEntry? FindInstalledEntry(IEnumerable<ModEntry> scanned, int nexusModId, string? nameGuess)
    {
        foreach (var x in scanned)
            if (x.NexusModId == nexusModId) return x;
        var guess = (nameGuess ?? "").Trim();
        if (guess.Length == 0) return null;
        ModEntry? byFolder = null, byName = null, byLeaf = null;
        foreach (var x in scanned)
        {
            if (byFolder is null && string.Equals(x.Folder, guess, StringComparison.OrdinalIgnoreCase)) byFolder = x;
            else if (byName is null && string.Equals(x.Name, guess, StringComparison.OrdinalIgnoreCase)) byName = x;
            else if (byLeaf is null && string.Equals(LeafOf(x.Folder), guess, StringComparison.OrdinalIgnoreCase)) byLeaf = x;
            if (byFolder is not null) break;
        }
        return byFolder ?? byName ?? byLeaf;
    }

    /// <summary>Mods 里的路径末段：捆绑包子包记作 "Top/Sub"，而它的显示名常常只等于 "Sub"。</summary>
    private static string LeafOf(string folder) =>
        folder.Replace('\\', '/').TrimEnd('/').Split('/')[^1];

    public static void RecordInstallByModId(JuniGridConfig cfg, string gamePath, string folderOrUid,
        int nexusModId, long fileId, string remoteVersion)
    {
        var rec = new NexusInstallRecord
        {
            NexusModId = nexusModId,
            FileId = fileId,
            RemoteVersion = remoteVersion ?? "",
            InstalledAtUtc = DateTime.UtcNow,
        };
        cfg.ModNexusInstalls[folderOrUid] = rec;
        if (!string.IsNullOrWhiteSpace(remoteVersion))
            TrySetManifestVersion(Path.Combine(gamePath, "Mods", folderOrUid), remoteVersion);
    }

    /// <summary>只改 manifest 里的 Version 字段（保留 // 注释与其它键），避免作者包被整文件重写。</summary>
    public static bool TrySetManifestVersion(string modDir, string newVersion)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(newVersion)) return false;
            var path = Path.Combine(modDir, "manifest.json");
            if (!File.Exists(path)) return false;
            var text = File.ReadAllText(path);
            var pattern = new System.Text.RegularExpressions.Regex(
                "(\"Version\"\\s*:\\s*\")([^\"]+)(\")",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var m = pattern.Match(text);
            if (!m.Success) return false;
            var old = m.Groups[2].Value;
            if (string.Equals(old, newVersion, StringComparison.OrdinalIgnoreCase)) return false;
            var replaced = pattern.Replace(text, "${1}" + newVersion + "${3}", 1);
            var tmp = path + ".junigrid.tmp";
            File.WriteAllText(tmp, replaced);
            File.Move(tmp, path, true);
            AppLog.Warn("Mods", $"[更新] manifest.Version {old} → {newVersion}（{modDir}）");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Mods", "回写 manifest Version 失败: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 列表/详情展示用的本地有效版本：若启动器装过 N 网文件且其版本比 manifest 新，
    /// 以安装记录为准 —— 用户看到的就是「装到的那一版」，不会一直显示 0.0.1。
    /// </summary>
    public static string EffectiveLocalVersion(JuniGridConfig cfg, ModEntry m)
    {
        var rec = GetInstallRecord(cfg, m);
        if (rec is not null && !string.IsNullOrWhiteSpace(rec.RemoteVersion))
        {
            if (string.IsNullOrWhiteSpace(m.Version)) return rec.RemoteVersion;
            if (VersionUtil.IsNewer(rec.RemoteVersion, m.Version)) return rec.RemoteVersion;
        }
        return m.Version;
    }

    /// <summary>本地是否已装上「当前这条 N 网 MAIN 文件」（fileId 或文件版本一致）。</summary>
    public static bool AlreadyHasRemoteFile(JuniGridConfig cfg, ModEntry m, string? remoteVersion, long? remoteFileId)
    {
        var rec = GetInstallRecord(cfg, m);
        if (rec is not null)
        {
            if (remoteFileId is long fid && fid != 0 && rec.FileId != 0 && rec.FileId == fid) return true;
            // 快道（smapi.io）只给版本号不给 fileId，这条是那种情况下唯一的判据 ——
            // 必须按语义比：N 网文件栏写 "1.4"、索引写 "1.4.0"，字面相等会把刚装的版认成旧版
            if (VersionUtil.Same(rec.RemoteVersion, remoteVersion)) return true;
        }
        // 启动器历史上下载过「这一条」MAIN 文件（ModFileLastDownload[fileId]），
        // 即使当时没写安装快照/没回写 manifest，也视为已拥有该文件
        if (remoteFileId is long dfid && dfid != 0
            && cfg.ModFileLastDownload.ContainsKey(dfid.ToString()))
            return true;
        // 捆绑包装一次往往只给某个子包写了记录（SevenDeadlySins 的 PFM/FTM 各算一条）。
        // 全局按 fileId 认领：任一子包装过这一版 MAIN 文件 ⇒ 整包都算已装。
        if (remoteFileId is long gfid && gfid != 0)
        {
            foreach (var r in cfg.ModNexusInstalls.Values)
                if (r is not null && r.FileId == gfid) return true;
        }
        // 历史修复：修复前 .nxm 一键安装根本不写下载/安装记录（InstallService 那条路径漏了），
        // 所以「早就装好最新」的 mod 会一直亮 ⇧。判据用下载产物本身：
        // 本地留着 nxm-<modId>-<当前远端fileId>.zip ⇒ 玩家下的就是这条文件。
        // 只匹配「当前远端 fileId」，所以下了旧包、下了没装都不算，不会把真更新吞掉。
        if (remoteFileId is long rf && rf != 0 && m.NexusModId is int mid && mid > 0)
        {
            var zip = Path.Combine(StoragePaths.DownloadsDir, $"nxm-{mid}-{rf}.zip");
            if (File.Exists(zip))
            {
                AppLog.Warn("Updates", $"按本地下载包认领为已装：{zip}（{m.Folder}）"
                    + " —— 若其实没装上，点一次「更新」即可纠正");
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 用「启动器曾下载过该 fileId」自动校准：补写安装快照 + 回写 manifest.Version，
    /// 让列表立刻与 N 网一致，无需用户再点一次更新（作者忘改 manifest 时的死循环）。
    /// 返回 true = 已是该文件/已校准成功。
    /// </summary>
    public static bool TryHealFromPriorDownload(JuniGridConfig cfg, ModEntry m,
        long fileId, string? remoteVersion, string gamePath)
    {
        if (m.IsConvertedPack) return false;
        if (AlreadyHasRemoteFile(cfg, m, remoteVersion, fileId))
        {
            // 有历史下载但缺快照、或快照当年没记版本号（.nxm 那条路曾经只写 fileId）→ 补齐，
            // 否则下次快道再拿"只给版本号不给 fileId"的结果来判，仍然认不出「这一版已装」
            var rec = GetInstallRecord(cfg, m);
            if (fileId != 0 && !string.IsNullOrWhiteSpace(remoteVersion)
                && (rec is null || string.IsNullOrWhiteSpace(rec.RemoteVersion)))
            {
                RecordInstall(cfg, m, m.NexusModId ?? 0, fileId, remoteVersion!, gamePath);
                if (!string.Equals(m.Version, remoteVersion, StringComparison.OrdinalIgnoreCase))
                    m.Version = remoteVersion!;
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// 是否应显示「可更新」：转换包永不比；已装上当前 N 网 MAIN 文件 → 否；
    /// 否则用有效本地版本与远端文件版本做语义化比较。
    /// </summary>
    public static bool ShouldShowUpdate(JuniGridConfig cfg, ModEntry m, string? remoteVersion, long? remoteFileId)
    {
        if (m.IsConvertedPack) return false;
        if (string.IsNullOrWhiteSpace(remoteVersion)) return false;
        if (AlreadyHasRemoteFile(cfg, m, remoteVersion, remoteFileId)) return false;
        var local = EffectiveLocalVersion(cfg, m);
        if (string.IsNullOrWhiteSpace(local)) return true;
        // 结构性误判护栏：远端版本号只有一段数字（作者把 N 网「文件版本」栏填成 1）、
        // 而本地是多段（0.0.1）—— 两边根本不是同一套编号体系，比大小没意义
        // （实测 Haley 动漫肖像包恒显示 0.0.1 ⇧ 1，怎么更新都消不掉）。
        // 反向（本地单段、远端多段）是正常情形，不拦。
        if (NumericSegments(remoteVersion) == 1 && NumericSegments(local) >= 2)
        {
            AppLog.Warn("Updates", $"跳过不可比较的版本号：本地 {local} vs 远端 {remoteVersion}"
                + $"（{m.Folder}）—— 远端只有一段数字，判定为不同编号体系");
            return false;
        }
        return VersionUtil.IsNewer(remoteVersion, local);
    }

    /// <summary>版本号里数字段的个数（剥掉 v 前缀与 -beta/+build 后缀后按点切）。</summary>
    private static int NumericSegments(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return 0;
        var v = version.Trim().TrimStart('v', 'V');
        var cut = v.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0) v = v[..cut];
        return v.Split('.', StringSplitOptions.RemoveEmptyEntries)
                .Count(seg => seg.Length > 0 && seg.All(char.IsDigit));
    }
}
