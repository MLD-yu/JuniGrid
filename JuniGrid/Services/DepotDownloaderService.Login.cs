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
    // ------------------------------------------------------------------
    // v1.5：DepotDownloader 引擎 —— 扫码登录 + 令牌免密下载
    // （早期 steamcmd 通道因登录提示符对管道极不友好已移除）
    // 首次 -qr 扫码（Steam 手机 App 确认），令牌由它自行持久化；之后 -username
    // + -remember-password 静默复用，全程无密码。
    // ------------------------------------------------------------------

    /// <summary>
    /// 备份当前 Steam 账号下该游戏的 userdata（413150）到 JuniGrid 缓存。
    /// 成就记录在 Steam 服务器，这里只是本地快照；切换旧版本体后 Steamworks
    /// 行为可能变化，留一份便于出问题时人工比对/恢复。
    /// </summary>
    private static void BackupSteamUserData(string appId)
    {
        try
        {
            var backupRoot = Path.Combine(StoragePaths.LocalAppDataDir, "userdata-backup", appId);
            if (Directory.Exists(backupRoot))
            {
                DateTime latest = DateTime.MinValue;
                foreach (var d in Directory.EnumerateDirectories(backupRoot))
                {
                    var ts = Directory.GetLastWriteTimeUtc(d);
                    if (ts > latest) latest = ts;
                }
                PruneUserDataBackups(backupRoot, keep: 3);
                if (DateTime.UtcNow - latest < TimeSpan.FromHours(20)) return;
            }

            string? steamRoot = null;
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                var p = key?.GetValue("SteamPath") as string;
                if (!string.IsNullOrWhiteSpace(p)) steamRoot = p.Replace('/', Path.DirectorySeparatorChar);
            }
            catch { }
            if (steamRoot is null || !Directory.Exists(steamRoot)) return;

            var udRoot = Path.Combine(steamRoot, "userdata");
            if (!Directory.Exists(udRoot)) return;
            string? bestSid = null;
            var bestTs = DateTime.MinValue;
            foreach (var sidDir in Directory.EnumerateDirectories(udRoot))
            {
                var appDir = Path.Combine(sidDir, appId);
                if (!Directory.Exists(appDir)) continue;
                var ts = Directory.GetLastWriteTime(appDir);
                if (ts > bestTs) { bestTs = ts; bestSid = sidDir; }
            }
            if (bestSid is null) return;

            var dest = Path.Combine(backupRoot, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(dest);
            ParallelCopyDirectory(Path.Combine(bestSid, appId), dest, skipTrash: true);
            PruneUserDataBackups(backupRoot, keep: 3);
            AppLog.Warn("DepotDownloader", $"已备份 Steam userdata → {dest}");
        }
        catch (Exception ex)
        {
            AppLog.Warn("DepotDownloader", "userdata 备份异常: " + ex.Message);
        }
    }

    private static void PruneUserDataBackups(string backupRoot, int keep)
    {
        try
        {
            var dirs = Directory.EnumerateDirectories(backupRoot)
                .OrderByDescending(d => Directory.GetLastWriteTimeUtc(d))
                .ToList();
            for (var i = keep; i < dirs.Count; i++)
            {
                try { Directory.Delete(dirs[i], true); } catch { }
            }
        }
        catch { }
    }

    /// <summary>从 DD 输出里提取账号名（多种成功文案都试）。</summary>
    private static string? TryParseAccount(string trimmed, string? current)
    {
        if (!string.IsNullOrEmpty(current)) return current;
        var am = Regex.Match(trimmed, "Logging in user '(.+?)'");
        if (am.Success) return am.Groups[1].Value;
        // 3.x 扫码成功：Success! Next time you can login with -username xxx -remember-password
        am = Regex.Match(trimmed, @"-username\s+(\S+)");
        if (am.Success && !am.Groups[1].Value.StartsWith('-')) return am.Groups[1].Value;
        am = Regex.Match(trimmed, @"Logged in(?: as| via)?\s+'?([A-Za-z0-9_]+)'?", RegexOptions.IgnoreCase);
        if (am.Success) return am.Groups[1].Value;
        return current;
    }

    private static bool LooksLikeLoginSuccess(string trimmed)
    {
        return trimmed.Contains("Success!", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Next time you can login with", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Done!", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Got depot key", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("licenses for account", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeNetworkFailure(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return text.Contains("Unable to get steam3 credentials", StringComparison.OrdinalIgnoreCase)
            || text.Contains("InitializeSteam failed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Connection to Steam failed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Trying again", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Connection timeout downloading depot", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Timeout", StringComparison.OrdinalIgnoreCase)
            || text.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || text.Contains("SocketException", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Socket closed", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>连不上 Steam 登录服务时的统一对策。CM 段、二维码失败段、「长时间无响应」段共用一份 ——
    /// 免得改了一处、另一处还在教用户配分流。v1.2.4 前这里写的是「Clash 设 DIRECT / 开 TUN」，
    /// 实测 DD 的 HttpClient 根本不读注册表系统代理，分流规则对它无效；能动的只有我们
    /// 替它注入 HTTPS_PROXY（见 ApplyDdProxyEnv），所以对策改成「确认代理开的是系统代理模式」。</summary>
    static string LoginProxyAdvice() => LocService.Tr(
        "国内访问 Steam 的登录接口时好时坏，与 Clash 的分流规则无关。" +
        "启动器会自动跟随你开启的 Windows 系统代理 —— 若本机代理没生效，请确认代理软件开的是「系统代理」模式；" +
        "也可以换手机热点或开加速器后重试。");

    /// <summary>失败该怎么说 —— 按**实际断在哪一段**分，不再一律甩锅 CM。
    /// 实测踩过：登录一路成功（"授权成功""已连上 Steam"都打了），断的是 CDN 分片，
    /// 旧文案却回一句"CM 握手失败 + 三条 Clash 建议"，把人往错的方向带。</summary>
    private static string NetworkHint(string? tail = null)
    {
        var t = tail ?? "";
        // "Failed to find any server with chunk …" 也算 CDN 段：登录/授权那时都已经成功了，
        // 而且判定只看最后几十行（前面几千行 Validating 会把它挤掉），漏了这一条就会甩锅 CM。
        if (t.Contains("downloading chunk", StringComparison.OrdinalIgnoreCase)
            || t.Contains("depot manifest", StringComparison.OrdinalIgnoreCase)
            || t.Contains("Failed to find any server", StringComparison.OrdinalIgnoreCase)
            || t.Contains("sending the request", StringComparison.OrdinalIgnoreCase))
            return LocService.Tr("登录是通的，断在从 Steam 内容服务器（CDN）拉数据：换手机热点或开加速器重试即可，改代理的分流规则没用。");
        return LocService.Tr("连不上 Steam 登录服务：") + LoginProxyAdvice();
    }

    /// <summary>工具装在程序安装目录（插件内容，跟 JuniGrid 走，不进缓存）。</summary>
    private static string DepotDownloaderDir =>
        Path.Combine(AppContext.BaseDirectory, "tools", "DepotDownloader");
    private static string DepotDownloaderExe => Path.Combine(DepotDownloaderDir, "DepotDownloader.exe");
    private static string LegacyDepotDownloaderDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JuniGrid", "DepotDownloader");

    // framework 包约 2.5MB zip / 解压约 8MB，需本机有 .NET 9+（SMAPI/多数玩家已有）；
    // 自包含包约 32MB zip / 解压约 76MB，无运行时也能跑。优先 framework。
    private const string DepotDownloaderFrameworkUrl =
        "https://github.com/SteamRE/DepotDownloader/releases/download/DepotDownloader_3.4.0/DepotDownloader-framework.zip";
    private const string DepotDownloaderStandaloneUrl =
        "https://github.com/SteamRE/DepotDownloader/releases/download/DepotDownloader_3.4.0/DepotDownloader-windows-x64.zip";

    /// <summary>本机是否有 .NET 9+ 运行时（framework 版 DD 的最低要求）。</summary>
    private static bool HasCompatibleDotnetRuntime()
    {
        try
        {
            var roots = new List<string>();
            var env = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrWhiteSpace(env)) roots.Add(env);
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"));
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet"));

            foreach (var root in roots)
            {
                var shared = Path.Combine(root, "shared", "Microsoft.NETCore.App");
                if (!Directory.Exists(shared)) continue;
                foreach (var dir in Directory.EnumerateDirectories(shared))
                {
                    var name = Path.GetFileName(dir);
                    var dot = name.IndexOf('.');
                    if (dot <= 0) continue;
                    if (int.TryParse(name.AsSpan(0, dot), out var major) && major >= 9) return true;
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 确保 DepotDownloader 就位（framework 约 3MB；无 .NET 9+ 时回退自包含约 32MB）。
    /// 装在安装目录 tools\DepotDownloader；旧 LocalAppData 副本首次访问时搬迁。
    /// </summary>
    public async Task EnsureDepotDownloaderAsync(IProgress<string>? log = null, CancellationToken ct = default)
    {
        await _depotEnsureGate.WaitAsync(ct);
        try
        {
        // 旧位置 → 安装目录
        try
        {
            if (!File.Exists(DepotDownloaderExe)
                && Directory.Exists(LegacyDepotDownloaderDir)
                && !Directory.Exists(DepotDownloaderDir))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DepotDownloaderDir)!);
                if (StorageService.TryMoveTree(LegacyDepotDownloaderDir, DepotDownloaderDir))
                    AppLog.Warn("DepotDownloader", "工具已迁到安装目录: " + DepotDownloaderDir);
            }
        }
        catch (Exception ex) { AppLog.Warn("DepotDownloader", "工具迁移失败: " + ex.Message); }

        if (File.Exists(DepotDownloaderExe)) return;

        try { Directory.CreateDirectory(DepotDownloaderDir); }
        catch (Exception ex)
        {
            throw new DepotException(
                $"无法在安装目录写入工具组件（{DepotDownloaderDir}）：{ex.Message}。" +
                "若是 Program Files 请用管理员运行，或把 JuniGrid 装到有写权限的目录");
        }

        var useFramework = HasCompatibleDotnetRuntime();
        var url = useFramework ? DepotDownloaderFrameworkUrl : DepotDownloaderStandaloneUrl;
        log?.Report(useFramework
            ? LocService.Tr("正在下载登录组件（GitHub 官方精简包，约 3MB）…")
            : LocService.Tr("正在下载登录组件（GitHub 官方完整包，约 32MB，本机缺少 .NET 9+ 运行时）…"));

        var zip = Path.Combine(Path.GetTempPath(), "junigrid-depotdownloader.zip");
        // v1.6.2：直连 GitHub 失败自动切镜像（ghfast.top / gh-proxy / ghproxy）——
        // 国内裸机首次切版本不再卡死在组件下载上
        Exception? lastErr = null;
        var downloaded = false;
        foreach (var candidate in UpdateService.GithubUrls(url))
        {
            try
            {
                using var resp = await DepotHttp.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                await using (var fs = File.Create(zip))
                {
                    await resp.Content.CopyToAsync(fs, ct);
                }
                // 镜像可能拿 200 状态回 HTML 错误页 —— 校验 PK 文件头，不是 zip 视为该候选失败
                using (var head = File.OpenRead(zip))
                {
                    Span<byte> b = stackalloc byte[2];
                    if (head.Read(b) < 2 || b[0] != (byte)'P' || b[1] != (byte)'K')
                        throw new InvalidOperationException("候选源返回的不是 zip（可能是错误页）");
                }
                downloaded = true;
                break;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastErr = ex;
                try { File.Delete(zip); } catch { }
            }
        }
        if (!downloaded)
            throw new DepotException($"登录组件下载失败（直连与镜像均不通）：{lastErr?.Message}");
        ZipFile.ExtractToDirectory(zip, DepotDownloaderDir, overwriteFiles: true);
        try { File.Delete(zip); } catch { }
        AppLog.Warn("DepotDownloader", $"DepotDownloader 就绪（{(useFramework ? "framework" : "standalone")}）：" + DepotDownloaderExe);
        }
        finally
        {
            _depotEnsureGate.Release();
        }
    }

    private const char BlockChar = (char)0x2588; // █
    // DD 不同版本可能用 ■ ▀ ▄ 等，一并认作「黑模块」
    private static bool IsQrDarkChar(char c) =>
        c is '█' or '▀' or '▄' or '■' or '▓' or '▒' or '●' or '#' or '@';

    private static bool LooksLikeQrLine(string line)
    {
        if (line.Length < 20) return false;
        var dark = 0;
        foreach (var c in line)
            if (IsQrDarkChar(c)) dark++;
            else if (!char.IsWhiteSpace(c)) return false;
        return dark >= 8;
    }

    private static string UrlToDataUri(string url)
    {
        using var gen = new QRCoder.QRCodeGenerator();
        using var data = gen.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.M);
        using var png = new QRCoder.PngByteQRCode(data);
        var bytes = png.GetGraphic(12);
        return "data:image/png;base64," + Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// DepotDownloader 通道：扫码（首次）或本机令牌（之后）登录并下载历史版本到暂存目录，
    /// 完成后直接替换游戏文件（保留 Mods/）。onDataUri 回调给出二维码 PNG 的 data URI（标准库生成，保证可扫）；
    /// 返回登录的 Steam 账号名（回填 UI 用）。
    /// </summary>
    public async Task<string> QrDownloadAndApplyAsync(
        string gamePath, string appId, string depotId, string manifestId, string? versionLabel,
        bool tokenAvailable, string? knownAccount,
        Func<string, Task> onDataUri,
        IProgress<Progress>? progress = null, CancellationToken ct = default,
        bool applyToGame = true)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
            throw new DepotException("游戏目录无效，请先在设置里定位游戏");
        if (string.IsNullOrWhiteSpace(manifestId) || !manifestId.All(char.IsDigit))
            throw new DepotException("Manifest ID 无效（应为一串数字）");

        // 只下载模式：本地已经有完整包就到此为止，不碰游戏目录
        if (!applyToGame && IsStagedComplete(manifestId))
        {
            progress?.Report(new Progress(LocService.Tr("本地已有完整缓存"), 100));
            return knownAccount ?? "";
        }
        // 本地已有完整包 → 直接应用，不下载、不删缓存、不连 Steam
        if (applyToGame && TryApplyStaged(gamePath, appId, depotId, manifestId, progress, TryReadGameVersion(gamePath)))
        {
            progress?.Report(new Progress(LocService.Tr("已切换到历史版本（本地缓存）"), 100));
            AppLog.Warn("DepotDownloader", $"历史版本从缓存应用：manifest={manifestId}");
            return knownAccount ?? "";
        }

        // 真要连 Steam 了才占会话：前面两条本地缓存快路径不排队。排队状态由这里报出去，
        // 上层只照着它标「等待中」—— 上层自己再拿一遍这把闸会死锁（见 HoldSteamSessionAsync）。
        // 判据是"槽位满了"而不是"有人在用"：3 路并行时第 2、3 路不算排队。
        if (SteamSessionCount >= MaxSteamSessions)
            progress?.Report(new Progress(LocService.Tr("排队中，等前面的版本下完"), null, Queued: true));
        using var session = await HoldSteamSessionAsync(ct);
        // 每一轮尝试都从 0% 起报：下载器按文件列表重走一遍，留着上一轮的旧数字会让人以为"又卡在同一段"
        progress?.Report(new Progress(LocService.Tr("正在启动下载…"), 0, Queued: false));

        await EnsureDepotDownloaderAsync(new Progress<string>(msg => progress?.Report(new Progress(msg, null))), ct);

        var staging = StagingDirFor(appId, depotId, versionLabel, manifestId);
        // 半截包不再一删了之。删了就等于每次重试都从 0 开始 —— 而国内到 Steam 内容服务器
        // 恰恰是**尾部 chunk 成片超时**（日志里成片的 Connection timeout downloading chunk），
        // 于是永远卡在同一段。留着让它接着下，并带 -verify-all：DD 会预分配整个包，
        // 半截文件"大小看着是对的"，只按大小判会把残缺当完成。
        var resuming = CanResumeStagedPackage(staging, manifestId);
        if (!resuming)
        {
            PreserveStagedModsBeforeWipe(staging);   // 抽屉里可能是用户唯一的 mod 本体，先挪走再清
            try
            {
                if (Directory.Exists(staging))
                {
                    AppLog.Warn("DepotDownloader",
                        $"这个缓存包不能续传（manifest 对不上或没有本体），清空重下：{staging}");
                    Directory.Delete(staging, true);
                }
            }
            catch { }
        }
        Directory.CreateDirectory(staging);
        WriteManifestMeta(staging, manifestId);
        if (resuming)
            progress?.Report(new Progress(LocService.Tr("接着上次没下完的部分继续（先校验已下的文件）…"), 0));

        var account = knownAccount;
        var qr = new List<string>();
        var failure = default(string);
        var qrEmitted = false;
        string? challengeUrl = null;
        // v1.4.6：DepotDownloader 拉 manifest 会无限 "Connection timeout ... Retrying."
        // （输出一直有行 → 静默看门狗永远不触发）。连超 N 次就掐掉，别让用户对着 90% 干等。
        var manifestTimeouts = 0;
        var manifestGiveUp = false;

        var onLine = new Action<string>(line =>
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) return;
            AppLog.Warn("DepotDownloader", "DD: " + trimmed);

            // 优先：日志里若直接有 s.team / 登录 URL，用 URL 生成标准码（比 ASCII 稳）
            var um = Regex.Match(trimmed, "https://s\\.team/q/[^\\s'\"`]+");
            if (um.Success && challengeUrl != um.Value)
            {
                challengeUrl = um.Value;
                try
                {
                    var uri = UrlToDataUri(challengeUrl);
                    qrEmitted = true;
                    _ = onDataUri(uri);
                }
                catch (Exception ex) { AppLog.Warn("DepotDownloader", "URL 生成二维码失败: " + ex.Message); }
            }

            // QR 行禁止 Trim：行尾空格是白模块
            if (LooksLikeQrLine(line)) { qr.Add(line.TrimEnd('\r')); return; }
            if (qr.Count >= 27)
            {
                var snapshot = qr.ToArray();
                qr.Clear();
                if (!qrEmitted)
                {
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var (dataUri, url) = QrAsciiToDataUriAndUrl(snapshot);
                            qrEmitted = true;
                            _ = onDataUri(dataUri);
                            AppLog.Warn("DepotDownloader", $"ASCII 二维码已生成 rows={snapshot.Length} url={url}");
                        }
                        catch (Exception ex) { AppLog.Warn("DepotDownloader", "ASCII 二维码解析失败: " + ex.Message); }
                    }, ct);
                }
            }
            else if (qr.Count > 0 && qr.Count < 27 && !LooksLikeQrLine(line))
            {
                // 二维码中间夹了非码行：保留已收集行，等凑齐；仅在明显中断时清空
                if (trimmed.Contains("ERROR", StringComparison.OrdinalIgnoreCase)) qr.Clear();
            }

            account = TryParseAccount(trimmed, account);
            var pct = Regex.Match(trimmed, @"\((\d+) / (\d+)\)");
            // manifest/chunk 连接超时：DD 自己每 10 秒重试一次，界面上必须看见
            if (trimmed.Contains("Connection timeout downloading depot", StringComparison.OrdinalIgnoreCase))
            {
                manifestTimeouts++;
                progress?.Report(new Progress(
                    manifestTimeouts >= 6
                        ? LocService.Tr("Steam CDN 连不上（版本清单超时），准备结束任务…")
                        : LocService.Tf("Steam CDN 超时，正在重试（{0}/6）…", manifestTimeouts),
                    90, trimmed));
                if (manifestTimeouts >= 6)
                {
                    manifestGiveUp = true;
                    failure = "Steam CDN 连接超时（版本清单一直拉不下来）";
                }
            }
            else if (pct.Success || trimmed.Contains("Downloading chunk", StringComparison.OrdinalIgnoreCase)
                     || trimmed.StartsWith("Got depot key", StringComparison.OrdinalIgnoreCase))
                manifestTimeouts = 0;

            if (LooksLikeLoginSuccess(trimmed) && !string.IsNullOrWhiteSpace(account))
            {
                // 扫码确认成功 —— 立刻让 UI 知道（进程可能还要下 manifest 才退出）
                progress?.Report(new Progress(LocService.Tr("Steam 授权成功，正在完成…"), 90));
            }
            if (pct.Success && long.TryParse(pct.Groups[1].Value, out var done) && long.TryParse(pct.Groups[2].Value, out var total) && total > 0)
                progress?.Report(new Progress(LocService.Tr("正在下载版本文件…"), (int)Math.Min(99, done * 100 / total), trimmed));
            else if (trimmed.Contains("Use the Steam Mobile App", StringComparison.OrdinalIgnoreCase))
                progress?.Report(new Progress(LocService.Tr("等待手机扫码确认…（若下方无图请稍候或点重试）"), null));
            else if (qrEmitted)
                progress?.Report(new Progress(LocService.Tr("等待手机扫码确认…"), null));
            else if (trimmed.Contains("Logging in user", StringComparison.OrdinalIgnoreCase))
                progress?.Report(new Progress(trimmed.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                    ? LocService.Tr("登录被 Steam 拒绝（令牌可能已失效，请重新扫码）") : LocService.Tr("正在登录 Steam 账号…"), null, trimmed));
            else if (trimmed.StartsWith("Downloading depot", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.Contains("Depot download", StringComparison.OrdinalIgnoreCase))
                progress?.Report(new Progress(LocService.Tr("正在下载版本文件…"), null, trimmed));
            else if (!LooksLikeQrLine(line))
            {
                var zh = EngineLineToChinese(trimmed);
                if (zh is not null) progress?.Report(new Progress(zh, null, trimmed));
            }

            if (trimmed.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("FAILED login", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("TwoFactorCodeMismatch", StringComparison.OrdinalIgnoreCase))
                failure = trimmed;
        });

        // 已扫码授权 → 优先本机令牌；无账号名时退回 -qr
        var useToken = tokenAvailable && !string.IsNullOrWhiteSpace(knownAccount);
        progress?.Report(new Progress(useToken ? LocService.Tr("正在用本机授权连接 Steam…") : LocService.Tr("正在连接 Steam…"), null));
        var args = $"-app {appId} -depot {depotId} -manifest {manifestId} -dir \"{staging}\" -remember-password -max-downloads 16{(resuming ? " -verify-all" : "")} -loginid {NextLoginId()}";
        args += useToken ? $" -username {knownAccount!.Trim()}" : " -qr";

        // 重开一路前要清掉「已出过码」的状态：ASCII 分支的 if (!qrEmitted) 会拦住第二张码
        void ResetQrState() { qrEmitted = false; qr.Clear(); challengeUrl = null; failure = null; }

        var (exitCode, tail) = await RunWithNetworkRetryAsync(args, line =>
        {
            onLine(line);
            if (line.Contains("Use the Steam Mobile App", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("STEAM GUARD", StringComparison.OrdinalIgnoreCase))
                SilenceTimeoutMs = 300000;
        }, progress, ct, kickoffDeadlineMs: KickoffDeadlineMs, onBeforeRetry: ResetQrState,
            stopWhen: () => manifestGiveUp);

        var joinedFail = string.Join("\n", tail);
        if (useToken && exitCode != 0 && !HasGameBinary(staging)
            && (joinedFail.Contains("password", StringComparison.OrdinalIgnoreCase)
                || (failure is not null && failure.Contains("password", StringComparison.OrdinalIgnoreCase))))
        {
            AppLog.Warn("DepotDownloader", "令牌路径失败，自动回退扫码");
            progress?.Report(new Progress(LocService.Tr("本机授权失效，改用扫码…"), null));
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
            Directory.CreateDirectory(staging);
            WriteManifestMeta(staging, manifestId);
            ResetQrState();
            useToken = false;
            args = $"-app {appId} -depot {depotId} -manifest {manifestId} -dir \"{staging}\" -remember-password -max-downloads 16{(resuming ? " -verify-all" : "")} -loginid {NextLoginId()} -qr";
            (exitCode, tail) = await RunWithNetworkRetryAsync(args, onLine, progress, ct,
                onBeforeRetry: ResetQrState);
        }

        if (exitCode != 0 || !HasGameBinary(staging))
        {
            var hint = failure ?? (tail.Count > 0 ? tail[^1] : "");
            var all = string.Join("\n", tail);
            if (string.IsNullOrWhiteSpace(account))
                foreach (var t in tail) { account = TryParseAccount(t, account); if (!string.IsNullOrWhiteSpace(account)) break; }

            // 账号没有这款游戏 / 无下载许可 —— 不是网络问题，必须单独说清楚
            if (all.Contains("is not available from this account", StringComparison.OrdinalIgnoreCase)
                || all.Contains("No license", StringComparison.OrdinalIgnoreCase)
                || all.Contains("not owned", StringComparison.OrdinalIgnoreCase))
            {
                var who = string.IsNullOrWhiteSpace(account) ? knownAccount : account;
                throw new DepotException(
                    $"当前 Steam 账号{(string.IsNullOrWhiteSpace(who) ? "" : $"（{who}）")}没有《星露谷物语》的下载许可，" +
                    "无法下载历史版本。请改用拥有本体的账号在设置里重新扫码授权。");
            }
            if (tail.Any(l => l.Contains("password", StringComparison.OrdinalIgnoreCase)) && !useToken)
                throw new DepotException("本机登录令牌已失效，请重新点击「扫码登录并下载」扫一次码");
            if (LooksLikeNetworkFailure(hint) || LooksLikeNetworkFailure(all))
            {
                // 屏幕上只留一句人话 + 一句对策；引擎原文（很长、带分片哈希）进日志备查
                AppLog.Warn("DepotDownloader", $"下载未完成（退出码 {exitCode}）引擎原文：{all}");
                throw new DepotException($"下载未完成：{NetworkHint(all)}");
            }
            throw new DepotException(
                $"扫码下载未完成（退出码 {exitCode}）。{(hint.Length > 0 ? "最后输出：" + hint + " " : "")}" +
                "常见原因：网络超时或二维码过期，可重试");
        }

        // 到这里才是"这一版真的下完了"：退出码 0 且本体在盘上。
        // 记这个标记是因为：分片超时会留下一个「看起来完整」的半截包（锚点文件都在，
        // 缺的是别的角色的贴图），切过去进档时 NPC 构造直接空引用闪退
        // （09-22 00:04 WER：NullReferenceException at StardewValley.NPC..ctor ← loadForNewGame）。
        TryWriteCompleteMark(staging);

        if (string.IsNullOrWhiteSpace(account))
            foreach (var t in tail) { account = TryParseAccount(t, account); if (!string.IsNullOrWhiteSpace(account)) break; }

        if (!applyToGame)
        {
            // 文件已经躺在缓存抽屉里（下载阶段就写进 staging 了），切换由用户点版本号时再做
            progress?.Report(new Progress(LocService.Tf("已下载完成，点版本号即可切换到 {0}", versionLabel ?? manifestId), 100));
            return account ?? knownAccount ?? "";
        }
        progress?.Report(new Progress(LocService.Tr("正在替换游戏文件…"), null));
        await Task.Run(() => ApplyStagingExclusive(staging, gamePath, progress, TryReadGameVersion(gamePath)), ct);
        progress?.Report(new Progress(LocService.Tr("已切换到历史版本"), 100));
        AppLog.Warn("DepotDownloader", $"DD 通道历史版本已应用：manifest={manifestId} → {gamePath}");
        return account ?? knownAccount ?? "";
    }

    /// <summary>一路 DD 从启动到「出码 / 登录成功 / 开始下载」的等待上限。实测出码是双峰的：
    /// 要成就是 2–5 秒，不成就是 DD 自己那 10 轮 CM 退避烧满 ~64 秒 —— 到点没进展就重开一路
    /// （新进程等于重新抽一次 CM 列表）。测试台会临时调小它来演这条。</summary>
    internal static int KickoffDeadlineMs = 20000;

    /// <summary>DepotDownloader 的英文状态行 → 给人看的中文。返回 null = 这句不占主提示位
    /// （宁可留着上一句中文，也不要让一行英文把「二维码已失效，正在重新获取（第 2/3 次）…」顶掉）。
    /// 原文每一行都以 DD-qr: 前缀进了 juni-grid.log，所以不显示 ≠ 查不到。
    /// 口径收在这一个函数：两条扫码通道（只登录 / 下载并应用）共用。</summary>
    private static string? EngineLineToChinese(string line)
    {
        if (line.Contains("Connection to Steam failed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Trying again", StringComparison.OrdinalIgnoreCase))
            return LocService.Tr("正在连接 Steam 服务器…（连不上会自动重开一路）");
        if (line.Contains("Lost connection", StringComparison.OrdinalIgnoreCase))
            return LocService.Tr("与 Steam 服务器的连接断了，正在重连…");
        if (line.Contains("InitializeSteam failed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Unable to get steam3 credentials", StringComparison.OrdinalIgnoreCase))
            return LocService.Tr("Steam 客户端初始化没成功，正在重开一路…");
        if (line.Contains("Got AppInfo", StringComparison.OrdinalIgnoreCase))
            return LocService.Tr("已连上 Steam，正在读取版本信息…");
        if (line.Contains("Processing depot", StringComparison.OrdinalIgnoreCase)
            || DepotProgressRx.IsMatch(line))
            return LocService.Tr("正在下载 depot…");
        if (line.Contains("Logging in", StringComparison.OrdinalIgnoreCase))
            return LocService.Tr("正在登录 Steam…");
        return null;
    }

    private static readonly Regex DepotProgressRx = new(@"\(\d+ / \d+\)", RegexOptions.Compiled);

    private static int SilenceTimeoutMs = 75000;   // 二维码出现后调大到 300 秒（等待用户扫码）

    /// <summary>「立即重开」的信号：出码截止是 20 秒，用户不该干等 —— 置位后当前这一路
    /// 会在 1 秒内被掐掉并马上开下一路，且这次重开不占用重试次数（是用户主动要的，不是失败）。
    /// 带时间戳只认 10 秒内的请求：没人轮询时留下的标志，会把之后某次正常下载掐掉 —— 那是更难查的事故。</summary>
    private static long _kickAt;
    public static void RequestKickNow() => Volatile.Write(ref _kickAt, Environment.TickCount64);
    private static bool TakeKick()
    {
        var at = Interlocked.Exchange(ref _kickAt, 0);
        return at != 0 && Environment.TickCount64 - at < 10_000;
    }

    /// <summary>仅扫码授权：登录并保存本机令牌（-manifest-only，不下载游戏文件）。返回账号名。
    /// onAsciiLines 给矢量码用的原始码行，onStale 在「这张码作废、正在重取」时回调。</summary>
    public async Task<string> QrLoginOnlyAsync(Func<string, Task> onDataUri,
        IProgress<Progress>? progress = null, CancellationToken ct = default,
        Func<IReadOnlyList<string>, Task>? onAsciiLines = null,
        Action? onStale = null)
    {
        // 与下载共用同一把会话闸：下载在飞时硬起第二路会把它顶掉（见 SessionGate 注释）
        using var session = await HoldSteamSessionAsync(ct);
        await EnsureDepotDownloaderAsync(new Progress<string>(msg => progress?.Report(new Progress(msg, null))), ct);
        SilenceTimeoutMs = 300000;

        string? account = null;
        var qr = new List<string>();
        var failure = default(string);
        var qrEmitted = false;
        var authorized = false;      // DD 打出 Success! 的那一刻（票据已落盘，见 RunDepotDownloaderAsync）
        string? challengeUrl = null;

        void EmitQr(string uri, string? url, IReadOnlyList<string>? ascii)
        {
            qrEmitted = true;
            if (url is not null) challengeUrl = url;
            _ = onDataUri(uri);
            if (ascii is not null && onAsciiLines is not null) _ = onAsciiLines(ascii);
        }

        // DD 每 ~22 秒换一张 challenge，而一张码的 29 行是在十几毫秒内连着吐完的。
        // 以前要等「下一行非码文」才结算，而那下一行往往正是「The QR code has changed」——
        // 于是界面永远停在上一张，用户扫的是已经作废的码，手机只报「加载二维码失败」
        // （实测 07:25:04 那张码到 07:25:26 换码时才推给界面）。现在按「最后一行后再无新行」结算。
        var qrLock = new object();
        var qrGen = 0;
        var qrDecoding = false;

        void FlushQr()
        {
            string[] snapshot;
            lock (qrLock)
            {
                if (qr.Count < 27 || qrDecoding) return;   // 半截码不推，扫不出更坑
                snapshot = qr.ToArray();
                qr.Clear();
                qrDecoding = true;
            }
            _ = Task.Run(() =>
            {
                try
                {
                    var (dataUri, url) = QrAsciiToDataUriAndUrl(snapshot);
                    EmitQr(dataUri, url, snapshot);
                    AppLog.Warn("DepotDownloader", $"ASCII 二维码已生成 rows={snapshot.Length} url={url}");
                }
                catch (Exception ex)
                {
                    AppLog.Warn("DepotDownloader", "ASCII 二维码解析失败: " + ex.Message);
                    var u = challengeUrl;
                    if (u is not null) { try { EmitQr(UrlToDataUri(u), u, snapshot); } catch { } }
                }
                finally { lock (qrLock) qrDecoding = false; }
            }, CancellationToken.None);
        }

        void ScheduleFlush()
        {
            int gen;
            lock (qrLock) gen = ++qrGen;
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(500, ct); } catch { return; }
                lock (qrLock) { if (gen != qrGen) return; }   // 期间又来了新行：交给最后那行的定时器
                FlushQr();
            }, CancellationToken.None);
        }

        var onLine = new Action<string>(line =>
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) return;
            AppLog.Warn("DepotDownloader", "DD-qr: " + trimmed);

            var um = Regex.Match(trimmed, "https://s\\.team/q/[^\\s'\"`]+");
            if (um.Success && challengeUrl != um.Value)
            {
                challengeUrl = um.Value;
                try
                {
                    EmitQr(UrlToDataUri(challengeUrl), challengeUrl, null);
                }
                catch (Exception ex) { AppLog.Warn("DepotDownloader", "URL 生成二维码失败: " + ex.Message); }
            }

            // QR 行禁止 Trim：行尾空格是白模块
            if (LooksLikeQrLine(line))
            {
                lock (qrLock) qr.Add(line.TrimEnd('\r'));
                ScheduleFlush();
                return;
            }

            // 非码文来了 ⇒ 这张码必定画完了，立刻结算（比 500ms 静默那条快）
            FlushQr();
            if (trimmed.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                lock (qrLock) qr.Clear();     // 半截码 + 报错：丢掉，别把残码推给人扫

            account = TryParseAccount(trimmed, account);
            if (LooksLikeLoginSuccess(trimmed) && !string.IsNullOrWhiteSpace(account))
            {
                authorized = true;
                progress?.Report(new Progress(LocService.Tr("Steam 授权成功，正在收尾…"), 90));
            }

            // 码出来之后 Steam 中继断了 → 挑战立即失效，必须让用户重开
            if (qrEmitted && (
                    trimmed.Contains("Lost connection", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Contains("InitializeSteam failed", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Contains("Unable to get steam3 credentials", StringComparison.OrdinalIgnoreCase)
                    || trimmed.Contains("Connection to Steam failed", StringComparison.OrdinalIgnoreCase)))
            {
                failure = "Steam 连接已中断，当前二维码已失效，请关闭后重新扫码";
                progress?.Report(new Progress(failure, null, trimmed));
                return;
            }

            if (trimmed.Contains("Use the Steam Mobile App", StringComparison.OrdinalIgnoreCase))
                progress?.Report(new Progress(LocService.Tr("等待手机扫码确认…（正在生成二维码）"), null));
            else if (qrEmitted && !LooksLikeLoginSuccess(trimmed))
                progress?.Report(new Progress(LocService.Tr("等待手机扫码确认…"), null));
            else if (trimmed.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.Contains("FAILED login", StringComparison.OrdinalIgnoreCase))
            {
                failure = trimmed;
                progress?.Report(new Progress(trimmed.Length > 80 ? trimmed[..80] + "…" : trimmed, null, trimmed));
            }
            else if (!LooksLikeQrLine(line))
            {
                var zh = EngineLineToChinese(trimmed);
                if (zh is not null) progress?.Report(new Progress(zh, null, trimmed));
            }
        });

        progress?.Report(new Progress(LocService.Tr("正在连接 Steam 中继…"), null));
        // 每路到点没出码就重开（新进程等于重新抽一次 CM 列表）。实测出码是双峰的 ——
        // 要成就是 2–5 秒，不成就是 DD 自己 10 轮退避烧满 60 秒，所以截止不会误杀
        // 「本来会成」的那一路。成功率约 1/3 → P(至少一路出码) ≈ 1-(2/3)^3 ≈ 70%，
        // 最坏 3×20+退避 ≈ 80 秒（旧配置 2×64 ≈ 130 秒）。
        var (code, tail) = await RunWithNetworkRetryAsync(
            $"-app 413150 -qr -remember-password -manifest-only -max-downloads 4 -loginid {NextLoginId()}",
            onLine, progress, ct, maxAttempts: 3, kickoffDeadlineMs: KickoffDeadlineMs,
            stopWhen: () => authorized,
            onBeforeRetry: () =>
            {
                // 死码必须撤干净：界面上留着旧码就是让人去扫一张废的。
                // （出码本身已不再被 qrEmitted 拦，但那张码归属的进程已经退了，仍要清缓冲。）
                onStale?.Invoke();
                qrEmitted = false;
                lock (qrLock) { qr.Clear(); qrDecoding = false; }
                challengeUrl = null;
                failure = null;
            });

        // 兜底：从输出尾部再捞一次账号
        if (string.IsNullOrWhiteSpace(account))
            foreach (var t in tail)
            {
                account = TryParseAccount(t, account);
                if (!string.IsNullOrWhiteSpace(account)) break;
            }

        var joinedTail = string.Join("\n", tail);
        var success = !string.IsNullOrWhiteSpace(account)
                      || LooksLikeLoginSuccess(joinedTail)
                      || code == 0 && !LooksLikeNetworkFailure(joinedTail);

        if (!success)
        {
            // 内部造的截止行（「启动器 N 秒内未拿到二维码…」）不是失败原因，别抖给用户
            var hint = failure ?? (tail.LastOrDefault(t => !t.Contains("启动器 ")) ?? "");
            if (LooksLikeNetworkFailure(hint) || LooksLikeNetworkFailure(joinedTail))
            {
                AppLog.Warn("DepotDownloader", "扫码 CM 握手失败，DD 原始输出尾部：" + joinedTail);
                // 出过码 ⇒ 真话是「码死了」而不是「连不上所以没码」，两者处置完全不同：
                // 前者重取一张并尽快扫就行，后者要先解决网络。实测 03:45:20 出码、
                // 03:45:35 掉线，那时甩"CM 握手已重试 3 次"给用户，人只会对着死码继续扫。
                if (qrEmitted)
                    throw new DepotException(
                        "二维码已失效 —— 出码后 Steam 连接就断了，这张码扫了手机只会报「加载二维码失败」。" +
                        "请等界面刷出新码后再扫；多次刷新都失败再按下面的网络建议处理。");
                throw new DepotException(LocService.Tr("连不上 Steam 服务器（CM 握手已重试 3 次）。") + LoginProxyAdvice());
            }
            if (!qrEmitted)
                throw new DepotException(
                    $"未拿到可扫描的二维码（退出码 {code}）。{hint} " +
                    "请把 %AppData%\\JuniGrid\\juni-grid.log 里最近的 DD-qr 行发来排查。");
            throw new DepotException(
                string.IsNullOrWhiteSpace(failure) || !failure.Contains("失效")
                    ? $"扫码授权未完成（退出码 {code}）。{hint} 常见原因：二维码过期、Steam 中继断开，请重新扫码"
                    : failure);
        }

        progress?.Report(new Progress(LocService.Tr("授权成功"), 100));
        AppLog.Warn("DepotDownloader", $"扫码授权完成 account={account ?? "?"} exit={code}");
        return account ?? "";
    }

    /// <summary>
    /// 把 DepotDownloader 输出的 ASCII 二维码还原成原始 URL，再用 QRCoder 生成标准 PNG data URI。
    /// 返回 (dataUri, 解码出的 URL)。
    /// </summary>
    private static (string DataUri, string Url) QrAsciiToDataUriAndUrl(IReadOnlyList<string> lines)
    {
        // 各行等长化（含行尾空白模块）；每模块 1 或 2 字符
        var maxLen = 0;
        foreach (var l in lines) maxLen = Math.Max(maxLen, l.Length);
        if (maxLen < 20) throw new DepotException("二维码行过短，无法解析");

        // 29×29 的 QR 模块宽约 58 字符（2 字符/模块）或 29（1 字符/模块）
        var twoCharPerCell = maxLen >= lines.Count * 1.5;
        var cells = twoCharPerCell ? (maxLen + 1) / 2 : maxLen;
        var rgb = new byte[lines.Count * cells * 3];
        for (var r = 0; r < lines.Count; r++)
        {
            var l = lines[r].PadRight(maxLen);
            for (var c = 0; c < cells; c++)
            {
                bool black;
                if (twoCharPerCell)
                {
                    var i0 = c * 2;
                    black = IsQrDarkChar(l[i0]) || (i0 + 1 < l.Length && IsQrDarkChar(l[i0 + 1]));
                }
                else black = IsQrDarkChar(l[Math.Min(c, l.Length - 1)]);
                var i = (r * cells + c) * 3;
                rgb[i] = rgb[i + 1] = rgb[i + 2] = black ? (byte)0 : (byte)255;
            }
        }
        var src = new ZXing.RGBLuminanceSource(rgb, cells, lines.Count);
        var reader = new ZXing.QrCode.QRCodeReader();
        var result = reader.decode(new ZXing.BinaryBitmap(new ZXing.Common.HybridBinarizer(src)))
                   ?? throw new DepotException($"二维码内容解析失败（{lines.Count}行×{cells}格，行宽{maxLen}）");
        return (UrlToDataUri(result.Text), result.Text);
    }

    /// <summary>
    /// CM 握手在国内经常秒败。网络类失败自动重试（间隔 2s/4s…）。
    /// 出过码后进程又被掉线杀掉，那张 challenge 已作废 ⇒ 额外重取一次新码（onBeforeRetry
    /// 先让调用方把死码从界面上撤掉、并放开重新出码的开关）。
    /// </summary>
    private async Task<(int ExitCode, List<string> Tail)> RunWithNetworkRetryAsync(
        string args, Action<string> onLine, IProgress<Progress>? progress, CancellationToken ct,
        int maxAttempts = 5, int kickoffDeadlineMs = 0, Action? onBeforeRetry = null,
        Func<bool>? stopWhen = null)
    {
        List<string> lastTail = new();
        int lastCode = 1;
        var afterDeadQr = false;
        var deadQrRetries = 0;
        var manualKicks = 0;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (attempt > 1)
            {
                // 用户主动踢的这一次不算进重试次数（他不是失败方，不该因为点了按钮而更早放弃）；
                // 用完就清零，否则后面每一轮都会被当成"手动重开"而跳过退避与计数
                var manual = manualKicks > 0;
                manualKicks = 0;
                if (manual) maxAttempts++;
                // 上一张码已随死掉的进程作废：先把它从界面上撤掉，再等重试间隔
                if (afterDeadQr) onBeforeRetry?.Invoke();
                var delay = manual ? 0 : Math.Min(15000, 2000 * attempt);
                progress?.Report(new Progress(manual
                    ? LocService.Tr("按你的要求重开一路连接…")
                    : afterDeadQr
                    ? LocService.Tf("二维码已失效，正在重新获取（第 {0}/{1} 次）…", attempt, maxAttempts)
                    : $"连不上 Steam 服务器，重开一路再试（第 {attempt}/{maxAttempts} 次）…", 0));
                await Task.Delay(delay, ct);
            }
            afterDeadQr = false;
            var gotQr = false;
            var kickedOffAny = false;
            var wrapped = new Action<string>(line =>
            {
                if (line.Contains("Use the Steam Mobile App", StringComparison.OrdinalIgnoreCase) ||
                    line.IndexOf(BlockChar) >= 0)
                    gotQr = kickedOffAny = true;
                // 登录已过 / 已在枚举 / 已在下载 ⇒ 这一路活着，别再按「卡住」掐它
                else if (LooksLikeLoginSuccess(line) ||
                         line.Contains("Got AppInfo", StringComparison.OrdinalIgnoreCase) ||
                         line.Contains("Processing depot", StringComparison.OrdinalIgnoreCase) ||
                         DepotProgressRx.IsMatch(line))
                    kickedOffAny = true;
                onLine(line);
            });
            var (code, tail) = await RunDepotDownloaderAsync(args, wrapped, ct, () => kickedOffAny,
                kickoffDeadlineMs, stopWhen, onKick: () => manualKicks++);
            lastCode = code;
            lastTail = tail;
            // 授权已经成了（stopWhen 是我们自己掐的进程）→ 别再按「失败」重开一路
            if (stopWhen?.Invoke() == true) return (code, tail);
            // 正常退出、或不是网络类的失败（账号/权限/令牌错）→ 不再重试
            var joined = string.Join("\n", tail);
            var netLoss = LooksLikeNetworkFailure(joined) ||
                          LooksLikeNetworkFailure(tail.Count > 0 ? tail[^1] : "");
            if (code == 0 || !netLoss) return (code, tail);
            // 出过码却仍被 CM 掉线杀掉 ⇒ 那张 challenge 已作废，扫它手机必报「加载二维码失败」。
            // 以前这里 gotQr 就无条件收手，用户只能对着死码 + 一句"连不上"自己关窗重开。
            // 只额外给一次：「用户点了拒绝」在 DD 输出里和掉线没区分度，多刷码会骚扰人。
            if (gotQr)
            {
                if (deadQrRetries >= 1) return (code, tail);
                deadQrRetries++;
                afterDeadQr = true;
            }
            if (attempt == maxAttempts) return (code, tail);
        }
        return (lastCode, lastTail);
    }

    // ------------------------------------------------------------------
    // v1.2.4：给 DD 子进程注入代理（F9）
    //
    // 为什么注入有用（本机实测，别再当成"分流玄学"）：DD 出二维码之前有两段串行网络请求 ——
    //   ① 查 CM 服务器列表：HttpClient → https://api.steampowered.com/ISteamDirectory/...
    //   ② 连上一台 CM：SteamKit2 从列表里挑一个候选，而列表里 **WebSocket 记录占九成**
    //      （12 轮实测首选候选 12 次全是 WS），WS 通道走的是 ClientWebSocket。
    // ①② 都在 HTTP 栈上 ⇒ 都能被一个 HTTP CONNECT 代理承载。实测把 HTTPS_PROXY 指到本地
    // 混合端口后，代理侧同时出现 `CONNECT api.steampowered.com:443` 与
    // `CONNECT cmp*.steamserver.net:2702x` —— 一次注入罩住两段。
    // 只有 netfilter（裸 TCP 27017）那类候选不走代理，但它只占一成。
    //
    // 为什么必须我们替它读注册表：DD 的 SocketsHttpHandler 是 Proxy=null + UseProxy=true，
    // 这种组合下 .NET 只查 **环境变量**（HttpEnvironmentProxy），**不读 Windows 系统代理**。
    // 而 Clash Verge 的「系统代理」模式写的正是注册表（本机实测 ProxyEnable=1/127.0.0.1:7897）——
    // 不替它翻译过去，用户已经在跑的代理对登录链路完全无效。
    // ------------------------------------------------------------------

    /// <summary>把 "127.0.0.1:7890" / "http://127.0.0.1:7890" 这类写法统一成带 scheme 的绝对地址。
    /// 判不了的（空、没有 host、socks 方案）返回 null —— 宁可不注入也不能注入一个必坏的值：
    /// 代理本身可能就是不通的（实测本机 mihomo 能转 steampowered 但转不动 google）。</summary>
    internal static string? NormalizeProxyUrl(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return null;
        if (!s.Contains("://")) s = "http://" + s;
        // socks 喂给 HTTPS_PROXY 时 .NET 的 CONNECT 路径不保证支持，我们只在 HTTP 代理上取到过证据
        if (s.StartsWith("socks", StringComparison.OrdinalIgnoreCase)) return null;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u)) return null;
        if (u.Host.Length == 0) return null;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return null;
        return $"{u.Scheme}://{u.Host}:{u.Port}";
    }

    /// <summary>读 Windows 系统代理（HKCU\...\Internet Settings）。
    /// ProxyServer 有两种形态：简单的 "127.0.0.1:7897"，或 IE 高级设置的分协议
    /// "http=h:p;https=h:p;socks=h:p" —— 取 https → http → 第一个可用的。</summary>
    internal static string? ReadSystemProxy()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser
                .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key is null) return null;
            // ProxyEnable 名义上是 REG_DWORD，但 GetValue 的返回类型随写入方而变，按数字判比按 bool 稳
            if (Convert.ToInt32(key.GetValue("ProxyEnable") ?? 0) == 0) return null;
            var server = key.GetValue("ProxyServer") as string;
            if (string.IsNullOrWhiteSpace(server)) return null;

            if (server.Contains('='))
            {
                string? byProto = null, first = null;
                foreach (var part in server.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var kv = part.Split('=', 2);
                    if (kv.Length != 2) continue;
                    var val = kv[1].Trim();
                    if (val.Length == 0 || val.StartsWith("<-", StringComparison.Ordinal)) continue;
                    first ??= val;
                    if (kv[0].Trim().Equals("https", StringComparison.OrdinalIgnoreCase)) { byProto = val; break; }
                    if (kv[0].Trim().Equals("http", StringComparison.OrdinalIgnoreCase)) byProto ??= val;
                }
                server = byProto ?? first;
            }
            return NormalizeProxyUrl(server);
        }
        catch (Exception ex)
        {
            AppLog.Warn("DepotDownloader", "读系统代理失败（按直连处理）: " + ex.Message);
            return null;
        }
    }

    /// <summary>起 DD 子进程前，把 Windows 系统代理翻译成它听得懂的环境变量。
    /// 刻意不给设置项：用户开着「系统代理」就已经表过态了，我们只是把一个它不读的信号转述过去。
    /// 代价是代理本身是坏的时会被带上 —— 但那种情况下浏览器同样不通，玩家先看到的是别的问题。</summary>
    static void ApplyDdProxyEnv(ProcessStartInfo psi) => ApplyDdProxyEnv(psi, ReadSystemProxy());

    /// <summary>注入本身单独拆一个重载，是为了能在测试台里喂固定地址做断言：
    /// 直接测上面那个的话，结果取决于跑测试这台机器的注册表，换台机器就退化成空断言。
    /// 撤掉这一行注入，F11（假 DD 转储自己拿到的环境）立刻转红。</summary>
    internal static void ApplyDdProxyEnv(ProcessStartInfo psi, string? proxy)
    {
        if (string.IsNullOrEmpty(proxy))
        {
            AppLog.Warn("DepotDownloader", "DD 代理注入 → 无（未开启系统代理，直连 Steam）");
            return;
        }
        // 两个都设：目录请求走 HttpClient、CM 会话走 ClientWebSocket，.NET 在不同入口上读的变量名
        // 不完全一样，少设一个就可能出现「一半流量走了代理、一半直连」这种更难查的状态。
        psi.Environment["HTTPS_PROXY"] = proxy;
        psi.Environment["HTTP_PROXY"] = proxy;
        AppLog.Warn("DepotDownloader", $"DD 代理注入 → {proxy}（来源：系统代理）");
    }

    /// <summary>跑一次 DepotDownloader，逐行回调输出；75 秒无输出判定无响应（扫码等待期自动放宽）。
    /// 整体放线程池执行 —— 内部 TryTake 带超时会阻塞线程，绝不能跑在 Blazor UI 线程上（实测整界面冻结）。</summary>
    private static Task<(int ExitCode, List<string> Tail)> RunDepotDownloaderAsync(
        string args, Action<string> onLine, CancellationToken ct,
        Func<bool>? kickedOff = null, int kickoffDeadlineMs = 0, Func<bool>? stopWhen = null,
        Action? onKick = null)
    {
        return Task.Run(async () =>
        {
            var psi = new ProcessStartInfo
            {
                FileName = DepotDownloaderExe,
                Arguments = args,
                WorkingDirectory = DepotDownloaderDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // 实测：DepotDownloader 重定向输出使用系统 GBK（936）编码
                StandardOutputEncoding = Encoding.GetEncoding(936),
                StandardErrorEncoding = Encoding.GetEncoding(936),
            };
            // DD 不读 Windows 系统代理，代理得由我们塞进它的环境变量（见 ApplyDdProxyEnv）
            ApplyDdProxyEnv(psi);

            using var proc = new Process { StartInfo = psi };
            proc.Start();
            var tail = new List<string>();
            var bc = new BlockingCollection<string>(new ConcurrentQueue<string>());
            ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });

            var pumpOut = PumpDepotAsync(proc.StandardOutput, bc, ct);
            var pumpErr = PumpDepotAsync(proc.StandardError, bc, ct);
            _ = Task.WhenAll(pumpOut, pumpErr).ContinueWith(_ => bc.CompleteAdding(), TaskScheduler.Default);

            var startedAt = Environment.TickCount64;
            var lastLineAt = Environment.TickCount64;
            // 有出码截止或有「拿到授权就收工」的判据时按秒轮询（否则维持原来的单次长阻塞）
            var takeMs = kickoffDeadlineMs > 0 || stopWhen is not null
                ? Math.Min(SilenceTimeoutMs, 1000) : SilenceTimeoutMs;
            var aborted = false;
            var stopAt = 0L;
            try
            {
                while (true)
                {
                    // 用户按了「立即重开」：1 秒内掐掉这一路，别让他等满 20 秒出码截止。
                    // 只在「还在等第一张码」时生效 —— 已经出码或已经在下载时掐进程会打断正经进度，
                    // 而那张码本来每 22 秒自动换一张，不需要手动踢。TakeKick 放最后，
                    // 前面任一条件不成立时不会把这次请求消费掉。
                    if (kickoffDeadlineMs > 0 && !(kickedOff?.Invoke() ?? false) && TakeKick())
                    {
                        aborted = true;
                        try { onKick?.Invoke(); } catch { }
                        try { proc.Kill(entireProcessTree: true); } catch { }
                        break;
                    }
                    // 授权一旦成功，DD 还要自己去枚举 license/appinfo/depot key（实测 4 秒；
                    // 卡住时能一路拖到 300 秒静默上限 —— 07:25:48 那次就是这么把弹窗挂死的）。
                    // 免密票据在打印「Next time you can login with -remember-password」时已落盘，
                    // 所以再等 2.5 秒收尾就掐进程，别让人对着不动的弹窗干等。
                    if (stopAt == 0 && stopWhen?.Invoke() == true) stopAt = Environment.TickCount64 + 2500;
                    if (stopAt != 0 && Environment.TickCount64 >= stopAt)
                    { try { proc.Kill(entireProcessTree: true); } catch { } break; }
                    if (!bc.TryTake(out var line, takeMs))
                    {
                        if (bc.IsCompleted) break;
                        var now = Environment.TickCount64;
                        // 实测 DD 连 Steam CM 单次成功率约 1/3，而它自己那 10 轮退避要烧掉 60 秒；
                        // 到点还没二维码就掐掉重开 —— 新进程等于重新抽一次 CM 列表，比干等退避快得多。
                        if (kickoffDeadlineMs > 0 && !(kickedOff?.Invoke() ?? false)
                            && now - startedAt >= kickoffDeadlineMs)
                        { aborted = true; try { proc.Kill(entireProcessTree: true); } catch { } break; }
                        if (kickoffDeadlineMs > 0 && now - lastLineAt < SilenceTimeoutMs) continue;
                        try { proc.Kill(entireProcessTree: true); } catch { }
                        ct.ThrowIfCancellationRequested();
                        throw new DepotException(LocService.Tr("连接 Steam 长时间无响应（可能令牌失效或网络断开）。") + LoginProxyAdvice());
                    }
                    lastLineAt = Environment.TickCount64;
                    if (line.Length == 0) continue;
                    lock (tail) { tail.Add(line); if (tail.Count > 40) tail.RemoveAt(0); }
                    try { onLine(line); } catch { }
                }
            }
            catch (OperationCanceledException) { }

            await Task.WhenAll(pumpOut, pumpErr);
            try { await proc.WaitForExitAsync(CancellationToken.None); } catch { }
            ct.ThrowIfCancellationRequested();
            if (aborted)
            {
                // 造一条 DD 自己的失败措辞，让外层「网络类失败」判定认出它并立刻重开一路
                lock (tail) tail.Add("Connection to Steam failed (启动器 "
                    + (kickoffDeadlineMs / 1000) + " 秒内没等到出码或登录成功，重开一次)");
                return (-1, tail);
            }
            lock (tail) return (proc.ExitCode, tail);
        }, ct);
    }

    /// <summary>按行泵：换行/回车都视为行分隔（DepotDownloader 的进度条用 \r 刷新）。</summary>
    private static async Task PumpDepotAsync(StreamReader reader, BlockingCollection<string> bc, CancellationToken ct)
    {
        try
        {
            var buf = new StringBuilder();
            var chunk = new char[1024];
            while (true)
            {
                var n = 0;
                try { n = await reader.ReadAsync(chunk, ct); }
                catch { break; }
                if (n <= 0) break;
                for (var i = 0; i < n; i++)
                {
                    var c = chunk[i];
                    if (c == '\n' || c == '\r')
                    {
                        if (buf.Length > 0) { bc.Add(buf.ToString()); buf.Clear(); }
                    }
                    else buf.Append(c);
                }
            }
            if (buf.Length > 0) bc.Add(buf.ToString());
        }
        catch { }
        finally { bc.CompleteAdding(); }
    }
}
