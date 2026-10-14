using System.Text.Json;
using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>
/// 依赖解析的「夹具回归」：用 2026-10-07 抓取的**N 网真实返回**（fixtures/nexus-search-*.json），
/// 喂给**真实的** NexusService.ParseModNodes + InstallService.UidSearchTerms/RankByAuthor，
/// 把"某依赖被标成『未找到对应 mod』"钉死。
///
/// 为什么不手写 JSON：手写的形状是按照我们对服务端的**想象**写的，线上字段一变形就测不出来；
/// 为什么不在测试里发真网络请求：用例要确定、要能离线跑。
/// 所以取中间：真实数据落盘 + 真实 C# 代码跑。
/// </summary>
/// <summary>N 网搜索返回夹具的读取器（两套夹具文件共用一个口径）。
/// <para><b>挑选那一步必须调生产判据</b> <see cref="InstallService.SearchHitIdentifies"/>，
/// 不许在这儿抄一份判据 —— 抄一份就等于测试和线上各跑各的：线上把闸收紧了，这里照样绿，
/// 夹具回归就成了摆设（2026-10-13 之前这里正是自己抄了一遍"只看作者段"）。</para></summary>
internal static class NexusSearchFixture
{
    public static string Path_(string file)
        => System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", file);

    /// <summary>按 <c>SearchDependencyCandidatesAsync</c> 的收集策略拼候选池：
    /// 逐词取 8 条、按 modId 去重、池满 12 条即停（夹具里每个词存的就是服务端那次返回的 8 条）。</summary>
    public static List<NexusModListEntry> Pool(string file, string uid)
    {
        var path = Path_(file);
        var pool = new List<NexusModListEntry>();
        var seen = new HashSet<int>();
        using var fixture = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var term in InstallService.UidSearchTerms(uid))
        {
            if (!fixture.RootElement.TryGetProperty(term, out var block) ||
                !block.TryGetProperty("nodes", out var nodes) ||
                nodes.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var h in NexusService.ParseModNodes(nodes, hideAdult: false))
            {
                if (h.Id <= 0 || !seen.Add(h.Id)) continue;
                pool.Add(h);
            }
            if (pool.Count >= 12) break;
        }
        return pool;
    }

    /// <summary>生产判据下的赢家（null = 认不出，UI 退回"未找到对应 mod"）。</summary>
    public static NexusModListEntry? Pick(string file, string uid)
        => InstallService.RankByAuthor(uid, Pool(file, uid))
           .FirstOrDefault(h => h.Id > 0 && InstallService.SearchHitIdentifies(uid, h.Name, h.Uploader));
}

public class DependencyResolutionFixtureTests
{
    private const string File20261007 = "nexus-search-20261007.json";
    private static readonly string FixturePath = NexusSearchFixture.Path_(File20261007);

    private static NexusModListEntry? Resolve(string uid) => NexusSearchFixture.Pick(File20261007, uid);

    [Fact]
    public void Fixture_IsPresent()
        => Assert.True(File.Exists(FixturePath), "缺少夹具文件：" + FixturePath);

    [Theory]
    // 作者改名加尾缀：搜 "PIF" 第一名就是本体，旧规则因缩写 "dlx"≠"dlxn" 判 0 而弃
    [InlineData("DLX.PIF", "PIF", "DLX", "DLX.PIF")]
    // 名字段是通用词：靠"作者段兜底"那一发才让本体进池（搜 "Tilesheets" 前 8 全是大热门）
    [InlineData("Sharogg.Tilesheets", "Tilesheets", "Sharogg", "Sharogg.Tilesheets")]
    public void SearchTerms_AreInProductionOrder(string uid, string t1, string t2, string t3)
        => Assert.Equal(new[] { t1, t2, t3 }, InstallService.UidSearchTerms(uid).ToArray());

    [Theory]
    [InlineData("DLX.PIF", 20537, "PIF - Personal Rooms")]
    [InlineData("Sharogg.Tilesheets", 27117, "Sharogg's Tilesheets")]
    public void RealServerData_ResolvesToTheRightMod(string uid, int modId, string namePrefix)
    {
        var winner = Resolve(uid);
        Assert.NotNull(winner);                                   // 旧代码这两条都会是 null →「未找到」
        Assert.Equal(modId, winner!.Id);
        Assert.StartsWith(namePrefix, winner.Name);
        Assert.True(InstallService.SearchHitIdentifies(uid, winner.Name, winner.Uploader));
    }

    [Fact]
    public void RealServerData_AuthorFallbackTermNoise_DoesNotWin()
    {
        // 搜 "DLX" 会搜到一条无关的 #44017「KcatClo dlx」—— 作者段兜底带来的噪音，
        // 亲和度闸必须拦住它，否则会把用户领到别人的 mod 页。
        var winner = Resolve("DLX.PIF");
        Assert.Contains(NexusSearchFixture.Pool(File20261007, "DLX.PIF"), h => h.Id == 44017);
        Assert.NotEqual(44017, winner!.Id);                       // 但没赢
    }

