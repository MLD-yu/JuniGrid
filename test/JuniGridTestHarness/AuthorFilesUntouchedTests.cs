using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>安装过程<b>绝不改动作者的安装物</b>，尤其是 <c>manifest.json</c> 里的 <c>Version</c>。
/// <para>起因（2026-10-11 实机）：装 Simple Hoard Chest（N 网 53440）时，我们把 N 网「文件版本」栏
/// 里作者手填的 <c>1</c> 抄进了包内 manifest（作者自述本是 <c>1.24</c>）。那个字段的读者是 SMAPI，
/// 它只认「至少两段数字」，于是 SMAPI 解析失败、<b>整个 mod 根本没进游戏</b>，玩家侧的症状是
/// “Robin 的介绍信没收到”。回写当年的动机（“N 网文件 1 / 包内 0.0.1 ⇒ 永远显示可更新”）早已由
/// <see cref="NexusUpdateTruth.ShouldShowUpdate"/> 的「单段 vs 多段 = 不同编号体系」那道闸和
/// fileId 认领解决，写盘这一步只剩副作用。</para>
/// <para>这批用例钉的是两件事：① 账与盘不一致时盘也不动（把回写加回来就会变红）；
/// ② 不写盘并不会把那个“永远亮着”的病请回来 —— 显示按包内自述、更新判定按护栏。</para></summary>
public class AuthorFilesUntouchedTests : IDisposable
{
    private const int ModId = 53440;
    private const long FileId = 186038;
    private const string AuthorVersion = "1.24";     // 包内 manifest 的自述
    private const string NexusVersion = "1";         // N 网「文件版本」栏，作者手填、SMAPI 读不出

    private readonly string _game =
        Path.Combine(Path.GetTempPath(), "jg-author-files-" + Guid.NewGuid().ToString("N")[..8]);

    public AuthorFilesUntouchedTests() => Directory.CreateDirectory(_game);

    public void Dispose()
    {
        // 只收自己刚建的那一个临时目录
        try { Directory.Delete(_game, true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string ModsDir(string folder) => Path.Combine(_game, "Mods", folder);

    /// <summary>作者包的样子：带 // 注释（旧回写用整文件正则替换，注释和键序都是它的靶子）。</summary>
    private string WriteManifest(string folder)
    {
        var dir = ModsDir(folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "manifest.json");
        File.WriteAllText(path,
            "{\n  // 作者自己的注释，我们必须原样留着\n" +
            $"  \"Name\": \"Simple Hoard Chest\",\n  \"Author\": \"xSyy\",\n  \"Version\": \"{AuthorVersion}\",\n" +
            "  \"UniqueID\": \"Xsyy.SimpleHoardChest\",\n  \"EntryDll\": \"SimpleHoardChest.dll\",\n" +
            "  \"MinimumApiVersion\": \"4.1.0\"\n}\n");
        return path;
    }

    private static ModEntry Entry(string folder) => new()
    {
        Folder = folder,
        Name = "Simple Hoard Chest",
        UniqueID = "Xsyy.SimpleHoardChest",
        Version = AuthorVersion,
        NexusModId = ModId,
    };

    [Fact]
    public void RecordInstallByModId_NexusSingleSegmentVersion_LeavesAuthorsManifestByteIdentical()
    {
        var path = WriteManifest("SimpleHoardChest");
        var before = File.ReadAllBytes(path);
        var cfg = new JuniGridConfig { GamePath = _game };

        NexusUpdateTruth.RecordInstallByModId(cfg, "SimpleHoardChest", ModId, FileId, NexusVersion);

        // 盘上一个字节都不许变（把「回写 manifest」加回来，这里立刻红 —— 已实测）
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Contains("\"Version\": \"" + AuthorVersion + "\"", File.ReadAllText(path));
        Assert.Contains("// 作者自己的注释", File.ReadAllText(path));
        // 而账要照记 —— 撤掉回写不等于撤掉记录
        Assert.Equal(NexusVersion, cfg.ModNexusInstalls["SimpleHoardChest"].RemoteVersion);
        Assert.Equal(FileId, cfg.ModNexusInstalls["SimpleHoardChest"].FileId);
    }

    [Fact]
    public void RecordInstall_LeavesAuthorsManifestByteIdentical()
    {
        var path = WriteManifest("SimpleHoardChest");
        var before = File.ReadAllBytes(path);
        var cfg = new JuniGridConfig { GamePath = _game };

        NexusUpdateTruth.RecordInstall(cfg, Entry("SimpleHoardChest"), ModId, FileId, NexusVersion);

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(NexusVersion, cfg.ModNexusInstalls["Xsyy.SimpleHoardChest"].RemoteVersion);
    }

    [Fact]
    public void DisplayPrefersPackagesOwnVersion_OverNexusLabel()
    {
        // 账上是 "1"、盘上是 "1.24"：显示取包内自述。这条就是「不写盘会不会显示成 N 网那个 1」的答案。
        var cfg = new JuniGridConfig { GamePath = _game };
        var m = Entry("SimpleHoardChest");
        NexusUpdateTruth.RecordInstall(cfg, m, ModId, FileId, NexusVersion);

        Assert.Equal(AuthorVersion, NexusUpdateTruth.EffectiveLocalVersion(cfg, m));
    }

    [Fact]
    public void StaleUpdateNag_DoesNotComeBack_WithoutRewritingManifest()
    {
        var cfg = new JuniGridConfig { GamePath = _game };
        var m = Entry("SimpleHoardChest");

        // ① fileId 对得上 ⇒ 「这一版已装」，不亮
        NexusUpdateTruth.RecordInstall(cfg, m, ModId, FileId, NexusVersion);
        Assert.False(NexusUpdateTruth.ShouldShowUpdate(cfg, m, NexusVersion, FileId));

        // ② 认不出 fileId 时，靠「单段 vs 多段 = 不同编号体系」那道闸兜住（同一套断言、换一条路径）
        var cfg2 = new JuniGridConfig { GamePath = _game };
        var m2 = Entry("SimpleHoardChest");
        cfg2.ModNexusInstalls["Xsyy.SimpleHoardChest"] = new NexusInstallRecord
        {
            NexusModId = ModId, FileId = 999999, RemoteVersion = NexusVersion,
        };
        Assert.False(NexusUpdateTruth.ShouldShowUpdate(cfg2, m2, NexusVersion, FileId));
    }
}
