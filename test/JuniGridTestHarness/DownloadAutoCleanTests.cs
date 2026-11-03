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
    public void MissingDir_IsNoOp()
        => Assert.Equal(0, StorageService.PruneStaleEntries(Path.Combine(_dir, "nope"), Cutoff));
}
