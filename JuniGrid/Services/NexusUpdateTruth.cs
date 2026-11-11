using System.IO;

namespace JuniGrid.Services;

/// <summary>
/// N 网更新「装得上、装完不粘」问题的判定与落盘。
/// 权威口径 = 最新 MAIN 文件的 FileId/Version，不是包内 manifest.Version。
/// </summary>
public static class NexusUpdateTruth
{
    public static string StampKey(ModEntry m) =>
        string.IsNullOrEmpty(m.UniqueID) ? m.Folder : m.UniqueID;

    public static NexusInstallRecord? GetInstallRecord(JuniGridConfig cfg, ModEntry m)
    {
        var key = StampKey(m);
        if (cfg.ModNexusInstalls.TryGetValue(key, out var r) && r is not null) return r;
        if (cfg.ModNexusInstalls.TryGetValue(m.Folder, out var r2) && r2 is not null) return r2;
        return null;
    }

    /// <summary>安装成功后：把 N 网 fileId/文件版本记进<b>我们自己的账</b>（<see cref="JuniGridConfig.ModNexusInstalls"/>）。
    /// <para>绝不回写作者的 <c>manifest.json</c>：那个字段的读者是 SMAPI，它只认「至少两段数字」，
    /// 而 N 网文件版本栏是作者手填的自由文本（实测 Simple Hoard Chest 填 <c>1</c>、包内 manifest 是
    /// <c>1.24</c>）。把前者抄进后者会让 SMAPI 解析失败而<b>整个 mod 不加载</b>（实机 2026-10-11：
    /// 信件没来，SMAPI-latest.txt 把它列进 "could not be added"）。
    /// 而这条记录只用于「这一版是否已装」（fileId）与更新判定的比大小（<see cref="LocalVersionToCompare"/>）；
    /// 界面显示的那一格永远取包内自述，见 <see cref="EffectiveLocalVersion"/>。两边都不需要动安装物。</para>
    /// <paramref name="downloadedModId"/> 必须是<b>真下载的那一页</b>：非官方续作的 manifest 里
    /// UpdateKeys 仍写着原版页（实测 2026-10-01 从续作页 20702 装的 Friends Forever，
    /// manifest 里写的是 Nexus:1738），拿 <c>m.NexusModId</c> 存就是把来源身份丢掉 ——
    /// 而「替代版是不是你手上这一版」正是靠这条记录来证明的。</summary>
    public static void RecordInstall(JuniGridConfig cfg, ModEntry m, int downloadedModId,
        long fileId, string remoteVersion)
    {
        if (string.IsNullOrWhiteSpace(remoteVersion) && fileId == 0) return;
        var rec = new NexusInstallRecord
        {
            NexusModId = downloadedModId > 0 ? downloadedModId : (m.NexusModId ?? 0),
            FileId = fileId,
            RemoteVersion = remoteVersion ?? "",
            InstalledAtUtc = DateTime.UtcNow,
        };
        var key = StampKey(m);
        cfg.ModNexusInstalls[key] = rec;
        if (!string.IsNullOrEmpty(m.Folder) && !string.Equals(m.Folder, key, StringComparison.OrdinalIgnoreCase))
            cfg.ModNexusInstalls[m.Folder] = rec;
    }

