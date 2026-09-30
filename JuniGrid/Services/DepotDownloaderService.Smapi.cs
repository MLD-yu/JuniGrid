using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace JuniGrid.Services;

public sealed partial class DepotDownloaderService
{
    // ─── SMAPI 共享池：按「游戏版本 ↔ SMAPI 兼容」分桶，切版本只恢复对应桶 ───
    // modern     = 游戏 1.6+（SMAPI 4.x / latest）
    // legacy-3   = 游戏 1.4–1.5（SMAPI 3.x）
    // legacy-2   = 游戏 1.2.30+ / 1.3（SMAPI 2.x）
    // unsupported= 游戏 1.0/1.1、1.2.29-、1.3.26-：没有可靠一键 SMAPI

    private static string SmapiPoolRoot => Path.Combine(StagingRoot, "_smapi-pool");

    private static string SmapiPoolBucket(string? gameVersion)
    {
        var tag = UpdateService.RecommendSmapiTag(gameVersion);
        if (tag is null) return "unsupported";
        if (tag == UpdateService.SmapiLatest) return "modern"; // 1.6+
        return "legacy-" + tag.Split('.')[0];                  // 2.x / 3.x
    }

    private static bool HasSmapiInstalled(string root)
        => File.Exists(Path.Combine(root, "StardewModdingAPI.exe"));

    /// <summary>SMAPI 安装器自带的示例 mod（在 Mods/ 里，不跟 exe 走）—— 切版本后要补上。</summary>
    private static readonly string[] SmapiSampleMods = ["ConsoleCommands", "SaveBackup"];

    private static string SmapiSamplesRoot => Path.Combine(SmapiPoolRoot, "_samples");

