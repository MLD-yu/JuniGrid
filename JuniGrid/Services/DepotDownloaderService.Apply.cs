using System.IO;

namespace JuniGrid.Services;

public sealed partial class DepotDownloaderService
{
    private static void ApplyStagingExclusive(string staging, string gamePath,
        IProgress<Progress>? progress, string? currentGameVersion)
    {
        var t = new PhaseTimer();
        ApplyGate.Wait();
        t.Lap(LocService.Tr("等闸门"));
        Interlocked.Increment(ref _applyActive);
        SetModWatchersEnabled(false);         // Mods 页的 FileSystemWatcher 是常驻句柄，不关它改名必败
        t.Lap(LocService.Tr("挂起读者与监听"));
        try
        {
            // 兜底：先确认这份缓存有没有资格铺进游戏目录，再动手。
            // 原先锚点校验只跑在 ApplyStaging 之后 —— 那时 Mods 已清空、本体已重写，
            // 抛错等于把用户留在半切换状态（实测：同版本 apply 清掉 3 个 mod 之后才报「缓存不完整」）。
            CheckStagingAnchors(staging);
            t.Lap(LocService.Tr("锚点校验"));
            EnsureDiskSpace(staging, gamePath);
            t.Lap(LocService.Tr("磁盘空间"));
            // 有人在往 Mods 里复制 mod 时切版本，那批文件会被算成「源版本的 Mods」一起归档 ——
            // 必须趁游戏目录还没被碰之前拦下来。Task.Run 包一层：这里可能是 UI 线程，静默等待不能睡在 UI 上。
            Task.Run(() => EnsureModsQuiescent(gamePath, progress)).GetAwaiter().GetResult();
            t.Lap(LocService.Tr("等 Mods 停笔"));
            // 存档只有一个目录、所有版本共用，而它会被单向升级（见 SaveVersionService）——
            // 趁游戏目录还没被碰，先把上次收起来的档放回、再给「切过去就回不去」的档留底。
            PrepareSavesFor(staging, progress);
            t.Lap(LocService.Tr("存档准备"));
            ApplyStaging(staging, gamePath, progress, currentGameVersion);
            t.Lap(LocService.Tr("应用本体与 Mods"));
            // 应用完整性：缺关键 Content 时进档会在 NPC 构造里 NRE 闪退（1.0 实测）
            ValidateAppliedGame(gamePath, staging);
            t.Lap(LocService.Tr("应用后校验"));
            // 记下"这个目录现在是被我们铺成哪个发行号的"：XNA 时代（1.0–1.4）的 exe
            // 自报 1.0.61xx，光读文件会把 1.2.x 认成游戏 1.0（判无适配 SMAPI、Mods 不归档）。
            UpdateService.RecordDeployedVersion(gamePath, DeployedLabelOf(staging), ReadManifestMeta(staging));
            t.Lap(LocService.Tr("记部署版本"));
            // XNA 运行库补装原来只挂在 VersionDownloadService 的下载通道上，弹窗
            // 「用本地缓存切换」根本不经过那里 → 1.0–1.4 在新电脑上照样起不来。
            // 下沉到这里 = 所有 apply 路径（弹窗 / 下载 / 后台 / 测试台）统一覆盖。
            // 用 Task.Run 包一层：这里可能是 UI 线程，直接 .Result 会因 SynchronizationContext 死锁。
            Task.Run(() => EnsureXnaForAppliedGameAsync(gamePath, progress)).GetAwaiter().GetResult();
            t.Lap(LocService.Tr("XNA 补装"));
        }
        finally
        {
            SetModWatchersEnabled(true);
            Interlocked.Decrement(ref _applyActive);
            try { ApplyGate.Release(); } catch (SemaphoreFullException) { }
            // 抛异常时也报：少了哪几段就说明停在哪一段，比只看一行错误文本好定位
            t.Report("DepotDownloader", LocService.Tf("版本切换阶段耗时（{0}）：", Path.GetFileName(staging)));
        }
    }

    /// <summary>
    /// 切换前后的存档两步：放回上次暂存的 → 给「会被新版本升上去」的留底。
    /// 切换前后的存档：放回上次收起来的 → 留底 → **把这个版本读不了的收起**（游戏列表只显示读得了的）。
    /// 整段尽力而为：存档这一步失败绝不拦下切换 —— 本体切过去比留底重要，失败只落日志。
    /// </summary>
    private static void PrepareSavesFor(string staging, IProgress<Progress>? progress)
    {
        void Log(string m) => AppLog.Warn("存档", m);
        try
        {
            var target = DeployedLabelOf(staging);
            // 差集放回：只放回这个版本读得了的。读不了的留在抽屉原地
            var back = SaveVersionService.RestoreHidden(Log, target);
            // 不做升级前留底：用高版本存盘后回不去，后果用户自担
            var hidden = SaveVersionService.HideUnreadable(target, staging, Log);
            foreach (var left in SaveVersionService.UnreadableBy(target))
                SaveVersionService.ForceHideToFocus(left, Log);
            // 不上屏：收起是常态动作、对玩家无操作价值，弹一行只是噪音（用户 2026-09-23 明确要求去掉）。
            // 每份的「已收起」和这条汇总都进日志，要查的人去日志看。
            if (hidden > 0)
                AppLog.Info("存档", $"本版读不了的 {hidden} 份存档已收起（仅日志，不上屏）");
            if (back > 0)
                progress?.Report(new Progress(LocService.Tf("放回了之前收起来的 {0} 份存档", back), null));
        }
        catch (Exception ex)
        {
            AppLog.Warn("存档", "切换前的存档处理没做完（不影响本次切换）：" + ex.Message);
        }
    }

    private static async Task EnsureXnaForAppliedGameAsync(string gamePath, IProgress<Progress>? progress)
    {
        try
        {
            var ver = TryReadGameVersion(gamePath);
            if (!XnaRedistService.GameNeedsXna(ver) || XnaRedistService.IsInstalled()) return;
            progress?.Report(new Progress(LocService.Tr("这台机器缺 XNA 4.0 运行库（1.0–1.4 必需），正在安装…"), null));
            var err = await XnaRedistService.EnsureInstalledAsync(
                (msg, pct) => progress?.Report(new Progress(msg, pct is null ? null : (int?)Math.Round(pct.Value))));
            if (err is not null)
            {
                progress?.Report(new Progress(LocService.Tr("XNA 运行库安装失败：") + err, null));
                AppLog.Warn("DepotDownloader", "补装 XNA 失败: " + err);
            }
        }
        catch (Exception ex) { AppLog.Warn("DepotDownloader", "补装 XNA 异常: " + ex.Message); }
    }