    /// <summary>装完后从扫描结果里认出「刚装的那一条」。文件夹名对不上时，再按 manifest 的显示名
    /// 和路径末段各认一次 —— 传进来的猜测名是 manifest 里的 <c>Name</c>，改名安装与捆绑包子包的场合
    /// 它就不等于文件夹名；认不出来的后果是以一个永远查不中的键写进 ModNexusInstalls（记录等于白写，
    /// 下次快道仍然认不出「这一版已装」）。三条判据按可信度排：N 网 id &gt; 文件夹全名 &gt; 显示名 / 末段。</summary>
    public static ModEntry? FindInstalledEntry(IEnumerable<ModEntry> scanned, int nexusModId, string? nameGuess)
    {
        foreach (var x in scanned)
            if (x.NexusModId == nexusModId) return x;
        var guess = (nameGuess ?? "").Trim();
        if (guess.Length == 0) return null;
        ModEntry? byFolder = null, byName = null, byLeaf = null;
        foreach (var x in scanned)
        {
            if (byFolder is null && string.Equals(x.Folder, guess, StringComparison.OrdinalIgnoreCase)) byFolder = x;
            else if (byName is null && string.Equals(x.Name, guess, StringComparison.OrdinalIgnoreCase)) byName = x;
            else if (byLeaf is null && string.Equals(LeafOf(x.Folder), guess, StringComparison.OrdinalIgnoreCase)) byLeaf = x;
            if (byFolder is not null) break;
        }
        return byFolder ?? byName ?? byLeaf;
    }

    /// <summary>Mods 里的路径末段：捆绑包子包记作 "Top/Sub"，而它的显示名常常只等于 "Sub"。</summary>
    private static string LeafOf(string folder) =>
        folder.Replace('\\', '/').TrimEnd('/').Split('/')[^1];

    public static void RecordInstallByModId(JuniGridConfig cfg, string folderOrUid,
        int nexusModId, long fileId, string remoteVersion)
    {
        var rec = new NexusInstallRecord
        {
            NexusModId = nexusModId,
            FileId = fileId,
            RemoteVersion = remoteVersion ?? "",
            InstalledAtUtc = DateTime.UtcNow,
        };
        cfg.ModNexusInstalls[folderOrUid] = rec;
    }

    /// <summary>列表显示用的本地版本：<b>包内 <c>manifest.json</c> 的自述就是答案</b>，只有它空缺时才退回账上的
    /// N 网文件版本。
    /// <para>绝不拿两边比大小：N 网「文件版本」栏是作者手填的自由文本，与包内自述常常是两套编号体系
    /// （实测 2026-10-13：鹕鸟镇 / N 网 50344 包内写 <c>1.2.6</c>，文件栏写 <c>26</c>）。比大小会让 <c>26 &gt; 1</c>
    /// 成立，于是把作者自己写的版本号盖掉 —— 玩家看到的就是一行「我们的应用改了人家的版本号」，
    /// 而 SMAPI、作者、更新日志认的都是 <c>1.2.6</c>。安装物从头到尾没动过（见
    /// <see cref="RecordInstall"/>），错的只是这一格读的是哪个字段。</para>
    /// <para>更新判定不走这里，走 <see cref="LocalVersionToCompare"/> —— 那里比的确实是「上一次装的那一条文件」。</para></summary>
    public static string EffectiveLocalVersion(JuniGridConfig cfg, ModEntry m)
    {
        if (!string.IsNullOrWhiteSpace(m.Version)) return m.Version;
        var rec = GetInstallRecord(cfg, m);
        return rec is null || string.IsNullOrWhiteSpace(rec.RemoteVersion) ? m.Version : rec.RemoteVersion;
    }

