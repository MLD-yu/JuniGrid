using System.Collections.Concurrent;
using System.IO;

namespace JuniGrid.Services;

public static partial class ContentOverrideService
{
    // ───────────────────── 目标解析 ─────────────────────

    /// <summary>某个本体资产的纹理宽高，按需精确解析并记忆化。解不出来返回 (0,0)。
    ///
    /// 为什么不建全量宽高索引：<see cref="BuildStemIndex"/> 要扫 3550 个 xnb，
    /// TryTextureArea 只读 14 字节头，很快；但真宽高要解压（TryLocate），
    /// 全量跑 4 秒以上 —— 这正是立绘页当初专门加跳过闸门躲的开销。
    /// 而【候选】每次只有 2–5 个（同一个基名），单独解这几个并缓存即可，
    /// 同一个资产在包里出现几十次也只解一次。
    ///
    /// 超过 24MB 的 xnb 直接放弃（那必然是 4096² 级别的超大图），免得为一个
    /// 判定结果解压几十 MB —— 判不出来就退回面积筛选，与以前一样不会更糟。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int w, int h)>
        _sizeCache = new(StringComparer.OrdinalIgnoreCase);

    private static (int w, int h) TextureSizeOf(string gamePath, string asset)
    {
        var key = gamePath + "|" + asset;
        return _sizeCache.GetOrAdd(key, _ =>
        {
            try
            {
                var f = AssetFullPath(gamePath, asset);
                if (!File.Exists(f) || new FileInfo(f).Length > 24L * 1024 * 1024) return (0, 0);
                var lay = XnbDecoder.TryLocate(f);
                return lay is null ? (0, 0) : (lay.Width, lay.Height);
            }
            catch { return (0, 0); }
        });
    }

