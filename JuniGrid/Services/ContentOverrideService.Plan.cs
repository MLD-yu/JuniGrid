using System.IO;
using System.Text.RegularExpressions;

namespace JuniGrid.Services;

public static partial class ContentOverrideService
{
    /// <summary>
    /// 把一条记录换成同包里另一个配色（<see cref="ContentOverrideRecord.Variants"/> 里列的那些）。
    ///
    /// <para>为什么账上不存 N 份字节：一个文件只装得下一张表，"换配色"本质就是**重新盖一次**；
    /// 而候选图本来就在 <see cref="ContentOverrideRecord.PackDir"/> 那份原包备份里，
    /// 重新解析一遍既能按<b>当前</b>本体重编（与换版本重编同一套），也免得每个配色都留一份副本占盘。</para>
    ///
    /// <para>纪律与 <see cref="Commit"/>/<see cref="Rejudge"/> 一致：<b>先写盘、后动账</b>。
    /// 记录是关着的（没在位）⇒ 只挑好新那份、把副本与标签换上，等下次打开就是它，不碰 Content。</para>
    /// </summary>
    public static string? SwitchVariant(ConfigService cfg, string gamePath, string asset, string slot, string label)
    {
        var key = KeyOf(asset, slot);
        if (!cfg.Current.ContentOverrides.TryGetValue(key, out var r)) return LocService.Tr("未找到该记录");
        if (r.Variants.Count < 2) return null;                                  // 单版本包，没得换
        if (string.Equals(r.VariantLabel, label, StringComparison.Ordinal)) return null;   // 已经是这一版

        var srcDir = ResolvePackBackup(gamePath, r);
        if (srcDir is null)
            return string.IsNullOrEmpty(r.PackDir)
                ? LocService.Tr("该包未保留原包备份，换配色需要原包 —— 请重新安装一次")
                : LocService.Tr("原包备份已不在磁盘上，换配色需要原包 —— 请重新安装一次");

        PlanResult plan;
        try { plan = Prepare(gamePath, srcDir); }
        catch (Exception ex)
        {
            AppLog.Warn("ContentOverride", "[换配色] 解析原包失败：" + ex.Message);
            return LocService.Tr("换配色时解析原包失败：") + ex.Message;
        }
        var pick = plan.VariantItems.TryGetValue(asset, out var cands)
            ? cands.FirstOrDefault(c => string.Equals(c.Label, label, StringComparison.OrdinalIgnoreCase))
            : null;
        if (pick is null)
            return LocService.Tf("原包里已找不到配色「{0}」—— 这个包可能被改动过，重新安装一次再换", label);

        // 在位的那条要盖 Content：盘上现在这份必须是我们放的，别人（另一 mod / 手动改）动过就别盲写。
        if (r.Enabled && ForeignOnDisk(gamePath, r))
            return LocService.Tr("该图像当前既非本程序写入也非原版（可能被其他 mod 或手动修改），已停止换配色");

        var dst = AssetFullPath(gamePath, asset);
        if (r.Enabled)
        {
            try { File.WriteAllBytes(dst, pick.Bytes); }
            catch (Exception ex)
            {
                AppLog.Warn("ContentOverride", "[换配色] 写入失败：" + ex.Message);
                return LocService.Tr("写入中断，已全部还原为原版：") + ex.Message;
            }
        }
        var prevStore = r.StoreId;
        r.StoreId = StoreCopy(gamePath, pick.Bytes, asset);
        if (prevStore.Length > 0 && prevStore != r.StoreId) DropStore(gamePath, prevStore);
        r.AppliedSha256 = Sha256Of(pick.Bytes);
        r.Encoded = pick.Encoded;
        r.VariantLabel = pick.Label.Length > 0 ? pick.Label : label;
        if (plan.VariantItems.TryGetValue(asset, out var facts))
            r.VariantFacts = facts.Select(v => new ContentOverrideVariant(v.Label, v.Note)).ToList();
        r.Version = OriginalVersionSegment(gamePath);
        if (r.Enabled) r.AppliedAtUtc = DateTime.UtcNow;
        // 候选清单跟着这次解析的结果刷新（原包被人动过时，界面上不该还列着没了的那版）
        if (plan.VariantLabels.TryGetValue(asset, out var fresh)) r.Variants = fresh;
        cfg.Save(cfg.Current);
        AppLog.Warn("ContentOverride", $"[换配色] {asset} ← {r.VariantLabel}（{(r.Enabled ? "已写入本体" : "包当前关着，下次打开生效")}）");
        return null;
    }

