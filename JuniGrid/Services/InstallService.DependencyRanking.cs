using System.Text;

namespace JuniGrid.Services;

public sealed partial class InstallService
{
    /// <summary>UID 的作者段（"ChaseXavier.Miku" → "ChaseXavier"）；没有点号或太短（"a.b"）返回 null。</summary>
    private static string? UidAuthorSegment(string uid)
    {
        var dot = uid.IndexOf('.');
        if (dot <= 0) return null;
        var seg = new string(uid[..dot].Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return seg.Length >= 2 ? seg : null;
    }

    /// <summary>某条搜索结果与这个 UID 的"作者亲和度"：
    /// 3=作者段与上传者完全相同 · 2=一方是另一方的前缀（Wildflour ↔ Wildflourmods）·
    /// 1=作者段是上传者的首字母缩写（CF ↔ CyanFire）· 0=对不上。
    /// 为什么必须看作者：搜 "Miku" 按下载数排第一是 lucasedu11 的「Miku skin for Abigail」(4291)，
    /// 而 ChaseXavier.Miku 真正对应的是排第三的「CP_Miku NPC」(4373)。旧写法直接取第一个命中，
    /// 于是弹窗把人指向别人的 mod，用户照装之后「缺少依赖」依旧（实测 2026-09-26）。</summary>
    internal static int AuthorAffinity(string uid, string? uploader)
    {
        var a = UidAuthorSegment(uid);
        if (a is null) return 0;                       // UID 没有作者段 → 无从判，交给名字
        var u = new string((uploader ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (u.Length == 0) return 0;
        if (u == a) return 3;
        // 只认<b>前缀</b>包含，不认任意位置包含。实测 2026-10-13 缓存里躺着一条
        // BFV.CombinedCrops →「Stardew Valley Expanded 1.15.11 Russian」，上传者是随机串
        // "jhmngbfvdcsaxdcfvgbh"，恰好在中间含有 "bfv" 就拿到了亲和度 2（搜 "BFV" 走的是
        // uploader WILDCARD，正是它把这一页灌进候选池的）。"Wildflour" ↔ "Wildflourmods"
        // 这种加尾缀的仍是前缀，那一档判定不受影响。
        if (u.StartsWith(a, StringComparison.Ordinal) || a.StartsWith(u, StringComparison.Ordinal)) return 2;
        // 首字母缩写：上传者的驼峰词首字母串（CyanFire → "cf"）
        var initials = new string((uploader ?? "").Where(char.IsUpper).ToArray()).ToLowerInvariant();
        if (initials.Length >= 2 && initials == a) return 1;
        // 作者改名加尾缀：DLX.PIF 的作者 DeLiXx 后来把 Nexus 账号改成 DeLiXxN —— 缩写从
        // "dlx" 变 "dlxn"，全等判不上了。实测（2026-10-07）搜 "PIF" 第一名就是本体（20537），
        // 却因亲和度 0 被弃 →「未找到对应 mod」。缩写恰好多出一个尾字母时按改名认（置信度同为 1，
        // 真伪仍由下载后的 manifest UniqueID 校验兜底）。
        if (initials.Length == a.Length + 1 && initials.StartsWith(a, StringComparison.Ordinal)) return 1;
        return 0;
    }

    /// <summary>候选名与 UID 名字段的匹配分（0/1）：同一个作者往往挂着一排 mod
    /// （Sharogg 名下光首页就 8 个），全按亲和度排是并列的，只靠下载数决定谁在前 ——
    /// 下载最高那个未必是依赖本体（Sharogg's Panoramas 79k / Tilesheets 109k，换个 mod 就颠倒）。
    /// 名字段（"Sharogg.Tilesheets" → "Tilesheets"）能在这排里指出哪个名字对得上，
    /// 用它当同亲和度内的第二排序键，比下载数靠谱。<b>名字段短于 3 字符不作数</b>，
    /// 否则 "UI"/"Mod" 这种泛词会把一排候选全点亮、等于没排。
    /// <para>多段 UID（<c>PeacefulEnd.Parchment.Core</c>）额外按点号拆开逐段认：整串
    /// "parchmentcore" 在真实页面名里根本不存在（那一页作者就叫它「Parchment」），只按整串比
    /// 等于这类 UID 永远过不了名字闸（实测 2026-10-13）。拆段认要求<b>整词相等</b> ——
    /// "Core" 不许从 "Scoreboard" 里抠出来当命中。</para></summary>
    internal static int NameMatchScore(string uid, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;
        var dot = uid.IndexOf('.');
        if (dot <= 0 || dot >= uid.Length - 1) return 0;
        var tail = uid[(dot + 1)..];
        var normName = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var blob = Alnum(tail);
        if (blob.Length >= 3 && normName.Contains(blob, StringComparison.Ordinal)) return 1;
        var tokens = NameTokens(name);
        foreach (var seg in tail.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var s = Alnum(seg);
            if (s.Length >= 3 && tokens.Contains(s, StringComparer.Ordinal)) return 1;
        }
        return 0;
    }

    /// <summary>从父包说明里作者亲笔写下的 N 网链接中，挑出<b>唯一</b>一个对得上这个依赖的页面。
    /// <para>为什么需要这一道：N 网还有第二种"前置"—— <b>按文件</b>登记的 requirements（网页
    /// Requirements 页那个「Fishing Log requires (1) → Parchment 1.14.0」卡片）。实测它不在任何
    /// 我们能读到的接口里：GraphQL 的 <c>ModRequirements</c> 只有页面级三个字段，文件侧只留了个
    /// <c>requirementsAlert</c> 整数；v1 的 <c>requirements.json</c> 路由已经没了（带着我们自己的
    /// OAuth bearer 打过去是 404，和瞎写一个 bogus 路径返回一模一样，而同一 token 打 files.json 是
    /// 200 —— 不是鉴权问题）；网页直连 403。所以作者关掉了页面级 Requirements
    /// （<c>legacyModRequirementsEnabled=false</c>，50188 与 49525 都是）之后，他亲笔写在说明里的
    /// 那条链接就是仅剩的权威源。</para>
    /// <para>判据不新造：候选页面名与上传者照走 <see cref="SearchHitIdentifies"/>（作者段＋名字段
    /// 双闸），与搜索通道同一把尺。<b>并列一律不认</b> —— 说明里两个链接都过闸时分不出作者指的是
    /// 哪个，与 <see cref="MatchRequirement"/> 的并列处置一致。</para></summary>
    internal static int? PickDescriptionLink(string uid,
        IReadOnlyList<(int Id, string Name, string Uploader)> candidates)
    {
        int? hit = null;
        foreach (var c in candidates)
        {
            if (c.Id <= 0 || !SearchHitIdentifies(uid, c.Name, c.Uploader)) continue;
            if (hit is not null) return null;      // 并列：认不出是谁就不指路
            hit = c.Id;
        }
        return hit;
    }

    /// <summary>按作者亲和度从高到低排候选：同亲和度里再看名字段对不对得上，
    /// 都一样才保持原有的下载数顺序，供展示与下载共用。</summary>
    internal static List<NexusModListEntry> RankByAuthor(string uid, IEnumerable<NexusModListEntry> hits)
        => hits.OrderByDescending(h => AuthorAffinity(uid, h.Uploader))
               .ThenByDescending(h => NameMatchScore(uid, h.Name))
               .ToList();

    /// <summary>搜索通道放行一个候选、把它当成"这个依赖就是它"的<b>唯一</b>通行证：
    /// 作者段和名字段<b>都</b>要对得上。
    /// <para>为什么作者闸单独不够：<see cref="UidSearchTerms"/> 的最后一发兜底词就是作者段
    /// （2026-10-07 为 Sharogg.Tilesheets 加的），它天生把"这个作者名下的全部 mod"灌进候选池，
    /// 而池里每一行的上传者都恰等于作者段 —— 亲和度清一色 3 分。这道闸对<b>它自己制造的噪声</b>
    /// 完全失明，于是排序退回下载数、取第一。实机 2026-10-13：Fishing Logbook 缺前置
    /// <c>PeacefulEnd.Parchment.Core</c>（它的三个名字段搜索词在 N 网全是 0 命中），兜底词
    /// "PeacefulEnd" 返回 44 个 mod，下载第一是 Fashion Sense（410 万），红字于是印成
    /// 「缺少依赖：Fashion Sense」—— 而 Fashion Sense 就装在上一行、还开着。</para>
    /// <para>代价（说清楚，不含糊）：名字段与页面名对不上的<b>真</b>依赖也会被拒，退回
    /// "未找到对应 mod" + 裸 UID、不给链接（例：Nom0ri.RomRas →「Romanceable Rasmodia -
    /// Female Wizard」，页面号其实是对的）。这是有意的取舍 —— 认不出就不指路。</para></summary>
    internal static bool SearchHitIdentifies(string uid, string? name, string? uploader)
        => AuthorAffinity(uid, uploader) > 0 && NameMatchScore(uid, name) > 0;

    /// <summary>配置里存着的一条依赖展示信息，今天还配不配再拿出去用。
    /// <para>判据必须在<b>读取处</b>重跑一遍，光有写入处的闸不够：判据收紧之前存下的错答案
    /// 躺在每个真用户的 junigrid.config.json 里（实测 2026-10-13：
    /// <c>PeacefulEnd.Parchment.Core → "Fashion Sense" / 9969</c>），只改写入处的话，
    /// 老用户重启一百次仍是那一格。</para>
    /// <para>认不过的那条由调用方标成 stale 重新解析；重解析仍认不出就
    /// <see cref="DropStaleDisplay"/> 把它从配置里删掉 —— 自愈，不要求用户手动清配置。</para></summary>
    internal static bool SavedDisplayStillTrustworthy(string uid, DependencyDisplayEntry saved)
        => saved.Source.Equals(DependencySourceRequirements, StringComparison.OrdinalIgnoreCase)
           || SearchHitIdentifies(uid, saved.Name, saved.Uploader);

    // ------------------------------------------------------------------
    // v1.5.2：权威来源 —— 父包在官网登记的 Requirements 表
    // 上面那套「拆词 → 全站搜索 → 按下载数和作者亲和度猜」是降级通道；作者其实早就在
    // N 网填好了「我要求哪几个 mod」，每一行直接带 modId（详情页 01 节用的就是它）。
    // 实测 2026-10-08，43657「Sunrooms and Greenhouses (PIF)」5 行：
    //   1915 Content Patcher · 28423 MMAP · 20537 PIF - Personal Rooms ·
    //   27117 Sharogg's Tilesheets (indoor and outdoor structures) · 2400 SMAPI
    // —— 也就是「官网那排名字能点、我们只能说未找到」的差距所在。
    // ------------------------------------------------------------------
    private static string Alnum(string s)
        => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>模组名按"非字母数字"切成小写整词（"Sharogg's Tilesheets (indoor…)" →
    /// sharogg / s / tilesheets / indoor / …）。整词比对是为了不让 "PIF" 从 "Spiffy" 里
    /// 抠出个子串就算命中。</summary>
    private static List<string> NameTokens(string modName)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        foreach (var c in modName ?? "")
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0) { list.Add(Alnum(sb.ToString())); sb.Clear(); }
        }
        if (sb.Length > 0) list.Add(Alnum(sb.ToString()));
        return list;
    }

