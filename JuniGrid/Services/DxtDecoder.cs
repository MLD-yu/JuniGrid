namespace JuniGrid.Services;

/// <summary>
/// DXT1 / DXT5（BC1 / BC3）块解码成 RGBA —— 只为字体图集服务。
/// <para>为什么需要它：官方 <c>Fonts/SmallFont.xnb</c> 里的图集不是未压缩的 Color 纹理，
/// 而是 DXT5（实测 256×260 的图集声明 dataSize=66560=w×h，正好 1 字节/像素；
/// tinyFont 512×196、SpriteFont1 512×272、SmallFont.zh-CN 2048×1404 四个文件全对）。
/// <see cref="XnbDecoder"/> 只认 Color，所以字体一直"解不出纹理"，素材页的字体卡只能摆占位图。</para>
/// <para>判格式看 <b>dataSize 与 w×h 的比值</b>，不看枚举值：星露谷 1.6 是 MonoGame 管线，
/// SurfaceFormat 的数字与 XNA 那套对不上号（实测这里 format=5），
/// 而"每像素几字节"是文件自己说清楚的。</para>
/// </summary>
public static class DxtDecoder
{
    /// <summary>
    /// 字体图集专用：每 16 字节一块，取【前 8 字节的 BC4 单通道块】当墨色遮罩。
    /// <para>为什么不是整块 DXT5：实测把后 8 字节按 DXT1 颜色块解出来是彩色雪花，
    /// 而按 BC4 解前 8 字节就是干净的字形表（tinyFont 512×196 肉眼确认）——
    /// 也就是说后 8 字节不是颜色块（字体图集没有颜色通道可用），整块按 DXT5 解必然花。</para>
    /// <para>输出【不透明】的"纸上黑墨"：底 <c>#F6F3ED</c>，墨 <c>#1A1A20</c>，按遮罩插值。
    /// 不铺底不行 —— 游戏里字体是被文字颜色染的透明图，直接给"黑字+透明底"在深色主题的
    /// 大图弹层上会整张看不见，给"白字+透明底"在浅色卡片上看不见；烘成不透明后
    /// 卡片、行缩略图、大图三处看到的是同一张图，不随主题变。</para>
    /// </summary>
    public static DecodedTexture DecodeFontMask(byte[] src, int pos, int w, int h)
    {
        const int bgR = 0xF6, bgG = 0xF3, bgB = 0xED;
        const int inkR = 0x1A, inkG = 0x1A, inkB = 0x20;
        var rgba = new byte[w * h * 4];
        var blocksX = (w + 3) / 4;
        var blocksY = (h + 3) / 4;
        var p = pos;
        Span<int> t = stackalloc int[8];
        for (var by = 0; by < blocksY; by++)
        {
            for (var bx = 0; bx < blocksX; bx++, p += 16)
            {
                if (p + 8 > src.Length) break;
                BuildAlphaTable(src[p], src[p + 1], t);
                ulong bits = 0;
                for (var i = 0; i < 6; i++) bits |= (ulong)src[p + 2 + i] << (8 * i);
                for (var y = 0; y < 4; y++)
                {
                    var py = by * 4 + y;
                    if (py >= h) break;
                    for (var x = 0; x < 4; x++)
                    {
                        var px = bx * 4 + x;
                        if (px >= w) break;
                        var a = t[(int)((bits >> (3 * (y * 4 + x))) & 7)];
                        var o = (py * w + px) * 4;
                        rgba[o] = (byte)((bgR * (255 - a) + inkR * a) / 255);
                        rgba[o + 1] = (byte)((bgG * (255 - a) + inkG * a) / 255);
                        rgba[o + 2] = (byte)((bgB * (255 - a) + inkB * a) / 255);
                        rgba[o + 3] = 255;
                    }
                }
            }
        }
        return new DecodedTexture(rgba, w, h);
    }

