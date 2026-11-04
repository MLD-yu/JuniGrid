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
    public void InstallStageCannotBePaused()
    {
        var task = new TaskItem { Kind = "install" };
        Assert.True(task.CanPause);
        task.Installing = true;
        Assert.False(task.CanPause);
        task.Installing = false;
        Assert.True(task.CanPause);
    }

    [Fact]
    public async Task RemovingPausedTask_CleansItsSegmentState()
    {
        var center = NewCenterThatNeverWritesDisk();
        var root = Path.Combine(Path.GetTempPath(), "jg-task-segments-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, "nxm-11-22.zip");
        var part = destination + ".seg";
        File.WriteAllText(part, "partial");
        File.WriteAllText(part + ".state", "state");
        File.WriteAllText(part + ".state.tmp", "temp");
        var task = center.Start("下载测试包", "install");
        Assert.True(center.SetPartialDownloadPath(task, destination));
        center.Pause(task);

        center.Remove(task);
        var removed = SpinWait.SpinUntil(() => !File.Exists(part) && !File.Exists(part + ".state"),
            TimeSpan.FromSeconds(3));

        Assert.True(removed, "removing a paused row should asynchronously release the segment allocation");
        Assert.False(File.Exists(part + ".state.tmp"));
        Assert.Null(task.PartialDownloadPath);
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    [Fact]
    public async Task RemovingPausedTask_AlsoDeletesSingleStreamPartialZip()
    {
        var center = NewCenterThatNeverWritesDisk();
        var root = Path.Combine(Path.GetTempPath(), "jg-task-single-stream-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, "nxm-11-22.zip");
        File.WriteAllText(destination, "partial zip bytes");
        var task = center.Start("下载测试包", "install");
        Assert.True(center.SetPartialDownloadPath(task, destination));
        center.Pause(task);

        center.Remove(task);

        Assert.True(SpinWait.SpinUntil(() => !File.Exists(destination), TimeSpan.FromSeconds(3)),
            "removing a paused single-stream download should delete its partial destination zip");
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    [Fact]
    public async Task RemovingFailedTask_PreservesItsCompletedArchive()
    {
        var center = NewCenterThatNeverWritesDisk();
        var root = Path.Combine(Path.GetTempPath(), "jg-task-failed-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, "nxm-11-22.zip");
        File.WriteAllText(destination, "complete archive");
        var task = center.Start("下载测试包", "install");
        Assert.True(center.SetPartialDownloadPath(task, destination));
        center.Finish(task, false, "安装失败");

        center.Remove(task);
        await Task.Delay(100);

        Assert.True(File.Exists(destination), "failed install rows may still need the downloaded archive for manual extraction");
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    [Fact]
    public async Task RemovingOneTask_LeavesSharedPathForAnotherTask()
    {
        var center = NewCenterThatNeverWritesDisk();
        var root = Path.Combine(Path.GetTempPath(), "jg-task-shared-segments-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, "direct-11-22.zip");
        var part = destination + ".seg";
        File.WriteAllText(part, "partial");
        File.WriteAllText(part + ".state", "state");
        var first = center.Start("第一次", "install");
        var second = center.Start("第二次", "install");
        Assert.True(center.SetPartialDownloadPath(first, destination));
        Assert.True(center.SetPartialDownloadPath(second, destination));

        center.Remove(first);
        await Task.Delay(100);
        Assert.True(File.Exists(part), "cleanup must not delete data still referenced by another task");

        center.Remove(second);
        Assert.True(SpinWait.SpinUntil(() => !File.Exists(part), TimeSpan.FromSeconds(3)));
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    [Fact]
    public async Task ClearingPausedRows_CleansTheirSegmentArtifacts()
    {
        var center = NewCenterThatNeverWritesDisk();
        var root = Path.Combine(Path.GetTempPath(), "jg-task-clear-segments-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, "dep-11-22.zip");
        var part = destination + ".seg";
        File.WriteAllText(part, "partial");
        File.WriteAllText(part + ".state", "state");
        var task = center.Start("暂停的依赖", "install");
        center.SetPartialDownloadPath(task, destination);
        center.Pause(task);

        center.ClearMatching(t => t.Id == task.Id);

        Assert.True(SpinWait.SpinUntil(() => !File.Exists(part), TimeSpan.FromSeconds(3)));
        Assert.False(File.Exists(part + ".state"));
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    [Fact]
    public void ResumingAPausedDownloadGoesBackToPreparing()
    {
        // 实机 2026-11-05 00:56 抓到的：暂停后 Downloading 留在 true（取消路径没清），
        // 点「继续」后要先重取一次直链（十几秒），这期间行首仍写「下载中 · 6%」，
        // 而那个 6 是 Step("…应用内页面…", 6) 的阶段刻度 —— 假进度就这样复辟了。
        var center = NewCenterThatNeverWritesDisk();
        var task = center.Start("下载并安装 测试", "install");
        task.Downloading = true;
        task.Percent = 42;
        center.Pause(task);
        task.Downloading = true;      // 模拟取消路径没清干净的那一版
        center.ResumeMark(task);      // 点「继续」

        Assert.False(task.Downloading);
        Assert.Equal(TaskRowRules.Stage.Prepare, TaskRowRules.StageOf(task));
        Assert.False(TaskRowRules.Describe(task).ShowPercent);
        Assert.True(TaskRowRules.Describe(task).IndeterminateBar);
    }

    [Fact]
    public void ClearingPausedRows_AlsoDeletesSingleStreamPartialZip()
    {
        // 批量清理走的是 ClearMatching：它先把 Status 改成 "removed" 再判断「是不是暂停行」，
        // 顺序颠倒过一次 —— 判断恒为 false，单流留下的半截 .zip 就永远删不掉
        //（单条移除那条路是对的，所以当时只测单条没暴露）。
        var center = NewCenterThatNeverWritesDisk();
        var root = Path.Combine(Path.GetTempPath(), "jg-task-clear-single-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, "direct-33-44.zip");
        File.WriteAllText(destination, "partial zip bytes");
        var task = center.Start("暂停的单流下载", "install");
        Assert.True(center.SetPartialDownloadPath(task, destination));
        center.Pause(task);

        center.ClearMatching(t => t.Id == task.Id);

        Assert.True(SpinWait.SpinUntil(() => !File.Exists(destination), TimeSpan.FromSeconds(3)),
            "批量清理暂停行时，单流下载的半截包也该一起删掉");
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    [Fact]
    public async Task ClearingFailedRows_PreservesTheirCompletedArchive()
    {
        // 失败行可能还要靠这份归档「手动解压」—— 批量清理不能顺手删掉它
        var center = NewCenterThatNeverWritesDisk();
        var root = Path.Combine(Path.GetTempPath(), "jg-task-clear-failed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, "direct-55-66.zip");
        File.WriteAllText(destination, "complete archive");
        var task = center.Start("失败的安装", "install");
        Assert.True(center.SetPartialDownloadPath(task, destination));
        center.Finish(task, success: false, finalMsg: "安装失败");

        center.ClearMatching(t => t.Id == task.Id);
        await Task.Delay(300);   // 给异步清理一点时间：它不该动这个文件

        Assert.True(File.Exists(destination), "失败行的完整包得留着，手动解压还要用");
        try { Directory.Delete(root, recursive: true); } catch { }
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

    // 语义在 v1.3.24 改过：以前「开始时间」是这一行第一次出现的时刻，重开/继续都不动它。
    // 但界面上那一格写的是「开始 hh:mm:ss」，用户拿它当本次开始时间 —— 13:01 创建、
    // 14:33 重试的行仍显示「开始 13:01:29」，看着就是时钟错了。现在它跟着这一遍走。

    [Fact]
    public void Restart_MovesStartedAt_ToThisAttempt()
    {
        var c = NewCenterThatNeverWritesDisk();
        int id = Id("clock");
        var row = c.StartOrRestart("下载并安装 测试包", "install", id);
        var thatNight = new DateTime(2026, 10, 8, 23, 14, 5);
        row.StartedAt = thatNight;          // 这一行是昨晚第一次点出来的
        row.RunFrom = thatNight;
        c.Finish(row, false, "失败：模拟");

        c.Restart(row);

        Assert.True(row.StartedAt > thatNight, "「开始」该换成这一遍的时刻");
        Assert.True(row.RunFrom > thatNight, "「已运行」该从这次重开算起");
        c.ClearMatching(t => t.NexusId == id);
    }

    [Fact]
    public void Resume_MovesStartedAt_Too()
    {
        var c = NewCenterThatNeverWritesDisk();
        int id = Id("resume");
        var row = c.StartOrRestart("下载并安装 测试包", "install", id);
        var thatNight = new DateTime(2026, 10, 8, 23, 14, 5);
        row.StartedAt = thatNight;
        c.Pause(row);
        c.ResumeMark(row);

        Assert.True(row.StartedAt > thatNight);
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
