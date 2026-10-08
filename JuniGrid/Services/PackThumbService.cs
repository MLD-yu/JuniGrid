using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;

namespace JuniGrid.Services;

/// <summary>
/// 素材包页「按对象」网格的缩略图管线：把游戏原版仓库 / 包副本里的 XNB 纹理裁出一张代表图。
///
/// 裁剪规则按资产种类分（与立绘页同源 —— 同一张表在不同页面长一个样，人才认得出来）：
///  · 立绘（Portraits/*）：左上 64×64 表情格 → 放大到 128，像素风；
///  · 行走图（Characters/*）：左上 16×32 站姿帧 → 放大到 64×128；
///  · 其余（树/房子/光标等整表）：整图等比收进 128 盒，小图最近邻放大。
///
/// 缓存策略：磁盘缓存（mtime+size 入键，源变自动失效）+
/// WebView2 虚拟主机短 URL（本服务自己的 <see cref="ThumbHostBase"/>
/// 及其映射目录，键空间用 jgpt 前缀隔开）+ 内存 LRU。未命中返回 null 并后台生成，
/// 完成后 <see cref="Changed"/> 通知页面重渲染 —— 绝不在 Blazor 渲染线程上同步解码。
/// </summary>
public static class PackThumbService
{
    public enum Kind { Portrait, Sprite, Sheet }

    /// <summary>生成完成（一张或多张）。页面订阅后 InvokeAsync(StateHasChanged)。</summary>
    public static event Action? Changed;

    /// <summary>v2：大图从 512 盒改为原始分辨率，旧 L 渲染全部作废重生。</summary>
    private const string CacheVersion = "v2";
    private const int MemoryCap = 2048;

    /// <summary>缓存目录与立绘页共用同一个：虚拟主机（MainWindow.OnThumbWebResourceRequested）
    /// 只映射这一个目录，另开目录短 URL 会 404。键里带 jgpt 前缀，文件名哈希天然不撞车。
    /// ⚠ 目录名里的 "portrait" 是历史遗留：换肤子系统拆掉后这里仍叫 portrait-covers，
    /// 改名会让所有已生成的素材包缩略图 404 一次（无害但要重生），所以留着不动。</summary>
    private static string CacheDir => StoragePaths.InCache("portrait-covers");

    /// <summary>WebView2 虚拟主机基址（由 MainWindow 挂上；没挂上时 UrlOf 退回 data URI）。
    /// 原来这两个静态字段住在 PortraitSkinService 上，素材包缩略图只是借用同一个通道。</summary>
    public static string? ThumbHostBase;

    /// <summary>缓存根迁移后要重新映射虚拟主机 —— MainWindow 在这里挂回调。</summary>
    public static Action? RemapThumbHost;

    private static readonly ConcurrentDictionary<string, string?> _memory = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> _generating = new(StringComparer.Ordinal);
    private static readonly ConcurrentQueue<string> _order = new();

    /// <summary>生成并发闸：进页预热会一次排产一两百张（整包立绘），解码是 CPU 密集，
    /// 不限流会把线程池和 UI 调度一起拖卡（立绘页踩过同款坑）。任务在闸上排队，不丢弃。</summary>
    private static readonly SemaphoreSlim _gate = new(Math.Clamp(Environment.ProcessorCount, 4, 8),
        Math.Clamp(Environment.ProcessorCount, 4, 8));

    public static Kind KindOf(string asset)
    {
        if (asset.StartsWith("Portraits/", StringComparison.OrdinalIgnoreCase)) return Kind.Portrait;
        if (asset.StartsWith("Characters/", StringComparison.OrdinalIgnoreCase)) return Kind.Sprite;
        return Kind.Sheet;
    }

    /// <summary>拿缩略图 URL。源不存在或未就绪返回 null（后者已排入后台生成，页面画占位）。</summary>
    public static string? Get(string? xnbPath, Kind kind) => GetCore(xnbPath, kind, large: false);

    /// <summary>拿大图 URL（版本弹窗「点击放大」用）：与缩略图同管线、同裁剪规则，
    /// 只是输出尺寸更大 —— 立绘 384、行走图 ×10、整表收进 512 盒。键空间带 L 前缀，与缩略图互不覆盖。</summary>
    public static string? GetLarge(string? xnbPath, Kind kind) => GetCore(xnbPath, kind, large: true);

