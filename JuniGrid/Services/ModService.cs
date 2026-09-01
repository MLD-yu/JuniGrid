using System.IO;
using System.IO.Compression;
using System.Text.Json;

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
    public IReadOnlyList<ModEntry> Scan(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return Array.Empty<ModEntry>();
        var modsDir = Path.Combine(gamePath, "Mods");
        if (!Directory.Exists(modsDir)) return Array.Empty<ModEntry>();

        // v0.52.0：清掉上次占用时残留的回收站（能删就删，删不掉就跳过——里面有被占用的文件）
        var trashDir = Path.Combine(modsDir, ".junigrid_trash");
        if (Directory.Exists(trashDir))
            try { Directory.Delete(trashDir, recursive: true); } catch { }

        // v0.72.6：先物化目录列表 —— 批量启禁时目录在惰性枚举途中被改名（X ↔ .X），
        // 枚举器会直接抛 DirectoryNotFoundException 炸穿整个 Rescan（2026-08-29 错误墙根因之一）
        List<string> dirs;
        try { dirs = Directory.EnumerateDirectories(modsDir).ToList(); }
        catch (DirectoryNotFoundException) { return Array.Empty<ModEntry>(); }

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

    // ------------------------------------------------------------------
    // Enable / disable / uninstall
    // ------------------------------------------------------------------
    /// <summary>Disabling = prefixing the folder with a dot (SMAPI skips those).
    /// 只对"顶层文件夹"改名：若传入的是多级子路径（如 Weather-Beta/[CC] 这种 Content Pack 分包），
    /// 永远只改第一段（整个顶层 mod）。这样禁用一个多分包时是整体改名，不会劈目录、不会残留空壳。</summary>
    public string? SetDisabled(string gamePath, string folderName, bool disabled)
    {
        try
        {
            var modsDir = Path.Combine(gamePath, "Mods");
            // 只取最顶层：多级子路径（"顶层/子包"）统一落到顶层文件夹，整体重命名
            var topLevel = folderName.Split('/')[0];
            var src = Path.Combine(modsDir, topLevel);
            if (!Directory.Exists(src) && !disabled && !topLevel.StartsWith('.'))
            {
                // 启用容错：调用方传的是不带点的旧名，但磁盘上实际是 .X
                var alt = Path.Combine(modsDir, "." + topLevel);
                if (Directory.Exists(alt)) { topLevel = "." + topLevel; src = alt; }
            }
            if (!Directory.Exists(src)) return "找不到 Mod 文件夹";

            var targetName = disabled
                ? (topLevel.StartsWith('.') ? topLevel : "." + topLevel)
                : topLevel.TrimStart('.');

            if (targetName == topLevel) return null;

            var dest = Path.Combine(modsDir, targetName);
            if (Directory.Exists(dest)) return "已存在同名文件夹，无法重命名";
            // v0.44.0：src 和 dest 都在同一个 Mods 目录下，Directory.Move 是纯元数据
            // 重命名（瞬时，不复制内容）。原 MoveDirectorySafe 遇占用会走"复制+删源"，
            // 大 mod 要搬几百 MB → 批量启禁巨慢的根因。改用瞬时改名，占用时让 Windows
            // 直接报错，由调用方提示。
            Directory.Move(src, dest);
            return null;
        }
        catch (Exception ex)
        {
            // v0.51.0：文件被占用时给中文提示，不再显示英文异常原文
            if (ex is IOException or UnauthorizedAccessException)
                return $"「{folderName}」正被占用，请先退出相关程序再{(disabled ? "禁用" : "启用")}";
            return ex.Message;
        }
    }

    public string? Uninstall(string gamePath, string folderName)
    {
        try
        {
            var dir = Path.Combine(gamePath, "Mods", folderName);
            if (!Directory.Exists(dir)) return "找不到 Mod 文件夹";
            // v0.51.0：原子化删除——先整个移到回收站验证"能不能删"，
            // 能移成功才从回收站彻底删；移不动（占用）就还原，绝不留半个文件夹
            var trash = Path.Combine(gamePath, "Mods", ".junigrid_trash");
            Directory.CreateDirectory(trash);
            var staging = Path.Combine(trash, folderName.Replace('/', '_') + "_" + Guid.NewGuid().ToString("N")[..8]);
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
            // 移成功 → 从回收站彻底删
            try { Directory.Delete(staging, recursive: true); }
            catch (Exception ex) { AppLog.Warn("ModService", "回收站清理失败: " + ex.Message); }
            // v0.52.0：删完立刻把回收站空目录也删掉，避免列表里多出 .junigrid_trash 空壳
            try { if (Directory.Exists(trash) && !Directory.EnumerateFileSystemEntries(trash).Any()) Directory.Delete(trash); }
            catch { }
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
            var modRoot = Path.GetDirectoryName(manifest)!;

            // 安全锁：多 Mod 共用一个 GitHub 仓库时，latest release 可能是别的 Mod。
            // 校验 UniqueID 不符就放弃，绝不能覆盖错装。
            if (expectedUniqueId is not null)
            {
                try
                {
                    using var check = JsonDocument.Parse(File.ReadAllText(manifest));
                    var uid = check.RootElement.TryGetProperty("UniqueID", out var u)
                        ? u.GetString() : null;
                    if (!string.Equals(uid, expectedUniqueId, StringComparison.OrdinalIgnoreCase))
                        return "下载的包不是这个 Mod（发布仓库里含多个 Mod），已放弃安装防止装错";
                }
                catch { return "无法校验更新包，已放弃安装"; }
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                if (doc.RootElement.TryGetProperty("Version", out var v))
                    newVersion = v.GetString();
            }
            catch (Exception __ex) { AppLog.Warn("ModService", __ex.Message); }

            var dest = Path.Combine(gamePath, "Mods", targetFolderName);
            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
            MoveDirectorySafe(modRoot, dest);   // 跨盘保护

            TryDelete(temp);
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
            var modRoot = Path.GetDirectoryName(manifest)!;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                if (doc.RootElement.TryGetProperty("Name", out var n))
                    modName = n.GetString();
            }
            catch (Exception __ex) { AppLog.Warn("ModService", __ex.Message); }

            var folderName = Path.GetFileName(modRoot);
            if (string.IsNullOrEmpty(folderName) || modRoot == temp)
                folderName = SanitizeFolderName(modName ?? "NewMod");

            var dest = Path.Combine(gamePath, "Mods", folderName);
            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
            // v0.71.9：同名【禁用】目录（.folderName）也要清掉 —— 旧逻辑只查不带点的 dest，
            // 禁用 mod（Mods/.X）+ 新下载（Mods/X）会同时存在，扫描出来就是同一个 mod 两行。
            var destDisabled = Path.Combine(gamePath, "Mods", "." + folderName);
            if (Directory.Exists(destDisabled)) Directory.Delete(destDisabled, recursive: true);
            // v0.71.9：再按 manifest UniqueID 兜底判重 —— 文件夹名不同但 UniqueID 相同
            // （如 ABC / ABC-1.2 / .ABC）也属于同一个 mod，一并清理，防任何形式的重复项。
            try
            {
                string? newUid = null;
                using (var doc = JsonDocument.Parse(File.ReadAllText(manifest)))
                    if (doc.RootElement.TryGetProperty("UniqueID", out var u)) newUid = u.GetString();
                if (!string.IsNullOrWhiteSpace(newUid))
                {
                    var modsDirScan = Path.Combine(gamePath, "Mods");
                    foreach (var dir2 in Directory.EnumerateDirectories(modsDirScan))
                    {
                        var name2 = Path.GetFileName(dir2);
                        if (string.Equals(name2, folderName, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(name2, "." + folderName, StringComparison.OrdinalIgnoreCase))
                            continue;   // 上面已处理
                        foreach (var mf in Directory.EnumerateFiles(dir2, "manifest.json", SearchOption.AllDirectories))
                        {
                            try
                            {
                                using var d2 = JsonDocument.Parse(File.ReadAllText(mf));
                                if (d2.RootElement.TryGetProperty("UniqueID", out var u2)
                                    && string.Equals(u2.GetString(), newUid, StringComparison.OrdinalIgnoreCase))
                                { Directory.Delete(dir2, recursive: true); break; }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch (Exception __ex) { AppLog.Warn("ModService", "UniqueID 判重清理失败: " + __ex.Message); }

            if (modRoot == temp)
                CopyDirectoryContents(temp, dest);   // files at zip root — copy into named folder
            else
                MoveDirectorySafe(modRoot, dest);   // 跨盘保护

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
            var text = File.ReadAllText(manifestPath);
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
                UniqueID = root["UniqueID"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)root["UniqueID"]! : "",
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
