namespace JuniGrid.Services;

/// <summary>
/// 启动期全屏弹窗的排队门：<b>新手引导优先</b>，「第 N 次启动」的赞助提示等引导关闭后再上场。
/// <para><b>为什么不能各自 sleep 一个固定时长：</b>引导在首屏后 900ms 才出现、用户可能看很久，
/// 而赞助提示在 200ms 就要判定。只靠 DOM 探测躲不开「引导还没出现」的那 900ms 空窗，
/// 谁先谁后会被时序随机决定。改成引导初始化时<b>声明</b>「本次会弹」、关闭或销毁时
/// <b>宣布结束</b>，赞助那边 await 这一个 Task，顺序就是确定的。</para>
/// <para>引导本次不弹（配置里已看过）时 Task 立刻完成，赞助提示不受任何额外延迟。</para>
/// </summary>
public static class StartupOverlayGate
{
    private static TaskCompletionSource<bool>? _welcome;

    /// <summary>引导组件初始化时调用一次。<paramref name="willShow"/> 为 false 表示这次不弹，
    /// 等待方立刻放行。</summary>
    public static void DeclareWelcome(bool willShow)
    {
        // MainLayout 重建会让引导重新初始化。旧等待方不能悬在那里，先放行；
        // 新一次引导无论如何都已写过「看过」记档、不会再显示，放行是安全的。
        _welcome?.TrySetResult(true);
        _welcome = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!willShow) _welcome.TrySetResult(true);
    }

    /// <summary>等引导结束。从没声明过（组件没挂载）就当作已经结束，不做任何等待。</summary>
    public static Task WaitWelcomeAsync() => _welcome?.Task ?? Task.CompletedTask;

    /// <summary>引导关闭或组件销毁时调用，重复调用无害。</summary>
    public static void WelcomeFinished() => _welcome?.TrySetResult(true);
}
