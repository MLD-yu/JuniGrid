using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;

namespace JuniGrid.Services;

public sealed partial class NexusService
{
    /// <summary>Newest MAIN file (fallback: newest file of any category).</summary>
    /// <param name="installedFolder">本机已装的包目录名。一个 mod 挂多条 MAIN 时（实测 mod 1839：
    /// CP 主包 182304 与散 xnb 素材包 182306 同日发布），只按上传时间会选中玩家没装的那条，
    /// 于是点「更新」永远装不到真正的包、⇧ 也永远消不掉。给了目录名就优先选与它同名的 MAIN。</param>
    public async Task<NexusFileInfo?> GetLatestMainFileAsync(int modId,
        bool patient = false, string? installedFolder = null,
        CancellationToken ct = default)
    {
        var all = await ListFilesAsync(modId, patient, ct);
        return all is null ? null : PickMainFile(all, installedFolder);
    }

    /// <summary>按 fileId 精确取一条文件 —— .nxm 一键安装记的是「玩家点的那一条」，不是最新那一条，
    /// 只有 files.json 里对得上 fileId 的版本号才是他实际装到的版本。</summary>
    public async Task<NexusFileInfo?> GetFileByIdAsync(int modId, long fileId, bool patient = false,
        CancellationToken ct = default)
    {
        var all = await ListFilesAsync(modId, patient, ct);
        return all?.FirstOrDefault(x => x.F.FileId == fileId).F;
    }

