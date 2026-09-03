using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JuniGrid.Services;

/// <summary>
/// Nexus Mods Public API v1 (https://api.nexusmods.com).
///  - Version checks work with any free personal API key.
///  - Direct download links are Premium-only (Nexus policy): free accounts
///    get HTTP 403 on download_link — surfaced as NeedsPremium.
/// Rate limits: ~100 req/day free, 2500/day premium (X-RL-* headers).
/// </summary>
public sealed class NexusService
{
    private const string Base = "https://api.nexusmods.com/v1/games/stardewvalley";
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var h = new HttpClient();
        h.DefaultRequestHeaders.UserAgent.ParseAdd("JuniGrid-Launcher");
        // Nexus AUP 要求的应用标识头
        h.DefaultRequestHeaders.TryAddWithoutValidation("Application-Name", "JuniGrid");
        h.DefaultRequestHeaders.TryAddWithoutValidation("Application-Version", "0.2.0");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        h.Timeout = TimeSpan.FromSeconds(15);   // v1.06.8：检查更新提速——慢请求 15s 快速失败，不再拖住整批
        return h;
    }

    private static HttpRequestMessage Req(string? apiKey, string url)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(apiKey))
            r.Headers.TryAddWithoutValidation("apikey", apiKey);
        return r;
    }

    // v1.08：宽容客户端 —— 详情页/按需单请求专用。Nexus API 单请求实测要 5~8 秒，
    // 详情页要串 7 个请求，15s 快速失败通道必然超时。批量检查更新仍走 Http(15s)。
    private static readonly HttpClient SlowHttp = CreateSlow();
    private static HttpClient CreateSlow()
    {
        var h = CreateClient();
        h.Timeout = TimeSpan.FromSeconds(45);
        return h;
    }

    /// <summary>Mod metadata (name + current version + cover). null on error.</summary>
    /// <summary>v0.46.0：拉取本游戏的官方分类表（category_id → 名称），调用方缓存进 config。</summary>
    public async Task<Dictionary<int, string>?> GetCategoriesAsync(string apiKey)
    {
        using var res = await Http.SendAsync(Req(apiKey, Base + ".json"));
        if (!res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("categories", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;
        var dict = new Dictionary<int, string>();
        foreach (var c in arr.EnumerateArray())
        {
            var id = c.TryGetProperty("category_id", out var e1) && e1.ValueKind == JsonValueKind.Number ? e1.GetInt32()
                   : c.TryGetProperty("id", out var e2) && e2.ValueKind == JsonValueKind.Number ? e2.GetInt32() : -1;
            var name = c.TryGetProperty("name", out var ne) && ne.ValueKind == JsonValueKind.String ? ne.GetString() : null;
            if (id >= 0 && !string.IsNullOrWhiteSpace(name)) dict[id] = name;
        }
        return dict;
    }

    public async Task<NexusModInfo?> GetModAsync(string apiKey, int modId)
    {
        using var res = await Http.SendAsync(Req(apiKey, $"{Base}/mods/{modId}.json"));
        if (!res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        return new NexusModInfo(
            modId,
            GetStr(root, "name"),
            GetStr(root, "version"),
            GetStr(root, "picture_url"),
            root.TryGetProperty("category_id", out var cid) && cid.ValueKind == JsonValueKind.Number
                ? cid.GetInt32() : (int?)null);
    }

    /// <summary>Full mod detail for the in-app detail page (incl. HTML description + cover).</summary>
    public async Task<NexusModDetail?> GetModDetailAsync(string apiKey, int modId)
    {
        using var res = await SlowHttp.SendAsync(Req(apiKey, $"{Base}/mods/{modId}.json"));
        if (!res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        var updatedTs = GetInt64(root, "updated_timestamp");
        var endorsements = GetInt64(root, "endorsement_count");
        if (endorsements == 0) endorsements = GetInt64(root, "endorsements");

        var deps = new List<NexusModDependency>();
        if (root.TryGetProperty("dependencies", out var depArr) && depArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in depArr.EnumerateArray())
            {
                var did = d.TryGetProperty("mod_id", out var di) && di.ValueKind == JsonValueKind.Number
                    ? di.GetInt32() : 0;
                var dname = GetStr(d, "name");
                var davail = GetStr(d, "availability");
                if (did > 0)
                    deps.Add(new NexusModDependency(did, dname, davail));
            }
        }

        // 图集：primary picture 打头，其余按 images 数组顺序排。
        // Nexus v1 的 images 每项字段可能是 picture_url / original_url / thumbnail_url，
        // 按清晰度优先取 original_url > picture_url > thumbnail_url。
        var primary = GetStr(root, "picture_url");
        var images = new List<string>();
        if (!string.IsNullOrWhiteSpace(primary)) images.Add(primary);
        if (root.TryGetProperty("images", out var imgArr) && imgArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var img in imgArr.EnumerateArray())
            {
                var u = GetStr(img, "original_url");
                if (string.IsNullOrEmpty(u)) u = GetStr(img, "picture_url");
                if (string.IsNullOrEmpty(u)) u = GetStr(img, "thumbnail_url");
                if (!string.IsNullOrEmpty(u) && !images.Contains(u, StringComparer.OrdinalIgnoreCase))
                    images.Add(u);
            }
        }
        if (images.Count == 0 && !string.IsNullOrEmpty(primary)) images.Add(primary);

        return new NexusModDetail(
            modId,
            GetStr(root, "name"),
            GetStr(root, "author"),
            GetStr(root, "version"),
            GetStr(root, "summary"),
            SanitizeHtml(GetStr(root, "description")),
            primary,
            GetInt64(root, "mod_downloads"),
            endorsements,
            updatedTs > 0
                ? DateTimeOffset.FromUnixTimeSeconds(updatedTs).LocalDateTime.ToString("yyyy-MM-dd")
                : "",
            deps,
            images);
    }

    // ---- JSON helpers ----
    private static string GetStr(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static long GetInt64(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt64(out var n) ? n : 0;

    /// <summary>
    /// Nexus 的 description 是 BBCode 风格（[b]…[/b]、[url=…]…[/url]、[list]…[/list]），
    /// 不是 HTML —— 直接 (MarkupString) 会把这些当成字面文本显示。
    /// 这里把常见 BBCode 转成真正的 HTML，让详情页能渲染出加粗/链接/列表/颜色。
    /// </summary>
    private static string SanitizeHtml(string html)
{
    if (string.IsNullOrEmpty(html)) return "";
    var s = html;
    // 先剥掉危险的 script（防御）
    s = Regex.Replace(s, "<script.*?</script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    // 若一段是转义实体（&lt;）则先解一次
    if (s.Contains("&lt;", StringComparison.OrdinalIgnoreCase))
        s = System.Net.WebUtility.HtmlDecode(s);

    // ── BBCode → HTML ──
    s = Regex.Replace(s, @"\[b\](.*?)\[/b\]", "<b>$1</b>", RegexOptions.Singleline);
    s = Regex.Replace(s, @"\[i\](.*?)\[/i\]", "<i>$1</i>", RegexOptions.Singleline);
    s = Regex.Replace(s, @"\[u\](.*?)\[/u\]", "<u>$1</u>", RegexOptions.Singleline);
    s = Regex.Replace(s, @"\[s\](.*?)\[/s\]", "<s>$1</s>", RegexOptions.Singleline);
    // 颜色 / 字号（Nexus 的 size 是 1–7 档位，映射到可读的像素字号）
    s = Regex.Replace(s, @"\[color\s*=\s*([^\]]+)\](.*?)\[/color\]",
        "<span style=\"color:$1\">$2</span>", RegexOptions.Singleline);
    s = Regex.Replace(s, @"\[size\s*=\s*([^\]]+)\](?<c>.*?)\[/size\]", m =>
    {
        var n = int.TryParse(m.Groups[1].Value.Trim(), out var v) ? v : 3;
        var px = n switch
        {
            <= 1 => 11, 2 => 12, 3 => 14, 4 => 17, 5 => 20,
            6 => 24, _ => 28
        };
        return $"<span style=\"font-size:{px}px\">{m.Groups["c"].Value}</span>";
    }, RegexOptions.Singleline);
    // 链接：[url=外链]文字[/url] 或 [url]外链[/url]
    s = Regex.Replace(s, @"\[url\s*=\s*[^\]]+?\](.*?)\[/url\]", m =>
    {
        var tag = m.Value;
        var url = Regex.Match(tag, @"\[url\s*=\s*(?<u>[^\]]+?)\]").Groups["u"].Value.Trim('"', '\'', ' ');
        var text = Regex.Replace(m.Value, @"^\[url\s*=[^\]]*\]|\[/url\]$", "");
        return $"<a href=\"{System.Net.WebUtility.HtmlEncode(url)}\" target=\"_blank\" rel=\"noopener\">{text}</a>";
    }, RegexOptions.Singleline);
    s = Regex.Replace(s, @"\[url\](.*?)\[/url\]",
        "<a href=\"$1\" target=\"_blank\" rel=\"noopener\">$1</a>", RegexOptions.Singleline);
    // 图片
    s = Regex.Replace(s, @"\[img\s*(?:=\s*([^\]]+))?\](.*?)\[/img\]",
        "<img src=\"$2\" alt=\"\" loading=\"lazy\" class=\"jg-desc-img\"/>", RegexOptions.Singleline);
    // v0.56.0：HTML <img> 标签也统一加 class，便于 Flip 放大
    s = Regex.Replace(s, @"<img\s+([^>]*?)src=""([^""]+)""([^>]*?)>",
        @"<img src=""$2"" alt="""" loading=""lazy"" class=""jg-desc-img""/>", RegexOptions.IgnoreCase);
    // v0.57.0：裸图片直链（既没包 [img] 也不是 <img> 的）也渲染成图片。
    // 前置否定 lookbehind 排除已生成标签属性里的 URL（href="/src=" 前的引号、> 等）。
    s = Regex.Replace(s, @"(?<![""'>=])(https?://[^\s<""'\]\[]+?\.(?:png|jpe?g|gif|webp)(?:\?[a-zA-Z0-9=&_%\-]*)?)",
        "<img src=\"$1\" alt=\"\" loading=\"lazy\" class=\"jg-desc-img\"/>", RegexOptions.IgnoreCase);
    // 引用 / 代码
    s = Regex.Replace(s, @"\[quote\](.*?)\[/quote\]",
        "<blockquote>$1</blockquote>", RegexOptions.Singleline);
    s = Regex.Replace(s, @"\[code\](.*?)\[/code\]",
        "<pre>$1</pre>", RegexOptions.Singleline);

    // 列表与列表项：用栈记录 open 的 <ul>/<ol>，让 [/list] 正确闭合并保持嵌套
    var listStack = new Stack<string>();
    s = Regex.Replace(s, @"\[(/)?list(=([^\]]+))?\]", match =>
    {
        if (match.Value == "[/list]")
        {
            if (listStack.Count == 0) return "";
            return "</" + listStack.Pop() + ">";
        }
        var kindAttr = match.Groups[2].Success ? match.Groups[3].Value.Trim() : "";
        var tag = (!string.IsNullOrEmpty(kindAttr) && kindAttr is "1" or "a" or "o") ? "ol" : "ul";
        listStack.Push(tag);
        return "<" + tag + ">";
    }, RegexOptions.IgnoreCase);
    s = Regex.Replace(s, @"\[item\]", "<li>", RegexOptions.IgnoreCase);
    s = Regex.Replace(s, @"\[\*\]", "<li>", RegexOptions.IgnoreCase);

    // 常用子集：字体、水平线、float 保留辅助样式
    s = Regex.Replace(s, @"\[font\s*=\s*([^\]]+)\](.*?)\[/font\]",
        "<span style=\"font-family:$1\">$2</span>", RegexOptions.Singleline);
    s = Regex.Replace(s, @"\[hr\]", "<hr/>", RegexOptions.IgnoreCase);

    // 剩下的未知 BBCode 当作纯文本剥掉标签语法，避免方括号毒化
    // v0.58.0：列表闭合标记 [*] 的收尾 [/ *] / [/*] 也要清掉（* 不是字母，原规则匹配不到）
    s = Regex.Replace(s, @"\[/?\*\]", "", RegexOptions.Singleline);
    s = Regex.Replace(s, @"\[/?[a-zA-Z][a-zA-Z0-9_=:,/\.# -]*?\]", "", RegexOptions.Singleline);

    return s;
}

    /// <summary>Newest MAIN file (fallback: newest file of any category).</summary>
    public async Task<NexusFileInfo?> GetLatestMainFileAsync(string apiKey, int modId, bool patient = false)
    {
        using var res = await (patient ? SlowHttp : Http).SendAsync(Req(apiKey, $"{Base}/mods/{modId}/files.json"));
        if (!res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("files", out var files)) return null;

            NexusFileInfo? bestMain = null, bestAny = null;
            long bestMainTs = -1, bestAnyTs = -1;
            foreach (var f in files.EnumerateArray())
            {
                var id = f.TryGetProperty("file_id", out var fi) ? fi.GetInt64() : 0;
                var name = f.TryGetProperty("name", out var fn) ? fn.GetString() ?? "" : "";
                var ver = f.TryGetProperty("version", out var fv) ? fv.GetString() ?? "" : "";
                var cat = f.TryGetProperty("category_name", out var fc) ? fc.GetString() ?? "" : "";
                var ts = f.TryGetProperty("uploaded_timestamp", out var ft) ? ft.GetInt64() : 0;
                // v1.04.0：主文件大小（字节）—— 详情页统计行「大小」用；缺失按 0 处理（详情页照样显示 0 B）
                var size = f.TryGetProperty("size", out var fz) && fz.ValueKind == JsonValueKind.Number
                    && fz.TryGetInt64(out var szl) ? szl : 0;
                var info = new NexusFileInfo(id, name, ver, cat, size);
                if (ts > bestAnyTs) { bestAnyTs = ts; bestAny = info; }
                if (cat.Equals("MAIN", StringComparison.OrdinalIgnoreCase) && ts > bestMainTs)
                { bestMainTs = ts; bestMain = info; }
            }
            return bestMain ?? bestAny;
    }

    /// <summary>
    /// vNext：更新检查「指纹批量快道」—— 一次 GraphQL legacyModsByDomain 拿一批 mod 的
    /// { modId, version, updatedAt, pictureUrl }（50 个/批、免 API key、与封面/榜单同一通道）。
    /// 核心依据（2026-09 实测验证）：mod 的 updatedAt 跟随文件上传变化（SVE 最新 MAIN 文件
    /// 上传于 23:17:39，updatedAt=23:19:24）—— updatedAt 没变 ⟹ 文件列表没变 ⟹ 上次
    /// files.json 查到的「最新 MAIN 文件版本」仍然有效，可整批跳过逐 mod 精查。
    /// 几百个 mod 的更新检查从 N 个请求塌缩到 ~N/50 个请求（重启后/超 60 分钟 TTL 的
    /// 常规进页从几十秒降到约 1 秒）。
    /// 任一批次失败返回 null（调用方整体回落到逐 mod 精查，宁可慢不可错）。
    /// </summary>
    public async Task<Dictionary<int, NexusModFingerprint>?> GetModFingerprintsBatchAsync(
        IEnumerable<int> modIds, string gameDomain = "stardewvalley")
    {
        try
        {
            var ids = modIds.Distinct().ToList();
            if (ids.Count == 0) return new Dictionary<int, NexusModFingerprint>();
            const int CHUNK = 50;   // 与浏览页 FetchChunkedAsync 同尺寸（单批 50 稳定可用）
            var result = new Dictionary<int, NexusModFingerprint>();
            // 各批次并行拉（几百个 mod 也只是几个并发 POST，无 key 无限流压力）
            var chunks = new List<Task<Dictionary<int, NexusModFingerprint>?>>();
            for (var i = 0; i < ids.Count; i += CHUNK)
            {
                var slice = ids.Skip(i).Take(CHUNK).ToList();
                chunks.Add(Task.Run(async () =>
                {
                    var idArgs = string.Join(",", slice.Select(id =>
                        "{gameDomain:\"" + gameDomain + "\", modId:" + id + "}"));
                    var d = await GraphQlAsync(
                        "{ legacyModsByDomain(ids:[" + idArgs + "]) { nodes { modId version updatedAt pictureUrl } } }");
                    if (d is null) return null;
                    var root = d.Value;
                    if (!root.TryGetProperty("legacyModsByDomain", out var lb)
                        || lb.ValueKind != JsonValueKind.Object
                        || !lb.TryGetProperty("nodes", out var nodes)
                        || nodes.ValueKind != JsonValueKind.Array)
                        return null;
                    var dict = new Dictionary<int, NexusModFingerprint>();
                    foreach (var n in nodes.EnumerateArray())
                    {
                        var mid = n.TryGetProperty("modId", out var m1) && m1.ValueKind == JsonValueKind.Number
                            ? m1.GetInt32()
                            : int.TryParse(GetStr(n, "modId"), out var p) ? p : 0;
                        if (mid <= 0) continue;
                        dict[mid] = new NexusModFingerprint(
                            mid, GetStr(n, "version"), GetStr(n, "updatedAt"), GetStr(n, "pictureUrl"));
                    }
                    return dict;
                }));
            }
            foreach (var t in chunks)
            {
                var dict = await t;
                if (dict is null) return null;
                foreach (var kv in dict) result[kv.Key] = kv.Value;
            }
            return result;
        }
        catch { return null; }
    }

    /// <summary>v0.69.0：mod 的更新日志（版本 → 变更行）。对应官网 LOGS 页签的 Changelogs。</summary>
    public async Task<List<NexusChangelog>?> GetChangelogsAsync(string apiKey, int modId)
    {
        using var res = await SlowHttp.SendAsync(Req(apiKey, $"{Base}/mods/{modId}/changelogs.json"));
        if (!res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var list = new List<NexusChangelog>();
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return list;
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var lines = new List<string>();
            if (prop.Value.ValueKind == JsonValueKind.Array)
                foreach (var l in prop.Value.EnumerateArray())
                    if (l.ValueKind == JsonValueKind.String) lines.Add(l.GetString() ?? "");
            list.Add(new NexusChangelog(prop.Name, lines));
        }
        return list;
    }

    /// <summary>
    /// v0.69.2：抓取 mod 图片页（?tab=images）补齐完整画廊。
    /// 根因：v1 mods.json 的 images 字段基本只含主图（官网 25 张不在其中），
    /// 完整图集只存在于图片页 HTML 里。抓到 staticdelivery 直链、按文件名去重。
    /// 任何失败返回 null（调用方保留原主图），绝不影响详情页。
    /// </summary>
    public async Task<List<string>?> GetModImagesAsync(int modId)
    {
        try
        {
            using var res = await Http.GetAsync(
                $"https://www.nexusmods.com/stardewvalley/mods/{modId}?tab=images");
            if (!res.IsSuccessStatusCode) return null;
            var html = await res.Content.ReadAsStringAsync();
            var urls = new List<string>();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                html, @"https://staticdelivery\.nexusmods\.com/[^""'\s\\]+?\.(?:png|jpe?g|webp)(?:\?[^""'\s\\]*)?",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                var u = m.Value;
                // 剔除头像/图标类小图
                if (u.Contains("/avatars/", StringComparison.OrdinalIgnoreCase)) continue;
                // 按 base（去掉查询串）去重，查询串不同的同图只留一张
                var baseU = u.Split('?')[0];
                if (!urls.Any(x => x.Split('?')[0].Equals(baseU, StringComparison.OrdinalIgnoreCase)))
                    urls.Add(u);
            }
            return urls.Count > 1 ? urls : null;
        }
        catch { return null; }
    }


    // ══════════════════════════════════════════════════════════════════
    // v0.69.5：Requirements 走 GraphQL v2（修复 v0.69.3 手工拼 JSON 内层引号未转义
    // 导致请求体非法、接口 400、UI 永远卡在 shimmer 的 bug —— 改用 JsonSerializer）。
    // ══════════════════════════════════════════════════════════════════
    // v0.96.0：切到官网同款 api-router 端点 —— 旧 api.nexusmods.com/v2 的 modId EQUALS(数字尾号搜索)
    // 在它上面永远返回空，api-router 上正常；其余字段/形状完全兼容（官网前端也走这条）。
    private const string GraphQlEndpoint = "https://api-router.nexusmods.com/graphql";
    /// <summary>v0.79.0：成人内容总开关 —— false=浏览/搜索 GraphQL 追加 adultContent:false 过滤条件。
    /// 由 ConfigService 按「设置 → 过滤色情内容」开关同步（FilterAdultContent=true ⇒ 这里=false）。</summary>
    public static bool IncludeAdultContent = true;
    /// <summary>「只显示成人内容」开关 —— true 时浏览/搜索 GraphQL 追加 adultContent:true 过滤条件
    /// （优先级高于 IncludeAdultContent，两者互斥由 ConfigService 保证）。默认关闭。</summary>
    public static bool OnlyAdultContent = false;
    /// <summary>v0.81.0：最近一次榜单查询服务端报告的 totalCount（分页器「第 x / N 页 · 共 X 个」的数据源）。</summary>
    public int? LastBrowseTotalCount;
    /// <summary>v0.96.0：Surprise 榜服务端 random 排序种子 —— 翻页期间保持不变保证页序连续，「换一批」时换新种子。</summary>
    public int SurpriseSeed { get; private set; } = Random.Shared.Next();
    public void ReshuffleSurprise() => SurpriseSeed = Random.Shared.Next();
    private static readonly ConcurrentDictionary<string, int> GameIdCache = new();

    private async Task<JsonElement?> GraphQlAsync(string query)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { query });
            using var res = await Http.PostAsync(GraphQlEndpoint,
                new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            // v0.76.0：GraphQL 语法/参数报错时响应是 200 + {"errors":[...],"data":null} ——
            // data 字段【存在但为 null】也必须当失败处理，否则下方解析抛异常、降级重试永远不触发
            // （这就是选时间段/Trending 必然「拉取失败」的根因）。
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind == JsonValueKind.Null)
                return null;
            return data.Clone();
        }
        catch { return null; }
    }

    /// <summary>拉取官网 Requirements（要求/依赖表格）。失败返回 null，由 UI 给"到官网查看"兜底。</summary>
    public async Task<List<NexusRequirement>?> GetModRequirementsAsync(int modId, string gameDomain = "stardewvalley")
    {
        if (!GameIdCache.TryGetValue(gameDomain, out var gameId))
        {
            var g = await GraphQlAsync("{ game(domainName:\"" + gameDomain + "\") { id } }");
            if (g is null) return null;
            var gv = g.Value;
            if (!gv.TryGetProperty("game", out var gg) || gg.ValueKind != JsonValueKind.Object) return null;
            var gidEl = gg.TryGetProperty("id", out var tmp) ? tmp : default;
            gameId = gidEl.ValueKind == JsonValueKind.Number ? gidEl.GetInt32()
                   : int.TryParse(gidEl.ToString(), out var p) ? p : 0;
            if (gameId <= 0) return null;
            GameIdCache[gameDomain] = gameId;
        }

        var d = await GraphQlAsync(
            "{ mod(gameId:\"" + gameId + "\", modId:\"" + modId + "\") { modRequirements { nexusRequirements { nodes { modName notes url modId externalRequirement } } } } }");
        if (d is null) return null;
        var root = d.Value;
        if (!root.TryGetProperty("mod", out var mod) || mod.ValueKind != JsonValueKind.Object ||
            !mod.TryGetProperty("modRequirements", out var mreq) || mreq.ValueKind != JsonValueKind.Object ||
            !mreq.TryGetProperty("nexusRequirements", out var nreq) || nreq.ValueKind != JsonValueKind.Object ||
            !nreq.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
            return null;

        var list = new List<NexusRequirement>();
        foreach (var n in nodes.EnumerateArray())
            list.Add(new NexusRequirement(
                GetStr(n, "modName"), GetStr(n, "notes"), GetStr(n, "url"),
                int.TryParse(GetStr(n, "modId"), out var rid) ? rid : 0,
                n.TryGetProperty("externalRequirement", out var ex) && ex.ValueKind == JsonValueKind.True));
        return list;
    }

    /// <summary>v0.69.7：Requirements + 封面（先取需求，再按 modId 批量补 pictureUrl）。</summary>
    public async Task<List<NexusRequirementEx>?> GetModRequirementsWithCoversAsync(int modId, string gameDomain = "stardewvalley")
    {
        var base_ = await GetModRequirementsAsync(modId, gameDomain);
        if (base_ is null) return null;
        var withId = base_.Where(r => r.ModId > 0 && !r.External).ToList();
        var covers = new Dictionary<int, string?>();
        if (withId.Count > 0)
        {
            try
            {
                var ids = string.Join(",", withId.Select(r =>
                    "{gameDomain:\"" + gameDomain + "\", modId:" + r.ModId + "}"));
                var d = await GraphQlAsync("{ legacyModsByDomain(ids:[" + ids + "]) { nodes { modId pictureUrl } } }");
                if (d is not null && d.Value.TryGetProperty("legacyModsByDomain", out var lm)
                    && lm.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
                    foreach (var n in nodes.EnumerateArray())
                    {
                        var mid = n.TryGetProperty("modId", out var m1) && m1.ValueKind == JsonValueKind.Number ? m1.GetInt32()
                                : int.TryParse(GetStr(n, "modId"), out var p) ? p : 0;
                        var pic = GetStr(n, "pictureUrl");
                        if (mid > 0) covers[mid] = string.IsNullOrWhiteSpace(pic) ? null : pic;
                    }
            }
            catch { /* 封面缺失不阻塞 */ }
        }
        return base_.Select(r => new NexusRequirementEx(
            r.ModName, r.Notes, r.Url, r.ModId, r.External,
            covers.TryGetValue(r.ModId, out var pu) ? pu : null)).ToList();
    }

    /// <summary>v0.69.7：译本 —— 用 mods 搜索按主 mod 名匹配翻译版本（官网 HTML 抓取被 403 反爬挡死）。</summary>
    public async Task<List<NexusTranslationItem>?> GetModTranslationsAsync(int modId, string modName, string gameDomain = "stardewvalley")
    {
        try
        {
            if (!GameIdCache.TryGetValue(gameDomain, out var gameId))
            {
                var g = await GraphQlAsync("{ game(domainName:\"" + gameDomain + "\") { id } }");
                if (g is null) return null;
                var gv = g.Value;
                if (!gv.TryGetProperty("game", out var gg) || gg.ValueKind != JsonValueKind.Object) return null;
                var gidEl = gg.TryGetProperty("id", out var tmp) ? tmp : default;
                gameId = gidEl.ValueKind == JsonValueKind.Number ? gidEl.GetInt32()
                       : int.TryParse(gidEl.ToString(), out var p) ? p : 0;
                if (gameId <= 0) return null;
                GameIdCache[gameDomain] = gameId;
            }
            // 主名做通配搜索；结果里排除本体，只留带翻译语义的
            var safeName = new string((modName ?? "")
                .Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_').ToArray()).Trim();
            if (string.IsNullOrEmpty(safeName)) return new List<NexusTranslationItem>();
            var q = "{ mods(filter:{gameId:{value:\"" + gameId + "\"}, name:{value:\"" + safeName
                + "\", op:WILDCARD}}, count:30) { nodes { modId name } } }";
            var d = await GraphQlAsync(q);
            if (d is null) return null;
            // v0.81.0：捕获 totalCount（调试功能：前端展示"服务端总数 vs 已加载数"）
            try
            {
                var rv0 = d.Value;
                if (rv0.ValueKind == JsonValueKind.Object
                    && rv0.TryGetProperty("mods", out var mv0) && mv0.ValueKind == JsonValueKind.Object
                    && mv0.TryGetProperty("totalCount", out var tv0) && tv0.ValueKind == JsonValueKind.Number)
                    LastBrowseTotalCount = tv0.GetInt32();
            }
            catch { }
            var root = d.Value;
            if (!root.TryGetProperty("mods", out var mods) || mods.ValueKind != JsonValueKind.Object ||
                !mods.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
                return null;
            var keywords = new[] { "translation", "chinese", "japanese", "korean", "francais", "french",
                "german", "deutsch", "spanish", "espanol", "portuguese", "russian", "italian", "polish",
                "czech", "turkish", "mandarin", "kor", "中文", "翻译", "汉化", "简体", "繁体" };
            var list = new List<NexusTranslationItem>();
            foreach (var n in nodes.EnumerateArray())
            {
                var mid = n.TryGetProperty("modId", out var m1) && m1.ValueKind == JsonValueKind.Number ? m1.GetInt32()
                        : int.TryParse(GetStr(n, "modId"), out var p) ? p : 0;
                var nm = GetStr(n, "name");
                if (mid <= 0 || mid == modId || string.IsNullOrWhiteSpace(nm)) continue;
                var low = nm.ToLowerInvariant();
                if (keywords.Any(k => low.Contains(k.ToLowerInvariant())))
                    list.Add(new NexusTranslationItem(nm, mid, null));
            }

            // v0.69.9：批量补封面（同 Requirements 逻辑）
            if (list.Count > 0)
            {
                try
                {
                    var ids = string.Join(",", list.Select(t =>
                        "{gameDomain:\"" + gameDomain + "\", modId:" + t.ModId + "}"));
                    var dc = await GraphQlAsync("{ legacyModsByDomain(ids:[" + ids + "]) { nodes { modId pictureUrl } } }");
                    if (dc is not null && dc.Value.TryGetProperty("legacyModsByDomain", out var lm)
                        && lm.TryGetProperty("nodes", out var cnodes) && cnodes.ValueKind == JsonValueKind.Array)
                    {
                        var picMap = new Dictionary<int, string>();
                        foreach (var n in cnodes.EnumerateArray())
                        {
                            var mid2 = n.TryGetProperty("modId", out var m2) && m2.ValueKind == JsonValueKind.Number ? m2.GetInt32()
                                     : int.TryParse(GetStr(n, "modId"), out var p2) ? p2 : 0;
                            var pic = GetStr(n, "pictureUrl");
                            if (mid2 > 0 && !string.IsNullOrWhiteSpace(pic)) picMap[mid2] = pic;
                        }
                        list = list.Select(t => picMap.TryGetValue(t.ModId, out var pu)
                            ? new NexusTranslationItem(t.Name, t.ModId, pu) : t).ToList();
                    }
                }
                catch { /* 封面缺失不阻塞 */ }
            }
            return list;
        }
        catch { return null; }
    }

    /// <summary>
    /// v0.69.2：抓取 mod 详情页 HTML，解析「许可与致谢 / 译本 / 包含该 mod 的合集」三块
    /// （这三块 v1 REST 与 GraphQL 公开文档均无对应端点，官网页面是服务端渲染的，可直接解析）。
    /// 任一区块解析失败就是空/ null，UI 显示"到官网查看"兜底。
    /// </summary>
    public async Task<NexusModExtras?> GetModPageExtrasAsync(int modId)
    {
        try
        {
            using var res = await SlowHttp.GetAsync(
                $"https://www.nexusmods.com/stardewvalley/mods/{modId}");
            if (!res.IsSuccessStatusCode) return null;
            var html = await res.Content.ReadAsStringAsync();

            // ── 译本：Translations 区块里的 mod 链接 ──
            var translations = new List<NexusLinkItem>();
            var tRegion = ExtractRegion(html, "Translations",
                "Changelogs", "Mods using this mod", "Collections containing this mod", "Posts");
            if (tRegion is not null)
                foreach (var (u, t) in ExtractModLinks(tRegion))
                    translations.Add(new NexusLinkItem(t, "https://www.nexusmods.com" + u, ""));

            // ── 合集：Collections containing this mod / Included in N collections 区块 ──
            var collections = new List<NexusLinkItem>();
            var cRegion = ExtractRegion(html, "Collections containing this mod",
                "Posts", "Bug reports", "Activity logs", "Mod statistics", "</footer");
            if (cRegion is null)
                cRegion = ExtractRegion(html, "Included in", "Posts", "</footer");
            if (cRegion is not null)
            {
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                    cRegion, @"href=""(?<u>/stardewvalley/collections/[a-zA-Z0-9]+)""[^>]*>(?<t>[^<]{1,80})<"))
                {
                    var t = m.Groups["t"].Value.Trim();
                    if (t.Length == 0) continue;
                    // 链接附近找 "N mods" 计数
                    var tail = cRegion.Substring(m.Index, Math.Min(400, cRegion.Length - m.Index));
                    var cm = System.Text.RegularExpressions.Regex.Match(tail, @"(\d[\d,]*)\s*mods");
                    var sub = cm.Success ? cm.Groups[1].Value + " mods" : "";
                    collections.Add(new NexusLinkItem(t, "https://www.nexusmods.com" + m.Groups["u"].Value, sub));
                }
            }

            // ── 许可与致谢：区块内剥标签取纯文本（太长截断）──
            string? permissions = null;
            var pRegion = ExtractRegion(html, "Permissions and credits",
                "Translations", "Changelogs", "Mods using this mod", "Collections containing this mod");
            if (pRegion is not null)
            {
                var txt = System.Text.RegularExpressions.Regex.Replace(pRegion, "<[^>]+>", " ");
                txt = System.Text.RegularExpressions.Regex.Replace(
                    System.Net.WebUtility.HtmlDecode(txt), "\\s+", " ").Trim();
                // 去掉开头的区块标题本身
                txt = System.Text.RegularExpressions.Regex.Replace(txt, "^Permissions and credits\\s*", "");
                if (txt.Length > 30) permissions = txt.Length > 900 ? txt[..900] + "…" : txt;
            }

            return new NexusModExtras(permissions, translations, collections);
        }
        catch { return null; }
    }

    /// <summary>截取 startMarker 到任一 endMarker 之间的 HTML 区域（找不到返回 null）。</summary>
    private static string? ExtractRegion(string html, string startMarker, params string[] endMarkers)
    {
        var i = html.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var end = html.Length;
        foreach (var em in endMarkers)
        {
            var j = html.IndexOf(em, i + startMarker.Length, StringComparison.OrdinalIgnoreCase);
            if (j > i && j < end) end = j;
        }
        var len = Math.Min(end - i, 200000);   // 防御：区域异常大时截断
        return html.Substring(i, len);
    }

    /// <summary>从 HTML 区域里提取 (mod 链接, 显示文本) 对。</summary>
    private static IEnumerable<(string Url, string Text)> ExtractModLinks(string region)
    {
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
            region, @"href=""(?<u>/stardewvalley/mods/\d+)[^""]*""[^>]*>(?<t>[^<]{1,80})<"))
        {
            var t = m.Groups["t"].Value.Trim();
            if (t.Length > 0) yield return (m.Groups["u"].Value, t);
        }
    }

    /// <summary>
    /// v0.69.0：用户下载历史（modId → 最后下载日期 yyyy-MM-dd）。
    /// 这是 v1 遗留端点、官方随时可能下线 —— 任何失败一律返回 null，调用方静默兜底，
    /// 绝不因为这个端点影响详情页打开。
    /// </summary>
    public async Task<Dictionary<int, string>?> GetDownloadHistoryAsync(string apiKey)
    {
        try
        {
            using var res = await SlowHttp.SendAsync(Req(apiKey,
                "https://api.nexusmods.com/v1/user/download_history.json"));
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var map = new Dictionary<int, string>();
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return map;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                // 防御式解析：mod_id 可能平铺也可能嵌在 "mod" 对象里
                var mid = e.TryGetProperty("mod_id", out var m1) && m1.ValueKind == JsonValueKind.Number ? m1.GetInt32()
                        : e.TryGetProperty("mod", out var mo) && mo.ValueKind == JsonValueKind.Object
                            && mo.TryGetProperty("mod_id", out var m2) && m2.ValueKind == JsonValueKind.Number ? m2.GetInt32() : 0;
                if (mid <= 0) continue;
                // 日期字段名历代不一：date / downloaded_at / time，ISO 字符串或 unix 秒都兜
                string? iso = null;
                foreach (var key in new[] { "date", "downloaded_at", "time" })
                {
                    if (!e.TryGetProperty(key, out var dv)) continue;
                    if (dv.ValueKind == JsonValueKind.String
                        && DateTime.TryParse(dv.GetString(), out var dt)) { iso = dt.ToString("yyyy-MM-dd"); break; }
                    if (dv.ValueKind == JsonValueKind.Number && dv.TryGetInt64(out var unix) && unix > 0)
                    { iso = DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("yyyy-MM-dd"); break; }
                }
                if (iso is null) continue;
                if (!map.TryGetValue(mid, out var cur) || string.Compare(iso, cur, StringComparison.Ordinal) > 0)
                    map[mid] = iso;
            }
            return map;
        }
        catch { return null; }
    }

    /// <summary>CDN download URL for a file. NeedsPremium=true on free accounts (HTTP 403).</summary>
    public async Task<NexusDownloadResult> GetDownloadUrlAsync(string apiKey, int modId, long fileId)
    {
        using var res = await Http.SendAsync(Req(apiKey,
            $"{Base}/mods/{modId}/files/{fileId}/download_link.json"));
        if ((int)res.StatusCode == 403) return NexusDownloadResult.PremiumRequired;
        if (!res.IsSuccessStatusCode) return NexusDownloadResult.Fail($"HTTP {(int)res.StatusCode}");

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        foreach (var server in doc.RootElement.EnumerateArray())
            if (server.TryGetProperty("URI", out var u) && u.GetString() is { } uri)
                return NexusDownloadResult.Ok(uri);
        return NexusDownloadResult.Fail("响应里没有下载地址");
    }

    // 大文件下载用单独的长超时客户端（免费账户限速约 1MB/s，
    // 30 秒超时的 API 客户端会把几百 MB 的合集包下载掐断）。
    private static readonly HttpClient DownloadHttp = CreateDownloadClient();

    private static HttpClient CreateDownloadClient()
    {
        var h = new HttpClient();
        h.DefaultRequestHeaders.UserAgent.ParseAdd("JuniGrid-Launcher");
        h.Timeout = TimeSpan.FromMinutes(30);
        return h;
    }

    /// <summary>流式下载：边下边写盘，不像 GetByteArrayAsync 那样整个读进内存。</summary>
    public Task DownloadFileAsync(string url, string destPath) =>
        DownloadFileAsync(url, destPath, null);

    /// <summary>
    /// 带实时进度回调的流式下载，供任务中心展示下载百分比和速度。
    /// progress 为 null 时退化为普通下载。
    /// v1.07：断点续传/自动重试统一走 ResumableDownload（掉连接不再从 0 重下）。
    /// </summary>
    public Task DownloadFileAsync(string url, string destPath,
        IProgress<NexusDownloadProgress>? progress)
    {
        return ResumableDownload.RunAsync(DownloadHttp, url, destPath,
            (msg, pct, spd) => progress?.Report(new NexusDownloadProgress(msg, pct ?? 0, spd)));
    }

    private static string FormatBytes(long bytes)
    {
        double value = bytes;
        string[] units = { "B", "KB", "MB", "GB" };
        int i = 0;
        while (value >= 1024 && i < units.Length - 1)
        {
            value /= 1024;
            i++;
        }
        return value.ToString(i == 0 ? "F0" : "F1") + " " + units[i];
    }

    // ------------------------------------------------------------------
    // nxm:// one-time links (free accounts OK — the key+expires come from
    // the user clicking "Mod Manager Download" on the website)
    // ------------------------------------------------------------------
    public async Task<NexusDownloadResult> GetNxmDownloadUrlAsync(
        string? apiKey, int modId, long fileId, string key, string expires)
    {
        var url = $"{Base}/mods/{modId}/files/{fileId}/download_link.json"
                + $"?key={Uri.EscapeDataString(key)}&expires={Uri.EscapeDataString(expires)}";
        using var res = await Http.SendAsync(Req(apiKey, url));
        if (!res.IsSuccessStatusCode)
        {
            var code = (int)res.StatusCode;
            return NexusDownloadResult.Fail(code is 400 or 401 or 403
                ? $"HTTP {code}（下载凭证与「设置」里的 API Key 所属账号不一致，或链接已过期——请确认应用和网页登录的是同一个 Nexus 账号，然后回网页重新点一次 Mod Manager Download）"
                : $"HTTP {code}（链接可能已过期，回网页重新点一次下载）");
        }

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        foreach (var server in doc.RootElement.EnumerateArray())
            if (server.TryGetProperty("URI", out var u) && u.GetString() is { } uri)
                return NexusDownloadResult.Ok(uri);
        return NexusDownloadResult.Fail("响应里没有下载地址");
    }

    // ------------------------------------------------------------------
    // Browse lists (no full-text search in API v1 — that's v2/OAuth only)
    // ------------------------------------------------------------------
    /// <summary>kind: trending | latest_added | latest_updated</summary>
    public async Task<IReadOnlyList<NexusModListEntry>> GetModListAsync(string apiKey, string kind)
    {
        using var res = await Http.SendAsync(Req(apiKey, $"{Base}/mods/{kind}.json"));
        if (!res.IsSuccessStatusCode) return Array.Empty<NexusModListEntry>();

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var list = new List<NexusModListEntry>();
        foreach (var m in doc.RootElement.EnumerateArray())
        {
            list.Add(new NexusModListEntry(
                m.TryGetProperty("mod_id", out var i) && i.ValueKind == JsonValueKind.Number
                    ? i.GetInt32() : 0,
                GetStr(m, "name"),
                GetStr(m, "summary"),
                GetStr(m, "version"),
                GetInt64(m, "mod_downloads"),
                GetStr(m, "picture_url")));
        }
        return list;
    }

    /// <summary>
    /// v0.77.0：浏览榜单对外入口。
    /// </summary>
    /// <summary>
    /// v0.96.0：时间过滤整体重写 —— 弃用「沿时间流爬取 + 客户端排序」模拟方案，改为与官网完全一致的
    /// 服务端过滤：GraphQL createdAt/updatedAt 过滤值用 Unix 时间戳秒（ISO 字符串在官网 ES 里匹配恒为 0，
    /// 这就是旧注释「日期过滤一加就挂」的真正原因）。实测口径（对照官网 games/{domain}/mods?sort=&timeRange=）：
    /// New      = createdAt 过滤 + createdAt 排序
    /// Updated  = updatedAt 过滤 + updatedAt 排序
    /// Trending = createdAt 过滤 + endorsements 排序（官网默认 timeRange=7，首页 Trending 板块仍用）
    /// Downloads= downloads 排序（原 Trending 榜位，按下载数排序）
    /// Popular  = createdAt 过滤 + downloads 排序（官网 popular 就是下载数，不是推荐数）
    /// Surprise = 无时间过滤 + 服务端 random{seed} 排序（种子稳定保证翻页连续，「换一批」换种子）
    /// 过滤下移服务端后每页都是单次精确查询：totalCount 全时段可用（分页器时间筛选也能显示尾页）。
    /// v1.04.0：去掉「自定义时间区间」（UI 已移除）；新增 direction（ASC/DESC，正序/倒序下拉）。
    /// </summary>
    public async Task<List<NexusModListEntry>?> BrowseModsAsync(string kind, int offset, int count,
        string gameDomain = "stardewvalley", string? searchText = null, string? categoryName = null,
        string timeRange = "all", string direction = "DESC")
    {
        // ─── Surprise 特殊路径：官网同款服务端 random 排序（seed 由 UI「换一批」控制）───
        if (kind == "surprise")
            return await FetchChunkedAsync(kind, offset, count, gameDomain, searchText, categoryName,
                randomSeed: SurpriseSeed);

        // ─── 时间窗口 → epoch 过滤条件（Updated tab 过滤更新时间，其余过滤发布时间，对照官网）───
        long? sinceEpoch = null, untilEpoch = null;
        var dateOnUpdatedAt = kind == "updated";
        if (timeRange != "all")
        {
            var days = timeRange switch { "day" => 1, "week" => 7, "2week" => 14, "month" => 28, "year" => 365, _ => 0 };
            if (days > 0) sinceEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - days * 86400;
        }

        return await FetchChunkedAsync(kind, offset, count, gameDomain, searchText, categoryName,
            sinceEpoch: sinceEpoch, untilEpoch: untilEpoch, dateOnUpdatedAt: dateOnUpdatedAt,
            direction: direction);
    }

    /// <summary>v0.77.0：按 CHUNK 循环补齐到 count 条（服务端截断时续拉凑满）。</summary>
    private async Task<List<NexusModListEntry>?> FetchChunkedAsync(string kind, int offset, int count,
        string gameDomain, string? searchText, string? categoryName,
        long? sinceEpoch = null, long? untilEpoch = null, bool dateOnUpdatedAt = false,
        string direction = "DESC", int? randomSeed = null)
    {
        var all = new List<NexusModListEntry>();
        var seen = new HashSet<int>();
        const int CHUNK = 50;
        var cur = offset;
        var emptyStreak = 0;
        for (var guard = 0; guard < 10 && all.Count < count; guard++)
        {
            var want = Math.Min(CHUNK, count - all.Count);   // v0.88.0：不多要 —— 每页精确条数，末排不再缺
            var batch = await BrowseModsChunkAsync(kind, cur, want, gameDomain, searchText, categoryName,
                sinceEpoch: sinceEpoch, untilEpoch: untilEpoch, dateOnUpdatedAt: dateOnUpdatedAt,
                direction: direction, randomSeed: randomSeed);
            if (batch is null)
            {
                await Task.Delay(400);
                batch = await BrowseModsChunkAsync(kind, cur, want, gameDomain, searchText, categoryName,
                    sinceEpoch: sinceEpoch, untilEpoch: untilEpoch, dateOnUpdatedAt: dateOnUpdatedAt,
                    direction: direction, randomSeed: randomSeed);
                if (batch is null) { await Task.Delay(900); batch = await BrowseModsChunkAsync(kind, cur, want, gameDomain, searchText, categoryName,
                    sinceEpoch: sinceEpoch, untilEpoch: untilEpoch, dateOnUpdatedAt: dateOnUpdatedAt,
                    direction: direction, randomSeed: randomSeed); }
            }
            if (batch is null) return all.Count > 0 ? all : null;
            var added = 0;
            foreach (var e in batch)
                if (seen.Add(e.Id)) { all.Add(e); added++; }
            cur += Math.Max(batch.Count, 1);
            // v0.77.0：单批 0 新增不能立刻判到顶（服务端偶发返回重叠一页）；连续 2 批 0 新增才真到顶
            if (added == 0) { if (++emptyStreak >= 2) break; }
            else emptyStreak = 0;
        }
        return all;
    }

    /// <summary>单批 GraphQL 拉取（原 BrowseModsAsync 实现，仅内部调用）。</summary>
    private async Task<List<NexusModListEntry>?> BrowseModsChunkAsync(string kind, int offset, int count,
        string gameDomain = "stardewvalley", string? searchText = null, string? categoryName = null,
        long? sinceEpoch = null, long? untilEpoch = null, bool dateOnUpdatedAt = false,
        string direction = "DESC", int? randomSeed = null)
    {
        try
        {
            if (!GameIdCache.TryGetValue(gameDomain, out var gameId))
            {
                var g = await GraphQlAsync("{ game(domainName:\"" + gameDomain + "\") { id } }");
                if (g is null) return null;
                var gv = g.Value;
                if (!gv.TryGetProperty("game", out var gg) || gg.ValueKind != JsonValueKind.Object) return null;
                var gidEl = gg.TryGetProperty("id", out var tmp) ? tmp : default;
                gameId = gidEl.ValueKind == JsonValueKind.Number ? gidEl.GetInt32()
                       : int.TryParse(gidEl.ToString(), out var p) ? p : 0;
                if (gameId <= 0) return null;
                GameIdCache[gameDomain] = gameId;
            }

            // v0.96.0：排序对照官网前端映射表（new=createdAt / updated=updatedAt / trending=endorsements
            // / downloads=downloads / popular=downloads / surprise=random）。random 只认 seed 不认 direction。
            // v1.04.0：正序/倒序下拉 → direction（ASC/DESC）；random 忽略方向。
            var dir = string.Equals(direction, "ASC", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";
            string sort = randomSeed is int rs
                ? "random:{seed:" + rs + "}"
                : kind switch
                {
                    "updated"     => $"updatedAt:{{direction:{dir}}}",
                    "trending"    => $"endorsements:{{direction:{dir}}}",
                    "downloads"   => $"downloads:{{direction:{dir}}}",
                    "popular"     => $"downloads:{{direction:{dir}}}",
                    _             => $"createdAt:{{direction:{dir}}}"
                };

            // v0.71.0：过滤条件 —— 恒过滤成人内容；分类；搜索（纯数字=N网尾号精确，否则名称/作者 OR 模糊）
            var conds = new List<string>
            {
                "{gameId:{value:\"" + gameId + "\"}}"
            };
            // 只显示成人内容优先；否则按总开关在关闭时过滤成人内容
            if (OnlyAdultContent)
                conds.Add("{adultContent:{value:true, op:EQUALS}}");
            else if (!IncludeAdultContent)
                conds.Add("{adultContent:{value:false, op:EQUALS}}");
            if (!string.IsNullOrWhiteSpace(categoryName))
                conds.Add("{categoryName:{value:\"" + categoryName.Replace("\"", "") + "\", op:EQUALS}}");
            if (!string.IsNullOrWhiteSpace(searchText))
            {
                var st = searchText.Trim();
                if (int.TryParse(st, out var idNum))
                    // v0.96.0：value 必须是字符串字面量（BaseFilterValue.value: String!），裸数字整个查询直接报错；
                    // 且 modId EQUALS 只在 api-router 端点正常，旧 v2 镜像恒返回空。
                    conds.Add("{modId:{value:\"" + idNum + "\", op:EQUALS}}");
                else
                {
                    var safe = new string(st.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '&').ToArray());
                    if (safe.Length > 0)
                        // v0.98.1：名称/作者/上传者三路 OR —— 搜作者名或上传者名都能列出他做的 mod
                        conds.Add("{filter:[{name:{value:\"" + safe + "\", op:WILDCARD}},"
                                + "{author:{value:\"" + safe + "\", op:WILDCARD}},"
                                + "{uploader:{value:\"" + safe + "\", op:WILDCARD}}], op:OR}");
                }
            }
            // v0.96.0：服务端时间过滤（对照官网）—— 过滤值用 Unix 时间戳秒。
            // 注意 ES 会把它拼成 date:>=<value> 的 Lucene 查询：ISO 字符串里的冒号会撞坏
            // Lucene 语法、纯日期字符串则匹配恒 0，只有纯数字 epoch 能真正命中。
            var dateField = dateOnUpdatedAt ? "updatedAt" : "createdAt";
            if (sinceEpoch is long se)
                conds.Add("{" + dateField + ":{value:\"" + se + "\", op:GTE}}");
            if (untilEpoch is long ue)
                conds.Add("{" + dateField + ":{value:\"" + ue + "\", op:LT}}");
            var sel = "modId name summary version thumbnailUrl pictureUrl downloads endorsements createdAt updatedAt "
                    + "uploader { name avatar } modCategory { name } fileSize";
            var q = "{ mods(filter:{filter:[" + string.Join(",", conds) + "], op:AND}, sort:{" + sort
                + "}, offset:" + offset + ", count:" + count + ") { totalCount nodes { " + sel + " } } }";
            var d = await GraphQlAsync(q);
            if (d is null) return null;
            var root = d.Value;
            if (!root.TryGetProperty("mods", out var mods) || mods.ValueKind != JsonValueKind.Object ||
                !mods.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
                return null;

            // v0.95.0：捕获服务端 totalCount —— 分页器「第 x / N 页 · 共 X 个」的数据源。
            // 同一查询里 totalCount 不随 offset 变化，任意一批捕获的值都等于全结果集大小。
            // v0.96.0：时间过滤下移服务端后，时间筛选也有精确 totalCount（尾页全程可用）。
            if (mods.TryGetProperty("totalCount", out var tv) && tv.ValueKind == JsonValueKind.Number
                && tv.TryGetInt32(out var tcv))
                LastBrowseTotalCount = tcv;

            var list = new List<NexusModListEntry>();
            foreach (var n in nodes.EnumerateArray())
            {
                var mid = n.TryGetProperty("modId", out var m1) && m1.ValueKind == JsonValueKind.Number ? m1.GetInt32()
                        : int.TryParse(GetStr(n, "modId"), out var p) ? p : 0;
                string? up = null, upAv = null;
                if (n.TryGetProperty("uploader", out var uo) && uo.ValueKind == JsonValueKind.Object)
                { up = GetStr(uo, "name"); upAv = GetStr(uo, "avatar"); }
                long fsize = n.TryGetProperty("fileSize", out var fs) && fs.ValueKind == JsonValueKind.Number
                    && fs.TryGetInt64(out var fsl) ? fsl : 0;
                string? cat = null;
                if (n.TryGetProperty("modCategory", out var mc) && mc.ValueKind == JsonValueKind.Object) cat = GetStr(mc, "name");
                long downloads = n.TryGetProperty("downloads", out var dl) && dl.ValueKind == JsonValueKind.Number && dl.TryGetInt64(out var dln) ? dln : 0;
                int endor = n.TryGetProperty("endorsements", out var en) && en.ValueKind == JsonValueKind.Number ? en.GetInt32() : 0;
                list.Add(new NexusModListEntry(
                    mid, GetStr(n, "name"), GetStr(n, "summary"), GetStr(n, "version"), downloads, GetStr(n, "pictureUrl"),
                    GetStr(n, "thumbnailUrl"), up, endor,
                    GetStr(n, "createdAt"), GetStr(n, "updatedAt"), cat, upAv, fsize));
            }
            return list;
        }
        catch { return null; }
    }

    /// <summary>v0.71.0：拉分类 facets（Showcase 4 分类 pills 数据源，含每类 mod 数）。</summary>
    public async Task<Dictionary<string, int>?> GetCategoryFacetsAsync(string gameDomain = "stardewvalley")
    {
        try
        {
            if (!GameIdCache.TryGetValue(gameDomain, out var gameId))
            {
                var g = await GraphQlAsync("{ game(domainName:\"" + gameDomain + "\") { id } }");
                if (g is null) return null;
                var gv = g.Value;
                if (!gv.TryGetProperty("game", out var gg) || gg.ValueKind != JsonValueKind.Object) return null;
                var gidEl = gg.TryGetProperty("id", out var tmp) ? tmp : default;
                gameId = gidEl.ValueKind == JsonValueKind.Number ? gidEl.GetInt32()
                       : int.TryParse(gidEl.ToString(), out var p) ? p : 0;
                if (gameId <= 0) return null;
                GameIdCache[gameDomain] = gameId;
            }
            var d = await GraphQlAsync("{ mods(filter:{gameId:{value:\"" + gameId
                + "\"}}, count:1, facets:{categoryName:[]}) { facetsData } }");
            if (d is null) return null;
            var root = d.Value;
            if (!root.TryGetProperty("mods", out var mods) || mods.ValueKind != JsonValueKind.Object ||
                !mods.TryGetProperty("facetsData", out var fd) || fd.ValueKind != JsonValueKind.Object ||
                !fd.TryGetProperty("categoryName", out var cn) || cn.ValueKind != JsonValueKind.Object)
                return null;
            var dict = new Dictionary<string, int>();
            foreach (var p in cn.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var n))
                    dict[p.Name] = n;
            return dict;
        }
        catch { return null; }
    }

    /// <summary>调 /v1/users/validate.json 拿当前 API Key 对应的账号信息。
    /// v1.08.0 实测：该接口已无 avatar / member_id 字段 —— 用户 id 叫 user_id（读 member_id 恒为 0，
    /// GraphQL 附加信息与头像回填整条链路因此从未跑起来）；头像直链可按
    /// avatars.nexusmods.com/{user_id}/100 构造（当前接口把它错装在 profile_url 字段里，顺手纠正）。</summary>
    public async Task<NexusUser?> ValidateAsync(string apiKey)
    {
        try
        {
            using var res = await Http.SendAsync(Req(apiKey, "https://api.nexusmods.com/v1/users/validate.json"));
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var r = doc.RootElement;
            var memberId = r.TryGetProperty("user_id", out var ui) && ui.ValueKind == JsonValueKind.Number ? ui.GetInt32()
                         : r.TryGetProperty("member_id", out var mi) && mi.ValueKind == JsonValueKind.Number ? mi.GetInt32() : 0;
            var avatar = GetStr(r, "avatar") ?? "";
            if (string.IsNullOrEmpty(avatar) && memberId > 0)
                avatar = $"https://avatars.nexusmods.com/{memberId}/100";
            var profileUrl = GetStr(r, "profile_url") ?? "";
            if (profileUrl.Contains("avatars.nexusmods.com", StringComparison.OrdinalIgnoreCase))
                profileUrl = $"https://www.nexusmods.com/users/{memberId}";
            return new NexusUser(
                GetStr(r, "name") ?? "",
                GetStr(r, "email") ?? "",
                profileUrl,
                avatar,
                r.TryGetProperty("is_premium", out var pp) && pp.ValueKind == JsonValueKind.True,
                memberId);
        }
        catch { return null; }
    }

    /// <summary>v0.70.1：GraphQL user(id) 拉用户扩展信息（tooltip 卡片用）。失败返回 null。</summary>
    public async Task<NexusUserExtras?> GetUserExtrasAsync(int memberId)
    {
        if (memberId <= 0) return null;
        // v1.08.0：user.id 是 Int! —— 旧写法 user(id:"123") 带引号被服务端整体拒绝
        // （Expected type 'Int!'），本查询（含头像回填与悬浮卡统计）因此从未成功过
        var d = await GraphQlAsync(
            "{ user(id:" + memberId + ") { avatar about joined country modCount uniqueModDownloads endorsementsGiven kudos recognizedAuthor verifiedCurator } }");
        if (d is null) return null;
        var u = d.Value;
        if (!u.TryGetProperty("user", out var uu) || uu.ValueKind != JsonValueKind.Object) return null;
        long GetLong(string k) => uu.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
        bool GetBool(string k) => uu.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
        return new NexusUserExtras(
            GetStr(uu, "avatar"),
            GetStr(uu, "about"), GetStr(uu, "joined"), GetStr(uu, "country"),
            (int)GetLong("modCount"), GetLong("uniqueModDownloads"),
            (int)GetLong("endorsementsGiven"), (int)GetLong("kudos"),
            GetBool("recognizedAuthor"), GetBool("verifiedCurator"));
    }

    /// <summary>v1.04.0：查询单个 mod 的上传者（详情页 by 作者名 hover 头像预览用）。
    /// v1.06.1：改走 legacyModsByDomain 单 mod 精确查询 —— 旧的 mods(filter modId EQUALS)
    /// 已被服务端封死（即使带上 gameId 也恒报 "gameId is required when filtering by modId"），
    /// 头像因此永远落空。legacyModsByDomain 实测稳定返回 uploader.name/avatar
    /// （avatar 为 https://avatars.nexusmods.com/<memberId>/100 真实直链）。失败返回 null。</summary>
    public async Task<(string? Name, string? Avatar)?> GetUploaderInfoAsync(int modId, string gameDomain = "stardewvalley")
    {
        try
        {
            var d = await GraphQlAsync("{ legacyModsByDomain(ids:[{gameDomain:\"" + gameDomain
                + "\", modId:" + modId + "}]) { nodes { uploader { name avatar } } } }");
            if (d is null) return null;
            var root = d.Value;
            if (!root.TryGetProperty("legacyModsByDomain", out var lb) || lb.ValueKind != JsonValueKind.Object ||
                !lb.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var n in nodes.EnumerateArray())
            {
                if (n.TryGetProperty("uploader", out var uo) && uo.ValueKind == JsonValueKind.Object)
                    return (GetStr(uo, "name"), GetStr(uo, "avatar"));
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>v1.05.1：详情页作者头像。
    /// v1.06.1：GraphQL legacyModsByDomain 提为主通道 —— 官网 HTML 抓取被 Cloudflare 403
    /// （HttpClient/curl 无论什么 UA 都拦），而 GraphQL 的 avatar 字段实测能返回真实直链，
    /// 之前拿不到是查询本身被服务端拒绝（见 GetUploaderInfoAsync 注释）。HTML 抓取保留兜底。</summary>
    public async Task<string?> GetUploaderAvatarAsync(int modId, string gameDomain = "stardewvalley")
    {
        // ① GraphQL legacyModsByDomain → uploader.avatar（当前唯一稳定通道）
        try
        {
            var up = await GetUploaderInfoAsync(modId, gameDomain);
            var gav = up?.Avatar;
            if (!string.IsNullOrWhiteSpace(gav) && !gav.Contains("/missing", StringComparison.OrdinalIgnoreCase))
                return gav;
        }
        catch { }
        // ② 官网页面 HTML 抓头像兜底（Cloudflare 放行时才有用）
        try
        {
            using var res = await Http.GetAsync($"https://www.nexusmods.com/{gameDomain}/mods/{modId}");
            if (res.IsSuccessStatusCode)
            {
                var html = await res.Content.ReadAsStringAsync();
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                    html, @"https://avatars\.nexusmods\.com/[^""'\s\\]+"))
                {
                    var u = m.Value;
                    if (u.Contains("/missing", StringComparison.OrdinalIgnoreCase)) continue;
                    return u;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>头像图片以 data URI 返回（避免 WebView2 跨域和 Referer 限制）。
    /// v1.05.0：过滤 avatars.nexusmods.com/missing 占位图 —— 无头像用户的 GraphQL avatar
    /// 字段会返回这个占位 URL，拉回来显示的是错误的「N 网占位头像」，改走首字母兜底。</summary>
    public async Task<string?> FetchAvatarAsync(string url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            if (url.Contains("/missing", StringComparison.OrdinalIgnoreCase)) return null;
            using var res = await SlowHttp.GetAsync(url);
            if (!res.IsSuccessStatusCode) return null;
            // v1.08.0：无头像用户的 ID 直链会 307 重定向到 missing 占位图 —— 重定向后的最终 URL
            // 要再查一次（原 URL 不含 /missing，落点含）
            var finalUrl = res.RequestMessage?.RequestUri?.ToString() ?? url;
            if (finalUrl.Contains("/missing", StringComparison.OrdinalIgnoreCase)) return null;
            var bytes = await res.Content.ReadAsByteArrayAsync();
            // avatars CDN 实测回 application/octet-stream —— data URI 统一标成图片类型
            var ct = res.Content.Headers.ContentType?.MediaType ?? "image/png";
            if (ct is null || ct == "application/octet-stream") ct = "image/png";
            return $"data:{ct};base64,{Convert.ToBase64String(bytes)}";
        }
        catch { return null; }
    }
}

public sealed record NexusUser(string Name, string Email, string ProfileUrl, string Avatar, bool IsPremium, int MemberId);

/// <summary>v0.70.1：用户扩展信息（GraphQL user(id)，tooltip 卡片用）。
/// v1.07.0：新增 Avatar —— GraphQL 真实头像直链，validate.json 头像缺失时的回填源。</summary>
public sealed record NexusUserExtras(string? Avatar, string? About, string? Joined, string? Country,
    int ModCount, long UniqueModDownloads, int EndorsementsGiven, int Kudos, bool RecognizedAuthor, bool VerifiedCurator);

public sealed record NexusModInfo(int Id, string Name, string Version, string PictureUrl, int? CategoryId);

/// <summary>vNext：更新检查指纹（GraphQL legacyModsByDomain 批量拉取）。
/// UpdatedAt 是 mod 的最后更新时间（ISO-8601，新文件上传必然带动它变化）——
/// 指纹没变 ⟹ 文件列表没变 ⟹ 上次精查到的最新 MAIN 文件版本仍有效。
/// Version 是 mod 顶层版本（作者手填、可能滞后，仅诊断参考，判定更新仍以文件版本为准）。</summary>
public sealed record NexusModFingerprint(int ModId, string Version, string UpdatedAt, string? PictureUrl);

public sealed record NexusFileInfo(long FileId, string Name, string Version, string Category, long Size = 0);

/// <summary>v0.69.0：一个版本的更新日志。</summary>
public sealed record NexusChangelog(string Version, List<string> Lines);

/// <summary>v0.69.2：页面附加数据里的一条链接（译本/合集共用）。Sub 为附加说明（如"553 mods"）。</summary>
public sealed record NexusLinkItem(string Name, string Url, string Sub);

/// <summary>v0.69.2：mod 页面附加数据（许可与致谢文本 / 译本列表 / 合集列表）。抓不到就为 null，UI 兜底给官网链接。</summary>
public sealed record NexusModExtras(string? PermissionsText, List<NexusLinkItem> Translations, List<NexusLinkItem> Collections);

public sealed record NexusModListEntry(
    int Id, string Name, string Summary, string Version, long Downloads, string PictureUrl,
    string? ThumbnailUrl = null, string? Uploader = null, int Endorsements = 0,
    string? CreatedAt = null, string? UpdatedAt = null, string? Category = null,
    string? UploaderAvatar = null, long FileSize = 0);

public sealed record NexusModDetail(
    int Id, string Name, string Author, string Version, string Summary,
    string DescriptionHtml, string PictureUrl, long Downloads, long Endorsements,
    string UpdatedAt, IReadOnlyList<NexusModDependency>? Dependencies,
    IReadOnlyList<string> ImageUrls);

/// <summary>Nexus 返回的"依赖此 mod 的其他 mod"。availability: published/removed 等。</summary>
public sealed record NexusModDependency(int ModId, string Name, string Availability);

/// <summary>v0.69.3：官网 Requirements 表的一行（GraphQL v2 数据源）。</summary>
public sealed record NexusRequirement(string ModName, string Notes, string Url, int ModId, bool External);

/// <summary>v0.69.7：带封面的 Requirements 行（GraphQL legacyModsByDomain 批量补封面）。</summary>
public sealed record NexusRequirementEx(string ModName, string Notes, string Url, int ModId, bool External, string? PictureUrl);

/// <summary>v0.69.7：译本条（GraphQL mods 搜索得到）。</summary>
public sealed record NexusTranslationItem(string Name, int ModId, string? PictureUrl);

public sealed record NexusDownloadResult(string? Url, string? Error, bool NeedsPremium)
{
    public static NexusDownloadResult Ok(string url) => new(url, null, false);
    public static NexusDownloadResult Fail(string err) => new(null, err, false);

    public static readonly NexusDownloadResult PremiumRequired =
        new(null, "Nexus 免费账户不能通过 API 直接下载（需要 Premium 会员）", true);
}

/// <summary>下载进度快照：文本 + 可选百分比 + 可选瞬时速度（MB/s）。</summary>
public sealed record NexusDownloadProgress(string Message, double Percent, double? SpeedMBps);