    /// <summary>更新判定用的本地版本：账上记着「上一次装的哪一条 N 网文件」，那条记录与远端返回值
    /// 同出一个字段，只有段数一致（同一套编号体系）时才拿它比大小；否则回到包内自述，
    /// 让 <see cref="ShouldShowUpdate"/> 里那道「段数不同 = 不可比」的闸照常生效。
    /// <para>v1.3.45：段数一致时取「账本」与「包内自述」较高者 —— 两边的偏差各有含义：
    /// 账本比自述新 = 作者忘改 manifest（账本才是真装上的那条文件，旧实现就是靠它救的）；
    /// 自述比账本新 = 账本过期（捆绑包安装只更新了一条子包记录 / 用户手工换过文件，
    /// 2026-11-06 实机：Parchment 的 Example Pack 账本停在 1.14.0，盘上已经是 1.15.1，
    /// 精查拿旧账比出「可更新」假徽标）。取较高的那个，两种偏差都落到「盘上到底是哪一版」。</para></summary>
    public static string LocalVersionToCompare(JuniGridConfig cfg, ModEntry m, string? remoteVersion)
    {
        var rec = GetInstallRecord(cfg, m);
        if (rec is not null && !string.IsNullOrWhiteSpace(rec.RemoteVersion)
            && !string.IsNullOrWhiteSpace(remoteVersion)
            && NumericSegments(rec.RemoteVersion) == NumericSegments(remoteVersion)
            && NumericSegments(rec.RemoteVersion) > 0)
        {
            var manifestVer = m.Version ?? "";
            if (NumericSegments(manifestVer) == NumericSegments(rec.RemoteVersion)
                && VersionUtil.IsNewer(manifestVer, rec.RemoteVersion))
                return manifestVer;
            return rec.RemoteVersion;
        }
        return EffectiveLocalVersion(cfg, m);
    }

    /// <summary>「这一版已经装进去了」的几类证据，<b>强弱不同，用法也不同</b> —— 见
    /// <see cref="FindInstallProof"/> 与 <see cref="TryHealFromPriorDownload"/>：前四类是「装成功
    /// 那一刻才写下」的痕迹，可以据此补账；<see cref="DownloadArtifact"/> 只是<b>下载</b>的痕迹，
    /// 只配让它当次不亮，绝不配变成永久账。</summary>
    public enum InstallEvidenceKind
    {
        /// <summary>我们自己的安装账（<see cref="JuniGridConfig.ModNexusInstalls"/>）：装成功才写。</summary>
        InstallRecord,
        /// <summary><see cref="JuniGridConfig.ModFileLastDownload"/>：下过这一条，且当年安装成功。</summary>
        PriorDownloadRecord,
        /// <summary>同一 N 网页面的兄弟子包装过这条文件（捆绑包只给一个子包记账）。</summary>
        SiblingPage,
        /// <summary>任一包记录过这条 fileId（捆绑包整包共用一条 MAIN 文件）。</summary>
        GlobalFileId,
        /// <summary>只剩下载缓存里的那个包 —— 最弱，必须另有「确实解压进 Mods」的痕迹。</summary>
        DownloadArtifact,
    }

    /// <summary>一条「凭什么判已装」的证据：类别 + 给人看的一句话（写进日志）。</summary>
    public sealed record InstallProof(InstallEvidenceKind Kind, string Detail);

    /// <summary>本地是否已装上「当前这条 N 网 MAIN 文件」（fileId 或文件版本一致）。</summary>
    public static bool AlreadyHasRemoteFile(JuniGridConfig cfg, ModEntry m, string? remoteVersion, long? remoteFileId)
        => FindInstallProof(cfg, m, remoteVersion, remoteFileId) is not null;

