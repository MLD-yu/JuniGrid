using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace JuniGrid.Services;

/// <summary>
/// v1.07：统一的断点续传流式下载器。此前 Nexus / SMAPI 两条下载路径都是裸流式，
/// 中途掉连接（Nexus 免费 CDN 很常见）整个任务就失败或从 0 重下 —— 也就是
/// 「下载到 3% 左右突然跳回 0% 重新下载」的根源。现在中途失败自动带
/// Range: bytes=written- 续传，最多重试 MaxAttempts-1 次；服务器不支持
/// Range（返回 200 而非 206）时才真正从 0 开始。
/// 进度回报统一按 0.4s 节流（原先每个 80KB 块都回调一次，大文件会狂刷 UI 线程）。
/// </summary>
public static class ResumableDownload
{
    private const int MaxAttempts = 5;
    // v1.1.8：读流停滞看门狗。HttpClient.Timeout 在 ResponseHeadersRead 之后
    // 并不覆盖 body 流读取；半开 TCP 会让 ReadAsync 永久阻塞，进度永远停在
    // 最后一帧（实测界面卡在「91% · 3.1 M/s」假死）。45 秒收不到任何字节就
    // 掐断本连接，走已有的 Range 续传重试。
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(45);

    public static async Task RunAsync(HttpClient http, string url, string destPath,
        Action<string, double?, double?> report, CancellationToken ct = default,
        IEnumerable<string>? fallbackUrls = null)
    {
        // v1.08：镜像候选 —— 直连失败且尚未写入字节时立刻切换下一候选，
        // 不再在死链上耗尽全部重试（旧逻辑 4 次重试 ≈ 干等 80 秒才轮到镜像）。
        // 已有半截数据时优先在当前主机续传（候选主机字节一致，续传也随时可换）。
        var candidates = new List<string> { url };
        if (fallbackUrls is not null) candidates.AddRange(fallbackUrls);
        var candIndex = 0;

        long written = 0, totalBytes = 0;
        // v1.1.7：暂停后继续 —— 目标文件已有半截数据时从断点发起 Range 请求
        if (File.Exists(destPath))
        {
            var existing = new FileInfo(destPath).Length;
            if (existing > 0) written = existing;
        }
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, candidates[candIndex]);
                if (written > 0)
                    req.Headers.Range = new RangeHeaderValue(written, null);
                using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                // v1.1.7：本地已有完整文件时服务器回 416 —— 视为下载已完成，直接进入安装段
                if (res.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && written > 0)
                {
                    report($"已下载完成（{FormatBytes(written)}）", 100, 0);
                    return;
                }
                // v1.1.2：4xx（除 408 请求超时 / 429 限流）是永久性错误 —— 404 链接失效、403 无权限，
                // 重试满 5 次只是白等 15 秒+，立即把失败交给任务中心
                if (!res.IsSuccessStatusCode && (int)res.StatusCode < 500
                    && (int)res.StatusCode != 408 && (int)res.StatusCode != 429)
                    throw new PermanentDownloadException($"HTTP {(int)res.StatusCode}（链接失效或无权限，已放弃重试）");
                res.EnsureSuccessStatusCode();

                // v1.6.2：镜像节点可能拿 200 状态回 HTML 错误页（实测 ghfast 偶发
                // "Invalid input."）—— 本应用下载的全是二进制资源，收到 text/html
                // 一律视为该候选失效，走重试逻辑立刻换下一个镜像
                var ctype = res.Content.Headers.ContentType?.MediaType ?? "";
                if (ctype.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"候选源返回的是网页而非文件（{ctype}）");

                // 206 = 续传成功接着写；200 = 服务器不给断点（或全新下载）→ 从头写
                var resumed = written > 0 && res.StatusCode == HttpStatusCode.PartialContent;
                if (!resumed)
                {
                    written = 0;
                    totalBytes = res.Content.Headers.ContentLength ?? 0;
                }
                else if (res.Content.Headers.ContentRange?.Length is long len)
                {
                    totalBytes = len;
                }

                await using var src = await res.Content.ReadAsStreamAsync();
                await using var dst = new FileStream(destPath, resumed ? FileMode.Append : FileMode.Create,
                    FileAccess.Write, FileShare.None, 81920, useAsync: true);

                var buffer = new byte[81920];
                var lastReport = DateTime.UtcNow;
                var lastWritten = written;
                // 每次成功读到字节就重置；超时抛 OCE 且外层 ct 未取消 → 当作连接停滞
                using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                while (true)
                {
                    stallCts.CancelAfter(StallTimeout);
                    int read;
                    try
                    {
                        read = await src.ReadAsync(buffer, stallCts.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new IOException($"下载连接停滞（{StallTimeout.TotalSeconds:0} 秒无数据）");
                    }
                    if (read == 0) break;
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    written += read;

                    // v1.08.2：节流必须包住 report 本身 —— 旧代码只对 speed 计算设了
                    // 1s 门限，report 仍然每 80KB 块回调一次，一个 12MB 安装包就是
                    // 150+ 次 InvokeAsync 刷爆 UI 线程（下载期间整个界面冻结的根源），
                    // 且每条都进任务日志，>200 条会把开头的备份/解压日志全顶出去。
                    var done = totalBytes > 0 && written >= totalBytes;
                    if (done || DateTime.UtcNow - lastReport > TimeSpan.FromSeconds(1.0))
                    {
                        var percent = totalBytes <= 0 ? 0.0 : Math.Min(100.0, written * 100.0 / totalBytes);
                        var speed = (written - lastWritten)
                                    / Math.Max((DateTime.UtcNow - lastReport).TotalSeconds, 0.001)
                                    / 1024.0 / 1024.0;
                        lastReport = DateTime.UtcNow;
                        lastWritten = written;
                        report($"正在下载… {FormatBytes(written)} / {FormatBytes(totalBytes)}", percent, speed);
                    }
                }
                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts
                && ex is not OperationCanceledException
                && ex is not PermanentDownloadException)
            {
                var pct = totalBytes > 0 ? Math.Min(99.0, written * 100.0 / totalBytes) : 0.0;
                if (written == 0 && candIndex < candidates.Count - 1)
                {
                    // 一个字节都没下到（连接不通）→ 立刻换镜像，不等重试耗尽
                    candIndex++;
                    report($"直连失败，切换镜像下载（{candIndex}/{candidates.Count - 1}）…", 0, 0);
                    await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
                }
                else
                {
                    report($"连接中断（{ex.Message}），从 {FormatBytes(written)} 处续传（重试 {attempt}/{MaxAttempts - 1}）…", pct, 0);
                    await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
                }
            }
        }
    }

    /// <summary>v1.1.2：4xx 等永久性下载错误 —— 重试没有意义，立即失败（由任务中心显示原因）</summary>
    public sealed class PermanentDownloadException(string message) : Exception(message);

    public static string FormatBytes(long bytes)
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
}
