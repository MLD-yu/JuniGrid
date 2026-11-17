using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;

namespace JuniGrid.Services;

/// <summary>扫码登录要选的那条网路。</summary>
public enum LoginRouteKind
{
    /// <summary>不经过任何代理，直接连 Steam。</summary>
    Direct,
    /// <summary>经 Windows 系统代理（DD 子进程里就是我们注入的 <c>HTTPS_PROXY</c>）。</summary>
    Proxy,
    /// <summary>
    /// 经「CM 443 直通」本地中继（<see cref="SteamCm443Relay"/>）：
    /// CM 的 270xx 端口在国内是 TLS 黑洞，中继把它们改写到同域名 443 ——
    /// 2026-11-13 实测这是唯一能在纯国内网络出码的一条路。
    /// </summary>
    Relay443,
}

/// <summary>
/// 一条「把 DD 送到 Steam CM」的路。
///
/// <para><b>为什么要把路做成显式的</b>：DD 出二维码之前有两跳 —— 先 HTTP 拿中继列表
/// （<c>api.steampowered.com</c>），再 WebSocket 连一台中继（<c>cmp*.steamserver.net</c>）。
/// 这两跳在 DD 子进程里走哪条路由<b>只由环境变量决定</b>（我们注入什么就走什么，没有代理就退回
/// 系统代理）。而国内最常见的坏法正是「代理把 Steam 登录域判给了直连 / 判给了一个连不通的节点」：
/// 那时 DD 每一路都死在同一跳上，重开多少次都是同一批候选、同一个结果 —— 看上去像"随机失败"，
/// 其实是一次都没真正换过路。所以重试必须以「换路」为单位，而不是以「重开进程」为单位。</para>
///
/// <para>还有一种更普遍的坏法（2026-11-13 实测）：CM 的 270xx 端口被整段 TLS 黑洞，
/// 直连/代理两条路<b>都会</b>死，重试无意义 —— 解法是 <see cref="LoginRouteKind.Relay443"/>
/// 把端口改写到 443。所以候选池里三条路都有才完整。</para>
/// </summary>
public sealed record LoginRoute(LoginRouteKind Kind, string? ProxyUrl)
{
    public static readonly LoginRoute Direct = new(LoginRouteKind.Direct, null);
    public static readonly LoginRoute Relay443 = new(LoginRouteKind.Relay443, null);

    public static LoginRoute? ForProxy(string? proxyUrl)
        => string.IsNullOrWhiteSpace(proxyUrl) ? null : new LoginRoute(LoginRouteKind.Proxy, proxyUrl);

    public string Id => Kind switch
    {
        LoginRouteKind.Direct => "direct",
        LoginRouteKind.Relay443 => "relay443",
        _ => "proxy:" + ProxyUrl,
    };

    public string Display => Kind switch
    {
        LoginRouteKind.Direct => LocService.Tr("直连"),
        LoginRouteKind.Relay443 => LocService.Tr("本地中继 443"),
        _ => LocService.Tf("代理 {0}", ProxyUrl ?? ""),
    };
}

/// <summary>一条路的体检结论：两跳各自通不通。<see cref="Ok"/> 才是「这一路现在能用」。</summary>
public readonly record struct RouteVerdict(LoginRoute Route, bool ApiOk, bool RelayOk, string Detail)
{
    /// <summary>
    /// 这一路现在能不能用。中继路是唯一例外：它<b>不依赖</b>那个中继列表接口 ——
    /// DD 自带一份默认 CM 列表，中继又能把连不上的主机跨池改拨到可达机房
    /// （2026-11-14 实测：<c>api.steampowered.com</c> 的 TCP 443 整段不通，
    /// <c>cmp3-hkg1:443</c> 却依然 101）。这种时候把整条路判死，等于自己关掉唯一能出码的路。
    /// </summary>
    public bool Ok => RelayOk && (ApiOk || Route.Kind == LoginRouteKind.Relay443);
}

