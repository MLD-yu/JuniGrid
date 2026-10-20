using System.IO;

namespace JuniGrid.Services;

public sealed partial class ModService
{
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
