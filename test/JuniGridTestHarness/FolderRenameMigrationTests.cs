using JuniGrid.Components.Pages;

namespace JuniGridTestHarness;

/// <summary>Mods.MigrateFolderKeys：兼容性预查那七本字典按 folder 索引，改名时必须跟着搬。
/// 少搬一本，那一行的证据就留在旧键上 —— 「维护存疑」的徽标与计数当场消失、筛进去一片空。</summary>
public class FolderRenameMigrationTests
{
    [Fact]
    public void TopRename_MovesTheWholeSubtree_AndLeavesOthersAlone()
    {
        var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["SVE"] = "ok",
            ["SVE/[CP] SVE"] = "unofficial",
            ["SVE/Extras"] = "expired",
            ["Other/.Sub"] = "broken",
        };
        Mods.MigrateFolderKeys(d, "SVE", ".SVE");
        Assert.Equal(new[] { ".SVE", ".SVE/Extras", ".SVE/[CP] SVE", "Other/.Sub" },
            d.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList());   // Ordinal：'E'(0x45) 在 '['(0x5B) 前
        Assert.Equal("unofficial", d[".SVE/[CP] SVE"]);      // 值跟着走，不是搬成一个空键
        Assert.Equal("broken", d["Other/.Sub"]);             // 别人家的键不许动
    }

    [Fact]
    public void ChildRename_MovesOnlyThatRow()
    {
        var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Muu/[CP] Muu"] = "unofficial",
            ["Muu/Extras"] = "ok",
        };
        Mods.MigrateFolderKeys(d, "Muu/[CP] Muu", "Muu/.[CP] Muu");
        Assert.Equal("unofficial", d["Muu/.[CP] Muu"]);
        Assert.Equal("ok", d["Muu/Extras"]);
        Assert.Equal(2, d.Count);                            // 不许留下旧键
    }
}
