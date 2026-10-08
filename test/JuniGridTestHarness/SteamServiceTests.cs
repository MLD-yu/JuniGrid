using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>SteamService 的日志/清单解析 —— 全部在临时目录里造夹具，不碰真 Steam 目录。</summary>
public class SteamServiceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "jg-test-steam-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ---------------- CloudSyncFromLog：日志尾部最后一条 Starting sync 定云开关

    private string WriteLog(params string[] lines)
    {
        Directory.CreateDirectory(_root);
        var p = Path.Combine(_root, "cloud_log.txt");
        File.WriteAllLines(p, lines);
        return p;
    }

    [Fact]
    public void MissingLog_IsUnknown()
        => Assert.Equal(SteamService.CloudSync.Unknown,
            SteamService.CloudSyncFromLog(Path.Combine(_root, "nope.txt"), "413150"));

    [Fact]
    public void EmptyAppId_IsUnknown()
        => Assert.Equal(SteamService.CloudSync.Unknown,
            SteamService.CloudSyncFromLog(WriteLog("[AppID 413150] Starting sync"), ""));

    [Fact]
    public void OtherAppOnly_IsUnknown()
        => Assert.Equal(SteamService.CloudSync.Unknown,
            SteamService.CloudSyncFromLog(WriteLog(
                "[2026-10-07 10:00:00] [AppID 999999] Starting sync for app 999999"), "413150"));

    [Fact]
    public void LastSyncDisabled_Wins_Disabled()
        => Assert.Equal(SteamService.CloudSync.Disabled,
            SteamService.CloudSyncFromLog(WriteLog(
                "[10:00] [AppID 413150] Starting sync for app 413150",
                "[10:01] Sync complete (12 files)",
                "[11:00] [AppID 413150] Starting sync for app 413150 (Sync Disabled)"), "413150"));

    [Fact]
    public void LastSyncEnabled_Wins_EvenIfEarlierDisabled()
        => Assert.Equal(SteamService.CloudSync.Enabled,
            SteamService.CloudSyncFromLog(WriteLog(
                "[10:00] [AppID 413150] Starting sync for app 413150 (Sync Disabled)",
                "[11:00] [AppID 413150] Starting sync for app 413150"), "413150"));

    // ---------------- FindAppManifest：从游戏目录向上找 appmanifest_<id>.acf

    [Fact]
    public void Manifest_InGameDirSteamapps_IsFound()
    {
        var game = Path.Combine(_root, "g1");
        Directory.CreateDirectory(Path.Combine(game, "steamapps"));
        var manifest = Path.Combine(game, "steamapps", "appmanifest_413150.acf");
        File.WriteAllText(manifest, "\"");
        Assert.Equal(manifest, SteamService.FindAppManifest(game, "413150"));
    }

    [Fact]
    public void Manifest_TwoLevelsUp_IsFound()
    {
        var game = Path.Combine(_root, "g2", "common", "Stardew Valley");
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(Path.Combine(_root, "g2", "steamapps"));
        var manifest = Path.Combine(_root, "g2", "steamapps", "appmanifest_413150.acf");
        File.WriteAllText(manifest, "\"");
        Assert.Equal(manifest, SteamService.FindAppManifest(game, "413150"));
    }

    [Fact]
    public void NoManifest_ReturnsNull_NonSteamInstall()
    {
        var game = Path.Combine(_root, "g3", "deep", "er", "still", "here");
        Directory.CreateDirectory(game);
        Assert.Null(SteamService.FindAppManifest(game, "413150"));
    }

    [Theory]
    [InlineData(null, "413150")]
    [InlineData("", "413150")]
    [InlineData(@"C:\somewhere", "")]
    public void EmptyArgs_ReturnNull(string? gamePath, string appId)
        => Assert.Null(SteamService.FindAppManifest(gamePath, appId));
}
