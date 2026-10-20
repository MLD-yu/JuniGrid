using System.IO;

namespace JuniGrid.Services;

public static partial class ContentOverrideService
{
    /// <summary>重判一个已装的包的结果。</summary>
    /// <param name="NewlyInstalled">这次【新装上】的资产数（原先被判不适配，现在能装了）。</param>
    /// <param name="StillRejected">仍然不适配的条数 —— 这些是真不适配，账上照旧留着。</param>
    /// <param name="Repaired">只改了账（分类从"不适配"改成"尺寸不同"），没动盘上的字节。</param>
    public sealed record RejudgeResult(int NewlyInstalled, int StillRejected, int Repaired, string? Error);

    /// <summary>
    /// 用<b>当前的</b>判定逻辑重判一个已装的包，并把能装上的补进去。
    ///
    /// 为什么需要它：界面上那些"不适配"是<b>安装当时</b>算好存在
    /// <see cref="ContentOverrideRecord.SkippedNote"/> 里的历史快照 ——
    /// 判定逻辑改对了，那些字不会自己变。实测用户账上仍挂着 25 条 2026-10-05 之前的误报
    /// （3 个字体 + 4 张尺寸不同 + 6 个 _Beach/_Winter 路由错的），不清掉就一直误导人。
    ///
    /// 走原包备份（<c>asset-packs/&lt;packDir&gt;</c>）重跑 <see cref="Plan"/>，不必重新下载；
    /// 备份不在了就只能如实说"没法重判"，绝不猜。
    ///
    /// <para><paramref name="rewriteForCurrentVersion"/> 是给「换了游戏版本再打开开关」用的：
    /// 默认那条路【不动】账上已有的资产（只补新能装上的），而版本一切换，账上那些行恰恰就是
    /// 要按当前本体<b>重编</b>的 —— 它们记的是上一个版本编出来的字节。开了这个参数才会把
    /// 每一条都重新编码、重新写盘、并把 <see cref="ContentOverrideRecord.Version"/> 换成当前版本。</para>
    ///
    /// <para>纪律与 <see cref="Commit"/> 一致：<b>先写盘、后动账</b>。
    /// 写盘失败 ⇒ 老账原样留着（宁可继续显示旧的误报，也不能显示"已修复"而盘上没动）。</para>
    /// </summary>
    public static RejudgeResult Rejudge(ConfigService cfg, string gamePath, string slot,
        bool rewriteForCurrentVersion = false, HashSet<string>? onlyAssets = null)
    {
        var rows = cfg.Current.ContentOverrides.Values
            .Where(r => r.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase)).ToList();
        if (rows.Count == 0)
            return new RejudgeResult(0, 0, 0, LocService.Tr("该包没有覆盖记录，无法重新检查"));

        var version = OriginalVersionSegment(gamePath);
        // 重编要先抄"当前版本的原版"当底，所以【这次要重算的那几张】里只要有一张盘上还盖着我们
        // 上次贴的那份，抄到的就是自己的图 —— 之后还原会把我们的图当原版退回去。
        // 判据必须逐张看盘，不能问"这个包里有没有别的行开着"：实机 2026-10-09 的包
        // 「No More Bowlegs」6 张里 Abigail 一张开着、另外 5 张盘上早就是原版，旧写法拿那一张
        // 把 5 张全锁死，而且它对那 5 张说的理由（"仍处于应用状态"）是假的。
        // 只在重编这条路算（要哈希文件）；进页那种普通重判不进这里。
        if (rewriteForCurrentVersion)
        {
            var stillOurs = rows
                .Where(r => onlyAssets is null || onlyAssets.Contains(r.Asset, StringComparer.OrdinalIgnoreCase))
                .Where(r => DiskState(gamePath, r) == OverrideState.InPlace)
                .Select(r => r.Asset).ToList();
            if (stillOurs.Count > 0)
                return new RejudgeResult(0, 0, 0, stillOurs.Count <= 3
                    ? LocService.Tf("这几张还盖着我们上次贴进去的图（{0}），先把它们关掉再开。",
                        string.Join("、", stillOurs))
                    : LocService.Tf("有 {0} 张图还盖着我们上次贴进去的那份（{1} 等），先把它们关掉再开。",
                        stillOurs.Count, string.Join("、", stillOurs.Take(3))));
        }

        var any = rows[0];
        var packDir = any.PackDir;
        if (string.IsNullOrEmpty(packDir))
            return new RejudgeResult(0, 0, 0, LocService.Tr("该包未保留原包备份，无法按当前本体重新解析 —— 请重新安装"));
        var srcDir = PackOriginalDir(gamePath, packDir);

