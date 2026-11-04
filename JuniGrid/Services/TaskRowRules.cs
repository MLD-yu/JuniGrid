namespace JuniGrid.Services;

/// <summary>任务行「怎么画」的规则集中在这里 —— 具体渲染在 Tasks.razor，判断在这儿。
/// <para>抽出来的原因：这套规则以前散在 Razor 的 markup 里（<c>@(t.Percent)%</c> 无条件
/// 写在行上、解压按钮只判 <c>Status=="failed" &amp;&amp; Kind=="install"</c>），既没法测，
/// 也没法一眼看出整行到底有几段状态。2026-11-04 实机截图暴露了两个具体毛病：
/// ①任务卡在「正在应用内打开 Nexus 文件页…」时行上写着「6%」——那个 6 是安装流程里
/// 「走到第几步」的刻度，不是下载进度，看着却像已经下了一截；
/// ②下载半路失败的行也给了「手动解压」按钮 —— 磁盘上连完整包都没有，点开必然报损坏。</para></summary>
public static class TaskRowRules
{
    /// <summary>运行中任务的三个真实阶段 + 排队。名称只用于判断，文案在 Tasks.razor 里翻。</summary>
    public enum Stage
    {
        /// <summary>非运行中（暂停/完成/失败）—— 阶段由 Status 直接决定，不参与判定。</summary>
        Idle,
        Queued,
        Prepare,
        Download,
        Install,
    }

    /// <summary>一行任务该怎么呈现。渲染层只照单执行，不再自己拼判断。</summary>
    public readonly record struct View(Stage Stage, bool ShowPercent, bool IndeterminateBar, double BarPercent);

    /// <param name="queuedElsewhere">服务级排队兜底（Steam 会话 / N 网隐藏窗口自带的排队表）。
    /// <see cref="TaskItem.Queued"/> 是统一口径，但老路径可能只翻了服务那一侧，两边取或。</param>
    public static View Describe(TaskItem t, bool queuedElsewhere = false)
    {
        var stage = StageOf(t, queuedElsewhere);

        // 百分比只在下载段出现：那一段才有「还剩多少字节」这回事。
        // 暂停时保留（停在哪儿是有用的信息，也不是假数字），准备/安装/排队一律不显示。
        var showPercent = stage == Stage.Download
                          || (t.Status == "paused" && t.Percent > 0);

        // 拿不出百分比的活着的行 → 画不定条（在做事，但没法量化），而不是一条停在
        // 假数字上的实心条。暂停的行不画动画（它没在动），给一条静态空槽。
        var indeterminate = t.Status == "running" && stage is Stage.Prepare or Stage.Queued or Stage.Install;

        var bar = t.Status switch
        {
            "done" => 100d,
            "failed" => Clamp(t.Percent),
            "paused" => Clamp(t.Percent),
            _ => stage == Stage.Download ? Clamp(t.Percent) : 0d,
        };

        return new View(stage, showPercent, indeterminate, bar);
    }

    public static Stage StageOf(TaskItem t, bool queuedElsewhere = false)
    {
        if (t.Status != "running") return Stage.Idle;
        if (t.Installing) return Stage.Install;          // 装的那一段没有字节进度
        if (t.Queued || queuedElsewhere) return Stage.Queued;
        if (t.Downloading) return Stage.Download;
        return Stage.Prepare;                            // 取文件信息 / 取直链 / 开隐藏窗口
    }

    /// <summary>「手动解压」只在<b>安装失败</b>时给：包已经完整下到本地、只是装不进去，
    /// 手动解出来放进 Mods 才有意义。下载段失败（半截包 / 根本没下完）不给 ——
    /// 磁盘上连一个完整的整合包都没有，那颗按钮按下去只能得到「压缩包损坏」。
    /// <para>「完整包在哪儿」有两条来源，缺一不可：</para>
    /// <list type="number">
    /// <item>任务行上的 <see cref="TaskItem.ArchivePath"/> —— 由下载段在完整性校验通过之后写入
    /// （并落盘），最新的一批任务都有；</item>
    /// <item><paramref name="findCompleteArchive"/> —— 老任务行没有那个字段（或者它指向的文件
    /// 被清掉了），去下载缓存里按 modId 找一份<b>能打开的</b>包。</item>
    /// </list>
    /// <para>只认第一条会误伤：2026-11-04 那条 failed 的「下载并安装 Pelipper Town」ArchivePath 是
    /// 空的（它生在没有该字段的版本里），可缓存里那份 94 MB 的包好端端在，按钮却被藏掉了。</para></summary>
    /// <param name="fileExists">文件存在性探测。测试里传桩，正常运行传 <c>File.Exists</c>。</param>
    /// <param name="findCompleteArchive">按 modId 在下载缓存里找一份可打开的完整包，找不到回 null。</param>
    public static bool CanManualExtract(
        TaskItem t, Func<string, bool> fileExists, Func<int, string?> findCompleteArchive)
    {
        if (t.Status != "failed") return false;
        if (t.Kind is not ("install" or "update")) return false;
        if (t.NexusId is not int modId || modId <= 0) return false;

        if (!string.IsNullOrWhiteSpace(t.ArchivePath) && fileExists(t.ArchivePath)) return true;
        return findCompleteArchive(modId) is not null;
    }

    private static double Clamp(double v) => double.IsNaN(v) ? 0 : Math.Clamp(v, 0, 100);
}