    /// <summary>作者段是否出现在模组名的某个整词里（"Sharogg" ↔ "Sharogg's"）。
    /// 只在两边都 ≥3 字符时才允许互相开头 —— 否则 "s"、"mo" 这种碎片词会把谁都判成同作者。</summary>
    private static bool AuthorInName(string authorNorm, string modName)
        => authorNorm.Length >= 3 && NameTokens(modName).Any(t =>
            t == authorNorm
            || (t.Length >= 3 && (t.StartsWith(authorNorm, StringComparison.Ordinal)
                                  || authorNorm.StartsWith(t, StringComparison.Ordinal))));

    /// <summary>模组名里是否带所有格撇号（"DaisyNiko's Tilesheets"）—— 带就说明这名字自己声明了
    /// 一个作者，作者段对不上时不能只靠名字整词就认它。</summary>
    private static bool HasPossessive(string modName)
        => (modName ?? "").IndexOfAny(new[] { '\'', '’' }) >= 0;

    /// <summary>在父包登记的那几行 Requirement 里认出一个依赖 UID 对应哪一行。
    /// 三档判据从强到弱，<b>只有在命中的那一档里唯一命中才敢认，并列一律返回 null</b>
    /// （宁可退回搜索那条老路，也不替用户在一排真前置里挑一个）：
    /// ① 名字段是模组名的一个整词，且作者段也在模组名里；② 名字段是模组名的一个整词
    /// （"DLX.PIF" ↔ "PIF - Personal Rooms"）；③ 名字段的连写出现在模组名去掉所有分隔符后
    /// （"Pathoschild.ContentPatcher" ↔ "Content Patcher"）。
    /// ②③ 这两档没有作者佐证，所以模组名带所有格撇号（名字自己报了作者）而作者段又对不上时
    /// 直接不认 —— 实测 25552「Balcony (PIF)」的前置里有 4736「DaisyNiko's Tilesheets」，
    /// 少了这道闸，"Sharogg.Tilesheets" 会被整词命中领到 DaisyNiko 的页面上去。
    /// 名字段短于 3 字符不作数（与 <see cref="NameMatchScore"/> 同一道闸）。
    /// 场外需求（External）没有 modId，本来就不算候选。</summary>
    internal static NexusRequirementEx? MatchRequirement(string uid, IReadOnlyList<NexusRequirementEx> reqs)
    {
        var dot = uid.IndexOf('.');
        if (dot <= 0 || dot >= uid.Length - 1) return null;
        var nameNorm = Alnum(uid[(dot + 1)..]);
        if (nameNorm.Length < 3) return null;
        var authorNorm = Alnum(uid[..dot]);

        var rows = reqs.Where(r => r.ModId > 0 && !r.External).ToList();
        if (rows.Count == 0) return null;

        bool WholeWord(NexusRequirementEx r) => NameTokens(r.ModName).Contains(nameNorm);
        bool Corroborated(NexusRequirementEx r)
            => AuthorInName(authorNorm, r.ModName) || !HasPossessive(r.ModName);
        bool Concatenated(NexusRequirementEx r)
            => string.Concat(NameTokens(r.ModName)).Contains(nameNorm, StringComparison.Ordinal);

        var wordAndAuthor = rows.Where(r => WholeWord(r) && AuthorInName(authorNorm, r.ModName)).ToList();
        var wholeWord = rows.Where(r => WholeWord(r) && Corroborated(r)).ToList();
        var concatenated = rows.Where(r => Concatenated(r) && Corroborated(r)).ToList();

        foreach (var tier in new[] { wordAndAuthor, wholeWord, concatenated })
        {
            if (tier.Count == 1) return tier[0];
            if (tier.Count > 1) return null;   // 并列：这一档分不出是谁，往下走只会更不准
        }
        return null;
    }

