using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>ModService.ChooseFolderName 是 internal static —— white-box 安装目录命名规则。</summary>
public class ModServiceFolderNameTests
{
    [Theory]
    [InlineData("NiceMod", false, null, false, "x.zip", "NiceMod")]
    public void CleanInnerName_PassesThrough(string inner, bool root, string? manifest,
        bool bundle, string zip, string expected)
        => Assert.Equal(expected, ModService.ChooseFolderName(inner, root, manifest, bundle, zip));

    [Fact]
    public void NonRootInnerName_IsUsedRaw_EvenWithSpecialChars()
    {
        // 行为钉住：只有兜底路径才清洗 —— zip 内层目录名原样使用
        Assert.Equal("My:Mod?", ModService.ChooseFolderName("My:Mod?", false, null, false, "x.zip"));
    }

    [Fact]
    public void AtZipRoot_FallsBackToManifestName_Sanitized()
        => Assert.Equal("A_B_C", ModService.ChooseFolderName("", true, "A:B*C", false, "x.zip"));

    [Fact]
    public void Bundle_UsesZipFileNameWithoutExtension()
        => Assert.Equal("My Pack!", ModService.ChooseFolderName("", true, null, true, @"D:\dl\My Pack!.zip"));

    [Fact]
    public void BlankName_FallsBackToNewMod()
        => Assert.Equal("NewMod", ModService.ChooseFolderName("", true, "   ", false, "x.zip"));

    [Fact]
    public void LossyName_UsesFallback()
        => Assert.Equal("Fallback", ModService.ChooseFolderName("Bad\uFFFD", false, "Fallback", false, "x.zip"));
}

/// <summary>覆盖对象分组/分类的纯函数（含 2026-10-07 用户问出来的 Minigames 归类修复）。</summary>
public class ContentOverrideTargetsTests
{
    [Theory]
    [InlineData("Portraits/Abigail_Winter", "冬季")]
    [InlineData("Characters/Haley_Beach", "沙滩")]
    [InlineData("Portraits/Abigail", "默认")]
    [InlineData("Characters/Abigail_JojaMart", "JojaMart 打工")]
    [InlineData("Portraits/Leah_Hospital", "医院上班")]
    [InlineData("Characters/Krobus_Trenchcoat", "风衣")]
    [InlineData("Animals/Horse", "默认")]                      // 非人物资产 → 默认
    public void VariantOf_SuffixTable(string asset, string expected)
        => Assert.Equal(expected, ContentOverrideTargets.VariantOf(asset));

    [Theory]
    [InlineData("Assorted_Fishermen_Winter", "Assorted_Fishermen")]  // 只剥一次已知后缀
    [InlineData("Abigail_Hospital", "Abigail")]
    [InlineData("Abigail", "Abigail")]
    public void PersonRoot_StripsOneKnownSuffix(string leaf, string expected)
        => Assert.Equal(expected, ContentOverrideTargets.PersonRoot(leaf));

    [Theory]
    [InlineData("Portraits/Abigail", "人物")]
    [InlineData("Characters/Abigail", "人物")]
    [InlineData("Animals/Horse", "动物")]
    [InlineData("Buildings/houses", "建筑")]
    [InlineData("TerrainFeatures/tree1", "树木植被")]
    [InlineData("LooseSprites/Cursors", "界面杂图")]
    [InlineData("Minigames/TitleButtons", "界面杂图")]         // 标题界面图集与 LooseSprites 同类
    [InlineData("Tilesheets/MenuTiles", "地图图块")]
    [InlineData("Maps/spring_outdoorsTileSheet", "地图贴图")]
    [InlineData("Fonts/SmallFont", "字体")]
    [InlineData("Data/NPCDispositions", "其他")]
    public void CategoryOf_TopFolderMapping(string asset, string expected)
        => Assert.Equal(expected, ContentOverrideTargets.CategoryOf(asset));

    [Theory]
    [InlineData("人物", 0)]
    [InlineData("动物", 1)]
    [InlineData("建筑", 2)]
    [InlineData("树木植被", 3)]
    [InlineData("界面杂图", 4)]
    [InlineData("地图图块", 5)]
    [InlineData("地图贴图", 6)]
    [InlineData("字体", 7)]
    [InlineData("其他", 8)]
    public void CategoryOrder_IsStable(string category, int expected)
        => Assert.Equal(expected, ContentOverrideTargets.CategoryOrder(category));

    [Fact]
    public void VariantOrder_DefaultFirst_ThenByRarity()
    {
        Assert.Equal(-1, ContentOverrideTargets.VariantOrder(ContentOverrideTargets.DefaultVariant));
        Assert.Equal(0, ContentOverrideTargets.VariantOrder("冬季"));
        Assert.Equal(1, ContentOverrideTargets.VariantOrder("沙滩"));
    }

    [Theory]
    [InlineData("Portraits/Abigail", true)]
    [InlineData("portraits/abigail", true)]                    // 大小写不敏感
    [InlineData("Characters/Dog", true)]
    [InlineData("Animals/Dog", false)]
    [InlineData("LooseSprites/Cursors", false)]
    public void IsPerson_BothPrefixes(string asset, bool expected)
        => Assert.Equal(expected, ContentOverrideTargets.IsPerson(asset));
}
