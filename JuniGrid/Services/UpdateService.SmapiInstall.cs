using System.IO;
using System.IO.Compression;

namespace JuniGrid.Services;

public sealed partial class UpdateService
{
    // ------------------------------------------------------------------
    // Run the official SMAPI installer — 全自动无人值守
    // ------------------------------------------------------------------
    /// <summary>
    /// 下载官方安装包，后台静默执行 SMAPI.Installer.exe
    /// （--install --no-prompt --game-path），全程不弹任何窗口、不跳浏览器。
    /// 返回 null 表示成功；否则返回错误消息。
    /// SMAPI 安装器原生支持无人值守参数（见 InteractiveInstaller 源码）：
    ///   --no-prompt   禁用交互询问
    ///   --install     执行安装/更新
    ///   --game-path   指定游戏目录（跳过自动探测）
    /// </summary>
    public async Task<string?> RunSmapiInstallerAsync(
        SmapiUpdateInfo info, string? gamePath,
        IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        string? temp = null;
        try
        {
            SweepSmapiTemps();   // 清掉历次安装遗留的解压目录（只留最新一份）
            GuardSmapiVersionMatchesGame(gamePath, info.LatestVersion);
            if (info.InstallerZipUrl is null)
                throw new InvalidOperationException("没找到 SMAPI 安装包下载地址");
            if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
                throw new InvalidOperationException("还没设置游戏目录 —— 先到「设置」页选择");
            ct.ThrowIfCancellationRequested();

            // v0.2.1：走统一缓存目录（默认仍在 LocalAppData）。不直接放 %TEMP%：
            // Defender 对 %TEMP% 里的 .NET 运行时 DLL 扫描更激进，经常写入瞬间挂锁。
            // 目录名带时间戳避免撞旧缓存。
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            temp = Path.Combine(
                StoragePaths.SmapiInstallerDir,
                $"{info.LatestVersion ?? "latest"}-{stamp}");
            Directory.CreateDirectory(temp);

            // 下载前先备份玩家 Mods，这样下载/解压/安装的任何一步出问题
            // 都不影响原始目录；安装完成后会把备份合并回 Mods 并删掉临时备份。
            // v1.08：备份是几百 MB 的整目录复制，必须离开 UI 线程（旧版同步复制，
            // 点下下载整个界面冻结到备份结束）；进度条同时提示备份阶段。
            progress?.Report(new InstallProgress(LocService.Tr("正在备份现有 Mods 目录…"), 0, 0));
            var modsBackup = await Task.Run(() => BackupMods(gamePath), ct);
            if (modsBackup is not null)
                progress?.Report(new InstallProgress(LocService.Tr("已备份现有 Mods 目录，开始下载 SMAPI…"), 0, 0));

            progress?.Report(new InstallProgress(LocService.Tr("正在下载 SMAPI 安装包…")));
            progress?.Report(new InstallProgress(LocService.Tr("连接下载服务器…"), 0, 0));
            // v1.1.7：半截包放稳定路径 —— 暂停后继续可续传，不再每次新建时间戳目录导致断点丢失
            var zip = Path.Combine(StoragePaths.SmapiInstallerDir,
                $"smapi-dl-{(info.LatestVersion ?? "latest").Replace('/', '-')}.zip");

            // v1.5：本机已有同名安装包 → 跳过网络下载。有该版本抽屉就存在抽屉里（跟版本走），
            // 没有就存在通用安装包缓存的 keep 子目录（见 GetSmapiInstallerCacheDir）。
            var ver = GetGameVersion(gamePath);
            var cachedDir = DepotDownloaderService.GetSmapiInstallerCacheDir(ver);
            var cachedZip = Path.Combine(cachedDir, Path.GetFileName(new Uri(info.InstallerZipUrl).LocalPath));
            if (File.Exists(cachedZip) && new FileInfo(cachedZip).Length > 1024)
            {
                progress?.Report(new InstallProgress(LocService.Tr("使用本机已缓存的 SMAPI 安装包…"), 50, 0));
                File.Copy(cachedZip, zip, overwrite: true);
            }
            else
            {
                await DownloadToFileAsync(info.InstallerZipUrl, zip, progress, ct);
                try
                {
                    Directory.CreateDirectory(cachedDir);
                    File.Copy(zip, cachedZip, overwrite: true);
                }
                catch { /* 缓存失败不影响安装 */ }
            }
            ct.ThrowIfCancellationRequested();

            var err = await InstallSmapiZipAsync(zip, gamePath, progress, info.LatestVersion);
            if (err is null)
            {
                // v1.6.7：装完校验游戏版本没变 —— 安装期间被切版本的话，这份 SMAPI 装进了
                // 别的版本目录，绝不能写进共享池/版本缓存污染其它版本。
                var verAfter = GetGameVersion(gamePath);
                if (!string.Equals(verAfter, ver, StringComparison.OrdinalIgnoreCase))
                    return $"SMAPI {info.LatestVersion ?? ""} 安装完成，但期间游戏版本已从 {ver} 切到 {verAfter}，" +
                           "当前目录里的 SMAPI 与游戏不匹配 —— 请重新执行一次 SMAPI 安装";
                // 装完把 SMAPI 产物写进当前版本 staging，下次切回免重装
                try { DepotDownloaderService.CacheSmapiForCurrentGame(gamePath); } catch { }
                try { if (File.Exists(zip)) File.Delete(zip); } catch { }
            }
            return err;
        }
        catch (OperationCanceledException)
        {
            // 暂停：稳定路径上的半截包 smapi-dl-*.zip 保留供续传（临时解压目录由 finally 清）
            throw;
        }
        catch (Exception ex)
        {
            // modsBackup 仍保留，方便用户手动恢复；临时目录由 finally 清
            return LocService.Tr("SMAPI 自动安装失败：") + ex.Message;
        }
        finally { CleanSmapiTemp(temp); }
    }

