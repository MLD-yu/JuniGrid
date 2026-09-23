using System.IO;

namespace JuniGrid.Services;

public sealed record DecodedTexture(byte[] PixelsRgba, int Width, int Height);

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
    private const int PrologueSize = 14;   // 3 magic + 1 platform + 2 version + 1 flags + 4 fileSize + 4 decompressedSize

    /// <summary>解出纹理；不支持的变体/损坏文件返回 null（调用方回退下一来源）。</summary>
    public static DecodedTexture? TryDecode(string xnbPath)
    {
        try
        {
            var file = File.ReadAllBytes(xnbPath);
            if (file.Length < PrologueSize) return null;
            if (file[0] != 'X' || file[1] != 'N' || file[2] != 'B') return null;
            // 头部布局（实测）：3 magic + 1 platform + 1 version + 1 flags + 4 fileSize + 4 decompressedSize
            // （版本是单字节 —— "XNBw 05 81 7c110000 b1800200"，0x81 = LZX|HiDef）
            var version = file[4];
            if (version is < 3 or > 5) return null;
            var flags = file[5];
            var fileSize = BitConverter.ToUInt32(file, 6);

            byte[] content;
            if ((flags & 0x80) != 0)
            {
                // LZX 压缩
                var decompressedSize = BitConverter.ToUInt32(file, 10);
                if (decompressedSize == 0 || decompressedSize > 64 * 1024 * 1024) return null;
                content = LzxDecompressFrames(file, PrologueSize, (int)fileSize, (int)decompressedSize);
            }
            else if ((flags & 0x40) != 0)
            {
                return null;   // LZ4（MonoGame 管线产物，星露谷官方 Content 不用）
            }
            else
            {
                content = file;
            }

            return ParseTexture(content);
        }
        catch (Exception ex)
        {
            AppLog.Warn("XnbDecoder", $"{Path.GetFileName(xnbPath)}: {ex.Message}");
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

    /// <summary>从解压后的内容区定位 Texture2D 数据。先严格解析，失败再小窗口扫描。</summary>
    private static DecodedTexture? ParseTexture(byte[] content)
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
                return ExtractMip(content, pos + 16, (int)w, (int)h);
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
            var tex = ExtractMip(content, p + 16, (int)w, (int)h);
            if (tex is not null) return tex;
        }
        return null;
    }

    /// <summary>mip 头后可能有 4 字节长度字段（=像素字节数则跳过），也可能直接是像素数据。</summary>
    private static DecodedTexture? ExtractMip(byte[] content, int mipPos, int w, int h)
    {
        var need = w * h * 4;
        if (mipPos + 4 <= content.Length
            && BitConverter.ToUInt32(content, mipPos) == (uint)need)
            mipPos += 4;
        if (mipPos + need > content.Length) return null;
        var px = new byte[need];
        Array.Copy(content, mipPos, px, 0, need);
        return new DecodedTexture(px, w, h);
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
