using System.IO;
using System.Text.RegularExpressions;

namespace JuniGrid.Services;

public static partial class ContentOverrideService
{
    // ───────────────────── 开关 / 删除 / 还原 ─────────────────────

    /// <summary>开或关一条。
    /// 「关→关」必须什么都不做：关掉一条已经关着的记录若走还原，会把同一个资产上【在位的那份兄弟】
    /// 当成自己的原版退回游戏目录（覆盖包那条踩过的坑）。
    /// 「开→开」反而要重新盖一次：盘上那份可能已经被 Steam 验证完整性、或游戏更新换回原版了，
    /// 而账还写着"开着" —— 这时候再点一下就是修复入口，不该要求人先关再开。</summary>
    public static string? SetEnabled(ConfigService cfg, string gamePath, string key, bool enabled)
    {
        if (!cfg.Current.ContentOverrides.TryGetValue(key, out var r)) return LocService.Tr("未找到该记录");
        if (!enabled && !r.Enabled) return null;
        if (enabled)
        {
            // 单行开关也要认版本：回放旧字节 = 把上一个版本编的表画进当前本体（见 SetPackEnabled）。
            var one = StaleRows(gamePath, new List<ContentOverrideRecord> { r });
            if (one.Count > 0)
                return ForVersion(RebuildForCurrentVersion(cfg, gamePath, r.Slot,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { r.Asset }));
        }
        try
        {
            if (enabled)
            {
                var copy = StoreFile(gamePath, r.StoreId, r.Asset);
                if (r.StoreId.Length == 0 || !File.Exists(copy))
                    // 从盘上认回来的账只给"真在位"的那几条编副本（见 RebuildFromDisk），所以"副本没了"
                    // 不等于"包没了"——原包备份往往好端端躺着。先按当前本体重编一次再放弃，
                    // 否则用户看到的是"要点启用却被告知必须重装"，而东西就在 asset-packs 里。
                    return RebuildForCurrentVersion(cfg, gamePath, r.Slot,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { r.Asset });
                var dst = AssetFullPath(gamePath, r.Asset);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(copy, dst, overwrite: true);
                foreach (var other in cfg.Current.ContentOverrides.Values.Where(o =>
                             o.Enabled && !ReferenceEquals(o, r)
                             && o.Asset.Equals(r.Asset, StringComparison.OrdinalIgnoreCase)).ToList())
                    other.Enabled = false;
                r.Enabled = true;
            }
            else
            {
                // 盘上那张既不是我们放的、也不是原版 ⇒ 动它之前先停下：盖回原版看起来是"还原成功"，
                // 实际抹掉的是第三方那张，而且账上没有任何地方说过它存在过。
                if (ForeignOnDisk(gamePath, r))
                    return LocService.Tr("该图像当前既非本程序写入也非原版（可能被其他 mod 或手动修改），已停止还原");
                if (!RevertAsset(gamePath, r)) return LocService.Tr("还原失败，游戏本体未作改动，可重试");
                r.Enabled = false;
            }
            cfg.Save(cfg.Current);
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Warn("ContentOverride", ex.Message);
            return LocService.Tr("操作失败：") + ex.Message;
        }
    }

    /// <summary>把一张退回原版；原本没有这张图时删掉我们新增的文件。r 还没落账时传 null。
    /// <paramref name="version"/> 传记录自己那一版 —— 切过版本之后原版躺在旧层里，
    /// 只查当前层会读不到，然后把旧覆盖当成"我们新增的文件"删掉。</summary>
    private static bool RevertAsset(string gamePath, string asset, string? appliedSha = null,
        string? version = null)
    {
        try
        {
            var dst = AssetFullPath(gamePath, asset);
            var orig = OriginalPath(gamePath, asset, version);
            if (File.Exists(orig))
            {
                File.Copy(orig, dst, overwrite: true);
                return true;
            }
            // 没有原版仓库：要么这本来就是我们新增的文件（删），要么动不了（报出来，别装成功）
            if (File.Exists(dst) && appliedSha is not null && Sha256(dst) == appliedSha) File.Delete(dst);
            return !File.Exists(dst) || appliedSha is null;
        }
        catch (Exception ex)
        {
            AppLog.Warn("ContentOverride", ex.Message);
            return false;
        }
    }
    private static bool RevertAsset(string gamePath, ContentOverrideRecord r)
        => RevertAsset(gamePath, r.Asset, r.AppliedSha256, r.Version);

