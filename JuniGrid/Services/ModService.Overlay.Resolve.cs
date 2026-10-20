using System.IO;
using System.Text.Json;

namespace JuniGrid.Services;

public sealed partial class ModService
{
    /// <summary>把包里的文件按【宿主目录里真实存在的同名文件】逐一对上落点，三种形状都试：
    /// 原样、加 <c>assets/</c> 前缀、去 <c>assets/</c> 前缀（各家 mod 收素材的层不一样）。
    /// 命中率<b>只算图片</b>（png / xnb）——"这是谁的素材"这件事只有素材本身能作证；
    /// 图片命中不足六成 ⇒ null（六成是这条链既有的校准线，见 <see cref="WithI18nHome"/>）。
    /// 没命中的文件（readme 之类）按包内路径原样落。</summary>
    private static Dictionary<string, string>? MapPackFilesOntoHost(string hostDir, List<string> packRel)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var images = 0;
        var hits = 0;
        foreach (var p in packRel)
        {
            var ext = Path.GetExtension(p);
            var isImage = ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
                          || ext.Equals(".xnb", StringComparison.OrdinalIgnoreCase);
            if (isImage) images++;                       // 分母要在"命中不命中"之前数，否则永远是 100%
            var found = HostPathShapes(p).FirstOrDefault(s =>
                File.Exists(Path.Combine(hostDir, s.Replace('/', Path.DirectorySeparatorChar))));
            if (found is not null)
            {
                map[p] = found;
                if (isImage) hits++;
                continue;
            }
            // 这张图宿主里没有同名文件（覆盖包顺手新增的素材）。落点仍按宿主自己的层走：
            // 取那个"父目录在宿主里已经存在"的形状，两边都不存在就原样落，不硬编一个位置。
            map[p] = isImage
                ? (HostPathShapes(p).FirstOrDefault(s => Directory.Exists(
                      Path.GetDirectoryName(Path.Combine(hostDir,
                          s.Replace('/', Path.DirectorySeparatorChar))))) ?? p)
                : p;
        }
        return images > 0 && hits * 100 >= images * 60 ? map : null;
    }

    private static IEnumerable<string> HostPathShapes(string packRelPath)
    {
        yield return packRelPath;
        if (packRelPath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
            yield return packRelPath["assets/".Length..];
        else
            yield return "assets/" + packRelPath;
    }

    /// <summary>一个包里所有 manifest 自己声明的 UniqueID（捆绑包收集全部子包）。</summary>
    private static HashSet<string> PackUniqueIds(string[] manifests)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mf in manifests)
            try
            {
                using var doc = JsonDocument.Parse(ReadManifestText(mf), ManifestJson);
                if (doc.RootElement.TryGetProperty("UniqueID", out var u)
                    && u.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(u.GetString()))
                    set.Add(u.GetString()!.Trim());
            }
            catch { }
        return set;
    }

    /// <summary><c>Mods\&lt;folderName&gt;</c>（或禁用态的 <c>Mods\.&lt;folderName&gt;</c>）里已经住着
    /// <b>另一个 mod</b> —— 它自己的 manifest 写了 UniqueID，而那个 ID 不在这一包声明的 ID 里 ——
    /// 就返回它的名字；目录不存在、读不出 manifest（空壳/无清单）、或本来就是同一个 mod 的旧版
    /// ⇒ null，照旧走"同名旧目录移进回收站"那条路。
    /// <para>没有这道闸的后果（测试舱实测 2026-10-08）：装一个 Name=HostOne / UniqueID=Some.Other
    /// 的包，已装的 HostOne（UniqueID=Host.One）会被整个移进 <c>Mods\.junigrid_trash</c> ——
    /// 两个 mod 只在文件夹名上撞车，用户看到的却是"我那个 mod 自己没了"。</para></summary>
    private static string? DifferentModInThatFolder(string gamePath, string folderName, HashSet<string> newUids)
    {
        if (newUids.Count == 0) return null;      // 这一包没写 UID ⇒ 没有可比的身份，不拦
        var modsDir = Path.Combine(gamePath, "Mods");
        foreach (var candidate in new[] { folderName, "." + folderName })
        {
            var dir = Path.Combine(modsDir, candidate);
            if (!Directory.Exists(dir)) continue;
            string[] manifests;
            try { manifests = Directory.GetFiles(dir, "manifest.json", SearchOption.AllDirectories); }
            catch { continue; }
            foreach (var mf in manifests)
            {
                try
                {
                    using var doc = JsonDocument.Parse(ReadManifestText(mf), ManifestJson);
                    if (doc.RootElement.TryGetProperty("UniqueID", out var u)
                        && u.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(u.GetString())
                        && !newUids.Contains(u.GetString()!.Trim()))
                        return doc.RootElement.TryGetProperty("Name", out var n)
                               && n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString())
                            ? n.GetString()!.Trim() : candidate;
                }
                catch { }
            }
        }
        return null;
    }

    /// <summary>包里文件所在的那层是「自己没有 manifest 的捆绑包壳」⇒ 宿主归到它真正的宿主行
    /// <c>顶层/子包</c>（Scan 不为壳顶层出行，只为带 manifest 的子包出行）。
    /// <para>不归一的症状（实机汉化包 28252 → Frontier Farm，2026-10-07）：包内路径
    /// <c>Frontier Farm/[CP] Frontier Farm/i18n/zh.json</c> 的顶层名恰好就是壳目录名，于是宿主记成了顶层；
    /// 而 Mods 页那两行都是子包，徽标按宿主形状查表 ⇒ 一个都查不到，覆盖包在界面上整个隐身，
    /// 开关、还原、删除三个入口全点不到 —— 文件其实已经正确落盘、游戏里也确实生效了。</para>
    /// 归不了返回 null（交回原判定）：顶层自己有 manifest、文件散在好几个子包、
    /// 有直属顶层的文件、或那一层子目录根本没有 manifest。</summary>
    private static List<OverlayPlan> One(OverlayPlan p) => new() { p };

    /// <summary>包里文件所在的那层是「自己没有 manifest 的捆绑包壳」⇒ 按子包拆开，<b>每个子包一条账</b>
    /// <c>顶层/子包</c>（Scan 不为壳顶层出行，只为带 manifest 的子包出行）。一个子包时它退化成原来
    /// 那份 <see cref="SplitShellPlanByBundleChild"/> 在一家时的结果，形状逐字不变。
    /// <para>不归一的症状（实机汉化包 28252 → Frontier Farm，2026-10-07）：包内路径
    /// <c>Frontier Farm/[CP] Frontier Farm/i18n/zh.json</c> 的顶层名恰好就是壳目录名，于是宿主记成了顶层；
    /// 而 Mods 页那两行都是子包，徽标按宿主形状查表 ⇒ 一个都查不到，覆盖包在界面上整个隐身，
    /// 开关、还原、删除三个入口全点不到 —— 文件其实已经正确落盘、游戏里也确实生效了。</para>
    /// <para>文件分住几个子包时（实机汉化包 10342 → SVE 的 Code 与 [CP] 两包）原来也记在壳上，
    /// 要等下次启动 <see cref="RelocateBundleTopOverlays"/> 才搬进子包行 —— 装完到重启之前那一截
    /// 同样是隐形的。这里当场拆开，就不需要等。</para>
    /// 拆不了返回 null（交回原判定）：顶层自己有 manifest、有直属顶层的文件、或某一层子目录没有 manifest。</summary>
    private static List<OverlayPlan>? SplitShellPlanByBundleChild(string topDir, string top, string packRoot,
        List<string> rel)
    {
        if (File.Exists(Path.Combine(topDir, "manifest.json"))) return null;
        var bySeg = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rel)
        {
            var i = r.IndexOf('/');
            if (i <= 0) return null;                       // 有文件直属壳这一层：拆不进任何子包
            if (!bySeg.TryGetValue(r[..i], out var g)) bySeg[r[..i]] = g = new List<string>();
            g.Add(r[(i + 1)..]);
        }
        var plans = new List<OverlayPlan>();
        foreach (var (seg, files) in bySeg)
        {
            if (!File.Exists(Path.Combine(topDir, seg, "manifest.json"))) return null;   // 那一层不是子包
            plans.Add(new OverlayPlan(top + "/" + seg, files, packRoot + "/" + seg));
        }
        return plans.Count > 0 ? plans : null;
    }

    /// <summary>包内并列摆着<b>好几个</b>别的 mod 的目录名时，逐字比对已装 mod 行：
    /// <b>每一家都唯一对上</b>才交回"一家一份计划"，否则 null（调用方报 <see cref="OverlayFail.MultiHost"/>）。
    /// <para>为什么原来一律弃权、现在敢盖：弃权那条写法顺带把【捆绑包的汉化包】也拒了 ——
    /// 实机 53406「To The Moon (中文)」包内两个目录就是已装捆绑包的两个子包，目录名逐字相同、
    /// 两个都装着，这里没有任何"该替谁决定"的余地，缺的只是"去盘上看一眼"。</para>
    /// <para>三道闸，一道都不松：① 只认<b>逐字相等</b>的行末段名（不拼写、不截断 —— 多宿主一次要落
    /// 好几家，猜错的面积比单宿主大）；② 一家都对不上才算，<b>有一家缺就整包不装</b>（只盖一半会让
    /// 这个包半生效，正是这条链最怕的静默）；③ 包里出现的目录名必须【全部】在候选清单里 ——
    /// 混着 assets/ 这类原版素材层名的包拆一半留一半，等于替用户丢了文件。</para></summary>
    private List<OverlayPlan>? ProbeOnePlanPerHostDir(string gamePath, string tempDir, List<string> dirNames,
        Dictionary<string, List<string>> filesByDir, string packRootPrefix)
    {
        if (dirNames.Count < 2 || dirNames.Count != filesByDir.Count) return null;
        var plans = new List<OverlayPlan>();
        foreach (var n in dirNames)
        {
            // 名字逐字相等 ⇒ 每家本来就是不同的一行，不需要再防"两个目录对上同一家"
            if (FindInstalledHostByExactDirName(gamePath, n) is not string host) return null;
            if (ResolveHostDir(gamePath, host) is not string hostDir) return null;
            plans.Add(WithI18nHome(tempDir, new OverlayPlan(host, filesByDir[n], packRootPrefix + n), hostDir));
        }
        return plans;
    }

    /// <summary>已装 mod 行里，<b>目录末段名逐字相等</b>的那一家。命中 0 个或多个都不返回。
    /// 行必须来自 <see cref="ScanRaw"/>（顶层 mod 与捆绑包子包都算），这样落盘的宿主一定是
    /// Mods 页有一行的宿主 —— 记在壳顶层上的账没有徽标、开关、还原任何入口。</summary>
    private string? FindInstalledHostByExactDirName(string gamePath, string dirName)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? one = null;
        try
        {
            foreach (var e in ScanRaw(gamePath))
            {
                var host = NormalizeOverlayHost(e.Folder);
                if (host.Length == 0) continue;
                if (!host.Replace('\\', '/').Split('/')[^1].Equals(dirName, StringComparison.OrdinalIgnoreCase)) continue;
                // 扫到却又不在磁盘上了 = 目录正在被改名，这份结果不能用
                if (ResolveHostDir(gamePath, host) is null) continue;
                if (found.Add(host)) one = host;
            }
        }
        catch { return null; }
        return found.Count == 1 ? one : null;
    }

    /// <summary>包里【声明】要翻译哪些配置项：它自己 i18n/*.json 里 <c>config.&lt;字段&gt;.name</c>
    /// 那些键的字段名。一个都没有 ⇒ 空集（这包不是配置项汉化，别拿这条判据套它）。</summary>
    private static HashSet<string> PackDeclaredConfigFields(string tempDir, string packRoot, List<string> relPaths)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in relPaths)
        {
            if (!r.StartsWith("i18n/", StringComparison.OrdinalIgnoreCase)
                || !r.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            if (JsonTopKeys(Path.Combine(tempDir,
                    packRoot.Replace('/', Path.DirectorySeparatorChar),
                    r.Replace('/', Path.DirectorySeparatorChar))) is not { } keys) continue;
            foreach (var k in keys)
                if (k.StartsWith("config.", StringComparison.OrdinalIgnoreCase)
                    && k.EndsWith(".name", StringComparison.OrdinalIgnoreCase)
                    && k.Length > "config..name".Length)
                    set.Add(k["config.".Length..^".name".Length]);
        }
        return set;
    }

    /// <summary>已装 mod 里，把自己声明的配置项字段名【全包含】want 的那些目录。
    /// 一家都没命中或命中好几家，都由调用方处理 —— 这里绝不替用户挑一个。</summary>
    private static List<string> FindHostsByDeclaredConfigFields(string gamePath, HashSet<string> want)
    {
        var hits = new List<string>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Path.Combine(gamePath, "Mods")))
            {
                var name = Path.GetFileName(dir);
                if (name.Equals(".junigrid_trash", StringComparison.OrdinalIgnoreCase)) continue;
                if (CoversConfigFields(dir, want)) hits.Add(NormalizeOverlayHost(name));
            }
        }
        catch { }
        return hits;
    }

    /// <summary>这个目录声明的配置项字段名：CP 的通用配置菜单就读这几处 ——
    /// manifest.json / content.json 的 <c>ConfigSchema</c>（嵌套一层），加上
    /// example_config.json / config.json 的顶层键。</summary>
    private static bool CoversConfigFields(string dir, HashSet<string> want)
    {
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in new[] { "manifest.json", "content.json" })
            try
            {
                using var doc = JsonDocument.Parse(ReadManifestText(Path.Combine(dir, file)), ManifestJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("ConfigSchema", out var cs)
                    && cs.ValueKind == JsonValueKind.Object)
                    foreach (var p in cs.EnumerateObject()) declared.Add(p.Name);
            }
            catch { }
        foreach (var file in new[] { "example_config.json", "config.json" })
            if (JsonTopKeys(Path.Combine(dir, file)) is { } keys) declared.UnionWith(keys);
        return declared.Count > 0 && want.IsSubsetOf(declared);
    }

    private static void AddDistinct(List<string> list, string name)
    {
        if (!list.Contains(name, StringComparer.OrdinalIgnoreCase)) list.Add(name);
    }

    /// <summary>把候选宿主名拼进一句提示里；超过三个只点名前三个并报总数，免得提示卡变成一堵字。</summary>
    private static string JoinHostNames(List<string> names)
        => names.Count <= 3 ? string.Join("、", names)
         : string.Join("、", names.Take(3)) + " 等 " + names.Count + " 个";

    /// <summary>包内目录名的拼写键：剥掉「N 网页号 + 分隔符」前缀与「[CP] 」这类标签，
    /// 再忽略大小写与空格/连字符/点。<c>2544 Canon-Friendly Dialogue Expansion</c> 与
    /// <c>[CP] Canon Friendly Dialogue Expansion</c> 得到同一个键。</summary>
    private static string HostSpellingKey(string s)
    {
        var n = (s ?? "").Trim();
        var sep = n.IndexOfAny(new[] { ' ', '_', '-', '.' });
        if (sep > 0 && n[..sep].All(char.IsDigit))
            n = n[(sep + 1)..].TrimStart(' ', '_', '-', '.');
        n = StripPackTag(n);
        var sb = new System.Text.StringBuilder(n.Length);
        foreach (var ch in n)
            if (!char.IsWhiteSpace(ch) && ch is not ('-' or '_' or '.')) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    /// <summary>按拼写键在<b>已装</b> mod 里找宿主：比目录名末段，也比 manifest 显示名
    /// （汉化包内那层目录常照抄显示名，而显示名和文件夹名本来就允许不同）。
    /// 命中 0 个或多个都不返回 —— 放宽的只是拼写，猜错的表现是把文件盖进隔壁 mod 里。</summary>
    private string? FindInstalledHostBySpelling(string gamePath, string packFolderName)
    {
        var key = HostSpellingKey(packFolderName);
        if (key.Length == 0) return null;
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? found = null;
        try
        {
            foreach (var e in ScanRaw(gamePath))
            {
                var host = NormalizeOverlayHost(e.Folder);
                if (host.Length == 0) continue;
                var leaf = host.Replace('\\', '/').Split('/')[^1];
                if (!HostSpellingKey(leaf).Equals(key, StringComparison.Ordinal)
                    && !HostSpellingKey(e.Name ?? "").Equals(key, StringComparison.Ordinal)) continue;
                // 扫到却又不在磁盘上了 = 目录正在被改名，这份结果不能用
                if (ResolveHostDir(gamePath, host) is null) continue;
                if (hosts.Add(host)) found = host;
            }
        }
        catch { return null; }
        return hosts.Count == 1 ? found : null;
    }

    /// <summary>包把翻译文件压在<b>包根</b>而不是 i18n/ 里时的搬家判定。实测 13273：整包只有
    /// <c>2544 Canon-Friendly Dialogue Expansion/zh.json</c> 一个文件，而 Content Patcher 与 SMAPI
    /// 只读 <c>i18n/&lt;语言&gt;.json</c>（宿主 i18n/default.json 开头那段给译者的说明、以及作者
    /// readme 都这么写）—— 按原样盖到宿主根目录，症状就是「装成功、游戏里毫无变化」，
    /// 正是这条链拒绝制造的那种静默。
    /// <para>判据不看文件名猜语言，只看两边自己声明的内容：包里裸在根目录的 json 只有【一个】、
    /// 宿主 i18n/ 里读得出键集、且这个 json 的顶层键 ≥60% 出现在那份键集里（实测 4669/4923＝94.8%）。
    /// content.json / manifest.json 是宿主根目录上有意义的名词，一律不搬。</para></summary>
    private static OverlayPlan WithI18nHome(string tempDir, OverlayPlan plan, string hostDir)
    {
        // 候选＝包里【唯一】一个裸在根目录的 json。再多一个裸 json 就说不清哪份是翻译，整包放弃搬家；
        // 旁边的 readme / license 之类不算竞争（它们照包内路径落在根目录，本来也不归谁读）。
        var bareJson = plan.RelPaths
            .Where(x => !x.Contains('/') && x.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).ToList();
        if (bareJson.Count != 1) return plan;
        var r = bareJson[0];
        if (r.Equals("content.json", StringComparison.OrdinalIgnoreCase)
            || r.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)) return plan;
        // 宿主根目录本来就有一个同名文件 = 作者就把它放这儿，不该动包的结构
        if (File.Exists(Path.Combine(hostDir, r.Replace('/', Path.DirectorySeparatorChar)))) return plan;
        if (JsonTopKeys(Path.Combine(tempDir,
                plan.PackRoot.Replace('/', Path.DirectorySeparatorChar),
                r.Replace('/', Path.DirectorySeparatorChar))) is not { Count: > 0 } packKeys) return plan;
        // 宿主 i18n/ 里一个 json 都读不出键集（没这个目录、或全是坏 json）⇒ 键重合必然为 0，
        // 下面那道比例闸就是这条证据本身，不再单列一条判不出东西的分支。
        var hostKeys = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var f in Directory.GetFiles(Path.Combine(hostDir, "i18n"), "*.json"))
                if (JsonTopKeys(f) is { } ks)
                    foreach (var k in ks) hostKeys.Add(k);
        }
        catch { return plan; }
        if (packKeys.Count(k => hostKeys.Contains(k)) * 100 < packKeys.Count * 60) return plan;
        return plan with
        {
            DstPaths = plan.RelPaths.Select(x => x == r ? "i18n/" + r : x).ToList()
        };
    }

    /// <summary>一个 json 的顶层键集合（容忍 SMAPI 的注释/尾逗号）。不是对象或读不了都算拿不到。</summary>
    private static HashSet<string>? JsonTopKeys(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(ReadManifestText(file), ManifestJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in doc.RootElement.EnumerateObject()) set.Add(p.Name);
            return set;
        }
        catch { return null; }
    }

    internal static string OverlayRefusal(OverlayFail fail, string gamePath, string hostGuess,
        NexusRequirement? soleHost = null)
    {
        // 页面上写了什么，几句拒绝都要能带上：认不出宿主 ≠ 页面上没写。实测 30539
        // 「Alternative Portraits and Sprites for Childhood Sweetheart Caroline (Overhaul)」
        // 整包是 assets/Portraits + assets/Sprites（原版素材层名，不是 mod 目录名），路径判据
        // 一条都不命中，而它页面登记的前置明写着 #16220。这句只陈述"页面登记了什么"，
        // 不替我们推断它是不是覆盖宿主 —— 一个只登记 Content Patcher 的独立 CP 包也会走到这里，
        // 那种情况前置是依赖不是宿主。
        var declared = soleHost is { } h
            ? LocService.Tf("这个包在 N 网登记的前置是「{0}」{1}。", h.ModName.Trim(),
                h.ModId > 0 ? LocService.Tf("（N 网 #{0}）", h.ModId) : "")
            : "";
        switch (fail)
        {
            case OverlayFail.HostNotInstalled:
                // 这条通道不挑内容：汉化、贴图、兼容包都走这里（判据只看包内路径），
                // 所以措辞里不许假设它是「翻译包」—— 实测 mod 1993 的文件 35407 是 256 张 PNG。
                // 「要盖在谁身上」这句有出处时优先用出处：包内那层目录名是作者随手起的，
                // 而包自己页面上登记的那条前置带页面号，人能去核对
                // （实测 34705 的包内目录叫「[CP] Seeds N' Saplings - …」，真宿主 #31185 的页面名不带 [CP]）。
                // 作者有时只登记名字不填页面号（实测 18080 的 modId 就是 0）—— 那就只报名字，
                // 不许凭空捏一个「#0」。
                var (hostName, page) = soleHost is { } s
                    ? (s.ModName.Trim(), s.ModId > 0 ? LocService.Tf("（N 网 #{0}）", s.ModId) : "")
                    : (hostGuess, "");
                return LocService.Tf("这个包要盖在「{0}」{1}上，但你还没装它。先装被覆盖的那个 mod，再装这个包。",
                    hostName, page);
            case OverlayFail.AmbiguousConfigHost:
                return LocService.Tf("这个包要翻译的配置项，好几个已装 mod 都声明了（{0}），认不出该盖谁，已放弃安装。", hostGuess);
            case OverlayFail.MultiHost:
                // 这句的原因随判据一起变了：包内几个目录名【每一家都装着】时我们已经会一家一份地盖，
                // 所以走到这里只剩一种可能 —— 有名字对不上唯一一家已装 mod。原来那句
                // 「该盖哪家我们替你决定不了」在这种机器上是谎话（实机 53406 的包内两个目录
                // 就是已装捆绑包的两个子包，我们只是没去盘上看）。
                return LocService.Tf("这个包里放着好几个别的 mod 的目录（{0}），本机没能把每一个都对上一个已装的 mod —— 只盖一半会让这个包半生效，已放弃安装。请只装你需要的那一个，或手动放进对应 mod。", hostGuess);
            case OverlayFail.DeclaredHostKeyMismatch:
                // 与「判断不出宿主」分开说：宿主认出来了、本机也装着，是这份文件的内容跟它对不上。
                return LocService.Tf("这个包登记的前置是「{0}」，本机也装着它，可这份文件里的条目跟它自己 i18n 里的对不上 —— 它不是这个 mod 的汉化，盖上去也不会生效，已放弃安装。", hostGuess);
            case OverlayFail.HostKeysUnmatched:
                // 这句只陈述量出来的事：本机没有一家【过半】用得到这些条目。最接近的那一家可以报出来
                // （它是"第二名长什么样"的证据，不是宿主 —— 措辞里不许把它说成宿主），一条都没撞上时
                // 连这个名字都不许出现。实测 45719：298 条里 95 条撞在原版 UIInfoSuite2 上（32%），
                // 而它真正对应的「UI Info Suite 2 Alternative」本机没装。
                return (hostGuess.Length > 0
                    ? LocService.Tf("这个包要翻译的条目，本机装着的 mod 里没有一家对得上 —— 最接近的一家是「{0}」，也只对得上其中一小部分。它对应的那个 mod 你还没装，先装它再装这个汉化。", hostGuess)
                    : LocService.Tr("这个包要翻译的条目，本机装着的 mod 里没有一家用得到。它对应的那个 mod 你还没装，先装它再装这个汉化。")) + declared;
            default:
                return LocService.Tf("这个包里没有 manifest.json，包内路径也没说明它是给哪个 mod 的，判断不出宿主，已放弃安装。{0}", declared);
        }
    }

    /// <summary>判断一个<b>带 manifest</b> 的包是不是「某个已安装 mod 的汉化重打包」——
    /// 作者把整个 mod 目录重发一遍、manifest 原封不动（UID 与显示名都和已装那份相同），只把内容翻译过来。
    /// 只有这种情况才允许逐文件盖到人家目录里，其余一律交回正常安装。
    /// <para>前提：同 UID 的已安装目录<b>只有一个</b>，且包里 ≥60% 的文件在宿主目录里已有同名文件
    /// （2026-09-30 LewdDew Valley 实测：汉化包 306 个文件里 305 个原包里就有）。</para>
    /// <para>在此之上三层否决，只看包自己声明的身份，<b>不猜文件名</b>（用户 2026-10-03 定的规则：
    /// 市面上的语言替换包基本都和宿主同名 ⇒ 名字不同就是另一个 mod）：</para>
    /// <list type="number">
    /// <item><b>同一个 Nexus 页面的两份文件 ⇒ 不盖。</b>那是"这一页换一份下载"。
    /// 实测 Cat Valley(39379) 的 Interface 与 Mobile Interface：路径重合 92.6%、重合里字节相同 99.3%、
    /// 两份 manifest 连显示名都一样，只有 content.json 不同。盖上去会留下"目录叫 -Mobile、
    /// 身子是 Interface"的怪状态（用户机上那条 10-02 20:30 的记录就是这样）。</item>
    /// <item><b>显示名不同 ⇒ 不盖，且拒绝安装</b>（通过 <paramref name="uidTwinHost"/> 报给调用方）。
    /// 实测 Goshcakes 把每个季节 UI 都发成同一个 UniqueID=<c>Goshcakes.customMenuEtc</c>、
    /// UpdateKeys 写的是字面量 <c>"Nexus:???"</c>（页号识别不出东西），于是 Fall UI(22253) 与
    /// Spring UI(26299) 路径重合 96% 被当成汉化包，27 个文件全盖进对方目录。
    /// 判成"另一个 mod"后也<b>不能</b>走正常安装 —— 安装链的「同 UID 判重清扫」会把已装那份整个
    /// 移进回收站（实测夹具宿主 ModS19 就这么消失了），那比覆盖更糟。</item>
    /// <item><b>同名但重合素材过半都换了 ⇒ 不盖。</b>那是接手的改版，不是汉化
    /// （实测 Fall/Spring 字节相同率只有 7.7%，真汉化包 ~99%）。</item>
    /// </list>
    /// </summary>
    private OverlayPlan? ProbeTranslatedRepackage(string gamePath, string tempDir, string modRoot,
        string manifest, int? packNexusModId, JuniGridConfig? cfg, out string? uidTwinHost)
    {
        uidTwinHost = null;
        string uid, packName;
        try
        {
            using var doc = JsonDocument.Parse(ReadManifestText(manifest), ManifestJson);
            uid = doc.RootElement.TryGetProperty("UniqueID", out var u) ? (u.GetString() ?? "").Trim() : "";
            packName = doc.RootElement.TryGetProperty("Name", out var nm) ? (nm.GetString() ?? "").Trim() : "";
        }
        catch { return null; }
        if (uid.Length == 0) return null;

        List<ModEntry> sameUid;
        try
        {
            sameUid = ScanRaw(gamePath)
                .Where(e => !string.IsNullOrWhiteSpace(e.UniqueID)
                    && e.UniqueID.Trim().Equals(uid, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch { return null; }
        var hosts = sameUid.Select(e => NormalizeOverlayHost(e.Folder)).Distinct().ToList();
        if (hosts.Count != 1) return null;              // 0 个=不是重打包；多个=目标不唯一，别猜

        var host = hosts[0];
        var hostEntry = sameUid.First(e =>
            NormalizeOverlayHost(e.Folder).Equals(host, StringComparison.OrdinalIgnoreCase));
        var hostDir = ResolveHostDir(gamePath, host);
        if (hostDir is null) return null;
        // 已经同路径 = 普通的"更新那个 mod"，交给原链路，不要改成逐文件覆盖
        if (NormalizeOverlayHost(Path.GetFileName(modRoot)).Equals(host, StringComparison.OrdinalIgnoreCase))
            return null;

        string[] files;
        try { files = Directory.GetFiles(modRoot, "*", SearchOption.AllDirectories); }
        catch { return null; }
        if (files.Length < 2) return null;              // 单文件的走普通覆盖链就够了
        var rel = files.Select(f => Path.GetRelativePath(modRoot, f).Replace('\\', '/')).ToList();
        var missing = rel.Where(r => !File.Exists(Path.Combine(hostDir,
            r.Replace('/', Path.DirectorySeparatorChar)))).ToList();
        var overlap = rel.Count - missing.Count;

        // 三层判据，只看包自己声明的身份，不再猜文件名（用户 2026-10-03 定的规则：
        // 市面上的语言替换包基本都和宿主同名 ⇒ 名字不同就是另一个 mod）。
        var samePage = packNexusModId is > 0 && (hostEntry.NexusModId == packNexusModId
            || InstalledPageOf(cfg, host) == packNexusModId);

        // ① 同一个 Nexus 页面的两份文件 ＝ "这一页换一份下载"，不是谁的汉化。交回正常安装：
        //    新那份成自己的目录、旧的同 UID 副本进 app 回收站（可恢复），不逐文件盖。
        //    实测 Cat Valley(39379) 的 Interface 与 Mobile Interface：路径重合 92.6%、重合里字节
        //    相同 99.3%、两份 manifest 连显示名都一模一样，只有 content.json 不同 ——
        //    作者就是在同一页发两个可选布局；盖上去会留下"目录叫 -Mobile、身子是 Interface"的怪状态。
        if (samePage) return null;

        // ② 显示名不同 ＝ 作者眼里就是两个 mod。实测 Goshcakes 把每个季节 UI 都发成同一个
        //    UniqueID=Goshcakes.customMenuEtc，Name 分别叫 Spring UI / Fall UI（Fall 盖掉 Spring
        //    那次就是栽在"只看 UID + 路径重合"上）。这种既不能盖（人家毁了）也不能走正常安装 ——
        //    安装链的「同 UID 判重清扫」会把已装那份整个移进回收站（实测夹具宿主 ModS19 就这么没了），
        //    那比覆盖更糟。所以报给调用方：拒绝安装，两边都不动。
        if (packName.Length > 0
            && !packName.Equals((hostEntry.Name ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
        {
            uidTwinHost = string.IsNullOrWhiteSpace(hostEntry.Name) ? host : hostEntry.Name.Trim();
            return null;
        }

        // ③ 同名同 UID、但重合素材过半都换了 ⇒ 是接手的改版，不是汉化（真汉化包素材字节不动：
        //    2026-09-30 LewdDew 实测 306 个文件里 305 个原包里就有）。别盖，交回正常安装。
        if (overlap > 0 && IdenticalOverlapRatio(modRoot, hostDir, rel) < 50) return null;

        if (overlap * 100 < files.Length * 60) return null;   // 目录结构对不上 → 它是要新增的另一个 mod
        return new OverlayPlan(host, rel, Path.GetRelativePath(tempDir, modRoot).Replace('\\', '/'));
    }

    /// <summary>重合文件里"字节完全相同"的百分比（0-100）。分母是【重合且读得到】的文件数 ——
    /// 长度不同就是"不相同"，必须计入分母（早先写成 continue 跳过，分母只剩长度恰好一样的那几个，
    /// 比率虚高到 100%，整道闸门形同没有，被 OV19 当场抓出来）。先比长度再算哈希，
    /// 是因为姊妹变体改的是图，长度几乎必然不同，这样大部分文件不用读进内存就能判掉。</summary>
    private static int IdenticalOverlapRatio(string modRoot, string hostDir, List<string> rel)
    {
        var same = 0;
        var total = 0;
        foreach (var r in rel)
        {
            var packFile = Path.Combine(modRoot, r.Replace('/', Path.DirectorySeparatorChar));
            var hostFile = Path.Combine(hostDir, r.Replace('/', Path.DirectorySeparatorChar));
            long packLen, hostLen;
            try
            {
                packLen = new FileInfo(packFile).Length;
                hostLen = new FileInfo(hostFile).Length;
            }
            catch { continue; }                          // 读不到的文件不进分母，免得把"不知道"当成"不同"
            total++;
            if (packLen != hostLen) continue;            // 长度都不同 ⇒ 确定不相同（计入分母，不算相同）
            if (packLen > 0 && Sha256Of(packFile) == Sha256Of(hostFile)) same++;
        }
        return total == 0 ? 0 : (int)(same * 100L / total);
    }

    /// <summary>这条已装目录当初是从哪个 Nexus 页面装进来的。同一个 mod 在安装记录里往往有多把键
    /// （UID、完整目录、末段目录、显示名），只有【完整目录】那把是宿主自己的身份：末段与显示名那两把
    /// 会被后装的汉化包改写（2026-09-30 LewdDew 实测：UID 与完整目录仍是原版页 7222，末段键已被改写
    /// 成汉化页 49428）。拿那两把判"同一页"就会把真翻译包也一起拦掉。</summary>
    private static int InstalledPageOf(JuniGridConfig? cfg, string host)
    {
        if (cfg is null) return 0;
        foreach (var kv in cfg.ModNexusInstalls)
            if (kv.Value.NexusModId > 0
                && NormalizeOverlayHost(kv.Key).Equals(host, StringComparison.OrdinalIgnoreCase))
                return kv.Value.NexusModId;
        return 0;
    }
}
