using System.Security.Cryptography;
using System.Text.Json;
using JuniGrid.Services;

namespace JuniGridTestHarness;

// ══════════════════════════════════════════════════════════════════════════
// 自更新那条链上，「下载完成」以前是**下载函数一返回就算成立**的。而它走的是
// 多镜像（含第三方 gh-proxy）+ Range 断点续传：
//   · ResumableDownload 读到流 EOF 就 return，从不比对 written 与 Content-Length
//     —— 镜像提前关流 = 半截文件被当成功；
//   · 紧接着无条件写 .done，下次点击直接拿这个残缺的 setup.exe 去启动。
// 用户侧的表现就是「下载成功之后打不开」，而我们自己给它盖了个"完整"的章。
// 现在两道闸：传输层看长度，产物层看官方 sha256（GitHub 在 release asset 上给 digest）。
// 其中「同长度、差一个字节」那条是变异用例：只有哈希闸能拦住它。它必须红，
// 否则说明 sha256 那道闸是摆设。
// ══════════════════════════════════════════════════════════════════════════

public class InstallerIntegrityTests
{
    /// <summary>真机抓的（2026-10-10，api.github.com /repos/MLD-yu/JuniGrid/releases/latest）。
    /// 手写 JSON 容易和线上形状不一样，测了等于没测。</summary>
    private const string RealReleaseJson = """
    {
      "tag_name": "v1.2.5",
      "assets": [
        {
          "name": "JuniGrid-cn-v1.2.5-setup.exe",
          "size": 125978998,
          "digest": "sha256:f2e8198dd375288df85aa701fe6e8daeae25d17ebcec4127c13b451b6cefbd88"
        }
      ]
    }
    """;