    /// <summary>「这一页给不给得出要求的版本」—— 依赖目标的唯一通行证。
    /// <para>判据是<b>逐字相等</b>，不是 ≥：作者 manifest 里的 <c>MinimumVersion</c> 是他从某一页
    /// 的"文件版本"栏抄下来的原话（N 网那栏是作者手填的自由文本，<c>0.6.4-unofficial-mushymato.0</c>
    /// 这种串不会撞车）。用 ≥ 会把任何把文件版本填成 <c>1.0.0</c> 的页放进来 —— 那是猜，不是判。</para></summary>
    internal static bool PageProvidesVersion(
        IReadOnlyList<(string Version, string Category)> rows, string? requiredVersion)
    {
        var need = (requiredVersion ?? "").Trim();
        return need.Length > 0 && rows.Any(r => VersionUtil.Same(r.Version, need));
    }

    /// <summary>任一要求串逐字命中即算这一页能供货（同一个依赖常被几个父包各自要求不同版本，
    /// 实机：完美手册要 <c>0.6.4-unofficial-mushymato.0</c>、牲畜集市要 <c>0.6.3-unofficial-mushymato.1</c>，
    /// 两串都在 43861 的文件表里）。</summary>
    internal static bool PageProvidesAnyVersion(
        IReadOnlyList<(string Version, string Category)> rows, IReadOnlyList<string> requiredVersions)
        => requiredVersions.Any(need => PageProvidesVersion(rows, need));
    /// <summary>UID → 搜索词：取 UniqueID 末段（作者段搜索噪音太大），驼峰拆词优先
    /// （"Pathoschild.ContentPatcher" → "Content Patcher" → "ContentPatcher" → 全 UID）。
    /// 末尾补一发作者段兜底：小众作者的名字段是通用词时（Sharogg.Tilesheets → 搜
    /// "Tilesheets"），按下载量排前 8 全是 DaisyNiko/Lumisteria 那种大热门，本体根本进不了
    /// 候选池；而搜 "Sharogg" 第一名就是它（实测 2026-10-07）。多出来的噪音候选由两道下游闸
    /// 消化：安装有下载后的 manifest UniqueID 校验；展示有 <see cref="SearchHitIdentifies"/>
    /// —— 注意后者<b>必须</b>带名字段那一半：作者段兜底词灌进来的整池候选，作者亲和度全是满分，
    /// 只过作者闸等于让这一发兜底词自己给自己投票（实机 2026-10-13，见该方法的注释）。
    /// internal 只为测试舱能拿它拼出与线上同序的搜索词（夹具回归用）。</summary>
    internal static IEnumerable<string> UidSearchTerms(string uid)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dot = uid.IndexOf('.');
        if (dot > 0 && dot < uid.Length - 1)
        {
            var namePart = uid[(dot + 1)..].Trim();
            if (namePart.Length > 0)
            {
                var camel = CamelSplit(namePart);
                if (camel.Length > 0 && seen.Add(camel)) yield return camel;
                if (seen.Add(namePart)) yield return namePart;
            }
            var author = uid[..dot].Trim();
            if (author.Length >= 2 && seen.Add(author)) yield return author;
            if (seen.Add(uid)) yield return uid;
        }
        else if (seen.Add(uid)) yield return uid;
    }

    /// <summary>驼峰拆词："BroadcastAPI" → "Broadcast API"（小写→大写与"大写后跟小写"处断开）。</summary>
    private static string CamelSplit(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (i > 0 && char.IsUpper(c)
                && (char.IsLower(s[i - 1])
                    || (i + 1 < s.Length && char.IsLower(s[i + 1]) && !char.IsWhiteSpace(s[i - 1]))))
                sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }
}
