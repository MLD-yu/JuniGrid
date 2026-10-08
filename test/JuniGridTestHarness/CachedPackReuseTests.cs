using System.IO.Compression;
using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>「重试不该重新下载本机已有的包」这一步的判据：<see cref="ModService.ArchiveReadable"/>
/// 只认列得出条目的压缩包。半截下载与被网络拦成网页的文件必须判 false —— 判错的代价是
/// 拿一个坏文件去装（然后报"压缩包损坏"），而它本该去重新下载。</summary>
public class CachedPackReuseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jg-cache-" + Guid.NewGuid().ToString("N"));

    public CachedPackReuseTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string Zip(string name)
    {
        var p = Path.Combine(_root, name);
        using var z = ZipFile.Open(p, ZipArchiveMode.Create);
        var e = z.CreateEntry("manifest.json");
        using var w = new StreamWriter(e.Open());
        w.Write("{}");
        return p;
    }

    [Fact]
    public void CompleteZip_IsReusable() => Assert.True(ModService.ArchiveReadable(Zip("ok.zip")));

    [Fact]
    public void TruncatedZip_IsNot()
    {
        var p = Zip("cut.zip");
        var bytes = File.ReadAllBytes(p);
        File.WriteAllBytes(p, bytes[..(bytes.Length - 40)]);   // 抹掉中央目录
        Assert.False(ModService.ArchiveReadable(p));
    }

    [Fact]
    public void MissingAndEmptyAndWebPage_AreNot()
    {
        Assert.False(ModService.ArchiveReadable(Path.Combine(_root, "nope.zip")));
        var empty = Path.Combine(_root, "empty.zip");
        File.WriteAllBytes(empty, Array.Empty<byte>());
        Assert.False(ModService.ArchiveReadable(empty));
        var html = Path.Combine(_root, "blocked.zip");
        File.WriteAllText(html, "<html><body>Just a moment…</body></html>");
        Assert.False(ModService.ArchiveReadable(html));
    }
}