    /// <summary>把当前 Mods 里的 SMAPI 示例 mod 收进共享种子（安装 SMAPI / 缓存时调用）。</summary>
    public static void SeedSmapiSampleMods(string gamePath)
    {
        var mods = Path.Combine(gamePath, "Mods");
        if (!Directory.Exists(mods)) return;
        foreach (var name in SmapiSampleMods)
        {
            var src = Path.Combine(mods, name);
            if (!Directory.Exists(src)) continue;
            var dest = Path.Combine(SmapiSamplesRoot, name);
            try
            {
                Directory.CreateDirectory(SmapiSamplesRoot);
                if (Directory.Exists(dest)) Directory.Delete(dest, true);
                if (!TryRobocopy(src, dest))
                    ParallelCopyDirectory(src, dest, skipTrash: false);
            }
            catch (Exception ex)
            {
                AppLog.Warn("DepotDownloader", $"缓存 SMAPI 示例 mod「{name}」失败: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 切完版本后：SMAPI 在、但 Mods 里缺示例 mod 时，从共享种子补上。
    /// 没有种子里就从各版本包 Mods 里认领一份（首次切换的过渡）。
    /// </summary>
    public static void EnsureSmapiSampleMods(string gamePath)
    {
        if (!HasSmapiInstalled(gamePath)) return;
        SeedSmapiSampleMods(gamePath);   // 已有就先入种
        var mods = Path.Combine(gamePath, "Mods");
        Directory.CreateDirectory(mods);
        foreach (var name in SmapiSampleMods)
        {
            var dst = Path.Combine(mods, name);
            if (Directory.Exists(dst) && Directory.EnumerateFileSystemEntries(dst).Any()) continue;
            var src = Path.Combine(SmapiSamplesRoot, name);
            if (!Directory.Exists(src) || !Directory.EnumerateFileSystemEntries(src).Any())
                src = FindSampleModInStaging(name) ?? "";
            if (src.Length == 0 || !Directory.Exists(src)) continue;
            try
            {
                if (Directory.Exists(dst)) Directory.Delete(dst, true);
                if (!TryRobocopy(src, dst))
                    ParallelCopyDirectory(src, dst, skipTrash: false);
                AppLog.Warn("DepotDownloader", $"已补上 SMAPI 自带示例 mod：{name}");
            }
            catch (Exception ex)
            {
                AppLog.Warn("DepotDownloader", $"补 SMAPI 示例 mod「{name}」失败: {ex.Message}");
            }
        }
    }

    private static string? FindSampleModInStaging(string name)
    {
        try
        {
            if (!Directory.Exists(StagingRoot)) return null;
            foreach (var dir in Directory.EnumerateDirectories(StagingRoot))
            {
                if (Path.GetFileName(dir).StartsWith("_", StringComparison.Ordinal)) continue;
                var m = Path.Combine(dir, "Mods", name);
                if (Directory.Exists(m) && Directory.EnumerateFileSystemEntries(m).Any()) return m;
            }
        }
        catch { }
        return null;
    }

    private static void CopySmapiFiles(string fromRoot, string toRoot)
    {
        Directory.CreateDirectory(toRoot);
        var files = new List<string>();
        try { files.AddRange(Directory.EnumerateFiles(fromRoot, "StardewModdingAPI*")); } catch { }
        foreach (var n in new[] { "Newtonsoft.Json.dll", "Mono.Cecil.dll", "steam_appid.txt" })
        {
            var src = Path.Combine(fromRoot, n);
            if (File.Exists(src)) files.Add(src);
        }
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = IoParallelism }, f =>
        {
            try { File.Copy(f, Path.Combine(toRoot, Path.GetFileName(f)), overwrite: true); } catch { }
        });
        var smapiInternal = Path.Combine(fromRoot, "smapi-internal");
        if (Directory.Exists(smapiInternal))
        {
            var destInternal = Path.Combine(toRoot, "smapi-internal");
            if (!TryRobocopy(smapiInternal, destInternal))
                ParallelCopyDirectory(smapiInternal, destInternal, skipTrash: false);
        }
    }

    /// <summary>
    /// SMAPI 快照是否完整可用：exe 在，依赖也得在 —— 2.x 的 Newtonsoft.Json.dll 在顶层，
    /// 3.x/4.x 收在 smapi-internal/。旧版收集只留 StardewModdingAPI*，恢复出的裸 exe
    /// 一启动就崩，用这条识别并淘汰残缺快照。
    /// </summary>
    private static bool IsCompleteSmapiSnapshot(string dir)
        => HasSmapiInstalled(dir)
           && (Directory.Exists(Path.Combine(dir, "smapi-internal"))
               || File.Exists(Path.Combine(dir, "Newtonsoft.Json.dll")));

    // 共享池桶里的标记文件：第一行 = 存入时该游戏版本对应的 SMAPI tag（"2.5"/"3.7.3"/"latest"）。
    // 桶按大版本共享（1.2.30 与 1.3.36 同桶），恢复时要求 tag 与目标版本该用的完全一致。
    private const string SmapiPoolMetaName = "_junigrid-smapi.meta";

    private static void WriteSmapiPoolMeta(string bucketDir, string? gameVersion)
    {
        try
        {
            var tag = UpdateService.RecommendSmapiTag(gameVersion) ?? "unknown";
            File.WriteAllText(Path.Combine(bucketDir, SmapiPoolMetaName), tag + "\n" + (gameVersion ?? ""));
        }
        catch { }
    }

    private static string? ReadSmapiPoolTag(string bucketDir)
    {
        try
        {
            var path = Path.Combine(bucketDir, SmapiPoolMetaName);
            if (!File.Exists(path)) return null;
            var first = File.ReadLines(path).FirstOrDefault()?.Trim();
            return string.IsNullOrWhiteSpace(first) ? null : first;
        }
        catch { return null; }
    }

    /// <summary>把游戏目录里的 SMAPI 产物存进共享池（按当前游戏版本的兼容桶）。
    /// 1.0/1.1 无适配 SMAPI，不入池（避免把 1.6 的 4.x 误存成「1.0 用的」）。</summary>
    public static bool SaveSmapiToPool(string gamePath, string? gameVersion)
    {
        // 残缺快照（如切版本中间态的裸 exe）不入池，避免污染桶。
        // 但 ② 随后会把整个本体删掉 —— 用户手装的那一份不能跟着蒸发，
        // 兜底留一份 as-found（不参与桶选择，只供找回）。
        if (!IsCompleteSmapiSnapshot(gamePath))
        {
            if (HasSmapiInstalled(gamePath)) ArchivePartialSmapi(gamePath, gameVersion);
            return false;
        }
        var bucket = SmapiPoolBucket(gameVersion);
        if (bucket == "unsupported") return false;
        var dest = Path.Combine(SmapiPoolRoot, bucket);
        var want = UpdateService.RecommendSmapiTag(gameVersion);
        var have = ReadSmapiPoolTag(dest);
        if (have is not null && IsCompleteSmapiSnapshot(dest)
            && !string.IsNullOrWhiteSpace(want)
            && string.Equals(have, want, StringComparison.OrdinalIgnoreCase))
        {
            // 桶 tag 对 modern 恒为 "latest"，同桶里池内那份完全可能是另一个（更旧的）小版本。
            // 无条件早退等于：② 先把用户手上这份删掉，再从池里铺旧的 —— 用户那份就此消失。
            // 所以只有「确实是同一份构建」才走快路径。
            if (SameSmapiBuild(gamePath, dest)) return true;
            AppLog.Warn("DepotDownloader",
                $"共享池 {bucket} 与游戏目录里的 SMAPI 不是同一份构建，改用游戏目录这份");
        }
        try { if (Directory.Exists(dest)) Directory.Delete(dest, true); } catch { }
        CopySmapiFiles(gamePath, dest);
        WriteSmapiPoolMeta(dest, gameVersion);
        AppLog.Warn("DepotDownloader", $"SMAPI 已写入共享池 {dest}");
        return true;
    }

    /// <summary>两份 SMAPI 是否同一构建：比 StardewModdingAPI.exe 的字节数与 FileVersion。
    /// 任一读不到都按「不同」处理（宁可多覆盖一次桶，也不能把用户那份弄丢）。</summary>
    private static bool SameSmapiBuild(string fromRoot, string toRoot)
    {
        try
        {
            var a = Path.Combine(fromRoot, "StardewModdingAPI.exe");
            var b = Path.Combine(toRoot, "StardewModdingAPI.exe");
            if (!File.Exists(a) || !File.Exists(b)) return false;
            var fa = new FileInfo(a); var fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;
            return (System.Diagnostics.FileVersionInfo.GetVersionInfo(a).FileVersion ?? "")
                == (System.Diagnostics.FileVersionInfo.GetVersionInfo(b).FileVersion ?? "");
        }
        catch { return false; }
    }

    /// <summary>清盘前把「不成套但确实装了」的 SMAPI 原样留档到
    /// _smapi-pool/as-found/&lt;版本&gt;-&lt;时间戳&gt;。不参与桶选择，仅供人工找回。</summary>
    private static void ArchivePartialSmapi(string gamePath, string? gameVersion)
    {
        try
        {
            var dest = Path.Combine(SmapiPoolRoot, "as-found",
                SafeDirName(gameVersion ?? "unknown") + "-" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            CopySmapiFiles(gamePath, dest);
            AppLog.Warn("DepotDownloader",
                $"游戏目录里的 SMAPI 不成套，已原样留档到 {dest}（清盘后可从这里找回，或在 Mods 页重装）");
        }
        catch (Exception ex) { AppLog.Warn("DepotDownloader", "留档残缺 SMAPI 失败: " + ex.Message); }
    }

    /// <summary>从共享池恢复 SMAPI 到游戏目录。桶缺失/残缺/tag 不匹配则 false。</summary>
    public static bool RestoreSmapiFromPool(string gamePath, string? gameVersion)
    {
        var bucket = SmapiPoolBucket(gameVersion);
        if (bucket == "unsupported") return false;
        var src = Path.Combine(SmapiPoolRoot, bucket);
        // 无 meta = 旧版逻辑攒的快照（多半是裸 exe 残缺品）→ 清掉不信任
        var tag = ReadSmapiPoolTag(src);
        if (tag is null || !IsCompleteSmapiSnapshot(src))
        {
            try { if (Directory.Exists(src)) Directory.Delete(src, true); } catch { }
            return false;
        }
        // 桶按大版本共享：桶里的 SMAPI 必须正是目标游戏版本该用的 tag，否则宁可走重装
        var want = UpdateService.RecommendSmapiTag(gameVersion);
        if (string.IsNullOrWhiteSpace(want) || !string.Equals(tag, want, StringComparison.OrdinalIgnoreCase))
            return false;
        CopySmapiFiles(src, gamePath);
        AppLog.Warn("DepotDownloader", $"SMAPI 已从共享池恢复（bucket={bucket}）");
        return true;
    }


    /// <summary>
    /// 安装/更新 SMAPI 成功后：写进当前版本 staging + 共享池，切回官方/其它历史版都免重装。
    /// </summary>
    public static void CacheSmapiForCurrentGame(string gamePath)
    {
        try
        {
            var ver = TryReadGameVersion(gamePath);
            // 共享池（官方最新版可能没有对应 staging，必须走这里）
            SaveSmapiToPool(gamePath, ver);
            SeedSmapiSampleMods(gamePath);

            var staging = FindStagingByVersionLabel(ver);
            if (staging is null) return;
            var dest = Path.Combine(staging, "smapi");
            try { if (Directory.Exists(dest)) Directory.Delete(dest, true); } catch { }
            CopySmapiFiles(gamePath, dest);
            AppLog.Warn("DepotDownloader", $"SMAPI 已缓存到版本 {ver}：{dest}");
        }
        catch (Exception ex) { AppLog.Warn("DepotDownloader", "缓存 SMAPI 失败: " + ex.Message); }
    }

    /// <summary>SMAPI 安装包的复用缓存路径：认得出该版本且有抽屉 → 存进<b>该版本抽屉</b>的
    /// smapi-installer（跟着版本走）；否则退到<b>通用安装包缓存下的 keep 子目录</b>
    /// （只用官方最新版、从没下过历史版本的用户也免重下 40 MB）。
    /// ⚠ 兜底不能再放回 depot-staging\smapi-cache：那是界面上看不见的第三个"安装包放哪儿"，
    /// 而 keep 就在存储页「SMAPI 安装包缓存」那一行的统计与清理范围内，空间随时可回收。</summary>
    public static string GetSmapiInstallerCacheDir(string? gameVersion)
    {
        var staging = FindStagingByVersionLabel(gameVersion);
        return staging is not null ? Path.Combine(staging, "smapi-installer")
            : Path.Combine(StoragePaths.SmapiInstallerDir, "keep");
    }

}
