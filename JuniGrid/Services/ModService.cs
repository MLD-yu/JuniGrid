using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace JuniGrid.Services;

/// <summary>
/// Scans the Mods/ folder, parses each mod's manifest.json (including
/// Nexus UpdateKeys so mods can be update-checked), and performs
/// install / update / enable / disable / uninstall operations.
/// </summary>
public sealed class ModService
{
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
        foreach (var dir in dirs)
        {
            // v0.72.6：单个目录在扫描瞬间被改名/删除属合法竞态 —— 局部容错跳过该项，
            // 绝不让它中断整次扫描；IO/权限异常单独记录但不改变原有扫描结果语义（不整吞）
            try
            {
                // v0.52.0：回收站目录不参与扫描
                if (string.Equals(Path.GetFileName(dir), ".junigrid_trash", StringComparison.OrdinalIgnoreCase))
                    continue;
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
        return results;
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
    public int CleanupDuplicateCopies(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return 0;
        // v1.1.5：顺带清扫孤儿 .tmp —— 原子写 manifest 的临时文件在进程被硬杀时
        // 会残留（finally 来不及执行）。正规 mod 不会携带 .tmp 文件，24 小时阈值
        // 兜底绝不误删"正在写一半"的临时文件。
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
                using var doc = System.Text.Json.JsonDocument.Parse(CleanManifestJson(text));
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
    /// Contains("/.")。独立 mod（无子路径）行为不变：整层改名，不会劈目录、不会残留空壳。</summary>
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
            if (!Directory.Exists(src)) return "找不到 Mod 文件夹";

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
    public string? Uninstall(string gamePath, string folderName, bool toTrash = false)
    {
        try
        {
            var dir = Path.Combine(gamePath, "Mods", folderName);
            if (!Directory.Exists(dir)) return "找不到 Mod 文件夹";
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

    /// <summary>游戏进程（SMAPI/游戏本体）是否存活 —— 与 LauncherService.IsGameRunning 同判据的静态版。</summary>
    private static bool IsGameProcessAlive =>
        System.Diagnostics.Process.GetProcessesByName("StardewModdingAPI").Length > 0 ||
        System.Diagnostics.Process.GetProcessesByName("Stardew Valley").Length > 0;

    // ------------------------------------------------------------------
    // v1.1.2：备注同步进 manifest.json —— 游戏内 GMCM 的 mod 标题读的就是
    // manifest 的 Name 字段，把它改成备注名，游戏里就显示中文名。
    // 原名先存进配置 ModOriginalNames，取消备注时还原。只动 Name，UniqueID 等
    // 其它字段一律不碰（更新检查、依赖关系全靠 UniqueID，不受影响）。
    // ------------------------------------------------------------------

    /// <summary>把备注写入（或从）指定 mod 的 manifest.json。remark 为空 = 还原原名。
    /// 返回 null 表示成功，否则为错误描述。</summary>
    public string? ApplyRemarkToManifest(string gamePath, string folder, string remark,
        Dictionary<string, string> originalNames)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gamePath) || string.IsNullOrWhiteSpace(folder))
                return "路径无效";
            var manifestPath = Path.Combine(gamePath, "Mods", folder.Replace('/', '\\'), "manifest.json");
            if (!File.Exists(manifestPath)) return "找不到 manifest.json";
            if (IsGameProcessAlive) return "游戏运行中，暂不能修改清单";

            // Newtonsoft 宽松解析（SMAPI manifest 允许尾随逗号/注释），再规整写回
            var text = ReadManifestText(manifestPath);
            var root = Newtonsoft.Json.Linq.JObject.Parse(CleanManifestJson(text));
            var currentName = root["Name"]?.Type == Newtonsoft.Json.Linq.JTokenType.String
                ? (string?)root["Name"] : null;
            if (string.IsNullOrEmpty(currentName)) return "清单缺少 Name 字段";

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
                    var root = Newtonsoft.Json.Linq.JObject.Parse(
                        CleanManifestJson(ReadManifestText(manifestPath)));
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
                    var err = ApplyRemarkToManifest(gamePath, folder, kv.Value, cfg.Current.ModOriginalNames);
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

    /// <summary>剥掉 SMAPI manifest 允许的尾随逗号与行内 // 注释，让 JObject.Parse 不炸。</summary>
    private static string CleanManifestJson(string raw)    {
        // 与 Scan 同一套宽松解析前置处理：去 // 注释与尾逗号的极简实现
        var sb = new System.Text.StringBuilder(raw.Length);
        var inStr = false;
        for (var i = 0; i < raw.Length; i++)
        {
            var ch = raw[i];
            if (inStr)
            {
                sb.Append(ch);
                if (ch == '\\' && i + 1 < raw.Length) { sb.Append(raw[++i]); continue; }
                if (ch == '"') inStr = false;
                continue;
            }
            if (ch == '"') { inStr = true; sb.Append(ch); continue; }
            if (ch == '/' && i + 1 < raw.Length && raw[i + 1] == '/')
            {
                while (i < raw.Length && raw[i] != '\n') i++;
                if (i < raw.Length) sb.Append('\n');
                continue;
            }
            if (ch == ',')
            {
                var j = i + 1;
                while (j < raw.Length && (raw[j] == ' ' || raw[j] == '\t' || raw[j] == '\r' || raw[j] == '\n')) j++;
                if (j < raw.Length && (raw[j] == '}' || raw[j] == ']')) continue;   // 尾逗号跳过
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }

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
            if (manifest is null) return "压缩包里没找到 manifest.json";
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
                        using var check = JsonDocument.Parse(ReadManifestText(mf));
                        var uid = check.RootElement.TryGetProperty("UniqueID", out var u)
                            ? u.GetString() : null;
                        if (string.Equals(uid, expectedUniqueId, StringComparison.OrdinalIgnoreCase))
                        { found = true; manifest = mf; break; }
                    }
                    catch { }
                }
                if (!found)
                    return "下载的包不是这个 Mod（发布仓库里含多个 Mod），已放弃安装防止装错";
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
    /// </summary>
    public string? InstallNew(string gamePath, string zipPath, out string? modName)
    {
        modName = null;
        string? temp = null;
        try
        {
            var manifest = ExtractToTemp(zipPath, "mod-install-", out temp);
            if (manifest is null)
            {
                // 没有 manifest.json 的不是独立 mod（多为汉化补丁/覆盖型文件包），
                // 自动装进去会以"孤儿文件夹"混进列表、且无法识别版本/依赖。
                // 改为提示手动下载，让用户自己决定怎么处理。
                TryDelete(temp);
                modName = null;
                return "这个压缩包没有 manifest.json，不是完整的独立 mod（可能是汉化补丁/覆盖包）。请改用 Manual download 手动下载并自行放置。";
            }
            var modRoot = ResolveModRoot(temp, out var allManifests);
            var isBundle = allManifests.Length > 1;

            // v1.1.5：与"没有 manifest 就拒绝"同一策略——manifest 是坏 JSON / 空文件 /
            // 根不是对象的也拒绝安装（否则会静默装上 SMAPI 无法加载的孤儿条目）。
            // CleanManifestJson 先行，SMAPI 风格的注释/尾逗号不受影响。
            foreach (var mf in allManifests)
            {
                try
                {
                    using var checkDoc = JsonDocument.Parse(CleanManifestJson(ReadManifestText(mf)));
                    if (checkDoc.RootElement.ValueKind != JsonValueKind.Object)
                        throw new JsonException("root is not an object");
                }
                catch (Exception __checkEx)
                {
                    AppLog.Warn("ModService", "manifest 校验失败: " + __checkEx.Message);
                    return "压缩包里的 manifest.json 已损坏或为空，不是完整的 mod，已放弃安装";
                }
            }

            try
            {
                using var doc = JsonDocument.Parse(ReadManifestText(manifest));
                if (doc.RootElement.TryGetProperty("Name", out var n))
                    modName = n.GetString();
            }
            catch (Exception __ex) { AppLog.Warn("ModService", __ex.Message); }
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

            var folderName = Path.GetFileName(modRoot);
            if (string.IsNullOrEmpty(folderName) || modRoot == temp)
                folderName = SanitizeFolderName(
                    (isBundle ? Path.GetFileNameWithoutExtension(zipPath) : null)
                    ?? modName ?? "NewMod");

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
                var newUids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var mf in isBundle ? allManifests : new[] { manifest })
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(ReadManifestText(mf));
                        if (doc.RootElement.TryGetProperty("UniqueID", out var u)
                            && !string.IsNullOrWhiteSpace(u.GetString()))
                            newUids.Add(u.GetString()!);
                    }
                    catch { }
                }
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
            return null;
        }
        catch (Exception ex)
        {
            if (temp is not null) TryDelete(temp);
            return ex.Message;
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------
    /// <summary>Extracts zip to a unique temp dir; returns the shallowest manifest.json path.</summary>
    private static string? ExtractToTemp(string zipPath, string prefix, out string tempDir)
    {
        tempDir = Path.Combine(StoragePaths.DownloadsDir, prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        ZipFile.ExtractToDirectory(zipPath, tempDir);
        return Directory
            .GetFiles(tempDir, "manifest.json", SearchOption.AllDirectories)
            .OrderBy(p => p.Length)
            .FirstOrDefault();
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

    /// <summary>剥掉子包名开头的标记前缀，如 "[BL] Downtown Zuzu" → "Downtown Zuzu"。
    /// SMAPI 社区惯用 "[XX] " 标注内容包类型（[CP]/[CC]/[DLL]…）。</summary>
    private static string StripPackTag(string name)
    {
        var n = name.TrimStart();
        while (n.StartsWith("[") )
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
    private static void CopyDirectoryContents(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.GetDirectories(src))
            CopyDirectoryContents(d, Path.Combine(dest, Path.GetFileName(d)));
    }

    /// <summary>
    /// v1.1.1：把 Mods 下已存在的旧版目录整体改名移入 .junigrid_trash（同盘瞬时原子改名，
    /// 失败即整体失败，绝不出现"删一半"）。命名沿用 Uninstall 的时间戳+序号兜底规则。
    /// 返回 staging 路径；被占用等改名失败直接抛异常，由调用方决定放弃或回滚。
    /// </summary>
    private static string StageExistingToTrash(string gamePath, string existingDir)
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

        public void Acquire(string trashDir)
        {
            if (_handle is { IsInvalid: false, IsClosed: false }) return;
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

            // Dependencies：只收"必需"依赖。SMAPI 的 IsRequired(旧名 Required) 默认 true，
            // 显式标 IsRequired=false 的是可选依赖（可缺但不应报缺失）→ 排除，避免凭空多报。
            var deps = new List<string>();
            if (root["Dependencies"] is Newtonsoft.Json.Linq.JArray depsArr)
            {
                foreach (var item in depsArr)
                {
                    if (item is not Newtonsoft.Json.Linq.JObject io
                        || io["UniqueID"]?.Type != Newtonsoft.Json.Linq.JTokenType.String) continue;
                    var s = (string)io["UniqueID"]!;
                    if (string.IsNullOrWhiteSpace(s)) continue;

                    // 显式标了 IsRequired/Required=false 的 → 可选依赖，不算必需
                    var required = true;
                    var reqToken = io["IsRequired"] ?? io["Required"];
                    if (reqToken?.Type == Newtonsoft.Json.Linq.JTokenType.Boolean)
                        required = (bool)reqToken!;
                    if (required) deps.Add(s);
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
    public List<string> ContentPackIds { get; set; } = new(); // 它作为内容包时依赖的宿主 mod UniqueID（合并进 Dependencies 用于缺失检测）
    public bool HasManifest { get; set; } = true;   // false = 该文件夹没有有效 manifest（用文件夹名兜底）
    /// <summary>v0.45.0：分类标签（仿 PCL2），在列表行简介前显示。代码 Mod / 内容包 / 代码·内容包；都不是则为空。</summary>
    public string Category { get; set; } = "";
}
