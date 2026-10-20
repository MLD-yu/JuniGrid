using System.Text.Json;

namespace JuniGrid.Services;

public sealed partial class NexusService
{
    // ------------------------------------------------------------------
    // Browse lists (no full-text search in API v1 — that's v2/OAuth only)
    // ------------------------------------------------------------------

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
    /// <param name="forceIncludeAdult">
    /// 依赖解析专用：置 true 时<b>无视「显示成人内容」这个展示偏好</b>，成人 mod 也进候选。
    /// 展示偏好只该管给用户看什么列表，不该管我们能不能解析到一个人的依赖 ——
    /// 实测开着过滤搜 "LewdDew" 得 0 条、关掉得 12 条，等于不开那个开关就永远装不上成人 mod 的依赖。
    /// 浏览/搜索页一律不传，行为不变。
    /// </param>
    public async Task<List<NexusModListEntry>?> BrowseModsAsync(string kind, int offset, int count,
        string gameDomain = "stardewvalley", string? searchText = null, string? categoryName = null,
        string timeRange = "all", string direction = "DESC", bool forceIncludeAdult = false)
    {
        // 同参数 10 分钟内直接吃上一份：审核要的就是「同一个用户不要把同一个查询打两遍」，
        // 而这条恰好也是进页卡顿的主因（每次进页重新拉两条链，还要过 2 路闸门排队）。
        // Surprise 榜带服务端随机种子 —— 「换一批」换新种子即新键，翻页种子不变。
        var key = Key("browse|" + kind + "|" + offset + "|" + count + "|" + gameDomain + "|"
            + (searchText ?? "") + "|" + (categoryName ?? "") + "|" + timeRange + "|" + direction + "|"
            + (forceIncludeAdult ? 1 : 0) + "|" + (kind == "surprise" ? SurpriseSeed : 0));
        var got = await CachedAsync<BrowseCache>(key, async () =>
        {
            var items = await BrowseModsCoreAsync(kind, offset, count, gameDomain, searchText,
                categoryName, timeRange, direction, forceIncludeAdult);
            return new BrowseCache(items, LastBrowseTotalCount);
        }, persist: true, sane: c => c.Items is not null);   // 空列表是合法结果（搜不到东西），null 才是失败
        if (got is null || got.Items is null) return null;
        LastBrowseTotalCount = got.Total;   // 分页器读的正是这个字段，命中缓存也必须还原
        return got.Items;
    }

    /// <summary>缓存里存的是「结果 + 服务端 totalCount」，命中后分页器仍显示正确的共 N 个。
    /// internal 只为测试舱能造出「壳非 null、Items 是 null」那种失败形状（U24）。</summary>
    internal sealed record BrowseCache(List<NexusModListEntry>? Items, int? Total);

    private async Task<List<NexusModListEntry>?> BrowseModsCoreAsync(string kind, int offset, int count,
        string gameDomain, string? searchText, string? categoryName,
        string timeRange, string direction, bool forceIncludeAdult)
    {
        // ─── Surprise 特殊路径：官网同款服务端 random 排序（seed 由 UI「换一批」控制）───
        if (kind == "surprise")
            return await FetchChunkedAsync(kind, offset, count, gameDomain, searchText, categoryName,
                randomSeed: SurpriseSeed, forceIncludeAdult: forceIncludeAdult);

        // ─── 时间窗口 → epoch 过滤条件（Updated tab 过滤更新时间，其余过滤发布时间，对照官网）───
        long? sinceEpoch = null;
        var dateOnUpdatedAt = kind == "updated";
        if (timeRange != "all")
        {
            var days = timeRange switch { "day" => 1, "week" => 7, "2week" => 14, "month" => 28, "year" => 365, _ => 0 };
            if (days > 0) sinceEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - days * 86400;
        }

        return await FetchChunkedAsync(kind, offset, count, gameDomain, searchText, categoryName,
            sinceEpoch: sinceEpoch, dateOnUpdatedAt: dateOnUpdatedAt, direction: direction,
            forceIncludeAdult: forceIncludeAdult);
    }

