using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace JuniGrid.Services;

public sealed partial class PortraitSkinService
{
    // ══════════════════════ 缩略图（磁盘缓存 + data URI） ══════════════════════

    private const string CacheVersion = "v8";   // 裁剪规则变了必须升版本

    private static string CacheDir => StoragePaths.InCache("portrait-covers");

    private readonly ConcurrentDictionary<string, string?> _memory = new();
    private readonly ConcurrentDictionary<string, byte> _generating = new();
    private readonly ConcurrentQueue<string> _memoryOrder = new();
    private const int MemoryCap = 1024;

    /// <summary>缓存键 = 版本 + 种类 + 源路径 + mtime + size（源文件更新自动失效）。
    /// ⚠ 不含"卡片/弹窗"这类范围——同一来源文件渲染结果相同，必须共享缓存，
    /// 否则打开弹窗时每个格子都要重新生成，先出一片占位图（实机用户反馈）。
    /// v1.7.1：stat 结果也缓存 —— 渲染每帧对每个缩略图 FileInfo，OneDrive/冷盘上是热点。</summary>
    private static readonly ConcurrentDictionary<string, string> SrcStatCache = new(StringComparer.OrdinalIgnoreCase);

    private static string CoverKey(ThumbKind kind, string? src)
    {
        string srcPart;
        try
        {
            if (src is null) srcPart = "none";
            else
            {
                srcPart = SrcStatCache.GetOrAdd(src, s =>
                {
                    try { return $"{File.GetLastWriteTimeUtc(s):yyyyMMddHHmmss}-{new FileInfo(s).Length}"; }
                    catch { return "err"; }
                });
                if (srcPart == "err")
                {
                    // 之前失败过，重试一次（文件可能刚生成）
                    try
                    {
                        srcPart = $"{File.GetLastWriteTimeUtc(src):yyyyMMddHHmmss}-{new FileInfo(src).Length}";
                        SrcStatCache[src] = srcPart;
                    }
                    catch { }
                }
            }
        }
        catch { srcPart = "err"; }
        // ⚠ 键里必须带路径哈希 —— 只用 mtime+size 时，同一次解压的两张同尺寸图
        //（SCC 的 Kent_Spring / Morris_Spring）会撞键，缩略图互相覆盖（莫里斯显示成肯特）。
        return $"{CacheVersion}|{kind}|{srcPart}|{Sha1(src ?? "none")[..12]}";
    }

    /// <summary>角色卡片封面 = 当前生效大头照（选中包 → 原版 xnb → 娘家包）。未就绪返回 null。
    /// 锁定中优先显示 PinFile；一键恢复默认的原版角色强制原版 xnb。</summary>
    public string? GetCharacterCover(string gamePath, PortraitCharacter ch, string? selectedPackFolder)
    {
        string? src = null;
        var lk = GetLock(_cfg.Current, ch.Id);
        if (lk?.PinFile is not null && File.Exists(lk.PinFile))
            src = lk.PinFile;
        if (src is null && selectedPackFolder is not null)
        {
            // v1.7.7：网格封面得跟「用户选的那个画风」一致，否则选第二张卡、格子上还是第一张
            var opt = MatchOption(ch.AllOptions, selectedPackFolder,
                GetMemberVariant(_cfg.Current, ch, null), ch.Id);
            if (opt is not null) src = opt.SourceFile;
        }
        if (src is null && ch.IsVanilla)
        {
            var trueVanilla = _cfg.Current.PortraitTrueVanilla
                .Contains(ch.Id, StringComparer.OrdinalIgnoreCase);
            // 一键恢复默认（PortraitTrueVanilla）→ 必须原版 xnb；
            // 否则无选择时封面跟随「默认行」（扩展包增强像 = 游戏实际显示）
            var xnb = VanillaPortraitXnb(gamePath, ch.Id);
            if (trueVanilla || selectedPackFolder is not null
                || _cfg.Current.PortraitSkins.ContainsKey(ch.Id))
                src = xnb;
            else
                src = ch.Vanilla?.SourceFile ?? xnb;
        }
        if (src is null) src = ch.Native?.SourceFile;
        // 娘家检测没中（注册形式没解析到）但有皮肤来源的 mod 角色：用皮肤来源兜底
        if (src is null) src = ch.Skins.Select(s => s.SourceFile).FirstOrDefault(s => s is not null);
        if (src is null) return null;
        return RequestThumb(CoverKey(ThumbKind.Portrait, src), ThumbKind.Portrait, src);
    }

