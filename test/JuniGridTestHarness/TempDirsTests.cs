namespace JuniGridTestHarness;

/// <summary>夹具临时目录登记处的两条底线：登记过的必须删干净；没登记过的一个都不能碰。
/// <para>后者是这个类的存在意义所在 —— <c>%TEMP%</c> 是整机共享目录，并行跑的另一个夹具会话
/// 正用着它自己的沙盒，按前缀扫过去就等于替别人清场（2026-10-09 那晚真机上一个测试类漏出
/// 3244 个 <c>jg-pack-*</c>，靠"清扫"补救会把对方的活目录一起删掉）。</para></summary>
public class TempDirsTests
{
    [Fact]
    public void RegisteredDirs_AreRemovedByCleanup()
    {
        var a = TempDirs.Create("tda");
        var b = TempDirs.Create("tdb");
        File.WriteAllText(Path.Combine(a, "x.txt"), "x");
        File.WriteAllText(Path.Combine(b, "y.txt"), "y");
        Assert.True(Directory.Exists(a) && Directory.Exists(b));
        Assert.True(TempDirs.IsRegistered(a) && TempDirs.IsRegistered(b));

        Assert.True(TempDirs.Cleanup() >= 2);

        Assert.False(Directory.Exists(a));
        Assert.False(Directory.Exists(b));
        Assert.False(TempDirs.IsRegistered(a));
    }

    [Fact]
    public void UnregisteredDirWithTheSamePrefix_SurvivesCleanup()
    {
        // 别人（另一个夹具进程 / 用户）造的、长得一模一样的目录
        var foreign = Path.Combine(Path.GetTempPath(),
            "jg-pack-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(foreign);
        var mine = TempDirs.Create("pack");

        try
        {
            TempDirs.Cleanup();

            Assert.False(Directory.Exists(mine));            // 我造的没了
            Assert.True(Directory.Exists(foreign));          // 没登记的一个都不许碰
        }
        finally
        {
            try { Directory.Delete(foreign, true); } catch { }
        }
    }
}
