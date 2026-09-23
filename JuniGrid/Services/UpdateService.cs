using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JuniGrid.Services;

/// <summary>
/// Version probing + SMAPI update checks.
///  - Game version comes from Stardew Valley.dll file metadata (offline);
///    the exe is only a fallback (its version is the native bootstrapper's own).
///  - SMAPI's latest release comes from the public GitHub Releases API
///    (no key required, ~60 requests/hour — we check once per app run).
///  - Updating downloads the official SMAPI installer zip and launches its
///    "install on Windows.bat", which handles the actual in-place update.
/// </summary>
public sealed class UpdateService
{
    private static readonly HttpClient Http = CreateClient();
    private SmapiUpdateInfo? _cached;
    private DateTime _cachedAt;

    private static HttpClient CreateClient()
    {
        var h = new HttpClient();
        h.DefaultRequestHeaders.UserAgent.ParseAdd("JuniGrid-Launcher");
        // GitHub API 推荐携带的接受头，能降低限流概率。
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        h.Timeout = TimeSpan.FromSeconds(12);
        return h;
    }

    public void Invalidate() { _cached = null; _cachedAt = DateTime.MinValue; }

    /// <summary>
    /// 按游戏本体版本推荐兼容的 SMAPI（用于旧版切换后提示，不自动安装）。
    /// 版本对照来自 SMAPI 官方兼容表的大致区间。
    /// </summary>
    public static (string Line, string? Url) RecommendSmapi(string? gameVersion)
    {
        var tag = RecommendSmapiTag(gameVersion);
        if (tag is null)
            return ("当前游戏版本没有可一键安装的 SMAPI（官方未提供匹配 release）。如需 mod，请在 Nexus 手动找对应历史包",
                "https://www.nexusmods.com/stardewvalley/mods/2400?tab=files");
        if (tag == SmapiLatest)
            return ("当前为 1.6+，请使用 SMAPI 4.x（Mod 页可一键装最新 4.x）", "https://smapi.io/");
        return ($"请使用 SMAPI {tag}（该系列适配当前游戏版本）", "https://github.com/Pathoschild/SMAPI/releases/tag/" + tag);
    }

    /// <summary>1.6+ 走 SMAPI 最新 4.x 的哨兵值（不是 GitHub tag）。</summary>
    public const string SmapiLatest = "latest";

    /// <summary>对外展示的版本号：文件内部版本号与最近一次应用的历史版本一致时，显示营销版本号
    ///（星露谷 1.4 的内部版本是 1.3.7269、1.0 是 1.0.5900，直接显示会把人绕晕）。</summary>
    public static string DisplayGameVersion(string? internalVersion, string? lastLabel, string? lastInternal)
    {
        if (!string.IsNullOrWhiteSpace(lastLabel)
            && !string.IsNullOrWhiteSpace(lastInternal)
            && string.Equals(lastInternal, internalVersion, StringComparison.OrdinalIgnoreCase))
            return lastLabel;
        if (string.IsNullOrWhiteSpace(internalVersion)) return "";   // 探测中：上层画 shimmer，别显示「未定位」
        // 没有切换记录时也不能把内部号直接甩给用户 —— 1.0 的内部号本来就是 1.0.5900
        return EngineInternalToFamily(internalVersion) ?? internalVersion;
    }

    /// <summary>Windows FileVersion 只有两个"对不上号"的内部版本：1.0 全系列是 1.0.5900、
    /// 1.4 全系列是 1.3.7269（补丁号在文件版本里根本体现不出来）。其余版本原样返回。</summary>
    public static string? EngineInternalToFamily(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (s.StartsWith("1.0.5900", StringComparison.OrdinalIgnoreCase)) return "1.0";
        if (s.StartsWith("1.3.7269", StringComparison.OrdinalIgnoreCase)) return "1.4";
        return null;
    }

    /// <summary>
    /// 游戏版本 → SMAPI 安装策略：
    /// <see cref="SmapiLatest"/> = 1.6+ 用最新 4.x；
    /// 具体 tag = GitHub 上仍有官方 release、且该 release 声明支持当前游戏版本；
    /// null = 1.0/1.1、1.2.26–1.2.29、1.3.26 及更早等，没有可靠一键 tag。
    /// 版本对照来自 SMAPI 各 release 正文的「Requires Stardew Valley …」声明（2026-03 核对）：
    ///   3.18.2→1.5.6+  3.13.0→1.5.5+  3.12.2→1.5.4+  3.8.3→1.5.2+  3.8.1→1.5.1+
    ///   3.7.3→1.4.1+（含 1.5.0）  3.0.1→1.4+  2.11→1.3.36+  2.7→1.3.28+  2.6→1.3.27+
    ///   2.5/2.4→1.2.30+
    /// 注意：旧逻辑把「1.3→3.9.5 / 1.4→3.12.2」写错了 —— 这两个 tag 实际都要求 1.5.4+。
    /// </summary>
    public static string? RecommendSmapiTag(string? gameVersion)
    {
        if (string.IsNullOrWhiteSpace(gameVersion)) return null;
        var (major, minor, patch) = ParseComparableGameVersion(gameVersion);
        if (major <= 0) return null;

        if (major > 1 || (major == 1 && minor >= 6)) return SmapiLatest;

        // ── 1.5.x ──
        if (major == 1 && minor == 5)
        {
            if (patch >= 6) return "3.18.2";
            if (patch >= 5) return "3.13.0";
            if (patch >= 4) return "3.12.2";
            if (patch >= 2) return "3.8.3";
            if (patch >= 1) return "3.8.1";
            return "3.7.3";   // 1.5.0：3.7.3 要求 1.4.1+，可跑
        }

        // ── 1.4.x ──
        if (major == 1 && minor == 4)
        {
            if (patch >= 1) return "3.7.3";   // 1.4.1–1.4.5
            return "3.0.1";                   // 1.4.0：3.0.x 专为 1.4 发布
        }

        // ── 1.3.x ──
        if (major == 1 && minor == 3)
        {
            if (patch >= 36) return "2.11";
            if (patch >= 28) return "2.7";
            if (patch >= 27) return "2.6";
            return null;   // 1.3.0–1.3.26：2.6 起才要求 1.3.27+
        }

        // ── 1.2.x ──
        if (major == 1 && minor == 2)
        {
            if (patch >= 30) return "2.5";
            return null;   // 1.2.26–1.2.29：2.4/2.5 都要求 1.2.30+
        }

        return null;   // 1.0 / 1.1
    }

