using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>
/// v1.5.2：无 manifest 的图片包 —— 作者在自己页面上写明了"我是谁的覆盖包"，而包内路径恰好是
/// <b>原版 Content 相对路径</b> 时，覆盖判定必须排在"转成 CP 包 / 烤进本体"之前。
/// <para>实机病例（2026-10-08，Nexus #34635「DaisyNiko's Earthy Recolour - Chinese」）：
/// 包内是 <c>Maps/fall_beach.png</c> + <c>LooseSprites/Cursors.png</c> 共 11 张，
/// 而 <c>Content\Maps\fall_beach.xnb</c> 真的存在 ⇒ 旧顺序把它转成了 CP 包
/// （<c>JuniGrid.AssetPack.B93A5E5852</c>，11 条 <c>PatchMode: Replace</c>）。
/// 但这 11 张 <b>11/11</b> 在已装的 <c>[CP] DaisyNiko's Earthy Recolour</c>（#5255）里有同名文件，
/// 转出来的 11 条目标也<b>全部</b>落在 ER 自己那 98 条目标之内 —— 一个独有目标都没有。</para>
/// 页面号、模组名、路径形状都是真机值；图片内容用占位（这里判的是"谁的目录里有同名文件"，不是像素）。
/// </summary>
public class DeclaredHostOverlayTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jg-dho-" + Guid.NewGuid().ToString("N"));
    private readonly string _game;
    private readonly ModService _ms = new();

    private const string HostFolder = "[CP] DaisyNiko's Earthy Recolour";
    private static readonly string[] HostAssetPaths =
    {
        "assets/Maps/fall_beach.png", "assets/Maps/spring_beach.png", "assets/Maps/DesertTiles.png",
        "assets/LooseSprites/Cursors.png",
    };

    public DeclaredHostOverlayTests()
    {
        _game = Path.Combine(_root, "game");
        var host = Path.Combine(_game, "Mods", HostFolder);
        foreach (var a in HostAssetPaths)
        {
            var f = Path.Combine(host, a.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllText(f, "host");
        }
        File.WriteAllText(Path.Combine(host, "manifest.json"),
            @"{ ""Name"": ""DaisyNiko's Earthy Recolour"", ""Author"": ""Daisy"", ""Version"": ""1.4.4"",
               ""UniqueID"": ""DaisyNiko.EarthyRecolour"", ""Description"": ""x"", ""GameVersion"": ""1.6.0"",
               ""EntryDll"": """", ""UpdateKeys"": [ ""Nexus:5255"" ] }");
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    /// <summary>包内路径 → 宿主内落点。断言一律按这个字典查 —— Directory.GetFiles 的返回顺序
    /// 不是我们控制的，按位置断言只会测到枚举顺序。</summary>
    private static Dictionary<string, string> Landed(ModService.OverlayPlan? p)
        => p is null ? new() : p.RelPaths.Zip(p.DstPaths!).ToDictionary(x => x.First, x => x.Second, StringComparer.Ordinal);

    private static NexusRequirement Req(int id, string name)
        => new(name, "", "https://www.nexusmods.com/stardewvalley/mods/" + id, id, false);

    /// <summary>按给定的包内路径摆一个解包目录（模拟"包里就是这些文件"）。</summary>
    private string PackDir(params string[] relPaths)
    {
        var d = Path.Combine(_root, "p" + Guid.NewGuid().ToString("N"));
        foreach (var rp in relPaths)
        {
            var fp = Path.Combine(d, rp.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fp)!);
            File.WriteAllText(fp, "pack");
        }
        return d;
    }

    [Fact]
    public void Real34635_FlatVanillaPaths_AreMappedOntoTheHostsOwnAssets()
    {
        // 真包形状：包内就是原版 Content 相对路径，没有宿主那一层目录名。
        var d = PackDir("Maps/fall_beach.png", "Maps/spring_beach.png", "Maps/DesertTiles.png",
                        "LooseSprites/Cursors.png");
        var plan = _ms.ProbeOverlayForDeclaredHost(_game, d, new[] { Req(5255, "DaisyNiko's Earthy Recolour") });
        Assert.NotNull(plan);
        Assert.Equal(HostFolder, plan!.Host);
        Assert.Equal("", plan.PackRoot);                       // 文件全在解包根下
        Assert.Equal(new Dictionary<string, string>
        {
            ["Maps/fall_beach.png"] = "assets/Maps/fall_beach.png",
            ["Maps/spring_beach.png"] = "assets/Maps/spring_beach.png",
            ["Maps/DesertTiles.png"] = "assets/Maps/DesertTiles.png",
            ["LooseSprites/Cursors.png"] = "assets/LooseSprites/Cursors.png",
        }, Landed(plan));
    }

    [Fact]
    public void PackAlreadyUsingTheHostsOwnLayout_MatchesAsIs()
    {
        // 包里本来就写着 assets/… 这种宿主自己的形状 ⇒ 原样对上，不该被"加一层 assets/"套两次。
        var d = PackDir("assets/Maps/fall_beach.png", "assets/Maps/spring_beach.png",
                        "assets/Maps/DesertTiles.png", "assets/LooseSprites/Cursors.png");
        var plan = _ms.ProbeOverlayForDeclaredHost(_game, d, new[] { Req(5255, "DaisyNiko's Earthy Recolour") });
        Assert.NotNull(plan);
        Assert.Equal("assets/Maps/fall_beach.png", Landed(plan)["assets/Maps/fall_beach.png"]);
    }

    [Fact]
    public void NoPageData_DoesNotTakeThePack()
    {
        // 作者没填 / rar 读不动 ⇒ 一条都不接管，下面原有的转换判定照旧执行。
        var d = PackDir("Maps/fall_beach.png", "Maps/spring_beach.png",
                        "Maps/DesertTiles.png", "LooseSprites/Cursors.png");
        Assert.Null(_ms.ProbeOverlayForDeclaredHost(_game, d, null));
    }

    [Fact]
    public void SeveralDeclaredReqs_OnlyOneInstalled_IsTaken_TwoInstalled_Refuse()
    {
        // 一排前置里只有【一条】本机装着 ⇒ 那条就是宿主（SMAPI 那行先剔掉：加载器不住在 Mods\ 里）。
        // 实测 24232 登记的就是两行：#20781 与 SMAPI —— 不剔 SMAPI 就永远"分不出该盖谁"。
        var d = PackDir("Maps/fall_beach.png", "Maps/spring_beach.png",
                        "Maps/DesertTiles.png", "LooseSprites/Cursors.png");
        var oneInstalled = _ms.ProbeOverlayForDeclaredHost(_game, d, new[]
        {
            Req(1915, "Content Patcher"), Req(5255, "DaisyNiko's Earthy Recolour"), Req(2400, "SMAPI"),
        });
        Assert.NotNull(oneInstalled);

        // 两家都装着 ⇒ 分不出该盖谁，一律不接管（宁可退回原来的转换判定）。
        // 那一例单独写在 TwoInstalledModsShareTheDeclaredName_NeitherIsTakenNorNamed。
        var two = _ms.ProbeOverlayForDeclaredHost(_game, d, new[]
        {
            Req(5255, "DaisyNiko's Earthy Recolour"), Req(2400, "SMAPI"),
        });
        Assert.NotNull(two);   // 剔掉 SMAPI 后只剩一条对上 ⇒ 接管
    }

    [Fact]
    public void DeclaredHostNotInstalled_DoesNotTakeThePack()
    {
        var d = PackDir("Maps/fall_beach.png", "Maps/spring_beach.png",
                        "Maps/DesertTiles.png", "LooseSprites/Cursors.png");
        Assert.Null(_ms.ProbeOverlayForDeclaredHost(_game, d, new[] { Req(999999, "Some Mod Nobody Installed") }));
    }

    [Fact]
    public void MajorityGate_ThreeOfFourImagesStillCounts()
    {
        // 六成线：4 张里 3 张在宿主里有同名文件 = 75% ⇒ 接管；那张独有的按包内路径原样落。
        var d = PackDir("Maps/fall_beach.png", "Maps/spring_beach.png", "Maps/DesertTiles.png",
                        "Maps/aBrandNewSheet.png");
        var plan = _ms.ProbeOverlayForDeclaredHost(_game, d, new[] { Req(5255, "DaisyNiko's Earthy Recolour") });
        Assert.NotNull(plan);
        Assert.Equal("assets/Maps/aBrandNewSheet.png", Landed(plan)["Maps/aBrandNewSheet.png"]);
    }

    [Fact]
    public void BelowMajority_TwoOfFourImages_Refuses()
    {
        var d = PackDir("Maps/fall_beach.png", "Maps/spring_beach.png",
                        "Maps/nope1.png", "Maps/nope2.png", "Maps/nope3.png");
        Assert.Null(_ms.ProbeOverlayForDeclaredHost(_game, d, new[] { Req(5255, "DaisyNiko's Earthy Recolour") }));
    }

    [Fact]
    public void NonImageFiles_AreNotCountedInTheRatio()
    {
        // 命中率只算图片：readme 在不在宿主里，不该决定这包是不是它的覆盖包。
        var d = PackDir("Maps/fall_beach.png", "Maps/spring_beach.png", "Maps/DesertTiles.png",
                        "LooseSprites/Cursors.png", "README.txt");
        var plan = _ms.ProbeOverlayForDeclaredHost(_game, d, new[] { Req(5255, "DaisyNiko's Earthy Recolour") });
        Assert.NotNull(plan);
        Assert.Equal("README.txt", Landed(plan)["README.txt"]);
    }

    [Fact]
    public void TwoInstalledModsShareTheDeclaredName_NeitherIsTakenNorNamed()
    {
        // 实机 31795 的形状：页面登记的前置只有一个名字，而本机有【两个】mod 的 manifest
        // 显示名都写着它（SVE 捆绑包里 <c>[CP] Stardew Valley Expanded</c> 与
        // <c>Stardew Valley Expanded Code</c>）。旧写法把"最后一个匹配到的"当成宿主交出去 ⇒
        // 图片包会盖进随机那家，汉化包的话术会指着隔壁那个目录说"本机也装着它"。
        var sib = Path.Combine(_game, "Mods", "SecondCopy");
        Directory.CreateDirectory(sib);
        File.WriteAllText(Path.Combine(sib, "manifest.json"),
            @"{ ""Name"": ""DaisyNiko's Earthy Recolour"", ""Author"": ""Daisy"", ""Version"": ""1.4.4"",
               ""UniqueID"": ""DaisyNiko.EarthyRecolour.Two"", ""Description"": ""x"", ""GameVersion"": ""1.6.0"",
               ""EntryDll"": """", ""UpdateKeys"": [ ""Nexus:5255"" ] }");
        var reqs = new[] { Req(5255, "DaisyNiko's Earthy Recolour") };

        // 图片通道：并列 ⇒ 一张都不许写。
        var imgs = PackDir("Maps/fall_beach.png", "Maps/spring_beach.png",
                           "Maps/DesertTiles.png", "LooseSprites/Cursors.png");
        Assert.Null(_ms.ProbeOverlayForDeclaredHost(_game, imgs, reqs));

        // 汉化通道：键也分不出时，那句拒绝里不许出现任何一个候选目录名。
        var json = PackDir("zh.json");
        File.WriteAllText(Path.Combine(json, "zh.json"),
            @"{ ""a.b"": ""1"", ""c.d"": ""2"", ""e.f"": ""3"", ""g.h"": ""4"" }");
        var plan = _ms.ProbeOverlay(_game, json, out var fail, out var guess, reqs);
        Assert.Null(plan);
        Assert.Equal("HostKeysUnmatched", fail.ToString());   // 键集在本机一家都没过半
        Assert.Equal("", guess);                              // 不许报出并列里的任何一个目录
        var msg = ModService.OverlayRefusal(fail, _game, guess, ModService.SoleHostCandidate(reqs));
        Assert.DoesNotContain("SecondCopy", msg);
        Assert.DoesNotContain(HostFolder, msg);
        // 只报页面自己那一行（名字 + 页面号），不报我们从本机目录里挑的那一个。
        Assert.Contains("#5255", msg);
    }
}
