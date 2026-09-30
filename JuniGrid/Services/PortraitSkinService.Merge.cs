using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace JuniGrid.Services;

public sealed partial class PortraitSkinService
{
    /// <summary>配置里给这个角色选的包已经不在本次扫描的候选里（包被卸载、或目录被人手工搬走）
    /// 时返回那个消失的包名，否则 null。不清配置 —— 那是不可逆的用户数据，包装回来选择就该复活；
    /// 界面上只把"现在其实是默认脸"说清楚，否则用户只会以为"我明明换过了"。</summary>
    public static string? StalePack(IEnumerable<PortraitSkinOption> options, string? want)
    {
        if (string.IsNullOrWhiteSpace(want)) return null;
        return options.Any(o => string.Equals(o.PackFolder ?? "", want, StringComparison.OrdinalIgnoreCase))
            ? null : want;
    }

    /// <summary>
    /// 同脸别名 NPC 表（SVE 的 ScarlettFake ↔ Scarlett 等）：别名 → 本名。
    /// 这些是游戏里真有其人的独立 NPC（有自己的对话与日程），只是被同一个包套了同一张脸 ——
    /// 不能折叠成一条，但要标注，否则看起来像我们把同一个角色列了两遍、去重漏了。
    /// 判据两条都要满足：① 两侧【任意一张脸】来自同一个文件；② 名字互为前缀且下一格是大写
    /// （Scarlett → ScarlettFake；Jas → Jasper 这种不算，实测会把 Jasper 并错）。
    /// 只满足①的是两张真的不同角色共用了素材（比如同款通用脸），标成别名反而误导。
    /// ⚠ ①为什么不能只看「默认行那一张」：v1.7.32 把「默认」行锁死原版后，本体的默认行不再
    /// 指向扩展包那张（Morris 的默认行=Morris.xnb，而 SCC-SVE 的图画在 MorrisTod 名下），
    /// 只比默认行就永远并不上 ⇒ 同一个人裂成两张裸 id 卡、本体还丢掉那款皮肤
    ///（2026-09-29 实测：对账从 0 格不一致涨到 20 格，Morris/Marlon/Gunther/Abigail/Emily 全中）。
    /// </summary>
    public static Dictionary<string, string> AliasMap(IReadOnlyList<PortraitCharacter> chars)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byFile = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        static IEnumerable<string> FaceFiles(PortraitCharacter ch) =>
            new[] { ch.Native?.SourceFile, ch.Vanilla?.SourceFile }
                .Concat(ch.Skins.Select(s => s.SourceFile))
                .Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f!)
                .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var ch in chars)
        {
            if (ch.Hidden) continue;   // 已经并过的卡不再参与（MergeAliasCards 幂等）
            foreach (var f in FaceFiles(ch))
            {
                if (!byFile.TryGetValue(f, out var lst)) byFile[f] = lst = new List<string>();
                lst.Add(ch.Id);
            }
        }
        foreach (var ids in byFile.Values)
        {
            if (ids.Count < 2) continue;
            var ordered = ids.OrderBy(x => x.Length).ToList();
            var main = ordered[0];
            foreach (var other in ordered.Skip(1))
                if (other.Length > main.Length
                    && other.StartsWith(main, StringComparison.OrdinalIgnoreCase)
                    && char.IsUpper(other[main.Length]))
                    map[other] = main;
        }
        return map;
    }

    /// <summary>游戏里同一个 NPC 的 id 同义词：SVE 把法师登记为 Magnus、把打手登记为 SVE_Henchman。
    /// 重绘构图不同时立绘相似度判不过，会拆成两张卡（实测法师）；DisplayName 也可能对不上 ——
    /// SVE_Henchman 的 DisplayName 是 {{i18n:Name.Henchman}}，没装汉化时解析不出、回落成 id，
    /// 与本体「Henchman」不同名，立绘文件也一个原版 xnb 一个 SVE png ⇒ 连 VariantGroups 都进不了
    /// 同一组，E0 佐证根本轮不到用（实测 09-27 页上两张同脸卡）。
    /// 所以这张表既当合并佐证（E0），也当 VariantGroups 的分组信号。</summary>
    private static readonly (string A, string B)[] SameNpcPairs =
    {
        ("Wizard", "Magnus"), ("Leo", "ParrotBoy"), ("Gil", "GilSprite"),
        ("Henchman", "SVE_Henchman"),
    };

    private static bool KnownSameNpc(string a, string b) =>
        SameNpcPairs.Any(p =>
            (a.Equals(p.A, StringComparison.OrdinalIgnoreCase) && b.Equals(p.B, StringComparison.OrdinalIgnoreCase))
            || (a.Equals(p.B, StringComparison.OrdinalIgnoreCase) && b.Equals(p.A, StringComparison.OrdinalIgnoreCase)));

    /// <summary>v1.7.37：把 id 归到同义词表的 A 侧（<c>Magnus</c> → <c>Wizard</c>）；
    /// 表里没有的原样返回。跨包对账"同一份资产上还有谁"必须走这个键 ——
    /// 否则 SVE 的 <c>Characters/Magnus</c> 与 Baechu 的 <c>Characters/Wizard</c>
    /// 各算一套对手，谁也不会发现自己把别人压掉了（实测法师那条就是这么漏的）。
    /// ⚠ 只认【整名相等】，不按 '_' 拆段归并：<c>Magnus_Winter</c> 与 <c>Wizard_Winter</c>
    /// 在游戏里是两份不同资产（outfit 表指名哪一个就加载哪一个），并成一个键
    /// 会凭空造出对手。这类场合资产各自成套，交叉影响留给 Asset 原文自己说。</summary>
    private static string CanonicalNpcId(string id)
    {
        foreach (var (a, b) in SameNpcPairs)
            if (id.Equals(b, StringComparison.OrdinalIgnoreCase)) return a;
        return id;
    }

    /// <summary>
    /// 变体分组：两条信号取并集 —— ① DisplayName 相同（v1.3.8 旧口径，只有装了汉化才命中）；
    /// ② 同脸别名（默认立绘同一个文件 + id 互为前缀，与语言无关）。
    /// ②是必须的：SVE 给剧情多开的条目（ScarlettFake / GuntherSilvian / MorrisTod /
    /// MarlonFay）DisplayName 写的就是本尊的 i18n 键，没装汉化时屏幕上一个是中文一个是英文，
    /// 旧口径永远不命中 ⇒ 同一个人列成两张卡（用户实测）。
    /// 组内保持传入顺序（排序后原版在前 ⇒ 本尊当天然赢家）。
    /// </summary>
    private static List<List<PortraitCharacter>> VariantGroups(List<PortraitCharacter> chars)
    {
        var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in chars) parent[c.Id] = c.Id;
        string Find(string id)
        {
            while (parent[id] != id) { parent[id] = parent[parent[id]]!; id = parent[id]!; }
            return id;
        }
        void Union(string a, string b)
        {
            var ra = Find(a); var rb = Find(b);
            if (ra != rb) parent[rb] = ra;
        }
        foreach (var g in chars.GroupBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            // 占位名（"???" / "？？？"这类无字母数字的名字）不合并 —— 那是 mod 故意的
            // 神秘 NPC 命名（RSV 的 RelicSpirit 与 TreehouseGirl 实测：两个不同的角色，
            // 游戏里就显示 ???），并非翻译撞车（用户指正）。
            if (!g.Key.Any(char.IsLetterOrDigit)) continue;
            var head = g.First().Id;
            foreach (var c in g.Skip(1)) Union(head, c.Id);
        }
        foreach (var (aid, mid) in AliasMap(chars)) Union(mid, aid);
        // ③ 形态学分组：本体 id + 大写开头的剧情后缀（MorrisTod / MarlonFay / GuntherSilvian /
        // AbigailLewd / EmilyLewd）。锁死默认行之后 ① 那套"共用同一张文件"对这类条目全打不中
        //（本体的默认行=原版 xnb，扩展包/大改包的图画在变体名下），不加这条就会裂成裸 id 页，
        // 而且本体丢掉那款皮肤 —— 实机 2026-09-29 对账 0 → 76 格，全是 17 个 …Lewd 的本体。
        // ⚠ 已知代价（当天量过、用户拍板保留合并）：这些条目在 Data/NPCDispositions 里其实
        // 有自己的生日与出生点（"AbigailLewd": "…/fall 13//Town 231 12 1/AbigailLewd"），
        // 游戏里是真独立 NPC；合并后它们不上屏，但 Members 仍逐个落盘，那份数据不会没人管。
        // 想改成"独立页 + 本体页共享该皮肤"需要把合并的后半段（挂皮肤）与前半段（并页）拆开，
        // 别再直接删这段 —— 删了就是那 76 格。长度门槛与大写边界同佐证 E1（Jasper ≠ Jas 的变体）。
        {
            var idsA = chars.Where(c => !c.Hidden).Select(c => c.Id).ToList();
            foreach (var longer in idsA)
                foreach (var shorter in idsA)
                {
                    // 门槛原本是 4（照抄佐证 E1），把三位字母的本体全挡掉了 —— 用户 2026-09-30
                    // 报「Sam Lewd 为什么单独显示」：Sam 三个字母 ⇒ SamLewd 并不进去。
                    // 降到 3 之后本机实测只多出一对（Sam+SamLewd），Gil/Gus/Ian/Jas/Jio/Leo/Pam
                    // 一个新配对都没有；误并靠下一条「交界字母必须大写」挡（Samantha ≠ Sam 的变体）。
                    if (shorter.Length < 3 || longer.Length <= shorter.Length) continue;
                    if (!longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!char.IsUpper(longer[shorter.Length])) continue;
                    Union(shorter, longer);
                }
        }
        // 硬同义词（SVE 给同一人开的第二个 id）：DisplayName 与默认立绘文件都可能对不上，
        // 只能靠这张表先把两条并进同一组，合并佐证那步的 E0 才有的用。
        foreach (var (a, b) in SameNpcPairs)
        {
            var ca = chars.FirstOrDefault(c => c.Id.Equals(a, StringComparison.OrdinalIgnoreCase));
            var cb = chars.FirstOrDefault(c => c.Id.Equals(b, StringComparison.OrdinalIgnoreCase));
            if (ca is not null && cb is not null) Union(ca.Id, cb.Id);
        }

        var groups = new List<List<PortraitCharacter>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in chars)
        {
            if (!seen.Add(Find(c.Id))) continue;
            var g = chars.Where(x => string.Equals(Find(x.Id), Find(c.Id), StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (g.Count > 1) groups.Add(g);
        }
        return groups;
    }

    /// <summary>解码成 RGBA 后取 SHA1（含宽高）：PNG 与 XNB 画的是同一张图也算同一个。
    /// 解不动返回 null（不参与去重，宁可多一张卡也不能把不同的画并掉）。进程内缓存。</summary>
    private static readonly ConcurrentDictionary<string, string?> ArtHashCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>图片类缓存的键：路径 + mtime + 长度。
    /// ⚠ 纯路径键在生产里就是错的 —— 覆盖包会被我们自己重写（换肤、画风回落实测），
    /// 包内文件也会被 mod 更新/用户手动替换，缓存会一直供上一张图的结论，
    /// 于是「同画面合并」把不同的画并成一条、或该并的没并。带 mtime+长度即自失效。</summary>
    private static string FileStatKey(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return path + '|' + fi.LastWriteTimeUtc.Ticks + '|' + fi.Length;
        }
        catch { return path; }
    }

    private static string? ArtHash(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path;
        return ArtHashCache.GetOrAdd(FileStatKey(p), _ =>
        {
            try
            {
                DecodedTexture? tex = null;
                if (p.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase)) tex = XnbDecoder.TryDecode(p);
                if (tex is null && File.Exists(p)) tex = PixelKit.DecodePng(p);
                if (tex is null) return null;
                using var sha = System.Security.Cryptography.SHA1.Create();
                var head = BitConverter.GetBytes(tex.Width)
                    .Concat(BitConverter.GetBytes(tex.Height)).ToArray();
                sha.TransformBlock(head, 0, head.Length, null, 0);
                sha.TransformFinalBlock(tex.PixelsRgba, 0, tex.PixelsRgba.Length);
                return Convert.ToHexString(sha.Hash!);
            }
            catch { return null; }
        });
    }

    /// <summary>
    /// 每张卡的一道折叠：与「默认像」同一个【文件路径】的皮肤行（扩展包增强默认后，
    /// 被并进来的变体默认行常常就是同一个文件 —— 冈瑟/马龙实测）。
    /// v1.7.15：跨包「逐像素一致 / 近似」的哈希去重（DedupeByIdenticalArt）已整块删除 ——
    /// 同脸但来自不同 mod 的卡不再自动折叠，每张单独列出、由用户自行判断。删它的主因：
    /// 精确合并只看立绘、不看走路图，会把"脸相同、身体/config 不同"的卡误并（Susan 的
    /// SCCC 皮肤与 SVE 默认像逐字节同脸、身体却是 SCCC 重画的）。
    /// </summary>
    public static void FoldSkinsAgainstDefault(List<PortraitCharacter> characters,
        List<(string, string)>? diagnostics = null)
    {
        for (var ci = 0; ci < characters.Count; ci++)
        {
            var ch0 = characters[ci];
            if (ch0.Skins.Count == 0) continue;
            var anchorSrc = ch0.Vanilla?.SourceFile ?? ch0.Native?.SourceFile;
            var keptS = new List<PortraitSkinOption>();
            foreach (var s in ch0.Skins)
            {
                if (string.IsNullOrWhiteSpace(s.SourceFile)) { keptS.Add(s); continue; }
                if (string.Equals(s.SourceFile, anchorSrc, StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics?.Add((ch0.Id, $"皮肤「{s.PackName}」与默认像同文件，已折叠"));
                    AppLog.Warn("Portraits", $"[视觉折叠] {ch0.Id} 皮肤「{s.PackName}」与默认像同文件，折叠");
                    continue;
                }
                keptS.Add(s);
            }
            // v1.7.15：哈希去重（DedupeByIdenticalArt）整块删除 —— 同脸但来自不同 mod 的皮肤
            // 不再自动折叠，每张单独列出、由用户自行判断。主要毛病：精确合并只看立绘、不看走路图，
            // 会把"脸相同、身体/config 不同"的卡误并（Susan 的 SCCC 皮肤与 SVE 默认像逐字节同脸、
            // 身体却是 SCCC 重画的）。这里只保留上面那条"皮肤与默认像同【文件路径】"的折叠
            //（同一个文件，不是哈希近似，属于真冗余）。
            if (keptS.Count != ch0.Skins.Count)
                characters[ci] = ch0 with { Skins = keptS };
        }
    }


    private static readonly ConcurrentDictionary<string, long?> DHashCache = new(StringComparer.OrdinalIgnoreCase);
    private static long? DHash(string path)
    {
        return DHashCache.GetOrAdd(FileStatKey(path), p =>
        {
            try
            {
                DecodedTexture? tex = null;
                if (p.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase)) tex = XnbDecoder.TryDecode(p);
                if (tex is null && File.Exists(p)) tex = PixelKit.DecodePng(p);
                if (tex is null) return null;
                const int W = 9, H = 8;
                var gray = new double[W * H];
                for (var gy = 0; gy < H; gy++)
                    for (var gx = 0; gx < W; gx++)
                    {
                        var sx = Math.Min(tex.Width - 1, gx * tex.Width / W);
                        var sy = Math.Min(tex.Height - 1, gy * tex.Height / H);
                        var o = (sy * tex.Width + sx) * 4;
                        // 立绘表透明底按黑算：alpha 低的像素灰度记 0
                        gray[gy * W + gx] = tex.PixelsRgba[o + 3] > 16
                            ? tex.PixelsRgba[o] * 0.299 + tex.PixelsRgba[o + 1] * 0.587
                              + tex.PixelsRgba[o + 2] * 0.114
                            : 0;
                    }
                long hash = 0;
                var bit = 0;
                for (var y = 0; y < H; y++)
                    for (var x = 0; x < W - 1; x++, bit++)
                        if (gray[y * W + x] > gray[y * W + x + 1]) hash |= 1L << bit;
                return hash;
            }
            catch { return null; }
        });
    }

    /// <summary>两个 64 位哈希的汉明距（不同位数）。</summary>
    private static int Hamming(long a, long b)
        => System.Numerics.BitOperations.PopCount((ulong)(a ^ b));

}
