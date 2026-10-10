using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace JuniGrid.Services;

/// <summary>一次自更新检查的结果。<see cref="SetupSha256"/> 只有 API 通道能填（GitHub 在
/// release asset 上直接给 <c>digest</c>）；HTML 回落那条只抠得到 tag，此时为 null，
/// 完整性就只能退回比长度。<see cref="SetupSize"/> 同理，0 = 不知道。</summary>
public sealed record SelfUpdateInfo(string LatestVersion, string DownloadUrl, string SetupUrl, bool HasUpdate,
    string? SetupSha256 = null, long SetupSize = 0);

/// <summary>
/// 应用自更新的唯一通道（检查 + 下载缓存 + 弹出安装）：
///  1) api.github.com /releases/latest —— 信息全，但匿名配额 60 次/小时/IP，容易被限流；
///  2) github.com/.../releases/latest HTML 302 回落 —— 最终跳转 URL 里带 tag，不受 API 配额限制。
/// 安装包下载到 StoragePaths.SelfUpdateDir（断点续传）：
///  · 已完整缓存 → 点击按钮直接弹安装向导，不再下载；
///  · 安装包向导被用户关掉 → 缓存保留，下次点击直接再弹；
///  · 安装成功（应用版本 >= 安装包版本）后，下次启动自动清掉缓存。
/// </summary>
public sealed class SelfUpdateService
{
    private const string TagMarker = "/releases/tag/";
    private static readonly HttpClient Http = CreateClient();
    private static readonly HttpClient DownloadHttp = CreateClient(minutes: 10);
    private volatile SelfUpdateInfo? _latest;

    /// <summary>最近一次检查结果；null = 还没查到（未检查或失败）。</summary>
    public SelfUpdateInfo? Latest => _latest;

    /// <summary>检查完成后通知（UI 订阅刷新角标/按钮）。</summary>
    public event Action? Changed;

    /// <summary>v1.1.4：安装包正在下载（缓存目录即将有百 MB 级文件写入）。
    /// 缓存与存储页的「自更新安装包缓存」清理/迁移据此避让，防止删掉正在下的包。</summary>
    public static volatile bool CacheBusy;

    private static HttpClient CreateClient(double minutes = 0.2)
    {
        var h = new HttpClient();
        h.DefaultRequestHeaders.UserAgent.ParseAdd("JuniGrid-Launcher");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        h.Timeout = TimeSpan.FromMinutes(minutes);
        return h;
    }

    /// <summary>启动后台预检查（不阻塞 UI，失败静默）：清掉已装完的旧安装包缓存，
    /// 并清掉旧肖像页留在 <c>Mods</c> 里的覆盖包。
    /// <para>覆盖包那份<b>不能</b>只挂在"自更新落地那一次"上 —— 绝大多数人是自己下载 setup.exe
    /// 覆盖装的，那条路上 <see cref="CleanupOldInstallers"/> 压根不会返回 true，于是包一直躺在
    /// Mods 里被 Content Patcher 加载，而这一版没有立绘页，界面上没有任何地方能关它
    /// （孤儿检测也看不见：它只遍历本体覆盖那本账，而肖像页从不写那本账）。
    /// 判据本身幂等（哪儿都没有就返回 0），所以每次启动扫一遍比"赌对那一次"可靠。</para></summary>
    public void StartBackgroundCheck(string? gamePath, DepotDownloaderService depot)
        => _ = Task.Run(async () =>
        {
            CleanupOldInstallers();
            PurgePortraitOverrides(PortraitTargets(gamePath, depot));
            try { await CheckAsync(); } catch { }
        });

    /// <summary>旧肖像页写进 Mods 的覆盖包名。立绘页已随换肤子系统一起删掉，包留在 Mods 里
    /// 仍会被 Content Patcher 加载：~ = 启用态，.~ = 禁用态，后两个是改名前的旧名。</summary>
    private static readonly string[] PortraitOverrideNames =
    {
        "~JuniGrid Portrait Overrides",
        ".~JuniGrid Portrait Overrides",
        "JuniGrid Portrait Overrides",
        ".JuniGrid Portrait Overrides",
    };