    /// <summary>删掉本次安装解压出来的临时目录。成功路径以前根本不删（本机就是这么堆出
    /// 11 个时间戳目录 / 218 MB 的），现在成功/失败/取消三条出口都走这里；
    /// 删不动（Defender 挂锁、被占用）必须留一行 WARN —— 静默失败等于只进不出且没人看得见。</summary>
    private static void CleanSmapiTemp(string? temp)
    {
        if (string.IsNullOrWhiteSpace(temp) || !Directory.Exists(temp)) return;
        try { Directory.Delete(temp, true); }
        catch (Exception ex)
        {
            AppLog.Warn("Smapi", $"SMAPI 安装临时目录没能删掉（下次安装前会再扫一次）：{temp} — {ex.Message}");
        }
    }

    /// <summary>清扫 smapi-installer 下遗留的「&lt;版本&gt;-&lt;时间戳&gt;」解压目录：没有任何代码会再读它们，
    /// 只保留 mtime 最新的一份（可能正被另一个并发安装用着）。
    /// ⚠ 半截续传包 smapi-dl-*.zip 不在此列 —— 暂停/继续要靠它，绝不能顺手清掉。</summary>
    private static void SweepSmapiTemps()
    {
        try
        {
            var root = StoragePaths.SmapiInstallerDir;
            if (!Directory.Exists(root)) return;
            var stale = Directory.EnumerateDirectories(root)
                .Where(d => System.Text.RegularExpressions.Regex.IsMatch(
                    Path.GetFileName(d), @"-\d{8}_\d{6}$"))
                .OrderByDescending(d => { try { return Directory.GetLastWriteTimeUtc(d); } catch { return DateTime.MinValue; } })
                .Skip(1)
                .ToList();
            foreach (var d in stale)
            {
                try { Directory.Delete(d, true); }
                catch (Exception ex) { AppLog.Warn("Smapi", "清理遗留的 SMAPI 解压目录失败: " + d + " — " + ex.Message); }
            }
            if (stale.Count > 0)
                AppLog.Warn("Smapi", $"已清理 {stale.Count} 个遗留的 SMAPI 解压临时目录");
        }
        catch { }
    }

    /// <summary>
    /// v1.3.2：从【已下载好的 SMAPI 官方安装器 zip】静默安装 —— 供两条来源复用：
    /// ① GitHub 最新版（RunSmapiInstallerAsync 下载后调这里）；
    /// ② Nexus 上用户手动挑选的任意历史版本（nxm 回流，如 SMAPI 4.5.1 —— Nexus 的
    ///    SMAPI 包本身就是官方安装器，内部结构与 GitHub release zip 相同）。
    /// 完成 备份 Mods → 解压 → install.dat 手动安装 → 恢复 Mods 全流程。
    /// 返回 null 表示成功；versionLabel 仅用于进度文案与临时目录命名（可为 null）。
    /// </summary>
    /// <summary>SMAPI 安装的底层闸门。所有入口（Mods 页两个按钮、.nxm 通道、切换后补装、
    /// 以后新增的任何入口）最终都汇到 RunSmapiInstallerAsync / InstallSmapiZipAsync，
    /// 校验放这一层才堵得住 —— 1.01 的目录里被装进 1.6 专用的 SMAPI 4.5.2，
    /// 就是因为判断只写在 UI 入口、底层不看。</summary>
    private static void GuardSmapiVersionMatchesGame(string? gamePath, string? versionToInstall)
    {
        var gameVer = ReadLocalGameVersion(gamePath);
        if (string.IsNullOrWhiteSpace(gameVer))
        {
            AppLog.Warn("SMAPI", "读不出当前游戏版本，跳过 SMAPI 兼容性闸门（不阻塞安装）");
            return;
        }
        var tag = RecommendSmapiTag(gameVer);
        if (tag is null)
            throw new InvalidOperationException(
                $"游戏 {gameVer} 没有适配的 SMAPI，拒绝安装 " +
                (string.IsNullOrWhiteSpace(versionToInstall) ? "该版本" : versionToInstall) +
                " —— 装上会在启动时崩（跨版本残留）。想玩 mod 请先切到 1.2.30 及以上版本。");

        var got = (versionToInstall ?? "").Trim().TrimStart('v', 'V');
        if (got.Length == 0) return;                                  // 调用方没报版本 → 不拦
        if (tag.Trim().Equals("latest", StringComparison.OrdinalIgnoreCase)) return;  // 1.6+ 就该用最新
        if (got.StartsWith(tag.Trim(), StringComparison.OrdinalIgnoreCase)) return;   // 3.7.3 ≈ 3.7.3.x
        throw new InvalidOperationException(
            $"要装的 SMAPI {got} 与游戏 {gameVer} 不匹配（该版本该用 {tag}）—— 已拒绝安装。");
    }

