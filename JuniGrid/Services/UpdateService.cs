using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace JuniGrid.Services;

/// <summary>
/// Version probing + SMAPI update checks.
///  - Game version comes from Stardew Valley.exe file metadata (offline).
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

    // ------------------------------------------------------------------
    // Game version (local, offline)
    // ------------------------------------------------------------------
    public string? GetGameVersion(string gamePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gamePath)) return null;
            var exe = Path.Combine(gamePath, "Stardew Valley.exe");
            if (!File.Exists(exe)) return null;

            var raw = FileVersionInfo.GetVersionInfo(exe).FileVersion
                   ?? FileVersionInfo.GetVersionInfo(exe).ProductVersion;
            if (string.IsNullOrWhiteSpace(raw)) return null;

            // "1.6.15.24356" → "1.6.15"
            var parts = raw.Split('.');
            return parts.Length >= 3 ? string.Join('.', parts.Take(3)) : raw;
        }
        catch
        {
            return null;
        }
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
            var json = await Http.GetStringAsync(
                "https://api.github.com/repos/Pathoschild/SMAPI/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var pageUrl = root.TryGetProperty("html_url", out var h)
                ? h.GetString() ?? "https://smapi.io" : "https://smapi.io";

            // 挑安装包资源：SMAPI 4.5+ 会同时上传
            //   SMAPI-x.y.z-installer.zip                （正常单层 zip，取这个）
            //   SMAPI-x.y.z-installer-double-zipped.zip  （外层再套一层，供某些浏览器保护策略使用）
            // 之前的循环碰到 "double-zipped" 会先命中，导致解压出来还是 zip，
            // 里面找不到 SMAPI.Installer.exe。这里显式跳过 double-zipped。
            string? zipUrl = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (!name.Contains("installer", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.Contains("double-zipped", StringComparison.OrdinalIgnoreCase)) continue;
                    zipUrl = asset.TryGetProperty("browser_download_url", out var d)
                        ? d.GetString() : null;
                    break;
                }
            }

            // 修复点：ProbeSmapiVersion 抽不到版本号时会返回字符串 "installed"，
            // Version.TryParse("installed") 失败 → hasUpdate 永远为 false，
            // 界面上会错误地显示「已是最新」。作为兵底，当本地版本无法解析
            // 但本地已装 SMAPI（installedVersion 不为 null）时，只要获取到了
            // 远端 tag 就提示一下有新版本可用，而不是默默当作已最新。
            var installedOk = Version.TryParse(Normalize(installedVersion), out var installed);
            var remoteOk = Version.TryParse(Normalize(tag), out var latest);
            bool hasUpdate;
            if (installedOk && remoteOk)
                hasUpdate = latest > installed;
            else if (!installedOk && remoteOk && !string.IsNullOrWhiteSpace(installedVersion))
                hasUpdate = true;   // 本地版本无法解析但确实装了，保守提示有新版
            else
                hasUpdate = false;

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

            var installedOk = Version.TryParse(Normalize(installedVersion), out var installed);
            var remoteOk = Version.TryParse(Normalize(tag), out var latest);
            bool hasUpdate;
            if (installedOk && remoteOk)
                hasUpdate = latest > installed;
            else if (!installedOk && remoteOk && !string.IsNullOrWhiteSpace(installedVersion))
                hasUpdate = true;
            else
                hasUpdate = false;

            return new SmapiUpdateInfo(
                installedVersion, tag, hasUpdate, installedOk, zipUrl, pageUrl, null);
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Game latest version (remote, Stardew Valley Wiki) — 启动时静默检测
    // ------------------------------------------------------------------
    /// <summary>
    /// 拉 Stardew Valley Wiki 的当前版本号，作为“游戏本体最新版”的来源。
    /// 失败（断网/页面改版）静默返回 null，界面就不显示“有新版本”的提示。
    /// 返回纯版本号字符串，如 "1.6.15"。
    /// </summary>
    public async Task<string?> GetLatestGameVersionAsync()
    {
        try
        {
            var html = await Http.GetStringAsync(
                "https://stardewvalleywiki.com/Main_Page");

            // 优先精确定位游戏本体当前版：Wiki 侧栏的 Version History 链接文本
            // 就是当前游戏版本，如 <a ... title="Version History">1.6.15</a>。
            // 绝不能对页面里所有 1.x.y 取「最大」—— 页面还混着很多其它 mod/数字
            // （如 1.35.1），会误判成"最新版比本地还高"。
            var precise = System.Text.RegularExpressions.Regex.Match(
                html, @"title=""Version History""[^>]*>\s*([0-9]+\.[0-9]+\.[0-9]+)");
            if (precise.Success) return precise.Groups[1].Value;

            // 兜底：页面里第一个 1.x.y（也倾向是最新版），失败返回 null 静默
            var first = System.Text.RegularExpressions.Regex.Match(html, @"1\.\d+\.\d+");
            return first.Success ? first.Value : null;
        }
        catch
        {
            return null;   // 离线/被墙/页面改版 → 静默不显示
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
        IProgress<InstallProgress>? progress = null)
    {
        try
        {
            if (info.InstallerZipUrl is null)
                return "没找到 SMAPI 安装包下载地址";
            if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
                return "还没设置游戏目录 —— 先到「设置」页选择";

            // v0.2.1：走统一缓存目录（默认仍在 LocalAppData）。不直接放 %TEMP%：
            // Defender 对 %TEMP% 里的 .NET 运行时 DLL 扫描更激进，经常写入瞬间挂锁。
            // 目录名带时间戳避免撞旧缓存。
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var temp = Path.Combine(
                StoragePaths.SmapiInstallerDir,
                $"{info.LatestVersion ?? "latest"}-{stamp}");
            Directory.CreateDirectory(temp);

            // 下载前先备份玩家 Mods，这样下载/解压/安装的任何一步出问题
            // 都不影响原始目录；安装完成后会把备份合并回 Mods 并删掉临时备份。
            // v1.08：备份是几百 MB 的整目录复制，必须离开 UI 线程（旧版同步复制，
            // 点下下载整个界面冻结到备份结束）；进度条同时提示备份阶段。
            progress?.Report(new InstallProgress("正在备份现有 Mods 目录…", 0, 0));
            var modsBackup = await Task.Run(() => BackupMods(gamePath));
            if (modsBackup is not null)
                progress?.Report(new InstallProgress("已备份现有 Mods 目录，开始下载 SMAPI…", 0, 0));

            progress?.Report(new InstallProgress("正在下载 SMAPI 安装包…"));
            progress?.Report(new InstallProgress("连接下载服务器…", 0, 0));
            var zip = Path.Combine(temp, "smapi-installer.zip");
            await DownloadToFileAsync(info.InstallerZipUrl, zip, progress);

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
            progress?.Report(new InstallProgress("正在后台安装 SMAPI（手动安装官方文件）…", 90));

            var windowsFiles = Path.Combine(temp, "SMAPI " + info.LatestVersion + " installer", "internal", "windows");
            if (!Directory.Exists(windowsFiles))
            {
                // 结构变化兜底：递归找 install.dat / StardewModdingAPI.exe
                var installDat = Directory
                    .GetFiles(temp, "install.dat", SearchOption.AllDirectories)
                    .FirstOrDefault(f => f.Contains(Path.Combine("windows", "install.dat"), StringComparison.OrdinalIgnoreCase));
                if (installDat is not null)
                    windowsFiles = Path.GetDirectoryName(installDat)!;
            }
            // install.dat 其实是个 zip（只是改了扩展名防误解压）。
            // 官方手动安装流程：
            //   1. 解压 install.dat 到临时目录
            //   2. 把解压出来的文件复制覆盖到游戏目录
            //   3. 复制 "Stardew Valley.deps.json" → "StardewModdingAPI.deps.json"
            var dat = Path.Combine(windowsFiles, "install.dat");
            if (!File.Exists(dat))
                return "解压后没找到 SMAPI 安装包（internal/windows/install.dat 不存在，安装包结构变了？）";

            var extracted = Path.Combine(temp, "smapi-files");
            Directory.CreateDirectory(extracted);
            progress?.Report(new InstallProgress("正在解压安装内容…", 95));
            await Task.Run(() => ExtractWithRetryAsync(dat, extracted, progress));   // v1.08

            if (!File.Exists(Path.Combine(extracted, "StardewModdingAPI.exe")))
                return "SMAPI 安装包内没有 StardewModdingAPI.exe（安装包异常）";

            // 先清掉旧 SMAPI 文件再复制新文件（避免文件锁定/残留版本文件）。
            // v1.08：复制/清理都是上百 MB 的磁盘工作，不能冻 UI。
            await Task.Run(() => CopySmapiBundle(extracted, gamePath));

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
            CopyBuiltinMods(Path.Combine(extracted, "Mods"), Path.Combine(gamePath, "Mods"));

            // 然后删除这次更新产生的临时备份目录。
            if (modsBackup is not null)
            {
                await Task.Run(() => TryDeleteBackup(modsBackup));   // v1.08：删除备份也是大 IO
            }

            progress?.Report(new InstallProgress($"SMAPI {info.LatestVersion} 安装完成", 100, 0));
            Invalidate();
            return null;
        }
        catch (Exception ex)
        {
            // 失败时保留备份，尽量不删，方便下次进来手动恢复。
            return "SMAPI 自动安装失败：" + ex.Message;
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

    /// <summary>把字节数显示成可读的 KB/MB/GB 文本。</summary>
    private static string FormatBytes(long bytes)
    {
        double value = bytes;
        string[] units = { "B", "KB", "MB", "GB" };
        int i = 0;
        while (value >= 1024 && i < units.Length - 1)
        {
            value /= 1024;
            i++;
        }
        return value.ToString(i == 0 ? "F0" : "F1") + " " + units[i];
    }

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
                CopyDirectoryContents(entry, dest);
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
            CopyDirectoryContents(entry, destMod);
        }
    }

    private static void CopyDirectoryContents(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.GetDirectories(src))
            CopyDirectoryContents(d, Path.Combine(dest, Path.GetFileName(d)));
    }

    /// <summary>
    /// 安装前把玩家 Mods 目录完整备份到 LocalAppData/JuniGrid/mods-backup，
    /// 只复制新增/变更文件，绝不删玩家原始 Mods。
    /// </summary>
    private static string? BackupMods(string gamePath)
    {
        var src = Path.Combine(gamePath, "Mods");
        if (!Directory.Exists(src)) return null;

        var backupRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JuniGrid", "mods-backup");
        Directory.CreateDirectory(backupRoot);

        // 每次安装前生成独立快照，不覆盖旧备份。
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var dest = Path.Combine(backupRoot, $"Mods-{stamp}");
        var i = 1;
        while (Directory.Exists(dest))
            dest = Path.Combine(backupRoot, $"Mods-{stamp}-{i++}");

        CopyDirectoryContents(src, dest);
        return dest;
    }

    /// <summary>
    /// 把更新前的备份合并回游戏 Mods：已存在的目录/文件保留当前版本，
    /// 缺的补回来。这个函数本身只复制，不删除任何用户文件。
    /// </summary>
    private static void RestoreMods(string backupPath, string destMods)
    {
        if (!Directory.Exists(backupPath)) return;
        CopyDirectoryContents(backupPath, destMods);
    }

    /// <summary>
    /// 删除这次更新产生的临时 Mods 备份目录。只删刚刚创建在
    /// LocalAppData/JuniGrid/mods-backup 下的快照，绝不碰游戏目录。
    /// </summary>
    private static void TryDeleteBackup(string backupPath)
    {
        var backupRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JuniGrid", "mods-backup");

        // 保险：只允许删除 mods-backup 子目录里的快照。
        if (string.IsNullOrWhiteSpace(backupPath)) return;
        var full = Path.GetFullPath(backupPath);
        var rootFull = Path.GetFullPath(backupRoot) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return;
        if (!Directory.Exists(full)) return;

        Directory.Delete(full, recursive: true);
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
