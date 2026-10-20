using System.IO;

namespace JuniGrid.Services;

public sealed partial class ModService
{
    // ------------------------------------------------------------------
    // 覆盖型包（汉化补丁这类没有 manifest.json、只往别的 mod 上盖文件的包）
    // ------------------------------------------------------------------

    private const string OverlayBackupSuffix = ".junigrid_backup";

    /// <summary>RelPaths = 文件在<b>包里</b>的相对路径；DstPaths 非 null 时是逐文件对应的<b>宿主内</b>
    /// 落点（汉化包把 zh.json 压在包根目录时要搬进 i18n/），null = 两边同路径。</summary>
    internal sealed record OverlayPlan(string Host, List<string> RelPaths, string PackRoot, List<string>? DstPaths = null);

    internal enum OverlayFail
    {
        None,
        /// <summary>包内文件都是光秃秃一层（裸 zh.json），没说宿主是谁。</summary>
        NoHostInPack,
        /// <summary>包内摆着好几个别的 mod 的目录（跨顶层，或顶层认不出而第二层有好几家），
        /// 而<b>不是每一家都唯一对上已装的 mod</b>：有的名字本机没装着，或两个目录对上同一行。
        /// 名字都点得出来，问题在【只盖一半会让这个包半生效】，所以整包不装。</summary>
        MultiHost,
        /// <summary>认出了宿主名，但用户没装它。</summary>
        HostNotInstalled,
        /// <summary>配置项汉化包点名的字段，好几家已装 mod 都声明了 ⇒ 认不出该盖谁。</summary>
        AmbiguousConfigHost,
        /// <summary>整包只有一个裸 json、宿主由包自己页面的前置认出来了、本机也装着它，
        /// 但这份 json 的条目跟宿主 i18n 里的对不上 ⇒ 它不是这个 mod 的汉化，盖上去不会生效。
        /// 与 <see cref="NoHostInPack"/> 分开说：那句是"我们认不出"，这句是"认出来了、内容不对"。</summary>
        DeclaredHostKeyMismatch,
        /// <summary>整包只有一个翻译 json，而它的条目在本机<b>没有任何</b>已装 mod 的 i18n 里过半出现
        /// ⇒ 它对应的那个 mod 没装（实测 45719/49340 是「UI Info Suite 2 <b>Alternative</b>」的汉化，
        /// 本机装的是原版 UIInfoSuite2，298 个键只撞得上 95 个）。与 <see cref="NoHostInPack"/>
        /// 分开说：那句是"我们认不出"，这句是"认得出没装着的是谁"。</summary>
        HostKeysUnmatched,
    }

    /// <summary>一次扫描的覆盖健康度汇总：ByHost 给列表徽标，StaleKeys 给清单弹窗逐行判定。</summary>
    public sealed record OverlayReport(
        Dictionary<string, (int Total, int Stale)> ByHost,
        HashSet<string> StaleKeys);

    /// <summary>宿主目录可能是 Mods/X，也可能是被禁用的 Mods/.X（SetDisabled 用点前缀）。
    /// 记录里统一存不带点的名字，找的时候两种都试 —— 与立绘页那条点名容错同款。</summary>
    /// <summary>宿主路径归一：统一分隔符、去首尾斜杠、去掉禁用改名的首段点号。
    /// 记录键、徽标分组、卸载连带清理三处必须用同一个形状，否则同一个宿主会分裂成两个键。</summary>
    public static string NormalizeOverlayHost(string host)
    {
        var s = host.Replace('\\', '/').Trim('/');
        return s.StartsWith('.') ? s[1..] : s;
    }

    /// <summary>记录键里的来源槽位（第三段）；同一个宿主文件上可以挂多条，靠它分组。</summary>
    public static string OverlaySlotOf(string key)
    {
        var i = key.LastIndexOf('|');
        return i < 0 ? key : key[(i + 1)..];
    }

    /// <summary>宿主目录：支持「顶层」和「捆绑包/子包」两种深度。禁用只把顶层改名（X → .X），
    /// 但每一段都试一次带点写法，别名捆绑包（.Bundle/Child）里的子包也能命中。</summary>
    private static string? ResolveHostDir(string gamePath, string host)
    {
        var rel = NormalizeOverlayHost(host);
        if (rel.Length == 0) return null;
        var cur = Path.Combine(gamePath, "Mods");
        foreach (var seg in rel.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var hit = Directory.Exists(Path.Combine(cur, seg)) ? seg
                : Directory.Exists(Path.Combine(cur, "." + seg)) ? "." + seg : null;
            if (hit is null) return null;
            cur = Path.Combine(cur, hit);
        }
        return Directory.Exists(cur) ? cur : null;
    }

