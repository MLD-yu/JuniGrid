using System.IO;

namespace JuniGrid.Services;

public sealed record DecodedTexture(byte[] PixelsRgba, int Width, int Height);

/// <summary>纹理在【去掉文件头的内容区】里的位置。编码侧靠它做"只换像素、其余一字不动"的重建，
/// 所以 Body 必须是原样可回写的那份 —— 压缩件给解压结果，未压缩件给 file[10..]（未压缩头只有 10 字节）。</summary>
public sealed record XnbTextureLayout(byte[] Body, int Width, int Height, int PixelPos, int PixelLen);

/// <summary>
/// XNB 纹理解码：LZX 分帧解压 → 定位 Texture2D 头 → 取出首 mip 的 RGBA 像素。
/// 只支持 SurfaceFormat.Color(=0) —— 星露谷 Content 下的 Portraits/Characters/Animals 全是 Color。
/// 踩坑记录（v1 实测，重建时已内建）：
///  1. 压缩流是分帧的：每帧前 u16 大端 block_size；首字节 0xFF 表示"自定义帧长"变长头
///     （0xFF + u16 大端 frame_size + u16 大端 block_size，共 5 字节），否则 2 字节头、帧长固定 0x8000。
///     整流一次性喂 LZX 会假成功/失败，必须逐帧喂同一个解码器实例。
///  2. 内容区各写入器字段略有差异：format/width/height 在小窗口内扫描定位
///     （format=0 且宽高合理），mip 头后可能有 4 字节数据长度字段（=像素字节数则跳过）。
/// </summary>
public static class XnbDecoder
{
    private const int PrologueSize = 14;   // 压缩件：3 magic + 1 platform + 2 version + 1 flags + 4 fileSize + 4 decompressedSize
    private const int UncompressedPrologueSize = 10;   // 未压缩件没有 decompressedSize 那 4 字节

    /// <summary>解出纹理；不支持的变体/损坏文件返回 null（调用方回退下一来源）。</summary>
    public static DecodedTexture? TryDecode(string xnbPath)
    {
        var lay = TryLocate(xnbPath);
        if (lay is null) return null;
        var px = new byte[lay.PixelLen];
        Array.Copy(lay.Body, lay.PixelPos, px, 0, lay.PixelLen);
        return new DecodedTexture(px, lay.Width, lay.Height);
    }

    /// <summary>
    /// 解字体图集（<c>Fonts/*.xnb</c> 里内联的那张）。<see cref="TryDecode"/> 走不通时才用它 ——
    /// 字体的图集是 DXT5，不是 Color，所以纹理定位必然失败（<see cref="DxtDecoder"/> 顶上）。
    /// <para>布局（在 4 个官方字体上实测一致）：类型表 → sharedCount(7bit)=0 → 根对象 reader 索引(7bit)=1
    /// → 【1 字节纹理引用】+ 5 个 int32（format / w / h / levels / dataSize）+ 4 字节尾巴
    /// → 像素块（起点 = 根对象 + 25）。</para>
    /// <para>格式按 <c>dataSize</c> 与 <c>w×h</c> 的比值判（1 字节/像素=字体遮罩块，0.5=DXT1，4=未压缩 Color），
    /// 不信枚举数字。解不出来返回 null，调用方继续摆占位。</para>
    /// </summary>
    public static DecodedTexture? TryDecodeFont(string xnbPath)
    {
        try
        {
            var content = ReadContentRegion(File.ReadAllBytes(xnbPath));
            if (content is null) return null;
            var pos = 0;
            var readerCount = Read7Bit(content, ref pos);
            for (var i = 0; i < readerCount && i < 64; i++)
            {
                var len = Read7Bit(content, ref pos);
                pos += len + 4;
            }
            _ = Read7Bit(content, ref pos);                       // sharedCount
            if (Read7Bit(content, ref pos) != 1) return null;     // 根对象必须是 reader[0]（SpriteFontReader）
            if (pos + 25 + 16 > content.Length) return null;
            var w = BitConverter.ToInt32(content, pos + 5);
            var h = BitConverter.ToInt32(content, pos + 9);
            var levels = BitConverter.ToInt32(content, pos + 13);
            var size = BitConverter.ToInt32(content, pos + 17);
            var px = pos + 25;
            if (w is <= 0 or > 8192 || h is <= 0 or > 8192 || levels != 1 || size <= 0) return null;
            if (px + size > content.Length) return null;
            if (size == w * h) return DxtDecoder.DecodeFontMask(content, px, w, h);
            if (size == w * h / 2) return DxtDecoder.DecodeDxt1(content, px, w, h);
            if (size == w * h * 4)
            {
                var raw = new byte[size];
                Array.Copy(content, px, raw, 0, size);
                return new DecodedTexture(raw, w, h);
            }
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Warn("XnbDecoder", $"{Path.GetFileName(xnbPath)} 字体图集: {ex.Message}");
            return null;
        }
    }