    /// <summary>这一行「凭什么判已装」；没有任何证据返回 null。
    /// <para>判定与诊断共用<b>这一份</b>实现 —— 各写一套的话，日志说的和界面做的早晚会分家。</para></summary>
    public static InstallProof? FindInstallProof(JuniGridConfig cfg, ModEntry m,
        string? remoteVersion, long? remoteFileId, string? downloadsDir = null)
    {
        var rec = GetInstallRecord(cfg, m);
        if (rec is not null)
        {
            if (remoteFileId is long fid && fid != 0 && rec.FileId != 0 && rec.FileId == fid)
                return new InstallProof(InstallEvidenceKind.InstallRecord, $"账上记着这条文件（fileId {fid}）");
            // 快道（smapi.io）只给版本号不给 fileId，这条是那种情况下唯一的判据 ——
            // 必须按语义比：N 网文件栏写 "1.4"、索引写 "1.4.0"，字面相等会把刚装的版认成旧版
            if (VersionUtil.Same(rec.RemoteVersion, remoteVersion))
                return new InstallProof(InstallEvidenceKind.InstallRecord,
                    $"账上记着这一版（{rec.RemoteVersion}）");
            // 自己这条对不上：**只承认同一页面的兄弟子包**装过这条文件。
            // 捆绑包常只给其中一个子包写记录（Frontier Farm 记 127365、同页的 SVE Code 记 135998），
            // 一律"有记录就到此为止"会把这半条证据也废掉 —— 于是那行明明已经是最新还一直亮 ⇧
            // （2026-11-07 CI：RemoteVersionRestoreTests 抓到）。
            // 但也不能无条件认领别的页：换过 UniqueID 重装成旧版时，配置里会同时留着旧 UID 的记录，
            // 越过它就会把真更新判成"已装最新"（2026-11-05 Farm Queue：当前 YuGe.FarmQueue=0.5.0，
            // 旧记录 189841=0.5.1 把它吞了）。两者的分界就是「是不是同一页」。
            var page = rec.NexusModId > 0 ? rec.NexusModId : (m.NexusModId ?? 0);
            if (remoteFileId is long sibFid && sibFid != 0 && page > 0)
            {
                foreach (var r in cfg.ModNexusInstalls.Values)
                    if (r is not null && r.FileId == sibFid && r.NexusModId == page)
                        return new InstallProof(InstallEvidenceKind.SiblingPage,
                            $"同页（{page}）的另一个子包装过这条文件（fileId {sibFid}）");
            }
            return null;
        }
        // 启动器历史上下载过「这一条」MAIN 文件（ModFileLastDownload[fileId]），
        // 即使当时没写安装快照/没回写 manifest，也视为已拥有该文件
        if (remoteFileId is long dfid && dfid != 0
            && cfg.ModFileLastDownload.ContainsKey(dfid.ToString()))
            return new InstallProof(InstallEvidenceKind.PriorDownloadRecord,
                $"本机装过这条文件（fileId {dfid}，下载记录留下了日期）");
        // 捆绑包装一次往往只给某个子包写了记录（SevenDeadlySins 的 PFM/FTM 各算一条）。
        // 全局按 fileId 认领：任一子包装过这一版 MAIN 文件 ⇒ 整包都算已装。
        if (remoteFileId is long gfid && gfid != 0)
        {
            foreach (var r in cfg.ModNexusInstalls.Values)
                if (r is not null && r.FileId == gfid)
                    return new InstallProof(InstallEvidenceKind.GlobalFileId,
                        $"本机有包装过这条文件（fileId {gfid}）");
        }
        // 最后一档：历史修复 —— 修复前 .nxm 一键安装根本不写下载/安装记录（InstallService 那条路径
        // 漏了），所以「早就装好最新」的 mod 会一直亮 ⇧。但「下过这个包」≠「装上了这个包」，
        // 所以这一档必须再证明一次「解压确实发生过」，见 DownloadArtifactProof。
        if (remoteFileId is long rf && rf != 0 && m.NexusModId is int mid && mid > 0)
        {
            var detail = DownloadArtifactProof(m, mid, rf, downloadsDir);
            if (detail is not null)
                return new InstallProof(InstallEvidenceKind.DownloadArtifact, detail);
        }
        return null;
    }

