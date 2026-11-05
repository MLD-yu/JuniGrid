using System.IO;
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
/// 都在目标处且大小一致；同名冲突再比 SHA-256。对不上就留着源并记一条失败，<b>绝不允许两头都没有</b>
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
            // 合并/跨盘要真复制。同名冲突还会另存一份，所以这里按"整目录都要复制"估 ——
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
                    MergeCopy(src, dst, out var skipped, out var preserved);
                    // 冲突的文件已经在 MergeCopy 里另存成 xxx.old-N（内容一个字都没丢），
                    // 所以这里只确认"每个源文件要么在目标处内容一致，要么有一份另存"。
                    // 只要都成立就能安全删源 —— 旧目录不该因为一个同名文件永远赖着不走。
                    var missing = new List<string>();
                    foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                    {
                        var rel = Path.GetRelativePath(src, f);
                        var to = Path.Combine(dst, rel);
                        if (!File.Exists(to)) { missing.Add(rel); continue; }
                        if (skipped.Contains(rel))
                        {
                            if (FilesEqual(f, to) || preserved.ContainsKey(rel)) continue;
                            missing.Add(rel + "（目标已有不同内容，且另存失败）");
                        }
                        else if (new FileInfo(to).Length != new FileInfo(f).Length)
                        {
                            missing.Add(rel + "（大小对不上）");
                        }
                        if (missing.Count >= 3) break;
                    }
                    if (missing.Count > 0)
                    {
                        failed.Add($"{name}：复制校验没过，缺/不符 {string.Join("、", missing)} —— 旧目录原样保留");
                        continue;
                    }
                    if (preserved.Count > 0)
                        progress?.Report(new InstallProgress(
                            $"{name}：{preserved.Count} 个同名文件内容不同，旧的那份已另存为 .old-N", 0.035));
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
                    if (FilesEqual(src, dst))
                    {
                        File.Delete(src);
                        moved.Add(name);
                    }
                    else
                    {
                        failed.Add($"{name}：新位置已有不同内容 —— 旧文件保留在原位置");
                    }
                    continue;
                }
                File.Move(src, dst);
                moved.Add(name);
            }
            catch (Exception ex) { failed.Add($"{name}：{ex.Message}"); }
        }

        var patched = !dryRun && moved.Count > 0
            && InstallConfig.PointCacheRootAt(configPath, oldFull, newFull);
        return new Result(moved, failed, bytes, patched, oldFull, newFull);
    }

    /// <summary>逐文件复制合并：已存在的文件一律不覆盖（新位置那份更新或更完整都不该被旧的盖掉）。
    /// 内容不同的旧文件<b>另存</b>为 <c>同名.old-N</c> —— 既不丢数据，也不让旧目录永远删不掉。
    /// <paramref name="skipped"/> 回传被跳过的相对名；<paramref name="preserved"/> 回传
    /// 「源相对名 → 另存后的文件名」。</summary>
    private static void MergeCopy(string src, string dst, out HashSet<string> skipped,
        out Dictionary<string, string> preserved)
    {
        skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        preserved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, f);
            var to = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (File.Exists(to))
            {
                skipped.Add(rel);
                if (FilesEqual(f, to)) continue;      // 内容一致：就是同一份，源可以删
                var keep = NextPreservedPath(to);
                try
                {
                    File.Copy(f, keep, false);
                    preserved[rel] = Path.GetFileName(keep);
                }
                catch { /* 另存失败就记不到 preserved 里，校验会因此判该目录没搬成 */ }
                continue;
            }
            File.Copy(f, to, false);
        }
        // 空目录也要过去，否则 depot-staging 里"占位但还没下东西"的版本目录会消失
        foreach (var d in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, d)));
    }

    /// <summary>给另存文件找个没被占用的名字：<c>body.bin</c> → <c>body.bin.old-1</c>。
    /// 反复换目录安装时不会互相覆盖。</summary>
    private static string NextPreservedPath(string target)
    {
        for (var i = 1; i < 1000; i++)
        {
            var candidate = target + ".old-" + i;
            if (!File.Exists(candidate)) return candidate;
        }
        return target + ".old-" + Guid.NewGuid().ToString("N")[..8];
    }

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
