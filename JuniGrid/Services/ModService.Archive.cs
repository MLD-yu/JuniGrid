using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SharpCompress.Archives;      // v1.6.3：rar/7z 托管解压（WriteToFile 扩展在此命名空间）
using SharpCompress.Common;

namespace JuniGrid.Services;

public sealed partial class ModService
{
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
                case "7z":
                    using (var a = SharpCompress.Archives.SevenZip.SevenZipArchive.OpenArchive(zipPath))
                        return a.Entries.Any(e => !e.IsDirectory && ManifestLeaf(e.Key));
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
}
