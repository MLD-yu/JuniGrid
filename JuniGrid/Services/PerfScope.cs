namespace JuniGrid.Services;

/// <summary>
/// 给 UI 卡顿探针用的「当前同步段」标记。
/// <para>为什么需要：探针原来只会写「前一条 Perf/Download 日志就是嫌疑段」—— 那是靠日志顺序猜的。
/// 实测发现这个猜法经常指错：配置解析、目录迁移、自更新检查在慢的时候根本不写 Perf 日志，
/// 于是每次超时都被归到下载头上，排查方向整个跑偏。标记进线程局部变量后，探针超时那一刻
/// 直接读出「当时正在跑哪一段、跑了多久」，不再需要猜。</para>
/// <para>用 <see cref="ThreadStaticAttribute"/> 而不是全局字段：探针只在 UI 线程跑，
/// 后台线程（解压、下载）压进来的标记不应该被误读成 UI 线程的阻塞源。</para>
/// </summary>
public static class PerfScope
{
    [ThreadStatic] private static string? _name;
    [ThreadStatic] private static long _startTick;

    // 「刚刚结束的那一段」。探针是 DispatcherTimer：UI 线程被占住时它自己也排不上队，
    // 等它跑起来的时候那段活早结束了 —— 只看 Current 会读到 null。所以把最近一次收尾的
    // 段名 + 结束时刻记下来，探针判断这段是否结束在它的检测窗口里（是才是真凶）。
    [ThreadStatic] private static string? _lastName;
    [ThreadStatic] private static long _lastElapsedMs;
    [ThreadStatic] private static long _lastEndTick;

    /// <summary>当前线程正在跑的同步段名；不在任何段里时为 null。</summary>
    public static string? Current => _name;

    /// <summary>当前段已经跑了多久（毫秒）；没有段时为 0。</summary>
    public static long ElapsedMs => _name is null ? 0 : Environment.TickCount64 - _startTick;

    /// <summary>
    /// 标记一段同步重活的开始。<c>using</c> 结束即恢复上一层的段名（支持嵌套：
    /// 比如「启动」里套着「配置载入」，探针会报最里层那个，也就是真正的执行点）。
    /// </summary>
    public static IDisposable Enter(string name)
    {
        var prevName = _name;
        var prevTick = _startTick;
        _name = name;
        _startTick = Environment.TickCount64;
        return new Restorer(prevName, prevTick);
    }

    /// <summary>
    /// 拼一句能直接用的归因说明。<paramref name="gapStartTick"/> 是探针这一拍「本该」触发的时刻，
    /// 用来判断最近收尾的那段是不是就落在本次卡顿窗口内。
    /// </summary>
    public static string Describe(long gapStartTick)
    {
        if (_name is not null)
            return $"嫌疑段={_name}（仍在进行，已 {ElapsedMs}ms）";

        // 段在检测窗口内收尾 ⇒ 就是它把线程占住的
        if (_lastName is not null && _lastEndTick >= gapStartTick)
            return $"嫌疑段={_lastName}（此段耗时 {_lastElapsedMs}ms）";

        return "无可归因段（GC / WebView2 渲染提交 / 输入回调 / 或不在标记范围内）";
    }

    private sealed class Restorer(string? prevName, long prevTick) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            // 只把「够得上卡顿量级」的段留作嫌疑线索，免得几微秒的小段把窗口挤掉
            var elapsed = Environment.TickCount64 - _startTick;
            if (elapsed >= 50)
            {
                _lastName = _name;
                _lastElapsedMs = elapsed;
                _lastEndTick = Environment.TickCount64;
            }
            _name = prevName;
            _startTick = prevTick;
        }
    }
}