    /// <summary>
    /// 基名 → Content 里的候选资产，连同只读 14 字节头算出的像素面积。
    /// 这一步【只读头不解压】，扫 3550 个资产约 1.7 秒（实测）——
    /// 立绘页当初专门加过跳过闸门躲的就是解压那几秒，别在这里把它请回来。
    /// 能不能整张替换由 <see cref="IsSwappable"/> 按需判 + 缓存，不塞进这张表。
    /// </summary>
    private static Dictionary<string, List<(string asset, int area)>> BuildStemIndex(
        string contentRoot, out HashSet<string> variantBases)
    {
        var idx = new Dictionary<string, List<(string, int)>>(StringComparer.OrdinalIgnoreCase);
        // 语言变体不进候选表：换基名那张才是正路，动变体等于替作者决定中文界面长什么样。
        // 但【基名本身存在】这件事要记下来 —— 包里若是 SmallFont.zh-CN 而本体只有那张中文变体，
        // 查不到候选就说"本体没有叫 SmallFont 的图像"是误报（本体明明就在那儿躺着）。
        // 在这里顺手记，ResolveTarget 是每个包内文件调一次，不能让它扫 3550 个键。
        variantBases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(contentRoot, "*.xnb", SearchOption.AllDirectories))
        {
            var asset = Path.GetRelativePath(contentRoot, f).Replace('\\', '/');
            asset = asset[..^".xnb".Length];
            var stem = Path.GetFileName(asset);
            if (LocaleTail.IsMatch(stem))
            {
                variantBases.Add(Path.GetFileNameWithoutExtension(stem));
                continue;
            }
            // area==0 = 解不出纹理但文件确实在本体里（字体 SpriteFont 就是这一类）。
            // 原来 `area is null ⇒ continue` 把它们全丢了，于是字体包一律报
            // "游戏本体里没有叫 SmallFont 的图像" —— 那是误报，文件明明就在那儿。
            var area = XnbCodec.TryTextureArea(f) ?? 0;
            if (!idx.TryGetValue(stem, out var l)) idx[stem] = l = new List<(string, int)>();
            l.Add((asset, area));
        }
        return idx;
    }

    /// <summary>能不能整张替换的判定结果，记住是因为它要解压内容区。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool>
        _swapCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 这份本体资产我们【能不能整张换掉】—— 有 Texture2DReader（纹理）或
    /// SpriteFontReader（字体）才算数，两者都是自描述容器，整份写进去游戏就按新表渲染。
    /// <para>素材页"这一行算不算一张图"用的也是它（见 <see cref="ImageRows"/>）：
    /// 装进来的闸门和列出来的闸门必须是同一个答案，否则会出现"装上了但界面上找不到"。</para>
    ///
    /// 判据【绝对不能】写 <c>area &gt; 0</c>：排程表与对白是 DictionaryReader&lt;String,String&gt;，
    /// 它的 bodyLen-177 照样可能是正的 4 倍数 —— 实测 Maru 的排程表算出 area=636、
    /// Strings 排程表 155、Penny 的对话表 3177，于是 area&gt;0 会把这堆纯文本当成
    /// "能换的纹理"顶进候选，Maru / Penny 就又变成"本体里有 2 张同名的，分不清该换哪一张"。
    ///
    /// 为什么按需判而不是建索引时全判：判"是不是纹理"得解压内容区，
    /// 3550 个资产全跑一遍要 4 秒以上（只读头全扫只要 1.7 秒），而 Plan 每装一个包跑一次。
    /// 真正要问的只有【同名候选】2–5 个 —— 一个包里绝大多数基名唯一，
    /// 唯一候选在 ResolveTarget 里直接返回，走都走不到这里。
    /// </summary>
    public static bool IsSwappable(string gamePath, string asset)
        => _swapCache.GetOrAdd(gamePath + "|" + asset, _ =>
        {
            try
            {
                // 目录级的快速排除：这些目录按内容分类就是文本/数据表
                // （实测 Characters/schedules/*、Characters/Dialogue/*、Strings/* 全是
                // DictionaryReader<String,String>），解内容区也只是白花时间。
                var a = asset.Replace('\\', '/');
                if (a.Contains("/schedules/") || a.Contains("/Dialogue/")
                    || a.StartsWith("Strings/", StringComparison.OrdinalIgnoreCase)) return false;

                var f = AssetFullPath(gamePath, asset);
                if (!File.Exists(f)) return false;
                return XnbCodec.IsTextureAsset(f) || XnbCodec.IsFontAsset(f);
            }
            catch { return false; }
        });

    /// <summary>
    /// 包内一个文件 → Content 资产。两级梯子，推不出来就【不猜】：
    /// ① 包自己声明的目标（<c>LooseSprites.png/Cursors.png</c>、<c>Starry Sky/LooseSprites/Cursors.xnb</c>）；
    /// ② 扁平单文件：基名唯一，或按像素面积筛到唯一 —— 实测 Nexus 1839 那 102 个文件只靠基名
    ///    有 101 个撞车（Abigail 同时在 Characters/、Portraits/、Dialogue/、schedules/ 里），
    ///    加上面积才剩 87 个唯一。
    /// 仍撞车的（立绘表和走路表都是 128×64 的 MrQi）只能点名问人：猜错的代价是游戏里
    /// 某个角色长成别人的脸，而玩家会以为是包的锅。
    /// <paramref name="sawVanillaName"/> = 这个名字在本体里【确实是一张原版图】（哪怕尺寸不对）。
    /// 调用方靠它区分"根本不是给本体的包"（交回原路）和"给本体但画的是别的版本"（就地拒绝并点名）——
    /// 只数装成功的会漏掉后者，而后者正是当年"13 个文件转出 5 条假立绘"的入口。
    /// </summary>
    private static string? ResolveTarget(string gamePath, string relInPack, string file,
        Dictionary<string, List<(string asset, int area)>> stemIndex, HashSet<string> variantBases,
        HashSet<string> modDeclared, out string? why, out bool sawVanillaName)
    {
        why = null; sawVanillaName = false;
        var declared = ModService.ResolveAssetTarget(gamePath, relInPack, modDeclared);
        if (declared is not null && File.Exists(AssetFullPath(gamePath, declared)))
        {
            sawVanillaName = true;
            return declared;
        }

        var stem = Path.GetFileNameWithoutExtension(relInPack);
        if (!stemIndex.TryGetValue(stem, out var all) || all.Count == 0)
        {
            // 先分清两种"查不到"，它们要给的结论完全不同：
            //  · 带语言变体后缀的（SmallFont.zh-CN）—— 本体【有】那张，只是被候选表排除了。
            //    无条件报"本体没有"是误报：用户会以为包坏了，而本体那张其实正躺在 Content 里。
            //  · 真的没有（实测 Dobson / tinyFontBorder / DialogBoxGreen：全库 3550 个资产搜不到，
            //    且游戏 DLL 的字符串堆里连这个名字都没有 —— 是游戏自己不认识它）—— 这才是实话。
            var bare = Path.GetFileNameWithoutExtension(stem);
            why = variantBases.Contains(bare)
                ? LocService.Tf("{0}：本体中对应的是 {1}；本程序不替换语言变体 —— 请替换 {1} 的原版",
                    Path.GetFileName(relInPack), bare)
                : LocService.Tf("{0}：游戏本体中不存在名为 {1} 的图像", Path.GetFileName(relInPack), stem);
            return null;
        }
        // 【剔掉换不了的】同名候选里混着排程表与对白（实测 Characters/schedules/Abigail、
        // Characters/Dialogue/Abigail、Strings/schedules/Abigail 全是 DictionaryReader<String,String>）。
        // 我们没有这类资产的编码器，整份塞进去只会把角色的行程与对白写坏，所以它们不进候选：
        // 早先只按 area==0 挑（把"算不出纹理的"一律当字体），每个角色凭空多出 3 个"同名"，
        // 于是 Abigail/Harvey/George… 26 个角色集体报"分不清该换哪一张"，
        // 而包里那张 128×320 跟本体 Portraits/Abigail 的 128×320 根本一模一样。
        //
        // 只有【多个候选】才需要判；单个候选直接返回（它要么装得上，要么 Plan 会给文案）。
        var cands = all.Count == 1 ? all : all.Where(c => IsSwappable(gamePath, c.asset)).ToList();
        if (cands.Count == 0)
        {
            why = LocService.Tf("{0}：本体中名为 {1} 的文件属于日程表/对话等文本，无法替换",
                Path.GetFileName(relInPack), stem);
            return null;
        }
        sawVanillaName = true;      // 名字对得上原版图，尺寸或歧义的问题在下面报
        var isPng = Path.GetExtension(file).Equals(".png", StringComparison.OrdinalIgnoreCase);
        if (cands.Count == 1 && isPng)
            return cands[0].asset;      // PNG 装不上，但认得出是哪张 ⇒ 拒绝文案与"同版另一编码"去重都要用它

        // 目标是字体（解不出纹理，但类型表里有 SpriteFontReader）⇒ 没有面积可比，
        // 可【名字对得上】本身就是最强的一条证据：字体表是自描述容器（内部记着自己的
        // 字号/行距/字形贴图），整份替换后游戏按新表渲染，不需要我们读懂它，
        // 也不存在"格位对不上"的问题。必须在这里返回 ——
        // 再往下走，.xnb 字体会在读尺寸那步拿到 null 被误判成"读不出尺寸"。
        if (!isPng)
        {
            // 用 TextureSizeOf 而不是 area==0：这里要问的是"解不解得出纹理"，
            // 而 area==0 也可能是别的原因（数据类资产），两者不再混为一谈。
            var byName = cands.Where(c => TextureSizeOf(gamePath, c.asset).w == 0).ToList();
            if (byName.Count == 1) return byName[0].asset;
            if (byName.Count > 1)
            {
                why = LocService.Tf("{0}：本体中有 {1} 张同名的 {2}，无法确定替换对象",
                    Path.GetFileName(relInPack), byName.Count, stem);
                return null;
            }
        }

        // 包里这张图的宽高读一次，既拿来筛候选也拿来写拒绝文案（两处各自再解一遍是浪费，
        // 而且两边万一读得不一致，文案就会说假话）。
        (int w, int h)? srcDims = isPng
            ? PixelKit.DecodePng(file) is { } t ? (t.Width, t.Height) : null
            : XnbDecoder.TryLocate(file) is { } l ? (l.Width, l.Height) : null;
        if (srcDims is null)
        {
            why = LocService.Tf("{0}：无法读取该图像尺寸", Path.GetFileName(relInPack));
            return null;
        }
        // 【宽度定案】立绘 128 宽为主、走路表 64 宽（实测本体 Portraits/ 是 128 宽 95 张 +
        // 64 宽 6 张；Characters/ 绝大多数 64 宽），所以裸包里一个 <Name>.xnb 属于哪张表，
        // 宽度基本就能定。放在面积筛选【之前】：Characters/ 与 Portraits/ 同名是常态
        // （Abigail 两边都有），而包里的图跟其中任意一张对不上也是常事（作者按旧格数画的），
        // 先用宽度定下是哪一类表，才不会走进"哪个都对不上"的死胡同。
        if (!isPng)
        {
            // 宽高都对上：最理想，直接定。
            var exact = cands
                .Where(c => TextureSizeOf(gamePath, c.asset) == (srcDims.Value.w, srcDims.Value.h))
                .ToList();
            if (exact.Count == 1) return exact[0].asset;

            // 宽度对上但高度不同 = 确定是同一类表（立绘 vs 走路表），只是格数不同。
            // 这种情况以前直接判"不适配"，现在交回 Plan 照装 —— 游戏用 SpriteBatch
            // 的源矩形按固定格位切帧、目标矩形另外缩放，少几帧表情不会串格。
            var sameWidth = cands
                .Where(c => srcDims.Value.w > 0 && TextureSizeOf(gamePath, c.asset).w == srcDims.Value.w)
                .ToList();
            if (sameWidth.Count == 1) return sameWidth[0].asset;

            // 源是 64×64 单帧、目标是 128×64 立绘 ⇒ 也是"同一张脸，只是少一列眨眼"。
            // 实测：本体四张 128×64 立绘（Grandpa/Gil/MrQi/Wizard）的第 2 列与第 1 列
            // 逐像素只差 56~783/4096，差异区域全部落在眼部 ⇒ 第 2 列是眨眼帧。
            // 以前这里匹配不到（同宽要求 64==128），一路掉到下面报"几张对不上"，
            // 把"少眨一次眼"误判成了"这不是给本体的包"。放行后交给 Plan 的宽度闸门，
            // 那里有同一个判据（IsSingleFramePortraitFill）兜底。
            var singleFrameFill = cands
                .Where(c => IsSingleFramePortraitFill(srcDims.Value.w, srcDims.Value.h,
                             TextureSizeOf(gamePath, c.asset).w, TextureSizeOf(gamePath, c.asset).h, c.asset))
                .ToList();
            if (singleFrameFill.Count == 1) return singleFrameFill[0].asset;
        }

        var hit = cands.Where(c => c.area == srcDims.Value.w * srcDims.Value.h).ToList();
        if (hit.Count == 1) return hit[0].asset;
        // 面积一张都没对上、但本体里这个名字只有一张图：仍然交回去，
        // 让下面那道尺寸闸门用「64×64 对 64×128」说话 —— 报"这张 4096 格"是要人自己开计算器，
        // 而装不上的人想知道的是"差在哪"，不是"差多少个数"。
        if (hit.Count == 0 && cands.Count == 1) return cands[0].asset;
        // 报"没有这个尺寸"必须把【每个候选各是多大的】一起列出来。
        // 原话是"多半是给别的版本画的"，实测是误导：Grandpa 包内 64×64 与本体
        // Portraits/Grandpa 128×64 讲的是同一个人（本体那张是两个 64 格并排的两帧），
        // 只是包里那张少一帧 —— 不是"别的版本"。只报尺寸数字，人只能自己猜差在哪。
        why = hit.Count == 0
            ? LocService.Tf("{0}：此文件 {1}×{2}，与本体中名为 {3} 的数张均不匹配（{4}）",
                Path.GetFileName(relInPack), srcDims.Value.w, srcDims.Value.h, stem,
                string.Join("、", cands.Select(c =>
                {
                    var d = TextureSizeOf(gamePath, c.asset);
                    return d.w == 0 ? c.asset : LocService.Tf("{0} 为 {1}×{2}", c.asset, d.w, d.h);
                })))
            : LocService.Tf("{0}：本体中有 {1} 张同名同尺寸的原版图像（{2}），无法确定替换对象",
                Path.GetFileName(relInPack), hit.Count, string.Join("、", hit.Select(h => h.asset)));
        return null;
    }

    /// <summary>
    /// "单帧立绘补列"放行：源 64 宽、目标 128 宽，且目标是立绘。
    ///
    /// 为什么只认这一个组合 —— 结论来自本体资产实测，不是对美术手法的猜测：
    /// 本体 <c>Content/Portraits/</c> 是 128 宽 95 张 + 64 宽 6 张；
    /// 128×64 的只有 Grandpa / Gil / MrQi / Wizard 四张，把它们的两列逐像素比对：
    /// Grandpa 差异 95/4096、MrQi 56、Gil 783、Wizard 187，且差异区域全部落在眼部
    /// （Grandpa 是 x∈[12,51] y∈[23,45]，正是眼睛的位置）⇒ 第 2 列是<b>眨眼帧</b>，
    /// 不是第二套表情、也不是第二个人。所以拿 64 宽单帧换进去，丢的是眨眼，脸是对的。
    /// 而本体里本就有 6 张 64×64 立绘（AnsweringMachine / Bouncer / Dwarf / Gunther），
    /// 证明游戏本来就渲染得了单列立绘。
    ///
    /// 高度也要求一致（都 64）：只允许"少一列眨眼"，不允许同时少行 ——
    /// 少行会丢掉整排表情，那是另一回事，仍该由下面的高度分支去照装/拒绝。
    /// 其余宽度不等一律仍走整单拒绝，那是真串格。
    /// </summary>
    private static bool IsSingleFramePortraitFill(
        int mineW, int mineH, int theirsW, int theirsH, string asset)
        => mineW == 64 && mineH == 64 && theirsW == 128 && theirsH == 64
           && asset.StartsWith("Portraits/", StringComparison.OrdinalIgnoreCase);
}
