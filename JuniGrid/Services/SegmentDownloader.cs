using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace JuniGrid.Services;

/// <summary>
/// v1.3.13：多连接分片下载（**实验性，默认关闭**）。
/// <para>
/// 为什么要有它：现有的 <see cref="ResumableDownload"/> 是单连接流式 —— 一条 TCP 跑满就到顶。
/// N 网免费账号实测单流 ~1MB/s，而 CDN 那侧常常是「每个连接各给一份配额」，
/// 于是同一个文件开 3~4 条 Range 并发能实打实快几倍。
/// </para>
/// <para>
/// 为什么默认关：多连接对 CDN 是不是友善、会不会吃 429，各家不一样。所以这一版的做法是
/// <b>先探一次、再做</b>：只有服务器明确回 <c>206 + Content-Range</c>（说明支持 Range）、
/// 文件够大（&gt; 4MB）、且用户在设置里主动打开时才分片；任何一片出错整体放弃、
/// 删掉半截文件、回落到单连接从头下 —— 最坏情况等于今天，不会更差。
/// </para>
/// <para>
/// 只在「目标文件不存在」时启用：分片各写各的区间，和单连接的「从头续写」语义不兼容，
/// 续传/暂停恢复一律交给 <see cref="ResumableDownload"/>。
/// </para>
/// </summary>
public static class SegmentDownloader
{
    /// <summary>小于这个大小分片没意义（握手开销比省下的时间还多）。</summary>
    private const long MinTotalBytes = 4L * 1024 * 1024;

    /// <summary>每片至少这么大，否则把 4 条车道挤成 20 个碎请求。</summary>
    private const long MinBytesPerLane = 512L * 1024;

    /// <summary>车道数上限。再多只会把 CDN 惹毛。</summary>
    private const int MaxLanes = 6;

    /// <summary>设置项由启动处挂进来（NexusService 是静态类，不能反依赖 DI 的 ConfigService）。
    /// 没挂 = 永远关闭，行为与 v1.3.12 完全一致。</summary>
    public static Func<bool>? EnabledHook;

    public static bool Enabled => EnabledHook?.Invoke() == true;

    /// <summary>车道数（用户可在设置里 1~6 选；1 = 等于不分片）。</summary>
    public static Func<int>? LanesHook;

    public static int Lanes
    {
        get
        {
            var n = LanesHook?.Invoke() ?? 4;
            return Math.Clamp(n, 1, MaxLanes);
        }
    }

