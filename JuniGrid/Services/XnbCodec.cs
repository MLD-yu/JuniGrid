using System.IO;

namespace JuniGrid.Services;

/// <summary>
/// 未压缩 XNB 的头与体检：往本体 Content 里换的那份字节【就是作者交的那张 .xnb】，
/// 我们只套头（10 字节：XNB + 'w' + 版本 5 + flags 0x01 + int32 总长）和验货，不重编像素。
///
/// 为什么可以不实现 LZX：官方 Content 里本来就有未压缩件（实测 Content/Characters/Grandpa.xnb
/// 与 Farmer/skinColors.xnb，flags=0x01、10 字节头），游戏照常加载。
///
/// 2026-10-10 起这里不再有"把 PNG 编成 XNB"的那两个入口 —— 内容包只收 .xnb：
/// 实测 Villagers Anime Portrait（N97）的 <c>Character Previews/</c> 是 45 张效果图（一张 xnb 都没有），
/// 而 <c>xnb/</c> 里 46 张才是给本体的；旧写法按文件夹名排序取第一版，于是把效果图重编进了本体。
/// </summary>
public static class XnbCodec
{
    /// <summary>未压缩 XNB 的头长（magic3 + platform1 + version1 + flags1 + fileSize4）。</summary>
    public const int RawHeaderSize = 10;

    /// <summary>给内容区（不含任何头）套上 10 字节未压缩头。</summary>
    public static byte[] WrapRaw(byte[] body)
    {
        var outBytes = new byte[RawHeaderSize + body.Length];
        outBytes[0] = (byte)'X'; outBytes[1] = (byte)'N'; outBytes[2] = (byte)'B';
        outBytes[3] = (byte)'w';          // 平台码：实测官方件是 'w'(0x77)，不是 flags 的一部分
        outBytes[4] = 5;                  // XNB 版本
        outBytes[5] = 0x01;               // flags：HiDef 档、未压缩（压缩位 0x80/0x40 都不置）
        BitConverter.TryWriteBytes(outBytes.AsSpan(6), outBytes.Length);
        body.CopyTo(outBytes.AsSpan(RawHeaderSize));
        return outBytes;
    }

    /// <summary>
    /// 写进 Content 之前的体检。一个坏 XNB 的表现是游戏开不起来，而且人会以为是自己的存档坏了，
    /// 所以宁可在这儿拦住。检查项全部来自实测的头部布局，不做任何猜测性放宽。
    /// 返回 null = 可以写。
    /// </summary>
    public static string? Validate(byte[] bytes)
    {
        if (bytes.Length < RawHeaderSize + 2) return LocService.Tr("文件太小，不是 XNB");
        if (bytes[0] != (byte)'X' || bytes[1] != (byte)'N' || bytes[2] != (byte)'B')
            return LocService.Tr("缺少 XNB 文件头");
        var version = bytes[4];
        if (version is < 3 or > 5) return LocService.Tf("XNB 版本 {0} 不认识", version);
        var flags = bytes[5];
        if ((flags & 0x40) != 0) return LocService.Tr("这个 XNB 用的是 LZ4 压缩，星露谷本体不用这种");
        var declared = BitConverter.ToInt32(bytes, 6);
        if (declared != bytes.Length)
            return LocService.Tf("文件头写的长度是 {0} 字节，实际 {1} 字节 —— 文件被截断或改坏过", declared, bytes.Length);
        var body = XnbDecoder.ReadContentRegion(bytes);
        if (body is null) return LocService.Tr("解不开这个 XNB 的内容");
        // 字体（SpriteFont）必须在这里放行，且要排在纹理判定【之前】。
        // 实测字体的类型表里【同时】列着 SpriteFontReader 与 Texture2DReader ——
        // 字体内部嵌了一张字形贴图。而 XnbDecoder.TryLocate 找的是【根对象】的纹理头，
        // 根对象是 SpriteFont 不是 Texture2D，于是定位必然失败 ⇒ 旧代码把每个字体
        // 都报成"解不开这个 XNB 的图像数据"，字体包一个都装不上。
        // 字体是自描述容器（字号/行距/字形贴图全记在里面），整份替换后游戏按新表渲染，
        // 我们不需要读懂它，所以不该用纹理的闸门去卡它。
        if (IndexOfAscii(body, SpriteFontReaderMark) >= 0) return null;
        // 只有纹理才谈得上"验货"：Data/Maps 那类资产我们没有对应的解析器，
        // 拦它 = 假装自己认得，所以原样放过，让「不确定」留在文案里而不是变成误报。
        if (IndexOfAscii(body, TextureReaderMark) >= 0 && XnbDecoder.TryLocate(bytes) is null)
            return LocService.Tr("解不开这个 XNB 的图像数据");
        return null;
    }

    /// <summary>类型表里 SpriteFont 的标记。字体内部也带 Texture2DReader，所以这条要优先判。</summary>
    private const string SpriteFontReaderMark = "SpriteFontReader";

    /// <summary>类型表里 Texture2D 的标记（内容区开头的纯 ASCII，不需要完整解析类型表）。</summary>
    private const string TextureReaderMark = "Texture2DReader";

    /// <summary>目标资产是不是我们能整张替换的纹理。</summary>
    public static bool IsTextureAsset(string xnbPath)
    {
        var body = ProbeContentRegion(xnbPath);
        return body is not null && IndexOfAscii(body, TextureReaderMark) >= 0;
    }

