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
public sealed partial class UpdateService
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
    /// SMAPI 专属通道下详情页按钮的文案 —— 纯本地，不联网。
    /// 为什么要有它：详情页的"已装"判据是去 Mods\ 找文件夹，而 SMAPI 装在游戏根目录，
    /// 于是本机明明装着加载器，按钮却说「安装」。这里只说看得见的实事：
    /// ① 主程序在 → 报已装（能抽到版本号就带上，抽不到不编版本号）；
    /// ② 装的大版本与当前游戏版本推荐的大版本不符 → 报「&lt;版本&gt; 不适配本游戏」。
    /// ② 不写成「装 SMAPI x.y.z」那样的祈使句：详情页的按钮对已装的 SMAPI 一律禁用
    /// （换适配版是 Mod 页那条按游戏版本选包的通道干的活），灰按钮上写指令是骗人点击。
    /// </summary>
    public static string SmapiDetailLabel(bool installed, string? installedVersion, string? gameVersion)
    {
        if (!installed) return LocService.Tr("安装");
        var v = (installedVersion ?? "").Trim();
        var tag = RecommendSmapiTag(gameVersion);
        if (tag is not null && v.Length > 0 && char.IsDigit(v[0]))
        {
            if (v.Split('.')[0] != (tag == SmapiLatest ? "4" : tag.Split('.')[0]))
                return LocService.Tf("SMAPI {0} 不适配本游戏", v);
        }
        return v.Length > 0 ? LocService.Tf("SMAPI 已装 {0}", v) : LocService.Tr("SMAPI 已装");
    }

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
