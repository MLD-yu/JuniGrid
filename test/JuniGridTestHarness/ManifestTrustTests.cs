using System.Security.Cryptography;
using System.Text;
using JuniGrid.Manifest;
using JuniGrid.Services;
using Xunit;

namespace JuniGridTestHarness;

// ══════════════════════════════════════════════════════════════════════════
// 自更新链路上「校验值从哪来」这件事，一共三种来源：
//   ① GitHub API 给的官方 sha256   —— 可信，但匿名配额 60 次/小时/IP，打满就没了
//   ② HTML 回落（解析页面）        —— 压根没有 sha256
//   ③ 签名清单（自己签的 latest.json）—— 有 sha256，且不依赖任何在线配额
//
// ②那条路以前是「零校验直接执行」，靠一句"要用户再点一次确认"兜着（审计第 10 条）。
// 现在③补上了这个缺口：清单里那份 sha256 是我们自己私钥签的，验签过了就能当凭据用。
//
// 这一组用例钉的是「③真的能顶上来」：
//   · 签名/验签这条路本身能走通（用临时密钥对，不碰真私钥）；
//   · 清单内容不合规时一律拒绝（fail closed），不能拿半截清单当凭据；
//   · 清单给的 sha256 能变成 .done 里的凭据 ⇒ 不再触发"再点一次确认"。
//
// ⚠ 真私钥永远不在仓库里，所以这里不可能用真私钥签 —— 用临时密钥对测算法，
//    真私钥那条路靠 tools/JuniGrid.ManifestSigner 的 verify 子命令在发版机上自检。
// ══════════════════════════════════════════════════════════════════════════

public class ManifestTrustTests
{
    private static byte[] Utf8(string s) => new UTF8Encoding(false).GetBytes(s);

    private static ManifestDocument SampleDoc(string version = "1.3.1") => new()
    {
        Schema = 1,
        Version = version,
        Sha256 = new string('a', 64),
        Size = 121_600_000,
        Url = "https://github.com/MLD-yu/JuniGrid/releases/download/v1.3.1/JuniGrid-cn-v1.3.1-setup.exe",
        PublishedAt = "2026-11-05T09:30:00Z",
        KeyId = ManifestTrust.KeyId,
    };

    // ── 算法本身 ───────────────────────────────────────────────────────────────

