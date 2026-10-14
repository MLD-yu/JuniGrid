using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>
/// v1.2.8：依赖解析的第四道来源 —— 父包<b>说明</b>里作者亲笔写下的 N 网链接。
/// <para>为什么需要它：N 网还有第二种"前置"，是<b>按文件</b>登记的 requirements（网页 Requirements
/// 页那张「Fishing Log requires (1) → Parchment 1.14.0」卡片）。实测我们读不到它：GraphQL 的
/// <c>ModRequirements</c> 只有页面级三个字段、文件侧只剩一个 <c>requirementsAlert</c> 整数；
/// v1 的 <c>requirements.json</c> 路由已下线（带我们自己的 OAuth bearer 打是 404，与瞎写的
/// bogus 路径同码，而同一 token 打 files.json 是 200 —— 不是鉴权问题）；网页直连 403。
/// 而 50188 / 49525 两页的 <c>legacyModRequirementsEnabled</c> 都是 false（作者把页面级那一栏
/// 整个关了），所以作者写在说明正文里的那条链接是仅剩的权威源。</para>
/// <para>下面的 BBCode 是 2026-10-13 从 <c>api.nexusmods.com/v1/games/stardewvalley/mods/*.json</c>
/// 的 <c>description</c> 字段逐字摘的（未过 SanitizeHtml —— 判据不读显示变换的产物）。</para>
/// </summary>
public class DependencyDescriptionLinkTests
{
    /// <summary>真机 50188「Parchment - Fishing Log」说明里的两条链接（原文里它们之间还隔着正文）。</summary>
    private const string Desc50188 =
        "The Fishing Log is a Parchment mod. You must also download and install "
        + "[url=https://www.nexusmods.com/stardewvalley/mods/49525]Parchment [/url] for it to work. "
        + "The original source is [url=https://github.com/Floogen/StardewSandbox/tree/parchment/fishing-logbook]available here[/url].";

    /// <summary>真机 49525「Parchment」说明：两条本站链接（1915、49526）夹着四条 GitHub/外链。</summary>
    private const string Desc49525 =
        "[url=https://www.nexusmods.com/stardewvalley/mods/1915]Content Patcher[/url] "
        + "[url=https://floogen.github.io/Parchment]documentation here[/url] "
        + "[url=https://github.com/Floogen/Parchment/tree/development/Parchment/Content%20Packs/%5BCP%5D%20Parchment%20Example%20Pack]example pack[/url] "
        + "[url=https://github.com/Floogen/Campgrounds/tree/development/Campgrounds/Content%20Packs/%5BCP%5D%20Campgrounds/assets/parchment]Camping Guide[/url] "
        + "[url=https://www.nexusmods.com/stardewvalley/mods/49526]Campgrounds[/url] "
        + "[url=https://github.com/Floogen/Parchment]found here[/url]";

    [Fact]
    public void RealParentDescription_YieldsOnlyTheNexusPage()
    {
        var ids = NexusService.DescriptionModLinks(Desc50188);
        Assert.Equal(new[] { 49525 }, ids);          // GitHub 那条不算，且只出现一次
    }

    [Fact]
    public void RealFrameworkDescription_KeepsOrderAndDedups()
        => Assert.Equal(new[] { 1915, 49526 }, NexusService.DescriptionModLinks(Desc49525));

    [Fact]
    public void OtherGameDomain_IsNotAStardewCandidate()
    {
        // 别的游戏的同号页面不能当 Stardew 的依赖
        Assert.Empty(NexusService.DescriptionModLinks(
            "[url=https://www.nexusmods.com/oblivion/mods/123]Oblivion thing[/url]"));
    }

    [Theory]
    // [url]裸链[/url] 与带查询串的写法
    [InlineData("[url]https://www.nexusmods.com/stardewvalley/mods/3231[/url]", 3231)]
    [InlineData("[url=https://www.nexusmods.com/stardewvalley/mods/1720?tab=files]Json Assets[/url]", 1720)]
    [InlineData("[URL=https://www.nexusmods.com/stardewvalley/mods/2400]SMAPI[/URL]", 2400)]
    public void BareAndQueryForms_StillParse(string bbcode, int expected)
        => Assert.Equal(new[] { expected }, NexusService.DescriptionModLinks(bbcode));

    [Fact]
    public void EmptyOrNull_YieldsNothing()
    {
        Assert.Empty(NexusService.DescriptionModLinks(null));
        Assert.Empty(NexusService.DescriptionModLinks("没有链接的一段说明"));
    }

    // ---- 定唯一赢家（判据与搜索通道同一把尺：SearchHitIdentifies） ----

    [Fact]
    public void TheRealCase_FrameworkPageNameMatchesTheDottedUidSegment()
    {
        // 页面名与上传者都是 2026-10-13 从 49525 的 v1 页面证据里抄的真值
        var hit = InstallService.PickDescriptionLink("PeacefulEnd.Parchment.Core",
            new[] { (49525, "Parchment", "PeacefulEnd") });
        Assert.Equal(49525, hit);
    }

    [Fact]
    public void DecoyLink_ByTheSameAuthor_ButWrongName_IsRefused()
    {
        // 作者亲笔也可能指错页（说明里顺手链接了自家别的 mod）：名字段对不上就不认
        Assert.Null(InstallService.PickDescriptionLink("PeacefulEnd.Parchment.Core",
            new[] { (9969, "Fashion Sense", "PeacefulEnd") }));
    }

    [Fact]
    public void DecoyLink_RightName_WrongAuthor_IsRefused()
        => Assert.Null(InstallService.PickDescriptionLink("PeacefulEnd.Parchment.Core",
            new[] { (1, "Parchment", "SomeoneElse") }));

    [Fact]
    public void TwoLinksBothPassing_AreATieAndRefused()
    {
        // 并列一律不指路（与 MatchRequirement 同一处置）。第二条是构造的：
        // 真机上作者不会把同一个框架挂两页，但说明里出现"同名同作者的另一个页"完全可能。
        var hit = InstallService.PickDescriptionLink("PeacefulEnd.Parchment.Core", new[]
        {
            (49525, "Parchment", "PeacefulEnd"),
            (50000, "Parchment Legacy", "PeacefulEnd"),
        });
        Assert.Null(hit);
    }

    [Fact]
    public void TieDoesNotHideBehindAnEarlierRefusal()
    {
        // 变异靶子：若实现是"取第一个过闸的"，这一串会返回 49525 而不是拒
        var hit = InstallService.PickDescriptionLink("PeacefulEnd.Parchment.Core", new[]
        {
            (9969, "Fashion Sense", "PeacefulEnd"),        // 过不了名字闸
            (49525, "Parchment", "PeacefulEnd"),           // 过
            (50000, "Parchment Remastered", "PeacefulEnd"),// 也过 → 并列
        });
        Assert.Null(hit);
    }
}
