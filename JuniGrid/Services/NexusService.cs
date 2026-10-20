using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JuniGrid.Services;

/// <summary>
/// Nexus Mods Public API v1 (https://api.nexusmods.com).
///  - 需鉴权的端点用 OAuth2 的 Bearer access token（由 NexusOAuthService 登录获得）；
///    未登录时请求不带 Authorization，仅公开 GraphQL 浏览可用。不使用个人 API Key。
///  - Direct download links are Premium-only (Nexus policy): free accounts
///    get HTTP 403 on download_link — surfaced as NeedsPremium.
/// Rate limits: ~100 req/day free, 2500/day premium (X-RL-* headers).
/// All api.nexusmods.com / GraphQL traffic goes through SendApiAsync (central concurrency
/// cap + 429 / Retry-After / X-RL-*-Reset handling) so one client never bursts aggressively.
/// </summary>
public sealed partial class NexusService
{
    private const string Base = "https://api.nexusmods.com/v1/games/stardewvalley";
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var h = new HttpClient();
        // AUP：Application-Name / Application-Version / UA 版本都必须与发行版本一致
        // —— 全部从 AppInfo 单一版本源读，不再写死字面量（旧值 0.2.0 早已过期）
        h.DefaultRequestHeaders.UserAgent.ParseAdd($"JuniGrid/{AppInfo.Version}");
        h.DefaultRequestHeaders.TryAddWithoutValidation("Application-Name", "JuniGrid");
        h.DefaultRequestHeaders.TryAddWithoutValidation("Application-Version", AppInfo.Version);
        h.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        h.Timeout = TimeSpan.FromSeconds(15);   // v1.06.8：检查更新提速——慢请求 15s 快速失败，不再拖住整批
        return h;
    }

    /// <summary>已登录用户的 OAuth2 access token（由 NexusOAuthService 设置）。未登录为 null ——
    /// 此时请求不带鉴权（公开 GraphQL 浏览仍可用）。</summary>
    private static string? _bearerToken;
    public static string? BearerToken
    {
        get => Volatile.Read(ref _bearerToken);
        set => Volatile.Write(ref _bearerToken, value);
    }
    /// <summary>用户是否已通过 OAuth2 登录（存在 Bearer token）。</summary>
    public static bool IsAuthenticated => !string.IsNullOrEmpty(BearerToken);

    /// <summary>token 快过期时的刷新器，由启动处在 DI 建好后挂进来 —— 本类是静态的，
    /// 反向依赖 NexusOAuthService 会把两个服务拧成环。</summary>
    public static Func<Task>? EnsureFreshToken;
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);
    private static DateTime _lastRefreshTryUtc = DateTime.MinValue;

    /// <summary>带 Bearer 的请求发出前刷一次过期的 access token。
    /// 10 秒内只试一次、且已有刷新在飞时直接放行 —— 否则一次 401 风暴会刷出几十次。</summary>
    private static async Task EnsureFreshBearerAsync(CancellationToken ct)
    {
        var hook = EnsureFreshToken;
        if (hook is null || !await RefreshGate.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try
        {
            if ((DateTime.UtcNow - _lastRefreshTryUtc).TotalSeconds < 10) return;
            _lastRefreshTryUtc = DateTime.UtcNow;
            await hook().ConfigureAwait(false);
        }
        catch { /* 刷新失败就按原 token 发，调用方照旧优雅降级 */ }
        finally { RefreshGate.Release(); }
    }

    // ══════════════════════════════════════════════════════════════════
    // Nexus API 集中限流闸门（AUP 二审：降低并发 + 集中识别 429 + 遵守 Retry-After）
    //  - MaxConcurrent：本进程在飞的 API/GraphQL 请求硬顶
    //  - 429：优先 Retry-After（秒或 HTTP-date），否则 X-RL-*-Reset
    //  - 成功响应若 Remaining==0 也主动暂停，避免下一轮冲进 429
    // CDN 文件下载不进此闸门（不同 host；download_link 本身只是一次 API 调用）
    // ══════════════════════════════════════════════════════════════════
    private const int MaxConcurrentApiRequests = 2;
    private static readonly SemaphoreSlim ApiGate = new(MaxConcurrentApiRequests, MaxConcurrentApiRequests);
    private static long _apiPauseUntilUtcTicks;
    // 服务端明确给出的等待时长不做上限截断（免费号 daily reset 可能数小时）；
    // 仅无头兜底与「单次调用内联重试」有短上限。
    private static readonly TimeSpan MinRetryAfter = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FallbackWhenNoHeaders = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxInlineRetryWait = TimeSpan.FromSeconds(15);
    /// <summary>在闸门外排队的上限。ct=default 时 WaitAsync 无期限等待，必须自己带超时：
    /// 2 个槽位被慢请求占满，第三个请求会静静排队到天荒地老，而界面只看见"某一步在转圈"。</summary>
    private static readonly TimeSpan ApiGateQueueTimeout = TimeSpan.FromSeconds(30);

    private static void PauseUntil(DateTimeOffset untilUtc)
    {
        var ticks = untilUtc.UtcTicks;
        while (true)
        {
            var cur = Volatile.Read(ref _apiPauseUntilUtcTicks);
            if (ticks <= cur) return;
            if (Interlocked.CompareExchange(ref _apiPauseUntilUtcTicks, ticks, cur) == cur) return;
        }
    }

    private static async Task WaitIfPausedAsync(CancellationToken ct)
    {
        while (true)
        {
            var until = Volatile.Read(ref _apiPauseUntilUtcTicks);
            var now = DateTimeOffset.UtcNow.UtcTicks;
            if (until <= now) return;
            var waitMs = (int)Math.Min((until - now) / TimeSpan.TicksPerMillisecond + 25, 30_000);
            if (waitMs <= 0) return;
            await Task.Delay(waitMs, ct).ConfigureAwait(false);
        }
    }

    private static bool TryGetIntHeader(HttpResponseMessage res, string name, out int value)
    {
        value = 0;
        if (!res.Headers.TryGetValues(name, out var vals)) return false;
        foreach (var v in vals)
            if (int.TryParse(v.Trim(), out value)) return true;
        return false;
    }

    /// <summary>配额耗尽（Remaining==0）时的主动暂停。</summary>
    private static void ObserveRateLimit(HttpResponseMessage res)
    {
        try
        {
            if (TryGetIntHeader(res, "X-RL-Hourly-Remaining", out var hourlyLeft) && hourlyLeft <= 0
                && TryGetIntHeader(res, "X-RL-Hourly-Reset", out var hourlyReset) && hourlyReset > 0)
            {
                var wait = TimeSpan.FromSeconds(Math.Max(hourlyReset, 1));
                PauseUntil(DateTimeOffset.UtcNow + wait);
                AppLog.Warn("Nexus", $"小时配额耗尽 — 暂停 API 调用 {wait.TotalSeconds:0}s");
            }
            else if (TryGetIntHeader(res, "X-RL-Daily-Remaining", out var dailyLeft) && dailyLeft <= 0
                     && TryGetIntHeader(res, "X-RL-Daily-Reset", out var dailyReset) && dailyReset > 0)
            {
                var wait = TimeSpan.FromSeconds(Math.Max(dailyReset, 1));
                PauseUntil(DateTimeOffset.UtcNow + wait);
                AppLog.Warn("Nexus", $"日配额耗尽 — 暂停 API 调用 {wait.TotalSeconds:0}s");
            }
        }
        catch { /* 头解析失败不得影响正常响应 */ }
    }

    /// <summary>解析 429 的 Retry-After / X-RL-*-Reset，写入全局暂停并返回应等待时长。</summary>
    private static TimeSpan ApplyPauseFrom429(HttpResponseMessage res)
    {
        TimeSpan wait;
        var ra = res.Headers.RetryAfter;
        if (ra?.Delta is TimeSpan d && d > TimeSpan.Zero) wait = d;
        else if (ra?.Date is DateTimeOffset dt)
        {
            wait = dt - DateTimeOffset.UtcNow;
            if (wait < MinRetryAfter) wait = MinRetryAfter;
        }
        else if (TryGetIntHeader(res, "X-RL-Hourly-Reset", out var hourlyReset) && hourlyReset > 0)
            wait = TimeSpan.FromSeconds(hourlyReset);
        else if (TryGetIntHeader(res, "X-RL-Daily-Reset", out var dailyReset) && dailyReset > 0)
            wait = TimeSpan.FromSeconds(dailyReset);
        else
            wait = FallbackWhenNoHeaders;

        if (wait < MinRetryAfter) wait = MinRetryAfter;
        PauseUntil(DateTimeOffset.UtcNow + wait);
        return wait;
    }

    /// <summary>
    /// Nexus API v1 与 GraphQL 的唯一发送出口：并发硬顶、等暂停、集中识别 429。
    /// 短窗口（≤15s）等满后重试一次；长锁立刻返回 429（释放闸门，不占槽位挂死 UI），
    /// 全局 PauseUntil 仍按服务端完整时长生效。
    /// </summary>
    /// <summary>本进程真实发出去的 Nexus API/GraphQL 请求数，和命中缓存的次数。
    /// AUP 审的正是「一个客户端消耗多少请求」，界面上给出来比口头保证可信：Nexus 页每次进页
    /// 打一行「本次进页 网络 N 次 / 缓存 M 次」，他截图就能回给审核方。</summary>
    private static long _apiRequests, _cacheHits, _cacheMisses;
    public static long ApiRequests => Volatile.Read(ref _apiRequests);
    public static long CacheHits => Volatile.Read(ref _cacheHits);
    public static long CacheMisses => Volatile.Read(ref _cacheMisses);

    /// <summary>给页面用的一帧快照：本次进页相对上次各涨了多少。</summary>
    public static (long Requests, long Hits, long Misses) TakeTrafficDelta()
    {
        var r = Interlocked.Exchange(ref _trafficReq, 0);
        var h = Interlocked.Exchange(ref _trafficHit, 0);
        var m = Interlocked.Exchange(ref _trafficMiss, 0);
        return (r, h, m);
    }
    private static long _trafficReq, _trafficHit, _trafficMiss;
    private static void BumpTraffic(long req, long hit, long miss)
    {
        Interlocked.Add(ref _trafficReq, req);
        Interlocked.Add(ref _trafficHit, hit);
        Interlocked.Add(ref _trafficMiss, miss);
    }

    /// <summary>唯一出口：数一次请求再交给闸门（计数只在这里加，别的地方数不到）。</summary>
    private static async Task<HttpResponseMessage> SendApiAsync(
        HttpClient client,
        string url,
        HttpMethod? method = null,
        string? jsonBody = null,
        bool useBearer = true,
        CancellationToken ct = default)
    {
        Interlocked.Increment(ref _apiRequests);
        Interlocked.Increment(ref _trafficReq);
        return await SendApiInnerAsync(client, url, method, jsonBody, useBearer, ct);
    }

    private static async Task<HttpResponseMessage> SendApiInnerAsync(
        HttpClient client,
        string url,
        HttpMethod? method,
        string? jsonBody,
        bool useBearer,
        CancellationToken ct)
    {
        for (; ; )
        {
            if (useBearer && BearerToken is not null)
                await EnsureFreshBearerAsync(ct).ConfigureAwait(false);
            // 等暂停必须在<b>取槽位之前</b>：旧写法先进闸门再睡，一次 429 能让 2 个槽全抱着
            // 槽位睡觉，浏览请求排在睡死的槽后面 —— 服务端看到的是"没并发"，用户看到的是"进页卡住"。
            await WaitIfPausedAsync(ct).ConfigureAwait(false);
            // 闸门排队也要有上限：ct 可能是 default（调用方没传），那样 WaitAsync 会无限期
            // 干等 —— 2 个槽位被慢请求占满时，第三个请求在闸门外静静排队，界面上看不出
            // 任何区别（同样表现为"某一步永远转圈"）。排队超过阈值就当这次调用失败，
            // 让调用方重试或报错，绝不静默挂死。
            if (!await ApiGate.WaitAsync(ApiGateQueueTimeout, ct).ConfigureAwait(false))
                throw new TimeoutException(
                    $"Nexus API 闸门排队超过 {ApiGateQueueTimeout.TotalSeconds:0}s（前面的请求还没腾出通道）");
            try
            {
                // 排队期间别人刚触发了新暂停 ⇒ 让出槽位回到闸门外重等，不占着坑睡觉
                if (Volatile.Read(ref _apiPauseUntilUtcTicks) > DateTimeOffset.UtcNow.UtcTicks) continue;

                for (var attempt = 0; ; attempt++)
                {
                    using var req = new HttpRequestMessage(method ?? HttpMethod.Get, url);
                    if (useBearer && BearerToken is { } token && token.Length > 0)
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    if (jsonBody is not null)
                        req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                    var res = await client.SendAsync(req, ct).ConfigureAwait(false);
                    ObserveRateLimit(res);
                    if ((int)res.StatusCode != 429 || attempt >= 1)
                        return res;

                    var wait = ApplyPauseFrom429(res);
                    res.Dispose();
                    if (wait > MaxInlineRetryWait)
                    {
                        AppLog.Warn("Nexus", $"HTTP 429 on {url} — API 暂停 {wait.TotalSeconds:0}s，本次调用直接失败不重试");
                        return new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                        {
                            ReasonPhrase = "Rate limited (paused per Retry-After / rate-limit reset)",
                        };
                    }
                    AppLog.Warn("Nexus", $"HTTP 429 on {url} — 短窗口 {wait.TotalSeconds:0}s，等待后重试一次");
                    await Task.Delay(wait, ct).ConfigureAwait(false);   // 有界 ≤15s，占槽可接受
                }
            }
            finally
            {
                ApiGate.Release();
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // 响应缓存（AUP：同一个用户的同一个查询不该打两次）
    //  - 10 分钟内同参数直接吃上一份结果，0 请求；「刷新」/登录登出/成人开关变化时整仓作废
    //  - 键里带成人过滤版本与登录态：这两者一变，同一查询文本对应的结果本来就不同
    //  - 失败（null）只记 60 秒，避免一次 429 把坏结果钉住 10 分钟
    //  - 同一 key 并发只发一次（single-flight）：进页的几条链否则会各打一份同样的请求
    // ══════════════════════════════════════════════════════════════════
    private const long CacheTtlMs = 10 * 60 * 1000;
    private const long CacheFailTtlMs = 60_000;
    private static readonly ConcurrentDictionary<string, (long ExpiresAtMs, object? Value)> ResponseCache = new();
    private static readonly ConcurrentDictionary<string, Task> InFlight = new();
    private static int _cacheGen;

    /// <summary>作废全部已缓存的 N 网响应。手动刷新、登录态变化、成人开关变化时调用。</summary>
    public static void ClearResponseCache()
    {
        Interlocked.Increment(ref _cacheGen);
        ResponseCache.Clear();
        InFlight.Clear();
        // 磁盘那层也要跟着清：开关一翻，上一份按旧偏好拉的榜单就不该还在盘上等被读出来
        try { if (Directory.Exists(BrowseDiskDir)) Directory.Delete(BrowseDiskDir, true); }
        catch { /* 缓存盘从来不许把业务拖住 */ }
    }

    /// <summary>榜单落盘的目录（跟着统一缓存目录走，用户搬缓存时它一起搬）。</summary>
    private static string BrowseDiskDir => StoragePaths.InCache("nexus-browse");

    /// <summary>公共键前缀：过滤条件版本 + 登录态。调用方只需拼自己的参数。
    /// 这两样都会改变「服务端/我们自己该给出哪些页」，所以必须进键，不能靠 Clear 兜。</summary>
    private static string Key(string what) =>
        what + "|av" + AdultFilterVersion + (IsAuthenticated ? "|au1" : "|au0");

    private static string BrowseDiskPath(string key)
    {
        // ⚠ 别用 SHA1.HashData(Span,Span) 那对重载 —— 它的顺序是 (source, destination)，
        // 写反了会把每个键都散列成同一个值（实测：所有榜单共用一个文件，后写的盖掉前写的，
        // 于是「搜 Content Patcher 返回 LewdDew 的 8 条」）。测试舱 U23 钉住这一点。
        var hash = System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(key));
        return Path.Combine(BrowseDiskDir, Convert.ToHexString(hash) + ".json");
    }

    /// <summary>落盘信封：连同「什么时候写的」一起存，读回来才能算还剩多少 TTL。</summary>
    private sealed class DiskEntry
    {
        public long WrittenAtUnixMs { get; set; }
        public string? Payload { get; set; }
    }

    /// <summary>缓存的唯一判据：这份结果值不值得按 10 分钟留着、要不要落盘。
    /// 抽出来只为一件事 —— <c>null</c> 被包进一层壳之后就不再是 null，光看"非 null"会把
    /// 一次抓取失败判成一次成功结果（2026-10-04 16:24 真机：TLS 抖动 ⇒ 空壳被缓存 10 分钟
    /// 并写盘 ⇒ 榜单连重启都是空的）。所以【壳里装的是什么】必须由 sane 说清楚。</summary>
    internal static bool Cacheable<T>(T? v, Func<T, bool>? sane) where T : class
        => v is not null && (sane is null || sane(v));

    private static async Task<T?> CachedAsync<T>(string key, Func<Task<T?>> factory) where T : class
        => await CachedAsync(key, factory, persist: false, sane: null);

    private static async Task<T?> CachedAsync<T>(string key, Func<Task<T?>> factory, bool persist) where T : class
        => await CachedAsync(key, factory, persist, sane: null);

    /// <param name="sane">命中后还要过一道「这结果本身像不像成功」，判据见 <see cref="Cacheable"/>。
    /// 命中到坏条目时该键就地作废，下一次调用重新去拉。</param>
    private static async Task<T?> CachedAsync<T>(string key, Func<Task<T?>> factory, bool persist,
        Func<T, bool>? sane) where T : class
    {
        bool Good(T? v) => Cacheable(v, sane);
        if (ResponseCache.TryGetValue(key, out var hit) && hit.ExpiresAtMs > Environment.TickCount64)
        {
            if (hit.Value is T memHit && Good(memHit))
            { Interlocked.Increment(ref _cacheHits); BumpTraffic(0, 1, 0); return memHit; }
            ResponseCache.TryRemove(key, out _);   // 坏条目就地踢掉，别一直占着这个键
        }
        var gen = Volatile.Read(ref _cacheGen);
        // 内存没有、但上次启动写过盘 ⇒ 冷启动第一次进页也 0 请求（AUP 要的就是少打，
        // 而「每次重启都要重新拉两条榜单链」正是 Nexus 页首屏慢的那一段）
        if (persist)
        {
            var fromDisk = ReadDisk<T>(key, gen);
            if (Good(fromDisk)) { Interlocked.Increment(ref _cacheHits); BumpTraffic(0, 1, 0); return fromDisk; }
        }
        Interlocked.Increment(ref _cacheMisses); BumpTraffic(0, 0, 1);
        var tcs = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = InFlight.GetOrAdd(key, tcs.Task);
        if (!ReferenceEquals(running, tcs.Task))
            return running is Task<T?> sameType ? await sameType.ConfigureAwait(false) : null;
        try
        {
            var v = await factory().ConfigureAwait(false);
            if (gen == Volatile.Read(ref _cacheGen))   // 等待期间被 Clear 过就别写回旧结果
            {
                // 失败（null 或"包了一层的失败"）只记 60 秒负缓存，而且【绝不写盘】
                ResponseCache[key] = (Environment.TickCount64 + (Good(v) ? CacheTtlMs : CacheFailTtlMs), v);
                if (persist && Good(v)) WriteDisk(key, v);
            }
            tcs.TrySetResult(v);
            return v;
        }
        catch (Exception ex) { tcs.TrySetException(ex); throw; }
        finally { InFlight.TryRemove(key, out _); }
    }

    private static T? ReadDisk<T>(string key, int gen) where T : class
    {
        try
        {
            var path = BrowseDiskPath(key);
            if (!File.Exists(path)) return null;
            var env = Newtonsoft.Json.JsonConvert.DeserializeObject<DiskEntry>(File.ReadAllText(path));
            if (env?.Payload is null) return null;
            var ageMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - env.WrittenAtUnixMs;
            if (ageMs < 0 || ageMs > CacheTtlMs) { try { File.Delete(path); } catch { } return null; }
            if (gen != Volatile.Read(ref _cacheGen)) return null;   // 读的瞬间开关翻了 ⇒ 作废
            var v = Newtonsoft.Json.JsonConvert.DeserializeObject<T>(env.Payload);
            if (v is not null)
                ResponseCache[key] = (Environment.TickCount64 + (CacheTtlMs - ageMs), v);
            return v;
        }
        catch { return null; }
    }

    private static void WriteDisk<T>(string key, T value) where T : class
    {
        // 落盘失败绝不影响这次结果，所以整段吞掉；写本身挪到线程池，不挡渲染
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(BrowseDiskDir);
                var env = new DiskEntry
                {
                    WrittenAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Payload = Newtonsoft.Json.JsonConvert.SerializeObject(value)
                };
                File.WriteAllText(BrowseDiskPath(key),
                    Newtonsoft.Json.JsonConvert.SerializeObject(env));
            }
            catch { }
        });
    }
    // v1.08：宽容客户端 —— 详情页/按需单请求专用。Nexus API 单请求实测要 5~8 秒，
    // 详情页要串 7 个请求，15s 快速失败通道必然超时。批量检查更新仍走 Http(15s)。
    private static readonly HttpClient SlowHttp = CreateSlow();
    private static HttpClient CreateSlow()
    {
        var h = CreateClient();
        h.Timeout = TimeSpan.FromSeconds(45);
        return h;
    }
}