    public async Task<string?> InstallSmapiZipAsync(
        string zip, string? gamePath,
        IProgress<InstallProgress>? progress = null, string? versionLabel = null)
    {
        string? temp = null;
        string? modsBackup = null;
        try
        {
            if (!File.Exists(zip))
                throw new InvalidOperationException("SMAPI 安装包不存在：" + zip);
            if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
                throw new InvalidOperationException("还没设置游戏目录 —— 先到「设置」页选择");
            GuardSmapiVersionMatchesGame(gamePath, versionLabel);

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            temp = Path.Combine(
                StoragePaths.SmapiInstallerDir,
                $"{versionLabel ?? "manual"}-{stamp}");
            Directory.CreateDirectory(temp);

            progress?.Report(new InstallProgress(LocService.Tr("正在备份现有 Mods 目录…"), 0, 0));
            modsBackup = await Task.Run(() => BackupMods(gamePath));
            if (modsBackup is not null)
                progress?.Report(new InstallProgress(LocService.Tr("已备份现有 Mods 目录…"), 0, 0));

            progress?.Report(new InstallProgress(LocService.Tr("正在解压安装包…")));
            await Task.Run(() => ExtractWithRetryAsync(zip, temp, progress));   // v1.08：离开 UI 线程

            // 兜底：如果这个 zip 是「double-zipped」外壳，解出来还是 zip，自动再解一层。
            var innerZip = Directory
                .GetFiles(temp, "*.zip", SearchOption.AllDirectories)
                .FirstOrDefault(f => !string.Equals(f, zip, StringComparison.OrdinalIgnoreCase));
            if (innerZip is not null)
            {
                progress?.Report(new InstallProgress(LocService.Tr("检测到内层压缩包，正在再次解压…")));
                await Task.Run(() => ExtractWithRetryAsync(innerZip, temp, progress));   // v1.08
            }

            // SMAPI 4.5.x 的官方安装器有个已知问题：它在 --install --no-prompt 模式
            // 下仍会调用 Console.Clear()。如果安装器进程 stdout/输入句柄不是真实控制台
            // （JuniGrid 做 WPF 后台进程时正是这种情况）， Console.Clear() 会抛
            // "句柄无效" IOException，安装直接失败。反复弹黑窗也仍可能失败。
            //
            // 所以这里改为按 SMAPI README 的手动安装步骤，直接从官方安装包里的
            // internal/windows 目录安装：官方 README 明确支持把 install.dat 解压复制到
            // 游戏目录，这完全无需 console，也是启动器更可靠的做法。
            // v1.1.8：2.x / 3.0.x / 3.7.x 与 4.5.2 同一条管线 —— 统一递归找 install.dat。
            // v1.1.9：SMAPI 2.5 没有 install.dat —— internal/Windows/ 本身就是解开的载荷。
            progress?.Report(new InstallProgress(LocService.Tr("正在后台安装 SMAPI（手动安装官方文件）…"), 90));

            string? dat = null;
            // ① 新结构：SMAPI {ver} installer/internal/windows/install.dat
            if (versionLabel is not null)
            {
                var expected = Path.Combine(temp, "SMAPI " + versionLabel + " installer", "internal", "windows", "install.dat");
                if (File.Exists(expected)) dat = expected;
            }
            // ② 老结构：internal/windows-install.dat（3.9.x / 2.11 / 3.0.1 等）
            if (dat is null)
            {
                var oldDat = Directory
                    .GetFiles(temp, "*-install.dat", SearchOption.AllDirectories)
                    .FirstOrDefault(f => f.EndsWith("windows-install.dat", StringComparison.OrdinalIgnoreCase));
                if (oldDat is not null) dat = oldDat;
            }
            // ③ 兜底：任意路径下的 install.dat，或大于 500KB 的 .dat（改名安装载荷）
            if (dat is null)
            {
                dat = Directory.GetFiles(temp, "install.dat", SearchOption.AllDirectories)
                    .OrderByDescending(f => f.Length)
                    .FirstOrDefault()
                    ?? Directory.GetFiles(temp, "*.dat", SearchOption.AllDirectories)
                        .Select(f => new FileInfo(f))
                        .Where(f => f.Length > 500 * 1024)
                        .OrderByDescending(f => f.Length)
                        .Select(f => f.FullName)
                        .FirstOrDefault();
            }
            // ④ SMAPI 2.5 等：没有 .dat，internal/Windows 已是解开的 StardewModdingAPI + Mods
            string? preExtractedWindows = null;
            if (dat is null)
            {
                preExtractedWindows = Directory
                    .EnumerateDirectories(temp, "*", SearchOption.AllDirectories)
                    .Where(d => string.Equals(Path.GetFileName(d), "Windows", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(Path.GetFileName(d), "windows", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault(d => File.Exists(Path.Combine(d, "StardewModdingAPI.exe")));
            }
            if (dat is null && preExtractedWindows is null)
                throw new InvalidOperationException(
                    "解压后没找到 SMAPI 安装载荷（install.dat / windows-install.dat / internal/Windows 均不存在）");

            string payloadRoot;
            if (dat is not null)
            {
                var extracted = Path.Combine(temp, "smapi-files");
                Directory.CreateDirectory(extracted);
                progress?.Report(new InstallProgress(LocService.Tr("正在解压安装内容…"), 95));
                await Task.Run(() => ExtractWithRetryAsync(dat, extracted, progress));   // v1.08
                payloadRoot = FindStardewApiRoot(extracted)
                    ?? throw new InvalidOperationException("SMAPI 安装包内没有 StardewModdingAPI.exe（安装包异常）");
            }
            else
            {
                // 2.5 载荷已就位，无需再解压
                progress?.Report(new InstallProgress(LocService.Tr("正在写入 SMAPI 文件…"), 95));
                payloadRoot = preExtractedWindows!;
            }

            // 先清掉旧 SMAPI 文件再复制新文件（避免文件锁定/残留版本文件）。
            // v1.08：复制/清理都是上百 MB 的磁盘工作，不能冻 UI。
            await Task.Run(() => CopySmapiBundle(payloadRoot, gamePath));

            // 更新结束后保留游戏自带的 deps.json、runtimeconfig.json，SMAPI 的
            // StardewModdingAPI.deps.json 需要在游戏主文件基础上生成/覆盖。
            var gameDeps = Path.Combine(gamePath, "Stardew Valley.deps.json");
            if (File.Exists(gameDeps))
                File.Copy(gameDeps, Path.Combine(gamePath, "StardewModdingAPI.deps.json"), true);

            // 安装完成后再把备份的 Mods 复制回去，保证用户 mod 一个不丢；
            // 官方 install.dat 里也带 SMAPI 自带 ConsoleCommands/SaveBackup 这类
            // 默认 mod，所以先恢复备份，再把缺失的默认 mod 补回，绝不整目录覆盖。
            if (modsBackup is not null)
            {
                progress?.Report(new InstallProgress(LocService.Tr("正在恢复 Mod 文件夹…"), 100));
                await Task.Run(() => RestoreMods(modsBackup, Path.Combine(gamePath, "Mods")));   // v1.08：离开 UI 线程
            }
            CopyBuiltinMods(Path.Combine(payloadRoot, "Mods"), Path.Combine(gamePath, "Mods"));

            // 然后删除这次更新产生的临时备份目录。
            if (modsBackup is not null)
            {
                await Task.Run(() => TryDeleteBackup(modsBackup));   // v1.08：删除备份也是大 IO
            }

            progress?.Report(new InstallProgress(string.IsNullOrWhiteSpace(versionLabel)
                ? LocService.Tr("SMAPI 安装完成")
                : LocService.Tf("SMAPI {0} 安装完成", versionLabel), 100, 0));
            Invalidate();
            return null;
        }
        catch (Exception ex)
        {
            // modsBackup 仍保留，方便用户手动恢复；临时目录由 finally 清
            return LocService.Tr("SMAPI 自动安装失败：") + ex.Message;
        }
        finally { CleanSmapiTemp(temp); }
    }
}
