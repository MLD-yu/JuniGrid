using System.Net.Http;
using System.Text.Json;

namespace JuniGrid.Services;

public sealed partial class NexusService
{
    /// <summary>拉取官网 Requirements（要求/依赖表格）。失败返回 null，由 UI 给"到官网查看"兜底。</summary>
    public async Task<List<NexusRequirement>?> GetModRequirementsAsync(int modId, string gameDomain = "stardewvalley")
    {
        if (await EnsureGameIdAsync(gameDomain) is not { } gameId) return null;

        var d = await GraphQlAsync(
            "{ mod(gameId:\"" + gameId + "\", modId:\"" + modId + "\") { modRequirements { nexusRequirements { nodes { modName notes url modId externalRequirement } } } } }");
        if (d is null) return null;
        var root = d.Value;
        if (!root.TryGetProperty("mod", out var mod) || mod.ValueKind != JsonValueKind.Object ||
            !mod.TryGetProperty("modRequirements", out var mreq) || mreq.ValueKind != JsonValueKind.Object ||
            !mreq.TryGetProperty("nexusRequirements", out var nreq) || nreq.ValueKind != JsonValueKind.Object ||
            !nreq.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
            return null;

        return ParseRequirementNodes(nodes);
    }

    /// <summary>Requirements 的 nodes 数组 → 行。服务端把 <c>modId</c> 当<b>字符串</b>发
    /// （实测 2026-10-08：43657 的 5 行全是 <c>"20537"</c> 这种），所以走 <see cref="GetStr"/>
    /// 再 <c>TryParse</c>，不能按数字取。拆出来是为了让测试舱能拿真实返回喂真解析器。</summary>
    internal static List<NexusRequirement> ParseRequirementNodes(JsonElement nodes)
    {
        var list = new List<NexusRequirement>();
        foreach (var n in nodes.EnumerateArray())
            list.Add(new NexusRequirement(
                GetStr(n, "modName"), GetStr(n, "notes"), GetStr(n, "url"),
                int.TryParse(GetStr(n, "modId"), out var rid) ? rid : 0,
                n.TryGetProperty("externalRequirement", out var ex) && ex.ValueKind == JsonValueKind.True));
        return list;
    }

    /// <summary>某一页的<b>文件版本表</b>（免 key GraphQL）。回答的是"这一版到底在不在这一页"——
    /// 这是唯一机器可读的依据：官网要求表是作者手填的、可以整页不存在（实机 2026-10-12：
    /// 完美手册 45150 的 <c>legacyModRequirementsEnabled=false</c>，那张表永远不会有内容，
    /// 而它正文里那句 "Download and install StardewUI Continued." 连链接都没有），
    /// 但作者 manifest 里那个 <c>MinimumVersion</c> 串，是他从某一页的"文件版本"栏抄下来的。</summary>
    /// <param name="includeOld">默认带上 OLD_VERSION / ARCHIVED。认"这一版在不在这一页"时它们同样算数
    /// —— 要求的那一版常常正是被顶下去的那一条（实测 43861：<c>0.6.4-unofficial-mushymato.0</c> 在
    /// OLD_VERSION，MAIN 已经是 <c>.1</c>）。只要"当前可下载的那一版"时传 false。</param>
    public async Task<List<(string Version, string Category)>?> GetModFileVersionsAsync(
        int modId, string gameDomain = "stardewvalley", bool includeOld = true)
    {
        if (modId <= 0) return null;
        if (await EnsureGameIdAsync(gameDomain) is not { } gameId) return null;
        var d = await GraphQlAsync("{ modFiles(modId:" + modId + ", gameId:\"" + gameId
            + "\") { uid version category } }");
        if (d is null) return null;
        // modFiles 直接返回 ModFile[]，没有 nodes 包装（实测 2026-10-12）
        if (!d.Value.TryGetProperty("modFiles", out var mf) || mf.ValueKind != JsonValueKind.Array)
            return null;
        var rows = ParseFileRows(mf);
        return includeOld ? rows
            : rows.Where(r => r.Category.Equals("MAIN", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>文件表数组 → (版本串, 分类)。版本栏空的行丢掉（作者没填，拿它比版本只会误判）。</summary>
    internal static List<(string Version, string Category)> ParseFileRows(JsonElement arr)
    {
        var list = new List<(string Version, string Category)>();
        foreach (var n in arr.EnumerateArray())
        {
            var v = GetStr(n, "version").Trim();
            if (v.Length == 0) continue;
            list.Add((v, GetStr(n, "category")));
        }
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

    /// <summary>按名字在 N 网搜 mod（免 Key 的 GraphQL WILDCARD 通配），返回最像的那条且排除本体。
    /// 用在 SMAPI 兼容性表只给了替代版**名字**、链接位是 <c>(#)</c> 占位的场合
    /// （实测 "⚠ use [Friends Forever (1.6)](#) instead." → N 网 20702 是独立的一个 mod，
    /// 而本体是 1738 —— 只开本体页面会让人以为"替代版跟现在这个一模一样"）。
    /// <para>清洗规则与 <see cref="GetModTranslationsAsync"/> 相反：那边削掉括号点号无所谓，
    /// 这里**必须保留** —— 实测 "Friends Forever (1.6)" 命中 1 条，削成 "Friends Forever 16" 命中 0 条。
    /// 所以只剥会破坏 GraphQL 字符串字面量的字符。</para></summary>
    public async Task<(int ModId, string Name)?> FindModByNameAsync(
        string name, int excludeModId = 0, string gameDomain = "stardewvalley")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (await EnsureGameIdAsync(gameDomain) is not { } gameId) return null;
            var safe = new string(name.Where(c => !new[] { '"', '\\', '\r', '\n', '{', '}' }.Contains(c)).ToArray()).Trim();
            if (safe.Length == 0) return null;
            // adultContent 必须是 name 的**兄弟**条件；塞进 name 那个对象里 GraphQL 直接不认
            var adult = IncludeAdultContent ? "" : ", adultContent:{value:false, op:EQUALS}";
            var q = "{ mods(filter:{gameId:{value:\"" + gameId + "\"}, name:{value:\"" + safe
                + "\", op:WILDCARD}" + adult + "}, count:20) { nodes { modId name } } }";
            var d = await GraphQlAsync(q);
            if (d is null || !d.Value.TryGetProperty("mods", out var mods)
                || mods.ValueKind != JsonValueKind.Object
                || !mods.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
                return null;
            (int ModId, string Name)? first = null, exact = null;
            var norm = NormalizeForNameMatch(safe);
            foreach (var n in nodes.EnumerateArray())
            {
                var mid = n.TryGetProperty("modId", out var m1) && m1.ValueKind == JsonValueKind.Number
                    ? m1.GetInt32() : 0;
                var nm = GetStr(n, "name");
                if (mid <= 0 || mid == excludeModId || string.IsNullOrWhiteSpace(nm)) continue;
                first ??= (mid, nm);
                if (NormalizeForNameMatch(nm) == norm) { exact = (mid, nm); break; }
            }
            return exact ?? first;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Nexus", "按名字搜 mod 失败(" + name + "): " + ex.Message);
            return null;
        }
    }

    /// <summary>名字比对用的归一化：只留小写字母数字，括号/点/空格全抹掉
    /// （"Friends Forever (1.6)" 与 "Friends Forever 1.6" 视为同一个名字）。</summary>
    private static string NormalizeForNameMatch(string s) =>
        new string(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>v0.69.7：译本 —— 用 mods 搜索按主 mod 名匹配翻译版本（官网 HTML 抓取被 403 反爬挡死）。</summary>
    public async Task<List<NexusTranslationItem>?> GetModTranslationsAsync(int modId, string modName, string gameDomain = "stardewvalley")
    {
        try
        {
            if (await EnsureGameIdAsync(gameDomain) is not { } gameId) return null;
            // 主名做通配搜索；结果里排除本体，只留带翻译语义的
            var safeName = new string((modName ?? "")
                .Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_').ToArray()).Trim();
            if (string.IsNullOrEmpty(safeName)) return new List<NexusTranslationItem>();
            // v1.2.6：这条名字通配搜以前不带成人条件 —— 详情页的「译本」卡片因此能把成人 mod
            // 列给关着开关的用户看（Nexus 一审正是点名这条）。与 FindModByNameAsync 同款处理。
            var adultCond = IncludeAdultContent ? "" : ", adultContent:{value:false, op:EQUALS}";
            var q = "{ mods(filter:{gameId:{value:\"" + gameId + "\"}, name:{value:\"" + safeName
                + "\", op:WILDCARD}" + adultCond + "}, count:30) { nodes { modId name } } }";
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

    // 原 GetModPageExtrasAsync（v0.69.2）及其 HTML 区块解析（ExtractRegion / ExtractModLinks）已移除：
    // 它们抓取 www.nexusmods.com 页面并正则解析「译本 / 合集 / 许可致谢」区块，
    // 违反 Nexus ToS 对自动化站点抓取的禁令 —— 详情页改为直接给出官网页面链接。

    /// <summary>
    /// v0.69.0：用户下载历史（modId → 最后下载日期 yyyy-MM-dd）。
    /// 这是 v1 遗留端点、官方随时可能下线 —— 任何失败一律返回 null，调用方静默兜底，
    /// 绝不因为这个端点影响详情页打开。
    /// </summary>
    public async Task<Dictionary<int, string>?> GetDownloadHistoryAsync()
    {
        try
        {
            using var res = await SendApiAsync(SlowHttp,
                "https://api.nexusmods.com/v1/user/download_history.json");
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
    public async Task<NexusDownloadResult> GetDownloadUrlAsync(int modId, long fileId,
        CancellationToken ct = default, bool patient = false)
    {
        // v1.09.0：安装流程传 patient=true 走 SlowHttp(45s)。download_link 是玩家点了
        // "安装"之后立刻要的那一下，15s 快速失败通道在 Nexus 边缘抖动（实测 TTFB
        // 5.8s、历史 1.2–26.5s）下会把"值得等"的请求误杀成失败 —— 而它后面紧跟着
        // 几十 MB 的下载，失败在这一步代价最大。批量/后台轮询仍走 15s 快速失败。
        using var res = await SendApiAsync(patient ? SlowHttp : Http,
            $"{Base}/mods/{modId}/files/{fileId}/download_link.json", ct: ct);
        if ((int)res.StatusCode == 403) return NexusDownloadResult.PremiumRequired;
        if (!res.IsSuccessStatusCode) return NexusDownloadResult.Fail($"HTTP {(int)res.StatusCode}");

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        foreach (var server in doc.RootElement.EnumerateArray())
            if (server.TryGetProperty("URI", out var u) && u.GetString() is { } uri)
                return NexusDownloadResult.Ok(uri);
        return NexusDownloadResult.Fail(LocService.Tr("响应里没有下载地址"));
    }

    // 大文件下载用单独的长超时客户端（免费账户限速约 1MB/s，
    // 30 秒超时的 API 客户端会把几百 MB 的合集包下载掐断）。
    private static readonly HttpClient DownloadHttp = CreateDownloadClient();

    private static HttpClient CreateDownloadClient()
    {
        var h = new HttpClient();
        h.DefaultRequestHeaders.UserAgent.ParseAdd($"JuniGrid/{AppInfo.Version}");
        // AUP 那两个头也带上：它打的是 CDN 不是 api.nexusmods.com，但少一个"这客户端是谁、什么版本"
        // 的自报家门就多一轮问答（审核方上次就盯着版本头问过）。
        h.DefaultRequestHeaders.TryAddWithoutValidation("Application-Name", "JuniGrid");
        h.DefaultRequestHeaders.TryAddWithoutValidation("Application-Version", AppInfo.Version);
        h.Timeout = TimeSpan.FromMinutes(30);
        return h;
    }

    /// <summary>
    /// 带实时进度回调的流式下载，供任务中心展示下载百分比和速度。
    /// progress 为 null 时退化为普通下载。
    /// v1.07：断点续传/自动重试统一走 ResumableDownload（掉连接不再从 0 重下）。
    /// </summary>
    public Task DownloadFileAsync(string url, string destPath,
        IProgress<NexusDownloadProgress>? progress, CancellationToken ct = default)
    {
        return ResumableDownload.RunAsync(DownloadHttp, url, destPath,
            (msg, pct, spd) => progress?.Report(new NexusDownloadProgress(msg, pct ?? 0, spd)),
            ct: ct);
    }

    // v1.1.6：私有 FormatBytes 副本已删（无任何调用者）—— 统一用 ResumableDownload.FormatBytes。

    // ------------------------------------------------------------------
    // nxm:// one-time links (free accounts OK — the key+expires come from
    // the user clicking "Mod Manager Download" on the website)
    // ------------------------------------------------------------------
    public async Task<NexusDownloadResult> GetNxmDownloadUrlAsync(
        int modId, long fileId, string key, string expires)
    {
        var url = $"{Base}/mods/{modId}/files/{fileId}/download_link.json"
                + $"?key={Uri.EscapeDataString(key)}&expires={Uri.EscapeDataString(expires)}";
        using var res = await SendApiAsync(Http, url);
        if (!res.IsSuccessStatusCode)
        {
            var code = (int)res.StatusCode;
            return NexusDownloadResult.Fail(code is 400 or 401 or 403
                ? LocService.Tf("HTTP {0}（下载凭证与当前登录的 Nexus 账号不一致，或链接已过期——请确认应用和网页登录的是同一个账号，然后回网页重新点一次 Mod Manager Download）", code)
                : $"HTTP {code}（链接可能已过期，回网页重新点一次下载）");
        }

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        foreach (var server in doc.RootElement.EnumerateArray())
            if (server.TryGetProperty("URI", out var u) && u.GetString() is { } uri)
                return NexusDownloadResult.Ok(uri);
        return NexusDownloadResult.Fail(LocService.Tr("响应里没有下载地址"));
    }
}
