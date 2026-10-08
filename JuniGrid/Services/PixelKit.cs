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
