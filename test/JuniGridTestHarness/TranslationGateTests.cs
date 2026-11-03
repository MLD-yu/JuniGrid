using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>自动翻译的放行闸：点连接的标识符（UniqueID / 文件名）绝不能送翻 ——
/// 翻译引擎会把英文句点当标点本地化：实机 2026-10-29，弹窗里的
/// <c>spacechase0.GenericModConfigMenu</c> 被翻成了 <c>spacechase0。GenericModConfigMenu</c>，
/// 玩家照抄去别处就对不上。
/// <para>JS 侧 junigrid.trans.js 的 worth() 有同一条；这里钉 C# 侧那一层，两层都不许放行。</para></summary>
public sealed class TranslationGateTests
{
    [Theory]
    [InlineData("spacechase0.GenericModConfigMenu")]
    [InlineData("Pathoschild.ContentPatcher")]
    [InlineData("junigrid.config.json")]
    [InlineData("manifest.json")]
    public void DottedIdentifiers_AreNeverTranslated(string text)
        => Assert.False(TranslationService.NeedsTranslation(text));

    [Theory]
    [InlineData("Content Patcher")]           // 带空格的正常名字照旧要翻
    [InlineData("Stardew Valley Expanded")]
    [InlineData("More grass, please!")]
    public void HumanNames_StillGoToTranslation(string text)
        => Assert.True(TranslationService.NeedsTranslation(text));
}