    /// <summary>下载缓存里那份包，能不能证明「这一版已经<b>装进</b> Mods 了」。
    /// <para>为什么不能只看「包在不在」：缓存里的 <c>nxm-&lt;页号&gt;-&lt;文件号&gt;.zip</c> 只要<b>下完</b>就躺在
    /// 那儿，跟装没装上没有关系 —— 安装失败、暂停、下完直接关掉应用、应用内取不到直链改走网页，
    /// 都会留下它；单流下载中断留下的还是<b>半截</b>包（「暂停=续传」正是靠这个半截文件）。
    /// 而 <c>StorageService.AutoCleanDownloads</c> 要闲置满 24 小时才清它，于是「有包 ⇒ 已装」这个
    /// 判据在用户眼里就是：<b>今天点过更新、明天 ⇧ 就没了，第三天晚上缓存一清又莫名冒回来</b>
    /// （2026-11 用户反馈的原话就是「第一天能检测到更新的 mod，第二天就检测不到了」）。</para>
    /// <para>三条全成立才认：① 没有分片残留（<c>.seg</c> / <c>.seg.state</c>）= 下载真的走完了；
    /// ② 包能被解析出条目（半截 zip 没有中央目录）；③ <b>Mods 里这个包目录的写入时间不早于包的
    /// 下载时间</b> = 解压落地确实发生过。① ② 排除「没下完」，③ 排除「下完了但没装成」——
    /// 前两条只证明「拿到了文件」，第三条才证明「装上了」。</para>
    /// <para>代价：真有安装但目录时间早于包（用户后来重下过同一个包却没装）会被判成没装，
    /// 结果是多亮一次 ⇧；点一次更新即可纠正，比「永久不提示更新」便宜得多。</para></summary>
    private static string? DownloadArtifactProof(ModEntry m, int modId, long fileId, string? downloadsDir)
    {
        try
        {
            var zip = Path.Combine(downloadsDir ?? StoragePaths.DownloadsDir, $"nxm-{modId}-{fileId}.zip");
            if (!File.Exists(zip)) return null;
            if (File.Exists(zip + ".seg") || File.Exists(zip + ".seg.state"))
                return null;   // 下载没走完（分片续传的残留还在），包里的东西从来没进过 Mods
            if (!ModService.ArchiveReadable(zip))
                return null;   // 半截包 / 镜像回的错误页：连条目都列不出来
            var downloaded = File.GetLastWriteTime(zip);
            // ModEntry.LastWrite 是 Mods 里这个包目录的最后写入时间（盘上扫描来的）。
            // 装过 ⇒ 目录被写过 ⇒ 不早于下载完成时间。留 2 秒余量吸收 FAT/NTFS 的时间粒度。
            if (m.LastWrite == default || m.LastWrite < downloaded.AddSeconds(-2))
                return null;
            return $"下载缓存里的 nxm-{modId}-{fileId}.zip 完整，且 Mods 目录写在它之后"
                + $"（目录 {m.LastWrite:yyyy-MM-dd HH:mm} / 包 {downloaded:yyyy-MM-dd HH:mm}）";
        }
        catch { return null; }
    }

    /// <summary>
    /// 用「启动器曾下载过该 fileId」自动校准：缺快照时补写安装快照，让更新判定立刻与 N 网一致，
    /// 无需用户再点一次更新（作者忘改 manifest 时的死循环）。
    /// <para>只补账、<b>不改作者的 manifest，也不把 N 网版本盖到 <c>m.Version</c> 上</b>：包内自述优先，
    /// 显示哪一版由 <see cref="EffectiveLocalVersion"/> 判，比哪一版由 <see cref="LocalVersionToCompare"/> 判，
    /// 「这一版已装」由 fileId 认。</para>
    /// 返回 true = 已是该文件/已校准成功。
    /// <para>v1.3.46：<b>只补「装成功」留下的痕迹</b>。补账是永久的（写进配置、下次启动还在），
    /// 拿下载缓存里那个包去补账等于把「下过」永久写成「装过」—— 一次误判就是这一行<b>再也不提示
    /// 更新</b>（用户反馈的「第二天就检测不到」正是这么被固化的：包第二天就被清掉了，账还留着）。
    /// 所以 <see cref="InstallEvidenceKind.DownloadArtifact"/> 只让它当次不亮，不进账本。</para>
    /// </summary>
    public static bool TryHealFromPriorDownload(JuniGridConfig cfg, ModEntry m,
        long fileId, string? remoteVersion)
    {
        if (m.IsConvertedPack) return false;
        var proof = FindInstallProof(cfg, m, remoteVersion, fileId);
        if (proof is null) return false;
        if (proof.Kind == InstallEvidenceKind.DownloadArtifact) return false;
        // 有历史下载但缺快照、或快照当年没记版本号（.nxm 那条路曾经只写 fileId）→ 补齐，
        // 否则下次快道再拿"只给版本号不给 fileId"的结果来判，仍然认不出「这一版已装」
        var rec = GetInstallRecord(cfg, m);
        if (fileId != 0 && !string.IsNullOrWhiteSpace(remoteVersion)
            && (rec is null || string.IsNullOrWhiteSpace(rec.RemoteVersion)))
            RecordInstall(cfg, m, m.NexusModId ?? 0, fileId, remoteVersion!);
        return true;
    }

