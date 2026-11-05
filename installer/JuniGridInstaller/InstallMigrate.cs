using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace JuniGridInstaller;

/// <summary>
/// 「换目录安装 = 搬家」里负责<b>迁移旧位置</b>的那一半（回收旧位置见 <see cref="InstallRecycle"/>）。
///
/// 用户换了个安装路径，他心里想的是「我那些东西跟着一起去 D 盘」—— 而不是「新版本装在 D 盘，
/// 旧目录里那 886 MB 游戏版本缓存和登录态留在原地，下次切版本重新下一遍」。
/// 所以顺序必须是：<b>先搬家 → 再解包/下载 → 最后清旧目录</b>：
/// 搬完了缓存就在新目录里，自更新和版本下载能接着用它，不会因为"数据不在"而重新拉一遍；
/// 反过来先删旧的，等于把要搬的东西删了。
///
/// 搬哪些：只搬<b>数据目录</b>。程序文件（wwwroot / i18n / runtimes / zh-Hans / bridge-mod / tools）
/// 由本次 payload 重新写出来，搬过去只会把旧版本的文件混进新安装里。
/// 怎么搬：同盘 <c>Directory.Move</c>（瞬间完成）；跨盘只能真复制 —— 复制完逐个确认源文件
/// 都在目标处且大小一致；目标处已有同名的以<b>新位置那份为准</b>（不比字节、不留副本）。
/// 对不上就留着源并记一条失败，<b>绝不允许两头都没有</b>
/// （那条跨盘 adopt 的老 bug 就是这么把旧版本体删掉、每次切版本重下 660 MB）。
/// 配置里的 <c>cacheRoot</c> 若指向旧安装目录（或压根没设过但旧目录里确实有数据），
/// 一并改指新目录 —— 不改的话应用回去用 %TEMP%，搬来的东西它看不见。
/// </summary>
public static class InstallMigrate
{
    /// <summary>应用配置的落点（与 <c>ConfigService.ConfigDir</c> 的默认分支一致；
    /// <c>JUNIGRID_CONFIG_DIR</c> 只有测试舱会设，装机时按默认位置读）。</summary>
    public static string DefaultConfigPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JuniGrid", "junigrid.config.json");

    /// <summary>搬家要搬的数据目录名（<c>StoragePaths</c> 里 InCache 的那几个）。
    /// ⚠ 这张表是<b>手抄</b>的：应用里新加一个 <c>InCache("新目录")</c> 而这里没跟上，
    /// 那个目录就会留在旧位置（2026-10-04 实测漏掉 <c>nexus-browse</c>）。
    /// 别再靠人记 —— 夹具 <c>installer/jg-installer-drift.py</c> 的 M10 会扫应用源码里
    /// 全部 <c>InCache("x")</c>，断言每个都列在这两张表里（当前 9 个全覆盖）。</summary>
    public static readonly string[] DataDirs =
    {
        "depot-staging", "downloads", "mods-backup", "portrait-covers",
        "self-update", "smapi-installer", "webview2", "nexus-browse", "nexus-web",
    };

    /// <summary>缓存根下的散装文件（不是目录），同样要跟着搬。</summary>
    public static readonly string[] DataFiles = { "_smapi-install.dat" };

    public sealed record Result(List<string> Moved, List<string> Failed,
        long Bytes, bool ConfigPatched, string? OldDir, string? NewDir);

    /// <summary>估算迁移阶段会额外占用的空间；同卷且目标目录不存在时走目录改名，不额外占空间。</summary>
    public static long EstimateAdditionalBytes(string? oldDir, string newDir)
    {
        if (string.IsNullOrWhiteSpace(oldDir) || !Directory.Exists(oldDir)) return 0;
        var oldFull = Norm(oldDir);
        var newFull = Norm(newDir);
        if (oldFull.Equals(newFull, StringComparison.OrdinalIgnoreCase)) return 0;

        var sameVolume = string.Equals(Path.GetPathRoot(oldFull), Path.GetPathRoot(newFull),
            StringComparison.OrdinalIgnoreCase);
        long total = 0;
        foreach (var name in DataDirs)
        {
            var src = Path.Combine(oldFull, name);
            if (!Directory.Exists(src)) continue;
            var dst = Path.Combine(newFull, name);
            // 同盘且目标不存在 ⇒ Directory.Move，一个字节都不额外占。
            if (sameVolume && !Directory.Exists(dst)) continue;
            // 合并/跨盘要真复制，按"整目录都要复制"估 ——
            // 宁可多算一点让检查提前拦下，也不能少算到搬一半才发现没空间。
            foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                total = checked(total + new FileInfo(file).Length);
        }
        foreach (var name in DataFiles)
        {
            var src = Path.Combine(oldFull, name);
            if (!File.Exists(src)) continue;
            var dst = Path.Combine(newFull, name);
            if (!File.Exists(dst) || new FileInfo(dst).Length != new FileInfo(src).Length)
                total = checked(total + new FileInfo(src).Length);
        }
        return total;
    }

