using System.IO;
using System.Net.Http;
using JuniGrid.Manifest;

namespace JuniGrid.Services;

/// <summary>
/// 星露谷历史版本表（版本号 ↔ depot manifest）的<b>运行时快照</b>。
///
/// <para>为什么要有它：这张表以前写死在 <c>DepotDownloaderService</c> 里，于是星露谷每更新一次，
/// 就得改代码 + 重编 + 发一个 120MB 的安装包，用户更新完才能在下拉框里看到新版本。
/// 现在表是三层的，优先级从低到高：</para>
/// <list type="number">
///   <item><b>内嵌资源</b>（<c>Assets/game-versions.json</c>，编进程序集）—— 首次启动 / 断网 /
///   所有托管点都挂时的兜底。它永远可用，所以列表不会因为网络问题变空。</item>
///   <item><b>本机缓存</b>（<see cref="StoragePaths.GameVersionMetaDir"/>）—— 上一次验签通过的
///   远端副本。启动时同步读，让"上次看到的新版本"在断网时也还在。</item>
///   <item><b>在线签名清单</b> —— 后台拉取，和自更新清单同一套 ECDSA P-256 验签、同一个公钥。
///   加一个游戏版本 = 改一行 json + 重签 + 推托管点，老客户端立刻能看到，<b>不用发版</b>。</item>
/// </list>
///
/// <para><b>为什么不担心"从哪拿"</b>：验签不过就丢弃这一份，什么都不改。所以托管点可以随便加
/// （GitHub Pages / raw / 自家域名 / 国内镜像），多一个源只提高可用性，不降低可信度 ——
/// 和 <see cref="SelfUpdateManifest"/> 是同一个安全模型。</para>
///
/// <para><b>顺序即语义</b>：列表从新到旧，下标就是版本序号（0 = 最新）。
/// <see cref="DepotDownloaderService.TryGetVersionRank"/> 和"当前是不是官方最新版"
/// 都直接拿下标说话，所以换表时顺序错了后果不是"少显示一项"，而是版本比较反了。</para>
/// </summary>
internal static class GameVersionCatalog
{
    internal const string FileName = "game-versions.json";
    internal const string SignatureName = "game-versions.json.sig";

    /// <summary>内嵌资源的逻辑名（&lt;RootNamespace&gt;.&lt;路径，分隔符换成点&gt;）。</summary>
    private const string EmbeddedResourceName = "JuniGrid.Assets.game-versions.json";

    /// <summary>表只对这一个 AppID 有效。别的游戏没有 manifest 表，别拿错文件当凭据。</summary>
    private const string StardewAppId = "413150";

    /// <summary>标签缺失时的兜底显示名。表里每条都带 label，这条几乎不会露脸。</summary>
    private const string FallbackLabel = "历史版";

    private static IReadOnlyList<DepotDownloaderService.KnownVersion> _entries = LoadEmbedded();

    /// <summary>当前生效的版本表（从新到旧）。整个引用原子替换，读者永远看到完整的一份。</summary>
    internal static IReadOnlyList<DepotDownloaderService.KnownVersion> Entries => Volatile.Read(ref _entries);

    /// <summary>这一份是从哪来的（"内置" / "本机缓存" / 托管点地址）。只用于日志与排错。</summary>
    internal static string Source { get; private set; } = "内置";