        // 账上记的目录名可能已经和盘上对不上了 —— 实测 2026-10-05：账里写着
        // "Starry Sky Interface…-2"，而 asset-packs 下只有不带后缀那一份
        // （PreserveOriginalPack 撞名时会加 -2，之后目录又被清理/重命名过）。
        // 这种情况不该把重判堵死：按包名去掉 -N 后缀再找一次，找到了就用它。
        if (!Directory.Exists(srcDir))
        {
            var trimmed = TrimDupSuffix(packDir);
            if (trimmed is not null)
            {
                var alt = PackOriginalDir(gamePath, trimmed);
                if (Directory.Exists(alt)) { srcDir = alt; packDir = trimmed; }
            }
        }
        if (!Directory.Exists(srcDir))
            return new RejudgeResult(0, 0, 0, LocService.Tr("原包备份已不在磁盘上，无法按当前本体重新解析 —— 请重新安装"));

        // 账上已经生效的那些资产 —— 重判要拿它们跟新计划对照，不能自己跟自己比。
        var onDisk = rows.Select(r => r.Asset).ToHashSet(StringComparer.OrdinalIgnoreCase);
        PlanResult plan;
        try { plan = Prepare(gamePath, srcDir); }
        catch (Exception ex)
        {
            AppLog.Warn("ContentOverride", "[重判] 解析失败：" + ex.Message);
            return new RejudgeResult(0, 0, 0, LocService.Tr("重新检查时解析原包失败：") + ex.Message);
        }
        if (plan.Items.Count == 0)
            return new RejudgeResult(0, plan.Rejects.Count, 0, LocService.Tr("原包当前没有任何文件可以安装，已保持原样"));

        // 玩家换过配色的那些行：重新解析给的默认版永远是【排序第一版】，照它写下去就会把人
        // 选的黑色悄悄换回三色。所以先按账上的 VariantLabel 把这一版从候选里挑回来。
        for (var i = 0; i < plan.Items.Count; i++)
        {
            var it = plan.Items[i];
            if (!plan.VariantItems.TryGetValue(it.Asset, out var cands) || cands.Count < 2) continue;
            if (!cfg.Current.ContentOverrides.TryGetValue(KeyOf(it.Asset, slot), out var old)) continue;
            if (old.VariantLabel.Length == 0) continue;
            var chosen = cands.FirstOrDefault(c => string.Equals(c.Label, old.VariantLabel, StringComparison.OrdinalIgnoreCase));
            if (chosen is not null) plan.Items[i] = chosen;
        }

