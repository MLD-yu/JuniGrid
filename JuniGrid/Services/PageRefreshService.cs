namespace JuniGrid.Services;

/// <summary>
/// 页内刷新信号：顶栏点击「当前已在页面」的导航图标时发出（mods / nexus），
/// 对应页面订阅并重新加载数据（重新扫描 / 重新拉取 / 重新检查更新）。
/// 从其它页切换过来时走 NavLink 正常导航、靠组件重建自然加载，不发信号、不浪费请求。
/// </summary>
public sealed class PageRefreshService
{
    public event Action<string>? OnRefresh;

    /// <summary>pageKey：触发刷新的页面标识（"mods" / "nexus"）。</summary>
    public void Request(string pageKey) => OnRefresh?.Invoke(pageKey);
}
