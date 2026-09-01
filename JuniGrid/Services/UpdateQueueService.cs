namespace JuniGrid.Services;

/// <summary>
/// 内置浏览器移除后的占位实现：保留旧接口避免连锁改动，
/// 队列自动更新功能已停用（点「更新」改为打开系统浏览器）。
/// </summary>
public sealed class UpdateQueueService
{
    public int? CurrentModId => null;
    public int Done => 0;
    public int Total => 0;
    public bool IsRunning => false;
    public event Action? OnAdvanced;
    public void Start(IEnumerable<int> modIds) { }
    public void Stop() { }
    public void NotifyFailed(int modId) { }
    public void NotifyInstalled(int modId) { }
    public void Skip() { }
    public void Advance() => OnAdvanced?.Invoke();
}