    /// <summary>在已安装 mod 里找<b>唯一</b>一个叶子目录名等于 leaf 的子包（形如 捆绑包/子包）。
    /// 命中 0 个或多个都不返回 —— 猜错的表现是盖到隔壁同名子包上，宁可放弃。</summary>
    private static string? FindInstalledSubpackageByLeaf(string gamePath, string leaf)
    {
        string? found = null;
        var hits = 0;
        try
        {
            foreach (var topDir in Directory.EnumerateDirectories(Path.Combine(gamePath, "Mods")))
            {
                var topName = Path.GetFileName(topDir);
                if (topName.Equals(".junigrid_trash", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var child in Directory.EnumerateDirectories(topDir))
                {
                    if (!Path.GetFileName(child).Equals(leaf, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!File.Exists(Path.Combine(child, "manifest.json"))) continue;
                    hits++;
                    found = topName.TrimStart('.') + "/" + Path.GetFileName(child);
                }
            }
        }
        catch { return null; }
        return hits == 1 ? found : null;
    }

    private static string Sha256Of(string file)
    {
        try { return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))); }
        catch { return ""; }
    }

    /// <summary>
    /// 判定一个没有 manifest.json 的包要盖到哪些已装 mod 上，交回【每个宿主一份】计划。
    /// <b>只采信包自己的内部路径</b>：所有文件共享同一个第一层目录名，那个名字就是宿主。
    /// 包名像不像某个 mod 只用来给人看，不作为落盘依据 —— 猜错的表现是"装成功了但游戏里
    /// 毫无变化"，用户自己永远查不出来，所以判定不出时返回 null 让调用方放弃安装。
    /// 第一层对不上时再往下探一层：汉化包常把真宿主再套一层
    /// （"zLewdDewValley translation/LewdDew_Valley/…"，宿主其实是捆绑包里的子包）。
    /// 还不行才按<b>拼写</b>认一次（<see cref="FindInstalledHostBySpelling"/>）：包内那层目录
    /// 允许只是把宿主的名字抄得不太一样，判据仍是【已装 mod 自己声明的目录名/显示名】且唯一命中。
    /// <para>包里摆着<b>好几个</b>别的 mod 的目录（跨顶层，或在顶层之下并列）不再一律弃权：
    /// 每个目录名都逐字对上唯一一家已装 mod 时，那是给这几个 mod 一起用的包（实机 53406
    /// 「To The Moon (中文)」就是捆绑包两个子包的汉化），<b>每家各一份计划</b>；
    /// 有一个对不上、或两个目录对上同一行 ⇒ 报 <see cref="OverlayFail.MultiHost"/>，
    /// 因为只盖一半会让这个包半生效。</para>
    /// </summary>
    internal List<OverlayPlan>? ProbeOverlay(string gamePath, string tempDir, out OverlayFail fail, out string hostGuess,
        IReadOnlyList<NexusRequirement>? packRequirements = null)
    {
        fail = OverlayFail.None;
        hostGuess = "";
        string[] files;
        try { files = Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories); }
        catch { return null; }
        if (files.Length == 0) { fail = OverlayFail.NoHostInPack; return null; }

        var packRels = files
            .Select(f => Path.GetRelativePath(tempDir, f).Replace('\\', '/')).ToList();
        // 包根目录上摊着的说明文件不参与判定，也不写进宿主：实测 11209「Stardew Valley Expanded
        // -image translation-Chinese」17 个条目里 16 个在 [CP] Stardew Valley Expanded/ 下、
        // 第 17 个就是一个裸的「说明.txt」—— 旧写法因为"一半裸在根、一半埋在目录下"整包否决。
        var judged = packRels.Where(r => !IsPackDocFile(r)).ToList();
        var bareCount = judged.Count(r => !r.Contains('/'));
        if (judged.Count == 0) { fail = OverlayFail.NoHostInPack; return null; }
        if (bareCount == judged.Count)
        {
            // 所有文件都光秃秃躺在包根目录（实测 41314 / 18080 / 44611 整包就一个裸 zh.json）——
            // 下面每一层判据都要先看"包内那层目录名"，这里根本没有那层。只剩两条证据可用：
            // 这个包页面登记的前置、以及这份翻译自己的键是谁的键。
            if (SoleTranslationJson(tempDir, judged) is string bareJson)
            {
                var bp = ProbeTranslationOnlyPack(gamePath, tempDir, bareJson, packRequirements,
                    out fail, out hostGuess);
                return bp is null ? null : new List<OverlayPlan> { bp };
            }
            fail = OverlayFail.NoHostInPack;
            return null;
        }
        if (bareCount > 0)
        {
            // 一半裸在根、一半埋在目录下：包内路径自己就矛盾，不在这里替用户猜。
            fail = OverlayFail.NoHostInPack;
            return null;
        }

        string? top = null;
        var rel = new List<string>();
        var relNested = new List<string>();
        var tops = new List<string>();     // 包内出现过的顶层目录名（>1 = 一个包里含多个 mod 的目录）
        var subs = new List<string>();     // 顶层之下那层的不同目录名（多宿主贴图包：一层一个别的 mod）
        // 逐目录名收集"它自己名下有哪些文件"，供多宿主包一家一份计划用（见 ProbeOnePlanPerHostDir）
        var filesByTop = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var filesBySub = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            var r = Path.GetRelativePath(tempDir, f).Replace('\\', '/');
            if (IsPackDocFile(r)) continue;      // 说明文件：不参与判定，也不写进宿主
            var parts = r.Split('/');
            top ??= parts[0];
            AddDistinct(tops, parts[0]);
            if (parts.Length > 2 && !IsVanillaAssetLayoutDir(parts[1])) AddDistinct(subs, parts[1]);
            rel.Add(r[(top.Length + 1)..]);
            if (parts.Length > 2) relNested.Add(string.Join('/', parts[2..]));
            (filesByTop.TryGetValue(parts[0], out var tf) ? tf : filesByTop[parts[0]] = new List<string>())
                .Add(string.Join('/', parts[1..]));
            if (parts.Length > 2)
                (filesBySub.TryGetValue(parts[1], out var sf) ? sf : filesBySub[parts[1]] = new List<string>())
                    .Add(string.Join('/', parts[2..]));
        }
        if (top is null || rel.Count == 0) { fail = OverlayFail.NoHostInPack; return null; }
        hostGuess = top;
        // 跨好几个顶层目录：先问"是不是每一家都装着"（实机 53406 那种一个包里摆着捆绑包两个
        // 子包的汉化）。全对得上就一家一份计划；有一家对不上才报多宿主，把名字报出来让人自己分开发。
        if (tops.Count > 1)
        {
            if (ProbeOnePlanPerHostDir(gamePath, tempDir, tops, filesByTop, "") is List<OverlayPlan> topPlans)
                return topPlans;
            fail = OverlayFail.MultiHost; hostGuess = JoinHostNames(tops); return null;
        }
        if (ResolveHostDir(gamePath, top) is string exactDir)
        {
            // 顶层就是【已装的那个壳】：文件分住在几个子包里时，每个子包各记一条账。
            // 记在壳上等于记在一个没有行的宿主上（徽标、开关、还原全点不到，要等下次启动的
            // RelocateBundleTopOverlays 才搬进子包），实机汉化包 10342 就是这样隐身过。
            if (SplitShellPlanByBundleChild(exactDir, top, top, rel) is List<OverlayPlan> shellPlans)
                return shellPlans;
            return new List<OverlayPlan>
            {
                WithI18nHome(tempDir, new OverlayPlan(top, rel, top), exactDir)
            };
        }
        // 顶层名字是【捆绑包里某个子包的目录名】（宿主住在 Mods/壳/子包 这一层）：ResolveHostDir
        // 只找顶层，所以 SVE 这类捆绑包的汉化/贴图包以前一律判"认不出宿主"。
        // 实测 11209 的顶层就是 [CP] Stardew Valley Expanded —— 正是那个子包的目录名。
        // 这条不是拼写猜测：目录名逐字相等、且那个子包自己有 manifest，命中多个同样不返回。
        if (FindInstalledSubpackageByLeaf(gamePath, top) is string childHost
            && ResolveHostDir(gamePath, childHost) is string childDir)
        {
            hostGuess = childHost;
            return One(WithI18nHome(tempDir, new OverlayPlan(childHost, rel, top), childDir));
        }
        // 第二层【只有一个】名字时它才可能是真宿主（汉化包把真宿主再套一层的写法）；
        // 好几个名字 = 这包本来就是给好几个 mod 用的，第二层是候选宿主清单，不是宿主。
        if (subs.Count == 1 && relNested.Count == judged.Count
            && FindInstalledSubpackageByLeaf(gamePath, subs[0]) is string nested
            && ResolveHostDir(gamePath, nested) is string nestedDir)
        {
            hostGuess = nested;
            return One(WithI18nHome(tempDir, new OverlayPlan(nested, relNested, top + "/" + subs[0]), nestedDir));
        }
        // 拼写不同 ≠ 没装：汉化作者常把 N 网页号写进包内目录名。认出来了仍然只往【已存在】的
        // 宿主目录里盖，一个候选都对不上才说「你还没装它」。
        if (FindInstalledHostBySpelling(gamePath, top) is string bySpelling
            && ResolveHostDir(gamePath, bySpelling) is string spelledDir)
        {
            hostGuess = bySpelling;
            return One(WithI18nHome(tempDir, new OverlayPlan(bySpelling, rel, top), spelledDir));
        }
        // 走到这里：顶层名字谁也对不上，而第二层摆着好几个别的 mod 的名字。那些名字是【候选宿主
        // 清单】不是宿主（见下面 ProbeOnePlanPerHostDir 那一步），所以不许拿顶层那个名字当
        // 「你还没装的前置」报出去（实测 mod 1993 的文件 35407：顶层是包自己的标题
        // 「Mod Compatibility Textures」，真宿主是第二层的 Deep Woods Obelisk / Tractor Garage，
        // 而这两个都没装 ⇒ 旧写法会凭空捏一个不存在的「前置」）。
        // 名字对不上 ≠ 不是它的汉化包：作者常把【翻译自己的名字】写成包内顶层目录 ——
        // 实测 mod 43130 整包只有 `[CP] Elle's Seasonal Buildings-CN/i18n/zh.json`，
        // 而宿主目录叫 `[CP] Elle's Seasonal Buildings`（多个 -CN），拼写键也认不下它。
        // 这里【不截名字】：截法一定会被同作者的姊妹包打脸（Spring 的界面包盖到 Fall 的
        // 界面包上，这台机器上踩过）。改看两边各自声明的内容 —— 包里 config.<字段>.name
        // 点名了要翻译哪些配置项，宿主就得在自己写的 ConfigSchema / example_config 里
        // 把这些字段全声明出来。实测这台机器：154 个已装目录里只有 1 个有任何交集，
        // 且 47/47 全覆盖，第二名 0 个 —— 分辨力够，才敢拿来落盘。
        var wantCfg = PackDeclaredConfigFields(tempDir, top, rel);
        if (wantCfg.Count > 0)
        {
            var cfgHits = FindHostsByDeclaredConfigFields(gamePath, wantCfg);
            if (cfgHits.Count == 1 && ResolveHostDir(gamePath, cfgHits[0]) is string cfgDir)
            {
                hostGuess = cfgHits[0];
                return One(WithI18nHome(tempDir, new OverlayPlan(cfgHits[0], rel, top), cfgDir));
            }
            if (cfgHits.Count > 1)
            {
                fail = OverlayFail.AmbiguousConfigHost;
                hostGuess = JoinHostNames(cfgHits);
                return null;
            }
        }
        if (subs.Count > 1)
        {
            // 第二层那几个名字【每一家都装着】⇒ 这是给这几个 mod 一起用的包，一家一份计划。
            // 实机 53406「To The Moon (中文)」：包内两个目录就是已装捆绑包 Nullnnow.ToTheMoon
            // v1.0.0 的两个子包，旧写法在这里直接弃权，把一个完整的汉化拒成"该盖哪家你决定"。
            // 有一家对不上就整包不装 —— 只盖一半会让这个包半生效，而那正是这条链最怕的静默。
            if (ProbeOnePlanPerHostDir(gamePath, tempDir, subs, filesBySub, top + "/") is List<OverlayPlan> subPlans)
                return subPlans;
            fail = OverlayFail.MultiHost; hostGuess = JoinHostNames(subs); return null;
        }
        // 包内那层目录名谁也对不上 ⇒ 最后再问一遍这个包页面登记的前置、以及这份翻译自己的键
        // （实测 24232 包内那层就是 "i18n"、49587 那层是翻译自己的标题 —— 两个都不是宿主名）。
        // 只在路径判据全落空时才走到这里，所以"顶层目录名就是宿主"那条既有的路一条都没动。
        if (SoleTranslationJson(tempDir, judged) is string transJson)
        {
            var tplan = ProbeTranslationOnlyPack(gamePath, tempDir, transJson, packRequirements,
                out var tfail, out var tguess);
            if (tplan is not null) { fail = OverlayFail.None; hostGuess = tguess; return One(tplan); }
            // 翻译包那条通道报得出【具体是谁】时（页面登记的前置），就用它的判定 ——
            // 实测 24232 包内那层是 "i18n"，旧写法会指着这个素材层名说「你还没装「i18n」」，
            // 而页面说得出真宿主是 #20781。它说不出名字时才轮到下面那道"层名不是 mod 名"的闸。
            // HostKeysUnmatched 例外：它是"键集在本机一家都没过半"这个【测出来的事实】，
            // 连最接近的一家都没有时也要用这句（否则就落到"拿包内目录名当前置"那句谎话上）。
            if (tfail is OverlayFail.HostKeysUnmatched
                || (tguess.Length > 0
                    && tfail is OverlayFail.HostNotInstalled or OverlayFail.DeclaredHostKeyMismatch))
            { fail = tfail; hostGuess = tguess; return null; }
        }
        // 顶层那个名字是原版素材层名（i18n / assets / Portraits…）时，它不是 mod 名，
        // 说「你还没装它」等于凭空捏一个不存在的 mod（实测界面就报了「要盖在「i18n」上」）。
        if (IsVanillaAssetLayoutDir(top)) { fail = OverlayFail.NoHostInPack; hostGuess = ""; return null; }
        fail = OverlayFail.HostNotInstalled;
        return null;
    }

    /// <summary>包内那层目录名里，这些是【原版 Content 的素材层名】而不是某个 mod 的目录名。
    /// 实测 30539「Alternative Portraits and Sprites for Childhood Sweetheart Caroline」整包是
    /// <c>assets/Portraits/*.png</c> + <c>assets/Sprites/*.png</c>，旧写法把第二层的
    /// "Portraits、Sprites" 当成"包里放着好几个别的 mod 的目录"报出去 —— 那是游戏自己的文件夹名。
    /// 立绘家族本来就有它自己那条转换通道，不参与"多宿主"判定。</summary>
    private static readonly string[] VanillaAssetLayoutDirs =
    {
        "portraits", "characters", "sprites", "maps", "tilesheets", "loosesprites",
        "assets", "i18n", "data", "text", "buildings", "terrainfeatures", "mines", "animals",
    };

    /// <summary>包根目录上摊着的说明文件：不是 mod 内容，既不参与"宿主是谁"的判定，
    /// 也不写进宿主目录（免得往人家 mod 里塞一个 说明.txt）。只看裸在包根的文件 ——
    /// 带目录的（Host/readme.txt）照包内路径走，本来就在宿主那一层下面。</summary>
    private static bool IsPackDocFile(string relPath)
    {
        if (relPath.Contains('/')) return false;
        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        if (ext is ".txt" or ".md" or ".pdf" or ".html" or ".rtf") return true;
        var leaf = Path.GetFileNameWithoutExtension(relPath).ToLowerInvariant();
        return leaf is "readme" or "license" or "licence" or "changelog"
            or "说明" or "介绍" or "注意" or "留言";
    }

    private static bool IsVanillaAssetLayoutDir(string name)
        => Array.IndexOf(VanillaAssetLayoutDirs, name.Trim().ToLowerInvariant()) >= 0;

    /// <summary>这个包在 N 网页面登记的前置里<b>可能当覆盖宿主</b>的那些行：
    /// 场外链接不算（没有页面号也没有本机目录）；<b>SMAPI 不算</b> —— 它是加载器，
    /// 不住在 Mods\ 里，永远不可能被"盖"（实测 24232 登记了两行：#20781 与 SMAPI，
    /// 剔掉 SMAPI 才看得出宿主只有一个）。</summary>
    private static List<NexusRequirement> HostCandidateRows(IReadOnlyList<NexusRequirement>? reqs)
        => (reqs ?? new List<NexusRequirement>())
           .Where(r => !r.External && r.ModName.Trim().Length > 0
                       && !r.ModName.Trim().StartsWith("SMAPI", StringComparison.OrdinalIgnoreCase))
           .ToList();

    /// <summary>登记的前置里，<b>本机装着且唯一对上</b>的那一条的宿主目录名。
    /// 一条都对不上或对上好几条都不给。页面号可以是 0（实测 18080 登记了名字
    /// "Crop Harvest Bubbles" 而 modId 填的是 "0"）—— 名字照样能用，只是报不出页面号。</summary>
    private string? FindInstalledRequirementHost(string gamePath, IReadOnlyList<NexusRequirement>? reqs)
        => MatchDeclaredHosts(gamePath, reqs).One;

    /// <summary>登记的前置与本机已装 mod 到底对上几个。<b>必须把"并列"和"一个都没对上"分开</b>：
    /// 前者是同名子包分不出该盖谁（实测 SVE 捆绑包里 <c>[CP] Stardew Valley Expanded</c> 与
    /// <c>Stardew Valley Expanded Code</c> 两个子包的 manifest Name 都写着 "Stardew Valley Expanded"），
    /// 后者才是"你还没装它"。把并列说成没装是第二句谎话 —— 用户会去找一个其实装着的 mod。
    /// <para><see cref="One"/> 在并列时<b>必须是 null</b>，不是"给最后一个"：两个下游都把它当
    /// 唯一的宿主用（<see cref="ProbeOverlayForDeclaredHost"/> 直接往那个目录写字、
    /// InstallNew 那句"本机也装着它"直接报名字），给错了就是盖错 mod / 指错 mod。
    /// 实测 2026-10-08 的 31795：前置写着 SVE，本机 SVE 撞出两个同名子包，界面报的是
    /// 排在后面的那个 <c>[FTM]</c> 兄弟目录。</para>
    /// <para>internal 只为测试舱能拿真机那三处互不相等的名字＋页面号喂它（2026-10-13 的 50261）。</para></summary>
    internal (int Count, string? One) MatchDeclaredHosts(string gamePath, IReadOnlyList<NexusRequirement>? reqs)
    {
        var hosts = new List<string>();
        foreach (var (_, host) in DeclaredHostMatches(gamePath, reqs))
            if (!hosts.Contains(host, StringComparer.OrdinalIgnoreCase)) hosts.Add(host);
        return (hosts.Count, hosts.Count == 1 ? hosts[0] : null);
    }

    /// <summary>登记的前置与本机已装 mod 的每一条对应关系（<c>Row</c> = 这是第几行前置，按
    /// <see cref="HostCandidateRows"/> 过滤后的顺序）。
    /// <para>为什么要带上"来自哪一行"：有两种完全不同的"多个候选"。</para>
    /// <list type="bullet">
    /// <item><b>同一行撞出两家</b> —— 两个已装 mod 的 manifest 都自述同一个页面号
    /// （实机 31795：SVE 捆绑包里 <c>[CP] Stardew Valley Expanded</c> 与
    /// <c>Stardew Valley Expanded Code</c> 都写 Nexus:3753）。这是<b>身份本身</b>分不出，
    /// 调用方必须弃权，一张都不许写。</item>
    /// <item><b>不同行各对上一家</b> —— 包登记了宿主 + GMCM + Content Patcher，三家都装着。
    /// 这不是并列，是"前置不止一个"；谁真该被盖，由包自己的内容（素材落在谁家 / 键是谁家的
    /// i18n）再筛一遍。把它当并列一律弃权，会让包只要顺手登记了 GMCM 就丢掉整档
    /// —— 实机 2026-10-13 我改出过这个回归，A/B 对表钉在 <c>DeclaredHostIdentityTests</c>。</item>
    /// </list>
    /// <para>号优先：带页面号的行先比页面号；比不到再按名字，但本机那一行<b>自述过别的页面号</b>时
    /// 即使名字逐字相等也排除（同名不同家＝静默盖错）。手工装的宿主常常没有 manifest、或有 manifest
    /// 却没填 UpdateKeys（实机 32901 / 41314 那批裸翻译包的宿主就这形状）—— 那种没有号可违，
    /// 名字是仅剩的证据。起因（实机 50261）：前置行「Parchment - Fishing Log」(50188)、宿主 manifest
    /// 的 Name「[Parchment] Fishing Logbook」、目录名 FishingLogbook 三个字符串互不相等，
    /// 旧判据只比名字的拼写键，于是"装着"被判成"没装"。</para></summary>
    internal List<(int Row, string Host)> DeclaredHostMatches(string gamePath,
        IReadOnlyList<NexusRequirement>? reqs)
    {
        var list = new List<(int, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var row = -1;
        foreach (var r in HostCandidateRows(reqs))
        {
            row++;
            var found = r.ModId > 0 ? InstalledHostsByPageId(gamePath, r.ModId) : new List<string>();
            if (found.Count == 0) found = InstalledHostsByName(gamePath, r.ModName, r.ModId);
            foreach (var h in found)
            {
                if (!seen.Add(h)) continue;      // 同一家的名字与号都对上 ⇒ 还是一条对应
                list.Add((row, h));
            }
        }
        return list;
    }

    /// <summary>某一行前置在本机撞出两家 ⇒ 身份并列（见 <see cref="DeclaredHostMatches"/>）。</summary>
    internal static bool HasRowTie(IReadOnlyList<(int Row, string Host)> matches)
        => matches.GroupBy(m => m.Row).Any(g => g.Count() > 1);

    /// <summary>已装 mod 里<b>自己 manifest 的 UpdateKeys 就写着这个页面号</b>的全部宿主。
    /// 与 <see cref="InstalledHostsByName"/> 同一条收集口径（归一化宿主、目录必须解析得出来），
    /// 只是把名字换成号。</summary>
    private List<string> InstalledHostsByPageId(string gamePath, int nexusModId)
    {
        var list = new List<string>();
        if (nexusModId <= 0) return list;
        try
        {
            foreach (var e in ScanRaw(gamePath))
            {
                if (e.NexusModId != nexusModId) continue;
                var host = NormalizeOverlayHost(e.Folder);
                if (host.Length == 0) continue;
                if (ResolveHostDir(gamePath, host) is null) continue;
                if (!list.Contains(host, StringComparer.OrdinalIgnoreCase)) list.Add(host);
            }
        }
        catch { return list; }
        return list;
    }

    /// <summary>已装 mod 里名字（目录末段或 manifest 显示名）与这个前置名对得上的全部宿主。
    /// 与 <see cref="FindInstalledHostBySpelling"/> 同一套拼写键，只是不替调用方在并列时弃权。
    /// <paramref name="declaredId"/> 是这一行前置自己的页面号：本机那一行<b>自述过另一个号</b>时
    /// 即使名字逐字相等也排除掉（同名不同家）；没自述过号的（手工装、manifest 缺 UpdateKeys）
    /// 仍然算 —— 那种情况下名字是仅剩的证据。</summary>
    private List<string> InstalledHostsByName(string gamePath, string name, int declaredId = 0)
    {
        var list = new List<string>();
        var key = HostSpellingKey(name);
        if (key.Length == 0) return list;
        try
        {
            foreach (var e in ScanRaw(gamePath))
            {
                var host = NormalizeOverlayHost(e.Folder);
                if (host.Length == 0) continue;
                var leaf = host.Replace('\\', '/').Split('/')[^1];
                if (!HostSpellingKey(leaf).Equals(key, StringComparison.Ordinal)
                    && !HostSpellingKey(e.Name ?? "").Equals(key, StringComparison.Ordinal)) continue;
                if (declaredId > 0 && e.NexusModId is int own && own > 0 && own != declaredId) continue;
                if (ResolveHostDir(gamePath, host) is null) continue;
                if (!list.Contains(host, StringComparer.OrdinalIgnoreCase)) list.Add(host);
            }
        }
        catch { return list; }
        return list;
    }

    /// <summary>整包只有一个翻译 json 时（见 <see cref="SoleTranslationJson"/>），页面登记的那
    /// <b>唯一</b>一条宿主候选 —— 用来把「你还没装它」说成具体是哪个 mod、哪个页面。
    /// 多于一行（说不清哪条是宿主）或一行都没有 ⇒ null。</summary>
    internal static NexusRequirement? SoleHostCandidate(IReadOnlyList<NexusRequirement>? reqs)
    {
        var rows = HostCandidateRows(reqs);
        return rows.Count == 1 ? rows[0] : null;
    }

    /// <summary>整包【只有一个翻译 json】的形状。这一批实机包全是它，但包内那层目录名五花八门：
    /// <list type="bullet">
    /// <item>41314 / 18080 / 44611 —— 裸在包根目录的 <c>zh.json</c></item>
    /// <item>24232 —— <c>i18n/zh.json</c></item>
    /// <item>49587 / 38630 —— <c>翻译自己的标题/i18n/zh.json</c></item>
    /// <item>32901 —— <c>Caroline/default.json</c>（角色名当目录名，1299 个键里 1280 个
    ///   就是宿主 i18n/default.json 的键）</item>
    /// </list>
    /// 所以<b>不看路径</b>：包内那层目录名要么是翻译自己的标题、要么是素材层名、要么是角色名，
    /// 拿它当宿主名会凭空捏一个不存在的 mod（实测界面就报过「这个包要盖在「i18n」上」）。
    /// "这是不是一份翻译"由文件自己的内容说了算 —— 顶层键 vs 各已装 mod 的 i18n 键集，
    /// 见 <see cref="ProbeTranslationOnlyPack"/>。返回包内路径；不满足"恰好一个文件、
    /// 它是 json、且不是 content.json / manifest.json"⇒ null。</summary>
    private static string? SoleTranslationJson(string tempDir, List<string> judgedRels)
    {
        if (judgedRels.Count != 1) return null;
        var r = judgedRels[0];
        var slash = r.LastIndexOf('/');
        var leaf = slash < 0 ? r : r[(slash + 1)..];
        if (!leaf.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("content.json", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)) return null;
        // 不再要求它躺在 i18n/ 下：实测 32901「Childhood Sweetheart Caroline (Chinese)」
        // 把翻译放在 <角色名>/default.json（1299 个键，与宿主 i18n/default.json 的 1285 个
        // 重合 1280 = 98.5%）。"这是不是一份翻译"由文件自己的内容说了算（顶层键 vs 宿主
        // i18n 键集，见 ProbeTranslationOnlyPack），包内那层目录名不参与判断。
        return r;
    }

    /// <summary>一个已装 mod 的 <c>i18n/*.json</c> 顶层键全集（非递归，与
    /// <see cref="WithI18nHome"/> 读的是同一处）。</summary>
    private static HashSet<string> HostI18nKeys(string hostDir)
    {
        var have = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var f in Directory.GetFiles(Path.Combine(hostDir, "i18n"), "*.json"))
                if (JsonTopKeys(f) is { } ks)
                    foreach (var k in ks) have.Add(k);
        }
        catch { }
        return have;
    }

    /// <summary>这份翻译的顶层键是否过半落在这个宿主的 i18n 键集里（六成 = 这条链既有的校准线）。</summary>
    private static bool HostI18nCovers(string hostDir, HashSet<string> packKeys)
    {
        var have = HostI18nKeys(hostDir);
        return have.Count > 0 && packKeys.Count(k => have.Contains(k)) * 100 >= packKeys.Count * 60;
    }

    /// <summary>已装 mod 里，i18n 键集过半覆盖 <paramref name="want"/> 的那些目录。
    /// 命中几家都交回调用方，这里绝不替用户挑。
    /// <para>遍历的单位必须是 <see cref="ScanRaw"/> 的行（顶层 + 捆绑包子包），不能只看
    /// <c>Mods\*</c> 一层：实机 2026-10-08 的「Grandpa''s Farm」是个壳，它的 i18n 在
    /// <c>Grandpa''s Farm/[CP] Grandpa''s Farm/i18n/</c>，只扫一层 ⇒ 键路由对整个捆绑包生态
    /// 全部失明（SVE、Grandpa''s Farm、Immersive Farm… 一个都认不出来），而名字路由
    /// （<see cref="InstalledHostsByName"/>）本来就看得到子包 —— 两条证据对"什么算宿主"
    /// 不一致，才会出现"宿主明明装着、却报判断不出宿主"。</para></summary>
    private List<string> FindHostsByI18nKeys(string gamePath, HashSet<string> want)
        => RankHostsByI18nKeys(gamePath, want)
            .Where(r => r.Matched * 100 >= want.Count * 60)
            .Select(r => r.Host).ToList();

    /// <summary>把全部已装宿主按"这个包的键在谁家 i18n 里出现得多"排名（一家都撞不上的不列）。
    /// 用来把拒绝说成【可核对的事实】：实测 45719「UI Info Suite 2 Alternative-Chinese」的 298 个键
    /// 里只有 95 个（32%）撞在本机装着的<b>原版</b> UIInfoSuite2 上 —— 说"判断不出宿主"是把已经
    /// 量出来的东西藏起来，说"它就是 UIInfoSuite2 的汉化"是撒谎，只有报出这一名和这个数是诚实的。</summary>
    private List<(string Host, int Matched)> RankHostsByI18nKeys(string gamePath, HashSet<string> want)
    {
        var rows = new List<(string, int)>();
        try
        {
            foreach (var e in ScanRaw(gamePath))
            {
                var host = NormalizeOverlayHost(e.Folder);
                if (host.Length == 0) continue;
                if (ResolveHostDir(gamePath, host) is not string dir) continue;
                var have = HostI18nKeys(dir);
                if (have.Count == 0) continue;
                var matched = want.Count(have.Contains);
                if (matched > 0) rows.Add((host, matched));
            }
        }
        catch { }
        return rows.OrderByDescending(r => r.Item2).ToList();
    }

    /// <summary>整包只有一个翻译 json ⇒ 认宿主并给出落点。两条证据按权威度排：
    /// ① <b>这个包页面登记的前置</b>里本机装着且唯一对上的那一条（作者自己写的）；
    /// ② 页面一行都没登记时，这份 json 的顶层键 vs 各已装 mod 自己 i18n 的键集，唯一命中
    ///    （实测 49587：ZoomLevel <b>100%</b>、第二名 0%）。
    /// <para><b>②不许在"页面登记了、可那条本机没装着"时兜底</b> —— 实测 24232「Crop Harvest
    /// Bubbles (Updated) 中文」登记的前置是<b>没装的</b> #20781，而它那 6 个键跟本机装着的
    /// 原版 Crop Harvest Bubbles 的 i18n 也 100% 重合（更新版的键是原版的子集）：
    /// 让键集兜底就会把"更新版"的翻译盖到原版头上，正是这条链最不该制造的那种静默。</para>
    /// 两条都不给答案时：页面只有唯一一条候选 ⇒ 报「你还没装它」并带页面号；否则 NoHostInPack。</summary>
    private OverlayPlan? ProbeTranslationOnlyPack(string gamePath, string tempDir, string jsonRel,
        IReadOnlyList<NexusRequirement>? packRequirements, out OverlayFail fail, out string hostGuess)
    {
        fail = OverlayFail.None;
        hostGuess = "";
        var rows = HostCandidateRows(packRequirements);
        var leaf = jsonRel.Split('/')[^1];
        var slash = jsonRel.LastIndexOf('/');
        var packRoot = slash < 0 ? "" : jsonRel[..slash];
        HashSet<string>? reqHostKeys = null;

        var matches = DeclaredHostMatches(gamePath, packRequirements);
        var cands = matches.Select(m => m.Host).ToList();
        if (rows.Count > 0 && cands.Count == 0)
        {
            // 一条前置都没对上本机已装 mod ⇒ 这才是真的"你还没装它"
            fail = rows.Count == 1 ? OverlayFail.HostNotInstalled : OverlayFail.NoHostInPack;
            hostGuess = rows.Count == 1 ? rows[0].ModName.Trim() : "";
            return null;
        }
        if (cands.Count > 0 && !HasRowTie(matches))
        {
            // 前置常常登记好几家且都装着（宿主 + GMCM + Content Patcher）。所以"候选数≠1"不能直接弃权 ——
            // 拿这份翻译自己的键去问"谁接得住"，只剩一家接得住才算认出来（实机 2026-10-13 A/B 对表
            // 抓出来的回归：旧写法只要包里多一条 GMCM 就丢掉整档）。
            var declaredKeys = JsonTopKeys(
                Path.Combine(tempDir, jsonRel.Replace('/', Path.DirectorySeparatorChar)));
            var covering = declaredKeys is { Count: > 0 }
                ? cands.Where(h => ResolveHostDir(gamePath, h) is string d && HostI18nCovers(d, declaredKeys)).ToList()
                : new List<string>();
            if (covering.Count == 1)
                return new OverlayPlan(covering[0], new List<string> { leaf }, packRoot,
                    new List<string> { "i18n/" + leaf });
            if (cands.Count == 1)
            {
                if (ResolveHostDir(gamePath, cands[0]) is null)
                { fail = OverlayFail.NoHostInPack; return null; }
                // 名字/页面号对上了、条目对不上 ⇒ 不是它的翻译（不许落到宿主根目录当静默无效文件）
                fail = OverlayFail.DeclaredHostKeyMismatch;
                hostGuess = cands[0];
                return null;
            }
            // 多家装着而接得住的不止一家、或一家都没有 ⇒ 交给下面那档按键去分
        }

        // 页面没给答案（一行都没登记，或登记的名字在本机撞出好几个同名子包分不出该盖谁）
        // ⇒ 只看这份翻译自己声明的内容：它的键是谁的 i18n 里的键。
        // 实测 31795「Grandpa's Farm 中文翻译」：登记的前置写着 SVE，而 SVE 在本机装着的是
        // 两个同名子包 —— 并列不许多数服从，交给键集去分（它的键是隔壁 Grandpa's Farm 的）。
        var keys = reqHostKeys ?? JsonTopKeys(
            Path.Combine(tempDir, jsonRel.Replace('/', Path.DirectorySeparatorChar)));
        if (keys is not { Count: > 0 })
        { fail = OverlayFail.NoHostInPack; return null; }
        var byKeys = FindHostsByI18nKeys(gamePath, keys);
        if (byKeys.Count == 1 && ResolveHostDir(gamePath, byKeys[0]) is not null)
            return new OverlayPlan(byKeys[0], new List<string> { leaf }, packRoot,
                new List<string> { "i18n/" + leaf });
        if (byKeys.Count == 0)
        {
            // 一家都没过半 ⇒ 我们其实【量出了东西】：最接近的是谁、对上了几条。
            // 报"判断不出宿主"是把这句藏起来让人自己去猜（实测 45719 就是这样被当成 bug 报上来的）。
            var rank = RankHostsByI18nKeys(gamePath, keys);
            fail = OverlayFail.HostKeysUnmatched;
            hostGuess = rank.Count > 0 ? rank[0].Host : "";
            AppLog.Info("Mods", $"这个汉化包的 {keys.Count} 个条目在本机最接近的宿主："
                + (rank.Count == 0 ? "一家都没有"
                    : string.Join(" , ", rank.Take(3).Select(r => $"{r.Host} {r.Matched}/{keys.Count}"))));
            return null;
        }
        fail = OverlayFail.NoHostInPack;
        hostGuess = "";
        return null;
    }

    /// <summary>无 manifest 的图片包，如果它自己页面登记的那<b>唯一</b>一条前置是本机装着的某个 mod，
    /// 而且包里的图片逐张都在那个 mod 的目录里有同名文件 ⇒ 它是那个 mod 的<b>覆盖包</b>，
    /// 不是"照着原版某张图画的面子包"。
    /// <para>为什么需要这一层（实机 2026-10-08，34635「DaisyNiko's Earthy Recolour - Chinese」）：
    /// 包内路径是 <c>Maps/fall_beach.png</c> 这种【原版 Content 相对路径】，而
    /// <c>Content\Maps\fall_beach.xnb</c> 真的存在 ⇒ <see cref="HasForeignAssetTarget"/> 先一步判定
    /// "该转成 CP 包盖原版"，覆盖链压根没执行。可这包 11 张图 <b>11/11</b> 在已装的
    /// <c>[CP] DaisyNiko's Earthy Recolour</c>（#5255）里有同名文件，转出来的 11 条 CP 目标
    /// 也<b>全部</b>落在 ER 自己那 98 条目标之内 —— 一个独有目标都没有。
    /// 作者登记的意图应当压过我们从路径猜出来的形状。</para></summary>
    internal OverlayPlan? ProbeOverlayForDeclaredHost(string gamePath, string tempDir,
        IReadOnlyList<NexusRequirement>? packRequirements)
    {
        List<string> packRel;
        try
        {
            packRel = Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(tempDir, f).Replace('\\', '/')).ToList();
        }
        catch { return null; }
        if (packRel.Count == 0) return null;

        // 前置行常常不止一条装着（宿主 + GMCM + Content Patcher…）。同一行撞出两家 = 身份并列 ⇒ 一张都不写
        // （实机 31795 的 SVE 两子包都自述 Nexus:3753）；不同行各对上一家，则逐家试"这包的素材是不是它的"，
        // 只剩一家接得住才落。早先写法是"候选数≠1 就直接不认"，于是包只要顺手登记了 GMCM 就丢掉整档
        // —— 实机 2026-10-13 A/B 对表抓出来的回归。
        var matches = DeclaredHostMatches(gamePath, packRequirements);
        if (HasRowTie(matches)) return null;
        OverlayPlan? found = null;
        foreach (var hostName in matches.Select(m => m.Host))
        {
            if (ResolveHostDir(gamePath, hostName) is not string hostDir) continue;
            if (MapPackFilesOntoHost(hostDir, packRel) is not Dictionary<string, string> map) continue;
            if (found is not null) return null;
            var rel = new List<string>();
            var dst = new List<string>();
            foreach (var p in packRel) { rel.Add(p); dst.Add(map[p]); }
            found = new OverlayPlan(hostName, rel, "", dst);
        }
        return found;
    }
}
