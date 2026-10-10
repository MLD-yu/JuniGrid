using System.Security.Cryptography;
using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>「与留底不一致」这一栏：图还盖在本体上、账却已经没了 ⇒ 页面以前完全看不见它。
/// <para>起因（2026-10-09 实机）：他装过一个兽化内容包，删掉之后 6 张村民立绘仍被盖着 ——
/// 而整页的数据起点是 <c>ContentOverrides</c>，那 6 张一条记录都没有，「全部还原」按下去一张都不碰。
/// 他看到的现象就是"还原是坏的"。</para>
/// <para>这一栏【只标注】：我们手上最好的证据只是"动手前盘上的样子"，而那张本身可能就是别人盖上去的
/// （那 6 张的留底就是动漫版）—— 拿它覆盖本体等于把错误再画一遍。恢复原版只有 Steam 验证文件完整性
/// 做得到，那条链路不归我们揽（2026-10-09 他拍的）。所以这里钉的全是"该现形/该闭嘴"的边界。</para></summary>
public class StrayOverrideTests
{
    private const string Face = "Portraits/Emily";
    private const string Ver = "1.6.15";

    /// <summary>装一个包 → 把它的账清空（"包删了、账没了"）→ 本体上换成一张对不上的图。</summary>
    private static GameSandbox Covered(string packName = "Stray Pack")
    {
        var g = new GameSandbox();
        g.WriteVanilla(Face, 16, 16);
        g.Install(packName, g.WritePack(packName, (Face, 16, 16)));
        Assert.True(File.Exists(ContentOverrideService.OriginalPath(g.Dir, Face)));   // 装的时候确实留了底
        g.Cfg.Current.ContentOverrides.Clear();
        GameSandbox.Write(g.Content(Face), GameSandbox.VanillaXnb(32, 32, 0x11));     // 别人盖了一张
        return g;
    }

    [Fact]
    public void CoveredWithNoLedgerRow_IsNamed()
    {
        using var g = Covered();
        var s = Assert.Single(ContentOverrideService.Strays(g.Cfg, g.Dir));
        Assert.Equal(Face, s.Asset);
        Assert.False(s.Moved);
        Assert.Equal(Ver, s.Version);
        Assert.True(File.Exists(s.CurrentFile));
        Assert.True(File.Exists(s.OriginalFile));
    }

    [Fact]
    public void AssetWithLedgerRows_IsLeftToTheExistingStatusMachine()
    {
        // 有账的图（哪怕现值是外来的）归 StateOf/Foreign 那套管；这里再报一遍就是两边打架。
        using var g = new GameSandbox();
        g.WriteVanilla(Face, 16, 16);
        g.Install("Tracked Pack", g.WritePack("Tracked Pack", (Face, 16, 16)));
        GameSandbox.Write(g.Content(Face), GameSandbox.VanillaXnb(32, 32, 0x11));
        Assert.NotEmpty(g.Cfg.Current.ContentOverrides);
        Assert.Empty(ContentOverrideService.Strays(g.Cfg, g.Dir));
    }

    [Fact]
    public void NoSnapshot_NoClaim()
    {
        // 没留底 = 我们从来没动过这张图，手里没有任何"它该长什么样"的依据 ⇒ 不许报。
        using var g = Covered();
        File.Delete(ContentOverrideService.OriginalPath(g.Dir, Face));
        Assert.Empty(ContentOverrideService.Strays(g.Cfg, g.Dir));
    }

    [Fact]
    public void CoveredByExactlyOurSnapshot_IsNotNamed()
    {
        // 本体上就是我们记下的那份 ⇒ 没什么可报的（否则每次进页都多一栏废话）。
        using var g = new GameSandbox();
        g.WriteVanilla(Face, 16, 16);
        g.Install("Quiet Pack", g.WritePack("Quiet Pack", (Face, 16, 16)));
        var orig = ContentOverrideService.OriginalPath(g.Dir, Face);
        g.Cfg.Current.ContentOverrides.Clear();
        File.Copy(orig, g.Content(Face), overwrite: true);
        Assert.Empty(ContentOverrideService.Strays(g.Cfg, g.Dir));
    }

    [Fact]
    public void SnapshotFromAnotherVersionLayer_IsNotOffered()
    {
        // 别的版本那一层的留底说不清当前本体，跨层写图正是版本分层要拦的事。
        using var g = Covered();
        var other = ContentOverrideService.OriginalPath(g.Dir, Face, "1.5.5");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.Move(ContentOverrideService.OriginalPath(g.Dir, Face), other, overwrite: true);
        Assert.Empty(ContentOverrideService.Strays(g.Cfg, g.Dir));
    }

    [Fact]
    public void StashRow_IsListedFromTheStash_NotAsAnUnfixedCover()
    {
        // 挪开过的：本体上已经是我们那份（比字节相等 ⇒ 本来不该报），
        // 但 content-foreign 里还躺着一张来路不明的图 —— 它必须仍然现形，否则那份字节就没人知道在哪。
        using var g = Covered();
        var stash = ContentOverrideService.ForeignPath(g.Dir, Face, Ver);
        Directory.CreateDirectory(Path.GetDirectoryName(stash)!);
        File.Copy(g.Content(Face), stash, overwrite: true);
        File.Copy(ContentOverrideService.OriginalPath(g.Dir, Face), g.Content(Face), overwrite: true);

        var s = Assert.Single(ContentOverrideService.Strays(g.Cfg, g.Dir));
        Assert.True(s.Moved);
        Assert.Equal(stash, s.ForeignFile);
        // 本体已经回到我们那份，而 stash 里那张与它不同 —— 不同才值得一直列着
        Assert.Equal(Hex(ContentOverrideService.OriginalPath(g.Dir, Face)), Hex(g.Content(Face)));
        Assert.NotEqual(Hex(stash), Hex(g.Content(Face)));
    }

    private static string Hex(string p) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)));
}
