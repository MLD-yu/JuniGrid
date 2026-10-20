using System.IO;

namespace JuniGrid.Services;

/// <summary>回收站的一行。同名 mod 的多份历史快照归成一组，组内按时间倒序。</summary>
public sealed record TrashItem(
    string FolderName,      // 磁盘上的真实目录名（含时间戳），删除/还原都要用它
    string DisplayName,     // 去掉时间戳后的名字，还原回 Mods 时用它
    DateTime? DeletedAt,    // 从目录名解析出来的删除时间；解析不出是 null
    long Bytes,
    int FileCount,
    int? NexusModId,        // .junigrid.json 里记的，没有就是 null
    bool AlsoInMods,        // Mods 下此刻已有同名目录 —— 界面标出来，方便顺手清理
    string RelPath,         // 相对回收站根的路径，子包是 "Top/Sub" 这种层级
    string Author = "",     // 从回收站里那份 manifest.json 读的，读不到是空串
    string Version = "",    // 同上
    string Description = "")// 同上
{
    /// <summary>这条在 Mods 里已经有同名的一份了 —— 还原会撞车，界面按这个给提示。</summary>
    public bool Conflicts => AlsoInMods;
}

/// <summary>回收站：列目录、还原、永久删除。
/// <para>目录名是唯一的状态载体，历史上改过三次命名规则，这里全部要能解析：
/// <c>原名_yyyyMMdd_HHmmss</c>（v1.09）、<c>原名-yyyyMMdd_HHmmss</c>（更早）、
/// <c>原名_8位十六进制</c>（toTrash=false 那条路径）。解不出时间的就不显示时间，但不动名字。</para>
/// <para>还原不信任目录名 —— 名字里本来带下划线/连字符的 mod 反解会错，
/// 所以界面允许调用方改写 DisplayName 再还原。</para></summary>
public sealed class TrashService
{
    private const string TrashFolder = ".junigrid_trash";

    public static string TrashRoot(string gamePath) =>
        Path.Combine(gamePath, "Mods", TrashFolder);

    /// <summary>列出回收站。回收站不存在时返回空表，不抛。</summary>
    public static List<TrashItem> List(string gamePath)
    {
        var root = TrashRoot(gamePath);
        var modsDir = Path.Combine(gamePath, "Mods");
        var list = new List<TrashItem>();
        if (!Directory.Exists(root)) return list;

        // 只看顶层目录：子包（Top/Sub）整棵挂在 Top 下，按一个条目报，
        // 还原时把 Top 整个搬回去（与 v1.1.4 的"保留原始层级"对称）。
        foreach (var dir in Directory.GetDirectories(root))
        {
            var folder = Path.GetFileName(dir);
            if (folder.StartsWith('.')) continue;   // 回收站自己的隐藏标记之类
            var (name, at) = ParseFolderName(folder);
            var (bytes, files) = Measure(dir);
            var meta = ReadManifestMeta(dir);
            list.Add(new TrashItem(
                FolderName: folder,
                DisplayName: name,
                DeletedAt: at,
                Bytes: bytes,
                FileCount: files,
                NexusModId: ReadNexusId(dir),
                AlsoInMods: Directory.Exists(Path.Combine(modsDir, name)),
                RelPath: folder,
                Author: meta.Author,
                Version: meta.Version,
                Description: meta.Description));
        }
        return list;
    }

    /// <summary>把回收站里的一份还原回 Mods。
    /// <para><paramref name="targetName"/> 由调用方确认（界面可编辑）。</para>
    /// <para><paramref name="overwrite"/> = true 时，Mods 里同名的那份先挪进回收站再放回
    /// —— 两边都留底，不真删。</para></summary>
    public static string? Restore(string gamePath, TrashItem item, string? targetName, bool overwrite)
    {
        try
        {
            var root = TrashRoot(gamePath);
            var modsDir = Path.Combine(gamePath, "Mods");
            var src = Path.Combine(root, item.FolderName);
            if (!Directory.Exists(src)) return LocService.Tr("回收站里找不到这一条");

            var name = Sanitize(targetName ?? item.DisplayName);
            if (string.IsNullOrWhiteSpace(name)) return LocService.Tr("还原名不能为空");
            var dst = Path.Combine(modsDir, name);

            if (Directory.Exists(dst))
            {
                if (!overwrite) return LocService.Tr("Mods 里已经有同名文件夹");
                // 现有的先留底再换回来 —— 覆盖不丢东西
                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var aside = Path.Combine(root, name + "_" + stamp);
                for (var n = 2; Directory.Exists(aside); n++)
                    aside = Path.Combine(root, $"{name}_{stamp}_{n}");
                Directory.Move(dst, aside);
                AppLog.Info("Trash", $"[还原] Mods 里现有的 {name} 先移入回收站：{Path.GetFileName(aside)}");
            }

            Directory.Move(src, dst);
            AppLog.Info("Trash", $"[还原] {item.FolderName} → {name}");
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Trash", "还原失败: " + ex.Message);
            return LocService.Tf("还原失败：{0}", ex.Message);
        }
    }

