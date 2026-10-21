using JuniGrid;

namespace JuniGridTestHarness;

/// <summary>
/// 卸载收尾（<c>--purge-install-dir</c>）的行为钉。
/// <para>起因：v1.3 把「cmd.exe 延时自删」换成拉起自身子实例收尾 —— 旧那串
/// <c>ping &amp; del /q /f &amp; rd</c> 是杀软启发式规则里最硬的经典命中（误报主要来源）。
/// 换实现时四场景只在开发机手测过一遍；手测不算防线，钉在这里才算。
/// 尤其要钉死两条不变量：<b>只删两个已知文件名</b>、<b>目录非空绝不删目录</b> ——
/// 版本缓存可以合法住在安装目录里，整树删等于把用户 886 MB 的东西端走。</para>
/// </summary>
public class PurgeInstallDirTests
{
    // ── 参数形状 ──────────────────────────────────────────────

    [Fact]
    public void PurgeArguments_IsFlagThenDir()
    {
        var args = UninstallWindow.PurgeArguments(@"E:\JuniGrid1");
        Assert.Equal(2, args.Length);
        Assert.Equal("--purge-install-dir", args[0]);
        Assert.Equal(@"E:\JuniGrid1", args[1]);
    }

    // ── 分流判据：什么时候才算收尾实例 ────────────────────────

    [Fact]
    public void NoFlag_IsNotPurgeMode()
        => Assert.False(App.TryRunPurgeInstallDir([]));

    [Fact]
    public void NoFlag_AmongOtherArgs_IsNotPurgeMode()
        => Assert.False(App.TryRunPurgeInstallDir(["nxm://stardewvalley/mods/1"]));

    [Fact]
    public void FlagIsMatchedCaseInsensitively()
    {
        // 大小写不同也要认成收尾实例（返回 true = 不进正常启动流程）。
        // 目录不存在 ⇒ 什么都不删，但分流本身必须成立。
        Assert.True(App.TryRunPurgeInstallDir(["--PURGE-INSTALL-DIR", TempDirs.Create("purge-case")]));
    }

    [Fact]
    public void FlagAfterOtherArgs_StillPurges()
    {
        var dir = FakeInstall(withUserFile: false);
        Assert.True(App.TryRunPurgeInstallDir(["--uninstall", "--purge-install-dir", dir]));
        Assert.False(Directory.Exists(dir));
    }

    // ── 安全边界：缺参数 / 目录不存在，都不许炸、都不许落回正常启动 ──

    [Fact]
    public void MissingDirArgument_HandledAndTouchesNothing()
        => Assert.True(App.TryRunPurgeInstallDir(["--purge-install-dir"]));

    [Fact]
    public void NonexistentDir_IsSafe()
        => Assert.True(App.TryRunPurgeInstallDir(
            ["--purge-install-dir", Path.Combine(Path.GetTempPath(), "jg-purge-nope-" + Guid.NewGuid().ToString("N"))]));

    // ── 核心不变量 1：只删两个已知文件名 ──────────────────────

    [Fact]
    public void OnlyTheTwoExes_AreDeletedAndDirRemoved()
    {
        var dir = FakeInstall(withUserFile: false);
        Assert.True(App.TryRunPurgeInstallDir(["--purge-install-dir", dir]));
        Assert.False(Directory.Exists(dir));   // 空了就收掉
    }

    [Fact]
    public void UnrelatedDll_NeverTouched_DirKept()
    {
        var dir = FakeInstall(withUserFile: false);
        File.WriteAllText(Path.Combine(dir, "JuniGrid.dll"), "assembly");   // 同名不同缀，不在白名单
        Assert.True(App.TryRunPurgeInstallDir(["--purge-install-dir", dir]));
        Assert.True(File.Exists(Path.Combine(dir, "JuniGrid.dll")));
        Assert.True(Directory.Exists(dir));
    }

    // ── 核心不变量 2：目录非空绝不删目录 ──────────────────────

    [Fact]
    public void UserFileSurvives_AndDirIsKept()
    {
        var dir = FakeInstall(withUserFile: true);
        Assert.True(App.TryRunPurgeInstallDir(["--purge-install-dir", dir]));
        Assert.False(File.Exists(Path.Combine(dir, "JuniGrid.exe")));
        Assert.False(File.Exists(Path.Combine(dir, "Uninstall.exe")));
        Assert.True(File.Exists(Path.Combine(dir, "cache.dat")));
        Assert.True(Directory.Exists(dir));    // 还剩一个文件 ⇒ 原样留着
    }

    [Fact]
    public void NonEmptySubdir_KeepsWholeDir()
    {
        var dir = FakeInstall(withUserFile: false);
        var sub = Path.Combine(dir, "tools", "DepotDownloader");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "DepotDownloader.exe"), "third party");
        Assert.True(App.TryRunPurgeInstallDir(["--purge-install-dir", dir]));
        Assert.True(Directory.Exists(dir));
        Assert.True(File.Exists(Path.Combine(sub, "DepotDownloader.exe")));
    }

    // ── 夹具 ──────────────────────────────────────────────────

    /// <summary>造一个假的安装目录：两个 exe（内容随意，收尾只认文件名），
    /// 可选再放一个「用户的文件」模拟版本缓存留在安装目录里的情况。</summary>
    private static string FakeInstall(bool withUserFile)
    {
        var dir = TempDirs.Create("purge");
        File.WriteAllText(Path.Combine(dir, "JuniGrid.exe"), "fake");
        File.WriteAllText(Path.Combine(dir, "Uninstall.exe"), "fake");
        if (withUserFile) File.WriteAllText(Path.Combine(dir, "cache.dat"), "user data");
        return dir;
    }
}
