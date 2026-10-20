using System.Collections.Concurrent;

namespace JuniGrid.Services;

public sealed partial class InstallService
{
    /// <summary>父包 Requirements 表的进程内缓存（父 modId → 行）。
    /// <b>只缓存服务端答出来的</b>（含"作者确实一行没填"这个空表）；请求失败不缓存，
    /// 下次还会再问 —— 否则一次网络抖动会把某个父包永久钉成"没有权威答案"。
    /// 空表在下游就是"这条链给不出答案"，与搜索降级同一条出路。</summary>
    private static readonly ConcurrentDictionary<int, List<NexusRequirementEx>> ParentReqCache = new();

    private async Task<List<NexusRequirementEx>> ParentRequirementsAsync(int parentModId)
    {
        if (ParentReqCache.TryGetValue(parentModId, out var cached)) return cached;
        List<NexusRequirementEx>? got = null;
        try { got = await _nexus.GetModRequirementsWithCoversAsync(parentModId); }
        catch { }
        if (got is null) return new List<NexusRequirementEx>();
        ParentReqCache[parentModId] = got;
        return got;
    }

    /// <summary>这个依赖在<b>所有</b>声明它的父包的 Requirements 里能不能认出唯一一行。
    /// 行取并集而不是"第一个父包说了算"：同一个名字段在不同父包的前置里可以各自命中一行
    /// （Sharogg 与 DaisyNiko 各有一个 Tilesheets），并起来才看得出谁带作者佐证。</summary>
    private async Task<NexusRequirementEx?> MatchDeclaredRequirement(
        string uid, IReadOnlyList<int>? parentModIds)
    {
        if (parentModIds is null) return null;
        var union = new List<NexusRequirementEx>();
        var seen = new HashSet<int>();
        foreach (var pid in parentModIds.Distinct())
        {
            if (pid <= 0) continue;
            foreach (var r in await ParentRequirementsAsync(pid))
                if (r.ModId > 0 && seen.Add(r.ModId)) union.Add(r);
        }
        return MatchRequirement(uid, union);
    }


