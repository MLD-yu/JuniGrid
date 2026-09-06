using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JuniGrid.Services;

/// <summary>
/// v1.1.2：内置翻译器（外文 → 简体中文）。
/// 引擎：谷歌翻译 —— 走 Chrome 内置翻译同款通道 clients5.google.com（质量 = 网页版谷歌翻译，
/// 免 key），失败自动回退 googleapis 同协议通道 → 有道 aidemo。主机级熔断：失败主机 60s 内
/// 不再重试（429 熔断 120s），期间排队文本挂起等冷却，不丢不重。
/// 提速三板斧：
///   ① 磁盘缓存 %APPDATA%/JuniGrid/junigrid.translate-cache.json —— 重启后同文本零请求秒出；
///   ② 微批 —— 30ms 窗口聚合同期请求，一次 HTTP 最多带 40 条文本，返回按下标 1:1 对齐；
///   ③ 并发去重 —— 同一文本全局只发一次网络请求，结果广播给所有等待者。
/// 已是中文 / 纯数字符号 / URL / Windows 路径 / 堆栈帧的文本自动跳过。
/// 任何失败一律返回原文，绝不抛异常影响页面渲染。
/// </summary>
public sealed class TranslationService
{
    private const string TargetLang = "zh-CN";
    private const int MaxBatchItems = 40;       // 单次请求最大条数
    private const int MaxBatchChars = 1200;     // 单次请求总字符（URL 编码后仍远低于 8k 上限）
    private const int CacheCap = 30000;         // 磁盘缓存条目上限（FIFO 淘汰）
    private const int HttpTimeoutMs = 12000;   // 代理隧道可能很慢，放宽整体超时
    private static readonly TimeSpan DirectConnectTimeout = TimeSpan.FromSeconds(3);   // 直连不通时快速失败，尽快切代理

