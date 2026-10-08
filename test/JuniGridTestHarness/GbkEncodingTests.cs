using System.IO.Compression;
using System.Text;

namespace JuniGridTestHarness;

/// <summary>
/// GBK（代码页 936）确实可用。两处依赖它：DepotDownloader 子进程的 stdout/stderr
/// （Steam 登录那边会吐中文），以及 mod 压缩包里的 GBK 文件名（ModService 用
/// ZipFile.ExtractToDirectory(zip, dir, Encoding.GetEncoding(936)) 解条目名）。
/// csproj 里那行 System.Text.Encoding.CodePages 被 NU1510 说成「可能不需要」，
/// 这两条用例就是那句判断的对照 —— 删包后还能过，才叫真的不需要。
/// </summary>
public class GbkEncodingTests
{
    // 「你好」的 GBK 字节：与 UTF-8 完全不同，提供程序没注册就解不出这两个字
    static readonly byte[] NiHaoGbk = { 0xC4, 0xE3, 0xBA, 0xC3 };

    [Fact]
    public void CodePages_Registers936_AndDecodesGbk()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var gbk = Encoding.GetEncoding(936);
        Assert.Equal("你好", gbk.GetString(NiHaoGbk));
        Assert.Equal(NiHaoGbk, gbk.GetBytes("你好"));
    }

    [Fact]
    public void Zip_GbkEntryName_ExtractsThrough936()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var gbk = Encoding.GetEncoding(936);
        var dir = Path.Combine(Path.GetTempPath(), "jg-gbk-zip-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var zip = Path.Combine(dir, "pack.zip");
            WriteGbkZip(zip, "说明.txt", "星露谷", gbk);

            var outDir = Path.Combine(dir, "out");
            ZipFile.ExtractToDirectory(zip, outDir, gbk);            // 与 ModService 同一句
            Assert.Equal("星露谷", File.ReadAllText(Path.Combine(outDir, "说明.txt"), gbk));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>手写一个最小合法 zip：条目名与正文都按给定编码存原文（不压缩），
    /// 名字不带 UTF-8 标志位 —— 国内 mod 包就是这个形状，.NET 默认按 OEM 码页猜，猜错就成乱码目录。</summary>
    static void WriteGbkZip(string path, string name, string text, Encoding gbk)
    {
        var nameBytes = gbk.GetBytes(name);
        var data = gbk.GetBytes(text);
        var crc = Crc32(data);

        using var fs = File.Create(path);
        var local = new List<byte>();
        local.AddRange(new byte[] { 0x50, 0x4B, 0x03, 0x04 });                    // 本地文件头
        local.AddRange(U2(20));                                                   // 解包所需版本
        local.AddRange(U2(0));                                                    // 标志位：不设 UTF-8 名
        local.AddRange(U2(0));                                                    // 0 = 存储
        local.AddRange(U2(0)); local.AddRange(U2(0));                             // 时间 / 日期
        local.AddRange(U4(crc));
        local.AddRange(U4((uint)data.Length));                                    // 压缩后大小
        local.AddRange(U4((uint)data.Length));                                    // 原始大小
        local.AddRange(U2((ushort)nameBytes.Length));
        local.AddRange(U2(0));                                                    // 扩展字段长
        local.AddRange(nameBytes);
        local.AddRange(data);
        fs.Write(local.ToArray());

        var cdOff = (uint)fs.Position;
        var cd = new List<byte>();
        cd.AddRange(new byte[] { 0x50, 0x4B, 0x01, 0x02 });                       // 中央目录项
        cd.AddRange(U2(20));                                                      // 制作方版本
        cd.AddRange(U2(20));                                                      // 解包所需版本
        cd.AddRange(U2(0)); cd.AddRange(U2(0));                                   // 标志 / 方法
        cd.AddRange(U2(0)); cd.AddRange(U2(0));                                   // 时间 / 日期
        cd.AddRange(U4(crc));
        cd.AddRange(U4((uint)data.Length));
        cd.AddRange(U4((uint)data.Length));
        cd.AddRange(U2((ushort)nameBytes.Length));
        cd.AddRange(U2(0)); cd.AddRange(U2(0));                                   // 扩展 / 注释长
        cd.AddRange(U2(0));                                                       // 起始盘号
        cd.AddRange(U2(0));                                                       // 内部属性
        cd.AddRange(U4(0));                                                       // 外部属性
        cd.AddRange(U4(0));                                                       // 本地头偏移
        cd.AddRange(nameBytes);
        fs.Write(cd.ToArray());

        var eocd = new List<byte>();
        eocd.AddRange(new byte[] { 0x50, 0x4B, 0x05, 0x06 });
        eocd.AddRange(U2(0)); eocd.AddRange(U2(0));                               // 本盘 / 起始盘
        eocd.AddRange(U2(1)); eocd.AddRange(U2(1));                               // 条目数
        eocd.AddRange(U4((uint)cd.Count));
        eocd.AddRange(U4(cdOff));
        eocd.AddRange(U2(0));                                                     // 注释长
        fs.Write(eocd.ToArray());
    }

    static byte[] U2(ushort v) => BitConverter.GetBytes(v);
    static byte[] U2(int v) => BitConverter.GetBytes((ushort)v);
    static byte[] U4(uint v) => BitConverter.GetBytes(v);

    static uint Crc32(byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (var b in data)
        {
            c ^= b;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1;
        }
        return c ^ 0xFFFFFFFF;
    }
}