    /// <summary>试着用分片下完整个文件。
    /// <returns>true = 文件已经完整落在 <paramref name="destPath"/>；
    /// false = 这条路没走通（不支持 Range / 文件太小 / 某片挂了），调用方请回落到单连接。
    /// 取消会原样抛 <see cref="OperationCanceledException"/>，不吞。</returns>
    /// </summary>
    public static async Task<bool> TryRunAsync(HttpClient http, string url, string destPath,
        int lanes, Action<string, double?, double?> report, CancellationToken ct = default)
    {
        if (lanes < 2) return false;
        // 已有目标文件 = 续传/覆盖场景，分片的洞补不了，交给单连接
        if (File.Exists(destPath)) return false;

        long total;
        try { total = await ProbeAsync(http, url, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { return false; }                       // 探测失败 → 当不支持，别赌
        if (total < MinTotalBytes) return false;

        lanes = (int)Math.Min(lanes, Math.Max(1, total / MinBytesPerLane));
        if (lanes < 2) return false;

        var part = destPath + ".seg";
        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        try { File.Delete(part); } catch { }

        try
        {
            using (var pre = new FileStream(part, FileMode.Create, FileAccess.Write,
                       FileShare.ReadWrite, 81920, useAsync: true))
                pre.SetLength(total);                 // 预分配：各车道才能直接 Seek 写自己的区间

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            long done = 0;
            var gate = new object();
            var clock = Stopwatch.StartNew();
            var lastAt = TimeSpan.Zero;
            long lastBytes = 0;

            void OnBytes(long add)
            {
                long snapshot;
                TimeSpan now;
                lock (gate)
                {
                    done += add;
                    snapshot = done;
                    now = clock.Elapsed;
                    // 1 秒节流（与单连接同口径）：每 80KB 刷一次会把 UI 线程刷爆
                    if (now - lastAt < TimeSpan.FromSeconds(1) && snapshot != total) return;
                    lastAt = now;
                }
                var speed = (snapshot - lastBytes) / Math.Max(now.TotalSeconds, 0.001) / 1024.0 / 1024.0;
                lastBytes = snapshot;
                var pct = Math.Min(100.0, snapshot * 100.0 / total);
                report($"正在下载（{lanes} 线并行）… {ResumableDownload.FormatBytes(snapshot)} / "
                       + ResumableDownload.FormatBytes(total), pct, speed);
            }

            var seg = total / lanes;
            var tasks = new List<Task>(lanes);
            for (var i = 0; i < lanes; i++)
            {
                var from = i * seg;
                var to = i == lanes - 1 ? total - 1 : (i + 1) * seg - 1;
                tasks.Add(LaneAsync(http, url, part, from, to, OnBytes, linked.Token));
            }

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch
            {
                // 一片挂 = 整体放弃。先掐掉还在跑的兄弟，再清场
                try { linked.Cancel(); } catch { }
                try { File.Delete(part); } catch { }
                throw;
            }

            if (new FileInfo(part).Length != total)
            {
                try { File.Delete(part); } catch { }
                return false;
            }

            File.Move(part, destPath, overwrite: true);
            report($"正在下载（{lanes} 线并行）… {ResumableDownload.FormatBytes(total)} / "
                   + ResumableDownload.FormatBytes(total), 100, 0);
            return true;
        }
        catch (OperationCanceledException)
        {
            try { File.Delete(part); } catch { }
            throw;
        }
        catch
        {
            try { File.Delete(part); } catch { }
            return false;                             // 让调用方走单连接
        }
    }

    /// <summary>探一次：服务器认不认 Range、这个文件到底多大。
    /// 用 <c>bytes=0-0</c> 只要 1 字节，比 HEAD 可靠（部分 CDN 不实现 HEAD）。
    /// <para>自带 20 秒上限：下载客户端整体 30 分钟超时，一个不吭声的节点会把探测挂在那儿
    /// 半小时 —— 探测本来就只是"试试看"，超时一律当"不支持"，立刻回落到单连接。</para></summary>
    private static async Task<long> ProbeAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new RangeHeaderValue(0, 0);
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (res.StatusCode != HttpStatusCode.PartialContent) return -1;
            var len = res.Content.Headers.ContentRange?.Length;
            return len is > 0 ? len.Value : -1;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return -1;                                // 探测超时 ≠ 用户取消
        }
    }

    /// <summary>一条车道：把 [from, to] 这段字节写到 <paramref name="path"/> 对应偏移。
    /// 中途断线在本车道内自愈（最多 <see cref="LaneAttempts"/> 次，每次从 cursor 继续），
    /// 不劳烦整个任务重启。</summary>
    private const int LaneAttempts = 4;

    private static async Task LaneAsync(HttpClient http, string url, string path,
        long from, long to, Action<long> onBytes, CancellationToken ct)
    {
        var cursor = from;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Range = new RangeHeaderValue(cursor, to);
                using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                if (res.StatusCode is not (HttpStatusCode.PartialContent or HttpStatusCode.OK))
                    throw new HttpRequestException($"HTTP {(int)res.StatusCode}");
                // 服务器忽略 Range 回 200 时不能照写（那是整个文件，写进去会错位）
                if (res.StatusCode == HttpStatusCode.OK) throw new InvalidOperationException("服务器忽略了 Range");

                await using var src = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = new FileStream(path, FileMode.Open, FileAccess.Write,
                    FileShare.ReadWrite, 81920, useAsync: true);
                dst.Seek(cursor, SeekOrigin.Begin);

                var buffer = new byte[81920];
                while (cursor <= to)
                {
                    var want = (int)Math.Min(buffer.Length, to - cursor + 1);
                    var read = await src.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
                    if (read == 0) break;
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    cursor += read;
                    onBytes(read);
                }
                if (cursor > to) return;              // 这片齐了
                throw new IOException($"分片没下完（{cursor}/{to + 1}）");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) when (attempt < LaneAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct).ConfigureAwait(false);
            }
        }
    }
}
