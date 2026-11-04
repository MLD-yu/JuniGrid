using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>下载/安装临时目录的自动清理：只删「超过 24 小时没动过」的条目。
/// 最要紧的一条是别删还在写的东西 —— 目录项时间不反映深层文件的改动，
/// 所以「目录旧、里面还有新文件」必须整目录留着。</summary>
public class DownloadAutoCleanTests : IDisposable
{
    private readonly string _dir = TempDirs.Create("dlclean");
    private readonly DateTime _now = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
    private DateTime Cutoff => _now - StorageService.DownloadsRetention;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string MakeFile(string name, double ageHours)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, "x");
        File.SetLastWriteTimeUtc(p, _now.AddHours(-ageHours));
        return p;
    }

    private string MakeDir(string name, double dirAgeHours, params (string Name, double AgeHours)[] files)
    {
        var d = Path.Combine(_dir, name);
        Directory.CreateDirectory(d);
        foreach (var (fn, age) in files)
        {
            var p = Path.Combine(d, fn);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, "x");
            File.SetLastWriteTimeUtc(p, _now.AddHours(-age));
        }
        // 必须在文件造完之后设目录时间：新建文件会把目录的写入时间刷新成现在
        Directory.SetLastWriteTimeUtc(d, _now.AddHours(-dirAgeHours));
        return d;
    }

    [Fact]
    public void StaleFileDeleted_FreshFileKept()
    {
        var stale = MakeFile("nxm-11209-52234.zip", 30);
        var fresh = MakeFile("direct-11209-52235.zip", 1);

        Assert.Equal(1, StorageService.PruneStaleEntries(_dir, Cutoff));
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void StaleExtractionDirDeletedWhole()
    {
        var stale = MakeDir("abc123", 30, ("manifest.json", 30), ("Sub/content.json", 30));

        Assert.Equal(1, StorageService.PruneStaleEntries(_dir, Cutoff));
        Assert.False(Directory.Exists(stale));
    }

    [Fact]
    public void DirWithFreshFileKept_EvenIfDirTimestampOld()
    {
        var d = MakeDir("still-writing", 30, ("manifest.json", 30), ("payload.bin", 2));

        Assert.Equal(0, StorageService.PruneStaleEntries(_dir, Cutoff));
        Assert.True(Directory.Exists(d));
    }

    [Fact]
    public void AutoClean_ProtectsPausedDownloadAndItsSegmentSidecars()
    {
        var destination = MakeFile("direct-11-22.zip", 48);
        var segment = MakeFile("direct-11-22.zip.seg", 48);
        var state = MakeFile("direct-11-22.zip.seg.state", 48);
        var orphan = MakeFile("orphan.zip.seg", 48);

        Assert.Equal(1, StorageService.PruneStaleEntries(_dir, Cutoff,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { destination }));

        Assert.True(File.Exists(destination));
        Assert.True(File.Exists(segment));
        Assert.True(File.Exists(state));
        Assert.False(File.Exists(orphan));
    }

    [Fact]
    public void PartialDownloadProtection_ExpiresAfterSevenDays()
    {
        var oldPath = MakeFile("old.zip", 8 * 24);
        MakeFile("old.zip.seg", 8 * 24);
        MakeFile("old.zip.seg.state", 8 * 24);
        var recentPath = MakeFile("recent.zip", 6 * 24);
        MakeFile("recent.zip.seg", 6 * 24);
        MakeFile("recent.zip.seg.state", 6 * 24);
        var tasks = new[]
        {
            new TaskItem { PartialDownloadPath = oldPath, Status = "paused" },
            new TaskItem { PartialDownloadPath = recentPath, Status = "paused" }
        };

        var protectedPaths = StorageService.GetFreshPartialDownloadPaths(tasks, _now);

        Assert.DoesNotContain(oldPath, protectedPaths);
        Assert.Contains(recentPath, protectedPaths);
    }

    [Fact]
    public void ExpiredPausedDownloadAndSidecars_ArePruned()
    {
        var destination = MakeFile("expired.zip", 8 * 24);
        var segment = MakeFile("expired.zip.seg", 8 * 24);
        var state = MakeFile("expired.zip.seg.state", 8 * 24);
        var protectedPaths = StorageService.GetFreshPartialDownloadPaths(
            new[] { new TaskItem { PartialDownloadPath = destination, Status = "paused" } }, _now);

        Assert.Equal(3, StorageService.PruneStaleEntries(_dir, Cutoff, protectedPaths));
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(segment));
        Assert.False(File.Exists(state));
    }

    [Fact]
    public void MissingDir_IsNoOp()
        => Assert.Equal(0, StorageService.PruneStaleEntries(Path.Combine(_dir, "nope"), Cutoff));
}
