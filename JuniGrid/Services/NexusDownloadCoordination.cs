using System.Collections.Concurrent;
using System.IO;

namespace JuniGrid.Services;

internal sealed class NexusFetchSlotRegistry
{
    private readonly ConcurrentDictionary<string, NexusFetchSlot> _active = new();

    public NexusFetchReservation Reserve(int modId, long fileId)
    {
        var key = Key(modId, fileId);
        while (true)
        {
            var slot = new NexusFetchSlot(modId, fileId);
            var actual = _active.GetOrAdd(key, slot);
            if (ReferenceEquals(actual, slot)) return new(slot, IsOwner: true);
            // A failed attempt may be completed just before its owner removes it. Do not
            // let a user retry inherit that completed failure as if it were an active task.
            if (actual.Finished)
            {
                Remove(modId, fileId, actual);
                continue;
            }
            return new(actual, IsOwner: false);
        }
    }

    public bool HasPending(int modId, long fileId)
        => _active.TryGetValue(Key(modId, fileId), out var slot) && !slot.Finished;

    public bool TryDeliver(int modId, long fileId, string? directUrl,
        IReadOnlyList<string>? mirrors = null)
        => _active.TryGetValue(Key(modId, fileId), out var slot) && slot.Deliver(directUrl, mirrors);

    public void Complete(int modId, long fileId, NexusFetchSlot slot, NexusWebDownloadResult result)
    {
        slot.Complete(result);
        Remove(modId, fileId, slot);
    }

    public void Remove(int modId, long fileId, NexusFetchSlot slot)
    {
        var pair = new KeyValuePair<string, NexusFetchSlot>(Key(modId, fileId), slot);
        ((ICollection<KeyValuePair<string, NexusFetchSlot>>)_active).Remove(pair);
    }

    private static string Key(int modId, long fileId) => modId + ":" + fileId;
}

internal sealed record NexusFetchReservation(NexusFetchSlot Slot, bool IsOwner);

internal sealed class NexusFetchSlot(int modId, long fileId)
{
    public int ModId { get; } = modId;
    public long FileId { get; } = fileId;
    public TaskCompletionSource<string?> DirectUrl { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<NexusWebDownloadResult> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IReadOnlyList<string>? Mirrors { get; private set; }
    public bool Finished => Completion.Task.IsCompleted;
    public bool Deliver(string? url, IReadOnlyList<string>? mirrors)
    {
        if (string.IsNullOrWhiteSpace(url) || Finished) return false;
        if (!DirectUrl.TrySetResult(url)) return false;
        Mirrors = mirrors;
        AppLog.Info("NexusWeb", $"采用 nxm 回流直链 mod={ModId} file={FileId}");
        Completion.TrySetResult(NexusWebDownloadResult.Ok(url, mirrors));
        return true;
    }

    public void Complete(NexusWebDownloadResult result)
    {
        if (result.Url is not null) DirectUrl.TrySetResult(result.Url);
        else DirectUrl.TrySetResult(null);
        Completion.TrySetResult(result);
    }
}

internal sealed class NexusDownloadSlotGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore;
    private readonly ConcurrentDictionary<Guid, int> _queued = new();

    public NexusDownloadSlotGate(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _semaphore = new SemaphoreSlim(capacity, capacity);
    }

    public bool IsQueued(TaskItem task) => _queued.ContainsKey(task.Id);

    /// <summary>等一个名额。<paramref name="timeout"/> 传 null = 不设上限（排队可能排在
    /// 「前面有人在下大文件」后面，不像隐藏窗口那样适合 150 秒兜底）。</summary>
    public async Task<bool> WaitAsync(TaskItem? task, TimeSpan? timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_semaphore.Wait(0)) return true;
        if (task is not null)
        {
            _queued.AddOrUpdate(task.Id, 1, static (_, count) => count + 1);
            // v1.3.31：排队口径下放任务行 —— TaskCenter 的 RowState/总数据此显示「等待中」，
            // 不再让排队任务冒充「运行中」拖累 TotalPercent/RunningCount。
            task.Queued = true;
        }
        try
        {
            if (timeout is null)
            {
                await _semaphore.WaitAsync(ct).ConfigureAwait(false);
                return true;
            }
            return await _semaphore.WaitAsync(timeout.Value, ct).ConfigureAwait(false);
        }
        finally
        {
            if (task is not null)
            {
                task.Queued = false;
                while (_queued.TryGetValue(task.Id, out var count))
                {
                    if (count <= 1)
                    {
                        if (((ICollection<KeyValuePair<Guid, int>>)_queued).Remove(new(task.Id, count))) break;
                    }
                    else if (_queued.TryUpdate(task.Id, count - 1, count)) break;
                }
            }
        }
    }

    public void Release() => _semaphore.Release();
    public void Dispose() => _semaphore.Dispose();
}

internal static class NexusDownloadCache
{
    /// <summary>四条落盘通道：direct（一键直装）/ nxm（网页回流）/ dep（依赖）/ mod（更新流水线）。
    /// 认的是【文件身份】不是通道 —— 任一通道下过的完整包都该被其余通道复用，
    /// 否则「重试又重下一遍」。</summary>
    private static readonly string[] Prefixes = ["direct", "nxm", "dep", "mod"];

    public static string? FindReusable(string downloadsDir, int modId, long fileId)
    {
        foreach (var prefix in Prefixes)
        {
            var path = Path.Combine(downloadsDir, $"{prefix}-{modId}-{fileId}.zip");
            if (ModService.ArchiveReadable(path)) return path;
        }
        return null;
    }

    /// <summary>只知道 modId 时的兜底查找（老任务行没记 <c>ArchivePath</c>，或记的那个已被清掉）：
    /// 下载缓存里这个 mod 的<b>最新一份能打开的</b>包。
    /// <para>不知道 fileId 只能按文件名里的 modId 通配，所以必须逐个验真 —— 暂停/取消留下的
    /// 半截包同样满足「文件存在且不为空」，不验的话会把「手动解压」引到一个解不开的文件上。</para>
    /// <para>v1.3.34：从 Tasks.razor 挪进来。原先是「点击时才找」，现在列表渲染也要知道
    /// 「这颗按钮该不该出现」，两边必须是同一套判据。</para></summary>
    public static string? NewestForMod(int modId)
    {
        try
        {
            var dir = StoragePaths.DownloadsDir;
            if (!Directory.Exists(dir)) return null;
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles($"*-{modId}-*.zip")
                         .OrderByDescending(f => f.LastWriteTimeUtc))
            {
                if (ModService.ArchiveReadable(f.FullName)) return f.FullName;
            }
            return null;
        }
        catch { return null; }
    }
}
