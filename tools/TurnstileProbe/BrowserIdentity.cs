using System.Text.Json;

namespace TurnstileProbe;

// ↓ 从 JuniGrid/Services/NexusWebDownloadService.cs 原样摘出（含 2026-11-11 三品牌修复）
internal static class BrowserIdentity
{
    /// <summary>压掉「我是被程序嵌进来的」的那些 JS 痕迹。
    /// <para>只改 UA 不够 —— 2026-11-11 02:20 实机：UA 已修成普通 Edge、CF cookie 也清了，
    /// 托管挑战照样一分钟不放行（<c>act=False</c>，连可点的 Turnstile 都不给）。WebView2 还留着一个
    /// 普通浏览器没有的全局对象 <c>window.chrome.webview</c>，一段 JS 就能问出来；
    /// 再加上 <c>navigator.webdriver</c>（自动化标记）。这里把它们在页面脚本执行之前抹平。</para>
    /// <para>只定义 getter 返回 undefined、不 delete：WebView2 自己挂的属性未必可删，
    /// 但可写 getter 足以让所有探测读不到东西。不改任何其它行为（postMessage 仍由宿主独占）。</para></summary>
    internal const string HideEmbeddedScript = """
(() => {
  const hide = (obj, key) => {
    if (!obj) return;
    try {
      Object.defineProperty(obj, key, { get: () => undefined, configurable: true, enumerable: false });
    } catch (e) { }
  };
  try {
    hide(window.chrome, 'webview');
    hide(window.chrome, 'webviewBridge');
    hide(Navigator.prototype, 'webdriver');
    hide(navigator, 'webdriver');
  } catch (e) { }
})();
""";

    /// <summary>Windows 构建号 → Chrome 口径的 platformVersion。
    /// <para>只在读不到内核自报值时才用：Win11 报 15.0.0、Win10 报 10.0.0，与 Chrome 一致。</para></summary>
    internal static string GuessPlatformVersion()
        => Environment.OSVersion.Version.Build >= 22000 ? "15.0.0" : "10.0.0";

    /// <summary>读身份脚本：只读不改。高熵字段拿不到就退化成低熵字段，
    /// 由 <see cref="Build"/> 兜底。</summary>
    internal const string ProbeScript = """
        (async () => {
          try {
            var d = navigator.userAgentData;
            var out = {
              ua: navigator.userAgent || '',
              langs: Array.prototype.slice.call(navigator.languages || []),
            };
            if (!d) return JSON.stringify(out);
            out.brands = d.brands || [];
            out.mobile = !!d.mobile;
            out.platform = d.platform || '';
            try {
              out.h = await d.getHighEntropyValues(
                ['architecture','bitness','model','platformVersion','uaFullVersion','fullVersionList','wow64']);
            } catch (e) { }
            return JSON.stringify(out);
          } catch (e) { return ''; }
        })()
        """;