    /// <summary>弹窗右侧的精灵图预览（整张表，等比缩到 ≤720）。</summary>
    public string? GetSpriteThumb(PortraitSkinOption skin) =>
        skin.SpriteFile is null
            ? null
            : RequestThumb(CoverKey(ThumbKind.Sprite, skin.SpriteFile), ThumbKind.Sprite, skin.SpriteFile);

    /// <summary>弹窗皮肤格缩略图（v2：全部用大头照）。</summary>
    public string? GetSkinThumb(PortraitCharacter ch, PortraitSkinOption skin) =>
        skin.SourceFile is null
            ? null
            : RequestThumb(CoverKey(ThumbKind.Portrait, skin.SourceFile), ThumbKind.Portrait, skin.SourceFile);

    /// <summary>任意路径的立绘缩略图（弹窗季节预览用，与皮肤格同一生成管线/缓存）。
    /// sync=true：缓存未命中时当场生成再返回 —— 弹窗里的图必须立即出现，
    /// 不允许"先空白占位、后台慢慢补"（用户实测换季闪空白）。单张仅几十毫秒。</summary>
    public string? GetPortraitThumbByPath(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? null
            : RequestThumb(CoverKey(ThumbKind.Portrait, path), ThumbKind.Portrait, path, sync: true);

    /// <summary>任意路径的精灵表缩略图（弹窗季节预览用，同步）。</summary>
    public string? GetSpriteThumbByPath(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? null
            : RequestThumb(CoverKey(ThumbKind.Sprite, path), ThumbKind.Sprite, path, sync: true);

    /// <summary>弹窗打开时预生成该角色全部皮肤的四季变体缩略图（后台、去重）——
    /// 用户点季节按钮时全部零等待。</summary>
    public void PrewarmSeasonThumbs(PortraitCharacter ch)
    {
        foreach (var opt in ch.AllOptions)
        {
            // v1.6.8：按角色 id 收集季节文件（每角色文件夹布局/冬季 Indoor 拆分）
            foreach (var f in GetSeasonFilesForChar(opt.SourceFile, ch.Id).Values)
                _ = RequestThumb(CoverKey(ThumbKind.Portrait, f), ThumbKind.Portrait, f);
            foreach (var f in GetSeasonFilesForChar(opt.SpriteFile, ch.Id).Values)
                _ = RequestThumb(CoverKey(ThumbKind.Sprite, f), ThumbKind.Sprite, f);
        }
    }

    /// <summary>预热：把扫描结果里所有角色的全部皮肤缩略图生成一遍（后台、去重）。
    /// 同一次扫描签名只跑一遍 —— 每次进立绘页都全量预热是「肖像全在重载」的元凶。</summary>
    private static string _prewarmedSig;

    public void PrewarmThumbs(string gamePath, PortraitScanResult scan)
    {
        var sig = _memScanSig;
        if (sig is not null && string.Equals(sig, _prewarmedSig, StringComparison.Ordinal))
            return;
        _prewarmedSig = sig ?? "";
        foreach (var ch in scan.Characters)
        {
            if (ch.Hidden) continue;
            if (ch.IsVanilla)
            {
                var coverSrc = ch.Vanilla?.SourceFile;
                if (coverSrc is not null)
                    _ = RequestThumb(CoverKey(ThumbKind.Portrait, coverSrc), ThumbKind.Portrait, coverSrc);
            }
            foreach (var opt in ch.AllOptions)
            {
                _ = GetSkinThumb(ch, opt);
                _ = GetSpriteThumb(opt);
            }
            _ = GetCharacterCover(gamePath, ch, null);
        }
    }

    private string? RequestThumb(string key, ThumbKind kind, string src, bool sync = false)
    {
        if (_memory.TryGetValue(key, out var cached)) return cached;
        // v1.3.4：生成并发限 4 路 —— 算法升级（alg2）后的全量重建是几百张图的
        // 解码+逐帧分析+编码，无限制地 Task.Run 会占满线程池，把整个应用的
        // UI 调度一起拖卡（用户实测"刚开始什么都不显示，过一会儿才好"）。
        if (_generating.TryAdd(key, 0))
        {
            if (sync)
            {
                // 同步路径（弹窗按需取图）：绕开后台队列当场生成。单张几十毫秒，
                // 不占 _thumbGate 名额（避免撞上页面级预热洪峰时把渲染线程卡死）。
                try
                {
                    var uri = GenerateThumbDataUri(key, kind, src);
                    // v1.6.8：失败不写负缓存 —— 瞬时失败（切版本文件占用等）被永久记住
                    // 会让卡片永远空白（实测）。下次渲染自动重试，_generating 去重防惊群。
                    if (uri is not null) Remember(key, uri);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Portraits", "缩略图生成失败: " + ex.Message);
                }
                finally { _generating.TryRemove(key, out _); }
            }
            else
            {
                _thumbGate.Wait();
                try
                {
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            // 后台预热让路给版本切换（同步路径不让：那会冻住渲染线程，
                            // 单张几十毫秒的读盘由切换侧的改名重试兜住）
                            using var lease = ReadLease();
                            var uri = GenerateThumbDataUri(key, kind, src);
                            // v1.6.8：失败不写负缓存（同上），下次预热自动重试
                            if (uri is not null)
                            {
                                Remember(key, uri);
                                NotifyChanged();
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLog.Warn("Portraits", "缩略图生成失败: " + ex.Message);
                        }
                        finally
                        {
                            _generating.TryRemove(key, out _);
                            _thumbGate.Release();
                        }
                    });
                }
                catch
                {
                    _generating.TryRemove(key, out _);
                    _thumbGate.Release();
                    throw;
                }
            }
        }
        return _memory.TryGetValue(key, out var v) ? v : null;
    }

