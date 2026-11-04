using System.IO.Compression;
using JuniGrid.Services;

namespace JuniGridTestHarness;

public sealed class NexusDownloadCoordinationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jg-nexus-coord-" + Guid.NewGuid().ToString("N"));

    public NexusDownloadCoordinationTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task NxmReturnCanClaimReservationBeforeWindowStarts()
    {
        var slots = new NexusFetchSlotRegistry();
        var reservation = slots.Reserve(11209, 52234);

        Assert.True(reservation.IsOwner);
        Assert.True(slots.HasPending(11209, 52234));
        var mirrors = new[] { "https://mirror.example/file.zip" };
        Assert.True(slots.TryDeliver(11209, 52234, "https://cdn.example/file.zip", mirrors));
        Assert.Equal("https://cdn.example/file.zip", await reservation.Slot.DirectUrl.Task);
        var delivered = await reservation.Slot.Completion.Task;
        Assert.Equal("https://cdn.example/file.zip", delivered.Url);
        Assert.Equal(mirrors, delivered.Mirrors);
    }

    [Fact]
    public void SameModAndFileShareOnePendingTaskSlot()
    {
        var slots = new NexusFetchSlotRegistry();
        var first = slots.Reserve(11209, 52234);
        var second = slots.Reserve(11209, 52234);

        Assert.True(first.IsOwner);
        Assert.False(second.IsOwner);
        Assert.Same(first.Slot, second.Slot);
    }

    [Fact]
    public async Task RetryAfterCompletedFailureGetsANewReservation()
    {
        var slots = new NexusFetchSlotRegistry();
        var first = slots.Reserve(11209, 52234);
        slots.Complete(11209, 52234, first.Slot, NexusWebDownloadResult.Fail("temporary failure"));

        var retry = slots.Reserve(11209, 52234);

        Assert.True(retry.IsOwner);
        Assert.NotSame(first.Slot, retry.Slot);
        Assert.True(slots.TryDeliver(11209, 52234, "https://cdn.example/retry.zip"));
        Assert.Equal("https://cdn.example/retry.zip", (await retry.Slot.Completion.Task).Url);
    }

    [Fact]
    public async Task WaitingForGlobalWindowSlotIsVisibleAndClearedWhenStarted()
    {
        using var gate = new NexusDownloadSlotGate(1);
        Assert.True(await gate.WaitAsync(null, TimeSpan.FromSeconds(1), CancellationToken.None));
        var task = new TaskItem { Id = Guid.NewGuid() };

        // timeout 传 null = 不设上限（下载段闸门的用法：前面的人可能在下大文件）
        var waiting = gate.WaitAsync(task, null, CancellationToken.None);
        Assert.True(gate.IsQueued(task));
        Assert.True(task.Queued);   // v1.3.31：排队口径下放任务行

        gate.Release();
        Assert.True(await waiting);
        Assert.False(gate.IsQueued(task));
        Assert.False(task.Queued);
        gate.Release();
    }

    [Fact]
    public async Task FastPathNeverMarksTaskQueued()
    {
        using var gate = new NexusDownloadSlotGate(1);
        var task = new TaskItem { Id = Guid.NewGuid() };

        Assert.True(await gate.WaitAsync(task, null, CancellationToken.None));

        Assert.False(task.Queued);
        gate.Release();
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("nxm")]
    [InlineData("dep")]
    [InlineData("mod")]
    public void CompleteArchiveIsFoundAcrossDownloadChannelPrefixes(string prefix)
    {
        var dir = Path.Combine(_root, prefix);
        Directory.CreateDirectory(dir);
        var cached = Path.Combine(dir, $"{prefix}-11209-52234.zip");
        using (var zip = ZipFile.Open(cached, ZipArchiveMode.Create))
            zip.CreateEntry("manifest.json");

        Assert.Equal(cached, NexusDownloadCache.FindReusable(dir, 11209, 52234));
    }

    [Fact]
    public void TruncatedArchiveIsNotReused()
    {
        var cached = Path.Combine(_root, "direct-11209-52234.zip");
        using (var zip = ZipFile.Open(cached, ZipArchiveMode.Create))
            zip.CreateEntry("manifest.json");
        var bytes = File.ReadAllBytes(cached);
        File.WriteAllBytes(cached, bytes[..^40]);

        Assert.Null(NexusDownloadCache.FindReusable(_root, 11209, 52234));
    }
}