    private static string? GetCore(string? xnbPath, Kind kind, bool large)
    {
        if (string.IsNullOrWhiteSpace(xnbPath) || !File.Exists(xnbPath)) return null;
        var key = KeyOf(xnbPath, kind, large);
        if (_memory.TryGetValue(key, out var hit)) return hit;

        // 磁盘缓存快路径：直接出 URL，不进生成队列
        var cacheFile = Path.Combine(CacheDir, FileName(key));
        try
        {
            if (File.Exists(cacheFile))
            {
                var url = UrlOf(cacheFile);
                if (url is not null)
                {
                    Remember(key, url);
                    return url;
                }
            }
        }
        catch { /* 快路径失败走下面的生成管线 */ }

        if (_generating.TryAdd(key, 0))
        {
            _ = Task.Run(async () =>
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    // 排到自己时可能已被别的请求生成完 —— 再查一次，别做重复功
                    if (_memory.TryGetValue(key, out _)) return;
                    var url = Generate(key, kind, xnbPath, cacheFile, large);
                    if (url is not null)
                    {
                        Remember(key, url);
                        Changed?.Invoke();
                    }
                }
                catch (Exception ex) { AppLog.Warn("PackThumbs", ex.Message); }
                finally
                {
                    _generating.TryRemove(key, out _);
                    _gate.Release();
                }
            });
        }
        return _memory.TryGetValue(key, out var v) ? v : null;
    }

    /// <summary>后台预热一批：网格封面进页就排产，不等点开才现切。重复调用安全（去重）。</summary>
    public static void Prewarm(IEnumerable<(string Path, Kind Kind)> jobs, bool large = false)
    {
        foreach (var (path, kind) in jobs) _ = GetCore(path, kind, large);
    }

    // ───────────────────── 内部 ─────────────────────

    private static string? Generate(string key, Kind kind, string src, string cacheFile, bool large)
    {
        var tex = XnbDecoder.TryDecode(src);
        // 字体图集是单通道遮罩块（不是 Color），上面那条路必然解不出 —— 只有这里能救它，
        // 所以素材页的字体卡从"灰 Aa 占位"变成真字形表。
        if (tex is null) tex = XnbDecoder.TryDecodeFont(src);
        // 包里混进来的 PNG 副本也认（旧版本存过 PNG 包编码前的源则没有，这里只兜 XNB 解不出的情况）
        if (tex is null && src.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            tex = PixelKit.DecodePng(src);
        if (tex is null) return null;

        byte[] png;
        switch (kind)
        {
            case Kind.Portrait:
            {
                var cell = Math.Min(64, Math.Min(tex.Width, tex.Height));
                var outSize = large ? 384 : 128;
                png = PixelKit.CropScalePng(tex, 0, 0, cell, cell, outSize, outSize);
                break;
            }
            case Kind.Sprite:
            {
                var fw = Math.Min(16, tex.Width);
                var fh = Math.Min(32, tex.Height);
                var mul = large ? 10 : 4;
                png = PixelKit.CropScalePng(tex, 0, 0, fw, fh, fw * mul, fh * mul);
                break;
            }
            default:
            {
                double scale;
                if (large)
                {
                    // 大图给原始分辨率：弹层里 Ctrl+滚轮放到 8 倍，放大的必须是 1:1 像素
                    // 才有细节可看 —— 之前收进 512 盒，NN 下采样直接丢像素（"放大失帧"的根因），
                    // 浏览器再拉回来只剩糊。只有超过 2048 的巨表才收（PNG 体积保护）。
                    scale = Math.Min(1.0, Math.Min(2048.0 / tex.Width, 2048.0 / tex.Height));
                }
                else
                {
                    // 缩略图：整图等比收进 128 盒；小图放大有上限
                    // （一张 16×16 的草放到 2048 就成色块墙了）
                    scale = Math.Min(128.0 / tex.Width, 128.0 / tex.Height);
                    if (scale > 1) scale = Math.Min(scale, 4);
                }
                var dw = Math.Max(1, (int)Math.Round(tex.Width * scale));
                var dh = Math.Max(1, (int)Math.Round(tex.Height * scale));
                png = PixelKit.CropScalePng(tex, 0, 0, tex.Width, tex.Height, dw, dh);
                break;
            }
        }

        try
        {
            Directory.CreateDirectory(CacheDir);
            File.WriteAllBytes(cacheFile, png);
        }
        catch (Exception ex) { AppLog.Warn("PackThumbs", "写缓存失败: " + ex.Message); }
        return UrlOf(cacheFile) ?? "data:image/png;base64," + Convert.ToBase64String(png);
    }

    private static string KeyOf(string path, Kind kind, bool large)
    {
        string stat;
        try { stat = $"{File.GetLastWriteTimeUtc(path):yyyyMMddHHmmss}-{new FileInfo(path).Length}"; }
        catch { stat = "err"; }
        var size = large ? "L" : "S";
        return $"jgpt|{CacheVersion}|{size}|{kind}|{stat}|{Convert.ToHexString(SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(path)))[..12]}";
    }

    private static string FileName(string key)
        => Convert.ToHexString(SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(key))) + ".png";

    private static string? UrlOf(string cacheFile)
    {
        if (ThumbHostBase is { } host)
            return host + "/pt/" + Path.GetFileName(cacheFile);
        try
        {
            var png = File.ReadAllBytes(cacheFile);
            return "data:image/png;base64," + Convert.ToBase64String(png);
        }
        catch { return null; }
    }

    private static void Remember(string key, string? url)
    {
        _memory[key] = url;
        _order.Enqueue(key);
        while (_memory.Count > MemoryCap && _order.TryDequeue(out var old))
            _memory.TryRemove(old, out _);
    }
}
