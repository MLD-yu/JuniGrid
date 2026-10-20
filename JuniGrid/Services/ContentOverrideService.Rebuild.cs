using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace JuniGrid.Services;

public static partial class ContentOverrideService
{
    /// <summary>本次进程里已经试过的原包目录（键 = 游戏目录|目录名）。
    /// 认不回来的包（真漂移）每次都重算一遍没有意义：解析一个 91 张图的包是秒级，
    /// 而内容包页的 <c>Reload</c> 会被按钮反复调用。</summary>
    private static readonly HashSet<string> _rebuildTried = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>盘上有没有"账上完全没记着"的原包目录 —— <see cref="RebuildFromDisk"/> 的廉价前置。
    /// 只做目录名与账本 <c>PackDir</c>/<c>PackName</c> 的集合比较，一个文件都不解析；
    /// 正常情况（账实一致）这里直接返回 false，页面按原路走。</summary>
    public static bool NeedsRebuild(ConfigService cfg, string gamePath)
    {
        var root = PacksRoot(gamePath);
        if (!Directory.Exists(root)) return false;
        var accounted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in cfg.Current.ContentOverrides.Values)
        {
            if (r.PackDir.Length > 0) accounted.Add(r.PackDir);
            if (r.PackName.Length > 0) accounted.Add(r.PackName);
        }
        try
        {
            foreach (var d in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(d);
                if (name.StartsWith('.')) continue;
                if (accounted.Contains(name)) continue;
                if (_rebuildTried.Contains(gamePath + "|" + name)) continue;
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>下载缓存里【zip 条目清单】→ 页号（zip 名就写着 <c>nxm-&lt;页号&gt;-&lt;文件号&gt;.zip</c>）。
    /// 同一份清单对应多个不同页号时那个页号也留着（调用方看到"不唯一"就不认）。</summary>
    private static Dictionary<string, List<int>> CacheZipPages()
    {
        var map = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        try
        {
            var dir = StoragePaths.DownloadsDir;
            if (!Directory.Exists(dir)) return map;
            foreach (var z in Directory.EnumerateFiles(dir, "*.zip"))
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    Path.GetFileName(z), @"^(?:nxm|direct|dep)-(\d+)-");
                if (!m.Success || !int.TryParse(m.Groups[1].Value, out var page) || page <= 0) continue;
                if (ZipEntrySets(z) is not { } variants) continue;
                foreach (var sig in variants)
                {
                    if (!map.TryGetValue(sig, out var list)) map[sig] = list = new List<int>();
                    if (!list.Contains(page)) list.Add(page);
                }
            }
        }
        catch (Exception ex) { AppLog.Warn("ContentOverride", "[页号回填] 扫下载缓存失败：" + ex.Message); }
        return map;
    }

    /// <summary>一个压缩包的条目清单签名，两种写法都给：压缩包带公共根目录时，
    /// 原包备份里那一层可能被剥掉也可能保留（取决于当初解包到哪一层）。
    /// 读不出清单（坏档、空包、加密 rar）⇒ null，调用方跳过，绝不猜。</summary>
    private static List<string>? ZipEntrySets(string zipPath)
    {
        try
        {
            if (EntryNames(zipPath) is not { } entries) return null;
            var names = entries
                .Where(p => !p.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase)
                            && !Path.GetFileName(p).Equals(".DS_Store", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.TrimStart('/').ToLowerInvariant())
                .Where(p => p.Length > 0)
                .Distinct().ToList();
            if (names.Count == 0) return null;
            var raw = string.Join("\n", names.OrderBy(s => s, StringComparer.Ordinal));
            var first = names[0].Split('/')[0];
            var outp = new List<string> { raw };
            if (names.All(n => n.Contains('/') && n.Split('/')[0] == first))
                outp.Add(string.Join("\n", names.Select(n => n[(first.Length + 1)..])
                    .OrderBy(s => s, StringComparer.Ordinal)));
            return outp;
        }
        catch { return null; }
    }