    /// <summary>缩略图生成并发闸（4 路）—— 解码/编码都是 CPU 密集，防线程池饥饿。</summary>
    private static readonly SemaphoreSlim _thumbGate = new(4, 4);

    private void Remember(string key, string? uri)
    {
        _memory[key] = uri;
        _memoryOrder.Enqueue(key);
        while (_memory.Count > MemoryCap && _memoryOrder.TryDequeue(out var old))
            _memory.TryRemove(old, out _);
    }

    private enum ThumbKind { Portrait, Sprite }

    private string? GenerateThumbDataUri(string cacheKey, ThumbKind kind, string src)
    {
        Directory.CreateDirectory(CacheDir);
        // v1.7.7：类别不符直接空 —— 精灵当头像裁出来是放大的一角（吉尔大头照），
        // 头像当精灵整表缩放是小人图集。没有正面像就留空（与苏琪空面板一致）。
        if (!src.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase))
        {
            if (kind == ThumbKind.Portrait && !LooksLikePortrait(src)) return null;
            if (kind == ThumbKind.Sprite && !LooksLikeSprite(src)) return null;
        }
        // 磁盘缓存文件名 = 键哈希（键已含版本+mtime+size，天然失效正确；哈希也避免
        // 包 Folder 里的 / 出现在文件路径里 —— v1 踩过的坑）。v1.3.4：预览算法升级
        //（空白帧扫描回退）—— 键追加了算法版本号，旧空白缓存图不会命中。
        var cacheFile = Path.Combine(CacheDir, Sha1(cacheKey + "|alg3") + ".png");
        byte[] png;
        if (File.Exists(cacheFile))
        {
            png = File.ReadAllBytes(cacheFile);
        }
        else
        {
            DecodedTexture? tex = null;
            if (src.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase))
                tex = XnbDecoder.TryDecode(src);
            if (tex is null && File.Exists(src))
                tex = PixelKit.DecodePng(src);
            if (tex is null) return null;

