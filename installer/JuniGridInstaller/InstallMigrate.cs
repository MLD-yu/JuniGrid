using System.IO;

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
/// 怎么搬：同盘 <c>Directory.Move</c>（瞬间完成）；跨盘只能真复制 —— 复制完必须逐个比对
/// 文件数与总字节，对得上才删源，对不上就留着源并记一条失败，<b>绝不允许两头都没有</b>
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
        "self-update", "smapi-installer", "webview2", "nexus-browse",
    };

    /// <summary>缓存根下的散装文件（不是目录），同样要跟着搬。</summary>
    public static readonly string[] DataFiles = { "_smapi-install.dat" };

    public sealed record Result(List<string> Moved, List<string> Failed,
        long Bytes, bool ConfigPatched, string? OldDir, string? NewDir);

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
                    MergeCopy(src, dst, out var skipped);
                    // 校验按【源里的每一个文件都得在新目录里】来判，不能比两边总字节：
                    // 合并场景新目录里那份同名文件是"更新的、不许被盖的"，它完全可以比源文件大或小，
                    // 拿总字节比会把这种正常情况判成失败，于是旧目录永远删不掉。
                    var missing = new List<string>();
                    foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                    {
                        var rel = Path.GetRelativePath(src, f);
                        var to = Path.Combine(dst, rel);
                        if (!File.Exists(to)) { missing.Add(rel); continue; }
                        var keepExisting = skipped.Contains(rel);
                        if (!keepExisting && new FileInfo(to).Length != new FileInfo(f).Length)
                            missing.Add(rel + "（大小对不上）");
                        if (missing.Count >= 3) break;
                    }
                    if (missing.Count > 0)
                    {
                        failed.Add($"{name}：复制校验没过，缺/不符 {string.Join("、", missing)} —— 旧目录原样保留");
                        continue;
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
                if (File.Exists(dst)) continue;      // 新位置已有：不覆盖，旧的留在原地
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
    /// <paramref name="skipped"/> 回传被跳过的那些相对名（大写），校验时它们不参与"字节必须一致"。</summary>
    private static void MergeCopy(string src, string dst, out HashSet<string> skipped)
    {
        skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, f);
            var to = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (File.Exists(to))
            {
                skipped.Add(rel);
                continue;
            }
            File.Copy(f, to, false);
        }
        // 空目录也要过去，否则 depot-staging 里"占位但还没下东西"的版本目录会消失
        foreach (var d in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, d)));
    }

    private static long DirSize(string dir) => DirStats(dir).bytes;

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