        // ① 先存原版 —— 与 Commit 同理：我们即将动 Content，得先留退路。
        var fresh = new List<string>();
        try
        {
            foreach (var it in plan.Items)
            {
                var alreadyStored = File.Exists(OriginalPath(gamePath, it.Asset));
                if (!File.Exists(AssetFullPath(gamePath, it.Asset)) || alreadyStored) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(OriginalPath(gamePath, it.Asset))!);
                File.Copy(AssetFullPath(gamePath, it.Asset), OriginalPath(gamePath, it.Asset), overwrite: true);
                fresh.Add(it.Asset);
            }
        }
        catch (Exception ex)
        {
            foreach (var a in fresh) { try { File.Delete(OriginalPath(gamePath, a)); } catch { } }
            AppLog.Warn("ContentOverride", "[重判] 存不下原版：" + ex.Message);
            return new RejudgeResult(0, 0, 0, LocService.Tr("无法保存原版文件，操作已中止，游戏本体未作任何修改：") + ex.Message);
        }

        // ② 写 Content。默认只写"账上还没有的" —— 已经在位的那些不重复写，
        //    免得把用户手动关掉又打开的顺序搅乱（那属于 SetEnabled 的地盘）。
        //    重编那条路相反：账上那些行【就是】要按当前本体重新编一遍的。
        var newly = rewriteForCurrentVersion
            ? (onlyAssets is null ? plan.Items : plan.Items.Where(it => onlyAssets.Contains(it.Asset)).ToList())
            : plan.Items.Where(it => !onDisk.Contains(it.Asset)).ToList();
        var written = new List<string>();
        try
        {
            foreach (var it in newly)
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
            AppLog.Warn("ContentOverride", "[重判] 写入中断，已退回原版：" + ex.Message);
            return new RejudgeResult(0, 0, 0, LocService.Tr("写入中断，已全部还原为原版：") + ex.Message);
        }

        // ③ 盘上对了，才动账。
        var repaired = 0;
        foreach (var it in newly)
        {
            var key = KeyOf(it.Asset, slot);
            foreach (var other in cfg.Current.ContentOverrides.Values.Where(o =>
                         o.Enabled && o.Asset.Equals(it.Asset, StringComparison.OrdinalIgnoreCase)
                         && !KeyOf(o.Asset, o.Slot).Equals(key, StringComparison.OrdinalIgnoreCase)).ToList())
                other.Enabled = false;
            var storeId = StoreCopy(gamePath, it.Bytes, it.Asset);
            // 已有行就地改，不 new 一个顶掉：顶掉会把 PackCover / CategoryName 这些
            // 界面懒补回来的字段擦成空，界面上就变成"封面没了 / 包掉回未分类"。
            if (!cfg.Current.ContentOverrides.TryGetValue(key, out var rec))
            {
                rec = new ContentOverrideRecord { Asset = it.Asset, Slot = slot };
                cfg.Current.ContentOverrides[key] = rec;
            }
            else if (rec.StoreId.Length > 0 && rec.StoreId != storeId)
                DropStore(gamePath, rec.StoreId);
            rec.PackName = any.PackName;
            rec.NexusModId = any.NexusModId;
            rec.OriginalSha256 = File.Exists(OriginalPath(gamePath, it.Asset))
                ? Sha256(OriginalPath(gamePath, it.Asset)) : "";
            rec.AppliedSha256 = Sha256Of(it.Bytes);
            rec.StoreId = storeId;
            rec.Enabled = true;
            rec.Encoded = it.Encoded;
            rec.PackTitle = any.PackTitle;
            rec.PackDir = packDir;
            rec.Version = version;
            rec.AppliedAtUtc = DateTime.UtcNow;
            rec.SkippedCount = plan.Rejects.Count;
            rec.SkippedNote = string.Join("\n", plan.Rejects.Take(8));
            rec.ResizedCount = plan.Resized.Count;
            rec.ResizedNote = string.Join("\n", plan.Resized.Take(8));
            // 配色清单跟着这次解析的结果走：原包被人改动过时，界面上不该还列着已经没了的那版。
            rec.Variants = plan.VariantLabels.TryGetValue(it.Asset, out var vl) ? vl : new();
            rec.VariantLabel = it.Label;
            rec.VariantFacts = plan.VariantItems.TryGetValue(it.Asset, out var vi2)
                ? vi2.Select(v => new ContentOverrideVariant(v.Label, v.Note)).ToList() : new();
            repaired++;
        }

        // ④ 已经在位的那些行：盘上的字节没动，但账上的"不适配/尺寸不同"分类要跟着新判定更新。
        //    不更新的话：同一个包里有的行写着"不适配"、有的写着"已运用"，界面上自相矛盾。
        var newSkip = string.Join("\n", plan.Rejects.Take(8));
        var newResized = string.Join("\n", plan.Resized.Take(8));
        var dirChanged = false;
        foreach (var r in rows)
        {
            // 上面按原名回退找到了实际目录 ⇒ 把纠正后的名字写回账。
            // 不写回的话下次重判还会撞同一个错（账指着一个不存在的目录）。
            if (!r.PackDir.Equals(packDir, StringComparison.OrdinalIgnoreCase)) { r.PackDir = packDir; dirChanged = true; }
            // 版本清单也照这次的解析结果刷新：它是【安装当时】的快照，判据一变（比如 2026-10-10
            // 起 PNG 不再算候选）账上就会继续列着包里已经没有的那几版 —— 界面上那些"角色预览 / xnb"
            // 配色 chip 就是这么留下的，点它还会去找一个不存在的候选。
            var newVariants = plan.VariantLabels.TryGetValue(r.Asset, out var vl)
                ? vl : new List<string>();
            var newFacts = plan.VariantItems.TryGetValue(r.Asset, out var vi)
                ? vi.Select(v => new ContentOverrideVariant(v.Label, v.Note)).ToList()
                : new List<ContentOverrideVariant>();
            if (r.SkippedNote == newSkip && r.ResizedNote == newResized
                && r.SkippedCount == plan.Rejects.Count && r.ResizedCount == plan.Resized.Count
                && r.Variants.SequenceEqual(newVariants)) continue;
            r.SkippedCount = plan.Rejects.Count;
            r.SkippedNote = newSkip;
            r.ResizedCount = plan.Resized.Count;
            r.ResizedNote = newResized;
            r.Variants = newVariants;
            r.VariantFacts = newFacts;
            // VariantLabel 故意不动：它记的是"现在贴着的那一份"是哪一版。
            // 那份候选可能已经不在清单里（效果图就是这种）—— 这时候一颗 chip 都不高亮才是实话，
            // 而玩家点清单里剩下的那一版，正好把它换成还在的。
        }

        cfg.Save(cfg.Current);
        AppLog.Warn("ContentOverride",
            $"[重判] {any.PackName}：新装上 {repaired} 张，分类已刷新"
            + (dirChanged ? $"，原包目录名已纠正为 {packDir}" : "")
            + $"，仍不适配 {plan.Rejects.Count} 个");
        return new RejudgeResult(repaired, plan.Rejects.Count, rows.Count, null);
    }
}
