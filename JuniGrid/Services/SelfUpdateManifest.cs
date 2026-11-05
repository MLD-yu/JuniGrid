using System.Net.Http;
using JuniGrid.Manifest;

namespace JuniGrid.Services;

/// <summary>
/// 自更新的<b>通道 2：签名清单</b>。
///
/// <para>为什么要有它：更新包要被执行，而"这包是不是官方的"只能靠校验值说话。校验值只有
/// GitHub API 通道给得出（release asset 上的 digest），而 API 有匿名配额（60 次/小时/IP），
/// 一被打满就走 HTML 回落 —— 那条通道<b>从来拿不到校验值</b>，于是出现过「零校验直接执行」
/// （审计第 10 条）。
///
/// <para>清单把这件事拆开：清单里写「版本 + sha256 + 大小 + 下载地址」，而清单本身是
/// <b>签过名的</b>（ECDSA P-256，公钥内置在应用里）。于是：
/// <list type="bullet">
///   <item>清单从哪取都无所谓 —— 自有域名、GitHub Pages、raw、任何镜像都行，
///   被改过就验签不过，这个源直接丢弃；</item>
///   <item>验过签的清单里的 sha256 就是我们自己签的凭据，和 API 通道的 digest 等价，
///   甚至更强（签名不依赖任何在线服务）。</item>
/// </list>
///
/// <para>失败处理有一条硬规矩：<b>验签失败只丢这个源，不算整轮更新失败</b>。
/// 否则有人污染一个托管点就能让所有人更新不了（DoS）。
/// </summary>
public static class SelfUpdateManifest
{
    /// <summary>
    /// 清单托管点。每个目录下放 <c>latest.json</c> 和 <c>latest.json.sig</c>。
    /// <list type="bullet">
    ///   <item>GitHub Pages：主源。</item>
    ///   <item>raw.githubusercontent.com：兜底（raw 没有 API 那样的匿名配额）。</item>
    /// </list>
    /// 以后有自己的域名就在这里加一条 —— 清单是签名的，加多少个源都不降低可信度，
    /// 只提高可用性。
    /// </summary>
    public static readonly string[] BaseUrls =
    {
        "https://mld-yu.github.io/JuniGrid",
        "https://raw.githubusercontent.com/MLD-yu/JuniGrid/gh-pages",
    };

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var h = new HttpClient();
        h.DefaultRequestHeaders.UserAgent.ParseAdd("JuniGrid-Launcher");
        // 5 秒上限：清单只在 API 通道失败时才试，而它后面还排着 HTML 回落。
        // 托管点连不上时不能拖太久 —— 8 秒 × 几个源 = 用户看着"检查更新"转十几秒。
        h.Timeout = TimeSpan.FromSeconds(5);
        return h;
    }

    /// <summary>
    /// 依次尝试各托管点，取第一份<b>验签通过</b>且能解析的清单。
    /// 返回 null = 所有源都不可用（调用方按老路走 HTML 回落）。
    /// </summary>
    public static async Task<SelfUpdateInfo?> TryFetchAsync()
    {
        foreach (var baseUrl in BaseUrls)
        {
            try
            {
                var json = await Http.GetByteArrayAsync(baseUrl + "/latest.json");
                var sigText = await Http.GetStringAsync(baseUrl + "/latest.json.sig");
                if (!ManifestTrust.VerifyBase64(json, sigText, out var keyId))
                {
                    AppLog.Warn("SelfUpdate", $"清单验签不过（{baseUrl}）—— 这个源不可信，换下一个");
                    continue;
                }
                var doc = ManifestDocument.Parse(json);
                if (doc is null)
                {
                    AppLog.Warn("SelfUpdate", $"清单内容不合规（{baseUrl}）—— 换下一个源");
                    continue;
                }
                var info = ToInfo(doc);
                if (info is null)
                {
                    AppLog.Warn("SelfUpdate", $"清单版本号看不懂（{baseUrl}）：{doc.Version}");
                    continue;
                }
                AppLog.Info("SelfUpdate",
                    $"清单通道：v{doc.Version} sha256={Short(doc.Sha256)} 大小={doc.Size} 密钥={keyId}（{baseUrl}）");
                return info;
            }
            catch (Exception ex)
            {
                AppLog.Warn("SelfUpdate", $"清单通道失败（{baseUrl}）：{ex.Message}");
            }
        }
        return null;
    }

    /// <summary>把清单映射成和 API/HTML 通道同一种结果对象。</summary>
    internal static SelfUpdateInfo? ToInfo(ManifestDocument doc)
    {
        var ver = doc.Version.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(ver, out var latest)) return null;
        if (!Version.TryParse(AppInfo.Version.Trim(), out var current)) return null;
        var hasUpdate = latest > current;
        var setupUrl = doc.Url;
        return new SelfUpdateInfo(ver, $"{AppInfo.ReleasesUrl}/tag/v{ver}", setupUrl, hasUpdate, doc.Sha256, doc.Size);
    }

    private static string Short(string hex) => hex.Length <= 12 ? hex : hex[..12] + "…";
}
