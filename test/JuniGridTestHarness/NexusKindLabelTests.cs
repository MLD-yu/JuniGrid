using JuniGrid.Components.Pages;

namespace JuniGridTestHarness;

/// <summary>Nexus 页「排序/分类」下拉触发器上显示的那一格叫什么（<see cref="Nexus.KindLabelFor"/>）。
/// <para>起因（实机 2026-10-12 20:01，英文界面）：首页 Trending 板块的「查看全部」把
/// <c>_browseKind</c> 设成 <c>"trending"</c>，而这一档早在 v1.04.0 就从下拉表里换成了
/// <c>"downloads"</c> ⇒ 查不到 ⇒ 旧写法把 null 直接喂给 <c>Loc.T</c> ⇒
/// <c>ArgumentNullException (Parameter 'key')</c>，整个 Nexus 页每渲染一次抛一次；
/// 又因为 <c>_svKind</c> 是 static，同一次运行里每次回到该页都再炸一遍。
/// 中文界面下 <c>T</c> 第一行就原样返回 null，同一个洞只表现为"标签是空的" —— 所以一直没人发现。</para>
/// <para>钉的是「认不出也必须有字、而且不许是 null」：把那句 <c>??</c> 兜底删掉，用例立刻红。</para></summary>
public class NexusKindLabelTests
{
    /// <summary>界面上真实那张表（照抄 <c>Nexus._browseTabs</c>，含 v1.04.0 的改名）。</summary>
    private static (string Key, string Label)[] Tabs() => new[]
    {
        ("new", "最新"),
        ("updated", "最近更新"),
        ("downloads", "下载最多"),
        ("popular", "最受欢迎"),
        ("surprise", "随机"),
    };

    [Theory]
    [InlineData("new", "最新")]                  // 表里有 → 取表里的
    [InlineData("downloads", "下载最多")]
    [InlineData("trending", "热门趋势")]         // 表里没有这一档（旧 key）→ 必须走兜底，不能是 null
    [InlineData("whatever", "最新")]             // 任何认不出的 key 也都得有字
    public void KindLabel_IsNeverNull_ForAnyKind(string kind, string want)
        => Assert.Equal(want, Nexus.KindLabelFor(Tabs(), kind));
}
