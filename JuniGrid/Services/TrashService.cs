using System.IO;
using System.Collections.Concurrent;

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

/// <summary>永久删除回收站快照时的细粒度进度。</summary>
public sealed record TrashDeleteProgress(
    int ItemsDone, int ItemsTotal, string CurrentFolder, int FilesDone, int FilesTotal);

/// <summary>回收站：列目录、还原、永久删除。
/// <para>目录名是唯一的状态载体，历史上改过三次命名规则，这里全部要能解析：
/// <c>原名_yyyyMMdd_HHmmss</c>（v1.09）、<c>原名-yyyyMMdd_HHmmss</c>（更早）、
/// <c>原名_8位十六进制</c>（toTrash=false 那条路径）。解不出时间的就不显示时间，但不动名字。</para>
/// <para>还原不信任目录名 —— 名字里本来带下划线/连字符的 mod 反解会错，
/// 所以界面允许调用方改写 DisplayName 再还原。</para></summary>
public sealed class TrashService
{
    private const string TrashFolder = ".junigrid_trash";
    private sealed record ListCache(long Signature, List<TrashItem> Items);
    private static readonly ConcurrentDictionary<string, ListCache> ListCaches = new(StringComparer.OrdinalIgnoreCase);

    public static string TrashRoot(string gamePath) =>
        Path.Combine(gamePath, "Mods", TrashFolder);

