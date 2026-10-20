using System.Text;
using System.Text.Json;

namespace JuniGrid.Services;

public sealed partial class NexusService
{
    /// <summary>当前登录账号信息 —— 直接读 OAuth access token 的 JWT payload。
    /// v1.2.6：不再打 /v1/users/validate.json。那个遗留端点只认老的个人 apikey，我们的 Bearer
    /// 恒得 401，而旧代码拿到 401 后走的就是下面这条 JWT 兜底 —— 等于每次进页白送一个必然失败的
    /// 请求（审核方看到的正是这种无意义流量）。账号信息本来就在 token 里：
    /// sub=用户 id、user.username、user.premium_expiry。</summary>
    public Task<NexusUser?> ValidateAsync()
    {
        var bt = BearerToken;
        var me = string.IsNullOrEmpty(bt) ? null : AccountFromJwt(bt);
        return Task.FromResult(me);
    }

    /// <summary>OAuth 的 access token 本身就是 nexus-user-service 签发的 JWT，payload 里带
    /// sub(用户 id) / user.username / user.membership_roles —— validate.json 不收 Bearer 时用它合成账号信息。</summary>
    private static NexusUser? AccountFromJwt(string jwt)
    {
        try
        {
            var seg = jwt.Split('.');
            if (seg.Length < 2) return null;
            var payload = seg[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            var r = doc.RootElement;
            var memberId = r.TryGetProperty("sub", out var s) && int.TryParse(s.GetString(), out var mid) ? mid : 0;
            if (!r.TryGetProperty("user", out var u) || u.ValueKind != JsonValueKind.Object) return null;
            // 会员与否看 premium_expiry（未开通=0；付费=未来到期时间戳），membership_roles 里的
            // "supporter" 只是社区标识、不代表付费会员，不能拿来判 premium。
            var premium = u.TryGetProperty("premium_expiry", out var pe) && pe.ValueKind == JsonValueKind.Number
                && pe.GetInt64() > DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return new NexusUser(
                u.TryGetProperty("username", out var un) ? un.GetString() ?? "" : "",
                "",
                memberId > 0 ? $"https://www.nexusmods.com/users/{memberId}" : "",
                memberId > 0 ? $"https://avatars.nexusmods.com/{memberId}/100" : "",
                premium,
                memberId);
        }
        catch { return null; }
    }

    /// <summary>v0.70.1：GraphQL user(id) 拉用户扩展信息（tooltip 卡片用）。失败返回 null。
    /// v1.2.6：10 分钟缓存 —— 同一个用户的资料不会一分钟变两次，旧写法每次进页打一次。</summary>
    public async Task<NexusUserExtras?> GetUserExtrasAsync(int memberId)
    {
        if (memberId <= 0) return null;
        return await CachedAsync<NexusUserExtras>(Key("extras|" + memberId), () => GetUserExtrasCoreAsync(memberId));
    }

    private async Task<NexusUserExtras?> GetUserExtrasCoreAsync(int memberId)
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
    /// v1.06.1：GraphQL legacyModsByDomain 是唯一通道 —— 它的 avatar 字段实测能返回真实直链。
    /// （原官网页面 HTML 抓头像的兜底已移除：对 www.nexusmods.com 页面的自动化请求违反 Nexus ToS。）
    /// GraphQL 没有可用头像时返回 null，调用方走首字母占位。</summary>
    public async Task<string?> GetUploaderAvatarAsync(int modId, string gameDomain = "stardewvalley")
    {
        try
        {
            var up = await GetUploaderInfoAsync(modId, gameDomain);
            var gav = up?.Avatar;
            if (!string.IsNullOrWhiteSpace(gav) && !gav.Contains("/missing", StringComparison.OrdinalIgnoreCase))
                return gav;
        }
        catch { }
        return null;
    }

    /// <summary>头像图片以 data URI 返回（避免 WebView2 跨域和 Referer 限制）。
    /// v1.05.0：过滤 avatars.nexusmods.com/missing 占位图 —— 无头像用户的 GraphQL avatar
    /// 字段会返回这个占位 URL，拉回来显示的是错误的「N 网占位头像」，改走首字母兜底。
    /// v1.2.6：加 10 分钟缓存。头像走的是 CDN（不进 API 闸门是对的，它不占 API 配额），
    /// 但旧写法每次进页重新下载 + 重新 base64，正是 Nexus 页「先进页再闪一下头像」的来源。</summary>
    public async Task<string?> FetchAvatarAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Contains("/missing", StringComparison.OrdinalIgnoreCase))
            return null;
        return await CachedAsync<string>("av|" + url, () => FetchAvatarCoreAsync(url));
    }

    private static async Task<string?> FetchAvatarCoreAsync(string url)
    {
        try
        {
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
