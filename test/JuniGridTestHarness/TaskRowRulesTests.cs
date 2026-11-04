using JuniGrid.Services;

namespace JuniGridTestHarness;

/// <summary>任务行怎么显示 —— 「只有下载中有进度」「手动解压只在安装失败时给」这两条规则的钉子。
/// <para>来源（实机 2026-11-04 截图）：任务卡在「正在应用内打开 Nexus 文件页…」，
/// 行上却写着「运行中 · 6% · 已运行 15 秒」—— 那个 6 是安装流程的阶段刻度（取文件信息=2、
/// 取直链=5、开隐藏窗口=6），显示成进度就是骗人。另一头：下载半路失败的行也给了
/// 「手动解压」按钮，可磁盘上只有半截包，解出来必然报损坏。</para>
/// <para>这里钉的是<b>行为</b>而不是签名：给一条任务，问「显示什么阶段 / 要不要百分比 /
/// 进度条怎么画 / 能不能手动解压」。</para></summary>
public class TaskRowRulesTests
{
    private static TaskItem Task(string status = "running", string kind = "install") => new()
    {
        Id = Guid.NewGuid(),
        Title = "下载并安装 测试 Mod",
        Kind = kind,
        Status = status,
        NexusId = 50344,
    };

    // ── 阶段 ──

    [Fact]
    public void FreshlyStartedTaskIsPreparingNotDownloading()
    {
        var t = Task();   // 什么都没置：正在取文件信息/取直链
        var v = TaskRowRules.Describe(t);

        Assert.Equal(TaskRowRules.Stage.Prepare, v.Stage);
        Assert.False(v.ShowPercent);      // ← 截图里那个「6%」就是在这里被挡掉的
        Assert.True(v.IndeterminateBar);
        Assert.Equal(0, v.BarPercent);
    }

    [Fact]
    public void BytesInFlightMeansDownloading()
    {
        var t = Task();
        t.Downloading = true;
        t.Percent = 37;
        var v = TaskRowRules.Describe(t);

        Assert.Equal(TaskRowRules.Stage.Download, v.Stage);
        Assert.True(v.ShowPercent);
        Assert.False(v.IndeterminateBar);
        Assert.Equal(37, v.BarPercent);
    }

    [Fact]
    public void DownloadFinishedThenInstallingLosesPercent()
    {
        // 下载段收尾（Downloading=false）+ 安装段开始（Installing=true）：
        // 百分比那一栏必须消失，哪怕 Percent 已经被安装段推到 100
        var t = Task();
        t.Downloading = false;
        t.Installing = true;
        t.Percent = 100;
        var v = TaskRowRules.Describe(t);

        Assert.Equal(TaskRowRules.Stage.Install, v.Stage);
        Assert.False(v.ShowPercent);
        Assert.True(v.IndeterminateBar);
    }

    [Fact]
    public void QueuedBeatsDownloadingAndHidesPercent()
    {
        // 排队时行上的 Percent 是上一遍的残值（重试复用的行可能是 80%），不能当进度显示
        var t = Task();
        t.Downloading = false;
        t.Queued = true;
        t.Percent = 80;
        var v = TaskRowRules.Describe(t);

        Assert.Equal(TaskRowRules.Stage.Queued, v.Stage);
        Assert.False(v.ShowPercent);
        Assert.True(v.IndeterminateBar);
    }

    [Fact]
    public void ServiceLevelQueueAlsoCounts()
    {
        // 老路径只翻了服务那一侧的排队表（Steam 会话 / N 网隐藏窗口），统一口径要取或
        var t = Task();
        Assert.Equal(TaskRowRules.Stage.Queued, TaskRowRules.Describe(t, queuedElsewhere: true).Stage);
    }

    [Fact]
    public void PausedKeepsItsPositionButStopsAnimating()
    {
        var t = Task("paused");
        t.Percent = 45;
        var v = TaskRowRules.Describe(t);

        Assert.True(v.ShowPercent);          // 停在 45% 是有用信息，不是假数字
        Assert.False(v.IndeterminateBar);    // 但它没在动，别画来回滑的条
        Assert.Equal(45, v.BarPercent);
    }

