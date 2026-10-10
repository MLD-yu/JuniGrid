using JuniGrid.Components.Pages;
using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>Mods 页行序（Mods.OrderRows）：捆绑包与散装 mod 排进同一个序列。
/// 钉住两条旧规则：① 捆绑包整批被钉在列表末尾（刚下载的包看着像没下完）；
/// ② 「最新在前」跨组时按最旧排。散装 mod 那批的可见顺序必须一字不变。</summary>
public class BundleRowOrderTests
{
    private static ModEntry M(string folder, string name, int day = 1) => new()
    { Folder = folder, Name = name, LastWrite = new DateTime(2026, 10, day) };

    /// <summary>界面上这一行显示的名字：捆绑包 = 顶层目录名，散装 mod = 它自己的名字。</summary>
    private static List<string> Labels(List<Mods.ModGroup> units)
        => units.Select(g => g.Key.Length > 0 ? g.Key : g.Items[0].Name).ToList();

    [Fact]
    public void Bundle_InterleavesWithFlatMods_Alpha()
    {
        var mods = new List<ModEntry>
        {
            M("Automate", "Automate"),
            M("Muu/[CP] Muu", "Muu Child"),
            M("Muu/Extras", "Muu Extras"),
            M("ZombiePants", "ZombiePants"),
        };
        Assert.Equal(new[] { "Automate", "Muu", "ZombiePants" }, Labels(Mods.OrderRows(mods, "alpha")));
    }

    [Fact]
    public void Bundle_IsOneRowUnit_CarryingAllItsChildren()
    {
        var units = Mods.OrderRows(new List<ModEntry>
        {
            M("Automate", "Automate"),
            M("Muu/[CP] Muu", "Muu Child"),
            M("Muu/Extras", "Muu Extras"),
        }, "alpha");
        Assert.Equal(2, units.Count);                       // 一行捆绑包 + 一行散装，不是三行
        Assert.Equal(2, units.Single(u => u.Key == "Muu").Items.Count);
        Assert.Equal(1, units.Count(u => u.Key == ""));     // 散装 mod 各自成一行
    }

    [Fact]
    public void NewestFirst_SortsByTheBundlesNewestChild()
    {
        // time-asc = 「最新在前」（界面标签与档位名反着读）
        var mods = new List<ModEntry>
        {
            M("Automate", "Automate", day: 1),
            M("Old/A", "Old A", day: 2),
            M("Old/B", "Old B", day: 8),
            M("NewBundle/Child", "NewBundle Child", day: 9),
            M("NewBundle/OldChild", "Old Child Inside", day: 3),
        };
        Assert.Equal(new[] { "NewBundle", "Old", "Automate" }, Labels(Mods.OrderRows(mods, "time-asc")));
    }

    [Fact]
    public void OldestFirst_InterleavesBundlesToo()
    {
        var mods = new List<ModEntry>
        {
            M("Newest", "Newest", day: 9),
            M("Bundle/A", "Bundle A", day: 1),
            M("Bundle/B", "Bundle B", day: 4),
            M("Middle", "Middle", day: 5),
        };
        Assert.Equal(new[] { "Bundle", "Middle", "Newest" }, Labels(Mods.OrderRows(mods, "time-desc")));
    }

    [Fact]
    public void FlatMods_KeepTheOrderTheyHad_BeforeBundlesWereInterleaved()
    {
        // 只有散装 mod 时，三种档位的结果必须和旧写法（根组排最前 + 组内排）逐字一致
        var mods = new List<ModEntry>
        {
            M("bbb", "bbb", day: 2),
            M("aaa", "aaa", day: 9),
            M("ccc", "ccc", day: 5),
        };
        Assert.Equal(new[] { "aaa", "bbb", "ccc" }, Labels(Mods.OrderRows(mods, "alpha")));
        Assert.Equal(new[] { "aaa", "ccc", "bbb" }, Labels(Mods.OrderRows(mods, "time-asc")));
        Assert.Equal(new[] { "bbb", "ccc", "aaa" }, Labels(Mods.OrderRows(mods, "time-desc")));
    }

