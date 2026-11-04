using JuniGrid.Services;

namespace JuniGridTestHarness;

public sealed class TrashListCacheTests : IDisposable
{
    private readonly string _game = Path.Combine(Path.GetTempPath(), "jg-trash-list-" + Guid.NewGuid().ToString("N"));

    public TrashListCacheTests()
    {
        Directory.CreateDirectory(Path.Combine(_game, "Mods"));
        TrashService.Invalidate(_game);
    }

    public void Dispose()
    {
        TrashService.Invalidate(_game);
        try { Directory.Delete(_game, true); } catch { }
    }

    [Fact]
    public void AddingSnapshotChangesTheCachedList()
    {
        var root = TrashService.TrashRoot(_game);
        Directory.CreateDirectory(root);
        Assert.Empty(TrashService.List(_game));

        var snapshot = Path.Combine(root, "Example_20261104_120000");
        Directory.CreateDirectory(snapshot);
        File.WriteAllText(Path.Combine(snapshot, "manifest.json"), "{\"Name\":\"Example\",\"Version\":\"1.0\"}");
        File.WriteAllText(Path.Combine(snapshot, "content.dat"), "payload");

        var items = TrashService.List(_game);

        var item = Assert.Single(items);
        Assert.Equal("Example", item.DisplayName);
        Assert.Equal(2, item.FileCount);
        Assert.True(item.Bytes > 0);
    }

    [Fact]
    public void ForceRefreshRecomputesSnapshotMetadata()
    {
        var snapshot = Path.Combine(TrashService.TrashRoot(_game), "Example_20261104_120000");
        Directory.CreateDirectory(snapshot);
        var content = Path.Combine(snapshot, "content.dat");
        File.WriteAllText(content, "x");
        var first = Assert.Single(TrashService.List(_game));
        File.WriteAllText(content, new string('x', 256));

        var cached = Assert.Single(TrashService.List(_game));
        var refreshed = Assert.Single(TrashService.List(_game, forceRefresh: true));

        Assert.Equal(first.Bytes, cached.Bytes);
        Assert.True(refreshed.Bytes > first.Bytes);
    }
}