    /// <summary>DXT1（BC1）：8 字节一块，颜色索引 2 bit，4 色模式下索引 3 为透明。</summary>
    public static DecodedTexture DecodeDxt1(byte[] src, int pos, int w, int h)
    {
        var rgba = new byte[w * h * 4];
        var blocksX = (w + 3) / 4;
        var blocksY = (h + 3) / 4;
        var p = pos;
        Span<int> palR = stackalloc int[4]; Span<int> palG = stackalloc int[4];
        Span<int> palB = stackalloc int[4]; Span<byte> palA = stackalloc byte[4];
        for (var by = 0; by < blocksY; by++)
        {
            for (var bx = 0; bx < blocksX; bx++, p += 8)
            {
                if (p + 8 > src.Length) break;
                ReadColorBlock(src, p, palR, palG, palB, palA);
                var cbits = (uint)(src[p + 4] | (src[p + 5] << 8) | (src[p + 6] << 16) | (src[p + 7] << 24));
                for (var y = 0; y < 4; y++)
                {
                    var py = by * 4 + y;
                    if (py >= h) break;
                    for (var x = 0; x < 4; x++)
                    {
                        var px = bx * 4 + x;
                        if (px >= w) break;
                        var ci = (int)((cbits >> (2 * (y * 4 + x))) & 3);
                        var o = (py * w + px) * 4;
                        rgba[o] = (byte)palR[ci];
                        rgba[o + 1] = (byte)palG[ci];
                        rgba[o + 2] = (byte)palB[ci];
                        rgba[o + 3] = palA[ci];
                    }
                }
            }
        }
        return new DecodedTexture(rgba, w, h);
    }

    /// <summary>读一个 8 字节 DXT1 颜色块 → 4 色调色板（含每色的 alpha）。</summary>
    private static void ReadColorBlock(byte[] src, int p,
        Span<int> palR, Span<int> palG, Span<int> palB, Span<byte> palA)
    {
        var c0 = (ushort)(src[p] | (src[p + 1] << 8));
        var c1 = (ushort)(src[p + 2] | (src[p + 3] << 8));
        // 5/6bit → 8bit：高位复制到低位，比 v*255/31 更贴近硬件取整
        static int R5(ushort c) { var v = (c >> 11) & 0x1F; return (v << 3) | (v >> 2); }
        static int G6(ushort c) { var v = (c >> 5) & 0x3F; return (v << 2) | (v >> 4); }
        static int B5(ushort c) { var v = c & 0x1F; return (v << 3) | (v >> 2); }
        var (r0, g0, b0) = (R5(c0), G6(c0), B5(c0));
        var (r1, g1, b1) = (R5(c1), G6(c1), B5(c1));
        if (c0 > c1)
        {
            palR[0] = r0; palG[0] = g0; palB[0] = b0;
            palR[1] = r1; palG[1] = g1; palB[1] = b1;
            palR[2] = (2 * r0 + r1) / 3; palG[2] = (2 * g0 + g1) / 3; palB[2] = (2 * b0 + b1) / 3;
            palR[3] = (r0 + 2 * r1) / 3; palG[3] = (g0 + 2 * g1) / 3; palB[3] = (b0 + 2 * b1) / 3;
            palA[0] = palA[1] = palA[2] = palA[3] = 255;
        }
        else
        {
            palR[0] = r0; palG[0] = g0; palB[0] = b0;
            palR[1] = r1; palG[1] = g1; palB[1] = b1;
            palR[2] = (r0 + r1) / 2; palG[2] = (g0 + g1) / 2; palB[2] = (b0 + b1) / 2;
            palR[3] = 0; palG[3] = 0; palB[3] = 0;
            palA[0] = palA[1] = palA[2] = 255; palA[3] = 0;
        }
    }

    private static void BuildAlphaTable(int a0, int a1, Span<int> t)
    {
        t[0] = a0;
        t[1] = a1;
        if (a0 > a1)
        {
            t[2] = (5 * a0 + a1) / 6; t[3] = (4 * a0 + 2 * a1) / 6;
            t[4] = (3 * a0 + 3 * a1) / 6; t[5] = (2 * a0 + 4 * a1) / 6;
            t[6] = (a0 + 5 * a1) / 6; t[7] = 0;
        }
        else
        {
            t[2] = (6 * a0 + a1) / 7; t[3] = (5 * a0 + 2 * a1) / 7;
            t[4] = (4 * a0 + 3 * a1) / 7; t[5] = (3 * a0 + 4 * a1) / 7;
            t[6] = (2 * a0 + 5 * a1) / 7; t[7] = (a0 + 6 * a1) / 7;
        }
    }
}
