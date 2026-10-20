using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace JuniGrid.Services;

public sealed partial class UpdateService
{
    /// <summary>从解压目录里定位真正含 StardewModdingAPI.exe 的根（install.dat 解出来可能多包一层）。</summary>
    private static string? FindStardewApiRoot(string dir)
    {
        if (File.Exists(Path.Combine(dir, "StardewModdingAPI.exe"))) return dir;
        return Directory
            .EnumerateDirectories(dir, "*", SearchOption.AllDirectories)
            .FirstOrDefault(d => File.Exists(Path.Combine(d, "StardewModdingAPI.exe")));
    }
    /// <summary>
    /// 解压带自动重试：Defender 常在 clrjit.dll/coreclr.dll 写入瞬间加锁导致
    /// UnauthorizedAccessException / IOException，等一下再试即可。
    /// </summary>
    private static async Task ExtractWithRetryAsync(
        string zipPath, string destDir, IProgress<InstallProgress>? progress)
    {
        const int maxAttempts = 4;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                ZipFile.ExtractToDirectory(zipPath, destDir, overwriteFiles: true);
                return;
            }
            catch (InvalidDataException ex)
            {
                // v1.6.2：坏包（下载不完整 / 镜像回错误页 / RAR 冒充 zip）重试解压没有意义 ——
                // 删掉半截包让下次下载从头来，给人话提示
                try { File.Delete(zipPath); } catch { }
                throw new InvalidOperationException(
                    "压缩包损坏（下载不完整或镜像返回了错误页），已清理坏包 —— 请重试，会自动重新下载", ex);
            }
            catch (Exception ex) when (
                ex is UnauthorizedAccessException || ex is IOException)
            {
                if (attempt == maxAttempts)
                {
                    throw new InvalidOperationException(
                        $"解压被系统拦截（{ex.Message}）—— 常见于 Windows Defender 实时保护，"
                        + "可临时把 %LocalAppData%\\JuniGrid 加入排除项后重试。", ex);
                }
                progress?.Report(new InstallProgress(LocService.Tf("解压被拦截，正在重试（{0}/{1}）…", attempt, maxAttempts - 1)));
                await Task.Delay(500 * attempt);
            }
        }
    }

    /// <summary>流式下载到文件，避免大文件占用内存。
    /// v1.07：断点续传/自动重试统一走 ResumableDownload（掉连接不再从 0 重下）。
    /// v1.08：GitHub 资源自动附带镜像候选 —— 直连 0 字节失败立刻切换镜像。</summary>
    private static Task DownloadToFileAsync(
        string url, string dest, IProgress<InstallProgress>? progress, CancellationToken ct = default)
    {
        var fallback = url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)
            ? GithubUrls(url).Skip(1)
            : null;
        return ResumableDownload.RunAsync(DownloadHttp, url, dest,
            (msg, pct, spd) => progress?.Report(new InstallProgress(msg, pct, spd)),
            fallbackUrls: fallback, ct: ct);
    }

    // ------------------------------------------------------------------
    // v1.08：国内加速 —— GitHub 直连失败（443 连接被拒）自动切换镜像前缀
    // 2026-09 实测：ghfast.top / gh-proxy.com / ghproxy.net 均可代理
    // releases/download；api.github.com 仅 gh-proxy.com 支持。
    // ------------------------------------------------------------------
    internal static readonly string[] GithubMirrorPrefixes =
        { "https://ghfast.top/", "https://gh-proxy.com/", "https://ghproxy.net/" };

    /// <summary>依次给出：原始 URL → 各镜像前缀 URL。逐个尝试直到成功。</summary>
    public static IEnumerable<string> GithubUrls(string url)
    {
        yield return url;
        foreach (var p in GithubMirrorPrefixes)
            yield return p + url;
    }

    // v1.1.6：此处的 FormatBytes 私有副本已删（无任何调用者）—— 统一用 ResumableDownload.FormatBytes。

    /// <summary>
    /// Copy SMAPI's unzipped install.dat payload into the game folder.
    /// Mirrors the official manual install steps while preserving existing Mods/,
    /// save-backups/, Content/, and other non-SMAPI game files.
    /// </summary>
    private static void CopySmapiBundle(string source, string gamePath)
    {
        Directory.CreateDirectory(gamePath);

        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var name = Path.GetFileName(entry);
            // Mods 不能整个覆盖，SMAPI 自带的 ConsoleMessages/SaveBackup 单独合并。
            if (string.Equals(name, "Mods", StringComparison.OrdinalIgnoreCase))
                continue;
            var dest = Path.Combine(gamePath, name);

            if (Directory.Exists(entry))
            {
                // SMAPI 安装包里的 smapi-internal/ 是需要完整覆盖的；
                // 若已存在同名目录，先删旧再复制。
                if (Directory.Exists(dest))
                    Directory.Delete(dest, recursive: true);
                ModService.CopyDirectoryContents(entry, dest);
            }
            else
            {
                File.Copy(entry, dest, overwrite: true);
            }
        }
    }

    /// <summary>
    /// 把 SMAPI 安装包里自带的默认 mod（ConsoleMessages / SaveCopier 等）合并进
    /// 玩家现有的 Mods 目录，不删除、不覆盖玩家已有目录里没有的东西。
    /// </summary>
    private static void CopyBuiltinMods(string sourceMods, string destMods)
    {
        if (!Directory.Exists(sourceMods)) return;
        Directory.CreateDirectory(destMods);

        foreach (var entry in Directory.EnumerateDirectories(sourceMods))
        {
            var modName = Path.GetFileName(entry);
            var destMod = Path.Combine(destMods, modName);
            if (Directory.Exists(destMod))
                continue;   // 玩家已有同名 mod 时保留玩家版本
            ModService.CopyDirectoryContents(entry, destMod);
        }
    }

    /// <summary>
    /// 安装前把玩家 Mods 目录备份到统一缓存目录的 mods-backup（跟随缓存盘，实测 C 盘满会把安装搞失败）。
    /// 只复制新增/变更文件，绝不删玩家原始 Mods；回收站文件夹（junigrid_trash/.junigrid_trash，可达数 GB）
    /// 不备份 —— 它留在原位不受安装影响。
    /// </summary>
    private static string? BackupMods(string gamePath)
    {
        var src = Path.Combine(gamePath, "Mods");
        if (!Directory.Exists(src)) return null;

        // v1.6.11：快照按【游戏内部版本号】分桶 —— 原先全挤在 smapi\ 一个桶里，
        // 而下面的清扫是无差别删 Mods-* 前缀：在 1.4 上安装失败留下的快照，会被
        // 切到 1.6.15 后的下一次安装直接删掉（用户视角＝"我的 mod 备份自己没了"）。
        var verBucket = ReadLocalGameVersion(gamePath);
        var backupRoot = Path.Combine(StoragePaths.ModsBackupDir, "smapi",
            string.IsNullOrWhiteSpace(verBucket) ? "unknown" : verBucket.Replace(',', '_').Trim());
        Directory.CreateDirectory(backupRoot);

        // 拍新快照前清掉【同一版本桶里】的旧快照 —— 该版本下永远最多一份。
        // 正常情况安装成功后 TryDeleteBackup 会删掉自己的快照；这里兜的是
        // 失败/中断留下的那份（重试时新快照覆盖同样的状态，旧的没有保留价值）。
        try
        {
            foreach (var old in Directory.GetDirectories(backupRoot))
            {
                if (!Path.GetFileName(old).StartsWith("Mods-", StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(old, true); } catch { }
            }
            // 旧布局（v1.6.11 之前不分版本，快照平铺在 smapi\ 下）留下的那批也要扫得掉，
            // 否则改成按版本分桶后，它们变成谁都不会再碰的死数据。
            var legacyRoot = Path.Combine(StoragePaths.ModsBackupDir, "smapi");
            if (!string.Equals(Path.GetFullPath(legacyRoot), Path.GetFullPath(backupRoot), StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(legacyRoot))
                foreach (var old in Directory.GetDirectories(legacyRoot))
                {
                    if (!Path.GetFileName(old).StartsWith("Mods-", StringComparison.OrdinalIgnoreCase)) continue;
                    try { Directory.Delete(old, true); } catch { }
                }
        }
        catch { }

        // 每次安装前生成独立快照，不覆盖旧备份。
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var dest = Path.Combine(backupRoot, $"Mods-{stamp}");
        var i = 1;
        while (Directory.Exists(dest))
            dest = Path.Combine(backupRoot, $"Mods-{stamp}-{i++}");

        ModService.CopyDirectoryContents(src, dest, skipTrash: true);
        return dest;
    }

    /// <summary>
    /// 把更新前的备份合并回游戏 Mods：已存在的目录/文件保留当前版本，
    /// 缺的补回来。这个函数本身只复制，不删除任何用户文件。
    /// </summary>
    private static void RestoreMods(string backupPath, string destMods)
    {
        if (!Directory.Exists(backupPath)) return;
        ModService.CopyDirectoryContents(backupPath, destMods, skipExisting: true);
    }

    /// <summary>
    /// 删除这次更新产生的临时 Mods 备份目录。只删刚刚创建在
    /// LocalAppData/JuniGrid/mods-backup 下的快照，绝不碰游戏目录。
    /// </summary>
    private static void TryDeleteBackup(string backupPath)
    {
        // 保险：只允许删除 mods-backup 根下的快照。备份位置随统一缓存目录迁移过
        //（LocalAppData → CacheRoot\mods-backup），两个根都要认 —— 否则新路径的
        // 快照被这里的校验静默拦下，删除永远不生效（实测 700MB×N 只进不出）。
        if (string.IsNullOrWhiteSpace(backupPath)) return;
        var full = Path.GetFullPath(backupPath);
        var roots = new[]
        {
            StoragePaths.ModsBackupDir,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JuniGrid", "mods-backup"),
        };
        foreach (var root in roots)
        {
            try
            {
                var rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
                if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) continue;
                if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
                return;
            }
            catch { }
        }
    }

    public static void OpenUrl(string url)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            // 记一条"交出去了"：ShellExecute 成功 ≠ 用户真看到窗口（默认浏览器被提权拦下、
            // 或关联指向一个不该以管理员启动的程序时，这里成功而屏幕上什么都没发生 —— 2026-10-07
            // 查「Premium 出口不开网页」就是卡在没有这条证据上）。
            AppLog.Info("OpenUrl", "已交给系统：" + url + "（进程 " + (p is null ? "无" : p.Id.ToString()) + "）");
        }
        catch (Exception __ex) { AppLog.Warn("UpdateService", "OpenUrl 失败：" + __ex.Message); }
    }

    // ------------------------------------------------------------------
    // Mod 的 GitHub 更新源（免费、无需 key、可直接下载）
    // ------------------------------------------------------------------
    /// <summary>repo = "owner/name"。返回最新 release 的版本号 + zip 资产地址。
    /// v1.08：api.github.com 直连失败时走 gh-proxy.com 镜像（实测唯一代理 API 可用的镜像）。</summary>
    public async Task<GitHubModRelease?> CheckModGitHubAsync(string repo)
    {
        var api = $"https://api.github.com/repos/{repo}/releases/latest";
        foreach (var url in GithubUrls(api))
        {
            try
            {
                var json = await Http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(tag)) return null;

                string? zip = null;
                if (root.TryGetProperty("assets", out var assets))
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            zip = a.TryGetProperty("browser_download_url", out var d)
                                ? d.GetString() : null;
                            break;
                        }
                    }
                }
                return new GitHubModRelease(tag, zip);
            }
            catch
            {
                continue;   // 换下一个通道（镜像/直连）再试
            }
        }
        return null;   // 全部通道失败 —— 交给 Nexus 源兜底
    }

    private static string Normalize(string? v) => (v ?? "").Trim().TrimStart('v', 'V');
}
