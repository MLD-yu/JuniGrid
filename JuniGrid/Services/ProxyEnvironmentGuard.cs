using System.Net.Sockets;

namespace JuniGrid.Services;

/// <summary>
/// 清掉「从父进程继承来的死代理」环境变量。
///
/// <para><b>为什么必须有这一层（2026-11-23 01:41 实机钉死）</b>：
/// 进程如果是从一个带 <c>HTTP_PROXY</c>/<c>HTTPS_PROXY</c> 的终端（WorkBuddy/CI/开发脚本）里拉起来的，
/// 这些变量会被子进程继承，而 .NET 的 <c>HttpClient.DefaultProxy</c> 是
/// <see cref="System.Net.Http.HttpEnvironmentProxy"/> —— <b>环境变量优先于系统代理</b>。
/// 那个终端关掉/沙箱换了以后，端口就没人听了，于是<b>全进程每一个 new HttpClient()</b>
/// （N 网 API、GraphQL、封面图、smapi.io 预查、覆盖包版本检查…）都统一死在
/// 「目标计算机积极拒绝 (127.0.0.1:61292)」。表现极易误判成「N 网挂了」「搜索没结果」，
/// 而 WebView2（界面、翻译）走系统代理，一点事没有 —— 现场看起来就像只有网络层坏了。</para>
///
/// <para>判据刻意收得很窄：<b>只有「回环地址 + 端口连不上」才清</b>。
/// 用户自己开的本地代理（Clash 7897 之类）是活的，原样保留；非回环的代理是用户自己的事，绝不碰。
/// 必须在任何 <c>HttpClient</c> 被创建/首用之前调用 —— <c>DefaultProxy</c> 只解析一次并缓存。</para>
/// </summary>
public static class ProxyEnvironmentGuard
{
    /// <summary>HttpEnvironmentProxy 认的全部变量名（小写优先，再大写）。</summary>
    private static readonly string[] ProxyKeys =
    {
        "http_proxy", "HTTP_PROXY",
        "https_proxy", "HTTPS_PROXY",
        "all_proxy", "ALL_PROXY",
    };

    /// <summary>
    /// 检查并清理。返回一句可直接落日志的结论（不含敏感信息，代理值里可能有账号密码）。
    /// </summary>
    public static string Sanitize()
    {
        var found = new List<(string Key, string Value)>();
        foreach (var key in ProxyKeys)
        {
            var v = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(v)) found.Add((key, v.Trim()));
        }
        if (found.Count == 0) return "无环境代理（按系统代理 / 直连）";

        // 同一个值常常挂在两个变量上（HTTP_PROXY + http_proxy），按值去重后只探一次
        var dead = new List<string>();
        var alive = new List<string>();
        foreach (var value in found.Select(f => f.Value).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var (host, port) = SplitEndpoint(value);
            if (!IsLoopback(host)) continue;          // 非回环 = 用户自己的网络环境，不碰
            (IsPortOpen(host, port) ? alive : dead).Add($"{host}:{port}");
        }

        if (dead.Count == 0)
            return alive.Count > 0
                ? "环境代理存活：" + string.Join(", ", alive)
                : "无回环环境代理（按系统代理 / 直连）";

        foreach (var key in found.Select(f => f.Key).Distinct(StringComparer.OrdinalIgnoreCase))
            ClearWhileDead(key);

        return $"已清除失效的本机代理环境变量：{string.Join(", ", dead)}"
               + "（父进程残留；不清掉会让本进程所有 HttpClient 连接被拒）";
    }

    /// <summary>
    /// 反复清同一个键，直到它为空或变成「活的」。
    /// <para>为什么不是删一次就完：Windows 的环境块<b>允许同名重复项</b>（父进程块里本来就有一份、
    /// 中间再被 shell 追加一份），删掉第一条，第二条就浮上来。实机见过这种形态：删掉死端口后，
    /// 同一个键又露出另一个还活着的端口 —— 那种情况应当保留，只把死的清干净。</para>
    /// </summary>
    private static void ClearWhileDead(string key)
    {
        for (var i = 0; i < 8; i++)
        {
            var v = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrWhiteSpace(v)) return;
            var (host, port) = SplitEndpoint(v.Trim());
            if (!IsLoopback(host) || IsPortOpen(host, port)) return;   // 活的/非回环：留着
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    /// <summary>"http://127.0.0.1:7897" / "127.0.0.1:7897" / "socks5://..." 统一取出 host 与 port。</summary>
    private static (string Host, int Port) SplitEndpoint(string raw)
    {
        var text = raw.Contains("://", StringComparison.Ordinal) ? raw : "http://" + raw;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return (string.Empty, 0);
        return (uri.Host, uri.Port > 0 ? uri.Port : 80);
    }

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host == "127.0.0.1"
        || host == "::1"
        || host == "[::1]";

    /// <summary>300 ms 内连不上就当死代理（本机回环不需要等更久）。</summary>
    private static bool IsPortOpen(string host, int port)
    {
        if (port <= 0) return false;
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(host, port);
            return connect.Wait(300) && client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