    /// <summary>定位纹理：返回可回写的内容区与像素块位置。编码侧与尺寸闸门都走这里。</summary>
    public static XnbTextureLayout? TryLocate(string xnbPath)
    {
        try
        {
            return TryLocate(File.ReadAllBytes(xnbPath));
        }
        catch (Exception ex)
        {
            AppLog.Warn("XnbDecoder", $"{Path.GetFileName(xnbPath)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>同上，字节版：体检一份还没落盘的 XNB 不必先写临时文件。</summary>
    public static XnbTextureLayout? TryLocate(byte[] file)
    {
        try
        {
            var content = ReadContentRegion(file);
            return content is null ? null : LocateTexture(content);
        }
        catch (Exception ex)
        {
            AppLog.Warn("XnbDecoder", ex.Message);
            return null;
        }
    }

    /// <summary>取出【不含文件头】的内容区（压缩件 = 解压结果）。头部不认识时返回 null。</summary>
    public static byte[]? ReadContentRegion(byte[] file)    {
        try
        {
            if (file.Length < PrologueSize) return null;
            if (file[0] != 'X' || file[1] != 'N' || file[2] != 'B') return null;
            // 头部布局（实测）：3 magic + 1 platform + 1 version + 1 flags + 4 fileSize，
            // 【仅压缩件】再跟 4 字节 decompressedSize —— 未压缩件的内容区从 offset 10 开始
            //（版本是单字节 —— "XNBw 05 81 7c110000 b1800200"，0x81 = LZX|HiDef）
            var version = file[4];
            if (version is < 3 or > 5) return null;
            var flags = file[5];
            var fileSize = BitConverter.ToUInt32(file, 6);

            if ((flags & 0x80) != 0)
            {
                // LZX 压缩
                var decompressedSize = BitConverter.ToUInt32(file, 10);
                if (decompressedSize == 0 || decompressedSize > 64 * 1024 * 1024) return null;
                return LzxDecompressFrames(file, PrologueSize, (int)fileSize, (int)decompressedSize);
            }
            if ((flags & 0x40) != 0) return null;   // LZ4（MonoGame 管线产物，星露谷官方 Content 不用）

            return file.Length > UncompressedPrologueSize ? file[UncompressedPrologueSize..] : null;
        }
        catch (Exception ex)
        {
            AppLog.Warn("XnbDecoder", ex.Message);
            return null;
        }
    }

    private static byte[] LzxDecompressFrames(byte[] file, int start, int totalSize, int decompressedSize)
    {
        var output = new byte[decompressedSize];
        using var outMs = new MemoryStream(output, writable: true);
        // 所有帧共用一个解码器：树/窗口/R0R1R2 跨帧延续
        var lzx = new LzxDecoder(16);

        var pos = start;
        var written = 0;
        while (pos < totalSize && written < decompressedSize)
        {
            int blockSize, frameSize;
            if (file[pos] == 0xFF)
            {
                if (pos + 5 > totalSize) throw new InvalidDataException("LZX 帧头截断");
                frameSize = (file[pos + 1] << 8) | file[pos + 2];
                blockSize = (file[pos + 3] << 8) | file[pos + 4];
                pos += 5;
            }
            else
            {
                if (pos + 2 > totalSize) break;
                blockSize = (file[pos] << 8) | file[pos + 1];
                frameSize = 0x8000;
                pos += 2;
            }
            if (blockSize == 0 || frameSize == 0) break;
            if (blockSize > 0x10000 || frameSize > 0x10000)
                throw new InvalidDataException($"LZX 帧长异常 block={blockSize} frame={frameSize}");

            var want = Math.Min(frameSize, decompressedSize - written);
            using var inMs = new MemoryStream(file, pos, blockSize, writable: false);
            lzx.Decompress(inMs, blockSize, outMs, want);
            pos += blockSize;
            written += want;
        }
        return output;
    }

    /// <summary>从内容区定位 Texture2D 数据。先严格解析，失败再小窗口扫描。</summary>
    private static XnbTextureLayout? LocateTexture(byte[] content)
    {
        var pos = 0;
        try
        {
            var readerCount = Read7Bit(content, ref pos);
            for (var i = 0; i < readerCount && i < 64; i++)
            {
                var len = Read7Bit(content, ref pos);
                pos += len + 4;   // 类型名 + 版本 int32
            }
            _ = Read7Bit(content, ref pos);   // shared count（只消费字节，值不参与后续逻辑）
            // XNB v5 在 shared count 之后还有根对象的 reader 索引（7-bit，值=1）——
            // 漏掉它整个纹理头会错位 1 字节（v1 实测踩坑）
            _ = Read7Bit(content, ref pos);

            var fmt = BitConverter.ToUInt32(content, pos);
            var w = BitConverter.ToUInt32(content, pos + 4);
            var h = BitConverter.ToUInt32(content, pos + 8);
            var mips = BitConverter.ToUInt32(content, pos + 12);
            if (fmt == 0 && Plausible(w, h, mips))
                return ExtractLayout(content, pos + 16, (int)w, (int)h);
        }
        catch (IndexOutOfRangeException) { }
        catch (ArgumentOutOfRangeException) { }

        // 扫描兜底：在内容区头部小窗口内找 "u32==0(Color) 且宽高合理" 的纹理头。
        // 必须 1 字节步进 —— 纹理头不保证 4 字节对齐（root index 字节会错位）
        var scanEnd = Math.Min(content.Length - 32, 192);
        for (var p = 0; p <= scanEnd; p++)
        {
            if (BitConverter.ToUInt32(content, p) != 0) continue;
            var w = BitConverter.ToUInt32(content, p + 4);
            var h = BitConverter.ToUInt32(content, p + 8);
            var mips = BitConverter.ToUInt32(content, p + 12);
            if (!Plausible(w, h, mips)) continue;
            var tex = ExtractLayout(content, p + 16, (int)w, (int)h);
            if (tex is not null) return tex;
        }
        return null;
    }

    /// <summary>mip 头后可能有 4 字节长度字段（=像素字节数则跳过），也可能直接是像素数据。</summary>
    private static XnbTextureLayout? ExtractLayout(byte[] content, int mipPos, int w, int h)
    {
        var need = w * h * 4;
        if (mipPos + 4 <= content.Length
            && BitConverter.ToUInt32(content, mipPos) == (uint)need)
            mipPos += 4;
        if (mipPos + need > content.Length) return null;
        return new XnbTextureLayout(content, w, h, mipPos, need);
    }

    private static bool Plausible(uint w, uint h, uint mips) =>
        w is > 0 and <= 8192 && h is > 0 and <= 8192 && mips is >= 1 and <= 16;

    /// <summary>XNA 7 位压缩整数（每字节低 7 位，最高位=继续标志）。</summary>
    private static int Read7Bit(byte[] data, ref int pos)
    {
        int result = 0, shift = 0;
        while (true)
        {
            if (pos >= data.Length) throw new IndexOutOfRangeException();
            var b = data[pos++];
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift > 28) throw new InvalidDataException("7BitInt 过长");
        }
    }
}
