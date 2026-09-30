using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace JuniGrid.Services;

/// <summary>PNG 解码 / 裁剪缩放 / PNG 编码（WPF Imaging，编码解码纯后台可用）。缩放一律最近邻保像素风。</summary>
public static class PixelKit
{
    public static DecodedTexture? DecodePng(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            var bmp = System.Windows.Media.Imaging.BitmapDecoder.Create(fs,
                System.Windows.Media.Imaging.BitmapCreateOptions.None,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
            var w = bmp.PixelWidth;
            var h = bmp.PixelHeight;
            if (w is <= 0 or > 8192 || h is <= 0 or > 8192) return null;
            var conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                bmp, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            var bgra = new byte[w * h * 4];
            conv.CopyPixels(bgra, w * 4, 0);
            // BGRA → RGBA（与 XNB 解码同口径）
            for (var i = 0; i < bgra.Length; i += 4)
                (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
            return new DecodedTexture(bgra, w, h);
        }
        catch { return null; }
    }

    /// <summary>裁剪 + 最近邻缩放，输出 PNG 字节。</summary>
    public static byte[] CropScalePng(DecodedTexture tex, int sx, int sy, int sw, int sh, int dw, int dh)
    {
        var outPx = new byte[dw * dh * 4];
        for (var y = 0; y < dh; y++)
        {
            var srow = Math.Min(tex.Height - 1, sy + (int)((double)y * sh / dh));
            for (var x = 0; x < dw; x++)
            {
                var scol = Math.Min(tex.Width - 1, sx + (int)((double)x * sw / dw));
                var si = (srow * tex.Width + scol) * 4;
                var di = (y * dw + x) * 4;
                outPx[di] = tex.PixelsRgba[si];
                outPx[di + 1] = tex.PixelsRgba[si + 1];
                outPx[di + 2] = tex.PixelsRgba[si + 2];
                outPx[di + 3] = tex.PixelsRgba[si + 3];
            }
        }
        // RGBA → BGRA 交给 WPF
        for (var i = 0; i < outPx.Length; i += 4)
            (outPx[i], outPx[i + 2]) = (outPx[i + 2], outPx[i]);
        return EncodePng(outPx, dw, dh);
    }

    /// <summary>把整张贴到 dw×dh 画布的左上角并按【64 格】平铺填满，输出 PNG 字节。
    /// 用于场合资产：替身比本尊小时，CP 的 EditImage 会因"目标区域超出图像右边界"整条作废 ⇒
    /// 那条场合没人钉 ⇒ 游戏在那个场合读别家的图。⚠ 不能留透明：立绘每格是一张表情，
    /// 长对话会走到后面的格 ⇒ 透明 = 说话框里没脸。按格平铺保证每一格都是我们这张画的脸，
    /// 且不会跨格撕开画面。</summary>
    public static byte[] PadPng(DecodedTexture tex, int dw, int dh)
    {
        const int cell = 64;
        var sc = Math.Max(1, tex.Width / cell);
        var sr = Math.Max(1, tex.Height / cell);
        var outPx = new byte[dw * dh * 4];      // BGRA
        for (var y = 0; y < dh; y++)
        {
            var srow = Math.Min(tex.Height - 1, (y / cell) % sr * cell + y % cell);
            for (var x = 0; x < dw; x++)
            {
                var scol = Math.Min(tex.Width - 1, (x / cell) % sc * cell + x % cell);
                var si = (srow * tex.Width + scol) * 4;
                var di = (y * dw + x) * 4;
                outPx[di] = tex.PixelsRgba[si + 2];
                outPx[di + 1] = tex.PixelsRgba[si + 1];
                outPx[di + 2] = tex.PixelsRgba[si];
                outPx[di + 3] = tex.PixelsRgba[si + 3];
            }
        }
        return EncodePng(outPx, dw, dh);
    }

    private static byte[] EncodePng(byte[] bgra, int w, int h)
    {
        var bmp = System.Windows.Media.Imaging.BitmapSource.Create(w, h, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, bgra, w * 4);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}