    [Fact]
    public void FinishedRowIsFullAndFailedRowKeepsWhereItStopped()
    {
        var done = Task("done");
        Assert.Equal(100, TaskRowRules.Describe(done).BarPercent);

        var failed = Task("failed");
        failed.Percent = 62;
        var v = TaskRowRules.Describe(failed);
        Assert.Equal(62, v.BarPercent);
        Assert.False(v.IndeterminateBar);
    }

    [Fact]
    public void OutOfRangePercentNeverProducesABrokenBar()
    {
        var t = Task();
        t.Downloading = true;
        t.Percent = 173;
        Assert.Equal(100, TaskRowRules.Describe(t).BarPercent);

        t.Percent = double.NaN;
        Assert.Equal(0, TaskRowRules.Describe(t).BarPercent);
    }

    // ── 手动解压 ──

    private static string? NoCache(int _) => null;
    private static string? CacheHasOne(int _) => @"D:\cache\direct-50344-189733.zip";

    [Fact]
    public void InstallFailureWithCompleteArchiveCanBeExtractedByHand()
    {
        var t = Task("failed");
        t.ArchivePath = @"D:\cache\direct-50344-189733.zip";

        Assert.True(TaskRowRules.CanManualExtract(t, _ => true, NoCache));
    }

    [Fact]
    public void DownloadFailureHasNothingToExtract()
    {
        // 下载段失败：ArchivePath 还是 null（它只在完整性校验通过后才写），缓存里也没有 → 绝不能给按钮
        var t = Task("failed");
        Assert.Null(t.ArchivePath);

        Assert.False(TaskRowRules.CanManualExtract(t, _ => true, NoCache));
    }

    [Fact]
    public void ArchivePointingAtADeletedFileIsNotOffered()
    {
        var t = Task("failed");
        t.ArchivePath = @"D:\cache\gone.zip";

        Assert.False(TaskRowRules.CanManualExtract(t, _ => false, NoCache));
    }

    [Fact]
    public void OldRowWithoutArchivePathStillOffersExtractionWhenTheCacheHasThePack()
    {
        // 2026-11-04 实测那条：任务行生在没有 ArchivePath 字段的年代（字段为 null），
        // 但缓存里那份 94 MB 的包好端端在 —— 只认字段会把按钮藏掉，用户就问「手动解压没了？」
        var t = Task("failed");
        Assert.Null(t.ArchivePath);

        Assert.True(TaskRowRules.CanManualExtract(t, _ => true, CacheHasOne));
    }

    [Fact]
    public void DeletedArchiveFallsBackToTheCacheCopy()
    {
        // ArchivePath 记的那份被清了，但缓存里另一份（别的通道下的）还在 → 仍然可以解压
        var t = Task("failed");
        t.ArchivePath = @"D:\cache\gone.zip";

        Assert.True(TaskRowRules.CanManualExtract(t, _ => false, CacheHasOne));
    }

    [Fact]
    public void OurOwnArchiveWinsOverTheCacheLookup()
    {
        // 任务行的包在，就不该去翻缓存目录（省一次磁盘枚举）
        var t = Task("failed");
        t.ArchivePath = @"D:\cache\a.zip";
        var cacheQueried = false;

        Assert.True(TaskRowRules.CanManualExtract(t, _ => true, _ => { cacheQueried = true; return null; }));
        Assert.False(cacheQueried);
    }

    [Fact]
    public void OnlyFailedNexusInstallsOfferExtraction()
    {
        foreach (var status in new[] { "running", "paused", "done" })
        {
            var t = Task(status);
            t.ArchivePath = @"D:\cache\a.zip";
            Assert.False(TaskRowRules.CanManualExtract(t, _ => true, CacheHasOne));
        }

        var noNexus = Task("failed");
        noNexus.NexusId = null;
        noNexus.ArchivePath = @"D:\cache\a.zip";
        Assert.False(TaskRowRules.CanManualExtract(noNexus, _ => true, CacheHasOne));

        var extractTask = Task("failed", kind: "extract");
        extractTask.ArchivePath = @"D:\cache\a.zip";
        Assert.False(TaskRowRules.CanManualExtract(extractTask, _ => true, CacheHasOne));

        var update = Task("failed", kind: "update");
        update.ArchivePath = @"D:\cache\a.zip";
        Assert.True(TaskRowRules.CanManualExtract(update, _ => true, CacheHasOne));   // 更新装失败同样是「安装失败」
    }
}
