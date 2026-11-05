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

    /// <summary>请求 GitHub 最新版本并与当前版本比较（API 失败自动走 HTML 回落）。
    /// <para>v1.2.11：两条通道内部改成并发竞速，并记下分段耗时。此前是「四条 API 通道串行
    /// → 三条 HTML 通道（还只有直连）」：直连不通时，一次检查要把 12 秒超时预算花在死链上
    /// 才轮到镜像，实测最坏 >50 秒，用户看到的就是「检查更新转半天，然后说没网」。</para></summary>
    public async Task<SelfUpdateInfo?> CheckAsync()
    {
        var sw = Stopwatch.StartNew();
        var api = await TryCheckViaApiAsync();
        var apiMs = sw.ElapsedMilliseconds;
        SelfUpdateInfo? result = api;
        if (result is null)
        {
            result = await TryCheckViaHtmlAsync();
            AppLog.Info("SelfUpdate", $"检查更新：API 通道 {apiMs}ms 未拿到版本，HTML 回落 {(sw.ElapsedMilliseconds - apiMs)}ms"
                + (result is null ? " 也失败" : " 成功"));
        }
        else
        {
            AppLog.Info("SelfUpdate", $"检查更新：API 通道 {apiMs}ms 命中 v{result.LatestVersion}");
        }
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
        string? marker = null;
        try { marker = File.ReadAllText(dest + ".done"); } catch { }
        if (want.Length == 0)
        {
            // 这次检查没拿到官方凭据（HTML 回落通道就是这样）。但 .done 里可能记着
            // 上次拿到的 sha256 —— 那是本机已有的一条可信凭据，不能因为这次没取到就丢掉：
            // 丢掉之后，任何字节只要躺在这个路径上都会被当成"完整缓存"直接执行。
            var recorded = RecordedSha(marker);
            if (recorded is null) return dest;   // 真的一条凭据都没有：维持原状（VerifyInstaller 会记 WARN）
            try
            {
                VerifyInstallerSha(dest, recorded);
                return dest;
            }
            catch (Exception ex)
            {
                DropInstaller(dest, ex.Message);
                return null;
            }
        }
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

    /// <summary>
    /// 这份缓存安装包当初是靠什么放行的 —— .done 第二个字段是不是空。
    ///
    /// <para>空 = 一条凭据都没有：API 被限流 → 走 HTML 回落 → 从第三方镜像下的包，
    /// 而那条通道从来拿不到官方 digest/size。这种包不该<b>自动</b>弹安装向导 ——
    /// 镜像换包至少不能再悄悄得手。判据读 .done 而不是这次检查的结果，
    /// 关掉应用再回来也绕不过（见 <see cref="CachedInstallerPath"/>）。</para>
    /// </summary>
    public static bool IsVerified(string installerPath)
    {
        try
        {
            var marker = File.ReadAllText(installerPath + ".done");
            var sp = marker.IndexOf(' ');
            return sp >= 0 && marker[(sp + 1)..].Trim().Length > 0;
        }
        catch { return false; }
    }

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

    /// <summary>从 <c>.done</c> 里取出上次记过的 sha256（格式「&lt;version&gt; sha256=…」）。
    /// 没有就返回 null。<b>只在这次检查拿不到官方凭据时用</b>：本机已有的凭据也是凭据，
    /// 不能因为这次查不到就把它丢了。</summary>
    internal static string? RecordedSha(string? markerText)
    {
        var line = (markerText ?? "").Trim();
        var sp = line.IndexOf(' ');
        if (sp < 0) return null;
        var rest = line[(sp + 1)..].Trim();
        const string prefix = "sha256=";
        return rest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && rest.Length > prefix.Length
            ? rest[prefix.Length..].Trim()
            : null;
    }

    /// <summary>按给定 sha256 复验本机文件；对不上就抛（调用方负责丢弃）。</summary>
    internal static void VerifyInstallerSha(string path, string sha256)
    {
        using var fs = File.OpenRead(path);
        var got = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        if (!string.Equals(got, sha256, StringComparison.OrdinalIgnoreCase))
            throw new InstallerIntegrityException(
                $"安装包 SHA-256 与本机记录的不符（本机 {Short(got)}，记录 {Short(sha256)}）"
                + "—— 已删除，需要重新下载");
    }

    private static string Short(string hex) => hex.Length <= 12 ? hex : hex[..12] + "…";

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

    // 通道 1：GitHub API（信息全，但有限流；直连不通时换镜像 —— gh-proxy 可代理 API）
    // <para><b>v1.2.11：并发竞速，不再串行 foreach</b>。直连 + 三条镜像同时发，先拿到版本的赢。
    // 此前串行时，一台直连不通的机器要把 12 秒超时预算耗在死链上才轮到镜像（实测本机四个通道
    // 单个耗时：直连 2.2s / ghfast 403 2.1s / gh-proxy 0.25s 即断 / ghproxy 403 2.0s —— 光是
    // 一路 403 累积就要 6.7s，真遇到超时是 48s+）。竞速后典型 = 最快那条（~2s），最坏 = 最慢那条。</para>
    // <para><b>403/429 不再早退</b>：那是匿名配额（60 次/小时/IP）打满，镜像代理的是同一个 API、
    // 同一个来源 IP，换过去只会再吃一次 403 —— 既然已经并发发了，就让它自然失败，整轮落空后
    // 走不受配额限制的 HTML 回落。</para>
    private async Task<SelfUpdateInfo?> TryCheckViaApiAsync()
    {
        var probes = UpdateService.GithubUrls(AppInfo.LatestApiUrl).Select(ProbeApiAsync).ToList();
        return await FirstNonNullAsync(probes);
    }

    private static async Task<SelfUpdateInfo?> ProbeApiAsync(string api)
    {
        try
        {
            using var resp = await Http.GetAsync(api);
            if (!resp.IsSuccessStatusCode)
            {
                var code = (int)resp.StatusCode;
                AppLog.Warn("SelfUpdate", $"API 通道失败：HTTP {code}（{new Uri(api).Host}）"
                    + (code is 403 or 429 ? "，配额打满 → 走 HTML 回落" : ""));
                // 直连吃 403/429 = 本机 IP 的匿名配额（60 次/小时）打满。镜像代理的是同一个 API，
                // 换过去只会再吃一次 403 —— 这是原串行版本「直接跳出镜像循环」的那条判断，
                // 改成并发后要显式抛出来，否则会陪着一条挂到 12 秒的镜像把预算耗光。
                // 只认直连这一条：某个镜像 403 可能只是它自己的问题，不能因此放弃整轮 API。
                if (code is 403 or 429 && string.Equals(api, AppInfo.LatestApiUrl, StringComparison.OrdinalIgnoreCase))
                    throw new QuotaExhaustedException();
                return null;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync());
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            // 只有这条通道能拿到官方校验值（release asset 上的 digest/size）
            return Build(tag, root);
        }
        catch (QuotaExhaustedException) { throw; }   // 配额信号要穿透到 FirstNonNullAsync，不能被下面的兜底吞掉
        catch (Exception ex)
        {
            AppLog.Warn("SelfUpdate", $"API 通道异常（{new Uri(api).Host}）：{ex.Message}");
            return null;
        }
    }

    // 通道 2：releases/latest 页面 302 重定向里抠 tag（SMAPI 检查同款方案，无配额限制）
    // <para><b>v1.2.11：补上镜像链</b>。这条以前只打 github.com 直连 —— 而它恰恰只在
    // 「直连不通 / 配额打满」时才会走到，于是在最需要它的场景里它必失败（12 秒超时后
    // 报「没网」），全库其它 GitHub 出口都挂了镜像，唯独这里漏了。</para>
    private async Task<SelfUpdateInfo?> TryCheckViaHtmlAsync()
    {
        var probes = UpdateService.GithubUrls(AppInfo.ReleasesUrl + "/latest").Select(ProbeHtmlAsync).ToList();
        return await FirstNonNullAsync(probes);
    }

    private static async Task<SelfUpdateInfo?> ProbeHtmlAsync(string url)
    {
        try
        {
            using var resp = await Http.GetAsync(url);
            var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? "";
            var marker = finalUrl.IndexOf(TagMarker, StringComparison.OrdinalIgnoreCase);
            if (!resp.IsSuccessStatusCode || marker < 0)
            {
                AppLog.Warn("SelfUpdate", $"HTML 回落失败：HTTP {(int)resp.StatusCode}（{new Uri(url).Host}）");
                return null;
            }
            return Build(finalUrl[(marker + TagMarker.Length)..]);
        }
        catch (Exception ex)
        {
            AppLog.Warn("SelfUpdate", $"HTML 回落异常（{new Uri(url).Host}）：{ex.Message}");
            return null;
        }
    }

    /// <summary>直连 API 明确回了 403/429 —— 本机 IP 的匿名配额打满，继续试镜像没有意义
    /// （见 <see cref="ProbeApiAsync"/> 里的说明）。仅用于在竞速中立刻让位给 HTML 回落。</summary>
    private sealed class QuotaExhaustedException : Exception;

    /// <summary>并发跑一组候选，返回第一个非 null 的结果；全部失败返回 null。
    /// <para>刻意不用 Task.WhenAll —— 一条挂到超时的慢通道不该拖住已经拿到答案的那条。
    /// 落败的探针不取消（都是几百字节的请求，各自 abort 的复杂度不值得）。</para></summary>
    private static async Task<SelfUpdateInfo?> FirstNonNullAsync(List<Task<SelfUpdateInfo?>> probes)
    {
        try
        {
            while (probes.Count > 0)
            {
                var done = await Task.WhenAny(probes);
                probes.Remove(done);
                SelfUpdateInfo? info;
                try { info = await done; }               // 探针内部吃掉所有网络异常，这里只可能收到配额信号
                catch (QuotaExhaustedException) { return null; }
                if (info is not null) return info;
            }
            return null;
        }
        finally
        {
            // v1.2.14：落败的探针不取消，但它们的异常必须有人「领走」。
            // 之前直连那条在配额打满时抛 QuotaExhaustedException，而镜像先返回结果 → 这里直接 return，
            // 那个 task 的异常再没人 await，最终由终结器线程重抛成
            // `[ERR] [Task] AggregateException ... unobserved`（每次启动刷一条）。
            // 探针本身不取消（几百字节的请求，各自 abort 的复杂度不值得），所以只挂一个只管异常的延续。
            foreach (var t in probes) ObserveFault(t);
        }
    }

    /// <summary>把一条可能失败的任务标记为「已观察」，避免未观察异常在终结器线程重抛。
    /// 用延续而不是 await：调用点要立刻返回结果，不能陪着一条挂到超时的慢通道。</summary>
    private static void ObserveFault(Task task)
    {
        if (task.IsCompleted)
        {
            _ = task.Exception;                      // 访问即标记 observed；成功/取消时为 null
            return;
        }
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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
