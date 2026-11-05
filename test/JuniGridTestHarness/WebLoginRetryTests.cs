using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>v1.3.43：网页登录成功后自动重跑「因登录受阻而失败」的任务。
/// 这里钉的是筛选条件 —— 多跑一条已完成/暂停的任务去下载，比不自动重跑更糟。</summary>
public class WebLoginRetryTests
{
    private static TaskItem Row(string status, bool flagged, int? nexusId)
        => new() { Status = status, NeedsLoginRetry = flagged, NexusId = nexusId };

    [Fact]
    public void FlaggedFailedTaskWithNexusId_IsPickedForRetry()
    {
        var t = Row("failed", true, 50344);

        Assert.Single(InstallService.PendingLoginRetries(new[] { t }));
    }

    [Fact]
    public void OnlyFlaggedFailedTasksWithId_ArePicked()
    {
        var list = new[]
        {
            Row("failed", true, 50344),    // ✅ 该重跑
            Row("done", true, 50344),      // 已完成：重跑等于白下一遍
            Row("failed", false, 50344),   // 没打标记：不是登录墙导致的
            Row("failed", true, null),     // 没有 NexusId：没法重新发起
            Row("paused", true, 50344),    // 暂停中：用户自己停的，别偷偷跑
        };

        var picked = InstallService.PendingLoginRetries(list);

        Assert.Single(picked);
        Assert.Same(list[0], picked[0]);
    }

    [Fact]
    public void NothingFlagged_ReturnsEmpty()
        => Assert.Empty(InstallService.PendingLoginRetries(Array.Empty<TaskItem>()));
}
