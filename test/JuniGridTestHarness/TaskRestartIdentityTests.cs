using System.Reflection;
using JuniGrid.Services;

namespace JuniGridTestHarness;

// ══════════════════════════════════════════════════════════════════════════
// 2026-10-09 他实机点出来的三件事，全在任务中心这一层：
//   ① 任务卡上没有开始时间 —— 任务长存（重启也留着），分不清哪条是哪一回点的；
//   ② 下载失败后重新下载，不在原来那一行上重开，而是又排一条，任务管理越用越长；
//   ③ 反复点开始/暂停，「下载详情」里那几行一直往上加。
// 断言一律按测试自己用的 NexusId 圈定范围，不数 Items 总数 —— 构造函数会把他
// %APPDATA% 里真实的 tasks.json 读进来，总数不是我们能钉的东西。
// ══════════════════════════════════════════════════════════════════════════

public class TaskRestartIdentityTests
{
    /// <summary>TaskCenterService 落盘写的是 StoragePaths.AppDataDir\tasks.json，
    /// TestSandbox 设的 JUNIGRID_CONFIG_DIR 管不到那里 —— 不掐掉防抖定时器就是往他
    /// 真任务表里写玩具行。（与 WorkerCancellationAndOverlayTests 同款，那边是私有的）</summary>
    private static TaskCenterService NewCenterThatNeverWritesDisk()
    {
        var c = new TaskCenterService();
        const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(TaskCenterService).GetField("_saveTimer", F)?.SetValue(c,
            new Timer(_ => { }, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite));
        (typeof(TaskCenterService).GetField("_reapTimer", F)?.GetValue(c) as Timer)?.Dispose();
        return c;
    }

    private static List<TaskItem> RowsOf(TaskCenterService c, int id)
        => c.Snapshot().Where(t => t.NexusId == id).ToList();

    // 显式编号：String.GetHashCode 每进程随机，靠它派生号会有两个用例撞同一个 mod 号的风险
    private static int Id(string which) => which switch
    {
        "fail" => 99_900_001, "paused" => 99_900_002, "done" => 99_900_003,
        "kind" => 99_900_004, "clock" => 99_900_005, "resume" => 99_900_006,
        _ => throw new ArgumentOutOfRangeException(nameof(which)),
    };

    // ─────────────── ② 重新下载不许多排一条 ───────────────

    [Fact]
    public void FailedRow_IsReopenedInPlace_NotAppended()
    {
        var c = NewCenterThatNeverWritesDisk();
        int id = Id("fail");
        var first = c.StartOrRestart("下载并安装 测试包", "install", id);
        c.Report(first, "正在获取下载地址…", 5);
        c.Finish(first, false, "安装失败：模拟");

        var again = c.StartOrRestart("下载并安装 测试包", "install", id);

        Assert.Same(first, again);                                  // 就是原来那一行
        Assert.Single(RowsOf(c, id));                               // 没有多排一条
        Assert.Equal("running", again.Status);
        Assert.Equal(0, again.Percent);
        Assert.Empty(again.Log);                                    // 上一遍的步骤让位给这一遍
        c.ClearMatching(t => t.NexusId == id);
    }

    [Fact]
    public void PausedRow_IsReopenedInPlace_Too()
    {
        var c = NewCenterThatNeverWritesDisk();
        int id = Id("paused");
        var row = c.StartOrRestart("下载并安装 测试包", "install", id);
        c.Pause(row);

        Assert.Same(row, c.StartOrRestart("下载并安装 测试包", "install", id));
        Assert.Equal("running", row.Status);
        Assert.Single(RowsOf(c, id));
        c.ClearMatching(t => t.NexusId == id);
    }

    [Fact]
    public void DoneRow_IsHistory_ASecondRunGetsItsOwnRow()
    {
        var c = NewCenterThatNeverWritesDisk();
        int id = Id("done");
        var row = c.StartOrRestart("下载并安装 测试包", "install", id);
        c.Finish(row, true, "安装完成：测试包");

        var again = c.StartOrRestart("下载并安装 测试包", "install", id);

        Assert.NotSame(row, again);
        Assert.Equal(2, RowsOf(c, id).Count);                       // 成功那一行留着
        Assert.Equal("done", row.Status);
        c.ClearMatching(t => t.NexusId == id);
    }

    [Fact]
    public void RowsWithoutAnId_NeverMerge()
    {
        var c = NewCenterThatNeverWritesDisk();
        var a = c.StartOrRestart("清理：测试缓存", "cleanup", null);
        c.Finish(a, false, "失败：模拟");
        var b = c.StartOrRestart("清理：测试缓存", "cleanup", null);

        Assert.NotSame(a, b);   // 没有号就没有「同一件事」可谈，照旧一次一条
        c.ClearMatching(t => t.Kind == "cleanup" && t.Title == "清理：测试缓存");
    }