    /// <summary>列出回收站。回收站不存在时返回空表，不抛。</summary>
    public static List<TrashItem> List(string gamePath, bool forceRefresh = false)
    {
        var root = TrashRoot(gamePath);
        var modsDir = Path.Combine(gamePath, "Mods");
        var key = Path.GetFullPath(root);
        var signature = DirectorySignature(root, modsDir);
        if (!forceRefresh && ListCaches.TryGetValue(key, out var cached) && cached.Signature == signature)
            return cached.Items.ToList();
        if (!Directory.Exists(root))
        {
            var empty = new List<TrashItem>();
            ListCaches[key] = new ListCache(signature, empty);
            return empty;
        }

        // 只看顶层目录：子包（Top/Sub）整棵挂在 Top 下，按一个条目报，
        // 还原时把 Top 整个搬回去（与 v1.1.4 的"保留原始层级"对称）。
        var dirs = Directory.GetDirectories(root);
        var items = new TrashItem?[dirs.Length];
        Parallel.For(0, dirs.Length,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 4) }, i =>
        {
            var dir = dirs[i];
            var folder = Path.GetFileName(dir);
            // 不要按 "." 前缀跳过：被禁用的 mod 原目录名就以 "." 开头，进回收站后仍是
            // ".原名_时间戳"。用户实测 .TP's Emily Portrait 因此在回收站里消失。
            var (name, at) = ParseFolderName(folder);
            var (bytes, files) = Measure(dir);
            var meta = ReadManifestMeta(dir);
            // 还原默认会去掉开头的点（启用被禁用的 mod），冲突判定也按还原目标名比。
            var restoreName = name.Trim('.', ' ');
            items[i] = new TrashItem(
                FolderName: folder,
                DisplayName: name,
                DeletedAt: at,
                Bytes: bytes,
                FileCount: files,
                NexusModId: ReadNexusId(dir),
                AlsoInMods: restoreName.Length > 0 && Directory.Exists(Path.Combine(modsDir, restoreName)),
                RelPath: folder,
                Author: meta.Author,
                Version: meta.Version,
                Description: meta.Description);
        });
        var list = items.Where(x => x is not null).Select(x => x!).ToList();
        // 目录树在递归统计期间若发生变化，不把这轮可能混合的新旧结果缓存起来。
        var after = DirectorySignature(root, modsDir);
        if (after == signature) ListCaches[key] = new ListCache(signature, list);
        return list;
    }

    /// <summary>清单在程序管理的还原/删除/移入操作后失效；外部改动由目录签名或手动刷新发现。</summary>
    public static void Invalidate(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return;
        try { ListCaches.TryRemove(Path.GetFullPath(TrashRoot(gamePath)), out _); } catch { }
    }

    private static long DirectorySignature(string root, string modsDir)
    {
        try
        {
            if (!Directory.Exists(root)) return 0;
            unchecked
            {
                long hash = 1469598103934665603;
                void Mix(long value) { hash ^= value; hash *= 1099511628211; }
                Mix(Directory.GetLastWriteTimeUtc(root).Ticks);
                foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    foreach (var ch in Path.GetFileName(dir)) Mix(ch);
                    Mix(Directory.GetLastWriteTimeUtc(dir).Ticks);
                }
                // The cached rows also contain same-name conflict badges, so Mods top-level
                // membership participates in the signature as well.
                if (Directory.Exists(modsDir))
                {
                    Mix(Directory.GetLastWriteTimeUtc(modsDir).Ticks);
                    foreach (var dir in Directory.EnumerateDirectories(modsDir)
                                 .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                        foreach (var ch in Path.GetFileName(dir)) Mix(ch);
                }
                return hash;
            }
        }
        catch { return DateTime.UtcNow.Ticks; }
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
            // 还原也算一次「新装」：文件夹 mtime 刷新到现在，列表的「最新在前」才会把它排到顶部。
            // 捆绑包的子包也可能各自排序，所以子目录一起刷新。
            try
            {
                var now = DateTime.Now;
                Directory.SetLastWriteTime(dst, now);
                foreach (var sub in Directory.EnumerateDirectories(dst, "*", SearchOption.AllDirectories))
                {
                    try { Directory.SetLastWriteTime(sub, now); } catch { }
                }
            }
            catch (Exception ex) { AppLog.Warn("Trash", "还原后刷新时间戳失败: " + ex.Message); }
            AppLog.Info("Trash", $"[还原] {item.FolderName} → {name}");
            Invalidate(gamePath);
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
            Invalidate(gamePath);
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Trash", "永久删除失败: " + ex.Message);
            return LocService.Tf("删除失败：{0}", ex.Message);
        }
    }

    /// <summary>批量永久删除。返回失败的条数。
    /// <para>v1.3.32：<paramref name="onProgress"/> 每删完一份回调 (已完成, 总数)。
    /// 一个几百 MB、十几万个小文件的包，<c>Directory.Delete(recursive)</c> 是分钟级的
    /// —— 期间界面不给任何反馈，用户只会看到「点了没反应」，然后继续点，
    /// 于是又开出第二条删除线程删同一批目录（实测卡死 3 分钟以上）。</para></summary>
    public static int DeletePermanently(string gamePath, IEnumerable<TrashItem> items,
        Action<TrashDeleteProgress>? onProgress = null)
    {
        var list = items as IReadOnlyList<TrashItem> ?? items.ToList();
        var failed = 0;
        var filesTotal = list.Sum(x => Math.Max(0, x.FileCount));
        var filesDone = 0;
        for (var i = 0; i < list.Count; i++)
        {
            var item = list[i];
            try { onProgress?.Invoke(new(i, list.Count, item.DisplayName, filesDone, filesTotal)); } catch { }
            var lastReport = Environment.TickCount64;
            var pendingFileReports = 0;
            var error = DeleteTreeWithProgress(
                Path.Combine(TrashRoot(gamePath), item.FolderName),
                () =>
                {
                    filesDone++;
                    pendingFileReports++;
                    var now = Environment.TickCount64;
                    if (pendingFileReports < 64 && now - lastReport < 120) return;
                    pendingFileReports = 0;
                    lastReport = now;
                    try { onProgress?.Invoke(new(i, list.Count, item.DisplayName, filesDone, Math.Max(filesTotal, filesDone))); } catch { }
                });
            if (error is not null) failed++;
            else Invalidate(gamePath);
            filesTotal = Math.Max(filesTotal, filesDone);
            try { onProgress?.Invoke(new(i + 1, list.Count, item.DisplayName, filesDone, filesTotal)); } catch { }
        }
        return failed;
    }

    /// <summary>
    /// 删除时逐文件推进进度，避免几分钟都停留在“0/N”。目录重解析点作为链接删除，
    /// 不跟随进入 Mods 之外的目标。
    /// </summary>
    private static string? DeleteTreeWithProgress(string root, Action fileDeleted)
    {
        if (!Directory.Exists(root)) return null;
        try
        {
            var pending = new Stack<(string Path, bool Visited)>();
            pending.Push((root, false));
            while (pending.Count > 0)
            {
                var (dir, visited) = pending.Pop();
                if (visited)
                {
                    Directory.Delete(dir, recursive: false);
                    continue;
                }

                pending.Push((dir, true));
                foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
                {
                    var attrs = File.GetAttributes(entry);
                    if ((attrs & FileAttributes.Directory) != 0)
                    {
                        if ((attrs & FileAttributes.ReparsePoint) != 0)
                        {
                            Directory.Delete(entry, recursive: false);
                            fileDeleted();
                        }
                        else pending.Push((entry, false));
                    }
                    else
                    {
                        File.Delete(entry);
                        fileDeleted();
                    }
                }
            }
            AppLog.Info("Trash", $"[永久删除] {Path.GetFileName(root)}");
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Trash", "永久删除失败: " + ex.Message);
            return LocService.Tf("删除失败：{0}", ex.Message);
        }
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
        // 开头的 "." 是禁用标记；还原进 Mods 时去掉它，用户才能在列表里看到并管理。
        // 结尾的 "." / 空格本来就非法，一并裁掉；全是点的名字直接拒绝。
        cleaned = cleaned.Trim('.', ' ');
        return cleaned is "" or "." or ".." ? "" : cleaned;
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
