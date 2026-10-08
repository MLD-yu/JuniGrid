using System.IO;

namespace JuniGridInstaller;

/// <summary>
/// 「换目录安装 = 搬家」里负责<b>回收旧位置</b>的那一半。
/// 规矩只有一条：<b>只删我们自己写出去的文件</b>。装机目录是用户选的，他可能往里放过
/// 截图、配置备份、甚至像今天这样把缓存目录指过来（<c>E:\JuniGrid1</c> 里同时躺着
/// <c>portrait-covers\</c> 和 <c>webview2\</c>）—— 那种东西一旦被"清理旧版本"连锅端掉，
/// 用户不会认为这是他的错。所以：
/// ① 有清单按清单删；② 没清单（老版本装的）按【本次 payload 的顶层条目名】删；
/// ③ 删完目录空了才删目录本身，不空就原样留着并把剩余项报回去。
/// 主程序的卸载器（<c>JuniGrid/UninstallWindow.xaml.cs</c>）读同一份清单格式，两边格式注释互指。
/// </summary>
public static class InstallRecycle
{
    /// <summary>清单文件名，落在装机目录根。格式：首行 <c>JGMF1</c>，之后每行一个相对路径（用 / 分隔）。</summary>
    public const string ManifestFile = "junigrid-files.txt";
    private const string ManifestHeader = "JGMF1";

    public sealed record Result(string OldDir, int Deleted, List<string> Failed,
        List<string> Remaining, bool DirRemoved, bool HadManifest);

    /// <summary>把本次 payload 的文件表写成清单放进新装机目录。写失败不影响安装（只记日志）。
    /// 清单里要补上解包之后才造出来的文件，否则回收时会漏掉它们。</summary>
    public static void WriteManifest(string targetDir, IEnumerable<string> relPaths)
    {
        var lines = new List<string> { ManifestHeader };
        var all = relPaths.ToList();
        all.AddRange(CreatedAfterPayload);
        foreach (var r in all.Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(r => r, StringComparer.Ordinal))
            lines.Add(r.Replace('\\', '/'));
        var dst = Path.Combine(targetDir, ManifestFile);
        var tmp = dst + ".junigrid.tmp";
        File.WriteAllText(tmp, string.Join("\n", lines) + "\n");
        File.Move(tmp, dst, true);
    }

    /// <summary>读旧位置的清单；没有清单／格式不认识 → 返回空表（调用方转白名单模式）。</summary>
    public static List<string> ReadManifest(string oldDir)
    {
        var list = new List<string>();
        try
        {
            var p = Path.Combine(oldDir, ManifestFile);
            if (!File.Exists(p)) return list;
            var lines = File.ReadAllLines(p);
            if (lines.Length == 0 || lines[0].Trim() != ManifestHeader) return list;
            foreach (var raw in lines.Skip(1))
            {
                var rel = raw.Trim();
                if (rel.Length == 0) continue;
                // 清单本身是可信文件，但仍然不接受穿越——写错一次就可能删到盘上别处
                var full = SafeCombine(oldDir, rel);
                if (full is null) continue;
                list.Add(rel);
            }
        }
        catch { }
        return list;
    }

    private static string? SafeCombine(string root, string rel)
    {
        if (Path.IsPathRooted(rel)) return null;
        var segs = rel.Replace('\\', '/').Split('/');
        if (segs.Any(s => s == "..")) return null;
        try
        {
            var full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            var rootFull = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return null;
            return full;
        }
        catch { return null; }
    }

