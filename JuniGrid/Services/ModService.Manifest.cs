using System.IO;
using System.Text.Json;

namespace JuniGrid.Services;

public sealed partial class ModService
{
    // ------------------------------------------------------------------
    // v1.1.2：备注同步进 manifest.json —— 游戏内 GMCM 的 mod 标题读的就是
    // manifest 的 Name 字段，把它改成备注名，游戏里就显示中文名。
    // 原名先存进配置 ModOriginalNames，取消备注时还原。只动 Name，UniqueID 等
    // 其它字段一律不碰（更新检查、依赖关系全靠 UniqueID，不受影响）。
    // ------------------------------------------------------------------

    /// <summary>把备注写入（或从）指定 mod 的 manifest.json。remark 为空 = 还原原名。
    /// 返回 null 表示成功，否则为错误描述。skipAliveCheck：批量同步时由调用方在循环外
    /// 统一判过一次存活（逐个判 = 每个 mod 两次全系统进程枚举）。</summary>
    public string? ApplyRemarkToManifest(string gamePath, string folder, string remark,
        Dictionary<string, string> originalNames, bool skipAliveCheck = false)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gamePath) || string.IsNullOrWhiteSpace(folder))
                return LocService.Tr("路径无效");
            var manifestPath = Path.Combine(gamePath, "Mods", folder.Replace('/', '\\'), "manifest.json");
            if (!File.Exists(manifestPath)) return LocService.Tr("找不到 manifest.json");
            if (!skipAliveCheck && LauncherService.IsGameProcessRunning()) return LocService.Tr("游戏运行中，暂不能修改清单");

            // Newtonsoft 宽松解析（SMAPI manifest 允许尾随逗号/注释），再规整写回
            var text = ReadManifestText(manifestPath);
            var root = Newtonsoft.Json.Linq.JObject.Parse(text);
            var currentName = root["Name"]?.Type == Newtonsoft.Json.Linq.JTokenType.String
                ? (string?)root["Name"] : null;
            if (string.IsNullOrEmpty(currentName)) return LocService.Tr("清单缺少 Name 字段");

            if (string.IsNullOrWhiteSpace(remark))
            {
                // 还原原名
                if (!originalNames.TryGetValue(folder, out var original)) return null;   // 没改过，无需还原
                root["Name"] = original;
                originalNames.Remove(folder);
            }
            else
            {
                // 存档原名（只在首次时记，mod 更新重置清单后仍以上次存档为准）
                if (!originalNames.TryGetValue(folder, out var original))
                    originalNames[folder] = currentName!;
                // v1.1.4：把备注真正写进 Name —— 之前只存档了原名、没赋值，
                // 写回的是原内容，manifest 永远不变（GMCM 一直显示英文名的根因）
                root["Name"] = remark;
            }

            WriteManifestAtomic(manifestPath, root.ToString(Newtonsoft.Json.Formatting.Indented));
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>扫描后批量对账：所有备注过的 mod，manifest 里的 Name 与备注不一致就补写
    /// （覆盖 mod 更新后清单被重置的情况）。静默执行，失败只记日志。</summary>
    public void SyncAllRemarks(string gamePath, ConfigService cfg)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gamePath) || LauncherService.IsGameProcessRunning()) return;
            var changed = false;
            foreach (var kv in cfg.Current.ModRemarks)
            {
                var folder = kv.Key;
                if (folder.Contains("/.")) continue;   // 跳过禁用的 mod
                var manifestPath = Path.Combine(gamePath, "Mods", folder.Replace('/', '\\'), "manifest.json");
                if (!File.Exists(manifestPath)) continue;
                try
                {
                    var root = Newtonsoft.Json.Linq.JObject.Parse(ReadManifestText(manifestPath));
                    var name = root["Name"]?.Type == Newtonsoft.Json.Linq.JTokenType.String
                        ? (string?)root["Name"] : null;
                    if (name == kv.Value)
                    {
                        // 自愈：manifest 已是备注名、但原名记录丢了（旧版本只在内存记原名、
                        // 退出前没回写配置），列表括号里的原名因此消失、取消备注也无从还原。
                        // 真名已不可考，用文件夹名兜底补记（Automate 这类文件夹=原名的能完全复原）。
                        if (!cfg.Current.ModOriginalNames.ContainsKey(folder))
                        {
                            cfg.Current.ModOriginalNames[folder] = folder.Split('/').Last();
                            changed = true;
                        }
                        continue;   // 已同步
                    }
                    var err = ApplyRemarkToManifest(gamePath, folder, kv.Value, cfg.Current.ModOriginalNames,
                        skipAliveCheck: true);   // v1.1.6：存活已在循环外判过，别每个 mod 都枚举一遍进程
                    if (err is not null) AppLog.Warn("Mods", $"[备注同步] {folder}: {err}");
                    else changed = true;   // ApplyRemarkToManifest 改了 ModOriginalNames（内存），一并落盘
                }
                catch (Exception ex) { AppLog.Warn("Mods", $"[备注同步] {folder}: {ex.Message}"); }
            }
            if (changed) cfg.Save(cfg.Current);
        }
        catch { }
    }

    // ------------------------------------------------------------------
    // v1.1.4 回归修复（BUG-2）：manifest 读写的并发与原子性。
    // Scan 后台的 ReadAllText（FileShare.Read）握住文件时，备注写入的
    // WriteAllText 申请写访问会被立即拒绝 → IOException（E10 复现根因）；
    // 且 WriteAllText 原地清空覆盖，写一半进程被杀 = manifest 损坏（E11）。
    // 修法三保险：读侧宽松共享锁 + 写侧临时文件原子替换 + 短重试。
    // ------------------------------------------------------------------

    /// <summary>宽松共享锁读 manifest：允许读的同时被重命名/替换，Scan 不再撞备注写入。</summary>
    private static string ReadManifestText(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, System.Text.Encoding.UTF8);
        return sr.ReadToEnd();
    }

    /// <summary>原子写 manifest：先写临时文件再 File.Replace 式替换（带 4 次短重试），
    /// 任何时刻磁盘上的 manifest 要么是旧的要么是新的，绝无半截。</summary>
    private static void WriteManifestAtomic(string path, string content)
    {
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, content, new System.Text.UTF8Encoding(false));
            for (var i = 0; i < 4; i++)
            {
                try
                {
                    File.Move(tempPath, path, overwrite: true);
                    return;
                }
                // v1.1.5：IOException 与 UnauthorizedAccessException 都重试 —— 杀软对
                // manifest 内存映射的瞬间 Move 会抛 UAE（"拒绝访问"），属瞬时态；
                // 真只读目标重试 4 次后照样上抛报错，E9 语义不变（多耗约 300ms）。
                catch (Exception ex) when (i < 3 && ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(50 * (i + 1));
                }
            }
        }
        finally
        {
            // 替换失败时清理临时文件，不留垃圾
            if (File.Exists(tempPath))
                try { File.Delete(tempPath); } catch { }
        }
    }

    /// <summary>剥掉 SMAPI manifest 允许的注释（// 行注释 + /* */ 块注释，如 East Scarp
    /// 的自动生成 manifest）与尾随逗号，让 System.Text.Json 的严格解析不炸。
    /// SMAPI 走 Newtonsoft 默认容忍注释能加载，这里必须对齐同样的宽容度，
    /// 否则会把好 mod 误报成"manifest.json 已损坏"。
    /// v1.3.2：internal —— 裸图转换器与包解析都要用这份清洗；CP 生态的非严格 JSON
    /// content.json 同样会遇到 CP 生态的非严格 JSON（East Scarp 尾逗号+注释组合
    /// 让 Newtonsoft 都炸），整套 CP 包解析共用这一份清洗。
    /// v1.3.3：改两遍法 —— 单遍流式在「"值", ⏎ //注释 ⏎ }」场景会漏剥尾逗号
    ///（扫空白时被注释行开头的 / 截停），East Scarp 的 NPC 子文件全是这个写法，
    /// 结果 Newtonsoft 拿着带尾逗号的文本照样炸、整个包静默消失。</summary>
    /// <summary>SMAPI 的 manifest/content 方言允许注释与尾逗号。Newtonsoft 默认就容忍，
    /// System.Text.Json 要显式开这两项 —— 不必再自己扫一遍字符串。</summary>
    internal static readonly System.Text.Json.JsonDocumentOptions ManifestJson = new()
    {
        CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // ------------------------------------------------------------------
    // Install / update from zips
    // ------------------------------------------------------------------
    /// <summary>
    /// Installs a downloaded mod UPDATE zip, replacing the existing folder.
    /// The target keeps its old name (so a ".Disabled" prefix survives).
    /// </summary>
    public string? InstallUpdate(string gamePath, string targetFolderName, string zipPath,
        out string? newVersion, string? expectedUniqueId = null)
    {
        newVersion = null;
        string? temp = null;
        try
        {
            var manifest = ExtractToTemp(zipPath, "mod-update-", out temp);
            if (manifest is null) return LocService.Tr("压缩包里没找到 manifest.json");
            var modRoot = ResolveModRoot(temp, out var allManifests);

            // 安全锁：多 Mod 共用一个 GitHub 仓库时，latest release 可能是别的 Mod。
            // 校验 UniqueID 不符就放弃，绝不能覆盖错装。
            // v1.1.4：多分包捆绑包 —— 任一子包的 UID 匹配即视为正确更新包
            //（Downtown Zuzu 这类 7 子包同发布，期望 UID 只是其中一个）。
            if (expectedUniqueId is not null)
            {
                var found = false;
                foreach (var mf in allManifests)
                {
                    try
                    {
                        // v1.3.1：校验必须用宽松选项 —— 带块注释的 manifest（East Scarp
                        // 等）裸 JsonDocument.Parse 会炸并被 catch 跳过，导致
                        // UID 明明匹配却误报"下载的包不是这个 Mod"。
                        using var check = JsonDocument.Parse(ReadManifestText(mf), ManifestJson);
                        var uid = check.RootElement.TryGetProperty("UniqueID", out var u)
                            ? u.GetString() : null;
                        if (string.Equals(uid, expectedUniqueId, StringComparison.OrdinalIgnoreCase))
                        { found = true; manifest = mf; break; }
                    }
                    catch { }
                }
                if (!found)
                    return LocService.Tr("下载的包不是这个 Mod（发布仓库里含多个 Mod），已放弃安装防止装错");
            }

            // 更新的是<b>子包行</b>（targetFolderName 形如 "顶层/子包"）时，zip 根往往是整个捆绑包。
            // 直接把 modRoot 搬进子包路径，会把整个包塞成孙子目录 —— 子包顶层不再有 manifest.json，
            // SMAPI 认不出这个 mod，实测等于把它弄坏（2026-09-30 用户报"捆绑包被拆散、用不了"）。
            // 所以只搬与被更新子包同名的那一层；作者只发一个独立 mod（zip 里没这层）时退回整包。
            var leafName = Path.GetFileName(targetFolderName.TrimEnd('/', '\\'));
            if (targetFolderName.Contains('/') && leafName.Length > 0)
            {
                var pick = Path.Combine(modRoot, leafName);
                var pickManifest = Path.Combine(pick, "manifest.json");
                if (Directory.Exists(pick) && File.Exists(pickManifest))
                {
                    modRoot = pick;
                    manifest = pickManifest;   // 版本号也要读这一层的，否则报的是别的子包的
                }
            }

            try
            {
                using var doc = JsonDocument.Parse(ReadManifestText(manifest));
                if (doc.RootElement.TryGetProperty("Version", out var v))
                    newVersion = v.GetString();
            }
            catch (Exception __ex) { AppLog.Warn("ModService", __ex.Message); }

            var dest = Path.Combine(gamePath, "Mods", targetFolderName);
            // v1.1.1：旧版不再直接删除 —— 先整体改名移入 .junigrid_trash（同盘原子改名，
            // 绝不出现"删一半"），再装新版；安装失败时原路移回 Mods。任何中断旧版都不丢。
            string? stagedOld = null;
            if (Directory.Exists(dest))
            {
                try { stagedOld = StageExistingToTrash(gamePath, dest); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { return $"「{targetFolderName}」正被占用，无法备份旧版，已放弃更新（旧版未动）"; }
            }
            try
            {
                MoveDirectorySafe(modRoot, dest);   // 跨盘保护
            }
            catch
            {
                // 清掉可能残缺的新版半成品，再把旧版原路移回；移不回去就留在回收站（可手动还原）
                try { if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true); } catch { }
                if (stagedOld is not null) RestoreStaged(stagedOld, dest);
                throw;
            }

            TryDelete(temp);
            if (stagedOld is not null)
                AppLog.Warn("Mods", $"[更新] 旧版已移入回收站保留：{Path.GetFileName(stagedOld)}（可在设置中还原或清理）");
            return null;
        }
        catch (Exception ex)
        {
            if (temp is not null) TryDelete(temp);
            return ex.Message;
        }
    }

    /// <summary>
    /// Installs a BRAND-NEW mod zip into Mods/. Folder name comes from the
    /// zip's inner folder, or the manifest Name when files sit at zip root.
    /// A name collision replaces the old folder (acts as an update).
    /// <para>v1.2.0：requireUniqueId 非空时，zip 内必须存在 UniqueID 与之（忽略大小写）
    /// 一致的 manifest 才允许安装 —— 一键装依赖按名称搜到的候选包以此防"装错 mod"。
    /// 不匹配返回 <see cref="UidMismatchError"/> 哨兵值，调用方换下一个候选。</para>
    /// </summary>
    public const string UidMismatchError = "uniqueid-mismatch";

    /// <summary>
    /// 判断一个"缺失依赖"其实是某个<b>已装 mod 包内的子模块</b>：作者段相同，且去掉作者段后
    /// 以「已装 UID 的名字段 + 分隔符」开头（<c>shurmash.LewdDew_Valley</c> → <c>shurmash.LewdDew_Valley_helper</c>）。
    /// 这类东西在 Nexus 上没有独立页面（实测搜 shurmash 只有 2 个 mod，没有 helper），
    /// 拿它去搜等于白费一次请求，再把人领到一个不存在的页面。命中返回那个已装 UID，否则 null。
    /// 多个候选取最长前缀；末字符必须是分隔符，否则 <c>A.Mod</c> 会误命中 <c>A.ModHelper</c>。
    /// </summary>
    public static string? BundledParentOf(string missingUid, IEnumerable<string> installedUids)
    {
        if (string.IsNullOrWhiteSpace(missingUid)) return null;
        var dot = missingUid.IndexOf('.');
        if (dot <= 0 || dot >= missingUid.Length - 1) return null;
        var author = missingUid[..dot];
        var rest = missingUid[(dot + 1)..];
        string? best = null;
        foreach (var raw in installedUids)
        {
            var uid = (raw ?? "").Trim();
            if (uid.Length == 0 || uid.Equals(missingUid, StringComparison.OrdinalIgnoreCase)) continue;
            var d = uid.IndexOf('.');
            if (d <= 0 || !string.Equals(uid[..d], author, StringComparison.OrdinalIgnoreCase)) continue;
            var head = uid[(d + 1)..];
            if (head.Length == 0 || !rest.StartsWith(head, StringComparison.OrdinalIgnoreCase)) continue;
            if (rest[head.Length] is not ('_' or '-' or '.')) continue;
            if (best is null || uid.Length > best.Length) best = uid;
        }
        return best;
    }
}