    /// <summary>
    /// 解析成可比较的 (major, minor, patch)，吸收两类历史坑：
    /// ① Windows FileVersion：游戏 1.4 全系列内部号是 1.3.7269、1.0 是 1.0.5900；
    /// ② 营销号：1.11=1.1.1、1.07=1.0.7、1.051b=1.0.51。
    /// </summary>
    private static (int Major, int Minor, int Patch) ParseComparableGameVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (0, 0, 0);
        var s = raw.Trim();

        if (s.StartsWith("1.3.7269", StringComparison.OrdinalIgnoreCase)) return (1, 4, 5);
        if (s.StartsWith("1.0.5900", StringComparison.OrdinalIgnoreCase)) return (1, 0, 0);

        // 去掉尾部字母后缀（1.051b）
        s = new string(s.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        if (s.Length == 0) return (0, 0, 0);

        var parts = s.Split('.', StringSplitOptions.RemoveEmptyEntries);
        static int Num(string p) => int.TryParse(p, out var n) ? n : 0;
        if (parts.Length == 0) return (0, 0, 0);

        var major = Num(parts[0]);
        if (parts.Length == 1) return (major, 0, 0);

        var minorRaw = parts[1];
        // 两位营销号：1.11 → 1.1.1；1.07 → 1.0.7
        if (parts.Length == 2 && minorRaw.Length == 2 && major == 1)
            return (1, minorRaw[0] - '0', minorRaw[1] - '0');
        // 1.051 / 1.051b → 1.0 系列补丁号
        if (parts.Length == 2 && minorRaw.Length >= 3 && minorRaw[0] == '0' && major == 1)
            return (1, 0, Num(minorRaw));

        var minor = Num(minorRaw);
        var patch = parts.Length >= 3 ? Num(parts[2]) : 0;
        return (major, minor, patch);
    }

    /// <summary>当前游戏是否没有可一键安装的 SMAPI（过旧）。</summary>
    public static bool IsSmapiUnsupported(string? gameVersion)
        => RecommendSmapiTag(gameVersion) is null && !string.IsNullOrWhiteSpace(gameVersion);

    /// <summary>
    /// 从 release 资产名里挑「玩家用」安装包：
    /// 优先 SMAPI-*-installer.zip / SMAPI.installer.zip，其次 SMAPI.zip；
    /// 排除 for-developers（开发者包，结构不同）、Z_OLD_*（归档旧包）、double-zipped。
    /// GitHub 字母序会把 installer-for-developers 排在 installer 前面 —— 旧逻辑会下错包。
    /// </summary>
    private static int ScoreInstallerAsset(string name)
    {
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return -1;
        if (name.Contains("double-zipped", StringComparison.OrdinalIgnoreCase)) return -1;
        if (name.Contains("for-developers", StringComparison.OrdinalIgnoreCase)) return -1;
        if (name.Contains("Z_OLD", StringComparison.OrdinalIgnoreCase)) return -1;
        if (name.Contains("installer", StringComparison.OrdinalIgnoreCase)) return 100;
        if (name.Equals("SMAPI.zip", StringComparison.OrdinalIgnoreCase)) return 50;
        return -1;
    }