    /// <summary>永久删除一条。不可逆，调用方必须先让用户二次确认。</summary>
    public static string? DeletePermanently(string gamePath, TrashItem item)
    {
        try
        {
            var src = Path.Combine(TrashRoot(gamePath), item.FolderName);
            if (!Directory.Exists(src)) return null;   // 已经不在了 = 目标达成
            Directory.Delete(src, recursive: true);
            AppLog.Info("Trash", $"[永久删除] {item.FolderName}（{item.Bytes / 1024 / 1024} MB）");
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Trash", "永久删除失败: " + ex.Message);
            return LocService.Tf("删除失败：{0}", ex.Message);
        }
    }

    /// <summary>批量永久删除。返回失败的条数。</summary>
    public static int DeletePermanently(string gamePath, IEnumerable<TrashItem> items)
    {
        var failed = 0;
        foreach (var it in items)
            if (DeletePermanently(gamePath, it) is not null) failed++;
        return failed;
    }

    // ── 名字解析 ────────────────────────────────────────────────

    /// <summary>从目录名反解「原名」和「删除时间」。三条历史规则都要认。</summary>
    internal static (string Name, DateTime? At) ParseFolderName(string folder)
    {
        // 规则一：原名_yyyyMMdd_HHmmss（可带 _N 序号）
        // 规则二：原名-yyyyMMdd_HHmmss（更早的连字符版）
        // 规则三：原名_8位十六进制（toTrash=false 那条路径，没有时间）
        var m = System.Text.RegularExpressions.Regex.Match(
            folder, @"^(?<name>.+?)[_-](?<stamp>\d{8}_\d{6})(?:_\d+)?$");
        if (m.Success)
        {
            var raw = m.Groups["stamp"].Value;
            return (m.Groups["name"].Value,
                DateTime.TryParseExact(raw, "yyyyMMdd_HHmmss",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var at) ? at : null);
        }

        var g = System.Text.RegularExpressions.Regex.Match(folder, @"^(?<name>.+?)_[0-9a-fA-F]{8}$");
        if (g.Success) return (g.Groups["name"].Value, null);

        return (folder, null);   // 认不出来就原样用
    }

    private static string Sanitize(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var cleaned = new string(s.Trim().Select(c => bad.Contains(c) ? '_' : c).ToArray());
        return cleaned.Trim('.', ' ');
    }

    private static (long Bytes, int Files) Measure(string dir)
    {
        long bytes = 0; var files = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { bytes += new FileInfo(f).Length; files++; } catch { }
            }
        }
        catch { }
        return (bytes, files);
    }

    /// <summary>读回收站里那份 manifest.json 的展示字段 —— 列表要显示作者/版本/简介，与 Mods 页一致。
    /// 读不到就留空，不抛（回收站里可能有半截目录）。</summary>
    private static (string Author, string Version, string Description) ReadManifestMeta(string dir)
    {
        try
        {
            var manifest = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifest))
            {
                // 捆绑包壳：往里找一层子包
                var sub = Directory.GetDirectories(dir).FirstOrDefault();
                if (sub is null) return ("", "", "");
                manifest = Path.Combine(sub, "manifest.json");
                if (!File.Exists(manifest)) return ("", "", "");
            }
            // SMAPI 方言：允许注释和尾逗号
            var opts = new System.Text.Json.JsonDocumentOptions
            {
                CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest), opts);
            string Get(string key) =>
                doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                    ? v.GetString() ?? "" : "";
            return (Get("Author"), Get("Version"), Get("Description"));
        }
        catch { return ("", "", ""); }
    }

    private static int? ReadNexusId(string dir)
    {
        try
        {
            var side = Path.Combine(dir, ".junigrid.json");
            if (!File.Exists(side)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(side));
            return doc.RootElement.TryGetProperty("nexusModId", out var v) && v.TryGetInt32(out var id) ? id : null;
        }
        catch { return null; }
    }
}