    private async Task<List<(NexusFileInfo F, long Ts)>?> ListFilesAsync(int modId, bool patient,
        CancellationToken ct = default)
    {
        using var res = await SendApiAsync(patient ? SlowHttp : Http, $"{Base}/mods/{modId}/files.json", ct: ct);
        if (!res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("files", out var files)) return null;

        var list = new List<(NexusFileInfo, long)>();
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
            list.Add((new NexusFileInfo(id, name, ver, cat, size), ts));
        }
        return list;
    }

    /// <summary>从一条 mod 的文件列表里挑「代表这个 mod 的那一条文件」。</summary>
    private static NexusFileInfo? PickMainFile(IReadOnlyList<(NexusFileInfo F, long Ts)> all, string? installedFolder)
    {
        if (all.Count == 0) return null;
        var mains = all.Where(x => x.F.Category.Equals("MAIN", StringComparison.OrdinalIgnoreCase)).ToList();
        var bestMain = mains.Count > 0 ? PickMainForInstall(mains, installedFolder) : null;
        // 肖像向可选文件（名字带 xnb/png/portrait 且体积不太小）——MAIN 可能是
        // 空壳/说明包时，安装管线要能下到真正的素材文件
        var assets = all.Where(x => x.F.Size >= 16 * 1024 && LooksLikeAssetPack(x.F.Name)).ToList();
        var bestPortrait = assets.Count > 0 ? assets.MaxBy(x => x.Ts).F : null;
        var bestAny = all.MaxBy(x => x.Ts).F;
        // MAIN 优先；MAIN 异常小且存在更像素材包的可选文件时改用后者
        if (bestMain is { } m && (m.Size <= 0 || m.Size >= 16 * 1024 || bestPortrait is null))
            return m;
        return bestPortrait ?? bestMain ?? bestAny;
    }

    /// <summary>多条 MAIN 里选一条：与已装包目录同名的优先，其余按上传时间取新。</summary>
    internal static NexusFileInfo? PickMainForInstall(
        IReadOnlyList<(NexusFileInfo F, long Ts)> mains, string? installedFolder)
    {
        var hint = NormPkgName(installedFolder);
        NexusFileInfo? best = null;
        long bestTs = -1;
        foreach (var (f, ts) in mains)
        {
            var hit = hint.Length > 0 && NormPkgName(f.Name) == hint;
            var bestHit = best is not null && NormPkgName(best.Name) == hint;
            if (hit && !bestHit || hit == bestHit && ts > bestTs) { best = f; bestTs = ts; }
        }
        return best;
    }

    /// <summary>包名归一：只留字母数字（顺带吃掉 [CP] / 空格 / 下划线 / 版本号点号），
    /// 让磁盘目录「[CP] Portrait Anime Mods OhoDavi」与 N 网文件名「CP Portrait Anime Mods OhoDavi」相等。</summary>
    private static string NormPkgName(string? s)
        => string.IsNullOrWhiteSpace(s) ? "" : new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static bool LooksLikeAssetPack(string name)
    {
        var nlow = name.ToLowerInvariant();
        return nlow.Contains("xnb") || nlow.Contains("portrait")
            || nlow.Contains("肖像") || nlow.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase)
            || nlow.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
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
            // 串行分批 —— 旧 Task.WhenAll 扇出会一次打开多个 GraphQL POST；
            // 集中 ApiGate 只允许 2 路并发，串行也避免与精查 files.json 抢通道
            for (var i = 0; i < ids.Count; i += CHUNK)
            {
                var slice = ids.Skip(i).Take(CHUNK).ToList();
                var idArgs = string.Join(",", slice.Select(id =>
                    "{gameDomain:\"" + gameDomain + "\", modId:" + id + "}"));
                var d = await GraphQlAsync(
                    "{ legacyModsByDomain(ids:[" + idArgs + "]) { nodes { modId version updatedAt pictureUrl"
                    + (IncludeAdultContent ? "" : " adultContent") + " } } }");
                if (d is null) return null;
                var root = d.Value;
                if (!root.TryGetProperty("legacyModsByDomain", out var lb)
                    || lb.ValueKind != JsonValueKind.Object
                    || !lb.TryGetProperty("nodes", out var nodes)
                    || nodes.ValueKind != JsonValueKind.Array)
                    return null;
                foreach (var n in nodes.EnumerateArray())
                {
                    var mid = n.TryGetProperty("modId", out var m1) && m1.ValueKind == JsonValueKind.Number
                        ? m1.GetInt32()
                        : int.TryParse(GetStr(n, "modId"), out var p) ? p : 0;
                    if (mid <= 0) continue;
                    // v1.2.6：这条通道是匿名 GraphQL，服务端不知道账号的成人设置 ⇒ 自己滤。
                    // 只撤封面图，不撤版本号：这些 mod 是用户自己装的，把整条记录删掉会让更新检查
                    // 静默报「没有更新」，那才是把事实藏了；而封面是纯展示，跟着开关走。
                    var adult = n.TryGetProperty("adultContent", out var ac) && ac.ValueKind == JsonValueKind.True;
                    result[mid] = new NexusModFingerprint(
                        mid, GetStr(n, "version"), GetStr(n, "updatedAt"),
                        adult ? null : GetStr(n, "pictureUrl"));
                }
            }
            return result;
        }
        catch { return null; }
    }

    /// <summary>v0.69.0：mod 的更新日志（版本 → 变更行）。对应官网 LOGS 页签的 Changelogs。</summary>
    public async Task<List<NexusChangelog>?> GetChangelogsAsync(int modId)
    {
        using var res = await SendApiAsync(SlowHttp, $"{Base}/mods/{modId}/changelogs.json");
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

    // ══════════════════════════════════════════════════════════════════
    // v0.69.5：Requirements 走 GraphQL v2（修复 v0.69.3 手工拼 JSON 内层引号未转义
    // 导致请求体非法、接口 400、UI 永远卡在 shimmer 的 bug —— 改用 JsonSerializer）。
    // ══════════════════════════════════════════════════════════════════
    // v0.96.0：切到官网同款 api-router 端点 —— 旧 api.nexusmods.com/v2 的 modId EQUALS(数字尾号搜索)
    // 在它上面永远返回空，api-router 上正常；其余字段/形状完全兼容（官网前端也走这条）。
    private const string GraphQlEndpoint = "https://api-router.nexusmods.com/graphql";
    /// <summary>v0.79.0：成人内容总开关 —— false=浏览/搜索 GraphQL 追加 adultContent:false 过滤条件。
    /// 由 ConfigService 按「设置 → 显示成人内容」开关同步（默认关）。
    /// 开启时不加任何成人条件：请求随登录用户在 Nexus 账号上的成人内容设置执行（服务端强制），
    /// 应用永不覆盖账号偏好 —— Nexus AUP 要求。
    /// v1.1.6：UI 线程写 / GraphQL 后台线程读，改走 Volatile 属性避免撕裂读。</summary>
    private static bool _includeAdultContent = false;
    public static bool IncludeAdultContent
    {
        get => Volatile.Read(ref _includeAdultContent);
        set => Volatile.Write(ref _includeAdultContent, value);
    }
    /// <summary>成人过滤条件的版本号：开关每实际变化一次 +1（ConfigService.SyncAdultFilter 维护）。
    /// Nexus 页快照存下取数时的版本，返回时版本对不上说明快照是旧过滤条件拉的数据 → 弃用重拉。</summary>
    private static int _adultFilterVersion;
    public static int AdultFilterVersion => Volatile.Read(ref _adultFilterVersion);
    /// <summary>开关实际变化时递增版本号（原子；属性只读，递增入口收敛在这里）。</summary>
    public static void BumpAdultFilterVersion() => Interlocked.Increment(ref _adultFilterVersion);
    /// <summary>v0.81.0：最近一次榜单查询服务端报告的 totalCount（分页器「第 x / N 页 · 共 X 个」的数据源）。
    /// v1.1.6：改 Volatile 存储（-1 哨兵 = null），GraphQL 线程写 / UI 线程读。</summary>
    private int _lastBrowseTotalCount = -1;
    public int? LastBrowseTotalCount
    {
        get => Volatile.Read(ref _lastBrowseTotalCount) is int v && v >= 0 ? v : null;
        set => Volatile.Write(ref _lastBrowseTotalCount, value is int v ? v : -1);
    }
    /// <summary>v0.96.0：Surprise 榜服务端 random 排序种子 —— 翻页期间保持不变保证页序连续，「换一批」时换新种子。</summary>
    public int SurpriseSeed { get; private set; } = Random.Shared.Next();
    public void ReshuffleSurprise() => SurpriseSeed = Random.Shared.Next();
    private static readonly ConcurrentDictionary<string, int> GameIdCache = new();

    private async Task<JsonElement?> GraphQlAsync(string query)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { query });
            // GraphQL 浏览公开（不带 Bearer）——仍走集中闸门（并发 + 429）
            using var res = await SendApiAsync(Http, GraphQlEndpoint, HttpMethod.Post, body, useBearer: false);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            // v0.76.0：GraphQL 语法/参数报错时响应是 200 + {"errors":[...],"data":null} ——
            // data 字段【存在但为 null】也必须当失败处理，否则下方解析抛异常、降级重试永远不触发
            // （这就是选时间段/Trending 必然「拉取失败」的根因）。
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind == JsonValueKind.Null)
                return null;
            return data.Clone();
        }
        catch (Exception ex)
        {
            // v1.1.6：不再完全静默 —— GraphQL 是浏览/搜索/详情的唯一通道，留一行日志便于诊断
            AppLog.Warn("Nexus", "GraphQL 请求失败: " + ex.Message);
            return null;
        }
    }

    /// <summary>gameDomain → 内部 gameId（带缓存）。v1.1.6：原先 4 个调用点各自复制了
    /// 一份「GraphQL 查询 + 解析 + 回填缓存」块，统一收敛到这里。失败返回 null。</summary>
    private async Task<int?> EnsureGameIdAsync(string gameDomain)
    {
        if (GameIdCache.TryGetValue(gameDomain, out var gameId)) return gameId;
        var g = await GraphQlAsync("{ game(domainName:\"" + gameDomain + "\") { id } }");
        if (g is null) return null;
        var gv = g.Value;
        if (!gv.TryGetProperty("game", out var gg) || gg.ValueKind != JsonValueKind.Object) return null;
        var gidEl = gg.TryGetProperty("id", out var tmp) ? tmp : default;
        gameId = gidEl.ValueKind == JsonValueKind.Number ? gidEl.GetInt32()
               : int.TryParse(gidEl.ToString(), out var p) ? p : 0;
        if (gameId <= 0) return null;
        GameIdCache[gameDomain] = gameId;
        return gameId;
    }
}