            int sx, sy, sw, sh;
            if (kind == ThumbKind.Sprite)
            {
                // 精灵图预览：只取正面站立那一帧（原版布局 4 列，帧宽 = 宽/4，
                // 标准帧 16×32；矮个角色 16×24 —— 矮人/科罗布斯）。整张表全是
                // 各方向行走帧，全显示没有意义（用户反馈）
                // 宽 < 64 装不下 4 列 16px 帧 → 整图就是一帧（SVE 的 GilSprite 16×32 单帧）
                if (tex.Width >= 64)
                {
                    var fw = Math.Max(1, tex.Width / 4);
                    var fh = 2 * fw;
                    if (tex.Height % fh != 0 && tex.Height % (fw * 3 / 2) == 0)
                        fh = fw * 3 / 2;
                    (sx, sy, sw, sh) = (0, 0, fw, fh);
                    // v1.3.4：mod 自定动画表的帧布局未必是 4 列 —— 左上帧可能是空白
                    //（JosephineK/Gwen 弹窗右侧空白）。逐帧找第一个有可见像素的帧；
                    // 全空回退整图缩放。
                    if (!RegionHasPixels(tex, sx, sy, sw, sh))
                    {
                        var found = false;
                        for (var fy = 0; !found && fy + fh <= tex.Height; fy += fh)
                            for (var fx = 0; !found && fx + fw <= tex.Width; fx += fw)
                                if (RegionHasPixels(tex, fx, fy, fw, fh))
                                { (sx, sy) = (fx, fy); found = true; }
                        if (!found) (sx, sy, sw, sh) = (0, 0, tex.Width, tex.Height);
                    }
                }
                else
                {
                    (sx, sy, sw, sh) = (0, 0, tex.Width, tex.Height);
                }
            }
            else
            {
                // 立绘表是 2 列布局（游戏按 宽/2 的方形帧解读，首帧在左上）：
                // 原版 128 宽（64 帧）与 Portraiture/HD 高清表（256/512 宽，帧 128/256）
                // 一律裁左上首帧 —— 与游戏内实际显示完全一致。
                // v1.3.5：v1.3.4 对非 128 宽「整图缩放」是矫枉过正 —— Seven Deadly Sins
                // 全部立绘是 512×512~512×3072 的 2 列高清表，整图贴出来就是一屏碎脸图集；
                // 像素探针证实 Emily/SDS 各文件 (0,0,宽/2,宽/2) 全部有内容。
                // 首帧空白 → 逐帧找第一个有内容的帧；全空回退整图。
                var fw = tex.Width / 2;
                var isSheet = fw >= 64 && tex.Width % 128 == 0 && tex.Height % fw == 0;
                if (isSheet)
                {
                    (sx, sy, sw, sh) = (0, 0, fw, fw);
                    if (!RegionHasPixels(tex, sx, sy, sw, sh))
                    {
                        var found = false;
                        for (var fy = 0; !found && fy + fw <= tex.Height; fy += fw)
                            for (var fx = 0; !found && fx + fw <= tex.Width; fx += fw)
                                if (RegionHasPixels(tex, fx, fy, fw, fw))
                                { (sx, sy) = (fx, fy); found = true; }
                        if (!found) (sx, sy, sw, sh) = (0, 0, tex.Width, tex.Height);
                    }
                }
                else
                {
                    (sx, sy, sw, sh) = (0, 0, tex.Width, tex.Height);
                }
            }

            // 最近邻整数倍缩放（像素风：绝不平滑插值）。立绘/马缩到 ≤128；
            // 精灵帧很小，改为整数倍放大到 ~144 宽（16×32 → 144×288）
            int dw, dh;
            if (kind == ThumbKind.Sprite)
            {
                // 精灵帧很小 → 整数倍「放大」（⚠ dw/dh 是乘法；除法会把 16×32 缩成 1×3 黑块）
                var up = Math.Max(1, Math.Min(144 / Math.Max(1, sw), 512 / Math.Max(1, sh)));
                (dw, dh) = (sw * up, sh * up);
            }
            else
            {
                var down = Math.Max(1, Math.Max(
                    (int)Math.Ceiling(sw / (double)128),
                    (int)Math.Ceiling(sh / (double)128)));
                (dw, dh) = (Math.Max(1, sw / down), Math.Max(1, sh / down));
            }
            png = PixelKit.CropScalePng(tex, sx, sy, sw, sh, dw, dh);
            try { File.WriteAllBytes(cacheFile, png); } catch { }
        }
        return "data:image/png;base64," + Convert.ToBase64String(png);
    }

}