    /// <summary>拉取与当前游戏版本匹配的 SMAPI Release（GitHub tag）。latest 仍走 /releases/latest。</summary>
    public async Task<SmapiUpdateInfo> CheckSmapiForGameAsync(string? installedVersion, string? gameVersion, bool force = false)
    {
        var tag = RecommendSmapiTag(gameVersion);
        if (tag is null)
        {
            // 1.0/1.1、1.2.29-、1.3.26- 等：没有一键包 —— 不要回落 latest（那是 1.6+ 专用的 SMAPI 4.x）
            return new SmapiUpdateInfo(installedVersion, null, false, false, null,
                "https://www.nexusmods.com/stardewvalley/mods/2400?tab=files",
                "当前游戏版本没有适配的一键安装 SMAPI");
        }
        if (tag == SmapiLatest) return await CheckSmapiAsync(installedVersion, force);

        // v1.1.8：历史 tag 与 latest 同样吃国内镜像 —— api.github.com / expanded_assets
        // 直连不通时依次换通道；拿到的 browser_download_url 仍是 github.com 原链，
        // 真正下 zip 时 DownloadToFileAsync 会再套一层镜像。
        string? zipUrl = null;
        string? tagName = tag;
        string pageUrl = "https://github.com/Pathoschild/SMAPI/releases/tag/" + tag;

        foreach (var api in GithubUrls($"https://api.github.com/repos/Pathoschild/SMAPI/releases/tags/{tag}"))
        {
            try
            {
                var json = await Http.GetStringAsync(api);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                tagName = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? tag : tag;
                if (root.TryGetProperty("html_url", out var h) && h.GetString() is { Length: > 0 } page)
                    pageUrl = page;
                var bestScore = -1;
                if (root.TryGetProperty("assets", out var assets))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        var score = ScoreInstallerAsset(name);
                        if (score <= bestScore) continue;
                        if (asset.TryGetProperty("browser_download_url", out var d) && d.GetString() is { } url)
                        {
                            zipUrl = url;
                            bestScore = score;
                        }
                    }
                }
                if (zipUrl is not null)
                    return new SmapiUpdateInfo(installedVersion, tagName, false, true, zipUrl, pageUrl, null);
            }
            catch
            {
                // 换下一个通道（镜像/直连）
            }
        }

        // api.github.com 未认证有 60 次/小时限流（共享出口 IP 下很容易 403）——
        // 回落到 releases 页面的资产清单 HTML 解析直链（无鉴权、不限流），同样带镜像
        foreach (var htmlUrl in GithubUrls($"https://github.com/Pathoschild/SMAPI/releases/expanded_assets/{tag}"))
        {
            try
            {
                var html = await Http.GetStringAsync(htmlUrl);
                string? best = null;
                var bestScore = -1;
                foreach (Match m in Regex.Matches(html, "href=\"([^\"]*releases/download/[^\"]*\\.zip)\""))
                {
                    var u = m.Groups[1].Value.Replace("&amp;", "&");
                    var fileName = u[(u.LastIndexOf('/') + 1)..];
                    var score = ScoreInstallerAsset(fileName);
                    if (score <= bestScore) continue;
                    best = u.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? u : "https://github.com" + u;
                    bestScore = score;
                }
                if (best is not null)
                    return new SmapiUpdateInfo(installedVersion, tagName, false, true, best, pageUrl, null);
            }
            catch
            {
                // 换下一个通道
            }
        }

        return new SmapiUpdateInfo(installedVersion, tag, false, true,
            null, pageUrl,
            $"拉取 SMAPI {tag} 失败：GitHub 直连与镜像均不可用");
    }

    // ------------------------------------------------------------------
    // Game version (local, offline)
    // ------------------------------------------------------------------
    /// <summary>当前游戏版本 —— 先认「我们自己把它铺成哪个版本」的记录，再退回读文件。
    /// 全项目读版本只走这一个出口。</summary>
    public string? GetGameVersion(string gamePath) => ResolveCurrentVersion(gamePath);

    public static string? ResolveCurrentVersion(string? gamePath)
        => ReadDeployedRecord(gamePath) ?? ReadLocalGameVersion(gamePath);

    private const string DeployRecordName = ".junigrid-version";

    /// <summary>切换成功后记下「这个目录是被我们铺成哪个发行号的」+ 当时本体文件的指纹。
    /// 为什么需要：1.0–1.4 那批 XNA 的 exe 自带版本资源不跟发行号走（实测 1.2.19/1.2.26/1.2.30
    /// 都自报 1.0.61xx），只读文件会把它们当成游戏 1.0 → 判「无适配 SMAPI」→ 不装加载器、
    /// Mods 不进版本抽屉、切走时也不归档本体。
    /// 之后本体被 Steam 校验/更新或手动换过，指纹对不上就自动作废，退回读文件。</summary>
    public static void RecordDeployedVersion(string gamePath, string? version, string? manifest)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gamePath) || string.IsNullOrWhiteSpace(version)) return;
            var body = DeployedBodyFile(gamePath);
            if (body is null) return;
            var fi = new FileInfo(body);
            File.WriteAllText(Path.Combine(gamePath, DeployRecordName),
                string.Join('|', Path.GetFileName(body), fi.Length.ToString(),
                    fi.LastWriteTimeUtc.Ticks.ToString(), version.Trim(), manifest?.Trim() ?? ""));
        }
        catch (Exception ex) { AppLog.Warn("Updates", "写入版本部署记录失败: " + ex.Message); }
    }

    private static string? ReadDeployedRecord(string? gamePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gamePath)) return null;
            var f = Path.Combine(gamePath, DeployRecordName);
            if (!File.Exists(f)) return null;
            var seg = File.ReadAllText(f).Split('|');
            if (seg.Length < 4) return null;
            var body = Path.Combine(gamePath, seg[0]);
            if (!File.Exists(body)) return null;
            var fi = new FileInfo(body);
            if (fi.Length != long.Parse(seg[1]) || fi.LastWriteTimeUtc.Ticks != long.Parse(seg[2])) return null;
            return seg[3].Trim();
        }
        catch { return null; }
    }

    private static string? DeployedBodyFile(string gamePath)
    {
        foreach (var n in new[] { "Stardew Valley.dll", "Stardew Valley.exe" })
        {
            var p = Path.Combine(gamePath, n);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>静态版本读取（启动链路等拿不到服务实例的场景用）。版本在托管主程序集
    /// Stardew Valley.dll 的文件版本里（如 1.6.15.24356）——exe 只是 1.6 起的原生启动壳
    /// （实测 4.5.1.0），dll 读不到才退回 exe。</summary>
    public static string? ReadLocalGameVersion(string? gamePath)
    {
        foreach (var name in new[] { "Stardew Valley.dll", "Stardew Valley.exe" })
        {
            try
            {
                if (string.IsNullOrWhiteSpace(gamePath)) return null;
                var file = Path.Combine(gamePath, name);
                if (!File.Exists(file)) continue;

                var fvi = FileVersionInfo.GetVersionInfo(file);
                var raw = fvi.FileVersion ?? fvi.ProductVersion;
                if (string.IsNullOrWhiteSpace(raw)) continue;

                // "1.6.15.24356" → "1.6.15"
                var parts = raw.Split('.');
                return parts.Length >= 3 ? string.Join('.', parts.Take(3)) : raw;
            }
            catch
            {
                // 单个文件读失败（占用/权限）继续试下一个
            }
        }

        return null;
    }

    // ------------------------------------------------------------------
    // SMAPI update check (GitHub Releases API)
    // ------------------------------------------------------------------
    public async Task<SmapiUpdateInfo> CheckSmapiAsync(string? installedVersion, bool force = false)
    {
        // 缓存策略：同一个本地版本或 20 分钟内的成功结果直接用；
        // 上次失败（GitHub 限流/断网）不永久缓存，用户点「↻ 检查更新」时重试。
        // v1.08：force = 手动刷新，绕过 5 分钟缓存强制重查。
        if (!force
            && _cached is not null
            && _cached.Error is null
            && _cached.ForVersion == installedVersion
            && DateTime.Now - _cachedAt < TimeSpan.FromMinutes(5))
            return _cached;

        try
        {
            // v1.1.8：latest 查询同样走国内镜像（与 CheckModGitHubAsync / 历史 tag 一致）
            string? json = null;
            Exception? lastApiEx = null;
            foreach (var api in GithubUrls("https://api.github.com/repos/Pathoschild/SMAPI/releases/latest"))
            {
                try { json = await Http.GetStringAsync(api); break; }
                catch (Exception ex) { lastApiEx = ex; }
            }
            if (json is null) throw lastApiEx ?? new InvalidOperationException("GitHub API 不可达");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var pageUrl = root.TryGetProperty("html_url", out var h)
                ? h.GetString() ?? "https://smapi.io" : "https://smapi.io";

            // 挑安装包资源：SMAPI 4.5+ 会同时上传
            //   SMAPI-x.y.z-installer.zip                （正常单层 zip，取这个）
            //   SMAPI-x.y.z-installer-double-zipped.zip  （外层再套一层，供某些浏览器保护策略使用）
            // 之前的循环碰到 "double-zipped" 会先命中，导致解压出来还是 zip，
            // 里面找不到 SMAPI.Installer.exe。这里显式跳过 double-zipped / for-developers。
            string? zipUrl = null;
            var bestScore = -1;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var score = ScoreInstallerAsset(name);
                    if (score <= bestScore) continue;
                    if (asset.TryGetProperty("browser_download_url", out var d) && d.GetString() is { } url)
                    {
                        zipUrl = url;
                        bestScore = score;
                    }
                }
            }

            // 修复点：ProbeSmapiVersion 抽不到版本号时会返回字符串 "installed"，
            // Version.TryParse("installed") 失败 → hasUpdate 永远为 false，
            // 界面上会错误地显示「已是最新」。作为兵底，当本地版本无法解析
            // 但本地已装 SMAPI（installedVersion 不为 null）时，只要获取到了
            // 远端 tag 就提示一下有新版本可用，而不是默默当作已最新。
            var installedOk = Version.TryParse(Normalize(installedVersion), out _);
            var hasUpdate = CompareSmapiVersion(installedVersion, tag);

            _cached = new SmapiUpdateInfo(
                installedVersion, string.IsNullOrEmpty(tag) ? null : tag,
                hasUpdate, installedOk, zipUrl, pageUrl, null);
            _cachedAt = DateTime.Now;
        }
        catch (Exception ex)
        {
            // v0.38.0：API 被限流（403 rate limit）时回落到 HTML release 页解析 ——
            // /releases/latest 的 302 重定向 URL 里带 tag，不受 API 配额（60次/小时/IP）限制。
            var fallback = await TryCheckSmapiViaHtmlAsync(installedVersion);
            if (fallback is not null)
            {
                _cached = fallback;
                _cachedAt = DateTime.Now;
                return _cached;
            }

            // Offline / rate-limited — 不缓存时间，下次检查时重试。
            var friendly = ex.Message.Contains("403") || ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                ? "GitHub API 暂时限流（每小时 60 次），稍后再试或点「检查更新」重试"
                : ex.Message;
            _cached = new SmapiUpdateInfo(
                installedVersion, null, false, false, null, "https://smapi.io", friendly);
            _cachedAt = DateTime.MinValue;
        }
        return _cached;
    }

    /// <summary>比较本地 SMAPI 版本与远端 tag 判断是否有新版本。
    /// 任一侧解析失败时：本地无法解析但确实装了 SMAPI → 保守提示有新版；其余情况视为无更新。</summary>
    private static bool CompareSmapiVersion(string? installedVersion, string tag)
    {
        var installedOk = Version.TryParse(Normalize(installedVersion), out var installed);
        var remoteOk = Version.TryParse(Normalize(tag), out var latest);
        if (installedOk && remoteOk) return latest > installed;
        return !installedOk && remoteOk && !string.IsNullOrWhiteSpace(installedVersion);
    }

    /// <summary>
    /// v0.38.0：GitHub API 限流时的回落通道 —— 请求 releases/latest（HTML），
    /// 从重定向后的最终 URL 里抠出 tag（如 /releases/tag/4.5.2），
    /// 再按官方命名规则拼 installer zip 的下载地址。
    /// HTML 页面走另一套配额，几乎不会被普通使用打满。
    /// </summary>
    private async Task<SmapiUpdateInfo?> TryCheckSmapiViaHtmlAsync(string? installedVersion)
    {
        try
        {
            // HttpClient 默认跟随重定向；最终 RequestUri 形如
            // https://github.com/Pathoschild/SMAPI/releases/tag/4.5.2
            // v1.08：直连失败（国内 github.com 443 不通）时依次走镜像；
            // 镜像回传的最终 URL 同样带 /releases/tag/，tag 解析逻辑不变。
            HttpResponseMessage? resp = null;
            foreach (var url in GithubUrls("https://github.com/Pathoschild/SMAPI/releases/latest"))
            {
                try
                {
                    resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                    if (resp.IsSuccessStatusCode) break;
                    resp.Dispose(); resp = null;
                }
                catch { /* 换下一个通道 */ }
            }
            if (resp is null) return null;

            var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? "";
            const string marker = "/releases/tag/";
            var idx = finalUrl.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return null;
            var tag = finalUrl[(idx + marker.Length)..].TrimEnd('/').Split('?')[0];
            if (string.IsNullOrWhiteSpace(tag)) return null;

            var zipUrl = $"https://github.com/Pathoschild/SMAPI/releases/download/{tag}/SMAPI-{tag}-installer.zip";
            var pageUrl = $"https://github.com/Pathoschild/SMAPI/releases/tag/{tag}";

            var installedOk = Version.TryParse(Normalize(installedVersion), out _);
            var hasUpdate = CompareSmapiVersion(installedVersion, tag);

            return new SmapiUpdateInfo(
                installedVersion, tag, hasUpdate, installedOk, zipUrl, pageUrl, null);
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Run the official SMAPI installer — 全自动无人值守
    // ------------------------------------------------------------------
    /// <summary>
    /// 下载官方安装包，后台静默执行 SMAPI.Installer.exe
    /// （--install --no-prompt --game-path），全程不弹任何窗口、不跳浏览器。
    /// 返回 null 表示成功；否则返回错误消息。
    /// SMAPI 安装器原生支持无人值守参数（见 InteractiveInstaller 源码）：
    ///   --no-prompt   禁用交互询问
    ///   --install     执行安装/更新
    ///   --game-path   指定游戏目录（跳过自动探测）
    /// </summary>
    public async Task<string?> RunSmapiInstallerAsync(
        SmapiUpdateInfo info, string? gamePath,
        IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        string? temp = null;
        try
        {
            SweepSmapiTemps();   // 清掉历次安装遗留的解压目录（只留最新一份）
            GuardSmapiVersionMatchesGame(gamePath, info.LatestVersion);
            if (info.InstallerZipUrl is null)
                throw new InvalidOperationException("没找到 SMAPI 安装包下载地址");
            if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
                throw new InvalidOperationException("还没设置游戏目录 —— 先到「设置」页选择");
            ct.ThrowIfCancellationRequested();

            // v0.2.1：走统一缓存目录（默认仍在 LocalAppData）。不直接放 %TEMP%：
            // Defender 对 %TEMP% 里的 .NET 运行时 DLL 扫描更激进，经常写入瞬间挂锁。
            // 目录名带时间戳避免撞旧缓存。
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            temp = Path.Combine(
                StoragePaths.SmapiInstallerDir,
                $"{info.LatestVersion ?? "latest"}-{stamp}");
            Directory.CreateDirectory(temp);

            // 下载前先备份玩家 Mods，这样下载/解压/安装的任何一步出问题
            // 都不影响原始目录；安装完成后会把备份合并回 Mods 并删掉临时备份。
            // v1.08：备份是几百 MB 的整目录复制，必须离开 UI 线程（旧版同步复制，
            // 点下下载整个界面冻结到备份结束）；进度条同时提示备份阶段。
            progress?.Report(new InstallProgress("正在备份现有 Mods 目录…", 0, 0));
            var modsBackup = await Task.Run(() => BackupMods(gamePath), ct);
            if (modsBackup is not null)
                progress?.Report(new InstallProgress("已备份现有 Mods 目录，开始下载 SMAPI…", 0, 0));

            progress?.Report(new InstallProgress("正在下载 SMAPI 安装包…"));
            progress?.Report(new InstallProgress("连接下载服务器…", 0, 0));
            // v1.1.7：半截包放稳定路径 —— 暂停后继续可续传，不再每次新建时间戳目录导致断点丢失
            var zip = Path.Combine(StoragePaths.SmapiInstallerDir,
                $"smapi-dl-{(info.LatestVersion ?? "latest").Replace('/', '-')}.zip");

            // v1.5：本机已有同名安装包 → 跳过网络下载。有该版本抽屉就存在抽屉里（跟版本走），
            // 没有就存在通用安装包缓存的 keep 子目录（见 GetSmapiInstallerCacheDir）。
            var ver = GetGameVersion(gamePath);
            var cachedDir = DepotDownloaderService.GetSmapiInstallerCacheDir(ver);
            var cachedZip = Path.Combine(cachedDir, Path.GetFileName(new Uri(info.InstallerZipUrl).LocalPath));
            if (File.Exists(cachedZip) && new FileInfo(cachedZip).Length > 1024)
            {
                progress?.Report(new InstallProgress("使用本机已缓存的 SMAPI 安装包…", 50, 0));
                File.Copy(cachedZip, zip, overwrite: true);
            }
            else
            {
                await DownloadToFileAsync(info.InstallerZipUrl, zip, progress, ct);
                try
                {
                    Directory.CreateDirectory(cachedDir);
                    File.Copy(zip, cachedZip, overwrite: true);
                }
                catch { /* 缓存失败不影响安装 */ }
            }
            ct.ThrowIfCancellationRequested();

            var err = await InstallSmapiZipAsync(zip, gamePath, progress, info.LatestVersion);
            if (err is null)
            {
                // v1.6.7：装完校验游戏版本没变 —— 安装期间被切版本的话，这份 SMAPI 装进了
                // 别的版本目录，绝不能写进共享池/版本缓存污染其它版本。
                var verAfter = GetGameVersion(gamePath);
                if (!string.Equals(verAfter, ver, StringComparison.OrdinalIgnoreCase))
                    return $"SMAPI {info.LatestVersion ?? ""} 安装完成，但期间游戏版本已从 {ver} 切到 {verAfter}，" +
                           "当前目录里的 SMAPI 与游戏不匹配 —— 请重新执行一次 SMAPI 安装";
                // 装完把 SMAPI 产物写进当前版本 staging，下次切回免重装
                try { DepotDownloaderService.CacheSmapiForCurrentGame(gamePath); } catch { }
                try { if (File.Exists(zip)) File.Delete(zip); } catch { }
            }
            return err;
        }
        catch (OperationCanceledException)
        {
            // 暂停：稳定路径上的半截包 smapi-dl-*.zip 保留供续传（临时解压目录由 finally 清）
            throw;
        }
        catch (Exception ex)
        {
            // modsBackup 仍保留，方便用户手动恢复；临时目录由 finally 清
            return "SMAPI 自动安装失败：" + ex.Message;
        }
        finally { CleanSmapiTemp(temp); }
    }

    /// <summary>删掉本次安装解压出来的临时目录。成功路径以前根本不删（本机就是这么堆出
    /// 11 个时间戳目录 / 218 MB 的），现在成功/失败/取消三条出口都走这里；
    /// 删不动（Defender 挂锁、被占用）必须留一行 WARN —— 静默失败等于只进不出且没人看得见。</summary>
    private static void CleanSmapiTemp(string? temp)
    {
        if (string.IsNullOrWhiteSpace(temp) || !Directory.Exists(temp)) return;
        try { Directory.Delete(temp, true); }
        catch (Exception ex)
        {
            AppLog.Warn("Smapi", $"SMAPI 安装临时目录没能删掉（下次安装前会再扫一次）：{temp} — {ex.Message}");
        }
    }

    /// <summary>清扫 smapi-installer 下遗留的「&lt;版本&gt;-&lt;时间戳&gt;」解压目录：没有任何代码会再读它们，
    /// 只保留 mtime 最新的一份（可能正被另一个并发安装用着）。
    /// ⚠ 半截续传包 smapi-dl-*.zip 不在此列 —— 暂停/继续要靠它，绝不能顺手清掉。</summary>
    private static void SweepSmapiTemps()
    {
        try
        {
            var root = StoragePaths.SmapiInstallerDir;
            if (!Directory.Exists(root)) return;
            var stale = Directory.EnumerateDirectories(root)
                .Where(d => System.Text.RegularExpressions.Regex.IsMatch(
                    Path.GetFileName(d), @"-\d{8}_\d{6}$"))
                .OrderByDescending(d => { try { return Directory.GetLastWriteTimeUtc(d); } catch { return DateTime.MinValue; } })
                .Skip(1)
                .ToList();
            foreach (var d in stale)
            {
                try { Directory.Delete(d, true); }
                catch (Exception ex) { AppLog.Warn("Smapi", "清理遗留的 SMAPI 解压目录失败: " + d + " — " + ex.Message); }
            }
            if (stale.Count > 0)
                AppLog.Warn("Smapi", $"已清理 {stale.Count} 个遗留的 SMAPI 解压临时目录");
        }
        catch { }
    }

    /// <summary>
    /// v1.3.2：从【已下载好的 SMAPI 官方安装器 zip】静默安装 —— 供两条来源复用：
    /// ① GitHub 最新版（RunSmapiInstallerAsync 下载后调这里）；
    /// ② Nexus 上用户手动挑选的任意历史版本（nxm 回流，如 SMAPI 4.5.1 —— Nexus 的
    ///    SMAPI 包本身就是官方安装器，内部结构与 GitHub release zip 相同）。
    /// 完成 备份 Mods → 解压 → install.dat 手动安装 → 恢复 Mods 全流程。
    /// 返回 null 表示成功；versionLabel 仅用于进度文案与临时目录命名（可为 null）。
    /// </summary>
    /// <summary>SMAPI 安装的底层闸门。所有入口（Mods 页两个按钮、.nxm 通道、切换后补装、
    /// 以后新增的任何入口）最终都汇到 RunSmapiInstallerAsync / InstallSmapiZipAsync，
    /// 校验放这一层才堵得住 —— 1.01 的目录里被装进 1.6 专用的 SMAPI 4.5.2，
    /// 就是因为判断只写在 UI 入口、底层不看。</summary>
    private static void GuardSmapiVersionMatchesGame(string? gamePath, string? versionToInstall)
    {
        var gameVer = ReadLocalGameVersion(gamePath);
        if (string.IsNullOrWhiteSpace(gameVer))
        {
            AppLog.Warn("SMAPI", "读不出当前游戏版本，跳过 SMAPI 兼容性闸门（不阻塞安装）");
            return;
        }
        var tag = RecommendSmapiTag(gameVer);
        if (tag is null)
            throw new InvalidOperationException(
                $"游戏 {gameVer} 没有适配的 SMAPI，拒绝安装 " +
                (string.IsNullOrWhiteSpace(versionToInstall) ? "该版本" : versionToInstall) +
                " —— 装上会在启动时崩（跨版本残留）。想玩 mod 请先切到 1.2.30 及以上版本。");

        var got = (versionToInstall ?? "").Trim().TrimStart('v', 'V');
        if (got.Length == 0) return;                                  // 调用方没报版本 → 不拦
        if (tag.Trim().Equals("latest", StringComparison.OrdinalIgnoreCase)) return;  // 1.6+ 就该用最新
        if (got.StartsWith(tag.Trim(), StringComparison.OrdinalIgnoreCase)) return;   // 3.7.3 ≈ 3.7.3.x
        throw new InvalidOperationException(
            $"要装的 SMAPI {got} 与游戏 {gameVer} 不匹配（该版本该用 {tag}）—— 已拒绝安装。");
    }

    public async Task<string?> InstallSmapiZipAsync(
        string zip, string? gamePath,
        IProgress<InstallProgress>? progress = null, string? versionLabel = null)
    {
        string? temp = null;
        string? modsBackup = null;
        try
        {
            if (!File.Exists(zip))
                throw new InvalidOperationException("SMAPI 安装包不存在：" + zip);
            if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
                throw new InvalidOperationException("还没设置游戏目录 —— 先到「设置」页选择");
            GuardSmapiVersionMatchesGame(gamePath, versionLabel);

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            temp = Path.Combine(
                StoragePaths.SmapiInstallerDir,
                $"{versionLabel ?? "manual"}-{stamp}");
            Directory.CreateDirectory(temp);

            progress?.Report(new InstallProgress("正在备份现有 Mods 目录…", 0, 0));
            modsBackup = await Task.Run(() => BackupMods(gamePath));
            if (modsBackup is not null)
                progress?.Report(new InstallProgress("已备份现有 Mods 目录…", 0, 0));

            progress?.Report(new InstallProgress("正在解压安装包…"));
            await Task.Run(() => ExtractWithRetryAsync(zip, temp, progress));   // v1.08：离开 UI 线程

            // 兜底：如果这个 zip 是「double-zipped」外壳，解出来还是 zip，自动再解一层。
            var innerZip = Directory
                .GetFiles(temp, "*.zip", SearchOption.AllDirectories)
                .FirstOrDefault(f => !string.Equals(f, zip, StringComparison.OrdinalIgnoreCase));
            if (innerZip is not null)
            {
                progress?.Report(new InstallProgress("检测到内层压缩包，正在再次解压…"));
                await Task.Run(() => ExtractWithRetryAsync(innerZip, temp, progress));   // v1.08
            }

            // SMAPI 4.5.x 的官方安装器有个已知问题：它在 --install --no-prompt 模式
            // 下仍会调用 Console.Clear()。如果安装器进程 stdout/输入句柄不是真实控制台
            // （JuniGrid 做 WPF 后台进程时正是这种情况）， Console.Clear() 会抛
            // "句柄无效" IOException，安装直接失败。反复弹黑窗也仍可能失败。
            //
            // 所以这里改为按 SMAPI README 的手动安装步骤，直接从官方安装包里的
            // internal/windows 目录安装：官方 README 明确支持把 install.dat 解压复制到
            // 游戏目录，这完全无需 console，也是启动器更可靠的做法。
            // v1.1.8：2.x / 3.0.x / 3.7.x 与 4.5.2 同一条管线 —— 统一递归找 install.dat。
            // v1.1.9：SMAPI 2.5 没有 install.dat —— internal/Windows/ 本身就是解开的载荷。
            progress?.Report(new InstallProgress("正在后台安装 SMAPI（手动安装官方文件）…", 90));

            string? dat = null;
            // ① 新结构：SMAPI {ver} installer/internal/windows/install.dat
            if (versionLabel is not null)
            {
                var expected = Path.Combine(temp, "SMAPI " + versionLabel + " installer", "internal", "windows", "install.dat");
                if (File.Exists(expected)) dat = expected;
            }
            // ② 老结构：internal/windows-install.dat（3.9.x / 2.11 / 3.0.1 等）
            if (dat is null)
            {
                var oldDat = Directory
                    .GetFiles(temp, "*-install.dat", SearchOption.AllDirectories)
                    .FirstOrDefault(f => f.EndsWith("windows-install.dat", StringComparison.OrdinalIgnoreCase));
                if (oldDat is not null) dat = oldDat;
            }
            // ③ 兜底：任意路径下的 install.dat，或大于 500KB 的 .dat（改名安装载荷）
            if (dat is null)
            {
                dat = Directory.GetFiles(temp, "install.dat", SearchOption.AllDirectories)
                    .OrderByDescending(f => f.Length)
                    .FirstOrDefault()
                    ?? Directory.GetFiles(temp, "*.dat", SearchOption.AllDirectories)
                        .Select(f => new FileInfo(f))
                        .Where(f => f.Length > 500 * 1024)
                        .OrderByDescending(f => f.Length)
                        .Select(f => f.FullName)
                        .FirstOrDefault();
            }
            // ④ SMAPI 2.5 等：没有 .dat，internal/Windows 已是解开的 StardewModdingAPI + Mods
            string? preExtractedWindows = null;
            if (dat is null)
            {
                preExtractedWindows = Directory
                    .EnumerateDirectories(temp, "*", SearchOption.AllDirectories)
                    .Where(d => string.Equals(Path.GetFileName(d), "Windows", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(Path.GetFileName(d), "windows", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault(d => File.Exists(Path.Combine(d, "StardewModdingAPI.exe")));
            }
            if (dat is null && preExtractedWindows is null)
                throw new InvalidOperationException(
                    "解压后没找到 SMAPI 安装载荷（install.dat / windows-install.dat / internal/Windows 均不存在）");

            string payloadRoot;
            if (dat is not null)
            {
                var extracted = Path.Combine(temp, "smapi-files");
                Directory.CreateDirectory(extracted);
                progress?.Report(new InstallProgress("正在解压安装内容…", 95));
                await Task.Run(() => ExtractWithRetryAsync(dat, extracted, progress));   // v1.08
                payloadRoot = FindStardewApiRoot(extracted)
                    ?? throw new InvalidOperationException("SMAPI 安装包内没有 StardewModdingAPI.exe（安装包异常）");
            }
            else
            {
                // 2.5 载荷已就位，无需再解压
                progress?.Report(new InstallProgress("正在写入 SMAPI 文件…", 95));
                payloadRoot = preExtractedWindows!;
            }

            // 先清掉旧 SMAPI 文件再复制新文件（避免文件锁定/残留版本文件）。
            // v1.08：复制/清理都是上百 MB 的磁盘工作，不能冻 UI。
            await Task.Run(() => CopySmapiBundle(payloadRoot, gamePath));

            // 更新结束后保留游戏自带的 deps.json、runtimeconfig.json，SMAPI 的
            // StardewModdingAPI.deps.json 需要在游戏主文件基础上生成/覆盖。
            var gameDeps = Path.Combine(gamePath, "Stardew Valley.deps.json");
            if (File.Exists(gameDeps))
                File.Copy(gameDeps, Path.Combine(gamePath, "StardewModdingAPI.deps.json"), true);

            // 安装完成后再把备份的 Mods 复制回去，保证用户 mod 一个不丢；
            // 官方 install.dat 里也带 SMAPI 自带 ConsoleCommands/SaveBackup 这类
            // 默认 mod，所以先恢复备份，再把缺失的默认 mod 补回，绝不整目录覆盖。
            if (modsBackup is not null)
            {
                progress?.Report(new InstallProgress("正在恢复 Mod 文件夹…", 100));
                await Task.Run(() => RestoreMods(modsBackup, Path.Combine(gamePath, "Mods")));   // v1.08：离开 UI 线程
            }
            CopyBuiltinMods(Path.Combine(payloadRoot, "Mods"), Path.Combine(gamePath, "Mods"));

            // 然后删除这次更新产生的临时备份目录。
            if (modsBackup is not null)
            {
                await Task.Run(() => TryDeleteBackup(modsBackup));   // v1.08：删除备份也是大 IO
            }

            progress?.Report(new InstallProgress($"SMAPI {versionLabel ?? ""} 安装完成".TrimEnd(), 100, 0));
            Invalidate();
            return null;
        }
        catch (Exception ex)
        {
            // modsBackup 仍保留，方便用户手动恢复；临时目录由 finally 清
            return "SMAPI 自动安装失败：" + ex.Message;
        }
        finally { CleanSmapiTemp(temp); }
    }

    /// <summary>从解压目录里定位真正含 StardewModdingAPI.exe 的根（install.dat 解出来可能多包一层）。</summary>
    private static string? FindStardewApiRoot(string dir)
    {
        if (File.Exists(Path.Combine(dir, "StardewModdingAPI.exe"))) return dir;
        return Directory
            .EnumerateDirectories(dir, "*", SearchOption.AllDirectories)
            .FirstOrDefault(d => File.Exists(Path.Combine(d, "StardewModdingAPI.exe")));
    }

    /// <summary>
    /// SMAPI 安装流程的进度消息。percent/speed 可选：只有 DownloadToFileAsync
    /// 报告的阶段会带数值，解压/安装阶段只更新文字和阶段百分比。
    /// </summary>
    public sealed record InstallProgress(
        string Message,
        double? Percent = null,
        double? SpeedMBps = null);


    // 大文件下载用单独的长超时客户端（版本检查的 Http 只有 12 秒超时，
    // 会把 SMAPI 安装包下载掐断）。
    private static readonly HttpClient DownloadHttp = CreateDownloadClient();

    private static HttpClient CreateDownloadClient()
    {
        var h = new HttpClient();
        h.DefaultRequestHeaders.UserAgent.ParseAdd("JuniGrid-Launcher");
        h.Timeout = TimeSpan.FromMinutes(5);
        return h;
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
                progress?.Report(new InstallProgress($"解压被拦截，正在重试（{attempt}/{maxAttempts - 1}）…"));
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
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception __ex) { AppLog.Warn("UpdateService", __ex.Message); }
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

public sealed record GitHubModRelease(string Tag, string? ZipUrl);

public sealed record SmapiUpdateInfo(
    string? ForVersion,
    string? LatestVersion,
    bool HasUpdate,
    bool InstalledParsed,
    string? InstallerZipUrl,
    string ReleasePageUrl,
    string? Error);
