using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>更新落地后硬删旧肖像页覆盖包（SelfUpdateService.PurgePortraitOverrides）。
/// 契约三条：四种命名都认、直接删不进 .junigrid_trash、没有就什么都不做。</summary>
public class PortraitOverridePurgeTests : IDisposable
{
    private static readonly string[] Names =
    {
        "~JuniGrid Portrait Overrides",
        ".~JuniGrid Portrait Overrides",
        "JuniGrid Portrait Overrides",
        ".JuniGrid Portrait Overrides",
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "jg-purge-" + Guid.NewGuid().ToString("N"));

    private string MakeMods()
    {
        var mods = Path.Combine(_root, "Mods");
        Directory.CreateDirectory(mods);
        return mods;
    }

    private static string MakePack(string mods, string name)
    {
        var dir = Path.Combine(mods, name);
        Directory.CreateDirectory(Path.Combine(dir, "assets"));
        File.WriteAllText(Path.Combine(dir, "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "assets", "a.png"), "x");
        return dir;
    }

    [Fact]
    public void DeletesAllFourNames_HardNotTrashed()
    {
        var mods = MakeMods();
        var packs = Names.Select(n => MakePack(mods, n)).ToList();
        var sibling = MakePack(mods, "[CP] Some Other Mod");

        Assert.Equal(4, SelfUpdateService.PurgePortraitOverrides(new List<string> { mods }));

        foreach (var p in packs) Assert.False(Directory.Exists(p), $"没删掉 {p}");
        Assert.True(Directory.Exists(sibling), "误删了同名规则之外的 mod");
        Assert.False(Directory.Exists(Path.Combine(mods, ".junigrid_trash")), "不该产生回收站");
        Assert.False(Directory.Exists(Path.Combine(mods, "junigrid_trash")), "不该产生回收站");
    }

    [Fact]
    public void Absent_IsNoOpAndReturnsZero()
    {
        var mods = MakeMods();
        var sibling = MakePack(mods, "Portraiture");
        var before = Directory.GetFileSystemEntries(mods);

        Assert.Equal(0, SelfUpdateService.PurgePortraitOverrides(new List<string> { mods }));

        Assert.True(Directory.Exists(sibling));
        Assert.Equal(before.OrderBy(x => x, StringComparer.Ordinal),
                     Directory.GetFileSystemEntries(mods).OrderBy(x => x, StringComparer.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(mods, ".junigrid_trash")), "不该产生回收站");
    }

    [Fact]
    public void MissingModsDir_IsTolerated()
    {
        // Mods 整个不存在（新暂存的版本包还没建抽屉）也不能抛
        Assert.Equal(0, SelfUpdateService.PurgePortraitOverrides(
            new List<string> { Path.Combine(_root, "nope", "Mods"), "" }));
    }

    [Fact]
    public void ScansEverySuppliedVersionDir()
    {
        // 当前游戏 + 各版本抽屉：调用方给几个就扫几个
        var a = MakeMods();
        var b = Path.Combine(_root, "staged-1.4", "Mods");
        Directory.CreateDirectory(b);
        var pa = MakePack(a, Names[0]);
        var pb = MakePack(b, Names[1]);

        Assert.Equal(2, SelfUpdateService.PurgePortraitOverrides(new List<string> { a, b }));
        Assert.False(Directory.Exists(pa));
        Assert.False(Directory.Exists(pb));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }
}