    /// <summary>最后一次成功应用远端表的时刻（UTC）。null = 本次会话还没成功过。</summary>
    internal static DateTime? LastOnlineRefreshUtc { get; private set; }

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var h = new HttpClient();
        h.DefaultRequestHeaders.UserAgent.ParseAdd("JuniGrid-Launcher");
        // 后台任务，不挡 UI：10 秒足够取一个 5KB 的文件，也足够判断"这个源挂死了"。
        h.Timeout = TimeSpan.FromSeconds(10);
        return h;
    }

    // ── 来源 ①：内嵌资源 ──────────────────────────────────────────────────────

    /// <summary>读程序集里那份。失败返回空表 —— 调用方都要能接受"空表"（UI 显示空列表，
    /// 而不是抛异常或回落到某个写死的旧表）。</summary>
    private static IReadOnlyList<DepotDownloaderService.KnownVersion> LoadEmbedded()
    {
        try
        {
            using var stream = typeof(GameVersionCatalog).Assembly
                .GetManifestResourceStream(EmbeddedResourceName);
            if (stream is null)
            {
                // csproj 里少一行 <EmbeddedResource> 就会走到这。不影响启动，但版本列表会是空的。
                AppLog.Warn("VersionCatalog", $"内嵌版本表缺失：{EmbeddedResourceName}（检查 JuniGrid.csproj）");
                return Array.Empty<DepotDownloaderService.KnownVersion>();
            }
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var doc = GameVersionCatalogDocument.Parse(ms.ToArray());
            if (doc is null)
            {
                AppLog.Warn("VersionCatalog", "内嵌版本表解析不过（JSON 格式/字段不合规）—— 版本列表将为空");
                return Array.Empty<DepotDownloaderService.KnownVersion>();
            }
            if (!string.Equals(doc.AppId, StardewAppId, StringComparison.Ordinal))
            {
                AppLog.Warn("VersionCatalog", $"内嵌版本表的 app_id 是 {doc.AppId}，不是 {StardewAppId} —— 忽略");
                return Array.Empty<DepotDownloaderService.KnownVersion>();
            }
            return ToKnown(doc);
        }
        catch (Exception ex)
        {
            AppLog.Warn("VersionCatalog", "读内嵌版本表失败：" + ex.Message);
            return Array.Empty<DepotDownloaderService.KnownVersion>();
        }
    }

    private static DepotDownloaderService.KnownVersion[] ToKnown(GameVersionCatalogDocument doc)
        => doc.Versions
            .Where(v => !string.IsNullOrWhiteSpace(v.Version) && !string.IsNullOrWhiteSpace(v.Manifest))
            .Select(v => new DepotDownloaderService.KnownVersion(
                string.IsNullOrWhiteSpace(v.Label) ? FallbackLabel : v.Label,
                v.Manifest.Trim(),
                v.Version.Trim()))
            .ToArray();

    // ── 来源 ②：本机缓存 ────────────────────────────────────────────────────

    private static string CacheJsonPath => Path.Combine(StoragePaths.GameVersionMetaDir, FileName);
    private static string CacheSigPath => Path.Combine(StoragePaths.GameVersionMetaDir, SignatureName);

    /// <summary>
    /// 启动时同步读一次缓存（5KB，可以忽略耗时）。必须先于任何 <c>GetKnownVersions</c> 调用。
    /// 缓存坏了/被删了都不影响启动：只是退回内嵌那份，下次后台刷新会补回来。
    /// </summary>
    internal static void LoadDiskCache()
    {
        try
        {
            if (!File.Exists(CacheJsonPath) || !File.Exists(CacheSigPath)) return;
            var bytes = File.ReadAllBytes(CacheJsonPath);
            var sigText = File.ReadAllText(CacheSigPath);
            // 缓存也验签：它是磁盘上的文件，别的进程/用户换掉它同样不该被当真。
            if (!ManifestTrust.VerifyBase64(bytes, sigText, out _))
            {
                AppLog.Warn("VersionCatalog", "本机缓存的版本表验签不过 —— 丢弃，下次联网重取");
                return;
            }
            var doc = GameVersionCatalogDocument.Parse(bytes);
            if (doc is null)
            {
                AppLog.Warn("VersionCatalog", "本机缓存的版本表解析不过 —— 丢弃");
                return;
            }
            Apply(doc, "本机缓存");
        }
        catch (Exception ex)
        {
            AppLog.Warn("VersionCatalog", "读本机版本表缓存失败：" + ex.Message);
        }
    }

    // ── 来源 ③：在线签名清单 ────────────────────────────────────────────────

    /// <summary>后台刷新一次，不阻塞、不抛。</summary>
    internal static void RefreshInBackground() => _ = RefreshAsync();

    /// <summary>
    /// 并发探所有托管点，取第一份验签通过、能解析、且不比本机旧的表。
    ///
    /// <para>为什么并发而不是串行：这些托管点在国内经常直接连不上，串行会把超时叠起来
    /// （2 个源 × 10 秒），后台任务虽然不挡 UI，但一开机就挂着两个死连接没必要。</para>
    /// </summary>
    /// <returns>有没有成功换上新的表。</returns>
    internal static async Task<bool> RefreshAsync()
    {
        try
        {
            var probes = SelfUpdateManifest.BaseUrls.Select(FetchOneAsync).ToList();
            var results = await Task.WhenAll(probes);
            foreach (var r in results)
            {
                if (r is null) continue;
                if (!Apply(r.Doc, r.BaseUrl)) continue;

                LastOnlineRefreshUtc = DateTime.UtcNow;
                SaveDiskCache(r.Bytes, r.Signature);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Warn("VersionCatalog", "版本表后台刷新失败：" + ex.Message);
            return false;
        }
    }

    private sealed record Fetched(GameVersionCatalogDocument Doc, byte[] Bytes, string Signature, string BaseUrl);

    /// <summary>取一个托管点。任何一步不过都返回 null（这个源不可信/不可用，不是整轮失败）。</summary>
    private static async Task<Fetched?> FetchOneAsync(string baseUrl)
    {
        try
        {
            // json 与 sig 并发取：同一个站点的两个小文件，没必要串着等。
            var jsonTask = Http.GetByteArrayAsync(baseUrl + "/" + FileName);
            var sigTask = Http.GetStringAsync(baseUrl + "/" + SignatureName);
            await Task.WhenAll(jsonTask, sigTask);
            var bytes = await jsonTask;
            var sigText = await sigTask;

            if (!ManifestTrust.VerifyBase64(bytes, sigText, out var keyId))
            {
                AppLog.Warn("VersionCatalog", $"版本表验签不过（{baseUrl}）—— 这个源不可信，换下一个");
                return null;
            }
            var doc = GameVersionCatalogDocument.Parse(bytes);
            if (doc is null)
            {
                AppLog.Warn("VersionCatalog", $"版本表内容不合规（{baseUrl}）—— 换下一个源");
                return null;
            }
            // 清单自述的 key_id 必须和实际验过的那把一致（轮换期两把钥匙并存时，
            // 这是唯一能发现"签名方和自述漂了"的地方），和 SelfUpdateManifest 同一道门。
            if (!string.IsNullOrWhiteSpace(doc.KeyId)
                && !string.Equals(doc.KeyId, keyId, StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Warn("VersionCatalog", $"版本表自述密钥 {doc.KeyId}，实际是 {keyId} 签的（{baseUrl}）—— 丢弃");
                return null;
            }
            return new Fetched(doc, bytes, sigText, baseUrl);
        }
        catch (Exception ex)
        {
            AppLog.Warn("VersionCatalog", $"版本表通道失败（{baseUrl}）：{ex.Message}");
            return null;
        }
    }

    private static void SaveDiskCache(byte[] bytes, string sigText)
    {
        try
        {
            // 两个文件分开原子写，中间崩掉会留下"新 json + 旧 sig"的一对 —— 下次读的时候
            // 验签不过就丢弃，退回内嵌那份，不会用到可疑数据。顺序（先 json 后 sig）随机，
            // 两种残留都是"验不过"而不是"验得过但内容错"。
            AtomicFile.WriteAllBytes(CacheJsonPath, bytes);
            AtomicFile.WriteAllText(CacheSigPath, sigText);
        }
        catch (Exception ex)
        {
            AppLog.Warn("VersionCatalog", "写版本表缓存失败（不影响本次使用）：" + ex.Message);
        }
    }

    // ── 应用与防重放 ─────────────────────────────────────────────────────────

    /// <summary>把一份目录换成当前快照。验签由调用方负责，这里只管一致性与防重放。</summary>
    private static bool Apply(GameVersionCatalogDocument doc, string source)
    {
        if (!string.Equals(doc.AppId, StardewAppId, StringComparison.Ordinal))
        {
            AppLog.Warn("VersionCatalog", $"版本表的 app_id 是 {doc.AppId}，不是 {StardewAppId}（{source}）—— 忽略");
            return false;
        }
        var next = ToKnown(doc);
        if (next.Length == 0) return false;

        if (IsStale(next, Entries, out var why))
        {
            AppLog.Warn("VersionCatalog", $"驳回更旧的版本表（{source}）：{why}");
            return false;
        }

        Interlocked.Exchange(ref _entries, next);
        Source = source;
        AppLog.Info("VersionCatalog",
            $"版本表就绪（{source}）：{next.Length} 个版本，最新 {next[0].DisplayName}");
        return true;
    }

    /// <summary>
    /// 「这份表比本机那份旧」的判据：本机最新版本在远端排到了第 2 位之后。
    ///
    /// <para>为什么要拦：<b>签名只证明"这份表是作者签的"，不证明"它是最新的"</b>。
    /// 一份旧的已签名表被重放（CDN 缓存、中间人、滞后的镜像）时，后果不是"少看到一个新版本"——
    /// <see cref="DepotDownloaderService.IsLatestOfficialManifest"/> 拿 <c>Entries[0]</c> 判断
    /// "当前装的是不是官方最新版"，表一旧就会把用户的真·最新版判成历史版，于是去锁 Steam 的
    /// appmanifest，Steam 自己更新时被卡成「磁盘写入错误」（v1.6.8 实际事故）。
    /// 也就是说，一张过期的表能把玩家卡在旧版本上出不来。</para>
    ///
    /// <para>本机没有记录（首次启动、内嵌表也空）或远端不认识本机最新版本时放行 ——
    /// 后者是作者主动删除/改名某个版本的正常情况，不该被当成攻击。</para>
    /// </summary>
    private static bool IsStale(DepotDownloaderService.KnownVersion[] next,
        IReadOnlyList<DepotDownloaderService.KnownVersion> current, out string why)
    {
        why = "";
        if (current.Count == 0 || next.Length == 0) return false;

        var newest = current[0].Version;
        if (string.IsNullOrWhiteSpace(newest)) return false;

        var idx = Array.FindIndex(next,
            v => string.Equals(v.Version, newest, StringComparison.OrdinalIgnoreCase));
        if (idx <= 0) return false;

        why = $"远端最新是 {next[0].DisplayName}，而本机这份的最新 {newest} 在远端排在第 {idx + 1} 位";
        return true;
    }
}