/// <summary>
/// 一次登录要用的重试顺序。每次尝试取 <see cref="RouteFor"/> —— 一条路试一路，轮流换。
///
/// <para>为什么不是「挑最好的一条，然后一直用它」：单次成功率本身就只有三成左右
/// （见 <see cref="DepotDownloaderService.KickoffDeadlineMs"/> 的实测注释），而且失败常常是
/// 分钟级的波动。固定一路等于把「三成」当成「一百成」；轮流换路至少把候选池翻倍，
/// 还能在「代理节点临时挂了 / 直连被临时掐了」这两种情况之间自动切换。</para>
/// </summary>
public sealed record LoginRoutePlan(IReadOnlyList<LoginRoute> Routes, IReadOnlyList<RouteVerdict> Verdicts)
{
    /// <summary>第 <paramref name="attempt"/> 次尝试（从 1 起）走哪条路。</summary>
    public LoginRoute RouteFor(int attempt)
    {
        if (Routes.Count == 0) return LoginRoute.Direct;
        // 只在体检通过的路里轮换（2026-11-13 真机教训：把体检死的路轮进去 = 白烧一轮 30 秒，
        // 用户对着「码已失效」干等）。一条都没通时才退回整池 —— 网络抖动时死马当活马医。
        var viable = Routes
            .Where(r => Verdicts.Any(v => v.Ok && v.Route.Id == r.Id))
            .ToList();
        var pool = viable.Count > 0 ? viable : Routes;
        var idx = (attempt - 1) % pool.Count;
        return pool[idx];
    }

    public bool AnyOk => Verdicts.Any(v => v.Ok);

    public bool ApiReachableAnywhere => Verdicts.Any(v => v.ApiOk);

    /// <summary>面板顶部那句话。</summary>
    public string Describe()
    {
        if (Verdicts.Count == 0) return LocService.Tr("没做连通性检查，直接开始。");
        var parts = string.Join(" / ", Verdicts.Select(v => $"{v.Route.Display} {(v.Ok ? "✓" : "✗")}"));
        return AnyOk
            ? LocService.Tf("连接检查：{0} —— 这一轮优先走通的那条", parts)
            : LocService.Tf("连接检查：{0} —— 两条都不通，仍然替你试几轮（网络可能刚好在抖）", parts);
    }
}

/// <summary>
/// 扫码成功率加固：先体检「直连 / 系统代理」两条路，再让每一次重试轮流换路。
///
/// <para>要解决的具体故障（日志实拍）：DD 子进程被注入 <c>HTTPS_PROXY</c> 后，
/// <c>CONNECT cmp2-hkg1.steamserver.net:27020</c> 全部失败，而同一时刻直连是通的。
/// 旧实现无论重开多少路都只用注入的那条代理，所以 3 次、5 次、22 次都一样是 0 出码。</para>
///
/// <para>反过来也成立：有些网络直连 <c>api.steampowered.com</c> 会被 RST，只有经代理才通。
/// 所以不能写死任何一条 —— 必须每次实测后决定顺序。</para>
/// </summary>
public static class SteamLoginRoute
{
    private const string CmListUrl =
        "https://api.steampowered.com/ISteamDirectory/GetCMListForConnect/v1/?cellid=0&format=json";

    /// <summary>
    /// 「直连」这条路在有系统代理的机器上必须显式绕行：.NET 在环境变量缺席时会退回
    /// <c>HttpWindowsProxy</c>（读注册表），也就是说「什么都不设」并不等于直连。
    ///
    /// <para>而 <c>NO_PROXY=*</c> 在 .NET 里<b>不</b>等于全绕过（实测：仍然 IsBypassed=False），
    /// 只有带点前缀的域名后缀才生效。所以这里把域名逐个列出来 —— 少列一个，那一跳就会悄悄
    /// 走回代理，诊断结论也跟着错。</para>
    ///
    /// <para><b>只列登录那两跳，故意不列 CDN</b>（<c>steamcontent.com</c> / <c>akamaihd.net</c> /
    /// <c>himalayanrt.com</c>）：这次要修的故障是「代理转不动 CM、但转 CDN 好好的」
    /// （2026-11-12 实拍就是这样）。把 CDN 也一起绕过去，等于为了修登录顺手改了下载走法 ——
    /// 万一玩家那边直连 CDN 更慢或不通，就成了「扫码好了、下载变慢」这种更难解释的回归。</para>
    /// </summary>
    private static readonly string[] SteamBypassDomains =
    {
        ".steamserver.net",     // CM 中继（扫码成败就在这一跳）
        ".steampowered.com",    // 中继列表接口 + appinfo
    };

    internal static string DirectBypassList => string.Join(",", SteamBypassDomains);

    // 会话级缓存：一次登录流程会问很多次「用哪条路」，但没必要每次都体检
    private static readonly object Gate = new();
    private static LoginRoutePlan? _cached;
    private static long _cachedAt;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(3);