    /// <summary>丢掉一张的原版仓库：只删它自己，再逐层回收空目录。
    /// 老写法是「递归删它的父目录」，而 <c>content-originals/&lt;版本&gt;/Portraits/</c> 里躺着的是
    /// 别的角色的原版 —— 删掉就再也回不去，而账上那些记录还以为自己在位，还原会直接失败。</summary>
    private static void DropOriginal(string gamePath, string asset, string? version = null)
    {
        var orig = OriginalPath(gamePath, asset, version);
        if (!File.Exists(orig)) return;
        File.Delete(orig);
        var stop = OriginalsRoot(gamePath) + Path.DirectorySeparatorChar;
        for (var dir = Path.GetDirectoryName(orig);
             dir is not null && dir.StartsWith(stop, StringComparison.OrdinalIgnoreCase)
             && !Directory.EnumerateFileSystemEntries(dir).Any();
             dir = Path.GetDirectoryName(dir))
            Directory.Delete(dir);
    }

    /// <summary>删一条记录。只有这个资产上的【最后一条】记录被删时才还原并丢掉原版仓库 ——
    /// 早删会把还活在盘上的兄弟那份内容当"原版"退回去。
    /// <para>盘上那张是别人放的（<see cref="ForeignOnDisk"/>）时【一条都不动】：这时删账会得到
    /// "界面上没这个包了、本体里却躺着一张没人认领的图"，正是他报过的那类看不见的事实。
    /// 返回要说给用户的话，null = 删干净了。</para></summary>
    public static string? Delete(ConfigService cfg, string gamePath, string key)
    {
        if (!cfg.Current.ContentOverrides.TryGetValue(key, out var r)) return null;
        var held = cfg.Current.ContentOverrides.Values
            .Where(o => o.Asset.Equals(r.Asset, StringComparison.OrdinalIgnoreCase)
                        && ForeignOnDisk(gamePath, o)).ToList();
        if (held.Count > 0)
            return LocService.Tf("有 {0} 个图像文件当前既非本程序写入也非原版（可能被其他 mod 或手动修改），已停止删除，记录与文件均保留", held.Count);
        var siblings = cfg.Current.ContentOverrides.Values
            .Count(o => o.Asset.Equals(r.Asset, StringComparison.OrdinalIgnoreCase));
        if (siblings <= 1)
        {
            if (r.Enabled) RevertAsset(gamePath, r);
            try { DropOriginal(gamePath, r.Asset, r.Version); }
            catch (Exception ex) { AppLog.Warn("ContentOverride", ex.Message); }
        }
        cfg.Current.ContentOverrides.Remove(key);
        if (r.StoreId.Length > 0) DropStore(gamePath, r.StoreId);
        cfg.Save(cfg.Current);
        return null;
    }