    private static int _preferredChannel;   // 0=直连 1=系统代理；成功通道优先复用，避免每批都白等直连超时


    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "JuniGrid", "junigrid.translate-cache.json");

    // ── 引擎主机（按优先级，同一协议三通道互为备份）──
    private static readonly (string Name, string Url)[] GoogleHosts =
    {
        ("clients5", "https://clients5.google.com/translate_a/t"),
        ("google.com", "https://translate.google.com/translate_a/t"),
        ("googleapis", "https://translate.googleapis.com/translate_a/t"),
    };

    private static readonly HttpClient Http = CreateHttp(useProxy: false);   // 直连优先：本机系统代理/加速器可能劫持谷歌域名且通道坏死
    private static readonly HttpClient HttpViaProxy = CreateHttp(useProxy: true);   // 直连被墙环境回落走系统代理

    private static HttpClient CreateHttp(bool useProxy)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = useProxy ? TimeSpan.FromSeconds(8) : DirectConnectTimeout,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            UseProxy = useProxy,   // false = 忽略系统代理强制直连
        };
        var h = new HttpClient(handler);
        h.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        h.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        h.Timeout = TimeSpan.FromMilliseconds(HttpTimeoutMs + 4000);
        return h;
    }

    // ── 缓存与去重 ──
    // v1.1.2：带滑动过期的缓存 —— 每次命中刷新访问时间，后台清扫器把过期条目从
    // 内存与磁盘一并清掉；翻译开关关闭时全量清空。
    // TTL 24h：5 分钟版在弱网下会造成「停留几分钟返回就重新翻」的体验，太激进。
    private sealed record CacheEntry(string Value, long LastAccessUtcTicks);

    private const int CacheTtlMinutes = 60 * 24;   // 24 小时滑动过期
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingTcs = new();
    private readonly ConcurrentQueue<(string Key, string Text)> _queue = new();
    private readonly SemaphoreSlim _gate = new(3);                    // 并发 HTTP 上限
    private int _draining;
    private readonly System.Timers.Timer _sweepTimer;                 // 过期清扫器

    // ── 熔断状态 ──
    private readonly ConcurrentDictionary<string, DateTime> _hostDownUntil = new();
    private DateTime _engineCooldownUntil;                            // 全引擎失败后的整体冷却

    private readonly object _saveLock = new();
    private System.Timers.Timer? _saveTimer;
    private volatile bool _dirty;

    public TranslationService()
    {
        try { LoadCache(); } catch { }
        // 过期清扫器：每 10 分钟清一次超过 24h 没访问的条目（内存+磁盘同步）
        _sweepTimer = new System.Timers.Timer(600_000) { AutoReset = true };
        _sweepTimer.Elapsed += (_, _) => SweepExpired();
        _sweepTimer.Start();
        // 进程退出把未落盘的新翻译刷盘（同 ConfigService 的兜底思路）
        System.AppDomain.CurrentDomain.ProcessExit += (_, _) => { _sweepTimer.Stop(); SaveNow(); };
    }

    /// <summary>清扫过期条目：超过 5 分钟没有再次访问的翻译直接删除。</summary>
    private void SweepExpired()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-CacheTtlMinutes).Ticks;
            var removed = 0;
            foreach (var kv in _cache)
                if (kv.Value.LastAccessUtcTicks < cutoff && _cache.TryRemove(kv.Key, out _))
                    removed++;
            if (removed > 0) SaveNow();
        }
        catch { }
    }

    /// <summary>翻译开关关闭时由 JS 调用：清空全部缓存（内存 + 磁盘文件）。</summary>
    [Microsoft.JSInterop.JSInvokable]
    public void ClearTransCache()
    {
        _cache.Clear();
        try { if (File.Exists(CachePath)) File.Delete(CachePath); } catch { }
    }

    /// <summary>缓存命中并刷新访问时间（滑动过期）。</summary>
    private bool TryGetFresh(string key, out string value)
    {
        if (_cache.TryGetValue(key, out var e))
        {
            _cache[key] = e with { LastAccessUtcTicks = DateTime.UtcNow.Ticks };
            value = e.Value;
            return true;
        }
        value = "";
        return false;
    }

    // ═══════════════ JS 全局观察器入口 ═══════════════

    /// <summary>
    /// junigrid.trans.js 的批量入口：观察器把页面新出现的英文文本去重后整批送来，
    /// 返回按序 1:1 对齐的译文数组（无需翻译/失败的项返回原文）。失败不抛异常。
    /// </summary>
    [Microsoft.JSInterop.JSInvokable]
    public Task<string[]> TransBatch(string[] texts)
        => TranslateBatchAsync(texts);

    // ═══════════════ 公共 API ═══════════════

    /// <summary>翻译一段文本 → 简体中文。无需翻译/失败时返回原文。永不抛异常。</summary>
    public Task<string> TranslateAsync(string text)
    {
        try
        {
            if (!NeedsTranslation(text)) return Task.FromResult(text);
            var key = Hash(text);
            if (TryGetFresh(key, out var hit)) return Task.FromResult(hit);

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var existing = _pendingTcs.GetOrAdd(key, tcs);
            if (!ReferenceEquals(existing, tcs)) return existing.Task;   // 已在队列/翻译中，等广播

            _queue.Enqueue((key, text));
            KickDrainer();
            return existing.Task;
        }
        catch { return Task.FromResult(text); }
    }

    /// <summary>批量翻译：返回数组与传入顺序 1:1 对应。失败项返回原文。</summary>
    public async Task<string[]> TranslateBatchAsync(IReadOnlyList<string> texts)
    {
        var result = new string[texts.Count];
        var tasks = new Task[texts.Count];
        for (var i = 0; i < texts.Count; i++)
        {
            var idx = i;
            tasks[i] = TranslateAsync(texts[i]).ContinueWith(t => { result[idx] = t.Result; });
        }
        await Task.WhenAll(tasks);
        return result;
    }

    /// <summary>文本是否值得翻译：含真实英文单词、且尚未是中文。</summary>
    public static bool NeedsTranslation(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        if (StackFrameRegex.IsMatch(s)) return false;              // 堆栈帧 "   at StardewValley..."（用原文判定，Trim 会吃掉缩进）
        var t = s.Trim();
        if (t.Length < 3) return false;
        if (WinPathRegex.IsMatch(t)) return false;                 // 纯 Windows 路径
        if (UrlOnlyRegex.IsMatch(t)) return false;                 // 纯 URL

        var cjk = 0; var latin = 0;
        foreach (var ch in t)
        {
            if (ch is >= '\u4e00' and <= '\u9fff' or >= '\u3400' and <= '\u4dbf') cjk++;
            else if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z')) latin++;
        }
        if (cjk > 0 && cjk * 2 >= latin) return false;             // 中文已占主导（半中文混合行如「已启动 SMAPI 进程」也没必要翻）
        if (latin < 3) return false;                               // 没有真正的单词
        return true;
    }

    private static readonly Regex StackFrameRegex = new(@"^\s{2,}at\s+\S", RegexOptions.Compiled);
    private static readonly Regex WinPathRegex = new(@"^[A-Za-z]:\\[\w\\ .:()\-&']+$", RegexOptions.Compiled);
    private static readonly Regex UrlOnlyRegex = new(@"^https?://\S+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ═══════════════ 微批排水器 ═══════════════

    private void KickDrainer()
    {
        if (Interlocked.CompareExchange(ref _draining, 1, 0) != 0) return;
        _ = Task.Run(DrainLoopAsync);
    }

    private async Task DrainLoopAsync()
    {
        try
        {
            await Task.Delay(30);   // 微批窗口：等同期请求聚成一车
            while (true)
            {
                var now = DateTime.UtcNow;
                if (now < _engineCooldownUntil)
                {
                    // 全引擎冷却中：留在队列里，冷却到点后再排（新请求也会再 kick）
                    var delay = _engineCooldownUntil - now + TimeSpan.FromMilliseconds(200);
                    _ = Task.Delay(delay).ContinueWith(_ => KickDrainer());
                    return;
                }

                var batch = new List<(string Key, string Text)>();
                var chars = 0;
                while (batch.Count < MaxBatchItems && _queue.TryDequeue(out var it))
                {
                    if (TryGetFresh(it.Key, out var hit))   // 排队期间别的批次已完成
                        Complete(it.Key, hit, broadcast: true);
                    else
                    {
                        batch.Add(it);
                        chars += it.Text.Length;
                        if (chars >= MaxBatchChars) break;
                    }
                }
                if (batch.Count == 0) return;

                try
                {
                    // 谷歌链路时通时断：整批全失败时自动重试最多 3 次（间隔 4s），
                    // 赶上链路恢复的窗口就把这批翻完，不再等新内容触发
                    string?[] translated = new string?[batch.Count];
                    for (var attempt = 1; attempt <= 3; attempt++)
                    {
                        translated = await EngineBatchAsync(batch.Select(b => b.Text).ToList());
                        if (!translated.All(t => t is null)) break;
                        if (attempt < 3) await Task.Delay(4000);
                    }
                    for (var i = 0; i < batch.Count; i++)
                    {
                        var zh = translated[i];
                        if (zh is not null && zh != batch[i].Text) CachePut(batch[i].Key, zh);
                        Complete(batch[i].Key, zh ?? batch[i].Text, broadcast: true);
                    }
                }
                catch
                {
                    // 引擎层意外异常：放行本批等待者（显示原文），绝不能让 TCS 留在
                    // pending 表里 —— 否则同文本后续所有请求都会 await 一个永不完成的任务
                    foreach (var b in batch) Complete(b.Key, b.Text, broadcast: true);
                }
            }
        }
        catch
        {
            // 排水器异常：兜底放行所有等待者（返回原文），防止 UI 永久挂起
        }
        finally
        {
            Volatile.Write(ref _draining, 0);
            if (!_queue.IsEmpty) KickDrainer();
        }
    }

    /// <summary>完成一个 key：从 pending 表摘除并广播结果。</summary>
    private void Complete(string key, string value, bool broadcast)
    {
        if (_pendingTcs.TryRemove(key, out var tcs) && broadcast)
            tcs.TrySetResult(value);
    }

    // ═══════════════ 引擎层 ═══════════════

    /// <summary>跑一遍引擎链，返回与 items 对齐的译文数组；null 项 = 该条失败（调用方用原文兜底）。</summary>
    private async Task<string?[]> EngineBatchAsync(List<string> items)
    {
        var result = new string?[items.Count];

        // v1.1.5：Edge 引擎第一优先 —— 免鉴权、免 Key、支持批量与自动语种检测，
        // 国内直连可用且不受免费谷歌端点的 429 限流影响（实测）。失败回落免费链。
        {
            var official = await EdgeBatchAsync(items);
            if (official is not null)
            {
                for (var i = 0; i < items.Count; i++) result[i] = official[i];
                return result;
            }
            AppLog.Warn("Trans", "Edge 通道不可用，回落免费通道");
        }

        // 通道顺序：上次成功的优先（粘性），失败过的按熔断表跳过
        var channels = _preferredChannel == 1
            ? new[] { (HttpViaProxy, "proxy"), (Http, "direct") }
            : new[] { (Http, "direct"), (HttpViaProxy, "proxy") };

        var googleUsable = false;
        foreach (var (name, url) in GoogleHosts)
        {
            // 同一主机先试优选通道、再试备用 —— 覆盖「代理劫持但直连可用」与「直连被墙走代理」两种环境
            var hostBothDead = true;
            foreach (var (client, tag) in channels)
            {
                var hostKey = $"{name}#{tag}";
                if (_hostDownUntil.TryGetValue(hostKey, out var down) && DateTime.UtcNow < down) continue;
                try
                {
                    var sb = new StringBuilder(url)
                        .Append("?client=dict-chrome-ex&sl=auto&tl=").Append(TargetLang);
                    foreach (var t in items)
                    {
                        sb.Append("&q=").Append(Uri.EscapeDataString(Linearize(t)));
                    }
                    using var cts = new System.Threading.CancellationTokenSource(HttpTimeoutMs);
                    using var res = await client.GetAsync(sb.ToString(), cts.Token);
                    if ((int)res.StatusCode == 429)
                    {
                        _hostDownUntil[hostKey] = DateTime.UtcNow.AddSeconds(120);
                        hostBothDead = false;   // 路是通的，只是限流 —— 别触发"整条路死"的快速切换
                        try { AppLog.Warn("Trans", $"{hostKey}: 429 限流，熔断 120s"); } catch { }
                        continue;
                    }
                    if (!res.IsSuccessStatusCode)
                    {
                        _hostDownUntil[hostKey] = DateTime.UtcNow.AddSeconds(60);
                        hostBothDead = false;
                        try { AppLog.Warn("Trans", $"{hostKey}: HTTP {(int)res.StatusCode}，熔断 60s"); } catch { }
                        continue;
                    }
                    var rows = ParseDictChromeEx(await res.Content.ReadAsStringAsync(cts.Token), items.Count);
                    if (rows is null)
                    {
                        _hostDownUntil[hostKey] = DateTime.UtcNow.AddSeconds(60);
                        hostBothDead = false;
                        try { AppLog.Warn("Trans", $"{hostKey}: 响应解析失败/条数不齐（可能被风控页），熔断 60s"); } catch { }
                        continue;
                    }
                    for (var i = 0; i < items.Count; i++)
                        result[i] = string.IsNullOrWhiteSpace(rows[i]) ? null : rows[i];
                    _preferredChannel = tag == "proxy" ? 1 : 0;   // 记住成功通道
                    googleUsable = true;
                    return result;
                }
                catch (Exception ex)
                {
                    _hostDownUntil[hostKey] = DateTime.UtcNow.AddSeconds(60);
                    try
                    {
                        AppLog.Error("Trans", $"{hostKey}: {ex.GetType().Name} {ex.Message}"
                            + (ex.InnerException is null ? "" : $" | inner: {ex.InnerException.Message}"));
                    }
                    catch { }
                }
            }
            // 首个主机的两条通道全超时 = 到谷歌的路整个不通（TUN/节点挂了），
            // 剩余主机同路同命，直接切有道省 20+ 秒
            if (!googleUsable && name != GoogleHosts[^1].Name && hostBothDead) break;
        }

        // 兜底引擎：有道 aidemo（无批量，逐条 POST；部分失败允许）
        if (!_hostDownUntil.TryGetValue("youdao", out var yd) || DateTime.UtcNow >= yd)
        {
            var anyOk = false;
            try
            {
                for (var i = 0; i < items.Count; i++)
                {
                    if (result[i] is not null) continue;
                    try
                    {
                        var zh = await YoudaoAsync(items[i]);
                        if (zh is not null) { result[i] = zh; anyOk = true; }
                    }
                    catch { }
                }
        if (!anyOk) _hostDownUntil["youdao"] = DateTime.UtcNow.AddSeconds(60);
            else try { AppLog.Warn("Trans", $"谷歌不可用，{items.Count} 条已用有道兜底"); } catch { }
        }
        catch { }
        }

        if (result.All(r => r is null))
        {
            // 全部引擎失败 → 整体冷却，避免后续每个文本都白打一圈超时
            _engineCooldownUntil = DateTime.UtcNow.AddSeconds(15);
            try { AppLog.Error("Trans", "翻译引擎全部不可用，15s 后自动重试"); } catch { }
        }
        return result;
    }

    /// <summary>
    /// v1.1.5：Edge 翻译接口（2025 新流程，免鉴权、免 Key、支持批量 + 自动语种检测）。
    /// Body 为 JSON 字符串数组，Query: to=zh-Hans & isEnterpriseClient=false。
    /// 响应按输入顺序 1:1 返回 detectedLanguage + translations[].text。
    /// 任何失败返回 null（回落免费链）。
    /// </summary>
    private static async Task<string?[]?> EdgeBatchAsync(List<string> items)
    {
        var result = new string?[items.Count];
        try
        {
            var body = JsonSerializer.Serialize(items.Select(Linearize).ToArray());
            using var req = new HttpRequestMessage(HttpMethod.Post,
                "https://edge.microsoft.com/translate/translatetext?to=zh-Hans&isEnterpriseClient=false");
            req.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/150.0.0.0 Safari/537.36 Edg/151.0.0.0");
            using var cts = new CancellationTokenSource(HttpTimeoutMs);
            using var res = await Http.SendAsync(req, cts.Token);
            if (!res.IsSuccessStatusCode)
            {
                AppLog.Warn("Trans", $"Edge: HTTP {(int)res.StatusCode}");
                return null;
            }
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(cts.Token));
            var i = 0;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (i >= items.Count) break;
                var txt = (string?)null;
                foreach (var tr in item.GetProperty("translations").EnumerateArray())
                {
                    if (tr.GetProperty("to").GetString() == "zh-Hans")
                    { txt = tr.GetProperty("text").GetString(); break; }
                }
                result[i++] = string.IsNullOrWhiteSpace(txt) ? null : txt;
            }
            // 条数不齐视为失败（对齐承诺优先）
            for (var k = 0; k < items.Count; k++)
                if (result[k] is null) return null;
            AppLog.Warn("Trans", $"{items.Count} 条已走 Edge 通道");
            return result;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Trans", "Edge 异常: " + ex.Message);
            return null;
        }
    }

    /// <summary>谷歌 dict-chrome-ex 响应 → 按下标对齐的译文行；条数不齐视为失败（返回 null 换下一主机）。</summary>
    private static List<string>? ParseDictChromeEx(string json, int expected)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array) return null;
            var rows = new List<string>();
            foreach (var row in root.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() == 0) return null;
                var first = row[0];
                if (first.ValueKind == JsonValueKind.Array)
                {
                    // 句段形态 [[["t1","t2",...],"lang"]]：拼接全部句段
                    var sb2 = new StringBuilder();
                    foreach (var seg in first.EnumerateArray())
                        if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0
                            && seg[0].ValueKind == JsonValueKind.String)
                            sb2.Append(seg[0].GetString());
                    rows.Add(sb2.ToString());
                }
                else if (first.ValueKind == JsonValueKind.String)
                {
                    rows.Add(first.GetString() ?? "");
                }
                else return null;
            }
            return rows.Count == expected ? rows : null;
        }
        catch { return null; }
    }

    private static async Task<string?> YoudaoAsync(string text)
    {
        foreach (var client in new[] { Http, HttpViaProxy })
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(HttpTimeoutMs);
                using var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["q"] = Linearize(text),
                    ["from"] = "auto",
                    ["to"] = "zh-CHS",
                });
                using var res = await client.PostAsync("https://aidemo.youdao.com/trans", content, cts.Token);
                if (!res.IsSuccessStatusCode) continue;
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(cts.Token));
                if (doc.RootElement.TryGetProperty("translation", out var tr)
                    && tr.ValueKind == JsonValueKind.Array && tr.GetArrayLength() > 0
                    && tr[0].ValueKind == JsonValueKind.String)
                    return tr[0].GetString();
            }
            catch { }
        }
        return null;
    }

    /// <summary>换行会让部分引擎按行拆行返回、破坏 1:1 对齐 —— 统一压成空格。</summary>
    private static string Linearize(string s) =>
        s.IndexOf('\n') < 0 && s.IndexOf('\r') < 0 ? s : s.Replace('\r', ' ').Replace('\n', ' ');

    // ═══════════════ 磁盘缓存 ═══════════════

    private void CachePut(string key, string value)
    {
        _cache[key] = new CacheEntry(value, DateTime.UtcNow.Ticks);
        if (_cache.Count > CacheCap)
        {
            // 超上限：先清过期，仍超就随机丢一条兜底（TTL 5 分钟下正常到不了这）
            SweepExpired();
            if (_cache.Count > CacheCap)
                foreach (var kv in _cache)
                {
                    _cache.TryRemove(kv.Key, out _);
                    break;
                }
        }
        _dirty = true;
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        lock (_saveLock)
        {
            _dirty = true;
            if (_saveTimer is not null) return;
            _saveTimer = new System.Timers.Timer(2000) { AutoReset = false };
            _saveTimer.Elapsed += (_, _) =>
            {
                lock (_saveLock)
                {
                    _saveTimer?.Dispose();
                    _saveTimer = null;
                }
                if (_dirty) SaveNow();
            };
            _saveTimer.Start();
        }
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(CachePath));
            if (!doc.RootElement.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Object) return;
            var cutoff = DateTime.UtcNow.AddMinutes(-CacheTtlMinutes).Ticks;
            foreach (var prop in items.EnumerateObject())
            {
                // v2 格式：{hash:{z:译文,t:最后访问 ticks}}；v1 旧格式（纯字符串）无时间戳直接弃
                if (prop.Value.ValueKind == JsonValueKind.Object
                    && prop.Value.TryGetProperty("z", out var z) && z.ValueKind == JsonValueKind.String
                    && prop.Value.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.Number
                    && t.GetInt64() >= cutoff)
                {
                    _cache[prop.Name] = new CacheEntry(z.GetString() ?? "", t.GetInt64());
                }
            }
        }
        catch { }
    }

    private void SaveNow()
    {
        try
        {
            _dirty = false;
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            var items = new Dictionary<string, object>(_cache.Count);
            foreach (var kv in _cache)
                items[kv.Key] = new { z = kv.Value.Value, t = kv.Value.LastAccessUtcTicks };
            var json = JsonSerializer.Serialize(new { v = 2, items },
                new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            File.WriteAllText(CachePath, json);
        }
        catch { }
    }

    private static string Hash(string text)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes);
    }
}
