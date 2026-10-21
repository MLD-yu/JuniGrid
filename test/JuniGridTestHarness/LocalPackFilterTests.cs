using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>
/// 本地 mod 压缩包入口判据 —— 白盒（全是 internal static）。
/// <para>拆成 <see cref="InstallService.HasPackExtension"/>（纯字符串）与
/// <see cref="InstallService.IsLocalPackPath"/>（查磁盘）两层，是为了让绝大部分
/// 判据不用造临时文件就能测。</para>
/// </summary>
public class LocalPackFilterTests
{
    // ── HasPackExtension：纯函数 ──────────────────────────────

    [Theory]
    [InlineData("mod.zip")]
    [InlineData("mod.ZIP")]
    [InlineData("mod.Zip")]
    [InlineData(@"C:\Users\me\Downloads\Stardew Mod 1.2.zip")]
    [InlineData("pack.7z")]
    [InlineData("pack.RAR")]
    public void HasPackExtension_AcceptsKnownArchives(string path)
        => Assert.True(InstallService.HasPackExtension(path));

    [Theory]
    [InlineData("mod.txt")]
    [InlineData("mod")]
    [InlineData("mod.")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void HasPackExtension_RejectsEverythingElse(string? path)
        => Assert.False(InstallService.HasPackExtension(path));

    /// <summary>钉住"用 Path.GetExtension 而不是 EndsWith"：一个叫 .zip.txt 的文件
    /// 不是压缩包，用 EndsWith(".zip") 会误判成能装。</summary>
    [Theory]
    [InlineData("foo.zip.txt")]
    [InlineData("foo.7z.bak")]
    [InlineData("foo.rar.log")]
    public void HasPackExtension_IsNotFooledByCompoundExtensions(string path)
        => Assert.False(InstallService.HasPackExtension(path));

    // ── IsLocalPackPath：查磁盘 ──────────────────────────────

    [Fact]
    public void IsLocalPackPath_RejectsMissingFile()
    {
        var missing = Path.Combine(Path.GetTempPath(), "jg-no-such-pack-" + Guid.NewGuid().ToString("N") + ".zip");
        Assert.False(InstallService.IsLocalPackPath(missing));
    }

    [Fact]
    public void IsLocalPackPath_RejectsDirectoryEvenWithZipName()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jg-packdir-" + Guid.NewGuid().ToString("N") + ".zip");
        Directory.CreateDirectory(dir);
        try { Assert.False(InstallService.IsLocalPackPath(dir)); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void IsLocalPackPath_AcceptsRealFile()
    {
        var f = Path.Combine(Path.GetTempPath(), "jg-pack-" + Guid.NewGuid().ToString("N") + ".zip");
        File.WriteAllBytes(f, new byte[] { 0x50, 0x4B, 0x03, 0x04 });   // PK\x03\x04，无所谓内容
        try { Assert.True(InstallService.IsLocalPackPath(f)); }
        finally { try { File.Delete(f); } catch { } }
    }

    [Fact]
    public void IsLocalPackPath_RejectsExistingNonArchive()
    {
        var f = Path.Combine(Path.GetTempPath(), "jg-pack-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(f, "not a pack");
        try { Assert.False(InstallService.IsLocalPackPath(f)); }
        finally { try { File.Delete(f); } catch { } }
    }

    // ── FilterLocalPacks：过滤 + 去重 ────────────────────────

    [Fact]
    public void FilterLocalPacks_KeepsOnlyValidArchives()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jg-filter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var zip = Path.Combine(dir, "a.zip");
            var txt = Path.Combine(dir, "b.txt");
            var missing = Path.Combine(dir, "c.7z");
            File.WriteAllBytes(zip, new byte[] { 0x50, 0x4B, 0x03, 0x04 });
            File.WriteAllText(txt, "nope");

            var kept = InstallService.FilterLocalPacks(new[] { zip, txt, missing });
            Assert.Single(kept);
            Assert.Equal(zip, kept[0]);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void FilterLocalPacks_DeduplicatesSamePath()
    {
        var f = Path.Combine(Path.GetTempPath(), "jg-dup-" + Guid.NewGuid().ToString("N") + ".zip");
        File.WriteAllBytes(f, new byte[] { 0x50, 0x4B, 0x03, 0x04 });
        try
        {
            var kept = InstallService.FilterLocalPacks(new[] { f, f });
            Assert.Single(kept);
        }
        finally { try { File.Delete(f); } catch { } }
    }

    [Fact]
    public void FilterLocalPacks_DeduplicatesCaseInsensitively()
    {
        var f = Path.Combine(Path.GetTempPath(), "jg-case-" + Guid.NewGuid().ToString("N") + ".zip");
        File.WriteAllBytes(f, new byte[] { 0x50, 0x4B, 0x03, 0x04 });
        try
        {
            // Windows 路径大小写不敏感：同一个文件的两种写法只该装一次
            var kept = InstallService.FilterLocalPacks(new[] { f, f.ToUpperInvariant() });
            Assert.Single(kept);
        }
        finally { try { File.Delete(f); } catch { } }
    }

    [Fact]
    public void FilterLocalPacks_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(InstallService.FilterLocalPacks(null));
        Assert.Empty(InstallService.FilterLocalPacks(Array.Empty<string>()));
    }
}