    /// <summary>
    /// 由内核自报的身份拼出 <c>setUserAgentOverride</c> 的参数。
    /// <para>纯函数（不碰界面），白盒测试直接喂 JSON 断言：brands 里不能再有 WebView2、
    /// 必须有一个 Microsoft Edge，UA 字符串里不能残留 WebView2 字样。</para>
    /// </summary>
    internal static string? Build(string? probeJson, string? engineVersion,
        string fallbackPlatformVersion)
    {
        var brands = new List<Dictionary<string, string>>();
        var fullBrands = new List<Dictionary<string, string>>();
        var langs = new List<string>();
        var ua = string.Empty;
        var platform = "Windows";
        var platformVersion = fallbackPlatformVersion;
        var fullVersion = string.Empty;
        var arch = "x86";
        var bitness = "64";
        var model = string.Empty;
        var mobile = false;
        var wow64 = false;

        if (!string.IsNullOrWhiteSpace(probeJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(probeJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("ua", out var u) && u.ValueKind == JsonValueKind.String)
                    ua = u.GetString() ?? "";
                if (root.TryGetProperty("langs", out var ls) && ls.ValueKind == JsonValueKind.Array)
                    foreach (var item in ls.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } one)
                            langs.Add(one);
                if (root.TryGetProperty("platform", out var p) && p.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(p.GetString()))
                    platform = p.GetString()!;
                if (root.TryGetProperty("mobile", out var m)
                    && m.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    mobile = m.GetBoolean();
                if (root.TryGetProperty("brands", out var bs) && bs.ValueKind == JsonValueKind.Array)
                    brands = ReadBrands(bs);

                if (root.TryGetProperty("h", out var h) && h.ValueKind == JsonValueKind.Object)
                {
                    if (h.TryGetProperty("fullVersionList", out var fb) && fb.ValueKind == JsonValueKind.Array)
                        fullBrands = ReadBrands(fb);
                    if (h.TryGetProperty("uaFullVersion", out var fv) && fv.ValueKind == JsonValueKind.String)
                        fullVersion = fv.GetString() ?? "";
                    if (h.TryGetProperty("platformVersion", out var pv) && pv.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(pv.GetString()))
                        platformVersion = pv.GetString()!;
                    if (h.TryGetProperty("architecture", out var a) && a.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(a.GetString()))
                        arch = a.GetString()!;
                    if (h.TryGetProperty("bitness", out var b) && b.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(b.GetString()))
                        bitness = b.GetString()!;
                    if (h.TryGetProperty("model", out var mo) && mo.ValueKind == JsonValueKind.String)
                        model = mo.GetString() ?? "";
                    if (h.TryGetProperty("wow64", out var w)
                        && w.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        wow64 = w.GetBoolean();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("NexusWeb", "解析内核身份失败（改用默认身份）：" + ex.Message);
            }
        }

        var engine = string.IsNullOrWhiteSpace(engineVersion) ? "135.0.0.0" : engineVersion!;
        if (string.IsNullOrWhiteSpace(fullVersion)) fullVersion = engine;
        var major = fullVersion.Split('.')[0];
        if (major.Length == 0) major = "135";

        // UA 字符串：正常 WebView2 里本来就没有 WebView2 字样，但个别版本/自定义 UA 会带 ——
        // 带就整条重建，别只删词（删出来的串更容易不一致）。
        if (string.IsNullOrWhiteSpace(ua) || ua.Contains("WebView2", StringComparison.OrdinalIgnoreCase))
            ua = $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
               + $"Chrome/{engine} Safari/537.36 Edg/{engine}";

        brands = NormalizeBrands(brands, major, fullVersion, fullList: false);
        fullBrands = NormalizeBrands(fullBrands, major, fullVersion, fullList: true);

        var acceptLanguage = langs.Count > 0
            ? string.Join(",", langs.Take(4).Select((l, i) =>
                i == 0 ? l : $"{l};q={Math.Max(0.1, 1 - i * 0.1):0.0}"))
            : "zh-CN,zh;q=0.9,en;q=0.8";

        var payload = new Dictionary<string, object?>
        {
            ["userAgent"] = ua,
            ["acceptLanguage"] = acceptLanguage,
            ["platform"] = "Win32",
            ["userAgentMetadata"] = new Dictionary<string, object?>
            {
                ["brands"] = brands,
                ["fullVersionList"] = fullBrands,
                ["fullVersion"] = fullVersion,
                ["platform"] = string.IsNullOrWhiteSpace(platform) ? "Windows" : platform,
                ["platformVersion"] = platformVersion,
                ["architecture"] = arch,
                ["model"] = model,
                ["mobile"] = mobile,
                ["bitness"] = bitness,
                ["wow64"] = wow64,
            },
        };
        return JsonSerializer.Serialize(payload);
    }

    private static List<Dictionary<string, string>> ReadBrands(JsonElement array)
    {
        var list = new List<Dictionary<string, string>>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var brand = item.TryGetProperty("brand", out var b) && b.ValueKind == JsonValueKind.String
                ? b.GetString() ?? "" : "";
            var version = item.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";
            if (brand.Length == 0) continue;
            list.Add(new Dictionary<string, string> { ["brand"] = brand, ["version"] = version });
        }
        return list;
    }

    /// <summary>拼出真 Edge 的 Sec-CH-UA 三品牌形态（熵增品牌 + Chromium + Microsoft Edge）。
    /// <para><b>2026-11-11 04:48 实锤，这是「内置浏览器过不了人机验证」的真正原因。</b>
    /// WebView2 原生自报四条品牌：<c>Chromium/157 | Microsoft Edge WebView2/157 |
    /// Not-A.Brand/99 | Microsoft Edge/157</c>。旧逻辑在这之上做「去掉 WebView2」的修补，
    /// 但只要身份探针取不到数据（<see cref="Build"/> 的兜底分支），brands 就是空列表，
    /// 修补完只剩孤零零一条 <c>Microsoft Edge</c>。真 Edge / Chrome 恒定发三条，
    /// 单条品牌是任何正常浏览器都不会有的形态，Cloudflare 一眼判异常 ——
    /// 同一台机器、同一个 URL、同一个窗口位置，只改这一个字段：
    /// 单品牌 3 次全卡死在「请稍候…」，补全三条后 3 次全放行（见 tools/TurnstileProbe）。</para>
    /// <para>所以这里不再「在输入上修补」，而是<b>无条件产出规范三品牌</b>：
    /// 熵增品牌沿用内核自己的（WebView2 157 给的是 <c>Not-A.Brand/99</c>，这正是该版本
    /// 该发的那条），取不到就按同款兜底；Chromium 与 Microsoft Edge 用真实版本号补齐。</para>
    /// <para>顺序照真 Edge 的口径：熵增品牌 → Chromium → Microsoft Edge。</para></summary>
    /// <param name="fullList">true = 这是 fullVersionList（版本号带补丁位），false = 低熵 brands（只有大版本）。</param>
    private static List<Dictionary<string, string>> NormalizeBrands(
        List<Dictionary<string, string>> input, string majorVersion, string fullVersion, bool fullList)
    {
        // 熵增品牌（GREASE）：内核自己给的那条优先 —— 它和当前 Chromium 大版本是配套的。
        var greaseName = input
            .Select(b => b.TryGetValue("brand", out var n) ? n : "")
            .FirstOrDefault(n => n.Contains("Not", StringComparison.OrdinalIgnoreCase)
                              || n.Contains("Brand", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(greaseName)) greaseName = "Not-A.Brand";

        var v = fullList ? fullVersion : majorVersion;
        return new List<Dictionary<string, string>>
        {
            new() { ["brand"] = greaseName, ["version"] = fullList ? "99.0.0.0" : "99" },
            new() { ["brand"] = "Chromium", ["version"] = v },
            new() { ["brand"] = "Microsoft Edge", ["version"] = v },
        };
    }
}