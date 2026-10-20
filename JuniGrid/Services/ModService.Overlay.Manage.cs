using System.IO;

namespace JuniGrid.Services;

public sealed partial class ModService
{
    /// <summary>把覆盖包的文件写进宿主目录，并逐文件记账。
    /// 备份只在"还没有备份"时创建 —— 已有备份说明先前有别的包盖过这里，那份才是真原版，
    /// 用当前值覆盖它就会把还原目标污染成上一个覆盖版。</summary>
    private string? ApplyOverlay(string gamePath, string tempDir, OverlayPlan plan, ConfigService cfg,
        int? nexusModId, string packName, out string? modName, List<string>? notes = null)
    {
        modName = null;
        var hostDir = ResolveHostDir(gamePath, plan.Host);
        if (hostDir is null) return LocService.Tr("找不到要覆盖的 mod，已放弃安装");
        var hostRoot = Path.GetFullPath(hostDir) + Path.DirectorySeparatorChar;
        // 包内那层目录 ≠ 宿主目录（汉化包常把宿主再套一层、或宿主是捆绑包里的子包），
        // 所以取文件一律用 PackRoot，落盘才用宿主目录。
        var packRoot = Path.GetFullPath(Path.Combine(tempDir, plan.PackRoot.Replace('/', Path.DirectorySeparatorChar)));
        var c = cfg.Current;
        var written = 0;
        var skipped = 0;
        var adopted = 0;

        foreach (var (r, dst) in plan.RelPaths.Zip(plan.DstPaths ?? plan.RelPaths))
        {
            // src 用包内路径，其余（落盘、账本键、AppData 副本、开关与还原）一律用宿主内路径
            var local = dst.Replace('/', Path.DirectorySeparatorChar);
            var src = Path.Combine(packRoot, r.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(src)) continue;
            // 「已放到宿主的 X」这句只能在【真的动了那个文件】之后说。
            // 原来它写在循环开头，于是内容早已在位、一个字节都没写的包（实测 44611：
            // 宿主的 i18n/zh.json 与包里逐字节相同）也会报"已放到宿主的「i18n/zh.json」"，
            // 而 Mods 行上一个覆盖徽标都没有 —— 用户读到的是"装了"，实际是"什么都没装"。
            var moved = !dst.Equals(r, StringComparison.Ordinal);
            var dstPath = Path.GetFullPath(Path.Combine(hostDir, local));
            // 包内出现 ../ 之类的越界路径时绝不允许写到宿主外面去
            if (!dstPath.StartsWith(hostRoot, StringComparison.OrdinalIgnoreCase))
                return LocService.Tr("覆盖包里含越界路径，已放弃安装");
            // 逐字节相同的文件不重抄一遍盘：汉化重打包会把整个目录原样带上来
            // （LewdDew 那个包 306 个文件里 160 个和原版一模一样），
            // 全记进账就变成几百条毫无意义的"可还原"，还会把备份目录撑爆。
            var same = File.Exists(dstPath) && Sha256Of(src) == Sha256Of(dstPath);
            var key = OverlayKey(plan.Host, dst, nexusModId, packName);
            var bakFile = dstPath + OverlayBackupSuffix;
            if (same)
            {
                // 内容已经在位 ≠ 账还在。账没了这份汉化就成了隐形文件：界面看不见、
                // 开关和还原都没有入口，再点安装还会被上面那条规则算成"没东西可写"。
                // 所以补账。补得了的前提是原版备份还在 —— 没备份就绝不伪造 origHash，
                // 否则「删除」会把宿主自己那份文件当成"覆盖包新增的"直接删掉。
                if (OverlayLive(c, key, local) || !File.Exists(bakFile)) { skipped++; continue; }
                if (!TryAdoptOverlay(c, plan, key, dst, local, src, dstPath, Sha256Of(bakFile), nexusModId, packName))
                    return $"「{Path.GetFileName(hostDir)}」里的文件正被占用，覆盖未完成：{dst}";
                if (moved) notes?.Add(MovedNote(dst));
                adopted++;
                continue;
            }
            // 备份才是真原版：第二个包盖上来时磁盘上那份是第一个包的内容，
            // 拿它当"原版"存下来，还原时就把第一个汉化一起毁掉了。
            var origHash = File.Exists(dstPath)
                ? (File.Exists(bakFile) ? Sha256Of(bakFile) : Sha256Of(dstPath))
                : "";
            try
            {
                if (File.Exists(dstPath) && !File.Exists(bakFile)) File.Copy(dstPath, bakFile);
                Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);
                File.Copy(src, dstPath, overwrite: true);
                // 留一份包里原件到 AppData 副本目录：没有它，"关掉"就变成"卸载"，再也打不开了。
                // 放 AppDataDir 而不是缓存目录 —— 缓存是「缓存与存储」页能一键清的，清了就等于弄丢用户的包。
                var storeId = OverlayStoreId(src, local, packName);
                // 同一个宿主文件被第二个汉化包重盖时，上一条记录的副本目录就再没人引用了
                // （记录被覆盖，「删除」只认新 StoreId）→ 永久孤儿。新副本落盘成功后清掉旧的。
                DropReplacedStore(c, key, storeId);
                c.Overlays[key] = new OverlayRecord
                {
                    Host = plan.Host,
                    RelPath = dst,
                    PackName = packName,
                    NexusModId = nexusModId,
                    OriginalSha256 = origHash,
                    AppliedSha256 = Sha256Of(dstPath),
                    StoreId = storeId,
                    Enabled = true,
                };
                // 文件槽位只有一个：这份占上了，同一个文件上的其它包一律记成"关着" ——
                // 账和副本都留着，所以想换回哪一个，去卡片里拨它那颗开关就行。
                foreach (var sib in OverlaySiblings(c, c.Overlays[key], key))
                    c.Overlays[sib].Enabled = false;
                if (moved) notes?.Add(MovedNote(dst));
                written++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"「{Path.GetFileName(hostDir)}」里的文件正被占用，覆盖未完成：{dst}";
            }
        }
        // 全都逐字节相同且账都在 = 这个包本来就装好了：那是成功，不是"没有可写入的文件"
        if (written == 0 && adopted == 0 && skipped == 0) return LocService.Tr("覆盖包里没有可写入的文件");
        if (written == 0 && adopted == 0 && skipped > 0)
            notes?.Add(LocService.Tr("内容已经在位，这次一个文件都没改"));

