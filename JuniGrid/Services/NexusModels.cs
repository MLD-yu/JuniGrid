namespace JuniGrid.Services;

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

public sealed record NexusModListEntry(
    int Id, string Name, string Summary, string Version, long Downloads, string PictureUrl,
    string? ThumbnailUrl = null, string? Uploader = null, int Endorsements = 0,
    string? CreatedAt = null, string? UpdatedAt = null, string? Category = null,
    string? UploaderAvatar = null, long FileSize = 0,
    // Adult = N 网把这页标了成人内容。开关关着时它决定「能不能把这个候选交给用户」——
    // 依赖解析会故意不带过滤去查（否则不开开关的人永远装不上成人 mod 的依赖），
    // 但查到的成人候选不能直接端出去。
    bool Adult = false);

/// <summary>v1.2.9：某位上传者的公开作品。Total 是服务端 totalCount（可能大于 Items.Count，
/// 模块只展示最新一批，完整列表走作者主页）。</summary>
public sealed record NexusAuthorMods(List<NexusModListEntry> Items, int Total);

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