    public static LoginRoutePlan? CachedPlan
    {
        get
        {
            lock (Gate)
                return _cached is not null && Environment.TickCount64 - _cachedAt < (long)CacheTtl.TotalMilliseconds
                    ? _cached : null;
        }
    }

    /// <summary>全部作废（一整轮都失败时调用：下次点重试要重新体检，别拿旧结论骗人）。</summary>
    public static void Invalidate()
    {
        lock (Gate) { _cached = null; _cachedAt = 0; }
    }

    /// <summary>体检全部路线（并行），按「能用的在前、上次成功过的优先」给出重试顺序。</summary>
    public static async Task<LoginRoutePlan> BuildPlanAsync(CancellationToken ct, bool force = false)
    {
        if (!force && CachedPlan is { } hit) return hit;

        var routes = new List<LoginRoute>();
        // 本地中继(443)是纯国内网络唯一能出码的一条路（2026-11-13 实测），排在候选池里；
        // 起不来（端口被占等）就退回老的两条，不许因此整个登录不可用。
        if (SteamCm443Relay.EnsureStarted() is not null) routes.Add(LoginRoute.Relay443);
        routes.Add(LoginRoute.Direct);
        if (LoginRoute.ForProxy(DepotDownloaderService.ReadSystemProxy()) is { } viaProxy)
            routes.Add(viaProxy);

        var verdicts = await Task.WhenAll(routes.Select(r => ProbeAsync(r, ct)));
        var lastGood = ReadLastGood();
        var ordered = verdicts
            // ① 能用的在前
            .OrderByDescending(v => v.Ok)
            // ② 能用的里面，中继(443)永远排第一：它是唯一「端口改写 + 同池兜底」的路。
            //    2026-11-13 真机教训：直接连上了不等于能熬过 TryAnotherCM ——
            //    Steam 出码后 20 秒左右会把 DD 甩到另一台 CM，甩到死机（tyo3）就直接断，
            //    「上次成功过」记的却是 direct，于是下一轮又把它排第一、又死在同一个地方。
            //    直连没有兜底能力，所以它不能靠历史战绩抢中继的位置。
            .ThenByDescending(v => v.Route.Kind == LoginRouteKind.Relay443)
            // ③ 剩下的按上次战绩排
            .ThenByDescending(v => lastGood is not null && v.Route.Id == lastGood)
            .Select(v => v.Route)
            .ToList();

        var plan = new LoginRoutePlan(ordered, verdicts);
        AppLog.Warn("SteamLogin", "登录路线体检 → " + string.Join(" / ",
            verdicts.Select(v => $"{v.Route.Id}={(v.Ok ? "ok" : "fail")}({v.Detail})"))
            + $"；顺序 {string.Join(" → ", ordered.Select(r => r.Id))}");
        lock (Gate) { _cached = plan; _cachedAt = Environment.TickCount64; }
        return plan;
    }

