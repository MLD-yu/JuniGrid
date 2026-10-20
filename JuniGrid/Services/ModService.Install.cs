using System.IO;
using System.Text.Json;

namespace JuniGrid.Services;

public sealed partial class ModService
{
    /// <summary>这个包该不该走「直接写游戏本体 Content」那条通道（<see cref="InstallNew"/> 里那段判据，
    /// 抽出来是为了能在沙盒里单独验，不必造压缩包、不必真游戏目录）。
    /// <para>硬前提：<paramref name="anyXnb"/> 为假（整包一张 XNB 都没有）就一律不走 ——
    /// 纯 PNG 包是作者交给 CP 的形状，不是给本体覆盖用的。</para></summary>
    internal static bool OverrideChannelTakes(ContentOverrideService.PlanResult? plan,
        bool anyXnb, bool anyPngUnpaired, bool hasSmapi)
        => plan is not null && anyXnb
           && (plan.Installable || plan.TargetsSeen > 0 || !anyPngUnpaired || !hasSmapi);

    public string? InstallNew(string gamePath, string zipPath, out string? modName, int? nexusModId = null,
        string? requireUniqueId = null, string? portraiturePackName = null, ConfigService? cfg = null,
        List<string>? installNotes = null, IReadOnlyList<NexusRequirement>? packRequirements = null)
    {
        modName = null;
        string? temp = null;
        try
        {
            var manifest = ExtractToTemp(zipPath, "mod-install-", out temp);
            if (manifest is null)
            {
                // 依赖直装（带 requireUniqueId）的候选包没有 manifest，就是"这个包不是我们要的那个 mod"，
                // 交回哨兵值让调用方换下一个候选。绝不能退化成覆盖安装或一条把循环打断的错误文案。
                if (requireUniqueId is not null)
                {
                    TryDelete(temp);
                    return UidMismatchError;
                }
                // v1.2.8：包里带着「写明目标资产的目录」（LooseSprites.png/Cursors.png 这种约定）时，
                // 按目录名转成 CP 包 —— 这条【必须排在肖像转换之前】。旧写法是"没有清单但图 ⇒ 当立绘包"，
                // 于是一个界面包被转成 Portraits/daybg 这类游戏里根本不存在的目标
                //（实测 Starry Sky Interface：13 个文件只写出 5 条假立绘，剩下 8 个静默丢掉）。
                // 只在推出【非立绘家族】的目标时才接管，纯立绘包照旧走下面那条，UID 与立绘页连续性不变。
                var loosePackName = SanitizeFolderName(
                    string.IsNullOrWhiteSpace(portraiturePackName)
                        ? Path.GetFileNameWithoutExtension(zipPath)
                        : portraiturePackName);
                // v1.5.2：覆盖判定整条【提到转换之前】。原来它排在最后，于是只要包里的图
                // 在原版 Content 里有同名资产，就先被"烤进本体 / 转成 CP 包"截走，覆盖那条路
                // 连看都看不到。实机 34635「DaisyNiko's Earthy Recolour - Chinese」：
                // 包内顶层目录名【就是】已装的 [CP] DaisyNiko's Earthy Recolour，
                // 15 张图全在它的 assets/ 下有同名文件 —— 这是整条链里最强的一条证据，
                // 却被 Maps/fall_beach 在原版里也存在这件事压过去，转成了假 CP 包（03:29 重装修复后复现）。
                // 现在顺序是：先问"这是不是谁的覆盖包"，答案是才落盘；不是才轮到转换。
                var declaredOvPlan = ProbeOverlayForDeclaredHost(gamePath, temp!, packRequirements);
                OverlayFail ofail = OverlayFail.NoHostInPack;
                string hostGuess = "";
                var ovPlans = declaredOvPlan is not null ? One(declaredOvPlan)
                             : ProbeOverlay(gamePath, temp!, out ofail, out hostGuess, packRequirements);
                if (ovPlans is not null && cfg is not null)
                {
                    // 一个包可以盖在好几家上（捆绑包的汉化就是这种形状）：一家一份计划、逐家落盘逐家记账。
                    // 某一家被占用就停在那里 —— 已经写进去的那些条条在账上，能从对应那行的卡片里撤。
                    string? oerr = null;
                    foreach (var p in ovPlans)
                    {
                        oerr = ApplyOverlay(gamePath, temp!, p, cfg, nexusModId,
                            loosePackName, out modName, installNotes);
                        if (oerr is not null) break;
                    }
                    TryDelete(temp);
                    return oerr;
                }
                // v1.6.9：「纯净版覆盖」—— 满足下面任一条形状，就直接落游戏本体 Content：
                //  ① 包里的位图全都有同名 XNB 兄弟（含"整包只有 XNB"那种前 SMAPI 时代的包）：
                //     作者本来就是按"换掉某张原版图"画的，转成 CP 包反而多依赖一层 SMAPI，
                //     而这条路上大大小小的 bug 全出在转换那一层；
                //  ② 这台机器【没装 SMAPI】：那种机器上转出来的 CP 包根本不会生效，装它等于装个死重量。
                // 两条都还要加一道前提 ——【包里得有 XNB】。
                // 【一张 XNB 都没有的纯 PNG 包不走这条通道】（2026-10-07 定）：作者只交 PNG，
                // 意思就是"这包靠 Content Patcher 加载"，我们把它烤进游戏本体等于替作者改了发布方式；
                // 而且这个口子一开，"任何 mod 都能被我们另找一条路塞进本体"就挡不住了 ——
                // 没装 SMAPI 用不了 CP 包是事实，不是要绕过去的障碍。
                //（今天上午曾加过第 ③ 条"Prepare 认得出目标就接管"，为的是让扁平 PNG 包在装了 SMAPI
                //  的机器上也有地方去，那条已按上面的理由撤掉；判据与执行仍共用同一份 Prepare 结果。）
                ImageShapeOf(temp!, out var anyXnb, out var anyPngUnpaired);
                var cplan = cfg is not null ? ContentOverrideService.Prepare(gamePath, temp!) : null;
                if (OverrideChannelTakes(cplan, anyXnb, anyPngUnpaired, GameService.HasSmapiLoader(gamePath)))
                {
                    if (cplan.Installable)
                    {
                        // srcDir = temp：落盘前先把完整原包（结构一点不变的 mod）收进游戏目录旁，
                        // 「打开文件夹」打开的就是这份，而不是扁平的逐资产副本。
                        var cerr = ContentOverrideService.Commit(gamePath, cplan, loosePackName, nexusModId, cfg, out modName, temp);
                        TryDelete(temp);
                        if (cerr is null) modName = loosePackName;
                        return cerr;
                    }
                    // 认得出这是"冲着本体某张原版图画的包"、却一张都对不上 ⇒ 就地拒绝并点名，
                    // 绝不退给转换那条路。退回去就是当年那个 bug 的形状：13 个文件转成 5 条假立绘，
                    // 剩下的静默丢掉，而安装结果一片祥和。
                    if (cplan.TargetsSeen > 0)
                    {
                        TryDelete(temp);
                        modName = null;
                        return LocService.Tr("这个包的图对不上游戏本体当前版本的尺寸，已放弃安装，你原来装的什么都没动：")
                            + string.Join("；", cplan.Rejects.Take(6));
                    }
                }
                if (HasForeignAssetTarget(gamePath, temp!))
                {
                    var aerr = InstallLooseAssetPackAsCp(gamePath, temp!, loosePackName, nexusModId, out modName);
                    TryDelete(temp);
                    if (aerr is null) modName = loosePackName;
                    return aerr;
                }
                // 覆盖判定在转换之前就跑过了（见上面 ovPlans）：它说不是覆盖包，下面这几条
                // 转换通道才有权接管；全都没接管，就用它那次的判定解释"为什么放弃"。
                // 宿主其实认出来了、本机也装着，而包里的文件与那个 mod 自己的文件【一张都不同名】——
                // 这既不是"判断不出宿主"也不是"你还没装"，说那两句都是撒谎。
                // 实机 30539：登记前置 #16220，本机 [CP] Childhood Sweetheart Caroline 就是它，
                // 但包里 21 张叫 Portraits/Aerobics.png，宿主自己 30 张叫 Portraits/Caroline_Aerobics.png
                // —— 同名 0/21，盖上去一张都不会替换（是给另一个版本的这个 mod 画的）。
                if (cfg is not null && ovPlans is null
                    && !Directory.EnumerateFiles(temp!, "*.json", SearchOption.AllDirectories).Any()
                    && FindInstalledRequirementHost(gamePath, packRequirements) is string installedHost)
                {
                    TryDelete(temp);
                    modName = null;
                    return LocService.Tf("这个包登记的前置是「{0}」，本机也装着它 —— 可包里的文件跟那个 mod 自己的文件对不上路径，盖上去不会替换任何东西（多半是给另一个版本的它画的），已放弃安装。", installedHost);
                }
                // cfg 为 null 时连账都记不了，宁可不装（没有记录 = 不可逆的静默改动）
                TryDelete(temp);
                modName = null;
                if (cfg is null)
                    return LocService.Tr("这个压缩包没有 manifest.json，且当前流程无法为覆盖安装留下还原记录，已放弃安装。");
                return OverlayRefusal(ofail, gamePath, hostGuess, SoleHostCandidate(packRequirements));
            }
            var modRoot = ResolveModRoot(temp, out var allManifests);
            var isBundle = allManifests.Length > 1;

            // v1.2.0：依赖直装防呆 —— 候选包必须真的提供期望的 UniqueID（捆绑包任一
            // 子包命中即可，与 InstallUpdate 的 expectedUniqueId 语义一致）。
            if (requireUniqueId is not null && !ManifestsContainUid(allManifests, requireUniqueId))
            {
                TryDelete(temp);
                modName = null;
                return UidMismatchError;
            }

            // v1.1.5：与"没有 manifest 就拒绝"同一策略——manifest 是坏 JSON / 空文件 /
            // 根不是对象的也拒绝安装（否则会静默装上 SMAPI 无法加载的孤儿条目）。
            // ManifestJson 选项已容忍 SMAPI 风格的注释/尾逗号。
            foreach (var mf in allManifests)
            {
                try
                {
                    using var checkDoc = JsonDocument.Parse(ReadManifestText(mf), ManifestJson);
                    if (checkDoc.RootElement.ValueKind != JsonValueKind.Object)
                        throw new JsonException("root is not an object");
                }
                catch (Exception __checkEx)
                {
                    AppLog.Warn("ModService", "manifest 校验失败: " + __checkEx.Message);
                    return LocService.Tr("压缩包里的 manifest.json 已损坏或为空，不是完整的 mod，已放弃安装");
                }
            }

            try
            {
                using var doc = JsonDocument.Parse(ReadManifestText(manifest));
                if (doc.RootElement.TryGetProperty("Name", out var n))
                    modName = n.GetString();
            }
            catch (Exception __ex) { AppLog.Warn("ModService", __ex.Message); }

            // v1.2.4：带 manifest 的包也可能是「同一个 mod 的汉化重打包」（作者把整个目录重发一遍、
            // manifest 原封不动）。这种要按文件盖回它对应的那个已装目录，绝不能当成第二个 mod
            // 装到别处 —— 同 UniqueID 会互相判重吃掉，捆绑包结构还会被拆散。
            var prettyName = modName;
            string? uidTwin = null;
            if (cfg is not null && allManifests.Length == 1 && requireUniqueId is null
                && ProbeTranslatedRepackage(gamePath, temp!, modRoot, manifest, nexusModId, cfg.Current, out uidTwin) is { } rep)
            {
                var rerr = ApplyOverlay(gamePath, temp!, rep, cfg, nexusModId,
                    SanitizeFolderName(Path.GetFileNameWithoutExtension(zipPath)), out _);
                TryDelete(temp);
                modName = prettyName;      // 卡片标题用 manifest 里的名字，不用 zip 文件名
                return rerr;
            }
            // 撞 UID 的另一个 mod：既不覆盖也不装，原地停下。继续走正常安装会触发「同 UID 判重清扫」，
            // 把用户已装的另一个 mod 整个移进回收站 —— 那比覆盖更糟（覆盖至少还能从覆盖账里退回）。
            if (uidTwin is not null)
            {
                TryDelete(temp);
                modName = null;
                return LocService.Tf("这个 mod 与已装的「{0}」用了同一个 UniqueID，而两边素材不是同一套（不是它的汉化包）。"
                    + "同一个 UniqueID 在游戏里只能生效一个，所以已放弃安装 —— 你装的那份一个字节都没动。"
                    + "想换成这一份，先到 Mods 页把「{0}」卸载或禁用。", uidTwin);
            }

            if (isBundle)
            {
                // 捆绑包的展示名取所有子包名的共同部分。子包名都带 "[BL] "/"[CC] "
                // 这类标记前缀，剥掉之后再取公共前缀（直接按字符取会在 '[' 后一个
                // 字符就分叉，得到空串 → 退化成第一个子包名 "[BL] Downtown Zuzu"）
                var names = allManifests.Select(m => SafeManifestName(m))
                    .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList();
                var stripped = names.Select(n => StripPackTag(n).Trim()).ToList();
                var common = CommonPrefix(stripped).Trim().TrimEnd('-', '_').Trim();
                if (common.Length > 1)
                    modName = common;
                else
                {
                    // v1.1.5：子包名没有公共前缀（如 [CC] Buildings/Streets/Props）时，
                    // 回退为 zip 内的顶层目录名，而不是带标签的单个子包名。
                    // 不用 zip 文件名兜底——Nexus 直装链路的 zip 名是 direct-{id}-{fileId}。
                    var topName = Path.GetFileName(modRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (modRoot != temp && !string.IsNullOrWhiteSpace(topName))
                        modName = topName;
                }
            }

            var folderName = ChooseFolderName(Path.GetFileName(modRoot), modRoot == temp, modName, isBundle, zipPath);

            var newUids = PackUniqueIds(isBundle ? allManifests : new[] { manifest });

            // v1.5.2：同名旧目录移进回收站之前，先看它自己声明的是不是同一个 mod。
            // 原来这一步【只看文件夹名】—— 两个 UniqueID 完全不同的 mod 只要显示名撞车
            // （同一个 mod 被换作者重传、或两个作者都叫 "Cursors"），你已装那份就会被
            // 无声无息地扫进 Mods\.junigrid_trash，新那份顶上它的名字。测试舱实测：
            // 装 Name=HostOne / UniqueID=Some.Other 的包，已装的 HostOne / Host.One 整个进了回收站。
            // 这比"盖错文件"更糟，所以这里直接拒装，一个字节都不动。
            var clash = DifferentModInThatFolder(gamePath, folderName, newUids);
            if (clash is not null)
            {
                TryDelete(temp);
                modName = null;
                return LocService.Tf("这个 mod 与已装的「{0}」要用同一个文件夹名，但两边 manifest 的 UniqueID 不同 —— 它们是两个不同的 mod。已放弃安装，你装的那份一个字节都没动。", clash);
            }

            // v1.1.1：同名旧目录（启用/禁用两份）不再直接删除 —— 先移入回收站，安装失败回滚
            var dest = Path.Combine(gamePath, "Mods", folderName);
            string? stagedDest = null, stagedDisabled = null;
            if (Directory.Exists(dest))
            {
                try { stagedDest = StageExistingToTrash(gamePath, dest); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { TryDelete(temp); return $"「{folderName}」正被占用，无法备份旧版，已放弃安装（旧版未动）"; }
            }
            // v0.71.9：同名【禁用】目录（.folderName）也要清掉 —— 旧逻辑只查不带点的 dest，
            // 禁用 mod（Mods/.X）+ 新下载（Mods/X）会同时存在，扫描出来就是同一个 mod 两行。
            var destDisabled = Path.Combine(gamePath, "Mods", "." + folderName);
            if (Directory.Exists(destDisabled))
            {
                try { stagedDisabled = StageExistingToTrash(gamePath, destDisabled); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (stagedDest is not null) RestoreStaged(stagedDest, dest);
                    TryDelete(temp);
                    return $"「.{folderName}」正被占用，无法备份旧版，已放弃安装（旧版未动）";
                }
            }
            // v0.71.9：按 manifest UniqueID 兜底判重 —— 文件夹名不同但 UniqueID 相同
            // （如 ABC / ABC-1.2 / .ABC）也属于同一个 mod，一并清理，防任何形式的重复项。
            // v1.1.4：捆绑包收集全部子包的 UID —— 旧版安装器漏装产生的散装子包
            // （如 Mods 下单独的 "[BL] Downtown Zuzu"）也要一并收进回收站。
            try
            {
                if (newUids.Count > 0)
                {
                    // v1.1.5：子包级清扫（与 CleanupDuplicateCopies 的赢家语义不同——
                    // 新装的 zip 就是赢家，2.3 规则要求两套判定分开实现，不复用函数）。
                    // 基于 ScanRaw 条目遍历：天然排除 .junigrid_trash（修复"把回收站移进
                    // 回收站自身"）、天然带禁用状态；命中 UID 的旧子包目录移入回收站
                    // （保留层级），unique 子包与无关 mod 一律不动；命中目录=顶层本身
                    // （独立 mod）才整层移动。旧写法按顶层枚举+整层移动，会把与新版
                    // 捆绑包共享任一公共子包 UID 的其他捆绑包整个端走（T7 实测）。
                    foreach (var old in ScanRaw(gamePath))
                    {
                        if (old.Disabled || string.IsNullOrWhiteSpace(old.UniqueID)) continue;
                        if (!newUids.Contains(old.UniqueID.Trim())) continue;
                        // 安装目标本身（同名冲突已在上面处理），不再清扫
                        if (string.Equals(old.Folder, folderName, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(old.Folder, "." + folderName, StringComparison.OrdinalIgnoreCase))
                            continue;
                        var oldDir = Path.Combine(gamePath, "Mods", old.Folder.Replace('/', Path.DirectorySeparatorChar));
                        if (!Directory.Exists(oldDir)) continue;
                        try
                        {
                            var stagedDup = old.Folder.Contains('/')
                                ? StageSubpackageToTrash(gamePath, old.Folder)
                                : StageExistingToTrash(gamePath, oldDir);
                            AppLog.Warn("Mods", $"[判重清理] 同 UniqueID 旧副本子包 {old.Folder} 已移入回收站：{Path.GetFileName(stagedDup)}");
                        }
                        catch (Exception stageEx)
                        {
                            AppLog.Warn("Mods", $"[判重清理] {old.Folder} 移入回收站失败（可能被占用），保留原样: {stageEx.Message}");
                        }
                    }
                }
            }
            catch (Exception __ex) { AppLog.Warn("ModService", "UniqueID 判重清理失败: " + __ex.Message); }

            try
            {
                if (modRoot == temp)
                    CopyDirectoryContents(temp, dest);   // files at zip root — copy into named folder
                else
                    MoveDirectorySafe(modRoot, dest);   // 跨盘保护
            }
            catch
            {
                // v1.1.1：安装失败 → 清掉残缺半成品，把回收站里的旧版原路移回
                try { if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true); } catch { }
                if (stagedDest is not null) RestoreStaged(stagedDest, dest);
                if (stagedDisabled is not null) RestoreStaged(stagedDisabled, destDisabled);
                throw;
            }

            TryDelete(temp);
            // v1.1.2：把安装来源（Nexus modId）写进安装根的边车文件。SVE 这类包的 manifest
            // 会把 UpdateKeys 留成无效占位 "Nexus:???"，扫描侧凭 manifest 拿不到 ID，
            // 「已安装」识别与更新检查就断了 —— 边车兜底；重装/更新时随目录被新版覆盖。
            if (nexusModId is int nid)
                WriteNexusIdSidecar(dest, nid);
            return null;
        }
        catch (Exception ex)
        {
            if (temp is not null) TryDelete(temp);
            return ex.Message;
        }
    }
}
