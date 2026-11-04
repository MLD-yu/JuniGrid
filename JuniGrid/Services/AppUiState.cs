namespace JuniGrid.Services;

/// <summary>
/// 主窗口当前是否可见（未最小化）。
/// <para>用途：主窗最小化时 <c>MainWindow</c> 会把 BlazorWebView 设为 <c>Collapsed</c> 并让
/// WebView2 进入低内存模式。此时再调 <c>StateHasChanged</c>，渲染批次发不进不可见的
/// WebView2，调用会一直挂着 —— 而且它挂在 UI 线程上。</para>
/// <para>实机 2026-11-04 14:26:46：装 Pelipper Town 时点了最小化，下一次走秒重绘
/// 的 <c>StateHasChanged</c> 卡了 <b>149,963ms</b>（直到 14:29:17 还原窗口），
/// 期间整个界面无响应，解压也被拖到 172 秒。所以「窗口不可见」期间的刷新一律跳过，
/// 恢复可见时由 <see cref="VisibilityChanged"/> 补一次。</para>
/// </summary>
public static class AppUiState
{
    private static volatile bool _windowVisible = true;

    /// <summary>窗口可见（未最小化）。默认 true —— 首帧渲染时窗口必然是显示的。</summary>
    public static bool WindowVisible => _windowVisible;

    /// <summary>可见性发生变化（最小化 ↔ 还原）。恢复可见时订阅方应补一次重绘。</summary>
    public static event Action? VisibilityChanged;

    public static void SetWindowVisible(bool visible)
    {
        if (_windowVisible == visible) return;
        _windowVisible = visible;
        try { VisibilityChanged?.Invoke(); } catch { /* 订阅方自己的异常不该影响窗口状态 */ }
    }
}