    /// <summary>把旧安装目录里的数据搬到新目录。<paramref name="dryRun"/> = 只算不搬（给 --migrate-preview 用）。
    /// <paramref name="configPath"/> 是应用的配置文件（<c>%APPDATA%\JuniGrid\junigrid.config.json</c>）。</summary>
    public static Result Run(string? oldDir, string newDir, string configPath, bool dryRun,
        IProgress<InstallProgress>? progress = null)
    {
        var moved = new List<string>();
        var failed = new List<string>();
        long bytes = 0;

        if (string.IsNullOrWhiteSpace(oldDir) || !Directory.Exists(oldDir))
            return new Result(moved, failed, 0, false, oldDir, newDir);

        var oldFull = Norm(oldDir);
        var newFull = Norm(newDir);
        // 同一个地址：没搬家这回事（覆盖安装走 InstallRecycle 那条）
        if (oldFull.Equals(newFull, StringComparison.OrdinalIgnoreCase))
            return new Result(moved, failed, 0, false, oldFull, newFull);

        var sameVolume = string.Equals(Path.GetPathRoot(oldFull), Path.GetPathRoot(newFull),
            StringComparison.OrdinalIgnoreCase);

        foreach (var name in DataDirs)
        {
            var src = Path.Combine(oldFull, name);
            if (!Directory.Exists(src)) continue;
            var dst = Path.Combine(newFull, name);
            var size = DirSize(src);
            bytes += size;

            if (dryRun)
            {
                moved.Add(name + $"（{(sameVolume ? "移动" : "复制")} {size / (1024 * 1024)} MB）");
                continue;
            }

            try
            {
                progress?.Report(new InstallProgress(
                    $"正在搬家：{name}（{size / (1024 * 1024)} MB）…", 0.03));
                Directory.CreateDirectory(newFull);

                if (sameVolume && !Directory.Exists(dst))
                {
                    Directory.Move(src, dst);
                }
                else
                {
                    // 跨盘（或新目录里已经有同名的数据目录）：只能复制合并，验证过才删源
                    var free = FreeSpace(newFull);
                    if (free >= 0 && free < size + 64L * 1024 * 1024)
                    {
                        failed.Add($"{name}：目标盘只剩 {free / (1024 * 1024)} MB，不够搬 {size / (1024 * 1024)} MB —— "
                            + "旧目录里的东西一个没动");
                        continue;
                    }
                    MergeCopy(src, dst, out var skipped, out var conflicts);
                    // 校验按【源里的每一个文件都得在新目录里】来判，不能比两边总字节：
                    // 合并场景新目录里那份同名文件是"更新的、不许被盖的"，它完全可以比源文件大或小，
                    // 拿总字节比会把这种正常情况判成失败，于是旧目录永远删不掉。
                    // 内容不同的以新位置那份为准 —— 但旧的那份送回收站，不硬删。
                    var missing = new List<string>();
                    foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                    {
                        var rel = Path.GetRelativePath(src, f);
                        var to = Path.Combine(dst, rel);
                        if (!File.Exists(to)) { missing.Add(rel); continue; }
                        if (!skipped.Contains(rel) && new FileInfo(to).Length != new FileInfo(f).Length)
                            missing.Add(rel + "（大小对不上）");
                        if (missing.Count >= 3) break;
                    }
                    if (missing.Count > 0)
                    {
                        failed.Add($"{name}：复制校验没过，缺/不符 {string.Join("、", missing)} —— 旧目录原样保留");
                        continue;
                    }
                    if (skipped.Count > 0 || conflicts.Count > 0)
                    {
                        // 内容不同而"输了"的源文件送回收站，再删旧目录 —— 顺序不能反：
                        // 先删目录就把它们一起埋了。
                        // 文案必须按<b>真实结果</b>写：回收站不可用时 RecycleOrDelete 会退回硬删，
                        // 那时候再说"送回收站"就是骗人（用户会去回收站找，找不到）。
                        int recycled = 0, hardDeleted = 0;
                        foreach (var old in conflicts)
                        {
                            if (RecycleOrDelete(old)) recycled++;
                            else hardDeleted++;
                        }
                        if (hardDeleted > 0)
                            App.Log($"{name}：{hardDeleted} 个内容不同的旧文件没能进回收站，已直接删除"
                                    + $"（{recycled} 个已送回收站）："
                                    + string.Join("、", conflicts.Select(Path.GetFileName).Take(5)));
                        progress?.Report(new InstallProgress(
                            $"{name}：{skipped.Count} 个同名文件保留新位置那份"
                            + (conflicts.Count == 0 ? "" : hardDeleted > 0
                                ? $"（其中 {conflicts.Count} 个内容不同：{recycled} 个进回收站、{hardDeleted} 个直接删除）"
                                : $"（其中 {conflicts.Count} 个内容不同，旧的已送回收站）"),
                            0.035));
                    }
                    Directory.Delete(src, true);
                }
                moved.Add(name);
            }
            catch (Exception ex)
            {
                failed.Add($"{name}：{ex.Message}");
            }
        }

        // 缓存根下的散装文件（_smapi-install.dat 这类）：目录搬了、文件漏下就等于半搬。
        foreach (var name in DataFiles)
        {
            var src = Path.Combine(oldFull, name);
            if (!File.Exists(src)) continue;
            var dst = Path.Combine(newFull, name);
            bytes += new FileInfo(src).Length;
            if (dryRun) { moved.Add(name + "（文件）"); continue; }
            try
            {
                Directory.CreateDirectory(newFull);
                if (File.Exists(dst))
                {
                    // 与目录同一套规则（MSI 的组件一致性：同名即同物）：
                    // 目标那份更全/更新 ⇒ 以它为准，旧的送回收站，不再"留原地标失败"。
                    // 老做法会让 migrationFailed 为真 ⇒ 旧卸载器被跳过 ⇒ 旧目录永远清不掉，
                    // 一个文件卡住整棵树。
                    if (FilesEqual(src, dst))
                    {
                        File.Delete(src);
                        moved.Add(name);
                    }
                    else
                    {
                        var toBin = RecycleOrDelete(src);
                        moved.Add(name + (toBin
                            ? "（同名不同内容，旧的送回收站）"
                            : "（同名不同内容，旧文件已直接删除：回收站不可用）"));
                        if (!toBin) App.Log($"{name}：回收站不可用，旧文件已直接删除");
                    }
                    continue;
                }
                // 跨卷是"复制+删除"，不是改名 —— 中断会留半截。走原子写入。
                if (sameVolume) File.Move(src, dst);
                else { AtomicCopy(src, dst); File.Delete(src); }
                moved.Add(name);
            }
            catch (Exception ex) { failed.Add($"{name}：{ex.Message}"); }
        }

        var patched = !dryRun && moved.Count > 0
            && InstallConfig.PointCacheRootAt(configPath, oldFull, newFull);
        return new Result(moved, failed, bytes, patched, oldFull, newFull);
    }