    /// <summary>体检一条路：先要中继列表，再<b>真做一次 WSS 握手</b>（<c>wss://host:port/cmsocket/</c>，
    /// 和 DD/SteamKit 连中继的那一跳一一对应）—— 别拿「裸 TCP 能连上」当体检：
    /// 2026-11-13 实测国内 CM 270xx 是「TCP 通、TLS 黑洞」，裸 TCP 会把死路误判成通。</summary>
    public static async Task<RouteVerdict> ProbeAsync(LoginRoute route, CancellationToken ct)
    {
        // 这条路的「代理语义」翻成 WSS 体检用的代理：直连=显式空代理，代理路=该代理，
        // 中继路=中继地址（它会把 270xx 改写到 443，正好测到 DD 实际走的路径）。
        Uri? wssProxy = route.Kind switch
        {
            LoginRouteKind.Direct => null,
            LoginRouteKind.Proxy when Uri.TryCreate(route.ProxyUrl, UriKind.Absolute, out var p) => p,
            LoginRouteKind.Relay443 when SteamCm443Relay.Url is { } r && Uri.TryCreate(r, UriKind.Absolute, out var rp) => rp,
            _ => null,
        };

        string? body = null;
        string? apiFailure = null;
        try
        {
            using var handler = Handler(route);
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(7) };
            using var resp = await http.GetAsync(CmListUrl, ct);
            if (resp.IsSuccessStatusCode) body = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { apiFailure = "接口不通：" + Short(ex.Message); }

        var targets = new List<string>();
        if (body is not null)
        {
            var (ws, tcp) = SteamConsoleDepot.ParseCmList(body);
            targets = ws.Count > 0 ? ws : tcp;
        }

        // 接口挂了 ≠ 这条路死了。中继路能跨池改拨，而 DD 自己也带一份默认 CM 列表（不走这个接口），
        // 所以中继路改用固定种子机房测 443。2026-11-14 早上实测就是：api 的 TCP 443 全断、
        // cmp3-hkg1:443 依然 101 —— 那时若直接判 fail，唯一能出码的路就被我们自己关掉了。
        var usedSeedPool = false;
        if (targets.Count == 0)
        {
            if (route.Kind != LoginRouteKind.Relay443)
                return new RouteVerdict(route, false, false, apiFailure ?? "中继列表为空");
            targets = SteamCm443Relay.SeedCmHosts
                .Select(h => $"{h}:{SteamCm443Relay.WsPort}")
                .ToList();
            usedSeedPool = true;
        }

        var candidates = targets.Take(4).ToList();
        var checks = new List<Task<(bool Ok, string Hp)>>();
        foreach (var hp in candidates)
        {
            var (host, port) = SteamConsoleDepot.SplitHostPort(hp);
            if (host is null) continue;
            checks.Add(SteamConsoleDepot.CanWssConnectAsync(host, port, wssProxy, ct)
                .ContinueWith(t => (t.Result, hp), TaskScheduler.Default));
        }
        while (checks.Count > 0)
        {
            var done = await Task.WhenAny(checks);
            checks.Remove(done);
            try
            {
                var (ok, hp) = await done;
                if (ok)
                {
                    // 中继路体检走通 = 中继到这台 CM 的 443 是活的 —— 喂进同池健康缓存，
                    // 之后 TryAnotherCM 甩到死机时有兜底可拨（见 SteamCm443Relay.FallbackCandidates）。
                    if (route.Kind == LoginRouteKind.Relay443)
                    {
                        var (h, _) = SteamConsoleDepot.SplitHostPort(hp);
                        if (h is not null) SteamCm443Relay.NoteGood(h);
                    }
                    return new RouteVerdict(route, !usedSeedPool, true,
                        usedSeedPool ? "接口不通，改用内置 CM 池体检（443 可达）" : "中继可达");
                }
            }
            catch { }
        }
        return new RouteVerdict(route, !usedSeedPool, false,
            usedSeedPool ? "接口不通，内置 CM 池也连不上" : $"{candidates.Count} 台中继都连不上");

        static string Short(string s) => s.Length <= 60 ? s : s[..60] + "…";
    }

    private static HttpMessageHandler Handler(LoginRoute route)
    {
        switch (route.Kind)
        {
            case LoginRouteKind.Proxy when route.ProxyUrl is { Length: > 0 } url:
                return new HttpClientHandler { Proxy = new WebProxy(url), UseProxy = true };
            case LoginRouteKind.Relay443 when SteamCm443Relay.Url is { Length: > 0 } relay:
                // 中继地址注入 HttpClient：api.steampowered.com 走透传，与 DD 的走法一致
                return new HttpClientHandler { Proxy = new WebProxy(relay), UseProxy = true };
            default:
                return new HttpClientHandler { UseProxy = false };
        }
    }