    /// <summary>这个包的这张图，字节实际在哪儿：<b>派生副本 → 原包备份 → 没有</b>。
    /// <para>为什么必须两处都找：从盘上认回来的账（<see cref="RebuildFromDisk"/>）只给"真在位"的那些
    /// 编派生副本，判成"关着"的那几条 <c>StoreId</c> 是空的 —— 可它的图一直好好躺在
    /// <c>asset-packs/&lt;包&gt;/</c> 里。预览只认 <c>StoreId</c> 的话，就会把"盘上明明有"
    /// 报成"该副本文件已不在磁盘上"（实机 2026-10-09：No More Bowlegs 的 Characters/Abigail 就这样
    /// 被谎报了一次，而写入那条路 <see cref="ResolvePackBackup"/> 本来就会回退到原包备份）。</para></summary>
    public static string? PackImageSource(string gamePath, ContentOverrideRecord r)
    {
        if (r.StoreId.Length > 0)
        {
            var copy = StoreFile(gamePath, r.StoreId, r.Asset);
            if (File.Exists(copy)) return copy;
        }
        var dir = ResolvePackBackup(gamePath, r);
        if (dir is null) return null;
        var direct = Path.Combine(dir, r.Asset.Replace('/', Path.DirectorySeparatorChar) + ".xnb");
        if (File.Exists(direct) && BytesMatch(direct, r.AppliedSha256)) return direct;
        // 备份是平铺的（包里那张图就躺在根上，"它盖哪张原版图"是 Prepare 按内容认出来的）⇒ 按同名候选找，
        // 但每一个都要拿账上那份字节验一次：只认文件名的话，一个包里 Portraits/Abigail 与
        // Characters/Abigail 会互相拿错图 —— 拿错图比不画更糟。
        //（实机 2026-10-09：No More Bowlegs 的备份就是 6 个平铺文件，直接拼资产路径必然找不到。）
        var name = Path.GetFileName(r.Asset) + ".xnb";
        try
        {
            foreach (var cand in Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories))
                if (BytesMatch(cand, r.AppliedSha256)) return cand;
        }
        catch { return null; }
        return null;
    }

    /// <summary>字节对得上才算这张是它。账上没写期望值的老行（AppliedSha256 空）放行。</summary>
    private static bool BytesMatch(string file, string wantSha)
        => wantSha.Length == 0
           || string.Equals(Sha256(file), wantSha, StringComparison.OrdinalIgnoreCase);

    /// <summary>账上那份原包备份的实际目录；没了就按去掉 <c>-2</c> 后缀的名字回退找一次。
    /// 找不到返回 null（<see cref="Rejudge"/> 与 <see cref="SwitchVariant"/> 共用这套说法）。</summary>
    private static string? ResolvePackBackup(string gamePath, ContentOverrideRecord r)
    {
        if (string.IsNullOrEmpty(r.PackDir)) return null;
        var srcDir = PackOriginalDir(gamePath, r.PackDir);
        if (Directory.Exists(srcDir)) return srcDir;
        var trimmed = TrimDupSuffix(r.PackDir);
        if (trimmed is not null)
        {
            var alt = PackOriginalDir(gamePath, trimmed);
            if (Directory.Exists(alt)) return alt;
        }
        return null;
    }

    /// <summary>去掉目录名末尾的 <c>-2</c>、<c>-3</c> 这类撞名后缀；没有就返回 null。
    /// 用来在"账上记的目录没了"时按原名回退去找（见 <see cref="Rejudge"/>）。</summary>
    private static string? TrimDupSuffix(string dirName)
    {
        var i = dirName.LastIndexOf('-');
        if (i <= 0 || i == dirName.Length - 1) return null;
        return int.TryParse(dirName.AsSpan(i + 1), out _) && dirName.AsSpan(i + 1).Length <= 3
            ? dirName[..i]
            : null;
    }

    /// <summary>账上所有还挂着"不适配"的包（界面上给"重判并修复"用）。</summary>
    public static List<(string Key, string PackName, int Skipped)> SlotsNeedingRejudge(ConfigService cfg)
        => cfg.Current.ContentOverrides.Values
            .Where(r => r.SkippedCount > 0)
            .GroupBy(r => r.Slot, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Key: g.Key, PackName: g.First().PackName, Skipped: g.Max(r => r.SkippedCount)))
            .OrderByDescending(x => x.Skipped)
            .ToList();

    /// <param name="Label">同一张目标的多个配色里，这一版叫什么（取包内文件夹名）。
    /// 单版本包留空，界面因此不会凭空多一条配色栏。</param>
    /// <param name="Note">这一版自己的说明（尺寸与本体不同时那句）。界面上"按配色分行"要用它，
    /// 不能再从拼好的整串备注里倒推是哪个配色 —— 4 个配色的文件名全都叫 hairstyles.png。</param>
    public sealed record PlanItem(string Asset, byte[] Bytes, bool Encoded, string Source,
        string Label = "", string Note = "");

    /// <summary>包内这张图所在的"版本文件夹"名，剥掉作者标编码方式的前缀（<c>[xnb]三色-Tricolor</c> ⇒ <c>三色-Tricolor</c>）。
    /// 根目录下的文件没有文件夹 ⇒ 空串，调用方回落到文件名。</summary>
    internal static string VariantFolderOf(string rel)
    {
        var dir = Path.GetDirectoryName(rel)?.Replace('\\', '/') ?? "";
        var seg = dir.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        return Regex.Replace(seg, @"^[\[（(]\s*[xX][nN][bB]\s*[\]）)]\s*", "").Trim();
    }

    public sealed class PlanResult
    {
        public List<PlanItem> Items { get; } = new();
        /// <summary>被跳过的文件，带【包内路径】与【推出来的资产】——收尾要拿这两个数认"同包的源图"。
        /// 界面与日志读的是 <see cref="Rejects"/> 那份纯文案表。</summary>
        public List<(string Rel, string Asset, string Msg)> RejectRows { get; } = new();
        /// <summary>包内某个文件 → 它自己那句"尺寸与本体不同"的说明。分组配色时按这个取备注。</summary>
        public Dictionary<string, string> FileNotes { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>跳过原因（逐条人话）。同包源图那类已在 Plan 收尾被摘掉，不算"不可用"。</summary>
        public List<string> Rejects => RejectRows.Select(r => r.Msg).ToList();
        public void AddReject(string rel, string? asset, string msg) => RejectRows.Add((rel, asset ?? "", msg));
        /// <summary>装上了、但尺寸与本体那张不同的条目。游戏按格位切帧，格位不变就
        /// 不会画错人，所以这类一律放行 —— 只是提醒玩家"这角色的表情比原版少几帧"，
        /// 不能再混进 Rejects 里说成"不适配"，那会让整张表被扣上一枚不可用的章。</summary>
        public List<string> Resized { get; } = new();
        /// <summary>一个能用的都没有 ⇒ 调用方不该接管这个包（交回原来的路由）。</summary>
        public bool Installable => Items.Count > 0;
        /// <summary>有多少个文件【认出了本体里的某张原版图】。
        /// 与 Installable 分开计数是为了区分两种"没的装"：
        /// 一种是这包根本不是给本体的（0，交回原路），另一种是给本体但编不成
        ///（&gt;0，必须就地拒绝并点名，不许悄悄退回转换器）。
        /// 高度不同不再计入这一类 —— 它照装，只是进 <see cref="Resized"/>；
        /// 宽度不同仍然算这一类，因为那会横向串格，必须点名拒绝。</summary>
        public int TargetsSeen;
        /// <summary>撞到同一张目标的多个配色（含被选为默认的那一个），键 = 资产。
        /// 只有两版以上才有条目 ⇒ 界面拿它决定要不要出配色栏。</summary>
        public Dictionary<string, List<string>> VariantLabels { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>上面那些标签对应的候选项本体（含默认版）。换配色就是从这张表里取另一版重盖一次，
        /// 所以候选的字节必须留着 —— 只留默认那版的话，"换配色"就没东西可换了。</summary>
        public Dictionary<string, List<PlanItem>> VariantItems { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Assets => Items.Select(i => i.Asset).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>逐文件推目标并算好要写的字节。整个阶段不碰游戏目录。</summary>
    private static PlanResult Plan(string gamePath, string srcDir)
    {
        var res = new PlanResult();
        var stemIndex = BuildStemIndex(ContentRoot(gamePath), out var variantBases);
        // 已装 mod 在 content.json 里声明的资产名：整趟只取一次。逐文件取 = 每个候选重扫一遍
        // Mods（实测 0.18 秒/次），256 张图的包光这一步就是几分钟。
        var modDeclared = ModService.DeclaredAssetNames(gamePath);
        foreach (var f in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(f);
            var isXnb = ext.Equals(".xnb", StringComparison.OrdinalIgnoreCase);
            if (!isXnb && !ext.Equals(".png", StringComparison.OrdinalIgnoreCase)) continue;
            var rel = Path.GetRelativePath(srcDir, f).Replace('\\', '/');
            var name = Path.GetFileName(rel);
            var asset = ResolveTarget(gamePath, rel, f, stemIndex, variantBases, modDeclared, out var why, out var sawVanillaName);
            if (sawVanillaName) res.TargetsSeen++;     // 认得出是本体某张原版图，哪怕这张装不成
            if (asset is null) { res.AddReject(rel, null, why!); continue; }
            var targetPath = AssetFullPath(gamePath, asset);
            if (isXnb)
            {
                byte[] bytes;
                try { bytes = File.ReadAllBytes(f); }
                catch (Exception ex) { AppLog.Warn("ContentOverride", ex.Message); continue; }
                var bad = XnbCodec.Validate(bytes);
                if (bad is not null) { res.AddReject(rel, asset, name + "：" + bad); continue; }

                // 目标本体件解不出纹理【有两种完全不同的原因】，必须分开判，不能一律当字体：
                //  · 字体（SpriteFont）：自描述容器，字号与行距都记在表里，整份替换成立
                //    （CP 的 EditImage 对字体也是整份替换）⇒ 收，且不做尺寸闸门；
                //    画面就是它，素材页还能把字形图集解出来画成卡。
                //  · 文字/数据表（DictionaryReader<String,String>：对话、事件脚本、日程、
                //    字符串表、礼物喜好…）：不是画面，装上去不改变任何像素 ⇒ 【不收】。
                // 旧写法在这里只看"解不解得出纹理"，于是整个第二类被当成字体一路放行 ——
                // 实测 1083 那个 furry 包因此往本体塞了 1236 份文字表，而包里那份是【1.6 之前】
                // 编的：Strings/BigCraftables 比本体少 14 条串、没有 BigChest_Name（大箱子是 1.6
                // 加的）、指环措辞还是 1.5.6 的 "re-roll trinkets"。整份盖上去=把本体较新的
                // 文本表退回旧版，缺键的条目在游戏里显示成空白或原样键名 —— 那才是真的画错。
                var theirs = XnbDecoder.TryLocate(targetPath);
                if (theirs is null)
                {
                    if (!XnbCodec.IsFontAsset(targetPath))
                    {
                        res.AddReject(rel, asset, LocService.Tf("{0}：{1} 在本体里不是图像也不是字体，是文字/数据表，不安装",
                            name, asset));
                        continue;
                    }
                    res.Items.Add(new PlanItem(asset, bytes, false, rel));
                    continue;
                }

                // 目标能解出纹理：这才是"整张换表"那种情况。
                // 高度不等【可以照装】：XNB 自描述，换进去的就是那张表自己的宽高，而游戏按固定
                // 行距从顶上往下切格 —— 源表矮了则下面几格取不到东西（那个表情画不出），高了则
                // 多出的几格游戏根本不会去索引。两种都只是"格数不对"，没有一个源像素换过行。
                //
                // 宽度不等要分两种，一种放行、一种拦（实测取证，别再一刀切）：
                //  · 源 64 宽 + 目标 128 宽立绘 ⇒ 放行，丢掉的第 2 列是【眨眼帧】。
                //    逐像素比对本体四张 128×64 立绘的两列：Grandpa 差异 95/4096、MrQi 56、
                //    Gil 783、Wizard 187，差异区域全部落在眼部（Grandpa x∈[12,51] y∈[23,45]）
                //    ⇒ 第 2 列是眨眼，不是第二套表情。装上去少眨一次眼，脸是对的。
                //    本体里本就有 6 张 64×64 立绘（AnsweringMachine / Bouncer / Dwarf /
                //    Gunther），证明游戏本来就处理得了单列立绘。
                //    —— 以前这里一律拒绝，是把"少眨一次眼"当成了"画错人"，过严。
                //  · 其余宽度不等 ⇒ 必须拦：一行里的列数变了，第 N 格直接落到别人的像素上
                //    ⇒ 横向串格，那不是"少几帧"，是画错人。
                var mine = XnbDecoder.TryLocate(bytes);
                if (mine is not null && mine.Width != theirs.Width
                    && !IsSingleFramePortraitFill(mine.Width, mine.Height, theirs.Width, theirs.Height, asset))
                {
                    res.AddReject(rel, asset, LocService.Tf("{0}：此文件 {1}×{2}，游戏 {3} 为 {4}×{5} —— 宽度不一致，整表替换会导致横向错位",
                        name, mine.Width, mine.Height, asset, theirs.Width, theirs.Height));
                    continue;
                }
                if (mine is not null && mine.Height != theirs.Height)
                    res.Resized.Add(LocService.Tf("{0}：此文件 {1}×{2}，游戏 {3} 为 {4}×{5} —— 按原样整体替换，{6}",
                        name, mine.Width, mine.Height, asset, theirs.Width, theirs.Height,
                        mine.Height < theirs.Height ? "本体多出来的那几格是空的" : "超出本体的那几格游戏取不到"));
                if (mine is not null && IsSingleFramePortraitFill(mine.Width, mine.Height, theirs.Width, theirs.Height, asset))
                    res.Resized.Add(LocService.Tf("{0}：此文件 {1}×{2}，游戏 {3} 为 {4}×{5} —— 按原样整体替换，{6}",
                        name, mine.Width, mine.Height, asset, theirs.Width, theirs.Height,
                        "本体右边那列是眨眼帧，这份只有单帧，角色少眨一次眼"));
                if (res.Resized.Count > 0) res.FileNotes[rel] = res.Resized[^1];   // 这份的尺寸说明归到自己名下
                res.Items.Add(new PlanItem(asset, bytes, false, rel));
            }
            else
            {
                // PNG 不当候选：往本体 Content 里贴图这条路只收游戏认得的 .xnb。
                // 作者只交 PNG 的那一张不是"失败的图"，是【没打算走这条路】—— 实测 Villagers
                // Anime Portrait（N97）：`xnb/` 里 46 张 xnb 才是给本体的，`Character Previews/`
                // 里 45 张 png 是给人看的效果图，而旧写法按【文件夹名排序】取第一版，
                // "Character Previews" 排在 "xnb" 前面 ⇒ 账上 20 条是从效果图重编出来的，
                // 其中 Portraits/Alex 当时就在位 —— 游戏里盖着的确实是效果图（用户 2026-10-10 抓到）。
                // 整包一张 xnb 都没有的那种更早就进不了这条路（ModService.OverrideChannelTakes 的
                // anyXnb 硬前提），这儿只管混装包里那些只有 PNG 的张。
                res.AddReject(rel, asset, LocService.Tf("{0}：只有 PNG，内容包只收 XNB", name));
            }
        }
        // 一个包里多张图撞到同一个资产：以前【整组都不装】（"作者没写清的事我们不能替他编"）。
        // 实测这条把一类正常的包全挡死了 —— Yomi's Golden Princess Hairstyle 就是
        // 同一张 Characters/Farmer/hairstyles 的 4 个配色（三色/可染白/金/黑），各带 png+xnb，
        // 8 个文件一起被拒，玩家一个都用不上。现在按【包内所在文件夹】分版本：
        // 候选只有 .xnb（PNG 在上面那步就不收了），所以"同一文件夹里 png 与 xnb 是同一份画的
        // 两种编码"这件事已经不会发生 —— 分版本只看【装了哪几个文件夹的 xnb】。
        // 不同文件夹才是不同版本，默认取按名排序的第一版，其余记进 VariantLabels，
        // 让玩家在对象卡的版本行里换（SwitchVariant）。
        var byTarget = res.Items.GroupBy(i => i.Asset, StringComparer.OrdinalIgnoreCase).ToList();
        res.Items.Clear();
        foreach (var g in byTarget)
        {
            if (g.Count() == 1) { res.Items.Add(g.First()); continue; }
            var versions = g.GroupBy(i => VariantFolderOf(i.Source), StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => x.OrderBy(y => y.Source, StringComparer.Ordinal).First())
                .ToList();
            if (versions.Count == 1) { res.Items.Add(versions[0]); continue; }
            var labeled = versions.Select(v => v with
            {
                Label = VariantFolderOf(v.Source).Length > 0
                    ? VariantFolderOf(v.Source)
                    : Path.GetFileNameWithoutExtension(v.Source),
                Note = res.FileNotes.GetValueOrDefault(v.Source, "")
            }).ToList();
            res.Items.Add(labeled[0]);
            res.VariantLabels[g.Key] = labeled.Select(v => v.Label).ToList();
            res.VariantItems[g.Key] = labeled;
        }
        // 同一份画的另一种编码（作者常把 PNG 源图与 XNB 并排放：Cursors.xnb + cursors.png）：
        // 装上的那份已经把它代表了，再列一条"不可用/无法识别"就是纯误导 —— 它不是失败，
        // 是没用的第二份编码。只摘"同资产已装上、且文件名只差扩展名"这一种，
        // 其它跳过原因（真不适配、真撞车）照旧逐条点名。
        res.RejectRows.RemoveAll(x => x.Asset.Length > 0 && res.Items.Any(i =>
            i.Asset.Equals(x.Asset, StringComparison.OrdinalIgnoreCase)
            && Path.GetFileNameWithoutExtension(i.Source)
                .Equals(Path.GetFileNameWithoutExtension(x.Rel), StringComparison.OrdinalIgnoreCase)));
        return res;
    }
}
