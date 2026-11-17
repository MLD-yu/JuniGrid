using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JuniGrid.Services;

/// <summary>
/// 「让 Steam 客户端自己下载」这条通道 —— 扫码走不通时的正路。
///
/// <para><b>为什么需要它</b>：DepotDownloader 出二维码之前必须先连上 Steam 的 CM（登录中继，
/// <c>cmp*.steamserver.net</c>）。国内这一段经常整段不通：实测日志里 21 次
/// <c>Connection to Steam failed</c>、0 次出码、0 次登录成功，而且系统代理已经注入进子进程了。
/// 那种情况下「优化扫码界面」没有任何意义 —— 码根本生成不出来。</para>
///
/// <para>而玩家为了玩星露谷，Steam 客户端本来就登录着、本来就连得上 CM（连不上他自己也玩不了）。
/// Steam 控制台自带的 <c>download_depot &lt;app&gt; &lt;depot&gt; &lt;manifest&gt;</c> 用的就是客户端
/// 自己的会话和登录态，完全绕开第三方登录，也不需要玩家把密码交给本程序。它把文件下到
/// <c>&lt;steam&gt;\steamapps\content\app_&lt;app&gt;\depot_&lt;depot&gt;</c>，之后由
/// <see cref="DepotDownloaderService.ImportLocalPackage"/> 认领进版本缓存 ——
/// 也就是说「导入本地包」那条已经存在的路，只是把「从哪来的目录」自动化了。</para>
///
/// <para>这条通道不碰 appmanifest、不锁文件、不写游戏目录，因此也不会出现
/// 「锁了清单 → Steam 更新时报磁盘写入错误」那类副作用。</para>
/// </summary>
public static class SteamConsoleDepot
{
    public const string StardewAppId = "413150";
    public const string StardewDepotId = "413151";

    /// <summary>Steam 控制台命令。manifest 是纯数字串，直接拼；调用方负责先验过
    /// <see cref="IsPlainManifestId"/>（表可能来自网络，别让一个带空格的字符串拼出两条命令）。</summary>
    public static string BuildCommand(string appId, string depotId, string manifestId)
        => $"download_depot {appId} {depotId} {manifestId}";

    internal static bool IsPlainManifestId(string? manifestId)
        => !string.IsNullOrWhiteSpace(manifestId) && manifestId.All(char.IsAsciiDigit);

    /// <summary>Steam 把 content 下载放在哪儿。PCGamingWiki 与实测一致：固定在
    /// <c>&lt;steam 安装目录&gt;\steamapps\content\app_&lt;app&gt;\depot_&lt;depot&gt;</c>，
    /// 与游戏装在哪个库无关。</summary>
    public static string? ContentDir(string? steamRoot, string appId, string depotId)
    {
        if (string.IsNullOrWhiteSpace(steamRoot)) return null;
        return Path.Combine(steamRoot, "steamapps", "content", $"app_{appId}", $"depot_{depotId}");
    }

    /// <summary>Steam 控制台日志在哪：<c>download_depot</c> 的输出（含失败原因）落在这里。
    /// 面板只盯 content 目录「有没有在写」，Steam 那边报错时面板会傻等 —— 读它才能及时认失败。</summary>
    public static string? ConsoleLogPath(string? steamRoot)
        => string.IsNullOrWhiteSpace(steamRoot) ? null : Path.Combine(steamRoot, "logs", "console_log.txt");