    /// <summary>删掉一个包：【整包一起删或整包不删】。逐张删的话，拦下一张时其它张已经没了，
    /// 用户看到的是"包被删了一半"，比删不掉更难解释。
    /// 返回要报给用户的话（null = 全删干净）。</summary>
    public static string? DeletePack(ConfigService cfg, string gamePath, string slot)
    {
        var mine = cfg.Current.ContentOverrides.Values
            .Where(r => r.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase)).ToList();
        var held = mine.Count(r => ForeignOnDisk(gamePath, r));
        if (held > 0)
            return LocService.Tf("有 {0} 个图像文件当前既非本程序写入也非原版（可能被其他 mod 或手动修改），已停止删除，记录与文件均保留", held);
        // 先记下原包备份的目录名：下面的循环会把账清空，清完就再也查不到这个包当初落在哪个文件夹了
        var packDir = mine.Select(r => r.PackDir).FirstOrDefault(d => d.Length > 0) ?? "";
        foreach (var r in mine) Delete(cfg, gamePath, KeyOf(r.Asset, r.Slot));
        DropPackBackup(gamePath, cfg, slot, packDir);
        return null;
    }

    /// <summary>删除一个包时，把它在 <c>asset-packs</c> 下的原包备份一起清掉。
    ///
    /// <para>以前只清账、不动这个目录，而全仓唯一会删 asset-packs 的代码是
    /// <see cref="PreserveOriginalPack"/> 里"重装同名包先清旧目录"那一条 —— 它靠【账上还有记录
    /// 指着这个目录名】认人。删除恰好把账清空，于是这个目录从此无人认领：
    /// 实测 2026-10-07 删掉 1554 张表那个包之后，210MB 的原包备份还躺在 asset-packs 里，
    /// 用户开文件夹看见"删掉的包还在"。</para>
    ///
    /// <para>只删这个 slot 自己记过的那个目录名；删前再确认没有别的 slot 还指着它
    ///（撞名会落 -2，理论上不会共用，但老账 PackDir 为空时我们本来也不知道目录是谁的，
    /// 那种情况不动手）。删失败只记日志 —— 账已经清了，备份留着比让删除整体报错强。</para></summary>
    private static void DropPackBackup(string gamePath, ConfigService cfg, string slot, string packDir)
    {
        if (packDir.Length == 0) return;
        if (cfg.Current.ContentOverrides.Values.Any(r =>
                !r.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase)
                && r.PackDir.Equals(packDir, StringComparison.OrdinalIgnoreCase))) return;
        var dir = PackOriginalDir(gamePath, packDir);
        if (!Directory.Exists(dir)) return;
        try
        {
            Directory.Delete(dir, true);
            AppLog.Warn("ContentOverride", $"[删除] 原包备份已清：asset-packs/{packDir}");
        }
        catch (Exception ex) { AppLog.Warn("ContentOverride", "原包备份删除失败（账已清，目录留着）：" + ex.Message); }
    }

    /// <summary>全部退回原版并清账。切游戏版本之前必须先跑它 —— 否则改过的 Content 会被
    /// 当成"那个版本的原版"整目录收进版本缓存（TryAdoptCurrentBody 是 Directory.Move）。
    /// <para><paramref name="HeldBack"/> = 因为盘上那张是别人放的而【没敢动】的张数。
    /// 这些行留着 Enabled，界面上继续标红，等人去看是什么盖的 —— 静默跳过比少还原一张更糟。</para></summary>
    public static (int Reverted, int HeldBack) RevertAll(ConfigService cfg, string gamePath)
    {
        var n = 0;
        var held = 0;
        foreach (var r in cfg.Current.ContentOverrides.Values.Where(r => r.Enabled).ToList())
        {
            if (ForeignOnDisk(gamePath, r)) { held++; continue; }
            if (RevertAsset(gamePath, r)) { r.Enabled = false; n++; }
        }
        cfg.Save(cfg.Current);
        if (held > 0) AppLog.Warn("ContentOverride", $"[全部还原] {held} 张盘上已是别人放的图，未覆盖");
        return (n, held);
    }

    // ───────────────────── 盘账一致性 ─────────────────────

    /// <summary>一条记录在当前本体上的实际状态。分开「早就被还原」与「被外来字节顶掉」是必要的：
    /// 前者最常见的原因是 Steam 验证了完整性或游戏更新换了本体，那是【已经回到原版】，
    /// 界面上报红色「已不在位」等于把一件已经做完了的事说成故障（用户 2026-10-07 指出过这类文案）。</summary>
    public enum OverrideState { Off, InPlace, AlreadyVanilla, Foreign, Gone }

    /// <summary>只看盘：这张图现在是谁的。<b>不问账上的开关</b> —— 需要判"底干不干净"的地方
    /// 要的就是这个；<see cref="StateOf"/> 在它之上多问一句"账上说开着吗"，那一问会把
    /// "账说关着、盘上还盖着我们的图"看成 Off。</summary>
    public static OverrideState DiskState(string gamePath, ContentOverrideRecord r)
    {
        var dst = AssetFullPath(gamePath, r.Asset);
        if (!File.Exists(dst)) return OverrideState.Gone;
        var now = Sha256(dst);
        if (now == r.AppliedSha256) return OverrideState.InPlace;
        if (r.OriginalSha256.Length > 0 && now == r.OriginalSha256) return OverrideState.AlreadyVanilla;
        var orig = OriginalPath(gamePath, r.Asset, r.Version);
        if (File.Exists(orig) && now == Sha256(orig)) return OverrideState.AlreadyVanilla;
        return OverrideState.Foreign;
    }

    public static OverrideState StateOf(string gamePath, ContentOverrideRecord r)
        => r.Enabled ? DiskState(gamePath, r) : OverrideState.Off;

    /// <summary>账上开着、盘上却不是那份字节，而且那份【既不是我们放的也不是原版】（= 别人或手动动过），
    /// 或者文件干脆没了。Steam 验证还原成原版那种不算，见 <see cref="StateOf"/>。</summary>
    public static bool Drifted(string gamePath, ContentOverrideRecord r)
        => StateOf(gamePath, r) is OverrideState.Foreign or OverrideState.Gone;

    /// <summary>还原前的一道闸：盘上那张既不是我们放上去的、也不是原版 ⇒ 别拿原版去盖它。
    /// 盖下去看着"还原成功"，实际抹掉的是第三方那张图，而且没有任何地方说过它存在过。
    /// 只有【开着】的行才谈得上：关着的行盘上本来就该是原版或别人的，还原是空操作。</summary>
    public static bool ForeignOnDisk(string gamePath, ContentOverrideRecord r)
        => StateOf(gamePath, r) == OverrideState.Foreign;

    /// <summary>一条"包已经不在了、但它对本体的覆盖还生效"的对账结果。
    /// 四个计数互斥、相加 = EnabledCount。</summary>
    public sealed record OrphanOverrideGroup(string Slot, string PackName, string PackDir,
        int EnabledCount, int SafeToRevert, int AlreadyReverted, int DriftedCount, int AddedOnly);

    /// <summary>孤儿对账：包已经不在了（Mods 下的 mod 目录、或 asset-packs 里的原包副本都没了），
    /// 但它写进游戏本体的覆盖【不会自己撤回】。
    ///
    /// <para>为什么会有这个洞：写盘的入口只有 <see cref="SetPackEnabled"/> 与 <see cref="DeletePack"/>，
    /// 而它俩的唯一调用点在素材页（<c>Packs.razor</c>）。Mods 页的删除/移入回收站走 <c>ModService</c>，
    /// 根本不碰 <c>contentOverrides</c>。2026-10-06 实测：一条 101 张 <c>Portraits/*</c> 的账，
    /// 源包 10-04 02:58 就被移进 <c>Mods\.junigrid_trash\</c>，覆盖在本体里躺了两天 ——
    /// 期间 SMAPI 每次启动报 <c>Some of the game's content files were modified or corrupted</c>，
    /// 而且它压住肖像页的一切选择（用户在 UI 里怎么设都看不到变化）。</para>
    ///
    /// <para>⚠ <b>存活判定不能用 <c>asset-packs/&lt;packDir&gt;</c></b>：那是【原包备份】，
    /// 按设计一直躺着（用途是"关掉再打开不必重新下载"）。拿它当存活证据，本案永远判不出来。
    /// 只认 <c>Mods</c> 树里带标记文件（<c>manifest.json</c> / <c>.junigrid.json</c>）的目录 ——
    /// 实测一级子目录里躺着 <c>assets</c>/<c>i18n</c> 这类通用名，只比目录名会误判。
    /// 带点号的【禁用态算活着】：那是用户在 Mods 页的明确决定，覆盖留着符合预期，不是孤儿。</para>
    ///
    /// <para>这一版【只点名、不写盘】：判定一旦误伤，自动还原会把用户故意留下的画风静默改掉。
    /// 逐条分四档，其中 <c>DriftedCount</c>（现值既不是我们那份也不是原版 = 别人或 Steam 动过）
    /// 与 <c>AddedOnly</c>（原版本来没这张，还原 = 删文件）永远不该被自动写。</para>
    ///
    /// <para><c>packDir</c> 与 <c>packName</c> 都为空的记录（老账）判不了 ⇒ 不报，别猜。</para>
    /// </summary>
    public static List<OrphanOverrideGroup> AuditOrphans(string gamePath, ConfigService cfg)
    {
        var live = LivePackNames(gamePath);
        // ⚠ 本体覆盖的包【不住在 Mods\】：Commit 把原包整份收进 <游戏>/JuniGrid/asset-packs/<名字>，
        //   「打开文件夹」打开的是它，还原时读原件也是从它读（见 PackOriginalDir）——
        //   所以它就是这类包的存活证据。只查 Mods 的后果实测过（2026-10-07 沙盒端到端）：
        //   刚装完两分钟的包立刻被判「包已删除，本体覆盖仍在生效」。
        try
        {
            if (Directory.Exists(PacksRoot(gamePath)))
                foreach (var d in Directory.EnumerateDirectories(PacksRoot(gamePath)))
                    live.Add(Path.GetFileName(d));
        }
        catch (Exception ex) { AppLog.Warn("ContentOverride", "asset-packs 存活枚举失败: " + ex.Message); }
        var outp = new List<OrphanOverrideGroup>();
        foreach (var g in cfg.Current.ContentOverrides.Values
                     .GroupBy(r => r.Slot, StringComparer.OrdinalIgnoreCase))
        {
            var on = g.Where(r => r.Enabled).ToList();
            if (on.Count == 0) continue;
            var dir = on.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.PackDir))?.PackDir ?? "";
            var name = on.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.PackName))?.PackName ?? "";
            if (dir.Length == 0 && name.Length == 0) continue;      // 判不了就不点名
            if (live.Contains(dir) || (name.Length > 0 && live.Contains(name))) continue;

            int safe = 0, already = 0, drift = 0, added = 0;
            foreach (var r in on)
            {
                if (r.OriginalSha256.Length == 0) { added++; continue; }   // 原版没这张 ⇒ 还原=删文件
                var dst = AssetFullPath(gamePath, r.Asset);
                if (!File.Exists(dst)) { drift++; continue; }
                var now = Sha256(dst);
                if (now == r.AppliedSha256) safe++;
                else if (now == r.OriginalSha256) already++;                // 账没跟上，盘上已经是原版
                else drift++;                                                 // 别人放的 ⇒ 只报不覆盖
            }
            outp.Add(new OrphanOverrideGroup(g.Key, name, dir, on.Count, safe, already, drift, added));
        }
        return outp.OrderBy(x => x.PackName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Mods 树里【真的装着包】的目录名与 manifest 里的 Name。
    /// 深度限 3（SVE 的包在 <c>Stardew Valley Expanded/[CP] …</c> 这种两层下面），
    /// 跳过点号开头的目录本身 —— 但把它的名字去掉点号后计入（禁用态仍算"在"）。</summary>
    private static HashSet<string> LivePackNames(string gamePath)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mods = Path.Combine(gamePath, "Mods");
        if (!Directory.Exists(mods)) return set;
        void Probe(string dir, int depth)
        {
            string[] subs;
            try { subs = Directory.GetDirectories(dir); } catch { return; }
            foreach (var d in subs)
            {
                var leaf = Path.GetFileName(d);
                if (leaf.Equals(".junigrid_trash", StringComparison.OrdinalIgnoreCase)) continue;
                var bare = leaf.TrimStart('.');
                var marker = false;
                try
                {
                    marker = File.Exists(Path.Combine(d, "manifest.json"))
                             || File.Exists(Path.Combine(d, ".junigrid.json"));
                }
                catch { }
                if (marker)
                {
                    set.Add(bare);
                    try
                    {
                        var nm = Newtonsoft.Json.Linq.JObject.Parse(
                            File.ReadAllText(Path.Combine(d, "manifest.json")))["Name"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(nm)) set.Add(nm!);
                    }
                    catch { }
                }
                if (depth < 3) Probe(d, depth + 1);
            }
        }
        Probe(mods, 1);
        return set;
    }

    /// <summary>这张原版图有没有语言变体（<c>Cursors.zh-CN.xnb</c> 那种）。有 ⇒ 只换基名那张，
    /// 中文客户端可能仍然读到变体，效果看不见。我们【不】替作者去盖变体：变体存在的唯一理由
    /// 就是那张图上有本地化文字，盖掉会把中文界面拆了。所以把它作为提醒原样报出来。</summary>
    public static List<string> LocaleSiblings(string gamePath, string asset)
    {
        var list = new List<string>();
        try
        {
            var full = AssetFullPath(gamePath, asset);
            var dir = Path.GetDirectoryName(full);
            var stem = Path.GetFileNameWithoutExtension(full);
            if (dir is null || !Directory.Exists(dir)) return list;
            foreach (var f in Directory.EnumerateFiles(dir, stem + ".*.xnb"))
            {
                var m = LocaleTail.Match(Path.GetFileNameWithoutExtension(f));
                if (m.Success && !list.Contains(m.Value[1..])) list.Add(m.Value[1..]);
            }
        }
        catch (Exception ex) { AppLog.Warn("ContentOverride", ex.Message); }
        return list;
    }

    // ───────────────────── 界面上一行 = 一张图 ─────────────────────

    /// <summary>资产名末尾的语言后缀（<c>Data/Boots.zh-CN</c> 的那截）。星露谷把同一份资产按语言
    /// 各存一个文件，游戏按当前语言优先读带后缀那份 —— 所以一个包要处处生效就得把 11 种语言
    /// 全带上（实测 Nexus 1083 那包 1554 个文件里 1217 个就是这么来的）。
    /// <para>这个式子原先在 <see cref="LocaleSiblings"/> 与 <see cref="BuildStemIndex"/> 里各写一遍，
    /// 收成一处：界面并行、候选表排除变体、写盘展开副本必须是【同一个】"什么算变体"的答案。</para></summary>
    private static readonly Regex LocaleTail = new(@"\.[a-z]{2}-[A-Z]{2}$", RegexOptions.Compiled);

    /// <summary>去掉语言后缀 = 这张图本来的名字；不带后缀的原样返回。</summary>
    public static string LocaleBase(string asset) => LocaleTail.Replace(asset, "");

    /// <summary>把包内的账收成界面行：先剔掉不是图的（对话表 / 事件脚本 / 日程 / 字符串表 ——
    /// 判据就用安装时那道 <see cref="IsSwappable"/>，不另造一个"看起来像图"的说法），
    /// 再把同一张图的语言副本并成一条。基名那条不在包里时由某个副本代表，
    /// 一条图都不会凭空从列表里消失。</summary>
    public static List<ContentOverrideRecord> ImageRows(string gamePath,
        IEnumerable<ContentOverrideRecord> items)
    {
        var picked = new List<ContentOverrideRecord>();
        foreach (var g in items.Where(r => IsSwappable(gamePath, r.Asset))
                     .GroupBy(r => LocaleBase(r.Asset), StringComparer.OrdinalIgnoreCase))
        {
            // 代表优先挑【开着】的那一条（基名在前）：基名被单独关掉、语言副本还盖着的时候，
            // 拿基名当代表会让「按对象」那张卡报成"没应用"，而游戏里早就换图了。
            picked.Add(g.FirstOrDefault(r => r.Enabled
                         && r.Asset.Equals(g.Key, StringComparison.OrdinalIgnoreCase))
                       ?? g.FirstOrDefault(r => r.Enabled)
                       ?? g.FirstOrDefault(r => r.Asset.Equals(g.Key, StringComparison.OrdinalIgnoreCase))
                       ?? g.First());
        }
        return picked.OrderBy(r => r.Asset, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>这个包里与 <paramref name="asset"/> 属于【同一张图】的全部资产名：基名本身
    /// 加它在账上的所有语言副本。界面并成一行，写盘就必须一起写 —— 只盖基名那张的话，
    /// 中文客户端读到的还是另一份，症状是"这个包关掉了却还有半包没还原"。</summary>
    public static List<string> SlotSiblings(ConfigService cfg, string slot, string asset)
    {
        var baseName = LocaleBase(asset);
        return cfg.Current.ContentOverrides.Values
            .Where(r => r.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase)
                        && LocaleBase(r.Asset).Equals(baseName, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Asset)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>给「按对象」视图用的 <c>take</c> 谓词：这条账在界面上是不是【一行】。
    /// 一次算好代表集合再查表，不在谓词里重扫账本 —— 谓词会被每个视图调用好几遍。</summary>
    public static Func<ContentOverrideRecord, bool> ShownRowPredicate(string gamePath, ConfigService cfg)
    {
        var reps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in cfg.Current.ContentOverrides.Values
                     .GroupBy(r => r.Slot, StringComparer.OrdinalIgnoreCase))
            foreach (var r in ImageRows(gamePath, g))
                reps.Add(KeyOf(r.Asset, r.Slot));
        return r => reps.Contains(KeyOf(r.Asset, r.Slot));
    }

    /// <summary>界面上的一行 = 一张图的全部文件（基名 + 它在这个包里的所有语言副本），
    /// 所以这一行的状态必须按【整组】算。
    /// <para>为什么不能只看基名那一条：实测 1083 那包的 <c>LooseSprites/Cursors</c> 基名被单独
    /// 关掉过，而 11 份语言副本还盖在游戏本体上 —— 游戏里已经是 fur 鼠标，只看基名却会报成
    /// "这一行没应用"。并成一行之后，行说的得是这 12 个文件的共同事实。</para>
    /// <para><see cref="StateOf"/> 对关着的行返回 <see cref="OverrideState.Off"/>，
    /// 所以外来字节 / 已还原只会从【开着】的那些文件里报出来。</para></summary>
    public static OverrideState ImageGroupState(string gamePath, ConfigService cfg,
        string slot, string asset)
    {
        var st = OverrideState.Off;
        foreach (var f in SlotSiblings(cfg, slot, asset))
        {
            if (!cfg.Current.ContentOverrides.TryGetValue(KeyOf(f, slot), out var r)) continue;
            var one = StateOf(gamePath, r);
            if (one is OverrideState.Foreign or OverrideState.Gone) return one;   // 失效最要紧，先报
            if (one == OverrideState.InPlace) st = OverrideState.InPlace;
            else if (one == OverrideState.AlreadyVanilla && st != OverrideState.InPlace) st = one;
        }
        return st;
    }
}
