using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace JuniGrid.Services;

/// <summary>
/// 「CM 443 直通」本地中继 —— 扫码出码失败的治本修复。
///
/// <para><b>根因（2026-11-13 本机实测钉死，别再猜别的）</b>：
/// <c>GetCMListForConnect</c> 返回的 Steam 登录中继全是 <c>cmp*.steamserver.net:27015-27025</c>，
/// 国内网络对这批端口是<b>整段 TLS 黑洞</b> —— TCP 能连上（所以旧的"体检"会误判成通），
/// 但 ClientHello 发出去读回 0 字节，SteamKit 每次都死在握手，二维码根本生成不出来。
/// 而<b>同域名的 443 端口完全正常</b>：<c>wss://cmp*.steamserver.net:443/cmsocket/</c>
/// 能拿到 <c>101 Switching Protocols</c>（实测 cmp1/cmp2-hkg1 都行）。</para>
///
/// <para>SteamKit 拼 WebSocket 地址的规则固定是 <c>wss://&lt;host&gt;:&lt;port&gt;/cmsocket/</c>
/// （SteamKit2 <c>WebSocketContext.ConstructUri</c>），端口来自中继列表。所以修复不需要改
/// DepotDownloader —— 只要在 TCP 隧道层把「<c>steamserver.net:270xx</c>」的 CONNECT
/// 改写到「同域名 443」，TLS 与 WSS 握手端到端不动，Steam 服务端看到的就是一次正常的
/// CM 连接（nginx 443 前端本来就为受限网络准备的）。</para>
///
/// <para><b>怎么接进现有链路</b>：DD 子进程的代理本来就由我们注入（<c>HTTPS_PROXY</c>，
/// 见 <see cref="SteamLoginRoute.ApplyEnv"/>）。把中继地址注进去即可：
/// CM 两跳被改写到 443；<c>api.steampowered.com</c> 与 CDN（<c>steamcontent.com</c> 等）
/// 走透传，透传语义刻意与旧默认一致 ——<b>有系统代理就仍走系统代理，没有才直连</b>，
/// 不为了修登录顺手改下载走法（见 <see cref="SteamLoginRoute.SteamBypassDomains"/> 的教训）。</para>
///
/// <para>已验证：同一台机器直连 DD 0 次出码；经本中继 + 官方 DD 3.4.0 直接打印可扫码
/// （PoC 脚本 %TEMP%\cm443_relay.py，测试台另有真网络冒烟）。中继只在 127.0.0.1 上听，
/// 只做字节转发，不碰 TLS 明文，没有凭据落盘。</para>
/// </summary>
public static partial class SteamCm443Relay
{
    /// <summary>Steam CM 中继端口段（GetCMListForConnect 实测 27017-27025；放宽到 27015-27050）。</summary>
    internal const int CmPortMin = 27015;
    internal const int CmPortMax = 27050;
    internal const int WsPort = 443;

    private static readonly object Gate = new();
    private static TcpListener? _listener;
    private static int _port;

    /// <summary>中继是否已就绪（失败时为 null，调用方退回直连/代理路线）。</summary>
    public static string? Url
    {
        get { lock (Gate) return _listener is null ? null : $"http://127.0.0.1:{_port}"; }
    }