    [Fact]
    public void RealServerData_GenericNameTerm_AloneWouldHaveLostIt()
    {
        // 把作者段兜底摘掉（只留旧版的名字段搜索词），验证 Sharogg 那条**确实会丢** ——
        // 这是本次修复的必要性证据；哪天有人把兜底删了，这条用例会红。
        using var fixture = JsonDocument.Parse(File.ReadAllText(FixturePath));
        var oldOnly = new List<NexusModListEntry>();
        var seen = new HashSet<int>();
        foreach (var h in NexusService.ParseModNodes(
                     fixture.RootElement.GetProperty("Tilesheets").GetProperty("nodes"), hideAdult: false))
            if (seen.Add(h.Id)) oldOnly.Add(h);

        var ranked = InstallService.RankByAuthor("Sharogg.Tilesheets", oldOnly);
        Assert.DoesNotContain(ranked, h => h.Id == 27117);         // 本体压根不在旧候选池里
        Assert.Null(ranked.FirstOrDefault(h =>
            InstallService.SearchHitIdentifies("Sharogg.Tilesheets", h.Name, h.Uploader)));
    }
}

/// <summary>
/// v1.2.8：作者段兜底词灌进来的"同作者别的 mod"必须被名字闸拦下。
/// <para>实机 2026-10-13：[Parchment] Fishing Logbook 缺前置 <c>PeacefulEnd.Parchment.Core</c>，
/// Mods 页的红字印成「缺少依赖：Fashion Sense」，而 Fashion Sense 就装在上一行、还开着 ——
/// 名字和链接都指错了。三个名字段搜索词在 N 网全是 0 命中，兜底词 "PeacefulEnd" 返回 44 个
/// mod 按下载数排，第一是 Fashion Sense（410 万），作者亲和度满分（上传者恰是作者段），
/// 旧判据只看作者闸于是照收，还把错答案写进了配置（<c>dependencyDisplays</c>）。</para>
/// <para>夹具是 2026-10-13 从 N 网 GraphQL 抓的真实返回，逐字复刻线上那条查询
/// （gameId 1303 + name/author/uploader 三路 WILDCARD + downloads DESC + count 8）。</para>
/// </summary>
public class SameAuthorNoiseFixtureTests
{
    private const string F = "nexus-search-20261013.json";
    private const string MissingUid = "PeacefulEnd.Parchment.Core";
    private const string FashionSenseUid = "PeacefulEnd.FashionSense";

    [Fact]
    public void Fixture_IsPresent()
        => Assert.True(File.Exists(NexusSearchFixture.Path_(F)), "缺少夹具文件：" + F);

    [Fact]
    public void AuthorFallbackPool_IsEntirelySameAuthorNoise()
    {
        // 正向对照：这一池噪声**过得了作者闸** —— 证明作者闸对它自己制造的噪声完全失明，
        // 也证明下面那条"认不出"不是因为池子空。
        var pool = NexusSearchFixture.Pool(F, MissingUid);
        Assert.NotEmpty(pool);
        Assert.All(pool, h => Assert.True(InstallService.AuthorAffinity(MissingUid, h.Uploader) > 0,
            "兜底词 " + MissingUid + " 的候选本该全是同作者的 mod"));
        Assert.All(pool, h => Assert.Equal(0, InstallService.NameMatchScore(MissingUid, h.Name)));
        Assert.Equal(9969, pool[0].Id);          // 旧判据（只看作者）取到的第一就是 Fashion Sense
    }

    [Fact]
    public void MissingDependency_NoLongerPointsAtSomeOtherModOfTheSameAuthor()
        => Assert.Null(NexusSearchFixture.Pick(F, MissingUid));   // 宁可"未找到"，不给错页

    [Fact]
    public void SamePool_TheUidThatDoesMatch_StillResolves()
    {
        // 双向用例的另一半：同一份真实数据里，Fashion Sense 自己仍然是认得出的。
        // 谁把判据改成"作者段兜底一律作废"或"搜索通道一律返回 null"，这条会红。
        var w = NexusSearchFixture.Pick(F, FashionSenseUid);
        Assert.NotNull(w);
        Assert.Equal(9969, w!.Id);
        Assert.Equal("Fashion Sense", w.Name);
    }
}