    /// <summary>为「装了仍不满足」的依赖找出真正有那一版的那一页。
    /// 逐个候选查文件版本表，<see cref="PageProvidesAnyVersion"/> 命中即返回；
    /// 一个都不命中返回 <c>null</c> —— 调用方必须就此罢手（说"找不到来源"），
    /// <b>不许</b>退回"就用本机这份来自的那一页"：那条退路正是实机 2026-10-12 那个 bug
    /// （完美手册要 <c>0.6.4-unofficial-mushymato.0</c>，我们反复把用户指回 focustense 的
    /// 28870，而那页最高只有 <c>0.6.1</c>）。
    /// <para>查询失败（返回 null）与"查到了但没有"分开处理：前者跳过不定罪，后者才算排除。</para></summary>
    public async Task<int?> FindVersionProviderAsync(string uid, IReadOnlyList<string> requiredVersions,
        IEnumerable<int> candidateModIds)
    {
        var needs = (requiredVersions ?? Array.Empty<string>())
            .Select(x => (x ?? "").Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (needs.Count == 0) return null;
        var label = string.Join("、", needs);
        var checked_ = new List<string>();
        foreach (var id in candidateModIds.Where(x => x > 0).Distinct())
        {
            var rows = await _nexus.GetModFileVersionsAsync(id);
            if (rows is null) { checked_.Add(id + "=查询失败"); continue; }
            if (PageProvidesAnyVersion(rows, needs))
            {
                AppLog.Info("Install", $"[依赖溯源] {uid} 要求 {label} → 命中页面 {id}"
                    + $"（此前查过：{(checked_.Count > 0 ? string.Join(" ", checked_) : "无")}）");
                return id;
            }
            checked_.Add(id + "=" + (rows.Count > 0
                ? "最高 " + rows.Select(r => r.Version)
                    .Aggregate((a, b) => VersionUtil.IsNewer(b, a) ? b : a)
                : "无文件"));
        }
        AppLog.Warn("Install", $"[依赖溯源] {uid} 要求 {label}：{checked_.Count} 个候选页都没有这一版，"
            + $"不再指向任何页面（查过的页：{string.Join(" ", checked_)}）"
            + " —— 该版本可能只在 GitHub 等站外发布");
        return null;
    }

    /// <summary>「这个依赖要的那一版到底在哪一页」—— 唯一的入口，两条调用方（Mods 页的溯源、
    /// 一键安装依赖）都必须走它，避免一处记得查搜索、另一处忘了。
    /// <para>候选池 = 本机那份来自的页 + 父包要求表里登记的页 + 两个持久缓存 + <b>按依赖名的全站搜索</b>。
    /// 最后一项是唯一的来路：fork 复用上游 UniqueID 时（实机 2026-10-12 mushymato 的
    /// 「StardewUI Continued」43861 沿用 <c>focustense.StardewUI</c>），前三项全都只指向原作者那一页，
    /// 而那一页整页最高 0.6.1、给不出要求的 0.6.4-unofficial-mushymato.0。</para>
    /// <para>返回 null = 没有任何一页<em>逐字</em>含有要求的版本串。调用方必须就此罢手 ——
    /// 不给页面号、不指回本机那份来自的页。</para></summary>
    public async Task<(int Id, string Name)?> FindDependencyProviderAsync(
        JuniGridConfig cfg, string uid, IReadOnlyList<string> requiredVersions,
        int? installedNexusId, IReadOnlyList<int>? parentModIds)
    {
        var needs = (requiredVersions ?? Array.Empty<string>())
            .Select(x => (x ?? "").Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (needs.Count == 0) return null;

        var pool = new List<int>();
        if (installedNexusId is int ins && ins > 0) pool.Add(ins);
        if (parentModIds is not null) pool.AddRange(parentModIds);
        if (cfg.DependencyDisplays.TryGetValue(uid, out var dd) && dd.ModId > 0) pool.Add(dd.ModId);
        if (cfg.DependencyNexusIds.TryGetValue(uid, out var kn) && kn > 0) pool.Add(kn);
        foreach (var c in await SearchDependencyCandidatesAsync(uid))
            if (c.Id > 0) pool.Add(c.Id);

        var found = await FindVersionProviderAsync(uid, needs, pool);
        if (found is not int id) return null;
        var name = (await _nexus.GetModAsync(id))?.Name;
        return (id, string.IsNullOrWhiteSpace(name) ? $"Mod #{id}" : name!.Trim());
    }

    /// <summary>v1.2.3：展示信息进程内缓存 —— 同会话反复打开弹窗不再重搜（mod 名/封面基本不变）。</summary>
    private static readonly ConcurrentDictionary<string, DependencyDisplayInfo> DisplayCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>解析单个依赖 UID 的展示信息：进程内缓存 → 配置持久缓存 → <b>父包官网登记的
    /// Requirements 表</b> → <b>父包说明里作者亲笔的 N 网链接</b> → 免 key 全站搜索。
    /// Requirements 那条是作者自己填的权威映射，认出来就直接给页面号；作者把这一栏关了
    /// （<c>legacyModRequirementsEnabled=false</c>）时还剩说明里那条链接是作者亲笔的；
    /// 两者都给不出唯一一行时，才降级到搜索 —— 而搜索的命中必须过
    /// <see cref="SearchHitIdentifies"/>（作者段＋名字段双闸）才敢拿出来当"这个依赖是谁"。
    /// 认不出就返回 null（弹窗退回显示裸 UID，不再给出可点的错误页面），并顺手把
    /// 老版本按"下载数第一"存下来的错答案从配置里清掉（自愈，不靠用户手动删配置）。</summary>
    public async Task<DependencyDisplayInfo?> ResolveDependencyDisplayAsync(
        string uid, IReadOnlyList<int>? parentModIds = null)
    {
        if (DisplayCache.TryGetValue(uid, out var cached)) return cached;
        // v1.2.4：配置持久缓存 —— 跨会话同步出结果，重启后弹窗首开不再等搜索
        var cfg = _cfg.Current;
        var staleSaved = false;
        if (cfg.DependencyDisplays.TryGetValue(uid, out var saved))
        {
            // 可信闸在读取处重跑一遍（判据收紧前存下的错答案就躺在用户配置里）
            if (SavedDisplayStillTrustworthy(uid, saved))
            {
                var fromSaved = new DependencyDisplayInfo(uid,
                    saved.Name.Length > 0 ? saved.Name : null,
                    saved.ModId > 0 ? saved.ModId : null,
                    saved.CoverUrl.Length > 0 ? saved.CoverUrl : null);
                DisplayCache[uid] = fromSaved;
                return fromSaved;
            }
            staleSaved = true;   // 缓存里没有上传者、或上传者与 UID 作者段对不上 → 重新解析
        }
        try
        {
            // ① 权威：拿父包登记的 Requirements 认这一行
            if (await MatchDeclaredRequirement(uid, parentModIds) is { } req)
            {
                var byReq = new DependencyDisplayInfo(uid, req.ModName, req.ModId,
                    string.IsNullOrWhiteSpace(req.PictureUrl) ? null : req.PictureUrl);
                DisplayCache[uid] = byReq;
                SaveDisplay(cfg, uid, byReq, DependencySourceRequirements, "");
                return byReq;
            }
            // ② 父包说明里作者亲笔的 N 网链接（页面级 Requirements 被作者关掉时仅剩的权威源）
            if (await MatchDescriptionLinkAsync(uid, parentModIds) is { } viaDesc)
            {
                DisplayCache[uid] = viaDesc.Info;
                SaveDisplay(cfg, uid, viaDesc.Info, DependencySourceDescription, viaDesc.Uploader);
                return viaDesc.Info;
            }
            // ③ 降级：全站搜索。放行判据是作者段＋名字段双闸（见 SearchHitIdentifies）——
            // 只过作者闸等于让兜底搜索词自己给自己投票。
            var ranked = RankByAuthor(uid, await SearchDependencyCandidatesAsync(uid));
            var top = ranked.FirstOrDefault(h => h.Id > 0 && SearchHitIdentifies(uid, h.Name, h.Uploader));
            if (top is null)
            {
                if (staleSaved) DropStaleDisplay(cfg, uid);
                return null;
            }
            var info = new DependencyDisplayInfo(uid, top.Name, top.Id,
                string.IsNullOrWhiteSpace(top.ThumbnailUrl) ? top.PictureUrl : top.ThumbnailUrl);
            DisplayCache[uid] = info;
            // 回写持久缓存（连上传者一起存，下次会话能直接判它可不可信），写失败不影响本次展示
            SaveDisplay(cfg, uid, info, "", top.Uploader ?? "");
            return info;
        }
        catch
        {
            if (staleSaved) DropStaleDisplay(cfg, uid);
            return null;
        }
    }

    private const string DependencySourceRequirements = "requirements";
    private const string DependencySourceDescription = "description";

    /// <summary>取父包说明里的 N 网链接，逐条查那一页自己的名字与上传者，交给
    /// <see cref="PickDescriptionLink"/> 定唯一赢家；null = 这条链给不出答案，调用方继续降级。
    /// <para>链接数封顶 12 条（与搜索候选池同一个上限）：说明里贴几十个链接的页面很常见，
    /// 不封顶就等于为一次展示发几十个请求。</para>
    /// <para>存进配置时带上真上传者，所以读取处的 <see cref="SavedDisplayStillTrustworthy"/>
    /// 会照旧重跑双闸 —— 这一道不享受 requirements 那种"按来源免检"。</para></summary>
    private async Task<(DependencyDisplayInfo Info, string Uploader)?> MatchDescriptionLinkAsync(
        string uid, IReadOnlyList<int>? parentModIds)
    {
        if (parentModIds is null) return null;
        foreach (var pid in parentModIds.Distinct())
        {
            if (pid <= 0) continue;
            var parent = await _nexus.GetModPageEvidenceAsync(pid);
            if (parent is null) continue;
            var links = NexusService.DescriptionModLinks(parent.DescriptionBbCode)
                .Where(id => id != pid).Take(12).ToList();
            if (links.Count == 0) continue;
            var evById = new Dictionary<int, NexusService.ModPageEvidence>();
            var cands = new List<(int Id, string Name, string Uploader)>();
            foreach (var id in links)
            {
                var ev = await _nexus.GetModPageEvidenceAsync(id);
                if (ev is null) continue;              // 查询失败：既不定罪，也不算命中
                evById[id] = ev;
                cands.Add((id, ev.Name, ev.UploadedBy));
            }
            if (PickDescriptionLink(uid, cands) is not int picked) continue;
            var win = evById[picked];
            // 留一行可定位的日志：这一道靠作者亲笔的链接，出问题时得能看出是哪一页、哪条链接
            AppLog.Info("Install", $"[依赖解析] {uid} ← 父包 {pid} 说明里的链接 → 页面 {picked}「{win.Name}」"
                + $"（上传者 {win.UploadedBy}）");
            return (new DependencyDisplayInfo(uid, win.Name, picked,
                string.IsNullOrWhiteSpace(win.PictureUrl) ? null : win.PictureUrl), win.UploadedBy);
        }
        return null;
    }

    /// <summary>把一次解析结果写进持久缓存。写失败不影响本次展示。</summary>
    private void SaveDisplay(JuniGridConfig cfg, string uid, DependencyDisplayInfo info,
        string source, string uploader)
    {
        try
        {
            cfg.DependencyDisplays[uid] = new DependencyDisplayEntry
            {
                Name = info.Name ?? "",
                ModId = info.ModId ?? 0,
                CoverUrl = info.CoverUrl ?? "",
                Uploader = uploader,
                Source = source,
            };
            _cfg.Save(cfg);
        }
        catch { }
    }

    /// <summary>清掉一条不可信的持久缓存（旧规则按下载数存进来的错答案，如 ChaseXavier.Miku → 4291）。</summary>
    private void DropStaleDisplay(JuniGridConfig cfg, string uid)
    {
        if (!cfg.DependencyDisplays.Remove(uid)) return;
        try { _cfg.Save(cfg); } catch { }
        AppLog.Info("Install", $"[依赖解析] 丢弃错认的缓存 {uid}（旧规则按下载数取首个命中）");
    }

    /// <summary>已知候选（v1.2.3：弹窗预解析 + 配置缓存）：零搜索开销直接用。只有第一个
    /// 候选解析一次显示名（过程日志可读），其余以 "Mod #id" 兜底；校验仍在下载后执行，
    /// 候选错了也装不进 Mods。</summary>
    private async Task<List<NexusModListEntry>> KnownCandidatesAsync(
        JuniGridConfig cfg, string uid, IReadOnlyDictionary<string, int>? preResolved,
        int? installedNexusId = null, IReadOnlyList<int>? parentModIds = null)
    {
        var ids = new List<int>();
        var seen = new HashSet<int>();
        // 本机就装着这个依赖（「版本过旧」那条路径）：它自己 manifest 的 UpdateKeys 就是权威答案，
        // 拿它当第一候选，既省一次官网搜索，也不会把名字撞上的别家 mod 装进来。
        if (installedNexusId is > 0 && seen.Add(installedNexusId.Value)) ids.Add(installedNexusId.Value);
        // 父包在官网 Requirements 里登记的那一行：作者自己填的映射，比"弹窗预解析/历史缓存"
        // 更新鲜，也远比全站搜索的"疑似"可靠 —— 排在它们前面。
        if (await MatchDeclaredRequirement(uid, parentModIds) is { } req && seen.Add(req.ModId))
            ids.Add(req.ModId);
        if (preResolved is not null && preResolved.TryGetValue(uid, out var pre) && pre > 0 && seen.Add(pre))
            ids.Add(pre);
        if (cfg.DependencyNexusIds.TryGetValue(uid, out var known) && known > 0 && seen.Add(known))
            ids.Add(known);
        if (ids.Count == 0) return new List<NexusModListEntry>();
        var firstName = "";
        try
        {
            var info = await _nexus.GetModAsync(ids[0]);
            if (!string.IsNullOrWhiteSpace(info?.Name)) firstName = info!.Name;
        }
        catch { }
        return ids.Select(id => new NexusModListEntry(
            id, id == ids[0] && firstName.Length > 0 ? firstName : $"Mod #{id}", "", "", 0, "")).ToList();
    }

    /// <summary>官网搜索候选（v1.2.3：已知候选全落空时的降级通道。免 key GraphQL，
    /// 按下载数排序）。候选只是"疑似"，真正放行靠下载后校验 manifest UniqueID。</summary>
    private async Task<List<NexusModListEntry>> SearchDependencyCandidatesAsync(string uid)
    {
        var list = new List<NexusModListEntry>();
        var seen = new HashSet<int>();
        foreach (var term in UidSearchTerms(uid))
        {
            List<NexusModListEntry>? hits = null;
            try
            {
                // forceIncludeAdult：这是"能不能解析到依赖"，不是"给用户看什么列表"。
                // 带上展示偏好的话，不开「显示成人内容」的人永远装不上成人 mod 的依赖
                // （实测搜 "LewdDew" 带过滤 0 条、不带 12 条）。
                hits = await _nexus.BrowseModsAsync("downloads", 0, 8, "stardewvalley",
                    searchText: term, forceIncludeAdult: true);
            }
            catch { }
            if (hits is null) continue;
            foreach (var h in hits)
            {
                if (h.Id <= 0 || !seen.Add(h.Id)) continue;
                // 用户没开「显示成人内容」⇒ 成人候选不端出去（解析可以查得到，但不能替他决定看什么）。
                // 落空后调用方走既有的「未找到 → 开搜索页人工确认」，不会把人领到成人页上。
                if (h.Adult && !NexusService.IncludeAdultContent)
                {
                    AppLog.Info("Install", "依赖候选 " + h.Id + " 被标成人内容，按用户设置跳过");
                    continue;
                }
                list.Add(h);
            }
            if (list.Count >= 12) break;   // 候选上限，防跑飞
        }
        // 作者段能对上的候选排前面：下载数高≠就是它（搜 "Miku" 下载第一是别人的皮肤包）
        return RankByAuthor(uid, list);
    }

    /// <summary>premiumBlocked 快速通道专用：只找一个候选 id 生成手动链接
    /// （本机已装 → 父包登记的 Requirements → 已知缓存 → 搜索）。
    /// 搜索来的结果过 <see cref="SearchHitIdentifies"/>（作者段＋名字段双闸）；
    /// 一个都不过时宁可返回 null（转"未找到"开搜索页人工确认），
    /// 也不能把人领到同作者另一个 mod 的页面上 —— 那条链接是"人工确认"的入口，
    /// 喂给它一个错页等于让人确认一个错答案。</summary>
    private async Task<NexusModListEntry?> FirstCandidateAsync(
        JuniGridConfig cfg, string uid, IReadOnlyDictionary<string, int>? preResolved,
        IReadOnlyList<int>? parentModIds = null)
    {
        var known = await KnownCandidatesAsync(cfg, uid, preResolved, parentModIds: parentModIds);
        if (known.Count > 0) return known[0];
        var hits = await SearchDependencyCandidatesAsync(uid);
        return hits.FirstOrDefault(h => SearchHitIdentifies(uid, h.Name, h.Uploader));
    }
}