    /// <summary>要扫的 Mods 目录：当前游戏 + 每一个已暂存版本各自的抽屉。</summary>
    private static List<string> PortraitTargets(string? gamePath, DepotDownloaderService depot)
    {
        var dirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(gamePath)) dirs.Add(Path.Combine(gamePath, "Mods"));
        try { dirs.AddRange(depot.ListStagedPackages().Select(p => Path.Combine(p.Path, "Mods"))); }
        catch { }
        return dirs;
    }

    /// <summary>硬删这些覆盖包，直接 Delete 不进 Mods/.junigrid_trash（回收站是给用户手删
    /// mod 用的，这份是应用自己造的）。哪儿都没有就什么都不做，返回 0。</summary>
    public static int PurgePortraitOverrides(List<string> modsDirs)
    {
        var deleted = 0;
        foreach (var mods in modsDirs)
        {
            if (!Directory.Exists(mods)) continue;
            foreach (var name in PortraitOverrideNames)
            {
                var full = Path.Combine(mods, name);
                if (!Directory.Exists(full)) continue;
                try { Directory.Delete(full, recursive: true); deleted++; }
                catch (Exception ex) { AppLog.Warn("SelfUpdate", $"删除旧肖像覆盖包失败：{full} — {ex.Message}"); }
            }
        }
        if (deleted > 0)
            AppLog.Warn("SelfUpdate", $"启动清理：已删除旧肖像页覆盖包 {deleted} 份（直接删除，未进回收站）");
        return deleted;
    }

    /// <summary>请求 GitHub 最新版本并与当前版本比较（API 失败自动走 HTML 回落）。</summary>
    public async Task<SelfUpdateInfo?> CheckAsync()
    {
        SelfUpdateInfo? result = await TryCheckViaApiAsync() ?? await TryCheckViaHtmlAsync();
        if (result is not null)
        {
            _latest = result;
            try { Changed?.Invoke(); } catch { }
        }
        return _latest;
    }

    /// <summary>安装包是否已完整缓存（下载成功后有 .done 标记，且标记里记的校验值对得上）。</summary>
    public string? CachedInstallerPath(SelfUpdateInfo info)
    {
        var dest = InstallerPath(info.LatestVersion);
        if (!File.Exists(dest) || !File.Exists(dest + ".done")) return null;

        // .done 记的是"当初放行这个文件时依据的是什么"。它和这次查到的官方值一致 → 直接信，
        // 不用每次开机都把 120MB 重算一遍。对不上、或压根没记（老版本写的 .done 只有版本号）
        // → 补一次本地复验：通过就把校验值补写进 .done（这份旧缓存就此归队，不用重下），
        // 不通过就删掉重下。
        var want = Expectation(info);
        if (want.Length == 0) return dest;   // 这次连官方凭据都没拿到，只能沿用「.done 在」这条弱判据
        string? marker = null;
        try { marker = File.ReadAllText(dest + ".done"); } catch { }
        if (MarkerRecords(marker, want)) return dest;
        try
        {
            VerifyInstaller(dest, info);
            File.WriteAllText(dest + ".done", info.LatestVersion + " " + want);
            return dest;
        }
        catch (Exception ex)
        {
            DropInstaller(dest, ex.Message);
            return null;
        }
    }

    /// <summary>.done 里该记的校验值：优先官方 sha256，退而求其次官方 size，都没有就是空串
    /// （空串 = 这台机器上没有任何凭据能证这个文件，见 <see cref="VerifyInstaller"/>）。</summary>
    internal static string Expectation(SelfUpdateInfo info) =>
        info.SetupSha256 is { Length: > 0 } sha ? "sha256=" + sha
        : info.SetupSize > 0 ? "size=" + info.SetupSize : "";

    /// <summary><c>.done</c> 的格式是「&lt;version&gt; &lt;校验值&gt;」。校验值可能为空
    /// （老版本只写了版本号，或那次压根没拿到官方凭据）—— 那种缓存是"无凭据放行"留下的，
    /// 不能只凭文件存在就信。判据就是：记的校验值等不等于这次该用的那个。</summary>
    internal static bool MarkerRecords(string? markerText, string want)
    {
        if (want.Length == 0) return false;   // 没有"该用的那个"，谈不上对得上
        var line = (markerText ?? "").Trim();
        var sp = line.IndexOf(' ');
        return sp > 0 && string.Equals(line[(sp + 1)..].Trim(), want, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>校验落盘的安装包。长度对不上或 sha256 对不上都抛
    /// <see cref="InstallerIntegrityException"/>；两者都无从判断时不抛，但记一条 WARN，
    /// 让日志能说清"这次是自更新链路没拿到官方校验值"，而不是"验过了没问题"。</summary>
    internal static void VerifyInstaller(string path, SelfUpdateInfo info)
    {
        var len = new FileInfo(path).Length;
        if (info.SetupSize > 0 && len != info.SetupSize)
            throw new InstallerIntegrityException(
                $"安装包大小不符（本机 {len} 字节，官方 {info.SetupSize} 字节）—— 已删除，需要重新下载");
        if (info.SetupSha256 is { Length: > 0 } want)
        {
            using var fs = File.OpenRead(path);
            var got = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
            if (!string.Equals(got, want, StringComparison.OrdinalIgnoreCase))
                throw new InstallerIntegrityException(
                    $"安装包 SHA-256 不符（本机 {got[..12]}…，官方 {want[..12]}…）—— 已删除，需要重新下载");
            return;
        }
        AppLog.Warn("SelfUpdate", $"安装包未做 SHA-256 校验（本次检查没取到官方 digest，只比了长度 {len} 字节）");
    }

    private static void DropInstaller(string dest, string why)
    {
        AppLog.Warn("SelfUpdate", "安装包不可信，已丢弃：" + why);
        try { File.Delete(dest); } catch { }
        try { File.Delete(dest + ".done"); } catch { }
    }

    /// <summary>校验不通过（大小或哈希对不上官方值）。和"网络断了"分开报：处理动作同样是重试，
    /// 但用户有权知道这次是包被截断/被换过，而不是一直怀疑自己网差。</summary>
    public sealed class InstallerIntegrityException(string message) : Exception(message);

    public static string InstallerPath(string version)
        => Path.Combine(StoragePaths.SelfUpdateDir, $"JuniGrid-cn-v{version}-setup.exe");

    /// <summary>
    /// 确保安装包已缓存：已完整缓存直接返回路径；否则下载（断点续传）到缓存目录，
    /// <b>校验通过才写 .done</b>。v1.2.6 之前是"下载函数一返回就写 .done"，而那条路走的是
    /// 多镜像 + Range 续传，中途被截断同样算成功 —— 于是一个残缺的 setup.exe 会被标成完整、
    /// 并在下次点击时直接拉起来。
    /// </summary>
    public async Task<string> EnsureInstallerAsync(SelfUpdateInfo info,
        Action<string, double?>? progress, CancellationToken ct = default)
    {
        var cached = CachedInstallerPath(info);
        if (cached is not null)
        {
            progress?.Invoke("安装包已就绪", 100);
            return cached;
        }

        Directory.CreateDirectory(StoragePaths.SelfUpdateDir);
        var dest = InstallerPath(info.LatestVersion);
        SelfUpdateService.CacheBusy = true;
        try
        {
            // v1.6.2：setup.exe 下载同样挂镜像候选（直连 GitHub 在国内经常超时）
            await ResumableDownload.RunAsync(DownloadHttp, info.SetupUrl, dest,
                (msg, pct, _) => progress?.Invoke(msg, pct),
                fallbackUrls: UpdateService.GithubUrls(info.SetupUrl).Skip(1), ct: ct);

            try { VerifyInstaller(dest, info); }
            catch { DropInstaller(dest, "校验未通过"); throw; }

            // 校验完整结束才写 .done，且把"依据的是什么"一起写进去（见 CachedInstallerPath）
            File.WriteAllText(dest + ".done", info.LatestVersion + " " + Expectation(info));
            progress?.Invoke("下载完成", 100);
            return dest;
        }
        finally
        {
            SelfUpdateService.CacheBusy = false;
        }
    }

    /// <summary>弹出安装向导（可见向导，非静默）。旧版应用由调用方自行退出，
    /// 因此不带 /CLOSEAPPLICATIONS —— 不会弹「关闭应用」询问框；
    /// 向导里点「完成」后 Inno 按 [Run] 自动启动新版本；向导被直接关掉也不影响缓存。</summary>
    public void LaunchInstaller(string path)
    {
        Process.Start(new ProcessStartInfo(path)
        { UseShellExecute = true });
    }

    /// <summary>启动时清理：缓存安装包的版本 <= 当前应用版本 = 已装完，删掉缓存。
    /// 返回 true = 这次确实清掉过一份，也就是更新已经落地。</summary>
    private static bool CleanupOldInstallers()
    {
        var landed = false;
        try
        {
            var dir = StoragePaths.SelfUpdateDir;
            if (!Directory.Exists(dir)) return false;
            if (!Version.TryParse(AppInfo.Version.Trim(), out var current)) return false;

            foreach (var exe in Directory.GetFiles(dir, "JuniGrid-cn-v*-setup.exe"))
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    Path.GetFileName(exe), @"\d+(?:\.\d+)+");
                if (!m.Success || !Version.TryParse(m.Value, out var v)) continue;
                if (v <= current)
                {
                    File.Delete(exe);
                    if (File.Exists(exe + ".done")) File.Delete(exe + ".done");
                    AppLog.Warn("SelfUpdate", $"已清理旧版安装包缓存：{Path.GetFileName(exe)}");
                    landed = true;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("SelfUpdate", $"清理安装包缓存失败：{ex.Message}");
        }
        return landed;
    }

    // 通道 1：GitHub API（信息全，但有限流；直连不通依次换镜像 —— gh-proxy 可代理 API）
    // <para><b>403/429 例外</b>：那是匿名配额（60 次/小时/IP）打满，镜像代理的是同一个 API、
    // 同一个来源 IP，换过去只会再吃一次 403。实测每次开机因此白打四条请求、刷四条日志，
    // 而真正拿到版本的是下面那条不受配额限制的 HTML 回落 —— 所以这两种状态码直接跳出镜像循环。</para>
    private async Task<SelfUpdateInfo?> TryCheckViaApiAsync()
    {
        foreach (var api in UpdateService.GithubUrls(AppInfo.LatestApiUrl))
        {
            try
            {
                using var resp = await Http.GetAsync(api);
                if (!resp.IsSuccessStatusCode)
                {
                    var code = (int)resp.StatusCode;
                    AppLog.Warn("SelfUpdate", $"API 通道失败：HTTP {code}（{new Uri(api).Host}）"
                        + (code is 403 or 429 ? "，配额打满，换镜像没用 → 走 HTML 回落" : "，换下一通道"));
                    if (code is 403 or 429) return null;
                    continue;
                }
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync());
                var root = doc.RootElement;
                var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                // 只有这条通道能拿到官方校验值（release asset 上的 digest/size）
                return Build(tag, root);
            }
            catch (Exception ex)
            {
                AppLog.Warn("SelfUpdate", $"API 通道异常：{ex.Message}，换下一通道");
            }
        }
        return null;
    }

    // 通道 2：releases/latest 页面 302 重定向里抠 tag（SMAPI 检查同款方案，无配额限制）
    private async Task<SelfUpdateInfo?> TryCheckViaHtmlAsync()
    {
        try
        {
            using var resp = await Http.GetAsync(AppInfo.ReleasesUrl + "/latest");
            var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? "";
            var marker = finalUrl.IndexOf(TagMarker, StringComparison.OrdinalIgnoreCase);
            if (!resp.IsSuccessStatusCode || marker < 0)
            {
                AppLog.Warn("SelfUpdate", $"HTML 回落失败：HTTP {(int)resp.StatusCode}");
                return null;
            }
            var tag = finalUrl[(marker + TagMarker.Length)..];
            return Build(tag);
        }
        catch (Exception ex)
        {
            AppLog.Warn("SelfUpdate", $"HTML 回落异常：{ex.Message}");
            return null;
        }
    }

    internal static string SetupAssetName(string ver) => $"JuniGrid-cn-v{ver}-setup.exe";

    internal static SelfUpdateInfo? Build(string tag, JsonElement? release = null)
    {
        // tag 允许带 v 前缀（v1.0.2 / 1.0.2 都认）
        var ver = tag.TrimStart('v', 'V');
        if (!Version.TryParse(ver, out var latest)) return null;
        if (!Version.TryParse(AppInfo.Version.Trim(), out var current)) return null;

        // Release 资产固定命名：JuniGrid-cn-vX.Y.Z-setup.exe
        var setupUrl = $"{AppInfo.ReleasesUrl}/latest/download/{SetupAssetName(ver)}";
        var hasUpdate = latest > current;
        var (sha, size) = release is { } r ? ReadAssetIntegrity(r, SetupAssetName(ver)) : (null, 0L);
        return new SelfUpdateInfo(ver, $"{AppInfo.ReleasesUrl}/tag/v{ver}", setupUrl, hasUpdate, sha, size);
    }

    /// <summary>从 release JSON 的 <c>assets[]</c> 里按文件名取官方校验值。
    /// <c>digest</c> 形如 <c>"sha256:f2e8…"</c>（GitHub 2024 起提供，早于它的上传可能为 null）；
    /// <c>size</c> 是精确字节数。取不到的那一项返回 null/0，由 <see cref="VerifyInstaller"/> 降级处理。</summary>
    internal static (string? Sha256, long Size) ReadAssetIntegrity(JsonElement release, string assetName)
    {
        string? sha = null;
        long size = 0;
        if (release.ValueKind != JsonValueKind.Object
            || !release.TryGetProperty("assets", out var assets)
            || assets.ValueKind != JsonValueKind.Array) return (null, 0);
        foreach (var a in assets.EnumerateArray())
        {
            if (a.ValueKind != JsonValueKind.Object
                || !a.TryGetProperty("name", out var n)
                || n.ValueKind != JsonValueKind.String
                || !string.Equals(n.GetString(), assetName, StringComparison.OrdinalIgnoreCase)) continue;
            if (a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String)
            {
                var s = d.GetString() ?? "";
                var colon = s.IndexOf(':');
                sha = colon < 0 ? s : s[(colon + 1)..];   // 只认 sha256 那一种，前缀丢掉
                if (colon >= 0 && !s[..colon].Equals("sha256", StringComparison.OrdinalIgnoreCase)) sha = null;
            }
            if (a.TryGetProperty("size", out var z) && z.ValueKind == JsonValueKind.Number && z.TryGetInt64(out var l))
                size = l;
            break;
        }
        return (sha, size);
    }
}
