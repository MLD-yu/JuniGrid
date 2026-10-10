using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>v1.2.7：默认缓存落点离开 %TEMP%（改成安装目录）+ 一次性搬家。
/// <para>起因：默认落点一改，「配置里那一栏空着」的含义就变了 —— 存量从没设过缓存目录的人，
/// 东西全躺在临时目录里。没有这次搬家，新默认一上线他们就会"看不见"Nexus 登录态、
/// SMAPI 更新前的 Mods 备份和几百 MB 版本缓存（<c>InstallMigrate</c> 注释警告的正是这个形态）。
/// 反过来，默认值要是还留在临时目录，那个位置归系统与各类清理工具处置。</para>
/// <para>这里钉的是两件事：<b>默认落点永远不在 %TEMP% 之下</b>（含安装目录本身在 TEMP/同步盘/
/// 不可写时的退路），以及<b>搬家不许把用户的东西弄丢</b>（搬不动就报旧路径、两处都有就不动）。</para></summary>
public class DefaultCacheRootTests : IDisposable
{
    // ResolveDefaultCacheRoot 认的是「同步盘之外、且可写」的安装目录，所以放行用例
    // 不能拿 %TEMP% 下的目录当安装目录 —— 夹具自己的临时目录正好落在被拒绝的那一类里。
    private readonly string _outsideTemp = Path.Combine(
        StoragePaths.LocalAppDataDir + "-defaultcache-test", Guid.NewGuid().ToString("N")[..8]);

    public DefaultCacheRootTests() => Directory.CreateDirectory(_outsideTemp);

    public void Dispose()
    {
        try { Directory.Delete(_outsideTemp, true); } catch { }
        GC.SuppressFinalize(this);
    }

    // ─────────────── ① 默认落点永远不在临时目录里 ───────────────

    // 这张名单只活在测试里：它钉的是「这 8 类东西的默认落点不许在临时目录」。
    // 生产代码里不该再有同名的表 —— 搬家与存储页都改成直接看旧位置里有什么（见
    // Migration_MovesThingsNoListKnowsAbout）。
    static readonly string[] CacheSubNames =
    {
        "depot-staging", "downloads", "mods-backup", "portrait-covers",
        "self-update", "smapi-installer", "webview2", "nexus-browse",
    };