    /// <summary>v0.77.0：按 CHUNK 循环补齐到 count 条（服务端截断时续拉凑满）。</summary>
    private async Task<List<NexusModListEntry>?> FetchChunkedAsync(string kind, int offset, int count,
        string gameDomain, string? searchText, string? categoryName,
        long? sinceEpoch = null, bool dateOnUpdatedAt = false,
        string direction = "DESC", int? randomSeed = null, bool forceIncludeAdult = false)
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
                sinceEpoch: sinceEpoch, dateOnUpdatedAt: dateOnUpdatedAt,
                direction: direction, randomSeed: randomSeed, forceIncludeAdult: forceIncludeAdult);
            if (batch is null)
            {
                await Task.Delay(400);
                batch = await BrowseModsChunkAsync(kind, cur, want, gameDomain, searchText, categoryName,
                    sinceEpoch: sinceEpoch, dateOnUpdatedAt: dateOnUpdatedAt,
                    direction: direction, randomSeed: randomSeed, forceIncludeAdult: forceIncludeAdult);
                if (batch is null)
                {
                    await Task.Delay(900); batch = await BrowseModsChunkAsync(kind, cur, want, gameDomain, searchText, categoryName,
                    sinceEpoch: sinceEpoch, dateOnUpdatedAt: dateOnUpdatedAt,
                    direction: direction, randomSeed: randomSeed, forceIncludeAdult: forceIncludeAdult);
                }
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
        long? sinceEpoch = null, bool dateOnUpdatedAt = false,
        string direction = "DESC", int? randomSeed = null, bool forceIncludeAdult = false)
    {
        try
        {
            // v1.2.6：纯数字＝N网尾号精确查，改走 legacyModsByDomain —— 旧的
            // mods(filter:{modId:{op:EQUALS}}) 已被服务端整条拒（见 BrowseByModIdAsync 注释）。
            // 这条通道不需要 gameId，所以排在 EnsureGameIdAsync 之前。
            if (int.TryParse(searchText?.Trim(), out var idExact) && idExact > 0)
                return await BrowseByModIdAsync(idExact, gameDomain, categoryName, forceIncludeAdult);

            if (await EnsureGameIdAsync(gameDomain) is not { } gameId) return null;

            // v0.96.0：排序对照官网前端映射表（new=createdAt / updated=updatedAt / trending=endorsements
            // / downloads=downloads / popular=downloads / surprise=random）。random 只认 seed 不认 direction。
            // v1.04.0：正序/倒序下拉 → direction（ASC/DESC）；random 忽略方向。
            var dir = string.Equals(direction, "ASC", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";
            string sort = randomSeed is int rs
                ? "random:{seed:" + rs + "}"
                : kind switch
                {
                    "updated" => $"updatedAt:{{direction:{dir}}}",
                    "trending" => $"endorsements:{{direction:{dir}}}",
                    "downloads" => $"downloads:{{direction:{dir}}}",
                    "popular" => $"downloads:{{direction:{dir}}}",
                    _ => $"createdAt:{{direction:{dir}}}"
                };

            // v0.71.0：过滤条件 —— 恒过滤成人内容；分类；搜索（纯数字=N网尾号精确，否则名称/作者 OR 模糊）
            var conds = new List<string>
            {
                "{gameId:{value:\"" + gameId + "\"}}"
            };
            // 成人内容：默认隐藏（显式过滤）；用户主动开启后不加条件 ——
            // 请求随登录用户 Nexus 账号的成人内容设置执行（服务端强制），应用永不覆盖账号偏好
            // forceIncludeAdult：依赖解析这条内部链路不带该条件（见 BrowseModsAsync 注释）
            if (!IncludeAdultContent && !forceIncludeAdult)
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
            var q = "{ mods(filter:{filter:[" + string.Join(",", conds) + "], op:AND}, sort:{" + sort
                + "}, offset:" + offset + ", count:" + count + ") { totalCount nodes { " + CardSel + " } } }";
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

            // 成人内容由服务端 filter 滤掉（关着的时候 adultContent:{value:false} 那条就在），
            // 这条通道不需要本地补滤。
            return ParseModNodes(nodes, hideAdult: false);
        }
        catch { return null; }
    }

    /// <summary>列表卡片要的字段。两条通道（mods(filter) / legacyModsByDomain）共用一份，
    /// 否则尾号搜出来的卡片会比名字搜出来的少字段。
    /// v1.2.6：<c>adultContent</c> 也进公共选择 —— 两条通道都要能知道自己给出的是不是成人页
    /// （mods(filter) 那条在服务端滤，legacyModsByDomain 在本地滤；依赖解析要拿这个标志决定
    /// 能不能把候选交给用户）。</summary>
    private const string CardSel = "modId name summary version thumbnailUrl pictureUrl downloads endorsements createdAt updatedAt "
        + "uploader { name avatar } modCategory { name } fileSize adultContent";
    /// <summary>把 GraphQL 的 nodes 数组转成卡片列表。hideAdult=true 时跳过 adultContent 为真的节点。
    /// internal 只为测试舱能喂真 JSON 进来（两条通道共用这一个解析器，改坏一边就两边都坏）。</summary>
    internal static List<NexusModListEntry> ParseModNodes(JsonElement nodes, bool hideAdult)
    {
        var list = new List<NexusModListEntry>();
        foreach (var n in nodes.EnumerateArray())
        {
            var isAdult = n.TryGetProperty("adultContent", out var ac) && ac.ValueKind == JsonValueKind.True;
            if (hideAdult && isAdult)
                continue;
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
                GetStr(n, "createdAt"), GetStr(n, "updatedAt"), cat, upAv, fsize, isAdult));
        }
        return list;
    }

    /// <summary>
    /// v1.2.9：按作者（Nexus 上传者账号名）列出公开 mod —— 不爬网页，走官网同款 GraphQL。
    /// <para>公开 REST v1 没有「按作者列出 mod」的端点；官方 v2 GraphQL 文档的
    /// <c>ModsFilter.uploader</c>（https://graphql.nexusmods.com/）支持 EQUALS，
    /// 实测大小写不敏感（MLD210 → 1 个，mld210 同样 1 个）。
    /// 只取上传者精确匹配，不拿 <c>author</c>（作者手填的自由文本，可能是「A &amp; B」）。</para>
    /// <para>结果沿用浏览列表的 10 分钟缓存；成人过滤条件与浏览页同一口径。
    /// 不写 <see cref="LastBrowseTotalCount"/> —— 那是浏览页分页器的状态，首页作者模块
    /// 不应该把「共 N 个」改成作者作品数。</para>
    /// </summary>
    public async Task<NexusAuthorMods?> GetModsByUploaderAsync(string uploader, int count = 24,
        string gameDomain = "stardewvalley")
    {
        var name = (uploader ?? "").Trim();
        if (name.Length == 0) return new NexusAuthorMods(new(), 0);
        if (count <= 0) count = 24;
        count = Math.Min(count, 50);   // GraphQL 单批上限；更多作品由「作者主页」承接

        var key = Key("uploader|" + gameDomain + "|" + name.ToLowerInvariant() + "|" + count);
        return await CachedAsync<NexusAuthorMods>(key, async () =>
        {
            if (await EnsureGameIdAsync(gameDomain) is not { } gameId) return null;

            // 用户名只保留不会破坏 GraphQL 字符串字面量的字符（与搜索词的清洗同款）。
            var safe = new string(name.Where(c => char.IsLetterOrDigit(c)
                || c is ' ' or '-' or '_' or '.' or '&').ToArray()).Trim();
            if (safe.Length == 0) return new NexusAuthorMods(new(), 0);

            var conds = new List<string>
            {
                "{gameId:{value:\"" + gameId + "\"}}",
                "{uploader:{value:\"" + safe + "\", op:EQUALS}}"
            };
            if (!IncludeAdultContent)
                conds.Add("{adultContent:{value:false, op:EQUALS}}");

            var q = "{ mods(filter:{filter:[" + string.Join(",", conds) + "], op:AND}, "
                + "sort:{createdAt:{direction:DESC}}, offset:0, count:" + count + ") "
                + "{ totalCount nodes { " + CardSel + " } } }";
            var d = await GraphQlAsync(q);
            if (d is null) return null;
            if (!d.Value.TryGetProperty("mods", out var mods) || mods.ValueKind != JsonValueKind.Object ||
                !mods.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
                return null;

            var items = ParseModNodes(nodes, hideAdult: !IncludeAdultContent);
            var total = items.Count;
            if (mods.TryGetProperty("totalCount", out var tv) && tv.ValueKind == JsonValueKind.Number
                && tv.TryGetInt32(out var tc) && tc >= 0)
                total = tc;
            return new NexusAuthorMods(items, total);
        }, persist: true, sane: c => c.Items is not null);
    }

    /// <summary>纯数字＝N网尾号精确查。<b>只能走 legacyModsByDomain</b>：
    /// <c>mods(filter:{modId:{value:"7416",op:EQUALS}})</c> 已被服务端整条拒，实测 2026-10-03
    /// 五种写法全部失败 —— 应用原样（filter 里本来就带 <c>{gameId:{value:"1303"}}</c>）、
    /// gameId 加 <c>op:EQUALS</c>、去掉 adultContent 条件、modId 改 <c>op:IN</c>（枚举里没这个值）、
    /// 顶层 <c>mods(gameId:)</c>（该字段不接受这个参数），错误恒为
    /// 「gameId is required when filtering by modId」；同一个 filter 里 <c>name:{op:WILDCARD}</c> 照常工作，
    /// 而 <c>modId:{op:WILDCARD}</c> 会返回 10 条无关结果（子串匹配，不能用）。
    /// <para>legacyModsByDomain 一次可查一批，卡片字段它全有；不存在的尾号返回 0 个节点
    /// ＝ 空结果（不是请求失败，别退化成 null 让 UI 报"加载失败"）。分类筛选在本地补做，
    /// 与旧的服务端 <c>categoryName EQUALS</c> 语义对齐。</para></summary>
    private async Task<List<NexusModListEntry>?> BrowseByModIdAsync(int modId, string gameDomain,
        string? categoryName, bool forceIncludeAdult)
    {
        try
        {
            var d = await GraphQlAsync("{ legacyModsByDomain(ids:[{gameDomain:\"" + gameDomain + "\",modId:" + modId
                + "}]) { nodes { " + CardSel + " } } }");
            if (d is null) return null;
            if (!d.Value.TryGetProperty("legacyModsByDomain", out var lb) || lb.ValueKind != JsonValueKind.Object
                || !lb.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
                return null;

            var list = ParseModNodes(nodes, hideAdult: !IncludeAdultContent && !forceIncludeAdult);
            if (!string.IsNullOrWhiteSpace(categoryName))
                list = list.Where(e => string.Equals(e.Category, categoryName, StringComparison.Ordinal)).ToList();
            LastBrowseTotalCount = list.Count;
            return list;
        }
        catch { return null; }
    }
}
