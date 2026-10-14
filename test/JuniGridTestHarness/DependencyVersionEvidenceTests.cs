using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>依赖溯源的判据：<b>要求的版本串必须在某一页的文件版本表里逐字存在</b>，那一页才算
/// "能供货"。这是「装了那页最新版仍不满足要求」时唯一不靠猜的认法。
/// <para>起因（实机 2026-10-12）：完美手册要 <c>focustense.StardewUI ≥0.6.4-unofficial-mushymato.0</c>，
/// 而 focustense 官方页 28870 整页最高 <c>0.6.1</c>（GitHub 也只到 v0.6.1）；那一版在 mushymato
/// 另开的 43861「StardewUI Continued」上。我们的依赖入口按"本机这份来自哪一页"取页号，
/// 于是一次次把用户领回 28870，红字永远消不掉。</para>
/// <para>为什么不用 ≥：N 网的"文件版本"栏是作者手填的自由文本，任何填着 <c>1.0.0</c> 的页都会
/// 被 ≥ 放进来 —— 那是猜，不是判。要求串是作者从某一页抄走的原话，逐字相等才是同一件事。
/// 把 <see cref="VersionUtil.Same"/> 换成"≥"，第 4 条用例立刻红。</para></summary>
public class DependencyVersionEvidenceTests
{
    // 真表（2026-10-12 从 api-router GraphQL modFiles 抓的，含 OLD_VERSION / ARCHIVED）
    private static readonly (string Version, string Category)[] Continued43861 =
    {
        ("0.6.2-unofficial-mushymato.2", "OLD_VERSION"),
        ("0.6.3-unofficial-mushymato.0", "ARCHIVED"),
        ("0.6.3-unofficial-mushymato.0", "OLD_VERSION"),
        ("0.6.3-unofficial-mushymato.1", "OLD_VERSION"),
        ("0.6.4-unofficial-mushymato.0", "OLD_VERSION"),
        ("0.6.4-unofficial-mushymato.1", "MAIN"),
    };

    // 官方 focustense 那页：最高就是 0.6.1
    private static readonly (string Version, string Category)[] Official28870 =
    {
        ("0.6.1", "MAIN"),
        ("0.6.0", "OLD_VERSION"),
        ("0.5.0", "OLD_VERSION"),
    };

    [Fact]
    public void ExactHit_PerfectionHandbookRequirement()
        => Assert.True(InstallService.PageProvidesVersion(Continued43861, "0.6.4-unofficial-mushymato.0"));

    [Fact]
    public void ExactHit_LivestockBazaarRequirement()
        => Assert.True(InstallService.PageProvidesVersion(Continued43861, "0.6.3-unofficial-mushymato.1"));

    [Fact]
    public void OfficialPage_DoesNotProvideEitherRequirement()
    {
        Assert.False(InstallService.PageProvidesVersion(Official28870, "0.6.4-unofficial-mushymato.0"));
        Assert.False(InstallService.PageProvidesVersion(Official28870, "0.6.3-unofficial-mushymato.1"));
    }

    /// <summary>这条就是"不许用 ≥"：某页文件版本填 <c>1.0.0</c>，数值上远高于要求，
    /// 但它不是作者抄走的那一句原话 ⇒ 不算供货。判据改成 ≥ 时这条变红。</summary>
    [Fact]
    public void HigherButDifferentVersionString_IsNotAHit()
        => Assert.False(InstallService.PageProvidesVersion(
            new[] { ("1.0.0", "MAIN"), ("2.5.0", "MAIN") }, "0.6.4-unofficial-mushymato.0"));

    [Fact]
    public void AnyOfSeveralRequirements_Hits()
    {
        // 同一个依赖被两个父包各自要求不同版本：任一串命中即算这一页能供货
        Assert.True(InstallService.PageProvidesAnyVersion(Continued43861,
            new[] { "0.9.9-not-here", "0.6.3-unofficial-mushymato.1" }));
        Assert.False(InstallService.PageProvidesAnyVersion(Continued43861,
            new[] { "0.9.9-not-here", "0.8.0-nope" }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void BlankRequirement_NeverHits(string? need)
        => Assert.False(InstallService.PageProvidesVersion(Continued43861, need));

    [Fact]
    public void SurroundingWhitespaceAndCase_StillHit()
    {
        // 走 VersionUtil.Same 的方言：前后空白要能吞掉（manifest 里手填常带空格）
        Assert.True(InstallService.PageProvidesVersion(Continued43861, "  0.6.4-unofficial-mushymato.0  "));
    }
}
