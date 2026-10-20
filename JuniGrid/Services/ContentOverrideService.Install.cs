using System.IO;
using System.Security.Cryptography;

namespace JuniGrid.Services;

public static partial class ContentOverrideService
{
    public static List<ContentOverrideRecord> Records(ConfigService cfg)
        => cfg.Current.ContentOverrides.Values
            .OrderBy(r => r.PackName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Asset, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>界面用的一行 = 一个包（一个包可能盖了几十张图，但开关是整包的）。
    /// 分组只看 Slot，不看 PackName —— 两个不同的包重名是常事，重名会把两包并成一行，
    /// 那一行的开关就变成"同时操作两个没关系的包"。
    /// <paramref name="VanillaCount"/> = 账上开着、盘上却已经是原版的那几张（被 Steam 验证
    /// 或游戏更新还原掉了），与 <paramref name="DriftedCount"/>（外来字节顶掉/文件没了）分两报。</summary>
    public sealed record PackGroup(string Slot, string PackName, int? NexusModId,
        List<ContentOverrideRecord> Items, bool AnyOn, int DriftedCount, int LocaleRiskCount,
        int VanillaCount, List<ContentOverrideRecord> ImageItems)
    {
        /// <summary>显示名与封面取自记录（由界面从 N 网补齐后写回），没补之前就是包目录名那种丑名字。</summary>
        public string PackTitle => Items.Count > 0 ? Items[0].PackTitle : "";
        public string PackCover => Items.Count > 0 ? Items[0].PackCover : "";
        /// <summary>这个包没装上的文件数与头几个原因（安装当时算好留在账上，见 ContentOverrideRecord）。</summary>
        public int Skipped => Items.Count > 0 ? Items[0].SkippedCount : 0;
        public string SkippedNote => Items.Count > 0 ? Items[0].SkippedNote : "";
        /// <summary>装上了但尺寸与本体不同的条目（不是失败，只是表情帧数与原版不同）。</summary>
        public int Resized => Items.Count > 0 ? Items[0].ResizedCount : 0;
        public string ResizedNote => Items.Count > 0 ? Items[0].ResizedNote : "";
        /// <summary>N 网分类名（素材包页按它分组）；空 = 还没补到或这包没有页号。</summary>
        public string Category => Items.Count > 0 ? Items[0].CategoryName : "";
        /// <summary>这个包是按哪个版本编的（界面按它分列）。一个包里出现混合版本（部分行还没按
        /// 当前本体重编）时留空 ⇒ 归进「版本待确认」，不替它挑一个。</summary>
        public string StampedVersion { get; } = Items.Select(r => r.Version)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
            ? Items.Select(r => r.Version).FirstOrDefault() ?? "" : "";
    }

    /// <summary>按包汇总账本。<see cref="PackGroup.Items"/> 是这个包的<b>全部账</b>，
    /// <c>ImageItems</c> 是界面上<b>列得出来的行</c>（只留图像，同一张图的语言副本并成一条）。
    /// <para>为什么必须分成两份：整包开关与删除都按 slot 重读账本来覆盖全部文件
    ///（<see cref="SetPackEnabled"/> / <see cref="DeletePack"/>）。把文本表和语言副本从界面上
    /// 藏起来，绝不能顺手把它们也从"关掉这个包"的动作里拿掉 —— 那就变成界面没这个包、
    /// 本体里还躺着一堆没人认领的覆盖。</para></summary>
    public static List<PackGroup> Groups(string gamePath, ConfigService cfg)
        => cfg.Current.ContentOverrides.Values
            .GroupBy(r => r.Slot, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var items = g.OrderBy(r => r.Asset, StringComparer.OrdinalIgnoreCase).ToList();
                return new PackGroup(
                    g.Key,
                    g.First().PackName,
                    g.First().NexusModId,
                    items,
                    g.Any(r => r.Enabled),
                    g.Count(r => Drifted(gamePath, r)),
                    g.Count(r => LocaleSiblings(gamePath, r.Asset).Count > 0),
                    g.Count(r => StateOf(gamePath, r) == OverrideState.AlreadyVanilla),
                    ImageRows(gamePath, items));
            })
            .OrderBy(x => x.PackName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>整包开或关。关 = 这个包盖过的每张图都退回原版；开 = 每张图重新盖回（含修复漂移）。
    /// <para>账上记的版本不是当前本体那一版时，打开走的是【重编】而不是回放：
    /// 副本里那份 xnb 是按上一个版本的行距/帧数编的，直接盖进新版本本体不会报错，
    /// 只会画错 —— 所以先拿原包备份对当前本体重跑一遍判定（一次解析覆盖整包，
    /// 逐行重编会把同一个包解 101 遍），重编完的行由那条路直接写盘并置为启用。</para></summary>
    public static string? SetPackEnabled(ConfigService cfg, string gamePath, string slot, bool enabled)
    {
        var mine = cfg.Current.ContentOverrides.Values
            .Where(r => r.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Asset, StringComparer.OrdinalIgnoreCase).ToList();
        var stale = enabled ? StaleRows(gamePath, mine) : new List<ContentOverrideRecord>();
        string? err = null;
        if (stale.Count > 0)
            err = ForVersion(RebuildForCurrentVersion(cfg, gamePath, slot,
                stale.Select(r => r.Asset).ToHashSet(StringComparer.OrdinalIgnoreCase))) ?? err;
        foreach (var r in mine)
        {
            if (stale.Contains(r)) continue;          // 重编那条路已经把它写完并置为启用了
            if (enabled || r.Enabled)                 // 关的时候别去动本来就关着的行（见 SetEnabled 那条理由）
            {
                var one = SetEnabled(cfg, gamePath, KeyOf(r.Asset, r.Slot), enabled);
                if (one is not null) err = one;
            }
        }
        return err;
    }

    /// <summary>账上版本与当前本体不一致的那些行。版本空白（老账）算一致 —— 猜不出属于哪版就不要为它重编。</summary>
    private static List<ContentOverrideRecord> StaleRows(string gamePath, List<ContentOverrideRecord> rows)
    {
        var current = OriginalVersionSegment(gamePath);
        return rows.Where(r => r.Version.Length > 0
                               && !r.Version.Equals(current, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>把 <paramref name="assets"/> 这些行按<b>当前</b>本体重编并盖上，返回要报给用户的话（null = 全部成功）。
    /// 原包备份没了 / 当前版本一张都判不上 / 那几张还盖着我们的图，都是拦得下来的诚实失败：
    /// 绝不退回"直接回放旧字节"，那是把上一个版本编出来的表画进当前本体。
    /// <para>这里<b>不加</b>"该包需按当前游戏版本重新编码"那句帽子 —— 这个函数也被【副本丢了】
    /// 那条路调用（见 <see cref="SetEnabled"/>），那条跟版本一点关系都没有，套上这句就是撒谎。
    /// 只有真是版本变了的调用方才配说这话。</para></summary>
    private static string? RebuildForCurrentVersion(ConfigService cfg, string gamePath, string slot,
        HashSet<string> assets)
    {
        var rj = Rejudge(cfg, gamePath, slot, true, assets);
        if (rj.Error is not null) return rj.Error;
        var left = cfg.Current.ContentOverrides.Values
            .Count(r => r.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase)
                        && assets.Contains(r.Asset) && !r.Enabled);
        if (left > 0)
            return LocService.Tf("当前版本有 {0} 个文件无法安装，已保持原版", left);
        return null;
    }

    /// <summary>"按当前版本重编"这句帽子只有【真是版本变了】的调用方才配戴。</summary>
    private static string? ForVersion(string? err)
        => err is null ? null : LocService.Tf("该包需按当前游戏版本重新编码，但{0}", err);

    // ───────────────────── 安装 ─────────────────────

    /// <summary>只解析、不写盘：包里每个文件落到哪张原版图、要写成什么字节。
    /// 「这包是不是给游戏本体的」这个判断与下面真正落盘用的是【同一份】结果 ——
    /// 另起一个分类器迟早会和执行分叉，到那时候要么该装的没装，要么装了的不该装。</summary>
    public static PlanResult Prepare(string gamePath, string srcDir)
        => Directory.Exists(ContentRoot(gamePath)) ? Plan(gamePath, srcDir) : new PlanResult();

    /// <summary>把 Prepare 的结果落盘。返回 null = 成功；返回文案 = 一个字节都没写。
    /// <paramref name="srcDir"/> = 解包出来的原包目录：装完原样备份到游戏目录的 asset-packs 下
    /// （打开文件夹看到的就是当初下载的那个 mod），传 null 则跳过备份。</summary>
    public static string? Commit(string gamePath, PlanResult plan, string packName, int? nexusModId,
        ConfigService cfg, out string? modName, string? srcDir = null)
    {
        modName = packName;
        var slot = SlotOf(packName, nexusModId);
        // 这一趟写盘属于哪一个版本 —— 原版层与记录上的版本戳必须出自同一次解析，
        // 不然中途版本变了就会出现「账说自己在 1.6.15，原版却存进了 1.6.16 那层」。
        var version = OriginalVersionSegment(gamePath);
        MigrateLegacyStore(gamePath);
        if (!Directory.Exists(ContentRoot(gamePath)))
            return LocService.Tr("游戏目录中没有 Content 文件夹，请先确认游戏本体已安装");

        if (plan.Items.Count == 0)
            return plan.Rejects.Count > 0
                ? LocService.Tr("这些文件没有一个能与游戏本体的图像对应：") + string.Join("；", plan.Rejects.Take(6))
                : LocService.Tr("包内未找到 .xnb 或 .png 图像文件");

        // ① 先把原版全部收进仓库。这一步失败游戏本体一个字节没动。
        var freshlyStored = new List<string>();
        try
        {
            foreach (var asset in plan.Assets)
            {
                var dst = AssetFullPath(gamePath, asset);
                if (!File.Exists(dst) || File.Exists(OriginalPath(gamePath, asset))) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(OriginalPath(gamePath, asset))!);
                File.Copy(dst, OriginalPath(gamePath, asset), overwrite: true);
                freshlyStored.Add(asset);
            }
        }
        catch (Exception ex)
        {
            foreach (var a in freshlyStored) { try { File.Delete(OriginalPath(gamePath, a)); } catch { } }
            AppLog.Warn("ContentOverride", "[纯净版] 存不下原版：" + ex.Message);
            return LocService.Tr("无法保存原版文件，操作已中止，游戏本体未作任何修改：") + ex.Message;
        }

        // ② 写 Content。中途失败就整单退回原版 —— 半截覆盖玩家看到的是"立绘一半新一半旧"。
        var written = new List<string>();
        try
        {
            foreach (var it in plan.Items)
            {
                var dst = AssetFullPath(gamePath, it.Asset);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.WriteAllBytes(dst, it.Bytes);
                written.Add(it.Asset);
            }
        }
        catch (Exception ex)
        {
            foreach (var a in written)
            {
                try
                {
                    if (File.Exists(OriginalPath(gamePath, a)))
                        File.Copy(OriginalPath(gamePath, a), AssetFullPath(gamePath, a), overwrite: true);
                    else File.Delete(AssetFullPath(gamePath, a));
                }
                catch (Exception backEx) { AppLog.Warn("ContentOverride", backEx.Message); }
            }
            AppLog.Warn("ContentOverride", "[纯净版] 写入中断，已退回原版：" + ex.Message);
            return LocService.Tr("写入中断，已全部还原为原版：") + ex.Message;
        }

        // ②.4 从盘上认回来的账，slot 只能用包名（那时没有页号）。他现在重新下载一次
        //      ⇒ 这次带页号，slot 变成 "N<页号>"。旧账与旧备份目录必须一起让位，否则：
        //      界面上同一个包出现两张卡（一张开一张关），磁盘上多出一份「包名-2」的备份目录
        //      （PreserveOriginalPack 见同名目录的主人不是这个 slot，就会往后缀上躲）。
        var nameSlot = Sanitize(packName);
        if (!slot.Equals(nameSlot, StringComparison.OrdinalIgnoreCase)
            && cfg.Current.ContentOverrides.Values.Any(v =>
                v.Slot.Equals(nameSlot, StringComparison.OrdinalIgnoreCase)
                && v.PackName.Equals(packName, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var kv in cfg.Current.ContentOverrides
                         .Where(kv => kv.Value.Slot.Equals(nameSlot, StringComparison.OrdinalIgnoreCase))
                         .Select(kv => kv.Key).ToList())
                cfg.Current.ContentOverrides.Remove(kv);
            DropPackBackup(gamePath, cfg, nameSlot, nameSlot);
            AppLog.Warn("ContentOverride",
                $"[重新绑定页号] 「{packName}」的无页号旧账已让位（slot {nameSlot} → {slot}）");
        }

        // ②.5 原包备份：盘上已经对了，再把解包目录原样收进 asset-packs。
        // 放在动账之前 —— 账上的 PackDir 要指着它；备份失败（""）不挡安装。
        var packDir = srcDir is not null && Directory.Exists(srcDir)
            ? PreserveOriginalPack(gamePath, srcDir, packName, slot, cfg)
            : "";

        // ③ 盘已经对了，才动账。账本只在这里被改，所以①②的失败路径不需要任何回滚账本的代码。
        foreach (var it in plan.Items)
        {
            var key = KeyOf(it.Asset, slot);
            // 同一个资产上原先在位的另一份：文件已经被我们盖掉了，账转成"关着"，
            // 副本留着，切回去就能重新生效（一个文件只装得下一张表）。
            foreach (var other in cfg.Current.ContentOverrides.Values.Where(o =>
                         o.Enabled && o.Asset.Equals(it.Asset, StringComparison.OrdinalIgnoreCase)
                         && !KeyOf(o.Asset, o.Slot).Equals(key, StringComparison.OrdinalIgnoreCase)).ToList())
                other.Enabled = false;
            var storeId = StoreCopy(gamePath, it.Bytes, it.Asset);
            if (cfg.Current.ContentOverrides.TryGetValue(key, out var prev) && prev.StoreId.Length > 0
                && prev.StoreId != storeId) DropStore(gamePath, prev.StoreId);
            cfg.Current.ContentOverrides[key] = new ContentOverrideRecord
            {
                Asset = it.Asset, Slot = slot, PackName = packName, NexusModId = nexusModId,
                OriginalSha256 = File.Exists(OriginalPath(gamePath, it.Asset)) ? Sha256(OriginalPath(gamePath, it.Asset)) : "",
                AppliedSha256 = Sha256Of(it.Bytes), StoreId = storeId, Enabled = true,
                Encoded = it.Encoded, PackTitle = packName, PackDir = packDir, Version = version,
                // 装成功 ≠ 全装上了。没装上的那几个数与名字要留在账上，界面上才说得出"为什么
                // 那几个角色没变"；只写进日志的话，等人来问的时候日志早就滚走了。
                SkippedCount = plan.Rejects.Count,
                SkippedNote = string.Join("\n", plan.Rejects.Take(8)),
                ResizedCount = plan.Resized.Count,
                ResizedNote = string.Join("\n", plan.Resized.Take(8)),
                // 同一张目标的多个配色：默认那版记在 VariantLabel，其余留在 Variants 里给人换。
                Variants = plan.VariantLabels.TryGetValue(it.Asset, out var vs) ? vs : new(),
                VariantLabel = it.Label,
                VariantFacts = plan.VariantItems.TryGetValue(it.Asset, out var vi)
                    ? vi.Select(v => new ContentOverrideVariant(v.Label, v.Note)).ToList() : new(),
            };
        }
        cfg.Save(cfg.Current);
        AppLog.Warn("ContentOverride",
            $"[纯净版] {packName}：写进 Content {plan.Items.Count} 张，丢弃 {plan.Rejects.Count} 个文件"
            + (plan.Resized.Count > 0 ? $"，其中 {plan.Resized.Count} 张尺寸与本体不同（已照装）" : ""));
        return null;
    }

    private static string StoreCopy(string gamePath, byte[] bytes, string asset)
    {
        var storeId = Guid.NewGuid().ToString("N")[..12];
        try
        {
            var keep = StoreFile(gamePath, storeId, asset);
            Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
            File.WriteAllBytes(keep, bytes);
            return storeId;
        }
        catch (Exception ex)
        {
            AppLog.Warn("ContentOverride", "[纯净版] 副本保存失败，之后将无法重新开启：" + ex.Message);
            return "";
        }
    }

    private static void DropStore(string gamePath, string storeId)
    {
        try
        {
            var dir = StoreDir(gamePath, storeId);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch (Exception ex) { AppLog.Warn("ContentOverride", ex.Message); }
    }

    private static string Sha256(string file)
    {
        try { return Sha256Of(File.ReadAllBytes(file)); }
        catch (Exception ex) { AppLog.Warn("ContentOverride", ex.Message); return ""; }
    }
    private static string Sha256Of(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