    /// <summary>
    /// 把这条件路由翻成 DD 子进程听得懂的环境变量。四种情况四条写法：
    /// <list type="bullet">
    ///   <item>没指定路（老行为）：注入系统代理，和以前一模一样；</item>
    ///   <item>代理路：注入该代理；</item>
    ///   <item>直连路：有系统代理时<b>必须</b>注入 NO_PROXY 白名单绕过，否则 .NET 会退回注册表代理
    ///   （见 <see cref="SteamBypassDomains"/> 的注释）；没有系统代理就什么都不用设；</item>
    ///   <item>中继路（<see cref="SteamCm443Relay"/>）：注入中继地址，全部流量过中继 ——
    ///   CM 270xx 被改写到 443，api/CDN 按「有系统代理走系统代理、否则直连」透传（不改下载走法）。</item>
    /// </list>
    /// </summary>
    internal static void ApplyEnv(ProcessStartInfo psi, LoginRoute? route, string? systemProxy)
    {
        if (route is null)
        {
            WriteProxy(psi, systemProxy, bypass: null);
            AppLog.Warn("DepotDownloader", systemProxy is null
                ? "DD 代理注入 → 无（未开启系统代理，直连 Steam）"
                : $"DD 代理注入 → {systemProxy}（来源：系统代理）");
            return;
        }

        switch (route.Kind)
        {
            case LoginRouteKind.Proxy when route.ProxyUrl is { Length: > 0 } url:
                WriteProxy(psi, url, bypass: null);
                AppLog.Warn("DepotDownloader", $"DD 代理注入 → {url}（按路线体检结果）");
                break;
            case LoginRouteKind.Relay443:
                var relay = SteamCm443Relay.EnsureStarted();
                if (relay is not null)
                {
                    // 全部流量走中继（不留 NO_PROXY）：改写与透传的分流在中继里做，DD 这层只认一个入口。
                    WriteProxy(psi, relay, bypass: null);
                    AppLog.Warn("DepotDownloader",
                        $"DD 代理注入 → {relay}（本地中继：CM 270xx 改写到 443；api/CDN 透传）");
                }
                else
                {
                    // 中继起不来不许硬注一个死代理：退回老行为（系统代理 / 直连）
                    WriteProxy(psi, systemProxy, bypass: null);
                    AppLog.Warn("DepotDownloader",
                        "DD 代理注入 → 本地中继启动失败，退回系统代理行为：" + (systemProxy ?? "无（直连）"));
                }
                break;
            default:
                if (systemProxy is null)
                {
                    WriteProxy(psi, null, bypass: null);
                    AppLog.Warn("DepotDownloader", "DD 代理注入 → 无（直连路线，且本机未开系统代理）");
                }
                else
                {
                    // 关键：代理仍然写上（挡住注册表那条路），同时把 Steam 域名全部绕过
                    WriteProxy(psi, systemProxy, bypass: DirectBypassList);
                    AppLog.Warn("DepotDownloader",
                        $"DD 代理注入 → 直连（绕过 {systemProxy}；NO_PROXY={DirectBypassList}）");
                }
                break;
        }

        static void WriteProxy(ProcessStartInfo psi, string? proxy, string? bypass)
        {
            if (string.IsNullOrEmpty(proxy))
            {
                psi.Environment.Remove("HTTPS_PROXY");
                psi.Environment.Remove("HTTP_PROXY");
                psi.Environment.Remove("NO_PROXY");
                psi.Environment.Remove("no_proxy");
                return;
            }
            // 两个都设：目录请求走 HttpClient、CM 会话走 ClientWebSocket，.NET 在不同入口上读的变量名
            // 不完全一样，少设一个就可能出现「一半流量走了代理、一半直连」这种更难查的状态。
            psi.Environment["HTTPS_PROXY"] = proxy;
            psi.Environment["HTTP_PROXY"] = proxy;
            // NO_PROXY 的大小写在不同运行时/库里都出现过，两个都写；空值必须显式清掉，
            // 否则上一条路的绕过名单会残留在这条路上（同一个 psi 会被复用吗？会 —— 测试台就是这么用的）。
            if (string.IsNullOrEmpty(bypass))
            {
                psi.Environment.Remove("NO_PROXY");
                psi.Environment.Remove("no_proxy");
            }
            else
            {
                psi.Environment["NO_PROXY"] = bypass;
                psi.Environment["no_proxy"] = bypass;
            }
        }
    }

    // ── 「上次走通的是哪条」────────────────────────────────────────────────────
    // 只影响同档内的先后顺序（两条都通时先用上次那条），不参与任何安全判断。
    // 单独一个小文件，不进主配置：它随时可以删掉，删了只是少了点偏好。

    private static string RouteCachePath =>
        Path.Combine(ConfigService.ConfigDir, "steam-login-route.json");

    internal static string? ReadLastGood()
    {
        try
        {
            if (!File.Exists(RouteCachePath)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(RouteCachePath));
            return doc.RootElement.TryGetProperty("last_good", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString() : null;
        }
        catch { return null; }
    }

    internal static void RememberGood(LoginRoute route)
    {
        try
        {
            if (ReadLastGood() == route.Id) return;
            Directory.CreateDirectory(ConfigService.ConfigDir);
            AtomicFile.WriteAllText(RouteCachePath, "{\"last_good\":\"" + route.Id.Replace("\"", "") + "\"}");
            AppLog.Warn("SteamLogin", "记住走通的那条路 → " + route.Id);
        }
        catch (Exception ex) { AppLog.Warn("SteamLogin", "记登录路线失败（不影响使用）: " + ex.Message); }
    }
}