    /// <summary>动手前确认目标盘装得下：② 是「先删本体、再写本体」，空间不够就是删得掉
    /// 写不回，现场比不切换更糟。体积按写入时记录的 size cache 估，估不出就跳过
    /// （不因为算不出来而挡住正常切换），另留 256 MB 余量给临时文件。</summary>
    private static void EnsureDiskSpace(string staging, string gamePath)
    {
        long need = ReadSizeCache(staging);
        if (need <= 0) return;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(gamePath));
            if (string.IsNullOrEmpty(root)) return;
            var free = new DriveInfo(root).AvailableFreeSpace;
            if (free >= need + 256L * 1024 * 1024) return;
            throw new DepotException(
                $"{root} 剩余 {free / 1024 / 1024} MB，不够写入这版本体（约 {need / 1024 / 1024} MB + 256 MB 余量）。" +
                "已中止切换，游戏目录未被改动。请清理磁盘，或在设置里把缓存目录换到空间充足的盘后重试。");
        }
        catch (DepotException) { throw; }
        catch { /* 拿不到盘符等异常不阻塞正常切换 */ }
    }

    /// <summary>缓存锚点校验，动手前跑（也在校验末尾再跑一遍）。检查项按游戏版本区分 ——
    /// NPCDispositions.xnb 是 1.0–1.5 的数据；1.6+ 已重构 Content，官方包里没有该文件，
    /// 不能拿来判「缓存不完整」。</summary>
    private static void CheckStagingAnchors(string staging)
    {
        var is16Plus = IsGameVersion16OrNewer(TryReadGameVersion(staging));
        var anchors = new List<string>
        {
            "Stardew Valley.exe",
            Path.Combine("Content", "Characters", "Abigail.xnb"),
            Path.Combine("Content", "Portraits", "Abigail.xnb"),
            // 1.0–1.5：NPC 配置表；1.6+：用 Data 下 Achievements 代替（该文件两代都有/1.6 必有）
            Path.Combine("Content", "Data", is16Plus ? "Achievements.xnb" : "NPCDispositions.xnb"),
        };

        var cacheMiss = anchors.Where(r => !File.Exists(Path.Combine(staging, r))).ToList();
        if (cacheMiss.Count > 0)
            throw new DepotException(
                $"版本缓存不完整（{staging}），缺少：{string.Join("、", cacheMiss)}。" +
                "请在版本管理里删除该版本本地缓存后重新下载。");
    }

    /// <summary>应用后校验完整性，缺失就抛错，避免用户进档才闪退。只查锚点文件
    /// （staging 与游戏目录各测一遍）—— 全量清单比对要各扫一遍上万个小文件，
    /// 是「直接应用」后半段卡顿主因，已移除。</summary>
    private static void ValidateAppliedGame(string gamePath, string staging)
    {
        CheckStagingAnchors(staging);
        CheckStagingAnchors(gamePath);
    }

    private static bool IsGameVersion16OrNewer(string? ver)
    {
        if (string.IsNullOrWhiteSpace(ver)) return false;
        var parts = ver.Split('.');
        var maj = parts.Length > 0 && int.TryParse(parts[0], out var ma) ? ma : 0;
        var min = parts.Length > 1 && int.TryParse(
            new string(parts[1].TakeWhile(char.IsDigit).ToArray()), out var mi) ? mi : 0;
        return maj > 1 || (maj == 1 && min >= 6);
    }

    /// <summary>这个 manifest 是不是已经有一份完整可切换的缓存包（只看，不应用）。</summary>
    public bool IsStagedComplete(string manifestId)
    {
        var staging = FindStagingDir(manifestId);
        return staging is not null && Directory.Exists(staging) && HasGameBinary(staging)
            && IsStagedPackageComplete(staging);   // 半截包不算「有完整缓存」
    }

    public bool TryApplyStaged(string gamePath, string appId, string depotId, string manifestId,
        IProgress<Progress>? progress = null, string? currentGameVersion = null)
    {
        var staging = FindStagingDir(manifestId) ?? Path.Combine(StagingRoot, $"{appId}-{depotId}-{manifestId}");
        if (!Directory.Exists(staging)) return false;
        if (!HasGameBinary(staging)) return false;
        // 没确认下完的包不铺进游戏目录：缺角色贴图时进档会直接空引用闪退。
        if (!IsStagedPackageComplete(staging)) return false;

        progress?.Report(new Progress(LocService.Tr("使用本地缓存，直接应用…"), null));
        ApplyStagingExclusive(staging, gamePath, progress, currentGameVersion);
        return true;
    }


    /// <summary>收存当前本体，但把游戏目录里的【共享存储】留在原地。
    /// 共享存储 = <c>&lt;游戏&gt;\JuniGrid</c>：纯净版覆盖的原版仓库、素材包原样备份、我们写进去
    /// 那份的副本。它记的是"这张替图长什么样"，与哪个版本现役无关；而
    /// <see cref="TryAdoptCurrentBody"/> 是整目录改名（同盘）或整目录复制（跨盘），
    /// 不先挪走就会被收进版本包里，下次切回该版本时又被当成这个版本自带的东西。
    /// 挪到游戏目录的【同级】—— 与游戏目录必然同盘，改名≈瞬时，不会退化成复制。</summary>
    private static string? AdoptCurrentBodyWithSharedStore(string gamePath, string targetStaging,
        string currentGameVersion, IProgress<Progress>? progress)
    {
        var parked = ParkSharedStore(gamePath);
        try { return TryAdoptCurrentBody(gamePath, targetStaging, currentGameVersion, progress); }
        finally { RestoreSharedStore(gamePath, parked); }
    }

    /// <summary>把共享存储挪到游戏目录同级。返回挪去的落点；null = 没挪（那儿根本没有这个文件夹，
    /// 或挪不动）。挪不动时【不拦切换】—— 让它跟着版本包走就是改造前的行为，
    /// 比因为一个缓存文件夹搬不动而拒绝换版本更好交代；东西还在，只是位置不对。</summary>
    private static string? ParkSharedStore(string gamePath)
    {
        var store = ContentOverrideService.SharedStoreDir(gamePath);
        var aside = SharedStoreAside(gamePath);
        try
        {
            var hasStore = Directory.Exists(store);
            var hasAside = Directory.Exists(aside);
            if (hasAside)
            {
                if (hasStore)
                {
                    AppLog.Warn("DepotDownloader",
                        $"共享存储有两份（游戏目录里和 {aside}），本次两份都不动");
                    return null;
                }
                Directory.Move(aside, store);      // 上次中断留下的：先放回，再走正常流程
                hasStore = true;
            }
            if (!hasStore) return null;
            Directory.Move(store, aside);
            return aside;
        }
        catch (Exception ex)
        {
            AppLog.Warn("DepotDownloader", "共享存储没能挪出来，本次它会跟着版本包走：" + ex.Message);
            return null;
        }
    }

    private static void RestoreSharedStore(string gamePath, string? aside)
    {
        if (aside is null) return;
        var store = ContentOverrideService.SharedStoreDir(gamePath);
        try
        {
            Directory.CreateDirectory(gamePath);
            // 游戏目录里已经有一份（版本包里带回来的那份）：绝不拿挪走这份去盖它 ——
            // 两份内容谁新说不准，留在原地至少看得见，盖错了就没了。
            if (Directory.Exists(store))
            {
                AppLog.Warn("DepotDownloader",
                    $"共享存储没放回：{store} 已有一份，挪走的那份仍完整留在 {aside}");
                return;
            }
            Directory.Move(aside, store);
        }
        catch (Exception ex)
        {
            AppLog.Warn("DepotDownloader", $"共享存储放回失败，它还在 {aside}：{ex.Message}");
        }
    }

    private static string SharedStoreAside(string gamePath)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(
               gamePath.TrimEnd(Path.DirectorySeparatorChar, '/')))!, ".junigrid-shared-store");

    /// <summary>把当前游戏目录整个收成「当前版本」的缓存包：补 manifest 元数据 → 让包自含 SMAPI。
    /// 同盘＝改名（瞬时、零拷贝）；跨盘＝复制＋逐文件校验，<b>校验过了才删源</b>。
    /// 返回包路径＝已收好；返回 null＝这次不收（该版本已有包，或改名被占用/权限挡住，
    /// 那种情况下 ② 自己也会因占用报错，不会假装成功）。
    /// <b>跨盘复制做不到时抛 <see cref="DepotException"/> 拦住本次切换。</b>老代码在这里是
    /// "失败就 return null"，于是 ② 把旧本体删了 —— 切换显示成功，用户下次切回来才发现
    /// 要重下 ~660 MB，而且中间没有任何地方告诉过他"这个版本没被保留"（2026-10-03 实测连丢两次）。
    /// 静默删是不可接受的：存不下就该停下来问，而不是替用户决定丢什么。</summary>
    private static string? TryAdoptCurrentBody(string gamePath, string targetStaging,
        string currentGameVersion, IProgress<Progress>? progress)
    {
        // 包名前缀抄目标包（413150-413151-…）：ApplyStaging 手上没有 appId/depotId，
        // 而这两者本来就来自同一份 Steam 配置，抄现成的最不容易错。
        var targetName = Path.GetFileName(targetStaging.TrimEnd(Path.DirectorySeparatorChar, '/'));
        var seg = targetName.Split('-');
        var prefix = seg.Length >= 3 ? seg[0] + "-" + seg[1] : "413150-413151";
        var parts = prefix.Split('-');
        var pkg = Path.Combine(StagingRoot, prefix + "-" + SanitizeFolderName(currentGameVersion));
        if (Directory.Exists(pkg)) return null;
        // StagingRoot 的 getter 只算路径不建目录 —— 全新缓存（刚装完机、刚换过缓存目录）时
        // 父目录不存在，Directory.Move 会抛 DirectoryNotFoundException 被下面当成"不收"，
        // 于是 ② 把旧本体删掉：这跟跨盘无关，是"第一次切版本必然丢一次"。先把父目录建出来。
        try { Directory.CreateDirectory(Path.GetDirectoryName(pkg)!); } catch { }

        // Steam 清单要在移动之前找：包落在缓存盘，从那儿向上溯找不到 steamapps。
        string? manifest = null;
        try
        {
            manifest = SteamService.ReadInstalledDepotManifest(
                SteamService.FindAppManifest(gamePath, parts[0]), parts[1]);
        }
        catch { }

        var srcRoot = Path.GetPathRoot(Path.GetFullPath(gamePath)) ?? "";
        var destRoot = Path.GetPathRoot(pkg) ?? "";
        var sameVol = string.Equals(srcRoot, destRoot, StringComparison.OrdinalIgnoreCase);

        if (sameVol)
        {
            try
            {
                progress?.Report(new Progress(LocService.Tf("正在把当前本体收进版本 {0} 的缓存…", currentGameVersion), null));
                Directory.Move(gamePath, pkg);
            }
            catch (Exception ex)
            {
                AppLog.Warn("DepotDownloader",
                    $"当前本体未能收进版本包（{ex.Message}）—— 游戏目录未动，本次切换按旧行为覆盖");
                return null;
            }
        }
        else
        {
            var need = StorageService.DirSize(gamePath);
            long free;
            try { free = new DriveInfo(destRoot).AvailableFreeSpace; } catch { free = 0; }
            if (!CrossVolumeFits(need, free))
                throw new DepotException(LocService.Tf(
                    "版本缓存和游戏不在同一个盘：收存当前版本要复制 {0}，缓存盘只剩 {1}，放不下。"
                    + "本次切换已停止，你的游戏一个文件都没动 —— 到「设置 → 存储」把缓存目录挪到游戏所在的盘就能切。",
                    Human(need), Human(free)));

            progress?.Report(new Progress(LocService.Tf(
                "正在把当前版本复制到缓存盘（跨盘，约 {0}，可能要几分钟）…", Human(need)), null));
            try { ParallelCopyDirectory(gamePath, pkg, skipTrash: false, progress, "收进版本缓存"); }
            catch (Exception ex)
            {
                DeleteHalfPackage(pkg);
                throw new DepotException(LocService.Tf(
                    "复制当前版本到缓存盘失败：{0}。本次切换已停止，你的游戏一个文件都没动。", ex.Message));
            }
            var missing = VerifyTree(gamePath, pkg);
            if (missing is not null)
            {
                DeleteHalfPackage(pkg);
                throw new DepotException(LocService.Tf(
                    "复制到版本缓存后校验没过（{0}）。本次切换已停止，你的游戏一个文件都没动。", missing));
            }
            // 校验过了才动源。删不干净也不要紧：② 会把游戏目录里的残留扫进 .junigrid-old-body 再清，
            // 而包已经是完整的一份，不会因此丢东西。
            try { DeleteTreeParallel(gamePath); }
            catch (Exception ex)
            {
                AppLog.Warn("DepotDownloader", "跨盘复制已完成，清理源目录时还有残留（不影响版本包）：" + ex.Message);
            }
        }

        if (!string.IsNullOrWhiteSpace(manifest)) WriteManifestMeta(pkg, manifest!);
        // 包自含 SMAPI（v1.6.8 口径）：④ 恢复时优先用包内这份，共享池只兜底。
        if (IsCompleteSmapiSnapshot(pkg) && !IsCompleteSmapiSnapshot(Path.Combine(pkg, "smapi")))
            CopySmapiFiles(pkg, Path.Combine(pkg, "smapi"));
        WriteSizeCache(pkg);
        // 归档来的这份就是现役本体，天然完整（缺东西的话游戏本身就跑不起来）。
        TryWriteCompleteMark(pkg);
        AppLog.Warn("DepotDownloader",
            $"当前本体已收进版本包 {pkg}（manifest={manifest ?? "未知"}，{(sameVol ? "同盘改名" : "跨盘复制")}）"
            + " —— 切回该版本不必再回 Steam 校验");
        return pkg;
    }

    /// <summary>跨盘收存前"装不装得下"：留 256 MB 余量（复制途中还要写临时文件、robocopy 也要地方）。
    /// need 读不出来（0）时放行 —— 宁可复制失败后拦住，也不能因为量不出来就说不行。</summary>
    internal static bool CrossVolumeFits(long needBytes, long freeBytes) =>
        needBytes <= 0 || freeBytes >= needBytes + 256L * 1024 * 1024;

    /// <summary>源里每一个文件都必须在目标里且大小一致。null＝通过；否则一句人话说明缺什么。
    /// 这条是"删源之前唯一的凭据"，所以宁可严：空目录也算不过。</summary>
    internal static string? VerifyTree(string src, string dest)
    {
        int n = 0;
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            n++;
            var rel = Path.GetRelativePath(src, f);
            var to = Path.Combine(dest, rel);
            long len;
            try { len = new FileInfo(f).Length; } catch { continue; }
            if (!File.Exists(to)) return LocService.Tf("目标里找不到 {0}", rel);
            long got;
            try { got = new FileInfo(to).Length; }
            catch (Exception ex) { return LocService.Tf("{0} 读不到大小：{1}", rel, ex.Message); }
            if (got != len) return LocService.Tf("{0} 大小不一致（源 {1}，副本 {2}）", rel, len, got);
        }
        return n == 0 ? LocService.Tr("源目录里一个文件都没数到") : null;
    }

    private static void DeleteHalfPackage(string pkg)
    {
        try { if (Directory.Exists(pkg)) DeleteTreeParallel(pkg); }
        catch (Exception ex) { AppLog.Warn("DepotDownloader", "半截版本包没删掉 " + pkg + "：" + ex.Message); }
    }

    private static string Human(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? $"{bytes / 1024.0 / 1024 / 1024:0.0} GB"
            : $"{bytes / 1024.0 / 1024:0} MB";

    /// <summary>
    /// v1.5：切换版本时 Mods / SMAPI 按版本隔离。
    /// ① 当前游戏目录里的 Mods/SMAPI 存进「当前版本」staging + SMAPI 共享池
    /// ② 用目标 staging 覆盖游戏本体（仍跳过 Mods）
    /// ③ 用目标 staging 的 Mods 替换游戏 Mods（无则建空目录）
    /// ④ 恢复 SMAPI：目标 staging 有则用之；否则从共享池按兼容桶恢复（切版本免重装）
    /// </summary>
    private static void ApplyStaging(string staging, string gamePath, IProgress<Progress>? progress,
        string? currentGameVersion)
    {
        var gameMods = Path.Combine(gamePath, "Mods");
        var targetMods = Path.Combine(staging, "Mods");
        var targetSmapi = Path.Combine(staging, "smapi");
        // 同盘 Mods 用改名迁移：整棵 Move 近似瞬时，避免 GB 级全量拷贝；
        // 改名失败会回落"拷贝 + 核对 + 删源"，语义仍然是移动，所以只记一个落点。
        string? modsMovedTo = null;
        // ① 的移动没通过核对 → 记下原因，①/①a 结束后立刻中止切换（游戏目录还没被碰过）
        string? modsMoveError = null;
        // ③ 结束时「游戏 Mods 顶层应当只有这些名字」的基线，用于回收在途复制后落的文件
        var allowedLiveMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // ⓪ 归档出来的包：② 万一失败时要把它报给用户（旧版本整份还在里面）
        string? adoptedPkg = null;

        var curStaging = FindStagingByVersionLabel(currentGameVersion);
        var gameModsHasEntries = Directory.Exists(gameMods)
            && Directory.EnumerateFileSystemEntries(gameMods).Any();
        // 兜底：目标版本 == 当前版本时，「游戏里的 Mods」本身就是「这个版本的 Mods」——
        // ①/①a 会刻意跳过搬运（目录不能搬给自己），③ 若照旧清空再铺快照，用户在 Steam
        // 官方目录里手装的 mod 就原地消失且零备份（沙箱实测：3 个 → 0 个）。
        var sameVersionApply = curStaging is not null
            && string.Equals(Path.GetFullPath(curStaging), Path.GetFullPath(staging),
                StringComparison.OrdinalIgnoreCase);

        // ⓪ 当前版本还没有包 → 把整个游戏目录改名成它的包（同盘改名≈瞬时，且要么全成要么不动）。
        // 官方原版用户第一次切走就是这个场景：以前 ② 直接把旧本体删掉，想回官方最新版只能
        // 回 Steam 校验（用户的原话是"不应该上来就把用户下载的内容自动缓存起来吗"）。
        // 只在「这个版本可以有 SMAPI」时整目录收 —— Mods 跟着一起进包才是它自己的 Mods；
        // 无 SMAPI 的版本（1.0/1.1）仍走 ① 的 _no-smapi 路径，免得把上一版本残留写进 1.0 的抽屉。
        if (!sameVersionApply && curStaging is null
            && !string.IsNullOrWhiteSpace(currentGameVersion)
            && UpdateService.RecommendSmapiTag(currentGameVersion) is not null
            && Directory.Exists(gamePath))
        {
            var adopted = AdoptCurrentBodyWithSharedStore(gamePath, staging, currentGameVersion!, progress);
            if (adopted is not null)
            {
                curStaging = adopted;
                adoptedPkg = adopted;
                Directory.CreateDirectory(gamePath);   // ②/③ 接下来按"空目录"往里写目标版本
            }
        }

        // ① 当前 Mods → 当前版本的 staging（按真实版本号找目录）。
        // 1.0/1.1 等无适配 SMAPI 的版本：不把上一版本残留写进该版本缓存，
        // 否则切回 1.0 会「恢复」根本不属于它的 1.6 Mods。
        try
        {
            var canUseMods = UpdateService.RecommendSmapiTag(currentGameVersion) is not null;
            var hasGameMods = gameModsHasEntries;

            if (hasGameMods && !canUseMods)
            {
                if (!HasMovableEntries(gameMods))
                {
                    // 只剩回收站就别建批次目录：2026-09-19 23:27 留下的 Mods-20260919_232740
                    // 是个 0 项空桶（源里只有 .junigrid_trash，被 skipTrash 跳掉）
                    AppLog.Warn("DepotDownloader",
                        $"当前版本无 SMAPI（{currentGameVersion}），Mods 里只剩回收站目录，不生成空批次");
                }
                else
                {
                    // 带时间戳：这个位置从不自动放回游戏，若固定用同一个路径，下一次切换的
                    // 移动会先把上一批整棵删掉（用户 2026-09-19 问的「备份是否被其它备份污染」
                    // 在这里成立）。每批各留一份，由存储清理页回收。
                    var leftover = Path.Combine(StagingRoot, "_mods-orphan", "_no-smapi",
                        SafeDirName(currentGameVersion ?? "unknown"),
                        "Mods-" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                    if (MoveDirectoryVerified(gameMods, leftover, null, progress, "移出 Mods") == DirMove.Failed)
                        modsMoveError =
                            LocService.Tf("当前版本（{0}）没有适配的 SMAPI，必须先把 Mods 移出游戏目录，", currentGameVersion) +
                            LocService.Tr("但移动没通过核对 —— 已保留原目录未动。请清理磁盘空间、或关闭正在占用 Mods 的程序后重试。");
                    else modsMovedTo = leftover;
                    AppLog.Warn("DepotDownloader",
                        $"当前版本无 SMAPI（{currentGameVersion}），Mods 已移出游戏目录到 {leftover}，不写入该版本缓存");
                }
            }
            else if (HasMovableEntries(gameMods)   // 必须有真 mod：只剩回收站时若照旧存入，
                                                   // MoveAsideForeignEntries 会把抽屉里 112 个真 mod
                                                   // 当「不属于本次移动」全扫进 _copyleft（09-23 事故）
                && curStaging is not null
                && !string.Equals(Path.GetFullPath(curStaging), Path.GetFullPath(staging), StringComparison.OrdinalIgnoreCase))
            {
                var destMods = Path.Combine(curStaging, "Mods");
                // 兜底：源和抽屉对不上（半截/竞态）→ 跳过存入，抽屉原封不动
                if (LooksUnlikeSnapshot(gameMods, destMods))
                {
                    AppLog.Warn("DepotDownloader",
                        $"存入 Mods 跳过：当前 Mods 与 {currentGameVersion} 抽屉几乎对不上（疑似半截/竞态），抽屉未动");
                    progress?.Report(new Progress(
                        LocService.Tf("当前 Mods 与版本 {0} 的缓存对不上，已跳过存入（缓存里的 mod 未动）", currentGameVersion), null));
                }
                else
                {
                    progress?.Report(new Progress(LocService.Tf("正在把当前 Mods 存入版本 {0}…", currentGameVersion), null));
                    // 抽屉里可能躺着上一批还没被认领走的条目（改名快路径会连整个 dest 删掉）→ 先挪隔离区
                    var grave = Path.Combine(StagingRoot, "_mods-orphan", "_copyleft",
                        SafeDirName(currentGameVersion ?? "unknown") + "-" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                    if (MoveDirectoryVerified(gameMods, destMods, grave, progress, "存入 Mods") == DirMove.Failed)
                        modsMoveError =
                            LocService.Tf("把当前 Mods 存入版本 {0} 的缓存时核对不通过 —— 已保留游戏目录里的 Mods 未动。", currentGameVersion) +
                            LocService.Tr("请清理磁盘空间或关闭占用 Mods 的程序后重试；实在不行先到 Mods 页手动备份。");
                    else
                    {
                        modsMovedTo = destMods;
                        AppLog.Warn("DepotDownloader", $"Mods 已移入 {curStaging}");
                        WriteSizeCache(curStaging);
                    }
                }
            }
        }
        catch (Exception ex) { AppLog.Warn("DepotDownloader", "存当前 Mods 失败: " + ex.Message); }

        // ① 的移动没兑现（拷贝后核对不过，源被完整保留）→ 现在就停。后面 ②/③ 一定会清游戏目录，
        // 让一份没核对过的副本去顶现役 Mods，正是"mod 自己没了"的成因。此时游戏目录一个字节没动。
        if (modsMoveError is not null)
        {
            AppLog.Warn("DepotDownloader", modsMoveError);
            throw new DepotException(modsMoveError);
        }

        // ①a 有对应版本但还没 staging（或官方目录）时，Mods 没地方收 —— 存进孤儿备份
        try
        {
            var canUseMods = UpdateService.RecommendSmapiTag(currentGameVersion) is not null;
            if (canUseMods
                && Directory.Exists(gameMods) && HasMovableEntries(gameMods)
                && FindStagingByVersionLabel(currentGameVersion) is null)
            {
                var orphanMods = Path.Combine(StagingRoot, "_mods-orphan",
                    SafeDirName(currentGameVersion ?? "unknown"), "Mods");
                var grave = Path.Combine(StagingRoot, "_mods-orphan", "_copyleft",
                    SafeDirName(currentGameVersion ?? "unknown") + "-" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                if (MoveDirectoryVerified(gameMods, orphanMods, grave, progress, "备份 Mods") == DirMove.Failed)
                {
                    AppLog.Warn("DepotDownloader", "Mods 移进孤儿备份时核对不通过，已保留游戏目录未动");
                    throw new DepotException(
                        $"把 Mods 移进孤儿备份 {orphanMods} 时核对不通过 —— 已保留游戏目录里的 Mods 未动，" +
                        "请清理磁盘空间或关闭占用 Mods 的程序后重试。");
                }
                modsMovedTo = orphanMods;
                AppLog.Warn("DepotDownloader", $"Mods 已移入孤儿备份 {orphanMods}");
            }
        }
        catch (DepotException) { throw; }
        catch (Exception ex) { AppLog.Warn("DepotDownloader", "存孤儿 Mods 失败: " + ex.Message); }

        // ①d 兜底：②/③ 接下来一定会清掉游戏 Mods。若 ①/①a 什么都没落地
        // （搬迁与拷贝双双抛错时只留下一行「存当前 Mods 失败」就继续往下），这份 Mods 就真没了。
        // 清盘前最后确认一次：还在游戏里、又没有任何已落地的备份 → 保底拷一份；
        // 连保底都失败 → 中止切换，别动游戏目录。
        // 判据用 HasMovableEntries 而不是 gameModsHasEntries：只剩回收站时 ①/①a 刻意不生成批次，
        // 这里若还算「有条目」，保底就拷出一个空目录 → 判"备份目录为空" → 把正常切换拦死。
        if (!sameVersionApply && modsMovedTo is null && HasMovableEntries(gameMods))
        {
            var safety = Path.Combine(StagingRoot, "_mods-orphan", "_safety",
                SafeDirName(currentGameVersion ?? "unknown") + "-" + DateTime.Now.ToString("yyyyMMdd_HHmmss"), "Mods");
            string? safetyErr = null;
            try
            {
                ParallelCopyDirectory(gameMods, safety, skipTrash: true, progress, "保底备份 Mods");
                if (!Directory.Exists(safety) || !Directory.EnumerateFileSystemEntries(safety).Any())
                    safetyErr = LocService.Tr("备份目录为空");
            }
            catch (Exception ex) { safetyErr = ex.Message; }

            if (safetyErr is not null)
                throw new DepotException(
                    $"当前 Mods 无法备份到 {safety}（{safetyErr}），已中止切换 —— 游戏目录未被改动。" +
                    "请检查缓存所在磁盘的剩余空间与写入权限后重试。");
            modsMovedTo = safety;
            AppLog.Warn("DepotDownloader", $"Mods 保底快照已写入 {safety}");
        }

        // ①b 清盘前把当前 SMAPI 打进共享池 + 当前版本包。
        // v1.6.8：版本包自包含 —— <staging>\smapi 与 ④ 的 targetSmapi 对应，
        // 恢复时优先用包内这份，共享池降级为兜底（用户诉求：每个版本的缓存内容
        // 都收在自己文件夹里，公共区只留真正跨版本的东西）。
        try { SaveSmapiToPool(gamePath, currentGameVersion); } catch { }
        try { SeedSmapiSampleMods(gamePath); } catch { }   // 趁 Mods 还是当前这套，先收 SMAPI 示例 mod
        try
        {
            if (curStaging is not null && IsCompleteSmapiSnapshot(gamePath)
                && !IsCompleteSmapiSnapshot(Path.Combine(curStaging, "smapi")))
            {
                var pkgSmapi = Path.Combine(curStaging, "smapi");
                try { if (Directory.Exists(pkgSmapi)) Directory.Delete(pkgSmapi, true); } catch { }
                CopySmapiFiles(gamePath, pkgSmapi);
                AppLog.Warn("DepotDownloader", $"SMAPI 已存入版本包 {pkgSmapi}");
            }
        }
        catch (Exception ex) { AppLog.Warn("DepotDownloader", "存 SMAPI 到版本包失败: " + ex.Message); }

        // ①c userdata 备份（节流 + 只留最近几份）
        try { BackupSteamUserData("413150"); }
        catch (Exception ex)
        {
            AppLog.Warn("DepotDownloader", "备份 Steam userdata 失败: " + ex.Message);
        }

        // ② 清理并写入本体。失败时若 Mods 已改名迁出，先迁回。
        try
        {
            progress?.Report(new Progress(LocService.Tr("正在清理旧游戏文件…"), null));
            // 整棵改名挪走（同盘≈瞬时）—— 以前逐文件 Delete Content 上万个小文件是切换最慢的一步。
            // 后台再慢慢删挪走的那棵，不挡用户。
            var oldBody = Path.Combine(gamePath, ".junigrid-old-body");
            try { if (Directory.Exists(oldBody)) DeleteTreeParallel(oldBody); } catch { }
            Directory.CreateDirectory(oldBody);
            foreach (var entry in Directory.EnumerateFileSystemEntries(gamePath))
            {
                var name = Path.GetFileName(entry);
                if (name.Equals("Mods", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals(".junigrid-old-body", StringComparison.OrdinalIgnoreCase)) continue;
                // 共享存储（纯净版覆盖的原版仓库 / 素材包 / 副本）全版本共用，不是"旧本体"的一部分。
                // 走到这里的是【该版本已有包】的路径 —— 没有 ⓪ 的整目录改名来把它收走，
                // 不豁免就等于第二次离开这个版本时把它扫进 .junigrid-old-body 删掉。
                if (name.Equals(ContentOverrideService.SharedStoreFolder, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var dest = Path.Combine(oldBody, name);
                    if (Directory.Exists(entry)) Directory.Move(entry, dest);
                    else File.Move(entry, dest);
                }
                catch (Exception ex)
                {
                    // 改名失败再删，语义仍是清掉旧文件
                    try
                    {
                        if (Directory.Exists(entry)) DeleteTreeParallel(entry);
                        else File.Delete(entry);
                    }
                    catch (Exception ex2)
                    {
                        throw new DepotException($"无法删除 {name}（文件可能被 Steam/游戏占用，请先退出后重试）：{ex2.Message}");
                    }
                    _ = ex;
                }
            }
            _ = Task.Run(() =>
            {
                try { if (Directory.Exists(oldBody)) DeleteTreeParallel(oldBody); } catch { }
            });

            progress?.Report(new Progress(LocService.Tr("正在写入目标版本文件…"), null));
            CopyGameBodyParallel(staging, gamePath, progress);
        }
        catch
        {
            if (modsMovedTo is not null && !Directory.Exists(gameMods))
            {
                try { TryRelocateDirectory(modsMovedTo, gameMods); } catch { }
            }
            if (adoptedPkg is not null)
                AppLog.Warn("DepotDownloader",
                    $"切换中断：旧版本整份保存在 {adoptedPkg}，在版本管理里点那个版本号就能原样放回");
            throw;
        }

        // ③ 恢复目标版本的 Mods：同盘优先从 staging 整棵改名进来
        progress?.Report(new Progress(LocService.Tr("正在恢复该版本的 Mods…"), null));
        try
        {
            // 「该版本能不能有 Mods」必须提到分支之前判：无适配 SMAPI 的版本（1.0/1.1…）
            // 即使 staging 里躺着一份 Mods（历史上被误存进来的），也绝不能铺回游戏目录 ——
            // 否则 1.0 会显示/加载 1.6 的 mod（实测首页 143 个）。
            var targetVer = TryReadGameVersion(staging);
            var targetCanUseMods = UpdateService.RecommendSmapiTag(targetVer) is not null;

            var targetHasMods = targetCanUseMods
                && Directory.Exists(targetMods)
                && Directory.EnumerateFileSystemEntries(targetMods).Any();
            if (sameVersionApply && gameModsHasEntries)
            {
                // 原地保留：游戏里的就是这版本的，现有条目全都算「合法」
                if (Directory.Exists(gameMods))
                    foreach (var n in Directory.EnumerateFileSystemEntries(gameMods))
                        allowedLiveMods.Add(Path.GetFileName(n));
                AppLog.Warn("DepotDownloader",
                    $"目标与当前版本一致（{currentGameVersion}），Mods 保持游戏目录现状：不清空、也不用快照覆盖（{targetMods} 原样留着）");
            }
            else if (targetHasMods)
            {
                foreach (var n in Directory.EnumerateFileSystemEntries(targetMods))
                    allowedLiveMods.Add(Path.GetFileName(n));
                if (Directory.Exists(gameMods)) ClearDirectoryParallel(gameMods);
                if (Directory.Exists(gameMods))
                {
                    try { Directory.Delete(gameMods, true); } catch { }
                }
                if (MoveDirectoryVerified(targetMods, gameMods, null, progress, "恢复 Mods") == DirMove.Failed)
                {
                    // 到这里本体和 SMAPI 已经换成目标版本了，中止只会更糟 —— 说清楚就好：
                    // 残缺的落点被挪开，源（该版本抽屉）完整保留，mod 一个没丢
                    AppLog.Warn("DepotDownloader", $"恢复 Mods 核对不通过，源完整保留在 {targetMods}");
                    progress?.Report(new Progress(
                        LocService.Tf("该版本的 Mods 没能完整恢复到游戏目录（源仍完整保留在 {0}）—— ", targetMods) +
                        LocService.Tr("请清理磁盘空间或关闭占用 Mods 的程序后，重新切换一次这个版本。"), null));
                }
                else WriteSizeCache(staging);
            }
            else
            {
                // 目标版本没有可用 Mods → 游戏目录必须先清空。
                // 同盘「改名迁移」失败时会走拷贝（源目录还在），若这里只 CreateDirectory
                // 不清盘，上一版本的 Mods 会原样留在 1.0 这类空版本上（实测 143 个）。
                if (Directory.Exists(gameMods))
                {
                    ClearDirectoryParallel(gameMods);
                    try { Directory.Delete(gameMods, true); } catch { }
                }
                Directory.CreateDirectory(gameMods);

                if (!targetCanUseMods)
                {
                    AppLog.Warn("DepotDownloader",
                        $"目标版本无 SMAPI（{targetVer ?? "?"}），Mods 保持为空");
                    if (Directory.Exists(targetMods)
                        && Directory.EnumerateFileSystemEntries(targetMods).Any())
                        AppLog.Warn("DepotDownloader",
                            $"该版本缓存里有历史误存的 Mods（{targetMods}），已忽略未恢复");
                }
                else
                {
                    var orphanMods = FindOrphanModsDir(targetVer);
                    if (orphanMods is not null)
                    {
                        foreach (var n in Directory.EnumerateFileSystemEntries(orphanMods))
                            allowedLiveMods.Add(Path.GetFileName(n));
                        if (MoveDirectoryVerified(orphanMods, gameMods, null, progress, "恢复 Mods") == DirMove.Failed)
                        {
                            AppLog.Warn("DepotDownloader", $"从孤儿备份恢复 Mods 核对不通过，源保留在 {orphanMods}");
                            progress?.Report(new Progress(
                                LocService.Tf("孤儿区的 Mods 没能完整恢复（源仍保留在 {0}）—— 请清理磁盘后重新切换一次。", orphanMods), null));
                        }
                        else AppLog.Warn("DepotDownloader", $"Mods 已从孤儿备份恢复 {orphanMods}");
                    }
                    else
                    {
                        AppLog.Warn("DepotDownloader",
                            $"目标版本无缓存 Mods，游戏 Mods 已清空（game={targetVer ?? "?"}）");

                        // unknown 黑洞兜底：currentGameVersion 读不出来时 Mods 被收进
                        // _mods-orphan 下的 unknown 目录，而恢复是按目标版本号查的 → 永远查不到
                        // （2026-09-18 08:31 实测发生过）。这里不自动并回 —— 自动并回正是当年
                        // 1.0 长出 114 个 1.6 mod 的成因；只把它报到界面上让人自己决定。
                        try
                        {
                            var blind = Path.Combine(StagingRoot, "_mods-orphan", "unknown", "Mods");
                            if (Directory.Exists(blind))
                            {
                                var pending = Directory.EnumerateFileSystemEntries(blind).Count();
                                if (pending > 0)
                                {
                                    progress?.Report(new Progress(
                                        LocService.Tf("另有 {0} 项 mod 被收在 unknown 区（当时读不出版本号），未自动放回：{1}", pending, blind), null));
                                    AppLog.Warn("DepotDownloader",
                                        $"unknown 孤儿区有 {pending} 项待认领：{blind}");
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
        }
        catch (Exception ex) { AppLog.Warn("DepotDownloader", "恢复 Mods 失败: " + ex.Message); }

        // ③z 在途复制的收尾回收：闸门只保证「按下切换的那一刻起 1.2 秒没动静」，
        // 复制暂停后又恢复就会把文件落进「新版本」的 Mods。必须放在 ④ 之前 ——
        // SMAPI 快照自带 Mods\ConsoleCommands 这类条目，④ 之后再比基线会把它们当成外来文件抢走。
        if (!sameVersionApply)
        {
            try
            {
                ReclaimInFlightMods(gameMods, allowedLiveMods,
                    modsMovedTo, currentGameVersion, progress);
            }
            catch (Exception ex) { AppLog.Warn("DepotDownloader", "在途 Mods 回收失败: " + ex.Message); }
        }


        // 移动语义没完全兑现时（改名失败 → 拷贝 → 源壳子被占用删不掉）屏幕上是没有痕迹的：
        // 抽屉里已经有一份完整的，游戏目录里还躺着删不掉的旧目录。必须报出来，
        // 否则下一次切换会拿它当"这个版本新装的 mod"再归档一遍。
        var stuck = Directory.Exists(gameMods)
            ? Directory.EnumerateFileSystemEntries(gameMods)
                .Select(e => Path.GetFileName(e.TrimEnd(Path.DirectorySeparatorChar)))
                .Where(n => !n.Equals(".junigrid_trash", StringComparison.OrdinalIgnoreCase)
                            && !allowedLiveMods.Contains(n))
                .ToList()
            : new List<string>();
        if (stuck.Count > 0)
        {
            AppLog.Warn("DepotDownloader",
                $"切换完成，但游戏 Mods 里还有 {stuck.Count} 项没被认领：" + string.Join("、", stuck));
            // 不上屏：这只是"原地留了个删不掉的壳"，不影响生效；要查就查上面那行日志。
        }

        // ④ 恢复 SMAPI —— 必须跟目标游戏版本走：
        //    1.6+ 才恢复 modern 池；1.2–1.5 恢复对应 legacy 桶；
        //    1.0/1.1 无适配 SMAPI，绝不能把 1.6 的 4.x 装回去。
        var targetGameVer = TryReadGameVersion(staging);
        var smapiTag = UpdateService.RecommendSmapiTag(targetGameVer);
        var restoredFrom = (string?)null;
        if (smapiTag is not null)
        {
            if (Directory.Exists(targetSmapi))
            {
                if (!IsCompleteSmapiSnapshot(targetSmapi))
                {
                    // 旧版逻辑攒下的残缺缓存（裸 exe）—— 清掉，让位给共享池/切换后补装
                    try { Directory.Delete(targetSmapi, true); } catch { }
                }
                else
                {
                    progress?.Report(new Progress(LocService.Tr("正在恢复该版本的 SMAPI…"), null));
                    try
                    {
                        // 走 ParallelCopyDirectory：同盘时硬链接、跨盘时 robocopy /MT，
                        // 都比原来逐文件串行 File.Copy 快，且与「直接应用本体/共享池」同一条口径。
                        ParallelCopyDirectory(targetSmapi, gamePath, skipTrash: false, progress, "恢复 SMAPI");
                        restoredFrom = "版本缓存";
                    }
                    catch (Exception ex) { AppLog.Warn("DepotDownloader", "恢复 SMAPI 失败: " + ex.Message); }
                }
            }
            if (restoredFrom is null && RestoreSmapiFromPool(gamePath, targetGameVer))
            {
                progress?.Report(new Progress(LocService.Tr("正在放回本机已有的 SMAPI（不用重新下载）…"), null));
                restoredFrom = "共享池";
            }
        }

        if (restoredFrom is not null)
        {
            AppLog.Warn("DepotDownloader", $"SMAPI 已恢复（来源：{restoredFrom}，游戏 {targetGameVer}）");
            // 示例 mod 在 Mods/ 里，不跟 SMAPI exe 走 —— 切完补上，避免「能跑 SMAPI 却没有 Console Commands」
            EnsureSmapiSampleMods(gamePath);
        }
        else if (smapiTag is null)
            AppLog.Warn("DepotDownloader", $"游戏 {targetGameVer} 无适配 SMAPI，切换后不装加载器");
        else
        {
            // 该版本有适配 SMAPI，但版本缓存和共享池都没能给出来，而 ② 已经把原来那份删了。
            // 静默过去的后果是用户点「启动」才发现（实测两次「未检测到 SMAPI」）→ 当场说。
            progress?.Report(new Progress(
                LocService.Tr("该版本的 SMAPI 未能恢复（缓存与共享池均不可用），请到 Mods 页重装 SMAPI 再启动"), null));
            AppLog.Error("DepotDownloader",
                $"游戏 {targetGameVer} 应有 SMAPI（{smapiTag}）但版本缓存与共享池都没恢复出来，需重装 SMAPI");
        }
    }

}