    /// <summary>
    /// 目标资产是不是字体（SpriteFont）—— 也就是"解不出纹理、但仍然能整份替换"的那一类。
    ///
    /// 为什么要单独问：<see cref="TryTextureArea"/> 返回 null 有两种完全不同的原因 ——
    ///   · 字体（Fonts/SmallFont）：类型表里有 SpriteFontReader，能整份替换，该收；
    ///   · 排程表与对话文本（Characters/schedules/Abigail 等 DictionaryReader&lt;String,String&gt;）：
    ///     我们没有这类资产的编码器，整份塞进去会把角色的日程和对话写坏，绝不能收。
    /// 早先只按"area==0"一刀切，两类都被当成候选，于是每个角色凭空多出 3 个文本同名项，
    /// 集体被判成"分不清该换哪一张"——26 个角色全装不上，而包里的图尺寸其实完全对得上。
    ///
    /// 要解内容区才能判，所以只在【候选】阶段对 2–5 个同名资产调用，不要放进建索引的循环里。
    /// </summary>
    public static bool IsFontAsset(string xnbPath)
    {
        try
        {
            var body = ProbeContentRegion(xnbPath);
            return body is not null && IndexOfAscii(body, SpriteFontReaderMark) >= 0;
        }
        catch (Exception ex)
        {
            AppLog.Warn("XnbCodec", ex.Message);
            return false;
        }
    }

    /// <summary>类型判定要扫的字节数 —— <see cref="IndexOfAscii"/> 本来就只看内容区前 4096 字节，
    /// 所以未压缩件读这么多就够，不必整读。</summary>
    private const int TypeMarkScanBytes = 4096;

    /// <summary>
    /// 只为"类型表里有没有那个 Reader 名"取内容区。
    ///
    /// <para>为什么不直接用 <see cref="XnbDecoder.ReadContentRegion"/>：那条路要先把整个文件读进内存。
    /// 素材页现在每次刷新都要问上千行（一个 1554 张表的 furry 包就是 200MB 贴图），
    /// 而未压缩件的内容区就是文件头之后的原字节 —— 类型表在开头，读 10 + 4096 字节
    /// 和读整份的答案一模一样。</para>
    ///
    /// <para>LZX 压缩件（flags &amp; 0x80）抄不了这个近路：类型表在压缩块里，
    /// 必须整读解压，所以那一条仍走 <see cref="SafeRead"/>。</para>
    /// </summary>
    private static byte[]? ProbeContentRegion(string xnbPath)
    {
        try
        {
            var buf = new byte[RawHeaderSize + TypeMarkScanBytes];
            int got;
            using (var fs = new FileStream(xnbPath, FileMode.Open, FileAccess.Read,
                       FileShare.Read | FileShare.Delete))
                got = fs.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
            if (got < RawHeaderSize) return null;
            if (buf[0] != (byte)'X' || buf[1] != (byte)'N' || buf[2] != (byte)'B') return null;
            if (buf[4] is < 3 or > 5) return null;                  // 与 ReadContentRegion 同一道版本闸
            if ((buf[5] & 0x40) != 0) return null;                  // LZ4：MonoGame 管线产物，读不出就是不是
            if ((buf[5] & 0x80) != 0)                               // LZX：类型表在压缩块里
                return XnbDecoder.ReadContentRegion(SafeRead(xnbPath));
            return buf.AsSpan(RawHeaderSize, got - RawHeaderSize).ToArray();
        }
        catch (Exception ex)
        {
            AppLog.Warn("XnbCodec", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 只读 14 字节头就算出纹理的像素个数，【不解压】—— 内容区固定是
    /// 161 字节前缀（含 format）+ 宽/高/mip 数/数据长 四个 int32 = 177 字节，剩下就是 RGBA。
    /// 给 3550 个资产建尺寸索引时，解压一遍要 4 秒以上（立绘页为躲这个开销专门加过跳过闸门），
    /// 而筛候选只需要"面积对不对"。真宽高仍以 XnbDecoder.TryLocate 为准。
    /// 不是单 mip Color 纹理的形状时返回 null（数据类资产就是这样，正好不该被当候选）。
    /// </summary>
    public static int? TryTextureArea(string xnbPath)
    {
        try
        {
            var head = new byte[14];
            using var fs = new FileStream(xnbPath, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            if (fs.ReadAtLeast(head, 14, throwOnEndOfStream: false) < 14) return null;
            if (head[0] != (byte)'X' || head[1] != (byte)'N' || head[2] != (byte)'B') return null;
            if ((head[5] & 0x40) != 0) return null;              // LZ4
            var bodyLen = (head[5] & 0x80) != 0
                ? BitConverter.ToInt32(head, 10)
                : (int)(new FileInfo(xnbPath).Length - RawHeaderSize);
            // 内容区里像素块之前的固定部分 = 161 前缀 + 宽/高/mip 数/数据长 四个 int32 = 177
            //（实测：skinColors 465-177=288=3×24×4，swimShadow 10417-177=10240=160×16×4）
            var pixelBytes = bodyLen - 177;
            if (pixelBytes <= 0 || pixelBytes % 4 != 0) return null;
            return pixelBytes / 4;
        }
        catch (Exception ex)
        {
            AppLog.Warn("XnbCodec", ex.Message);
            return null;
        }
    }

    private static byte[] SafeRead(string path)
    {
        try { return File.ReadAllBytes(path); }
        catch (Exception ex) { AppLog.Warn("XnbCodec", ex.Message); return Array.Empty<byte>(); }
    }

    private static int IndexOfAscii(byte[] hay, string needle)
    {
        var n = System.Text.Encoding.ASCII.GetBytes(needle);
        var limit = Math.Min(hay.Length, 4096);
        for (var i = 0; i <= limit - n.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < n.Length; j++) if (hay[i + j] != n[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }
}