    /// <summary>取压缩包条目名清单。<b>按文件头认格式，不信扩展名</b> —— 安装器把所有落盘名一律写成
    /// <c>.zip</c>（实机 2026-10-08：缓存里 193 个"zip"有 14 个实为 RAR/7z）。
    /// <para>zip 的条目名按 GBK 解，与 <c>ModService.ExtractToDirectory</c> 同一套：中文作者压的包大量用
    /// GBK 存条目名且不置 UTF-8 标志位，用默认 UTF-8 读会把「[xnb]三色-Tricolor」读成一串乱码 ⇒
    /// 与原包目录的清单【看起来】不相等 ⇒ 页号白白认不回来。</para>
    /// <para>rar/7z 走 SharpCompress，与安装解压用的是同一套。<b>安装能吃的格式，认页号时也必须能吃</b>：
    /// 否则从 RAR 装的内容包永远接不回详情页 —— 实机同一轮里剩下"找不到"的 4 个包
    /// （Better Artifact Digspots / Galarian Ponyta / Starry Sky / Yuriyuri's Hairstyles）
    /// 清单逐条躺在 nxm-774、nxm-4618、nxm-1310、nxm-2393 这四个 RAR 里。</para></summary>
    private static List<string>? EntryNames(string zipPath)
    {
        switch (ModService.DetectArchiveFormat(zipPath))
        {
            case "zip":
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                using var z = System.IO.Compression.ZipFile.Open(zipPath,
                    System.IO.Compression.ZipArchiveMode.Read, System.Text.Encoding.GetEncoding(936));
                return z.Entries
                    .Where(e => e.Length > 0 || !e.FullName.EndsWith('/'))
                    .Select(e => e.FullName.Replace('\\', '/')).ToList();
            }
            case "rar":
            {
                using var a = SharpCompress.Archives.Rar.RarArchive.OpenArchive(zipPath);
                return a.Entries.Where(e => !e.IsDirectory)
                    .Select(e => (e.Key ?? "").Replace('\\', '/')).ToList();
            }
            case "7z":
            {
                using var a = SharpCompress.Archives.SevenZip.SevenZipArchive.OpenArchive(zipPath);
                return a.Entries.Where(e => !e.IsDirectory)
                    .Select(e => (e.Key ?? "").Replace('\\', '/')).ToList();
            }
            default: return null;   // 裸 xnb / 裸 png / html 错误页 / 坏档：没有"清单"可谈
        }
    }

    /// <summary>原包目录的文件清单签名（与 <see cref="ZipEntrySets"/> 同一套规范化）。</summary>
    private static string PackDirSignature(string packDir)
        => string.Join("\n", Directory.EnumerateFiles(packDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(packDir, f).Replace('\\', '/').ToLowerInvariant())
            .Where(p => !p.StartsWith("__macosx/") && Path.GetFileName(p) != ".ds_store")
            .OrderBy(s => s, StringComparer.Ordinal));

    /// <summary>盘上的页号回填：原包目录的文件清单与缓存里某个 zip <b>全等</b>，且该清单只指向
    /// <b>一个</b>页号，才认。实测 2026-10-08：12 个从盘上认回来的包里 5 个这样找回了页号。
    /// <para>为什么判据必须这么窄：松一档（"包里每个文件都能在某个压缩包里找到"）时，
    /// 只有一个文件的包会同时命中 3 个页号（实测 Yuriyuri's Hairstyles → 20581/1083/…），
    /// 而写错页号比没页号更糟 —— 卡片上「访问 mod 详情」会把人领到别人的 mod 页，
    /// 封面与分类也会跟着错到别人身上。</para></summary>
    private static int? PageIdForPack(Dictionary<string, List<int>> zipPages, string packDir)
    {
        try
        {
            if (zipPages.TryGetValue(PackDirSignature(packDir), out var pages) && pages.Count == 1)
                return pages[0];
        }
        catch { }
        return null;
    }

    /// <summary>给账上<b>没有页号</b>的记录补页号 —— 判据与 <see cref="PageIdForPack"/> 完全同源
    /// （下载缓存里某个 zip 的条目清单与原包目录<b>全等</b>，且该清单只指向<b>一个</b>页号）。
    ///
    /// <para>为什么单独要这一步：从盘上认回来的账只有包名，而详情页、封面、分类全都挂在页号上
    /// （<c>EnsureMetaAsync</c> 只给带页号的记录补元数据）。用户看到的就是"包和 N 网分离了"：
    /// 卡片是字母块、12 个包全挤在「未分类」、点「访问 mod 详情」只有一句"并非从 Nexus 安装"。</para>
    ///
    /// <para>补上页号的同时把 slot 从"包名"改写成"N&lt;页号&gt;"，与正常安装的记录同一个形状 ——
    /// 不改的话他以后重新下载这个包，会按新 slot 再写一套账，界面上同一个包两张卡。</para></summary>
    public static int BindPageIds(ConfigService cfg, string gamePath)
    {
        var slots = cfg.Current.ContentOverrides.Values
            .Where(v => v.NexusModId is null && v.PackDir.Length > 0)
            .Select(v => v.Slot).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (slots.Count == 0) return 0;

        var zipPages = CacheZipPages();
        if (zipPages.Count == 0) return 0;
        int bound = 0;
        foreach (var slot in slots)
        {
            var rows = cfg.Current.ContentOverrides.Values
                .Where(v => v.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase)).ToList();
            if (rows.Count == 0) continue;
            var dir = PackOriginalDir(gamePath, rows[0].PackDir);
            if (!Directory.Exists(dir)) continue;
            if (PageIdForPack(zipPages, dir) is not int page) continue;
            RekeySlot(cfg, slot, SlotOf(rows[0].PackName, page), page);
            bound++;
        }
        if (bound > 0)
        {
            cfg.Save(cfg.Current);
            AppLog.Warn("ContentOverride", $"[页号回填] {bound} 个包按下载缓存认回页号，详情页/封面/分类已可回填");
        }
        return bound;
    }

    /// <summary>把一个包的所有记录从旧 slot 搬到新 slot，并顺手清掉封面与分类
    /// （那两样是按页号去 N 网取的，换页号后必须重取，不能留着旧的）。</summary>
    private static void RekeySlot(ConfigService cfg, string oldSlot, string newSlot, int page)
    {
        if (oldSlot.Equals(newSlot, StringComparison.OrdinalIgnoreCase)) return;
        var moving = cfg.Current.ContentOverrides
            .Where(kv => kv.Value.Slot.Equals(oldSlot, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var kv in moving) cfg.Current.ContentOverrides.Remove(kv.Key);
        foreach (var kv in moving)
        {
            kv.Value.Slot = newSlot;
            kv.Value.NexusModId = page;
            kv.Value.PackCover = "";
            kv.Value.CategoryName = "";
            cfg.Current.ContentOverrides[KeyOf(kv.Value.Asset, newSlot)] = kv.Value;
        }
    }

    /// <summary>盘上有、账上没有的包被认回来的结果。</summary>
    /// <param name="Packs">认回来的包数。</param>
    /// <param name="Records">写回（或试算出）的资产条数。</param>
    /// <param name="Applied">判为【正在生效】的条数（现值 == 我们那份）。</param>
    /// <param name="Drifted">现值既不是我们那份也不是原版 ⇒ 一条都没认的资产数。</param>
    /// <param name="Already">账上本来就有记录、因此没动的包数。</param>
    /// <param name="Unresolved">一个目标都没认回来的包，带原因。</param>
    public sealed record RebuildReport(int Packs, int Records, int Applied, int Drifted, int Already,
        List<string> Unresolved);

    /// <summary>以 <c>asset-packs\&lt;目录&gt;</c> 为起点，把丢掉的账认回来。
    ///
    /// <para>为什么需要它：全仓原来<b>没有一条从盘到账的路</b>。页面、开关、还原、
    /// <see cref="AuditOrphans"/> 的起点都是 <see cref="JuniGridConfig.ContentOverrides"/>，
    /// 盘只被用作"这条账的包还活着吗"的证据（<c>AuditOrphans</c> 里那句
    /// <c>foreach (var d in EnumerateDirectories(PacksRoot))</c> 只往一个集合里塞名字）。
    /// 账一旦为空，那 13 个原包目录就再也没有第二个人看它们一眼 —— 而游戏本体里
    /// 147 个文件仍然被它们盖着（2026-10-08 实机：配置被一个模型里没有这个键的旧安装副本
    /// 整份 <c>Save</c> 覆盖，账清了、盘没动）。</para>
    ///
    /// <para><b>三方对得上才认</b>，每条资产单独判，判不动的就跳过（绝不猜）：
    /// ① 拿 <see cref="Prepare"/> 对原包目录重跑一遍解析，算出它应当替换哪些资产、字节是什么；
    /// ② 现值 == 我们算出来的那份 ⇒ 生效中；现值 == <c>content-originals</c> 那一层里的原版 ⇒ 关着；
    /// 现值是<b>同包的另一个配色</b>（用户换过色）或<b>别的包那份副本</b> ⇒ 也算关着，
    /// 这两种都不是漂移，判成漂移会让整包回不来；
    /// ③ 副本按【字节】在 <c>content-derived</c> 里找现成的（<see cref="StoreCopy"/> 用的是随机 id，
    /// 不认字节的活就会把盘上那批变成孤儿）。
    /// 三张都不是 ⇒ 这条不写，算进 <see cref="RebuildReport.Drifted"/>。</para>
    ///
    /// <para>⚠ <b>绝不从 Content 抄原版</b>：包还盖在盘上时抄到的就是【我们自己那张】，
    /// 之后还原会把我们的图当原版退回去（<see cref="Rejudge"/> 里同一条纪律）。
    /// 原版只从 <c>content-originals/&lt;版本&gt;/</c> 读，读不到就按"我们新增的文件"处理。</para>
    ///
    /// <para><paramref name="dryRun"/> 只算不写（连副本都不另落）—— 先看表再决定，用于实机核对。</para></summary>
    public static RebuildReport RebuildFromDisk(ConfigService cfg, string gamePath, bool dryRun)
    {
        var root = PacksRoot(gamePath);
        if (!Directory.Exists(root)) return new RebuildReport(0, 0, 0, 0, 0, new());

        // 账上记着的包一律不动：这条路只补"账上完全没有、盘上却躺着原包"的那一批。
        var accounted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in cfg.Current.ContentOverrides.Values)
        {
            if (r.PackDir.Length > 0) accounted.Add(r.PackDir);
            if (r.PackName.Length > 0) accounted.Add(r.PackName);
        }

        string[] dirs;
        try { dirs = Directory.GetDirectories(root); }
        catch { return new RebuildReport(0, 0, 0, 0, accounted.Count, new()); }

        var stores = StoreIndexByBytes(gamePath);
        var layers = OriginalLayers(gamePath);
        var zipPages = CacheZipPages();          // 页号只可能从下载缓存的 zip 名里认回来
        var pending = new List<(string Key, ContentOverrideRecord Rec)>();
        var unresolved = new List<string>();
        int packs = 0, applied = 0, drifted = 0, already = 0;

        foreach (var d in dirs)
        {
            var name = Path.GetFileName(d);
            if (name.StartsWith('.')) continue;                 // .store 这类内部层不是包
            if (accounted.Contains(name)) { already++; continue; }
            _rebuildTried.Add(gamePath + "|" + name);           // 本进程只试一次（认不回来也一样）

            PlanResult plan;
            try { plan = Prepare(gamePath, d); }
            catch (Exception ex) { unresolved.Add($"{name}：解析失败 {ex.Message}"); continue; }
            if (plan.Items.Count == 0)
            {
                unresolved.Add($"{name}：包内没有一个能与本体图像对应的文件");
                continue;
            }

            var page = PageIdForPack(zipPages, d);
            var slot = SlotOf(name, page);
            var wrote = 0;
            foreach (var it in plan.Items)
            {
                var live = AssetFullPath(gamePath, it.Asset);
                var cur = File.Exists(live) ? Sha256(live) : "";
                var mine = Sha256Of(it.Bytes);
                var (origHash, origVer) = ReadOriginal(gamePath, it.Asset, layers);
                var storeId = stores.TryGetValue((it.Asset.ToLowerInvariant(), mine), out var sid) ? sid : "";
                var inPlace = it;                       // 在位的那一版：默认是它，换过配色就不是

                bool on;
                if (cur.Length > 0 && cur == mine) on = true;
                else if (cur.Length > 0 && plan.VariantItems.TryGetValue(it.Asset, out var cands)
                         && cands.FirstOrDefault(c => Sha256Of(c.Bytes) == cur) is { } hit)
                {
                    // 用户在这个包里换过配色：在位的是【同包的另一版】，不是漂移。
                    // 认它，否则这个包整包被判"对不上"，换过配色的包一个都回不来。
                    inPlace = hit; mine = cur; on = true;
                    storeId = stores.TryGetValue((it.Asset.ToLowerInvariant(), cur), out var s2) ? s2 : "";
                }
                else if (origHash.Length > 0 && cur == origHash) on = false;
                else if (origHash.Length == 0 && cur.Length == 0 && storeId.Length > 0) on = false;
                else if (cur.Length > 0 && stores.ContainsKey((it.Asset.ToLowerInvariant(), cur)))
                    on = false;                         // 现值是【别的包】那份副本 ⇒ 这一个是关着的
                else
                {
                    drifted++;                          // 三张都不是 ⇒ 不认，也别猜
                    continue;
                }

                if (on) applied++;
                // 试算一个字节都不许写：StoreCopy 会往 content-derived 落新目录
                if (on && !dryRun && storeId.Length == 0) storeId = StoreCopy(gamePath, inPlace.Bytes, inPlace.Asset);
                pending.Add((KeyOf(it.Asset, slot), new ContentOverrideRecord
                {
                    Asset = it.Asset, Slot = slot, PackName = name, PackTitle = name, PackDir = name,
                    NexusModId = page,
                    OriginalSha256 = origHash, AppliedSha256 = mine, StoreId = storeId, Enabled = on,
                    Encoded = inPlace.Encoded,
                    Version = origVer.Length > 0 ? origVer : OriginalVersionSegment(gamePath),
                    SkippedCount = plan.Rejects.Count, SkippedNote = string.Join("\n", plan.Rejects.Take(8)),
                    ResizedCount = plan.Resized.Count, ResizedNote = string.Join("\n", plan.Resized.Take(8)),
                    Variants = plan.VariantLabels.TryGetValue(it.Asset, out var vs) ? vs : new(),
                    VariantLabel = inPlace.Label,
                    VariantFacts = plan.VariantItems.TryGetValue(it.Asset, out var vi)
                        ? vi.Select(v => new ContentOverrideVariant(v.Label, v.Note)).ToList() : new(),
                }));
                wrote++;
            }
            if (wrote > 0) packs++;
            else unresolved.Add($"{name}：{plan.Items.Count} 个目标一条都对不上（现值既不是我们那份也不是原版）");
        }

        if (!dryRun && pending.Count > 0)
        {
            foreach (var (key, rec) in pending) cfg.Current.ContentOverrides[key] = rec;
            cfg.Save(cfg.Current);
        }
        AppLog.Warn("ContentOverride",
            $"[账本重建{(dryRun ? "·试算" : "")}] 认回 {packs} 个包 / {pending.Count} 条（生效中 {applied}），"
            + $"对不上 {drifted} 条，账上已有 {already} 个包未动");
        return new RebuildReport(packs, pending.Count, applied, drifted, already, unresolved);
    }

    /// <summary><c>content-originals</c> 下面实际存在的版本层（没有先后，逐个试）。</summary>
    private static List<string> OriginalLayers(string gamePath)
    {
        var list = new List<string>();
        try
        {
            if (Directory.Exists(OriginalsRoot(gamePath)))
                list.AddRange(Directory.GetDirectories(OriginalsRoot(gamePath)).Select(Path.GetFileName)!);
        }
        catch { }
        return list;
    }

    /// <summary>按留底读某张资产的原版哈希，以及它在<b>哪一版</b>那一层。都没有 ⇒ ("", "")。</summary>
    private static (string Hash, string Version) ReadOriginal(string gamePath, string asset, List<string> layers)
    {
        foreach (var v in layers)
        {
            var p = OriginalPath(gamePath, asset, v);
            if (File.Exists(p)) return (Sha256(p), v);
        }
        return ("", "");
    }

    /// <summary>盘上现成的派生副本，按【资产 + 字节哈希】索引 —— 重建时拿它认回 <c>StoreId</c>，
    /// 而不是再写一份新的（<see cref="StoreCopy"/> 用随机 id，另写就等于把盘上那批变成孤儿）。</summary>
    private static Dictionary<(string Asset, string Hash), string> StoreIndexByBytes(string gamePath)
    {
        var map = new Dictionary<(string, string), string>();
        var root = DerivedRoot(gamePath);
        if (!Directory.Exists(root)) return map;
        foreach (var dir in Directory.GetDirectories(root))
        {
            var id = Path.GetFileName(dir);
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(dir, f).Replace('\\', '/');
                if (!rel.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase)) continue;
                try { map[(rel[..^4].ToLowerInvariant(), Sha256(f))] = id; } catch { }
            }
        }
        return map;
    }
}