    /// <summary>签名 → 验签，一条路走通。这里用临时密钥对：真私钥不在仓库里，
    /// 也不该为了测试而把私钥塞进仓库（那等于把签名权送人）。</summary>
    [Fact]
    public void SignThenVerifyRoundTrips()
    {
        var (pem, pub) = ManifestSigner.GenerateKeyPair();
        var bytes = ManifestDocument.Serialize(SampleDoc());

        var sig = ManifestSigner.Sign(bytes, pem);

        Assert.Equal(64, sig.Length);          // IEEE P1363 定长：r||s，各 32 字节
        using var ec = ECDsa.Create();
        ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pub), out _);
        Assert.True(ec.VerifyData(bytes, sig, HashAlgorithmName.SHA256));
    }

    /// <summary>公钥能从私钥导出，且和签名用的是同一把 —— 这是「贴进代码的那行 base64」
    /// 必须对的前提。贴错了会一路错到发版才炸。</summary>
    [Fact]
    public void PublicKeyDerivedFromPrivateKeyMatches()
    {
        var (pem, pub1) = ManifestSigner.GenerateKeyPair();
        var pub2 = ManifestSigner.PublicKeyFromPem(pem);
        Assert.Equal(pub1, pub2);
    }

    /// <summary>
    /// 私钥备份最常见的"内容对但用不了"：文本编辑器存 UTF-8 带 BOM（记事本就有这选项），
    /// 或者前后多带空白/零宽字符。<c>ImportFromPem</c> 遇到 BOM 直接抛"不是有效 PEM"——
    /// 一份完全正确的备份被判成废的，用户只会以为自己抄错了。
    /// 所以这里钉死：带 BOM、带空白都必须能用。
    /// </summary>
    [Theory]
    [InlineData("\uFEFF")]                 // UTF-8 BOM
    [InlineData("")]                      // 无
    [InlineData("  \r\n ")]               // 空白
    [InlineData("\u200B")]                // 零宽空格
    public void PemWithBOMOrWhitespaceStillUsable(string prefix)
    {
        var (pem, pub) = ManifestSigner.GenerateKeyPair();
        var dirty = prefix + pem + "\r\n";

        // 能导出公钥（读得进去）
        Assert.Equal(pub, ManifestSigner.PublicKeyFromPem(dirty));

        // 能签名，且签出来的东西验得过
        var bytes = ManifestDocument.Serialize(SampleDoc());
        var sig = ManifestSigner.Sign(bytes, dirty);
        var spki = Convert.FromBase64String(pub);
        Assert.True(ManifestTrust.VerifyWithKey(bytes, sig, spki, out _));
    }

    /// <summary>内置公钥必须是合法 SPKI、且必须是 P-256 那条曲线。
    /// 有人手滑贴错 base64 / 贴成别的曲线，应用必须立刻发现（而不是每次更新都验签失败）。</summary>
    [Fact]
    public void EmbeddedPublicKeyIsValidP256()
    {
        var spki = Convert.FromBase64String(ManifestTrust.PublicKeySpkiBase64);
        using var ec = ECDsa.Create();
        ec.ImportSubjectPublicKeyInfo(spki, out _);    // 非法 SPKI 会在这里抛
        Assert.Equal(256, ec.KeySize);                 // P-256 = 256 位
        Assert.Equal(spki, ec.ExportSubjectPublicKeyInfo());   // 原样导出，没被改动
    }

    /// <summary>验签必须对「原始字节」生效：清单里任何一个字节被改（哪怕只改一个空格），
    /// 都必须验不过。这是签名唯一的作用 —— 改一个字节就废。</summary>
    [Fact]
    public void TamperedManifestFailsVerification()
    {
        var (pem, _) = ManifestSigner.GenerateKeyPair();
        var bytes = ManifestDocument.Serialize(SampleDoc());
        var sig = ManifestSigner.Sign(bytes, pem);

        // 篡改：把 sha256 的第一个字符换掉
        var tampered = (byte[])bytes.Clone();
        var idx = Array.IndexOf(tampered, (byte)'a');
        tampered[idx] = (byte)'b';

        using var ec = ECDsa.Create();
        ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(ManifestTrust.PublicKeySpkiBase64), out _);
        Assert.False(ec.VerifyData(tampered, sig, HashAlgorithmName.SHA256));
    }

    /// <summary>内置公钥验不了的签名，一律拒绝。空签名/空清单也不能算通过。</summary>
    [Fact]
    public void VerifyRejectsGarbage()
    {
        Assert.False(ManifestTrust.Verify(Array.Empty<byte>(), new byte[64], out _));
        Assert.False(ManifestTrust.Verify(Utf8("{}"), Array.Empty<byte>(), out _));
        Assert.False(ManifestTrust.Verify(Utf8("{}"), new byte[64], out _));   // 随机签名
    }

    /// <summary>
    /// 签名文件是 base64 文本：托管点和传输都可能给它加换行/空格/尾换行，
    /// 那些必须不影响验签（解码时空白被忽略）。
    ///
    /// <para>顺带钉住"不是 base64 的东西"不能被当成"验不过"以外的怪行为 ——
    /// 解析不了就该直接拒，不能抛。</para>
    /// </summary>
    [Fact]
    public void VerifyBase64IgnoresWhitespaceAndRejectsGarbage()
    {
        var (pem, pub) = ManifestSigner.GenerateKeyPair();
        var spki = Convert.FromBase64String(pub);
        var bytes = ManifestDocument.Serialize(SampleDoc());
        var b64 = Convert.ToBase64String(ManifestSigner.Sign(bytes, pem));

        // 传输里常见的脏法：中间换行 + 前后空白 + 尾换行
        var messy = "  " + b64.Insert(40, "\n") + "\r\n  ";
        Assert.True(ManifestTrust.VerifyBase64WithKey(bytes, messy, spki, out _));

        // 压根不是 base64 —— 拒，不能抛
        Assert.False(ManifestTrust.VerifyBase64WithKey(bytes, "这不是 base64 !!!", spki, out _));
        Assert.False(ManifestTrust.VerifyBase64WithKey(bytes, "", spki, out _));
        Assert.False(ManifestTrust.VerifyBase64WithKey(bytes, null!, spki, out _));

        // 内容被改过一个字节 —— 验不过
        var tampered = (byte[])bytes.Clone();
        tampered[tampered.Length - 2] ^= 0x20;
        Assert.False(ManifestTrust.VerifyBase64WithKey(tampered, b64, spki, out _));
    }

    // ── 清单解析：不合规就拒绝 ────────────────────────────────────────────────

    [Fact]
    public void ParseAcceptsWellFormedManifest()
    {
        var bytes = ManifestDocument.Serialize(SampleDoc());
        var doc = ManifestDocument.Parse(bytes);
        Assert.NotNull(doc);
        Assert.Equal("1.3.1", doc!.Version);
        Assert.Equal(121_600_000, doc.Size);
        Assert.Equal(new string('a', 64), doc.Sha256);
    }

    /// <summary>版本号带 v 前缀也要认 —— 清单是人写的，别因为多一个 v 就整份丢掉。</summary>
    [Fact]
    public void ParseAcceptsVersionWithVPrefix()
    {
        var doc = ManifestDocument.Parse(Utf8($$"""
            {"schema":1,"version":"v1.3.1","sha256":"{{new string('b', 64)}}","size":1,"url":"https://x/y.exe"}
            """));
        Assert.NotNull(doc);
        Assert.Equal("1.3.1", doc!.Version);
    }

    /// <summary>不合规的清单一律返回 null（fail closed）。
    /// 半截清单绝不能被拿去当凭据 —— 那是"看起来有校验值"的错觉。</summary>
    [Theory]
    [InlineData("""{"schema":2,"version":"1.3.1","sha256":"aaaa","size":1,"url":"https://x/y.exe"}""")]   // schema 不认识
    [InlineData("""{"schema":1,"version":"不是版本号","sha256":"aaaa","size":1,"url":"https://x/y.exe"}""")]
    [InlineData("""{"schema":1,"version":"1.3.1","sha256":"abc","size":1,"url":"https://x/y.exe"}""")]      // sha 长度不对
    [InlineData("""{"schema":1,"version":"1.3.1","sha256":"zzzz","size":1,"url":"https://x/y.exe"}""")]     // sha 不是十六进制
    [InlineData("""{"schema":1,"version":"1.3.1","sha256":"aaaa","size":0,"url":"https://x/y.exe"}""")]      // 大小为 0
    [InlineData("""{"schema":1,"version":"1.3.1","sha256":"aaaa","size":-5,"url":"https://x/y.exe"}""")]     // 大小为负
    [InlineData("""{"schema":1,"version":"1.3.1","sha256":"aaaa","size":1,"url":"http://x/y.exe"}""")]     // 不是 https
    [InlineData("""{"schema":1,"version":"1.3.1","size":1,"url":"https://x/y.exe"}""")]                     // 缺 sha
    [InlineData("""{"schema":1,"version":"1.3.1","sha256":"aaaa","url":"https://x/y.exe"}""")]              // 缺 size
    [InlineData("""not json at all""")]
    public void ParseRejectsMalformedManifest(string json)
    {
        Assert.Null(ManifestDocument.Parse(Utf8(json)));
    }

    // ── 映射到自更新结果 ───────────────────────────────────────────────────────

    /// <summary>清单 → SelfUpdateInfo：版本、sha256、大小都要带过去。
    /// 少了 sha256 就等于没凭据（会退回去要用户再点一次确认）。</summary>
    [Fact]
    public void ManifestMapsToUpdateInfo()
    {
        var info = SelfUpdateManifest.ToInfo(SampleDoc("1.3.1"));
        Assert.NotNull(info);
        Assert.Equal("1.3.1", info!.LatestVersion);
        Assert.Equal(new string('a', 64), info.SetupSha256);
        Assert.Equal(121_600_000, info.SetupSize);
        Assert.Contains("JuniGrid-cn-v1.3.1-setup.exe", info.SetupUrl);
    }

    /// <summary>比当前版本旧的清单不算更新（防重放：有人拿旧的已签名清单骗老用户装旧版）。</summary>
    [Fact]
    public void OlderManifestIsNotAnUpdate()
    {
        var info = SelfUpdateManifest.ToInfo(SampleDoc("0.0.1"));
        Assert.NotNull(info);
        Assert.False(info!.HasUpdate);
    }

    // ── 打通到 .done：这是"不再触发二次点击"的真正开关 ─────────────────────────

    /// <summary>
    /// 清单给出的 sha256 写进 .done 之后，<see cref="SelfUpdateService.IsVerified"/>
    /// 必须判"有凭据" —— 这是「第二次点击」那扇门的开关。门开着的意思是：
    /// 走清单这条路时，用户不会看到"未校验完整性，仍要安装"。
    ///
    /// <para>反过来：.done 里凭据为空 ⇒ 门必须关着（否则"无凭据也自动弹"就回来了，
    /// 那正是审计第 10 条的原始形态）。</para>
    /// </summary>
    [Fact]
    public void ManifestCredentialMakesCachedInstallerCountAsVerified()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jg-manifest-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var exe = Path.Combine(dir, "JuniGrid-cn-v1.3.1-setup.exe");
            File.WriteAllText(exe, "fake installer bytes");
            var doc = SampleDoc("1.3.1");

            // 有凭据（清单给的 sha256）→ .done 记下它 → 判定"有凭据"
            var done = exe + ".done";
            File.WriteAllText(done, doc.Version + " sha256=" + doc.Sha256);
            Assert.True(SelfUpdateService.IsVerified(exe));

            // 无凭据（老版本写的 .done，或零凭据放行的）→ 判定"没凭据" → 门关着
            File.WriteAllText(done, doc.Version + " ");
            Assert.False(SelfUpdateService.IsVerified(exe));

            // 压根没有 .done → 也没凭据
            File.Delete(done);
            Assert.False(SelfUpdateService.IsVerified(exe));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
