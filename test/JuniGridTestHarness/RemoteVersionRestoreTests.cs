using JuniGrid.Components.Pages;
using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>行上的「⇧新版本 x.y.z」徽标与筛选栏的「可更新」计数读的是<b>同一个字段</b>
/// （<c>st.RemoteVersion</c> + <c>st.RemoteFileId</c>）。谁往那儿写多强的证据，直接决定玩家看到几个。
/// <para>起因（实机 2026-10-12）：进 Mods 页先亮 4 个、等会儿又冒出一个，精查回来只剩 2 个。
/// 差的就是 fileId —— 「这一版我已经装过了」四条判据里三条靠它（ModService.AlreadyHasRemoteFile），
/// 而进页回灌用的会话缓存原先只存版本号，回灌时 fileId 传死 <c>null</c>。</para>
/// <para>这批用例钉三件事：① 回灌的证据强度必须与终态一致；② 只回版本号确实会多亮一下
/// （那一下就是故障本身，别当成正常表现改回去）；③ 快道是唯一来源的那些行必须照旧亮 ——
/// 治闪烁不许把没配 N 网 Key 用户的「可更新」整个关掉。</para></summary>
public class RemoteVersionRestoreTests
{
    // 真账（2026-10-12 从 %AppData%\JuniGrid\junigrid.config.json 抄的「边境农场」那两条）
    private const int SvePage = 3753;              // 它俩都挂在 SVE 主页上
    private const long FrontierFileId = 127365;    // 我们下的那条 optional：文件版本 1.15.9
    private const long PageMainFileId = 135998;    // 该页最新 MAIN：文件版本 1.15.11

    /// <summary>边境农场自己记着 1.15.9，同页另一个子包（SVE Code）已经装过 135998 那条 MAIN。</summary>
    private static JuniGridConfig FrontierLedger()
    {
        var cfg = new JuniGridConfig { GamePath = @"X:\unused-in-these-cases" };
        cfg.ModNexusInstalls["flashshifter.FrontierFarm"] = new NexusInstallRecord
        { NexusModId = SvePage, FileId = FrontierFileId, RemoteVersion = "1.15.9" };
        cfg.ModNexusInstalls["FlashShifter.SVECode"] = new NexusInstallRecord
        { NexusModId = SvePage, FileId = PageMainFileId, RemoteVersion = "1.15.11" };
        return cfg;
    }

    private static ModEntry FrontierFarm() => new()
    {
        Folder = "Frontier Farm/[CP] Frontier Farm",
        Name = "Frontier Farm",
        UniqueID = "flashshifter.FrontierFarm",
        Version = "1.15.9",
        NexusModId = SvePage,
    };

    [Fact]
    public void RestoreCarriesFileId_SameVerdictAsSettledLane()
    {
        var cfg = FrontierLedger();
        var m = FrontierFarm();

        // 终态：精查/指纹带着 fileId 回来 ⇒ 判已装，不亮
        Assert.False(NexusUpdateTruth.ShouldShowUpdate(cfg, m, "1.15.11", PageMainFileId));
        // 回灌必须给出同一个答案（fileId 一起端回来）
        Assert.False(new Mods.RemoteVersionCache("1.15.11", DateTime.UtcNow,
            fileId: PageMainFileId).ShowsUpdate(cfg, m));
    }

    [Fact]
    public void RestoreWithVersionOnly_IsTheFlickerItself()
    {
        // 旧写法：缓存里根本没有 fileId，回灌时传 null ⇒ 靠 fileId 的三条判据全部无从下手，
        // 只剩版本串相比（1.15.9 vs 1.15.11）⇒ 这一行当场亮成「新版本 1.15.11」，
        // 几秒后精查回来又灭。玩家看到的就是计数在跳。
        Assert.True(new Mods.RemoteVersionCache("1.15.11", DateTime.UtcNow)
            .ShowsUpdate(FrontierLedger(), FrontierFarm()));
    }

    [Fact]
    public void PrecheckOnlyLane_StillReportsUpdate()
    {
        // 反例闸门：没配 N 网 Key 的用户走的是「全部按 smapi.io 版本号判定」那条路，
        // 永远拿不到 fileId。若把「没有 fileId 就不亮」当成修法，这批人的「可更新」会整个消失。
        var cfg = new JuniGridConfig { GamePath = @"X:\unused-in-these-cases" };
        var crops = new ModEntry
        {
            Folder = "PassableCrops",
            Name = "Passable Crops",
            UniqueID = "NCarigon.PassableCrops",
            Version = "1.2.3",
            NexusModId = 15223,
        };

        Assert.True(new Mods.RemoteVersionCache("1.3.0", DateTime.UtcNow).ShowsUpdate(cfg, crops));
    }
}