    /// <summary>
    /// 回收旧安装目录。<paramref name="fallbackTopLevel"/> 是本次 payload 的顶层条目名集合
    /// （旧位置没有清单时用它当"哪些文件是我们家的"依据）。
    /// <paramref name="dryRun"/> = 只算不删，给 <c>--recycle-preview</c> 用。
    /// </summary>
    public static Result RecycleOldInstall(string oldDir, IReadOnlyCollection<string> fallbackTopLevel, bool dryRun)
    {
        var failed = new List<string>();
        var deleted = 0;

        var driveRoot = Path.GetPathRoot(oldDir);
        if (string.IsNullOrWhiteSpace(oldDir) || !Directory.Exists(oldDir)
            || (driveRoot is not null && oldDir.TrimEnd('\\', '/').Equals(driveRoot.TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase)))
            return new Result(oldDir, 0, failed, new List<string>(), false, false);

        var manifest = ReadManifest(oldDir);
        var hadManifest = manifest.Count > 0;
        var targets = hadManifest
            ? manifest
            : TopLevelWhitelist(oldDir, fallbackTopLevel);

        foreach (var rel in targets)
        {
            var full = SafeCombine(oldDir, rel);
            if (full is null) continue;
            try
            {
                if (File.Exists(full))
                {
                    if (!dryRun) File.Delete(full);
                    deleted++;
                }
                else if (Directory.Exists(full))
                {
                    if (!dryRun) Directory.Delete(full, true);
                    deleted++;
                }
            }
            catch (Exception ex)
            {
                failed.Add(rel + "（" + ex.Message + "）");
            }
        }

        // 清单自己也要删掉，否则下次回收还会读到一份指向已消失文件的表
        if (!dryRun)
        {
            try
            {
                var mf = Path.Combine(oldDir, ManifestFile);
                if (File.Exists(mf)) File.Delete(mf);
            }
            catch (Exception ex) { failed.Add(ManifestFile + "（" + ex.Message + "）"); }
        }

        var remaining = new List<string>();
        bool dirRemoved = false;
        if (!dryRun)
        {
            TryPruneEmptyDirs(oldDir);
            remaining = EnumerateTopLevel(oldDir);
            if (remaining.Count == 0)
            {
                try { Directory.Delete(oldDir); dirRemoved = true; }
                catch (Exception ex) { failed.Add("目录本身（" + ex.Message + "）"); }
            }
        }
        else
        {
            // 预览模式：算一下"照这么删完，目录里还会剩什么"，不真动。
            // 清单文件本身真跑时会被删掉，所以不该出现在"剩余"里（否则预览骗人）。
            remaining = EnumerateTopLevel(oldDir)
                .Where(n => !targets.Any(t => t.Split('/')[0].Equals(n, StringComparison.OrdinalIgnoreCase))
                            && !n.Equals(ManifestFile, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return new Result(oldDir, deleted, failed, remaining, dirRemoved, hadManifest);
    }

    /// <summary>没清单时的兜底：只认「本次 payload 里也有的顶层条目名」
    /// <b>再加上解包之后我们自己造的那几个</b> —— <c>Uninstall.exe</c> 不在 payload 表里
    /// （它是解包完成后从 JuniGrid.exe 复制出来的，见 InstallerEngine.cs 的"配置卸载程序"一步），
    /// 漏了它就会在旧位置留下一个还能双击的隐形副本，正是这套回收要消灭的东西。</summary>
    private static readonly string[] CreatedAfterPayload = { "Uninstall.exe" };

    private static List<string> TopLevelWhitelist(string oldDir, IReadOnlyCollection<string> payloadRels)
    {
        var ours = new HashSet<string>(
            payloadRels.Select(r => r.Replace('\\', '/').Split('/')[0]), StringComparer.OrdinalIgnoreCase);
        ours.Add(ManifestFile);
        foreach (var n in CreatedAfterPayload) ours.Add(n);
        var hits = new List<string>();
        foreach (var name in EnumerateTopLevel(oldDir))
            if (ours.Contains(name)) hits.Add(name);
        return hits;
    }

    private static List<string> EnumerateTopLevel(string dir)
    {
        var list = new List<string>();
        try
        {
            foreach (var e in Directory.EnumerateFileSystemEntries(dir))
                list.Add(Path.GetFileName(e));
        }
        catch { }
        return list;
    }

    /// <summary>自底向上清空目录（只空删，非空不动）—— 我们写出去的文件删干净后，
    /// 留下的空壳不该继续占着用户的目录。</summary>
    private static void TryPruneEmptyDirs(string root)
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                         .OrderByDescending(x => x.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
                }
                catch { }
            }
        }
        catch { }
    }
}