    private const string RealSha =
        "f2e8198dd375288df85aa701fe6e8daeae25d17ebcec4127c13b451b6cefbd88";

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Sha256Of(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    private static string MakeFile(string prefix, string content)
    {
        var p = Path.Combine(TempDirs.Create(prefix), "setup.exe");
        File.WriteAllText(p, content);
        return p;
    }

    // ───────── 官方凭据抠得到吗 ─────────

    [Fact]
    public void ReadsDigestAndSizeFromRealAssetShape()
    {
        var (sha, size) = SelfUpdateService.ReadAssetIntegrity(
            Parse(RealReleaseJson), SelfUpdateService.SetupAssetName("1.2.5"));
        Assert.Equal(RealSha, sha);                       // "sha256:" 前缀必须剥掉
        Assert.Equal(125978998L, size);
    }

    [Fact]
    public void MissingDigestFallsBackToSizeOnly()
    {
        // 早于 2024 的上传没有 digest 字段 —— 不能因此把 size 也丢掉
        var json = """{"assets":[{"name":"a-setup.exe","size":123,"digest":null}]}""";
        var (sha, size) = SelfUpdateService.ReadAssetIntegrity(Parse(json), "a-setup.exe");
        Assert.Null(sha);
        Assert.Equal(123L, size);
    }

    [Fact]
    public void ForeignAssetNameYieldsNoCredentials()
    {
        // 资产名对不上就当没查到，绝不能把别的地方的哈希安到这个包头上
        var (sha, size) = SelfUpdateService.ReadAssetIntegrity(Parse(RealReleaseJson), "other.exe");
        Assert.Null(sha);
        Assert.Equal(0L, size);
    }

    [Fact]
    public void ApiChannelPutsCredentialsOnTheInfo()
    {
        var info = SelfUpdateService.Build("v1.2.5", Parse(RealReleaseJson));
        Assert.NotNull(info);
        Assert.Equal(RealSha, info!.SetupSha256);
        Assert.Equal("sha256=" + RealSha, SelfUpdateService.Expectation(info));
    }

    [Fact]
    public void HtmlChannelCarriesNoCredentials()
    {
        // 通道 2（releases/latest 302 里抠 tag）只抠得到 tag，没有 release JSON
        var info = SelfUpdateService.Build("v9.9.9");
        Assert.NotNull(info);
        Assert.Null(info!.SetupSha256);
        Assert.Equal(0L, info.SetupSize);
        Assert.Equal("", SelfUpdateService.Expectation(info));   // 空 = 本机没凭据可证
    }

    [Fact]
    public void ExpectationPrefersDigestOverSize()
    {
        var both = new SelfUpdateInfo("9.9.9", "", "", true, RealSha, 125978998);
        Assert.StartsWith("sha256=", SelfUpdateService.Expectation(both));
        var sizeOnly = new SelfUpdateInfo("9.9.9", "", "", true, null, 125978998);
        Assert.Equal("size=125978998", SelfUpdateService.Expectation(sizeOnly));
    }

    // ───────── 落盘那个文件，放不放行 ─────────

    [Fact]
    public void GoodFilePasses()
    {
        var p = MakeFile("integ-ok", "payload-bytes");
        var info = new SelfUpdateInfo("9.9.9", "", "", true, Sha256Of(p), new FileInfo(p).Length);
        SelfUpdateService.VerifyInstaller(p, info);   // 不抛即通过
    }

    /// <summary>变异用例：长度刻意对上，只改内容。长度闸放得过去，哈希闸必须拦。</summary>
    [Fact]
    public void OneByteSwapWithCorrectLengthIsRejected()
    {
        var p = MakeFile("integ-mut", "payload-bytes");
        var len = new FileInfo(p).Length;
        var info = new SelfUpdateInfo("9.9.9", "", "", true, Sha256Of(p), len);

        File.WriteAllText(p, "payload-byteT");       // 同长度、换一个字符
        Assert.Equal(len, new FileInfo(p).Length);    // 前提：长度这道闸确实拦不住它
        Assert.Throws<SelfUpdateService.InstallerIntegrityException>(
            () => SelfUpdateService.VerifyInstaller(p, info));
    }

    [Fact]
    public void TruncatedFileIsRejected()
    {
        var p = MakeFile("integ-trunc", "payload-bytes");
        var info = new SelfUpdateInfo("9.9.9", "", "", true, Sha256Of(p), new FileInfo(p).Length);

        File.WriteAllText(p, "payload-byte");        // 少一个字节：镜像提前关流的样子
        Assert.Throws<SelfUpdateService.InstallerIntegrityException>(
            () => SelfUpdateService.VerifyInstaller(p, info));
    }

    [Fact]
    public void SizeOnlyStillCatchesTruncation()
    {
        // HTML 回落拿不到 digest 时，官方 size 也还能挡住截断这一类
        var p = MakeFile("integ-size", "payload-bytes");
        var info = new SelfUpdateInfo("9.9.9", "", "", true, null, new FileInfo(p).Length);
        SelfUpdateService.VerifyInstaller(p, info);

        File.AppendAllText(p, "X");
        Assert.Throws<SelfUpdateService.InstallerIntegrityException>(
            () => SelfUpdateService.VerifyInstaller(p, info));
    }

    [Fact]
    public void NoCredentialsDoesNotPretendToVerify()
    {
        // 没凭据时不能抛（否则国内自更新直接废掉），但也不能谎称验过 ——
        // 这条钉的是"不抛"，"记一条 WARN"由日志那一侧承担。
        var p = MakeFile("integ-none", "whatever");
        SelfUpdateService.VerifyInstaller(p, new SelfUpdateInfo("9.9.9", "", "", true));
    }

    // ───────── .done 的旧快照谁来刷 ─────────

    [Fact]
    public void OldMarkerWithoutValueForcesRecheck()
    {
        var want = "sha256=" + RealSha;
        // 老版本写的 .done 只有版本号 —— 那份缓存是"无凭据放行"留下的，不能只凭它在就信
        Assert.False(SelfUpdateService.MarkerRecords("1.2.5", want));
        Assert.False(SelfUpdateService.MarkerRecords("1.2.5 ", want));
        Assert.True(SelfUpdateService.MarkerRecords("1.2.5 " + want, want));
        // 缓存是别的版本/别的包留下的凭据 → 不算数
        Assert.False(SelfUpdateService.MarkerRecords("1.2.4 sha256=deadbeef", want));
    }

    // ───────── 传输层：流结束 ≠ 下完 ─────────

    [Fact]
    public void ShortStreamIsNotComplete()
    {
        Assert.Throws<IOException>(() => ResumableDownload.EnsureComplete(99, 100));
        ResumableDownload.EnsureComplete(100, 100);   // 刚好齐 → 放行
        ResumableDownload.EnsureComplete(50, 0);      // 服务器没给 Content-Length → 无从判断，放行给哈希兜底
    }
}