    /// <summary>扫 <paramref name="baselineOffset"/> 之后追加的内容里有没有 depot 下载失败行。
    /// 只看追加段：上一次命令留下的旧失败不能算这次的。Steam 持着写句柄，用 ReadWrite 共享读；
    /// 日志被 Steam 截断重开（长度变短）时从头扫 —— 重开后的失败都是新的。</summary>
    public static string? ScanConsoleFailure(string? logPath, long baselineOffset)
    {
        if (string.IsNullOrWhiteSpace(logPath)) return null;
        try
        {
            var fi = new FileInfo(logPath);
            if (!fi.Exists) return null;
            var from = fi.Length < baselineOffset ? 0 : baselineOffset;
            if (fi.Length <= from) return null;
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(from, SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            foreach (var line in sr.ReadToEnd().Split('\n'))
            {
                if (line.Contains("Depot download failed", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("No connection to content servers", StringComparison.OrdinalIgnoreCase))
                    return line.Trim();
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>Steam 客户端在跑吗。控制台命令必须由它执行 —— 没跑就得先让人启动 Steam。</summary>
    public static bool IsSteamRunning()
    {
        try
        {
            var list = Process.GetProcessesByName("steam");
            try { return list.Length > 0; }
            finally { foreach (var p in list) p.Dispose(); }
        }
        catch { return false; }
    }

    /// <summary>打开 Steam 控制台（客户端里的「控制台」标签页）。没启动 Steam 时由它自己拉起。</summary>
    public static bool OpenConsole()
    {
        try
        {
            Process.Start(new ProcessStartInfo("steam://open/console") { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("SteamConsole", "打开 Steam 控制台失败: " + ex.Message);
            return false;
        }
    }

    // ------------------------------------------------------------------
    // 下载进度：Steam 控制台不给机器可读的进度，只能盯目录
    //
    // 判据不是「大小到了多少」（各版本体积不同，而且 1.0 至今差了一倍多），而是
    // **先看见它长过，再看见它停住**：content 目录在命令执行前是空的（或上一次留下的旧包），
    // 所以「相对基线涨过」= 这次真的在写；「连续 N 秒不再涨」= 写完了。
    // 与 DD 那套「退出码 0 + 本体在盘上」殊途同归，区别只是这里没有退出码可看。
    // ------------------------------------------------------------------

    public readonly record struct Snapshot(long Bytes, int Files)
    {
        public string Human => ResumableDownload.FormatBytes(Bytes);
    }

    /// <summary>量一下目录：总字节数 + 文件数。目录不存在 ⇒ (0,0)。</summary>
    public static Snapshot Measure(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return new Snapshot(0, 0);
        try
        {
            var di = new DirectoryInfo(dir);
            if (!di.Exists) return new Snapshot(0, 0);
            long bytes = 0;
            var files = 0;
            foreach (var f in di.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try { bytes += f.Length; files++; } catch { }
            }
            return new Snapshot(bytes, files);
        }
        catch { return new Snapshot(0, 0); }
    }

    /// <summary>这个目录里有没有游戏本体 —— 与 <c>DepotDownloaderService.HasGameBinary</c> 同口径，
    /// 只是放在这里给「下完了没」的判断用（下载途中 dll 会先落地，所以它只能当辅助信号）。</summary>
    public static bool HasGameBinary(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        try
        {
            if (!Directory.Exists(dir)) return false;
            return Directory.EnumerateFiles(dir, "Stardew Valley.dll", SearchOption.AllDirectories).Any()
                || Directory.EnumerateFiles(dir, "Stardew Valley.exe", SearchOption.AllDirectories).Any();
        }
        catch { return false; }
    }

    /// <summary>轮询等 Steam 下完。返回 true = 认作下完了（调用方还要自己校验 + 导入）。
    ///
    /// <para><paramref name="baselineBytes"/> 是命令执行<em>之前</em>量到的体积：</para>
    /// <list type="number">
    ///   <item>只认「比基线涨过」的样本 —— 否则用户上一次下过同版本、目录里本来就躺着完整包时，
    ///   第一次采样就会「静默 N 秒」成立，立刻误判成下完。</item>
    ///   <item>从涨到停那一刻重新计静默窗口 —— Steam 换 chunk 批次之间会有几秒空档。</item>
    /// </list>
    /// </summary>
    public static async Task<bool> WaitForDownloadAsync(
        string? dir, long baselineBytes, Func<Snapshot, Task>? onSample,
        CancellationToken ct,
        int stableSeconds = 20, long minBytes = 64L * 1024 * 1024,
        int pollMs = 2000, TimeSpan? maxWait = null)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        var deadline = DateTime.UtcNow + (maxWait ?? TimeSpan.FromMinutes(90));
        var grew = false;
        var last = baselineBytes;
        var stableSince = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(pollMs, ct); } catch (OperationCanceledException) { return false; }

            var snap = Measure(dir);
            if (snap.Bytes != last)
            {
                // 「变了」而不是「涨了」：换个更小的版本时 Steam 会先删旧文件，体积先掉后涨；
                // 只认上涨的话那一次就永远等不到。静止判据仍然成立（没变化 ⇒ 不进这个分支）。
                grew = true;
                last = snap.Bytes;
                stableSince = DateTime.UtcNow;
            }
            if (onSample is not null)
            {
                try { await onSample(snap); } catch { }
            }
            if (grew && snap.Bytes >= minBytes
                && (DateTime.UtcNow - stableSince).TotalSeconds >= stableSeconds)
                return true;
            if (DateTime.UtcNow > deadline) return false;
        }
        return false;
    }

    // ------------------------------------------------------------------
    // 登录链路自检：扫码为什么失败，分成两种完全不同的情况
    //
    //  ① 连不上 https://api.steampowered.com/ISteamDirectory/...（DD 出码前第一件事）——
    //     拿不到中继列表，后面什么都谈不上。
    //  ② 接口通、列表也拿到了，但列表里的中继一台都连不上 —— 典型是代理按域名分流，
    //     Steam 登录域被丢去直连（本机实测就是这么坏的：CDN 走代理没问题、CM 全灭）。
    //
    // 两种都不该再让人对着二维码干等 60 秒。分清楚了才给得出对策。
    // ------------------------------------------------------------------

    public enum LoginPath
    {
        /// <summary>没测（取消、异常、Steam 接口结构变了）。</summary>
        Unknown,
        /// <summary>接口和中继都通，扫码有机会成。</summary>
        Ok,
        /// <summary>连不上 Steam 登录接口（拿不到中继列表）。</summary>
        ApiUnreachable,
        /// <summary>拿到列表但一台中继都连不上（直连与代理都不通）。</summary>
        RelayUnreachable,
        /// <summary>接口通了但列表是空的。</summary>
        EmptyList,
        /// <summary>直连能连上中继，但按系统代理那条路连不上 —— 也就是 DD 实际会失败的那种。</summary>
        ProxyBlocksRelay,
    }

    public readonly record struct ProbeResult(LoginPath Path, string Detail);

    private const string CmListUrl =
        "https://api.steampowered.com/ISteamDirectory/GetCMListForConnect/v1/?cellid=0&format=json";

    // 自检用独立 HttpClient：短超时，绝不跟下载/更新那条长超时的客户端混用
    private static HttpClient? _probeHttp;
    private static HttpClient ProbeHttp => _probeHttp ??= new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>
    /// 探一次「DD 出码这条路」通不通。
    ///
    /// <para>顺序照抄 DD 的真实路径：先要中继列表（HTTP），再连一台中继（WebSocket）。
    /// <b>连中继必须用真实 WSS 握手，不能用裸 TCP</b>（2026-11-13 实测教训）：国内网络对
    /// CM 的 270xx 端口是「TCP 通、TLS 黑洞」，裸 TCP 体检会一路误判成 Ok，而 DD 每次都死在
    /// 握手 —— 体检和真实故障对不上，等于没诊断。</para>
    ///
    /// <para>另外现在 DD 首选「本地中继(443)」路线（见 <see cref="SteamCm443Relay"/>），
    /// 所以自检先测那条 —— 否则体检说「走不通」、扫码其实能成，两头打架。</para>
    /// </summary>
    public static async Task<ProbeResult> ProbeLoginPathAsync(CancellationToken ct)
    {
        string? body = null;
        try
        {
            using var resp = await ProbeHttp.GetAsync(CmListUrl, ct);
            if (resp.IsSuccessStatusCode) body = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            AppLog.Warn("SteamConsole", "自检：连不上 Steam 登录接口 —— " + ex.Message);
            return new ProbeResult(LoginPath.ApiUnreachable, ex.Message);
        }
        if (body is null)
            return new ProbeResult(LoginPath.ApiUnreachable, "接口没有正常响应");

        var (ws, tcp) = ParseCmList(body);
        var targets = ws.Count > 0 ? ws : tcp;
        if (targets.Count == 0)
            return new ProbeResult(LoginPath.EmptyList, "接口返回的中继列表是空的");

        var candidates = targets.Take(3).ToList();
        var proxy = SystemProxyUri();

        // ⓪ DD 首选的「本地中继(443)」：把 270xx 改写到 443 那条 —— 先测它，
        //    体检结论才和 DD 实际会走的路一致。
        if (SteamCm443Relay.EnsureStarted() is { } relayUrl
            && Uri.TryCreate(relayUrl, UriKind.Absolute, out var relayUri)
            && await AnyConnectedAsync(candidates, relayUri, ct))
            return new ProbeResult(LoginPath.Ok, "经本地中继（CM 端口改写到 443）");

        // ① 回退路：有系统代理就先经代理连（老路线注入的就是系统代理）
        if (proxy is not null)
        {
            var viaProxy = await AnyConnectedAsync(candidates, proxy, ct);
            if (viaProxy) return new ProbeResult(LoginPath.Ok, $"经代理 {proxy.Host}:{proxy.Port}");
            // ② 代理不通再看直连：直连通 ⇒ 是代理把 Steam 中继拦了，而不是网络到不了
            if (await AnyConnectedAsync(candidates, null, ct))
                return new ProbeResult(LoginPath.ProxyBlocksRelay, proxy.Host + ":" + proxy.Port);
            return new ProbeResult(LoginPath.RelayUnreachable, $"试了 {candidates.Count} 台中继，中继/代理/直连都不通");
        }

        if (await AnyConnectedAsync(candidates, null, ct))
            return new ProbeResult(LoginPath.Ok, candidates[0]);
        return new ProbeResult(LoginPath.RelayUnreachable, $"试了 {candidates.Count} 台中继都连不上");
    }

    /// <summary>读 Windows 系统代理，形状不对（socks/空）就当没有 —— 与
    /// <see cref="DepotDownloaderService.ReadSystemProxy"/> 同源，这里只是换成 Uri 方便 CONNECT。</summary>
    private static Uri? SystemProxyUri()
    {
        try
        {
            var raw = DepotDownloaderService.ReadSystemProxy();
            return raw is not null && Uri.TryCreate(raw, UriKind.Absolute, out var u) ? u : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// 并发用<b>真实 WSS 握手</b>试这批候选，任一台成功即返回 true。并发是为了别让自检把面板卡上二十秒。
    ///
    /// <para>判据从裸 TCP 换成 WSS 握手（<c>wss://host:port/cmsocket/</c>，SteamKit 的固定路径）：
    /// 2026-11-13 实测国内对 CM 270xx 是「TCP 通、TLS 黑洞」，裸 TCP 会把死路误判成通。</para>
    /// </summary>
    private static async Task<bool> AnyConnectedAsync(IReadOnlyList<string> targets, Uri? proxy, CancellationToken ct)
    {
        var tasks = new List<Task<bool>>();
        foreach (var hp in targets)
        {
            var (host, port) = SplitHostPort(hp);
            if (host is null) continue;
            tasks.Add(CanWssConnectAsync(host, port, proxy, ct));
        }
        while (tasks.Count > 0)
        {
            var done = await Task.WhenAny(tasks);
            tasks.Remove(done);
            try { if (await done) return true; } catch { }
        }
        return false;
    }

    /// <summary>
    /// 真连一次 Steam CM 的 WebSocket 端点（<c>wss://host:port/cmsocket/</c>），
    /// 握手成功即返回 true 并立刻断开。这就是 SteamKit/DepotDownloader 连中继的那一跳。
    /// <paramref name="proxy"/>：null = 强制直连（显式空 WebProxy，防 .NET 退回注册表系统代理）；
    /// 给 Uri = 经该 HTTP 代理 CONNECT。
    /// </summary>
    internal static async Task<bool> CanWssConnectAsync(string host, int port, Uri? proxy, CancellationToken ct)
    {
        try
        {
            using var ws = new System.Net.WebSockets.ClientWebSocket();
            // null ≠ 不走代理（.NET 默认会读系统代理），必须显式给空 WebProxy 才是真直连
            ws.Options.Proxy = proxy is null ? new System.Net.WebProxy() : new System.Net.WebProxy(proxy);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await ws.ConnectAsync(new Uri($"wss://{host}:{port}/cmsocket/"), timeout.Token);
            return ws.State == System.Net.WebSockets.WebSocketState.Open;
        }
        catch { return false; }
    }

    /// <summary>解析 <c>GetCMListForConnect</c> 的响应。取 WebSocket 那批（DD 走的就是它们，占九成），
    /// 没有时回落到其余类型。纯函数，钉在测试里。
    ///
    /// <para><b>两种元素形状都要认</b>（2026-11-13 实测）：老结构是字符串 <c>"host:port"</c>，
    /// 现在（至少 <c>cellid=0</c>）返回的是对象
    /// <c>{"endpoint":"cmp2-hkg1.steamserver.net:27020","type":"websockets",…}</c>。
    /// 只认字符串的话，接口明明通了也会解析出空列表 —— 自检就会误报成「中继列表是空的」，
    /// 把「网络不通」和「我解析错了」混成一句话，等于没诊断。</para></summary>
    internal static (List<string> WebSocket, List<string> Tcp) ParseCmList(string json)
    {
        var ws = new List<string>();
        var tcp = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("response", out var resp)) return (ws, tcp);
            Read(resp, "serverlist_websockets", ws, tcp, stringsToWs: true);
            Read(resp, "serverlist", ws, tcp, stringsToWs: false);
        }
        catch { /* 不是合法 JSON（错误页 / 截断）—— 当空列表 */ }

        // 两个字段可能报同一批中继，去重后才好拿去连
        static List<string> Distinct(List<string> a) =>
            a.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return (Distinct(ws), Distinct(tcp));

        static void Read(JsonElement resp, string name, List<string> ws, List<string> tcp, bool stringsToWs)
        {
            if (!resp.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
            foreach (var e in arr.EnumerateArray())
            {
                switch (e.ValueKind)
                {
                    case JsonValueKind.String:
                        var s = e.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) (stringsToWs ? ws : tcp).Add(s!);
                        break;
                    case JsonValueKind.Object:
                        // 老结构的对象形状：endpoint + type（type 决定它走 WebSocket 还是裸 TCP）
                        var endpoint = Str(e, "endpoint") ?? Str(e, "legacy_endpoint");
                        if (string.IsNullOrWhiteSpace(endpoint)) continue;
                        var type = Str(e, "type") ?? "";
                        (type.Contains("websocket", StringComparison.OrdinalIgnoreCase) ? ws : tcp).Add(endpoint!);
                        break;
                }
            }
        }

        static string? Str(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    /// <summary>"host:port" / "[::1]:443" 拆开。拆不出端口就按 443。</summary>
    internal static (string? Host, int Port) SplitHostPort(string hostPort)
    {
        if (string.IsNullOrWhiteSpace(hostPort)) return (null, 0);
        var s = hostPort.Trim();
        var idx = s.LastIndexOf(':');
        if (idx <= 0) return (s, 443);
        var host = s[..idx].Trim('[', ']');
        return int.TryParse(s[(idx + 1)..], out var port) && port is > 0 and < 65536
            ? (host, port)
            : (s, 443);
    }

    internal static async Task<bool> CanConnectAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            await client.ConnectAsync(host, port, timeout.Token);
            return client.Connected;
        }
        catch { return false; }
    }

    /// <summary>经 HTTP 代理做一次 CONNECT 握手 —— 这就是 DD 子进程（被注入了 HTTPS_PROXY）实际走的那一跳。
    /// 仅测 TCP 到代理端口是不够的：代理活着、但把 Steam 的域名判成直连（或被规则拒绝）时，
    /// CONNECT 会返回非 200，而 DD 拿到的表现就是连不上中继。</summary>
    internal static async Task<bool> CanConnectViaProxyAsync(Uri proxy, string host, int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(proxy.Host, proxy.Port, timeout.Token);
            await using var stream = client.GetStream();
            var req = System.Text.Encoding.ASCII.GetBytes(
                $"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\nProxy-Connection: Keep-Alive\r\n\r\n");
            await stream.WriteAsync(req, timeout.Token);
            var buf = new byte[256];
            var n = await stream.ReadAsync(buf, timeout.Token);
            if (n <= 0) return false;
            var status = System.Text.Encoding.ASCII.GetString(buf, 0, n);
            return status.Contains(" 200", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    /// <summary>把自检结论翻成一句人话（面板顶部那行）。<paramref name="detail"/> 只在需要时拼上。</summary>
    public static string DescribeProbe(ProbeResult r) => r.Path switch
    {
        LoginPath.Ok => LocService.Tr("自检：Steam 登录链路是通的，扫码多半能成 —— 想登录也可以去设置里点「登录授权」。"),
        LoginPath.ApiUnreachable => LocService.Tr(
            "自检：本机连不上 Steam 登录接口（api.steampowered.com），所以二维码根本生成不出来。" +
            "这是网络/代理的问题，重试扫码没用 —— 下面这条路不依赖它。"),
        LoginPath.ProxyBlocksRelay => LocService.Tr(
            "自检：直连能连上 Steam 中继，走你的系统代理连不上 —— 扫码时会自动改用直连（不用你手动关代理）；" +
            "如果扫码仍然反复失败，用下面这条路更省事。"),
        LoginPath.RelayUnreachable => LocService.Tr(
            "自检：能拿到 Steam 中继列表，但一台都连不上（代理和直连都不通）。" +
            "扫码这条路走不通，用下面这条。"),
        LoginPath.EmptyList => LocService.Tr(
            "自检：Steam 登录接口回了空的中继列表，扫码这条路暂时走不通，用下面这条。"),
        _ => LocService.Tr("自检没测出结果（不影响下面这条路）。"),
    };
}
