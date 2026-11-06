using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>
/// 每个 mod 可以同时归进多个分类（2026-11-07 安装弹窗改成可多选）。
/// 配置里存的仍是 <c>Dictionary&lt;string,string&gt;</c>，多值用逗号拼起来 ——
/// 这些用例钉三件事：拆分不把 "9,10" 当成一个分类、取第一个决定列表分段、
/// 拼接不重复膨胀（每保存一次多一个逗号是最容易犯的错）。
/// </summary>
public class ModCategorySplitTests
{
    [Fact]
    public void Split_SeparatesMultipleCategories()
    {
        Assert.Equal(["9", "10"], ModCategoryUtil.Split("9,10"));
        Assert.Equal(["美化", "功能"], ModCategoryUtil.Split("美化,功能"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",,,")]
    public void Split_Blank_IsEmpty(string? raw)
        => Assert.Empty(ModCategoryUtil.Split(raw));

    [Fact]
    public void Split_TrimsWhitespace()
        => Assert.Equal(["9", "10"], ModCategoryUtil.Split("  9 , 10  "));

    [Fact]
    public void Split_DropsEmptyItems()
        => Assert.Equal(["9", "10"], ModCategoryUtil.Split("9,,10"));

    [Fact]
    public void Split_DeduplicatesIgnoringCase()
    {
        Assert.Equal(["Beauty"], ModCategoryUtil.Split("Beauty,beauty"));
        Assert.Equal(["9"], ModCategoryUtil.Split("9, 9"));
    }

    [Fact]
    public void Split_IgnoresLeadingComma()
        => Assert.Equal(["9"], ModCategoryUtil.Split(",9"));
}

public class ModCategoryFirstTests
{
    [Fact]
    public void First_ReturnsLeftmost()
    {
        Assert.Equal("9", ModCategoryUtil.First("9,10"));
        // 不是排序：写在前面的就是第一个
        Assert.Equal("10", ModCategoryUtil.First("10,9"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",9")]
    public void First_BlankMeansUncategorised(string? raw)
        => Assert.Equal("", ModCategoryUtil.First(raw));

    [Fact]
    public void First_TrimsSingleValue()
        => Assert.Equal("9", ModCategoryUtil.First(" 9 "));

    [Fact]
    public void First_MatchesSplitHead()
    {
        // 列表分段与 First 必须同一口径，否则同一个 mod 会画到别的段里
        foreach (var raw in new[] { "9,10", " 3 , 1 ", "Beauty,beauty", "10" })
            Assert.Equal(ModCategoryUtil.Split(raw)[0], ModCategoryUtil.First(raw));
    }
}

public class ModCategoryJoinTests
{
    [Fact]
    public void Join_CommaSeparates()
        => Assert.Equal("9,10", ModCategoryUtil.Join(["9", "10"]));

    [Fact]
    public void Join_EmptyMeansUncategorised()
    {
        // "" 的语义是「从分类表里摘掉」——空选直接装的路径就是靠它
        Assert.Equal("", ModCategoryUtil.Join(Array.Empty<string>()));
        Assert.Equal("", ModCategoryUtil.Join(null));
        Assert.Equal("", ModCategoryUtil.Join(["", "   "]));
    }

    [Fact]
    public void Join_TrimsAndDeduplicates()
        => Assert.Equal("9,10", ModCategoryUtil.Join([" 9 ", "9", "10"]));

    [Fact]
    public void Join_DropsNulls()
        => Assert.Equal("9,10", ModCategoryUtil.Join(["9", null, "10"]));

    [Fact]
    public void Join_FlattensDirtyValues()
        => Assert.Equal("9,10,11", ModCategoryUtil.Join(["9,10", "11"]));

    [Fact]
    public void Join_SingleCategoryStaysSingle()
        => Assert.Equal("9", ModCategoryUtil.Join(["9"]));

    [Fact]
    public void RoundTrip_IsIdempotent()
    {
        // 每次保存都跑一遍 Join(Split(x))：不能越存越长（9 → 9, → 9,, → …）
        var once = ModCategoryUtil.Join(ModCategoryUtil.Split("3, 1 ,3 , 7"));
        Assert.Equal("3,1,7", once);
        Assert.Equal(once, ModCategoryUtil.Join(ModCategoryUtil.Split(once)));
        Assert.Equal(once, ModCategoryUtil.Join(ModCategoryUtil.Split(ModCategoryUtil.Join(
            ModCategoryUtil.Split(once)))));
    }

    [Fact]
    public void LegacySingleValue_SurvivesRoundTrip()
    {
        // 老配置里全是单值，来回读写必须原样
        foreach (var raw in new[] { "9", "美化", "My Category" })
        {
            var joined = ModCategoryUtil.Join(ModCategoryUtil.Split(raw));
            Assert.Equal(raw, joined);
            Assert.Equal(raw, ModCategoryUtil.First(joined));
        }
    }
}
