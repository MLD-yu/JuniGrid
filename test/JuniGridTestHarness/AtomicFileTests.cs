using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>AtomicFile 是 internal —— 靠主项目已有的 InternalsVisibleTo("JuniGridTestHarness") 直测。</summary>
public class AtomicFileTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "jg-test-atomicfile-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Write_Read_Roundtrip_And_Overwrite()
    {
        var path = Path.Combine(_dir, "nested", "deeper", "f.json");
        AtomicFile.WriteAllText(path, "v1");
        Assert.Equal("v1", File.ReadAllText(path));

        AtomicFile.WriteAllText(path, "v2");
        Assert.Equal("v2", File.ReadAllText(path));

        // 成功路径上不允许残留 .tmp
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ConcurrentWriters_LastOneWins_NoTmpLeft_NoException()
    {
        var path = Path.Combine(_dir, "tasks.json");
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            await Task.Yield();
            var content = $"v{i}";
            AtomicFile.WriteAllText(path, content);
            return content;
        }));

        var onDisk = File.ReadAllText(path);
        Assert.Contains(onDisk, results);           // 落盘的是某一方的完整内容
        Assert.Matches(@"^v\d$", onDisk);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void EmptyContent_IsWrittenLiterally()
    {
        var path = Path.Combine(_dir, "empty.txt");
        AtomicFile.WriteAllText(path, "");
        Assert.Equal("", File.ReadAllText(path));
        Assert.True(File.Exists(path));
    }
}