    /// <summary>
    /// 逐文件复制合并：已存在的文件一律不覆盖（新位置那份更新或更完整都不该被旧的盖掉）。
    ///
    /// <para><b>写入是原子的</b>：先写 <c>.junigrid-part</c> 再改名。搬家中途断电/被杀时，
    /// 最终路径上要么是完整文件、要么压根没有 —— 绝不会留一个半截文件冒充"已迁移"
    /// （老版本直接 <c>File.Copy</c> 写最终路径，中断就留半截；下次搬家见它存在便跳过、
    /// 校验又不比内容，于是把<b>完整的源文件删掉、半截的留下</b>，方向正好反了）。</para>
    ///
    /// <para>目标已存在但其实是老版本留下的半截文件（内容是源文件的前缀）⇒ 覆盖成完整的。</para>
    ///
    /// <para>内容确实不同的，以<b>新位置那份为准</b>，旧的那份交给调用方送回收站 ——
    /// 不硬删：装机目录不该堆积 <c>.old-N</c>，但旧文件也不能就这么没了。</para>
    /// </summary>
    /// <param name="skipped">被跳过的相对名（新位置那份已存在且被保留）。</param>
    /// <param name="conflicts">内容不同、应当送回收站的<b>源文件完整路径</b>。</param>
    private static void MergeCopy(string src, string dst, out HashSet<string> skipped,
        out List<string> conflicts)
    {
        skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        conflicts = new List<string>();
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, f);
            var to = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (File.Exists(to))
            {
                if (IsTruncatedCopy(f, to))
                {
                    // 老版本搬家中断留下的半截文件：覆盖成完整的，别让它冒充"已迁移"
                    AtomicCopy(f, to);
                    continue;
                }
                skipped.Add(rel);
                if (!FilesEqual(f, to)) conflicts.Add(f);
                continue;
            }
            AtomicCopy(f, to);
        }
        // 空目录也要过去，否则 depot-staging 里"占位但还没下东西"的版本目录会消失
        foreach (var d in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, d)));
        // 上一轮搬家若中断，会留下 .junigrid-part 残片；它不是数据，收掉
        SweepPartFiles(dst);
    }

    /// <summary>复制到 <c>.junigrid-part</c> 再改名覆盖到最终路径 —— 中断只会留下残片，
    /// 不会让半截文件出现在最终路径上。</summary>
    private static void AtomicCopy(string source, string target)
    {
        var part = target + ".junigrid-part";
        try
        {
            File.Copy(source, part, true);
            File.Move(part, target, true);
        }
        catch
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { }
            throw;
        }
    }

    /// <summary>收掉搬家中断留下的 <c>.junigrid-part</c> 残片（只删残片，不碰别的）。</summary>
    private static void SweepPartFiles(string root)
    {
        try
        {
            foreach (var p in Directory.EnumerateFiles(root, "*.junigrid-part", SearchOption.AllDirectories))
                try { File.Delete(p); } catch { }
        }
        catch { }
    }

    /// <summary>目标那份是不是源文件的<b>半截</b>（老版本搬家中断留下的）。
    /// 判据：目标更短，且它的字节是源文件的前缀。内容不同的同名文件（另一个版本/另一个东西）
    /// 不满足前缀关系，不会被误判。</summary>
    private static bool IsTruncatedCopy(string source, string target)
    {
        try
        {
            var si = new FileInfo(source);
            var ti = new FileInfo(target);
            if (ti.Length == 0 || ti.Length >= si.Length) return false;
            const int chunk = 1 << 16;
            var a = new byte[chunk];
            var b = new byte[chunk];
            using var fs = File.OpenRead(source);
            using var ft = File.OpenRead(target);
            long left = ti.Length;
            while (left > 0)
            {
                int n = (int)Math.Min(chunk, left);
                if (fs.Read(a, 0, n) != n || ft.Read(b, 0, n) != n) return false;
                for (int i = 0; i < n; i++) if (a[i] != b[i]) return false;
                left -= n;
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>把文件送进 Windows 回收站（可还原），失败就退回硬删 —— 不能因为回收站
    /// 不可用就把搬家卡住。用 <c>SHFileOperation</c> + <c>FOF_ALLOWUNDO</c>，不依赖额外程序集。
    /// <para><b>返回值必须是"文件真进了回收站"这个事实，不能只看 SHFileOperation 的返回码</b>：
    /// 实测在 subst 出来的盘上它返回成功、文件却被永久删除（没有回收站的卷都会这样：
    /// subst / 网络盘 / 回收站被关掉）。所以改用回收站条目数前后对比来验结果 ——
    /// 数不出来（-1）时按"没进回收站"处理，宁可少说一句"可在回收站还原"，也不能骗人。</para></summary>
    /// <returns>true = 确实进了回收站；false = 已被永久删除（或压根删不掉）。</returns>
    internal static bool RecycleOrDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return true;   // 没有要删的东西，不算失败
            var before = RecycleBinItems(path);
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                // pFrom 必须双 \0 结尾；封送只补一个，这里自己再补一个
                pFrom = path + "\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT,
            };
            if (SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted)
            {
                var after = RecycleBinItems(path);
                return before >= 0 && after > before;
            }
        }
        catch { /* 回收站不可用 → 落到下面硬删 */ }
        try { File.Delete(path); } catch { }
        return false;
    }

    /// <summary>该文件所在卷的回收站条目数；查不到返回 -1。</summary>
    private static long RecycleBinItems(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return -1;
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            return SHQueryRecycleBin(root, ref info) == 0 ? info.i64NumItems : -1;
        }
        catch { return -1; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    private static long DirSize(string dir) => DirStats(dir).bytes;

    private static bool FilesEqual(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (leftInfo.Length != rightInfo.Length) return false;
        using var leftStream = File.OpenRead(left);
        using var rightStream = File.OpenRead(right);
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(leftStream), SHA256.HashData(rightStream));
    }

    private static (int files, long bytes) DirStats(string dir)
    {
        int n = 0; long b = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                n++;
                try { b += new FileInfo(f).Length; } catch { }
            }
        }
        catch { }
        return (n, b);
    }

    private static long FreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            return string.IsNullOrEmpty(root) ? -1 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch { return -1; }
    }

    private static string Norm(string p)
        => Path.GetFullPath(p.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
