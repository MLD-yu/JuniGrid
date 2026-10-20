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

    /// <summary>更新判定用的本地版本：账上记着「上一次装的是哪一条 N 网文件」，那条记录与远端返回值
    /// 同出一个字段，只有段数一致（同一套编号体系）时才拿它比大小；否则回到包内自述，
    /// 让 <see cref="ShouldShowUpdate"/> 里那道「段数不同 = 不可比」的闸照常生效。</summary>
    public static string LocalVersionToCompare(JuniGridConfig cfg, ModEntry m, string? remoteVersion)
    {
        var rec = GetInstallRecord(cfg, m);
        if (rec is not null && !string.IsNullOrWhiteSpace(rec.RemoteVersion)
            && !string.IsNullOrWhiteSpace(remoteVersion)
            && NumericSegments(rec.RemoteVersion) == NumericSegments(remoteVersion)
            && NumericSegments(rec.RemoteVersion) > 0)
            return rec.RemoteVersion;
        return EffectiveLocalVersion(cfg, m);
    }

    /// <summary>本地是否已装上「当前这条 N 网 MAIN 文件」（fileId 或文件版本一致）。</summary>
    public static bool AlreadyHasRemoteFile(JuniGridConfig cfg, ModEntry m, string? remoteVersion, long? remoteFileId)
    {
        var rec = GetInstallRecord(cfg, m);
        if (rec is not null)
        {
            if (remoteFileId is long fid && fid != 0 && rec.FileId != 0 && rec.FileId == fid) return true;
            // 快道（smapi.io）只给版本号不给 fileId，这条是那种情况下唯一的判据 ——
            // 必须按语义比：N 网文件栏写 "1.4"、索引写 "1.4.0"，字面相等会把刚装的版认成旧版
            if (VersionUtil.Same(rec.RemoteVersion, remoteVersion)) return true;
        }
        // 启动器历史上下载过「这一条」MAIN 文件（ModFileLastDownload[fileId]），
        // 即使当时没写安装快照/没回写 manifest，也视为已拥有该文件
        if (remoteFileId is long dfid && dfid != 0
            && cfg.ModFileLastDownload.ContainsKey(dfid.ToString()))
            return true;
        // 捆绑包装一次往往只给某个子包写了记录（SevenDeadlySins 的 PFM/FTM 各算一条）。
        // 全局按 fileId 认领：任一子包装过这一版 MAIN 文件 ⇒ 整包都算已装。
        if (remoteFileId is long gfid && gfid != 0)
        {
            foreach (var r in cfg.ModNexusInstalls.Values)
                if (r is not null && r.FileId == gfid) return true;
        }
        // 历史修复：修复前 .nxm 一键安装根本不写下载/安装记录（InstallService 那条路径漏了），
        // 所以「早就装好最新」的 mod 会一直亮 ⇧。判据用下载产物本身：
        // 本地留着 nxm-<modId>-<当前远端fileId>.zip ⇒ 玩家下的就是这条文件。
        // 只匹配「当前远端 fileId」，所以下了旧包、下了没装都不算，不会把真更新吞掉。
        if (remoteFileId is long rf && rf != 0 && m.NexusModId is int mid && mid > 0)
        {
            var zip = Path.Combine(StoragePaths.DownloadsDir, $"nxm-{mid}-{rf}.zip");
            if (File.Exists(zip))
            {
                AppLog.Warn("Updates", $"按本地下载包认领为已装：{zip}（{m.Folder}）"
                    + " —— 若其实没装上，点一次「更新」即可纠正");
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 用「启动器曾下载过该 fileId」自动校准：缺快照时补写安装快照，让更新判定立刻与 N 网一致，
    /// 无需用户再点一次更新（作者忘改 manifest 时的死循环）。
    /// <para>只补账、<b>不改作者的 manifest，也不把 N 网版本盖到 <c>m.Version</c> 上</b>：包内自述优先，
    /// 显示哪一版由 <see cref="EffectiveLocalVersion"/> 判，比哪一版由 <see cref="LocalVersionToCompare"/> 判，
    /// 「这一版已装」由 fileId 认。</para>
    /// 返回 true = 已是该文件/已校准成功。
    /// </summary>
    public static bool TryHealFromPriorDownload(JuniGridConfig cfg, ModEntry m,
        long fileId, string? remoteVersion)
    {
        if (m.IsConvertedPack) return false;
        if (AlreadyHasRemoteFile(cfg, m, remoteVersion, fileId))
        {
            // 有历史下载但缺快照、或快照当年没记版本号（.nxm 那条路曾经只写 fileId）→ 补齐，
            // 否则下次快道再拿"只给版本号不给 fileId"的结果来判，仍然认不出「这一版已装」
            var rec = GetInstallRecord(cfg, m);
            if (fileId != 0 && !string.IsNullOrWhiteSpace(remoteVersion)
                && (rec is null || string.IsNullOrWhiteSpace(rec.RemoteVersion)))
                RecordInstall(cfg, m, m.NexusModId ?? 0, fileId, remoteVersion!);
            return true;
        }
        return false;
    }

    /// <summary>
    /// 是否应显示「可更新」：转换包永不比；已装上当前 N 网 MAIN 文件 → 否；
    /// 否则用有效本地版本与远端文件版本做语义化比较。
    /// </summary>
    public static bool ShouldShowUpdate(JuniGridConfig cfg, ModEntry m, string? remoteVersion, long? remoteFileId)
    {
        if (m.IsConvertedPack) return false;
        if (string.IsNullOrWhiteSpace(remoteVersion)) return false;
        if (AlreadyHasRemoteFile(cfg, m, remoteVersion, remoteFileId)) return false;
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
