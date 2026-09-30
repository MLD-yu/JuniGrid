using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace JuniGrid.Services;

public sealed partial class PortraitSkinService
{
    /// <summary>这个 NPC 所有卡自带的走路表 = 走路表的「底图清单」，场合资产补白（SubstituteNeedsPad）
    /// 与立绘铺高比的就是它。拿整份而不是"选中那张"：底图是谁提供的我们说了不算，只能保证盖满最坏情况
    ///（与立绘那条 faceSizes 同一个理由）。</summary>
    public static List<string> BodySheetsOf(PortraitCharacter ch)
    {
        var sheets = new List<string>();
        foreach (var o in ch.AllOptions)
            if (o.SpriteFile is { Length: > 0 } sf && File.Exists(sf)) sheets.Add(sf);
        return sheets;
    }

    /// <summary>
    /// v1.7.13：皮肤自己没走路表时的身体解析链第二步 —— 沿 manifest Dependencies 广度优先
    /// （含依赖的依赖）找第一个给这个角色配了走路表的【已启用】前置包，返回（那张表，它的包目录）。
    /// 一个都没有 → (null,null)（= 沿用默认：落盘端不钉 Characters，预览端画默认行）。
    /// 只认扫描结果里存在的包：没装/被禁用的前置在游戏里本来就不加载，借它的身体等于钉死图。
    /// 预览窗与写盘共用这一个入口，两边不许各写一套判据（v1.7.12 的教训就是三方各说一套）。
    /// v1.7.38：前置的判定从"它得出一张卡"放宽成"它声明过这个角色的走路表"——
    /// 见循环里 DeclaredBodyFile 那段（法师：SCC-SVE 只按季发货 ⇒ 不出卡 ⇒ 链子跳过它）。
    /// </summary>
    public static (string? File, string? PackFolder) ResolvePrereqBody(PortraitScanResult scan,
        PortraitCharacter ch, PortraitSkinOption selected)
    {
        if (scan is null || ch is null || selected is null
            || string.IsNullOrEmpty(selected.PackFolder)) return (null, null);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { selected.PackFolder };
        var queue = new Queue<string>();
        queue.Enqueue(selected.PackFolder);
        while (queue.Count > 0)
        {
            if (!scan.PackDeps.TryGetValue(queue.Dequeue(), out var deps)) continue;
            foreach (var uid in deps)
            {
                if (!scan.PackFolderByUid.TryGetValue(uid, out var depFolder)) continue;
                if (!seen.Add(depFolder)) continue;
                var hit = ch.AllOptions.FirstOrDefault(o =>
                    string.Equals(o.PackFolder, depFolder, StringComparison.OrdinalIgnoreCase)
                    && o.SpriteFile is not null
                    && LooksLikeSprite(o.SpriteFile)
                    && !IsFakeWalkSheet(o.SpriteFile, o.SourceFile, ch.Id));
                if (hit?.SpriteFile is { Length: > 0 } hitSpr) return (hitSpr, depFolder);
                // v1.7.38：祖先包【不出卡】但它确实声明了这个角色的走路表 ⇒ 用它的表。
                // 法师实测形状：Donut 的 fifadog 分支只有脸，它声明的头号前置 SCC-SVE 把
                // Magnus 的身子按季发（Characters/Magnus ← Magnus_Spring…64×480），而该包对
                // Magnus 的【基础】文件作者没发货 ⇒ 对这个角色一张卡都不出 ⇒ 旧代码在这里空手，
                // 链子跳到 dep#2 的 SVE，于是钉的是 SVE 的身子（用户：其它角色都对，就法师不对）。
                if (DeclaredBodyFile(scan, ch, depFolder) is { Length: > 0 } sheet)
                    return (sheet, depFolder);
                queue.Enqueue(depFolder);
            }
        }
        return (null, null);
    }