/// <summary>
/// v1.5.2：依赖解析的**权威链**夹具回归 —— 父包在官网登记的 Requirements 表（作者手填，
/// 每行直接带 modId）。夹具是 2026-10-08 从 N 网 GraphQL 抓的真实返回（五个父包），
/// 喂给真解析器 <see cref="NexusService.ParseRequirementNodes"/> + 真判据
/// <see cref="InstallService.MatchRequirement"/>，与上面那套搜索降级同一套打法。
///
/// 这条链存在的理由：用户报的那两条（DLX.PIF / Sharogg.Tilesheets）在搜索那条路上要靠
/// 改名尾缀、作者段兜底、下载数排序三道运气才认得出来，而 43657 的官网表里两行都明写着页面号。
/// </summary>
public class RequirementsResolutionFixtureTests
{
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "nexus-requirements-20261008.json");

    /// <summary>某个父包在夹具里的那几行（走真实解析器）。封面在生产里由第二个请求补，
    /// 与"认得出这是哪一行"无关，这里一律 null。</summary>
    private static List<NexusRequirementEx> Rows(string parentModId)
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(FixturePath));
        var nodes = fixture.RootElement.GetProperty(parentModId).GetProperty("nodes");
        return NexusService.ParseRequirementNodes(nodes)
            .Select(r => new NexusRequirementEx(r.ModName, r.Notes, r.Url, r.ModId, r.External, null))
            .ToList();
    }

    /// <summary>几个父包的前置并集（与 InstallService.MatchDeclaredRequirement 同一口径：按 modId 去重）。
    /// 传参顺序就是遍历顺序，所以用它来钉"先看到谁"会不会改变结论。</summary>
    private static List<NexusRequirementEx> Union(params string[] parents)
    {
        var all = new List<NexusRequirementEx>();
        var seen = new HashSet<int>();
        foreach (var p in parents)
            foreach (var r in Rows(p))
                if (seen.Add(r.ModId)) all.Add(r);
        return all;
    }

    [Fact]
    public void Fixture_IsPresent()
        => Assert.True(File.Exists(FixturePath), "缺少夹具文件：" + FixturePath);

    [Theory]
    // 用户报的那一条：43657「Sunrooms and Greenhouses (PIF)」的前置表
    [InlineData("DLX.PIF", 20537)]                        // 整词命中 "PIF - Personal Rooms"
    [InlineData("Sharogg.Tilesheets", 27117)]             // 整词 + 作者段双重命中
    [InlineData("mushymato.MMAP", 28423)]                 // 整词命中缩写 "… - MMAP"
    [InlineData("Pathoschild.ContentPatcher", 1915)]      // 只有连写命中 "Content Patcher"
    public void ReportedCase_ResolvesToTheDeclaredPage(string uid, int modId)
    {
        var hit = InstallService.MatchRequirement(uid, Rows("43657"));
        Assert.NotNull(hit);
        Assert.Equal(modId, hit!.ModId);
    }

    [Fact]
    public void SameNamePart_DifferentAuthor_IsNotStolen()
    {
        // 25552「Balcony (PIF)」的前置里有 4736「DaisyNiko's Tilesheets」—— 名字整词对得上，
        // 作者段却对不上，而且模组名自己声明了作者（所有格撇号）。这一行**不许**被认成
        // Sharogg.Tilesheets，否则又把用户领到别人的 mod 页上（旧代码就栽过这个：Miku → 4291）。
        var balcony = Rows("25552");
        Assert.Contains(balcony, r => r.ModId == 4736);           // 陷阱确实在这一包里
        Assert.Null(InstallService.MatchRequirement("Sharogg.Tilesheets", balcony));
    }

    [Fact]
    public void UnionWithTrapFirst_StillPicksTheCorroboratedRow()
    {
        // 两个父包都缺同一个前置时取并集：把带陷阱的那个排在前面，结论不许变。
        var hit = InstallService.MatchRequirement("Sharogg.Tilesheets", Union("25552", "43657"));
        Assert.NotNull(hit);
        Assert.Equal(27117, hit!.ModId);
    }

    [Fact]
    public void PossessiveRow_WithMatchingAuthor_StillResolves()
    {
        // 上一条码得太死的反证：作者段真对得上时，带撇号的名字照样认得出来
        // （"DaisyNiko" ↔ "DaisyNiko's"）。UID 是构造的（本机没装 DaisyNiko 的 Tilesheets），
        // 模组名与页面号是夹具里的真实值。
        var hit = InstallService.MatchRequirement("DaisyNiko.Tilesheets", Rows("25552"));
        Assert.NotNull(hit);
        Assert.Equal(4736, hit!.ModId);
    }

    [Fact]
    public void ParentWithNoDeclaredRequirements_FallsThrough()
    {
        // 2400「SMAPI」自己一行前置都没登记 → 这条链给不出答案，调用方退回全站搜索。
        Assert.Empty(Rows("2400"));
        Assert.Null(InstallService.MatchRequirement("DLX.PIF", Rows("2400")));
    }

    [Theory]
    [InlineData("X.UI")]                    // 名字段短于 3 字符：泛词会把整排行全点亮
    [InlineData("NoDotAtAll")]              // 没有作者段
    [InlineData("Lumisteria.TtilesheetsOutdoor")]   // 真 UID，但这几个父包的前置里没有它
    public void WeakOrAbsentNamePart_IsRefused(string uid)
        => Assert.Null(InstallService.MatchRequirement(uid, Rows("43657")));

    [Fact]
    public void Tie_InEveryTier_IsRefused()
    {
        // 并列一律拒（构造数据：两行都不带撇号、都能整词命中 "tilesheets"）。
        // 真实夹具里凑不出这种并列，所以这条只能构造 —— 判据要守的就是"分不出就不认"。
        var rows = new List<NexusRequirementEx>
        {
            new("Foo Tilesheets", "", "", 111, false, null),
            new("Bar Tilesheets", "", "", 222, false, null),
        };
        Assert.Null(InstallService.MatchRequirement("Zed.Tilesheets", rows));
    }
}
