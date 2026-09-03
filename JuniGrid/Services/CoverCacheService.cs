using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace JuniGrid.Services;

/// <summary>
/// v1.08：Nexus 图片本地缓存 —— 图片 CDN（staticdelivery.nexusmods.com）国内直连极慢，
/// WebView2 直接加载远程 URL 会导致列表/Nexus 页图片长时间空白。
/// 这里把封面图后台下载一份落盘（LocalAppData/JuniGrid/covers/&lt;sha1&gt;.img），
/// 之后 <see cref="Get"/> 返回本地 data URI，图片展示彻底摆脱 Nexus 网络。
/// 下载失败只放弃本次会话（不再反复重试拖慢渲染），下次启动会重试。
/// </summary>
public sealed class CoverCacheService
{
    private static readonly HttpClient Http = CreateHttp();
    private static HttpClient CreateHttp()
    {
        var h = new HttpClient();
        h.DefaultRequestHeaders.UserAgent.ParseAdd("JuniGrid-Launcher");
        h.Timeout = TimeSpan.FromSeconds(20);
        return h;
    }

    private static string CacheDir => Path.Combine(StoragePaths.LocalAppDataDir, "covers");

    /// <summary>url → data URI（null = 本次会话下载失败，不再重试）。</summary>
    private readonly ConcurrentDictionary<string, string?> _memory = new();
    private readonly ConcurrentDictionary<string, byte> _downloading = new();
    /// <summary>并发闸：Nexus CDN 国内链路脆弱，图片下载最多 8 路并发。</summary>
    private static readonly SemaphoreSlim DownloadGate = new(8, 8);

    /// <summary>有新图片下载完成时触发（UI 订阅后刷新渲染）。
    /// v1.08：500ms 防抖合并 —— 首次进页面几十张图并行下载，每张都触发一次
    /// 整页重渲染会把页面卡死；合并成每 500ms 最多刷新一次。</summary>
    public event Action? Changed;
    private int _notifyPending;

    private void NotifyChanged()
    {
        if (Interlocked.Exchange(ref _notifyPending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            Interlocked.Exchange(ref _notifyPending, 0);
            Changed?.Invoke();
        });
    }

    /// <summary>
    /// 渲染时调用：已缓存 → 返回 data URI；未缓存 → 触发后台下载并返回 null
    /// （UI 显示占位块，下载完成后经 <see cref="Changed"/> 刷新）。
    /// 非 http URL（本地路径/data URI）原样返回。
    /// </summary>
    public string? Get(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return url;
        if (_memory.TryGetValue(url, out var cached)) return cached;
        _ = DownloadAsync(url);
        return null;
    }

    /// <summary>
    /// v1.08：批量预取 —— 进页后立即后台并发下载已知封面，用户滚动到时
    /// 本地已就绪（首次加载的体感优化：抢跑 + 只下一次）。
    /// 已缓存的 url 会直接短路返回，不会重复发请求。
    /// </summary>
    public void Prewarm(IEnumerable<string?> urls)
    {
        foreach (var url in urls)
        {
            if (string.IsNullOrWhiteSpace(url)) continue;
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
            if (_memory.ContainsKey(url)) continue;
            _ = DownloadAsync(url);
        }
    }

    private async Task DownloadAsync(string url)
    {
        if (!_downloading.TryAdd(url, 0)) return;   // 已在下载中
        try
        {
            Directory.CreateDirectory(CacheDir);

            // v1.08：下载即缩略 —— 优先走 weserv 代理压到 240px 小图（十几 KB，
            // 列表渲染/解码开销降一个数量级）；代理失败回退直连原图。
            var file = Path.Combine(CacheDir, "w240-" + Sha1(url) + ".img");
            byte[] bytes;
            if (File.Exists(file))
            {
                bytes = await File.ReadAllBytesAsync(file);
            }
            else
            {
                await DownloadGate.WaitAsync();
                try
                {
                    var thumbUrl = "https://images.weserv.nl/?url=" +
                                   Uri.EscapeDataString(url) + "&w=240&output=jpg";
                    try { bytes = await Http.GetByteArrayAsync(thumbUrl); }
                    catch { bytes = await Http.GetByteArrayAsync(url); }   // 回退原图
                }
                finally { DownloadGate.Release(); }
                if (bytes.Length == 0) throw new InvalidOperationException("空图片");
                await File.WriteAllBytesAsync(file, bytes);
            }

            _memory[url] = ToDataUri(bytes);
            NotifyChanged();
        }
        catch
        {
            _memory[url] = null;   // 本会话放弃，占位块兜底
        }
        finally
        {
            _downloading.TryRemove(url, out _);
        }
    }

    private static string Sha1(string s)
    {
        using var sha = SHA1.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(s)));
    }

    /// <summary>按文件头识别图片类型（Nexus 封面多为 webp/jpeg/png/gif）。</summary>
    private static string ToDataUri(byte[] b)
    {
        string type = "image/jpeg";
        if (b.Length > 12 && b[0] == 0x89 && b[1] == 0x50) type = "image/png";
        else if (b.Length > 3 && b[0] == 0x47 && b[1] == 0x49) type = "image/gif";
        else if (b.Length > 12 && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) type = "image/webp";
        return $"data:{type};base64,{Convert.ToBase64String(b)}";
    }
}
