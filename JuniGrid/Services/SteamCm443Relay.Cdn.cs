using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace JuniGrid.Services;

/// <summary>
/// 中继的 CDN 智能代理分支 —— 「国内下载时好时坏」的治本层。
///
/// <para><b>两个根因（2026-11-16 探针实测钉死）</b>：
/// ① 官方目录（IContentServerDirectoryService/GetServersForSteamPipe）给国内 IP 发的是
/// 境内合作 CDN 边缘（clngaa/eccdnx/pphimalayanrt 等），里面有活有死、还有 https 证书坏的：
/// DD 自己抽签，抽到死的/证书坏的就整包失败 —— 边缘粒度的「时好时坏」。
/// ② 中继原先只认 CONNECT；DD 对 http CDN 发的是绝对 URI GET，被中继 400 ——
/// Relay443 路线下「扫码好了、下载全坏」。</para>
///
/// <para>本分支接管绝对 URI 的 GET/HEAD：按健康度选边缘（剔死、避坏证书、跟随边缘 302 含跨域、
/// 失败换源），把响应原样回给 DD；每次尝试落 jsonl（host/边缘/状态/字节/毫秒），
/// 既是修复也是「境内边缘能 serve 真实 chunk」的对照证据。CONNECT 隧道一字不动。</para>
/// </summary>
public static partial class SteamCm443Relay
{
    private static readonly object CdnGate = new();
    private static readonly Dictionary<string, CdnHealth> CdnPool = new(StringComparer.OrdinalIgnoreCase);
    private static List<string> _directoryHosts = new();
    private static Timer? _directoryTimer;

    private sealed class CdnHealth
    {
        public int ConsecutiveBad;
        public long LastGoodMs = long.MaxValue;
        public long BadUntil;
    }