    /// <summary>
    /// 确保中继在听（幂等）。返回注入给 DD 的代理地址；起不来返回 null（调用方降级，不许把
    /// 「起不来」伪装成成功 —— 注入一个死代理比不注入更糟）。
    /// </summary>
    public static string? EnsureStarted()
    {
        lock (Gate)
        {
            if (_listener is not null) return $"http://127.0.0.1:{_port}";
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                _port = ((IPEndPoint)listener.LocalEndpoint).Port;
                _listener = listener;
                _ = Task.Run(() => AcceptLoopAsync(listener));
                AppLog.Warn("SteamCm443Relay",
                    $"本地中继已启动 → 127.0.0.1:{_port}（steamserver.net 的 {CmPortMin}-{CmPortMax} 端口会改写到 {WsPort}）");
                return $"http://127.0.0.1:{_port}";
            }
            catch (Exception ex)
            {
                AppLog.Warn("SteamCm443Relay", "本地中继启动失败（退回直连/代理路线）: " + ex.Message);
                return null;
            }
        }
    }

    /// <summary>测试用：停掉中继（正常运行不需要停 —— 它跟着进程走，几十 KB 内存）。</summary>
    internal static void StopForTests()
    {
        lock (Gate)
        {
            try { _listener?.Stop(); } catch { }
            _listener = null;
            _port = 0;
        }
    }

    private static async Task AcceptLoopAsync(TcpListener listener)
    {
        while (true)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(); }
            catch { return; }   // listener 停了（进程退出 / StopForTests）
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    /// <summary>
    /// 处理一条 CONNECT 隧道。三步：读请求行 → 决定上游（改写 or 透传）→ 双向转发字节。
    /// 全程只碰密文字节流，不解 TLS。
    /// </summary>
    private static async Task HandleAsync(TcpClient client)
    {
        TcpClient? upstream = null;
        try
        {
            // 35 秒只罩「读请求 + 拨上游」这一段（含兜底重拨）；双向转发不看这个 token，
            // 隧道建起来后想活多久活多久。
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            var stream = client.GetStream();
            var request = await ReadConnectRequestAsync(stream, cts.Token);
            if (request is null)
            {
                await TryWriteAsciiAsync(stream, "HTTP/1.1 400 Bad Request\r\n\r\n", CancellationToken.None);
                return;
            }
            // DD 对 http CDN 发的是绝对 URI GET（不是 CONNECT）—— 交给智能 CDN 分支；
            // 原先这里一律按 CONNECT 解析 → 400，Relay443 路线下下载全坏（2026-11-16 定位）。
            var firstLine = request.Split('\n')[0].TrimEnd('\r');
            if (!firstLine.StartsWith("CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                await HandlePlainHttpAsync(stream, request, firstLine);
                return;
            }
            if (!TryParseConnect(request, out var host, out var port))
            {
                await TryWriteAsciiAsync(stream, "HTTP/1.1 400 Bad Request\r\n\r\n", CancellationToken.None);
                return;
            }

            var rewrite = IsCmEndpoint(host, port);
            var targetPort = rewrite ? WsPort : port;
            if (rewrite) NoteSeen(host);
            // 改写目标（CM → 443）：直连优先 —— 这条路就是为「代理把 Steam 判死」的网络准备的，
            // 直连不通再借系统代理兜底；透传目标：有系统代理就仍走系统代理（与旧默认一致，别改下载走法）。
            var dialHost = host;
            upstream = rewrite
                ? await DialAsync(dialHost, targetPort, cts.Token)
                  ?? await DialViaSystemProxyAsync(dialHost, targetPort, cts.Token)
                : await DialViaSystemProxyAsync(dialHost, targetPort, cts.Token)
                  ?? await DialAsync(dialHost, targetPort, cts.Token);

            // TryAnotherCM 会把 DD 甩到同池任意一台 CM（2026-11-13 真机实测甩到 cmp1-tyo3，
            // 那台的 443 整台不通）—— 请求的那台连不上就改拨同池已知可达的。换 CM 本来就该落到
            // 活机器上；否则重连必死、二维码链条断掉，用户看到的就是「码一直失效」。
            // 只兜底 CM 改写目标，透传目标（api/CDN）不许换主机。
            if (upstream is null && rewrite)
            {
                foreach (var alt in FallbackCandidates(host))
                {
                    upstream = await DialAsync(alt, targetPort, cts.Token)
                               ?? await DialViaSystemProxyAsync(alt, targetPort, cts.Token);
                    if (upstream is not null)
                    {
                        AppLog.Warn("SteamCm443Relay",
                            $"{host}:{targetPort} 连不上，改拨同池中继 {alt}:{targetPort}（TryAnotherCM 兜底）");
                        dialHost = alt;
                        break;
                    }
                }
            }

            // 第二层兜底：同池没有可拨的（或全死）时，改拨固定种子机房。
            // 触发场景很具体：api.steampowered.com 不通 → DD 退回自带默认 CM（东京那批）→
            // 那批 443 全死 → 池里一台活的都没有。此时只有跨池能把这条路救回来。
            if (upstream is null && rewrite)
            {
                foreach (var alt in CrossPoolCandidates(host))
                {
                    upstream = await DialAsync(alt, targetPort, cts.Token)
                               ?? await DialViaSystemProxyAsync(alt, targetPort, cts.Token);
                    if (upstream is not null)
                    {
                        AppLog.Warn("SteamCm443Relay",
                            $"{host}:{targetPort} 连不上，改拨跨池中继 {alt}:{targetPort}（种子机房兜底）");
                        dialHost = alt;
                        break;
                    }
                }
            }

            if (upstream is null)
            {
                AppLog.Warn("SteamCm443Relay", $"上游连不上：{host}:{port}{(rewrite ? $" → {host}:{WsPort}" : "")}");
                await TryWriteAsciiAsync(stream, "HTTP/1.1 502 Bad Gateway\r\n\r\n", CancellationToken.None);
                return;
            }
            if (rewrite) NoteGood(dialHost);

            await TryWriteAsciiAsync(stream, "HTTP/1.1 200 Connection Established\r\n\r\n", CancellationToken.None);
            var up = upstream.GetStream();
            await Task.WhenAll(PumpAsync(stream, up), PumpAsync(up, stream));
        }
        catch { /* 单条隧道失败不该影响别的连接，也不该冒泡到 AcceptLoop */ }
        finally
        {
            try { client.Close(); } catch { }
            try { upstream?.Close(); } catch { }
        }
    }

    // ── 改写规则（纯函数，钉在测试里）────────────────────────────────────────

    /// <summary>这条 CONNECT 要不要改写到 443：只有 Steam CM 登录中继域 + 270xx 端口段。
    /// 域名按「整段后缀」匹配（.steamserver.net），防 evil-steamserver.net 这类拼写骗过后缀检查。</summary>
    internal static bool IsCmEndpoint(string host, int port)
        => port is >= CmPortMin and <= CmPortMax
           && (host.Equals("steamserver.net", StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(".steamserver.net", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 解析 CONNECT 请求行（<c>CONNECT host:port HTTP/1.1</c>）。IPv6 字面量带方括号。
    /// 不合法返回 false —— 宁可 400 也不猜。
    /// </summary>
    internal static bool TryParseConnect(string requestLine, out string host, out int port)
    {
        host = ""; port = 0;
        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !parts[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase)) return false;
        var hp = parts[1];
        if (hp.StartsWith('['))
        {
            var close = hp.IndexOf(']');
            if (close <= 1) return false;
            host = hp[1..close];
            var rest = hp[(close + 1)..];
            if (rest.StartsWith(':') && int.TryParse(rest[1..], out var p6) && p6 is > 0 and < 65536)
            { port = p6; return true; }
            port = 443;
            return true;
        }
        var idx = hp.LastIndexOf(':');
        if (idx < 0) { host = hp; port = 443; return hp.Length > 0; }
        if (idx == 0) return false;   // ":27023" —— 主机名为空
        host = hp[..idx];
        return int.TryParse(hp[(idx + 1)..], out var p) && p is > 0 and < 65536
            ? (port = p) > 0
            : false;
    }

    // ── 读写原语 ────────────────────────────────────────────────────────────

    /// <summary>读到 <c>\r\n\r\n</c> 为止（CONNECT 请求没有 body）。超限/超时返回 null。</summary>
    private static async Task<string?> ReadConnectRequestAsync(Stream stream, CancellationToken ct)
    {
        var buf = new byte[1024];
        var acc = new StringBuilder();
        while (acc.Length < 16384)
        {
            var n = await stream.ReadAsync(buf, ct);
            if (n <= 0) return null;
            acc.Append(Encoding.ASCII.GetString(buf, 0, n));
            var s = acc.ToString();
            var end = s.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0) return s[..end];
        }
        return null;
    }

    private static async Task TryWriteAsciiAsync(Stream stream, string text, CancellationToken ct)
    {
        try
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
        }
        catch { }
    }

    private static async Task PumpAsync(Stream from, Stream to)
    {
        var buf = new byte[81920];
        try
        {
            while (true)
            {
                var n = await from.ReadAsync(buf);
                if (n <= 0) break;
                await to.WriteAsync(buf.AsMemory(0, n));
                await to.FlushAsync();
            }
        }
        catch { }
    }

    // ── 同池 CM 健康缓存（TryAnotherCM 兜底用）────────────────────────────────
    // CM 列表是一台池子：DD 被 TryAnotherCM 甩到哪台是随机的，而池里有死有活
    // （2026-11-13 实测 cmp1-tyo3:443 整台不通、cmp*-hkg1:443 正常）。
    // 记下「见过的 / 拨通过的」，请求的那台连不上时改拨活的那几台。
    // 只存主机名，不存任何连接内容；进程退出即消失。

    private static readonly object PoolGate = new();
    private static readonly List<string> SeenCms = new();
    private static readonly List<string> GoodCms = new();
    private const int MaxPool = 12;

    internal static void NoteSeen(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return;
        lock (PoolGate)
        {
            SeenCms.RemoveAll(h => h.Equals(host, StringComparison.OrdinalIgnoreCase));
            SeenCms.Insert(0, host);
            if (SeenCms.Count > MaxPool) SeenCms.RemoveRange(MaxPool, SeenCms.Count - MaxPool);
        }
    }

    /// <summary>这台的 443 刚拨通过 —— 兜底时优先用它。</summary>
    internal static void NoteGood(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return;
        NoteSeen(host);
        lock (PoolGate)
        {
            GoodCms.RemoveAll(h => h.Equals(host, StringComparison.OrdinalIgnoreCase));
            GoodCms.Insert(0, host);
            if (GoodCms.Count > MaxPool) GoodCms.RemoveRange(MaxPool, GoodCms.Count - MaxPool);
        }
    }

    /// <summary>
    /// 跨池兜底种子 —— 443 长期可用的几台亚洲机房。
    ///
    /// <para><b>为什么必须有这一层</b>：<see cref="FallbackCandidates"/> 只在「见过 / 拨通过」的主机里挑，
    /// 而 DD 首次连接用的往往是它自带的默认 CM（东京 cellid 170 那批）。池里一台都没通时就没有候选，
    /// CONNECT 直接 502，二维码链条断在第一步。</para>
    ///
    /// <para>2026-11-14 实测的正是这种局面：<c>api.steampowered.com</c> 整段 TCP 443 不通
    /// （拿不到中继列表），<c>cmp1-tyo3:443</c> 全死，而 <c>cmp2/cmp3-hkg1:443</c> 是 101 正常。
    /// CM 之间共用同一套登录前端、同一张 <c>*.steamserver.net</c> 通配证书，把死主机改拨到活机房
    /// 对 Steam 而言就是一次普通的 CM 连接 —— 这是纯客户端能做到的最后一层兜底。</para>
    /// </summary>
    internal static readonly string[] SeedCmHosts =
    {
        "cmp3-hkg1.steamserver.net",
        "cmp2-hkg1.steamserver.net",
        "cmp1-hkg1.steamserver.net",
        "cmp2-sgp1.steamserver.net",
        "cmp1-sgp1.steamserver.net",
    };

    /// <summary>跨池兜底候选：固定种子机房，排除 <paramref name="requested"/> 自己，去重，最多
    /// <paramref name="max"/> 个。<b>只在同池候选全失败后才用</b> —— 同池是「Steam 让你去的那台」，
    /// 优先级永远高于我们猜的机房。</summary>
    internal static List<string> CrossPoolCandidates(string requested, int max = 2)
    {
        var result = new List<string>();
        foreach (var h in SeedCmHosts)
        {
            if (result.Count >= max) break;
            if (h.Equals(requested, StringComparison.OrdinalIgnoreCase)) continue;
            if (result.Any(x => x.Equals(h, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(h);
        }
        return result;
    }

    /// <summary>同池兜底候选：拨通过的在前、见过的在后，排除 <paramref name="requested"/> 自己，
    /// 去重；最多 <paramref name="max"/> 个（每个拨号最多 6+6 秒，别把 CONNECT 阶段拖爆）。</summary>
    internal static List<string> FallbackCandidates(string requested, int max = 3)
    {
        lock (PoolGate)
        {
            var result = new List<string>();
            foreach (var h in GoodCms.Concat(SeenCms))
            {
                if (result.Count >= max) break;
                if (h.Equals(requested, StringComparison.OrdinalIgnoreCase)) continue;
                if (result.Any(x => x.Equals(h, StringComparison.OrdinalIgnoreCase))) continue;
                result.Add(h);
            }
            return result;
        }
    }

    internal static void ResetPoolForTests()
    {
        lock (PoolGate) { SeenCms.Clear(); GoodCms.Clear(); }
    }

    // ── 上游拨号 ────────────────────────────────────────────────────────────

    /// <summary>直连拨号（改写目标的首选路径）。</summary>
    private static async Task<TcpClient?> DialAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token);
            return client;
        }
        catch { return null; }
    }

    /// <summary>借 Windows 系统代理做一次 CONNECT 拨号（代理本身可能不通，失败返回 null）。</summary>
    private static async Task<TcpClient?> DialViaSystemProxyAsync(string host, int port, CancellationToken ct)
    {
        var raw = DepotDownloaderService.ReadSystemProxy();
        if (raw is null || !Uri.TryCreate(raw, UriKind.Absolute, out var proxy)) return null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            var client = new TcpClient();
            await client.ConnectAsync(proxy.Host, proxy.Port, timeout.Token);
            var stream = client.GetStream();
            var req = Encoding.ASCII.GetBytes(
                $"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\nProxy-Connection: Keep-Alive\r\n\r\n");
            await stream.WriteAsync(req, timeout.Token);
            var buf = new byte[256];
            var n = await stream.ReadAsync(buf, timeout.Token);
            if (n <= 0) { client.Close(); return null; }
            var status = Encoding.ASCII.GetString(buf, 0, n);
            if (!status.Contains(" 200", StringComparison.Ordinal)) { client.Close(); return null; }
            return client;
        }
        catch { return null; }
    }
}
