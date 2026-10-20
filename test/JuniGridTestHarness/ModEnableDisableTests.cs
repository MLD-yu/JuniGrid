using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>Mod 启停的落盘形状：启用 = 去掉目录名的前导点，禁用 = 加前导点。
/// 为什么钉这个：整页 186 个 mod 的开关全走 ModService.SetDisabled 一条路，
/// 而它此前没有任何用例覆盖（475 个用例里 0 个碰它）。目录改名是唯一的状态载体，
/// 一旦改名规则写错，界面显示和 SMAPI 实际加载就会分家 —— 症状是「关了还在跑」。</summary>
public class ModEnableDisableTests : IDisposable
{
    private readonly string _game =
        Path.Combine(Path.GetTempPath(), "jg-enable-disable-" + Guid.NewGuid().ToString("N")[..8]);

    public ModEnableDisableTests() => Directory.CreateDirectory(Path.Combine(_game, "Mods"));

    public void Dispose()
    {
        try { Directory.Delete(_game, true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string Dir(string folder) => Path.Combine(_game, "Mods", folder);

    private void MakeMod(string folder) => Directory.CreateDirectory(Dir(folder));

    /// <summary>禁用把目录加前导点；再启用把它摘掉。</summary>
    [Fact]
    public void Disable_AddsLeadingDot_Enable_RemovesIt()
    {
        MakeMod("SomeMod");
        var svc = new ModService();

        svc.SetDisabled(_game, "SomeMod", true);
        Assert.True(Directory.Exists(Dir(".SomeMod")));
        Assert.False(Directory.Exists(Dir("SomeMod")));

        svc.SetDisabled(_game, "SomeMod", false);
        Assert.True(Directory.Exists(Dir("SomeMod")));
        Assert.False(Directory.Exists(Dir(".SomeMod")));
    }

    /// <summary>目录不存在时报错，不静默成功（调用方要靠返回值弹提示）。</summary>
    [Fact]
    public void MissingFolder_ReturnsError()
    {
        var err = new ModService().SetDisabled(_game, "NoSuchMod", true);
        Assert.False(string.IsNullOrEmpty(err));
    }

    /// <summary>子包只改末段：顶层名保持不变（v1.1.5 起的行为，SVE 这类多子包靠它）。</summary>
    [Fact]
    public void SubPackage_OnlyLeafIsDotted()
    {
        MakeMod("Top");
        Directory.CreateDirectory(Dir("Top/Sub"));

        new ModService().SetDisabled(_game, "Top/Sub", true);

        Assert.True(Directory.Exists(Dir("Top/.Sub")));
        Assert.True(Directory.Exists(Dir("Top")));
        Assert.False(Directory.Exists(Dir("Top/Sub")));
    }

    /// <summary>对已禁用的子包再禁用一次：末段已带点，目标名等于现名，直接返回 null 且不碰盘。</summary>
    [Fact]
    public void DisableTwice_SecondIsNoOp()
    {
        MakeMod("Top");
        Directory.CreateDirectory(Dir("Top/Sub"));

        new ModService().SetDisabled(_game, "Top/Sub", true);
        var again = new ModService().SetDisabled(_game, "Top/.Sub", true);

        Assert.Null(again);
        Assert.True(Directory.Exists(Dir("Top/.Sub")));
    }

    /// <summary>启用时解开带点的祖先（真实界面传的就是带点名）。
    /// 逐段从顶往下改名，改完末段才动 —— 避免半开状态让 SMAPI 仍整棵跳过。</summary>
    [Fact]
    public void DottedAncestor_Enable_UndotsTopDown()
    {
        MakeMod(".Top");
        Directory.CreateDirectory(Dir(".Top/.Sub"));

        new ModService().SetDisabled(_game, ".Top/.Sub", false);

        Assert.True(Directory.Exists(Dir("Top")));
        Assert.True(Directory.Exists(Dir("Top/Sub")));
        Assert.False(Directory.Exists(Dir(".Top")));
    }
}