    [Fact]
    public void DisabledBundle_KeepsItsOwnRowUnderTheDottedFolderName()
    {
        // 禁用 = 顶层改名 ".X"：整包仍然是一行，不会因为多个顶层而分裂
        var mods = new List<ModEntry>
        {
            M("Zeta", "Zeta", day: 1),
            M(".Yucca/A", "Yucca A", day: 2),
            M(".Yucca/B", "Yucca B", day: 3),
        };
        var units = Mods.OrderRows(mods, "alpha");
        Assert.Equal(new[] { ".Yucca", "Zeta" }, Labels(units));
        Assert.Equal(2, units.Single(u => u.Key == ".Yucca").Items.Count);
    }

    [Fact]
    public void BundleSwitch_TouchesOneTopFolderPerBundle()
    {
        // 父行整包开关与工具条批量走同一条折算：一个捆绑包 = 一次改名，不是每个子包一次。
        // 去掉折叠（直接返回 folders）这条就红。
        Assert.Equal(new[] { "Muu" },
            Mods.TopLevelsOf(new[] { "Muu/[CP] Muu", "Muu/Extras", "Muu/.Extras2" }));
        Assert.Equal(new[] { "Muu", "Automate" },
            Mods.TopLevelsOf(new[] { "Muu/[CP] Muu", "Automate" }));
    }

    [Fact]
    public void SingleChildBundle_IsJustARow_NotAnAccordion()
    {
        // 壳里只有一个 mod 的"捆绑包"不该套父行：端出那个子包本身当普通一行。
        // 两个子包的那家必须仍然是捆绑包（一条断言同时钉住两个方向）。
        var units = Mods.OrderRows(new List<ModEntry>
        {
            M("Automate", "Automate"),
            M("Grandpas Farm/[CP] Grandpas Farm", "Grandpa's Farm"),
            M("Muu/[CP] Muu", "Muu Child"),
            M("Muu/Extras", "Muu Extras"),
        }, "alpha");
        Assert.Equal(new[] { "Automate", "Grandpa's Farm", "Muu" }, Labels(units));
        var gf = units.Single(u => u.Items[0].Name == "Grandpa's Farm");
        Assert.Equal("", gf.Key);                                       // 不是捆绑包行 → 无父行/无箭头/无缩进
        Assert.Equal("Grandpas Farm/[CP] Grandpas Farm", gf.Items[0].Folder);
        Assert.Equal(2, units.Single(u => u.Key == "Muu").Items.Count);
    }

    [Fact]
    public void GroupIdentity_SurvivesTheAncestorUnDotOnEnable()
    {
        // 子包那层改名只动末段（Top/Sub ↔ Top/.Sub），但<b>启用</b>子包时会顺手解开带点的祖先
        // （.SVE/Sub → SVE/Sub，ModService.SetDisabled v1.1.7）—— 顶层名一变，按原始顶层名记的
        // "我展开过"就对不上号，症状是展开后一拨开关整包当场折回去（实机 2026-10-09 报的）。
        // 恒等实现（不剥点）会让第一条直接红。
        Assert.Equal(Mods.NormGroupKey("Stardew Valley Expanded"),
                     Mods.NormGroupKey(".Stardew Valley Expanded"));
        Assert.Equal("SVE", Mods.NormGroupKey(".SVE"));
    }

    [Fact]
    public void EnablePass_AlsoUndotsTheLeaves_AfterTheTop()
    {
        // 关：一个捆绑包一次改名（只折顶层，保留原样 —— 带点的顶层再关一次是空操作，不该被"顺手改名"）。
        Assert.Equal(new[] { ".Muu", "Muu" },
            Mods.SwitchTargetsOf(new[] { ".Muu/.A", "Muu/B" }, true));
        // 开：顶层必须排在子包前面（先解顶层，否则带点的旧路径失效），且子包按解点后的顶层名传
        // —— ModService 的启用容错只补末段的点，不补祖先，传 ".Muu/.A" 会报"找不到 Mod 文件夹"。
        Assert.Equal(new[] { "Muu", "Muu/.A", "Muu/B" },
            Mods.SwitchTargetsOf(new[] { ".Muu/.A", "Muu/B" }, false));
    }
}