    [Fact]
    public void EveryMovableItem_DefaultsOutOfTemp()
    {
        var prevRoot = StoragePaths.CacheRoot;
        try
        {
            StoragePaths.CacheRoot = null;
            foreach (var sub in CacheSubNames)
            {
                var dir = StoragePaths.InCache(sub);
                Assert.False(dir.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase),
                    $"{sub} 的默认落点还在临时目录：{dir}");
                Assert.False(dir.StartsWith(StoragePaths.TempRoot, StringComparison.OrdinalIgnoreCase));
            }
            Assert.False(StoragePaths.LegacySmapiInstallDat
                .StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase));
        }
        finally { StoragePaths.CacheRoot = prevRoot; }
    }

    [Fact]
    public void Resolve_KeepsPlainInstallDir_RejectsTempAndUnwritable()
    {
        // 正常安装目录：就用它，程序和数据住一起
        var plain = Path.Combine(_outsideTemp, "JuniGrid");
        Directory.CreateDirectory(plain);
        Assert.Equal(plain, StoragePaths.ResolveDefaultCacheRoot(plain));

        // 安装目录本身在 %TEMP% 里（测试舱、临时拷一份跑）：不退到同一个被清理的地方
        var inTemp = TempDirs.Create("inst");
        Assert.Equal(Path.Combine(StoragePaths.LocalAppDataDir, "cache"),
            StoragePaths.ResolveDefaultCacheRoot(inTemp));

        // 不存在 / 不可写：不能把缓存写到一个创建不出来的地方
        Assert.Equal(Path.Combine(StoragePaths.LocalAppDataDir, "cache"),
            StoragePaths.ResolveDefaultCacheRoot(Path.Combine(_outsideTemp, "does-not-exist")));
    }

    [Fact]
    public void Resolve_DoesNotTrustPathNames()
    {
        // 名字里带 onedrive 不算同步盘 —— 判据只认系统自己声明的 OneDrive 根（环境变量）
        var namedLikeSync = Path.Combine(_outsideTemp, "OneDrive");
        Directory.CreateDirectory(namedLikeSync);
        Assert.Equal(namedLikeSync, StoragePaths.ResolveDefaultCacheRoot(namedLikeSync));
    }

    [Fact]
    public void Resolve_RejectsTheRealOneDriveRoot()
    {
        var declared = Environment.GetEnvironmentVariable("OneDrive");
        if (string.IsNullOrWhiteSpace(declared)) return;            // 这台没挂 OneDrive，判据无从验证
        // 探针目录必须是自己名字里带 guid 的一次性子目录。以前这里写死
        // Path.Combine(declared, "Desktop", "JuniGrid") —— 那是开发仓库本身，finally 的
        // Directory.Delete(inside, true) 每次跑夹具都把整个仓库递归删掉（2026-10-10 连发三次）。
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var inside = Path.Combine(desktop, "jg-cache-root-probe-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.False(Directory.Exists(inside));
        Directory.CreateDirectory(inside);
        try
        {
            Assert.True(inside.StartsWith(declared.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase),
                "探针目录得真的落在同步盘里，否则这条判据没测到东西");
            Assert.NotEqual(inside, StoragePaths.ResolveDefaultCacheRoot(inside));
            Assert.False(StoragePaths.ResolveDefaultCacheRoot(inside)
                .StartsWith(declared.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        }
        finally { try { Directory.Delete(inside, true); } catch { } }
    }

    // ─────────────── ② 一次性搬家：不许把用户的东西弄丢 ───────────────

    private static void MakeTree(string root, string sub, params (string Name, string Content)[] files)
    {
        var dir = Path.Combine(root, sub);
        foreach (var (name, content) in files)
        {
            var full = Path.Combine(dir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
    }

    [Fact]
    public void Migration_MovesEverythingAndLeavesNothingBehind()
    {
        var srcRoot = TempDirs.Create("mig-src");
        var dstRoot = TempDirs.Create("mig-dst");
        MakeTree(srcRoot, "downloads", ("a.zip", "AAA"));
        MakeTree(srcRoot, "webview2", ("Local State", "{\"login\":1}"));
        MakeTree(srcRoot, "mods-backup", ("smapi/Mods-1/a.txt", "B"));
        MakeTree(srcRoot, "nexus-browse", ("x.json", "{}"));        // 曾经漏抄的那一项
        File.WriteAllText(Path.Combine(srcRoot, "_smapi-install.dat"), "DAT");

        StorageService.MigrateFromLegacyTempRoot(null, out var stuck, out var moved, srcRoot, dstRoot);

        Assert.Null(stuck);
        Assert.Equal(5, moved);
        Assert.Equal("AAA", File.ReadAllText(Path.Combine(dstRoot, "downloads", "a.zip")));
        Assert.Equal("{\"login\":1}", File.ReadAllText(Path.Combine(dstRoot, "webview2", "Local State")));
        Assert.Equal("B", File.ReadAllText(Path.Combine(dstRoot, "mods-backup", "smapi", "Mods-1", "a.txt")));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(dstRoot, "nexus-browse", "x.json")));
        Assert.Equal("DAT", File.ReadAllText(Path.Combine(dstRoot, "_smapi-install.dat")));
        Assert.False(Directory.Exists(srcRoot));                    // 空壳也收掉，不留"看着像没搬完"的目录
    }

    /// <summary>生产代码里已经没有"缓存目录名单"这第三张表：搬家直接看旧位置里有什么。
    /// 这条用例钉的就是这一点 —— 任何表都不认识的目录也得照样搬走。
    /// 若有人把它改回按名单循环，这条先红（名单里没这个名字 ⇒ 留在旧位置 ⇒ 下面两条断言失败）。</summary>
    [Fact]
    public void Migration_MovesThingsNoListKnowsAbout()
    {
        var srcRoot = TempDirs.Create("nl-src");
        var dstRoot = TempDirs.Create("nl-dst");
        MakeTree(srcRoot, "some-future-cache", ("thing.bin", "FUTURE"));
        MakeTree(srcRoot, "downloads", ("a.zip", "A"));

        StorageService.MigrateFromLegacyTempRoot(null, out var stuck, out var moved, srcRoot, dstRoot);

        Assert.Null(stuck);
        Assert.Equal(2, moved);
        Assert.Equal("FUTURE", File.ReadAllText(Path.Combine(dstRoot, "some-future-cache", "thing.bin")));
        Assert.Equal("A", File.ReadAllText(Path.Combine(dstRoot, "downloads", "a.zip")));
    }

    [Fact]
    public void Migration_DoesNotTouchAUserChosenCacheRoot()
    {
        var srcRoot = TempDirs.Create("mig2-src");
        var dstRoot = TempDirs.Create("mig2-dst");
        MakeTree(srcRoot, "downloads", ("a.zip", "AAA"));

        // 用户自己在设置里指过缓存目录 → 一律不动（那一栏非空时应用读的就是他指的地方）
        StorageService.MigrateFromLegacyTempRoot(dstRoot, out var stuck, out var moved, srcRoot, dstRoot);

        Assert.Equal(0, moved);
        Assert.Null(stuck);
        Assert.Equal("AAA", File.ReadAllText(Path.Combine(srcRoot, "downloads", "a.zip")));
    }

    [Fact]
    public void Migration_WhenWebView2IsLocked_ReportsOldPathAndSwitchesToNothing()
    {
        var srcRoot = TempDirs.Create("mig3-src");
        var dstRoot = TempDirs.Create("mig3-dst");
        MakeTree(srcRoot, "webview2", ("lockme.db", "IN-USE"), ("Local State", "{\"login\":1}"));
        var locked = Path.Combine(srcRoot, "webview2", "lockme.db");

        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            StorageService.MigrateFromLegacyTempRoot(null, out var stuck, out var moved, srcRoot, dstRoot);

            // 调用方据此写 PendingWebView2MoveFrom，本会话继续用旧目录 —— 绝不能切到一个空的新目录，
            // 那等于把 Nexus 登录状态"弄丢"（登录态还在旧目录，应用已经不去看它了）
            Assert.Equal(Path.Combine(srcRoot, "webview2"), stuck);
            Assert.Equal(0, moved);
            // 被独占的那个文件读不了（这正是本用例要造的状态），所以只能验"它还在、没被搬空"
            Assert.True(File.Exists(locked));
            Assert.Equal(6, new FileInfo(locked).Length);                     // "IN-USE"
            Assert.Equal("{\"login\":1}", File.ReadAllText(
                Path.Combine(srcRoot, "webview2", "Local State")));            // 同目录的其它文件也没被搬走
            Assert.False(Directory.Exists(Path.Combine(dstRoot, "webview2"))); // 不许留半成品
        }
    }

    [Fact]
    public void Migration_WhenBothPlacesHaveData_KeepsBothAndUsesNeitherForNothing()
    {
        var srcRoot = TempDirs.Create("mig4-src");
        var dstRoot = TempDirs.Create("mig4-dst");
        MakeTree(srcRoot, "mods-backup", ("old.txt", "OLD"));
        MakeTree(srcRoot, "downloads", ("old.zip", "O"));
        MakeTree(dstRoot, "mods-backup", ("new.txt", "NEW"));       // 两处都有：不替他选哪份活下来

        StorageService.MigrateFromLegacyTempRoot(null, out var stuck, out var moved, srcRoot, dstRoot);

        Assert.Equal(1, moved);                                     // downloads 搬了，mods-backup 没动
        Assert.Null(stuck);
        Assert.Equal("OLD", File.ReadAllText(Path.Combine(srcRoot, "mods-backup", "old.txt")));
        Assert.Equal("NEW", File.ReadAllText(Path.Combine(dstRoot, "mods-backup", "new.txt")));
        Assert.False(Directory.Exists(Path.Combine(srcRoot, "downloads")));
        Assert.Equal("O", File.ReadAllText(Path.Combine(dstRoot, "downloads", "old.zip")));
    }

    [Fact]
    public void Migration_ReplacesAnEmptyNewShellInsteadOfGivingUp()
    {
        // 应用曾经在新默认位置启动过一次（留了个空壳），旧位置才是真有数据的那一份
        var srcRoot = TempDirs.Create("mig5-src");
        var dstRoot = TempDirs.Create("mig5-dst");
        MakeTree(srcRoot, "portrait-covers", ("thumb.png", "PNG"));
        Directory.CreateDirectory(Path.Combine(dstRoot, "portrait-covers"));

        StorageService.MigrateFromLegacyTempRoot(null, out var stuck, out var moved, srcRoot, dstRoot);

        Assert.Equal(1, moved);
        Assert.Null(stuck);
        Assert.Equal("PNG", File.ReadAllText(Path.Combine(dstRoot, "portrait-covers", "thumb.png")));
    }
}
