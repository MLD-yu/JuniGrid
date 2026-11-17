using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        // DD 3.x 账密/令牌这两条路都是这么打的：Logging 'oicz73157' into Steam3...
        // 它出现在**验密码之前**，只用来记「这趟是谁」，绝不能当成功判据（见 LooksLikeLoginSuccess）。
        am = Regex.Match(trimmed, @"Logging '(.+?)' into Steam3");
        if (am.Success) return am.Groups[1].Value;
        // 3.x 扫码成功：Success! Next time you can login with -username xxx -remember-password
        am = Regex.Match(trimmed, @"-username\s+(\S+)");
        if (am.Success && !am.Groups[1].Value.StartsWith('-')) return am.Groups[1].Value;
        am = Regex.Match(trimmed, @"Logged in(?: as| via)?\s+'?([A-Za-z0-9_]+)'?", RegexOptions.IgnoreCase);
        if (am.Success) return am.Groups[1].Value;
        return current;
    }

    /// <summary>
    /// 「这一行能证明登录已经过了 Steam 那道关」。
    ///
    /// <para><b>曾经把 <c>"Done!"</c> 也算成功，这是个真伤</b>：DD 连上 CM 就打
    /// <c>Connecting to Steam3... Done!</c>（重试时是另起一行的 <c> Done!</c>），
    /// 那一步跟账号密码一点关系都没有。于是 <c>stopWhen</c> 在「刚连上、还没验密码」时就成立，
    /// 进程被杀（退出码 -1），上层再按 <c>authorized</c> 判成功 —— 随便填什么账号密码都「登录成功」。
    /// 2026-11-16 实测：<c>-username zz_fake_acct_9999 -password definitely_wrong_pw_123</c>
    /// 的输出正是 <c>Connecting to Steam3... Done!</c> → <c>Logging '...' into Steam3...</c> →
    /// <c>InvalidPassword</c>，而前两行都会被旧判据命中。</para>
    ///
    /// <para>只保留**验过之后**才会出现的行：<c>Success! Next time you can login with…</c>、
    /// <c>Got N licenses for account!</c>、<c>Got depot key</c>。</para>
    /// </summary>
    internal static bool LooksLikeLoginSuccess(string trimmed)
    {
        return trimmed.Contains("Success!", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Next time you can login with", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("Got depot key", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("licenses for account", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>RunDepotDownloaderAsync 的专用退出码：DD 要 stdin 输入（要 Steam 密码），
    /// 说明本机没有保存的登录凭据。上层据此直接收手，不再当网络故障重开一路。</summary>
    private const int ExitNeedsCredentials = -2;

    /// <summary>
    /// 凭据缺失时塞进 tail 的标记行。措辞刻意避开 LooksLikeNetworkFailure 认的
    /// Timeout / Trying again 等词 —— 它不是网络问题，别被当成可重试的失败。
    /// <para><b>DD 的凭据放在哪（2026-11-12 实机查清，别再照旧注释猜"安装目录下的 account.config"）：</b>
    /// DD 3.4 的 <c>AccountSettingsStore</c> 用 <c>IsolatedStorageFile.GetUserStoreForAssembly()</c> 落盘，
    /// 实际文件是 <c>%LOCALAPPDATA%\IsolatedStorage\&lt;user&gt;\&lt;id&gt;\Url.&lt;hash&gt;\AssemFiles\account.config</c>，
    /// 那个 <c>Url.&lt;hash&gt;</c> 是<b>按 DepotDownloader.exe 所在路径分桶</b>的。所以「换安装目录 / 重装到别处」
    /// 等于换了个空桶：之前扫的码在这儿根本看不见，DD 便转去要密码。而 <c>cfg.DepotQrLoggedIn</c>
    /// 是应用自己的标记、当然还是 true —— 这正是"界面说已授权、下载却一直失败"的由来。</para>
    /// </summary>
    internal const string CredentialMissingMarker = "[JuniGrid] 本机没有保存的 Steam 登录凭据（需要重新登录授权）";

    /// <summary>
    /// trim 裁剪版 DepotDownloader 的死法（2026-11-13 实测，稳定复现）：protobuf-net 的反射被
    /// 裁剪掉，走通 CM 后 QR 认证必抛 <c>Failed to authenticate with Steam: Property Get method
    /// was not found</c>。安装器旧版优先打包的 <c>DepotDownloader-win-x64-trimmed.zip</c> 就是这种。
    /// 命中它说明<b>换网络路线没用</b> —— 只能把工具换成官方构建（见 RunWithNetworkRetryAsync 的自愈）。
    /// </summary>
    internal const string BrokenTrimmedBuildMarker = "Property Get method was not found";

    /// <summary>DD 找不到保存的登录凭据时会转去 stdin 要密码。这条提示是 Console.Write 打出来的、
    /// **不带换行**，行式泵永远吐不出这一行 —— 只能去读那截还没成行的缓冲。
    /// 判据来自 DD 源码（Program.InitializeSteam）：只有 LoginTokens 里没有这个账号时才会问密码，
    /// 所以命中它就等于「令牌不在」，不是网络问题。</summary>
    internal static bool LooksLikeCredentialPrompt(string text) =>
        text.Contains("Enter account password", StringComparison.OrdinalIgnoreCase)
        || text.Contains("Enter the password for", StringComparison.OrdinalIgnoreCase);

    // ── 账号密码登录（-password 路径）─────────────────────────────────────────
    //
    // 下面这几条判据的文案**不是猜的**：2026-11-14 直接从随包的 DepotDownloader.dll 与
    // SteamKit2.dll 的 UTF-16 字符串表里抠出来的原句 ——
    //   "STEAM GUARD! Please enter your 2-factor auth code from your authenticator app:"
    //   "STEAM GUARD! Please enter the auth code sent to the email at <mail>:"
    //   "Please enter the authentication code sent to your email address:"
    //   "STEAM GUARD! Use the Steam Mobile App to confirm your sign in..."   ← 要的是手机点确认
    //   "The previous 2-factor auth code you have provided is incorrect."
    //   "Unable to login to Steam3: {0}"（{0} 是 EResult 名，如 InvalidPassword）
    //
    // 为什么必须区分「输码」和「手机确认」：默认路是设备批准（DD 轮询等手机点「批准」），
    // 只有用户明确回落输码时才加 -no-mobile。两条路里写 stdin 都只对输码路有用 —— 报错文案完全不同。
    // 所有判据都做成纯函数，钉在 test/JuniGridTestHarness/DepotPasswordLoginTests.cs 里。

    /// <summary>DD/SteamKit 在等用户敲 Steam Guard 验证码（邮箱码或手机令牌码）。
    /// 注意「上一条码是错的」那句里也含 "2-factor auth code"，必须先排除掉，
    /// 否则输入错一次就会把「你上次错了」当成「请再输一次」再弹一次空框。</summary>
    internal static bool LooksLikeGuardCodePrompt(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (LooksLikeGuardCodeRejected(text)) return false;
        return text.Contains("2-factor auth code", StringComparison.OrdinalIgnoreCase)
            || text.Contains("auth code sent to the email", StringComparison.OrdinalIgnoreCase)
            || text.Contains("authentication code sent to your email", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Steam Guard code", StringComparison.OrdinalIgnoreCase)
            // 中文系统上 SteamKit 仍打英文原句，但这几条留着兜住本地化构建/未来改版
            || text.Contains("验证码", StringComparison.Ordinal)
            || text.Contains("授权码", StringComparison.Ordinal);
    }

    /// <summary>上一条 Guard 码被 Steam 判错 —— 要把「已经问过一次」的状态放开，让人能再输。</summary>
    internal static bool LooksLikeGuardCodeRejected(string text) =>
        text.Contains("2-factor auth code you have provided is incorrect", StringComparison.OrdinalIgnoreCase)
        || text.Contains("TwoFactorCodeMismatch", StringComparison.OrdinalIgnoreCase)
        || text.Contains("InvalidLoginAuthCode", StringComparison.OrdinalIgnoreCase);

    /// <summary>Steam 要的是「在手机上批准登录」，不是输码。写 stdin 无效，只能提示用户去看手机。</summary>
    internal static bool LooksLikeMobileConfirmPrompt(string text) =>
        text.Contains("confirm your sign in", StringComparison.OrdinalIgnoreCase);

    /// <summary>设备确认轮询失败（超时/拒绝/会话作废）：DD 把 AuthenticationException 的
    /// Message 原样打出（"Failed to poll status"），EResult 枚举不在句子里 ⇒ 只能认这句。
    /// 配合「进过手机确认流程且没成功」使用，把「批准没完成」和网络失败分开说。</summary>
    internal static bool LooksLikeAuthPollFailed(string text) =>
        !string.IsNullOrEmpty(text)
        && text.Contains("Failed to authenticate with Steam", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 账号密码这一层的「重开没用」类失败：密码错、被限流、被风控。
    /// <para>为什么不能重试：Steam 对连续密码失败有递增冷却，自动重开只会把用户锁得更久。
    /// 所以这里认出就必须**当场上抛**，不许走 RunWithNetworkRetryAsync 的换路重试。</para>
    /// </summary>
    internal static bool LooksLikeBadCredentials(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return text.Contains("InvalidPassword", StringComparison.OrdinalIgnoreCase)
            || text.Contains("AccountLoginDeniedThrottle", StringComparison.OrdinalIgnoreCase)
            || text.Contains("AccountLoginDeniedNeedTwoFactor", StringComparison.OrdinalIgnoreCase)
            || text.Contains("RateLimitExceeded", StringComparison.OrdinalIgnoreCase)
            || text.Contains("LoginDenied", StringComparison.OrdinalIgnoreCase)
            || text.Contains("InvalidLoginAuthCode", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Expired", StringComparison.OrdinalIgnoreCase) && text.Contains("AuthCode", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 给日志用的命令行描述：<c>-password</c> 后面那一项永远打成 <c>***</c>。
    ///
    /// <para>为什么要有这个函数而不是直接不记日志：出问题时「这一路到底带了哪些参数」是
    /// 第一手证据（有没有 -no-mobile、loginid 冲没冲突）。把密码掩掉就能两全。
    /// 掩码判据按**位置**认（前一项是 -password），不按内容猜 —— 密码里出现 "***" 也无所谓。</para>
    /// </summary>
    internal static string DescribeArgsForLog(IReadOnlyList<string> args)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < args.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            var a = args[i];
            if (i > 0 && args[i - 1].Equals("-password", StringComparison.OrdinalIgnoreCase))
                a = "***";
            sb.Append(a.Contains(' ') || a.Contains('"') ? "\"" + a.Replace("\"", "") + "\"" : a);
        }
        return sb.ToString();
    }

    internal static bool LooksLikeNetworkFailure(string text)
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

    /// <summary>「超时」和「连不上」是两种坏法，处置不同，不许混说：
    /// 超时是这条链路慢（重开同一张网也可能成），连不上是中继列表都拿不到。
    /// DD 只有前者会打 timeout 字样；我们自己的截止行带「N 秒内」，一起算超时。</summary>
    internal static bool LooksLikeTimeout(string text) =>
        text.Contains("Timeout", StringComparison.OrdinalIgnoreCase)
        || text.Contains("timed out", StringComparison.OrdinalIgnoreCase)
        || text.Contains("秒内没等到", StringComparison.Ordinal)
        || text.Contains("秒内未拿到", StringComparison.Ordinal);

    /// <summary>网络失败统一补的那半句对策 —— 四处共用，别各自写一遍。
    /// ⚠ 别写成「去 Clash 设 DIRECT / 开 TUN」：实测 DD 的 HttpClient 不读注册表系统代理，
    /// 分流规则对它无效，能动的只有我们替它注入的 HTTPS_PROXY（见 ApplyDdProxyEnv）。</summary>
    static string NetworkRemedy() => LocService.Tr("，换手机热点或换个代理节点再试。");

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
        return LocService.Tr(LooksLikeTimeout(t) ? "连 Steam 中继超时" : "连不上 Steam 登录中继") + NetworkRemedy();
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

        if (File.Exists(DepotDownloaderExe))
        {
            // 打包进来的可能是 framework 构建（目录里没有 coreclr.dll）：本机没有 .NET 9+ 时
            // 那个 exe 根本起不来。与其让用户看到一串看不懂的启动失败，就地换成自包含构建
            // （会走下面的下载逻辑，直连 + GitHub 镜像）。换装失败保留原组件并返回 ——
            // 不许把「换装失败」升级成「工具没了」。
            if (!File.Exists(Path.Combine(DepotDownloaderDir, "coreclr.dll")) && !HasCompatibleDotnetRuntime())
            {
                AppLog.Warn("DepotDownloader", "工具是 framework 构建但本机没有 .NET 9+ 运行时，换装自包含版…");
                try { Directory.Delete(DepotDownloaderDir, true); }
                catch (Exception ex) { AppLog.Warn("DepotDownloader", "换装失败（保留原组件）: " + ex.Message); return; }
            }
            else return;
        }

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

        // 镜像投毒防线：只认 GitHub Release 官方公布的 sha256。
        // 拿不到官方值（API 限流/不通）时记 WARN 放行 —— 与 SelfUpdateService 同一取舍：
        // 宁可让"这次没验"在日志里留痕，也不因为 API 抽风就把所有人挡在门外。
        var wantSha = await TryGetOfficialSha256Async(url, ct);
        if (wantSha is { Length: > 0 })
        {
            var gotSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip))).ToLowerInvariant();
            if (!string.Equals(gotSha, wantSha, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(zip); } catch { }
                throw new DepotException(
                    $"登录组件校验失败（本机 {gotSha[..12]}…，官方 {wantSha[..12]}…）—— 镜像可能被篡改，已丢弃。请稍后重试。");
            }
            AppLog.Warn("DepotDownloader", $"组件 SHA-256 校验通过：{gotSha[..16]}…");
        }
        else
        {
            AppLog.Warn("DepotDownloader", "组件未做 SHA-256 校验（本次没取到官方 digest）—— 仅凭 GitHub 域名与 PK 头放行");
        }

        ZipFile.ExtractToDirectory(zip, DepotDownloaderDir, overwriteFiles: true);
        try { File.Delete(zip); } catch { }
        AppLog.Warn("DepotDownloader", $"DepotDownloader 就绪（{(useFramework ? "framework" : "standalone")}）：" + DepotDownloaderExe);
        }
        finally
        {
            _depotEnsureGate.Release();
        }
    }

    /// <summary>
    /// 把坏掉的工具（旧裁剪版，见 <see cref="BrokenTrimmedBuildMarker"/>）换掉，重下官方构建。
    /// 走的就是 EnsureDepotDownloaderAsync 的既有下载链（直连 + GitHub 镜像 + SHA 校验）。
    ///
    /// <para><b>为什么是「先改名再造、成了才删」而不是直接 Delete</b>：重下要过 GitHub，
    /// 国内经常整个域名不可达（2026-11-13 实测：release 直链 10 秒超时）。先删再下的话，
    /// 一次下载失败就把用户仅有的组件删干净了 —— 之后连「准备扫码组件」都过不去，
    /// 比原来的登录缺陷更糟。这里先把旧目录挪成 .broken，新组件装成了才删备份，装不成再挪回来。</para>
    /// </summary>
    private async Task ReinstallBrokenDepotDownloaderAsync(CancellationToken ct)
    {
        AppLog.Warn("DepotDownloader", $"组件命中裁剪版缺陷（{BrokenTrimmedBuildMarker}），换装官方构建");
        var backup = DepotDownloaderDir.TrimEnd(Path.DirectorySeparatorChar) + ".broken";
        try
        {
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            if (Directory.Exists(DepotDownloaderDir)) Directory.Move(DepotDownloaderDir, backup);
        }
        catch (Exception ex)
        {
            AppLog.Warn("DepotDownloader", "移走坏组件失败，放弃换装: " + ex.Message);
            return;
        }

        try
        {
            await EnsureDepotDownloaderAsync(null, ct);
            if (!File.Exists(DepotDownloaderExe)) throw new DepotException("换装后仍然没有 DepotDownloader.exe");
            try { Directory.Delete(backup, true); } catch { }
        }
        catch (Exception ex)
        {
            AppLog.Warn("DepotDownloader", "重下官方构建失败，恢复原组件: " + ex.Message);
            try
            {
                if (Directory.Exists(DepotDownloaderDir)) Directory.Delete(DepotDownloaderDir, true);
                if (Directory.Exists(backup)) Directory.Move(backup, DepotDownloaderDir);
            }
            catch { }
            throw;
        }
    }

    /// <summary>
    /// 向 GitHub Release API 查这个下载地址对应资产的官方 sha256。
    /// 返回 null = 查不到（限流、不通、API 结构变化）—— 调用方按"没验"处理并留日志。
    ///
    /// <para><b>边界（自述必须带上这条）</b>：这是"取到才校验"——digest 拿到才比对，
    /// 拿不到（api.github.com 匿名限速 / 国内不可达 / 老资产没有 digest 字段）照常放行。
    /// 它治得住「镜像把文件换成别的」，治不住「API 不通时等于没校验」。</para>
    /// </summary>
    private static async Task<string?> TryGetOfficialSha256Async(string downloadUrl, CancellationToken ct)
    {
        var api = ReleaseApiUrlForAsset(downloadUrl, out var fileName);
        if (api is null || fileName is null) return null;

        try
        {
            // API 走同一个 HttpClient（带 UA / 代理），并挂镜像候选 —— 国内直连 api.github.com 常不通
            foreach (var candidate in UpdateService.GithubUrls(api))
            {
                try
                {
                    using var resp = await DepotHttp.GetAsync(candidate, ct);
                    if (!resp.IsSuccessStatusCode) continue;
                    var json = await resp.Content.ReadAsStringAsync(ct);
                    var sha = ExtractDigestFromReleaseJson(json, fileName);
                    if (sha is not null) return sha;
                }
                catch (OperationCanceledException) { throw; }
                catch { /* 这个候选不行，换下一个 */ }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Warn("DepotDownloader", "查询官方 digest 失败（按未校验处理）：" + ex.Message);
        }
        return null;
    }

    /// <summary>下载直链 → Release API 地址。URL 形如
    /// <c>https://github.com/{owner}/{repo}/releases/download/{tag}/{file}</c>，
    /// 换成 <c>https://api.github.com/repos/{owner}/{repo}/releases/tags/{tag}</c>。
    /// 不是这个形状（比如镜像前缀没剥干净、或压根不是 GitHub 直链）⇒ null，调用方按"无从校验"走。
    /// 纯函数，钉在测试里 —— 正则手改一个字符，校验就静默失效。</summary>
    internal static string? ReleaseApiUrlForAsset(string downloadUrl, out string? fileName)
    {
        fileName = null;
        var m = Regex.Match(downloadUrl,
            @"^https://github\.com/([^/]+)/([^/]+)/releases/download/([^/]+)/([^/?#]+)$",
            RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        fileName = Uri.UnescapeDataString(m.Groups[4].Value);
        return $"https://api.github.com/repos/{m.Groups[1].Value}/{m.Groups[2].Value}" +
               $"/releases/tags/{m.Groups[3].Value}";
    }

    /// <summary>从 Release API 响应里取指定资产名的 sha256 digest（小写十六进制，去掉
    /// "sha256:" 前缀）。资产不存在 / 没有 digest 字段 / JSON 不认识 ⇒ null。
    /// GitHub 从 2024 年起才提供 digest，老仓库/老资产没有，属正常情况。
    /// 纯函数，钉在测试里。</summary>
    internal static string? ExtractDigestFromReleaseJson(string json, string assetName)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("assets", out var assets)) return null;
            foreach (var a in assets.EnumerateArray())
            {
                if (!a.TryGetProperty("name", out var n)) continue;
                if (!string.Equals(n.GetString(), assetName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!a.TryGetProperty("digest", out var d)) continue;
                var digest = d.GetString();
                if (string.IsNullOrWhiteSpace(digest)) continue;
                const string prefix = "sha256:";
                if (digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return digest[prefix.Length..].Trim().ToLowerInvariant();
            }
        }
        catch { /* 响应不是合法 JSON（错误页/截断）—— 按查不到处理 */ }
        return null;
    }

    private const char BlockChar = (char)0x2588; // █
    // DD 不同版本可能用 ■ ▀ ▄ 等，一并认作「黑模块」
    private static bool IsQrDarkChar(char c) =>
        c is '█' or '▀' or '▄' or '■' or '▓' or '▒' or '●' or '#' or '@';

    private static bool LooksLikeQrLine(string line) => IsQrRow(line, inBlock: false);

    /// <summary>
    /// 这一行是不是二维码矩阵的一行。
    ///
    /// <para><paramref name="inBlock"/> 为 true 时允许<b>整行全白</b>：DD 打的码上下各带 4 行
    /// 纯空格静区，QR 矩阵内部也可能出现整行全白的数据行。旧判据只认「含黑块」的行，于是这些行
    /// 被当成空行丢掉 —— 2026-11-13 真机日志里就是「29 行 × 37 格」：宽度含左右静区、高度只剩码体，
    /// 行列不方，ZXing 必然解不出来，界面永远停在「正在生成二维码」。</para>
    /// </summary>
    private static bool IsQrRow(string line, bool inBlock)
    {
        if (line.Length < 20) return false;
        var dark = 0;
        foreach (var c in line)
            if (IsQrDarkChar(c)) dark++;
            else if (!char.IsWhiteSpace(c)) return false;
        return inBlock || dark >= 8;
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
            var qrRow = line.TrimEnd('\r');

            // 二维码行必须最先收：静区行整行是空格（trim 后为空），会被下面的「空行忽略」吃掉。
            // 缺行 ⇒ 矩阵错位 ⇒ ZXing 解不出来（2026-11-13 真机就卡在这里，DD 明明已经在出码）。
            if (IsQrRow(qrRow, inBlock: qr.Count > 0))
            {
                if (qr.Count == 0) AppLog.Warn("DepotDownloader", "二维码开始输出（DD ASCII 码）");
                qr.Add(qrRow);
                return;
            }

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

            // 非码行 = 这张码画完了（DD 换码时会打 "The QR code has changed:"，块间不会粘连）
            if (qr.Count >= 20)
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
            else if (qr.Count > 0)
            {
                // 码画到一半来了非码行（多为 DD 报错/换码提示）：残码扫了只会失败，丢掉重收
                qr.Clear();
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

        // 令牌登录同样要连 CM 拿 depot key —— 和扫码一样吃 270xx 黑洞，所以同样走体检路线
        // （体检结果缓存 3 分钟，令牌快路径不会因此多等）。
        LoginRoutePlan routePlan = await SteamLoginRoute.BuildPlanAsync(ct);
        if (!useToken) progress?.Report(new Progress(routePlan.Describe(), null));

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
            stopWhen: () => manifestGiveUp, plan: routePlan);

        // ⚠ 这里**不再**自动回退扫码。唯一的调用方是后台版本下载（VersionDownloadService），
        // onDataUri 是个空委托 —— 回退到 -qr 等于起一路没人看得见二维码的登录：DD 会每 22 秒
        // 刷一张新码（kickedOffAny=true 后静默看门狗永不触发），任务就永远挂在 0%「下载中」。
        // 凭据丢了只能由人去设置页扫一次，这里负责把话说清楚。
        var joinedFail = string.Join("\n", tail);
        if (exitCode == ExitNeedsCredentials
            || (useToken && exitCode != 0 && !HasGameBinary(staging)
                && (joinedFail.Contains("password", StringComparison.OrdinalIgnoreCase)
                    || (failure is not null && failure.Contains("password", StringComparison.OrdinalIgnoreCase)))))
        {
            AppLog.Warn("DepotDownloader",
                $"本机没有可用的 Steam 登录凭据（exit={exitCode}），需要重新登录授权");
            throw new AuthRequiredException(LocService.Tr(
                "Steam 登录凭据不在当前安装目录里 —— 下载器要重新登录一次才能下历史版本" +
                "（重装到别的位置、换过安装目录后都会这样）。" +
                "请到「设置 → Steam 平台」点「登录授权」，登录后再回来点「下载」；" +
                "同一目录下以后再升级都不用重新授权。"));
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
                    "无法下载历史版本。请改用拥有本体的账号在设置里重新登录授权。");
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

        // 只看一眼「是不是已经在收码」（决定全白行算不算码行），不参与结算
        int WatchQrCount() { lock (qrLock) return qr.Count; }

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
            var qrRow = line.TrimEnd('\r');

            // 二维码行必须最先收（含整行全白的静区行）：静区行 trim 后为空，会被「空行忽略」吃掉，
            // 缺行 ⇒ 矩阵错位 ⇒ ZXing 解不出来。2026-11-13 真机就卡在这一步。
            if (IsQrRow(qrRow, inBlock: WatchQrCount() > 0))
            {
                lock (qrLock)
                {
                    if (qr.Count == 0) AppLog.Warn("DepotDownloader", "二维码开始输出（DD ASCII 码）");
                    qr.Add(qrRow);
                }
                ScheduleFlush();
                return;
            }

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

        // 出码之前先体检「直连 / 系统代理」两条路（并行，1～7 秒）。
        // 值这一步的理由：以前每一路都只用注入的那条代理，代理把 Steam 登录域判错时，
        // 重开多少次都是同一个结果 —— 现在按体检结论决定顺序，并且每次尝试换一条路。
        progress?.Report(new Progress(LocService.Tr("正在检查 Steam 连接（直连 / 代理）…"), null));
        var plan = await SteamLoginRoute.BuildPlanAsync(ct);
        progress?.Report(new Progress(plan.Describe(), null));
        // 两条都不通 ⇒ 别拿 6 次 × 20 秒骗人，快速试两轮就把真实原因交回去。
        // 有一路通 ⇒ 6 轮换着走（单路约三成时 P(全失败) 从 1-(2/3)³≈30% 降到 1-(2/3)⁶≈9%，
        // 而且换路能把「代理节点临时挂了 / 直连被临时掐了」这两种情况自动跨过去）。
        var attempts = plan.AnyOk ? 6 : 2;

        progress?.Report(new Progress(LocService.Tr("正在连接 Steam 中继…"), null));
        // 每路到点没出码就重开（新进程等于重新抽一次 CM 列表）。实测出码是双峰的 ——
        // 要成就是 2–5 秒，不成就是 DD 自己 10 轮退避烧满 60 秒，所以截止不会误杀
        // 「本来会成」的那一路。
        var (code, tail) = await RunWithNetworkRetryAsync(
            $"-app 413150 -qr -remember-password -manifest-only -max-downloads 4 -loginid {NextLoginId()}",
            onLine, progress, ct, maxAttempts: attempts, kickoffDeadlineMs: KickoffDeadlineMs,
            stopWhen: () => authorized, plan: plan,
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
                // 把「试过几条路、几条路当时是通的」一起说出来：只说「重试 3 次」等于让人
                // 猜是不是自己网络的问题。这一行进日志后，下次看日志就能知道当时坏在哪一跳。
                var routesTried = plan.Verdicts.Count == 0
                    ? ""
                    : "，" + string.Join("、", plan.Verdicts.Select(v => v.Route.Display + (v.Ok ? "当时通" : "当时就不通")));
                throw new DepotException(
                    LocService.Tf("连不上 Steam 服务器（换了 {0} 次路都失败{1}）", attempts, routesTried)
                    + NetworkRemedy());
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

    /// <summary>Steam 限流（不是密码错）：短时间密码/验证码尝试过多后的临时拒绝。</summary>
    internal static bool LooksLikeRateLimited(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return text.Contains("RateLimitExceeded", StringComparison.OrdinalIgnoreCase)
            || text.Contains("AccountLoginDeniedThrottle", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 账号密码登录（<c>-password</c> 路径）—— 扫码走不通时的备用入口。
    ///
    /// <para><b>为什么需要它</b>：二维码是 <c>https://s.team/q/1/...</c>，手机端扫码后要自己去
    /// 访问 Steam。国内手机没加速器时这一步会一直转圈，用户看到的就是"管理器出码失败"，
    /// 但 PC 端其实一切正常。账密这条路完全不经过手机，是唯一能绕开它的登录方式。</para>
    ///
    /// <para><b>密码怎么传</b>：必须走 <c>-password</c> 参数，不能写 stdin —— DD 的
    /// <c>Util.ReadPassword()</c> 用 <c>Console.ReadKey</c> 逐字符读，重定向输入时直接抛
    /// "Cannot read keys when ... console input has been redirected"（这就是
    /// <c>LooksLikeCredentialPrompt</c> 那条分支存在的原因）。Steam Guard 码不同，那一步是
    /// <c>Console.ReadLine</c>，可以写 stdin（见 <see cref="RunDepotDownloaderAsync"/>）。</para>
    ///
    /// <para><b>秘密的生命周期</b>：密码只作为 <see cref="ProcessStartInfo.ArgumentList"/> 的一项
    /// 存在于本次进程启动前后；不写 config、不写日志（<see cref="DescribeArgsForLog"/> 打码）、
    /// 不进崩溃转储。登录成功后 <c>-remember-password</c> 让 DD 把**令牌**写进它自己的
    /// IsolatedStorage，之后所有下载都走 <c>-username</c> 静默路径，不再需要密码。</para>
    /// </summary>
    /// <param name="requestGuardCode">Steam 要 Guard 码时回调（参数是 DD 的原始提示）。返回 null 表示用户放弃。</param>
    public async Task<string> PasswordLoginOnlyAsync(
        string account, string password, bool tokenLikelyPresent,
        Func<string, CancellationToken, Task<string?>> requestGuardCode,
        IProgress<Progress>? progress = null, CancellationToken ct = default, bool preferCode = false,
        Func<string, Task>? onAuthenticated = null)
    {
        if (string.IsNullOrWhiteSpace(account))
            throw new DepotException(LocService.Tr("请填写 Steam 账号名（登录名，不是昵称）"));
        if (string.IsNullOrEmpty(password))
            throw new DepotException(LocService.Tr("请填写 Steam 密码"));

        // 与下载/扫码共用同一把会话闸：同一账号两路登录会互踢
        using var session = await HoldSteamSessionAsync(ct);
        await EnsureDepotDownloaderAsync(new Progress<string>(msg => progress?.Report(new Progress(msg, null))), ct);
        // 等用户敲 Guard 码的时间算在静默窗口里 —— 人翻手机找码可能要半分钟
        SilenceTimeoutMs = 300000;

        var loginName = account.Trim();
        var singlePass = !tokenLikelyPresent;
        // 顺序与扫码路保持一致（-manifest-only 让 DD 登完就退）。
        // 设备批准是默认路（与官方客户端一致：手机点「批准」直接登录，不用输码）。
        // -no-mobile 会让 DD 的 AcceptDeviceConfirmationAsync 返回 false ⇒ 不轮询批准；
        // 而 Steam 的手机推送在建会话时就发出、拦不掉 ⇒ 点批准没反应、只能输码
        // （2026-11-17 用户报障，DD 3.4.0 ConsoleAuthenticator 源码实锤）。
        // 只有用户在弹窗里明确点「改为输入验证码」时才加它。
        //
        // ⚠ -remember-password 分两趟用，原因见下面那段注释。
        List<string> BuildArgs(bool persist)
        {
            var list = new List<string>
            {
                "-app", "413150",
                "-manifest-only",
                "-max-downloads", "4",
                "-loginid", NextLoginId().ToString(),
                "-username", loginName,
                "-password", password,
            };
            if (preferCode) list.Add("-no-mobile");
            if (persist) list.Add("-remember-password");
            return list;
        }

        progress?.Report(new Progress(LocService.Tr("正在检查 Steam 连接（直连 / 代理）…"), null));
        var plan = await SteamLoginRoute.BuildPlanAsync(ct);
        progress?.Report(new Progress(plan.Describe(), null));

        // ── 两趟登录：第一趟真验密码，第二趟才把免密令牌落盘 ──────────────────
        // 为什么不能一趟搞定：`-remember-password` 在 DD 里不只是「登完把令牌写下来」——
        // 它会让 DD 先去 IsolatedStorage 找这个账号的令牌，**命中就直接用令牌登录，
        // -password 根本不参与校验**。2026-11-16 实测（同一个 exe、同一个账号名，
        // 密码分别填 fake_pw_1/2/3）：带这个参数三趟全是 "Got 57 licenses for account!"；
        // 去掉它立刻变成 InvalidPassword。于是「本机已授权过」的机器上，随便什么密码都显示登录成功。
        // 令牌复用本身是合理的，问题在于用户没法用密码登录来**确认自己填对了**，
        // 也没法用它换到一个密码确实正确的账号上 —— 所以第一趟不带这个参数，逼 DD 认真验。
        async Task<string> RunPassAsync(bool persist, CancellationToken passCt)
        {
            var background = persist && !singlePass;
            var passProgress = background ? null : progress;
            var args = BuildArgs(persist);
            AppLog.Warn("DepotDownloader",
                (singlePass ? "账号密码登录·单趟（验密码+存令牌）参数：" : persist ? "账号密码登录·第二趟（存令牌）参数：" : "账号密码登录·第一趟（验密码）参数：")
                + DescribeArgsForLog(args));

            string? seenAccount = null;
            var authorized = false;
            var guardRejected = false;
            var mobileConfirmSeen = false;

            var onLine = new Action<string>(line =>
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) return;
                AppLog.Warn("DepotDownloader", "DD-pwd: " + trimmed);

                seenAccount = TryParseAccount(trimmed, seenAccount);
                if (LooksLikeLoginSuccess(trimmed))
                {
                    authorized = true;
                    // 成功行必须报给 UI：Guard 弹窗要靠它演「验证通过」洗白，
                    // 手机确认弹窗要靠它立刻收掉（此前这些行被 EngineLineToChinese 漏掉）
                    passProgress?.Report(new Progress(LocService.Tr("Steam 授权成功，正在收尾…"), null, trimmed));
                }

                if (LooksLikeGuardCodeRejected(trimmed))
                {
                    guardRejected = true;
                    passProgress?.Report(new Progress(LocService.Tr("Steam Guard 验证码不正确，请重新输入"), null, trimmed));
                    return;
                }
                if (LooksLikeGuardCodePrompt(trimmed))
                {
                    passProgress?.Report(new Progress(LocService.Tr("需要 Steam Guard 验证码"), null, trimmed));
                    return;
                }
                // 设备确认路（默认）：Steam 要的是手机上点批准，输码无效，弹窗说清楚去点。
                if (LooksLikeMobileConfirmPrompt(trimmed))
                {
                    if (!mobileConfirmSeen)
                    {
                        mobileConfirmSeen = true;
                        guardRejected = false;
                        passProgress?.Report(new Progress(
                            LocService.Tr("这次登录要手机确认：打开手机 Steam App，点「批准登录」…"), null, trimmed));
                    }
                    return;
                }
                if (trimmed.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Contains("FAILED", StringComparison.OrdinalIgnoreCase))
                    passProgress?.Report(new Progress(Shorten(trimmed), null, trimmed));
                else
                {
                    var zh = EngineLineToChinese(trimmed);
                    if (zh is not null) passProgress?.Report(new Progress(zh, null, trimmed));
                }

                static string Shorten(string s) => s.Length > 80 ? s[..80] + "…" : s;
            });

            passProgress?.Report(new Progress(LocService.Tr("正在用账号密码登录 Steam…"), null));

            // maxAttempts=2：只给「网络抖一下」一次机会。真正的密码错/风控已经在
            // RunWithNetworkRetryAsync 里被 LooksLikeBadCredentials 拦下、不会重试。
            // kickoffDeadlineMs=30s：连接阶段熔断 —— DD 自己连 CM 失败会内部退避重试十轮
            // （实测每轮 ~108s，烧掉十几分钟，界面一直「正在登录」）。30s 没有任何真实进展
            // 就掐掉重开换路；「等用户输码/手机批准」不算网络停滞，见 RunDepotDownloaderAsync 的豁免。
            var (code, tail) = await RunWithNetworkRetryAsync(
                "", onLine, passProgress, passCt, maxAttempts: 2, kickoffDeadlineMs: 30000,
                stopWhen: () => authorized, plan: plan,
                askGuard: background ? null : requestGuardCode, argList: args);

            var joined = string.Join("\n", tail);
            if (string.IsNullOrWhiteSpace(seenAccount))
                foreach (var t in tail)
                {
                    seenAccount = TryParseAccount(t, seenAccount);
                    if (!string.IsNullOrWhiteSpace(seenAccount)) break;
                }

            // DD 「再次要码」的固定句式里自带 "previous ... incorrect"，那是提示不是判决：
            // 只要后面出现过验过之后才有的行（authorized），这条就不能算失败。
            if ((guardRejected || LooksLikeGuardCodeRejected(joined)) && !authorized)
                throw new DepotException(LocService.Tr(
                    "Steam Guard 验证码不正确或已过期。重新点一次「账号密码登录」再输新码。"));
            if (LooksLikeBadCredentials(joined))
            {
                AppLog.Warn("DepotDownloader", "账密登录被拒，DD 输出尾部：" + joined);
                // 限流和密码错必须分开说：把 RateLimitExceeded 报成「密码不正确」
                // 会让人反复重输密码/验证码，越试越限（2026-11-16 实机踩坑）
                throw new DepotException(LooksLikeRateLimited(joined)
                    ? LocService.Tr("Steam 临时限制了这次登录（短时间密码/验证码尝试过多），不是密码错：等几分钟再试，或改用扫码登录（扫码不受此限制）。")
                    : LocService.Tr(
                        "Steam 账号或密码不正确。若确认无误，多半是这次登录被 Steam 风控拦了（短时间多次尝试、"
                        + "换网络环境、异地登录都会触发）：等几分钟再试，或改用扫码登录。"));
            }
            if (mobileConfirmSeen && !authorized && LooksLikeAuthPollFailed(joined))
                throw new DepotException(LocService.Tr(
                    "手机批准没有完成：Steam 的确认窗口只有几分钟，超时或在手机上点了拒绝都会这样。重新登录并及时点「批准」，或在弹窗里点「改为输入验证码」。"));
            if (LooksLikeMobileConfirmPrompt(joined) && !authorized)
                throw new DepotException(LocService.Tr(
                    "Steam 要求先在手机 App 上批准这次登录，但确认没有完成。在手机上点「批准」后重试，或改用扫码登录。"));

            // 判据里不许再有「看到账号名就算成功」：`Logging 'x' into Steam3...` 是验密码**之前**
            // 就打出来的。要么见到验过之后才有的行（authorized），要么 DD 自己干净退 0。
            var success = authorized
                          || (code == 0 && !LooksLikeNetworkFailure(joined) && !LooksLikeBadCredentials(joined));
            if (!success)
            {
                if (LooksLikeNetworkFailure(joined))
                {
                    AppLog.Warn("DepotDownloader", "账密登录网络失败，DD 输出尾部：" + joined);
                    throw new DepotException(
                        LocService.Tr(LooksLikeTimeout(joined)
                            ? "连 Steam 中继超时"
                            : "连不上 Steam 登录中继"));
                }
                var hint = tail.LastOrDefault(t => !t.StartsWith("[JuniGrid]", StringComparison.Ordinal)) ?? "";
                throw new DepotException(
                    LocService.Tf("账号密码登录未完成（退出码 {0}）。", code) + hint);
            }

            AppLog.Warn("DepotDownloader",
                $"账号密码登录第{(singlePass ? "单" : persist ? "二" : "一")}趟完成 account={seenAccount ?? "?"} exit={code}");
            return string.IsNullOrWhiteSpace(seenAccount) ? loginName : seenAccount;
        }

        var resolved = await RunPasswordLoginPassesAsync(
            tokenLikelyPresent, RunPassAsync, onAuthenticated, progress, ct);
        progress?.Report(new Progress(LocService.Tr("登录成功"), 100));
        AppLog.Warn("DepotDownloader", $"账号密码登录完成 account={resolved}");
        return resolved;
    }

    internal static async Task<string> RunPasswordLoginPassesAsync(bool tokenLikelyPresent,
        Func<bool, CancellationToken, Task<string>> runPass, Func<string, Task>? onAuthenticated,
        IProgress<Progress>? progress, CancellationToken ct)
    {
        var resolved = await runPass(!tokenLikelyPresent, ct);
        if (onAuthenticated is not null) await onAuthenticated(resolved);
        if (!tokenLikelyPresent) return resolved;

        // 已验证的登录不受窗口取消影响，后台令牌保存单独限时。
        using var persistCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await runPass(true, persistCts.Token);
        }
        catch (Exception ex)
        {
            AppLog.Warn("DepotDownloader", "保存免密登录令牌失败（本次登录已成功，不影响本次授权）：" + ex.Message);
            progress?.Report(new Progress(
                LocService.Tr("登录成功，但免密令牌没存下来；下次下载可能还要再登一次"), null));
        }
        return resolved;
    }


    /// <summary>
    /// 把 DepotDownloader 输出的 ASCII 二维码还原成原始 URL，再用 QRCoder 生成标准 PNG data URI。
    /// 返回 (dataUri, 解码出的 URL)。
    ///
    /// <para><b>为什么先裁白边再补静区</b>：DD 打的码上下各带 4 行纯空格静区，而收集端只认
    /// 「含黑块」的行（纯空格行看起来就是空行）—— 于是矩阵拿到 29 行 × 37 格：宽度含左右静区、
    /// 高度却只有码本身。行列不方，ZXing 必然解不出（2026-11-13 真机：DD 明明打出码了，界面一直
    /// 停在「正在生成二维码」，日志是「28行×37格」）。所以这里不假设外边距存在：
    /// 先裁到「最外一圈黑模块」的内容边界，再统一补规范 4 模块静区 —— 有没有静区行都能解。</para>
    /// </summary>
    internal static (string DataUri, string Url) QrAsciiToDataUriAndUrl(IReadOnlyList<string> lines)
    {
        // 各行等长化（含行尾空白模块）；每模块 1 或 2 字符
        var maxLen = 0;
        foreach (var l in lines) maxLen = Math.Max(maxLen, l.Length);
        if (maxLen < 20) throw new DepotException("二维码行过短，无法解析");

        // 29×29 的 QR 模块宽约 58 字符（2 字符/模块）或 29（1 字符/模块）
        var twoCharPerCell = maxLen >= lines.Count * 1.5;
        var cells = twoCharPerCell ? (maxLen + 1) / 2 : maxLen;
        var dark = new bool[lines.Count, cells];
        for (var r = 0; r < lines.Count; r++)
        {
            var l = lines[r].PadRight(maxLen);
            for (var c = 0; c < cells; c++)
            {
                if (twoCharPerCell)
                {
                    var i0 = c * 2;
                    dark[r, c] = IsQrDarkChar(l[i0]) || (i0 + 1 < l.Length && IsQrDarkChar(l[i0 + 1]));
                }
                else dark[r, c] = IsQrDarkChar(l[Math.Min(c, l.Length - 1)]);
            }
        }

        // 裁到内容边界（去掉四周全白行/列）
        int top = 0, bottom = dark.GetLength(0) - 1, left = 0, right = cells - 1;
        while (top <= bottom && RowIsLight(dark, top, left, right)) top++;
        while (bottom >= top && RowIsLight(dark, bottom, left, right)) bottom--;
        while (left <= right && ColIsLight(dark, left, top, bottom)) left++;
        while (right >= left && ColIsLight(dark, right, top, bottom)) right--;
        if (top > bottom || left > right) throw new DepotException("二维码里没有黑模块，无法解析");

        const int quiet = 4;                      // 规范静区下限
        const int scale = 8;                      // 每模块 8 像素：ZXing 的检测器在 30~40 像素的小图上会认不出定位符
        var rows = bottom - top + 1;
        var cols = right - left + 1;
        var side = Math.Max(rows, cols) + quiet * 2;   // 正方形画布（码必须是方的）
        var px = side * scale;
        var rgb = new byte[px * px * 3];
        rgb.AsSpan().Fill(255);
        var offY = (side - rows) / 2;
        var offX = (side - cols) / 2;
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
            {
                if (!dark[top + r, left + c]) continue;
                for (var dy = 0; dy < scale; dy++)
                {
                    var rowStart = ((offY + r) * scale + dy) * px * 3;
                    for (var dx = 0; dx < scale; dx++)
                    {
                        var i = rowStart + ((offX + c) * scale + dx) * 3;
                        rgb[i] = rgb[i + 1] = rgb[i + 2] = 0;
                    }
                }
            }

        var src = new ZXing.RGBLuminanceSource(rgb, px, px);
        var reader = new ZXing.QrCode.QRCodeReader();
        var result = reader.decode(new ZXing.BinaryBitmap(new ZXing.Common.HybridBinarizer(src)))
                   ?? throw new DepotException($"二维码内容解析失败（内容 {rows}行×{cols}格，行宽{maxLen}）");
        return (UrlToDataUri(result.Text), result.Text);

        static bool RowIsLight(bool[,] m, int r, int from, int to)
        {
            for (var c = from; c <= to; c++) if (m[r, c]) return false;
            return true;
        }

        static bool ColIsLight(bool[,] m, int c, int from, int to)
        {
            for (var r = from; r <= to; r++) if (m[r, c]) return false;
            return true;
        }
    }

    /// <summary>
    /// CM 握手在国内经常秒败。网络类失败自动重试（间隔 2s/4s…）。
    /// 出过码后进程又被掉线杀掉，那张 challenge 已作废 ⇒ 额外重取一次新码（onBeforeRetry
    /// 先让调用方把死码从界面上撤掉、并放开重新出码的开关）。
    /// </summary>
    private async Task<(int ExitCode, List<string> Tail)> RunWithNetworkRetryAsync(
        string? args, Action<string> onLine, IProgress<Progress>? progress, CancellationToken ct,
        int maxAttempts = 5, int kickoffDeadlineMs = 0, Action? onBeforeRetry = null,
        Func<bool>? stopWhen = null, LoginRoutePlan? plan = null,
        Func<string, CancellationToken, Task<string?>>? askGuard = null, IReadOnlyList<string>? argList = null)
    {
        List<string> lastTail = new();
        int lastCode = 1;
        var afterDeadQr = false;
        var deadQrRetries = 0;
        var manualKicks = 0;
        var healedBrokenBuild = false;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            // v1.4.7：每一次尝试换一条路（直连 ⇄ 系统代理）。以前每一路都只用注入的那条代理，
            // 于是「代理把 Steam 登录域判给了一个连不通的节点」时，重开 3 次 / 5 次 / 22 次
            // 结果完全一样 —— 看着像随机失败，其实一次都没换过路。见 SteamLoginRoute。
            var route = plan?.RouteFor(attempt);
            var routeTag = plan is { Routes.Count: > 1 } && route is not null ? $" · {route.Display}" : "";
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
                    ? LocService.Tf("二维码已失效，正在重新获取（第 {0}/{1} 次{2}）…", attempt, maxAttempts, routeTag)
                    : LocService.Tf("连不上 Steam 服务器，换一条路再试（第 {0}/{1} 次{2}）…", attempt, maxAttempts, routeTag), 0));
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
                // 登录已过 / 已在枚举 / 已在下载 / 已在等用户输码 ⇒ 这一路活着，别再按「卡住」掐它
                else if (LooksLikeLoginSuccess(line) ||
                         line.Contains("Got AppInfo", StringComparison.OrdinalIgnoreCase) ||
                         line.Contains("Processing depot", StringComparison.OrdinalIgnoreCase) ||
                         LooksLikeGuardCodePrompt(line) ||
                         DepotProgressRx.IsMatch(line))
                    kickedOffAny = true;
                onLine(line);
            });
            var (code, tail) = await RunDepotDownloaderAsync(args, wrapped, ct, () => kickedOffAny,
                kickoffDeadlineMs, stopWhen, onKick: () => manualKicks++, route: route,
                askGuard: askGuard, argList: argList);
            lastCode = code;
            lastTail = tail;
            // 这一路真的动起来了（出码 / 登录过 / 在下）⇒ 这条路值得下次优先
            if (kickedOffAny && route is not null) SteamLoginRoute.RememberGood(route);
            // 本机没凭据：再开一路只会再要一遍密码，立刻交回上层（上层会说「去重新扫码」）
            if (code == ExitNeedsCredentials) return (code, tail);
            // 授权已经成了（stopWhen 是我们自己掐的进程）→ 别再按「失败」重开一路
            if (stopWhen?.Invoke() == true) return (code, tail);
            var joined = string.Join("\n", tail);
            // 密码错 / 被风控 / Guard 码错：换一条网络路完全没用，而且 Steam 对连续密码失败
            // 有递增冷却 —— 自动重开只会把用户锁得更久。认出就必须当场收手。
            if (LooksLikeBadCredentials(joined) || LooksLikeGuardCodeRejected(joined)) return (code, tail);
            // 坏掉的裁剪版组件（BrokenTrimmedBuildMarker）：这不是网络问题，换一条路还是同一个错 ——
            // 把工具删了重下官方构建再试。只自动修一次；修完还坏就当场说清楚，别再循环撞墙。
            if (joined.Contains(BrokenTrimmedBuildMarker, StringComparison.OrdinalIgnoreCase))
            {
                if (healedBrokenBuild)
                    throw new DepotException(
                        "下载器组件损坏（旧裁剪版的已知缺陷），自动换成完整组件后仍未恢复。" +
                        "请删除安装目录下的 tools\\DepotDownloader 文件夹后重试，或重装 JuniGrid。");
                healedBrokenBuild = true;
                maxAttempts++;   // 组件坏了不是用户的网络问题，不该占掉重试额度
                progress?.Report(new Progress(LocService.Tr("下载器组件有缺陷（旧裁剪版），正在自动换成完整组件…"), null));
                await ReinstallBrokenDepotDownloaderAsync(ct);
                continue;
            }
            // 正常退出、或不是网络类的失败（账号/权限/令牌错）→ 不再重试
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
            if (attempt == maxAttempts)
            {
                // 一整轮都没成 ⇒ 体检结论已经过期（网况变了/节点挂了），下次点重试要重新体检
                SteamLoginRoute.Invalidate();
                return (code, tail);
            }
        }
        SteamLoginRoute.Invalidate();
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
    // 为什么我们还得替它读一次注册表（2026-10-05 修正过理由，别再抄回旧的那句）：
    // 旧注释写的是「.NET 只查环境变量，不读 Windows 系统代理」—— 那在 net10 上已经不成立：
    // 本机实测 HttpClient.DefaultProxy 现在是 System.Net.Http.HttpWindowsProxy，注册表
    // ProxyEnable=1/ProxyServer=127.0.0.1:7897 时 DefaultProxy.GetProxy() 就直接返回它。
    // 保留注入的理由只剩两条：① DD 的 runtimeconfig 写的是 net9.0，跑在 9.x 上时是否也读注册表
    // **没重测过**（本机只有 10.0.12 的 ref pack，测不了 9）；② 环境变量优先级高于注册表，
    // 所以注入的是「我们读到的那个代理」，比留给运行时猜更确定。
    // 注意反面：正因为环境变量压过注册表，注入一个错值会盖掉用户本来能用的代理 —— 宁缺毋滥，
    // 判不了的地址（socks、空）NormalizeProxyUrl 一律返回 null。
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
    /// 刻意不给设置项：用户开着「系统代理」就已经表过态了，我们只是把一个它不读的信号转述过去。</summary>
    static void ApplyDdProxyEnv(ProcessStartInfo psi) => ApplyDdProxyEnv(psi, ReadSystemProxy());

    /// <summary>注入本身单独拆一个重载，是为了能在测试台里喂固定地址做断言：
    /// 直接测上面那个的话，结果取决于跑测试这台机器的注册表，换台机器就退化成空断言。</summary>
    internal static void ApplyDdProxyEnv(ProcessStartInfo psi, string? proxy)
        => SteamLoginRoute.ApplyEnv(psi, LoginRoute.ForProxy(proxy), proxy);

    /// <summary>
    /// 按「这一次要走哪条路」注入。三条写法收在
    /// <see cref="SteamLoginRoute.ApplyEnv"/> 里 —— 因为「直连」在开了系统代理的机器上
    /// 并不是「什么都不设」（.NET 会退回注册表代理），必须显式写 NO_PROXY 白名单。
    /// </summary>
    internal static void ApplyDdProxyEnv(ProcessStartInfo psi, LoginRoute? route)
        => SteamLoginRoute.ApplyEnv(psi, route, ReadSystemProxy());

    /// <summary>跑一次 DepotDownloader，逐行回调输出；75 秒无输出判定无响应（扫码等待期自动放宽）。
    /// 整体放线程池执行 —— 内部 TryTake 带超时会阻塞线程，绝不能跑在 Blazor UI 线程上（实测整界面冻结）。</summary>
    private static Task<(int ExitCode, List<string> Tail)> RunDepotDownloaderAsync(
        string? args, Action<string> onLine, CancellationToken ct,
        Func<bool>? kickedOff = null, int kickoffDeadlineMs = 0, Func<bool>? stopWhen = null,
        Action? onKick = null, LoginRoute? route = null,
        Func<string, CancellationToken, Task<string?>>? askGuard = null, IReadOnlyList<string>? argList = null)
    {
        return Task.Run(async () =>
        {
            var psi = new ProcessStartInfo
            {
                FileName = DepotDownloaderExe,
                WorkingDirectory = DepotDownloaderDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // 标准输入必须接管：DD 在「本机没有令牌」时会去 stdin 要密码。
                // 窗口程序没有控制台，若不接管就会走进 Util.ReadPassword() 的 ReadKey —— 直接崩/卡死，
                // 而外层一行输出都收不到，只能当成网络超时，重试 5 轮后报「连不上 Steam」。
                // ⚠ 接管后**不要立刻 Close()**：实测关掉管道 DD 会在 ReadLine 上抛未处理异常当场崩掉，
                // 连那句提示都还没落到我们手里；留着管道不动它就会停在 ReadLine 上等，提示留在
                // 半截缓冲里，我们 1 秒内就能认出来并掐进程（见下面的 LooksLikeCredentialPrompt）。
                // 账号密码登录时这条管道另有用途：Guard 码那一步 DD 读的是 Console.ReadLine，
                // 可以安全地写进去（与密码不同 —— 密码必须走 -password 参数，见 PasswordLoginOnlyAsync）。
                RedirectStandardInput = true,
                // 实测：DepotDownloader 重定向输出使用系统 GBK（936）编码
                StandardOutputEncoding = Encoding.GetEncoding(936),
                StandardErrorEncoding = Encoding.GetEncoding(936),
                // 输入方向也对齐：Guard 码是 ASCII，但保持同一套编码免得中文用户名之类的扩展字符被截断
                StandardInputEncoding = Encoding.GetEncoding(936),
            };
            // 参数两种给法：既有调用传拼好的字符串（DD 自己的参数里没有用户输入，安全）；
            // 带密码这一路必须走 ArgumentList —— 密码里出现空格 / 双引号 / 结尾反斜杠时，
            // 手拼命令行会被 Win32 的参数解析截断，DD 收到的是半个密码，报的却是「密码错误」。
            if (argList is { Count: > 0 })
            {
                foreach (var a in argList) psi.ArgumentList.Add(a);
            }
            else
            {
                psi.Arguments = args ?? "";
            }
            // DD 不读 Windows 系统代理，代理得由我们塞进它的环境变量（见 ApplyDdProxyEnv）
            ApplyDdProxyEnv(psi, route);

            using var proc = new Process { StartInfo = psi };
            proc.Start();
            var tail = new List<string>();
            var bc = new BlockingCollection<string>(new ConcurrentQueue<string>());
            // 还没成行的那截缓冲：DD 的密码提示不带换行，只能从这里认出来。
            // out / err 各一份 —— 共用一个的话，另一边吐一行就会把提示冲掉。
            var partialOut = new System.Text.StringBuilder();
            var partialErr = new System.Text.StringBuilder();
            object partialOutGate = new(), partialErrGate = new();
            ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });

            var pumpOut = PumpDepotAsync(proc.StandardOutput, bc, ct, partialOut, partialOutGate);
            var pumpErr = PumpDepotAsync(proc.StandardError, bc, ct, partialErr, partialErrGate);
            _ = Task.WhenAll(pumpOut, pumpErr).ContinueWith(_ => bc.CompleteAdding(), TaskScheduler.Default);

            var startedAt = Environment.TickCount64;
            var lastLineAt = Environment.TickCount64;
            // 每秒醒一次：既要盯出码截止，也要盯那截「要密码」的半行缓冲（它永远不会自己成行）
            var takeMs = Math.Min(SilenceTimeoutMs, 1000);
            var aborted = false;
            var needsCredential = false;
            var stopAt = 0L;

            string PartialSnapshot()
            {
                string a, b;
                lock (partialOutGate) a = partialOut.ToString();
                lock (partialErrGate) b = partialErr.ToString();
                return a + "\n" + b;
            }
            void KillForCredentials()
            {
                needsCredential = true;
                try { proc.Kill(entireProcessTree: true); } catch { }
            }

            // ── Steam Guard 验证码（只走账号密码这一路）────────────────────────
            // DD 的那一步读的是 Console.ReadLine，所以可以安全地往 stdin 写一行；
            // **密码不行**：ReadPassword() 用的是 Console.ReadKey，重定向输入时会直接抛
            // "Cannot read keys when ... console input has been redirected" —— 密码只能走
            // -password 参数（见 PasswordLoginOnlyAsync）。
            // 等用户输入的这段时间必须留在别的线程上：行泵还得继续排空，否则 DD 判错后
            // 打出的「上一条码不正确」会堵在管道里，我们就永远问不出第二次。
            //
            // ⚠ 问码请求必须绑进程生命周期（guardCts）：手机批准 / 令牌命中会让登录在
            // 「码还没输」时就完成，进程随即被掐 —— 若不把挂着的问码 Task 放掉，弹窗就成
            // 僵尸：继续问码、输进去写进死进程、毫无反馈，看起来像「随便输什么都成功」。
            // 进程一退（或授权已成）就 Cancel，UI 侧据此立刻收弹窗。
            using var guardCts = new CancellationTokenSource();
            ct.Register(() => { try { guardCts.Cancel(); } catch { } });
            var guardAsked = false;
            void MaybeAskGuard(string text)
            {
                if (askGuard is null || guardAsked || guardCts.IsCancellationRequested) return;
                if (!LooksLikeGuardCodePrompt(text)) return;
                guardAsked = true;
                var prompt = text;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var code = await askGuard(prompt, guardCts.Token).ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(code)) return;   // 用户取消：留给超时兜底
                        if (proc.HasExited) return;                    // 进程已退：写了也白写
                        await proc.StandardInput.WriteLineAsync(code.Trim()).ConfigureAwait(false);
                        await proc.StandardInput.FlushAsync().ConfigureAwait(false);
                        lock (tail) { tail.Add("[JuniGrid] 已提交 Steam Guard 验证码"); }
                    }
                    catch { /* 进程已退 / 管道已关：交给后面的失败判定，不在这里冒泡 */ }
                }, CancellationToken.None);
            }
            try
            {
                while (true)
                {
                    // 第一优先：DD 在等密码输入。这一条不是网络问题，重开一百次也只会再问一遍，
                    // 而且它永远等不到人敲 —— 必须当场收手，别耗到出码截止再当成"连不上"。
                    if (!needsCredential && LooksLikeCredentialPrompt(PartialSnapshot()))
                    { KillForCredentials(); break; }
                    // 其次：在等 Steam Guard 码。这条提示同样可能不带换行（Console.Write），
                    // 只能从半截缓冲里认。判错那句也可能留在半截缓冲里 —— 同样要放开 guardAsked。
                    var snap = PartialSnapshot();
                    if (LooksLikeGuardCodeRejected(snap)) guardAsked = false;
                    MaybeAskGuard(snap);
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
                    if (stopAt == 0 && stopWhen?.Invoke() == true)
                    {
                        stopAt = Environment.TickCount64 + 2500;
                        try { guardCts.Cancel(); } catch { }   // 登录已成：挂着的问码立刻作废
                    }
                    if (stopAt != 0 && Environment.TickCount64 >= stopAt)
                    { try { proc.Kill(entireProcessTree: true); } catch { } break; }
                    if (!bc.TryTake(out var line, takeMs))
                    {
                        if (bc.IsCompleted) break;
                        var now = Environment.TickCount64;
                        // 实测 DD 连 Steam CM 单次成功率约 1/3，而它自己那 10 轮退避要烧掉 60 秒；
                        // 到点还没二维码就掐掉重开 —— 新进程等于重新抽一次 CM 列表，比干等退避快得多。
                        // 豁免「等用户」：半截缓冲里是要码/手机批准提示时，停滞的是人不是网络，
                        // 不能按连接截止掐（账密路 30s 熔断也走这里）。
                        var waitSnap = PartialSnapshot();
                        if (kickoffDeadlineMs > 0 && !(kickedOff?.Invoke() ?? false)
                            && !LooksLikeGuardCodePrompt(waitSnap) && !LooksLikeMobileConfirmPrompt(waitSnap)
                            && now - startedAt >= kickoffDeadlineMs)
                        { aborted = true; try { proc.Kill(entireProcessTree: true); } catch { } break; }
                        // 静默窗口与二维码截止无关：账密那条路没有码可等（kickoffDeadlineMs=0），
                        // 这里若跟着 kickoffDeadlineMs 一起短路，DD 刚启动还一个字都没打时就会被
                        // 判成「长时间无响应」—— 实测 1 秒就误杀，报的还是网络问题。
                        if (now - lastLineAt < SilenceTimeoutMs) continue;
                        try { proc.Kill(entireProcessTree: true); } catch { }
                        ct.ThrowIfCancellationRequested();
                        throw new DepotException(LocService.Tr("连接 Steam 长时间无响应（可能令牌失效或网络断开）") + NetworkRemedy());
                    }
                    lastLineAt = Environment.TickCount64;
                    if (line.Length == 0) continue;
                    // 提示也可能被换行收成一行（有的版本问完会补一个空行），也别漏
                    if (!needsCredential && LooksLikeCredentialPrompt(line))
                    {
                        lock (tail) { tail.Add(line); if (tail.Count > 40) tail.RemoveAt(0); }
                        KillForCredentials();
                        break;
                    }
                    lock (tail) { tail.Add(line); if (tail.Count > 40) tail.RemoveAt(0); }
                    // 输错了要把「已经问过」的状态放开，否则第二次提示进来时会被 guardAsked 拦住，
                    // 用户就只能干等超时（实测 DD 判错后会立刻再问一次，这一步不能漏）。
                    if (LooksLikeGuardCodeRejected(line)) guardAsked = false;
                    MaybeAskGuard(line);
                    try { onLine(line); } catch { }
                }
            }
            catch (OperationCanceledException) { }

            await Task.WhenAll(pumpOut, pumpErr);
            try { await proc.WaitForExitAsync(CancellationToken.None); } catch { }
            try { guardCts.Cancel(); } catch { }   // 进程退了：问码弹窗不许比进程活得久
            ct.ThrowIfCancellationRequested();
            if (needsCredential)
            {
                // 不是网络失败：用专用退出码交回上层，让它直接说「去设置重新扫码」而不是重开一路
                lock (tail) tail.Add(CredentialMissingMarker);
                return (ExitNeedsCredentials, tail);
            }
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

    /// <summary>按行泵：换行/回车都视为行分隔（DepotDownloader 的进度条用 \r 刷新）。
    /// <paramref name="partial"/> 是"还没等到分隔符"的那截原文的镜像 —— DD 追问密码的提示
    /// 用 Console.Write 打出、不带换行，永远成不了一行，上层只能靠它认出来。</summary>
    internal static async Task PumpDepotAsync(StreamReader reader, BlockingCollection<string> bc,
        CancellationToken ct, StringBuilder partial, object partialGate)
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
                        // 只镜像短前缀：密码提示、进度条前缀都在几十字符内，
                        // 攒着整段会把一个长行（分片哈希）也拷进锁里
                        lock (partialGate) { partial.Clear(); }
                    }
                    else
                    {
                        buf.Append(c);
                        lock (partialGate)
                        {
                            if (partial.Length < 512) partial.Append(c);
                        }
                    }
                }
            }
            if (buf.Length > 0) bc.Add(buf.ToString());
        }
        catch { }
        finally { bc.CompleteAdding(); }
    }
}