    /// <summary>是不是 CDN/内容域（决定走智能选边还是原样透传）。后缀整段匹配防拼写骗过。</summary>
    internal static bool IsCdnHost(string host)
    {
        string[] suffixes =
        {
            ".steamcontent.com", ".steamserver.net", ".akamaihd.net", ".edge.steam-dns.top.comcast.net",
            ".clngaa.com", ".eccdnx.com", ".pphimalayanrt.com", ".wscdns.com", ".8686c.com",
        };
        foreach (var s in suffixes)
            if (host.EndsWith(s, StringComparison.OrdinalIgnoreCase) || host.Equals(s[1..], StringComparison.OrdinalIgnoreCase))
                return true;
        // 目录发下来的边缘一律算 CDN（哪怕域名没见过）
        lock (CdnGate) return _directoryHosts.Any(h => h.Equals(host, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 选边顺序：请求的那台没在冷却就排第一（Steam 让去的那台优先），其余按「最近成功延迟」升序、
    /// 冷却中的排最后。纯函数，钉在测试里。
    /// </summary>
    internal static List<string> OrderCdnCandidates(string requested, IReadOnlyList<string> pool,
        IReadOnlyDictionary<string, (int Bad, long GoodMs, long BadUntil)> health, long now)
    {
        var alive = new List<(string Host, long GoodMs, bool Cooling)>();
        foreach (var h in pool.Append(requested).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            health.TryGetValue(h, out var st);
            alive.Add((h, st.GoodMs, st.BadUntil > now));
        }
        return alive
            .OrderBy(x => x.Cooling ? 1 : 0)
            .ThenBy(x => x.Host.Equals(requested, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(x => x.GoodMs)
            .Select(x => x.Host)
            .ToList();
    }

    internal static void NoteCdnResult(string host, bool ok, long ms)
    {
        lock (CdnGate)
        {
            if (!CdnPool.TryGetValue(host, out var h)) CdnPool[host] = h = new CdnHealth();
            if (ok)
            {
                h.ConsecutiveBad = 0;
                h.BadUntil = 0;
                h.LastGoodMs = ms;
            }
            else
            {
                h.ConsecutiveBad++;
                // 连坏 2 次冷却 60 秒；再坏翻倍（上限 5 分钟），别在死边缘上反复烧时间
                if (h.ConsecutiveBad >= 2)
                    h.BadUntil = Environment.TickCount64 + Math.Min(300_000, 60_000 * (h.ConsecutiveBad - 1));
            }
        }
    }

    private static void EnsureDirectoryRefresh()
    {
        if (_directoryTimer is not null) return;
        _directoryTimer = new Timer(_ => _ = RefreshDirectoryAsync(), null, 0, 300_000);
    }

    private static async Task RefreshDirectoryAsync()
    {
        try
        {
            var hosts = new List<string>();
            foreach (var payload in new[]
                     {
                         "{\"cell_id\":0,\"max_servers\":12,\"current_connections\":[]}",
                         "{\"cell_id\":0,\"max_servers\":12,\"current_connections\":[],\"ip_override\":\"114.114.114.114\"}",
                     })
            {
                var url = "https://api.steampowered.com/IContentServerDirectoryService/GetServersForSteamPipe/v1/?format=json&input_json="
                          + Uri.EscapeDataString(payload);
                var json = await FetchDirectoryJsonAsync(url);
                if (json is null) continue;
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("response", out var r)
                    && r.TryGetProperty("servers", out var servers))
                    foreach (var s in servers.EnumerateArray())
                        if (s.TryGetProperty("host", out var h) && h.GetString() is { Length: > 0 } hs)
                            hosts.Add(hs);
            }
            lock (CdnGate)
            {
                _directoryHosts = hosts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            if (hosts.Count > 0)
                AppLog.Warn("SteamCdnProxy", $"目录刷新：{hosts.Count} 个边缘 → {string.Join(", ", hosts.Distinct().Take(6))}");
        }
        catch (Exception ex)
        {
            AppLog.Warn("SteamCdnProxy", "目录刷新失败（沿用旧池/请求主机）: " + ex.Message);
        }
    }

    /// <summary>
    /// 目录请求：先直连，不通再借系统代理。api.steampowered.com 在国内直连经常整段超时
    /// （2026-11-16 22:45 实测），而探针的 urllib 默认走系统代理所以拿得到列表 ——
    /// 少了这条兜底，境内机器上候选池永远是空的，换源等于不存在。
    /// </summary>
    private static async Task<string?> FetchDirectoryJsonAsync(string url)
    {
        var sys = DepotDownloaderService.ReadSystemProxy();
        foreach (var proxy in sys is null ? new string?[] { null } : new string?[] { null, sys })
        {
            try
            {
                var handler = new HttpClientHandler { UseProxy = proxy is not null };
                if (proxy is not null) handler.Proxy = new WebProxy(proxy);
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
                using var resp = await http.GetAsync(url);
                if (resp.IsSuccessStatusCode) return await resp.Content.ReadAsStringAsync();
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// 接管一条绝对 URI 的 GET/HEAD：智能选边拉取并把响应原样回给 DD。
    /// 非 CDN 域（api 等）原样透传一次，保持旧语义。
    /// </summary>
    private static async Task HandlePlainHttpAsync(NetworkStream stream, string head, string firstLine)
    {
        var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || (parts[0] != "GET" && parts[0] != "HEAD"))
        {
            await TryWriteAsciiAsync(stream, "HTTP/1.1 405 Method Not Allowed\r\n\r\n", CancellationToken.None);
            return;
        }
        if (!Uri.TryCreate(parts[1], UriKind.Absolute, out var uri) || uri.Scheme != "http")
        {
            await TryWriteAsciiAsync(stream, "HTTP/1.1 400 Bad Request\r\n\r\n", CancellationToken.None);
            return;
        }

        EnsureDirectoryRefresh();
        string? range = null;
        foreach (var line in head.Split('\n'))
        {
            var t = line.TrimEnd('\r');
            if (t.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) range = t[6..].Trim();
        }
        List<string> candidates;
        lock (CdnGate)
        {
            var health = CdnPool.ToDictionary(kv => kv.Key,
                kv => (kv.Value.ConsecutiveBad, kv.Value.LastGoodMs, kv.Value.BadUntil));
            var pool = _directoryHosts.ToList();
            candidates = IsCdnHost(uri.Host)
                ? OrderCdnCandidates(uri.Host, pool, health, Environment.TickCount64)
                : new List<string> { uri.Host };
        }

        byte[]? lastBody = null;
        HttpStatusCode lastStatus = HttpStatusCode.BadGateway;
        string? lastContentRange = null;
        foreach (var edge in candidates.Take(4))
        {
            var target = new UriBuilder(uri) { Host = edge }.Uri;
            var (status, body, ms, ok, contentRange) = await FetchCdnAsync(target, parts[0] == "HEAD", range);
            NoteCdnResult(edge, ok, ms);
            LogCdnAttempt(uri.Host, edge, (int)status, body?.LongLength ?? 0, ms, ok);
            lastStatus = status; lastBody = body; lastContentRange = contentRange;
            if (ok) break;
        }

        if (lastBody is null && lastStatus == HttpStatusCode.BadGateway)
        {
            await TryWriteAsciiAsync(stream, "HTTP/1.1 502 Bad Gateway\r\n\r\n", CancellationToken.None);
            return;
        }
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {(int)lastStatus} {lastStatus}\r\n");
        sb.Append($"Content-Length: {lastBody?.LongLength ?? 0}\r\n");
        if (lastContentRange is not null) sb.Append($"Content-Range: {lastContentRange}\r\n");
        sb.Append("Connection: close\r\n\r\n");
        await TryWriteAsciiAsync(stream, sb.ToString(), CancellationToken.None);
        if (lastBody is { Length: > 0 } && parts[0] == "GET")
        {
            try { await stream.WriteAsync(lastBody); await stream.FlushAsync(); } catch { }
        }
    }

    /// <summary>拉一次（含边缘 302 跟随、https 证书坏回落 http、Range 转发）。ok = 200/206。</summary>
    private static async Task<(HttpStatusCode Status, byte[]? Body, long Ms, bool Ok, string? ContentRange)> FetchCdnAsync(Uri uri, bool headOnly, string? range)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var current = uri;
        for (var hop = 0; hop < 3; hop++)
        {
            var followed = false;
            foreach (var scheme in new[] { current.Scheme, "http" }.Distinct())
            {
                var u = new UriBuilder(current) { Scheme = scheme, Port = scheme == "https" ? 443 : 80 }.Uri;
                try
                {
                    using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
                    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
                    using var req = new HttpRequestMessage(headOnly ? HttpMethod.Head : HttpMethod.Get, u);
                    if (range is not null) req.Headers.TryAddWithoutValidation("Range", range);
                    using var resp = await http.SendAsync(req);
                    // 边缘自己的 302 一律跟（含跨域：2026-11-16 探针 round48 实测
                    // st.dl.eccdnx.com → gstore.val.smogfly.com，是 Steam 的调度不是攻击）。
                    // 内容完整性由 DD 兜底：SteamKit2 对每个 chunk 校验长度/SHA1
                    // （dll 里实测有 "Length mismatch after downloading depot chunk!"）。
                    if ((int)resp.StatusCode is 301 or 302 or 307 or 308
                        && resp.Headers.Location is { } loc)
                    {
                        var next = loc.IsAbsoluteUri ? loc : new Uri(u, loc);
                        if (next.Scheme is "http" or "https")
                        {
                            current = next;
                            followed = true;
                            break;   // 跟跳转，进下一 hop
                        }
                    }
                    var body = headOnly ? Array.Empty<byte>() : await resp.Content.ReadAsByteArrayAsync();
                    sw.Stop();
                    var ok = resp.StatusCode is HttpStatusCode.OK or HttpStatusCode.PartialContent;
                    return (resp.StatusCode, body, sw.ElapsedMilliseconds, ok, resp.Content.Headers.ContentRange?.ToString());
                }
                catch (HttpRequestException ex) when (scheme == "https" && ex.InnerException is System.Security.Authentication.AuthenticationException)
                {
                    continue;   // 证书坏 → 回落 http 同主机
                }
                catch { }
            }
            if (!followed) break;   // 没有跳转可跟（或两种 scheme 都失败）：收工
        }
        sw.Stop();
        return (HttpStatusCode.BadGateway, null, sw.ElapsedMilliseconds, false, null);
    }

    private static readonly object LogGate = new();
    private static void LogCdnAttempt(string requested, string edge, int status, long bytes, long ms, bool ok)
    {
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                ts = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                requested, edge, status, bytes, ms, ok,
            });
            lock (LogGate)
                File.AppendAllText(Path.Combine(ConfigService.ConfigDir, "cdn-proxy-log.jsonl"), line + "\n");
        }
        catch { }
        AppLog.Warn("SteamCdnProxy", $"{requested} → {edge} http{status} {bytes}B {ms}ms {(ok ? "ok" : "fail")}");
    }
}