        cfg.Save(c);
        modName = packName;
        AppLog.Info("Mods", $"[覆盖包] {packName} → {Path.GetFileName(hostDir)}，共写入 {written} 个文件"
            + (notes is { Count: > 0 } ? "（" + string.Join("；", notes) + "）" : "")
            + (adopted > 0 ? $"（另有 {adopted} 个内容早就在位、这次把账补回来了）" : "")
            + (skipped > 0 ? $"（另有 {skipped} 个与原版逐字节相同，跳过）" : ""));
        return null;
    }

    /// <summary>搬家提示。实测两种形状：包里裸在根目录的 <c>zh.json</c>（13273 / 44611），
    /// 以及压在角色名下的 <c>Caroline/default.json</c>（32901）—— 所以不能说"它在包里是根目录文件"。</summary>
    private static string MovedNote(string dst)
        => LocService.Tf("已放到宿主的「{0}」—— 按包里那个位置放不会生效", dst);

    /// <summary>这条覆盖在账上是不是"活着的"：记着、开着、副本文件还在。
    /// 副本没了就不算 —— 那样「关掉再打开」会打不开，正是要靠重装补回来的那种。</summary>
    private static bool OverlayLive(JuniGridConfig c, string key, string local)
    {
        if (!c.Overlays.TryGetValue(key, out var rec) || !rec.Enabled
            || string.IsNullOrEmpty(rec.StoreId)) return false;
        return File.Exists(Path.Combine(OverlayStoreDir(rec.StoreId), local));
    }

    /// <summary>把一份"文件已经在位但账没了"的覆盖重新记账：不重写文件（内容一样，重抄没有意义），
    /// 只补记录 + 补副本，让卡片、开关和「还原」重新有入口。</summary>
    private static bool TryAdoptOverlay(JuniGridConfig c, OverlayPlan plan, string key, string rel,
        string local, string src, string dst, string origHash, int? nexusModId, string packName)
    {
        try
        {
            var storeId = OverlayStoreId(src, local, packName);
            DropReplacedStore(c, key, storeId);
            c.Overlays[key] = new OverlayRecord
            {
                Host = plan.Host,
                RelPath = rel,
                PackName = packName,
                NexusModId = nexusModId,
                OriginalSha256 = origHash,
                AppliedSha256 = Sha256Of(dst),
                StoreId = storeId,
                Enabled = true,
            };
            foreach (var sib in OverlaySiblings(c, c.Overlays[key], key))
                c.Overlays[sib].Enabled = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>存一份包里原件到 AppData 副本目录，返回它的 storeId；存失败返回 ""（只失去"重新开启"的能力）。</summary>
    private static string OverlayStoreId(string src, string local, string packName)
    {
        var storeId = Guid.NewGuid().ToString("N")[..12];
        try
        {
            var keep = Path.Combine(OverlayStoreDir(storeId), local);
            Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
            File.Copy(src, keep, overwrite: true);
            return storeId;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Mods", "[覆盖包] 副本保存失败，之后将无法重新开启：" + ex.Message);
            return "";
        }
    }

    /// <summary>同一个槽位换了新副本，旧副本目录就再没人引用了（记录已被覆盖，「删除」只认新 StoreId）→ 删掉。</summary>
    private static void DropReplacedStore(JuniGridConfig c, string key, string storeId)
    {
        if (storeId.Length == 0 || !c.Overlays.TryGetValue(key, out var replaced)
            || replaced.StoreId.Length == 0 || replaced.StoreId.Equals(storeId, StringComparison.Ordinal)) return;
        try { Directory.Delete(OverlayStoreDir(replaced.StoreId), recursive: true); }
        catch (Exception ex)
        { AppLog.Warn("Mods", "[覆盖包] 上一个包的副本目录没清掉（只是多占点盘）: " + ex.Message); }
    }

    /// <summary>扫描时校验覆盖记录：磁盘上还是我们放上去的那一份吗？
    /// 宿主 mod 更新会把整个旧目录移进回收站再装新版，覆盖于是静默消失。
    /// 这里<b>只报不修</b>：自动重放会盖掉上游可能已经改好的官方翻译，判断错了没人知道。
    /// ByHost 给列表行的徽标用，StaleKeys 给清单弹窗逐行标红用。</summary>
    public static OverlayReport OverlayHealth(string gamePath, JuniGridConfig cfg)
    {
        var byHost = new Dictionary<string, (int Total, int Stale)>(StringComparer.OrdinalIgnoreCase);
        var stale = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 同一个宿主文件上可以有好几个汉化包各占一条账（磁盘槽位只有一个），所以按文件分组判：
        // 有人在位就只校验在位的那条，否则两份内容撞得一模一样时，关着的那条会被误报成失效。
        foreach (var grp in cfg.Overlays.GroupBy(kv => NormalizeOverlayHost(kv.Value.Host) + "|" + kv.Value.RelPath,
                     StringComparer.OrdinalIgnoreCase))
        {
            var rows = grp.ToList();
            var activeKey = rows.FirstOrDefault(kv => kv.Value.Enabled).Key;
            var hostDir = ResolveHostDir(gamePath, rows[0].Value.Host);
            var file = hostDir is null ? null
                : Path.Combine(hostDir, rows[0].Value.RelPath.Replace('/', Path.DirectorySeparatorChar));
            var hash = file is not null && File.Exists(file) ? Sha256Of(file) : null;
            foreach (var (key, rec) in rows)
            {
                var ours = hash is not null && hash == rec.AppliedSha256;
                // "正常" = 磁盘状态和开关一致：开着该是我们的文件在位，关着该是不在位
                var alive = activeKey is null ? !ours
                    : string.Equals(key, activeKey, StringComparison.OrdinalIgnoreCase) ? ours : true;
                var norm = NormalizeOverlayHost(rec.Host);
                var cur = byHost.TryGetValue(norm, out var v) ? v : (0, 0);
                byHost[norm] = (cur.Item1 + 1, alive ? cur.Item2 : cur.Item2 + 1);
                if (!alive) stale.Add(key);
            }
        }
        return new OverlayReport(byHost, stale);
    }

    /// <summary>覆盖记录的键：宿主 | 包内相对路径 | 来源槽位。
    /// 槽位用 N 网 modId（本地包退回包名），所以同一个文件能被多个汉化包各占一条记录 ——
    /// 磁盘上那个文件同一时刻只可能是其中一份，其余的账和副本都留着，开关一拨就换上去。</summary>
    public static string OverlayKey(string host, string relPath, int? nexusModId, string packName)
        => host + "|" + relPath + "|" + (nexusModId?.ToString()
            ?? (string.IsNullOrWhiteSpace(packName) ? "legacy" : packName));

    /// <summary>这个 N 网 mod 是不是正以「覆盖包」的形式装着、而且此刻真的在位。
    /// 汉化包住在别人的目录里，Mods 列表没有它那一行，所以详情页那种「Mods\ 里有没有这个文件夹」
    /// 的已装判据对它永远为空 —— 只能问覆盖账本，再拿磁盘上的实际哈希对一遍
    /// （宿主更新过一次 = 账在但文件不是我们的了，那不算已装，该让人重装）。</summary>
    public static bool OverlayAppliedForMod(string gamePath, JuniGridConfig cfg, int modId)
    {
        foreach (var rec in cfg.Overlays.Values)
        {
            if (rec.NexusModId != modId || !rec.Enabled) continue;
            var hostDir = ResolveHostDir(gamePath, rec.Host);
            if (hostDir is null) continue;
            var file = Path.Combine(hostDir, rec.RelPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(file) && Sha256Of(file) == rec.AppliedSha256) return true;
        }
        return false;
    }

    /// <summary>同一个宿主文件上的其它覆盖记录（不含自己）。文件槽位只有一个，谁在位谁算数。</summary>
    private static List<string> OverlaySiblings(JuniGridConfig c, OverlayRecord rec, string selfKey)
        => c.Overlays.Where(kv => !kv.Key.Equals(selfKey, StringComparison.OrdinalIgnoreCase)
                && NormalizeOverlayHost(kv.Value.Host).Equals(NormalizeOverlayHost(rec.Host), StringComparison.OrdinalIgnoreCase)
                && kv.Value.RelPath.Equals(rec.RelPath, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key).ToList();

    /// <summary>覆盖包文件的副本目录。开关要能来回，就得留着包里那份原件；
    /// 放 AppDataDir 而不是缓存目录 —— 缓存是「缓存与存储」页能一键清掉的，清了就等于弄丢用户的包。
    /// <para>根目录与配置认同一个 <c>JUNIGRID_CONFIG_DIR</c>：不设时仍是
    /// <c>%APPDATA%\JuniGrid\overlays</c>（逐字节同旧行为）；设了则连副本都落在临时目录里。
    /// 为什么补这道缝：这页既不在「缓存与存储」里露出、app 也不回收孤儿，
    /// 2026-10-01 几轮测试就在玩家真目录里堆出 171 个永久孤儿目录。</para></summary>
    public static string OverlayStoreDir(string storeId)
        => Path.Combine(ConfigService.ConfigDir, "overlays", storeId);

    /// <summary>把历史上记成「捆绑包壳顶层」的覆盖账改记到它真正的宿主行 <c>顶层/子包</c>
    /// （症状与判据见 <see cref="SplitShellPlanByBundleChild"/>；那条判据只在新装时生效，救不了已经落盘的账）。
    /// <para>键里带着宿主，所以键、<see cref="OverlayRecord.Host"/>、
    /// <see cref="OverlayRecord.RelPath"/> 三处一起改，AppData 那份副本也要跟着搬 ——
    /// 搬不动就整条不动：宁可留着这条看不见的账，也不能把「关掉再打开」弄坏。</para>
    /// 判据只看磁盘，改完就不再命中，所以每次启动跑一遍是空转，不需要版本戳。返回改了几条。</summary>
    public static int RelocateBundleTopOverlays(JuniGridConfig c, string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || c.Overlays.Count == 0) return 0;
        var moved = 0;
        foreach (var (key, rec) in c.Overlays.ToList())
        {
            var host = NormalizeOverlayHost(rec.Host);
            var i = rec.RelPath.IndexOf('/');
            if (host.Length == 0 || host.Contains('/') || i <= 0) continue;   // 已是子包形状、或直属顶层的文件：没有更深的行可归
            if (ResolveHostDir(gamePath, host) is not string topDir) continue; // 宿主目录都不在了：那是 Purge 的活
            if (File.Exists(Path.Combine(topDir, "manifest.json"))) continue;  // 顶层自己有行
            var seg = rec.RelPath[..i];
            if (!File.Exists(Path.Combine(topDir, seg, "manifest.json"))) continue;  // 那一层不是子包
            var newHost = host + "/" + seg;
            var newRel = rec.RelPath[(i + 1)..];
            if (!TryMoveOverlayCopy(rec, newRel, out var why))
            {
                AppLog.Warn("Mods", $"[覆盖包] 副本没搬动，这条账先按原样留着：{key}（{why}）");
                continue;
            }
            rec.Host = newHost;
            rec.RelPath = newRel;
            c.Overlays.Remove(key);
            c.Overlays[newHost + "|" + newRel + "|" + OverlaySlotOf(key)] = rec;
            moved++;
        }
        if (moved > 0)
            AppLog.Info("Mods", $"[覆盖包] {moved} 条账的宿主从「捆绑包壳顶层」改记到子包那一行");
        return moved;
    }

    /// <summary>副本跟着宿主形状搬位：开关是按「副本目录 + 宿主内路径」找那份留底的。
    /// 搬不动就整条账不动 —— 先改了账而副本还在旧路径上，「关掉再打开」就再也打不开了。</summary>
    private static bool TryMoveOverlayCopy(OverlayRecord rec, string newRel, out string? why)
    {
        why = null;
        if (rec.StoreId.Length == 0) return true;
        var sep = Path.DirectorySeparatorChar;
        var root = OverlayStoreDir(rec.StoreId);
        var oldCopy = Path.Combine(root, rec.RelPath.Replace('/', sep));
        var newCopy = Path.Combine(root, newRel.Replace('/', sep));
        if (!File.Exists(oldCopy)) return true;   // 旧位置没有：要么早就搬过，要么副本本来就没留住
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(newCopy)!);
            File.Move(oldCopy, newCopy, overwrite: true);
            return true;
        }
        catch (Exception ex) { why = ex.Message; return false; }
    }

    /// <summary>卸载宿主时清掉它名下的覆盖记录与副本目录。
    /// <b>不做还原</b>：宿主目录本身就要没了，把原版拷回去毫无意义，而副本留在 AppData 里
    /// 就再也无人引用 —— 记录也会变成一条永远"找不到宿主"的死账。</summary>
    private static void PurgeOverlaysOf(string folderName, ConfigService cfg)
    {
        var c = cfg.Current;
        if (c.Overlays.Count == 0) return;
        // 卸载顶层会连里面的子包一起没掉，所以「等于它」或「在它下面」的记录都要清；
        // 反过来只卸某个子包时，顶层和兄弟子包的覆盖不能跟着陪葬。
        var gone = NormalizeOverlayHost(folderName);
        var doomed = c.Overlays
            .Where(kv => NormalizeOverlayHost(kv.Value.Host).Equals(gone, StringComparison.OrdinalIgnoreCase)
                || NormalizeOverlayHost(kv.Value.Host).StartsWith(gone + "/", StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key).ToList();
        foreach (var key in doomed)
        {
            if (c.Overlays[key].StoreId is { Length: > 0 } sid)
                try { Directory.Delete(OverlayStoreDir(sid), recursive: true); } catch { }
            c.Overlays.Remove(key);
        }
        if (doomed.Count == 0) return;
        cfg.Save(c);
        AppLog.Info("Mods", $"[覆盖包] 宿主 {gone} 被卸载，连带清除 {doomed.Count} 条覆盖记录与副本");
    }

    /// <summary>开 / 关一条覆盖。开着 = 我们的文件在位；关掉 = 把原文件放回去，<b>但账和副本都留着</b>，
    /// 所以随时能再打开，不需要重新下载汉化包。彻底删账走 <see cref="RevertOverlay"/>。</summary>
    public string? SetOverlayEnabled(string gamePath, string key, bool enabled, ConfigService cfg, bool save = true)
    {
        var c = cfg.Current;
        if (!c.Overlays.TryGetValue(key, out var rec)) return LocService.Tr("找不到这条覆盖记录");
        var hostDir = ResolveHostDir(gamePath, rec.Host);
        if (hostDir is null) return LocService.Tf("找不到宿主 mod「{0}」", rec.Host);
        var local = rec.RelPath.Replace('/', Path.DirectorySeparatorChar);
        var dst = Path.GetFullPath(Path.Combine(hostDir, local));
        var bak = dst + OverlayBackupSuffix;
        // 状态没变就别动盘：关着的那条再"关"一次会把当前在位的另一个包的文件换成原版
        if (rec.Enabled == enabled) return null;
        try
        {
            if (enabled)
            {
                var keep = string.IsNullOrEmpty(rec.StoreId)
                    ? null : Path.Combine(OverlayStoreDir(rec.StoreId), local);
                if (keep is null || !File.Exists(keep))
                    return LocService.Tr("覆盖包的文件副本已丢失，无法重新开启。请重新安装这个覆盖包。");
                if (File.Exists(dst) && !File.Exists(bak)) File.Copy(dst, bak);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(keep, dst, overwrite: true);
                rec.AppliedSha256 = Sha256Of(dst);
                // 抢占槽位：同一个文件上其它包的记录一律记成"关着"（账和副本都留着）
                foreach (var sib in OverlaySiblings(c, rec, key)) c.Overlays[sib].Enabled = false;
            }
            else if (File.Exists(bak))
            {
                File.Copy(bak, dst, overwrite: true);   // 备份留着：下次开启时它才是"原版"
            }
            else if (File.Exists(dst) && string.IsNullOrEmpty(rec.OriginalSha256))
            {
                File.Delete(dst);                       // 这个文件本来就是覆盖包新增的
            }
            rec.Enabled = enabled;
            if (save) cfg.Save(c);
            AppLog.Info("Mods", $"[覆盖包] {(enabled ? "开启" : "关闭")} {rec.Host}/{rec.RelPath}");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return LocService.Tr("文件正被占用，操作失败");
        }
    }

    /// <summary>彻底撤销一条覆盖：还原原文件、删掉备份与副本、销账。
    /// 备份没了就不硬编 —— 宿主被整体重装过时，目录里那份新文件正是上游原版，没什么可还原的，
    /// 这时只把账销掉，并如实说明。</summary>
    public string? RevertOverlay(string gamePath, string key, ConfigService cfg, bool save = true)
    {
        var c = cfg.Current;
        if (!c.Overlays.TryGetValue(key, out var rec)) return LocService.Tr("找不到这条覆盖记录");
        var hostDir = ResolveHostDir(gamePath, rec.Host);
        var dst = hostDir is null
            ? null
            : Path.GetFullPath(Path.Combine(hostDir, rec.RelPath.Replace('/', Path.DirectorySeparatorChar)));
        var bak = dst is null ? null : dst + OverlayBackupSuffix;

        try
        {
            // 先算同一个文件上还留着谁：还剩别人，备份和槽位就还要用，不能顺手清掉
            var sibs = OverlaySiblings(c, rec, key);
            if (rec.Enabled)
            {
                if (bak is not null && File.Exists(bak))
                {
                    if (dst is not null) File.Copy(bak, dst, overwrite: true);
                    if (sibs.Count == 0) File.Delete(bak);
                }
                else if (dst is not null && rec.OriginalSha256.Length == 0 && File.Exists(dst))
                {
                    File.Delete(dst);   // 原本没这个文件，是覆盖包新增的 → 删掉就是还原
                }
            }
            else if (sibs.Count == 0 && bak is not null && File.Exists(bak))
            {
                File.Delete(bak);       // 删的是关着的那条，且这个文件上再没别的账 → 备份也没人用了
            }
            if (!string.IsNullOrEmpty(rec.StoreId))
                try { Directory.Delete(OverlayStoreDir(rec.StoreId), recursive: true); } catch { }
            c.Overlays.Remove(key);
            if (save) cfg.Save(c);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return LocService.Tr("文件正被占用，还原失败");
        }
    }

    /// <summary>整包开关：一个汉化包可能盖了上百个文件（LewdDew 那种重打包 146 个），
    /// 卡片上是一行，所以按「宿主 + 来源槽位」成组操作，只在最后落一次盘。</summary>
    public string? SetOverlayGroupEnabled(string gamePath, string host, string slot, bool enabled, ConfigService cfg)
    {
        var c = cfg.Current;
        var keys = GroupKeys(c, host, slot);
        string? err = null;
        foreach (var k in keys)
        {
            err = SetOverlayEnabled(gamePath, k, enabled, cfg, save: false);
            if (err is not null) break;
        }
        if (keys.Count > 0) cfg.Save(c);
        return err;
    }

    /// <summary>整包删除（还原 + 销账 + 删副本）。逐条走 RevertOverlay，最后统一落盘一次。</summary>
    public string? RevertOverlayGroup(string gamePath, string host, string slot, ConfigService cfg)
    {
        var c = cfg.Current;
        var keys = GroupKeys(c, host, slot);
        string? err = null;
        foreach (var k in keys)
        {
            err = RevertOverlay(gamePath, k, cfg, save: false);
            if (err is not null) break;
        }
        if (keys.Count > 0) cfg.Save(c);
        return err;
    }

    /// <summary>某个包在某个宿主名下的全部记录键（按路径排序，保证逐条操作顺序稳定）。</summary>
    private static List<string> GroupKeys(JuniGridConfig c, string host, string slot)
        => c.Overlays
            .Where(kv => NormalizeOverlayHost(kv.Value.Host).Equals(NormalizeOverlayHost(host), StringComparison.OrdinalIgnoreCase)
                && OverlaySlotOf(kv.Key).Equals(slot, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