    /// <summary>
    /// 是否应显示「可更新」：转换包永不比；已装上当前 N 网 MAIN 文件 → 否；
    /// 否则用有效本地版本与远端文件版本做语义化比较。
    /// </summary>
    public static bool ShouldShowUpdate(JuniGridConfig cfg, ModEntry m, string? remoteVersion, long? remoteFileId)
        => ShouldShowUpdate(cfg, m, remoteVersion, remoteFileId, null, out _);

    /// <summary>同上；<paramref name="installedProof"/> 带回「这次为什么不亮」的证据（没因已装而不亮时为 null）——
    /// 检查流水线拿它把「按下载缓存认领」写进日志，用户再说「今天检测不到」时能直接指出依据。</summary>
    public static bool ShouldShowUpdate(JuniGridConfig cfg, ModEntry m, string? remoteVersion, long? remoteFileId,
        out InstallProof? installedProof)
        => ShouldShowUpdate(cfg, m, remoteVersion, remoteFileId, null, out installedProof);

    /// <param name="downloadsDir">只给测试用：下载缓存目录换成临时目录，不碰真机的
    /// <see cref="StoragePaths.DownloadsDir"/>。生产一律传 null。</param>
    public static bool ShouldShowUpdate(JuniGridConfig cfg, ModEntry m, string? remoteVersion, long? remoteFileId,
        string? downloadsDir, out InstallProof? installedProof)
    {
        installedProof = null;
        if (m.IsConvertedPack) return false;
        if (string.IsNullOrWhiteSpace(remoteVersion)) return false;
        installedProof = FindInstallProof(cfg, m, remoteVersion, remoteFileId, downloadsDir);
        if (installedProof is not null) return false;
        var local = LocalVersionToCompare(cfg, m, remoteVersion);
        if (string.IsNullOrWhiteSpace(local)) return true;
        // 结构性误判护栏：远端版本号只有一段数字（作者把 N 网「文件版本」栏填成 1）、
        // 而本地是多段（0.0.1）—— 两边根本不是同一套编号体系，比大小没意义
        // （实测 Haley 动漫肖像包恒显示 0.0.1 ⇧ 1，怎么更新都消不掉）。
        // 反向（本地单段、远端多段）是正常情形，不拦。
        if (NumericSegments(remoteVersion) == 1 && NumericSegments(local) >= 2)
        {
            AppLog.Warn("Updates", $"跳过不可比较的版本号：本地 {local} vs 远端 {remoteVersion}"
                + $"（{m.Folder}）—— 远端只有一段数字，判定为不同编号体系");
            return false;
        }
        return VersionUtil.IsNewer(remoteVersion, local);
    }

    /// <summary>版本号里数字段的个数（剥掉 v 前缀与 -beta/+build 后缀后按点切）。</summary>
    private static int NumericSegments(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return 0;
        var v = version.Trim().TrimStart('v', 'V');
        var cut = v.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0) v = v[..cut];
        return v.Split('.', StringSplitOptions.RemoveEmptyEntries)
                .Count(seg => seg.Length > 0 && seg.All(char.IsDigit));
    }
}
