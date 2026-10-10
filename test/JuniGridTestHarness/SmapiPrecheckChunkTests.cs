using JuniGrid.Components.Pages;

namespace JuniGridTestHarness;

/// <summary>smapi.io 预查的分块（Mods.Chunks）。
/// 为什么还留着这一层：一条把整批带哑的东西确实存在（见 <see cref="SmapiInstalledVersionTests"/> ——
/// 真凶是非法 installedVersion，不是当初以为的"认不出的 key"，那条已在 2026-10-10 撤回）。
/// 分块保证"一条出事不带走全部"，所以这里的判据是<b>不丢、不重、保序、不留空块</b>。</summary>
public class SmapiPrecheckChunkTests
{
    private static List<int> Flat(List<List<int>> chunks) => chunks.SelectMany(c => c).ToList();

    [Fact]
    public void Chunks_KeepEveryItemOnce_InOrder()
    {
        var items = Enumerable.Range(1, 153).ToList();          // 他真机 153 条带 key 的 mod
        var chunks = Mods.Chunks(items, 40);

        Assert.Equal(new[] { 40, 40, 40, 33 }, chunks.Select(c => c.Count).ToArray());
        Assert.Equal(items, Flat(chunks));                      // 不丢、不重、保序
    }

    [Fact]
    public void ExactMultiple_LeavesNoTrailingEmptyChunk()
    {
        // 变异 `i <= items.Count` 或"补一块空的"会让这条红：80/40 必须是 2 块而不是 3 块。
        // 多出来的空块会白发一次请求（上游对空 mods 也回 []），而"整块回 0 行"是要上日志的
        // —— 白多一块就等于每轮都误报一次"某块回空"。
        var chunks = Mods.Chunks(Enumerable.Range(1, 80).ToList(), 40);
        Assert.Equal(2, chunks.Count);
        Assert.All(chunks, c => Assert.NotEmpty(c));
    }

    [Fact]
    public void EmptyInput_SendsNothing_NotOneEmptyBatch()
    {
        Assert.Empty(Mods.Chunks(new List<int>(), 40));
    }

    [Fact]
    public void NonPositiveSize_FallsBackToASingleBatch()
    {
        var items = Enumerable.Range(1, 7).ToList();
        Assert.Equal(items, Flat(Mods.Chunks(items, 0)));
        Assert.Single(Mods.Chunks(items, 0));
        Assert.Single(Mods.Chunks(items, -5));
    }

    [Fact]
    public void SmallerThanChunk_IsOneBatch()
    {
        // 变异成"至少切两块"会让这条红：只有一条可发时它就该待在那一块里，
        // 拆成两块等于凭空多发一次请求、还把失败域切成两半。
        var chunks = Mods.Chunks(new List<int> { 1 }, 40);
        Assert.Single(chunks);
        Assert.Single(chunks[0]);
    }
}

/// <summary>Mods.SmapiInstalledVersion —— 发给 smapi.io 的 installedVersion 必须先洗过。
/// 判据不是推出来的，是 2026-10-10 逐串实测的（每次一发只改 installedVersion 一个变量，
/// updateKeys 固定用真实存在的 nexus:541）：
///   上游收："1.0" "1.0.0" "0.1" "0.0.1" "1.0.0-beta" "1.0.0+build" ""
///   上游拒："1" "10" "1." "1.0-" "v1.0" "01.02" "1.0.0.5" "0.0.0" "0" "0.0" "abc"
/// 而<b>一条被拒，整批回 []</b>（正常 1 条 + 版本 "1" 的 1 条混发 → 0 条）。
/// 作者手写 Version:"1" 很常见（SVE 的 6 个子包、Better Stardew Valley 都是），
/// 所以这不是边角：它会让整块预查白跑，全掉进 2 车道的逐个精查。</summary>
public class SmapiInstalledVersionTests
{
    [Theory]
    [InlineData("1", "1.0.0")]                    // 实测被上游拒的单段 → 补全
    [InlineData("10", "10.0.0")]                  // 同上（作者真这么写：SVE 那 6 个子包是 "1"）
    [InlineData("1.0", "1.0.0")]
    [InlineData("0.1", "0.1.0")]
    [InlineData("1.55.0", "1.55.0")]              // 原样
    [InlineData("0.0.1", "0.0.1")]                // 上游收，别误杀
    [InlineData("1.0.0-beta", "1.0.0-beta")]
    [InlineData("1.2.3-unofficial.1-x", "1.2.3-unofficial.1-x")]
    [InlineData("1.0.0+build", "1.0.0+build")]
    [InlineData("  1.2.3  ", "1.2.3")]
    public void AcceptableShapes_AreSentInAFormUpstreamParses(string raw, string want)
        => Assert.Equal(want, Mods.SmapiInstalledVersion(raw));

    [Theory]
    [InlineData("1.")]                             // 残缺
    [InlineData("1.0-")]                           // 预发布段为空
    [InlineData("v1.0")]                           // 带 v 前缀（不替作者猜，宁可不发）
    [InlineData("01.02")]                          // 前导零
    [InlineData("1.0.0.5")]                        // 四段
    [InlineData("abc")]
    [InlineData("0.0.0")]                          // 上游把全零当非法
    [InlineData("0.0")]
    [InlineData("0")]
    [InlineData("")]
    [InlineData(null)]
    public void UnparsableVersions_AreSentAsEmpty_NotPassedThrough(string? raw)
        => Assert.Equal("", Mods.SmapiInstalledVersion(raw));

    [Fact]
    public void ZeroPaddedSingleSegment_IsNotMistakenForZero()
    {
        // 变异成"补 .0 而不是 .0.0"或"0.0 直接放行"都会让这条红：
        // "0.0" 上游收，但补成 "0.0.0" 就变成整批杀手 —— 必须退回空串。
        Assert.Equal("", Mods.SmapiInstalledVersion("0.0"));
        Assert.Equal("1.0.0", Mods.SmapiInstalledVersion("1"));
    }
}
