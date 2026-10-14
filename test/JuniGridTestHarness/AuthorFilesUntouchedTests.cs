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

    /// <summary>实机 2026-10-13：鹕鸟镇（N 网 50344）包内 manifest 自述 <c>1.2.6</c>，作者把 N 网
    /// 「文件版本」栏填成 <c>26</c>。旧显示逻辑拿两个字段比大小，<c>26 &gt; 1</c> 成立 ⇒ 界面印成 <c>26</c>，
    /// 玩家看到的是「启动器把作者的版本号改了」。盘上其实一个字节都没动</summary>
    [Theory]
    [InlineData("1.2.6", "26")]    // 鹕鸟镇实测：两套编号体系
    [InlineData("1.24", "1")]      // Simple Hoard Chest 实测
    [InlineData("0.0.1", "1.4")]   // 账上的写法段数更少，但更大
    [InlineData("3.0.0", "3.1")]   // 同段数、账上更高 —— 也一样不许盖
    [InlineData("1.0", "999")]
    public void Display_AlwaysShowsPackagesOwnVersion_NeverTheNexusLabel(string author, string nexusFileLabel)
    {
        var cfg = new JuniGridConfig { GamePath = _game };
        var m = Entry("SimpleHoardChest");
        m.Version = author;
        NexusUpdateTruth.RecordInstall(cfg, m, ModId, FileId, nexusFileLabel);

        Assert.Equal(author, NexusUpdateTruth.EffectiveLocalVersion(cfg, m));
    }

    [Fact]
    public void Display_FallsBackToInstallRecord_WhenPackageStatesNoVersion()
    {
        var cfg = new JuniGridConfig { GamePath = _game };
        var m = Entry("SimpleHoardChest");
        m.Version = "";
        NexusUpdateTruth.RecordInstall(cfg, m, ModId, FileId, "26");

        Assert.Equal("26", NexusUpdateTruth.EffectiveLocalVersion(cfg, m));
    }

    /// <summary>2026-10-13 对真机的全量口径（只读）：<c>modNexusInstalls</c> 537 行、盘上 220 份 manifest、
    /// 按 UID 对上 139 行；两个字段<b>不一致</b>的有 20 行，其中旧显示逻辑会把作者值顶掉的是下面 9 个。
    /// 所以这不是鹕鸟镇一个特例，是一批 —— 全部按「显示取包内自述」这一条规则一起解决，
    /// 新装的 mod 不需要再加任何判断（显示这一格已经没有能读到 N 网标签的代码路径）。</summary>
    [Theory]
    [InlineData("1.2.6", "26")]                                            // PelipperTown.Mod / N 网 50344
    [InlineData("0.6.20", "0.7.0")]                                        // LetsMoveIt
    [InlineData("2.0.0", "2.0.1")]                                         // PartOfTheCommunity
    [InlineData("1.2.3", "1.3.0")]                                         // PassableCrops
    [InlineData("2.2.0", "2.3.2")]                                         // SDV-Radiance
    [InlineData("0.6.1", "2.7.0")]                                         // SpritesInDetail
    [InlineData("0.6.1", "0.6.4-unofficial-mushymato.1")]                  // StardewUI
    [InlineData("0.2.0-beta", "0.2.9-beta")]                               // Stardropium
    [InlineData("1.0.2", "4.0.0")]                                         // [CP] More Bakery / N 网 51650
    public void Display_TakesPackageOwnVersion_ForEveryRealConflictOnHisMachine(
        string author, string nexusFileLabel)
    {
        var cfg = new JuniGridConfig { GamePath = _game };
        var m = Entry("RealConflict");
        m.Version = author;
        NexusUpdateTruth.RecordInstall(cfg, m, ModId, FileId, nexusFileLabel);

        Assert.Equal(author, NexusUpdateTruth.EffectiveLocalVersion(cfg, m));
    }

    /// <summary>显示改取包内自述，但更新判定不能跟着瞎 —— 把比大小一起撤掉的话，
    /// 「文件栏当计数器用」的作者（26 → 27）永远不再提示更新。这条钉住分工：
    /// 显示看盘，判定看账上那一条同类编号。</summary>
    /// <summary>「以后下载的 mod 会不会又被写成 N 网那个号」—— 端到端跑一遍真路径：
    /// 造 zip → <see cref="ModService.InstallUpdate"/>（解包、旧版进 <c>.junigrid_trash</c>、装新版）
    /// → 真 <see cref="ModService.Scan"/> → 真记帐（N 网文件栏这次叫 <c>27</c>）。
    /// 三处都要落在作者自己写的 <c>1.2.6</c> 上：装出来的返回值、扫出来的行、显示的那一格。</summary>
    [Fact]
    public void FreshDownload_ThroughTheRealInstallPath_PrintsAuthorsOwnVersion()
    {
        const string Manifest = "{\"Name\":\"Pelipper Town\",\"Author\":\"AlexanderVerde\","
            + "\"Version\":\"1.2.6\",\"UniqueID\":\"Griff.PelipperTown\",\"EntryDll\":\"PelipperTown.Mod.dll\"}";

        var stage = Path.Combine(_game, "stage", "PelipperTown.Mod");
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "manifest.json"), Manifest);
        File.WriteAllText(Path.Combine(stage, "PelipperTown.Mod.dll"), "stub");
        var zip = Path.Combine(_game, "mod-50344-186001.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(stage, zip);

        var dest = ModsDir("PelipperTown.Mod");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "manifest.json"), Manifest);

        var svc = new ModService();
        Assert.Null(svc.InstallUpdate(_game, "PelipperTown.Mod", zip, out var newVer, "Griff.PelipperTown"));
        Assert.Equal("1.2.6", newVer);

        var cfg = new JuniGridConfig { GamePath = _game };
        var entry = svc.Scan(_game).Single(e => e.UniqueID == "Griff.PelipperTown");
        NexusUpdateTruth.RecordInstall(cfg, entry, 50344, 186001, "27");

        Assert.Equal("1.2.6", entry.Version);
        Assert.Equal("1.2.6", NexusUpdateTruth.EffectiveLocalVersion(cfg, entry));
        Assert.Contains("\"Version\":\"1.2.6\"", File.ReadAllText(Path.Combine(dest, "manifest.json")));
    }

    [Fact]
    public void UpdateBadge_StillTracksTheSameNumberingSystem_AsTheInstallRecord()
    {
        var cfg = new JuniGridConfig { GamePath = _game };
        var m = Entry("PelipperTown.Mod");
        m.UniqueID = "Griff.PelipperTown";
        m.Version = "1.2.6";
        NexusUpdateTruth.RecordInstall(cfg, m, 50344, 185392, "26");

        Assert.True(NexusUpdateTruth.ShouldShowUpdate(cfg, m, "27", 186001));   // 新的一条文件
        Assert.False(NexusUpdateTruth.ShouldShowUpdate(cfg, m, "26", 185392));  // 已装这一条
        Assert.Equal("1.2.6", NexusUpdateTruth.EffectiveLocalVersion(cfg, m));  // 显示仍是作者的值
    }

    [Fact]
    public void UpdateBadge_StaysOff_ForSingleSegmentRemoteOverMultiSegmentPackage()
    {
        // 没有安装记录可比 ⇒ 判定退回包内自述 ⇒「段数不同 = 不同编号体系」那道闸生效
        // （Haley 动漫肖像包 0.0.1 ⇧ 1 那个「怎么更新都消不掉」的病）
        var cfg = new JuniGridConfig { GamePath = _game };
        var m = Entry("HaleyAnimePortraits");
        m.Version = "0.0.1";

        Assert.False(NexusUpdateTruth.ShouldShowUpdate(cfg, m, "1", null));
    }
}