    /// <summary>某个包对【这个角色】声明过哪张走路表 —— 从 RivalSheets 里按包名捞，不看它出不出卡。
    /// 取最高的那张；同高时按春→夏→秋→冬优先（和弹窗"没点季节 tab 时画春季"同一口径）。</summary>
    private static string? DeclaredBodyFile(PortraitScanResult scan, PortraitCharacter ch, string packFolder)
    {
        if (scan.RivalSheets.Count == 0 || string.IsNullOrEmpty(packFolder)) return null;
        var keys = new List<string> { "Characters/" + CanonicalNpcId(ch.Id) };
        foreach (var v in scan.VariantAssets)
            if (v.Kind.Equals("Characters", StringComparison.OrdinalIgnoreCase)
                && v.BaseId.Equals(ch.Id, StringComparison.OrdinalIgnoreCase)
                && !keys.Contains("Characters/" + CanonicalNpcId(v.VariantId), StringComparer.OrdinalIgnoreCase))
                keys.Add("Characters/" + CanonicalNpcId(v.VariantId));
        var best = "";
        var bestH = -1;
        foreach (var key in keys)
        {
            if (!scan.RivalSheets.TryGetValue(key, out var rows)) continue;
            foreach (var r in rows)
            {
                if (!r.FullSheet || r.File.Length == 0) continue;
                if (!string.Equals(r.Pack, packFolder, StringComparison.OrdinalIgnoreCase)) continue;
                if (!LooksLikeSprite(r.File) || IsFakeWalkSheet(r.File, null, ch.Id)) continue;
                var seasonalFirst = key.EndsWith("_Spring", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                var rank = (r.H, seasonalFirst);
                var cur = (bestH, 1);
                if (best.Length > 0 && rank.CompareTo(cur) <= 0) continue;
                if (best.Length == 0 && r.H <= 0) continue;
                best = r.File; bestH = r.H;
            }
        }
        return best.Length > 0 ? best : null;
    }

    /// <summary>选中这张卡、而它【自己没有走路图】时，身体该用哪一张 ——
    /// 前置依赖链（含前置的前置、一直到祖先前置）里第一个有身体的包 → 都没有就用【默认行】
    /// （原版角色=默认行的精灵，取不到再回落原版 xnb；mod 角色=娘家行的精灵）。
    /// 默认行也没有才返回 (null,null)（那才是真没有身体可钉，例如 mod 角色娘家也不带走路表）。
    /// 返回的那一档【连它自己的包目录】一起给：按季取文件要靠它 —— 空串在 SeasonFilesFor 里
    /// 就是「原版/默认行」，那一档一律不查包的声明表（B63：借了别家的四季图当默认行封面），
    /// 所以卡罗琳那种四季文件只叫 Spring.png 的包，必须拿到真包名才解得出表（v1.7.15）。
    /// ⚠ 这条链的最后一档在 v1.7.12/v1.7.24 被摘掉过，理由是"钉默认 = 压掉别家 mod 配的身体"。
    /// 但弹窗预览一直画的就是默认行 ⇒ 落盘不钉时游戏里留下的是【第三家】的身体，
    /// 界面、落盘、游戏三方各说一套（2026-09-29 用户报：阿比盖尔选的肖像既不是她的默认像、
    /// 也不是所选包的前置，全量对账 25 格同一格）。预览端 Portraits.razor 共用本方法。
    /// </summary>
    public static (string? File, string? PackFolder) ResolveBody(string? gamePath,
        PortraitScanResult scan, PortraitCharacter ch, PortraitSkinOption selected)
    {
        var (preSpr0, preFolder) = ResolvePrereqBody(scan, ch, selected);
        if (preSpr0 is { Length: > 0 } preSpr && LooksLikeSprite(preSpr))
            return (preSpr, preFolder);
        var def = ch.IsVanilla ? ch.Vanilla : ch.Native;
        if (def?.SpriteFile is { Length: > 0 } defSpr && LooksLikeSprite(defSpr))
            return (defSpr, def.PackFolder);
        // 原版角色的默认行自己没填精灵（没被扩展包增强过）⇒ 游戏里那具身体就是原版 xnb。
        if (ch.IsVanilla && !string.IsNullOrEmpty(gamePath)
            && VanillaSpriteXnb(gamePath, ch.Id) is { Length: > 0 } xnb && LooksLikeSprite(xnb))
            return (xnb, null);
        return (null, null);
    }

    /// <summary>已生效的那张换肤，在【走路表】上比游戏里最高的那张矮几行 —— 立绘页那行提示的数据源。
    /// 读的就是覆盖包已经钉出去的图（assets/Characters/&lt;资产&gt;.png），【不重跑选表逻辑】：
    /// 界面说的必须就是盘上那张，否则又是"预览 / 落盘 / 游戏各说一套"（2026-09-29 阿比盖尔实测）；
    /// 而未选中的卡要预测，就得把落盘那六档出口在只读路径上再走一遍 —— 那是新的漂移源。
    /// 用户 2026-10-01 拍板：角标只出在已选中的那张 + 详情。
    /// ⚠ 只碰 Characters/：立绘矮（实测 Henchman 128&lt;192）是 B65/B66 那条「底图候选看不见对手」的账，
    /// 让走路表角标去说它 = 用 A 的 UI 写 B 的账（变异 A8 守这里）。
    /// ⚠ 对手只算 RivalSheet.FullSheet（整表声明）且【同宽】的那些 —— 局部差分与叠加层涂不满整行，
    /// 不同宽 CP 会整条作废；算进来会把一批 NPC 误报成"矮一大截"（变异 A4b、A6 各守一边）。</summary>
    public static List<BodyCoverageGroup> BodyCoverage(PortraitScanResult scan, string? gamePath, string charId)
    {
        var byKey = new Dictionary<string, (BodyCoverageAsset Asset, string[] Packs)>(
            StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(gamePath) || scan.RivalSheets.Count == 0) return new();
        var dir = Path.Combine(gamePath!, "Mods", OverrideFolder, "assets", "Characters");
        // 覆盖包没生成、或被 Mods 页禁用（点前缀）⇒ 没有"我方钉了哪张"可陈述
        if (!Directory.Exists(dir)) return new();
        var canon = CanonicalNpcId(charId);
        foreach (var file in Directory.GetFiles(dir, "*.png"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var us = name.IndexOf('_');
            // 场合变体（Jas_Winter）与同义词另一侧（SVE_Henchman）都按首段认人
            if (!CanonicalNpcId(us > 0 ? name[..us] : name)
                    .Equals(canon, StringComparison.OrdinalIgnoreCase)) continue;
            var (w, h) = ImgSize(file);
            if (w <= 0 || h <= 0) continue;                        // 量不出就不判，不报"矮 0 行"
            var key = "Characters/" + CanonicalNpcId(name);
            if (!scan.RivalSheets.TryGetValue(key, out var riv)) continue;
            var sameW = riv.Where(r => r.FullSheet && r.W == w).ToList();
            if (sameW.Count == 0) continue;
            var rivalH = sameW.Max(r => r.H);
            if (rivalH <= h) continue;                             // 我们就是最高那张 ⇒ 没话要说
            // 同一个键可能钉了两条（Characters/Wizard 与 Characters/Magnus 是同一个人）：
            // 取更矮的那条，界面才敢用单数口径说"本包覆盖到第 N 行"。
            if (byKey.TryGetValue(key, out var cur) && cur.Asset.OwnRows * 32 <= h) continue;
            byKey[key] = (new BodyCoverageAsset(key, h / 32, rivalH / 32),
                sameW.Where(r => r.H == rivalH).Select(r => r.Pack)
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.Ordinal).ToArray());
        }
        // 展示层合并：我方档数相同、对手也是同几个包 ⇒ 一行（Jas 三条）；否则分行（Wizard 与
        // Wizard_Beach 对手不同包）。每一格的真实差口留在 Assets 里，不为了少写一行而抹平。
        return byKey.Values
            .GroupBy(v => v.Asset.OwnRows + "|" + string.Join("|", v.Packs))
            .Select(g => new BodyCoverageGroup(g.First().Asset.OwnRows, g.First().Packs,
                g.Select(x => x.Asset).OrderBy(x => x.Key, StringComparer.Ordinal).ToArray()))
            .OrderBy(x => x.Assets[0].Key, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// 立绘页那颗「肖像换肤」开关做的事：改名覆盖包（与 Mods 页的禁用同一个机制 ——
    /// 目录名就是唯一真相，不另存标志）。
    /// ⚠ 只有【从没生成过这个包】的那一次才顺手建包 + 全量落盘（用户要的"首次启用自动放进 Mods"）；
    /// 平时切换就是一次改名，毫秒级 —— 以前每次都跑 EnsureOverridePackHealthy，1400 多条补丁 +
    /// 几百张 PNG 的重建全压在点击上，界面要"跳一下状态再跳回来"才结束（2026-09-30 实测抱怨）。
    /// 停用期间的选择不丢（配置照记），重新启用后由启动前自检补齐。
    /// </summary>
    public void SetOverridesEnabled(string gamePath, PortraitScanResult? scan, bool on)
    {
        AppLog.Info("Portraits", on
            ? "[换肤开关] 启用：覆盖包恢复加载，按已选皮肤钉图"
            : "[换肤开关] 停用：覆盖包改名 .前缀，CP 不再加载 ⇒ 游戏完全按 mod 自己的图");
        if (string.IsNullOrWhiteSpace(gamePath)) return;
        var mods = Path.Combine(gamePath, "Mods");
        if (!Directory.Exists(mods)) return;
        var everExisted = false;
        foreach (var name in new[] { OverrideFolder, LegacyOverrideFolder })
        {
            var live = Directory.Exists(Path.Combine(mods, name));
            var off = Directory.Exists(Path.Combine(mods, "." + name));
            if (!live && !off) continue;
            everExisted = true;
            _mods.SetDisabled(gamePath, name, !on);
        }
        if (!on || scan is null || everExisted) return;
        try { EnsureOverridePackHealthy(gamePath, scan); }
        catch (Exception ex) { AppLog.Warn("Portraits", "[换肤开关] 首次生成覆盖包失败: " + ex.Message); }
    }

    /// <summary>覆盖包是否被禁用（目录带 <c>.</c> 前缀，Mods 页那个开关就是改名）。
    /// 从没生成过 ⇒ 不算禁用（返回 false，让自检正常建包）。</summary>
    public static bool OverridePackDisabled(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return false;
        var mods = Path.Combine(gamePath, "Mods");
        if (!Directory.Exists(mods)) return false;
        foreach (var name in new[] { OverrideFolder, LegacyOverrideFolder })
        {
            if (Directory.Exists(Path.Combine(mods, name))) return false;
            if (Directory.Exists(Path.Combine(mods, "." + name))) return true;
        }
        return false;
    }

    /// <summary>
    /// 启动游戏前的最后一道保险：保证覆盖包「有清单、已启用、content.json 可解析且含已选角色，
    /// 且是按当前落盘语义生成的」。任一不满足就全量重建 —— 用户要求：肖像页设置过 +
    /// 覆盖包启用 ⇒ 下次启动必须生效。
    /// onlyWhenFormatStale = true（进肖像页时的对账）：落盘格式没换过就直接返回，
    /// 连清单/启用都不碰 —— 那条路径每次进页都会走，不能变成"每次进页重建 45 秒"。
    /// </summary>
    public void EnsureOverridePackHealthy(string gamePath, PortraitScanResult scan,
        bool onlyWhenFormatStale = false)
    {
        try
        {
            var modsDir = Path.Combine(gamePath, "Mods");
            var root = Path.Combine(modsDir, OverrideFolder);
            // 用户在 Mods 页禁用了覆盖包 = 他要的正是"换肤整体不生效、游戏按 mod 自己的图"。
            // 这条自检（含后面那句"保证已启用"）必须让路，否则禁了会被自动改回来 —— 实测
            // 三条路都会拉回：启动自检、启动弹窗的自动启用、选完皮肤的落盘末尾。
            if (OverridePackDisabled(gamePath))
            {
                AppLog.Info("Portraits", "[覆盖包] 已被禁用（Mods 页）⇒ 自检跳过：不重建、不改回启用");
                return;
            }
            if (onlyWhenFormatStale && ReadOverrideFormat(root) == OverrideFormat.ToString()) return;
            var mf = Path.Combine(root, "manifest.json");
            var cj = Path.Combine(root, "content.json");
            var needFull = false;

            if (!File.Exists(mf) || !File.Exists(cj)) needFull = true;
            else
            {
                try
                {
                    var doc = JObject.Parse(File.ReadAllText(cj));
                    if (doc["Changes"] is not JArray arr || arr.Count == 0) needFull = true;
                    else
                    {
                        // 已选角色必须在补丁里（Target 前缀命中）
                        foreach (var id in _cfg.Current.PortraitSkins.Keys
                                     .Concat(_cfg.Current.PortraitLocks.Keys))
                        {
                            var asset = GameAssetId(id);
                            var hit = arr.Any(x =>
                            {
                                var t = x?["Target"]?.ToString() ?? "";
                                return t.StartsWith("Portraits/" + asset, StringComparison.OrdinalIgnoreCase)
                                    || t.StartsWith("Characters/" + asset, StringComparison.OrdinalIgnoreCase);
                            });
                            if (!hit) { needFull = true; break; }
                        }
                    }
                }
                catch { needFull = true; }
            }

            // 落盘语义换过版本 → 旧 content.json 里按旧规则钉进去的条目必须整体重建掉，
            // 光看"文件在不在、已选角色有没有条目"是查不出来的（v1.7.12 实测：法师那条
            // Characters/Wizard 结构完好，只是按旧语义借了原版身体，永远不自愈）。
            if (!needFull)
            {
                var stamp = ReadOverrideFormat(root);
                if (stamp != OverrideFormat.ToString())
                {
                    AppLog.Warn("Portraits",
                        $"[覆盖包] 落盘格式 {(stamp.Length == 0 ? "(无标记)" : stamp)} → {OverrideFormat}，全量重建");
                    needFull = true;
                }
            }

            if (needFull)
            {
                AppLog.Warn("Portraits", "[覆盖包] 启动前检测到不完整，全量重建");
                WriteOverridePack(gamePath, scan, _cfg.Current.PortraitSkins,
                    _cfg.Current.PortraitVanillaDefaults, onlyIds: null);
            }
            // 无条件保证：清单在。⚠ 不再"保证已启用"—— 被禁用是用户在 Mods 页的明确决定，
            // 以前这里每次启动都把目录名改回来，导致"禁用肖像页"根本禁不掉。
            if (!File.Exists(mf))
                KeepDirTime(root, () => File.WriteAllText(mf,
                    "{\"Name\":\"JuniGrid Portrait Overrides\",\"Author\":\"JuniGrid\",\"Version\":\"1.0.0\"," +
                    "\"Description\":\"Portrait overrides managed by JuniGrid. Regenerated automatically.\"," +
                    "\"UniqueID\":\"" + OverrideUid + "\"," +
                    "\"ContentPackFor\":{\"UniqueID\":\"Pathoschild.ContentPatcher\"}}"));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Portraits", "启动前覆盖包自检失败: " + ex.Message);
        }
    }

    /// <summary>读覆盖包的落盘格式标记；没有或读不出来返回空串（= 当成旧版本，触发重建）。</summary>
    private static string ReadOverrideFormat(string root)
    {
        try
        {
            var fp = Path.Combine(root, OverrideFormatFile);
            return File.Exists(fp) ? File.ReadAllText(fp).Trim() : "";
        }
        catch { return ""; }
    }

    // ══════════════════════ 立绘覆盖包 ══════════════════════

    /// <summary>生成/更新覆盖包：对每个「显式选择」的角色，把对应立绘（和精灵表）拷进
    /// assets 并以最高优先级 EditImage 压过其它启用包的同名补丁（含 Nyapu 这类
    /// 无参 EditImage 整表替换）；显式回默认的角色拷原版立绘。拷贝失败的项直接跳过 ——
    /// 绝不写引用不存在文件的补丁（CP 会报错）。
    /// onlyIds 非空 = 增量：只重建这些角色的条目，其余保留旧 content.json 内容。</summary>
    private void WriteOverridePack(string gamePath, PortraitScanResult scan,
        Dictionary<string, string> skins, List<string> vanillaDefaults,
        IReadOnlyList<string>? onlyIds = null)
    {
        // 覆盖包是我们自己的记账包：它的时间戳变了同样会让 Mods 列表重排（默认按目录时间排）
        var ovDir = Path.Combine(gamePath, "Mods", OverrideFolder);
        DateTime? ovBefore = null;
        try { if (Directory.Exists(ovDir)) ovBefore = Directory.GetLastWriteTime(ovDir); } catch { }
        try
        {
            var modsDir = Path.Combine(gamePath, "Mods");
            var root = Path.Combine(modsDir, OverrideFolder);
            var assets = Path.Combine(root, "assets");
            Directory.CreateDirectory(assets);
            var vanillaSet = new HashSet<string>(vanillaDefaults, StringComparer.OrdinalIgnoreCase);

            // manifest 必须每次都在 —— 旧逻辑「只在全量时写一次」，换肤走增量 never 创建，
            // SMAPI 直接 Skipped（no manifest.json），Mods 页显示「无清单」，肖像全体失效。
            var mf = Path.Combine(root, "manifest.json");
            if (!File.Exists(mf))
                File.WriteAllText(mf,
                    "{\"Name\":\"JuniGrid Portrait Overrides\",\"Author\":\"JuniGrid\",\"Version\":\"1.0.0\"," +
                    "\"Description\":\"Portrait overrides managed by JuniGrid. Regenerated automatically.\"," +
                    "\"UniqueID\":\"" + OverrideUid + "\"," +
                    "\"ContentPackFor\":{\"UniqueID\":\"Pathoschild.ContentPatcher\"}}");

            // 历史遗留清理只在全量时做（增量热路径不碰回收站）
            if (onlyIds is null)
            {
                foreach (var legacyName in new[] { LegacyOverrideFolder, "." + LegacyOverrideFolder })
                {
                    var legacy = Path.Combine(modsDir, legacyName);
                    if (!Directory.Exists(legacy)) continue;
                    try
                    {
                        ModService.StageExistingToTrash(gamePath, legacy);
                        AppLog.Warn("Portraits", $"[覆盖包] 已回收历史遗留目录 {legacyName}");
                    }
                    catch (Exception lex)
                    {
                        AppLog.Warn("Portraits", $"[覆盖包] 清理遗留目录失败 {legacyName}: {lex.Message}");
                    }
                }
            }

            // v1.3.9b：不再「整目录删除重建」—— 游戏运行时 SMAPI/CP 占着覆盖包文件句柄，
            // Directory.Delete 直接抛 IOException 被 catch 吞掉，覆盖包从此再也不更新，
            // 用户当天所有换肤全部无效（实测大坑）。改为：逐文件覆盖写入（游戏运行时
            // 大多数文件可覆盖），删除残留文件失败只记日志不影响其它文件。
            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // 尺寸闸要用：某资产名在各包里登记的原生图（Characters/Victor_Beach 等）。
            var natByAsset = scan.VariantAssets
                .GroupBy(v => v.Kind + "/" + v.VariantId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(v => v.File).ToList(),
                    StringComparer.OrdinalIgnoreCase);
            // 这条走路表资产在游戏里最终有多高 = 同源（同宽）各家图里最高的那张。
            // 实机证明（2026-09-29 双补丁同资产探针）：CP 先应用所有 Load、再应用 EditImage，
            // Priority 压不住阶段 ⇒ 矮表盖不满高画布（下面几行仍是别家），而"整张换(Load)"救不了
            // （编辑后跑）；CP 也没有任何字段能把画布改小（DLL 字段清单无 ToDimension/ToScale）。
            // ⇒ 想保住"用户选的那包的身子"，只剩把我们那张【按行等比拉到画布高】这一条路。
            int BodyCanvas(string kind, string id, string? ourFile, List<string>? extra)
            {
                var (ow, oh) = ImgSize(ourFile);
                if (ow <= 0 || oh <= 0) return 0;
                var best = oh;
                void Scan(IEnumerable<string>? files)
                {
                    if (files is null) return;
                    foreach (var f in files)
                    {
                        var (w2, h2) = ImgSize(f);
                        if (w2 == ow && h2 > best) best = h2;
                    }
                }
                Scan(natByAsset.TryGetValue(kind + "/" + id, out var nat) ? nat : null);
                Scan(extra);
                return best;
            }
            Action<string, byte[]> WriteFile = (dest, bytes) =>
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.WriteAllBytes(dest, bytes);
                    expected.Add(Path.GetFullPath(dest).ToUpperInvariant());
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Portraits", $"[覆盖包] 写入 {Path.GetFileName(dest)} 失败（游戏占用？）: {ex.Message}");
                }
            };

            var changes = new List<object>();
            // v1.6.8：目标去重 —— 同一资产多条补丁在部分 CP 版本会触发 Exclusive 冲突
            //（两条 Load 抢同一素材 = 都不生效，Emily_Summer #1/#2 实测）。
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ch in scan.Characters)
            {
                // 增量模式：只处理本次变更的角色（含合并卡隐藏成员）
                if (onlyIds is not null
                    && !onlyIds.Contains(ch.Id, StringComparer.OrdinalIgnoreCase)
                    && !ch.Members.Any(m => onlyIds.Contains(m, StringComparer.OrdinalIgnoreCase))
                    && !(ch.AliasOf is { Length: > 0 } ao && onlyIds.Contains(ao, StringComparer.OrdinalIgnoreCase)))
                    continue;
                string? sel = null, portraitSrc = null, spriteSrc = null;
                PortraitSkinOption? selectedOpt = null;
                Dictionary<string, string>? selectedSeasonFiles = null;
                // 钉住的那具身子【属于哪个包】——四季表必须按它查，不能拿皮肤包名去查
                //（身体链会走到前置/默认行，那张身子不在皮肤包里）。
                string? bodyPack = null;
                var vanillaDefault = false;
                // 这个角色所有卡自带的走路表 = 基资产那条的「画布高」候选清单。
                var bodySheets = BodySheetsOf(ch);
                // v1.7 锁定：四季一律钉锁定瞬间那张图，跳过季节变体轮换
                var lockInfo = GetLock(_cfg.Current, ch.Id);
                // v1.7.29：这个角色选中的是 HD 肖像通道的包（只写 Mods/HDPortraits/<角色>、
                // 不碰 Portraits/ 的那种，例：[CP] Dacar Rasmodia Portraits）⇒ 脸改走另一条路：
                // 把那条数据资产的 Portrait 指回【本包自己的高清资产】，让 CP 在运行时按它自己的
                // 条件（帽子/花舞节/海滩…）去解析那张表。
                // ⚠ 不能当普通皮肤钉：512 宽的表做 EditImage Replace 到 128 宽的目标上，CP 直接
                // 拒绝（"target area extends past the right edge of the image"）⇒ 一条都生效不了。
                // 但【只掐脸这一侧】：身体、四季/场合的走路表仍按普通皮肤那条链钉 ——
                // 早先这里直接 continue ⇒ 法师身上只剩 2 条补丁，小人掉回 RomRas 的绿色女巫身子
                //（2026-09-29 用户实测"大头照对了精灵图又不对"，和之前那批 NPC 同一个症状）。
                // 锁定中的角色不走这条：锁定的语义是"四季一律钉这一张图"，那是 Portraits/ 通道。
                var hdFaceOnly = false;
                if (lockInfo is null && scan.HdPortraitRendererInstalled
                    && (skins.TryGetValue(ch.Id, out var hdSelPack)
                        || ch.Members.Any(m => skins.TryGetValue(m, out hdSelPack)))
                    && !string.IsNullOrEmpty(hdSelPack))
                {
                    var hdMine = scan.HdSkins
                        .Where(h => string.Equals(h.Pack, hdSelPack, StringComparison.OrdinalIgnoreCase))
                        .GroupBy(h => h.DataAsset, StringComparer.OrdinalIgnoreCase).ToList();
                    if (hdMine.Count > 0)
                    {
                        hdFaceOnly = true;
                        foreach (var g in hdMine)
                        {
                            var first = g.First();
                            changes.Add(new
                            {
                                Action = "EditData",
                                Target = g.Key,
                                Entries = new Dictionary<string, object>
                                {
                                    ["Portrait"] = first.PortraitAsset,
                                    ["Size"] = first.Cell,
                                },
                            });
                            written.Add(g.Key);
                        }
                        AppLog.Info("Portraits", $"[HD肖像通道] {ch.Id} 选的是高清表（{hdSelPack}）" +
                            $"⇒ {hdMine.Count} 条 Portrait 指回本包资产；脸不钉 Portraits/，身体照钉");
                    }
                }
                if (lockInfo is not null)
                {
                    sel = lockInfo.PackFolder.Length == 0 ? null : lockInfo.PackFolder;
                    vanillaDefault = lockInfo.PackFolder.Length == 0;
                    // PinFile 优先（锁定时预览的那张，含夏季变体）；失效则回落包默认/原版
                    if (lockInfo.PinFile is not null && File.Exists(lockInfo.PinFile))
                    {
                        portraitSrc = lockInfo.PinFile;
                        if (sel is not null)
                        {
                            var lopt = MatchOption(ch.AllOptions, sel,
                                GetMemberVariant(_cfg.Current, ch, null), ch.Id);
                            spriteSrc = lopt?.SpriteFile;
                            // 精灵必须钉到与锁定季一致的那张（Sam_Winter 等）。
                            // 用 GetSeasonFilesForChar：支持每角色文件夹与 <id>_Winter 命名
                            //（旧的 GetSeasonFiles 只认源文件同名 stem，精灵找不到冬装 → 锁冬像仍是春精灵）
                            if (spriteSrc is not null && lockInfo.Season is not null)
                            {
                                var seasons = GetSeasonFilesForChar(spriteSrc, ch.Id);
                                if (seasons.TryGetValue(lockInfo.Season, out var ls))
                                    spriteSrc = ls;
                            }
                            // 精灵没季变体时，再看 PinFile 同目录有没有 <id>_Winter 等精灵
                            if (lockInfo.Season is not null
                                && (spriteSrc is null || !GetSeasonFilesForChar(spriteSrc, ch.Id).ContainsKey(lockInfo.Season)))
                            {
                                var nearPin = GetSeasonFilesForChar(lockInfo.PinFile, ch.Id);
                                if (nearPin.TryGetValue(lockInfo.Season, out var np)
                                    && !string.Equals(np, lockInfo.PinFile, StringComparison.OrdinalIgnoreCase))
                                {
                                    // 同目录的才是精灵表（宽≠立绘 64）；拿它当精灵
                                    spriteSrc = np;
                                }
                            }
                            // v1.7.16：锁定 = 只锁头；身子仍要像"未锁定"那样解析。若这个包【自己没有走路图】
                            // （如 Donut's Olivia 只画脸），就沿身体链补齐：前置包借走路图
                            // （Donut→Seasonal Cute Characters SVE），整条前置链都没有就用默认行那张，
                            // 并按锁定的那一季取对应那张。
                            // ⚠ 只在"自己没身子"时才借 ⇒ 自带走路图的包（艾米丽/Baechu）走不到这里、完全不受影响。
                            // 未锁定链下方 sel 分支已用同一个 ResolveBodySprite；这里补齐，锁定与未锁定/预览同口径。
                            if (spriteSrc is null || !LooksLikeSprite(spriteSrc))
                            {
                                var preSpr = ResolveBody(gamePath, scan, ch, lopt).File;
                                if (preSpr is { Length: > 0 } && LooksLikeSprite(preSpr))
                                {
                                    spriteSrc = preSpr;
                                    if (lockInfo.Season is not null)
                                    {
                                        var preSeasons = GetSeasonFilesForChar(spriteSrc, ch.Id);
                                        if (preSeasons.TryGetValue(lockInfo.Season, out var preSpr2))
                                            spriteSrc = preSpr2;
                                    }
                                }
                            }
                        }
                        else
                        {
                            spriteSrc = ch.IsVanilla ? VanillaSpriteXnb(gamePath, ch.Id) : ch.Native?.SpriteFile;
                            // 原版锁定也要跟季：Characters/Sam.xnb → Characters/Sam_Winter.xnb
                            //（本体就有这套 xnb；旧逻辑钉基础表 → 大头照冬装、精灵仍随季变）
                            if (spriteSrc is not null && lockInfo.Season is not null)
                            {
                                var vs = GetSeasonFilesForChar(spriteSrc, ch.Id);
                                if (vs.TryGetValue(lockInfo.Season, out var wl))
                                    spriteSrc = wl;
                            }
                        }
                    }
                    else if (sel is not null)
                    {
                        // v1.7.7：锁定的是多画风包里的某一张 → 按锁定时那条选择记录的画风取，
                        // 没记画风（旧配置/非画风包）时与旧写法逐字等价
                        var lopt = MatchOption(ch.AllOptions, sel,
                            GetMemberVariant(_cfg.Current, ch, null), ch.Id);
                        portraitSrc = lopt?.SourceFile;
                        spriteSrc = lopt?.SpriteFile;
                        if (spriteSrc is not null && lockInfo.Season is not null)
                        {
                            var ls = GetSeasonFilesForChar(spriteSrc, ch.Id);
                            if (ls.TryGetValue(lockInfo.Season, out var sw)) spriteSrc = sw;
                        }
                    }
                    else
                    {
                        portraitSrc = ch.IsVanilla ? VanillaPortraitXnb(gamePath, ch.Id) : ch.Native?.SourceFile;
                        spriteSrc = ch.IsVanilla ? VanillaSpriteXnb(gamePath, ch.Id) : ch.Native?.SpriteFile;
                        if (spriteSrc is not null && lockInfo.Season is not null)
                        {
                            var ls = GetSeasonFilesForChar(spriteSrc, ch.Id);
                            if (ls.TryGetValue(lockInfo.Season, out var sw)) spriteSrc = sw;
                        }
                    }
                    // 锁定路径：不收集季节变体（selectedSeasonFiles 保持 null → 单文件钉入）
                }
                else if (skins.TryGetValue(ch.Id, out var s)
                    && !s.StartsWith("Portraiture/", StringComparison.OrdinalIgnoreCase))
                {
                    sel = s;
                    // v1.7.7：这个人在配置里记了画风 → 用对应画风那张卡；没记（老配置、
                    // 或者这个包根本没拆画风）时 MatchOption 就是旧的第一条命中，逐字等价。
                    var wantVar = GetMemberVariant(_cfg.Current, ch, null);
                    var opt = MatchOption(ch.AllOptions, sel, wantVar, ch.Id);
                    // v1.7.10：画风是在本尊那张卡上拆出来的（包只声明 Portraits/Wizard），
                    // 合并成员（Magnus）自己那几行里根本没有这个画风 ⇒ 只判 opt is null 不够，
                    // 必须连"画风没落成"也去借本尊的行 —— 否则 Wizard 换了脸、Magnus 钉成
                    // 该包第一条，同一个人的两份 NPC 数据各显示一张（实测 [画风失效] 日志）。
                    var needBorrow = opt is null
                        || (wantVar is { Length: > 0 } && !string.Equals(opt.Variant, wantVar,
                            StringComparison.OrdinalIgnoreCase));
                    if (needBorrow && ch.AliasOf is { Length: > 0 } ownId)
                    {
                        // 合并卡的成员自己没这个包的行（那张皮肤是本尊独有的）：借本尊那一行。
                        // 不借就等于 SelectSkin 把选择写进了它名下却解析不出文件 ⇒
                        // 游戏里本尊换脸、替身没换 = 半生效。
                        var own = scan.Characters.FirstOrDefault(c => string.Equals(
                            c.Id, ownId, StringComparison.OrdinalIgnoreCase));
                        var borrowed = MatchOption(own?.AllOptions ?? Enumerable.Empty<PortraitSkinOption>(), sel,
                            GetMemberVariant(_cfg.Current, own ?? ch, null), ownId);
                        if (borrowed is not null && (!needBorrow || borrowed.Variant is { Length: > 0 }))
                            opt = borrowed;
                    }
                    if (opt is not null)
                    {
                        selectedOpt = opt;
                        // HD 通道：脸这一侧没有可用的 128 宽图（那张是 512 高清表），
                        // 来源一律置空 ⇒ 后面所有 Portraits/ 的钉法自然跳过，改由
                        // Mods/HDPortraits/<角色> 那条 EditData 供图。
                        portraitSrc = hdFaceOnly ? null : opt.SourceFile;
                        // v1.7.29：身体出处 = 皮肤自己有走路图就是它自己的包；没有则交给下面的
                        // 身体链，链上每一档【连它自己的包目录】一起给 —— 四季表要按"那张身子
                        // 所属的包"去查，拿皮肤包名去查前置/默认行的表必然查不到（v1.7.15 教训：
                        // 卡罗琳/奥莉薇亚那种四季文件不带角色名的包，查不到就被压成同一张）。
                        // ⚠ 比底图矮【不换人】：换人=用户看到的不是他选的那包（2026-09-29 用户报
                        // "奥莉薇亚该用前置包的走路图，你用了默认的；法师大头照对、走路图还是不对"）。
                        // 矮的问题由落盘端改整张换（Load）解决，见 BodyAction。
                        spriteSrc = LooksLikeSprite(opt.SpriteFile)
                                    && !IsFakeWalkSheet(opt.SpriteFile, opt.SourceFile, ch.Id)
                            ? opt.SpriteFile
                            : null;
                        bodyPack = spriteSrc is not null ? opt.PackFolder : null;
                        if (spriteSrc is null)
                        {
                            // 再兜一层：从【选中包自己】的目录里按角色名/别名搜精灵表
                            //（RRRR 这类性转包把精灵图直铺在 Characters/ 下时实测能救回来）。
                            // 搜到的仍是这个包自己的身体，不算借别家。
                            var packDir = ResolvePackDir(modsDir, opt.PackFolder ?? "");
                            var found = packDir is null ? null : FindCharacterAsset(packDir, "Characters", ch.Id);
                            if (found is not null && LooksLikeSprite(found) && !IsNonWalkSprite(found)
                                && !IsFakeWalkSheet(found, opt.SourceFile, ch.Id))
                            { spriteSrc = found; bodyPack = opt.PackFolder; }
                        }
                        if (spriteSrc is null)
                        {
                            // v1.7.13→v1.7.29：身体链（用户拍板）——自己的精灵 → 祖先前置链的精灵
                            // → 整条链都没有就用【默认行】的精灵。
                            // RomRas 这类前置虽然无条件写 Characters/Wizard，但 SMAPI 同优先级
                            // 按加载序生效 —— 不钉进覆盖包就可能被字母序更后的别家包顶掉。
                            var (bodyFile, bodyFolder) = ResolveBody(gamePath, scan, ch, opt);
                            spriteSrc = bodyFile;
                            bodyPack = bodyFolder;
                        }
                        if (spriteSrc is not null && !LooksLikeSprite(spriteSrc))
                            spriteSrc = null;
                        // v1.6.8：按角色 id 收集季节文件（支持每角色文件夹布局与冬季
                        // Indoor/Outdoor 拆分 —— Baechu 形态）
                        // ⚠ HD 通道（hdFaceOnly）时【不收集】：opt.SourceFile 是 512 宽的高清表，
                        // 拿它当 Portraits/<角色> 的四季来源钉上去，CP 会按尺寸整条拒绝。
                        if (!hdFaceOnly)
                        {
                            selectedSeasonFiles = SeasonFilesFor(scan, opt.SourceFile, ch.Id,
                                "Portraits", opt.PackFolder);
                            // v1.3.9b：季节包的 SourceFile 常是「4×2 四季总表」（Baechu 的
                            // Emily.png），钉进覆盖包后游戏里四季都显示总表第一帧 —— 和弹窗
                            // 卡片显示的春装图对不上，被用户误认成别的包（实测）。基础像改用
                            // 卡片显示的那张（春季文件），冬季另有 Emily_Winter 变体钉住。
                            if (GetSeasonFiles(opt.SourceFile).TryGetValue("spring", out var springP))
                                portraitSrc = springP;
                        }
                    }
                }
                else if (vanillaSet.Contains(ch.Id))
                {
                    // 显式回默认：拷「默认行」当前来源 —— 原版角色若被扩展包增强
                    // （马龙/法师/冈瑟…），默认行指向扩展包文件（=游戏实际显示）；
                    // 卸载扩展包后重新扫描，默认行回落原版 xnb，恢复原版肖像。
                    // mod 角色拷娘家默认外观。
                    // v1.7「一键恢复默认」：PortraitTrueVanilla 里的角色强制走原版 xnb
                    //（用户拍板：冈瑟这种原版/SVE 双默认的统一用原版）。
                    vanillaDefault = true;
                    var forceTrue = _cfg.Current.PortraitTrueVanilla
                        .Contains(ch.Id, StringComparer.OrdinalIgnoreCase);
                    if (forceTrue && ch.IsVanilla)
                    {
                        portraitSrc = VanillaPortraitXnb(gamePath, ch.Id);
                        spriteSrc = VanillaSpriteXnb(gamePath, ch.Id);
                    }
                    else
                    {
                        portraitSrc = ch.Vanilla?.SourceFile
                            ?? (ch.IsVanilla ? VanillaPortraitXnb(gamePath, ch.Id) : ch.Native?.SourceFile);
                        spriteSrc = ch.Vanilla?.SpriteFile
                            ?? (ch.IsVanilla ? VanillaSpriteXnb(gamePath, ch.Id) : ch.Native?.SpriteFile);
                    }
                }
                // ⚠ hdFaceOnly 不算"没脸"：HD 通道的脸由 Mods/HDPortraits/<角色> 那条 EditData 供，
                // Portraits/ 这一侧本来就没有（也不该有）来源。把这条闸门当成"没脸"就会连
                // 身体一起跳过 ⇒ 选了高清脸的 NPC 走路小人掉回别家 mod（法师实测）。
                if (portraitSrc is null && !hdFaceOnly)
                {
                    // v1.7.24：全局那一档钉得了解释不了按季。skins 里的 Portraiture 素材没有
                    // CP 通道（Portraiture 在绘制期贴图），老写法一句 continue 把【整个角色】跳过 ⇒
                    // 用户给艾米丽四季各挑了一个 CP 包，覆盖包一条补丁都没有，游戏里四季全凭
                    // 别人家的栈（2026-09-28 用户报"艾米丽不会随季节变化了"）。
                    // 这里只跳过"全局/锁定"那两段，下面的按季段照常落盘。
                    if (!_cfg.Current.PortraitSeasonSkins.TryGetValue(ch.Id, out var ssProbe)
                        || string.IsNullOrWhiteSpace(ssProbe)) continue;
                }

                // v1.6.8：季节包（来源文件同目录/角色文件夹里有 <id>_<季>.png 变体，如
                // Seasonal Baechu）逐季钉入 + Season 条件 —— 静态钉"春季文件"会让游戏里
                // 四季都显示同一张图（实测：Baechu 冬季=春季）。非季节包维持单文件钉入。
                // 游戏真实资产名（Leo → ParrotBoy）—— Target 必须用它，用角色 id 会打空
                var assetId = GameAssetId(ch.Id);
                // v1.7.27：立绘底图有多高？取这个 NPC 所有卡里最高的那张立绘作下限。
                // 覆盖包钉得比底图矮 ⇒ 多出来的行仍是别的包的画（法师第 5 行永远是 RomRas 的脸）。
                // 用"所有卡"而不是"选中那张"：底图是谁提供的我们说了不算，只能保证盖满最坏情况。
                var faceSizes = new List<(int w, int h)>();
                foreach (var o in ch.AllOptions)
                    if (o.SourceFile is { Length: > 0 } osf && File.Exists(osf))
                    {
                        var (ow, oh) = ImgSize(osf);
                        if (ow > 0 && oh > 0) faceSizes.Add((ow, oh));
                    }
                // v1.7.20：按文件名找不到季节图时，改用包自己声明的「基资产 + When:{Season}」映射。
                // Caroline (Overhaul) 的四季文件就叫 Spring/Summer/Fall/Caroline.png（不带角色名），
                // 光看文件名认不出来 ⇒ 以前退化成"静态钉一张 + Late+10"，把包自己的四季全压成
                // 同一张（=用户最初报的"春夏秋冬都是裸体"）。
                if (selectedSeasonFiles is not { Count: > 0 } && !hdFaceOnly)
                    selectedSeasonFiles = PackSeasonFiles(scan, selectedOpt?.PackFolder,
                        "Portraits", assetId, ch.Id);
                if (selectedOpt is not null && !hdFaceOnly
                    && selectedSeasonFiles is { Count: > 0 })
                {
                    PinOverrideAsset(changes, written, assets, "Portraits", assetId,
                        selectedOpt.SourceFile, selectedSeasonFiles, faceSizes);
                    if (spriteSrc is not null)
                    {
                        // 四季表按【身子自己的包】查：身体链走到前置/默认行时，那张身子并不在
                        // 皮肤包里，拿皮肤包名查就查不到表 ⇒ 四季被压成同一张（奥莉薇亚实测）。
                        PinOverrideAsset(changes, written, assets, "Characters", assetId,
                            spriteSrc, SeasonFilesFor(scan, spriteSrc, ch.Id,
                                "Characters", bodyPack ?? selectedOpt.PackFolder),
                            bodyCanvas: f => BodyCanvas("Characters", assetId, f, bodySheets));
                    }
                }
                else if (hdFaceOnly && selectedOpt is not null && spriteSrc is not null)
                {
                    // HD 通道：脸由上面那条 EditData（Mods/HDPortraits/<角色> → 本包高清资产）供，
                    // 这里【只钉身体】—— 四季/场合的走路表照普通皮肤同一条链走。
                    // 少了这一段就等于"选了高清脸，小人掉回别家 mod 的身子"（法师实测）。
                    PinOverrideAsset(changes, written, assets, "Characters", assetId,
                            spriteSrc, SeasonFilesFor(scan, spriteSrc, ch.Id,
                                "Characters", bodyPack ?? selectedOpt.PackFolder),
                            bodyCanvas: f => BodyCanvas("Characters", assetId, f, bodySheets));
                }
                else if (portraitSrc is not null
                         && written.Add("Portraits/" + assetId)
                         && CopyPortraitFull(portraitSrc,
                             Path.Combine(assets, "Portraits", assetId + ".png"), faceSizes))
                {
                    // v1.6.8：目标一律 EditImage+Replace，不用 Load —— 2026-09-29 实机证明
                    // CP 先跑完所有 Load 再跑 EditImage（Priority 压不住阶段），所以 Load 既
                    // 抢不过别人的编辑、也保不住自己的像素；EditImage 才是"最后一笔"。
                    // 矮于画布的问题改由 CopyBody 纵向拉伸铺满解决（见其注释）。
                    changes.Add(new { Action = "EditImage", Target = "Portraits/" + assetId,
                        FromFile = "assets/Portraits/" + assetId + ".png", PatchMode = "Replace" });
                    if (spriteSrc is not null && LooksLikeSprite(spriteSrc)
                        && CopyBody(spriteSrc, Path.Combine(assets, "Characters", assetId + ".png"),
                            BodyCanvas("Characters", assetId, spriteSrc, bodySheets))
                        && written.Add("Characters/" + assetId))
                    {
                        changes.Add(new { Action = "EditImage", Target = "Characters/" + assetId,
                            FromFile = "assets/Characters/" + assetId + ".png", PatchMode = "Replace" });
                    }
                }

                // 变体资产整族钉住（季节外观等）：Baechu 法师走 1.6 Appearance 引用
                // Portraits/Wizard_Spring 等变体资产，而它的变体 Load 被"装 SVE 不生效"
                // 门槛挡掉 → 不补齐，游戏按季节解析外观就指向缺失 → 立绘空白（实机）。
                // 选中皮肤 → 用该包登记的变体文件；显式默认 → 全部钉到原版，不让引用悬空。
                // v1.7 锁定中：变体也全部钉到 PinFile（同一张图），Appearance 按季切换时
                // 看到的仍是锁定图 —— 这就是「不管什么季节都是这个肖像」的落盘点。
                IEnumerable<(string Kind, string VariantId, string? File)> variants;
                if (lockInfo is not null)
                {
                    bool hasPin = lockInfo.PinFile is not null && File.Exists(lockInfo.PinFile);
                    if (lockInfo.PackFolder.Length == 0)
                    {
                        variants = scan.VariantAssets
                            .Where(v => string.Equals(v.BaseId, ch.Id, StringComparison.OrdinalIgnoreCase))
                            .GroupBy(v => (v.Kind, v.VariantId))
                            .Select(g =>
                            {
                                string? file = hasPin
                                    ? lockInfo.PinFile
                                    : ResolveSeasonalVariantFile(
                                        g.Key.Kind == "Portraits" ? portraitSrc : spriteSrc, g.Key.VariantId, ch.Id);
                                return (g.Key.Kind, g.Key.VariantId, file);
                            });
                    }
                    else
                    {
                        // Characters 变体（Sam_Winter 等）必须用「锁定季精灵」：
                        // 旧逻辑一律拷基础精灵表 → 冬天 Appearance 切到
                        // Characters/Sam_Winter 仍是春装（实机山姆）
                        variants = scan.VariantAssets
                            .Where(v => string.Equals(v.BaseId, ch.Id, StringComparison.OrdinalIgnoreCase))
                            .Select(v =>
                            {
                                string? file;
                                if (v.Kind == "Portraits")
                                    file = hasPin ? lockInfo.PinFile : portraitSrc;
                                else
                                    file = spriteSrc ?? ResolveSeasonalVariantFile(v.File, v.VariantId, ch.Id);
                                return (v.Kind, v.VariantId, file);
                            });
                    }
                }
                else if (sel is not null)
                {
                    // v1.7.1：Appearance 引用的是「变体资产」（Portraits/Sophia_Spring 等独立
                    // 资产），不是基础像 + Season 条件。旧写法只钉「所选包自己登记过的」变体 ——
                    // 所选包若走 Season-on-base（Donut's 动漫包）就没登记变体，游戏仍去加载
                    // 别的包 Load 的 Portraits/Sophia_Spring → 春秋显示的不是所选包（实测）。
                    // 改为：拿全角色的变体资产名，文件优先用所选包的同名变体，拿不到再按季
                    // 从所选包的基础像解析，保底钉基础像不让引用悬空。
                    // v1.7.7：选中多画风包里的某一张时，变体资产也必须在**同一个画风目录**里找。
                    // RecordVariant 当年只登记了兜底命中的那套（字母序第一个），
                    // 不按目录筛就会把「卡片显示 Donut、游戏里 Alesia_Spring 仍是 Dawn」
                    // 这种半生效留下（实测：覆盖包钉的是 Dawn 那张，sha 9b5b4746…）。
                    // 画风目录里没有同名变体时，file 落到 ResolveSeasonalVariantFile(基础像)
                    // —— 基础像本身就是该画风的文件，按季解析出来的仍是这套画。
                    var styleOn = selectedOpt?.Variant is { Length: > 0 };
                    // 所选包若用「基资产 + When:{Season}」表达四季（卡罗琳 Overhaul），
                    // 变体资产也要从这张表里取对应季的那张，不能整族钉成封面。
                    var faceSeasons = SeasonFilesFor(scan, portraitSrc, ch.Id, "Portraits", sel);
                    // 身子可能来自身体链（前置包/默认行），那张身子不在皮肤包里 ⇒ 四季表必须按
                    // 【身子自己的包】查，拿皮肤包名查不到表就会整族钉成同一具（奥莉薇亚实测）。
                    var bodySeasons = spriteSrc is not null
                        ? SeasonFilesFor(scan, spriteSrc, ch.Id, "Characters", bodyPack ?? sel) : null;
                    variants = scan.VariantAssets
                        .Where(v => string.Equals(v.BaseId, ch.Id, StringComparison.OrdinalIgnoreCase))
                        .GroupBy(v => (v.Kind, v.VariantId))
                        .Select(g =>
                        {
                            var anchor = g.Key.Kind == "Portraits" ? portraitSrc : spriteSrc;
                            var styleDir = styleOn ? Path.GetDirectoryName(anchor) : null;
                            var own = g.FirstOrDefault(v =>
                                string.Equals(v.Pack, sel, StringComparison.OrdinalIgnoreCase)
                                && (styleDir is null
                                    || string.Equals(Path.GetDirectoryName(v.File), styleDir,
                                         StringComparison.OrdinalIgnoreCase)));
                            string? file = own.File is { Length: > 0 } && File.Exists(own.File) ? own.File : null;
                            file ??= ResolveSeasonalVariantFile(anchor, g.Key.VariantId, ch.Id,
                                g.Key.Kind == "Portraits" ? faceSeasons : bodySeasons);
                            return (g.Key.Kind, g.Key.VariantId, File: file);
                        });
                }
                else if (vanillaDefault)
                {
                    // 显式默认：季节变体（Emily_Winter / Emily_Winter_Indoor）必须钉
                    // 该季的原版文件，不能整族钉成基础肖像 —— Baechu 的 Appearance
                    // 会引用 Emily_Winter_Indoor，钉成春装后冬天就永远不换冬衣（Emily 实测）
                    variants = scan.VariantAssets
                        .Where(v => string.Equals(v.BaseId, ch.Id, StringComparison.OrdinalIgnoreCase))
                        .GroupBy(v => (v.Kind, v.VariantId))
                        .Select(g =>
                        {
                            string? file = ResolveSeasonalVariantFile(
                                g.Key.Kind == "Portraits" ? portraitSrc : spriteSrc, g.Key.VariantId, ch.Id);
                            return (g.Key.Kind, g.Key.VariantId, file);
                        });
                }
                else
                {
                    variants = Enumerable.Empty<(string Kind, string VariantId, string? File)>();
                }
                foreach (var v in variants)
                {
                    if (v.File is null) continue;
                    // Characters/ 变体也必须是精灵表，头像图钉上去 CP 直接报错
                    if (v.Kind.Equals("Characters", StringComparison.OrdinalIgnoreCase)
                        && !LooksLikeSprite(v.File))
                        continue;
                    var keyV = v.Kind + "/" + v.VariantId;
                    var natV2 = natByAsset.TryGetValue(keyV, out var natVi) ? natVi : null;
                    var destV = Path.Combine(assets, v.Kind, v.VariantId + ".png");
                    // v1.7.26：立绘场合的替身比本尊小 ⇒ 【补白到本尊尺寸再钉】。
                    // 旧写法直接跳过 ⇒ 那条场合资产没人钉 ⇒ 游戏在那个场合读的是别家的图，
                    // 而覆盖包是 Late+100 的整张替换，用户看到的是"我选了 A 四季全是 B"
                    //（2026-09-28 实机：法师选 Seasonal Rasmodia，Portraits/Wizard_Spring…Winter
                    // 四条全被尺寸闸丢掉 ⇒ 游戏里永远是 Donut 的女法师脸，怎么换都没用）。
                    var okV = v.Kind.Equals("Portraits", StringComparison.OrdinalIgnoreCase)
                        ? (SubstituteNeedsPad(natV2, v.File, out var pw, out var ph)
                            ? CopyAsPngPadded(v.File, destV, pw, ph) : CopyAsPng(v.File, destV))
                        // 走路表场合资产：替身矮 ⇒ 【纵向拉伸铺满】照钉（2026-09-29 实测：法师冬天
                        // 读的是 Characters/Magnus_Winter，旧写法嫌 Baechu 的 192 比本尊 480 矮就
                        // 整条不钉 ⇒ 游戏里永远是 SCC-SVE 的紫发女巫，用户报"精灵图还是错的"）。
                        // 归属不在这里判：file 已经是上面按"所选包 → 身体链那包 → 本包同名变体"
                        // 解析出来的结果，这里只负责"尺寸不合就拉伸钉上"。
                        : CopyBody(v.File, destV, BodyCanvas(v.Kind, v.VariantId, v.File, null));
                    if (okV && written.Add(keyV))
                    {
                        changes.Add(new { Action = "EditImage", Target = keyV,
                            FromFile = $"assets/{v.Kind}/{v.VariantId}.png", PatchMode = "Replace" });
                    }
                }

                // v1.3.9：每季节独立皮肤 —— 用户给某季指定了包 → 把该季变体资产钉成
                // 那个包的对应文件（借 scan.VariantAssets 拿到 mod 用的确切变体资产名）。
                // v1.7 锁定中跳过：四季统一 PinFile，不再按季覆盖。
                // v1.2.1：必须【强制】覆盖 —— 全局包已把 Sam_Winter 钉进 written，
                // 旧逻辑 written.Add 失败就跳过，单季指定永远不生效（实机）。
                if (lockInfo is null && _cfg.Current.PortraitSeasonSkins.TryGetValue(ch.Id, out var ssRaw))
                {
                    var ss = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var seg in ssRaw.Split('␟'))
                    {
                        var i = seg.IndexOf(':');
                        if (i > 0) ss[seg[..i]] = seg[(i + 1)..];
                    }
                    foreach (var (season, pack) in ss)
                    {
                        var cap = char.ToUpperInvariant(season[0]) + season[1..].ToLowerInvariant();
                        // pack 为空串 = 原版默认行（用户可把某一季固定回原版样）
                        // v1.7.7：该季独立选过的画风走 "角色 id␟季节" 那条记录，没记 = 第一条
                        var opt = pack is { Length: 0 }
                            ? ch.Vanilla ?? ch.Native
                            : MatchOption(ch.AllOptions, pack,
                                GetMemberVariant(_cfg.Current, ch, season), ch.Id);
                        if (opt?.SourceFile is null) continue;
                        // 必须 GetSeasonFilesForChar：支持 <id>_Winter / 每角色文件夹
                        var seasonP = GetSeasonFilesForChar(opt.SourceFile, ch.Id).TryGetValue(season, out var sp)
                            ? sp : opt.SourceFile;
                        var seasonS = opt.SpriteFile is not null
                            ? GetSeasonFilesForChar(opt.SpriteFile, ch.Id).TryGetValue(season, out var ss2) ? ss2 : opt.SpriteFile
                            : null;
                        // v1.7.16→v1.7.29：本季选的包【没有走路图】时，走与全局路径同一条身体链：
                        // 祖先前置链的精灵 → 都没有就用默认行那张，再按本季取对应的季节文件。
                        // （v1.7.24 曾把"默认行"这一档从这里删掉、让 mod 栈自己决定 —— 结果是游戏里
                        // 留下第三家 mod 的身体，而弹窗预览一直画的是默认行，三方各说一套；
                        // 2026-09-29 用户按「自己→前置→祖先前置→默认」重新拍板。）
                        if (seasonS is null)
                        {
                            var preSpr = ResolveBody(gamePath, scan, ch, opt).File;
                            if (preSpr is { Length: > 0 } && LooksLikeSprite(preSpr))
                                seasonS = GetSeasonFilesForChar(preSpr, ch.Id).TryGetValue(season, out var preSpr2)
                                    ? preSpr2 : preSpr;
                        }
                        // 变体资产（Portraits/Elliott_Winter_Indoor 等）强制钉入。
                        // 优先用【本季所选包】登记的那张变体文件 —— Baechu 是
                        // Appearance + Elliott_Winter_Indoor/_Outdoor，不能拿一张
                        // seasonS 糊到所有冬变体上（精灵四季全一样就是这么来的）。
                        foreach (var v in scan.VariantAssets)
                        {
                            if (!string.Equals(v.BaseId, ch.Id, StringComparison.OrdinalIgnoreCase)) continue;
                            var isSeasonVar = v.VariantId.EndsWith(cap, StringComparison.OrdinalIgnoreCase)
                                || v.VariantId.Contains("_" + cap, StringComparison.OrdinalIgnoreCase);
                            // v1.7.26：节日场合资产（Beach/Luau/Fair/Jellies…名字不带季节）也要跟着
                            // 这一季选的包钉，各带 When:{Season}。全局档钉不了的角色（Portraiture 素材）
                            // 只有这段在写盘 ⇒ 不补就等于那些场合永远读别家（2026-09-28 对账：
                            // 艾米丽 8 条节日场合全漏）。走路表不补 —— 拿脸图顶精灵表 CP 直接报错。
                            if (!isSeasonVar && (v.Kind != "Portraits" || seasonP is null)) continue;
                            string? file = null;
                            if (isSeasonVar && !string.IsNullOrEmpty(v.File)
                                && string.Equals(v.Pack, pack, StringComparison.OrdinalIgnoreCase)
                                && File.Exists(v.File))
                                file = v.File;
                            file ??= v.Kind == "Portraits" ? seasonP : seasonS;
                            if (file is null) continue;
                            var key = v.Kind + "/" + v.VariantId;
                            var natS2 = natByAsset.TryGetValue(key, out var natSi) ? natSi : null;
                            // 同一张场合资产按季各钉一条 ⇒ 落盘文件必须带季名，否则四条补丁
                            // 读的是同一个物理文件（Windows 不分大小写），只有最后一季生效。
                            var fname = isSeasonVar ? v.VariantId : v.VariantId + "__" + season;
                            var dest = Path.Combine(assets, v.Kind, fname + ".png");
                            // 立绘场合尺寸不够 ⇒ 按格平铺补满再钉；走路表场合 ⇒ 纵向拉伸铺满再钉
                            var okS = v.Kind == "Portraits"
                                ? (SubstituteNeedsPad(natS2, file, out var spw, out var sph)
                                    ? CopyAsPngPadded(file, dest, spw, sph) : CopyAsPng(file, dest))
                                : CopyBody(file, dest, BodyCanvas(v.Kind, v.VariantId, file, null));
                            if (!okS) continue;
                            // 强制：即使全局已钉过同名变体也要再钉一条（后写者在 CP 里赢）
                            written.Add(key);
                            if (isSeasonVar)
                                changes.Add(new { Action = "EditImage", Target = key,
                                    FromFile = $"assets/{v.Kind}/{fname}.png", PatchMode = "Replace" });
                            else
                                changes.Add(new { Action = "EditImage", Target = key,
                                    FromFile = $"assets/{v.Kind}/{fname}.png", PatchMode = "Replace",
                                    When = new Dictionary<string, string> { ["Season"] = season } });
                        }
                        // 基础资产的 When Season 也要跟着覆盖（游戏按 Season 条件解析时）
                        foreach (var kind in new[] { "Portraits", "Characters" })
                        {
                            var file = kind == "Portraits" ? seasonP : seasonS;
                            if (file is null) continue;
                            if (kind == "Characters" && !LooksLikeSprite(file)) continue;
                            if (kind == "Portraits" && !LooksLikePortrait(file)) continue;
                            // ⚠ 双下划线：单下划线会和【场合资产】那条写的
                            // assets/Portraits/Caroline_Summer.png 撞成同一个物理文件
                            //（Windows 不分大小写），两条补丁读同一份字节 ⇒ 四季里有一季被
                            // 另一条的来源顶掉（2026-09-28 对账：卡罗琳夏季钉成春季那张）。
                            var img = $"{assetId}__{season}.png";
                            if (!(kind == "Portraits"
                                    ? CopyPortraitFull(file, Path.Combine(assets, kind, img), faceSizes)
                                    : CopyBody(file, Path.Combine(assets, kind, img),
                                        BodyCanvas(kind, assetId, file, bodySheets)))) continue;
                            changes.Add(new
                            {
                                Action = "EditImage",
                                Target = kind + "/" + assetId,
                                FromFile = $"assets/{kind}/{img}",
                                PatchMode = "Replace",
                                When = new Dictionary<string, string> { ["Season"] = season }
                            });
                        }
                    }
                }

                // v1.7.28：接管 HD 肖像通道。别的包写了 Mods/HDPortraits/<资产名> 时，渲染端
                //（Portraiture 的 HDP 模式 / HD Portraits 本体）画的是那条数据资产里 Portrait 指向的图，
                // 【完全不读】Portraits/<npc> ⇒ 上面钉得再对，对话框里那张脸也纹丝不动
                //（2026-09-29 实机：法师四季都钉的是用户选的 Seasonal Rasmodia、像素哈希逐张对得上，
                // 游戏里却始终是 [CP] Dacar Rasmodia Portraits 那张 512 宽高清脸）。
                // 做法：把那条数据资产的 Portrait 指回 Portraits/<资产名>、Size 改成我们那张图的格宽，
                // HD 通道画出来的就是用户选的那张。
                // ⚠ 只在【我们自己钉过】Portraits/<资产名> 时接管 —— 用户没给这个角色选过皮肤，
                // 就不该动别人家的高清肖像（那条通道本来就该归它）。
                // 放在按季段之后：场合资产（Wizard_Beach 等）要到变体那段才进 written。
                // ⚠ hdFaceOnly 时整段跳过：这个角色选的就是 HD 包，本文件开头已经给那条数据资产
                // 写过 Portrait→本包高清资产；这里再追加一条同名补丁会在合并时按 target 顶掉前者
                //（实测：身体照钉了、脸却被改回 Portraits/<角色> ⇒ 游戏里仍是别家 mod 的高清脸）。
                if (scan.HdPortraitRendererInstalled && !hdFaceOnly)
                    foreach (var hd in scan.HdPortraitEntries)
                    {
                        if (!string.Equals(hd.Npc, assetId, StringComparison.OrdinalIgnoreCase)
                            && !hd.Npc.StartsWith(assetId + "_", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!written.Contains("Portraits/" + hd.Npc)) continue;
                        if (HdCellSize(assets, hd.Npc) is not { } hdSize) continue;
                        changes.Add(new
                        {
                            Action = "EditData",
                            Target = hd.DataAsset,
                            // 与 Dacar 自己的写法同构（Entries 设顶层键）—— 那份资产是
                            // {"Size":256} 这种对象，CP 按字典处理，Entries 就是改顶层键。
                            Entries = new Dictionary<string, object>
                            {
                                ["Portrait"] = "Portraits/" + hd.Npc,
                                ["Size"] = hdSize,
                            },
                        });
                        AppLog.Warn("Portraits",
                            $"[HD肖像通道] 已接管 {hd.DataAsset} → Portraits/{hd.Npc}（Size={hdSize}，来源包 {hd.Pack}）");
                    }
            }

            var contentPath = Path.Combine(root, "content.json");
            JArray contentArr;
            if (onlyIds is null)
            {
                contentArr = new JArray();
            }
            else
            {
                // 增量：保留其它角色的旧条目，只替换本次角色相关 Target
                contentArr = new JArray();
                var mergeOk = true;
                // v1.7.28：HD 肖像通道的接管条目（Mods/HDPortraits/<资产名>）也按角色归属 ——
                // 不一起换掉，增量换肤后那条通道还指着上一次的选择。
                var hdAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var hd in scan.HdPortraitEntries)
                {
                    var aid = hd.DataAsset.StartsWith("Mods/HDPortraits/", StringComparison.OrdinalIgnoreCase)
                        ? hd.DataAsset["Mods/HDPortraits/".Length..] : hd.Npc;
                    if (onlyIds.Any(id =>
                            string.Equals(aid, GameAssetId(id), StringComparison.OrdinalIgnoreCase)
                            || aid.StartsWith(GameAssetId(id) + "_", StringComparison.OrdinalIgnoreCase)))
                        hdAssets.Add(hd.DataAsset);
                }
                try
                {
                    if (File.Exists(contentPath))
                    {
                        var old = JObject.Parse(File.ReadAllText(contentPath));
                        if (old["Changes"] is JArray prev)
                        {
                            foreach (var item in prev)
                            {
                                var t = item?["Target"]?.ToString() ?? "";
                                var hit = hdAssets.Contains(t) || onlyIds.Any(id =>
                                    t.Equals("Portraits/" + GameAssetId(id), StringComparison.OrdinalIgnoreCase)
                                    || t.Equals("Characters/" + GameAssetId(id), StringComparison.OrdinalIgnoreCase)
                                    || t.StartsWith("Portraits/" + GameAssetId(id) + "_", StringComparison.OrdinalIgnoreCase)
                                    || t.StartsWith("Characters/" + GameAssetId(id) + "_", StringComparison.OrdinalIgnoreCase));
                                if (!hit) contentArr.Add(item);
                            }
                        }
                    }
                }
                catch
                {
                    // content.json 读坏 → 空表继续增量会把其它角色全清掉（「设完不生效要再设」）。
                    mergeOk = false;
                }
                if (!mergeOk)
                {
                    AppLog.Warn("Portraits", "[覆盖包] content.json 合并失败，改为全量重建");
                    WriteOverridePack(gamePath, scan, skins, vanillaDefaults, onlyIds: null);
                    return;
                }
            }
            var skippedGhosts = 0;
            // 同一 Target + 同一 When.Season 只留最后一条（单季覆盖必须赢过全局包）。
            // 旧逻辑无去重：全局钉了 Sam_Winter，单季指定又加一条，CP 行为不确定。
            var slots = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            static string SlotKey(JObject jo)
            {
                var t = jo["Target"]?.ToString() ?? "";
                // 键要含【完整 When】，不能只看 Season —— 冬天「室内/室外」两条同 Target、
                // 同 Season=winter，只按 Season 归并会把其中一条吃掉（实测只剩 IsOutdoors=false）。
                // 全 When 参与：Season-only 的旧语义（单季覆盖赢过全局包）不变，多一条 IsOutdoors
                // 才分槽。
                var w = jo["When"];
                var ws = w is null ? "" : string.Join(",", w.Children<JProperty>()
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(p => p.Name + "=" + p.Value));
                return t + "␟" + ws.ToLowerInvariant();
            }
            foreach (var item in contentArr)
                if (item is JObject prevJo) slots[SlotKey(prevJo)] = prevJo;
            foreach (var c in changes)
            {
                var jo = JObject.FromObject(c);
                var from = jo["FromFile"]?.ToString();
                // EditData 不带 FromFile（v1.7.28 的 HD 肖像通道接管就是这一类）——
                // 它不引用任何本包文件，没有"幽灵引用"可言，不能被下面这道闸吃掉。
                if (!string.Equals(jo["Action"]?.ToString(), "EditData", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(from))
                    { skippedGhosts++; continue; }
                    var abs = Path.Combine(root, from.Replace('/', Path.DirectorySeparatorChar));
                    // 终检：绝不写出引用不存在 PNG 的补丁 —— CP 加载失败会把资产置 null，
                    // SMAPI 再拦 null，游戏绘制循环 ContentLoadException → 整局崩溃。
                    if (!File.Exists(abs))
                    {
                        skippedGhosts++;
                        AppLog.Warn("Portraits", $"[覆盖包] 跳过幽灵补丁 {jo["Target"]} ← {from}（文件不存在）");
                        continue;
                    }
                }
                slots[SlotKey(jo)] = jo;
            }
            contentArr = new JArray(slots.Values);
            // v1.7.3：Priority 必须压过社区包 —— Donut's 这类写的是 "Late + 1"，
            // 覆盖包默认优先级（500）会在它之前生效、随后被它盖回去，季节单独指定
            // 的皮肤等于白设（实测：Sophia 夏天指了 SCC 仍显示 Donut 动漫脸）。
            // v1.7.22：再抬到 "Late + 100"。原来这条是 "Late + 10"，而【我们自己】早期版本
            // 转出来的裸图包（Female Wizard - Sprite And Portrait = JuniGrid.PortraitPack.DAB63FB592）
            // 每条补丁也写着 "Late + 10" ⇒ 同优先级并列，谁赢只由加载顺序决定（不是我们的选择）。
            // 实机症状：法师四季各锁了一个包，游戏里却始终显示那张 FemWizard 的脸
            //（2026-09-28 用户截图：紫发宽檐帽，与 assets/Female Wizard/FemWizard.png 逐像素一致）。
            // 自愈不能靠"去掉那边的 Priority"——转换护栏规定带 Priority 的老包不碰，
            // 所以闸门只能放在这一侧：选择器必须严格高于一切被选择的包。
            foreach (var item in contentArr)
                if (item is JObject pJo && pJo["Priority"] is null)
                    pJo["Priority"] = OverridePriority;
            var content = new JObject(
                new JProperty("Format", "2.0.0"),
                new JProperty("Changes", contentArr));
            // 先写临时文件再替换，避免写一半留下半截 content.json。
            // 游戏/SMAPI 可能占着 content.json → 重试几次，仍失败就整体全量重建兜底。
            var tmpContent = contentPath + ".junigrid.tmp";
            var writtenOk = false;
            for (var wtry = 1; wtry <= 4 && !writtenOk; wtry++)
            {
                try
                {
                    File.WriteAllText(tmpContent, content.ToString(Newtonsoft.Json.Formatting.None));
                    File.Move(tmpContent, contentPath, true);
                    writtenOk = true;
                }
                catch (Exception wex)
                {
                    AppLog.Warn("Portraits", $"[覆盖包] 写 content.json 失败（{wtry}/4）: {wex.Message}");
                    Thread.Sleep(80 * wtry);
                }
            }
            if (!writtenOk)
            {
                if (onlyIds is not null)
                {
                    WriteOverridePack(gamePath, scan, skins, vanillaDefaults, onlyIds: null);
                    return;
                }
                AppLog.Error("Portraits", "[覆盖包] content.json 多次写入失败，本次换肤可能未生效");
            }
            // 终检：写完立刻回读，确保文件可解析且条目非空（有选择时）
            try
            {
                var verify = JObject.Parse(File.ReadAllText(contentPath));
                if (verify["Changes"] is not JArray va || va.Count == 0)
                    AppLog.Warn("Portraits", "[覆盖包] content.json 写入后为空，下次启动前会自动全量重建");
            }
            catch (Exception vex)
            {
                AppLog.Warn("Portraits", "[覆盖包] content.json 写入后无法解析: " + vex.Message);
            }
            // v1.7.27：清掉【没有任何补丁引用】的旧落盘图。以前只覆盖不删，assets 里越积越多
            //（实测 505 张孤儿），排查时极易被当成"我们钉了这张"—— 2026-09-28 另一模型据此
            // 误判法师底图来自 RomRas。游戏占用删不掉只记日志，不影响本次写盘。
            if (onlyIds is null && writtenOk)
            {
                var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in contentArr)
                {
                    var fr = (item as JObject)?["FromFile"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(fr))
                        keep.Add(Path.GetFullPath(Path.Combine(root,
                            fr.Replace('/', Path.DirectorySeparatorChar))));
                }
                int swept = 0, locked = 0;
                foreach (var kind in new[] { "Portraits", "Characters" })
                {
                    var dir = Path.Combine(assets, kind);
                    if (!Directory.Exists(dir)) continue;
                    foreach (var f in Directory.GetFiles(dir))
                    {
                        if (keep.Contains(Path.GetFullPath(f))) continue;
                        try { File.Delete(f); swept++; } catch { locked++; }
                    }
                }
                if (swept > 0 || locked > 0)
                    AppLog.Warn("Portraits", $"[覆盖包] 清掉未被引用的旧图 {swept} 张" +
                        (locked > 0 ? $"（{locked} 张被游戏占用，下次再清）" : ""));
            }
            // 记下这份 content.json 是按哪版落盘语义生成的（启动自检据此决定要不要全量重建）。
            // 写失败就不记 —— 下次启动会当作"版本不符"再重建一遍，比记错版本安全。
            if (writtenOk)
            {
                try { File.WriteAllText(Path.Combine(root, OverrideFormatFile), OverrideFormat.ToString()); }
                catch (Exception fex)
                { AppLog.Warn("Portraits", "[覆盖包] 写落盘格式标记失败: " + fex.Message); }
            }
            if (onlyIds is null)
                AppLog.Warn("Portraits", $"[覆盖包] 已重建：{contentArr.Count} 条补丁" +
                    (skippedGhosts > 0 ? $"（跳过 {skippedGhosts} 条幽灵引用）" : ""));

        }
        catch (Exception ex)
        { AppLog.Warn("Portraits", "覆盖包更新失败: " + ex.Message); }
        finally
        {
            if (ovBefore is { } b) { try { Directory.SetLastWriteTime(ovDir, b); } catch { } }
        }
    }


    /// <summary>
    /// v1.6.8：把「基础文件 + 季节变体」钉进覆盖包。有季节变体（&lt;id&gt;_&lt;季&gt;.png）时
    /// 逐季 EditImage + Season 条件（基础图只兜底未被季节图覆盖的季节）；没有季节
    /// 变体时维持 Load+EditImage 单文件钉入。季节包（Baechu 等）由此实现
    /// 游戏内四季正确轮换 —— 静态钉一张图会让四季都显示同一张（实测）。
    /// assetId = 游戏真实资产名（Leo → ParrotBoy），Target 与 FromFile 都用它。
    /// </summary>
    private static void PinOverrideAsset(List<object> changes, HashSet<string> written,
        string assets, string assetKind, string assetId,
        string? baseSrc, Dictionary<string, string>? seasonFiles, List<(int w, int h)>? faceSizes = null,
        Func<string?, int>? bodyCanvas = null)
    {
        // 立绘走"补满底图"那条；走路表矮 ⇒ 纵向拉伸铺满画布（见 CopyBody）
        bool Write(string src, string dest) =>
            assetKind.Equals("Portraits", StringComparison.OrdinalIgnoreCase)
                ? CopyPortraitFull(src, dest, faceSizes)
                : CopyBody(src, dest, bodyCanvas?.Invoke(src) ?? 0);
        // v1.7.7：Characters/ 目标只收走路精灵表 —— 头像（128×320）钉上去会报
        // "target area extends past the right edge of the image (Width:64)"（Andy 实测）。
        if (assetKind.Equals("Characters", StringComparison.OrdinalIgnoreCase))
        {
            // ⚠ 这里【不判尺寸】：矮于底图曾经被当成"没身子"直接丢掉 ⇒ 那条资产一条补丁都没有，
            // 游戏里整具身子换成 mod 栈的结果 = 用户明确禁止的"换人"（2026-09-29 反馈）。
            // 尺寸不等该由"换整张（Load）"解决，不该由"少钉一条"解决，见 docs/方案-20260929-走路表落盘-v2.md。
            if (seasonFiles is { Count: > 0 })
                seasonFiles = seasonFiles
                    .Where(kv => LooksLikeSprite(kv.Value))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            if (seasonFiles is { Count: 0 }) seasonFiles = null;
            if (baseSrc is not null && !LooksLikeSprite(baseSrc)) baseSrc = null;
            if (baseSrc is null && seasonFiles is null) return;
        }
        if (!written.Add(assetKind + "/" + assetId)) return;   // 同目标已钉（Exclusive 冲突防护）
        var target = assetKind + "/" + assetId;
        var seasons = new[] { "spring", "summer", "fall", "winter" };

        if (seasonFiles is { Count: > 0 })
        {
            var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (season, sFile) in seasonFiles)
            {
                // v1.7.14：冬天常拆「室内 / 室外」两张（Baechu 的 Emily、Donut 的 Claire…）。
                // 这是同一张肖像按运行时状态切换，不是两张卡 —— 必须【两张都钉】，各带
                // IsOutdoors 条件，游戏进屋/出屋才各自显示对的那张。只钉一张 = 另一地点掉图。
                // 判据用文件名，散图 / Portraiture / 转换包同样适用（不依赖 ConfigSchema）。
                if (season.Equals("winter", StringComparison.OrdinalIgnoreCase)
                    && WinterLocationPair(sFile) is { } pair)
                {
                    var any = false;
                    foreach (var (io, f) in new[] { ("true", pair.outdoor), ("false", pair.indoor) })
                    {
                        var pDest = Path.Combine(assets, assetKind, $"{assetId}__winter_{io}.png");
                        if (!Write(f, pDest)) continue;
                        any = true;
                        changes.Add(new
                        {
                            Action = "EditImage",
                            Target = target,
                            FromFile = $"assets/{assetKind}/{assetId}__winter_{io}.png",
                            PatchMode = "Replace",
                            When = new Dictionary<string, string> { ["Season"] = "winter", ["IsOutdoors"] = io },
                        });
                    }
                    if (any) { covered.Add(season); continue; }
                }
                // ⚠ 双下划线：单下划线会和【变体资产】那条写的
                // assets/Characters/Caroline_Spring.png 撞成同一个物理文件（Windows 不分大小写），
                // 变体那条在后面 ⇒ 把四季的钉图全覆盖成基础图（卡罗琳实测：四条 MD5 相同）。
                var dest = Path.Combine(assets, assetKind, $"{assetId}__{season}.png");
                if (!Write(sFile, dest)) continue;
                covered.Add(season);
                changes.Add(new
                {
                    Action = "EditImage",
                    Target = target,
                    FromFile = $"assets/{assetKind}/{assetId}__{season}.png",
                    PatchMode = "Replace",
                    When = new Dictionary<string, string> { ["Season"] = season },

                });
            }
            if (baseSrc is not null)
            {
                var rest = string.Join(", ", seasons.Where(s => !covered.Contains(s)));
                if (rest.Length == 0) return;
                var bDest = Path.Combine(assets, assetKind, assetId + ".png");
                if (!Write(baseSrc, bDest)) return;
                changes.Add(new
                {
                    Action = "EditImage",
                    Target = target,
                    FromFile = $"assets/{assetKind}/{assetId}.png",
                    PatchMode = "Replace",
                    When = new Dictionary<string, string> { ["Season"] = rest },

                });
            }
            return;
        }

        if (baseSrc is null) return;
        var d = Path.Combine(assets, assetKind, assetId + ".png");
        if (!Write(baseSrc, d)) return;
        changes.Add(new { Action = "EditImage", Target = target,
            FromFile = $"assets/{assetKind}/{assetId}.png", PatchMode = "Replace" });
    }

    /// <summary>若这张冬季图是 &lt;stem&gt;_Winter_Indoor / _Winter_Outdoor 且同目录存在另一张，
    /// 返回 (indoor, outdoor) 供「两张都钉」；否则 null（普通单张冬季图，走单钉）。
    /// 同扩展名（.png/.xnb）才算一对。判据纯文件名 ⇒ CP / 散图 / Portraiture / 转换包通用。</summary>
    private static (string indoor, string outdoor)? WinterLocationPair(string? file)
    {
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return null;
        const string inSfx = "_Winter_Indoor", outSfx = "_Winter_Outdoor";
        var name = Path.GetFileNameWithoutExtension(file);   // 不含扩展名，才能安全截后缀
        var ext = Path.GetExtension(file);
        var dir = Path.GetDirectoryName(file);
        if (dir is null) return null;
        string? other;
        bool thisIsIndoor;
        if (name.EndsWith(inSfx, StringComparison.OrdinalIgnoreCase))
        { other = Path.Combine(dir, name[..^inSfx.Length] + outSfx + ext); thisIsIndoor = true; }
        else if (name.EndsWith(outSfx, StringComparison.OrdinalIgnoreCase))
        { other = Path.Combine(dir, name[..^outSfx.Length] + inSfx + ext); thisIsIndoor = false; }
        else return null;
        if (!File.Exists(other)) return null;
        return thisIsIndoor ? (file, other) : (other, file);
    }

    /// <summary>走路表落盘：原样拷（⚠ 不要纵向拉伸 —— 见下面说明）。</summary>
    /// <remarks>
    /// 2026-09-29 我先按"游戏取格行高 = 纹理高 ÷ 4"的模型做过一版拉伸铺满，实机直接把法师
    /// 拉成一个长头。墨迹剖面量下来，各家走路表其实一律按【32 像素行距】铺画：
    /// Baechu Wizard_Winter 64×192 = 6 行×32、Baechu Haley_Fall 384 = 12 行×32、
    /// SCC Haley_Fall 416 = 13 行×32、SCC-SVE Magnus_Winter 480 同样 32 行距；
    /// 而拉伸后的那张变成 6 行×80 ⇒ 不管游戏按 32 还是按 高÷4 取格，拉伸都不会更好。
    /// ⇒ 保留"场合资产也要钉所选包那张"（这条是真 bug 修复：以前嫌矮整条不钉，
    /// 游戏里永远是别家身子），撤掉拉伸。取格模型还没定死，下一步用实机一张图分辨。
    /// </remarks>
    private static bool CopyBody(string? src, string dest, int canvasH) => CopyAsPng(src, dest);

    /// <summary>把立绘/精灵表源拷成覆盖包里的 PNG（xnb 先解码）。失败返回 false，调用方跳过该项。</summary>
    private static bool CopyAsPng(string? src, string dest)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(src) || !File.Exists(src)) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            // v1.6.8：目标与源一致（大小+修改时间）→ 跳过复制。切换皮肤时全量重钉
            // 几十张图，绝大多数没变 —— 跳过让重复切换近乎零 IO。
            if (File.Exists(dest)
                && new FileInfo(src).Length == new FileInfo(dest).Length
                && File.GetLastWriteTimeUtc(src) == File.GetLastWriteTimeUtc(dest))
                return true;
            if (src.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase))
            {
                var tex = XnbDecoder.TryDecode(src);
                if (tex is not null)
                {
                    File.WriteAllBytes(dest,
                        PixelKit.CropScalePng(tex, 0, 0, tex.Width, tex.Height, tex.Width, tex.Height));
                    File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(src));
                    return true;
                }
                // 兜底：扩展名是 .xnb 但内容其实是 PNG（测试夹具/用户手改）→ 按 PNG 原样拷
                var head = new byte[8];
                using (var fs = File.OpenRead(src))
                {
                    if (fs.Read(head, 0, 8) < 8) return false;
                }
                if (head[0] == 0x89 && head[1] == (byte)'P' && head[2] == (byte)'N' && head[3] == (byte)'G')
                {
                    File.Copy(src, dest, overwrite: true);
                    File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(src));
                    return true;
                }
                return false;
            }
            try
            {
                File.Copy(src, dest, overwrite: true);
                File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(src));   // 供下次跳过判定
            }
            catch (IOException) when (File.Exists(dest))
            {
                // 目标被游戏进程锁住 → 读源字节后覆盖写（多数情况下可成功）
                File.WriteAllBytes(dest, File.ReadAllBytes(src));
                File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(src));
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>解析包在磁盘上的真实目录：优先规范化无点名，回退末段带 . 前缀的禁用名。</summary>
    private static string? ResolvePackDir(string modsDir, string pack)
    {
        var parts = pack.Replace('\\', '/').Split('/');
        var parent = parts.Length == 1
            ? modsDir
            : Path.Combine(modsDir, string.Join(Path.DirectorySeparatorChar, parts, 0, parts.Length - 1));
        var clean = Path.Combine(parent, parts[^1]);
        if (Directory.Exists(clean)) return clean;
        var alt = Path.Combine(parent, "." + parts[^1]);
        return Directory.Exists(alt) ? alt : null;
    }

    /// <summary>把"往某个包目录里写东西"的动作包一层，写完把【目录】的最后修改时间还回去。
    /// Mods 页默认按目录修改时间排序（"最新在前"），而换肤只是我们往包里写 config.json、
    /// 重建覆盖包 —— 不还原时间戳，用户每切一次肖像列表就重排一次（实测）。
    /// ⚠ 只还原目录，不动文件自己的 mtime：内容真变了，扫描缓存该重扫就得重扫。</summary>
    private static void KeepDirTime(string dir, Action write)
    {
        DateTime? before = null;
        try { if (Directory.Exists(dir)) before = Directory.GetLastWriteTime(dir); } catch { }
        try { write(); }
        finally
        {
            if (before is { } b) { try { Directory.SetLastWriteTime(dir, b); } catch { } }
        }
    }

    /// <summary>写包的 config.json：本包被选中角色的开关 → true；包内其它角色的开关 → false
    /// （社区惯例布尔键含角色名，如 ReplaceAbigail）。原子替换，失败只记日志不炸页面。
    /// v1.7.7：键写成 "键=值" 形态时（同包多画风的那个配置键，见 PortraitSkinOption.VariantConfigKey）
    /// 落的是**字符串**值 —— CP 要的是 assets/ 下的目录名，写布尔它自己解析不出路径
    ///（Donut's 实测：包里三套画，游戏里"看着生效"全靠我们那个高优先级覆盖包压着）。
    /// variants = 角色 id → 选中的画风目录名；用它从同一个键的多条候选里挑一条。</summary>
    private static void WritePackConfig(string packDir, string pack, PortraitScanResult scan,
        HashSet<string> enabledChars, Dictionary<string, string> variants)
    {
        try
        {
            var path = Path.Combine(packDir, "config.json");
            JObject root;
            if (File.Exists(path))
            {
                using var sr = new StreamReader(path);
                root = JObject.Parse(sr.ReadToEnd());
            }
            else root = new JObject();

            var changed = false;
            var skippedStyle = 0;
            foreach (var (key, chars) in scan.ConfigKeys)
            {
                if (!string.Equals(key.Pack, pack, StringComparison.OrdinalIgnoreCase)) continue;
                var want = enabledChars.Contains(key.Char);
                // 画风键：同一个键会带着全部画风的候选（扫描期还不知道用户选哪套），
                // 这里按配置挑一条出来写，挑不出就不写 —— 拿字母序第一个去猜会把用户
                // 没选的那套画写进包里，那是往用户机器上写错数据。
                var style = chars.Where(k => SplitStyleKey(k) is not null)
                    .GroupBy(k => SplitStyleKey(k)!.Value.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
                foreach (var k in chars)
                {
                    if (SplitStyleKey(k) is { } kv)
                    {
                        if (!want) continue;   // 没选中这个角色：不动作者的画风设置
                        if (style[kv.Key].Count < 2)
                        {
                            // 只有一条候选 = 扫描时只有那一个画风目录，直接写它
                            if (SetStyleValue(root, kv.Key, kv.Value)) changed = true;
                            continue;
                        }
                        // 没记画风（老配置、或作者把目录删了）⇒ 用第一条候选 = 作者默认档，
                        // 与覆盖包那边的回落口径一致。以前这里只记一行警告，于是每次换肤
                        // 给每个"选了该包但没记画风"的角色刷一行（实测十几行噪音）。
                        variants.TryGetValue(key.Char, out var picked);
                        if (picked is { Length: > 0 }
                            && style[kv.Key].Any(x => SplitStyleKey(x)!.Value.Value == picked))
                        {
                            if (SetStyleValue(root, kv.Key, picked)) changed = true;
                        }
                        // 没记画风（老配置 / 作者把目录删了）⇒ **不写**：那是用户的选择记录，
                        // 拿"第一条候选"去猜就是替他改设置（B26e 钉着这条）。
                        // 但也不许每个角色刷一行日志 —— 攒起来一行报数。
                        else skippedStyle++;
                        continue;
                    }
                    if (root[k] is { Type: JTokenType.Boolean } tok && (bool)tok == want) continue;
                    // ⚠ 不敢替角色关掉"名字里不含它"的开关：社区惯例的换肤键是 ReplaceAbigail 这种
                    // 带角色名的，而 Nose Overlay Toggle / SCA Overwrite 这类是包级装饰开关 ——
                    // 合并卡的另一个成员没选这个包时，want=false 会把它们一起关掉
                    //（实测：切一次肖像把用户的雀斑/鼻子叠加写成了 false）。
                    // 选中时照样写 true（换肤需要它开），不选中就不碰。
                    if (!want && !k.Contains(key.Char, StringComparison.OrdinalIgnoreCase)) continue;
                    root[k] = want;
                    changed = true;
                }
            }
            if (skippedStyle > 0)
                AppLog.Info("Portraits",
                    $"[配置未写] {pack} 有 {skippedStyle} 个角色没记画风（老配置或画风目录被作者删了），保持包内现值不动");
            if (!changed) return;
            var tmp = path + ".junigrid.tmp";
            KeepDirTime(packDir, () =>
            {
                File.WriteAllText(tmp, root.ToString(Newtonsoft.Json.Formatting.Indented));
                File.Move(tmp, path, true);
            });
        }
        catch (Exception ex)
        { AppLog.Warn("Portraits", $"写 {pack}/config.json 失败: {ex.Message}"); }
    }

    /// <summary>把画风值写回 config.json，返回"是否真的改了"。
    /// ⚠ 保持作者原来用的 JSON 形态：原来写布尔就还布尔、写字符串就还字符串。
    /// CP 内部按字符串比，两种都吃（Donut's 自己就写着 "Ras Patch": "true"），
    /// 但把它改成作者没用过的那种就是往用户机器上写怪数据。</summary>
    private static bool SetStyleValue(JObject root, string key, string value)
    {
        var cur = root[key];
        if (cur is { Type: JTokenType.Boolean } && bool.TryParse(value, out var bv))
        {
            if ((bool)cur == bv) return false;
            root[key] = bv;
            return true;
        }
        if (cur?.ToString() == value) return false;
        root[key] = value;
        return true;
    }

    /// <summary>把 ConfigKeys 里的一项拆成 (键, 画风目录)；不是 "键=值" 形态返回 null。
    /// ⚠ 只在花括号**外**的第一个 '=' 处切：包里有 "HasFile:{{FromFile}}" 这类伪键
    ///（Donut's 的 config.json 与 ConfigSchema 都带它，实测就在同一份键表里），
    /// 花括号内出现 '=' 的写法不能被当成画风键，否则会被当字符串写进包里。</summary>
    private static (string Key, string Value)? SplitStyleKey(string k)
    {
        if (string.IsNullOrEmpty(k)) return null;
        var depth = 0;
        for (var i = 0; i < k.Length; i++)
        {
            var c = k[i];
            if (c == '{') depth++;
            else if (c == '}') depth--;
            else if (c == '=' && depth == 0 && i > 0 && i < k.Length - 1)
                return (k[..i], k[(i + 1)..]);
        }
        return null;
    }

    /// <summary>设置某季节的独立皮肤（packFolder=null 清除该季，回全局选择）。写盘重建覆盖包。
    /// 锁定中的角色拒绝写入。
    /// v1.7.7：variant = 该季独立选的那张卡的画风。「季节版按它现有的键规则各存各的」——
    /// PortraitSeasonSkins 的 "季节:包␟…" 值格式不动（改了老配置读不出来、别处也在按这格式解析），
    /// 画风另存 "角色 id␟季节" 键，与全局那条互不干扰。</summary>
    public void SetSeasonSkin(string gamePath, PortraitScanResult scan, string charId, string season,
        string? packFolder, string? variant = null)
    {
        // 与 SelectSkin 同理：合并卡的一季选择要落到这个人的全部 NPC 条目上
        foreach (var id in MemberIds(scan, charId))
        {
            // 长按=固定到当前季：与 SelectSkin 同口径，用户明确的选择压过锁定
            _cfg.Current.PortraitLocks.Remove(id);
            var cur = _cfg.Current.PortraitSeasonSkins.TryGetValue(id, out var v) ? v : "";
            var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var seg in cur.Split('␟'))
            {
                var i = seg.IndexOf(':');
                if (i > 0) parts[seg[..i]] = seg[(i + 1)..];
            }
            if (packFolder is null) parts.Remove(season);
            else parts[season] = packFolder;
            if (parts.Count == 0) _cfg.Current.PortraitSeasonSkins.Remove(id);
            else _cfg.Current.PortraitSeasonSkins[id] = string.Join("␟", parts.Select(kv => kv.Key + ":" + kv.Value));
            var vKey = id + VariantSep + season;
            if (string.IsNullOrEmpty(packFolder) || string.IsNullOrEmpty(variant))
                _cfg.Current.PortraitSkinVariants.Remove(vKey);
            else _cfg.Current.PortraitSkinVariants[vKey] = variant;
        }
        _cfg.Save(_cfg.Current);
        SyncToDisk(gamePath, scan, MemberIds(scan, charId));
    }

    /// <summary>解析角色的每季皮肤表（␟ 分隔串 → 季节→包）。
    /// 空包名 = 显式固定到默认/原版行（PackFolder 空串）—— 右键「默认」卡写入的
    /// "winter:" 必须保留，否则冬天永远回不到原版冬装（Emily 实测）。</summary>
    public static Dictionary<string, string> GetSeasonSkins(JuniGridConfig cfg, string charId)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (cfg.PortraitSeasonSkins.TryGetValue(charId, out var v))
            foreach (var seg in v.Split('␟'))
            {
                var i = seg.IndexOf(':');
                if (i > 0) d[seg[..i]] = seg[(i + 1)..];
            }
        return d;
    }

    /// <summary>变体资产名含季节段时，从基准文件旁取该季源；否则退回基准文件。
    /// 用于显式默认：Emily_Winter_Indoor → Content/Portraits/Emily_Winter.xnb。
    /// charId 非空时走 GetSeasonFilesForChar（支持每角色文件夹与 &lt;id&gt;_Season 命名）——
    /// OhoDavi 的 assets/Abigail/Normal.png 这种「不带角色名的基准图」靠它才能找到
    /// 同目录的 Abigail_Spring.png。</summary>
    private static string? ResolveSeasonalVariantFile(string? baseFile, string variantId, string? charId = null,
        Dictionary<string, string>? declaredSeasons = null)
    {
        if (string.IsNullOrWhiteSpace(baseFile)) return baseFile;
        var seasons = charId is { Length: > 0 }
            ? GetSeasonFilesForChar(baseFile, charId)
            : GetSeasonFiles(baseFile);
        // 按文件名猜不出来时，用【包自己声明的】「基资产 + When:{Season}」表兜上 ——
        // 卡罗琳 Overhaul 的四季文件叫 assets/Portraits/Spring.png（不带角色名），
        // 上面那条永远返回空 ⇒ 变体整族被钉成同一张，而游戏走 1.6 Appearance
        // 读的就是这些变体资产（Portraits/Caroline_Winter_Outdoor 等）⇒ 冬天仍显示春装
        //（2026-09-28 实机：存档 currentSeason=winter，截图脸与 Portraits/Spring.png 像素距 26.0、
        //  覆盖包里 Caroline_Spring/Summer/Fall/Winter_Indoor/Winter_Outdoor 五张 MD5 全等）。
        foreach (var (key, token) in new[]
                 { ("winter", "_Winter"), ("spring", "_Spring"), ("summer", "_Summer"), ("fall", "_Fall") })
        {
            if (!variantId.Contains(token, StringComparison.OrdinalIgnoreCase)) continue;
            if (seasons.TryGetValue(key, out var f)) return f;
            if (declaredSeasons is not null && declaredSeasons.TryGetValue(key, out var df)
                && File.Exists(df)) return df;
        }
        return baseFile;
    }

    /// <summary>探测立绘/精灵来源文件的春夏秋冬变体：同目录 <基准名>_spring/_summer/
    /// _fall/_winter.png（大小写不敏感）。覆盖两类实测布局 —— SCCC 的
    /// assets/Portraits/Sophia_Spring.png 与 Sunberry 的 assets/Portraits/&lt;id&gt;/&lt;id&gt;_spring.png，
    /// 都是「同目录 + _季节后缀」。来源文件本身是变体（Sophia_Spring）时先剥回基准名。
    /// 没有的季节无键 —— UI 切换到缺失季节时保持当前图（用户要求）。
    /// 结果按源路径缓存 —— 换肤渲染会对每张卡反复探测，磁盘 IO 是弹窗卡顿来源之一。</summary>
    private static readonly ConcurrentDictionary<string, Dictionary<string, string>> SeasonFileCache =
        new(StringComparer.OrdinalIgnoreCase);

    public static Dictionary<string, string> GetSeasonFiles(string? sourceFile)
    {
        if (string.IsNullOrWhiteSpace(sourceFile))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // 返回副本：调用方（GetSeasonFilesForChar）会在其上继续填键
        var cached = SeasonFileCache.GetOrAdd(sourceFile, static src =>
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var dir = Path.GetDirectoryName(src);
                if (dir is null || !Directory.Exists(dir)) return result;
                var stem = Path.GetFileNameWithoutExtension(src);
                foreach (var suffix in new[] { "_Spring", "_Summer", "_Fall", "_Winter" })
                    if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    { stem = stem[..^suffix.Length]; break; }
                foreach (var (key, suffix) in new[]
                         { ("spring", "_Spring"), ("summer", "_Summer"), ("fall", "_Fall"), ("winter", "_Winter") })
                {
                    var p = Path.Combine(dir, stem + suffix + ".png");
                    if (File.Exists(p)) { result[key] = p; continue; }
                    var sub = Path.Combine(dir, stem, stem + suffix + ".png");
                    if (File.Exists(sub)) { result[key] = sub; continue; }
                    var xnb = Path.Combine(dir, stem + suffix + ".xnb");
                    if (File.Exists(xnb)) result[key] = xnb;
                }
                if (!result.ContainsKey("winter"))
                {
                    var ownDir = Path.GetDirectoryName(src);
                    if (ownDir is not null
                        && Path.GetFileName(ownDir).Equals(stem, StringComparison.OrdinalIgnoreCase))
                    {
                        var indoor = Path.Combine(ownDir, stem + "_Winter_Indoor.png");
                        var outdoor = Path.Combine(ownDir, stem + "_Winter_Outdoor.png");
                        if (File.Exists(indoor)) result["winter"] = indoor;
                        else if (File.Exists(outdoor)) result["winter"] = outdoor;
                    }
                }
            }
            catch { }
            return result;
        });
        return new Dictionary<string, string>(cached, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// v1.6.8：按「角色 id + 来源文件」收集季节文件。结果缓存（源路径+角色 id）。
    /// </summary>
    private static readonly ConcurrentDictionary<string, Dictionary<string, string>> SeasonForCharCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>图片尺寸：PNG 读头 24 字节，XNB 解码后取宽高（身体链最后一档、走路表画布高都要量它）。
    /// </summary>
    /// <remarks>缓存键带【大小+mtime】：只按路径缓存会在原地换图后继续用旧尺寸 ——
    /// 用户更新一个 mod 就是原地覆盖同一批文件，那时会拿上一版的尺寸做错判（B55 实测）。</remarks>
    private static readonly ConcurrentDictionary<string, (int w, int h)> ImgSizeCache = new();
    private static (int w, int h) ImgSize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return (0, 0);
        string key;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return (0, 0);
            key = path + '|' + fi.Length + '|' + fi.LastWriteTimeUtc.Ticks;
        }
        catch { return (0, 0); }
        return ImgSizeCache.GetOrAdd(key, _ =>
        {
            PngSize(path, out var w, out var h);
            if (w > 0 && h > 0) return (w, h);
            if (path!.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase))
                try
                {
                    var t = XnbDecoder.TryDecode(path);
                    if (t is not null) return (t.Width, t.Height);
                }
                catch { }
            return (0, 0);
        });
    }

    /// <summary>场合资产要钉"别人那张"时，替身比本尊小 ⇒ 补白到本尊尺寸（true + 给出尺寸）。
    /// ⚠ 只给 Portraits 用：立绘是定格栅，补白只是多出几格空白，游戏照样取左上那格；
    /// 走路表不能补白（行高 = 纹理高 ÷ 4，补白会把行撑大 ⇒ 每帧裁错），那条走 <see cref="CopyBody"/>
    /// 的纵向拉伸。</summary>
    private static bool SubstituteNeedsPad(List<string>? natives, string? file, out int padW, out int padH)
    {
        padW = padH = 0;
        if (file is null || natives is null || natives.Count == 0) return false;
        var (w, h) = ImgSize(file);          // 不能用 PngSize：场合图的来源也可能是原版 .xnb
        if (w <= 0 || h <= 0) return false;
        // ⚠ 不再"用的就是这张资产自己登记的图 ⇒ 放行"：同一条资产别的包可能登记更高的表，
        // 游戏加载到的就是那张，我们矮 ⇒ 多出来的行仍是它的画（2026-09-28 对账剩 7 张这么漏的）。
        // 只比【同宽度】的底图：加宽会让目标区域超出图像右边界，CP 整条作废。
        foreach (var n in natives)
        {
            var (ow, oh) = ImgSize(n);
            if (ow != w || oh <= h) continue;
            padW = w; padH = Math.Max(padH, oh);
        }
        return padW > 0 && padH > 0;
    }

    /// <summary>解码源图、左上对齐贴进 w×h 的透明画布再写出去（xnb 也支持）。失败返回 false。</summary>
    private static bool CopyAsPngPadded(string? src, string dest, int w, int h)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(src) || !File.Exists(src)) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            var tex = src.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase)
                ? XnbDecoder.TryDecode(src) : PixelKit.DecodePng(src);
            if (tex is null) return false;
            File.WriteAllBytes(dest, PixelKit.PadPng(tex, w, h));
            return true;
        }
        catch { return false; }
    }

    /// <summary>立绘补丁必须【整张盖住】游戏里那张底图。CP 的 Replace 只覆盖"源图那么大"的一块，
    /// 底图多出来的行仍是别的包的画 —— 实测法师：我方 128×256、RomRas 底图 128×320 ⇒ 第 5 行
    /// 永远是它的脸，用户看到"无论选什么都蹦出那张脸"（2026-09-28）。
    /// 所以比底图矮时按 64 格【纵向】平铺补满；⚠ 绝不补宽：目标区域一旦比图像宽就整条作废
    ///（CP 报 "target area extends past the right edge of the image"，Donut 的 Gunther 实测）。</summary>
    private static bool CopyPortraitFull(string? src, string dest, List<(int w, int h)>? faceSizes)
    {
        if (string.IsNullOrWhiteSpace(src) || !File.Exists(src)) return false;
        // 必须用 ImgSize：PngSize 只读 PNG 头，原版那一侧（.xnb）量出来是 0×0 ⇒ 下面
        // "比底图矮就纵向平铺补满"整段被跳过 ⇒ 【默认行永远只钉一格】。
        // 2026-09-30 用 CP 的 `patch export "Portraits/Wizard"` 导出游戏真正在用的资产才看清：
        // 底图 128×1024（16 行表情帧），只有第 0 行是我们的原版脸，第 1–15 行仍是 OhoDavi 的
        // 女巫 ⇒ 台词取到哪一行就露哪张脸（用户报的"两句话变三次脸"，用例 B65）。
        var (w, h) = ImgSize(src);
        if (w > 0 && h > 0 && faceSizes is { Count: > 0 })
        {
            // 只认【同宽度】的底图：宽度不同就是另一套格子布局，拿它的高度来平铺会把
            // 一张 128×256 撑成 128×1024（实测法师），白涨几十倍像素还改变游戏取格。
            var minH = 0;
            foreach (var (fw, fh) in faceSizes)
                if (fw == w && fh > minH) minH = fh;
            if (minH > h) return CopyAsPngPadded(src, dest, w, minH);
        }
        return CopyAsPng(src, dest);
    }

    /// <summary>v1.7.28：HD 肖像通道的格宽。渲染端按数据资产里的 <c>Size</c> 从图上切一格，
    /// 立绘表恒为两列 ⇒ <c>Size = 图宽 / 2</c>（Dacar 的 512 宽表配 <c>Size:256</c> 实测）。
    /// 我们把 <c>Portrait</c> 指回 <c>Portraits/&lt;角色&gt;</c> 之后 Size 必须跟着改成我们那张图的格宽，
    /// 否则渲染端照旧按别家的 256 去切一张 128 宽的图 ⇒ 切出半张脸。
    /// 只认本尊落盘文件（<c>&lt;id&gt;.png</c> / <c>&lt;id&gt;__&lt;季节&gt;.png</c>）——
    /// <c>&lt;id&gt;_Spring.png</c> 这类是场合资产，宽度可能与本尊不同。取不到返回 null（不接管）。</summary>
    private static int? HdCellSize(string assetsDir, string assetId)
    {
        var dir = Path.Combine(assetsDir, "Portraits");
        if (!Directory.Exists(dir)) return null;
        foreach (var f in Directory.GetFiles(dir, assetId + "*.png"))
        {
            var stem = Path.GetFileNameWithoutExtension(f);
            if (stem.Length < assetId.Length
                || !stem.StartsWith(assetId, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = stem[assetId.Length..];
            if (rest.Length != 0 && !rest.StartsWith("__", StringComparison.Ordinal)) continue;
            PngSize(f, out var w, out _);
            if (w > 0) return w / 2;
        }
        return null;
    }

    /// <summary>预览端与落盘端【同一条】分季判据：先按文件名找（&lt;id&gt;_Spring.png），找不到再问包自己
    /// 声明的「基资产 + When:{Season}」表 —— Caroline (Overhaul) 那类四季共用一个资产名、文件只叫
    /// Spring.png 的包，光看文件名永远找不到，卡片就会一直显示它的基础图（=冬季那张）。</summary>
    public Dictionary<string, string> SeasonFilesFor(PortraitScanResult? scan, string? sourceFile,
        string charId, string kind, string? packFolder)
    {
        var byName = GetSeasonFilesForChar(sourceFile, charId);
        if (scan is null) return byName;
        var declared = PackSeasonFiles(scan, packFolder, kind, GameAssetId(charId), charId);
        // 包自己声明的表算数（≥2 张不同的图）：作者写明的四季，比"猜文件名"权威。
        // 实测卡罗琳那个包：按文件名那条会返回"四季都指向同一张"的退化表（非空！），
        // 于是"只在空表时兜底"的老写法被挡在外面 ⇒ 覆盖包四条钉的是同一张图。
        var distinct = declared.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return distinct >= 2 ? declared : byName;
    }

    /// <summary>从包自己声明的「基资产 + When:{Season}」取分季文件。
    /// 返回空表 = 这个包不是这种写法（调用方维持原行为，绝不瞎猜）。
    /// ⚠ packFolder 空 = 官方/原版行（见 SkinOption.PackFolder），这时【一律不查】。
    /// 老写法「全机只有一个包声明了这个资产，那就算它的」会把别家包的分季图借到原版脸上 ——
    /// 而事实上法师/桑迪这类角色机子上往往真就只有一个剧情包写过四季 ⇒ 「默认」那张卡的封面
    /// 变成那个包的动漫脸，可覆盖包钉进游戏的字节仍等于原版 xnb（B63 复现）。
    /// 默认行只准用与源文件同目录的图，那条走 GetSeasonFilesForChar，不碰这张表。</summary>
    private static Dictionary<string, string> PackSeasonFiles(PortraitScanResult scan,
        string? packFolder, string kind, string assetId, string charId)
    {
        var res = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(packFolder)) return res;
        var hits = scan.BaseSeasonPatches.Where(e =>
            string.Equals(e.Pack, packFolder, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(e.File) && File.Exists(e.File));
        foreach (var id in new[] { assetId, charId }.Concat(VanillaAssetAliases(charId)).Distinct())
        {
            var key = kind + "/" + id;
            foreach (var e in hits)
                if (string.Equals(e.Asset, key, StringComparison.OrdinalIgnoreCase))
                    res[e.Season] = e.File;
            if (res.Count > 0) break;
        }
        return res;
    }

    public static Dictionary<string, string> GetSeasonFilesForChar(string? sourceFile, string charId)    {
        if (string.IsNullOrWhiteSpace(sourceFile) || string.IsNullOrWhiteSpace(charId))
            return GetSeasonFiles(sourceFile);
        var cacheKey = sourceFile + "\u241F" + charId;
        var cached = SeasonForCharCache.GetOrAdd(cacheKey, _ => BuildSeasonFilesForChar(sourceFile, charId));
        return new Dictionary<string, string>(cached, StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> BuildSeasonFilesForChar(string sourceFile, string charId)
    {
        var result = GetSeasonFiles(sourceFile);
        try
        {
            var dir = Path.GetDirectoryName(sourceFile);
            if (dir is null || !Directory.Exists(dir)) return result;

            var idCandidates = VanillaAssetAliases(charId).ToList();
            foreach (var (key, suffix) in new[]
                     { ("spring", "_Spring"), ("summer", "_Summer"), ("fall", "_Fall"), ("winter", "_Winter") })
            {
                if (result.ContainsKey(key)) continue;
                foreach (var id in idCandidates)
                {
                    var p = Path.Combine(dir, id + suffix + ".png");
                    if (File.Exists(p)) { result[key] = p; break; }
                    var p2 = Path.Combine(dir, id + suffix + ".xnb");
                    if (File.Exists(p2)) { result[key] = p2; break; }
                }
            }
            if (!result.ContainsKey("winter"))
            {
                foreach (var id in idCandidates)
                {
                    var indoor = Path.Combine(dir, id + "_Winter_Indoor.png");
                    var outdoor = Path.Combine(dir, id + "_Winter_Outdoor.png");
                    if (File.Exists(indoor)) { result["winter"] = indoor; break; }
                    if (File.Exists(outdoor)) { result["winter"] = outdoor; break; }
                }
            }
        }
        catch { }
        return result;
    }

}
