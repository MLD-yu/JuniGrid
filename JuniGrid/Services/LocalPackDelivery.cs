namespace JuniGrid.Services;

/// <summary>
/// WPF 宿主层（<see cref="MainWindow"/> 的 Drop 处理）与 Blazor 层
/// （<c>Mods.razor</c> / <c>MainLayout.razor</c>）之间唯一的交接点。
/// <para>WPF 拿得到真实磁盘路径（<c>DataFormats.FileDrop</c>），但拿不到 Blazor 的渲染
/// 上下文；Blazor 拿不到路径，却知道怎么开任务、怎么弹 toast、怎么刷新列表。
/// 这一层只负责"把路径交出去"，不装、不判、不提示。</para>
/// <para>为什么是<b>队列</b>而不是一个单槽：用户停在首页时连拖两批，单槽会让第二批
/// 覆盖第一批，用户以为两批都装了、实际只装了一半。</para>
/// </summary>
public sealed class LocalPackDelivery
{
    private readonly Queue<IReadOnlyList<string>> _queue = new();

    /// <summary>有新的一批包送达。订阅方（MainLayout / Mods.razor）负责导航与安装。</summary>
    public event Action? Delivered;

    /// <summary>WPF 线程调用：入队并通知。一批 = 用户一次拖放/一次文件对话框选择。</summary>
    public void Deliver(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        _queue.Enqueue(paths);
        Delivered?.Invoke();
    }

    /// <summary>取走最早的一批（取一次就没了）。没有则返回 null。</summary>
    public IReadOnlyList<string>? Take()
        => _queue.Count > 0 ? _queue.Dequeue() : null;
}
