using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>
/// DepotDownloader 组件下载的官方 digest 查询 —— 两个纯函数的形状钉。
/// <para>起因：v1.3 给镜像下载补 SHA-256 校验（治「镜像把文件换成别的」）。
/// 校验本身只在「取到 digest」时才比对，取不到照常放行 —— 所以这条防线
/// 的全部强度都压在「能不能正确地从 URL 推出 API 地址、从响应里取出 digest」
/// 上。这两步是正则 + JSON 形状匹配，手改一个字符校验就静默失效，
/// 且失效后没有任何报错（只会悄悄不校验）。钉死它们。</para>
/// </summary>
public class DepotDigestLookupTests
{
    // ── URL → API 地址 ────────────────────────────────────────

    [Fact]
    public void StandardReleaseDownloadUrl_MapsToTagsApi()
    {
        var api = DepotDownloaderService.ReleaseApiUrlForAsset(
            "https://github.com/SteamRE/DepotDownloader/releases/download/DepotDownloader_3.4.0/DepotDownloader-framework.zip",
            out var name);
        Assert.Equal("https://api.github.com/repos/SteamRE/DepotDownloader/releases/tags/DepotDownloader_3.4.0", api);
        Assert.Equal("DepotDownloader-framework.zip", name);
    }

    [Fact]
    public void SchemeAndHostAreMatchedCaseInsensitively()
    {
        var api = DepotDownloaderService.ReleaseApiUrlForAsset(
            "HTTPS://GITHUB.COM/a/b/releases/download/t/f.zip", out var name);
        Assert.NotNull(api);
        Assert.Equal("f.zip", name);
    }

    [Theory]
    [InlineData("https://ghfast.top/https://github.com/a/b/releases/download/t/f.zip")]  // 镜像前缀没剥
    [InlineData("https://example.com/a/b/releases/download/t/f.zip")]                    // 不是 GitHub
    [InlineData("https://github.com/a/b/releases/download/t")]                           // 缺文件名
    [InlineData("https://github.com/a/b/releases/tag/t")]                                // 页面不是下载直链
    [InlineData("")]
    public void NonCanonicalUrl_YieldsNull_AndCallerSkipsLookup(string url)
    {
        Assert.Null(DepotDownloaderService.ReleaseApiUrlForAsset(url, out var name));
        Assert.Null(name);
    }

    [Fact]
    public void PercentEncodedFileName_IsUnescaped()
    {
        DepotDownloaderService.ReleaseApiUrlForAsset(
            "https://github.com/a/b/releases/download/t/my%20tool.zip", out var name);
        Assert.Equal("my tool.zip", name);
    }

    // ── API 响应 → digest ─────────────────────────────────────

    [Fact]
    public void MatchingAssetWithDigest_ReturnsLowercaseHex()
    {
        var json = """
        {"assets":[
          {"name":"other.zip","digest":"sha256:AAAA"},
          {"name":"tool.zip","digest":"sha256:13eb4b9ee5adedde47b31e975c1d90c73ad27b6b165a1dcd80c7c545eb65b903"}
        ]}
        """;
        var sha = DepotDownloaderService.ExtractDigestFromReleaseJson(json, "tool.zip");
        Assert.Equal("13eb4b9ee5adedde47b31e975c1d90c73ad27b6b165a1dcd80c7c545eb65b903", sha);
    }

    [Fact]
    public void AssetNameMatchIsCaseInsensitive()
    {
        var json = """{"assets":[{"name":"Tool.Zip","digest":"sha256:ab12"}]}""";
        Assert.Equal("ab12", DepotDownloaderService.ExtractDigestFromReleaseJson(json, "tool.zip"));
    }

    [Fact]
    public void DigestWithoutPrefix_IsNotAcceptedAsSha256()
    {
        // 只认 "sha256:" 前缀。将来若上游加别的算法（sha512:…），不能误拿它当 sha256 比对。
        var json = """{"assets":[{"name":"tool.zip","digest":"sha512:ffff"}]}""";
        Assert.Null(DepotDownloaderService.ExtractDigestFromReleaseJson(json, "tool.zip"));
    }

    [Theory]
    [InlineData("""{"assets":[{"name":"tool.zip"}]}""")]                // 老资产没有 digest 字段
    [InlineData("""{"assets":[{"name":"tool.zip","digest":null}]}""")]  // 显式 null
    [InlineData("""{"assets":[{"name":"tool.zip","digest":"   "}]}""")] // 空白
    [InlineData("""{"assets":[]}""")]                                   // 空清单
    [InlineData("""{"assets":[{"name":"other.zip","digest":"sha256:ab"}]}""")]  // 没有这个资产
    [InlineData("""{"message":"Not Found"}""")]                         // 错误响应
    [InlineData("not json at all")]                                     // 镜像回的错误页
    public void AnythingUnrecognised_YieldsNull_CallerProceedsUnverified(string json)
        => Assert.Null(DepotDownloaderService.ExtractDigestFromReleaseJson(json, "tool.zip"));
}