    [Fact]
    public void OtherKind_WithSameId_StillGetsItsOwnRow()
    {
        var c = NewCenterThatNeverWritesDisk();
        int id = Id("kind");
        var install = c.StartOrRestart("下载并安装 测试包", "install", id);
        c.Finish(install, false, "失败：模拟");

        Assert.NotSame(install, c.StartOrRestart("别的通道 测试包", "download", id));
        c.ClearMatching(t => t.NexusId == id);
    }

    // ─────────────── ① 开始时间 ───────────────

    [Fact]
    public void Restart_KeepsStartedAt_MovesTheRunClock()
    {
        var c = NewCenterThatNeverWritesDisk();
        int id = Id("clock");
        var row = c.StartOrRestart("下载并安装 测试包", "install", id);
        var thatNight = new DateTime(2026, 10, 8, 23, 14, 5);
        row.StartedAt = thatNight;          // 这一行是昨晚第一次点出来的
        row.RunFrom = thatNight;
        c.Finish(row, false, "失败：模拟");

        c.Restart(row);

        Assert.Equal(thatNight, row.StartedAt);                     // 「开始时间」不许被重开改写
        Assert.True(row.RunFrom > thatNight, "「已运行」该从这次重开算起");
        c.ClearMatching(t => t.NexusId == id);
    }

    [Fact]
    public void Resume_KeepsStartedAt_Too()
    {
        var c = NewCenterThatNeverWritesDisk();
        int id = Id("resume");
        var row = c.StartOrRestart("下载并安装 测试包", "install", id);
        var thatNight = new DateTime(2026, 10, 8, 23, 14, 5);
        row.StartedAt = thatNight;
        c.Pause(row);
        c.ResumeMark(row);

        Assert.Equal(thatNight, row.StartedAt);
        Assert.True(row.RunFrom > thatNight);
        c.ClearMatching(t => t.NexusId == id);
    }

    // ─────────────── ③ 详情不许越点越长 ───────────────

    [Fact]
    public void RepeatedPauseResume_KeepsOneControlLine()
    {
        var c = NewCenterThatNeverWritesDisk();
        var row = c.Start("下载并安装 测试包", "install");
        c.Report(row, "正在下载…", 10);

        for (int i = 0; i < 6; i++) { c.Pause(row); c.ResumeMark(row); }

        var lines = c.CopyLog(row).Select(TaskStrip).ToList();
        Assert.Equal(1, lines.Count(l => l == LocService.Tr("已暂停") || l == LocService.Tr("继续中…")));
        Assert.Contains("正在下载…", lines[^2]);        // 中间那一步照常留着
        c.Remove(row);
    }

    [Fact]
    public void StepBetweenPauses_IsNotSwallowed()
    {
        var c = NewCenterThatNeverWritesDisk();
        var row = c.Start("下载并安装 测试包", "install");
        c.Pause(row);
        c.ResumeMark(row);                                     // 真·暂停两次之间必须先继续
        c.Report(row, "正在安装到 Mods…", 95);
        c.Pause(row);

        // 中间隔了一条真步骤，第二次暂停就不是"同一条控制行的新值"，得留着
        Assert.Equal(3, c.CopyLog(row).Count);
        c.Remove(row);
    }

    [Fact]
    public void ResumeAfterFinish_DoesNotEatTheFailureLine()
    {
        var c = NewCenterThatNeverWritesDisk();
        var row = c.Start("下载并安装 测试包", "install");
        c.Pause(row);                                    // 上一条是控制行
        c.Finish(row, false, "安装失败：模拟");           // 收尾行不是控制行
        c.ResumeMark(row);

        Assert.Contains(c.CopyLog(row), l => l.Contains("安装失败：模拟"));
        c.Remove(row);
    }

    [Fact]
    public void Retry_ReopensTheSameRow_WithAFreshTimeline()
    {
        var c = NewCenterThatNeverWritesDisk();
        var row = c.Start("下载并安装 测试包", "install");
        c.Report(row, "正在获取文件信息…", 2);
        c.Report(row, "正在下载…", 8);
        c.Finish(row, false, "获取下载地址失败：403");
        int ran = 0;
        c.RegisterResume(row, ct => { ran++; return Task.CompletedTask; });

        Assert.True(c.Retry(row));
        Assert.Equal("running", row.Status);
        Assert.Single(c.CopyLog(row));                   // 只有「正在重试…」，不是旧的两步加新的一步
        Assert.DoesNotContain(c.CopyLog(row), l => l.Contains("正在获取文件信息…"));
        c.Remove(row);
    }

    /// <summary>去掉行首 "[HH:mm:ss] " 时间戳拿正文。</summary>
    private static string TaskStrip(string line)
    {
        var i = line.IndexOf("] ", StringComparison.Ordinal);
        return i >= 0 && line.StartsWith("[") ? line[(i + 2)..] : line;
    }
}
