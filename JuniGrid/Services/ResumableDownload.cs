using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace JuniGrid.Services;

/// <summary>
/// v1.07：统一的断点续传流式下载器。此前 Nexus / SMAPI 两条下载路径都是裸流式，
/// 中途掉连接（Nexus 免费 CDN 很常见）整个任务就失败或从 0 重下 —— 也就是
/// 「下载到 3% 左右突然跳回 0% 重新下载」的根源。现在中途失败自动带
/// Range: bytes=written- 续传，最多重试 maxAttempts-1 次；服务器不支持
/// Range（返回 200 而非 206）时才真正从 0 开始。
/// 进度回报统一按 0.4s 节流（原先每个 80KB 块都回调一次，大文件会狂刷 UI 线程）。
/// </summary>
public static class ResumableDownload
{
    public static async Task RunAsync(HttpClient http, string url, string destPath,
        Action<string, double?, double?> report, int maxAttempts = 5)
    {
        long written = 0, totalBytes = 0;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (written > 0)
                    req.Headers.Range = new RangeHeaderValue(written, null);
                using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                res.EnsureSuccessStatusCode();

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
                while (true)
                {
                    int read = await src.ReadAsync(buffer);
                    if (read == 0) break;
                    await dst.WriteAsync(buffer.AsMemory(0, read));
                    written += read;

                    var percent = totalBytes <= 0 ? 0.0 : Math.Min(100.0, written * 100.0 / totalBytes);
                    double? speed = null;
                    if (DateTime.UtcNow - lastReport > TimeSpan.FromSeconds(0.4)
                        || (totalBytes > 0 && written >= totalBytes))
                    {
                        speed = (written - lastWritten) / (DateTime.UtcNow - lastReport).TotalSeconds / 1024.0 / 1024.0;
                        lastReport = DateTime.UtcNow;
                        lastWritten = written;
                    }
                    report($"正在下载… {FormatBytes(written)} / {FormatBytes(totalBytes)}", percent, speed);
                }
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                var pct = totalBytes > 0 ? Math.Min(99.0, written * 100.0 / totalBytes) : 0.0;
                report($"连接中断（{ex.Message}），从 {FormatBytes(written)} 处续传（重试 {attempt}/{maxAttempts - 1}）…", pct, 0);
                await Task.Delay(TimeSpan.FromSeconds(attempt));
            }
        }
    }

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
